using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Host.Diagnostics;

namespace RecompOne.Runtime.Host.Window;

/// <summary>Optional top-right FPS + Working Set overlay, plus a brief mode toast.</summary>
internal static class DevHudOverlay
{
    const long FlashMs = 1500;
    static string? _flash;
    static long _flashUntil;

    public static void Flash(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        _flash = message;
        _flashUntil = Environment.TickCount64 + FlashMs;
    }

    public static void Draw()
    {
        var vp = ImGui.GetMainViewport();
        float ui = Math.Clamp(ImGui.GetIO().FontGlobalScale, 1f, 2.5f);
        DrawFlash(vp, ui);
        DrawStats(vp, ui);
    }

    static void DrawFlash(ImGuiViewportPtr vp, float ui)
    {
        if (_flash == null || Environment.TickCount64 >= _flashUntil)
        {
            _flash = null;
            return;
        }

        var pad = new Vector2(0f, 16 * ui);
        ImGui.SetNextWindowPos(vp.WorkPos + new Vector2(vp.WorkSize.X * 0.5f, pad.Y), ImGuiCond.Always, new Vector2(0.5f, 0f));
        ImGui.SetNextWindowBgAlpha(0.7f);
        PushHudStyle(ui);
        if (ImGui.Begin("##dev-hud-flash", HudFlags))
            ImGui.TextUnformatted(_flash);
        ImGui.End();
        ImGui.PopStyleVar(3);
    }

    static void DrawStats(ImGuiViewportPtr vp, float ui)
    {
        if (!ConfigManager.View.ShowDevHud) return;

        var pad = new Vector2(12 * ui, 12 * ui);
        ImGui.SetNextWindowPos(vp.WorkPos + new Vector2(vp.WorkSize.X - pad.X, pad.Y), ImGuiCond.Always, new Vector2(1f, 0f));
        ImGui.SetNextWindowBgAlpha(0.55f);
        PushHudStyle(ui);
        if (ImGui.Begin("##dev-hud", HudFlags))
        {
            ImGui.TextUnformatted($"{HostDiagnostics.Fps:0.00} fps");
            ImGui.TextUnformatted($"WS {HostDiagnostics.FormatBytes(HostDiagnostics.WorkingSetBytes)}");
        }
        ImGui.End();
        ImGui.PopStyleVar(3);
    }

    static void PushHudStyle(float ui)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8 * ui, 6 * ui));
    }

    const ImGuiWindowFlags HudFlags =
        ImGuiWindowFlags.NoDecoration |
        ImGuiWindowFlags.AlwaysAutoResize |
        ImGuiWindowFlags.NoSavedSettings |
        ImGuiWindowFlags.NoFocusOnAppearing |
        ImGuiWindowFlags.NoNav |
        ImGuiWindowFlags.NoMove;
}
