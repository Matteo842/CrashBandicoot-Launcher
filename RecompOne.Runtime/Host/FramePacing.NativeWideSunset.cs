using System.Numerics;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    // Sunset Vista's temple (level 35): climbing shafts and the corridors
    // between them. Every mesh has one big back wall (local Z plane) with
    // ledges in front; the shafts' side walls (X planes) are only 1600 deep
    // and their ends towards the camera were culled to the 4:3 view, as were
    // the back walls beside them. Rows of whole stone cells are continued
    // with copies of their outermost cells (no stretched or mirrored texture),
    // drawn behind all real scenery (renderer), so they only fill what the
    // 16:9 view saw nothing of. They still cover the sky: inside the shafts
    // the sky only shows past the culled wall ends.
    static List<NativeWideRepair> NativeWideSunsetRepairs(IMemory m, NativeWideWorld world)
    {
        var repairs = new List<NativeWideRepair>();
        var faces = NativeWideFacesOf(m, world);
        int x = (int)m.ReadU32(world.Header);
        // The rooftops (j, l), the tops of the second and third shafts (i, n)
        // and the ending (o) open onto the sky, where their low fronts and
        // floors really end.
        bool roof = x is 81600 or 103600 or 142400 or 151235 or 175200;
        NativeWideRowRepairs(faces, roof ? NativeWideSunsetRoofRows : NativeWideSunsetRows, repairs);
        // The third shaft's mesh (m) meets the last rooftop (l), whose facade
        // ends at absolute X 151200 against the sky: its back wall copies stop there.
        if (x == 158400)
        {
            repairs.RemoveAll(r => Math.Min(r.A.X, Math.Min(r.B.X, r.C.X)) < 151200 - x);
            // Behind fills lose to any scenery farther away, and the outside (the
            // rooftops' forest, l) lies behind this shaft. Where the 16:9 view sees
            // past a culled end, the continuation keeps its true depth:
            // - the back wall (Z 0) west of the left wall below its lowest row
            //   (Y -38007), seen under the moss ledge from the shaft's bottom;
            // - the big ledge on the right wall (X 161200..162800, top Y -35607):
            //   its top, front and underside (the underside's culled half and
            //   east half copied from its west half), and the right wall's two
            //   row under it (Y -36807..-35959, culled at every depth), copied
            //   with their continuations from one 3200 period (Y) above;
            // - the merlons on the ledge at the shaft's bottom (top Y -40207,
            //   every 400 from X 161256): the third was halved, the fourth culled.
            float y = (int)m.ReadU32(world.Header + 4), z = (int)m.ReadU32(world.Header + 8);
            bool In(NativeWideRepair r, Vector3 lo, Vector3 hi) => new[] { r.A, r.B, r.C }.All(v =>
                Position(v) + new Vector3(x, y, z) == Vector3.Clamp(Position(v) + new Vector3(x, y, z), lo, hi));
            int solid = repairs.Count;
            NativeWideSunsetCopies(m, world, faces, NativeWideSunsetShaftCopies, repairs, snap: false, withRepairs: true);
            for (int i = 0; i < repairs.Count; i++)
                if (i >= solid || In(repairs[i], new(151184, -39616, -16), new(156400, -37998, 16))
                    || In(repairs[i], new(161184, -36016, -16), new(162816, -35598, 1616)) && !In(repairs[i], new(150000, -50000, -16), new(170000, 0, 16)))
                    repairs[i] = repairs[i] with { Boundary = true };
        }
        // The walkway outside and the temple's front end at absolute X 100400,
        // before the pools (the 4:3 view shows nothing beyond): nothing of
        // them continues west of it.
        if (x == 110000)
        {
            repairs.RemoveAll(r => Math.Max(r.A.X, Math.Max(r.B.X, r.C.X)) <= 100416 - x);
            NativeWideSunsetCopies(m, world, faces, NativeWideSunsetWalkwayCopies, repairs, snap: false);
            // Above the first shaft's entrance the lintel's upper row (X 110800,
            // Y -80800..-80400) ends at Z 1200, a cell short of the rows above
            // and below it: the corridor's back wall showed through the gap.
            // Its Z 400 cell (the pattern alternates) fills it, at true depth.
            int lintel = repairs.Count;
            NativeWideSunsetCopies(m, world, faces, NativeWideSunsetLintelCopies, repairs, snap: false);
            for (int i = lintel; i < repairs.Count; i++) repairs[i] = repairs[i] with { Boundary = true };
        }
        // At the third shaft's top (n) the right wall steps in from X 162795
        // to 161995 above Y -14801 (a 400 step at X 162395): too low to count
        // as walls, so the ending's forest showed past the step's culled near
        // end. The step's faces (sides and tops) continue towards the camera
        // like the walls, keeping their true depth.
        if (x == 151235)
        {
            var step = new List<NativeWideRepair>();
            NativeWideRowRepairs(faces, NativeWideSunsetStepRows, step);
            float y = (int)m.ReadU32(world.Header + 4);
            bool InStep(NativeWideClipVertex v) => v.X >= 161980 - x && v.X <= 162810 - x
                && v.Y >= -14810 - y && v.Y <= -11300 - y;
            foreach (var r in step)
                if (InStep(r.A) && InStep(r.B) && InStep(r.C)) repairs.Add(r with { Boundary = true });
        }
        // The shafts' outer walls (absolute X, side the outside lies on): the
        // first shaft's (meshes f and g), the second shaft's right wall and the
        // third shaft's (its upper mesh, n, has them 5 units to the left).
        // Rooms, rooftops and forest lie beyond them, so their continuations
        // keep their true depth and hide them. A half cell completed over a
        // skewed neighbour stays behind (in depth it would fight that face).
        // Ledges, floors and fronts continued along X stop at the right walls
        // (outside them lies only the exterior): through them they stuck out
        // wherever the wall's continuation has a gap (the first shaft's
        // alcove, the third shaft's big ledges). The back wall (Z 0) goes on
        // behind them; continuations above and below a wall (the exit at the
        // third shaft's top) stay, as does everything beyond the left walls,
        // where the entrances and the corridor between the shafts lie.
        (int X, int Side)[] outerWalls = x switch
        {
            110000 => [(111200, -1)],
            113200 => [(111200, -1), (117200, 1)],
            81600 => [(88800, 1)],
            158400 => [(156400, -1), (162800, 1)],
            151235 => [(156395, -1), (162795, 1)],
            _ => [],
        };
        float back = -(int)m.ReadU32(world.Header + 8);
        foreach (var (wall, side) in outerWalls)
        {
            float outerWall = wall - x;
            bool OnWall(NativeWideClipVertex v) => Math.Abs(v.X - outerWall) <= 16;
            var retail = new NativeWideTriangleGrid();
            float low = float.MaxValue, high = float.MinValue;
            foreach (var f in faces)
                if (f.Valid && f.T.All(OnWall))
                {
                    retail.Add(f.T);
                    foreach (var v in f.T) (low, high) = (Math.Min(low, v.Y), Math.Max(high, v.Y));
                }
            bool Beyond(NativeWideClipVertex v) => side * (v.X - outerWall) >= -16;
            if (side > 0)
                repairs.RemoveAll(r => Beyond(r.A) && Beyond(r.B) && Beyond(r.C)
                    && !(OnWall(r.A) && OnWall(r.B) && OnWall(r.C))
                    && !(r.A.Z == back && r.B.Z == back && r.C.Z == back)
                    && Math.Min(r.A.Y, Math.Min(r.B.Y, r.C.Y)) >= low - 100
                    && Math.Max(r.A.Y, Math.Max(r.B.Y, r.C.Y)) <= high + 100);
            for (int i = 0; i < repairs.Count; i++)
            {
                var r = repairs[i];
                if (!OnWall(r.A) || !OnWall(r.B) || !OnWall(r.C)) continue;
                var centre = (Position(r.A) + Position(r.B) + Position(r.C)) / 3;
                NativeWideClipVertex Shrunk(NativeWideClipVertex v) => v with
                {
                    Y = centre.Y + (v.Y - centre.Y) * 0.9f, Z = centre.Z + (v.Z - centre.Z) * 0.9f,
                };
                if (!retail.Overlaps([Shrunk(r.A), Shrunk(r.B), Shrunk(r.C)])) repairs[i] = r with { Boundary = true };
            }
        }
        return repairs;
    }

    // Behind the rooftops a slanted strip of forest (absolute Z -15048..-14000,
    // 2800-wide quads) runs along meshes i to m, each holding its own stretch;
    // nothing else lies that far back. A temple zone draws only its own
    // meshes, so at its ends the side band looked past the strip into the sky
    // (l1_zZ: j's stretch was missing). The neighbours add this strip only.
    const int NativeWideSunsetForestZ = -14000;
    static readonly Dictionary<NativeWideSceneryKey, int[]> _nativeWideSunsetForest = new();

    static int[] NativeWideSunsetForestStrip(IMemory m, NativeWideWorld world)
    {
        var key = new NativeWideSceneryKey(35, m.ReadU32(world.Header), m.ReadU32(world.Header + 4),
            m.ReadU32(world.Header + 8), world.PolyCount, world.VertexCount);
        if (_nativeWideSunsetForest.TryGetValue(key, out var strip)) return strip;
        int z = (int)m.ReadU32(world.Header + 8);
        bool Far(int vertex) => vertex < world.VertexCount
            && z + ReadNativeWideLocal(m, world, vertex).Z <= NativeWideSunsetForestZ;
        var polygons = new List<int>();
        for (int pi = 0; pi < world.PolyCount; pi++)
        {
            uint poly = world.Polygons + (uint)pi * 8u;
            NativeWidePolygonVertices(FastU32(m, poly), FastU32(m, poly + 4u), out int a, out int b, out int c);
            if (Far(a) && Far(b) && Far(c)) polygons.Add(pi);
        }
        return _nativeWideSunsetForest[key] = [.. polygons];
    }

    // The sunset skies (skyzW over the rooftops, sk2zW over the ending) are
    // camera-centred panoramas, rows of cells ending at about +/-47° (the
    // lowest row at +/-26°), so the 16:9 corners saw past them. Every cell
    // has its own picture, so nothing repeats. Like the other skies, each
    // row's end cell is mirrored about its outer edge, and the mirror again
    // about its own, until the row reaches past +/-75°; the seams stay
    // continuous. The end edges lean, so the mirror plane passes through the
    // edge and the camera. A cell the 4:3 cull halved along its diagonal is
    // completed first. Rows mirrored about slightly different planes would
    // split where they meet: each mirrored corner takes the mean of its
    // close images, then the position of a corner already there within 2°.
    static bool NativeWideSunsetSky(IMemory m, NativeWideWorld world) =>
        m.ReadU32(world.Header + 0x1C) == 1
        && (world.PolyCount, world.VertexCount) is (35, 27) or (42, 32);

    static List<NativeWideRepair> NativeWideSunsetSkyRepairs(IMemory m, NativeWideWorld world)
    {
        const float Degree = MathF.PI / 180;
        static float Angle(NativeWideClipVertex v) => MathF.Atan2(v.X, -v.Z);
        static Vector3 Key(NativeWideClipVertex v) => new(MathF.Round(v.X), MathF.Round(v.Y), MathF.Round(v.Z));
        var vertices = Enumerable.Range(0, world.VertexCount).Select(i => ReadNativeWideLocal(m, world, i)).ToArray();
        var sky = new List<(int Polygon, NativeWideClipVertex[] T)>();
        for (int pi = 0; pi < world.PolyCount; pi++)
        {
            uint poly = world.Polygons + (uint)pi * 8;
            NativeWidePolygonVertices(m.ReadU32(poly), m.ReadU32(poly + 4), out int a, out int b, out int c);
            if (!TryNativeWideMaterial(m, world, pi, 0, out _, out _,
                out short u0, out short v0, out short u1, out short v1, out short u2, out short v2)) continue;
            sky.Add((pi, [vertices[a] with { U = u0, V = v0 }, vertices[b] with { U = u1, V = v1 },
                vertices[c] with { U = u2, V = v2 }]));
        }
        var repairs = new List<NativeWideRepair>();
        // Edges used once, as (triangle, edge index).
        static List<(int T, int E)> Open(List<(int Polygon, NativeWideClipVertex[] T)> all)
        {
            static (Vector3, Vector3) EdgeOf(NativeWideClipVertex[] t, int e)
            {
                Vector3 p = Key(t[e]), q = Key(t[(e + 1) % 3]);
                return p.X < q.X || (p.X == q.X && (p.Y < q.Y || (p.Y == q.Y && p.Z < q.Z))) ? (p, q) : (q, p);
            }
            var count = new Dictionary<(Vector3, Vector3), int>();
            foreach (var (_, t) in all)
                for (int e = 0; e < 3; e++) count[EdgeOf(t, e)] = count.GetValueOrDefault(EdgeOf(t, e)) + 1;
            var open = new List<(int, int)>();
            for (int ti = 0; ti < all.Count; ti++)
                for (int e = 0; e < 3; e++)
                    if (count[EdgeOf(all[ti].T, e)] == 1) open.Add((ti, e));
            return open;
        }
        // Half cells: an open diagonal (wide in angle and in height) gets the
        // missing corner p + q - r, in position and texture.
        foreach (var (ti, e) in Open(sky))
        {
            var t = sky[ti].T;
            var p = t[e]; var q = t[(e + 1) % 3]; var r = t[(e + 2) % 3];
            if (MathF.Abs(Angle(p) - Angle(q)) < 5 * Degree || MathF.Abs(p.Y - q.Y) < 200) continue;
            repairs.Add(new(sky[ti].Polygon, q, p, p with
            {
                X = p.X + q.X - r.X, Y = p.Y + q.Y - r.Y, Z = p.Z + q.Z - r.Z, U = p.U + q.U - r.U, V = p.V + q.V - r.V,
            }));
        }
        List<(int Polygon, NativeWideClipVertex[] T)> cells = [.. sky, .. repairs.Select(r => (r.Polygon, new[] { r.A, r.B, r.C }))];
        // Each row's end: an open edge nearly along a column, and the cell
        // inside it (triangles within one column's width of the edge, in the
        // edge's height range).
        var ends = new List<(NativeWideClipVertex A, NativeWideClipVertex B, List<(int Polygon, NativeWideClipVertex[] T)> Cell)>();
        foreach (var (ti, e) in Open(cells))
        {
            var t = cells[ti].T;
            var a = t[e]; var b = t[(e + 1) % 3];
            if (MathF.Abs(Angle(a) - Angle(b)) > 3 * Degree || MathF.Abs(a.Y - b.Y) < 200) continue;
            float edge = (Angle(a) + Angle(b)) / 2, side = Math.Sign(edge - Angle(t[(e + 2) % 3]));
            float yLow = Math.Min(a.Y, b.Y) - 64, yHigh = Math.Max(a.Y, b.Y) + 64;
            var cell = cells.Where(c => c.T.All(v => v.Y >= yLow && v.Y <= yHigh
                && (edge - Angle(v)) * side >= -3 * Degree && (edge - Angle(v)) * side <= 21 * Degree)).ToList();
            if (cell.Count > 0) ends.Add((a, b, cell));
        }
        var corners = cells.SelectMany(c => c.T).ToList();
        for (int step = 0; step < 4 && ends.Count > 0; step++)
        {
            // Every end cell mirrored about the plane through its edge and the camera.
            var images = new Dictionary<Vector3, List<Vector3>>();
            foreach (var (a, b, cell) in ends)
            {
                var normal = Vector3.Normalize(Vector3.Cross(Position(a), Position(b)));
                foreach (var v in cell.SelectMany(c => c.T))
                {
                    if (!images.TryGetValue(Key(v), out var list)) images[Key(v)] = list = [];
                    list.Add(Position(v) - 2 * Vector3.Dot(Position(v), normal) * normal);
                }
            }
            // A corner shared by two rows ending at the same column has two
            // close images; rows ending elsewhere mirror it far away.
            NativeWideClipVertex Image(NativeWideClipVertex v, Vector3 normal)
            {
                var own = Position(v) - 2 * Vector3.Dot(Position(v), normal) * normal;
                var close = images[Key(v)].Where(x => Vector3.Distance(x, own) < 120).ToList();
                var p = close.Aggregate(Vector3.Zero, (s, x) => s + x) / close.Count;
                var moved = v with { X = p.X, Y = p.Y, Z = p.Z };
                foreach (var o in corners)
                    if (MathF.Abs(Angle(o) - Angle(moved)) < 2 * Degree && MathF.Abs(o.Y - moved.Y) <= 48)
                        return moved with { X = o.X, Y = o.Y, Z = o.Z };
                return moved;
            }
            var next = new List<(NativeWideClipVertex A, NativeWideClipVertex B, List<(int Polygon, NativeWideClipVertex[] T)> Cell)>();
            foreach (var (a, b, cell) in ends)
            {
                float edge = (Angle(a) + Angle(b)) / 2;
                var normal = Vector3.Normalize(Vector3.Cross(Position(a), Position(b)));
                var copy = cell.Select(c => (c.Polygon, T: c.T.Select(v => Image(v, normal)).ToArray()))
                    .Where(c => Key(c.T[0]) != Key(c.T[1]) && Key(c.T[1]) != Key(c.T[2]) && Key(c.T[2]) != Key(c.T[0]))
                    .ToList();
                if (copy.Count == 0) continue;
                foreach (var (polygon, t) in copy) repairs.Add(new(polygon, t[0], t[1], t[2]));
                var points = copy.SelectMany(c => c.T).ToList();
                corners.AddRange(points);
                // The copy's outer edge: its corners farthest from this edge,
                // at the top and at the bottom of the row.
                float side = Math.Sign(Angle(points.MaxBy(v => MathF.Abs(Angle(v) - edge))) - edge);
                float yMid = (a.Y + b.Y) / 2;
                var top = points.Where(v => v.Y < yMid).MaxBy(v => (Angle(v) - edge) * side);
                var bottom = points.Where(v => v.Y >= yMid).MaxBy(v => (Angle(v) - edge) * side);
                if (MathF.Abs(Angle(top)) < 75 * Degree) next.Add((top, bottom, copy.Select(c => (c.Polygon, c.T)).ToList()));
            }
            ends = next;
        }
        return repairs;
    }

    // The level starts at the west end of a walkway in front of the temple,
    // with forest panels behind. The panels start at absolute X 0, the
    // walkway at X 4800, and the 16:9 view saw past both. The panels repeat
    // every 6400 (four cells of different textures), the walkway (floor and
    // front; not the temple facade above it) every 800: whole periods
    // are copied west. Nothing is added over a face already there, so the
    // cells the 4:3 cull halved at the walkway's end are completed too.
    static bool NativeWideSunsetStart(IMemory m, NativeWideWorld world) =>
        world.PolyCount == 1897 && world.VertexCount == 1954
        && (int)m.ReadU32(world.Header) == 8000 && (int)m.ReadU32(world.Header + 8) == -4400;

    static readonly NativeWideCopy[] NativeWideSunsetStartCopies =
    [
        new(new(12700, -90000, -5700), new(19300, -70000, -2700), new(-12800, 0, 0)),
        new(new(12700, -90000, -5700), new(19300, -70000, -2700), new(-19200, 0, 0)),
        // The walkway's floor strips and front rows repeat every 800.
        new(new(5600, -84500, 0), new(6400, -83190, 1700), new(-800, 0, 0), Opaque: true),
        new(new(5600, -84500, 0), new(6400, -83190, 1700), new(-1600, 0, 0), Opaque: true),
        new(new(5600, -84500, 0), new(6400, -83190, 1700), new(-2400, 0, 0), Opaque: true),
        new(new(5600, -84500, 0), new(6400, -83190, 1700), new(-3200, 0, 0), Opaque: true),
        new(new(5600, -84500, 0), new(6400, -83190, 1700), new(-4000, 0, 0), Opaque: true),
    ];

    static List<NativeWideRepair> NativeWideSunsetStartRepairs(IMemory m, NativeWideWorld world)
    {
        var repairs = new List<NativeWideRepair>();
        NativeWideSunsetCopies(m, world, NativeWideFacesOf(m, world), NativeWideSunsetStartCopies, repairs, snap: true);
        return repairs;
    }

    // The temple's first mesh (f) also holds the west end of the walkway
    // outside (absolute X 100400), whose end face the 4:3 cull removed but
    // for one triangle. The walkway does not continue west (the pools lie
    // there); its end face is the next block's west face moved one block.
    static readonly NativeWideCopy[] NativeWideSunsetWalkwayCopies =
    [
        new(new(101184, -83616, 0), new(101216, -83184, 1600), new(-800, 0, 0)),
    ];

    static readonly NativeWideCopy[] NativeWideSunsetLintelCopies =
    [
        new(new(110784, -80816, 384), new(110816, -80384, 816), new(0, 0, 800)),
    ];

    static readonly NativeWideCopy[] NativeWideSunsetShaftCopies =
    [
        // The big ledge's underside (Y -36007).
        new(new(161184, -36016, -16), new(162016, -35998, 816), new(800, 0, 0)),
        new(new(161184, -36016, -16), new(162016, -35998, 816), new(0, 0, 800)),
        new(new(161184, -36016, -16), new(162016, -35998, 816), new(800, 0, 800)),
        // The right wall under it.
        new(new(162784, -33616, -16), new(162816, -32750, 9000), new(0, -3200, 0)),
        // The merlons (top, front and west side; their other sides face away).
        new(new(161600, -40400, 1200), new(162000, -40199, 1590), new(400, 0, 0)),
        new(new(161600, -40400, 1200), new(162000, -40199, 1590), new(800, 0, 0)),
    ];

    // Whole polygons in each copy's box, moved (and mirrored), each added
    // only where neither a face nor a repair is. Snap: for the start's Z-plane
    // copies (below).
    static void NativeWideSunsetCopies(IMemory m, NativeWideWorld world, NativeWideFace[] faces,
        NativeWideCopy[] copies, List<NativeWideRepair> repairs, bool snap, bool withRepairs = false)
    {
        // WithRepairs: the repairs already in each box (a wall's continuations) are copied too.
        var sources = faces.Select((f, pi) => (Valid: f.Valid, f.SemiTrans, f.T, Polygon: pi)).ToList();
        if (withRepairs)
            sources.AddRange(repairs.Select(r => (Valid: true, SemiTrans: false, T: new[] { r.A, r.B, r.C }, r.Polygon)));
        var origin = new Vector3((int)m.ReadU32(world.Header), (int)m.ReadU32(world.Header + 4),
            (int)m.ReadU32(world.Header + 8));
        // The forest panels are semi-transparent. The walkway's root decals
        // neither block its copies nor are copied (they do not repeat).
        NativeWideTriangleGrid all = new(), opaque = new();
        foreach (var f in faces)
        {
            all.Add(f.T);
            if (!f.SemiTrans) opaque.Add(f.T);
        }
        foreach (var r in repairs)
        {
            all.Add([r.A, r.B, r.C]);
            opaque.Add([r.A, r.B, r.C]);
        }
        // Cell corners are off the grid by up to 16, so a copy's corner can miss
        // the corner it meets by as much and open a crack: it takes that
        // corner's position (same plane) instead.
        var corners = new Dictionary<(int, int, float), List<Vector3>>();
        static (int, int, float) CellOf(Vector3 p) => ((int)MathF.Floor(p.X / 64), (int)MathF.Floor(p.Y / 64), p.Z);
        void AddCorner(Vector3 p)
        {
            if (!corners.TryGetValue(CellOf(p), out var list)) corners[CellOf(p)] = list = [];
            list.Add(p);
        }
        foreach (var f in faces) foreach (var v in f.T) AddCorner(Position(v));
        NativeWideClipVertex Snap(NativeWideClipVertex v)
        {
            var p = Position(v);
            var (cx, cy, z) = CellOf(p);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (corners.TryGetValue((cx + dx, cy + dy, z), out var list))
                        foreach (var q in list)
                            if (q != p && Vector3.DistanceSquared(p, q) <= 24 * 24) return v with { X = q.X, Y = q.Y };
            return v;
        }
        foreach (var copy in copies)
            foreach (var source in sources)
            {
                var t = source.T;
                if (!source.Valid || (copy.Opaque && source.SemiTrans) || t.Any(v => Position(v) + origin != Vector3.Clamp(Position(v) + origin, copy.Min, copy.Max)))
                    continue;
                NativeWideClipVertex[] moved = [.. t.Select(v => v with
                {
                    X = (float.IsNaN(copy.MirrorX) ? v.X : 2 * (copy.MirrorX - origin.X) - v.X) + copy.Offset.X,
                    Y = v.Y + copy.Offset.Y, Z = v.Z + copy.Offset.Z,
                }).Select(v => snap ? Snap(v) : v)];
                // Test a slightly smaller triangle, so neighbours touching by a few
                // units still fit.
                var centre = (Position(moved[0]) + Position(moved[1]) + Position(moved[2])) / 3;
                if ((copy.Opaque ? opaque : all).Overlaps([.. moved.Select(v => v with
                {
                    X = centre.X + (v.X - centre.X) * 0.9f, Y = centre.Y + (v.Y - centre.Y) * 0.9f,
                    Z = centre.Z + (v.Z - centre.Z) * 0.9f,
                })])) continue;
                all.Add(moved);
                opaque.Add(moved);
                foreach (var v in moved) AddCorner(Position(v));
                repairs.Add(new(source.Polygon, moved[0], moved[1], moved[2]));
            }
    }

    // The shafts' side walls (X planes, tall and reaching the shaft front at
    // Z 1600) continue towards the camera (+Z); not the sides of blocks,
    // pillars and door jambs. Back walls, statue fronts (Z planes) and floors
    // (Y planes) continue both ways along X, but not the thin platforms
    // (ledges, slabs) on the walls. The shafts' bases (statue fronts and their
    // floors) also stop at the outer walls, and the lowest side wall rows are
    // only two cells deep. The sides of braziers' slabs on one wall share
    // an X plane as tall as a wall, but cover little of it. The side walls'
    // front edges are lit blue: their copies fade back to the wall's own
    // colour within a cell instead of staying blue. A side wall cell with a
    // corner off the grid is still one cell (the third shaft's left wall,
    // where its two meshes meet at Y -27200: the row stopped at Z 1200).
    static readonly NativeWideRows[] NativeWideSunsetRows =
    [
        new(0, 2, Low: false, High: true, Extend: 4800, MinBlock: 1200, MinHeight: 4800, MinLength: 400, MinRun: 400,
            EdgeColour: true, Skew: 16, MinEnd: 1500, MinCover: 2400, EdgeFade: 400, MergeCells: true),
        new(2, 0, Low: true, High: true, Extend: 3200, MinBlock: 800, MinHeight: 800, MinLength: 800,
            MinRowHeight: 300, EdgeColour: true, Skew: 16),
        new(1, 0, Low: true, High: true, Extend: 3200, MinBlock: 800, MinHeight: 400, MinLength: 800,
            MinThickness: 300, EdgeColour: true, Skew: 16),
    ];

    // The third shaft's top step: X and Y planes of any size, towards +Z.
    static readonly NativeWideRows[] NativeWideSunsetStepRows =
    [
        new(0, 2, Low: false, High: true, Extend: 4800, MinBlock: 400, MinHeight: 400, MinLength: 400, MinRun: 400,
            EdgeColour: true, Skew: 16),
        new(1, 2, Low: false, High: true, Extend: 4800, MinBlock: 400, MinHeight: 400, MinLength: 400, MinRun: 400,
            EdgeColour: true, Skew: 16),
    ];

    // On the rooftops only tall walls continue.
    static readonly NativeWideRows[] NativeWideSunsetRoofRows =
    [
        new(0, 2, Low: false, High: true, Extend: 4800, MinBlock: 1200, MinHeight: 4800, MinLength: 400, MinRun: 400,
            EdgeColour: true, Skew: 16, MinEnd: 1500, MinCover: 2400, EdgeFade: 400, MergeCells: true),
        new(2, 0, Low: true, High: true, Extend: 3200, MinBlock: 800, MinHeight: 1600, MinLength: 1600,
            MinRowHeight: 300, EdgeColour: true, Skew: 16),
    ];
}
