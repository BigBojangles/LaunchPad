using LaunchPad.Models;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using LaunchPad.Views;

namespace LaunchPad;

public partial class MainWindow : Window
{
    private readonly AppServices _services;
    private CancellationTokenSource? _setupCts;
    private SetupPhase _setupPhase = SetupPhase.Checking;
    private readonly SessionBoard _board = new();
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private EdgeBarWindow? _edge;
    private HomeView? _home;
    private ExistingProjectsView? _projects;
    private bool _liveBusy;
#if DEBUG
    // Debug builds only: --tile-demo fills the tiles from your real project names so the
    // layout can be checked without starting a VM. Release builds do not have this.
    private bool _tileDemo;
#endif

    public MainWindow(AppServices services, bool runStartup = true)
    {
        _services = services;
        InitializeComponent();
        _board.RenameSession = (item, name, reset) =>
        {
            if (reset) _services.Settings.ResetDisplayName(item.ProjectPath, item.Id);
            else _services.Settings.SaveDisplayName(item.ProjectPath, name, item.Id);
        };
        _board.ActivateSession = item =>
        {
            var error = item.Session is { } session ? _services.Runtime.FocusSession(session)
                : "This session's window is not linked yet.";
            StatusText.Text = error ?? "";
        };
        _services.Settings.DisplayNamesChanged += DisplayNamesChanged;
        WireReturn();
        ProductIcons.SetHostAgentIcon(() => _services.Desktop.ReadProgramIcon(_services.Runtime.HostAgentExecutable));
        ProductIcons.ReadProgramIcon = _services.Desktop.ReadProgramIcon;
        _board.GroupForSession = id => _services.Settings.Current.SessionBoardGroups?.GetValueOrDefault(id);
        _board.SaveGroup = _services.Settings.SaveBoardGroup;
        _board.DisplayGroupName = path => _services.Settings.DisplayNameFor(path);
        _board.AgentProgram = path => _services.Settings.AgentFor(path).Program;
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty) MainWindow_StateChanged(this, EventArgs.Empty);
            else if (e.Property == OffScreenMarginProperty) UpdateWindowChrome();
        };
        UpdateWindowChrome();
        PositionChanged += (_, _) => _edge?.SyncToMain(this);
        SizeChanged += (_, _) => _edge?.SyncToMain(this);
        Activated += (_, _) => _projects?.Reload();
        Closed += MainWindow_Closed;
        _liveTimer.Tick += LiveTimer_Tick;
        if (runStartup)
            Loaded += MainWindow_Loaded;
    }

    private void WireReturn()
    {
        ProjectReturnHost.ReadSavedRemote = path => _services.Settings.BackupRemote(path);
        ProjectReturnHost.OfferBackupAsync = path => UiDialogs.OnUIAsync(() => OfferBackupAsync(path));
        ProjectReturnHost.RemindBackupAsync = remote => UiDialogs.OnUIAsync(() => UiDialogs.ShowAsync(this, BackupLine(remote)));
        ProjectReturnHost.TellAsync = text => UiDialogs.OnUIAsync(() => UiDialogs.ShowAsync(this, text));
    }

    private static string BackupLine(string remote) =>
        remote.Contains('@', StringComparison.Ordinal)
            ? "Backing up before updating the project."
            : "Backing up to " + remote + " before updating the project.";

    private async Task<string?> OfferBackupAsync(string project)
    {
        var origin = GitBackup.ReadOrigin(project);
        var dialog = new BackupPrompt(origin, settings: false);
        if (await dialog.ShowDialog<bool>(this) != true || string.IsNullOrWhiteSpace(dialog.Remote))
            return null;
        if (!GitBackup.EnsureRemote(project, dialog.Remote))
            return null;
        _services.Settings.SaveBackupRemote(project, dialog.Remote);
        return dialog.Remote;
    }

    private async void MainWindow_Loaded(object? sender, RoutedEventArgs e)
    {
        // Created here, not as a field, so this window stays Application.MainWindow.
        _edge = new EdgeBarWindow { DataContext = _board };
        _edge.SyncToMain(this);
#if DEBUG
        var args = Environment.GetCommandLineArgs();
        _tileDemo = args.Contains("--tile-demo");
        if (args.Contains("--edge-demo"))
            _edge.ShowPinned();
#endif
        _liveTimer.Start();
        LiveTimer_Tick(this, EventArgs.Empty);
        var setupTask = RunSetupAsync();
        if (FirstWarning.ShouldShow(_services.Settings.Current))
            await ShowOnboarding();

        ShowHome();
        await setupTask;
    }

    public void ShowHome()
    {
        _home ??= new HomeView(this, _services.Runtime.NativeOnly) { DataContext = _board };
        _projects ??= new ExistingProjectsView(_services, OpenProject, OpenUnfenced, ShowNewProject);
        _board.ProjectMenu = _projects.CreateProjectMenu;
        Host.Content = _home;
        ProjectsHost.Content = _projects;
        _projects.Reload();
    }

    // The project list is always on the left now. Kept so older callers still land on it.
    public void ShowExistingProjects()
    {
        ShowHome();
    }

    public async Task ShowOnboarding()
    {
        while (true)
        {
            var choice = new OnboardingChoiceDialog();
            if (await choice.ShowDialog<bool>(this) != true || choice.SelectedPath is null)
                return;

            if (choice.SelectedPath == OnboardingPath.AgentBob)
            {
                var wizard = new OnboardingWizard(NewProjectsRoot, (name, reuse) => EnsureOnboardingProject(name, reuse),
                    ConfigureAndOpenProjectAsync, _services.Runtime.NativeOnly);
                await wizard.ShowDialog(this);
                if (wizard.Completed)
                    MarkOnboardingComplete();
                _projects?.Reload();
                return;
            }

            var welcome = new WelcomeDialog(_services.Runtime.NativeOnly);
            await welcome.ShowDialog(this);
            if (welcome.DontShowAgain)
                MarkOnboardingComplete();
            return;
        }
    }

    private void MarkOnboardingComplete()
    {
        var previous = _services.Settings.Current.OnboardingCompleted;
        _services.Settings.Current.OnboardingCompleted = true;
        try { _services.Settings.SaveSettings(); } catch { _services.Settings.Current.OnboardingCompleted = previous; throw; }
    }

    public async Task ShowShare()
    {
        await new ShareDialog().ShowDialog(this);
    }

    public async Task ShowMachine()
    {
        await new MachineWindow(_services.Settings, resources: _services.Resources).ShowDialog(this);
    }

    public async Task ShowNewProject()
    {
        var dialog = new NewProjectDialog(NewProjectsRoot);
        if (await dialog.ShowDialog<bool>(this) != true || string.IsNullOrWhiteSpace(dialog.ProjectName))
            return;

        var status = EnsureOnboardingProject(dialog.ProjectName, reuseExisting: false, projectsRoot: dialog.ProjectsRoot);
        var error = status.Result == OnboardingProjectStatus.Kind.Ready ? null : status.Message;
        if (error is not null)
        {
            await UiDialogs.ShowAsync(this, error);
        }

        _projects?.Reload();
        if (error is null)
        {
            if (dialog.RememberLocation)
            {
                var previous = _services.Settings.Current.NewProjectsRoot;
                _services.Settings.Current.NewProjectsRoot = dialog.ProjectsRoot;
                try { _services.Settings.SaveSettings(); }
                catch
                {
                    _services.Settings.Current.NewProjectsRoot = previous;
                    await UiDialogs.ShowAsync(this, "The project was created, but its default location could not be saved.");
                }
            }
            await ConfigureAndOpenProjectAsync(this, dialog.ProjectPath, reuseExisting: false);
        }
    }

    private string NewProjectsRoot
    {
        get
        {
            var saved = _services.Settings.Current.NewProjectsRoot;
            try
            {
                if (!string.IsNullOrWhiteSpace(saved) && Path.IsPathFullyQualified(saved))
                {
                    var full = Path.GetFullPath(saved);
                    if (!File.Exists(full)) return full;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
            return _services.Paths.ProjectsRoot;
        }
    }

    private OnboardingProjectStatus EnsureOnboardingProject(string rawName, bool reuseExisting, string? projectsRoot = null)
    {
        if (!ProjectNames.TrySanitize(rawName, out var name, out var error))
            return OnboardingProjectStatus.Fail(error);

        projectsRoot ??= NewProjectsRoot;
        var folder = Path.Combine(projectsRoot, name);
        try
        {
            if (Directory.Exists(folder))
            {
                if (!reuseExisting)
                    return OnboardingProjectStatus.AlreadyExists(folder,
                        $"A project named “{name}” already exists. Use it, or type a different name.");

                _services.Settings.RememberProject(name, folder);
                return OnboardingProjectStatus.Ok(folder);
            }

            Directory.CreateDirectory(projectsRoot);
            Directory.CreateDirectory(folder);
            _services.Settings.RememberProject(name, folder);
        }
        catch
        {
            return OnboardingProjectStatus.Fail("Couldn’t create that folder. Try a different name.");
        }

        return OnboardingProjectStatus.Ok(folder);
    }

    private async Task<bool> ConfigureAndOpenProjectAsync(Window owner, string path, bool reuseExisting)
    {
        if (!reuseExisting || !_services.Settings.Current.ProjectAgent.ContainsKey(Path.GetFullPath(path)))
        {
            var agent = new AgentWindow(_services.Settings, path, _services.Settings.DisplayNameFor(path), _services.Runtime.NativeOnly);
            if (await agent.ShowDialog<bool>(owner) != true) return false;
        }
        OpenProject(path);
        return true; // Launch requested; provider readiness is reported separately.
    }

    public async void OpenProject(string path)
    {
        if (!ProjectOpen.TryOpen(path, out var error))
        {
            await UiDialogs.ShowAsync(this, error);
            return;
        }

        if (_services.Runtime.IsFencedOpen(path))
        {
            _services.Settings.MarkOpened(path);
            await UiDialogs.ShowAsync(this, SealText.AlreadyRunning);
            return;
        }

        if (_services.Runtime.IsHostOpen(path))
        {
            _services.Settings.MarkOpened(path);
            return;
        }

        _services.Settings.MarkOpened(path);

        try
        {
            if (_services.Runtime.NativeOnly || _services.Settings.LaunchModeFor(path) == "native")
            {
                await LaunchWindowsGrok(path);
                return;
            }
            if (!_services.Runtime.HasVirtualMachine)
            {
                await UiDialogs.ShowAsync(this, "The fenced VM runtime is not installed or is incomplete. Install the full package, or choose Native (no sandbox) in the project Agent menu.");
                return;
            }

            await OpenFenced(path);
        }
        catch (Exception ex)
        {
            await UiDialogs.ShowAsync(this, ex.Message);
        }
    }

    private async Task OpenFenced(string path)
    {
        if (_services.Runtime.CreateFencedSession().ReadRecovery(path).RestartBlocked)
        {
            await new FenceDialog(_services, path).ShowDialog(this);
            return;
        }
        var previous = StatusText.Text;
        StatusText.Text = SealText.WarmingUp;
        var launched = false;
        try
        {
            var plan = await Task.Run(() => SendList.Collect(path, false));
            long total = 0;
            foreach (var file in plan)
                total += file.Length;

            if (CopyMath.IsLarge(total))
            {
                StatusText.Text = previous;
                var dialog = new LargeCopyWindow(path, total, SendList.Tops(plan));
                if (await dialog.ShowDialog<bool>(this) != true)
                    return;

                if (dialog.StartGit)
                    SendList.InitRepo(path);
                if (dialog.LeaveOut.Count > 0)
                    SendList.AddIgnores(path, dialog.LeaveOut);
                CopyScope.SendAll.Value = dialog.SendEverything;
            }
            else
            {
                CopyScope.SendAll.Value = false;
            }

            var progress = new Progress<string>(text =>
            {
                if (!launched && (text is SealText.WarmingUp or SealText.VmLaunching or SealText.BlastOff
                    || text == "The machine could not read its config. Starting again."))
                    StatusText.Text = text;
            });
            await _services.Runtime.OpenFencedAsync(path, CancellationToken.None, progress);
            launched = true;
            StatusText.Text = SealText.BlastOff;
        }
        finally
        {
            CopyScope.SendAll.Value = false;
            if (!launched) StatusText.Text = previous;
        }
    }

    private async Task OpenUnfenced(string path)
    {
        if (!ProjectOpen.TryOpen(path, out var error))
        {
            await UiDialogs.ShowAsync(this, error);
            return;
        }

        if (_services.Runtime.IsFencedOpen(path))
        {
            _services.Settings.MarkOpened(path);
            await UiDialogs.ShowAsync(this, SealText.AlreadyRunning);
            return;
        }

        if (_services.Runtime.IsHostOpen(path))
        {
            _services.Settings.MarkOpened(path);
            return;
        }

        _services.Settings.MarkOpened(path);

        try
        {
            await LaunchWindowsGrok(path);
        }
        catch (Exception ex)
        {
            await UiDialogs.ShowAsync(this, ex.Message);
        }
    }

    private async Task LaunchWindowsGrok(string path)
    {
        if (_services.Runtime.NativeOnly) _services.Settings.SaveLaunchMode(path, "native");
        await Task.CompletedTask;
        if (!_services.Runtime.TryLaunchHostAgent(path, GetLaunchPlacement(), out var error) && !string.IsNullOrEmpty(error))
            await UiDialogs.ShowAsync(this, error);
    }

    private LaunchPlacement? GetLaunchPlacement()
    {
        try
        {
            var scale = RenderScaling;
            var x = Position.X + (int)Math.Round(28 * scale);
            var y = Position.Y + (int)Math.Round(28 * scale);
            var columns = Math.Clamp((int)Math.Round(Bounds.Width / 8.5), 80, 140);
            var rows = Math.Clamp((int)Math.Round(Bounds.Height / 18.0), 24, 48);
            return new LaunchPlacement(x, y, columns, rows);
        }
        catch
        {
            return null;
        }
    }

    private void Window_MouseLeftButtonDown(object sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        try
        {
            BeginMoveDrag(e);
        }
        catch (InvalidOperationException)
        {
            // The button was already released.
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized
        ? WindowState.Normal : WindowState.Maximized;

    private void Resize_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState != WindowState.Normal || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || sender is not Control { Tag: string tag } || !Enum.TryParse<WindowEdge>(tag, out var edge)) return;
        e.Handled = true;
        try { BeginResizeDrag(edge, e); }
        catch (InvalidOperationException) { /* The button was already released. */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private async void Settings_Click(object? sender, RoutedEventArgs e) => await new SettingsWindow(_services.Settings, _services.Resources, _services.Runtime.NativeOnly, _services.Notifications).ShowDialog(this);

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        UpdateWindowChrome();
        _edge?.SyncToMain(this);
    }

    private void UpdateWindowChrome()
    {
        var maximized = WindowState == WindowState.Maximized;
        // Avalonia reports the actual native frame hidden beyond this monitor,
        // already converted to logical pixels. Recompute when state or DPI changes.
        Padding = maximized ? OffScreenMargin : new Thickness(0);
        ResizeHandles.IsVisible = WindowState == WindowState.Normal;
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        ToolTip.SetTip(MaximizeButton, maximized ? "Restore" : "Maximize");
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _liveTimer.Stop();
        _edge?.Close();
        _services.Settings.DisplayNamesChanged -= DisplayNamesChanged;
    }

    private void DisplayNamesChanged(string project)
    {
        _projects?.Reload();
        try
        {
            _services.Runtime.UpdateSessionTitles(project,
                _services.Settings.DisplayNameFor(project, "vm:" + QemuLayout.ProjectKey(project)),
                _services.Settings.DisplayNameFor(project, "host:" + QemuLayout.ProjectKey(project)));
        }
        catch (Exception error) { _services.Log.Write("Display name saved; window title update failed: " + error.Message); }
        LiveTimer_Tick(this, EventArgs.Empty);
    }
    public void SaveProjectDisplayName(string path, string name, bool reset)
    { if (reset) _services.Settings.ResetDisplayName(path); else _services.Settings.SaveDisplayName(path, name); }

    private async void LiveTimer_Tick(object? sender, EventArgs e)
    {
        if (_liveBusy)
            return;
        _liveBusy = true;
        try
        {
            var projects = _services.Catalog.ListProjects();
            var known = _services.Settings.KnownProjects
                .Select(p => p.Path)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();
#if DEBUG
            if (_tileDemo)
            {
                _board.Show(DemoTiles(projects, known));
                return;
            }
#endif
            var tiles = await Task.Run(() => LiveTiles(projects, known));
            _board.Show(tiles);
        }
        catch (Exception ex)
        {
            _services.Log.Write("Live tiles: " + ex.Message);
        }
        finally
        {
            _liveBusy = false;
        }
    }

    // Read-only: asks the session trackers the app already has which projects are open.
    private List<LiveTile> LiveTiles(IReadOnlyList<ProjectEntry> projects, List<string> known)
    {
        var tiles = new List<LiveTile>();
        foreach (var project in projects)
        {
            var color = ColorIndex(project.Path, known, projects);
            if (_services.Runtime.IsFencedOpen(project.Path))
                tiles.Add(new LiveTile(project.Path, project.Name, color, IsVm: true, _services.Runtime.DescribeFenced(project.Path), _services.Settings.DisplayNameFor(project.Path, "vm:" + QemuLayout.ProjectKey(project.Path))));
            var host = _services.Runtime.DescribeHost(project.Path);
            if (_services.Runtime.IsHostOpen(project.Path) || host?.State == SessionLifecycle.Failed)
                tiles.Add(new LiveTile(project.Path, project.Name, color, IsVm: false, host, _services.Settings.DisplayNameFor(project.Path, "host:" + QemuLayout.ProjectKey(project.Path))));
        }

        return tiles;
    }

    // Each project keeps its color: its place in the saved project list, in palette order.
    private static int ColorIndex(string path, List<string> known, IReadOnlyList<ProjectEntry> projects)
        => ProjectIdentity.IndexFor(path, known, projects);

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

#if DEBUG
    private static List<LiveTile> DemoTiles(IReadOnlyList<ProjectEntry> projects, List<string> known)
    {
        var tiles = new List<LiveTile>();
        for (var i = 0; i < Math.Min(projects.Count, 5); i++)
        {
            var project = projects[i];
            var color = ColorIndex(project.Path, known, projects);
            tiles.Add(new LiveTile(project.Path, project.Name, color, IsVm: i % 2 == 0));
            if (i == 0)
                tiles.Add(new LiveTile(project.Path, project.Name, color, IsVm: false));
        }

        return tiles;
    }
#endif

    private void CreditLink_RequestNavigate(object sender, RoutedEventArgs e)
    {
        ExternalLinks.Open("https://x.com/BigBojangles_");
        e.Handled = true;
    }

    private void CaseyLink_RequestNavigate(object sender, RoutedEventArgs e)
    {
        ExternalLinks.Open("https://caseynielsen.tech");
        e.Handled = true;
    }

    private void GitHubLink_RequestNavigate(object sender, RoutedEventArgs e)
    {
        ExternalLinks.Open("https://github.com/BigBojangles/LaunchPad");
        e.Handled = true;
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e) => await RunSetupAsync();

    private async Task RunSetupAsync()
    {
        _setupCts?.Cancel();
        _setupCts = new CancellationTokenSource();
        var token = _setupCts.Token;

        SetStatus(SetupPhase.Checking, "Getting things ready…");

        SetupStatus result;
        try
        {
            result = await Task.Run(() => _services.Setup.EnsureReadyAsync(token), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _services.Log.Write("Setup task failed: " + ex.Message);
            result = new SetupStatus(SetupPhase.Failed, "Couldn’t finish setup. Check your internet and try again.");
        }

        if (token.IsCancellationRequested)
            return;

        SetStatus(result.Phase, result.UserMessage);
    }

    private void SetStatus(SetupPhase phase, string message)
    {
        _setupPhase = phase;
        StatusText.Text = message;
        RetryButton.IsVisible = phase == SetupPhase.Failed;
    }
}
