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

    /// <summary>
    /// Debug menu bar (Settings / Mods / Debug / Help) hidden. Off by default:
    /// players use Esc and the Developer Menu; F1 shows the bar. Stored as
    /// ShowMenuBar so configs from when the bar was on by default start hidden.
    /// </summary>
    public bool HideTopBar
    {
        get => !GetBool("ShowMenuBar");
        set
        {
            SetBool("ShowMenuBar", !value);
            Values.Remove("HideTopBar");
        }
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

    /// <summary>
    /// Dither pattern sized to PS1 pixels at any internal resolution (original look, #119).
    /// Off: the pattern runs per internal pixel, finer above 1x. No effect with Dedither.
    /// On by default.
    /// </summary>
    public bool ScaledDither
    {
        get => GetBool("ScaledDither", true);
        set => SetBool("ScaledDither", value);
    }

    /// <summary>Keep GTE subpixel screen coords to reduce polygon wobble. Off on menus &amp; cutscenes.</summary>
    public bool Dejitter
    {
        get => GetBool("Dejitter");
        set => SetBool("Dejitter", value);
    }

    /// <summary>
    /// Native 16:9 gameplay: renders additional scenery at the original projection scale
    /// and keeps objects visible in the extra view. Menus, the map, and cutscenes retain 4:3.
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

    /// <summary>
    /// MattiasCRT output filter (#131): curved CRT TV look by Mattias Gustavsson, with
    /// scanlines, glow and a shadow mask. Off by default.
    /// </summary>
    public bool CrtFilter
    {
        get => GetBool("CrtFilter");
        set => SetBool("CrtFilter", value);
    }

    public const int GraphicsApiAuto = 0;
    public const int GraphicsApiGl33 = 1;
    public const int GraphicsApiGles = 2;
    public const int GraphicsApiVulkan = 3;
    public const int GraphicsApiOpenGl = 4;

    /// <summary>
    /// Renderer for the desktop game window (#167, #80). Auto opens Vulkan on
    /// Windows and OpenGL 4.3 elsewhere, then falls back to OpenGL 4.3, 3.3 and
    /// ES 3 when the driver lacks one. The other values are tried first, then the
    /// rest of that list (OpenGL = the OpenGL list without Vulkan). Applies on next start.
    /// </summary>
    public int GraphicsApi
    {
        get => Math.Clamp(GetInt("GraphicsApi", GraphicsApiAuto), GraphicsApiAuto, GraphicsApiOpenGl);
        set => SetInt("GraphicsApi", Math.Clamp(value, GraphicsApiAuto, GraphicsApiOpenGl));
    }

    /// <summary>Whether the desktop window starts on Vulkan (Linux Auto stays on OpenGL until tested there).</summary>
    public bool DesktopWantsVulkan => GraphicsApi == GraphicsApiVulkan
        || GraphicsApi == GraphicsApiAuto && OperatingSystem.IsWindows();

    /// <summary>
    /// Vulkan game renderer on Android (the desktop uses <see cref="GraphicsApi"/>).
    /// Off by default (no gain over OpenGL ES measured on phones); any Vulkan
    /// failure falls back to OpenGL ES. Applies on next game start.
    /// </summary>
    public bool VulkanRenderer
    {
        get => GetBool("VulkanRenderer");
        set => SetBool("VulkanRenderer", value);
    }

    /// <summary>Host swap-interval VSync (reduces tearing; may add input latency).</summary>
    public bool VSync
    {
        get => GetBool("VSync");
        set => SetBool("VSync", value);
    }

    public const int FrameRateOriginal = 0;
    public const int FrameRateUncapped = -1;

    /// <summary>
    /// 25 FPS present cap (#153). Below 30 the unlocked clock clamps every
    /// step to one original 34-tick frame, so gameplay runs the 30 Hz logic
    /// at 25/30 speed, like a 50 Hz PAL TV. Not the PAL disc's own tuning.
    /// Last in the list so FPS keys 1–5 keep their presets.
    /// </summary>
    public const int FrameRatePalSpeed = 25;

    public static readonly int[] FrameRateOptionValues = [0, 60, 120, 240, -1, FrameRatePalSpeed];

    public static readonly string[] FrameRateLabels =
    [
        "Original (30 FPS)",
        "60 FPS",
        "120 FPS",
        "240 FPS",
        "Uncapped",
        "25 FPS (PAL speed)",
    ];

    /// <summary>
    /// 0 = original 30 FPS pad. 60/120/240 = unlocked present cap; sim dt is
    /// wall time (1020 ticks/s) so 60 and 120 play at the same speed. -1 = uncapped.
    /// 25 = PAL speed (see <see cref="FrameRatePalSpeed"/>). Menus stay 30.
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

    /// <summary>First-run "press F3" hint already shown (menu opened or timed out).</summary>
    public bool DevMenuHintSeen
    {
        get => GetBool("DevMenuHintSeen");
        set => SetBool("DevMenuHintSeen", value);
    }

    /// <summary>Show FPS + Working Set HUD overlay (top-right).</summary>
    public bool ShowDevHud
    {
        get => GetBool("ShowDevHud");
        set => SetBool("ShowDevHud", value);
    }

    /// <summary>
    /// HUD shows frame times, renderer and memory under the FPS line (#176).
    /// Off by default: the HUD is a plain FPS counter.
    /// </summary>
    public bool DevHudDetails
    {
        get => GetBool("DevHudDetails");
        set => SetBool("DevHudDetails", value);
    }

    /// <summary>
    /// Pause menu, Developer Menu and Settings drawn in the launcher look (Bungee /
    /// Nunito, Wumpa colors). Off = the plain gray ImGui menus. On by default.
    /// </summary>
    public bool CrashMenuStyle
    {
        get => GetBool("CrashMenuStyle", true);
        set => SetBool("CrashMenuStyle", value);
    }

    /// <summary>
    /// Start opens the recompiled pause menu instead of the game's pause screen, wherever the
    /// game would pause (levels and the island map). Off = the original pause screen; Esc
    /// still opens the menu. On by default.
    /// </summary>
    public bool StartPauseMenu
    {
        get => GetBool("StartPauseMenu", true);
        set => SetBool("StartPauseMenu", value);
    }

    /// <summary>Keys 1–5 switch frame-rate presets during gameplay. On by default.</summary>
    public bool FrameRateHotkeys
    {
        get => GetBool("FrameRateHotkeys", true);
        set => SetBool("FrameRateHotkeys", value);
    }

    /// <summary>
    /// Quick save / quick load (#130): F5 save, F8 load, F6/F7 slot, and the
    /// pause menu entries. Off hides them all. On by default.
    /// </summary>
    public bool SaveStates
    {
        get => GetBool("SaveStates", true);
        set => SetBool("SaveStates", value);
    }

    /// <summary>Selected quick save slot (1–<see cref="Host.SaveStates.SlotCount"/>).</summary>
    public int SaveStateSlot
    {
        get => Math.Clamp(GetInt("SaveStateSlot", 1), 1, Host.SaveStates.SlotCount);
        set => SetInt("SaveStateSlot", Math.Clamp(value, 1, Host.SaveStates.SlotCount));
    }

    /// <summary>
    /// Memory card saves keep the current lives and Load Game restores them (Crash 2/3
    /// style, #21). Off = original game: every load starts with 4 lives. On by default.
    /// </summary>
    public bool SaveLives
    {
        get => GetBool("SaveLives", true);
        set => SetBool("SaveLives", value);
    }

    /// <summary>
    /// Pad 1 left stick steers Crash in any direction (#131), not only the eight
    /// D-pad directions. D-pad and keyboard are unchanged. Off by default.
    /// </summary>
    public bool AnalogMovement
    {
        get => GetBool("AnalogMovement");
        set => SetBool("AnalogMovement", value);
    }

    /// <summary>
    /// Box counter on screen during levels (#131): boxes broken / boxes needed for
    /// the level's gem. Off by default.
    /// </summary>
    public bool BoxCounter
    {
        get => GetBool("BoxCounter");
        set => SetBool("BoxCounter", value);
    }

    /// <summary>
    /// Gem hints (#131): what the selected level's gem needs on the map, a reminder
    /// at level start, the gem an inactive gem platform needs, and a notice when a
    /// death loses the gem. Off by default.
    /// </summary>
    public bool GemHints
    {
        get => GetBool("GemHints");
        set => SetBool("GemHints", value);
    }

    /// <summary>
    /// Japanese level end (#170): after a deathless run with missed boxes, only one
    /// box drops on Crash, and the counter text appears with the full missed count once
    /// that box has hit him (#174). Off by default (NTSC-U: one box per missed box).
    /// </summary>
    public bool SingleBoxDrop
    {
        get => GetBool("SingleBoxDrop");
        set => SetBool("SingleBoxDrop", value);
    }

    /// <summary>
    /// Pity mask (#186): after 3 deaths in a row without a new checkpoint, Crash
    /// respawns with an Aku Aku mask, like Crash 2 and 3. Off by default.
    /// </summary>
    public bool PityMask
    {
        get => GetBool("PityMask");
        set => SetBool("PityMask", value);
    }

    /// <summary>
    /// Story intro on New Game (#171): New Game plays the intro cutscene (retail:
    /// attract demo only) before the map, like Crash 2 and 3. Start skips it. Off by
    /// default.
    /// </summary>
    public bool IntroOnNewGame
    {
        get => GetBool("IntroOnNewGame");
        set => SetBool("IntroOnNewGame", value);
    }
}
