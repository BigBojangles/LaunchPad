namespace LaunchPad.Services.Fence;

/// <summary>Retains the existing ready-before-resize ordering in either owner.</summary>
public static class SessionSizeRelay
{
    public static async Task RunAsync(string directory, int qmpPort, CancellationToken token)
    {
        ConsoleSizeLink? monitor = null;
        try
        {
            monitor = await ConsoleSizeLink.ConnectAsync(qmpPort, token).ConfigureAwait(false);
            var rows = 0; var cols = 0;
            while (!token.IsCancellationRequested)
            {
                if (monitor is not null && File.Exists(Path.Combine(directory, "console.ready"))
                    && TryReadSize(Path.Combine(directory, "winsize.txt"), out var nextRows, out var nextCols)
                    && (nextRows != rows || nextCols != cols))
                {
                    monitor.Send(nextCols, nextRows);
                    rows = nextRows; cols = nextCols;
                }
                await Task.Delay(200, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { monitor?.Dispose(); }
    }
    private static bool TryReadSize(string path, out int rows, out int cols)
    {
        rows = 0; cols = 0;
        try
        {
            var parts = File.ReadAllText(path).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 2 && int.TryParse(parts[0], out rows) && int.TryParse(parts[1], out cols)
                && rows is >= 2 and <= 500 && cols is >= 2 and <= 500;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
