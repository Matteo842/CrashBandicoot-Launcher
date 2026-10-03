using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    /// <summary>
    /// LevelSpawnObjects only spawns the entities of the current zone's
    /// neighbours. Fruit and lives one zone further can be outside the 4:3
    /// frame but already inside the 16:9 side bands, so they popped in once
    /// the camera crossed a zone (#75: Castle Machinery's gem path, the lives
    /// of e2_TZ while the camera is still in x0_TZ). Spawn those FruiC
    /// pickups early through the game's own GoolObjectSpawn once they are on
    /// screen. Crates (stack links depend on spawn order) and every other
    /// entity keep the original timing.
    /// </summary>
    const uint GoolObjectSpawnAddr = 0x8001BCC8u;
    const uint GoolZoneObjectsTerminateAddr = 0x8001D200u;
    /// <summary>spawns[entity id]: bit 0 alive, bit 1 collected or killed.</summary>
    const uint EntitySpawnsAddr = 0x8005FF58u;
    const uint FreeObjectsAddr = 0x80060DF8u;
    const uint LdatPtrAddr = 0x8005C53Cu;
    const uint LdatExecMapOff = 0x14u;
    const uint GoolEntryType = 11u;
    const uint ZonePathsIdxOff = 0x204u;
    const uint ZonePathCountOff = 0x208u;
    const uint ZoneEntityCountOff = 0x20Cu;
    const uint ZoneNeighborCountOff = 0x210u;
    const uint ZoneNeighborsOff = 0x214u;
    const uint ZoneVisibilityDepthOff = 0x2E8u;
    const uint EntityGroupOff = 0x6u;
    const uint EntityIdOff = 0x8u;
    const uint EntitySubtypeOff = 0x13u;
    const uint EntityLocOff = 0x14u;
    const byte EntityGroupSpawn = 3;
    const byte FruiSpawnLife = 5;
    const byte FruiSpawnWumpa = 16;
    /// <summary>Same far limit as RGteCalcSpriteRotMatrix.</summary>
    const int NativeWideSpriteMaxDepth = 12000;
    /// <summary>Pool headroom left for the game's own spawns (96 objects).</summary>
    const int NativeWidePickupFreeMin = 16;
    const int NativeWidePickupSpawnsPerFrame = 8;

    /// <summary>Far zone entry → its EID, for zones holding early pickups.</summary>
    static readonly Dictionary<uint, uint> _nativeWidePickupZones = [];
    static readonly List<uint> _nativeWideNearZones = new(9);
    static readonly List<uint> _nativeWideFarZones = new(16);
    static readonly List<uint> _nativeWideDropZones = new(4);

    /// <summary>
    /// Before GfxUpdateMatrices: LevelSpawnObjects and CamUpdate have run,
    /// GoolUpdateObjects has not, so a new pickup is drawn this frame.
    /// </summary>
    static void SpawnNativeWidePickups(CpuContext c, IMemory m)
    {
        if (_inNsInit) return;
        try
        {
            DropEvictedNativeWidePickups(c, m);
            // Not NativeWideRendererActive: FinishNativeWideDraw clears it every frame.
            if (!GpuHle.WideFovActive || !NativeWideLevelSupported(m.ReadU32(Catalog.LevelIdAddr))) return;
            uint crash = m.ReadU32(CrashPtrAddr);
            if (!NativeWideGuestPointer(crash) || m.ReadU32(crash) is 0 or 2) return;
            if ((int)m.ReadU32(DrawSkipAddr) > 0) return;
            if ((m.ReadU32(DisplayFlagsAddr) & FlagSpinDeath) != 0 || GamePaused(m)) return;
            uint cur = m.ReadU32(CamZoneAddr);
            if (!NativeWideGuestPointer(cur)) return;
            uint curHeader = EntryItem(m, cur, 0);
            if (!NativeWideGuestPointer(curHeader)) return;
            // GoolObjectInit would page FruiC in from disc.
            uint ldat = m.ReadU32(LdatPtrAddr);
            if (!NativeWideGuestPointer(ldat)) return;
            if (NativeWideResidentEntry(m, m.ReadU32(ldat + LdatExecMapOff + GoolTypeFrui * 4u), GoolEntryType) == 0)
                return;
            int budget = NativeWideFreeObjects(m, NativeWidePickupFreeMin + NativeWidePickupSpawnsPerFrame)
                - NativeWidePickupFreeMin;
            if (budget <= 0) return;
            if (!CollectNativeWideFarZones(m, cur, curHeader) || _nativeWideFarZones.Count == 0) return;

            int proj = (int)m.ReadU32(NativeWideProjectionAddr);
            if (proj is <= 0 or > 4096) return;
            int far = (int)m.ReadU32(curHeader + ZoneVisibilityDepthOff) >> 8;
            if (far <= 0 || far > NativeWideSpriteMaxDepth) far = NativeWideSpriteMaxDepth;
            Span<short> rot = stackalloc short[9];
            NativeWideReadMsRot(m, rot);
            int camx = (int)m.ReadU32(NativeWideCamTransPrevAddr);
            int camy = (int)m.ReadU32(NativeWideCamTransPrevAddr + 4);
            int camz = (int)m.ReadU32(NativeWideCamTransPrevAddr + 8);
            int wideMid = NativeWideFrustumCore + GpuHle.WideMargin(NativeWideFrustumCore * 2)
                + NativeWideFrustumMidPad;

            foreach (uint zone in _nativeWideFarZones)
            {
                uint header = EntryItem(m, zone, 0);
                uint rect = EntryItem(m, zone, 1);
                if (!NativeWideGuestPointer(header) || !NativeWideGuestPointer(rect)) continue;
                int first = (int)(m.ReadU32(header + ZonePathsIdxOff) + m.ReadU32(header + ZonePathCountOff));
                int count = (int)m.ReadU32(header + ZoneEntityCountOff);
                int items = (int)m.ReadU32(zone + 0xCu);
                if (first < 2 || count <= 0 || first + count > items || items > 128) continue;
                int zx = (int)m.ReadU32(rect), zy = (int)m.ReadU32(rect + 4), zz = (int)m.ReadU32(rect + 8);
                for (int i = 0; i < count; i++)
                {
                    uint entity = EntryItem(m, zone, first + i);
                    if (!NativeWideGuestPointer(entity)) continue;
                    if (m.ReadU8(entity + EntityGroupOff) != EntityGroupSpawn) continue;
                    if (m.ReadU8(entity + EntityTypeOff) != GoolTypeFrui) continue;
                    byte spawn = m.ReadU8(entity + EntitySubtypeOff);
                    if (spawn is not (FruiSpawnLife or FruiSpawnWumpa)) continue;
                    // Ids 1-4 belong to the main object in GoolObjectSpawn.
                    uint id = m.ReadU16(entity + EntityIdOff);
                    if (id < 5 || (m.ReadU32(EntitySpawnsAddr + id * 4u) & 3u) != 0) continue;
                    // GoolObjectOrientOnPath: ((point << 2) + zone) << 8.
                    int wx = (((short)m.ReadU16(entity + EntityLocOff) << 2) + zx) << 8;
                    int wy = (((short)m.ReadU16(entity + EntityLocOff + 2) << 2) + zy) << 8;
                    int wz = (((short)m.ReadU16(entity + EntityLocOff + 4) << 2) + zz) << 8;
                    // Sprites skip mn_trans: TR = ms_rot * (trans - cam_trans_prev).
                    if (!NativeWideRotTrans(rot, 0, 0, 0, wx, wy, wz, camx, camy, camz, proj,
                            out int sx, out int sy, out int sz))
                        continue;
                    if (sz <= proj || sz > far) continue;
                    if (Math.Abs(sx) > wideMid || Math.Abs(sy) > NativeWideFrustumMidY) continue;
                    if (!CallNativeWideSpawn(c, m, zone, i)) return;
                    _nativeWidePickupZones[zone] = m.ReadU32(zone + 4u);
                    if (--budget == 0) return;
                }
            }
        }
        catch
        {
            // zone swap
        }
    }

    /// <summary>
    /// Zones one step past the current zone's neighbours. False when a
    /// neighbour is not resident: it could not be told apart from a far zone.
    /// </summary>
    static bool CollectNativeWideFarZones(IMemory m, uint cur, uint curHeader)
    {
        _nativeWideNearZones.Clear();
        _nativeWideFarZones.Clear();
        _nativeWideNearZones.Add(cur);
        int count = (int)m.ReadU32(curHeader + ZoneNeighborCountOff);
        if (count is <= 0 or > 8) return false;
        for (int i = 0; i < count; i++)
        {
            uint zone = NativeWideResidentEntry(m, m.ReadU32(curHeader + ZoneNeighborsOff + (uint)i * 4u),
                NativeWideZdatType);
            if (zone == 0) return false;
            if (!_nativeWideNearZones.Contains(zone)) _nativeWideNearZones.Add(zone);
        }
        foreach (uint near in _nativeWideNearZones)
        {
            if (near == cur) continue;
            uint header = EntryItem(m, near, 0);
            if (!NativeWideGuestPointer(header)) continue;
            int n = (int)m.ReadU32(header + ZoneNeighborCountOff);
            if (n is <= 0 or > 8) continue;
            for (int i = 0; i < n; i++)
            {
                // Not resident: its entities would page code in. Skip it.
                uint zone = NativeWideResidentEntry(m, m.ReadU32(header + ZoneNeighborsOff + (uint)i * 4u),
                    NativeWideZdatType);
                if (zone == 0 || _nativeWideNearZones.Contains(zone) || _nativeWideFarZones.Contains(zone))
                    continue;
                _nativeWideFarZones.Add(zone);
            }
        }
        return true;
    }

    /// <summary>
    /// LevelUpdate entry. ZoneTerminateDifference only kills objects of the
    /// old neighbours, so early pickups are dropped here, before NSZoneUnload
    /// can release their zone. A zone that becomes a neighbour is left to the
    /// game: LevelUpdate marks it and LevelSpawnObjects skips what is alive.
    /// LevelRestart clears cur_zone first; drop everything then.
    /// </summary>
    static void DropNativeWidePickupsOnZoneChange(CpuContext c, IMemory m)
    {
        if (_nativeWidePickupZones.Count == 0) return;
        uint next = c.A0;
        uint cur = m.ReadU32(CamZoneAddr);
        if (next == 0 || next == cur) return;
        _nativeWideDropZones.Clear();
        try
        {
            uint header = cur != 0 && NativeWideGuestPointer(next) ? EntryItem(m, next, 0) : 0;
            int count = NativeWideGuestPointer(header) ? (int)m.ReadU32(header + ZoneNeighborCountOff) : 0;
            if (count is < 0 or > 8) count = 0;
            foreach (uint zone in _nativeWidePickupZones.Keys)
            {
                bool neighbor = false;
                for (int i = 0; i < count && !neighbor; i++)
                    neighbor = NativeWideResidentEntry(m, m.ReadU32(header + ZoneNeighborsOff + (uint)i * 4u),
                        NativeWideZdatType) == zone;
                if (!neighbor) _nativeWideDropZones.Add(zone);
            }
        }
        catch
        {
            _nativeWideDropZones.Clear();
            _nativeWideDropZones.AddRange(_nativeWidePickupZones.Keys);
        }
        _nativeWidePickupZones.Clear();
        foreach (uint zone in _nativeWideDropZones)
            TerminateNativeWideZone(c, m, zone);
    }

    /// <summary>
    /// A far zone is not in the current loadlist; if NS reused its page, its
    /// pickups must not keep running on a stale entity.
    /// </summary>
    static void DropEvictedNativeWidePickups(CpuContext c, IMemory m)
    {
        if (_nativeWidePickupZones.Count == 0) return;
        _nativeWideDropZones.Clear();
        foreach (var (zone, eid) in _nativeWidePickupZones)
        {
            if (m.ReadU32(zone) != EntryMagic || m.ReadU32(zone + 4u) != eid
                || m.ReadU32(zone + 8u) != NativeWideZdatType)
                _nativeWideDropZones.Add(zone);
        }
        foreach (uint zone in _nativeWideDropZones)
        {
            _nativeWidePickupZones.Remove(zone);
            TerminateNativeWideZone(c, m, zone);
        }
    }

    static int NativeWideFreeObjects(IMemory m, int limit)
    {
        uint obj = m.ReadU32(FreeObjectsAddr) == 2u
            ? m.ReadU32(FreeObjectsAddr + 4u)
            : m.ReadU32(FreeObjectsAddr + ObjChildrenOff);
        int n = 0;
        for (; n < limit && NativeWideGuestPointer(obj); n++)
            obj = m.ReadU32(obj + ObjSiblingOff);
        return n;
    }

    static bool CallNativeWideSpawn(CpuContext c, IMemory m, uint zone, int index)
    {
        var saved = c.Snapshot();
        try
        {
            c.A0 = zone;
            c.A1 = (uint)index;
            Dispatcher.Call(c, m, GoolObjectSpawnAddr);
            return NativeWideGuestPointer(c.V0);
        }
        finally
        {
            c.Restore(saved);
        }
    }

    /// <summary>GoolZoneObjectsTerminate: kills objects whose zone is this entry.</summary>
    static void TerminateNativeWideZone(CpuContext c, IMemory m, uint zone)
    {
        var saved = c.Snapshot();
        try
        {
            c.A0 = zone;
            Dispatcher.Call(c, m, GoolZoneObjectsTerminateAddr);
        }
        catch
        {
            // objects already gone
        }
        finally
        {
            c.Restore(saved);
        }
    }
}
