using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class SessionGuardianTests
{
    [Theory]
    [InlineData("{\"outcome\":\"shutdown-unverified\",\"needsRecovery\":false}", true)]
    [InlineData("{\"outcome\":\"guest-shutdown\",\"needsRecovery\":true}", true)]
    [InlineData("{\"outcome\":\"guest-shutdown\",\"needsRecovery\":false}", false)]
    [InlineData("{\"outcome\":\"machine-ended\",\"needsRecovery\":false}", true)]
    [InlineData("{\"outcome\":\"guest-shutdown\"", true)]
    public void AFailedOrIncompleteShutdownRecordCannotPermitRestart(string record, bool recovery)
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, SessionGuardian.ResultFile), record);
            Assert.Equal(recovery, SessionGuardian.NeedsRecovery(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void AFirstLaunchIsAllowedButAnOwnerWithMissingCompletionRecordRequiresRecovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "LaunchPadTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.False(SessionGuardian.NeedsRecovery(root));
            File.WriteAllText(Path.Combine(root, "session-owner.json"), "{}");
            Assert.True(SessionGuardian.NeedsRecovery(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
