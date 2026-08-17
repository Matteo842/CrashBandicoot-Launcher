using System.Diagnostics;
using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace RecompOne.Runtime.Host;

/// <summary>
/// Wall-clock dt = seconds × 1020. Crash trans still sees 34 so hang-boost
/// <c>spd(vely, 5454)</c> matches the sogliola-era jump; we then scale only
/// that small vely delta by realDt/34 (the 10.5m impulse stays intact) and
/// restore trans before physics (real dt gravity). FLAG_2D keeps GOOL
/// trans/scale (moveto2d / *0.9) so HUD fruit can finish. Path objects see
/// 34 then lerp progress — including the PATH_END frame. Physics enemies
/// see real dt; fling/crate scale deltas are ScaleSoft (never min ±1).
/// </summary>
public static class FramePacing
{
    public const double TicksPerSecond = 17.0 * 60.0;
    public const uint RefTicks = 34u;
    const double HitchSeconds = RefTicks / TicksPerSecond;
    const double MinStepSeconds = 0.00025;

    public const uint TicksElapsedAddr = 0x80034520u;
    const uint GfxC1pAddr = 0x80058404u;
    const uint GfxC2pAddr = 0x80058408u;
    const uint GfxCurAddr = 0x8005840Cu;
    const uint TicksPerFrameOff = 0x84u;
    const double PadWindowSeconds = 0.003;

    const uint GoolUpdateObjectsAddr = 0x8001D5ECu;
    const uint GoolObjectUpdateAddr = 0x8001DA0Cu;
    const uint GoolObjectPhysicsAddr = 0x8001F30Cu;
    const uint GoolSeekAddr = 0x80024628u;
    const uint LevelUpdateAddr = 0x80025A60u;
    const uint CrashPtrAddr = 0x800566B4u;
    const uint CamZoneAddr = 0x80057914u;
    const uint CamPathAddr = 0x8005791Cu;
    const uint CamProgressAddr = 0x80057920u;

    const uint ObjTransOff = 0x80u;
    const uint ObjRotOff = 0x8Cu;
    const uint ObjScaleOff = 0x98u;
    const uint ObjVelYOff = 0xA8u;
    const uint ObjStatusAOff = 0xC8u;
    const uint ObjStatusBOff = 0xCCu;
    const uint ObjAnimFrameOff = 0x10Cu;
    const uint ObjPathProgOff = 0x114u;

    const uint FlagTransMotion = 0x40u;
    const uint FlagRotY = 0x1u;
    const uint Flag2D = 0x200u;
    const uint FlagRotX = 0x2000u;
    const uint FlagRotY2 = 0x80000u;
    const uint FlagPathEnd = 0x10u;

    const int Teleport = 0x80000;
    const int PathWrap = 0x8000;
    const int PathTransTeleport = 0x800000;
    const int ScaleSnap = 0x2000;
    // spd(vely, 5454) at ticks=34 is ~181. Jump CODE sets vely=10.5m (much larger).
    const int HangBoostAbsMax = 0x400;
    const int AnimFrameCap = 24 << 8;

    static readonly long IrqPeriod = Stopwatch.Frequency / 60;

    static uint _guestTicks;
    static uint _frameTicks = 34;
    static double _tickFrac;
    static long _simTs;
    static long _lastPrimaryEndTs;
    static bool _didPrimary;
    static long _irqTs;
    static bool _clockArmed;
    static bool _hooksInstalled;

    static uint _obj;
    static int _ox, _oy, _oz, _orx, _ory, _orz, _osx, _osy, _osz, _oanim;
    static int _ovy, _opath;
    static bool _haveObj;
    static bool _physicsBlended;
    static bool _crashObj;

    public static bool ForceOriginal { get; set; }

    public static uint LastFrameTicks => _frameTicks;

    /// <summary>Wall milliseconds used for the last sim step (after hitch clamp).</summary>
    public static double LastDtMs => _frameTicks * 1000.0 / TicksPerSecond;

    public static bool WantsUnlock =>
        !ForceOriginal && ConfigManager.View.FrameRate != ViewConfig.FrameRateOriginal;

    public static bool IsActive(IMemory? m)
    {
        if (!WantsUnlock || m == null) return false;
        try
        {
            uint id = m.ReadU32(Catalog.LevelIdAddr);
            return Catalog.Levels.AllowsUnlockedFps(id);
        }
        catch
        {
            return false;
        }
    }

    public static void Reset()
    {
        _guestTicks = 0;
        _frameTicks = 34;
        _tickFrac = 0;
        _simTs = 0;
        _lastPrimaryEndTs = 0;
        _didPrimary = false;
        _irqTs = 0;
        _clockArmed = false;
        _haveObj = false;
        _physicsBlended = false;
        _crashObj = false;
    }

