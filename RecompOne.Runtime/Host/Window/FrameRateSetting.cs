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
        ImGuiEx.TextDisabled("Gameplay, bonus, saves and level end. Title and map menus stay 30 FPS.");
        if (ConfigManager.View.FrameRateHotkeys)
            ImGuiEx.TextDisabled("Keys 1-5: original, 60, 120, 240, uncapped.");
        ImGuiEx.TextDisabled("This is a refresh cap. Speed is delta time (60 and 120 play the same).");
        ImGuiEx.TextDisabled("PAL speed: original game at 25 FPS, 17% slower, like a 50 Hz PAL TV.");
        ImGuiEx.TextDisabled("Turn VSync off if 120/240 stay locked to the monitor.");
    }

    /// <summary>Apply a <see cref="ViewConfig.FrameRateOptionValues"/> index. No-op if already set.</summary>
    public static bool TryApplyIndex(int index)
    {
        if ((uint)index >= (uint)ViewConfig.FrameRateOptionValues.Length)
            return false;
        int rate = ViewConfig.FrameRateOptionValues[index];
        if (ConfigManager.View.FrameRate == rate)
            return false;
        ConfigManager.View.FrameRate = rate;
        HostWindow.ApplyFramePacing();
        ConfigManager.SaveView(PanelManager.Panels);
        return true;
    }
}
