namespace CrashBandicoot.Launcher.Recomp;

public sealed class PipelineProgress
{
    public string Stage { get; init; } = "";
    public string Detail { get; init; } = "";
    public float Fraction { get; init; }
}

public static class RecompPipeline
{
    public static string ConfigPath
    {
        get
        {
            var baseDir = AppContext.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(baseDir, "Recomp", "CrashBandicoot.json"),
                Path.Combine(baseDir, "CrashBandicoot.json"),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "Recomp", "CrashBandicoot.json")),
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            throw new FileNotFoundException("CrashBandicoot.json not found.");
        }
    }

    /// <summary>Ensure the prepared game under game/ exists for this disc; returns DLL path.</summary>
    public static string EnsureReady(string cuePath, IProgress<PipelineProgress>? progress = null)
    {
        void Report(string stage, string detail, float f) =>
            progress?.Report(new PipelineProgress { Stage = stage, Detail = detail, Fraction = f });

        Report("Validate", "Checking disc image…", 0.05f);
        var v = DiscValidator.Validate(cuePath);
        if (!v.Ok)
            throw new InvalidOperationException($"{v.Title}: {v.Problem} — {v.Fix}");

        if (GameStore.TryGetValid(v.Fingerprint, v.CuePath, out var dll) && File.Exists(dll))
        {
            Report("Game", "Using prepared game (disc dump still required).", 1f);
            return dll;
        }

        var srcDir = GameStore.SourcesDir(v.Fingerprint);
        var dllPath = GameStore.DllPath(v.Fingerprint);
        Directory.CreateDirectory(srcDir);

        var textProgress = new Progress<string>(msg => Report("Recompile", msg, 0.35f));
        Report("Recompile", "Recompiling from your disc (first time only)…", 0.15f);
        RecompRunner.Run(ConfigPath, v.CuePath, srcDir, textProgress);

        Report("Compile", "Compiling native game assembly…", 0.7f);
        GameCompiler.CompileToDll(srcDir, dllPath, new Progress<string>(msg => Report("Compile", msg, 0.85f)));

        GameStore.WriteManifest(v.Fingerprint, v.CuePath, v.BinPath, dllPath);
        Report("Done", "Game ready.", 1f);
        return dllPath;
    }
}
