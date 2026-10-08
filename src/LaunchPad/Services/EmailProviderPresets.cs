using LaunchPad.Models;
using MimeKit;

namespace LaunchPad.Services;

public sealed record EmailProviderPreset(string Name, string Host, int Port, NotificationSmtpSecurity Security,
    string SignInHint, string HelpUrl, bool RequiresOAuth = false);

// Public provider documentation verified 2026-10-07. This table performs no
// discovery requests and never derives an SMTP host from an arbitrary domain.
public static class EmailProviderPresets
{
    public static IReadOnlyList<EmailProviderPreset> All { get; } = Array.AsReadOnly(new[]
    {
        new EmailProviderPreset("Gmail", "smtp.gmail.com", 587, NotificationSmtpSecurity.StartTls,
            "Use a Google app password. Google requires 2-Step Verification to create one. Enter it here, not your normal Google password.",
            "https://myaccount.google.com/apppasswords"),
        new EmailProviderPreset("Yahoo", "smtp.mail.yahoo.com", 465, NotificationSmtpSecurity.TlsOnConnect,
            "Generate a Yahoo app password for LaunchPad and enter it here.",
            "https://help.yahoo.com/kb/SLN15241.html"),
        new EmailProviderPreset("iCloud", "smtp.mail.me.com", 587, NotificationSmtpSecurity.StartTls,
            "Generate an Apple app-specific password for LaunchPad and enter it here.",
            "https://support.apple.com/en-us/102654"),
        new EmailProviderPreset("Fastmail", "smtp.fastmail.com", 465, NotificationSmtpSecurity.TlsOnConnect,
            "Use a Fastmail app password and your account username. Aliases may need a different username. Basic plans do not include SMTP access.",
            "https://www.fastmail.help/hc/en-us/articles/360058752854-App-passwords"),
        new EmailProviderPreset("Outlook / Hotmail", "smtp-mail.outlook.com", 587, NotificationSmtpSecurity.StartTls,
            "Microsoft requires modern sign-in, which LaunchPad does not support yet. Use a different email account for LaunchPad pages.",
            "https://support.microsoft.com/en-us/outlook/pop-imap-and-smtp-settings-for-outlook-com", RequiresOAuth: true)
    });

    public static string? Address(string? text)
    {
        var value = text?.Trim();
        return value is { Length: > 0 and <= 254 } && !value.Any(char.IsControl)
            && MailboxAddress.TryParse(value, out var mailbox)
            && string.Equals(value, mailbox.Address, StringComparison.OrdinalIgnoreCase) ? mailbox.Address : null;
    }

    public static EmailProviderPreset? Match(string? email)
    {
        var address = Address(email);
        if (address is null) return null;
        var domain = address[(address.LastIndexOf('@') + 1)..].ToLowerInvariant();
        var index = domain switch
        {
            "gmail.com" => 0,
            "yahoo.com" or "ymail.com" or "rocketmail.com" => 1,
            "icloud.com" or "me.com" or "mac.com" => 2,
            "fastmail.com" => 3,
            "outlook.com" or "hotmail.com" or "live.com" or "msn.com" => 4,
            _ => -1
        };
        return index >= 0 ? All[index] : null;
    }
}
