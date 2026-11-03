using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using ScanApp.Core.Acquisition;
using ScanApp.Core.Model;

namespace ScanApp.Core.Simulation;

/// <summary>
/// A blob of reflection spikes centred on (<paramref name="X"/>, <paramref name="Y"/>) in the height function's frame:
/// some of the points within <paramref name="RadiusMm"/> stand <paramref name="HeightMm"/> above the surface and are dim.
/// </summary>
public sealed record SpikeBlob(double X, double Y, double RadiusMm, double HeightMm);

public sealed record SimulationOptions(
    Func<double, double, double> HeightAt,   // z(x, y) in mm; NaN = no return
    double LineWidthMm = 200, int PointsPerProfile = 2560,
    double ProfileRateHz = 1000,
    Func<double, double>? SpeedMmPerSAt = null, // speed vs. time since start; default 20 mm/s
    double DeadTimeStartS = 1.0,             // scanner still before moving
    double NoiseMm = 0.02, int Seed = 1,
    bool WithReflectance = false,            // surface 216, spike points 31, no return 0
    IReadOnlyList<SpikeBlob>? Spikes = null);

/// <summary>
/// A fake scanner pushed along a rail over a height function, paced in real time. Timestamps are simulated time.
/// </summary>
public sealed class SimulatedProfileSource : IProfileSource
{
    private const double DefaultSpeedMmPerS = 20;
    private const ushort SurfaceReflectance = 216, SpikeReflectance = 31;
    /// <summary>The share of a blob's points that are spikes; the rest stay on the surface, as in the recordings.</summary>
    private const double SpikeShare = 0.25;
    private readonly SimulationOptions _options;

