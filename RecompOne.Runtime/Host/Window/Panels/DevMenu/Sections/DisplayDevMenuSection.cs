using ImGuiNET;
using RecompOne.Runtime.Config;

namespace RecompOne.Runtime.Host.Window;

internal sealed class DisplayDevMenuSection : IDevMenuSection
{
    public string Id => "display";
    public string Title => "Display";
    public int Order => 10;

    public void Draw()
    {
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
        ImGuiEx.TextDisabled("Removes dither noise");

        bool dejitter = ConfigManager.View.Dejitter;
        if (ImGui.Checkbox("Dejitter", ref dejitter))
        {
            ConfigManager.View.Dejitter = dejitter;
            Hle.GpuHle.Dejitter = dejitter;
            ConfigManager.SaveView(PanelManager.Panels);
        }
        ImGuiEx.TextDisabled("Less polygon wobble");

        bool showBar = !ConfigManager.View.HideTopBar;
        if (ImGui.Checkbox("Show Menu Bar", ref showBar))
        {
            ConfigManager.View.HideTopBar = !showBar;
            ConfigManager.SaveView(PanelManager.Panels);
        }
        ImGuiEx.TextDisabled("Also F1");
        ImGuiEx.TextDisabled("More options: Rendering");
    }
}
