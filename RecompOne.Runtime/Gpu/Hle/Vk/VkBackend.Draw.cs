using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace RecompOne.Runtime.Hle.Vulkan;

// Primitive submission and VRAM traffic. Mirrors GlBackend line by line where
// the logic is API independent (classification, batching, scissor bands).
public sealed unsafe partial class VkBackend
{
    const int FbSlackW = 64;
    const int FbSlackH = 32;

    public void SetDrawEnv(in HleDrawEnv env)
    {
        if (env.ClipX0 == _env.ClipX0 && env.ClipY0 == _env.ClipY0
            && env.ClipX1 == _env.ClipX1 && env.ClipY1 == _env.ClipY1
            && env.TwMaskX == _env.TwMaskX && env.TwMaskY == _env.TwMaskY
            && env.TwOffX == _env.TwOffX && env.TwOffY == _env.TwOffY
            && env.SetMask == _env.SetMask && env.CheckMask == _env.CheckMask
            && env.Dither == _env.Dither)
            return;
        _env = env;
        _classifyValid = false;
    }

    VkDisplayRt? ClassifyCached()
    {
        if (_classifyValid) return _classifiedRt;
        _classifiedRt = Classify();
        _classifyValid = true;
        return _classifiedRt;
    }

    public void BeginWideDepth(uint? clearColor)
    {
        Flush();
        _classifyValid = false;
        var rt = Classify();
        if (rt == null || rt.Margin <= 0) return;
        rt.HasWideWorld = GpuHle.NativeWideRendererActive
            || GpuHle.DrawEnvClearsBackground
            || clearColor.HasValue;
        BeginPass(rt);
        int s = GlVram.Scale;
        if (clearColor is uint color)
        {
            float r = (color & 255) / 255f, g = ((color >> 8) & 255) / 255f, b = ((color >> 16) & 255) / 255f;
            ClearInPass(0, 0, rt.Margin * s, rt.TexH, rt.TexW, rt.TexH, true, r, g, b, 0f, false);
            ClearInPass((rt.Margin + rt.W) * s, 0, rt.Margin * s, rt.TexH, rt.TexW, rt.TexH, true, r, g, b, 0f, false);
        }
        ClearInPass(0, 0, rt.TexW, rt.TexH, rt.TexW, rt.TexH, false, 0, 0, 0, 0, true);
    }

    public void FillWideBands(int y, int h, uint color)
    {
        Flush();
        var rt = Classify();
        if (rt == null || rt.Margin <= 0) return;
        int top = Math.Clamp(y - rt.Y, 0, rt.H), bottom = Math.Clamp(y + h - rt.Y, 0, rt.H);
        if (bottom <= top) return;
        int s = GlVram.Scale;
        float r = (color & 255) / 255f, g = ((color >> 8) & 255) / 255f, b = ((color >> 16) & 255) / 255f;
        BeginPass(rt);
        ClearInPass(0, top * s, rt.Margin * s, (bottom - top) * s, rt.TexW, rt.TexH, true, r, g, b, 0f, false);
        ClearInPass((rt.Margin + rt.W) * s, top * s, rt.Margin * s, (bottom - top) * s, rt.TexW, rt.TexH, true, r, g, b, 0f, false);
    }

    VkDisplayRt? Classify()
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

