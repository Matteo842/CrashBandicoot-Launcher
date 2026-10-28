using System.Numerics;
using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    // The Lost City's pushers (RWaOC) are the walls sliding out of the back
    // wall of the corridor below the climb. Seen from the climb, the first
    // platform's beams hang in front of them, but the ordering table files
    // the pushers' large faces nearer (zindex 24 and three-vertex averages):
    // they paint over the beams, in 4:3 too, at the left screen edge. The
    // 16:9 side band draws the same beams with real depth, so the 4:3 part
    // looked cut off and the cut moved with the walls (#178). Their 4:3 core
    // part is depth-tested instead, against the core fill, which also takes
    // the platform's polygons here.
    static readonly List<NativeWidePrimRange> _nativeWideCoreDepthRanges = [];
    static uint _nativeWideCoreDepthStart;
    static bool _nativeWideCoreDepthOpen;
    static readonly Dictionary<NativeWideSceneryKey, int[]?> _nativeWideCoreOccluders = [];

    /// <summary>Before GfxTransformSvtx/Cvtx: open a primitive range for a pusher.</summary>
    static void BeginNativeWideCoreDepthObject(CpuContext c, IMemory m)
    {
        _nativeWideCoreDepthOpen = false;
        if (!GpuHle.NativeWideRendererActive) return;
        try
        {
            if (m.ReadU32(Catalog.LevelIdAddr) != LidLostCity) return;
            uint obj = c.A2;
            if ((obj & 0xFF000000u) != 0x80000000u) obj = _obj;
            if ((obj & 0xFF000000u) != 0x80000000u
                || !TryReadGoolClass(m, obj, out uint type, out _) || type != GoolTypeRWaO
                || m.ReadU32(obj + ObjStateOff) is < StateRwaPusherSpawn or > StateRwaPusherLast)
                return;
            if (!TryReadNativeWidePrimsTail(m, out uint tail)) return;
            _nativeWideCoreDepthStart = tail & 0x1FFFFCu;
            _nativeWideCoreDepthOpen = true;
        }
        catch
        {
            _nativeWideCoreDepthOpen = false;
        }
    }

    /// <summary>After GfxTransformSvtx/Cvtx: the pusher's primitives end at the new tail.</summary>
    static void EndNativeWideCoreDepthObject(IMemory m)
    {
        if (!_nativeWideCoreDepthOpen) return;
        _nativeWideCoreDepthOpen = false;
        try
        {
            if (!TryReadNativeWidePrimsTail(m, out uint tail)) return;
            uint end = tail & 0x1FFFFCu;
            // Consumed by this frame's DrawOTag; a frame that is not drawn
            // must not leave ranges that later primitives reuse.
            if (_nativeWideCoreDepthRanges.Count >= 32) _nativeWideCoreDepthRanges.Clear();
            if (end > _nativeWideCoreDepthStart)
                _nativeWideCoreDepthRanges.Add(new NativeWidePrimRange(_nativeWideCoreDepthStart, end));
        }
        catch
        {
            // overlay swap
        }
    }

    public static bool IsNativeWideCoreDepthPrimitive(uint physicalAddress)
    {
        foreach (var range in _nativeWideCoreDepthRanges)
            if (physicalAddress >= range.Start && physicalAddress < range.End)
                return true;
        return false;
    }

    static void ClearNativeWideCoreDepth()
    {
        _nativeWideCoreDepthRanges.Clear();
        _nativeWideCoreDepthOpen = false;
    }

    /// <summary>
    /// Sorted polygons of the camera zone's own mesh that the core fill also
    /// draws, so the core depth buffer holds them for the pushers; null when
    /// the mesh has none. The game paints its own image over the fill.
    /// </summary>
    static int[]? NativeWideCoreOccluders(IMemory m, NativeWideWorld world)
    {
        uint level = m.ReadU32(Catalog.LevelIdAddr);
        if (level != LidLostCity) return null;
        // The climb (d__wW): the first platform with its beams and merlons.
        if ((world.PolyCount, world.VertexCount, (int)m.ReadU32(world.Header), (int)m.ReadU32(world.Header + 8))
            != (2679, 2327, 93633, -5998))
            return null;
        var key = new NativeWideSceneryKey(level, m.ReadU32(world.Header), m.ReadU32(world.Header + 4),
            m.ReadU32(world.Header + 8), world.PolyCount, world.VertexCount);
        if (_nativeWideCoreOccluders.TryGetValue(key, out var polygons)) return polygons;
        var min = new Vector3(91550, -30700, -10);
        var max = new Vector3(93250, -29790, 1600);
        var origin = new Vector3((int)m.ReadU32(world.Header), (int)m.ReadU32(world.Header + 4), (int)m.ReadU32(world.Header + 8));
        var found = new List<int>();
        bool Inside(int vertex)
        {
            var p = Position(ReadNativeWideLocal(m, world, vertex)) + origin;
            return p == Vector3.Clamp(p, min, max);
        }
        for (int pi = 0; pi < world.PolyCount; pi++)
        {
            uint poly = world.Polygons + (uint)pi * 8;
            NativeWidePolygonVertices(FastU32(m, poly), FastU32(m, poly + 4), out int a, out int b, out int c);
            if (Inside(a) && Inside(b) && Inside(c)) found.Add(pi);
        }
        PaceLog($"native-wide level={level} core occluders={found.Count}");
        return _nativeWideCoreOccluders[key] = found.Count > 0 ? [.. found] : null;
    }
}
