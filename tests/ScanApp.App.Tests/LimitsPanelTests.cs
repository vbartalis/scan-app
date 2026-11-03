using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ScanApp.App.ViewModels;
using ScanApp.App.Views;
using ScanApp.Core.Model;

namespace ScanApp.App.Tests;

public sealed class LimitsPanelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"limits-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private async Task<(WorkbenchViewModel vm, PassMetadata pass, LimitsPanel panel, Window window)> InRawView(
        Func<WorkbenchViewModel, PassMetadata>? commit = null, string folder = "a")
    {
        var vm = TestPasses.WithScan(Path.Combine(_root, folder));
        var pass = (commit ?? TestPasses.CommitMoving)(vm);
        await TestPasses.ShowRaw(vm, pass);
        var (panel, window) = Host(vm);
        return (vm, pass, panel, window);
    }

    private static (LimitsPanel panel, Window window) Host(WorkbenchViewModel vm)
    {
        var panel = new LimitsPanel(vm);
        var window = new Window { Width = 600, Height = 500, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (panel, window);
    }

    private static async Task Close(WorkbenchViewModel vm, Window window)
    {
        window.Close();
        await vm.RawGeometryLoaded.WaitAsync(TestPasses.Timeout);
    }

    private static Point At(Control control, Window window, double fraction) =>
        control.TranslatePoint(new Point(control.Bounds.Width * fraction, control.Bounds.Height / 2), window)!.Value;

    [AvaloniaFact]
    public async Task Slider_Drag_PreviewsThenCommitsOnce()
    {
        var (vm, _, panel, window) = await InRawView();
        var slider = panel.SliderOf(LimitKind.XMin);
        int zoneChanges = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(WorkbenchViewModel.Zone)) zoneChanges++; };

        window.MouseDown(At(slider, window, 0.3), MouseButton.Left);
        window.MouseMove(At(slider, window, 0.4));
        Dispatcher.UIThread.RunJobs();

        double live = vm.ShownLimits!.XMin;
        Assert.True(double.IsFinite(live), $"live x min {live}");
        Assert.Null(vm.Zone.XMin);
        Assert.Equal(0, zoneChanges);

        window.MouseMove(At(slider, window, 0.45));
        window.MouseUp(At(slider, window, 0.45), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, zoneChanges);
        Assert.NotNull(vm.Zone.XMin);
        Assert.Equal(vm.Zone.XMin!.Value, vm.ShownLimits!.XMin);
        Assert.True(vm.Zone.XMin > live, $"{vm.Zone.XMin} after {live}");
        await Close(vm, window);
    }

    [AvaloniaFact]
    public async Task Box_Enter_Commits()
    {
        var (vm, _, panel, window) = await InRawView();
        var box = panel.BoxOf(LimitKind.XMax);

        box.Focus();
        box.Text = "12";
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(12, vm.Zone.XMax);
        Assert.Equal(12, vm.ShownLimits!.XMax);
        await Close(vm, window);
    }

    [AvaloniaFact]
    public async Task Wheel_NudgesByStep()
    {
        var (vm, _, panel, window) = await InRawView();
        var x = vm.ScaleOf(LimitKind.XMin)!;

        window.MouseWheel(At(panel.SliderOf(LimitKind.XMin), window, 0.5), new Vector(0, 1));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(x.Min + 1, vm.Zone.XMin!.Value, 6);
        window.MouseWheel(At(panel.BoxOf(LimitKind.XMin), window, 0.5), new Vector(0, 1));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(x.Min + 2, vm.Zone.XMin!.Value, 6);

        Assert.True(vm.CommitLimit(LimitKind.TrimStart, 1.5));
        Dispatcher.UIThread.RunJobs();
        double before = vm.LimitValue(LimitKind.TrimStart)!.Value;
        window.MouseWheel(At(panel.BoxOf(LimitKind.TrimStart), window, 0.5), new Vector(0, -1));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(before - 0.05, vm.LimitValue(LimitKind.TrimStart)!.Value, 3);
        await Close(vm, window);
    }

    [AvaloniaFact]
    public async Task Wheel_OnAutoTrim_NudgesFromTheAutoTrim()
    {
        var (vm, _, panel, window) = await InRawView();
        var times = vm.Analysis!.TimeS;
        Assert.Null(vm.LimitValue(LimitKind.TrimStart));
        Assert.Null(vm.LimitValue(LimitKind.TrimEnd));
        double autoStart = times[vm.ShownLimits!.TrimStart], autoEnd = times[vm.ShownLimits.TrimEnd];

        window.MouseWheel(At(panel.SliderOf(LimitKind.TrimStart), window, 0.5), new Vector(0, 1));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(autoStart + 0.05, vm.LimitValue(LimitKind.TrimStart)!.Value, 3);

        window.MouseWheel(At(panel.BoxOf(LimitKind.TrimEnd), window, 0.5), new Vector(0, -1));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(autoEnd - 0.05, vm.LimitValue(LimitKind.TrimEnd)!.Value, 3);
        await Close(vm, window);
    }

    [AvaloniaFact]
    public async Task Reset_TrimToAuto_ZoneToNoLimit()
    {
        var (vm, pass, panel, window) = await InRawView();
        Assert.True(vm.CommitLimit(LimitKind.TrimStart, 1.5));
        Assert.True(vm.CommitLimit(LimitKind.HeightMax, 1));
        Dispatcher.UIThread.RunJobs();
        var trimReset = panel.ResetOf(LimitKind.TrimStart);
        var heightReset = panel.ResetOf(LimitKind.HeightMax);
        Assert.Equal("auto", trimReset.Content);
        Assert.Equal("✕", heightReset.Content);
        await vm.RawGeometryLoaded.WaitAsync(TestPasses.Timeout);
        Dispatcher.UIThread.RunJobs();

        trimReset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        heightReset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.TrimIsAuto);
        Assert.Null(vm.Zone.HeightMax);
        Assert.Null(vm.Scan!.LoadEdits(pass.Id).TrimStart);
        Assert.Equal("auto", panel.BoxOf(LimitKind.TrimStart).Text);
        Assert.Equal("—", panel.BoxOf(LimitKind.HeightMax).Text);
        await Close(vm, window);
    }

    [AvaloniaFact]
    public async Task SliderAtEnd_ShowsAutoOrDash()
    {
        var (vm, _, panel, window) = await InRawView();
        Assert.Equal("auto", panel.BoxOf(LimitKind.TrimStart).Text);
        Assert.Equal("auto", panel.BoxOf(LimitKind.TrimEnd).Text);
        Assert.Equal("—", panel.BoxOf(LimitKind.XMin).Text);
        Assert.Equal("—", panel.BoxOf(LimitKind.HeightMax).Text);
        Assert.Equal(0, panel.SliderOf(LimitKind.TrimStart).Value);
        Assert.Equal(1, panel.SliderOf(LimitKind.TrimEnd).Value);
        Assert.Equal(0, panel.SliderOf(LimitKind.XMin).Value);
        Assert.Equal(1, panel.SliderOf(LimitKind.XMax).Value);

        Assert.True(vm.CommitLimit(LimitKind.XMax, 12));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(12.ToString(System.Globalization.CultureInfo.CurrentCulture), panel.BoxOf(LimitKind.XMax).Text);
        Assert.True(panel.SliderOf(LimitKind.XMax).Value < 1);

        panel.SliderOf(LimitKind.XMax).Value = 1;   // the slider moved to its own end (as by the keyboard)
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.Zone.XMax);
        Assert.Equal("—", panel.BoxOf(LimitKind.XMax).Text);
        await Close(vm, window);
    }

    [AvaloniaFact]
    public async Task HeightRows_DisabledWithHintUntilReady()
    {
        var vm = TestPasses.WithScan(_root);
        var pass = TestPasses.CommitMoving(vm);
        vm.SelectedPass = pass;
        await vm.PreviewLoaded.WaitAsync(TestPasses.Timeout);
        var gate = new TaskCompletionSource();
        vm.RawRefreshGate = gate.Task;
        var (panel, window) = Host(vm);
        vm.ShowRaw = true;
        await vm.PreviewLoaded.WaitAsync(TestPasses.Timeout);
        Dispatcher.UIThread.RunJobs();

        Assert.True(panel.IsVisible);
        Assert.False(vm.HeightLimitsReady);
        Assert.False(panel.BoxOf(LimitKind.HeightMin).IsEnabled);
        Assert.False(panel.SliderOf(LimitKind.HeightMax).IsEnabled);
        Assert.True(panel.HeightHint.IsVisible);
        Assert.Equal("Fitting the table…", panel.HeightHint.Text);
        Assert.True(panel.SliderOf(LimitKind.XMin).IsEnabled);

        gate.SetResult();
        await vm.RawGeometryLoaded.WaitAsync(TestPasses.Timeout);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.HeightLimitsReady);
        Assert.True(panel.BoxOf(LimitKind.HeightMin).IsEnabled);
        Assert.True(panel.SliderOf(LimitKind.HeightMax).IsEnabled);
        Assert.False(panel.HeightHint.IsVisible);
        await Close(vm, window);
    }

    [AvaloniaFact]
    public async Task HeightHint_SaysWhenNoTableOrTheFitFailed()
    {
        var (vm, _, panel, window) = await InRawView(v =>
        {
            v.RawTableSeam = _ => null;
            return TestPasses.CommitMoving(v);
        });
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(RawFitState.NoTable, vm.RawFit);
        Assert.True(panel.HeightHint.IsVisible);
        Assert.Equal("No table found — height limits unavailable", panel.HeightHint.Text);
        Assert.False(panel.SliderOf(LimitKind.HeightMin).IsEnabled);
        await Close(vm, window);

        var (failing, _, failingPanel, failingWindow) = await InRawView(v =>
        {
            v.RawRefreshGate = Task.FromException(new IOException("disk gone"));
            return TestPasses.CommitMoving(v);
        }, "b");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(RawFitState.Failed, failing.RawFit);
        Assert.True(failingPanel.HeightHint.IsVisible);
        Assert.Equal("Could not fit the table — edit a limit to retry", failingPanel.HeightHint.Text);
        await Close(failing, failingWindow);
    }

    [AvaloniaFact]
    public async Task FilterBox_DisabledWithoutReflectance()
    {
        var (vm, _, panel, window) = await InRawView();
        Assert.Equal("Apply reflection filter", panel.FilterBox.Content);
        Assert.False(panel.FilterBox.IsEnabled);
        Assert.True(panel.FilterBox.IsChecked);
        await Close(vm, window);

        var (reflective, pass, reflectivePanel, reflectiveWindow) = await InRawView(TestPasses.CommitReflective, "b");
        Assert.True(reflectivePanel.FilterBox.IsEnabled);
        reflectivePanel.FilterBox.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Assert.False(reflective.FilterReflections);
        Assert.False(reflective.Scan!.LoadEdits(pass.Id).ReflectionFilter);
        await Close(reflective, reflectiveWindow);
    }
}
