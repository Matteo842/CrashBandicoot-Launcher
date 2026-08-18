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
/// Crash: 34-tick trans+physics then scale(dt/34) so StopAtWalls
/// sees a real bitmap cell. Enemies (GOOL category 0x300, SkunC, ground
/// walkers) run one original 34-tick GOOL update per 34 wall ticks and
/// still draw every display frame — trans is per-call (>>=2, ReactSolid).
/// Path rollers stay on real dt every frame.
/// Unscaled trans <c>x += vel</c> (later turtles) is kept at dt/34 of the extra.
/// GOOL spawn() in trans is capped to a 30 Hz burst so it cannot fill the 96
/// object pool. Wumpa sprite frames are <c>+= 1</c> per trans; scaled to dt/34.
/// HUD uses real ticks. Crash anim_frame is not scaled (GOOL wait).
/// Bonus rounds and Willy_Warp_Out stay on the original 30 Hz pad so CardC
/// save/continue cannot skip.
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
    const uint GoolObjectCreateAddr = 0x8001C6C8u;
    const uint LevelUpdateAddr = 0x80025A60u;
    const uint GpuUpdateAddr = 0x80016E5Cu;
    const uint NsInitAddr = 0x80015B58u;
    const uint DrawSkipAddr = 0x8005C54Cu;
    const uint GfxTransformSvtxAddr = 0x80018964u;
    const uint GfxTransformCvtxAddr = 0x80018A40u;
    const uint CrashPtrAddr = 0x800566B4u;
    const uint ObjStateOff = 0x2Cu;
    /// <summary>WillC <c>Willy_Warp_Out</c> (EventWarp). NTSC-U SCUS-94900.</summary>
    const uint StateWarpOut = 32;
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
    const uint ObjGlobalOff = 0x20u;

    const uint FlagGroundLand = 0x1u;
    const uint FlagFirstFrame = 0x20u;
    const uint Flag2D = 0x200u;
    const uint FlagStoppedBySolid = 0x8u;
    const uint FlagCollidable = 0x10u;
    const uint FlagGravity = 0x20u;
    const uint FlagTransMotion = 0x40u;
    const uint FlagInvisible = 0x100u;
    const uint FlagStall = 0x10000000u;
    const uint GoolCategoryEnemy = 0x300u;
    const uint ErrorObjectPoolFull = 0xFFFFFFEAu;
    const uint GoolSuccess = 0xFFFFFF01u; // SUCCESS -255
    const uint EntryMagic = 0x100FFFFu;
    const int ExtraTransMin = 0x2000;
    const int SpawnBurstMax = 16;

    const int Teleport = 0x80000;
    const int PathWrap = 0x8000;
    const int AnimFrameCap = 32 << 8;
    const byte AnimTypeSprite = 2;

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
    static int _ox, _oy, _oz, _orx, _ory, _orz, _oanim;
    static int _ovy, _ovx, _ovz, _ospeed;
    static int _paceLog;
    static bool _haveObj;
    static bool _crashObj;
    static bool _solidObj;
    static bool _objScaled;
    static bool _spawnBurst;
    static bool _didSpawn;
    static bool _spawnFirstFrame;
    static int _spawnBudget;
    static double _spawnAcc;
    static readonly Dictionary<uint, double> _spawnCredit = new();
    static readonly Dictionary<uint, double> _simAcc = new();
    static int _objClassLog;

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
        if (!WantsUnlock || m == null || _inNsInit) return false;
        if (!_levelReady) return false;
        try
        {
            uint id = m.ReadU32(Catalog.LevelIdAddr);
            if (!Catalog.Levels.AllowsUnlockedFps(id) || IsWarpOut(m))
            {
                DropToOriginalPad(m, id);
                return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Crash flies up, then CardC save/continue. That GOOL is per display
    /// frame — keep the original 30 Hz pad until NSInit.
    /// </summary>
    static bool IsWarpOut(IMemory m)
    {
        uint crash = m.ReadU32(CrashPtrAddr);
        if ((crash & 0xFF000000u) != 0x80000000u) return false;
        return m.ReadU32(crash + ObjStateOff) == StateWarpOut;
    }

    static void DropToOriginalPad(IMemory m, uint id)
    {
        if (!_levelReady) return;
        _levelReady = false;
        _frameTicks = RefTicks;
        _exactTicks = RefTicks;
        WriteAllTicks(m, RefTicks);
        PaceLog($"original pad lid={id}");
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
        _crashObj = false;
        _solidObj = false;
        _objScaled = false;
        _spawnBurst = false;
        _didSpawn = false;
        _spawnFirstFrame = false;
        _spawnBudget = 0;
        _spawnAcc = 0;
        _spawnCredit.Clear();
        _simAcc.Clear();
        _objClassLog = 0;
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
        HookPost(GoolObjectPhysicsAddr, PostObjectPhysics);
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
        {
            AdvanceWallClock(m);
            RefillSpawnBudget();
        }
        else
        {
            _spawnBudget = 0;
            _spawnAcc = 0;
            if (_spawnCredit.Count > 0)
                _spawnCredit.Clear();
        }
        return true;
    }

    static void RefillSpawnBudget()
    {
        _spawnAcc += _exactTicks;
        while (_spawnAcc >= RefTicks)
        {
            _spawnAcc -= RefTicks;
            _spawnBudget += SpawnBurstMax;
        }
        if (_spawnBudget > SpawnBurstMax + SpawnBurstMax / 2)
            _spawnBudget = SpawnBurstMax + SpawnBurstMax / 2;
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
                WriteAllTicks(m, _crashObj || _solidObj ? RefTicks : _frameTicks);
            return true;
        }
        if (addr == GoolObjectCreateAddr)
            return PreObjectCreate(c, m);
        if (addr != GoolObjectUpdateAddr) return true;
        _haveObj = false;
        _crashObj = false;
        _solidObj = false;
        _objScaled = false;
        _spawnBurst = false;
        _didSpawn = false;
        _spawnFirstFrame = false;
        if (!IsActive(m)) return true;
        SnapshotObject(m, c.A0);
        _solidObj = _haveObj && !_crashObj && !IsHud(m, c.A0) && HasSolidPhysics(m, c.A0);
        LogObjectClass(m, c.A0);
        if (_solidObj)
        {
            // Trans runs every display frame. >>=2, ReactSolid, setvel, spawn
            // in trans are per-call not per-tick — 34+scale cannot fix that.
            // Run one original 34-tick update per 34 wall ticks; still draw.
            if (_simAcc.Count > 128)
                _simAcc.Clear();
            _simAcc.TryGetValue(_obj, out double acc);
            acc += _exactTicks;
            if (acc < RefTicks)
            {
                _simAcc[_obj] = acc;
                DrawGatedObject(c, m, _obj);
                c.V0 = GoolSuccess;
                return false;
            }
            _simAcc[_obj] = acc - RefTicks;
            WriteAllTicks(m, RefTicks);
        }
        else
            WriteAllTicks(m, _crashObj ? RefTicks : _frameTicks);
        if (_crashObj)
            HoldCrashStall(m, c.A0);
        if (!_crashObj)
            ClampAnimFrame(m, c.A0);
        if (_haveObj)
            ArmSpawnBurst(m, c.A0);
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
                FinishPacedScale(m);
            return;
        }
        if (addr != GoolObjectUpdateAddr) return;
        NoteSpawnUsed();
        if (_crashObj && !_objScaled && _haveObj)
            FinishPacedScale(m);
        else if (!_crashObj && !_solidObj && _haveObj)
        {
            PaceSpriteAnim(m);
            PaceExtraTrans(m);
        }
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
            WriteAllTicks(m, _crashObj || _solidObj ? RefTicks : _frameTicks);
        return true;
    }

    static void PostObjectPhysics(CpuContext c, IMemory m)
    {
        if (_crashObj && IsActive(m))
            FinishPacedScale(m);
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
        // Crash trans is 34+scale. Others already used real dt.
        if (_haveObj) return true;
        c.A2 = (uint)ScaleStep((int)c.A2);
        return true;
    }

    static bool HasSolidPhysics(IMemory m, uint obj)
    {
        if ((obj & 0xFF000000u) != 0x80000000u) return false;
        try
        {
            TryReadGoolClass(m, obj, out uint type, out uint cat);
            if (type == 2) return true;
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

    static bool TryReadGoolClass(IMemory m, uint obj, out uint type, out uint cat)
    {
        type = 0;
        cat = 0;
        uint en = m.ReadU32(obj + ObjGlobalOff);
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
            type = 0;
            cat = 0;
            return false;
        }
        return true;
    }

    static void DrawGatedObject(CpuContext c, IMemory m, uint obj)
    {
        try
        {
            uint seq = m.ReadU32(obj + ObjAnimSeqOff);
            if ((seq & 0xFF000000u) != 0x80000000u) return;
            if ((m.ReadU32(obj + ObjStatusBOff) & FlagInvisible) != 0) return;
            uint a0 = c.A0;
            c.A0 = obj;
            Dispatcher.Call(c, m, GoolObjectTransformAddr);
            c.A0 = a0;
        }
        catch
        {
            // object freed / no mesh this frame
        }
    }

    static void LogObjectClass(IMemory m, uint obj)
    {
        if (_objClassLog >= 8 || !_haveObj || _crashObj) return;
        try
        {
            uint b = m.ReadU32(obj + ObjStatusBOff);
            TryReadGoolClass(m, obj, out uint type, out uint cat);
            _objClassLog++;
            PaceLog($"obj 0x{obj:X8} b=0x{b:X} type={type} cat=0x{cat:X} solid={_solidObj}");
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
        if (!_spawnBurst || _spawnBudget <= 0)
        {
            c.V0 = ErrorObjectPoolFull;
            return false;
        }
        _spawnBudget--;
        _didSpawn = true;
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
    /// Guest just ran a full 34-tick step (StopAtWalls needs a move larger
    /// than one 2048-unit bitmap cell). Keep dt/34 of pos/vel/rot.
    /// Leave anim_frame alone — GOOL waits on draw_stamp/34.
    /// </summary>
    static void FinishPacedScale(IMemory m)
    {
        if (!_haveObj || !_crashObj || _objScaled) return;
        try
        {
            _objScaled = true;
            if (_exactTicks <= 0)
            {
                RestorePaced(m);
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
            if (_crashObj && speedTo <= 4 && speedTo >= -4)
                m.WriteU32(o + ObjSpeedOff, (uint)speedTo);
            else
                m.WriteU32(o + ObjSpeedOff, (uint)ScaleExact(_ospeed, speedTo, Teleport));
            m.WriteU32(o + ObjRotOff, (uint)ScaleAng(_orx, (int)m.ReadU32(o + ObjRotOff)));
            m.WriteU32(o + ObjRotOff + 4, (uint)ScaleAng(_ory, (int)m.ReadU32(o + ObjRotOff + 4)));
            m.WriteU32(o + ObjRotOff + 8, (uint)ScaleAng(_orz, (int)m.ReadU32(o + ObjRotOff + 8)));

            if (_crashObj)
            {
                uint statusA = m.ReadU32(o + ObjStatusAOff);
                int floor = (int)m.ReadU32(o + ObjFloorYOff);
                int vy = (int)m.ReadU32(o + ObjVelYOff);
                if ((statusA & FlagGroundLand) != 0 && y > floor + 0x100 && vy > 0)
                    m.WriteU32(o + ObjStatusAOff, statusA & ~FlagGroundLand);
            }

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
