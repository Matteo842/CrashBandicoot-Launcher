using Avalonia.Input;
using RecompOne.Runtime.Config;

namespace CrashBandicoot.Launcher.Linux;

/// <summary>
/// Avalonia key events to the Silk.NET key names stored in settings.json.
/// The game reads keys through GLFW, which names them by physical position,
/// so the physical key is preferred over the layout-dependent one.
/// </summary>
static class LinuxKeyNames
{
    public static string? FromKeyEvent(KeyEventArgs e)
    {
        if (FromPhysical(e.PhysicalKey) is { } name)
            return name;

        // No physical key reported: try the logical key name (Return, D1, LeftShift, …).
        return e.Key != Key.None && KeyBindingNames.TryParse(e.Key.ToString(), out var key)
            ? key.ToString()
            : null;
    }

    static string? FromPhysical(PhysicalKey k)
    {
        switch (k)
        {
            case >= PhysicalKey.A and <= PhysicalKey.Z:
                return k.ToString();
            case >= PhysicalKey.Digit0 and <= PhysicalKey.Digit9:
                return "Number" + (k - PhysicalKey.Digit0);
            case >= PhysicalKey.NumPad0 and <= PhysicalKey.NumPad9:
                return "Keypad" + (k - PhysicalKey.NumPad0);
            case >= PhysicalKey.F1 and <= PhysicalKey.F24:
                return k.ToString();
        }

        return k switch
        {
            PhysicalKey.Backquote => "GraveAccent",
            PhysicalKey.Backslash => "BackSlash",
            PhysicalKey.BracketLeft => "LeftBracket",
            PhysicalKey.BracketRight => "RightBracket",
            PhysicalKey.Comma => "Comma",
            PhysicalKey.Equal => "Equal",
            PhysicalKey.Minus => "Minus",
            PhysicalKey.Period => "Period",
            PhysicalKey.Quote => "Apostrophe",
            PhysicalKey.Semicolon => "Semicolon",
            PhysicalKey.Slash => "Slash",
            PhysicalKey.IntlBackslash => "World2",
            PhysicalKey.AltLeft => "AltLeft",
            PhysicalKey.AltRight => "AltRight",
            PhysicalKey.ControlLeft => "ControlLeft",
            PhysicalKey.ControlRight => "ControlRight",
            PhysicalKey.ShiftLeft => "ShiftLeft",
            PhysicalKey.ShiftRight => "ShiftRight",
            PhysicalKey.MetaLeft => "SuperLeft",
            PhysicalKey.MetaRight => "SuperRight",
            PhysicalKey.ContextMenu => "Menu",
            PhysicalKey.Backspace => "Backspace",
            PhysicalKey.CapsLock => "CapsLock",
            PhysicalKey.Enter => "Enter",
            PhysicalKey.Space => "Space",
            PhysicalKey.Tab => "Tab",
            PhysicalKey.Escape => "Escape",
            PhysicalKey.Delete => "Delete",
            PhysicalKey.End => "End",
            PhysicalKey.Home => "Home",
            PhysicalKey.Insert => "Insert",
            PhysicalKey.PageDown => "PageDown",
            PhysicalKey.PageUp => "PageUp",
            PhysicalKey.ArrowDown => "Down",
            PhysicalKey.ArrowLeft => "Left",
            PhysicalKey.ArrowRight => "Right",
            PhysicalKey.ArrowUp => "Up",
            PhysicalKey.NumLock => "NumLock",
            PhysicalKey.NumPadAdd => "KeypadAdd",
            PhysicalKey.NumPadSubtract => "KeypadSubtract",
            PhysicalKey.NumPadMultiply => "KeypadMultiply",
            PhysicalKey.NumPadDivide => "KeypadDivide",
            PhysicalKey.NumPadDecimal => "KeypadDecimal",
            PhysicalKey.NumPadEnter => "KeypadEnter",
            PhysicalKey.NumPadEqual => "KeypadEqual",
            PhysicalKey.PrintScreen => "PrintScreen",
            PhysicalKey.ScrollLock => "ScrollLock",
            PhysicalKey.Pause => "Pause",
            _ => null,
        };
    }
}
