using LaunchPad.Models;

namespace LaunchPad.Services;

public static class FirstWarning
{
    public const string Text =
        "This folder is not mounted. Grok works on a copy inside a small Debian machine.";

    public static bool ShouldShow(AppSettings settings) => !settings.OnboardingCompleted;

    public static void Continue(AppSettings settings, bool dontShowAgain, Action save)
    {
        if (!dontShowAgain)
            return;

        settings.OnboardingCompleted = true;
        save();
    }
}
