using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LaunchPad.Models;
using LaunchPad.Services;
using LaunchPad.Services.Fence;
using Xunit;

namespace LaunchPad.Tests;

public sealed class ClaudeActivityFeedTests
{
    [Fact]
    public void FailedPublicSinkRetainsCheckpointAndReceiptsAndReopenCannotRenewWork()
    {
        var f = new Fixture();
        var submitted = f.Capture("UserPromptSubmit");
        var work = f.Capture("PreToolUse", tool: "toolu_first");
        var pending = f.Feed.Read().Pending;
        Assert.Equal(new[] { submitted.Id, work.Id }, pending.Select(row => row.Id));
        Assert.DoesNotContain("PRIVATE-", File.ReadAllText(f.File));
        Assert.Throws<IOException>(() => f.Feed.Publish(true, _ => throw new IOException("owned failed sink"), live: true));
        Assert.Equal(2, f.Feed.Read().Checkpoint.Cursor);
        Assert.Equal(pending, f.Feed.Read().Pending);
        var reopened = new ClaudeActivityFeed(f.Directory, f.Binding, f.Time);
        reopened.Publish(true, f.Publish, live: true);
        Assert.Empty(reopened.Read().Pending);
        Assert.Equal(AgentActivity.Unknown, f.Published!.State);
        Assert.Equal(AgentActivity.Unknown, SessionActivityStore.Current(f.Directory, f.Binding.Generation, DateTimeOffset.UtcNow).State);
        Assert.Null(reopened.Read().Checkpoint.Activity.LastEvent);
        Assert.Equal(work.Id, reopened.Read().Checkpoint.ReceiptIds[^1]);
        Assert.NotNull(reopened.Capture(f.Input("PostToolUse", "toolu_first"), f.Time.GetUtcNow()));
        reopened.Publish(true, f.Publish, live: true);
        Assert.Equal(AgentActivity.Working, f.Published!.State);
        Assert.Equal(AgentActivity.Working, SessionActivityStore.Current(f.Directory, f.Binding.Generation, DateTimeOffset.UtcNow).State);
        f.Time.Advance(TimeSpan.FromSeconds(5));
        reopened.Publish(true, f.Publish, live: true);
        Assert.Equal(AgentActivity.Unknown, f.Published.State);
        Assert.Equal(AgentActivity.Unknown, SessionActivityStore.Current(f.Directory, f.Binding.Generation, DateTimeOffset.UtcNow).State);
    }

    [EnvironmentFact("OS", "Windows_NT")]
    public void FailedAcknowledgmentPreservesPublishedReceiptIdsWithoutReprojection()
    {
        var f = new Fixture();
        f.Capture("UserPromptSubmit"); f.Capture("PreToolUse", tool: "toolu_live");
        var ids = f.Feed.Read().Pending.Select(row => row.Id).ToArray();
        FileStream? held = null;
        try
        {
            var error = Record.Exception(() => f.Feed.Publish(true, state =>
            {
                f.Publish(state);
                held = new FileStream(f.File, FileMode.Open, FileAccess.Read, FileShare.Read);
            }, live: true));
            Assert.True(error is IOException or UnauthorizedAccessException, "The held queue must reject atomic acknowledgment.");
            Assert.Equal(AgentActivity.Working, f.Published!.State);
            Assert.Equal(ids, f.Feed.Read().Pending.Select(row => row.Id));
            Assert.Equal(2, f.Feed.Read().Checkpoint.Cursor);
        }
        finally { held?.Dispose(); }
        f.Time.Advance(TimeSpan.FromSeconds(5));
        f.Feed.Publish(true, f.Publish, live: true);
        Assert.Empty(f.Feed.Read().Pending);
        Assert.Equal(AgentActivity.Unknown, f.Published!.State);
        Assert.Equal(ids, f.Feed.Read().Checkpoint.ReceiptIds);
    }

