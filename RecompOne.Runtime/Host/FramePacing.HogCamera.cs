using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

/// <summary>
/// Hog Wild / Whole Hog run Crash at a fixed speed, so the original ties
/// every spawn-timed obstacle to his progress: a rotisserie starts its
/// spit cycle when its zone spawns, and the third one in Whole Hog only
/// leaves a ~230 ms takeoff window at that exact phase (#140).
/// Unlocked, CamFollow ran every present: CamAdjustProgress picks its
/// catch-up step from the current gap (delta/2 below 0x200, then 0x200),
/// and finer steps settle closer to Crash; it also sampled him half a
/// frame later. The camera ran ~0.9 frames ahead at 60 FPS and ~1.3 at
/// 120, and the zone switch spawned the next obstacles that much early.
/// Newborns then got their first update one present after the switch
/// instead of one frame. So while Crash rides the hog: CamFollow runs as
/// an original 34-tick step only on presents where the 30 Hz gate frame
/// advances, gated newborns wait for the next gate frame, and the view
/// lerps the last two 30 Hz camera poses.
/// </summary>
public static partial class FramePacing
{
    /// <summary>NTSC-U <c>cam_trans</c> / <c>cam_rot</c>. GfxUpdateMatrices copies them for objects.</summary>
    const uint CamTransAddr = 0x80057864u;
    const uint CamRotAddr = 0x80057870u;
    /// <summary>Hog camera moves ~40k per 30 Hz step; a respawn or path snap is far more.</summary>
    const int HogCamPoseTeleport = 0x80000;
    const int HogCamRotTeleport = 0x200;

    static bool _hogGateReady;
    static bool _hogGateBoundary;
    static uint _hogGateFrame = uint.MaxValue;
    static bool _hogCamFullStep;
    static bool _hogPoseValid;
    static bool _hogPosePatched;
    static readonly int[] _hogPoseFrom = new int[6];
    static readonly int[] _hogPoseTo = new int[6];
    static readonly int[] _hogPoseSaved = new int[6];
    static readonly int[] _hogPoseShown = new int[6];

    /// <summary>Unlocked, unpaused, Crash on the warthog (not the death cine).</summary>
    static bool HogCamSyncOn(IMemory m)
    {
        if (!IsActive(m) || GamePaused(m) || _exactTicks >= RefTicks - 0.01) return false;
        return TryReadCrash(m, out uint crash) && CrashOnWarthog(m, crash);
    }

    /// <summary>
    /// This present starts a new 30 Hz gate frame: the same test gated
    /// objects use (GatedShouldInterpret), taken once per game loop.
    /// </summary>
    static bool HogGateBoundary(IMemory m)
    {
        if (_hogGateReady) return _hogGateBoundary;
        EnsureFrameTime(m);
        uint frame = CurrentGateFrame(out _);
        _hogGateBoundary = frame != _hogGateFrame;
        _hogGateFrame = frame;
        _hogGateReady = true;
        return _hogGateBoundary;
    }

    static void ClearHogCam()
    {
        _hogMeshTo = 0;
        _hogGateReady = false;
        _hogGateFrame = uint.MaxValue;
        _hogCamFullStep = false;
        _hogPoseValid = false;
        // A patched pose stays restorable: Reset may run off the game thread.
    }

    /// <summary>
    /// PreCamFollow on Crash. False skips CamFollow this present; true with
    /// <see cref="_hogCamFullStep"/> runs it unscaled (seeks, catch-up, LevelUpdate).
    /// </summary>
    static bool HogCamFollow(IMemory m, uint obj, out bool run)
    {
        run = true;
        if (!TryReadCrash(m, out uint crash) || obj != crash) return false;
        if (!HogCamSyncOn(m))
        {
            _hogPoseValid = false;
            return false;
        }
        if (!HogGateBoundary(m))
        {
            run = false;
            return true;
        }
        try
        {
            ReadCamPose(m, _hogPoseFrom);
        }
        catch
        {
            return false;
        }
        _hogCamFullStep = true;
        return true;
    }

    static void EndHogCamFollow(IMemory m)
    {
        _hogCamFullStep = false;
        try
        {
            ReadCamPose(m, _hogPoseTo);
            _hogPoseValid = true;
        }
        catch
        {
            _hogPoseValid = false;
        }
    }

    /// <summary>
    /// Gated newborn while the hog camera is on the 30 Hz grid: the zone
    /// spawned it one present after the switch, the original one frame
    /// after. Its first update waits for the next gate frame.
    /// </summary>
    static bool DeferHogNewborn(IMemory m) =>
        _newObj && HogCamSyncOn(m) && !HogGateBoundary(m);

    /// <summary>
    /// Before GfxUpdateMatrices: lerp the last two 30 Hz poses by the gate
    /// phase, one 30 Hz step behind. The phase is already taken at the end of
    /// this present (the clock advanced before CamFollow), so adding dt again
    /// counted it twice: at 120 FPS t went .375/.63/.88/1 (clamped), a short
    /// last stride and a long first one every step, and Crash shook against
    /// the scenery (#207). Extrapolating past the step overshot whenever the
    /// catch-up speed changed and snapped back on the next step. Real pose
    /// comes back before objects run.
    /// </summary>
    static void ApplyHogCamPose(IMemory m)
    {
        RestoreHogCamPose(m);
        if (!_hogPoseValid || !HogCamSyncOn(m)) return;
        try
        {
            CurrentGateFrame(out double phase);
            double s = Math.Clamp(phase / RefTicks, 0, 1);
            if (s >= 1) return;
            ReadCamPose(m, _hogPoseSaved);
            for (int i = 0; i < 6; i++)
                if (_hogPoseSaved[i] != _hogPoseTo[i]) return; // pose moved since the step
            for (int i = 0; i < 3; i++)
            {
                long d = (long)_hogPoseTo[i] - _hogPoseFrom[i];
                if (d > HogCamPoseTeleport || d < -HogCamPoseTeleport) return;
                _hogPoseShown[i] = _hogPoseTo[i] - (int)Math.Round(d * (1 - s));
            }
            for (int i = 3; i < 6; i++)
            {
                int d = AngDelta(_hogPoseFrom[i], _hogPoseTo[i]);
                if (d > HogCamRotTeleport || d < -HogCamRotTeleport) return;
                _hogPoseShown[i] = _hogPoseTo[i] - (int)Math.Round(d * (1 - s));
            }
            WriteCamPose(m, _hogPoseShown);
            _hogPosePatched = true;
        }
        catch
        {
            // overlay swap
        }
    }

