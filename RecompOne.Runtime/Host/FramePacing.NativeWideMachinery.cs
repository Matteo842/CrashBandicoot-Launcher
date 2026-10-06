using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    // Heavy Machinery (level 6) is a side view. Every mesh is backed by a
    // riveted wall in the absolute plane Z 0, built from 400-unit cells, with
    // machinery, pillars and walkways in front of it. Its rows were culled per
    // triangle to the 4:3 view: wherever the camera stops moving sideways (the
    // level start, the vertical shafts, the turns) the 16:9 bands saw past
    // their ends and through cells missing between them. Each row is filled
    // with copies of its own cells, in every free slot of its grid and a few
    // past both ends. Extensions only reach the side bands and yield to every
    // original surface, so cells filled behind scenery stay hidden.
    const float NativeWideMachineryCell = 400;
    const int NativeWideMachinerySlots = 10;
    // A new cell repeats the cell this far towards the row's middle (or a
    // multiple of it), so the wall's panels stay lined up from row to row.
    const float NativeWideMachineryPeriod = 1600;

    static List<NativeWideRepair> NativeWideMachineryRepairs(IMemory m, NativeWideWorld world)
    {
        float plane = -(int)m.ReadU32(world.Header + 8);
        float cell = NativeWideMachineryCell, slack = cell / 25;
        var rects = new List<(float X0, float X1, float Y0, float Y1)>();
        var rows = new Dictionary<(float Y0, float Y1), SortedDictionary<float, List<NativeWideRepair>>>();
        var repairs = new List<NativeWideRepair>();
        for (int pi = 0; pi < world.PolyCount; pi++)
        {
            uint poly = world.Polygons + (uint)pi * 8;
            NativeWidePolygonVertices(FastU32(m, poly), FastU32(m, poly + 4), out int a, out int b, out int c);
            NativeWideClipVertex[] t = [ReadNativeWideLocal(m, world, a), ReadNativeWideLocal(m, world, b),
                ReadNativeWideLocal(m, world, c)];
            if (t[0].Z != plane || t[1].Z != plane || t[2].Z != plane) continue;
            float x0 = t.Min(v => v.X), x1 = t.Max(v => v.X), y0 = t.Min(v => v.Y), y1 = t.Max(v => v.Y);
            rects.Add((x0, x1, y0, y1));
            if (Math.Abs(x1 - x0 - cell) > slack * 2 || !TryNativeWideMaterial(m, world, pi, 0, out _, out _,
                out short u0, out short v0, out short u1, out short v1, out short u2, out short v2)) continue;
            if (!rows.TryGetValue((y0, y1), out var cells)) rows[(y0, y1)] = cells = [];
            if (!cells.TryGetValue(x0, out var tris)) cells[x0] = tris = [];
            tris.Add(new(pi, t[0] with { U = u0, V = v0 }, t[1] with { U = u1, V = v1 }, t[2] with { U = u2, V = v2 }));
        }
        // A slot is free when no face of the wall, original or added, overlaps it.
        bool Free(float x0, float x1, float y0, float y1) => !rects.Any(r =>
            r.X0 < x1 - slack && x0 < r.X1 - slack && r.Y0 < y1 - slack && y0 < r.Y1 - slack);
        foreach (var ((y0, y1), cells) in rows)
        {
            // Whole cells; a half cell left by the cull gets its missing corner.
            var whole = new SortedDictionary<float, List<NativeWideRepair>>();
            foreach (var (x0, tris) in cells)
            {
                if (tris.Count >= 2) { whole[x0] = tris; continue; }
                var t = tris[0];
                if (!NativeWideOppositeCorner([t.A, t.B, t.C], out var p, out var q, out var corner)) continue;
                repairs.Add(new(t.Polygon, p, q, corner));
                whole[x0] = [t, repairs[^1]];
            }
            if (whole.Count == 0) continue;
            // Cells hidden behind pillars and machinery are shaded black; copies
            // come from lit ones.
            var lit = whole.Where(e => e.Value.All(t => NativeWideLit(t.A) && NativeWideLit(t.B) && NativeWideLit(t.C)))
                .Select(e => e.Key).ToList();
            if (lit.Count == 0) lit = [.. whole.Keys];
            float first = cells.Keys.First(), last = cells.Keys.Last(), middle = (first + last) / 2;
            // The row's grid follows most of its cells (edge cells can be 408 wide).
            float phase = cells.Keys.GroupBy(x => ((x - first) % cell + cell) % cell).MaxBy(g => g.Count())!.Key;
            float start = first + phase - (phase > 0 ? cell : 0);
            int slots = (int)MathF.Ceiling((last - start) / cell);
            for (int i = -NativeWideMachinerySlots; i <= slots + NativeWideMachinerySlots; i++)
            {
                float x = start + i * cell;
                if (!Free(x, x + cell, y0, y1)) continue;
                // Towards the middle by whole periods, the nearest lit cell otherwise.
                float inward = x < middle ? 1 : -1, source = float.NaN;
                for (float s = x + inward * NativeWideMachineryPeriod; float.IsNaN(source)
                    && (inward > 0 ? s <= last + slack : s >= first - slack); s += inward * NativeWideMachineryPeriod)
                    source = lit.FirstOrDefault(e => Math.Abs(e - s) <= slack, float.NaN);
                if (float.IsNaN(source)) source = lit.MinBy(e => Math.Abs(e - x));
                float dx = x - source;
                foreach (var t in whole[source])
                    repairs.Add(new(t.Polygon, t.A with { X = t.A.X + dx }, t.B with { X = t.B.X + dx },
                        t.C with { X = t.C.X + dx }));
                rects.Add((x, x + cell, y0, y1));
            }
        }
        return repairs;
    }

    static bool NativeWideLit(NativeWideClipVertex v) => Math.Max(v.R, Math.Max(v.G, v.B)) > 24;
}
