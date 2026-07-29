using RecompOne.Runtime.Catalogs;

namespace RecompOne.Runtime.Hle;

public static class GpuHle
{
    public static bool Active { get; set; }
    public static IGpuBackend? Backend { get; set; }

    public static float WideAspect { get; set; }
    public static float OutputAspect { get; set; } = 4f / 3f;
    public static bool NativeResolution { get; set; }
    public static float TargetAspect { get; set; } = 4f / 3f;
    public const float BaseAspect = 4f / 3f;

    /// <summary>Configured texture filter mode (0 = off). See <see cref="Config.ViewConfig"/>.</summary>
    public static int TextureFilter { get; set; }

    /// <summary>0..1 blend strength for the active texture filter.</summary>
    public static float TextureFilterStrength { get; set; } = 0.5f;

    public static bool Dedither { get; set; }
    public static bool Dejitter { get; set; }

    /// <summary>Force nearest filtering on the present texture (crisp pixels when upscaled).</summary>
    public static bool PresentNearest { get; set; }

    /// <summary>
    /// When widescreen is on, expand GTE FOV into side margins during gameplay only.
    /// Off on title/menu/map/cinema — those expose unfinished sides / stale RT junk.
    /// </summary>
    public static bool WideFovActive { get; private set; }

    /// <summary>Texture filters follow the user setting on all levels (including menus).</summary>
    public static bool TextureFiltersActive { get; private set; }

    /// <summary>Dedither follows the user setting on all levels.</summary>
    public static bool DeditherActive { get; private set; }

    /// <summary>Dejitter follows the user setting (no-op where geometry is not GTE-projected).</summary>
    public static bool DejitterActive { get; private set; }

    /// <summary>Call once per frame (e.g. PutDrawEnv) before GTE draws.</summary>
    public static void RefreshWideFov()
    {
        bool uiOrCinema = false;
        var m = Runtime.Mem;
        if (m != null)
            uiOrCinema = Catalog.Levels.IsUiOrCinema(m.ReadU32(Catalog.LevelIdAddr));

        // Gameplay only — menus/map/cinema keep clean 4:3 with black pillars.
        WideFovActive = WideAspect > 0f && !uiOrCinema;

        TextureFiltersActive = TextureFilter > 0;
        DeditherActive = Dedither;
        DejitterActive = Dejitter;
    }

    /// <summary>Effective filter mode for the GPU (0 when gated off).</summary>
    public static int EffectiveTextureFilter => TextureFiltersActive ? TextureFilter : 0;

    public struct DispRect { public int X, Y, W, H; public long Stamp; public bool Valid; }

    static readonly DispRect[] _rects = new DispRect[2];
    static long _stamp;

    public static void NotifyDisplay(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        int slot = -1;
        for (int i = 0; i < _rects.Length; i++)
            if (_rects[i].Valid && _rects[i].X == x && _rects[i].Y == y) { slot = i; break; }
        if (slot < 0)
        {
            slot = 0;
            for (int i = 1; i < _rects.Length; i++)
                if (!_rects[i].Valid || _rects[i].Stamp < _rects[slot].Stamp) slot = i;
        }
        _rects[slot] = new DispRect { X = x, Y = y, W = w, H = h, Stamp = ++_stamp, Valid = true };
    }

    public static int RectCount => _rects.Length;

    public static DispRect GetRect(int i) => _rects[i];

    public static int WideMargin(int w)
    {
        // No side pads unless FOV expand is actually drawing into them (avoids stale gutter junk).
        if (WideAspect <= 0f || !WideFovActive) return 0;
        int wide = (int)MathF.Ceiling(w * WideAspect / BaseAspect);
        return Math.Max(0, (wide - w + 1) / 2);
    }
}
