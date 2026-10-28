// Bridges: a strip of new ground between two edges, joined to the vertices of both. The
// edges come from two pieces (piece A marked, piece B selected: the edges that face each
// other) or are clicked vertex by vertex with the bridge tool. The strip is a grid whose
// columns pass through every vertex of both edges, heights following both slopes, the
// texture tile mirrored cell by cell (no seams, never outside the tile).
using System.Numerics;
using ImGuiNET;

namespace WideEditor;

sealed partial class Editor
{
    List<Geo>? _bridgeA;
    readonly List<Vector3>[] _bridgePts = [[], []];
    int _bridgeSide;
    bool _bridgeCurve = true;
    int _bridgeAlong, _bridgeAcross;

    /// <summary>Corner key with a few units of tolerance (automatic pieces are not always
    /// joined to the unit).</summary>
    static VKey LooseKeyOf(Vector3 p) => new((int)MathF.Round(p.X / 4), (int)MathF.Round(p.Y / 4), (int)MathF.Round(p.Z / 4));

    /// <summary>One edge of the bridge: its corners in order, with colour and the ground
    /// slope going out of its piece (NaN where no triangle borders it).</summary>
    sealed class Chain
    {
        public List<Vector3> P = [];
        public List<Vector3> Rgb = [];
        public List<float> Slope = [];
        public float[] Cum = [];
        public Geo? Tex;

        public float Length => Cum.Length > 0 ? Cum[^1] : 0;

        public void Measure()
        {
            Cum = new float[P.Count];
            for (int i = 1; i < P.Count; i++) Cum[i] = Cum[i - 1] + Vector3.Distance(P[i - 1], P[i]);
        }

        public float S(int i) => Length > 0 ? Cum[i] / Length : 0;

        /// <summary>Point, colour and slope at a fraction of the length.</summary>
        public (Vector3 Pos, Vector3 Rgb, float Slope) At(float s)
        {
            float d = Math.Clamp(s, 0, 1) * Length;
            int i = 0;
            while (i < P.Count - 2 && Cum[i + 1] < d) i++;
            float seg = Cum[i + 1] - Cum[i], f = seg > 1e-4f ? Math.Clamp((d - Cum[i]) / seg, 0, 1) : 0;
            float a = Slope[i], b = Slope[i + 1];
            float slope = float.IsNaN(a) ? b : float.IsNaN(b) ? a : a + (b - a) * f;
            return (Vector3.Lerp(P[i], P[i + 1], f), Vector3.Lerp(Rgb[i], Rgb[i + 1], f), slope);
        }
    }

    /// <summary>A copy of one triangle of any kind, independent of later edits.</summary>
    Geo? GeoOf(long key)
    {
        if (L == null || _auto == null || !Valid(key)) return null;
        int i = IndexOf(key);
        switch (KindOf(key))
        {
            case Kind.Retail:
            {
                var (m, p) = RetailOf(i);
                if (m.Backdrop) return null;
                var (tp, uv) = RetailTexture(m, p);
                var g = new Geo { Key = key, Mesh = m.Name, Poly = tp, Uv = uv, Behind = _newBehind };
                for (int k = 0; k < 3; k++)
                {
                    g.P[k] = m.Corner(p, k);
                    g.Rgb[k] = m.Rgb[m.Tri[p * 3 + k]];
                }
                return g;
            }
            case Kind.Auto:
            {
                var t = _auto.Tris[i];
                if (t.Mesh.Backdrop) return null;
                return new Geo
                {
                    Key = key, Mesh = t.Mesh.Name, Poly = t.Poly < t.Mesh.PolyCount ? t.Poly : -1,
                    P = [t.A.Pos, t.B.Pos, t.C.Pos], Uv = [t.A.Uv, t.B.Uv, t.C.Uv], Rgb = [t.A.Rgb, t.B.Rgb, t.C.Rgb],
                    Behind = t.Behind, Culled = t.Culled, Rank = t.Rank,
                };
            }
            default:
            {
                var t = _doc.Tris[i];
                return new Geo
                {
                    Key = key, Mesh = t.Mesh, Poly = t.Poly,
                    P = [.. t.V.Select(v => v.Pos)], Uv = [.. t.V.Select(v => v.Uv)], Rgb = [.. t.V.Select(v => v.Rgb)],
                    Behind = t.Behind, Culled = t.Culled, Rank = t.Rank,
                };
            }
        }
    }

