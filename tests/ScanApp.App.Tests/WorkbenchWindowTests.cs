using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScanApp.App.ViewModels;
using ScanApp.App.Views;
using ScanApp.Core.Cleanup;
using ScanApp.Core.Combine;
using ScanApp.Core.Simulation;

namespace ScanApp.App.Tests;

public class WorkbenchWindowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"window-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static MotionAnalysis CappedAnalysis(bool capped) =>
        new([], [], 0, 0, capped, new ProfileRange(0, 1), true, [], []);

    private static TextBlock FindText(Window window, string startsWith) =>
        window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text?.StartsWith(startsWith) == true);

    [AvaloniaFact]
    public void OpeningAResult_FramesItsCloud_AndAColourSwitchKeepsTheCamera()
    {
        var vm = new WorkbenchViewModel(_ => new SimulatedProfileSource(new SimulationOptions((_, _) => 1)), _root);
        var window = new WorkbenchWindow(vm);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var view = window.GetVisualDescendants().OfType<ScanApp.Rendering.PointCloudView>().Single();

        var result = new CombineResult(1, DateTimeOffset.Now, 1,
            [new PassOutcome(1, new PassPose(0, 0, 0), new SpeedProfile([0, 1], [0, 20]), 20, 0.1, 0.1)], false, false);
        vm.Results.Add(result);
        vm.SelectedResult = result;            // the cloud is still loading
        Dispatcher.UIThread.RunJobs();
        vm.ResultCloud = new MergedCloud([100f, 300f], [50f, 250f], [0f, 20f], [0, 0]);
        Dispatcher.UIThread.RunJobs();

        float z = vm.FlipHeight ? -10f : 10f;
        Assert.Equal(new System.Numerics.Vector3(200f, 150f, z), view.Camera.Target);
        view.Camera.Orbit(40, 10);
        var (target, yaw) = (view.Camera.Target, view.Camera.YawRad);
        vm.ColorByPass = !vm.ColorByPass;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((target, yaw), (view.Camera.Target, view.Camera.YawRad));
        window.Close();
    }

    [AvaloniaFact]
    public void TrimHint_FollowsTrimNeedsCheck_AfterSwitchingToAResultAndBack()
    {
        var vm = new WorkbenchViewModel(_ => new SimulatedProfileSource(new SimulationOptions((_, _) => 1)), _root);
        var window = new WorkbenchWindow(vm);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var hint = FindText(window, "No still reference");
        Assert.False(hint.IsVisible);

        var result = new CombineResult(1, DateTimeOffset.Now, 1,
            [new PassOutcome(1, new PassPose(0, 0, 0), new SpeedProfile([0, 1], [0, 20]), 20, 0.1, 0.1)], false, false);
        vm.Results.Add(result);
        vm.SelectedResult = result;
        Dispatcher.UIThread.RunJobs();
        vm.SelectedResult = null;
        vm.ViewMode = MainViewMode.ThreeD;
        vm.ViewMode = MainViewMode.HeightMap;
        Dispatcher.UIThread.RunJobs();

        Assert.False(hint.IsVisible);
        vm.Analysis = CappedAnalysis(true);
        Assert.True(hint.IsVisible);
        vm.Analysis = CappedAnalysis(false);
        Assert.False(hint.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void ResultView_ShowsOneLegendLinePerPass_AndHidesThePassTools()
    {
        var vm = new WorkbenchViewModel(_ => new SimulatedProfileSource(new SimulationOptions((_, _) => 1)), _root);
        var window = new WorkbenchWindow(vm);
        window.Show();
        var result = new CombineResult(1, DateTimeOffset.Now, 1,
        [
            new PassOutcome(1, new PassPose(0, 0, 0), new SpeedProfile([0, 1], [0, 20]), 34.6, 0.25, 0.09),
            new PassOutcome(4, new PassPose(0, 0, 0), new SpeedProfile([0, 1], [0, 20]), 25.9, 0.28, 0.12),
        ], false, false);
        vm.Results.Add(result);

        vm.SelectedResult = result;
        Dispatcher.UIThread.RunJobs();

        Assert.True(FindText(window, "pass 1 (reference)").IsEffectivelyVisible);
        Assert.True(FindText(window, "pass 4: 25.9 mm/s").IsEffectivelyVisible);
        Assert.False(FindText(window, "Assumed speed").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Panel_VisibleOnlyInRawPassView()
    {
        var vm = TestPasses.WithScan(_root);
        var pass = TestPasses.CommitMoving(vm);
        var window = new WorkbenchWindow(vm);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var panel = window.GetVisualDescendants().OfType<LimitsPanel>().Single();
        var reflections = window.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "Show removed reflections"));
        Assert.False(panel.IsVisible);

        vm.SelectedPass = pass;
        await vm.PreviewLoaded.WaitAsync(TestPasses.Timeout);
        Dispatcher.UIThread.RunJobs();
        Assert.False(panel.IsVisible);   // the cleaned view
        Assert.False(reflections.IsEffectivelyVisible);

        vm.ShowRaw = true;
        await vm.PreviewLoaded.WaitAsync(TestPasses.Timeout);
        await vm.RawGeometryLoaded.WaitAsync(TestPasses.Timeout);
        Dispatcher.UIThread.RunJobs();
        Assert.True(panel.IsEffectivelyVisible);
        Assert.True(reflections.IsEffectivelyVisible);
        var map = window.GetVisualDescendants().OfType<HeightMapView>().Single();
        Assert.True(map.CanDrag);   // bound to CanEditTrim
        Assert.Equal(vm.ShownLimits, map.Limits);
        Assert.Same(vm.RawGeometry, map.Geometry);

        var result = new CombineResult(1, DateTimeOffset.Now, 1,
            [new PassOutcome(1, new PassPose(0, 0, 0), new SpeedProfile([0, 1], [0, 20]), 20, 0.1, 0.1)], false, false);
        vm.Results.Add(result);
        vm.SelectedResult = result;
        Dispatcher.UIThread.RunJobs();
        Assert.False(panel.IsVisible);
        Assert.False(reflections.IsEffectivelyVisible);
        Assert.Null(map.Limits);

        vm.SelectedPass = pass;
        await vm.PreviewLoaded.WaitAsync(TestPasses.Timeout);
        await vm.RawGeometryLoaded.WaitAsync(TestPasses.Timeout);
        Dispatcher.UIThread.RunJobs();
        Assert.True(panel.IsVisible);

        vm.ShowRaw = false;
        await vm.PreviewLoaded.WaitAsync(TestPasses.Timeout);
        Dispatcher.UIThread.RunJobs();
        Assert.False(panel.IsVisible);
        Assert.False(reflections.IsEffectivelyVisible);
        Assert.Null(map.Limits);
        await vm.RawGeometryLoaded.WaitAsync(TestPasses.Timeout);
        window.Close();
    }

    [AvaloniaFact]
    public void ShowRemovedReflections_ReachesTheMap()
    {
        var vm = new WorkbenchViewModel(_ => new SimulatedProfileSource(new SimulationOptions((_, _) => 1)), _root);
        var window = new WorkbenchWindow(vm);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var map = window.GetVisualDescendants().OfType<HeightMapView>().Single();
        var box = window.GetVisualDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "Show removed reflections"));
        Assert.False(map.ShowReflections);

        box.IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.ShowRemovedReflections);
        Assert.True(map.ShowReflections);
        vm.ShowRemovedReflections = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(map.ShowReflections);
        Assert.False(box.IsChecked);
        window.Close();
    }
}
