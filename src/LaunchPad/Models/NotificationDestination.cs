using System.Globalization;
using System.Text.RegularExpressions;
using MimeKit;

namespace LaunchPad.Models;

public enum NotificationProvider { Email, Telegram, Discord, Ntfy }
public enum NotificationSmtpSecurity { StartTls, TlsOnConnect }

// Deliberately not a record: ToString must never print credentials.
public sealed class NotificationDestination
{
    public NotificationProvider Provider { get; set; }
    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; } = 587;
    public NotificationSmtpSecurity SmtpSecurity { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
    public string? Token { get; set; }
    public string? ChatId { get; set; }
    public string? WebhookUrl { get; set; }
    public string? ServerUrl { get; set; }
    public string? Topic { get; set; }

    public void Validate()
    {
        if (!Enum.IsDefined(Provider) || new[] { SmtpHost, Username, Password, From, To, Token, ChatId, WebhookUrl, ServerUrl, Topic }
            .Any(value => value is { Length: > 4096 } || value?.Any(char.IsControl) == true))
            throw new ArgumentException("Notification setup contains an invalid or oversized field.");
        var valid = Provider switch
        {
            NotificationProvider.Email => Uri.CheckHostName(SmtpHost ?? "") != UriHostNameType.Unknown
                && SmtpPort is > 0 and <= 65535 && Enum.IsDefined(SmtpSecurity)
                && !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrEmpty(Password)
                && MailboxAddress.TryParse(From ?? "", out _) && MailboxAddress.TryParse(To ?? "", out _),
            NotificationProvider.Telegram => Regex.IsMatch(Token ?? "", @"\A[0-9]{1,20}:[A-Za-z0-9_-]{15,200}\z")
                && (long.TryParse(ChatId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var chat) && chat != 0
                    || Regex.IsMatch(ChatId ?? "", @"\A@[A-Za-z0-9_]{5,64}\z")),
            NotificationProvider.Discord => HttpsUri(WebhookUrl, out var webhook) && webhook.Port == 443
                && (webhook.Host is "discord.com" or "discordapp.com")
                && Regex.IsMatch(webhook.AbsolutePath, @"\A/api/(?:v[0-9]+/)?webhooks/[0-9]+/[A-Za-z0-9_-]{10,512}\z"),
            NotificationProvider.Ntfy => HttpsUri(ServerUrl, out _)
                && Regex.IsMatch(Topic ?? "", @"\A[A-Za-z0-9_-]{1,64}\z")
                && (string.IsNullOrEmpty(Token) || Regex.IsMatch(Token, @"\A[A-Za-z0-9_-]{1,512}\z")),
            _ => false
        };
        if (!valid) throw new ArgumentException("Complete the selected notification provider's required setup. HTTPS and encrypted SMTP are required.");
    }

    private static bool HttpsUri(string? value, out Uri uri)
    {
        uri = null!;
        return Uri.TryCreate(value, UriKind.Absolute, out uri!) && uri.Scheme == Uri.UriSchemeHttps
            && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    }
}
