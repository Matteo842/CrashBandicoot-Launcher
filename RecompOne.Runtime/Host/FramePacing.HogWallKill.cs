using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

/// <summary>
/// Hog Wild / Whole Hog: touching a solid kills. In GoolObjectPhysics the
/// engine clears obj->event (0x13C) to 0xFF, the wall query stores the hit
/// solid's event there (0x700 on the spiked totems) and sets status_a 0x400,
/// and TransSmoothStopAtSolid (0x8002BE8C) sends that event to the object:
/// Willy_Warthog_Run takes Event7 into Willy_Death_Warthog. On the PS1 the
/// test runs once per frame, from points 35 cm apart along the ride.
/// Unlocked it runs every present from every point in between, so it also
/// finds spike corners the PS1 never samples: the second spiked totem near
/// the end of Hog Wild needs a quick swing to the right that passes at 30 FPS
/// when started on frames 24-30 after Matteo's save state, but only on 24 at
/// 240 FPS (artifacts/hogswing probe). The wall kill now only counts on the
/// present that starts a new 30 Hz gate frame, the PS1's own sample point;
/// on the presents in between the Death_Warthog change from the wall event
/// is skipped. The wall push itself still runs every present, and hits
/// from objects (EventHit and the rest) are untouched.
/// </summary>
public static partial class FramePacing
{
    const uint ObjEventOff = 0x13Cu;
    const uint WallKillEvent = 0x700u;
    /// <summary>NTSC-U Willy_Death_Warthog (gooc numbering 40).</summary>
    const uint StateDeathWarthogU = 39;

    /// <summary>Crash's physics on a hog present between gate frames.</summary>
    static bool _hogWallSubstep;

    /// <summary>PrePhysics on Crash.</summary>
    static void BeginHogWallSubstep(IMemory m) =>
        _hogWallSubstep = _crashObj && _crashHog && HogCamSyncOn(m) && !HogGateBoundary(m);

    /// <summary>PreChangeState: true = skip this wall kill.</summary>
    static bool SkipHogWallKill(CpuContext c, IMemory m, uint obj)
    {
        if (!_hogWallSubstep || c.A1 != StateDeathWarthogU) return false;
        if (m.ReadU32(obj + ObjEventOff) != WallKillEvent) return false;
        if (_hogWallKillLog < 8)
        {
            _hogWallKillLog++;
            PaceLog($"skip hog wall kill between gate frames fe={m.ReadU32(FramesElapsedAddr)}");
        }
        return true;
    }

    static int _hogWallKillLog;
}
