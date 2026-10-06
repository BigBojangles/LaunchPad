namespace LaunchPad.Services.Fence;

public sealed class SessionTable
{
    private readonly Dictionary<int, string> _paths = new();

    public void Add(int pid, string path)
    {
        _paths[pid] = Path.GetFullPath(path);
    }

    public void Remove(int pid) => _paths.Remove(pid);

    public bool Blocks(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var open in _paths.Values)
        {
            if (string.Equals(open, full, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public bool OtherThan(int pid)
    {
        foreach (var openPid in _paths.Keys)
        {
            if (openPid != pid)
                return true;
        }

        return false;
    }
}
