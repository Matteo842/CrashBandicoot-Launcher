using Android.App;
using Android.Content;
using Android.OS;
using Android.Views;
using MotionAxis = Android.Views.Axis;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Hardware;
using InputManager = Android.Hardware.Input.InputManager;

namespace CrashBandicoot.AndroidRuntime;

enum GamepadRoute
{
    Launcher,
    Gameplay,
    DevMenu,
}

[Flags]
enum LauncherPadAction
{
    None = 0,
    Up = 1,
    Down = 2,
    Left = 4,
    Right = 8,
    Confirm = 16,
    Cancel = 32,
}

/// <summary>
/// Android HID gamepad → the same SDL-style bindings the desktop host uses.
/// Crash 1 is digital; left stick and hat also feed the D-pad.
/// </summary>
static class AndroidGamepad
{
    const float StickDeadzone = 0.50f;
    const float TriggerThreshold = 0.25f;
    const int RepeatDelayMs = 400;
    const int RepeatIntervalMs = 165;

    const int SdlA = 0, SdlB = 1, SdlX = 2, SdlY = 3;
    const int SdlBack = 4, SdlGuide = 5, SdlStart = 6;
    const int SdlL3 = 7, SdlR3 = 8, SdlL1 = 9, SdlR1 = 10;
    const int SdlDup = 11, SdlDdown = 12, SdlDleft = 13, SdlDright = 14;
    const int SdlL2 = 100, SdlR2 = 101;
    const int SdlLsLeft = 102, SdlLsRight = 103, SdlLsUp = 104, SdlLsDown = 105;
    const int SdlRsLeft = 106, SdlRsRight = 107, SdlRsUp = 108, SdlRsDown = 109;

    static readonly object Gate = new();
    static readonly HashSet<int> _held = [];
    static readonly Handler _repeat = new(Looper.MainLooper!);
    static readonly HashSet<int> _connectedIds = [];

    static float _lx, _ly, _rx, _ry, _lt, _rt;
    static int _hatX, _hatY;
    static int _repeatDir;
    static bool _wasConnected;
    static InputManager? _inputManager;
    static DeviceListener? _listener;
    static int _rumbleDeviceId = -1;

    public static Func<GamepadRoute>? ResolveRoute { get; set; }
    public static Action<LauncherPadAction>? LauncherInput { get; set; }
    public static Action? CloseDevMenu { get; set; }
    public static Action<bool>? ConnectionChanged { get; set; }

    public static bool IsConnected
    {
        get { lock (Gate) return _connectedIds.Count > 0; }
    }

    public static void Attach(Activity activity)
    {
        Controller.RumbleRequested = SetRumble;
        _inputManager = (InputManager?)activity.GetSystemService(Context.InputService);
        _listener = new DeviceListener();
        _inputManager?.RegisterInputDeviceListener(_listener, _repeat);
        Rescan();
    }

    public static void Detach()
    {
        Controller.RumbleRequested = null;
        if (_inputManager != null && _listener != null)
            _inputManager.UnregisterInputDeviceListener(_listener);
        _inputManager = null;
        _listener = null;
        _repeat.RemoveCallbacksAndMessages(null);
        lock (Gate)
        {
            _held.Clear();
            _connectedIds.Clear();
        }
        PublishGameplay(forceClear: true);
    }

    public static void ReleaseHeld()
    {
        lock (Gate)
        {
            _held.Clear();
            _lx = _ly = _rx = _ry = _lt = _rt = 0;
            _hatX = _hatY = 0;
        }
        _repeatDir = 0;
        _repeat.RemoveCallbacksAndMessages(null);
        PublishGameplay(forceClear: true);
    }

    public static void SyncGameplay() => PublishGameplay();

    public static void Rescan()
    {
        var ids = InputDevice.GetDeviceIds() ?? [];
        bool connected;
        lock (Gate)
        {
            _connectedIds.Clear();
            foreach (var id in ids)
            {
                if (!IsPadDevice(InputDevice.GetDevice(id))) continue;
                _connectedIds.Add(id);
                if (_rumbleDeviceId < 0) _rumbleDeviceId = id;
            }
            if (_rumbleDeviceId >= 0 && !_connectedIds.Contains(_rumbleDeviceId))
                _rumbleDeviceId = _connectedIds.Count > 0 ? _connectedIds.First() : -1;
            connected = _connectedIds.Count > 0;
            if (!connected)
            {
                _held.Clear();
                _lx = _ly = _rx = _ry = _lt = _rt = 0;
                _hatX = _hatY = 0;
            }
        }

        PublishGameplay(forceClear: !connected);
        NotifyConnection(connected);
    }

