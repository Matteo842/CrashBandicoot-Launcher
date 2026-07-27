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
        if (ImGui.Checkbox("Widescreen (16:9)", ref widescreen))
        {
            ConfigManager.View.Widescreen = widescreen;
            HostWindow.ApplyWidescreen(widescreen);
            ConfigManager.SaveView(PanelManager.Panels);
        }
        ImGui.TextDisabled("Expands the view — does not stretch");

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

        bool vsync = ConfigManager.View.VSync;
        if (ImGui.Checkbox("VSync", ref vsync))
        {
            ConfigManager.View.VSync = vsync;
            HostWindow.ApplyVSync(vsync);
            ConfigManager.SaveView(PanelManager.Panels);
        }

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

        bool dejitter = ConfigManager.View.Dejitter;
        if (ImGui.Checkbox("Dejitter", ref dejitter))
        {
            ConfigManager.View.Dejitter = dejitter;
            Hle.GpuHle.Dejitter = dejitter;
            ConfigManager.SaveView(PanelManager.Panels);
        }
        ImGui.TextDisabled("Reduces polygon wobble (GTE subpixel)");

        ImGui.TextDisabled("Filters auto-off on menus & cutscenes");
    }

    static int IndexOfScale(int scale)
    {
        var opts = ViewConfig.InternalResolutionOptions;
        for (int i = 0; i < opts.Length; i++)
            if (opts[i] == scale) return i;
        return 2; // 4x
    }
}
