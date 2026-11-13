using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    // #221: island map, sky colour missing in a strip at the screen edge while
    // the camera turns above 30 FPS. The map sky is a coarse band mesh (sky
    // world, header+0x1C set) in the zone's draw list (SLST). LevelUpdate
    // picks the list by progress >> 8, ZonePathProgressToLoc interpolates the
    // pose toward the next point. The retail map camera stops only on whole
    // points (+0x100 per step), so each list's band columns cover exactly that
    // view: between points the column entering at the edge is not drawn yet.
    // Before the game transforms its list, the next point's sky polygons the
    // list lacks are put at its start, where the band already is (drawn first,
    // behind everything), and the list is restored right after. Only sky:
    // scenery from the next list includes what this view hides, and only the
    // list's own order keeps the band behind the island (a separate pass drew
    // it over the huts).

    const uint MapCurPolyIdsAddr = 0x800566B8u;     // gp+0x2BC, the list GfxTransformWorlds reads
    const uint MapSparePolyIdsAAddr = 0x800566BCu;  // gp+0x2C0
    const uint MapSparePolyIdsBAddr = 0x80056700u;  // gp+0x304
    const uint MapSlstUpdateAddr = 0x80029B0Cu;
    const int MapPolyIdCapacity = 2610;             // SlstInit on lid 25

    static uint _mapSkyListAddr;
    // Camera point of the last two real steps ("Exact 30 FPS logic" passes lerp between them).
    static uint _mapStepZone, _mapStepPath, _mapPrevZone, _mapPrevPath;
    static int _mapStepPt, _mapPrevPt;
    static uint _mapStepMoved;
    const uint MapFramesElapsedAddr = 0x80060E04u;
    static byte[] _mapSkyListSaved = [];
    static byte[] _mapSkyCurSaved = [];
    static int _mapSkyListBytes;
    static readonly int[] _mapSkyStamp = new int[0x8000];
    static int _mapSkyFrame;
    static ushort[] _mapSkyExtra = new ushort[256];

    /// <summary>Before a world transform: add the neighbouring points' missing sky polygons.</summary>
    static void PatchMapSkyList(CpuContext c, IMemory m)
    {
        RestoreMapSkyList(m);
        if (!IslandMapShowing(m)) return;
        try
        {
            uint zone = m.ReadU32(CamZoneAddr);
            uint path = m.ReadU32(CamPathAddr);
            int progress = Math.Abs((int)m.ReadU32(CamProgressAddr));
            // Unlocked FPS: the pose is between progress >> 8 and the next point.
            // "Exact 30 FPS logic" draw passes: progress is whole and the camera is
            // lerped from the previous step's point (see below).
            bool between = (progress & 0xFF) != 0;
            if (!NativeWideGuestPointer(zone) || !NativeWideGuestPointer(path)) return;
            int pt = progress >> 8;
            if (!_interpRender && (_mapStepPath != path || _mapStepPt != pt))
            {
                // A real step: remember where the camera was for the next passes
                // (stamped with frames_elapsed; a pass runs on a copy of its step).
                (_mapPrevZone, _mapPrevPath, _mapPrevPt) = (_mapStepZone, _mapStepPath, _mapStepPt);
                (_mapStepZone, _mapStepPath, _mapStepPt) = (zone, path, pt);
                _mapStepMoved = m.ReadU32(MapFramesElapsedAddr);
            }
            if (!between && !_interpRender) return;
            uint header = EntryItem(m, zone, 0);
            if (!NativeWideGuestPointer(header)) return;
            Span<uint> curMeshes = stackalloc uint[8];
            uint skyMeshes = MapSkyMeshes(m, header, curMeshes);
            if (skyMeshes == 0) return;
            uint cur = m.ReadU32(MapCurPolyIdsAddr);
            uint spare = m.ReadU32(MapSparePolyIdsAAddr);
            if (spare == cur) spare = m.ReadU32(MapSparePolyIdsBAddr);
            if (spare == cur || !NativeWideGuestPointer(cur) || !NativeWideGuestPointer(spare)) return;

            int stamp = ++_mapSkyFrame;
            if (stamp == int.MaxValue)
            {
                Array.Clear(_mapSkyStamp);
                _mapSkyFrame = stamp = 1;
            }
            int curLength = m.ReadU16(cur);
            for (int i = 0; i < curLength; i++)
                _mapSkyStamp[m.ReadU16(cur + 4u + (uint)i * 2u) & 0x7FFF] = stamp;
            int extra = 0;
            if (!_interpRender)
            {
                if (between)
                    AddMapSkyExtras(c, m, zone, path, header, skyMeshes, curMeshes, pt, cur, spare, stamp, ref extra);
            }
            // "Exact 30 FPS logic" pass: the camera is lerped from the previous step's
            // point, which can be several points back or on another path (transit
            // paths switch at middle points). Its list is rebuilt as LevelUpdate does:
            // the nearer end's raw list, then deltas. A pass runs on a copy of the
            // newest step, whose own world draw is not seen here: the last recorded
            // point is usually the lerp's start. Only right after a move.
            else
            {
                uint fe = m.ReadU32(MapFramesElapsedAddr);
                uint fromZone = 0, fromPath = 0;
                int fromPt = -1;
                if ((_mapStepPath != path || _mapStepPt != pt) && fe - _mapStepMoved <= 1u)
                    (fromZone, fromPath, fromPt) = (_mapStepZone, _mapStepPath, _mapStepPt);
                else if (_mapStepPath == path && _mapStepPt == pt && _mapStepMoved == fe)
                    (fromZone, fromPath, fromPt) = (_mapPrevZone, _mapPrevPath, _mapPrevPt);
                if (NativeWideGuestPointer(fromZone) && NativeWideGuestPointer(fromPath))
                {
                    AddMapSkyPointExtras(c, m, zone, skyMeshes, curMeshes, fromZone, fromPath, fromPt,
                        cur, spare, stamp, ref extra);
                    // Lists are tight to their own view: the lerp between two points can
                    // bulge a few pixels past both (transit paths). One point further each
                    // way brings the next band column.
                    int length = m.ReadU16(path + ZonePathLengthOff);
                    if (pt > 0)
                        AddMapSkyPointExtras(c, m, zone, skyMeshes, curMeshes, zone, path, pt - 1, cur, spare, stamp, ref extra);
                    if (pt + 1 < length)
                        AddMapSkyPointExtras(c, m, zone, skyMeshes, curMeshes, zone, path, pt + 1, cur, spare, stamp, ref extra);
                }
            }
            if (extra == 0 || curLength + extra > MapPolyIdCapacity) return;

            int bytes = 4 + curLength * 2;
            if (_mapSkyListSaved.Length < bytes) _mapSkyListSaved = new byte[MapPolyIdCapacity * 2 + 4];
            for (int i = 0; i < bytes; i++) _mapSkyListSaved[i] = m.ReadU8(cur + (uint)i);
            _mapSkyListAddr = cur;
            _mapSkyListBytes = bytes;
            // List index 0 is transformed last and drawn first: the band's place.
            for (int i = curLength - 1; i >= 0; i--)
                m.WriteU16(cur + 4u + (uint)(i + extra) * 2u, m.ReadU16(cur + 4u + (uint)i * 2u));
            for (int i = 0; i < extra; i++)
                m.WriteU16(cur + 4u + (uint)i * 2u, _mapSkyExtra[i]);
            m.WriteU16(cur, (ushort)(curLength + extra));
        }
        catch
        {
            // zone swap
        }
    }

    /// <summary>Sky polygons of point <paramref name="pt"/> of <paramref name="path"/> the current list lacks.</summary>
    static void AddMapSkyPointExtras(CpuContext c, IMemory m, uint zone, uint skyMeshes, Span<uint> curMeshes,
        uint pathZone, uint path, int pt, uint cur, uint spare, int stamp, ref int extra)
    {
        int length = m.ReadU16(path + ZonePathLengthOff);
        if (pt < 0 || pt >= length || length > 256) return;
        Span<(int Item, uint Dir)> steps = stackalloc (int, uint)[length + 1];
        int n = 0;
        // LevelUpdate on a path change: raw list of the nearer end, then deltas.
        if (pt < length / 2)
        {
            steps[n++] = (0, 1u);
            for (int k = 1; k <= pt; k++) steps[n++] = (k, 1u);
        }
        else
        {
            steps[n++] = (length, 0u);
            for (int k = length - 1; k > pt; k--) steps[n++] = (k, 0u);
        }
        AddMapSkyListExtras(c, m, zone, skyMeshes, curMeshes, pathZone, path, steps[..n], cur, spare, stamp, ref extra);
    }

    /// <summary>
    /// Decodes the list of the point after <paramref name="pt"/> into <paramref name="spare"/>
    /// and collects its sky polygons the current list lacks. Past the last point:
    /// the following path's end, as ZonePathProgressToLoc heads there.
    /// </summary>
    static void AddMapSkyExtras(CpuContext c, IMemory m, uint zone, uint path, uint header, uint skyMeshes,
        Span<uint> curMeshes, int pt, uint cur, uint spare, int stamp, ref int extra)
    {
        int length = m.ReadU16(path + ZonePathLengthOff);
        uint nextZone = zone, slstRef = path;
        // LevelUpdate: forward to pt+1 applies items[pt+1] with dir 1.
        int itemIndex = pt + 1;
        uint dir = 1u;
        if (pt + 1 >= length)
        {
            if (!MapFollowingPath(m, path, header, out nextZone, out uint nextPath, out bool toLast)) return;
            slstRef = nextPath;
            // Raw lists: item 0 = first point, item length = last point.
            itemIndex = toLast ? m.ReadU16(nextPath + ZonePathLengthOff) : 0;
            dir = toLast ? 0u : 1u;
        }
        Span<(int Item, uint Dir)> step = [(itemIndex, dir)];
        AddMapSkyListExtras(c, m, zone, skyMeshes, curMeshes, nextZone, slstRef, step, cur, spare, stamp, ref extra);
    }

    /// <summary>
    /// Runs SlstUpdate over <paramref name="steps"/> (items of <paramref name="slstRef"/>'s SLST),
    /// starting from the current list, and collects the result's sky polygons the
    /// current list lacks. One step writes only <paramref name="spare"/>; more ping-pong
    /// with the current list's buffer, saved and put back here.
    /// </summary>
    static void AddMapSkyListExtras(CpuContext c, IMemory m, uint zone, uint skyMeshes, Span<uint> curMeshes,
        uint nextZone, uint slstRef, ReadOnlySpan<(int Item, uint Dir)> steps, uint cur, uint spare, int stamp, ref int extra)
    {
        // That list's world index -> this zone's, for sky meshes both zones draw.
        Span<int> remap = stackalloc int[8];
        remap.Fill(-1);
        if (nextZone == zone)
        {
            for (int wi = 0; wi < 8; wi++) if ((skyMeshes & (1u << wi)) != 0) remap[wi] = wi;
        }
        else
        {
            uint nextHeader = EntryItem(m, nextZone, 0);
            if (!NativeWideGuestPointer(nextHeader)) return;
            Span<uint> nextMeshes = stackalloc uint[8];
            MapSkyMeshes(m, nextHeader, nextMeshes);
            for (int nj = 0; nj < 8; nj++)
                for (int wi = 0; wi < 8 && nextMeshes[nj] != 0; wi++)
                    if ((skyMeshes & (1u << wi)) != 0 && curMeshes[wi] == nextMeshes[nj]) remap[nj] = wi;
        }
        uint slst = MapResidentEntry(m, slstRef);
        if (slst == 0 || steps.Length == 0) return;
        foreach (var step in steps)
            if (!NativeWideGuestPointer(EntryItem(m, slst, step.Item))) return;

        int curBytes = 4 + Math.Min((int)m.ReadU16(cur), MapPolyIdCapacity) * 2;
        if (steps.Length > 1)
        {
            if (_mapSkyCurSaved.Length < curBytes) _mapSkyCurSaved = new byte[MapPolyIdCapacity * 2 + 4];
            for (int i = 0; i < curBytes; i++) _mapSkyCurSaved[i] = m.ReadU8(cur + (uint)i);
        }
        uint src = cur, dst = spare;
        var saved = c.Snapshot();
        try
        {
            foreach (var step in steps)
            {
                c.A0 = EntryItem(m, slst, step.Item);
                c.A1 = src;
                c.A2 = dst;
                c.A3 = step.Dir;
                Dispatcher.Call(c, m, MapSlstUpdateAddr);
                (src, dst) = (dst, src);
            }
        }
        finally
        {
            c.Restore(saved);
        }
        uint result = src;
        CollectMapSkyExtras(m, result, remap, stamp, ref extra);
        if (steps.Length > 1)
            for (int i = 0; i < curBytes; i++) m.WriteU8(cur + (uint)i, _mapSkyCurSaved[i]);
    }

    static void CollectMapSkyExtras(IMemory m, uint list, ReadOnlySpan<int> remap, int stamp, ref int extra)
    {
        int listLength = Math.Min((int)m.ReadU16(list), MapPolyIdCapacity);
        // poly_id: poly index bits 0-11, world index 12-14, flag 15.
        for (int i = 0; i < listLength && extra < _mapSkyExtra.Length; i++)
        {
            ushort id = m.ReadU16(list + 4u + (uint)i * 2u);
            int world = remap[(id >> 12) & 7];
            if (world < 0) continue;
            id = (ushort)((id & 0x8FFF) | (world << 12));
            if (_mapSkyStamp[id & 0x7FFF] == stamp) continue;
            _mapSkyStamp[id & 0x7FFF] = stamp;
            _mapSkyExtra[extra++] = id;
        }
    }

    /// <summary>Bit per world whose mesh is a sky (header+0x1C); fills <paramref name="meshes"/> with each world's mesh.</summary>
    static uint MapSkyMeshes(IMemory m, uint header, Span<uint> meshes)
    {
        meshes.Clear();
        uint sky = 0;
        int worldCount = Math.Min((int)m.ReadU32(header), 8);
        for (int wi = 0; wi < worldCount; wi++)
        {
            uint mesh = m.ReadU32(header + 4u + (uint)wi * 0x40u + 0x10u);
            if (!NativeWideGuestPointer(mesh)) continue;
            meshes[wi] = mesh;
            if (m.ReadU32(mesh + 0x1Cu) != 0) sky |= 1u << wi;
        }
        return sky;
    }

    /// <summary>Entry behind an entry ref (PTE or EID); 0 when its page is not resident. Never loads.</summary>
    static uint MapResidentEntry(IMemory m, uint refAddr) => ResolveSvtxEntry(m, m.ReadU32(refAddr));

    /// <summary>ZonePathProgressToLoc's following path past the last point (relation &amp; 2, cam_mode != 1).</summary>
    static bool MapFollowingPath(IMemory m, uint path, uint header, out uint nextZone, out uint nextPath, out bool toLast)
    {
        nextZone = nextPath = 0;
        toLast = false;
        int count = Math.Min((int)m.ReadU32(path + 0x8u), 4);
        for (int i = 0; i < count; i++)
        {
            uint link = path + 0xCu + (uint)i * 4u;
            if ((m.ReadU8(link) & 2) == 0) continue;
            nextZone = MapResidentEntry(m, header + 0x214u + m.ReadU8(link + 1u) * 4u);
            if (nextZone == 0) return false;
            uint nextHeader = EntryItem(m, nextZone, 0);
            if (!NativeWideGuestPointer(nextHeader)) return false;
            nextPath = EntryItem(m, nextZone, (int)m.ReadU32(nextHeader + 0x204u) + m.ReadU8(link + 2u));
            if (!NativeWideGuestPointer(nextPath) || (short)m.ReadU16(nextPath + 0x20u) == 1) return false;
            toLast = (m.ReadU8(link + 3u) & 2) != 0;
            return true;
        }
        return false;
    }

    /// <summary>After the world transform: LevelUpdate decodes deltas from the exact list.</summary>
    static void RestoreMapSkyList(IMemory m)
    {
        if (_mapSkyListAddr == 0) return;
        try
        {
            for (int i = 0; i < _mapSkyListBytes; i++) m.WriteU8(_mapSkyListAddr + (uint)i, _mapSkyListSaved[i]);
        }
        catch
        {
            // overlay swap
        }
        _mapSkyListAddr = 0;
    }
}
