using System.Text.Json;
using LaunchPad.Models;
using LaunchPad.Services;
using Xunit;

namespace LaunchPad.Tests;

public sealed class GrokActivityAdapterTests
{
    private const string Project = "/home/builder/in/project";
    private static string Input(string kind, string? prompt = null, string session = "root-session", string cwd = Project)
        => JsonSerializer.Serialize(new { hookEventName = kind, sessionId = session, cwd, workspaceRoot = Project, promptId = prompt,
            stopHookActive = false, backgroundTasks = Array.Empty<object>(), sessionCrons = Array.Empty<object>() });

    [Fact]
    public void BackgroundTaskWakeupKeepsTheOriginalRunAcrossCheckpointAndCannotStartAnUnboundRun()
    {
        const string task = "01a113f1-3214-70d0-9549-4760e9fcf795";
        const string wake = "task-completed-" + task;
        var generation = Guid.NewGuid().ToString("N");
        var adapter = new GrokActivityAdapter(generation, "root-session", Project);
        Assert.False(adapter.TryAccept(Input("UserPromptSubmit", wake), out _));
        Assert.True(adapter.TryAccept(Input("UserPromptSubmit", "original-user-run"), out _));
        var input = JsonSerializer.Deserialize<Dictionary<string, object?>>(Input("Stop", "original-user-run"))!;
        input["backgroundTasks"] = new object[] { new { id = task, type = "shell", status = "running" } };
        Assert.False(adapter.TryAccept(JsonSerializer.Serialize(input), out _));
        var restored = new GrokActivityAdapter(generation, "root-session", Project, checkpoint: adapter.Checkpoint);
        Assert.Throws<ArgumentException>(() => new GrokActivityAdapter(generation, "root-session", "/another/project", checkpoint: adapter.Checkpoint));
        Assert.Throws<ArgumentException>(() => new GrokActivityAdapter(generation, "root-session", Project,
            checkpoint: adapter.Checkpoint with { BackgroundTaskIds = new[] { task.ToUpperInvariant() } }));
        Assert.False(restored.TryAccept(Input("UserPromptSubmit", "task-completed-" + Guid.NewGuid()), out _));
        Assert.True(restored.TryAccept(Input("UserPromptSubmit", wake), out var working));
        Assert.Equal(AgentEventKind.Working, working!.Value.Kind);
        Assert.Equal("original-user-run", working.Value.RunId);
        Assert.True(restored.HistoryComplete);
        var duringWakeup = new GrokActivityAdapter(generation, "root-session", Project, checkpoint: restored.Checkpoint);
        Assert.True(duringWakeup.TryAccept(Input("Stop", wake), out var finished));
        Assert.Equal("original-user-run", finished!.Value.RunId);
        Assert.Equal(AgentEventKind.RunFinished, finished.Value.Kind);
        Assert.False(duringWakeup.TryAccept(Input("Stop", wake), out _));
        Assert.False(duringWakeup.TryAccept(Input("UserPromptSubmit", wake), out _));
        Assert.True(duringWakeup.TryAccept(Input("UserPromptSubmit", "next-user-run"), out _));
        Assert.False(duringWakeup.TryAccept(Input("UserPromptSubmit", wake), out _));

        var duplicate = new GrokActivityAdapter(generation, "root-session", Project);
        Assert.True(duplicate.TryAccept(Input("UserPromptSubmit", "original-user-run"), out _));
        input["backgroundTasks"] = new object[] { new { id = task, type = "shell", status = "running" }, new { id = task, type = "shell", status = "running" } };
        Assert.False(duplicate.TryAccept(JsonSerializer.Serialize(input), out _));
        Assert.False(duplicate.TryAccept(Input("UserPromptSubmit", wake), out _));
        Assert.Empty(duplicate.Checkpoint.BackgroundTaskIds);
    }

    [Fact]
    public void BackgroundContinuationAndIncompleteStopDoNotFinishAndNewPromptResyncsWithoutInventingOutcome()
    {
        var generation = Guid.NewGuid().ToString("N");
        var adapter = new GrokActivityAdapter(generation, "root-session", Project);
        Assert.True(adapter.TryAccept(Input("UserPromptSubmit", "prompt-1"), out _));
        foreach (var (key, replacement) in new (string, object?)[]
        {
            ("stopHookActive", true), ("stopHookActive", null), ("stopHookActive", "false"),
            ("stop_hook_active", true), ("backgroundTasks", new object[] { new { id = "owned-task" } }),
            ("backgroundTasks", null), ("background_tasks", new object[] { "owned-task" }),
            ("sessionCrons", new object[] { new { id = "owned-cron" } }), ("session_crons", false)
        })
        {
            var input = JsonSerializer.Deserialize<Dictionary<string, object?>>(Input("Stop", "prompt-1"))!;
            input[key] = replacement;
            Assert.False(adapter.TryAccept(JsonSerializer.Serialize(input), out _));
            Assert.True(adapter.Snapshot.RunActive);
        }
        foreach (var missing in new[] { "stopHookActive", "backgroundTasks", "sessionCrons" })
        {
            var input = JsonSerializer.Deserialize<Dictionary<string, object?>>(Input("Stop", "prompt-1"))!;
            input.Remove(missing);
            Assert.False(adapter.TryAccept(JsonSerializer.Serialize(input), out _));
        }
        Assert.True(adapter.TryAccept(Input("UserPromptSubmit", "prompt-2"), out var started));
        Assert.Null(started); // State resync is not a manufactured historical event.
        Assert.False(adapter.HistoryComplete);
        Assert.Equal("prompt-2", adapter.Snapshot.RunId);
        var reopened = new GrokActivityAdapter(generation, "root-session", Project, adapter.Snapshot,
            previousHistoryComplete: adapter.HistoryComplete);
        Assert.False(reopened.HistoryComplete);
        Assert.False(reopened.TryAccept(Input("Stop", "prompt-1"), out _));
        Assert.False(reopened.TryAccept(Input("UserPromptSubmit", "prompt-1"), out _));
        Assert.True(reopened.TryAccept(Input("Stop", "prompt-2"), out var ended));
        Assert.Equal("prompt-2", ended!.Value.RunId);
        Assert.Equal(AgentEventKind.RunFinished, ended.Value.Kind);
    }

