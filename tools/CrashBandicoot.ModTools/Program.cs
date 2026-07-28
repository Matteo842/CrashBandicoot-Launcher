using System.Text.Json;
using RecompOne.Runtime.Cdrom;
using RecompOne.Runtime.Modding;

namespace CrashBandicoot.ModTools;

static class Program
{
    static int Main(string[] args)
    {
        try
        {
            var cli = CliArgs.Parse(args);
            return cli.Command switch
            {
                "" or "help" or "-h" or "--help" => PrintHelp(),
                "scaffold" => CmdScaffold(cli),
                "export-disc" => CmdExportDisc(cli),
                "add-texture" => CmdAddTexture(cli),
                _ => Fail($"Unknown command '{cli.Command}'. Run without args for help."),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    static int PrintHelp()
    {
        Console.WriteLine("""
            CrashBandicoot.ModTools — export / scaffold asset packs (declared mod.json assets)

            Usage:
              CrashBandicoot.ModTools scaffold --id <mod-id> [--name <display>] [--out <modsDir>]
              CrashBandicoot.ModTools export-disc --iso <ISO-path> --mod <id> [--cue <path.cue>] [--out <modsDir>]
              CrashBandicoot.ModTools add-texture --mod <id> --id <catalog-id> --png <file.png> [--out <modsDir>]
              CrashBandicoot.ModTools add-texture --mod <id> --id <catalog-id> --from-capture <png|dir> [--out <modsDir>]

            Defaults:
              --out  ./mods  (cwd-relative)
              --cue  settings.json CdPath next to this tool's exe, if present

            Notes:
              • Packs use declared-only assets (no C# required).
              • With CatalogDiscovery=true, unknown VRAM Loads dump to captures/textures/*.png
              • Do not commit retail disc dumps or captures into the repo.
            """);
        return 0;
    }

    static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 1;
    }

    static string ResolveModsOut(CliArgs cli)
    {
        var o = cli.Get("out");
        if (string.IsNullOrWhiteSpace(o) || o == "true")
            return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "mods"));
        return Path.GetFullPath(o);
    }

    static int CmdScaffold(CliArgs cli)
    {
        string id = cli.Require("id").Trim();
        if (id.Contains('/') || id.Contains('\\') || id.Contains(".."))
            return Fail("Mod id must be a single folder name (no path separators).");

        string name = cli.Get("name") is { Length: > 0 } n && n != "true" ? n : id;
        string modsOut = ResolveModsOut(cli);
        string modDir = ModJson.ModDir(modsOut, id);

        if (Directory.Exists(modDir) && File.Exists(ModJson.ModJsonPath(modsOut, id)))
            return Fail($"Mod already exists: {modDir}");

        Directory.CreateDirectory(Path.Combine(modDir, "textures"));
        Directory.CreateDirectory(Path.Combine(modDir, "disc"));
        Directory.CreateDirectory(Path.Combine(modDir, "audio"));

        var info = new ModInfo
        {
            Id = id,
            Name = name,
            Version = "1.0.0",
            Author = "",
            Dependencies = [],
            Assets = new ModAssets
            {
                Textures = [],
                Audio = [],
                Disc = [],
            },
        };
        ModJson.Save(modsOut, info);

        Console.WriteLine($"Created asset pack: {modDir}");
        Console.WriteLine("  mod.json  textures/  disc/  audio/");
        ModJson.PrintAssetsSnippet(info);
        return 0;
    }

