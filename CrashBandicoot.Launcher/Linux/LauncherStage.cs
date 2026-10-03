using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using static CrashBandicoot.Launcher.Linux.LinuxTheme;

namespace CrashBandicoot.Launcher.Linux;

/// <summary>
/// Main menu screen, painted like <c>NativeLauncherUi</c> on Windows: jungle
/// background, world map, brand, bobbing ? crate, menu column and footer.
/// Split into layers so the crate animation and hover changes only repaint
/// their own area (the launcher may run on software rendering).
/// </summary>
sealed class LauncherStage : Panel
{
    public const int MenuCount = 6;
    public const int FocusInfo = 6;
    public const int FocusChip = 7;
    const int FocusCount = 8;
    const int FootH = 78;
    const int CrateSize = 220;
    const int CrateMargin = 30;

    static readonly string[] MenuLabels = ["START GAME", "CONTROLS", "SETTINGS", "MODS", "CHEAT", "EXIT"];

    readonly Backdrop _backdrop;
    readonly CrateView _crate = new();
    readonly MenuView _menu;
    readonly Footer _footer;

    int _focusIndex;
    bool _canStart;
    string _status = "";
    string _statusKind = "";
    string _discPath = "";
    string _version = "";

    /// <summary>Raised for a click, Enter/Space or gamepad confirm on a menu entry, info or the disc chip.</summary>
    public event Action<int>? Activated;

    public LauncherStage(Bitmap? map)
    {
        _backdrop = new Backdrop(map);
        _menu = new MenuView(this);
        _footer = new Footer(this);
        Children.Add(_backdrop);
        Children.Add(_crate);
        Children.Add(_menu);
        Children.Add(_footer);
        Background = Brush(Night);
    }

    public bool CanStart => _canStart;
    public int FocusIndex => _focusIndex;

    public void SetState(bool canStart, string status, string statusKind, string discPath, string version)
    {
        _canStart = canStart;
        _status = status;
        _statusKind = statusKind;
        _discPath = discPath;
        _version = version;
        EnsureFocusValid();
        _menu.InvalidateVisual();
        _footer.InvalidateVisual();
    }

    /// <summary>Advance the crate bob (call from the ~30 Hz UI timer).</summary>
    public void Animate()
    {
        _crate.Phase += 0.065f;
        if (_crate.Phase > MathF.PI * 2) _crate.Phase -= MathF.PI * 2;
        _crate.InvalidateVisual();
    }

    public void MoveFocus(int delta)
    {
        if (delta == 0) return;
        var next = _focusIndex;
        for (var step = 0; step < FocusCount + 1; step++)
        {
            next = (next + delta + FocusCount) % FocusCount;
            if (!IsFocusSelectable(next)) continue;
            SetFocusIndex(next);
            return;
        }
    }

    public void ActivateFocused() => Activate(_focusIndex);

    void Activate(int index)
    {
        if (index == 0 && !_canStart) return;
        Activated?.Invoke(index);
    }

    void SetFocusIndex(int index)
    {
        if (_focusIndex == index) return;
        _focusIndex = index;
        _menu.InvalidateVisual();
        _footer.InvalidateVisual();
    }

    bool IsFocusSelectable(int index) => index switch
    {
        0 => _canStart,
        >= 1 and < MenuCount => true,
        FocusInfo or FocusChip => true,
        _ => false,
    };

    void EnsureFocusValid()
    {
        if (IsFocusSelectable(_focusIndex)) return;
        for (var i = 0; i < FocusCount; i++)
        {
            if (!IsFocusSelectable(i)) continue;
            _focusIndex = i;
            return;
        }
        _focusIndex = 1;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var w = finalSize.Width;
        var h = finalSize.Height;
        var contentH = h - FootH;
        _backdrop.Arrange(new Rect(finalSize));

        var brandY = Math.Max(36, contentH / 2 - 180);
        _backdrop.BrandOrigin = new Point(40, brandY);
        _crate.Arrange(new Rect(40 - CrateMargin, brandY + 120 - CrateMargin,
            CrateSize + CrateMargin * 2, CrateSize + CrateMargin * 2));

        _menu.Arrange(new Rect(w - 340, contentH / 2 - 190, 330, MenuCount * MenuView.Row));
        _footer.Arrange(new Rect(0, h - FootH, w, FootH));
        return finalSize;
    }

    /// <summary>Background, map and brand: static, repainted only on resize.</summary>
    sealed class Backdrop(Bitmap? map) : Control
    {
        Point _brandOrigin;

