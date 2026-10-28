// Ground work: extending a mesh past its cut edge with mirrored cells (the edge row
// continued like the runtime's terrain lattice, but by hand), sculpting the user's
// triangles in height (raise, lower, smooth, flatten), and cleanup helpers: select
// everything linked to the selection, box selection through the scene.
using System.Numerics;
using ImGuiNET;

namespace WideEditor;

enum SculptMode { Raise, Lower, Smooth, Flatten }
enum ExtendSlope { Continue, Fade, Flat }
enum ExtendSide { Bands, Left, Right, Far, All }

sealed partial class Editor
{
    SculptMode _sculptMode = SculptMode.Smooth;
    float _sculptRadius = 600, _sculptStrength = 0.5f, _sculptY0, _frameDt;
    bool _sculptAutos = true, _sculptLockOriginals = true;
    int _sculptGroup;
    HashSet<VKey>? _sculptLocked;
    int _extendSteps = 3;
    ExtendSlope _extendSlope = ExtendSlope.Fade;
    ExtendSide _extendSide;
    bool _boxThrough, _extendLone;

    /// <summary>A vertex position rounded to whole units: corners that touch share it.</summary>
    readonly record struct VKey(int X, int Y, int Z);

    static VKey KeyOf(Vector3 p) => new((int)MathF.Round(p.X), (int)MathF.Round(p.Y), (int)MathF.Round(p.Z));

    static (VKey, VKey) EdgeOf(Vector3 a, Vector3 b)
    {
        var ka = KeyOf(a);
        var kb = KeyOf(b);
        bool less = ka.X != kb.X ? ka.X < kb.X : ka.Y != kb.Y ? ka.Y < kb.Y : ka.Z < kb.Z;
        return less ? (ka, kb) : (kb, ka);
    }

    static Vector3 NormalOf(Vector3[] p)
    {
        var n = Vector3.Cross(p[1] - p[0], p[2] - p[0]);
        return n.LengthSquared() > 1e-6f ? Vector3.Normalize(n) : Vector3.Zero;
    }

    static float Flat(Vector3 d) => MathF.Sqrt(d.X * d.X + d.Z * d.Z);

    /// <summary>Any triangle of the scene (original, automatic or own) with its texture.</summary>
    sealed class Geo
    {
        public long Key;
        public Vector3[] P = new Vector3[3];
        public Vector2[] Uv = new Vector2[3];
        public Vector3[] Rgb = new Vector3[3];
        public string Mesh = "";
        public int Poly = -1;
        public bool Behind, Culled;
        public float Rank;
        public (string Mesh, int Page, Material Mat) Tex;
    }

    bool AutoDrawn(Mesh m) => _showAuto && (!_gameView || OwnMesh(m) || _auto!.NeighbourRepairs);

    /// <summary>Texture an original is drawn with (its own, or the one put on it).</summary>
    (int Poly, Vector2[] Uv) RetailTexture(Mesh m, int p)
    {
        if (_doc.Retex.TryGetValue((m.Name, p), out var r) && r.Source < m.PolyCount)
        {
            var su = L!.UvsOf(m, r.Source);
            return (r.Source, [.. Enumerable.Range(0, 3).Select(j => su[(j - r.Turn % 3 + 3) % 3])]);
        }
        return (p, L!.UvsOf(m, p));
    }

    /// <summary>Fast "automatic switched off" test for many triangles.</summary>
    Func<AutoTri, bool> RepairOffTest()
    {
        var centres = new Dictionary<(string, int), List<Vector3>>();
        foreach (var h in _doc.HiddenRepairs)
        {
            if (!centres.TryGetValue((h.Mesh, h.Poly), out var list)) centres[(h.Mesh, h.Poly)] = list = [];
            list.Add(h.Center);
        }
        // Same test as EditDoc.IsRepairHidden, only among the switched-off pieces of that polygon.
        return t => centres.TryGetValue((t.Mesh.Name, t.Poly), out var list)
            && list.Exists(c => Vector3.DistanceSquared(c, t.Center) < 4);
    }

