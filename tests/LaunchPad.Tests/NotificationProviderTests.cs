using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LaunchPad.Models;
using LaunchPad.Services;
using MailKit.Security;
using MailKit.Net.Smtp;
using Xunit;

namespace LaunchPad.Tests;

[Trait("Category", "Windows")]
public sealed class NotificationProviderTests
{
    private static readonly AgentNotificationMessage Message = new("Project @everyone", "Codex CLI", "run-1",
        AgentNotificationKind.RunEnded, AgentNotificationOutcome.Finished, DateTimeOffset.UtcNow);
    private static NotificationDestination Email() => new() { Provider = NotificationProvider.Email, SmtpHost = "smtp.example.invalid",
        Username = "test@example.invalid", Password = "fixture-password", From = "test@example.invalid", To = "phone@example.invalid" };
    private static NotificationDestination Telegram() => new() { Provider = NotificationProvider.Telegram,
        Token = "123456:fixture_token_not_a_real_bot", ChatId = "-123456" };
    private static NotificationDestination Discord() => new() { Provider = NotificationProvider.Discord,
        WebhookUrl = "https://discord.com/api/webhooks/123456/fixture_token_not_a_real_webhook" };
    private static NotificationDestination Ntfy() => new() { Provider = NotificationProvider.Ntfy,
        ServerUrl = "https://notify.example.invalid", Topic = "fixture-topic", Token = "fixture_token" };

