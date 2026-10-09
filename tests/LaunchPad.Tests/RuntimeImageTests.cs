using System.Security.Cryptography;
using System.Text.Json;
using System.IO.Compression;
using System.Text;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class RuntimeImageTests
{
    [Theory]
    [InlineData(AgentChoice.Codex)]
    [InlineData(AgentChoice.Claude)]
    [InlineData(AgentChoice.Custom)]
    public void DeclaredAgentChoiceAllowsAgentsWithoutPlaintextImageMarkers(string agent)
    {
        using var fixture = new Fixture();
        var selected = fixture.CompressedMarkers([AgentChoice.Capability]);
        Assert.False(ConfigHeal.ImageHasText(selected.ImagePath, AgentChoice.Marker));
        RuntimeImages.Verify(fixture.Root, selected);
        Assert.True(AgentChoice.RuntimeSupports(new(agent, null, null), selected.ImagePath, selected.Manifest!.Capabilities));
        Assert.False(ConfigHeal.ImageHasConfigFix(selected.ImagePath, selected.Manifest.Capabilities));
    }

    [Fact]
    public void ConfigRecoveryUsesItsOwnCapabilityOnCompressedPayloads()
    {
        using var fixture = new Fixture();
        var selected = fixture.CompressedMarkers([ConfigHeal.Capability]);
        Assert.False(ConfigHeal.ImageHasConfigFix(selected.ImagePath));
        Assert.True(ConfigHeal.ImageHasConfigFix(selected.ImagePath, selected.Manifest!.Capabilities));
        Assert.False(AgentChoice.RuntimeSupports(new(AgentChoice.Codex, null, null), selected.ImagePath, selected.Manifest.Capabilities));
    }

    [Theory]
    [InlineData(AgentChoice.Codex)]
    [InlineData(AgentChoice.Claude)]
    [InlineData(AgentChoice.Custom)]
    public void LegacyMarkersRemainAFallbackWithoutDeclaringCapabilities(string agent)
    {
        using var fixture = new Fixture();
        var selected = RuntimeImages.Read(fixture.Root);
        Assert.False(AgentChoice.RuntimeSupports(new(agent, null, null), selected.ImagePath, []));
        Assert.True(AgentChoice.RuntimeSupports(AgentLaunch.Grok, selected.ImagePath, []));
        File.WriteAllText(selected.ImagePath, AgentChoice.Marker + "\n" + ConfigHeal.Marker);
        Assert.True(AgentChoice.RuntimeSupports(new(agent, null, null), selected.ImagePath, []));
        Assert.True(ConfigHeal.ImageHasConfigFix(selected.ImagePath, []));
    }

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

    [Fact]
    public void StandaloneSelectionDoesNotRequireOrReadHistoricalBackingImages()
    {
        using var fixture = new Fixture();
        fixture.Save(fixture.Manifest with { Dependencies = Array.Empty<RuntimeImageFile>() });
        File.Delete(Path.Combine(fixture.Images, "debian-12-builder.qcow2"));
        var selected = RuntimeImages.Read(fixture.Root);
        RuntimeImages.Verify(fixture.Root, selected);
        Assert.Empty(selected.Manifest!.Dependencies);
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
        public RuntimeImageSelection CompressedMarkers(IReadOnlyList<string> capabilities)
        {
            var path = Path.Combine(Images, Manifest.Image.File);
            using (var output = File.Create(path))
            using (var compressed = new ZLibStream(output, CompressionLevel.SmallestSize))
                compressed.Write(Encoding.ASCII.GetBytes(AgentChoice.Marker + "\n" + ConfigHeal.Marker));
            Save(Manifest with { Image = Manifest.Image with { Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) }, Capabilities = capabilities });
            return RuntimeImages.Read(Root);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
