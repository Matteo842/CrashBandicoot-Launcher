using System.Numerics;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    // The Lab (level 41) is one long corridor walked towards -Z with a fixed
    // camera heading, and its scenery was culled per triangle to the 4:3 view
    // all along it: the stone towers beside the walkway lost their outer
    // sides, the back walls of each room their outer columns, the side walls
    // their ends towards the camera. Towers are rebuilt in place at their true
    // depth; wall continuations are drawn behind all real scenery (renderer),
    // so they only show where the 16:9 view saw nothing.
    static List<NativeWideRepair> NativeWideLabRepairs(IMemory m, NativeWideWorld world)
    {
        if (m.ReadU32(world.Header + 0x1C) != 0) return NativeWideLabSky(m, world);
        var origin = new Vector3((int)m.ReadU32(world.Header), (int)m.ReadU32(world.Header + 4),
            (int)m.ReadU32(world.Header + 8));
        var repairs = new List<NativeWideRepair>();
        var faces = NativeWideFacesOf(m, world);
        if (NativeWideLabTowers(world.PolyCount, world.VertexCount, (int)origin.X, (int)origin.Z) is { } towers)
            NativeWideTowerRepairs(faces, origin, towers, repairs);
        NativeWideRowRepairs(faces, NativeWideLabRows, repairs);
        NativeWidePeriodicRepairs(faces, 0, 2, 2400, 2400, 4800, repairs);
        return repairs;
    }

    // The Lab's sky is a small camera-centred patch (-17° to -52°) seen only
    // through the windows on the left; the 16:9 view looks through them past
    // its end. Behind it, a camera-centred band of one of its tiles (polygon
    // 9's, mirrored cell to cell so it tiles without seams) covers the left
    // side from -110° to -5°, at window height. It is a fill like the walls' continuations, but
    // farther than all of them (radius 60000), so it is drawn first and shows
    // only where neither the walls, their continuations nor the sky patch do.
    static List<NativeWideRepair> NativeWideLabSky(IMemory m, NativeWideWorld world)
    {
        var band = new List<NativeWideRepair>();
        const int Template = 9;
        if (world.PolyCount != 12 || !TryNativeWideMaterial(m, world, Template, 0, out _, out _,
            out short u0, out short v0, out short u1, out short v1, out short u2, out short v2)) return band;
        float uLow = Math.Min(u0, Math.Min(u1, u2)), uHigh = Math.Max(u0, Math.Max(u1, u2));
        float vLow = Math.Min(v0, Math.Min(v1, v2)), vHigh = Math.Max(v0, Math.Max(v1, v2));
        const float Radius = 60000, Step = 15 * MathF.PI / 180;
        float height = Radius * Step;
        NativeWideClipVertex Corner(float angle, float y, float u, float v) =>
            new(Radius * MathF.Sin(angle), y, -Radius * MathF.Cos(angle), 127, 127, 127, u, v);
        for (int c = 0; c < 7; c++)
        {
            float a0 = (-110 + c * 15) * MathF.PI / 180, a1 = a0 + Step;
            float ua = (c & 1) == 0 ? uLow : uHigh, ub = (c & 1) == 0 ? uHigh : uLow;
            // From 22° below the camera (the lowest window bottom; the pits
            // below stay dark) to 33° above it.
            for (int r = 0; r < 4; r++)
            {
                float y0 = Radius * MathF.Tan(-22 * MathF.PI / 180) + r * height, y1 = y0 + height;
                float va = (r & 1) == 0 ? vHigh : vLow, vb = (r & 1) == 0 ? vLow : vHigh;
                var p00 = Corner(a0, y0, ua, va); var p10 = Corner(a1, y0, ub, va);
                var p01 = Corner(a0, y1, ua, vb); var p11 = Corner(a1, y1, ub, vb);
                band.Add(new(Template, p00, p10, p11));
                band.Add(new(Template, p00, p11, p01));
            }
        }
        return band;
    }

    // A mesh's triangles in local coordinates, with texture coordinates when
    // the material resolves (Valid), read once for all passes.
    readonly record struct NativeWideFace(NativeWideClipVertex[] T, bool Valid, bool SemiTrans);

    static NativeWideFace[] NativeWideFacesOf(IMemory m, NativeWideWorld world)
    {
        var faces = new NativeWideFace[world.PolyCount];
        for (int pi = 0; pi < world.PolyCount; pi++)
        {
            uint poly = world.Polygons + (uint)pi * 8;
            NativeWidePolygonVertices(FastU32(m, poly), FastU32(m, poly + 4), out int a, out int b, out int c);
            NativeWideClipVertex[] t = [ReadNativeWideLocal(m, world, a), ReadNativeWideLocal(m, world, b),
                ReadNativeWideLocal(m, world, c)];
            bool valid = TryNativeWideMaterial(m, world, pi, 0, out var flags, out _,
                out short u0, out short v0, out short u1, out short v1, out short u2, out short v2);
            if (valid)
            {
                t[0] = t[0] with { U = u0, V = v0 };
                t[1] = t[1] with { U = u1, V = v1 };
                t[2] = t[2] with { U = u2, V = v2 };
            }
            faces[pi] = new(t, valid, flags.SemiTrans);
        }
        return faces;
    }

    // Each tower is round about a vertical axis (absolute X, Z) and built from
    // parts sharing it (shaft, ledges, crown, spikes), found from one polygon
    // of each. Towers stand in mirrored twins across the walkway: a twin lies
    // mirrored about absolute X = TwinAxis and moved by TwinShift along Z. Of
    // the left twin in the a8/a9 room and the one at the end only spikes are
    // left. `wide.py axes 41` prints these lines.
    static NativeWideTower[]? NativeWideLabTowers(int polys, int vertices, int x, int z) =>
        (polys, vertices, x, z) switch
        {
            (3126, 3043, 8280, 88930) =>
            [
                new(10796, 77196, [2368, 2491, 2529, 2522, 2560, 2443, 2367, 2381, 2490], 1, 7596, -800),
                // The neighbouring mesh (c__FW) holds this tower's front base and crown spikes.
                new(4396, 76396, [0, 49, 42, 39, 46, 62, 38], 0, 7596, 800,
                    new(new(4120, 2590, 74815), new(5660, 6410, 75625))),
            ],
            (3441, 3459, 8024, 39786) =>
            [
                new(5196, 37196, [210, 351, 402, 354, 404, 251, 436], 1, 7796, 400),
                new(10396, 37596, [2999, 3101, 3076, 3095, 3073], 0, 7796, -400),
            ],
            (3356, 3257, 7368, 15021) =>
            [
                new(11196, 18797, [2688, 2912, 3000, 2979, 2973, 2998, 3056], 1, 7996, 0),
                new(4796, 18797, [142, 306, 348, 299], 0, 7996, 0),
            ],
            (1455, 1520, 7350, -24109) =>
            [
                new(10797, -18802, [1028, 1204, 1271, 1269, 1319, 1242], 1, 8598, -1200),
                new(6397, -20002, [332, 512, 584, 553, 556, 637], 0, 8598, 1200),
                new(8397, -15602, [639, 813, 660, 901, 860, 866, 899, 942]),
                new(4797, -16803, [50, 146, 181], 5, 8598, 1200),
                new(5997, -12803, [207, 286, 311, 280, 313, 223, 274], 6, 8198, 400),
                new(12394, -15603, [1321, 1390, 1418, 1415, 1389], 3, 8598, -1200),
                new(10399, -12403, [988, 997], 4, 8198, -400),
            ],
            _ => null,
        };

    // Back walls (Z planes) continue both ways along X; side walls (X planes)
    // only towards the camera (+Z), since their far ends meet the back walls.
    // Low walls such as the walkway's front faces, and short ones such as the
    // door alcoves' sides and the plates on the walls, are not continued.
    static readonly NativeWideRows[] NativeWideLabRows =
    [
        new(2, 0, Low: true, High: true, Extend: 4800, MinBlock: 1600, MinHeight: 2400, MinLength: 2400),
        new(0, 2, Low: false, High: true, Extend: 4800, MinBlock: 800, MinHeight: 2400, MinLength: 2400),
    ];

    readonly record struct NativeWideTower(float X, float Z, int[] Seeds, int Twin = -1, float TwinAxis = 0,
        float TwinShift = 0, NativeWideBox? Skip = null);

    // A tower is completed first from its twin, mirrored onto it, then from
    // itself and those copies turned half a turn about its axis (texture keeps
    // its direction). A triangle is only added where it covers no original
    // face or earlier addition nearly coplanar with it: those would fight in
    // the depth test. The towers are not quite symmetric (vertices on an
    // 8-unit grid), so copies of what survived are left out.
    const float NativeWideTowerRadius = 1750;

    static void NativeWideTowerRepairs(NativeWideFace[] faces, Vector3 origin, NativeWideTower[] towers,
        List<NativeWideRepair> repairs)
    {
        var all = faces.Select(f => f.T).ToArray();
        var byVertex = new Dictionary<Vector3, List<int>>();
        for (int pi = 0; pi < all.Length; pi++)
            foreach (var v in all[pi])
            {
                if (!byVertex.TryGetValue(Position(v), out var list)) byVertex[Position(v)] = list = [];
                list.Add(pi);
            }
        // Each tower's original triangles.
        var parts = new List<(int Polygon, NativeWideClipVertex[] T)>[towers.Length];
        for (int ti = 0; ti < towers.Length; ti++)
        {
            var found = new HashSet<int>();
            foreach (int seed in towers[ti].Seeds) found.UnionWith(NativeWideConnected(all, byVertex, seed));
            parts[ti] = [.. found.Order().Where(pi => faces[pi].Valid).Select(pi => (pi, all[pi]))];
        }
        for (int ti = 0; ti < towers.Length; ti++)
        {
            var tower = towers[ti];
            float x = tower.X - origin.X, z = tower.Z - origin.Z;
            var lo = new Vector3(x - NativeWideTowerRadius, 1700 - origin.Y, z - NativeWideTowerRadius);
            var hi = new Vector3(x + NativeWideTowerRadius, 7200 - origin.Y, z + NativeWideTowerRadius);
            // Original faces near the tower and the triangles added to it.
            var blockers = new NativeWideTriangleGrid();
            for (int pi = 0; pi < all.Length; pi++)
                if (all[pi].Any(v => Position(v) == Vector3.Clamp(Position(v), lo, hi)))
                    blockers.Add(all[pi]);
            bool Add(int polygon, NativeWideClipVertex[] t)
            {
                // Parts another mesh draws (absolute box) are not added again.
                if (tower.Skip is { } skip && t.All(v => Position(v) + origin == Vector3.Clamp(Position(v) + origin,
                    skip.Min, skip.Max))) return false;
                if (blockers.Overlaps(t)) return false;
                blockers.Add(t);
                repairs.Add(new(polygon, t[0], t[1], t[2], Boundary: true));
                return true;
            }
            var added = new List<(int Polygon, NativeWideClipVertex[] T)>();
            if (tower.Twin >= 0 && tower.Twin < towers.Length)
            {
                float axis = tower.TwinAxis - origin.X;
                foreach (var (pi, t) in parts[tower.Twin])
                {
                    NativeWideClipVertex[] r = [.. t.Select(v => v with { X = 2 * axis - v.X, Z = v.Z - tower.TwinShift })];
                    if (Add(pi, r)) added.Add((pi, r));
                }
            }
            foreach (var (pi, t) in parts[ti].Concat(added).ToList())
                Add(pi, [.. t.Select(v => v with { X = 2 * x - v.X, Z = 2 * z - v.Z })]);
        }
    }

    static (Vector3 Min, Vector3 Max, NativeWideClipVertex[] T) NativeWideBounded(NativeWideClipVertex[] t) =>
        (Vector3.Min(Position(t[0]), Vector3.Min(Position(t[1]), Position(t[2]))),
            Vector3.Max(Position(t[0]), Vector3.Max(Position(t[1]), Position(t[2]))), t);

    // Triangles a and b overlap in their interiors while a lies within 24 units
    // of b's plane: drawn together they would fight in the depth test.
    static bool NativeWideCoplanarOverlap(NativeWideClipVertex[] a, NativeWideClipVertex[] b)
    {
        Vector3 b0 = Position(b[0]);
        var normal = Vector3.Cross(Position(b[1]) - b0, Position(b[2]) - b0);
        float length = normal.Length();
        if (length < 1) return false;
        normal /= length;
        foreach (var v in a)
            if (Math.Abs(Vector3.Dot(Position(v) - b0, normal)) > 24) return false;
        // Project onto the plane's two longest axes and look for a separating edge.
        var n = Vector3.Abs(normal);
        int drop = n.X >= n.Y && n.X >= n.Z ? 0 : n.Y >= n.Z ? 1 : 2;
        Vector2 Flat(NativeWideClipVertex v) => drop == 0 ? new(v.Y, v.Z) : drop == 1 ? new(v.X, v.Z) : new(v.X, v.Y);
        Span<Vector2> pa = [Flat(a[0]), Flat(a[1]), Flat(a[2])], pb = [Flat(b[0]), Flat(b[1]), Flat(b[2])];
        for (int k = 0; k < 6; k++)
        {
            var tri = k < 3 ? pa : pb;
            Vector2 e = tri[(k + 1) % 3] - tri[k % 3], axis = new(-e.Y, e.X);
            float scale = axis.Length();
            if (scale < 1e-3f) continue;
            axis /= scale;
            float aMin = float.MaxValue, aMax = float.MinValue, bMin = float.MaxValue, bMax = float.MinValue;
            for (int i = 0; i < 3; i++)
            {
                float da = Vector2.Dot(pa[i], axis), db = Vector2.Dot(pb[i], axis);
                aMin = Math.Min(aMin, da); aMax = Math.Max(aMax, da);
                bMin = Math.Min(bMin, db); bMax = Math.Max(bMax, db);
            }
            // Triangles that only share an edge or touch are not overlapping.
            if (aMax <= bMin + 2 || bMax <= aMin + 2) return false;
        }
        return true;
    }

    // Polygons joined to seed through shared vertex positions.
    static List<int> NativeWideConnected(NativeWideClipVertex[][] polygons, Dictionary<Vector3, List<int>> byVertex,
        int seed)
    {
        var found = new List<int>();
        if ((uint)seed >= (uint)polygons.Length) return found;
        var seen = new HashSet<int> { seed };
        var queue = new Queue<int>();
        queue.Enqueue(seed);
        while (queue.Count > 0)
        {
            int pi = queue.Dequeue();
            found.Add(pi);
            foreach (var v in polygons[pi])
                foreach (int next in byVertex[Position(v)])
                    if (seen.Add(next)) queue.Enqueue(next);
        }
        return found;
    }

    // Planar surfaces cut along a staircase, as rows of cells: faces in planes
    // normal to axis Normal (0 X, 1 Y, 2 Z) that share their extent across the
    // row form a row along axis U. A half cell (one triangle of a rectangle)
    // is completed. Each listed end (Low = -U, High = +U) of every run of
    // adjacent whole cells then continues with copies of the run's outermost
    // cells, at least MinBlock long, moved by their length, until Extend past
    // the end or a face of the same plane is in the way. Only planes at least
    // MinHeight tall across their rows, and rows whose whole cells span
    // MinLength, take part. Copies never overlap faces or each other.
    readonly record struct NativeWideRows(int Normal, int U, bool Low, bool High, float Extend, float MinBlock,
        float MinHeight, float MinLength);

    static float NativeWideAxis(NativeWideClipVertex v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    static NativeWideClipVertex NativeWideMoved(NativeWideClipVertex v, int axis, float by) =>
        axis == 0 ? v with { X = v.X + by } : axis == 1 ? v with { Y = v.Y + by } : v with { Z = v.Z + by };

    static void NativeWideRowRepairs(NativeWideFace[] all, NativeWideRows[] specs, List<NativeWideRepair> repairs)
    {
        foreach (var spec in specs)
        {
            int axisV = 3 - spec.Normal - spec.U;
            var faces = new Dictionary<float, List<(float U0, float U1, float V0, float V1)>>();
            var rows = new Dictionary<(float Plane, float V0, float V1),
                SortedDictionary<(float U0, float U1), List<NativeWideRepair>>>();
            for (int pi = 0; pi < all.Length; pi++)
            {
                var t = all[pi].T;
                float plane = NativeWideAxis(t[0], spec.Normal);
                if (!all[pi].Valid || NativeWideAxis(t[1], spec.Normal) != plane
                    || NativeWideAxis(t[2], spec.Normal) != plane) continue;
                float lo = Math.Min(NativeWideAxis(t[0], spec.U), Math.Min(NativeWideAxis(t[1], spec.U), NativeWideAxis(t[2], spec.U)));
                float hi = Math.Max(NativeWideAxis(t[0], spec.U), Math.Max(NativeWideAxis(t[1], spec.U), NativeWideAxis(t[2], spec.U)));
                float low = Math.Min(NativeWideAxis(t[0], axisV), Math.Min(NativeWideAxis(t[1], axisV), NativeWideAxis(t[2], axisV)));
                float high = Math.Max(NativeWideAxis(t[0], axisV), Math.Max(NativeWideAxis(t[1], axisV), NativeWideAxis(t[2], axisV)));
                if (!faces.TryGetValue(plane, out var list)) faces[plane] = list = [];
                list.Add((lo, hi, low, high));
                // Decals and other semi-transparent faces block copies but are not copied.
                if (all[pi].SemiTrans) continue;
                if (!rows.TryGetValue((plane, low, high), out var cells)) rows[(plane, low, high)] = cells = [];
                if (!cells.TryGetValue((lo, hi), out var cell)) cells[(lo, hi)] = cell = [];
                cell.Add(new(pi, t[0], t[1], t[2]));
            }
            bool Free(float plane, float lo, float hi, float low, float high) => !faces[plane].Any(r =>
                r.U0 < hi - 2 && lo < r.U1 - 2 && r.V0 < high - 2 && low < r.V1 - 2);
            foreach (var ((plane, low, high), cells) in rows)
            {
                var span = faces[plane];
                if (span.Max(r => r.V1) - span.Min(r => r.V0) < spec.MinHeight) continue;
                // Whole cells; a half cell left by the cull gets its missing corner.
                var whole = new List<(float U0, float U1, List<NativeWideRepair> T)>();
                foreach (var ((lo, hi), tris) in cells)
                {
                    float area = (hi - lo) * (high - low), covered = 0;
                    foreach (var r in tris)
                        covered += Vector3.Cross(Position(r.B) - Position(r.A), Position(r.C) - Position(r.A)).Length() / 2;
                    if (covered >= area * 0.98f) { whole.Add((lo, hi, tris)); continue; }
                    var t = tris[0];
                    if (tris.Count != 1 || Math.Abs(covered - area / 2) > area * 0.02f
                        || !NativeWideOppositeCorner([t.A, t.B, t.C], out var p, out var q, out var corner)) continue;
                    repairs.Add(new(t.Polygon, p, q, corner));
                    whole.Add((lo, hi, [t, repairs[^1]]));
                }
                if (whole.Count == 0 || whole.Max(e => e.U1) - whole.Min(e => e.U0) < spec.MinLength) continue;
                // Runs of adjacent whole cells. Every free end of a run continues,
                // so the gaps between runs (wall hidden behind an arch or machine
                // in 4:3) close too. Openings that show real scenery or the sky
                // stay open: the copies are drawn behind both.
                whole.Sort((x, y) => x.U0.CompareTo(y.U0));
                var runs = new List<List<(float U0, float U1, List<NativeWideRepair> T)>>();
                foreach (var cell in whole)
                {
                    if (runs.Count > 0 && Math.Abs(runs[^1][^1].U1 - cell.U0) <= 2) runs[^1].Add(cell);
                    else runs.Add([cell]);
                }
                foreach (var run in runs)
                    foreach (int side in new[] { -1, 1 })
                    {
                        if (side < 0 ? !spec.Low : !spec.High) continue;
                        // The run's outermost cells, at least MinBlock long.
                        var block = new List<(float U0, float U1, List<NativeWideRepair> T)>();
                        float length = 0;
                        for (int i = 0; i < run.Count && length < spec.MinBlock; i++)
                        {
                            block.Add(run[side > 0 ? run.Count - 1 - i : i]);
                            length += block[^1].U1 - block[^1].U0;
                        }
                        if (length < spec.MinBlock) continue;
                        // Copies move by the block's length; cells go in from the one
                        // landing next to the end, until a face is in the way.
                        var order = side > 0 ? block.OrderBy(e => e.U0).ToList() : [.. block.OrderByDescending(e => e.U0)];
                        for (float reached = 0; reached < spec.Extend; reached += length)
                        {
                            float offset = side * (reached + length);
                            bool placed = true;
                            foreach (var cell in order)
                            {
                                float lo = cell.U0 + offset, hi = cell.U1 + offset;
                                if (!Free(plane, lo, hi, low, high)) { placed = false; break; }
                                foreach (var r in cell.T)
                                    repairs.Add(new(r.Polygon, NativeWideMoved(r.A, spec.U, offset),
                                        NativeWideMoved(r.B, spec.U, offset), NativeWideMoved(r.C, spec.U, offset)));
                                span.Add((lo, hi, low, high));
                            }
                            if (!placed) break;
                        }
                    }
            }
        }
    }

    // Walls whose faces are no grid of cells (the arched windows) but repeat
    // along U with a period: faces in planes normal to axis Normal at least
    // MinHeight tall and MinLength long. The period is the U distance most
    // vertex pairs at the same height agree on. The wall moved by up to three
    // whole periods fills the gaps the cull left inside it and continues up
    // to Extend past its +U end; only triangles over no face of the plane,
    // original or added, are kept.
    static void NativeWidePeriodicRepairs(NativeWideFace[] faces, int normal, int axisU, float minHeight,
        float minLength, float extend, List<NativeWideRepair> repairs)
    {
        int axisV = 3 - normal - axisU;
        var planes = new Dictionary<float, (List<(int Polygon, NativeWideClipVertex[] T)> Copy, List<NativeWideClipVertex[]> All)>();
        for (int pi = 0; pi < faces.Length; pi++)
        {
            var t = faces[pi].T;
            float plane = NativeWideAxis(t[0], normal);
            if (!faces[pi].Valid || NativeWideAxis(t[1], normal) != plane || NativeWideAxis(t[2], normal) != plane) continue;
            if (!planes.TryGetValue(plane, out var lists)) planes[plane] = lists = ([], []);
            lists.All.Add(t);
            if (!faces[pi].SemiTrans) lists.Copy.Add((pi, t));
        }
        foreach (var r in repairs)
        {
            float plane = NativeWideAxis(r.A, normal);
            if (NativeWideAxis(r.B, normal) == plane && NativeWideAxis(r.C, normal) == plane
                && planes.TryGetValue(plane, out var lists)) lists.All.Add([r.A, r.B, r.C]);
        }
        foreach (var (plane, (copy, all)) in planes)
        {
            if (copy.Count < 8) continue;
            var points = copy.SelectMany(e => e.T).Select(v => (U: NativeWideAxis(v, axisU), V: NativeWideAxis(v, axisV)))
                .Distinct().ToList();
            float uLow = points.Min(p => p.U), uHigh = points.Max(p => p.U);
            if (points.Max(p => p.V) - points.Min(p => p.V) < minHeight || uHigh - uLow < minLength) continue;
            var votes = new Dictionary<float, int>();
            foreach (var row in points.GroupBy(p => p.V))
            {
                var us = row.Select(p => p.U).Order().ToArray();
                for (int i = 0; i < us.Length; i++)
                    for (int j = i + 1; j < us.Length && us[j] - us[i] <= 4000; j++)
                        if (us[j] - us[i] >= 400) votes[us[j] - us[i]] = votes.GetValueOrDefault(us[j] - us[i]) + 1;
            }
            if (votes.Count == 0) continue;
            var (period, count) = votes.MaxBy(e => e.Value);
            if (count < points.Count * 0.15f) continue;
            var blockers = new NativeWideTriangleGrid();
            foreach (var t in all) blockers.Add(t);
            // Coarse map of the plane (100-unit cells whose centre a face
            // covers): a copy whose centre and edge midpoints all fall on it
            // lies on the wall already, without the exact test.
            var covered = new HashSet<(int, int)>();
            Vector2 Flat(NativeWideClipVertex v) => new(NativeWideAxis(v, axisU), NativeWideAxis(v, axisV));
            (int, int) CellOf(Vector2 p) => ((int)MathF.Floor(p.X / 100), (int)MathF.Floor(p.Y / 100));
            void Cover(NativeWideClipVertex[] t)
            {
                Vector2 a = Flat(t[0]), b = Flat(t[1]), c = Flat(t[2]);
                var (x0, y0) = CellOf(Vector2.Min(a, Vector2.Min(b, c)));
                var (x1, y1) = CellOf(Vector2.Max(a, Vector2.Max(b, c)));
                float Side(Vector2 p, Vector2 q, Vector2 r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                    {
                        var centre = new Vector2(x * 100 + 50, y * 100 + 50);
                        float d0 = Side(a, b, centre), d1 = Side(b, c, centre), d2 = Side(c, a, centre);
                        if ((d0 >= 0 && d1 >= 0 && d2 >= 0) || (d0 <= 0 && d1 <= 0 && d2 <= 0)) covered.Add((x, y));
                    }
            }
            foreach (var t in all) Cover(t);
            // Up to three periods: longer gaps are openings, not cull.
            int steps = Math.Min(3, (int)MathF.Ceiling(extend / period));
            for (int k = 1; k <= steps; k++)
                foreach (float shift in new[] { k * period, -k * period })
                    foreach (var (pi, t) in copy)
                    {
                        NativeWideClipVertex[] moved = [NativeWideMoved(t[0], axisU, shift), NativeWideMoved(t[1], axisU, shift),
                            NativeWideMoved(t[2], axisU, shift)];
                        if (moved.Any(v => NativeWideAxis(v, axisU) < uLow - 2 || NativeWideAxis(v, axisU) > uHigh + extend))
                            continue;
                        Vector2 a = Flat(moved[0]), b = Flat(moved[1]), c = Flat(moved[2]);
                        if (covered.Contains(CellOf((a + b + c) / 3)) && covered.Contains(CellOf((a + b) / 2))
                            && covered.Contains(CellOf((b + c) / 2)) && covered.Contains(CellOf((c + a) / 2))) continue;
                        if (blockers.Overlaps(moved)) continue;
                        blockers.Add(moved);
                        Cover(moved);
                        repairs.Add(new(pi, moved[0], moved[1], moved[2]));
                    }
        }
    }

    // Triangles bucketed by 400-unit cells of their bounds, for overlap tests
    // against many faces.
    sealed class NativeWideTriangleGrid
    {
        const float Cell = 400;
        readonly Dictionary<(int, int, int), List<(Vector3 Min, Vector3 Max, NativeWideClipVertex[] T)>> _cells = [];

        static (int, int, int) Key(Vector3 p) =>
            ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell), (int)MathF.Floor(p.Z / Cell));

        public void Add(NativeWideClipVertex[] t)
        {
            var e = NativeWideBounded(t);
            var (x0, y0, z0) = Key(e.Min);
            var (x1, y1, z1) = Key(e.Max);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    for (int z = z0; z <= z1; z++)
                    {
                        if (!_cells.TryGetValue((x, y, z), out var list)) _cells[(x, y, z)] = list = [];
                        list.Add(e);
                    }
        }

        // True when t overlaps a nearly coplanar triangle of the grid.
        public bool Overlaps(NativeWideClipVertex[] t)
        {
            var (min, max, _) = NativeWideBounded(t);
            var pad = new Vector3(24);
            var (x0, y0, z0) = Key(min - pad);
            var (x1, y1, z1) = Key(max + pad);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    for (int z = z0; z <= z1; z++)
                        if (_cells.TryGetValue((x, y, z), out var list))
                            foreach (var e in list)
                                if (min == Vector3.Min(min, e.Max + pad) && max == Vector3.Max(max, e.Min - pad)
                                    && NativeWideCoplanarOverlap(t, e.T)) return true;
            return false;
        }
    }
}
