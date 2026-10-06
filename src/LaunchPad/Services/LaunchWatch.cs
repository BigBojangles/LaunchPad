namespace LaunchPad.Services;

/// <summary>
/// One open Grok window per project path, identified by the process id this launcher stored.
/// </summary>
public sealed class LaunchWatch
{
    private const int Idle = 0;

    private readonly Dictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private int _nextGeneration;

    public bool IsOpen(string projectPath)
    {
        var full = Normalize(projectPath);
        if (full is null)
            return false;

        lock (_gate)
            return _slots.ContainsKey(full);
    }

    public int TryBegin(string projectPath)
    {
        var full = Normalize(projectPath);
        if (full is null)
            return Idle;

        lock (_gate)
        {
            if (_slots.ContainsKey(full))
                return Idle;

            var generation = ++_nextGeneration;
            _slots[full] = new Slot(generation);
            return generation;
        }
    }

    public bool NotePid(string projectPath, int generation, int pid)
    {
        if (pid <= 0)
            return false;

        var full = Normalize(projectPath);
        if (full is null)
            return false;

        lock (_gate)
        {
            if (!_slots.TryGetValue(full, out var slot) || slot.Generation != generation)
                return false;

            slot.Starting = false;
            return slot.Pids.Add(pid);
        }
    }

    public void NoteExited(string projectPath, int generation, int pid)
    {
        var full = Normalize(projectPath);
        if (full is null)
            return;

        lock (_gate)
        {
            if (!_slots.TryGetValue(full, out var slot) || slot.Generation != generation)
                return;

            slot.Pids.Remove(pid);
            if (slot.Pids.Count == 0 && !slot.Starting)
                _slots.Remove(full);
        }
    }

    public void CancelStart(string projectPath, int generation)
    {
        var full = Normalize(projectPath);
        if (full is null)
            return;

        lock (_gate)
        {
            if (!_slots.TryGetValue(full, out var slot) || slot.Generation != generation)
                return;

            slot.Starting = false;
            if (slot.Pids.Count == 0)
                _slots.Remove(full);
        }
    }

    public void PruneDead(string projectPath, Func<int, bool> isRunning)
    {
        var full = Normalize(projectPath);
        if (full is null)
            return;

        int generation;
        int[] pids;
        lock (_gate)
        {
            if (!_slots.TryGetValue(full, out var slot))
                return;

            generation = slot.Generation;
            pids = slot.Pids.ToArray();
        }

        foreach (var pid in pids)
        {
            if (!isRunning(pid))
                NoteExited(projectPath, generation, pid);
        }
    }

    public (bool Starting, int[] ProcessIds)? Describe(string projectPath)
    {
        var full = Normalize(projectPath);
        if (full is null) return null;
        lock (_gate)
            return _slots.TryGetValue(full, out var slot) ? (slot.Starting, slot.Pids.Order().ToArray()) : null;
    }

    private static string? Normalize(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            return null;

        try
        {
            return Path.GetFullPath(projectPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }

    private sealed class Slot
    {
        public Slot(int generation)
        {
            Generation = generation;
        }

        public int Generation { get; }
        public bool Starting { get; set; } = true;
        public HashSet<int> Pids { get; } = new();
    }
}
