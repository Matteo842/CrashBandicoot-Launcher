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

    /// <summary>
    /// Expand horizontal FOV to 16:9 (side margins). Does not stretch the 4:3 image.
    /// </summary>
    public bool Widescreen
    {
        get => GetBool("Widescreen");
        set => SetBool("Widescreen", value);
    }

    /// <summary>Host hotkey that opens/closes the Cheat menu (Silk Key name, e.g. F3).</summary>
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
}
