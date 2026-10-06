using System.Text;

namespace LaunchPad.Services.Fence;

public sealed class StatusBuffer
{
    private readonly List<byte> _pending = new();
    private readonly List<byte> _auth = new();
    private readonly List<byte> _home = new();
    private int _bodyNeed;
    private bool _readingAuth;
    private bool _readingHome;

    public bool SizeAccepted { get; private set; }
    public string? Activity { get; private set; }
    public bool AuthFinished { get; private set; }
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
                return;

            var line = Encoding.ASCII.GetString(_pending.Take(newline).ToArray()).TrimEnd('\r');
            _pending.RemoveRange(0, newline + 1);
            if (line is "busy" or "needs-an-answer" or "idle")
            {
                Activity = line;
                continue;
            }
            if (line.StartsWith("SIZE-OK ", StringComparison.Ordinal))
            {
                SizeAccepted = true;
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
            }
        }
    }
}
