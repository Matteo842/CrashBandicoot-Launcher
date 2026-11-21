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
/// 240 FPS (artifacts/hogswing probe). The wall kill now only counts where
/// the PS1 samples: the hog moves spd(pathprog, 4) = 34 per frame, so the
/// PS1 tests path progress on a fixed grid (spawn progress + 34k). The
/// check runs on the first present whose progress (the guest's full step,
/// as the wall query sees it) reaches the next grid point; on the presents
/// in between the Death_Warthog change from the wall event is skipped.
/// The 30 Hz gate frame was tried first: its phase against the grid is
/// random, so it tested up to a whole step early and the totem still killed
/// 4 units before the PS1's sample point. The wall push itself still runs
/// every present, and hits from objects (EventHit and the rest) are
/// untouched.
/// </summary>
public static partial class FramePacing
{
    const uint ObjEventOff = 0x13Cu;
    const uint WallKillEvent = 0x700u;
    /// <summary>NTSC-U Willy_Death_Warthog (gooc numbering 40).</summary>
    const uint StateDeathWarthogU = 39;

    /// <summary>spd(pathprog, 4.0) per 34-tick step.</summary>
    const int HogPathFrameStep = 34;

    /// <summary>Crash's physics on a hog present between PS1 sample points.</summary>
    static bool _hogWallSubstep;
    /// <summary>PS1 progress grid: residue mod 34 (-1 = take it from the next ride present).</summary>
    static int _hogGridResidue = -1;
    static long _hogGridCell;

    /// <summary>PrePhysics on Crash.</summary>
    static void BeginHogWallSubstep(IMemory m)
    {
        _hogWallSubstep = false;
        if (!_crashObj || !_crashHog || !HogCamSyncOn(m)) return;
        try
        {
            int prog = (int)m.ReadU32(_obj + ObjPathProgOff);
            // Spawn and checkpoint progress start the grid; after a save
            // state or an FPS switch it starts where the ride is, as a
            // 30 FPS run from there would.
            if (_hogGridResidue < 0 || m.ReadU32(_obj + ObjStateOff) == StateWarthogSpawn)
            {
                _hogGridResidue = ((prog % HogPathFrameStep) + HogPathFrameStep) % HogPathFrameStep;
                _hogGridCell = HogGridCell(prog);
                return;
            }
            long cell = HogGridCell(prog);
            _hogWallSubstep = cell == _hogGridCell;
            _hogGridCell = cell;
        }
        catch
        {
            // overlay swap
        }
    }

    static long HogGridCell(int prog) => (long)Math.Floor((prog - (double)_hogGridResidue) / HogPathFrameStep);

    /// <summary>PreChangeState: true = skip this wall kill.</summary>
    static bool SkipHogWallKill(CpuContext c, IMemory m, uint obj)
    {
        if (!_hogWallSubstep || c.A1 != StateDeathWarthogU) return false;
        if (m.ReadU32(obj + ObjEventOff) != WallKillEvent) return false;
        if (_hogWallKillLog < 8)
        {
            _hogWallKillLog++;
            PaceLog($"skip hog wall kill between PS1 sample points fe={m.ReadU32(FramesElapsedAddr)}");
        }
        return true;
    }

    static int _hogWallKillLog;
}
