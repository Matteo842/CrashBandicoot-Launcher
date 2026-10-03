using CrashBandicoot.Launcher;
using CrashBandicoot.Launcher.Recomp;
using RecompOne.Runtime.Config;

namespace CrashBandicoot.Launcher;

internal static class Program
{
#if WINDOWS
    [STAThread]
#endif
    private static int Main(string[] args)
    {
        try
        {
            Directory.SetCurrentDirectory(RecompOne.Runtime.AppPaths.Root);
        }
        catch
        {
            // ignore
        }

        RecompOne.Runtime.AppPaths.EnsureCreated();

        if (args.Length >= 1 && string.Equals(args[0], "--help", StringComparison.OrdinalIgnoreCase))
        {
            PrintHelp();
            return 0;
        }

#if !WINDOWS
        // Game started by the Linux launcher window: the launcher holds the instance
        // lock and copies our console output into logs/last-run.txt.
        if (Linux.GameSession.IsChildRun(args, out var childCue))
            return RunGame(childCue, desktop: false);
#endif

        // Exclusive file lock — works the same on Windows and Linux (no named Mutex).
        if (!SingleInstance.TryAcquire(out var singleInstance))
        {
            SingleInstance.NotifyAlreadyRunning();
            return 1;
        }

        using (singleInstance)
        {
#if !WINDOWS
            LinuxLaunch.StartLog(args);
#endif

            if (args.Length >= 2 && string.Equals(args[0], "--prepare", StringComparison.OrdinalIgnoreCase))
            {
                var cue = Path.GetFullPath(args[1]);
                Console.WriteLine($"[CrashBandicoot] preparing from {cue}");
                var progress = new Progress<PipelineProgress>(p =>
                    Console.WriteLine($"  [{p.Fraction * 100,3:0}%] {p.Stage}: {p.Detail}"));
                var dll = RecompPipeline.EnsureReady(cue, progress);
                Console.WriteLine($"[CrashBandicoot] ready: {dll}");
                return 0;
            }

            if (args.Length >= 1 && string.Equals(args[0], "--run", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryResolveCue(args, out var cue, out var cueError))
                {
                    Console.Error.WriteLine("[CrashBandicoot] " + cueError);
                    PrintHelp();
                    return 1;
                }

                return RunGame(cue, desktop: false);
            }

            if (args.Length >= 1 && string.Equals(args[0], "--smoke", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryResolveCue(args, out var cue, out var cueError))
                {
                    Console.Error.WriteLine("[smoke] " + cueError);
                    PrintHelp();
                    return 1;
                }

                try
                {
                    ConfigManager.Load();
                    ConfigManager.Game.CdPath = cue;
                    ConfigManager.SaveGame();
                    var dll = RecompPipeline.EnsureReady(cue);
                    Console.WriteLine("[smoke] launching " + dll);
                    var t = Task.Run(() => GameLoader.Run(dll, cue));
                    if (!t.Wait(TimeSpan.FromSeconds(12)))
                    {
                        Console.WriteLine("[smoke] still running after 12s — OK (window likely open)");
                        return 0;
                    }
                    if (t.IsFaulted)
                        throw t.Exception!.GetBaseException();
                    Console.WriteLine("[smoke] Entry.Run returned");
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[smoke] FAIL: " + ex.GetBaseException().Message);
                    Console.Error.WriteLine(ex);
                    return 1;
                }
            }

#if WINDOWS
            // WinForms requires STA ([STAThread] above). Do not flip apartment mode here.
            ApplicationConfiguration.Initialize();
            Application.Run(new LauncherHost());
            return 0;
#else
            // Without arguments the binary was double-clicked or started from a menu /
            // Steam shortcut: open the launcher window. With no X11 display to show it
            // on, start the game directly (saved disc, else the one next to the program).
            if (args.Length == 0)
            {
                if (Linux.LinuxGui.TryRun(args, out var launcherExit))
                    return launcherExit;

                var disc = LinuxLaunch.FindDisc();
                if (disc != null)
                    return RunGame(disc, desktop: true);

                const string noDisc =
                    "No disc selected. Put your Crash Bandicoot .chd (or .cue + .bin) next to the program, " +
                    "or run it from a terminal: --run <file.cue|file.chd>";
                Console.Error.WriteLine("[CrashBandicoot] " + noDisc);
                LinuxLaunch.Notify(noDisc, error: true);
                PrintHelp();
                return 1;
            }

            PrintHelp();
            return 1;
#endif
        }
    }