    public static bool IsPadCall()
    {
        if (!_didPrimary || _lastPrimaryEndTs == 0) return false;
        double dt = (Stopwatch.GetTimestamp() - _lastPrimaryEndTs) / (double)Stopwatch.Frequency;
        return dt < PadWindowSeconds;
    }

    public static void MarkPrimaryEnd()
    {
        _didPrimary = true;
        _lastPrimaryEndTs = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Sim dt from wall time since the last primary present. The FrameRate
    /// combo only caps refresh — 120 Hz with a 60 Hz present still uses 17 ticks.
    /// A hitch longer than one original 30 Hz frame is dropped, not caught up.
    /// </summary>
    public static uint AdvanceWallClock(IMemory m)
    {
        long now = Stopwatch.GetTimestamp();
        if (!_clockArmed)
        {
            try { _guestTicks = m.ReadU32(TicksElapsedAddr); }
            catch { _guestTicks = 0; }
            _simTs = now;
            _tickFrac = 0;
            _clockArmed = true;
            _frameTicks = 17;
            PatchTicksPerFrame(m);
            return _guestTicks;
        }

        double sec = (now - _simTs) / (double)Stopwatch.Frequency;
        if (sec < MinStepSeconds)
        {
            PatchTicksPerFrame(m);
            return _guestTicks;
        }

        _simTs = now;
        if (sec > HitchSeconds)
            sec = HitchSeconds;

        _tickFrac += sec * TicksPerSecond;
        uint dt = (uint)Math.Floor(_tickFrac);
        _tickFrac -= dt;
        if (dt < 1) dt = 1;
        if (dt > RefTicks) dt = RefTicks;

        _frameTicks = dt;
        _guestTicks += dt;
        PatchTicksPerFrame(m);
        return _guestTicks;
    }

    public static void PatchTicksPerFrame(IMemory m)
    {
        if (!IsActive(m)) return;
        WriteAllTicks(m, _frameTicks);
    }

    static void WriteAllTicks(IMemory m, uint ticks)
    {
        WriteTicks(m, GfxC1pAddr, ticks);
        WriteTicks(m, GfxC2pAddr, ticks);
        WriteTicks(m, GfxCurAddr, ticks);
    }

    static void WriteTicks(IMemory m, uint ptrAddr, uint ticks)
    {
        try
        {
            uint ctx = m.ReadU32(ptrAddr);
            if ((ctx & 0xFF000000u) != 0x80000000u) return;
            m.WriteU32(ctx + TicksPerFrameOff, ticks);
        }
        catch
        {
            // overlay swap / unmapped
        }
    }

    public static uint TryReadGuestTicksPerFrame(IMemory? m)
    {
        if (m == null) return 0;
        try
        {
            uint ctx = m.ReadU32(GfxC1pAddr);
            if ((ctx & 0xFF000000u) != 0x80000000u) return 0;
            return m.ReadU32(ctx + TicksPerFrameOff);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Original 30 FPS: one IRQ per present. Unlocked 120/240: extra IRQs so
    /// the sequencer stays at 60 Hz. Never used to advance ticks_elapsed.
    /// </summary>
    public static void PulseVblankIrq()
    {
        if (!IsActive(Runtime.Mem))
        {
            Runtime.DispatchIrq(0);
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (_irqTs == 0)
        {
            _irqTs = now;
            Runtime.DispatchIrq(0);
            return;
        }

        int n = 0;
        while (now - _irqTs >= IrqPeriod && n < 2)
        {
            _irqTs += IrqPeriod;
            Runtime.DispatchIrq(0);
            n++;
        }

        if (n == 0 && now - _irqTs > IrqPeriod * 8)
            _irqTs = now;
    }

    public static void NoteVblankIrq() => _irqTs = Stopwatch.GetTimestamp();

    public static void InstallGameHooks()
    {
        if (_hooksInstalled) return;
        _hooksInstalled = true;

        Dispatcher.CallPre = OnCallPre;
        Dispatcher.CallPost = OnCallPost;

        HookPre(GoolUpdateObjectsAddr, PreUpdateObjects);
        HookPre(GoolObjectPhysicsAddr, PrePhysics);
        HookPre(LevelUpdateAddr, PreLevelUpdate);
        HookPre(GoolSeekAddr, PreGoolSeek);
    }

    static void HookPre(uint addr, Func<CpuContext, IMemory, bool> pre)
    {
        var mi = SymbolRegistry.Resolve("main", null, addr);
        if (mi == null)
        {
            Console.Error.WriteLine($"[FramePacing] no func 0x{addr:X8}");
            return;
        }
        HookManager.AddPre(mi, pre);
    }

    static bool PreUpdateObjects(CpuContext c, IMemory m)
    {
        if (IsActive(m))
            PatchTicksPerFrame(m);
        return true;
    }

    static bool OnCallPre(uint addr, CpuContext c, IMemory m)
    {
        if (addr != GoolObjectUpdateAddr) return true;
        _haveObj = false;
        _physicsBlended = false;
        _crashObj = false;
        if (!IsActive(m)) return true;
        // Crash hang-boost runs in trans; gravity in physics. Trans stays at
        // 34 (the jump that worked during sogliola). PrePhysics scales vely
        // by realDt/34 so holding X is not a rocket. HUD/physics objects use
        // real dt directly (spd already includes ticks).
        if (WantsRealTicks(m, c.A0))
            WriteAllTicks(m, _frameTicks);
        else
            WriteAllTicks(m, RefTicks);
        SnapshotObject(m, c.A0);
        return true;
    }

    static void OnCallPost(uint addr, CpuContext c, IMemory m)
    {
        if (addr != GoolObjectUpdateAddr || _physicsBlended || !_haveObj) return;
        BlendObject(m);
        WriteAllTicks(m, _frameTicks);
    }

    static bool PrePhysics(CpuContext c, IMemory m)
    {
        if (IsActive(m))
        {
            if (_haveObj && c.A0 == _obj)
            {
                if (_crashObj)
                    FinishCrashTrans(m);
                else
                    BlendObject(m);
            }
            WriteAllTicks(m, _frameTicks);
            _physicsBlended = _haveObj && c.A0 == _obj;
        }
        return true;
    }

    /// <summary>
    /// Same path: scale the progress step so CamFollow / auto-cam move at 30 Hz
    /// wall speed. ZonePathProgressToLoc then builds a consistent pose — no Euler lerp.
    /// Zone/path changes are teleports; leave those alone.
    /// </summary>
    static bool PreLevelUpdate(CpuContext c, IMemory m)
    {
        if (!IsActive(m) || _frameTicks >= RefTicks) return true;
        try
        {
            if (c.A0 != m.ReadU32(CamZoneAddr) || c.A1 != m.ReadU32(CamPathAddr))
                return true;
            int cur = (int)m.ReadU32(CamProgressAddr);
            int req = (int)c.A2;
            int d = req - cur;
            if (d == 0) return true;
            c.A2 = (uint)(cur + ScaleStep(d));
        }
        catch
        {
            // zone swap
        }
        return true;
    }

    static bool PreGoolSeek(CpuContext c, IMemory m)
    {
        if (!IsActive(m) || _frameTicks >= RefTicks) return true;
        // Object trans is ticks=34 + BlendObject, or real-dt HUD. Scaling seek
        // again would double-dt fruit and turtles.
        if (_haveObj) return true;
        c.A2 = (uint)ScaleStep((int)c.A2);
        return true;
    }

    static bool WantsRealTicks(IMemory m, uint obj)
    {
        if ((obj & 0xFF000000u) != 0x80000000u) return false;
        try
        {
            if (obj == m.ReadU32(CrashPtrAddr)) return false;
            uint statusB = m.ReadU32(obj + ObjStatusBOff);
            return (statusB & (Flag2D | FlagTransMotion)) != 0;
        }
        catch
        {
            return false;
        }
    }

    static int ScaleStep(int step)
    {
        if (step == 0) return 0;
        long n = (long)step * _frameTicks;
        int s = (int)((n + (n >= 0 ? RefTicks / 2 : -(RefTicks / 2))) / RefTicks);
        if (s == 0) return step > 0 ? 1 : -1;
        return s;
    }

    static int ScaleSoft(int step)
    {
        if (step == 0) return 0;
        long n = (long)step * _frameTicks;
        return (int)((n + (n >= 0 ? RefTicks / 2 : -(RefTicks / 2))) / RefTicks);
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
            _osx = (int)m.ReadU32(obj + ObjScaleOff);
            _osy = (int)m.ReadU32(obj + ObjScaleOff + 4);
            _osz = (int)m.ReadU32(obj + ObjScaleOff + 8);
            _ovy = (int)m.ReadU32(obj + ObjVelYOff);
            _oanim = (int)m.ReadU32(obj + ObjAnimFrameOff);
            _opath = (int)m.ReadU32(obj + ObjPathProgOff);
            _haveObj = true;
        }
        catch
        {
            _haveObj = false;
        }
    }

    static void FinishCrashTrans(IMemory m)
    {
        try
        {
            // Hang-boost is spd(vely, 5454) in trans at ticks=34. Scale that
            // small delta to realDt. The jump CODE write (vely = 10.5m) is an
            // impulse — scaling it by dt/34 is the short jump.
            int vy = (int)m.ReadU32(_obj + ObjVelYOff);
            long d = (long)vy - _ovy;
            int ad = d < 0 ? (int)-d : (int)d;
            if (ad > 0 && ad <= HangBoostAbsMax)
                m.WriteU32(_obj + ObjVelYOff, (uint)(_ovy + ScaleSoft((int)d)));

            KeepTrans(m, _obj + ObjTransOff, _ox);
            KeepTrans(m, _obj + ObjTransOff + 4, _oy);
            KeepTrans(m, _obj + ObjTransOff + 8, _oz);
        }
        catch
        {
            // object freed
        }
    }

    static void KeepTrans(IMemory m, uint addr, int from)
    {
        // Crash GOOL must not accumulate 30 Hz position steps at 180 fps.
        // Crate floors write a large Y snap — leave those.
        int to = (int)m.ReadU32(addr);
        long d = (long)to - from;
        if (d > Teleport || d < -Teleport)
            return;
        m.WriteU32(addr, (uint)from);
    }

    static void BlendObject(IMemory m)
    {
        if (!_haveObj || _crashObj || _frameTicks >= RefTicks) return;
        try
        {
            uint statusA = m.ReadU32(_obj + ObjStatusAOff);
            uint statusB = m.ReadU32(_obj + ObjStatusBOff);
            bool physicsMoves = (statusB & FlagTransMotion) != 0;
            bool physicsRots = (statusB & (FlagRotY | FlagRotX | FlagRotY2)) != 0;

            if ((statusB & Flag2D) != 0)
                return;

            int pathTo = (int)m.ReadU32(_obj + ObjPathProgOff);
            long pathD = (long)pathTo - _opath;
            bool pathMoved = pathD != 0;
            bool pathEnded = (statusA & FlagPathEnd) != 0;
            if (pathD > PathWrap || pathD < -PathWrap)
            {
                // loop/wrap — keep the snap
            }
            else if (pathMoved)
            {
                // Still lerp the frame that set PATH_END (full 34-tick step
                // would snap). Do not revert the flag itself.
                m.WriteU32(_obj + ObjPathProgOff, (uint)(_opath + ScaleSoft((int)pathD)));
            }

            if (!physicsMoves)
            {
                int transTeleport = pathMoved ? PathTransTeleport : Teleport;
                BlendTrans(m, _obj + ObjTransOff, _ox, transTeleport);
                BlendTrans(m, _obj + ObjTransOff + 4, _oy, transTeleport);
                BlendTrans(m, _obj + ObjTransOff + 8, _oz, transTeleport);
            }

            if (!physicsRots && !pathMoved)
            {
                BlendAng(m, _obj + ObjRotOff, _orx);
                BlendAng(m, _obj + ObjRotOff + 4, _ory);
                BlendAng(m, _obj + ObjRotOff + 8, _orz);
            }

            // Fling/crates: scalex -= 1.0S/30 each trans. ScaleSoft, never ±1.
            BlendScale(m, _obj + ObjScaleOff, _osx);
            BlendScale(m, _obj + ObjScaleOff + 4, _osy);
            BlendScale(m, _obj + ObjScaleOff + 8, _osz);

            if (!pathEnded)
                BlendAnim(m, _obj + ObjAnimFrameOff, _oanim);
        }
        catch
        {
            // object freed
        }
    }

    static void BlendScale(IMemory m, uint addr, int from)
    {
        int to = (int)m.ReadU32(addr);
        long d = (long)to - from;
        if (d == 0) return;
        if (d > ScaleSnap || d < -ScaleSnap)
            return;
        int s = ScaleSoft((int)d);
        if (s == 0) return;
        m.WriteU32(addr, (uint)(from + s));
    }

    static void BlendTrans(IMemory m, uint addr, int from, int teleport)
    {
        int to = (int)m.ReadU32(addr);
        long d = (long)to - from;
        if (d > teleport || d < -teleport)
            return;
        m.WriteU32(addr, (uint)(from + ScaleStep((int)d)));
    }

    static void BlendAng(IMemory m, uint addr, int from)
    {
        int to = (int)m.ReadU32(addr);
        int d = to - from;
        if (d > 0x800) d -= 0x1000;
        if (d < -0x800) d += 0x1000;
        if (d > Teleport || d < -Teleport)
            return;
        m.WriteU32(addr, (uint)(from + ScaleStep(d)));
    }

    static void BlendAnim(IMemory m, uint addr, int from)
    {
        int to = (int)m.ReadU32(addr);
        // GoolObjectTransform indexes svtx/sprite frames with anim_frame>>8.
        // Unscaled +=1 at 180 fps walks off the entry → unmapped 0x63CD0010.
        if (to > AnimFrameCap || from > AnimFrameCap)
        {
            m.WriteU32(addr, 0);
            return;
        }
        int d = to - from;
        if (d > 0x400 || d < -0x400)
            return;
        if (d == 0) return;
        int s = ScaleSoft(d);
        if (s == 0) return;
        int blended = from + s;
        if (blended > AnimFrameCap) blended = AnimFrameCap;
        if (blended < 0) blended = 0;
        m.WriteU32(addr, (uint)blended);
    }
}
