namespace LaunchPad.Services.Fence;

public static class SealText
{
    public const string Running =
        "Grok is running in a small Debian machine with no screen and no graphics card. It works on a copy of this project. This folder is not mounted in that machine.";

    public const string Clipboard =
        "Text you copy from the terminal leaves the seal.";

    public const string Network =
        "The machine can use the network, including programs on this PC.";

    public const string WindowsGrok =
        "The Windows Grok already on this PC is unchanged. Open does not start Windows Grok.";

    public const string Aside =
        "The copy is now a normal folder on this PC. It is not sealed.";

    public const string Explorer =
        "Opening that folder in Explorer, or running a file from it, runs as you and breaks the seal.";

    public const string Stub =
        "The check before copy-back did not look inside the files. It is a placeholder.";

    public const string Exe =
        "That program is not run in the Debian machine, and LaunchPad does not start it as you. It is copied to a separate standard account, run there, and the copy is deleted. That account is not the Debian machine.";

    public const string CopyBack =
        "Files the copy changed are written onto your project. Files only on your PC are left as they are.";

    public const string TestAccountMissing =
        "The test account is not set up. LaunchPad will not start that program as you.";

    public const string AlreadyRunning =
        "A fenced session is already running.";

    public const string NoFileShare =
        "This QEMU build has no 9p file share. LaunchPad will not mount this project.";
}
