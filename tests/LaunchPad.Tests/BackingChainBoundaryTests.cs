using System.Buffers.Binary;
using System.Text;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class BackingChainBoundaryTests
{
    [Fact]
    public void SavedChildCanReferenceItsProjectAndAnOlderRuntimeTemplate()
    {
        var root = Root();
        var project = Path.Combine(root, "project");
        var images = Path.Combine(root, "images");
        var template = Image(images, "old-template.qcow2", null);
        var original = Image(project, "session.qcow2", template);
        var child = Image(Path.Combine(project, "upgrade-owned"), "session.qcow2", "../session.qcow2");
        Assert.Equal(new[] { child, original, template }, RestrictedRuntimeAccess.BackingChain(child, project, images));
    }

    [Fact]
    public void OtherProjectAndForeignBackingPathsAreRejectedBeforeProvisioning()
    {
        var root = Root();
        var project = Path.Combine(root, "project");
        var foreign = Image(Path.Combine(root, "other-project"), "session.qcow2", null);
        var original = Image(project, "session.qcow2", foreign);
        Assert.Throws<InvalidDataException>(() => RestrictedRuntimeAccess.BackingChain(original, project, Path.Combine(root, "images")));
    }

    [Fact]
    public void CyclicBackingReferencesAreRejected()
    {
        var root = Root();
        var first = Image(root, "first.qcow2", "second.qcow2");
        Image(root, "second.qcow2", "first.qcow2");
        Assert.Throws<InvalidDataException>(() => RestrictedRuntimeAccess.BackingChain(first, root, Path.Combine(root, "images")));
    }

    [Fact]
    public void TruncatedPathAndExternalDataFileMetadataAreRejected()
    {
        var root = Root();
        var truncated = Image(root, "truncated.qcow2", "missing.qcow2");
        using (var file = new FileStream(truncated, FileMode.Open, FileAccess.Write)) file.SetLength(104);
        Assert.Throws<InvalidDataException>(() => RestrictedRuntimeAccess.BackingChain(truncated, root, Path.Combine(root, "images")));
        var external = Image(root, "external.qcow2", null);
        var bytes = File.ReadAllBytes(external);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(72, 8), 4);
        File.WriteAllBytes(external, bytes);
        Assert.Throws<InvalidDataException>(() => RestrictedRuntimeAccess.BackingChain(external, root, Path.Combine(root, "images")));
    }

    // Metadata-only boundary fixtures, not valid bootable guest images. Actual
    // native qemu-img/maintenance proof exercises the complete real chain.
    private static string Image(string directory, string name, string? backing)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        var text = backing is null ? [] : Encoding.UTF8.GetBytes(backing);
        var bytes = new byte[104 + text.Length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), 0x514649fb);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4, 4), 3);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(100, 4), 104);
        if (text.Length > 0)
        {
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8, 8), 104);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), (uint)text.Length);
            text.CopyTo(bytes, 104);
        }
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string Root()
    {
        var root = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration",
            "backing-chain-control-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        return root;
    }
}
