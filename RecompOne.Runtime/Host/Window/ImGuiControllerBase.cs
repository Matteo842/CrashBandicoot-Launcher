using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Input.Extensions;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace RecompOne.Runtime.Host.Window;

/// <summary>
/// ImGui context, frame and input for our own renderers (OpenGL ES, Vulkan).
/// Input handling is ported from Silk.NET's ImGuiController (MIT, .NET
/// Foundation) so the menus behave the same on every path. Derived classes
/// upload the font atlas and draw the frame.
/// </summary>
abstract class ImGuiControllerBase : IDisposable
{
    protected readonly IView View;
    readonly IInputContext _input;
    IKeyboard _keyboard = null!;
    readonly List<char> _pressedChars = [];
    protected IntPtr Context { get; private set; }
    bool _frameBegun;
    int _windowWidth, _windowHeight;

    protected ImGuiControllerBase(IView view, IInputContext input)
    {
        View = view;
        _input = input;
        _windowWidth = view.Size.X;
        _windowHeight = view.Size.Y;
    }

    /// <summary>Create the context and begin the first frame; call at the end of the derived constructor.</summary>
    protected void Start(Action? onConfigureIO)
    {
        Context = ImGui.CreateContext();
        ImGui.SetCurrentContext(Context);
        ImGui.StyleColorsDark();

        onConfigureIO?.Invoke();
        // Both renderers honour ImDrawCmd.VtxOffset (GLES: in the attribute
        // pointers, as it has no glDrawElementsBaseVertex).
        ImGui.GetIO().BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

        CreateDeviceResources();
        SetPerFrameData(1f / 60f);

        ImGui.NewFrame();
        _frameBegun = true;
        _keyboard = _input.Keyboards[0];
        View.Resize += WindowResized;
        _keyboard.KeyDown += OnKeyDown;
        _keyboard.KeyUp += OnKeyUp;
        _keyboard.KeyChar += OnKeyChar;
    }

    protected abstract void CreateDeviceResources();
    protected abstract void RenderDrawData(ImDrawDataPtr drawData);
    protected abstract void DisposeDeviceResources();

    public void Update(float deltaSeconds)
    {
        var oldContext = ImGui.GetCurrentContext();
        if (oldContext != Context) ImGui.SetCurrentContext(Context);

        if (_frameBegun) ImGui.Render();
        SetPerFrameData(deltaSeconds);
        UpdateInput();
        _frameBegun = true;
        ImGui.NewFrame();

        if (oldContext != Context) ImGui.SetCurrentContext(oldContext);
    }

    public void Render()
    {
        if (!_frameBegun) return;
        var oldContext = ImGui.GetCurrentContext();
        if (oldContext != Context) ImGui.SetCurrentContext(Context);

        _frameBegun = false;
        ImGui.Render();
        RenderDrawData(ImGui.GetDrawData());

        if (oldContext != Context) ImGui.SetCurrentContext(oldContext);
    }

    void SetPerFrameData(float deltaSeconds)
    {
        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(_windowWidth, _windowHeight);
        // Float division: at a fractional Wayland scale (175%) int division gave 1 and
        // ImGui drew into the bottom-left corner of the framebuffer (#187).
        if (_windowWidth > 0 && _windowHeight > 0)
            io.DisplayFramebufferScale = new Vector2((float)View.FramebufferSize.X / _windowWidth,
                (float)View.FramebufferSize.Y / _windowHeight);
        io.DeltaTime = deltaSeconds;
    }

    void UpdateInput()
    {
        var io = ImGui.GetIO();
        using var mouse = _input.Mice[0].CaptureState();
        io.MouseDown[0] = mouse.IsButtonPressed(MouseButton.Left);
        io.MouseDown[1] = mouse.IsButtonPressed(MouseButton.Right);
        io.MouseDown[2] = mouse.IsButtonPressed(MouseButton.Middle);
        io.MousePos = new Vector2((int)mouse.Position.X, (int)mouse.Position.Y);
        var wheel = mouse.GetScrollWheels()[0];
        io.MouseWheel = wheel.Y;
        io.MouseWheelH = wheel.X;

        foreach (var c in _pressedChars)
            io.AddInputCharacter(c);
        _pressedChars.Clear();

        io.KeyCtrl = _keyboard.IsKeyPressed(Key.ControlLeft) || _keyboard.IsKeyPressed(Key.ControlRight);
        io.KeyAlt = _keyboard.IsKeyPressed(Key.AltLeft) || _keyboard.IsKeyPressed(Key.AltRight);
        io.KeyShift = _keyboard.IsKeyPressed(Key.ShiftLeft) || _keyboard.IsKeyPressed(Key.ShiftRight);
        io.KeySuper = _keyboard.IsKeyPressed(Key.SuperLeft) || _keyboard.IsKeyPressed(Key.SuperRight);
    }

