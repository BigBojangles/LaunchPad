using System.Security.Cryptography;
using System.Text.Json;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class WindowsTestCodecIntegrationTests
{
    [EnvironmentFact("LAUNCHPAD_WINDOWS_CODEC_INTEROP", "1")]
    [Trait("Category", "Integration")]
    public async Task ActualLinuxClientPacketIsConsumedAndAnsweredByWindowsCodec()
    {
        var root = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests", "TestResults", "migration", "windows-bridge-20261007");
        using var identity = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "python-request-identity.json")));
        using var proof = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "linux-client-proof-private.json")));
        var source = Path.Combine(GuestBaselineTests.RepositoryRoot(), "scripts", "windows-test-client.py");
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant(), proof.RootElement.GetProperty("clientSha256").GetString());
        var generation = identity.RootElement.GetProperty("generation").GetString()!;
        var requestId = identity.RootElement.GetProperty("requestId").GetString()!;
        using var input = File.OpenRead(Path.Combine(root, "python-request.bin"));
        using var response = new MemoryStream();
        var result = await new WindowsTestBridge(Path.Combine(root, "interop-runs"), generation, new Executor()).ProcessAsync(input, response);
        Assert.Equal("finished", result.Outcome);
        Assert.Equal(requestId, result.RequestId);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal(2, result.Files.Length);
        File.WriteAllBytes(Path.Combine(root, "dotnet-response.bin"), response.ToArray());
    }

    private sealed class Executor : IWindowsTestExecutor
    {
        public Task<WindowsTestExecution> ExecuteAsync(WindowsTestRequest request, string workingCopy, CancellationToken token)
        {
            Assert.Equal("Unicode source: naïve project\n", File.ReadAllText(Path.Combine(workingCopy, "source.txt")));
            Assert.Equal(new[] { "build" }, request.Arguments);
            File.WriteAllText(Path.Combine(workingCopy, "result.txt"), "Windows codec artifact");
            File.WriteAllText(Path.Combine(workingCopy, "stdout.txt"), "Windows codec log");
            return Task.FromResult(new WindowsTestExecution("finished", 7, StandardOutput: "stdout.txt"));
        }
    }
}
