using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Input;
using Silk.NET.SDL;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Hardware;
using EventBus = RecompOne.Runtime.Events.Event;
using KeyboardEvent = RecompOne.Runtime.Events.KeyboardEvent;
using MouseEvent = RecompOne.Runtime.Events.MouseEvent;
using ControllerEvent = RecompOne.Runtime.Events.ControllerEvent;
using MouseAction = RecompOne.Runtime.Events.MouseAction;
using EvMouseButton = RecompOne.Runtime.Events.MouseButton;

namespace RecompOne.Runtime.Host;

internal static unsafe class InputManager
{
    static IKeyboard?_keyboard;
    static IMouse?_mouse;
    static Sdl?_sdl;
    static GameController* _pad0;
    static GameController* _pad1;

    const int AxisThreshold = 8000;
    const int StickThreshold = 16000;
    const int LeftTrigger = 100;
    const int RightTrigger = 101;
    const int LeftStickLeft = 102;
    const int LeftStickRight = 103;
    const int LeftStickUp = 104;
    const int LeftStickDown = 105;
    const int RightStickLeft = 106;
    const int RightStickRight = 107;
    const int RightStickUp = 108;
    const int RightStickDown = 109;
    static bool _topBarToggle;
    static bool _fullscreenToggle;
    static bool _sessionMarker;
    static bool _cheatMenuToggle;
    static bool _pauseMenuToggle;
    static readonly HashSet<Key> _keysDown = [];

    
    public static bool ConsumeTopBarToggle() { var v = _topBarToggle; _topBarToggle = false; return v; }
    public static bool ConsumeFullscreenToggle(){ var v = _fullscreenToggle; _fullscreenToggle = false; return v; }
    public static bool ConsumeSessionMarker() { var v = _sessionMarker; _sessionMarker = false; return v; }
    public static bool ConsumeCheatMenuToggle() { var v = _cheatMenuToggle; _cheatMenuToggle = false; return v; }
    public static bool ConsumePauseMenuToggle() { var v = _pauseMenuToggle; _pauseMenuToggle = false; return v; }

    /// <summary>Host / async-key path can request a toggle without Silk KeyDown.</summary>
    public static void RequestFullscreenToggle() => _fullscreenToggle = true;

    /// <summary>Host / async-key path can request a cheat-menu toggle without Silk KeyDown.</summary>
    public static void RequestCheatMenuToggle() => _cheatMenuToggle = true;

    /// <summary>Host / async-key path can request a pause-menu toggle without Silk KeyDown.</summary>
    public static void RequestPauseMenuToggle() => _pauseMenuToggle = true;

    public static void Initialize(IInputContext input)
    {
        if (input.Keyboards.Count > 0)
        {
            _keyboard = input.Keyboards[0];
            _keyboard.KeyDown += OnKeyDown;
            _keyboard.KeyUp += OnKeyUp;
        }

        if (input.Mice.Count > 0)
        {
            _mouse = input.Mice[0];
            _mouse.MouseMove += OnMouseMove;
            _mouse.MouseDown += OnMouseDown;
            _mouse.MouseUp += OnMouseUp;
            _mouse.Scroll += OnScroll;
        }


        try
        {
            _sdl = Sdl.GetApi();
            _sdl.SetHint("SDL_JOYSTICK_RAWINPUT", "0");
            _sdl.InitSubSystem(Sdl.InitGamecontroller);
            Rescan();
        }
        catch { _sdl = null; }
    }

    public static bool IsConnected => _pad0 != null;

    public static bool IsPadConnected(int pad) => pad == 0 ? _pad0 != null : _pad1 != null;

    // Prefer our own edge-tracked set: IsKeyPressed is unreliable with a manual DoEvents pump.
    public static bool IsKeyDown(Key k) => IsKeyDownReconciled(k);

    static bool IsKeyDownReconciled(Key k)
    {
        if (_keyboard != null)
        {
            try
            {
                bool hw = _keyboard.IsKeyPressed(k);
                if (!hw) _keysDown.Remove(k);
                else _keysDown.Add(k);
                return hw;
            }
            catch
            {
                // fall through to tracked set
            }
        }
        return _keysDown.Contains(k);
    }

    public static void Poll()
    {
        PollFullscreenHotkeys();
        PollCheatMenuHotkey();
        PollPauseMenuHotkey();
        PollGamepadEvents();
        PollKeyboard();
        PollGamepads();
        Controller.Connected2 = _pad1 != null || HasAnyKey(ConfigManager.Game.Keys2);
    }

