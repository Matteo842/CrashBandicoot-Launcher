using System.Text.Json;
using System.Text.Json.Serialization;
using RecompOne.Runtime.Modding;

namespace CrashBandicoot.ModTools;

/// <summary>Load / save / merge <c>mod.json</c> asset packs (declared-only layout).</summary>
static class ModJson
{
    static readonly JsonSerializerOptions ReadOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string ModDir(string modsRoot, string modId) =>
        Path.Combine(modsRoot, modId);

    public static string ModJsonPath(string modsRoot, string modId) =>
        Path.Combine(ModDir(modsRoot, modId), "mod.json");

    public static ModInfo LoadOrThrow(string modsRoot, string modId)
    {
        var path = ModJsonPath(modsRoot, modId);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Mod not found: {path}. Run scaffold first.");
        var info = JsonSerializer.Deserialize<ModInfo>(File.ReadAllText(path), ReadOpts)
            ?? throw new InvalidDataException($"Invalid mod.json: {path}");
        if (string.IsNullOrWhiteSpace(info.Id))
            info.Id = modId;
        return info;
    }

    public static void Save(string modsRoot, ModInfo info)
    {
        var dir = ModDir(modsRoot, info.Id);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "mod.json");
        File.WriteAllText(path, JsonSerializer.Serialize(info, WriteOpts) + Environment.NewLine);
    }

    public static ModAssets EnsureAssets(ModInfo info)
    {
        info.Assets ??= new ModAssets();
        info.Assets.Textures ??= [];
        info.Assets.Audio ??= [];
        info.Assets.Disc ??= [];
        return info.Assets;
    }

    public static void UpsertTexture(ModInfo info, string id, string relFile)
    {
        var assets = EnsureAssets(info);
        relFile = ModAssetFiles.NormalizeRel(relFile);
        if (relFile.Length == 0)
            throw new ArgumentException("Invalid texture file path.");
        id = id.Trim();
        if (id.Length == 0)
            throw new ArgumentException("Texture id is required.");

        var list = assets.Textures!.ToList();
        var idx = list.FindIndex(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        var entry = new ModTextureAsset { Id = id, File = relFile };
        if (idx >= 0) list[idx] = entry;
        else list.Add(entry);
        assets.Textures = list.ToArray();
    }

    public static void UpsertDisc(ModInfo info, string isoPath, string relFile)
    {
        var assets = EnsureAssets(info);
        isoPath = ModAssetFiles.NormalizeRel(isoPath);
        relFile = ModAssetFiles.NormalizeRel(relFile);
        if (isoPath.Length == 0)
            throw new ArgumentException("Invalid ISO path.");
        if (relFile.Length == 0)
            throw new ArgumentException("Invalid disc file path.");

        var list = assets.Disc!.ToList();
        var idx = list.FindIndex(d => d.Path.Equals(isoPath, StringComparison.OrdinalIgnoreCase));
        var entry = new ModDiscAsset { Path = isoPath, File = relFile };
        if (idx >= 0) list[idx] = entry;
        else list.Add(entry);
        assets.Disc = list.ToArray();
    }

    public static void PrintAssetsSnippet(ModInfo info)
    {
        var assets = info.Assets ?? new ModAssets
        {
            Textures = [],
            Audio = [],
            Disc = [],
        };
        Console.WriteLine("--- assets snippet ---");
        Console.WriteLine(JsonSerializer.Serialize(assets, WriteOpts));
    }
}