    public static void BindDialog(Dialog dialog)
    {
        dialog.SetOnKeyListener(new DialogKeyListener());
    }

    public static bool TryHandleKey(KeyEvent e)
    {
        if (!IsPadKey(e.KeyCode) && !(e.KeyCode == Keycode.Back && IsFromPad(e)))
            return false;
        if (!IsFromPad(e) && e.KeyCode != Keycode.Back)
        {
            // D-pad keys can arrive as SOURCE_DPAD on a real pad.
            if (!IsPadDevice(e.Device)) return false;
        }

        RememberDevice(e.DeviceId);

        var sdl = ToSdlButton(e.KeyCode);
        var down = e.Action == KeyEventActions.Down;
        if (sdl >= 0)
        {
            lock (Gate)
            {
                if (down) _held.Add(sdl);
                else _held.Remove(sdl);
            }
        }

        var route = ResolveRoute?.Invoke() ?? GamepadRoute.Launcher;
        if (route == GamepadRoute.Gameplay)
        {
            PublishGameplay();
            return true;
        }

        if (route == GamepadRoute.DevMenu)
        {
            PublishGameplay(forceClear: true);
            if (down && e.RepeatCount == 0 && IsCancelKey(e.KeyCode, e))
                CloseDevMenu?.Invoke();
            return true;
        }

        PublishGameplay(forceClear: true);
        if (!down) return true;
        var action = LauncherActionFromKey(e.KeyCode, e);
        if (action == LauncherPadAction.None) return true;
        if (IsDirection(action) || e.RepeatCount == 0)
            LauncherInput?.Invoke(action);
        return true;
    }

    public static bool TryHandleMotion(MotionEvent e)
    {
        if (!IsPadDevice(e.Device)) return false;
        RememberDevice(e.DeviceId);

        lock (Gate)
        {
            _lx = ReadAxis(e, MotionAxis.X);
            _ly = ReadAxis(e, MotionAxis.Y);
            _rx = FirstAxis(e, MotionAxis.Z, MotionAxis.Rx);
            _ry = FirstAxis(e, MotionAxis.Rz, MotionAxis.Ry);
            _lt = Trigger(e, MotionAxis.Ltrigger, MotionAxis.Brake);
            _rt = Trigger(e, MotionAxis.Rtrigger, MotionAxis.Gas);
            _hatX = Hat(e, MotionAxis.HatX);
            _hatY = Hat(e, MotionAxis.HatY);
        }

        var route = ResolveRoute?.Invoke() ?? GamepadRoute.Launcher;
        if (route == GamepadRoute.Gameplay)
        {
            PublishGameplay();
            return true;
        }

        PublishGameplay(forceClear: true);
        if (route == GamepadRoute.DevMenu) return true;

        UpdateLauncherStick();
        return true;
    }

    public static bool IsFromPad(InputEvent e) => IsPadDevice(e.Device);

    static bool IsCancelKey(Keycode key, KeyEvent e) =>
        key is Keycode.ButtonB or Keycode.ButtonSelect
        || (key == Keycode.Back && IsFromPad(e));

    static void PublishGameplay(bool forceClear = false)
    {
        bool connected;
        ushort buttons;
        byte lx, ly, rx, ry;
        lock (Gate)
        {
            connected = _connectedIds.Count > 0;
            if (forceClear || !connected)
            {
                buttons = 0;
                lx = ly = rx = ry = 0x80;
            }
            else
            {
                buttons = Bind(ConfigManager.Game.Pad, SnapshotSdl());
                lx = AxisToByte(_lx);
                ly = AxisToByte(_ly);
                rx = AxisToByte(_rx);
                ry = AxisToByte(_ry);
            }
        }

        var send = !forceClear && connected
                   && ResolveRoute?.Invoke() == GamepadRoute.Gameplay;
        Controller.SetPhysicalPadState(
            send ? buttons : (ushort)0, lx, ly, rx, ry, connected);
    }

