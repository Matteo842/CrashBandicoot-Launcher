using System.Diagnostics;
using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

/// <summary>
/// Unlocked sim dt is wall seconds × 1020. Never branch on 60/120/240.
/// Crash: grounded 34-tick trans+physics then scale(dt/34) so StopAtWalls
/// sees a real bitmap cell. Jump trans hang uses wall ticks; physics XZ
/// stays 34+scale; Y is hang + wall-dt gravity.
/// After pit death, Warp_In / Force_Fall physics runs once per 34 wall
/// ticks (original step, not 34+scale every refresh). Wall ticks clip
/// through the spawn floor; 34+scale every display frame is hang/gravity
/// at 30 Hz × fps (infinite jump + faster FALL_KILL loop).
/// Trans/ani stay in GoolObjectUpdate every display frame.
/// Enemies (GOOL category 0x300) and boxes (type 0x22) run one original
/// 34-tick GOOL update per 34 wall ticks and still draw every display
/// frame. Gated crate AABB is the last real GoolObjectBound, not a
/// reconstructed col/yaw. Box stacks share velocity with box_link;
/// 300 Hz trans/collide eats the pile.
/// Path rollers stay on real dt every frame.
/// JunOC butterflies (type 22, Fly/Pose) do <c>loopseek(pathprog, …, 0.02)</c>
/// and <c>x += velx</c> in trans with no spd()/ticks. One display-frame step
/// per refresh finishes the path and they freeze in Pose. Same 30 Hz gate as
/// enemies: one original update per 34 wall ticks, still drawn. Not all of
/// JunOC — rollers keep real dt.
/// Unscaled trans <c>x += vel</c> (later turtles) is kept at dt/34 of the extra.
/// GOOL spawn() in trans is capped to a 30 Hz burst so it cannot fill the 96
/// object pool. Wumpa sprite frames are <c>+= 1</c> per trans; scaled to dt/34.
/// HUD uses real ticks. Crash anim_frame is not scaled (GOOL wait).
/// ChangeAnim wait=0 is "next GoolObjectUpdate", not 33 ms. At unlocked
/// refresh that replays look/TNT rock frames every present. Hold that
/// wait until 34 wall ticks; trans and physics still run. Gated boxes
/// skip Update — draw lerps last pose (yaw, trans, svtx origin/verts)
/// from→to with the same wall fraction so GOOL and skip frames share
/// one crate. Yaw-only left the mesh hopping a few cm each 30 Hz frame.
/// World UV anim (draw_count @ 0x80057960) and water ripple use a dedicated
/// wall clock. Worlds run before object AdvanceWallClock, so they must not
/// reuse leftover ticks (that is +1 per display frame).
/// Bonus rounds and Willy_Warp_Out stay on the original 30 Hz pad so CardC
/// save/continue cannot skip.
/// CamFollow look-behind is cam_offset_z += 0x3200 per display frame
/// (12 original frames from -0x12C00 to +0x12C00). Scale that seek — not
/// CamFollow snaps, not CamAdjustProgress (PreLevelUpdate already paces
/// same-path progress). Double-scaling those made walk/spin slow-mo.
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
    const uint ObjectBoundsAddr = 0x80060E08u;
    const uint ObjectBoundCountAddr = 0x80061888u;
    const int ObjectBoundMax = 96;
    const uint ObjBoundOff = 0x8u;
    const uint LevelUpdateAddr = 0x80025A60u;
    const uint GpuUpdateAddr = 0x80016E5Cu;
    const uint NsInitAddr = 0x80015B58u;
    const uint DrawSkipAddr = 0x8005C54Cu;
    const uint GfxUpdateMatricesAddr = 0x80017A14u;
    const uint GfxTransformSvtxAddr = 0x80018964u;
    const uint GfxTransformCvtxAddr = 0x80018A40u;
    const uint GfxTransformWorldsAddr = 0x80019508u;
    const uint GfxTransformWorldsFogAddr = 0x80019BCCu;
    const uint GfxTransformWorldsRippleAddr = 0x80019DE0u;
    const uint GfxTransformWorldsLightningAddr = 0x80019F90u;
    const uint GfxTransformWorldsDarkAddr = 0x8001A0CCu;
    const uint GfxTransformWorldsDark2Addr = 0x8001A2E0u;
    /// <summary>
    /// NTSC-U <c>draw_count</c>: GpuUpdate does ++, worlds pass it as A3 for
    /// wgeo UV. 0x80060E04 is a different GOOL stamp, not this counter.
    /// </summary>
    const uint DrawCountAddr = 0x80057960u;
    const uint RippleSpeedAddr = 0x80056474u;
    const uint RipplePeriodAddr = 0x80056478u;
    const uint TriWaveAddr = 0x800567B8u;
    const uint PausedAddr = 0x80056400u;
    const uint CrashPtrAddr = 0x800566B4u;
    const uint ObjStateOff = 0x2Cu;
    /// <summary>WillC <c>Willy_Warp_Out</c> (EventWarp). NTSC-U SCUS-94900.</summary>
    const uint StateWarpOut = 32;
    /// <summary>WillC spawn/respawn fall. <c>statusc 0</c> so FALL_KILL re-enters.</summary>
    const uint StateForceFall = 12;
    const uint StateDeathFall = 22;
    const uint StateDeathFast = 29;
    const uint StateDeathFlat = 31;
    const uint StateDeathWarthog = 40;
    const uint StateWarpIn = 41;
    const uint CamZoneAddr = 0x80057914u;
    const uint CamPathAddr = 0x8005791Cu;
    const uint CamProgressAddr = 0x80057920u;
    /// <summary>CamFollow (NTSC-U). Look-behind / side-look seek lives here.</summary>
    const uint CamFollowAddr = 0x8002A82Cu;
    const uint CamUpdateAddr = 0x8002B2BCu;
    const uint CamOffsetZAddr = 0x800564A4u;
    const uint CamZoomAddr = 0x800564A8u;
    const uint CamOffsetYAddr = 0x800564B4u;
    const uint CamOffsetXAddr = 0x800564B8u;
    /// <summary>Original per-30 Hz seeks in CamFollow. Larger writes are snaps.</summary>
    const int CamSeekZ = 0x3200;
    const int CamSeekX = 0x6400;
    const int CamSeekY = 0x6400;
    const int CamSeekZoom = 0x1900;

    const uint ObjTransOff = 0x80u;
    const uint ObjRotOff = 0x8Cu;
    const uint ObjScaleOff = 0x98u;
    const uint ObjVelXOff = 0xA4u;
    const uint ObjVelYOff = 0xA8u;
    const uint ObjVelZOff = 0xACu;
    const uint ObjStatusAOff = 0xC8u;
    const uint ObjStatusBOff = 0xCCu;
    const uint ObjSpOff = 0xDCu;
    const uint ObjAnimSeqOff = 0x108u;
    const uint ObjAnimFrameOff = 0x10Cu;
    const uint ObjAnimCounterOff = 0x104u;
    const uint FramesElapsedAddr = 0x80060E04u;
    const uint ObjPathProgOff = 0x114u;
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
    const uint FlagSolidSides = 0x10000u;
    const uint FlagSolidTop = 0x20000u;
    const uint FlagStall = 0x10000000u;
    const uint GoolCategoryEnemy = 0x300u;
    /// <summary>BoxC / crate GOOL header type (NTSC-U entity type 0x22).</summary>
    const uint GoolTypeBox = 0x22u;
    /// <summary>JunOC jungle objects. Decimal 22 — not BoxC 0x22.</summary>
    const uint GoolTypeJunO = 22u;
    /// <summary>JunOC <c>Butterfly_Fly</c> / <c>Butterfly_Pose</c>.</summary>
    const uint StateButterflyFly = 1;
    const uint StateButterflyPose = 2;
    const uint ErrorObjectPoolFull = 0xFFFFFFEAu;
    const uint GoolSuccess = 0xFFFFFF01u; // SUCCESS -255
    const uint EntryMagic = 0x100FFFFu;
    const int ExtraTransMin = 0x2000;
    const int SpawnBurstMax = 16;
    /// <summary>
    /// Ignore hairline XZ overlap so slope AABB grazing does not freeze walk.
    /// </summary>
    const int EmbedSlop = 0x400;

    const int Teleport = 0x80000;
    /// <summary>Jump takeoff vely is larger than <see cref="Teleport"/>; still scale it.</summary>
    const int VelTeleport = 0x800000;
    const int PathWrap = 0x8000;
    const int AnimFrameCap = 32 << 8;
    const byte AnimTypeSprite = 2;
    const byte AnimTypeVtx = 1;
    const uint SvtxEntryType = 1;
    const int SvtxHeaderBytes = 56;
    const int SvtxVertBytes = 6;
    const int SvtxVertMax = 512;

    struct BoundSnap
    {
        public int X1, Y1, Z1, X2, Y2, Z2;
    }

    struct GatePose
    {
        public int Fx, Fy, Fz, Tx, Ty, Tz;
        public int Px, Py, Pz, Qx, Qy, Qz;
        public int FromAnim, ToAnim;
    }

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
    /// <summary>Last PsyQ VSync HLE timestamp. Gap &gt; 0.5 ms starts a new GpuUpdate burst.</summary>
    static long _lastVsyncHleTs;
    static bool _armedThisGpu;
    static bool _gpuFinished;
    static bool _didPreUpdateObjects;

    static uint _obj;
    static int _ox, _oy, _oz, _orx, _ory, _orz, _oanim;
    static int _ovy, _ovx, _ovz, _ospeed;
    static int _paceLog;
    static bool _haveObj;
    static bool _crashObj;
    static bool _solidObj;
    static bool _gatedSolid;
    static bool _objScaled;
    static bool _crashAir;
    static int _yTrans, _vyTrans;
    static bool _haveTransY;
    static readonly Dictionary<uint, BoundSnap> _lastBound = new();
    static readonly Dictionary<uint, double> _animAcc = new();
    static readonly Dictionary<uint, GatePose> _gateRot = new();
    static int _dispSaveX, _dispSaveY, _dispSaveZ;
    static int _dispSavePx, _dispSavePy, _dispSavePz;
    static bool _dispRotApplied;
    static bool _dispTransApplied;
    static uint _dispRotObj;
    static readonly byte[] _svtxSave = new byte[SvtxVertMax * SvtxVertBytes];
    static int _svtxSaveOx, _svtxSaveOy, _svtxSaveOz;
    static int _svtxSaveN;
    static uint _svtxFrame;
    static bool _svtxPatched;
    static bool _animHeld;
    static bool _spawnBurst;
    static bool _didSpawn;
    static bool _spawnFirstFrame;
    static int _spawnBudget;
    static double _spawnAcc;
    static readonly Dictionary<uint, double> _spawnCredit = new();
    static readonly Dictionary<uint, double> _simAcc = new();
    static int _objClassLog;
    static uint _worldDraw;
    static double _worldDrawFrac;
    static double _rippleFrac;
    static int _savedRippleSpeed;
    static bool _ripplePatched;
    static long _waterTs;
    static bool _waterArmed;
    static bool _waterDoneThisLoop;
    static int _waterLog;
    static double _landPhysAcc;
    static bool _landPhysRan;
    static bool _inCamFollow;
    static int _camOffZ, _camOffX, _camOffY, _camZoom;
    static int _camLog;

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
        ResetWaterClock();
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
        _gatedSolid = false;
        _objScaled = false;
        _crashAir = false;
        _landPhysAcc = 0;
        _landPhysRan = false;
        _lastBound.Clear();
        _animAcc.Clear();
        _gateRot.Clear();
        _dispRotApplied = false;
        _dispTransApplied = false;
        _svtxPatched = false;
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
        _lastVsyncHleTs = 0;
        _armedThisGpu = false;
        _gpuFinished = false;
        _didPreUpdateObjects = false;
        _worldDraw = 0;
        _worldDrawFrac = 0;
        _rippleFrac = 0;
        _ripplePatched = false;
        _inCamFollow = false;
        _camLog = 0;
        ResetWaterClock();
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
    /// MonoMod does not detour on Android, so PreGpuUpdate never runs. PsyQ
    /// VSync(0) is two HLE waits a few microseconds apart; a new GpuUpdate is
    /// anything after a 0.5 ms gap.
    /// </summary>
    public static void NoteSoftwareVblank()
    {
        if (!WantsUnlock) return;
        long now = Stopwatch.GetTimestamp();
        double ms = _lastVsyncHleTs == 0
            ? 999.0
            : (now - _lastVsyncHleTs) * 1000.0 / Stopwatch.Frequency;
        _lastVsyncHleTs = now;
        if (ms > 0.5)
            BeginGpuUpdate();
    }

    /// <summary>
    /// Called from every host present. Arms delta-time after the 30-frame hold
    /// even when GpuUpdate was a direct jal (no MonoMod, no Dispatcher.Call).
    /// </summary>
    public static void OnHostPresent(IMemory? m)
    {
        if (_inGpuUpdate)
            _didPresentThisGpu = true;
        if (m != null)
            TryArmUnlock(m);
    }

    static void BeginGpuUpdate()
    {
        _inGpuUpdate = true;
        _vsyncsInGpu = 0;
        _didPresentThisGpu = false;
        _armedThisGpu = false;
        _gpuFinished = false;
        // Android: GoolUpdateObjects is a direct jal, so PreUpdateObjects never
        // clears this. Reset here so AdvanceUnlocked can take the wall-clock step.
        // Windows already took dt in PreUpdateObjects — leave the flag alone.
        if (!_didPreUpdateObjects)
            _ticksTakenThisLoop = false;
    }

    public static void FinishGpuUpdate(IMemory m)
    {
        if (_gpuFinished) return;
        _gpuFinished = true;
        _inGpuUpdate = false;
        _vsyncsInGpu = 0;
        _didPresentThisGpu = false;
        _didPreUpdateObjects = false;
        TryArmUnlock(m);
        PatchTicksPerFrame(m);
        CommitWaterDrawCount(m);
        RestoreRippleSpeed(m);
        _waterDoneThisLoop = false;
    }

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
            // Full original step so the first unlocked frame does not keep
            // 1/34 of a StopAtWalls correction (falls off crate lips).
            _exactTicks = RefTicks;
            _clockArmed = true;
            _frameTicks = RefTicks;
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

    /// <summary>
    /// Jump trans does <c>vely = spd(vely, 5454)</c> while X is held. That
    /// must share the wall clock with gravity. If these states use 34 ticks
    /// per display frame, hang is 30 Hz × fps (fly until X is released).
    /// Walk/stance stay 34+scale even if leftover fall vy is large.
    /// </summary>
    static bool CrashAirborne(IMemory m, uint obj)
    {
        try
        {
            uint state = m.ReadU32(obj + ObjStateOff);
            // Jump / fall-jump / bounce / air spin. Walk is 2. Hang is in 3, 4, 10.
            return state is 3 or 4 or 5 or 6 or 10 or 11 or 14 or 16 or 18 or 20;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// WillC spin: Adjust_Time 15, Air_Adjust 16, Spin 17, Spin_Air 18,
    /// Spin_End 19, Spin_Air_End 20. Trans sends EventSpinHit to collider;
    /// crate Bound/PlotObjWalls need Crash inside the AABB.
    /// </summary>
    static bool CrashSpinning(IMemory m, uint obj)
    {
        try
        {
            uint state = m.ReadU32(obj + ObjStateOff);
            return state is 15 or 16 or 17 or 18 or 19 or 20;
        }
        catch
        {
            return false;
        }
    }

    static bool IsLandLockedState(IMemory m, uint obj)
    {
        try
        {
            uint state = m.ReadU32(obj + ObjStateOff);
            return state == StateForceFall || state == StateWarpIn || state == StateDeathFlat
                || state == StateDeathWarthog
                || (state >= StateDeathFall && state <= StateDeathFast);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Respawn/death: one original 34-tick physics per 34 wall ticks. Extra
    /// display frames skip physics (interpret still runs). 34+scale every
    /// refresh reapplies hang/gravity at 30 Hz × fps.
    /// </summary>
    static bool SkipLandLockedPhysics(IMemory m)
    {
        if (!_crashObj || !IsLandLockedState(m, _obj))
        {
            _landPhysAcc = 0;
            _landPhysRan = false;
            return false;
        }
        if (!_landPhysRan)
        {
            _landPhysRan = true;
            WriteAllTicks(m, RefTicks);
            _crashAir = false;
            _objScaled = true;
            return false;
        }
        _landPhysAcc += _exactTicks;
        if (_landPhysAcc < RefTicks)
        {
            _objScaled = true;
            return true;
        }
        _landPhysAcc -= RefTicks;
        WriteAllTicks(m, RefTicks);
        _crashAir = false;
        _objScaled = true;
        return false;
    }

    static void WriteCrashOrObjectTicks(IMemory m)
    {
        if (_crashObj)
        {
            _crashAir = CrashAirborne(m, _obj);
            WriteAllTicks(m, _crashAir ? _frameTicks : RefTicks);
            return;
        }
        WriteAllTicks(m, _solidObj ? RefTicks : _frameTicks);
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

    /// <summary>
    /// GOOL function-pointer updates go through <see cref="Dispatcher.Call"/>.
    /// Direct jals (physics, cam, worlds, GpuUpdate) are hooked at compile time
    /// in CrashBandicoot.json — MonoMod detours do not exist on Android.
    /// </summary>
    public static void InstallGameHooks()
    {
        if (_hooksInstalled) return;
        _hooksInstalled = true;

        Dispatcher.CallPre = OnCallPre;
        Dispatcher.CallPost = OnCallPost;
    }

    public static bool PreUpdateObjects(CpuContext c, IMemory m)
    {
        if (_didPreUpdateObjects) return true;
        _didPreUpdateObjects = true;
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

    static bool OnCallPre(uint addr, CpuContext c, IMemory m) => addr switch
    {
        NsInitAddr => PreNsInit(c, m),
        GoolUpdateObjectsAddr => PreUpdateObjects(c, m),
        GpuUpdateAddr => PreGpuUpdate(c, m),
        GoolObjectTransformAddr => PreTransform(c, m),
        GoolObjectPhysicsAddr => PrePhysics(c, m),
        GoolObjectCreateAddr => PreObjectCreate(c, m),
        GfxUpdateMatricesAddr => PreGfxUpdateMatrices(c, m),
        GfxTransformSvtxAddr or GfxTransformCvtxAddr => true,
        GfxTransformWorldsAddr or GfxTransformWorldsFogAddr
            or GfxTransformWorldsLightningAddr
            or GfxTransformWorldsDarkAddr
            or GfxTransformWorldsDark2Addr => PreWorlds(c, m),
        GfxTransformWorldsRippleAddr => PreWorldsRipple(c, m),
        LevelUpdateAddr => PreLevelUpdate(c, m),
        GoolSeekAddr => PreGoolSeek(c, m),
        CamFollowAddr or CamUpdateAddr => PreCamFollow(c, m),
        GoolObjectUpdateAddr => PreGoolObjectUpdate(c, m),
        _ => true,
    };

    static bool PreGoolObjectUpdate(CpuContext c, IMemory m)
    {
        _haveObj = false;
        _crashObj = false;
        _solidObj = false;
        _gatedSolid = false;
        _objScaled = false;
        _crashAir = false;
        _haveTransY = false;
        _spawnBurst = false;
        _didSpawn = false;
        _spawnFirstFrame = false;
        _animHeld = false;
        if (!IsActive(m)) return true;
        SnapshotObject(m, c.A0);
        _solidObj = _haveObj && !_crashObj && !IsHud(m, c.A0)
            && (HasSolidPhysics(m, c.A0) || IsJunocButterfly(m, c.A0));
        LogObjectClass(m, c.A0);
        if (_solidObj)
        {
            // Trans runs every display frame. >>=2, ReactSolid, setvel, spawn
            // in trans are per-call not per-tick — 34+scale cannot fix that.
            // Run one original 34-tick update per 34 wall ticks; still draw.
            // First frame always runs so box_link / stall init is not delayed.
            if (_simAcc.Count > 128)
                _simAcc.Clear();
            if (IsFirstFrame(m, _obj))
            {
                _simAcc[_obj] = 0;
                WriteAllTicks(m, RefTicks);
            }
            else
            {
                _simAcc.TryGetValue(_obj, out double acc);
                acc += _exactTicks;
                if (acc < RefTicks)
                {
                    _simAcc[_obj] = acc;
                    _gatedSolid = true;
                    DrawGatedObject(c, m, _obj);
                    c.V0 = GoolSuccess;
                    return false;
                }
                _simAcc[_obj] = acc - RefTicks;
                WriteAllTicks(m, RefTicks);
            }
        }
        else
        {
            WriteCrashOrObjectTicks(m);
            // Gated objects already run one Update per 34 wall ticks.
            // wait=0 on Crash / path TNT is next present — hold to wall dt.
            if (_haveObj)
                HoldAnimWait(m, c.A0);
        }
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
        switch (addr)
        {
            case NsInitAddr:
                PostNsInit(c, m);
                return;
            case GpuUpdateAddr:
                PostGpuUpdate(c, m);
                return;
            case GoolObjectPhysicsAddr:
                PostObjectPhysics(c, m);
                return;
            case GfxTransformWorldsAddr:
            case GfxTransformWorldsFogAddr:
            case GfxTransformWorldsLightningAddr:
            case GfxTransformWorldsDarkAddr:
            case GfxTransformWorldsDark2Addr:
                PostWorlds(c, m);
                return;
            case GfxTransformWorldsRippleAddr:
                PostWorldsRipple(c, m);
                return;
            case CamFollowAddr:
            case CamUpdateAddr:
                PostCamFollow(c, m);
                return;
            case GoolObjectTransformAddr:
                RestoreGatedDisplayRot(m, c.A0);
                return;
            case GoolObjectUpdateAddr:
                break;
            default:
                return;
        }
        NoteSpawnUsed();
        if (_haveObj)
            RestoreGatedDisplayRot(m, _obj);
        if (_solidObj && !_gatedSolid && _haveObj)
        {
            CaptureBound(m, _obj);
            CaptureGateRot(m, _obj);
        }
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

    public static bool PreTransform(CpuContext c, IMemory m)
    {
        if (IsActive(m))
            ClampAnimFrame(m, c.A0);
        ApplyGatedDisplayPose(m, c.A0);
        return true;
    }

    public static bool PrePhysics(CpuContext c, IMemory m)
    {
        if (!IsActive(m)) return true;
        if (SkipLandLockedPhysics(m)) return false;
        if (_crashObj && IsLandLockedState(m, _obj))
            return true;
        WriteCrashOrObjectTicks(m);
        // Trans already used wall ticks (hang). Physics needs a 34-tick
        // StopAtWalls step on XZ; Y is reintegrated in FinishPacedScale.
        if (_crashObj && _crashAir)
        {
            _yTrans = (int)m.ReadU32(_obj + ObjTransOff + 4);
            _vyTrans = (int)m.ReadU32(_obj + ObjVelYOff);
            _haveTransY = true;
            WriteAllTicks(m, RefTicks);
        }
        return true;
    }

    public static void PostObjectPhysics(CpuContext c, IMemory m)
    {
        if (_crashObj && IsActive(m))
            FinishPacedScale(m);
    }

    public static bool PreGpuUpdate(CpuContext c, IMemory m)
    {
        BeginGpuUpdate();
        if (_levelReady && !_loggedUnlockGpu)
        {
            _loggedUnlockGpu = true;
            PaceLog("first unlocked GpuUpdate");
        }
        return true;
    }

    public static void PostGpuUpdate(CpuContext c, IMemory m) => FinishGpuUpdate(m);

    public static bool PreNsInit(CpuContext c, IMemory m)
    {
        _inNsInit = true;
        _levelReady = false;
        _holdLocked = 0;
        _loggedUnlockGpu = false;
        ResetWaterClock();
        _lastBound.Clear();
        _animAcc.Clear();
        _gateRot.Clear();
        _dispRotApplied = false;
        _dispTransApplied = false;
        _svtxPatched = false;
        PaceLog($"NSInit start lid=0x{c.A1:X}");
        return true;
    }

    public static void PostNsInit(CpuContext c, IMemory m)
    {
        _inNsInit = false;
        _clockArmed = false;
        PaceLog("NSInit end");
    }

    static void TryArmUnlock(IMemory m)
    {
        if (_levelReady || !WantsUnlock || _inNsInit) return;
        if (_armedThisGpu) return;
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

            _armedThisGpu = true;
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
            ResetWaterClock();
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

    /// <summary>
    /// CoreLoop: ShaderParams (writes ripple_speed) → GfxUpdateMatrices →
    /// TransformWorlds* → objects → GpuUpdate (++ draw_count). Advance water
    /// here so worlds see wall time, not leftover object ticks.
    /// </summary>
    public static bool PreGfxUpdateMatrices(CpuContext c, IMemory m)
    {
        AdvanceWater(m);
        return true;
    }

    static void ResetWaterClock()
    {
        _waterArmed = false;
        _waterDoneThisLoop = false;
        _worldDrawFrac = 0;
        _rippleFrac = 0;
        _ripplePatched = false;
        _waterLog = 0;
    }

    static void CommitWaterDrawCount(IMemory m)
    {
        if (!_waterArmed || !IsActive(m)) return;
        try { m.WriteU32(DrawCountAddr, _worldDraw); }
        catch { /* overlay swap */ }
    }

    /// <summary>
    /// One wall sample per game loop. 30 original frames per wall second, with
    /// a remainder — 60/120/280/350 must match. Never uses <c>_exactTicks</c>.
    /// </summary>
    static void AdvanceWater(IMemory m)
    {
        if (!IsActive(m))
        {
            _waterArmed = false;
            return;
        }

        if (_waterDoneThisLoop)
        {
            CommitWaterDrawCount(m);
            return;
        }

        long now = Stopwatch.GetTimestamp();
        if (!_waterArmed)
        {
            try { _worldDraw = m.ReadU32(DrawCountAddr); }
            catch { return; }
            _worldDrawFrac = 0;
            _rippleFrac = 0;
            _waterTs = now;
            _waterArmed = true;
            _waterDoneThisLoop = true;
            CommitWaterDrawCount(m);
            ZeroRippleSpeed(m);
            return;
        }

        double sec = (now - _waterTs) / (double)Stopwatch.Frequency;
        _waterTs = now;
        if (sec < 0) sec = 0;
        if (sec > HitchSeconds) sec = HitchSeconds;

        double frames = sec * (TicksPerSecond / RefTicks);
        _worldDrawFrac += frames;
        int n = (int)Math.Floor(_worldDrawFrac);
        _worldDrawFrac -= n;
        if (n > 0)
            _worldDraw += (uint)n;

        _waterDoneThisLoop = true;
        CommitWaterDrawCount(m);
        PaceRippleByFrames(m, frames);

        if (_waterLog < 8)
        {
            _waterLog++;
            PaceLog($"water wall {sec * 1000:0.00}ms origFrames={frames:0.000} draw={_worldDraw}");
        }
    }

    public static bool PreWorlds(CpuContext c, IMemory m)
    {
        CommitWaterDrawCount(m);
        return true;
    }

    public static void PostWorlds(CpuContext c, IMemory m) => RestoreRippleSpeed(m);

    public static bool PreWorldsRipple(CpuContext c, IMemory m)
    {
        // A0 == 0 is init: fill tri_wave from period, no present.
        if (c.A0 == 0) return true;
        CommitWaterDrawCount(m);
        ZeroRippleSpeed(m);
        return true;
    }

    public static void PostWorldsRipple(CpuContext c, IMemory m) => RestoreRippleSpeed(m);

    static void ZeroRippleSpeed(IMemory m)
    {
        if (_ripplePatched || !IsActive(m)) return;
        try
        {
            int speed = (int)m.ReadU32(RippleSpeedAddr);
            if (speed == 0) return;
            _savedRippleSpeed = speed;
            m.WriteU32(RippleSpeedAddr, 0);
            _ripplePatched = true;
        }
        catch
        {
            // overlay swap
        }
    }

    /// <summary>
    /// <c>tri_wave[i] += ripple_speed</c> is one original 30 Hz step. Scale by
    /// wall original-frames (not display frames) and leave the guest add at 0.
    /// </summary>
    static void PaceRippleByFrames(IMemory m, double frames)
    {
        try
        {
            if (m.ReadU32(PausedAddr) != 0) return;
            int speed = (int)m.ReadU32(RippleSpeedAddr);
            if (speed == 0) return;
            _savedRippleSpeed = speed;
            _rippleFrac += speed * frames;
            int inc = (int)Math.Floor(_rippleFrac);
            _rippleFrac -= inc;
            if (inc != 0)
            {
                int period = (int)m.ReadU32(RipplePeriodAddr);
                for (uint i = 0; i < 16; i++)
                {
                    uint addr = TriWaveAddr + i * 4;
                    int w = (int)m.ReadU32(addr) + inc;
                    if (w > period)
                        w = -(period - 1);
                    m.WriteU32(addr, (uint)w);
                }
            }
            m.WriteU32(RippleSpeedAddr, 0);
            _ripplePatched = true;
        }
        catch
        {
            // overlay swap
        }
    }

    static void RestoreRippleSpeed(IMemory m)
    {
        if (!_ripplePatched) return;
        _ripplePatched = false;
        try { m.WriteU32(RippleSpeedAddr, (uint)_savedRippleSpeed); }
        catch { /* overlay swap */ }
    }

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
    public static bool PreLevelUpdate(CpuContext c, IMemory m)
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

    /// <summary>
    /// Look-behind / L-R offset and GoolSeek zoom/pan are +N per display
    /// frame. Keep dt/34 of those seeks. Snaps (new path zoom, force x=0)
    /// stay instant. Do not touch cam_progress here.
    /// </summary>
    public static bool PreCamFollow(CpuContext c, IMemory m)
    {
        _inCamFollow = false;
        if (!IsActive(m) || _frameTicks >= RefTicks) return true;
        try
        {
            _camOffZ = (int)m.ReadU32(CamOffsetZAddr);
            _camOffX = (int)m.ReadU32(CamOffsetXAddr);
            _camOffY = (int)m.ReadU32(CamOffsetYAddr);
            _camZoom = (int)m.ReadU32(CamZoomAddr);
            _inCamFollow = true;
        }
        catch
        {
            // overlay swap
        }
        return true;
    }

    public static void PostCamFollow(CpuContext c, IMemory m)
    {
        if (!_inCamFollow) return;
        _inCamFollow = false;
        try
        {
            BlendCamSeek(m, CamOffsetZAddr, _camOffZ, CamSeekZ);
            BlendCamSeek(m, CamOffsetXAddr, _camOffX, CamSeekX);
            BlendCamSeek(m, CamOffsetYAddr, _camOffY, CamSeekY);
            BlendCamSeek(m, CamZoomAddr, _camZoom, CamSeekZoom);
        }
        catch
        {
            // overlay swap
        }
    }

    static void BlendCamSeek(IMemory m, uint addr, int from, int maxStep)
    {
        int to = (int)m.ReadU32(addr);
        int d = to - from;
        if (d == 0) return;
        int ad = d < 0 ? -d : d;
        if (ad > maxStep) return;
        int kept = ScaleStep(d);
        m.WriteU32(addr, (uint)(from + kept));
        if (_camLog < 8)
        {
            _camLog++;
            PaceLog($"cam 0x{addr:X8} {from}->{to} kept={kept} dt={_exactTicks:0.00}");
        }
    }

    public static bool PreGoolSeek(CpuContext c, IMemory m)
    {
        if (!IsActive(m) || _frameTicks >= RefTicks) return true;
        // PostCamFollow scales the zoom/pan result. Crash trans is 34+scale.
        if (_inCamFollow || _haveObj) return true;
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
            // Crates: stack link lives in the velocity union. 300 Hz trans
            // collides them and GOOL_EVENT_BOX_STACK_BREAK eats the pile.
            if (type == GoolTypeBox) return true;
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
            uint statusB = m.ReadU32(obj + ObjStatusBOff);
            if ((statusB & FlagInvisible) != 0) return;
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
    /// Poses before/after the last 30 Hz GOOL step. Draw lerps from→to so
    /// skip frames and the GOOL frame share one crate, not a ghost pair.
    /// </summary>
    static void CaptureGateRot(IMemory m, uint obj)
    {
        try
        {
            if (_gateRot.Count > 128)
                _gateRot.Clear();
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
    /// t = wall ticks since that step / 34. The TNT hop is the svtx frame
    /// origin/verts snapping every 30 Hz — lerp those too.
    /// </summary>
    static void ApplyGatedDisplayPose(IMemory m, uint obj)
    {
        if (_dispRotApplied || _dispTransApplied || _svtxPatched)
            RestoreGatedDisplayRot(m, _dispRotObj);
        if (obj != _obj) return;
        if (!_solidObj && !_crashObj) return;
        if (_exactTicks >= RefTicks - 0.01) return;

        double t;
        int fromAnim, toAnim;
        int fx, fy, fz, tx, ty, tz;
        int px, py, pz, qx, qy, qz;
        bool lerpTrans = _solidObj;
        if (_solidObj)
        {
            if (!_simAcc.TryGetValue(obj, out double acc)) return;
            t = acc / RefTicks;
            if (_gatedSolid)
            {
                if (!_gateRot.TryGetValue(obj, out GatePose g)) return;
                fx = g.Fx; fy = g.Fy; fz = g.Fz;
                tx = g.Tx; ty = g.Ty; tz = g.Tz;
                px = g.Px; py = g.Py; pz = g.Pz;
                qx = g.Qx; qy = g.Qy; qz = g.Qz;
                fromAnim = g.FromAnim;
                toAnim = g.ToAnim;
            }
            else
            {
                fx = _orx; fy = _ory; fz = _orz;
                px = _ox; py = _oy; pz = _oz;
                fromAnim = _oanim;
                try
                {
                    tx = (int)m.ReadU32(obj + ObjRotOff);
                    ty = (int)m.ReadU32(obj + ObjRotOff + 4);
                    tz = (int)m.ReadU32(obj + ObjRotOff + 8);
                    qx = (int)m.ReadU32(obj + ObjTransOff);
                    qy = (int)m.ReadU32(obj + ObjTransOff + 4);
                    qz = (int)m.ReadU32(obj + ObjTransOff + 8);
                    toAnim = (int)m.ReadU32(obj + ObjAnimFrameOff);
                }
                catch
                {
                    return;
                }
            }
        }
        else
        {
            // Crash: vertex look/walk is 30 Hz (wait hold). Do not lerp trans
            // (FinishPacedScale already did). Same t as the ani wait.
            _animAcc.TryGetValue(obj, out double acc);
            t = acc / RefTicks;
            fromAnim = _oanim;
            try { toAnim = (int)m.ReadU32(obj + ObjAnimFrameOff); }
            catch { return; }
            fx = fy = fz = tx = ty = tz = 0;
            px = py = pz = qx = qy = qz = 0;
            lerpTrans = false;
        }
        if (t < 0) t = 0;
        if (t > 1) t = 1;
        try
        {
            _dispRotObj = obj;
            if (_solidObj)
            {
                _dispSaveX = (int)m.ReadU32(obj + ObjRotOff);
                _dispSaveY = (int)m.ReadU32(obj + ObjRotOff + 4);
                _dispSaveZ = (int)m.ReadU32(obj + ObjRotOff + 8);
                m.WriteU32(obj + ObjRotOff, (uint)LerpAng(fx, tx, t));
                m.WriteU32(obj + ObjRotOff + 4, (uint)LerpAng(fy, ty, t));
                m.WriteU32(obj + ObjRotOff + 8, (uint)LerpAng(fz, tz, t));
                _dispRotApplied = true;
            }
            if (lerpTrans)
            {
                _dispSavePx = (int)m.ReadU32(obj + ObjTransOff);
                _dispSavePy = (int)m.ReadU32(obj + ObjTransOff + 4);
                _dispSavePz = (int)m.ReadU32(obj + ObjTransOff + 8);
                int x = LerpPos(px, qx, t);
                int y = LerpPos(py, qy, t);
                int z = LerpPos(pz, qz, t);
                m.WriteU32(obj + ObjTransOff, (uint)x);
                m.WriteU32(obj + ObjTransOff + 4, (uint)y);
                m.WriteU32(obj + ObjTransOff + 8, (uint)z);
                _dispTransApplied = true;
            }
            PatchSvtxLerp(m, obj, fromAnim, toAnim, t);
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
    /// anim_seq+4 starts as an EID (LSB=1). After the first Transform,
    /// NSLookup writes the NSD PTE there. PTE.entry is the svtx entry.
    /// </summary>
    static bool TrySvtxEntry(IMemory m, uint obj, out uint en, out uint seq)
    {
        en = 0;
        seq = m.ReadU32(obj + ObjAnimSeqOff);
        if ((seq & 0xFF000000u) != 0x80000000u) return false;
        if (m.ReadU8(seq) != AnimTypeVtx) return false;
        uint r = m.ReadU32(seq + 4);
        if ((r & 1u) != 0) return false;
        if ((r & 0xFF000000u) != 0x80000000u) return false;
        uint magic = m.ReadU32(r);
        if (magic == EntryMagic)
            en = r;
        else
        {
            if ((magic & 1u) != 0) return false;
            en = magic;
            if ((en & 0xFF000000u) != 0x80000000u) return false;
            if (m.ReadU32(en) != EntryMagic) return false;
        }
        if (m.ReadU32(en + 8) != SvtxEntryType) return false;
        return true;
    }

    static bool TrySvtxFrames(IMemory m, uint obj, int fromIdx, int toIdx,
        out uint fromFrame, out uint toFrame, out int nverts)
    {
        fromFrame = 0;
        toFrame = 0;
        nverts = 0;
        if (!TrySvtxEntry(m, obj, out uint en, out _)) return false;
        int items = (int)m.ReadU32(en + 12);
        if ((uint)fromIdx >= (uint)items || (uint)toIdx >= (uint)items) return false;
        fromFrame = EntryItem(m, en, fromIdx);
        toFrame = EntryItem(m, en, toIdx);
        int nTo = SvtxVertCount(m, toFrame);
        int nFrom = SvtxVertCount(m, fromFrame);
        if (nTo <= 0 || nTo != nFrom || nTo > SvtxVertMax) return false;
        nverts = nTo;
        return true;
    }

    static int SvtxVertCount(IMemory m, uint frame)
    {
        int bytes = (int)m.ReadU32(frame) * 4;
        if (bytes < SvtxHeaderBytes + SvtxVertBytes) return 0;
        return (bytes - SvtxHeaderBytes) / SvtxVertBytes;
    }

    static void PatchSvtxLerp(IMemory m, uint obj, int fromAnim, int toAnim, double t)
    {
        int fromIdx = fromAnim >> 8;
        int toIdx = toAnim >> 8;
        int d = toIdx - fromIdx;
        if (d < 0) d = -d;
        if (d > 2) return;
        bool lookAhead = false;
        if (d == 0)
        {
            // Crash wait=0 hold: Transform still draws this frame. Blend it
            // toward the next item; patching the next item would be invisible.
            if (!_crashObj || !_animHeld) return;
            if (!TrySvtxEntry(m, obj, out _, out uint seq)) return;
            int len = m.ReadU8(seq + 2);
            if (len <= 1) return;
            int next = fromIdx + 1;
            if (next >= len) return;
            toIdx = next;
            lookAhead = true;
        }
        else if (_crashObj)
        {
            return;
        }
        if (!TrySvtxFrames(m, obj, fromIdx, toIdx, out uint fromFrame, out uint toFrame, out int n))
            return;

        // Gated: draw index is `to` (already advanced). Crash hold: draw is `from`.
        uint drawn = lookAhead ? fromFrame : toFrame;
        uint srcFrom = fromFrame;
        uint srcTo = toFrame;
        _svtxSaveOx = (int)m.ReadU32(drawn + 8);
        _svtxSaveOy = (int)m.ReadU32(drawn + 12);
        _svtxSaveOz = (int)m.ReadU32(drawn + 16);
        int ox0 = lookAhead ? _svtxSaveOx : (int)m.ReadU32(srcFrom + 8);
        int oy0 = lookAhead ? _svtxSaveOy : (int)m.ReadU32(srcFrom + 12);
        int oz0 = lookAhead ? _svtxSaveOz : (int)m.ReadU32(srcFrom + 16);
        int ox1 = (int)m.ReadU32(srcTo + 8);
        int oy1 = (int)m.ReadU32(srcTo + 12);
        int oz1 = (int)m.ReadU32(srcTo + 16);
        m.WriteU32(drawn + 8, (uint)LerpPos(ox0, ox1, t));
        m.WriteU32(drawn + 12, (uint)LerpPos(oy0, oy1, t));
        m.WriteU32(drawn + 16, (uint)LerpPos(oz0, oz1, t));

        uint fromV = srcFrom + (uint)SvtxHeaderBytes;
        uint toV = srcTo + (uint)SvtxHeaderBytes;
        uint drawnV = drawn + (uint)SvtxHeaderBytes;
        int bytes = n * SvtxVertBytes;
        for (int i = 0; i < bytes; i++)
            _svtxSave[i] = m.ReadU8(drawnV + (uint)i);
        for (int i = 0; i < bytes; i += SvtxVertBytes)
        {
            for (int k = 0; k < 3; k++)
            {
                int a = lookAhead ? _svtxSave[i + k] : m.ReadU8(fromV + (uint)(i + k));
                int b = m.ReadU8(toV + (uint)(i + k));
                m.WriteU8(drawnV + (uint)(i + k), (byte)(a + (int)Math.Round((b - a) * t)));
            }
        }
        _svtxFrame = drawn;
        _svtxSaveN = bytes;
        _svtxPatched = true;
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
        int d = AngDelta(from, to);
        if (Math.Abs(d) > 0x200) return to;
        return from + (int)Math.Round(d * t);
    }

    static int AngDelta(int from, int to)
    {
        int d = to - from;
        if (d > 0x800) d -= 0x1000;
        if (d < -0x800) d += 0x1000;
        return d;
    }

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
        if (_lastBound.TryGetValue(obj, out BoundSnap snap))
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
                if (_lastBound.Count > 128)
                    _lastBound.Clear();
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
        if (TryReadGoolClass(m, _obj, out uint ptype, out _) && ptype == GoolTypeBox)
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

    /// <summary>
    /// ChangeAnim pushes <c>(wait&lt;&lt;24)|frames_elapsed</c>. wait=0 means
    /// next Update, which at unlocked refresh replays the same vertex frames
    /// (Crash look-at-cam, TNT rock). Hold until 34 wall ticks. Trans and
    /// physics still run this Update. Does not depend on 60/120/240.
    /// </summary>
    static void HoldAnimWait(IMemory m, uint obj)
    {
        if (_exactTicks >= RefTicks - 0.01) return;
        if (IsFirstFrame(m, obj))
        {
            _animAcc[obj] = 0;
            return;
        }
        try
        {
            uint sp = m.ReadU32(obj + ObjSpOff);
            if ((sp & 0xFF000000u) != 0x80000000u || sp < 4) return;
            uint tagAddr = sp - 4;
            uint top = m.ReadU32(tagAddr);
            if ((top >> 24) != 0) return;
            uint ts = top & 0x00FFFFFFu;
            if (ts == 0) return;
            uint fe = m.ReadU32(FramesElapsedAddr) & 0x00FFFFFFu;
            int dt = (int)fe - (int)ts;
            if (dt < 0) dt = -dt;
            if (dt > 4) return;
            if (_animAcc.Count > 128)
                _animAcc.Clear();
            _animAcc.TryGetValue(obj, out double acc);
            acc += _exactTicks;
            if (acc < RefTicks)
            {
                _animAcc[obj] = acc;
                _animHeld = true;
                m.WriteU32(tagAddr, 0x01000000u | fe);
                return;
            }
            _animAcc[obj] = acc - RefTicks;
            m.WriteU32(tagAddr, fe);
        }
        catch
        {
            // stack not mapped
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
    /// than one 2048-unit bitmap cell). Keep dt/34 of pos/vel/rot.
    /// Leave anim_frame alone — GOOL waits on draw_stamp/34.
    /// </summary>
    static void FinishPacedScale(IMemory m)
    {
        if (!_haveObj || !_crashObj || _objScaled) return;
        try
        {
            _objScaled = true;
            if (_crashAir)
            {
                FinishJumpScale(m);
                return;
            }
            if (_exactTicks <= 0)
            {
                RestorePaced(m);
                return;
            }
            if (_exactTicks >= RefTicks - 0.01) return;

            uint o = _obj;
            m.WriteU32(o + ObjTransOff, (uint)ScaleExact(_ox, (int)m.ReadU32(o + ObjTransOff), Teleport));
            m.WriteU32(o + ObjTransOff + 8, (uint)ScaleExact(_oz, (int)m.ReadU32(o + ObjTransOff + 8), Teleport));

            int yTo = (int)m.ReadU32(o + ObjTransOff + 4);
            int y = ScaleExact(_oy, yTo, VelTeleport);
            m.WriteU32(o + ObjTransOff + 4, (uint)y);
            m.WriteU32(o + ObjVelXOff, (uint)ScaleExact(_ovx, (int)m.ReadU32(o + ObjVelXOff), VelTeleport));
            int vyTo = (int)m.ReadU32(o + ObjVelYOff);
            m.WriteU32(o + ObjVelYOff, (uint)ScaleExact(_ovy, vyTo, VelTeleport));
            m.WriteU32(o + ObjVelZOff, (uint)ScaleExact(_ovz, (int)m.ReadU32(o + ObjVelZOff), VelTeleport));
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
                if ((statusA & FlagGroundLand) != 0 && vyTo > 0 && yTo > _oy + 0x100)
                    m.WriteU32(o + ObjStatusAOff, statusA & ~FlagGroundLand);
            }
            RejectCrateEmbed(m);

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

    static bool IsBoxWall(IMemory m, uint obj)
    {
        if ((obj & 0xFF000000u) != 0x80000000u) return false;
        try
        {
            if (!TryReadGoolClass(m, obj, out uint type, out _) || type != GoolTypeBox)
                return false;
            return (m.ReadU32(obj + ObjStatusBOff) & FlagSolidSides) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Side-stuck in a BoxC wall. Standing on the lid is not a hit: Y is the
    /// shallow axis. Scenery / slope AABBs are ignored (not type 0x22).
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
            if (kv.Key == _obj || !IsBoxWall(m, kv.Key)) continue;
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
            if (other == _obj || !IsBoxWall(m, other)) continue;
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
            if (kv.Key == _obj || !IsBoxWall(m, kv.Key)) continue;
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
