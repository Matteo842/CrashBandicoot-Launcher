using CrashBandicoot.Launcher;
using CrashBandicoot.Launcher.Recomp;

namespace CrashBandicoot.Launcher;

internal static class Program
{
    [STAThread]
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
            var cue = args.Length >= 2
                ? Path.GetFullPath(args[1])
                : Path.GetFullPath(@"D:\GitHub\RecompOne\Crash Bandicoot.cue");
            try
            {
                RecompOne.Runtime.Config.ConfigManager.Load();
                RecompOne.Runtime.Config.ConfigManager.Game.CdPath = cue;
                RecompOne.Runtime.Config.ConfigManager.SaveGame();
                var dll = RecompPipeline.EnsureReady(cue);
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
                return 1;
            }
        }

        if (args.Length >= 1 && string.Equals(args[0], "--smoke", StringComparison.OrdinalIgnoreCase))
        {
            var cue = args.Length >= 2
                ? Path.GetFullPath(args[1])
                : Path.GetFullPath(@"D:\GitHub\RecompOne\Crash Bandicoot.cue");
            try
            {
                RecompOne.Runtime.Config.ConfigManager.Load();
                RecompOne.Runtime.Config.ConfigManager.Game.CdPath = cue;
                RecompOne.Runtime.Config.ConfigManager.SaveGame();
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

        if (args.Length >= 1 && string.Equals(args[0], "--help", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Crash Bandicoot: Recompiled");
            Console.WriteLine("  (no args)              open the launcher");
            Console.WriteLine("  --prepare <file.cue>   prepare game folder without UI");
            Console.WriteLine("  --run [file.cue]       prepare (if needed) and play (no UI)");
            Console.WriteLine("  --smoke [file.cue]     load prepared game briefly (debug)");
            return 0;
        }

        // WinForms requires STA ([STAThread] above). Do not flip apartment mode here.
        ApplicationConfiguration.Initialize();
        Application.Run(new LauncherHost());
        return 0;
    }
}