        public Point BrandOrigin
        {
            get => _brandOrigin;
            set
            {
                if (_brandOrigin == value) return;
                _brandOrigin = value;
                InvalidateVisual();
            }
        }

        public override void Render(DrawingContext g)
        {
            var bounds = new Rect(Bounds.Size);
            DrawBackground(g, bounds);
            DrawMap(g, bounds);
            DrawBrand(g);
        }

        static void DrawBackground(DrawingContext g, Rect bounds)
        {
            g.FillRectangle(GdiLinear(bounds, 165, (0, JungleTop), (0.45, Night), (1, JungleWarm)), bounds);

            // Warm / green radial glows
            var w = bounds.Width;
            var h = bounds.Height;
            DrawGlow(g, new Rect(-w / 5, h / 2, w / 2, h), Argb(55, 255, 138, 0));
            DrawGlow(g, new Rect(w * 2 / 3, -h / 8, w / 2, h / 2), Argb(70, 40, 120, 70));

            // Diagonal scanlines
            var pen = new Pen(Brush(8, 255, 180, 40));
            for (var i = -h; i < w + h; i += 15)
                g.DrawLine(pen, new Point(i, 0), new Point(i + h, h));
        }

        static void DrawGlow(DrawingContext g, Rect ellipse, Color center)
        {
            var brush = new RadialGradientBrush
            {
                Center = RelativePoint.Center,
                GradientOrigin = RelativePoint.Center,
                RadiusX = RelativeScalar.Middle,
                RadiusY = RelativeScalar.Middle,
                GradientStops =
                {
                    new GradientStop(center, 0),
                    new GradientStop(A(0, center), 1),
                },
            };
            g.DrawEllipse(brush, null, ellipse);
        }

        void DrawMap(DrawingContext g, Rect bounds)
        {
            if (map == null) return;
            var size = map.Size;
            var maxW = Math.Min((int)(bounds.Width * 0.58), 520);
            var maxH = (int)(bounds.Height * 0.70);
            var scale = Math.Min(maxW / size.Width, maxH / size.Height);
            var dw = (int)(size.Width * scale);
            var dh = (int)(size.Height * scale);
            var x = (bounds.Width - dw) / 2;
            var y = (int)(bounds.Height * 0.46) - dh / 2 - 20;

            var src = new Rect(size);
            // Soft drop shadow
            using (g.PushOpacity(0.35))
                g.DrawImage(map, src, new Rect(x + 6, y + 14, dw, dh));
            g.DrawImage(map, src, new Rect(x, y, dw, dh));
        }

        void DrawBrand(DrawingContext g)
        {
            DrawOutlinedText(g, "CRASH", 64, Wumpa, Argb(255, 58, 21, 0), _brandOrigin, 3);
            DrawOutlinedText(g, "RECOMPILED", 28, Danger, Argb(255, 58, 0, 0), _brandOrigin + new Vector(0, 66), 2);
        }

        static void DrawOutlinedText(DrawingContext g, string text, double size, Color fill, Color stroke,
            Point origin, double strokeWidth)
        {
            var geo = Text(text, BungeeFace, size, Brushes.White).BuildGeometry(origin);
            if (geo == null) return;
            using (g.PushTransform(Matrix.CreateTranslation(0, 6)))
                g.DrawGeometry(Brush(120, 0, 0, 0), null, geo);
            using (g.PushTransform(Matrix.CreateTranslation(0, 4)))
                g.DrawGeometry(Brush(200, 90, 34, 0), null, geo);
            g.DrawGeometry(null, new Pen(Brush(stroke), strokeWidth, lineJoin: PenLineJoin.Round), geo);
            g.DrawGeometry(Brush(fill), null, geo);
        }
    }

    /// <summary>The bobbing ? crate. Drawn with a margin so rotation/bob/shadow stay inside.</summary>
    sealed class CrateView : Control
    {
        public float Phase;

        static readonly (double A, double B, Color C)[] Bands =
        [
            (0.00, 0.21, Color.FromRgb(208, 137, 58)),
            (0.21, 0.24, Color.FromRgb(122, 64, 16)),
            (0.24, 0.45, Color.FromRgb(196, 122, 42)),
            (0.45, 0.48, Color.FromRgb(122, 64, 16)),
            (0.48, 0.69, Color.FromRgb(208, 142, 63)),
            (0.69, 0.72, Color.FromRgb(122, 64, 16)),
            (0.72, 1.00, Color.FromRgb(184, 111, 36)),
        ];

        public CrateView() => IsHitTestVisible = false;

