using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Config;

namespace RecompOne.Runtime.Host.Window;

internal sealed class RenderingDevMenuSection : IDevMenuSection
{
    public string Id => "rendering";
    public string Title => "Rendering";
    public int Order => 20;

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
        ImGui.Spacing();
        for (int i = 0; i < GraphicsPresets.Labels.Length; i++)
        {
            if (ImGui.Button(GraphicsPresets.Labels[i], new Vector2(-1, 0)))
                GraphicsPresets.Apply(i);
        }
        ImGuiEx.TextDisabled("Live apply, no restart");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        bool widescreen = ConfigManager.View.Widescreen;
        if (ImGui.Checkbox("Widescreen (16:9)", ref widescreen))
        {
            ConfigManager.View.Widescreen = widescreen;
            HostWindow.ApplyWidescreen(widescreen);
            ConfigManager.SaveView(PanelManager.Panels);
        }
        ImGuiEx.TextDisabled("Hack: stretches 4:3");

        bool dedither = ConfigManager.View.Dedither;
        if (ImGui.Checkbox("Dedither", ref dedither))
        {
            ConfigManager.View.Dedither = dedither;
            Hle.GpuHle.Dedither = dedither;
            ConfigManager.SaveView(PanelManager.Panels);
        }

        bool dejitter = ConfigManager.View.Dejitter;
        if (ImGui.Checkbox("Dejitter", ref dejitter))
        {
            ConfigManager.View.Dejitter = dejitter;
            Hle.GpuHle.Dejitter = dejitter;
            ConfigManager.SaveView(PanelManager.Panels);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        bool integer = ConfigManager.View.IntegerScale;
        if (ImGui.Checkbox("Integer scaling", ref integer))
        {
            ConfigManager.View.IntegerScale = integer;
            ConfigManager.SaveView(PanelManager.Panels);
        }
        ImGuiEx.TextDisabled("Sharp upscale, black bars");

        bool nearest = ConfigManager.View.PresentNearest;
        if (ImGui.Checkbox("Crisp pixels (nearest)", ref nearest))
        {
            ConfigManager.View.PresentNearest = nearest;
            Hle.GpuHle.PresentNearest = nearest;
            ConfigManager.SaveView(PanelManager.Panels);
        }
        ImGuiEx.TextDisabled("No blurry upscale");

        bool vsync = ConfigManager.View.VSync;
        if (ImGui.Checkbox("VSync", ref vsync))
        {
            ConfigManager.View.VSync = vsync;
            HostWindow.ApplyVSync(vsync);
            ConfigManager.SaveView(PanelManager.Panels);
        }
        ImGuiEx.TextDisabled("Less tearing, slight lag");

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
        ImGuiEx.TextDisabled("Higher = more VRAM (Engine)");
        if (ConfigManager.View.InternalResolution != Hle.GlVram.Scale)
            ImGuiEx.TextDisabled("Restart required");

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

        ImGuiEx.TextDisabled("Off on menus & cutscenes");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextUnformatted("Panels");
        ImGui.Spacing();
        TogglePanel<OutputPanel>("Output");
        TogglePanel<VramViewerPanel>("VRAM Viewer");
    }

    static void TogglePanel<T>(string label) where T : class, IPanel
    {
        var panel = PanelManager.Get<T>();
        if (panel == null) return;
        bool open = panel.IsOpen;
        if (ImGui.Checkbox(label, ref open))
            panel.IsOpen = open;
    }

    static int IndexOfScale(int scale)
    {
        var opts = ViewConfig.InternalResolutionOptions;
        for (int i = 0; i < opts.Length; i++)
            if (opts[i] == scale) return i;
        return 2;
    }
}
