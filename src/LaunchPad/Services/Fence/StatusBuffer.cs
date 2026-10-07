using System.Text;
using LaunchPad.Models;

namespace LaunchPad.Services.Fence;

public sealed class StatusBuffer
{
    private readonly List<byte> _pending = new();
    private readonly List<byte> _auth = new();
    private readonly List<byte> _home = new();
    private int _bodyNeed;
    private bool _readingAuth;
    private bool _readingHome;
    private bool _discardingLine;
    private readonly AgentActivityTracker? _activity;
    private readonly List<AcceptedAgentActivityEvent> _events = new();
    private readonly List<HookDiagnostic> _diagnostics = new();

    public HookDiagnostic[] TakeDiagnostics()
    {
        var result = _diagnostics.ToArray();
        _diagnostics.Clear();
        return result;
    }

    public StatusBuffer(string? generation = null, AgentActivitySnapshot? previous = null)
    {
        if (generation is not null) _activity = new(generation, previous);
    }

    public bool SizeAccepted { get; private set; }
    // Trusted guest parent lifecycle, separate from an agent's turn/hook metadata.
    public bool AgentExited { get; private set; }
    public string? Activity { get; private set; }
    public AgentActivitySnapshot ActivitySnapshot => _activity?.Snapshot ?? AgentActivitySnapshot.Unavailable;
    public bool HistoryComplete => _activity?.HistoryComplete ?? true;

    public AgentActivityEvent[] TakeEvents()
        => TakeAcceptedEvents().Select(item => item.Value).ToArray();

    public AcceptedAgentActivityEvent[] TakeAcceptedEvents()
    {
        var result = _events.ToArray();
        _events.Clear();
        return result;
    }
    public bool AuthFinished { get; private set; }
    public long AuthRevision { get; private set; }
    public byte[] AuthBody { get; private set; } = Array.Empty<byte>();
    public bool HomeFinished { get; private set; }
    public bool HomeTooBig { get; private set; }
    public byte[] HomeBody { get; private set; } = Array.Empty<byte>();

    public void Push(byte[] data, int count)
    {
        for (var i = 0; i < count; i++)
            _pending.Add(data[i]);

        while (true)
        {
            if (_readingAuth || _readingHome)
            {
                var sink = _readingAuth ? _auth : _home;
                var take = Math.Min(_bodyNeed, _pending.Count);
                for (var i = 0; i < take; i++)
                    sink.Add(_pending[i]);
                if (take > 0)
                    _pending.RemoveRange(0, take);
                _bodyNeed -= take;
                if (_bodyNeed > 0)
                    return;

                if (_readingAuth)
                {
                    AuthBody = _auth.ToArray();
                    AuthFinished = true;
                    AuthRevision++;
                    _readingAuth = false;
                }
                else
                {
                    HomeBody = _home.ToArray();
                    HomeFinished = true;
                    _readingHome = false;
                }

                continue;
            }

            var newline = _pending.IndexOf((byte)'\n');
            if (newline < 0)
            {
                if (_pending.Count > AgentActivityTracker.MaxEventBytes || _discardingLine)
                {
                    _pending.Clear();
                    _discardingLine = true;
                }
                return;
            }

            if (_discardingLine || newline >= AgentActivityTracker.MaxEventBytes)
            {
                _pending.RemoveRange(0, newline + 1);
                _discardingLine = false;
                continue;
            }

            var line = Encoding.UTF8.GetString(_pending.Take(newline).ToArray()).TrimEnd('\r');
            _pending.RemoveRange(0, newline + 1);
            if (line.StartsWith(HookDiagnostics.Prefix, StringComparison.Ordinal))
            {
                var diagnostic = HookDiagnostics.Decode(line);
                if (diagnostic is not null && _diagnostics.Count < 64) _diagnostics.Add(diagnostic);
                continue;
            }
            if (_activity is not null && _activity.TryAcceptLine(line, out var value))
            {
                _events.Add(new AcceptedAgentActivityEvent(value!));
                continue;
            }
            if (_activity is not null && _activity.TryAcceptStateLine(line)) continue;
            if (line is "busy" or "needs-an-answer" or "idle")
            {
                // Legacy startup strings are retained for diagnostics, not activity proof.
                Activity = line;
                continue;
            }
            if (line.StartsWith("SIZE-OK ", StringComparison.Ordinal))
            {
                SizeAccepted = true;
                continue;
            }

            if (line == "AGENT-EXITED")
            {
                AgentExited = true;
                continue;
            }

            if (line == "HOME BIG")
            {
                HomeTooBig = true;
                HomeFinished = true;
                HomeBody = Array.Empty<byte>();
                continue;
            }

            if (line.StartsWith("HOME ", StringComparison.Ordinal))
            {
                if (!int.TryParse(line.AsSpan(5), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var homeSize)
                    || homeSize < 0
                    || homeSize > GuestHome.MaxBytes)
                    continue;

                _home.Clear();
                _bodyNeed = homeSize;
                _readingHome = homeSize > 0;
                if (homeSize == 0)
                {
                    HomeBody = Array.Empty<byte>();
                    HomeFinished = true;
                }

                continue;
            }

            if (!line.StartsWith("AUTH ", StringComparison.Ordinal))
                continue;

            if (!int.TryParse(line.AsSpan(5), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var size)
                || size < 0
                || size > GuestAuth.MaxBytes)
                continue;

            _auth.Clear();
            _bodyNeed = size;
            _readingAuth = size > 0;
            if (size == 0)
            {
                AuthBody = Array.Empty<byte>();
                AuthFinished = true;
                AuthRevision++;
            }
        }
    }
}
