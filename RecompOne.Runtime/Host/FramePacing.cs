using System.Diagnostics;
using System.Reflection;
using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

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
/// Default: one original 34-tick GOOL update per 34 wall ticks (still
/// drawn). That is the turtle skip, at 60 and at uncapped — not an
/// <c>if (fps==60)</c> branch. 60 Hz is two presents per 34 ticks.
/// Opt out HUD, SOLID_TOP platforms (Euler dt/34), path rollers,
/// JunOC rollers, and FruiC. Hoppers (i+=1 / lerp / loopseek) never
/// opt into Euler; Jump may OR SOLID_TOP, sticky ids keep the skip.
/// Gated crate AABB
/// is the last real GoolObjectBound, not a
/// reconstructed col/yaw. Box stacks share velocity with box_link;
/// 300 Hz trans/collide eats the pile.
/// Path rollers stay on real dt every frame.
/// GOOL category 0x600 solid meshes (RuiOC pillars / RWaOC seesaws)
/// trans is a 30 Hz Euler step: <c>spd()</c> plus <c>rot += accel</c>.
/// Wall ticks 2–3 make spd 0; Euler every present at 400 Hz slams to
/// rest. Each display frame runs the original 34-tick trans then keeps
/// dt/34 (remainder so a 2-tick frame is not lost). Sprites in the same
/// executable (torch flame) stay on wall ticks — <c>scalex += 0.1S</c>
/// plus <c>200&lt;&lt;shrink</c> must not see ticks=34 every present.
/// Children that <c>vectransf2</c> from a paced parent are not scaled
/// again (that double-step added seesaw momentum).
/// Seesaw <c>PlatOrbitRot</c> is GOOL 8.8 in 0..360.0. Writing a
/// negative leftover as uint (or wrapping 0-epsilon to 359.99) flips
/// the gravity quadrant and the slabs rubber-band, then 360. Unwrap
/// only a real 0↔360 step; keep sub-zero leftovers at 0 in quadrant 1.
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
/// wait until 34 wall ticks and rewrite the tag every present with the
/// current frames_elapsed — a one-shot wait=1 expires on the next
/// display if that stamp advances per GoolUpdateObjects (pause + FPS
/// switch makes the leftover vibrate track refresh, not wall dt).
/// Trans interpret has no SUSPEND_ON_ANIM, so ChangeAnim in tp still
/// writes anim_frame every present (the wait tag is pushed on the trans
/// frame and popped before the code wait check). Hold anim_seq/frame to
/// one original 33 ms of wall time; 240 Hz and uncapped then step alike.
/// Trans and physics still run. Gated boxes skip Update — draw lerps
/// last pose (yaw, trans) from→to. Vertex meshes lerp the previous SVTX
/// keyframe toward the current one over 33 ms wall (not current toward
/// next: look-at-cam reverse, and look-ahead overshoots).
/// Hold does not treat reverse as a teleport — stance look is
/// playanim 14↔19 wait=0. HoldAnimWait is the 30 Hz step; the mesh
/// lerps previous→current on the Gfx SVTX pointer. HoldAnimPose is
/// not applied to Crash (it froze idx so the morph never ran).
/// Ripper Roo BIG TNT (RooOC type 39) is not a
/// mesh ping-pong: BIG_TNT is 1 CVTX frame. Stop-state trans does
/// <c>troty = randi(±10deg)</c> when GOOL <c>time(0.4s)</c> is 0.
/// time() is <c>(offset + draw_count) % period</c>. Without a 30 Hz gate
/// that test stays true for every present in the 33 ms wall bucket, so
/// yaw re-rolls at refresh rate. In-game pause must not keep skip-frame
/// look-aheads running.
/// World UV anim (draw_count @ 0x80057960) and water ripple use a dedicated
/// wall clock. Worlds run before object AdvanceWallClock, so they must not
/// reuse leftover ticks (that is +1 per display frame).
/// Bonus rounds use the same wall dt as gameplay. CardC save UI (Tawna
/// Congrats spawn, Warp_Out playnull) and the crate-tally complete lid
/// stay on the original 30 Hz pad — playnull is per interpret, and BonoC
/// spawn() is not Crash so the 30 Hz burst cap would eat CardC. Sticky
/// until NSInit so TryArmUnlock cannot re-arm 30 presents later.
/// Crash on the warthog (Hog Wild / Whole Hog) writes pathprog with
/// spd() then calcpath into x/z. Grounded 34+scale keeps StopAtWalls
/// but leaves pathprog at the full 30 Hz step, so the hog runs at
/// 30 Hz × fps. Keep dt/34 of pathprog, ComboBounce, and troty too.
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
    /// <summary>
    /// gfx_context: prims_tail +4 = draw_stamp, +8 = sync_stamp, +12 = ticks_per_frame.
    /// GoolUpdateObjects does frames_elapsed = c2_p-&gt;draw_stamp / 34.
    /// </summary>
    const uint DrawStampOff = 0x7Cu;
    const uint SyncStampOff = 0x80u;

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
    const uint NsLookupAddr = 0x80015A98u;
    const uint NsInitAddr = 0x80015B58u;
    const uint NsPteBucketsAddr = 0x8005C530u;
    const uint NsPageTableAddr = 0x8005C534u;
    const uint NsNsdAddr = 0x8005C540u;
    const uint NsdPageTableSizeOff = 0x404u;
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
    /// <summary>WillC hog ride. Spawn 36 / Run_Start 38 do not spd the path.</summary>
    const uint StateWarthogRun = 37;
    const uint StateWarthogJump = 39;
    /// <summary>WillC <c>ComboBounce</c> — crate combo on foot, lateral hog offset on the hog.</summary>
    const int MemComboBounce = 16;
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
    /// <summary>gool_process.path_length (entity path_length &lt;&lt; 8).</summary>
    const uint ObjPathLenOff = 0x118u;
    const uint ObjSpeedOff = 0x124u;
    const uint ObjColliderOff = 0x78u;
    const uint ObjParentOff = 0x64u;
    const uint ObjTrotOff = 0xB0u;
    const uint ObjMiscOff = 0xBCu;
    const uint ObjMemOff = 0x15Cu;
    const uint ObjEntityOff = 0x110u;
    const uint EntityTypeOff = 18u;
    const uint ObjExternalOff = 0x24u;
    const int ObjMemCount = 64;
    const int PlatSlotTrans = 0;
    const int PlatSlotRot = 3;
    const int PlatSlotVel = 6;
    const int PlatSlotTrot = 9;
    const int PlatSlotMisc = 12;
    const int PlatSlotPath = 15;
    const int PlatSlotSpeed = 16;
    const int PlatSlotMem = 17;
    const int PlatSlotCrash = 17 + ObjMemCount;
    const int PlatSlotScale = PlatSlotCrash + 3;
    const int PlatSlotCount = PlatSlotScale + 3;
    /// <summary>GOOL <c>360.0</c> (8.8). Seesaw wrap, not a teleport.</summary>
    const int Deg360 = 360 << 8;
    const int Deg180 = 180 << 8;

    enum PlatDelta
    {
        Linear,
        Ang12,
        Auto,
    }
    const uint ObjGlobalOff = 0x20u;

    const uint FlagGroundLand = 0x1u;
    const uint FlagFirstFrame = 0x20u;
    const uint Flag2D = 0x200u;
    const uint FlagTrackPathRot = 0x2u;
    const uint FlagStoppedBySolid = 0x8u;
    const uint FlagCollidable = 0x10u;
    const uint FlagGravity = 0x20u;
    const uint FlagTransMotion = 0x40u;
    const uint FlagInvisible = 0x100u;
    const uint FlagSolidGround = 0x4000u;
    const uint FlagSolidSides = 0x10000u;
    const uint FlagSolidTop = 0x20000u;
    const uint FlagStall = 0x10000000u;
    const uint GoolCategoryEnemy = 0x300u;
    /// <summary>Platform GOOL (RuiOC, RWaOC, PoPlC, …). Header category 0x600.</summary>
    const uint GoolCategoryPlatform = 0x600u;
    /// <summary>BoxC / crate GOOL header type (NTSC-U entity type 0x22).</summary>
    const uint GoolTypeBox = 0x22u;
    /// <summary>RooOC Ripper Roo objects. BIG TNT hops/rocks here, not BoxsC.</summary>
    const uint GoolTypeRooO = 39u;
    /// <summary>JunOC jungle objects. Decimal 22 — not BoxC 0x22.</summary>
    const uint GoolTypeJunO = 22u;
    /// <summary>LizaC. Header often fails to parse; RuiOC 42 without SOLID_TOP is the live mesh.</summary>
    const uint GoolTypeLiza = 47u;
    /// <summary>RuiOC. Pillars have SOLID_TOP; hoppers / bats do not.</summary>
    const uint GoolTypeRuiO = 42u;
    /// <summary>JunOC <c>Butterfly_Fly</c> / <c>Butterfly_Pose</c>.</summary>
    const uint StateButterflyFly = 1;
    const uint StateButterflyPose = 2;
    /// <summary>BonoC / CardC (NTSC-U GOOL header type).</summary>
    const uint GoolTypeBono = 56u;
    const uint GoolTypeCard = 57u;
    /// <summary>BonoC <c>Tawna_Congrats</c> — spawns CardC-34 after the wave.</summary>
    const uint StateTawnaCongrats = 1;
    /// <summary>BonoC <c>Brio_Escape</c> / <c>Cortex_Escape</c> at the round end.</summary>
    const uint StateBrioEscape = 6;
    const uint StateCortexEscape = 8;
    const uint HandlesAddr = 0x80060DB8u;
    const int HandleCount = 8;
    const uint HandleChildrenOff = 4u;
    const uint ObjSiblingOff = 0x68u;
    const uint ObjChildrenOff = 0x6Cu;
    const uint HandleListType = 2;
    const int ObjectPoolMax = 96;
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
    const uint CvtxEntryType = 20;
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

    struct PoseClock
    {
        public int Idx;
        public int From;
        public long Ts;
    }

    struct AnimPose
    {
        public uint Seq;
        public int Frame;
        public int Target;
        public int LastStep;
        public long Ts;
        public bool Have;
        public bool Holding;
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
    /// <summary>CardC / Warp_Out / Tawna Congrats — stay on the 30 Hz pad until NSInit.</summary>
    static bool _saveUiPad;
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
    static int _opath, _otrotY, _ocombo;
    static double _hogPathFrac, _hogTrotFrac, _hogComboFrac;
    static bool _crashHog;
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
    static readonly HashSet<uint> _animHold = new();
    static readonly Dictionary<uint, long> _waitHoldTs = new();
    static readonly Dictionary<uint, PoseClock> _poseClock = new();
    static readonly Dictionary<uint, AnimPose> _animPose = new();
    static int _poseHoldLog;
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
    static int _svtxLog;
    static int _svtxCrashLog;
    static int _svtxBoxLog;
    static bool _gfxHookTried;
    static bool _spawnBurst;
    static bool _didSpawn;
    static bool _spawnFirstFrame;
    static int _spawnBudget;
    static double _spawnAcc;
    static readonly Dictionary<uint, double> _spawnCredit = new();
    static readonly Dictionary<uint, double> _simAcc = new();
    static readonly Dictionary<uint, double[]> _platFrac = new();
    static readonly int[] _platFrom = new int[PlatSlotCount];
    static bool _platObj;
    static bool _platFirst;
    static bool _platChild;
    static bool _platCarry;
    static int _platLog;
    static int _objClassLog;
    /// <summary>Jump may OR SOLID_TOP. Remember hoppers from Wait so they stay on the 30 Hz skip.</summary>
    static readonly HashSet<uint> _pathHoppers = new();
    static int _stampLog;
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
            if (!Catalog.Levels.AllowsUnlockedFps(id) || WantsOriginalPad(m))
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

    /// <summary>
    /// Warp_Out, Tawna/Brio/Cortex end, or a live CardC object. Sticky so
    /// TryArmUnlock cannot re-arm after 30 presents (0.1 s at uncapped).
    /// </summary>
    static void ArmSaveUiPad()
    {
        if (_saveUiPad) return;
        _saveUiPad = true;
        PaceLog("save UI pad");
    }

    static bool WantsOriginalPad(IMemory m)
    {
        if (_saveUiPad) return true;
        try
        {
            if (IsWarpOut(m))
            {
                ArmSaveUiPad();
                return true;
            }
        }
        catch
        {
            // overlay swap
        }
        return false;
    }

    static void NoteSaveUiObject(IMemory m, uint obj)
    {
        if (_saveUiPad) return;
        if ((obj & 0xFF000000u) != 0x80000000u) return;
        try
        {
            if (m.ReadU32(obj) == HandleListType) return;
            if (!TryReadGoolClass(m, obj, out uint type, out _)) return;
            if (type == GoolTypeCard)
            {
                ArmSaveUiPad();
                return;
            }
            if (type != GoolTypeBono) return;
            uint state = m.ReadU32(obj + ObjStateOff);
            if (state is StateTawnaCongrats or StateBrioEscape or StateCortexEscape)
                ArmSaveUiPad();
        }
        catch
        {
            // object freed
        }
    }

    static void NoteSaveUiWorld(IMemory m)
    {
        if (_saveUiPad) return;
        try
        {
            if (IsWarpOut(m))
            {
                ArmSaveUiPad();
                return;
            }
            int left = ObjectPoolMax;
            for (int h = 0; h < HandleCount && left > 0; h++)
            {
                uint handle = HandlesAddr + (uint)(h * 8);
                uint child = m.ReadU32(handle + HandleChildrenOff);
                WalkSaveUi(m, child, ref left);
                if (_saveUiPad) return;
            }
        }
        catch
        {
            // overlay swap
        }
    }

    static void WalkSaveUi(IMemory m, uint obj, ref int left)
    {
        int n = 0;
        while ((obj & 0xFF000000u) == 0x80000000u && n++ < ObjectPoolMax && left-- > 0)
        {
            uint kind = m.ReadU32(obj);
            if (kind == HandleListType)
            {
                WalkSaveUi(m, m.ReadU32(obj + HandleChildrenOff), ref left);
            }
            else
            {
                NoteSaveUiObject(m, obj);
                if (_saveUiPad) return;
                WalkSaveUi(m, m.ReadU32(obj + ObjChildrenOff), ref left);
                if (_saveUiPad) return;
            }
            obj = m.ReadU32(obj + ObjSiblingOff);
        }
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
        _crashHog = false;
        _hogPathFrac = 0;
        _hogTrotFrac = 0;
        _hogComboFrac = 0;
        _solidObj = false;
        _gatedSolid = false;
        _objScaled = false;
        _crashAir = false;
        _landPhysAcc = 0;
        _landPhysRan = false;
        _lastBound.Clear();
        _animAcc.Clear();
        _animHold.Clear();
        _waitHoldTs.Clear();
        _poseClock.Clear();
        _animPose.Clear();
        _poseHoldLog = 0;
        _gateRot.Clear();
        _dispRotApplied = false;
        _dispTransApplied = false;
        _svtxPatched = false;
        _svtxLog = 0;
        _svtxCrashLog = 0;
        _svtxBoxLog = 0;
        _spawnBurst = false;
        _didSpawn = false;
        _spawnFirstFrame = false;
        _spawnBudget = 0;
        _spawnAcc = 0;
        _spawnCredit.Clear();
        _simAcc.Clear();
        _pathHoppers.Clear();
        _platFrac.Clear();
        _platObj = false;
        _platLog = 0;
        _objClassLog = 0;
        _stampLog = 0;
        _paceLog = 0;
        _vsyncsInGpu = 0;
        _inGpuUpdate = false;
        _didPresentThisGpu = false;
        _ticksTakenThisLoop = false;
        _loggedUnlockGpu = false;
        _inNsInit = false;
        _levelReady = false;
        _saveUiPad = false;
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
            File.AppendAllText(Path.Combine(AppPaths.LogsDir, "pacing.txt"),
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
    /// GOOL waits on frames_elapsed = draw_stamp/34. GpuUpdate copies
    /// ticks_elapsed into draw_stamp after the object tree. If that guest
    /// clock only moves +1 per present, wait=1 lasts 34 displays (~1 Hz at
    /// 60 FPS, faster at 120). Publish wall ticks into ticks_elapsed and
    /// every gfx draw_stamp before GoolUpdateObjects so /34 is 30 Hz at
    /// any refresh.
    /// </summary>
    static void PublishWallStamps(IMemory m)
    {
        try { m.WriteU32(TicksElapsedAddr, _guestTicks); }
        catch { /* overlay swap */ }
        WriteDrawStamp(m, GfxC1pAddr);
        WriteDrawStamp(m, GfxC2pAddr);
        WriteDrawStamp(m, GfxCurAddr);
        if (_stampLog >= 6) return;
        try
        {
            _stampLog++;
            uint fe = m.ReadU32(FramesElapsedAddr);
            PaceLog($"stamp ticks={_guestTicks} fe={fe} wallFe={_guestTicks / RefTicks} dt={_exactTicks:0.00}/{_frameTicks}");
        }
        catch { /* */ }
    }

    static void WriteDrawStamp(IMemory m, uint ptrAddr)
    {
        try
        {
            uint ctx = m.ReadU32(ptrAddr);
            if ((ctx & 0xFF000000u) != 0x80000000u) return;
            m.WriteU32(ctx + DrawStampOff, _guestTicks);
            m.WriteU32(ctx + SyncStampOff, _guestTicks);
        }
        catch
        {
            // overlay swap
        }
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
    /// Hog Wild / Whole Hog ride. Trans does <c>pathprog = spd(pathprog, 4)</c>
    /// then calcpath into x/z — not walk physics. Spawn/intro do not spd.
    /// </summary>
    static bool CrashOnWarthog(IMemory m, uint obj)
    {
        try
        {
            uint state = m.ReadU32(obj + ObjStateOff);
            return state == StateWarthogRun || state == StateWarthogJump;
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
        NoteSaveUiWorld(m);
        if (IsActive(m))
        {
            AdvanceWallClock(m);
            PublishWallStamps(m);
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
        GfxTransformSvtxAddr or GfxTransformCvtxAddr => PreGfxTransformMesh(c, m),
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
        _crashHog = false;
        _solidObj = false;
        _gatedSolid = false;
        _objScaled = false;
        _crashAir = false;
        _haveTransY = false;
        _spawnBurst = false;
        _didSpawn = false;
        _spawnFirstFrame = false;
        _platObj = false;
        _platFirst = false;
        _platChild = false;
        _platCarry = false;
        // Hold / NSInit run GOOL with pacing off. Hoppers Wait then Jump
        // (may OR SOLID_TOP). If we do not record them here, unlock treats
        // them as pillars and i+=1 runs every present until Crash dies and
        // they spawn again while unlocked.
        if (!IsActive(m))
        {
            IsPathHopper(m, c.A0);
            return true;
        }
        SnapshotObject(m, c.A0);
        _solidObj = _haveObj && !_crashObj && !KeepRealDt(m, c.A0);
        _platObj = _haveObj && !_crashObj && !_solidObj && IsPlatformObj(m, c.A0);
        _platFirst = _platObj && IsFirstFrame(m, c.A0);
        _platChild = _platObj && IsPacedPlatformChild(m, c.A0);
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
            else if (GamePaused(m))
            {
                // Display still runs every present. Do not accumulate skip
                // t or let wait=0 GOOL fire at refresh rate.
                _gatedSolid = true;
                DrawGatedObject(c, m, _obj);
                c.V0 = GoolSuccess;
                return false;
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
            if (_platObj)
            {
                // Original 34-tick spd()/Euler, then Post keeps dt/34.
                // Children of a paced parent only need ticks=34 for spd(force).
                if (!_platChild)
                    SnapshotPlatform(m, c.A0);
                WriteAllTicks(m, RefTicks);
            }
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
            case GfxTransformSvtxAddr:
            case GfxTransformCvtxAddr:
                RestoreSvtx(m);
                return;
            case GoolObjectUpdateAddr:
                break;
            default:
                return;
        }
        NoteSpawnUsed();
        if (_haveObj)
        {
            NoteSaveUiObject(m, _obj);
            RestoreGatedDisplayRot(m, _obj);
        }
        if (_solidObj && !_gatedSolid && _haveObj)
        {
            CaptureBound(m, _obj);
            CaptureGateRot(m, _obj);
        }
        if (_crashObj && !_objScaled && _haveObj)
            FinishPacedScale(m);
        else if (_platObj && !_platFirst && !_platChild && _haveObj)
            PacePlatform(m);
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
        EnsureGfxHook();
        if (IsActive(m))
        {
            ClampAnimFrame(m, c.A0);
            HoldAnimPose(m, c.A0);
        }
        ApplyGatedDisplayPose(m, c.A0);
        // Crash look is patched in PreGfxTransformMesh on the real SVTX
        // pointer (A0). PreTransform is too early and a second patch here
        // made PreGfx skip the buffer Gfx actually reads.
        if (IsActive(m) && c.A0 == _obj && !_crashObj)
            PatchObjectMesh(c, m, c.A0);
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
        _saveUiPad = false;
        _holdLocked = 0;
        _loggedUnlockGpu = false;
        ResetWaterClock();
        _lastBound.Clear();
        _animAcc.Clear();
        _animHold.Clear();
        _waitHoldTs.Clear();
        _poseClock.Clear();
        _animPose.Clear();
        _poseHoldLog = 0;
        _gateRot.Clear();
        _dispRotApplied = false;
        _dispTransApplied = false;
        _svtxPatched = false;
        _svtxLog = 0;
        _svtxCrashLog = 0;
        _svtxBoxLog = 0;
        _stampLog = 0;
        _platFrac.Clear();
        _platObj = false;
        _platLog = 0;
        _pathHoppers.Clear();
        _hogPathFrac = 0;
        _hogTrotFrac = 0;
        _hogComboFrac = 0;
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
            NoteSaveUiWorld(m);
            if (!Catalog.Levels.AllowsUnlockedFps(id) || _saveUiPad)
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

    static void EnsureGfxHook()
    {
        if (_gfxHookTried) return;
        _gfxHookTried = true;
        TryHookGfx(GfxTransformSvtxAddr, "func_80018964");
        TryHookGfx(GfxTransformCvtxAddr, "func_80018A40");
        try
        {
            HookManager.Commit();
        }
        catch (Exception ex)
        {
            PaceLog($"gfx commit fail {ex.GetType().Name}: {ex.Message}");
        }
    }

    static void TryHookGfx(uint addr, string name)
    {
        MethodInfo? mi = null;
        foreach (var ov in Dispatcher.Overlays.Values)
        {
            if (ov.Functions.TryGetValue(addr, out var fn))
            {
                mi = fn.Method;
                break;
            }
        }
        if (mi == null)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[]? types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types ?? Type.EmptyTypes; }
                catch { continue; }
                if (types == null) continue;
                foreach (var t in types)
                {
                    if (t is null) continue;
                    var found = t.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (found == null) continue;
                    mi = found;
                    break;
                }
                if (mi != null) break;
            }
        }
        if (mi == null)
        {
            PaceLog($"no gfx fn {name} 0x{addr:X8} overlays={Dispatcher.Overlays.Count}");
            return;
        }
        HookManager.AddPre(mi, PreGfxTransformMesh);
        HookManager.AddPost(mi, PostGfxTransformMesh);
        PaceLog($"hook gfx {mi.DeclaringType?.Name}.{mi.Name}");
    }

    /// <summary>
    /// GoolObjectTransform already NSLookup'd the svtx. A0 is the drawn
    /// frame, A2 the object. Patch toward the next item with wall t —
    /// PreTransform is too early (seq+4 still an EID).
    /// </summary>
    public static bool PreGfxTransformMesh(CpuContext c, IMemory m)
    {
        RestoreSvtx(m);
        if (!IsActive(m)) return true;
        if (GamePaused(m)) return true;
        // Full original 33 ms step already landed on the GOOL pose.
        if (_exactTicks >= RefTicks - 0.01) return true;
        uint drawn = c.A0;
        uint obj = c.A2;
        if ((obj & 0xFF000000u) != 0x80000000u)
            obj = _obj;
        if ((obj & 0xFF000000u) != 0x80000000u) return true;
        bool crash = false;
        bool box = false;
        try
        {
            crash = obj == m.ReadU32(CrashPtrAddr);
            box = TryReadGoolClass(m, obj, out uint typ, out _) && typ == GoolTypeBox;
        }
        catch
        {
            // keep going; mesh resolve decides
        }
        try
        {
            PatchDrawnLookAhead(c, m, obj, drawn, crash, box);
        }
        catch
        {
            RestoreSvtx(m);
        }
        return true;
    }

    public static void PostGfxTransformMesh(CpuContext c, IMemory m) => RestoreSvtx(m);

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

    /// <summary>
    /// Real wall dt every display. Standing platforms need Euler dt/34
    /// (SOLID_TOP). Everything else — lizards, turtles, boxes, unknown
    /// GOOL — one original 30 Hz step, same skip at 60 and uncapped.
    /// </summary>
    static bool KeepRealDt(IMemory m, uint obj)
    {
        if ((obj & 0xFF000000u) != 0x80000000u) return true;
        try
        {
            if (IsHud(m, obj)) return true;
            if (IsPathHopper(m, obj)) return false;
            if (HasSolidPhysics(m, obj) || IsJunocButterfly(m, obj)) return false;
            TryReadGoolClass(m, obj, out uint type, out uint cat);
            uint b = m.ReadU32(obj + ObjStatusBOff);
            if ((b & FlagSolidTop) != 0
                && (IsPlatformGoolType(type) || cat == GoolCategoryPlatform))
                return true;
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
        type is 11 or 26 or 28 or 33 or 42 or 46 or 58;

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

    /// <summary>
    /// Per-CODE hop (i+=1 / lerp / loopseek), any level. Jump may OR
    /// SOLID_TOP — sticky so it cannot become a pillar. Boxes are never hoppers.
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
            if (_pathHoppers.Contains(obj)) return true;
            if (type == GoolTypeLiza)
            {
                _pathHoppers.Add(obj);
                return true;
            }
            uint b = m.ReadU32(obj + ObjStatusBOff);
            bool hop = type == GoolTypeRuiO && (b & FlagSolidTop) == 0;
            if (!hop && IsLizaWaitFlags(b)) hop = true;
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
            if (!TryReadGoolClass(m, obj, out _, out uint cat) || cat != GoolCategoryPlatform)
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
            if (_crashHog)
                FinishWarthogScale(m);

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
        if (_crashHog)
        {
            m.WriteU32(o + ObjPathProgOff, (uint)_opath);
            m.WriteU32(o + ObjTrotOff + 4, (uint)_otrotY);
            m.WriteU32(o + ObjMemOff + (uint)MemComboBounce * 4u, (uint)_ocombo);
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
            uint col = m.ReadU32(obj + ObjColliderOff);
            if (crash != 0 && col == crash && (crash & 0xFF000000u) == 0x80000000u)
            {
                _platCarry = true;
                ReadPlatVec(m, crash + ObjTransOff, PlatSlotCrash);
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
                _platFrac.Clear();
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
                    WritePlatVec(m, crash + ObjTransOff, PlatSlotCrash, PlatDelta.Linear);
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
            if (d > Teleport || d < -Teleport) return to;
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
        m.WriteU32(o + ObjPathProgOff, (uint)KeepHogDelta(_opath, (int)m.ReadU32(o + ObjPathProgOff), ref _hogPathFrac, ang: false));
        m.WriteU32(o + ObjTrotOff + 4, (uint)KeepHogDelta(_otrotY, (int)m.ReadU32(o + ObjTrotOff + 4), ref _hogTrotFrac, ang: true));
        m.WriteU32(o + ObjMemOff + (uint)MemComboBounce * 4u,
            (uint)KeepHogDelta(_ocombo, (int)m.ReadU32(o + ObjMemOff + (uint)MemComboBounce * 4u), ref _hogComboFrac, ang: false));
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
                _otrotY = (int)m.ReadU32(obj + ObjTrotOff + 4);
                _ocombo = (int)m.ReadU32(obj + ObjMemOff + (uint)MemComboBounce * 4u);
            }
            else
            {
                _hogPathFrac = 0;
                _hogTrotFrac = 0;
                _hogComboFrac = 0;
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
