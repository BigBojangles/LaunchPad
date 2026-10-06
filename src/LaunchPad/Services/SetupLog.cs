namespace LaunchPad.Services;

public sealed class SetupLog
{
    private readonly string _path;
    private readonly object _gate = new();

    public SetupLog(AppPaths paths)
    {
        _path = paths.SetupLogFile;
    }

    public void Write(string message)
    {
        try
        {
            lock (_gate)
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
                File.AppendAllText(_path, line);
            }
        }
        catch
        {
            // Logging must never break the app.
        }
    }
}
