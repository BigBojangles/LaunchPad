using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LaunchPad.Services;
using LaunchPad.Views;

namespace LaunchPad;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;
    protected virtual AppPaths CreatePaths() => new();
    protected virtual AppServices CreateServices(AppPaths paths) => AppServices.CreateDefault(paths);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Services?.Log.Write("Unhandled UI error: " + e.Exception);
            _ = UiDialogs.ShowAsync("Something went wrong. Try again.");
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Services?.Log.Write("Unhandled error: " + e.ExceptionObject);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            var paths = CreatePaths();
            bool TryOpen()
            {
                try
                {
                    Services = CreateServices(paths);
                    var main = new MainWindow(Services);
                    var restarting = desktop.MainWindow is SettingsRecoveryWindow;
                    desktop.MainWindow = main;
                    if (restarting) main.Show();
                    return true;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    new SetupLog(paths).Write("Saved settings startup stopped: " + error.Message);
                    if (desktop.MainWindow is SettingsRecoveryWindow existing)
                    { _ = UiDialogs.ShowAsync(existing, error.Message); return false; }
                    desktop.MainWindow = new SettingsRecoveryWindow(error, TryOpen,
                        () => new WindowsDesktopIntegration().OpenProjectFolder(paths.AppDataDir));
                    return false;
                }
            }
            TryOpen();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