    static int CmdExportDisc(CliArgs cli)
    {
        string modId = cli.Require("mod").Trim();
        string isoPath = ModAssetFiles.NormalizeRel(cli.Require("iso"));
        if (isoPath.Length == 0)
            return Fail("Invalid --iso path.");

        string modsOut = ResolveModsOut(cli);
        var info = ModJson.LoadOrThrow(modsOut, modId);

        string cue = ResolveCue(cli);
        if (string.IsNullOrWhiteSpace(cue) || !File.Exists(cue))
            return Fail("Cue not found. Pass --cue path/to/game.cue (or set CdPath in settings.json).");

        byte[] data;
        using (var fs = CueFs.Open(cue))
        {
            try
            {
                data = fs.ReadFileWithoutOverlay(isoPath);
            }
            catch (FileNotFoundException)
            {
                return Fail($"ISO path not on disc: '{isoPath}' (cue={cue})");
            }
        }

        string hostRel = "disc/" + isoPath.Replace('\\', '/');
        string hostFull = Path.Combine(ModJson.ModDir(modsOut, modId),
            hostRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(hostFull)!);
        File.WriteAllBytes(hostFull, data);

        ModJson.UpsertDisc(info, isoPath, hostRel);
        ModJson.Save(modsOut, info);

        Console.WriteLine($"Exported {data.Length} bytes: {isoPath} → {modId}/{hostRel}");
        ModJson.PrintAssetsSnippet(info);
        return 0;
    }

    static int CmdAddTexture(CliArgs cli)
    {
        string modId = cli.Require("mod").Trim();
        string texId = cli.Require("id").Trim();
        string modsOut = ResolveModsOut(cli);
        var info = ModJson.LoadOrThrow(modsOut, modId);

        string? pngPath = null;
        var pngFlag = cli.Get("png");
        var fromCapture = cli.Get("from-capture");

        if (!string.IsNullOrWhiteSpace(pngFlag) && pngFlag != "true")
        {
            pngPath = Path.GetFullPath(pngFlag);
        }
        else if (!string.IsNullOrWhiteSpace(fromCapture) && fromCapture != "true")
        {
            pngPath = ResolveCapturePng(fromCapture);
        }
        else
        {
            return Fail("Provide --png <file.png> or --from-capture <png|dir>.");
        }

        if (!File.Exists(pngPath))
            return Fail($"PNG not found: {pngPath}");

        string hostRel = $"textures/{texId}.png";
        string hostFull = Path.Combine(ModJson.ModDir(modsOut, modId),
            hostRel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(hostFull)!);
        File.Copy(pngPath, hostFull, overwrite: true);

        ModJson.UpsertTexture(info, texId, hostRel);
        ModJson.Save(modsOut, info);

        Console.WriteLine($"Added texture '{texId}' ← {pngPath}");
        Console.WriteLine($"  → {modId}/{hostRel}");
        ModJson.PrintAssetsSnippet(info);
        return 0;
    }

    static string ResolveCapturePng(string fromCapture)
    {
        var path = Path.GetFullPath(fromCapture);
        if (File.Exists(path) && path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return path;

        if (!Directory.Exists(path))
            throw new FileNotFoundException($"Capture path not found: {path}");

        var pngs = Directory.EnumerateFiles(path, "*.png", SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (pngs.Count == 0)
            throw new FileNotFoundException($"No PNG files in capture dir: {path}");
        if (pngs.Count == 1)
            return pngs[0];

        throw new InvalidOperationException(
            $"Multiple PNGs in '{path}' ({pngs.Count}). Pass a specific file to --from-capture.");
    }

    static string ResolveCue(CliArgs cli)
    {
        var cue = cli.Get("cue");
        if (!string.IsNullOrWhiteSpace(cue) && cue != "true")
            return Path.GetFullPath(cue);

        try
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath)
                ?? AppContext.BaseDirectory;
            var settingsPath = Path.Combine(exeDir, "settings.json");
            if (!File.Exists(settingsPath))
            {
                // Also try cwd (common when running via `dotnet run` from repo)
                settingsPath = Path.Combine(Directory.GetCurrentDirectory(), "settings.json");
            }
            if (!File.Exists(settingsPath))
                return "";

            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (doc.RootElement.TryGetProperty("CdPath", out var cd) ||
                doc.RootElement.TryGetProperty("cdPath", out cd))
            {
                var p = cd.GetString();
                if (!string.IsNullOrWhiteSpace(p))
                    return Path.GetFullPath(p);
            }
        }
        catch
        {
            // ignore — caller reports missing cue
        }

        return "";
    }
}