    private static NotificationDestinationStore Store(out string directory)
    {
        Assert.True(OperatingSystem.IsWindows(), "This fixture proves the Windows credential adapter only.");
        directory = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration",
            "notification-provider-fixture-" + Guid.NewGuid().ToString("N")[..12]);
        return new(directory, new WindowsNotificationSecretProtector());
    }

    private sealed class FakeHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        { Calls++; return handle(request, cancellation); }
    }
    private sealed class FakeEmail : ISmtpNotificationSender
    {
        public int Calls;
        public Task<ProviderAcceptance> SendAsync(NotificationDestination destination, AgentNotificationMessage message, CancellationToken cancellation)
        {
            Calls++;
            Assert.Equal("smtp.example.invalid", destination.SmtpHost);
            Assert.Equal("phone@example.invalid", destination.To);
            Assert.Equal(Message, message);
            return Task.FromResult(ProviderAcceptance.Accepted);
        }
    }
    private static HttpResponseMessage Response(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public class SmtpFixture : DispatchProxy
    {
        public string FailAt = "";
        public List<string> Operations { get; } = [];
        public SecureSocketOptions? Security;
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            var name = method!.Name;
            if (name == "set_Timeout" || name == "Dispose") return null;
            if (name == "get_IsConnected") return true;
            Operations.Add(name);
            if (name == "ConnectAsync")
            {
                Security = arguments!.OfType<SecureSocketOptions>().Single();
                return FailAt == "connect" ? Task.FromException(new IOException("fixture connection failure")) : Task.CompletedTask;
            }
            if (name == "AuthenticateAsync") return Task.CompletedTask;
            if (name == "SendAsync") return FailAt == "send"
                ? Task.FromException<string>(new IOException("fixture lost confirmation")) : Task.FromResult("accepted");
            if (name == "DisconnectAsync") return FailAt == "quit"
                ? Task.FromException(new IOException("fixture QUIT failure")) : Task.CompletedTask;
            throw new InvalidOperationException("Unexpected SMTP fixture operation: " + name);
        }
    }

    [Theory]
    [InlineData("connect", ProviderAcceptance.NotAccepted)]
    [InlineData("send", ProviderAcceptance.Unknown)]
    [InlineData("quit", ProviderAcceptance.Accepted)]
    [InlineData("", ProviderAcceptance.Accepted)]
    public async Task ProductionEmailControlFlowPreservesAcceptanceAndUsesRequiredTls(string failAt, ProviderAcceptance expected)
    {
        var client = DispatchProxy.Create<ISmtpClient, SmtpFixture>();
        var fixture = (SmtpFixture)client;
        fixture.FailAt = failAt;
        var sender = new MailKitNotificationSender(() => client);
        Assert.Equal(expected, await sender.SendAsync(Email(), Message, default));
        Assert.Equal(SecureSocketOptions.StartTls, fixture.Security);
        Assert.Equal(failAt == "connect" ? 0 : 1, fixture.Operations.Count(operation => operation == "SendAsync"));
        Assert.Equal(failAt == "connect" ? 0 : 1, fixture.Operations.Count(operation => operation == "AuthenticateAsync"));
    }

    [Fact]
    public void CurrentUserEncryptedSetupSurvivesReopenAndEditsAlwaysRotateReference()
    {
        var store = Store(out var directory);
        var destination = Email();
        var first = store.Add(destination);
        var original = File.ReadAllBytes(Path.Combine(directory, first + ".bin"));
        Assert.DoesNotContain("fixture-password", Encoding.UTF8.GetString(original));
        Assert.DoesNotContain("phone@example.invalid", Encoding.UTF8.GetString(original));
        destination.To = "other@example.invalid";
        var second = store.Add(destination);
        Assert.NotEqual(first, second);
        var reopened = new NotificationDestinationStore(directory, new WindowsNotificationSecretProtector());
        Assert.Equal("phone@example.invalid", reopened.Read(first).To);
        Assert.Equal("other@example.invalid", reopened.Read(second).To);
        File.Copy(Path.Combine(directory, first + ".bin"), Path.Combine(directory, second + ".bin"), overwrite: true);
        Assert.Throws<IOException>(() => reopened.Read(second));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(directory, first + ".bin")));
        var damaged = original.ToArray();
        damaged[^1] ^= 1;
        File.WriteAllBytes(Path.Combine(directory, first + ".bin"), damaged);
        Assert.Throws<CryptographicException>(() => reopened.Read(first));
        Assert.Equal(damaged, File.ReadAllBytes(Path.Combine(directory, first + ".bin")));
    }

    [Fact]
    public void EmailBuildsOnePlainTextMessageAndTlsPolicyCannotFallBackToPlaintext()
    {
        var destination = Email();
        using var mail = MailKitNotificationSender.CreateMessage(destination, Message);
        Assert.Single(mail.From);
        Assert.Single(mail.To);
        Assert.Equal(Message.Subject, mail.Subject);
        Assert.Equal(Message.Text, mail.TextBody!.Replace("\r\n", "\n"));
        Assert.Null(mail.HtmlBody);
        Assert.Empty(mail.Attachments);
        Assert.Equal(SecureSocketOptions.StartTls, MailKitNotificationSender.SecurityFor(NotificationSmtpSecurity.StartTls));
        Assert.Equal(SecureSocketOptions.SslOnConnect, MailKitNotificationSender.SecurityFor(NotificationSmtpSecurity.TlsOnConnect));
        Assert.Throws<ArgumentException>(() => MailKitNotificationSender.SecurityFor((NotificationSmtpSecurity)100));
        destination.From = "test@example.invalid\r\nBcc: leak@example.invalid";
        Assert.Throws<ArgumentException>(destination.Validate);
    }

    [Fact]
    public async Task EmailDispatchDoesNotEnterHttpAndMissingSetupCannotSend()
    {
        var store = Store(out _);
        using var handler = new FakeHttp((_, _) => throw new InvalidOperationException("Unexpected HTTP request."));
        using var http = new HttpClient(handler);
        var email = new FakeEmail();
        using var transport = new NotificationTransport(store, http, email);
        Assert.Equal(ProviderAcceptance.Accepted, await transport.SendAsync(store.Add(Email()), Message, default));
        Assert.Equal(1, email.Calls);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(ProviderAcceptance.NotAccepted, await transport.SendAsync(Guid.NewGuid().ToString("N"), Message, default));
        Assert.Equal(1, email.Calls);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task TelegramSendsOnlyPlainTextAndRequiresSuccessfulMessageIdentity()
    {
        var store = Store(out _);
        using var handler = new FakeHttp(async (request, cancellation) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("api.telegram.org", request.RequestUri!.Host);
            Assert.EndsWith("/sendMessage", request.RequestUri.AbsolutePath);
            Assert.Equal("", request.RequestUri.Query);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
            Assert.Equal("-123456", body.RootElement.GetProperty("chat_id").GetString());
            Assert.Contains("run-1", body.RootElement.GetProperty("text").GetString());
            Assert.False(body.RootElement.TryGetProperty("parse_mode", out _));
            Assert.False(body.RootElement.TryGetProperty("allow_paid_broadcast", out _));
            Assert.True(body.RootElement.GetProperty("link_preview_options").GetProperty("is_disabled").GetBoolean());
            return Response("{\"ok\":true,\"result\":{\"message_id\":42,\"chat\":{\"id\":-123456}}}");
        });
        using var http = new HttpClient(handler);
        using var transport = new NotificationTransport(store, http, new FakeEmail());
        Assert.Equal(ProviderAcceptance.Accepted, await transport.SendAsync(store.Add(Telegram()), Message, default));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task DiscordRequestsConfirmationAndCannotMentionEveryone()
    {
        var store = Store(out _);
        using var handler = new FakeHttp(async (request, cancellation) =>
        {
            Assert.Equal("?wait=true", request.RequestUri!.Query);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
            Assert.Contains("@everyone", body.RootElement.GetProperty("content").GetString());
            Assert.Empty(body.RootElement.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
            return Response("{\"id\":\"123456789\"}");
        });
        using var http = new HttpClient(handler);
        using var transport = new NotificationTransport(store, http, new FakeEmail());
        Assert.Equal(ProviderAcceptance.Accepted, await transport.SendAsync(store.Add(Discord()), Message, default));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task NtfyPublishesJsonAtServerRootWithHeaderTokenAndMatchingConfirmation()
    {
        var store = Store(out _);
        using var handler = new FakeHttp(async (request, cancellation) =>
        {
            Assert.Equal("https://notify.example.invalid/", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("fixture_token", request.Headers.Authorization.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
            Assert.Equal("fixture-topic", body.RootElement.GetProperty("topic").GetString());
            Assert.Equal(Message.Subject, body.RootElement.GetProperty("title").GetString());
            Assert.Equal(Message.Text, body.RootElement.GetProperty("message").GetString());
            Assert.False(body.RootElement.TryGetProperty("actions", out _));
            return Response("{\"id\":\"fixture-id\",\"event\":\"message\",\"topic\":\"fixture-topic\"}");
        });
        using var http = new HttpClient(handler);
        using var transport = new NotificationTransport(store, http, new FakeEmail());
        Assert.Equal(ProviderAcceptance.Accepted, await transport.SendAsync(store.Add(Ntfy()), Message, default));
    }

    [Theory]
    [InlineData(NotificationProvider.Telegram, "{\"ok\":false}", ProviderAcceptance.NotAccepted)]
    [InlineData(NotificationProvider.Telegram, "{\"ok\":true,\"result\":{}}", ProviderAcceptance.Unknown)]
    [InlineData(NotificationProvider.Telegram, "{\"ok\":true,\"result\":{\"message_id\":42,\"chat\":{\"id\":999}}}", ProviderAcceptance.Unknown)]
    [InlineData(NotificationProvider.Discord, "{}", ProviderAcceptance.Unknown)]
    [InlineData(NotificationProvider.Ntfy, "{\"id\":\"fixture-id\",\"event\":\"message\",\"topic\":\"other\"}", ProviderAcceptance.Unknown)]
    public async Task HttpSuccessWithoutProviderConfirmationCannotClaimAcceptance(NotificationProvider provider, string json, ProviderAcceptance expected)
    {
        var store = Store(out _);
        using var handler = new FakeHttp((_, _) => Task.FromResult(Response(json)));
        using var http = new HttpClient(handler);
        using var transport = new NotificationTransport(store, http, new FakeEmail());
        var destination = provider switch { NotificationProvider.Telegram => Telegram(), NotificationProvider.Discord => Discord(), _ => Ntfy() };
        Assert.Equal(expected, await transport.SendAsync(store.Add(destination), Message, default));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(302, ProviderAcceptance.Unknown)]
    [InlineData(401, ProviderAcceptance.NotAccepted)]
    [InlineData(408, ProviderAcceptance.Unknown)]
    [InlineData(429, ProviderAcceptance.NotAccepted)]
    [InlineData(500, ProviderAcceptance.Unknown)]
    public async Task HttpErrorsAreClassifiedWithoutResponseTextOrRetry(int status, ProviderAcceptance expected)
    {
        var store = Store(out _);
        using var handler = new FakeHttp((_, _) => Task.FromResult(Response("secret-token-in-error", (HttpStatusCode)status)));
        using var http = new HttpClient(handler);
        using var transport = new NotificationTransport(store, http, new FakeEmail());
        Assert.Equal(expected, await transport.SendAsync(store.Add(Discord()), Message, default));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task OversizedOrBrokenResponsesAndTransportExceptionsStayUnknownWithoutRetry()
    {
        var store = Store(out _);
        var reference = store.Add(Discord());
        foreach (var reply in new[] { new string('x', 65537), "{broken json" })
        {
            using var handler = new FakeHttp((_, _) => Task.FromResult(Response(reply)));
            using var http = new HttpClient(handler);
            using var transport = new NotificationTransport(store, http, new FakeEmail());
            Assert.Equal(ProviderAcceptance.Unknown, await transport.SendAsync(reference, Message, default));
            Assert.Equal(1, handler.Calls);
        }
        using var failure = new FakeHttp((_, _) => throw new IOException("token-bearing-uri"));
        using var failedHttp = new HttpClient(failure);
        using var failedTransport = new NotificationTransport(store, failedHttp, new FakeEmail());
        Assert.Equal(ProviderAcceptance.Unknown, await failedTransport.SendAsync(reference, Message, default));
        Assert.Equal(1, failure.Calls);
    }

    [Fact]
    public void InvalidSetupCannotSaveCredentialBearingOrPlaintextEndpoints()
    {
        var store = Store(out var directory);
        foreach (var uri in new[] { "http://discord.com/api/webhooks/123456/fixture_token", "https://elsewhere.invalid/api/webhooks/123456/fixture_token",
            "https://user:password@discord.com/api/webhooks/123456/fixture_token", "https://discord.com/api/webhooks/123456/fixture_token?wait=false" })
        {
            var destination = Discord(); destination.WebhookUrl = uri;
            Assert.Throws<ArgumentException>(() => store.Add(destination));
        }
        var ntfy = Ntfy(); ntfy.ServerUrl = "http://notify.example.invalid";
        Assert.Throws<ArgumentException>(() => store.Add(ntfy));
        Assert.Empty(Directory.EnumerateFiles(directory));
    }
}
