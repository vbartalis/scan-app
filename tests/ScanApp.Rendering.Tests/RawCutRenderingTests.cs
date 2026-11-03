using System.Numerics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace ScanApp.Rendering.Tests;

public class RawCutRenderingTests
{
    private static RawPointAttributes Attributes() => new([0, 1], [0.5f, float.NaN], [0, 2]);

    [AvaloniaFact]
    public void SetCut_WithoutContext_DoesNotThrow()
    {
        var view = new PointCloudView();
        var window = new Window { Width = 400, Height = 300, Content = view };
        window.Show();
        view.SetPoints([0, 0, 0, 100, 50, 10], [0, 1], 0, 1);
        view.SetRawAttributes(Attributes());

        view.SetCut(new RawCutUniforms(0, 1, -float.MaxValue, float.MaxValue, 0, 5, true, true),
                    new CutBox(new Vector3(0, 0, 0), new Vector3(100, 50, 10)));
        view.SetCut(null, null);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void SetRawAttributes_Null_ReturnsToPlainMode()
    {
        var view = new PointCloudView();
        view.SetRawAttributes(Attributes());
        Assert.True(view.IsRawMode);

        view.SetRawAttributes(null);

        Assert.False(view.IsRawMode);
    }
}
