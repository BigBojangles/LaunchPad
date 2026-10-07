using LaunchPad.Models;

namespace LaunchPad.Services;

public static class FirstWarning
{
    public const string Text =
        "VM agents work on a copy; native Windows agents work directly in your folder using your account permissions.";

    public static bool ShouldShow(AppSettings settings) => !settings.OnboardingCompleted;

    public static void Continue(AppSettings settings, bool dontShowAgain, Action save)
    {
        if (!dontShowAgain)
            return;

        settings.OnboardingCompleted = true;
        save();
    }
}
