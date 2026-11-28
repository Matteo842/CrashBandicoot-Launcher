using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using RecompOne.Runtime;

namespace CrashBandicoot.Launcher;

/// <summary>
/// Console output of the last run, kept in logs/last-run.txt (Windows, Linux and macOS)
/// so players can attach it to a bug report. The file holds the first lines of the
/// session in full; past <see cref="MaxHeadLines"/> only the last <see cref="TailLines"/>
/// are kept in memory and written out on exit or on an unhandled exception.
/// A file without the "clean exit" line ended in a native crash, a freeze that was
/// killed, or a power loss.
/// </summary>
internal static class RunLog
{
    const string LogFileName = "last-run.txt";
    const int MaxHeadLines = 5000;
    const int TailLines = 1000;

    public static string LogPath => Path.Combine(AppPaths.LogsDir, LogFileName);

    static LogSink? _sink;

    /// <summary>Mirror Console.Out/Error into logs/last-run.txt (overwritten each run).</summary>
    public static void Start(string[] args)
    {
        try
        {
            var sink = new LogSink(OpenLog($"args: {string.Join(' ', args)}"));
            Console.SetOut(new Tee(Console.Out, sink));
            Console.SetError(new Tee(Console.Error, sink));
            _sink = sink;
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                sink.Finish($"UNHANDLED EXCEPTION{(e.IsTerminating ? " (terminating)" : "")}{Environment.NewLine}{e.ExceptionObject}");
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                sink.Finish($"clean exit (code {Environment.ExitCode})");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CrashBandicoot] could not write {LogFileName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Start logs/last-run.txt over (the launcher calls this for each game it starts,
    /// so the file always holds the last game session in full).
    /// </summary>
    public static void Restart(string reason)
    {
        if (_sink == null) return;
        try
        {
            _sink.Replace(() => OpenLog(reason));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CrashBandicoot] could not restart {LogFileName}: {ex.Message}");
        }
    }

    static StreamWriter OpenLog(string firstLine)
    {
        Directory.CreateDirectory(AppPaths.LogsDir);
        var file = new StreamWriter(LogPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        var version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        file.WriteLine($"Crash Bandicoot: Recompiled {version}  {DateTime.Now:O}");
        file.WriteLine(firstLine);
        file.WriteLine($"os: {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}  " +
                       $"cpus: {Environment.ProcessorCount}  " +
                       $"ram: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024)} MB");
        if (OperatingSystem.IsLinux())
            file.WriteLine($"session: {Env("XDG_SESSION_TYPE")}  desktop: {Env("XDG_CURRENT_DESKTOP")}  " +
                           $"WAYLAND_DISPLAY={Env("WAYLAND_DISPLAY")}  DISPLAY={Env("DISPLAY")}");
        file.WriteLine($"root: {AppPaths.Root}");
        file.WriteLine("----");
        return file;
    }

    static string Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : "-";

    sealed class LogSink(StreamWriter file)
    {
        readonly object _gate = new();
        readonly Queue<string> _tail = new();
        readonly StringBuilder _line = new();
        StreamWriter? _file = file;
        int _lines;
        long _skipped;
        bool _finished;

        /// <summary>
        /// Close the current file first: Windows keeps it locked while open, so
        /// opening the same path again would fail.
        /// </summary>
        public void Replace(Func<StreamWriter> open)
        {
            lock (_gate)
            {
                try
                {
                    _file?.Dispose();
                }
                catch
                {
                    // ignore
                }
                _file = null;
                _file = open();
                _lines = 0;
                _skipped = 0;
                _tail.Clear();
                _line.Clear();
                _finished = false;
            }
        }

        bool TailMode => _lines >= MaxHeadLines;

        public void Write(char c)
        {
            lock (_gate)
            {
                if (_file == null) return;
                if (TailMode) { TailChar(c); return; }
                _file.Write(c);
                if (c == '\n') CountLine();
            }
        }

        public void Write(string? s)
        {
            if (string.IsNullOrEmpty(s)) return;
            lock (_gate)
            {
                if (_file == null) return;
                if (TailMode)
                {
                    foreach (char c in s) TailChar(c);
                    return;
                }
                _file.Write(s);
                foreach (char c in s)
                    if (c == '\n') CountLine();
            }
        }

        public void WriteLine(string? s)
        {
            lock (_gate)
            {
                if (_file == null) return;
                if (TailMode)
                {
                    _line.Append(s);
                    PushTail();
                    return;
                }
                _file.WriteLine(s);
                CountLine();
            }
        }

        void CountLine()
        {
            if (++_lines == MaxHeadLines && _file != null)
                _file.WriteLine($"---- {MaxHeadLines} lines reached: only the last {TailLines} are kept from here ----");
        }

        void TailChar(char c)
        {
            if (c == '\n') PushTail();
            else if (c != '\r') _line.Append(c);
        }

        void PushTail()
        {
            _tail.Enqueue(_line.ToString());
            _line.Clear();
            if (_tail.Count > TailLines)
            {
                _tail.Dequeue();
                _skipped++;
            }
        }

        /// <summary>Write the kept tail and a closing line. Only the first call per log counts.</summary>
        public void Finish(string closing)
        {
            lock (_gate)
            {
                if (_file == null || _finished) return;
                _finished = true;
                try
                {
                    if (_line.Length > 0) PushTail();
                    if (_tail.Count > 0)
                    {
                        if (_skipped > 0)
                            _file.WriteLine($"---- {_skipped} lines skipped ----");
                        foreach (var line in _tail)
                            _file.WriteLine(line);
                        _tail.Clear();
                    }
                    _file.WriteLine($"---- {closing.TrimEnd()}");
                    _file.WriteLine("----");
                    _file.Flush();
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    sealed class Tee(TextWriter console, LogSink sink) : TextWriter
    {
        public override Encoding Encoding => console.Encoding;

        public override void Write(char value)
        {
            console.Write(value);
            sink.Write(value);
        }

        public override void Write(string? value)
        {
            console.Write(value);
            sink.Write(value);
        }

        public override void WriteLine(string? value)
        {
            console.WriteLine(value);
            sink.WriteLine(value);
        }

        public override void Flush() => console.Flush();
    }
}
