using System.Diagnostics;
using System.Text.Json;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public class HostSecurityAuditTests
{
    [EnvironmentFact("LAUNCHPAD_HOST_AUDIT_CONFIG")]
    [Trait("Category", "Integration")]
    public void SecurityToolsRunAsTheActualStandardLaunchAccount()
    {
        var config = Environment.GetEnvironmentVariable("LAUNCHPAD_HOST_AUDIT_CONFIG")!;
        var root = Path.GetDirectoryName(config)!;
        var script = Path.Combine(root, "security-host-windows.ps1");
        Assert.True(File.Exists(script), "Host audit script was not staged by security-audit.ps1.");
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var args = new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Configuration", config };
        Assert.True(TestUserRunner.TryStart(powershell, root, args, out var process), "Host audit launch failed: " + TestUserRunner.LastStartError);
        Assert.NotNull(process);
        using var worker = process!;
        try
        {
            Assert.True(worker.WaitForExit(20 * 60 * 1000), "Host audit timed out; coverage is incomplete.");
        }
        finally
        {
            try { if (!worker.HasExited) worker.Kill(entireProcessTree: true); }
            finally { TestUserRunner.ReleaseMachine(worker.Id); }
        }
        var reportPath = Path.Combine(root, "host-audit.json");
        Assert.True(File.Exists(reportPath), "Host audit produced no identity/coverage report.");
        using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
        Assert.True(report.RootElement.GetProperty("identityMatches").GetBoolean(), "Host audit did not run as the non-administrator launch account.");
        var tools = report.RootElement.GetProperty("tools").EnumerateArray().ToArray();
        foreach (var required in new[] { "winpeas", "hardeningkitty-machine", "hardeningkitty-user" })
        {
            var found = tools.Where(item => item.GetProperty("id").GetString() == required).ToArray();
            Assert.True(found.Length == 1 && found[0].GetProperty("execution").GetString() == "finished", required + " mandatory coverage blocked; inspect the private host-audit.json.");
        }
        // This proves execution identity only. The security entry point still requires
        // coverage review and finding triage before it can issue a security verdict.
    }
}
