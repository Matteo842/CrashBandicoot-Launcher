using ImGuiNET;
using RecompOne.Runtime.Config;

namespace RecompOne.Runtime.Host.Window;

internal static class FrameRateSetting
{
    public static void DrawCombo(string id = "FrameRate")
    {
        int idx = ViewConfig.FrameRateToIndex(ConfigManager.View.FrameRate);
        if (ImGui.Combo($"Frame rate##{id}", ref idx, ViewConfig.FrameRateLabels, ViewConfig.FrameRateLabels.Length))
        {
            ConfigManager.View.FrameRate = ViewConfig.FrameRateOptionValues[idx];
            HostWindow.ApplyFramePacing();
            ConfigManager.SaveView(PanelManager.Panels);
        }
        ImGuiEx.TextDisabled("Gameplay only — menus & cutscenes stay 30 FPS.");
        ImGuiEx.TextDisabled("This is a refresh cap. Speed is delta time (60 and 120 play the same).");
        ImGuiEx.TextDisabled("Turn VSync off if 120/240 stay locked to the monitor.");
    }
}
