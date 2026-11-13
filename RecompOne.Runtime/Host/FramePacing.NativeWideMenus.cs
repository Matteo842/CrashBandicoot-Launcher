using System.Numerics;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    // The options menu (#189, title state 6, zone 0f_pZ) stands in front of a
    // flat stone wall (mesh 0f_pW, origin X -8000) with two torch pillars:
    // 12 columns of 400 from X -10400 to -5600, ending at the 4:3 edges. Each
    // side continues with its edge column mirrored about the wall end, then the
    // same column as it is, then mirrored again. Every seam joins a column edge
    // to that same edge, so the stone pattern never jumps. Only the wall plane
    // (Z 0) is copied, not the pillars in front of it.
    static bool NativeWideOptionsWall(IMemory m, NativeWideWorld world, uint level) =>
        level == LidTitleMap && world.PolyCount == 554
        && (int)m.ReadU32(world.Header) == -8000 && m.ReadU32(world.Header + 4) == 0
        && m.ReadU32(world.Header + 8) == 0;

    static readonly Vector3 OptionsLeftMin = new(-10400, -2100, -16), OptionsLeftMax = new(-10000, 1700, 16);
    static readonly Vector3 OptionsRightMin = new(-6000, -2100, -16), OptionsRightMax = new(-5600, 1700, 16);

    static readonly NativeWideCopies NativeWideOptionsWallCopies = new([
        new(OptionsLeftMin, OptionsLeftMax, Vector3.Zero, MirrorX: -10400),
        new(OptionsLeftMin, OptionsLeftMax, new(-800, 0, 0)),
        new(OptionsLeftMin, OptionsLeftMax, new(-800, 0, 0), MirrorX: -10400),
        new(OptionsRightMin, OptionsRightMax, Vector3.Zero, MirrorX: -5600),
        new(OptionsRightMin, OptionsRightMax, new(800, 0, 0)),
        new(OptionsRightMin, OptionsRightMax, new(800, 0, 0), MirrorX: -5600),
    ], []);

    static IReadOnlyList<NativeWideRepair> NativeWideOptionsWallRepairs(IMemory m, NativeWideWorld world, uint level)
    {
        var key = new NativeWideSceneryKey(level, m.ReadU32(world.Header), m.ReadU32(world.Header + 4),
            m.ReadU32(world.Header + 8), world.PolyCount, world.VertexCount);
        if (_nativeWideSceneryRepairs.TryGetValue(key, out var wall)) return wall;
        wall = NativeWideCopyRepairs(m, world, NativeWideOptionsWallCopies);
        PaceLog($"native-wide options wall repairs={wall.Count}");
        return _nativeWideSceneryRepairs[key] = wall;
    }
}
