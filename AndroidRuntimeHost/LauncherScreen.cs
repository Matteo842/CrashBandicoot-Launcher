using Android.App;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace CrashBandicoot.AndroidRuntime;

/// <summary>
/// Canvas-based Android version of the desktop launcher. The desktop surface is
/// also custom-painted, so keeping the same composition here avoids two drifting
/// widget layouts and lets both hosts share the same visual language.
/// </summary>
sealed class LauncherScreen : View
{
    const float FooterUiScale = 1.30f;

    static readonly string[] MenuLabels =
    [
        "START GAME", "CONTROLS", "SETTINGS", "GPU LAB", "CHEAT", "EXIT",
    ];

    readonly Activity _activity;
    readonly Paint _paint = new(PaintFlags.AntiAlias | PaintFlags.FilterBitmap);
    readonly Paint _text = new(PaintFlags.AntiAlias | PaintFlags.SubpixelText);
    readonly Typeface _displayFont;
    readonly Typeface _bodyFont;
    readonly Typeface _bodyBoldFont;
    readonly Bitmap? _map;
    readonly string _versionLabel;
    readonly RectF[] _menuBounds = new RectF[MenuLabels.Length];
    readonly RectF _crateBounds = new();
    readonly RectF _infoBounds = new();
    readonly RectF _chipBounds = new();
    readonly RectF _mapBounds = new();
    Dialog? _aboutDialog;
    bool _portrait;
    float _brandX;
    float _brandY;
    float _brandSize;
    float _recompSize;
    float _menuTextSize;

    readonly Color _night = Color.Rgb(6, 16, 24);
    readonly Color _jungleTop = Color.Rgb(10, 40, 24);
    readonly Color _jungleWarm = Color.Rgb(18, 10, 8);
    readonly Color _wumpa = Color.Rgb(255, 138, 0);
    readonly Color _wumpaHot = Color.Rgb(255, 176, 32);
    readonly Color _sand = Color.Rgb(244, 228, 188);
    readonly Color _danger = Color.Rgb(255, 59, 59);
    readonly Color _ok = Color.Rgb(125, 255, 154);

    bool _ready;
    bool _busy;
    int _pressed = -1;
    float _unit = 1f;
    float _footerTop;
    string _status = "Select a legal Crash Bandicoot CUE/BIN dump";
    string _statusKind = "";
    string _discLine = "(none)";

    public event Action? SelectDiscRequested;
    public event Action? StartGameRequested;
    public event Action? SettingsRequested;
    public event Action? GpuLabRequested;

    public LauncherScreen(Activity activity) : base(activity)
    {
        _activity = activity;
        _displayFont = LoadTypeface("Fonts/Bungee-Regular.ttf", Typeface.DefaultBold!);
        _bodyFont = LoadTypeface("Fonts/Nunito-SemiBold.ttf", Typeface.Default!);
        _bodyBoldFont = LoadTypeface("Fonts/Nunito-ExtraBold.ttf", Typeface.DefaultBold!);
        _versionLabel = ReadVersionLabel(activity);
        for (var i = 0; i < _menuBounds.Length; i++)
            _menuBounds[i] = new RectF();

        try
        {
            using var stream = activity.Assets?.Open("Images/world_map.png");
            _map = stream == null ? null : BitmapFactory.DecodeStream(stream);
        }
        catch
        {
            _map = null;
        }

        SetBackgroundColor(_night);
        Focusable = true;
        Clickable = true;
    }

    public void SetBusy(bool busy, string? detail = null)
    {
        _busy = busy;
        if (busy)
        {
            _status = "Preparing game";
            _statusKind = "busy";
            _discLine = detail ?? "Checking the disc and recompiled runtime…";
        }
        Invalidate();
    }

    public void ShowDisc(bool ready, string title, string detail)
    {
        _busy = false;
        _ready = ready;
        _status = title;
        _statusKind = ready ? "ok" : "error";
        _discLine = detail.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? detail;
        Invalidate();
    }

