using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace RecompOne.Runtime.Hle;

public sealed class GlBackend : IGpuBackend
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void TextureBarrierProc();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void ShadingRateProc(uint rate);

    [StructLayout(LayoutKind.Sequential)]
    struct GlVertex { public float X, Y; public uint Color; public int Clut, Texpage; public float U, V, InvZ; }

    const int MaxVerts = 0x40000;

    readonly GL _gl;
    readonly GlVram _vram;
    readonly GlDisplayRt?[] _rts = new GlDisplayRt?[2];
    long _rtStamp;
    long _frame;
    int _frameFlushes;
    int _frameWritebacks;
    int _frameVertices;

    uint _vao, _vbo, _presentVao, _presentVbo, _progPrim, _progPrimFast, _progPresent, _progPresent24;
    uint _presentFbo, _presentTex;
    int _presentW, _presentH;
    bool _presentNearest;
    bool _gles;
    GlesFramebufferFetchPath _glesFramebufferFetchPath;
    TextureBarrierProc? _glesTextureBarrier;
    ShadingRateProc? _glesShadingRate;
    byte[] _readback = [];

    readonly GlVertex[] _verts = new GlVertex[MaxVerts];
    int _count;

    HleDrawEnv _env;

    GlDisplayRt? _kTarget;
    bool _kTransparent;
    bool _kSubtractBatch;
    int _kBlend, _kSetMask, _kCheckMask;
    int _kTwAndX, _kTwAndY, _kTwOrX, _kTwOrY;
    int _kClipX0, _kClipY0, _kClipX1, _kClipY1;
    int _uTexWindow, _uBlend, _uBlendOpaque, _uSetMask, _uCheckMask, _uPosBias, _uFbInv, _uFilterMode, _uFilterStrength, _uDedither;
    int _ufTexWindow, _ufBlendOpaque, _ufSetMask, _ufCheckMask, _ufPosBias, _ufFbInv, _ufFilterMode, _ufFilterStrength, _ufDedither;
    int _uPresentOrigin, _uPresentSize, _uPresentTexSize, _uPresent24Origin, _uPresent24Size;

    public bool Ready { get; private set; }
    public string LastDiagnostic { get; private set; } = "ok";
    public int LastFrameFlushes { get; private set; }
    public int LastFrameWritebacks { get; private set; }
    public int LastFrameVertices { get; private set; }
    public GlesFramebufferFetchPath FramebufferFetchPath => _glesFramebufferFetchPath;

    public GlBackend(GL gl) { _gl = gl; _vram = new GlVram(gl); }

    public bool ConfigureGlesTextureBarrier(nint address)
    {
        if (address == 0) return false;
        _glesTextureBarrier = Marshal.GetDelegateForFunctionPointer<TextureBarrierProc>(address);
        return true;
    }

    public bool ConfigureGlesShadingRate(nint address)
    {
        if (address == 0) return false;
        _glesShadingRate = Marshal.GetDelegateForFunctionPointer<ShadingRateProc>(address);
        return true;
    }

    public unsafe void InitGl(bool gles = false,
        GlesFramebufferFetchPath framebufferFetch = GlesFramebufferFetchPath.None)
    {
        _gles = gles;
        _glesFramebufferFetchPath = gles ? framebufferFetch : GlesFramebufferFetchPath.None;
        _vram.Init(gles);
        CheckError("vram.init");

        _progPrim = GlShaders.Build(_gl, GlShaders.PrimVs, GlShaders.PrimFs, "prim", gles, _glesFramebufferFetchPath);
        if (_glesFramebufferFetchPath != GlesFramebufferFetchPath.None)
            _progPrimFast = GlShaders.Build(_gl, GlShaders.PrimVs, GlShaders.PrimFs, "prim-fast", gles,
                opaqueOnly: true);
        _progPresent = GlShaders.Build(_gl, GlShaders.FullscreenVs, GlShaders.PresentFs, "present", gles);
        _progPresent24 = GlShaders.Build(_gl, GlShaders.FullscreenVs, GlShaders.Present24Fs, "present24", gles);
        CheckError("shaders");
        if (_progPrim == 0 || (_glesFramebufferFetchPath != GlesFramebufferFetchPath.None && _progPrimFast == 0) ||
            _progPresent == 0 || _progPresent24 == 0) return;

        _uTexWindow = _gl.GetUniformLocation(_progPrim, "uTexWindow");
        _uBlend = _gl.GetUniformLocation(_progPrim, "uBlend");
        _uBlendOpaque = _gl.GetUniformLocation(_progPrim, "uBlendOpaque");
        _uSetMask = _gl.GetUniformLocation(_progPrim, "uSetMask");
        _uCheckMask = _gl.GetUniformLocation(_progPrim, "uCheckMask");
        _uPosBias = _gl.GetUniformLocation(_progPrim, "uPosBias");
        _uFbInv = _gl.GetUniformLocation(_progPrim, "uFbInv");
        _uFilterMode = _gl.GetUniformLocation(_progPrim, "uFilterMode");
        _uFilterStrength = _gl.GetUniformLocation(_progPrim, "uFilterStrength");
        _uDedither = _gl.GetUniformLocation(_progPrim, "uDedither");

        _gl.UseProgram(_progPrim);
        _gl.Uniform1(_gl.GetUniformLocation(_progPrim, "uVram"), 0);
        _gl.Uniform1(_gl.GetUniformLocation(_progPrim, "uDest"), 1);
        _gl.Uniform1(_gl.GetUniformLocation(_progPrim, "uScale"), GlVram.Scale);
        // At 8x the final linear downsample already combines several internal
        // texels per screen pixel. Running four extra PS1 VRAM decodes here is
        // redundant and dominates mobile fragment cost, so keep the true 8x
        // raster while sampling each internal fragment once.
        _gl.Uniform1(_uFilterMode, _gles && GlVram.Scale >= 8 ? 0 : GpuHle.EffectiveTextureFilter);
        _gl.Uniform1(_uFilterStrength, GpuHle.TextureFilterStrength);
        _gl.Uniform1(_uDedither, GpuHle.DeditherActive ? 1 : 0);

        if (_progPrimFast != 0)
        {
            _ufTexWindow = _gl.GetUniformLocation(_progPrimFast, "uTexWindow");
            _ufBlendOpaque = _gl.GetUniformLocation(_progPrimFast, "uBlendOpaque");
            _ufSetMask = _gl.GetUniformLocation(_progPrimFast, "uSetMask");
            _ufCheckMask = _gl.GetUniformLocation(_progPrimFast, "uCheckMask");
            _ufPosBias = _gl.GetUniformLocation(_progPrimFast, "uPosBias");
            _ufFbInv = _gl.GetUniformLocation(_progPrimFast, "uFbInv");
            _ufFilterMode = _gl.GetUniformLocation(_progPrimFast, "uFilterMode");
            _ufFilterStrength = _gl.GetUniformLocation(_progPrimFast, "uFilterStrength");
            _ufDedither = _gl.GetUniformLocation(_progPrimFast, "uDedither");

            _gl.UseProgram(_progPrimFast);
            _gl.Uniform1(_gl.GetUniformLocation(_progPrimFast, "uVram"), 0);
            _gl.Uniform1(_gl.GetUniformLocation(_progPrimFast, "uDest"), 1);
            _gl.Uniform1(_gl.GetUniformLocation(_progPrimFast, "uScale"), GlVram.Scale);
            _gl.Uniform1(_ufFilterMode, _gles && GlVram.Scale >= 8 ? 0 : GpuHle.EffectiveTextureFilter);
            _gl.Uniform1(_ufFilterStrength, GpuHle.TextureFilterStrength);
            _gl.Uniform1(_ufDedither, GpuHle.DeditherActive ? 1 : 0);
        }

        _uPresentOrigin = _gl.GetUniformLocation(_progPresent, "uOrigin");
        _uPresentSize = _gl.GetUniformLocation(_progPresent, "uSize");
        _uPresentTexSize = _gl.GetUniformLocation(_progPresent, "uTexSize");
        _gl.UseProgram(_progPresent);
        _gl.Uniform1(_gl.GetUniformLocation(_progPresent, "uVram"), 0);

        _uPresent24Origin = _gl.GetUniformLocation(_progPresent24, "uOrigin");
        _uPresent24Size = _gl.GetUniformLocation(_progPresent24, "uSize");
        _gl.UseProgram(_progPresent24);
        _gl.Uniform1(_gl.GetUniformLocation(_progPresent24, "uVram"), 0);
        _gl.Uniform1(_gl.GetUniformLocation(_progPresent24, "uScale"), GlVram.Scale);

        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(MaxVerts * sizeof(GlVertex)), null, BufferUsageARB.DynamicDraw);
        uint stride = (uint)sizeof(GlVertex);
        _gl.EnableVertexAttribArray(0); _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1); _gl.VertexAttribIPointer(1, 1, VertexAttribIType.UnsignedInt, stride, (void*)8);
        _gl.EnableVertexAttribArray(2); _gl.VertexAttribIPointer(2, 1, VertexAttribIType.Int, stride, (void*)12);
        _gl.EnableVertexAttribArray(3); _gl.VertexAttribIPointer(3, 1, VertexAttribIType.Int, stride, (void*)16);
        _gl.EnableVertexAttribArray(4); _gl.VertexAttribPointer(4, 2, VertexAttribPointerType.Float, false, stride, (void*)20);
        _gl.EnableVertexAttribArray(5); _gl.VertexAttribPointer(5, 1, VertexAttribPointerType.Float, false, stride, (void*)28);

        // fullscreen quad for present, real vbo since gl_VertexID without arrays does not draw on mesa for some reason?? or i did it wrong?
        _presentVao = _gl.GenVertexArray();
        _presentVbo = _gl.GenBuffer();
        _gl.BindVertexArray(_presentVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _presentVbo);
        float[] quad = { -1f, -1f, 1f, -1f, -1f, 1f, 1f, 1f };
        fixed (float* qp = quad)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quad.Length * sizeof(float)), qp, BufferUsageARB.StaticDraw);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);

        _presentTex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _presentTex);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _presentFbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _presentFbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _presentTex, 0);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        CheckError("geometry/fbo");

        _kClipX1 = 1023; _kClipY1 = 511;
        Ready = true;
    }

    public void SetDrawEnv(in HleDrawEnv env) => _env = env;

    const int FbSlackW = 64;
    const int FbSlackH = 32;

    GlDisplayRt? Classify()
    {
        int clipX = _env.ClipX0, clipY = _env.ClipY0;
        int clipW = _env.ClipX1 - _env.ClipX0 + 1, clipH = _env.ClipY1 - _env.ClipY0 + 1;
        if (clipW <= 0 || clipH <= 0) return null;

        long bestStamp = -1;
        int fbX = 0, fbY = 0, fbW = 0, fbH = 0;
        for (int i = 0; i < GpuHle.RectCount; i++)
        {
            var r = GpuHle.GetRect(i);
            if (!r.Valid || r.W <= 0 || r.H <= 0 || r.Stamp <= bestStamp) continue;

            bool clipInside = clipX >= r.X && clipX + clipW <= r.X + r.W &&
                              clipY >= r.Y && clipY + clipH <= r.Y + r.H;
            bool clipIsFb = clipX <= r.X && clipX + clipW >= r.X + r.W &&
                            clipY <= r.Y && clipY + clipH >= r.Y + r.H &&
                            clipW - r.W <= FbSlackW && clipH - r.H <= FbSlackH;
            if (clipInside) { bestStamp = r.Stamp; fbX = r.X; fbY = r.Y; fbW = r.W; fbH = r.H; }
            else if (clipIsFb) { bestStamp = r.Stamp; fbX = clipX; fbY = clipY; fbW = clipW; fbH = clipH; }
        }
        return bestStamp < 0 ? null : GetOrCreateRt(fbX, fbY, fbW, fbH);
    }

    GlDisplayRt GetOrCreateRt(int fbX, int fbY, int fbW, int fbH)
    {
        int slot = -1;
        for (int i = 0; i < _rts.Length; i++)
            if (_rts[i] is { } rt && rt.X == fbX && rt.Y == fbY)
            {
                bool sameW = rt.W == fbW;
                bool fitsH = rt.H >= fbH && rt.H - fbH <= FbSlackH;
                if (sameW && fitsH && rt.Margin == GpuHle.WideMargin(rt.W))
                {
                    rt.Stamp = ++_rtStamp;
                    return rt;
                }
                slot = i;
                break;
            }

        if (slot < 0)
        {
            slot = 0;
            for (int i = 1; i < _rts.Length; i++)
            {
                if (_rts[i] == null) { slot = i; break; }
                if (_rts[slot] != null && _rts[i]!.Stamp < _rts[slot]!.Stamp) slot = i;
            }
        }

        if (_rts[slot] is { } old)
        {
            if (old.Dirty) Writeback(old);
            old.Destroy(_gl);
        }

        var fresh = new GlDisplayRt { X = fbX, Y = fbY, W = fbW, H = fbH, Margin = GpuHle.WideMargin(fbW), Stamp = ++_rtStamp, LastDrawFrame = _frame };
        fresh.Create(_gl);
        _rts[slot] = fresh;
        SyncRtFromVram(fresh, fbX, fbY, fbW, fbH);
        return fresh;
    }

    void Writeback(GlDisplayRt rt)
    {
        _frameWritebacks++;
        int s = GlVram.Scale;
        _gl.Disable(EnableCap.ScissorTest);
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, rt.Fbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _vram.Fbo);
        _gl.BlitFramebuffer(rt.Margin * s, 0, (rt.Margin + rt.W) * s, rt.H * s,
            rt.X * s, rt.Y * s, (rt.X + rt.W) * s, (rt.Y + rt.H) * s,
            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        rt.Dirty = false;
    }

    void SyncRtFromVram(GlDisplayRt rt, int rx, int ry, int rw, int rh)
    {
        int x0 = Math.Max(rx, rt.X), y0 = Math.Max(ry, rt.Y);
        int x1 = Math.Min(rx + rw, rt.X + rt.W), y1 = Math.Min(ry + rh, rt.Y + rt.H);
        if (x0 >= x1 || y0 >= y1) return;
        int s = GlVram.Scale;
        _gl.Disable(EnableCap.ScissorTest);
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _vram.Fbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, rt.Fbo);
        _gl.BlitFramebuffer(x0 * s, y0 * s, x1 * s, y1 * s,
            (x0 - rt.X + rt.Margin) * s, (y0 - rt.Y) * s, (x1 - rt.X + rt.Margin) * s, (y1 - rt.Y) * s,
            ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    void WritebackDirtyIntersecting(int x, int y, int w, int h)
    {
        foreach (var rt in _rts)
            if (rt is { Dirty: true } && rt.Intersects(x, y, w, h)) Writeback(rt);
    }

    void SyncRtsFromVram(int x, int y, int w, int h)
    {
        foreach (var rt in _rts)
            if (rt != null && rt.Intersects(x, y, w, h)) SyncRtFromVram(rt, x, y, w, h);
    }

    void CheckTextureFeedback(in PrimFlags f)
    {
        if (!f.Textured) return;
        int px = (f.TPage & 0xF) * 64;
        int py = ((f.TPage >> 4) & 1) * 256;
        int depth = (f.TPage >> 7) & 3;
        int pw = depth == 0 ? 64 : depth == 1 ? 128 : 256;
        foreach (var rt in _rts)
            if (rt is { Dirty: true } && rt.Intersects(px, py, pw, 256))
            {
                Flush();
                Writeback(rt);
            }
    }

    bool DesiredMatches(bool transparent, int blend)
    {
        int twAndX = ~(_env.TwMaskX * 8) & 0xFF, twAndY = ~(_env.TwMaskY * 8) & 0xFF;
        int twOrX = (_env.TwOffX & _env.TwMaskX) * 8, twOrY = (_env.TwOffY & _env.TwMaskY) * 8;
        bool blendMatches = _glesFramebufferFetchPath != GlesFramebufferFetchPath.None
            ? true
            : _gles
            ? _kSubtractBatch == (transparent && blend == 2)
            : _kTransparent == transparent && _kBlend == blend;
        return blendMatches
            && _kSetMask == (_env.SetMask ? 1 : 0) && _kCheckMask == (_env.CheckMask ? 1 : 0)
            && _kTwAndX == twAndX && _kTwAndY == twAndY && _kTwOrX == twOrX && _kTwOrY == twOrY
            && _kClipX0 == _env.ClipX0 && _kClipY0 == _env.ClipY0 && _kClipX1 == _env.ClipX1 && _kClipY1 == _env.ClipY1;
    }

    void Begin(in PrimFlags f, int vertsNeeded)
    {
        bool transparent = f.SemiTrans;
        int blend = f.BlendMode;
        bool subtractBatch = transparent && blend == 2;
        var target = Classify();
        if (_count > 0 && (target != _kTarget || !DesiredMatches(transparent, blend))) Flush();
        if (_count + vertsNeeded > MaxVerts) Flush();
        CheckTextureFeedback(f);

        _kTarget = target;
        _kSubtractBatch = subtractBatch;
        _kTransparent = transparent; _kBlend = blend;
        _kSetMask = _env.SetMask ? 1 : 0; _kCheckMask = _env.CheckMask ? 1 : 0;
        _kTwAndX = ~(_env.TwMaskX * 8) & 0xFF; _kTwAndY = ~(_env.TwMaskY * 8) & 0xFF;
        _kTwOrX = (_env.TwOffX & _env.TwMaskX) * 8; _kTwOrY = (_env.TwOffY & _env.TwMaskY) * 8;
        _kClipX0 = _env.ClipX0; _kClipY0 = _env.ClipY0; _kClipX1 = _env.ClipX1; _kClipY1 = _env.ClipY1;
    }

    bool DitherOf(in PrimFlags f) => _env.Dither && (f.Gouraud || (f.Textured && !f.RawTexture));

    GlVertex V(in HleVertex v, in PrimFlags f, bool dither)
    {
        uint color = (f.Textured && f.RawTexture) ? 0x808080u : (uint)(v.R | (v.G << 8) | (v.B << 16));
        int tpage = f.Textured ? (f.TPage & 0x1FF) : 0x8000;
        if (dither) tpage |= 0x400;
        if (f.SemiTrans) tpage |= 0x1000;
        tpage |= (f.BlendMode & 3) << 13;
        float invZ = v.HasGteZ && v.Z > 0f ? 1f / v.Z : 0f;
        return new GlVertex
        {
            X = v.X, Y = v.Y,
            Color = color,
            Clut = f.Clut & 0x7FFF,
            Texpage = tpage,
            U = v.U, V = v.V,
            InvZ = invZ,
        };
    }

    public void DrawTri(in HleVertex a, in HleVertex b, in HleVertex c, in PrimFlags f)
    {
        Begin(f, 3);
        bool dith = DitherOf(f);
        _verts[_count++] = V(a, f, dith); _verts[_count++] = V(b, f, dith); _verts[_count++] = V(c, f, dith);
    }

    public void DrawRect(in HleRect r, in PrimFlags f)
    {
        Begin(f, 6);
        var a = new HleVertex { X = r.X, Y = r.Y, R = r.R, G = r.G, B = r.B, U = r.U, V = r.V };
        var b = new HleVertex { X = r.X + r.W, Y = r.Y, R = r.R, G = r.G, B = r.B, U = (short)(r.U + r.W), V = r.V };
        var c = new HleVertex { X = r.X, Y = r.Y + r.H, R = r.R, G = r.G, B = r.B, U = r.U, V = (short)(r.V + r.H) };
        var d = new HleVertex { X = r.X + r.W, Y = r.Y + r.H, R = r.R, G = r.G, B = r.B, U = (short)(r.U + r.W), V = (short)(r.V + r.H) };
        _verts[_count++] = V(a, f, false); _verts[_count++] = V(b, f, false); _verts[_count++] = V(c, f, false);
        _verts[_count++] = V(b, f, false); _verts[_count++] = V(d, f, false); _verts[_count++] = V(c, f, false);
    }

    public void DrawLine(in HleVertex a, in HleVertex b, in PrimFlags f)
    {
        Begin(f, 6);
        bool dith = _env.Dither;
        float x1 = a.X, y1 = a.Y;
        float x2 = b.X, y2 = b.Y;
        float dx = x2 - x1, dy = y2 - y1;

        if (dx == 0 && dy == 0)
        {
            LineVert(x1, y1, a, f, dith); LineVert(x1 + 1, y1, a, f, dith); LineVert(x1 + 1, y1 + 1, a, f, dith);
            LineVert(x1 + 1, y1 + 1, a, f, dith); LineVert(x1, y1 + 1, a, f, dith); LineVert(x1, y1, a, f, dith);
            return;
        }

        float xo, yo;
        if (Math.Abs(dx) > Math.Abs(dy)) { xo = 0; yo = 1; if (dx > 0) x2++; else x1++; }
        else { xo = 1; yo = 0; if (dy > 0) y2++; else y1++; }

        LineVert(x1, y1, a, f, dith); LineVert(x2, y2, b, f, dith); LineVert(x2 + xo, y2 + yo, b, f, dith);
        LineVert(x2 + xo, y2 + yo, b, f, dith); LineVert(x1 + xo, y1 + yo, a, f, dith); LineVert(x1, y1, a, f, dith);
    }

    void LineVert(float x, float y, in HleVertex src, in PrimFlags f, bool dither)
    {
        var v = src; v.X = x; v.Y = y;
        _verts[_count++] = V(v, f, dither);
    }

    public void FillRect(int x, int y, int w, int h, ushort color15)
    {
        Flush();
        _vram.Fill(x, y, w, h, color15);
        foreach (var rt in _rts)
        {
            if (rt == null || !rt.Intersects(x, y, w, h)) continue;
            if (rt.Covers(x, y, x + w - 1, y + h - 1))
            {
                FillRtFull(rt, color15);
                rt.Dirty = false;
                rt.LastDrawFrame = _frame;
            }
            else SyncRtFromVram(rt, x, y, w, h);
        }
    }

    void FillRtFull(GlDisplayRt rt, ushort color15)
    {
        float r = (color15 & 0x1F) / 31f, g = ((color15 >> 5) & 0x1F) / 31f, b = ((color15 >> 10) & 0x1F) / 31f;
        float a = (color15 & 0x8000) != 0 ? 1f : 0f;
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, rt.Fbo);
        _gl.Disable(EnableCap.ScissorTest);
        _gl.ClearColor(r, g, b, a);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    public void CopyVram(int sx, int sy, int dx, int dy, int w, int h)
    {
        Flush();
        WritebackDirtyIntersecting(sx, sy, w, h);
        _vram.CopyRect(sx, sy, dx, dy, w, h);
        SyncRtsFromVram(dx, dy, w, h);
    }

    public void WriteVram(int x, int y, int w, int h, ReadOnlySpan<ushort> px)
    {
        Flush();
        _vram.WriteRect(x, y, w, h, px);
        SyncRtsFromVram(x, y, w, h);
    }

    public void ReadVram(int x, int y, int w, int h, Span<ushort> px)
    {
        Flush();
        WritebackDirtyIntersecting(x, y, w, h);
        _vram.ReadRect(x, y, w, h, px);
    }

    void BindPrimState(bool fast, GlDisplayRt? rt, uint destTex)
    {
        uint program = fast ? _progPrimFast : _progPrim;
        int uTexWindow = fast ? _ufTexWindow : _uTexWindow;
        int uBlendOpaque = fast ? _ufBlendOpaque : _uBlendOpaque;
        int uSetMask = fast ? _ufSetMask : _uSetMask;
        int uCheckMask = fast ? _ufCheckMask : _uCheckMask;
        int uPosBias = fast ? _ufPosBias : _uPosBias;
        int uFbInv = fast ? _ufFbInv : _uFbInv;
        int uFilterMode = fast ? _ufFilterMode : _uFilterMode;
        int uFilterStrength = fast ? _ufFilterStrength : _uFilterStrength;
        int uDedither = fast ? _ufDedither : _uDedither;

        _gl.UseProgram(program);
        _gl.BindVertexArray(_vao);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _vram.Texture);
        _gl.ActiveTexture(TextureUnit.Texture1);
        _gl.BindTexture(TextureTarget.Texture2D, _kCheckMask != 0 ? destTex : _vram.Texture);
        _gl.ActiveTexture(TextureUnit.Texture0);
        if (rt != null)
        {
            _gl.Uniform2(uPosBias, (float)(rt.Margin - rt.X), (float)(-rt.Y));
            _gl.Uniform2(uFbInv, 2f / rt.Wide1x, 2f / rt.H);
        }
        else
        {
            _gl.Uniform2(uPosBias, 0f, 0f);
            _gl.Uniform2(uFbInv, 2f / VramShadow.Width, 2f / VramShadow.Height);
        }
        _gl.Uniform4(uTexWindow, _kTwAndX, _kTwAndY, _kTwOrX, _kTwOrY);
        _gl.Uniform1(uSetMask, _kSetMask == 1 ? 1f : 0f);
        _gl.Uniform1(uCheckMask, _kCheckMask);
        _gl.Uniform1(uFilterMode, _gles && GlVram.Scale >= 8 ? 0 : GpuHle.EffectiveTextureFilter);
        _gl.Uniform1(uFilterStrength, GpuHle.TextureFilterStrength);
        _gl.Uniform1(uDedither, GpuHle.DeditherActive ? 1 : 0);
        _gl.Uniform4(uBlendOpaque, 1f, 1f, 1f, 0f);
    }

    public unsafe void Flush()
    {
        if (_count == 0) return;
        _frameFlushes++;
        _frameVertices += _count;

        var rt = _kTarget;
        uint destTex;
        if (rt == null)
        {
            _vram.BindDraw();
            destTex = _vram.Texture;
        }
        else
        {
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, rt.Fbo);
            _gl.Viewport(0, 0, (uint)rt.TexW, (uint)rt.TexH);
            destTex = rt.Tex;
        }

        // Display render targets are separate from the PS1 texture VRAM. Most
        // Crash batches therefore have no feedback dependency at all. Only
        // synchronize when the fragment shader genuinely reads the texture that
        // is currently attached for drawing (mask checks, or direct VRAM draws).
        bool readsDrawTarget = (_kCheckMask != 0 && _glesFramebufferFetchPath == GlesFramebufferFetchPath.None) ||
                               (rt == null && BatchSamplesVram());
        if (readsDrawTarget)
        {
            Barrier();
        }

        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.ScissorTest);
        int s = GlVram.Scale;
        if (rt == null)
        {
            int sw = _kClipX1 - _kClipX0 + 1, sh = _kClipY1 - _kClipY0 + 1;
            _gl.Scissor(_kClipX0 * s, _kClipY0 * s, (uint)Math.Max(0, sw * s), (uint)Math.Max(0, sh * s));
        }
        else
        {
            int cx0 = _kClipX0 - rt.X + rt.Margin, cy0 = _kClipY0 - rt.Y;
            int cx1 = _kClipX1 - rt.X + rt.Margin, cy1 = _kClipY1 - rt.Y;
            if (rt.Margin > 0 && _kClipX0 <= rt.X && _kClipX1 >= rt.X + rt.W - 1) { cx0 = 0; cx1 = rt.Wide1x - 1; }
            _gl.Scissor(cx0 * s, cy0 * s, (uint)Math.Max(0, (cx1 - cx0 + 1) * s), (uint)Math.Max(0, (cy1 - cy0 + 1) * s));
        }

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        bool splitFetch = _glesFramebufferFetchPath != GlesFramebufferFetchPath.None && _progPrimFast != 0 &&
                          GlVram.Scale >= 8 && rt != null && _kCheckMask == 0;
        if (splitFetch)
        {
            // Upload the complete PS1 draw list once, then switch shaders only
            // at opaque/transparent boundaries. This preserves exact primitive
            // order (important on the map) without repeating buffer uploads and
            // uniform setup for every blend-mode change during Crash's spin.
            _gl.BufferSubData<GlVertex>(BufferTargetARB.ArrayBuffer, 0, _verts.AsSpan(0, _count));
            BindPrimState(true, rt, destTex);
            BindPrimState(false, rt, destTex);
            _gl.Disable(EnableCap.Blend);

            int runStart = 0;
            while (runStart < _count)
            {
                bool transparent = (_verts[runStart].Texpage & 0x1000) != 0;
                int runEnd = runStart + 3;
                while (runEnd < _count &&
                       ((_verts[runEnd].Texpage & 0x1000) != 0) == transparent)
                    runEnd += 3;

                _gl.UseProgram(transparent ? _progPrim : _progPrimFast);
                if (_glesShadingRate != null)
                    _glesShadingRate(transparent ? 0x96A6u : 0x96A9u);
                _gl.DrawArrays(PrimitiveType.Triangles, runStart, (uint)(runEnd - runStart));
                runStart = runEnd;
            }
            if (_glesShadingRate != null)
                _glesShadingRate(0x96A6); // GL_SHADING_RATE_1X1_PIXELS_QCOM
        }
        else
        {
            BindPrimState(false, rt, destTex);
            _gl.BufferSubData<GlVertex>(BufferTargetARB.ArrayBuffer, 0, _verts.AsSpan(0, _count));

            // Coarse shading cannot accelerate a mixed framebuffer-fetch shader,
            // but remains useful on the fixed-function fallback.
            bool coarseShading = _glesFramebufferFetchPath == GlesFramebufferFetchPath.None &&
                                 _glesShadingRate != null && GlVram.Scale >= 8;
            if (coarseShading) _glesShadingRate!(0x96A9); // GL_SHADING_RATE_2X2_PIXELS_QCOM

            if (_glesFramebufferFetchPath != GlesFramebufferFetchPath.None)
            {
                _gl.Disable(EnableCap.Blend);
                _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_count);
            }
            else if (_gles && !_kSubtractBatch)
            {
                _gl.Enable(EnableCap.Blend);
                _gl.BlendEquation(BlendEquationModeEXT.FuncAdd);
                _gl.BlendFuncSeparate(BlendingFactor.Src1Color, BlendingFactor.OneMinusSrc1Alpha,
                    BlendingFactor.One, BlendingFactor.Zero);
                _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_count);
            }
            else if (_gles)
            {
                _gl.Enable(EnableCap.Blend);
                _gl.BlendEquationSeparate(BlendEquationModeEXT.FuncReverseSubtract, BlendEquationModeEXT.FuncAdd);
                _gl.BlendFuncSeparate(BlendingFactor.Src1Color, BlendingFactor.OneMinusSrc1Alpha,
                    BlendingFactor.One, BlendingFactor.Zero);
                _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_count);
            }
            else if (!_kTransparent)
            {
                _gl.Disable(EnableCap.Blend);
                _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_count);
            }
            else
            {
                _gl.Enable(EnableCap.Blend);
                _gl.BlendFuncSeparate(BlendingFactor.Src1Color, BlendingFactor.Src1Alpha, BlendingFactor.One, BlendingFactor.Zero);
                if (_kBlend == 2)
                {
                    _gl.BlendEquation(BlendEquationModeEXT.FuncAdd);
                    SetBlend(0f, 1f);
                    _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_count);

                    if (readsDrawTarget) Barrier();
                    _gl.BlendEquationSeparate(BlendEquationModeEXT.FuncReverseSubtract, BlendEquationModeEXT.FuncAdd);
                    SetBlend(1f, 1f);
                    _gl.Uniform4(_uBlendOpaque, 0f, 0f, 0f, 1f);
                    _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_count);
                }
                else
                {
                    _gl.BlendEquation(BlendEquationModeEXT.FuncAdd);
                    SetBlend(_kBlend switch { 0 => 0.5f, 3 => 0.25f, _ => 1f }, _kBlend == 0 ? 0.5f : 1f);
                    _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)_count);
                }
            }

            if (coarseShading) _glesShadingRate!(0x96A6); // GL_SHADING_RATE_1X1_PIXELS_QCOM
        }

        _gl.Disable(EnableCap.ScissorTest);
        if (rt != null) { rt.Dirty = true; rt.LastDrawFrame = _frame; }
        _count = 0;
    }

    bool BatchSamplesVram()
    {
        for (int i = 0; i < _count; i += 3)
            if ((_verts[i].Texpage & 0x8000) == 0)
                return true;
        return false;
    }

    void SetBlend(float src, float dst) => _gl.Uniform4(_uBlend, src, src, src, dst);

    // Prefer the driver's GLES texture-barrier extension. It resolves the PS1
    // VRAM feedback dependency without stalling the CPU after every batch.
    // Flush is a conservative fallback for GLES drivers without the extension.
    void Barrier()
    {
        if (!_gles)
            _vram.Barrier();
        else if (_glesTextureBarrier != null)
            _glesTextureBarrier();
        else
            _gl.Flush();
    }

    public void Present(in HleDispEnv disp) => PresentDisplay(disp.X, disp.Y, disp.W, disp.H, disp.Rgb24);

    public unsafe (uint tex, int w, int h, float aspect) PresentDisplay(int dispX, int dispY, int w, int h, bool rgb24 = false, int outW = 0, int outH = 0)
    {
        if (!Ready || w <= 0 || h <= 0) return (0, 0, 0, GpuHle.OutputAspect);
        _frame++;
        Flush();

        for (int i = 0; i < _rts.Length; i++)
        {
            if (_rts[i] is not { } rt) continue;
            if (_frame - rt.LastDrawFrame > 300)
            {
                // Preserve deferred display-RT contents before recycling the
                // texture. Texture feedback, VRAM copies and CPU reads already
                // write back on demand; presenting alone does not need a full
                // high-resolution copy into the monolithic VRAM texture.
                if (rt.Dirty) Writeback(rt);
                rt.Destroy(_gl);
                _rts[i] = null;
            }
        }

        GlDisplayRt? src = null;
        if (!rgb24)
            foreach (var rt in _rts)
            {
                // A dirty RT is the authoritative framebuffer even when a static
                // scene has not submitted new geometry for several frames.
                if (rt == null || (!rt.Dirty && _frame - rt.LastDrawFrame > 4)) continue;
                if (dispX < rt.X || dispY < rt.Y || dispX + w > rt.X + rt.W || dispY + h > rt.Y + rt.H) continue;
                if (src == null || rt.LastDrawFrame > src.LastDrawFrame) src = rt;
            }

        // Only show side margins while FOV expand is filling them. Otherwise present the
        // 4:3 core alone (clean black pillars) — avoids flickering stale gutter pixels.
        bool showWide = src is { Margin: > 0 } && GpuHle.WideFovActive;
        int w1x = showWide ? w + src!.Margin * 2 : w;
        int h1x = h;
        float aspect = showWide ? GpuHle.WideAspect : GpuHle.OutputAspect;

        int presentScale = GlVram.Scale;
        int fbW = w1x * presentScale;
        int fbH = h1x * presentScale;
        if (outW > 0 && outH > 0)
        {
            // Never rasterize the intermediate presentation texture above the
            // number of pixels the output surface can actually display. The
            // internal render targets remain true 1/2/4/8x, so this only removes
            // a redundant 4K pass before Android downsamples to its 1080p panel.
            int visibleW = outW;
            int visibleH = Math.Max(1, (int)MathF.Round(visibleW / aspect));
            if (visibleH > outH)
            {
                visibleH = outH;
                visibleW = Math.Max(1, (int)MathF.Round(visibleH * aspect));
            }
            if ((long)visibleW * visibleH < (long)fbW * fbH)
            {
                fbW = visibleW;
                fbH = visibleH;
            }
        }
        // Nearest when native scale, or when the player asked for crisp pixels.
        bool nearest = presentScale <= 1 || GpuHle.PresentNearest;
        EnsurePresentSize(fbW, fbH, nearest);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _presentFbo);
        _gl.Viewport(0, 0, (uint)fbW, (uint)fbH);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.ScissorTest);
        _gl.Disable(EnableCap.CullFace);

        _gl.UseProgram(rgb24 ? _progPresent24 : _progPresent);
        _gl.BindVertexArray(_presentVao);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, src?.Tex ?? _vram.Texture);
        if (rgb24)
        {
            _gl.Uniform2(_uPresent24Origin, (float)dispX, dispY);
            _gl.Uniform2(_uPresent24Size, (float)w, h);
        }
        else if (src != null)
        {
            int ox, ow, texW;
            if (showWide)
            {
                // Full wide RT including side margins (extra FOV).
                ox = 0;
                ow = src.Wide1x;
                texW = src.Wide1x;
            }
            else if (src.Margin > 0)
            {
                // Wide RT exists but WideFov off: crop to 4:3 core only.
                ox = src.Margin + dispX - src.X;
                ow = w;
                texW = src.Wide1x;
            }
            else
            {
                ox = dispX - src.X;
                ow = w;
                texW = src.W;
            }
            _gl.Uniform2(_uPresentOrigin, (float)ox, dispY - src.Y);
            _gl.Uniform2(_uPresentSize, (float)ow, h1x);
            _gl.Uniform2(_uPresentTexSize, (float)texW, src.H);
        }
        else
        {
            _gl.Uniform2(_uPresentOrigin, (float)dispX, dispY);
            _gl.Uniform2(_uPresentSize, (float)w, h);
            _gl.Uniform2(_uPresentTexSize, (float)VramShadow.Width, VramShadow.Height);
        }
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

        LastFrameFlushes = _frameFlushes;
        LastFrameWritebacks = _frameWritebacks;
        LastFrameVertices = _frameVertices;
        _frameFlushes = 0;
        _frameWritebacks = 0;
        _frameVertices = 0;

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        return (_presentTex, fbW, fbH, aspect);
    }

    unsafe void EnsurePresentSize(int w, int h, bool nearest)
    {
        if (w == _presentW && h == _presentH && nearest == _presentNearest) return;
        _gl.BindTexture(TextureTarget.Texture2D, _presentTex);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        var filter = nearest ? GLEnum.Nearest : GLEnum.Linear;
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)filter);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)filter);
        // Some GLES drivers do not refresh an attachment that was connected
        // while its texture still had no storage. Reattach after allocation.
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _presentFbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer,
            FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _presentTex, 0);
        _presentW = w; _presentH = h; _presentNearest = nearest;
    }

    /// <summary>
    /// Read the high-resolution present target into Android-compatible ARGB pixels.
    /// OpenGL's bottom-left origin is flipped here so callers can upload the result
    /// directly into a Bitmap without another allocation.
    /// </summary>
    public unsafe bool ReadPresentArgb(Span<int> destination)
    {
        var count = _presentW * _presentH;
        if (!Ready || count <= 0 || destination.Length < count) return false;
        var byteCount = count * 4;
        if (_readback.Length < byteCount) _readback = new byte[byteCount];

        // GL_FRAMEBUFFER binds both read and draw targets and is the most
        // consistently supported path on Android GLES drivers.
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _presentFbo);
        fixed (byte* raw = _readback)
            _gl.ReadPixels(0, 0, (uint)_presentW, (uint)_presentH,
                PixelFormat.Rgba, PixelType.UnsignedByte, raw);

        for (var y = 0; y < _presentH; y++)
        {
            // VRAM row zero is the top of the PS1 image but was uploaded at GL
            // row zero (the bottom), so the GL readback is already top-first
            // for Android. Flipping it here would display the game upside down.
            var sourceRow = y * _presentW * 4;
            var targetRow = y * _presentW;
            for (var x = 0; x < _presentW; x++)
            {
                var source = sourceRow + x * 4;
                destination[targetRow + x] = unchecked((int)(0xFF000000u |
                    ((uint)_readback[source] << 16) |
                    ((uint)_readback[source + 1] << 8) |
                    _readback[source + 2]));
            }
        }
        CheckError("readback");
        return true;
    }

    /// <summary>
    /// Composite the high-resolution present texture straight into the current
    /// EGL window surface. This is Android's fast path: no glReadPixels, managed
    /// pixel conversion, Bitmap allocation, or CPU-to-GPU upload.
    /// </summary>
    public void PresentToDefaultFramebuffer(int surfaceWidth, int surfaceHeight, float aspect)
    {
        if (!Ready || _presentTex == 0 || surfaceWidth <= 0 || surfaceHeight <= 0)
            return;

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)surfaceWidth, (uint)surfaceHeight);
        _gl.Disable(EnableCap.DepthTest);
        _gl.Disable(EnableCap.Blend);
        _gl.Disable(EnableCap.ScissorTest);
        _gl.Disable(EnableCap.CullFace);
        // Magenta makes the EGL surface/viewport visible while validating the
        // direct Android presentation path; the final build restores black.
        _gl.ClearColor(0f, 0f, 0f, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);

        int targetWidth;
        int targetHeight;
        int targetX;
        int targetY;
        if (GpuHle.IntegerScale && _presentW > 0 && _presentH > 0)
        {
            int scale = (int)MathF.Floor(MathF.Min(
                surfaceWidth / (float)_presentW, surfaceHeight / (float)_presentH));
            if (scale >= 1)
            {
                targetWidth = _presentW * scale;
                targetHeight = _presentH * scale;
                targetX = (surfaceWidth - targetWidth) / 2;
                targetY = (surfaceHeight - targetHeight) / 2;
                goto blit;
            }
        }

        targetWidth = surfaceWidth;
        targetHeight = Math.Max(1, (int)MathF.Round(targetWidth / aspect));
        if (targetHeight > surfaceHeight)
        {
            targetHeight = surfaceHeight;
            targetWidth = Math.Max(1, (int)MathF.Round(targetHeight * aspect));
        }
        targetX = (surfaceWidth - targetWidth) / 2;
        targetY = (surfaceHeight - targetHeight) / 2;

        blit:
        _gl.Viewport(targetX, targetY, (uint)targetWidth, (uint)targetHeight);

        _gl.UseProgram(_progPresent);
        _gl.BindVertexArray(_presentVao);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _presentTex);
        // The PS1's top row lives at GL texture row zero. Flip only during this
        // final GPU pass so the Android surface receives conventional top-down video.
        _gl.Uniform2(_uPresentOrigin, 0f, (float)_presentH);
        _gl.Uniform2(_uPresentSize, (float)_presentW, (float)-_presentH);
        _gl.Uniform2(_uPresentTexSize, (float)_presentW, (float)_presentH);
        _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
    }

    void CheckError(string stage)
    {
        var error = _gl.GetError();
        if (error != GLEnum.NoError)
            LastDiagnostic = $"{stage}: {error}";
    }

    public void Dispose()
    {
        foreach (var rt in _rts) rt?.Destroy(_gl);
        _vram.Dispose();
        if (_vbo != 0) _gl.DeleteBuffer(_vbo);
        if (_presentVbo != 0) _gl.DeleteBuffer(_presentVbo);
        if (_vao != 0) _gl.DeleteVertexArray(_vao);
        if (_presentVao != 0) _gl.DeleteVertexArray(_presentVao);
        if (_progPrim != 0) _gl.DeleteProgram(_progPrim);
        if (_progPrimFast != 0) _gl.DeleteProgram(_progPrimFast);
        if (_progPresent != 0) _gl.DeleteProgram(_progPresent);
        if (_progPresent24 != 0) _gl.DeleteProgram(_progPresent24);
        if (_presentTex != 0) _gl.DeleteTexture(_presentTex);
        if (_presentFbo != 0) _gl.DeleteFramebuffer(_presentFbo);
    }
}
