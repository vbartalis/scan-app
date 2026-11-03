using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScanApp.App.Views;
using ScanApp.Core.Acquisition;
using ScanApp.Core.Cleanup;
using ScanApp.Core.Combine;
using ScanApp.Core.Model;
using ScanApp.Core.Storage;
using ScanApp.Core.Viewing;

namespace ScanApp.App.ViewModels;

public enum SourceKind { Simulated, Ruler3000 }

public enum MainViewMode { HeightMap, ThreeD }

/// <summary>Where the raw view's table fit stands: being fitted, fitted, fitted but no table found, or failed.</summary>
public enum RawFitState { Fitting, Ready, NoTable, Failed }

/// <summary>Combines the given passes of a scan into a saved result; the signature of <see cref="Combiner.CombineAsync"/>.</summary>
public delegate Task<CombineResult> CombineOperation(ScanRepository scan, IReadOnlyList<int> passIds,
                                                     IProgress<(string stage, double fraction)>? progress, CancellationToken ct);

/// <summary>The workbench: one open scan, its passes, and recording a new pass from the selected source.</summary>
public partial class WorkbenchViewModel : ObservableObject
{
    private static readonly TimeSpan LiveViewInterval = TimeSpan.FromMilliseconds(30);

    private readonly Func<SourceKind, IProfileSource> _sourceFactory;
    private readonly LatestValueThrottle<Profile> _liveView;
    private PassRecorder? _recorder;
    private Task? _recordingEnded;
    private CancellationTokenSource? _loadCts;
    private int _loadVersion;
    private double _assumedSpeedMmPerS = 20;
    private readonly Dictionary<int, MotionAnalysis> _analyses = [];
    private readonly HashSet<int> _backwardFlagged = [];
    private PassEdits _edits = PassEdits.None;
    private int _analysisCount;
    private readonly CombineOperation _combine;
    private CancellationTokenSource? _combineCts;
    private CancellationTokenSource? _resultCts;
    private int _resultVersion;
    private readonly Dictionary<int, bool> _hasReflectance = [];
    private CancellationTokenSource? _rawCts;
    private int _rawVersion;
    private (ProfileRange Trim, bool Filter)? _rawKey;
    private PassEdits? _liveEdits;
    private (RawGeometry Geometry, float Min, float Max)? _heightRange;

