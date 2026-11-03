using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ScanApp.Rendering.Tests;

public class PointCloudViewInputTests
{
    private static Window Show(Control content)
    {
        var window = new Window { Width = 400, Height = 300, Content = content };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void Hit_testing_finds_a_control_with_a_background()
    {
        var border = new Border { Background = Brushes.Red };
        var window = Show(border);

        Assert.Same(border, window.InputHitTest(new Point(200, 150)));
    }

    [AvaloniaFact]
    public void Pointer_inside_the_view_hits_the_view()
    {
        var view = new PointCloudView();
        var window = Show(view);

        var hit = window.InputHitTest(new Point(200, 150));

        Assert.True(hit is Visual v && (ReferenceEquals(v, view) || view.IsVisualAncestorOf(v)),
            $"hit was {hit?.GetType().Name ?? "null"}");
    }

    [AvaloniaFact]
    public void SetPoints_WithoutRefit_KeepsTheCamera()
    {
        var view = new PointCloudView();
        view.SetPoints([0, 0, 0, 100, 50, 10], [0, 1], 0, 1);
        view.Camera.Orbit(40, 10);
        var (target, distance, yaw, pitch) = (view.Camera.Target, view.Camera.Distance, view.Camera.YawRad, view.Camera.PitchRad);

        view.SetPoints([0, 0, 0, 300, 200, 10], [0, 1], 0, 1, refit: false);

        Assert.Equal((target, distance, yaw, pitch), (view.Camera.Target, view.Camera.Distance, view.Camera.YawRad, view.Camera.PitchRad));
        view.ResetView();
        Assert.NotEqual(target, view.Camera.Target);   // the new bounds are kept for the next reset
    }
}
