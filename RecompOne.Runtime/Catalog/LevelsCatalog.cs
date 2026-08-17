using System.Text.Json;

namespace RecompOne.Runtime.Catalogs;

public sealed class LevelsCatalog
{
    public static LevelsCatalog Empty { get; } = new(DefaultLevelIdAddr, []);

    const uint DefaultLevelIdAddr = 0x80056710u;

    readonly Dictionary<uint, LevelInfo> _byId;
    readonly Dictionary<string, LevelInfo> _bySlug;

    LevelsCatalog(uint levelIdAddr, IEnumerable<LevelInfo> levels)
    {
        LevelIdAddr = levelIdAddr;
        _byId = new Dictionary<uint, LevelInfo>();
        _bySlug = new Dictionary<string, LevelInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var level in levels)
        {
            _byId[level.Id] = level;
            if (!string.IsNullOrWhiteSpace(level.Slug))
                _bySlug[level.Slug] = level;
        }
    }

    public uint LevelIdAddr { get; }
    public int Count => _byId.Count;

    public bool TryGet(uint id, out LevelInfo info) => _byId.TryGetValue(id, out info!);

    public bool TryGetBySlug(string slug, out LevelInfo info)
    {
        info = null!;
        if (string.IsNullOrWhiteSpace(slug)) return false;
        return _bySlug.TryGetValue(slug, out info!);
    }

    public bool TryGetCurrent(out LevelInfo info)
    {
        info = null!;
        if (!TryReadCurrentId(out uint id)) return false;
        return TryGet(id, out info);
    }

    public bool TryReadCurrentId(out uint id)
    {
        id = 0;
        var mem = Runtime.Mem;
        if (mem == null) return false;
        try
        {
            id = mem.ReadU32(LevelIdAddr);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool IsUiOrCinema(uint id)
    {
        if (TryGet(id, out var info))
            return info.IsUiOrCinema;
        // Fallback mirrors the pre-catalog GpuHle gate constants.
        return id is 0x19u or 0x2Du or 0x38u or 0x39u;
    }

    public bool IsGameplay(uint id) =>
        TryGet(id, out var info) ? info.Kind == LevelKind.Gameplay : !IsUiOrCinema(id);

    /// <summary>Gameplay + bonus: skip the 30 FPS pad; physics uses raw ticks_per_frame.</summary>
    public bool AllowsUnlockedFps(uint id)
    {
        if (TryGet(id, out var info))
            return info.Kind is LevelKind.Gameplay or LevelKind.Bonus;
        return !IsUiOrCinema(id);
    }

    internal static LevelsCatalog FromJson(JsonDocument? doc, uint defaultAddr)
    {
        if (doc == null) return Empty;
        var root = doc.RootElement;
        uint addr = Catalog.ParseHexUInt(
            root.TryGetProperty("levelIdAddr", out var a) ? a.GetString() : null,
            defaultAddr);

        var list = new List<LevelInfo>();
        if (root.TryGetProperty("levels", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                var dto = JsonSerializer.Deserialize<LevelDto>(el.GetRawText(), DtoOpts);
                if (dto == null) continue;
                list.Add(new LevelInfo
                {
                    Id = dto.Id,
                    Slug = dto.Slug ?? $"level-{dto.Id}",
                    Name = dto.Name ?? dto.Slug ?? $"Level {dto.Id}",
                    Kind = ParseKind(dto.Kind),
                    Code = dto.Code,
                });
            }
        }

        return new LevelsCatalog(addr, list);
    }

    static LevelKind ParseKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "gameplay" => LevelKind.Gameplay,
        "titlemap" or "title-map" or "title_map" => LevelKind.TitleMap,
        "complete" => LevelKind.Complete,
        "cinema" => LevelKind.Cinema,
        "bonus" => LevelKind.Bonus,
        "unused" => LevelKind.Unused,
        "empty" => LevelKind.Empty,
        _ => LevelKind.Empty,
    };

    static readonly JsonSerializerOptions DtoOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    sealed class LevelDto
    {
        public uint Id { get; set; }
        public string? Slug { get; set; }
        public string? Name { get; set; }
        public string? Kind { get; set; }
        public string? Code { get; set; }
    }
}
