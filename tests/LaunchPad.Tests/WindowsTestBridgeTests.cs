using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class WindowsTestBridgeTests
{
    [Fact]
    public async Task SnapshotResultRoundTripPreservesOriginalAndCachesOneExecution()
    {
        var fixture = new Fixture();
        var request = fixture.Request(["result.txt"]);
        var executor = new Executor((_, workspace, _) =>
        {
            Assert.Equal("original source", File.ReadAllText(Path.Combine(workspace, "source.txt")));
            File.WriteAllText(Path.Combine(workspace, "source.txt"), "changed test copy");
            File.WriteAllText(Path.Combine(workspace, "result.txt"), "selected artifact");
            File.WriteAllText(Path.Combine(workspace, "stdout.txt"), "test log");
            return Task.FromResult(new WindowsTestExecution("finished", 7, StandardOutput: "stdout.txt"));
        });
        var bridge = new WindowsTestBridge(fixture.Runs, request.Generation, executor);
        var output = new MemoryStream();
        var first = await bridge.ProcessAsync(await fixture.Wire(request), output);
        Assert.Equal("finished", first.Outcome);
        Assert.Equal(7, first.ExitCode);
        Assert.Equal(2, first.Files.Length);
        output.Position = 0;
        var response = await WindowsTestProtocol.ReadResponseAsync(output, request.Generation, request.RequestId, fixture.Result("first"), default);
        Assert.Equal("selected artifact", File.ReadAllText(Path.Combine(fixture.Result("first"), "artifacts", "result.txt")));
        Assert.Equal("test log", File.ReadAllText(Path.Combine(fixture.Result("first"), "logs", "stdout.txt")));
        Assert.Equal("original source", File.ReadAllText(Path.Combine(fixture.Source, "source.txt")));
        var second = await bridge.ProcessAsync(await fixture.Wire(request), new MemoryStream());
        Assert.Equal(response.ExitCode, second.ExitCode);
        Assert.Equal(first.Files, second.Files);
        Assert.Equal(1, executor.Calls);
        Assert.Equal("changed test copy", File.ReadAllText(Path.Combine(fixture.Runs, request.Generation, request.RequestId, "workspace", "source.txt")));
    }

    [Fact]
    public async Task CrashAfterLaunchIntentNeverAutomaticallyStartsAgain()
    {
        var fixture = new Fixture();
        var request = fixture.Request();
        var executor = new Executor((_, _, _) => throw new InvalidOperationException("Owned simulated crash"));
        var bridge = new WindowsTestBridge(fixture.Runs, request.Generation, executor);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bridge.ProcessAsync(fixture.Wire(request).GetAwaiter().GetResult(), new MemoryStream()));
        var reopened = new WindowsTestBridge(fixture.Runs, request.Generation, executor);
        var result = await reopened.ProcessAsync(await fixture.Wire(request), new MemoryStream());
        Assert.Equal("interrupted", result.Outcome);
        Assert.Null(result.ExitCode);
        Assert.Contains("not be started again", result.Error);
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task SameRequestIdWithDifferentContentIsRefused()
    {
        var fixture = new Fixture();
        var request = fixture.Request();
        var executor = new Executor();
        var bridge = new WindowsTestBridge(fixture.Runs, request.Generation, executor);
        await bridge.ProcessAsync(await fixture.Wire(request), new MemoryStream());
        var changed = request with { Arguments = ["different-command"] };
        await Assert.ThrowsAsync<InvalidDataException>(() => bridge.ProcessAsync(fixture.Wire(changed).GetAwaiter().GetResult(), new MemoryStream()));
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task BadHashAndTruncatedSnapshotNeverExecuteAndRetainPartialCopy()
    {
        foreach (var truncate in new[] { false, true })
        {
            var fixture = new Fixture();
            var request = fixture.Request();
            var executor = new Executor();
            var wire = (await fixture.Wire(request)).ToArray();
            if (truncate) wire = wire[..^24];
            else
            {
                var metadataEnd = Array.IndexOf(wire, (byte)'\n') + 1;
                var size = int.Parse(Encoding.ASCII.GetString(wire, 0, metadataEnd - 1).Split(' ')[2]);
                wire[metadataEnd + size] ^= 1;
            }
            var result = await new WindowsTestBridge(fixture.Runs, request.Generation, executor).ProcessAsync(new MemoryStream(wire), new MemoryStream());
            Assert.Equal("failed", result.Outcome);
            Assert.Equal(0, executor.Calls);
            Assert.True(File.Exists(Path.Combine(fixture.Runs, request.Generation, request.RequestId, "ledger.json")));
            Assert.Equal("original source", File.ReadAllText(Path.Combine(fixture.Source, "source.txt")));
        }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("C:/private.txt")]
    [InlineData("folder/NUL.txt")]
    [InlineData("folder\\escape")]
    [InlineData(".launchpad-test/command.bin")]
    public void UnsafeGuestPathsAreRejectedBeforeStaging(string path)
    {
        var request = new Fixture().Request() with { Artifacts = [path] };
        Assert.Throws<InvalidDataException>(() => WindowsTestProtocol.Validate(request, request.Generation));
    }

    [Fact]
    public void WrongGenerationOversizedPayloadAndWindowsNameCollisionsAreRejected()
    {
        var request = new Fixture().Request();
        Assert.Throws<InvalidDataException>(() => WindowsTestProtocol.Validate(request, Guid.NewGuid().ToString("N")));
        var oversized = request with { Files = [new("large.dat", WindowsTestProtocol.MaximumSnapshotBytes + 1, new('a', 64))] };
        Assert.Throws<InvalidDataException>(() => WindowsTestProtocol.Validate(oversized, request.Generation));
        var duplicate = request with { Files = [new("FILE", 0, new('a', 64)), new("file", 0, new('a', 64))] };
        Assert.Throws<InvalidDataException>(() => WindowsTestProtocol.Validate(duplicate, request.Generation));
        var collision = request with { Files = [new("directory", 0, new('a', 64)), new("directory/file", 0, new('a', 64))] };
        Assert.Throws<InvalidDataException>(() => WindowsTestProtocol.Validate(collision, request.Generation));
    }

    [Fact]
    public async Task MissingArtifactStillReturnsLogsAndObservedExitCode()
    {
        var fixture = new Fixture();
        var request = fixture.Request(["absent.txt"]);
        var executor = new Executor((_, workspace, _) =>
        {
            File.WriteAllText(Path.Combine(workspace, "output.log"), "useful failure detail");
            return Task.FromResult(new WindowsTestExecution("finished", 13, StandardOutput: "output.log"));
        });
        var result = await new WindowsTestBridge(fixture.Runs, request.Generation, executor).ProcessAsync(await fixture.Wire(request), new MemoryStream());
        Assert.Equal("failed", result.Outcome);
        Assert.Equal(13, result.ExitCode);
        Assert.Single(result.Files);
        Assert.Contains("could not be returned", result.Error);
    }

    [Theory]
    [InlineData("failed", null)]
    [InlineData("canceled", null)]
    [InlineData("timed-out", null)]
    [InlineData("finished", 0)]
    public async Task MissingArtifactCannotEraseThePrimaryExecutorFailureOrChangeItOnReplay(string outcome, int? exitCode)
    {
        var fixture = new Fixture(); var request = fixture.Request(["absent.txt"]);
        const string primary = "Owned executor diagnostic";
        var executor = new Executor((_, _, _) => Task.FromResult(new WindowsTestExecution(outcome, exitCode, primary)));
        var bridge = new WindowsTestBridge(fixture.Runs, request.Generation, executor);
        var first = await bridge.ProcessAsync(await fixture.Wire(request), new MemoryStream());
        Assert.Equal(outcome == "finished" ? "failed" : outcome, first.Outcome); Assert.Equal(exitCode, first.ExitCode);
        Assert.StartsWith(primary + "\n", first.Error); Assert.Contains("could not be returned", first.Error);
        Assert.DoesNotContain("Windows test finished", first.Error);
        var second = await bridge.ProcessAsync(await fixture.Wire(request), new MemoryStream());
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(first), System.Text.Json.JsonSerializer.Serialize(second));
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task ConcurrentDuplicateCannotLaunchAnotherExecutor()
    {
        var fixture = new Fixture();
        var request = fixture.Request();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = new Executor(async (_, _, _) => { entered.SetResult(); await release.Task; return new("finished", 0); });
        var bridge = new WindowsTestBridge(fixture.Runs, request.Generation, executor);
        var first = bridge.ProcessAsync(await fixture.Wire(request), new MemoryStream());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await Assert.ThrowsAsync<IOException>(() => new WindowsTestBridge(fixture.Runs, request.Generation, executor)
                .ProcessAsync(fixture.Wire(request).GetAwaiter().GetResult(), new MemoryStream()));
            Assert.Equal(1, executor.Calls);
        }
        finally { release.SetResult(); await first; }
    }

    [Fact]
    public async Task CancellationHasDurableResultAndDoesNotReplay()
    {
        var fixture = new Fixture();
        var request = fixture.Request();
        var executor = new Executor((_, _, _) => throw new OperationCanceledException("owned canceled executor"));
        var bridge = new WindowsTestBridge(fixture.Runs, request.Generation, executor);
        var result = await bridge.ProcessAsync(await fixture.Wire(request), new MemoryStream());
        Assert.Equal("canceled", result.Outcome);
        await bridge.ProcessAsync(await fixture.Wire(request), new MemoryStream());
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task TestCreatedHardlinkCannotReturnAnotherHostFile()
    {
        Assert.True(OperatingSystem.IsWindows());
        var fixture = new Fixture();
        var outside = Path.Combine(fixture.Root, "owned-outside.txt");
        File.WriteAllText(outside, "owned private fixture; must not be returned");
        var request = fixture.Request(["linked.txt"]);
        var executor = new Executor((_, workspace, _) =>
        {
            Assert.True(CreateHardLink(Path.Combine(workspace, "linked.txt"), outside, 0));
            return Task.FromResult(new WindowsTestExecution("finished", 0));
        });
        var result = await new WindowsTestBridge(fixture.Runs, request.Generation, executor).ProcessAsync(await fixture.Wire(request), new MemoryStream());
        Assert.Equal("failed", result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.Files);
        Assert.Contains("Linked Windows test result", result.Error);
        Assert.Equal("owned private fixture; must not be returned", File.ReadAllText(outside));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    private static extern bool CreateHardLink(string name, string existing, nint attributes);

    [Fact]
    public async Task WrongResultIdentityAndDamagedResponseNeverBecomeAcceptedResults()
    {
        var fixture = new Fixture();
        var request = fixture.Request();
        var output = new MemoryStream();
        await new WindowsTestBridge(fixture.Runs, request.Generation, new Executor()).ProcessAsync(await fixture.Wire(request), output);
        output.Position = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => WindowsTestProtocol.ReadResponseAsync(output, request.Generation,
            Guid.NewGuid().ToString("N"), fixture.Result("wrong"), default));
        Assert.False(Directory.Exists(fixture.Result("wrong")));
        var truncated = new MemoryStream(output.ToArray()[..^8]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => WindowsTestProtocol.ReadResponseAsync(truncated, request.Generation,
            request.RequestId, fixture.Result("truncated"), default));
    }

    [Fact]
    public async Task CompiledRunnerBoundsBothLogsAndPropagatesExitOutsideRestrictedAccount()
    {
        Assert.True(OperatingSystem.IsWindows(), "This runner test is Windows-only; it is not restricted-account acceptance.");
        var fixture = new Fixture();
        var runner = Path.Combine(fixture.Root, "WindowsTestRunner.exe");
        using (var resource = typeof(ManagedWindowsTestExecutor).Assembly.GetManifestResourceStream("LaunchPad.WindowsTestRunner.exe")!)
        using (var output = File.Create(runner)) resource.CopyTo(output);
        var command = Path.Combine(fixture.Root, "command.bin");
        using (var writer = new BinaryWriter(File.Create(command)))
        {
            writer.Write("LaunchPad.WindowsTest.v1");
            writer.Write(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"));
            writer.Write(fixture.Root);
            string[] args = ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Out.Write('x' * 1100000); [Console]::Error.Write('y' * 1100000); exit 7"];
            writer.Write(args.Length);
            foreach (var argument in args) writer.Write(argument);
        }
        var start = new ProcessStartInfo(runner) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = fixture.Root };
        start.ArgumentList.Add(command);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        Assert.Equal(7, process.ExitCode);
        Assert.Equal(1024 * 1024, new FileInfo(Path.Combine(fixture.Root, "stdout.txt")).Length);
        Assert.Equal(1024 * 1024, new FileInfo(Path.Combine(fixture.Root, "stderr.txt")).Length);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "logs-truncated")));
    }

    private sealed class Executor(Func<WindowsTestRequest, string, CancellationToken, Task<WindowsTestExecution>>? run = null) : IWindowsTestExecutor
    {
        public int Calls { get; private set; }
        public Task<WindowsTestExecution> ExecuteAsync(WindowsTestRequest request, string workingCopy, CancellationToken token)
        { Calls++; return run?.Invoke(request, workingCopy, token) ?? Task.FromResult(new WindowsTestExecution("finished", 0)); }
    }

    private sealed class Fixture
    {
        public string Root { get; } = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration", "windows-bridge-20261007", "fixture-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Root, "original");
        public string Runs => Path.Combine(Root, "runs");
        public string Result(string name) => Path.Combine(Root, "received-" + name);
        public Fixture() { Directory.CreateDirectory(Source); File.WriteAllText(Path.Combine(Source, "source.txt"), "original source"); }
        public WindowsTestRequest Request(string[]? artifacts = null)
        {
            var path = Path.Combine(Source, "source.txt");
            return new(1, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "dotnet", null, ".", ["build"], false, 600,
                artifacts ?? [], [new("source.txt", new FileInfo(path).Length, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant())]);
        }
        public async Task<MemoryStream> Wire(WindowsTestRequest request)
        {
            var stream = new MemoryStream();
            await WindowsTestProtocol.WriteRequestAsync(stream, request, Source, default);
            stream.Position = 0;
            return stream;
        }
    }
}
