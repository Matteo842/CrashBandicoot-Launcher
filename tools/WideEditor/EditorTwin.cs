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
        public (float Reach, bool TexOnly)? AddKey;
        public int OtherMesh;
    }

    sealed class TwinSearch
    {
        public List<TwinTri> Pool = [];
        public Dictionary<(int, int, int), List<int>> Grid = [];
        public List<TwinTri> Sel = [];
        public HashSet<long> SelKeys = [];
        public HashSet<Material> SelMats = [];
        public Vector3 SelMin, SelMax;
        public string Target = "";
        public List<TwinHyp> Hyps = [];
        public int Index;
        public Dictionary<Mesh, Dictionary<VKey, List<int>>> CornerMaps = [];
        // Triangles already in place: originals not deleted and own ones.
        public List<Vector3[]> Existing = [];
        public Dictionary<(int, int, int), List<int>> ExistingGrid = [];
        public int DocVersion;
    }

    TwinSearch? _twin;
    float _twinReach = 1;
    bool _twinTexOnly;

    const float TwinCell = 64, TwinTol = 12;

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

    /// <summary>Looks for copies of the selected piece among the originals of the loaded meshes.</summary>
    void FindTwin()
    {
        _twin = null;
        if (L == null || _sel.Count == 0) { Say("Seleziona prima il pezzo tagliato (clic, poi L per i collegati)."); return; }
        var s = new TwinSearch { SelKeys = [.. _sel], DocVersion = _docVersion };
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

        // The originals of the loaded area (the zone and its neighbours), sky excluded.
        var bySig = new Dictionary<TexSig, List<int>>();
        foreach (var m in _drawMeshes)
        {
            if (m.Backdrop) continue;
            var cornerMap = new Dictionary<VKey, List<int>>();
            s.CornerMaps[m] = cornerMap;
            for (int p = 0; p < m.PolyCount; p++)
            {
                var uv = L.UvsOf(m, p);
                // Originals deleted by hand are neither twins nor in place.
                if (_doc.Removed.Contains((m.Name, p))) continue;
                var t = new TwinTri { M = m, Poly = p, UvF = uv, Uv = [.. uv.Select(UvInt)] };
                for (int k = 0; k < 3; k++) t.P[k] = m.Corner(p, k);
                if (Vector3.Cross(t.P[1] - t.P[0], t.P[2] - t.P[0]).LengthSquared() < 1) continue;
                t.Sig = SigOf(L.MaterialOf(m, p), t.Uv);
                t.C = (t.P[0] + t.P[1] + t.P[2]) / 3;
                int i = s.Pool.Count;
                s.Pool.Add(t);
                GridAdd(s.Grid, t.C, i);
                if (!bySig.TryGetValue(t.Sig, out var list)) bySig[t.Sig] = list = [];
                list.Add(i);
                foreach (var c in t.P)
                {
                    var vk = KeyOf(c);
                    if (!cornerMap.TryGetValue(vk, out var at)) cornerMap[vk] = at = [];
                    at.Add(i);
                }
                GridAdd(s.ExistingGrid, t.C, s.Existing.Count);
                s.Existing.Add(t.P);
            }
        }
        foreach (var u in _doc.Tris)
        {
            if (L.MeshByName.TryGetValue(u.Mesh, out var um) && um.Backdrop) continue;
            GridAdd(s.ExistingGrid, u.Center, s.Existing.Count);
            s.Existing.Add([.. u.V.Select(v => v.Pos)]);
        }

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
            foreach (int ci in bySig[seed.Sig].OrderBy(ci => Vector3.DistanceSquared(s.Pool[ci].C, seed.C)).Take(600))
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
        var near = new List<int>();
        var q = new Vector3[3];
        int Vote(Xf xf, IReadOnlyList<TwinTri> tris, List<int>? matched)
        {
            int votes = 0;
            foreach (var t in tris)
            {
                for (int k = 0; k < 3; k++) q[k] = xf.Apply(t.P[k]);
                GridNear(s.Grid, (q[0] + q[1] + q[2]) / 3, TwinTol, near);
                foreach (int j in near)
                {
                    var c = s.Pool[j];
                    if (c.Sig != t.Sig || !SameCorners(q, c.P, t.Uv, c.Uv)) continue;
                    votes++;
                    matched?.Add(j);
                    break;
                }
            }
            return votes;
        }
        List<TwinTri> sample = s.Sel.Count <= 24 ? s.Sel : [.. Enumerable.Range(0, 24).Select(i => s.Sel[i * s.Sel.Count / 24])];
        var shortlist = hyps.Values.Select(x => (Xf: x, Votes: Vote(x, sample, null)))
            .Where(h => h.Votes >= Math.Max(1, (int)MathF.Ceiling(sample.Count * 0.3f)))
            .OrderByDescending(h => h.Votes).ThenBy(h => h.Xf.Mirror).Take(40).ToList();
        int need = s.Sel.Count == 1 ? 1 : Math.Max(2, (int)MathF.Ceiling(s.Sel.Count * 0.35f));
        var seen = new HashSet<string>();
        foreach (var (xf, _) in shortlist)
        {
            var h = new TwinHyp { ToTwin = xf };
            h.Votes = Vote(xf, s.Sel, h.Matched);
            if (h.Votes < need) continue;
            // The same twin found twice (a symmetric piece fits turned and mirrored): once.
            string id = string.Join(",", h.Matched.Distinct().Order()) + (xf.Mirror ? "m" : "");
            if (!seen.Add(id) || (xf.Mirror && seen.Contains(id[..^1]))) continue;
            s.Hyps.Add(h);
        }
        foreach (var h in s.Hyps) TwinAdditions(s, h);
        s.Hyps = [.. s.Hyps.Where(h => h.Add.Count > 0).OrderByDescending(h => h.Votes).ThenByDescending(h => h.Add.Count)];
        if (s.Hyps.Count == 0)
        {
            Say(hyps.Count == 0 || shortlist.Count == 0
                ? "Nessun gemello nelle mesh caricate: nessun altro modello uguale qui intorno. Avvicina la camera a un'altra copia (o attiva \"tutte le mesh\")."
                : "Ho trovato copie uguali, ma nessuna ha pezzi in più di questo. Prova ad aumentare \"quanto cercare attorno\".");
            return;
        }
        _twin = s;
        TwinSay();
    }

    /// <summary>The twin's whole model (its originals joined to the matched ones, within the
    /// reach) and the triangles of it that are not already at the selected piece.</summary>
    void TwinAdditions(TwinSearch s, TwinHyp h)
    {
        if (h.AddKey is { } done && done.Reach == _twinReach && done.TexOnly == _twinTexOnly) return;
        h.AddKey = (_twinReach, _twinTexOnly);
        h.Piece.Clear();
        h.Add.Clear();
        h.OtherMesh = 0;
        var matched = h.Matched.ToHashSet();
        if (matched.Count == 0) return;
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (int i in matched)
            foreach (var p in s.Pool[i].P) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        var size = s.SelMax - s.SelMin;
        float reach = _twinReach * Math.Max(Math.Max(size.X, size.Y), Math.Max(size.Z, 200));
        min -= new Vector3(reach);
        max += new Vector3(reach);
        bool Inside(Vector3 p) => p.X >= min.X && p.Y >= min.Y && p.Z >= min.Z && p.X <= max.X && p.Y <= max.Y && p.Z <= max.Z;
        // Poly of the selected piece's mesh with the same material: the copy then belongs to
        // that mesh and is drawn wherever it is.
        var target = L!.MeshByName[s.Target];
        var byMat = new Dictionary<Material, int>();
        for (int p = target.PolyCount - 1; p >= 0; p--) byMat[L.MaterialOf(target, p)] = p;
        var back = h.ToTwin.Inverse;
        var near = new List<int>();
        // The twin's model: from the matched triangles through shared corners. A triangle that
        // is already at the piece too (the same floor around both copies) is not taken and
        // the search does not go on through it, so it stays on the model.
        var seen = new HashSet<int>(matched);
        var queue = new Queue<int>(matched);
        var piece = new List<int>(matched);
        while (queue.Count > 0)
        {
            var t = s.Pool[queue.Dequeue()];
            var map = s.CornerMaps[t.M];
            foreach (var c in t.P)
            {
                if (!map.TryGetValue(KeyOf(c), out var list)) continue;
                foreach (int j in list)
                {
                    if (!seen.Add(j)) continue;
                    var u = s.Pool[j];
                    if (!Inside(u.C) || (_twinTexOnly && !s.SelMats.Contains(u.Sig.Mat))) continue;
                    var q = new Vector3[3];
                    for (int k = 0; k < 3; k++) q[k] = back.Apply(u.P[k]);
                    GridNear(s.ExistingGrid, (q[0] + q[1] + q[2]) / 3, TwinTol, near);
                    if (near.Exists(e => SameCorners(q, s.Existing[e]))) continue;
                    piece.Add(j);
                    queue.Enqueue(j);
                    string mesh = u.M.Name;
                    int poly = u.Poly;
                    if (byMat.TryGetValue(u.Sig.Mat, out int tp)) { mesh = target.Name; poly = tp; }
                    else h.OtherMesh++;
                    // A mirrored copy keeps facing the same way as its neighbours.
                    int[] order = h.ToTwin.Mirror ? [0, 2, 1] : [0, 1, 2];
                    h.Add.Add(new UserTri
                    {
                        Mesh = mesh, Poly = poly, Behind = _newBehind,
                        V = [.. order.Select(k => new EdVertex { Pos = q[k], Uv = u.UvF[k], Rgb = u.M.Rgb[u.M.Tri[u.Poly * 3 + k]] })],
                    });
                }
            }
        }
        h.Piece = piece;
    }

    TwinHyp? CurrentTwin()
    {
        if (_twin == null) return null;
        // A new selection or another edit makes the preview stale.
        if (_twin.DocVersion != _docVersion || !_sel.SetEquals(_twin.SelKeys)) { _twin = null; return null; }
        var h = _twin.Hyps[_twin.Index];
        TwinAdditions(_twin, h);
        return h;
    }

    void TwinSay()
    {
        if (CurrentTwin() is not { } h) return;
        Say($"Gemello {_twin!.Index + 1} di {_twin.Hyps.Count}: {h.Votes} triangoli su {_twin.Sel.Count} uguali"
            + $"{(h.ToTwin.Mirror ? " (specchiato)" : "")}, {h.Add.Count} pezzi da aggiungere (in verde).");
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

    void ApplyTwin()
    {
        if (CurrentTwin() is not { } h || h.Add.Count == 0) return;
        BeginChange();
        int group = _doc.NextGroup++;
        _sel.Clear();
        foreach (var t in h.Add)
        {
            var c = t.Clone();
            c.Group = group;
            _sel.Add(Key(Kind.User, _doc.Tris.Count));
            _doc.Tris.Add(c);
        }
        int other = h.OtherMesh;
        _twin = null;
        Changed();
        Say($"Aggiunti {h.Add.Count} pezzi dal gemello (selezionati, gruppo {group})."
            + (other > 0 ? $" {other} hanno una texture che la mesh del pezzo non usa: restano legati alla mesh del gemello." : "")
            + " Luce: \"Illuminazione dagli originali vicini\" se il colore non si intona.");
    }

    void TwinSelectionSection()
    {
        ImGui.SeparatorText("Completa dal gemello");
        if (ImGui.Button("Cerca il gemello (modello uguale)", new Vector2(-1, 0))) FindTwin();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Seleziona il modello tagliato (clic, poi L per i collegati) e premi qui.\n"
                + "Cerca nelle mesh caricate un'altra copia dello stesso modello (stesse texture e forma,\n"
                + "anche girata o specchiata) e mostra in verde i pezzi che a questo mancano.\n"
                + "Sono copie esatte dei triangoli del gemello, rimessi al posto giusto.");
        var h = CurrentTwin();
        if (h == null) return;
        ImGui.TextWrapped($"Gemello {_twin!.Index + 1} di {_twin.Hyps.Count}: {h.Votes}/{_twin.Sel.Count} uguali"
            + $"{(h.ToTwin.Mirror ? ", specchiato" : "")}. Da aggiungere: {h.Add.Count}.");
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
            ImGui.SetTooltip("Fin dove prendere i pezzi del gemello, rispetto alla grandezza del pezzo selezionato.\nSe in verde entra anche il pavimento o un muro vicino, abbassalo.");
        ImGui.Checkbox("Solo le texture del pezzo##twin", ref _twinTexOnly);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Prende dal gemello solo triangoli con texture che il pezzo selezionato già usa.");
        ImGui.BeginDisabled(h.Add.Count == 0);
        if (ImGui.Button($"Aggiungi i {h.Add.Count} pezzi mancanti", new Vector2(-1, 0))) ApplyTwin();
        ImGui.EndDisabled();
        if (ImGui.SmallButton("annulla##twin")) _twin = null;
    }

    /// <summary>Preview: the twin outlined in light blue, the pieces it would add in green.</summary>
    void TwinOverlay(ImDrawListPtr dl)
    {
        if (CurrentTwin() is not { } h) return;
        var s = new Vector2[3];
        int drawn = 0;
        foreach (int i in h.Piece)
        {
            if (++drawn > 6000) break;
            var t = _twin!.Pool[i];
            bool ok = true;
            for (int k = 0; k < 3 && ok; k++) ok = _cam.Project(t.P[k], _view, out s[k]);
            if (ok) dl.AddPolyline(ref s[0], 3, Col(0.3f, 0.8f, 1, 0.7f), ImDrawFlags.Closed, 1.2f * _ui);
        }
        foreach (var t in h.Add)
        {
            if (++drawn > 12000) break;
            bool ok = true;
            for (int k = 0; k < 3 && ok; k++) ok = _cam.Project(t.V[k].Pos, _view, out s[k]);
            if (!ok) continue;
            dl.AddConvexPolyFilled(ref s[0], 3, Col(0.2f, 1, 0.3f, 0.25f));
            dl.AddPolyline(ref s[0], 3, Col(0.2f, 1, 0.3f, 0.95f), ImDrawFlags.Closed, 1.6f * _ui);
        }
    }
}