    [Fact]
    public void StablePromptIdsBindRepeatedRootRunsAndRefuseReplayAfterRestart()
    {
        var generation = Guid.NewGuid().ToString("N");
        var adapter = new GrokActivityAdapter(generation, "root-session", Project);
        Assert.True(adapter.TryAccept(Input("SessionStart"), out _));
        Assert.True(adapter.TryAccept(Input("user_prompt_submit", "prompt-1"), out var started));
        Assert.Equal(AgentActivity.Working, adapter.Snapshot.State);
        Assert.Equal("root-session", started!.Value.AgentSessionId);
        Assert.False(adapter.TryAccept(Input("Stop", "another-prompt"), out _));
        Assert.True(adapter.TryAccept(Input("Stop", "prompt-1"), out var ended));
        Assert.Equal(AgentEventKind.RunFinished, ended!.Value.Kind);
        Assert.Equal("prompt-1", ended.Value.RunId);
        var reopened = new GrokActivityAdapter(generation, "root-session", Project, adapter.Snapshot);
        Assert.False(reopened.TryAccept(Input("Stop", "prompt-1"), out _));
        Assert.False(reopened.TryAccept(Input("UserPromptSubmit", "prompt-1"), out _));
        Assert.True(reopened.TryAccept(Input("UserPromptSubmit", "prompt-2"), out _));
        Assert.True(reopened.TryAccept(Input("StopFailure", "prompt-2"), out var failed));
        Assert.Equal(AgentEventKind.RunFailed, failed!.Value.Kind);
        Assert.NotEqual(ended.Value.EventId, failed.Value.EventId);
        Assert.Throws<ArgumentException>(() => new GrokActivityAdapter(generation, "another-session", Project, reopened.Snapshot));
    }

    [Fact]
    public void AmbiguousSubagentContextAndUnidentifiedCallbacksDoNotPublishMainOutcomes()
    {
        var adapter = new GrokActivityAdapter(Guid.NewGuid().ToString("N"), "root-session", Project);
        Assert.False(adapter.TryAccept(Input("UserPromptSubmit", "prompt-1", "child-session"), out _));
        Assert.False(adapter.TryAccept(Input("UserPromptSubmit", "prompt-1", cwd: "/another/project"), out _));
        Assert.True(adapter.TryAccept(Input("UserPromptSubmit", "prompt-1"), out _));
        foreach (var property in new[] { "agentId", "agent_id", "subagentId", "parent_session_id" })
        {
            var value = JsonSerializer.Deserialize<Dictionary<string, object?>>(Input("Stop", "prompt-1"))!;
            value[property] = "child";
            Assert.False(adapter.TryAccept(JsonSerializer.Serialize(value), out _));
        }
        foreach (var name in new[] { "SubagentStop", "Notification", "PostToolUse", "SessionEnd" })
            Assert.False(adapter.TryAccept(Input(name), out _));
        Assert.False(adapter.TryAccept(Input("Stop"), out _));
        Assert.True(adapter.Snapshot.RunActive);
    }

    [Fact]
    public void ConflictingAliasesDuplicateKeysAndOversizeInputAreRefused()
    {
        var adapter = new GrokActivityAdapter(Guid.NewGuid().ToString("N"), "root-session", Project);
        var value = JsonSerializer.Deserialize<Dictionary<string, object?>>(Input("UserPromptSubmit", "prompt-1"))!;
        value["session_id"] = "another-session";
        Assert.False(adapter.TryAccept(JsonSerializer.Serialize(value), out _));
        value["session_id"] = "root-session";
        value["hook_event_name"] = "Stop";
        Assert.False(adapter.TryAccept(JsonSerializer.Serialize(value), out _));
        value["hook_event_name"] = "user_prompt_submit";
        Assert.True(adapter.TryAccept(JsonSerializer.Serialize(value), out _));
        Assert.False(adapter.TryAccept("{\"cwd\":\"first\",\"cwd\":\"second\"}", out _));
        Assert.False(adapter.TryAccept(new string(' ', GrokActivityAdapter.MaxInputBytes + 1), out _));
        Assert.False(adapter.TryAccept("{\"sessionId\":[]}", out _));
    }
}
