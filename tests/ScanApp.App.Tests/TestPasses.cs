using ScanApp.App.ViewModels;
using ScanApp.Core.Model;
using ScanApp.Core.Simulation;
using ScanApp.Core.Storage;

namespace ScanApp.App.Tests;

/// <summary>Simulated passes for the view tests, written into the view model's open scan.</summary>
internal static class TestPasses
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static double Height(double x, double y) =>
        2 * Math.Sin(y / 3) + 0.8 * Math.Sin(x / 5 + y / 1.7) + 0.5 * Math.Sin(x / 11 - y / 2.3);

    /// <summary>A second of dead time, 3 s of steady push, then still: auto trim about 1000..3999 of 5500. No reflectance.</summary>
    public static PassMetadata CommitMoving(WorkbenchViewModel vm) =>
        Commit(vm, new SimulationOptions(Height, PointsPerProfile: 64, ProfileRateHz: 1000,
                                         SpeedMmPerSAt: t => t < 4 ? 20 : 0, DeadTimeStartS: 1.0, NoiseMm: 0.002), 64, false);

    /// <summary>Like <see cref="CommitMoving"/>, with reflectance and two dim blobs 11 mm above the surface.</summary>
    public static PassMetadata CommitReflective(WorkbenchViewModel vm) =>
        Commit(vm, new SimulationOptions(Height, PointsPerProfile: 200, ProfileRateHz: 1000,
                                         SpeedMmPerSAt: t => t < 4 ? 20 : 0, DeadTimeStartS: 1.0, NoiseMm: 0.002, WithReflectance: true,
                                         Spikes: [new SpikeBlob(-40, 15, 2, 11), new SpikeBlob(30, 40, 2, 11)]), 200, true);

    private static PassMetadata Commit(WorkbenchViewModel vm, SimulationOptions options, int points, bool reflectance)
    {
        using var pending = vm.Scan!.BeginPass(PassDirection.CameraFront, points, reflectance, "test", new Dictionary<string, string>());
        foreach (var p in SimulatedProfileSource.Generate(options, 5500)) pending.Writer.Write(p);
        var pass = pending.Commit(PassStatus.Complete);
        vm.Passes.Add(pass);
        return pass;
    }

    /// <summary>A view model with a new scan in <paramref name="root"/>.</summary>
    public static WorkbenchViewModel WithScan(string root)
    {
        var vm = new WorkbenchViewModel(_ => new SimulatedProfileSource(new SimulationOptions((_, _) => 1)), Path.Combine(root, "data"));
        vm.NewScanCommand.Execute("scan");
        return vm;
    }

    /// <summary>Selects <paramref name="pass"/> and switches to its raw view, waiting for the preview and the raw geometry.</summary>
    public static async Task ShowRaw(WorkbenchViewModel vm, PassMetadata pass)
    {
        vm.SelectedPass = pass;
        await vm.PreviewLoaded.WaitAsync(Timeout);
        vm.ShowRaw = true;
        await vm.PreviewLoaded.WaitAsync(Timeout);
        await vm.RawGeometryLoaded.WaitAsync(Timeout);
    }
}
