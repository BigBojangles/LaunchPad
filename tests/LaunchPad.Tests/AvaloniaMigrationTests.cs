using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LaunchPad;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using LaunchPad.Views;
using LaunchPad.Models;
using Xunit;

namespace LaunchPad.Tests;

public sealed class AvaloniaMigrationTests
{
    [Fact]
    public async Task MigratedViewsRenderAndDialogsPreserveSavedProjectChoices()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<App>().UseSkia()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                    .SetupWithoutStarting();
                VerifyViews();
                finished.SetResult();
            }
            catch (Exception error) { finished.SetException(error); }
        })
        { IsBackground = true, Name = "LaunchPad Avalonia migration verification" };
        if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static void VerifyViews()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestResults", "migration",
            "ui-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff")));
        Directory.CreateDirectory(root);
        var paths = new AppPaths(userProfile: root, grokHome: Path.Combine(root, "grok"),
            grokBin: Path.Combine(root, "grok", "bin"), appDataDir: Path.Combine(root, "settings"));
        var log = new SetupLog(paths);
        var settings = new SettingsStore(paths);
        var catalog = new ProjectCatalog(paths, settings);
        var locator = new GrokLocator(paths);
        var resources = new FixtureResources();
        var runtime = new FixtureRuntime();
        var desktop = new FixtureDesktop();
        var services = new AppServices(paths, settings, catalog, locator, new GrokSetup(paths, locator, log),
            new ProjectLauncher(locator, log), new ShortcutService(paths, log), log,
            runtime: runtime, resources: resources, desktop: desktop);
        var project = Path.Combine(paths.ProjectsRoot, "Migration fixture");
        Directory.CreateDirectory(project);
        settings.RememberProject("Migration fixture", project);
        var owner = new MainWindow(services, runStartup: false);
        try
        {
            owner.ShowHome();
            owner.Show();
            Pump();
            var projects = owner.FindControl<ContentControl>("ProjectsHost")!.Content as ExistingProjectsView;
            Assert.NotNull(projects);
            Assert.Single(projects.FindControl<ItemsControl>("ProjectList")!.Items);
            Capture(owner, Path.Combine(root, "home-1080x720.png"));
            var bubble = projects.GetVisualDescendants().OfType<Button>().Single(b => b.ContextMenu is not null);
            bubble.ContextMenu!.DataContext = bubble.DataContext;
            var openFolder = bubble.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Open folder"));
            openFolder.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Pump();
            Assert.Equal(project, desktop.OpenedFolder);
            Assert.Equal(5, bubble.ContextMenu.Items.OfType<MenuItem>().Count(item => !Equals(item.Header, "More…")));
            Assert.Contains(bubble.ContextMenu.Items.OfType<MenuItem>(), item => Equals(item.Header, "More…"));
            var moreActions = bubble.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "More…"));
            Assert.Equal(new[] { "Memory", "Rename project", "Reset to folder name", "Open unfenced" }, moreActions.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()));
            var nameEditor = projects.GetVisualDescendants().OfType<EditableDisplayName>().Single();
            Click(nameEditor.FindControl<Button>("CaptionButton")!);
            Assert.True(nameEditor.IsEditing);
            nameEditor.FindControl<TextBox>("Editor")!.Text = "Cancelled label";
            nameEditor.FindControl<TextBox>("Editor")!.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Assert.False(nameEditor.IsEditing);
            Assert.Equal("Migration fixture", settings.DisplayNameFor(project));
            Click(nameEditor.FindControl<Button>("CaptionButton")!);
            nameEditor.FindControl<TextBox>("Editor")!.Text = "Display name only";
            nameEditor.FindControl<TextBox>("Editor")!.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Pump();
            Assert.Equal("Display name only", new SettingsStore(paths).DisplayNameFor(project));
            Assert.True(Directory.Exists(project));
            nameEditor = projects.GetVisualDescendants().OfType<EditableDisplayName>().Single();
            Click(nameEditor.FindControl<Button>("CaptionButton")!);
            Click(nameEditor.FindControl<Button>("ResetButton")!);
            Pump();
            Assert.Equal("Migration fixture", settings.DisplayNameFor(project));
            var preferences = new SettingsWindow(settings, resources);
            var preferencesResult = preferences.ShowDialog<bool>(owner);
            preferences.FindControl<CheckBox>("TipsSwitch")!.IsChecked = false;
            preferences.FindControl<ComboBox>("DefaultAgentBox")!.SelectedIndex = 2;
            Capture(preferences, Path.Combine(root, "settings.png"));
            Click(preferences.FindControl<Button>("SaveButton")!);
            Pump();
            Assert.True(preferencesResult.GetAwaiter().GetResult());
            Assert.False(new SettingsStore(paths).Current.ShowTips);
            Assert.Equal(AgentChoice.Claude, new SettingsStore(paths).Current.DefaultAgent);
            Assert.False(projects.FindControl<TextBlock>("TipText")!.IsVisible);
            owner.Width = 620;
            owner.Height = 400;
            Capture(owner, Path.Combine(root, "home-620x400.png"));
            owner.Width = 1080;
            owner.Height = 720;

            var agent = new AgentWindow(settings, project, "Migration fixture");
            var agentResult = agent.ShowDialog<bool>(owner);
            var choices = agent.FindControl<ComboBox>("AgentBox")!;
            Assert.Equal(new[] { "Grok", "Codex CLI", "Claude Code", "Custom" }, choices.Items.Cast<string>());
            choices.SelectedIndex = 3;
            Pump();
            Assert.True(agent.FindControl<StackPanel>("CustomPanel")!.IsVisible);
            choices.SelectedIndex = 1;
            Pump();
            Assert.False(agent.FindControl<StackPanel>("CustomPanel")!.IsVisible);
            Capture(agent, Path.Combine(root, "agent-choice.png"));
            Click(agent.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Save")));
            Pump();
            Assert.True(agentResult.GetAwaiter().GetResult());
            Assert.Equal(AgentChoice.Codex, new SettingsStore(paths).AgentFor(project).Id);

            var memory = new MachineWindow(settings, project, "Migration fixture", resources);
            var memoryResult = memory.ShowDialog<bool>(owner);
            Assert.Equal(new[] { "2 GB", "3 GB", "4 GB", "5 GB", "6 GB" }, memory.FindControl<ComboBox>("MemoryBox")!.Items.Cast<string>());
            memory.FindControl<ComboBox>("MemoryBox")!.SelectedIndex = 0;
            Click(memory.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Save")));
            Pump();
            Assert.True(memoryResult.GetAwaiter().GetResult());
            Assert.Equal(2048, new SettingsStore(paths).ProjectMemoryMbFor(project));

            var machine = new MachineWindow(settings, resources: resources);
            var machineResult = machine.ShowDialog<bool>(owner);
            Assert.Equal(new[] { "1 core", "2 cores", "3 cores" }, machine.FindControl<ComboBox>("CoresBox")!.Items.Cast<string>());
            machine.Close(false);
            Pump();
            Assert.False(machineResult.GetAwaiter().GetResult());

            var create = new NewProjectDialog(paths.ProjectsRoot);
            var createResult = create.ShowDialog<bool>(owner);
            create.FindControl<TextBox>("NameBox")!.Text = "New fixture";
            Pump();
            Assert.Contains("New fixture", create.FindControl<TextBlock>("PathPreview")!.Text);
            Click(create.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Create")));
            Pump();
            Assert.True(createResult.GetAwaiter().GetResult());
            Assert.Equal("New fixture", create.ProjectName);

            var board = new SessionBoard();
            board.Show(new[] { new LiveTile(project, "Migration fixture", 0, true), new LiveTile(project, "Migration fixture", 0, false) });
            board.Select(board.AllSessions[1]);
            board.Show(new[] { new LiveTile(project, "Renamed fixture", 0, true), new LiveTile(project, "Renamed fixture", 0, false) });
            Assert.True(board.AllSessions.Single(s => !s.IsVm).IsSelected);
            Assert.True(board.Groups.Single().ShowFrame);
            Assert.Equal(2, board.Groups.Single().Sessions.Count);
            Assert.Equal(1, desktop.IconRequests);
            owner.FindControl<ContentControl>("Host")!.Content = new HomeView(owner) { DataContext = board };
            Capture(owner, Path.Combine(root, "grouped-session-tiles.png"));
            var vmId = "vm:" + QemuLayout.ProjectKey(project);
            var vm = new SessionRecord(vmId, project, AgentChoice.Claude, SessionKind.VirtualMachine, 123, 456, null, SessionLifecycle.Busy);
            var tiles = new[] { new LiveTile(project, "Migration fixture", 0, true, vm), new LiveTile(project, "Migration fixture", 0, false) };
            board.Show(tiles);
            Pump();
            var originalItem = board.AllSessions.Single(item => item.IsVm);
            var mark = owner.GetVisualDescendants().OfType<SessionMark>().Single(item => ReferenceEquals(item.DataContext, originalItem));
            var identityPaint = originalItem.IdentityBrush;
            SessionItem? activated = null;
            board.ActivateSession = item => activated = item;
            mark.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            Assert.Same(originalItem, activated);
            Assert.True(originalItem.IsBusy);
            Assert.Equal(StatusColors.Green, originalItem.StatusBrush!.Color);
            Assert.Equal("Working", mark.FindControl<TextBlock>("StatusLabel")!.Text);
            var caption = mark.FindControl<EditableDisplayName>("Caption")!;
            Click(caption.FindControl<Button>("CaptionButton")!);
            Assert.True(caption.IsEditing);
            activated = null;
            board.Show(new[] { tiles[0] with { Session = vm with { State = SessionLifecycle.NeedsAnswer }, DisplayName = "Updated label" }, tiles[1] });
            Pump();
            Assert.Same(originalItem, board.AllSessions.Single(item => item.IsVm));
            Assert.True(caption.IsEditing);
            Assert.Same(identityPaint, originalItem.IdentityBrush);
            Assert.Equal(StatusColors.Yellow, originalItem.StatusBrush!.Color);
            Assert.False(originalItem.IsBusy);
            Assert.Equal("Needs answer", mark.FindControl<TextBlock>("StatusLabel")!.Text);
            Assert.Contains("Updated label", originalItem.ToolTip);
            caption.FindControl<TextBox>("Editor")!.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Assert.Null(activated);
            board.Show(new[] { tiles[0] with { Session = vm with { State = SessionLifecycle.Unknown, Error = "Disconnected" } }, tiles[1] });
            Assert.Contains("Disconnected", originalItem.ToolTip);
            Assert.NotEqual(StatusColors.Green, originalItem.StatusBrush!.Color);
            Capture(owner, Path.Combine(root, "session-status-lamps.png"));
            var crowded = new SessionBoard();
            crowded.Show(Enumerable.Range(0, 24).Select(index => new LiveTile(Path.Combine(root, "crowded-" + index), "Long session label " + index, index, true)).ToArray());
            var edge = new EdgeBarWindow { DataContext = crowded };
            edge.ShowPinned();
            Pump();
            var scroll = edge.FindControl<ScrollViewer>("SessionScroll")!;
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            scroll.Offset = new Vector(0, scroll.Extent.Height);
            Pump();
            Assert.True(scroll.Offset.Y > 0);
            Capture(edge, Path.Combine(root, "edge-many-sessions.png"));
            edge.Close();
            var retryCount = 0;
            var folderCount = 0;
            var retryOk = false;
            var settingsRecovery = new SettingsRecoveryWindow(new IOException("Owned malformed settings fixture"),
                () => { retryCount++; return retryOk; }, () => folderCount++);
            settingsRecovery.Show();
            Pump();
            Click(settingsRecovery.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RetryButton"));
            Assert.Equal(1, retryCount);
            Assert.True(settingsRecovery.IsVisible);
            Click(settingsRecovery.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "OpenSettingsButton"));
            Assert.Equal(1, folderCount);
            Capture(settingsRecovery, Path.Combine(root, "settings-recovery.png"));
            retryOk = true;
            Click(settingsRecovery.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RetryButton"));
            Assert.False(settingsRecovery.IsVisible);

            var fence = new FenceDialog(services, project);
            var dialogs = new Window[]
            {
                new BackupPrompt(null, false), new WelcomeDialog(), new OnboardingChoiceDialog(),
                new OnboardingWizard(paths.ProjectsRoot, (_, _) => OnboardingProjectStatus.Ok(project)),
                new LargeCopyWindow(project, 1024, Array.Empty<TopRow>()), new ShareDialog(),
                new LaunchAccountWindow(() => false),
                new HypervisorWindow(HypervisorCheck.Decide(new HypervisorState(false, false, true, "fixture"), false), () => false),
                fence, new EdgeBarWindow { DataContext = board }
            };
            foreach (var dialog in dialogs)
            {
                var result = dialog.ShowDialog(owner);
                Pump();
                Assert.NotNull(dialog.Content);
                if (ReferenceEquals(dialog, fence))
                {
                    Assert.True(fence.FindControl<Button>("StartButton")!.IsEnabled);
                    Assert.Equal("fixture availability note", fence.FindControl<TextBlock>("StatusText")!.Text);
                    Click(fence.FindControl<Button>("StartButton")!);
                    Pump();
                    Assert.Equal(project, runtime.Session.StartedProject);
                    Assert.Contains("fixture session warning", fence.ReportedWarnings);
                }
                dialog.Close();
                Pump();
                Assert.True(result.IsCompleted);
            }
            var receivedDirectory = Path.Combine(root, "return-complete");
            var partialDirectory = Path.Combine(root, "return-partial");
            Directory.CreateDirectory(Path.Combine(receivedDirectory, "payload"));
            Directory.CreateDirectory(Path.Combine(partialDirectory, "payload"));
            runtime.Session.Recovery = new ProjectRecovery(root, true, "The saved VM needs recovery.", new[]
            {
                new SavedReturn(partialDirectory, DateTime.UtcNow, 0, false, "Incomplete transfer."),
                new SavedReturn(receivedDirectory, DateTime.UtcNow.AddMinutes(-1), 1, true, "Complete transfer.")
            }, CanResume: true);
            runtime.Session.RetryError = "The file scanner is unavailable.";
            var recoveryDialog = new FenceDialog(services, project);
            var recoveryDialogResult = recoveryDialog.ShowDialog(owner);
            Pump();
            Assert.False(recoveryDialog.FindControl<Button>("StartButton")!.IsEnabled);
            Assert.True(recoveryDialog.FindControl<Button>("ResumeSavedButton")!.IsEnabled);
            Click(recoveryDialog.FindControl<Button>("ResumeSavedButton")!);
            Pump();
            Assert.Equal(project, runtime.Session.ResumedProject);
            Assert.Contains("No Windows project files", recoveryDialog.ReportedStatus);
            Assert.True(recoveryDialog.FindControl<Button>("CopyBackButton")!.IsEnabled);
            Click(recoveryDialog.FindControl<Button>("OpenReturnButton")!);
            Assert.Equal(Path.Combine(receivedDirectory, "payload"), desktop.OpenedFolder);
            Click(recoveryDialog.FindControl<Button>("CopyBackButton")!);
            Pump();
            Assert.Equal(receivedDirectory, runtime.Session.CopiedRecovery);
            Assert.Contains("scanner is unavailable", recoveryDialog.ReportedStatus);
            var recoveryChoices = recoveryDialog.FindControl<ComboBox>("RecoveryBox")!;
            recoveryChoices.SelectedIndex = 0;
            Pump();
            Assert.False(recoveryDialog.FindControl<Button>("CopyBackButton")!.IsEnabled);
            Assert.True(recoveryDialog.FindControl<Button>("OpenReturnButton")!.IsEnabled);
            runtime.Session.RetryError = null;
            recoveryChoices.SelectedIndex = 1;
            Click(recoveryDialog.FindControl<Button>("CopyBackButton")!);
            Pump();
            Assert.False(recoveryDialog.FindControl<Button>("CopyBackButton")!.IsEnabled);
            Assert.False(recoveryDialog.FindControl<Button>("StartButton")!.IsEnabled);
            Capture(recoveryDialog, Path.Combine(root, "saved-return-recovery.png"));
            recoveryDialog.Close();
            Pump();
            Assert.True(recoveryDialogResult.IsCompleted);

            runtime.Availability = new FenceStartAvailability("fixture unavailable", true);
            var blocked = new FenceDialog(services, project);
            Assert.False(blocked.FindControl<Button>("StartButton")!.IsEnabled);
            Assert.Equal("fixture unavailable", blocked.FindControl<TextBlock>("StatusText")!.Text);
            blocked.Close();
            Assert.Single(new SettingsStore(paths).KnownProjects);
            Assert.Equal(AgentChoice.Codex, new SettingsStore(paths).AgentFor(project).Id);
        }
        finally { owner.Close(); Pump(); }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private sealed class FixtureRuntime : IProjectRuntime
    {
        public FixtureSession Session { get; } = new();
        public FenceStartAvailability Availability { get; set; } = new("fixture availability note", false);
        public bool HasVirtualMachine => true;
        public string? HostAgentExecutable => null;
        public FenceStartAvailability FenceStartAvailability => Availability;
        public IFencedProjectSession CreateFencedSession() => Session;
        public bool IsFencedOpen(string project) => false;
        public bool IsHostOpen(string project) => false;
        public Task OpenFencedAsync(string project, CancellationToken cancellationToken, IProgress<string>? progress) => Task.CompletedTask;
        public Task<bool> EnsureHostAgentAsync() => Task.FromResult(true);
        public bool TryLaunchHostAgent(string project, LaunchPlacement? placement, out string error) { error = ""; return true; }
        public Task<string?> SendProjectAsync(string project, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public LaunchPad.Models.SessionRecord? DescribeFenced(string project) => null;
        public LaunchPad.Models.SessionRecord? DescribeHost(string project) => null;
    }
    private sealed class FixtureSession : IFencedProjectSession
    {
        public ProjectRecovery? Recovery;
        public string? CopiedRecovery;
        public string? RetryError;
        public string? StartedProject { get; private set; }
        public string? ResumedProject { get; private set; }
        public Task ResumeSavedAsync(string project, LaunchPlacement? placement, IProgress<string>? progress, CancellationToken cancellationToken)
        { ResumedProject = project; return Task.CompletedTask; }
        public IReadOnlyList<string> Warnings => new[] { "fixture session warning" };
        public string? LatestAside(string project) => null;
        public ScanReport? LatestScan(string project) => null;
        public ProjectRecovery ReadRecovery(string project) => Recovery ?? new(project, false, "", Array.Empty<SavedReturn>());
        public Task CopyBackToProjectAsync(string project, string? recoveryDirectory = null)
        {
            CopiedRecovery = recoveryDirectory;
            if (RetryError is not null) return Task.FromException(new InvalidOperationException(RetryError));
            if (Recovery is null) throw new InvalidOperationException("This fixture must not apply returns.");
            Recovery = Recovery with { Returns = Recovery.Returns.Select(item => item.Directory == recoveryDirectory
                ? item with { CanRetry = false, Message = "Applied." } : item).ToArray() };
            return Task.CompletedTask;
        }
        public Task StartAsync(string project, LaunchPlacement? placement, IProgress<string>? progress,
            CancellationToken cancellationToken, Action<IReadOnlyList<string>>? warningsChanged = null, bool leaveRunning = false)
        {
            StartedProject = project;
            warningsChanged?.Invoke(Warnings);
            return Task.CompletedTask;
        }
    }
    private sealed class FixtureResources : IHostResources
    {
        public int? InstalledMemoryMegabytes => 8192;
        public int LogicalProcessors => 3;
    }
    private sealed class FixtureDesktop : IDesktopIntegration
    {
        public string? OpenedFolder { get; private set; }
        public int IconRequests { get; private set; }
        public void OpenProjectFolder(string path) => OpenedFolder = path;
        public Avalonia.Media.IImage? ReadProgramIcon(string? executable) { IconRequests++; return null; }
    }
    private static void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Capture(Window window, string path)
    {
        Pump();
        using var image = window.CaptureRenderedFrame();
        Assert.NotNull(image);
        image.Save(path);
    }
}