    static HashSet<int> SnapshotSdl()
    {
        var pressed = new HashSet<int>(_held);
        if (_lt > TriggerThreshold) pressed.Add(SdlL2);
        if (_rt > TriggerThreshold) pressed.Add(SdlR2);
        if (_hatY < 0) pressed.Add(SdlDup);
        if (_hatY > 0) pressed.Add(SdlDdown);
        if (_hatX < 0) pressed.Add(SdlDleft);
        if (_hatX > 0) pressed.Add(SdlDright);
        AddStick(pressed, _lx, _ly, SdlLsLeft, SdlLsRight, SdlLsUp, SdlLsDown);
        AddStick(pressed, _rx, _ry, SdlRsLeft, SdlRsRight, SdlRsUp, SdlRsDown);
        return pressed;
    }

    static void AddStick(HashSet<int> pressed, float x, float y,
        int left, int right, int up, int down)
    {
        if (x <= -StickDeadzone) pressed.Add(left);
        if (x >= StickDeadzone) pressed.Add(right);
        if (y <= -StickDeadzone) pressed.Add(up);
        if (y >= StickDeadzone) pressed.Add(down);
    }

    static ushort Bind(GamepadBindings pad, HashSet<int> sdl)
    {
        ushort s = 0;
        void Map(int[] bindings, ushort bit)
        {
            foreach (var b in bindings)
            {
                if (!sdl.Contains(b)) continue;
                s |= bit;
                return;
            }
        }

        Map(pad.Cross, Controller.Cross);
        Map(pad.Circle, Controller.Circle);
        Map(pad.Square, Controller.Square);
        Map(pad.Triangle, Controller.Triangle);
        Map(pad.L1, Controller.L1);
        Map(pad.R1, Controller.R1);
        Map(pad.L2, Controller.L2);
        Map(pad.R2, Controller.R2);
        Map(pad.L3, Controller.L3);
        Map(pad.R3, Controller.R3);
        Map(pad.Start, Controller.Start);
        Map(pad.Select, Controller.Select);
        Map(pad.Up, Controller.Up);
        Map(pad.Down, Controller.Down);
        Map(pad.Left, Controller.Left);
        Map(pad.Right, Controller.Right);
        return s;
    }

    static void UpdateLauncherStick()
    {
        int dir;
        lock (Gate)
        {
            dir = 0;
            if (_ly <= -StickDeadzone || _hatY < 0) dir = 1;
            else if (_ly >= StickDeadzone || _hatY > 0) dir = 2;
            else if (_lx <= -StickDeadzone || _hatX < 0) dir = 3;
            else if (_lx >= StickDeadzone || _hatX > 0) dir = 4;
        }

        if (dir == 0)
        {
            _repeatDir = 0;
            _repeat.RemoveCallbacksAndMessages(null);
            return;
        }

        if (dir == _repeatDir) return;
        _repeatDir = dir;
        _repeat.RemoveCallbacksAndMessages(null);
        LauncherInput?.Invoke(DirAction(dir));
        _repeat.PostDelayed(StickRepeat, RepeatDelayMs);
    }

    static void StickRepeat()
    {
        if (_repeatDir == 0) return;
        LauncherInput?.Invoke(DirAction(_repeatDir));
        _repeat.PostDelayed(StickRepeat, RepeatIntervalMs);
    }

    static LauncherPadAction DirAction(int dir) => dir switch
    {
        1 => LauncherPadAction.Up,
        2 => LauncherPadAction.Down,
        3 => LauncherPadAction.Left,
        4 => LauncherPadAction.Right,
        _ => LauncherPadAction.None,
    };

    static LauncherPadAction LauncherActionFromKey(Keycode key, KeyEvent e)
    {
        if (IsCancelKey(key, e)) return LauncherPadAction.Cancel;
        return key switch
        {
            Keycode.DpadUp => LauncherPadAction.Up,
            Keycode.DpadDown => LauncherPadAction.Down,
            Keycode.DpadLeft => LauncherPadAction.Left,
            Keycode.DpadRight => LauncherPadAction.Right,
            Keycode.ButtonA or Keycode.ButtonStart or Keycode.DpadCenter
                => LauncherPadAction.Confirm,
            _ => LauncherPadAction.None,
        };
    }

    static bool IsDirection(LauncherPadAction action) =>
        action is LauncherPadAction.Up or LauncherPadAction.Down
            or LauncherPadAction.Left or LauncherPadAction.Right;