    /// <summary>Every triangle drawn around the camera (meshes of the zone + neighbours):
    /// originals, automatics that are on, own triangles. <paramref name="pick"/>: as the
    /// view shows them (switched-off ones too when they are shown in red).</summary>
    List<Geo> SceneGeo(bool pick)
    {
        var list = new List<Geo>();
        if (L == null || _auto == null) return list;
        var off = RepairOffTest();
        var drawn = new HashSet<string>();
        foreach (var m in _drawMeshes)
        {
            if (m.Backdrop) continue;
            drawn.Add(m.Name);
            for (int p = 0; p < m.PolyCount; p++)
            {
                if (_doc.Removed.Contains((m.Name, p)) && !(pick && _showOff)) continue;
                var (tp, uv) = RetailTexture(m, p);
                var g = new Geo
                {
                    Key = Key(Kind.Retail, _meshBase[m.Index] + p), Mesh = m.Name, Poly = tp, Uv = uv,
                    Behind = _newBehind, Tex = (m.Name, m.Tpag[tp], L.MaterialOf(m, tp)),
                };
                for (int k = 0; k < 3; k++)
                {
                    g.P[k] = m.Corner(p, k);
                    g.Rgb[k] = m.Rgb[m.Tri[p * 3 + k]];
                }
                list.Add(g);
            }
            if (AutoDrawn(m) && _auto.Ranges.TryGetValue(m, out var r))
                for (int i = r.Start; i < r.Start + r.Count; i++)
                {
                    var t = _auto.Tris[i];
                    if (off(t) && !(pick && _showOff)) continue;
                    bool textured = t.Poly >= 0 && t.Poly < m.PolyCount;
                    list.Add(new Geo
                    {
                        Key = Key(Kind.Auto, i), Mesh = m.Name, Poly = textured ? t.Poly : -1,
                        P = [t.A.Pos, t.B.Pos, t.C.Pos], Uv = [t.A.Uv, t.B.Uv, t.C.Uv], Rgb = [t.A.Rgb, t.B.Rgb, t.C.Rgb],
                        Behind = t.Behind, Culled = t.Culled, Rank = t.Rank,
                        Tex = textured ? (m.Name, m.Tpag[t.Poly], L.MaterialOf(m, t.Poly)) : default,
                    });
                }
        }
        if (!pick || _showUser)
            for (int i = 0; i < _doc.Tris.Count; i++)
            {
                var t = _doc.Tris[i];
                if (!drawn.Contains(t.Mesh) || !L.MeshByName.TryGetValue(t.Mesh, out var m)) continue;
                bool textured = t.Poly >= 0 && t.Poly < m.PolyCount;
                list.Add(new Geo
                {
                    Key = Key(Kind.User, i), Mesh = t.Mesh, Poly = textured ? t.Poly : -1,
                    P = [.. t.V.Select(v => v.Pos)], Uv = [.. t.V.Select(v => v.Uv)], Rgb = [.. t.V.Select(v => v.Rgb)],
                    Behind = t.Behind, Culled = t.Culled, Rank = t.Rank,
                    Tex = textured ? (m.Name, m.Tpag[t.Poly], L.MaterialOf(m, t.Poly)) : default,
                });
            }
        return list;
    }

    // ---- Extend the ground past its edge ----

    /// <summary>An open edge being pushed outward: P0 -> P1 in its triangle's winding, N0/N1
    /// the corners one cell back (the edge moves by P - N), with texture and colour of all four.</summary>
    sealed class Front
    {
        public required Geo Src;
        public Vector3 P0, P1, N0, N1;
        public Vector2 U0, U1, UN0, UN1;
        public Vector3 C0, C1, CN0, CN1;
        public bool Lone;
        public float Len0;
    }

