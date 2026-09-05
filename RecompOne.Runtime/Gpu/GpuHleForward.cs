using RecompOne.Runtime.Hle;

namespace RecompOne.Runtime;

public sealed partial class Gpu
{
    static bool HleOn => GpuHle.Active && GpuHle.Backend is { Ready: true };

    public int DrawOffsetX => _drawOffsetX;
    public int DrawOffsetY => _drawOffsetY;
    public HleDrawEnv CurrentHleDrawEnv => CurEnv();

    int CurTPage() => ((_texPageX / 64) & 0xf) | (((_texPageY / 256) & 1) << 4)
                    | ((_blendMode & 3) << 5) | ((_texDepth & 3) << 7);

    HleDrawEnv CurEnv() => new()
    {
        ClipX0 = _drawAreaLeft, ClipY0 = _drawAreaTop, ClipX1 = _drawAreaRight, ClipY1 = _drawAreaBottom,
        TwMaskX = _texWinMaskX, TwMaskY = _texWinMaskY, TwOffX = _texWinOffX, TwOffY = _texWinOffY,
        SetMask = _setMask, CheckMask = _checkMask, Dither = _dither,
    };

    static HleVertex HV(in Vert v, bool useSubpixel, bool useZ) => new()
    {
        X = useSubpixel ? v.Fx : v.X,
        Y = useSubpixel ? v.Fy : v.Y,
        Z = useZ ? v.GteZ : 0f,
        HasGteZ = useZ,
        R = (byte)v.R, G = (byte)v.G, B = (byte)v.B, U = (short)v.U, V = (short)v.V,
    };

    PrimFlags PrimOf(bool tex, bool semi, bool raw, int clut, bool gouraud = false) => new()
    {
        Textured = tex, SemiTrans = semi, RawTexture = raw, Gouraud = gouraud,
        TPage = (ushort)CurTPage(), Clut = (ushort)clut,
        WideMode = GpuHle.NativeWideRendererActive && GpuHle.CurrentPrimitiveKind == GpuHle.PrimitiveKind.World
            ? WidePrimitiveMode.CoreOnly
            : WidePrimitiveMode.Default,
    };

    void HleTri(in Vert a, in Vert b, in Vert c, bool tex, bool gouraud, bool semi, bool raw, int clut)
    {
        int spanX = Math.Max(a.X, Math.Max(b.X, c.X)) - Math.Min(a.X, Math.Min(b.X, c.X));
        int spanY = Math.Max(a.Y, Math.Max(b.Y, c.Y)) - Math.Min(a.Y, Math.Min(b.Y, c.Y));
        if (spanX > 1023 || spanY > 511) return;

        // All-or-nothing: mixed int/float verts warp UVs every frame (texture flicker).
        bool sub = a.Subpixel && b.Subpixel && c.Subpixel;
        bool depth = GpuHle.NativeWideRendererActive && a.HasGteZ && b.HasGteZ && c.HasGteZ;
        var flags = PrimOf(tex, semi, raw, clut, gouraud);
        var be = GpuHle.Backend!;
        be.SetDrawEnv(CurEnv());
        if (flags.WideMode == WidePrimitiveMode.Default && depth)
        {
            // The original centre relies on the PS1 ordering table, including
            // authored exceptions to geometric depth. Preserve that ordering.
            flags.WideMode = WidePrimitiveMode.CoreOnly;
            be.DrawTri(HV(a, sub, false), HV(b, sub, false), HV(c, sub, false), flags);
            flags.WideMode = WidePrimitiveMode.DepthTest;
            be.DrawTri(HV(a, sub, true), HV(b, sub, true), HV(c, sub, true), flags);
        }
        else
            be.DrawTri(HV(a, sub, false), HV(b, sub, false), HV(c, sub, false), flags);
    }