    List<Geo> SelectionGeo() => [.. _sel.OrderBy(k => k).Select(GeoOf).OfType<Geo>()];

    void MarkBridgeA()
    {
        var geo = SelectionGeo();
        if (geo.Count == 0) { Say("Seleziona prima il pezzo A (originali, automatici o tuoi)."); return; }
        _bridgeA = geo;
        Say($"Pezzo A del ponte: {geo.Count} triangoli. Ora seleziona il pezzo B e premi \"Crea ponte\".");
    }

    /// <summary>Bridge from the marked piece A to the selected piece B, between the edges of
    /// the two that face each other.</summary>
    void BridgePieces()
    {
        var b = SelectionGeo();
        if (b.Count == 0) { Say("Seleziona i due pezzi da collegare (o segna prima il pezzo A)."); return; }
        var a = _bridgeA;
        if (a == null)
        {
            // Both pieces selected together: the two biggest pieces of the selection.
            var pieces = Pieces(b);
            if (pieces.Count < 2)
            {
                Say("La selezione è un pezzo solo: seleziona anche l'altro pezzo, oppure segna questo come pezzo A.");
                return;
            }
            a = pieces[0];
            b = pieces[1];
        }
        var ca = FacingChain(a, b);
        var cb = FacingChain(b, a);
        if (ca == null || cb == null)
        {
            Say($"Non trovo i bordi che si guardano nel pezzo {(ca == null ? "A" : "B")}. Prova con lo strumento Ponte (clic sui vertici).");
            return;
        }
        BuildBridge(ca, cb);
    }

    /// <summary>Triangles split into pieces joined by a shared side (two pieces touching at
    /// one corner, like a V, stay two), biggest first.</summary>
    static List<List<Geo>> Pieces(List<Geo> geo)
    {
        var parent = Enumerable.Range(0, geo.Count).ToArray();
        int Find(int x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }
        var owner = new Dictionary<(VKey, VKey), int>();
        for (int i = 0; i < geo.Count; i++)
            for (int k = 0; k < 3; k++)
            {
                VKey ka = LooseKeyOf(geo[i].P[k]), kb = LooseKeyOf(geo[i].P[(k + 1) % 3]);
                if (ka == kb) continue;
                var key = (ka.X != kb.X ? ka.X < kb.X : ka.Y != kb.Y ? ka.Y < kb.Y : ka.Z < kb.Z) ? (ka, kb) : (kb, ka);
                if (owner.TryGetValue(key, out int o)) parent[Find(i)] = Find(o);
                else owner[key] = i;
            }
        return [.. Enumerable.Range(0, geo.Count).GroupBy(Find).Select(g => g.Select(i => geo[i]).ToList()).OrderByDescending(p => p.Count)];
    }