    /// <summary>Prepare (first time only) and play. <paramref name="desktop"/> = started without a terminal.</summary>
    static int RunGame(string cue, bool desktop)
    {
        try
        {
#if !WINDOWS
            int notified = 0;
#endif
            var progress = new Progress<PipelineProgress>(p =>
            {
                Console.WriteLine($"  [{p.Fraction * 100,3:0}%] {p.Stage}: {p.Detail}");
#if !WINDOWS
                if (desktop && p.Stage == "Recompile" && Interlocked.Exchange(ref notified, 1) == 0)
                    LinuxLaunch.Notify("First launch: building the game from your disc. " +
                                       "This takes a minute or two, the game window opens when it is done.");
#endif
            });

            ConfigManager.Load();
            var dll = RecompPipeline.EnsureReady(cue, progress);
            // Save only a disc that passed validation, so a bad pick is asked again next time.
            ConfigManager.Game.CdPath = cue;
            ConfigManager.SaveGame();
            Console.WriteLine("[CrashBandicoot] launching " + dll);
            GameLoader.Run(dll, cue);
            return 0;
        }
        catch (Exception ex)
        {
            RecompOne.Runtime.Diagnostics.SessionLog.Exception("--run", ex.GetBaseException());
            RecompOne.Runtime.Diagnostics.SessionLog.Stop();
            Console.Error.WriteLine("[CrashBandicoot] FAIL: " + ex.GetBaseException().Message);
            Console.Error.WriteLine(ex);
#if !WINDOWS
            if (desktop)
                LinuxLaunch.Notify($"{ex.GetBaseException().Message}\n\nDetails: {LinuxLaunch.LogPath}", error: true);
#endif
            return 1;
        }
    }

    static void PrintHelp()
    {
        Console.WriteLine("Crash Bandicoot: Recompiled");
#if WINDOWS
        Console.WriteLine("  (no args)              open the launcher");
#else
        Console.WriteLine("  (no args)              open the launcher (no X11 display: play the saved disc directly)");
#endif
        Console.WriteLine("  --prepare <file.cue|file.chd>   prepare game folder without UI");
        Console.WriteLine("  --run <file.cue|file.chd>       prepare (if needed) and play (no UI)");
        Console.WriteLine("  --smoke <file.cue|file.chd>     load prepared game briefly (debug)");
        Console.WriteLine("  --help                 show this help");
        Console.WriteLine();
        Console.WriteLine("If <file.cue|file.chd> is omitted for --run/--smoke, uses CdPath from settings.json when set.");
    }

    /// <summary>
    /// Resolve disc path from CLI arg, else saved <see cref="ConfigManager.Game.CdPath"/>.
    /// </summary>
    static bool TryResolveCue(string[] args, out string cue, out string error)
    {
        cue = "";
        error = "";

        if (args.Length >= 2 && !string.IsNullOrWhiteSpace(args[1]))
        {
            cue = Path.GetFullPath(args[1]);
            if (!File.Exists(cue))
            {
                error = $"disc not found: {cue}";
                return false;
            }
            return true;
        }

        try
        {
            ConfigManager.Load();
            var saved = ConfigManager.Game.CdPath;
            if (!string.IsNullOrWhiteSpace(saved) && File.Exists(saved))
            {
                cue = Path.GetFullPath(saved);
                Console.WriteLine($"[CrashBandicoot] using saved disc: {cue}");
                return true;
            }
        }
        catch
        {
            // fall through
        }

        error = "missing <file.cue|file.chd> (and no valid CdPath in settings.json)";
        return false;
    }
}
