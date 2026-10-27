using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Input.Extensions;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace RecompOne.Runtime.Host.Window;

/// <summary>
/// ImGui controller for an OpenGL ES 3.0+ game window (#167). Silk's desktop
/// controller compiles GLSL 330 and calls glPolygonMode, which an ES context
/// rejects, and its ES variant needs glDrawElementsBaseVertex (ES 3.2 only).
/// Input handling is ported from Silk.NET's ImGuiController (MIT, .NET
/// Foundation) so the menus behave the same on both paths.
/// </summary>
sealed class GlesImGuiController : IDisposable
{
    const string VertexSource = """
        #version 300 es
        precision highp float;
        layout (location = 0) in vec2 Position;
        layout (location = 1) in vec2 UV;
        layout (location = 2) in vec4 Color;
        uniform mat4 ProjMtx;
        out vec2 Frag_UV;
        out vec4 Frag_Color;
        void main()
        {
            Frag_UV = UV;
            Frag_Color = Color;
            gl_Position = ProjMtx * vec4(Position.xy, 0.0, 1.0);
        }
        """;

    const string FragmentSource = """
        #version 300 es
        precision mediump float;
        in vec2 Frag_UV;
        in vec4 Frag_Color;
        uniform sampler2D Texture;
        layout (location = 0) out vec4 Out_Color;
        void main()
        {
            Out_Color = Frag_Color * texture(Texture, Frag_UV.st);
        }
        """;

    readonly GL _gl;
    readonly IView _view;
    readonly IInputContext _input;
    readonly IKeyboard _keyboard;
    readonly List<char> _pressedChars = [];
    readonly IntPtr _context;
    bool _frameBegun;
    int _windowWidth, _windowHeight;

    uint _program, _vbo, _ebo, _vao, _fontTexture;
    int _locTexture, _locProjMtx;

    public GlesImGuiController(GL gl, IView view, IInputContext input, Action? onConfigureIO = null)
    {
        _gl = gl;
        _view = view;
        _input = input;
        _windowWidth = view.Size.X;
        _windowHeight = view.Size.Y;

        _context = ImGui.CreateContext();
        ImGui.SetCurrentContext(_context);
        ImGui.StyleColorsDark();

        onConfigureIO?.Invoke();
        // Without glDrawElementsBaseVertex the vertex offset goes into the
        // attribute pointers instead (see RenderDrawData).
        ImGui.GetIO().BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

        CreateDeviceResources();
        SetPerFrameData(1f / 60f);

        ImGui.NewFrame();
        _frameBegun = true;
        _keyboard = _input.Keyboards[0];
        _view.Resize += WindowResized;
        _keyboard.KeyDown += OnKeyDown;
        _keyboard.KeyUp += OnKeyUp;
        _keyboard.KeyChar += OnKeyChar;
    }

    public void Update(float deltaSeconds)
    {
        var oldContext = ImGui.GetCurrentContext();
        if (oldContext != _context) ImGui.SetCurrentContext(_context);

        if (_frameBegun) ImGui.Render();
        SetPerFrameData(deltaSeconds);
        UpdateInput();
        _frameBegun = true;
        ImGui.NewFrame();

        if (oldContext != _context) ImGui.SetCurrentContext(oldContext);
    }

    public void Render()
    {
        if (!_frameBegun) return;
        var oldContext = ImGui.GetCurrentContext();
        if (oldContext != _context) ImGui.SetCurrentContext(_context);

        _frameBegun = false;
        ImGui.Render();
        RenderDrawData(ImGui.GetDrawData());

        if (oldContext != _context) ImGui.SetCurrentContext(oldContext);
    }

