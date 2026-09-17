using RecompOne.Runtime.Context;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    /// <summary>
    /// GfxCalcObjectMatrices (NTSC-U). Projects the object with <c>ms_rot</c>
    /// and culls when the 2D midpoint is outside ±316, or the AABB outside
    /// ±256. Those limits are the 4:3 frame; native 16:9 keeps the same
    /// projection and only widens X.
    /// </summary>
    const uint NativeWideMsRotAddr = 0x80057844u;
    const uint NativeWideMnTransAddr = 0x8005726Cu;
    const uint NativeWideCamTransPrevAddr = 0x80057888u;
    const int NativeWideMatTransOff = 20;
    const int NativeWideFrustumCore = 256;
    const int NativeWideFrustumMidPad = 60;
    const int NativeWideFrustumMidY = 188;
    const int NativeWideFrustumAabbY = 108;

    static bool _wideFrustumPatched;
    static uint _wideFrustumObj;
    static uint _wideFrustumSaved;

    /// <summary>
    /// Before GfxTransformSvtx/Cvtx: if the object is in front of the near
    /// plane and inside the 16:9 X bounds but outside the 4:3 test, set
    /// status_b 0x40000 so GfxCalcObjectMatrices does not drop it. HUD,
    /// near-plane, and vertical cull stay on the original path.
    /// </summary>
    static void WidenNativeWideObjectFrustum(CpuContext c, IMemory m)
    {
        uint obj = c.A2;
        if ((obj & 0xFF000000u) != 0x80000000u)
            obj = _obj;
        WidenNativeWideObjectFrustumFor(m, obj);
    }

    static void WidenNativeWideObjectFrustumFor(IMemory m, uint obj)
    {
        if (!GpuHle.WideFovActive) return;
        if ((obj & 0xFF000000u) != 0x80000000u) return;
        try
        {
            if ((m.ReadU32(DisplayFlagsAddr) & FlagSpinDeath) != 0) return;
            if (IsNativeWideHudObject(m, obj, out _)) return;
            uint statusB = m.ReadU32(obj + ObjStatusBOff);
            if ((statusB & FlagSkipFrustum) != 0) return;
            if (!NativeWideObjectNeedsFrustumStretch(m, obj, statusB)) return;
            _wideFrustumSaved = statusB;
            _wideFrustumObj = obj;
            _wideFrustumPatched = true;
            m.WriteU32(obj + ObjStatusBOff, statusB | FlagSkipFrustum);
        }
        catch
        {
            RestoreNativeWideObjectFrustum(m);
        }
    }

    static void RestoreNativeWideObjectFrustum(IMemory? m)
    {
        if (!_wideFrustumPatched) return;
        uint obj = _wideFrustumObj;
        uint saved = _wideFrustumSaved;
        _wideFrustumPatched = false;
        _wideFrustumObj = 0;
        if (m == null || (obj & 0xFF000000u) != 0x80000000u) return;
        try
        {
            uint cur = m.ReadU32(obj + ObjStatusBOff);
            m.WriteU32(obj + ObjStatusBOff, (cur & ~FlagSkipFrustum) | (saved & FlagSkipFrustum));
        }
        catch
        {
            // object or overlay gone
        }
    }

    /// <summary>
    /// True when the original 4:3 test would hide an object that still
    /// overlaps the 16:9 frame. Near clip and the 4:3 Y test are unchanged.
    /// </summary>
    static bool NativeWideObjectNeedsFrustumStretch(IMemory m, uint obj, uint statusB)
    {
        if (!TryNativeWideCameraProject(m, obj, out int sx, out int sy, out int sz, out int proj))
            return false;
        if (sz <= proj) return false;
        int wideHalf = NativeWideFrustumCore + GpuHle.WideMargin(NativeWideFrustumCore * 2);
        int wideMid = wideHalf + NativeWideFrustumMidPad;
        if ((statusB & FlagScreenAreaCull) != 0)
        {
            if (!TryNativeWideProjectBound(m, obj, proj, out int minX, out int maxX, out int minY, out int maxY))
                return false;
            if (NativeWideAabbHidden(minX, maxX, wideHalf) || NativeWideAabbHidden(minY, maxY, NativeWideFrustumAabbY))
                return false;
            return NativeWideAabbHidden(minX, maxX, NativeWideFrustumCore);
        }

        if (sy > NativeWideFrustumMidY) return false;
        int ax = sx < 0 ? -sx : sx;
        if (ax > wideMid) return false;
        return ax > NativeWideFrustumCore + NativeWideFrustumMidPad;
    }

    static bool NativeWideAabbHidden(int a, int b, int limit) =>
        (a < -limit && b < -limit) || (a > limit && b > limit);

    static bool TryNativeWideCameraProject(IMemory m, uint obj,
        out int sx, out int sy, out int sz, out int proj)
    {
        sx = 0;
        sy = 0;
        sz = 0;
        proj = (int)m.ReadU32(NativeWideProjectionAddr);
        if (proj is <= 0 or > 4096) return false;
        Span<short> rot = stackalloc short[9];
        NativeWideReadMsRot(m, rot);
        int tx = (int)m.ReadU32(NativeWideMnTransAddr + NativeWideMatTransOff);
        int ty = (int)m.ReadU32(NativeWideMnTransAddr + NativeWideMatTransOff + 4);
        int tz = (int)m.ReadU32(NativeWideMnTransAddr + NativeWideMatTransOff + 8);
        int camx = (int)m.ReadU32(NativeWideCamTransPrevAddr);
        int camy = (int)m.ReadU32(NativeWideCamTransPrevAddr + 4);
        int camz = (int)m.ReadU32(NativeWideCamTransPrevAddr + 8);
        int wx = (int)m.ReadU32(obj + ObjTransOff);
        int wy = (int)m.ReadU32(obj + ObjTransOff + 4);
        int wz = (int)m.ReadU32(obj + ObjTransOff + 8);
        return NativeWideRotTrans(rot, tx, ty, tz, wx, wy, wz, camx, camy, camz, proj,
            out sx, out sy, out sz);
    }

    static bool TryNativeWideProjectBound(IMemory m, uint obj, int proj,
        out int minX, out int maxX, out int minY, out int maxY)
    {
        minX = int.MaxValue;
        maxX = int.MinValue;
        minY = int.MaxValue;
        maxY = int.MinValue;
        Span<short> rot = stackalloc short[9];
        NativeWideReadMsRot(m, rot);
        int tx = (int)m.ReadU32(NativeWideMnTransAddr + NativeWideMatTransOff);
        int ty = (int)m.ReadU32(NativeWideMnTransAddr + NativeWideMatTransOff + 4);
        int tz = (int)m.ReadU32(NativeWideMnTransAddr + NativeWideMatTransOff + 8);
        int camx = (int)m.ReadU32(NativeWideCamTransPrevAddr);
        int camy = (int)m.ReadU32(NativeWideCamTransPrevAddr + 4);
        int camz = (int)m.ReadU32(NativeWideCamTransPrevAddr + 8);
        int ox = (int)m.ReadU32(obj + ObjTransOff);
        int oy = (int)m.ReadU32(obj + ObjTransOff + 4);
        int oz = (int)m.ReadU32(obj + ObjTransOff + 8);
        int x1 = ox + (int)m.ReadU32(obj + ObjBoundOff);
        int y1 = oy + (int)m.ReadU32(obj + ObjBoundOff + 4);
        int z1 = oz + (int)m.ReadU32(obj + ObjBoundOff + 8);
        int x2 = ox + (int)m.ReadU32(obj + ObjBoundOff + 12);
        int y2 = oy + (int)m.ReadU32(obj + ObjBoundOff + 16);
        int z2 = oz + (int)m.ReadU32(obj + ObjBoundOff + 20);
        int hits = 0;
        for (int i = 0; i < 8; i++)
        {
            int wx = (i & 1) == 0 ? x1 : x2;
            int wy = (i & 2) == 0 ? y1 : y2;
            int wz = (i & 4) == 0 ? z1 : z2;
            if (!NativeWideRotTrans(rot, tx, ty, tz, wx, wy, wz, camx, camy, camz, proj,
                    out int sx, out int sy, out int sz)
                || sz <= 0)
                continue;
            hits++;
            if (sx < minX) minX = sx;
            if (sx > maxX) maxX = sx;
            if (sy < minY) minY = sy;
            if (sy > maxY) maxY = sy;
        }
        return hits > 0;
    }

    static void NativeWideReadMsRot(IMemory m, Span<short> rot)
    {
        for (int i = 0; i < 9; i++)
            rot[i] = (short)m.ReadU16(NativeWideMsRotAddr + (uint)i * 2);
    }

    static bool NativeWideRotTrans(ReadOnlySpan<short> rot,
        int tx, int ty, int tz,
        int wx, int wy, int wz,
        int camx, int camy, int camz,
        int proj,
        out int sx, out int sy, out int sz)
    {
        sx = 0;
        sy = 0;
        int ux = (wx - camx) >> 8;
        int uy = (wy - camy) >> 8;
        int uz = (wz - camz) >> 8;
        int rx = (int)(((long)rot[0] * ux + (long)rot[1] * uy + (long)rot[2] * uz) >> 12) + tx;
        int ry = (int)(((long)rot[3] * ux + (long)rot[4] * uy + (long)rot[5] * uz) >> 12) + ty;
        sz = (int)(((long)rot[6] * ux + (long)rot[7] * uy + (long)rot[8] * uz) >> 12) + tz;
        if (sz == 0) return false;
        sx = (int)((long)proj * rx / sz);
        sy = (int)((long)proj * ry / sz);
        return true;
    }
}
