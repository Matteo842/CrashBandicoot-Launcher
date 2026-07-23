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
            Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        }
        catch
        {
            // ignore
        }

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
            Console.WriteLine("  --prepare <file.cue>   prepare game cache without UI");
            Console.WriteLine("  --smoke [file.cue]     load cached game briefly (debug)");
            return 0;
        }

        // WebView2 / WinForms require STA ([STAThread] above). Do not flip apartment mode here.
        ApplicationConfiguration.Initialize();

        LaunchRequest? launch;
        using (var host = new LauncherHost())
        {
            Application.Run(host);
            launch = host.Launch;
        }

        if (launch == null)
            return 0;

        try
        {
            RecompOne.Runtime.Config.ConfigManager.Load();
            RecompOne.Runtime.Config.ConfigManager.Game.CdPath = launch.CuePath;
            RecompOne.Runtime.Config.ConfigManager.SaveGame();

            Console.WriteLine($"[CrashBandicoot] launching {launch.DllPath}");
            GameLoader.Run(launch.DllPath, launch.CuePath);
            return 0;
        }
        catch (Exception ex)
        {
            var msg = ex;
            while (msg.InnerException != null) msg = msg.InnerException;
            Console.Error.WriteLine("[CrashBandicoot] launch failed: " + msg.Message);
            Console.Error.WriteLine(ex);
            MessageBox.Show(
                msg.Message,
                "Crash Bandicoot: Recompiled",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }
}
