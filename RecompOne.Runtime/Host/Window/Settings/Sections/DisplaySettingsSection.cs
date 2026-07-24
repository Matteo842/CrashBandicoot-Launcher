using ImGuiNET;
using RecompOne.Runtime.Config;

namespace RecompOne.Runtime.Host.Window;

internal sealed class DisplaySettingsSection : ISettingsSection
{
    public string Id => "display";
    public string Title => "Display";
    public int Order => 5;

    public void Draw()
    {
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

        bool native = ConfigManager.View.NativeResolution;
        if (ImGui.Checkbox("Native resolution", ref native))
        {
            ConfigManager.View.NativeResolution = native;
            Hle.GpuHle.NativeResolution = native;
            ConfigManager.SaveView(PanelManager.Panels);
            NoticePopup.Show("You need to restart the application to apply this configuration");
        }
        if (ConfigManager.View.NativeResolution != (Hle.GlVram.Scale == 1))
            ImGui.TextDisabled("restart is required");
    }
}
