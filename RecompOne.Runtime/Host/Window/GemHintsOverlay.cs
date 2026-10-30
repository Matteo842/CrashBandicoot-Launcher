using System.Numerics;
using ImGuiNET;

namespace RecompOne.Runtime.Host.Window;

/// <summary>
/// Gem hints (#131) card in the bottom-right corner of the game image: gem icon,
/// logo-style title and short lines (see <see cref="GemHints"/>). Slides in from the
/// right edge when a hint appears.
/// </summary>
internal static class GemHintsOverlay
{
    static bool _visible;
    static float _slide;

    public static void Draw()
    {
        if (!GemHints.TryGet(out var card) || OutputPanel.ImageFrame != ImGui.GetFrameCount())
        {
            _visible = false;
            return;
        }

        float dt = Math.Min(ImGui.GetIO().DeltaTime, 0.1f);
        if (!_visible) _slide = 0f;
        _visible = true;
        _slide = Math.Min(1f, _slide + dt * 7f);

        var min = OutputPanel.HudMin;
        var max = OutputPanel.HudMax;
        float w = max.X - min.X;
        float h = max.Y - min.Y;
        if (h < 120f || w < 160f) return;

        float body = Math.Max(12f, h * 0.026f);
        float title = Math.Max(14f, h * 0.036f);
        float pad = body * 0.75f;
        float gemR = title * 0.42f;
        float textMax = Math.Min(w * 0.42f, body * 26f);

        var lines = new List<(string Text, Vector4 Color)>();
        foreach (var line in card.Lines)
            foreach (string part in Wrap(line.Text, body, textMax))
                lines.Add((part, CrashUi.HintColor(line.Tone)));

        string head = card.Title.ToUpperInvariant();
        var headSize = CrashUi.HudTextSize(head, title);
        float textW = gemR * 2f + body * 0.5f + headSize.X;
        foreach (var (text, _) in lines)
            textW = Math.Max(textW, CrashUi.HudBodySize(text, body).X);
        float lineH = body * 1.25f;
        var size = new Vector2(textW + pad * 2f, pad * 2f + title * 1.15f + body * 0.3f + lineH * lines.Count);

        float margin = h * 0.04f;
        float ease = 1f - MathF.Pow(1f - _slide, 3f);
        var cardMin = new Vector2(max.X - margin - size.X + (1f - ease) * (size.X + margin), max.Y - margin - size.Y);

        ImGui.SetNextWindowPos(min);
        ImGui.SetNextWindowSize(max - min);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        if (ImGui.Begin("##gem-hints", Flags))
        {
            var dl = ImGui.GetWindowDrawList();
            dl.PushClipRect(min, max, true);
            CrashUi.HudPanel(dl, cardMin, cardMin + size, body * 0.6f);

            var gemColor = CrashUi.GemColor(card.Icon);
            float y = cardMin.Y + pad;
            CrashUi.HudGem(dl, new Vector2(cardMin.X + pad + gemR, y + title * 0.55f), gemR, gemColor, card.Owned);
            var titleColor = card.TitleTone == GemHints.Tone.Bad ? CrashUi.HintColor(GemHints.Tone.Bad) : gemColor;
            CrashUi.HudTitle(dl, head, new Vector2(cardMin.X + pad + gemR * 2f + body * 0.5f, y), title, titleColor);
            y += title * 1.15f + body * 0.3f;
            foreach (var (text, color) in lines)
            {
                CrashUi.HudBody(dl, text, new Vector2(cardMin.X + pad, y), body, color);
                y += lineH;
            }
            dl.PopClipRect();
        }
        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    /// <summary>Greedy word wrap to <paramref name="maxWidth"/> pixels.</summary>
    static IEnumerable<string> Wrap(string text, float px, float maxWidth)
    {
        if (CrashUi.HudBodySize(text, px).X <= maxWidth)
        {
            yield return text;
            yield break;
        }
        string line = "";
        foreach (string word in text.Split(' '))
        {
            string next = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && CrashUi.HudBodySize(next, px).X > maxWidth)
            {
                yield return line;
                line = word;
            }
            else
            {
                line = next;
            }
        }
        if (line.Length > 0) yield return line;
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
