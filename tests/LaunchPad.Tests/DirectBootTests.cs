using System.Security.Cryptography;
using System.Text.Json;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class DirectBootTests
{
    [Fact]
    public void VerifiedAssetsAddDirectBootWithoutChangingAnyOtherArguments()
    {
        using var f = new Fixture();
        var boot = RuntimeBoot.Read(f.Root, f.Runtime)!;
        var legacy = QemuCommand.Build("whpx", "owned.qcow2", 0, 47200, "fence", null);
        var direct = QemuCommand.Build("whpx", "owned.qcow2", 0, 47200, "fence", null, directBoot: boot);
        Assert.Equal(legacy, direct.Take(legacy.Count));
        Assert.Equal(new[] { "-kernel", boot.Kernel, "-initrd", boot.Initrd, "-append",
            "root=/dev/vda1 ro console=ttyS0,115200 quiet" }, direct.Skip(legacy.Count));
        Assert.Null(RuntimeBoot.Read(f.Root, f.Runtime with { DirectBoot = null }));
    }

    [Fact]
    public void MissingAndChangedAssetsFailBeforeBootAndDoNotTouchSessionWork()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Session, "saved-work.txt"), "preserved");
        File.WriteAllText(Path.Combine(f.Images, f.Boot.Kernel.File), "tampered");
        Assert.Throws<InvalidDataException>(() => RuntimeBoot.Read(f.Root, f.Runtime));
        File.Delete(Path.Combine(f.Images, f.Boot.Kernel.File));
        Assert.Throws<InvalidDataException>(() => RuntimeBoot.Read(f.Root, f.Runtime));
        Assert.Equal("preserved", File.ReadAllText(Path.Combine(f.Session, "saved-work.txt")));
    }

    [Theory]
    [InlineData("../kernel")]
    [InlineData("nested/kernel")]
    [InlineData("C:\\kernel")]
    public void UnsafeDeclaredPathsAreRefused(string name)
    {
        using var f = new Fixture();
        Assert.Throws<InvalidDataException>(() => RuntimeBoot.Validate(f.Boot with { Kernel = f.Boot.Kernel with { File = name } }));
    }

    [Fact]
    public void DuplicateAssetsAndInvalidHashesAreRefused()
    {
        using var f = new Fixture();
        Assert.Throws<InvalidDataException>(() => RuntimeBoot.Validate(f.Boot with { Initrd = f.Boot.Kernel }));
        Assert.Throws<InvalidDataException>(() => RuntimeBoot.Validate(f.Boot with { Kernel = f.Boot.Kernel with { Sha256 = "not-a-hash" } }));
    }

    [Fact]
    public void OnlyNewOrExplicitlyMatchedSessionsUseDirectBoot()
    {
        using var f = new Fixture();
        var boot = RuntimeBoot.Read(f.Root, f.Runtime)!;
        Assert.Same(boot, RuntimeBoot.ForSession(boot, "owned-v1", f.Session, false));
        Assert.Null(RuntimeBoot.ForSession(boot, "owned-v1", f.Session, true));
        f.SaveSession("owned-v1", null);
        Assert.Null(RuntimeBoot.ForSession(boot, "owned-v1", f.Session, true));
        f.SaveSession("owned-v1", f.Boot);
        Assert.Same(boot, RuntimeBoot.ForSession(boot, "owned-v1", f.Session, true));
        Assert.Null(RuntimeBoot.ForSession(boot, "newer-v2", f.Session, true));
        f.SaveSession("owned-v1", f.Boot with { KernelRelease = "different" });
        Assert.Null(RuntimeBoot.ForSession(boot, "owned-v1", f.Session, true));
        File.WriteAllText(Path.Combine(f.Session, "upgrade-result.json"), "{\"verified\":true,\"version\":\"newer-v2\"}");
        Assert.Null(RuntimeBoot.ForSession(boot, "newer-v2", f.Session, true));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N"));
        public string Images => Path.Combine(Root, "images");
        public string Session => Path.Combine(Root, "session");
        public DirectBootManifest Boot { get; }
        public RuntimeImageManifest Runtime { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Images); Directory.CreateDirectory(Session);
            RuntimeImageFile Asset(string name, string body)
            {
                File.WriteAllText(Path.Combine(Images, name), body);
                return new(name, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body))));
            }
            Boot = new("6.1.0-53-amd64", Asset("owned.kernel", "kernel"), Asset("owned.initrd", "initrd"));
            Runtime = new(1, "owned-v1", new("debian-12-builder-owned.qcow2", new string('0', 64)), [], [], DirectBoot: Boot);
        }
        public void SaveSession(string version, DirectBootManifest? boot) => File.WriteAllText(Path.Combine(Session, "session-runtime.json"),
            JsonSerializer.Serialize(new { version, directBoot = boot }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        public void Dispose() => Directory.Delete(Root, true);
    }
}