using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Modding;

/// <summary>
/// Proves PNG→BGR555: folder scan registers textures/*.png. Stamps a magenta
/// checker corner into each large VRAM Load so you can see it without needing
/// catalog rect fingerprints. Full-rect auto-replace still runs when a Load
/// resolves to demo_tile_a / demo_tile_b. Zero copyrighted art.
/// </summary>
public sealed class TextureReplaceStubMod : IMod
{
    const int Stamp = 16;
    const int LogLimit = 12;

    ushort[]? _stamp;
    int _stampW, _stampH;
    int _logged;
    bool _announced;

    public void OnLoad()
    {
        // Folder scan already registered PNGs; pull decoded pixels for the corner stamp.
        if (TextureReplacements.TryGet("demo_tile_a", out var px, out int w, out int h))
        {
            _stamp = px;
            _stampW = w;
            _stampH = h;
        }

        Event.AddListener<VramTransferEvent>(OnVram);
        Console.WriteLine(
            "[texture-replace-stub] PNG stamp on Loads ≥16×16 (magenta checker corner)" +
            (_stamp != null ? "" : " — WARNING: demo_tile_a not registered"));
    }

    public void OnUnload()
    {
        Event.RemoveListener<VramTransferEvent>(OnVram);
    }

    void OnVram(VramTransferEvent e)
    {
        if (e.Direction != VramTransfer.Load || e.Pixels == null) return;

        bool catalogHit = Catalog.Textures.Resolve(e, out var tex) && tex != null
            && tex.Id is "demo_tile_a" or "demo_tile_b";

        if (_stamp != null && e.W >= Stamp && e.H >= Stamp)
        {
            BlitStamp(e.Pixels, e.W, e.H);
            if (!_announced)
            {
                _announced = true;
                Console.WriteLine(
                    "[texture-replace-stub] OK — look for a magenta checker square on newly loaded textures");
            }
        }

        if (!catalogHit && _logged >= LogLimit) return;
        if (catalogHit || _logged < LogLimit)
        {
            if (_logged < LogLimit) _logged++;
            if (catalogHit)
                Console.WriteLine(
                    $"[texture-replace-stub] catalog id={tex!.Id} rect=({e.X},{e.Y}) {e.W}x{e.H}");
        }
    }

    void BlitStamp(ushort[] dest, int dw, int dh)
    {
        for (int row = 0; row < Stamp && row < dh; row++)
        {
            int sy = row * _stampH / Stamp;
            for (int col = 0; col < Stamp && col < dw; col++)
            {
                int sx = col * _stampW / Stamp;
                dest[row * dw + col] = _stamp![sy * _stampW + sx];
            }
        }
    }
}
