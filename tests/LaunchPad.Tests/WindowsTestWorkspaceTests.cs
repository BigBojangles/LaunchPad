using LaunchPad.Services;
using Xunit;

namespace LaunchPad.Tests;

public sealed class WindowsTestWorkspaceTests
{
    [Fact]
    public void FinalResultRetainsEarlierProcessIdentityInHistory()
    {
        var root = NewFixture();
        WindowsTestWorkspace.SaveResult(root, new { stage = "process-started", processId = 123 });
        WindowsTestWorkspace.SaveResult(root, new { stage = "exited", exitCode = -2147023782 });
        var entries = File.ReadAllLines(Path.Combine(root, "events.jsonl"));
        Assert.Equal(2, entries.Length);
        Assert.Contains("process-started", entries[0]);
        Assert.Contains("123", entries[0]);
        Assert.Contains("exited", entries[1]);
        Assert.Contains("exited", File.ReadAllText(Path.Combine(root, "result.json")));
    }

    [Fact]
    public void BuiltInDemoExtractsACompiledWindowsExecutable()
    {
        if (!OperatingSystem.IsWindows()) return;
        var copy = WindowsTestWorkspace.StageDemo(Path.Combine(NewFixture(), "runs"));
        var bytes = File.ReadAllBytes(copy.Executable);
        Assert.True(bytes.Length > 1024);
        Assert.Equal((byte)'M', bytes[0]);
        Assert.Equal((byte)'Z', bytes[1]);
        Assert.True(File.Exists(Path.Combine(copy.Directory, "result.json")));
    }

    [Fact]
    public void ProgramCopiesDependenciesAndLeavesOriginalsUntouched()
    {
        var root = NewFixture();
        var project = Path.Combine(root, "project");
        var output = Path.Combine(project, "bin", "release");
        Directory.CreateDirectory(Path.Combine(output, "assets"));
        var exe = Path.Combine(output, "app.exe");
        File.WriteAllText(exe, "owned stand-in executable; never launched");
        File.WriteAllText(Path.Combine(output, "assets", "settings.json"), "original data");
        var copy = WindowsTestWorkspace.StageProgram(Path.Combine(root, "runs"), project, exe);
        Assert.NotEqual(exe, copy.Executable);
        Assert.Equal(File.ReadAllText(exe), File.ReadAllText(copy.Executable));
        var copiedData = Path.Combine(copy.Directory, "app", "assets", "settings.json");
        File.WriteAllText(copiedData, "changed test copy");
        Assert.Equal("original data", File.ReadAllText(Path.Combine(output, "assets", "settings.json")));
        Assert.True(File.Exists(Path.Combine(copy.Directory, "result.json")));
    }

    [Fact]
    public void RefusesOutsideProjectAndSelfRecursiveCopies()
    {
        var root = NewFixture();
        Directory.CreateDirectory(Path.Combine(root, "project"));
        var exe = Path.Combine(root, "other.exe");
        File.WriteAllText(exe, "never launched");
        Assert.Throws<ArgumentException>(() => WindowsTestWorkspace.StageProgram(Path.Combine(root, "runs"), Path.Combine(root, "project"), exe));
        var projectExe = Path.Combine(root, "project", "app.exe");
        File.WriteAllText(projectExe, "never launched");
        Assert.Throws<ArgumentException>(() => WindowsTestWorkspace.StageProgram(Path.Combine(root, "project", "recursive"), Path.Combine(root, "project"), projectExe));
    }

    [Fact]
    public void CancellationPreservesPartialCopyAndFailureReceipt()
    {
        var root = NewFixture();
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        var exe = Path.Combine(project, "app.exe");
        File.WriteAllText(exe, "never launched");
        var runs = Path.Combine(root, "runs");
        Assert.Throws<IOException>(() => WindowsTestWorkspace.StageProgram(runs, project, exe, new CancellationToken(true)));
        Assert.Equal("never launched", File.ReadAllText(exe));
        var retained = Assert.Single(Directory.GetDirectories(runs));
        Assert.Contains("copy-failed", File.ReadAllText(Path.Combine(retained, "result.json")));
    }

    private static string NewFixture()
    {
        var root = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration", "windows-test-copy-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        return root;
    }
}