    [Theory]
    [InlineData("foreign-generation")]
    [InlineData("duplicate-key")]
    [InlineData("duplicate-case-key")]
    [InlineData("missing-closed")]
    [InlineData("private-extra-field")]
    [InlineData("missing-pending")]
    public void CorruptOrIncompleteQueueIsPreservedAndCannotPublish(string defect)
    {
        var f = new Fixture();
        f.Capture("UserPromptSubmit"); f.Capture("PreToolUse", tool: "toolu_live");
        var original = File.ReadAllText(f.File);
        var root = JsonNode.Parse(original)!;
        switch (defect)
        {
            case "foreign-generation": root["binding"]!["generation"] = Guid.NewGuid().ToString("N"); break;
            case "missing-closed": root["checkpoint"]!.AsObject().Remove("closed"); break;
            case "private-extra-field": root["pending"]![0]!["privateTranscript"] = "PRIVATE-CONTENT"; break;
            case "missing-pending": root["pending"] = new JsonArray(); break;
        }
        var corrupt = defect switch
        {
            "duplicate-key" => original.Replace("\"schema\": 1", "\"schema\": 1, \"schema\": 1"),
            "duplicate-case-key" => original.Replace("\"schema\": 1", "\"schema\": 1, \"Schema\": 1"),
            _ => root.ToJsonString()
        };
        Assert.NotEqual(original, corrupt);
        File.WriteAllText(f.File, corrupt);
        var calls = 0;
        Assert.Throws<InvalidDataException>(() => f.Feed.Publish(true, _ => calls++, live: true));
        Assert.Equal(0, calls);
        Assert.Equal(corrupt, File.ReadAllText(f.File));
    }

    [Fact]
    public void ReopenedUnprojectedQueueNeedsANewProducerObservationBeforeShowingWork()
    {
        var f = new Fixture();
        f.Capture("UserPromptSubmit"); f.Capture("PreToolUse", tool: "toolu_old_owner");
        var reopened = new ClaudeActivityFeed(f.Directory, f.Binding, f.Time);
        reopened.Publish(true, f.Publish, live: true);
        Assert.Equal(AgentActivity.Unknown, f.Published!.State);
        Assert.Equal(AgentActivity.Working, reopened.Read().Checkpoint.Activity.State);
        Assert.NotNull(reopened.Capture(f.Input("PostToolUse", "toolu_new_owner"), f.Time.GetUtcNow()));
        reopened.Publish(true, f.Publish, live: true);
        Assert.Equal(AgentActivity.Working, f.Published!.State);
        // Separate callback helper instances can share only the explicitly supplied
        // live owner's epoch, as NativeClaudeActivity does after identity checks.
        var second = new Fixture();
        var epoch = Guid.NewGuid().ToString("N");
        var callback = new ClaudeActivityFeed(second.Directory, second.Binding, second.Time, epoch);
        callback.Capture(second.Input("UserPromptSubmit"), second.Time.GetUtcNow());
        callback.Capture(second.Input("PreToolUse", "toolu_bound_owner"), second.Time.GetUtcNow());
        var owner = new ClaudeActivityFeed(second.Directory, second.Binding, second.Time, epoch);
        owner.Publish(true, second.Publish, live: true);
        Assert.Equal(AgentActivity.Working, second.Published!.State);
    }

    [Fact]
    public void CapacityAndSessionEndRetainEarlierReceiptsAndNeverTurnExitIntoCompletion()
    {
        var f = new Fixture();
        for (var index = 0; index < ClaudeActivityFeed.Capacity; index++) f.Capture("UserPromptSubmit");
        var bytes = File.ReadAllBytes(f.File);
        Assert.Throws<IOException>(() => f.Capture("UserPromptSubmit"));
        Assert.Equal(bytes, File.ReadAllBytes(f.File));
        f.Feed.Publish(true, f.Publish, live: true);
        Assert.Empty(f.Feed.Read().Pending);
        Assert.Equal(AgentActivity.Unknown, f.Published!.State);
        f.Capture("SessionEnd");
        Assert.Null(f.Feed.Capture(f.Input("PreToolUse", "toolu_after_end"), f.Time.GetUtcNow()));
        f.Feed.Publish(false, f.Publish, live: true);
        Assert.True(f.Feed.Read().Checkpoint.Closed);
        Assert.Null(f.Feed.Read().Checkpoint.Activity.LastEvent);
        Assert.Equal(AgentActivity.Unknown, f.Published!.State);
    }

