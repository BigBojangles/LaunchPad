using System.Text;
using System.Net;
using System.Net.Sockets;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class GuestAuthPersistenceTests
{
    [Fact]
    public async Task AuthRequestWaitsForANewFrameAndCannotReuseAnEarlierCheckpoint()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var guest = new TcpClient();
        await guest.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var link = new StatusLink(await listener.AcceptTcpClientAsync());
        var first = link.RequestAuthAsync(TimeSpan.FromSeconds(2));
        await guest.GetStream().WriteAsync("AUTH 3\none"u8.ToArray());
        Assert.Equal("one", Encoding.UTF8.GetString((await first)!));
        Assert.Null(await link.RequestAuthAsync(TimeSpan.FromMilliseconds(150)));
        var second = link.RequestAuthAsync(TimeSpan.FromSeconds(2));
        await guest.GetStream().WriteAsync("AUTH 3\ntwo"u8.ToArray());
        Assert.Equal("two", Encoding.UTF8.GetString((await second)!));
        var empty = link.RequestAuthAsync(TimeSpan.FromSeconds(2));
        await guest.GetStream().WriteAsync("AUTH 0\n"u8.ToArray());
        Assert.Empty((await empty)!);
    }

    [Fact]
    public void SavedLoginChoiceSurvivesRestartAndFailedPreferenceSave()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPadLoginChoice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new AppPaths(userProfile: root, appDataDir: Path.Combine(root, "settings"));
            var settings = new SettingsStore(paths);
            settings.SavePreferences(true, AgentChoice.Grok, 4096, 2, rememberGrokSignIn: false);
            Assert.False(new SettingsStore(paths).Current.RememberGrokSignIn);
            using (var held = new FileStream(paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.Throws<IOException>(() => settings.SavePreferences(true, AgentChoice.Grok, 4096, 2, rememberGrokSignIn: true));
            Assert.False(settings.Current.RememberGrokSignIn);
            Assert.False(new SettingsStore(paths).Current.RememberGrokSignIn);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [Fact]
    public void ProtectedCacheRoundTripsAndMissingGuestStateDoesNotEraseIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "LaunchPadAuth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var body = Encoding.UTF8.GetBytes("{\"provider\":{\"key\":\"owned-test-login\"}}");
            GuestAuth.Save(body, root);
            var stored = File.ReadAllBytes(Path.Combine(root, "auth.dpapi"));
            Assert.DoesNotContain("owned-test-login", Encoding.UTF8.GetString(stored));
            Assert.Equal(body, GuestAuth.ReadHost(root));
            GuestAuth.Save([], root);
            GuestAuth.Save(Encoding.UTF8.GetBytes("invalid-json"), root);
            Assert.Equal(stored, File.ReadAllBytes(Path.Combine(root, "auth.dpapi")));
            Assert.Equal(body, GuestAuth.ReadHost(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void LegacyLoginIsPreservedAndBadEncryptedStateCannotFallBackToAnOldLogin()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "LaunchPadAuth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var legacy = Encoding.UTF8.GetBytes("{\"refresh_token\":\"owned-legacy-login\"}");
            File.WriteAllBytes(Path.Combine(root, "auth.json"), legacy);
            Assert.Equal(legacy, GuestAuth.ReadHost(root));
            Assert.Equal(legacy, File.ReadAllBytes(Path.Combine(root, "auth.json")));
            File.WriteAllBytes(Path.Combine(root, "auth.dpapi"), Encoding.UTF8.GetBytes("corrupt"));
            Assert.Null(GuestAuth.ReadHost(root));
            Assert.Equal(legacy, File.ReadAllBytes(Path.Combine(root, "auth.json")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void FreshAuthFramesAreRecognizedAcrossFragmentsWithoutConsumingHomeTraffic()
    {
        var buffer = new StatusBuffer();
        var first = Encoding.UTF8.GetBytes("AUTH 3\noneHOME 4\nhome");
        foreach (var value in first) buffer.Push([value], 1);
        Assert.Equal(1, buffer.AuthRevision);
        Assert.Equal("one", Encoding.UTF8.GetString(buffer.AuthBody));
        Assert.Equal("home", Encoding.UTF8.GetString(buffer.HomeBody));
        var second = Encoding.UTF8.GetBytes("AUTH 3\ntwo");
        buffer.Push(second, second.Length);
        Assert.Equal(2, buffer.AuthRevision);
        Assert.Equal("two", Encoding.UTF8.GetString(buffer.AuthBody));
        Assert.Equal("home", Encoding.UTF8.GetString(buffer.HomeBody));
    }
}
