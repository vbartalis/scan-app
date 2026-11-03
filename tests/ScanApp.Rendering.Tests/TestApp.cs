using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using ScanApp.Rendering.Tests;

[assembly: AvaloniaTestApplication(typeof(TestApp))]

namespace ScanApp.Rendering.Tests;

public sealed class TestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