    void HleRect(int x, int y, int w, int h, int u, int v, int clut, int r, int g, int b, bool tex, bool semi, bool raw)
    {
        var be = GpuHle.Backend!;
        be.SetDrawEnv(CurEnv());
        be.DrawRect(new HleRect { X = x, Y = y, W = w, H = h, U = (short)u, V = (short)v, R = (byte)r, G = (byte)g, B = (byte)b },
            PrimOf(tex, semi, raw, clut));
    }

    void HleLine(int x0, int y0, int r0, int g0, int b0, int x1, int y1, int r1, int g1, int b1, bool semi, bool gouraud)
    {
        if (Math.Abs(x1 - x0) > 1023 || Math.Abs(y1 - y0) > 511) return;

        var be = GpuHle.Backend!;
        be.SetDrawEnv(CurEnv());
        be.DrawLine(
            new HleVertex { X = x0, Y = y0, R = (byte)r0, G = (byte)g0, B = (byte)b0 },
            new HleVertex { X = x1, Y = y1, R = (byte)r1, G = (byte)g1, B = (byte)b1 },
            PrimOf(false, semi, false, 0, gouraud));
    }

    void HleFill(int x, int y, int w, int h, ushort color) => GpuHle.Backend!.FillRect(x, y, w, h, color);
    void HleCopy(int sx, int sy, int dx, int dy, int w, int h) => GpuHle.Backend!.CopyVram(sx, sy, dx, dy, w, h);

    ushort[] _readBuf = Array.Empty<ushort>();

    void HleReadback(int x, int y, int w, int h)
    {
        int n = w * h;
        if (_readBuf.Length < n) _readBuf = new ushort[n];
        GpuHle.Backend!.ReadVram(x, y, w, h, _readBuf);
    }

    void CopySoftRectToReadBuf(int x, int y, int w, int h)
    {
        int n = w * h;
        if (_readBuf.Length < n) _readBuf = new ushort[n];
        for (int i = 0; i < n; i++)
        {
            int px = (x + (i % w)) & (VramWidth - 1);
            int py = (y + (i / w)) & (VramHeight - 1);
            _readBuf[i] = Vram[py * VramWidth + px];
        }
    }

    // img load buffer (HLE upload and/or VramTransferEvent hook)
    ushort[] _hleLoad = Array.Empty<ushort>();
    bool _hleLoadActive;
    int _hleLoadPos;

    void HleLoadBegin()
    {
        // Buffer when HLE/GL is on, or when VRAM Load intercept is needed
        // (mod listeners, catalog discovery, or PNG texture replacements).
        _hleLoadActive = HleOn || Events.VramTransfers.NeedsLoadIntercept;
        if (!_hleLoadActive) return;
        int n = _loadW * _loadH;
        if (_hleLoad.Length < n) _hleLoad = new ushort[n];
        _hleLoadPos = 0;
    }

    void HleLoadPut(ushort value)
    {
        if (_hleLoadActive && _hleLoadPos < _hleLoad.Length) _hleLoad[_hleLoadPos++] = value;
    }

    void HleLoadFlush()
    {
        if (!_hleLoadActive) return;
        int n = _loadW * _loadH;
        var pixels = _hleLoad;
        Events.VramTransfers.NotifyLoad(_loadX, _loadY, _loadW, _loadH, pixels, n);

        var span = pixels.AsSpan(0, n);
        if (HleOn)
            GpuHle.Backend!.WriteVram(_loadX, _loadY, _loadW, _loadH, span);
        else
            WriteSoftLoadRect(span);

        _hleLoadActive = false;
    }

    void WriteSoftLoadRect(ReadOnlySpan<ushort> px)
    {
        for (int i = 0; i < px.Length; i++)
        {
            int x = (_loadX + (i % _loadW)) & (VramWidth - 1);
            int y = (_loadY + (i / _loadW)) & (VramHeight - 1);
            int idx = y * VramWidth + x;
            if (_checkMask && (Vram[idx] & 0x8000) != 0) continue;
            Vram[idx] = px[i];
        }
    }
}
