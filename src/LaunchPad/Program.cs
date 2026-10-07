using Avalonia;
using LaunchPad.Services.Fence;
using LaunchPad.Services;

namespace LaunchPad;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (RuntimeActivation.IsRequest(args))
        {
            if (args.Length != 2) return 87;
            if (!OperatingSystem.IsWindows()) return 50;
            try
            {
                if (!TestUserRunner.CheckStoredCredential().Ready) return 1;
                RuntimeActivation.Activate(AppContext.BaseDirectory, args[1]);
                return 0;
            }
            catch (Exception error)
            {
                new SetupLog(new AppPaths()).Write("Installed runtime activation failed; previous selection preserved: " + error);
                return 1;
            }
        }
        if (NativeGrokActivity.IsRequest(args)) return NativeGrokActivity.RunCallback(args);
        if (NativeClaudeActivity.IsRequest(args)) return NativeClaudeActivity.RunCallback(args);
        if (NotificationDeliveryOwner.IsRequest(args)) return NotificationDeliveryOwner.Run(args);
        if (WindowsRuntimeRepair.IsRequest(args))
        {
            try
            {
                if (!OperatingSystem.IsWindows()) return 50;
                var account = TestUserRunner.CheckStoredCredential();
                if (!account.Ready) return 1;
                var root = AppContext.BaseDirectory;
                WindowsRuntimeRepair.Apply(WindowsRuntimeRepair.Plan(root, !File.Exists(Path.Combine(root, "native-only.txt"))));
                return 0;
            }
            catch { return 1; }
        }
        if (OperatingSystem.IsWindows() && InteractiveTestDesktop.IsGuardRequest(args)) return InteractiveTestDesktop.RunGuard(args);
        if (NativeAgentTerminal.IsRequest(args)) return NativeAgentTerminal.Run(args);
        if (OperatingSystem.IsWindows() && WindowsHostNetworkBoundary.IsPreparationRequest(args))
        {
            try { WindowsHostNetworkBoundary.PrepareRuntime(args[1]); return 0; }
            catch (Exception error)
            {
                new SetupLog(new AppPaths()).Write("Host-network setup failed: " + error);
                return 1;
            }
        }
        if (RestrictedHostLaunch.IsRequest(args)) return RestrictedHostLaunch.Run(args);
        // Terminal mode must work without starting a desktop UI or owning its VM.
        if (WindowsSessionWindow.IsRequest(args))
        {
            WindowsSessionWindow.Run(args);
            return 0;
        }
        if (args.Contains("--release-install", StringComparer.OrdinalIgnoreCase))
        {
            SessionSweep.StopAbandoned(Path.Combine(QemuLayout.Root, "sessions"));
            return 0;
        }
        if (TuiWindow.IsRequest(args))
        {
            TuiWindow.Run(args);
            return 0;
        }
        if (SessionGuardian.IsRequest(args))
        {
            SessionGuardian.Run(args);
            return 0;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
