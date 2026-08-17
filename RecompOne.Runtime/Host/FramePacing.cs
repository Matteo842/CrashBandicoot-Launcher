using System.Diagnostics;
using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace RecompOne.Runtime.Host;

/// <summary>
/// Unlocked sim dt is wall seconds × 1020. Never branch on 60/120/240.
/// Crash trans+physics use a 34-tick step so StopAtWalls sees a move larger
/// than one 2048-unit bitmap cell; FinishCrashScale keeps dt/34 of pos/vel/rot.
/// Do not scale anim_frame or speed: GOOL waits on draw_stamp/34, and walk→idle
/// is <c>abs(speed)&gt;&gt;2</c>. HUD uses real ticks. Other objects: 34 + BlendObject.
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

    const uint GoolUpdateObjectsAddr = 0x8001D5ECu;
    const uint GoolObjectUpdateAddr = 0x8001DA0Cu;
    const uint GoolObjectTransformAddr = 0x8001DE78u;
    const uint GoolObjectPhysicsAddr = 0x8001F30Cu;
    const uint GoolSeekAddr = 0x80024628u;
    const uint LevelUpdateAddr = 0x80025A60u;
    const uint GpuUpdateAddr = 0x80016E5Cu;
    const uint NsInitAddr = 0x80015B58u;
    const uint DrawSkipAddr = 0x8005C54Cu;
    const uint GfxTransformSvtxAddr = 0x80018964u;
    const uint GfxTransformCvtxAddr = 0x80018A40u;
    const uint CrashPtrAddr = 0x800566B4u;
    const uint CamZoneAddr = 0x80057914u;
    const uint CamPathAddr = 0x8005791Cu;
    const uint CamProgressAddr = 0x80057920u;

    const uint ObjTransOff = 0x80u;
    const uint ObjRotOff = 0x8Cu;
    const uint ObjScaleOff = 0x98u;
    const uint ObjVelXOff = 0xA4u;
    const uint ObjVelYOff = 0xA8u;
    const uint ObjVelZOff = 0xACu;
    const uint ObjStatusAOff = 0xC8u;
    const uint ObjStatusBOff = 0xCCu;
    const uint ObjAnimSeqOff = 0x108u;
    const uint ObjAnimFrameOff = 0x10Cu;
    const uint ObjAnimCounterOff = 0x104u;
    const uint ObjPathProgOff = 0x114u;
    const uint ObjFloorYOff = 0x11Cu;
    const uint ObjSpeedOff = 0x124u;

    const uint FlagTransMotion = 0x40u;
    const uint FlagGravity = 0x20u;
    const uint FlagGroundLand = 0x1u;
    const uint FlagRotY = 0x1u;
    const uint Flag2D = 0x200u;
    const uint FlagRotX = 0x2000u;
    const uint FlagRotY2 = 0x80000u;
    const uint FlagPathEnd = 0x10u;
    const uint FlagStall = 0x10000000u;

    const int Teleport = 0x80000;
    const int PathWrap = 0x8000;
    const int PathTransTeleport = 0x800000;
    const int ScaleSnap = 0x2000;
    const int AnimFrameCap = 32 << 8;

    static readonly long IrqPeriod = Stopwatch.Frequency / 60;

    static uint _guestTicks;
    static uint _frameTicks = 34;
    static double _tickFrac;
    static double _exactTicks = 34;
    static double _stallFrac;
    static long _simTs;
    static long _irqTs;
    static bool _clockArmed;
    static bool _hooksInstalled;
    static int _vsyncsInGpu;
    static bool _inGpuUpdate;
    static bool _didPresentThisGpu;
    static bool _ticksTakenThisLoop;
    static bool _loggedUnlockGpu;
    static bool _inNsInit;
    /// <summary>Unlocked VSync only after a gameplay GpuUpdate with Crash spawned.</summary>
    static bool _levelReady;
    /// <summary>GpuUpdates to keep at 30 FPS after the first real DrawOTag.</summary>
    static int _holdLocked;

    static uint _obj;
    static int _ox, _oy, _oz, _orx, _ory, _orz, _osx, _osy, _osz, _oanim;
    static int _ovy, _ovx, _ovz, _ospeed, _opath;
    static int _paceLog;
    static bool _haveObj;
    static bool _physicsBlended;
    static bool _crashObj;
    static bool _crashScaled;

    public static bool ForceOriginal { get; set; }

    public static uint LastFrameTicks => _frameTicks;

    /// <summary>Wall milliseconds used for the last sim step (after hitch clamp).</summary>
    public static double LastDtMs => _exactTicks * 1000.0 / TicksPerSecond;

    public static bool WantsUnlock =>
        !ForceOriginal && ConfigManager.View.FrameRate != ViewConfig.FrameRateOriginal;

    /// <summary>
    /// While a level is still settling (skip frames + hold), cap presents at
    /// 60 Hz even if the user picked 240. Otherwise locked VSync is instant and
    /// spawn physics run at 4× (face-plant on the sand).
    /// </summary>
    public static bool NeedsOriginalVblank =>
        WantsUnlock && !_levelReady && !_inNsInit;

    public static bool IsActive(IMemory? m)
    {
        if (!WantsUnlock || m == null || _inNsInit || !_levelReady) return false;
        try
        {
            uint id = m.ReadU32(Catalog.LevelIdAddr);
            if (!Catalog.Levels.AllowsUnlockedFps(id))
            {
                _levelReady = false;
                return false;
            }
            return true;
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
        _exactTicks = 34;
        _stallFrac = 0;
        _simTs = 0;
        _irqTs = 0;
        _clockArmed = false;
        _haveObj = false;
        _physicsBlended = false;
        _crashObj = false;
        _crashScaled = false;
        _paceLog = 0;
        _vsyncsInGpu = 0;
        _inGpuUpdate = false;
        _didPresentThisGpu = false;
        _ticksTakenThisLoop = false;
        _loggedUnlockGpu = false;
        _inNsInit = false;
        _levelReady = false;
        _holdLocked = 0;
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            File.WriteAllText(Path.Combine(AppPaths.LogsDir, "pacing.txt"),
                $"{DateTime.Now:HH:mm:ss.fff} reset{Environment.NewLine}");
        }
        catch { /* ignore */ }
    }

    /// <summary>
    /// PsyQ VSync(0) calls the wait helper twice. The second helper is not the
    /// game's 30 Hz pad — skipping it skipped the only present and froze after
    /// unlock. Treat extra waits as pad only after this GpuUpdate already presented.
    /// </summary>
    public static bool IsPadCall() => _inGpuUpdate && _didPresentThisGpu;

    public static void NoteGpuVSync()
    {
        if (_inGpuUpdate)
            _vsyncsInGpu++;
    }

    public static void NoteGpuPresent() => _didPresentThisGpu = true;

    /// <summary>
    /// Sim dt from wall time since the last sim step. The FrameRate combo
    /// only caps refresh — never a per-Hz lookup. A hitch longer than one
    /// original 30 Hz frame is dropped, not caught up.
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
            _exactTicks = 1;
            _clockArmed = true;
            _frameTicks = 1;
            _ticksTakenThisLoop = true;
            PatchTicksPerFrame(m);
            return _guestTicks;
        }

        // One dt per game loop. Extra PsyQ VSync helpers must not split it.
        if (_ticksTakenThisLoop)
        {
            PatchTicksPerFrame(m);
            return _guestTicks;
        }

        double sec = (now - _simTs) / (double)Stopwatch.Frequency;
        if (sec < MinStepSeconds)
        {
            _exactTicks = 0;
            PatchTicksPerFrame(m);
            return _guestTicks;
        }

        _simTs = now;
        if (sec > HitchSeconds)
            sec = HitchSeconds;

        _exactTicks = sec * TicksPerSecond;
        _tickFrac += _exactTicks;
        uint dt = (uint)Math.Floor(_tickFrac);
        _tickFrac -= dt;
        if (dt < 1) dt = 1;
        if (dt > RefTicks) dt = RefTicks;

        _frameTicks = dt;
        _guestTicks += dt;
        _ticksTakenThisLoop = true;
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
        HookPre(GoolObjectTransformAddr, PreTransform);
        HookPre(GoolObjectPhysicsAddr, PrePhysics);
        HookPost(GoolObjectPhysicsAddr, PostCrashPhysics);
        HookPre(GfxTransformSvtxAddr, PreTransformSvtx);
        HookPre(GfxTransformCvtxAddr, PreTransformCvtx);
        HookPre(GpuUpdateAddr, PreGpuUpdate);
        HookPost(GpuUpdateAddr, PostGpuUpdate);
        HookPre(NsInitAddr, PreNsInit);
        HookPost(NsInitAddr, PostNsInit);
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

    static void HookPost(uint addr, Action<CpuContext, IMemory> post)
    {
        var mi = SymbolRegistry.Resolve("main", null, addr);
        if (mi == null)
        {
            Console.Error.WriteLine($"[FramePacing] no func 0x{addr:X8}");
            return;
        }
        HookManager.AddPost(mi, post);
    }

    static bool PreUpdateObjects(CpuContext c, IMemory m)
    {
        _ticksTakenThisLoop = false;
        if (IsActive(m))
            AdvanceWallClock(m);
        return true;
    }

    static bool OnCallPre(uint addr, CpuContext c, IMemory m)
    {
        if (addr == NsInitAddr)
        {
            _inNsInit = true;
            _levelReady = false;
            _holdLocked = 0;
            _loggedUnlockGpu = false;
            PaceLog($"NSInit start lid=0x{c.A1:X}");
            return true;
        }
        if (addr == GoolObjectTransformAddr)
        {
            if (IsActive(m))
                ClampAnimFrame(m, c.A0);
            return true;
        }
        if (addr == GoolObjectPhysicsAddr)
        {
            if (IsActive(m))
                WriteAllTicks(m, _crashObj ? RefTicks : _frameTicks);
            return true;
        }
        if (addr != GoolObjectUpdateAddr) return true;
        _haveObj = false;
        _physicsBlended = false;
        _crashObj = false;
        _crashScaled = false;
        if (!IsActive(m)) return true;
        // Crash: original 34-tick trans+physics, then FinishCrashScale(dt/34).
        // HUD: real dt. Everyone else: 34, then BlendObject.
        if (IsHud(m, c.A0))
            WriteAllTicks(m, _frameTicks);
        else
            WriteAllTicks(m, RefTicks);
        SnapshotObject(m, c.A0);
        if (_crashObj)
            HoldCrashStall(m, c.A0);
        if (!_crashObj)
            ClampAnimFrame(m, c.A0);
        return true;
    }

    static void OnCallPost(uint addr, CpuContext c, IMemory m)
    {
        if (addr == NsInitAddr)
        {
            _inNsInit = false;
            _clockArmed = false;
            PaceLog("NSInit end");
            return;
        }
        if (addr == GpuUpdateAddr)
        {
            PatchTicksPerFrame(m);
            return;
        }
        if (addr == GoolObjectPhysicsAddr)
        {
            if (_crashObj && IsActive(m))
                FinishCrashScale(m);
            return;
        }
        if (addr != GoolObjectUpdateAddr) return;
        if (!_physicsBlended && _haveObj)
            BlendObject(m);
        if (IsActive(m))
            WriteAllTicks(m, _frameTicks);
    }

    static bool PreTransform(CpuContext c, IMemory m)
    {
        if (IsActive(m))
            ClampAnimFrame(m, c.A0);
        return true;
    }

    static bool PrePhysics(CpuContext c, IMemory m)
    {
        if (IsActive(m))
        {
            if (_haveObj && c.A0 == _obj && !_crashObj)
                BlendObject(m);
            WriteAllTicks(m, _crashObj ? RefTicks : _frameTicks);
            _physicsBlended = _haveObj && c.A0 == _obj;
        }
        return true;
    }

    static void PostCrashPhysics(CpuContext c, IMemory m)
    {
        if (_crashObj && IsActive(m))
            FinishCrashScale(m);
    }

    static bool PreGpuUpdate(CpuContext c, IMemory m)
    {
        _inGpuUpdate = true;
        _vsyncsInGpu = 0;
        _didPresentThisGpu = false;
        if (_levelReady && !_loggedUnlockGpu)
        {
            _loggedUnlockGpu = true;
            PaceLog("first unlocked GpuUpdate");
        }
        return true;
    }

    static void PostGpuUpdate(CpuContext c, IMemory m)
    {
        _inGpuUpdate = false;
        _vsyncsInGpu = 0;
        _didPresentThisGpu = false;
        TryArmUnlock(m);
        PatchTicksPerFrame(m);
    }

    static bool PreNsInit(CpuContext c, IMemory m)
    {
        _inNsInit = true;
        _levelReady = false;
        _holdLocked = 0;
        _loggedUnlockGpu = false;
        PaceLog($"NSInit start lid=0x{c.A1:X}");
        return true;
    }

    static void PostNsInit(CpuContext c, IMemory m)
    {
        _inNsInit = false;
        _clockArmed = false;
        PaceLog("NSInit end");
    }

    static void TryArmUnlock(IMemory m)
    {
        if (_levelReady || !WantsUnlock || _inNsInit) return;
        try
        {
            uint id = m.ReadU32(Catalog.LevelIdAddr);
            if (!Catalog.Levels.AllowsUnlockedFps(id))
            {
                _holdLocked = 0;
                return;
            }

            int skip = (int)m.ReadU32(DrawSkipAddr);
            uint crash = m.ReadU32(CrashPtrAddr);
            if (skip > 0 || (crash & 0xFF000000u) != 0x80000000u)
                return;

            uint type = m.ReadU32(crash);
            if (type is 0 or 2) return;

            if (_holdLocked == 0)
            {
                _holdLocked = 30;
                PaceLog($"first draw lid={id} crash=0x{crash:X8} — 30 FPS × 30");
            }

            _holdLocked--;
            PaceLog($"hold {_holdLocked}");
            if (_holdLocked > 0) return;

            _levelReady = true;
            _clockArmed = false;
            PaceLog($"unlock armed lid={id} crash=0x{crash:X8}");
        }
        catch
        {
            // overlay swap
        }
    }

    static void PaceLog(string msg)
    {
        Console.WriteLine("[FramePacing] " + msg);
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            File.AppendAllText(Path.Combine(AppPaths.LogsDir, "pacing.txt"),
                $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Guest already integrated with wall dt. Only keep the jump from being
    /// cancelled: GROUNDLAND + floor snap on a small first step.
    /// </summary>
    static bool PreTransformSvtx(CpuContext c, IMemory m) => true;

    static bool PreTransformCvtx(CpuContext c, IMemory m) => true;

    static void WriteAxisDt(IMemory m, uint addr, int from, int vel, int dt)
    {
        int game = (int)m.ReadU32(addr);
        long d = (long)game - from;
        if (d > Teleport || d < -Teleport)
            return;
        m.WriteU32(addr, (uint)(from + (int)((long)vel * dt / 1024)));
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
            if (d > PathWrap || d < -PathWrap) return true;
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
        // Object trans is ticks=34 + BlendObject (HUD is real-dt). Scaling
        // seek again would double-dt fruit and turtles.
        if (_haveObj) return true;
        c.A2 = (uint)ScaleStep((int)c.A2);
        return true;
    }

    static bool IsHud(IMemory m, uint obj)
    {
        if ((obj & 0xFF000000u) != 0x80000000u) return false;
        try
        {
            return (m.ReadU32(obj + ObjStatusBOff) & Flag2D) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// GoolObjectUpdate decrements anim_counter once per display frame.
    /// Add one back until 34 wall ticks pass so stall lasts 30 Hz, not 300 Hz.
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

    static int ScaleExact(int from, int to, int teleport)
    {
        long d = (long)to - from;
        if (d > teleport || d < -teleport) return to;
        return from + (int)Math.Round(d * _exactTicks / RefTicks);
    }

    static int ScaleAng(int from, int to)
    {
        int d = to - from;
        if (d > 0x800) d -= 0x1000;
        if (d < -0x800) d += 0x1000;
        return from + (int)Math.Round(d * _exactTicks / RefTicks);
    }

    /// <summary>
    /// Guest just ran a full 34-tick Crash step (needed so StopAtWalls sees
    /// a move larger than one 2048-unit bitmap cell). Keep dt/34 of pos/vel/rot.
    /// Leave anim_frame and a stopped speed field alone — those are GOOL state.
    /// </summary>
    static void FinishCrashScale(IMemory m)
    {
        if (!_haveObj || !_crashObj || _crashScaled) return;
        try
        {
            _crashScaled = true;
            if (_exactTicks <= 0)
            {
                RestoreCrash(m);
                return;
            }
            if (_exactTicks >= RefTicks - 0.01) return;

            uint o = _obj;
            m.WriteU32(o + ObjTransOff, (uint)ScaleExact(_ox, (int)m.ReadU32(o + ObjTransOff), Teleport));
            m.WriteU32(o + ObjTransOff + 8, (uint)ScaleExact(_oz, (int)m.ReadU32(o + ObjTransOff + 8), Teleport));

            int y = ScaleExact(_oy, (int)m.ReadU32(o + ObjTransOff + 4), Teleport);
            m.WriteU32(o + ObjTransOff + 4, (uint)y);
            m.WriteU32(o + ObjVelXOff, (uint)ScaleExact(_ovx, (int)m.ReadU32(o + ObjVelXOff), Teleport));
            m.WriteU32(o + ObjVelYOff, (uint)ScaleExact(_ovy, (int)m.ReadU32(o + ObjVelYOff), Teleport));
            m.WriteU32(o + ObjVelZOff, (uint)ScaleExact(_ovz, (int)m.ReadU32(o + ObjVelZOff), Teleport));
            int speedTo = (int)m.ReadU32(o + ObjSpeedOff);
            // Walk→idle is abs(speed)>>2. A 34-tick stop scaled by dt/34 never
            // reaches 0 (Round tails to ~5) so the walk cycle keeps playing.
            if (speedTo <= 4 && speedTo >= -4)
                m.WriteU32(o + ObjSpeedOff, (uint)speedTo);
            else
                m.WriteU32(o + ObjSpeedOff, (uint)ScaleExact(_ospeed, speedTo, Teleport));
            m.WriteU32(o + ObjRotOff, (uint)ScaleAng(_orx, (int)m.ReadU32(o + ObjRotOff)));
            m.WriteU32(o + ObjRotOff + 4, (uint)ScaleAng(_ory, (int)m.ReadU32(o + ObjRotOff + 4)));
            m.WriteU32(o + ObjRotOff + 8, (uint)ScaleAng(_orz, (int)m.ReadU32(o + ObjRotOff + 8)));

            uint statusA = m.ReadU32(o + ObjStatusAOff);
            int floor = (int)m.ReadU32(o + ObjFloorYOff);
            int vy = (int)m.ReadU32(o + ObjVelYOff);
            if ((statusA & FlagGroundLand) != 0 && y > floor + 0x100 && vy > 0)
                m.WriteU32(o + ObjStatusAOff, statusA & ~FlagGroundLand);

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

    static void RestoreCrash(IMemory m)
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
            _ovx = (int)m.ReadU32(obj + ObjVelXOff);
            _ovz = (int)m.ReadU32(obj + ObjVelZOff);
            _ospeed = (int)m.ReadU32(obj + ObjSpeedOff);
            _oanim = (int)m.ReadU32(obj + ObjAnimFrameOff);
            _opath = (int)m.ReadU32(obj + ObjPathProgOff);
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

    static void BlendObject(IMemory m)
    {
        // Crash/HUD already used real dt. Other objects: 34-tick trans scaled here.
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