    static void RestoreHogCamPose(IMemory m)
    {
        if (!_hogPosePatched) return;
        _hogPosePatched = false;
        try
        {
            WriteCamPose(m, _hogPoseSaved);
        }
        catch
        {
            // overlay swap
        }
    }

    static uint _hogMeshFrom, _hogMeshTo, _hogMeshToEntry, _hogMeshFrame;
    /// <summary>NTSC-U WillC Willy_Warthog_Spawn (gooc 36): the hog intro.</summary>
    const uint StateWarthogSpawn = 35;

    /// <summary>
    /// Crash's mesh is never vertex-lerped, so unlocked his pose holds for a
    /// whole 30 Hz frame while he slides smoothly. On the hog the run cycle is
    /// 15 poses per half second and the legs read as stepping (#207). While he
    /// rides it (not the death cine), blend the previous drawn pose into the
    /// current one by the frames_elapsed phase: poses change exactly on
    /// guest-tick /34 boundaries, so strides are even and the shown pose is at
    /// most one original frame behind. The previous frame may belong to the
    /// other run sequence (RUN_2 → RUN_1); same vertex count is required. A pose
    /// held past its own frame (jump sleepframe) is drawn as authored.
    /// Only blend from the pose of the frames_elapsed frame right before: a
    /// tracker fed only by blended presents (full steps, the level-entry hold,
    /// pause skip the patch) blended from a pose several frames old and Crash
    /// twitched in the hog intro. Every Crash draw now feeds the tracker, and
    /// the intro (Willy_Warthog_Spawn, Crash standing beside the hog) is drawn
    /// as authored.
    /// </summary>
    static void PatchHogRideMesh(CpuContext c, IMemory m, uint obj, uint drawn, bool blend = true)
    {
        if ((drawn & 0xFF000000u) != 0x80000000u) return;
        if (!CrashOnWarthog(m, obj) || (m.ReadU32(obj + ObjStateFlagsOff) & FlagStateDeathCine) != 0)
        {
            _hogMeshTo = 0;
            return;
        }
        uint frame = m.ReadU32(FramesElapsedAddr);
        if (drawn != _hogMeshTo)
        {
            // Intro poses are deloaded mid-spawn: only blend from a frame its entry still holds.
            bool previous = _hogMeshTo != 0 && frame == _hogMeshFrame + 1
                && IsEntryFrame(m, _hogMeshToEntry, _hogMeshTo);
            _hogMeshFrom = previous ? _hogMeshTo : drawn;
            _hogMeshTo = drawn;
            _hogMeshToEntry = TrySvtxEntry(c, m, obj, out uint en, out _) ? en : 0;
            _hogMeshFrame = frame;
        }
        if (!blend || _hogMeshFrom == drawn || frame != _hogMeshFrame) return;
        if (GamePaused(m) || m.ReadU32(obj + ObjStateOff) == StateWarthogSpawn) return;
        double t = (_guestTicks % RefTicks + _tickFrac) / RefTicks;
        if (t >= 0.99) return;
        int n = SvtxVertCount(m, drawn);
        if (n <= 0 || n > SvtxVertMax || n != SvtxVertCount(m, _hogMeshFrom)) return;
        LerpSvtxFrame(m, drawn, _hogMeshFrom, drawn, n, t);
    }

    /// <summary>Every Crash draw (full steps, original rate, pause too) feeds the hog pose tracker.</summary>
    static void TrackHogRidePose(CpuContext c, IMemory m)
    {
        try
        {
            uint obj = c.A2;
            if ((obj & 0xFF000000u) == 0x80000000u && obj == m.ReadU32(CrashPtrAddr))
            {
                PatchHogRideMesh(c, m, obj, c.A0, blend: false);
                PatchWarpMesh(c, m, obj, c.A0, blend: false);
            }
        }
        catch
        {
            // object freed
        }
    }

    static bool IsEntryFrame(IMemory m, uint en, uint frame)
    {
        if ((en & 0xFF000000u) != 0x80000000u || m.ReadU32(en) != EntryMagic) return false;
        uint type = m.ReadU32(en + 8);
        if (type != SvtxEntryType && type != CvtxEntryType) return false;
        int items = (int)m.ReadU32(en + 12);
        return items is > 0 and <= 256 && FrameIndex(m, en, items, frame) >= 0;
    }

    static void ReadCamPose(IMemory m, int[] pose)
    {
        for (int i = 0; i < 3; i++)
        {
            pose[i] = (int)m.ReadU32(CamTransAddr + (uint)i * 4u);
            pose[3 + i] = (int)m.ReadU32(CamRotAddr + (uint)i * 4u);
        }
    }

    static void WriteCamPose(IMemory m, int[] pose)
    {
        for (int i = 0; i < 3; i++)
        {
            m.WriteU32(CamTransAddr + (uint)i * 4u, (uint)pose[i]);
            m.WriteU32(CamRotAddr + (uint)i * 4u, (uint)pose[3 + i]);
        }
    }
}