    // Embedded child HWND often doesn't get Silk KeyDown (focus on parent / lost focus).
    // Edge-detect F11 / Alt+Enter / cheat menu / Esc via GetAsyncKeyState so toggles always work.
    static bool _asyncF11;
    static bool _asyncAltEnter;
    static bool _asyncCheatMenu;
    static bool _asyncPauseMenu;
    const int VkF11 = 0x7A;
    const int VkMenu = 0x12;   // either Alt
    const int VkReturn = 0x0D;
    const int VkEscape = 0x1B;

    static void PollFullscreenHotkeys()
    {
        // Win32 fallback for embedded HWND (focus often stays on the WinForms parent).
        // Standalone Silk windows get F11 / Alt+Enter via OnKeyDown.
        if (!OperatingSystem.IsWindows()) return;

        bool f11 = (GetAsyncKeyState(VkF11) & 0x8000) != 0;
        if (f11 && !_asyncF11) _fullscreenToggle = true;
        _asyncF11 = f11;

        bool alt = (GetAsyncKeyState(VkMenu) & 0x8000) != 0;
        bool enter = (GetAsyncKeyState(VkReturn) & 0x8000) != 0;
        bool altEnter = alt && enter;
        if (altEnter && !_asyncAltEnter) _fullscreenToggle = true;
        _asyncAltEnter = altEnter;
    }

