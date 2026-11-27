using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    /// <summary>
    /// #213 island map wrap-around (ViewConfig.MapWrapAround). IsldC
    /// <c>IslandDoControls</c> never walks past slot 1 (N. Sanity Beach) or slot 31
    /// (Dr. Neo Cortex). With the option on and the Cortex slot unlocked
    /// (LEVELSUNLOCKED &gt;= 31), a fresh push forward on slot 31 goes to slot 1 and a
    /// push back on slot 1 goes to slot 31. The slots are on different islands with
    /// no path between them, so the map is reloaded the game's own way: c1 title.c
    /// <c>sub_8003263C</c> (fade out), then TitleUpdate kills every object and
    /// TitleLoadState(15) opens the island zone of <c>cur_map_level</c> and spawns a
    /// new IsldC there, exactly as when Crash comes back from a level. Camera, slot
    /// and level always match, nothing is moved by hand.
    /// </summary>
    const uint MapWrapFirstSlot = 1u;
    const uint MapWrapLastSlot = 31u;
    // IslandLevel holds the level ID as a GOOL fixed-point value (lid << 8).
    const uint MapWrapFirstLid = 9u << 8;  // N. Sanity Beach
    const uint MapWrapLastLid = 31u << 8;  // Dr. Neo Cortex
    const uint MapWrapFirstPath = 10u; // island 1 path
    const uint MapWrapLastPath = 30u;  // island 3 path
    /// <summary>c1 globals cur_map_level (gooc CURRENTLEVEL) and levels_unlocked.</summary>
    const uint MapWrapCurMapLevelAddr = 0x800618DCu;
    const uint MapWrapLevelsUnlockedAddr = 0x80061948u;
    /// <summary>pads[0].held: D-pad up 0x1000, right 0x2000, down 0x4000, left 0x8000.</summary>
    const uint MapWrapPadHeldAddr = 0x8005E720u;
    const uint MapWrapPadForward = 0x1000u | 0x2000u;
    const uint MapWrapPadBack = 0x4000u | 0x8000u;
    const uint MapWrapTitleShown = 3u;   // transition: finished fading in
    const uint MapWrapTitleFadeOut = 5u; // transition: started fading out
    const int MapWrapFadeStart = -256;
    /// <summary>c1 zone_header: neighbor_count, neighbors[8], display_flags.</summary>
    const uint ZoneNeighbourCountOff = 0x210u;
    const uint ZoneNeighboursOff = 0x214u;
    const uint ZoneDisplayFlagsOff = 0x2DCu;
    const int MapWrapMaxNeighbours = 8;

    // IsldC vars from IslandProgress (NTSC-J list, same on NTSC-U; probe-checked):
    // IslandCanRotate -4, IslandCurrentPath -3, IslandTargetPath -2, IslandLevel +1,
    // IslandCanMove +2, IslandDirections +3.
    const int MapWrapProgressVar = 16;

    static bool _mapWrapArmed;

    /// <summary>PostGpuUpdate, once per logic frame, outside any object update.</summary>
    static void UpdateMapWrap(IMemory m)
    {
        if (!ConfigManager.View.MapWrapAround)
        {
            _mapWrapArmed = false;
            return;
        }
        try
        {
            if (!TryMapWrapSlot(m, out uint traveler, out uint slot))
            {
                _mapWrapArmed = false;
                return;
            }
            uint held = m.ReadU32(MapWrapPadHeldAddr) & 0xF000u;
            // IsldC tests forward (right/up) first: at slot 1 a back push only
            // counts when forward is not held too, or IsldC walks to slot 2.
            bool push = slot == MapWrapLastSlot
                ? (held & MapWrapPadForward) != 0
                : (held & MapWrapPadBack) != 0 && (held & MapWrapPadForward) == 0;
            // Only a new push: holding a direction across the island stops at the
            // end slot as in retail; the next push wraps.
            if (!push)
            {
                _mapWrapArmed = true;
                return;
            }
            if (!_mapWrapArmed) return;
            _mapWrapArmed = false;
            BeginMapWrap(m, traveler, slot == MapWrapLastSlot ? MapWrapFirstSlot : MapWrapLastSlot);
        }
        catch
        {
            _mapWrapArmed = false;
        }
    }

    /// <summary>The map is fully shown and Crash stands still on slot 1 or 31.</summary>
    static bool TryMapWrapSlot(IMemory m, out uint traveler, out uint slot)
    {
        traveler = 0;
        slot = 0;
        if (m.ReadU32(Catalog.LevelIdAddr) != LidTitleMap
            || m.ReadU32(PausedAddr) != 0
            || m.ReadU32(NextLidAddr) != uint.MaxValue
            || m.ReadU32(TitleStateAddr) != TitleMap
            || (int)m.ReadU32(MapWrapLevelsUnlockedAddr) < (int)MapWrapLastSlot)
            return false;
        uint title = m.ReadU32(TitlePtrAddr);
        if ((title & 0xFF000000u) != 0x80000000u
            || m.ReadU32(title + TitleCurStateOff) != TitleMap
            || m.ReadU32(title + TitleNextStateOff) != TitleMap
            || m.ReadU32(title + TitleTransitionOff) != MapWrapTitleShown)
            return false;
        if (!TryReadCrash(m, out traveler) || !IsMapTraveler(m, traveler)) return false;

        slot = MapWrapVar(m, traveler, 0);
        uint lid = MapWrapVar(m, traveler, 1);
        uint path = MapWrapVar(m, traveler, -2);
        bool first = slot == MapWrapFirstSlot && lid == MapWrapFirstLid && path == MapWrapFirstPath;
        bool last = slot == MapWrapLastSlot && lid == MapWrapLastLid && path == MapWrapLastPath;
        if (!first && !last) return false;
        // IslandCanMove: arrived, camera settled (IsldC sets it with ISLANDCAMSTATUS 1).
        // IslandDirections 0 on both slots: forward = right/up, back = left/down.
        return MapWrapVar(m, traveler, 2) != 0
            && MapWrapVar(m, traveler, 3) == 0
            && MapWrapVar(m, traveler, -3) == path
            && m.ReadU32(IslandCamStateAddr) == 1;
    }

    static uint MapWrapVar(IMemory m, uint traveler, int fromProgress) =>
        m.ReadU32(traveler + 0x160u + (uint)(4 * (MapWrapProgressVar + fromProgress)));

    static void MapWrapWriteVar(IMemory m, uint traveler, int fromProgress, uint value) =>
        m.WriteU32(traveler + 0x160u + (uint)(4 * (MapWrapProgressVar + fromProgress)), value);

    static void BeginMapWrap(IMemory m, uint traveler, uint target)
    {
        // c1 zone_header display_flags (+0x2DC) bit 0 = objects spawned, bit 1 =
        // spawn pending. The traveler is entity 1 of 1a_pZ, a neighbour of every
        // island zone, so 1a_pZ stays "spawned" across the reload and LevelSpawnObjects
        // would never bring the killed traveler back. Clear both bits on the current
        // neighbours, as LevelRestart does before its reload: the new zone's
        // LevelUpdate then marks all its neighbours to spawn again.
        Span<uint> headers = stackalloc uint[MapWrapMaxNeighbours];
        uint zone = m.ReadU32(CamZoneAddr);
        if (!NativeWideGuestPointer(zone)) return;
        uint header = EntryItem(m, zone, 0);
        if (!NativeWideGuestPointer(header)) return;
        int count = (int)m.ReadU32(header + ZoneNeighbourCountOff);
        if (count <= 0 || count > MapWrapMaxNeighbours) return;
        for (int i = 0; i < count; i++)
        {
            uint neighbour = MapResidentEntry(m, header + ZoneNeighboursOff + (uint)i * 4u);
            if (neighbour == 0) return;
            headers[i] = EntryItem(m, neighbour, 0);
            if (!NativeWideGuestPointer(headers[i])) return;
        }
        for (int i = 0; i < count; i++)
            m.WriteU32(headers[i] + ZoneDisplayFlagsOff, m.ReadU32(headers[i] + ZoneDisplayFlagsOff) & ~3u);

        // The new IsldC spawns at CURRENTLEVEL (GotoIslandProg) and TitleLoadState
        // opens that slot's island zone (1a_pZ for slot 1, 3a_pZ for slot 31).
        m.WriteU32(MapWrapCurMapLevelAddr, target);
        // During the fade-out: no level pick (IslandCanMove), no L1/R1 turn, and
        // ISLANDCAMSTATUS 0 keeps IsldC from setting IslandCanMove again and the map
        // camera still (CamUpdate mode 7 returns at once).
        MapWrapWriteVar(m, traveler, 2, 0);
        MapWrapWriteVar(m, traveler, -4, 0);
        m.WriteU32(IslandCamStateAddr, 0);
        // sub_8003263C(15): TitleUpdate fades out, then reloads title state 15.
        uint title = m.ReadU32(TitlePtrAddr);
        m.WriteU32(FadeCounterAddr, unchecked((uint)MapWrapFadeStart));
        m.WriteU32(TitleStateAddr, TitleMap);
        m.WriteU32(title + TitleNextStateOff, TitleMap);
        m.WriteU32(title + TitleTransitionOff, MapWrapTitleFadeOut);
        PaceLog($"map wrap to slot {target}");
    }
}
