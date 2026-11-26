// Completing a cut model from its twin: the scenery is full of copies of the same model
// (totems, pillars, towers, wall blocks) and the copy at the 4:3 edge is the one cut in
// half. The selected piece is searched among the originals of the loaded meshes (same
// texture, same shape, moved, turned or mirrored); the twin found is a whole copy, and its
// triangles the selected piece lacks are copied back in place as own triangles. Exact
// copy and paste, nothing invented: a preview shows what would be added before it is.
using System.Numerics;
using ImGuiNET;

namespace WideEditor;

sealed partial class Editor
{
    /// <summary>What a triangle looks like: material and its three texture corners, in any order.</summary>
    readonly record struct TexSig(Material Mat, long Uv);

    /// <summary>A rigid move (turn + shift, mirrored when <c>Nb</c> is flipped) that lays
    /// triangle A on triangle B: the frame of A's corners onto the frame of B's.</summary>
    readonly record struct Xf(Vector3 A0, Vector3 Ea1, Vector3 Ea2, Vector3 Na, Vector3 B0, Vector3 Eb1, Vector3 Eb2, Vector3 Nb, bool Mirror)
    {
        public Vector3 Apply(Vector3 p)
        {
            var d = p - A0;
            return B0 + Eb1 * Vector3.Dot(Ea1, d) + Eb2 * Vector3.Dot(Ea2, d) + Nb * Vector3.Dot(Na, d);
        }

        public Xf Inverse => new(B0, Eb1, Eb2, Nb, A0, Ea1, Ea2, Na, Mirror);
    }

    sealed class TwinTri
    {
        public required Mesh M;
        public int Poly;
        public Vector3[] P = new Vector3[3];
        public Vector2[] UvF = new Vector2[3];
        public int[] Uv = new int[3];
        public TexSig Sig;
        public Vector3 C;
    }

    sealed class TwinHyp
    {
        public Xf ToTwin;
        public List<int> Matched = [];
        public int Votes;
        public List<int> Piece = [];
        public List<UserTri> Add = [];
        public (float Reach, bool TexOnly, bool Holes)? AddKey;
        public int OtherMesh;
        // Originals around the twin that are also around the piece (same place once laid back):
        // the right copy, turned the right way, brings back the same pillar, wall and floor.
        public int Context;
        // Additions copied from the user's own triangles at the twin (work already done there).
        public HashSet<VKey> Mine = [];
    }

    /// <summary>Every original of the level (sky excluded), built once per level and shared
    /// by all the searches: looking through the whole level then costs no rebuild.</summary>
    sealed class TwinPool
    {
        public required LevelData Level;
        public List<TwinTri> Pool = [];
        public Dictionary<(int, int, int), List<int>> Grid = [];
        public Dictionary<TexSig, List<int>> BySig = [];
        public Dictionary<Mesh, Dictionary<VKey, List<int>>> CornerMaps = [];
        public Dictionary<(string, int), int> Index = [];
    }

    sealed class TwinSearch
    {
        public required LevelData Level;
        public TwinPool P = null!;
        public List<TwinTri> Pool => P.Pool;
        public Dictionary<(int, int, int), List<int>> Grid => P.Grid;
        public Dictionary<Mesh, Dictionary<VKey, List<int>>> CornerMaps => P.CornerMaps;
        // Originals deleted by hand (by pool index): neither twins nor in place.
        public bool[] Gone = [];
        public HashSet<(string, int)> Removed = [];
        public List<TwinTri> Sel = [];
        public HashSet<long> SelKeys = [];
        public HashSet<Material> SelMats = [];
        public Vector3 SelMin, SelMax;
        public string Target = "";
        public List<TwinHyp> Hyps = [];
        public int Index;
        // Own triangles already in place (any, the selection's too), by centre.
        public List<Vector3[]> UserEx = [];
        public Dictionary<(int, int, int), List<int>> UserExGrid = [];
        public int DocVersion;
        public string Message = "";
        // Pieces the user picked, from any twin, by centre: kept when switching twin or reach.
        public Dictionary<VKey, UserTri> Chosen = [];
        public List<int> Hover = [];
        // The user's own triangles (sky excluded, selection excluded): centres and corners.
        public List<UserTri> User = [];
        public Dictionary<(int, int, int), List<int>> UserGrid = [];
        public Dictionary<(int, int, int), List<int>> UserCorners = [];
        // Open sides around the selected corners (nothing on the other side): where the model
        // is missing. Only twin pieces attached to them are offered.
        public List<(Vector3 A, Vector3 B)> Holes = [];
        // No twin had pieces attached to the open sides: everything is offered.
        public bool HolesOff;
        // Faces around the piece (originals and own), in the coarse cells they touch.
        public List<Vector3[]> Surf = [];
        public Dictionary<(int, int, int), List<int>> SurfGrid = [];

        /// <summary>Is a triangle with these corners already at the piece (original or own)?</summary>
        public bool AtPiece(Vector3[] q, List<int> near)
        {
            var c = (q[0] + q[1] + q[2]) / 3;
            GridNear(Grid, c, TwinTol, near);
            foreach (int j in near)
                if (!Gone[j] && SameCorners(q, Pool[j].P)) return true;
            GridNear(UserExGrid, c, TwinTol, near);
            foreach (int e in near)
                if (SameCorners(q, UserEx[e])) return true;
            return false;
        }
    }

    TwinSearch? _twin;
    TwinPool? _twinPool;
    Task<TwinSearch>? _twinJob;
    float _twinReach = 1;
    bool _twinTexOnly;
    bool _twinHoles = true;

    const float TwinCell = 64, TwinTol = 12;

    static float SegDist2(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        float l2 = ab.LengthSquared();
        float t = l2 < 1e-6f ? 0 : Math.Clamp(Vector3.Dot(p - a, ab) / l2, 0, 1);
        return Vector3.DistanceSquared(p, a + ab * t);
    }

    /// <summary>The stretch of side a-b (distance from a) that side c-d lies on, or null.</summary>
    static (float Lo, float Hi)? Covered(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        var ab = b - a;
        float len = ab.Length();
        if (len < 1) return null;
        var u = ab / len;
        float tc = Vector3.Dot(c - a, u), td = Vector3.Dot(d - a, u);
        if ((c - a - u * tc).LengthSquared() > TwinTol * TwinTol || (d - a - u * td).LengthSquared() > TwinTol * TwinTol) return null;
        float lo = Math.Max(0, Math.Min(tc, td)), hi = Math.Min(len, Math.Max(tc, td));
        return hi - lo > TwinTol ? (lo, hi) : null;
    }

    const float SurfCell = 256;

    static (int, int, int) SurfCellOf(Vector3 p) =>
        ((int)MathF.Floor(p.X / SurfCell), (int)MathF.Floor(p.Y / SurfCell), (int)MathF.Floor(p.Z / SurfCell));

