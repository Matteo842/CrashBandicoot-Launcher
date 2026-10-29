// Icons and theme: glyphs from Windows' own icon font (Segoe MDL2 Assets, segmdl2.ttf),
// drawn centred on their real bounds, plus icon buttons and toggles with tooltips.
// Without the font every icon button falls back to its text label.
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace WideEditor;

static class Icon
{
    // Tools
    public const string Select = "\uE8B0", Vertex = "\uEDFB", Quad = "\uEF20", Triangle = "\uE879",
        Eyedropper = "\uEF3C", Sculpt = "\uEF16", Bridge = "\uEF90";
    // Gizmo
    public const string MoveRotate = "\uECE9", Move = "\uE759", Rotate = "\uE7AD", Scale = "\uE740";
    // File and edit
    public const string Levels = "\uE8F1", Save = "\uE74E", Undo = "\uE7A7", Redo = "\uE7A6", Help = "\uE897",
        Close = "\uE894", Add = "\uE710";
    // Snap and picking
    public const string Snap = "\uE81D", Weld = "\uE71B", Grid = "\uE80A", Original = "\uE7FC", Auto = "\uE945",
        Mine = "\uE77B", Through = "\uEF19", Depth = "\uF158", Behind = "\uE81E";
    // View
    public const string GameView = "\uE7FC", AllView = "\uE8A9", Holes = "\uED5B", Wire = "\uF157", Core = "\uF0E2",
        Eye = "\uE7B3", Trash = "\uE74D", Palette = "\uE790", Globe = "\uE909", Sun = "\uE706", Bright = "\uE793",
        Speed = "\uEC4A";
    // Camera
    public const string Camera = "\uE722", Pin = "\uE707", Prev = "\uE76B", Next = "\uE76C", Path = "\uE7B7",
        Refresh = "\uE72C";
    // Selection
    public const string Deselect = "\uE8E6", Frame = "\uF617", Copy = "\uE8C8", Linked = "\uE71B",
        MirrorH = "\uF1CB", MirrorV = "\uE8CB", Light = "\uE82F", Pencil = "\uE70F";

    public static IEnumerable<char> All() => typeof(Icon).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral).Select(f => ((string)f.GetRawConstantValue()!)[0]).Distinct();
}

sealed partial class Editor
{
    const float IconBake = 26;
    static ImFontPtr _iconFont;
    static bool _hasIcons;