        public override void Render(DrawingContext g)
        {
            var bob = MathF.Sin(Phase) * 8f;
            var rot = MathF.Sin(Phase) * 2f;
            var cx = CrateMargin + CrateSize / 2.0;
            var cy = CrateMargin + CrateSize / 2.0 + bob;

            var m = Matrix.CreateTranslation(-CrateSize / 2.0, -CrateSize / 2.0)
                    * Matrix.CreateRotation(rot * Math.PI / 180.0)
                    * Matrix.CreateTranslation(cx, cy);
            using var _ = g.PushTransform(m);

            var r = new Rect(0, 0, CrateSize, CrateSize);
            g.FillRectangle(Brush(120, 0, 0, 0), new Rect(8, 14, r.Width, r.Height));
            g.FillRectangle(Brush(CrateCore), r);

            // Planks
            var inset = r.Deflate(20);
            foreach (var (a, b, c) in Bands)
            {
                var y0 = inset.Y + (int)(inset.Height * a);
                var y1 = inset.Y + (int)(inset.Height * b);
                g.FillRectangle(Brush(c), new Rect(inset.X, y0, inset.Width, Math.Max(1, y1 - y0)));
            }

            // X beams
            DrawBeam(g, r, 45);
            DrawBeam(g, r, -45);

            // Frame
            g.DrawRectangle(null, new Pen(Brush(WoodFrame), 20), r.Deflate(10));
            g.DrawRectangle(null, new Pen(Brush(CrateCore), 2), r.Deflate(21));

            // Bolts
            DrawBolt(g, 13, 13);
            DrawBolt(g, r.Width - 26, 13);
            DrawBolt(g, 13, r.Height - 26);
            DrawBolt(g, r.Width - 26, r.Height - 26);

            // ?
            var mark = Text("?", BungeeFace, 112, Brushes.White)
                .BuildGeometry(new Point(r.Width / 2 - 36, r.Height / 2 - 62));
            if (mark != null)
            {
                g.DrawGeometry(null, new Pen(Brush(MarkRed), 8, lineJoin: PenLineJoin.Round), mark);
                g.DrawGeometry(Brush(MarkYellow), null, mark);
            }
        }

        static void DrawBeam(DrawingContext g, Rect crate, double angle)
        {
            var m = Matrix.CreateRotation(angle * Math.PI / 180.0)
                    * Matrix.CreateTranslation(crate.Width / 2, crate.Height / 2);
            using var _ = g.PushTransform(m);
            var beam = new Rect(-13, -crate.Height / 2 - 10, 26, crate.Height + 20);
            g.DrawRectangle(
                GdiLinear(beam, 0, (0, Color.FromRgb(138, 74, 18)), (1, Color.FromRgb(224, 160, 80))),
                new Pen(Brush(90, 30, 12, 0), 2),
                beam);
        }

        static void DrawBolt(DrawingContext g, double x, double y)
        {
            var r = new Rect(x, y, 13, 13);
            g.DrawEllipse(
                GdiLinear(r, 45, (0, Color.FromRgb(232, 234, 236)), (1, Color.FromRgb(58, 62, 68))),
                new Pen(Brush(90, 0, 0, 0)),
                r);
        }
    }

    /// <summary>START GAME … EXIT column.</summary>
    sealed class MenuView : Control
    {
        public const int Row = 62;
        readonly LauncherStage _stage;
        int _hover = -1;

        public MenuView(LauncherStage stage) => _stage = stage;

        int HitIndex(Point p)
        {
            if (p.X < 0 || p.X > 300) return -1;
            var i = (int)(p.Y / Row);
            if (i < 0 || i >= MenuCount || p.Y - i * Row > 52) return -1;
            return i == 0 && !_stage._canStart ? -1 : i;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var i = HitIndex(e.GetPosition(this));
            Cursor = i >= 0 ? HandCursor : Cursor.Default;
            if (i == _hover) return;
            _hover = i;
            if (i >= 0) _stage.SetFocusIndex(i);
            InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _hover = -1;
            InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            var i = HitIndex(e.GetPosition(this));
            if (i < 0) return;
            e.Handled = true;
            _stage.Activate(i);
        }

        public override void Render(DrawingContext g)
        {
            // Transparent fill keeps the whole column hit-testable for hover.
            g.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
            for (var i = 0; i < MenuCount; i++)
            {
                var disabled = i == 0 && !_stage._canStart;
                var hot = !disabled && (_hover == i || _stage._focusIndex == i);
                Color color;
                if (disabled) color = Argb(128, 122, 117, 104);
                else if (hot) color = WumpaHot;
                else if (i == 0) color = Wumpa;
                else color = Sand;

                var origin = new Point(hot ? 10 : 0, i * Row);
                var geo = Text(MenuLabels[i], BungeeFace, 38, Brushes.White).BuildGeometry(origin);
                if (geo == null) continue;
                using (g.PushTransform(Matrix.CreateTranslation(0, 2)))
                    g.DrawGeometry(Brush(180, 0, 0, 0), null, geo);
                g.DrawGeometry(Brush(color), null, geo);
            }
        }
    }

