using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    /// <summary>DoctC <c>Witch_Doctor_On_Willy</c> (invincibility, third mask).</summary>
    const uint StateDoctorOnWilly = 2u;
    /// <summary>c1 gool.c GoolTransform(in, trans, rot, scale, out); out is the 5th (stack) arg.</summary>
    const uint GoolTransformAddr = 0x8002465Cu;
    /// <summary>svtx_frame: tgeo eid at +4, x/y/z at +8, vertices (6 bytes each) at +0x38.</summary>
    const uint SvtxFrameVertsOff = 0x38u;

    /// <summary>
    /// #226 invincibility mask sinks into Crash above 30 FPS. On_Willy trans
    /// SETs the mask to Crash's mesh vertex 0 (<c>getvert(vtrans, collider, 0.0)</c>)
    /// and copies his heading (<c>roty = collider-&gt;roty; rotz = roty</c>).
    /// DoctC is gated, so that pose is taken once per 30 Hz step and the skip
    /// presents lerp between the last two steps, while Crash is drawn at his
    /// real-dt pose: the mask trails him by up to two steps and ends up inside
    /// him. Display only: rebuild the getvert pose from Crash's current state
    /// with the game's own GoolTransform (same math as GOOL op 6).
    /// Returns false when the pose can't be read; the normal lerp runs then.
    /// </summary>
    static bool ApplyDoctorMaskPose(CpuContext c, IMemory m, uint obj)
    {
        try
        {
            if (!TryReadGoolClass(m, obj, out uint type, out _) || type != GoolTypeDoct) return false;
            if (m.ReadU32(obj + ObjStateOff) != StateDoctorOnWilly) return false;
            uint crash = m.ReadU32(obj + ObjColliderOff);
            if ((crash & 0xFF000000u) != 0x80000000u) return false;
            if (!TrySvtxEntry(c, m, crash, out uint en, out _)) return false;
            if (m.ReadU32(en + 8) != SvtxEntryType) return false;
            int items = (int)m.ReadU32(en + 12);
            int cur = (int)m.ReadU32(crash + ObjAnimFrameOff) >> 8;
            if ((uint)cur >= (uint)items) return false;
            uint frame = EntryItem(m, en, cur);
            uint tgeo = ResolveSvtxEntry(m, m.ReadU32(frame + 4u));
            if (tgeo == 0) return false;
            uint header = EntryItem(m, tgeo, 0);

            int vx = (int)m.ReadU32(frame + 8u) + (sbyte)(m.ReadU8(frame + SvtxFrameVertsOff) - 0x80);
            int vy = (int)m.ReadU32(frame + 12u) + (sbyte)(m.ReadU8(frame + SvtxFrameVertsOff + 1u) - 0x80);
            int vz = (int)m.ReadU32(frame + 16u) + (sbyte)(m.ReadU8(frame + SvtxFrameVertsOff + 2u) - 0x80);
            int sx = (int)(((long)(int)m.ReadU32(header + 4u) * (int)m.ReadU32(crash + ObjScaleOff)) >> 12);
            int sy = (int)(((long)(int)m.ReadU32(header + 8u) * (int)m.ReadU32(crash + ObjScaleOff + 4)) >> 12);
            int sz = (int)(((long)(int)m.ReadU32(header + 12u) * (int)m.ReadU32(crash + ObjScaleOff + 8)) >> 12);

            if (!GoolTransformGuest(c, m, vx << 10, vy << 10, vz << 10,
                    crash + ObjTransOff, crash + ObjRotOff, sx, sy, sz,
                    out int px, out int py, out int pz))
                return false;

            uint heading = m.ReadU32(crash + ObjHeadingOff);
            _dispRotObj = obj;
            _dispSaveX = (int)m.ReadU32(obj + ObjRotOff);
            _dispSaveY = (int)m.ReadU32(obj + ObjRotOff + 4);
            _dispSaveZ = (int)m.ReadU32(obj + ObjRotOff + 8);
            m.WriteU32(obj + ObjRotOff + 4, heading);
            m.WriteU32(obj + ObjRotOff + 8, heading);
            _dispRotApplied = true;
            _dispSavePx = (int)m.ReadU32(obj + ObjTransOff);
            _dispSavePy = (int)m.ReadU32(obj + ObjTransOff + 4);
            _dispSavePz = (int)m.ReadU32(obj + ObjTransOff + 8);
            m.WriteU32(obj + ObjTransOff, (uint)px);
            m.WriteU32(obj + ObjTransOff + 4, (uint)py);
            m.WriteU32(obj + ObjTransOff + 8, (uint)pz);
            _dispTransApplied = true;
            return true;
        }
        catch
        {
            RestoreGatedDisplayRot(m, obj);
            return false;
        }
    }

    /// <summary>
    /// Guest GoolTransform on scratch below SP. Clobbers GTE like the getvert
    /// op does in the mask's own trans, right before its Transform.
    /// </summary>
    static bool GoolTransformGuest(CpuContext c, IMemory m, int ix, int iy, int iz,
        uint transPtr, uint rotPtr, int sx, int sy, int sz, out int ox, out int oy, out int oz)
    {
        ox = oy = oz = 0;
        var saved = c.Snapshot();
        try
        {
            uint sp = (c.SP - 0x80u) & ~7u;
            uint inV = sp + 0x20u;
            uint scaleV = sp + 0x30u;
            uint outV = sp + 0x40u;
            m.WriteU32(inV, (uint)ix);
            m.WriteU32(inV + 4, (uint)iy);
            m.WriteU32(inV + 8, (uint)iz);
            m.WriteU32(scaleV, (uint)sx);
            m.WriteU32(scaleV + 4, (uint)sy);
            m.WriteU32(scaleV + 8, (uint)sz);
            m.WriteU32(sp + 0x10u, outV);
            c.SP = sp;
            c.A0 = inV;
            c.A1 = transPtr;
            c.A2 = rotPtr;
            c.A3 = scaleV;
            Dispatcher.Call(c, m, GoolTransformAddr);
            ox = (int)m.ReadU32(outV);
            oy = (int)m.ReadU32(outV + 4);
            oz = (int)m.ReadU32(outV + 8);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            c.Restore(saved);
        }
    }
}
