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
    static bool GamePaused(IMemory m)
    {
        try { return m.ReadU32(PausedAddr) != 0; }
        catch { return false; }
    }

    static int ScaleStep(int step)
    {
        if (step == 0) return 0;
        long n = (long)step * _frameTicks;
        int s = (int)((n + (n >= 0 ? RefTicks / 2 : -(RefTicks / 2))) / RefTicks);
        if (s == 0) return step > 0 ? 1 : -1;
        return s;
    }

    static int ScaleExact(int from, int to, int teleport)
    {
        long d = (long)to - from;
        if (d > teleport || d < -teleport) return to;
        return from + (int)Math.Round(d * _exactTicks / RefTicks);
    }

    /// <summary>
    /// Same dt/34 as <see cref="ScaleExact"/>, but leftovers stay in
    /// <paramref name="frac"/> instead of rounding to ±1 every present.
    /// </summary>
    static int KeepCrashDelta(int from, int to, int teleport, ref double frac)
    {
        long d = (long)to - from;
        if (d > teleport || d < -teleport)
        {
            frac = 0;
            return to;
        }
        if (d == 0)
        {
            frac = 0;
            return from;
        }
        if (_exactTicks <= 0) return from;
        double step = d * _exactTicks / RefTicks + frac;
        int kept = (int)Math.Truncate(step);
        frac = step - kept;
        return from + kept;
    }

    static int KeepCrashAng(int from, int to, ref double frac)
    {
        int a = from & 0xFFF;
        int b = to & 0xFFF;
        int d = b - a;
        if (d > 0x800) d -= 0x1000;
        if (d < -0x800) d += 0x1000;
        if (d == 0)
        {
            frac = 0;
            return a;
        }
        if (_exactTicks <= 0) return a;
        double step = d * _exactTicks / RefTicks + frac;
        int kept = (int)Math.Truncate(step);
        frac = step - kept;
        return (a + kept) & 0xFFF;
    }

    static void ClearCrashScaleFrac()
    {
        _crashFracTX = 0;
        _crashFracTY = 0;
        _crashFracTZ = 0;
        _crashFracVX = 0;
        _crashFracVY = 0;
        _crashFracVZ = 0;
        _crashFracRX = 0;
        _crashFracRY = 0;
        _crashFracRZ = 0;
        _crashFracSp = 0;
    }

    /// <summary>
    /// Guest yaw/pitch/roll are 12-bit. Interpolating the raw ints unwraps
    /// past 0x1000 when turning CCW through 0; the next step then sees
    /// from≡to (mod 4096) and d=0 — Crash sticks on that heading. 0x200 is
    /// the 45° stick diagonal. Only runs when dt&lt;34, so 30 FPS is original
    /// and 60 (t=1/2) shows it hardest. Same wrap at any unlocked dt.
    /// </summary>
    static int ScaleAng(int from, int to)
    {
        int a = from & 0xFFF;
        int b = to & 0xFFF;
        int d = b - a;
        if (d > 0x800) d -= 0x1000;
        if (d < -0x800) d += 0x1000;
        return (a + (int)Math.Round(d * _exactTicks / RefTicks)) & 0xFFF;
    }

    /// <summary>
    /// Physics ran a 34-tick StopAtWalls step. Keep dt/34 of XZ. Rebuild Y
    /// from post-trans vy (hang already at wall ticks) + 4000*dt gravity.
    /// Guest order: displace then gravity.
    /// </summary>
    static void FinishJumpScale(IMemory m)
    {
        uint o = _obj;
        if (_exactTicks <= 0)
        {
            m.WriteU32(o + ObjTransOff, (uint)_ox);
            m.WriteU32(o + ObjTransOff + 8, (uint)_oz);
            m.WriteU32(o + ObjVelXOff, (uint)_ovx);
            m.WriteU32(o + ObjVelZOff, (uint)_ovz);
            if (_haveTransY)
            {
                m.WriteU32(o + ObjTransOff + 4, (uint)_yTrans);
                m.WriteU32(o + ObjVelYOff, (uint)_vyTrans);
            }
            return;
        }

        if (_exactTicks < RefTicks - 0.01)
        {
            m.WriteU32(o + ObjTransOff, (uint)ScaleExact(_ox, (int)m.ReadU32(o + ObjTransOff), Teleport));
            m.WriteU32(o + ObjTransOff + 8, (uint)ScaleExact(_oz, (int)m.ReadU32(o + ObjTransOff + 8), Teleport));
            m.WriteU32(o + ObjVelXOff, (uint)ScaleExact(_ovx, (int)m.ReadU32(o + ObjVelXOff), VelTeleport));
            m.WriteU32(o + ObjVelZOff, (uint)ScaleExact(_ovz, (int)m.ReadU32(o + ObjVelZOff), VelTeleport));
            int speedTo = (int)m.ReadU32(o + ObjSpeedOff);
            if (speedTo > 4 || speedTo < -4)
                m.WriteU32(o + ObjSpeedOff, (uint)ScaleExact(_ospeed, speedTo, Teleport));
            m.WriteU32(o + ObjRotOff, (uint)ScaleAng(_orx, (int)m.ReadU32(o + ObjRotOff)));
            m.WriteU32(o + ObjRotOff + 4, (uint)ScaleAng(_ory, (int)m.ReadU32(o + ObjRotOff + 4)));
            m.WriteU32(o + ObjRotOff + 8, (uint)ScaleAng(_orz, (int)m.ReadU32(o + ObjRotOff + 8)));
        }

        if (!_haveTransY) return;

        int yPhys = (int)m.ReadU32(o + ObjTransOff + 4);
        uint statusA = m.ReadU32(o + ObjStatusAOff);
        int y = _yTrans + (int)Math.Round(_vyTrans * _exactTicks / 1024.0);
        int vy = _vyTrans - (int)Math.Round(4000.0 * _exactTicks);
        if (vy < -0x2EE000) vy = -0x2EE000;

        bool landed = (statusA & FlagGroundLand) != 0;
        if (landed && y > yPhys + 0x400)
            m.WriteU32(o + ObjStatusAOff, statusA & ~FlagGroundLand);
        else if (landed)
        {
            m.WriteU32(o + ObjTransOff + 4, (uint)yPhys);
            m.WriteU32(o + ObjVelYOff, 0);
            RejectCrateEmbed(m);
            return;
        }

        m.WriteU32(o + ObjTransOff + 4, (uint)y);
        m.WriteU32(o + ObjVelYOff, (uint)vy);
        if (vy > 0)
        {
            statusA = m.ReadU32(o + ObjStatusAOff);
            m.WriteU32(o + ObjStatusAOff, statusA & ~FlagGroundLand);
        }
        RejectCrateEmbed(m);
    }

    /// <summary>
