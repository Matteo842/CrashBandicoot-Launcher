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
    /// Iron crates are walls via <c>PlotObjWalls</c> + SOLID_SIDES. Skipping
    /// GoolObjectUpdate also skips GoolObjectBound, so they vanish from
    /// <c>object_bounds</c> and Crash walks through. Write the AABB only —
    /// do not call Bound (same-stamp GoolCollide kills siblings mid-walk).
    /// </summary>
    static void RegisterGatedBound(IMemory m, uint obj, uint statusB)
    {
        if ((statusB & FlagCollidable) == 0) return;
        if ((statusB & (FlagSolidSides | FlagSolidTop)) == 0) return;
        int n = (int)m.ReadU32(ObjectBoundCountAddr);
        if ((uint)n >= (uint)ObjectBoundMax) return;
        uint slot = ObjectBoundsAddr + (uint)n * 28u;
        bool fromSnap = _lastBound.TryGetValue(obj, out BoundSnap snap);
        if (fromSnap)
        {
            m.WriteU32(slot, (uint)snap.X1);
            m.WriteU32(slot + 4, (uint)snap.Y1);
            m.WriteU32(slot + 8, (uint)snap.Z1);
            m.WriteU32(slot + 12, (uint)snap.X2);
            m.WriteU32(slot + 16, (uint)snap.Y2);
            m.WriteU32(slot + 20, (uint)snap.Z2);
        }
        else
        {
            int tx = (int)m.ReadU32(obj + ObjTransOff);
            int ty = (int)m.ReadU32(obj + ObjTransOff + 4);
            int tz = (int)m.ReadU32(obj + ObjTransOff + 8);
            m.WriteU32(slot, (uint)(tx + (int)m.ReadU32(obj + ObjBoundOff)));
            m.WriteU32(slot + 4, (uint)(ty + (int)m.ReadU32(obj + ObjBoundOff + 4)));
            m.WriteU32(slot + 8, (uint)(tz + (int)m.ReadU32(obj + ObjBoundOff + 8)));
            m.WriteU32(slot + 12, (uint)(tx + (int)m.ReadU32(obj + ObjBoundOff + 12)));
            m.WriteU32(slot + 16, (uint)(ty + (int)m.ReadU32(obj + ObjBoundOff + 16)));
            m.WriteU32(slot + 20, (uint)(tz + (int)m.ReadU32(obj + ObjBoundOff + 20)));
        }
        TranslateGatedRideBound(m, obj, slot, fromSnap);
        m.WriteU32(slot + 24, obj);
        m.WriteU32(ObjectBoundCountAddr, (uint)(n + 1));
    }

    /// <summary>
    /// Copy the AABB GoolObjectBound just wrote. Extra gated frames reuse it
    /// instead of reconstructing col/yaw (that shifted the box and Crash
    /// walked through).
    /// </summary>
    static void CaptureBound(IMemory m, uint obj)
    {
        try
        {
            int n = (int)m.ReadU32(ObjectBoundCountAddr);
            if (n < 0) return;
            if (n > ObjectBoundMax) n = ObjectBoundMax;
            for (int i = n - 1; i >= 0; i--)
            {
                uint slot = ObjectBoundsAddr + (uint)i * 28u;
                if (m.ReadU32(slot + 24) != obj) continue;
                if (_lastBound.Count > 96)
                    EvictDict(_lastBound, obj);
                _lastBound[obj] = new BoundSnap
                {
                    X1 = (int)m.ReadU32(slot),
                    Y1 = (int)m.ReadU32(slot + 4),
                    Z1 = (int)m.ReadU32(slot + 8),
                    X2 = (int)m.ReadU32(slot + 12),
                    Y2 = (int)m.ReadU32(slot + 16),
                    Z2 = (int)m.ReadU32(slot + 20)
                };
                return;
            }
            _lastBound.Remove(obj);
        }
        catch
        {
            // object freed / overlay swap
        }
    }

    /// <summary>
    /// Temple / Jaws PoPlC, RuiOC slabs, RWaOC wall mill, and Auto path discs (time()).
    /// Not 2D flames, not Euler Wait/Active on other lids.
    /// </summary>
    static bool IsGatedRideSolid(IMemory m, uint obj)
    {
        if ((obj & 0xFF000000u) != 0x80000000u) return false;
        try
        {
            if (!TryReadGoolClass(m, obj, out uint type, out _) || !IsGatedTempleSolid(m, obj, type))
                return false;
            uint b = m.ReadU32(obj + ObjStatusBOff);
            if ((b & Flag2D) != 0) return false;
            return (b & FlagSolidTop) != 0;
        }
        catch
        {
            return false;
        }
    }

    static bool TryReadCrash(IMemory m, out uint crash)
    {
        crash = 0;
        try
        {
            crash = m.ReadU32(CrashPtrAddr);
            return crash != 0 && (crash & 0xFF000000u) == 0x80000000u;
        }
        catch
        {
            return false;
        }
    }

    static bool CrashCanRide(IMemory m, uint crash)
    {
        try
        {
            return !IsLandLockedState(m, crash) && !CrashOnWarthog(m, crash);
        }
        catch
        {
            return false;
        }
    }

    static bool CrashLeftRide(IMemory m, uint obj, uint crash)
    {
        if (!CrashCanRide(m, crash)) return true;
        if (!CrashAirborne(m, crash)) return false;
        try
        {
            // Jump takeoff. A mill reverse bump can set a fall state with
            // vy≤0 while Crash is still on the AABB — keep the carry or he
            // flies off the old way.
            if ((int)m.ReadU32(crash + ObjVelYOff) > 0)
                return true;
        }
        catch
        {
            return true;
        }
        return !CrashStandingOnPlat(m, obj, crash);
    }

    static void DampRideReverseVel(IMemory m, uint obj, uint crash, long dx, long dy, long dz)
    {
        if (_rideObj != obj) return;
        if (_rideRemX * dx + _rideRemY * dy + _rideRemZ * dz >= 0) return;
        try
        {
            m.WriteU32(crash + ObjVelXOff, 0);
            m.WriteU32(crash + ObjVelYOff, 0);
            m.WriteU32(crash + ObjVelZOff, 0);
        }
        catch
        {
            // object freed
        }
    }

    static void ClearGatedRide()
    {
        _rideObj = 0;
        _rideDidSnap = false;
        _rideRemX = 0;
        _rideRemY = 0;
        _rideRemZ = 0;
        _rideAppX = 0;
        _rideAppY = 0;
        _rideAppZ = 0;
    }

    static void SnapshotGatedCarry(IMemory m, uint obj)
    {
        _rideDidSnap = false;
        if (!IsGatedRideSolid(m, obj) || !TryReadCrash(m, out uint crash))
            return;
        try
        {
            _rideSnapX = (int)m.ReadU32(crash + ObjTransOff);
            _rideSnapY = (int)m.ReadU32(crash + ObjTransOff + 4);
            _rideSnapZ = (int)m.ReadU32(crash + ObjTransOff + 8);
            _rideDidSnap = true;
        }
        catch
        {
            _rideDidSnap = false;
        }
    }

    /// <summary>
    /// Finish the last 30 Hz carry before a new interpret so CarryCollider
    /// sees Crash at the end of the previous step, not still mid-lerp.
    /// Must snap to Q here: <c>_simAcc</c> is already the next leftover.
    /// </summary>
    static void FlushGatedRide(IMemory m, uint obj)
    {
        if (_rideObj != obj) return;
        ApplyRideTarget(m, _rideRemX, _rideRemY, _rideRemZ);
        _rideDidSnap = false;
    }

    /// <summary>
    /// Trans already wrote the full 30 Hz carry (including 0.985). Undo it
    /// and add rem × (acc/34) — the same leftover clock as the drawn lerp
    /// and skip AABB. Spreading rem over 34 ticks from this present's dt
    /// put Crash ahead of the floor (leftover t is 0..dt, not dt). At 30
    /// FPS dt is 34 so this is a no-op and CarryCollider stays original.
    /// </summary>
    static void PaceGatedCarry(IMemory m, uint obj)
    {
        if (!_rideDidSnap)
            return;
        _rideDidSnap = false;
        if (!IsGatedRideSolid(m, obj) || !TryReadCrash(m, out uint crash))
        {
            if (_rideObj == obj)
                ClearGatedRide();
            return;
        }
        try
        {
            if (CrashLeftRide(m, obj, crash))
            {
                m.WriteU32(crash + ObjTransOff, (uint)_rideSnapX);
                m.WriteU32(crash + ObjTransOff + 4, (uint)_rideSnapY);
                m.WriteU32(crash + ObjTransOff + 8, (uint)_rideSnapZ);
                ClearGatedRide();
                return;
            }
            if (_exactTicks >= RefTicks - 0.01)
            {
                if (_rideObj == obj)
                    ClearGatedRide();
                return;
            }
            int nx = (int)m.ReadU32(crash + ObjTransOff);
            int ny = (int)m.ReadU32(crash + ObjTransOff + 4);
            int nz = (int)m.ReadU32(crash + ObjTransOff + 8);
            long dx = (long)nx - _rideSnapX;
            long dy = (long)ny - _rideSnapY;
            long dz = (long)nz - _rideSnapZ;
            if (dx > VelTeleport || dx < -VelTeleport
                || dy > VelTeleport || dy < -VelTeleport
                || dz > VelTeleport || dz < -VelTeleport)
            {
                ClearGatedRide();
                return;
            }
            if (dx == 0 && dy == 0 && dz == 0
                && !TryGatedPlatDelta(m, obj, crash, out dx, out dy, out dz))
            {
                if (_rideObj == obj)
                    ClearGatedRide();
                return;
            }
            // Undo the 30 Hz GOOL write. Skip presents add rem×(acc/34)
            // so extra StopAtWalls cannot eat a frozen-AABB remainder.
            DampRideReverseVel(m, obj, crash, dx, dy, dz);
            m.WriteU32(crash + ObjTransOff, (uint)_rideSnapX);
            m.WriteU32(crash + ObjTransOff + 4, (uint)_rideSnapY);
            m.WriteU32(crash + ObjTransOff + 8, (uint)_rideSnapZ);
            _rideObj = obj;
            _rideRemX = dx;
            _rideRemY = dy;
            _rideRemZ = dz;
            _rideAppX = 0;
            _rideAppY = 0;
            _rideAppZ = 0;
            FollowGatedRideAcc(m);
            if (_rideLog < 8)
            {
                _rideLog++;
                PaceLog($"ride 0x{obj:X8} d={dx},{dz} dt={_exactTicks:0.00}");
            }
        }
        catch
        {
            ClearGatedRide();
        }
    }

    /// <summary>
    /// Path delta when CarryCollider did not run (collider cleared). Crash is
    /// still at the snapshot; GatePose P→Q is the 30 Hz step just captured.
    /// </summary>
    static bool TryGatedPlatDelta(IMemory m, uint obj, uint crash,
        out long dx, out long dy, out long dz)
    {
        dx = 0;
        dy = 0;
        dz = 0;
        if (!_gateRot.TryGetValue(obj, out GatePose g)) return false;
        bool onPlat = CrashOnPlatPos(m, obj, crash, g.Px, g.Py, g.Pz);
        if (!onPlat)
        {
            try
            {
                onPlat = m.ReadU32(obj + ObjColliderOff) == crash;
            }
            catch
            {
                return false;
            }
        }
        if (!onPlat) return false;
        dx = (long)g.Qx - g.Px;
        dy = (long)g.Qy - g.Py;
        dz = (long)g.Qz - g.Pz;
        if (dx == 0 && dy == 0 && dz == 0) return false;
        if (dx > VelTeleport || dx < -VelTeleport
            || dy > VelTeleport || dy < -VelTeleport
            || dz > VelTeleport || dz < -VelTeleport)
            return false;
        return true;
    }

    /// <summary>
    /// Original CarryCollider is collider==crash (standing). Spreading that
    /// through jump states glued Crash to the disc in the air — the tight
    /// third ferry jump is then impossible at uncapped, and the mesh (acc
    /// already += dt) buzzes against Crash (still on last present's t).
    /// Apply after the whole object tree so skip acc matches the drawn lerp.
    /// </summary>
    static void RideAfterCrash(IMemory m)
    {
        if (_rideObj == 0) return;
        if (GamePaused(m)) return;
        if (!TryReadCrash(m, out uint crash)
            || CrashLeftRide(m, _rideObj, crash))
        {
            ClearGatedRide();
            return;
        }
        FollowGatedRideAcc(m);
    }

    /// <summary>
    /// Same t as <see cref="ApplyGatedDisplayPose"/> / skip AABB.
    /// </summary>
    static void FollowGatedRideAcc(IMemory m)
    {
        if (_rideObj == 0) return;
        if (!_simAcc.TryGetValue(_rideObj, out double acc))
            return;
        double t = acc / RefTicks;
        if (t < 0) t = 0;
        if (t > 1) t = 1;
        ApplyRideTarget(m, _rideRemX * t, _rideRemY * t, _rideRemZ * t);
    }

    static void ApplyRideTarget(IMemory m, double tx, double ty, double tz)
    {
        if (_rideObj == 0) return;
        if (!TryReadCrash(m, out uint crash) || !CrashCanRide(m, crash))
        {
            ClearGatedRide();
            return;
        }
        try
        {
            int ix = (int)Math.Round(tx) - _rideAppX;
            int iy = (int)Math.Round(ty) - _rideAppY;
            int iz = (int)Math.Round(tz) - _rideAppZ;
            _rideAppX += ix;
            _rideAppY += iy;
            _rideAppZ += iz;
            if (ix != 0)
                m.WriteU32(crash + ObjTransOff, (uint)((int)m.ReadU32(crash + ObjTransOff) + ix));
            if (iy != 0)
                m.WriteU32(crash + ObjTransOff + 4, (uint)((int)m.ReadU32(crash + ObjTransOff + 4) + iy));
            if (iz != 0)
                m.WriteU32(crash + ObjTransOff + 8, (uint)((int)m.ReadU32(crash + ObjTransOff + 8) + iz));
        }
        catch
        {
            ClearGatedRide();
        }
    }

    static bool TryGatedSolidVisual(uint obj, out int vx, out int vy, out int vz)
    {
        vx = 0;
        vy = 0;
        vz = 0;
        if (!_gateRot.TryGetValue(obj, out GatePose g))
            return false;
        if (!_simAcc.TryGetValue(obj, out double acc))
            return false;
        double t = acc / RefTicks;
        if (t < 0) t = 0;
        if (t > 1) t = 1;
        vx = LerpPos(g.Px, g.Qx, t);
        vy = LerpPos(g.Py, g.Qy, t);
        vz = LerpPos(g.Pz, g.Qz, t);
        return true;
    }

    /// <summary>
    /// Bound ran before trans, so the snap is at GatePose P. Shift it to the
    /// display lerp so Crash's extra StopAtWalls sees the moving floor.
    /// </summary>
    static void TranslateGatedRideBound(IMemory m, uint obj, uint slot, bool fromSnap)
    {
        if (!IsGatedRideSolid(m, obj)) return;
        if (!TryGatedSolidVisual(obj, out int vx, out int vy, out int vz))
            return;
        if (!_gateRot.TryGetValue(obj, out GatePose g)) return;
        int bx, by, bz;
        if (fromSnap)
        {
            bx = g.Px;
            by = g.Py;
            bz = g.Pz;
        }
        else
        {
            bx = (int)m.ReadU32(obj + ObjTransOff);
            by = (int)m.ReadU32(obj + ObjTransOff + 4);
            bz = (int)m.ReadU32(obj + ObjTransOff + 8);
        }
        int dx = vx - bx;
        int dy = vy - by;
        int dz = vz - bz;
        if (dx == 0 && dy == 0 && dz == 0) return;
        m.WriteU32(slot, (uint)((int)m.ReadU32(slot) + dx));
        m.WriteU32(slot + 4, (uint)((int)m.ReadU32(slot + 4) + dy));
        m.WriteU32(slot + 8, (uint)((int)m.ReadU32(slot + 8) + dz));
        m.WriteU32(slot + 12, (uint)((int)m.ReadU32(slot + 12) + dx));
        m.WriteU32(slot + 16, (uint)((int)m.ReadU32(slot + 16) + dy));
        m.WriteU32(slot + 20, (uint)((int)m.ReadU32(slot + 20) + dz));
    }

    static void LogObjectClass(IMemory m, uint obj)
    {
        if (!_haveObj || _crashObj) return;
        try
        {
            uint b = m.ReadU32(obj + ObjStatusBOff);
            TryReadGoolClass(m, obj, out uint type, out uint cat);
            bool tnt = type == GoolTypeBox || type == GoolTypeRooO;
            bool plat = cat == GoolCategoryPlatform;
            if (!tnt && !plat && _objClassLog >= 8) return;
            if ((tnt || plat) && _objClassLog >= 24) return;
            _objClassLog++;
            PaceLog($"obj 0x{obj:X8} b=0x{b:X} type={type} cat=0x{cat:X} solid={_solidObj} plat={_platObj} hop={_pathHoppers.Contains(obj)}");
        }
        catch
        {
            // object freed
        }
    }

    static bool IsFirstFrame(IMemory m, uint obj)
    {
        if ((obj & 0xFF000000u) != 0x80000000u) return false;
        try
        {
            return (m.ReadU32(obj + ObjStatusAOff) & FlagFirstFrame) != 0;
        }
        catch
        {
            return false;
        }
    }

    static void ArmSpawnBurst(IMemory m, uint obj)
    {
        _spawnBurst = IsFirstFrame(m, obj);
        _spawnFirstFrame = _spawnBurst;
        if (_spawnBurst || (obj & 0xFF000000u) != 0x80000000u) return;
        _spawnCredit.TryGetValue(obj, out double credit);
        credit += _exactTicks;
        if (credit >= RefTicks)
            _spawnBurst = true;
        if (_spawnCredit.Count > 128)
            _spawnCredit.Clear();
        _spawnCredit[obj] = credit;
    }

    static void NoteSpawnUsed()
    {
        if (!_didSpawn || !_spawnBurst || _crashObj || !_haveObj || _spawnFirstFrame) return;
        if (_spawnCredit.TryGetValue(_obj, out double credit))
        {
            credit -= RefTicks;
            if (credit < 0) credit = 0;
            _spawnCredit[_obj] = credit;
        }
    }

    /// <summary>
    /// Trans runs every display frame. spawn() in trans at 270 Hz fills the
    /// 96-slot pool (GoolObjectAlloc then steals expendables) and overflows
    /// world-clip scratchpad. Keep GOOL creates at one 30 Hz burst.
    /// </summary>
    static bool PreObjectCreate(CpuContext c, IMemory m)
    {
        if (!IsActive(m) || !_haveObj || _crashObj) return true;
        if (c.A1 == 0 && c.A2 == 0) return true;
        if (IsFirstFrame(m, _obj))
        {
            _didSpawn = true;
            return true;
        }
        if (TryReadGoolClass(m, _obj, out uint ptype, out _)
            && ptype is GoolTypeBox or GoolTypeBono or GoolTypeCard)
        {
            _didSpawn = true;
            return true;
        }
        if (!_spawnBurst || _spawnBudget <= 0)
        {
            c.V0 = ErrorObjectPoolFull;
            return false;
        }
        _spawnBudget--;
        _didSpawn = true;
        return true;
    }

    /// <summary>
    /// DispC (lives, wumpa, Tawna tokens, pause). Not every FLAG_2D sprite —
    /// PoRoC mist and RuiOC flames use that bit in the world.
    /// </summary>
    static bool IsHud(IMemory m, uint obj)
    {
        if ((obj & 0xFF000000u) != 0x80000000u) return false;
        try
        {
            if (!TryReadGoolClass(m, obj, out uint type, out uint cat))
                return false;
            return type == GoolTypeDisp || cat == GoolCategoryHud;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// GoolObjectUpdate decrements anim_counter once per display frame.
    /// Add one back until 34 wall ticks pass so stall lasts 30 Hz, not 300 Hz.
    /// Death / Warp_In skip extra Updates — do not call this there.
    /// </summary>
    static void HoldCrashStall(IMemory m, uint obj)
    {
        try
        {
            if ((m.ReadU32(obj + ObjStatusBOff) & FlagStall) == 0) return;
            uint n = m.ReadU32(obj + ObjAnimCounterOff);
            if (n == 0) return;
            _stallFrac += _exactTicks;
            if (_stallFrac < RefTicks)
                m.WriteU32(obj + ObjAnimCounterOff, n + 1);
            else
                _stallFrac -= RefTicks;
        }
        catch
        {
            // object freed
        }
    }

    /// <summary>
    /// Trans ChangeAnim does not suspend (no SUSPEND_ON_ANIM), so anim_frame
    /// still advances every present even when the code wait tag is held.
    /// Keep seq/frame at the last committed pose until 33 ms of wall time
    /// pass. Reverse is the look ping-pong, not a cut — hold it too.
    /// Gated solids already step at 30 Hz; holding them again made
    /// CaptureGateRot see from==to so the mesh never lerped.
    /// </summary>
    static void HoldAnimPose(IMemory m, uint obj)
    {
        if ((obj & 0xFF000000u) != 0x80000000u) return;
        if (IsHud(m, obj)) return;
        if (GamePaused(m)) return;
        if (obj == _obj && _solidObj) return;
        // Crash look is CODE playanim wait=0 (stance 14↔19). HoldAnimWait
        // already steps that like gated TNT: one ChangeAnim per 33 ms wall.
        // Holding anim_frame again freezes idx so WallAnimFrac sees from==to
        // for the whole wait — 30 Hz snaps, no morph. Leave the new frame
        // and lerp previous→current on the Gfx pointer.
        if (obj == _obj && _crashObj)
        {
            if (_animPose.TryGetValue(obj, out AnimPose freeze) && freeze.Holding)
            {
                freeze.Holding = false;
                _animPose[obj] = freeze;
            }
            return;
        }
        try
        {
            uint type = m.ReadU32(obj);
            if (type is 0 or 2) return;
            uint seq = m.ReadU32(obj + ObjAnimSeqOff);
            if ((seq & 0xFF000000u) != 0x80000000u) return;
            if (m.ReadU8(seq) != AnimTypeVtx) return;
            int frame = (int)m.ReadU32(obj + ObjAnimFrameOff);
            long now = Stopwatch.GetTimestamp();
            if (_exactTicks >= RefTicks - 0.01)
            {
                CommitAnimPose(obj, seq, frame, 0, now);
                return;
            }
            if (!_animPose.TryGetValue(obj, out AnimPose p) || !p.Have)
            {
                CommitAnimPose(obj, seq, frame, 0, now);
                return;
            }
            if (seq != p.Seq)
            {
                CommitAnimPose(obj, seq, frame, 0, now);
                return;
            }
            int a = frame >> 8;
            int b = p.Frame >> 8;
            if (a == b)
            {
                if (!p.Holding) return;
                double holdSec = (now - p.Ts) / (double)Stopwatch.Frequency;
                if (holdSec < HitchSeconds) return;
                long holdTick = (long)(HitchSeconds * Stopwatch.Frequency);
                long holdTs = p.Ts + holdTick;
                if (now - holdTs > holdTick)
                    holdTs = now;
                m.WriteU32(obj + ObjAnimFrameOff, (uint)p.Target);
                CommitAnimPose(obj, seq, p.Target, p.LastStep, holdTs);
                return;
            }
            int step = a - b;
            int d = step < 0 ? -step : step;
            if (d > 2)
            {
                CommitAnimPose(obj, seq, frame, step, now);
                return;
            }
            double sec = (now - p.Ts) / (double)Stopwatch.Frequency;
            if (sec < 0) sec = 0;
            if (sec < HitchSeconds)
            {
                m.WriteU32(obj + ObjAnimSeqOff, p.Seq);
                m.WriteU32(obj + ObjAnimFrameOff, (uint)p.Frame);
                p.Target = frame;
                p.LastStep = step;
                p.Holding = true;
                _animPose[obj] = p;
                if (_poseHoldLog < 8)
                {
                    _poseHoldLog++;
                    bool crash = false;
                    try { crash = obj == m.ReadU32(CrashPtrAddr); } catch { /* */ }
                    PaceLog($"pose hold obj=0x{obj:X8} crash={crash} {b}->{a} sec={sec:0.000}");
                }
                return;
            }
            long tick = (long)(HitchSeconds * Stopwatch.Frequency);
            long ts = p.Ts + tick;
            if (now - ts > tick)
                ts = now;
            CommitAnimPose(obj, seq, frame, step, ts);
        }
        catch
        {
            // object freed
        }
    }

    static void CommitAnimPose(uint obj, uint seq, int frame, int step, long ts)
    {
        _animPose[obj] = new AnimPose
        {
            Seq = seq,
            Frame = frame,
            Target = frame,
            LastStep = step,
            Ts = ts,
            Have = true,
            Holding = false
        };
        int idx = frame >> 8;
        int from = idx;
        if (_poseClock.TryGetValue(obj, out PoseClock p))
            from = p.Idx;
        long behind = (long)(HitchSeconds * Stopwatch.Frequency);
        _poseClock[obj] = new PoseClock { Idx = idx, From = from, Ts = ts - behind };
    }

    /// <summary>
    /// wait=0 is next Update. Hold it for one original 33 ms of wall time
    /// so 300 FPS and 500 FPS take the same pose step. A present that is
    /// already a full 34-tick step does not hold (that is the original
    /// 30 Hz loop, not a 60/120 table).
    /// </summary>
    static void HoldAnimWait(IMemory m, uint obj)
    {
        if (_exactTicks >= RefTicks - 0.01) return;
        if (IsFirstFrame(m, obj))
        {
            _animAcc[obj] = 0;
            _animHold.Remove(obj);
            _waitHoldTs.Remove(obj);
            return;
        }
        try
        {
            uint sp = m.ReadU32(obj + ObjSpOff);
            if ((sp & 0xFF000000u) != 0x80000000u || sp < 4) return;
            uint tagAddr = sp - 4;
            uint top = m.ReadU32(tagAddr);
            uint wait = top >> 24;
            if (wait > 1)
            {
                _animHold.Remove(obj);
                _waitHoldTs.Remove(obj);
                return;
            }
            uint fe = m.ReadU32(FramesElapsedAddr) & 0x00FFFFFFu;
            bool holding = _waitHoldTs.ContainsKey(obj) || _animHold.Contains(obj);
            if (wait == 1 && !holding) return;
            if (_waitHoldTs.Count > 128)
            {
                _waitHoldTs.Clear();
                _animHold.Clear();
                _animAcc.Clear();
            }
            long now = Stopwatch.GetTimestamp();
            if (!_waitHoldTs.TryGetValue(obj, out long start))
            {
                _waitHoldTs[obj] = now;
                start = now;
            }
            double sec = (now - start) / (double)Stopwatch.Frequency;
            if (sec < HitchSeconds)
            {
                _animAcc[obj] = sec * TicksPerSecond;
                _animHold.Add(obj);
                m.WriteU32(tagAddr, 0x01000000u | fe);
                return;
            }
            _waitHoldTs.Remove(obj);
            _animHold.Remove(obj);
            _animAcc[obj] = 0;
            m.WriteU32(tagAddr, fe);
        }
        catch
        {
            // stack not mapped
        }
    }

}
