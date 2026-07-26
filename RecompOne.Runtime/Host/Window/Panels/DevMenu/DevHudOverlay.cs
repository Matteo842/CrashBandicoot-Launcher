using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Host.Diagnostics;

namespace RecompOne.Runtime.Host.Window;

/// <summary>Optional top-right FPS + Working Set overlay.</summary>
internal static class DevHudOverlay
{
    public static void Draw()
    {
        if (!ConfigManager.View.ShowDevHud) return;

        var vp = ImGui.GetMainViewport();
        float ui = Math.Clamp(ImGui.GetIO().FontGlobalScale, 1f, 2.5f);
        var pad = new Vector2(12 * ui, 12 * ui);
        ImGui.SetNextWindowPos(vp.WorkPos + new Vector2(vp.WorkSize.X - pad.X, pad.Y), ImGuiCond.Always, new Vector2(1f, 0f));
        ImGui.SetNextWindowBgAlpha(0.55f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8 * ui, 6 * ui));

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoMove;

        if (ImGui.Begin("##dev-hud", flags))
        {
            ImGui.TextUnformatted($"{HostDiagnostics.Fps:0.00} fps");
            ImGui.TextUnformatted($"WS {HostDiagnostics.FormatBytes(HostDiagnostics.WorkingSetBytes)}");
        }
        ImGui.End();
        ImGui.PopStyleVar(3);
    }
}
