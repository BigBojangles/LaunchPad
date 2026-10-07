using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class WindowsSetupRepairTests
{
    [Fact]
    public void ActualCompiledRuntimeDependenciesResolveWithoutGrantingTestOrReportInputs()
    {
        var plan = WindowsRuntimeRepair.Plan(AppContext.BaseDirectory, includeVm: false);
        Assert.Contains(Path.Combine(plan.Root, "Avalonia.Base.dll"), plan.ReadPaths);
        Assert.Contains(plan.ReadPaths, path => path.EndsWith("av_libglesv2.dll", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(plan.ReadPaths.Select(path => Path.GetRelativePath(plan.Root, path)), relative => relative.Contains("LaunchPad.Tests") || relative.StartsWith("TestResults"));
        Assert.DoesNotContain(Path.Combine(plan.Root, "LaunchPad.Tests.dll"), plan.ReadPaths);
        Assert.Null(plan.SessionsDirectory);
    }

    [Fact]
    public void FullRuntimeRepairVisitsOnlyRequiredInputsAndPreservesSavedFiles()
    {
        var root = Fixture(full: true);
        var saved = Path.Combine(root, "sessions", "existing", "session.qcow2");
        var project = Path.Combine(root, "projects", "real-name", "unfinished.txt");
        var credential = Path.Combine(root, "settings", "fence-user.bin");
        Write(saved, "saved VM work"); Write(project, "unfinished project"); Write(credential, "existing ciphertext");
        Write(Path.Combine(root, "notes", "private.txt"), "not a runtime input");
        var before = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        var plan = WindowsRuntimeRepair.Plan(root, includeVm: true);
        var grants = new List<string>();
        WindowsRuntimeRepair.Apply(plan, grants.Add);
        Assert.Contains(Path.Combine(root, "images", "debian-12-builder.qcow2"), grants);
        Assert.Contains(Path.Combine(root, "images", "base.qcow2"), grants);
        Assert.Contains(Path.Combine(root, "qemu", "share", "firmware.bin"), grants);
        Assert.Contains(Path.Combine(root, "sessions"), grants);
        Assert.DoesNotContain(grants, path => path.StartsWith(Path.Combine(root, "sessions") + Path.DirectorySeparatorChar));
        Assert.DoesNotContain(grants.Select(path => Path.GetRelativePath(root, path)), relative => relative.StartsWith("projects") || relative.StartsWith("settings") || relative.StartsWith("notes"));
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void NativeRepairNeedsNoVmAndDoesNotVisitItsExistingDirectories()
    {
        var root = Fixture(full: false);
        Write(Path.Combine(root, "images", "broken.qcow2"), "broken but irrelevant");
        Write(Path.Combine(root, "sessions", "existing", "session.qcow2"), "saved unrelated disk");
        var plan = WindowsRuntimeRepair.Plan(root, includeVm: false);
        var grants = new List<string>(); WindowsRuntimeRepair.Apply(plan, grants.Add);
        Assert.Null(plan.SessionsDirectory);
        Assert.DoesNotContain(grants, path => path.Contains("images") || path.Contains("sessions") || path.Contains("qemu"));
        Assert.Contains(Path.Combine(root, "runtimes", "win-x64", "native", "fixture.dll"), grants);
        Assert.Equal("saved unrelated disk", File.ReadAllText(Path.Combine(root, "sessions", "existing", "session.qcow2")));
        Assert.Equal(new HypervisorEnableResult(true, true), HypervisorCheck.FeatureResult(3010));
        Assert.Equal(new HypervisorEnableResult(false, false), HypervisorCheck.FeatureResult(5));
    }

    [Fact]
    public void MissingDependencyOrChangedPlanFailsBeforeAnyGrant()
    {
        var root = Fixture(full: false);
        var deps = Path.Combine(root, "LaunchPad.deps.json");
        var plan = WindowsRuntimeRepair.Plan(root, false);
        File.Delete(deps);
        Assert.Throws<IOException>(() => WindowsRuntimeRepair.Plan(root, false));
        var grants = new List<string>();
        Assert.Throws<IOException>(() => WindowsRuntimeRepair.Apply(plan, grants.Add));
        Assert.Empty(grants);
        Assert.Equal("app", File.ReadAllText(Path.Combine(root, "LaunchPad.exe")));
    }

    [Fact]
    public void InvalidImageProvenanceOrOutsideBackingFailsWithoutTouchingSavedWork()
    {
        var root = Fixture(full: true);
        var builder = Path.Combine(root, "images", "debian-12-builder.qcow2");
        var saved = Path.Combine(root, "sessions", "existing", "session.qcow2");
        Write(saved, "recover this");
        var manifest = new RuntimeImageManifest(1, "owned-fixture", new("debian-12-builder.qcow2", new string('0', 64)), [], []);
        File.WriteAllText(Path.Combine(root, "images", RuntimeImages.ManifestName), JsonSerializer.Serialize(manifest));
        Assert.Throws<InvalidDataException>(() => WindowsRuntimeRepair.Plan(root, true));
        File.Delete(Path.Combine(root, "images", RuntimeImages.ManifestName));
        Disk(builder, "../../outside.qcow2");
        Assert.Throws<InvalidDataException>(() => WindowsRuntimeRepair.Plan(root, true));
        Assert.Equal("recover this", File.ReadAllText(saved));
    }

    [Fact]
    public void FailedCredentialReplacementPreservesExistingBytesAndRemovesOnlyItsTemporaryFile()
    {
        var root = Fixture(full: false);
        var path = Path.Combine(root, "owned-cipher.bin");
        File.WriteAllBytes(path, [1, 2, 3]);
        var method = typeof(TestUserRunner).GetMethod("StoreProtectedPassword", BindingFlags.Static | BindingFlags.NonPublic)!;
        using (var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, new object[] { new byte[] { 4, 5, 6 }, path, true }));
            Assert.True(error.InnerException is IOException or UnauthorizedAccessException);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        }
        Assert.Throws<TargetInvocationException>(() => method.Invoke(null, new object[] { new byte[] { 7 }, path, false }));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
        Assert.Empty(Directory.EnumerateFiles(root, "owned-cipher.bin.*.tmp"));
        method.Invoke(null, new object[] { new byte[] { 9 }, path, true });
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task AccountHelperRepairsMembershipWithoutResettingAnExistingPassword()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Fixture(full: false);
        var script = Path.Combine(root, "account-helper.ps1");
        var wrapper = Path.Combine(root, "owned-helper-fixture.ps1");
        var report = Path.Combine(root, "mocked-account-report.json");
        var secret = Path.Combine(root, "owned-fixture.secret");
        File.WriteAllText(secret, "owned-fixture-not-a-real-account-password", new UTF8Encoding(false));
        File.WriteAllText(script, LaunchAccountSetup.AccountScript, new UTF8Encoding(false));
        File.WriteAllText(wrapper, MockedAccountFixture, new UTF8Encoding(false));
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", wrapper, "-Helper", script, "-SecretFile", secret, "-Report", report }) start.ArgumentList.Add(arg);
        using var worker = Process.Start(start)!;
        var output = worker.StandardOutput.ReadToEndAsync(); var errors = worker.StandardError.ReadToEndAsync();
        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(worker.ExitCode == 0, await output + await errors);
        using var data = JsonDocument.Parse(File.ReadAllText(report));
        var old = data.RootElement.GetProperty("existing");
        Assert.Equal("S-1-5-21-1-2-3-9000", old.GetProperty("sid").GetString());
        Assert.Equal(0, old.GetProperty("created").GetInt32());
        Assert.True(old.GetProperty("enabled").GetBoolean());
        Assert.True(old.GetProperty("users").GetBoolean()); Assert.False(old.GetProperty("admins").GetBoolean());
        var fresh = data.RootElement.GetProperty("fresh");
        Assert.Equal(1, fresh.GetProperty("created").GetInt32());
        Assert.True(fresh.GetProperty("users").GetBoolean()); Assert.False(fresh.GetProperty("admins").GetBoolean());
        Assert.Equal("owned-fixture-not-a-real-account-password", File.ReadAllText(secret));
    }

    private static string Fixture(bool full)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestResults", "migration", "setup-repair-20261006", "owned-" + Guid.NewGuid().ToString("N")));
        Write(Path.Combine(root, "LaunchPad.exe"), "app");
        Write(Path.Combine(root, "LaunchPad.dll"), "app-dll");
        Write(Path.Combine(root, "LaunchPad.runtimeconfig.json"), "{}");
        Write(Path.Combine(root, "LaunchPad.deps.json"), """
            {"targets":{"owned-fixture":{"LaunchPad/1":{"runtime":{"LaunchPad.dll":{},"lib/net8.0/owned-library.dll":{}},"runtimeTargets":{"runtimes/win-x64/native/fixture.dll":{"rid":"win-x64"},"runtimes/linux-x64/native/unneeded.so":{"rid":"linux-x64"}}}}}}
            """);
        Write(Path.Combine(root, "runtimes", "win-x64", "native", "fixture.dll"), "owned native library");
        Write(Path.Combine(root, "owned-library.dll"), "owned flattened library");
        if (full)
        {
            Write(Path.Combine(root, "qemu", "qemu-img.exe"), "owned img stand-in");
            Write(Path.Combine(root, "qemu", "fence", "qemu-system-x86_64.exe"), "owned runtime stand-in");
            Write(Path.Combine(root, "qemu", "share", "firmware.bin"), "owned firmware stand-in");
            Disk(Path.Combine(root, "images", "base.qcow2"));
            Disk(Path.Combine(root, "images", "debian-12-builder.qcow2"), "base.qcow2");
        }
        return root;
    }
    private static void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
    private static void Disk(string path, string? backing = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var name = Encoding.UTF8.GetBytes(backing ?? ""); var bytes = new byte[104 + name.Length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), 0x514649fb);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4, 4), 3);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(100, 4), 104);
        if (backing is not null)
        {
            BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(8, 8), 104);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), (uint)name.Length); name.CopyTo(bytes, 104);
        }
        File.WriteAllBytes(path, bytes);
    }

    private const string MockedAccountFixture = """
        param([string]$Helper, [string]$SecretFile, [string]$Report)
        $ErrorActionPreference = 'Stop'
        $tokens = $null; $parseErrors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile($Helper, [ref]$tokens, [ref]$parseErrors)
        if ($parseErrors.Count) { throw 'The owned helper did not parse.' }
        $allowed = @('Get-LocalUser','New-LocalUser','Enable-LocalUser','Get-LocalGroup','Get-LocalGroupMember','Add-LocalGroupMember','Remove-LocalGroupMember','ConvertTo-SecureString','Where-Object','Out-Null')
        foreach ($command in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] }, $true)) {
            if ($allowed -notcontains $command.GetCommandName()) { throw 'Unexpected command in owned helper.' }
        }
        function global:Get-LocalUser { [CmdletBinding()]param($Name) $global:fixtureAccount }
        function global:New-LocalUser {
            param($Name,$Password,[switch]$PasswordNeverExpires,[switch]$UserMayNotChangePassword,[switch]$AccountNeverExpires)
            if ($global:fixtureAccount -or $Password -ne 'owned-fixture-not-a-real-account-password') { throw 'Invalid mocked creation.' }
            $global:fixtureCreated++; $global:fixtureAccount = [pscustomobject]@{ SID='S-1-5-21-1-2-3-9001'; Enabled=$false }
        }
        function global:ConvertTo-SecureString { param($String,[switch]$AsPlainText,[switch]$Force) $String }
        function global:Enable-LocalUser { param($Name) $global:fixtureAccount.Enabled = $true }
        function global:Get-LocalGroup {
            param($SID)
            if ($SID -eq 'S-1-5-32-545') { return [pscustomobject]@{ Name='Benutzer_fixture' } }
            if ($SID -eq 'S-1-5-32-544') { return [pscustomobject]@{ Name='Administratoren_fixture' } }
            throw 'Unexpected group SID.'
        }
        function global:Get-LocalGroupMember {
            param($Group)
            if (($Group -eq 'Benutzer_fixture' -and $global:fixtureUsers) -or ($Group -eq 'Administratoren_fixture' -and $global:fixtureAdmins)) { [pscustomobject]@{ SID=$global:fixtureAccount.SID } }
        }
        function global:Add-LocalGroupMember { param($Group,$Member) if ($Group -ne 'Benutzer_fixture') { throw 'Wrong mocked group.' }; $global:fixtureUsers=$true }
        function global:Remove-LocalGroupMember { param($Group,$Member) if ($Group -ne 'Administratoren_fixture') { throw 'Wrong mocked group.' }; $global:fixtureAdmins=$false }
        function global:Set-LocalUser { throw 'Existing passwords must never be reset.' }
        $global:fixtureAccount = [pscustomobject]@{ SID='S-1-5-21-1-2-3-9000'; Enabled=$false }
        $global:fixtureUsers=$false; $global:fixtureAdmins=$true; $global:fixtureCreated=0
        & $Helper -Path ($SecretFile + '.does-not-exist')
        $existing = @{sid=$global:fixtureAccount.SID;enabled=$global:fixtureAccount.Enabled;users=$global:fixtureUsers;admins=$global:fixtureAdmins;created=$global:fixtureCreated}
        $global:fixtureAccount=$null; $global:fixtureUsers=$false; $global:fixtureAdmins=$false; $global:fixtureCreated=0
        & $Helper -Path $SecretFile
        $fresh = @{sid=$global:fixtureAccount.SID;enabled=$global:fixtureAccount.Enabled;users=$global:fixtureUsers;admins=$global:fixtureAdmins;created=$global:fixtureCreated}
        @{existing=$existing;fresh=$fresh} | ConvertTo-Json | Set-Content -LiteralPath $Report -Encoding UTF8
        """;
}
