// Lighting: in Crash the shading of the scenery is the vertex colour (the texture is the
// same everywhere), so a copied piece keeps the light of the place it came from. These
// commands give own triangles the colours of a marked piece (corner by corner, matched by
// texture corner, so moved, turned or mirrored copies work) or of the original scenery
// around them (same surface only).
using System.Numerics;
using ImGuiNET;

namespace WideEditor;

sealed partial class Editor
{
    List<Geo>? _lightSource;
    float _lightRadius = 400;

    void MarkLightSource()
    {
        var geo = SelectionGeo();
        if (geo.Count == 0) { Say("Seleziona prima il pezzo con l'illuminazione giusta."); return; }
        _lightSource = geo;
        Say($"Sorgente dell'illuminazione: {geo.Count} triangoli. Ora seleziona i tuoi triangoli e premi \"Copia illuminazione\".");
    }

    /// <summary>Colours of the marked piece onto the selected own triangles. Each corner
    /// takes the colour of the source corner with the same texture corner (nearest one once
    /// both pieces are laid on each other's centre); a corner with no such texture corner
    /// takes the nearest source corner.</summary>
    void CopyLightFromSource()
    {
        var targets = SelUser.ToList();
        if (_lightSource == null || targets.Count == 0) { Say("Segna la sorgente e seleziona i tuoi triangoli da colorare."); return; }
        var src = _lightSource.SelectMany(g => Enumerable.Range(0, 3).Select(k => (P: g.P[k], Uv: g.Uv[k], Rgb: g.Rgb[k]))).ToList();
        var srcCentre = src.Aggregate(Vector3.Zero, (s, v) => s + v.P) / src.Count;
        var dstCentre = targets.SelectMany(i => _doc.Tris[i].V).Aggregate(Vector3.Zero, (s, v) => s + v.Pos) / (targets.Count * 3);
        var shift = srcCentre - dstCentre;
        BeginChange();
        int byTexture = 0, byPlace = 0;
        bool SameCorners(Geo g, UserTri t) =>
            t.V.All(v => g.Uv.Any(u => Vector2.DistanceSquared(u, v.Uv) < 0.5f))
            && g.Uv.All(u => t.V.Any(v => Vector2.DistanceSquared(u, v.Uv) < 0.5f));
        foreach (int i in targets)
        {
            var t = _doc.Tris[i];
            // The source triangle it is a copy of: same texture polygon and corners, the
            // nearest one when several are; its corners matched by texture corner.
            var c = (t.V[0].Pos + t.V[1].Pos + t.V[2].Pos) / 3 + shift;
            var twin = _lightSource.Where(g => g.Mesh == t.Mesh && g.Poly == t.Poly && SameCorners(g, t))
                .MinBy(g => Vector3.DistanceSquared((g.P[0] + g.P[1] + g.P[2]) / 3, c));
            if (twin != null)
            {
                for (int k = 0; k < 3; k++)
                {
                    var uv = t.V[k].Uv;
                    int j = Enumerable.Range(0, 3).MinBy(j => Vector2.DistanceSquared(twin.Uv[j], uv));
                    t.V[k].Rgb = twin.Rgb[j];
                }
                byTexture += 3;
                continue;
            }
            for (int k = 0; k < 3; k++)
            {
                var v = _doc.Tris[i].V[k];
                var at = v.Pos + shift;
                var same = src.Where(s => Vector2.DistanceSquared(s.Uv, v.Uv) < 0.5f).ToList();
                var pick = (same.Count > 0 ? same : src).MinBy(s => Vector3.DistanceSquared(s.P, at));
                if (same.Count > 0) byTexture++; else byPlace++;
                _doc.Tris[i].V[k].Rgb = pick.Rgb;
            }
        }
        Changed();
        Say($"Illuminazione copiata su {targets.Count} triangoli ({byTexture} vertici abbinati per texture"
            + (byPlace > 0 ? $", {byPlace} per posizione" : "") + ").");
    }

