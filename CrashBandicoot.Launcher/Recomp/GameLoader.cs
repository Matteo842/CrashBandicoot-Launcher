using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using RecompOne.Runtime;
using RecompOne.Runtime.Diagnostics;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Memory;

namespace CrashBandicoot.Launcher.Recomp;

public sealed class GameManifest
{
    public string Fingerprint { get; set; } = "";
    public string CuePath { get; set; } = "";
    public string BinPath { get; set; } = "";
    public string DllPath { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string PipelineVersion { get; set; } = GameStore.PipelineVersion;
}

/// <summary>
/// Persistent prepared/recompiled game next to the exe under game/{fingerprint}/.
/// Not a temp cache — files stay until the user deletes them.
/// </summary>
public static class GameStore
{
    public const string PipelineVersion = "4";

    public static string RootDir => AppPaths.GameDir;

    public static string SlotDir(string fingerprint) => Path.Combine(RootDir, fingerprint);

    public static string DllPath(string fingerprint) => Path.Combine(SlotDir(fingerprint), "game.recomp.dll");

    public static string SourcesDir(string fingerprint) => Path.Combine(SlotDir(fingerprint), "src");

    public static string ManifestPath(string fingerprint) => Path.Combine(SlotDir(fingerprint), "manifest.json");

    public static bool TryGetValid(string fingerprint, string cuePath, out string dllPath)
    {
        dllPath = DllPath(fingerprint);
        var man = ManifestPath(fingerprint);
        if (!File.Exists(dllPath) || !File.Exists(man)) return false;
        try
        {
            var m = JsonSerializer.Deserialize<GameManifest>(File.ReadAllText(man));
            if (m == null ||
                m.Fingerprint != fingerprint ||
                m.PipelineVersion != PipelineVersion ||
                !File.Exists(m.DllPath))
                return false;

            if (!DiscValidator.EnsureDiscPresentForLaunch(cuePath, fingerprint).Ok)
                return false;

            if (!string.IsNullOrWhiteSpace(m.BinPath) && !File.Exists(m.BinPath))
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void WriteManifest(string fingerprint, string cuePath, string? binPath, string dllPath)
    {
        Directory.CreateDirectory(SlotDir(fingerprint));
        var m = new GameManifest
        {
            Fingerprint = fingerprint,
            CuePath = cuePath,
            BinPath = binPath ?? "",
            DllPath = dllPath,
            CreatedUtc = DateTime.UtcNow.ToString("O"),
            PipelineVersion = PipelineVersion,
        };
        File.WriteAllText(ManifestPath(fingerprint), JsonSerializer.Serialize(m, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public static class GameLoader
{
    static AssemblyLoadContext? _ctx;
    static Assembly? _asm;

    public static void Run(string dllPath, string cuePath)
    {
        try
        {
            _ctx = new AssemblyLoadContext("CrashGame", isCollectible: false);
            _asm = _ctx.LoadFromAssemblyPath(Path.GetFullPath(dllPath));

            var entry = _asm.GetType("Recompiled.Entry")
                        ?? throw new InvalidOperationException("Recompiled.Entry not found in game DLL.");
            var run = entry.GetMethod("Run", BindingFlags.Public | BindingFlags.Static)
                      ?? throw new InvalidOperationException("Entry.Run not found.");

            run.Invoke(null, [new PSMemory(), cuePath]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is GameSessionEndedException)
        {
            // Embedded session closed — return to launcher UI.
            SessionLog.Stop();
        }
        catch (GameSessionEndedException)
        {
            SessionLog.Stop();
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            SessionLog.Exception("Entry.Run", ex.InnerException);
            SessionLog.Stop();
            throw new InvalidOperationException(ex.InnerException.Message, ex.InnerException);
        }
        catch (Exception ex)
        {
            SessionLog.Exception("GameLoader.Run", ex);
            SessionLog.Stop();
            throw;
        }
    }
}