    /// <summary>Called by the ImGui controller before it builds the font texture.</summary>
    static unsafe void LoadIconFont(float ui)
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", "segmdl2.ttf");
        if (!File.Exists(path)) return;
        var io = ImGui.GetIO();
        if (io.Fonts.Fonts.Size == 0) io.Fonts.AddFontDefault();
        // Only the glyphs we use: the whole private area would need a huge atlas.
        var chars = Icon.All().OrderBy(c => c).ToList();
        var ranges = (ushort*)Marshal.AllocHGlobal((chars.Count * 2 + 1) * sizeof(ushort));
        for (int i = 0; i < chars.Count; i++) ranges[2 * i] = ranges[2 * i + 1] = chars[i];
        ranges[chars.Count * 2] = 0;
        var cfg = new ImFontConfigPtr(ImGuiNative.ImFontConfig_ImFontConfig());
        cfg.OversampleH = 2;
        cfg.OversampleV = 2;
        _iconFont = io.Fonts.AddFontFromFileTTF(path, MathF.Round(IconBake * ui), cfg, (IntPtr)ranges);
        cfg.Destroy();
        _hasIcons = _iconFont.NativePtr != null;
    }

    /// <summary>Neutral greys with a blue accent, like Photoshop's dark interface.</summary>
    static void ApplyTheme()
    {
        var style = ImGui.GetStyle();
        style.WindowRounding = 0;
        style.ChildRounding = 3;
        style.FrameRounding = 3;
        style.PopupRounding = 3;
        style.GrabRounding = 3;
        style.TabRounding = 3;
        style.ScrollbarRounding = 6;
        style.WindowBorderSize = 1;
        style.FrameBorderSize = 0;
        style.WindowPadding = new Vector2(10, 10);
        style.FramePadding = new Vector2(8, 5);
        style.ItemSpacing = new Vector2(8, 6);
        style.ItemInnerSpacing = new Vector2(6, 5);
        style.ScrollbarSize = 12;
        style.GrabMinSize = 10;
        style.SeparatorTextBorderSize = 1;
        style.SeparatorTextPadding = new Vector2(10, 4);

        static Vector4 Rgb(int r, int g, int b, float a = 1) => new(r / 255f, g / 255f, b / 255f, a);
        var c = style.Colors;
        var accent = Rgb(38, 128, 235);
        c[(int)ImGuiCol.Text] = Rgb(225, 225, 225);
        c[(int)ImGuiCol.TextDisabled] = Rgb(140, 140, 140);
        c[(int)ImGuiCol.WindowBg] = Rgb(50, 50, 50);
        c[(int)ImGuiCol.ChildBg] = Rgb(44, 44, 44);
        c[(int)ImGuiCol.PopupBg] = Rgb(40, 40, 40, 0.98f);
        c[(int)ImGuiCol.Border] = Rgb(28, 28, 28);
        c[(int)ImGuiCol.BorderShadow] = Rgb(0, 0, 0, 0);
        c[(int)ImGuiCol.FrameBg] = Rgb(36, 36, 36);
        c[(int)ImGuiCol.FrameBgHovered] = Rgb(30, 30, 30);
        c[(int)ImGuiCol.FrameBgActive] = Rgb(26, 26, 26);
        c[(int)ImGuiCol.TitleBg] = Rgb(38, 38, 38);
        c[(int)ImGuiCol.TitleBgActive] = Rgb(38, 38, 38);
        c[(int)ImGuiCol.TitleBgCollapsed] = Rgb(38, 38, 38);
        c[(int)ImGuiCol.MenuBarBg] = Rgb(38, 38, 38);
        c[(int)ImGuiCol.ScrollbarBg] = Rgb(44, 44, 44);
        c[(int)ImGuiCol.ScrollbarGrab] = Rgb(90, 90, 90);
        c[(int)ImGuiCol.ScrollbarGrabHovered] = Rgb(110, 110, 110);
        c[(int)ImGuiCol.ScrollbarGrabActive] = Rgb(130, 130, 130);
        c[(int)ImGuiCol.CheckMark] = Rgb(225, 225, 225);
        c[(int)ImGuiCol.SliderGrab] = Rgb(150, 150, 150);
        c[(int)ImGuiCol.SliderGrabActive] = accent;
        c[(int)ImGuiCol.Button] = Rgb(64, 64, 64);
        c[(int)ImGuiCol.ButtonHovered] = Rgb(80, 80, 80);
        c[(int)ImGuiCol.ButtonActive] = Rgb(96, 96, 96);
        c[(int)ImGuiCol.Header] = Rgb(60, 60, 60);
        c[(int)ImGuiCol.HeaderHovered] = Rgb(72, 72, 72);
        c[(int)ImGuiCol.HeaderActive] = Rgb(84, 84, 84);
        c[(int)ImGuiCol.Separator] = Rgb(30, 30, 30);
        c[(int)ImGuiCol.SeparatorHovered] = accent;
        c[(int)ImGuiCol.SeparatorActive] = accent;
        c[(int)ImGuiCol.ResizeGrip] = Rgb(0, 0, 0, 0);
        c[(int)ImGuiCol.Tab] = Rgb(44, 44, 44);
        c[(int)ImGuiCol.TabHovered] = Rgb(72, 72, 72);
        c[(int)ImGuiCol.TextSelectedBg] = Rgb(38, 128, 235, 0.45f);
        c[(int)ImGuiCol.NavHighlight] = accent;
        c[(int)ImGuiCol.ModalWindowDimBg] = Rgb(0, 0, 0, 0.5f);
    }

    static readonly Vector4 Accent = new(38 / 255f, 128 / 255f, 235 / 255f, 1), AccentHover = new(60 / 255f, 145 / 255f, 245 / 255f, 1);

    /// <summary>Draws a glyph centred on <paramref name="centre"/> using its real ink bounds.</summary>
    static void DrawIcon(ImDrawListPtr dl, string icon, Vector2 centre, float size, uint col)
    {
        if (!_hasIcons) return;
        var g = _iconFont.FindGlyph(icon[0]);
        float k = size / _iconFont.FontSize;
        var ink = new Vector2(g.X0 + g.X1, g.Y0 + g.Y1) / 2 * k;
        var pos = centre - ink;
        dl.AddText(_iconFont, size, new Vector2(MathF.Round(pos.X), MathF.Round(pos.Y)), col, icon);
    }

    static void Tip(string label, string? help)
    {
        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled | ImGuiHoveredFlags.DelayShort)) return;
        ImGui.SetTooltip(string.IsNullOrEmpty(help) ? label : $"{label}\n\n{help}");
    }

    /// <summary>Square button with an icon; <paramref name="label"/> is its tooltip title
    /// (and its text when the icon font is missing). <paramref name="on"/> paints it blue.</summary>
    bool IconButton(string icon, string label, string? help = null, bool on = false, float side = 0, uint? tint = null)
    {
        if (side <= 0) side = ImGui.GetFrameHeight();
        if (on)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, Accent);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AccentHover);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, AccentHover);
        }
        else ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0, 0, 0, 0));
        bool hit;
        if (_hasIcons)
        {
            hit = ImGui.Button($"##{icon}{label}", new Vector2(side));
            DrawIcon(ImGui.GetWindowDrawList(), icon, (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) / 2,
                MathF.Round(side * 0.6f), tint ?? ImGui.GetColorU32(ImGuiCol.Text));
        }
        else hit = ImGui.Button($"{label}##{icon}", new Vector2(0, side));
        ImGui.PopStyleColor(on ? 3 : 1);
        Tip(label, help);
        return hit;
    }

    bool IconToggle(string icon, string label, ref bool value, string? help = null, uint? tint = null)
    {
        if (!IconButton(icon, label, help, value, 0, tint)) return false;
        value = !value;
        return true;
    }

    /// <summary>Normal button with an icon before the text. width -1 = whole row.</summary>
    bool IconTextButton(string icon, string text, string? help = null, float width = 0, bool on = false)
    {
        string shown = text.Split("##")[0];
        if (!_hasIcons)
        {
            bool plain = ImGui.Button(text, new Vector2(width, 0));
            Tip(shown, help);
            return plain;
        }
        var pad = ImGui.GetStyle().FramePadding;
        float h = ImGui.GetFrameHeight(), iconSide = ImGui.GetFontSize() * 1.15f, gap = ImGui.GetStyle().ItemInnerSpacing.X;
        float w = width != 0 ? width : pad.X * 2 + iconSide + gap + ImGui.CalcTextSize(shown).X;
        if (on)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, Accent);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AccentHover);
        }
        bool hit = ImGui.Button($"##{text}", new Vector2(w, h));
        if (on) ImGui.PopStyleColor(2);
        var min = ImGui.GetItemRectMin();
        var dl = ImGui.GetWindowDrawList();
        uint col = ImGui.GetColorU32(ImGuiCol.Text);
        DrawIcon(dl, icon, min + new Vector2(pad.X + iconSide / 2, h / 2), MathF.Round(iconSide), col);
        dl.AddText(min + new Vector2(pad.X + iconSide + gap, pad.Y), col, shown);
        if (help != null) Tip(shown, help);
        return hit;
    }

    /// <summary>Icon used as the label in front of a slider or combo (tooltip on hover).</summary>
    void IconLabel(string icon, string label)
    {
        float h = ImGui.GetFrameHeight();
        if (_hasIcons)
        {
            ImGui.Dummy(new Vector2(h));
            DrawIcon(ImGui.GetWindowDrawList(), icon, (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) / 2,
                MathF.Round(h * 0.6f), ImGui.GetColorU32(ImGuiCol.TextDisabled));
        }
        else
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextDisabled(label);
        }
        Tip(label, null);
        ImGui.SameLine();
    }

    /// <summary>Thin vertical divider between groups of a horizontal toolbar.</summary>
    void BarDivider()
    {
        ImGui.SameLine();
        float h = ImGui.GetFrameHeight();
        var p = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddLine(p + new Vector2(5, 3), p + new Vector2(5, h - 3), Col(0.45f, 0.45f, 0.45f, 0.8f));
        ImGui.Dummy(new Vector2(11, h));
        ImGui.SameLine();
    }

    /// <summary>Collapsing panel header with its icon drawn at the right end.</summary>
    bool PanelHeader(string icon, string label, bool open = true)
    {
        bool shown = ImGui.CollapsingHeader(label, open ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        float h = max.Y - min.Y;
        DrawIcon(ImGui.GetWindowDrawList(), icon, new Vector2(max.X - h / 2 - 4, (min.Y + max.Y) / 2), MathF.Round(h * 0.6f),
            ImGui.GetColorU32(ImGuiCol.TextDisabled));
        return shown;
    }
}