    void SetPerFrameData(float deltaSeconds)
    {
        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(_windowWidth, _windowHeight);
        // Float division: at a fractional Wayland scale (175%) int division gave 1 and
        // ImGui drew into the bottom-left corner of the framebuffer (#187).
        if (_windowWidth > 0 && _windowHeight > 0)
            io.DisplayFramebufferScale = new Vector2((float)_view.FramebufferSize.X / _windowWidth,
                (float)_view.FramebufferSize.Y / _windowHeight);
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

    unsafe void CreateDeviceResources()
    {
        _program = BuildProgram();
        _locTexture = _gl.GetUniformLocation(_program, "Texture");
        _locProjMtx = _gl.GetUniformLocation(_program, "ProjMtx");
        _vbo = _gl.GenBuffer();
        _ebo = _gl.GenBuffer();
        _vao = _gl.GenVertexArray();

        var io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out _);
        _gl.GetInteger(GLEnum.TextureBinding2D, out int lastTexture);
        _fontTexture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _fontTexture);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, (void*)pixels);
        io.Fonts.SetTexID((IntPtr)_fontTexture);
        _gl.BindTexture(TextureTarget.Texture2D, (uint)lastTexture);
    }

    uint BuildProgram()
    {
        uint vs = Compile(ShaderType.VertexShader, VertexSource);
        uint fs = Compile(ShaderType.FragmentShader, FragmentSource);
        uint program = _gl.CreateProgram();
        _gl.AttachShader(program, vs);
        _gl.AttachShader(program, fs);
        _gl.LinkProgram(program);
        _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int ok);
        if (ok == 0)
            Console.Error.WriteLine($"[ImGui] GLES link failed: {_gl.GetProgramInfoLog(program)}");
        _gl.DetachShader(program, vs);
        _gl.DetachShader(program, fs);
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
        return program;
    }

    uint Compile(ShaderType type, string source)
    {
        uint shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);
        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0)
            Console.Error.WriteLine($"[ImGui] GLES {type} compile failed: {_gl.GetShaderInfoLog(shader)}");
        return shader;
    }

    unsafe void BindVertexLayout(nuint vertexOffset)
    {
        uint stride = (uint)sizeof(ImDrawVert);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)vertexOffset);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(vertexOffset + 8));
        _gl.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, stride, (void*)(vertexOffset + 16));
    }

    unsafe void RenderDrawData(ImDrawDataPtr drawData)
    {
        int framebufferWidth = (int)(drawData.DisplaySize.X * drawData.FramebufferScale.X);
        int framebufferHeight = (int)(drawData.DisplaySize.Y * drawData.FramebufferScale.Y);
        if (framebufferWidth <= 0 || framebufferHeight <= 0) return;

        // Back up the state the game renderer relies on.
        _gl.GetInteger(GLEnum.ActiveTexture, out int lastActiveTexture);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.GetInteger(GLEnum.CurrentProgram, out int lastProgram);
        _gl.GetInteger(GLEnum.TextureBinding2D, out int lastTexture);
        _gl.GetInteger(GLEnum.SamplerBinding, out int lastSampler);
        _gl.GetInteger(GLEnum.ArrayBufferBinding, out int lastArrayBuffer);
        _gl.GetInteger(GLEnum.VertexArrayBinding, out int lastVertexArray);
        Span<int> lastScissorBox = stackalloc int[4];
        _gl.GetInteger(GLEnum.ScissorBox, lastScissorBox);
        _gl.GetInteger(GLEnum.BlendSrcRgb, out int lastBlendSrcRgb);
        _gl.GetInteger(GLEnum.BlendDstRgb, out int lastBlendDstRgb);
        _gl.GetInteger(GLEnum.BlendSrcAlpha, out int lastBlendSrcAlpha);
        _gl.GetInteger(GLEnum.BlendDstAlpha, out int lastBlendDstAlpha);
        _gl.GetInteger(GLEnum.BlendEquationRgb, out int lastBlendEquationRgb);
        _gl.GetInteger(GLEnum.BlendEquationAlpha, out int lastBlendEquationAlpha);
        bool lastBlend = _gl.IsEnabled(GLEnum.Blend);
        bool lastCullFace = _gl.IsEnabled(GLEnum.CullFace);
        bool lastDepthTest = _gl.IsEnabled(GLEnum.DepthTest);
        bool lastStencilTest = _gl.IsEnabled(GLEnum.StencilTest);
        bool lastScissorTest = _gl.IsEnabled(GLEnum.ScissorTest);

        _gl.Enable(GLEnum.Blend);
        _gl.BlendEquation(GLEnum.FuncAdd);
        _gl.BlendFuncSeparate(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha, GLEnum.One, GLEnum.OneMinusSrcAlpha);
        _gl.Disable(GLEnum.CullFace);
        _gl.Disable(GLEnum.DepthTest);
        _gl.Disable(GLEnum.StencilTest);
        _gl.Enable(GLEnum.ScissorTest);

        float l = drawData.DisplayPos.X;
        float r = drawData.DisplayPos.X + drawData.DisplaySize.X;
        float t = drawData.DisplayPos.Y;
        float b = drawData.DisplayPos.Y + drawData.DisplaySize.Y;
        Span<float> projection = stackalloc float[]
        {
            2.0f / (r - l), 0.0f, 0.0f, 0.0f,
            0.0f, 2.0f / (t - b), 0.0f, 0.0f,
            0.0f, 0.0f, -1.0f, 0.0f,
            (r + l) / (l - r), (t + b) / (b - t), 0.0f, 1.0f,
        };
        _gl.UseProgram(_program);
        _gl.Uniform1(_locTexture, 0);
        _gl.UniformMatrix4(_locProjMtx, 1, false, projection);
        _gl.BindSampler(0, 0);

        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(GLEnum.ArrayBuffer, _vbo);
        _gl.BindBuffer(GLEnum.ElementArrayBuffer, _ebo);
        _gl.EnableVertexAttribArray(0);
        _gl.EnableVertexAttribArray(1);
        _gl.EnableVertexAttribArray(2);

        Vector2 clipOff = drawData.DisplayPos;
        Vector2 clipScale = drawData.FramebufferScale;
        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            ImDrawListPtr cmdList = drawData.CmdLists[n];
            _gl.BufferData(GLEnum.ArrayBuffer, (nuint)(cmdList.VtxBuffer.Size * sizeof(ImDrawVert)),
                (void*)cmdList.VtxBuffer.Data, GLEnum.StreamDraw);
            _gl.BufferData(GLEnum.ElementArrayBuffer, (nuint)(cmdList.IdxBuffer.Size * sizeof(ushort)),
                (void*)cmdList.IdxBuffer.Data, GLEnum.StreamDraw);

            uint boundVtxOffset = uint.MaxValue;
            for (int i = 0; i < cmdList.CmdBuffer.Size; i++)
            {
                ImDrawCmdPtr cmd = cmdList.CmdBuffer[i];
                if (cmd.UserCallback != IntPtr.Zero) continue;

                Vector4 clip;
                clip.X = (cmd.ClipRect.X - clipOff.X) * clipScale.X;
                clip.Y = (cmd.ClipRect.Y - clipOff.Y) * clipScale.Y;
                clip.Z = (cmd.ClipRect.Z - clipOff.X) * clipScale.X;
                clip.W = (cmd.ClipRect.W - clipOff.Y) * clipScale.Y;
                if (clip.X >= framebufferWidth || clip.Y >= framebufferHeight || clip.Z < 0.0f || clip.W < 0.0f)
                    continue;

                if (cmd.VtxOffset != boundVtxOffset)
                {
                    BindVertexLayout((nuint)(cmd.VtxOffset * (uint)sizeof(ImDrawVert)));
                    boundVtxOffset = cmd.VtxOffset;
                }
                _gl.Scissor((int)clip.X, (int)(framebufferHeight - clip.W),
                    (uint)(clip.Z - clip.X), (uint)(clip.W - clip.Y));
                _gl.BindTexture(GLEnum.Texture2D, (uint)cmd.TextureId);
                _gl.DrawElements(GLEnum.Triangles, cmd.ElemCount, GLEnum.UnsignedShort,
                    (void*)(cmd.IdxOffset * sizeof(ushort)));
            }
        }

        _gl.UseProgram((uint)lastProgram);
        _gl.BindTexture(GLEnum.Texture2D, (uint)lastTexture);
        _gl.BindSampler(0, (uint)lastSampler);
        _gl.ActiveTexture((GLEnum)lastActiveTexture);
        _gl.BindVertexArray((uint)lastVertexArray);
        _gl.BindBuffer(GLEnum.ArrayBuffer, (uint)lastArrayBuffer);
        _gl.BlendEquationSeparate((GLEnum)lastBlendEquationRgb, (GLEnum)lastBlendEquationAlpha);
        _gl.BlendFuncSeparate((GLEnum)lastBlendSrcRgb, (GLEnum)lastBlendDstRgb,
            (GLEnum)lastBlendSrcAlpha, (GLEnum)lastBlendDstAlpha);
        SetEnabled(GLEnum.Blend, lastBlend);
        SetEnabled(GLEnum.CullFace, lastCullFace);
        SetEnabled(GLEnum.DepthTest, lastDepthTest);
        SetEnabled(GLEnum.StencilTest, lastStencilTest);
        SetEnabled(GLEnum.ScissorTest, lastScissorTest);
        _gl.Scissor(lastScissorBox[0], lastScissorBox[1], (uint)lastScissorBox[2], (uint)lastScissorBox[3]);
    }

    void SetEnabled(GLEnum cap, bool on)
    {
        if (on) _gl.Enable(cap);
        else _gl.Disable(cap);
    }

    public void Dispose()
    {
        _view.Resize -= WindowResized;
        _keyboard.KeyDown -= OnKeyDown;
        _keyboard.KeyUp -= OnKeyUp;
        _keyboard.KeyChar -= OnKeyChar;
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteTexture(_fontTexture);
        _gl.DeleteProgram(_program);
        ImGui.DestroyContext(_context);
    }
}
