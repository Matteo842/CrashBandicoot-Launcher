using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Silk.NET.Input;

namespace RecompOne.Runtime.Config;

/// <summary>
/// Parses and canonicalizes keyboard binding names stored in settings.json.
/// Names are Silk.NET <see cref="Key"/> identifiers (e.g. Z, Enter, ShiftRight),
/// but launcher text boxes historically saved lowercase letters and WinForms aliases.
/// </summary>
public static class KeyBindingNames
{
    static readonly Dictionary<int, Key> VkToKey = BuildVkToKey();
    static readonly Dictionary<string, Key> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["shift"] = Key.ShiftLeft,
        ["shiftkey"] = Key.ShiftLeft,
        ["leftshift"] = Key.ShiftLeft,
        ["rightshift"] = Key.ShiftRight,
        ["lshift"] = Key.ShiftLeft,
        ["rshift"] = Key.ShiftRight,
        ["control"] = Key.ControlLeft,
        ["ctrl"] = Key.ControlLeft,
        ["controlkey"] = Key.ControlLeft,
        ["leftcontrol"] = Key.ControlLeft,
        ["rightcontrol"] = Key.ControlRight,
        ["lcontrol"] = Key.ControlLeft,
        ["rcontrol"] = Key.ControlRight,
        ["lctrl"] = Key.ControlLeft,
        ["rctrl"] = Key.ControlRight,
        ["alt"] = Key.AltLeft,
        ["altkey"] = Key.AltLeft,
        ["leftalt"] = Key.AltLeft,
        ["rightalt"] = Key.AltRight,
        ["lalt"] = Key.AltLeft,
        ["ralt"] = Key.AltRight,
        ["return"] = Key.Enter,
        ["back"] = Key.Backspace,
        ["bksp"] = Key.Backspace,
        ["esc"] = Key.Escape,
        ["spacebar"] = Key.Space,
        ["pgup"] = Key.PageUp,
        ["pgdn"] = Key.PageDown,
        ["pagedown"] = Key.PageDown,
        ["pageup"] = Key.PageUp,
        ["ins"] = Key.Insert,
        ["del"] = Key.Delete,
        ["win"] = Key.SuperLeft,
        ["windows"] = Key.SuperLeft,
        ["leftwindows"] = Key.SuperLeft,
        ["rightwindows"] = Key.SuperRight,
        ["lwin"] = Key.SuperLeft,
        ["rwin"] = Key.SuperRight,
        ["oemminus"] = Key.Minus,
        ["oemcomma"] = Key.Comma,
        ["oemperiod"] = Key.Period,
        ["oemquestion"] = Key.Slash,
        ["oem1"] = Key.Semicolon,
        ["oemsemicolon"] = Key.Semicolon,
        ["oemplus"] = Key.Equal,
        ["oemopenbrackets"] = Key.LeftBracket,
        ["oempipe"] = Key.BackSlash,
        ["oemclosebrackets"] = Key.RightBracket,
        ["oemtilde"] = Key.GraveAccent,
        ["oemquotes"] = Key.Apostrophe,
        ["oem5"] = Key.BackSlash,
        ["multiply"] = Key.KeypadMultiply,
        ["add"] = Key.KeypadAdd,
        ["subtract"] = Key.KeypadSubtract,
        ["decimal"] = Key.KeypadDecimal,
        ["divide"] = Key.KeypadDivide,
        ["numpadenter"] = Key.KeypadEnter,
        ["0"] = Key.Number0,
        ["1"] = Key.Number1,
        ["2"] = Key.Number2,
        ["3"] = Key.Number3,
        ["4"] = Key.Number4,
        ["5"] = Key.Number5,
        ["6"] = Key.Number6,
        ["7"] = Key.Number7,
        ["8"] = Key.Number8,
        ["9"] = Key.Number9,
        ["d0"] = Key.Number0,
        ["d1"] = Key.Number1,
        ["d2"] = Key.Number2,
        ["d3"] = Key.Number3,
        ["d4"] = Key.Number4,
        ["d5"] = Key.Number5,
        ["d6"] = Key.Number6,
        ["d7"] = Key.Number7,
        ["d8"] = Key.Number8,
        ["d9"] = Key.Number9,
        ["numpad0"] = Key.Keypad0,
        ["numpad1"] = Key.Keypad1,
        ["numpad2"] = Key.Keypad2,
        ["numpad3"] = Key.Keypad3,
        ["numpad4"] = Key.Keypad4,
        ["numpad5"] = Key.Keypad5,
        ["numpad6"] = Key.Keypad6,
        ["numpad7"] = Key.Keypad7,
        ["numpad8"] = Key.Keypad8,
        ["numpad9"] = Key.Keypad9,
        ["scroll"] = Key.ScrollLock,
        ["capslock"] = Key.CapsLock,
        ["apps"] = Key.Menu,
    };

    public static bool TryParse(string? name, out Key key)
    {
        key = Key.Unknown;
        if (string.IsNullOrWhiteSpace(name)) return false;

        var raw = name.Trim();
        if (Enum.TryParse(raw, ignoreCase: true, out key) && key != Key.Unknown)
            return true;

        var compact = Compact(raw);
        if (compact.Length == 0) return false;
        if (Aliases.TryGetValue(compact, out key))
            return true;
        return Enum.TryParse(compact, ignoreCase: true, out key) && key != Key.Unknown;
    }

    /// <summary>Silk identifier to persist, or the trimmed original when unrecognized.</summary>
    public static string Canonical(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var trimmed = name.Trim();
        var compact = Compact(trimmed);
        if (IsGeneric(compact, "shift", "shiftkey")) return "Shift";
        if (IsGeneric(compact, "control", "ctrl", "controlkey")) return "Control";
        if (IsGeneric(compact, "alt", "altkey")) return "Alt";
        return TryParse(trimmed, out var key) ? key.ToString() : trimmed;
    }

    public static bool AnyPressed(string? name, Func<Key, bool> isDown)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var compact = Compact(name);
        if (IsGeneric(compact, "shift", "shiftkey") &&
            (isDown(Key.ShiftLeft) || isDown(Key.ShiftRight)))
            return true;
        if (IsGeneric(compact, "control", "ctrl", "controlkey") &&
            (isDown(Key.ControlLeft) || isDown(Key.ControlRight)))
            return true;
        if (IsGeneric(compact, "alt", "altkey") &&
            (isDown(Key.AltLeft) || isDown(Key.AltRight)))
            return true;
        return TryParse(name, out var key) && isDown(key);
    }

    /// <summary>Win32 virtual-key code for a Silk key, or null when no stable mapping exists.</summary>
    public static int? ToVirtualKey(Key key) => key switch
    {
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
        Key.Apostrophe => 0xDE, Key.Comma => 0xBC, Key.Minus => 0xBD,
        Key.Period => 0xBE, Key.Slash => 0xBF, Key.Semicolon => 0xBA,
        Key.Equal => 0xBB, Key.LeftBracket => 0xDB, Key.BackSlash => 0xDC,
        Key.RightBracket => 0xDD, Key.GraveAccent => 0xC0,
        Key.Space => 0x20, Key.Escape => 0x1B, Key.Tab => 0x09,
        Key.Enter => 0x0D, Key.Backspace => 0x08, Key.Insert => 0x2D,
        Key.Delete => 0x2E, Key.Home => 0x24, Key.End => 0x23,
        Key.PageUp => 0x21, Key.PageDown => 0x22,
        Key.Up => 0x26, Key.Down => 0x28, Key.Left => 0x25, Key.Right => 0x27,
        Key.CapsLock => 0x14, Key.ScrollLock => 0x91, Key.NumLock => 0x90,
        Key.PrintScreen => 0x2C, Key.Pause => 0x13,
        Key.F1 => 0x70, Key.F2 => 0x71, Key.F3 => 0x72, Key.F4 => 0x73,
        Key.F5 => 0x74, Key.F6 => 0x75, Key.F7 => 0x76, Key.F8 => 0x77,
        Key.F9 => 0x78, Key.F10 => 0x79, Key.F11 => 0x7A, Key.F12 => 0x7B,
        Key.F13 => 0x7C, Key.F14 => 0x7D, Key.F15 => 0x7E, Key.F16 => 0x7F,
        Key.F17 => 0x80, Key.F18 => 0x81, Key.F19 => 0x82, Key.F20 => 0x83,
        Key.F21 => 0x84, Key.F22 => 0x85, Key.F23 => 0x86, Key.F24 => 0x87,
        Key.Keypad0 => 0x60, Key.Keypad1 => 0x61, Key.Keypad2 => 0x62,
        Key.Keypad3 => 0x63, Key.Keypad4 => 0x64, Key.Keypad5 => 0x65,
        Key.Keypad6 => 0x66, Key.Keypad7 => 0x67, Key.Keypad8 => 0x68,
        Key.Keypad9 => 0x69, Key.KeypadDecimal => 0x6E, Key.KeypadDivide => 0x6F,
        Key.KeypadMultiply => 0x6A, Key.KeypadSubtract => 0x6D,
        Key.KeypadAdd => 0x6B, Key.KeypadEnter => 0x0D, Key.KeypadEqual => 0x92,
        Key.ShiftLeft => 0xA0, Key.ShiftRight => 0xA1,
        Key.ControlLeft => 0xA2, Key.ControlRight => 0xA3,
        Key.AltLeft => 0xA4, Key.AltRight => 0xA5,
        Key.SuperLeft => 0x5B, Key.SuperRight => 0x5C, Key.Menu => 0x5D,
        _ => null,
    };

    /// <summary>
    /// Silk key name for a Win32 / WinForms virtual-key code.
    /// Distinguishes left/right modifiers via GetAsyncKeyState when the generic VK is reported.
    /// </summary>
    public static string? NameFromVirtualKey(int vk)
    {
        if (vk is 0 or 0xE5) return null;
        var right = false;
        if (OperatingSystem.IsWindows())
            right = IsRightModifier(vk);
        var key = vk switch
        {
            0xA0 => Key.ShiftLeft,
            0xA1 => Key.ShiftRight,
            0x10 => right ? Key.ShiftRight : Key.ShiftLeft,
            0xA2 => Key.ControlLeft,
            0xA3 => Key.ControlRight,
            0x11 => right ? Key.ControlRight : Key.ControlLeft,
            0xA4 => Key.AltLeft,
            0xA5 => Key.AltRight,
            0x12 => right ? Key.AltRight : Key.AltLeft,
            _ => VkToKey.TryGetValue(vk, out var mapped) ? mapped : (Key?)null,
        };
        return key is null or Key.Unknown ? null : key.Value.ToString();
    }

    [SupportedOSPlatform("windows")]
    static bool IsRightModifier(int vk) => vk switch
    {
        0x10 => Down(0xA1) && !Down(0xA0),
        0x11 => Down(0xA3) && !Down(0xA2),
        0x12 => Down(0xA5) && !Down(0xA4),
        0xA1 or 0xA3 or 0xA5 => true,
        _ => false,
    };

    [SupportedOSPlatform("windows")]
    static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    static Dictionary<int, Key> BuildVkToKey()
    {
        var map = new Dictionary<int, Key>();
        foreach (var key in Enum.GetValues<Key>())
        {
            if (key == Key.Unknown) continue;
            if (ToVirtualKey(key) is int vk)
                map.TryAdd(vk, key);
        }
        return map;
    }

    static string Compact(string name)
    {
        Span<char> buf = stackalloc char[name.Length];
        var n = 0;
        foreach (var c in name)
        {
            if (c is ' ' or '-' or '_' ) continue;
            buf[n++] = c;
        }
        return n == 0 ? "" : new string(buf[..n]);
    }

    static bool IsGeneric(string compact, params string[] names)
    {
        foreach (var name in names)
        {
            if (compact.Equals(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
