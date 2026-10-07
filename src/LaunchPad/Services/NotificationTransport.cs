using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json;
using LaunchPad.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace LaunchPad.Services;

public interface ISmtpNotificationSender
{
    Task<ProviderAcceptance> SendAsync(NotificationDestination destination, AgentNotificationMessage message, CancellationToken cancellation);
}

public sealed class MailKitNotificationSender(Func<ISmtpClient>? createClient = null) : ISmtpNotificationSender
{
    public static SecureSocketOptions SecurityFor(NotificationSmtpSecurity mode) => mode switch
    {
        NotificationSmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        NotificationSmtpSecurity.TlsOnConnect => SecureSocketOptions.SslOnConnect,
        _ => throw new ArgumentException("Choose encrypted SMTP transport.")
    };

    public static MimeMessage CreateMessage(NotificationDestination destination, AgentNotificationMessage message)
    {
        destination.Validate();
        var mail = new MimeMessage();
        mail.From.Add(MailboxAddress.Parse(destination.From!));
        mail.To.Add(MailboxAddress.Parse(destination.To!));
        mail.Subject = message.Subject;
        mail.Body = new TextPart("plain") { Text = message.Text };
        return mail;
    }

    public async Task<ProviderAcceptance> SendAsync(NotificationDestination destination, AgentNotificationMessage message, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var client = createClient?.Invoke() ?? new SmtpClient();
        client.Timeout = 20000;
        var submitting = false;
        ProviderAcceptance outcome;
        try
        {
            using var mail = CreateMessage(destination, message);
            // No plaintext fallback, certificate override, protocol logging or automatic retry.
            await client.ConnectAsync(destination.SmtpHost!, destination.SmtpPort, SecurityFor(destination.SmtpSecurity), timeout.Token).ConfigureAwait(false);
            await client.AuthenticateAsync(destination.Username!, destination.Password!, timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            submitting = true;
            await client.SendAsync(mail, timeout.Token).ConfigureAwait(false);
            outcome = ProviderAcceptance.Accepted;
        }
        catch (SmtpCommandException) { outcome = ProviderAcceptance.NotAccepted; }
        catch (Exception) { outcome = submitting ? ProviderAcceptance.Unknown : ProviderAcceptance.NotAccepted; }
        // A failed QUIT after successful DATA must not erase known acceptance.
        try { if (client.IsConnected) await client.DisconnectAsync(true, timeout.Token).ConfigureAwait(false); }
        catch (Exception) { }
        return outcome;
    }
}

public sealed class NotificationTransport : INotificationTransport, IDisposable
{
    private readonly NotificationDestinationStore _destinations;
    private readonly HttpClient _http;
    private readonly ISmtpNotificationSender _email;
    private readonly bool _ownsClient;

    public NotificationTransport(NotificationDestinationStore destinations, HttpClient http, ISmtpNotificationSender email, bool ownsClient = false)
    { _destinations = destinations; _http = http; _email = email; _ownsClient = ownsClient; }

    public static NotificationTransport Create(NotificationDestinationStore destinations) => new(destinations,
        new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(20) },
        new MailKitNotificationSender(), ownsClient: true);

