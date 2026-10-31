using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace RecompOne.Runtime.Host.Window;

/// <summary>
/// ImGui controller for an OpenGL ES 3.0+ game window (#167). Silk's desktop
/// controller compiles GLSL 330 and calls glPolygonMode, which an ES context
/// rejects, and its ES variant needs glDrawElementsBaseVertex (ES 3.2 only).
/// Input and frames come from <see cref="ImGuiControllerBase"/>.
/// </summary>
sealed class GlesImGuiController : ImGuiControllerBase
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

    uint _program, _vbo, _ebo, _vao, _fontTexture;
    int _locTexture, _locProjMtx;

    public GlesImGuiController(GL gl, IView view, IInputContext input, Action? onConfigureIO = null)
        : base(view, input)
    {
        _gl = gl;
        Start(onConfigureIO);
    }

    protected override unsafe void CreateDeviceResources()
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

    protected override unsafe void RenderDrawData(ImDrawDataPtr drawData)
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

    protected override void DisposeDeviceResources()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteTexture(_fontTexture);
        _gl.DeleteProgram(_program);
    }
}
