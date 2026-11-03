using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using ScanApp.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestApp))]

namespace ScanApp.App.Tests;

public sealed class TestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
