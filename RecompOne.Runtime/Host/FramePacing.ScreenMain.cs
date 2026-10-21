using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    /// <summary>IsldC: the island map traveler (Crash walking the map paths).</summary>
    const uint GoolTypeIsld = 59u;
    /// <summary>Map, main menu, title, game over and logos share this lid (zones).</summary>
    const uint LidTitleMap = 25u;

    /// <summary>
    /// Screens whose main object (entity id 1, so it is <c>crash</c> and its
    /// Update runs PadUpdate) is not WillC: GamOC Level_End_Willy on the level
    /// completion screen, IsldC on the island map. They are paced as gated GOOL
    /// (one original step per 34 wall ticks, like the PS1). WillC state indices
    /// (Win_Card is 22, a "death" index), Crash physics and the hog path (IsldC
    /// has TRACK_PATH_SIGN) must not touch them.
    /// </summary>
    static bool IsScreenMain(IMemory m, uint obj)
    {
        try
        {
            return TryReadGoolClass(m, obj, out uint type, out _)
                && type is GoolTypeGamO or GoolTypeIsld;
        }
        catch
        {
            return false;
        }
    }

    static bool IsMapTraveler(IMemory m, uint obj)
    {
        try
        {
            return TryReadGoolClass(m, obj, out uint type, out _) && type == GoolTypeIsld;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Catalog kinds, plus the island map. Lid 25 is also the title, the CardC
    /// main menu, game over and the logos, which stay on the original pad: only
    /// the zone whose main object is IsldC runs unlocked.
    /// </summary>
    static bool AllowsUnlock(IMemory m, uint id)
    {
        if (Catalog.Levels.AllowsUnlockedFps(id)) return true;
        return id == LidTitleMap && TryReadCrash(m, out uint crash) && IsMapTraveler(m, crash);
    }

    /// <summary>
    /// The map allows pause, and CoreLoop tests <c>pads[0].tapped</c> for Start
    /// every loop; the pause menu reads it every present. A gated IsldC would
    /// run PadUpdate once per 30 Hz step and leave a stale tap for the other
    /// presents (pause on, off, on). Run PadUpdate on its skipped presents like
    /// a land-locked Crash. Taps pressed while paused belong to the pause menu,
    /// so they are not sampled for IsldC's latch (X on "resume" must not pick a
    /// level).
    /// </summary>
    static void RunSkippedMapPad(CpuContext c, IMemory m)
    {
        if (!TryReadCrash(m, out uint crash) || _obj != crash || !IsMapTraveler(m, crash)) return;
        RunSkippedCrashPad(c, m);
        _padRan = !GamePaused(m);
    }

    /// <summary>
    /// Island camera (cam_mode 7) going back asks for the whole path point
    /// before <c>cur_progress &gt;&gt; 8</c>: it walks the path ring forward and
    /// keeps the last point it passed. On the PS1 progress is always whole,
    /// so that is one point. Unlocked, PreLevelUpdate leaves a fraction, so the
    /// step was 1 to 2 points and the camera turned up to twice as fast going
    /// back as going forward (forward asks cur + 0x100). Give it the relative
    /// step the PS1 took.
    /// </summary>
    static int IslandCamBackStep(IMemory m, int cur, int req)
    {
        if (req >= cur || (req & 0xFF) != 0 || (cur & 0xFF) == 0) return req;
        if (m.ReadU32(Catalog.LevelIdAddr) != LidTitleMap) return req;
        return cur + (req - (cur & ~0xFF));
    }

    /// <summary>
    /// Going back from inside the first point (0 &lt; progress &lt; 0x100), the
    /// island camera (cam_mode 7 and the island-to-island cam_mode 8) jumps to
    /// the previous path at once. On the PS1 it left from exactly point 0.
    /// Finish the way to point 0 at wall speed first; the next call leaves
    /// from there. Forward exits leave from the path end, never from inside
    /// point 0 of a path longer than one point.
    /// </summary>
    static bool PaceIslandCamLeaveStart(CpuContext c, IMemory m)
    {
        if (m.ReadU32(Catalog.LevelIdAddr) != LidTitleMap) return false;
        uint zone = m.ReadU32(CamZoneAddr);
        uint path = m.ReadU32(CamPathAddr);
        if ((path & 0xFF000000u) != 0x80000000u || m.ReadU16(path + ZonePathLengthOff) <= 1)
            return false;
        int cur = (int)m.ReadU32(CamProgressAddr);
        if (cur <= 0 || cur >= 0x100) return false;
        if (_cameraProgressZone != zone || _cameraProgressPath != path)
        {
            _cameraProgressFrac = 0;
            _cameraProgressZone = zone;
            _cameraProgressPath = path;
        }
        c.A0 = zone;
        c.A1 = path;
        c.A2 = (uint)KeepCrashDelta(cur, 0, PathWrap, ref _cameraProgressFrac);
        return true;
    }
}