    static void PollCheatMenuHotkey()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (SilkKeyToVk(ResolveCheatMenuKey()) is not int vk) return;
        bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
        if (down && !_asyncCheatMenu) _cheatMenuToggle = true;
        _asyncCheatMenu = down;
    }

    static void PollPauseMenuHotkey()
    {
        if (!OperatingSystem.IsWindows()) return;
        bool down = (GetAsyncKeyState(VkEscape) & 0x8000) != 0;
        if (down && !_asyncPauseMenu) _pauseMenuToggle = true;
        _asyncPauseMenu = down;
    }

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    public static int? GetFirstPressedPadButton(int pad = 0)
    {
        var ctrl = pad == 0 ? _pad0 : _pad1;
        if (_sdl == null || ctrl == null) return null;
        for (int b = 0; b < (int)GameControllerButton.Max; b++)
            if (_sdl.GameControllerGetButton(ctrl, (GameControllerButton)b) != 0)
                return b;
        if (Pressed(ctrl, LeftTrigger)) return LeftTrigger;
        if (Pressed(ctrl, RightTrigger)) return RightTrigger;
        for (int b = LeftStickLeft; b <= RightStickDown; b++)
            if (Pressed(ctrl, b)) return b;
        return null;
    }

    static bool IsStickBinding(int b) => b is >= LeftStickLeft and <= RightStickDown;

    static (GameControllerAxis Axis, bool Positive) AxisBinding(int b) => b switch
    {
        LeftStickLeft   => (GameControllerAxis.Leftx,  false),
        LeftStickRight  => (GameControllerAxis.Leftx,  true),
        LeftStickUp     => (GameControllerAxis.Lefty,  false),
        LeftStickDown   => (GameControllerAxis.Lefty,  true),
        RightStickLeft  => (GameControllerAxis.Rightx, false),
        RightStickRight => (GameControllerAxis.Rightx, true),
        RightStickUp    => (GameControllerAxis.Righty, false),
        _               => (GameControllerAxis.Righty, true),
    };

    public static void Shutdown()
    {
        CloseControllers();
        _sdl?.QuitSubSystem(Sdl.InitGamecontroller);
        _sdl?.Dispose();
        _sdl = null;
    }

    static void PollGamepadEvents()
    {
        if (_sdl == null) return;
        Silk.NET.SDL.Event ev;
        bool changed = false;
        bool anyCtrl = EventBus.HasAnyListeners<ControllerEvent>();
        while (_sdl.PollEvent(&ev) != 0)
        {
            if (ev.Type == (uint)EventType.Controllerdeviceadded) changed = true;
            if (ev.Type == (uint)EventType.Controllerdeviceremoved) changed = true;
            if (!anyCtrl) continue;
            if (ev.Type == (uint)EventType.Controllerbuttondown || ev.Type == (uint)EventType.Controllerbuttonup)
                EventBus.Dispatch(new ControllerEvent
                {
                    Device = ev.Cbutton.Which,
                    Button = ev.Cbutton.Button,
                    Pressed = ev.Type == (uint)EventType.Controllerbuttondown,
                });
            else if (ev.Type == (uint)EventType.Controlleraxismotion)
                EventBus.Dispatch(new ControllerEvent
                {
                    Device = ev.Caxis.Which,
                    Axis = ev.Caxis.Axis,
                    Value = ev.Caxis.Value / 32768f,
                });
        }
        if (changed) Rescan();
    }

    static void CloseControllers()
    {
        if (_pad0 != null) { _sdl?.GameControllerClose(_pad0); _pad0 = null; }
        if (_pad1 != null) { _sdl?.GameControllerClose(_pad1); _pad1 = null; }
    }

    static void Rescan()
    {
        if (_sdl == null) return;
        CloseControllers();
        int n = _sdl.NumJoysticks();
        for (int i = 0; i < n; i++)
        {
            if (_sdl.IsGameController(i) != SdlBool.True) continue;
            var ctrl = _sdl.GameControllerOpen(i);
            if (ctrl == null) continue;
            if (_pad0 == null) _pad0 = ctrl;
            else { _pad1 = ctrl; break; }
        }
    }

    static void PollKeyboard()
    {
        var kb = _keyboard;
        if (kb == null)
        {
            Controller.State = 0xFFFF;
            Controller.State2 = 0xFFFF;
            return;
        }
        Controller.State = KeyState(kb, ConfigManager.Game.Keys);
        Controller.State2 = KeyState(kb, ConfigManager.Game.Keys2);
    }

    static ushort KeyState(IKeyboard kb, KeyBindings cfg)
    {
        ushort s = 0xFFFF;
        void B(string keyName, ushort bit)
        {
            if (string.IsNullOrEmpty(keyName)) return;
            if (!Enum.TryParse<Key>(keyName, out var k)) return;
            if (IsKeyDown(k))
                s &= (ushort)~bit;
        }

        B(cfg.Cross,    Controller.Cross);
        B(cfg.Circle,   Controller.Circle);
        B(cfg.Square,   Controller.Square);
        B(cfg.Triangle, Controller.Triangle);
        B(cfg.L1,       Controller.L1);
        B(cfg.R1,       Controller.R1);
        B(cfg.L2,       Controller.L2);
        B(cfg.R2,       Controller.R2);
        B(cfg.L3,       Controller.L3);
        B(cfg.R3,       Controller.R3);
        B(cfg.Start,    Controller.Start);
        B(cfg.Select,   Controller.Select);
        B(cfg.Up,       Controller.Up);
        B(cfg.Down,     Controller.Down);
        B(cfg.Left,     Controller.Left);
        B(cfg.Right,    Controller.Right);

        return s;
    }

    static bool HasAnyKey(KeyBindings cfg) =>
        cfg.Cross.Length > 0 || cfg.Circle.Length > 0 || cfg.Square.Length > 0 || cfg.Triangle.Length > 0 ||
        cfg.L1.Length > 0 || cfg.R1.Length > 0 || cfg.L2.Length > 0 || cfg.R2.Length > 0 ||
        cfg.L3.Length > 0 || cfg.R3.Length > 0 || cfg.Start.Length > 0 || cfg.Select.Length > 0 ||
        cfg.Up.Length > 0 || cfg.Down.Length > 0 || cfg.Left.Length > 0 || cfg.Right.Length > 0;

    static void PollGamepads()
    {
        if (_sdl == null) return;

        if (_pad0 != null)
        {
            Controller.State = PadState(_pad0, ConfigManager.Game.Pad, Controller.State);
            Controller.LeftX = AxisToByte(_sdl.GameControllerGetAxis(_pad0, GameControllerAxis.Leftx));
            Controller.LeftY = AxisToByte(_sdl.GameControllerGetAxis(_pad0, GameControllerAxis.Lefty));
            Controller.RightX = AxisToByte(_sdl.GameControllerGetAxis(_pad0, GameControllerAxis.Rightx));
            Controller.RightY = AxisToByte(_sdl.GameControllerGetAxis(_pad0, GameControllerAxis.Righty));
        }

        if (_pad1 != null)
        {
            Controller.State2 = PadState(_pad1, ConfigManager.Game.Pad2, Controller.State2);
            Controller.LeftX2 = AxisToByte(_sdl.GameControllerGetAxis(_pad1, GameControllerAxis.Leftx));
            Controller.LeftY2 = AxisToByte(_sdl.GameControllerGetAxis(_pad1, GameControllerAxis.Lefty));
            Controller.RightX2 = AxisToByte(_sdl.GameControllerGetAxis(_pad1, GameControllerAxis.Rightx));
            Controller.RightY2 = AxisToByte(_sdl.GameControllerGetAxis(_pad1, GameControllerAxis.Righty));
        }
        else
        {
            Controller.LeftX2 = Controller.LeftY2 = Controller.RightX2 = Controller.RightY2 = 0x80;
        }
    }

    static ushort PadState(GameController* ctrl, GamepadBindings pad, ushort s)
    {
        s = Apply(ctrl, pad.Cross,    Controller.Cross,    s);
        s = Apply(ctrl, pad.Circle,   Controller.Circle,   s);
        s = Apply(ctrl, pad.Square,   Controller.Square,   s);
        s = Apply(ctrl, pad.Triangle, Controller.Triangle, s);
        s = Apply(ctrl, pad.L1,       Controller.L1,       s);
        s = Apply(ctrl, pad.R1,       Controller.R1,       s);
        s = Apply(ctrl, pad.L2,       Controller.L2,       s);
        s = Apply(ctrl, pad.R2,       Controller.R2,       s);
        s = Apply(ctrl, pad.L3,       Controller.L3,       s);
        s = Apply(ctrl, pad.R3,       Controller.R3,       s);
        s = Apply(ctrl, pad.Start,    Controller.Start,    s);
        s = Apply(ctrl, pad.Select,   Controller.Select,   s);
        s = Apply(ctrl, pad.Up,       Controller.Up,       s);
        s = Apply(ctrl, pad.Down,     Controller.Down,     s);
        s = Apply(ctrl, pad.Left,     Controller.Left,     s);
        s = Apply(ctrl, pad.Right,    Controller.Right,    s);
        return s;
    }

    static ushort Apply(GameController* ctrl, int[] bindings, ushort bit, ushort s)
    {
        foreach (var binding in bindings)
            if (Pressed(ctrl, binding))
                return (ushort)(s & ~bit);
        return s;
    }

    static bool Pressed(GameController* ctrl, int binding)
    {
        if (_sdl == null) return false;
        if (binding == LeftTrigger)
            return _sdl.GameControllerGetAxis(ctrl, GameControllerAxis.Triggerleft) > AxisThreshold;
        if (binding == RightTrigger)
            return _sdl.GameControllerGetAxis(ctrl, GameControllerAxis.Triggerright) > AxisThreshold;
        if (IsStickBinding(binding))
        {
            var (axis, positive) = AxisBinding(binding);
            short v = _sdl.GameControllerGetAxis(ctrl, axis);
            return positive ? v > StickThreshold : v < -StickThreshold;
        }
        return _sdl.GameControllerGetButton(ctrl, (GameControllerButton)binding) != 0;
    }

    static byte AxisToByte(short axis)
    {
        float f = Math.Clamp(axis * 1.3f / 32768.0f, -1.0f, 1.0f);
        return (byte)Math.Clamp((int)MathF.Round((f + 1.0f) * 127.5f), 0, 255);
    }

    public static void SetRumble(byte large, byte small)
    {
        if (_sdl == null || _pad0 == null) return;
        ushort lo = (ushort)(large * 257);
        ushort hi = small != 0 ? (ushort)65535 : (ushort)0;
        uint duration = large == 0 && small == 0 ? 0u : 500u;
        _sdl.GameControllerRumble(_pad0, lo, hi, duration);
    }

    static void OnKeyDown(IKeyboard kb, Key key, int _)
    {
        if (key != Key.Unknown) _keysDown.Add(key);
        if (key == Key.F1)  _topBarToggle = true;
        if (key == Key.F9)  _sessionMarker = true;
        if (key == Key.F11) _fullscreenToggle = true;
        if (key == Key.Escape) _pauseMenuToggle = true;
        if (key == ResolveCheatMenuKey()) _cheatMenuToggle = true;
        // Alt+Enter — common fullscreen shortcut (Enter alone stays Start/Cross).
        if (key is Key.Enter or Key.KeypadEnter
            && (_keysDown.Contains(Key.AltLeft) || _keysDown.Contains(Key.AltRight)))
        {
            _fullscreenToggle = true;
            _keysDown.Remove(key);
        }

        if (EventBus.HasAnyListeners<KeyboardEvent>())
        {
            EventBus.Dispatch(new KeyboardEvent{
                Key = (int)key,
                Pressed = true
            });
        }
    }

    static void OnKeyUp(IKeyboard kb, Key key, int _)
    {
        _keysDown.Remove(key);
        if (EventBus.HasAnyListeners<KeyboardEvent>())
        {
            EventBus.Dispatch(new KeyboardEvent{
                Key = (int)key,
                Pressed = false
            });
        }
    }

    static Key ResolveCheatMenuKey()
    {
        var name = ConfigManager.View.CheatMenuKey;
        if (string.IsNullOrWhiteSpace(name)) return Key.F3;
        return Enum.TryParse<Key>(name.Trim(), ignoreCase: true, out var bound) ? bound : Key.F3;
    }

    /// <summary>Win32 VK for host hotkeys; null if we have no async-key fallback for that Silk key.</summary>
    static int? SilkKeyToVk(Key key) => key switch
    {
        Key.F1 => 0x70, Key.F2 => 0x71, Key.F3 => 0x72, Key.F4 => 0x73,
        Key.F5 => 0x74, Key.F6 => 0x75, Key.F7 => 0x76, Key.F8 => 0x77,
        Key.F9 => 0x78, Key.F10 => 0x79, Key.F11 => 0x7A, Key.F12 => 0x7B,
        Key.A => 0x41, Key.B => 0x42, Key.C => 0x43, Key.D => 0x44,
        Key.E => 0x45, Key.F => 0x46, Key.G => 0x47, Key.H => 0x48,
        Key.I => 0x49, Key.J => 0x4A, Key.K => 0x4B, Key.L => 0x4C,
        Key.M => 0x4D, Key.N => 0x4E, Key.O => 0x4F, Key.P => 0x50,
        Key.Q => 0x51, Key.R => 0x52, Key.S => 0x53, Key.T => 0x54,
        Key.U => 0x55, Key.V => 0x56, Key.W => 0x57, Key.X => 0x58,
        Key.Y => 0x59, Key.Z => 0x5A,
        Key.Number0 => 0x30, Key.Number1 => 0x31, Key.Number2 => 0x32,
        Key.Number3 => 0x33, Key.Number4 => 0x34, Key.Number5 => 0x35,
        Key.Number6 => 0x36, Key.Number7 => 0x37, Key.Number8 => 0x38,
        Key.Number9 => 0x39,
        Key.Space => 0x20, Key.Escape => 0x1B, Key.Tab => 0x09,
        Key.Enter => 0x0D, Key.Backspace => 0x08, Key.Insert => 0x2D,
        Key.Delete => 0x2E, Key.Home => 0x24, Key.End => 0x23,
        Key.PageUp => 0x21, Key.PageDown => 0x22,
        Key.Up => 0x26, Key.Down => 0x28, Key.Left => 0x25, Key.Right => 0x27,
        _ => null,
    };

    static void OnMouseMove(IMouse mouse, Vector2 position)
    {
        if (EventBus.HasAnyListeners<MouseEvent>())
        {
            EventBus.Dispatch(new MouseEvent
            {
                Action = MouseAction.Move,
                X = (int)position.X,
                Y = (int)position.Y
            });
        }
    }

    static void OnMouseDown(IMouse mouse, MouseButton mouseButton)
    {
        if (EventBus.HasAnyListeners<MouseEvent>())
        {
            EventBus.Dispatch(new MouseEvent
            {
                Action = MouseAction.Button,
                Button = MapMouseButton(mouseButton),
                Pressed = true,
                X = (int)mouse.Position.X,
                Y = (int)mouse.Position.Y
            });
        }
    }

    static void OnMouseUp(IMouse mouse, MouseButton mouseButton)
    {
        if (EventBus.HasAnyListeners<MouseEvent>())
        {
            EventBus.Dispatch(new MouseEvent
            {
                Action = MouseAction.Button,
                Button = MapMouseButton(mouseButton),
                Pressed = false,
                X = (int)mouse.Position.X,
                Y = (int)mouse.Position.Y
            });
        }
    }
    
    static void OnScroll(IMouse mouse, ScrollWheel wheel)
    {
        if (EventBus.HasAnyListeners<MouseEvent>())
        {
            EventBus.Dispatch(new MouseEvent
            {
                Action = MouseAction.Wheel,
                Wheel = (int)wheel.Y,
                X = (int)mouse.Position.X,
                Y = (int)mouse.Position.Y
            });
        }
    }
    static EvMouseButton MapMouseButton(MouseButton button) => button switch
    {
        MouseButton.Left => EvMouseButton.Left,
        MouseButton.Right => EvMouseButton.Right,
        MouseButton.Middle => EvMouseButton.Middle,
        _ => EvMouseButton.None
    };

}
