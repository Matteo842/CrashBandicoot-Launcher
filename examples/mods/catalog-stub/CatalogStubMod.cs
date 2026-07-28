using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Modding;

/// <summary>
/// Demonstrates catalog lookup: logs level slug/name, and whether VRAM Loads /
/// SPU DMA uploads resolve to a catalog id (DB starts empty — expect unresolved).
/// Does not mutate pixels or audio. Zero copyrighted assets.
/// </summary>
public sealed class CatalogStubMod : IMod
{
    const int LogLimit = 16;
    int _vramLogged;
    int _spuLogged;
    uint? _lastLevel;

    public void OnLoad()
    {
        Event.AddListener<VSyncEvent>(OnVSync);
        Event.AddListener<VramTransferEvent>(OnVram);
        Event.AddListener<SpuDmaEvent>(OnSpu);
        Console.WriteLine("[catalog-stub] listening for level / texture / sound catalog matches");
        if (Catalog.Levels.TryGetBySlug("n-sanity-beach", out var beach))
            Console.WriteLine($"[catalog-stub] levels ok: {beach.Slug} = id {beach.Id}");
    }

    public void OnUnload()
    {
        Event.RemoveListener<VSyncEvent>(OnVSync);
        Event.RemoveListener<VramTransferEvent>(OnVram);
        Event.RemoveListener<SpuDmaEvent>(OnSpu);
    }

    void OnVSync(VSyncEvent e)
    {
        if (!Catalog.Levels.TryReadCurrentId(out uint id)) return;
        if (_lastLevel == id) return;
        _lastLevel = id;
        if (Catalog.Levels.TryGet(id, out var info))
            Console.WriteLine($"[catalog-stub] level → {info.Slug} ({info.Name}) kind={info.Kind}");
        else
            Console.WriteLine($"[catalog-stub] level → unknown id 0x{id:X}");
    }

    void OnVram(VramTransferEvent e)
    {
        if (e.Direction != VramTransfer.Load) return;
        if (_vramLogged >= LogLimit) return;
        _vramLogged++;
        if (Catalog.Textures.Resolve(e, out var tex) && tex != null)
            Console.WriteLine($"[catalog-stub] texture match id={tex.Id} rect=({e.X},{e.Y}) {e.W}x{e.H}");
        else
        {
            string hash = e.Pixels != null && e.PixelCount > 0
                ? Fingerprint.Sha256Hex(e.Pixels.AsSpan(0, e.PixelCount))
                : "n/a";
            Console.WriteLine(
                $"[catalog-stub] texture unresolved rect=({e.X},{e.Y}) {e.W}x{e.H} hash={hash}");
        }
    }

    void OnSpu(SpuDmaEvent e)
    {
        if (_spuLogged >= LogLimit) return;
        _spuLogged++;
        if (Catalog.Sounds.Resolve(e, out var snd) && snd != null)
            Console.WriteLine($"[catalog-stub] sound match id={snd.Id} spu=0x{e.SpuAddr:X} len={e.Length}");
        else
        {
            var span = e.Data != null && e.Length > 0
                ? e.Data.AsSpan(0, Math.Min(e.Length, e.Data.Length))
                : ReadOnlySpan<byte>.Empty;
            string hash = span.Length > 0 ? Fingerprint.Sha256Hex(span) : "n/a";
            Console.WriteLine(
                $"[catalog-stub] sound unresolved spu=0x{e.SpuAddr:X} len={e.Length} hash={hash}");
        }
    }
}
