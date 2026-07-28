using System.Text.Json;

namespace RecompOne.Runtime.Catalogs;

public sealed class SoundsCatalog
{
    public static SoundsCatalog Empty { get; } = new([]);

    readonly Dictionary<string, SoundInfo> _byId;
    readonly List<SoundInfo> _entries;

    SoundsCatalog(IEnumerable<SoundInfo> entries)
    {
        _entries = entries.ToList();
        _byId = new Dictionary<string, SoundInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _entries)
            _byId[e.Id] = e;
    }

    public int Count => _entries.Count;

    public bool TryGet(string id, out SoundInfo info)
    {
        info = null!;
        if (string.IsNullOrWhiteSpace(id)) return false;
        return _byId.TryGetValue(id, out info!);
    }

    public bool Resolve(Events.SpuDmaEvent e, out SoundInfo? info)
    {
        info = null;
        var span = e.Data != null && e.Length > 0
            ? e.Data.AsSpan(0, Math.Min(e.Length, e.Data.Length))
            : ReadOnlySpan<byte>.Empty;
        if (!TryResolve(e.SpuAddr, span, out var matched))
            return false;
        info = matched;
        return true;
    }

    internal bool TryResolve(uint spuAddr, ReadOnlySpan<byte> data, out SoundInfo info)
    {
        info = null!;
        if (_entries.Count == 0) return false;

        string? hash = data.Length > 0 ? Fingerprint.Sha256Hex(data) : null;

        if (hash != null)
        {
            foreach (var e in _entries)
            {
                if (string.IsNullOrEmpty(e.PayloadHash)) continue;
                if (!hash.Equals(e.PayloadHash, StringComparison.OrdinalIgnoreCase)) continue;
                info = e;
                return true;
            }
        }

        foreach (var e in _entries)
        {
            if (!string.IsNullOrEmpty(e.PayloadHash)) continue;
            if (e.Length <= 0) continue;
            if (e.SpuAddr != spuAddr || e.Length != data.Length) continue;
            info = e;
            return true;
        }

        return false;
    }

    internal static SoundsCatalog FromJson(JsonDocument? doc)
    {
        if (doc == null) return Empty;
        var root = doc.RootElement;
        var list = new List<SoundInfo>();
        if (!root.TryGetProperty("sounds", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Empty;

        foreach (var el in arr.EnumerateArray())
        {
            string? id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) continue;

            uint spuAddr = 0;
            int length = 0;
            if (el.TryGetProperty("match", out var match) && match.ValueKind == JsonValueKind.Object)
            {
                if (match.TryGetProperty("spuAddr", out var sa))
                {
                    if (sa.ValueKind == JsonValueKind.String)
                        spuAddr = Catalog.ParseHexUInt(sa.GetString(), 0);
                    else if (sa.ValueKind == JsonValueKind.Number)
                        spuAddr = sa.GetUInt32();
                }
                if (match.TryGetProperty("length", out var len) && len.ValueKind == JsonValueKind.Number)
                    length = len.GetInt32();
            }

            string? payloadHash = null;
            if (el.TryGetProperty("payloadHash", out var ph) && ph.ValueKind == JsonValueKind.String)
                payloadHash = ph.GetString();

            list.Add(new SoundInfo
            {
                Id = id,
                SpuAddr = spuAddr,
                Length = length,
                PayloadHash = string.IsNullOrWhiteSpace(payloadHash) ? null : payloadHash,
            });
        }

        return new SoundsCatalog(list);
    }
}
