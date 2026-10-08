using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Input.Platform;
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
            // Physical snap/grip/DPI behavior remains Casey's review gate.
            var maximize = owner.FindControl<Button>("MaximizeButton")!;
            var grips = owner.FindControl<Grid>("ResizeHandles")!;
            Assert.True(grips.IsVisible);
            Click(maximize);
            Pump();
            Assert.Equal(WindowState.Maximized, owner.WindowState);
            Assert.Equal(owner.OffScreenMargin, owner.Padding);
            Assert.False(grips.IsVisible);
            Assert.Equal("Restore", ToolTip.GetTip(maximize));
            Click(maximize);
            Pump();
            Assert.Equal(WindowState.Normal, owner.WindowState);
            Assert.Equal(new Thickness(0), owner.Padding);
            Assert.True(grips.IsVisible);
            Assert.Equal("Maximize", ToolTip.GetTip(maximize));
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
            Assert.Equal(new[] { "VM memory", "Project permissions", "Windows tests", "Rename project", "Reset to folder name", "Open native (no sandbox)" }, moreActions.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()));
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
            var renamedMenu = projects.CreateProjectMenu(project);
            renamedMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Agent"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Pump();
            var renamedAgent = owner.OwnedWindows.OfType<AgentWindow>().Single();
            Assert.Equal("Display name only", renamedAgent.Title);
            renamedAgent.Close(false);
            var renamedMore = renamedMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "More…"));
            renamedMore.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "VM memory"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Pump();
            var renamedMemory = owner.OwnedWindows.OfType<MachineWindow>().Single();
            Assert.Equal("Display name only", renamedMemory.Title);
            renamedMemory.Close(false);
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
            VerifySetupRepair(owner, settings, resources, root);
            VerifyNotificationSetup(owner, services, projects, resources, project, root);

            var memory = new MachineWindow(settings, project, "Migration fixture", resources);
            var memoryResult = memory.ShowDialog<bool>(owner);
            Assert.Equal(new[] { "2 GB", "3 GB", "4 GB", "5 GB", "6 GB" }, memory.FindControl<ComboBox>("MemoryBox")!.Items.Cast<string>());
            memory.FindControl<ComboBox>("MemoryBox")!.SelectedIndex = 0;
            Click(memory.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Save")));
            Pump();
            Assert.True(memoryResult.GetAwaiter().GetResult());
            Assert.Equal(2048, new SettingsStore(paths).ProjectMemoryMbFor(project));
            VerifyMachineSaveFailures(owner, resources, project, root);

            var machine = new MachineWindow(settings, resources: resources);
            var machineResult = machine.ShowDialog<bool>(owner);
            Assert.Equal(new[] { "1 core", "2 cores", "3 cores" }, machine.FindControl<ComboBox>("CoresBox")!.Items.Cast<string>());
            machine.Close(false);
            Pump();
            Assert.False(machineResult.GetAwaiter().GetResult());

            var create = new NewProjectDialog(paths.ProjectsRoot);
            var createResult = create.ShowDialog<bool>(owner);
            create.FindControl<TextBox>("NameBox")!.Text = "New fixture";
            var chosenRoot = Path.Combine(root, "chosen-projects");
            create.FindControl<TextBox>("FolderBox")!.Text = "relative-folder";
            Click(create.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Create")));
            Assert.False(createResult.IsCompleted);
            create.FindControl<TextBox>("FolderBox")!.Text = chosenRoot;
            Pump();
            Assert.Contains("New fixture", create.FindControl<TextBlock>("PathPreview")!.Text);
            Click(create.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Create")));
            Pump();
            Assert.True(createResult.GetAwaiter().GetResult());
            Assert.Equal("New fixture", create.ProjectName);
            Assert.Equal(Path.Combine(chosenRoot, "New fixture"), create.ProjectPath);
            Assert.False(Directory.Exists(chosenRoot)); // The dialog never creates files before its caller commits.

            // Changing ordinary preferences in the lite package must not replace
            // either automatic VM defaults or allocations from another machine.
            foreach (var allocation in new[] { (Memory: 0, Cores: 0), (Memory: 32768, Cores: 16) })
            {
                settings.Current.MachineMemoryMb = allocation.Memory;
                settings.Current.MachineCores = allocation.Cores;
                settings.Current.RememberGrokSignIn = false;
                settings.SaveSettings();
                var nativePreferences = new SettingsWindow(settings, resources, nativeOnly: true);
                var nativePreferencesResult = nativePreferences.ShowDialog<bool>(owner);
                Assert.False(nativePreferences.FindControl<ComboBox>("MemoryBox")!.IsEnabled);
                Assert.False(nativePreferences.FindControl<ComboBox>("CoresBox")!.IsEnabled);
                Assert.False(nativePreferences.FindControl<CheckBox>("RememberSignInSwitch")!.IsEnabled);
                nativePreferences.FindControl<CheckBox>("RememberSignInSwitch")!.IsChecked = true;
                nativePreferences.FindControl<ComboBox>("DefaultAgentBox")!.SelectedIndex = 2;
                nativePreferences.FindControl<CheckBox>("TipsSwitch")!.IsChecked = true;
                Click(nativePreferences.FindControl<Button>("SaveButton")!);
                Pump();
                Assert.True(nativePreferencesResult.GetAwaiter().GetResult());
                var saved = new SettingsStore(paths).Current;
                Assert.Equal(allocation.Memory, saved.MachineMemoryMb);
                Assert.Equal(allocation.Cores, saved.MachineCores);
                Assert.Equal(AgentChoice.Claude, saved.DefaultAgent);
                Assert.True(saved.ShowTips);
                Assert.False(saved.RememberGrokSignIn);
            }

            var nativeHome = new HomeView(owner, nativeOnly: true);
            Assert.False(nativeHome.FindControl<Button>("MachineButton")!.IsVisible);
            Assert.False(nativeHome.FindControl<TextBlock>("MachineSeparator")!.IsVisible);

            var genericChoice = new OnboardingChoiceDialog();
            var genericChoiceResult = genericChoice.ShowDialog<bool>(owner);
            Click(genericChoice.FindControl<Button>("CodingAgentButton")!);
            Pump();
            Assert.True(genericChoiceResult.GetAwaiter().GetResult());
            Assert.Equal(OnboardingPath.CodingAgent, genericChoice.SelectedPath);

            runtime.NativeOnly = true;
            var nativeOwner = new MainWindow(services, runStartup: false);
            nativeOwner.ShowHome();
            nativeOwner.Show();
            try
            {
                settings.Current.NewProjectsRoot = "relative-default";
                settings.SaveSettings();
                var createTask = nativeOwner.ShowNewProject();
                Pump();
                var newProject = nativeOwner.OwnedWindows.OfType<NewProjectDialog>().Single();
                Assert.Equal(paths.ProjectsRoot, newProject.FindControl<TextBox>("FolderBox")!.Text);
                newProject.FindControl<TextBox>("NameBox")!.Text = "Native chosen fixture";
                newProject.FindControl<TextBox>("FolderBox")!.Text = chosenRoot;
                Click(newProject.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Create")));
                Pump();
                var firstAgent = nativeOwner.OwnedWindows.OfType<AgentWindow>().Single();
                Assert.Empty(runtime.NativeLaunches);
                Assert.Equal(2, firstAgent.FindControl<ComboBox>("AgentBox")!.SelectedIndex);
                Assert.Equal(1, firstAgent.FindControl<ComboBox>("LaunchModeBox")!.SelectedIndex);
                Assert.False(firstAgent.FindControl<ComboBox>("LaunchModeBox")!.IsEnabled);
                firstAgent.FindControl<ComboBox>("AgentBox")!.SelectedIndex = 1;
                Click(firstAgent.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Save")));
                Pump();
                Assert.True(createTask.IsCompletedSuccessfully);
                var nativeProject = Path.Combine(chosenRoot, "Native chosen fixture");
                Assert.Equal(new[] { nativeProject }, runtime.NativeLaunches);
                Assert.Equal(0, runtime.FencedLaunches);
                var persisted = new SettingsStore(paths);
                Assert.Equal(AgentChoice.Codex, persisted.AgentFor(nativeProject).Id);
                Assert.Equal("native", persisted.LaunchModeFor(nativeProject));
                Assert.Equal(chosenRoot, persisted.Current.NewProjectsRoot);
                Assert.False(Directory.Exists(Path.Combine(paths.ProjectsRoot, "Native chosen fixture")));

                var canceledLocation = Path.Combine(root, "canceled-location");
                var dismissTask = nativeOwner.ShowNewProject();
                Pump();
                newProject = nativeOwner.OwnedWindows.OfType<NewProjectDialog>().Single();
                Assert.Equal(chosenRoot, newProject.FindControl<TextBox>("FolderBox")!.Text);
                newProject.FindControl<TextBox>("NameBox")!.Text = "Never created";
                newProject.FindControl<TextBox>("FolderBox")!.Text = canceledLocation;
                Click(newProject.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Cancel")));
                Pump();
                Assert.True(dismissTask.IsCompletedSuccessfully);
                Assert.False(Directory.Exists(canceledLocation));
                Assert.Single(runtime.NativeLaunches);
                Assert.Equal(chosenRoot, new SettingsStore(paths).Current.NewProjectsRoot);

                var cancelTask = nativeOwner.ShowNewProject();
                Pump();
                newProject = nativeOwner.OwnedWindows.OfType<NewProjectDialog>().Single();
                newProject.FindControl<TextBox>("NameBox")!.Text = "Native cancel fixture";
                Click(newProject.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Create")));
                Pump();
                nativeOwner.OwnedWindows.OfType<AgentWindow>().Single().Close(false);
                Pump();
                Assert.True(cancelTask.IsCompletedSuccessfully);
                Assert.Single(runtime.NativeLaunches);
                Assert.True(Directory.Exists(Path.Combine(chosenRoot, "Native cancel fixture")));
            }
            finally { nativeOwner.Close(); runtime.NativeOnly = false; Pump(); }

            var reused = false;
            var requestCount = 0;
            var guided = new OnboardingWizard(paths.ProjectsRoot,
                (_, useExisting) => useExisting ? OnboardingProjectStatus.Ok(project) : OnboardingProjectStatus.AlreadyExists(project, "Use the saved project."),
                (_, path, useExisting) => { Assert.Equal(project, path); reused = useExisting; requestCount++; return Task.FromResult(true); }, nativeOnly: true);
            guided.ShowDialog(owner);
            Click(guided.FindControl<Button>("PrimaryButton")!);
            Assert.True(guided.FindControl<StackPanel>("StepAgentBob")!.IsVisible);
            var companion = guided.FindControl<ComboBox>("CompanionBox")!;
            var fullInstructions = AgentBobInstructions.Load();
            Assert.Equal(fullInstructions, guided.FindControl<TextBox>("InstructionsPreview")!.Text);
            foreach (var choice in CompanionChoice.All)
            {
                companion.SelectedItem = choice;
                Pump();
                Assert.Equal(choice.ChatUrl is not null, guided.FindControl<Button>("OpenCompanionButton")!.IsEnabled);
                Assert.Equal(choice.InstructionsUrl is not null, guided.FindControl<Button>("CompanionHelpButton")!.IsVisible);
                Assert.Equal(fullInstructions, guided.FindControl<TextBox>("InstructionsPreview")!.Text);
                Click(guided.FindControl<Button>("CopyInstructionsButton")!);
                Pump();
                Assert.Equal(fullInstructions, guided.Clipboard!.TryGetTextAsync().GetAwaiter().GetResult());
            }
            Assert.Equal(0, requestCount);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using (var capture = guided.CaptureRenderedFrame())
            {
                Assert.NotNull(capture);
                capture!.Save(Path.Combine(root, "companion-setup.png"));
            }
            Click(guided.FindControl<Button>("PrimaryButton")!);
            Click(guided.FindControl<Button>("PrimaryButton")!);
            Assert.True(guided.FindControl<Button>("UseExistingButton")!.IsVisible);
            Assert.Equal(0, requestCount);
            Click(guided.FindControl<Button>("UseExistingButton")!);
            Pump();
            Assert.True(reused);
            Assert.Equal(1, requestCount);
            Assert.True(guided.FindControl<StackPanel>("StepNext")!.IsVisible);
            Assert.False(guided.Completed);
            guided.Close();

            var board = new SessionBoard();
            board.Show(new[] { new LiveTile(project, "Migration fixture", 0, true), new LiveTile(project, "Migration fixture", 0, false) });
            board.Select(board.AllSessions[1]);
            board.Show(new[] { new LiveTile(project, "Renamed fixture", 0, true), new LiveTile(project, "Renamed fixture", 0, false) });
            Assert.True(board.AllSessions.Single(s => !s.IsVm).IsSelected);
            Assert.True(board.Groups.Single().ShowFrame);
            Assert.Equal(2, board.Groups.Single().Sessions.Count);
            Assert.Equal(0, desktop.IconRequests);
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
            var originalGroup = board.Groups.Single();
            var originalEditor = caption.FindControl<TextBox>("Editor")!;
            originalEditor.Text = "Unfinished rename";
            var resets = 0;
            System.Collections.Specialized.NotifyCollectionChangedEventHandler countResets = (_, change) =>
            {
                if (change.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++;
            };
            board.Groups.CollectionChanged += countResets;
            board.AllSessions.CollectionChanged += countResets;
            originalGroup.Sessions.CollectionChanged += countResets;
            var addedTile = new LiveTile(Path.Combine(root, "another-session"), "Another project", 1, true);
            var updatingTile = tiles[0] with { Session = vm with { State = SessionLifecycle.NeedsAnswer }, DisplayName = "Updated label" };
            foreach (var membership in new[] { new[] { updatingTile, tiles[1], addedTile }, new[] { updatingTile, tiles[1] } })
            {
                board.Show(membership);
                Pump();
                Assert.Same(originalGroup, board.Groups.Single(group => group.ShowFrame));
                Assert.Same(originalItem, board.AllSessions.Single(item => item.Id == vmId));
                Assert.Same(mark, owner.GetVisualDescendants().OfType<SessionMark>().Single(item => ReferenceEquals(item.DataContext, originalItem)));
                Assert.True(originalItem.IsSelected);
                Assert.True(caption.IsEditing);
                Assert.Equal("Unfinished rename", originalEditor.Text);
                Assert.True(originalEditor.IsFocused);
            }
            Assert.Equal(0, resets);
            caption.FindControl<TextBox>("Editor")!.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Assert.Null(activated);
            foreach (var state in new[] { SessionLifecycle.Idle, SessionLifecycle.Stopped, SessionLifecycle.Running })
            {
                board.Show(new[] { tiles[0] with { Session = vm with { State = state } }, tiles[1] });
                Pump();
                Assert.Same(originalItem, board.AllSessions.Single(item => item.IsVm));
                Assert.Same(identityPaint, originalItem.IdentityBrush);
                Assert.False(originalItem.IsBusy);
                Assert.Equal(state == SessionLifecycle.Running ? "Open" : state == SessionLifecycle.Idle ? "Idle" : "Stopped",
                    mark.FindControl<TextBlock>("StatusLabel")!.Text);
                if (state == SessionLifecycle.Running)
                {
                    Assert.Null(originalItem.StatusBrush);
                    Assert.False(mark.FindControl<Border>("StatusLamp")!.IsVisible);
                }
                else Assert.Equal(StatusColors.Red, originalItem.StatusBrush!.Color);
            }
            var failedEvent = AgentActivityTests.Event(Guid.NewGuid().ToString("N"), 99, AgentEventKind.RunFailed);
            var failedActivity = new AgentActivitySnapshot(AgentActivity.Idle, failedEvent.RunId, null, false, failedEvent.Sequence, failedEvent);
            board.Show(new[] { tiles[0] with { Session = vm with { State = SessionLifecycle.Idle, Activity = failedActivity,
                Error = AgentActivityTracker.OutcomeText(failedActivity) } }, tiles[1] });
            Pump();
            Assert.Equal("Run failed", mark.FindControl<TextBlock>("StatusLabel")!.Text);
            Assert.Contains("VM remains open", originalItem.ToolTip);
            Assert.Equal(StatusColors.Red, originalItem.StatusBrush!.Color);
            foreach (var diagnostic in new[] { (SessionLifecycle.Starting, "Starting"), (SessionLifecycle.Stopping, "Saving"),
                (SessionLifecycle.Unknown, "Activity unavailable") })
            {
                board.Show(new[] { tiles[0] with { Session = vm with { State = diagnostic.Item1, Error = "Disconnected" } }, tiles[1] });
                Pump();
                Assert.Same(originalItem, board.AllSessions.Single(item => item.IsVm));
                Assert.Same(identityPaint, originalItem.IdentityBrush);
                Assert.Contains("Disconnected", originalItem.ToolTip);
                Assert.Null(originalItem.StatusBrush);
                Assert.False(originalItem.HasStatus);
                Assert.False(mark.FindControl<Border>("StatusLamp")!.IsVisible);
                Assert.Equal(diagnostic.Item2, mark.FindControl<TextBlock>("StatusLabel")!.Text);
            }
            Capture(owner, Path.Combine(root, "session-status-lamps.png"));
            board.Show(new[] { tiles[1], tiles[0] });
            Pump();
            Assert.Same(originalItem, board.AllSessions[1]);
            Assert.Same(originalGroup, board.Groups.Single());
            Assert.True(originalItem.IsSelected);
            board.Show(new[] { tiles[0] });
            Pump();
            Assert.Same(originalItem, Assert.Single(board.AllSessions));
            Assert.False(board.Groups.Single().ShowFrame);
            Assert.True(originalItem.IsSelected);
            board.Show(tiles);
            Pump();
            Assert.Same(originalItem, board.AllSessions.Single(item => item.Id == vmId));
            Assert.True(board.Groups.Single().ShowFrame);
            Assert.True(originalItem.IsSelected);
            board.Show(new[] { tiles[1] });
            Pump();
            Assert.False(board.AllSessions.Single().IsSelected);
            board.Activate(originalItem);
            Assert.Null(activated);
            board.Show(tiles);
            Pump();
            Assert.Equal(0, resets);
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
            var managedRoot = WindowsTestControls.ProjectRoot(paths, project);
            var managedGeneration = Guid.NewGuid().ToString("N");
            var managedRequest = Guid.NewGuid().ToString("N");
            var managedRun = Path.Combine(managedRoot, managedGeneration, managedRequest);
            Directory.CreateDirectory(managedRun);
            using var uiOwner = System.Diagnostics.Process.GetCurrentProcess();
            File.WriteAllText(Path.Combine(managedRun, "control-status.json"), System.Text.Json.JsonSerializer.Serialize(new WindowsTestControlState(1, managedGeneration,
                managedRequest, new string('a', 64), Guid.NewGuid().ToString("N"), uiOwner.Id, uiOwner.StartTime.ToUniversalTime().Ticks,
                "awaiting-approval", "dotnet", null, ["build"], false, ".", 30, DateTimeOffset.UtcNow.AddMinutes(1), false, null, null, null, null, DateTimeOffset.UtcNow),
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
            var dialogs = new Window[]
            {
                new ManagedWindowsTestsWindow(services, project),
                new WindowsTestWindow(services, project),
                new BackupPrompt(null, false), new WelcomeDialog(), new WelcomeDialog(nativeOnly: true), new OnboardingChoiceDialog(),
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
                Assert.NotNull(dialog.Icon);
                if (dialog is ManagedWindowsTestsWindow managed)
                {
                    Assert.True(managed.FindControl<Button>("ApproveButton")!.IsEnabled);
                    Assert.False(managed.FindControl<Button>("ShowButton")!.IsEnabled);
                    Click(managed.FindControl<Button>("ApproveButton")!);
                    Assert.True(File.Exists(Path.Combine(managedRun, "decision.json")));
                    Capture(managed, Path.Combine(root, "windows-tests-permission.png"));
                }
                if (dialog is WelcomeDialog)
                {
                    var body = dialog.FindControl<TextBlock>("WarningText")!;
                    Capture(dialog, Path.Combine(root, body.Text!.Contains("no VM runtime", StringComparison.Ordinal)
                        ? "welcome-native.png" : "welcome-full.png"));
                    Assert.True(body.Bounds.Height >= body.DesiredSize.Height - 1);
                }
                if (ReferenceEquals(dialog, fence))
                {
                    Assert.True(fence.FindControl<Button>("StartButton")!.IsEnabled);
                    Assert.Equal("fixture availability note", fence.FindControl<TextBlock>("StatusText")!.Text);
                    Click(fence.FindControl<Button>("StartButton")!);
                    Pump();
                    Assert.Equal(project, runtime.Session.StartedProject);
                    Assert.Contains("fixture session warning", fence.ReportedWarnings);
                    Assert.False(fence.IsVisible);
                    Assert.True(result.IsCompleted);
                }
                dialog.Close();
                Pump();
                Assert.False(File.Exists(Path.Combine(managedRun, "cancel.json")));
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
            Assert.False(recoveryDialog.FindControl<Button>("StartButton")!.IsVisible);
            Assert.True(recoveryDialog.FindControl<Button>("ResumeSavedButton")!.IsEnabled);
            Assert.Equal("Continue project", recoveryDialog.FindControl<Button>("ResumeSavedButton")!.Content);
            Assert.False(recoveryDialog.FindControl<Button>("CopyBackButton")!.IsVisible);
            Click(recoveryDialog.FindControl<Button>("ResumeSavedButton")!);
            Pump();
            Assert.Equal(project, runtime.Session.ResumedProject);
            Assert.Contains("No Windows project files", recoveryDialog.ReportedStatus);
            Assert.False(recoveryDialog.IsVisible);
            Assert.True(recoveryDialogResult.IsCompleted);

            recoveryDialog = new FenceDialog(services, project);
            recoveryDialogResult = recoveryDialog.ShowDialog(owner);
            Pump();
            recoveryDialog.FindControl<Expander>("RecoveryMore")!.IsExpanded = true;
            recoveryDialog.FindControl<ComboBox>("RecoveryBox")!.SelectedIndex = 1;
            Pump();
            Assert.True(recoveryDialog.FindControl<Button>("CopyBackButton")!.IsEnabled);
            Assert.Equal("Save to Windows", recoveryDialog.FindControl<Button>("CopyBackButton")!.Content);
            Click(recoveryDialog.FindControl<Button>("OpenReturnButton")!);
            Assert.Equal(Path.Combine(receivedDirectory, "payload"), desktop.OpenedFolder);
            runtime.Session.ReturnPending = new TaskCompletionSource();
            var resumeCalls = runtime.Session.ResumeCalls;
            Click(recoveryDialog.FindControl<Button>("CopyBackButton")!);
            Pump();
            Assert.False(recoveryDialog.FindControl<Button>("ResumeSavedButton")!.IsEnabled);
            Assert.False(recoveryDialog.FindControl<Button>("DismissButton")!.IsEnabled);
            recoveryDialog.Close();
            Pump();
            Assert.True(recoveryDialog.IsVisible);
            Click(recoveryDialog.FindControl<Button>("ResumeSavedButton")!);
            Assert.Equal(resumeCalls, runtime.Session.ResumeCalls);
            runtime.Session.ReturnPending.SetResult();
            Pump();
            runtime.Session.ReturnPending = null;
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

            var keptRecovery = runtime.Session.Recovery;
            var dismissed = new FenceDialog(services, project);
            var dismissedResult = dismissed.ShowDialog(owner);
            Pump();
            Click(dismissed.FindControl<Button>("DismissButton")!);
            Pump();
            Assert.True(dismissedResult.IsCompleted);
            Assert.False(dismissed.IsVisible);
            Assert.Same(keptRecovery, runtime.Session.Recovery);
            Assert.True(runtime.Session.Recovery!.RestartBlocked);

            runtime.Availability = new FenceStartAvailability("fixture unavailable", true);
            var blocked = new FenceDialog(services, project);
            Assert.False(blocked.FindControl<Button>("StartButton")!.IsEnabled);
            Assert.Equal("fixture unavailable", blocked.FindControl<TextBlock>("StatusText")!.Text);
            blocked.Close();
            Assert.Equal(3, new SettingsStore(paths).KnownProjects.Count);
            Assert.Equal(AgentChoice.Codex, new SettingsStore(paths).AgentFor(project).Id);
        }
        finally { owner.Close(); Pump(); }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void VerifySetupRepair(Window owner, SettingsStore settings, IHostResources resources, string root)
    {
        var before = File.ReadAllBytes(settings.Paths.SettingsFile);
        foreach (var nativeOnly in new[] { false, true })
        {
            var repair = new FixtureSetupRepair();
            var preferences = new SettingsWindow(settings, resources, nativeOnly: nativeOnly, repair: repair);
            var preferencesResult = preferences.ShowDialog<bool>(owner);
            Pump();
            var button = preferences.FindControl<Button>("SetupRepairButton")!;
            Assert.True(button.IsEnabled);
            Assert.StartsWith(nativeOnly ? "Windows test setup" : "Repair setup", button.Content!.ToString());
            Click(button); Pump();
            var window = preferences.OwnedWindows.OfType<SetupRepairWindow>().Single();
            Assert.Equal(1, repair.Checks);
            Assert.Equal(nativeOnly ? "Windows test setup" : "Repair setup", window.Title);
            Assert.Contains("still needed", window.FindControl<TextBlock>("RepairStatus")!.Text);
            if (nativeOnly) Assert.DoesNotContain("VM launch", window.FindControl<TextBlock>("RepairStatus")!.Text);
            window.Width = 380; window.Height = 380; Pump();
            foreach (var name in new[] { "CheckSetupButton", "RepairSetupButton", "CloseRepairButton" })
            {
                var control = window.FindControl<Button>(name)!;
                var position = control.TranslatePoint(default, window)!.Value;
                Assert.InRange(position.X, 0, window.Bounds.Width - control.Bounds.Width);
                Assert.InRange(position.Y, 0, window.Bounds.Height - control.Bounds.Height);
            }
            Capture(window, Path.Combine(root, nativeOnly ? "native-test-setup.png" : "full-setup-repair.png"));
            Click(window.FindControl<Button>("RepairSetupButton")!); Pump();
            Assert.Equal(1, repair.Repairs);
            Assert.False(window.FindControl<Button>("CloseRepairButton")!.IsEnabled);
            window.Close(); Pump();
            Assert.Contains(window, preferences.OwnedWindows);
            repair.Pending.SetResult(new([new("Owned fixture", true, "Simulated only; no Windows settings changed.")], RestartRequired: true));
            Pump();
            Assert.Contains("requires a restart", window.FindControl<TextBlock>("RepairStatus")!.Text);
            Assert.DoesNotContain("passed", window.FindControl<TextBlock>("RepairStatus")!.Text);
            Assert.True(window.FindControl<Button>("CloseRepairButton")!.IsEnabled);
            Click(window.FindControl<Button>("CloseRepairButton")!); Pump();
            Assert.Empty(preferences.OwnedWindows);
            preferences.Close(false); Pump();
            Assert.False(preferencesResult.GetAwaiter().GetResult());
        }
        Assert.Equal(before, File.ReadAllBytes(settings.Paths.SettingsFile));
    }

    private sealed class FixtureSetupRepair : IWindowsSetupRepair
    {
        public int Checks;
        public int Repairs;
        public TaskCompletionSource<SetupRepairReport> Pending { get; } = new();
        public Task<SetupRepairReport> CheckAsync()
        { Checks++; return Task.FromResult(new SetupRepairReport([new("Owned fixture", true, "Simulated check; no real account or VM.")])); }
        public Task<SetupRepairReport> RepairAsync() { Repairs++; return Pending.Task; }
    }
    private static void VerifyNotificationSetup(Window owner, AppServices services, ExistingProjectsView projects, IHostResources resources, string project, string root)
    {
        var settings = services.Settings;
        var starts = 0;
        var notifications = new NotificationService(services.Paths, () => starts++);
        MenuItem More() => projects.CreateProjectMenu(project).Items.OfType<MenuItem>().Single(item => item.Header?.ToString()?.StartsWith("More") == true);
        Assert.DoesNotContain(More().Items.OfType<MenuItem>(), item => item.Header?.ToString()?.StartsWith("Notifications:") == true);
        var window = new SettingsWindow(settings, resources, notifications: notifications);
        var result = window.ShowDialog<bool>(owner);
        Assert.False(window.FindControl<CheckBox>("NotificationsSwitch")!.IsChecked == true);
        Click(window.FindControl<Button>("ConfigureNotificationsButton")!); Pump();
        var setup = window.OwnedWindows.OfType<NotificationSetupWindow>().Single();
        var provider = setup.FindControl<ComboBox>("ProviderBox")!;
        var sender = setup.FindControl<TextBox>("FromBox")!;
        var emailService = setup.FindControl<ComboBox>("EmailServiceBox")!;
        var password = setup.FindControl<TextBox>("PasswordBox")!;
        Assert.Null(setup.FindControl<TextBox>("ToBox"));
        sender.Text = "pager@gmail.com"; Pump();
        Assert.Equal("smtp.gmail.com", setup.FindControl<TextBox>("HostBox")!.Text);
        Assert.Equal("pager@gmail.com", setup.FindControl<TextBox>("UserBox")!.Text);
        Assert.False(setup.FindControl<Expander>("AdvancedEmailSettings")!.IsExpanded);
        password.Text = "owned-fixture-password";
        setup.FindControl<TextBox>("HostBox")!.Text = "smtp.custom.invalid"; Pump();
        Assert.Equal("", password.Text);
        sender.Text = "other@gmail.com"; Pump();
        Assert.Equal("smtp.custom.invalid", setup.FindControl<TextBox>("HostBox")!.Text);
        Assert.Equal("other@gmail.com", setup.FindControl<TextBox>("UserBox")!.Text);
        sender.Text = "pager@hotmail.com"; Pump();
        Assert.False(setup.FindControl<Button>("SaveChannelButton")!.IsEnabled);
        Click(setup.FindControl<Button>("SaveChannelButton")!); Pump();
        Assert.Null(setup.ResultReference);
        sender.Text = "pager@company.invalid";
        emailService.SelectedIndex = 1; Pump(); // Explicit Gmail for a hosted custom domain.
        Assert.Equal("smtp.gmail.com", setup.FindControl<TextBox>("HostBox")!.Text);
        Assert.True(setup.FindControl<Button>("SaveChannelButton")!.IsEnabled);
        password.Text = "owned-fixture-password";
        Click(setup.FindControl<Button>("SaveChannelButton")!); Pump();
        var emailReference = setup.ResultReference!;
        var selfDestination = notifications.Destinations.Read(emailReference);
        Assert.Equal("pager@company.invalid", selfDestination.From);
        Assert.Equal(selfDestination.From, selfDestination.To);
        Click(window.FindControl<Button>("ConfigureNotificationsButton")!); Pump();
        setup = window.OwnedWindows.OfType<NotificationSetupWindow>().Single();
        Assert.Equal("pager@company.invalid", setup.FindControl<TextBox>("FromBox")!.Text);
        Assert.Equal("owned-fixture-password", setup.FindControl<TextBox>("PasswordBox")!.Text);
        Assert.Equal("smtp.gmail.com", setup.FindControl<TextBox>("HostBox")!.Text);
        Assert.Equal(1, setup.FindControl<ComboBox>("EmailServiceBox")!.SelectedIndex);
        setup.FindControl<TextBox>("FromBox")!.Text = "restored@gmail.com";
        setup.FindControl<ComboBox>("EmailServiceBox")!.SelectedIndex = 0; Pump();
        setup.FindControl<TextBox>("PasswordBox")!.Text = "owned-fixture-password";
        Click(setup.FindControl<Button>("SaveChannelButton")!); Pump();
        Click(window.FindControl<Button>("ConfigureNotificationsButton")!); Pump();
        setup = window.OwnedWindows.OfType<NotificationSetupWindow>().Single();
        Assert.Equal(0, setup.FindControl<ComboBox>("EmailServiceBox")!.SelectedIndex);
        setup.FindControl<TextBox>("FromBox")!.Text = "restored@yahoo.com"; Pump();
        Assert.Equal("smtp.mail.yahoo.com", setup.FindControl<TextBox>("HostBox")!.Text);
        Assert.Equal("465", setup.FindControl<TextBox>("PortBox")!.Text);
        Assert.Equal("", setup.FindControl<TextBox>("PasswordBox")!.Text);
        provider = setup.FindControl<ComboBox>("ProviderBox")!;
        for (var index = 0; index < 4; index++)
        {
            provider.SelectedIndex = index; Pump();
            Assert.Equal(index == 0, setup.FindControl<StackPanel>("EmailPanel")!.IsVisible);
            Assert.Equal(index == 1, setup.FindControl<StackPanel>("TelegramPanel")!.IsVisible);
            Assert.Equal(index == 2, setup.FindControl<StackPanel>("DiscordPanel")!.IsVisible);
            Assert.Equal(index == 3, setup.FindControl<StackPanel>("NtfyPanel")!.IsVisible);
        }
        provider.SelectedIndex = 2;
        setup.FindControl<TextBox>("WebhookBox")!.Text = "https://discord.com/api/webhooks/123456/fixture_token_not_a_real_webhook";
        Assert.NotEqual('\0', setup.FindControl<TextBox>("WebhookBox")!.PasswordChar);
        Capture(setup, Path.Combine(root, "notification-channel.png"));
        Click(setup.FindControl<Button>("SaveChannelButton")!); Pump();
        Assert.Null(settings.Current.NotificationDestination);
        window.FindControl<CheckBox>("NotificationsSwitch")!.IsChecked = true;
        Pump();
        var settingsScroll = window.FindControl<ScrollViewer>("SettingsScroll")!;
        settingsScroll.Offset = new Vector(0, settingsScroll.Extent.Height);
        Pump();
        var configure = window.FindControl<Button>("ConfigureNotificationsButton")!;
        var configurePosition = configure.TranslatePoint(default, settingsScroll);
        Assert.NotNull(configurePosition);
        Assert.InRange(configurePosition.Value.Y, 0, settingsScroll.Viewport.Height - configure.Bounds.Height);
        Assert.True(window.FindControl<Button>("SaveButton")!.Bounds.Height > 0);
        Capture(window, Path.Combine(root, "notification-settings.png"));
        Click(window.FindControl<Button>("SaveButton")!); Pump();
        Assert.True(result.GetAwaiter().GetResult());
        Assert.Equal(1, starts);
        var saved = new SettingsStore(services.Paths);
        Assert.True(saved.Current.NotificationsEnabled);
        Assert.NotNull(saved.Current.NotificationDestination);
        Assert.False(saved.NotificationConsentFor(project).ProjectEnabled);
        var optIn = More().Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Notifications: Off"));
        optIn.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); Pump();
        Assert.True(new SettingsStore(services.Paths).NotificationConsentFor(project).ProjectEnabled);
        Assert.Contains(More().Items.OfType<MenuItem>(), item => Equals(item.Header, "Notifications: On"));
        Assert.DoesNotContain("fixture_token", File.ReadAllText(services.Paths.SettingsFile));
        Assert.DoesNotContain("fixture_token", File.ReadAllText(services.Paths.ProjectsFile));
        var oldReference = settings.Current.NotificationDestination;
        var cancelled = new SettingsWindow(settings, resources, notifications: notifications);
        var cancelledResult = cancelled.ShowDialog<bool>(owner);
        cancelled.FindControl<CheckBox>("NotificationsSwitch")!.IsChecked = false;
        Click(cancelled.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Cancel"))); Pump();
        Assert.False(cancelledResult.GetAwaiter().GetResult());
        Assert.Equal(oldReference, new SettingsStore(services.Paths).Current.NotificationDestination);
        Assert.True(new SettingsStore(services.Paths).Current.NotificationsEnabled);
        Assert.Equal(1, starts);
        settings.SavePreferences(settings.Current.ShowTips, settings.Current.DefaultAgent, settings.Current.MachineMemoryMb, settings.Current.MachineCores,
            notifications: new(false, oldReference));
        Assert.DoesNotContain(More().Items.OfType<MenuItem>(), item => item.Header?.ToString()?.StartsWith("Notifications:") == true);
    }
    private sealed class FixtureRuntime : IProjectRuntime
    {
        public FixtureSession Session { get; } = new();
        public bool NativeOnly { get; set; }
        public List<string> NativeLaunches { get; } = new();
        public int FencedLaunches { get; private set; }
        public FenceStartAvailability Availability { get; set; } = new("fixture availability note", false);
        public bool HasVirtualMachine => true;
        public string? HostAgentExecutable => null;
        public FenceStartAvailability FenceStartAvailability => Availability;
        public IFencedProjectSession CreateFencedSession() => Session;
        public bool IsFencedOpen(string project) => false;
        public bool IsHostOpen(string project) => false;
        public Task OpenFencedAsync(string project, CancellationToken cancellationToken, IProgress<string>? progress) { FencedLaunches++; return Task.CompletedTask; }
        public Task<bool> EnsureHostAgentAsync() => Task.FromResult(true);
        public bool TryLaunchHostAgent(string project, LaunchPlacement? placement, out string error) { NativeLaunches.Add(project); error = ""; return true; }
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
        public int ResumeCalls { get; private set; }
        public TaskCompletionSource? ReturnPending;
        public Task ResumeSavedAsync(string project, LaunchPlacement? placement, IProgress<string>? progress, CancellationToken cancellationToken)
        { ResumeCalls++; ResumedProject = project; return Task.CompletedTask; }
        public IReadOnlyList<string> Warnings => new[] { "fixture session warning" };
        public string? LatestAside(string project) => null;
        public ScanReport? LatestScan(string project) => null;
        public ProjectRecovery ReadRecovery(string project) => Recovery ?? new(project, false, "", Array.Empty<SavedReturn>());
        public async Task CopyBackToProjectAsync(string project, string? recoveryDirectory = null)
        {
            CopiedRecovery = recoveryDirectory;
            if (ReturnPending is not null) await ReturnPending.Task;
            if (RetryError is not null) throw new InvalidOperationException(RetryError);
            if (Recovery is null) throw new InvalidOperationException("This fixture must not apply returns.");
            Recovery = Recovery with { Returns = Recovery.Returns.Select(item => item.Directory == recoveryDirectory
                ? item with { CanRetry = false, Message = "Applied." } : item).ToArray() };
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
    private static void VerifyMachineSaveFailures(Window owner, IHostResources resources, string project, string root)
    {
        if (!OperatingSystem.IsWindows()) return; // The held-file replacement witness is Windows-specific.
        var paths = new AppPaths(userProfile: root, appDataDir: Path.Combine(root, "machine-save-settings"));
        var settings = new SettingsStore(paths);
        settings.SavePreferences(false, AgentChoice.Grok, 4096, 2);
        settings.SaveProjectMemory(project, 2048);
        var memory = new MachineWindow(settings, project, resources: resources);
        var memoryResult = memory.ShowDialog<bool>(owner);
        memory.FindControl<ComboBox>("MemoryBox")!.SelectedIndex = 1;
        var before = File.ReadAllBytes(paths.SettingsFile);
        using (var held = new FileStream(paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Click(memory.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Save")));
            Pump();
            Assert.False(memoryResult.IsCompleted);
            Assert.Contains("Memory could not be saved", memory.FindControl<TextBlock>("StatusText")!.Text);
            Assert.Equal(1, memory.FindControl<ComboBox>("MemoryBox")!.SelectedIndex);
            Assert.Equal(2048, settings.ProjectMemoryMbFor(project));
            Assert.Equal(before, File.ReadAllBytes(paths.SettingsFile));
        }
        Click(memory.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Save")));
        Pump();
        Assert.True(memoryResult.GetAwaiter().GetResult());
        Assert.Equal(3072, new SettingsStore(paths).ProjectMemoryMbFor(project));

        var stale = new SettingsStore(paths);
        var machine = new MachineWindow(stale, resources: resources);
        var machineResult = machine.ShowDialog<bool>(owner);
        machine.FindControl<ComboBox>("MemoryBox")!.SelectedIndex = 2;
        machine.FindControl<ComboBox>("CoresBox")!.SelectedIndex = 0;
        settings.SavePreferences(false, AgentChoice.Claude, 6144, 3);
        before = File.ReadAllBytes(paths.SettingsFile);
        Click(machine.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Save")));
        Pump();
        Assert.False(machineResult.IsCompleted);
        Assert.Contains("VM defaults could not be saved", machine.FindControl<TextBlock>("StatusText")!.Text);
        Assert.Equal(2, machine.FindControl<ComboBox>("MemoryBox")!.SelectedIndex);
        Assert.Equal(0, machine.FindControl<ComboBox>("CoresBox")!.SelectedIndex);
        Assert.Equal(4096, stale.Current.MachineMemoryMb);
        Assert.Equal(2, stale.Current.MachineCores);
        Assert.Equal(before, File.ReadAllBytes(paths.SettingsFile));
        machine.Close(false);
        Pump();
        Assert.False(machineResult.GetAwaiter().GetResult());
        var reopened = new SettingsStore(paths);
        Assert.Equal(6144, reopened.Current.MachineMemoryMb);
        Assert.Equal(3, reopened.Current.MachineCores);
        Assert.Equal(AgentChoice.Claude, reopened.Current.DefaultAgent);
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
