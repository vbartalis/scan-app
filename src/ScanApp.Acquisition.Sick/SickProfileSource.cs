using System.Globalization;
using System.Runtime.CompilerServices;
using ScanApp.Core.Acquisition;
using ScanApp.Core.Model;
using Sick.GenIStream;

namespace ScanApp.Acquisition.Sick;

/// <param name="DeviceSerialOrIp">Pick this device; null picks the first one found.</param>
/// <param name="ParameterFile">A GenIStream parameter file (for example exported from Stream Setup) applied on connect.</param>
public sealed record SickOptions(string? DeviceSerialOrIp = null, string? ParameterFile = null);

/// <summary>
/// A SICK Ruler3000-family scanner through SICK's GenIStream SDK (bundled in lib/sick). One dedicated thread owns the device:
/// it connects, configures free-running calibrated output, grabs frames, and releases the device on dispose.
/// Profiles are only forwarded while <see cref="StreamAsync"/> runs.
/// </summary>
public sealed class SickProfileSource : IProfileSource
{
    // Applied once per network adapter: 3 s took ~12 s on a 4-adapter PC; 500 ms still finds a directly cabled scanner.
    private static readonly TimeSpan DiscoveryTime = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan GrabTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(5);
    private const uint LinesPerFrame = 20;   // ~20 ms per frame at 1 kHz
    private const int BufferedProfiles = 3000; // ~3 s at 1 kHz, ~77 MB at 2560 points, before a slow disk stops recording

    private readonly SickOptions _options;
    private readonly TaskCompletionSource _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ProfileBuffer _buffer = new(BufferedProfiles);
    private Thread? _worker;
    private Dictionary<string, string> _settings = [];
    private volatile bool _disconnected;

    public SickProfileSource(SickOptions options) => _options = options;

    public string Description { get; private set; } = "Ruler3000";
    public int PointsPerProfile { get; private set; }
    public bool HasReflectance { get; private set; }
    public IReadOnlyDictionary<string, string> Settings => _settings;

    public Task ConnectAsync(CancellationToken ct)
    {
        if (_worker is not null)
            throw new InvalidOperationException("Already connected.");
        _worker = new Thread(Run) { IsBackground = true, Name = "Ruler3000 acquisition" };
        _worker.Start();
        return _connected.Task.WaitAsync(ct);
    }

