using System.Runtime.CompilerServices;
using ScanApp.App.ViewModels;
using ScanApp.App.Views;
using ScanApp.Core.Acquisition;
using ScanApp.Core.Cleanup;
using ScanApp.Core.Combine;
using ScanApp.Core.Model;
using ScanApp.Core.Simulation;
using ScanApp.Core.Storage;
using ScanApp.Core.Viewing;

namespace ScanApp.App.Tests;

public sealed class WorkbenchViewModelTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"workbench-{Guid.NewGuid():N}");
    private string DataFolder => Path.Combine(_root, "data");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private WorkbenchViewModel Create(Func<IProfileSource> source) => new(_ => source(), DataFolder);

    private WorkbenchViewModel CreateWithScan(Func<IProfileSource> source)
    {
        var vm = Create(source);
        vm.NewScanCommand.Execute("scan");
        Assert.NotNull(vm.Scan);
        return vm;
    }

    private static IProfileSource FastSimulation() =>
        new SimulatedProfileSource(new SimulationOptions((_, _) => 1, PointsPerProfile: 64, ProfileRateHz: 10_000));

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    [Fact]
    public void NewScan_CreatesScanInDataFolder()
    {
        var vm = Create(FastSimulation);

        vm.NewScanCommand.Execute("remote");

        Assert.Equal(Path.Combine(DataFolder, "remote"), vm.Scan?.Directory);
        Assert.True(File.Exists(Path.Combine(DataFolder, "remote", "scan.json")));
    }

    [Fact]
    public void ListScans_ReturnsScanNamesInDataFolder_IgnoringOtherFolders()
    {
        var vm = Create(FastSimulation);
        Assert.Empty(vm.ListScans());

        vm.NewScanCommand.Execute("b-scan");
        vm.NewScanCommand.Execute("a-scan");
        Directory.CreateDirectory(Path.Combine(DataFolder, "not-a-scan"));

        Assert.Equal(["a-scan", "b-scan"], vm.ListScans());
    }

    [Fact]
    public async Task Record_ThenStop_AddsPassToList()
    {
        var vm = CreateWithScan(FastSimulation);

        await vm.RecordCommand.ExecuteAsync(null);
        Assert.True(vm.IsRecording);
        await Task.Delay(50);
        await vm.StopCommand.ExecuteAsync(null);

        Assert.False(vm.IsRecording);
        var pass = Assert.Single(vm.Passes);
        Assert.Equal(PassStatus.Complete, pass.Status);
        Assert.True(pass.ProfileCount > 0);
    }

    [Fact]
    public void Record_DisabledWithoutOpenScan()
    {
        var vm = Create(FastSimulation);

        Assert.Null(vm.Scan);
        Assert.False(vm.RecordCommand.CanExecute(null));
        Assert.False(vm.StopCommand.CanExecute(null));
    }

    [Fact]
    public async Task Stop_WithNoProfiles_SetsNothingSavedMessage()
    {
        var vm = CreateWithScan(() => new FakeSource(profilesBeforeFault: null));

        await vm.RecordCommand.ExecuteAsync(null);
        await vm.StopCommand.ExecuteAsync(null);

        Assert.Empty(vm.Passes);
        Assert.Equal("No profiles received — nothing saved.", vm.StatusMessage);
        Assert.False(vm.IsRecording);
    }

    [Fact]
    public async Task ConnectFailure_SetsStatusMessage_AndNotRecording()
    {
        var vm = CreateWithScan(() => new FakeSource(profilesBeforeFault: null, connectError: new IOException("no route to device")));

        await vm.RecordCommand.ExecuteAsync(null);

        Assert.False(vm.IsRecording);
        Assert.Contains("no route to device", vm.StatusMessage);
        Assert.Empty(vm.Passes);
    }

    [Fact]
    public async Task SourceFault_EndsRecording_AndKeepsInterruptedPass()
    {
        var vm = CreateWithScan(() => new FakeSource(profilesBeforeFault: 5));

        await vm.RecordCommand.ExecuteAsync(null);
        await WaitUntil(() => !vm.IsRecording);
        await WaitUntil(() => vm.Passes.Count == 1);

        var pass = vm.Passes[0];
        Assert.Equal(PassStatus.Interrupted, pass.Status);
        Assert.Equal(5, pass.ProfileCount);
        Assert.Contains("device went away", vm.StatusMessage);
        Assert.Contains("interrupted", vm.StatusMessage);
        Assert.True(vm.RecordCommand.CanExecute(null));
    }

    [Fact]
    public async Task SelectedDirection_IsStoredOnPass()
    {
        var vm = CreateWithScan(FastSimulation);
        vm.SelectedDirection = PassDirection.CameraBack;

        await vm.RecordCommand.ExecuteAsync(null);
        await Task.Delay(50);
        await vm.StopCommand.ExecuteAsync(null);

        Assert.Equal(PassDirection.CameraBack, Assert.Single(vm.Passes).Direction);
    }

    [Fact]
    public void OpenScan_ByName_ListsExistingPasses()
    {
        var vm = CreateWithScan(FastSimulation);
        using (var pending = vm.Scan!.BeginPass(PassDirection.CameraFront, 2, false, "test", new Dictionary<string, string>()))
        {
            pending.Writer.Write(new Profile(0, 0, [0f, 1f], [0f, 1f], null));
            pending.Commit(PassStatus.Complete);
        }

        var reopened = Create(FastSimulation);
        reopened.OpenScanCommand.Execute("scan");

        Assert.Equal(Path.Combine(DataFolder, "scan"), reopened.Scan?.Directory);
        Assert.Single(reopened.Passes);
    }

    private static void CommitPass(WorkbenchViewModel vm)
    {
        using var pending = vm.Scan!.BeginPass(PassDirection.CameraFront, 2, false, "test", new Dictionary<string, string>());
        pending.Writer.Write(new Profile(0, 0, [0f, 1f], [0f, 1f], null));
        vm.Passes.Add(pending.Commit(PassStatus.Complete));
    }

    private static PassMetadata CommitPass(WorkbenchViewModel vm, int profiles, bool allNaN = false)
    {
        using var pending = vm.Scan!.BeginPass(PassDirection.CameraFront, 3, false, "test", new Dictionary<string, string>());
        for (int i = 0; i < profiles; i++)
        {
            float nan = float.NaN;
            pending.Writer.Write(allNaN
                ? new Profile(i, i * 1_000_000L, [nan, nan, nan], [nan, nan, nan], null)
                : new Profile(i, i * 1_000_000L, [0f, 1f, 2f], [i, i + 1f, i + 2f], null));
        }
        var pass = pending.Commit(PassStatus.Complete);
        vm.Passes.Add(pass);
        return pass;
    }

    [Fact]
    public async Task SelectPass_LoadsPreview()
    {
        var vm = CreateWithScan(FastSimulation);
        var pass = CommitPass(vm, 10);

        vm.SelectedPass = pass;
        await vm.PreviewLoaded;

        Assert.Equal(10, vm.Preview?.ProfileCount);
        Assert.False(vm.IsLoadingPreview);
    }

    [Fact]
    public async Task SelectPass_Switching_ShowsOnlyTheLatest()
    {
        var vm = CreateWithScan(FastSimulation);
        var first = CommitPass(vm, 5000);
        var second = CommitPass(vm, 7);

        vm.SelectedPass = first;
        vm.SelectedPass = second;
        await vm.PreviewLoaded;
        await Task.Delay(200);

        Assert.Equal(7, vm.Preview?.ProfileCount);
        Assert.False(vm.IsLoadingPreview);
    }

    [Fact]
    public async Task SelectPass_EmptyPreview_SetsMessage()
    {
        var vm = CreateWithScan(FastSimulation);
        var pass = CommitPass(vm, 4, allNaN: true);

        vm.SelectedPass = pass;
        await vm.PreviewLoaded;

        Assert.True(vm.Preview?.IsEmpty);
        Assert.Equal("No valid points in this pass.", vm.StatusMessage);
    }

    [Fact]
    public async Task SelectPass_LoadFailure_SetsMessage()
    {
        var vm = CreateWithScan(FastSimulation);
        var pass = CommitPass(vm, 4);
        File.Delete(Path.Combine(vm.Scan!.Directory, "passes", $"pass-{pass.Id:000}.bin"));

        vm.SelectedPass = pass;
        await vm.PreviewLoaded;

        Assert.Null(vm.Preview);
        Assert.StartsWith($"Could not load pass {pass.Id}: ", vm.StatusMessage);
        Assert.False(vm.IsLoadingPreview);
    }

    [Fact]
    public async Task DeleteSelectedPass_ClearsPreview()
    {
        var vm = CreateWithScan(FastSimulation);
        var pass = CommitPass(vm, 10);
        vm.SelectedPass = pass;
        await vm.PreviewLoaded;
        Assert.NotNull(vm.Preview);

        await vm.DeletePassCommand.ExecuteAsync(pass);
        await vm.PreviewLoaded;

        Assert.Null(vm.SelectedPass);
        Assert.Null(vm.Preview);
    }

    [Fact]
    public async Task DeleteScan_ClearsPreview()
    {
        var vm = CreateWithScan(FastSimulation);
        vm.SelectedPass = CommitPass(vm, 10);
        await vm.PreviewLoaded;

        await vm.DeleteScanCommand.ExecuteAsync(null);

        Assert.Null(vm.Preview);
        Assert.Null(vm.SelectedPass);
    }

    [Fact]
    public async Task PointCloudData_ScalesTravelAndNegatesZOnlyWhenFlipped()
    {
        var vm = CreateWithScan(FastSimulation);
        vm.SelectedPass = CommitPass(vm, 3);
        await vm.PreviewLoaded;
        var preview = vm.Preview!;

        var (xyz, value) = PointCloudData.Build(preview, 20, flip: false);
        var (flipped, flippedValue) = PointCloudData.Build(preview, 20, flip: true);

        Assert.Equal(preview.Points.Count * 3, xyz.Length);
        Assert.Equal(preview.Points.TimeS[^1] * 20, xyz[^2], 3);
        Assert.Equal(preview.Points.Z[^1], xyz[^1]);
        Assert.Equal(-preview.Points.Z[^1], flipped[^1]);
        Assert.Equal(value, flippedValue);
    }

    [Fact]
    public void AssumedSpeed_RejectsNonPositive()
    {
        var vm = Create(FastSimulation);
        Assert.Equal(20, vm.AssumedSpeedMmPerS);

        vm.AssumedSpeedMmPerS = 0;
        Assert.Equal(20, vm.AssumedSpeedMmPerS);
        Assert.Equal("Speed must be greater than 0 mm/s.", vm.StatusMessage);

        vm.StatusMessage = null;
        vm.AssumedSpeedMmPerS = -5;
        Assert.Equal(20, vm.AssumedSpeedMmPerS);
        Assert.Equal("Speed must be greater than 0 mm/s.", vm.StatusMessage);

        vm.AssumedSpeedMmPerS = 35;
        Assert.Equal(35, vm.AssumedSpeedMmPerS);
    }

    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NaN)]
    public void AssumedSpeed_RejectsNonFinite(double value)
    {
        var vm = Create(FastSimulation);

        vm.AssumedSpeedMmPerS = value;

        Assert.Equal(20, vm.AssumedSpeedMmPerS);
        Assert.Equal("Speed must be a finite number of mm/s.", vm.StatusMessage);
    }

    [Fact]
    public async Task SelectPass_AfterEmptyOrFailedPreview_ClearsItsMessage()
    {
        var vm = CreateWithScan(FastSimulation);
        var empty = CommitPass(vm, 4, allNaN: true);
        var good = CommitMoving(vm);

        vm.SelectedPass = empty;
        await vm.PreviewLoaded;
        Assert.Equal("No valid points in this pass.", vm.StatusMessage);

        vm.SelectedPass = good;
        await vm.PreviewLoaded;
        Assert.Null(vm.StatusMessage);
        Assert.False(vm.Preview?.IsEmpty);
    }

    [Fact]
    public async Task SelectPass_KeepsUnrelatedStatusMessage()
    {
        var vm = CreateWithScan(FastSimulation);
        var pass = CommitMoving(vm);
        vm.StatusMessage = "Pass 9 saved: 4 lines.";

        vm.SelectedPass = pass;
        await vm.PreviewLoaded;

        Assert.Equal("Pass 9 saved: 4 lines.", vm.StatusMessage);
    }

    /// <summary>Selects the pass with a gate that never opens, and waits until the load really holds the file.</summary>
    private static async Task SelectAndWaitForOpen(WorkbenchViewModel vm, PassMetadata pass)
    {
        vm.PreviewLoadGate = new TaskCompletionSource().Task;
        var opened = vm.PreviewLoadOpened = new TaskCompletionSource();
        vm.SelectedPass = pass;
        Assert.True(vm.IsLoadingPreview);
        await opened.Task.WaitAsync(Timeout);
    }

    [Fact]
    public async Task DeleteSelectedPass_WhileLoading_WaitsForTheLoadAndSucceeds()
    {
        var vm = CreateWithScan(FastSimulation);
        var pass = CommitPass(vm, 10);
        await SelectAndWaitForOpen(vm, pass);

        await vm.DeletePassCommand.ExecuteAsync(pass);

        Assert.Empty(vm.Passes);
        Assert.Empty(vm.Scan!.Passes);
        Assert.Empty(Directory.GetFiles(Path.Combine(vm.Scan.Directory, "passes"), "pass-*.bin"));
        Assert.Null(vm.SelectedPass);
        Assert.Null(vm.Preview);
        Assert.False(vm.IsLoadingPreview);
    }

    [Fact]
    public async Task DeleteScan_WhileLoading_WaitsForTheLoadAndSucceeds()
    {
        var vm = CreateWithScan(FastSimulation);
        await SelectAndWaitForOpen(vm, CommitPass(vm, 10));
        string directory = vm.Scan!.Directory;

        await vm.DeleteScanCommand.ExecuteAsync(null);

        Assert.Null(vm.Scan);
        Assert.False(Directory.Exists(directory));
        Assert.Null(vm.SelectedPass);
    }

    [Fact]
    public async Task DeleteScanByName_OfTheOpenScanWhileLoading_Succeeds()
    {
        var vm = CreateWithScan(FastSimulation);
        await SelectAndWaitForOpen(vm, CommitPass(vm, 10));
        string directory = vm.Scan!.Directory;

        await vm.DeleteScanByNameCommand.ExecuteAsync("scan");

        Assert.Null(vm.Scan);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task DeletePass_Failure_ResyncsPassesWithTheScan()
    {
        var vm = CreateWithScan(FastSimulation);
        var pass = CommitPass(vm, 10);
        var other = CommitPass(vm, 5);
        string bin = Path.Combine(vm.Scan!.Directory, "passes", $"pass-{pass.Id:000}.bin");
        using var held = new FileStream(bin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);   // blocks the delete

        await vm.DeletePassCommand.ExecuteAsync(pass);

        Assert.StartsWith($"Could not delete pass {pass.Id}", vm.StatusMessage);
        Assert.Equal(vm.Scan.Passes.Select(p => p.Id), vm.Passes.Select(p => p.Id));
        Assert.Contains(other.Id, vm.Passes.Select(p => p.Id));
    }

    [Fact]
    public async Task DeletePass_RemovesItFromTheListAndDisk()
    {
        var vm = CreateWithScan(FastSimulation);
        CommitPass(vm);
        CommitPass(vm);

        Assert.True(vm.DeletePassCommand.CanExecute(vm.Passes[0]));
        await vm.DeletePassCommand.ExecuteAsync(vm.Passes[0]);

        Assert.Equal([2], vm.Passes.Select(p => p.Id));
        Assert.Equal([2], vm.Scan!.Passes.Select(p => p.Id));
    }

    [Fact]
    public async Task DeleteScan_ClosesItAndRemovesItFromTheList()
    {
        var vm = CreateWithScan(FastSimulation);
        CommitPass(vm);

        await vm.DeleteScanCommand.ExecuteAsync(null);

        Assert.Null(vm.Scan);
        Assert.Empty(vm.Passes);
        Assert.Empty(vm.ListScans());
        Assert.False(vm.DeleteScanCommand.CanExecute(null));
    }

    [Fact]
    public async Task DeleteScanByName_RemovesAScanThatCannotBeOpened()
    {
        var vm = Create(FastSimulation);
        vm.NewScanCommand.Execute("old");
        vm.NewScanCommand.Execute("current");
        File.WriteAllText(Path.Combine(DataFolder, "old", "passes", "pass-001.json"), "{ \"Direction\": \"Away\" }");

        await vm.DeleteScanByNameCommand.ExecuteAsync("old");

        Assert.Equal(["current"], vm.ListScans());
        Assert.Equal("current", vm.Scan?.Info.Name);
    }

    [Fact]
    public async Task DeleteScanByName_OfTheOpenScan_ClosesIt()
    {
        var vm = CreateWithScan(FastSimulation);

        await vm.DeleteScanByNameCommand.ExecuteAsync("scan");

        Assert.Null(vm.Scan);
        Assert.Empty(vm.ListScans());
    }

    [Fact]
    public async Task Delete_IsDisabledWhileBusy()
    {
        var connectGate = new TaskCompletionSource();
        var vm = CreateWithScan(() => new FakeSource(profilesBeforeFault: null, connectGate: connectGate.Task));
        CommitPass(vm);

        var record = vm.RecordCommand.ExecuteAsync(null);
        Assert.False(vm.DeletePassCommand.CanExecute(vm.Passes[0]));
        Assert.False(vm.DeleteScanCommand.CanExecute(null));

        connectGate.SetResult();
        await record.WaitAsync(Timeout);
        await vm.StopCommand.ExecuteAsync(null);
        Assert.True(vm.DeleteScanCommand.CanExecute(null));
    }

    private static double Height(double x, double y) =>
        2 * Math.Sin(y / 3) + 0.8 * Math.Sin(x / 5 + y / 1.7) + 0.5 * Math.Sin(x / 11 - y / 2.3);

    /// <summary>A pass with a second of dead time, then 3 s of steady push, then still: auto trim about 1000..3999 of 5500.</summary>
    private static PassMetadata CommitMoving(WorkbenchViewModel vm, PassDirection direction = PassDirection.CameraFront,
                                             int count = 5500, Func<double, double>? speed = null)
    {
        var options = new SimulationOptions(Height, PointsPerProfile: 64, ProfileRateHz: 1000,
                                            SpeedMmPerSAt: speed ?? (t => t < 4 ? 20 : 0), DeadTimeStartS: 1.0, NoiseMm: 0.002);
        using var pending = vm.Scan!.BeginPass(direction, 64, false, "test", new Dictionary<string, string>());
        foreach (var p in SimulatedProfileSource.Generate(options, count)) pending.Writer.Write(p);
        var pass = pending.Commit(PassStatus.Complete);
        vm.Passes.Add(pass);
        return pass;
    }

    [Fact]
    public async Task SelectPass_AnalysesAndTrims()
    {
        var vm = CreateWithScan(FastSimulation);
        var pass = CommitMoving(vm);
        vm.StatusMessage = null;

        vm.SelectedPass = pass;
        await vm.PreviewLoaded;

        Assert.True(vm.Analysis?.HasMovement);
        var trim = vm.Trim!.Value;
        Assert.InRange(trim.Start, 940, 1060);
        Assert.InRange(trim.End, 3939, 4059);
        Assert.True(vm.TrimIsAuto);
        Assert.Equal(trim.End - trim.Start + 1, vm.Preview?.ProfileCount);
        Assert.Null(vm.StatusMessage);
    }

    [Fact]
    public async Task SelectPass_CameraBack_BuildsReversed()
    {
        var vm = CreateWithScan(FastSimulation);
        var pass = CommitMoving(vm, PassDirection.CameraBack);

        vm.SelectedPass = pass;
        await vm.PreviewLoaded;

        PassPreview Reference(bool reverse)
        {
            using var reader = vm.Scan!.OpenPass(pass.Id);
            return PassPreviewBuilder.Build(reader, new PreviewOptions(),
                                            PassCleanup.From(vm.Analysis!, PassEdits.None, reverse), null, default);
        }
        Assert.Equal(Reference(true).Grid.Z, vm.Preview!.Grid.Z);
        Assert.NotEqual(Reference(false).Grid.Z, vm.Preview.Grid.Z);
    }

    [Fact]
    public async Task SetTrim_SavesEdits_AndSurvivesReopen()
    {
        var vm = CreateWithScan(FastSimulation);
        var pass = CommitMoving(vm);
        vm.SelectedPass = pass;
        await vm.PreviewLoaded;

        Assert.True(vm.SetTrim(1200, 3000));
        await vm.PreviewLoaded;

        Assert.Equal(new ProfileRange(1200, 3000), vm.Trim);
        Assert.False(vm.TrimIsAuto);
        Assert.Equal(1801, vm.Preview?.ProfileCount);
        Assert.Equal(new PassEdits(1200, 3000), vm.Scan!.LoadEdits(pass.Id));

        var reopened = Create(FastSimulation);
        reopened.OpenScanCommand.Execute("scan");
        reopened.SelectedPass = reopened.Passes[0];
        await reopened.PreviewLoaded;
        Assert.Equal(new ProfileRange(1200, 3000), reopened.Trim);
        Assert.False(reopened.TrimIsAuto);
    }

    [Fact]
    public async Task AutoTrim_ClearsEdits()
    {
        var vm = CreateWithScan(FastSimulation);
        var pass = CommitMoving(vm);
        vm.SelectedPass = pass;
        await vm.PreviewLoaded;
        var auto = vm.Trim;
        vm.SetTrim(1200, 3000);
        await vm.PreviewLoaded;

        await vm.AutoTrimCommand.ExecuteAsync(null);
        await vm.PreviewLoaded;

        Assert.Equal(auto, vm.Trim);
        Assert.True(vm.TrimIsAuto);
        Assert.Equal(PassEdits.None, vm.Scan!.LoadEdits(pass.Id));
    }

    [Fact]
    public async Task SetTrim_Invalid_IsRejected()
    {
        var vm = CreateWithScan(FastSimulation);
        var pass = CommitMoving(vm);
        Assert.False(vm.SetTrim(10, 100));
        Assert.Equal("Trim start must be before trim end, inside the pass.", vm.StatusMessage);
        vm.SelectedPass = pass;
        await vm.PreviewLoaded;
        var before = vm.Trim;

        foreach (var (start, end) in new[] { (100, 100), (200, 100), (-1, 100), (0, 5500) })
        {
            vm.StatusMessage = null;
            Assert.False(vm.SetTrim(start, end));
            Assert.Equal("Trim start must be before trim end, inside the pass.", vm.StatusMessage);
        }

        Assert.Equal(before, vm.Trim);
        Assert.True(vm.TrimIsAuto);
        Assert.Equal(PassEdits.None, vm.Scan!.LoadEdits(pass.Id));
        Assert.True(vm.SetTrim(0, 5499));
        await vm.PreviewLoaded;
    }

    [Fact]
    public async Task SelectPass_NoMovement_SetsMessage()
    {
        var vm = CreateWithScan(FastSimulation);
        var pass = CommitMoving(vm, count: 3000, speed: _ => 0);

        vm.SelectedPass = pass;
        await vm.PreviewLoaded;

        Assert.Equal("No movement detected — showing the whole pass.", vm.StatusMessage);
        Assert.Equal(new ProfileRange(0, 2999), vm.Trim);
        Assert.Equal(3000, vm.Preview?.ProfileCount);
    }

    [Fact]
    public async Task SelectPass_CorruptEdits_FallsBackToAuto()
    {
        var vm = CreateWithScan(FastSimulation);
        var pass = CommitMoving(vm);
        File.WriteAllText(Path.Combine(vm.Scan!.Directory, "passes", $"pass-{pass.Id:000}.edit.json"), "{ not json");

        vm.SelectedPass = pass;
        await vm.PreviewLoaded;

        Assert.StartsWith($"Ignoring unreadable edits for pass {pass.Id}: ", vm.StatusMessage);
        Assert.True(vm.TrimIsAuto);
        Assert.NotNull(vm.Preview);
        Assert.InRange(vm.Trim!.Value.Start, 940, 1060);
    }

    [Fact]
    public async Task ShowRaw_BuildsUncleanedPreview()
    {
        var vm = CreateWithScan(FastSimulation);
        var pass = CommitMoving(vm);
        vm.SelectedPass = pass;
        await vm.PreviewLoaded;
        Assert.True(vm.Preview!.ProfileCount < 5500);

        vm.ShowRaw = true;
        await vm.PreviewLoaded;
        Assert.Equal(5500, vm.Preview?.ProfileCount);

        vm.ShowRaw = false;
        await vm.PreviewLoaded;
        Assert.True(vm.Preview!.ProfileCount < 5500);
        await vm.RawGeometryLoaded;
    }

    [Fact]
    public async Task TrimEdit_DoesNotReanalyse()
    {
        var vm = CreateWithScan(FastSimulation);
        var pass = CommitMoving(vm);
        vm.SelectedPass = pass;
        await vm.PreviewLoaded;
        Assert.Equal(1, vm.AnalysisCount);

        vm.SetTrim(1200, 3000);
        await vm.PreviewLoaded;
        await vm.AutoTrimCommand.ExecuteAsync(null);
        await vm.PreviewLoaded;
        vm.ShowRaw = true;
        await vm.PreviewLoaded;
        vm.SelectedPass = null;
        vm.SelectedPass = pass;
        await vm.PreviewLoaded;
        await vm.RawGeometryLoaded;

        Assert.Equal(1, vm.AnalysisCount);
    }

    [Fact]
    public async Task SetTrim_InRawView_KeepsThePreviewAndUpdatesTrimAtOnce()
    {
        var vm = CreateWithScan(FastSimulation);
        var pass = CommitMoving(vm);
        vm.SelectedPass = pass;
        await vm.PreviewLoaded;
        vm.ShowRaw = true;
        await vm.PreviewLoaded;
        var preview = vm.Preview;
        Assert.NotNull(preview);

        Assert.True(vm.SetTrim(1200, 3000));

        Assert.Same(preview, vm.Preview);
        Assert.False(vm.IsLoadingPreview);
        Assert.Equal(new ProfileRange(1200, 3000), vm.Trim);
        Assert.False(vm.TrimIsAuto);
        Assert.Equal(new PassEdits(1200, 3000), vm.Scan!.LoadEdits(pass.Id));

        await vm.AutoTrimCommand.ExecuteAsync(null);

        Assert.Same(preview, vm.Preview);
        Assert.True(vm.TrimIsAuto);
        Assert.InRange(vm.Trim!.Value.Start, 940, 1060);
        Assert.Equal(PassEdits.None, vm.Scan.LoadEdits(pass.Id));

        vm.ShowRaw = false;
        await vm.PreviewLoaded;
        Assert.Equal(vm.Trim!.Value.End - vm.Trim.Value.Start + 1, vm.Preview?.ProfileCount);
        await vm.RawGeometryLoaded;
    }

    [Fact]
    public async Task SetTrim_InCleanedView_RebuildsThePreview()
    {
        var vm = CreateWithScan(FastSimulation);
        var pass = CommitMoving(vm);
        vm.SelectedPass = pass;
        await vm.PreviewLoaded;
        var preview = vm.Preview;

        Assert.True(vm.SetTrim(1200, 3000));
        Assert.Null(vm.Preview);
        await vm.PreviewLoaded;

        Assert.NotSame(preview, vm.Preview);
        Assert.Equal(1801, vm.Preview?.ProfileCount);
    }

    [Fact]
    public async Task SelectPass_WithNoProfiles_ShowsTheEmptyMessageNotALoadError()
    {
        var vm = CreateWithScan(FastSimulation);
        var pass = CommitPass(vm, 0);

        vm.SelectedPass = pass;
        await vm.PreviewLoaded;

        Assert.Null(vm.Trim);
        Assert.Equal("No valid points in this pass.", vm.StatusMessage);
        Assert.False(vm.IsLoadingPreview);
    }

    [Fact]
    public async Task DeleteSelectedPass_WhileAnalysing_WaitsForTheLoadAndSucceeds()
    {
        var vm = CreateWithScan(FastSimulation);
        var pass = CommitMoving(vm, count: 500);
        await SelectAndWaitForOpen(vm, pass);

        await vm.DeletePassCommand.ExecuteAsync(pass);

        Assert.Empty(vm.Scan!.Passes);
        Assert.Null(vm.Analysis);
        Assert.Null(vm.Trim);
    }

    [Fact]
    public void DescribePass_FlagsBackward()
    {
        var pass = new PassMetadata(3, PassDirection.CameraFront, DateTimeOffset.Now, 900, 2560, PassStatus.Complete,
                                    "test", new Dictionary<string, string>(), "");

        Assert.Equal("Pass 3 · 900 lines · camera front · backward?", WorkbenchViewModel.DescribePass(pass, true));
        Assert.Equal("Pass 3 · 900 lines · camera front", WorkbenchViewModel.DescribePass(pass, false));
    }

    [Fact]
    public void DescribePass_NamesTheCameraPosition()
    {
        var pass = new PassMetadata(2, PassDirection.CameraBack, DateTimeOffset.Now, 1500, 2560, PassStatus.Interrupted,
                                    "test", new Dictionary<string, string>(), "");

        Assert.Equal("Pass 2 · 1500 lines · camera back · interrupted", WorkbenchViewModel.DescribePass(pass));
    }

    [Fact]
    public void NewScan_InvalidName_SetsStatusMessage()
    {
        var vm = Create(FastSimulation);

        vm.NewScanCommand.Execute("a/b");

        Assert.Null(vm.Scan);
        Assert.False(string.IsNullOrEmpty(vm.StatusMessage));
    }

    [Fact]
    public async Task Connecting_DisablesScanSwitching_AndShutdownReleasesTheSource()
    {
        var connectGate = new TaskCompletionSource();
        var source = new FakeSource(profilesBeforeFault: null, connectGate: connectGate.Task);
        var vm = CreateWithScan(() => source);

        var record = vm.RecordCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        Assert.False(vm.NewScanCommand.CanExecute("other"));
        Assert.False(vm.OpenScanCommand.CanExecute("other"));

        var shutdown = vm.ShutdownAsync();
        connectGate.SetResult();
        await Task.WhenAll(record, shutdown).WaitAsync(Timeout);

        Assert.False(vm.IsBusy);
        Assert.False(vm.IsRecording);
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task RecordingEnd_SourceDisposeFails_StillEndsRecording()
    {
        var vm = CreateWithScan(() => new FakeSource(profilesBeforeFault: 2, disposeError: new IOException("release failed")));

        await vm.RecordCommand.ExecuteAsync(null);
        await WaitUntil(() => !vm.IsRecording);

        Assert.False(vm.IsBusy);
        Assert.True(vm.RecordCommand.CanExecute(null));
        Assert.Contains("release failed", vm.StatusMessage);
    }

    private WorkbenchViewModel CreateWithCombine(CombineOperation combine)
    {
        var vm = new WorkbenchViewModel(_ => FastSimulation(), DataFolder, combine);
        vm.NewScanCommand.Execute("scan");
        Assert.NotNull(vm.Scan);
        return vm;
    }

    /// <summary>Stands in for the slow real combine: saves a small result through the real repository.</summary>
    private static Task<CombineResult> SaveSmallResult(ScanRepository scan, IReadOnlyList<int> ids,
                                                       IProgress<(string stage, double fraction)>? progress, CancellationToken ct)
    {
        progress?.Report(("Merging", 1.0));
        var outcomes = ids.Select((id, i) => new PassOutcome(id, new PassPose(0, i, 0), new SpeedProfile([0, 1], [0, 20]), 20, 0.1, 0.13)).ToList();
        var cloud = new MergedCloud([0f, 1f], [0f, 1f], [1f, 2f], [0, 1]);
        return Task.FromResult(scan.SaveResult(id => new CombineResult(id, DateTimeOffset.Now, ids[0], outcomes, false, false), cloud));
    }

    private static CombineResult SaveResult(WorkbenchViewModel vm, params int[] ids) =>
        SaveSmallResult(vm.Scan!, ids, null, default).Result;

    [Fact]
    public async Task TrimEditing_IsDisabledWhileCombining()
    {
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var vm = CreateWithCombine(async (_, _, _, _) =>
        {
            started.SetResult();
            await release.Task;
            throw new InvalidOperationException("stop");
        });
        var pass = CommitMoving(vm);
        CommitMoving(vm);
        vm.SelectedPass = pass;
        await vm.PreviewLoaded;
        Assert.True(vm.CanEditTrim);
        Assert.True(vm.AutoTrimCommand.CanExecute(null));
        vm.CheckedPasses.Add(1);
        vm.CheckedPasses.Add(2);

        var run = vm.CombineCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(Timeout);

        Assert.False(vm.CanEditTrim);
        Assert.False(vm.AutoTrimCommand.CanExecute(null));
        Assert.False(vm.SetTrim(1200, 3000));
        Assert.Equal(PassEdits.None, vm.Scan!.LoadEdits(pass.Id));

        release.SetResult();
        await run.WaitAsync(Timeout);
        Assert.True(vm.CanEditTrim);
        Assert.True(vm.SetTrim(1200, 3000));
        await vm.PreviewLoaded;
    }

    /// <summary>
    /// Like <see cref="CommitMoving"/> over a table tilted 0.5 mm across the 200 mm line and 0.02 mm per mm of travel,
    /// with a 40 x 20 x 10 mm box at x -20..20, travel 20..40 mm.
    /// </summary>
    private static PassMetadata CommitBox(WorkbenchViewModel vm)
    {
        var options = new SimulationOptions((x, y) =>
        {
            double z = 0.5 * (x + 100) / 200 + 0.02 * y;
            return Math.Abs(x) <= 20 && y is >= 20 and <= 40 ? z + 10 : z;
        }, PointsPerProfile: 200, ProfileRateHz: 1000, SpeedMmPerSAt: t => t < 4 ? 20 : 0, DeadTimeStartS: 1.0, NoiseMm: 0.002);
        using var pending = vm.Scan!.BeginPass(PassDirection.CameraFront, 200, false, "test", new Dictionary<string, string>());
        foreach (var p in SimulatedProfileSource.Generate(options, 5500)) pending.Writer.Write(p);
        var pass = pending.Commit(PassStatus.Complete);
        vm.Passes.Add(pass);
        return pass;
    }

    private async Task<WorkbenchViewModel> SelectedMoving()
    {
        var vm = CreateWithScan(FastSimulation);
        vm.SelectedPass = CommitMoving(vm);
        await vm.PreviewLoaded;
        return vm;
    }

    [Fact]
    public async Task SetZone_SavesBesideTrims()
    {
        var vm = await SelectedMoving();

        Assert.True(vm.SetTrim(10, 200));
        await vm.PreviewLoaded;
        Assert.True(vm.SetZone(new(-50, 50, null, null)));
        await vm.PreviewLoaded;

        Assert.Equal(new PassEdits(10, 200, -50, 50), vm.Scan!.LoadEdits(vm.SelectedPass!.Id));
        Assert.Equal(new PassZone(-50, 50, null, null), vm.Zone);
        Assert.Equal(new ProfileRange(10, 200), vm.Trim);
    }

    [Fact]
    public async Task AutoTrim_KeepsTheZone()
    {
        var vm = await SelectedMoving();
        var auto = vm.Trim;
        vm.SetZone(new(-50, 50, null, null));
        await vm.PreviewLoaded;
        vm.SetTrim(1200, 3000);
        await vm.PreviewLoaded;

        await vm.AutoTrimCommand.ExecuteAsync(null);
        await vm.PreviewLoaded;

        Assert.True(vm.TrimIsAuto);
        Assert.Equal(auto, vm.Trim);
        Assert.Equal(new PassZone(-50, 50, null, null), vm.Zone);
        Assert.Equal(new PassEdits(null, null, -50, 50), vm.Scan!.LoadEdits(vm.SelectedPass!.Id));
    }

    [Fact]
    public async Task ClearZone_KeepsTheTrims()
    {
        var vm = await SelectedMoving();
        Assert.False(vm.ClearZoneCommand.CanExecute(null));
        vm.SetTrim(1200, 3000);
        await vm.PreviewLoaded;
        vm.SetZone(new(-50, 50, 0.5, 4));
        await vm.PreviewLoaded;
        Assert.True(vm.ClearZoneCommand.CanExecute(null));

        await vm.ClearZoneCommand.ExecuteAsync(null);
        await vm.PreviewLoaded;

        Assert.Equal(PassZone.None, vm.Zone);
        Assert.False(vm.ClearZoneCommand.CanExecute(null));
        Assert.Equal(new ProfileRange(1200, 3000), vm.Trim);
        Assert.Equal(new PassEdits(1200, 3000), vm.Scan!.LoadEdits(vm.SelectedPass!.Id));
    }

    [Fact]
    public async Task SetZone_Invalid_KeepsOldZoneWithMessage()
    {
        var vm = await SelectedMoving();
        var zone = new PassZone(-50, 50, null, null);
        vm.SetZone(zone);
        await vm.PreviewLoaded;

        foreach (var bad in new PassZone[] { new(50, -50, null, null), new(10, 10, null, null), new(null, null, 4, 1),
                                             new(null, null, 2, 2), new(double.NaN, null, null, null) })
        {
            vm.StatusMessage = null;
            Assert.False(vm.SetZone(bad));
            Assert.Equal(WorkbenchViewModel.InvalidZoneMessage, vm.StatusMessage);
        }

        Assert.Equal(zone, vm.Zone);
        Assert.Equal(PassEdits.None.WithZone(zone), vm.Scan!.LoadEdits(vm.SelectedPass!.Id));
    }

    [Fact]
    public async Task SetZone_WhileCombining_IsRejected()
    {
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var vm = CreateWithCombine(async (_, _, _, _) =>
        {
            started.SetResult();
            await release.Task;
            throw new InvalidOperationException("stop");
        });
        var pass = CommitMoving(vm);
        CommitMoving(vm);
        vm.SelectedPass = pass;
        await vm.PreviewLoaded;
        vm.SetZone(new(-50, 50, null, null));
        await vm.PreviewLoaded;
        Assert.True(vm.ClearZoneCommand.CanExecute(null));
        vm.CheckedPasses.Add(1);
        vm.CheckedPasses.Add(2);

        var run = vm.CombineCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(Timeout);

        Assert.False(vm.ClearZoneCommand.CanExecute(null));
        Assert.False(vm.SetZone(new(-10, 10, null, null)));
        Assert.Equal(new PassEdits(null, null, -50, 50), vm.Scan!.LoadEdits(pass.Id));

        release.SetResult();
        await run.WaitAsync(Timeout);
        Assert.True(vm.ClearZoneCommand.CanExecute(null));
        Assert.True(vm.SetZone(new(-10, 10, null, null)));
        await vm.PreviewLoaded;
    }

    [Fact]
    public async Task SelectPass_LoadsItsZone()
    {
        var vm = CreateWithScan(FastSimulation);
        var zoned = CommitMoving(vm);
        var plain = CommitMoving(vm);
        var zone = new PassZone(-20, 20, 1, null);
        vm.Scan!.SaveEdits(zoned.Id, new PassEdits(1200, 3000).WithZone(zone));
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SelectedPass = zoned;
        await vm.PreviewLoaded;

        Assert.Equal(zone, vm.Zone);
        Assert.Contains(nameof(WorkbenchViewModel.Zone), changed);
        Assert.True(vm.ClearZoneCommand.CanExecute(null));

        vm.SelectedPass = plain;
        await vm.PreviewLoaded;

        Assert.Equal(PassZone.None, vm.Zone);
        Assert.False(vm.ClearZoneCommand.CanExecute(null));
    }

    [Fact]
    public async Task CleanedPreview_DropsOutOfZonePoints()
    {
        var vm = CreateWithScan(FastSimulation);
        vm.SelectedPass = CommitBox(vm);
        await vm.PreviewLoaded;
        Assert.Contains(vm.Preview!.Points.X, x => x < -30);

        Assert.True(vm.SetZone(new(-30, 30, 1, null)));
        await vm.PreviewLoaded;

        var points = vm.Preview!.Points;
        Assert.NotEmpty(points.X);
        Assert.All(points.X, x => Assert.InRange(x, -20.5f, 20.5f));
        Assert.All(points.Z, z => Assert.True(z > 9f));
    }

    [Fact]
    public async Task CleanedPreview_HidesReflections_RawShowsThem()
    {
        // Like CommitMoving, with reflectance and three dim blobs 11 mm above the textured surface (at most ±3.3 mm).
        var options = new SimulationOptions(Height, PointsPerProfile: 400, ProfileRateHz: 1000,
            SpeedMmPerSAt: t => t < 4 ? 20 : 0, DeadTimeStartS: 1.0, NoiseMm: 0.002, WithReflectance: true,
            Spikes: [new SpikeBlob(-40, 15, 2, 11), new SpikeBlob(0, 30, 2, 11), new SpikeBlob(50, 45, 2, 11)]);
        var vm = CreateWithScan(FastSimulation);
        PassMetadata pass;
        using (var pending = vm.Scan!.BeginPass(PassDirection.CameraFront, 400, true, "test", new Dictionary<string, string>()))
        {
            foreach (var p in SimulatedProfileSource.Generate(options, 5500)) pending.Writer.Write(p);
            pass = pending.Commit(PassStatus.Complete);
        }
        vm.Passes.Add(pass);

        vm.SelectedPass = pass;
        await vm.PreviewLoaded;
        Assert.True(vm.Preview!.ZMax < 5, $"cleaned z max {vm.Preview.ZMax}");

        vm.ShowRaw = true;
        await vm.PreviewLoaded;
        Assert.True(vm.Preview!.ZMax > 9, $"raw z max {vm.Preview.ZMax}");

        vm.ShowRaw = false;
        await vm.PreviewLoaded;
        Assert.True(vm.Preview!.ZMax < 5, $"cleaned z max {vm.Preview.ZMax}");
        await vm.RawGeometryLoaded;
    }

    private static async Task RawReady(WorkbenchViewModel vm)
    {
        await vm.PreviewLoaded.WaitAsync(Timeout);
        await vm.RawGeometryLoaded.WaitAsync(Timeout);
    }

    private async Task<(WorkbenchViewModel vm, PassMetadata pass)> InRawView(Func<WorkbenchViewModel, PassMetadata>? commit = null)
    {
        var vm = CreateWithScan(FastSimulation);
        var pass = (commit ?? (v => CommitMoving(v)))(vm);
        vm.SelectedPass = pass;
        await vm.PreviewLoaded;
        vm.ShowRaw = true;
        await RawReady(vm);
        return (vm, pass);
    }

    /// <summary>Like <see cref="CommitMoving"/>, with reflectance and three dim blobs 11 mm above the textured surface.</summary>
    private static PassMetadata CommitReflective(WorkbenchViewModel vm)
    {
        var options = new SimulationOptions(Height, PointsPerProfile: 400, ProfileRateHz: 1000,
            SpeedMmPerSAt: t => t < 4 ? 20 : 0, DeadTimeStartS: 1.0, NoiseMm: 0.002, WithReflectance: true,
            Spikes: [new SpikeBlob(-40, 15, 2, 11), new SpikeBlob(0, 30, 2, 11), new SpikeBlob(50, 45, 2, 11)]);
        using var pending = vm.Scan!.BeginPass(PassDirection.CameraFront, 400, true, "test", new Dictionary<string, string>());
        foreach (var p in SimulatedProfileSource.Generate(options, 5500)) pending.Writer.Write(p);
        var pass = pending.Commit(PassStatus.Complete);
        vm.Passes.Add(pass);
        return pass;
    }

    private static string EditsPath(WorkbenchViewModel vm, PassMetadata pass) =>
        Path.Combine(vm.Scan!.Directory, "passes", $"pass-{pass.Id:000}.edit.json");

    [Fact]
    public async Task CommitLimit_InRaw_DoesNotRebuildPreview()
    {
        var (vm, pass) = await InRawView();
        var preview = vm.Preview;
        var geometry = vm.RawGeometry;
        Assert.NotNull(preview);
        Assert.NotNull(geometry);

        Assert.True(vm.CommitLimit(LimitKind.TrimStart, 1.5));

        Assert.Same(preview, vm.Preview);
        Assert.False(vm.IsLoadingPreview);
        int start = WorkbenchViewModel.NearestIndex(vm.Analysis!.TimeS, 1.5);
        Assert.Equal(start, vm.Trim!.Value.Start);
        Assert.Equal(start, vm.Scan!.LoadEdits(pass.Id).TrimStart);
        Assert.Null(vm.Scan.LoadEdits(pass.Id).TrimEnd);
        Assert.Equal(vm.Analysis.TimeS[start], vm.LimitValue(LimitKind.TrimStart));

        await vm.RawGeometryLoaded.WaitAsync(Timeout);
        Assert.Same(preview, vm.Preview);
        Assert.NotNull(vm.RawGeometry);
        Assert.NotSame(geometry, vm.RawGeometry);

        // A zone limit needs no refit: the geometry stays and the preview stays.
        var refitted = vm.RawGeometry;
        Assert.True(vm.CommitLimit(LimitKind.XMin, -30));
        await vm.RawGeometryLoaded.WaitAsync(Timeout);
        Assert.Same(preview, vm.Preview);
        Assert.Same(refitted, vm.RawGeometry);
        Assert.Equal(-30, vm.Zone.XMin);
        Assert.Equal(-30, vm.ShownLimits!.XMin);
    }

    [Fact]
    public async Task PreviewLimit_ChangesShownLimitsWithoutSaving()
    {
        var (vm, pass) = await InRawView();
        var saved = vm.ShownLimits!;
        Assert.Equal(double.NegativeInfinity, saved.XMin);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.PreviewLimit(LimitKind.XMin, -30);
        vm.PreviewLimit(LimitKind.TrimEnd, 3.5);

        Assert.Contains(nameof(WorkbenchViewModel.ShownLimits), changed);
        Assert.Equal(-30, vm.ShownLimits!.XMin);
        Assert.Equal(-30, vm.LimitValue(LimitKind.XMin));
        int end = WorkbenchViewModel.NearestIndex(vm.Analysis!.TimeS, 3.5);
        Assert.Equal(end, vm.ShownLimits.TrimEnd);
        Assert.Equal(saved.TrimStart, vm.ShownLimits.TrimStart);
        Assert.False(File.Exists(EditsPath(vm, pass)));
        Assert.Equal(PassZone.None, vm.Zone);
        Assert.True(vm.TrimIsAuto);

        // Back to "no limit" while dragging, then the committed value replaces the preview.
        vm.PreviewLimit(LimitKind.XMin, null);
        Assert.Equal(double.NegativeInfinity, vm.ShownLimits!.XMin);
        Assert.Null(vm.LimitValue(LimitKind.XMin));
        Assert.True(vm.CommitLimit(LimitKind.XMin, -20));
        Assert.Equal(-20, vm.ShownLimits!.XMin);
        Assert.Equal(saved.TrimEnd, vm.ShownLimits.TrimEnd);
        Assert.Equal(new PassEdits(null, null, -20), vm.Scan!.LoadEdits(pass.Id));
        await vm.RawGeometryLoaded.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Refresh_SupersededByNewerEdit_IsDiscarded()
    {
        var (vm, pass) = await InRawView();
        vm.StatusMessage = null;
        var gate = new TaskCompletionSource();
        vm.RawRefreshGate = gate.Task;
        vm.RawRefreshOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new List<RawGeometry?>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkbenchViewModel.RawGeometry)) applied.Add(vm.RawGeometry);
        };

        Assert.True(vm.CommitLimit(LimitKind.TrimStart, 1.2));
        await vm.RawRefreshOpened.Task.WaitAsync(Timeout);
        var first = vm.RawGeometryLoaded;
        Assert.True(vm.CommitLimit(LimitKind.TrimStart, 1.6));
        gate.SetResult();
        await first.WaitAsync(Timeout);
        await vm.RawGeometryLoaded.WaitAsync(Timeout);

        Assert.Single(applied);
        Assert.Null(vm.StatusMessage);

        // What was applied is the geometry of the newer trim.
        var analysis = vm.Analysis!;
        var cleanup = PassCleanup.From(analysis, vm.Scan!.LoadEdits(pass.Id), reverse: false);
        Assert.Equal(WorkbenchViewModel.NearestIndex(analysis.TimeS, 1.6), cleanup.Trim.Start);
        using var reader = vm.Scan.OpenPass(pass.Id);
        var expected = RawGeometry.Build(vm.Preview!, analysis,
                                         cleanup with { Table = AlignmentMapBuilder.Build(reader, cleanup, null, default).Table }, null);
        Assert.Equal(expected.CellHeight, vm.RawGeometry!.CellHeight);
    }

    [Fact]
    public async Task RawFit_Failed_IsRetriedByRecommitting()
    {
        var vm = CreateWithScan(FastSimulation);
        var pass = CommitBox(vm);
        vm.SelectedPass = pass;
        await vm.PreviewLoaded;
        vm.RawRefreshGate = Task.FromException(new IOException("disk gone"));

        vm.ShowRaw = true;
        await vm.PreviewLoaded.WaitAsync(Timeout);
        await vm.RawGeometryLoaded.WaitAsync(Timeout);

        Assert.Equal(RawFitState.Failed, vm.RawFit);
        Assert.False(vm.HeightLimitsReady);
        Assert.Contains("disk gone", vm.StatusMessage);

        vm.RawRefreshGate = null;
        Assert.True(vm.CommitLimit(LimitKind.TrimStart, null));   // the same, automatic trim again
        Assert.Equal(RawFitState.Fitting, vm.RawFit);
        await vm.RawGeometryLoaded.WaitAsync(Timeout);

        Assert.Equal(RawFitState.Ready, vm.RawFit);
        Assert.True(vm.HeightLimitsReady);
    }

    [Fact]
    public async Task RawFit_NoTable_LeavesHeightLimitsUnavailable()
    {
        var vm = CreateWithScan(FastSimulation);
        var pass = CommitBox(vm);
        vm.SelectedPass = pass;
        await vm.PreviewLoaded;
        vm.RawTableSeam = _ => null;
        Assert.Equal(RawFitState.Fitting, vm.RawFit);

        vm.ShowRaw = true;
        await vm.PreviewLoaded.WaitAsync(Timeout);
        await vm.RawGeometryLoaded.WaitAsync(Timeout);

        Assert.Equal(RawFitState.NoTable, vm.RawFit);
        Assert.NotNull(vm.RawGeometry);
        Assert.Null(vm.RawTable);
        Assert.False(vm.HeightLimitsReady);
        Assert.Null(vm.ScaleOf(LimitKind.HeightMin));
    }

    [Fact]
    public async Task HeightLimits_NotReadyUntilTableFitted()
    {
        var vm = CreateWithScan(FastSimulation);
        var pass = CommitBox(vm);
        vm.SelectedPass = pass;
        await vm.PreviewLoaded;
        var gate = new TaskCompletionSource();
        vm.RawRefreshGate = gate.Task;
        vm.RawRefreshOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        vm.ShowRaw = true;
        await vm.PreviewLoaded.WaitAsync(Timeout);
        await vm.RawRefreshOpened.Task.WaitAsync(Timeout);

        Assert.NotNull(vm.Preview);
        Assert.NotNull(vm.ShownLimits);
        Assert.False(vm.HeightLimitsReady);
        Assert.Null(vm.RawGeometry);
        Assert.Null(vm.RawTable);
        Assert.Null(vm.ScaleOf(LimitKind.HeightMin));
        Assert.Null(vm.ScaleOf(LimitKind.HeightMax));
        Assert.NotNull(vm.ScaleOf(LimitKind.TrimStart));
        Assert.NotNull(vm.ScaleOf(LimitKind.XMin));

        gate.SetResult();
        await vm.RawGeometryLoaded.WaitAsync(Timeout);

        Assert.True(vm.HeightLimitsReady);
        Assert.NotNull(vm.RawTable);
        var height = vm.ScaleOf(LimitKind.HeightMax)!;
        var heights = vm.RawGeometry!.PointHeight.Where(float.IsFinite).ToArray();
        Assert.Equal(heights.Min(), height.Min, 5);
        Assert.Equal(heights.Max(), height.Max, 5);
        Assert.InRange(height.Max, 9, 11);   // the 10 mm box above the table

        var trim = vm.ScaleOf(LimitKind.TrimEnd)!;
        Assert.Equal(vm.Analysis!.TimeS[0], trim.Min);
        Assert.Equal(vm.Analysis.TimeS[^1], trim.Max);
        var x = vm.ScaleOf(LimitKind.XMin)!;
        Assert.Equal(vm.Preview!.Grid.XMin, x.Min);
        Assert.Equal(vm.Preview.Grid.XMax, x.Max);
    }

    [Fact]
    public async Task ShowRawOff_AfterCut_RebuildsCleanedOnce()
    {
        var (vm, _) = await InRawView(CommitBox);
        Assert.True(vm.CommitLimit(LimitKind.TrimStart, 1.2));
        Assert.True(vm.CommitLimit(LimitKind.TrimEnd, 3.0));
        Assert.True(vm.CommitLimit(LimitKind.XMin, -30));
        Assert.True(vm.CommitLimit(LimitKind.XMax, 30));
        await vm.RawGeometryLoaded.WaitAsync(Timeout);
        int built = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorkbenchViewModel.Preview) && vm.Preview is not null) built++;
        };

        vm.ShowRaw = false;
        await vm.PreviewLoaded.WaitAsync(Timeout);

        await vm.RawGeometryLoaded.WaitAsync(Timeout);
        Assert.Equal(1, built);
        Assert.Null(vm.RawGeometry);
        Assert.Null(vm.ShownLimits);
        var trim = vm.Trim!.Value;
        Assert.Equal(trim.End - trim.Start + 1, vm.Preview!.ProfileCount);
        Assert.All(vm.Preview.Points.X, x => Assert.InRange(x, -30f, 30f));
    }

    [Fact]
    public async Task FilterSwitch_PassWithoutReflectance_IsDisabled()
    {
        var (vm, pass) = await InRawView();

        Assert.False(vm.CanSwitchFilter);
        Assert.True(vm.FilterReflections);
        Assert.False(vm.SetFilterReflections(false));
        Assert.True(vm.FilterReflections);
        Assert.False(File.Exists(EditsPath(vm, pass)));
    }

    [Fact]
    public async Task SetFilterReflections_SavesAndRefreshes()
    {
        var (vm, pass) = await InRawView(CommitReflective);
        Assert.True(vm.CanSwitchFilter);
        Assert.True(vm.FilterReflections);
        Assert.True(vm.ShownLimits!.FilterReflections);
        var preview = vm.Preview;
        var geometry = vm.RawGeometry!;
        Assert.Contains(geometry.PointFlags, f => (f & RawGeometry.Reflection) != 0);

        Assert.True(vm.SetFilterReflections(false));

        Assert.False(vm.FilterReflections);
        Assert.False(vm.ShownLimits!.FilterReflections);
        Assert.False(vm.Scan!.LoadEdits(pass.Id).ReflectionFilter);
        Assert.Same(preview, vm.Preview);
        await vm.RawGeometryLoaded.WaitAsync(Timeout);
        Assert.NotSame(geometry, vm.RawGeometry);
        // With the filter off the raw view still marks what the filter would remove.
        Assert.Contains(vm.RawGeometry!.PointFlags, f => (f & RawGeometry.Reflection) != 0);

        // The cleaned view keeps the spikes with the filter off and drops them once it is back on.
        vm.ShowRaw = false;
        await vm.PreviewLoaded.WaitAsync(Timeout);
        Assert.True(vm.Preview!.ZMax > 9, $"cleaned z max {vm.Preview.ZMax}");
        Assert.True(vm.SetFilterReflections(true));
        await vm.PreviewLoaded.WaitAsync(Timeout);
        Assert.True(vm.Preview!.ZMax < 5, $"cleaned z max {vm.Preview.ZMax}");
        Assert.True(vm.FilterReflections);
        Assert.Equal(PassEdits.None, vm.Scan.LoadEdits(pass.Id));
        await vm.RawGeometryLoaded.WaitAsync(Timeout);
    }

    [Fact]
    public async Task CommitLimit_WhileCombining_IsRejected()
    {
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var vm = CreateWithCombine(async (_, _, _, _) =>
        {
            started.SetResult();
            await release.Task;
            throw new InvalidOperationException("stop");
        });
        var pass = CommitMoving(vm);
        CommitMoving(vm);
        vm.SelectedPass = pass;
        await vm.PreviewLoaded;
        vm.ShowRaw = true;
        await RawReady(vm);
        vm.CheckedPasses.Add(1);
        vm.CheckedPasses.Add(2);

        var run = vm.CombineCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(Timeout);

        var shown = vm.ShownLimits;
        Assert.False(vm.CommitLimit(LimitKind.TrimStart, 1.5));
        Assert.False(vm.CommitLimit(LimitKind.XMin, -10));
        vm.PreviewLimit(LimitKind.XMax, 10);
        Assert.Equal(shown, vm.ShownLimits);
        Assert.False(vm.CanSwitchFilter);
        Assert.False(vm.SetFilterReflections(false));
        Assert.False(File.Exists(EditsPath(vm, pass)));

        release.SetResult();
        await run.WaitAsync(Timeout);
        Assert.True(vm.CommitLimit(LimitKind.XMin, -10));
        await vm.RawGeometryLoaded.WaitAsync(Timeout);
    }

    [Fact]
    public async Task ShownLimits_NullOutsideRawViews()
    {
        var vm = CreateWithScan(FastSimulation);
        var pass = CommitMoving(vm);
        var other = CommitMoving(vm);
        Assert.Null(vm.ShownLimits);
        vm.ShowRaw = true;
        Assert.Null(vm.ShownLimits);

        vm.SelectedPass = pass;
        await RawReady(vm);
        Assert.Equal(CutLimits.From(vm.Trim!.Value, PassZone.None, true), vm.ShownLimits);

        vm.ShowRaw = false;
        await vm.PreviewLoaded;
        Assert.Null(vm.ShownLimits);

        vm.ShowRaw = true;
        await RawReady(vm);
        Assert.NotNull(vm.ShownLimits);
        vm.SelectedResult = SaveResult(vm, pass.Id, other.Id);
        Assert.Null(vm.ShownLimits);
        Assert.Null(vm.RawGeometry);
        await vm.ResultLoaded;
        await vm.RawGeometryLoaded;
    }

    [Fact]
    public void Combine_DisabledWithFewerThanTwoPasses()
    {
        var vm = Create(FastSimulation);
        vm.CheckedPasses.Add(1);
        vm.CheckedPasses.Add(2);
        Assert.False(vm.CombineCommand.CanExecute(null));   // no scan open

        vm = CreateWithCombine(SaveSmallResult);
        var changes = 0;
        vm.CombineCommand.CanExecuteChanged += (_, _) => changes++;
        Assert.False(vm.CombineCommand.CanExecute(null));
        vm.CheckedPasses.Add(1);
        Assert.False(vm.CombineCommand.CanExecute(null));
        vm.CheckedPasses.Add(2);
        Assert.True(vm.CombineCommand.CanExecute(null));
        vm.CheckedPasses.Remove(2);
        Assert.False(vm.CombineCommand.CanExecute(null));
        Assert.True(changes >= 3);
    }

    [Fact]
    public async Task Combine_AddsAndSelectsResult()
    {
        var vm = CreateWithCombine(SaveSmallResult);
        CommitPass(vm);
        CommitPass(vm);
        vm.CheckedPasses.Add(2);
        vm.CheckedPasses.Add(1);

        await vm.CombineCommand.ExecuteAsync(null);
        await vm.ResultLoaded;

        var result = Assert.Single(vm.Results);
        Assert.Equal(2, result.ReferencePassId);   // the first ticked is the reference
        Assert.Same(result, vm.SelectedResult);
        Assert.Equal(2, vm.ResultCloud?.X.Length);
        Assert.Single(vm.Scan!.Results);
        Assert.False(vm.IsCombining);
        Assert.False(vm.IsBusy);
        Assert.Null(vm.CombineStage);
    }

    [Fact]
    public async Task Cancel_ReturnsToIdle()
    {
        var started = new TaskCompletionSource();
        var vm = CreateWithCombine(async (_, _, progress, ct) =>
        {
            progress?.Report(("Aligning", 0.5));
            started.SetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        vm.CheckedPasses.Add(1);
        vm.CheckedPasses.Add(2);

        var run = vm.CombineCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(Timeout);
        Assert.True(vm.IsCombining);
        Assert.True(vm.IsBusy);
        Assert.False(vm.CombineCommand.CanExecute(null));
        Assert.True(vm.CancelCombineCommand.CanExecute(null));
        vm.CancelCombineCommand.Execute(null);
        await run.WaitAsync(Timeout);

        Assert.False(vm.IsCombining);
        Assert.False(vm.IsBusy);
        Assert.Null(vm.CombineStage);
        Assert.Equal(0, vm.CombineProgress);
        Assert.Equal("Combine cancelled.", vm.StatusMessage);
        Assert.Empty(vm.Results);
        Assert.False(vm.CancelCombineCommand.CanExecute(null));
    }

    [Fact]
    public async Task CombineFailure_SetsMessage()
    {
        var vm = CreateWithCombine((_, _, _, _) => throw new InvalidDataException("No object found in pass 2."));
        vm.CheckedPasses.Add(1);
        vm.CheckedPasses.Add(2);

        await vm.CombineCommand.ExecuteAsync(null);

        Assert.Equal("Combine failed: No object found in pass 2.", vm.StatusMessage);
        Assert.False(vm.IsCombining);
        Assert.False(vm.IsBusy);
        Assert.Empty(vm.Results);
    }

    [Fact]
    public async Task SelectResult_LoadsCloud_AndClearsPassSelection()
    {
        var vm = CreateWithScan(FastSimulation);
        var pass = CommitPass(vm, 10);
        var result = SaveResult(vm, 1, 2);
        vm.Results.Add(result);
        vm.SelectedPass = pass;
        await vm.PreviewLoaded;
        Assert.NotNull(vm.Preview);

        vm.SelectedResult = result;
        await vm.ResultLoaded;

        Assert.Null(vm.SelectedPass);
        Assert.Null(vm.Preview);
        Assert.Equal(2, vm.ResultCloud?.X.Length);

        vm.SelectedPass = pass;
        Assert.Null(vm.SelectedResult);
        Assert.Null(vm.ResultCloud);
    }

    [Fact]
    public async Task SelectResult_Switching_ShowsOnlyTheLatest()
    {
        var vm = CreateWithScan(FastSimulation);
        var first = SaveResult(vm, 1, 2);
        var second = SaveResult(vm, 1, 3);
        vm.Results.Add(first);
        vm.Results.Add(second);

        vm.SelectedResult = first;
        vm.SelectedResult = second;
        await vm.ResultLoaded;
        await Task.Delay(100);

        Assert.Same(second, vm.SelectedResult);
        Assert.NotNull(vm.ResultCloud);
        Assert.False(vm.IsLoadingResult);
    }

    [Fact]
    public async Task DeleteResult_RemovesIt()
    {
        var vm = CreateWithScan(FastSimulation);
        var keep = SaveResult(vm, 1, 2);
        var gone = SaveResult(vm, 1, 3);
        vm.Results.Add(keep);
        vm.Results.Add(gone);
        vm.SelectedResult = gone;
        await vm.ResultLoaded;

        await vm.DeleteResultCommand.ExecuteAsync(gone);

        Assert.Equal([keep.Id], vm.Results.Select(r => r.Id));
        Assert.Equal([keep.Id], vm.Scan!.Results.Select(r => r.Id));
        Assert.Null(vm.SelectedResult);
        Assert.Null(vm.ResultCloud);
    }

    [Fact]
    public void ResultsSurviveReopen()
    {
        var vm = CreateWithScan(FastSimulation);
        var saved = SaveResult(vm, 1, 2);

        var reopened = Create(FastSimulation);
        reopened.OpenScanCommand.Execute("scan");

        Assert.Equal([saved.Id], reopened.Results.Select(r => r.Id));
    }

    [Fact]
    public async Task CheckedPasses_ClearedByOpeningAScan_AndPassDelete()
    {
        var vm = CreateWithScan(FastSimulation);
        CommitPass(vm);
        CommitPass(vm);
        vm.CheckedPasses.Add(1);
        vm.CheckedPasses.Add(2);

        await vm.DeletePassCommand.ExecuteAsync(vm.Passes[0]);
        Assert.Equal([2], vm.CheckedPasses);

        vm.OpenScanCommand.Execute("scan");
        Assert.Empty(vm.CheckedPasses);
    }

    [Fact]
    public void DescribeResult_ShowsFlags()
    {
        PassOutcome Outcome(int id, double mad) => new(id, new PassPose(0, 0, 0), new SpeedProfile([0, 1], [0, 1]), 20, 0, mad);
        CombineResult Make(bool uncertain, bool scaleUnknown) =>
            new(3, DateTimeOffset.Now, 1, [Outcome(1, 0.5), Outcome(4, 0.126)], uncertain, scaleUnknown);

        Assert.Equal("Result 3 · passes 1, 4 · overlap 0.13 mm", WorkbenchViewModel.DescribeResult(Make(false, false)));
        Assert.Equal("Result 3 · passes 1, 4 · overlap 0.13 mm · uncertain", WorkbenchViewModel.DescribeResult(Make(true, false)));
        Assert.Equal("Result 3 · passes 1, 4 · overlap 0.13 mm · scale unknown", WorkbenchViewModel.DescribeResult(Make(false, true)));
        Assert.Equal("Result 3 · passes 1, 4 · overlap 0.13 mm · uncertain · scale unknown", WorkbenchViewModel.DescribeResult(Make(true, true)));
    }

    [Fact]
    public async Task Shutdown_CancelsARunningCombine()
    {
        var started = new TaskCompletionSource();
        var vm = CreateWithCombine(async (_, _, _, ct) =>
        {
            started.SetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        vm.CheckedPasses.Add(1);
        vm.CheckedPasses.Add(2);
        var run = vm.CombineCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(Timeout);

        await vm.ShutdownAsync().WaitAsync(Timeout);

        Assert.True(run.IsCompleted);
        Assert.False(vm.IsCombining);
        Assert.False(vm.IsBusy);   // so the window closes
        Assert.Equal("Combine cancelled.", vm.StatusMessage);
    }

    [Fact]
    public async Task OutcomeLines_MarkADeletedPass()
    {
        var vm = CreateWithScan(FastSimulation);
        CommitPass(vm, 10);
        var gone = CommitPass(vm, 10);
        var result = SaveResult(vm, 1, gone.Id);

        await vm.DeletePassCommand.ExecuteAsync(gone);
        var lines = vm.OutcomeLines(result);

        Assert.Equal(2, lines.Count);
        Assert.DoesNotContain("(deleted)", lines[0]);
        Assert.StartsWith($"pass {gone.Id}:", lines[1]);
        Assert.EndsWith(" (deleted)", lines[1]);
    }

    [Fact]
    public async Task DeleteResult_KeepsANewerSelection()
    {
        var vm = CreateWithScan(FastSimulation);
        // A large cloud, so its load is still running when the delete starts waiting for it.
        int n = 1_000_000;
        var big = vm.Scan!.SaveResult(id => new CombineResult(id, DateTimeOffset.Now, 1,
            [new PassOutcome(1, new PassPose(0, 0, 0), new SpeedProfile([0, 1], [0, 20]), 20, 0, 0.1)], false, false),
            new MergedCloud(new float[n], new float[n], new float[n], new byte[n]));
        var other = SaveResult(vm, 1, 2);
        vm.Results.Add(big);
        vm.Results.Add(other);
        vm.SelectedResult = big;

        var delete = vm.DeleteResultCommand.ExecuteAsync(big);
        Assert.False(delete.IsCompleted);   // else the race was lost and the test would prove nothing
        vm.SelectedResult = other;
        await delete.WaitAsync(Timeout);
        await vm.ResultLoaded;

        Assert.Equal([other.Id], vm.Results.Select(r => r.Id));
        Assert.Same(other, vm.SelectedResult);
        Assert.NotNull(vm.ResultCloud);
    }

    private sealed class FakeSource(int? profilesBeforeFault, Exception? connectError = null,
                                    Task? connectGate = null, Exception? disposeError = null) : IProfileSource
    {
        public string Description => "Fake";
        public int PointsPerProfile => 2;
        public bool HasReflectance => false;
        public IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>();
        public bool Disposed { get; private set; }

        public async Task ConnectAsync(CancellationToken ct)
        {
            if (connectGate is not null) await connectGate;
            if (connectError is not null) throw connectError;
        }

        public async IAsyncEnumerable<Profile> StreamAsync([EnumeratorCancellation] CancellationToken ct)
        {
            if (profilesBeforeFault is not { } n)
            {
                await Task.Delay(System.Threading.Timeout.Infinite, ct);
                yield break;
            }
            for (int i = 0; i < n; i++)
                yield return new Profile(i, i * 1000L, [0f, 1f], [0f, 1f], null);
            await Task.Yield();
            throw new IOException("device went away");
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return disposeError is null ? ValueTask.CompletedTask : ValueTask.FromException(disposeError);
        }
    }
}
