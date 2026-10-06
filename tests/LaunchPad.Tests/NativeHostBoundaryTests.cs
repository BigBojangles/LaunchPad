using System.Diagnostics;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using LaunchPad.Services.Fence;
using Xunit;
using static LaunchPad.Tests.GuestBaselineTests;
using static LaunchPad.Tests.GuestResendTests;

namespace LaunchPad.Tests;

public sealed class NativeHostBoundaryTests
{
    [EnvironmentFact("LAUNCHPAD_NATIVE_HOST_BOUNDARY", "1")]
    [Trait("Category", "Integration")]
    public async Task FailedRestrictedHandoffNeverExecutesTheOwnedChild()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native Windows host boundary required.");
        await ProbeFailedHandoffWindows();
    }

    [SupportedOSPlatform("windows")]
    private static async Task ProbeFailedHandoffWindows(bool inspectBroker = false)
    {
        Assert.True(TestUserRunner.LaunchAccountReady(), "Existing launch identity required; no account changes permitted.");
        var root = Path.Combine(RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration",
            (inspectBroker ? "native-host-broker-control-" : "native-host-failed-handoff-") + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        await Run("icacls.exe", root, "/grant", TestUserRunner.UserName + ":(OI)(CI)M", "*S-1-5-12:(OI)(CI)M");
        var marker = Path.Combine(root, "must-not-execute.txt");
        var script = Path.Combine(root, "owned-child.ps1");
        await File.WriteAllTextAsync(script, "Set-Content -LiteralPath '" + marker.Replace("'", "''") + "' -Value 'executed'\n");
        var shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var command = "\"" + shell + "\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\"";
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var readPassword = typeof(TestUserRunner).GetMethod("ReadPassword", flags)!;
        var start = typeof(RestrictedHostLaunch).GetMethod("Start", flags)!;
        Process? suspended = null;
        var parked = false;
        var confirmedStopped = false;
        var passed = false;
        var error = 0;
        string? brokerReport = null;
        try
        {
            Func<int, nint, bool> rejectJob = (pid, childHandle) =>
            {
                suspended = Process.GetProcessById(pid);
                _ = suspended.StartTime;
                _ = suspended.Handle;
                parked = true;
                if (inspectBroker) brokerReport = ProbeLiveBrokerWindows(shell, root, pid);
                return false; // Real child exists, but handoff must fail before execution.
            };
            object?[] arguments = [shell, command, root, readPassword.Invoke(null, null), rejectJob, null, 0];
            try
            {
                Assert.False((bool)start.Invoke(null, arguments)!);
                error = (int)arguments[6]!;
                Assert.Null(arguments[5]);
            }
            finally { arguments[3] = null; } // Never record credentials in fixture evidence.
            Assert.True(parked, "The actual restricted child must reach the rejected handoff.");
            Assert.Equal(5, error);
            confirmedStopped = suspended!.WaitForExit(10000);
            Assert.True(confirmedStopped, "Failed handoff left its owned child running.");
            Assert.False(File.Exists(marker), "Failed handoff executed the requested program.");
            Assert.False(TestUserRunner.TryStart(Path.Combine(root, "missing-owned-program.exe"), root, [], out var missing));
            Assert.Null(missing);
            Assert.Equal(2, TestUserRunner.LastStartError);
            Assert.False(File.Exists(marker));
            if (inspectBroker)
            {
                using var report = JsonDocument.Parse(File.ReadAllText(brokerReport!));
                var observed = report.RootElement;
                Assert.True(observed.GetProperty("finished").GetBoolean());
                Assert.False(observed.GetProperty("isAdministrator").GetBoolean());
                AssertRestrictingSids(observed);
                Assert.EndsWith("\\" + TestUserRunner.UserName, observed.GetProperty("identity").GetString()!, StringComparison.OrdinalIgnoreCase);
                Assert.NotEmpty(observed.GetProperty("logonSids").EnumerateArray());
                Assert.All(observed.GetProperty("logonSids").EnumerateArray(), sid =>
                {
                    var tokenFlags = Convert.ToUInt32(sid.GetString()!.Split(':')[1], 16);
                    Assert.True((tokenFlags & 16) != 0 && (tokenFlags & 4) == 0, "Broker observer must already be restricted.");
                });
                Assert.All(observed.GetProperty("processRights").EnumerateArray()
                    .Where(entry => entry.GetProperty("name").GetString() != "QUERY_LIMITED_INFORMATION"), entry =>
                {
                    Assert.False(entry.GetProperty("opened").GetBoolean(), "Restricted sibling obtained trusted bootstrap rights: " + entry);
                    Assert.Equal(5, entry.GetProperty("win32Error").GetInt32());
                });
            }
            passed = true;
        }
        finally
        {
            // This handle belongs only to the new child captured by this fixture.
            if (suspended is not null && !suspended.WaitForExit(0))
            {
                suspended.Kill(entireProcessTree: true);
                suspended.WaitForExit(5000);
            }
            await File.WriteAllTextAsync(Path.Combine(root, "failed-handoff-private.json"), JsonSerializer.Serialize(new
            {
                root, passed, parked, error, confirmedStopped, markerExists = File.Exists(marker), inspectBroker, brokerReport,
                childPid = suspended?.Id, scriptSha256 = HashFile(script),
                assemblySha256 = HashFile(typeof(TestUserRunner).Assembly.Location),
                limitations = "Actual suspended restricted child, rejected job callback and missing owned executable. No requested child execution or unrestricted fallback; does not cover every request/receipt/timeout failure. No credentials recorded."
            }, new JsonSerializerOptions { WriteIndented = true }));
            suspended?.Dispose();
        }
    }

    [EnvironmentFact("LAUNCHPAD_NATIVE_HOST_BOUNDARY", "1")]
    [Trait("Category", "Integration")]
    public async Task ActualRestrictedSiblingCannotOpenTrustedBootstrapMutationRights()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native Windows host boundary required.");
        await ProbeFailedHandoffWindows(inspectBroker: true);
    }

    [SupportedOSPlatform("windows")]
    private static string ProbeLiveBrokerWindows(string shell, string root, int childPid)
    {
        var query = new ProcessStartInfo(shell)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "Get-CimInstance Win32_Process -Filter 'ProcessId = " + childPid + "' | Select-Object ParentProcessId | ConvertTo-Json -Compress" })
            query.ArgumentList.Add(argument);
        int brokerPid;
        using (var inspector = Process.Start(query)!)
        {
            var output = inspector.StandardOutput.ReadToEndAsync();
            var errors = inspector.StandardError.ReadToEndAsync();
            if (!inspector.WaitForExit(10000)) { inspector.Kill(); throw new IOException("Owned broker identity lookup timed out."); }
            Assert.Equal(0, inspector.ExitCode);
            using var identity = JsonDocument.Parse(output.GetAwaiter().GetResult());
            brokerPid = identity.RootElement.GetProperty("ParentProcessId").GetInt32();
        }
        return ProbeBrokerWindows(shell, root, brokerPid, "suspended-child handoff");
    }

    [SupportedOSPlatform("windows")]
    private static string ProbeBrokerWindows(string shell, string root, int brokerPid, string observationStage)
    {
        using var broker = Process.GetProcessById(brokerPid);
        Assert.Equal("LaunchPad", broker.ProcessName);
        var brokerStart = broker.StartTime.ToUniversalTime().Ticks;
        var brokerHandle = OpenProcess(0x20000, false, brokerPid); // READ_CONTROL only; no memory read or modification.
        if (brokerHandle == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        string sddl;
        try { sddl = ReadProcessAcl(brokerHandle); }
        finally { CloseHandle(brokerHandle); }
        var canaryRoot = root + "-private";
        Directory.CreateDirectory(canaryRoot); // Keep inherited private ACL; grant only the existing report root.
        var canary = Path.Combine(canaryRoot, "owned-canary.txt");
        File.WriteAllText(canary, "owned broker control");
        var config = Path.Combine(root, "configuration.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new
        {
            workspace = RepositoryRoot(), canary,
            owners = new[] { new { name = "owned-trusted-bootstrap", pid = brokerPid, start = brokerStart, sddl } }
        }));
        var source = Path.Combine(RepositoryRoot(), "scripts", "owned-host-boundary.ps1");
        var script = Path.Combine(root, "probe.ps1");
        File.Copy(source, script);
        Assert.True(TestUserRunner.TryStart(shell, root,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Configuration", config, "-Child"], out var started),
            "Owned restricted sibling could not start: " + TestUserRunner.LastStartError);
        using var sibling = started!;
        try
        {
            Assert.True(sibling.WaitForExit(10000), "Owned restricted sibling did not finish while bootstrap was live.");
            Assert.False(broker.WaitForExit(0), "The bootstrap must still be live when rights are observed.");
            Assert.Equal(brokerStart, broker.StartTime.ToUniversalTime().Ticks);
            File.WriteAllText(Path.Combine(root, "broker-owner-private.json"), JsonSerializer.Serialize(new
            {
                brokerPid, brokerStart, brokerLiveAfter = true, sddl, observationStage, siblingPid = sibling.Id, exitCode = sibling.ExitCode,
                sourceSha256 = HashFile(source), executedSha256 = HashFile(script),
                limitations = "Actual already-restricted sibling opens/closes owned live bootstrap rights only. No injection, token use or memory read. Observation stage is recorded separately; no broker ACL was modified."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            if (!sibling.WaitForExit(0)) { sibling.Kill(entireProcessTree: true); sibling.WaitForExit(5000); }
            TestUserRunner.ReleaseMachine(sibling.Id);
        }
        return Path.Combine(root, "host-boundary-child-private.json");
    }

    [EnvironmentFact("LAUNCHPAD_NATIVE_HOST_BOUNDARY", "1")]
    [Trait("Category", "Integration")]
    public async Task ActualLaunchAccountCannotOpenOwnedPrivateFilesOrOwnerProcessMutationRights()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native Windows host boundary required.");
        await ProbeWindows();
    }

    [SupportedOSPlatform("windows")]
    private static async Task ProbeWindows()
    {
        var reports = Path.Combine(RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration");
        var id = Guid.NewGuid().ToString("N")[..12];
        var root = Path.Combine(reports, "native-host-boundary-" + id);
        var privateRoot = Path.Combine(reports, "native-host-private-" + id);
        var readOnlyRoot = Path.Combine(reports, "native-host-readonly-" + id);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(privateRoot); // Keep normal inherited owner-folder ACLs.
        Directory.CreateDirectory(readOnlyRoot);
        var canary = Path.Combine(privateRoot, "owned-canary.txt");
        await File.WriteAllTextAsync(canary, "owned private canary " + Guid.NewGuid().ToString("N"));
        var before = HashFile(canary);
        var readOnlyCanary = Path.Combine(readOnlyRoot, "owned-readonly-canary.txt");
        await File.WriteAllTextAsync(readOnlyCanary, "owned runtime read-only control");
        var readOnlyBefore = HashFile(readOnlyCanary);
        RestrictedRuntimeAccess.ReadFile(readOnlyCanary);
        using var owner = Process.GetCurrentProcess();
        var ownerStart = owner.StartTime.ToUniversalTime().Ticks;
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var sentinelInfo = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 120" }) sentinelInfo.ArgumentList.Add(argument);
        using var sentinelOwner = new OwnedSentinel(Process.Start(sentinelInfo)!);
        var sentinel = sentinelOwner.Process;
        var sentinelStart = sentinel.StartTime.ToUniversalTime().Ticks;
        var owners = new[]
        {
            new { name = "owner-testhost", pid = owner.Id, start = ownerStart, sddl = ReadProcessAcl(owner.Handle) },
            new { name = "owned-owner-sentinel", pid = sentinel.Id, start = sentinelStart, sddl = ReadProcessAcl(sentinel.Handle) }
        };
        var config = Path.Combine(root, "configuration.json");
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new
        {
            workspace = RepositoryRoot(), canary, readOnlyCanary, owners
        }));
        var source = Path.Combine(RepositoryRoot(), "scripts", "owned-host-boundary.ps1");
        var script = Path.Combine(root, "probe.ps1");
        File.Copy(source, script);
        var sourceHash = HashFile(source);
        Assert.Equal(sourceHash, HashFile(script));
        await Run("icacls.exe", root, "/grant", TestUserRunner.UserName + ":(OI)(CI)M", "*S-1-5-12:(OI)(CI)M");
        Assert.True(TestUserRunner.TryStart(shell, root,
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Configuration", config], out var started),
            "Native launch-account probe error: " + TestUserRunner.LastStartError);
        using var process = started!;
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var report = Path.Combine(root, "host-boundary-private.json");
        try
        {
            while (!process.WaitForExit(0)) await Task.Delay(100, stop.Token);
        }
        finally
        {
            // Only this newly owned probe may be stopped on timeout.
            if (!process.WaitForExit(0)) process.Kill(entireProcessTree: true);
            TestUserRunner.ReleaseMachine(process.Id);
            var sentinelLive = !sentinel.HasExited && sentinelStart == sentinel.StartTime.ToUniversalTime().Ticks;
            using var identity = WindowsIdentity.GetCurrent();
            await File.WriteAllTextAsync(Path.Combine(root, "host-boundary-owner-private.json"), JsonSerializer.Serialize(new
            {
                root, privateRoot, canary, before, after = HashFile(canary), readOnlyCanary, readOnlyBefore,
                readOnlyAfter = HashFile(readOnlyCanary), ownerPid = owner.Id,
                ownerStart, finalOwnerStart = owner.StartTime.ToUniversalTime().Ticks,
                owners, sentinelLive, ownerIdentity = identity.Name, ownerSid = identity.User?.Value, exitCode = process.ExitCode,
                sourceHash, finalSourceHash = HashFile(source), executedHash = HashFile(script),
                assemblySha256 = HashFile(typeof(TestUserRunner).Assembly.Location),
                limitations = "Actual standard Windows launch account, inherited ACL owned canary and current owner test process. File/process rights are opened and closed only; no memory read, signalling, real user file, guest escape or exhaustive host protection claim. Granted modification only to this probe/report directory; private canary ACLs untouched."
            }, new JsonSerializerOptions { WriteIndented = true }));
            if (!sentinel.HasExited) { sentinel.Kill(); await sentinel.WaitForExitAsync(); } // Only newly owned sentinel.
        }
        // Foreign Process.GetProcessById cannot reliably expose ExitCode after
        // exit; terminal polling plus the actual completed report is the witness.
        if (!File.Exists(report))
        {
            var diagnosticCommand = "\"" + shell + "\" -NoProfile -NonInteractive -Command \"[Console]::WriteLine('owned-powershell-started')\" > \""
                + Path.Combine(root, "startup-output-private.txt") + "\" 2> \"" + Path.Combine(root, "startup-error-private.txt") + "\"";
            if (TestUserRunner.TryStart(Path.Combine(Environment.SystemDirectory, "cmd.exe"), root,
                ["/d", "/s", "/c", "\"" + diagnosticCommand + "\""], out var diagnostic))
            {
                using var owned = diagnostic!;
                try { Assert.True(owned.WaitForExit(10000), "Owned startup diagnostic must finish."); }
                finally
                {
                    if (!owned.WaitForExit(0)) { owned.Kill(entireProcessTree: true); owned.WaitForExit(5000); }
                    TestUserRunner.ReleaseMachine(owned.Id);
                }
            }
        }
        Assert.True(File.Exists(report));
        using var observed = JsonDocument.Parse(await File.ReadAllTextAsync(report));
        var data = observed.RootElement;
        Assert.True(data.GetProperty("finished").GetBoolean());
        Assert.EndsWith("\\" + TestUserRunner.UserName, data.GetProperty("identity").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.False(data.GetProperty("isAdministrator").GetBoolean());
        AssertRestrictingSids(data);
        Assert.Equal(0, data.GetProperty("childExit").GetInt32());
        Assert.True(data.GetProperty("stationAndDesktop")[0].GetString() != "WinSta0");
        Assert.StartsWith("LaunchPad-", data.GetProperty("stationAndDesktop")[1].GetString());
        Assert.NotEmpty(data.GetProperty("logonSids").EnumerateArray());
        Assert.All(data.GetProperty("logonSids").EnumerateArray(), sid =>
        {
            var flags = Convert.ToUInt32(sid.GetString()!.Split(':')[1], 16);
            Assert.True((flags & 16) != 0 && (flags & 4) == 0, "Actual logon grant still enabled.");
        });
        Assert.Equal("owned launch-account write", await File.ReadAllTextAsync(Path.Combine(root, "allowed-write.txt")));
        Assert.All(data.GetProperty("canary").EnumerateArray(), entry =>
        {
            Assert.False(entry.GetProperty("opened").GetBoolean(), "Unexpected native inherited-ACL canary access: " + entry);
            Assert.Equal(unchecked((int)0x80070005), entry.GetProperty("hresult").GetInt32());
        });
        AssertReadOnlyControl(data);
        Assert.All(data.GetProperty("processRights").EnumerateArray().Where(entry => entry.GetProperty("name").GetString() != "QUERY_LIMITED_INFORMATION"), entry =>
        {
            Assert.False(entry.GetProperty("opened").GetBoolean(), "Unexpected owner process rights: " + entry);
            Assert.Equal(5, entry.GetProperty("win32Error").GetInt32());
        });
        Assert.Equal(before, HashFile(canary));
        Assert.Equal(readOnlyBefore, HashFile(readOnlyCanary));
        Assert.Equal(ownerStart, owner.StartTime.ToUniversalTime().Ticks);
        Assert.Equal(sourceHash, HashFile(script));
        using var child = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "host-boundary-child-private.json")));
        var descendant = child.RootElement;
        Assert.True(descendant.GetProperty("finished").GetBoolean());
        Assert.Equal(data.GetProperty("identity").GetString(), descendant.GetProperty("identity").GetString());
        AssertRestrictingSids(descendant);
        Assert.Equal(data.GetProperty("logonSids").GetRawText(), descendant.GetProperty("logonSids").GetRawText());
        Assert.Equal(data.GetProperty("stationAndDesktop").GetRawText(), descendant.GetProperty("stationAndDesktop").GetRawText());
        Assert.All(descendant.GetProperty("processRights").EnumerateArray().Where(entry => entry.GetProperty("name").GetString() != "QUERY_LIMITED_INFORMATION"), entry =>
        {
            Assert.False(entry.GetProperty("opened").GetBoolean());
            Assert.Equal(5, entry.GetProperty("win32Error").GetInt32());
        });
        Assert.All(descendant.GetProperty("canary").EnumerateArray(), entry => Assert.False(entry.GetProperty("opened").GetBoolean()));
        AssertReadOnlyControl(descendant);
    }

    private static void AssertReadOnlyControl(JsonElement observed)
    {
        var controls = observed.GetProperty("readOnlyCanary").EnumerateArray().ToArray();
        Assert.Equal(2, controls.Length);
        Assert.True(controls.Single(entry => entry.GetProperty("access").GetString() == "Read").GetProperty("opened").GetBoolean());
        var write = controls.Single(entry => entry.GetProperty("access").GetString() == "Write");
        Assert.False(write.GetProperty("opened").GetBoolean());
        Assert.Equal(unchecked((int)0x80070005), write.GetProperty("hresult").GetInt32());
    }

    private static void AssertRestrictingSids(JsonElement observed) =>
        Assert.Equal(new[] { "S-1-1-0", "S-1-5-11", "S-1-5-12", "S-1-5-32-545" },
            observed.GetProperty("restrictingSids").EnumerateArray().Select(entry => entry.GetString()!).Order(StringComparer.Ordinal));

    [EnvironmentFact("LAUNCHPAD_NATIVE_HOST_BOUNDARY", "1")]
    [Trait("Category", "Integration")]
    public async Task ActualRestrictedSiblingCannotOpenBootstrapBeforeItsFirstInstruction()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native Windows host boundary required.");
        await ProbeSuspendedBrokerWindows();
    }

    [SupportedOSPlatform("windows")]
    private static async Task ProbeSuspendedBrokerWindows()
    {
        var root = Path.Combine(RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration",
            "native-host-broker-creation-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        await Run("icacls.exe", root, "/grant", TestUserRunner.UserName + ":(OI)(CI)M", "*S-1-5-12:(OI)(CI)M");
        var helper = Path.Combine(AppContext.BaseDirectory, "LaunchPad.exe");
        var shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var password = (string)typeof(TestUserRunner).GetMethod("ReadPassword", flags)!.Invoke(null, null)!;
        var startup = new BootstrapStartup { Size = Marshal.SizeOf<BootstrapStartup>() };
        BootstrapProcess bootstrap = default;
        string? reportPath = null;
        var passed = false;
        var stopped = false;
        try
        {
            // Identical native creation API/flags and default object security to
            // production. This owned helper is NEVER resumed or patched.
            if (!CreateProcessWithLogonW(TestUserRunner.UserName, ".", password, 1, helper,
                new System.Text.StringBuilder("\"" + helper + "\""), 0x08000004, 0,
                AppContext.BaseDirectory, ref startup, out bootstrap))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            password = "";
            reportPath = ProbeBrokerWindows(shell, root, bootstrap.ProcessId, "creation-suspended-before-first-instruction");
            using var observed = JsonDocument.Parse(File.ReadAllText(reportPath));
            var data = observed.RootElement;
            Assert.True(data.GetProperty("finished").GetBoolean());
            AssertRestrictingSids(data);
            Assert.All(data.GetProperty("processRights").EnumerateArray()
                .Where(entry => entry.GetProperty("name").GetString() != "QUERY_LIMITED_INFORMATION"), entry =>
            {
                Assert.False(entry.GetProperty("opened").GetBoolean(), "Restricted sibling obtained new bootstrap rights: " + entry);
                Assert.Equal(5, entry.GetProperty("win32Error").GetInt32());
            });
            passed = true;
        }
        finally
        {
            password = "";
            if (bootstrap.Process != 0)
            {
                TerminateProcess(bootstrap.Process, 1);
                stopped = WaitForSingleObject(bootstrap.Process, 5000) == 0;
                CloseHandle(bootstrap.Process);
            }
            if (bootstrap.Thread != 0) CloseHandle(bootstrap.Thread);
            await File.WriteAllTextAsync(Path.Combine(root, "creation-control-private.json"), JsonSerializer.Serialize(new
            {
                passed, stopped, reportPath, bootstrapPid = bootstrap.ProcessId, resumed = false, aclModified = false,
                assemblySha256 = HashFile(typeof(TestUserRunner).Assembly.Location),
                limitations = "Owned helper remains suspended throughout safe open/close observations. No credentials, memory read, injection, token use or signalling of unrelated processes."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Assert.True(stopped, "Owned suspended bootstrap cleanup must be observed.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct BootstrapStartup
    {
        public int Size; public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2; public nint ReservedPtr, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BootstrapProcess { public nint Process, Thread; public int ProcessId, ThreadId; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessWithLogonW(string user, string domain, string password, uint logonFlags, string exe, System.Text.StringBuilder command, uint creationFlags, nint environment, string directory, ref BootstrapStartup info, out BootstrapProcess process);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(nint process, uint code);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint timeout);

    [SupportedOSPlatform("windows")]
    private static string ReadProcessAcl(nint handle)
    {
        var error = GetSecurityInfo(handle, 6, 7, out _, out _, out _, out _, out var descriptor);
        if (error != 0) throw new System.ComponentModel.Win32Exception((int)error);
        nint text = 0;
        try
        {
            if (!ConvertSecurityDescriptorToStringSecurityDescriptor(descriptor, 1, 7, out text, out _))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return Marshal.PtrToStringUni(text)!;
        }
        finally { if (text != 0) LocalFree(text); LocalFree(descriptor); }
    }

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(nint handle, uint type, uint information,
        out nint owner, out nint group, out nint dacl, out nint sacl, out nint descriptor);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(nint descriptor,
        uint revision, uint information, out nint text, out uint length);
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    private sealed class OwnedSentinel(Process process) : IDisposable
    {
        public Process Process { get; } = process;
        public void Dispose()
        {
            if (!Process.HasExited) { Process.Kill(); Process.WaitForExit(5000); }
            Process.Dispose();
        }
    }
}
