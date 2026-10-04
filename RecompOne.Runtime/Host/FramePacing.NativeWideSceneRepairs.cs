using System.Numerics;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    // Some retail scenery ends at the authored 4:3 frustum. These additions
    // complete its exposed surfaces in world space; no rendered pixels move.
    // Repairs are deliberately asset-specific. A boundary can also be a real
    // cliff, doorway or hole, so extending every mesh boundary would be wrong.
    // Boundary repairs keep their true depth even where the scene would
    // otherwise draw its additions behind all real scenery.
    readonly record struct NativeWideRepair(int Polygon, NativeWideClipVertex A,
        NativeWideClipVertex B, NativeWideClipVertex C, bool Boundary = false);
    readonly record struct NativeWideEdge(Vector3 A, Vector3 B);
    readonly record struct NativeWideEdgeOwner(int Polygon, int Edge, int Count);
    readonly record struct NativeWideSceneryKey(uint Level, uint X, uint Y, uint Z, int Polygons, int Vertices);
    static readonly Dictionary<NativeWideSceneryKey, List<NativeWideRepair>> _nativeWideSceneryRepairs = [];
    static List<NativeWideRepair>? _nativeWideBeachSky;
    static readonly Dictionary<NativeWideSceneryKey, List<NativeWideRepair>> _nativeWideSkyArcs = [];

    static IReadOnlyList<NativeWideRepair> NativeWideSceneRepairs(IMemory m, NativeWideWorld world)
    {
        uint level = m.ReadU32(Catalogs.Catalog.LevelIdAddr);
        if (NativeWideSkyArc(m, world, level))
            return NativeWideSkyArcRepairs(m, world, level);
        if (level is not (7 or 9 or 12 or 15 or 17 or 18 or 24 or 26 or 32 or 35 or 44 or 46 or 55)) return Array.Empty<NativeWideRepair>();
        bool beach = level == 9 && world.PolyCount == 2664 && world.VertexCount == 3054
            && m.ReadU32(world.Header) == 8355 && m.ReadU32(world.Header + 4) == 5547
            && m.ReadU32(world.Header + 8) == 130513;
        bool gate = level == 18 && world.PolyCount == 2620 && world.VertexCount == 3114
            && m.ReadU32(world.Header) == 46280 && (int)m.ReadU32(world.Header + 4) == -45049
            && (int)m.ReadU32(world.Header + 8) == -2060;
        bool fortress = level == 26 && world.PolyCount == 2602 && world.VertexCount == 3024
            && m.ReadU32(world.Header) == 44642 && (int)m.ReadU32(world.Header + 4) == -43983
            && (int)m.ReadU32(world.Header + 8) == -1010;
        bool jungle = level == 12 && world.PolyCount == 1319 && world.VertexCount == 1407
            && m.ReadU32(world.Header) == 9900 && m.ReadU32(world.Header + 4) == 6303
            && m.ReadU32(world.Header + 8) == 122966;
        bool castle = level == 55 && world.PolyCount == 2615 && world.VertexCount == 2424
            && m.ReadU32(world.Header) == 43251 && m.ReadU32(world.Header + 4) == 4192
            && m.ReadU32(world.Header + 8) == 0;
        bool slippery = level == 46 && world.PolyCount == 2094 && world.VertexCount == 1998
            && m.ReadU32(world.Header) == 97600 && (int)m.ReadU32(world.Header + 4) == -48800
            && m.ReadU32(world.Header + 8) == 0;
        bool upstream = level == 15 && world.PolyCount == 517 && world.VertexCount == 536
            && m.ReadU32(world.Header) == 8197 && m.ReadU32(world.Header + 4) == 7651
            && m.ReadU32(world.Header + 8) == 100003;
        bool creek = level == 24 && world.PolyCount == 1208 && world.VertexCount == 1276
            && m.ReadU32(world.Header) == 8197 && m.ReadU32(world.Header + 4) == 6592
            && m.ReadU32(world.Header + 8) == 122966;
        bool creekNext = level == 24 && world.PolyCount == 1380 && world.VertexCount == 1509
            && m.ReadU32(world.Header) == 8197 && m.ReadU32(world.Header + 4) == 6468
            && m.ReadU32(world.Header + 8) == 114910;
        bool hog = level == 17 && world.PolyCount == 1232 && world.VertexCount == 1382
            && m.ReadU32(world.Header) == 8383 && m.ReadU32(world.Header + 4) == 7163
            && m.ReadU32(world.Header + 8) == 122966;
        // The Great Hall's end walls stop at the 4:3 edge as they approach the
        // camera: the hall's left and right ends, and the right wall where the
        // second gallery turns. Their stone cells repeat, so a block of original
        // cells is copied along Z instead (no seams, no coplanar overlap between
        // copies). Absolute X plane, Y and Z range of the block, Y offset, first
        // Z shift; copies step by the block length until the wall passes the
        // camera's nearest path point.
        NativeWideHallWalls? hallWalls = level != 44 ? null
            : (world.PolyCount, world.VertexCount, (int)m.ReadU32(world.Header), (int)m.ReadU32(world.Header + 8)) switch
            {
                (2466, 2395, 15063, 126774) => new(
                [
                    // Right end: three cells per period; the grid restarts after the column.
                    new(22799, 797, 6685, 121198, 122798, 0, 2400, 127200),
                    // Left end, beside and above its window.
                    new(5599, 2397, 3997, 122798, 124398, 0, 1600, 127200),
                    new(5599, 3997, 5597, 123998, 124398, 0, 400, 127200),
                    new(5599, 4797, 5597, 123998, 124398, 800, 0, 127200),
                    new(5599, 6397, 6997, 122798, 123398, 0, 600, 127200),
                ],
                // The window keeps only the far half of its arch surround.
                Complete: [], Mirror: [112, 113, 114], MirrorZ: 123398),
                (3148, 3050, 27889, 111327) => new(
                [
                    // Second gallery's right wall: one column per period. Each row
                    // continues after its own last cell.
                    new(35193, 5597, 6397, 111199, 111599, 0, 400, 115200),
                    new(35193, 3197, 5597, 111199, 111599, 0, 800, 115200),
                    new(35193, 2397, 3197, 111199, 111599, 0, 1400, 115200),
                ],
                // Cells cut along their diagonal at the end of two rows.
                Complete: [2681, 2682], Mirror: [], MirrorZ: 0),
                _ => null,
            };
        // Toxic Waste starts in front of a hazard-striped doorway. Its jambs stop
        // below the top of the 16:9 view, and the dark wall face beside them,
        // fading to black, keeps one triangle per side. Each listed half cell is
        // completed, then the whole cell is copied up by the given Y offsets.
        NativeWideCell[]? doorCells = level == 7 && world.PolyCount == 711 && world.VertexCount == 703
            && m.ReadU32(world.Header) == 8200 && m.ReadU32(world.Header + 4) == 6339
            && m.ReadU32(world.Header + 8) == 123690
            ? [new(653, []), new(658, [400]), new(650, [400, 800]), new(662, [400, 800])] : null;
        // The Lost City starts at the west end of the temple walkway. Its floor
        // and front wall stop just outside the 4:3 view, the jungle backdrop
        // behind them a little further left. All of them repeat, so whole
        // periods of original polygons are copied (absolute box, offset).
        NativeWideCopies? lostCity = level != 32 ? null
            : (world.PolyCount, world.VertexCount, (int)m.ReadU32(world.Header), (int)m.ReadU32(world.Header + 8)) switch
            {
                (699, 725, 6768, -5999) => new(
                [
                    // Floor and front wall cells repeat every 1600 units. The
                    // semi-transparent root decals on the floor stay behind.
                    new(new(10000, -33600, -7), new(11600, -32400, 1593), new(-1600, 0, 0), Opaque: true),
                    new(new(10000, -33600, -7), new(11600, -32400, 1593), new(-3200, 0, 0), Opaque: true),
                    // Backdrop panels alternate every 3200 units between two
                    // adjacent 64-halfword segments of an 8-bit page, so U + 128
                    // reads the other panel with the same material.
                    new(new(4400, -32800, -4007), new(7600, -29600, -4007), new(-3200, 0, 0), DU: 128),
                    new(new(4400, -33600, -4007), new(6000, -32800, -4007), new(-1600, 0, 0), DU: 192),
                    new(new(4400, -33600, -4007), new(6000, -32800, -4007), new(-3200, 0, 0), DU: 128),
                    // Its bottom edge shows above the walkway's back edge where no
                    // bushes stand in front. Reflect the bottom row below it.
                    new(new(4400, -33600, -4007), new(6000, -32800, -4007), Vector3.Zero, MirrorY: -33600),
                    new(new(4400, -33600, -4007), new(6000, -32800, -4007), new(-1600, 0, 0), DU: 192, MirrorY: -33600),
                    new(new(4400, -33600, -4007), new(6000, -32800, -4007), new(-3200, 0, 0), DU: 128, MirrorY: -33600),
                    // The upper backdrop runs the other way and is in the odd
                    // segment here; polygon 0 has the even one and the same CLUT.
                    new(new(4400, -30176, -5607), new(6000, -28576, -5607), new(-1600, 0, 0), DU: -64, Material: 0),
                    new(new(4400, -30176, -5607), new(6000, -28576, -5607), new(-3200, 0, 0), Material: 0),
                ], []),
                // The upper backdrop lacks the panel hidden behind the temple roof
                // in 4:3; the same panel appears one period (6400 units) earlier.
                (3127, 3297, 22967, -5998) =>
                    new([new(new(15599, -30176, -5606), new(17199, -28576, -5606), new(6400, 0, 0))], []),
                // The climb: the shaft's side walls are only 1200-1600 units deep,
                // with no front faces, so 16:9 sees past their near edges. Below
                // the right wall the back wall stops at X 98793. Its last column
                // pair is reflected, then repeated: the vertex colours stay
                // continuous and the brazier glow further left is not copied.
                // Mask pairs stay whole. The top row alternates two cells.
                (2679, 2327, 93633, -5998) => new(
                [
                    new(new(97993, -34005, -6), new(98793, -28405, -6), Vector3.Zero, MirrorX: 98793),
                    new(new(97993, -34005, -6), new(98793, -28405, -6), new(1600, 0, 0)),
                    new(new(97593, -28405, -6), new(98393, -28005, -6), new(800, 0, 0)),
                    new(new(97593, -28405, -6), new(98393, -28005, -6), new(1600, 0, 0)),
                    new(new(97593, -28405, -6), new(97993, -28005, -6), new(2400, 0, 0)),
                    // From below the right wall's bottom edge, the back wall shows
                    // a little above it too.
                    new(new(97593, -28805, -6), new(98393, -28005, -6), new(0, 800, 0)),
                    new(new(97593, -28805, -6), new(98393, -28005, -6), new(800, 800, 0)),
                ], [91193, 97593]),
                (2457, 2298, 93679, -5998) => new([], [91199, 97599]),
                _ => null,
            };
        // Sunset Vista's temple interior: climbing shafts and the rooms between them.
        // The outdoor opening and its corridor are not included.
        bool sunset = level == 35 && world.PolyCount == 3463 && world.VertexCount == 3183
            && m.ReadU32(world.Header) == 110000 && (int)m.ReadU32(world.Header + 4) == -82800
            && (int)m.ReadU32(world.Header + 8) == -4400;
        bool temple = NativeWideSunsetTemple(m, world);
        // The first two shafts share the temple's outer left wall (absolute X
        // 111200). Rooms and forest lie beyond it, so its continuation must hide them.
        float outerWall = !temple ? float.NaN : ((int)m.ReadU32(world.Header), (int)m.ReadU32(world.Header + 4)) switch
        {
            (110000, -82800) => 1200,
            (113200, -64800) => -2000,
            _ => float.NaN,
        };
        bool scenery = beach || gate || fortress || jungle || castle || slippery || upstream || creek || creekNext || hog
            || temple || hallWalls != null || doorCells != null || lostCity != null;
        bool sky = level == 9 && world.PolyCount == 21 && world.VertexCount == 19
            && m.ReadU32(world.Header + 0x1C) == 1;
        if (!scenery && !sky) return Array.Empty<NativeWideRepair>();
        // Several loaded meshes can need different additions in the same level.
        // Identify the asset, not its transient RAM address or the level alone.
        var key = new NativeWideSceneryKey(level, m.ReadU32(world.Header), m.ReadU32(world.Header + 4),
            m.ReadU32(world.Header + 8), world.PolyCount, world.VertexCount);
        var cached = scenery ? _nativeWideSceneryRepairs.GetValueOrDefault(key) : _nativeWideBeachSky;
        if (cached != null) return cached;
        if (hallWalls != null)
        {
            var hall = NativeWideWallBlockRepairs(m, world, hallWalls);
            PaceLog($"native-wide level={level} wall blocks repairs={hall.Count}");
            return _nativeWideSceneryRepairs[key] = hall;
        }
        if (doorCells != null)
        {
            var door = NativeWideCellRepairs(m, world, doorCells);
            PaceLog($"native-wide level={level} cell repairs={door.Count}");
            return _nativeWideSceneryRepairs[key] = door;
        }
        if (lostCity != null)
        {
            var copies = NativeWideCopyRepairs(m, world, lostCity);
            PaceLog($"native-wide level={level} copy repairs={copies.Count}");
            return _nativeWideSceneryRepairs[key] = copies;
        }

        var edges = new Dictionary<NativeWideEdge, NativeWideEdgeOwner>();
        var triangles = new NativeWideClipVertex[world.PolyCount * 3];
        for (int pi = 0; pi < world.PolyCount; pi++)
        {
            uint poly = world.Polygons + (uint)pi * 8;
            NativeWidePolygonVertices(FastU32(m, poly), FastU32(m, poly + 4), out int a, out int b, out int c);
            int o = pi * 3;
            triangles[o] = ReadNativeWideLocal(m, world, a);
            triangles[o + 1] = ReadNativeWideLocal(m, world, b);
            triangles[o + 2] = ReadNativeWideLocal(m, world, c);
            for (int ei = 0; ei < 3; ei++)
            {
                var edge = NativeWideEdgeKey(triangles[o + ei], triangles[o + (ei + 1) % 3]);
                edges[edge] = edges.TryGetValue(edge, out var owner)
                    ? owner with { Count = owner.Count + 1 } : new NativeWideEdgeOwner(pi, ei, 1);
            }
        }

        // Temple walls are axis aligned: side walls in X planes, back walls in Z planes.
        Dictionary<float, List<int>>? xPlanes = null, zPlanes = null;
        Dictionary<float, float>? wallBack = null;
        if (temple)
        {
            xPlanes = []; zPlanes = [];
            for (int pi = 0; pi < world.PolyCount; pi++)
            {
                var t0 = triangles[pi * 3]; var t1 = triangles[pi * 3 + 1]; var t2 = triangles[pi * 3 + 2];
                if (t0.X == t1.X && t1.X == t2.X) AddNativeWidePlane(xPlanes, t0.X, pi);
                if (t0.Z == t1.Z && t1.Z == t2.Z) AddNativeWidePlane(zPlanes, t0.Z, pi);
            }
            // Planes whose walls end toward the camera; their back walls also stop there.
            wallBack = [];
            foreach (var owner in edges.Values)
            {
                if (owner.Count != 1) continue;
                int o = owner.Polygon * 3;
                var a = triangles[o + owner.Edge];
                if (!NativeWideWallNearEdge(a, triangles[o + (owner.Edge + 1) % 3], triangles[o + (owner.Edge + 2) % 3])
                    || wallBack.ContainsKey(a.X)) continue;
                float back = float.MaxValue;
                foreach (int pi in xPlanes[a.X])
                    for (int k = 0; k < 3; k++) back = Math.Min(back, triangles[pi * 3 + k].Z);
                wallBack[a.X] = back;
            }
        }

        var repairs = new List<NativeWideRepair>();
        foreach (var owner in edges.Values)
        {
            if (owner.Count != 1) continue;
            int o = owner.Polygon * 3;
            var a = triangles[o + owner.Edge];
            var b = triangles[o + (owner.Edge + 1) % 3];
            var c = triangles[o + (owner.Edge + 2) % 3];
            if (sky)
            {
                if (a.Y != 64 || b.Y != 64) continue;
                // Continue the horizon colour below the existing sky strip.
                var bottomA = a with { Y = -4096 }; var bottomB = b with { Y = -4096 };
                repairs.Add(new(-1, a, b, bottomB)); repairs.Add(new(-1, a, bottomB, bottomA));
                continue;
            }
            uint p0 = m.ReadU32(world.Polygons + (uint)owner.Polygon * 8);
            int material = (int)((p0 >> 8) & 4095);
            Vector3? direction = null;
            Vector3? endDirection = null;
            float distance = 1600;
            if (beach && (material is not (593 or 595) || Math.Min(a.Z, b.Z) < 800 || Math.Max(a.Z, b.Z) > 4000
                || Math.Min(Math.Abs(a.X), Math.Abs(b.X)) < 2400)) continue;
            if (gate || fortress)
            {
                bool bank = gate ? material is (673 or 675) && Math.Max(a.Y, b.Y) <= -4800
                    : material is (645 or 647) && Math.Max(a.Y, b.Y) <= -5880;
                bool leftTrunk = gate
                    ? material is (603 or 615 or 619 or 627 or 631 or 633) && Math.Max(a.X, b.X) <= -3800 && Math.Min(a.Z, b.Z) >= 2200
                    : material is (579 or 591 or 595 or 601 or 605 or 607) && Math.Max(a.X, b.X) <= -2200 && Math.Min(a.Z, b.Z) >= 1150;
                bool rightTrunk = gate
                    ? material is (583 or 585 or 587 or 589 or 591) && Math.Min(a.X, b.X) >= 1480 && Math.Min(a.Z, b.Z) >= 2980
                    : material is (559 or 561 or 563 or 565 or 567) && Math.Min(a.X, b.X) >= 3112 && Math.Min(a.Z, b.Z) >= 1930;
                if (!bank && !leftTrunk && !rightTrunk) continue;
                if (leftTrunk) direction = -Vector3.UnitX;
                if (rightTrunk) direction = Vector3.UnitX;
            }
            if (jungle)
            {
                bool bank = material is (48 or 50 or 60 or 62 or 64 or 78)
                    && Math.Min(a.Z, b.Z) >= 1400 && Math.Max(a.Z, b.Z) <= 3100;
                bool leftTrunk = material is (82 or 84 or 86 or 92) && Math.Max(a.X, b.X) <= -2800
                    && Math.Min(a.Z, b.Z) >= 500 && Math.Max(a.Z, b.Z) <= 1800;
                bool rightTrunk = material is (82 or 84) && Math.Min(a.X, b.X) >= 7200
                    && Math.Max(a.Z, b.Z) <= -3952;
                if (!bank && !leftTrunk && !rightTrunk) continue;
                if (leftTrunk) direction = -Vector3.UnitX;
                if (rightTrunk)
                {
                    // The cut also clips the top of this trunk. Fan its upper
                    // contour upward while keeping the root at the bank. The
                    // offset depends on position, so adjoining edges still meet.
                    var axis = Vector3.Normalize(new Vector3(208, 0, 512));
                    distance = 3200;
                    direction = axis + Vector3.UnitY * (float)Math.Max(0, a.Y + 2504) / 6400;
                    endDirection = axis + Vector3.UnitY * (float)Math.Max(0, b.Y + 2504) / 6400;
                }
            }
            if (castle && (material is not (20 or 24 or 28) || a.X != -3656 || b.X != -3656)) continue;
            if (slippery && (a.Z != 0 || b.Z != 0 || !(
                a.X == 6400 && b.X == 6400 && material is (265 or 317 or 257 or 261)
                || Math.Min(a.X, b.X) >= 6400 && material == 287))) continue;
            if (upstream)
            {
                // Continue only the outer, static banks. Water triangles have
                // separate animated materials and must retain the river width.
                bool bank = material is (0 or 2) && Math.Min(Math.Abs(a.X), Math.Abs(b.X)) >= 2500
                    && Math.Min(a.Z, b.Z) >= -4200 && Math.Max(a.Z, b.Z) <= -2400;
                if (!bank || Math.Sign(a.X) != Math.Sign(b.X)) continue;
                direction = Math.Sign(a.X) * Vector3.UnitX;
            }
            if (creek)
            {
                // The opening river banks end through both turf and trunk
                // surfaces. Continue their shared cut in one direction so the
                // turf stays attached to its supporting bank and tree roots.
                bool bank = material is (40 or 54 or 56 or 62 or 68 or 311 or 315 or 321)
                    && Math.Min(Math.Abs(a.X), Math.Abs(b.X)) >= 1900
                    && Math.Min(a.Z, b.Z) >= -400 && Math.Max(a.Z, b.Z) <= 3100;
                bool leftTrunk = material is (16 or 18 or 28 or 517 or 519 or 521 or 523 or 525 or 529)
                    && Math.Max(a.X, b.X) <= -1900
                    && Math.Min(a.Z, b.Z) >= -550 && Math.Max(a.Z, b.Z) <= 1800;
                bool rightTrunk = material is (657 or 663 or 665) && Math.Min(a.X, b.X) >= 2800
                    && Math.Min(a.Z, b.Z) >= -460 && Math.Max(a.Z, b.Z) <= 650;
                bool bankFace = material is (509 or 317) && Math.Min(Math.Abs(a.X), Math.Abs(b.X)) >= 1800
                    && Math.Min(a.Z, b.Z) >= 2200 && Math.Max(a.Z, b.Z) <= 3040;
                // Beyond the log, the next trunk and the bank behind the right
                // totem reveal a second cut. Continue only their outer contour.
                bool laterLeftTrunk = material is (90 or 92 or 102) && Math.Max(a.X, b.X) <= -2300
                    && Math.Min(a.Z, b.Z) >= -2800 && Math.Max(a.Z, b.Z) <= -2200;
                bool laterRightBank = material is (54 or 56) && Math.Min(a.X, b.X) >= 3200
                    && Math.Min(a.Z, b.Z) >= -4168 && Math.Max(a.Z, b.Z) <= -2896;
                if ((!bank && !leftTrunk && !rightTrunk && !bankFace && !laterLeftTrunk && !laterRightBank)
                    || Math.Sign(a.X) != Math.Sign(b.X)) continue;
                direction = Math.Sign(a.X) * Vector3.UnitX;
            }
            if (creekNext)
            {
                // The far side of the same opening spans the adjacent WGEO.
                // Its turf continues behind the totem, leaving the river open.
                bool bank = material is (8 or 10) && Math.Min(a.X, b.X) >= 4200
                    && Math.Min(a.Z, b.Z) >= 2688 && Math.Max(a.Z, b.Z) <= 3888;
                if (!bank) continue;
                direction = Vector3.UnitX;
            }
            if (hog)
            {
                // The starting banks stop underneath the foreground foliage.
                // Continue their outer slope and rear corner together. The
                // positive Z component covers the low corner nearest the camera.
                bool bank = material is (8 or 10 or 44 or 46 or 48 or 50 or 336)
                    && Math.Min(Math.Abs(a.X), Math.Abs(b.X)) >= 1100
                    && Math.Min(a.Z, b.Z) >= 0 && Math.Max(a.Z, b.Z) <= 3032;
                bool roofLeft = material is (224 or 230) && Math.Max(a.X, b.X) <= -3000;
                bool roofRight = material is (224 or 226) && Math.Min(a.X, b.X) >= 4200;
                bool canopy = material == 70 && Math.Min(Math.Abs(a.X), Math.Abs(b.X)) >= 1480;
                if ((!bank && !roofLeft && !roofRight && !canopy) || Math.Sign(a.X) != Math.Sign(b.X)) continue;
                // A turf join below the root replaces this inward-facing edge.
                if (owner.Polygon == 530 && a.Z == b.Z) continue;
                direction = new Vector3(Math.Sign(a.X), 0.5f, 0.4f);
                distance = 2400;
                if (roofLeft || roofRight || canopy) direction = Math.Sign(a.X) * Vector3.UnitX;
            }
            if (temple)
            {
                // Side walls end at the camera's 4:3 edge; continue their near
                // contour toward the camera. Back walls behind them also stop at
                // the wall plane; continue those outward. Coplanar faces beyond
                // an edge mean a window or doorway, which must stay open. The
                // bridge front below the first shaft's right wall also stops short.
                distance = 3200;
                float yLow = Math.Min(a.Y, b.Y), yHigh = Math.Max(a.Y, b.Y);
                bool bridge = sunset && a.X == 6800 && b.X == 6800 && a.Z == 6000 && b.Z == 6000
                    && c.Z == 6000 && yHigh <= 0;
                if (bridge) direction = Vector3.UnitX;
                else if (NativeWideWallNearEdge(a, b, c))
                {
                    if (a.Z == b.Z && NativeWidePlaneContinues(triangles, xPlanes![a.X], 2, a.Z, 1,
                        yLow, yHigh, distance)) continue;
                    direction = Vector3.UnitZ;
                }
                else if (a.X == b.X && a.Z == b.Z && b.Z == c.Z
                    && wallBack!.TryGetValue(a.X, out float back) && a.Z <= back + 16)
                {
                    float sign = Math.Sign(a.X - c.X);
                    if (sign == 0 || NativeWidePlaneContinues(triangles, zPlanes![a.Z], 0, a.X, sign,
                        yLow, yHigh, distance)) continue;
                    direction = sign * Vector3.UnitX;
                }
                else continue;
            }
            if (!TryNativeWideMaterial(m, world, owner.Polygon, 0, out _, out _,
                out short u0, out short v0, out short u1, out short v1, out short u2, out short v2)) continue;
            Vector2[] uv = [new(u0, v0), new(u1, v1), new(u2, v2)];
            int added = repairs.Count;
            AddNativeWideSceneryStrip(repairs, owner.Polygon, a, b, c,
                uv[owner.Edge], uv[(owner.Edge + 1) % 3], uv[(owner.Edge + 2) % 3], direction, endDirection, distance);
            if (a.X == outerWall && b.X == outerWall && a.Z == 6000 && b.Z == 6000 && direction == Vector3.UnitZ)
                for (int i = added; i < repairs.Count; i++) repairs[i] = repairs[i] with { Boundary = true };
        }
        if (hog)
        {
            // Grass ends around the narrow root. Join the two turf corners
            // below it; extending the root itself would create a wooden wall.
            var a = ReadNativeWideLocal(m, world, 672);
            var b = ReadNativeWideLocal(m, world, 678);
            var c = ReadNativeWideLocal(m, world, 673);
            if (TryNativeWideMaterial(m, world, 530, 0, out _, out _,
                out short u0, out short v0, out short u1, out short v1, out short u2, out short v2))
            {
                repairs.Add(new(530, a with { U = u0, V = v0 }, b with { U = u2, V = v2 }, c with { U = u1, V = v1 }));
                AddNativeWideSceneryStrip(repairs, 530, a, b, c, new(u0, v0), new(u2, v2), new(u1, v1),
                    new Vector3(1, 0.5f, 0.4f), distance: 2400);
            }
        }
        PaceLog($"native-wide level={level} {(scenery ? "scenery" : "sky")} repairs={repairs.Count}");
        if (scenery) _nativeWideSceneryRepairs[key] = repairs; else _nativeWideBeachSky = repairs;
        return repairs;
    }

    readonly record struct NativeWideWallBlock(float X, float YLow, float YHigh, float ZFrom, float ZTo,
        float DY, float Shift, float Until);
    // Blocks to copy, polygons whose cut cell is completed, and polygons mirrored across MirrorZ.
    sealed record NativeWideHallWalls(NativeWideWallBlock[] Blocks, int[] Complete, int[] Mirror, float MirrorZ);

    static List<NativeWideRepair> NativeWideWallBlockRepairs(IMemory m, NativeWideWorld world, NativeWideHallWalls walls)
    {
        int hx = (int)m.ReadU32(world.Header), hy = (int)m.ReadU32(world.Header + 4), hz = (int)m.ReadU32(world.Header + 8);
        var repairs = new List<NativeWideRepair>();
        Span<NativeWideClipVertex> t = stackalloc NativeWideClipVertex[3];
        for (int pi = 0; pi < world.PolyCount; pi++)
        {
            uint poly = world.Polygons + (uint)pi * 8;
            NativeWidePolygonVertices(FastU32(m, poly), FastU32(m, poly + 4), out int a, out int b, out int c);
            t[0] = ReadNativeWideLocal(m, world, a);
            t[1] = ReadNativeWideLocal(m, world, b);
            t[2] = ReadNativeWideLocal(m, world, c);
            if (Array.IndexOf(walls.Complete, pi) >= 0 && TryNativeWideMaterial(m, world, pi, 0, out _, out _,
                out short cu0, out short cv0, out short cu1, out short cv1, out short cu2, out short cv2))
            {
                // A rectangular cell split along its diagonal: the right-angle corner
                // shares Y with one vertex and Z with the other. The missing half
                // uses the opposite corner, in position and texture.
                t[0] = t[0] with { U = cu0, V = cv0 }; t[1] = t[1] with { U = cu1, V = cv1 }; t[2] = t[2] with { U = cu2, V = cv2 };
                for (int r = 0; r < 3; r++)
                {
                    var right = t[r]; var p = t[(r + 1) % 3]; var q = t[(r + 2) % 3];
                    if (!((right.Y == p.Y && right.Z == q.Z) || (right.Y == q.Y && right.Z == p.Z))) continue;
                    repairs.Add(new(pi, p, q, p with
                    {
                        Y = p.Y + q.Y - right.Y, Z = p.Z + q.Z - right.Z, U = p.U + q.U - right.U, V = p.V + q.V - right.V,
                        R = (p.R + q.R) / 2, G = (p.G + q.G) / 2, B = (p.B + q.B) / 2,
                    }));
                    break;
                }
            }
            foreach (var block in walls.Blocks)
            {
                bool inside = true;
                foreach (var v in t)
                    inside &= v.X + hx == block.X && v.Y + hy >= block.YLow && v.Y + hy <= block.YHigh
                        && v.Z + hz >= block.ZFrom && v.Z + hz <= block.ZTo;
                if (!inside || !TryNativeWideMaterial(m, world, pi, 0, out _, out _,
                    out short u0, out short v0, out short u1, out short v1, out short u2, out short v2)) continue;
                for (float dz = block.Shift; block.ZFrom + dz < block.Until; dz += block.ZTo - block.ZFrom)
                    repairs.Add(new(pi, t[0] with { Y = t[0].Y + block.DY, Z = t[0].Z + dz, U = u0, V = v0 },
                        t[1] with { Y = t[1].Y + block.DY, Z = t[1].Z + dz, U = u1, V = v1 },
                        t[2] with { Y = t[2].Y + block.DY, Z = t[2].Z + dz, U = u2, V = v2 }));
            }
        }
        float mirror = 2 * (walls.MirrorZ - hz);
        foreach (int pi in walls.Mirror)
        {
            uint poly = world.Polygons + (uint)pi * 8;
            NativeWidePolygonVertices(FastU32(m, poly), FastU32(m, poly + 4), out int a, out int b, out int c);
            if (!TryNativeWideMaterial(m, world, pi, 0, out _, out _,
                out short u0, out short v0, out short u1, out short v1, out short u2, out short v2)) continue;
            NativeWideClipVertex Mirror(int index, short u, short v)
            {
                var p = ReadNativeWideLocal(m, world, index);
                return p with { Z = mirror - p.Z, U = u, V = v };
            }
            repairs.Add(new(pi, Mirror(a, u0, v0), Mirror(b, u1, v1), Mirror(c, u2, v2)));
        }
        return repairs;
    }

    // The surviving right-triangle half of a rectangular cell, and the Y offsets
    // at which the completed cell is copied.
    readonly record struct NativeWideCell(int Polygon, int[] Lifts);

    static List<NativeWideRepair> NativeWideCellRepairs(IMemory m, NativeWideWorld world, NativeWideCell[] cells)
    {
        var repairs = new List<NativeWideRepair>();
        foreach (var cell in cells)
        {
            uint poly = world.Polygons + (uint)cell.Polygon * 8;
            NativeWidePolygonVertices(FastU32(m, poly), FastU32(m, poly + 4), out int a, out int b, out int c);
            if (!TryNativeWideMaterial(m, world, cell.Polygon, 0, out _, out _,
                out short u0, out short v0, out short u1, out short v1, out short u2, out short v2)) continue;
            NativeWideClipVertex[] t = [ReadNativeWideLocal(m, world, a) with { U = u0, V = v0 },
                ReadNativeWideLocal(m, world, b) with { U = u1, V = v1 }, ReadNativeWideLocal(m, world, c) with { U = u2, V = v2 }];
            for (int r = 0; r < 3; r++)
            {
                var right = t[r]; var p = t[(r + 1) % 3]; var q = t[(r + 2) % 3];
                if (Vector3.Dot(Position(p) - Position(right), Position(q) - Position(right)) != 0) continue;
                // The opposite corner continues both edges in position, texture
                // and colour, so a fade to black stays black at its far edge.
                var corner = new NativeWideClipVertex(p.X + q.X - right.X, p.Y + q.Y - right.Y, p.Z + q.Z - right.Z,
                    Math.Clamp(p.R + q.R - right.R, 0, 255), Math.Clamp(p.G + q.G - right.G, 0, 255),
                    Math.Clamp(p.B + q.B - right.B, 0, 255), p.U + q.U - right.U, p.V + q.V - right.V);
                repairs.Add(new(cell.Polygon, p, q, corner));
                foreach (int lift in cell.Lifts)
                {
                    repairs.Add(new(cell.Polygon, right with { Y = right.Y + lift }, p with { Y = p.Y + lift },
                        q with { Y = q.Y + lift }));
                    repairs.Add(new(cell.Polygon, p with { Y = p.Y + lift }, q with { Y = q.Y + lift },
                        corner with { Y = corner.Y + lift }));
                }
                break;
            }
        }
        return repairs;
    }

    // Polygons with every vertex inside an absolute box, copied by Offset with U
    // moved by DU. MirrorX / MirrorY, when set, first reflect them about that
    // absolute X / Y. Material, when set, supplies the texture page and CLUT
    // instead of each source polygon. Opaque leaves semi-transparent polygons out.
    readonly record struct NativeWideCopy(Vector3 Min, Vector3 Max, Vector3 Offset, int DU = 0,
        int Material = -1, bool Opaque = false, float MirrorX = float.NaN, float MirrorY = float.NaN);
    // Copies, and side walls (absolute X planes) whose open near edges continue toward the camera.
    sealed record NativeWideCopies(NativeWideCopy[] Copies, float[] NearWalls);

    static List<NativeWideRepair> NativeWideCopyRepairs(IMemory m, NativeWideWorld world, NativeWideCopies scenery)
    {
        var copies = scenery.Copies;
        var origin = new Vector3((int)m.ReadU32(world.Header), (int)m.ReadU32(world.Header + 4), (int)m.ReadU32(world.Header + 8));
        var repairs = new List<NativeWideRepair>();
        Span<NativeWideClipVertex> t = stackalloc NativeWideClipVertex[3];
        for (int pi = 0; pi < world.PolyCount; pi++)
        {
            uint poly = world.Polygons + (uint)pi * 8;
            NativeWidePolygonVertices(FastU32(m, poly), FastU32(m, poly + 4), out int a, out int b, out int c);
            t[0] = ReadNativeWideLocal(m, world, a);
            t[1] = ReadNativeWideLocal(m, world, b);
            t[2] = ReadNativeWideLocal(m, world, c);
            foreach (var copy in copies)
            {
                bool inside = true;
                foreach (var v in t)
                {
                    var p = Position(v) + origin;
                    inside &= p == Vector3.Clamp(p, copy.Min, copy.Max);
                }
                if (!inside || !TryNativeWideMaterial(m, world, pi, 0, out var flags, out _,
                    out short u0, out short v0, out short u1, out short v1, out short u2, out short v2)
                    || (copy.Opaque && flags.SemiTrans)) continue;
                NativeWideClipVertex Move(NativeWideClipVertex v, short u, short texV) => v with
                {
                    X = (float.IsNaN(copy.MirrorX) ? v.X : 2 * (copy.MirrorX - origin.X) - v.X) + copy.Offset.X,
                    Y = (float.IsNaN(copy.MirrorY) ? v.Y : 2 * (copy.MirrorY - origin.Y) - v.Y) + copy.Offset.Y,
                    Z = v.Z + copy.Offset.Z, U = u + copy.DU, V = texV,
                };
                repairs.Add(new(copy.Material >= 0 ? copy.Material : pi,
                    Move(t[0], u0, v0), Move(t[1], u1, v1), Move(t[2], u2, v2)));
            }
        }
        if (scenery.NearWalls.Length > 0)
            NativeWideNearWallRepairs(m, world, origin, scenery.NearWalls, repairs);
        return repairs;
    }

    // Each open edge of a wall face whose surface ends toward the camera (+Z)
    // continues 1200 units further, as mirrored tiles of its own texture. Rows
    // of a wall end at different depths but never overlap, so neither do their strips.
    static void NativeWideNearWallRepairs(IMemory m, NativeWideWorld world, Vector3 origin, float[] planes,
        List<NativeWideRepair> repairs)
    {
        var faces = new List<(int Polygon, NativeWideClipVertex[] T)>();
        var edges = new Dictionary<NativeWideEdge, int>();
        for (int pi = 0; pi < world.PolyCount; pi++)
        {
            uint poly = world.Polygons + (uint)pi * 8;
            NativeWidePolygonVertices(FastU32(m, poly), FastU32(m, poly + 4), out int a, out int b, out int c);
            NativeWideClipVertex[] t = [ReadNativeWideLocal(m, world, a), ReadNativeWideLocal(m, world, b),
                ReadNativeWideLocal(m, world, c)];
            if (t[0].X != t[1].X || t[1].X != t[2].X || Array.IndexOf(planes, t[0].X + origin.X) < 0) continue;
            faces.Add((pi, t));
            for (int ei = 0; ei < 3; ei++)
            {
                var edge = NativeWideEdgeKey(t[ei], t[(ei + 1) % 3]);
                edges[edge] = edges.GetValueOrDefault(edge) + 1;
            }
        }
        foreach (var (pi, t) in faces)
        {
            if (!TryNativeWideMaterial(m, world, pi, 0, out _, out _,
                out short u0, out short v0, out short u1, out short v1, out short u2, out short v2)) continue;
            Vector2[] uv = [new(u0, v0), new(u1, v1), new(u2, v2)];
            for (int ei = 0; ei < 3; ei++)
            {
                var a = t[ei]; var b = t[(ei + 1) % 3]; var c = t[(ei + 2) % 3];
                if (edges[NativeWideEdgeKey(a, b)] != 1 || !NativeWideWallNearEdge(a, b, c)) continue;
                AddNativeWideSceneryStrip(repairs, pi, a, b, c, uv[ei], uv[(ei + 1) % 3], uv[(ei + 2) % 3],
                    Vector3.UnitZ, distance: 1200);
            }
        }
    }

    static NativeWideEdge NativeWideEdgeKey(NativeWideClipVertex a, NativeWideClipVertex b)
    {
        Vector3 pa = Position(a), pb = Position(b);
        bool swap = pa.X > pb.X || (pa.X == pb.X && (pa.Y > pb.Y || (pa.Y == pb.Y && pa.Z > pb.Z)));
        return swap ? new(pb, pa) : new(pa, pb);
    }

    // Backdrop skies built as a camera-centred arc that ends inside the 16:9 view.
    static bool NativeWideSkyArc(IMemory m, NativeWideWorld world, uint level) =>
        m.ReadU32(world.Header + 0x1C) == 1 && (level, world.PolyCount, world.VertexCount) is
            // Road to Nowhere / The High Road bridges: +/-41°.
            (20 or 22, 12, 14)
            // The Great Hall: the hall's sky (+/-45°) and the ending's (-117° to +45°).
            or (44, 38, 30) or (44, 81, 57);

    static IReadOnlyList<NativeWideRepair> NativeWideSkyArcRepairs(IMemory m, NativeWideWorld world, uint level)
    {
        var key = new NativeWideSceneryKey(level, m.ReadU32(world.Header), m.ReadU32(world.Header + 4),
            m.ReadU32(world.Header + 8), world.PolyCount, world.VertexCount);
        if (_nativeWideSkyArcs.TryGetValue(key, out var cached)) return cached;
        var vertices = Enumerable.Range(0, world.VertexCount).Select(i => ReadNativeWideLocal(m, world, i)).ToArray();
        // Angle around the camera, 0 straight ahead (-Z).
        var angles = vertices.Select(v => MathF.Atan2((float)v.X, -(float)v.Z)).ToArray();
        var repairs = new List<NativeWideRepair>();
        // Continue the arc at both end columns, keeping its radius and texture
        // density. Reflection in the end's radial plane mirrors the sky beyond it.
        // The Great Hall's end columns are only nearly radial (within 0.1°), so
        // the mirror reuses the original seam vertices to leave no crack.
        foreach (int end in new[] { Array.IndexOf(angles, angles.Min()), Array.IndexOf(angles, angles.Max()) })
        {
            var radial = Vector2.Normalize(new Vector2((float)vertices[end].X, (float)vertices[end].Z));
            NativeWideClipVertex Reflect(int index, short u, short texV)
            {
                var v = vertices[index];
                if (Math.Abs(angles[index] - angles[end]) < MathF.PI / 180) return v with { U = u, V = texV };
                var p = new Vector2((float)v.X, (float)v.Z);
                var reflected = 2 * Vector2.Dot(p, radial) * radial - p;
                return v with { X = reflected.X, Z = reflected.Y, U = u, V = texV };
            }
            for (int pi = 0; pi < world.PolyCount; pi++)
            {
                uint poly = world.Polygons + (uint)pi * 8;
                NativeWidePolygonVertices(m.ReadU32(poly), m.ReadU32(poly + 4), out int a, out int b, out int c);
                if (!TryNativeWideMaterial(m, world, pi, 0, out _, out _,
                    out short u0, out short v0, out short u1, out short v1, out short u2, out short v2)) continue;
                repairs.Add(new(pi, Reflect(a, u0, v0), Reflect(b, u1, v1), Reflect(c, u2, v2)));
            }
        }
        _nativeWideSkyArcs[key] = repairs;
        return repairs;
    }

    static bool NativeWideSunsetTemple(IMemory m, NativeWideWorld world) =>
        m.ReadU32(Catalogs.Catalog.LevelIdAddr) == 35
        && ((int)m.ReadU32(world.Header), (int)m.ReadU32(world.Header + 4), world.PolyCount, world.VertexCount) is
            (110000, -82800, 3463, 3183) or (113200, -64800, 2627, 2040) or (91200, -60400, 2742, 2093)
            or (81600, -42400, 3144, 2845) or (103600, -41600, 2250, 2011) or (142400, -41600, 2143, 1968)
            or (158400, -38423, 2805, 2238) or (151235, -16761, 3259, 2860) or (175200, -16600, 2113, 2038);

    // The temple's generic wall continuation cannot tell a level boundary from a
    // wall that ends inside an open room. Draw it behind all real scenery so it
    // only fills pixels nothing else covers. Depth is 1 - 64/Z; the remap keeps
    // the additions' own order but places them beyond the loaded rooms.
    static void NativeWideBehindScenery(List<NativeWideTriangle> triangles, int start)
    {
        static HleVertex Far(HleVertex v)
        {
            v.Z = Math.Min(40000f + v.Z * 0.3f, 65000f);
            return v;
        }
        for (int i = start; i < triangles.Count; i++)
        {
            var t = triangles[i];
            triangles[i] = t with { A = Far(t.A), B = Far(t.B), C = Far(t.C) };
        }
    }

    static void AddNativeWidePlane(Dictionary<float, List<int>> planes, float plane, int polygon)
    {
        if (!planes.TryGetValue(plane, out var list)) planes[plane] = list = [];
        list.Add(polygon);
    }

    // Open edge of a face lying in an X plane whose surface ends toward the camera (+Z).
    static bool NativeWideWallNearEdge(NativeWideClipVertex a, NativeWideClipVertex b, NativeWideClipVertex c)
    {
        if (a.X != b.X || b.X != c.X) return false;
        Vector3 pa = Position(a), edge = Position(b) - pa, outward = pa - Position(c);
        outward -= edge * (Vector3.Dot(outward, edge) / Vector3.Dot(edge, edge));
        return outward.Z > 1;
    }

    // True when another face in the same plane lies beyond the edge (axis 0 = X,
    // 2 = Z) within the extension distance and overlaps its height.
    static bool NativeWidePlaneContinues(NativeWideClipVertex[] triangles, List<int> plane, int axis,
        float start, float sign, float yLow, float yHigh, float distance)
    {
        foreach (int pi in plane)
        {
            var t0 = triangles[pi * 3]; var t1 = triangles[pi * 3 + 1]; var t2 = triangles[pi * 3 + 2];
            if (Math.Min(yHigh, Math.Max(t0.Y, Math.Max(t1.Y, t2.Y)))
                - Math.Max(yLow, Math.Min(t0.Y, Math.Min(t1.Y, t2.Y))) <= 0) continue;
            float d0 = ((axis == 0 ? t0.X : t0.Z) - start) * sign;
            float d1 = ((axis == 0 ? t1.X : t1.Z) - start) * sign;
            float d2 = ((axis == 0 ? t2.X : t2.Z) - start) * sign;
            float near = Math.Min(d0, Math.Min(d1, d2));
            if (near >= 0 && Math.Max(d0, Math.Max(d1, d2)) > 0 && near < distance) return true;
        }
        return false;
    }

    static Vector3 Position(NativeWideClipVertex v) => new((float)v.X, (float)v.Y, (float)v.Z);

    static NativeWideClipVertex ReadNativeWideLocal(IMemory m, NativeWideWorld world, int index)
    {
        uint address = world.Vertices + (uint)index * 8;
        uint a = FastU32(m, address), b = FastU32(m, address + 4);
        return new(NativeWideSign13((int)((b >> 3) & 8191)) * 8,
            NativeWideSign13((int)((b >> 19) & 8191)) * 8,
            NativeWideSign13((int)((a >> 24) | (((b >> 1) & 3) << 8) | (((b >> 16) & 7) << 10))) * 8,
            (byte)a, (byte)(a >> 8), (byte)(a >> 16), 0, 0);
    }

    static NativeWideClipVertex NativeWideRepairToCamera(IMemory m, NativeWideClipVertex v, NativeWideWorld world, short[] matrix)
    {
        var camera = v with
        {
            X = MathF.Floor((matrix[0] * v.X + matrix[1] * v.Y + matrix[2] * v.Z) / 4096f) + world.X,
            Y = MathF.Floor((matrix[3] * v.X + matrix[4] * v.Y + matrix[5] * v.Z) / 4096f) + world.Y,
            Z = MathF.Floor((matrix[6] * v.X + matrix[7] * v.Y + matrix[8] * v.Z) / 4096f) + world.Z,
        };
        int r = (int)Math.Round(v.R), g = (int)Math.Round(v.G), b = (int)Math.Round(v.B);
        NativeWideShadeVertex(m, world, (int)v.X, (int)v.Y, (int)v.Z, camera.Z, false, ref r, ref g, ref b);
        return camera with { R = r, G = g, B = b };
    }

    static void AddNativeWideSceneryStrip(List<NativeWideRepair> output, int polygon,
        NativeWideClipVertex a, NativeWideClipVertex b, NativeWideClipVertex c, Vector2 ua, Vector2 ub, Vector2 uc,
        Vector3? direction = null, Vector3? endDirection = null, float distance = 1600)
    {
        Vector3 pa = Position(a), e = Position(b) - pa, f = Position(c) - pa;
        float ee = Vector3.Dot(e, e), ef = Vector3.Dot(e, f), ff = Vector3.Dot(f, f);
        float det = ee * ff - ef * ef;
        Vector2 ue = ub - ua, uf = uc - ua;
        float uvDet = ue.X * uf.Y - ue.Y * uf.X;
        if (ee < 1 || det < 1 || Math.Abs(uvDet) < 1) return;
        Vector3 outward = Vector3.Normalize(e * (ef / ee) - f) * distance;
        float pe = Vector3.Dot(outward, e), pf = Vector3.Dot(outward, f);
        Vector2 uvOut = ue * ((pe * ff - pf * ef) / det) + uf * ((pf * ee - pe * ef) / det);
        if (direction is Vector3 axis)
        {
            // Adjacent cut edges share their endpoint offsets, so their new
            // boundary vertices coincide despite different surface slopes.
            // Keep the original edge scale and texels per perpendicular unit.
            outward = axis * distance;
            float alongEdge = Vector3.Dot(outward, e) / ee;
            uvOut = ue * alongEdge + uvOut * ((outward - e * alongEdge).Length() / distance);
        }
        Vector3 outwardEnd = endDirection is Vector3 endAxis ? endAxis * distance : outward;
        float stripDet = ue.X * uvOut.Y - ue.Y * uvOut.X;
        if (Math.Abs(stripDet) < 0.001f) return;
        // Adjacent bank segments have different slopes. Overlap their ends
        // with depth bias at coplanar seams so their skirts cannot open cracks.
        float overlap = direction.HasValue ? 0 : 0.25f;
        Vector2 start = ua - ue * overlap, end = ub + ue * overlap;
        Vector2[] strip = [start, end, end + uvOut, start + uvOut];
        float uMin = Math.Min(ua.X, Math.Min(ub.X, uc.X)), uMax = Math.Max(ua.X, Math.Max(ub.X, uc.X));
        float vMin = Math.Min(ua.Y, Math.Min(ub.Y, uc.Y)), vMax = Math.Max(ua.Y, Math.Max(ub.Y, uc.Y));
        float tileW = uMax - uMin, tileH = vMax - vMin;
        if (tileW < 1 || tileH < 1) return;
        int x0 = (int)Math.Floor((strip.Min(v => v.X) - uMin) / tileW), x1 = (int)Math.Floor((strip.Max(v => v.X) - uMin) / tileW);
        int y0 = (int)Math.Floor((strip.Min(v => v.Y) - vMin) / tileH), y1 = (int)Math.Floor((strip.Max(v => v.Y) - vMin) / tileH);
        if ((x1 - x0 + 1) * (y1 - y0 + 1) > 256) return;

        for (int iy = y0; iy <= y1; iy++) for (int ix = x0; ix <= x1; ix++)
        {
            var clipped = strip.ToList();
            clipped = ClipNativeWideUv(clipped, 0, uMin + ix * tileW, true);
            clipped = ClipNativeWideUv(clipped, 0, uMin + (ix + 1) * tileW, false);
            clipped = ClipNativeWideUv(clipped, 1, vMin + iy * tileH, true);
            clipped = ClipNativeWideUv(clipped, 1, vMin + (iy + 1) * tileH, false);
            if (clipped.Count < 3) continue;
            NativeWideClipVertex Vertex(Vector2 uv)
            {
                Vector2 delta = uv - ua;
                float s = (delta.X * uvOut.Y - delta.Y * uvOut.X) / stripDet;
                float t = (ue.X * delta.Y - ue.Y * delta.X) / stripDet;
                Vector3 p = pa + e * s + (outward + (outwardEnd - outward) * s) * t;
                float along = Math.Clamp(s, 0, 1);
                float u = uv.X - (uMin + ix * tileW), v = uv.Y - (vMin + iy * tileH);
                if ((ix & 1) != 0) u = tileW - u;
                if ((iy & 1) != 0) v = tileH - v;
                return new(p.X, p.Y, p.Z, a.R + (b.R - a.R) * along, a.G + (b.G - a.G) * along,
                    a.B + (b.B - a.B) * along, uMin + u, vMin + v);
            }
            var first = Vertex(clipped[0]);
            for (int i = 1; i + 1 < clipped.Count; i++) output.Add(new(polygon, first, Vertex(clipped[i]), Vertex(clipped[i + 1])));
        }
    }

    static List<Vector2> ClipNativeWideUv(List<Vector2> input, int axis, float edge, bool greater)
    {
        if (input.Count == 0) return input;
        var result = new List<Vector2>();
        var prev = input[^1];
        float Coordinate(Vector2 p) => axis == 0 ? p.X : p.Y;
        foreach (var current in input)
        {
            float a = Coordinate(prev) - edge, b = Coordinate(current) - edge;
            bool insideA = greater ? a >= 0 : a <= 0, insideB = greater ? b >= 0 : b <= 0;
            if (insideA != insideB) result.Add(Vector2.Lerp(prev, current, a / (a - b)));
            if (insideB) result.Add(current);
            prev = current;
        }
        return result;
    }
}