    void WindowResized(Vector2D<int> size)
    {
        _windowWidth = size.X;
        _windowHeight = size.Y;
    }

    void OnKeyChar(IKeyboard keyboard, char c) => _pressedChars.Add(c);

    static void OnKeyDown(IKeyboard keyboard, Key key, int scancode) => OnKeyEvent(key, scancode, true);

    static void OnKeyUp(IKeyboard keyboard, Key key, int scancode) => OnKeyEvent(key, scancode, false);

    static void OnKeyEvent(Key key, int scancode, bool down)
    {
        var imGuiKey = TranslateKey(key);
        if (imGuiKey == ImGuiKey.None) return;
        var io = ImGui.GetIO();
        io.AddKeyEvent(imGuiKey, down);
        io.SetKeyEventNativeData(imGuiKey, (int)key, scancode);
    }

    /// <summary>Same table as Silk's controller; unknown keys map to None instead of throwing.</summary>
    static ImGuiKey TranslateKey(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return ImGuiKey.A + (key - Key.A);
        if (key is >= Key.Number0 and <= Key.Number9) return ImGuiKey._0 + (key - Key.Number0);
        if (key is >= Key.Keypad0 and <= Key.Keypad9) return ImGuiKey.Keypad0 + (key - Key.Keypad0);
        if (key is >= Key.F1 and <= Key.F24) return ImGuiKey.F1 + (key - Key.F1);
        return key switch
        {
            Key.Tab => ImGuiKey.Tab,
            Key.Left => ImGuiKey.LeftArrow,
            Key.Right => ImGuiKey.RightArrow,
            Key.Up => ImGuiKey.UpArrow,
            Key.Down => ImGuiKey.DownArrow,
            Key.PageUp => ImGuiKey.PageUp,
            Key.PageDown => ImGuiKey.PageDown,
            Key.Home => ImGuiKey.Home,
            Key.End => ImGuiKey.End,
            Key.Insert => ImGuiKey.Insert,
            Key.Delete => ImGuiKey.Delete,
            Key.Backspace => ImGuiKey.Backspace,
            Key.Space => ImGuiKey.Space,
            Key.Enter => ImGuiKey.Enter,
            Key.Escape => ImGuiKey.Escape,
            Key.Apostrophe => ImGuiKey.Apostrophe,
            Key.Comma => ImGuiKey.Comma,
            Key.Minus => ImGuiKey.Minus,
            Key.Period => ImGuiKey.Period,
            Key.Slash => ImGuiKey.Slash,
            Key.Semicolon => ImGuiKey.Semicolon,
            Key.Equal => ImGuiKey.Equal,
            Key.LeftBracket => ImGuiKey.LeftBracket,
            Key.BackSlash => ImGuiKey.Backslash,
            Key.RightBracket => ImGuiKey.RightBracket,
            Key.GraveAccent => ImGuiKey.GraveAccent,
            Key.CapsLock => ImGuiKey.CapsLock,
            Key.ScrollLock => ImGuiKey.ScrollLock,
            Key.NumLock => ImGuiKey.NumLock,
            Key.PrintScreen => ImGuiKey.PrintScreen,
            Key.Pause => ImGuiKey.Pause,
            Key.KeypadDecimal => ImGuiKey.KeypadDecimal,
            Key.KeypadDivide => ImGuiKey.KeypadDivide,
            Key.KeypadMultiply => ImGuiKey.KeypadMultiply,
            Key.KeypadSubtract => ImGuiKey.KeypadSubtract,
            Key.KeypadAdd => ImGuiKey.KeypadAdd,
            Key.KeypadEnter => ImGuiKey.KeypadEnter,
            Key.KeypadEqual => ImGuiKey.KeypadEqual,
            Key.ShiftLeft => ImGuiKey.LeftShift,
            Key.ControlLeft => ImGuiKey.LeftCtrl,
            Key.AltLeft => ImGuiKey.LeftAlt,
            Key.SuperLeft => ImGuiKey.LeftSuper,
            Key.ShiftRight => ImGuiKey.RightShift,
            Key.ControlRight => ImGuiKey.RightCtrl,
            Key.AltRight => ImGuiKey.RightAlt,
            Key.SuperRight => ImGuiKey.RightSuper,
            Key.Menu => ImGuiKey.Menu,
            _ => ImGuiKey.None,
        };
    }

    public void Dispose()
    {
        View.Resize -= WindowResized;
        _keyboard.KeyDown -= OnKeyDown;
        _keyboard.KeyUp -= OnKeyUp;
        _keyboard.KeyChar -= OnKeyChar;
        DisposeDeviceResources();
        ImGui.DestroyContext(Context);
    }
}
