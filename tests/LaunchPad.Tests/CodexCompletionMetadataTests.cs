using System.Text.Json;
using LaunchPad.Services;
using Xunit;

namespace LaunchPad.Tests;

public sealed class CodexCompletionMetadataTests
{
    private const string Generation = "2acb42e0ea1b4d94b88b5ed71c639b7d";
    // Actual D70 callback schema/identifiers; prompts and messages are synthetic canaries.
    private const string Thread = "01a115d0-589f-73a0-ba1d-50be056d6e1c";
    private const string Turn = "01a115d0-58c9-7211-98e1-cd6b84c00e19";
    private static readonly string Project = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LaunchPad-owned-codex-metadata"));
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 10, 2, 18, TimeSpan.Zero);

    private static string Payload(string thread = Thread, string turn = Turn, string? cwd = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["type"] = "agent-turn-complete", ["thread-id"] = thread, ["turn-id"] = turn, ["cwd"] = cwd ?? Project,
            ["input-messages"] = new[] { "private-prompt-canary" }, ["last-assistant-message"] = "private-answer-canary"
        });
    private static CodexCompletionMetadata? Capture(string json) =>
        CodexCompletionDecoder.Capture(json, Generation, Project, Thread, Turn, Now);

    [Fact]
    public void MatchingCompletionRetainsOnlyBoundIdentifiersAndHostTime()
    {
        var receipt = Assert.IsType<CodexCompletionMetadata>(Capture(Payload()));
        Assert.Equal(new(Generation, Thread, Turn, Now), receipt);
        var saved = JsonSerializer.Serialize(receipt);
        Assert.DoesNotContain("private-prompt-canary", saved);
        Assert.DoesNotContain("private-answer-canary", saved);
        Assert.DoesNotContain(Project, saved);
    }

    [Fact]
    public void DifferentProjectThreadOrTurnCannotCompleteTheBoundRun()
    {
        Assert.Null(Capture(Payload(thread: Guid.NewGuid().ToString("D"))));
        Assert.Null(Capture(Payload(turn: Guid.NewGuid().ToString("D"))));
        Assert.Null(Capture(Payload(cwd: Project + "-other")));
        Assert.Null(Capture(Payload(cwd: "relative/project")));
        Assert.Null(CodexCompletionDecoder.Capture(Payload(), Generation, Project, "", Turn, Now));
        Assert.Null(CodexCompletionDecoder.Capture(Payload(), Generation, Project, Thread, "", Now));
    }

    [Theory]
    [InlineData("type")]
    [InlineData("thread-id")]
    [InlineData("turn-id")]
    [InlineData("cwd")]
    public void MissingWrongTypedOrDuplicateRequiredFieldsAreRefused(string key)
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Payload())!;
        fields.Remove(key);
        Assert.Null(Capture(JsonSerializer.Serialize(fields)));
        fields[key] = JsonSerializer.SerializeToElement(5);
        Assert.Null(Capture(JsonSerializer.Serialize(fields)));
        var original = Payload();
        Assert.Null(Capture(original[..^1] + "," + JsonSerializer.Serialize(key) + ":null}"));
    }

    [Theory]
    [InlineData("agent_id")]
    [InlineData("parent_session_id")]
    [InlineData("parent_thread_id")]
    [InlineData("source")]
    public void MarkedOrAmbiguousChildPayloadDoesNotMatchRoot(string marker) =>
        Assert.Null(Capture(Payload()[..^1] + "," + JsonSerializer.Serialize(marker) + ":null}"));

    [Fact]
    public void InvalidEnvelopesAndUnboundedInputAreRefusedWithoutSideEffects()
    {
        Assert.Null(Capture("[]"));
        Assert.Null(Capture("{"));
        Assert.Null(Capture(Payload().Replace("agent-turn-complete", "other-event", StringComparison.Ordinal)));
        Assert.Null(Capture(new string(' ', CodexCompletionDecoder.MaxInputBytes + 1)));
        Assert.Null(Capture(Payload().Replace("private-answer-canary", new string('\u00e9', 33000), StringComparison.Ordinal)));
        Assert.Null(CodexCompletionDecoder.Capture(Payload(), "invalid", Project, Thread, Turn, Now));
        Assert.Null(CodexCompletionDecoder.Capture(Payload(), Generation, Project, Thread, Turn, default));
        Assert.Null(CodexCompletionDecoder.Capture(Payload(), Generation, Project, Thread, Turn, Now.ToOffset(TimeSpan.FromHours(1))));
    }
}
