using System.Globalization;

namespace RecompOne.Runtime.Config;

public class PanelState
{
    public bool Open { get; set; }
}

public class ViewConfig
{
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, PanelState> Panels { get; set; } = [];

    public bool GetBool(string key, bool fallback = false)
        => Values.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallback;

    public void SetBool(string key, bool value) => Values[key] = value.ToString();

    public int GetInt(string key, int fallback = 0)
        => Values.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;

    public void SetInt(string key, int value) => Values[key] = value.ToString(CultureInfo.InvariantCulture);

    public float GetFloat(string key, float fallback = 0f)
        => Values.TryGetValue(key, out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : fallback;

    public void SetFloat(string key, float value) => Values[key] = value.ToString(CultureInfo.InvariantCulture);

    public string GetString(string key, string fallback = "")
        => Values.TryGetValue(key, out var v) ? v : fallback;

    public void SetString(string key, string value) => Values[key] = value;

    public bool HideTopBar
    {
        get => GetBool("HideTopBar");
        set => SetBool("HideTopBar", value);
    }

    public bool Fullscreen
    {
        get => GetBool("Fullscreen");
        set => SetBool("Fullscreen", value);
    }

    /// <summary>Allowed internal render scales (1 = native PS1, 8 ≈ 4K).</summary>
    public static readonly int[] InternalResolutionOptions = [1, 2, 4, 8];

    /// <summary>
    /// GPU internal resolution multiplier. 1 = native, 4 = previous default, 8 ≈ 4K.
    /// Requires restart (VRAM textures are allocated at init).
    /// </summary>
    public int InternalResolution
    {
        get
        {
            if (Values.TryGetValue("InternalResolution", out var raw) &&
                int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                return SnapInternalResolution(i);

            // Legacy bool: NativeResolution=true → 1x, else previous enhanced default (4x).
            return GetBool("NativeResolution") ? 1 : 4;
        }
        set
        {
            int n = SnapInternalResolution(value);
            SetInt("InternalResolution", n);
            SetBool("NativeResolution", n <= 1);
        }
    }

    /// <summary>True when rendering at native 1x (legacy key kept in sync).</summary>
    public bool NativeResolution
    {
        get => InternalResolution <= 1;
        set
        {
            if (value)
                InternalResolution = 1;
            else if (InternalResolution <= 1)
                InternalResolution = 4;
        }
    }

    public static int SnapInternalResolution(int value)
    {
        if (value <= 1) return 1;
        if (value <= 2) return 2;
        if (value <= 4) return 4;
        return 8;
    }

    /// <summary>Texture filter modes shown in settings (index = mode id).</summary>
    public static readonly string[] TextureFilterLabels =
    [
        "Off",
        "Bilinear",
        "Sharp bilinear",
        "Soft smooth",
    ];

    public const int TextureFilterOff = 0;
    public const int TextureFilterBilinear = 1;
    public const int TextureFilterSharpBilinear = 2;
    public const int TextureFilterSoftSmooth = 3;

    /// <summary>
    /// Active texture filter. Live — no restart.
    /// Auto-disabled on title/menu/map and cinema levels.
    /// </summary>
    public int TextureFilter
    {
        get
        {
            if (Values.TryGetValue("TextureFilter", out var raw) &&
                int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                return SnapTextureFilter(i);

            // Legacy bool from the first bilinear toggle.
            return GetBool("BilinearFilter") ? TextureFilterBilinear : TextureFilterOff;
        }
        set
        {
            int n = SnapTextureFilter(value);
            SetInt("TextureFilter", n);
            SetBool("BilinearFilter", n == TextureFilterBilinear);
        }
    }

    /// <summary>0..1 blend of filtered vs nearest. Default 0.5 (full was too strong).</summary>
    public float TextureFilterStrength
    {
        get
        {
            if (!Values.ContainsKey("TextureFilterStrength"))
                return 0.5f;
            return Math.Clamp(GetFloat("TextureFilterStrength", 0.5f), 0f, 1f);
        }
        set => SetFloat("TextureFilterStrength", Math.Clamp(value, 0f, 1f));
    }

    public static int SnapTextureFilter(int value)
    {
        if (value < 0) return TextureFilterOff;
        if (value >= TextureFilterLabels.Length) return TextureFilterLabels.Length - 1;
        return value;
    }

    /// <summary>Remove PS1 dither noise (true-color style). Off on menus &amp; cutscenes.</summary>
    public bool Dedither
    {
        get => GetBool("Dedither");
        set => SetBool("Dedither", value);
    }

    /// <summary>Keep GTE subpixel screen coords to reduce polygon wobble. Off on menus &amp; cutscenes.</summary>
    public bool Dejitter
    {
        get => GetBool("Dejitter");
        set => SetBool("Dejitter", value);
    }

    /// <summary>
    /// Widescreen hack (gameplay only): GTE FOV expand + wide present. Off on menus/map/cinema.
    /// On Crash 1 this stretches pre-rendered 4:3 backgrounds — no extra scenery to reveal.
    /// </summary>
    public bool Widescreen
    {
        get => GetBool("Widescreen");
        set => SetBool("Widescreen", value);
    }

    /// <summary>Scale the framebuffer by whole pixels only (no blurry fractional upscale).</summary>
    public bool IntegerScale
    {
        get => GetBool("IntegerScale");
        set => SetBool("IntegerScale", value);
    }

    /// <summary>Nearest sampling on the presented frame (crisp pixels). Default on at 1x internal.</summary>
    public bool PresentNearest
    {
        get => GetBool("PresentNearest");
        set => SetBool("PresentNearest", value);
    }

    /// <summary>Host swap-interval VSync (reduces tearing; may add input latency).</summary>
    public bool VSync
    {
        get => GetBool("VSync");
        set => SetBool("VSync", value);
    }

    public const int FrameRateOriginal = 0;
    public const int FrameRateUncapped = -1;

    public static readonly int[] FrameRateOptionValues = [0, 60, 120, 240, -1];

    public static readonly string[] FrameRateLabels =
    [
        "Original (30 FPS)",
        "60 FPS",
        "120 FPS",
        "240 FPS",
        "Uncapped",
    ];

    /// <summary>
    /// 0 = original 30 FPS pad. 60/120/240 = unlocked present cap; sim dt is
    /// wall time (1020 ticks/s) so 60 and 120 play at the same speed. -1 = uncapped.
    /// Menus stay 30.
    /// </summary>
    public int FrameRate
    {
        get => SnapFrameRate(GetInt("FrameRate", FrameRateOriginal));
        set => SetInt("FrameRate", SnapFrameRate(value));
    }

    public static int SnapFrameRate(int value)
    {
        for (int i = 0; i < FrameRateOptionValues.Length; i++)
            if (FrameRateOptionValues[i] == value) return value;
        if (value < 0) return FrameRateUncapped;
        if (value < 45) return FrameRateOriginal;
        if (value < 90) return 60;
        if (value < 180) return 120;
        return 240;
    }

    public static int FrameRateToIndex(int value)
    {
        int snapped = SnapFrameRate(value);
        for (int i = 0; i < FrameRateOptionValues.Length; i++)
            if (FrameRateOptionValues[i] == snapped) return i;
        return 0;
    }

    /// <summary>Host hotkey that opens/closes the Developer Menu (Silk Key name, e.g. F3).</summary>
    public string CheatMenuKey
    {
        get
        {
            // Prefer new key; fall back to legacy MiracomandoKey if present.
            if (Values.TryGetValue("CheatMenuKey", out var v) && !string.IsNullOrWhiteSpace(v))
                return v;
            var legacy = GetString("MiracomandoKey", "");
            return string.IsNullOrWhiteSpace(legacy) ? "F3" : legacy;
        }
        set => SetString("CheatMenuKey", string.IsNullOrWhiteSpace(value) ? "F3" : value);
    }

    /// <summary>Last selected Developer Menu section id (default: cheats).</summary>
    public string DevMenuSection
    {
        get
        {
            var v = GetString("DevMenuSection", "cheats");
            return string.IsNullOrWhiteSpace(v) ? "cheats" : v;
        }
        set => SetString("DevMenuSection", string.IsNullOrWhiteSpace(value) ? "cheats" : value);
    }

    /// <summary>Show FPS + Working Set HUD overlay (top-right).</summary>
    public bool ShowDevHud
    {
        get => GetBool("ShowDevHud");
        set => SetBool("ShowDevHud", value);
    }
}