    public async IAsyncEnumerable<Profile> StreamAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await _connected.Task.ConfigureAwait(false);
        await foreach (var profile in _buffer.StreamAsync(ct).ConfigureAwait(false))
            yield return profile;
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_worker is { } worker)
            await Task.Run(() => worker.Join()).ConfigureAwait(false);
        _shutdown.Dispose();
    }

    /// <summary>The acquisition thread: the only code that touches the device.</summary>
    private void Run()
    {
        CameraDiscovery? discovery = null;
        ICamera? camera = null;
        FrameGrabber? grabber = null;
        try
        {
            string cti = Path.Combine(AppContext.BaseDirectory, "SICKGigEVisionTL.cti");
            discovery = CameraDiscovery.CreateFromProducerFile(cti);
            camera = Connect(discovery);
            camera.Disconnected += _ => _disconnected = true;
            Configure(camera);
            grabber = camera.CreateFrameGrabber();
            grabber.Start();
            ProbeLayout(grabber);
            _connected.SetResult();
        }
        catch (Exception e)
        {
            Release(discovery, camera, grabber);
            _connected.TrySetException(e is IOException ? e : new IOException(e.Message, e));
            return;
        }

        try
        {
            GrabLoop(grabber);
        }
        catch (Exception e)
        {
            // Kept by the buffer, so a stream started after the fault still reports it.
            _buffer.Fail(e is IOException ? e : new IOException(e.Message, e));
        }
        finally
        {
            _buffer.Finish();
            Release(discovery, camera, grabber);
        }
    }

    private ICamera Connect(CameraDiscovery discovery)
    {
        var found = discovery.ScanForCameras(DiscoveryTime).ToList();
        string? wanted = _options.DeviceSerialOrIp;
        var match = found.FirstOrDefault(c => wanted is null || c.SerialNumber == wanted || c.IpAddress?.ToString() == wanted)
            ?? throw new IOException(wanted is null ? "No scanner found on the network." : $"No scanner {wanted} found on the network.");
        string name = $"{match.Model} SN {match.SerialNumber}";
        if (match.Status != DiscoveredCamera.AccessStatus.READ_WRITE)
            throw new IOException(match.Status == DiscoveredCamera.AccessStatus.BUSY
                ? $"The scanner ({name}) is in use by another application."
                : $"The scanner ({name}) cannot be opened (access status {match.Status}).");
        Description = name;
        return discovery.ConnectTo(match);
    }

    private void Configure(ICamera camera)
    {
        if (_options.ParameterFile is { } file)
        {
            var result = camera.ImportParametersFromFile(file);
            if (result.Status is not (ConfigurationStatus.OK or ConfigurationStatus.WARNING))
                throw new IOException($"The parameter file {file} was rejected: {result.Status} {result.Message}");
        }

        var p = camera.GetCameraParameters();
        p.DeviceScanType.Set(DeviceScanType.LINESCAN_3D);
        p.Scan3dExtraction(Scan3dExtractionId.SCAN_3D_EXTRACTION_1).Scan3dOutputMode.Set(Scan3dOutputMode.CALIBRATED_AC);
        p.AcquisitionMode.Set(AcquisitionMode.CONTINUOUS);
        p.Trigger(TriggerId.LINE_START).TriggerMode.Set(TriggerMode.OFF);
        p.Trigger(TriggerId.FRAME_START).TriggerMode.Set(TriggerMode.OFF);

        // Profiles reach the PC a whole frame at a time, so lines per frame / line rate is the live-view latency.
        // Boot settings vary (50 and 1024 seen); always use small frames.
        var frameHeight = p.Region(RegionId.SCAN_3D_EXTRACTION_1).Height;
        uint increment = Math.Max(1, frameHeight.GetIncrement());
        frameHeight.Set(Math.Clamp(LinesPerFrame / increment * increment, frameHeight.GetMin(), frameHeight.GetMax()));

        var region = p.Region(RegionId.REGION_1);
        _settings = new Dictionary<string, string>
        {
            ["DeviceModelName"] = p.DeviceModelName.Get(),
            ["DeviceSerialNumber"] = p.DeviceSerialNumber.Get(),
            ["DeviceFirmwareVersion"] = p.DeviceFirmwareVersion.Get().Trim(),
            ["AcquisitionLineRate"] = p.AcquisitionLineRate.Get().ToString(CultureInfo.InvariantCulture),
            ["ExposureTime"] = region.ExposureTime.Get().ToString(CultureInfo.InvariantCulture),
            ["LinesPerFrame"] = p.Region(RegionId.SCAN_3D_EXTRACTION_1).Height.Get().ToString(CultureInfo.InvariantCulture),
            ["Scan3dOutputMode"] = Scan3dOutputMode.CALIBRATED_AC.ToString(),
            ["ParameterFile"] = _options.ParameterFile ?? "",
        };
    }

    /// <summary>Learns the profile layout from the first frame, which is then dropped.</summary>
    private void ProbeLayout(FrameGrabber grabber)
    {
        var result = grabber.Grab(FirstFrameTimeout);
        if (!result.HasCompleted())
            throw new IOException($"The scanner sent no profiles within {FirstFrameTimeout.TotalSeconds:0} s.");
        var frame = result.GetOrThrow();
        try
        {
            var region = frame.GetRegion(RegionId.SCAN_3D_EXTRACTION_1);
            PointsPerProfile = region.GetComponent(ComponentId.RANGE).GetWidth();
            HasReflectance = region.HasComponent(ComponentId.REFLECTANCE);
        }
        finally { frame.Release(); }
    }

    private void GrabLoop(FrameGrabber grabber)
    {
        var token = _shutdown.Token;
        while (!token.IsCancellationRequested)
        {
            var result = grabber.Grab(GrabTimeout);
            if (result.HasTimedOut())
            {
                if (_disconnected)
                    throw new IOException("The scanner disconnected.");
                continue;
            }
            if (result.HasAborted())
            {
                if (token.IsCancellationRequested) return;
                throw new IOException(_disconnected ? "The scanner disconnected." : "The scanner aborted acquisition.");
            }

            var frame = result.GetOrThrow();
            try
            {
                foreach (var profile in Convert(frame))
                    _buffer.Publish(profile);
            }
            finally { frame.Release(); }
        }
    }

    /// <summary>Splits a frame into profiles: X and Z in mm, NaN where the scanner found no laser line.</summary>
    private List<Profile> Convert(IFrame frame)
    {
        var region = frame.GetRegion(RegionId.SCAN_3D_EXTRACTION_1);
        var range = region.GetComponent(ComponentId.RANGE);
        var reflectance = HasReflectance ? region.GetComponent(ComponentId.REFLECTANCE) : null;
        var cs = range.GetCoordinateSystem();
        double xScale = cs.A.Transform.Scale, xOffset = cs.A.Transform.Offset;
        double zScale = cs.C.Transform.Scale, zOffset = cs.C.Transform.Offset;
        ushort missing = (ushort)cs.C.MissingDataValue;
        int width = range.GetWidth();
        if (width != PointsPerProfile)
            throw new IOException($"The scanner changed its profile width from {PointsPerProfile} to {width} points.");

        int lines = frame.GetDeliveredLineCount();
        int configuredLines = frame.GetConfiguredLineCount();
        long firstSequence = ((long)frame.GetFrameId() - 1) * configuredLines;
        var metadata = frame.GetLineMetadata();
        var profiles = new List<Profile>(lines);
        unsafe
        {
            int rangeStride = (int)range.GetStride() / sizeof(ushort);
            ushort* xs = (ushort*)range.GetData(0), zs = (ushort*)range.GetData(1);
            byte* refl = reflectance is null ? null : (byte*)reflectance.GetData();
            int reflStride = reflectance is null ? 0 : (int)reflectance.GetStride();

            for (int line = 0; line < lines; line++)
            {
                var x = new float[width];
                var z = new float[width];
                var r = refl is null ? null : new ushort[width];
                for (int i = 0; i < width; i++)
                {
                    ushort rawZ = zs[line * rangeStride + i];
                    if (rawZ == missing)
                    {
                        x[i] = float.NaN;
                        z[i] = float.NaN;
                    }
                    else
                    {
                        x[i] = (float)(xs[line * rangeStride + i] * xScale + xOffset);
                        z[i] = (float)(rawZ * zScale + zOffset);
                    }
                    if (r is not null) r[i] = refl[line * reflStride + i];
                }
                long timestampNs = line < metadata.Count ? (long)metadata[line].Timestamp : 0;
                profiles.Add(new Profile(firstSequence + line, timestampNs, x, z, r));
            }
        }
        return profiles;
    }

    private static void Release(CameraDiscovery? discovery, ICamera? camera, FrameGrabber? grabber)
    {
        // Runs on the acquisition thread, where an escaping exception would end the process; the device may be gone.
        static void Try(Action release) { try { release(); } catch { /* nothing more can be done for this handle */ } }
        Try(() => grabber?.Stop());
        Try(() => grabber?.Dispose());
        Try(() => { if (camera?.IsConnected == true) camera.Disconnect(); });
        Try(() => (camera as IDisposable)?.Dispose());
        Try(() => discovery?.Dispose());
    }
}
