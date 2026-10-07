using ImGuiNET;

namespace RecompOne.Runtime.Host.Window;

internal sealed class DebugDevMenuSection : IDevMenuSection
{
    public string Id => "debug";
    public string Title => "Debug";
    public int Order => 30;

    public void Draw()
    {
        ImGuiEx.TextDisabled("Debug tools (also in the F1 menu bar)");
        ImGui.Spacing();

        ImGui.TextUnformatted("GPU");
        TogglePanel<OutputPanel>("Output");
        TogglePanel<VramViewerPanel>("VRAM Viewer");

        ImGui.Spacing();
        ImGui.TextUnformatted("CPU / Memory");
        TogglePanel<CpuStatePanel>("CPU State");
        TogglePanel<RamMapPanel>("RAM Map");
        TogglePanel<MemoryEditorPanel>("Memory Editor");

        ImGui.Spacing();
        ImGui.TextUnformatted("Hardware");
        TogglePanel<SpuViewerPanel>("SPU Viewer");
        TogglePanel<CdDebugPanel>("CD Debug");

        ImGui.Spacing();
        ImGui.TextUnformatted("System");
        TogglePanel<OverlayEventsPanel>("Overlay Events");
        TogglePanel<ConsolePanel>("Console");

        ImGui.Spacing();
        ImGui.TextUnformatted("Frame pacing");
        bool camTrace = FramePacing.CameraTrace;
        if (ImGui.Checkbox("Camera trace", ref camTrace))
            FramePacing.CameraTrace = camTrace;
        ImGuiEx.TextDisabled("One line per frame to logs/camtrace.txt (overwritten each time it is enabled).");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        if (ImGui.Button("Reset View"))
            Config.ConfigManager.ResetView(PanelManager.Panels);
    }

    static void TogglePanel<T>(string label) where T : class, IPanel
    {
        var panel = PanelManager.Get<T>();
        if (panel == null) return;
        bool open = panel.IsOpen;
        if (ImGui.Checkbox(label, ref open))
            panel.IsOpen = open;
    }
}
