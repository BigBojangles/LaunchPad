using System.Runtime.InteropServices;

namespace LaunchPad.Services;

public sealed class ShortcutService
{
    private readonly AppPaths _paths;
    private readonly SetupLog _log;

    public ShortcutService(AppPaths paths, SetupLog log)
    {
        _paths = paths;
        _log = log;
    }

    public bool DesktopShortcutExists => File.Exists(_paths.DesktopShortcutPath);
    public bool StartMenuShortcutExists => File.Exists(_paths.StartMenuShortcutPath);
    public bool AnyShortcutExists => DesktopShortcutExists || StartMenuShortcutExists;

    public void CreateRequested(bool desktop, bool startMenu)
    {
        if (desktop)
            CreateShortcut(_paths.DesktopShortcutPath);
        if (startMenu)
            CreateShortcut(_paths.StartMenuShortcutPath);
    }

    private void CreateShortcut(string shortcutPath)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("Windows shortcuts require Windows.");
            var directory = Path.GetDirectoryName(shortcutPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
                throw new InvalidOperationException("WScript.Shell is not available.");

            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                var shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = _paths.ExePath;
                shortcut.WorkingDirectory = _paths.ExeDirectory;
                shortcut.WindowStyle = 1;
                shortcut.Description = "LaunchPad";
                shortcut.IconLocation = _paths.ExePath + ",0";
                shortcut.Save();
            }
            finally
            {
                if (shell is not null)
                    Marshal.FinalReleaseComObject(shell);
            }

            _log.Write("Created shortcut: " + shortcutPath);
        }
        catch (Exception ex)
        {
            _log.Write("Shortcut failed: " + ex.Message);
            throw;
        }
    }
}
