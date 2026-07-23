using System.Text;

namespace RecompOne.Runtime.Diagnostics;

public static class ConsoleMirror
{
    const int MaxLines = 4000;

    static readonly object _gate = new();
    static readonly List<string> _lines = new();
    static readonly StringBuilder _pendingOut = new();
    static readonly StringBuilder _pendingErr = new();
    static int _version;
    static bool _installed;

    public static int Version { get { lock (_gate) return _version; } }

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        Console.SetOut(new Tee(Console.Out, isError: false));
        Console.SetError(new Tee(Console.Error, isError: true));
    }

    public static void Clear()
    {
        lock (_gate)
        {
            _lines.Clear();
            _pendingOut.Clear();
            _pendingErr.Clear();
            _version++;
        }
    }

    public static int SnapshotInto(List<string> dst)
    {
        lock (_gate)
        {
            dst.Clear();
            dst.AddRange(_lines);
            if (_pendingOut.Length > 0) dst.Add(_pendingOut.ToString());
            if (_pendingErr.Length > 0) dst.Add(_pendingErr.ToString());
            return _version;
        }
    }

    static void Append(string? text, bool isError)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_gate)
        {
            var pending = isError ? _pendingErr : _pendingOut;
            foreach (char c in text)
            {
                if (c == '\n') FlushPendingLocked(pending, isError);
                else if (c != '\r') pending.Append(c);
            }
            _version++;
        }
    }

    static void AppendChar(char c, bool isError)
    {
        lock (_gate)
        {
            var pending = isError ? _pendingErr : _pendingOut;
            if (c == '\n') FlushPendingLocked(pending, isError);
            else if (c != '\r') pending.Append(c);
            _version++;
        }
    }

    static void FlushPendingLocked(StringBuilder pending, bool isError)
    {
        var line = pending.ToString();
        pending.Clear();
        _lines.Add(line);
        if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
        SessionLog.CaptureConsoleLine(line, isError);
    }

    sealed class Tee : TextWriter
    {
        readonly TextWriter _inner;
        readonly bool _isError;

        public Tee(TextWriter inner, bool isError)
        {
            _inner = inner;
            _isError = isError;
        }

        public override Encoding Encoding => _inner.Encoding;

        public override void Write(char value)
        {
            _inner.Write(value);
            AppendChar(value, _isError);
        }

        public override void Write(string? value)
        {
            _inner.Write(value);
            Append(value, _isError);
        }

        public override void WriteLine(string? value)
        {
            _inner.WriteLine(value);
            Append(value, _isError);
            AppendChar('\n', _isError);
        }

        public override void Flush() => _inner.Flush();
    }
}
