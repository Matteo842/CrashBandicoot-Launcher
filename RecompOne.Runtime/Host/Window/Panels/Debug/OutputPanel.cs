using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Config;

namespace RecompOne.Runtime.Host.Window;

internal sealed class OutputPanel : IPanel
{
    public string Name => "Output";
    public bool IsOpen { get; set; } = true;

    static uint _texId;
    static int _texW, _texH;
    static float _aspect = 4f / 3f;

    public static void SetTexture(uint id, int w, int h, float aspect = 0f)
        => (_texId, _texW, _texH, _aspect) = (id, w, h, aspect > 0f ? aspect : 4f / 3f);

    public void Draw()
    {
        // Framebuffer view — never allow closing (X used to persist IsOpen=false).
        IsOpen = true;

        // Fullscreen: cover the viewport with no title / dock tab ("Output" blue bar).
        // Skipping Begin("Output") leaves the dock node empty (passthru), so no chrome.
        if (ConfigManager.View.Fullscreen)
        {
            DrawImmersive();
            return;
        }

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoBringToFrontOnFocus;

        if (!ImGui.Begin(Name, flags))
        {
            ImGui.End();
            return;
        }

        DrawImage();
        ImGui.End();
    }

    static void DrawImmersive()
    {
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(vp.Pos);
        ImGui.SetNextWindowSize(vp.Size);
        const ImGuiWindowFlags fsFlags =
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoBringToFrontOnFocus |
            ImGuiWindowFlags.NoNavFocus |
            ImGuiWindowFlags.NoDocking |
            ImGuiWindowFlags.NoBackground;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        if (!ImGui.Begin("##FullscreenOutput", fsFlags))
        {
            ImGui.End();
            ImGui.PopStyleVar(2);
            return;
        }

        DrawImage();
        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    static void DrawImage()
    {
        if (_texId == 0 || _texW <= 0 || _texH <= 0) return;

        var avail = ImGui.GetContentRegionAvail();
        var imageSize = FitAspect(new Vector2(_aspect, 1f), avail, ConfigManager.View.IntegerScale);
        var offset = (avail - imageSize) * 0.5f;
        ImGui.SetCursorPos(ImGui.GetCursorPos() + offset);
        ImGui.Image((nint)_texId, imageSize);
    }

    static Vector2 FitAspect(Vector2 src, Vector2 dst, bool integerScale)
    {
        if (integerScale && _texW > 0 && _texH > 0)
        {
            // Largest whole-pixel scale that still fits. If the FB is bigger than the
            // window (high internal res), fall back to fractional letterbox — never
            // force 1:1 which overflows and looks like a huge zoom.
            int i = (int)MathF.Floor(MathF.Min(dst.X / _texW, dst.Y / _texH));
            if (i >= 1)
                return new Vector2(_texW * i, _texH * i);
        }

        float scale = MathF.Min(dst.X / src.X, dst.Y / src.Y);
        return src * scale;
    }
}
