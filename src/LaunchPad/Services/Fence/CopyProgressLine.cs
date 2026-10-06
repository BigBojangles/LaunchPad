namespace LaunchPad.Services.Fence;

static class CopyProgressLine
{
    public static volatile bool Shown;
    public static volatile bool GuestStarted;

    public static void Finish()
    {
        if (GuestStarted)
            return;

        GuestStarted = true;
        if (!Shown)
            return;

        try
        {
            Console.Write("\u001b[0m\u001b[?7h\u001b[?1049l\u001b[2J\u001b[H\u001b[?25h");
        }
        catch
        {
            // The guest text still follows if the console rejects the reset.
        }
    }
}
