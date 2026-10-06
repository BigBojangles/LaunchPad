using System.Security.Cryptography;
using System.Text.Json;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class MaintenanceSafetyTests
{
    [Fact]
    public void MaintenanceDoesNotExpandAShortArgumentBeyondWindowsPathLimit()
    {
        var drive = Path.GetPathRoot(Path.GetTempPath())!;
        var cwd = Path.Combine(drive, new string('a', 110), new string('b', 110), new string('c', 20));
        var kernel = Path.Combine(drive, "owned-maintenance-kit", "kernel");
        Assert.True(Path.Combine(cwd, Path.GetRelativePath(cwd, kernel)).Length >= 260);
        var args = QemuCommand.BuildMaintenance(Path.Combine(cwd, "session.qcow2"), kernel,
            Path.Combine(drive, "owned-maintenance-kit", "initrd"), 24000, 24001, Path.Combine(drive, "firmware"), cwd, 24002);
        Assert.Equal("session.qcow2", args[args.ToList().IndexOf("-drive") + 1].Split(',')[0]["file=".Length..]);
        Assert.Equal(kernel, args[args.ToList().IndexOf("-kernel") + 1]);
        var ordinary = QemuCommand.Build("whpx", Path.Combine(cwd, "session.qcow2"), 0, 24000, "fence", null,
            firmwareDir: Path.GetDirectoryName(kernel), workingDirectory: cwd);
        Assert.Equal(Path.GetDirectoryName(kernel), ordinary[ordinary.ToList().IndexOf("-L") + 1]);
        Assert.Equal(@"C:\Agent.exe ""a b"" plain".Length,
            TestUserRunner.CommandLineLength(@"C:\Agent.exe", new[] { "a b", "plain" }));
    }

    [Fact]
    public void MaintenancePayloadChannelIsOfflineAndAbsentFromOrdinaryAgentLaunches()
    {
        var arguments = QemuCommand.BuildMaintenance("disk.qcow2", "kernel", "initrd", 24000, 24001, "share", ".", 24002);
        var network = arguments.ToList().IndexOf("-net");
        Assert.Equal("none", arguments[network + 1]);
        Assert.Contains("socket,id=p,host=127.0.0.1,port=24002,server=on,wait=off", arguments);
        Assert.Contains("virtserialport,bus=m.0,chardev=p,name=launchpad-maintenance", arguments);
        Assert.DoesNotContain(arguments, argument => argument.Contains("fsdev") || argument.Contains("hostfwd"));
        var ordinary = QemuCommand.Build("whpx", "session.qcow2", 0, 24000, "fence", null);
        Assert.DoesNotContain(ordinary, argument => argument.Contains("maintenance"));
    }

    [Fact]
    public void ChangedOfflineKitAndEscapingArtifactFailBeforeAnUpgradeCanUseThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPad-maintenance-" + Guid.NewGuid().ToString("N"));
        var images = Path.Combine(root, "images");
        Directory.CreateDirectory(images);
        try
        {
            foreach (var name in new[] { "kernel", "initrd", "payload" }) File.WriteAllText(Path.Combine(images, name), name);
            MaintenanceArtifact Artifact(string name) => new(name,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(images, name)))).ToLowerInvariant());
            var manifest = new MaintenanceManifest(1, "owned-test", Artifact("kernel"), Artifact("initrd"), Artifact("payload"), new string('a', 64));
            var path = Path.Combine(images, "maintenance.json");
            File.WriteAllText(path, JsonSerializer.Serialize(manifest));
            Assert.Equal(Path.Combine(images, "payload"), MaintenanceKit.Read(root).Payload);
            File.WriteAllText(Path.Combine(images, "payload"), "changed");
            Assert.Throws<InvalidDataException>(() => MaintenanceKit.Read(root));
            File.WriteAllText(path, JsonSerializer.Serialize(manifest with { Payload = manifest.Payload with { File = "../payload" } }));
            Assert.Throws<InvalidDataException>(() => MaintenanceKit.Read(root));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(images)) File.Delete(file);
            Directory.Delete(images);
            Directory.Delete(root);
        }
    }
}
