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
    /// #189: the island map is a 3D scene like a level, so it gets the 16:9 view.
    /// The title, menus, game over and logos share lid 25 and stay 4:3.
    /// </summary>
    public static bool IslandMapShowing(IMemory m)
    {
        try
        {
            return m.ReadU32(Catalog.LevelIdAddr) == LidTitleMap
                && TryReadCrash(m, out uint crash) && IsMapTraveler(m, crash);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>title struct pointer (gp + 0x2E4) and its current state.</summary>
    const uint TitleStructPtrAddr = 0x800566E0u;
    const uint TitleStructStateOff = 0x35D0u;

    /// <summary>
    /// #189: the options menu (title state 6, zone 0f_pZ), the game over screen
    /// (12, 0b_pZ) and the password/load menus (13/14, 0e_pZ) load a 3D world
    /// zone like the map (TitleLoadScreen type 1), so they get the 16:9 view
    /// too. The main menu (5) and the Naughty Dog logo (8) are 2D pictures.
    /// </summary>
    public static bool TitleMenuWorldShowing(IMemory m)
    {
        try
        {
            if (m.ReadU32(Catalog.LevelIdAddr) != LidTitleMap) return false;
            uint title = m.ReadU32(TitleStructPtrAddr);
            if ((title & 0xFF000000u) != 0x80000000u) return false;
            return m.ReadU32(title + TitleStructStateOff) is 6u or 12u or 13u or 14u;
        }
        catch
        {
            return false;
        }
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

    /// <summary>RA of CamUpdate's cam_mode 7 LevelUpdate (shared with the auto-cam same-path step).</summary>
    const uint IslandCamRa = 0x8002BA7Cu;
    /// <summary>RA of CamUpdate's cam_mode 8 (island-to-island) LevelUpdate.</summary>
    const uint IslandTransitCamRa = 0x8002B730u;
    /// <summary>
    /// NTSC-U <c>island_cam_state</c>: -1 map init, 1 follow, 5 / 6 leave to
    /// the next / previous island (cam_mode 7 spins +4 points per call).
    /// </summary>
    const uint IslandCamStateAddr = 0x80061994u;

    /// <summary>
    /// The island camera path is a ring of zone paths: a fractional progress
    /// on a path's last point already blends into the next path's point 0.
    /// On the PS1 every call moves a whole step (1 point, 4 while leaving the
    /// island) and a seam is crossed inside that step. Unlocked, the same-path
    /// steps keep dt/34, but the seam call jumped at once: forward a whole
    /// point (up to 4) in one present, backward a whole point after an
    /// exponential crawl to point 0 (dt/34 of the rest per present, ~120 ms
    /// nearly stopped at 60 FPS: #191 L1/R1 rotation stops at every seam,
    /// #196 hitches between levels). That crawl also left the camera on the
    /// wrong path while IsldC already saw it within 3 degrees and took the
    /// next move; leaving the island then spun forward a whole lap (#196).
    /// Keep moving dt/34 of the PS1 step along the ring: stay on the old path
    /// until the step crosses the seam, then enter the new one with the rest.
    /// </summary>
    static bool PaceIslandCamJunction(CpuContext c, IMemory m)
    {
        bool cam7 = c.RA == IslandCamRa;
        if (!cam7 && c.RA != IslandTransitCamRa) return false;
        if (m.ReadU32(Catalog.LevelIdAddr) != LidTitleMap) return false;
        int state = (int)m.ReadU32(IslandCamStateAddr);
        if (state <= 0) return false;
        uint zone = m.ReadU32(CamZoneAddr);
        uint path = m.ReadU32(CamPathAddr);
        uint newPath = c.A1;
        if ((path & 0xFF000000u) != 0x80000000u || (newPath & 0xFF000000u) != 0x80000000u)
            return false;
        int end = m.ReadU16(path + ZonePathLengthOff) << 8;
        int newEnd = m.ReadU16(newPath + ZonePathLengthOff) << 8;
        if (end <= 0x100 || newEnd <= 0) return false;
        int cur = (int)m.ReadU32(CamProgressAddr);
        int req = (int)c.A2;
        int newLast = newEnd - 0x100;
        // cam_mode 7: +4 points while leaving the island, else one point either
        // way (angle sign). cam_mode 8: one point, forward on odd states.
        int step = cam7 && (state & 4) != 0 ? 0x400 : 0x100;
        bool mayForward = cam7 || (state & 1) != 0;
        bool mayBackward = cam7 ? (state & 4) == 0 : (state & 1) == 0;
        bool forward = mayForward && cur < end && cur + step >= end && (req == 0 || req == newLast);
        bool backward = !forward && mayBackward && cur >= 0 && cur < 0x100;
        if (!forward && !backward) return false;
        if (_cameraProgressZone != zone || _cameraProgressPath != path)
            _cameraProgressFrac = 0;
        int kept = KeepCrashDelta(0, step, PathWrap, ref _cameraProgressFrac);
        if (forward ? cur + kept < end : kept <= cur)
        {
            c.A0 = zone;
            c.A1 = path;
            c.A2 = (uint)(forward ? cur + kept : cur - kept);
            _cameraProgressZone = zone;
            _cameraProgressPath = path;
            return true;
        }
        if (forward)
        {
            // Entered at point 0 it moves on forward; entered at its end (leaving
            // to the previous island) the transit path is walked backward.
            int rest = Math.Min(cur + kept - end, newLast);
            c.A2 = (uint)(req == 0 ? rest : req - rest);
        }
        else if (req == newLast)
        {
            // Previous path's last point blends into this path's point 0.
            c.A2 = (uint)(newLast + 0x100 - (kept - cur));
        }
        // else: a backward entry at point 0 (transit start back onto the ring)
        // is the same spot; take it as the PS1 does.
        _cameraProgressZone = c.A0;
        _cameraProgressPath = newPath;
        return true;
    }
}