    /// <summary>The open edges of a piece facing the other piece, as one ordered chain:
    /// the run of facing edges nearest the other piece (the longest among close ones).</summary>
    Chain? FacingChain(List<Geo> piece, List<Geo> other)
    {
        var counts = new Dictionary<(VKey, VKey), int>();
        (VKey, VKey) Edge(Vector3 a, Vector3 b)
        {
            var ka = LooseKeyOf(a);
            var kb = LooseKeyOf(b);
            bool less = ka.X != kb.X ? ka.X < kb.X : ka.Y != kb.Y ? ka.Y < kb.Y : ka.Z < kb.Z;
            return less ? (ka, kb) : (kb, ka);
        }
        foreach (var g in piece)
            for (int k = 0; k < 3; k++)
            {
                var e = Edge(g.P[k], g.P[(k + 1) % 3]);
                counts[e] = counts.GetValueOrDefault(e) + 1;
            }
        // Corners of the other piece, without those it shares with this one (the tip of a V:
        // every edge ending there would seem to point at it).
        var own = piece.SelectMany(g => g.P).Select(LooseKeyOf).ToHashSet();
        var targets = other.SelectMany(g => g.P).Where(p => !own.Contains(LooseKeyOf(p))).ToList();
        if (targets.Count == 0) return null;
        var centre = Centre(other);
        Vector3 Nearest(Vector3 p) => targets.MinBy(q => Vector3.DistanceSquared(p, q));
        // Facing open edges, joined into runs by their corners.
        var facing = new List<(Vector3 A, Vector3 B, float Dist)>();
        foreach (var g in piece)
            for (int k = 0; k < 3; k++)
            {
                Vector3 a = g.P[k], b = g.P[(k + 1) % 3], x = g.P[(k + 2) % 3];
                var e = Edge(a, b);
                if (e.Item1 == e.Item2 || counts[e] != 1) continue;
                var mid = (a + b) / 2;
                var ab = b - a;
                var outward = mid - x;
                outward -= ab * (Vector3.Dot(outward, ab) / ab.LengthSquared());
                var to = Nearest(mid) - mid;
                var toCentre = centre - mid;
                if (outward.LengthSquared() < 1e-3f || to.LengthSquared() < 1e-3f || toCentre.LengthSquared() < 1e-3f) continue;
                // Facing: out toward the other piece's nearest corner and toward its middle
                // (the nearest corner alone also catches edges at the ends of a strip).
                outward = Vector3.Normalize(outward);
                if (Vector3.Dot(outward, Vector3.Normalize(to)) < 0.3f || Vector3.Dot(outward, Vector3.Normalize(toCentre)) < 0.2f) continue;
                facing.Add((a, b, to.Length()));
            }
        if (facing.Count == 0) return null;
        var byCorner = new Dictionary<VKey, List<int>>();
        for (int i = 0; i < facing.Count; i++)
            foreach (var p in new[] { facing[i].A, facing[i].B })
            {
                var key = LooseKeyOf(p);
                if (!byCorner.TryGetValue(key, out var l)) byCorner[key] = l = [];
                l.Add(i);
            }
        var runOf = new int[facing.Count];
        Array.Fill(runOf, -1);
        var runs = new List<List<int>>();
        for (int i = 0; i < facing.Count; i++)
        {
            if (runOf[i] >= 0) continue;
            var run = new List<int>();
            var stack = new Stack<int>([i]);
            runOf[i] = runs.Count;
            while (stack.Count > 0)
            {
                int j = stack.Pop();
                run.Add(j);
                foreach (var p in new[] { facing[j].A, facing[j].B })
                    foreach (int n in byCorner[LooseKeyOf(p)])
                        if (runOf[n] < 0) { runOf[n] = runs.Count; stack.Push(n); }
            }
            runs.Add(run);
        }
        float RunDist(List<int> r) => r.Average(i => facing[i].Dist);
        float RunLength(List<int> r) => r.Sum(i => Vector3.Distance(facing[i].A, facing[i].B));
        float nearest = runs.Min(RunDist);
        var chosen = runs.Where(r => RunDist(r) <= nearest * 1.5f + 50).MaxBy(RunLength)!;
        // Walk the run corner to corner from an end.
        var links = new Dictionary<VKey, List<(VKey To, Vector3 P)>>();
        var at = new Dictionary<VKey, Vector3>();
        foreach (int i in chosen)
        {
            VKey ka = LooseKeyOf(facing[i].A), kb = LooseKeyOf(facing[i].B);
            at[ka] = facing[i].A;
            at[kb] = facing[i].B;
            if (!links.TryGetValue(ka, out var la)) links[ka] = la = [];
            if (!links.TryGetValue(kb, out var lb)) links[kb] = lb = [];
            la.Add((kb, facing[i].B));
            lb.Add((ka, facing[i].A));
        }
        var start = links.FirstOrDefault(kv => kv.Value.Count == 1).Key;
        if (!links.ContainsKey(start)) start = links.Keys.First();
        var pts = new List<Vector3> { at[start] };
        var seen = new HashSet<VKey> { start };
        var cur = start;
        while (true)
        {
            var nexts = links[cur].Where(l => !seen.Contains(l.To)).ToList();
            if (nexts.Count == 0) break;
            var step = nexts[0];
            seen.Add(step.To);
            pts.Add(step.P);
            cur = step.To;
        }
        if (pts.Count < 2) return null;
        return DescribeChain(pts, piece, Centre(other));
    }

