using System.Runtime.InteropServices;
using System.Security.Principal;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class WindowsTestEnvironmentTests
{
    [Fact]
    public void AccountVariablesAndUnicodeSurviveWhileTemporaryPathsAreManaged()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "launchpad-env-temp");
        var logs = Path.Combine(Path.GetTempPath(), "launchpad-env-logs");
        var block = WindowsTestEnvironment.BuildBlock(["USERNAME=BuildLaunchTest", "USERPROFILE=C:\\Users\\BuildLaunchTest",
            "PATH=C:\\Windows;C:\\Tools", "TEMP=old", "TMP=old", "COMPLUS_CLRLoadLogDir=old", "LABEL=naïve=task", "=C:=C:\\work"], temporary, logs);
        Assert.EndsWith("\0\0", block);
        var entries = block.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("USERNAME=BuildLaunchTest", entries);
        Assert.Contains("USERPROFILE=C:\\Users\\BuildLaunchTest", entries);
        Assert.Contains("PATH=C:\\Windows;C:\\Tools", entries);
        Assert.Contains("LABEL=naïve=task", entries);
        Assert.Contains("=C:=C:\\work", entries);
        Assert.Contains("TEMP=" + Path.GetFullPath(temporary), entries);
        Assert.Contains("TMP=" + Path.GetFullPath(temporary), entries);
        Assert.Contains("COMPLUS_CLRLoadLogDir=" + Path.GetFullPath(logs), entries);
    }

    [Fact]
    public void ActualWindowsAccountEnvironmentDoesNotInheritProcessOnlyValues()
    {
        if (!OperatingSystem.IsWindows()) return;
        var name = "LAUNCHPAD_ENV_PROBE_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "must-not-reach-child");
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            using var environment = WindowsTestEnvironment.ForUser(identity.Token,
                Path.Combine(Path.GetTempPath(), "launchpad-env-temp"), Path.Combine(Path.GetTempPath(), "launchpad-env-logs"));
            var entries = new List<string>();
            var cursor = environment.Block;
            while (Marshal.ReadInt16(cursor) != 0)
            {
                var entry = Marshal.PtrToStringUni(cursor)!;
                entries.Add(entry);
                cursor += (entry.Length + 1) * 2;
            }
            Assert.DoesNotContain(entries, entry => entry.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
            Assert.False(string.IsNullOrWhiteSpace(environment.UserName));
            Assert.False(string.IsNullOrWhiteSpace(environment.UserProfile));
            Assert.EndsWith("\\" + environment.UserName, identity.Name, StringComparison.OrdinalIgnoreCase);
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }
}
