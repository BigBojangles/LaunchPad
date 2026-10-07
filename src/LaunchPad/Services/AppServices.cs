namespace LaunchPad.Services;

public sealed class AppServices
{
    public AppServices(
        AppPaths paths,
        SettingsStore settings,
        ProjectCatalog catalog,
        GrokLocator locator,
        GrokSetup setup,
        ProjectLauncher launcher,
        ShortcutService shortcuts,
        SetupLog log,
        IProjectRuntime? runtime = null,
        IHostResources? resources = null,
        IApplicationSetup? applicationSetup = null,
        IDesktopIntegration? desktop = null)
    {
        Paths = paths;
        Settings = settings;
        Catalog = catalog;
        Locator = locator;
        Setup = applicationSetup ?? setup;
        Launcher = launcher;
        Shortcuts = shortcuts;
        Log = log;
        Runtime = runtime ?? new WindowsProjectRuntime(setup, launcher, log, paths, settings);
        Resources = resources ?? HostResources.Current;
        Desktop = desktop ?? new WindowsDesktopIntegration();
    }

    public AppPaths Paths { get; }
    public SettingsStore Settings { get; }
    public ProjectCatalog Catalog { get; }
    public GrokLocator Locator { get; }
    public IApplicationSetup Setup { get; }
    public ProjectLauncher Launcher { get; }
    public ShortcutService Shortcuts { get; }
    public SetupLog Log { get; }
    public IProjectRuntime Runtime { get; }
    public IHostResources Resources { get; }
    public IDesktopIntegration Desktop { get; }
    public NotificationService Notifications => new(Paths);

    public static AppServices CreateDefault(AppPaths? paths = null)
    {
        paths ??= new AppPaths();
        var log = new SetupLog(paths);
        var settings = new SettingsStore(paths);
        var catalog = new ProjectCatalog(paths, settings);
        var locator = new GrokLocator(paths);
        var setup = new GrokSetup(paths, locator, log);
        var launcher = new ProjectLauncher(locator, log);
        var shortcuts = new ShortcutService(paths, log);
        try { NotificationDeliveryOwner.StartIfEnabled(paths); }
        catch { /* Notification startup must never block the project launcher. */ }
        return new AppServices(paths, settings, catalog, locator, setup, launcher, shortcuts, log);
    }
}

