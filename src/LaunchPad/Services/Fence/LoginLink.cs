using System.Text;
using System.Text.RegularExpressions;

namespace LaunchPad.Services.Fence;

public sealed class LoginLink
{
    private const string Prefix = "https://auth.x.ai/";
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _url = new();
    private readonly StringBuilder _osc = new();
    private bool _inUrl;
    private bool _openedUrl;
    private bool _esc;
    private bool _skipCsi;
    private bool _inOsc;
    private bool _oscEsc;
    private string? _openedCode;
    private string? _ready;

    public string? Push(byte[] data, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var url = Take(data[i]);
            if (url is not null)
                _ready = url;
        }

        if (_ready is null)
            _ready = TryCode();

        var open = _ready;
        _ready = null;
        return open;
    }

    private string? Take(byte value)
    {
        if (_inOsc)
            return TakeOsc(value);

        if (_skipCsi)
        {
            if (value is >= 0x40 and <= 0x7E)
                _skipCsi = false;
            return null;
        }

        if (_esc)
        {
            _esc = false;
            if (value == (byte)'[')
                _skipCsi = true;
            else if (value == (byte)']')
                _inOsc = true;
            return null;
        }

        if (value == 27)
        {
            _esc = true;
            return _inUrl ? FinishUrl(force: true) : null;
        }

        if (value is 9 or 10 or 13 or (byte)' ')
        {
            var finished = _inUrl ? FinishUrl(force: false) : null;
            Append(' ');
            return finished;
        }

        if (value < 32)
            return _inUrl ? FinishUrl(force: true) : null;

        var ch = (char)value;
        if (!_inUrl)
        {
            Append(ch);
            if (_openedUrl || !_text.ToString().EndsWith(Prefix, StringComparison.Ordinal) || StuckToPrevious())
                return null;

            _inUrl = true;
            _url.Clear();
            _url.Append(Prefix);
            return null;
        }

        if (IsUrl(ch))
        {
            Append(ch);
            _url.Append(ch);
            if (_url.Length >= 512)
                return FinishUrl(force: true);
            return null;
        }

        var done = FinishUrl(force: true);
        Append(ch);
        return done;
    }

    private string? TakeOsc(byte value)
    {
        if (_oscEsc)
        {
            _oscEsc = false;
            if (value == (byte)'\\')
            {
                _inOsc = false;
                return FinishOsc();
            }

            _inOsc = false;
            _esc = true;
            return FinishOsc();
        }

        if (value == 7)
        {
            _inOsc = false;
            return FinishOsc();
        }

        if (value == 27)
        {
            _oscEsc = true;
            return null;
        }

        if (value >= 32 && _osc.Length < 2048)
            _osc.Append((char)value);
        return null;
    }

    private string? FinishOsc()
    {
        var body = _osc.ToString();
        _osc.Clear();
        var at = body.IndexOf(Prefix, StringComparison.Ordinal);
        if (at < 0)
            return null;

        var uri = body[at..];
        var end = 0;
        while (end < uri.Length && IsUrl(uri[end]))
            end++;
        return Accept(uri[..end]);
    }

    private string? FinishUrl(bool force)
    {
        var url = _url.ToString();
        if (!force && KeepWrapping(url))
            return null;

        _inUrl = false;
        _url.Clear();
        return Accept(url);
    }

    private static bool KeepWrapping(string url)
    {
        if (HasFullCode(url) || url.Length <= Prefix.Length || url.Length >= 512)
            return false;
        if (Regex.IsMatch(url, @"user_code=[A-Z0-9]{4}$"))
            return false;
        if (url.Contains("user_code=", StringComparison.Ordinal) || url.Contains("user_", StringComparison.Ordinal))
            return true;
        return url.EndsWith('/')
            || url.EndsWith('=')
            || url.EndsWith('-')
            || url.EndsWith('?')
            || url.EndsWith('&')
            || url.EndsWith('.');
    }

    private string? Accept(string url)
    {
        if (!url.StartsWith(Prefix, StringComparison.Ordinal) || url.Length <= Prefix.Length)
            return null;

        var code = Regex.Match(url, @"user_code=([A-Z0-9]{4}-[A-Z0-9]{4})\b");
        if (code.Success)
        {
            if (string.Equals(code.Groups[1].Value, _openedCode, StringComparison.Ordinal))
                return null;

            _openedCode = code.Groups[1].Value;
            _openedUrl = true;
            return url;
        }

        if (_openedUrl || CutOff(url))
            return null;

        _openedUrl = true;
        return url;
    }

    private static bool CutOff(string url)
    {
        if (HasFullCode(url) || Regex.IsMatch(url, @"user_code=[A-Z0-9]{4}$"))
            return false;
        if (url.Contains("user_code=", StringComparison.Ordinal) || url.Contains("user_", StringComparison.Ordinal))
            return true;
        return url.EndsWith('/')
            || url.EndsWith('=')
            || url.EndsWith('-')
            || url.EndsWith('?')
            || url.EndsWith('&');
    }

    private string? TryCode()
    {
        var text = _text.ToString();
        if (text.IndexOf("Approve in your browser", StringComparison.Ordinal) < 0
            && text.IndexOf("Waiting for approval", StringComparison.Ordinal) < 0)
            return null;

        var code = Regex.Match(text, @"\b[A-Z0-9]{4}-[A-Z0-9]{4}\b");
        if (!code.Success || code.Value == _openedCode)
            return null;

        _openedCode = code.Value;
        return Prefix + "device?user_code=" + code.Value;
    }

    private static bool HasFullCode(string url) =>
        Regex.IsMatch(url, @"user_code=[A-Z0-9]{4}-[A-Z0-9]{4}\b");

    private void Append(char value)
    {
        _text.Append(value);
        if (_text.Length > 4096)
            _text.Remove(0, _text.Length - 2048);
    }

    private bool StuckToPrevious()
    {
        var text = _text.ToString();
        var at = text.Length - Prefix.Length;
        if (at <= 0)
            return false;

        var before = text[at - 1];
        return char.IsLetterOrDigit(before) || before is '/' or '.';
    }

    private static bool IsUrl(char value) =>
        char.IsLetterOrDigit(value) || "-._~:/?#[]@!$&'()*+,;=%".Contains(value);
}