    [Fact]
    public void NativeGateCreatesNoFilesAndSessionPluginContainsOnlySilentHooks()
    {
        var f = new Fixture();
        var paths = new AppPaths(userProfile: f.Directory, appDataDir: Path.Combine(f.Directory, "state"),
            exePath: Path.Combine(f.Directory, "helper-never-run.exe"));
        var record = new NativeLaunchRecord(f.Directory, AgentChoice.Claude, Path.Combine(f.Directory, "claude-never-run.exe"),
            f.Binding.Generation, Environment.ProcessId, 1);
        Assert.False(NativeClaudeActivity.NativeStatusCompatibilityVerified);
        Assert.Null(NativeClaudeActivity.TryCreate(f.Directory, record, paths));
        Assert.False(System.IO.Directory.Exists(Path.Combine(f.Directory, "claude-activity")));
        Assert.Empty(System.IO.Directory.GetFiles(f.Directory, "*", SearchOption.AllDirectories));
        Assert.Equal(0, NativeClaudeActivity.RunCallback([NativeClaudeActivity.Argument]));
        var token = Guid.NewGuid().ToString("N");
        var files = NativeClaudeActivity.PluginFiles(token);
        Assert.Equal(new[] { ".claude-plugin/plugin.json", "hooks/hooks.json" }, files.Keys.Order());
        using var manifest = JsonDocument.Parse(files[".claude-plugin/plugin.json"]);
        Assert.Equal("launchpad-status-" + token, manifest.RootElement.GetProperty("name").GetString());
        Assert.False(manifest.RootElement.TryGetProperty("hooks", out _)); // Automatic hook file discovery only once.
        using var config = JsonDocument.Parse(files["hooks/hooks.json"]);
        Assert.Single(config.RootElement.EnumerateObject());
        var hooks = config.RootElement.GetProperty("hooks");
        Assert.Equal(10, hooks.EnumerateObject().Count());
        foreach (var entry in hooks.EnumerateObject())
        {
            var hook = entry.Value[0].GetProperty("hooks")[0];
            Assert.Equal(4, hook.GetProperty("timeout").GetInt32());
            var command = hook.GetProperty("command").GetString()!;
            var decoded = Encoding.Unicode.GetString(Convert.FromBase64String(command[(command.LastIndexOf(' ') + 1)..]));
            Assert.Contains(NativeClaudeActivity.Argument, decoded);
            Assert.Contains(token, decoded);
            Assert.Contains("65537", decoded);
            Assert.Contains("WaitForExit(2000)", decoded);
            Assert.DoesNotContain("Write-Host", decoded);
        }
        var batch = record with { Program = Path.Combine(f.Directory, "ordinary-agent.cmd") };
        var start = NativeAgentTerminal.AgentStart(batch);
        Assert.DoesNotContain("--plugin-dir", start.ArgumentList);
        Assert.DoesNotContain(NativeClaudeActivity.TokenVariable, start.Environment.Keys);
    }

    private sealed class Fixture
    {
        public string Directory { get; } = Path.Combine(GuestBaselineTests.RepositoryRoot(), "tests", "LaunchPad.Tests",
            "TestResults", "migration", "claude-owner-20261007", Guid.NewGuid().ToString("N"));
        public string File => Path.Combine(Directory, ClaudeActivityFeed.FileName);
        public TestTime Time { get; } = new();
        public ClaudeActivityBinding Binding { get; }
        public ClaudeActivityFeed Feed { get; }
        public AgentActivitySnapshot? Published { get; private set; }
        private readonly string _prompt = Guid.NewGuid().ToString("D");
        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Binding = new(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("D"), Directory);
            Feed = new(Directory, Binding, Time);
        }
        public string Input(string kind, string? tool = null) => JsonSerializer.Serialize(new
        { session_id = Binding.RootSessionId, cwd = Directory, hook_event_name = kind, prompt_id = _prompt,
            tool_use_id = tool ?? "toolu_unused", prompt = "PRIVATE-PROMPT", transcript_path = "PRIVATE-PATH",
            tool_input = new { password = "PRIVATE-SECRET" }, message = "PRIVATE-MESSAGE" });
        public ClaudeActivityReceipt Capture(string kind, string? tool = null) => Feed.Capture(Input(kind, tool), Time.GetUtcNow())
            ?? throw new InvalidOperationException("Owned callback capture failed.");
        public void Publish(AgentActivitySnapshot state)
        {
            Published = state;
            SessionActivityStore.Publish(new(Directory, Binding.Generation, Directory, AgentChoice.Claude), state, true,
                synchronized: state.State != AgentActivity.Unknown, historyComplete: false);
        }
    }
    private sealed class TestTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan value) { _now += value; _ticks += value.Ticks; }
    }
}