    public SimulatedProfileSource(SimulationOptions options)
    {
        Validate(options);
        _options = options;
        Settings = new Dictionary<string, string>
        {
            ["LineWidthMm"] = options.LineWidthMm.ToString(CultureInfo.InvariantCulture),
            ["PointsPerProfile"] = options.PointsPerProfile.ToString(CultureInfo.InvariantCulture),
            ["ProfileRateHz"] = options.ProfileRateHz.ToString(CultureInfo.InvariantCulture),
            ["DeadTimeStartS"] = options.DeadTimeStartS.ToString(CultureInfo.InvariantCulture),
            ["NoiseMm"] = options.NoiseMm.ToString(CultureInfo.InvariantCulture),
            ["Seed"] = options.Seed.ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// The first <paramref name="count"/> profiles a source with these options emits, generated at once with no
    /// real-time pacing. Identical to what <see cref="StreamAsync"/> yields.
    /// </summary>
    public static IEnumerable<Profile> Generate(SimulationOptions options, int count)
    {
        Validate(options);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        return Sequence(options).Take(count);
    }

    private static void Validate(SimulationOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.PointsPerProfile, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ProfileRateHz);
    }

    /// <summary>A flat plate 5 mm high, 160 × 150 mm, with a 20 mm grid of 2 mm high bumps.</summary>
    public static SimulationOptions DemoPlate() => new((x, y) =>
    {
        if (Math.Abs(x) > 80 || y < 10 || y > 160) return 0;
        double dx = x - Math.Round(x / 20) * 20, dy = y - Math.Round(y / 20) * 20;
        double r2 = dx * dx + dy * dy;
        return r2 < 16 ? 5 + 2 * Math.Sqrt(1 - r2 / 16) : 5;
    });

    /// <summary>
    /// A board 10 mm up, wider than the line (x −115..115) and spanning y 20..190, so a 0..200 mm pass sees the table
    /// on about 15 % of its cells. Four Ø 6 mm through-holes show the table. On top: a 30 × 20 × 8 mm block at
    /// (−40, 70), a 10 × 40 × 4 mm block at (35, 120) and a cylinder r 8 mm, 12 mm high, at (10, 150).
    /// </summary>
    public static SimulationOptions RaisedBoard() => new((x, y) =>
    {
        const double board = 10;
        if (Math.Abs(x) > 115 || y < 20 || y > 190) return 0;
        foreach (var (hx, hy) in (ReadOnlySpan<(double, double)>)[(-80, 40), (80, 40), (-80, 170), (80, 170)])
            if ((x - hx) * (x - hx) + (y - hy) * (y - hy) < 9) return 0;
        if (Math.Abs(x + 40) <= 15 && Math.Abs(y - 70) <= 10) return board + 8;
        if (Math.Abs(x - 35) <= 5 && Math.Abs(y - 120) <= 20) return board + 4;
        if ((x - 10) * (x - 10) + (y - 150) * (y - 150) <= 64) return board + 12;
        return board;
    });

    public string Description => "Simulated";
    public int PointsPerProfile => _options.PointsPerProfile;
    public bool HasReflectance => _options.WithReflectance;
    public IReadOnlyDictionary<string, string> Settings { get; }

    public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Emits profiles at <see cref="SimulationOptions.ProfileRateHz"/> on average. OS timers are far coarser than 1 ms,
    /// so each tick emits every profile that is due by the wall clock, not exactly one.
    /// </summary>
    public async IAsyncEnumerable<Profile> StreamAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var profiles = Sequence(_options).GetEnumerator();
        long sequence = 0;

        var clock = Stopwatch.StartNew();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1));
        while (true)
        {
            long due = (long)(clock.Elapsed.TotalSeconds * _options.ProfileRateHz) + 1;
            for (; sequence < due; sequence++)
            {
                ct.ThrowIfCancellationRequested();
                profiles.MoveNext();
                yield return profiles.Current;
            }
            await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>The endless profile sequence. Position y integrates the speed once the dead time has passed.</summary>
    private static IEnumerable<Profile> Sequence(SimulationOptions o)
    {
        var random = new Random(o.Seed);
        // Its own stream, drawn only inside blobs, so the noise of a pass without spikes is unchanged.
        var spikeRandom = new Random(unchecked(o.Seed * 7919 + 17));
        var speedAt = o.SpeedMmPerSAt ?? (_ => DefaultSpeedMmPerS);
        double dt = 1 / o.ProfileRateHz;
        double y = 0;
        for (long sequence = 0; ; sequence++)
        {
            double t = sequence * dt;
            if (t > o.DeadTimeStartS)
                y += speedAt(t) * dt;
            yield return MakeProfile(o, sequence, t, y, random, spikeRandom);
        }
    }

    private static Profile MakeProfile(SimulationOptions o, long sequence, double t, double y, Random random, Random spikeRandom)
    {
        var xs = new float[o.PointsPerProfile];
        var zs = new float[o.PointsPerProfile];
        var reflectance = o.WithReflectance ? new ushort[o.PointsPerProfile] : null;
        double step = o.LineWidthMm / (o.PointsPerProfile - 1);
        for (int i = 0; i < o.PointsPerProfile; i++)
        {
            double x = -o.LineWidthMm / 2 + i * step;
            double z = o.HeightAt(x, y);
            if (double.IsNaN(z))
            {
                xs[i] = float.NaN;
                zs[i] = float.NaN;
                continue;
            }
            bool spike = false;
            if (o.Spikes is { } blobs)
                foreach (var b in blobs)
                {
                    if ((x - b.X) * (x - b.X) + (y - b.Y) * (y - b.Y) > b.RadiusMm * b.RadiusMm) continue;
                    if (spikeRandom.NextDouble() < SpikeShare)
                    {
                        z += b.HeightMm;
                        spike = true;
                    }
                    break;
                }
            xs[i] = (float)x;
            zs[i] = (float)(z + (o.NoiseMm > 0 ? o.NoiseMm * Gaussian(random) : 0));
            if (reflectance is not null) reflectance[i] = spike ? SpikeReflectance : SurfaceReflectance;
        }
        return new Profile(sequence, (long)Math.Round(t * 1e9), xs, zs, reflectance);
    }

    private static double Gaussian(Random random)
    {
        double u1 = 1 - random.NextDouble(), u2 = random.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
