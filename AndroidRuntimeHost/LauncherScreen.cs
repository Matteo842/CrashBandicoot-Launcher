using Android.App;
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
    static readonly string[] MenuLabels =
    [
        "START GAME", "CONTROLS", "SETTINGS", "MODS", "CHEAT", "EXIT",
    ];

    readonly Activity _activity;
    readonly Paint _paint = new(PaintFlags.AntiAlias | PaintFlags.FilterBitmap);
    readonly Paint _text = new(PaintFlags.AntiAlias | PaintFlags.SubpixelText);
    readonly Typeface _displayFont;
    readonly Typeface _bodyFont;
    readonly Typeface _bodyBoldFont;
    readonly Bitmap? _map;
    readonly RectF[] _menuBounds = new RectF[MenuLabels.Length];
    readonly RectF _crateBounds = new();
    readonly RectF _infoBounds = new();
    readonly RectF _chipBounds = new();

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

    public LauncherScreen(Activity activity) : base(activity)
    {
        _activity = activity;
        _displayFont = LoadTypeface("Fonts/Bungee-Regular.ttf", Typeface.DefaultBold!);
        _bodyFont = LoadTypeface("Fonts/Nunito-SemiBold.ttf", Typeface.Default!);
        _bodyBoldFont = LoadTypeface("Fonts/Nunito-ExtraBold.ttf", Typeface.DefaultBold!);
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
        DrawMap(canvas, Width, Height);
        DrawBrand(canvas);
        DrawCrate(canvas);
        DrawMenu(canvas);
        DrawFooter(canvas, Width, Height);
    }

    void LayoutScene(float width, float height)
    {
        _unit = Math.Clamp(height / 760f, 0.75f, 1.65f);
        var footerHeight = 78f * _unit;
        _footerTop = height - footerHeight;
        var contentHeight = _footerTop;

        var brandX = 40f * _unit;
        var brandY = Math.Max(56f * _unit, contentHeight * 0.18f);
        var crateSize = Math.Min(width * 0.17f, contentHeight * 0.40f);
        _crateBounds.Set(brandX, brandY + 120f * _unit,
            brandX + crateSize, brandY + 120f * _unit + crateSize);

        var menuX = width * 0.675f;
        var menuY = Math.Max(54f * _unit, contentHeight * 0.15f);
        var menuRow = 60f * _unit;
        for (var i = 0; i < _menuBounds.Length; i++)
        {
            var top = menuY + i * menuRow;
            _menuBounds[i].Set(menuX - 12f * _unit, top,
                width - 30f * _unit, top + 52f * _unit);
        }

        var footerY = _footerTop + 16f * _unit;
        _infoBounds.Set(28f * _unit, footerY, 64f * _unit, footerY + 36f * _unit);
        _chipBounds.Set(76f * _unit, footerY + _unit,
            244f * _unit, footerY + 35f * _unit);
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

    void DrawMap(Canvas canvas, float width, float height)
    {
        if (_map == null) return;
        var contentHeight = _footerTop;
        var maxWidth = Math.Min(width * 0.43f, contentHeight * 1.02f);
        var maxHeight = contentHeight * 0.72f;
        var scale = Math.Min(maxWidth / _map.Width, maxHeight / _map.Height);
        var drawWidth = _map.Width * scale;
        var drawHeight = _map.Height * scale;
        var centerX = width * 0.505f;
        var centerY = contentHeight * 0.49f;
        var destination = new RectF(
            centerX - drawWidth / 2f,
            centerY - drawHeight / 2f,
            centerX + drawWidth / 2f,
            centerY + drawHeight / 2f);

        _paint.Alpha = 88;
        var shadow = new RectF(destination);
        shadow.Offset(7f * _unit, 14f * _unit);
        canvas.DrawBitmap(_map, null, shadow, _paint);
        _paint.Alpha = 255;
        canvas.DrawBitmap(_map, null, destination, _paint);
    }

    void DrawBrand(Canvas canvas)
    {
        var x = 40f * _unit;
        var y = Math.Max(56f * _unit, _footerTop * 0.18f);
        DrawOutlinedText(canvas, "CRASH", _displayFont, 64f * _unit,
            _wumpa, Color.Rgb(58, 21, 0), x, y, 3f * _unit, true);
        DrawOutlinedText(canvas, "RECOMPILED", _displayFont, 28f * _unit,
            _danger, Color.Rgb(58, 0, 0), x, y + 65f * _unit, 2f * _unit, true);
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
        _paint.StrokeWidth = 2f * _unit;
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
            DrawTextAtTop(canvas, MenuLabels[i], _displayFont, 38f * _unit,
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
        DrawCenteredText(canvas, "i", _displayFont, 18f * _unit, _wumpaHot,
            _infoBounds.CenterX(), _infoBounds.CenterY());

        _paint.Color = _pressed == 6
            ? Color.Argb(90, 255, 138, 0)
            : Color.Argb(38, 255, 138, 0);
        canvas.DrawRoundRect(_chipBounds, 17f * _unit, 17f * _unit, _paint);
        _paint.SetStyle(Paint.Style.Stroke);
        _paint.StrokeWidth = Math.Max(1f, _unit);
        _paint.Color = Color.Argb(110, 255, 180, 60);
        canvas.DrawRoundRect(_chipBounds, 17f * _unit, 17f * _unit, _paint);
        _paint.SetStyle(Paint.Style.Fill);
        DrawCenteredText(canvas, "Select disc (.cue)", _bodyBoldFont, 15f * _unit,
            _sand, _chipBounds.CenterX(), _chipBounds.CenterY());

        var textX = _chipBounds.Right + 14f * _unit;
        var textRight = width - 118f * _unit;
        var statusColor = _statusKind == "error"
            ? Color.Rgb(255, 143, 143)
            : _statusKind == "ok" ? _ok
            : Color.Argb(215, _sand.R, _sand.G, _sand.B);
        DrawFooterLine(canvas, _status, _bodyFont, 14f * _unit, statusColor,
            textX, _footerTop + 7f * _unit, textRight - textX);
        DrawFooterLine(canvas, _discLine, _bodyFont, 15f * _unit,
            Color.Argb(200, _sand.R, _sand.G, _sand.B),
            textX, _chipBounds.Top + 6f * _unit, textRight - textX);

        _text.SetTypeface(_bodyBoldFont);
        _text.TextSize = 23f * _unit;
        _text.Color = Color.Argb(190, _sand.R, _sand.G, _sand.B);
        _text.TextAlign = Paint.Align.Right;
        canvas.DrawText("v0.2.0", width - 18f * _unit, height - 15f * _unit, _text);
        _text.TextAlign = Paint.Align.Left;
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
                ShowComingSoon("Controls and touch mapping");
                break;
            case 2:
                ShowComingSoon("Settings");
                break;
            case 3:
                ShowComingSoon("Mods");
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
                Toast.MakeText(_activity,
                    "Crash Bandicoot Recompiled — unofficial fan project",
                    ToastLength.Short)?.Show();
                break;
        }
    }

    void ShowComingSoon(string feature) =>
        Toast.MakeText(_activity, $"{feature}: prossimo passaggio del port Android.",
            ToastLength.Short)?.Show();

    Typeface LoadTypeface(string assetPath, Typeface fallback)
    {
        try { return Typeface.CreateFromAsset(_activity.Assets, assetPath) ?? fallback; }
        catch { return fallback; }
    }
}
