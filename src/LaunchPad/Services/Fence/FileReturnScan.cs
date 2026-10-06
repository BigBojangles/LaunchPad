namespace LaunchPad.Services.Fence;

public enum FileScanVerdict { Allowed, Rejected, Unavailable }

public interface IReturnFileScanner
{
    FileScanVerdict Scan(Stream content, string name);
}

public sealed class WindowsReturnFileScanner : IReturnFileScanner
{
    public FileScanVerdict Scan(Stream content, string name) => FileReturnScan.Scan(content, name);
}

public static class FileReturnScan
{
    public static bool StaysInside(string root, string file) =>
        FenceFiles.Relative(root, file) is { } relative
        && FenceFiles.TryResolveUnlinked(root, relative, out _);

    public static bool Allows(string root, string file, LaunchPad.Services.SetupLog log)
    {
        if (!StaysInside(root, file)) return false;
        try
        {
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var verdict = Scan(input, Path.GetFileName(file));
            if (verdict != FileScanVerdict.Allowed) log.Write("Return scan: " + verdict);
            return verdict == FileScanVerdict.Allowed;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    public static FileScanVerdict Scan(Stream input, string name) => WindowsAmsiScanner.Scan(input, name).Verdict;
}
