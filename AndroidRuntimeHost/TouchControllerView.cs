using Android.Content;
using Android.Graphics;
using Android.Views;
using RecompOne.Runtime.Hardware;

namespace CrashBandicoot.AndroidRuntime;

/// <summary>
/// Multitouch PlayStation controller overlay. Every pointer owns its current
/// button mask, allowing directions and action buttons to be held together.
/// </summary>
sealed class TouchControllerView : View
{
    readonly Paint _paint = new(PaintFlags.AntiAlias | PaintFlags.SubpixelText);
    readonly Dictionary<int, ushort> _pointerButtons = [];

    readonly RectF _triangle = new();
    readonly RectF _circle = new();
    readonly RectF _cross = new();
    readonly RectF _square = new();
    readonly RectF _l1 = new();
    readonly RectF _l2 = new();
    readonly RectF _r1 = new();
    readonly RectF _r2 = new();
    readonly RectF _start = new();
    readonly RectF _select = new();

    float _radius;
    float _dpadX;
    float _dpadY;
    ushort _pressedButtons;

    static readonly Color Sand = Color.Rgb(255, 239, 194);
    static readonly Color Gold = Color.Rgb(255, 153, 20);
    static readonly Color CrossBlue = Color.Rgb(80, 155, 255);
    static readonly Color CircleRed = Color.Rgb(255, 90, 90);
    static readonly Color SquarePink = Color.Rgb(255, 120, 205);
    static readonly Color TriangleGreen = Color.Rgb(90, 230, 145);

