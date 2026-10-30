using System.Numerics;
using ImGuiNET;

namespace RecompOne.Runtime.Host.Window;

/// <summary>
/// Box counter (#131) in the bottom-left corner of the game image: a crate and
/// "broken/total" in the logo style. Blue when the gem is earned, gray after a death
/// (see <see cref="BoxCounter.Reading.Lost"/>).
/// </summary>
internal static class BoxCounterOverlay
{
    static int _shown = -1;
    static float _bump;

    public static void Draw()
    {
        if (!BoxCounter.TryRead(out var r) || OutputPanel.ImageFrame != ImGui.GetFrameCount())
        {
            _shown = -1;
            return;
        }

        // Small pop when a box breaks, like the game's own HUD counters.
        float dt = Math.Min(ImGui.GetIO().DeltaTime, 0.1f);
        if (_shown >= 0 && r.Broken > _shown) _bump = 1f;
        _shown = r.Broken;
        _bump = Math.Max(0f, _bump - dt * 6f);

        var min = OutputPanel.HudMin;
        var max = OutputPanel.HudMax;
        float h = max.Y - min.Y;
        if (h < 32f) return;

        ImGui.SetNextWindowPos(min);
        ImGui.SetNextWindowSize(max - min);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        if (ImGui.Begin("##box-counter", Flags))
        {
            var dl = ImGui.GetWindowDrawList();
            float px = Math.Max(16f, h * 0.055f) * (1f + 0.18f * _bump);
            float crate = px * 0.9f;
            float margin = h * 0.05f;
            string text = $"{r.Broken}/{r.Total}";
            var size = CrashUi.HudTextSize(text, px);
            float baseY = max.Y - margin;
            var cratePos = new Vector2(min.X + margin, baseY - crate);
            CrashUi.HudCrate(dl, cratePos, crate, r.Lost);
            var tone = r.Lost ? CrashUi.HudTone.Lost
                : r.Complete ? CrashUi.HudTone.Gem
                : CrashUi.HudTone.Wumpa;
            CrashUi.HudText(dl, text,
                new Vector2(cratePos.X + crate + px * 0.3f, baseY - crate * 0.5f - size.Y * 0.5f), px, tone);
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    const ImGuiWindowFlags Flags =
        ImGuiWindowFlags.NoDecoration |
        ImGuiWindowFlags.NoInputs |
        ImGuiWindowFlags.NoBackground |
        ImGuiWindowFlags.NoSavedSettings |
        ImGuiWindowFlags.NoFocusOnAppearing |
        ImGuiWindowFlags.NoNav |
        ImGuiWindowFlags.NoDocking |
        ImGuiWindowFlags.NoMove;
}
