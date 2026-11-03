using Avalonia;
using Avalonia.OpenGL;

namespace ScanApp.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // WGL with a desktop 3.3 context is preferred: the 3D view needs it: it draws the point cloud with OpenGL 3.3 core shaders.
    // Where that is unavailable (basic display adapter, VM, remote desktop) the app still starts and only the 3D view is lost.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .With(new Win32PlatformOptions
        {
            RenderingMode = [Win32RenderingMode.Wgl, Win32RenderingMode.AngleEgl, Win32RenderingMode.Software],
            WglProfiles = [new GlVersion(GlProfileType.OpenGL, 3, 3)],
        })
        .LogToTrace();
}
