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

    /// <summary>
    /// When widescreen is on, expand GTE horizontal FOV into side margins and present them.
    /// Off for Crash 1 non-gameplay levels (title/menu/map, intro, ending, completion):
    /// those keep clean 4:3 with black pillars — FOV expand only shows unfinished sides / stale RT junk.
    /// </summary>
    public static bool WideFovActive { get; private set; }

    // SCUS-94900: current level ID (cbhacks Memory Map).
    const uint Crash1LevelIdAddr = 0x80056710u;
    const uint Crash1TitleMenuMap = 0x19u; // title, menus, map, game over
    const uint Crash1LevelComplete = 0x2Du;
    const uint Crash1IntroLevel = 0x38u;   // Naughty Dog house / opening cinema
    const uint Crash1EndingLevel = 0x39u;

    static bool IsPillarboxLevel(uint level) =>
        level == Crash1TitleMenuMap || level == Crash1LevelComplete
        || level == Crash1IntroLevel || level == Crash1EndingLevel;

    /// <summary>Call once per frame (e.g. PutDrawEnv) before GTE draws.</summary>
    public static void RefreshWideFov()
    {
        if (WideAspect <= 0f)
        {
            WideFovActive = false;
            return;
        }

        var m = Runtime.Mem;
        if (m != null && IsPillarboxLevel(m.ReadU32(Crash1LevelIdAddr)))
        {
            WideFovActive = false;
            return;
        }

        WideFovActive = true;
    }

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
