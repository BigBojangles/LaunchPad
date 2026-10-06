using LaunchPad.Services;
using Xunit;

namespace LaunchPad.Tests;

public class LaunchWatchTests
{
    [Fact]
    public void ClearsTheButtonWhenTheStoredProcessExits()
    {
        var watch = new LaunchWatch();
        var path = @"C:\Work\Demo";

        var generation = watch.TryBegin(path);
        Assert.True(watch.IsOpen(path));
        Assert.True(watch.NotePid(path, generation, 4242));

        watch.NoteExited(path, generation, 4242);

        Assert.False(watch.IsOpen(path));
        Assert.NotEqual(0, watch.TryBegin(path));
    }

    [Fact]
    public void StaysOpenUntilEveryStoredProcessExits()
    {
        var watch = new LaunchWatch();
        var path = @"C:\Work\Demo";
        var generation = watch.TryBegin(path);
        watch.NotePid(path, generation, 10);
        watch.NotePid(path, generation, 11);

        watch.NoteExited(path, generation, 10);

        Assert.True(watch.IsOpen(path));
        watch.NoteExited(path, generation, 11);
        Assert.False(watch.IsOpen(path));
    }

    [Fact]
    public void IgnoresAnOlderLaunchWhenANewOneIsStored()
    {
        var watch = new LaunchWatch();
        var path = @"C:\Work\Demo";
        var first = watch.TryBegin(path);
        watch.CancelStart(path, first);

        var second = watch.TryBegin(path);
        watch.NotePid(path, second, 50);
        watch.NoteExited(path, first, 50);
        watch.CancelStart(path, first);

        Assert.True(watch.IsOpen(path));
        Assert.Equal(0, watch.TryBegin(path));
    }

    [Fact]
    public void DropsADeadPidOnTheNextCheck()
    {
        var watch = new LaunchWatch();
        var path = @"C:\Work\Demo";
        var generation = watch.TryBegin(path);
        watch.NotePid(path, generation, 7);
        watch.NotePid(path, generation, 8);

        watch.PruneDead(path, pid => pid == 8);

        Assert.True(watch.IsOpen(path));
        watch.PruneDead(path, _ => false);
        Assert.False(watch.IsOpen(path));
    }
}