    public void ShowError(string message)
    {
        _busy = false;
        _status = "Launch failed";
        _statusKind = "error";
        _discLine = message;
        Invalidate();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (Width <= 0 || Height <= 0) return;

        LayoutScene(Width, Height);
        DrawBackground(canvas, Width, Height);
        DrawMap(canvas);
        DrawBrand(canvas);
        DrawCrate(canvas);
        DrawMenu(canvas);
        DrawFooter(canvas, Width, Height);
    }

    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        if (w > 0 && h > 0)
        {
            LayoutScene(w, h);
            Invalidate();
        }
    }

    void LayoutScene(float width, float height)
    {
        _portrait = height > width * 1.05f;
        if (_portrait)
            LayoutPortrait(width, height);
        else
            LayoutLandscape(width, height);

        var footerY = _footerTop + 16f * FooterUiScale * _unit;
        _infoBounds.Set(28f * FooterUiScale * _unit, footerY,
            64f * FooterUiScale * _unit, footerY + 36f * FooterUiScale * _unit);
        var chipRight = (_portrait ? 292f : 244f) * FooterUiScale * _unit;
        _chipBounds.Set(76f * FooterUiScale * _unit, footerY + FooterUiScale * _unit,
            chipRight, footerY + 35f * FooterUiScale * _unit);
    }

    void LayoutLandscape(float width, float height)
    {
        _unit = Math.Clamp(height / 760f, 0.75f, 1.65f);
        var footerHeight = 78f * FooterUiScale * _unit;
        _footerTop = height - footerHeight;
        var contentHeight = _footerTop;

        _brandX = 40f * _unit;
        _brandY = Math.Max(56f * _unit, contentHeight * 0.18f);
        _brandSize = 64f * _unit;
        _recompSize = 28f * _unit;

        var crateSize = Math.Min(width * 0.17f, contentHeight * 0.40f);
        _crateBounds.Set(_brandX, _brandY + 165f * _unit,
            _brandX + crateSize, _brandY + 165f * _unit + crateSize);

        LayoutMap(width * 0.505f, contentHeight * 0.49f,
            Math.Min(width * 0.43f, contentHeight * 1.02f), contentHeight * 0.72f);

        _menuTextSize = 45.6f * _unit;
        var menuX = width * 0.72f;
        var menuY = Math.Max(54f * _unit, contentHeight * 0.15f);
        var menuRow = 69f * _unit;
        for (var i = 0; i < _menuBounds.Length; i++)
        {
            var top = menuY + i * menuRow;
            _menuBounds[i].Set(menuX - 12f * _unit, top,
                width - 24f * _unit, top + 62f * _unit);
        }
    }

    void LayoutPortrait(float width, float height)
    {
        _unit = Math.Clamp(Math.Min(width / 400f, height / 900f), 0.70f, 1.30f);
        var footerHeight = 92f * FooterUiScale * _unit;
        _footerTop = height - footerHeight;
        var contentHeight = _footerTop;

        _brandX = 22f * _unit;
        _brandY = Math.Max(36f * _unit, contentHeight * 0.035f);
        _brandSize = 48f * _unit;
        _recompSize = 20f * _unit;

        var crateSize = Math.Min(width * 0.20f, contentHeight * 0.16f);
        _crateBounds.Set(width - 22f * _unit - crateSize, _brandY + 6f * _unit,
            width - 22f * _unit, _brandY + 6f * _unit + crateSize);

        var mapTop = _brandY + 112f * _unit;
        var mapHeight = contentHeight * 0.30f;
        LayoutMap(width * 0.5f, mapTop + mapHeight * 0.5f, width * 0.82f, mapHeight);

        _menuTextSize = 34f * _unit;
        var menuX = 22f * _unit;
        var menuY = Math.Min(_mapBounds.Bottom + 16f * _unit, contentHeight * 0.48f);
        var available = Math.Max(48f * _unit, contentHeight - menuY - 10f * _unit);
        var menuRow = Math.Min(52f * _unit, available / MenuLabels.Length);
        for (var i = 0; i < _menuBounds.Length; i++)
        {
            var top = menuY + i * menuRow;
            _menuBounds[i].Set(menuX, top, width - 18f * _unit, top + menuRow - 4f * _unit);
        }
    }

    void LayoutMap(float centerX, float centerY, float maxWidth, float maxHeight)
    {
        if (_map == null)
        {
            _mapBounds.Set(centerX - maxWidth / 2f, centerY - maxHeight / 2f,
                centerX + maxWidth / 2f, centerY + maxHeight / 2f);
            return;
        }

        var scale = Math.Min(maxWidth / _map.Width, maxHeight / _map.Height);
        var drawWidth = _map.Width * scale;
        var drawHeight = _map.Height * scale;
        _mapBounds.Set(
            centerX - drawWidth / 2f,
            centerY - drawHeight / 2f,
            centerX + drawWidth / 2f,
            centerY + drawHeight / 2f);
    }

    void DrawBackground(Canvas canvas, float width, float height)
    {
        using (var shader = new LinearGradient(
                   width, 0, 0, height, _jungleTop, _jungleWarm, Shader.TileMode.Clamp))
        {
            _paint.SetShader(shader);
            canvas.DrawRect(0, 0, width, height, _paint);
            _paint.SetShader(null);
        }

        _paint.Color = Color.Argb(145, _night.R, _night.G, _night.B);
        canvas.DrawRect(0, 0, width, height, _paint);

        using (var warm = new RadialGradient(
                   0, height, width * 0.48f,
                   Color.Argb(70, 255, 138, 0), Color.Transparent, Shader.TileMode.Clamp))
        {
            _paint.SetShader(warm);
            canvas.DrawRect(0, height * 0.35f, width * 0.55f, height, _paint);
            _paint.SetShader(null);
        }

        using (var green = new RadialGradient(
                   width, 0, width * 0.40f,
                   Color.Argb(62, 40, 120, 70), Color.Transparent, Shader.TileMode.Clamp))
        {
            _paint.SetShader(green);
            canvas.DrawRect(width * 0.55f, 0, width, height * 0.65f, _paint);
            _paint.SetShader(null);
        }

        _paint.Color = Color.Argb(13, 255, 180, 40);
        _paint.StrokeWidth = Math.Max(1f, _unit);
        var step = 15f * _unit;
        for (var x = -height; x < width + height; x += step)
            canvas.DrawLine(x, 0, x + height, height, _paint);
    }

    void DrawMap(Canvas canvas)
    {
        if (_map == null || _mapBounds.Width() <= 0) return;

        _paint.Alpha = 88;
        var shadow = new RectF(_mapBounds);
        shadow.Offset(7f * _unit, 14f * _unit);
        canvas.DrawBitmap(_map, null, shadow, _paint);
        _paint.Alpha = 255;
        canvas.DrawBitmap(_map, null, _mapBounds, _paint);
    }

    void DrawBrand(Canvas canvas)
    {
        DrawOutlinedText(canvas, "CRASH", _displayFont, _brandSize,
            _wumpa, Color.Rgb(58, 21, 0), _brandX, _brandY, 3f * _unit, true);
        DrawOutlinedText(canvas, "RECOMPILED", _displayFont, _recompSize,
            _danger, Color.Rgb(58, 0, 0), _brandX, _brandY + _brandSize * 1.40f, 2f * _unit, true);
    }

    void DrawCrate(Canvas canvas)
    {
        var r = _crateBounds;
        var size = r.Width();
        if (size <= 0) return;

        _paint.SetStyle(Paint.Style.Fill);
        _paint.Color = Color.Argb(125, 0, 0, 0);
        canvas.DrawRect(r.Left + 8f * _unit, r.Top + 14f * _unit,
            r.Right + 8f * _unit, r.Bottom + 14f * _unit, _paint);
        _paint.Color = Color.Rgb(58, 32, 8);
        canvas.DrawRect(r, _paint);

        var inset = new RectF(r);
        inset.Inset(20f * _unit, 20f * _unit);
        var bands = new (float Start, float End, Color Color)[]
        {
            (0f, .21f, Color.Rgb(208, 137, 58)),
            (.21f, .24f, Color.Rgb(122, 64, 16)),
            (.24f, .45f, Color.Rgb(196, 122, 42)),
            (.45f, .48f, Color.Rgb(122, 64, 16)),
            (.48f, .69f, Color.Rgb(208, 142, 63)),
            (.69f, .72f, Color.Rgb(122, 64, 16)),
            (.72f, 1f, Color.Rgb(184, 111, 36)),
        };
        foreach (var band in bands)
        {
            _paint.Color = band.Color;
            canvas.DrawRect(inset.Left, inset.Top + inset.Height() * band.Start,
                inset.Right, inset.Top + inset.Height() * band.End, _paint);
        }

        DrawBeam(canvas, r, 45f);
        DrawBeam(canvas, r, -45f);

        _paint.SetStyle(Paint.Style.Stroke);
        _paint.StrokeWidth = 20f * _unit;
        _paint.Color = Color.Rgb(184, 111, 36);
        var frame = new RectF(r);
        frame.Inset(10f * _unit, 10f * _unit);
        canvas.DrawRect(frame, _paint);
        _paint.StrokeWidth = 2f * FooterUiScale * _unit;
        _paint.Color = Color.Rgb(58, 32, 8);
        var inner = new RectF(r);
        inner.Inset(21f * _unit, 21f * _unit);
        canvas.DrawRect(inner, _paint);
        _paint.SetStyle(Paint.Style.Fill);

        var boltRadius = Math.Max(5f * _unit, size * 0.027f);
        DrawBolt(canvas, r.Left + 13f * _unit + boltRadius, r.Top + 13f * _unit + boltRadius, boltRadius);
        DrawBolt(canvas, r.Right - 13f * _unit - boltRadius, r.Top + 13f * _unit + boltRadius, boltRadius);
        DrawBolt(canvas, r.Left + 13f * _unit + boltRadius, r.Bottom - 13f * _unit - boltRadius, boltRadius);
        DrawBolt(canvas, r.Right - 13f * _unit - boltRadius, r.Bottom - 13f * _unit - boltRadius, boltRadius);

        DrawCenteredOutlinedText(canvas, "?", _displayFont, size * 0.58f,
            Color.Rgb(255, 225, 74), Color.Rgb(224, 24, 24),
            r.CenterX(), r.CenterY() - size * 0.015f, Math.Max(5f, size * 0.035f));
    }

    void DrawBeam(Canvas canvas, RectF crate, float angle)
    {
        canvas.Save();
        canvas.Rotate(angle, crate.CenterX(), crate.CenterY());
        var half = 13f * _unit;
        using var shader = new LinearGradient(
            crate.CenterX() - half, crate.Top, crate.CenterX() + half, crate.Bottom,
            Color.Rgb(138, 74, 18), Color.Rgb(224, 160, 80), Shader.TileMode.Clamp);
        _paint.SetShader(shader);
        _paint.SetStyle(Paint.Style.Fill);
        canvas.DrawRect(crate.CenterX() - half, crate.Top - 10f * _unit,
            crate.CenterX() + half, crate.Bottom + 10f * _unit, _paint);
        _paint.SetShader(null);
        _paint.SetStyle(Paint.Style.Stroke);
        _paint.StrokeWidth = 2f * _unit;
        _paint.Color = Color.Argb(95, 30, 12, 0);
        canvas.DrawRect(crate.CenterX() - half, crate.Top - 10f * _unit,
            crate.CenterX() + half, crate.Bottom + 10f * _unit, _paint);
        _paint.SetStyle(Paint.Style.Fill);
        canvas.Restore();
    }

    void DrawBolt(Canvas canvas, float x, float y, float radius)
    {
        using var shader = new RadialGradient(x - radius * .3f, y - radius * .3f, radius * 1.4f,
            Color.Rgb(232, 234, 236), Color.Rgb(58, 62, 68), Shader.TileMode.Clamp);
        _paint.SetShader(shader);
        canvas.DrawCircle(x, y, radius, _paint);
        _paint.SetShader(null);
        _paint.SetStyle(Paint.Style.Stroke);
        _paint.StrokeWidth = Math.Max(1f, _unit);
        _paint.Color = Color.Argb(100, 0, 0, 0);
        canvas.DrawCircle(x, y, radius, _paint);
        _paint.SetStyle(Paint.Style.Fill);
    }

    void DrawMenu(Canvas canvas)
    {
        for (var i = 0; i < MenuLabels.Length; i++)
        {
            var disabled = i == 0 && (!_ready || _busy);
            var hot = _pressed == i && !disabled;
            var color = disabled
                ? Color.Argb(150, 122, 117, 104)
                : i is 0 or 5 ? (hot ? _wumpaHot : _wumpa)
                : hot ? _wumpaHot : _sand;
            var bounds = _menuBounds[i];
            var x = bounds.Left + (hot ? 10f * _unit : 0);
            DrawTextAtTop(canvas, MenuLabels[i], _displayFont, _menuTextSize,
                color, x, bounds.Top + 2f * _unit, shadow: true);
        }
    }

    void DrawFooter(Canvas canvas, float width, float height)
    {
        using (var shader = new LinearGradient(0, _footerTop, 0, height,
                   Color.Transparent, Color.Argb(115, 0, 0, 0), Shader.TileMode.Clamp))
        {
            _paint.SetShader(shader);
            canvas.DrawRect(0, _footerTop, width, height, _paint);
            _paint.SetShader(null);
        }
        _paint.Color = Color.Argb(45, 255, 200, 120);
        _paint.StrokeWidth = Math.Max(1f, _unit);
        canvas.DrawLine(0, _footerTop, width, _footerTop, _paint);

        _paint.SetStyle(Paint.Style.Fill);
        _paint.Color = _pressed == 7
            ? Color.Argb(100, 255, 138, 0)
            : Color.Argb(55, 255, 138, 0);
        canvas.DrawOval(_infoBounds, _paint);
        _paint.SetStyle(Paint.Style.Stroke);
        _paint.StrokeWidth = 2f * _unit;
        _paint.Color = Color.Argb(165, 255, 180, 60);
        canvas.DrawOval(_infoBounds, _paint);
        _paint.SetStyle(Paint.Style.Fill);
        DrawCenteredText(canvas, "i", _displayFont, 18f * FooterUiScale * _unit, _wumpaHot,
            _infoBounds.CenterX(), _infoBounds.CenterY());

        _paint.Color = _pressed == 6
            ? Color.Argb(90, 255, 138, 0)
            : Color.Argb(38, 255, 138, 0);
        canvas.DrawRoundRect(_chipBounds, 17f * FooterUiScale * _unit,
            17f * FooterUiScale * _unit, _paint);
        _paint.SetStyle(Paint.Style.Stroke);
        _paint.StrokeWidth = Math.Max(1f, FooterUiScale * _unit);
        _paint.Color = Color.Argb(110, 255, 180, 60);
        canvas.DrawRoundRect(_chipBounds, 17f * FooterUiScale * _unit,
            17f * FooterUiScale * _unit, _paint);
        _paint.SetStyle(Paint.Style.Fill);
        DrawCenteredText(canvas, "Select disc (.cue)", _bodyBoldFont,
            15f * FooterUiScale * _unit,
            _sand, _chipBounds.CenterX(), _chipBounds.CenterY());

        var textX = _chipBounds.Right + 14f * FooterUiScale * _unit;
        var textRight = width - 118f * FooterUiScale * _unit;
        var statusColor = _statusKind == "error"
            ? Color.Rgb(255, 143, 143)
            : _statusKind == "ok" ? _ok
            : Color.Argb(215, _sand.R, _sand.G, _sand.B);
        DrawFooterLine(canvas, _status, _bodyFont, 14f * FooterUiScale * _unit, statusColor,
            textX, _footerTop + 7f * FooterUiScale * _unit, textRight - textX);
        DrawFooterLine(canvas, _discLine, _bodyFont, 15f * FooterUiScale * _unit,
            Color.Argb(200, _sand.R, _sand.G, _sand.B),
            textX, _chipBounds.Top + 6f * FooterUiScale * _unit, textRight - textX);

        _text.SetTypeface(_bodyBoldFont);
        _text.TextSize = 23f * FooterUiScale * _unit;
        _text.Color = Color.Argb(190, _sand.R, _sand.G, _sand.B);
        _text.TextAlign = Paint.Align.Right;
        canvas.DrawText(_versionLabel, width - 18f * FooterUiScale * _unit,
            height - 15f * FooterUiScale * _unit, _text);
        _text.TextAlign = Paint.Align.Left;
    }

    static string ReadVersionLabel(Activity activity)
    {
        try
        {
            var version = activity.PackageManager?
                .GetPackageInfo(activity.PackageName!, PackageInfoFlags.MatchAll)?.VersionName;
            return string.IsNullOrWhiteSpace(version) ? "development" : $"v{version}";
        }
        catch
        {
            return "development";
        }
    }

    void DrawFooterLine(Canvas canvas, string value, Typeface font, float size,
        Color color, float x, float top, float maxWidth)
    {
        _text.SetTypeface(font);
        _text.TextSize = size;
        _text.Color = color;
        _text.TextAlign = Paint.Align.Left;
        var fitted = FitText(value, _text, maxWidth);
        var metrics = _text.GetFontMetrics();
        canvas.DrawText(fitted, x, top - metrics.Top, _text);
    }

    static string FitText(string text, Paint paint, float maxWidth)
    {
        if (maxWidth <= 0 || paint.MeasureText(text) <= maxWidth) return text;
        const string ellipsis = "…";
        var length = paint.BreakText(text, true, Math.Max(0, maxWidth - paint.MeasureText(ellipsis)), null);
        return length <= 0 ? ellipsis : text[..length].TrimEnd() + ellipsis;
    }

    void DrawOutlinedText(Canvas canvas, string text, Typeface font, float size,
        Color fill, Color stroke, float x, float top, float strokeWidth, bool shadow)
    {
        ConfigureText(font, size, Paint.Align.Left);
        var metrics = _text.GetFontMetrics();
        var baseline = top - metrics.Top;
        if (shadow)
        {
            _text.SetStyle(Paint.Style.Stroke);
            _text.StrokeWidth = strokeWidth + 2f * _unit;
            _text.Color = Color.Argb(170, 0, 0, 0);
            canvas.DrawText(text, x, baseline + 6f * _unit, _text);
        }
        _text.SetStyle(Paint.Style.Stroke);
        _text.StrokeWidth = strokeWidth;
        _text.Color = stroke;
        canvas.DrawText(text, x, baseline, _text);
        _text.SetStyle(Paint.Style.Fill);
        _text.Color = fill;
        canvas.DrawText(text, x, baseline, _text);
    }

    void DrawCenteredOutlinedText(Canvas canvas, string text, Typeface font, float size,
        Color fill, Color stroke, float centerX, float centerY, float strokeWidth)
    {
        ConfigureText(font, size, Paint.Align.Center);
        var metrics = _text.GetFontMetrics();
        var baseline = centerY - (metrics.Ascent + metrics.Descent) / 2f;
        _text.SetStyle(Paint.Style.Stroke);
        _text.StrokeWidth = strokeWidth;
        _text.Color = stroke;
        canvas.DrawText(text, centerX, baseline, _text);
        _text.SetStyle(Paint.Style.Fill);
        _text.Color = fill;
        canvas.DrawText(text, centerX, baseline, _text);
    }

    void DrawTextAtTop(Canvas canvas, string text, Typeface font, float size,
        Color color, float x, float top, bool shadow)
    {
        ConfigureText(font, size, Paint.Align.Left);
        var metrics = _text.GetFontMetrics();
        var baseline = top - metrics.Top;
        if (shadow)
        {
            _text.Color = Color.Argb(190, 0, 0, 0);
            canvas.DrawText(text, x, baseline + 3f * _unit, _text);
        }
        _text.Color = color;
        canvas.DrawText(text, x, baseline, _text);
    }

    void DrawCenteredText(Canvas canvas, string text, Typeface font, float size,
        Color color, float centerX, float centerY)
    {
        ConfigureText(font, size, Paint.Align.Center);
        _text.Color = color;
        var metrics = _text.GetFontMetrics();
        var baseline = centerY - (metrics.Ascent + metrics.Descent) / 2f;
        canvas.DrawText(text, centerX, baseline, _text);
    }

    void ConfigureText(Typeface font, float size, Paint.Align align)
    {
        _text.SetTypeface(font);
        _text.TextSize = size;
        _text.TextAlign = align;
        _text.SetStyle(Paint.Style.Fill);
        _text.StrokeWidth = 0;
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null) return false;
        var hit = HitTest(e.GetX(), e.GetY());
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _pressed = hit;
                Invalidate();
                return hit >= 0;
            case MotionEventActions.Move:
                if (_pressed >= 0 && hit != _pressed)
                {
                    _pressed = -1;
                    Invalidate();
                }
                return true;
            case MotionEventActions.Up:
                var selected = _pressed == hit ? hit : -1;
                _pressed = -1;
                Invalidate();
                if (selected >= 0)
                {
                    PerformClick();
                    Activate(selected);
                }
                return true;
            case MotionEventActions.Cancel:
                _pressed = -1;
                Invalidate();
                return true;
            default:
                return base.OnTouchEvent(e);
        }
    }

    public override bool PerformClick()
    {
        base.PerformClick();
        return true;
    }

    int HitTest(float x, float y)
    {
        for (var i = 0; i < _menuBounds.Length; i++)
        {
            if (_menuBounds[i].Contains(x, y))
                return i == 0 && (!_ready || _busy) ? -1 : i;
        }
        if (_chipBounds.Contains(x, y)) return 6;
        if (_infoBounds.Contains(x, y)) return 7;
        return -1;
    }

    void Activate(int target)
    {
        switch (target)
        {
            case 0:
                StartGameRequested?.Invoke();
                break;
            case 1:
                Post(() => TouchControlsDialog.Show(_activity));
                break;
            case 2:
                SettingsRequested?.Invoke();
                break;
            case 3:
                GpuLabRequested?.Invoke();
                break;
            case 4:
                ShowComingSoon("Cheats");
                break;
            case 5:
                _activity.Finish();
                break;
            case 6:
                SelectDiscRequested?.Invoke();
                break;
            case 7:
                Post(ShowAboutDialog);
                break;
        }
    }

    void ShowAboutDialog()
    {
        if (_aboutDialog is { IsShowing: true }) return;

        var dialog = new Dialog(_activity);
        dialog.RequestWindowFeature((int)WindowFeatures.NoTitle);
        dialog.SetCanceledOnTouchOutside(false);

        var card = new LinearLayout(_activity)
        {
            Orientation = Orientation.Vertical,
        };
        card.SetPadding(Dp(28), Dp(22), Dp(28), Dp(22));
        card.Background = RoundedBackground(
            Color.Rgb(11, 35, 27),
            Color.Argb(150, 255, 176, 32),
            Dp(1),
            Dp(2));

        var title = new TextView(_activity)
        {
            Text = "ABOUT",
            TextSize = 20,
        };
        title.SetTypeface(_displayFont, TypefaceStyle.Normal);
        title.SetTextColor(_wumpa);
        title.SetIncludeFontPadding(false);
        card.AddView(title, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        var body = new TextView(_activity)
        {
            Text = "Unofficial fan project — not affiliated with Sony, Activision, or Naughty Dog.\n\n" +
                   "Unofficial tools for a disc you own. The first prepare writes game files into app storage; later Starts reuse them.\n\n" +
                   "Prepared files never replace your dump: you still need a valid NTSC-U .cue + .bin (SCUS-94900) every time you play.",
            TextSize = 12,
        };
        body.SetTypeface(_bodyFont, TypefaceStyle.Normal);
        body.SetTextColor(_sand);
        body.SetLineSpacing(0, 1.12f);
        card.AddView(body, new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = Dp(18),
            BottomMargin = Dp(20),
        });

        var back = new Button(_activity)
        {
            Text = "Back",
            TextSize = 12,
            Gravity = GravityFlags.Center,
            StateListAnimator = null,
        };
        back.SetTypeface(_bodyBoldFont, TypefaceStyle.Normal);
        back.SetTextColor(_sand);
        back.SetMinHeight(0);
        back.SetMinWidth(0);
        back.Background = RoundedBackground(
            Color.Rgb(31, 19, 13),
            Color.Argb(210, 255, 176, 32),
            Dp(1),
            Dp(2));
        back.Click += (_, _) => dialog.Dismiss();
        card.AddView(back, new LinearLayout.LayoutParams(Dp(130), Dp(38)));

        dialog.SetContentView(card);
        dialog.DismissEvent += (_, _) =>
        {
            if (ReferenceEquals(_aboutDialog, dialog)) _aboutDialog = null;
            dialog.Dispose();
        };
        _aboutDialog = dialog;
        dialog.Show();

        if (dialog.Window != null)
        {
            dialog.Window.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));
            dialog.Window.AddFlags(WindowManagerFlags.DimBehind);
            dialog.Window.SetDimAmount(0.72f);
            var displayWidth = Resources?.DisplayMetrics?.WidthPixels ?? Width;
            var fraction = Height > Width ? 0.90f : 0.62f;
            dialog.Window.SetLayout(
                (int)(displayWidth * fraction),
                ViewGroup.LayoutParams.WrapContent);
            dialog.Window.SetGravity(GravityFlags.Center);
        }
    }

    static GradientDrawable RoundedBackground(
        Color fill, Color stroke, int strokeWidth, float radius)
    {
        var drawable = new GradientDrawable();
        drawable.SetColor(fill);
        drawable.SetCornerRadius(radius);
        drawable.SetStroke(strokeWidth, stroke);
        return drawable;
    }

    int Dp(float value) =>
        (int)(value * (Resources?.DisplayMetrics?.Density ?? 1f) + 0.5f);

    void ShowComingSoon(string feature) =>
        Toast.MakeText(_activity, $"{feature}: next step of the Android port.",
            ToastLength.Short)?.Show();

    Typeface LoadTypeface(string assetPath, Typeface fallback)
    {
        try { return Typeface.CreateFromAsset(_activity.Assets, assetPath) ?? fallback; }
        catch { return fallback; }
    }
}
