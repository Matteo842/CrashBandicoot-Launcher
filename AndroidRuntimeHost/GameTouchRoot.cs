using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using Java.Lang;

namespace CrashBandicoot.AndroidRuntime;

/// <summary>
/// Game FrameLayout that steals a three-finger hold so the virtual pad never
/// sees it, then toggles the developer menu after 500 ms.
/// </summary>
sealed class GameTouchRoot : FrameLayout
{
    const int HoldMilliseconds = 500;

    readonly Handler _handler;
    IRunnable? _pending;

    public Action? ThreeFingerHold { get; set; }

    public GameTouchRoot(Context context) : base(context)
    {
        _handler = new Handler(Looper.MainLooper!);
    }

    public override bool OnInterceptTouchEvent(MotionEvent? e)
    {
        if (e == null) return false;
        if (e.PointerCount >= 3)
        {
            RecompOne.Runtime.Hardware.Controller.SetVirtualPadState(0);
            if (e.ActionMasked is MotionEventActions.Down or MotionEventActions.PointerDown)
                ArmHold();
            return true;
        }

        CancelHold();
        return false;
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e == null) return false;
        if (e.PointerCount >= 3)
        {
            if (e.ActionMasked is MotionEventActions.Up or MotionEventActions.Cancel
                or MotionEventActions.PointerUp)
            {
                if (e.PointerCount <= 3)
                    CancelHold();
            }
            return true;
        }

        CancelHold();
        return false;
    }

    void ArmHold()
    {
        CancelHold();
        _pending = new Runnable(FireHold);
        _handler.PostDelayed(_pending, HoldMilliseconds);
    }

    void CancelHold()
    {
        if (_pending == null) return;
        _handler.RemoveCallbacks(_pending);
        _pending = null;
    }

    void FireHold()
    {
        _pending = null;
        ThreeFingerHold?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) CancelHold();
        base.Dispose(disposing);
    }
}