    /// <param name="dataDirectory">The one folder all scans live in, created on first use.</param>
    /// <param name="combine">The combine operation; <see cref="Combiner.CombineAsync"/> when null. A test seam.</param>
    public WorkbenchViewModel(Func<SourceKind, IProfileSource> sourceFactory, string dataDirectory, CombineOperation? combine = null)
    {
        _sourceFactory = sourceFactory;
        _combine = combine ?? Combiner.CombineAsync;
        DataDirectory = dataDirectory;
        var clock = Stopwatch.StartNew();
        _liveView = new LatestValueThrottle<Profile>(LiveViewInterval, p => LatestProfile = p, () => clock.Elapsed);
        NewScanCommand = new RelayCommand<string>(NewScan, _ => !IsBusy);
        OpenScanCommand = new RelayCommand<string>(OpenScan, _ => !IsBusy);
        RecordCommand = new AsyncRelayCommand(RecordAsync, () => Scan is not null && !IsBusy);
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsRecording);
        DeletePassCommand = new AsyncRelayCommand<PassMetadata>(DeletePassAsync, p => p is not null && Scan is not null && !IsBusy);
        DeleteScanCommand = new AsyncRelayCommand(DeleteScanAsync, () => Scan is not null && !IsBusy);
        DeleteScanByNameCommand = new AsyncRelayCommand<string>(DeleteScanByNameAsync, _ => !IsBusy);
        AutoTrimCommand = new AsyncRelayCommand(AutoTrimAsync, () => CanEditTrim && Analysis is not null && Scan is not null && SelectedPass is not null);
        ClearZoneCommand = new AsyncRelayCommand(ClearZoneAsync, () => AutoTrimCommand.CanExecute(null) && Zone != PassZone.None);
        CombineCommand = new AsyncRelayCommand(CombineAsync, () => Scan is not null && CheckedPasses.Count >= 2 && !IsBusy && !IsCombining);
        CancelCombineCommand = new RelayCommand(() => _combineCts?.Cancel(), () => IsCombining);
        DeleteResultCommand = new AsyncRelayCommand<CombineResult>(DeleteResultAsync, r => r is not null && Scan is not null && !IsBusy);
        CheckedPasses.CollectionChanged += (_, _) => CombineCommand.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecordCommand), nameof(DeletePassCommand), nameof(DeleteScanCommand), nameof(CombineCommand), nameof(DeleteResultCommand))]
    public partial ScanRepository? Scan { get; set; }

    /// <summary>The pass selected in the list, the target of Delete pass.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeletePassCommand))]
    [NotifyPropertyChangedFor(nameof(ShownLimits), nameof(CanSwitchFilter))]
    public partial PassMetadata? SelectedPass { get; set; }

    public ObservableCollection<PassMetadata> Passes { get; } = [];

    /// <summary>Ids of the passes ticked for combining, in tick order; the first is the reference.</summary>
    public ObservableCollection<int> CheckedPasses { get; } = [];

    /// <summary>The combine results of the open scan.</summary>
    public ObservableCollection<CombineResult> Results { get; } = [];

    /// <summary>The result selected in the list; selecting one clears <see cref="SelectedPass"/> and loads its cloud.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownLimits))]
    public partial CombineResult? SelectedResult { get; set; }

    /// <summary>The merged points of <see cref="SelectedResult"/>; null while loading or when none is selected.</summary>
    [ObservableProperty]
    public partial MergedCloud? ResultCloud { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingResult { get; set; }

    /// <summary>Completes when the cloud load for the current <see cref="SelectedResult"/> has ended (or at once if there is none).</summary>
    public Task ResultLoaded { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CombineCommand), nameof(CancelCombineCommand), nameof(AutoTrimCommand), nameof(ClearZoneCommand))]
    [NotifyPropertyChangedFor(nameof(CanEditTrim), nameof(CanSwitchFilter))]
    public partial bool IsCombining { get; set; }

    /// <summary>False while a combine runs: a trim edit could change a pass the combine has not read yet.</summary>
    public bool CanEditTrim => !IsCombining;

    /// <summary>The stage the running combine reports ("Aligning", ...); null when idle.</summary>
    [ObservableProperty]
    public partial string? CombineStage { get; set; }

    /// <summary>Progress of the current combine stage, 0 to 1.</summary>
    [ObservableProperty]
    public partial double CombineProgress { get; set; }

    /// <summary>True to colour the combined cloud by source pass.</summary>
    [ObservableProperty]
    public partial bool ColorByPass { get; set; }

    /// <summary>Combines the ticked passes (the first ticked is the reference) into a new result and selects it.</summary>
    public IAsyncRelayCommand CombineCommand { get; }

    public IRelayCommand CancelCombineCommand { get; }

    /// <summary>Permanently deletes the given result of the open scan. The view confirms first.</summary>
    public IAsyncRelayCommand<CombineResult> DeleteResultCommand { get; }

    /// <summary>
    /// One line per pass of a result: <c>pass 4: 25.9 mm/s ±28 % · overlap 0.12 mm</c>, with "(reference)" after the id
    /// for the reference pass, " · table uncertain" after the overlap for a pass whose table fit looks wrong,
    /// " · N reflections removed" after that for a pass with reflectance (" · reflection filter off" instead when it was switched off), and " (deleted)" at the end for a pass no
    /// longer in the scan.
    /// </summary>
    public static string DescribeOutcome(PassOutcome o, bool isReference, bool isDeleted) =>
        $"pass {o.PassId}{(isReference ? " (reference)" : "")}: {o.MeanSpeedMmPerS.ToString("F1", CultureInfo.InvariantCulture)} mm/s" +
        $" ±{(o.SpeedRangeFraction * 100).ToString("F0", CultureInfo.InvariantCulture)} %" +
        $" · overlap {o.OverlapMadMm.ToString("F2", CultureInfo.InvariantCulture)} mm" +
        (o.TableUncertain ? " · table uncertain" : "") +
        (o.ReflectionFilterOff ? " · reflection filter off" : "") +
        (o.ReflectionsRemoved is { } removed ? $" · {removed.ToString(CultureInfo.InvariantCulture)} reflections removed" : "") +
        (isDeleted ? " (deleted)" : "");

    /// <summary>The <see cref="DescribeOutcome"/> lines of a result, a pass counting as deleted when the open scan no longer has it.</summary>
    public IReadOnlyList<string> OutcomeLines(CombineResult r)
    {
        var live = (Scan?.Passes ?? []).Select(p => p.Id).ToHashSet();
        return [.. r.Passes.Select(o => DescribeOutcome(o, o.PassId == r.ReferencePassId, !live.Contains(o.PassId)))];
    }

    public const string UncertainText = "Uncertain alignment";
    public const string ScaleUnknownText = "Scale unknown — no pass at a different rotation; travel uses the reference speed estimate only";

    /// <summary>
    /// The text for each flag the result carries: uncertain first, then one line per open cycle
    /// (<see cref="OpenCycleText"/>), then scale unknown; empty when none is set.
    /// </summary>
    public static IReadOnlyList<string> DescribeFlags(CombineResult r) =>
        [.. (r.UncertainAlignment ? [UncertainText] : Array.Empty<string>()),
         .. (r.OpenCycles ?? []).Select(OpenCycleText),
         .. (r.ScaleUnknown ? [ScaleUnknownText] : Array.Empty<string>())];

    /// <summary>"Passes 13, 14 and 16 do not agree: their alignments do not close".</summary>
    public static string OpenCycleText(int[] passIds) =>
        $"Passes {string.Join(", ", passIds[..^1])} and {passIds[^1]} do not agree: their alignments do not close";

    public static string DescribeResult(CombineResult r)
    {
        double overlap = r.Passes.Where(p => p.PassId != r.ReferencePassId).Select(p => p.OverlapMadMm).DefaultIfEmpty(0).Max();
        return $"Result {r.Id} · passes {string.Join(", ", r.Passes.Select(p => p.PassId))}" +
               $" · overlap {overlap.ToString("F2", CultureInfo.InvariantCulture)} mm" +
               (r.UncertainAlignment ? " · uncertain" : "") +
               (r.ScaleUnknown ? " · scale unknown" : "");
    }

    partial void OnSelectedResultChanged(CombineResult? value)
    {
        if (value is not null) SelectedPass = null;
        StartResultLoad(value);
    }

    private void StartResultLoad(CombineResult? result)
    {
        _resultCts?.Cancel();
        _resultCts = null;
        int version = ++_resultVersion;
        ResultCloud = null;
        IsLoadingResult = false;
        ResultLoaded = Task.CompletedTask;
        if (result is null || Scan is not { } scan) return;

        var cts = _resultCts = new CancellationTokenSource();
        IsLoadingResult = true;
        ResultLoaded = LoadResultAsync(scan, result, version, cts.Token);
    }

    /// <summary>Reads the cloud off the UI thread; only the load whose version is still current may set <see cref="ResultCloud"/>. Never throws.</summary>
    private async Task LoadResultAsync(ScanRepository scan, CombineResult result, int version, CancellationToken ct)
    {
        try
        {
            var cloud = await Task.Run(() => scan.OpenResultCloud(result.Id), ct);
            if (version == _resultVersion) ResultCloud = cloud;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            if (version == _resultVersion) StatusMessage = $"Could not load result {result.Id}: {e.Message}";
        }
        finally
        {
            if (version == _resultVersion) IsLoadingResult = false;
        }
    }

    private async Task CombineAsync()
    {
        if (Scan is not { } scan || CheckedPasses.Count < 2) return;
        var ids = CheckedPasses.ToList();
        StatusMessage = null;
        IsBusy = true;
        IsCombining = true;
        CombineStage = null;
        CombineProgress = 0;
        var cts = _combineCts = new CancellationTokenSource();
        var progress = new Progress<(string stage, double fraction)>(p =>
        {
            if (ReferenceEquals(_combineCts, cts))
            {
                CombineStage = p.stage;
                CombineProgress = p.fraction;
            }
        });
        try
        {
            var result = await _combine(scan, ids, progress, cts.Token);
            SyncResults(scan);
            SelectedResult = Results.FirstOrDefault(r => r.Id == result.Id) ?? result;
            StatusMessage = $"Combined into result {result.Id}.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Combine cancelled.";
        }
        catch (Exception e)
        {
            StatusMessage = $"Combine failed: {e.Message}";
        }
        finally
        {
            _combineCts = null;
            cts.Dispose();
            CombineStage = null;
            CombineProgress = 0;
            IsCombining = false;
            IsBusy = false;
        }
    }

    private async Task DeleteResultAsync(CombineResult? result)
    {
        if (result is null || Scan is not { } scan) return;
        bool wasSelected = SelectedResult?.Id == result.Id;
        if (wasSelected)
        {
            _resultCts?.Cancel();
            await ResultLoaded;
        }
        try
        {
            scan.DeleteResult(result.Id);
            SyncResults(scan);
            // The operator may have selected another result while the load was released.
            if (wasSelected && SelectedResult?.Id == result.Id) SelectedResult = null;
            StatusMessage = $"Deleted result {result.Id}.";
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Could not delete result {result.Id}: {e.Message}";
            SyncResults(scan);
            if (wasSelected && SelectedResult?.Id == result.Id)
            {
                if (Results.Any(r => r.Id == result.Id)) StartResultLoad(SelectedResult);
                else SelectedResult = null;
            }
        }
    }

    private void SyncResults(ScanRepository scan)
    {
        var ids = scan.Results.Select(r => r.Id).ToHashSet();
        for (int i = Results.Count - 1; i >= 0; i--)
            if (!ids.Contains(Results[i].Id)) Results.RemoveAt(i);
        var have = Results.Select(r => r.Id).ToHashSet();
        foreach (var result in scan.Results)
            if (!have.Contains(result.Id)) Results.Add(result);
    }

    private void ClearCombineState()
    {
        SelectedResult = null;
        Results.Clear();
        CheckedPasses.Clear();
    }

    /// <summary>The preview of <see cref="SelectedPass"/>; null while loading or when nothing is selected.</summary>
    [ObservableProperty]
    public partial PassPreview? Preview { get; set; }

    /// <summary>The motion analysis of <see cref="SelectedPass"/>; null until it has run.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AutoTrimCommand), nameof(ClearZoneCommand))]
    [NotifyPropertyChangedFor(nameof(ShownLimits), nameof(CanSwitchFilter))]
    public partial MotionAnalysis? Analysis { get; set; }

    /// <summary>The trim in use, as raw profile indices: the saved edit if there is one, otherwise the automatic trim.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownLimits))]
    public partial ProfileRange? Trim { get; set; }

    /// <summary>True to show the raw pass instead of the cleaned one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownLimits))]
    public partial bool ShowRaw { get; set; }

    /// <summary>True when no trim edit is saved for the selected pass.</summary>
    public bool TrimIsAuto => _edits.TrimStart is null && _edits.TrimEnd is null;

    /// <summary>The selected pass's saved zone; <see cref="PassZone.None"/> when it has none or nothing is selected.</summary>
    public PassZone Zone => _edits.Zone;

    /// <summary>True when the noise floor was capped, so the automatic trim deserves a look.</summary>
    public bool TrimNeedsCheck => Analysis?.NoiseFloorCapped == true;

    /// <summary>How many motion analyses have run (not counting cache hits). A test seam.</summary>
    internal int AnalysisCount => Volatile.Read(ref _analysisCount);

    [ObservableProperty]
    public partial bool IsLoadingPreview { get; set; }

    /// <summary>Progress of the running preview load, 0 to 1.</summary>
    [ObservableProperty]
    public partial double PreviewProgress { get; set; }

    [ObservableProperty]
    public partial MainViewMode ViewMode { get; set; } = MainViewMode.HeightMap;

    [ObservableProperty]
    public partial bool FlipHeight { get; set; }

    /// <summary>What is under the pointer in the height map; shown beside the status line.</summary>
    [ObservableProperty]
    public partial string HoverReadout { get; set; } = "";

    /// <summary>
    /// The travel speed assumed for the pass views (the scanner has no encoder). A value of zero or less, or one that is not
    /// finite, is rejected: the previous value stays and the status line says why. Raises a change either way so a bound box re-shows it.
    /// </summary>
    public double AssumedSpeedMmPerS
    {
        get => _assumedSpeedMmPerS;
        set
        {
            if (!double.IsFinite(value) || value <= 0)
            {
                StatusMessage = double.IsFinite(value)
                    ? "Speed must be greater than 0 mm/s."
                    : "Speed must be a finite number of mm/s.";
                OnPropertyChanged();
                return;
            }
            SetProperty(ref _assumedSpeedMmPerS, value);
        }
    }

    /// <summary>Completes when the preview load for the current <see cref="SelectedPass"/> has ended (or at once if there is none).</summary>
    public Task PreviewLoaded { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    public partial PassDirection SelectedDirection { get; set; } = PassDirection.CameraFront;

    [ObservableProperty]
    public partial SourceKind SelectedSource { get; set; } = SourceKind.Simulated;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    public partial bool IsRecording { get; set; }

    /// <summary>True from pressing Record (including connecting) until the recording has fully ended.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RecordCommand), nameof(NewScanCommand), nameof(OpenScanCommand),
                                nameof(DeletePassCommand), nameof(DeleteScanCommand), nameof(DeleteScanByNameCommand),
                                nameof(CombineCommand), nameof(DeleteResultCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>Errors and notices for the user.</summary>
    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    /// <summary>The newest profile, throttled for the live view.</summary>
    [ObservableProperty]
    public partial Profile? LatestProfile { get; set; }

    public string DataDirectory { get; }

    /// <summary>Creates a scan of the given name in <see cref="DataDirectory"/>.</summary>
    public IRelayCommand<string> NewScanCommand { get; }

    /// <summary>Permanently deletes the given pass of the open scan. The view confirms first.</summary>
    public IAsyncRelayCommand<PassMetadata> DeletePassCommand { get; }

    /// <summary>Permanently deletes the open scan and closes it. The view confirms first.</summary>
    public IAsyncRelayCommand DeleteScanCommand { get; }

    /// <summary>
    /// Permanently deletes the named scan in <see cref="DataDirectory"/> without opening it, so unreadable scans
    /// can go too. Closes it if it is the open one. The view confirms first.
    /// </summary>
    public IAsyncRelayCommand<string> DeleteScanByNameCommand { get; }

    /// <summary>Opens the scan of the given name from <see cref="DataDirectory"/>.</summary>
    public IRelayCommand<string> OpenScanCommand { get; }
    public IAsyncRelayCommand RecordCommand { get; }
    public IAsyncRelayCommand StopCommand { get; }

    /// <summary>Called by the view's UI timer to push the newest profile to <see cref="LatestProfile"/>.</summary>
    public void TickLiveView() => _liveView.Tick();

    public static string DescribePass(PassMetadata pass) => DescribePass(pass, false);

    public static string DescribePass(PassMetadata pass, bool backwardFlagged) =>
        $"Pass {pass.Id} · {pass.ProfileCount} lines · {DescribeDirection(pass.Direction)}" +
        (pass.Status == PassStatus.Interrupted ? " · interrupted" : "") +
        (backwardFlagged ? " · backward?" : "");

    /// <summary>True when an analysis done this session found a backward run inside the pass's trim.</summary>
    public bool IsBackwardFlagged(int passId) => _backwardFlagged.Contains(passId);

    /// <summary>Resets the selected pass to its automatic trim, keeping its zone, and rebuilds the preview.</summary>
    public IAsyncRelayCommand AutoTrimCommand { get; }

    /// <summary>Removes the selected pass's zone, keeping its trims, and rebuilds the preview.</summary>
    public IAsyncRelayCommand ClearZoneCommand { get; }

    public const string InvalidZoneMessage = "Zone limits must be left below right and lower below upper.";
    private const string ZoneWhileCombiningMessage = "Zone cannot be edited while combining.";

    private Task AutoTrimAsync()
    {
        if (IsCombining || Scan is not { } scan || SelectedPass is not { } pass || Analysis is null) return Task.CompletedTask;
        ApplyEdits(scan, pass, _edits.WithoutTrim());
        return Task.CompletedTask;
    }

    private Task ClearZoneAsync()
    {
        if (IsCombining || Scan is not { } scan || SelectedPass is not { } pass || Analysis is null) return Task.CompletedTask;
        ApplyEdits(scan, pass, _edits.WithZone(PassZone.None));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Saves the zone for the selected pass beside its trims and rebuilds the preview. Returns false, with a message and
    /// the old zone kept, while combining or when a limit is not finite or a pair of limits is crossed or equal.
    /// </summary>
    public bool SetZone(PassZone zone)
    {
        if (IsCombining)
        {
            StatusMessage = ZoneWhileCombiningMessage;
            return false;
        }
        if (Analysis is null || Scan is not { } scan || SelectedPass is not { } pass || !IsFiniteAndValid(zone))
        {
            StatusMessage = InvalidZoneMessage;
            return false;
        }
        return ApplyEdits(scan, pass, _edits.WithZone(zone));
    }

    private static bool IsFiniteAndValid(PassZone zone) =>
        zone.IsValid && new[] { zone.XMin, zone.XMax, zone.HeightMin, zone.HeightMax }.All(v => v is null || double.IsFinite(v.Value));

    /// <summary>
    /// Saves a manual trim for the selected pass and rebuilds the preview, without re-running the analysis.
    /// Indices are raw profile indices, 0 up to the pass's last profile. Returns false, with a message and the old trim kept, if invalid.
    /// </summary>
    public bool SetTrim(int start, int end) => SaveTrim(_edits with { TrimStart = start, TrimEnd = end });

    /// <summary>Saves edits whose trims (null = automatic) must resolve to 0 ≤ start &lt; end ≤ the last profile; null edits are invalid.</summary>
    private bool SaveTrim(PassEdits? edits)
    {
        if (IsCombining)
        {
            StatusMessage = "Trim cannot be edited while combining.";
            return false;
        }
        if (Analysis is not { } analysis || Scan is not { } scan || SelectedPass is not { } pass
            || edits is null || !TrimFits(edits, analysis))
        {
            StatusMessage = InvalidTrimMessage;
            return false;
        }
        return ApplyEdits(scan, pass, edits);
    }

    private static bool TrimFits(PassEdits edits, MotionAnalysis analysis)
    {
        int start = edits.TrimStart ?? analysis.AutoTrim.Start, end = edits.TrimEnd ?? analysis.AutoTrim.End;
        return start >= 0 && start < end && end <= analysis.Signal.Length - 1;
    }

    /// <summary>
    /// Saves the edits, then shows them. The raw preview depends on none of them, so in the raw view it stays: only
    /// <see cref="Trim"/>, <see cref="Zone"/> and <see cref="ShownLimits"/> change at once, and
    /// <see cref="RawGeometry"/> is refreshed in the background when the table or mask depends on what changed. The
    /// cleaned view rebuilds.
    /// </summary>
    private bool ApplyEdits(ScanRepository scan, PassMetadata pass, PassEdits edits)
    {
        if (!TrySaveEdits(scan, pass, edits)) return false;
        if (ShowRaw && Analysis is { } analysis)
        {
            SetEdits(edits);
            Trim = PassCleanup.From(analysis, edits, pass.Direction == PassDirection.CameraBack).Trim;
            StartRawRefresh();
        }
        else
            StartPreviewLoad(pass, rebuild: true);
        return true;
    }

    private bool TrySaveEdits(ScanRepository scan, PassMetadata pass, PassEdits edits)
    {
        try
        {
            scan.SaveEdits(pass.Id, edits);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Could not save the trim: {e.Message}";
            return false;
        }
    }

    partial void OnShowRawChanged(bool value)
    {
        if (SelectedPass is { } pass && Analysis is not null) StartPreviewLoad(pass, rebuild: true);
    }

    partial void OnAnalysisChanged(MotionAnalysis? value) => OnPropertyChanged(nameof(TrimNeedsCheck));

    /// <summary>
    /// Per raw pass, the heights above the table and the flags the raw views test the limits against; refreshed in the
    /// background (<see cref="RawGeometryLoaded"/>). Null outside the raw view of a pass and until the first refresh ends.
    /// A newer refresh replaces it; until then the last one stays, so the band uses the table of the last fit.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeightLimitsReady))]
    public partial RawGeometry? RawGeometry { get; private set; }

    /// <summary>The table <see cref="RawGeometry"/> was built from; null until fitted (or when the fit found none).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeightLimitsReady))]
    public partial TablePlane? RawTable { get; private set; }

    /// <summary>
    /// Completes when every raw refresh started so far has ended, the current one and any cancelled or superseded one
    /// still running (at once if none is), so awaiting it also releases their file handles.
    /// </summary>
    public Task RawGeometryLoaded { get; private set; } = Task.CompletedTask;

    /// <summary>True once the raw view's table is fitted, so the height limits can be shown against it.</summary>
    public bool HeightLimitsReady => RawGeometry is not null && RawTable is not null;

    /// <summary>
    /// The raw table fit: <see cref="RawFitState.Fitting"/> from the raw view's entry (and again for a refresh after no
    /// table or a failure) until a refresh ends; then <see cref="RawFitState.Ready"/>, <see cref="RawFitState.NoTable"/>
    /// when the fit found none, or <see cref="RawFitState.Failed"/>. A refresh after a ready fit keeps Ready, as the last
    /// table stays in use until the new one replaces it. A failure is retried by the next edit.
    /// </summary>
    [ObservableProperty]
    public partial RawFitState RawFit { get; private set; }

    /// <summary>A view option: the raw views mark the points the reflection filter removes (or would, when it is off).</summary>
    [ObservableProperty]
    public partial bool ShowRemovedReflections { get; set; }

    /// <summary>True for a selected pass with reflectance once its analysis is ready, while no combine runs.</summary>
    public bool CanSwitchFilter =>
        SelectedPass is { } pass && _hasReflectance.GetValueOrDefault(pass.Id) && Analysis is not null && !IsCombining;

    /// <summary>Whether the selected pass's saved edits apply the reflection filter (on unless switched off).</summary>
    public bool FilterReflections => _edits.FiltersReflections;

    /// <summary>
    /// Switches the selected pass's reflection filter and saves it (on is saved as null, as older files read). Returns
    /// false while combining (with a message) or when <see cref="CanSwitchFilter"/> is false. Refreshes like a limit.
    /// </summary>
    public bool SetFilterReflections(bool on)
    {
        if (IsCombining)
        {
            StatusMessage = "The reflection filter cannot be switched while combining.";
            return false;
        }
        if (!CanSwitchFilter || Scan is not { } scan || SelectedPass is not { } pass) return false;
        return ApplyEdits(scan, pass, _edits with { ReflectionFilter = on ? null : false });
    }

    /// <summary>
    /// The limits the raw views show: the live drag (<see cref="PreviewLimit"/>) if one is in progress, else the saved
    /// edits. Null outside the raw view of a pass (no pass, a result selected, the cleaned view, no analysis or trim yet).
    /// </summary>
    public CutLimits? ShownLimits =>
        ShowRaw && SelectedResult is null && SelectedPass is { } pass && Analysis is { } analysis && Trim is not null
            ? LimitsOf(analysis, pass, _liveEdits ?? _edits)
            : null;

    private static CutLimits LimitsOf(MotionAnalysis analysis, PassMetadata pass, PassEdits edits) =>
        CutLimits.From(PassCleanup.From(analysis, edits, pass.Direction == PassDirection.CameraBack).Trim,
                       edits.Zone, edits.FiltersReflections);

    /// <summary>
    /// The slider range of a limit: the trims over the pass's raw times, X over the raw preview's X range, the height over
    /// min..max of <see cref="RawGeometry.PointHeight"/>. Null while it is not available.
    /// </summary>
    public LimitScale? ScaleOf(LimitKind kind)
    {
        switch (kind)
        {
            case LimitKind.TrimStart or LimitKind.TrimEnd:
                return Analysis is { TimeS: { Length: >= 2 } t } ? new LimitScale(kind, t[0], t[^1]) : null;
            case LimitKind.XMin or LimitKind.XMax:
                return ShowRaw && Preview is { IsEmpty: false, Grid: var g } && g.XMax > g.XMin
                    ? new LimitScale(kind, g.XMin, g.XMax) : null;
            default:
                if (!HeightLimitsReady) return null;
                var (min, max) = HeightRange(RawGeometry!);
                return max > min ? new LimitScale(kind, min, max) : null;
        }
    }

    /// <summary>The finite min..max of a geometry's point heights, scanned once per geometry (the panel asks on every drag tick).</summary>
    private (float Min, float Max) HeightRange(RawGeometry geometry)
    {
        if (_heightRange is { } cached && ReferenceEquals(cached.Geometry, geometry)) return (cached.Min, cached.Max);
        float min = float.PositiveInfinity, max = float.NegativeInfinity;
        foreach (float h in geometry.PointHeight)
        {
            if (!float.IsFinite(h)) continue;
            min = Math.Min(min, h);
            max = Math.Max(max, h);
        }
        _heightRange = (geometry, min, max);
        return (min, max);
    }

    /// <summary>
    /// A limit as shown (the live drag included): the trims in seconds from the first raw profile, the zone in mm.
    /// Null is automatic (trims) or no limit (zone).
    /// </summary>
    public double? LimitValue(LimitKind kind)
    {
        var edits = _liveEdits ?? _edits;
        double? Seconds(int? index) =>
            index is { } i && Analysis is { } a && i >= 0 && i < a.TimeS.Length ? a.TimeS[i] : null;
        return kind switch
        {
            LimitKind.TrimStart => Seconds(edits.TrimStart),
            LimitKind.TrimEnd => Seconds(edits.TrimEnd),
            LimitKind.XMin => edits.XMin,
            LimitKind.XMax => edits.XMax,
            LimitKind.HeightMin => edits.HeightMin,
            _ => edits.HeightMax,
        };
    }

    /// <summary>
    /// Where a wheel notch on a limit starts: <see cref="LimitValue"/>, except that an automatic trim starts from the
    /// automatic trim's time (the line the raw views show), so a notch moves it by one step, not from the pass's end.
    /// </summary>
    public double? NudgeOrigin(LimitKind kind)
    {
        if (LimitValue(kind) is { } value) return value;
        if (kind is not (LimitKind.TrimStart or LimitKind.TrimEnd) || ShownLimits is not { } shown || Analysis is not { } a)
            return null;
        int index = kind == LimitKind.TrimStart ? shown.TrimStart : shown.TrimEnd;
        return index >= 0 && index < a.TimeS.Length ? a.TimeS[index] : null;
    }

    /// <summary>
    /// Shows a limit live, unsaved (a slider or line being dragged): sets <see cref="ShownLimits"/>. Ignored outside the
    /// raw view of a pass, while combining, or for a value that is not finite.
    /// </summary>
    public void PreviewLimit(LimitKind kind, double? value)
    {
        if (IsCombining || ShownLimits is null || Analysis is not { } analysis) return;
        if (value is { } v && !double.IsFinite(v)) return;
        _liveEdits = WithLimit(_liveEdits ?? _edits, kind, value, analysis.TimeS);
        OnPropertyChanged(nameof(ShownLimits));
    }

    /// <summary>
    /// Saves a limit (the drag released, or a box committed) and clears the live preview. A trim in seconds goes to the
    /// nearest raw profile (null = automatic, that edge only) and is checked as <see cref="SetTrim"/> checks it; a zone
    /// limit (null = no limit) goes through <see cref="SetZone"/>. False, with their message, when rejected.
    /// </summary>
    public bool CommitLimit(LimitKind kind, double? value)
    {
        bool hadLive = _liveEdits is not null;
        _liveEdits = null;
        bool saved;
        if (kind is LimitKind.TrimStart or LimitKind.TrimEnd)
        {
            bool finite = value is not { } v || double.IsFinite(v);
            saved = SaveTrim(finite && Analysis is { } analysis ? WithLimit(_edits, kind, value, analysis.TimeS) : null);
        }
        else
            saved = SetZone(WithLimit(_edits, kind, value, []).Zone);
        if (hadLive && !saved) OnPropertyChanged(nameof(ShownLimits));
        return saved;
    }

    /// <summary><paramref name="edits"/> with one limit replaced; a trim in seconds becomes the nearest raw profile of <paramref name="timeS"/>.</summary>
    private static PassEdits WithLimit(PassEdits edits, LimitKind kind, double? value, double[] timeS)
    {
        int? Profile() => value is { } s ? NearestIndex(timeS, s) : null;
        return kind switch
        {
            LimitKind.TrimStart => edits with { TrimStart = Profile() },
            LimitKind.TrimEnd => edits with { TrimEnd = Profile() },
            LimitKind.XMin => edits with { XMin = value },
            LimitKind.XMax => edits with { XMax = value },
            LimitKind.HeightMin => edits with { HeightMin = value },
            _ => edits with { HeightMax = value },
        };
    }

    /// <summary>The index of the entry of the ascending <paramref name="timeS"/> nearest to <paramref name="seconds"/>.</summary>
    public static int NearestIndex(double[] timeS, double seconds)
    {
        int i = Array.BinarySearch(timeS, seconds);
        if (i >= 0) return i;
        i = ~i;
        if (i == 0) return 0;
        if (i == timeS.Length) return timeS.Length - 1;
        return seconds - timeS[i - 1] <= timeS[i] - seconds ? i - 1 : i;
    }

    /// <summary>Drops the raw geometry and cancels its refresh: the raw view is left or a new preview loads.</summary>
    private void ResetRaw()
    {
        _rawCts?.Cancel();
        _rawCts = null;
        _rawVersion++;
        _rawKey = null;
        _liveEdits = null;
        RawGeometry = null;
        RawTable = null;
        RawFit = RawFitState.Fitting;
        OnPropertyChanged(nameof(ShownLimits));
    }

    /// <summary>
    /// In the raw view of a shown pass, starts <see cref="RefreshRawGeometryAsync"/> for the saved edits, unless the last
    /// refresh was for the same trim and filter (the mask and table depend on nothing else, so a zone change needs none).
    /// </summary>
    private void StartRawRefresh()
    {
        if (ShowRaw && Scan is { } scan && SelectedPass is { } pass && Analysis is { TimeS.Length: > 0 } analysis
            && Preview is { } preview)
        {
            var cleanup = PassCleanup.From(analysis, _edits, pass.Direction == PassDirection.CameraBack);
            bool filter = _edits.FiltersReflections;
            if (_rawKey != (cleanup.Trim, filter))
            {
                _rawKey = (cleanup.Trim, filter);
                if (RawFit != RawFitState.Ready) RawFit = RawFitState.Fitting;
                _rawCts?.Cancel();
                int version = ++_rawVersion;
                var cts = _rawCts = new CancellationTokenSource();
                var refresh = RefreshRawGeometryAsync(scan, pass, preview, analysis, cleanup, filter, version, cts.Token);
                RawGeometryLoaded = RawGeometryLoaded.IsCompleted ? refresh : Task.WhenAll(RawGeometryLoaded, refresh);
            }
        }
    }

    /// <summary>
    /// Off the UI thread: the reflection mask (always, so the raw view can mark what the filter would remove), the table
    /// fitted inside the trims on the masked pass when the filter is on and on the unmasked pass when it is off, the
    /// mask's cells, then <see cref="RawGeometry.Build"/>. Only the refresh whose version is still current is applied.
    /// Never throws; a cancelled refresh leaves no message.
    /// </summary>
    private async Task RefreshRawGeometryAsync(ScanRepository scan, PassMetadata pass, PassPreview preview, MotionAnalysis analysis,
                                               PassCleanup cleanup, bool filter, int version, CancellationToken ct)
    {
        try
        {
            var (geometry, table) = await Task.Run(() =>
            {
                using var reader = scan.OpenPass(pass.Id);
                AtRawSeam(ct);
                var mask = ReflectionFilter.Find(reader, cleanup, null, ct);
                var fitted = AlignmentMapBuilder.Build(reader, filter ? cleanup with { Reflections = mask } : cleanup, null, ct).Table;
                if (RawTableSeam is { } seam) fitted = seam(fitted);
                bool[]? cells = mask.Count > 0 ? ReflectionCells.Find(reader, preview.Grid, mask) : null;
                ct.ThrowIfCancellationRequested();
                var built = ScanApp.Core.Viewing.RawGeometry.Build(preview, analysis, cleanup with { Table = fitted, Reflections = mask }, cells);
                return (built, fitted);
            }, ct);
            if (version != _rawVersion) return;
            RawTable = table;
            RawGeometry = geometry;
            RawFit = table is null ? RawFitState.NoTable : RawFitState.Ready;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            if (version != _rawVersion) return;
            _rawKey = null;   // so the next edit refreshes again, even for the same trim and filter
            RawFit = RawFitState.Failed;
            StatusMessage = $"Could not load pass {pass.Id}: {e.Message}";
        }
    }

    private void AtRawSeam(CancellationToken ct)
    {
        RawRefreshOpened?.TrySetResult();
        RawRefreshGate?.Wait(ct);
    }

    /// <summary>Test seam: when set, a raw refresh replaces its fitted table (or none) with what this returns.</summary>
    internal Func<TablePlane?, TablePlane?>? RawTableSeam { get; set; }

    /// <summary>Test seam: when set, a raw refresh waits on it (after opening the pass) before computing.</summary>
    internal Task? RawRefreshGate { get; set; }

    /// <summary>Test seam: completed once a raw refresh has opened its pass file, just before it waits on <see cref="RawRefreshGate"/>.</summary>
    internal TaskCompletionSource? RawRefreshOpened { get; set; }

    public static string DescribeDirection(PassDirection direction) => direction switch
    {
        PassDirection.CameraFront => "camera front",
        PassDirection.CameraBack => "camera back",
        _ => direction.ToString(),
    };

    /// <summary>Names of the scans in <see cref="DataDirectory"/>, sorted.</summary>
    public IReadOnlyList<string> ListScans() =>
        Directory.Exists(DataDirectory)
            ? Directory.EnumerateDirectories(DataDirectory)
                .Where(d => File.Exists(Path.Combine(d, "scan.json")))
                .Select(Path.GetFileName)
                .OfType<string>()
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

    partial void OnSelectedPassChanged(PassMetadata? value)
    {
        if (value is not null) SelectedResult = null;
        StartPreviewLoad(value);
    }

    /// <param name="rebuild">True to rebuild the preview of the pass already shown, keeping its analysis and trim on screen.</param>
    private void StartPreviewLoad(PassMetadata? pass, bool rebuild = false)
    {
        _loadCts?.Cancel();
        _loadCts = null;
        if (StatusMessage is not null && IsPreviewMessage(StatusMessage)) StatusMessage = null;
        int version = ++_loadVersion;
        ResetRaw();
        Preview = null;
        PreviewProgress = 0;
        IsLoadingPreview = false;
        PreviewLoaded = Task.CompletedTask;
        if (!rebuild)
        {
            Analysis = null;
            Trim = null;
            SetEdits(PassEdits.None);
        }
        if (pass is null || Scan is not { } scan) return;

        var cts = _loadCts = new CancellationTokenSource();
        IsLoadingPreview = true;
        PreviewLoaded = LoadPreviewAsync(scan, pass, version, cts.Token);
    }

    private static bool IsPreviewMessage(string message) =>
        message is EmptyPreviewMessage or NoMovementMessage or InvalidTrimMessage or InvalidZoneMessage
        || message.StartsWith("Could not load pass ", StringComparison.Ordinal)
        || message.StartsWith("Ignoring unreadable edits for pass ", StringComparison.Ordinal)
        || message.StartsWith("Could not save the trim: ", StringComparison.Ordinal);

    private const string EmptyPreviewMessage = "No valid points in this pass.";
    private const string NoMovementMessage = "No movement detected — showing the whole pass.";
    private const string InvalidTrimMessage = "Trim start must be before trim end, inside the pass.";

    private void SetEdits(PassEdits edits)
    {
        bool zoneChanged = edits.Zone != _edits.Zone;
        _edits = edits;
        _liveEdits = null;
        OnPropertyChanged(nameof(TrimIsAuto));
        OnPropertyChanged(nameof(FilterReflections));
        OnPropertyChanged(nameof(ShownLimits));
        if (!zoneChanged) return;
        OnPropertyChanged(nameof(Zone));
        ClearZoneCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The saved edits, or none (with a message) when the file is unreadable, its trim does not fit the pass or its zone is invalid.</summary>
    private PassEdits ReadEdits(ScanRepository scan, int passId, MotionAnalysis analysis)
    {
        try
        {
            var edits = scan.LoadEdits(passId);
            int start = edits.TrimStart ?? analysis.AutoTrim.Start, end = edits.TrimEnd ?? analysis.AutoTrim.End;
            if (edits != PassEdits.None && (start < 0 || start >= end || end > analysis.Signal.Length - 1))
                throw new InvalidDataException("the saved trim does not fit the pass");
            if (!IsFiniteAndValid(edits.Zone))
                throw new InvalidDataException("the saved zone is invalid");
            return edits;
        }
        catch (Exception e)
        {
            StatusMessage = $"Ignoring unreadable edits for pass {passId}: {e.Message}";
            return PassEdits.None;
        }
    }

    /// <summary>
    /// Builds the preview off the UI thread. Resumes on the UI thread (no ConfigureAwait(false)) to set bound
    /// properties, and only the load whose version is still current may touch them.
    /// </summary>
    private async Task LoadPreviewAsync(ScanRepository scan, PassMetadata pass, int version, CancellationToken ct)
    {
        var analysisProgress = new Progress<double>(p =>
        {
            if (version == _loadVersion) PreviewProgress = p / 2;
        });
        var buildProgress = new Progress<double>(p =>
        {
            if (version == _loadVersion) PreviewProgress = 0.5 + p / 2;
        });
        try
        {
            if (!_analyses.TryGetValue(pass.Id, out var analysis))
            {
                (analysis, bool reflectance) = await Task.Run(() =>
                {
                    using var reader = scan.OpenPass(pass.Id);
                    AtLoadSeam(ct);
                    Interlocked.Increment(ref _analysisCount);
                    return (MotionAnalyzer.Analyze(reader, new MotionOptions(), analysisProgress, ct), reader.HasReflectance);
                }, ct);
                if (version != _loadVersion) return;
                _analyses[pass.Id] = analysis;
                _hasReflectance[pass.Id] = reflectance;
            }

            var edits = ReadEdits(scan, pass.Id, analysis);
            var cleanup = PassCleanup.From(analysis, edits, pass.Direction == PassDirection.CameraBack);
            SetEdits(edits);
            Analysis = analysis;
            Trim = analysis.TimeS.Length == 0 ? null : cleanup.Trim;
            if (analysis.BackwardRuns.Any(r => r.End >= cleanup.Trim.Start && r.Start <= cleanup.Trim.End))
                _backwardFlagged.Add(pass.Id);
            else
                _backwardFlagged.Remove(pass.Id);
            OnPropertyChanged(nameof(IsBackwardFlagged));

            // The raw view fits its table in the raw refresh, after the preview is shown.
            bool raw = ShowRaw;
            bool filtered = false;
            if (!raw && cleanup.Zone.HasBand && analysis.TimeS.Length > 0)
            {
                // The band is measured from the pass's table, fitted as the combine fits it: on the masked pass.
                var unmasked = cleanup;
                cleanup = await Task.Run(() =>
                {
                    using var reader = scan.OpenPass(pass.Id);
                    AtLoadSeam(ct);
                    var masked = edits.FiltersReflections
                        ? unmasked with { Reflections = ReflectionFilter.Find(reader, unmasked, null, ct) }
                        : unmasked;
                    return masked with { Table = AlignmentMapBuilder.Build(reader, masked, null, ct).Table };
                }, ct);
                if (version != _loadVersion) return;
                filtered = true;
            }

            // The cleaned view drops the reflections, as the combine does; the raw view shows everything.
            var ready = cleanup;
            var preview = await Task.Run(() =>
            {
                using var reader = scan.OpenPass(pass.Id);
                AtLoadSeam(ct);
                var shown = raw ? null
                    : filtered ? ready
                    : !edits.FiltersReflections ? ready
                    : ready with { Reflections = ReflectionFilter.Find(reader, ready, null, ct) };
                return PassPreviewBuilder.Build(reader, new PreviewOptions(), shown, buildProgress, ct);
            }, ct);
            if (version != _loadVersion) return;
            Preview = preview;
            if (preview.IsEmpty) StatusMessage = EmptyPreviewMessage;
            else if (!analysis.HasMovement) StatusMessage = NoMovementMessage;
            if (raw) StartRawRefresh();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            if (version == _loadVersion) StatusMessage = $"Could not load pass {pass.Id}: {e.Message}";
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoadingPreview = false;
                PreviewProgress = 1;
            }
        }
    }

    private void NewScan(string? name)
    {
        if (name is null) return;
        try
        {
            Show(ScanRepository.Create(DataDirectory, name));
            StatusMessage = $"Created scan \"{name}\".";
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Could not create the scan: {e.Message}";
        }
    }

    private void OpenScan(string? name)
    {
        if (name is null) return;
        try
        {
            var scan = ScanRepository.Open(Path.Combine(DataDirectory, name));
            Show(scan);
            StatusMessage = $"Opened scan \"{scan.Info.Name}\" ({scan.Passes.Count} passes).";
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Could not open the scan: {e.Message}";
        }
    }

    private void AtLoadSeam(CancellationToken ct)
    {
        PreviewLoadOpened?.TrySetResult();
        PreviewLoadGate?.Wait(ct);
    }

    /// <summary>Test seam: when set, a preview load waits on it (after opening the pass) before building.</summary>
    internal Task? PreviewLoadGate { get; set; }

    /// <summary>Test seam: completed once a preview load has opened its pass file, just before it waits on <see cref="PreviewLoadGate"/>.</summary>
    internal TaskCompletionSource? PreviewLoadOpened { get; set; }

    /// <summary>
    /// Cancels the running preview load and raw refresh and waits for them to end, so their file handles are released
    /// before a delete. <see cref="LoadPreviewAsync"/> and <see cref="RefreshRawGeometryAsync"/> never throw.
    /// </summary>
    private async Task ReleasePreviewLoadAsync()
    {
        _loadCts?.Cancel();
        _rawCts?.Cancel();
        await PreviewLoaded;
        await RawGeometryLoaded;
    }

    /// <summary>After a failed delete: reloads the selected pass if it still exists, otherwise clears the selection.</summary>
    private void RestoreSelection(ScanRepository scan)
    {
        SyncPasses(scan);
        if (SelectedPass is { } selected)
        {
            if (Passes.Contains(selected)) StartPreviewLoad(selected);
            else SelectedPass = null;
        }
    }

    private void SyncPasses(ScanRepository scan)
    {
        var ids = scan.Passes.Select(p => p.Id).ToHashSet();
        for (int i = Passes.Count - 1; i >= 0; i--)
            if (!ids.Contains(Passes[i].Id)) Passes.RemoveAt(i);
        var have = Passes.Select(p => p.Id).ToHashSet();
        foreach (var pass in scan.Passes)
            if (!have.Contains(pass.Id)) Passes.Add(pass);
    }

    private async Task DeletePassAsync(PassMetadata? pass)
    {
        if (pass is null || Scan is not { } scan) return;
        bool wasSelected = SelectedPass?.Id == pass.Id;
        if (wasSelected) await ReleasePreviewLoadAsync();
        try
        {
            scan.DeletePass(pass.Id);
            _analyses.Remove(pass.Id);
            _hasReflectance.Remove(pass.Id);
            _backwardFlagged.Remove(pass.Id);
            SyncPasses(scan);
            CheckedPasses.Remove(pass.Id);
            if (wasSelected) SelectedPass = null;
            StatusMessage = $"Deleted pass {pass.Id}.";
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Could not delete pass {pass.Id}: {e.Message}";
            RestoreSelection(scan);
        }
    }

    private async Task DeleteScanAsync()
    {
        if (Scan is not { } scan) return;
        await ReleasePreviewLoadAsync();
        try
        {
            scan.DeleteScan();
            ClearAnalyses();
            SelectedPass = null;
            ClearCombineState();
            Scan = null;
            Passes.Clear();
            LatestProfile = null;
            StatusMessage = $"Deleted scan \"{scan.Info.Name}\".";
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Could not delete the scan: {e.Message}";
            RestoreSelection(scan);
        }
    }

    private async Task DeleteScanByNameAsync(string? name)
    {
        if (name is null) return;
        string directory = Path.Combine(DataDirectory, name);
        var open = Scan is { } current && Path.GetFullPath(current.Directory) == Path.GetFullPath(directory) ? current : null;
        if (open is not null) await ReleasePreviewLoadAsync();
        try
        {
            ScanRepository.Delete(directory);
            if (open is not null)
            {
                ClearAnalyses();
                SelectedPass = null;
                ClearCombineState();
                Scan = null;
                Passes.Clear();
                LatestProfile = null;
            }
            StatusMessage = $"Deleted scan \"{name}\".";
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"Could not delete scan \"{name}\": {e.Message}";
            if (open is not null) RestoreSelection(open);
        }
    }

    private void ClearAnalyses()
    {
        _analyses.Clear();
        _hasReflectance.Clear();
        _backwardFlagged.Clear();
    }

    private void Show(ScanRepository scan)
    {
        SelectedPass = null;
        ClearCombineState();
        ClearAnalyses();
        Scan = scan;
        Passes.Clear();
        foreach (var pass in scan.Passes) Passes.Add(pass);
        foreach (var result in scan.Results) Results.Add(result);
        LatestProfile = null;
    }

    private async Task RecordAsync()
    {
        if (Scan is not { } scan) return;
        StatusMessage = null;
        IsBusy = true;

        IProfileSource? source = null;
        PassRecorder recorder;
        try
        {
            source = _sourceFactory(SelectedSource);
            await source.ConnectAsync(CancellationToken.None);
            recorder = new PassRecorder(scan, source);
            recorder.ProfileReceived += _liveView.Post;
            recorder.Start(SelectedDirection);
        }
        catch (Exception e)
        {
            StatusMessage = $"Could not start recording: {e.Message}";
            if (source is not null)
            {
                try { await source.DisposeAsync(); }
                catch (Exception disposeError) { StatusMessage += $" Releasing the scanner also failed: {disposeError.Message}"; }
            }
            IsBusy = false;
            return;
        }

        _recorder = recorder;
        IsRecording = true;
        _recordingEnded = OnRecordingEndedAsync(recorder, source);
    }

    private async Task StopAsync()
    {
        if (_recorder is null || _recordingEnded is null) return;
        await _recorder.StopAsync();
        await _recordingEnded;
    }

    /// <summary>
    /// Cancels a running combine and waits for it to end, waits for any connect in progress, then stops and saves a
    /// running recording. Used when the window closes.
    /// </summary>
    public async Task ShutdownAsync()
    {
        _combineCts?.Cancel();
        if (CombineCommand.ExecutionTask is { } combining)
            await combining;
        if (RecordCommand.ExecutionTask is { } starting)
            await starting;
        await StopAsync();
    }

    /// <summary>
    /// Runs once per recording, whether it ended by Stop or by a source fault. Must resume on the UI thread
    /// (no ConfigureAwait(false)): it updates <see cref="Passes"/> and bound properties.
    /// </summary>
    private async Task OnRecordingEndedAsync(PassRecorder recorder, IProfileSource source)
    {
        try
        {
            var result = await recorder.Completion;
            if (result.Pass is { } pass) Passes.Add(pass);
            StatusMessage = result switch
            {
                { Error: { } e, Pass: not null } => $"Recording stopped: {e.Message} — partial pass saved as interrupted.",
                { Error: { } e } => $"Recording stopped: {e.Message} — nothing saved.",
                { Pass: { } p } => $"Pass {p.Id} saved: {p.ProfileCount} lines.",
                _ => "No profiles received — nothing saved.",
            };
        }
        catch (Exception e)
        {
            StatusMessage = $"Recording ended with an error: {e.Message}";
        }
        finally
        {
            recorder.ProfileReceived -= _liveView.Post;
            try { await source.DisposeAsync(); }
            catch (Exception e) { StatusMessage += $" Releasing the scanner failed: {e.Message}"; }
            _recorder = null;
            IsRecording = false;
            IsBusy = false;
        }
    }
}