    public async Task<ProviderAcceptance> SendAsync(string destinationReference, AgentNotificationMessage message, CancellationToken cancellation)
    {
        NotificationDestination destination;
        try { destination = _destinations.Read(destinationReference); }
        catch (Exception) { return ProviderAcceptance.NotAccepted; } // No network operation occurred.
        if (destination.Provider == NotificationProvider.Email)
            return await _email.SendAsync(destination, message, cancellation).ConfigureAwait(false);

        using var request = CreateRequest(destination, message);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 5xx/timeout responses can follow a committed message. Never infer rejection from them.
                var status = (int)response.StatusCode;
                return status is >= 400 and < 500 && status != 408 ? ProviderAcceptance.NotAccepted : ProviderAcceptance.Unknown;
            }
            using var body = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
            var root = body.RootElement;
            return destination.Provider switch
            {
                NotificationProvider.Telegram => TelegramAcceptance(root, destination.ChatId!),
                NotificationProvider.Discord => HasStringId(root) ? ProviderAcceptance.Accepted : ProviderAcceptance.Unknown,
                NotificationProvider.Ntfy => HasStringId(root) && root.TryGetProperty("event", out var kind) && kind.ValueKind == JsonValueKind.String
                    && kind.GetString() == "message" && root.TryGetProperty("topic", out var topic) && topic.ValueKind == JsonValueKind.String
                    && topic.GetString() == destination.Topic ? ProviderAcceptance.Accepted : ProviderAcceptance.Unknown,
                _ => ProviderAcceptance.Unknown
            };
        }
        catch (Exception) { return ProviderAcceptance.Unknown; } // Never retain response bodies or token-bearing exception URLs.
    }

    private static HttpRequestMessage CreateRequest(NotificationDestination destination, AgentNotificationMessage message)
    {
        switch (destination.Provider)
        {
            case NotificationProvider.Telegram:
                return new(HttpMethod.Post, "https://api.telegram.org/bot" + destination.Token + "/sendMessage")
                {
                    Content = JsonContent.Create(new { chat_id = destination.ChatId, text = message.Subject + "\n" + message.Text,
                        link_preview_options = new { is_disabled = true } })
                };
            case NotificationProvider.Discord:
                return new(HttpMethod.Post, destination.WebhookUrl + "?wait=true")
                {
                    Content = JsonContent.Create(new { content = message.Subject + "\n" + message.Text,
                        allowed_mentions = new { parse = Array.Empty<string>() } })
                };
            case NotificationProvider.Ntfy:
                var request = new HttpRequestMessage(HttpMethod.Post, destination.ServerUrl!.TrimEnd('/') + "/")
                { Content = JsonContent.Create(new { topic = destination.Topic, title = message.Subject, message = message.Text }) };
                if (!string.IsNullOrEmpty(destination.Token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", destination.Token);
                return request;
            default: throw new ArgumentException("Unknown notification provider.");
        }
    }

    private static ProviderAcceptance TelegramAcceptance(JsonElement root, string chatId)
    {
        if (!root.TryGetProperty("ok", out var ok)) return ProviderAcceptance.Unknown;
        if (ok.ValueKind == JsonValueKind.False) return ProviderAcceptance.NotAccepted;
        if (!(ok.ValueKind == JsonValueKind.True && root.TryGetProperty("result", out var result)
            && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("message_id", out var id)
            && id.TryGetInt64(out var number) && number > 0 && result.TryGetProperty("chat", out var chat)
            && chat.ValueKind == JsonValueKind.Object)) return ProviderAcceptance.Unknown;
        var matches = long.TryParse(chatId, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var target)
            ? chat.TryGetProperty("id", out var actual) && actual.TryGetInt64(out var chatNumber) && chatNumber == target
            : chat.TryGetProperty("username", out var username) && username.ValueKind == JsonValueKind.String
                && string.Equals(username.GetString(), chatId.TrimStart('@'), StringComparison.OrdinalIgnoreCase);
        return matches ? ProviderAcceptance.Accepted : ProviderAcceptance.Unknown;
    }

    private static bool HasStringId(JsonElement root) => root.TryGetProperty("id", out var id)
        && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString());

    private static async Task<JsonDocument> ReadBoundedAsync(HttpContent content, CancellationToken cancellation)
    {
        const int maximum = 64 * 1024;
        if (content.Headers.ContentLength > maximum) throw new IOException("Notification response is too large.");
        await using var input = await content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var body = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) != 0)
        {
            if (body.Length + count > maximum) throw new IOException("Notification response is too large.");
            body.Write(buffer, 0, count);
        }
        return JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
    }

    public void Dispose() { if (_ownsClient) _http.Dispose(); }
}