    static int ToSdlButton(Keycode key) => key switch
    {
        Keycode.ButtonA or Keycode.DpadCenter => SdlA,
        Keycode.ButtonB => SdlB,
        Keycode.ButtonX => SdlX,
        Keycode.ButtonY => SdlY,
        Keycode.ButtonSelect => SdlBack,
        Keycode.ButtonMode => SdlGuide,
        Keycode.ButtonStart => SdlStart,
        Keycode.ButtonThumbl => SdlL3,
        Keycode.ButtonThumbr => SdlR3,
        Keycode.ButtonL1 => SdlL1,
        Keycode.ButtonR1 => SdlR1,
        Keycode.ButtonL2 => SdlL2,
        Keycode.ButtonR2 => SdlR2,
        Keycode.DpadUp => SdlDup,
        Keycode.DpadDown => SdlDdown,
        Keycode.DpadLeft => SdlDleft,
        Keycode.DpadRight => SdlDright,
        _ => -1,
    };

    static bool IsPadKey(Keycode key) => ToSdlButton(key) >= 0;

    static bool IsPadDevice(InputDevice? device)
    {
        if (device == null || device.IsVirtual) return false;
        var sources = device.Sources;
        return sources.HasFlag(InputSourceType.Gamepad)
               || sources.HasFlag(InputSourceType.Joystick);
    }

    static void RememberDevice(int deviceId)
    {
        if (deviceId < 0) return;
        bool became;
        lock (Gate)
        {
            became = _connectedIds.Add(deviceId);
            if (_rumbleDeviceId < 0) _rumbleDeviceId = deviceId;
        }
        if (became) NotifyConnection(true);
    }

    static void NotifyConnection(bool connected)
    {
        if (connected == _wasConnected) return;
        _wasConnected = connected;
        ConnectionChanged?.Invoke(connected);
    }

    static float ReadAxis(MotionEvent e, MotionAxis axis)
    {
        var range = e.Device?.GetMotionRange(axis);
        if (range == null) return 0f;
        return Math.Clamp(e.GetAxisValue(axis), -1f, 1f);
    }

    static float FirstAxis(MotionEvent e, MotionAxis primary, MotionAxis fallback)
    {
        if (e.Device?.GetMotionRange(primary) != null)
            return Math.Clamp(e.GetAxisValue(primary), -1f, 1f);
        return ReadAxis(e, fallback);
    }

    static float Trigger(MotionEvent e, MotionAxis primary, MotionAxis fallback)
    {
        var axis = e.Device?.GetMotionRange(primary) != null ? primary : fallback;
        return Math.Clamp(e.GetAxisValue(axis), 0f, 1f);
    }

    static int Hat(MotionEvent e, MotionAxis axis)
    {
        var v = e.GetAxisValue(axis);
        if (v <= -0.5f) return -1;
        if (v >= 0.5f) return 1;
        return 0;
    }

    static byte AxisToByte(float axis)
    {
        var f = Math.Clamp(axis * 1.3f, -1f, 1f);
        return (byte)Math.Clamp((int)MathF.Round((f + 1f) * 127.5f), 0, 255);
    }

    static void SetRumble(byte large, byte small)
    {
        int id;
        lock (Gate) id = _rumbleDeviceId;
        if (id < 0) return;
        var device = InputDevice.GetDevice(id);
        var vibrator = device?.Vibrator;
        if (vibrator == null || !vibrator.HasVibrator) return;
        try
        {
            if (large == 0 && small == 0)
            {
                vibrator.Cancel();
                return;
            }

            var amplitude = Math.Clamp(Math.Max(large, small) * 2, 1, 255);
            vibrator.Vibrate(VibrationEffect.CreateOneShot(500, amplitude));
        }
        catch
        {
            /* some pads expose a vibrator that rejects app-driven effects */
        }
    }

    sealed class DeviceListener : Java.Lang.Object, InputManager.IInputDeviceListener
    {
        public void OnInputDeviceAdded(int deviceId) => Rescan();
        public void OnInputDeviceRemoved(int deviceId) => Rescan();
        public void OnInputDeviceChanged(int deviceId) => Rescan();
    }

    sealed class DialogKeyListener : Java.Lang.Object, IDialogInterfaceOnKeyListener
    {
        public bool OnKey(IDialogInterface? dialog, Keycode keyCode, KeyEvent? e)
        {
            if (e == null || e.Action != KeyEventActions.Down || e.RepeatCount != 0)
                return false;
            if (!IsCancelKey(keyCode, e)) return false;
            dialog?.Dismiss();
            return true;
        }
    }
}
