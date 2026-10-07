using LaunchPad.Models;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public class FenceTests
{
    [Fact]
    public void GrokBootHasNoDisplayAndDoesNotMountTheLiveFolder()
    {
        var live = @"C:\Secret\Live Project";
        var args = QemuCommand.Build(
            "tcg",
            @"C:\fence\session.qcow2",
            2222,
            4444,
            "fence=grok",
            share: null);

        var text = string.Join("\n", args);
        Assert.Contains("-accel", args);
        Assert.Contains("whpx", args);
        Assert.Equal("max", args[args.ToList().IndexOf("-cpu") + 1]);
        Assert.DoesNotContain("tcg", args);
        Assert.Contains("-netdev", args);
        Assert.Contains("user,id=net0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hostfwd", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("name=fence", text, StringComparison.Ordinal);
        Assert.Contains("name=status", text, StringComparison.Ordinal);
        Assert.Contains("name=tui", text, StringComparison.Ordinal);
        Assert.Contains("-display", args);
        Assert.Contains("none", args);
        Assert.Contains("-vga", args);
        Assert.DoesNotContain("virtio-gpu", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cuda", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vbox", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sandboxie", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(live, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-fsdev", args);
        Assert.Contains("cache=writethrough", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ImportDoesNotPutTheHostPathOnTheCommand()
    {
        var live = @"C:\Secret\Live Project";
        var staging = @"C:\Temp\fence-staging";
        var args = QemuCommand.Build(
            "whpx",
            @"C:\fence\session.qcow2",
            2222,
            4444,
            "fence=import",
            new FenceShare(staging, "hostin"));

        var text = string.Join("\n", args);
        Assert.DoesNotContain(staging, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(live, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-fsdev", args);
    }

    [Fact]
    public void StubScanSaysItDidNotLookInsideTheFiles()
    {
        var report = new StubReturnScan().Scan(@"C:\does-not-need-to-exist");

        Assert.True(report.IsStub);
        Assert.Equal(SealText.Stub, report.PlainStatement);
        Assert.Contains("did not look inside the files", report.PlainStatement, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DisabledFsdevMeansTheProjectIsNotMounted()
    {
        Assert.True(QemuCommand.FsdevIsDisabled("qemu-system-x86_64.exe: -fsdev help: fsdev support is disabled"));
        Assert.False(QemuCommand.FsdevIsDisabled("fsdev options:\nlocal"));
        Assert.DoesNotContain("9p", SealText.NoFileShare, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("copied into the VM", SealText.NoFileShare, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not mounted there", SealText.NoFileShare, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void MissingNinePDoesNotBlockFencedStart()
    {
        var reason = FenceSession.FileShareBlockReason();
        Assert.Equal(SealText.NoFileShare, reason);
        Assert.False(FenceSession.StartBlocked(reason));
        Assert.True(FenceSession.StartBlocked("QEMU is not in the folder beside this project."));
    }

    [Fact]
    public void LaunchReadyRequiresTheStoredSecret()
    {
        Assert.False(TestUserRunner.IsLaunchReady(accountExists: true, passwordStored: false));
        Assert.False(TestUserRunner.IsLaunchReady(accountExists: false, passwordStored: true));
        Assert.True(TestUserRunner.IsLaunchReady(accountExists: true, passwordStored: true));
    }

    [Fact]
    public void RepairPreservesThePasswordOnTheExistingAccount()
    {
        var script = LaunchAccountSetup.AccountScript;
        var setAt = script.IndexOf("New-LocalUser", StringComparison.Ordinal);
        var clearAt = script.LastIndexOf("$sec = $null", StringComparison.Ordinal);
        Assert.True(setAt > 0);
        Assert.True(clearAt > setAt);
        Assert.DoesNotContain("Set-LocalUser", script, StringComparison.Ordinal);
    }

    [Fact]
    public void StartFailureKeepsTheStopSentence()
    {
        Assert.Equal(SealText.TestAccountMissing, FencePanel.Failure(SealText.TestAccountMissing));
        Assert.Equal(FencePanel.Stopped, FencePanel.Failure(""));
    }

    [Fact]
    public void SealTextWarnsThatExplorerBreaksTheSeal()
    {
        Assert.Contains("Explorer", SealText.Explorer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("breaks the seal", SealText.Explorer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TestUserRunnerRefusesWhenTheAccountIsMissing()
    {
        var runner = new TestUserRunner(accountExists: () => false, passwordStored: () => false);
        var exe = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(exe, "not a real program");
        try
        {
            var started = runner.TryRun(exe, out var message);
            Assert.False(started);
            Assert.Equal(SealText.TestAccountMissing, message);
        }
        finally
        {
            File.Delete(exe);
        }
    }

    [Fact]
    public void CopyBackKeepsFilesThatExistOnlyOnTheLiveProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N"));
        var aside = Path.Combine(root, "aside");
        var live = Path.Combine(root, "live");
        Directory.CreateDirectory(aside);
        Directory.CreateDirectory(live);
        try
        {
            File.WriteAllText(Path.Combine(live, "only-live.txt"), "keep");
            File.WriteAllText(Path.Combine(live, "a.txt"), "old");
            File.WriteAllText(Path.Combine(aside, "a.txt"), "new");
            File.WriteAllText(Path.Combine(aside, "from-guest.txt"), "guest");

            CopyBack.Apply(aside, live);

            Assert.Equal("keep", File.ReadAllText(Path.Combine(live, "only-live.txt")));
            Assert.Equal("new", File.ReadAllText(Path.Combine(live, "a.txt")));
            Assert.Equal("guest", File.ReadAllText(Path.Combine(live, "from-guest.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FailedCopyLeavesTheLiveTreeUnchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N"));
        var aside = Path.Combine(root, "aside");
        var live = Path.Combine(root, "live");
        Directory.CreateDirectory(aside);
        Directory.CreateDirectory(live);
        try
        {
            File.WriteAllText(Path.Combine(live, "keep.txt"), "live-only");
            File.WriteAllText(Path.Combine(live, "shared.txt"), "live");
            File.WriteAllText(Path.Combine(aside, "shared.txt"), "session");
            File.WriteAllText(Path.Combine(aside, "COPY-FAILED"), "failed");
            var before = File.ReadAllText(Path.Combine(live, "keep.txt")) + File.ReadAllText(Path.Combine(live, "shared.txt"));

            var applied = CopyBack.TryApply(aside, live);

            Assert.False(applied);
            Assert.Equal("live-only", File.ReadAllText(Path.Combine(live, "keep.txt")));
            Assert.Equal("live", File.ReadAllText(Path.Combine(live, "shared.txt")));
            Assert.Equal(before, File.ReadAllText(Path.Combine(live, "keep.txt")) + File.ReadAllText(Path.Combine(live, "shared.txt")));
            Assert.False(File.Exists(Path.Combine(live, "COPY-FAILED")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SessionCopyGitAddressIsNotTheWindowsOrigin()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N"));
        var live = Path.Combine(root, "live");
        var copy = Path.Combine(root, "copy");
        Directory.CreateDirectory(Path.Combine(live, ".git"));
        Directory.CreateDirectory(Path.Combine(copy, ".git"));
        try
        {
            var liveConfig = "[remote \"origin\"]\n\turl = https://example.invalid/real.git\n";
            var copyConfig = liveConfig;
            File.WriteAllText(Path.Combine(live, ".git", "config"), liveConfig);
            File.WriteAllText(Path.Combine(copy, ".git", "config"), copyConfig);

            SessionGit.PointCopyAtFence(copy);

            Assert.Equal(liveConfig, File.ReadAllText(Path.Combine(live, ".git", "config")));
            var sessionUrl = File.ReadAllText(Path.Combine(copy, ".git", "config"));
            Assert.Contains(SessionGit.FenceOrigin, sessionUrl, StringComparison.Ordinal);
            Assert.DoesNotContain("https://example.invalid/real.git", sessionUrl, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PanelLabelsMatchTheForcedStates()
    {
        Assert.Equal("busy", FencePanel.Label("busy", vmStopped: false));
        Assert.Equal("needs an answer", FencePanel.Label("needs-an-answer", vmStopped: false));
        Assert.Equal("stopped", FencePanel.Label("busy", vmStopped: true));
    }

    [Fact]
    public async Task MissingLaunchAccountStopsBeforeQemu()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N"));
        var live = Path.Combine(root, "live");
        var sessions = Path.Combine(root, "sessions");
        Directory.CreateDirectory(live);
        var before = System.Diagnostics.Process.GetProcessesByName("qemu-system-x86_64").Select(p => p.Id).ToHashSet();
        try
        {
            var paths = new AppPaths(userProfile: root, appDataDir: Path.Combine(root, "appdata"));
            var session = new FenceSession(
                new SetupLog(paths),
                launchAccountReady: () => false,
                sessionsRoot: () => sessions);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                session.StartAsync(live, null, null, CancellationToken.None));

            Assert.Equal(SealText.TestAccountMissing, error.Message);
            var after = System.Diagnostics.Process.GetProcessesByName("qemu-system-x86_64").Select(p => p.Id).ToHashSet();
            Assert.Empty(after.Except(before));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExplainDoesNotCreateTheAccount()
    {
        var created = false;
        var text = LaunchAccountSetup.Explain(() => created = true);
        Assert.False(created);
        Assert.Contains(LaunchAccountSetup.NotSignedIn, text, StringComparison.Ordinal);
        Assert.Contains(LaunchAccountSetup.NotBuilder, text, StringComparison.Ordinal);
        Assert.DoesNotContain("copied", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deleted", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Open does not start Windows Grok.", SealText.WindowsGrok, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenDoesNotStartWindowsGrok()
    {
        var before = ProcessIds("grok");
        var root = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var opened = ProjectOpen.TryOpen(root, out var error);
            Assert.True(opened);
            Assert.Equal("", error);
            Assert.Empty(ProcessIds("grok").Except(before));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FirstWarningIsOneSentenceAndTheCheckboxSkipsTheNextLaunch()
    {
        Assert.Equal(
            "VM agents work on a copy; native Windows agents work directly in your folder using your account permissions.",
            FirstWarning.Text);
        Assert.DoesNotContain("\n", FirstWarning.Text);

        var settings = new AppSettings();
        Assert.True(FirstWarning.ShouldShow(settings));
        var saved = 0;
        FirstWarning.Continue(settings, dontShowAgain: false, () => saved++);
        Assert.Equal(0, saved);
        Assert.True(FirstWarning.ShouldShow(settings));

        FirstWarning.Continue(settings, dontShowAgain: true, () => saved++);
        Assert.Equal(1, saved);
        Assert.True(settings.OnboardingCompleted);
        Assert.False(FirstWarning.ShouldShow(settings));

        var welcome = File.ReadAllText(RepoFile("src", "LaunchPad", "Views", "WelcomeDialog.axaml"));
        Assert.Contains("Continue", welcome, StringComparison.Ordinal);
        Assert.Contains("Don't show this again", welcome, StringComparison.Ordinal);
        Assert.Contains("New Project creates a folder", welcome, StringComparison.Ordinal);
        Assert.Contains(FirstWarning.Text, welcome, StringComparison.Ordinal);

        var main = File.ReadAllText(RepoFile("src", "LaunchPad", "MainWindow.axaml.cs"));
        var loadedAt = main.IndexOf("private async void MainWindow_Loaded", StringComparison.Ordinal);
        var homeAt = main.IndexOf("public void ShowHome()", StringComparison.Ordinal);
        var loaded = main[loadedAt..homeAt];
        Assert.Contains("ShowHome()", loaded, StringComparison.Ordinal);
        Assert.Contains("ShowOnboarding()", loaded, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowExistingProjects()", loaded, StringComparison.Ordinal);

        var openAt = main.IndexOf("public async void OpenProject", StringComparison.Ordinal);
        var open = main[openAt..];
        Assert.DoesNotContain("EnsureVmReadyAsync", open, StringComparison.Ordinal);
        Assert.DoesNotContain("HypervisorWindow", main, StringComparison.Ordinal);
        Assert.DoesNotContain("LaunchAccountWindow", main, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectClickStartsTheFencedSessionAndDoesNotStartGrokOrQemu()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N"));
        var live = Path.Combine(root, "live");
        var sessions = Path.Combine(root, "sessions");
        var kept = Path.Combine(root, "images", "debian-12-builder.qcow2");
        Directory.CreateDirectory(live);
        var grokBefore = ProcessIds("grok");
        var qemuBefore = ProcessIds("qemu-system-x86_64");
        SessionLaunch? seen = null;
        try
        {
            var paths = new AppPaths(userProfile: root, appDataDir: Path.Combine(root, "appdata"));
            var session = new FenceSession(
                new SetupLog(paths),
                launchAccountReady: () => true,
                keptImage: () => kept,
                sessionsRoot: () => sessions,
                starter: plan =>
                {
                    seen = plan;
                    return true;
                });

            await ProjectRow.OpenAsync(session, live, CancellationToken.None);

            Assert.NotNull(seen);
            Assert.Equal("BuildLaunchTest", seen!.UserName);
            Assert.Contains(seen.Arguments, arg => arg.Contains("name=tui", StringComparison.Ordinal));
            Assert.Empty(ProcessIds("grok").Except(grokBefore));
            Assert.Empty(ProcessIds("qemu-system-x86_64").Except(qemuBefore));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AddProjectListsTheFolderAndDoesNotStartASession()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "User");
        var folder = Path.Combine(root, "Added");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(folder);
        var grokBefore = ProcessIds("grok");
        var qemuBefore = ProcessIds("qemu-system-x86_64");
        try
        {
            var paths = new AppPaths(
                userProfile: home,
                grokHome: Path.Combine(home, ".grok"),
                grokBin: Path.Combine(home, ".grok", "bin"),
                appDataDir: Path.Combine(root, "AppData"));
            var settings = new SettingsStore(paths);
            ProjectRow.AddFolder(settings, "Added", folder);

            var listed = new ProjectCatalog(paths, settings).ListProjects();
            Assert.Contains(listed, project => string.Equals(project.Path, folder, StringComparison.OrdinalIgnoreCase));
            Assert.False(Directory.Exists(Path.Combine(root, "sessions")));
            Assert.Empty(ProcessIds("grok").Except(grokBefore));
            Assert.Empty(ProcessIds("qemu-system-x86_64").Except(qemuBefore));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProjectPageDropsTheFencedStartButtonAndTheProgramTest()
    {
        var projects = File.ReadAllText(RepoFile("src", "LaunchPad", "Views", "ExistingProjectsView.axaml"));
        var dialog = File.ReadAllText(RepoFile("src", "LaunchPad", "Views", "FenceDialog.axaml"));
        Assert.DoesNotContain("Fenced start", projects, StringComparison.Ordinal);
        Assert.DoesNotContain("FenceState", projects, StringComparison.Ordinal);
        Assert.DoesNotContain("Test a Windows program", dialog, StringComparison.Ordinal);
        Assert.Contains("Add Project", projects, StringComparison.Ordinal);
        Assert.DoesNotContain("Open on this PC", projects, StringComparison.Ordinal);
        Assert.DoesNotContain(">Open unfenced<", projects, StringComparison.Ordinal);

        var home = File.ReadAllText(RepoFile("src", "LaunchPad", "Views", "HomeView.axaml"));
        Assert.DoesNotContain("Open unfenced", home, StringComparison.Ordinal);

        var listCode = File.ReadAllText(RepoFile("src", "LaunchPad", "Views", "ExistingProjectsView.axaml.cs"));
        Assert.Contains("_services.Runtime.HasVirtualMachine", listCode, StringComparison.Ordinal);
        Assert.Contains("e.Handled = true", listCode, StringComparison.Ordinal);
    }

    [Fact]
    public void FenceCopyNamesTheProjectFileAndTheTuiPort()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "Demo");
        Directory.CreateDirectory(Path.Combine(project, "src"));
        var file = Path.Combine(project, "src", "a.txt");
        File.WriteAllText(file, "demo");
        try
        {
            var relative = FenceFiles.Relative(project, file);
            Assert.Equal("src/a.txt", relative);
            Assert.Equal("FILE 4 src/a.txt\n", FenceFiles.Header(4, relative!));
            Assert.False(FenceFiles.IsSafe("../secret"));
            Assert.Contains("--tui 4567", TuiWindow.Arguments(4567, "Demo"), StringComparison.Ordinal);
            var exe = @"C:\LaunchPad\LaunchPad.exe";
            var pid = @"C:\LaunchPad\sessions\20261003235959\tui.pid";
            var command = TuiWindow.TerminalArguments(exe, 4567, "Demo Project", pid);
            Assert.Contains("cmd.exe /c", command, StringComparison.Ordinal);
            Assert.Contains("--tui 4567", command, StringComparison.Ordinal);
            Assert.Contains("--title \"Demo Project\"", command, StringComparison.Ordinal);
            var runAt = command.IndexOf("cmd.exe /c", StringComparison.Ordinal);
            Assert.True(runAt >= 0 && runAt < command.IndexOf("--tui", StringComparison.Ordinal));
            Assert.False(command[(runAt + "cmd.exe /c".Length)..].TrimStart().StartsWith("\"", StringComparison.Ordinal));
            var session = File.ReadAllText(RepoFile("src", "LaunchPad", "Services", "Fence", "FenceSession.cs"));
            Assert.DoesNotContain("tui.Exited", session, StringComparison.Ordinal);
            Assert.Contains("tui.pid", session, StringComparison.Ordinal);
            Assert.Equal("AUTH 4\n", GuestAuth.Header(4));

            var link = new LoginLink();
            var other = System.Text.Encoding.ASCII.GetBytes("https://grok.com/login ");
            Assert.Null(link.Push(other, other.Length));
            var first = System.Text.Encoding.ASCII.GetBytes("see https://auth.x.");
            var second = System.Text.Encoding.ASCII.GetBytes("ai/device?user_code=ABCD\n");
            Assert.Null(link.Push(first, first.Length));
            Assert.Equal("https://auth.x.ai/device?user_code=ABCD", link.Push(second, second.Length));
            var again = System.Text.Encoding.ASCII.GetBytes("https://auth.x.ai/again\n");
            Assert.Null(link.Push(again, again.Length));

            var asking = new LoginLink();
            var screen = System.Text.Encoding.ASCII.GetBytes("Approve in your browser to finish signing in.\n\u001b[12;40HYHG4-TEQH\nWaiting for approval\nYHG4-TEQH\n");
            Assert.Equal("https://auth.x.ai/device?user_code=YHG4-TEQH", asking.Push(screen, screen.Length));
            Assert.Null(asking.Push(screen, screen.Length));
            var quiet = System.Text.Encoding.ASCII.GetBytes("Grok Build ready\n");
            Assert.Null(new LoginLink().Push(quiet, quiet.Length));

            var statusBuffer = new StatusBuffer();
            var sizeOk = System.Text.Encoding.ASCII.GetBytes("busy\nSIZE-OK 40 120\n");
            statusBuffer.Push(sizeOk, sizeOk.Length);
            Assert.True(statusBuffer.SizeAccepted);
            var authHead = System.Text.Encoding.ASCII.GetBytes("AUTH 4\n");
            statusBuffer.Push(authHead, authHead.Length);
            Assert.False(statusBuffer.AuthFinished);
            var authBody = System.Text.Encoding.ASCII.GetBytes("demo");
            statusBuffer.Push(authBody, authBody.Length);
            Assert.True(statusBuffer.AuthFinished);
            Assert.Equal("demo", System.Text.Encoding.ASCII.GetString(statusBuffer.AuthBody));

            var guest = File.ReadAllText(RepoFile("build-launch-qemu", "images", "prepare", "bl-proof.sh"));
            Assert.Contains("GROK_LOGIN_DEVICE_FLOW", guest, StringComparison.Ordinal);
            Assert.Contains("AUTH-OUT", guest, StringComparison.Ordinal);
            Assert.Contains("HOME-OUT", guest, StringComparison.Ordinal);
            Assert.Contains("drop_to_builder", guest, StringComparison.Ordinal);
            Assert.Contains("sessions", guest, StringComparison.Ordinal);
            Assert.DoesNotContain("worktrees", guest, StringComparison.Ordinal);
            Assert.DoesNotContain("config.toml", guest, StringComparison.Ordinal);
            Assert.True(GuestAuth.IsDocument(System.Text.Encoding.UTF8.GetBytes("{\"access_token\":\"abc\"}")));
            Assert.False(GuestAuth.IsDocument(System.Text.Encoding.UTF8.GetBytes("not-json")));
            Assert.False(GuestAuth.IsDocument(System.Text.Encoding.UTF8.GetBytes("{\"rules\":\"../etc\"}")));
            var unit = File.ReadAllText(RepoFile("build-launch-qemu", "images", "prepare", "bl-proof.service"));
            Assert.DoesNotContain("User=builder", unit, StringComparison.Ordinal);
            var rules = File.ReadAllText(RepoFile("build-launch-qemu", "images", "prepare", "99-bl-fence.rules"));
            Assert.Contains("MODE=\"0600\"", rules, StringComparison.Ordinal);
            Assert.DoesNotContain("dialout", rules, StringComparison.Ordinal);
            var net = File.ReadAllText(RepoFile("build-launch-qemu", "images", "prepare", "bl-net.sh"));
            Assert.Contains("10.0.2.2", net, StringComparison.Ordinal);
            Assert.Contains("/dev/hvc0", guest, StringComparison.Ordinal);
            var homeOwner = guest.IndexOf("GROK-HOME builder", StringComparison.Ordinal);
            var grokExec = guest.IndexOf("start_agent($agent_diag_out);", StringComparison.Ordinal);
            Assert.True(homeOwner > 0 && grokExec > homeOwner);
            Assert.Contains("$bin = '/usr/local/bin/grok';", guest, StringComparison.Ordinal);
            Assert.Contains("exec $bin;", guest, StringComparison.Ordinal);
            Assert.DoesNotContain("WINSIZE", guest, StringComparison.Ordinal);
            Assert.DoesNotContain("0x5414", guest, StringComparison.Ordinal);
            Assert.DoesNotContain("pack 'S4', 40, 120", guest, StringComparison.Ordinal);
            var qemuSource = File.ReadAllText(RepoFile("src", "LaunchPad", "Services", "Fence", "QemuCommand.cs"));
            Assert.Contains("console-size=on", qemuSource, StringComparison.Ordinal);
            var sizeSource = File.ReadAllText(RepoFile("src", "LaunchPad", "Services", "Fence", "ConsoleSizeLink.cs"));
            Assert.Contains("chardev-window-size-changed", sizeSource, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SetupDoesNotInstallWindowsGrokAndPackagesQemuSeparately()
    {
        var setup = File.ReadAllText(RepoFile("src", "LaunchPad", "Services", "GrokSetup.cs"));
        var readyAt = setup.IndexOf("EnsureReadyAsync", StringComparison.Ordinal);
        var installAt = setup.IndexOf("InstallGrokAsync", StringComparison.Ordinal);
        var ready = setup[readyAt..installAt];
        Assert.DoesNotContain("InstallGrokAsync(", ready, StringComparison.Ordinal);
        Assert.Contains("Windows Grok is not installed by LaunchPad.", ready, StringComparison.Ordinal);

        var iss = File.ReadAllText(RepoFile("installer", "LaunchPad.iss"));
        Assert.Contains("qemu\\*", iss, StringComparison.Ordinal);
        Assert.Contains("#include RuntimeFilesInclude", iss, StringComparison.Ordinal);
        Assert.DoesNotContain("Source: \"..\\..\\build-launch-qemu\\images\\debian-12-builder.qcow2\"", iss, StringComparison.Ordinal);
        Assert.DoesNotContain("Source: \"..\\dist\\grok.exe\"", iss, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateCustomPage", iss, StringComparison.Ordinal);
        Assert.Contains("DisableWelcomePage=no", iss, StringComparison.Ordinal);
        Assert.Contains("InfoBeforeFile=..\\DOWNLOAD-NOTE.txt", iss, StringComparison.Ordinal);
        Assert.Contains("desktopicon", iss, StringComparison.Ordinal);
        Assert.DoesNotContain("DisableDirPage=yes", iss, StringComparison.Ordinal);
        Assert.DoesNotContain("DisableReadyPage=yes", iss, StringComparison.Ordinal);
        Assert.Contains("HypervisorPlatform", iss, StringComparison.Ordinal);
        Assert.Contains("New-LocalUser", iss, StringComparison.Ordinal);

        var prepare = File.ReadAllText(RepoFile("installer", "SetupPrepare.ps1"));
        Assert.Contains("HypervisorPlatform", prepare, StringComparison.Ordinal);
        Assert.Contains("New-LocalUser", prepare, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-LocalUser", prepare, StringComparison.Ordinal);
        Assert.DoesNotContain("qemu-system", prepare, StringComparison.Ordinal);

        Assert.DoesNotContain("SetupPublic.ps1", iss, StringComparison.Ordinal);
        Assert.Contains("--activate-runtime", iss, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildLaunchTest:(OI)(CI)RX", iss, StringComparison.Ordinal);
        Assert.Contains("onlyifdoesntexist nocompression uninsneveruninstall",
            File.ReadAllText(RepoFile("scripts", "installer-runtime-files.ps1")), StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Users\Public\LaunchPad", File.ReadAllText(RepoFile("src", "LaunchPad", "Services", "Fence", "PublicRuntime.cs")), StringComparison.Ordinal);

        var root = @"C:\Users\Big Bojangles\AppData\Local\Programs\LaunchPad";
        var session = root + @"\sessions\20261003235959";
        var qemuDirectory = root + @"\qemu\fence";
        var args = QemuCommand.Build(
            "whpx",
            session + @"\session.qcow2",
            0,
            65535,
            "fence",
            null,
            session + @"\serial.log",
            firmwareDir: root + @"\qemu\share",
            workingDirectory: qemuDirectory);
        var command = Quote(root + @"\qemu\fence\qemu-system-x86_64.exe");
        foreach (var arg in args)
            command += " " + Quote(arg);

        Assert.InRange(command.Length, 1, 1023);
        var drive = args[args.ToList().IndexOf("-drive") + 1];
        var diskPath = drive["file=".Length..drive.IndexOf(",if=", StringComparison.Ordinal)];
        Assert.Equal(session + @"\session.qcow2", Path.GetFullPath(diskPath, qemuDirectory));
        var serial = args[args.ToList().IndexOf("-serial") + 1];
        Assert.Equal(session + @"\serial.log", Path.GetFullPath(serial["file:".Length..], qemuDirectory));
        Assert.Equal(root + @"\qemu\share", Path.GetFullPath(args[args.ToList().IndexOf("-L") + 1], qemuDirectory));
    }

    private static string Quote(string value)
    {
        if (value.IndexOfAny([' ', '\t', '"']) < 0)
            return value;
        return "\"" + value + "\"";
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, parts));
    }

    [Fact]
    public async Task FencedStartRecordsTheLaunchAccountAndDoesNotStartQemuAsTheSignedInUser()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N"));
        var live = Path.Combine(root, "live");
        var sessions = Path.Combine(root, "sessions");
        var kept = Path.Combine(root, "images", "debian-12-builder.qcow2");
        Directory.CreateDirectory(live);
        var before = ProcessIds("qemu-system-x86_64");
        SessionLaunch? seen = null;
        try
        {
            var paths = new AppPaths(userProfile: root, appDataDir: Path.Combine(root, "appdata"));
            var session = new FenceSession(
                new SetupLog(paths),
                launchAccountReady: () => true,
                keptImage: () => kept,
                sessionsRoot: () => sessions,
                starter: plan =>
                {
                    seen = plan;
                    return true;
                });

            await session.StartAsync(live, null, null, CancellationToken.None);

            Assert.NotNull(seen);
            Assert.Equal("BuildLaunchTest", seen!.UserName);
            Assert.NotEqual(Environment.UserName, seen.UserName);
            Assert.NotEqual("builder", seen.UserName);
            Assert.EndsWith("debian-12-builder.qcow2", seen.BackingImage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("nocloud", seen.BackingImage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("prepared.qcow2", seen.BackingImage, StringComparison.OrdinalIgnoreCase);
            var text = string.Join("\n", seen.Arguments);
            Assert.Contains("whpx", text, StringComparison.Ordinal);
            Assert.Contains("user,id=net0", text, StringComparison.Ordinal);
            Assert.DoesNotContain("hostfwd", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("-fsdev", seen.Arguments);
            Assert.DoesNotContain(live, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("nocloud", text, StringComparison.OrdinalIgnoreCase);
            var recorded = File.ReadAllText(Directory.EnumerateFiles(sessions, "session-user.txt", SearchOption.AllDirectories).Single()).Trim();
            Assert.Equal("BuildLaunchTest", recorded);
            Assert.DoesNotContain(Environment.UserName, recorded, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("builder", recorded, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(ProcessIds("qemu-system-x86_64").Except(before));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedTokenStartDoesNotRunAsTheSignedInUser()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N"));
        var live = Path.Combine(root, "live");
        var sessions = Path.Combine(root, "sessions");
        var kept = Path.Combine(root, "images", "debian-12-builder.qcow2");
        Directory.CreateDirectory(live);
        var before = ProcessIds("qemu-system-x86_64");
        try
        {
            var paths = new AppPaths(userProfile: root, appDataDir: Path.Combine(root, "appdata"));
            var session = new FenceSession(
                new SetupLog(paths),
                launchAccountReady: () => true,
                keptImage: () => kept,
                sessionsRoot: () => sessions,
                starter: _ => false);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                session.StartAsync(live, null, null, CancellationToken.None));

            Assert.Equal(SealText.TestAccountMissing, error.Message);
            var recorded = File.ReadAllText(Directory.EnumerateFiles(sessions, "session-user.txt", SearchOption.AllDirectories).Single()).Trim();
            Assert.Equal("BuildLaunchTest", recorded);
            Assert.DoesNotContain(Environment.UserName, recorded, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(ProcessIds("qemu-system-x86_64").Except(before));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SignInLinkOpensTheOscTargetAndAWrappedCode()
    {
        var bel = new LoginLink();
        var osc = System.Text.Encoding.ASCII.GetBytes("\u001b]8;;https://auth.x.ai/device?user_code=638E-YTSS\u0007click here");
        Assert.Equal("https://auth.x.ai/device?user_code=638E-YTSS", bel.Push(osc, osc.Length));
        Assert.Null(bel.Push(osc, osc.Length));

        var st = new LoginLink();
        var stBytes = System.Text.Encoding.ASCII.GetBytes("\u001b]8;;https://auth.x.ai/device?user_code=6M7W-G2Q5\u001b\\click here");
        Assert.Equal("https://auth.x.ai/device?user_code=6M7W-G2Q5", st.Push(stBytes, stBytes.Length));

        var other = new LoginLink();
        var example = System.Text.Encoding.ASCII.GetBytes("\u001b]8;;https://example.com/docs\u0007docs");
        Assert.Null(other.Push(example, example.Length));

        var wrapped = new LoginLink();
        var split = System.Text.Encoding.ASCII.GetBytes("https://auth.x.ai/device?user_co\nde=WXYZ-ABCD\n");
        Assert.Equal("https://auth.x.ai/device?user_code=WXYZ-ABCD", wrapped.Push(split, split.Length));

        var shortUrl = new LoginLink();
        var hostOnly = System.Text.Encoding.ASCII.GetBytes("https://auth.x.ai/\n");
        Assert.Null(shortUrl.Push(hostOnly, hostOnly.Length));
        var quiet = System.Text.Encoding.ASCII.GetBytes("Grok Build ready\n");
        Assert.Null(new LoginLink().Push(quiet, quiet.Length));
    }

    [Fact]
    public async Task ASecondProjectIsAllowedAndTheSameFolderIsNot()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "one");
        var second = Path.Combine(root, "two");
        var sessions = Path.Combine(root, "sessions");
        var kept = Path.Combine(root, "images", "debian-12-builder.qcow2");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        var qemuBefore = ProcessIds("qemu-system-x86_64");
        try
        {
            var table = new SessionTable();
            table.Add(11, first);
            table.Add(12, second);
            Assert.True(table.Blocks(first));
            Assert.False(table.Blocks(Path.Combine(root, "three")));
            Assert.True(table.OtherThan(11));
            table.Remove(12);
            Assert.False(table.OtherThan(11));

            Assert.Equal(20000, PortChoice.Next(Array.Empty<int>()));
            Assert.Equal(20005, PortChoice.Next(new[] { 20000 }));
            Assert.NotEqual(PortChoice.Next(Array.Empty<int>()), PortChoice.Next(new[] { 20000 }));

            var paths = new AppPaths(userProfile: root, appDataDir: Path.Combine(root, "appdata"));
            FenceSession.NoteOpen(Environment.ProcessId, first, 20896);
            try
            {
                var blocked = new FenceSession(
                    new SetupLog(paths),
                    launchAccountReady: () => true,
                    keptImage: () => kept,
                    sessionsRoot: () => sessions,
                    starter: _ => true);
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    blocked.StartAsync(first, null, null, CancellationToken.None));
                Assert.Equal(SealText.AlreadyRunning, error.Message);

                SessionLaunch? seen = null;
                var allowed = new FenceSession(
                    new SetupLog(paths),
                    launchAccountReady: () => true,
                    keptImage: () => kept,
                    sessionsRoot: () => sessions,
                    starter: plan =>
                    {
                        seen = plan;
                        return true;
                    });
                await allowed.StartAsync(second, null, null, CancellationToken.None);
                Assert.NotNull(seen);
            }
            finally
            {
                FenceSession.NoteClosed(Environment.ProcessId);
            }

            Assert.Empty(ProcessIds("qemu-system-x86_64").Except(qemuBefore));
            var source = File.ReadAllText(RepoFile("src", "LaunchPad", "Services", "Fence", "FenceSession.cs"));
            Assert.Contains("IsProjectOpen", source, StringComparison.Ordinal);
            Assert.DoesNotContain("if (IsRunning)", source, StringComparison.Ordinal);
        }
        finally
        {
            FenceSession.NoteClosed(Environment.ProcessId);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ProjectHistoryRoundTripStaysWithThatFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var one = QemuLayout.ProjectKey(@"C:\projects\one");
            var two = QemuLayout.ProjectKey(@"C:\projects\two");
            Assert.NotEqual(one, two);
            var body = System.Text.Encoding.ASCII.GetBytes("session-bytes");
            Assert.Equal("HOME 13\n", GuestHome.Header(body.Length));
            GuestHome.Save(one, body, root);
            Assert.Equal("session-bytes", System.Text.Encoding.ASCII.GetString(GuestHome.Read(one, root)!));
            Assert.Null(GuestHome.Read(two, root));
            GuestHome.Save(one, Array.Empty<byte>(), root);
            GuestHome.Save(one, null, root);
            Assert.Equal("session-bytes", System.Text.Encoding.ASCII.GetString(GuestHome.Read(one, root)!));

            var buffer = new StatusBuffer();
            var big = System.Text.Encoding.ASCII.GetBytes("HOME BIG\n");
            buffer.Push(big, big.Length);
            Assert.True(buffer.HomeTooBig);
            var home = new StatusBuffer();
            var head = System.Text.Encoding.ASCII.GetBytes("HOME 4\n");
            home.Push(head, head.Length);
            Assert.False(home.HomeFinished);
            var payload = System.Text.Encoding.ASCII.GetBytes("tar!");
            home.Push(payload, payload.Length);
            Assert.True(home.HomeFinished);
            Assert.Equal("tar!", System.Text.Encoding.ASCII.GetString(home.HomeBody));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GuestWarningKeepsTheReadyLineAndDropsSixtyFourHex()
    {
        var secret = new string('a', 64);
        var log = "CMD-RAN 1\nWARN fence-ready\nWARN " + secret + "\nhello\n";
        var kept = GuestWarnings.Keep(log);
        var joined = string.Join("\n", kept);
        Assert.Equal(new[] { "WARN fence-ready" }, kept);
        Assert.Equal(0, GuestWarnings.Hex64Count(joined));
        Assert.DoesNotContain(secret, joined, StringComparison.Ordinal);
    }

    [Fact]
    public void ForcedOffHypervisorAsksOnceAndTheNextStartClearsTheResume()
    {
        var enables = 0;
        var off = HypervisorCheck.Decide(new HypervisorState(false, false, true, "AuthenticAMD"), resumePending: false);
        Assert.True(off.Show);
        Assert.True(off.RequestRestart);
        Assert.True(off.RequestElevation);
        Assert.True(off.SetResume);
        Assert.False(off.ClearResume);
        Assert.Equal(0, enables);
        Assert.Equal(1, HypervisorCheck.AcceptElevation(off, () => enables++));
        Assert.Equal(1, enables);

        var on = HypervisorCheck.Decide(new HypervisorState(true, true, true, "AuthenticAMD"), resumePending: true);
        Assert.False(on.Show);
        Assert.False(on.RequestRestart);
        Assert.False(on.RequestElevation);
        Assert.True(on.ClearResume);
        Assert.Equal(0, HypervisorCheck.AcceptElevation(on, () => enables++));
        Assert.Equal(1, enables);
    }

    [Fact]
    public void FirmwareOffNamesOnlyTheMatchingSetting()
    {
        var amd = HypervisorCheck.Decide(new HypervisorState(true, false, false, "AuthenticAMD"), resumePending: false);
        Assert.Contains("SVM Mode", amd.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Intel", amd.Message, StringComparison.Ordinal);
        Assert.False(amd.RequestElevation);

        var intel = HypervisorCheck.Decide(new HypervisorState(true, false, false, "GenuineIntel"), resumePending: false);
        Assert.Contains("Intel Virtualization Technology", intel.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SVM", intel.Message, StringComparison.Ordinal);
        Assert.False(intel.RequestElevation);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void LiveHypervisorCheckRequestsNoRestart()
    {
        var state = HypervisorCheck.Query();
        Assert.True(state.FeatureEnabled);
        Assert.True(state.HypervisorPresent);
        var decision = HypervisorCheck.Decide(state, resumePending: false);
        Assert.False(decision.Show);
        Assert.False(decision.RequestRestart);
        Assert.False(decision.RequestElevation);
        var enables = 0;
        Assert.Equal(0, HypervisorCheck.AcceptElevation(decision, () => enables++));
    }

    [EnvironmentFact("LAUNCHPAD_RECORD_SCREEN", "1")]
    [Trait("Category", "Integration")]
    public void RecordScreenTextWhileTheAccountIsMissing()
    {
        if (Environment.GetEnvironmentVariable("LAUNCHPAD_RECORD_SCREEN") != "1")
            return;

        var created = false;
        var text = LaunchAccountSetup.Explain(() => created = true);
        Assert.False(created);
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "net.exe",
            Arguments = "user " + TestUserRunner.UserName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(start);
        Assert.NotNull(process);
        process!.WaitForExit(10_000);
        Assert.NotEqual(0, process.ExitCode);
        File.WriteAllText(
            Path.Combine(Path.GetTempPath(), "bl-account-screen.txt"),
            "NET_USER_EXIT=" + process.ExitCode + "\r\n" + text);
    }

    [EnvironmentFact("LAUNCHPAD_CREATE_ACCOUNT", "1")]
    [Trait("Category", "Integration")]
    public void CreateLaunchAccountWhenRequested()
    {
        if (Environment.GetEnvironmentVariable("LAUNCHPAD_CREATE_ACCOUNT") != "1")
            return;

        var ok = LaunchAccountSetup.TryCreate(out var message);
        Assert.True(ok, message);
    }

    [EnvironmentFact("LAUNCHPAD_SERIAL_PROOF")]
    [Trait("Category", "Integration")]
    public void ProofGuestWarningsFromSerialLog()
    {
        var path = Environment.GetEnvironmentVariable("LAUNCHPAD_SERIAL_PROOF");
        if (string.IsNullOrWhiteSpace(path))
            return;

        var kept = GuestWarnings.Keep(File.ReadAllText(path));
        var joined = string.Join("\n", kept);
        var outPath = Environment.GetEnvironmentVariable("LAUNCHPAD_WARNING_OUT");
        if (!string.IsNullOrWhiteSpace(outPath))
            File.WriteAllText(outPath, joined);
        Assert.Contains("WARN fence-ready", kept);
        Assert.Equal(0, GuestWarnings.Hex64Count(joined));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void LaunchAccountIsAStandardUser()
    {
        var user = ReadProcess("net.exe", "user " + TestUserRunner.UserName);
        Assert.Contains("User name", user, StringComparison.Ordinal);
        Assert.Contains(TestUserRunner.UserName, user, StringComparison.Ordinal);
        Assert.Contains("*Users", user, StringComparison.Ordinal);
        Assert.DoesNotContain("Administrators", user, StringComparison.Ordinal);
        Assert.DoesNotContain("builder", user, StringComparison.OrdinalIgnoreCase);

        var admins = ReadProcess("net.exe", "localgroup Administrators");
        Assert.DoesNotContain(TestUserRunner.UserName, admins, StringComparison.OrdinalIgnoreCase);
        var users = ReadProcess("net.exe", "localgroup Users");
        Assert.Contains(TestUserRunner.UserName, users, StringComparison.OrdinalIgnoreCase);

        var outFile = @"C:\Users\Public\bl-whoami.txt";
        if (File.Exists(outFile))
            File.Delete(outFile);
        var started = TestUserRunner.TryStart(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Environment.SystemDirectory,
            new[] { "/c", "whoami > " + outFile },
            out var process);
        Assert.True(started);
        Assert.NotNull(process);
        process!.WaitForExit(20_000);
        var who = File.ReadAllText(outFile);
        File.Delete(outFile);
        Assert.Contains(TestUserRunner.UserName, who, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, who, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IgnoredWeightsStayOffTheCopyAndTheRocketUsesBytesSent()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "weights"));
            Directory.CreateDirectory(Path.Combine(root, "src"));
            Directory.CreateDirectory(Path.Combine(root, "notes"));
            File.WriteAllText(Path.Combine(root, "weights", "big.bin"), "weights");
            File.WriteAllText(Path.Combine(root, "src", "a.txt"), "source");
            File.WriteAllText(Path.Combine(root, "notes", "n.txt"), "note");
            File.WriteAllText(Path.Combine(root, ".gitignore"), "weights/\n.git\n");
            Assert.True(SendList.InitRepo(root));

            var sent = SendList.Collect(root, false);
            Assert.Contains(sent, file => file.Relative == "src/a.txt");
            Assert.Contains(sent, file => file.Relative == "notes/n.txt");
            Assert.DoesNotContain(sent, file => file.Relative.StartsWith("weights/", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(sent, file => file.Relative.StartsWith(".git/", StringComparison.OrdinalIgnoreCase));
            Assert.False(CopyMath.IsLarge(sent.Sum(file => file.Length)));

            SendList.AddIgnores(root, new[] { "notes" });
            var after = SendList.Collect(root, false);
            Assert.DoesNotContain(after, file => file.Relative.StartsWith("notes/", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("/notes/", File.ReadAllText(Path.Combine(root, ".gitignore")), StringComparison.Ordinal);

            var everything = SendList.Collect(root, true);
            Assert.Contains(everything, file => file.Relative == "weights/big.bin");

            Assert.False(CopyMath.IsLarge(CopyMath.LargeBytes));
            Assert.True(CopyMath.IsLarge(CopyMath.LargeBytes + 1));
            var emptyRocket = RocketPicture.Lines(0);
            var fullRocket = RocketPicture.Lines(100);
            Assert.True(emptyRocket.Count >= 12);
            Assert.True(emptyRocket.All(line => line.Length == emptyRocket[0].Length && line.Length >= 15));
            Assert.Contains("****************", emptyRocket[0], StringComparison.Ordinal);
            Assert.Equal(emptyRocket[0], fullRocket[0]);
            Assert.DoesNotContain("o", string.Join("", RocketPicture.Mask(0)), StringComparison.Ordinal);
            Assert.Contains("e", string.Join("", RocketPicture.Mask(0)), StringComparison.Ordinal);
            Assert.DoesNotContain("e", string.Join("", RocketPicture.Mask(100)), StringComparison.Ordinal);
            Assert.Equal(0, RocketPicture.FilledCount(0));
            Assert.Equal(RocketPicture.FillableCount, RocketPicture.FilledCount(100));
            Assert.True(RocketPicture.FillableCount > 0);
            var percent = CopyMath.Percent(65536, 262144);
            Assert.Equal(25, percent);
            Assert.Equal(RocketPicture.FillableCount * percent / 100, RocketPicture.FilledCount(percent));
            Assert.Null(CopyMath.Remaining(65536, 262144, TimeSpan.FromMilliseconds(500)));
            var wide = RocketView.Frame(0, 100, 1, TimeSpan.Zero, 200, 60);
            var halfNose = RocketView.Half(RocketPicture.Lines(0))[0].Trim();
            Assert.Equal("****************", halfNose);
            Assert.Contains(halfNose, wide, StringComparison.Ordinal);
            Assert.DoesNotContain("..::-----::..", wide, StringComparison.Ordinal);
            var frame = RocketView.Frame(65536, 262144, 2, TimeSpan.FromSeconds(2), 48, 30);
            Assert.Contains("Copying what changed.", frame, StringComparison.Ordinal);
            Assert.DoesNotContain("takes a minute", frame, StringComparison.Ordinal);
            Assert.Contains("about ", frame, StringComparison.Ordinal);
            Assert.Contains("240;120;40", frame, StringComparison.Ordinal);
            Assert.Contains("233;228;220", frame, StringComparison.Ordinal);
            Assert.Contains("[12;1H", frame, StringComparison.Ordinal);
            Assert.DoesNotContain("[0m", frame, StringComparison.Ordinal);
            Assert.Contains("?1049h", RocketView.OpenScreen(), StringComparison.Ordinal);
            Assert.True(RocketView.TryRead("65536\t262144\t2\tsrc/a.txt", out var readSent, out var readTotal, out var readFiles, out var name));
            Assert.Equal(65536, readSent);
            Assert.Equal(262144, readTotal);
            Assert.Equal(2, readFiles);
            Assert.Equal("src/a.txt", name);
            Assert.Equal(25, CopyMath.Percent(readSent, readTotal));

            var main = File.ReadAllText(RepoFile("src", "LaunchPad", "MainWindow.axaml.cs"));
            var open = main[main.IndexOf("public async void OpenProject", StringComparison.Ordinal)..];
            Assert.Contains("CopyMath.IsLarge", open, StringComparison.Ordinal);
            Assert.Contains("LargeCopyWindow", open, StringComparison.Ordinal);
            Assert.DoesNotContain("EnsureVmReadyAsync", open, StringComparison.Ordinal);

            var host = File.ReadAllText(RepoFile("src", "LaunchPad", "Services", "Fence", "FenceHost.cs"));
            var loop = host.IndexOf("while (left > 0)", StringComparison.Ordinal);
            Assert.True(loop > 0);
            Assert.Contains("SendList.Collect", host, StringComparison.Ordinal);
            Assert.True(host.IndexOf("WriteProgress", loop, StringComparison.Ordinal) > loop);

            var tui = File.ReadAllText(RepoFile("src", "LaunchPad", "Services", "Fence", "TuiWindow.cs"));
            Assert.Contains("RocketView.Frame", tui, StringComparison.Ordinal);
            Assert.DoesNotContain("PadRight(78)", tui, StringComparison.Ordinal);

            var finish = File.ReadAllText(RepoFile("src", "LaunchPad", "Services", "Fence", "CopyProgressLine.cs"));
            Assert.Contains("[2J", finish, StringComparison.Ordinal);
            Assert.Contains("?1049l", finish, StringComparison.Ordinal);

        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void TheTerminalOwnsTheMachineAndClosingTheAppDoesNot()
    {
        var runner = File.ReadAllText(RepoFile("src", "LaunchPad", "Services", "Fence", "TestUserRunner.cs"));
        var hand = runner.IndexOf("public static bool HandMachineTo", StringComparison.Ordinal);
        var release = runner.IndexOf("public static void ReleaseMachine", hand, StringComparison.Ordinal);
        var handBody = runner[hand..release];
        var duplicate = handBody.IndexOf("DuplicateInto", StringComparison.Ordinal);
        var arm = handBody.IndexOf("SetKillOnClose", StringComparison.Ordinal);
        Assert.True(duplicate >= 0 && arm > duplicate);
        Assert.Equal(144, TestUserRunner.ExtendedJobLimitBytes);

        var session = File.ReadAllText(RepoFile("src", "LaunchPad", "Services", "Fence", "FenceSession.cs"));
        var sweep = session.IndexOf("SessionSweep.StopAbandoned", StringComparison.Ordinal);
        var launch = session.IndexOf("TestUserRunner.TryStart", StringComparison.Ordinal);
        Assert.True(sweep > 0 && sweep < launch);
        var watch = session.IndexOf("private void WatchTui", StringComparison.Ordinal);
        var handed = session.IndexOf("SessionGuardian.StartAsync", StringComparison.Ordinal);
        var waited = session.IndexOf("WaitForConsole", watch, StringComparison.Ordinal);
        Assert.True(handed > launch && handed < watch && waited > watch);
        var guardian = File.ReadAllText(RepoFile("src", "LaunchPad", "Services", "Fence", "SessionGuardian.cs"));
        Assert.Contains("TestUserRunner.HandMachineTo", guardian, StringComparison.Ordinal);
        Assert.Contains("MachineShutdown.WaitForGuestExit", guardian, StringComparison.Ordinal);
        Assert.Contains("console.done", session, StringComparison.Ordinal);

        var tui = File.ReadAllText(RepoFile("src", "LaunchPad", "Services", "Fence", "TuiWindow.cs"));
        var copy = tui.IndexOf("CopyToConsole", StringComparison.Ordinal);
        var end = tui.IndexOf("SessionEnd.AfterConsole", copy, StringComparison.Ordinal);
        Assert.True(copy > 0 && end > copy);

        var now = new DateTime(2026, 10, 4, 18, 0, 0, DateTimeKind.Utc);
        Assert.True(SessionEnd.HostStillWatching(now.AddSeconds(-1), now));
        Assert.False(SessionEnd.HostStillWatching(now.AddSeconds(-5), now));

        var root = @"C:\Users\Big Bojangles\AppData\Local\Programs\LaunchPad\sessions";
        var ours = "qemu-system-x86_64.exe -drive file=" + root + @"\EB9B427956C4ACF0\session.qcow2";
        var other = @"C:\Tools\qemu-system-x86_64.exe -drive file=D:\other\session.qcow2";
        Assert.True(SessionSweep.ShouldStop(ours, root, false));
        Assert.False(SessionSweep.ShouldStop(ours, root, true));
        Assert.False(SessionSweep.ShouldStop(other, root, false));
        Assert.False(SessionSweep.ShouldStop("", root, false));

        var disk = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N") + ".qcow2");
        try
        {
            using var held = new FileStream(disk, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            held.WriteByte(1);
            var locking = SessionSweep.LockingPids(disk);
            Assert.Contains(Environment.ProcessId, locking);
        }
        finally
        {
            try
            {
                File.Delete(disk);
            }
            catch
            {
                // The lock test already finished with this file.
            }
        }
    }

    [Fact]
    public void AFolderGitignoreLeavesThatFolderOutBeforeGitExists()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "weights"));
            Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "weights", "big.bin"), "weights");
            File.WriteAllText(Path.Combine(root, "src", "a.txt"), "source");
            SendList.AddIgnores(root, new[] { "weights" });

            var sent = SendList.Collect(root, false);
            Assert.Contains(sent, file => file.Relative == "src/a.txt");
            Assert.DoesNotContain(sent, file => file.Relative.StartsWith("weights/", StringComparison.OrdinalIgnoreCase));
            Assert.False(CopyMath.IsLarge(sent.Sum(file => file.Length)));
            Assert.False(Directory.Exists(Path.Combine(root, ".git")));
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public void FirstCopySaysItTakesAMinuteAndLaterCopiesSayWhatChanged()
    {
        var full = RocketPicture.Lines(0);
        var half = RocketView.Half(full);
        Assert.Equal((full.Count + 1) / 2, half.Count);
        var first = full[0];
        var expected = new string(Enumerable.Range(0, (first.Length + 1) / 2).Select(column => first[column * 2]).ToArray());
        Assert.Equal(expected, half[0]);
        var halfMask = RocketView.Half(RocketPicture.Mask(0));
        Assert.Equal(half.Count, halfMask.Count);
        Assert.Equal(half[0].Length, halfMask[0].Length);
        for (var index = 0; index < half[0].Length; index++)
            Assert.Equal(half[0][index] != ' ', halfMask[0][index] != ' ');

        var opening = RocketView.Frame(0, 100, 1, TimeSpan.Zero, 200, 60, firstCopy: true);
        Assert.Contains(SealText.WarmingUp, opening, StringComparison.Ordinal);
        Assert.DoesNotContain(SealText.VmLaunching, opening, StringComparison.Ordinal);
        var copied = RocketView.Frame(100, 100, 1, TimeSpan.FromSeconds(1), 200, 60);
        Assert.Contains(SealText.VmLaunching, copied, StringComparison.Ordinal);
        Assert.DoesNotContain(SealText.BlastOff, copied, StringComparison.Ordinal);
        Assert.Contains("Copying your project into the sandbox.", opening, StringComparison.Ordinal);
        Assert.Contains("First open, so this one takes a minute.", opening, StringComparison.Ordinal);
        Assert.Contains("\u001b[2m", opening, StringComparison.Ordinal);
        var later = RocketView.Frame(0, 100, 1, TimeSpan.Zero, 200, 60);
        Assert.Contains("Copying what changed.", later, StringComparison.Ordinal);
        Assert.DoesNotContain("Copying your project into the sandbox.", later, StringComparison.Ordinal);
        Assert.DoesNotContain("takes a minute", later, StringComparison.Ordinal);

        Assert.True(RocketView.TryRead("1\t2\t3\tsrc/a.txt\t1", out _, out _, out _, out var name, out var firstCopy));
        Assert.Equal("src/a.txt", name);
        Assert.True(firstCopy);
        Assert.True(RocketView.TryRead("1\t2\t3\tsrc/a.txt", out _, out _, out _, out _, out var missingFlag));
        Assert.False(missingFlag);

        var host = File.ReadAllText(RepoFile("src", "LaunchPad", "Services", "Fence", "FenceHost.cs"));
        Assert.Contains("!manifest.Any", host, StringComparison.Ordinal);
        Assert.Contains("firstCopy ? \"1\" : \"0\"", host, StringComparison.Ordinal);
    }

    [Fact]
    public void MachineDefaultsToFourGigabytesAndTwoCores()
    {
        Assert.Equal(4096, GuestMemory.ChooseMegabytes(0, 16384));
        Assert.Equal(4096, GuestMemory.ChooseMegabytes(1024, 16384));
        Assert.Equal(6144, GuestMemory.ChooseMegabytes(6144, 16384));
        Assert.Equal(14336, GuestMemory.ChooseMegabytes(20000, 16384));
        Assert.Equal(2048, GuestMemory.ChooseMegabytes(0, 3000));
        Assert.Equal(2048, GuestMemory.ChooseMegabytes(8192, 4096));
        Assert.Equal(2, GuestMemory.ChooseCores(0, 8));
        Assert.Equal(4, GuestMemory.ChooseCores(4, 8));
        Assert.Equal(8, GuestMemory.ChooseCores(16, 8));
        Assert.Equal(1, GuestMemory.ChooseCores(4, 1));
        Assert.Equal(8192, GuestMemory.ChooseProjectMegabytes(8192, 4096, 16384));
        Assert.Equal(4096, GuestMemory.ChooseProjectMegabytes(0, 0, 16384));
        Assert.Equal(6144, GuestMemory.ChooseProjectMegabytes(0, 6144, 16384));
        Assert.Equal(14336, GuestMemory.ChooseProjectMegabytes(20000, 4096, 16384));

        var defaults = string.Join("\n", QemuCommand.Build("whpx", @"C:\fence\session.qcow2", 0, 4444, "fence", null));
        Assert.Contains("-m\n4096", defaults, StringComparison.Ordinal);
        Assert.Contains("-smp\n2", defaults, StringComparison.Ordinal);
        var chosen = string.Join("\n", QemuCommand.Build("whpx", @"C:\fence\session.qcow2", 0, 4444, "fence", null, memoryMb: 8192, cores: 4));
        Assert.Contains("-m\n8192", chosen, StringComparison.Ordinal);
        Assert.Contains("-smp\n4", chosen, StringComparison.Ordinal);

        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        var saved = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{"machineMemoryMb":8192,"machineCores":6}""", options);
        Assert.NotNull(saved);
        Assert.Equal(8192, saved!.MachineMemoryMb);
        Assert.Equal(6, saved.MachineCores);
        var blank = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}", options);
        Assert.NotNull(blank);
        Assert.Equal(0, blank!.MachineMemoryMb);
        Assert.Equal(0, blank.MachineCores);

        var home = File.ReadAllText(RepoFile("src", "LaunchPad", "Views", "HomeView.axaml"));
        Assert.Contains("Machine", home, StringComparison.Ordinal);
        var dialog = File.ReadAllText(RepoFile("src", "LaunchPad", "Views", "MachineWindow.axaml"));
        Assert.Contains("Memory", dialog, StringComparison.Ordinal);
        Assert.Contains("Cores", dialog, StringComparison.Ordinal);
        var window = File.ReadAllText(RepoFile("src", "LaunchPad", "Views", "MachineWindow.axaml.cs"));
        Assert.Contains("MachineMemoryMb", window, StringComparison.Ordinal);
        Assert.Contains("MachineCores", window, StringComparison.Ordinal);

        var session = File.ReadAllText(RepoFile("src", "LaunchPad", "Services", "Fence", "FenceSession.cs"));
        Assert.Contains("ChooseProjectMegabytes", session, StringComparison.Ordinal);
    }

    [Fact]
    public void ARenamedProjectKeepsItsOwnMemory()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N"));
        var oldPath = Path.Combine(root, "Old");
        var newPath = Path.Combine(root, "Next");
        Directory.CreateDirectory(oldPath);
        try
        {
            var paths = new AppPaths(userProfile: root, appDataDir: Path.Combine(root, "appdata"));
            var settings = new SettingsStore(paths);
            settings.RememberProject("Old", oldPath);
            settings.SaveProjectMemory(oldPath, 8192);
            settings.Current.MachineMemoryMb = 4096;
            settings.SaveSettings();

            Assert.Equal(8192, settings.ProjectMemoryMbFor(oldPath));
            Assert.Equal(0, settings.ProjectMemoryMbFor(Path.Combine(root, "missing")));

            settings.UpdateProjectPath(oldPath, "Next", newPath);
            Assert.Equal(0, settings.ProjectMemoryMbFor(oldPath));
            Assert.Equal(8192, settings.ProjectMemoryMbFor(newPath));

            var reloaded = new SettingsStore(paths);
            Assert.Equal(8192, reloaded.ProjectMemoryMbFor(newPath));
            Assert.Equal(4096, reloaded.Current.MachineMemoryMb);
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public void OldConfigStopsForRecoveryWithoutDiscardingPersistentState()
    {
        Assert.False(ConfigHeal.LogShowsBrokenConfig(""));
        Assert.False(ConfigHeal.LogShowsBrokenConfig("AUTH-IN 2\n"));
        Assert.False(ConfigHeal.LogShowsBrokenConfig("AUTH-IN 2\nGROK-HOME builder\nWARN fence-ready\n"));
        Assert.True(ConfigHeal.LogShowsBrokenConfig("AUTH-IN 2\nWARN fence-ready\n"));
        Assert.False(ConfigHeal.LogShowsBrokenConfig("AUTH-IN 2\nWARN fence-ready\nGROK-HOME builder\n"));

        var root = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var image = Path.Combine(root, "disk.bin");
            File.WriteAllBytes(image, System.Text.Encoding.ASCII.GetBytes("prefix GROK-HOME builder suffix"));
            Assert.True(ConfigHeal.ImageHasConfigFix(image));
            File.WriteAllBytes(image, System.Text.Encoding.ASCII.GetBytes("GROK-HOME build"));
            Assert.False(ConfigHeal.ImageHasConfigFix(image));
            Assert.False(ConfigHeal.ImageHasConfigFix(Path.Combine(root, "missing.bin")));

            using (var stream = File.Create(image))
            {
                var head = new byte[ConfigHeal.SearchBytes - 6];
                Array.Fill(head, (byte)'x');
                stream.Write(head);
                stream.Write(System.Text.Encoding.ASCII.GetBytes(ConfigHeal.Marker));
            }

            Assert.True(ConfigHeal.ImageHasConfigFix(image));

            var session = Path.Combine(root, "session");
            Directory.CreateDirectory(session);
            File.WriteAllText(Path.Combine(session, "session.qcow2"), "disk");
            File.WriteAllText(Path.Combine(session, "sent.manifest"), "1\t2\ta.txt");
            File.WriteAllText(Path.Combine(session, "serial.log"), "keep");
            var error = Assert.Throws<InvalidOperationException>(() => ConfigHeal.StopForRecovery(session));
            Assert.Contains("preserved for recovery", error.Message, StringComparison.Ordinal);
            Assert.Contains(Path.GetFullPath(session), error.Message, StringComparison.Ordinal);
            Assert.Equal("disk", File.ReadAllText(Path.Combine(session, "session.qcow2")));
            Assert.Equal("1\t2\ta.txt", File.ReadAllText(Path.Combine(session, "sent.manifest")));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(session, "serial.log")));

            var source = File.ReadAllText(RepoFile("src", "LaunchPad", "Services", "Fence", "FenceSession.cs"));
            Assert.Contains("Path.Combine(QemuLayout.Root, \"sessions\")", source, StringComparison.Ordinal);
            Assert.DoesNotContain("LaunchSessionsRoot", source, StringComparison.Ordinal);
            Assert.Contains("ConfigHeal.StopForRecovery(sessionDir)", source, StringComparison.Ordinal);
            Assert.DoesNotContain("ForgetSessionDisk", source, StringComparison.Ordinal);
            Assert.DoesNotContain("allowHeal: false", source, StringComparison.Ordinal);
            var heal = source.IndexOf("ConfigStayedBroken", StringComparison.Ordinal);
            var watch = source.IndexOf("WatchTui(", StringComparison.Ordinal);
            Assert.True(heal > 0 && watch > heal);

            var app = File.ReadAllText(RepoFile("src", "LaunchPad", "Program.cs"));
            var release = app.IndexOf("--release-install", StringComparison.Ordinal);
            var tui = app.IndexOf("TuiWindow.IsRequest", StringComparison.Ordinal);
            Assert.True(release > 0 && tui > release);
            Assert.Contains("QemuLayout.Root", app, StringComparison.Ordinal);

            var prepare = File.ReadAllText(RepoFile("installer", "SetupPrepare.ps1"));
            var prepareRelease = prepare.IndexOf("--release-install", StringComparison.Ordinal);
            var earlyExit = prepare.IndexOf("exit 0", StringComparison.Ordinal);
            Assert.True(prepareRelease > 0 && earlyExit > prepareRelease);
            Assert.DoesNotContain("qemu-system", prepare, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    public async Task CompletedImportWaitsForAgentStartupBeforeRequestingRecovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var serial = Path.Combine(root, "serial.log");
            File.WriteAllText(serial, "WARN fence-ready\n");
            var check = typeof(FenceSession).GetMethod("AgentScriptMissing",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var pending = (Task<bool>)check.Invoke(null, new object[] { serial, cancellation.Token })!;
            // The old check completed synchronously on the import marker,
            // incorrectly rejecting the guest before its child could start.
            Assert.False(pending.IsCompleted);
            File.AppendAllText(serial, "AGENT-PICK codex\n");
            Assert.False(await pending);
        }
        finally { cancellation.Cancel(); DeleteTree(root); }
    }

    [Fact]
    public void AgentChoiceIsOneCliAndTheWindowsFolderStaysInsideItsRoot()
    {
        Assert.Equal("grok", AgentChoice.Normalize(null));
        Assert.Equal("codex", AgentChoice.Normalize("codex"));
        Assert.Equal("claude", AgentChoice.Normalize("claude"));
        Assert.Equal("AGENT codex\n", AgentChoice.Header("codex"));
        Assert.Equal("AGENT-CMD opencode\n", AgentChoice.ProgramHeader("opencode"));
        Assert.True(AgentChoice.SafeProgram("claude"));
        Assert.False(AgentChoice.SafeProgram("cmd.exe /c"));
        Assert.False(AgentChoice.SafeProgram("../grok"));

        var root = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var folder = Path.Combine(root, "windows");
            Directory.CreateDirectory(folder);
            Assert.True(WindowsWork.TryResolve(folder, "tool", out var inside));
            Assert.StartsWith(folder, inside, StringComparison.OrdinalIgnoreCase);
            Assert.False(WindowsWork.TryResolve(folder, "..\\secret", out _));
            Assert.True(WindowsWork.IsShell(Path.Combine(folder, "cmd.exe")));
            Assert.True(WindowsWork.IsShell(Path.Combine(folder, "powershell.exe")));
            Assert.False(WindowsWork.IsShell(Path.Combine(folder, "codec.exe")));

        }
        finally
        {
            DeleteTree(root);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void PreparedGuestScriptContainsBundledAgentEntrypoints()
    {
        var script = File.ReadAllText(Path.Combine(QemuLayout.Root, "images", "prepare", "bl-proof.sh"));
        Assert.Contains("/usr/local/bin/codex", script, StringComparison.Ordinal);
        Assert.Contains("/usr/local/bin/claude", script, StringComparison.Ordinal);
        Assert.Contains("AGENT-PICK", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Codex.app", script, StringComparison.Ordinal);
    }

    private static void DeleteTree(string root)
    {
        if (!Directory.Exists(root))
            return;

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }

    private static string ReadProcess(string fileName, string arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = System.Diagnostics.Process.Start(start);
        var text = process!.StandardOutput.ReadToEnd();
        process.WaitForExit(10_000);
        return text;
    }

    private static HashSet<int> ProcessIds(string name)
    {
        var ids = new HashSet<int>();
        foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
        {
            ids.Add(process.Id);
            process.Dispose();
        }

        return ids;
    }
}