    /// <summary>The coarse cells a box (plus the tolerance) touches.</summary>
    static IEnumerable<(int, int, int)> SurfCells(Vector3 min, Vector3 max)
    {
        var lo = SurfCellOf(min - new Vector3(TwinTol));
        var hi = SurfCellOf(max + new Vector3(TwinTol));
        for (int x = lo.Item1; x <= hi.Item1; x++)
            for (int y = lo.Item2; y <= hi.Item2; y++)
                for (int z = lo.Item3; z <= hi.Item3; z++)
                    yield return (x, y, z);
    }

    /// <summary>Puts triangle <paramref name="i"/> in every coarse cell its box touches.</summary>
    static void SurfAdd(Dictionary<(int, int, int), List<int>> grid, Vector3[] q, int i)
    {
        foreach (var cell in SurfCells(Vector3.Min(q[0], Vector3.Min(q[1], q[2])), Vector3.Max(q[0], Vector3.Max(q[1], q[2]))))
        {
            if (!grid.TryGetValue(cell, out var list)) grid[cell] = list = [];
            list.Add(i);
        }
    }

    /// <summary>Does point <paramref name="c"/> lie on triangle <paramref name="t"/> (on its plane, inside it)?</summary>
    static bool PointOnTri(Vector3[] t, Vector3 c)
    {
        var v0 = t[1] - t[0];
        var v1 = t[2] - t[0];
        var n = Vector3.Cross(v0, v1);
        float nl = n.Length();
        if (nl < 1) return false;
        var v2 = c - t[0];
        if (MathF.Abs(Vector3.Dot(v2, n / nl)) > TwinTol) return false;
        float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1);
        float d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);
        float den = d00 * d11 - d01 * d01;
        if (MathF.Abs(den) < 1e-6f) return false;
        float v = (d11 * d20 - d01 * d21) / den, w = (d00 * d21 - d01 * d20) / den;
        return v >= -0.01f && w >= -0.01f && v + w <= 1.01f;
    }

    /// <summary>Do two triangles share (part of) a side?</summary>
    static bool ShareSide(Vector3[] p, Vector3[] q)
    {
        for (int e = 0; e < 3; e++)
            for (int f = 0; f < 3; f++)
                if (Covered(p[e], p[(e + 1) % 3], q[f], q[(f + 1) % 3]) != null
                    || Covered(q[f], q[(f + 1) % 3], p[e], p[(e + 1) % 3]) != null) return true;
        return false;
    }

    /// <summary>Open sides near the selected corners: stretches of triangle sides (originals
    /// and own, any mesh) that no other triangle's side lies on. Sides split differently on the
    /// two faces (T joints) count as closed where they overlap. Also keeps the faces around the
    /// piece (<c>Surf</c>), to leave out copies that would lie on them.</summary>
    static void FindHoles(TwinSearch s)
    {
        var size = s.SelMax - s.SelMin;
        float maxSize = Math.Max(size.X, Math.Max(size.Y, size.Z));
        float r = Math.Max(250, 0.5f * maxSize);
        var corners = s.Sel.SelectMany(t => t.P).ToList();
        // As far as a twin piece can land (the reach slider goes up to 3x).
        var pad = new Vector3(Math.Max(3 * Math.Max(maxSize, 200), 1000) + 768);
        var idx = new List<int>();
        GridBox(s.Grid, s.SelMin - pad, s.SelMax + pad, idx);
        foreach (int j in idx)
            if (!s.Gone[j]) { SurfAdd(s.SurfGrid, s.Pool[j].P, s.Surf.Count); s.Surf.Add(s.Pool[j].P); }
        GridBox(s.UserExGrid, s.SelMin - pad, s.SelMax + pad, idx);
        foreach (int e in idx) { SurfAdd(s.SurfGrid, s.UserEx[e], s.Surf.Count); s.Surf.Add(s.UserEx[e]); }
        var loc = s.Surf;
        float r2 = r * r;
        var spans = new List<(float Lo, float Hi)>();
        var others = new HashSet<int>();
        for (int i = 0; i < loc.Count; i++)
            for (int k = 0; k < 3; k++)
            {
                Vector3 a = loc[i][k], b = loc[i][(k + 1) % 3];
                float len = Vector3.Distance(a, b);
                if (len < 2 * TwinTol || !corners.Any(c => SegDist2(c, a, b) < r2)) continue;
                others.Clear();
                foreach (var cell in SurfCells(Vector3.Min(a, b), Vector3.Max(a, b)))
                    if (s.SurfGrid.TryGetValue(cell, out var list)) others.UnionWith(list);
                spans.Clear();
                foreach (int j in others)
                {
                    // The same triangle twice (an own copy over an original) closes nothing.
                    if (j == i || SameCorners(loc[i], loc[j])) continue;
                    for (int m = 0; m < 3; m++)
                        if (Covered(a, b, loc[j][m], loc[j][(m + 1) % 3]) is { } cv) spans.Add(cv);
                }
                spans.Sort();
                var u = (b - a) / len;
                float at = 0;
                foreach (var (lo, hi) in spans)
                {
                    if (lo - at > 2 * TwinTol) s.Holes.Add((a + u * at, a + u * lo));
                    at = Math.Max(at, hi);
                }
                if (len - at > 2 * TwinTol) s.Holes.Add((a + u * at, b));
            }
    }

    /// <summary>Keeps only the additions attached to an open side near the selection, and
    /// those joined to them side by side (also across T joints): the hole is filled from its
    /// edge outwards. Copies that would lie on a face already there are left out.</summary>
    static void KeepAtHoles(TwinSearch s, TwinHyp h)
    {
        bool OnFace(Vector3 c)
        {
            if (!s.SurfGrid.TryGetValue(SurfCellOf(c), out var list)) return false;
            foreach (int i in list)
                if (PointOnTri(s.Surf[i], c)) return true;
            return false;
        }
        var add = h.Add.Where(t => !OnFace(t.Center)).ToList();
        var pos = add.Select(t => t.V.Select(v => v.Pos).ToArray()).ToList();
        var grid = new Dictionary<(int, int, int), List<int>>();
        for (int i = 0; i < add.Count; i++) SurfAdd(grid, pos[i], i);
        var keep = new bool[add.Count];
        var queue = new Queue<int>();
        for (int i = 0; i < add.Count; i++)
        {
            bool touches = false;
            for (int e = 0; e < 3 && !touches; e++)
            {
                Vector3 a = pos[i][e], b = pos[i][(e + 1) % 3];
                foreach (var (ha, hb) in s.Holes)
                    if (Covered(ha, hb, a, b) != null || Covered(a, b, ha, hb) != null) { touches = true; break; }
            }
            if (touches) { keep[i] = true; queue.Enqueue(i); }
        }
        var near = new HashSet<int>();
        while (queue.Count > 0)
        {
            int a = queue.Dequeue();
            near.Clear();
            var pa = pos[a];
            foreach (var cell in SurfCells(Vector3.Min(pa[0], Vector3.Min(pa[1], pa[2])), Vector3.Max(pa[0], Vector3.Max(pa[1], pa[2]))))
                if (grid.TryGetValue(cell, out var list)) near.UnionWith(list);
            foreach (int j in near)
                if (!keep[j] && ShareSide(pa, pos[j])) { keep[j] = true; queue.Enqueue(j); }
        }
        h.Add = [.. add.Where((_, i) => keep[i])];
        h.Mine.IntersectWith(h.Add.Select(t => KeyOf(t.Center)));
    }

    static int UvInt(Vector2 uv) => ((int)MathF.Round(uv.X) & 255) << 8 | ((int)MathF.Round(uv.Y) & 255);

    static TexSig SigOf(Material mat, int[] uv)
    {
        int a = uv[0], b = uv[1], c = uv[2];
        if (a > b) (a, b) = (b, a);
        if (b > c) (b, c) = (c, b);
        if (a > b) (a, b) = (b, a);
        return new(mat, (long)a << 32 | (long)b << 16 | (uint)c);
    }

    static (int, int, int) TwinCellOf(Vector3 p) =>
        ((int)MathF.Floor(p.X / TwinCell), (int)MathF.Floor(p.Y / TwinCell), (int)MathF.Floor(p.Z / TwinCell));

    static void GridAdd(Dictionary<(int, int, int), List<int>> grid, Vector3 p, int i)
    {
        var c = TwinCellOf(p);
        if (!grid.TryGetValue(c, out var list)) grid[c] = list = [];
        list.Add(i);
    }

    /// <summary>Indices in the cells around a point (within <paramref name="r"/>).</summary>
    static void GridNear(Dictionary<(int, int, int), List<int>> grid, Vector3 p, float r, List<int> into)
    {
        into.Clear();
        var lo = TwinCellOf(p - new Vector3(r));
        var hi = TwinCellOf(p + new Vector3(r));
        for (int x = lo.Item1; x <= hi.Item1; x++)
            for (int y = lo.Item2; y <= hi.Item2; y++)
                for (int z = lo.Item3; z <= hi.Item3; z++)
                    if (grid.TryGetValue((x, y, z), out var list)) into.AddRange(list);
    }

    /// <summary>Indices in the cells that touch a box.</summary>
    static void GridBox(Dictionary<(int, int, int), List<int>> grid, Vector3 min, Vector3 max, List<int> into)
    {
        into.Clear();
        var lo = TwinCellOf(min);
        var hi = TwinCellOf(max);
        for (int x = lo.Item1; x <= hi.Item1; x++)
            for (int y = lo.Item2; y <= hi.Item2; y++)
                for (int z = lo.Item3; z <= hi.Item3; z++)
                    if (grid.TryGetValue((x, y, z), out var list)) into.AddRange(list);
    }

    static bool SameCorners(ReadOnlySpan<Vector3> q, ReadOnlySpan<Vector3> p, int[]? quv = null, int[]? puv = null)
    {
        for (int k = 0; k < 3; k++)
        {
            bool found = false;
            for (int j = 0; j < 3 && !found; j++)
                found = Vector3.DistanceSquared(q[k], p[j]) < TwinTol * TwinTol && (quv == null || puv == null || quv[k] == puv[j]);
            if (!found) return false;
        }
        return true;
    }

    static bool Frame(Vector3 p0, Vector3 p1, Vector3 p2, out Vector3 e1, out Vector3 e2, out Vector3 n)
    {
        e1 = e2 = n = default;
        var a = p1 - p0;
        var c = Vector3.Cross(a, p2 - p0);
        if (a.LengthSquared() < 1 || c.LengthSquared() < 1) return false;
        e1 = Vector3.Normalize(a);
        n = Vector3.Normalize(c);
        e2 = Vector3.Cross(n, e1);
        return true;
    }

    TwinTri? SelTwinTri(long key)
    {
        if (L == null || !Valid(key)) return null;
        Mesh m;
        int poly;
        Vector3[] p;
        Vector2[] uv;
        if (KindOf(key) == Kind.Retail)
        {
            (m, poly) = RetailOf(IndexOf(key));
            p = [m.Corner(poly, 0), m.Corner(poly, 1), m.Corner(poly, 2)];
            uv = L.UvsOf(m, poly);
        }
        else
        {
            if (GeoOf(key) is not { } g || g.Poly < 0 || !L.MeshByName.TryGetValue(g.Mesh, out var gm)) return null;
            m = gm;
            poly = g.Poly;
            p = g.P;
            uv = g.Uv;
        }
        if (m.Backdrop || poly < 0 || poly >= m.PolyCount) return null;
        var t = new TwinTri { M = m, Poly = poly, P = [.. p], UvF = [.. uv], Uv = [.. uv.Select(UvInt)] };
        t.Sig = SigOf(L.MaterialOf(m, poly), t.Uv);
        t.C = (p[0] + p[1] + p[2]) / 3;
        return t;
    }

    static TwinPool BuildTwinPool(LevelData lv)
    {
        var tp = new TwinPool { Level = lv };
        foreach (var m in lv.Meshes)
        {
            if (m.Backdrop) continue;
            var cornerMap = new Dictionary<VKey, List<int>>();
            tp.CornerMaps[m] = cornerMap;
            for (int p = 0; p < m.PolyCount; p++)
            {
                var uv = lv.UvsOf(m, p);
                var t = new TwinTri { M = m, Poly = p, UvF = uv, Uv = [.. uv.Select(UvInt)] };
                for (int k = 0; k < 3; k++) t.P[k] = m.Corner(p, k);
                if (Vector3.Cross(t.P[1] - t.P[0], t.P[2] - t.P[0]).LengthSquared() < 1) continue;
                t.Sig = SigOf(lv.MaterialOf(m, p), t.Uv);
                t.C = (t.P[0] + t.P[1] + t.P[2]) / 3;
                int i = tp.Pool.Count;
                tp.Pool.Add(t);
                tp.Index[(m.Name, p)] = i;
                GridAdd(tp.Grid, t.C, i);
                if (!tp.BySig.TryGetValue(t.Sig, out var list)) tp.BySig[t.Sig] = list = [];
                list.Add(i);
                foreach (var c in t.P)
                {
                    var vk = KeyOf(c);
                    if (!cornerMap.TryGetValue(vk, out var at)) cornerMap[vk] = at = [];
                    at.Add(i);
                }
            }
        }
        return tp;
    }

    /// <summary>Looks for copies of the selected piece in the whole level. The search runs on
    /// its own thread (the editor keeps answering); the result shows up when it is done.</summary>
    void FindTwin()
    {
        if (_twinJob != null) { Say("Sto ancora cercando il gemello, un attimo..."); return; }
        _twin = null;
        if (L == null || _sel.Count == 0) { Say("Seleziona prima il pezzo tagliato (clic, poi L per i collegati)."); return; }
        var s = new TwinSearch { Level = L, SelKeys = [.. _sel], DocVersion = _docVersion, Removed = [.. _doc.Removed] };
        foreach (var key in _sel.OrderBy(k => k))
            if (SelTwinTri(key) is { } t) s.Sel.Add(t);
        if (s.Sel.Count == 0) { Say("Nella selezione non c'è niente con una texture del gioco da cercare."); return; }
        if (s.Sel.Count > 3000) { Say($"Selezione troppo grande ({s.Sel.Count} triangoli): seleziona solo il modello tagliato."); return; }
        s.SelMin = new(float.MaxValue);
        s.SelMax = new(float.MinValue);
        foreach (var t in s.Sel)
            foreach (var p in t.P) { s.SelMin = Vector3.Min(s.SelMin, p); s.SelMax = Vector3.Max(s.SelMax, p); }
        s.SelMats = [.. s.Sel.Select(t => t.Sig.Mat)];
        s.Target = s.Sel.GroupBy(t => t.M.Name).MaxBy(g => g.Count())!.Key;
        // Own triangles copied now: the search thread never reads the document.
        for (int ui = 0; ui < _doc.Tris.Count; ui++)
        {
            var u = _doc.Tris[ui];
            if (L.MeshByName.TryGetValue(u.Mesh, out var um) && um.Backdrop) continue;
            GridAdd(s.UserExGrid, u.Center, s.UserEx.Count);
            s.UserEx.Add([.. u.V.Select(v => v.Pos)]);
            // Own triangles of the selection are the piece itself, not work done at a twin.
            if (s.SelKeys.Contains(Key(Kind.User, ui))) continue;
            int i = s.User.Count;
            s.User.Add(u.Clone());
            GridAdd(s.UserGrid, u.Center, i);
            foreach (var v in u.V) GridAdd(s.UserCorners, v.Pos, i);
        }
        var pool = _twinPool?.Level == L ? _twinPool : null;
        float reach = _twinReach;
        bool texOnly = _twinTexOnly, behind = _newBehind, holes = _twinHoles;
        _twinJob = Task.Run(() => TwinCompute(s, pool, reach, texOnly, behind, holes));
        Say(pool == null ? "Cerco il gemello in tutto il livello (la prima volta ci mette di più)..." : "Cerco il gemello in tutto il livello...");
    }

    /// <summary>Called every frame: takes the finished search, if any.</summary>
    void PollTwinJob()
    {
        if (_twinJob is not { IsCompleted: true } job) return;
        _twinJob = null;
        if (job.IsFaulted) { Say("Ricerca del gemello fallita: " + job.Exception?.InnerException?.Message); return; }
        var s = job.Result;
        if (s.Level != L) return;
        _twinPool = s.P;
        if (s.Hyps.Count == 0) { Say(s.Message); return; }
        if (s.DocVersion != _docVersion || !_sel.SetEquals(s.SelKeys))
        {
            Say("La selezione è cambiata durante la ricerca: premi di nuovo \"Cerca il gemello\".");
            return;
        }
        _twin = s;
        TwinSay();
    }

    /// <summary>The search itself (own thread): reads only the level and the copies in
    /// <paramref name="s"/>.</summary>
    static TwinSearch TwinCompute(TwinSearch s, TwinPool? pool, float reachSetting, bool texOnly, bool behind, bool holes)
    {
        s.P = pool ?? BuildTwinPool(s.Level);
        s.Gone = new bool[s.Pool.Count];
        foreach (var r in s.Removed)
            if (s.P.Index.TryGetValue(r, out int gi)) s.Gone[gi] = true;
        if (holes) FindHoles(s);
        var bySig = s.P.BySig;
        int threads = Math.Max(1, Environment.ProcessorCount / 2);

        // Ways to lay the piece on a copy: from the selected triangles with the rarest look,
        // each one laid on every original that looks the same (corners matched by texture).
        var selCentre = (s.SelMin + s.SelMax) / 2;
        var seeds = s.Sel.Where(t => Vector3.Cross(t.P[1] - t.P[0], t.P[2] - t.P[0]).LengthSquared() >= 1 && bySig.ContainsKey(t.Sig))
            .OrderBy(t => bySig[t.Sig].Count).Take(8).ToList();
        var hyps = new Dictionary<(VKey, VKey, VKey, VKey), Xf>();
        int[][] perms = [[0, 1, 2], [0, 2, 1], [1, 0, 2], [1, 2, 0], [2, 0, 1], [2, 1, 0]];
        VKey Q(Vector3 p) => KeyOf(p / 16);
        foreach (var seed in seeds)
        {
            if (!Frame(seed.P[0], seed.P[1], seed.P[2], out var ea1, out var ea2, out var na)) continue;
            foreach (int ci in bySig[seed.Sig].Where(ci => !s.Gone[ci]).OrderBy(ci => Vector3.DistanceSquared(s.Pool[ci].C, seed.C)).Take(1500))
            {
                var c = s.Pool[ci];
                foreach (var pi in perms)
                {
                    bool ok = true;
                    for (int k = 0; k < 3 && ok; k++)
                        ok = seed.Uv[k] == c.Uv[pi[k]]
                            && MathF.Abs(Vector3.Distance(seed.P[k], seed.P[(k + 1) % 3]) - Vector3.Distance(c.P[pi[k]], c.P[pi[(k + 1) % 3]])) < TwinTol;
                    if (!ok || !Frame(c.P[pi[0]], c.P[pi[1]], c.P[pi[2]], out var eb1, out var eb2, out var nb)) continue;
                    foreach (bool mirror in new[] { false, true })
                    {
                        var xf = new Xf(seed.P[0], ea1, ea2, na, c.P[pi[0]], eb1, eb2, mirror ? -nb : nb, mirror);
                        var r0 = xf.Apply(selCentre);
                        var rx = xf.Apply(selCentre + new Vector3(512, 0, 0));
                        var ry = xf.Apply(selCentre + new Vector3(0, 512, 0));
                        var rz = xf.Apply(selCentre + new Vector3(0, 0, 512));
                        // The piece laid on itself is not a twin.
                        if (Vector3.Distance(r0, selCentre) < 8 && Vector3.Distance(rx, selCentre + new Vector3(512, 0, 0)) < 8
                            && Vector3.Distance(ry, selCentre + new Vector3(0, 512, 0)) < 8) continue;
                        hyps.TryAdd((Q(r0), Q(rx), Q(ry), Q(rz)), xf);
                    }
                }
            }
        }

        // Vote: how many selected triangles each way lays on an original that looks the same.
        int Vote(Xf xf, IReadOnlyList<TwinTri> tris, List<int>? matched)
        {
            var near = new List<int>();
            var q = new Vector3[3];
            int votes = 0;
            foreach (var t in tris)
            {
                for (int k = 0; k < 3; k++) q[k] = xf.Apply(t.P[k]);
                GridNear(s.Grid, (q[0] + q[1] + q[2]) / 3, TwinTol, near);
                foreach (int j in near)
                {
                    var c = s.Pool[j];
                    if (s.Gone[j] || c.Sig != t.Sig || !SameCorners(q, c.P, t.Uv, c.Uv)) continue;
                    votes++;
                    matched?.Add(j);
                    break;
                }
            }
            return votes;
        }
        // Own triangles around where the piece lands on a copy: a copy already fixed by hand
        // is looked at before the others.
        var selSize = s.SelMax - s.SelMin;
        float nearR = 2 * Math.Max(Math.Max(selSize.X, selSize.Y), Math.Max(selSize.Z, 200));
        int UserNear(Xf xf)
        {
            var nearUser = new List<int>();
            GridNear(s.UserGrid, xf.Apply(selCentre), nearR, nearUser);
            return nearUser.Count;
        }
        List<TwinTri> sample = s.Sel.Count <= 24 ? s.Sel : [.. Enumerable.Range(0, 24).Select(i => s.Sel[i * s.Sel.Count / 24])];
        int needSample = Math.Max(1, (int)MathF.Ceiling(sample.Count * 0.3f));
        var shortlist = hyps.Values.AsParallel().WithDegreeOfParallelism(threads)
            .Select(x => (Xf: x, Votes: Vote(x, sample, null)))
            .Where(h => h.Votes >= needSample)
            .Select(h => (h.Xf, h.Votes, Near: UserNear(h.Xf) > 0))
            .ToList()
            .OrderByDescending(h => h.Near).ThenByDescending(h => h.Votes).ThenBy(h => h.Xf.Mirror).Take(150).ToList();
        int need = s.Sel.Count == 1 ? 1 : Math.Max(2, (int)MathF.Ceiling(s.Sel.Count * 0.35f));
        foreach (var (xf, _, _) in shortlist)
        {
            var h = new TwinHyp { ToTwin = xf };
            h.Votes = Vote(xf, s.Sel, h.Matched);
            if (h.Votes >= need) s.Hyps.Add(h);
        }
        Parallel.ForEach(s.Hyps, new ParallelOptions { MaxDegreeOfParallelism = threads },
            h => TwinAdditions(s, h, reachSetting, texOnly, behind, holes));
        // Nothing attached to the open sides anywhere: offer the twins' whole models instead.
        if (holes && s.Hyps.Count > 0 && s.Hyps.All(h => h.Add.Count == 0))
        {
            s.HolesOff = true;
            foreach (var h in s.Hyps) h.AddKey = null;
            Parallel.ForEach(s.Hyps, new ParallelOptions { MaxDegreeOfParallelism = threads },
                h => TwinAdditions(s, h, reachSetting, texOnly, behind, holes));
        }
        // Best first: the copy whose surroundings agree most with the piece's (a small piece
        // fits many copies, turned any way, and only the right one has the same neighbours);
        // among the good ones, a copy already fixed by hand comes first.
        int best = s.Hyps.Count == 0 ? 0 : s.Hyps.Max(h => h.Votes + h.Context);
        var ordered = s.Hyps.Where(h => h.Add.Count > 0)
            .OrderByDescending(h => h.Mine.Count > 0 && h.Votes + h.Context >= best * 0.8f)
            .ThenByDescending(h => h.Votes + h.Context).ThenByDescending(h => h.Mine.Count).ThenByDescending(h => h.Add.Count).ThenBy(h => h.ToTwin.Mirror);
        // The same twin found twice (a symmetric piece fits turned and mirrored): the better one.
        var seen = new HashSet<string>();
        s.Hyps = [.. ordered.Where(h => seen.Add(string.Join(",", h.Matched.Distinct().Order())))];
        if (s.Hyps.Count == 0)
            s.Message = hyps.Count == 0 || shortlist.Count == 0
                ? "Nessun gemello in tutto il livello: nessun altro modello uguale."
                : "Ho trovato copie uguali, ma nessuna ha pezzi in più di questo. Prova ad aumentare \"quanto cercare attorno\".";
        return s;
    }

    static Material MatOf(LevelData lv, UserTri t) =>
        lv.MeshByName.TryGetValue(t.Mesh, out var m) && t.Poly >= 0 && t.Poly < m.PolyCount ? lv.MaterialOf(m, t.Poly) : default;

    /// <summary>The twin's whole model (its originals joined to the matched ones, within the
    /// reach) and the triangles of it that are not already at the selected piece. Reads only
    /// the level and <paramref name="s"/>: safe on the search threads.</summary>
    static void TwinAdditions(TwinSearch s, TwinHyp h, float reachSetting, bool texOnly, bool behind, bool holes)
    {
        holes = holes && !s.HolesOff && s.Holes.Count > 0;
        if (h.AddKey is { } done && done.Reach == reachSetting && done.TexOnly == texOnly && done.Holes == holes) return;
        h.AddKey = (reachSetting, texOnly, holes);
        h.Piece.Clear();
        h.Add.Clear();
        h.Mine.Clear();
        h.OtherMesh = 0;
        var matched = h.Matched.ToHashSet();
        if (matched.Count == 0) return;
        var lv = s.Level;
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (int i in matched)
            foreach (var p in s.Pool[i].P) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        var size = s.SelMax - s.SelMin;
        float reach = reachSetting * Math.Max(Math.Max(size.X, size.Y), Math.Max(size.Z, 200));
        min -= new Vector3(reach);
        max += new Vector3(reach);
        bool Inside(Vector3 p) => p.X >= min.X && p.Y >= min.Y && p.Z >= min.Z && p.X <= max.X && p.Y <= max.Y && p.Z <= max.Z;
        // Poly of the selected piece's mesh with the same material: the copy then belongs to
        // that mesh and is drawn wherever it is.
        var target = lv.MeshByName[s.Target];
        var byMat = new Dictionary<Material, int>();
        for (int p = target.PolyCount - 1; p >= 0; p--) byMat[lv.MaterialOf(target, p)] = p;
        var back = h.ToTwin.Inverse;
        var near = new List<int>();
        h.Context = 0;
        var ctx = new List<int>();
        GridBox(s.Grid, min, max, ctx);
        var cq = new Vector3[3];
        foreach (int j in ctx)
        {
            var u = s.Pool[j];
            if (s.Gone[j] || matched.Contains(j) || !Inside(u.C)) continue;
            for (int k = 0; k < 3; k++) cq[k] = back.Apply(u.P[k]);
            if (s.AtPiece(cq, near)) h.Context++;
        }
        // The twin's model: from the matched triangles through shared corners. A triangle that
        // is already at the piece too (the same floor around both copies) is not taken and
        // the search does not go on through it, so it stays on the model.
        var seen = new HashSet<int>(matched);
        var queue = new Queue<int>(matched);
        var piece = new List<int>(matched);
        // Takes original j of the twin if it is not already at the piece.
        bool Take(int j)
        {
            var u = s.Pool[j];
            if (texOnly && !s.SelMats.Contains(u.Sig.Mat)) return false;
            var q = new Vector3[3];
            for (int k = 0; k < 3; k++) q[k] = back.Apply(u.P[k]);
            if (s.AtPiece(q, near)) return false;
            piece.Add(j);
            string mesh = u.M.Name;
            int poly = u.Poly;
            if (byMat.TryGetValue(u.Sig.Mat, out int tp)) { mesh = target.Name; poly = tp; }
            else h.OtherMesh++;
            // A mirrored copy keeps facing the same way as its neighbours.
            int[] order = h.ToTwin.Mirror ? [0, 2, 1] : [0, 1, 2];
            h.Add.Add(new UserTri
            {
                Mesh = mesh, Poly = poly, Behind = behind,
                V = [.. order.Select(k => new EdVertex { Pos = q[k], Uv = u.UvF[k], Rgb = u.M.Rgb[u.M.Tri[u.Poly * 3 + k]] })],
            });
            return true;
        }
        if (holes)
        {
            // Filling the holes around the selection: every original around the twin, of any
            // mesh and joined or not (the missing wall may be another piece); KeepAtHoles then
            // keeps only what grows from the open sides.
            float far = Math.Max(reach, 1000);
            Vector3 bmin = min + new Vector3(reach - far), bmax = max - new Vector3(reach - far);
            var box = new List<int>();
            GridBox(s.Grid, bmin, bmax, box);
            foreach (int j in box)
            {
                var c = s.Pool[j].C;
                if (s.Gone[j] || matched.Contains(j) || !seen.Add(j)) continue;
                if (c.X < bmin.X || c.Y < bmin.Y || c.Z < bmin.Z || c.X > bmax.X || c.Y > bmax.Y || c.Z > bmax.Z) continue;
                Take(j);
            }
        }
        else
            while (queue.Count > 0)
            {
                var t = s.Pool[queue.Dequeue()];
                var map = s.CornerMaps[t.M];
                foreach (var c in t.P)
                {
                    if (!map.TryGetValue(KeyOf(c), out var list)) continue;
                    foreach (int j in list)
                    {
                        if (!seen.Add(j) || s.Gone[j] || !Inside(s.Pool[j].C)) continue;
                        if (Take(j)) queue.Enqueue(j);
                    }
                }
            }
        h.Piece = piece;

        // Work already done by hand at the twin: own triangles touching the twin's model or in
        // its box, and those joined to them, within three times the reach. Copied back with
        // their light, depth and rank as they are.
        var umin = min - new Vector3(2 * reach);
        var umax = max + new Vector3(2 * reach);
        bool UInside(Vector3 p) => p.X >= umin.X && p.Y >= umin.Y && p.Z >= umin.Z && p.X <= umax.X && p.Y <= umax.Y && p.Z <= umax.Z;
        var useen = new HashSet<int>();
        var uqueue = new Queue<int>();
        var unear = new List<int>();
        void Touching(Vector3 c)
        {
            GridNear(s.UserCorners, c, TwinTol, unear);
            foreach (int j in unear)
                if (!useen.Contains(j) && UInside(s.User[j].Center) && s.User[j].V.Any(v => Vector3.DistanceSquared(v.Pos, c) < TwinTol * TwinTol))
                { useen.Add(j); uqueue.Enqueue(j); }
        }
        foreach (int i in piece)
            foreach (var c in s.Pool[i].P) Touching(c);
        GridBox(s.UserGrid, min, max, unear);
        foreach (int j in unear.ToList())
            if (Inside(s.User[j].Center) && useen.Add(j)) uqueue.Enqueue(j);
        while (uqueue.Count > 0)
        {
            var u = s.User[uqueue.Dequeue()];
            foreach (var v in u.V) Touching(v.Pos);
            var mat = MatOf(lv, u);
            if (texOnly && !s.SelMats.Contains(mat)) continue;
            var q = new Vector3[3];
            for (int k = 0; k < 3; k++) q[k] = back.Apply(u.V[k].Pos);
            if (s.AtPiece(q, near)) continue;
            var c = u.Clone();
            c.Core = false;
            c.Group = 0;
            if (byMat.TryGetValue(mat, out int tp)) { c.Mesh = target.Name; c.Poly = tp; }
            else h.OtherMesh++;
            int[] order = h.ToTwin.Mirror ? [0, 2, 1] : [0, 1, 2];
            c.V = [.. order.Select(k => u.V[k] with { Pos = q[k] })];
            h.Add.Add(c);
            h.Mine.Add(KeyOf(c.Center));
        }
        if (holes)
        {
            KeepAtHoles(s, h);
            // The blue outline: the matched part only (the box holds far more than the model).
            h.Piece = [.. matched];
        }
    }

    TwinHyp? CurrentTwin()
    {
        if (_twin == null) return null;
        // A new selection or another edit makes the preview stale.
        if (_twin.DocVersion != _docVersion || !_sel.SetEquals(_twin.SelKeys)) { _twin = null; return null; }
        var h = _twin.Hyps[_twin.Index];
        TwinAdditions(_twin, h, _twinReach, _twinTexOnly, _newBehind, _twinHoles);
        return h;
    }

    void TwinSay()
    {
        if (CurrentTwin() is not { } h) return;
        Say($"Gemello {_twin!.Index + 1} di {_twin.Hyps.Count}: {h.Votes} triangoli su {_twin.Sel.Count} uguali"
            + $"{(h.ToTwin.Mirror ? " (specchiato)" : "")}, {h.Add.Count} pezzi possibili"
            + (h.Mine.Count > 0 ? $", {h.Mine.Count} già fatti da te là (gialli)" : "") + HolesNote() + ". Clic sui pezzi verdi o gialli per sceglierli.");
    }

    string HolesNote()
    {
        if (!_twinHoles || _twin == null) return "";
        if (_twin.Holes.Count == 0) return " (nessun bordo aperto attorno alla selezione: mostro tutto)";
        if (_twin.HolesOff) return $" (niente attaccato ai {_twin.Holes.Count} bordi aperti: mostro tutto)";
        return $", solo quelli attaccati ai {_twin.Holes.Count} bordi aperti (arancio)";
    }

    void TwinStep(int d)
    {
        if (_twin == null) return;
        _twin.Index = (_twin.Index + d + _twin.Hyps.Count) % _twin.Hyps.Count;
        TwinSay();
    }

    void FrameTwin()
    {
        if (CurrentTwin() is not { } h || h.Piece.Count == 0) return;
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (int i in h.Piece)
            foreach (var p in _twin!.Pool[i].P) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        _cam.Pos = (min + max) / 2 - _cam.Forward * Math.Max(Vector3.Distance(min, max) * 1.2f, 500);
        StopFollow();
    }

    /// <summary>The twin preview is open: clicks only choose its green pieces.</summary>
    bool TwinPicking => _tool == Tool.Select && CurrentTwin() != null;

    /// <summary>Pieces that can be clicked: the current twin's ones and those already chosen.</summary>
    List<(VKey Key, UserTri T)> TwinCandidates()
    {
        var list = new List<(VKey, UserTri)>();
        if (CurrentTwin() is not { } h) return list;
        var seen = new HashSet<VKey>();
        foreach (var t in h.Add)
        {
            var k = KeyOf(t.Center);
            if (seen.Add(k)) list.Add((k, t));
        }
        foreach (var (k, t) in _twin!.Chosen)
            if (seen.Add(k)) list.Add((k, t));
        return list;
    }

    /// <summary>The nearest green triangle under the mouse (-1: none).</summary>
    int TwinPickAt(List<(VKey Key, UserTri T)> list, Vector2 mouse)
    {
        int best = -1;
        float bestDepth = float.MaxValue;
        Span<Vector2> s = stackalloc Vector2[3];
        for (int i = 0; i < list.Count; i++)
        {
            var t = list[i].T;
            bool ok = true;
            for (int k = 0; k < 3 && ok; k++) ok = _cam.Depth(t.V[k].Pos) > 1 && _cam.Project(t.V[k].Pos, _view, out s[k]);
            if (!ok) continue;
            float c0 = Cross2(s[1] - s[0], mouse - s[0]), c1 = Cross2(s[2] - s[1], mouse - s[1]), c2 = Cross2(s[0] - s[2], mouse - s[2]);
            if (!((c0 >= 0 && c1 >= 0 && c2 >= 0) || (c0 <= 0 && c1 <= 0 && c2 <= 0))) continue;
            float depth = _cam.Depth(t.Center);
            if (depth < bestDepth) { bestDepth = depth; best = i; }
        }
        return best;
    }

    static float Cross2(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    Material TwinMat(UserTri t) => L!.MeshByName.TryGetValue(t.Mesh, out var m) && t.Poly < m.PolyCount ? L.MaterialOf(m, t.Poly) : default;

    static int SharedCorners(UserTri a, UserTri b) =>
        a.V.Count(v => b.V.Any(w => Vector3.DistanceSquared(v.Pos, w.Pos) < 4));

    /// <summary>What one click takes: the triangle and the other half of its square (shared
    /// side, same texture, same surface); <paramref name="piece"/> (double click, Alt): every
    /// triangle joined to it by a side with the same texture.</summary>
    List<int> TwinSquare(List<(VKey Key, UserTri T)> list, int i, bool piece)
    {
        var cell = new List<int> { i };
        var mat = TwinMat(list[i].T);
        if (!piece)
        {
            var a = list[i].T;
            var n = Vector3.Cross(a.V[1].Pos - a.V[0].Pos, a.V[2].Pos - a.V[0].Pos);
            int partner = -1;
            float best = 0.95f;
            for (int j = 0; j < list.Count; j++)
            {
                if (j == i || SharedCorners(a, list[j].T) < 2 || TwinMat(list[j].T) != mat) continue;
                var t = list[j].T;
                var m = Vector3.Cross(t.V[1].Pos - t.V[0].Pos, t.V[2].Pos - t.V[0].Pos);
                if (n.LengthSquared() < 1e-6f || m.LengthSquared() < 1e-6f) continue;
                float dot = MathF.Abs(Vector3.Dot(Vector3.Normalize(n), Vector3.Normalize(m)));
                if (dot > best) { best = dot; partner = j; }
            }
            if (partner >= 0) cell.Add(partner);
            return cell;
        }
        var seen = new HashSet<int> { i };
        var queue = new Queue<int>(cell);
        while (queue.Count > 0)
        {
            int a = queue.Dequeue();
            for (int j = 0; j < list.Count; j++)
                if (!seen.Contains(j) && SharedCorners(list[a].T, list[j].T) >= 2 && TwinMat(list[j].T) == mat)
                {
                    seen.Add(j);
                    cell.Add(j);
                    queue.Enqueue(j);
                }
        }
        return cell;
    }

    List<(VKey Key, UserTri T)>? _twinHoverList;

    void TwinHover(Vector2 mouse)
    {
        if (_twin == null) return;
        var list = TwinCandidates();
        int i = TwinPickAt(list, mouse);
        _twin.Hover = i < 0 ? [] : TwinSquare(list, i, Alt);
        _twinHoverList = list;
    }

    /// <summary>Click on a green piece: chosen if it was not, left out if it was.</summary>
    void TwinClick(Vector2 mouse, bool piece)
    {
        if (_twin == null) return;
        var list = TwinCandidates();
        int i = TwinPickAt(list, mouse);
        if (i < 0) return;
        var cell = TwinSquare(list, i, piece);
        bool take = !_twin.Chosen.ContainsKey(list[i].Key);
        foreach (int j in cell)
            if (take) _twin.Chosen[list[j].Key] = list[j].T.Clone();
            else _twin.Chosen.Remove(list[j].Key);
        Say($"{(take ? "Scelti" : "Tolti")} {cell.Count} triangoli. Scelti in tutto: {_twin.Chosen.Count}. Invio = aggiungi, Esc = chiudi.");
    }

    /// <summary>Rectangle over the green pieces: chooses those inside (Alt: leaves them out).</summary>
    void TwinBox(Vector2 a, Vector2 b)
    {
        if (_twin == null) return;
        var min = Vector2.Min(a, b);
        var max = Vector2.Max(a, b);
        int n = 0;
        foreach (var (k, t) in TwinCandidates())
        {
            if (_cam.Depth(t.Center) <= 1 || !_cam.Project(t.Center, _view, out var c)) continue;
            if (c.X < min.X || c.Y < min.Y || c.X > max.X || c.Y > max.Y) continue;
            if (Alt) _twin.Chosen.Remove(k); else _twin.Chosen[k] = t.Clone();
            n++;
        }
        Say($"{(Alt ? "Tolti" : "Scelti")} {n} triangoli col rettangolo. Scelti in tutto: {_twin.Chosen.Count}.");
    }

    void TwinChooseAll(bool on)
    {
        if (_twin == null) return;
        if (!on) { _twin.Chosen.Clear(); return; }
        foreach (var (k, t) in TwinCandidates()) _twin.Chosen[k] = t.Clone();
    }

    /// <summary>Adds the chosen pieces (from every twin looked at) as own triangles.</summary>
    void ApplyTwin()
    {
        if (CurrentTwin() == null || _twin!.Chosen.Count == 0)
        {
            Say("Nessun pezzo scelto: clic sui pezzi verdi (o rettangolo) per sceglierli.");
            return;
        }
        BeginChange();
        int group = _doc.NextGroup++;
        _sel.Clear();
        foreach (var t in _twin.Chosen.Values)
        {
            var c = t.Clone();
            c.Group = group;
            _sel.Add(Key(Kind.User, _doc.Tris.Count));
            _doc.Tris.Add(c);
        }
        int count = _twin.Chosen.Count;
        int other = _twin.Chosen.Values.Count(t => t.Mesh != _twin.Target);
        _twin = null;
        Changed();
        Say($"Aggiunti {count} triangoli dal gemello (selezionati, gruppo {group})."
            + (other > 0 ? $" {other} hanno una texture che la mesh del pezzo non usa: restano legati alla mesh del gemello." : "")
            + " Luce: \"Illuminazione dagli originali vicini\" se il colore non si intona.");
    }

    void TwinSelectionSection()
    {
        ImGui.SeparatorText("Completa dal gemello");
        if (ImGui.Button("Cerca il gemello (modello uguale)", new Vector2(-1, 0))) FindTwin();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Seleziona il modello tagliato (clic, poi L per i collegati) e premi qui.\n"
                + "Cerca in tutto il livello un'altra copia dello stesso modello (stesse texture e forma,\n"
                + "anche girata o specchiata) e mostra in verde i pezzi che a questo mancano.\n"
                + "Se un gemello l'hai già sistemato a mano viene per primo: i tuoi triangoli di là\n"
                + "sono in giallo, da scegliere col mouse come i verdi.\n"
                + "Poi scegli col mouse quali pezzi verdi vuoi: si aggiungono solo quelli.");
        var h = CurrentTwin();
        if (h == null) return;
        ImGui.TextWrapped($"Gemello {_twin!.Index + 1} di {_twin.Hyps.Count}: {h.Votes}/{_twin.Sel.Count} uguali"
            + $"{(h.ToTwin.Mirror ? ", specchiato" : "")}. Pezzi possibili: {h.Add.Count}"
            + (h.Mine.Count > 0 ? $", di cui {h.Mine.Count} già fatti da te (gialli)." : "."));
        if (ImGui.Button("<##twin")) TwinStep(-1);
        ImGui.SameLine();
        if (ImGui.Button(">##twin")) TwinStep(1);
        ImGui.SameLine();
        if (ImGui.Button("Vai al gemello")) FrameTwin();
        ImGui.SameLine();
        if (ImGui.Button("Torna al pezzo")) FrameSelection();
        ImGui.SetNextItemWidth(120 * S);
        ImGui.SliderFloat("quanto cercare attorno##twin", ref _twinReach, 0.1f, 3, "%.1f x");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Fin dove prendere i pezzi del gemello, rispetto alla grandezza del pezzo selezionato.");
        ImGui.Checkbox("Solo dove manca, attorno alla selezione##twin", ref _twinHoles);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Cerca i bordi aperti (in arancio) attorno ai vertici selezionati e propone solo i pezzi\n"
                + "del gemello attaccati a quei bordi, e quelli attaccati a loro: riempie il buco dal bordo.\n"
                + "Cambiandolo vale per la prossima ricerca anche per l'ordine dei gemelli.");
        ImGui.Checkbox("Solo le texture del pezzo##twin", ref _twinTexOnly);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Prende dal gemello solo triangoli con texture che il pezzo selezionato già usa.");
        ImGui.TextWrapped("Clic su un pezzo verde = sceglilo / toglilo (il quadrato intero). Doppio clic o Alt = tutto il pezzo "
            + "con quella texture. Rettangolo = scegli quelli dentro, Alt+rettangolo = togli. Si possono cliccare solo i pezzi verdi.");
        if (ImGui.SmallButton("scegli tutti##twin")) TwinChooseAll(true);
        ImGui.SameLine();
        if (ImGui.SmallButton("nessuno##twin")) TwinChooseAll(false);
        ImGui.BeginDisabled(_twin.Chosen.Count == 0);
        if (ImGui.Button($"Aggiungi i {_twin.Chosen.Count} scelti (Invio)", new Vector2(-1, 0))) ApplyTwin();
        ImGui.EndDisabled();
        if (ImGui.SmallButton("chiudi (Esc)##twin")) _twin = null;
    }

    /// <summary>Preview: the twin outlined in light blue, the pieces it could add in pale
    /// green (yellow: copied from own work at the twin), the chosen ones in full colour, the
    /// piece under the mouse in white.</summary>
    void TwinOverlay(ImDrawListPtr dl)
    {
        PollTwinJob();
        if (CurrentTwin() is not { } h) return;
        var s = new Vector2[3];
        int drawn = 0;
        if (_twinHoles && !_twin!.HolesOff)
            foreach (var (a, b) in _twin.Holes)
                if (_cam.Depth(a) > 1 && _cam.Depth(b) > 1 && _cam.Project(a, _view, out var sa) && _cam.Project(b, _view, out var sb))
                    dl.AddLine(sa, sb, Col(1, 0.45f, 0.05f, 0.95f), 3f * _ui);
        bool Proj(UserTri t)
        {
            for (int k = 0; k < 3; k++)
                if (_cam.Depth(t.V[k].Pos) <= 1 || !_cam.Project(t.V[k].Pos, _view, out s[k])) return false;
            return true;
        }
        foreach (int i in h.Piece)
        {
            if (++drawn > 6000) break;
            var t = _twin!.Pool[i];
            bool ok = true;
            for (int k = 0; k < 3 && ok; k++) ok = _cam.Project(t.P[k], _view, out s[k]);
            if (ok) dl.AddPolyline(ref s[0], 3, Col(0.3f, 0.8f, 1, 0.35f), ImDrawFlags.Closed, 1f * _ui);
        }
        foreach (var t in h.Add)
        {
            if (++drawn > 12000) break;
            if (_twin!.Chosen.ContainsKey(KeyOf(t.Center)) || !Proj(t)) continue;
            bool mine = h.Mine.Contains(KeyOf(t.Center));
            dl.AddConvexPolyFilled(ref s[0], 3, mine ? Col(1, 0.85f, 0.1f, 0.10f) : Col(0.2f, 1, 0.3f, 0.10f));
            dl.AddPolyline(ref s[0], 3, mine ? Col(1, 0.85f, 0.1f, 0.55f) : Col(0.2f, 1, 0.3f, 0.55f), ImDrawFlags.Closed, 1.2f * _ui);
        }
        foreach (var t in _twin!.Chosen.Values)
        {
            if (!Proj(t)) continue;
            bool mine = h.Mine.Contains(KeyOf(t.Center));
            dl.AddConvexPolyFilled(ref s[0], 3, mine ? Col(1, 0.8f, 0.1f, 0.5f) : Col(0.15f, 0.95f, 0.25f, 0.55f));
            dl.AddPolyline(ref s[0], 3, mine ? Col(1, 0.95f, 0.5f, 1) : Col(0.6f, 1, 0.6f, 1), ImDrawFlags.Closed, 2f * _ui);
        }
        if (_twinHoverList != null && _view.Contains(ImGui.GetIO().MousePos))
            foreach (int i in _twin.Hover)
                if (i < _twinHoverList.Count && Proj(_twinHoverList[i].T))
                {
                    dl.AddConvexPolyFilled(ref s[0], 3, Col(1, 1, 1, 0.18f));
                    dl.AddPolyline(ref s[0], 3, Col(1, 1, 1, 1), ImDrawFlags.Closed, 2.5f * _ui);
                }
    }
}
