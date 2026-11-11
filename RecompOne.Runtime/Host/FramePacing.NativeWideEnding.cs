using System.Numerics;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Hle;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    // The 100% ending's epilogue (#189, The Great Hall's y__IZ) shows each boss
    // in front of a stone wall: WinGC Background_Stance, a FLAG_2D model
    // (Obg0V) at scale 0.6 with a random tilt, sized for the 4:3 screen with
    // about 8% to spare on each side (x -20..529 of 512). The 16:9 bands showed
    // black beside it. While it is drawn in widescreen its scale grows by the
    // 16:9 / 4:3 ratio, so the wall keeps the same spare on the wider screen;
    // nothing else of the scene changes.
    const uint NativeWideEpilogueLevel = 44;
    const int NativeWideEpilogueScale = 2458, NativeWideEpilogueScaleSlop = 64;

    static uint _wideEpilogueObj;
    static int _wideEpilogueX, _wideEpilogueY, _wideEpilogueZ;

    static void WidenNativeWideEpilogueBackground(CpuContext c, IMemory m)
    {
        if (!GpuHle.WideFovActive || GpuHle.WideAspect <= GpuHle.BaseAspect) return;
        uint obj = c.A2;
        if ((obj & 0xFF000000u) != 0x80000000u) obj = _obj;
        if ((obj & 0xFF000000u) != 0x80000000u) return;
        try
        {
            if (m.ReadU32(Catalogs.Catalog.LevelIdAddr) != NativeWideEpilogueLevel) return;
            if ((m.ReadU32(obj + ObjStatusBOff) & Flag2D) == 0) return;
            if (!TryReadGoolClass(m, obj, out uint type, out _) || type != GoolTypeWinG) return;
            int x = (int)m.ReadU32(obj + ObjScaleOff), y = (int)m.ReadU32(obj + ObjScaleOff + 4),
                z = (int)m.ReadU32(obj + ObjScaleOff + 8);
            if (Math.Abs(x - NativeWideEpilogueScale) > NativeWideEpilogueScaleSlop || y != x || z != x) return;
            float grow = GpuHle.WideAspect / GpuHle.BaseAspect;
            _wideEpilogueObj = obj;
            (_wideEpilogueX, _wideEpilogueY, _wideEpilogueZ) = (x, y, z);
            for (uint i = 0; i < 3; i++)
                m.WriteU32(obj + ObjScaleOff + i * 4, (uint)(int)MathF.Round(x * grow));
        }
        catch
        {
            RestoreNativeWideEpilogueBackground(m);
        }
    }

    static void RestoreNativeWideEpilogueBackground(IMemory? m)
    {
        uint obj = _wideEpilogueObj;
        if (obj == 0) return;
        _wideEpilogueObj = 0;
        if (m == null) return;
        try
        {
            m.WriteU32(obj + ObjScaleOff, (uint)_wideEpilogueX);
            m.WriteU32(obj + ObjScaleOff + 4, (uint)_wideEpilogueY);
            m.WriteU32(obj + ObjScaleOff + 8, (uint)_wideEpilogueZ);
        }
        catch
        {
            // object or overlay gone
        }
    }

    // The ending's skies (#189): sk3VW behind the blimp, sk4VW behind the
    // credits. Both are camera-centred grids, columns every 18° around the
    // camera and rows at fixed heights, culled to the 4:3 view: sk3VW lost its
    // top-right and bottom-left corner cells, sk4VW nearly all of its left
    // column (the credits look left of it). Half cells are completed, then each
    // empty cell copies its neighbour in the same row mirrored about their
    // shared column (the sky's gradient runs along the columns), or the one in
    // the same column when its row has none. Copied corners keep the source
    // corner's texture coordinate and colour, so seams match exactly.
    static bool NativeWideEndingSky(IMemory m, NativeWideWorld world, uint level) =>
        level == 57 && m.ReadU32(world.Header + 0x1C) == 1
        && (world.PolyCount, world.VertexCount) is (62, 44) or (83, 55);

    static IReadOnlyList<NativeWideRepair> NativeWideEndingSkyRepairs(IMemory m, NativeWideWorld world, uint level)
    {
        var key = new NativeWideSceneryKey(level, m.ReadU32(world.Header), m.ReadU32(world.Header + 4),
            m.ReadU32(world.Header + 8), world.PolyCount, world.VertexCount);
        if (_nativeWideSkyArcs.TryGetValue(key, out var cached)) return cached;
        var repairs = new List<NativeWideRepair>();
        _nativeWideSkyArcs[key] = repairs;

        var vertices = Enumerable.Range(0, world.VertexCount).Select(i => ReadNativeWideLocal(m, world, i)).ToArray();
        static float Angle(NativeWideClipVertex v) => MathF.Atan2(v.X, -v.Z);
        float[] columns = NativeWideClusters(vertices.Select(Angle), MathF.PI / 180);
        float[] rows = NativeWideClusters(vertices.Select(v => v.Y), 64);
        int Column(NativeWideClipVertex v) => NativeWideNearest(columns, Angle(v));
        int Row(NativeWideClipVertex v) => NativeWideNearest(rows, v.Y);
        int nc = columns.Length, nr = rows.Length;
        if (nc < 2 || nr < 2) return repairs;

        // Grid points: the mesh's own vertex where there is one, else the
        // column's angle at the row's height and mean horizontal radius.
        var points = new Vector3?[nc, nr];
        foreach (var v in vertices) points[Column(v), Row(v)] ??= Position(v);
        for (int r = 0; r < nr; r++)
        {
            var row = vertices.Where(v => Row(v) == r).ToArray();
            float radius = row.Average(v => MathF.Sqrt(v.X * v.X + v.Z * v.Z));
            for (int c = 0; c < nc; c++)
                points[c, r] ??= new(radius * MathF.Sin(columns[c]), rows[r], -radius * MathF.Cos(columns[c]));
        }

        // Cells hold triangles as corner offsets (0/1 column, 0/1 row) with the
        // corner's texture coordinate and colour.
        var cells = new List<(int Polygon, (int Dc, int Dr, NativeWideClipVertex V)[] T)>[nc - 1, nr - 1];
        for (int c = 0; c < nc - 1; c++)
            for (int r = 0; r < nr - 1; r++)
                cells[c, r] = [];
        var faces = NativeWideFacesOf(m, world);
        for (int pi = 0; pi < faces.Length; pi++)
        {
            if (!faces[pi].Valid) continue;
            var t = faces[pi].T;
            int c0 = t.Min(Column), r0 = t.Min(Row);
            if (t.Max(Column) != c0 + 1 || t.Max(Row) != r0 + 1) continue;
            cells[c0, r0].Add((pi, t.Select(v => (Column(v) - c0, Row(v) - r0, v)).ToArray()));
        }

        NativeWideClipVertex At(int c, int r, NativeWideClipVertex look)
        {
            var p = points[c, r]!.Value;
            return look with { X = p.X, Y = p.Y, Z = p.Z };
        }
        void Add(int c, int r, int polygon, (int Dc, int Dr, NativeWideClipVertex V)[] t)
        {
            cells[c, r].Add((polygon, t));
            repairs.Add(new(polygon, At(c + t[0].Dc, r + t[0].Dr, t[0].V),
                At(c + t[1].Dc, r + t[1].Dr, t[1].V), At(c + t[2].Dc, r + t[2].Dr, t[2].V)));
        }

        // Half cells: the missing corner continues both edges of the right angle.
        for (int c = 0; c < nc - 1; c++)
            for (int r = 0; r < nr - 1; r++)
            {
                if (cells[c, r].Count != 1) continue;
                var (pi, t) = cells[c, r][0];
                int right = Enumerable.Range(0, 3).FirstOrDefault(k =>
                    t[(k + 1) % 3].Dc != t[(k + 2) % 3].Dc && t[(k + 1) % 3].Dr != t[(k + 2) % 3].Dr, -1);
                if (right < 0) continue;
                var a = t[(right + 1) % 3]; var b = t[(right + 2) % 3]; var o = t[right];
                var corner = o.V with
                {
                    U = a.V.U + b.V.U - o.V.U, V = a.V.V + b.V.V - o.V.V,
                    R = Math.Clamp(a.V.R + b.V.R - o.V.R, 0, 255),
                    G = Math.Clamp(a.V.G + b.V.G - o.V.G, 0, 255),
                    B = Math.Clamp(a.V.B + b.V.B - o.V.B, 0, 255),
                };
                Add(c, r, pi, [a, b, (a.Dc + b.Dc - o.Dc, a.Dr + b.Dr - o.Dr, corner)]);
            }

        // Empty cells, outward from the authored ones: row neighbours first.
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (bool alongRow in new[] { true, false })
            {
                var fills = new List<(int C, int R, int Sc, int Sr)>();
                for (int c = 0; c < nc - 1; c++)
                    for (int r = 0; r < nr - 1; r++)
                    {
                        if (cells[c, r].Count != 0) continue;
                        foreach (int step in new[] { -1, 1 })
                        {
                            int sc = alongRow ? c + step : c, sr = alongRow ? r : r + step;
                            if (sc < 0 || sr < 0 || sc >= nc - 1 || sr >= nr - 1 || cells[sc, sr].Count < 2) continue;
                            fills.Add((c, r, sc, sr));
                            break;
                        }
                    }
                foreach (var (c, r, sc, sr) in fills)
                {
                    if (cells[c, r].Count != 0) continue;
                    foreach (var (pi, t) in cells[sc, sr].ToArray())
                        Add(c, r, pi, t.Select(e => alongRow ? (1 - e.Dc, e.Dr, e.V) : (e.Dc, 1 - e.Dr, e.V)).ToArray());
                }
                if (fills.Count > 0) { changed = true; break; }
            }
        }
        PaceLog($"native-wide level={level} ending sky repairs={repairs.Count}");
        return repairs;
    }

    // Sorted cluster centres of values closer than tolerance to their neighbour.
    static float[] NativeWideClusters(IEnumerable<float> values, float tolerance)
    {
        var centres = new List<(float Sum, int Count, float Last)>();
        foreach (float v in values.OrderBy(v => v))
            if (centres.Count > 0 && v - centres[^1].Last < tolerance)
                centres[^1] = (centres[^1].Sum + v, centres[^1].Count + 1, v);
            else
                centres.Add((v, 1, v));
        return centres.Select(e => e.Sum / e.Count).ToArray();
    }

    static int NativeWideNearest(float[] centres, float value)
    {
        int best = 0;
        for (int i = 1; i < centres.Length; i++)
            if (Math.Abs(centres[i] - value) < Math.Abs(centres[best] - value)) best = i;
        return best;
    }
}
