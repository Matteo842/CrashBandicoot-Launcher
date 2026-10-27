// OpenGL side of the editor: PS1 texture pages sampled in the shader (4/8/15-bit,
// CLUT, transparent texel 0, semi-transparency modes), an offscreen target with a
// pick-id and a world-position attachment, and per-mesh vertex ranges.
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace WideEditor;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct GpuVertex
{
    public Vector3 Pos;
    public Vector2 Uv;
    public Vector3 Rgb;
    public uint Info, Clut, Id, Spare;
}

static class Bits
{
    public const uint Textured = 1u << 8, Semi = 1u << 9, Backdrop = 1u << 16, Hidden = 1u << 19, Behind = 1u << 20;
    public const int KindShift = 17;

    public static (uint Info, uint Clut) Of(Material m, int kind, bool hidden, bool behind, float rank, bool backdrop)
    {
        uint info = (uint)Math.Max(m.Layer, 0) & 255;
        if (m.Textured && m.Layer >= 0) info |= Textured;
        if (m.Semi) info |= Semi;
        info |= (uint)(m.Abr & 3) << 10;
        info |= (uint)(m.Segment & 3) << 12;
        info |= (uint)(m.ColorMode & 3) << 14;
        if (backdrop) info |= Backdrop;
        info |= (uint)(kind & 3) << KindShift;
        if (hidden) info |= Hidden;
        if (behind) info |= Behind;
        uint clut = (uint)(m.ClutX & 15) | ((uint)(m.ClutY & 127) << 8)
            | ((uint)Math.Clamp((int)MathF.Round(rank), 0, 255) << 16);
        return (info, clut);
    }
}

sealed class VertexBuffer : IDisposable
{
    readonly GL _gl;
    readonly uint _vao, _vbo;
    public int Count { get; private set; }

    public unsafe VertexBuffer(GL gl)
    {
        _gl = gl;
        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        uint stride = (uint)sizeof(GpuVertex);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)12);
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, stride, (void*)20);
        gl.EnableVertexAttribArray(3);
        gl.VertexAttribIPointer(3, 4, VertexAttribIType.UnsignedInt, stride, (void*)32);
        gl.BindVertexArray(0);
    }

    public void Upload(List<GpuVertex> data)
    {
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        var span = CollectionsMarshal.AsSpan(data);
        _gl.BufferData<GpuVertex>(BufferTargetARB.ArrayBuffer, span, BufferUsageARB.DynamicDraw);
        Count = data.Count;
    }

    public void Draw(int first, int count)
    {
        if (count <= 0 || first < 0 || first + count > Count) return;
        _gl.BindVertexArray(_vao);
        _gl.DrawArrays(PrimitiveType.Triangles, first, (uint)count);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
    }
}