    /// <summary>Footer strip: info button, Select disc chip, status/path and version.</summary>
    sealed class Footer : Control
    {
        readonly LauncherStage _stage;
        static readonly Rect InfoRect = new(28, 16, 36, 36);
        static readonly Rect ChipRect = new(76, 17, 168, 34);
        bool _hoverInfo;
        bool _hoverChip;

        public Footer(LauncherStage stage) => _stage = stage;

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var p = e.GetPosition(this);
            var info = InfoRect.Contains(p);
            var chip = ChipRect.Contains(p);
            Cursor = info || chip ? HandCursor : Cursor.Default;
            if (info == _hoverInfo && chip == _hoverChip) return;
            _hoverInfo = info;
            _hoverChip = chip;
            if (info) _stage.SetFocusIndex(FocusInfo);
            else if (chip) _stage.SetFocusIndex(FocusChip);
            InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _hoverInfo = _hoverChip = false;
            InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            var p = e.GetPosition(this);
            if (InfoRect.Contains(p)) { e.Handled = true; _stage.Activate(FocusInfo); }
            else if (ChipRect.Contains(p)) { e.Handled = true; _stage.Activate(FocusChip); }
        }

        public override void Render(DrawingContext g)
        {
            var w = Bounds.Width;
            var strip = new Rect(0, 0, w, FootH);
            g.FillRectangle(GdiLinear(strip, 90, (0, Argb(0, 0, 0, 0)), (1, Argb(90, 0, 0, 0))), strip);
            g.DrawLine(new Pen(Brush(30, 255, 200, 120)), new Point(0, 0.5), new Point(w, 0.5));

            // Info button, glyph centered on its outline bounds
            var infoHot = _hoverInfo || _stage._focusIndex == FocusInfo;
            g.DrawEllipse(infoHot ? Brush(90, 255, 138, 0) : Brush(45, 255, 138, 0),
                new Pen(Brush(140, 255, 180, 60), 2), InfoRect);
            var glyph = Text("i", BungeeFace, 18, Brushes.White).BuildGeometry(default);
            if (glyph != null)
            {
                var gb = glyph.Bounds;
                var dx = InfoRect.Center.X - gb.Center.X;
                var dy = InfoRect.Center.Y - gb.Center.Y;
                using (g.PushTransform(Matrix.CreateTranslation(dx, dy)))
                    g.DrawGeometry(Brush(WumpaHot), null, glyph);
            }

            // Disc chip
            var chipHot = _hoverChip || _stage._focusIndex == FocusChip;
            g.DrawRectangle(chipHot ? Brush(70, 255, 138, 0) : Brush(30, 255, 138, 0),
                new Pen(Brush(90, 255, 180, 60)), ChipRect, 17, 17);
            var chipText = Text("Select disc", NunitoFace, 15, Brush(Sand));
            g.DrawText(chipText, new Point(
                ChipRect.X + (ChipRect.Width - chipText.Width) / 2,
                ChipRect.Y + (ChipRect.Height - chipText.Height) / 2));

            // Status + path. Windows draws these with GDI TextRenderer, which ignores
            // alpha, so they are opaque there too.
            var textX = ChipRect.Right + 14;
            var textW = Math.Max(40, w - textX - 100);
            if (!string.IsNullOrEmpty(_stage._status))
            {
                var sc = _stage._statusKind == "error" ? Color.FromRgb(255, 143, 143)
                    : _stage._statusKind == "ok" ? Ok
                    : Sand;
                g.DrawText(Line(_stage._status, Brush(sc), textW), new Point(textX, 6));
            }

            var pathText = string.IsNullOrEmpty(_stage._discPath) ? "(none)" : _stage._discPath;
            g.DrawText(Line(pathText, Brush(Sand), textW), new Point(textX, ChipRect.Y + 5));

            var ver = Text(_stage._version, NunitoFace, 26, Brush(Sand));
            g.DrawText(ver, new Point(w - 14 - ver.Width, FootH - 44 + (32 - ver.Height) / 2));
        }

        static FormattedText Line(string text, IBrush brush, double width)
        {
            var ft = Text(text, NunitoFace, 15, brush);
            ft.MaxTextWidth = width;
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
            return ft;
        }
    }
}
