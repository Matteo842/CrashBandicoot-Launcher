using System.Globalization;
using System.Text.Json;
using RecompOne.Runtime.Config;

namespace RecompOne.Runtime.Catalogs;

/// <summary>
/// Stable asset / level catalogs for modding (SCUS-94900).
/// Embedded defaults + optional loose overrides under <see cref="AppPaths.CatalogDir"/>.
/// </summary>
public static class Catalog
{
    const uint DefaultLevelIdAddr = 0x80056710u;
    const int DiscoveryCap = 256;

    static readonly object Gate = new();
    static bool _initialized;
    static LevelsCatalog _levels = LevelsCatalog.Empty;
    static TexturesCatalog _textures = TexturesCatalog.Empty;
    static SoundsCatalog _sounds = SoundsCatalog.Empty;
    static readonly HashSet<string> _loggedTextures = new(StringComparer.Ordinal);
    static readonly HashSet<string> _loggedSounds = new(StringComparer.Ordinal);

    public static LevelsCatalog Levels
    {
        get { EnsureInitialized(); return _levels; }
    }

    public static TexturesCatalog Textures
    {
        get { EnsureInitialized(); return _textures; }
    }

    public static SoundsCatalog Sounds
    {
        get { EnsureInitialized(); return _sounds; }
    }

    /// <summary>When true, log unknown VRAM Loads / SPU DMA uploads once per fingerprint.</summary>
    public static bool DiscoveryEnabled { get; set; }

    /// <summary>VA of the current level id (from levels catalog, else SCUS-94900 default).</summary>
    public static uint LevelIdAddr
    {
        get { EnsureInitialized(); return _levels.LevelIdAddr; }
    }

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized) return;
            LoadUnlocked();
            _initialized = true;
        }
    }

    static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (Gate)
        {
            if (_initialized) return;
            LoadUnlocked();
            _initialized = true;
        }
    }

    static void LoadUnlocked()
    {
        try
        {
            DiscoveryEnabled = ConfigManager.Game.CatalogDiscovery;
        }
        catch
        {
            DiscoveryEnabled = false;
        }

        using var levelsDoc = LoadJsonDoc("levels.scus94900.json");
        using var texturesDoc = LoadJsonDoc("textures.scus94900.json");
        using var soundsDoc = LoadJsonDoc("sounds.scus94900.json");

        // Loose files replace embedded for that catalog kind.
        using var levelsLoose = TryLoadLoose("levels.scus94900.json");
        using var texturesLoose = TryLoadLoose("textures.scus94900.json");
        using var soundsLoose = TryLoadLoose("sounds.scus94900.json");

        _levels = LevelsCatalog.FromJson(levelsLoose ?? levelsDoc, DefaultLevelIdAddr);
        _textures = TexturesCatalog.FromJson(texturesLoose ?? texturesDoc);
        _sounds = SoundsCatalog.FromJson(soundsLoose ?? soundsDoc);

        Console.WriteLine(
            $"[Catalog] levels={_levels.Count} textures={_textures.Count} sounds={_sounds.Count}" +
            (DiscoveryEnabled ? " discovery=on" : ""));
    }

    static JsonDocument? LoadJsonDoc(string fileName)
    {
        var asm = typeof(Catalog).Assembly;
        string expected = $"{asm.GetName().Name}.Catalog.data.{fileName}";
        string? name = asm.GetManifestResourceNames().FirstOrDefault(
            n => n == expected || n.EndsWith("." + fileName, StringComparison.Ordinal));
        if (name == null) return null;
        using var stream = asm.GetManifestResourceStream(name);
        if (stream == null) return null;
        return JsonDocument.Parse(stream);
    }

    static JsonDocument? TryLoadLoose(string fileName)
    {
        try
        {
            var path = Path.Combine(AppPaths.CatalogDir, fileName);
            if (!File.Exists(path)) return null;
            var doc = JsonDocument.Parse(File.ReadAllText(path));
            Console.WriteLine($"[Catalog] override ← catalog/{fileName}");
            return doc;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Catalog] failed to load catalog/{fileName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Called from VRAM Load path when discovery is on.</summary>
    internal static void ObserveTextureLoad(int x, int y, int w, int h, ushort[]? pixels, int count)
    {
        if (!DiscoveryEnabled) return;
        EnsureInitialized();
        if (_textures.TryResolve(x, y, w, h, pixels, count, out _)) return;

        string hash = pixels != null && count > 0
            ? Fingerprint.Sha256Hex(pixels.AsSpan(0, count))
            : "n/a";
        string key = $"t:{x},{y},{w},{h}:{hash}";
        if (!TryMarkLogged(_loggedTextures, key)) return;

        string level = FormatCurrentLevel();
        Console.WriteLine(
            $"[Catalog] unknown texture rect=({x},{y}) {w}x{h} hash={hash} level={level}");

        if (pixels != null && count > 0 && w > 0 && h > 0 && count >= w * h)
            TryDumpTextureCapture(x, y, w, h, hash, level, pixels.AsSpan(0, w * h));
    }

    static void TryDumpTextureCapture(
        int x, int y, int w, int h, string hash, string level, ReadOnlySpan<ushort> pixels)
    {
        try
        {
            string safeHash = hash == "n/a" ? "na" : hash;
            string stem = $"r{x}_{y}_{w}x{h}_{safeHash}";
            string dir = AppPaths.TextureCapturesDir;
            Directory.CreateDirectory(dir);
            string pngPath = Path.Combine(dir, stem + ".png");
            if (File.Exists(pngPath)) return;

            if (!PngBgr555.TryEncodeFile(pngPath, pixels, w, h))
                return;

            string metaPath = Path.Combine(dir, stem + ".json");
            var meta =
                $"{{\n" +
                $"  \"rect\": {{ \"x\": {x}, \"y\": {y}, \"w\": {w}, \"h\": {h} }},\n" +
                $"  \"pixelHash\": \"{safeHash}\",\n" +
                $"  \"level\": \"{EscapeJson(level)}\",\n" +
                $"  \"file\": \"{stem}.png\"\n" +
                "}\n";
            File.WriteAllText(metaPath, meta);
            Console.WriteLine($"[Catalog] capture → captures/textures/{stem}.png");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Catalog] capture failed: {ex.Message}");
        }
    }

    static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>Called from SPU DMA path when discovery is on.</summary>
    internal static void ObserveSound(uint spuAddr, ReadOnlySpan<byte> data)
    {
        if (!DiscoveryEnabled) return;
        EnsureInitialized();
        if (_sounds.TryResolve(spuAddr, data, out _)) return;

        string hash = data.Length > 0 ? Fingerprint.Sha256Hex(data) : "n/a";
        string key = $"s:0x{spuAddr:X}:{data.Length}:{hash}";
        if (!TryMarkLogged(_loggedSounds, key)) return;

        Console.WriteLine(
            $"[Catalog] unknown sound spu=0x{spuAddr:X} len={data.Length} hash={hash}");
    }

    static bool TryMarkLogged(HashSet<string> set, string key)
    {
        lock (Gate)
        {
            if (set.Count >= DiscoveryCap) return false;
            return set.Add(key);
        }
    }

    static string FormatCurrentLevel()
    {
        if (!_levels.TryGetCurrent(out var info))
        {
            if (_levels.TryReadCurrentId(out uint id))
                return $"0x{id:X}";
            return "?";
        }
        return $"0x{info.Id:X} ({info.Slug})";
    }

    internal static uint ParseHexUInt(string? s, uint fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)
            ? v
            : fallback;
    }
}
