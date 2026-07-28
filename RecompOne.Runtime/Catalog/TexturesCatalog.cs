using System.Text.Json;

namespace RecompOne.Runtime.Catalogs;

public sealed class TexturesCatalog
{
    public static TexturesCatalog Empty { get; } = new([]);

    readonly Dictionary<string, TextureInfo> _byId;
    readonly List<TextureInfo> _entries;

    TexturesCatalog(IEnumerable<TextureInfo> entries)
    {
        _entries = entries.ToList();
        _byId = new Dictionary<string, TextureInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _entries)
            _byId[e.Id] = e;
    }

    public int Count => _entries.Count;

    public bool TryGet(string id, out TextureInfo info)
    {
        info = null!;
        if (string.IsNullOrWhiteSpace(id)) return false;
        return _byId.TryGetValue(id, out info!);
    }

    /// <summary>Resolve a VRAM Load against catalog fingerprints (hash preferred, else rect).</summary>
    public bool Resolve(Events.VramTransferEvent e, out TextureInfo? info)
    {
        info = null;
        if (e.Direction != Events.VramTransfer.Load) return false;
        if (!TryResolve(e.X, e.Y, e.W, e.H, e.Pixels, e.PixelCount, out var matched))
            return false;
        info = matched;
        return true;
    }

    /// <summary>
    /// Register a PNG replacement for a catalog texture id (RGBA→BGR555).
    /// Applied automatically on matching VRAM Loads (see <see cref="TextureReplacements"/>).
    /// </summary>
    public void Replace(string id, ReadOnlySpan<byte> pngBytes)
    {
        if (!TextureReplacements.TryRegisterPng(id, pngBytes))
            Console.Error.WriteLine($"[Textures] Replace failed to decode PNG for id={id}");
    }

    /// <summary>Register a PNG file replacement for a catalog texture id.</summary>
    public void Replace(string id, string pngPath)
    {
        if (!TextureReplacements.TryRegisterPngFile(id, pngPath))
            Console.Error.WriteLine($"[Textures] Replace failed for id={id} path={pngPath}");
    }

    /// <summary>Remove a previously registered replacement.</summary>
    public void ClearReplace(string id) => TextureReplacements.Remove(id);

    internal bool TryResolve(
        int x, int y, int w, int h, ushort[]? pixels, int count, out TextureInfo info)
    {
        info = null!;
        if (_entries.Count == 0) return false;

        string? hash = null;
        if (pixels != null && count > 0)
            hash = Fingerprint.Sha256Hex(pixels.AsSpan(0, count));

        uint? levelId = null;
        if (Catalog.Levels.TryReadCurrentId(out uint lid))
            levelId = lid;

        // Prefer content hash matches.
        if (hash != null)
        {
            foreach (var e in _entries)
            {
                if (string.IsNullOrEmpty(e.PixelHash)) continue;
                if (!hash.Equals(e.PixelHash, StringComparison.OrdinalIgnoreCase)) continue;
                if (!LevelOk(e, levelId)) continue;
                info = e;
                return true;
            }
        }

        // Fall back to destination rect (+ optional level filter).
        foreach (var e in _entries)
        {
            if (!string.IsNullOrEmpty(e.PixelHash)) continue; // hash-only entries skip rect path
            if (e.W <= 0 || e.H <= 0) continue;
            if (e.X != x || e.Y != y || e.W != w || e.H != h) continue;
            if (!LevelOk(e, levelId)) continue;
            info = e;
            return true;
        }

        return false;
    }

    static bool LevelOk(TextureInfo e, uint? levelId)
    {
        if (e.LevelIds == null || e.LevelIds.Count == 0) return true;
        if (levelId == null) return false;
        return e.LevelIds.Contains(levelId.Value);
    }

    internal static TexturesCatalog FromJson(JsonDocument? doc)
    {
        if (doc == null) return Empty;
        var root = doc.RootElement;
        var list = new List<TextureInfo>();
        if (!root.TryGetProperty("textures", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Empty;

        foreach (var el in arr.EnumerateArray())
        {
            string? id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) continue;

            int x = 0, y = 0, w = 0, h = 0;
            if (el.TryGetProperty("match", out var match) && match.ValueKind == JsonValueKind.Object)
            {
                if (match.TryGetProperty("x", out var xv)) x = xv.GetInt32();
                if (match.TryGetProperty("y", out var yv)) y = yv.GetInt32();
                if (match.TryGetProperty("w", out var wv)) w = wv.GetInt32();
                if (match.TryGetProperty("h", out var hv)) h = hv.GetInt32();
            }

            string? pixelHash = null;
            if (el.TryGetProperty("pixelHash", out var ph) && ph.ValueKind == JsonValueKind.String)
                pixelHash = ph.GetString();

            List<uint>? levelIds = null;
            if (el.TryGetProperty("levelIds", out var lids) && lids.ValueKind == JsonValueKind.Array)
            {
                levelIds = [];
                foreach (var lid in lids.EnumerateArray())
                {
                    if (lid.ValueKind == JsonValueKind.Number)
                        levelIds.Add(lid.GetUInt32());
                    else if (lid.ValueKind == JsonValueKind.String)
                        levelIds.Add(Catalog.ParseHexUInt(lid.GetString(), 0));
                }
            }

            list.Add(new TextureInfo
            {
                Id = id,
                X = x,
                Y = y,
                W = w,
                H = h,
                PixelHash = string.IsNullOrWhiteSpace(pixelHash) ? null : pixelHash,
                LevelIds = levelIds,
            });
        }

        return new TexturesCatalog(list);
    }
}