sealed class Gfx : IDisposable
{
    const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec3 aPos;
        layout(location = 1) in vec2 aUv;
        layout(location = 2) in vec3 aRgb;
        layout(location = 3) in uvec4 aInfo;
        uniform mat4 uView;
        uniform mat4 uProj;
        uniform vec3 uCam;
        out vec2 vUv;
        out vec3 vRgb;
        out vec3 vWorld;
        flat out uvec4 vInfo;
        void main()
        {
            vec3 p = aPos;
            if ((aInfo.x & 65536u) != 0u) p += uCam;
            vec4 vp = uView * vec4(p, 1.0);
            vec4 clip = uProj * vp;
            float rank = float((aInfo.y >> 16) & 255u);
            if (rank > 0.0)
            {
                vec4 back = uProj * vec4(vp.xy, vp.z - rank * 256.0, 1.0);
                clip.z = back.z / back.w * clip.w;
            }
            gl_Position = clip;
            vUv = aUv;
            vRgb = aRgb;
            vWorld = p;
            vInfo = aInfo;
        }
        """;

    const string FragmentSource = """
        #version 330 core
        in vec2 vUv;
        in vec3 vRgb;
        in vec3 vWorld;
        flat in uvec4 vInfo;
        uniform usampler2DArray uPages;
        uniform int uPass;
        uniform int uAbr;
        uniform int uDropHidden;
        uniform int uTint;
        uniform int uBehind;
        uniform int uWire;
        uniform int uGhost;
        layout(location = 0) out vec4 oColor;
        layout(location = 1) out uint oId;
        layout(location = 2) out vec4 oPos;
        uint fetch(int x, int y, int layer) { return texelFetch(uPages, ivec3(x & 255, y & 127, layer), 0).r; }
        void main()
        {
            uint info = vInfo.x;
            bool hidden = (info & 524288u) != 0u;
            if (hidden && uDropHidden != 0) discard;
            bool behind = (info & 1048576u) != 0u;
            if (uBehind == 0 && behind) discard;
            if (uBehind == 1 && !behind) discard;
            bool textured = (info & 256u) != 0u;
            bool semi = (info & 512u) != 0u;
            int abr = int((info >> 10) & 3u);
            vec3 c;
            bool stp = false;
            if (textured)
            {
                int layer = int(info & 255u);
                int seg = int((info >> 12) & 3u) * 64;
                int cm = int((info >> 14) & 3u);
                int u = int(floor(vUv.x)) & 255;
                int v = int(floor(vUv.y)) & 127;
                int cx = int(vInfo.y & 15u) * 16;
                int cy = int((vInfo.y >> 8) & 127u);
                uint t;
                if (cm == 0)
                {
                    uint w = fetch(seg + u / 4, v, layer);
                    t = fetch(cx + int((w >> uint((u & 3) * 4)) & 15u), cy, layer);
                }
                else if (cm == 1)
                {
                    uint w = fetch(seg + u / 2, v, layer);
                    t = fetch(cx + int((w >> uint((u & 1) * 8)) & 255u), cy, layer);
                }
                else t = fetch(seg + u, v, layer);
                if (t == 0u) discard;
                stp = (t & 32768u) != 0u;
                vec3 tc = vec3(float(t & 31u), float((t >> 5) & 31u), float((t >> 10) & 31u)) * 8.0 / 255.0;
                c = min(tc * vRgb / 128.0, vec3(1.0));
            }
            else c = min(vRgb / 255.0, vec3(1.0));
            bool semiPx = semi && (!textured || stp);
            if (uPass == 0 && semiPx) discard;
            if (uPass == 1 && (!semiPx || abr != uAbr)) discard;
            uint kind = (info >> 17) & 3u;
            if ((uTint & 1) != 0 && kind == 1u) c = mix(c, vec3(0.1, 0.85, 1.0), 0.35);
            if ((uTint & 2) != 0 && kind == 2u) c = mix(c, vec3(0.25, 1.0, 0.25), 0.3);
            if ((uTint & 4) != 0 && hidden) c = mix(c, vec3(1.0, 0.0, 0.0), 0.6);
            if (uWire != 0) c = vec3(0.0, 0.0, 0.0);
            // Editor-only view of the user's triangles in the 4:3 core (the game
            // never draws them there): every other pixel.
            if (uGhost != 0 && ((int(gl_FragCoord.x) + int(gl_FragCoord.y)) & 1) == 1) discard;
            oColor = vec4(c, 1.0);
            oId = vInfo.z;
            oPos = vec4(vWorld, 1.0);
        }
        """;

    const string VoidVertex = """
        #version 330 core
        void main()
        {
            vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;

    // Pixels nothing was drawn on (id 0) get the void colour; semi-transparent
    // surfaces over the background keep the game's black behind them.
    const string VoidFragment = """
        #version 330 core
        uniform usampler2D uIds;
        uniform vec3 uVoid;
        layout(location = 0) out vec4 oColor;
        void main()
        {
            if (texelFetch(uIds, ivec2(gl_FragCoord.xy), 0).r != 0u) discard;
            oColor = vec4(uVoid, 1.0);
        }
        """;

    readonly GL _gl;
    readonly uint _program, _voidProgram, _emptyVao;
    readonly Dictionary<string, int> _uniforms = [];
    uint _pages;
    uint _fbo, _color, _id, _pos, _depth;
    public int Width { get; private set; }
    public int Height { get; private set; }

    public Gfx(GL gl)
    {
        _gl = gl;
        _program = Compile(VertexSource, FragmentSource);
        _voidProgram = Compile(VoidVertex, VoidFragment);
        _emptyVao = gl.GenVertexArray();
    }

    public unsafe void VoidPass(Vector3 color)
    {
        var one = stackalloc GLEnum[] { GLEnum.ColorAttachment0 };
        _gl.DrawBuffers(1, one);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.ScissorTest);
        _gl.Disable(EnableCap.Blend);
        _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
        _gl.UseProgram(_voidProgram);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, _id);
        _gl.Uniform1(_gl.GetUniformLocation(_voidProgram, "uIds"), 1);
        _gl.Uniform3(_gl.GetUniformLocation(_voidProgram, "uVoid"), color.X, color.Y, color.Z);
        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.UseProgram(_program);
    }

    uint Compile(string vs, string fs)
    {
        uint Shader(ShaderType type, string src)
        {
            uint s = _gl.CreateShader(type);
            _gl.ShaderSource(s, src);
            _gl.CompileShader(s);
            _gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
            if (ok == 0) throw new InvalidOperationException($"{type}: {_gl.GetShaderInfoLog(s)}");
            return s;
        }
        uint v = Shader(ShaderType.VertexShader, vs), f = Shader(ShaderType.FragmentShader, fs);
        uint p = _gl.CreateProgram();
        _gl.AttachShader(p, v);
        _gl.AttachShader(p, f);
        _gl.LinkProgram(p);
        _gl.GetProgram(p, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0) throw new InvalidOperationException(_gl.GetProgramInfoLog(p));
        _gl.DeleteShader(v);
        _gl.DeleteShader(f);
        return p;
    }

    int U(string name)
    {
        if (!_uniforms.TryGetValue(name, out int loc)) _uniforms[name] = loc = _gl.GetUniformLocation(_program, name);
        return loc;
    }

    public unsafe void UploadPages(LevelData level)
    {
        if (_pages != 0) _gl.DeleteTexture(_pages);
        _pages = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2DArray, _pages);
        int n = Math.Max(level.PageOrder.Count, 1);
        var all = new ushort[n * 256 * 128];
        for (int i = 0; i < level.PageOrder.Count; i++)
            level.Pages[level.PageOrder[i]].CopyTo(all, i * 256 * 128);
        fixed (ushort* p = all)
            _gl.TexImage3D(TextureTarget.Texture2DArray, 0, InternalFormat.R16ui, 256, 128, (uint)n, 0,
                PixelFormat.RedInteger, PixelType.UnsignedShort, p);
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
    }

    public unsafe void EnsureTarget(int w, int h)
    {
        w = Math.Max(w, 16);
        h = Math.Max(h, 16);
        if (w == Width && h == Height && _fbo != 0) return;
        if (_fbo != 0)
        {
            _gl.DeleteFramebuffer(_fbo);
            _gl.DeleteTexture(_color);
            _gl.DeleteTexture(_id);
            _gl.DeleteTexture(_pos);
            _gl.DeleteRenderbuffer(_depth);
        }
        Width = w;
        Height = h;
        uint Tex(InternalFormat format, PixelFormat pf, PixelType type)
        {
            uint t = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, t);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, format, (uint)w, (uint)h, 0, pf, type, null);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            return t;
        }
        _color = Tex(InternalFormat.Rgba8, PixelFormat.Rgba, PixelType.UnsignedByte);
        _id = Tex(InternalFormat.R32ui, PixelFormat.RedInteger, PixelType.UnsignedInt);
        _pos = Tex(InternalFormat.Rgba32f, PixelFormat.Rgba, PixelType.Float);
        _depth = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depth);
        _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Depth24Stencil8, (uint)w, (uint)h);
        _fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _color, 0);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1, TextureTarget.Texture2D, _id, 0);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment2, TextureTarget.Texture2D, _pos, 0);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment,
            RenderbufferTarget.Renderbuffer, _depth);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public unsafe void Begin(Matrix4x4 view, Matrix4x4 proj, Vector3 cam, Vector3 voidColor)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        var buffers = stackalloc GLEnum[] { GLEnum.ColorAttachment0, GLEnum.ColorAttachment1, GLEnum.ColorAttachment2 };
        _gl.DrawBuffers(3, buffers);
        _gl.Viewport(0, 0, (uint)Width, (uint)Height);
        _gl.Disable(EnableCap.ScissorTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.Disable(EnableCap.Blend);
        _gl.ColorMask(true, true, true, true);
        _gl.DepthMask(true);
        var clearColor = stackalloc float[] { voidColor.X, voidColor.Y, voidColor.Z, 1 };
        var clearId = stackalloc uint[] { 0, 0, 0, 0 };
        var clearPos = stackalloc float[] { 0, 0, 0, 0 };
        _gl.ClearBuffer(BufferKind.Color, 0, clearColor);
        _gl.ClearBuffer(BufferKind.Color, 1, clearId);
        _gl.ClearBuffer(BufferKind.Color, 2, clearPos);
        _gl.Clear(ClearBufferMask.DepthBufferBit);
        _gl.UseProgram(_program);
        _gl.UniformMatrix4(U("uView"), 1, false, (float*)&view);
        _gl.UniformMatrix4(U("uProj"), 1, false, (float*)&proj);
        _gl.Uniform3(U("uCam"), cam.X, cam.Y, cam.Z);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2DArray, _pages);
        _gl.Uniform1(U("uPages"), 0);
        _gl.Uniform1(U("uWire"), 0);
        _gl.Uniform1(U("uGhost"), 0);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);
    }

    public void Ghost(bool on) => _gl.Uniform1(U("uGhost"), on ? 1 : 0);

    public void State(int tint, bool dropHidden, int behind)
    {
        _gl.Uniform1(U("uTint"), tint);
        _gl.Uniform1(U("uDropHidden"), dropHidden ? 1 : 0);
        _gl.Uniform1(U("uBehind"), behind);
    }

    public void Depth(bool test, bool write)
    {
        if (test) _gl.Enable(EnableCap.DepthTest); else _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(write);
    }

    public void Scissor(int x, int y, int w, int h)
    {
        _gl.Enable(EnableCap.ScissorTest);
        _gl.Scissor(x, y, (uint)Math.Max(w, 0), (uint)Math.Max(h, 0));
    }

    public void NoScissor() => _gl.Disable(EnableCap.ScissorTest);
    public void ClearDepth() => _gl.Clear(ClearBufferMask.DepthBufferBit);

    /// <summary>Opaque pass (pass 0) or one PS1 blend mode (pass 1, abr 0-3).</summary>
    public void Pass(int pass, int abr = 0)
    {
        _gl.Uniform1(U("uPass"), pass);
        _gl.Uniform1(U("uAbr"), abr);
        if (pass == 0)
        {
            _gl.Disable(EnableCap.Blend);
            return;
        }
        _gl.Enable(EnableCap.Blend, 0);
        _gl.Disable(EnableCap.Blend, 1);
        _gl.Disable(EnableCap.Blend, 2);
        switch (abr)
        {
            case 0:
                _gl.BlendEquation(GLEnum.FuncAdd);
                _gl.BlendColor(0.5f, 0.5f, 0.5f, 0.5f);
                _gl.BlendFunc(GLEnum.ConstantColor, GLEnum.ConstantColor);
                break;
            case 1:
                _gl.BlendEquation(GLEnum.FuncAdd);
                _gl.BlendFunc(GLEnum.One, GLEnum.One);
                break;
            case 2:
                _gl.BlendEquation(GLEnum.FuncReverseSubtract);
                _gl.BlendFunc(GLEnum.One, GLEnum.One);
                break;
            default:
                _gl.BlendEquation(GLEnum.FuncAdd);
                _gl.BlendColor(0.25f, 0.25f, 0.25f, 0.25f);
                _gl.BlendFunc(GLEnum.ConstantColor, GLEnum.One);
                break;
        }
    }

    public void Wire(bool on)
    {
        _gl.Uniform1(U("uWire"), on ? 1 : 0);
        _gl.PolygonMode(TriangleFace.FrontAndBack, on ? PolygonMode.Line : PolygonMode.Fill);
        if (on)
        {
            _gl.Enable(EnableCap.PolygonOffsetLine);
            _gl.PolygonOffset(-1, -1);
            _gl.ColorMask(true, true, true, true);
        }
        else _gl.Disable(EnableCap.PolygonOffsetLine);
    }

    public void End()
    {
        _gl.Disable(EnableCap.Blend);
        _gl.BlendEquation(GLEnum.FuncAdd);
        _gl.Disable(EnableCap.ScissorTest);
        _gl.Disable(EnableCap.DepthTest);
        _gl.DepthMask(true);
        _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void Blit(int x, int y, int w, int h)
    {
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
        _gl.ReadBuffer(ReadBufferMode.ColorAttachment0);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
        _gl.BlitFramebuffer(0, 0, Width, Height, x, y, x + w, y + h, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public unsafe uint ReadId(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return 0;
        uint id = 0;
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
        _gl.ReadBuffer(ReadBufferMode.ColorAttachment1);
        _gl.ReadPixels(x, y, 1, 1, PixelFormat.RedInteger, PixelType.UnsignedInt, &id);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return id;
    }

    public unsafe uint[] ReadIds(int x, int y, int w, int h)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        w = Math.Clamp(w, 1, Width - x);
        h = Math.Clamp(h, 1, Height - y);
        var ids = new uint[w * h];
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
        _gl.ReadBuffer(ReadBufferMode.ColorAttachment1);
        fixed (uint* p = ids) _gl.ReadPixels(x, y, (uint)w, (uint)h, PixelFormat.RedInteger, PixelType.UnsignedInt, p);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return ids;
    }

    public unsafe Vector4 ReadPos(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return default;
        Vector4 p = default;
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
        _gl.ReadBuffer(ReadBufferMode.ColorAttachment2);
        _gl.ReadPixels(x, y, 1, 1, PixelFormat.Rgba, PixelType.Float, &p);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return p;
    }

    /// <summary>RGBA thumbnail of a texture tile, for the panels.</summary>
    public unsafe uint Thumbnail(uint texture, byte[] rgba, int w, int h)
    {
        if (texture == 0) texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        fixed (byte* p = rgba)
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)w, (uint)h, 0, PixelFormat.Rgba,
                PixelType.UnsignedByte, p);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        return texture;
    }

    public void Dispose()
    {
        _gl.DeleteProgram(_program);
        _gl.DeleteProgram(_voidProgram);
        _gl.DeleteVertexArray(_emptyVao);
        if (_pages != 0) _gl.DeleteTexture(_pages);
        if (_fbo != 0)
        {
            _gl.DeleteFramebuffer(_fbo);
            _gl.DeleteTexture(_color);
            _gl.DeleteTexture(_id);
            _gl.DeleteTexture(_pos);
            _gl.DeleteRenderbuffer(_depth);
        }
    }
}
