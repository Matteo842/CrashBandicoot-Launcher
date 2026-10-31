using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Config;

namespace RecompOne.Runtime.Host.Window;

internal sealed class OutputPanel : IPanel
{
    public string Name => "Output";
    public bool IsOpen { get; set; } = true;

    static uint _texId;
    static int _texW, _texH, _nativeH;
    static float _aspect = 4f / 3f;

    /// <summary>Screen rect of the game image, for overlays drawn on it (box counter).</summary>
    public static Vector2 ImageMin { get; private set; }
    public static Vector2 ImageMax { get; private set; }
    /// <summary>
    /// Rect for HUD overlays: the 16:9 image during widescreen gameplay, even on
    /// frames still presented at 4:3 (level entry), so the HUD does not jump.
    /// </summary>
    public static Vector2 HudMin { get; private set; }
    public static Vector2 HudMax { get; private set; }
    /// <summary>ImGui frame the rect was taken; older = no image this frame.</summary>
    public static int ImageFrame { get; private set; } = -1;

    public static void SetTexture(uint id, int w, int h, float aspect = 0f, int nativeHeight = 0)
        => (_texId, _texW, _texH, _nativeH, _aspect) =
            (id, w, h, nativeHeight > 0 ? nativeHeight : h, aspect > 0f ? aspect : 4f / 3f);

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
        var imageSize = FitAspect(avail, ConfigManager.View.IntegerScale);
        var offset = (avail - imageSize) * 0.5f;
        ImGui.SetCursorPos(ImGui.GetCursorPos() + offset);
        uint tex = _texId;
        if (Hle.GpuHle.CrtFilter && HostWindow.IsPresentTexture(_texId))
            tex = CrtTexture(ref imageSize);
        ImGui.Image((nint)tex, imageSize);
        ImageMin = ImGui.GetItemRectMin();
        ImageMax = ImGui.GetItemRectMax();
        ImageFrame = ImGui.GetFrameCount();
        (HudMin, HudMax) = (ImageMin, ImageMax);
        // The first frames after a loading screen are presented at 4:3 before
        // the side bands exist; the box counter moved from the 4:3 corner to
        // the 16:9 one. Anchor HUD overlays to the 16:9 rect throughout. Read
        // the level live: coming from the map, WideFovActive only turns on at
        // the level's first PutDrawEnv, after the counter already shows.
        float wide = Hle.GpuHle.WideAspect;
        var mem = Runtime.Mem;
        if (wide > _aspect + 0.01f && mem != null
            && !Catalogs.Catalog.Levels.IsUiOrCinema(mem.ReadU32(Catalogs.Catalog.LevelIdAddr)))
        {
            var size = Hle.DisplayLayout.Fit(avail.X, avail.Y, wide, _nativeH, ConfigManager.View.IntegerScale);
            var center = (ImageMin + ImageMax) * 0.5f;
            var half = new Vector2(size.Width, size.Height) * 0.5f;
            (HudMin, HudMax) = (center - half, center + half);
        }
    }

    static uint CrtTexture(ref Vector2 imageSize)
    {
        // The shadow mask needs one shader pixel per screen pixel: snap the image
        // to whole framebuffer pixels and filter at exactly that size.
        var scale = ImGui.GetIO().DisplayFramebufferScale;
        if (scale.X <= 0f || scale.Y <= 0f) scale = Vector2.One;
        int width = Math.Max(1, (int)MathF.Round(imageSize.X * scale.X));
        int height = Math.Max(1, (int)MathF.Round(imageSize.Y * scale.Y));
        var pos = ImGui.GetCursorScreenPos() * scale;
        ImGui.SetCursorScreenPos(new Vector2(MathF.Round(pos.X) / scale.X, MathF.Round(pos.Y) / scale.Y));
        uint tex = HostWindow.RenderCrt(width, height);
        if (tex == 0) return _texId;
        imageSize = new Vector2(width / scale.X, height / scale.Y);
        return tex;
    }

    static Vector2 FitAspect(Vector2 dst, bool integerScale)
    {
        var size = Hle.DisplayLayout.Fit(dst.X, dst.Y, _aspect, _nativeH, integerScale);
        return new Vector2(size.Width, size.Height);
    }
}
