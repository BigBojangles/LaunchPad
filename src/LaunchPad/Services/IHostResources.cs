namespace LaunchPad.Services;

public interface IHostResources
{
    int? InstalledMemoryMegabytes { get; }
    int LogicalProcessors { get; }
}

public static class HostResources
{
    public static IHostResources Current { get; } = OperatingSystem.IsWindows()
        ? new WindowsHostResources() : new UnavailableHostResources();

    private sealed class UnavailableHostResources : IHostResources
    {
        public int? InstalledMemoryMegabytes => null;
        public int LogicalProcessors => Math.Max(1, Environment.ProcessorCount);
    }
}
