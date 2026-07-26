using Silk.NET.SDL;

namespace CrashBandicoot.Launcher.Ui;

/// <summary>
/// Lightweight SDL GameController poller for the WinForms launcher menu.
/// Releases the pad while the UI is hidden so the runtime can open it in-game.
/// </summary>
internal unsafe sealed class LauncherGamepad : IDisposable
{
    const short StickDeadzone = 16000;
    const int RepeatDelayTicks = 12; // ~400 ms at 33 ms tick
    const int RepeatIntervalTicks = 5;

    Sdl? _sdl;
    GameController* _pad;
    bool _ownSubsystem;
    bool _disposed;

    bool _up, _down, _left, _right, _confirm, _cancel;
    bool _prevUp, _prevDown, _prevLeft, _prevRight, _prevConfirm, _prevCancel;
    int _repeatDir; // 1=up 2=down 3=left 4=right
    int _repeatTicks;
    int _rescanCooldown;

    public bool IsConnected => _pad != null;

    public bool TryEnsureInit()
    {
        if (_disposed) return false;
        if (_sdl != null) return true;
        try
        {
            _sdl = Sdl.GetApi();
            _sdl.SetHint("SDL_JOYSTICK_RAWINPUT", "0");
            // Init may already be up if something else touched SDL; track ownership lightly.
            if (_sdl.WasInit(Sdl.InitGamecontroller) == 0)
            {
                if (_sdl.InitSubSystem(Sdl.InitGamecontroller) != 0)
                {
                    _sdl.Dispose();
                    _sdl = null;
                    return false;
                }
                _ownSubsystem = true;
            }
            Rescan();
            return true;
        }
        catch
        {
            _sdl?.Dispose();
            _sdl = null;
            return false;
        }
    }

    public void Release()
    {
        ClosePad();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClosePad();
        if (_ownSubsystem && _sdl != null)
        {
            try { _sdl.QuitSubSystem(Sdl.InitGamecontroller); }
            catch { /* ignore */ }
        }
        _sdl?.Dispose();
        _sdl = null;
    }

    /// <summary>Poll once per UI tick. Returns edge/repeat navigation actions.</summary>
    public LauncherPadAction Poll()
    {
        if (_disposed || _sdl == null) return LauncherPadAction.None;

        DrainHotplug();
        if (_pad == null)
        {
            if (--_rescanCooldown <= 0)
            {
                _rescanCooldown = 30;
                Rescan();
            }
            ClearHeld();
            return LauncherPadAction.None;
        }

        Sample();
        var action = LauncherPadAction.None;

        if (Edge(ref _prevConfirm, _confirm)) action |= LauncherPadAction.Confirm;
        if (Edge(ref _prevCancel, _cancel)) action |= LauncherPadAction.Cancel;

        action |= DirWithRepeat(1, ref _prevUp, _up);
        action |= DirWithRepeat(2, ref _prevDown, _down);
        action |= DirWithRepeat(3, ref _prevLeft, _left);
        action |= DirWithRepeat(4, ref _prevRight, _right);

        return action;
    }

    LauncherPadAction DirWithRepeat(int dir, ref bool prev, bool now)
    {
        if (Edge(ref prev, now))
        {
            _repeatDir = dir;
            _repeatTicks = 0;
            return DirAction(dir);
        }

        if (!now)
        {
            if (_repeatDir == dir) _repeatDir = 0;
            return LauncherPadAction.None;
        }

        if (_repeatDir != dir) return LauncherPadAction.None;
        _repeatTicks++;
        if (_repeatTicks == RepeatDelayTicks ||
            (_repeatTicks > RepeatDelayTicks && (_repeatTicks - RepeatDelayTicks) % RepeatIntervalTicks == 0))
            return DirAction(dir);
        return LauncherPadAction.None;
    }

    static LauncherPadAction DirAction(int dir) => dir switch
    {
        1 => LauncherPadAction.Up,
        2 => LauncherPadAction.Down,
        3 => LauncherPadAction.Left,
        4 => LauncherPadAction.Right,
        _ => LauncherPadAction.None,
    };

    static bool Edge(ref bool prev, bool now)
    {
        var edge = now && !prev;
        prev = now;
        return edge;
    }

    void Sample()
    {
        if (_pad == null || _sdl == null)
        {
            ClearHeld();
            return;
        }

        _up = Button(GameControllerButton.DpadUp) || Stick(GameControllerAxis.Lefty, negative: true);
        _down = Button(GameControllerButton.DpadDown) || Stick(GameControllerAxis.Lefty, negative: false);
        _left = Button(GameControllerButton.DpadLeft) || Stick(GameControllerAxis.Leftx, negative: true);
        _right = Button(GameControllerButton.DpadRight) || Stick(GameControllerAxis.Leftx, negative: false);
        // A / Cross = confirm, B / Circle = cancel (SDL south/east face buttons).
        _confirm = Button(GameControllerButton.A) || Button(GameControllerButton.Start);
        _cancel = Button(GameControllerButton.B) || Button(GameControllerButton.Back);
    }

    bool Button(GameControllerButton b) =>
        _sdl != null && _pad != null && _sdl.GameControllerGetButton(_pad, b) != 0;

    bool Stick(GameControllerAxis axis, bool negative)
    {
        if (_sdl == null || _pad == null) return false;
        short v = _sdl.GameControllerGetAxis(_pad, axis);
        return negative ? v < -StickDeadzone : v > StickDeadzone;
    }

    void DrainHotplug()
    {
        if (_sdl == null) return;
        Event ev;
        var changed = false;
        while (_sdl.PollEvent(&ev) != 0)
        {
            if (ev.Type is (uint)EventType.Controllerdeviceadded or (uint)EventType.Controllerdeviceremoved)
                changed = true;
        }
        if (changed) Rescan();
    }

    void Rescan()
    {
        if (_sdl == null) return;
        ClosePad();
        int n = _sdl.NumJoysticks();
        for (int i = 0; i < n; i++)
        {
            if (_sdl.IsGameController(i) != SdlBool.True) continue;
            var ctrl = _sdl.GameControllerOpen(i);
            if (ctrl == null) continue;
            _pad = ctrl;
            break;
        }
        ClearHeld();
        _prevUp = _prevDown = _prevLeft = _prevRight = _prevConfirm = _prevCancel = false;
    }

    void ClosePad()
    {
        if (_pad != null && _sdl != null)
        {
            _sdl.GameControllerClose(_pad);
            _pad = null;
        }
        ClearHeld();
        _repeatDir = 0;
        _repeatTicks = 0;
    }

    void ClearHeld() => _up = _down = _left = _right = _confirm = _cancel = false;
}

[Flags]
internal enum LauncherPadAction
{
    None = 0,
    Up = 1,
    Down = 2,
    Left = 4,
    Right = 8,
    Confirm = 16,
    Cancel = 32,
}