    VkDisplayRt GetOrCreateRt(int fbX, int fbY, int fbW, int fbH)
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
            if (_count > 0 && ReferenceEquals(_kTarget, old))
                Flush();
            if (old.Dirty) Writeback(old);
            DestroyRt(old);
        }

        var fresh = new VkDisplayRt { X = fbX, Y = fbY, W = fbW, H = fbH, Margin = GpuHle.WideMargin(fbW), Stamp = ++_rtStamp, LastDrawFrame = _frame };
        CreateRt(fresh);
        _rts[slot] = fresh;
        SyncRtFromVram(fresh, fbX, fbY, fbW, fbH);
        return fresh;
    }

    void CreateRt(VkDisplayRt rt)
    {
        rt.Color = VkTex.Create(_gpu, rt.TexW, rt.TexH, Format.R8G8B8A8Unorm,
            ImageUsageFlags.SampledBit | ImageUsageFlags.ColorAttachmentBit |
            ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit);
        var depthAspect = _gpu.DepthFormat is Format.D24UnormS8Uint
            ? ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit
            : ImageAspectFlags.DepthBit;
        rt.Depth = VkTex.Create(_gpu, rt.TexW, rt.TexH, _gpu.DepthFormat,
            ImageUsageFlags.DepthStencilAttachmentBit, depthAspect);
        rt.Framebuffer = CreateFramebuffer(_rtPass, rt.Color.View, rt.Depth.View, rt.TexW, rt.TexH);

        // GL clears a new RT to transparent black and depth 1.
        EndPass();
        Barrier(rt.Depth, ImageLayout.DepthStencilAttachmentOptimal);
        BeginPass(rt);
        ClearInPass(0, 0, rt.TexW, rt.TexH, rt.TexW, rt.TexH, true, 0, 0, 0, 0, true);
    }

    void DestroyRt(VkDisplayRt rt)
    {
        if (_passRt == rt) EndPass();
        if (ReferenceEquals(_kTarget, rt)) _kTarget = null;
        ForgetSets(rt.Color);
        Defer(() => DestroyRtNow(rt));
    }

    void DestroyRtNow(VkDisplayRt rt)
    {
        if (rt.Framebuffer.Handle != 0) _vk.DestroyFramebuffer(_dev, rt.Framebuffer, null);
        rt.Framebuffer = default;
        rt.Color?.Destroy(_gpu);
        rt.Depth?.Destroy(_gpu);
    }

    // RT pixels (core only) back into VRAM.
    void Writeback(VkDisplayRt rt)
    {
        _frameWritebacks++;
        int s = GlVram.Scale;
        EndPass();
        Barrier(rt.Color, ImageLayout.TransferSrcOptimal, _vram, ImageLayout.TransferDstOptimal);
        var region = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            SrcOffset = new Offset3D(rt.Margin * s, 0, 0),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstOffset = new Offset3D(rt.X * s, rt.Y * s, 0),
            Extent = ClampExtent(rt.X * s, rt.Y * s, rt.W * s, rt.H * s, _vram),
        };
        if (region.Extent.Width > 0 && region.Extent.Height > 0)
            _vk.CmdCopyImage(Cb, rt.Color.Image, ImageLayout.TransferSrcOptimal, _vram.Image, ImageLayout.TransferDstOptimal, 1, &region);
        MarkVramDirty(rt.X, rt.Y, rt.W, rt.H);
        rt.Dirty = false;
    }

    static Extent3D ClampExtent(int x, int y, int w, int h, VkTex dst)
        => new((uint)Math.Max(0, Math.Min(w, dst.W - x)), (uint)Math.Max(0, Math.Min(h, dst.H - y)), 1);

    void SyncRtFromVram(VkDisplayRt rt, int rx, int ry, int rw, int rh)
    {
        int x0 = Math.Max(rx, rt.X), y0 = Math.Max(ry, rt.Y);
        int x1 = Math.Min(rx + rw, rt.X + rt.W), y1 = Math.Min(ry + rh, rt.Y + rt.H);
        x1 = Math.Min(x1, VramShadow.Width); y1 = Math.Min(y1, VramShadow.Height);
        if (x0 >= x1 || y0 >= y1) return;
        int s = GlVram.Scale;
        EndPass();
        Barrier(_vram, ImageLayout.TransferSrcOptimal, rt.Color, ImageLayout.TransferDstOptimal);
        var region = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            SrcOffset = new Offset3D(x0 * s, y0 * s, 0),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstOffset = new Offset3D((x0 - rt.X + rt.Margin) * s, (y0 - rt.Y) * s, 0),
            Extent = new Extent3D((uint)((x1 - x0) * s), (uint)((y1 - y0) * s), 1),
        };
        _vk.CmdCopyImage(Cb, _vram.Image, ImageLayout.TransferSrcOptimal, rt.Color.Image, ImageLayout.TransferDstOptimal, 1, &region);
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

    // ------------------------------------------------------------- VRAM copy

    void MarkVramDirty(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        int x1 = Math.Min(VramShadow.Width - 1, x + w - 1), y1 = Math.Min(VramShadow.Height - 1, y + h - 1);
        x = Math.Max(0, x); y = Math.Max(0, y);
        if (x > x1 || y > y1) return;
        if (_readDirtyX1 < _readDirtyX0)
        {
            _readDirtyX0 = x; _readDirtyY0 = y; _readDirtyX1 = x1; _readDirtyY1 = y1;
            return;
        }
        _readDirtyX0 = Math.Min(_readDirtyX0, x); _readDirtyY0 = Math.Min(_readDirtyY0, y);
        _readDirtyX1 = Math.Max(_readDirtyX1, x1); _readDirtyY1 = Math.Max(_readDirtyY1, y1);
    }

    void EnsureVramRead()
    {
        if (_vramRead != null) return;
        _vramRead = VkTex.Create(_gpu, _vram.W, _vram.H, Format.R8G8B8A8Unorm,
            ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit,
            ImageAspectFlags.ColorBit);
        _readDirtyX0 = 0; _readDirtyY0 = 0;
        _readDirtyX1 = VramShadow.Width - 1; _readDirtyY1 = VramShadow.Height - 1;
        SyncVramRead();
    }

    /// <summary>Copy the dirty part of VRAM into the read copy that VRAM-target draws sample.</summary>
    void SyncVramRead()
    {
        EnsureVramRead();
        if (_readDirtyX1 < _readDirtyX0) return;
        int s = GlVram.Scale;
        EndPass();
        Barrier(_vram, ImageLayout.TransferSrcOptimal, _vramRead!, ImageLayout.TransferDstOptimal);
        var region = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            SrcOffset = new Offset3D(_readDirtyX0 * s, _readDirtyY0 * s, 0),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstOffset = new Offset3D(_readDirtyX0 * s, _readDirtyY0 * s, 0),
            Extent = new Extent3D((uint)((_readDirtyX1 - _readDirtyX0 + 1) * s), (uint)((_readDirtyY1 - _readDirtyY0 + 1) * s), 1),
        };
        _vk.CmdCopyImage(Cb, _vram.Image, ImageLayout.TransferSrcOptimal, _vramRead!.Image, ImageLayout.TransferDstOptimal, 1, &region);
        _readDirtyX0 = 0; _readDirtyY0 = 0; _readDirtyX1 = -1; _readDirtyY1 = -1;
    }

    // Snapshot of the target for mask-bit tests (GL samples the target with a texture barrier).
    VkTex SnapshotRt(VkDisplayRt rt)
    {
        if (_rtRead == null || _rtRead.W != rt.TexW || _rtRead.H != rt.TexH)
        {
            if (_rtRead != null)
            {
                var old = _rtRead;
                ForgetSets(old);
                Defer(() => old.Destroy(_gpu));
            }
            _rtRead = VkTex.Create(_gpu, rt.TexW, rt.TexH, Format.R8G8B8A8Unorm,
                ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit);
        }
        EndPass();
        Barrier(rt.Color, ImageLayout.TransferSrcOptimal, _rtRead, ImageLayout.TransferDstOptimal);
        var region = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            Extent = new Extent3D((uint)rt.TexW, (uint)rt.TexH, 1),
        };
        _vk.CmdCopyImage(Cb, rt.Color.Image, ImageLayout.TransferSrcOptimal, _rtRead.Image, ImageLayout.TransferDstOptimal, 1, &region);
        return _rtRead;
    }

    // ------------------------------------------------------------ primitives

    bool DesiredMatches(bool transparent, int blend)
    {
        int twAndX = ~(_env.TwMaskX * 8) & 0xFF, twAndY = ~(_env.TwMaskY * 8) & 0xFF;
        int twOrX = (_env.TwOffX & _env.TwMaskX) * 8, twOrY = (_env.TwOffY & _env.TwMaskY) * 8;
        return _kSubtractBatch == (transparent && blend == 2)
            && _kSetMask == (_env.SetMask ? 1 : 0) && _kCheckMask == (_env.CheckMask ? 1 : 0)
            && _kTwAndX == twAndX && _kTwAndY == twAndY && _kTwOrX == twOrX && _kTwOrY == twOrY
            && _kClipX0 == _env.ClipX0 && _kClipY0 == _env.ClipY0 && _kClipX1 == _env.ClipX1 && _kClipY1 == _env.ClipY1
            && _kWideMode == _pendingWideMode;
    }

    void Begin(in PrimFlags f, int vertsNeeded)
    {
        bool transparent = f.SemiTrans;
        int blend = f.BlendMode;
        var target = ClassifyCached();
        _pendingWideMode = f.WideMode;
        if (_count > 0 && (target != _kTarget || !DesiredMatches(transparent, blend))) Flush();
        if (_count + vertsNeeded > MaxVerts) Flush();
        if (!IsNativeWidePass(f.WideMode))
            CheckTextureFeedback(f);

        _kTarget = target;
        _kSubtractBatch = transparent && blend == 2;
        _kTransparent = transparent; _kBlend = blend;
        _batchHasSemi |= transparent;
        _kWideMode = f.WideMode;
        _kSetMask = _env.SetMask ? 1 : 0; _kCheckMask = _env.CheckMask ? 1 : 0;
        _kTwAndX = ~(_env.TwMaskX * 8) & 0xFF; _kTwAndY = ~(_env.TwMaskY * 8) & 0xFF;
        _kTwOrX = (_env.TwOffX & _env.TwMaskX) * 8; _kTwOrY = (_env.TwOffY & _env.TwMaskY) * 8;
        _kClipX0 = _env.ClipX0; _kClipY0 = _env.ClipY0; _kClipX1 = _env.ClipX1; _kClipY1 = _env.ClipY1;
    }

    bool DitherOf(in PrimFlags f) => _env.Dither && (f.Gouraud || (f.Textured && !f.RawTexture));

    static VkVertex V(in HleVertex v, in PrimFlags f, bool dither)
    {
        uint color = (f.Textured && f.RawTexture) ? 0x808080u : (uint)(v.R | (v.G << 8) | (v.B << 16));
        int tpage = f.Textured ? (f.TPage & 0x1FF) : 0x8000;
        if (dither) tpage |= 0x400;
        if (f.SemiTrans) tpage |= 0x1000;
        tpage |= (f.BlendMode & 3) << 13;
        float invZ = v.HasGteZ && v.Z > 0f ? 1f / v.Z : 0f;
        return new VkVertex
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

    // ----------------------------------------------------------- VRAM access

    public void FillRect(int x, int y, int w, int h, ushort color15)
    {
        Flush();
        float r = (color15 & 0x1F) / 31f, g = ((color15 >> 5) & 0x1F) / 31f, b = ((color15 >> 10) & 0x1F) / 31f;
        float a = (color15 & 0x8000) != 0 ? 1f : 0f;
        int s = GlVram.Scale;
        BeginPass(null);
        ClearInPass(x * s, y * s, w * s, h * s, _vram.W, _vram.H, true, r, g, b, a, false);
        MarkVramDirty(x, y, w, h);
        foreach (var rt in _rts)
        {
            if (rt == null || !rt.Intersects(x, y, w, h)) continue;
            if (rt.Covers(x, y, x + w - 1, y + h - 1))
            {
                BeginPass(rt);
                ClearInPass(0, 0, rt.TexW, rt.TexH, rt.TexW, rt.TexH, true, r, g, b, a, false);
                rt.Dirty = false;
                rt.LastDrawFrame = _frame;
            }
            else SyncRtFromVram(rt, x, y, w, h);
        }
    }

    public void CopyVram(int sx, int sy, int dx, int dy, int w, int h)
    {
        Flush();
        WritebackDirtyIntersecting(sx, sy, w, h);
        int s = GlVram.Scale;
        var extent = ClampExtent(sx * s, sy * s, w * s, h * s, _vram);
        extent = ClampExtent(dx * s, dy * s, (int)extent.Width, (int)extent.Height, _vram);
        if (extent.Width == 0 || extent.Height == 0 || sx < 0 || sy < 0 || dx < 0 || dy < 0) return;
        var region = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            SrcOffset = new Offset3D(sx * s, sy * s, 0),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstOffset = new Offset3D(dx * s, dy * s, 0),
            Extent = extent,
        };
        EndPass();
        bool overlap = sx < dx + w && dx < sx + w && sy < dy + h && dy < sy + h;
        if (!overlap)
        {
            // Same image: both ends in GENERAL.
            Barrier(_vram, ImageLayout.General);
            _vk.CmdCopyImage(Cb, _vram.Image, ImageLayout.General, _vram.Image, ImageLayout.General, 1, &region);
        }
        else
        {
            var scratch = EnsureScratch((int)extent.Width, (int)extent.Height);
            Barrier(_vram, ImageLayout.TransferSrcOptimal, scratch, ImageLayout.TransferDstOptimal);
            var toScratch = region;
            toScratch.DstOffset = new Offset3D(0, 0, 0);
            _vk.CmdCopyImage(Cb, _vram.Image, ImageLayout.TransferSrcOptimal, scratch.Image, ImageLayout.TransferDstOptimal, 1, &toScratch);
            Barrier(scratch, ImageLayout.TransferSrcOptimal, _vram, ImageLayout.TransferDstOptimal);
            var fromScratch = region;
            fromScratch.SrcOffset = new Offset3D(0, 0, 0);
            _vk.CmdCopyImage(Cb, scratch.Image, ImageLayout.TransferSrcOptimal, _vram.Image, ImageLayout.TransferDstOptimal, 1, &fromScratch);
        }
        MarkVramDirty(dx, dy, w, h);
        SyncRtsFromVram(dx, dy, w, h);
    }

    VkTex EnsureScratch(int w, int h)
    {
        if (_scratch != null && _scratch.W >= w && _scratch.H >= h) return _scratch;
        if (_scratch != null)
        {
            var old = _scratch;
            Defer(() => old.Destroy(_gpu));
            w = Math.Max(w, old.W); h = Math.Max(h, old.H);
        }
        _scratch = VkTex.Create(_gpu, w, h, Format.R8G8B8A8Unorm,
            ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit);
        return _scratch;
    }

    public void WriteVram(int x, int y, int w, int h, ReadOnlySpan<ushort> px)
    {
        Flush();
        int srcW = w;
        if (x < 0 || y < 0 || w <= 0 || h <= 0 || px.Length < w * h) return;
        w = Math.Min(w, VramShadow.Width - x); h = Math.Min(h, VramShadow.Height - y);
        if (w <= 0 || h <= 0) return;

        int count = w * h;
        var (buffer, offset, ptr) = Alloc((ulong)count * 4, 16);
        uint* dst = (uint*)ptr;
        for (int row = 0; row < h; row++)
        for (int col = 0; col < w; col++)
        {
            ushort p = px[row * srcW + col];
            uint r = (uint)((p & 0x1f) * 255 / 31);
            uint g = (uint)(((p >> 5) & 0x1f) * 255 / 31);
            uint b = (uint)(((p >> 10) & 0x1f) * 255 / 31);
            uint a = (p & 0x8000) != 0 ? 255u : 0u;
            dst[row * w + col] = r | (g << 8) | (b << 16) | (a << 24);
        }

        EndPass();
        int s = GlVram.Scale;
        var copy = new BufferImageCopy
        {
            BufferOffset = offset,
            BufferRowLength = (uint)w,
            BufferImageHeight = (uint)h,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = new Offset3D(x, y, 0),
            ImageExtent = new Extent3D((uint)w, (uint)h, 1),
        };
        if (s == 1)
        {
            Barrier(_vram, ImageLayout.TransferDstOptimal);
            _vk.CmdCopyBufferToImage(Cb, buffer, _vram.Image, ImageLayout.TransferDstOptimal, 1, &copy);
        }
        else
        {
            Barrier(_stage, ImageLayout.TransferDstOptimal);
            _vk.CmdCopyBufferToImage(Cb, buffer, _stage.Image, ImageLayout.TransferDstOptimal, 1, &copy);
            Barrier(_stage, ImageLayout.TransferSrcOptimal, _vram, ImageLayout.TransferDstOptimal);
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            };
            blit.SrcOffsets[0] = new Offset3D(x, y, 0);
            blit.SrcOffsets[1] = new Offset3D(x + w, y + h, 1);
            blit.DstOffsets[0] = new Offset3D(x * s, y * s, 0);
            blit.DstOffsets[1] = new Offset3D((x + w) * s, (y + h) * s, 1);
            _vk.CmdBlitImage(Cb, _stage.Image, ImageLayout.TransferSrcOptimal, _vram.Image, ImageLayout.TransferDstOptimal, 1, &blit, Filter.Nearest);
        }
        MarkVramDirty(x, y, w, h);
        SyncRtsFromVram(x, y, w, h);
    }

    public void ReadVram(int x, int y, int w, int h, Span<ushort> px)
    {
        Flush();
        x = Math.Max(0, x); y = Math.Max(0, y);
        int cw = Math.Min(w, VramShadow.Width - x), ch = Math.Min(h, VramShadow.Height - y);
        if (cw <= 0 || ch <= 0) return;
        WritebackDirtyIntersecting(x, y, cw, ch);
        _frameCpuReads++;
        int s = GlVram.Scale;
        _readback ??= VkChunk.Create(_gpu, (ulong)VramShadow.Width * VramShadow.Height * 4,
            BufferUsageFlags.TransferDstBit, readback: true);

        EndPass();
        VkTex source = _vram;
        if (s != 1)
        {
            Barrier(_vram, ImageLayout.TransferSrcOptimal, _stage, ImageLayout.TransferDstOptimal);
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            };
            blit.SrcOffsets[0] = new Offset3D(x * s, y * s, 0);
            blit.SrcOffsets[1] = new Offset3D((x + cw) * s, (y + ch) * s, 1);
            blit.DstOffsets[0] = new Offset3D(x, y, 0);
            blit.DstOffsets[1] = new Offset3D(x + cw, y + ch, 1);
            _vk.CmdBlitImage(Cb, _vram.Image, ImageLayout.TransferSrcOptimal, _stage.Image, ImageLayout.TransferDstOptimal, 1, &blit, Filter.Nearest);
            source = _stage;
        }
        Barrier(source, ImageLayout.TransferSrcOptimal);
        var copy = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = (uint)cw,
            BufferImageHeight = (uint)ch,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = new Offset3D(x, y, 0),
            ImageExtent = new Extent3D((uint)cw, (uint)ch, 1),
        };
        _vk.CmdCopyImageToBuffer(Cb, source.Image, ImageLayout.TransferSrcOptimal, _readback.Buffer, 1, &copy);
        // Make the transfer visible to the host before the fence is waited.
        var barrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.HostReadBit,
        };
        _vk.CmdPipelineBarrier(Cb, PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit, 0, 1, &barrier, 0, null, 0, null);
        SubmitAndWait();
        _readback.Invalidate(_gpu);

        // Round to nearest: WriteVram stores 5-bit v as v*255/31 (floored).
        byte* src = _readback.Ptr;
        for (int row = 0; row < ch; row++)
        for (int col = 0; col < cw; col++)
        {
            int o = (row * cw + col) * 4;
            px[row * w + col] = (ushort)(((src[o] * 31 + 127) / 255) |
                (((src[o + 1] * 31 + 127) / 255) << 5) |
                (((src[o + 2] * 31 + 127) / 255) << 10) |
                (src[o + 3] >= 128 ? 0x8000 : 0));
        }
    }

    // ---------------------------------------------------------------- flush

    readonly struct ScissorBand
    {
        public readonly int X0, Y0, X1, Y1;
        public ScissorBand(int x0, int y0, int x1, int y1)
        {
            X0 = x0; Y0 = y0; X1 = x1; Y1 = y1;
        }
        public bool Empty => X1 < X0 || Y1 < Y0;
    }

    static bool NeedsSplitScissor(WidePrimitiveMode mode) => mode is
        WidePrimitiveMode.OverlaySides or
        WidePrimitiveMode.DepthTest;

    static bool IsNativeWideSideDraw(WidePrimitiveMode mode) => mode is
        WidePrimitiveMode.WorldSides or
        WidePrimitiveMode.BackdropSides or
        WidePrimitiveMode.OverlaySides or
        WidePrimitiveMode.WorldExtensionSides or
        WidePrimitiveMode.WorldUserSides or
        WidePrimitiveMode.DepthTest;

    static bool IsNativeWidePass(WidePrimitiveMode mode) =>
        IsNativeWideSideDraw(mode) || mode == WidePrimitiveMode.WorldCore;

    int BuildScissorBands(VkDisplayRt? rt, Span<ScissorBand> bands)
    {
        if (rt == null)
        {
            bands[0] = new ScissorBand(_kClipX0, _kClipY0, _kClipX1, _kClipY1);
            return 1;
        }

        int cx0 = _kClipX0 - rt.X + rt.Margin, cy0 = _kClipY0 - rt.Y;
        int cx1 = _kClipX1 - rt.X + rt.Margin, cy1 = _kClipY1 - rt.Y;
        if (_kWideMode is WidePrimitiveMode.CoreOnly or WidePrimitiveMode.WorldCore
                or WidePrimitiveMode.CoreDepthTest && rt.Margin > 0)
        {
            cx0 = rt.Margin;
            cx1 = rt.Margin + rt.W - 1;
        }
        else if (rt.Margin > 0 && _kClipX0 <= rt.X && _kClipX1 >= rt.X + rt.W - 1)
        {
            cx0 = 0;
            cx1 = rt.Wide1x - 1;
        }

        if (rt.Margin > 0 && NeedsSplitScissor(_kWideMode))
        {
            int n = 0;
            int left1 = Math.Min(cx1, rt.Margin - 1);
            if (cx0 <= left1)
                bands[n++] = new ScissorBand(cx0, cy0, left1, cy1);
            int right0 = Math.Max(cx0, rt.Margin + rt.W);
            if (right0 <= cx1)
                bands[n++] = new ScissorBand(right0, cy0, cx1, cy1);
            return n;
        }

        bands[0] = new ScissorBand(cx0, cy0, cx1, cy1);
        return 1;
    }

    void DrawScissored(ReadOnlySpan<ScissorBand> bands, int bandCount, int scale, int targetW, int targetH)
    {
        for (int i = 0; i < bandCount; i++)
        {
            var b = bands[i];
            if (b.Empty) continue;
            int x0 = Math.Clamp(b.X0 * scale, 0, targetW), y0 = Math.Clamp(b.Y0 * scale, 0, targetH);
            int x1 = Math.Clamp((b.X1 + 1) * scale, 0, targetW), y1 = Math.Clamp((b.Y1 + 1) * scale, 0, targetH);
            if (x1 <= x0 || y1 <= y0) continue;
            var scissor = new Rect2D(new Offset2D(x0, y0), new Extent2D((uint)(x1 - x0), (uint)(y1 - y0)));
            _vk.CmdSetScissor(Cb, 0, 1, &scissor);
            _vk.CmdDraw(Cb, (uint)_count, 1, 0, 0);
        }
    }

    bool BatchSamplesVram()
    {
        for (int i = 0; i < _count; i += 3)
            if ((_verts[i].Texpage & 0x8000) == 0)
                return true;
        return false;
    }

    public void Flush()
    {
        if (_count == 0) return;
        _frameFlushes++;
        _frameVertices += _count;

        // Vertices first: Alloc may split a very long frame into two submissions.
        ulong bytes = (ulong)(_count * sizeof(VkVertex));
        var (buffer, offset, ptr) = Alloc(bytes, 16);
        fixed (VkVertex* src = _verts)
            System.Buffer.MemoryCopy(src, (void*)ptr, bytes, bytes);

        var rt = _kTarget;
        bool checkMask = _kCheckMask != 0;
        DescriptorSet set;
        if (rt == null)
        {
            if (checkMask || BatchSamplesVram()) SyncVramRead();
            else EnsureVramRead();
            set = SetFor(_vramRead!, _vramRead!);
        }
        else if (checkMask)
            set = SetFor(_vram, SnapshotRt(rt));
        else
            set = SetFor(_vram, _vram);

        BeginPass(rt);
        int targetW = rt?.TexW ?? _vram.W, targetH = rt?.TexH ?? _vram.H;
        var cb = Cb;

        bool extension = _kWideMode == WidePrimitiveMode.WorldExtensionSides;
        bool user = _kWideMode == WidePrimitiveMode.WorldUserSides;
        bool worldPass = _kWideMode is WidePrimitiveMode.WorldSides or WidePrimitiveMode.WorldCore;
        bool depthTest = rt != null && _kWideMode is WidePrimitiveMode.WorldSides or WidePrimitiveMode.WorldCore
            or WidePrimitiveMode.WorldExtensionSides or WidePrimitiveMode.WorldUserSides or WidePrimitiveMode.DepthTest
            or WidePrimitiveMode.CoreDepthTest;
        bool depthWrite = worldPass || extension || user
            || !_kTransparent && _kWideMode != WidePrimitiveMode.CoreDepthTest;
        int depthLess = !depthTest ? 0 : depthWrite ? 1 : 2;

        var viewport = new Viewport(0, 0, targetW, targetH, 0f, 1f);
        _vk.CmdSetViewport(cb, 0, 1, &viewport);
        if (rt != null)
        {
            // Extensions yield to coplanar retail surfaces, Wide Editor
            // triangles cover them (see GlBackend.Flush).
            float bias = extension ? 1f : user ? -1f : 0f;
            _vk.CmdSetDepthBias(cb, bias, 0f, bias);
        }
        var vb = buffer;
        _vk.CmdBindVertexBuffers(cb, 0, 1, &vb, &offset);
        _vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _primLayout, 0, 1, &set, 0, null);

        var push = new PrimPush
        {
            TwAndX = _kTwAndX, TwAndY = _kTwAndY, TwOrX = _kTwOrX, TwOrY = _kTwOrY,
            SetMask = _kSetMask == 1 ? 1f : 0f,
            CheckMask = _kCheckMask,
            FilterMode = Mobile && GlVram.Scale >= 8 ? 0 : GpuHle.EffectiveTextureFilter,
            FilterStrength = GpuHle.TextureFilterStrength,
            Dedither = GpuHle.DeditherActive ? 1 : 0,
            ScaledDither = GpuHle.ScaledDither ? 1 : 0,
            Scale = GlVram.Scale,
        };
        if (rt != null)
        {
            push.PosBiasX = rt.Margin - rt.X; push.PosBiasY = -rt.Y;
            push.FbInvX = 2f / rt.Wide1x; push.FbInvY = 2f / rt.H;
            push.WideMode = rt.Margin > 0 && IsNativeWideSideDraw(_kWideMode) && !NeedsSplitScissor(_kWideMode) ? 1 : 0;
            push.WideCoreX = rt.Margin; push.WideCoreY = rt.Margin + rt.W;
        }
        else
        {
            push.FbInvX = 2f / VramShadow.Width; push.FbInvY = 2f / VramShadow.Height;
        }

        int s = GlVram.Scale;
        Span<ScissorBand> bands = stackalloc ScissorBand[2];
        int bandCount = BuildScissorBands(rt, bands);

        if (_kSubtractBatch)
        {
            // Opaque texels first, then dst - src for the semi-transparent ones.
            push.BlendPass = 1;
            BindPrim(cb, rt, depthLess, BlendAdd);
            _vk.CmdPushConstants(cb, _primLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(PrimPush), &push);
            DrawScissored(bands, bandCount, s, targetW, targetH);

            push.BlendPass = 2;
            // The world pass wrote depth in the first draw: the second must pass at equal depth.
            BindPrim(cb, rt, depthTest ? depthLess + 2 : 0, BlendRevSub);
            _vk.CmdPushConstants(cb, _primLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(PrimPush), &push);
            DrawScissored(bands, bandCount, s, targetW, targetH);
        }
        else
        {
            push.BlendPass = 0;
            BindPrim(cb, rt, depthLess, _batchHasSemi ? BlendAdd : BlendOff);
            _vk.CmdPushConstants(cb, _primLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(PrimPush), &push);
            DrawScissored(bands, bandCount, s, targetW, targetH);
        }

        if (rt != null) { rt.Dirty = true; rt.LastDrawFrame = _frame; }
        else MarkVramDirty(_kClipX0, _kClipY0, _kClipX1 - _kClipX0 + 1, _kClipY1 - _kClipY0 + 1);
        _count = 0;
        _batchHasSemi = false;
    }

    void BindPrim(CommandBuffer cb, VkDisplayRt? rt, int depthState, int blend)
    {
        var pipeline = rt != null ? _rtPipes[depthState * 3 + blend] : _vramPipes[blend];
        _vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
    }
}
