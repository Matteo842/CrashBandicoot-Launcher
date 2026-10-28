using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Config;

namespace RecompOne.Runtime.Host.Window;

/// <summary>
/// Launcher look for the in-game menus (pause, Developer Menu, Settings): Bungee rows,
/// Nunito body text and the Wumpa / Sand palette of the launcher. Without the launcher
/// fonts (other hosts) the same colors are drawn with ImGui's font.
/// <see cref="ViewConfig.CrashMenuStyle"/> off = the plain gray menus.
/// </summary>
internal static class CrashUi
{
    public static readonly Vector4 Night = Rgb(6, 16, 24);
    public static readonly Vector4 CardTop = Rgb(22, 51, 37);
    public static readonly Vector4 CardBottom = Rgb(12, 22, 24);
    public static readonly Vector4 Wumpa = Rgb(255, 138, 0);
    public static readonly Vector4 WumpaHot = Rgb(255, 176, 32);
    public static readonly Vector4 Sand = Rgb(244, 228, 188);
    public static readonly Vector4 Muted = Rgb(176, 166, 140);
    public static readonly Vector4 Disabled = Rgb(122, 117, 104, 0.6f);
    public static readonly Vector4 CrateCore = Rgb(58, 32, 8);
    public static readonly Vector4 MarkRed = Rgb(224, 24, 24);
    static readonly Vector4 Leaf = Rgb(86, 168, 52);

    // Fonts are baked once; ≥2x bakes stay sharp up to the 2.25x UI scale.
    const float BodyPx = 14f;
    const float BodyBake = 28f;
    const float DisplaySmallBake = 40f;
    const float DisplayLargeBake = 72f;

    static ImFontPtr? _body, _bodyBold, _displaySmall, _displayLarge;
    static readonly Dictionary<string, float> _hot = new();

    public static bool Enabled => ConfigManager.View.CrashMenuStyle;

    /// <summary>
    /// Adds the launcher fonts (Ui/fonts next to the exe) to the atlas. Must run before
    /// the atlas is built (ImGuiController onConfigureIO). ImGui's font stays the default.
    /// </summary>
    public static void LoadFonts()
    {
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "Ui", "fonts");
            string bungee = Path.Combine(dir, "Bungee-Regular.ttf");
            string semi = Path.Combine(dir, "Nunito-SemiBold.ttf");
            string extra = Path.Combine(dir, "Nunito-ExtraBold.ttf");
            if (!File.Exists(bungee) && !File.Exists(semi)) return;

