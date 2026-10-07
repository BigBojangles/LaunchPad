using System.Net.Sockets;
using System.Text;
using LaunchPad.Models;

namespace LaunchPad.Services.Fence;

public sealed class StatusLink : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly StatusBuffer _buffer;
    private readonly SessionActivityContext? _activityContext;
    private readonly object _gate = new();
    private readonly object _writeGate = new();
    private readonly TimeProvider _time;
    private long _producerReceived;
    private bool _closed;
    private bool _hasFreshActivity;
    private bool _historyComplete = true;
    private readonly List<AgentActivityEvent> _pendingEvents = new();
    private string? _observationError;
    private readonly Action<AcceptedAgentActivityEvent>? _notify;
    private readonly List<AcceptedAgentActivityEvent> _pendingNotifications = new();
    private bool _notificationHistoryIncomplete;
    public string? NotificationError { get; private set; }

    public StatusLink(TcpClient client, SessionActivityContext? activityContext = null, TimeProvider? time = null,
        Action<AcceptedAgentActivityEvent>? notify = null)
    {
        _time = time ?? TimeProvider.System;
        _client = client;
        _stream = client.GetStream();
        _activityContext = activityContext;
        _notify = notify;
        if (_notify is null && activityContext?.ProjectPath is { } project && AgentChoice.Known(activityContext.AgentId))
        {
            var notifications = new NotificationService(new AppPaths());
            _notify = accepted => notifications.Queue(accepted, project, activityContext.AgentId!);
        }
        var previous = activityContext is null ? null : SessionActivityStore.Read(activityContext.Directory, activityContext.Generation);
        _buffer = new(activityContext?.Generation, previous?.Activity);
        _historyComplete = previous?.HistoryComplete ?? true;
        _ = Task.Run(ReadLoop);
        if (activityContext is not null)
            Write(Encoding.ASCII.GetBytes("EVENTS 1 " + activityContext.Generation + "\n"));
    }

    public bool SizeAccepted
    {
        get
        {
            lock (_gate)
                return _buffer.SizeAccepted;
        }
    }

    public string? Activity
    {
        get { lock (_gate) return _buffer.Activity; }
    }

    public bool AgentExited
    {
        get { lock (_gate) return _buffer.AgentExited; }
    }

    public (bool Connected, string? Activity) Snapshot
    {
        get { lock (_gate) return (!_closed, _closed ? null : _buffer.Activity); }
    }

    public AgentActivitySnapshot AgentActivity
    {
        get { lock (_gate) return _closed || !ProducerCurrent ? AgentActivitySnapshot.Unavailable : _buffer.ActivitySnapshot; }
    }

    private bool ProducerCurrent => _hasFreshActivity && _time.GetElapsedTime(_producerReceived) <= SessionActivityStore.Freshness;

    public string? ObservationError { get { lock (_gate) return _observationError; } }

    public void SendPause()
    {
        Write(Encoding.ASCII.GetBytes("PAUSE\n"));
    }

    public void SendWinsize(int rows, int cols)
    {
        var line = Encoding.ASCII.GetBytes(
            "WINSIZE "
            + rows.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " "
            + cols.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "\n");
        Write(line);
    }

    public async Task<byte[]?> RequestAuthAsync(TimeSpan timeout)
    {
        long previousRevision;
        lock (_gate) previousRevision = _buffer.AuthRevision;
        Write(Encoding.ASCII.GetBytes("AUTH-OUT\n"));
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (_buffer.AuthRevision > previousRevision)
                    return _buffer.AuthBody;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        return null;
    }

    public bool HomeTooBig
    {
        get
        {
            lock (_gate)
                return _buffer.HomeTooBig;
        }
    }

    public async Task<byte[]?> RequestHomeAsync(TimeSpan timeout)
    {
        Write(Encoding.ASCII.GetBytes("HOME-OUT\n"));
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (_buffer.HomeFinished)
                    return _buffer.HomeBody;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        return null;
    }

    public void Dispose()
    {
        lock (_gate) _closed = true;
        try
        {
            _client.Dispose();
        }
        catch
        {
            // The status port is already closed.
        }
    }

    private void Write(byte[] line)
    {
        // The read loop must keep draining the guest while this write waits.
        // Holding one lock across both sides stalls the window-size exchange.
        lock (_writeGate)
        {
            try
            {
                _stream.Write(line, 0, line.Length);
            }
            catch
            {
                // The guest has already closed the status port.
            }
        }
    }

    private async Task ReadLoop()
    {
        var buffer = new byte[4096];
        try
        {
            var pendingRead = _stream.ReadAsync(buffer.AsMemory()).AsTask();
            while (true)
            {
                if (await Task.WhenAny(pendingRead, Task.Delay(1000)).ConfigureAwait(false) != pendingRead)
                {
                    PublishObservation();
                    continue;
                }
                var read = await pendingRead.ConfigureAwait(false);
                if (read <= 0)
                    break;

                byte[]? changedAuth = null;
                lock (_gate)
                {
                    var before = _buffer.ActivitySnapshot.LastSequence;
                    var beforeAuth = _buffer.AuthRevision;
                    _buffer.Push(buffer, read);
                    if (_buffer.AuthRevision > beforeAuth && _activityContext?.AgentId == AgentChoice.Grok)
                        changedAuth = _buffer.AuthBody;
                    if (_buffer.ActivitySnapshot.LastSequence > before)
                    {
                        _hasFreshActivity = true;
                        _producerReceived = _time.GetTimestamp();
                    }
                }
                if (changedAuth is { Length: > 0 })
                {
                    try
                    {
                        if (new SettingsStore(new AppPaths()).Current.RememberGrokSignIn)
                            GuestAuth.Save(changedAuth);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                    { new SetupLog(new AppPaths()).Write("Grok sign-in cache could not be refreshed; existing login files were preserved."); }
                }
                PublishObservation();
                pendingRead = _stream.ReadAsync(buffer.AsMemory()).AsTask();
            }
        }
        catch
        {
            // The session closed the status port.
        }
        finally
        {
            lock (_gate) _closed = true;
            PublishObservation();
        }
    }

    private void PublishObservation()
    {
        if (_activityContext is null) return;
        AgentActivitySnapshot activity;
        AcceptedAgentActivityEvent[] accepted;
        HookDiagnostic[] diagnostics;
        bool connected;
        bool synchronized;
        lock (_gate)
        {
            activity = _buffer.ActivitySnapshot;
            accepted = _buffer.TakeAcceptedEvents();
            diagnostics = _buffer.TakeDiagnostics();
            connected = !_closed;
            synchronized = ProducerCurrent;
            _historyComplete &= _buffer.HistoryComplete;
        }
        HookDiagnostics.Append(_activityContext, diagnostics);
        if (_notify is not null)
        {
            _pendingNotifications.AddRange(accepted);
            if (_pendingNotifications.Count > 1024)
            {
                _pendingNotifications.RemoveRange(0, _pendingNotifications.Count - 1024);
                _notificationHistoryIncomplete = true;
            }
            try
            {
                while (_pendingNotifications.Count > 0)
                {
                    _notify(_pendingNotifications[0]);
                    _pendingNotifications.RemoveAt(0);
                }
                NotificationError = _notificationHistoryIncomplete ? "Notification event history is incomplete; some alerts may be missing." : null;
            }
            catch { NotificationError = "Notification queue is unavailable. Agent controls remain available."; }
        }
        _pendingEvents.AddRange(accepted.Select(item => item.Value));
        if (_pendingEvents.Count > 1024)
        {
            _pendingEvents.RemoveRange(0, _pendingEvents.Count - 1024);
            _historyComplete = false;
        }
        try
        {
            SessionActivityStore.Publish(_activityContext, activity, connected, _pendingEvents, synchronized, _historyComplete);
            _pendingEvents.Clear();
            lock (_gate) _observationError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            lock (_gate) _observationError = error.Message;
            // Reporting failure must not break AUTH/HOME, resize or return traffic.
            // A missing/stale observation is unavailable in a reopened desktop.
        }
    }
}
