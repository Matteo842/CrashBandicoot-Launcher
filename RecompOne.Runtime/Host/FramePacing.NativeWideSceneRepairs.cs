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
    static readonly Dictionary<uint, List<NativeWideRepair>> _nativeWideGroundRepairs = [];
    static List<NativeWideRepair>? _nativeWideBeachSky;
    static readonly Dictionary<uint, List<NativeWideRepair>> _nativeWideBridgeSkies = [];

    static IReadOnlyList<NativeWideRepair> NativeWideSceneRepairs(IMemory m, NativeWideWorld world)
    {
        uint level = m.ReadU32(Catalogs.Catalog.LevelIdAddr);
        if (level is 20 or 22 && world.PolyCount == 12 && world.VertexCount == 14
            && m.ReadU32(world.Header + 0x1C) == 1)
            return NativeWideBridgeSkyRepairs(m, world);
        if (level is not (9 or 12 or 18 or 26 or 46 or 55)) return Array.Empty<NativeWideRepair>();
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
        bool ground = beach || gate || fortress || jungle || castle || slippery;
        bool sky = level == 9 && world.PolyCount == 21 && world.VertexCount == 19
            && m.ReadU32(world.Header + 0x1C) == 1;
        if (!ground && !sky) return Array.Empty<NativeWideRepair>();
        var cached = ground ? _nativeWideGroundRepairs.GetValueOrDefault(level) : _nativeWideBeachSky;
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
            if (beach && (material is not (593 or 595) || Math.Min(a.Z, b.Z) < 800 || Math.Max(a.Z, b.Z) > 4000
                || Math.Min(Math.Abs(a.X), Math.Abs(b.X)) < 2400)) continue;
            if (gate && (material is not (673 or 675) || Math.Max(a.Y, b.Y) > -4800)) continue;
            if (fortress && (material is not (645 or 647) || Math.Max(a.Y, b.Y) > -5880)) continue;
            if (jungle && (material is not (48 or 50 or 60 or 62 or 64 or 78)
                || Math.Min(a.Z, b.Z) < 1400 || Math.Max(a.Z, b.Z) > 3100)) continue;
            if (castle && (material is not (20 or 24 or 28) || a.X != -3656 || b.X != -3656)) continue;
            if (slippery && (a.X != 6400 || b.X != 6400 || a.Z != 0 || b.Z != 0
                || material is not (265 or 317 or 257 or 261))) continue;
            if (!TryNativeWideMaterial(m, world, owner.Polygon, 0, out _, out _,
                out short u0, out short v0, out short u1, out short v1, out short u2, out short v2)) continue;
            Vector2[] uv = [new(u0, v0), new(u1, v1), new(u2, v2)];
            AddNativeWideSceneryStrip(repairs, owner.Polygon, a, b, c,
                uv[owner.Edge], uv[(owner.Edge + 1) % 3], uv[(owner.Edge + 2) % 3]);
        }
        PaceLog($"native-wide level={level} {(ground ? "ground" : "sky")} repairs={repairs.Count}");
        if (ground) _nativeWideGroundRepairs[level] = repairs; else _nativeWideBeachSky = repairs;
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
        NativeWideClipVertex a, NativeWideClipVertex b, NativeWideClipVertex c, Vector2 ua, Vector2 ub, Vector2 uc)
    {
        Vector3 pa = Position(a), e = Position(b) - pa, f = Position(c) - pa;
        float ee = Vector3.Dot(e, e), ef = Vector3.Dot(e, f), ff = Vector3.Dot(f, f);
        float det = ee * ff - ef * ef;
        Vector2 ue = ub - ua, uf = uc - ua;
        float uvDet = ue.X * uf.Y - ue.Y * uf.X;
        if (ee < 1 || det < 1 || Math.Abs(uvDet) < 1) return;
        Vector3 outward = Vector3.Normalize(e * (ef / ee) - f) * 1600;
        float pe = Vector3.Dot(outward, e), pf = Vector3.Dot(outward, f);
        Vector2 uvOut = ue * ((pe * ff - pf * ef) / det) + uf * ((pf * ee - pe * ef) / det);
        // Adjacent bank segments have different slopes. Overlap their ends
        // behind the authored mesh so their outward skirts cannot open cracks.
        Vector2 start = ua - ue * 0.25f, end = ub + ue * 0.25f;
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
                float s = (delta.X * uf.Y - delta.Y * uf.X) / uvDet;
                float t = (ue.X * delta.Y - ue.Y * delta.X) / uvDet;
                Vector3 p = pa + e * s + f * t;
                float along = Math.Clamp(Vector3.Dot(p - pa, e) / ee, 0, 1);
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
