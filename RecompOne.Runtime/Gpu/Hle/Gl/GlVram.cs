using Silk.NET.OpenGL;

namespace RecompOne.Runtime.Hle;

public sealed class GlVram
{
    public static int Scale { get; set; } = 4;
    public static int Width => VramShadow.Width * Scale;
    public static int Height => VramShadow.Height * Scale;

    readonly GL _gl;
    bool _gles;
    uint _tex, _fbo;
    uint _stageTex, _stageFbo;
    uint _scratchTex;
    byte[] _transfer = [];

    public uint Texture => _tex;
    public uint Fbo => _fbo;

    public GlVram(GL gl) => _gl = gl;

    public void Init(bool gles = false)
    {
        _gles = gles;
        _tex = CreateTex(Width, Height);
        _fbo = CreateFbo(_tex);
        _stageTex = CreateTex(VramShadow.Width, VramShadow.Height);
        _stageFbo = CreateFbo(_stageTex);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    unsafe uint CreateTex(int w, int h)
    {
        uint t = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, t);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        // null data: allocate GPU storage without a giant CPU zero-fill (matters at 8x / 4K).
        // UNSIGNED_SHORT_1_5_5_5_REV is not part of OpenGL ES. Use RGBA8 on
        // Android; the shaders already quantize to PS1 colour precision.
        _gl.TexImage2D(TextureTarget.Texture2D, 0,
            _gles ? InternalFormat.Rgba8 : InternalFormat.Rgb5A1,
            (uint)w, (uint)h, 0, PixelFormat.Rgba,
            _gles ? PixelType.UnsignedByte : PixelType.UnsignedShort1555Rev, null);
        return t;
    }

    uint CreateFbo(uint tex)
    {
        uint f = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, f);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            TextureTarget.Texture2D, tex, 0);
        return f;
    }

    public void BindDraw()
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.Viewport(0, 0, (uint)Width, (uint)Height);
    }

    public void Barrier() => _gl.TextureBarrier();

    public void WriteRect(int x, int y, int w, int h, ReadOnlySpan<ushort> px)
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.BindTexture(TextureTarget.Texture2D, _stageTex);
        if (_gles)
        {
            int count = w * h;
            if (_transfer.Length < count * 4) _transfer = new byte[count * 4];
            for (int i = 0; i < count; i++)
            {
                ushort p = px[i];
                int o = i * 4;
                _transfer[o] = (byte)((p & 0x1f) * 255 / 31);
                _transfer[o + 1] = (byte)(((p >> 5) & 0x1f) * 255 / 31);
                _transfer[o + 2] = (byte)(((p >> 10) & 0x1f) * 255 / 31);
                _transfer[o + 3] = (byte)((p & 0x8000) != 0 ? 255 : 0);
            }
            _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            _gl.TexSubImage2D<byte>(TextureTarget.Texture2D, 0, x, y, (uint)w, (uint)h,
                PixelFormat.Rgba, PixelType.UnsignedByte, _transfer.AsSpan(0, count * 4));
        }
        else
        {
            _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 2);
            _gl.TexSubImage2D(TextureTarget.Texture2D, 0, x, y, (uint)w, (uint)h,
                PixelFormat.Rgba, PixelType.UnsignedShort1555Rev, px);
        }

        _gl.Disable(EnableCap.ScissorTest);
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _stageFbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _fbo);
        _gl.BlitFramebuffer(x, y, x + w, y + h,
            x * Scale, y * Scale, (x + w) * Scale, (y + h) * Scale,
            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
    }

    public void Fill(int x, int y, int w, int h, ushort color15)
    {
        float r = (color15 & 0x1F) / 31f, g = ((color15 >> 5) & 0x1F) / 31f, b = ((color15 >> 10) & 0x1F) / 31f;
        float a = (color15 & 0x8000) != 0 ? 1f : 0f;
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.Enable(EnableCap.ScissorTest);
        _gl.Scissor(x * Scale, y * Scale, (uint)Math.Max(0, w * Scale), (uint)Math.Max(0, h * Scale));
        _gl.ClearColor(r, g, b, a);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
        _gl.Disable(EnableCap.ScissorTest);
    }

    public void CopyRect(int sx, int sy, int dx, int dy, int w, int h)
    {
        int sw = w * Scale, sh = h * Scale;
        bool overlap = sx < dx + w && dx < sx + w && sy < dy + h && dy < sy + h;
        if (!overlap)
        {
            _gl.CopyImageSubData(_tex, CopyImageSubDataTarget.Texture2D, 0, sx * Scale, sy * Scale, 0,
                _tex, CopyImageSubDataTarget.Texture2D, 0, dx * Scale, dy * Scale, 0, (uint)sw, (uint)sh, 1);
            return;
        }

        EnsureScratch();
        _gl.CopyImageSubData(_tex, CopyImageSubDataTarget.Texture2D, 0, sx * Scale, sy * Scale, 0,
            _scratchTex, CopyImageSubDataTarget.Texture2D, 0, 0, 0, 0, (uint)sw, (uint)sh, 1);
        _gl.CopyImageSubData(_scratchTex, CopyImageSubDataTarget.Texture2D, 0, 0, 0, 0,
            _tex, CopyImageSubDataTarget.Texture2D, 0, dx * Scale, dy * Scale, 0, (uint)sw, (uint)sh, 1);
    }

    public void ReadRect(int x, int y, int w, int h, Span<ushort> dst)
    {
        _gl.Disable(EnableCap.ScissorTest);
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _fbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _stageFbo);
        _gl.BlitFramebuffer(x * Scale, y * Scale, (x + w) * Scale, (y + h) * Scale,
            x, y, x + w, y + h, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);

        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _stageFbo);
        if (_gles)
        {
            int count = w * h;
            if (_transfer.Length < count * 4) _transfer = new byte[count * 4];
            _gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
            _gl.ReadPixels<byte>(x, y, (uint)w, (uint)h, PixelFormat.Rgba,
                PixelType.UnsignedByte, _transfer.AsSpan(0, count * 4));
            for (int i = 0; i < count; i++)
            {
                int o = i * 4;
                dst[i] = (ushort)((_transfer[o] * 31 / 255) |
                    ((_transfer[o + 1] * 31 / 255) << 5) |
                    ((_transfer[o + 2] * 31 / 255) << 10) |
                    (_transfer[o + 3] >= 128 ? 0x8000 : 0));
            }
        }
        else
        {
            _gl.PixelStore(PixelStoreParameter.PackAlignment, 2);
            _gl.ReadPixels(x, y, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.UnsignedShort1555Rev, dst);
        }
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
    }

    void EnsureScratch()
    {
        if (_scratchTex != 0) return;
        _scratchTex = CreateTex(Width, Height);
    }

    public void Dispose()
    {
        if (_fbo != 0) _gl.DeleteFramebuffer(_fbo);
        if (_stageFbo != 0) _gl.DeleteFramebuffer(_stageFbo);
        if (_tex != 0) _gl.DeleteTexture(_tex);
        if (_stageTex != 0) _gl.DeleteTexture(_stageTex);
        if (_scratchTex != 0) _gl.DeleteTexture(_scratchTex);
    }
}
