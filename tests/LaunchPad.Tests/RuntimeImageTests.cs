using System.Security.Cryptography;
using System.Text.Json;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class RuntimeImageTests
{
    [Fact]
    public void AVersionedImageKeepsTheOriginalAndVerifiesAllBackingIdentities()
    {
        using var fixture = new Fixture();
        var selected = RuntimeImages.Read(fixture.Root);
        Assert.EndsWith("debian-12-builder-v2.qcow2", selected.ImagePath);
        RuntimeImages.Verify(fixture.Root, selected);
        Assert.Equal("original", File.ReadAllText(Path.Combine(fixture.Images, "debian-12-builder.qcow2")));
        File.WriteAllText(Path.Combine(fixture.Images, "debian-12-builder.qcow2"), "changed backing");
        Assert.Throws<InvalidDataException>(() => RuntimeImages.Verify(fixture.Root, selected));
        Assert.Equal("new runtime", File.ReadAllText(selected.ImagePath));
    }

    [Fact]
    public void RestoredSizeAndTimestampDoNotHideChangedRuntimeContent()
    {
        using var fixture = new Fixture();
        var selected = RuntimeImages.Read(fixture.Root);
        RuntimeImages.Verify(fixture.Root, selected);
        var original = Path.Combine(fixture.Images, "debian-12-builder.qcow2");
        var stamp = File.GetLastWriteTimeUtc(original);
        File.WriteAllText(original, "modified");
        File.SetLastWriteTimeUtc(original, stamp);
        Assert.Throws<InvalidDataException>(() => RuntimeImages.Verify(fixture.Root, selected));
    }

    [Theory]
    [InlineData("../outside.qcow2")]
    [InlineData("nested/runtime.qcow2")]
    [InlineData("debian-12-nocloud.qcow2")]
    public void UnsafeOrNonBuilderSelectionsCannotFallBackSilently(string image)
    {
        using var fixture = new Fixture();
        fixture.Save(fixture.Manifest with { Image = fixture.Manifest.Image with { File = image } });
        Assert.Throws<InvalidDataException>(() => RuntimeImages.Read(fixture.Root));
        Assert.True(File.Exists(Path.Combine(fixture.Images, "debian-12-builder.qcow2")));
    }

    [Fact]
    public void AMissingOrChangedActiveImageIsRefusedWithoutTouchingSavedState()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Images, "debian-12-builder-v2.qcow2"), "tampered");
        Assert.Throws<InvalidDataException>(() => RuntimeImages.Verify(fixture.Root, RuntimeImages.Read(fixture.Root)));
        File.Delete(Path.Combine(fixture.Images, "debian-12-builder-v2.qcow2"));
        Assert.Throws<InvalidDataException>(() => RuntimeImages.Read(fixture.Root));
    }

    [Fact]
    public void AnAbsentManifestPreservesLegacyDiscoveryButACorruptManifestDoesNot()
    {
        using var fixture = new Fixture();
        File.Delete(Path.Combine(fixture.Images, RuntimeImages.ManifestName));
        Assert.Null(RuntimeImages.Read(fixture.Root).Manifest);
        File.WriteAllText(Path.Combine(fixture.Images, RuntimeImages.ManifestName), "{");
        Assert.Throws<InvalidDataException>(() => RuntimeImages.Read(fixture.Root));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N"));
        public string Images => Path.Combine(Root, "images");
        public RuntimeImageManifest Manifest { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Images);
            RuntimeImageFile FileRecord(string name, string content)
            {
                File.WriteAllText(Path.Combine(Images, name), content);
                return new(name, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))));
            }
            var original = FileRecord("debian-12-builder.qcow2", "original");
            var current = FileRecord("debian-12-builder-v2.qcow2", "new runtime");
            Manifest = new(1, "v2", current, [original], ["safe-merge"]);
            Save(Manifest);
        }
        public void Save(RuntimeImageManifest manifest) => File.WriteAllText(Path.Combine(Images, RuntimeImages.ManifestName),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