    /// <summary>Open edges of the selection (no other triangle drawn on them) get rows of new
    /// cells outward, each the size of the cell behind it, the texture mirrored across the
    /// edge (so it stays inside the tile and has no seam), heights continued, fading or flat.</summary>
    void ExtendTerrain()
    {
        if (L == null || _sel.Count == 0) return;
        var all = SceneGeo(false);
        var edges = new Dictionary<(VKey, VKey), List<int>>();
        for (int i = 0; i < all.Count; i++)
            for (int k = 0; k < 3; k++)
            {
                var e = EdgeOf(all[i].P[k], all[i].P[(k + 1) % 3]);
                if (e.Item1 == e.Item2) continue;
                if (!edges.TryGetValue(e, out var list)) edges[e] = list = [];
                list.Add(i);
            }
        var front = new List<Front>();
        int sources = 0, joined = 0, untextured = 0;
        for (int i = 0; i < all.Count; i++)
        {
            if (!_sel.Contains(all[i].Key)) continue;
            sources++;
            if (all[i].Poly < 0) { untextured++; continue; }
            for (int k = 0; k < 3; k++)
            {
                var e = EdgeOf(all[i].P[k], all[i].P[(k + 1) % 3]);
                if (e.Item1 == e.Item2) continue;
                if (edges[e].Count > 1) { joined++; continue; }
                if (SideOf(all, edges, i, k) is { } f) front.Add(f);
            }
        }
        if (sources == 0) { Say("Seleziona prima il terreno da estendere (originali, automatici o tuoi)."); return; }
        // Holes first, on every open edge (the side filter would break their rings).
        var holes = HoleEdges(front);
        int lone = front.Count(f => f.Lone && !holes.Contains(f));
        int open = front.Count;
        front = [.. front.Where(f => !holes.Contains(f) && (_extendLone || !f.Lone) && FacesSide(f))];
        string skipped = (holes.Count > 0 ? $" {holes.Count} bordi di buchi interni saltati." : "")
            + (lone > 0 && !_extendLone ? $" {lone} bordi di triangoli isolati saltati." : "");
        if (front.Count == 0)
        {
            Say(untextured == sources ? "Questi triangoli non hanno una texture del gioco da continuare."
                : open == 0 ? "Nessun bordo libero da estendere: i bordi della selezione toccano già altri triangoli."
                    + " Elimina prima gli automatici che li coprono, oppure seleziona le celle del bordo."
                : "Nessun bordo da estendere" + (_extendSide != ExtendSide.All ? " rivolto dal lato scelto." : ".") + skipped);
            return;
        }

        BeginChange();
        int group = _doc.NextGroup++;
        float keep = _extendSlope switch { ExtendSlope.Continue => 1, ExtendSlope.Fade => 0.5f, _ => 0 };
        var made = new List<UserTri>();
        UserTri Tri(Geo src, Vector3 a, Vector3 b, Vector3 c, Vector2 ua, Vector2 ub, Vector2 uc, Vector3 ca, Vector3 cb, Vector3 cc) => new()
        {
            Mesh = src.Mesh, Poly = src.Poly, Behind = src.Behind, Rank = src.Rank, Culled = src.Culled, Group = group,
            V = [new() { Pos = a, Uv = ua, Rgb = ca }, new() { Pos = b, Uv = ub, Rgb = cb }, new() { Pos = c, Uv = uc, Rgb = cc }],
        };
        foreach (var f in front) f.Len0 = Vector3.Distance(f.P0, f.P1);
        int edgeCount = front.Count, stopped = 0;
        var ground = new GroundIndex();
        foreach (var g in all) ground.Add(g.P[0], g.P[1], g.P[2]);
        for (int step = 0; step < _extendSteps && front.Count > 0; step++)
        {
            // One offset per corner: rows along a straight edge share it, corners get the
            // mitre of both rows, so neighbouring cells stay joined.
            var pushes = new Dictionary<VKey, List<Vector3>>();
            void Push(Vector3 p, Vector3 d)
            {
                d.Y *= keep;
                var key = KeyOf(p);
                if (!pushes.TryGetValue(key, out var l)) pushes[key] = l = [];
                l.Add(d);
            }
            foreach (var f in front)
            {
                Push(f.P0, f.P0 - f.N0);
                Push(f.P1, f.P1 - f.N1);
            }
            var offset = pushes.ToDictionary(kv => kv.Key, kv => Mitre(kv.Value));
            var next = new List<Front>();
            var cells = new List<(Vector3, Vector3, Vector3, Vector3)>();
            foreach (var f in front)
            {
                var q0 = f.P0 + offset[KeyOf(f.P0)];
                var q1 = f.P1 + offset[KeyOf(f.P1)];
                // A row stops where its new edge turns over or gets much shorter or longer
                // than at the start (rings closing in, crowded corners), or where the new cell
                // would lie on ground already there (a neighbour not joined at the corners).
                Vector3 was = f.P1 - f.P0, now = q1 - q0;
                if (Vector3.Dot(was, now) < 0.5f * was.Length() * now.Length()
                    || now.Length() < 0.5f * f.Len0 || now.Length() > 2 * f.Len0
                    || ground.Covers((f.P0 + f.P1 + q0 + q1) / 4, (now.Length() + was.Length()) / 4))
                {
                    stopped++;
                    continue;
                }
                // The edge runs P1 -> P0 in the new cell (the other side of it).
                made.Add(Tri(f.Src, f.P1, f.P0, q0, f.U1, f.U0, f.UN0, f.C1, f.C0, f.CN0));
                made.Add(Tri(f.Src, f.P1, q0, q1, f.U1, f.UN0, f.UN1, f.C1, f.CN0, f.CN1));
                cells.Add((f.P1, f.P0, q0, q1));
                next.Add(new Front
                {
                    Src = f.Src, P0 = q0, P1 = q1, N0 = f.P0, N1 = f.P1, Len0 = f.Len0,
                    U0 = f.UN0, U1 = f.UN1, UN0 = f.U0, UN1 = f.U1, C0 = f.CN0, C1 = f.CN1, CN0 = f.C0, CN1 = f.C1,
                });
            }
            // Later rows must not run over these either (rows crossing at corners).
            foreach (var (a, b, c, d) in cells)
            {
                ground.Add(a, b, c);
                ground.Add(a, c, d);
            }
            front = next;
        }
        if (made.Count == 0)
        {
            Say("Nessuna cella creata: oltre il bordo c'è già altra geometria, oppure le celle verrebbero storte." + skipped);
            return;
        }
        _sel.Clear();
        foreach (var t in made)
        {
            _sel.Add(Key(Kind.User, _doc.Tris.Count));
            _doc.Tris.Add(t);
        }
        Changed();
        Say($"Terreno esteso da {edgeCount} bordi, fino a {_extendSteps} celle ({made.Count} triangoli tuoi, selezionati)."
            + (stopped > 0 ? $" {stopped} file fermate prima (sopra altra geometria o celle storte)." : "")
            + skipped + " Premi ancora per allungare, oppure usa Scolpisci per sistemare le altezze.");
    }

