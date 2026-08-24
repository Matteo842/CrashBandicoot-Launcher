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
/// After pit death, Warp_In / Force_Fall / death cine interpret once per
/// 34 wall ticks (still drawn). That acc is not shared with gated solids:
/// EvictDict in a busy room (Generator Room) dropped Crash so Death_Fall
/// never left playframe wait=1 / FadeToBlack. wait=1 is next Update, so
/// every present is FALL_KILL at 30 Hz × fps. Physics on those 30 Hz
/// steps is original 34 ticks — not 34+scale, and not a second skip.
/// HoldCrashStall is walk-only: Update still runs every present, so it
/// adds back anim_counter until 34 wall ticks. Death already skips extra
/// Updates. On the 30 Hz step _exactTicks is still the slice (~2 at 400
/// Hz), so the add-back cancelled the only decrement and playframes never
/// finished (Death_Spin / Warp_In freeze, world still ticking).
/// Walk / jump / hog keep trans every display frame.
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
/// GOOL category 0x600 solid meshes: RWaOC seesaws / PoPlC path plats
/// trans is a 30 Hz Euler step: <c>spd()</c> plus <c>rot += accel</c>.
/// Wall ticks 2–3 make spd 0; Euler every present at 400 Hz slams to
/// rest. Each display frame runs the original 34-tick trans then keeps
/// dt/34 (remainder so a 2-tick frame is not lost). RuiOC (Temple Ruins
/// orbit slabs, spears, torches) is the same executable as those flames:
/// <c>vectransf2</c>, <c>playanim</c> loops, <c>time()</c> spawn. One original
/// interpret per 34 wall ticks, still drawn — Euler every present froze
/// the process as soon as the camera saw them (30 FPS is fine).
/// RWaOC wall mill (Slippery Climb, Stormy Ascent, …) is the same
/// <c>time()</c> + child <c>vectransf2</c>; SOLID_TOP used to Euler the
/// slabs at refresh. Seesaw <c>PlatOrbitRot</c> stays Euler.
/// Sprites in the same executable (torch flame) stay on wall ticks — <c>scalex += 0.1S</c>
/// plus <c>200&lt;&lt;shrink</c> must not see ticks=34 every present.
/// Children that <c>vectransf2</c> from a paced parent are not scaled
/// again (that double-step added seesaw momentum).
/// RuiOC orbit slabs (Temple Ruins) trans does <c>spd(troty)</c> then
/// <c>vectransf2</c>, and on Crash <c>RotPlatCarryPlayer</c>. GoolObjectBound
/// sets collider after Pre; Physics clears it before Post. Snapshot Crash
/// every platform step and keep dt/34 of whatever trans actually wrote —
/// not <c>collider==crash</c> at Pre (that was always 0, so carry stayed a
/// full 34-tick orbit every present and StopAtWalls froze on touch).
/// Temple Ruins / Jaws PoPlC (type 11, the round path plats) is worse:
/// only those lids set <c>PlatformRotSpeed = 70deg</c> and then
/// <c>RotPlatCarryPlayer</c> (0.985 per trans). Euler Active at 240 embeds
/// Crash and PlotObjWalls hangs. All type-11 on those lids stay 30 Hz
/// so Wait CODE (<c>sleepframe(0)</c> shared with Active) does not Euler.
/// Bound-before-trans can clear collider; Interpret Pre rewrites it if
/// Crash is on the AABB so Wait and CarryCollider fire on every lid
/// (Cortex Power discs included), not only Temple / Jaws. Drop plats
/// (Generator Room / Cortex Power) CODE playframe 0↔1 picks spd(y)
/// up vs down — Euler flipped the bob every present. Same 30 Hz gate
/// as Active, not Wait/Spawn. Slippery Climb path ferries are that
/// Active/Auto gate: <c>LoopPathProg</c> / <c>TimePathProg</c> per
/// interpret. Euler of those SETs vibrates the mesh at refresh and
/// CarryCollider fights Crash's 34+scale (1 cm saw). Skip presents
/// lerp the disc and add the same visual delta to Crash — wall dt,
/// not an FPS table. RuiOC torch
/// flames stay gated even after sprite FLAG_2D. World FLAG_2D (PoRoC
/// mist) is not HUD — treating it as HUD Euler'd <c>scalex +=</c> into a
/// giant sprite over the pit and Death_Fall never reached FadeToBlack.
/// Gated SOLID_TOP skip presents freeze the Bound AABB while Crash still
/// does grounded 34+scale. He walks on a floor that is not moving, so he
/// jitters and slides off XZ movers. Spread the interpret's Crash delta
/// (CarryCollider + 0.985) across leftover wall ticks — do not re-run
/// 0.985, and do not Euler Active. Skip AABB follows the display lerp.
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
/// next: look-at-cam reverse, and look-ahead overshoots). Crash's mesh
/// is not lerped — blending WillC keys tears the face (eyes/brows) and
/// a signed 8-bit take exploded the whole model. PinsC (Pinstripe) is
/// the same SVTX wrap: unsigned lerp of signed xyz flies the suit and
/// head apart. Still gated as an enemy (one original interpret per 34
/// wall ticks); extra presents draw the authored key. HoldAnimWait still
/// steps Crash's pose at 30 Hz; extra presents hold the authored frame.
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
/// 30 Hz × fps. Keep dt/34 of pathprog, ComboBounce (mem scan, not a
/// WillC index), and troty. 15deg is per present — 8.8 vs 12-bit from
/// the raw step so &amp; 0xFFF cannot trash trot.
/// Jump Y cannot use that lerp: CODE sets vely and trans does
/// spd(vely, hang) on a 34-tick present, so ScaleExact(Y) is two
/// half-steps at 60 (looks like 30) and a rocket at 120/240. Rebuild
/// Y from hang×dt + gravity×dt like the foot jump. Ride is
/// TRACK_PATH_SIGN, not a WillC state index.
/// Hog spawn calcpath is a checkpoint snap — do not lerp XZ from the
/// death pose. Death cine is stateflag 0x4000 (not a WillC index).
/// CamFollow look-behind is cam_offset_z += 0x3200 per display frame
/// (12 original frames from -0x12C00 to +0x12C00). Scale that seek — not
/// CamFollow snaps, not CamAdjustProgress (PreLevelUpdate already paces
/// same-path progress). Double-scaling those made walk/spin slow-mo.
/// Death orbit is CamDeath (SPIN_DEATH). Do not lerp or rewind its pose:
/// GoolTransform SETs a circle, so dt/34 of the coords is a chord (wrong
/// radius). Skip extra CamDeath calls; one original interpret per 34 wall
/// ticks. Same clock as the death cine. CamFollow is untouched.
/// </summary>
public static partial class FramePacing
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
    const uint GoolObjectChangeStateAddr = 0x8001D698u;
    const uint GoolObjectUpdateAddr = 0x8001DA0Cu;
    const uint GoolObjectTransformAddr = 0x8001DE78u;
    const uint GoolObjectPhysicsAddr = 0x8001F30Cu;
    /// <summary>GoolObjectInterpret. Trans jal after Bound, before physics.</summary>
    const uint GoolObjectInterpretAddr = 0x800201DCu;
    const uint GoolSeekAddr = 0x80024628u;
    const uint GoolObjectCreateAddr = 0x8001C6C8u;
    const uint ObjectBoundsAddr = 0x80060E08u;
    const uint ObjectBoundCountAddr = 0x80061888u;
    const int ObjectBoundMax = 96;
    const uint ObjBoundOff = 0x8u;
    const uint LevelUpdateAddr = 0x80025A60u;
    const uint GpuUpdateAddr = 0x80016E5Cu;
    /// <summary>NTSC-U <c>fade_counter</c> / <c>fade_step</c>. GpuUpdate steps these every present.</summary>
    const uint FadeCounterAddr = 0x80061A34u;
    const uint FadeStepAddr = 0x80061A38u;
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
    /// <summary>
    /// NTSC-U Warp_In is 40 (gooc dump lists Death_Warthog there and Warp_In
    /// at 41). Spawn/respawn cine. Land-lock either index.
    /// </summary>
    const uint StateDeathWarthog = 40;
    const uint StateWarpIn = 41;
    /// <summary>WillC process vars. Hog lateral is ComboBounce (spd 1440/480); index is not trusted.</summary>
    const int HogMemCount = 24;
    /// <summary>Crate combo is ±1.0 (256). Hog strafe spd is thousands per 34-tick step.</summary>
    const int HogMemMinStep = 512;
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
    /// <summary>CamDeath (NTSC-U). SPIN_DEATH orbit; skip extras, do not rewrite pose.</summary>
    const uint CamDeathAddr = 0x8002BAB4u;
    const uint DisplayFlagsAddr = 0x800618B0u;
    const uint FlagSpinDeath = 0x10000u;

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
    /// <summary>gool_process.state_flags. Death_Fall / Death_Warthog set 0x4000.</summary>
    const uint ObjStateFlagsOff = 0x120u;
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
    /// <summary>WillC troty. RotPlatCarryPlayer does <c>spd(player.troty)</c>.</summary>
    const int PlatSlotCrashTrot = PlatSlotScale + 3;
    const int PlatSlotCount = PlatSlotCrashTrot + 1;
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
    /// <summary>WillC death cine <c>stateflag 0x4000</c> (Fall / Warthog). Not a state index.</summary>
    const uint FlagStateDeathCine = 0x4000u;
    const uint Flag2D = 0x200u;
    const uint FlagTrackPathRot = 0x2u;
    const uint FlagTrackPathSign = 0x4u;
    const uint FlagStoppedBySolid = 0x8u;
    const uint FlagCollidable = 0x10u;
    const uint FlagGravity = 0x20u;
    const uint FlagTransMotion = 0x40u;
    const uint FlagInvisible = 0x100u;
    const uint FlagSolidGround = 0x4000u;
    const uint FlagSolidSides = 0x10000u;
    const uint FlagSolidTop = 0x20000u;
    const uint FlagStall = 0x10000000u;
    const uint GoolCategoryHud = 0x200u;
    const uint GoolCategoryEnemy = 0x300u;
    /// <summary>Platform GOOL (RuiOC, RWaOC, PoPlC, …). Header category 0x600.</summary>
    const uint GoolCategoryPlatform = 0x600u;
    /// <summary>DispC lives / fruit / Tawna pickup HUD. Not world FLAG_2D (mist, torches).</summary>
    const uint GoolTypeDisp = 4u;
    /// <summary>BoxC / crate GOOL header type (NTSC-U entity type 0x22).</summary>
    const uint GoolTypeBox = 0x22u;
    /// <summary>PinsC Pinstripe. Enemy cat 0x300; SVTX skip like WillC.</summary>
    const uint GoolTypePins = 15u;
    /// <summary>RooOC Ripper Roo objects. BIG TNT hops/rocks here, not BoxsC.</summary>
    const uint GoolTypeRooO = 39u;
    /// <summary>JunOC jungle objects. Decimal 22 — not BoxC 0x22.</summary>
    const uint GoolTypeJunO = 22u;
    /// <summary>LizaC. Header often fails to parse; Wait flags without SOLID_TOP.</summary>
    const uint GoolTypeLiza = 47u;
    /// <summary>
    /// RuiOC (Temple Ruins / Jaws). Orbit slabs, spears, torches, crushers.
    /// Never Euler — CODE/trans is per interpret (<c>vectransf2</c>, <c>time()</c>).
    /// </summary>
    const uint GoolTypeRuiO = 42u;
    /// <summary>
    /// RWaOC. Wall mill + slide/pusher gate; seesaw / sensitive / iguana Euler.
    /// </summary>
    const uint GoolTypeRWaO = 46u;
    /// <summary>RWaOC seesaw array. Inclusive start of the Euler state range.</summary>
    const uint StateRwaOrbitArray = 4;
    /// <summary>RWaOC sensitive bob. Inclusive end of the Euler state range.</summary>
    const uint StateRwaSensitiveBob = 6;
    const uint LidLostCity = 32u, StateRwaPusherSpawn = 16u, StateRwaPusherLast = 18u;
    /// <summary>PoPlC path platforms. Euler + Pace; Auto <c>time()</c> still gates.</summary>
    const uint GoolTypePoPl = 11u;
    /// <summary>PoPlC <c>Platform_Path_Spawn</c> / Wait / Active / Auto. Drop is 1–4.</summary>
    const uint StatePoPlSpawn = 5;
    const uint StatePoPlWait = 6;
    const uint StatePoPlActive = 7;
    const uint StatePoPlAuto = 8;
    /// <summary>0.5 m. Wait/Active carry tests <c>player.y - y &gt; -0.5m</c>.</summary>
    const int HalfMeter = 0xC800;
    /// <summary>NTSC-U <c>s</c> / <c>t</c> — only lids with PoPlC 70deg + 0.985 carry.</summary>
    const uint LidTempleRuins = 28u;
    const uint LidJawsOfDarkness = 29u;
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
    static double _fadeAcc;
    static bool _fadeHold;
    static uint _fadeStepSaved;
    static int _deathReenterLog;
    static int _deathFadeLog;
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
    /// <summary>Extra 30 Hz presents while Crash is still in Warp_In after the 30-frame hold.</summary>
    static int _warpHold;
    /// <summary>Last PsyQ VSync HLE timestamp. Gap &gt; 0.5 ms starts a new GpuUpdate burst.</summary>
    static long _lastVsyncHleTs;
    static bool _armedThisGpu;
    static bool _gpuFinished;
    static bool _didPreUpdateObjects;

    static uint _obj;
    static int _ox, _oy, _oz, _orx, _ory, _orz, _oanim;
    static int _ovy, _ovx, _ovz, _ospeed;
    static int _opath, _otrotX, _otrotY, _otrotZ;
    static double _hogPathFrac, _hogTrotFracX, _hogTrotFracY, _hogTrotFracZ;
    static double _crashFracTX, _crashFracTY, _crashFracTZ;
    static double _crashFracVX, _crashFracVY, _crashFracVZ;
    static double _crashFracRX, _crashFracRY, _crashFracRZ;
    static double _crashFracSp;
    static readonly int[] _hogMem = new int[HogMemCount];
    static readonly double[] _hogMemFrac = new double[HogMemCount];
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
    static int _platColLog;
    /// <summary>Gated SOLID_TOP whose last interpret carried Crash.</summary>
    static uint _rideObj;
    static int _rideSnapX, _rideSnapY, _rideSnapZ;
    static bool _rideDidSnap;
    static double _rideRemX, _rideRemY, _rideRemZ;
    static int _rideAppX, _rideAppY, _rideAppZ;
    static int _rideLog;
    /// <summary>Crash already FinishPacedScale this present (object-list order).</summary>
    static bool _crashDidScale;
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
    static uint _crashGateState = uint.MaxValue;
    static bool _crashSpawnUsed;
    /// <summary>
    /// Land-lock wall acc. Must not live in <c>_simAcc</c>: Generator Room
    /// fills that dict and EvictDict drops Crash, so Death_Fall never gets
    /// another 34-tick step (wait=1 / fade stuck, no respawn).
    /// </summary>
    static double _crashLandAcc;
    static bool _inCamFollow;
    static int _camOffZ, _camOffX, _camOffY, _camZoom;
    static int _camLog;
    static bool _dcamArmed;
    static double _dcamAcc;
    static int _dcamLog;

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

}
