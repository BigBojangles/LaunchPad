using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class WindowsHostNetworkBoundaryTests
{
    [EnvironmentFact("LAUNCHPAD_NATIVE_HOST_NETWORK", "1")]
    [Trait("Category", "Integration")]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task DeferredPolicyProbeReportsMissingPolicyWithoutStartingAProcess()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Actual Windows filtering check required.");
        var root = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults",
            "migration", "host-network-missing-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        // Only an owned non-executable path-identity fixture, no app/runtime copy.
        var executable = Path.Combine(root, "qemu-system-x86_64.exe");
        await File.WriteAllTextAsync(executable, "owned policy-absence fixture; never execute");
        var before = Process.GetProcessesByName("LaunchPad").Select(value => { using (value) return value.Id; }).ToHashSet();
        var failure = Assert.Throws<Win32Exception>(() => WindowsHostNetworkBoundary.Require(executable));
        Assert.Contains(unchecked((uint)failure.NativeErrorCode), new uint[] { 0x80320003, 0x80320007 });
        var after = Process.GetProcessesByName("LaunchPad").Select(value => { using (value) return value.Id; }).ToHashSet();
        Assert.True(before.SetEquals(after), "The read-only policy probe must not start any process.");
        await File.WriteAllTextAsync(Path.Combine(root, "missing-policy-private.json"), JsonSerializer.Serialize(new
        {
            passed = true, executable, nativeError = unchecked((uint)failure.NativeErrorCode), before, after,
            assemblySha256 = GuestBaselineTests.HashFile(typeof(TestUserRunner).Assembly.Location),
            limitations = "Read-only actual Windows engine query for the deferred adapter. Ordinary production launches do not require this policy in the dogfood build. No filter installed, executable run, guest boot or network-denial claim."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
