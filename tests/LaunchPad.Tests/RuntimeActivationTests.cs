using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class RuntimeActivationTests
{
    [Fact]
    public void StandalonePackageActivatesWithoutHistoricalBaseAndPreservesSavedSessions()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Images, fixture.Manifest.Image.File);
        var bytes = File.ReadAllBytes(path)[..72];
        bytes.AsSpan(8, 12).Clear();
        File.WriteAllBytes(path, bytes);
        var manifest = fixture.Manifest with
        {
            Image = fixture.Manifest.Image with { Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) },
            Dependencies = Array.Empty<RuntimeImageFile>()
        };
        File.Delete(Path.Combine(fixture.Images, PublicRuntime.BaseName));
        var candidate = fixture.Address(RuntimeActivation.RuntimePrefix, manifest);
        var result = RuntimeActivation.Activate(fixture.Root, candidate, _ => { });
        Assert.True(result.Changed);
        Assert.Empty(RuntimeImages.Read(fixture.Root).Manifest!.Dependencies);
        Assert.Equal("owned saved session", File.ReadAllText(fixture.Session));
        Assert.Equal(fixture.OriginalBuilder, File.ReadAllBytes(fixture.Builder));
    }

    [Fact]
    public void ActivationPreservesPreviousSelectionAndLegacyKitWhileSelectingOneNewPair()
    {
        using var fixture = new Fixture();
        var captured = RuntimeImages.Read(fixture.Root).Manifest!;
        var previous = File.ReadAllBytes(fixture.Active);
        var result = RuntimeActivation.Activate(fixture.Root, fixture.Candidate, plan =>
        {
            Assert.Equal(previous, File.ReadAllBytes(fixture.Active));
            Assert.Contains(Path.Combine(fixture.Images, fixture.KitName), plan.ReadPaths);
            Assert.Contains(Path.Combine(fixture.Images, "v2.kernel"), plan.ReadPaths);
            Assert.Throws<IOException>(() => File.Open(fixture.Builder, FileMode.Open, FileAccess.Write, FileShare.None));
        });
        Assert.True(result.Changed);
        Assert.Equal(previous, File.ReadAllBytes(result.PreviousManifest!));
        var current = RuntimeImages.Read(fixture.Root).Manifest!;
        Assert.Equal("v2", current.Version);
        Assert.Equal(fixture.KitName, current.MaintenanceManifest);
        Assert.Equal("v1", MaintenanceKit.Read(fixture.Root, captured.MaintenanceManifest ?? "maintenance.json").Manifest.Version);
        Assert.Equal("v2", MaintenanceKit.Read(fixture.Root, current.MaintenanceManifest!).Manifest.Version);
        Assert.Equal(fixture.OriginalBuilder, File.ReadAllBytes(fixture.Builder));
        Assert.Equal("owned saved session", File.ReadAllText(fixture.Session));
        var again = RuntimeActivation.Activate(fixture.Root, fixture.Candidate, _ => { });
        Assert.False(again.Changed);
        Assert.Null(again.PreviousManifest);
        Assert.Single(Directory.GetFiles(fixture.Images, "runtime-before-*.json"));
    }

    [Fact]
    public void AConflictingRetainedBackingFileIsNeverOverwrittenOrActivated()
    {
        using var fixture = new Fixture();
        var previous = File.ReadAllBytes(fixture.Active);
        File.WriteAllText(fixture.Builder, "owned conflicting bytes");
        var prepared = false;
        Assert.Throws<InvalidDataException>(() => RuntimeActivation.Activate(fixture.Root, fixture.Candidate, _ => prepared = true));
        Assert.False(prepared);
        Assert.Equal(previous, File.ReadAllBytes(fixture.Active));
        Assert.Equal("owned conflicting bytes", File.ReadAllText(fixture.Builder));
        Assert.Equal("owned saved session", File.ReadAllText(fixture.Session));
    }

    [Fact]
    public void DeniedAccessPreparationLeavesTheOldSelectionAndDisksIntact()
    {
        using var fixture = new Fixture();
        var previous = File.ReadAllBytes(fixture.Active);
        Assert.Throws<UnauthorizedAccessException>(() => RuntimeActivation.Activate(fixture.Root, fixture.Candidate,
            _ => throw new UnauthorizedAccessException("Owned denied preparation")));
        Assert.Equal(previous, File.ReadAllBytes(fixture.Active));
        Assert.Equal(fixture.OriginalBuilder, File.ReadAllBytes(fixture.Builder));
        Assert.Empty(Directory.GetFiles(fixture.Images, "runtime-before-*.json"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../maintenance.json")]
    public void AnInvalidExplicitKitCannotFallBackToTheWorkingLegacyKit(string kit)
    {
        using var fixture = new Fixture();
        var previous = File.ReadAllBytes(fixture.Active);
        fixture.Candidate = fixture.Address(RuntimeActivation.RuntimePrefix, fixture.Manifest with { MaintenanceManifest = kit });
        Assert.Throws<InvalidDataException>(() => RuntimeActivation.Activate(fixture.Root, fixture.Candidate, _ => { }));
        Assert.Equal(previous, File.ReadAllBytes(fixture.Active));
        Assert.Equal("v1", MaintenanceKit.Read(fixture.Root).Manifest.Version);
    }

    [Fact]
    public void MissingKitAndMismatchedVersionDoNotActivate()
    {
        using var fixture = new Fixture();
        var previous = File.ReadAllBytes(fixture.Active);
        var mismatch = fixture.Address(RuntimeActivation.MaintenancePrefix, fixture.Kit with { Version = "v3" });
        fixture.Candidate = fixture.Address(RuntimeActivation.RuntimePrefix, fixture.Manifest with { MaintenanceManifest = mismatch });
        Assert.Throws<InvalidDataException>(() => RuntimeActivation.Activate(fixture.Root, fixture.Candidate, _ => { }));
        File.Delete(Path.Combine(fixture.Images, mismatch));
        Assert.Throws<FileNotFoundException>(() => RuntimeActivation.Activate(fixture.Root, fixture.Candidate, _ => { }));
        Assert.Equal(previous, File.ReadAllBytes(fixture.Active));
    }

    [Fact]
    public void ChangedPackageManifestCannotReuseItsReviewedAddress()
    {
        using var fixture = new Fixture();
        var previous = File.ReadAllBytes(fixture.Active);
        File.AppendAllText(Path.Combine(fixture.Images, fixture.KitName), " ");
        Assert.Throws<InvalidDataException>(() => RuntimeActivation.Activate(fixture.Root, fixture.Candidate, _ => { }));
        Assert.Throws<InvalidDataException>(() => MaintenanceKit.Read(fixture.Root, fixture.KitName));
        Assert.Equal(previous, File.ReadAllBytes(fixture.Active));
    }

    [Fact]
    public void AnUndeclaredActualBackingFileCannotBeActivated()
    {
        using var fixture = new Fixture();
        var previous = File.ReadAllBytes(fixture.Active);
        fixture.Candidate = fixture.Address(RuntimeActivation.RuntimePrefix, fixture.Manifest with
        { Dependencies = fixture.Manifest.Dependencies.Where(image => image.File != "debian-12-builder.qcow2").ToArray() });
        Assert.Throws<InvalidDataException>(() => RuntimeActivation.Activate(fixture.Root, fixture.Candidate, _ => { }));
        Assert.Equal(previous, File.ReadAllBytes(fixture.Active));
        Assert.Equal(fixture.OriginalBuilder, File.ReadAllBytes(fixture.Builder));
    }

    [Fact]
    public void ASecondPublisherCannotChangeTheSelectionWhileTheFirstOwnsIt()
    {
        using var fixture = new Fixture();
        var previous = File.ReadAllBytes(fixture.Active);
        using var first = new FileStream(Path.Combine(fixture.Images, "runtime-activation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<IOException>(() => RuntimeActivation.Activate(fixture.Root, fixture.Candidate, _ => { }));
        Assert.Equal(previous, File.ReadAllBytes(fixture.Active));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LaunchPadActivation-" + Guid.NewGuid().ToString("N"));
        public string Images => Path.Combine(Root, "images");
        public string Active => Path.Combine(Images, "runtime.json");
        public string Builder => Path.Combine(Images, "debian-12-builder.qcow2");
        public string Session => Path.Combine(Root, "sessions", "owned", "session.qcow2");
        public byte[] OriginalBuilder { get; }
        public RuntimeImageManifest Manifest { get; }
        public MaintenanceManifest Kit { get; }
        public string KitName { get; }
        public string Candidate { get; set; }
        private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public Fixture()
        {
            Directory.CreateDirectory(Images);
            Directory.CreateDirectory(Path.Combine(Root, "qemu", "fence"));
            Directory.CreateDirectory(Path.Combine(Root, "qemu", "share"));
            Directory.CreateDirectory(Path.GetDirectoryName(Session)!);
            foreach (var name in new[] { "LaunchPad.exe", "qemu/qemu-img.exe", "qemu/fence/qemu-system-x86_64.exe" })
                File.WriteAllText(Path.Combine(Root, name), "owned runtime placeholder");
            File.WriteAllText(Session, "owned saved session");
            RuntimeImageFile Image(string name, string? backing)
            {
                var backingBytes = Encoding.UTF8.GetBytes(backing ?? "");
                var bytes = new byte[72 + backingBytes.Length];
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), 0x514649fb);
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4, 4), 2);
                if (backing is not null)
                {
                    BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8, 8), 72);
                    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), (uint)backingBytes.Length);
                    backingBytes.CopyTo(bytes, 72);
                }
                File.WriteAllBytes(Path.Combine(Images, name), bytes);
                return new(name, Convert.ToHexString(SHA256.HashData(bytes)));
            }
            var baseImage = Image(PublicRuntime.BaseName, null);
            var old = Image("debian-12-builder.qcow2", baseImage.File);
            var current = Image("debian-12-builder-v2.qcow2", old.File);
            OriginalBuilder = File.ReadAllBytes(Builder);
            MaintenanceArtifact Artifact(string name)
            {
                var bytes = Encoding.UTF8.GetBytes("owned " + name);
                File.WriteAllBytes(Path.Combine(Images, name), bytes);
                return new(name, Convert.ToHexString(SHA256.HashData(bytes)));
            }
            Kit = new(1, "v2", Artifact("v2.kernel"), Artifact("v2.initrd"), Artifact("v2.tar.gz"), new string('a', 64));
            File.WriteAllText(Path.Combine(Images, "maintenance.json"), JsonSerializer.Serialize(Kit with { Version = "v1" }, Json));
            KitName = Address(RuntimeActivation.MaintenancePrefix, Kit);
            Manifest = new(1, "v2", current, [old, baseImage], ["safe-merge"], KitName);
            File.WriteAllText(Active, JsonSerializer.Serialize(new RuntimeImageManifest(1, "v1", old, [baseImage], []), Json));
            Candidate = Address(RuntimeActivation.RuntimePrefix, Manifest);
        }
        public string Address<T>(string prefix, T value)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            var name = prefix + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + ".json";
            File.WriteAllBytes(Path.Combine(Images, name), bytes);
            return name;
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