            var fonts = ImGui.GetIO().Fonts;
            fonts.AddFontDefault(); // first font = default for the debug panels
            _body = Add(fonts, semi, BodyBake, BodyPx, oversample: 2);
            _bodyBold = Add(fonts, extra, BodyBake, BodyPx, oversample: 2);
            _displaySmall = Add(fonts, bungee, DisplaySmallBake, DisplaySmallBake, oversample: 1);
            _displayLarge = Add(fonts, bungee, DisplayLargeBake, DisplayLargeBake, oversample: 1);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[CrashUi] launcher fonts not loaded: {e.Message}");
        }
    }

    static unsafe ImFontPtr? Add(ImFontAtlasPtr fonts, string path, float bakePx, float drawPx, int oversample)
    {
        if (!File.Exists(path)) return null;
        var cfg = new ImFontConfigPtr(ImGuiNative.ImFontConfig_ImFontConfig());
        cfg.OversampleH = oversample;
        cfg.OversampleV = 1;
        var font = fonts.AddFontFromFileTTF(path, bakePx, cfg);
        cfg.Destroy();
        if (font.NativePtr == null) return null;
        // Widgets size text as FontSize * Scale * FontGlobalScale.
        font.Scale = drawPx / bakePx;
        return font;
    }

    static ImFontPtr Display(float px)
    {
        if (px <= DisplaySmallBake && _displaySmall is { } small) return small;
        return _displayLarge ?? _displaySmall ?? ImGui.GetFont();
    }

    static ImFontPtr Body => _body ?? ImGui.GetFont();
    static ImFontPtr BodyBold => _bodyBold ?? _body ?? ImGui.GetFont();

    /// <summary>Pops whatever <see cref="Theme"/> / <see cref="Classic"/> pushed. Dispose after End().</summary>
    public readonly struct Scope(int colors, int vars, bool font) : IDisposable
    {
        public void Dispose()
        {
            if (font) ImGui.PopFont();
            if (colors > 0) ImGui.PopStyleColor(colors);
            if (vars > 0) ImGui.PopStyleVar(vars);
        }
    }

    /// <summary>Crash style for a menu card, or the old gray chrome when the style is off.</summary>
    public static Scope Card(float ui, Vector2 classicPadding, float classicAlpha, float bgAlpha = 0.96f)
        => Enabled ? Theme(ui, card: true, bgAlpha) : Classic(classicPadding, classicAlpha);

    /// <summary>Crash colors, rounding and body font for one window and its popups.</summary>
    public static Scope Theme(float ui, bool card, float bgAlpha = 0.96f)
    {
        int c = 0, v = 0;
        void C(ImGuiCol col, Vector4 value) { ImGui.PushStyleColor(col, value); c++; }
        void F(ImGuiStyleVar sv, float value) { ImGui.PushStyleVar(sv, value); v++; }
        void V(ImGuiStyleVar sv, Vector2 value) { ImGui.PushStyleVar(sv, value); v++; }

        C(ImGuiCol.WindowBg, A(CardBottom, bgAlpha));
        C(ImGuiCol.ChildBg, Vector4.Zero);
        C(ImGuiCol.PopupBg, A(Night, 0.98f));
        C(ImGuiCol.Border, A(Wumpa, 0.4f));
        C(ImGuiCol.BorderShadow, Vector4.Zero);
        C(ImGuiCol.Text, Sand);
        C(ImGuiCol.TextDisabled, Muted);
        C(ImGuiCol.TextSelectedBg, A(Wumpa, 0.35f));
        C(ImGuiCol.TitleBg, Night);
        C(ImGuiCol.TitleBgActive, CardTop);
        C(ImGuiCol.TitleBgCollapsed, Night);
        C(ImGuiCol.FrameBg, A(Sand, 0.07f));
        C(ImGuiCol.FrameBgHovered, A(Wumpa, 0.18f));
        C(ImGuiCol.FrameBgActive, A(Wumpa, 0.28f));
        C(ImGuiCol.CheckMark, WumpaHot);
        C(ImGuiCol.SliderGrab, Wumpa);
        C(ImGuiCol.SliderGrabActive, WumpaHot);
        C(ImGuiCol.Button, A(Wumpa, 0.18f));
        C(ImGuiCol.ButtonHovered, A(Wumpa, 0.38f));
        C(ImGuiCol.ButtonActive, A(Wumpa, 0.55f));
        C(ImGuiCol.Header, A(Wumpa, 0.2f));
        C(ImGuiCol.HeaderHovered, A(Wumpa, 0.32f));
        C(ImGuiCol.HeaderActive, A(Wumpa, 0.45f));
        C(ImGuiCol.Separator, A(Sand, 0.14f));
        C(ImGuiCol.ScrollbarBg, Vector4.Zero);
        C(ImGuiCol.ScrollbarGrab, A(Wumpa, 0.35f));
        C(ImGuiCol.ScrollbarGrabHovered, A(Wumpa, 0.55f));
        C(ImGuiCol.ScrollbarGrabActive, A(WumpaHot, 0.7f));
        C(ImGuiCol.ResizeGrip, A(Wumpa, 0.2f));
        C(ImGuiCol.ResizeGripHovered, A(Wumpa, 0.45f));
        C(ImGuiCol.ResizeGripActive, A(Wumpa, 0.7f));
        C(ImGuiCol.TableHeaderBg, A(Wumpa, 0.15f));
        C(ImGuiCol.TableBorderStrong, A(Sand, 0.15f));
        C(ImGuiCol.TableBorderLight, A(Sand, 0.08f));
        C(ImGuiCol.TableRowBg, Vector4.Zero);
        C(ImGuiCol.TableRowBgAlt, A(Sand, 0.03f));
        C(ImGuiCol.NavHighlight, A(WumpaHot, 0.5f));
        // Inner tab bars (Input: Keyboard/Gamepad, Pad 1/2) were stock ImGui blue.
        C(ImGuiCol.Tab, A(Sand, 0.07f));
        C(ImGuiCol.TabHovered, A(Wumpa, 0.38f));
        C(ImGuiCol.TabActive, A(Wumpa, 0.55f));
        C(ImGuiCol.TabUnfocused, A(Sand, 0.07f));
        C(ImGuiCol.TabUnfocusedActive, A(Wumpa, 0.45f));

        F(ImGuiStyleVar.WindowRounding, (card ? 10f : 8f) * ui);
        F(ImGuiStyleVar.ChildRounding, 6f * ui);
        F(ImGuiStyleVar.FrameRounding, 5f * ui);
        F(ImGuiStyleVar.PopupRounding, 8f * ui);
        F(ImGuiStyleVar.GrabRounding, 5f * ui);
        F(ImGuiStyleVar.ScrollbarRounding, 6f * ui);
        F(ImGuiStyleVar.WindowBorderSize, 1.5f);
        if (card)
        {
            V(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f) * ui);
            V(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 4f) * ui);
            V(ImGuiStyleVar.FramePadding, new Vector2(6f, 3f) * ui);
            // Thin scrollbar; only scrolling child regions show one (cards use NoScrollbar).
            F(ImGuiStyleVar.ScrollbarSize, 8f * ui);
        }

        bool font = _body.HasValue;
        if (font) ImGui.PushFont(_body!.Value);
        return new Scope(c, v, font);
    }

    /// <summary>The pre-2.1 gray book chrome.</summary>
    public static Scope Classic(Vector2 padding, float bgAlpha)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, padding);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.14f, 0.14f, 0.14f, bgAlpha));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.5f, 0.5f, 0.5f, 0.45f));
        ImGui.PushStyleColor(ImGuiCol.TitleBg, new Vector4(0.16f, 0.16f, 0.16f, Math.Min(1f, bgAlpha + 0.05f)));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, new Vector4(0.18f, 0.18f, 0.18f, Math.Min(1f, bgAlpha + 0.05f)));
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.28f, 0.28f, 0.28f, 0.55f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.34f, 0.34f, 0.3f, 0.75f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0.4f, 0.38f, 0.22f, 0.85f));
        return new Scope(7, 3, false);
    }

    /// <summary>Bold body font for small UI outside a themed window (menu bar). Pair with PopFont.</summary>
    public static bool PushBodyBold()
    {
        if (!_bodyBold.HasValue) return false;
        ImGui.PushFont(_bodyBold.Value);
        return true;
    }

    /// <summary>Full-screen launcher gradient behind a modal card (old style: flat dim).</summary>
    public static void Backdrop(string id, ImGuiViewportPtr vp, float ui)
    {
        ImGui.SetNextWindowPos(vp.Pos);
        ImGui.SetNextWindowSize(vp.Size);
        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoInputs
            | ImGuiWindowFlags.NoBringToFrontOnFocus;
        bool crash = Enabled;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, crash ? Vector4.Zero : new Vector4(0f, 0f, 0f, 0.45f));
        if (ImGui.Begin(id, flags) && crash)
        {
            var dl = ImGui.GetWindowDrawList();
            var a = vp.Pos;
            var b = vp.Pos + vp.Size;
            // Launcher background: jungle green top-right, warm glow bottom-left.
            dl.AddRectFilledMultiColor(a, b,
                U32(Night, 0.55f),
                U32(Rgb(10, 58, 34), 0.5f),
                U32(Night, 0.7f),
                U32(Rgb(70, 30, 6), 0.55f));
            Stripes(dl, a, b, 0.025f, 22f * ui, 7f * ui);
        }
        ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
    }

    /// <summary>Header glow and diagonal stripes on the current card. Call right after Begin.</summary>
    public static void CardDecor(float ui)
    {
        if (!Enabled) return;
        var dl = ImGui.GetWindowDrawList();
        var a = ImGui.GetWindowPos();
        var b = a + ImGui.GetWindowSize();
        float r = ImGui.GetStyle().WindowRounding;
        const float inset = 1.5f; // keep the Wumpa border visible
        float band = Math.Min(72f * ui, b.Y - a.Y);
        // Follow the card opacity so a see-through Dev Menu stays see-through.
        var glow = A(CardTop, Math.Min(0.95f, ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg].W));
        dl.AddRectFilled(a + new Vector2(inset), new Vector2(b.X - inset, a.Y + r),
            U32(glow), Math.Max(0f, r - inset), ImDrawFlags.RoundCornersTop);
        dl.AddRectFilledMultiColor(new Vector2(a.X + inset, a.Y + r), new Vector2(b.X - inset, a.Y + band),
            U32(glow), U32(glow), U32(glow with { W = 0f }), U32(glow with { W = 0f }));
        Stripes(dl, a + new Vector2(r * 0.5f), b - new Vector2(r * 0.5f), 0.022f, 18f * ui, 5f * ui);
    }

    static void Stripes(ImDrawListPtr dl, Vector2 a, Vector2 b, float alpha, float step, float thick)
    {
        uint col = U32(Sand, alpha);
        float h = b.Y - a.Y;
        dl.PushClipRect(a, b, true);
        for (float x = a.X - h; x < b.X; x += step)
            dl.AddLine(new Vector2(x, b.Y), new Vector2(x + h, a.Y), col, thick);
        dl.PopClipRect();
    }

    /// <summary>
    /// Red kicker, big outlined Wumpa title and accent bar (old style: gray caption +
    /// separator). True when the optional close button was clicked.
    /// </summary>
    public static bool Header(string kicker, string title, float ui, bool closeButton = false, float titlePx = 30f)
    {
        if (!Enabled)
        {
            ImGuiEx.TextDisabled(title.ToUpperInvariant());
            ImGui.Separator();
            ImGui.Spacing();
            return false;
        }

        var dl = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;
        float s = titlePx / 30f;
        float bs = 26f * Math.Max(0.8f, s) * ui;
        float y = start.Y;

        if (kicker.Length > 0)
        {
            float k = Math.Max(10f, 12f * s) * ui;
            ShadowText(dl, Display(k), k, new Vector2(start.X, y), MarkRed, kicker.ToUpperInvariant(), ui);
            y += k + 2f * ui;
        }

        // Shrink long titles ("WARP TO LEVEL") to the card width.
        string text = title.ToUpperInvariant();
        float size = titlePx * ui;
        float room = w - (closeButton ? bs + 6f * ui : 0f);
        float tw = Display(size).CalcTextSizeA(size, float.MaxValue, 0f, text).X;
        if (tw > room && tw > 0f) size *= room / tw;
        OutlinedTitle(dl, Display(size), size, new Vector2(start.X, y), text, ui);
        y += size + 5f * ui;

        dl.AddRectFilled(new Vector2(start.X, y), new Vector2(start.X + 40f * ui, y + 3f * ui), U32(Wumpa), 1.5f * ui);
        dl.AddRectFilledMultiColor(
            new Vector2(start.X + 46f * ui, y + 1f * ui), new Vector2(start.X + w, y + 2f * ui),
            U32(Wumpa, 0.45f), U32(Wumpa, 0f), U32(Wumpa, 0f), U32(Wumpa, 0.45f));
        y += 9f * ui;
        ImGui.Dummy(new Vector2(w, y - start.Y));
        if (!closeButton) return false;

        var after = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(new Vector2(start.X + w - bs, start.Y));
        bool close = ImGui.InvisibleButton("##crash-close", new Vector2(bs, bs));
        bool hot = ImGui.IsItemHovered();
        var c = ImGui.GetItemRectMin() + new Vector2(bs * 0.5f);
        dl.AddCircleFilled(c, bs * 0.5f, hot ? U32(Wumpa, 0.4f) : U32(Sand, 0.07f));
        float d = bs * 0.2f;
        uint xc = U32(hot ? WumpaHot : Sand);
        dl.AddLine(c - new Vector2(d, d), c + new Vector2(d, d), xc, 2.5f * ui);
        dl.AddLine(c + new Vector2(-d, d), c + new Vector2(d, -d), xc, 2.5f * ui);
        ImGui.SetCursorScreenPos(after);
        return close;
    }

    /// <summary>Gutter icon of a <see cref="Row"/>; None = the Wumpa cursor when hot.</summary>
    public enum RowIcon { None, Resume, SaveState, LoadState, Settings, Map, Leave }

    /// <summary>
    /// Launcher menu row: Bungee label, Wumpa cursor and slide-in when hot. Old style:
    /// gray selectable with a yellow ">" gutter. <paramref name="tag"/> = small right text.
    /// With an <paramref name="icon"/> the gutter always shows it, dark gray until hot.
    /// </summary>
    public static bool Row(string label, string id, float ui, bool enabled = true, string? tag = null, float px = 20f,
        RowIcon icon = RowIcon.None)
    {
        if (!Enabled) return ClassicRow(tag == null ? label : $"{label} ({tag})", id, enabled);

        float size = px * ui;
        float h = size * 1.5f;
        float w = Math.Max(1f, ImGui.GetContentRegionAvail().X);
        if (!enabled) ImGui.BeginDisabled();
        bool clicked = ImGui.InvisibleButton("##row-" + id, new Vector2(w, h));
        bool hot = enabled && (ImGui.IsItemHovered() || (ImGui.IsItemFocused() && ImGui.GetIO().NavVisible));
        if (!enabled) ImGui.EndDisabled();

        float t = Ease(id, hot);
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var dl = ImGui.GetWindowDrawList();
        // Leaving the game is the one red row.
        bool danger = icon == RowIcon.Leave;
        var accent = danger ? Danger : Wumpa;
        var accentHot = danger ? Danger : WumpaHot;
        if (t > 0.01f)
        {
            dl.AddRectFilledMultiColor(min, max,
                U32(accent, 0.24f * t), U32(accent, 0f), U32(accent, 0f), U32(accent, 0.24f * t));
            dl.AddRectFilled(min, new Vector2(min.X + 3f * ui, max.Y), U32(accentHot, t));
            // Cursor gutter scales with the row font (pause 20 px, Dev Menu 15 px).
            if (icon == RowIcon.None)
                DrawWumpa(dl, new Vector2(min.X + size * 0.7f, (min.Y + max.Y) * 0.5f + ui),
                    size * 0.3f * (0.6f + 0.4f * t), t);
        }
        if (icon != RowIcon.None)
            DrawIcon(dl, icon, new Vector2(min.X + size * 0.72f, (min.Y + max.Y) * 0.5f), size * 0.42f,
                t, IconPhase(id, t), enabled, ui);

        var color = enabled ? Vector4.Lerp(Sand, accentHot, t) : Disabled;
        var pos = new Vector2(min.X + size * 1.45f + 6f * t * ui, min.Y + (h - size) * 0.5f);
        ShadowText(dl, Display(size), size, pos, color, label.TrimEnd('.').ToUpperInvariant(), ui);

        if (tag != null)
        {
            float ts = 13f * ui;
            var tsz = BodyBold.CalcTextSizeA(ts, float.MaxValue, 0f, tag);
            dl.AddText(BodyBold, ts, new Vector2(max.X - tsz.X - 8f * ui, min.Y + (h - tsz.Y) * 0.5f),
                U32(enabled ? Muted : Disabled), tag);
        }
        return clicked && enabled;
    }

    static bool ClassicRow(string label, string id, bool enabled)
    {
        if (!enabled) ImGui.BeginDisabled();
        bool clicked = ImGui.Selectable($"  {label}##{id}");
        if (enabled && (ImGui.IsItemHovered() || ImGui.IsItemActive() || ImGui.IsItemFocused()))
        {
            var min = ImGui.GetItemRectMin();
            float y = min.Y + (ImGui.GetItemRectSize().Y - ImGui.GetTextLineHeight()) * 0.5f;
            ImGui.GetWindowDrawList().AddText(
                new Vector2(min.X, y),
                ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.92f, 0.4f, 1f)),
                ">");
        }
        if (!enabled) ImGui.EndDisabled();
        return clicked && enabled;
    }

    /// <summary>
    /// Section tabs of the launcher Settings sheet: equal-width boxes across the card, the
    /// selected one filled Wumpa. Returns the selected index. Old style: ImGui tab bar, where
    /// <paramref name="select"/> moves it to <paramref name="selected"/> (L1/R1); it shows
    /// the new tab from the next frame.
    /// </summary>
    public static int Tabs(string id, IReadOnlyList<string> labels, int selected, float ui, bool select = false)
    {
        if (labels.Count == 0) return selected;
        if (!Enabled) return ClassicTabs(id, labels, selected, select);

        float gap = 8f * ui;
        float h = 30f * ui;
        float avail = ImGui.GetContentRegionAvail().X;
        float w = Math.Max(1f, (avail - gap * (labels.Count - 1)) / labels.Count);
        float r = 2f * ui;
        var dl = ImGui.GetWindowDrawList();
        int result = selected;
        for (int i = 0; i < labels.Count; i++)
        {
            if (i > 0) ImGui.SameLine(0f, gap);
            if (ImGui.InvisibleButton($"##{id}-{i}", new Vector2(w, h)))
                result = i;
            bool sel = i == selected;
            // Keyboard / pad navigation starts on the open tab when the window appears.
            if (sel) ImGui.SetItemDefaultFocus();
            bool hover = ImGui.IsItemHovered();
            bool focus = ImGui.IsItemFocused() && ImGui.GetIO().NavVisible;
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();

            var back = sel ? (hover ? WumpaHot : Wumpa) : (hover ? Rgb(48, 32, 20) : Rgb(28, 18, 12));
            dl.AddRectFilled(min, max, U32(back), r);
            if (focus)
                dl.AddRect(min + new Vector2(1f), max - new Vector2(1f), U32(Sand), r, ImDrawFlags.None, 2f);
            else
                dl.AddRect(min, max, sel ? U32(Rgb(255, 180, 60), 0.86f) : U32(Rgb(255, 200, 120), 0.55f), r);

            // Many sections (mods add their own): shrink the label to the tab.
            string text = labels[i].ToUpperInvariant();
            float size = 14f * ui;
            var font = Display(size);
            float tw = font.CalcTextSizeA(size, float.MaxValue, 0f, text).X;
            float room = w - 10f * ui;
            if (tw > room && tw > 0f)
            {
                size *= room / tw;
                tw = room;
            }
            var pos = new Vector2(min.X + (w - tw) * 0.5f, min.Y + (h - size) * 0.5f);
            if (sel)
                dl.AddText(font, size, pos, U32(Rgb(42, 18, 0)), text);
            else
                ShadowText(dl, font, size, pos, Sand, text, ui);
        }
        return result;
    }

    static int ClassicTabs(string id, IReadOnlyList<string> labels, int selected, bool select)
    {
        int result = selected;
        if (!ImGui.BeginTabBar("##" + id)) return result;
        for (int i = 0; i < labels.Count; i++)
        {
            var flags = select && i == selected ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
            if (!BeginTabItem($"{labels[i]}##{id}-{i}", flags)) continue;
            result = i;
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
        return result;
    }

    /// <summary>ImGui.NET has flags only with p_open, and a p_open draws a close button on the tab.</summary>
    static unsafe bool BeginTabItem(string label, ImGuiTabItemFlags flags)
    {
        int n = System.Text.Encoding.UTF8.GetByteCount(label);
        byte* text = stackalloc byte[n + 1];
        fixed (char* c = label)
            System.Text.Encoding.UTF8.GetBytes(c, label.Length, text, n);
        text[n] = 0;
        return ImGuiNative.igBeginTabItem(text, null, flags) != 0;
    }

    /// <summary>Fading Wumpa rule between blocks (old style: separator).</summary>
    public static void Divider(float ui)
    {
        if (!Enabled)
        {
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            return;
        }
        ImGui.Dummy(new Vector2(0f, 2f * ui));
        var p = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;
        ImGui.GetWindowDrawList().AddRectFilledMultiColor(p, p + new Vector2(w, Math.Max(1f, ui)),
            U32(Wumpa, 0.5f), U32(Wumpa, 0f), U32(Wumpa, 0f), U32(Wumpa, 0.5f));
        ImGui.Dummy(new Vector2(w, 6f * ui));
    }

    /// <summary>Small bold caption above a widget.</summary>
    public static void Caption(string text)
    {
        bool font = _bodyBold.HasValue;
        if (font) ImGui.PushFont(_bodyBold!.Value);
        ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        ImGui.TextUnformatted(text.ToUpperInvariant());
        ImGui.PopStyleColor();
        if (font) ImGui.PopFont();
    }

    /// <summary>Key chips ("[Esc] Resume"), wrapped to the card width. Old style: one gray line.</summary>
    public static void Hints(float ui, params (string Key, string Action)[] items)
    {
        if (!Enabled)
        {
            ImGui.PushTextWrapPos(0f);
            ImGuiEx.TextDisabled(string.Join(" · ", items.Select(i => $"{i.Key} {i.Action.ToLowerInvariant()}")));
            ImGui.PopTextWrapPos();
            return;
        }

        var dl = ImGui.GetWindowDrawList();
        float size = 12f * ui;
        float padX = 6f * ui, gap = 5f * ui, spacing = 12f * ui;
        float chipH = size + 5f * ui;
        float avail = ImGui.GetContentRegionAvail().X;
        var origin = ImGui.GetCursorScreenPos();
        float x = 0f, y = 0f;
        foreach (var (key, action) in items)
        {
            var kSz = BodyBold.CalcTextSizeA(size, float.MaxValue, 0f, key);
            var aSz = Body.CalcTextSizeA(size, float.MaxValue, 0f, action);
            float chipW = kSz.X + padX * 2f;
            float w = chipW + gap + aSz.X;
            if (x > 0f && x + w > avail)
            {
                x = 0f;
                y += chipH + 4f * ui;
            }
            var p = origin + new Vector2(x, y);
            dl.AddRectFilled(p, p + new Vector2(chipW, chipH), U32(Wumpa, 0.85f), 4f * ui);
            dl.AddText(BodyBold, size, p + new Vector2(padX, (chipH - kSz.Y) * 0.5f), U32(Night), key);
            dl.AddText(Body, size, p + new Vector2(chipW + gap, (chipH - aSz.Y) * 0.5f), U32(Muted), action);
            x += w + spacing;
        }
        ImGui.Dummy(new Vector2(avail, y + chipH));
    }

    static void ShadowText(ImDrawListPtr dl, ImFontPtr font, float size, Vector2 pos, Vector4 color, string text, float ui)
    {
        dl.AddText(font, size, pos + new Vector2(0f, 2f * ui), U32(Vector4.Zero with { W = 0.7f * color.W }), text);
        dl.AddText(font, size, pos, U32(color), text);
    }

    /// <summary>Launcher logo treatment: crate-brown outline, drop shadow, yellow-to-orange fill.</summary>
    static void OutlinedTitle(ImDrawListPtr dl, ImFontPtr font, float size, Vector2 pos, string text, float ui)
        => OutlinedTitle(dl, font, size, pos, text, ui, Rgb(255, 196, 48), Wumpa);

    static void OutlinedTitle(ImDrawListPtr dl, ImFontPtr font, float size, Vector2 pos, string text, float ui,
        Vector4 top, Vector4 bottom)
    {
        float o = Math.Max(1.5f, 2f * ui);
        dl.AddText(font, size, pos + new Vector2(0f, o + 3f * ui), U32(Vector4.Zero with { W = 0.55f }), text);
        uint outline = U32(CrateCore);
        for (int i = 0; i < 8; i++)
        {
            float a = i * MathF.PI / 4f;
            dl.AddText(font, size, pos + new Vector2(MathF.Cos(a), MathF.Sin(a)) * o, outline, text);
        }
        // One fill pass, then recolor its vertices top→bottom. (Two clipped halves left a
        // 1 px seam on fractional y where the dark outline showed through.)
        unsafe
        {
            int first = dl.NativePtr->VtxBuffer.Size;
            dl.AddText(font, size, pos, 0xFFFFFFFFu, text);
            var verts = (ImDrawVert*)dl.NativePtr->VtxBuffer.Data;
            int end = dl.NativePtr->VtxBuffer.Size;
            float y0 = pos.Y + size * 0.2f, span = size * 0.6f;
            for (int i = first; i < end; i++)
            {
                float t = Math.Clamp((verts[i].pos.Y - y0) / span, 0f, 1f);
                verts[i].col = U32(Vector4.Lerp(top, bottom, t));
            }
        }
    }

    /// <summary>Fill colors of <see cref="HudText"/>.</summary>
    public enum HudTone { Wumpa, Gem, Lost }

    public static Vector2 HudTextSize(string text, float px) =>
        Display(px).CalcTextSizeA(px, float.MaxValue, 0f, text);

    /// <summary>In-game HUD text (box counter): the logo treatment, any size.</summary>
    public static void HudText(ImDrawListPtr dl, string text, Vector2 pos, float px, HudTone tone)
    {
        var (top, bottom) = tone switch
        {
            HudTone.Gem => (Rgb(255, 255, 255), Rgb(120, 200, 255)),
            HudTone.Lost => (Rgb(170, 164, 150), Rgb(120, 114, 102)),
            _ => (Rgb(255, 196, 48), Wumpa),
        };
        OutlinedTitle(dl, Display(px), px, pos, text, px / 30f, top, bottom);
    }

    /// <summary>Wooden crate icon for the box counter, <paramref name="s"/> px square.</summary>
    public static void HudCrate(ImDrawListPtr dl, Vector2 min, float s, bool faded)
    {
        float a = faded ? 0.45f : 1f;
        var max = min + new Vector2(s);
        float edge = Math.Max(1f, s * 0.14f);
        dl.AddRectFilled(min + new Vector2(0f, s * 0.08f), max + new Vector2(0f, s * 0.08f), U32(Vector4.Zero, 0.55f * a));
        dl.AddRectFilled(min, max, U32(CrateCore, a));
        dl.AddRectFilled(min + new Vector2(edge), max - new Vector2(edge), U32(Rgb(196, 112, 36), a));
        // Planks and the diagonal brace of the Crash 1 crate.
        uint dark = U32(Rgb(120, 62, 14), a);
        float third = (s - 2f * edge) / 3f;
        for (int i = 1; i < 3; i++)
        {
            float y = min.Y + edge + third * i;
            dl.AddLine(new Vector2(min.X + edge, y), new Vector2(max.X - edge, y), dark, Math.Max(1f, s * 0.04f));
        }
        dl.AddLine(min + new Vector2(edge, s - edge), min + new Vector2(s - edge, edge), U32(CrateCore, a), edge * 0.9f);
        dl.AddRect(min, max, U32(Rgb(36, 18, 4), a), 0f, ImDrawFlags.None, Math.Max(1f, s * 0.05f));
    }

    /// <summary>Fill color of a gem (gem hints card).</summary>
    public static Vector4 GemColor(GemHints.Gem gem) => gem switch
    {
        GemHints.Gem.Red => Rgb(255, 70, 60),
        GemHints.Gem.Blue => Rgb(70, 140, 255),
        GemHints.Gem.Green => Rgb(70, 210, 90),
        GemHints.Gem.Purple => Rgb(180, 95, 255),
        GemHints.Gem.Yellow => Rgb(255, 225, 60),
        GemHints.Gem.Orange => Rgb(255, 150, 30),
        _ => Rgb(170, 215, 255),
    };

    /// <summary>Line colors of the gem hints card.</summary>
    public static Vector4 HintColor(GemHints.Tone tone) => tone switch
    {
        GemHints.Tone.Good => Rgb(140, 225, 120),
        GemHints.Tone.Bad => Rgb(255, 130, 105),
        _ => Sand,
    };

    public static Vector2 HudBodySize(string text, float px) =>
        BodyBold.CalcTextSizeA(px, float.MaxValue, 0f, text);

    /// <summary>Nunito HUD line with a drop shadow (gem hints card).</summary>
    public static void HudBody(ImDrawListPtr dl, string text, Vector2 pos, float px, Vector4 color)
    {
        dl.AddText(BodyBold, px, pos + new Vector2(0f, Math.Max(1f, px * 0.08f)), U32(Vector4.Zero, 0.75f), text);
        dl.AddText(BodyBold, px, pos, U32(color), text);
    }

    /// <summary>Logo-style HUD title tinted from <paramref name="color"/> (gem hints card).</summary>
    public static void HudTitle(ImDrawListPtr dl, string text, Vector2 pos, float px, Vector4 color)
        => OutlinedTitle(dl, Display(px), px, pos, text, px / 30f, Vector4.Lerp(color, Rgb(255, 255, 255), 0.55f), color);

    /// <summary>Dark rounded card behind HUD text with a thin Wumpa border.</summary>
    public static void HudPanel(ImDrawListPtr dl, Vector2 min, Vector2 max, float rounding)
    {
        dl.AddRectFilled(min, max, U32(Night, 0.78f), rounding);
        dl.AddRect(min, max, U32(Wumpa, 0.45f), rounding, ImDrawFlags.None, Math.Max(1f, rounding * 0.15f));
    }

    /// <summary>Cut gem icon centred on <paramref name="c"/>: solid when owned, else a faint outline.</summary>
    public static void HudGem(ImDrawListPtr dl, Vector2 c, float r, Vector4 color, bool owned)
    {
        var tl = c + new Vector2(-0.55f * r, -0.6f * r);
        var tr = c + new Vector2(0.55f * r, -0.6f * r);
        var gr = c + new Vector2(r, -0.15f * r);
        var gl = c + new Vector2(-r, -0.15f * r);
        var bottom = c + new Vector2(0f, r);
        float a = owned ? 1f : 0.22f;
        dl.AddQuadFilled(tl, tr, gr, gl, U32(Vector4.Lerp(color, Rgb(255, 255, 255), 0.35f), a));
        dl.AddTriangleFilled(gl, gr, bottom, U32(color, a));
        // Facets: girdle and the crown corners down to the point.
        uint facet = U32(Vector4.Lerp(color, Vector4.Zero with { W = 1f }, 0.35f), owned ? 0.9f : 0.5f);
        float t = Math.Max(1f, r * 0.08f);
        dl.AddLine(gl, gr, facet, t);
        dl.AddLine(tl, bottom, facet, t);
        dl.AddLine(tr, bottom, facet, t);
        uint edge = U32(owned ? Rgb(36, 18, 4) : color, owned ? 0.9f : 1f);
        float e = Math.Max(1f, r * 0.12f);
        dl.AddLine(tl, tr, edge, e);
        dl.AddLine(tr, gr, edge, e);
        dl.AddLine(gr, bottom, edge, e);
        dl.AddLine(bottom, gl, edge, e);
        dl.AddLine(gl, tl, edge, e);
        if (owned)
            dl.AddTriangleFilled(tl + new Vector2(0.15f * r, 0.1f * r), c + new Vector2(-0.1f * r, -0.5f * r),
                c + new Vector2(-0.45f * r, -0.2f * r), U32(Rgb(255, 255, 255), 0.7f));
    }

    /// <summary>Tiny Wumpa fruit used as the menu cursor.</summary>
    static void DrawWumpa(ImDrawListPtr dl, Vector2 c, float r, float alpha)
    {
        dl.AddQuadFilled(
            c + new Vector2(0.05f * r, -0.8f * r),
            c + new Vector2(0.5f * r, -1.55f * r),
            c + new Vector2(1.15f * r, -1.35f * r),
            c + new Vector2(0.55f * r, -0.75f * r),
            U32(Leaf, alpha));
        dl.AddCircleFilled(c, r, U32(Rgb(196, 84, 0), alpha));
        dl.AddCircleFilled(c - new Vector2(0.12f * r), r * 0.84f, U32(Wumpa, alpha));
        dl.AddCircleFilled(c - new Vector2(0.38f * r), r * 0.26f, U32(Rgb(255, 226, 150), alpha));
    }

    static readonly Vector4 IconIdle = Rgb(96, 94, 86);
    static readonly Vector4 IconIdleDark = Rgb(74, 72, 66);
    static readonly Vector4 IconIdleDeep = Rgb(46, 44, 40);
    static readonly Vector4 IconOff = Rgb(64, 62, 56, 0.7f);
    static readonly Vector4 Go = Rgb(96, 208, 72);
    static readonly Vector4 Steel = Rgb(184, 192, 202);
    static readonly Vector4 Danger = Rgb(255, 72, 56);
    static readonly Dictionary<string, float> _iconPhase = new();

    /// <summary>Seconds of hover, weighted by the hover ease: animations slow to a stop.</summary>
    static float IconPhase(string id, float t)
    {
        _iconPhase.TryGetValue(id, out float p);
        p = (p + Math.Min(ImGui.GetIO().DeltaTime, 0.1f) * t) % 3600f;
        _iconPhase[id] = p;
        return p;
    }

    /// <summary>Row icon with a drop shadow; idle dark gray, colored and animated when hot.</summary>
    static void DrawIcon(ImDrawListPtr dl, RowIcon icon, Vector2 c, float r, float t, float phase, bool enabled, float ui)
    {
        if (!enabled) t = 0f;
        IconShape(dl, icon, c + new Vector2(0f, 2f * ui), r, t, phase, enabled, shadow: true);
        IconShape(dl, icon, c, r, t, phase, enabled, shadow: false);
    }

    /// <summary>One pass of a row icon. The shadow pass draws only the silhouette.</summary>
    static void IconShape(ImDrawListPtr dl, RowIcon icon, Vector2 c, float r, float t, float phase, bool enabled, bool shadow)
    {
        uint P(Vector4 hot, Vector4 idle) =>
            shadow ? U32(Vector4.Zero, 0.55f) : U32(enabled ? Vector4.Lerp(idle, hot, t) : IconOff);
        Vector2 V(float x, float y) => c + new Vector2(x, y) * r;

        switch (icon)
        {
            case RowIcon.Resume:
            {
                // Play triangle that pulses, with a fading ghost sliding out behind it.
                float s = 1f + 0.08f * MathF.Sin(phase * 7f) * t;
                var o = new Vector2(0.08f, 0f);
                if (!shadow && t > 0.01f)
                {
                    var g = o - new Vector2(0.45f * t, 0f);
                    dl.AddTriangleFilled(V(g.X - 0.6f * s, -0.85f * s), V(g.X + 0.85f * s, 0f),
                        V(g.X - 0.6f * s, 0.85f * s), U32(Go, 0.3f * t));
                }
                dl.AddTriangleFilled(V(o.X - 0.6f * s, -0.85f * s), V(o.X + 0.85f * s, 0f),
                    V(o.X - 0.6f * s, 0.85f * s), P(Go, IconIdle));
                break;
            }
            case RowIcon.SaveState:
            case RowIcon.LoadState:
            {
                // Arrow into (save) or out of (load) a tray; the arrow bobs when hot.
                uint col = P(WumpaHot, IconIdle);
                float th = Math.Max(1.5f, 0.2f * r);
                dl.PathLineTo(V(-0.8f, 0.3f));
                dl.PathLineTo(V(-0.8f, 0.85f));
                dl.PathLineTo(V(0.8f, 0.85f));
                dl.PathLineTo(V(0.8f, 0.3f));
                dl.PathStroke(col, ImDrawFlags.None, th);
                float bob = 0.16f * MathF.Sin(phase * 8f) * t;
                float shaft = th * 1.1f;
                if (icon == RowIcon.SaveState)
                {
                    dl.AddLine(V(0f, -0.95f + bob), V(0f, 0.05f + bob), col, shaft);
                    dl.AddTriangleFilled(V(-0.38f, bob), V(0.38f, bob), V(0f, 0.48f + bob), col);
                }
                else
                {
                    dl.AddLine(V(0f, 0.45f + bob), V(0f, -0.5f + bob), col, shaft);
                    dl.AddTriangleFilled(V(0.38f, -0.45f + bob), V(-0.38f, -0.45f + bob), V(0f, -0.95f + bob), col);
                }
                break;
            }
            case RowIcon.Settings:
            {
                // Gear that spins while hot and coasts to a stop.
                float a = phase * 2.6f;
                uint col = P(Steel, IconIdle);
                const int teeth = 8;
                for (int i = 0; i < teeth; i++)
                {
                    float ang = a + i * MathF.Tau / teeth;
                    var dir = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * r;
                    var side = new Vector2(-dir.Y, dir.X);
                    dl.AddQuadFilled(c + dir * 0.6f - side * 0.2f, c + dir - side * 0.13f,
                        c + dir + side * 0.13f, c + dir * 0.6f + side * 0.2f, col);
                }
                dl.AddCircle(c, 0.5f * r, col, 24, 0.4f * r); // body ring 0.3r..0.7r
                break;
            }
            case RowIcon.Map:
            {
                // Folded pirate map; when hot the dotted trail marches to a pulsing red X.
                float[] xs = { -0.9f, -0.3f, 0.3f, 0.9f };
                float Top(int k) => k % 2 == 0 ? -0.72f : -0.88f;
                for (int i = 0; i < 3; i++)
                {
                    uint fill = i % 2 == 0
                        ? P(Rgb(236, 208, 152), IconIdle)
                        : P(Rgb(204, 168, 108), IconIdleDark);
                    dl.AddQuadFilled(V(xs[i], Top(i)), V(xs[i + 1], Top(i + 1)),
                        V(xs[i + 1], Top(i + 1) + 1.6f), V(xs[i], Top(i) + 1.6f), fill);
                }
                if (shadow) break;

                uint dot = P(Rgb(120, 62, 20), IconIdleDeep);
                var p0 = new Vector2(-0.62f, 0.5f);
                var p1 = new Vector2(-0.2f, -0.7f);
                var p2 = new Vector2(0.48f, -0.18f);
                const int dots = 5;
                float march = phase * 1.5f % 1f;
                for (int k = 0; k < dots; k++)
                {
                    float u = (k + march) / dots * 0.8f;
                    var q = (1 - u) * (1 - u) * p0 + 2 * (1 - u) * u * p1 + u * u * p2;
                    dl.AddCircleFilled(V(q.X, q.Y), Math.Max(1f, 0.08f * r), dot);
                }
                float xr = 0.2f * (1f + 0.3f * MathF.Sin(phase * 8f) * t);
                uint xc = P(MarkRed, IconIdleDeep);
                float xt = Math.Max(1.5f, 0.14f * r);
                dl.AddLine(V(p2.X - xr, p2.Y - xr), V(p2.X + xr, p2.Y + xr), xc, xt);
                dl.AddLine(V(p2.X - xr, p2.Y + xr), V(p2.X + xr, p2.Y - xr), xc, xt);
                break;
            }
            case RowIcon.Leave:
            {
                // Red X: quarter turn as the hover eases in, then a light pulse.
                float a = t * MathF.PI * 0.5f;
                float s = 1f + 0.1f * MathF.Sin(phase * 9f) * t;
                uint col = P(Danger, IconIdle);
                float th = Math.Max(2f, 0.3f * r);
                for (int i = 0; i < 2; i++)
                {
                    float ang = a + MathF.PI * 0.25f + i * MathF.PI * 0.5f;
                    var d = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * (0.75f * r * s);
                    dl.AddLine(c - d, c + d, col, th);
                    dl.AddCircleFilled(c - d, th * 0.5f, col);
                    dl.AddCircleFilled(c + d, th * 0.5f, col);
                }
                break;
            }
        }
    }

    static float Ease(string id, bool hot)
    {
        _hot.TryGetValue(id, out float t);
        float step = Math.Min(ImGui.GetIO().DeltaTime, 0.1f) * 12f;
        t = hot ? Math.Min(1f, t + step) : Math.Max(0f, t - step);
        _hot[id] = t;
        return t;
    }

    static Vector4 Rgb(int r, int g, int b, float a = 1f) => new(r / 255f, g / 255f, b / 255f, a);
    static Vector4 A(Vector4 c, float alpha) => c with { W = alpha };
    static uint U32(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);
    static uint U32(Vector4 c, float alpha) => ImGui.ColorConvertFloat4ToU32(c with { W = alpha });
}