/// Guest just ran a full 34-tick step (StopAtWalls needs a move larger
/// than one 2048-unit bitmap cell). Keep dt/34 of pos/vel/rot with a
/// remainder (Round of a 0.5-cell residual was ±1 world unit every
/// present — ears vibrate against a wall, hog intro mesh pops).
/// Leave anim_frame alone — GOOL waits on draw_stamp/34.
    /// </summary>
    static void FinishPacedScale(IMemory m)
    {
        if (!_haveObj || !_crashObj || _objScaled) return;
        try
        {
            _objScaled = true;
            _crashDidScale = true;
            if (IsLandLockedState(m, _obj))
            {
                ClearCrashScaleFrac();
                return;
            }
            if (_crashAir)
            {
                ClearCrashScaleFrac();
                FinishJumpScale(m);
                RideAfterCrash(m);
                return;
            }
            if (_exactTicks <= 0)
            {
                RestorePaced(m);
                return;
            }
            if (_exactTicks >= RefTicks - 0.01)
            {
                ClearCrashScaleFrac();
                return;
            }

            uint o = _obj;
            // Spawn CODE sets pathprog from z then calcpath. Lerping XZ from
            // the death pose toward that snap leaves Crash beside the path
            // whenever dt&lt;34 (uncapped). At a 30 Hz hitch the lerp is off
            // so the checkpoint looks correct — same 60-only trap.
            if (_crashHog && IsFirstFrame(m, o))
            {
                _hogPathFrac = 0;
                _hogTrotFracX = 0;
                _hogTrotFracY = 0;
                _hogTrotFracZ = 0;
                Array.Clear(_hogMemFrac);
                ClearCrashScaleFrac();
                if (_haveTransY)
                    FinishHogJumpY(m);
                RejectCrateEmbed(m);
                return;
            }
            m.WriteU32(o + ObjTransOff, (uint)KeepCrashDelta(_ox, (int)m.ReadU32(o + ObjTransOff), Teleport, ref _crashFracTX));
            m.WriteU32(o + ObjTransOff + 8, (uint)KeepCrashDelta(_oz, (int)m.ReadU32(o + ObjTransOff + 8), Teleport, ref _crashFracTZ));

            bool hogJumpY = _crashHog && _haveTransY;
            int yTo = (int)m.ReadU32(o + ObjTransOff + 4);
            if (!hogJumpY)
                m.WriteU32(o + ObjTransOff + 4, (uint)KeepCrashDelta(_oy, yTo, VelTeleport, ref _crashFracTY));
            m.WriteU32(o + ObjVelXOff, (uint)KeepCrashDelta(_ovx, (int)m.ReadU32(o + ObjVelXOff), VelTeleport, ref _crashFracVX));
            int vyTo = (int)m.ReadU32(o + ObjVelYOff);
            if (!hogJumpY)
                m.WriteU32(o + ObjVelYOff, (uint)KeepCrashDelta(_ovy, vyTo, VelTeleport, ref _crashFracVY));
            m.WriteU32(o + ObjVelZOff, (uint)KeepCrashDelta(_ovz, (int)m.ReadU32(o + ObjVelZOff), VelTeleport, ref _crashFracVZ));
            int speedTo = (int)m.ReadU32(o + ObjSpeedOff);
            if (_crashObj && speedTo <= 4 && speedTo >= -4)
                m.WriteU32(o + ObjSpeedOff, (uint)speedTo);
            else
                m.WriteU32(o + ObjSpeedOff, (uint)KeepCrashDelta(_ospeed, speedTo, Teleport, ref _crashFracSp));
            m.WriteU32(o + ObjRotOff, (uint)KeepCrashAng(_orx, (int)m.ReadU32(o + ObjRotOff), ref _crashFracRX));
            m.WriteU32(o + ObjRotOff + 4, (uint)KeepCrashAng(_ory, (int)m.ReadU32(o + ObjRotOff + 4), ref _crashFracRY));
            m.WriteU32(o + ObjRotOff + 8, (uint)KeepCrashAng(_orz, (int)m.ReadU32(o + ObjRotOff + 8), ref _crashFracRZ));
            if (_crashHog)
                FinishWarthogScale(m);
            if (hogJumpY)
                FinishHogJumpY(m);

            if (_crashObj && !hogJumpY)
            {
                uint statusA = m.ReadU32(o + ObjStatusAOff);
                if ((statusA & FlagGroundLand) != 0 && vyTo > 0 && yTo > _oy + 0x100)
                    m.WriteU32(o + ObjStatusAOff, statusA & ~FlagGroundLand);
            }
            RejectCrateEmbed(m);
            RideAfterCrash(m);

            if (++_paceLog >= 90)
            {
                _paceLog = 0;
                PaceLog($"crash dt {_exactTicks:0.00}/{RefTicks} ticks host={_frameTicks}");
            }
        }
        catch
        {
            // object freed
        }
    }

    static bool AabbOverlap(int ax1, int ay1, int az1, int ax2, int ay2, int az2,
        int bx1, int by1, int bz1, int bx2, int by2, int bz2)
    {
        return ax1 < bx2 && ax2 > bx1 && ay1 < by2 && ay2 > by1 && az1 < bz2 && az2 > bz1;
    }

    static bool IsBoxOrPusherWall(IMemory m, uint obj)
    {
        if ((obj & 0xFF000000u) != 0x80000000u) return false;
        try
        {
            if (!TryReadGoolClass(m, obj, out uint type, out _)
                || (type != GoolTypeBox && !IsLostCityPusher(m, obj, type)))
                return false;
            uint b = m.ReadU32(obj + ObjStatusBOff);
            return (b & FlagSolidSides) != 0
                && (type == GoolTypeBox || (b & FlagCollidable) != 0);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Side-stuck in a BoxC wall or Lost City pusher. Standing on the lid is
    /// not a hit: Y is the shallow axis. Other scenery AABBs are ignored.
    /// </summary>
    static bool CrateWallHit(int x1, int y1, int z1, int x2, int y2, int z2,
        int bx1, int by1, int bz1, int bx2, int by2, int bz2)
    {
        if (!AabbOverlap(x1, y1, z1, x2, y2, z2, bx1, by1, bz1, bx2, by2, bz2))
            return false;
        int penX = Math.Min(x2 - bx1, bx2 - x1);
        int penY = Math.Min(y2 - by1, by2 - y1);
        int penZ = Math.Min(z2 - bz1, bz2 - z1);
        if (penX <= EmbedSlop || penY <= 0 || penZ <= EmbedSlop)
            return false;
        int penU = by2 - y1;
        int penD = y2 - by1;
        if (penY <= penX && penY <= penZ && penU <= penD)
            return false;
        return true;
    }

    static bool CrashInCrateWall(IMemory m, int x, int y, int z)
    {
        int x1 = x + (int)m.ReadU32(_obj + ObjBoundOff);
        int y1 = y + (int)m.ReadU32(_obj + ObjBoundOff + 4);
        int z1 = z + (int)m.ReadU32(_obj + ObjBoundOff + 8);
        int x2 = x + (int)m.ReadU32(_obj + ObjBoundOff + 12);
        int y2 = y + (int)m.ReadU32(_obj + ObjBoundOff + 16);
        int z2 = z + (int)m.ReadU32(_obj + ObjBoundOff + 20);

        foreach (var kv in _lastBound)
        {
            if (kv.Key == _obj || !IsBoxOrPusherWall(m, kv.Key)) continue;
            BoundSnap s = kv.Value;
            if (CrateWallHit(x1, y1, z1, x2, y2, z2, s.X1, s.Y1, s.Z1, s.X2, s.Y2, s.Z2))
                return true;
        }

        int n = (int)m.ReadU32(ObjectBoundCountAddr);
        if (n < 0) return false;
        if (n > ObjectBoundMax) n = ObjectBoundMax;
        for (int i = 0; i < n; i++)
        {
            uint slot = ObjectBoundsAddr + (uint)i * 28u;
            uint other = m.ReadU32(slot + 24);
            if (other == _obj || !IsBoxOrPusherWall(m, other)) continue;
            if (CrateWallHit(x1, y1, z1, x2, y2, z2,
                    (int)m.ReadU32(slot), (int)m.ReadU32(slot + 4), (int)m.ReadU32(slot + 8),
                    (int)m.ReadU32(slot + 12), (int)m.ReadU32(slot + 16), (int)m.ReadU32(slot + 20)))
                return true;
        }
        return false;
    }

    static void RejectCrateEmbed(IMemory m)
    {
        if (CrashSpinning(m, _obj)) return;
        int x = (int)m.ReadU32(_obj + ObjTransOff);
        int y = (int)m.ReadU32(_obj + ObjTransOff + 4);
        int z = (int)m.ReadU32(_obj + ObjTransOff + 8);
        if (!CrashInCrateWall(m, x, y, z)) return;
        if (!CrashInCrateWall(m, _ox, _oy, _oz))
        {
            m.WriteU32(_obj + ObjTransOff, (uint)_ox);
            m.WriteU32(_obj + ObjTransOff + 8, (uint)_oz);
            return;
        }
        EjectFromCrateSides(m, x, y, z);
    }

    static void EjectFromCrateSides(IMemory m, int x, int y, int z)
    {
        int x1 = x + (int)m.ReadU32(_obj + ObjBoundOff);
        int y1 = y + (int)m.ReadU32(_obj + ObjBoundOff + 4);
        int z1 = z + (int)m.ReadU32(_obj + ObjBoundOff + 8);
        int x2 = x + (int)m.ReadU32(_obj + ObjBoundOff + 12);
        int y2 = y + (int)m.ReadU32(_obj + ObjBoundOff + 16);
        int z2 = z + (int)m.ReadU32(_obj + ObjBoundOff + 20);

        foreach (var kv in _lastBound)
        {
            if (kv.Key == _obj || !IsBoxOrPusherWall(m, kv.Key)) continue;
            BoundSnap s = kv.Value;
            if (!CrateWallHit(x1, y1, z1, x2, y2, z2, s.X1, s.Y1, s.Z1, s.X2, s.Y2, s.Z2))
                continue;
            int penL = x2 - s.X1;
            int penR = s.X2 - x1;
            int penN = z2 - s.Z1;
            int penF = s.Z2 - z1;
            int ax = Math.Min(penL, penR);
            int az = Math.Min(penN, penF);
            if (ax <= az)
                x += penL < penR ? -penL : penR;
            else
                z += penN < penF ? -penN : penF;
            m.WriteU32(_obj + ObjTransOff, (uint)x);
            m.WriteU32(_obj + ObjTransOff + 8, (uint)z);
            return;
        }
    }

    static void RestorePaced(IMemory m)
    {
        uint o = _obj;
        m.WriteU32(o + ObjTransOff, (uint)_ox);
        m.WriteU32(o + ObjTransOff + 4, (uint)_oy);
        m.WriteU32(o + ObjTransOff + 8, (uint)_oz);
        m.WriteU32(o + ObjVelXOff, (uint)_ovx);
        m.WriteU32(o + ObjVelYOff, (uint)_ovy);
        m.WriteU32(o + ObjVelZOff, (uint)_ovz);
        m.WriteU32(o + ObjSpeedOff, (uint)_ospeed);
        m.WriteU32(o + ObjRotOff, (uint)_orx);
        m.WriteU32(o + ObjRotOff + 4, (uint)_ory);
        m.WriteU32(o + ObjRotOff + 8, (uint)_orz);
        if (_crashHog)
        {
            m.WriteU32(o + ObjPathProgOff, (uint)_opath);
            m.WriteU32(o + ObjTrotOff, (uint)_otrotX);
            m.WriteU32(o + ObjTrotOff + 4, (uint)_otrotY);
            m.WriteU32(o + ObjTrotOff + 8, (uint)_otrotZ);
            for (int i = 0; i < HogMemCount; i++)
                m.WriteU32(o + ObjMemOff + (uint)i * 4u, (uint)_hogMem[i]);
        }
    }

    /// <summary>
    /// Wumpa (and other sprites) do <c>animframe += 1</c> every trans. Vertex
    /// enemies wait on draw_stamp/34 — do not lerp those or the mesh skips.
    /// </summary>
    static void PaceSpriteAnim(IMemory m)
    {
        if (!_haveObj || _crashObj || _frameTicks >= RefTicks) return;
        try
        {
            uint seq = m.ReadU32(_obj + ObjAnimSeqOff);
            if ((seq & 0xFF000000u) != 0x80000000u) return;
            if (m.ReadU8(seq) != AnimTypeSprite) return;

            int to = (int)m.ReadU32(_obj + ObjAnimFrameOff);
            int from = _oanim;
            if (to > AnimFrameCap || from > AnimFrameCap)
            {
                m.WriteU32(_obj + ObjAnimFrameOff, 0);
                return;
            }
            int d = to - from;
            if (d > 0x180 || d < -0x180) return;
            if (d == 0) return;
            int s = (int)Math.Round(d * _exactTicks / RefTicks);
            if (s == 0) return;
            int n = from + s;
            if (n > AnimFrameCap) n = AnimFrameCap;
            if (n < 0) n = 0;
            m.WriteU32(_obj + ObjAnimFrameOff, (uint)n);
        }
        catch
        {
            // object freed
        }
    }

    /// <summary>
    /// Category 0x600 trans is one original 30 Hz Euler/spd step. Run it
    /// with ticks=34 so spd is not 0, then keep wall dt/34 of every field
    /// (remainder carries sub-integer motion at uncapped). Not a 30 Hz skip
    /// and not a 60/120/240 table.
    /// Bound runs inside Update; Physics clears collider before Post. Always
    /// snapshot Crash — Pace keeps dt/34 of a real carry, no-ops if trans
    /// did not write him.
    /// </summary>
    static void SnapshotPlatform(IMemory m, uint obj)
    {
        try
        {
            ReadPlatVec(m, obj + ObjTransOff, PlatSlotTrans);
            ReadPlatVec(m, obj + ObjRotOff, PlatSlotRot);
            ReadPlatVec(m, obj + ObjScaleOff, PlatSlotScale);
            ReadPlatVec(m, obj + ObjVelXOff, PlatSlotVel);
            ReadPlatVec(m, obj + ObjTrotOff, PlatSlotTrot);
            ReadPlatVec(m, obj + ObjMiscOff, PlatSlotMisc);
            _platFrom[PlatSlotPath] = (int)m.ReadU32(obj + ObjPathProgOff);
            _platFrom[PlatSlotSpeed] = (int)m.ReadU32(obj + ObjSpeedOff);
            for (int i = 0; i < ObjMemCount; i++)
                _platFrom[PlatSlotMem + i] = (int)m.ReadU32(obj + ObjMemOff + (uint)i * 4u);
            _platCarry = false;
            uint crash = m.ReadU32(CrashPtrAddr);
            if (crash != 0 && (crash & 0xFF000000u) == 0x80000000u)
            {
                _platCarry = CrashStandingOnPlat(m, obj, crash) && !CrashLeftRide(m, obj, crash);
                if (_platCarry)
                {
                    ReadPlatVec(m, crash + ObjTransOff, PlatSlotCrash);
                    _platFrom[PlatSlotCrashTrot] = (int)m.ReadU32(crash + ObjTrotOff + 4);
                }
            }
        }
        catch
        {
            _platObj = false;
        }
    }

    static void ReadPlatVec(IMemory m, uint addr, int slot)
    {
        _platFrom[slot] = (int)m.ReadU32(addr);
        _platFrom[slot + 1] = (int)m.ReadU32(addr + 4);
        _platFrom[slot + 2] = (int)m.ReadU32(addr + 8);
    }

    static void PacePlatform(IMemory m)
    {
        if (_exactTicks >= RefTicks - 0.01) return;
        uint o = _obj;
        try
        {
            if (_platFrac.Count > 96)
                EvictDict(_platFrac, o);
            WritePlatVec(m, o + ObjTransOff, PlatSlotTrans, PlatDelta.Linear);
            WritePlatVec(m, o + ObjRotOff, PlatSlotRot, PlatDelta.Ang12);
            WritePlatVec(m, o + ObjScaleOff, PlatSlotScale, PlatDelta.Linear);
            WritePlatVec(m, o + ObjVelXOff, PlatSlotVel, PlatDelta.Linear);
            WritePlatVec(m, o + ObjTrotOff, PlatSlotTrot, PlatDelta.Ang12);
            WritePlatVec(m, o + ObjMiscOff, PlatSlotMisc, PlatDelta.Linear);
            WritePlatWord(m, o + ObjPathProgOff, PlatSlotPath, PlatDelta.Linear);
            WritePlatWord(m, o + ObjSpeedOff, PlatSlotSpeed, PlatDelta.Linear);
            for (int i = 0; i < ObjMemCount; i++)
                WritePlatWord(m, o + ObjMemOff + (uint)i * 4u, PlatSlotMem + i, PlatDelta.Auto);
            if (_platCarry)
            {
                uint crash = m.ReadU32(CrashPtrAddr);
                if (crash != 0 && (crash & 0xFF000000u) == 0x80000000u)
                {
                    if (TryReadGoolClass(m, o, out uint type, out _) && IsLostCityPusher(m, o, type))
                        for (int i = 0; i < 3; i++)
                            m.WriteU32(crash + ObjTransOff + (uint)i * 4u, (uint)(_platFrom[PlatSlotCrash + i]
                                + (int)m.ReadU32(o + ObjTransOff + (uint)i * 4u) - _platFrom[PlatSlotTrans + i]));
                    else
                        WritePlatVec(m, crash + ObjTransOff, PlatSlotCrash, PlatDelta.Linear);
                    WritePlatWord(m, crash + ObjTrotOff + 4, PlatSlotCrashTrot, PlatDelta.Ang12);
                }
            }
            if (_platLog < 6)
            {
                _platLog++;
                int y0 = _platFrom[PlatSlotTrans + 1];
                int y1 = (int)m.ReadU32(o + ObjTransOff + 4);
                PaceLog($"plat 0x{o:X8} y {y0}->{y1} dt={_exactTicks:0.00}");
            }
        }
        catch
        {
            // object freed
        }
    }

    static void WritePlatVec(IMemory m, uint addr, int slot, PlatDelta kind)
    {
        WritePlatWord(m, addr, slot, kind);
        WritePlatWord(m, addr + 4, slot + 1, kind);
        WritePlatWord(m, addr + 8, slot + 2, kind);
    }

    static void WritePlatWord(IMemory m, uint addr, int slot, PlatDelta kind)
    {
        int from = _platFrom[slot];
        int to = (int)m.ReadU32(addr);
        m.WriteU32(addr, (uint)KeepPlatDelta(_obj, slot, from, to, kind));
    }

    static bool IsGoolPtr(int v) => ((uint)v & 0xFF000000u) == 0x80000000u;

    static bool IsAng12Field(int from, int to) =>
        (uint)from <= 0x1FFFu && (uint)to <= 0x1FFFu;

    static bool IsDeg360Field(int from, int to) =>
        from >= 0 && from <= Deg360 && to >= 0 && to <= Deg360;

    static int KeepPlatDelta(uint obj, int slot, int from, int to, PlatDelta kind)
    {
        if (IsGoolPtr(from) || IsGoolPtr(to)) return to;

        long d;
        bool wrap88 = false;

        if (kind == PlatDelta.Ang12 && IsAng12Field(from, to))
        {
            d = AngDelta(from, to);
        }
        else
        {
            d = (long)to - from;
            // Orbit vectransf2 can exceed Teleport (4m×~22°) and still be
            // one 30 Hz step — keep dt/34. True warps are first-frame.
            int cap = kind == PlatDelta.Linear ? VelTeleport : Teleport;
            if (d > cap || d < -cap) return to;
            if (Math.Abs(from) <= 16 && Math.Abs(to) <= 16 && Math.Abs((int)d) <= 16)
                return to;
            // Real 0↔360.0 step only. Y/speed in this range must stay linear
            // or a 60000→20000 drop is treated as a full turn.
            if (kind != PlatDelta.Linear && IsDeg360Field(from, to)
                && (d > Deg180 || d < -Deg180))
            {
                if (d > Deg180) d -= Deg360;
                else d += Deg360;
                wrap88 = true;
            }
        }

        if (d == 0) return to;
        if (_exactTicks <= 0) return from;

        if (!_platFrac.TryGetValue(obj, out double[]? frac) || frac.Length != PlatSlotCount)
        {
            frac = new double[PlatSlotCount];
            _platFrac[obj] = frac;
        }

        double step = d * _exactTicks / RefTicks + frac[slot];
        int kept = (int)Math.Truncate(step);
        frac[slot] = step - kept;
        long r = (long)from + kept;

        if (kind == PlatDelta.Ang12 && IsAng12Field(from, to))
            return (int)r & 0xFFF;

        if (wrap88)
        {
            // GOOL already crossed into quadrant 4 (359…). Keep that
            // representation so gravity sign matches the Euler.
            r %= Deg360;
            if (r < 0) r += Deg360;
            return (int)r;
        }

        // Sub-zero leftover of a Q1 step: writing 359.99 flips accel sign.
        if (kind != PlatDelta.Linear && IsDeg360Field(from, to))
        {
            if (r < 0)
            {
                frac[slot] += r;
                return 0;
            }
            if (r >= Deg360)
            {
                frac[slot] += r - Deg360;
                return 0;
            }
        }

        return (int)r;
    }

    static void FinishWarthogScale(IMemory m)
    {
        uint o = _obj;
        m.WriteU32(o + ObjPathProgOff, (uint)KeepHogPath(_opath, (int)m.ReadU32(o + ObjPathProgOff)));
        FinishWarthogSteer(m);
        for (int i = 0; i < HogMemCount; i++)
        {
            int from = _hogMem[i];
            int to = (int)m.ReadU32(o + ObjMemOff + (uint)i * 4u);
            long d = (long)to - from;
            if (d == 0 || d > Teleport || d < -Teleport) continue;
            if (IsGoolPtr(from) || IsGoolPtr(to)) continue;
            if (d < HogMemMinStep && d > -HogMemMinStep) continue;
            m.WriteU32(o + ObjMemOff + (uint)i * 4u,
                (uint)KeepHogDelta(from, to, ref _hogMemFrac[i], ang: false));
        }
    }

    /// <summary>
    /// Hog jump trans did a 34-tick hang <c>spd(vely)</c> and physics a
    /// 34-tick displace. ScaleExact on Y is two half-steps at 60. Takeoff
    /// / bounce is a SET (keep vy). Hang is dt/34 of that spd add. Then
    /// y += vy×dt/1024, gravity 4000×dt — same order as the guest, any fps.
    /// </summary>
    static void FinishHogJumpY(IMemory m)
    {
        uint o = _obj;
        int vyAfter = _vyTrans;
        int dvy = vyAfter - _ovy;
        // Takeoff/bounce SET is millions; 34-tick hang spd is much smaller.
        // Scaling the SET is the 60-only half-impulse. Keep it; scale hang.
        int vyHang = IsFirstFrame(m, o) || dvy > 0x80000 || dvy < -0x80000
            ? vyAfter
            : _ovy + (int)Math.Round(dvy * _exactTicks / RefTicks);
        int y = _yTrans + (int)Math.Round(vyHang * _exactTicks / 1024.0);
        int vy = vyHang - (int)Math.Round(4000.0 * _exactTicks);
        if (vy < -0x2EE000) vy = -0x2EE000;

        int yPhys = (int)m.ReadU32(o + ObjTransOff + 4);
        uint statusA = m.ReadU32(o + ObjStatusAOff);
        bool landed = (statusA & FlagGroundLand) != 0;
        if (landed && y > yPhys + 0x400)
            m.WriteU32(o + ObjStatusAOff, statusA & ~FlagGroundLand);
        else if (landed)
        {
            m.WriteU32(o + ObjTransOff + 4, (uint)yPhys);
            m.WriteU32(o + ObjVelYOff, 0);
            return;
        }

        m.WriteU32(o + ObjTransOff + 4, (uint)y);
        m.WriteU32(o + ObjVelYOff, (uint)vy);
        if (vy > 0)
        {
            statusA = m.ReadU32(o + ObjStatusAOff);
            m.WriteU32(o + ObjStatusAOff, statusA & ~FlagGroundLand);
        }
    }

    static int KeepHogPath(int from, int to)
    {
        long d = (long)to - from;
        // Spawn/checkpoint writes (73.0 / 138.0) are snaps, not spd(…, 4).
        if (d > 0x1000 || d < -0x1000) return to;
        return KeepHogDelta(from, to, ref _hogPathFrac, ang: false);
    }

    static void FinishWarthogSteer(IMemory m)
    {
        uint o = _obj;
        m.WriteU32(o + ObjTrotOff, (uint)KeepHogSteer(_otrotX, (int)m.ReadU32(o + ObjTrotOff), ref _hogTrotFracX));
        m.WriteU32(o + ObjTrotOff + 4, (uint)KeepHogSteer(_otrotY, (int)m.ReadU32(o + ObjTrotOff + 4), ref _hogTrotFracY));
        m.WriteU32(o + ObjTrotOff + 8, (uint)KeepHogSteer(_otrotZ, (int)m.ReadU32(o + ObjTrotOff + 8), ref _hogTrotFracZ));
    }

    /// <summary>
    /// <c>troty += 15deg</c> is per present. 12-bit 15deg is ~171; 8.8 15deg
    /// is 3840, which AngDelta treats as a wrap to -256 and then <c>&amp; 0xFFF</c>
    /// corrupts 8.8. Linear when the raw step is larger than 180° 12-bit.
    /// </summary>
    static int KeepHogSteer(int from, int to, ref double frac)
    {
        long raw = (long)to - from;
        if (raw > 0x800 || raw < -0x800)
            return KeepHogDelta(from, to, ref frac, ang: false);
        return KeepHogDelta(from, to, ref frac, ang: true);
    }

    static int KeepHogDelta(int from, int to, ref double frac, bool ang)
    {
        if (IsGoolPtr(from) || IsGoolPtr(to)) return to;
        long d = ang ? AngDelta(from, to) : (long)to - from;
        if (d > Teleport || d < -Teleport) return to;
        if (d == 0) return to;
        if (_exactTicks <= 0) return from;
        double step = d * _exactTicks / RefTicks + frac;
        int kept = (int)Math.Truncate(step);
        frac = step - kept;
        int r = from + kept;
        return ang ? r & 0xFFF : r;
    }

    /// <summary>
    /// Path GOOL sometimes does <c>x += vel</c> every trans (no ticks). A
    /// 30 Hz world step is large; a tick-scaled path step at 270 FPS is not.
    /// Keep only dt/34 of the leftover after vel*ticks.
    /// </summary>
    static void PaceExtraTrans(IMemory m)
    {
        if (!_haveObj || _crashObj || _solidObj || _frameTicks >= RefTicks) return;
        try
        {
            int x = (int)m.ReadU32(_obj + ObjTransOff);
            int z = (int)m.ReadU32(_obj + ObjTransOff + 8);
            long dx = (long)x - _ox;
            long dz = (long)z - _oz;
            if (dx > Teleport || dx < -Teleport || dz > Teleport || dz < -Teleport)
                return;
            int expectX = (int)((long)_ovx * _frameTicks / 1024);
            int expectZ = (int)((long)_ovz * _frameTicks / 1024);
            int extraX = (int)dx - expectX;
            int extraZ = (int)dz - expectZ;
            if (Math.Abs(extraX) < ExtraTransMin && Math.Abs(extraZ) < ExtraTransMin)
                return;
            if (Math.Abs(extraX) >= ExtraTransMin)
            {
                int kept = (int)Math.Round(extraX * _exactTicks / RefTicks);
                m.WriteU32(_obj + ObjTransOff, (uint)(_ox + expectX + kept));
            }
            if (Math.Abs(extraZ) >= ExtraTransMin)
            {
                int kept = (int)Math.Round(extraZ * _exactTicks / RefTicks);
                m.WriteU32(_obj + ObjTransOff + 8, (uint)(_oz + expectZ + kept));
            }
        }
        catch
        {
            // object freed
        }
    }

    static void SnapshotObject(IMemory m, uint obj)
    {
        if ((obj & 0xFF000000u) != 0x80000000u) return;
        try
        {
            uint type = m.ReadU32(obj);
            if (type is 0 or 2) return;
            _obj = obj;
            _crashObj = obj == m.ReadU32(CrashPtrAddr);
            _ox = (int)m.ReadU32(obj + ObjTransOff);
            _oy = (int)m.ReadU32(obj + ObjTransOff + 4);
            _oz = (int)m.ReadU32(obj + ObjTransOff + 8);
            _orx = (int)m.ReadU32(obj + ObjRotOff);
            _ory = (int)m.ReadU32(obj + ObjRotOff + 4);
            _orz = (int)m.ReadU32(obj + ObjRotOff + 8);
            _ovy = (int)m.ReadU32(obj + ObjVelYOff);
            _ovx = (int)m.ReadU32(obj + ObjVelXOff);
            _ovz = (int)m.ReadU32(obj + ObjVelZOff);
            _ospeed = (int)m.ReadU32(obj + ObjSpeedOff);
            _oanim = (int)m.ReadU32(obj + ObjAnimFrameOff);
            _crashHog = _crashObj && CrashOnWarthog(m, obj);
            if (_crashHog)
            {
                _opath = (int)m.ReadU32(obj + ObjPathProgOff);
                _otrotX = (int)m.ReadU32(obj + ObjTrotOff);
                _otrotY = (int)m.ReadU32(obj + ObjTrotOff + 4);
                _otrotZ = (int)m.ReadU32(obj + ObjTrotOff + 8);
                for (int i = 0; i < HogMemCount; i++)
                    _hogMem[i] = (int)m.ReadU32(obj + ObjMemOff + (uint)i * 4u);
            }
            else
            {
                _hogPathFrac = 0;
                _hogTrotFracX = 0;
                _hogTrotFracY = 0;
                _hogTrotFracZ = 0;
                Array.Clear(_hogMemFrac);
            }
            _haveObj = true;
        }
        catch
        {
            _haveObj = false;
        }
    }

    static void ClampAnimFrame(IMemory m, uint obj)
    {
        if ((obj & 0xFF000000u) != 0x80000000u) return;
        try
        {
            uint type = m.ReadU32(obj);
            if (type is 0 or 2) return;
            int anim = (int)m.ReadU32(obj + ObjAnimFrameOff);
            // GoolObjectTransform: svtx/sprite[anim_frame >> 8]. Do not wrap
            // by GOOL length — that byte is not valid on every seq during load.
            if (anim > AnimFrameCap || anim < 0)
                m.WriteU32(obj + ObjAnimFrameOff, 0);
        }
        catch
        {
            // object freed
        }
    }
}
