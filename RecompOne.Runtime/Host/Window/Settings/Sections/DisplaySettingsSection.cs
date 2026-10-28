using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Config;

namespace RecompOne.Runtime.Host.Window;

internal sealed class DisplaySettingsSection : ISettingsSection
{
    public string Id => "display";
    public string Title => "Display";
    public int Order => 5;

    static readonly string[] ResolutionLabels =
    [
        "Native (1x)",
        "2x",
        "4x",
        "8x (4K)",
    ];

    public void Draw()
    {
        ImGui.TextUnformatted("Presets");
        for (int i = 0; i < GraphicsPresets.Labels.Length; i++)
        {
            if (ImGui.Button(GraphicsPresets.Labels[i], new Vector2(-1, 0)))
                GraphicsPresets.Apply(i);
        }
        ImGui.TextDisabled("One-click look — live, no restart");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        bool fullscreen = ConfigManager.View.Fullscreen;
        if (ImGui.Checkbox("Fullscreen", ref fullscreen))
        {
            ConfigManager.View.Fullscreen = fullscreen;
            HostWindow.SetFullscreen(fullscreen);
            ConfigManager.SaveView(PanelManager.Panels);
        }

        bool widescreen = ConfigManager.View.Widescreen;
        if (ImGui.Checkbox("Native widescreen (16:9)", ref widescreen))
        {
            ConfigManager.View.Widescreen = widescreen;
            HostWindow.ApplyWidescreen(widescreen);
            ConfigManager.SaveView(PanelManager.Panels);
        }
        ImGui.TextDisabled("Native 16:9 gameplay with a wider view.");
        ImGui.TextDisabled("Crates and other objects stay visible in the extra view.");
        ImGui.TextDisabled("Menus, map, and cutscenes stay 4:3.");

        bool integer = ConfigManager.View.IntegerScale;
        if (ImGui.Checkbox("Integer scaling", ref integer))
        {
            ConfigManager.View.IntegerScale = integer;
            ConfigManager.SaveView(PanelManager.Panels);
        }
        ImGui.TextDisabled("Whole-pixel upscale — sharp, with black bars");

        bool nearest = ConfigManager.View.PresentNearest;
        if (ImGui.Checkbox("Crisp pixels (nearest)", ref nearest))
        {
            ConfigManager.View.PresentNearest = nearest;
            Hle.GpuHle.PresentNearest = nearest;
            ConfigManager.SaveView(PanelManager.Panels);
        }

        bool crt = ConfigManager.View.CrtFilter;
        if (ImGui.Checkbox("CRT filter (MattiasCRT)", ref crt))
        {
            ConfigManager.View.CrtFilter = crt;
            Hle.GpuHle.CrtFilter = crt;
            ConfigManager.SaveView(PanelManager.Panels);
        }
        ImGui.TextDisabled("Curved CRT TV look by Mattias Gustavsson");

        bool boxes = ConfigManager.View.BoxCounter;
        if (ImGui.Checkbox("Box counter", ref boxes))
        {
            ConfigManager.View.BoxCounter = boxes;
            ConfigManager.SaveView(PanelManager.Panels);
        }
        ImGui.TextDisabled("Boxes broken / boxes needed for the gem, in the corner");

        bool gemHints = ConfigManager.View.GemHints;
        if (ImGui.Checkbox("Gem hints", ref gemHints))
        {
            ConfigManager.View.GemHints = gemHints;
            ConfigManager.SaveView(PanelManager.Panels);
        }
        ImGui.TextDisabled("What each gem needs: map card, level start, gem platforms");

        bool singleDrop = ConfigManager.View.SingleBoxDrop;
        if (ImGui.Checkbox("Japanese box drop (one box)", ref singleDrop))
        {
            ConfigManager.View.SingleBoxDrop = singleDrop;
            ConfigManager.SaveView(PanelManager.Panels);
        }
        ImGui.TextDisabled("Level end: one box drops for all missed boxes, like NTSC-J");

        bool intro = ConfigManager.View.IntroOnNewGame;
        if (ImGui.Checkbox("Story intro on New Game", ref intro))
        {
            ConfigManager.View.IntroOnNewGame = intro;
            ConfigManager.SaveView(PanelManager.Panels);
        }
        ImGui.TextDisabled("New Game plays the story cutscene first, like Crash 2 and 3");

        bool vsync = ConfigManager.View.VSync;
        if (ImGui.Checkbox("VSync", ref vsync))
        {
            ConfigManager.View.VSync = vsync;
            HostWindow.ApplyVSync(vsync);
            ConfigManager.SaveView(PanelManager.Panels);
        }

        ImGui.Spacing();
        FrameRateSetting.DrawCombo("display");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        int scale = ConfigManager.View.InternalResolution;
        int idx = IndexOfScale(scale);
        if (ImGui.Combo("Internal resolution", ref idx, ResolutionLabels, ResolutionLabels.Length))
        {
            int next = ViewConfig.InternalResolutionOptions[idx];
            ConfigManager.View.InternalResolution = next;
            Hle.GpuHle.NativeResolution = next <= 1;
            ConfigManager.SaveView(PanelManager.Panels);
            NoticePopup.Show("You need to restart the application to apply this configuration");
        }
        ImGui.TextDisabled("Higher scales look sharper on 1440p/4K");
        if (ConfigManager.View.InternalResolution != Hle.GlVram.Scale)
            ImGui.TextDisabled("restart is required");

        int filter = ConfigManager.View.TextureFilter;
        if (ImGui.Combo("Texture filter", ref filter, ViewConfig.TextureFilterLabels, ViewConfig.TextureFilterLabels.Length))
        {
            ConfigManager.View.TextureFilter = filter;
            Hle.GpuHle.TextureFilter = filter;
            ConfigManager.SaveView(PanelManager.Panels);
        }

        if (ConfigManager.View.TextureFilter != ViewConfig.TextureFilterOff)
        {
            float pct = ConfigManager.View.TextureFilterStrength * 100f;
            if (ImGui.SliderFloat("Filter strength", ref pct, 0f, 100f, "%.0f%%"))
            {
                float strength = Math.Clamp(pct / 100f, 0f, 1f);
                ConfigManager.View.TextureFilterStrength = strength;
                Hle.GpuHle.TextureFilterStrength = strength;
                ConfigManager.SaveView(PanelManager.Panels);
            }
        }

        bool dedither = ConfigManager.View.Dedither;
        if (ImGui.Checkbox("Dedither", ref dedither))
        {
            ConfigManager.View.Dedither = dedither;
            Hle.GpuHle.Dedither = dedither;
            ConfigManager.SaveView(PanelManager.Panels);
        }
        ImGui.TextDisabled("Removes PS1 dither noise");

        ImGui.BeginDisabled(dedither);
        bool hiResDither = !ConfigManager.View.ScaledDither;
        if (ImGui.Checkbox("High-res dither", ref hiResDither))
        {
            ConfigManager.View.ScaledDither = !hiResDither;
            Hle.GpuHle.ScaledDither = !hiResDither;
            ConfigManager.SaveView(PanelManager.Panels);
        }
        ImGui.EndDisabled();
        ImGui.TextDisabled("Finer dither dots above 1x; off = PS1-size dots");

        bool dejitter = ConfigManager.View.Dejitter;
        if (ImGui.Checkbox("Dejitter", ref dejitter))
        {
            ConfigManager.View.Dejitter = dejitter;
            Hle.GpuHle.Dejitter = dejitter;
            ConfigManager.SaveView(PanelManager.Panels);
        }
        ImGui.TextDisabled("Reduces polygon wobble (GTE subpixel)");

        ImGui.TextDisabled("Filters off on menus & cutscenes; dedither applies everywhere");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        bool crashMenus = ConfigManager.View.CrashMenuStyle;
        if (ImGui.Checkbox("Crash menu style", ref crashMenus))
        {
            ConfigManager.View.CrashMenuStyle = crashMenus;
            ConfigManager.SaveView(PanelManager.Panels);
        }
        ImGui.TextDisabled("Launcher look for pause, Dev Menu and Settings; off = plain gray");

        bool startPause = ConfigManager.View.StartPauseMenu;
        if (ImGui.Checkbox("Pause menu on Start", ref startPause))
        {
            ConfigManager.View.StartPauseMenu = startPause;
            ConfigManager.SaveView(PanelManager.Panels);
        }
        ImGui.TextDisabled("Start opens this menu in levels and on the map; off = original pause");
        ImGui.TextDisabled("screen. Esc opens this menu either way.");
    }

    static int IndexOfScale(int scale)
    {
        var opts = ViewConfig.InternalResolutionOptions;
        for (int i = 0; i < opts.Length; i++)
            if (opts[i] == scale) return i;
        return 2; // 4x
    }
}
