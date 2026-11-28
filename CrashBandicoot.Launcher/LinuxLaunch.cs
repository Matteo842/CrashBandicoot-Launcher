#if !WINDOWS
using System.Diagnostics;
using NativeFileDialogSharp;
using RecompOne.Runtime;
using RecompOne.Runtime.Config;

namespace CrashBandicoot.Launcher;

/// <summary>
/// Linux helpers outside the launcher window: when the window cannot open
/// (no X11 display) these pick a disc and report progress and errors as desktop
/// notifications. The console log (logs/last-run.txt) is <see cref="RunLog"/>.
/// </summary>
internal static class LinuxLaunch
{
    internal const string AppName = "Crash Bandicoot: Recompiled";

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
            if (OperatingSystem.IsMacOS())
            {
                // macOS (#14): Notification Center through AppleScript; text passed as argv, no quoting.
                var mac = new ProcessStartInfo("osascript") { UseShellExecute = false };
                foreach (var line in new[] { "on run argv", "display notification (item 1 of argv) with title (item 2 of argv)", "end run" })
                {
                    mac.ArgumentList.Add("-e");
                    mac.ArgumentList.Add(line);
                }
                mac.ArgumentList.Add(message);
                mac.ArgumentList.Add(AppName);
                using var _m = Process.Start(mac);
                return;
            }
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
}
#endif
