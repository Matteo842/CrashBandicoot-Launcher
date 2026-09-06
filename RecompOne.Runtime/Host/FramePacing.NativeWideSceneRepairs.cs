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
    readonly record struct NativeWideRepair(int Polygon, NativeWideClipVertex A,
        NativeWideClipVertex B, NativeWideClipVertex C);
    readonly record struct NativeWideEdge(Vector3 A, Vector3 B);
    readonly record struct NativeWideEdgeOwner(int Polygon, int Edge, int Count);
    readonly record struct NativeWideSceneryKey(uint Level, uint X, uint Y, uint Z, int Polygons, int Vertices);
    static readonly Dictionary<NativeWideSceneryKey, List<NativeWideRepair>> _nativeWideSceneryRepairs = [];
    static List<NativeWideRepair>? _nativeWideBeachSky;
    static readonly Dictionary<uint, List<NativeWideRepair>> _nativeWideBridgeSkies = [];

    static IReadOnlyList<NativeWideRepair> NativeWideSceneRepairs(IMemory m, NativeWideWorld world)
    {
        uint level = m.ReadU32(Catalogs.Catalog.LevelIdAddr);
        if (level is 20 or 22 && world.PolyCount == 12 && world.VertexCount == 14
            && m.ReadU32(world.Header + 0x1C) == 1)
            return NativeWideBridgeSkyRepairs(m, world);
        if (level is not (9 or 12 or 15 or 17 or 18 or 24 or 26 or 46 or 55)) return Array.Empty<NativeWideRepair>();
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
        bool scenery = beach || gate || fortress || jungle || castle || slippery || upstream || creek || creekNext || hog;
        bool sky = level == 9 && world.PolyCount == 21 && world.VertexCount == 19
            && m.ReadU32(world.Header + 0x1C) == 1;
        if (!scenery && !sky) return Array.Empty<NativeWideRepair>();
        // Several loaded meshes can need different additions in the same level.
        // Identify the asset, not its transient RAM address or the level alone.
        var key = new NativeWideSceneryKey(level, m.ReadU32(world.Header), m.ReadU32(world.Header + 4),
            m.ReadU32(world.Header + 8), world.PolyCount, world.VertexCount);
        var cached = scenery ? _nativeWideSceneryRepairs.GetValueOrDefault(key) : _nativeWideBeachSky;
        if (cached != null) return cached;

        var edges = new Dictionary<NativeWideEdge, NativeWideEdgeOwner>();
        var triangles = new NativeWideClipVertex[world.PolyCount][];
        for (int pi = 0; pi < world.PolyCount; pi++)
        {
            uint poly = world.Polygons + (uint)pi * 8;
            NativeWidePolygonVertices(m.ReadU32(poly), m.ReadU32(poly + 4), out int a, out int b, out int c);
            var vertices = new[] { ReadNativeWideLocal(m, world, a), ReadNativeWideLocal(m, world, b), ReadNativeWideLocal(m, world, c) };
            triangles[pi] = vertices;
            for (int ei = 0; ei < 3; ei++)
            {
                Vector3 pa = Position(vertices[ei]), pb = Position(vertices[(ei + 1) % 3]);
                bool swap = pa.X > pb.X || (pa.X == pb.X && (pa.Y > pb.Y || (pa.Y == pb.Y && pa.Z > pb.Z)));
                var edge = swap ? new NativeWideEdge(pb, pa) : new NativeWideEdge(pa, pb);
                edges[edge] = edges.TryGetValue(edge, out var owner)
                    ? owner with { Count = owner.Count + 1 } : new NativeWideEdgeOwner(pi, ei, 1);
            }
        }

        var repairs = new List<NativeWideRepair>();
        foreach (var owner in edges.Values)
        {
            if (owner.Count != 1) continue;
            var vertices = triangles[owner.Polygon];
            var a = vertices[owner.Edge]; var b = vertices[(owner.Edge + 1) % 3]; var c = vertices[(owner.Edge + 2) % 3];
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
            if (!TryNativeWideMaterial(m, world, owner.Polygon, 0, out _, out _,
                out short u0, out short v0, out short u1, out short v1, out short u2, out short v2)) continue;
            Vector2[] uv = [new(u0, v0), new(u1, v1), new(u2, v2)];
            AddNativeWideSceneryStrip(repairs, owner.Polygon, a, b, c,
                uv[owner.Edge], uv[(owner.Edge + 1) % 3], uv[(owner.Edge + 2) % 3], direction, endDirection, distance);
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

    static IReadOnlyList<NativeWideRepair> NativeWideBridgeSkyRepairs(IMemory m, NativeWideWorld world)
    {
        uint key = m.ReadU32(world.Header + 8);
        if (_nativeWideBridgeSkies.TryGetValue(key, out var cached)) return cached;
        var vertices = Enumerable.Range(0, world.VertexCount).Select(i => ReadNativeWideLocal(m, world, i)).ToArray();
        var repairs = new List<NativeWideRepair>();
        // The bridges' sky is a textured cylinder segment ending at +/-41°.
        // Continue its arc at both end columns, keeping its radius and texture
        // density. Reflection in the radial plane fixes every seam vertex.
        foreach (var end in new[] { vertices.MinBy(v => v.X), vertices.MaxBy(v => v.X) })
        {
            var radial = Vector2.Normalize(new Vector2((float)end.X, (float)end.Z));
            NativeWideClipVertex Reflect(NativeWideClipVertex v, short u, short texV)
            {
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
                repairs.Add(new(pi, Reflect(vertices[a], u0, v0), Reflect(vertices[b], u1, v1), Reflect(vertices[c], u2, v2)));
            }
        }
        _nativeWideBridgeSkies[key] = repairs;
        return repairs;
    }

    static Vector3 Position(NativeWideClipVertex v) => new((float)v.X, (float)v.Y, (float)v.Z);

    static NativeWideClipVertex ReadNativeWideLocal(IMemory m, NativeWideWorld world, int index)
    {
        uint address = world.Vertices + (uint)index * 8;
        uint a = m.ReadU32(address), b = m.ReadU32(address + 4);
        return new(NativeWideSign13((int)((b >> 3) & 8191)) * 8,
            NativeWideSign13((int)((b >> 19) & 8191)) * 8,
            NativeWideSign13((int)((a >> 24) | (((b >> 1) & 3) << 8) | (((b >> 16) & 7) << 10))) * 8,
            (byte)a, (byte)(a >> 8), (byte)(a >> 16), 0, 0);
    }

    static NativeWideClipVertex NativeWideRepairToCamera(IMemory m, NativeWideClipVertex v, NativeWideWorld world, short[] matrix)
    {
        var camera = v with
        {
            X = Math.Floor((matrix[0] * v.X + matrix[1] * v.Y + matrix[2] * v.Z) / 4096) + world.X,
            Y = Math.Floor((matrix[3] * v.X + matrix[4] * v.Y + matrix[5] * v.Z) / 4096) + world.Y,
            Z = Math.Floor((matrix[6] * v.X + matrix[7] * v.Y + matrix[8] * v.Z) / 4096) + world.Z,
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
