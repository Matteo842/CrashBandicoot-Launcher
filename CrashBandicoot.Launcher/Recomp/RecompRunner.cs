using System.Globalization;
using RecompOne.Recompiler.CodeGen;
using RecompOne.Recompiler.Config;
using RecompOne.Runtime.Cdrom;

namespace CrashBandicoot.Launcher.Recomp;

public static class RecompRunner
{
    public static void Run(
        string configTemplatePath,
        string cuePath,
        string outDir,
        IProgress<string>? progress = null,
        string? postPassPatchPath = null)
    {
        // Generated C# must not pick up the phone/OS thousands separator
        // (it-IT "1.024", sv-SE "1 024") or Roslyn reports CS1525.
        var restoreCulture = CultureInfo.CurrentCulture;
        var restoreUi = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        try
        {
            RunCore(configTemplatePath, cuePath, outDir, progress, postPassPatchPath);
        }
        finally
        {
            CultureInfo.CurrentCulture = restoreCulture;
            CultureInfo.CurrentUICulture = restoreUi;
        }
    }

    static void RunCore(
        string configTemplatePath,
        string cuePath,
        string outDir,
        IProgress<string>? progress,
        string? postPassPatchPath)
    {
        progress?.Report("Loading recompiler config…");
        if (!File.Exists(configTemplatePath))
            throw new FileNotFoundException("CrashBandicoot.json not found next to the launcher.", configTemplatePath);

        Directory.CreateDirectory(outDir);
        foreach (var stale in Directory.EnumerateFiles(outDir, "*.cs"))
            File.Delete(stale);

        var config = ConfigLoader.Load(configTemplatePath);
        config.Cue = Path.GetFullPath(cuePath);
        config.Game.Output = Path.GetFullPath(outDir);

        string? Resolve(string? p) =>
            p == null ? null : Path.IsPathRooted(p) ? Path.GetFullPath(p) : Path.GetFullPath(Path.Combine(outDir, p));

        config.Elf = Resolve(config.Elf);
        config.Map = Resolve(config.Map);
        config.FuncMap = Resolve(config.FuncMap);
        foreach (var overlay in config.Overlays)
        {
            overlay.Elf = Resolve(overlay.Elf);
            overlay.Map = Resolve(overlay.Map);
            overlay.FuncMap = Resolve(overlay.FuncMap);
        }

        var resolvedCue = Path.GetFullPath(cuePath);
        var resolvedOut = Path.GetFullPath(outDir);

        progress?.Report("Reading disc and recompiling…");
        Directory.CreateDirectory(resolvedOut);
        using var fs = CueFs.Open(resolvedCue);
        OverlayWriter.Write(config, fs, resolvedOut);

        progress?.Report("Applying Crash compatibility post-pass…");
        var mainCs = Path.Combine(resolvedOut, "main.cs");
        var patch = postPassPatchPath ?? FindPatchPath();
        PostPassApplier.Apply(mainCs, patch);

        progress?.Report("Recompilation finished.");
    }

    static string FindPatchPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "Recomp", "Patches", "main.cs.patch"),
            Path.Combine(baseDir, "Patches", "main.cs.patch"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "Recomp", "Patches", "main.cs.patch")),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        throw new FileNotFoundException("main.cs.patch not found. Ensure Recomp/Patches is copied to the output.");
    }
}
