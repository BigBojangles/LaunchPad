namespace LaunchPad.Services.Fence;

public static class PortChoice
{
    public const int First = 20000;
    public const int Last = 20900;
    // Preserve existing saved-session port allocations. The former fifth
    // Windows-test offset stays reserved but no listener is exposed there.
    public const int Width = 5;

    public static int Next(IReadOnlyCollection<int> takenBases)
    {
        for (var port = First; port <= Last; port++)
        {
            if (!Overlaps(port, takenBases))
                return port;
        }

        throw new IOException("The fenced session did not find a free port.");
    }

    public static bool Overlaps(int port, IReadOnlyCollection<int> takenBases)
    {
        foreach (var taken in takenBases)
        {
            if (port < taken + Width && taken < port + Width)
                return true;
        }

        return false;
    }
}
