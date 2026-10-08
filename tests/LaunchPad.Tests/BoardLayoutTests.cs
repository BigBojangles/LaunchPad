using LaunchPad.Models;
using LaunchPad.Views;
using Xunit;

namespace LaunchPad.Tests;

public sealed class BoardLayoutTests
{
    [Fact]
    public void RegroupingChangesOnlyDisplayGroupingAndKeepsWindowStatusIdentityAndProjectActions()
    {
        var layout = new Dictionary<string, string>();
        var board = new SessionBoard { GroupForSession = id => layout.GetValueOrDefault(id), SaveGroup = (id, path) => layout[id] = path };
        var first = new SessionRecord("first", "project-a", "grok", SessionKind.VirtualMachine, 11, 12, 13, SessionLifecycle.Busy);
        var second = new SessionRecord("second", "project-b", "codex", SessionKind.VirtualMachine, 21, 22, 23, SessionLifecycle.NeedsAnswer);
        board.Show(new[] { new LiveTile("project-a", "A", 0, true, first), new LiveTile("project-b", "B", 1, true, second) });
        var item = board.AllSessions.Single(i => i.Id == first.Id);
        var paint = item.IdentityBrush.Color;
        Assert.True(board.MoveToGroup(first.Id, "project-b"));
        Assert.Equal("project-a", item.ProjectPath);
        Assert.Same(first, item.Session);
        Assert.Equal(paint, item.IdentityBrush.Color);
        Assert.Equal(StatusColors.Green, item.StatusBrush!.Color);
        Assert.Equal("project-b", Assert.Single(board.Groups).ProjectPath);
        Assert.Contains(item, Assert.Single(board.Groups).Sessions);
        Assert.Throws<ArgumentException>(() => board.Show(new[] { new LiveTile("project-a", "A", 0, true, first), new LiveTile("project-a", "A", 0, true, first) }));
        Assert.True(board.MoveToGroup(first.Id, first.ProjectPath));
        Assert.Equal(first.ProjectPath, layout[first.Id]);
        Assert.False(board.MoveToGroup("unknown", "project-a"));
        Assert.Single(layout);
    }
}
