namespace LaunchPad.Services.Fence;

public static class GuestMemory
{
    public const int DefaultMegabytes = 4096;
    public const int DefaultCores = 2;

    public static int InstalledMegabytes() =>
        HostResources.Current.InstalledMemoryMegabytes ?? DefaultMegabytes + 2048;

    public static int ChooseMegabytes(int saved, int installedMb)
    {
        var cap = installedMb - 2048;
        if (cap < 2048)
            cap = 2048;
        var chosen = saved >= 2048 ? saved : DefaultMegabytes;
        if (chosen < 2048)
            chosen = 2048;
        if (chosen > cap)
            chosen = cap;
        return chosen;
    }

    public static int ChooseProjectMegabytes(int projectSaved, int machineSaved, int installedMb)
    {
        var saved = projectSaved >= 2048 ? projectSaved : machineSaved;
        return ChooseMegabytes(saved, installedMb);
    }

    public static int ChooseCores(int saved, int logical)
    {
        if (logical < 1)
            logical = 1;
        var chosen = saved >= 1 ? saved : DefaultCores;
        if (chosen < 1)
            chosen = 1;
        if (chosen > logical)
            chosen = logical;
        return chosen;
    }

}