    /// <summary>Each corner of the selected own triangles takes the colour of the original
    /// scenery around it: corners within the radius on surfaces facing the same way,
    /// nearer ones counting more.</summary>
    void LightFromNeighbours()
    {
        var targets = SelUser.ToList();
        if (L == null || targets.Count == 0) { Say("Seleziona i tuoi triangoli da colorare."); return; }
        // Original corners with the normal of the surfaces they belong to.
        var corners = new List<(Vector3 P, Vector3 N, Vector3 Rgb)>();
        foreach (var m in _drawMeshes)
        {
            if (m.Backdrop) continue;
            var normals = new Vector3[m.VertexCount];
            for (int p = 0; p < m.PolyCount; p++)
            {
                if (_doc.Removed.Contains((m.Name, p))) continue;
                var n = Vector3.Cross(m.Corner(p, 1) - m.Corner(p, 0), m.Corner(p, 2) - m.Corner(p, 0));
                if (n.LengthSquared() < 1e-6f) continue;
                n = Vector3.Normalize(n);
                for (int k = 0; k < 3; k++)
                {
                    ref var acc = ref normals[m.Tri[p * 3 + k]];
                    // Windings are not consistent: add facing the same way as what is there.
                    acc += Vector3.Dot(acc, n) < 0 ? -n : n;
                }
            }
            for (int v = 0; v < m.VertexCount; v++)
                if (normals[v].LengthSquared() > 1e-6f) corners.Add((m.Pos[v], Vector3.Normalize(normals[v]), m.Rgb[v]));
        }
        BeginChange();
        int lit = 0, alone = 0;
        float r2 = _lightRadius * _lightRadius;
        foreach (int i in targets)
        {
            var t = _doc.Tris[i];
            var n = Vector3.Cross(t.V[1].Pos - t.V[0].Pos, t.V[2].Pos - t.V[0].Pos);
            n = n.LengthSquared() > 1e-6f ? Vector3.Normalize(n) : Vector3.UnitY;
            for (int k = 0; k < 3; k++)
            {
                var p = t.V[k].Pos;
                Vector3 sum = Vector3.Zero;
                float weight = 0;
                foreach (var c in corners)
                {
                    float d2 = Vector3.DistanceSquared(c.P, p);
                    if (d2 > r2) continue;
                    float facing = MathF.Abs(Vector3.Dot(c.N, n));
                    if (facing < 0.5f) continue;
                    float w = facing / (d2 + 100);
                    sum += c.Rgb * w;
                    weight += w;
                }
                if (weight > 0) { t.V[k].Rgb = sum / weight; lit++; }
                else alone++;
            }
        }
        Changed();
        Say($"Illuminazione presa dagli originali vicini: {lit} vertici."
            + (alone > 0 ? $" {alone} senza originali vicini sulla stessa superficie: allarga il raggio." : ""));
    }

    void LightSelectionSection()
    {
        ImGui.SeparatorText("Illuminazione");
        if (ImGui.Button("Questa è la sorgente della luce")) MarkLightSource();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Ricorda i colori dei vertici (la luce) del pezzo selezionato:\noriginali, automatici o tuoi. Poi seleziona la tua copia e premi Copia illuminazione.");
        if (_lightSource != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"{_lightSource.Count} tri.");
            ImGui.SameLine();
            if (ImGui.SmallButton("togli##light")) _lightSource = null;
        }
        if (!SelUser.Any()) return;
        ImGui.BeginDisabled(_lightSource == null);
        if (ImGui.Button("Copia illuminazione dalla sorgente", new Vector2(-1, 0))) CopyLightFromSource();
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Ogni vertice dei tuoi triangoli prende il colore del vertice della sorgente\ncon lo stesso punto della texture: va bene per copie spostate, girate o specchiate.");
        if (ImGui.Button("Illuminazione dagli originali vicini")) LightFromNeighbours();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Ogni vertice prende la media dei colori degli originali vicini\nsulla stessa superficie (stessa direzione), entro il raggio: si intona col posto dove sta.");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90 * S);
        ImGui.DragFloat("raggio##light", ref _lightRadius, 5, 50, 5000, "%.0f");
    }
}
