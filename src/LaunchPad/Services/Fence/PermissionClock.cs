namespace LaunchPad.Services.Fence;

public sealed class PermissionClock(Func<DateTimeOffset>? utc = null, Func<long>? uptime = null)
{
    private readonly object _gate = new();
    private DateTimeOffset? _latest;
    private long? _latestUptime;
    private bool _valid = true;
    public (DateTimeOffset Utc, long UptimeMs, bool Valid) Read()
    {
        lock (_gate)
        {
            var now = utc?.Invoke() ?? DateTimeOffset.UtcNow;
            var ticks = uptime?.Invoke() ?? Environment.TickCount64;
            if (_latest is not null && now < _latest || ticks < 0 || _latestUptime is not null && ticks < _latestUptime) _valid = false;
            if (_latest is null || now > _latest) _latest = now;
            if (_latestUptime is null || ticks > _latestUptime) _latestUptime = ticks;
            return (now, ticks, _valid);
        }
    }
}
