using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using LaunchPad.Models;
using LaunchPad.Services.Fence;

namespace LaunchPad.Services;

public sealed class NotificationDestinationStore
{
    public sealed class Envelope
    {
        [JsonRequired] public int Version { get; set; } = 1;
        [JsonRequired] public string Reference { get; set; } = "";
        [JsonRequired] public NotificationDestination Destination { get; set; } = new();
    }
    private readonly string _directory;
    private readonly INotificationSecretProtector _protector;
    public NotificationDestinationStore(string directory, INotificationSecretProtector protector)
    {
        _directory = Path.GetFullPath(directory);
        _protector = protector;
        SafePath("probe");
        Directory.CreateDirectory(_directory);
    }

    // Every edit creates a new reference. Queued alerts can never change recipients.
    public string Add(NotificationDestination destination)
    {
        destination.Validate();
        var reference = Guid.NewGuid().ToString("N");
        var plain = JsonSerializer.SerializeToUtf8Bytes(new Envelope { Reference = reference, Destination = destination }, JsonFile.Options);
        var temp = SafePath(reference + ".tmp");
        try
        {
            var cipher = _protector.Protect(plain);
            if (cipher.Length > 64 * 1024) throw new IOException("Encrypted notification setup is too large.");
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(cipher); stream.Flush(flushToDisk: true); }
            File.Move(temp, SafePath(reference + ".bin"), overwrite: false);
            return reference;
        }
        finally { CryptographicOperations.ZeroMemory(plain); if (File.Exists(temp)) File.Delete(temp); }
    }

    public NotificationDestination Read(string reference)
    {
        if (!Guid.TryParseExact(reference, "N", out _)) throw new IOException("Invalid notification destination reference.");
        var path = SafePath(reference + ".bin");
        if (new FileInfo(path).Length > 64 * 1024) throw new IOException("Saved notification setup is too large; it was preserved.");
        var plain = _protector.Unprotect(File.ReadAllBytes(path));
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(plain, JsonFile.Options);
            if (envelope is null || envelope.Version != 1 || envelope.Reference != reference || envelope.Destination is null)
                throw new IOException("Saved notification setup is invalid; it was preserved.");
            envelope.Destination.Validate();
            return envelope.Destination;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        { throw new IOException("Saved notification setup is invalid; it was preserved."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private string SafePath(string name) => FenceFiles.TryResolveUnlinked(_directory, name, out var path)
        ? path : throw new IOException("Notification credential storage contains a link or unsafe path.");
}
