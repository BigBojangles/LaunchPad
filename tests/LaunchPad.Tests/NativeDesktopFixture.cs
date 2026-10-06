using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LaunchPad.Models;
using LaunchPad.Services;
using LaunchPad.Views;

namespace LaunchPad.Tests;

// Opt-in test executable only. The shipping app has no diagnostic command.
public static class NativeDesktopFixture
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private static string _root = "";

    [STAThread]
    public static int Main(string[] args)
    {
        if (args is not ["--owned-native-desktop", var directory] || !OperatingSystem.IsWindows()) return 2;
        _root = Path.GetFullPath(directory);
        var allowed = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults");
        if (!_root.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(_root)) return 2;
        try { return AppBuilder.Configure<OwnedApplication>().UsePlatformDetect().StartWithClassicDesktopLifetime([]); }
        catch (Exception error) { File.WriteAllText(Path.Combine(_root, "desktop-error-private.txt"), error.ToString()); return 1; }
    }

    internal static Process Start(string root)
    {
        Directory.CreateDirectory(root);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet.exe")
        { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "LaunchPad.Tests.dll"));
        start.ArgumentList.Add("--owned-native-desktop");
        start.ArgumentList.Add(root);
        return Process.Start(start) ?? throw new IOException("The owned native desktop did not start.");
    }

    internal static DesktopState? Read(string root)
    {
        try
        {
            using var input = new FileStream(Path.Combine(root, "desktop-state.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<DesktopState>(input, Json);
        }
        catch (Exception error) when (error is IOException or JsonException) { return null; }
    }

    private sealed class OwnedApplication : LaunchPad.App
    {
        public OwnedApplication() { }
        private string _project = "";
        private DispatcherTimer? _snapshots;
        protected override AppPaths CreatePaths() => new(userProfile: _root, grokHome: Path.Combine(_root, "grok"),
            grokBin: Path.Combine(_root, "grok", "bin"), appDataDir: Path.Combine(_root, "settings"),
            exePath: WindowsConsoleProbeTests.LaunchPadExecutable());
        protected override AppServices CreateServices(AppPaths paths)
        {
            var settings = new SettingsStore(paths);
            settings.Current.OnboardingCompleted = true;
            settings.Current.ShortcutPromptShown = true;
            settings.SaveSettings();
            _project = Path.Combine(paths.ProjectsRoot, "Native project");
            Directory.CreateDirectory(_project);
            settings.RememberProject("Native project", _project);
            var log = new SetupLog(paths);
            var locator = new GrokLocator(paths);
            return new AppServices(paths, settings, new ProjectCatalog(paths, settings), locator,
                new GrokSetup(paths, locator, log), new ProjectLauncher(locator, log), new ShortcutService(paths, log), log,
                applicationSetup: new OwnedSetup());
        }
        public override void OnFrameworkInitializationCompleted()
        {
            base.OnFrameworkInitializationCompleted();
            _snapshots = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _snapshots.Tick += (_, _) => Snapshot();
            _snapshots.Start();
        }
        private void Snapshot()
        {
            if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime lifetime) return;
            if (lifetime.MainWindow is SettingsRecoveryWindow recovery)
            {
                SaveState(new DesktopState(Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
                    recovery.TryGetPlatformHandle()?.Handle.ToInt64() ?? 0, false, "", false, false, false, "", false, false, false, [], [], Recovery: true));
                Capture(recovery, "recovery-native.png");
                return;
            }
            if (lifetime.MainWindow is not LaunchPad.MainWindow main) return;
            var view = main.FindControl<ContentControl>("ProjectsHost")?.Content as ExistingProjectsView;
            var editor = view?.GetVisualDescendants().OfType<EditableDisplayName>().SingleOrDefault();
            var row = view?.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.Classes.Contains("ProjectBubble"));
            var preferences = lifetime.Windows.OfType<SettingsWindow>().FirstOrDefault();
            var controls = new Dictionary<string, DesktopControl>();
            void Point(string name, Control? control)
            {
                if (control is null || !control.IsEffectivelyVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0) return;
                var window = TopLevel.GetTopLevel(control);
                var handle = window?.TryGetPlatformHandle()?.Handle;
                if (handle is null || handle == 0) return;
                var point = control.PointToScreen(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2));
                controls[name] = new(handle.Value.ToInt64(), point.X, point.Y);
            }
            Point("settings", main.FindControl<Button>("SettingsButton"));
            Point("close", main.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => Equals(ToolTip.GetTip(button), "Close")));
            Point("name", editor?.FindControl<Button>("CaptionButton"));
            Point("editor", editor?.FindControl<TextBox>("Editor"));
            Point("resetName", editor?.FindControl<Button>("ResetButton"));
            Point("menu", row?.GetVisualDescendants().OfType<Button>().FirstOrDefault(button => button.Classes.Contains("menuButton")));
            Point("tips", preferences?.FindControl<CheckBox>("TipsSwitch"));
            Point("agent", preferences?.FindControl<ComboBox>("DefaultAgentBox"));
            Point("save", preferences?.FindControl<Button>("SaveButton"));
            var state = new DesktopState(Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks,
                main.TryGetPlatformHandle()?.Handle.ToInt64() ?? 0, view is not null, Services.Settings.DisplayNameFor(_project),
                editor?.IsEditing == true, preferences is not null, Services.Settings.Current.ShowTips, Services.Settings.Current.DefaultAgent,
                Services.Settings.Current.SeenTips.Contains("project-menu"), view?.FindControl<TextBlock>("TipText")?.IsVisible == true,
                row?.ContextMenu?.IsOpen == true, row?.ContextMenu?.Items.OfType<MenuItem>().Select(item => item.Header?.ToString() ?? "").ToArray() ?? [], controls);
            SaveState(state);
            if (state.HomeReady) Capture(main, "home-native.png");
            if (editor?.IsEditing == true) Capture(main, "name-edit-native.png");
            if (preferences is not null) Capture(preferences, "settings-native.png");
            if (state.DisplayName == "Native renamed project") Capture(main, "home-renamed-native.png");
        }
        private static void SaveState(DesktopState state)
        {
            var path = Path.Combine(_root, "desktop-state.json");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, Json));
            File.Move(temporary, path, overwrite: true);
        }
        private static void Capture(Window window, string name)
        {
            var path = Path.Combine(_root, name);
            if (File.Exists(path) || window.Bounds.Width <= 0 || window.Bounds.Height <= 0) return;
            using var bitmap = new RenderTargetBitmap(new PixelSize((int)window.Bounds.Width, (int)window.Bounds.Height), new Vector(96, 96));
            bitmap.Render(window); bitmap.Save(path);
        }
    }
    private sealed class OwnedSetup : IApplicationSetup
    {
        public async Task<SetupStatus> EnsureReadyAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(400, cancellationToken);
            return new(SetupPhase.Ready, "Owned desktop fixture ready");
        }
    }
}

internal sealed record DesktopControl(long Window, int X, int Y);
internal sealed record DesktopState(int Pid, long StartTicks, long Window, bool HomeReady, string DisplayName, bool Editing,
    bool PreferencesOpen, bool ShowTips, string DefaultAgent, bool MenuTipSeen, bool MenuTipVisible, bool MenuOpen,
    string[] MenuCaptions, Dictionary<string, DesktopControl> Controls, bool Recovery = false);
