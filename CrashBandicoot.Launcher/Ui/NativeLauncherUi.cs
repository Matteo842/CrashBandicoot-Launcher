using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Text.Json;

namespace CrashBandicoot.Launcher.Ui;

/// <summary>
/// Native WinForms launcher surface (menu, sheets, prepare overlay).
/// </summary>
public sealed class NativeLauncherUi : UserControl, ILauncherUi
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    static readonly (string Label, string Id)[] KeyFields =
    [
        ("Cross", "cross"), ("Circle", "circle"), ("Square", "square"), ("Triangle", "triangle"),
        ("Start", "start"), ("Select", "select"),
        ("Up", "up"), ("Down", "down"), ("Left", "left"), ("Right", "right"),
        ("Cheat menu", "cheatMenu"),
    ];

    static readonly Dictionary<string, string> FocusLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cue"] = "Problem with the .cue",
        ["bin"] = "Problem with the .bin",
        ["pair"] = ".cue + .bin pair",
        ["game"] = "Wrong game / region",
        ["other"] = "Launcher error",
    };

    readonly Panel _stage = new() { Dock = DockStyle.Fill };
    readonly System.Windows.Forms.Timer _anim = new() { Interval = 33 };
    readonly List<MenuHit> _menuHits = [];

    Image? _map;
    Font? _fontCrash;
    Font? _fontRecomp;
    Font? _fontMenu;
    Font? _fontMenuHot;
    Font? _fontChip;
    Font? _fontFooter;
    Font? _fontVer;
    Font? _fontInfo;
    Font? _fontMark;

    Rectangle _crateRect;
    Rectangle _infoRect;
    Rectangle _chipRect;
    Rectangle _brandCrashRect;
    Rectangle _brandRecompRect;
    int _hoverMenu = -1;
    bool _hoverInfo;
    bool _hoverChip;
    bool _canStart;
    float _cratePhase;
    string _status = "";
    string _statusKind = "";
    string _discPath = "";
    string _version = "v1.3.0";
    bool _disposed;

    // Sheets / overlays
    Panel? _prepOverlay;
    Label? _prepDetail;
    Panel? _prepBarFill;
    Panel? _sheetHost;
    Panel? _activeSheet;

    // Settings / controls / cheats state buffers
    readonly Dictionary<string, TextBox> _keyBoxes = new(StringComparer.Ordinal);
    ThemeSlider? _vol;
    CheckBox? _muted;
    CheckBox? _fullscreen;
    CheckBox? _widescreen;
    ComboBox? _internalRes;
    ComboBox? _texFilter;
    ThemeSlider? _filterStrength;
    Label? _filterStrengthLabel;
    CheckBox? _dedither;
    CheckBox? _dejitter;
    CheckBox? _cheatLives;
    CheckBox? _cheatLevel;

    public NativeLauncherUi()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
        Dock = DockStyle.Fill;
        BackColor = NativeTheme.Night;

        _stage.SetStylePublic();
        _stage.Dock = DockStyle.Fill;
        _stage.BackColor = NativeTheme.Night;
        _stage.Paint += OnStagePaint;
        _stage.Resize += (_, _) => LayoutHits();
        _stage.MouseMove += OnStageMouseMove;
        _stage.MouseLeave += (_, _) =>
        {
            _hoverMenu = -1;
            _hoverInfo = _hoverChip = false;
            _stage.Cursor = Cursors.Default;
            _stage.Invalidate();
        };
        _stage.MouseClick += OnStageMouseClick;
        Controls.Add(_stage);

        _anim.Tick += (_, _) =>
        {
            _cratePhase += 0.065f;
            if (_cratePhase > MathF.PI * 2) _cratePhase -= MathF.PI * 2;
            _stage.Invalidate(_crateRect.InflateCopy(12, 16));
        };
    }

    public Control? AsControl => this;

    public event EventHandler<LauncherUiMessageEventArgs>? MessageReceived;

    public Task InitializeAsync(string uiDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uiDirectory);
        NativeTheme.LoadFonts(uiDirectory);

        _fontCrash = NativeTheme.MakeBungee(64);
        _fontRecomp = NativeTheme.MakeBungee(28);
        _fontMenu = NativeTheme.MakeBungee(38);
        _fontMenuHot = NativeTheme.MakeBungee(38);
        _fontChip = NativeTheme.MakeNunito(15, extraBold: true);
        _fontFooter = NativeTheme.MakeNunito(15);
        _fontVer = NativeTheme.MakeNunito(13);
        _fontInfo = NativeTheme.MakeBungee(18);
        _fontMark = NativeTheme.MakeBungee(112);

        var mapPath = Path.Combine(uiDirectory, "world_map.png");
        if (File.Exists(mapPath))
        {
            using var fs = File.OpenRead(mapPath);
            _map = Image.FromStream(fs);
        }

        BuildOverlays();
        LayoutHits();
        _anim.Start();

        // Defer ready so host has subscribed MessageReceived (InitAsync order).
        BeginInvoke(() => Emit(new { type = "ready" }));
        return Task.CompletedTask;
    }

    public void PostJson(string json)
    {
        if (_disposed || IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => PostJson(json));
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) return;
            switch (typeEl.GetString())
            {
                case "state":
                    if (root.TryGetProperty("state", out var state))
                        ApplyState(state);
                    break;
                case "prepare":
                    ShowPrep(true);
                    var frac = root.TryGetProperty("fraction", out var f) && f.TryGetSingle(out var fv) ? fv : 0f;
                    var detail = root.TryGetProperty("detail", out var d) ? d.GetString() : null;
                    SetPrepProgress(frac, detail);
                    break;
                case "prepareDone":
                    ShowPrep(false);
                    break;
                case "desktopWarning":
                    ShowDesktopWarning(root);
                    break;
                case "error":
                    ShowError(root);
                    break;
            }
        }
        catch
        {
            // ignore malformed host payloads
        }
    }

    void Emit(object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        MessageReceived?.Invoke(this, new LauncherUiMessageEventArgs(json));
    }

    void ApplyState(JsonElement state)
    {
        if (state.TryGetProperty("version", out var ver) && ver.GetString() is { } v)
            _version = "v" + v;
        if (state.TryGetProperty("status", out var st))
            _status = st.GetString() ?? "";
        if (state.TryGetProperty("statusKind", out var sk))
            _statusKind = sk.GetString() ?? "";
        if (state.TryGetProperty("canStart", out var cs))
            _canStart = cs.ValueKind == JsonValueKind.True;
        if (state.TryGetProperty("discPath", out var dp))
            _discPath = (dp.GetString() ?? "").Trim();
        else if (state.TryGetProperty("discName", out var dn))
            _discPath = dn.GetString() ?? "";

        if (state.TryGetProperty("keys", out var keys))
        {
            foreach (var (_, id) in KeyFields)
            {
                if (_keyBoxes.TryGetValue(id, out var box) &&
                    !box.IsDisposed &&
                    keys.TryGetProperty(id, out var kv) &&
                    kv.GetString() is { } s)
                    box.Text = s;
            }
        }

        SetFloat(_vol, state, "masterVolume", 0, 100, x => (int)Math.Round(x * 100));
        SetCheck(_muted, state, "muted");
        SetCheck(_fullscreen, state, "fullscreen");
        SetCheck(_widescreen, state, "widescreen");
        if (state.TryGetProperty("internalResolution", out var ir) && ir.TryGetInt32(out var scale))
            SetCombo(_internalRes, scale);
        if (state.TryGetProperty("textureFilter", out var tf) && tf.TryGetInt32(out var filter))
        {
            SetCombo(_texFilter, filter);
            SyncFilterStrengthRow();
        }
        if (state.TryGetProperty("textureFilterStrength", out var tfs) && tfs.TryGetSingle(out var strength) &&
            _filterStrength is { IsDisposed: false })
            _filterStrength.Value = Math.Clamp((int)Math.Round(strength * 100), 0, 100);
        SetCheck(_dedither, state, "dedither");
        SetCheck(_dejitter, state, "dejitter");
        SetCheck(_cheatLives, state, "infiniteLives");
        SetCheck(_cheatLevel, state, "levelSelect");

        LayoutHits();
        _stage.Invalidate();
    }

    static void SetCheck(CheckBox? box, JsonElement state, string name)
    {
        if (box == null || box.IsDisposed || !state.TryGetProperty(name, out var el)) return;
        box.Checked = el.ValueKind == JsonValueKind.True;
    }

    static void SetFloat(ThemeSlider? bar, JsonElement state, string name, int min, int max, Func<float, int> map)
    {
        if (bar == null || bar.IsDisposed || !state.TryGetProperty(name, out var el) || !el.TryGetSingle(out var v)) return;
        bar.Value = Math.Clamp(map(v), min, max);
    }

    static void SetCombo(ComboBox? cb, int value)
    {
        if (cb == null || cb.IsDisposed || cb.DataSource == null) return;
        for (var i = 0; i < cb.Items.Count; i++)
        {
            if (cb.Items[i] is Kv kv && kv.Value == value)
            {
                cb.SelectedIndex = i;
                return;
            }
        }
    }

    void LayoutHits()
    {
        var w = _stage.ClientSize.Width;
        var h = _stage.ClientSize.Height;
        if (w < 10 || h < 10) return;

        const int footH = 78;
        var contentH = h - footH;

        // Brand top-left
        var brandX = 40;
        var brandY = Math.Max(36, contentH / 2 - 180);
        _brandCrashRect = new Rectangle(brandX, brandY, 420, 70);
        _brandRecompRect = new Rectangle(brandX, brandY + 66, 420, 36);

        // Crate under brand
        const int crate = 220;
        _crateRect = new Rectangle(brandX, brandY + 120, crate, crate);

        // Menu right — larger Bungee needs more width/row height
        _menuHits.Clear();
        var menuLabels = new[] { "START GAME", "CONTROLS", "SETTINGS", "CHEAT", "EXIT" };
        var menuX = w - 340;
        var menuY = contentH / 2 - 170;
        const int menuRow = 70;
        for (var i = 0; i < menuLabels.Length; i++)
        {
            _menuHits.Add(new MenuHit(menuLabels[i], new Rectangle(menuX, menuY + i * menuRow, 300, 56), i));
        }

        // Footer
        var footY = h - footH + 16;
        _infoRect = new Rectangle(28, footY, 36, 36);
        _chipRect = new Rectangle(76, footY + 1, 168, 34);
    }

    void OnStageMouseMove(object? sender, MouseEventArgs e)
    {
        var prevMenu = _hoverMenu;
        var prevInfo = _hoverInfo;
        var prevChip = _hoverChip;
        _hoverMenu = -1;
        for (var i = 0; i < _menuHits.Count; i++)
        {
            if (_menuHits[i].Bounds.Contains(e.Location))
            {
                if (i == 0 && !_canStart) break;
                _hoverMenu = i;
                break;
            }
        }
        _hoverInfo = _infoRect.Contains(e.Location);
        _hoverChip = _chipRect.Contains(e.Location);
        _stage.Cursor = (_hoverMenu >= 0 || _hoverInfo || _hoverChip) ? Cursors.Hand : Cursors.Default;
        if (prevMenu != _hoverMenu || prevInfo != _hoverInfo || prevChip != _hoverChip)
            _stage.Invalidate();
    }

    void OnStageMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (_sheetHost is { Visible: true } || _prepOverlay is { Visible: true }) return;

        if (_infoRect.Contains(e.Location))
        {
            ShowAbout();
            return;
        }
        if (_chipRect.Contains(e.Location))
        {
            Emit(new { type = "pickDisc" });
            return;
        }

        for (var i = 0; i < _menuHits.Count; i++)
        {
            if (!_menuHits[i].Bounds.Contains(e.Location)) continue;
            switch (i)
            {
                case 0:
                    if (_canStart) Emit(new { type = "start" });
                    break;
                case 1:
                    Emit(new { type = "getState" });
                    ShowControls();
                    break;
                case 2:
                    Emit(new { type = "getState" });
                    ShowSettings();
                    break;
                case 3:
                    Emit(new { type = "getState" });
                    ShowCheat();
                    break;
                case 4:
                    Emit(new { type = "exit" });
                    break;
            }
            break;
        }
    }

    void OnStagePaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var w = _stage.ClientSize.Width;
        var h = _stage.ClientSize.Height;
        var bounds = new Rectangle(0, 0, w, h);

        DrawBackground(g, bounds);
        DrawMap(g, bounds);
        DrawBrand(g);
        DrawCrate(g);
        DrawMenu(g);
        DrawFooter(g, bounds);
    }

    static void DrawBackground(Graphics g, Rectangle bounds)
    {
        using (var brush = new LinearGradientBrush(bounds, NativeTheme.JungleTop, NativeTheme.Night, 165f))
        {
            var blend = new ColorBlend
            {
                Colors = [NativeTheme.JungleTop, NativeTheme.Night, NativeTheme.JungleWarm],
                Positions = [0f, 0.45f, 1f],
            };
            brush.InterpolationColors = blend;
            g.FillRectangle(brush, bounds);
        }

        // Warm / green radial glows
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(-bounds.Width / 5, bounds.Height / 2, bounds.Width / 2, bounds.Height);
            using var pgb = new PathGradientBrush(path)
            {
                CenterColor = Color.FromArgb(55, 255, 138, 0),
                SurroundColors = [Color.FromArgb(0, 255, 138, 0)],
            };
            g.FillPath(pgb, path);
        }
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(bounds.Width * 2 / 3, -bounds.Height / 8, bounds.Width / 2, bounds.Height / 2);
            using var pgb = new PathGradientBrush(path)
            {
                CenterColor = Color.FromArgb(70, 40, 120, 70),
                SurroundColors = [Color.FromArgb(0, 40, 120, 70)],
            };
            g.FillPath(pgb, path);
        }

        // Diagonal scanlines
        var w = bounds.Width;
        var h = bounds.Height;
        using var pen = new Pen(Color.FromArgb(8, 255, 180, 40), 1f);
        for (var i = -h; i < w + h; i += 15)
            g.DrawLine(pen, i, 0, i + h, h);
    }

    void DrawMap(Graphics g, Rectangle bounds)
    {
        if (_map == null) return;
        var maxW = Math.Min((int)(bounds.Width * 0.58), 520);
        var maxH = (int)(bounds.Height * 0.70);
        var scale = Math.Min(maxW / (float)_map.Width, maxH / (float)_map.Height);
        var dw = (int)(_map.Width * scale);
        var dh = (int)(_map.Height * scale);
        var x = (bounds.Width - dw) / 2;
        var y = (int)(bounds.Height * 0.46f) - dh / 2 - 20;

        // Soft drop shadow
        var matrix = new ColorMatrix { Matrix33 = 0.35f };
        using var attrs = new ImageAttributes();
        attrs.SetColorMatrix(matrix);
        g.DrawImage(_map, new Rectangle(x + 6, y + 14, dw, dh), 0, 0, _map.Width, _map.Height, GraphicsUnit.Pixel, attrs);
        g.DrawImage(_map, new Rectangle(x, y, dw, dh));
    }

    void DrawBrand(Graphics g)
    {
        if (_fontCrash == null || _fontRecomp == null) return;
        DrawOutlinedText(g, "CRASH", _fontCrash, NativeTheme.Wumpa, Color.FromArgb(58, 21, 0),
            _brandCrashRect.Location, strokeWidth: 3f, shadow: true);
        DrawOutlinedText(g, "RECOMPILED", _fontRecomp, NativeTheme.Danger, Color.FromArgb(58, 0, 0),
            _brandRecompRect.Location, strokeWidth: 2f, shadow: true);
    }

    static void DrawOutlinedText(Graphics g, string text, Font font, Color fill, Color strokeColor,
        Point origin, float strokeWidth, bool shadow)
    {
        using var path = new GraphicsPath();
        path.AddString(text, font.FontFamily, (int)font.Style, font.Size, origin, StringFormat.GenericTypographic);
        if (shadow)
        {
            using var shadowPath = (GraphicsPath)path.Clone();
            var m = new Matrix();
            m.Translate(0, 6);
            shadowPath.Transform(m);
            using var sb = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
            g.FillPath(sb, shadowPath);
            using var deep = new SolidBrush(Color.FromArgb(200, 90, 34, 0));
            var m2 = new Matrix();
            m2.Translate(0, 4);
            using var deepPath = (GraphicsPath)path.Clone();
            deepPath.Transform(m2);
            g.FillPath(deep, deepPath);
        }
        using (var pen = new Pen(strokeColor, strokeWidth) { LineJoin = LineJoin.Round })
            g.DrawPath(pen, path);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);
    }

    void DrawCrate(Graphics g)
    {
        if (_fontMark == null) return;
        var bob = MathF.Sin(_cratePhase) * 8f;
        var rot = MathF.Sin(_cratePhase) * 2f;
        var cx = _crateRect.X + _crateRect.Width / 2f;
        var cy = _crateRect.Y + _crateRect.Height / 2f + bob;

        var state = g.Save();
        g.TranslateTransform(cx, cy);
        g.RotateTransform(rot);
        g.TranslateTransform(-_crateRect.Width / 2f, -_crateRect.Height / 2f);

        var r = new Rectangle(0, 0, _crateRect.Width, _crateRect.Height);
        using (var shadow = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            g.FillRectangle(shadow, 8, 14, r.Width, r.Height);

        using (var core = new SolidBrush(NativeTheme.CrateCore))
            g.FillRectangle(core, r);

        // Planks
        var inset = Rectangle.Inflate(r, -20, -20);
        var plankH = inset.Height;
        var bands = new[]
        {
            (0.00f, 0.21f, Color.FromArgb(208, 137, 58)),
            (0.21f, 0.24f, Color.FromArgb(122, 64, 16)),
            (0.24f, 0.45f, Color.FromArgb(196, 122, 42)),
            (0.45f, 0.48f, Color.FromArgb(122, 64, 16)),
            (0.48f, 0.69f, Color.FromArgb(208, 142, 63)),
            (0.69f, 0.72f, Color.FromArgb(122, 64, 16)),
            (0.72f, 1.00f, Color.FromArgb(184, 111, 36)),
        };
        foreach (var (a, b, c) in bands)
        {
            var y0 = inset.Y + (int)(plankH * a);
            var y1 = inset.Y + (int)(plankH * b);
            using var br = new SolidBrush(c);
            g.FillRectangle(br, inset.X, y0, inset.Width, Math.Max(1, y1 - y0));
        }

        // X beams
        DrawBeam(g, r, 45f);
        DrawBeam(g, r, -45f);

        // Frame
        using (var frame = new Pen(NativeTheme.WoodFrame, 20))
            g.DrawRectangle(frame, Rectangle.Inflate(r, -10, -10));
        using (var inner = new Pen(NativeTheme.CrateCore, 2))
            g.DrawRectangle(inner, Rectangle.Inflate(r, -21, -21));

        // Bolts
        DrawBolt(g, 13, 13);
        DrawBolt(g, r.Width - 26, 13);
        DrawBolt(g, 13, r.Height - 26);
        DrawBolt(g, r.Width - 26, r.Height - 26);

        // ?
        using var markPath = new GraphicsPath();
        var markOrigin = new Point(r.Width / 2 - 36, r.Height / 2 - 62);
        markPath.AddString("?", _fontMark.FontFamily, (int)_fontMark.Style, _fontMark.Size,
            markOrigin, StringFormat.GenericTypographic);
        using (var stroke = new Pen(NativeTheme.MarkRed, 8f) { LineJoin = LineJoin.Round })
            g.DrawPath(stroke, markPath);
        using (var fill = new SolidBrush(NativeTheme.MarkYellow))
            g.FillPath(fill, markPath);

        g.Restore(state);
    }

    static void DrawBeam(Graphics g, Rectangle crate, float angle)
    {
        var state = g.Save();
        g.TranslateTransform(crate.Width / 2f, crate.Height / 2f);
        g.RotateTransform(angle);
        var beam = new Rectangle(-13, -crate.Height / 2 - 10, 26, crate.Height + 20);
        using var br = new LinearGradientBrush(beam,
            Color.FromArgb(138, 74, 18), Color.FromArgb(224, 160, 80), 0f);
        g.FillRectangle(br, beam);
        using var edge = new Pen(Color.FromArgb(90, 30, 12, 0), 2);
        g.DrawRectangle(edge, beam);
        g.Restore(state);
    }

    static void DrawBolt(Graphics g, int x, int y)
    {
        var r = new Rectangle(x, y, 13, 13);
        using var br = new LinearGradientBrush(r, Color.FromArgb(232, 234, 236), Color.FromArgb(58, 62, 68), 45f);
        g.FillEllipse(br, r);
        using var pen = new Pen(Color.FromArgb(90, 0, 0, 0));
        g.DrawEllipse(pen, r);
    }

    void DrawMenu(Graphics g)
    {
        if (_fontMenu == null) return;
        for (var i = 0; i < _menuHits.Count; i++)
        {
            var hit = _menuHits[i];
            var disabled = i == 0 && !_canStart;
            var hot = _hoverMenu == i && !disabled;
            Color color;
            if (disabled) color = Color.FromArgb(128, 122, 117, 104);
            else if (i == 0) color = hot ? NativeTheme.WumpaHot : NativeTheme.Wumpa;
            else if (hot) color = NativeTheme.WumpaHot;
            else color = NativeTheme.Sand;

            var origin = hit.Bounds.Location;
            if (hot) origin.Offset(10, 0);

            using var path = new GraphicsPath();
            path.AddString(hit.Label, _fontMenu.FontFamily, (int)_fontMenu.Style, _fontMenu.Size,
                origin, StringFormat.GenericTypographic);
            using (var shadow = new SolidBrush(Color.FromArgb(180, 0, 0, 0)))
            {
                using var sp = (GraphicsPath)path.Clone();
                var m = new Matrix();
                m.Translate(0, 2);
                sp.Transform(m);
                g.FillPath(shadow, sp);
            }
            using var brush = new SolidBrush(color);
            g.FillPath(brush, path);
        }
    }

    void DrawFooter(Graphics g, Rectangle bounds)
    {
        if (_fontChip == null || _fontFooter == null || _fontVer == null || _fontInfo == null) return;

        var footTop = bounds.Height - 78;
        using (var br = new LinearGradientBrush(
                   new Rectangle(0, footTop, bounds.Width, 78),
                   Color.FromArgb(0, 0, 0, 0),
                   Color.FromArgb(90, 0, 0, 0),
                   90f))
            g.FillRectangle(br, 0, footTop, bounds.Width, 78);
        using (var pen = new Pen(Color.FromArgb(30, 255, 200, 120)))
            g.DrawLine(pen, 0, footTop, bounds.Width, footTop);

        // Info button — center glyph via path bounds (Bungee metrics are off with TextRenderer)
        var infoBack = _hoverInfo
            ? Color.FromArgb(90, 255, 138, 0)
            : Color.FromArgb(45, 255, 138, 0);
        using (var br = new SolidBrush(infoBack))
            g.FillEllipse(br, _infoRect);
        using (var pen = new Pen(Color.FromArgb(140, 255, 180, 60), 2f))
            g.DrawEllipse(pen, _infoRect);
        using (var infoPath = new GraphicsPath())
        {
            infoPath.AddString("i", _fontInfo.FontFamily, (int)_fontInfo.Style, _fontInfo.Size,
                Point.Empty, StringFormat.GenericTypographic);
            var gb = infoPath.GetBounds();
            var dx = _infoRect.X + _infoRect.Width / 2f - (gb.X + gb.Width / 2f);
            var dy = _infoRect.Y + _infoRect.Height / 2f - (gb.Y + gb.Height / 2f);
            using var mx = new Matrix();
            mx.Translate(dx, dy);
            infoPath.Transform(mx);
            using var fill = new SolidBrush(NativeTheme.WumpaHot);
            g.FillPath(fill, infoPath);
        }

        // Chip
        var chipFill = _hoverChip
            ? Color.FromArgb(70, 255, 138, 0)
            : Color.FromArgb(30, 255, 138, 0);
        using (var path = RoundRect(_chipRect, 17))
        using (var br = new SolidBrush(chipFill))
        using (var pen = new Pen(Color.FromArgb(90, 255, 180, 60)))
        {
            g.FillPath(br, path);
            g.DrawPath(pen, path);
        }
        TextRenderer.DrawText(g, "Select disc (.cue)", _fontChip, _chipRect, NativeTheme.Sand,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        // Status + path
        var textX = _chipRect.Right + 14;
        var textW = bounds.Width - textX - 80;
        if (!string.IsNullOrEmpty(_status))
        {
            var sc = _statusKind == "error" ? Color.FromArgb(255, 143, 143)
                : _statusKind == "ok" ? NativeTheme.Ok
                : Color.FromArgb(200, NativeTheme.Sand);
            TextRenderer.DrawText(g, _status, _fontFooter,
                new Rectangle(textX, footTop + 6, textW, 22), sc,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        var pathText = string.IsNullOrEmpty(_discPath) ? "(none)" : _discPath;
        TextRenderer.DrawText(g, pathText, _fontFooter,
            new Rectangle(textX, _chipRect.Y + 5, textW, 24),
            Color.FromArgb(160, NativeTheme.Sand),
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        TextRenderer.DrawText(g, _version, _fontVer,
            new Rectangle(bounds.Width - 70, bounds.Height - 36, 58, 20),
            Color.FromArgb(115, NativeTheme.Sand),
            TextFormatFlags.Right | TextFormatFlags.NoPadding);
    }

    static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ── Overlays / sheets ──────────────────────────────────────────────

    void BuildOverlays()
    {
        _prepOverlay = MakeDimHost();
        var prepCard = MakeCard(480, 180);
        prepCard.Controls.Add(MakeTitle("Preparing game"));
        _prepDetail = new Label
        {
            Text = "Working from your disc…",
            ForeColor = Color.FromArgb(210, NativeTheme.Sand),
            Font = NativeTheme.MakeNunito(15),
            Location = new Point(36, 110),
            Size = new Size(480, 24),
            BackColor = Color.Transparent,
        };
        prepCard.Controls.Add(_prepDetail);
        var barBg = new Panel
        {
            Location = new Point(36, 148),
            Size = new Size(480, 12),
            BackColor = Color.FromArgb(20, 255, 255, 255),
        };
        _prepBarFill = new Panel
        {
            Location = new Point(0, 0),
            Size = new Size(0, 12),
            BackColor = NativeTheme.Wumpa,
        };
        barBg.Controls.Add(_prepBarFill);
        prepCard.Controls.Add(barBg);
        prepCard.Controls.Add(new Label
        {
            Text = "First prepare writes into the game folder next to the exe. Your .cue + .bin must still be present to play.",
            ForeColor = Color.FromArgb(140, NativeTheme.Sand),
            Font = NativeTheme.MakeNunito(13),
            Location = new Point(36, 176),
            Size = new Size(480, 40),
            BackColor = Color.Transparent,
        });
        _prepOverlay.Controls.Add(prepCard);
        CenterChild(_prepOverlay, prepCard);
        _prepOverlay.Resize += (_, _) => CenterChild(_prepOverlay, prepCard);
        Controls.Add(_prepOverlay);
        _prepOverlay.BringToFront();

        _sheetHost = MakeDimHost();
        Controls.Add(_sheetHost);
        _sheetHost.BringToFront();
    }

    Panel MakeDimHost()
    {
        var p = new Panel
        {
            Dock = DockStyle.Fill,
            Visible = false,
            BackColor = Color.FromArgb(200, 4, 10, 12),
        };
        return p;
    }

    Panel MakeCard(int width, int height)
    {
        // ~15% larger than the old HTML sheet footprint
        width = (int)(width * 1.15f);
        height = (int)(height * 1.15f);
        var card = new DoubleBufferedPanel
        {
            Size = new Size(width, height),
            BackColor = NativeTheme.CardBottom,
        };
        card.Paint += (_, e) =>
        {
            var g = e.Graphics;
            using var br = new LinearGradientBrush(card.ClientRectangle, NativeTheme.CardTop, NativeTheme.CardBottom, 160f);
            g.FillRectangle(br, card.ClientRectangle);
            using var pen = new Pen(Color.FromArgb(90, 255, 160, 40), 1.5f);
            g.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
        };
        return card;
    }

    Label MakeTitle(string text, Point? loc = null) => new()
    {
        Text = text,
        Font = NativeTheme.MakeBungee(31),
        ForeColor = NativeTheme.Wumpa,
        Location = loc ?? new Point(36, 28),
        AutoSize = true,
        BackColor = Color.Transparent,
    };

    Button MakePrimaryBtn(string text) => ThemedButton(text, primary: true);
    Button MakeGhostBtn(string text) => ThemedButton(text, primary: false);

    Button ThemedButton(string text, bool primary)
    {
        var btn = new Button
        {
            Text = text,
            Font = NativeTheme.MakeNunito(17, extraBold: true),
            FlatStyle = FlatStyle.Flat,
            Size = new Size(160, 42),
            Cursor = Cursors.Hand,
            ForeColor = primary ? Color.FromArgb(42, 18, 0) : NativeTheme.Sand,
            BackColor = primary ? NativeTheme.Wumpa : Color.Transparent,
        };
        btn.FlatAppearance.BorderSize = primary ? 0 : 1;
        btn.FlatAppearance.BorderColor = Color.FromArgb(90, 255, 200, 120);
        return btn;
    }

    CheckBox MakeCheck(string text)
    {
        return new CheckBox
        {
            Text = text,
            ForeColor = NativeTheme.Sand,
            Font = NativeTheme.MakeNunito(17),
            AutoSize = true,
            BackColor = Color.Transparent,
            FlatStyle = FlatStyle.Flat,
        };
    }

    static void CenterChild(Control host, Control child)
    {
        child.Location = new Point(
            Math.Max(8, (host.ClientSize.Width - child.Width) / 2),
            Math.Max(8, (host.ClientSize.Height - child.Height) / 2));
    }

    void ShowPrep(bool show)
    {
        if (_prepOverlay == null) return;
        _prepOverlay.Visible = show;
        if (show) _prepOverlay.BringToFront();
    }

    void SetPrepProgress(float fraction, string? detail)
    {
        if (_prepBarFill != null)
            _prepBarFill.Width = (int)(Math.Clamp(fraction, 0f, 1f) * 480);
        if (detail != null && _prepDetail != null)
            _prepDetail.Text = detail;
    }

    void CloseSheet()
    {
        if (_sheetHost == null) return;
        _sheetHost.Controls.Clear();
        _sheetHost.Visible = false;
        _activeSheet = null;
    }

    void OpenSheet(Panel card)
    {
        if (_sheetHost == null) return;
        _sheetHost.Controls.Clear();
        _sheetHost.Controls.Add(card);
        CenterChild(_sheetHost, card);
        _sheetHost.Resize -= SheetResize;
        _sheetHost.Resize += SheetResize;
        _activeSheet = card;
        _sheetHost.Visible = true;
        _sheetHost.BringToFront();
    }

    void SheetResize(object? sender, EventArgs e)
    {
        if (_sheetHost != null && _activeSheet != null)
            CenterChild(_sheetHost, _activeSheet);
    }

    void ShowAbout()
    {
        var card = MakeCard(520, 360);
        card.Controls.Add(MakeTitle("About"));
        card.Controls.Add(BodyLabel(
            "Unofficial fan project — not affiliated with Sony, Activision, or Naughty Dog.\n\n" +
            "Unofficial tools for a disc you own. First prepare writes a game folder next to the exe — later Starts reuse that.\n\n" +
            "Prepared files never replace your dump: you still need a valid NTSC-U .cue + .bin (SCUS-94900) every time you play.",
            new Point(36, 110), 520, 170));
        var back = MakeGhostBtn("Back");
        back.Location = new Point(36, 295);
        back.Click += (_, _) => CloseSheet();
        card.Controls.Add(back);
        OpenSheet(card);
    }

    void ShowControls()
    {
        var card = MakeCard(560, 520);
        card.AutoScroll = true;
        card.Controls.Add(MakeTitle("Controls"));
        card.Controls.Add(BodyLabel("Keyboard bindings (player 1). Saved to settings.json.", new Point(36, 110), 520, 28));

        _keyBoxes.Clear();
        var y = 150;
        foreach (var (label, id) in KeyFields)
        {
            card.Controls.Add(new Label
            {
                Text = label,
                ForeColor = NativeTheme.Sand,
                Font = NativeTheme.MakeNunito(17),
                Location = new Point(36, y + 4),
                Size = new Size(160, 28),
                BackColor = Color.Transparent,
            });
            var box = new TextBox
            {
                Location = new Point(210, y),
                Size = new Size(340, 30),
                MaxLength = 32,
                BackColor = Color.FromArgb(30, 20, 12),
                ForeColor = NativeTheme.Sand,
                BorderStyle = BorderStyle.FixedSingle,
                Font = NativeTheme.MakeNunito(16),
            };
            _keyBoxes[id] = box;
            card.Controls.Add(box);
            y += 40;
        }

        var save = MakePrimaryBtn("Save");
        save.Location = new Point(36, y + 16);
        save.Click += (_, _) =>
        {
            var keys = new Dictionary<string, string>();
            foreach (var (_, id) in KeyFields)
                keys[id] = _keyBoxes.TryGetValue(id, out var b) ? b.Text : "";
            Emit(new { type = "saveControls", keys });
            CloseSheet();
        };
        var back = MakeGhostBtn("Back");
        back.Location = new Point(210, y + 16);
        back.Click += (_, _) => CloseSheet();
        card.Controls.Add(save);
        card.Controls.Add(back);
        card.Height = Math.Min(640, y + 90);
        OpenSheet(card);
        Emit(new { type = "getState" });
    }

    void ShowSettings()
    {
        var card = MakeCard(560, 560);
        card.AutoScroll = true;
        card.Controls.Add(MakeTitle("Settings"));
        var y = 110;
        const int labelW = 200;
        const int controlX = 250;

        void AddLabel(string t)
        {
            card.Controls.Add(new Label
            {
                Text = t,
                ForeColor = NativeTheme.Sand,
                Font = NativeTheme.MakeNunito(17),
                Location = new Point(36, y + 2),
                Size = new Size(labelW, 28),
                BackColor = Color.Transparent,
            });
        }

        AddLabel("Master volume");
        _vol = new ThemeSlider
        {
            Minimum = 0,
            Maximum = 100,
            Value = 100,
            Location = new Point(controlX, y + 4),
            Size = new Size(310, 24),
        };
        card.Controls.Add(_vol);
        y += 44;

        _muted = MakeCheck("Muted");
        _muted.Location = new Point(36, y);
        card.Controls.Add(_muted);
        y += 36;

        _fullscreen = MakeCheck("Fullscreen");
        _fullscreen.Location = new Point(36, y);
        card.Controls.Add(_fullscreen);
        y += 36;

        _widescreen = MakeCheck("Widescreen 16:9");
        _widescreen.Location = new Point(36, y);
        card.Controls.Add(_widescreen);
        y += 32;
        card.Controls.Add(Hint("Expands the field of view — does not stretch the image.", ref y));

        AddLabel("Internal resolution");
        _internalRes = MakeCombo(new Point(controlX, y), [
            (1, "Native (1x)"), (2, "2x"), (4, "4x"), (8, "8x (4K)"),
        ]);
        card.Controls.Add(_internalRes);
        y += 42;
        card.Controls.Add(Hint("Applies on next game start. Use 8x on 4K monitors.", ref y));

        AddLabel("Texture filter");
        _texFilter = MakeCombo(new Point(controlX, y), [
            (0, "Off"), (1, "Bilinear"), (2, "Sharp bilinear"), (3, "Soft smooth"),
        ]);
        _texFilter.SelectedIndexChanged += (_, _) => SyncFilterStrengthRow();
        card.Controls.Add(_texFilter);
        y += 42;

        _filterStrengthLabel = new Label
        {
            Text = "Filter strength",
            ForeColor = NativeTheme.Sand,
            Font = NativeTheme.MakeNunito(17),
            Location = new Point(36, y + 2),
            Size = new Size(labelW, 28),
            BackColor = Color.Transparent,
        };
        card.Controls.Add(_filterStrengthLabel);
        _filterStrength = new ThemeSlider
        {
            Minimum = 0,
            Maximum = 100,
            Value = 50,
            Location = new Point(controlX, y + 4),
            Size = new Size(310, 24),
        };
        card.Controls.Add(_filterStrength);
        y += 44;

        _dedither = MakeCheck("Dedither");
        _dedither.Location = new Point(36, y);
        card.Controls.Add(_dedither);
        y += 34;
        _dejitter = MakeCheck("Dejitter");
        _dejitter.Location = new Point(36, y);
        card.Controls.Add(_dejitter);
        y += 34;
        card.Controls.Add(Hint("Texture filters, dedither & dejitter auto-off on menus & cutscenes.", ref y));

        var save = MakePrimaryBtn("Save");
        save.Location = new Point(36, y + 10);
        save.Click += (_, _) =>
        {
            Emit(new
            {
                type = "saveSettings",
                masterVolume = (_vol?.Value ?? 100) / 100.0,
                muted = _muted?.Checked ?? false,
                fullscreen = _fullscreen?.Checked ?? false,
                widescreen = _widescreen?.Checked ?? false,
                internalResolution = _internalRes?.SelectedItem is Kv irKv ? irKv.Value : 4,
                textureFilter = _texFilter?.SelectedItem is Kv tfKv ? tfKv.Value : 0,
                textureFilterStrength = (_filterStrength?.Value ?? 50) / 100.0,
                dedither = _dedither?.Checked ?? false,
                dejitter = _dejitter?.Checked ?? false,
            });
            CloseSheet();
        };
        var back = MakeGhostBtn("Back");
        back.Location = new Point(210, y + 10);
        back.Click += (_, _) => CloseSheet();
        card.Controls.Add(save);
        card.Controls.Add(back);
        card.Height = Math.Min(720, y + 80);
        OpenSheet(card);
        SyncFilterStrengthRow();
        Emit(new { type = "getState" });
    }

    void ShowCheat()
    {
        var card = MakeCard(520, 340);
        card.Controls.Add(MakeTitle("Cheat"));
        var y = 110;
        card.Controls.Add(Hint("Cheat toggles for NTSC-U. Applied while the game is running (also open in-game with the Cheat menu key).", ref y));
        _cheatLives = MakeCheck("Infinite Lives");
        _cheatLives.Location = new Point(36, y);
        card.Controls.Add(_cheatLives);
        y += 40;
        _cheatLevel = MakeCheck("Level Select");
        _cheatLevel.Location = new Point(36, y);
        card.Controls.Add(_cheatLevel);
        y += 40;
        card.Controls.Add(Hint("99 Lives (map) and Instant Save Menu are one-shots available in the in-game Cheat menu.", ref y));

        var save = MakePrimaryBtn("Save");
        save.Location = new Point(36, y + 12);
        save.Click += (_, _) =>
        {
            Emit(new
            {
                type = "saveCheats",
                infiniteLives = _cheatLives?.Checked ?? false,
                levelSelect = _cheatLevel?.Checked ?? false,
            });
            CloseSheet();
        };
        var back = MakeGhostBtn("Back");
        back.Location = new Point(210, y + 12);
        back.Click += (_, _) => CloseSheet();
        card.Controls.Add(save);
        card.Controls.Add(back);
        card.Height = Math.Max(card.Height, y + 80);
        OpenSheet(card);
        Emit(new { type = "getState" });
    }

    void ShowError(JsonElement data)
    {
        ShowPrep(false);
        _canStart = false;
        _status = "";
        _stage.Invalidate();

        var title = data.TryGetProperty("title", out var t) ? t.GetString() ?? "Disc problem" : "Disc problem";
        var problem = data.TryGetProperty("message", out var m) ? m.GetString()
            : data.TryGetProperty("problem", out var p) ? p.GetString() : "Something went wrong.";
        var fix = data.TryGetProperty("fix", out var f) ? f.GetString()
            : "Select a valid Crash Bandicoot .cue that sits next to its .bin.";
        var focus = data.TryGetProperty("focus", out var fo) ? fo.GetString() ?? "other" : "other";
        FocusLabels.TryGetValue(focus, out var focusLabel);
        focusLabel ??= FocusLabels["other"];

        var card = MakeCard(560, 380);
        card.Controls.Add(MakeTitle(title));
        card.Controls.Add(new Label
        {
            Text = focusLabel,
            ForeColor = NativeTheme.Wumpa,
            Font = NativeTheme.MakeNunito(13, extraBold: true),
            Location = new Point(36, 108),
            AutoSize = true,
            BackColor = Color.FromArgb(40, 255, 138, 0),
            Padding = new Padding(10, 4, 10, 4),
        });
        card.Controls.Add(Section("What went wrong", problem ?? "", new Point(36, 148), 540));
        card.Controls.Add(Section("How to fix it", fix ?? "", new Point(36, 228), 540));

        var paths = new List<string>();
        if (data.TryGetProperty("cuePath", out var cp) && cp.GetString() is { Length: > 0 } cue)
            paths.Add(".cue  " + cue);
        if (data.TryGetProperty("binPath", out var bp) && bp.GetString() is { Length: > 0 } bin)
            paths.Add(".bin  " + bin);
        if (paths.Count > 0)
        {
            card.Controls.Add(new Label
            {
                Text = string.Join("\n", paths),
                ForeColor = Color.FromArgb(180, NativeTheme.Sand),
                Font = NativeTheme.MakeNunito(11),
                Location = new Point(28, 244),
                Size = new Size(500, 40),
                BackColor = Color.Transparent,
            });
        }

        var pick = MakePrimaryBtn("Select disc (.cue)");
        pick.Size = new Size(180, 42);
        pick.Location = new Point(36, 310);
        pick.Click += (_, _) =>
        {
            CloseSheet();
            Emit(new { type = "pickDisc" });
        };
        var close = MakeGhostBtn("Close");
        close.Location = new Point(230, 310);
        close.Click += (_, _) => CloseSheet();
        card.Controls.Add(pick);
        card.Controls.Add(close);
        OpenSheet(card);
    }

    void ShowDesktopWarning(JsonElement data)
    {
        var title = data.TryGetProperty("title", out var t) ? t.GetString() ?? "Running from the Desktop" : "Running from the Desktop";
        var message = data.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        var fix = data.TryGetProperty("fix", out var f) ? f.GetString() ?? "" : "";
        var path = data.TryGetProperty("path", out var p) ? p.GetString() : null;

        var card = MakeCard(560, 340);
        card.Controls.Add(MakeTitle(title));
        card.Controls.Add(Section("Why this matters", message, new Point(36, 110), 540));
        card.Controls.Add(Section("What to do", fix, new Point(36, 195), 540));
        if (!string.IsNullOrEmpty(path))
        {
            card.Controls.Add(new Label
            {
                Text = "Current folder  " + path,
                ForeColor = Color.FromArgb(180, NativeTheme.Sand),
                Font = NativeTheme.MakeNunito(13),
                Location = new Point(36, 250),
                Size = new Size(540, 30),
                BackColor = Color.Transparent,
            });
        }
        var got = MakePrimaryBtn("Got it");
        got.Location = new Point(36, 285);
        got.Click += (_, _) => CloseSheet();
        card.Controls.Add(got);
        OpenSheet(card);
    }

    Label BodyLabel(string text, Point loc, int w, int h) => new()
    {
        Text = text,
        ForeColor = Color.FromArgb(210, NativeTheme.Sand),
        Font = NativeTheme.MakeNunito(16),
        Location = loc,
        Size = new Size(w, h),
        BackColor = Color.Transparent,
    };

    Label Hint(string text, ref int y)
    {
        var lbl = new Label
        {
            Text = text,
            ForeColor = Color.FromArgb(160, NativeTheme.Sand),
            Font = NativeTheme.MakeNunito(14),
            Location = new Point(36, y),
            Size = new Size(540, 40),
            BackColor = Color.Transparent,
        };
        y += 44;
        return lbl;
    }

    Panel Section(string heading, string body, Point loc, int width)
    {
        var p = new Panel
        {
            Location = loc,
            Size = new Size(width, 68),
            BackColor = Color.FromArgb(70, 0, 0, 0),
        };
        p.Controls.Add(new Label
        {
            Text = heading.ToUpperInvariant(),
            ForeColor = Color.FromArgb(120, NativeTheme.Sand),
            Font = NativeTheme.MakeNunito(11, extraBold: true),
            Location = new Point(12, 8),
            AutoSize = true,
            BackColor = Color.Transparent,
        });
        p.Controls.Add(new Label
        {
            Text = body,
            ForeColor = Color.FromArgb(230, NativeTheme.Sand),
            Font = NativeTheme.MakeNunito(13),
            Location = new Point(12, 28),
            Size = new Size(width - 24, 36),
            BackColor = Color.Transparent,
        });
        return p;
    }

    ComboBox MakeCombo(Point loc, (int Value, string Text)[] items)
    {
        var cb = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = loc,
            Size = new Size(310, 32),
            Font = NativeTheme.MakeNunito(16),
            BackColor = Color.FromArgb(30, 20, 12),
            ForeColor = NativeTheme.Sand,
            FlatStyle = FlatStyle.Flat,
            DisplayMember = "Text",
            ValueMember = "Value",
            DataSource = items.Select(i => new Kv(i.Value, i.Text)).ToList(),
        };
        return cb;
    }

    void SyncFilterStrengthRow()
    {
        var on = _texFilter?.SelectedValue is int v && v != 0;
        if (_filterStrength != null) _filterStrength.Visible = on;
        if (_filterStrengthLabel != null) _filterStrengthLabel.Visible = on;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _anim.Stop();
            _anim.Dispose();
            _map?.Dispose();
            _fontCrash?.Dispose();
            _fontRecomp?.Dispose();
            _fontMenu?.Dispose();
            _fontMenuHot?.Dispose();
            _fontChip?.Dispose();
            _fontFooter?.Dispose();
            _fontVer?.Dispose();
            _fontInfo?.Dispose();
            _fontMark?.Dispose();
            NativeTheme.DisposeFonts();
        }
        base.Dispose(disposing);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    readonly record struct MenuHit(string Label, Rectangle Bounds, int Index);
    sealed record Kv(int Value, string Text);

    sealed class ThemeSlider : Control
    {
        int _min;
        int _max = 100;
        int _value;
        bool _dragging;

        public ThemeSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Height = 24;
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Minimum
        {
            get => _min;
            set { _min = value; Invalidate(); }
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Maximum
        {
            get => _max;
            set { _max = Math.Max(value, _min + 1); Invalidate(); }
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Value
        {
            get => _value;
            set
            {
                var v = Math.Clamp(value, _min, _max);
                if (v == _value) return;
                _value = v;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public event EventHandler? ValueChanged;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var trackY = Height / 2 - 3;
            var track = new Rectangle(6, trackY, Math.Max(1, Width - 12), 6);
            using (var bg = new SolidBrush(Color.FromArgb(50, 255, 255, 255)))
            using (var path = RoundRectPath(track, 3))
                g.FillPath(bg, path);

            var t = _max == _min ? 0f : (_value - _min) / (float)(_max - _min);
            var fillW = (int)(track.Width * t);
            if (fillW > 0)
            {
                var fill = new Rectangle(track.X, track.Y, Math.Max(fillW, 1), track.Height);
                using var orange = new LinearGradientBrush(fill, NativeTheme.Wumpa, NativeTheme.WumpaHot, 0f);
                if (fillW < 8)
                    g.FillRectangle(orange, fill);
                else
                {
                    using var path = RoundRectPath(fill, 3);
                    g.FillPath(orange, path);
                }
            }

            var thumbX = track.X + fillW;
            var thumb = new Rectangle(thumbX - 8, Height / 2 - 8, 16, 16);
            using (var thumbBr = new SolidBrush(NativeTheme.Sand))
                g.FillEllipse(thumbBr, thumb);
            using (var ring = new Pen(NativeTheme.Wumpa, 2f))
                g.DrawEllipse(ring, thumb);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _dragging = true;
            Capture = true;
            SetFromMouse(e.X);
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging) SetFromMouse(e.X);
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            _dragging = false;
            Capture = false;
            base.OnMouseUp(e);
        }

        void SetFromMouse(int x)
        {
            var trackLeft = 6;
            var trackW = Math.Max(1, Width - 12);
            var t = Math.Clamp((x - trackLeft) / (float)trackW, 0f, 1f);
            Value = _min + (int)Math.Round(t * (_max - _min));
        }

        static GraphicsPath RoundRectPath(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    sealed class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
        }
    }
}

static class NativeUiExtensions
{
    public static void SetStylePublic(this Panel panel)
    {
        typeof(Control).GetMethod("SetStyle",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(panel, [ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true]);
        typeof(Control).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(panel, true);
    }

    public static Rectangle InflateCopy(this Rectangle r, int x, int y)
    {
        var c = r;
        c.Inflate(x, y);
        return c;
    }
}
