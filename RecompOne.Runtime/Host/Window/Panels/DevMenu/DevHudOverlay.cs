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
            ImGui.TextUnformatted($"{HostDiagnostics.DrawnFps:0.00} drawn/s");
            DrawFrameTimes(ui);
            ImGui.TextUnformatted($"WS {HostDiagnostics.FormatBytes(HostDiagnostics.WorkingSetBytes)}");
        }
        ImGui.End();
        ImGui.PopStyleVar(3);
    }

    /// <summary>Average ms per phase over the last 0.5 s, plus the slowest frame (hitches).</summary>
    static void DrawFrameTimes(float ui)
    {
        float col = 64 * ui;
        void Row(string label, double ms)
        {
            ImGui.TextUnformatted(label);
            ImGui.SameLine(col);
            ImGui.TextUnformatted($"{ms,6:0.00} ms");
        }

        Row("frame", FrameTimer.AverageFrameMs);
        Row("worst", FrameTimer.WorstFrameMs);
        // Held 5 s: biggest hitch and how much of it was JIT / GC.
        Row("peak 5s", FrameTimer.PeakFrameMs);
        Row("  jit", FrameTimer.PeakJitMs);
        Row("  gc", FrameTimer.PeakGcMs);
        ImGui.Separator();
        Row("game", FrameTimer.Average(FrameTimer.Phase.Game));
        Row("events", FrameTimer.Average(FrameTimer.Phase.Events));
        Row("render", FrameTimer.Average(FrameTimer.Phase.Render));
        Row("ui", FrameTimer.Average(FrameTimer.Phase.Ui));
        Row("swap", FrameTimer.Average(FrameTimer.Phase.Swap));
        Row("wait", FrameTimer.Average(FrameTimer.Phase.Wait));
        ImGui.Separator();
        // Batches = GL draw submissions; cpu reads stall the game until the GPU catches up.
        if (Hle.GpuHle.Backend is Hle.GlBackend gl)
        {
            ImGui.TextUnformatted($"batches {gl.LastFrameFlushes}  verts {gl.LastFrameVertices}");
            ImGui.TextUnformatted($"cpu reads {gl.LastFrameCpuReads}  rt copies {gl.LastFrameWritebacks}");
            ImGui.Separator();
        }
        ImGui.TextUnformatted($"jit warmup {JitWarmup.Status}");
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
