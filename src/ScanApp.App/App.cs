using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using ScanApp.Acquisition.Sick;
using ScanApp.App.ViewModels;
using ScanApp.App.Views;
using ScanApp.Core.Acquisition;
using ScanApp.Core.Simulation;

namespace ScanApp.App;

/// <summary>Code-only Avalonia application: Fluent theme following the system light/dark setting.</summary>
public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Default;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new WorkbenchWindow(new WorkbenchViewModel(CreateSource, DataFolder.Locate(AppContext.BaseDirectory)));
        base.OnFrameworkInitializationCompleted();
    }

    private static IProfileSource CreateSource(SourceKind kind) => kind switch
    {
        SourceKind.Simulated => new SimulatedProfileSource(SimulatedProfileSource.DemoPlate()),
        SourceKind.Ruler3000 => new SickProfileSource(new SickOptions()),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
