using System.Diagnostics;
using System.Reflection;
using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    /// <summary>
    /// Real wall dt every display. Standing platforms need Euler dt/34
    /// (SOLID_TOP). Everything else — lizards, turtles, boxes, unknown
    /// GOOL — one original 30 Hz step, same skip at 60 and uncapped.
    /// RuiOC is cat 0x600 with SOLID_TOP but is not Euler: gate it.
    /// Torch flames are the same exe with <c>do playanim while 1</c>. Sprite
    /// anims OR FLAG_2D. RuiOC is gated before this. DispC HUD still Euler.
    /// World FLAG_2D (PoRoC mist) must not count as HUD or <c>scalex +=</c>
    /// Euler fills the pit (Death_Fall cine never reaches the fade wait).
    /// </summary>
    static bool KeepRealDt(IMemory m, uint obj)
    {
        if ((obj & 0xFF000000u) != 0x80000000u) return true;
        try
        {
            TryReadGoolClass(m, obj, out uint type, out uint cat);
            // Lost City's later LizaC uses a long authored vertex animation
            // for its left/right jump. Keep its GOOL on the original 30 Hz
            // wall-time gate; ClampAnimFrame exempts this exact class so the
            // sequence can advance beyond frame 32 instead of restarting.
            if (IsLizaEntity(m, obj, type))
                return false;
            if (IsGatedTempleSolid(m, obj, type)) return false;
            if (IsHud(m, obj)) return true;
            uint b = m.ReadU32(obj + ObjStatusBOff);
            if ((b & FlagSolidTop) != 0
                && (IsPlatformGoolType(type) || cat == GoolCategoryPlatform))
            {
                _pathHoppers.Remove(obj);
                return true;
            }
            if (IsPathHopper(m, obj)) return false;
            if (HasSolidPhysics(m, obj) || IsJunocButterfly(m, obj)) return false;
            if ((b & FlagTrackPathRot) != 0) return true;
            if (type == GoolTypeJunO && !IsJunocButterfly(m, obj)) return true;
            if (type == 3u) return true; // FruiC
            return false;
        }
        catch
        {
            return false;
        }
    }

    static bool IsPlatformGoolType(uint type) =>
        type is 11 or 26 or 28 or 33 or 46 or 58;

    /// <summary>
    /// RuiOC always — meshes, spears, and 2D torch flames (<c>playanim</c>
    /// loops). RWaOC wall mill too (<c>time()</c> on the array, children
    /// <c>vectransf2</c> + CarryCollider). Seesaws in that exe stay Euler.
    /// Temple / Jaws every PoPlC (0.985). Other lids: Active and
    /// Auto (path after Wait, and <c>time()</c>). Drop plats too: CODE
    /// playframe 0↔1 picks <c>spd(y, 2m)</c> vs <c>-0.5m</c>, so Euler
    /// flips the bob every present (Generator Room freeze on step). Wait /
    /// Spawn stay Euler so Bound can arm the 0.8 s start. First ride is
    /// Active; after death the disc is already gated.
    /// </summary>
    static bool IsGatedTempleSolid(IMemory m, uint obj, uint type)
    {
        if (type == GoolTypeRuiO) return true;
        if (IsGatedRwaocMover(m, obj, type)) return true;
        if (type != GoolTypePoPl) return false;
        try
        {
            uint lid = m.ReadU32(Catalog.LevelIdAddr);
            if (lid == LidTempleRuins || lid == LidJawsOfDarkness) return true;
            uint state = m.ReadU32(obj + ObjStateOff);
            // Path Wait/Spawn Euler so Bound arms the 0.8 s start.
            // Drop (1–4) playframe 0↔1 must not Euler: spd(y) sign follows
            // that frame (Generator Room / Cortex Power freeze on step).
            return state != StatePoPlSpawn && state != StatePoPlWait;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// RWaOC wall mill (0–3) is RuiOC orbit. Slide/wiggle/pusher (10–18)
    /// CODE <c>playframes</c> the mesh in and out of the wall — Euler
    /// finishes that in one present. Seesaw (4–5) and sensitive bob (6)
    /// stay Euler; iguana (7–9) stays on the enemy wall-time gate.
    /// </summary>
    static bool IsGatedRwaocMover(IMemory m, uint obj, uint type)
    {
        if (type != GoolTypeRWaO) return false;
        try
        {
            uint state = m.ReadU32(obj + ObjStateOff);
            if (IsLostCityPusher(m, obj, type)) return false;
            if (state is >= StateRwaOrbitArray and <= StateRwaSensitiveBob)
                return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    static bool IsLostCityPusher(IMemory m, uint obj, uint type) =>
        type == GoolTypeRWaO && m.ReadU32(Catalog.LevelIdAddr) == LidLostCity
        && m.ReadU32(obj + ObjStateOff) is >= StateRwaPusherSpawn and <= StateRwaPusherLast;

    static bool HasSolidPhysics(IMemory m, uint obj)
    {
        if ((obj & 0xFF000000u) != 0x80000000u) return false;
        try
        {
            TryReadGoolClass(m, obj, out uint type, out uint cat);
            if (type == 2) return true;
            // Crates: stack link lives in the velocity union. 300 Hz trans
            // collides them and GOOL_EVENT_BOX_STACK_BREAK eats the pile.
            if (type == GoolTypeBox) return true;
            // Ripper Roo BIG TNT: pathprog += 0.07 and troty = randi(±10deg)
            // in trans (draw_count time()). Not TRANS_MOTION, not BoxsC.
            if (type == GoolTypeRooO)
            {
                uint rb = m.ReadU32(obj + ObjStatusBOff);
                if ((rb & Flag2D) == 0) return true;
            }
            // Enemies (0x300): one original 30 Hz GOOL update per 34 wall ticks.
            if (cat == GoolCategoryEnemy) return true;
            uint b = m.ReadU32(obj + ObjStatusBOff);
            if ((b & FlagTransMotion) == 0) return false;
            if ((b & FlagStoppedBySolid) != 0) return true;
            return (b & FlagGravity) != 0 && (b & FlagCollidable) != 0;
        }
        catch
        {
            return false;
        }
    }

    static bool HasPatrolPath(IMemory m, uint obj)
    {
        try
        {
            return m.ReadU32(obj + ObjPathLenOff) >= 0x200u;
        }
        catch
        {
            return false;
        }
    }

    static bool IsLizaWaitFlags(uint b) =>
        (b & (FlagCollidable | FlagSolidGround | FlagSolidSides | FlagSolidTop))
            == (FlagCollidable | FlagSolidGround | FlagSolidSides);

    static bool IsLizaEntity(IMemory m, uint obj, uint type)
    {
        if (type == GoolTypeLiza) return true;
        uint entity = m.ReadU32(obj + ObjEntityOff);
        return (entity & 0xFF000000u) == 0x80000000u
            && m.ReadU8(entity + EntityTypeOff) == GoolTypeLiza;
    }

    /// <summary>
    /// Per-CODE hop (i+=1 / lerp / loopseek), any level. Jump may OR
    /// SOLID_TOP — sticky so it cannot become a pillar. Boxes are never hoppers.
    /// RuiOC stays gated even with SOLID_TOP (orbit <c>vectransf2</c>).
    /// RWaOC wall mill too. Temple / Jaws every PoPlC. Other lids' Auto / Drop / Active
    /// as well. Wait / Spawn on those lids stay Euler. Spawn CODE writes
    /// SOLID_TOP after the first Pre; drop the sticky bit so those path
    /// plats can Euler. Lizards stay sticky.
    /// </summary>
    static bool IsPathHopper(IMemory m, uint obj)
    {
        if ((obj & 0xFF000000u) != 0x80000000u) return false;
        try
        {
            TryReadGoolClass(m, obj, out uint type, out uint cat);
            if (type == GoolTypeBox)
            {
                _pathHoppers.Remove(obj);
                return false;
            }
            if (IsLizaEntity(m, obj, type))
            {
                _pathHoppers.Add(obj);
                return true;
            }
            if (IsGatedTempleSolid(m, obj, type))
            {
                _pathHoppers.Add(obj);
                return true;
            }
            uint b = m.ReadU32(obj + ObjStatusBOff);
            if ((b & FlagSolidTop) != 0
                && (IsPlatformGoolType(type) || cat == GoolCategoryPlatform))
            {
                _pathHoppers.Remove(obj);
                return false;
            }
            if (_pathHoppers.Contains(obj)) return true;
            bool hop = IsLizaWaitFlags(b);
            if (!hop && HasPatrolPath(m, obj) && (b & FlagSolidTop) == 0)
                hop = true;
            if (!hop && cat == GoolCategoryEnemy
                && (b & FlagSolidSides) != 0 && (b & FlagSolidTop) == 0)
                hop = true;
            if (hop) _pathHoppers.Add(obj);
            return hop;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// JunOC butterfly trans is per display frame, not per tick: pathprog
    /// seeks 0.02 and <c>x += velx</c> (rand impulse, no spd). At unlocked
    /// refresh that finishes the patrol path and they sit in Pose. Gate like
    /// enemies so one original step runs per 34 wall ticks at any fps.
    /// Rollers in the same executable use spd() — leave them on real dt.
    /// </summary>
    static bool IsJunocButterfly(IMemory m, uint obj)
    {
        if ((obj & 0xFF000000u) != 0x80000000u) return false;
        try
        {
            if (!TryReadGoolClass(m, obj, out uint type, out _) || type != GoolTypeJunO)
                return false;
            uint state = m.ReadU32(obj + ObjStateOff);
            return state == StateButterflyFly || state == StateButterflyPose;
        }
        catch
        {
            return false;
        }
    }

    static bool IsPlatformObj(IMemory m, uint obj)
    {
        if ((obj & 0xFF000000u) != 0x80000000u) return false;
        try
        {
            if (IsHud(m, obj)) return false;
            if (IsPathHopper(m, obj)) return false;
            if (!TryReadGoolClass(m, obj, out uint type, out uint cat))
                return false;
            if (IsGatedTempleSolid(m, obj, type)) return false;
            if (!IsPlatformGoolType(type) && cat != GoolCategoryPlatform)
                return false;
            uint b = m.ReadU32(obj + ObjStatusBOff);
            // SOLID_SIDES alone is an enemy hitbox (LizaC Wait). Pillars have a top.
            if ((b & FlagSolidTop) == 0) return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    static bool IsPacedPlatformChild(IMemory m, uint obj)
    {
        try
        {
            uint parent = m.ReadU32(obj + ObjParentOff);
            if (parent == 0 || parent == obj) return false;
            return IsPlatformObj(m, parent);
        }
        catch
        {
            return false;
        }
    }

    static bool IsPinsC(IMemory m, uint obj)
    {
        try
        {
            return TryReadGoolClass(m, obj, out uint type, out _) && type == GoolTypePins;
        }
        catch
        {
            return false;
        }
    }

    static bool TryReadGoolClass(IMemory m, uint obj, out uint type, out uint cat)
    {
        if (TryReadGoolClassFrom(m, m.ReadU32(obj + ObjGlobalOff), out type, out cat))
            return true;
        return TryReadGoolClassFrom(m, m.ReadU32(obj + ObjExternalOff), out type, out cat);
    }

    static bool TryReadGoolClassFrom(IMemory m, uint en, out uint type, out uint cat)
    {
        type = 0;
        cat = 0;
        if ((en & 0xFF000000u) != 0x80000000u) return false;
        uint magic = m.ReadU32(en);
        uint item0 = m.ReadU32(en + 16);
        uint header;
        if ((item0 & 0xFF000000u) == 0x80000000u)
            header = item0;
        else if (magic == EntryMagic)
            header = en + item0;
        else
            return false;
        if ((header & 0xFF000000u) != 0x80000000u) return false;
        type = m.ReadU32(header);
        cat = m.ReadU32(header + 4);
        if (type > 63)
        {
            uint shifted = type >> 8;
            if ((type & 0xFF) == 0 && shifted > 0 && shifted <= 63)
                type = shifted;
            else
            {
                type = 0;
                cat = 0;
                return false;
            }
        }
        if (cat > 0 && cat < 16)
            cat <<= 8;
        return true;
    }

    static void DrawGatedObject(CpuContext c, IMemory m, uint obj)
    {
        try
        {
            uint seq = m.ReadU32(obj + ObjAnimSeqOff);
            if ((seq & 0xFF000000u) != 0x80000000u) return;
            uint statusB = m.ReadU32(obj + ObjStatusBOff);
            if ((statusB & FlagInvisible) != 0) return;
            if (!_gateRot.ContainsKey(obj))
                CaptureGateRot(m, obj);
            RegisterGatedBound(m, obj, statusB);
            uint a0 = c.A0;
            c.A0 = obj;
            Dispatcher.Call(c, m, GoolObjectTransformAddr);
            c.A0 = a0;
        }
        catch
        {
            RestoreGatedDisplayRot(m, obj);
        }
    }

    /// <summary>
    /// Drop one stale id. Clear() wiped ride poses in busy rooms — first
    /// mount had no from→to lerp; after death the dict was smaller and worked.
    /// </summary>
    static void EvictDict<T>(Dictionary<uint, T> d, uint keep)
    {
        if (d.Count < 96) return;
        uint drop = 0;
        foreach (var k in d.Keys)
        {
            if (k == keep) continue;
            drop = k;
            break;
        }
        if (drop != 0)
            d.Remove(drop);
    }

    /// <summary>
    /// Poses before/after the last 30 Hz GOOL step. Draw lerps from→to so
    /// skip frames and the GOOL frame share one crate, not a ghost pair.
    /// </summary>
    static void CaptureGateRot(IMemory m, uint obj)
    {
        try
        {
            if (_gateRot.Count > 96)
                EvictDict(_gateRot, obj);
            _gateRot[obj] = new GatePose
            {
                Fx = _orx,
                Fy = _ory,
                Fz = _orz,
                Tx = (int)m.ReadU32(obj + ObjRotOff),
                Ty = (int)m.ReadU32(obj + ObjRotOff + 4),
                Tz = (int)m.ReadU32(obj + ObjRotOff + 8),
                Px = _ox,
                Py = _oy,
                Pz = _oz,
                Qx = (int)m.ReadU32(obj + ObjTransOff),
                Qy = (int)m.ReadU32(obj + ObjTransOff + 4),
                Qz = (int)m.ReadU32(obj + ObjTransOff + 8),
                FromAnim = _oanim,
                ToAnim = (int)m.ReadU32(obj + ObjAnimFrameOff)
            };
        }
        catch
        {
            // object freed
        }
    }

    /// <summary>
    /// Display-only. Logic pose stays at the GOOL result; restore after Transform.
    /// t = wall ticks since that step / 34. Ripper Roo TNT yaw/path is this
    /// rot+trans lerp; BoxsC TNT is a single CVTX frame (no mesh ping-pong).
    /// </summary>
    static void ApplyGatedDisplayPose(IMemory m, uint obj)
    {
        if (_dispRotApplied || _dispTransApplied || _svtxPatched)
            RestoreGatedDisplayRot(m, _dispRotObj);
        if (obj != _obj) return;
        if (!_solidObj) return;
        if (GamePaused(m)) return;
        if (_exactTicks >= RefTicks - 0.01) return;

        if (!_simAcc.TryGetValue(obj, out double acc)) return;
        double t = acc / RefTicks;
        int fx, fy, fz, tx, ty, tz;
        int px, py, pz, qx, qy, qz;
        if (_gatedSolid)
        {
            if (!_gateRot.TryGetValue(obj, out GatePose g)) return;
            fx = g.Fx; fy = g.Fy; fz = g.Fz;
            tx = g.Tx; ty = g.Ty; tz = g.Tz;
            px = g.Px; py = g.Py; pz = g.Pz;
            qx = g.Qx; qy = g.Qy; qz = g.Qz;
        }
        else
        {
            fx = _orx; fy = _ory; fz = _orz;
            px = _ox; py = _oy; pz = _oz;
            try
            {
                tx = (int)m.ReadU32(obj + ObjRotOff);
                ty = (int)m.ReadU32(obj + ObjRotOff + 4);
                tz = (int)m.ReadU32(obj + ObjRotOff + 8);
                qx = (int)m.ReadU32(obj + ObjTransOff);
                qy = (int)m.ReadU32(obj + ObjTransOff + 4);
                qz = (int)m.ReadU32(obj + ObjTransOff + 8);
            }
            catch
            {
                return;
            }
        }
        if (t < 0) t = 0;
        if (t > 1) t = 1;
        try
        {
            _dispRotObj = obj;
            _dispSaveX = (int)m.ReadU32(obj + ObjRotOff);
            _dispSaveY = (int)m.ReadU32(obj + ObjRotOff + 4);
            _dispSaveZ = (int)m.ReadU32(obj + ObjRotOff + 8);
            m.WriteU32(obj + ObjRotOff, (uint)LerpAng(fx, tx, t));
            m.WriteU32(obj + ObjRotOff + 4, (uint)LerpAng(fy, ty, t));
            m.WriteU32(obj + ObjRotOff + 8, (uint)LerpAng(fz, tz, t));
            _dispRotApplied = true;
            _dispSavePx = (int)m.ReadU32(obj + ObjTransOff);
            _dispSavePy = (int)m.ReadU32(obj + ObjTransOff + 4);
            _dispSavePz = (int)m.ReadU32(obj + ObjTransOff + 8);
            m.WriteU32(obj + ObjTransOff, (uint)LerpPos(px, qx, t));
            m.WriteU32(obj + ObjTransOff + 4, (uint)LerpPos(py, qy, t));
            m.WriteU32(obj + ObjTransOff + 8, (uint)LerpPos(pz, qz, t));
            _dispTransApplied = true;
        }
        catch
        {
            RestoreGatedDisplayRot(m, obj);
        }
    }

    static void RestoreGatedDisplayRot(IMemory m, uint obj)
    {
        RestoreSvtx(m);
        if (obj != _dispRotObj) return;
        try
        {
            if (_dispRotApplied)
            {
                m.WriteU32(obj + ObjRotOff, (uint)_dispSaveX);
                m.WriteU32(obj + ObjRotOff + 4, (uint)_dispSaveY);
                m.WriteU32(obj + ObjRotOff + 8, (uint)_dispSaveZ);
            }
            if (_dispTransApplied)
            {
                m.WriteU32(obj + ObjTransOff, (uint)_dispSavePx);
                m.WriteU32(obj + ObjTransOff + 4, (uint)_dispSavePy);
                m.WriteU32(obj + ObjTransOff + 8, (uint)_dispSavePz);
            }
        }
        catch
        {
            // object freed
        }
        _dispRotApplied = false;
        _dispTransApplied = false;
    }

    static uint EntryItem(IMemory m, uint en, int idx)
    {
        uint off = m.ReadU32(en + 16u + (uint)idx * 4u);
        if ((off & 0xFF000000u) == 0x80000000u) return off;
        return en + off;
    }

    /// <summary>
    /// anim_seq+4 starts as an EID (LSB=1). NSLookup may write a PTE there.
    /// Probe the NSD page table so vertex lerp can run before that writeback.
    /// </summary>
    static bool TrySvtxEntry(CpuContext c, IMemory m, uint obj, out uint en, out uint seq)
    {
        en = 0;
        seq = m.ReadU32(obj + ObjAnimSeqOff);
        if ((seq & 0xFF000000u) != 0x80000000u) return false;
        if (m.ReadU8(seq) != AnimTypeVtx) return false;
        uint r = m.ReadU32(seq + 4);
        en = ResolveSvtxEntry(m, r);
        if (en == 0)
            en = LookupAnimEntry(c, m, seq);
        if (en == 0) return false;
        uint type = m.ReadU32(en + 8);
        return type == SvtxEntryType || type == CvtxEntryType;
    }

    static uint ResolveSvtxEntry(IMemory m, uint r)
    {
        uint en = EntryFromRef(m, r);
        if (en != 0) return en;
        return ProbeEidEntry(m, r);
    }

    static uint LookupAnimEntry(CpuContext c, IMemory m, uint seq)
    {
        uint a0 = c.A0, a1 = c.A1, a2 = c.A2, a3 = c.A3, v0 = c.V0, v1 = c.V1;
        try
        {
            c.A0 = seq + 4;
            Dispatcher.Call(c, m, NsLookupAddr);
            uint en = c.V0;
            if ((en & 0xFF000000u) != 0x80000000u) return 0;
            if (m.ReadU32(en) != EntryMagic) return 0;
            return en;
        }
        catch
        {
            return 0;
        }
        finally
        {
            c.A0 = a0;
            c.A1 = a1;
            c.A2 = a2;
            c.A3 = a3;
            c.V0 = v0;
            c.V1 = v1;
        }
    }

    static uint EntryFromRef(IMemory m, uint r)
    {
        if ((r & 1u) != 0) return 0;
        if ((r & 0xFF000000u) != 0x80000000u) return 0;
        uint magic = m.ReadU32(r);
        if (magic == EntryMagic) return r;
        if ((magic & 1u) != 0) return 0;
        if ((magic & 0xFF000000u) != 0x80000000u) return 0;
        if (m.ReadU32(magic) != EntryMagic) return 0;
        return magic;
    }

    static uint ProbeEidEntry(IMemory m, uint eid)
    {
        if ((eid & 1u) == 0) return 0;
        uint buckets = m.ReadU32(NsPteBucketsAddr);
        uint pageTable = m.ReadU32(NsPageTableAddr);
        uint nsd = m.ReadU32(NsNsdAddr);
        if ((buckets & 0xFF000000u) != 0x80000000u) return 0;
        int tableSize = 4096;
        if ((nsd & 0xFF000000u) == 0x80000000u)
        {
            int n = (int)m.ReadU32(nsd + NsdPageTableSizeOff);
            if (n > 0 && n <= 65536) tableSize = n;
        }
        uint pte = m.ReadU32(buckets + ((eid >> 15) & 0xFFu) * 4u);
        uint tableEnd = 0;
        if ((pageTable & 0xFF000000u) == 0x80000000u)
            tableEnd = pageTable + (uint)tableSize * 8u;
        for (int i = 0; i < tableSize; i++, pte += 8)
        {
            if ((pte & 0xFF000000u) != 0x80000000u) break;
            if (tableEnd != 0 && pte >= tableEnd) break;
            if (m.ReadU32(pte + 4) != eid) continue;
            uint v = m.ReadU32(pte);
            if ((v & 1u) != 0) return 0;
            if ((v & 0xFF000000u) != 0x80000000u) return 0;
            if (m.ReadU32(v) != EntryMagic) return 0;
            return v;
        }
        return 0;
    }

    static int SvtxVertCount(IMemory m, uint frame)
    {
        int bytes = (int)m.ReadU32(frame) * 4;
        if (bytes < SvtxHeaderBytes + SvtxVertBytes) return 0;
        return (bytes - SvtxHeaderBytes) / SvtxVertBytes;
    }

    static void PatchObjectMesh(CpuContext c, IMemory m, uint obj)
    {
        if (_svtxPatched) return;
        if (GamePaused(m)) return;
        if (_exactTicks >= RefTicks - 0.01) return;
        bool box = false;
        try { box = TryReadGoolClass(m, obj, out uint typ, out _) && typ == GoolTypeBox; }
        catch { /* */ }
        try
        {
            if (!TrySvtxEntry(c, m, obj, out uint en, out _))
            {
                if (WantSvtxLog(_crashObj, box))
                {
                    uint seq2 = 0, r = 0;
                    byte at = 0;
                    try
                    {
                        seq2 = m.ReadU32(obj + ObjAnimSeqOff);
                        if ((seq2 & 0xFF000000u) == 0x80000000u)
                        {
                            at = m.ReadU8(seq2);
                            r = m.ReadU32(seq2 + 4);
                        }
                    }
                    catch { /* */ }
                    NoteSvtxLog(_crashObj, box,
                        $"svtx miss obj=0x{obj:X8} crash={_crashObj} box={box} seq=0x{seq2:X8} type={at} ref=0x{r:X8}");
                }
                return;
            }
            int items = (int)m.ReadU32(en + 12);
            int cur = (int)m.ReadU32(obj + ObjAnimFrameOff) >> 8;
            if ((uint)cur >= (uint)items) return;
            PatchDrawnLookAhead(c, m, obj, EntryItem(m, en, cur), _crashObj, box, items);
        }
        catch
        {
            RestoreSvtx(m);
        }
    }

    static bool WantSvtxLog(bool crash, bool box) =>
        crash ? _svtxCrashLog < 6 : box ? _svtxBoxLog < 6 : _svtxLog < 4;

    static void NoteSvtxLog(bool crash, bool box, string msg)
    {
        if (crash) _svtxCrashLog++;
        else if (box) _svtxBoxLog++;
        else _svtxLog++;
        PaceLog(msg);
    }

    /// <summary>
    /// Fraction of one original 33 ms pose. Sample at the end of the current
    /// present (elapsed + dt) so a 33 ms key with two displays is t=1/2 then
    /// t=1 (authored frame). Starting at t=0 never reaches 1 before the next
    /// playanim — only a 50 % morph, which is the Crash 60 Hz look. 120+ has
    /// more samples and does reach 1; 30 Hz skips lerp. Wall dt, not an FPS table.
    /// </summary>
    static double WallAnimFrac(uint obj, int idx, out int fromIdx)
    {
        fromIdx = idx;
        long now = Stopwatch.GetTimestamp();
        double present = _exactTicks / TicksPerSecond;
        if (present < 0) present = 0;
        if (_poseClock.TryGetValue(obj, out PoseClock p) && p.Idx == idx)
        {
            fromIdx = p.From;
            double sec = (now - p.Ts) / (double)Stopwatch.Frequency + present;
            if (sec < 0) sec = 0;
            if (sec > HitchSeconds) sec = HitchSeconds;
            return sec / HitchSeconds;
        }
        int from = idx;
        if (_poseClock.TryGetValue(obj, out p))
            from = p.Idx;
        fromIdx = from;
        _poseClock[obj] = new PoseClock { Idx = idx, From = from, Ts = now };
        double t0 = present / HitchSeconds;
        if (t0 > 1) t0 = 1;
        return t0;
    }

    static bool TryAnimLerpKeys(uint obj, int drawnIdx, int items,
        out int fromIdx, out int toIdx, out double t)
    {
        fromIdx = drawnIdx;
        toIdx = drawnIdx;
        t = 0;
        if (_gateRot.TryGetValue(obj, out GatePose g) &&
            _simAcc.TryGetValue(obj, out double acc))
        {
            fromIdx = g.FromAnim >> 8;
            toIdx = g.ToAnim >> 8;
            t = acc / RefTicks;
            if (t < 0) t = 0;
            if (t > 1) t = 1;
        }
        else if (_animPose.TryGetValue(obj, out AnimPose ap) && ap.Have && ap.Holding)
        {
            fromIdx = ap.Frame >> 8;
            toIdx = ap.Target >> 8;
            double sec = (Stopwatch.GetTimestamp() - ap.Ts) / (double)Stopwatch.Frequency;
            if (sec < 0) sec = 0;
            if (sec > HitchSeconds) sec = HitchSeconds;
            t = sec / HitchSeconds;
        }
        else
        {
            t = WallAnimFrac(obj, drawnIdx, out fromIdx);
            toIdx = drawnIdx;
        }
        if (fromIdx == toIdx) return false;
        if ((uint)fromIdx >= (uint)items || (uint)toIdx >= (uint)items) return false;
        return t < 0.99;
    }

    static void PatchDrawnLookAhead(
        CpuContext c, IMemory m, uint obj, uint drawn, bool crash, bool box, int itemsHint = 0)
    {
        if (crash) return;
        if (IsPinsC(m, obj)) return;
        if ((drawn & 0xFF000000u) != 0x80000000u) return;
        if (GamePaused(m)) return;

        if (!TrySvtxEntry(c, m, obj, out uint en, out _) &&
            !TryEntryFromFrame(m, drawn, out en))
        {
            if (WantSvtxLog(crash, box))
                NoteSvtxLog(crash, box,
                    $"svtx miss obj=0x{obj:X8} crash={crash} box={box} drawn=0x{drawn:X8}");
            return;
        }

        int items = itemsHint > 0 ? itemsHint : (int)m.ReadU32(en + 12);
        int cur = FrameIndex(m, en, items, drawn);
        if (cur < 0) return;
        if (!TryAnimLerpKeys(obj, cur, items, out int from, out int to, out double t))
            return;
        uint srcFrom = EntryItem(m, en, from);
        uint srcTo = EntryItem(m, en, to);
        int n = SvtxVertCount(m, drawn);
        int nFrom = SvtxVertCount(m, srcFrom);
        int nTo = SvtxVertCount(m, srcTo);
        if (n <= 0 || n != nFrom || n != nTo || n > SvtxVertMax) return;
        if (WantSvtxLog(crash, box))
            NoteSvtxLog(crash, box,
                $"svtx lerp obj=0x{obj:X8} crash={crash} box={box} {from}->{to} n={n} t={t:0.00}");

        _svtxSaveOx = (int)m.ReadU32(drawn + 8);
        _svtxSaveOy = (int)m.ReadU32(drawn + 12);
        _svtxSaveOz = (int)m.ReadU32(drawn + 16);
        int fromOx = (int)m.ReadU32(srcFrom + 8);
        int fromOy = (int)m.ReadU32(srcFrom + 12);
        int fromOz = (int)m.ReadU32(srcFrom + 16);
        int toOx = (int)m.ReadU32(srcTo + 8);
        int toOy = (int)m.ReadU32(srcTo + 12);
        int toOz = (int)m.ReadU32(srcTo + 16);
        m.WriteU32(drawn + 8, (uint)LerpPos(fromOx, toOx, t));
        m.WriteU32(drawn + 12, (uint)LerpPos(fromOy, toOy, t));
        m.WriteU32(drawn + 16, (uint)LerpPos(fromOz, toOz, t));

        uint drawnV = drawn + (uint)SvtxHeaderBytes;
        uint fromV = srcFrom + (uint)SvtxHeaderBytes;
        uint toV = srcTo + (uint)SvtxHeaderBytes;
        int bytes = n * SvtxVertBytes;
        for (int i = 0; i < bytes; i++)
            _svtxSave[i] = m.ReadU8(drawnV + (uint)i);
        for (int i = 0; i < bytes; i += SvtxVertBytes)
        {
            for (int k = 0; k < 3; k++)
            {
                int a = m.ReadU8(fromV + (uint)(i + k));
                int b = m.ReadU8(toV + (uint)(i + k));
                m.WriteU8(drawnV + (uint)(i + k), (byte)(a + (int)Math.Round((b - a) * t)));
            }
        }
        _svtxFrame = drawn;
        _svtxSaveN = bytes;
        _svtxPatched = true;
    }

    static int FrameIndex(IMemory m, uint en, int items, uint frame)
    {
        for (int i = 0; i < items; i++)
            if (EntryItem(m, en, i) == frame) return i;
        return -1;
    }

    static bool TryEntryFromFrame(IMemory m, uint frame, out uint en)
    {
        en = 0;
        uint page = frame & ~0xFFFFu;
        uint p = frame & ~3u;
        for (int n = 0; n < 4096 && p > page + 16; n++)
        {
            p -= 4;
            if (m.ReadU32(p) != EntryMagic) continue;
            uint type = m.ReadU32(p + 8);
            if (type != SvtxEntryType && type != CvtxEntryType) continue;
            int items = (int)m.ReadU32(p + 12);
            if (items <= 0 || items > 256) continue;
            if (FrameIndex(m, p, items, frame) < 0) continue;
            en = p;
            return true;
        }
        return false;
    }

    static void RestoreSvtx(IMemory m)
    {
        if (!_svtxPatched) return;
        _svtxPatched = false;
        try
        {
            m.WriteU32(_svtxFrame + 8, (uint)_svtxSaveOx);
            m.WriteU32(_svtxFrame + 12, (uint)_svtxSaveOy);
            m.WriteU32(_svtxFrame + 16, (uint)_svtxSaveOz);
            uint toV = _svtxFrame + (uint)SvtxHeaderBytes;
            for (int i = 0; i < _svtxSaveN; i++)
                m.WriteU8(toV + (uint)i, _svtxSave[i]);
        }
        catch
        {
            // overlay swap
        }
    }

    static int LerpPos(int from, int to, double t)
    {
        long d = (long)to - from;
        if (d > Teleport || d < -Teleport) return to;
        return from + (int)Math.Round(d * t);
    }

    static int LerpAng(int from, int to, double t)
    {
        int a = from & 0xFFF;
        int d = AngDelta(from, to);
        if (Math.Abs(d) > 0x200) return to & 0xFFF;
        return (a + (int)Math.Round(d * t)) & 0xFFF;
    }

    static int AngDelta(int from, int to)
    {
        int d = (to & 0xFFF) - (from & 0xFFF);
        if (d > 0x800) d -= 0x1000;
        if (d < -0x800) d += 0x1000;
        return d;
    }

}