    /// <summary>Ground triangles bucketed on the X/Z plane, to ask whether a point has
    /// ground right under or over it (walls and steep faces left out).</summary>
    sealed class GroundIndex
    {
        const float Cell = 512;
        readonly Dictionary<(int, int), List<(Vector3 A, Vector3 B, Vector3 C)>> _cells = [];

        public void Add(Vector3 a, Vector3 b, Vector3 c)
        {
            var n = Vector3.Cross(b - a, c - a);
            if (n.LengthSquared() < 1e-3f || MathF.Abs(n.Y) < 0.3f * n.Length()) return;
            int x0 = (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) / Cell), x1 = (int)MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X)) / Cell);
            int z0 = (int)MathF.Floor(MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) / Cell), z1 = (int)MathF.Floor(MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) / Cell);
            if ((long)(x1 - x0 + 1) * (z1 - z0 + 1) > 4096) return;
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    if (!_cells.TryGetValue((x, z), out var list)) _cells[(x, z)] = list = [];
                    list.Add((a, b, c));
                }
        }

        /// <summary>Ground near the point's height (twice <paramref name="height"/>, at least 600) above or below it.</summary>
        public bool Covers(Vector3 p, float height)
        {
            if (!_cells.TryGetValue(((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Z / Cell)), out var list)) return false;
            foreach (var (a, b, c) in list)
            {
                float d = (b.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (b.Z - a.Z);
                if (MathF.Abs(d) < 1e-3f) continue;
                float u = ((p.X - a.X) * (c.Z - a.Z) - (c.X - a.X) * (p.Z - a.Z)) / d;
                float v = ((b.X - a.X) * (p.Z - a.Z) - (p.X - a.X) * (b.Z - a.Z)) / d;
                if (u < 0 || v < 0 || u + v > 1) continue;
                float y = a.Y + u * (b.Y - a.Y) + v * (c.Y - a.Y);
                if (MathF.Abs(y - p.Y) < Math.Max(2 * height, 600)) return true;
            }
            return false;
        }
    }

    /// <summary>Open edges that close a ring around a hole of the surface (they would grow
    /// inward and pile up): rings followed corner to corner, a hole being a ring whose
    /// edges all push toward its inside. Winding-free: the push is from the cell geometry.</summary>
    static HashSet<Front> HoleEdges(List<Front> front)
    {
        var holes = new HashSet<Front>();
        var byStart = new Dictionary<VKey, List<Front>>();
        foreach (var f in front)
        {
            var key = KeyOf(f.P0);
            if (!byStart.TryGetValue(key, out var list)) byStart[key] = list = [];
            list.Add(f);
        }
        var used = new HashSet<Front>();
        foreach (var first in front)
        {
            if (used.Contains(first)) continue;
            var ring = new List<Front> { first };
            used.Add(first);
            var start = KeyOf(first.P0);
            var at = first;
            bool closed = false;
            while (ring.Count < 100000)
            {
                var end = KeyOf(at.P1);
                if (end == start) { closed = true; break; }
                if (!byStart.TryGetValue(end, out var nexts)) break;
                var next = nexts.FirstOrDefault(n => !used.Contains(n));
                if (next == null) break;
                used.Add(next);
                ring.Add(next);
                at = next;
            }
            if (!closed || ring.Count < 3) continue;
            // Signed area on the ground plane, and how the pushes lean against the edges' left.
            float area = 0, lean = 0;
            foreach (var f in ring)
            {
                area += f.P0.X * f.P1.Z - f.P1.X * f.P0.Z;
                var e = f.P1 - f.P0;
                var push = (f.P0 - f.N0) + (f.P1 - f.N1);
                lean += push.X * -e.Z + push.Z * e.X;
            }
            if (area * lean > 0) foreach (var f in ring) holes.Add(f);
        }
        return holes;
    }

    /// <summary>The open edge k of triangle i with the corners one cell back: from the
    /// triangle sharing its diagonal (the other half of the quad), else straight across.</summary>
    static Front? SideOf(List<Geo> all, Dictionary<(VKey, VKey), List<int>> edges, int i, int k)
    {
        var g = all[i];
        int kb = (k + 1) % 3, kx = (k + 2) % 3;
        var f = new Front
        {
            Src = g, P0 = g.P[k], P1 = g.P[kb], U0 = g.Uv[k], U1 = g.Uv[kb], C0 = g.Rgb[k], C1 = g.Rgb[kb],
        };
        var x = g.P[kx];
        var mid = (f.P0 + f.P1) / 2;
        var n = NormalOf(g.P);
        // Both other edges can have a neighbour of the same texture: the one across the
        // quad's diagonal gives two parallel sides and a far edge like the open one; the one
        // across a side is the next cell over (its corner would send the row off at a slant).
        float best = 0;
        foreach (bool viaA in new[] { true, false })
        {
            var shared = viaA ? f.P0 : f.P1;
            if (!edges.TryGetValue(EdgeOf(shared, x), out var list)) continue;
            foreach (int j in list)
            {
                var h = all[j];
                if (j == i || h.Tex != g.Tex || MathF.Abs(Vector3.Dot(NormalOf(h.P), n)) < 0.6f) continue;
                int ky = -1;
                for (int q = 0; q < 3; q++)
                    if (KeyOf(h.P[q]) != KeyOf(shared) && KeyOf(h.P[q]) != KeyOf(x)) ky = q;
                if (ky < 0) continue;
                // Quad P0 P1 X Y (shared P0-X) or P0 P1 Y X (shared P1-X): each edge corner's
                // neighbour along the quad's side.
                Vector3 n0 = viaA ? h.P[ky] : x, n1 = viaA ? x : h.P[ky];
                var d0 = f.P0 - n0;
                var d1 = f.P1 - n1;
                if (d0.LengthSquared() < 1 || d1.LengthSquared() < 1) continue;
                if (Vector3.Dot(d0 + d1, mid - x) <= 0) continue;
                var far = n1 - n0;
                var open = f.P1 - f.P0;
                if (far.LengthSquared() < 1) continue;
                float sides = Vector3.Dot(Vector3.Normalize(d0), Vector3.Normalize(d1));
                float edgesAlike = Vector3.Dot(Vector3.Normalize(far), Vector3.Normalize(open));
                float score = sides + edgesAlike;
                if (sides < 0.85f || edgesAlike < 0.85f || score <= best) continue;
                best = score;
                f.N0 = n0;
                f.N1 = n1;
                f.UN0 = viaA ? h.Uv[ky] : g.Uv[kx];
                f.UN1 = viaA ? g.Uv[kx] : h.Uv[ky];
                f.CN0 = viaA ? h.Rgb[ky] : g.Rgb[kx];
                f.CN1 = viaA ? g.Rgb[kx] : h.Rgb[ky];
            }
        }
        if (best > 0) return f;
        // A triangle on its own: the far corner's distance from the edge, straight out.
        var ab = f.P1 - f.P0;
        if (ab.LengthSquared() < 1) return null;
        var d = f.P0 + ab * (Vector3.Dot(x - f.P0, ab) / ab.LengthSquared()) - x;
        if (d.LengthSquared() < 1) return null;
        f.N0 = f.P0 - d;
        f.N1 = f.P1 - d;
        f.UN0 = f.UN1 = g.Uv[kx];
        f.CN0 = f.CN1 = g.Rgb[kx];
        f.Lone = true;
        return f;
    }

    /// <summary>The side filter: which way the edge grows on screen.</summary>
    bool FacesSide(Front f)
    {
        if (_extendSide == ExtendSide.All) return true;
        var d = (f.P0 - f.N0) + (f.P1 - f.N1);
        d.Y = 0;
        if (d.LengthSquared() < 1e-3f) return false;
        d = Vector3.Normalize(d);
        var forward = _cam.Forward with { Y = 0 };
        forward = forward.LengthSquared() > 1e-4f ? Vector3.Normalize(forward) : Vector3.Zero;
        return _extendSide switch
        {
            ExtendSide.Left => Vector3.Dot(d, _cam.Right) < -0.35f,
            ExtendSide.Right => Vector3.Dot(d, _cam.Right) > 0.35f,
            ExtendSide.Bands => MathF.Abs(Vector3.Dot(d, _cam.Right)) > 0.35f,
            _ => Vector3.Dot(d, forward) > 0.35f,
        };
    }

    /// <summary>One corner's move from the rows meeting there: the same along a straight
    /// edge, the mitre of the two rows at a corner (heights from the plane through both).</summary>
    static Vector3 Mitre(List<Vector3> ds)
    {
        var avg = ds.Aggregate(Vector3.Zero, (a, b) => a + b) / ds.Count;
        if (ds.Count != 2) return avg;
        Vector3 d1 = ds[0], d2 = ds[1];
        Vector2 h1 = new(d1.X, d1.Z), h2 = new(d2.X, d2.Z);
        float l1 = h1.Length(), l2 = h2.Length();
        if (l1 < 1 || l2 < 1) return avg;
        Vector2 n1 = h1 / l1, n2 = h2 / l2;
        if (Vector2.Dot(n1, n2) > 0.95f) return avg;
        float det = n1.X * n2.Y - n1.Y * n2.X;
        if (MathF.Abs(det) < 0.2f) return avg;
        // o . n1 = l1 and o . n2 = l2: where both pushed edges meet.
        var o = new Vector2((l1 * n2.Y - n1.Y * l2) / det, (n1.X * l2 - l1 * n2.X) / det);
        float cap = 2.5f * Math.Max(l1, l2);
        if (o.Length() > cap) o = o / o.Length() * cap;
        float dh = h1.X * h2.Y - h1.Y * h2.X;
        var grad = new Vector2((d1.Y * h2.Y - h1.Y * d2.Y) / dh, (h1.X * d2.Y - d1.Y * h2.X) / dh);
        float ycap = 3 * Math.Max(MathF.Abs(d1.Y), MathF.Abs(d2.Y));
        return new(o.X, Math.Clamp(Vector2.Dot(grad, o), -ycap, ycap), o.Y);
    }

    // ---- Sculpt ----

    void StartSculpt()
    {
        if (_hoverPos.W < 0.5f) { Say("Scolpisci: clicca su una superficie."); return; }
        BeginChange();
        _sculptY0 = _hoverPos.Y;
        _sculptGroup = _doc.NextGroup++;
        _sculptLocked = null;
        if (_sculptLockOriginals && L != null)
        {
            _sculptLocked = [];
            foreach (var m in _drawMeshes)
                if (!m.Backdrop)
                    foreach (var p in m.Pos) _sculptLocked.Add(KeyOf(p));
        }
        _drag = DragMode.Sculpt;
        SculptStep();
    }

    /// <summary>One frame of the held brush: own triangles' corners inside the circle
    /// move in height only, joined corners together, softly toward the edge.</summary>
    void SculptStep()
    {
        if (L == null || _hoverPos.W < 0.5f) return;
        var c = new Vector3(_hoverPos.X, _hoverPos.Y, _hoverPos.Z);
        float radius = _sculptRadius;
        if (_sculptAutos) TakeAutosNear(c, radius * 1.3f);
        var drawn = _drawMeshes.Select(m => m.Name).ToHashSet();
        var pos = new Dictionary<VKey, Vector3>();
        var members = new Dictionary<VKey, List<(int T, int K)>>();
        var links = new Dictionary<VKey, HashSet<VKey>>();
        Span<VKey> keys = stackalloc VKey[3];
        for (int i = 0; i < _doc.Tris.Count; i++)
        {
            var t = _doc.Tris[i];
            if (!drawn.Contains(t.Mesh)) continue;
            if (Flat(t.V[0].Pos - c) >= radius && Flat(t.V[1].Pos - c) >= radius && Flat(t.V[2].Pos - c) >= radius) continue;
            for (int k = 0; k < 3; k++) keys[k] = KeyOf(t.V[k].Pos);
            for (int k = 0; k < 3; k++)
            {
                if (!members.TryGetValue(keys[k], out var mem))
                {
                    members[keys[k]] = mem = [];
                    links[keys[k]] = [];
                    pos[keys[k]] = t.V[k].Pos;
                }
                mem.Add((i, k));
                links[keys[k]].Add(keys[(k + 1) % 3]);
                links[keys[k]].Add(keys[(k + 2) % 3]);
            }
        }
        if (members.Count == 0) return;
        var mode = Shift ? SculptMode.Smooth : _sculptMode;
        if (Ctrl && mode == SculptMode.Raise) mode = SculptMode.Lower;
        else if (Ctrl && mode == SculptMode.Lower) mode = SculptMode.Raise;
        float rate = _sculptStrength * Math.Min(_frameDt, 0.05f);
        var heights = new List<(VKey Key, float Y)>();
        foreach (var (key, p) in pos)
        {
            float d = Flat(p - c);
            if (d >= radius || _sculptLocked?.Contains(key) == true) continue;
            float fall = 1 - d * d / (radius * radius);
            fall *= fall;
            float y = p.Y, blend = Math.Min(1, rate * fall * 10);
            switch (mode)
            {
                case SculptMode.Raise: y += rate * fall * radius * 1.5f; break;
                case SculptMode.Lower: y -= rate * fall * radius * 1.5f; break;
                case SculptMode.Smooth:
                {
                    float sum = 0;
                    int n = 0;
                    foreach (var o in links[key])
                        if (pos.TryGetValue(o, out var q)) { sum += q.Y; n++; }
                    if (n > 0) y += (sum / n - y) * blend;
                    break;
                }
                case SculptMode.Flatten: y += (_sculptY0 - y) * blend; break;
            }
            heights.Add((key, y));
        }
        foreach (var (key, y) in heights)
            foreach (var (ti, k) in members[key]) _doc.Tris[ti].V[k].Pos.Y = y;
        if (heights.Count > 0) Changed();
    }

    /// <summary>Automatic repairs under the brush become own triangles (the automatic one
    /// switched off), so the generated ground can be shaped.</summary>
    void TakeAutosNear(Vector3 c, float radius)
    {
        if (_auto == null) return;
        Func<AutoTri, bool>? off = null;
        bool any = false;
        foreach (var m in _drawMeshes)
        {
            if (m.Backdrop || !AutoDrawn(m) || !_auto.Ranges.TryGetValue(m, out var r)) continue;
            for (int i = r.Start; i < r.Start + r.Count; i++)
            {
                var t = _auto.Tris[i];
                if (t.Poly < 0 || (Flat(t.A.Pos - c) >= radius && Flat(t.B.Pos - c) >= radius && Flat(t.C.Pos - c) >= radius)) continue;
                off ??= RepairOffTest();
                if (off(t) || AsUser(Key(Kind.Auto, i), _sculptGroup) is not { } mine) continue;
                _doc.HiddenRepairs.Add(new(m.Name, t.Poly, t.Center));
                _sel.Remove(Key(Kind.Auto, i));
                _doc.Tris.Add(mine);
                any = true;
            }
        }
        if (any)
        {
            _autoDirty = true;
            Changed();
        }
    }

    // ---- Selection helpers ----

    /// <summary>Adds everything joined to the selection by shared corners: own triangles
    /// with own ones, automatics with automatics of the same mesh, originals within their mesh.</summary>
    void SelectLinked()
    {
        if (L == null || _sel.Count == 0) return;
        var all = SceneGeo(true);
        string ClassOf(Geo g) => KindOf(g.Key) == Kind.User ? "U" : $"{(int)KindOf(g.Key)}:{g.Mesh}";
        var byCorner = new Dictionary<(string, VKey), List<int>>();
        for (int i = 0; i < all.Count; i++)
        {
            if (!Allowed(KindOf(all[i].Key))) continue;
            string cls = ClassOf(all[i]);
            foreach (var p in all[i].P)
            {
                var key = (cls, KeyOf(p));
                if (!byCorner.TryGetValue(key, out var list)) byCorner[key] = list = [];
                list.Add(i);
            }
        }
        var seen = new bool[all.Count];
        var queue = new Queue<int>();
        for (int i = 0; i < all.Count; i++)
            if (_sel.Contains(all[i].Key)) { seen[i] = true; queue.Enqueue(i); }
        int before = _sel.Count;
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            string cls = ClassOf(all[i]);
            foreach (var p in all[i].P)
                if (byCorner.TryGetValue((cls, KeyOf(p)), out var list))
                    foreach (int j in list)
                        if (!seen[j])
                        {
                            seen[j] = true;
                            _sel.Add(all[j].Key);
                            queue.Enqueue(j);
                        }
        }
        Say($"Selezionati i pezzi collegati: +{_sel.Count - before} triangoli ({_sel.Count} in tutto).");
    }

    /// <summary>All automatic repairs of the meshes the selected automatics come from.</summary>
    void SelectMeshAutos()
    {
        if (_auto == null) return;
        var meshes = _sel.Where(k => KindOf(k) == Kind.Auto && Valid(k)).Select(k => _auto.Tris[IndexOf(k)].Mesh).ToHashSet();
        var off = RepairOffTest();
        foreach (var m in meshes)
            if (_auto.Ranges.TryGetValue(m, out var r))
                for (int i = r.Start; i < r.Start + r.Count; i++)
                    if (_showOff || !off(_auto.Tris[i])) _sel.Add(Key(Kind.Auto, i));
        Say($"Tutti gli automatici di {string.Join(", ", meshes.Select(m => m.Name))}: {_sel.Count} selezionati.");
    }

    /// <summary>Box selection through the scene: every triangle whose centre is in the box,
    /// hidden behind others or not.</summary>
    void BoxSelectThrough(Vector2 a, Vector2 b)
    {
        var min = Vector2.Min(a, b);
        var max = Vector2.Max(a, b);
        if (!Shift && !Ctrl && !Alt) _sel.Clear();
        foreach (var g in SceneGeo(true))
        {
            if (!Allowed(KindOf(g.Key))) continue;
            if (!_cam.Project((g.P[0] + g.P[1] + g.P[2]) / 3, _view, out var s)) continue;
            if (s.X < min.X || s.Y < min.Y || s.X > max.X || s.Y > max.Y) continue;
            if (Alt) _sel.Remove(g.Key);
            else _sel.Add(g.Key);
        }
    }

    // ---- Panels and overlay ----

    void SculptPanel()
    {
        ImGui.Indent();
        void Mode(SculptMode m, string label)
        {
            if (ImGui.RadioButton(label, _sculptMode == m)) _sculptMode = m;
        }
        Mode(SculptMode.Raise, "Alza");
        ImGui.SameLine();
        Mode(SculptMode.Lower, "Abbassa");
        ImGui.SameLine();
        Mode(SculptMode.Smooth, "Liscia");
        ImGui.SameLine();
        Mode(SculptMode.Flatten, "Appiattisci");
        ImGui.SetNextItemWidth(150 * S);
        ImGui.SliderFloat("raggio", ref _sculptRadius, 50, 8000, "%.0f", ImGuiSliderFlags.Logarithmic);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Anche Ctrl+rotella sulla vista.");
        ImGui.SetNextItemWidth(150 * S);
        ImGui.SliderFloat("forza", ref _sculptStrength, 0.05f, 2, "%.2f");
        ImGui.Checkbox("Rendi miei gli automatici toccati", ref _sculptAutos);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Gli automatici sotto il pennello si spengono e diventano tuoi triangoli uguali,\ncosì il terreno generato si può modellare.");
        ImGui.Checkbox("Non muovere i vertici sugli originali", ref _sculptLockOriginals);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("I vertici che toccano la scena originale restano fermi:\nil terreno nuovo resta attaccato al bordo senza fessure.");
        ImGui.TextDisabled("Ctrl: al contrario. Shift: liscia.");
        ImGui.Unindent();
    }

    void TerrainSelectionSection()
    {
        ImGui.SeparatorText("Terreno");
        if (ImGui.Button("Estendi il bordo", new Vector2(-1, 0))) ExtendTerrain();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("I bordi liberi della selezione (dove non c'è altro attaccato) continuano con nuove celle,\n"
                + "grandi come quella dietro, texture specchiata (niente righe), altezza che continua.\n"
                + "Funziona da originali, automatici o tuoi: seleziona le celle del bordo (o tutto il pezzo con L).\n"
                + "Prima elimina gli automatici brutti che coprono quel bordo.");
        ImGui.SetNextItemWidth(110 * S);
        ImGui.SliderInt("celle##ext", ref _extendSteps, 1, 30);
        string[] slopes = ["Continua la pendenza", "Pendenza che si spegne", "Piatta"];
        int slope = (int)_extendSlope;
        ImGui.SetNextItemWidth(220 * S);
        if (ImGui.Combo("altezza##ext", ref slope, slopes, slopes.Length)) _extendSlope = (ExtendSlope)slope;
        string[] sides = ["Verso le bande (sinistra e destra)", "Solo verso sinistra", "Solo verso destra", "Solo lontano dalla camera", "Tutti i bordi liberi"];
        int side = (int)_extendSide;
        ImGui.SetNextItemWidth(220 * S);
        if (ImGui.Combo("lato##ext", ref side, sides, sides.Length)) _extendSide = (ExtendSide)side;
        ImGui.Checkbox("Anche da triangoli isolati", ref _extendLone);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Di solito si estendono solo le celle a quad (2 triangoli): i triangoli da soli\nfanno punte e texture stirate. I bordi dei buchi interni non si estendono mai.");
        if (ImGui.Button("Seleziona collegati (L)")) SelectLinked();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Aggiunge tutto ciò che è attaccato alla selezione: tuoi con tuoi,\nautomatici con automatici della stessa mesh, originali nella loro mesh.");
        if (_sel.Any(k => KindOf(k) == Kind.Auto))
        {
            ImGui.SameLine();
            if (ImGui.Button("Automatici della mesh")) SelectMeshAutos();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Tutti gli automatici della stessa mesh, anche lontani (per eliminarli in blocco).");
        }
    }

    void SculptOverlay(ImDrawListPtr dl)
    {
        if (_hoverPos.W < 0.5f) return;
        var c = new Vector3(_hoverPos.X, _hoverPos.Y, _hoverPos.Z);
        const int n = 48;
        var pts = new Vector2[n];
        int got = 0;
        for (int i = 0; i < n; i++)
        {
            float a = i * MathF.Tau / n;
            if (_cam.Project(c + new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * _sculptRadius, _view, out var s)) pts[got++] = s;
        }
        if (got == n) dl.AddPolyline(ref pts[0], n, Col(1, 0.6f, 0.1f, 0.9f), ImDrawFlags.Closed, 2 * _ui);
        if (_cam.Project(c, _view, out var cs)) dl.AddCircleFilled(cs, 3 * _ui, Col(1, 0.6f, 0.1f, 0.9f));
        var mode = Shift ? SculptMode.Smooth : _sculptMode;
        string label = mode switch
        {
            SculptMode.Raise => Ctrl ? "abbassa" : "alza",
            SculptMode.Lower => Ctrl ? "alza" : "abbassa",
            SculptMode.Smooth => "liscia",
            _ => "appiattisci",
        };
        dl.AddText(ImGui.GetIO().MousePos + new Vector2(14, 10), Col(1, 0.7f, 0.2f), label);
    }
}
