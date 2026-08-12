using System.Text;

namespace RecompOne.Runtime.Diagnostics;

/// <summary>
/// Focused playtest log: exceptions, overlays, host/GPU failures, markers — not full GPU/DMA spam.
/// Keep <see cref="Enabled"/> true while hunting bugs; set false before release pushes.
/// </summary>
public static class SessionLog
{
    // Playtest file log. Leave false in shipping builds — AutoFlush on every
    // BIOS/card line hits storage and hitch the game thread.
    public static bool Enabled = false;

    static readonly object Gate = new();
    static StreamWriter? _writer;
    static string? _path;
    static int _marker;

    public static string? CurrentPath
    {
        get { lock (Gate) return _path; }
    }

    public static void Start(string? note = null)
    {
        if (!Enabled) return;
        lock (Gate)
        {
            if (_writer != null) return;
            try
            {
                Directory.CreateDirectory(AppPaths.LogsDir);
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                _path = Path.Combine(AppPaths.LogsDir, $"session-{stamp}.txt");
                _writer = new StreamWriter(_path, append: false, Encoding.UTF8) { AutoFlush = true };
                _marker = 0;
                WriteLocked("==== session start ====");
                WriteLocked($"time={DateTime.Now:O}");
                WriteLocked($"root={AppPaths.Root}");
                WriteLocked($"cwd={Directory.GetCurrentDirectory()}");
                if (!string.IsNullOrWhiteSpace(note))
                    WriteLocked(note);
                WriteLocked("F9 = marker (press when you see a glitch)");
                WriteLocked("====");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SessionLog] failed to open log: {ex.Message}");
                _writer = null;
                _path = null;
            }
        }

        if (_path != null)
            Console.WriteLine($"[SessionLog] writing { _path }");
    }

    public static void Stop()
    {
        lock (Gate)
        {
            if (_writer == null) return;
            try
            {
                WriteLocked("==== session end ====");
                _writer.Dispose();
            }
            catch
            {
                // ignore
            }
            _writer = null;
        }
    }

    public static void Marker(string? detail = null)
    {
        if (!Enabled) return;
        int n;
        lock (Gate)
        {
            n = ++_marker;
            var msg = string.IsNullOrWhiteSpace(detail)
                ? $"[MARKER #{n}]"
                : $"[MARKER #{n}] {detail}";
            WriteLocked(msg);
        }
        Console.WriteLine($"[SessionLog] marker #{n}" + (string.IsNullOrWhiteSpace(detail) ? "" : $" — {detail}"));
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void Exception(string context, Exception ex)
    {
        if (!Enabled) return;
        lock (Gate)
        {
            WriteLocked($"[ERROR] {context}: {ex.GetType().Name}: {ex.Message}");
            WriteLocked(ex.ToString());
        }
    }

    public static void Write(string level, string message)
    {
        if (!Enabled) return;
        lock (Gate) WriteLocked($"[{level}] {message}");
    }

    /// <summary>Console tee hook — keeps only interesting lines.</summary>
    public static void CaptureConsoleLine(string? line, bool fromError)
    {
        if (!Enabled || string.IsNullOrEmpty(line)) return;
        if (!fromError && !IsInteresting(line)) return;
        lock (Gate) WriteLocked(fromError ? $"[stderr] {line}" : line);
    }

    static bool IsInteresting(string line)
    {
        // Always keep our own / host / recomp signals. Skip noisy category spam ([GPU], [DMA], …).
        if (line.StartsWith("[SessionLog]", StringComparison.Ordinal) ||
            line.StartsWith("[MARKER", StringComparison.Ordinal) ||
            line.StartsWith("[Dispatcher]", StringComparison.Ordinal) ||
            line.StartsWith("[GlBackend]", StringComparison.Ordinal) ||
            line.StartsWith("[Host]", StringComparison.Ordinal) ||
            line.StartsWith("[Event]", StringComparison.Ordinal) ||
            line.StartsWith("[Mods]", StringComparison.Ordinal) ||
            line.StartsWith("[CrashBandicoot]", StringComparison.Ordinal) ||
            line.StartsWith("[smoke]", StringComparison.Ordinal))
            return true;

        if (line.Contains("unmapped", StringComparison.OrdinalIgnoreCase))
            return true;
        if (line.Contains("exception", StringComparison.OrdinalIgnoreCase))
            return true;
        if (line.Contains("fail", StringComparison.OrdinalIgnoreCase))
            return true;
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase) &&
            !line.StartsWith("[GPU]", StringComparison.Ordinal) &&
            !line.StartsWith("[SPU]", StringComparison.Ordinal) &&
            !line.StartsWith("[BIOS]", StringComparison.Ordinal) &&
            !line.StartsWith("[DMA]", StringComparison.Ordinal) &&
            !line.StartsWith("[SDK]", StringComparison.Ordinal) &&
            !line.StartsWith("[MDEC]", StringComparison.Ordinal))
            return true;

        // CD unknown commands (not the optional Log.Cd spam)
        if (line.StartsWith("[CD]", StringComparison.Ordinal) &&
            line.Contains("unknow", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    static void WriteLocked(string message)
    {
        if (_writer == null) return;
        try
        {
            _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
        }
        catch
        {
            // ignore
        }
    }
}