    public TouchControllerView(Context context) : base(context)
    {
        Clickable = true;
        Focusable = true;
        ContentDescription = "Controller touch PlayStation";
        SetBackgroundColor(Color.Transparent);
    }

    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        LayoutControls(w, h);
    }

    void LayoutControls(float width, float height)
    {
        var density = Resources?.DisplayMetrics?.Density ?? 1f;
        _radius = Math.Clamp(height * 0.085f, 28f * density, 44f * density);
        var margin = Math.Max(10f * density, height * 0.022f);
        var step = _radius * 1.38f;

        _dpadX = margin + step + _radius;
        _dpadY = height - margin - step - _radius;

        var faceX = width - margin - step - _radius;
        var faceY = _dpadY;
        SetCircle(_triangle, faceX, faceY - step);
        SetCircle(_circle, faceX + step, faceY);
        SetCircle(_cross, faceX, faceY + step);
        SetCircle(_square, faceX - step, faceY);

        var shoulderWidth = _radius * 1.85f;
        var shoulderHeight = _radius * 0.72f;
        var shoulderGap = _radius * 0.22f;
        // Stay below Android's status-bar icons on devices with camera cut-outs.
        var shoulderTop = Math.Max(margin, 34f * density);
        _l1.Set(margin, shoulderTop, margin + shoulderWidth, shoulderTop + shoulderHeight);
        _l2.Set(_l1.Right + shoulderGap, shoulderTop,
            _l1.Right + shoulderGap + shoulderWidth, shoulderTop + shoulderHeight);
        _r1.Set(width - margin - shoulderWidth, shoulderTop,
            width - margin, shoulderTop + shoulderHeight);
        _r2.Set(_r1.Left - shoulderGap - shoulderWidth, shoulderTop,
            _r1.Left - shoulderGap, shoulderTop + shoulderHeight);

        var systemWidth = _radius * 1.72f;
        var systemHeight = _radius * 0.62f;
        var systemGap = _radius * 0.28f;
        var systemTop = height - margin - systemHeight;
        _select.Set(width * 0.5f - systemGap * 0.5f - systemWidth, systemTop,
            width * 0.5f - systemGap * 0.5f, systemTop + systemHeight);
        _start.Set(width * 0.5f + systemGap * 0.5f, systemTop,
            width * 0.5f + systemGap * 0.5f + systemWidth, systemTop + systemHeight);
    }

    void SetCircle(RectF bounds, float x, float y) =>
        bounds.Set(x - _radius, y - _radius, x + _radius, y + _radius);

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (_radius <= 0) LayoutControls(Width, Height);

        DrawDpad(canvas);
        DrawCircleButton(canvas, _triangle, "△", TriangleGreen, Controller.Triangle);
        DrawCircleButton(canvas, _circle, "○", CircleRed, Controller.Circle);
        DrawCircleButton(canvas, _cross, "×", CrossBlue, Controller.Cross);
        DrawCircleButton(canvas, _square, "□", SquarePink, Controller.Square);

        DrawPill(canvas, _l1, "L1", Controller.L1);
        DrawPill(canvas, _l2, "L2", Controller.L2);
        DrawPill(canvas, _r2, "R2", Controller.R2);
        DrawPill(canvas, _r1, "R1", Controller.R1);
        DrawPill(canvas, _select, "SELECT", Controller.Select);
        DrawPill(canvas, _start, "START", Controller.Start);
    }

    void DrawDpad(Canvas canvas)
    {
        var step = _radius * 1.38f;
        DrawDirection(canvas, _dpadX, _dpadY - step, "▲", Controller.Up);
        DrawDirection(canvas, _dpadX + step, _dpadY, "▶", Controller.Right);
        DrawDirection(canvas, _dpadX, _dpadY + step, "▼", Controller.Down);
        DrawDirection(canvas, _dpadX - step, _dpadY, "◀", Controller.Left);

        _paint.SetStyle(Paint.Style.Fill);
        _paint.Color = Color.Argb(76, 8, 14, 20);
        canvas.DrawCircle(_dpadX, _dpadY, _radius * 0.58f, _paint);
    }

    void DrawDirection(Canvas canvas, float x, float y, string label, ushort bit)
    {
        var pressed = (_pressedButtons & bit) != 0;
        DrawControlCircle(canvas, x, y, _radius, pressed, Sand);
        DrawCenteredLabel(canvas, label, x, y, _radius * 0.64f,
            pressed ? Color.White : Sand);
    }

    void DrawCircleButton(Canvas canvas, RectF bounds, string label, Color accent, ushort bit)
    {
        var pressed = (_pressedButtons & bit) != 0;
        DrawControlCircle(canvas, bounds.CenterX(), bounds.CenterY(), _radius, pressed, accent);
        DrawCenteredLabel(canvas, label, bounds.CenterX(), bounds.CenterY(), _radius * 0.86f,
            pressed ? Color.White : accent);
    }

    void DrawControlCircle(Canvas canvas, float x, float y, float radius, bool pressed, Color accent)
    {
        _paint.SetStyle(Paint.Style.Fill);
        _paint.Color = pressed ? Color.Argb(178, accent.R, accent.G, accent.B) : Color.Argb(76, 8, 14, 20);
        canvas.DrawCircle(x, y, radius, _paint);
        _paint.SetStyle(Paint.Style.Stroke);
        _paint.StrokeWidth = Math.Max(2f, radius * 0.065f);
        _paint.Color = pressed ? Color.White : Color.Argb(205, accent.R, accent.G, accent.B);
        canvas.DrawCircle(x, y, radius, _paint);
        _paint.SetStyle(Paint.Style.Fill);
    }

    void DrawPill(Canvas canvas, RectF bounds, string label, ushort bit)
    {
        var pressed = (_pressedButtons & bit) != 0;
        _paint.SetStyle(Paint.Style.Fill);
        _paint.Color = pressed ? Color.Argb(190, Gold.R, Gold.G, Gold.B) : Color.Argb(92, 8, 14, 20);
        canvas.DrawRoundRect(bounds, bounds.Height() * 0.42f, bounds.Height() * 0.42f, _paint);
        _paint.SetStyle(Paint.Style.Stroke);
        _paint.StrokeWidth = Math.Max(2f, _radius * 0.045f);
        _paint.Color = pressed ? Color.White : Color.Argb(205, Gold.R, Gold.G, Gold.B);
        canvas.DrawRoundRect(bounds, bounds.Height() * 0.42f, bounds.Height() * 0.42f, _paint);
        _paint.SetStyle(Paint.Style.Fill);
        DrawCenteredLabel(canvas, label, bounds.CenterX(), bounds.CenterY(),
            Math.Max(_radius * 0.34f, bounds.Height() * 0.45f), pressed ? Color.White : Sand);
    }

    void DrawCenteredLabel(Canvas canvas, string label, float x, float y, float size, Color color)
    {
        _paint.SetTypeface(Typeface.DefaultBold);
        _paint.TextSize = size;
        _paint.TextAlign = Paint.Align.Center;
        _paint.Color = color;
        var metrics = _paint.GetFontMetrics();
        canvas.DrawText(label, x, y - (metrics.Ascent + metrics.Descent) * 0.5f, _paint);
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null) return false;

        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
            case MotionEventActions.PointerDown:
                UpdatePointer(e, e.ActionIndex);
                return true;

            case MotionEventActions.Move:
                for (var i = 0; i < e.PointerCount; i++) UpdatePointer(e, i);
                return true;

            case MotionEventActions.Up:
            case MotionEventActions.PointerUp:
                for (var i = 0; i < e.PointerCount; i++)
                    if (i != e.ActionIndex) UpdatePointer(e, i, publish: false);
                _pointerButtons.Remove(e.GetPointerId(e.ActionIndex));
                PublishState();
                PerformClick();
                return true;

            case MotionEventActions.Cancel:
                ReleaseAll();
                return true;

            default:
                return true;
        }
    }

    void UpdatePointer(MotionEvent e, int index, bool publish = true)
    {
        _pointerButtons[e.GetPointerId(index)] = HitTest(e.GetX(index), e.GetY(index));
        if (publish) PublishState();
    }

    ushort HitTest(float x, float y)
    {
        ushort buttons = 0;
        var dx = x - _dpadX;
        var dy = y - _dpadY;
        var reach = _radius * 2.45f;
        if (dx * dx + dy * dy <= reach * reach)
        {
            var threshold = _radius * 0.32f;
            if (dx < -threshold) buttons |= Controller.Left;
            if (dx > threshold) buttons |= Controller.Right;
            if (dy < -threshold) buttons |= Controller.Up;
            if (dy > threshold) buttons |= Controller.Down;
            return buttons;
        }

        if (CircleContains(_triangle, x, y)) buttons |= Controller.Triangle;
        if (CircleContains(_circle, x, y)) buttons |= Controller.Circle;
        if (CircleContains(_cross, x, y)) buttons |= Controller.Cross;
        if (CircleContains(_square, x, y)) buttons |= Controller.Square;
        if (_l1.Contains(x, y)) buttons |= Controller.L1;
        if (_l2.Contains(x, y)) buttons |= Controller.L2;
        if (_r1.Contains(x, y)) buttons |= Controller.R1;
        if (_r2.Contains(x, y)) buttons |= Controller.R2;
        if (_select.Contains(x, y)) buttons |= Controller.Select;
        if (_start.Contains(x, y)) buttons |= Controller.Start;
        return buttons;
    }

    static bool CircleContains(RectF bounds, float x, float y)
    {
        var dx = x - bounds.CenterX();
        var dy = y - bounds.CenterY();
        var radius = bounds.Width() * 0.5f;
        return dx * dx + dy * dy <= radius * radius;
    }

    void PublishState()
    {
        ushort combined = 0;
        foreach (var buttons in _pointerButtons.Values) combined |= buttons;
        if (combined == _pressedButtons) return;
        _pressedButtons = combined;
        Controller.SetVirtualPadState(combined);
        Invalidate();
    }

    void ReleaseAll()
    {
        _pointerButtons.Clear();
        if (_pressedButtons == 0) return;
        _pressedButtons = 0;
        Controller.SetVirtualPadState(0);
        Invalidate();
    }

    protected override void OnDetachedFromWindow()
    {
        ReleaseAll();
        base.OnDetachedFromWindow();
    }
}