    /// <summary>Colours, outward slopes and a texture for a chain of corners, from the
    /// triangles that border its edges (the given ones, else everything drawn).</summary>
    static Vector3 Centre(List<Geo> geo) => geo.Aggregate(Vector3.Zero, (s, g) => s + g.P[0] + g.P[1] + g.P[2]) / Math.Max(geo.Count * 3, 1);

    static Vector3 Centre(List<Vector3> pts) => pts.Aggregate(Vector3.Zero, (s, p) => s + p) / Math.Max(pts.Count, 1);

    /// <param name="toward">Where the other edge is: of two triangles on an edge, the one on
    /// the far side gives the slope.</param>
    Chain DescribeChain(List<Vector3> pts, List<Geo> around, Vector3 toward)
    {
        var chain = new Chain { P = pts };
        chain.Measure();
        var byEdge = new Dictionary<(VKey, VKey), List<Geo>>();
        var corners = new Dictionary<VKey, Vector3>();
        (VKey, VKey) Edge(VKey ka, VKey kb) =>
            (ka.X != kb.X ? ka.X < kb.X : ka.Y != kb.Y ? ka.Y < kb.Y : ka.Z < kb.Z) ? (ka, kb) : (kb, ka);
        foreach (var g in around)
            for (int k = 0; k < 3; k++)
            {
                var e = Edge(LooseKeyOf(g.P[k]), LooseKeyOf(g.P[(k + 1) % 3]));
                if (!byEdge.TryGetValue(e, out var l)) byEdge[e] = l = [];
                l.Add(g);
                corners.TryAdd(LooseKeyOf(g.P[k]), g.Rgb[k]);
            }
        var slopes = new float[pts.Count];
        var weights = new float[pts.Count];
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            if (!byEdge.TryGetValue(Edge(LooseKeyOf(pts[i]), LooseKeyOf(pts[i + 1])), out var tris)) continue;
            var ab = pts[i + 1] - pts[i];
            var mid = (pts[i] + pts[i + 1]) / 2;
            // The bordering triangle facing the other edge; its corner off the edge gives the
            // slope out to the edge.
            Geo? g = null;
            var outward = Vector3.Zero;
            float best = -0.2f;
            foreach (var t in tris)
            {
                var x = t.P.FirstOrDefault(p => Vector3.DistanceSquared(p, pts[i]) > 16 && Vector3.DistanceSquared(p, pts[i + 1]) > 16, t.P[0]);
                var o = mid - x;
                o -= ab * (Vector3.Dot(o, ab) / Math.Max(ab.LengthSquared(), 1e-3f));
                var to = toward - mid;
                if (o.LengthSquared() < 1e-3f || to.LengthSquared() < 1e-3f) continue;
                float score = Vector3.Dot(Vector3.Normalize(o), Vector3.Normalize(to)) + (t.Poly >= 0 ? 0.01f : 0);
                if (score > best) { best = score; g = t; outward = o; }
            }
            if (g == null) continue;
            chain.Tex ??= g.Poly >= 0 ? g : null;
            float h = Flat(outward);
            if (h < 1) continue;
            float slope = Math.Clamp(outward.Y / h, -1.5f, 1.5f);
            slopes[i] += slope; weights[i]++;
            slopes[i + 1] += slope; weights[i + 1]++;
        }
        var fallback = chain.Tex is { } tex ? (tex.Rgb[0] + tex.Rgb[1] + tex.Rgb[2]) / 3 : new Vector3(128);
        for (int i = 0; i < pts.Count; i++)
        {
            chain.Slope.Add(weights[i] > 0 ? slopes[i] / weights[i] : float.NaN);
            chain.Rgb.Add(corners.TryGetValue(LooseKeyOf(pts[i]), out var c) ? c : fallback);
        }
        return chain;
    }

    /// <summary>The strip between two chains: columns at every corner of both (so it joins
    /// them exactly) and at every texture cell edge, rows at every cell edge across.</summary>
    void BuildBridge(Chain a, Chain b)
    {
        if (L == null) return;
        if (a.P.Count < 2 || b.P.Count < 2 || a.Length < 1 || b.Length < 1) { Say("Ogni lato del ponte deve avere almeno 2 vertici."); return; }
        // Same direction along both edges.
        if (Vector3.Distance(a.P[0], b.P[0]) + Vector3.Distance(a.P[^1], b.P[^1])
            > Vector3.Distance(a.P[0], b.P[^1]) + Vector3.Distance(a.P[^1], b.P[0]))
        {
            b.P.Reverse(); b.Rgb.Reverse(); b.Slope.Reverse();
            b.Measure();
        }
        // Texture: the brush, else the triangle along edge A (or B).
        string mesh;
        int poly;
        Vector2 lo, hi;
        (Vector3 U, Vector3 V)? axes;
        if (_brush != null)
        {
            mesh = _brush.Mesh; poly = _brush.Poly; lo = _brush.Min; hi = _brush.Max;
            axes = BrushTexelAxes(_brush);
        }
        else if ((a.Tex ?? b.Tex) is { } t)
        {
            mesh = t.Mesh; poly = t.Poly;
            lo = Vector2.Min(t.Uv[0], Vector2.Min(t.Uv[1], t.Uv[2]));
            hi = Vector2.Max(t.Uv[0], Vector2.Max(t.Uv[1], t.Uv[2]));
            axes = TexelAxes(t.P, t.Uv);
        }
        else { Say("Non trovo una texture: prendi un pennello col contagocce."); return; }
        if (!L.MeshByName.ContainsKey(mesh)) { Say("La texture non è utilizzabile."); return; }
        var tile = hi - lo;
        float tileU = axes is { } ax && tile.X >= 1 ? ax.U.Length() * tile.X : 600;
        float tileV = axes is { } ay && tile.Y >= 1 ? ay.V.Length() * tile.Y : 600;
        float gap = 0;
        for (int i = 0; i <= 8; i++) gap += Vector3.Distance(a.At(i / 8f).Pos, b.At(i / 8f).Pos) / 9;
        if (gap < 1) { Say("I due lati si toccano già."); return; }
        int along = _bridgeAlong > 0 ? _bridgeAlong : Math.Clamp((int)MathF.Round((a.Length + b.Length) / 2 / Math.Max(tileU, 1)), 1, 64);
        int across = _bridgeAcross > 0 ? _bridgeAcross : Math.Clamp((int)MathF.Round(gap / Math.Max(tileV, 1)), 1, 64);

        // Columns: every corner of both edges and every cell edge, close ones merged.
        var cols = new List<float>();
        for (int i = 0; i < a.P.Count; i++) cols.Add(a.S(i));
        for (int i = 0; i < b.P.Count; i++) cols.Add(b.S(i));
        for (int k = 0; k <= along; k++) cols.Add((float)k / along);
        cols.Sort();
        var s = new List<float>();
        foreach (float c in cols)
            if (s.Count == 0 || c - s[^1] > 1e-3f) s.Add(Math.Clamp(c, 0, 1));
        static float Wave(float x)
        {
            float w = x - 2 * MathF.Floor(x / 2);
            return w <= 1 ? w : 2 - w;
        }
        var grid = new EdVertex[s.Count, across + 1];
        for (int i = 0; i < s.Count; i++)
        {
            var (pa, ra, ma) = a.At(s[i]);
            var (pb, rb, mb) = b.At(s[i]);
            float run = Flat(pb - pa);
            float straight = run > 1 ? (pb.Y - pa.Y) / run : 0;
            // Slope leaving A toward B, and arriving into B (B's outward slope turned round).
            float ta = _bridgeCurve && !float.IsNaN(ma) ? ma : straight;
            float tb = _bridgeCurve && !float.IsNaN(mb) ? -mb : straight;
            for (int j = 0; j <= across; j++)
            {
                float t = (float)j / across, t2 = t * t, t3 = t2 * t;
                var pos = Vector3.Lerp(pa, pb, t);
                pos.Y = (2 * t3 - 3 * t2 + 1) * pa.Y + (t3 - 2 * t2 + t) * run * ta + (-2 * t3 + 3 * t2) * pb.Y + (t3 - t2) * run * tb;
                grid[i, j] = new EdVertex
                {
                    Pos = pos,
                    Uv = lo + new Vector2(Wave(s[i] * along), Wave(t * across)) * tile,
                    Rgb = Vector3.Lerp(ra, rb, t),
                };
            }
        }
        BeginChange();
        int group = _doc.NextGroup++;
        var made = new List<UserTri>();
        UserTri Tri(EdVertex p, EdVertex q, EdVertex r) => new()
        {
            Mesh = mesh, Poly = poly, Behind = _newBehind, Group = group, V = [p, q, r],
        };
        for (int i = 0; i + 1 < s.Count; i++)
            for (int j = 0; j < across; j++)
            {
                if (Vector3.DistanceSquared(grid[i, j].Pos, grid[i + 1, j].Pos) < 1e-2f
                    && Vector3.DistanceSquared(grid[i, j + 1].Pos, grid[i + 1, j + 1].Pos) < 1e-2f) continue;
                // Where both edges start at the same corner the cells there are triangles.
                foreach (var tri in new[] { Tri(grid[i, j], grid[i + 1, j], grid[i + 1, j + 1]), Tri(grid[i, j], grid[i + 1, j + 1], grid[i, j + 1]) })
                    if (Vector3.Cross(tri.V[1].Pos - tri.V[0].Pos, tri.V[2].Pos - tri.V[0].Pos).LengthSquared() > 1)
                        made.Add(tri);
            }
        _sel.Clear();
        foreach (var t in made)
        {
            _sel.Add(Key(Kind.User, _doc.Tris.Count));
            _doc.Tris.Add(t);
        }
        Changed();
        Say($"Ponte creato: {along} x {across} celle ({made.Count} triangoli tuoi, selezionati). "
            + "Se la texture va storta prova \"Gira angoli\"/specchia, o cambia le celle e rifallo (Ctrl+Z).");
    }

    // ---- Bridge tool: clicked vertices ----

    void BridgeClick()
    {
        Vector3? p = _snapVerts && _snapHit is { } snap ? snap
            : _hoverPos.W > 0.5f ? new Vector3(_hoverPos.X, _hoverPos.Y, _hoverPos.Z) : null;
        if (p is not { } point) { Say("Ponte: clicca su un vertice o su una superficie."); return; }
        _bridgePts[_bridgeSide].Add(point);
    }

    void BridgeNext()
    {
        var side = _bridgePts[_bridgeSide];
        if (side.Count < 2) { Say("Servono almeno 2 vertici per lato."); return; }
        if (_bridgeSide == 0) { _bridgeSide = 1; return; }
        var scene = SceneGeo(false);
        var a = DescribeChain([.. _bridgePts[0]], scene, Centre(_bridgePts[1]));
        var b = DescribeChain([.. _bridgePts[1]], scene, Centre(_bridgePts[0]));
        BuildBridge(a, b);
        BridgeReset();
    }

    void BridgeReset()
    {
        _bridgePts[0].Clear();
        _bridgePts[1].Clear();
        _bridgeSide = 0;
    }

    bool BridgeHasPoints => _bridgePts[0].Count + _bridgePts[1].Count > 0;

    void BridgeUndoPoint()
    {
        if (_bridgePts[_bridgeSide].Count > 0) _bridgePts[_bridgeSide].RemoveAt(_bridgePts[_bridgeSide].Count - 1);
        else if (_bridgeSide == 1) _bridgeSide = 0;
    }

    // ---- Panels and overlay ----

    void BridgeOptions()
    {
        ImGui.Checkbox("Curvo (segue le pendenze dei due lati)", ref _bridgeCurve);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Spento: altezza in linea retta da un lato all'altro.");
        ImGui.SetNextItemWidth(60 * S);
        ImGui.DragInt("##bal", ref _bridgeAlong, 0.1f, 0, 64, _bridgeAlong == 0 ? "auto" : "%d");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Celle lungo i bordi (0 = dalla grandezza della texture).");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(60 * S);
        ImGui.DragInt("celle (lungo, attraverso)##bac", ref _bridgeAcross, 0.1f, 0, 64, _bridgeAcross == 0 ? "auto" : "%d");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Celle da un lato all'altro (0 = dalla grandezza della texture).");
    }

    void BridgeToolPanel()
    {
        ImGui.Indent();
        ImGui.TextWrapped(_bridgeSide == 0
            ? $"Lato A: clic sui vertici in fila ({_bridgePts[0].Count}). Invio: passa al lato B."
            : $"Lato B: clic sui vertici in fila ({_bridgePts[1].Count}). Invio: crea il ponte.");
        if (ImGui.Button(_bridgeSide == 0 ? "Passa al lato B (Invio)" : "Crea ponte (Invio)")) BridgeNext();
        if (ImGui.Button("Togli ultimo (Backspace)")) BridgeUndoPoint();
        ImGui.SameLine();
        if (ImGui.Button("Ricomincia")) BridgeReset();
        BridgeOptions();
        ImGui.TextDisabled(_brush != null ? "Texture: il pennello." : "Texture: quella del bordo A (o prendi un pennello).");
        ImGui.Unindent();
    }

    void BridgeSelectionSection()
    {
        ImGui.SeparatorText("Ponte tra due pezzi");
        if (ImGui.Button("Questo è il pezzo A")) MarkBridgeA();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Ricorda la selezione come primo lato del ponte.\nPoi seleziona l'altro pezzo e premi Crea ponte.");
        if (_bridgeA != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"A: {_bridgeA.Count} tri.");
            ImGui.SameLine();
            if (ImGui.SmallButton("togli##bridgeA")) _bridgeA = null;
        }
        if (ImGui.Button(_bridgeA != null ? "Crea ponte dal pezzo A alla selezione" : "Crea ponte tra i 2 pezzi selezionati", new Vector2(-1, 0))) BridgePieces();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Riempie lo spazio tra i bordi dei due pezzi che si guardano, attaccato ai loro vertici.\nSeleziona i due pezzi insieme (Ctrl+clic, doppio clic per il pezzo intero), oppure segna prima il pezzo A.\nTexture: pennello, se c'è, altrimenti quella del pezzo A.");
        BridgeOptions();
    }

    void BridgeOverlay(ImDrawListPtr dl)
    {
        for (int side = 0; side < 2; side++)
        {
            uint col = side == 0 ? Col(0.3f, 1, 0.4f) : Col(0.3f, 0.8f, 1);
            Vector2? prev = null;
            foreach (var p in _bridgePts[side])
                if (_cam.Project(p, _view, out var sp))
                {
                    dl.AddCircleFilled(sp, 5 * _ui, col);
                    if (prev is { } pv) dl.AddLine(pv, sp, col, 2.5f * _ui);
                    prev = sp;
                }
            if (side == _bridgeSide && prev is { } last && _view.Contains(ImGui.GetIO().MousePos))
            {
                var target = _snapHit is { } sh && _cam.Project(sh, _view, out var ss) ? ss : ImGui.GetIO().MousePos;
                dl.AddLine(last, target, col, 1.5f * _ui);
            }
        }
        dl.AddText(ImGui.GetIO().MousePos + new Vector2(14, 10), Col(0.4f, 1, 0.6f), _bridgeSide == 0 ? "ponte: lato A" : "ponte: lato B");
    }

    /// <summary>Piece A of a bridge outlined in orange while it waits for piece B.</summary>
    void BridgePieceOverlay(ImDrawListPtr dl)
    {
        if (_bridgeA == null) return;
        var s = new Vector2[3];
        int drawn = 0;
        foreach (var g in _bridgeA)
        {
            if (++drawn > 4000) break;
            bool ok = true;
            for (int k = 0; k < 3 && ok; k++) ok = _cam.Project(g.P[k], _view, out s[k]);
            if (ok) dl.AddPolyline(ref s[0], 3, Col(1, 0.55f, 0.1f, 0.9f), ImDrawFlags.Closed, 1.5f * _ui);
        }
    }
}
