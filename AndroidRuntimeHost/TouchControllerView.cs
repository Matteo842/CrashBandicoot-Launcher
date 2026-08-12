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
    readonly TouchControlSettings _settings;
    readonly bool _editing;

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
    float _controlStep;
    float _joystickTravelRadius;
    float _dpadX;
    float _dpadY;
    float _faceX;
    float _faceY;
    float _systemX;
    float _systemY;
    ushort _pressedButtons;
    TouchControlGroup? _dragGroup;
    int _dragPointerId = -1;
    int _joystickPointerId = -1;
    bool _joystickActive;
    float _joystickOriginX;
    float _joystickOriginY;
    float _joystickKnobX;
    float _joystickKnobY;

    static readonly Color Sand = Color.Rgb(255, 239, 194);
    static readonly Color Neutral = Color.Rgb(174, 184, 186);
    static readonly Color Gold = Color.Rgb(255, 153, 20);
    static readonly Color CrossBlue = Color.Rgb(80, 155, 255);
    static readonly Color CircleRed = Color.Rgb(255, 90, 90);
    static readonly Color SquarePink = Color.Rgb(255, 120, 205);
    static readonly Color TriangleGreen = Color.Rgb(90, 230, 145);

    public TouchControllerView(Context context)
        : this(context, new TouchControlSettings(context), editing: false)
    {
    }

    internal TouchControllerView(
        Context context,
        TouchControlSettings settings,
        bool editing) : base(context)
    {
        _settings = settings;
        _editing = editing;
        Clickable = true;
        Focusable = true;
        ContentDescription = editing
            ? "Touch controller position editor"
            : "PlayStation touch controller";
        SetBackgroundColor(Color.Transparent);
        Alpha = settings.Opacity;
    }

    internal void RefreshSettings()
    {
        Alpha = _settings.Opacity;
        if (Width > 0 && Height > 0) LayoutControls(Width, Height);
        Invalidate();
    }

    internal void ResetLayout()
    {
        _settings.Reset();
        RefreshSettings();
    }

    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        LayoutControls(w, h);
    }

    void LayoutControls(float width, float height)
    {
        var density = Resources?.DisplayMetrics?.Density ?? 1f;
        _radius = Math.Clamp(height * 0.085f, 28f * density, 44f * density) * _settings.Scale;
        var margin = Math.Max(10f * density, height * 0.022f);
        var maximumVerticalStep = Math.Max(_radius,
            height * 0.5f - margin - _radius);
        _controlStep = Math.Min(_radius * 1.80f, maximumVerticalStep);
        var groupReach = _controlStep + _radius;

        _joystickTravelRadius = Math.Min(_radius * 2.0f, height * 0.32f);
        var joystickReach = _joystickTravelRadius + _radius * 0.58f;
        _dpadX = Math.Clamp(width * _settings.DpadX,
            margin + joystickReach, width * 0.48f - joystickReach);
        _dpadY = Math.Clamp(height * _settings.DpadY,
            margin + joystickReach, height - margin - joystickReach);

        _faceX = Math.Clamp(width * _settings.FaceX,
            width * 0.52f + _radius, width - margin - groupReach);
        _faceY = Math.Clamp(height * _settings.FaceY,
            margin + groupReach, height - margin - groupReach);
        SetCircle(_triangle, _faceX, _faceY - _controlStep);
        SetCircle(_circle, _faceX + _controlStep, _faceY);
        SetCircle(_cross, _faceX, _faceY + _controlStep);
        SetCircle(_square, _faceX - _controlStep, _faceY);

        var shoulderWidth = _radius * 1.85f;
        var shoulderHeight = _radius * 0.72f;
        var shoulderGap = _radius * 0.22f;
        // Stay below Android's status-bar icons on devices with camera cut-outs.
        var shoulderTop = Math.Clamp(
            height * _settings.ShouldersY - shoulderHeight * 0.5f,
            34f * density,
            height - margin - shoulderHeight);
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
        _systemX = Math.Clamp(width * _settings.SystemX,
            systemWidth + systemGap + margin,
            width - systemWidth - systemGap - margin);
        _systemY = Math.Clamp(height * _settings.SystemY,
            systemHeight * 0.5f + margin,
            height - systemHeight * 0.5f - margin);
        var systemTop = _systemY - systemHeight * 0.5f;
        _select.Set(_systemX - systemGap * 0.5f - systemWidth, systemTop,
            _systemX - systemGap * 0.5f, systemTop + systemHeight);
        _start.Set(_systemX + systemGap * 0.5f, systemTop,
            _systemX + systemGap * 0.5f + systemWidth, systemTop + systemHeight);
    }

    void SetCircle(RectF bounds, float x, float y) =>
        bounds.Set(x - _radius, y - _radius, x + _radius, y + _radius);

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (_radius <= 0) LayoutControls(Width, Height);

        DrawJoystick(canvas);
        DrawCircleButton(canvas, _triangle, "△", TriangleGreen, Controller.Triangle);
        DrawCircleButton(canvas, _circle, "○", CircleRed, Controller.Circle);
        DrawCircleButton(canvas, _cross, "×", CrossBlue, Controller.Cross);
        DrawCircleButton(canvas, _square, "□", SquarePink, Controller.Square);

        if (_settings.ShowShoulders)
        {
            DrawPill(canvas, _l1, "L1", Controller.L1);
            DrawPill(canvas, _l2, "L2", Controller.L2);
            DrawPill(canvas, _r2, "R2", Controller.R2);
            DrawPill(canvas, _r1, "R1", Controller.R1);
        }
        DrawPill(canvas, _select, "SELECT", Controller.Select);
        DrawPill(canvas, _start, "START", Controller.Start);
    }

    void DrawJoystick(Canvas canvas)
    {
        var originX = _joystickActive ? _joystickOriginX : _dpadX;
        var originY = _joystickActive ? _joystickOriginY : _dpadY;
        var knobX = _joystickActive ? _joystickKnobX : originX;
        var knobY = _joystickActive ? _joystickKnobY : originY;
        var accent = _settings.UseColors ? Sand : Neutral;
        var moving = (_pressedButtons &
            (Controller.Up | Controller.Right | Controller.Down | Controller.Left)) != 0;

        _paint.SetStyle(Paint.Style.Fill);
        _paint.Color = Color.Argb(_joystickActive ? 82 : 48, 8, 14, 20);
        canvas.DrawCircle(originX, originY, _joystickTravelRadius, _paint);

        _paint.SetStyle(Paint.Style.Stroke);
        _paint.StrokeWidth = Math.Max(2f, _radius * 0.055f);
        var baseAlpha = _settings.UseColors ? (_joystickActive ? 195 : 120) : (_joystickActive ? 145 : 90);
        _paint.Color = Color.Argb(baseAlpha, accent.R, accent.G, accent.B);
        canvas.DrawCircle(originX, originY, _joystickTravelRadius, _paint);

        _paint.SetStyle(Paint.Style.Fill);
        _paint.Color = moving
            ? Color.Argb(190, accent.R, accent.G, accent.B)
            : Color.Argb(_joystickActive ? 120 : 74, accent.R, accent.G, accent.B);
        canvas.DrawCircle(knobX, knobY, _radius * 0.58f, _paint);

        _paint.SetStyle(Paint.Style.Stroke);
        _paint.StrokeWidth = Math.Max(2f, _radius * 0.045f);
        _paint.Color = moving ? Color.White : Color.Argb(baseAlpha, accent.R, accent.G, accent.B);
        canvas.DrawCircle(knobX, knobY, _radius * 0.58f, _paint);
        _paint.SetStyle(Paint.Style.Fill);
    }

    void DrawCircleButton(Canvas canvas, RectF bounds, string label, Color accent, ushort bit)
    {
        var pressed = (_pressedButtons & bit) != 0;
        if (!_settings.UseColors) accent = Neutral;
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
        var outlineAlpha = _settings.UseColors ? 205 : 145;
        _paint.Color = pressed ? Color.White : Color.Argb(outlineAlpha, accent.R, accent.G, accent.B);
        canvas.DrawCircle(x, y, radius, _paint);
        _paint.SetStyle(Paint.Style.Fill);
    }

    void DrawPill(Canvas canvas, RectF bounds, string label, ushort bit)
    {
        var pressed = (_pressedButtons & bit) != 0;
        var accent = _settings.UseColors ? Gold : Neutral;
        _paint.SetStyle(Paint.Style.Fill);
        _paint.Color = pressed ? Color.Argb(190, accent.R, accent.G, accent.B) : Color.Argb(92, 8, 14, 20);
        canvas.DrawRoundRect(bounds, bounds.Height() * 0.42f, bounds.Height() * 0.42f, _paint);
        _paint.SetStyle(Paint.Style.Stroke);
        _paint.StrokeWidth = Math.Max(2f, _radius * 0.045f);
        var outlineAlpha = _settings.UseColors ? 205 : 145;
        _paint.Color = pressed ? Color.White : Color.Argb(outlineAlpha, accent.R, accent.G, accent.B);
        canvas.DrawRoundRect(bounds, bounds.Height() * 0.42f, bounds.Height() * 0.42f, _paint);
        _paint.SetStyle(Paint.Style.Fill);
        DrawCenteredLabel(canvas, label, bounds.CenterX(), bounds.CenterY(),
            Math.Max(_radius * 0.34f, bounds.Height() * 0.45f),
            pressed ? Color.White : (_settings.UseColors ? Sand : Neutral));
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
        if (_editing) return OnEditorTouch(e);

        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
            case MotionEventActions.PointerDown:
            {
                var index = e.ActionIndex;
                var x = e.GetX(index);
                var y = e.GetY(index);
                var pointerId = e.GetPointerId(index);
                if (!_joystickActive && ShouldStartJoystick(x, y))
                    BeginJoystick(pointerId, x, y);
                else
                    UpdatePointer(e, index);
                return true;
            }

            case MotionEventActions.Move:
                for (var i = 0; i < e.PointerCount; i++) UpdatePointer(e, i);
                return true;

            case MotionEventActions.Up:
            case MotionEventActions.PointerUp:
                for (var i = 0; i < e.PointerCount; i++)
                    if (i != e.ActionIndex) UpdatePointer(e, i, publish: false);
                var releasedPointer = e.GetPointerId(e.ActionIndex);
                _pointerButtons.Remove(releasedPointer);
                if (releasedPointer == _joystickPointerId) EndJoystick();
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

    bool OnEditorTouch(MotionEvent e)
    {
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
            case MotionEventActions.PointerDown:
            {
                var index = e.ActionIndex;
                if (_dragGroup != null) return true;
                _dragGroup = FindEditGroup(e.GetX(index), e.GetY(index));
                _dragPointerId = _dragGroup == null ? -1 : e.GetPointerId(index);
                return true;
            }
            case MotionEventActions.Move:
            {
                if (_dragGroup == null || _dragPointerId < 0) return true;
                var index = e.FindPointerIndex(_dragPointerId);
                if (index < 0) return true;
                _settings.Move(_dragGroup.Value,
                    e.GetX(index) / Math.Max(1f, Width),
                    e.GetY(index) / Math.Max(1f, Height));
                LayoutControls(Width, Height);
                Invalidate();
                return true;
            }
            case MotionEventActions.Up:
            case MotionEventActions.PointerUp:
                if (e.GetPointerId(e.ActionIndex) == _dragPointerId)
                {
                    _settings.CommitLayout();
                    _dragGroup = null;
                    _dragPointerId = -1;
                    PerformClick();
                }
                return true;
            case MotionEventActions.Cancel:
                _settings.CommitLayout();
                _dragGroup = null;
                _dragPointerId = -1;
                return true;
            default:
                return true;
        }
    }

    TouchControlGroup? FindEditGroup(float x, float y)
    {
        var reach = _radius * 1.65f;
        if (DistanceSquared(x, y, _dpadX, _dpadY) <= reach * reach)
            return TouchControlGroup.Dpad;
        if (DistanceSquared(x, y, _faceX, _faceY) <= reach * reach)
            return TouchControlGroup.FaceButtons;
        if (_select.Contains(x, y) || _start.Contains(x, y))
            return TouchControlGroup.SystemButtons;
        if (_settings.ShowShoulders &&
            (_l1.Contains(x, y) || _l2.Contains(x, y) || _r1.Contains(x, y) || _r2.Contains(x, y)))
            return TouchControlGroup.ShoulderButtons;
        return null;
    }

    static float DistanceSquared(float x1, float y1, float x2, float y2)
    {
        var dx = x1 - x2;
        var dy = y1 - y2;
        return dx * dx + dy * dy;
    }

    void UpdatePointer(MotionEvent e, int index, bool publish = true)
    {
        var pointerId = e.GetPointerId(index);
        if (pointerId == _joystickPointerId)
            UpdateJoystick(pointerId, e.GetX(index), e.GetY(index));
        else
            _pointerButtons[pointerId] = HitTest(e.GetX(index), e.GetY(index));
        if (publish) PublishState();
    }

    ushort HitTest(float x, float y)
    {
        ushort buttons = 0;
        if (CircleContains(_triangle, x, y, 1.15f)) buttons |= Controller.Triangle;
        if (CircleContains(_circle, x, y, 1.15f)) buttons |= Controller.Circle;
        if (CircleContains(_cross, x, y, 1.15f)) buttons |= Controller.Cross;
        if (CircleContains(_square, x, y, 1.15f)) buttons |= Controller.Square;
        if (_settings.ShowShoulders)
        {
            if (_l1.Contains(x, y)) buttons |= Controller.L1;
            if (_l2.Contains(x, y)) buttons |= Controller.L2;
            if (_r1.Contains(x, y)) buttons |= Controller.R1;
            if (_r2.Contains(x, y)) buttons |= Controller.R2;
        }
        if (_select.Contains(x, y)) buttons |= Controller.Select;
        if (_start.Contains(x, y)) buttons |= Controller.Start;
        return buttons;
    }

    bool ShouldStartJoystick(float x, float y)
    {
        if (x >= Width * 0.48f || y <= Height * 0.16f) return false;
        if (_select.Contains(x, y) || _start.Contains(x, y)) return false;
        if (_settings.ShowShoulders && (_l1.Contains(x, y) || _l2.Contains(x, y))) return false;
        return true;
    }

    void BeginJoystick(int pointerId, float x, float y)
    {
        _joystickActive = true;
        _joystickPointerId = pointerId;
        _joystickOriginX = _joystickKnobX = x;
        _joystickOriginY = _joystickKnobY = y;
        _pointerButtons[pointerId] = 0;
        PublishState();
        Invalidate();
    }

    void UpdateJoystick(int pointerId, float x, float y)
    {
        var dx = x - _joystickOriginX;
        var dy = y - _joystickOriginY;
        var distance = MathF.Sqrt(dx * dx + dy * dy);
        var knobScale = distance > _joystickTravelRadius && distance > 0
            ? _joystickTravelRadius / distance
            : 1f;
        _joystickKnobX = _joystickOriginX + dx * knobScale;
        _joystickKnobY = _joystickOriginY + dy * knobScale;
        _pointerButtons[pointerId] = JoystickDirection(dx, dy, distance);
        Invalidate();
    }

    ushort JoystickDirection(float dx, float dy, float distance)
    {
        if (distance < _radius * 0.22f) return 0;

        var absX = Math.Abs(dx);
        var absY = Math.Abs(dy);
        const float cardinalRatio = 2.41421356f;

        if (absX > absY * cardinalRatio)
            return dx < 0 ? Controller.Left : Controller.Right;
        if (absY > absX * cardinalRatio)
            return dy < 0 ? Controller.Up : Controller.Down;

        ushort diagonal = dx < 0 ? Controller.Left : Controller.Right;
        diagonal |= dy < 0 ? Controller.Up : Controller.Down;
        return diagonal;
    }

    void EndJoystick()
    {
        _joystickActive = false;
        _joystickPointerId = -1;
        _joystickOriginX = _joystickKnobX = _dpadX;
        _joystickOriginY = _joystickKnobY = _dpadY;
        Invalidate();
    }

    static bool CircleContains(RectF bounds, float x, float y, float radiusScale = 1f)
    {
        var dx = x - bounds.CenterX();
        var dy = y - bounds.CenterY();
        var radius = bounds.Width() * 0.5f * radiusScale;
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
        var hadJoystick = _joystickActive;
        _pointerButtons.Clear();
        EndJoystick();
        if (_pressedButtons == 0)
        {
            if (hadJoystick) Invalidate();
            return;
        }
        _pressedButtons = 0;
        Controller.SetVirtualPadState(0);
        Invalidate();
    }

    public void DismissInput() => ReleaseAll();

    protected override void OnDetachedFromWindow()
    {
        ReleaseAll();
        base.OnDetachedFromWindow();
    }
}
