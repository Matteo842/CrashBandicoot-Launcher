#if !WINDOWS
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using NativeFileDialogSharp;
using RecompOne.Runtime;
using RecompOne.Runtime.Config;

namespace CrashBandicoot.Launcher;

/// <summary>
/// Linux helpers outside the launcher window: the console output is kept in
/// logs/last-run.txt so users can attach it to bug reports, and when the window
/// cannot open (no X11 display) these pick a disc and report progress and
/// errors as desktop notifications.
/// </summary>
internal static class LinuxLaunch
{
    const string AppName = "Crash Bandicoot: Recompiled";
    const string LogFileName = "last-run.txt";

    /// <summary>Startup and crash output is what matters; stop before a long session grows the file.</summary>
    const int MaxLogLines = 5000;

    public static string LogPath => Path.Combine(AppPaths.LogsDir, LogFileName);

    static LogSink? _sink;

    /// <summary>Mirror Console.Out/Error into logs/last-run.txt (overwritten each run).</summary>
    public static void StartLog(string[] args)
    {
        try
        {
            var sink = new LogSink(OpenLog($"args: {string.Join(' ', args)}"), MaxLogLines);
            Console.SetOut(new Tee(Console.Out, sink));
            Console.SetError(new Tee(Console.Error, sink));
            _sink = sink;
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
    public static void RestartLog(string reason)
    {
        if (_sink == null) return;
        try
        {
            _sink.Replace(OpenLog(reason));
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
        file.WriteLine($"{AppName} {version}  {DateTime.Now:O}");
        file.WriteLine(firstLine);
        file.WriteLine($"os: {RuntimeInformation.OSDescription}");
        file.WriteLine($"session: {Env("XDG_SESSION_TYPE")}  desktop: {Env("XDG_CURRENT_DESKTOP")}  " +
                       $"WAYLAND_DISPLAY={Env("WAYLAND_DISPLAY")}  DISPLAY={Env("DISPLAY")}");
        file.WriteLine($"root: {AppPaths.Root}");
        file.WriteLine("----");
        return file;
    }

    static string Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : "-";

    /// <summary>
    /// Disc for a start without arguments: the saved one, else the only .chd/.cue
    /// next to the program, else a file picker (needs GTK 3). Null when none.
    /// </summary>
    public static string? FindDisc()
    {
        try
        {
            ConfigManager.Load();
            var saved = ConfigManager.Game.CdPath;
            if (!string.IsNullOrWhiteSpace(saved) && File.Exists(saved))
            {
                Console.WriteLine($"[CrashBandicoot] using saved disc: {saved}");
                return Path.GetFullPath(saved);
            }
        }
        catch
        {
            // fall through
        }

        try
        {
            var nearby = Directory.EnumerateFiles(AppPaths.Root)
                .Where(f => f.EndsWith(".chd", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (nearby.Length == 1)
            {
                Console.WriteLine($"[CrashBandicoot] using disc next to the program: {nearby[0]}");
                return nearby[0];
            }
        }
        catch
        {
            // fall through
        }

        try
        {
            Console.WriteLine("[CrashBandicoot] no disc configured — opening file picker");
            var pick = Dialog.FileOpen("cue,chd", AppPaths.Root);
            if (pick.IsOk && !string.IsNullOrWhiteSpace(pick.Path))
                return Path.GetFullPath(pick.Path);
            if (pick.IsError)
                Console.Error.WriteLine($"[CrashBandicoot] file picker error: {pick.ErrorMessage}");
        }
        catch (Exception ex)
        {
            // libnfd links GTK 3 (libgtk-3.so.0).
            Console.Error.WriteLine($"[CrashBandicoot] file picker unavailable: {ex.GetBaseException().Message}");
        }

        return null;
    }

    /// <summary>Best-effort desktop notification through notify-send; silent when it is missing.</summary>
    public static void Notify(string message, bool error = false)
    {
        try
        {
            var psi = new ProcessStartInfo("notify-send") { UseShellExecute = false };
            psi.ArgumentList.Add("--app-name=" + AppName);
            if (error)
                psi.ArgumentList.Add("--urgency=critical");
            psi.ArgumentList.Add(AppName);
            psi.ArgumentList.Add(message);
            using var _ = Process.Start(psi);
        }
        catch
        {
            // no notification daemon / libnotify
        }
    }

    sealed class LogSink(StreamWriter file, int maxLines)
    {
        readonly object _gate = new();
        StreamWriter? _file = file;
        int _lines;

        public void Replace(StreamWriter file)
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
                _file = file;
                _lines = 0;
            }
        }

        public void Write(char c)
        {
            lock (_gate)
            {
                if (_file == null) return;
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
                _file.WriteLine(s);
                CountLine();
            }
        }

        void CountLine()
        {
            if (++_lines < maxLines || _file == null) return;
            try
            {
                _file.WriteLine($"---- log stopped after {maxLines} lines ----");
                _file.Dispose();
            }
            catch
            {
                // ignore
            }
            _file = null;
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
#endif
