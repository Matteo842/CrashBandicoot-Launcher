// Unreal-style gizmo on any selection: move (axes, planes, screen plane), rotate
// (rings, angle steps), scale (axes, uniform). Grabbing automatic repairs makes them
// editable (switched off and copied); grabbing original scenery moves a copy.
// Also the right-click menu.
using System.Numerics;
using ImGuiNET;

namespace WideEditor;

enum GizmoMode { All, Move, Rotate, Scale }

sealed partial class Editor
{
    static readonly Vector3[] Axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
    GizmoMode _gizmo = GizmoMode.All;
    int _handle;
    // Drags are measured on screen, like Unity/Unreal: the mouse movement along the
    // handle as drawn, turned into world units at the pivot. A ray/axis crossing blew up
    // when an axis pointed at the camera.
    Vector2 _mouse0, _axisDir, _planeU, _planeV;
    float _axisPixels;
    float _rotStep = 15, _angle0, _angleNow, _scale0;
    Vector2 _pivotScreen0;
    uint _ctxHover;
    (int Tri, int K)? _ctxVertex;
    bool _ctxOpen;

    float GizmoLength(Vector3 pivot) => 110 * _ui * _cam.PixelSize(pivot, _view);

    /// <summary>Centre of every selected triangle, whatever its kind.</summary>
    Vector3 SelPivotAll()
    {
        Span<Vector3> c = stackalloc Vector3[3];
        Vector3 sum = Vector3.Zero;
        int n = 0;
        foreach (var key in _sel)
            if (Corners(key, c)) { sum += c[0] + c[1] + c[2]; n += 3; }
        return n > 0 ? sum / n : Vector3.Zero;
    }

    static (Vector3 U, Vector3 V) PlaneAxes(int normal) => normal switch
    {
        0 => (Vector3.UnitY, Vector3.UnitZ),
        1 => (Vector3.UnitX, Vector3.UnitZ),
        _ => (Vector3.UnitX, Vector3.UnitY),
    };

    Vector3[] Ring(Vector3 pivot, int axis, float radius, int count = 64)
    {
        var (u, v) = PlaneAxes(axis);
        var pts = new Vector3[count + 1];
        for (int i = 0; i <= count; i++)
        {
            float t = i * MathF.Tau / count;
            pts[i] = pivot + (u * MathF.Cos(t) + v * MathF.Sin(t)) * radius;
        }
        return pts;
    }

    // Handles: 0-2 move along an axis, 3 move with the screen, 4-6 move on the plane with
    // that normal, 10-12 rotate about an axis, 20-22 scale along an axis, 23 scale all.
    bool GizmoMoves => _gizmo is GizmoMode.Move or GizmoMode.All;
    bool GizmoRotates => _gizmo is GizmoMode.Rotate or GizmoMode.All;
    float RingRadius(float len) => _gizmo == GizmoMode.All ? len * 1.3f : len * 0.85f;

    int GizmoHit(Vector2 mouse)
    {
        var pivot = SelPivotAll();
        if (!_cam.Project(pivot, _view, out var ps)) return -1;
        float len = GizmoLength(pivot), best = 8 * _ui;
        int hit = -1;
        if (_gizmo != GizmoMode.Rotate && Math.Abs(mouse.X - ps.X) < 10 * _ui && Math.Abs(mouse.Y - ps.Y) < 10 * _ui)
            return GizmoMoves ? 3 : 23;
        if (GizmoMoves)
            for (int n = 0; n < 3; n++)
            {
                var (u, v) = PlaneAxes(n);
                if (_cam.Project(pivot + (u + v) * len * 0.3f, _view, out var pc) && Vector2.Distance(pc, mouse) < 11 * _ui)
                    return 4 + n;
            }
        if (_gizmo != GizmoMode.Rotate)
            for (int a = 0; a < 3; a++)
            {
                if (!_cam.Project(pivot + Axes[a] * len, _view, out var pe)) continue;
                float d = SegmentDistance(mouse, ps, pe);
                if (d < best) { best = d; hit = GizmoMoves ? a : 20 + a; }
            }
        if (GizmoRotates)
            for (int a = 0; a < 3; a++)
            {
                var ring = Ring(pivot, a, RingRadius(len));
                for (int i = 0; i + 1 < ring.Length; i++)
                    if (_cam.Project(ring[i], _view, out var s0) && _cam.Project(ring[i + 1], _view, out var s1))
                    {
                        float d = SegmentDistance(mouse, s0, s1);
                        if (d < best) { best = d; hit = 10 + a; }
                    }
            }
        return hit;
    }

    static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
        return Vector2.Distance(p, a + ab * t);
    }

    float AxisParam(Vector2 mouse, Vector3 axis)
    {
        var d = _cam.Ray(mouse, _view);
        var w = _pivot0 - _cam.Pos;
        float b = Vector3.Dot(axis, d), dd = Vector3.Dot(axis, w), e = Vector3.Dot(d, w);
        float denom = 1 - b * b;
        return denom < 1e-5f ? 0 : (b * e - dd) / denom;
    }

    Vector3 PlaneHit(Vector2 mouse, Vector3 through, Vector3 normal)
    {
        var d = _cam.Ray(mouse, _view);
        float dn = Vector3.Dot(d, normal);
        if (MathF.Abs(dn) < 1e-4f) dn = dn < 0 ? -1e-4f : 1e-4f;
        float t = Vector3.Dot(through - _cam.Pos, normal) / dn;
        return _cam.Pos + d * Math.Clamp(t, 0, Camera.Far);
    }

    /// <summary>Turns the selection into the user's own triangles: automatic repairs are
    /// switched off and copied (so they move), original scenery is copied (it stays).</summary>
    bool EnsureEditable()
    {
        if (_sel.Count == 0) return false;
        // Every caller changes the selection next: one undo step for both.
        BeginChange();
        if (!_sel.Any(k => KindOf(k) != Kind.User)) return SelUser.Any();
        int group = _doc.NextGroup++;
        bool retail = false;
        var keep = _sel.Where(k => KindOf(k) == Kind.User).ToList();
        var made = new List<long>();
        foreach (var key in _sel.Where(k => KindOf(k) != Kind.User).OrderBy(k => k).ToList())
        {
            if (AsUser(key, group) is not { } t) continue;
            if (KindOf(key) == Kind.Auto)
            {
                var a = _auto!.Tris[IndexOf(key)];
                if (!_doc.IsRepairHidden(a)) _doc.HiddenRepairs.Add(new(a.Mesh.Name, a.Poly, a.Center));
                _autoDirty = true;
            }
            else retail = true;
            made.Add(Key(Kind.User, _doc.Tris.Count));
            _doc.Tris.Add(t);
        }
        _sel.Clear();
        foreach (var k in keep.Concat(made)) _sel.Add(k);
        Changed();
        if (made.Count > 0)
            Say(retail ? "Gli originali non si spostano: lavori su una copia (l'originale resta)."
                : "Automatici resi modificabili.");
        return _sel.Count > 0;
    }

    void StartGizmo(int handle, Vector2 mouse)
    {
        if (Alt && SelUser.Any() && !_sel.Any(k => KindOf(k) != Kind.User)) CopyAsMine(false);
        if (!EnsureEditable()) return;
        BeginChange();
        _moving.Clear();
        var exclude = new HashSet<(int, int)>();
        foreach (int i in SelUser)
            for (int k = 0; k < 3; k++)
            {
                _moving.Add((i, k, _doc.Tris[i].V[k].Pos));
                exclude.Add((i, k));
            }
        _pivot0 = SelPivot();
        _cam.Project(_pivot0, _view, out _pivotScreen0);
        _handle = handle;
        _mouse0 = mouse;
        switch (handle)
        {
            case < 3:
                _axis = handle;
                (_axisDir, _axisPixels) = ScreenAxis(Axes[_axis]);
                _drag = DragMode.Axis;
                break;
            case 3:
                _hit0 = PlaneHit(mouse, _pivot0, _cam.Forward);
                _drag = DragMode.Plane;
                break;
            case < 10:
            {
                _axis = handle - 4;
                var (u, v) = PlaneAxes(_axis);
                var (du, pu) = ScreenAxis(u);
                var (dv, pv) = ScreenAxis(v);
                _planeU = du * pu;
                _planeV = dv * pv;
                _hit0 = PlaneHit(mouse, _pivot0, _cam.Forward);
                _drag = DragMode.PlaneAxis;
                break;
            }
            case < 20:
                _axis = handle - 10;
                _angle0 = MathF.Atan2(mouse.Y - _pivotScreen0.Y, mouse.X - _pivotScreen0.X);
                _angleNow = 0;
                _drag = DragMode.Rotate;
                break;
            default:
                _axis = handle - 20;
                if (_axis < 3)
                {
                    (_axisDir, _axisPixels) = ScreenAxis(Axes[_axis]);
                    _scale0 = _axisPixels * GizmoLength(_pivot0);
                }
                _drag = DragMode.Scale;
                break;
        }
        BuildSnapTargets(exclude);
    }

    /// <summary>Screen direction of a world axis at the pivot and pixels per world unit
    /// along it (an axis pointing at the camera moves with the mouse's up/down).</summary>
    (Vector2 Dir, float Pixels) ScreenAxis(Vector3 axis)
    {
        float len = GizmoLength(_pivot0);
        if (_cam.Project(_pivot0, _view, out var ps) && _cam.Project(_pivot0 + axis * len, _view, out var pe)
            && Vector2.Distance(ps, pe) > 6 * _ui)
        {
            var d = pe - ps;
            return (Vector2.Normalize(d), d.Length() / len);
        }
        return (new Vector2(0, -1), 1 / _cam.PixelSize(_pivot0, _view));
    }

    static Vector3 RoundTo(Vector3 v, float step) => step <= 0 ? v
        : new(MathF.Round(v.X / step) * step, MathF.Round(v.Y / step) * step, MathF.Round(v.Z / step) * step);

    void UpdateGizmo(Vector2 mouse)
    {
        if (_drag == DragMode.Rotate)
        {
            float screen = MathF.Atan2(mouse.Y - _pivotScreen0.Y, mouse.X - _pivotScreen0.X) - _angle0;
            // Screen angles turn clockwise (Y down); a positive turn about an axis that
            // points at the camera looks counter-clockwise.
            float degrees = screen * 180 / MathF.PI * (Vector3.Dot(Axes[_axis], _cam.Forward) < 0 ? -1 : 1);
            degrees = (degrees + 540) % 360 - 180;
            if (_rotStep > 0) degrees = MathF.Round(degrees / _rotStep) * _rotStep;
            _angleNow = degrees;
            var q = Quaternion.CreateFromAxisAngle(Axes[_axis], degrees * MathF.PI / 180);
            foreach (var (tri, k, orig) in _moving) _doc.Tris[tri].V[k].Pos = _pivot0 + Vector3.Transform(orig - _pivot0, q);
            Changed();
            return;
        }
        if (_drag == DragMode.Scale)
        {
            var moved = mouse - _mouse0;
            float f = _axis < 3 ? 1 + Vector2.Dot(moved, _axisDir) / Math.Max(_scale0, 1)
                : MathF.Exp((moved.X - moved.Y) / (150 * _ui));
            f = Math.Max(f, 0.01f);
            var scale = _axis < 3 ? Vector3.One + Axes[_axis] * (f - 1) : new Vector3(f);
            _angleNow = f * 100;
            foreach (var (tri, k, orig) in _moving) _doc.Tris[tri].V[k].Pos = _pivot0 + (orig - _pivot0) * scale;
            Changed();
            return;
        }
        Vector3 delta;
        if (_drag == DragMode.Axis)
        {
            float d = Vector2.Dot(mouse - _mouse0, _axisDir) / _axisPixels;
            if (_grid > 0) d = MathF.Round(d / _grid) * _grid;
            delta = Axes[_axis] * d;
        }
        else if (_drag == DragMode.PlaneAxis)
        {
            // Mouse movement split along the two plane axes as drawn on screen.
            var m = mouse - _mouse0;
            float det = _planeU.X * _planeV.Y - _planeU.Y * _planeV.X;
            var (u, v) = PlaneAxes(_axis);
            if (MathF.Abs(det) < 0.05f * _planeU.Length() * _planeV.Length())
                delta = PlaneHit(mouse, _pivot0, _cam.Forward) - _hit0;
            else
            {
                float a = (m.X * _planeV.Y - m.Y * _planeV.X) / det, b = (_planeU.X * m.Y - _planeU.Y * m.X) / det;
                delta = u * a + v * b;
            }
            delta = RoundTo(delta, _grid);
        }
        else
            delta = RoundTo(PlaneHit(mouse, _pivot0, _cam.Forward) - _hit0, _grid);
        _snapHit = null;
        if (_snapVerts)
        {
            float best = 12;
            Vector3 bestFrom = default, bestTo = default;
            var seen = new HashSet<Vector3>();
            foreach (var (_, _, orig) in _moving)
            {
                if (!seen.Add(orig) || seen.Count > 600) continue;
                var at = orig + delta;
                if (!_cam.Project(at, _view, out var s)) continue;
                // Only vertices really next to it in 3D: one far behind that only looks
                // close on screen made the triangle jump there.
                float near = 16 * _ui * _cam.PixelSize(at, _view);
                if (FindSnap(s, best, out var target, out float dist, at, near))
                {
                    best = dist;
                    bestFrom = orig;
                    bestTo = target;
                    _snapHit = target;
                }
            }
            if (_snapHit != null)
            {
                var gap = bestTo - (bestFrom + delta);
                delta += _drag switch
                {
                    DragMode.Axis => Axes[_axis] * Vector3.Dot(gap, Axes[_axis]),
                    DragMode.PlaneAxis => gap - Axes[_axis] * Vector3.Dot(gap, Axes[_axis]),
                    _ => gap,
                };
            }
        }
        foreach (var (tri, k, orig) in _moving) _doc.Tris[tri].V[k].Pos = orig + delta;
        Changed();
    }

    void DrawGizmo(ImDrawListPtr dl)
    {
        var pivot = _drag is DragMode.Rotate or DragMode.Scale ? _pivot0 : SelPivotAll();
        if (!_cam.Project(pivot, _view, out var ps)) return;
        float len = GizmoLength(pivot), w = 3.5f * _ui;
        uint yellow = Col(1, 1, 0.3f);
        uint[] colours = [Col(1, 0.25f, 0.25f), Col(0.3f, 1, 0.3f), Col(0.35f, 0.55f, 1)];
        bool dragging = _drag is DragMode.Axis or DragMode.Plane or DragMode.PlaneAxis or DragMode.Rotate or DragMode.Scale;
        bool Hot(int handle) => dragging ? handle == _handle : _hoverAxis == handle;
        string[] names = ["X", "Y", "Z"];

        if (GizmoRotates)
            for (int a = 0; a < 3; a++)
            {
                var ring = Ring(pivot, a, RingRadius(len));
                uint c = Hot(10 + a) ? yellow : colours[a];
                for (int i = 0; i + 1 < ring.Length; i++)
                    if (_cam.Project(ring[i], _view, out var s0) && _cam.Project(ring[i + 1], _view, out var s1))
                        dl.AddLine(s0, s1, c, Hot(10 + a) ? w * 1.4f : w * 0.8f);
            }
        if (_drag == DragMode.Rotate)
            dl.AddText(ps + new Vector2(14, -24) * _ui, yellow, $"{names[_axis]} {_angleNow:0.#}°");
        if (_gizmo == GizmoMode.Rotate)
        {
            dl.AddCircleFilled(ps, 4 * _ui, Col(1, 1, 1, 0.8f));
            return;
        }
        if (GizmoMoves)
            for (int n = 0; n < 3; n++)
            {
                var (u, v) = PlaneAxes(n);
                Span<Vector2> q = stackalloc Vector2[4];
                bool ok = _cam.Project(pivot + (u * 0.2f + v * 0.2f) * len, _view, out q[0])
                    & _cam.Project(pivot + (u * 0.4f + v * 0.2f) * len, _view, out q[1])
                    & _cam.Project(pivot + (u * 0.4f + v * 0.4f) * len, _view, out q[2])
                    & _cam.Project(pivot + (u * 0.2f + v * 0.4f) * len, _view, out q[3]);
                if (!ok) continue;
                uint c = Hot(4 + n) ? Col(1, 1, 0.3f, 0.8f) : (colours[n] & 0x00FFFFFF) | 0x70000000;
                dl.AddQuadFilled(q[0], q[1], q[2], q[3], c);
            }
        for (int a = 0; a < 3; a++)
        {
            if (!_cam.Project(pivot + Axes[a] * len, _view, out var pe)) continue;
            int handle = GizmoMoves ? a : 20 + a;
            uint c = Hot(handle) ? yellow : colours[a];
            dl.AddLine(ps, pe, c, Hot(handle) ? w * 1.4f : w);
            var dir = pe - ps;
            if (dir.LengthSquared() > 1)
            {
                dir = Vector2.Normalize(dir);
                var side = new Vector2(-dir.Y, dir.X);
                float head = 14 * _ui;
                if (GizmoMoves)
                    dl.AddTriangleFilled(pe + dir * head, pe + side * head * 0.45f, pe - side * head * 0.45f, c);
                else
                    dl.AddRectFilled(pe - new Vector2(6 * _ui), pe + new Vector2(6 * _ui), c);
            }
            dl.AddText(pe + new Vector2(10, -10) * _ui, colours[a], names[a]);
        }
        bool centre = Hot(GizmoMoves ? 3 : 23);
        dl.AddRectFilled(ps - new Vector2(8 * _ui), ps + new Vector2(8 * _ui), centre ? Col(1, 1, 0.3f, 0.9f) : Col(1, 1, 1, 0.6f));
        if (_drag == DragMode.Scale) dl.AddText(ps + new Vector2(14, -24) * _ui, yellow, $"{_angleNow:0}%");
    }

    /// <summary>Move / rotate / scale buttons in the corner of the 3D view.</summary>
    void GizmoBar()
    {
        if (_tool != Tool.Select) return;
        ImGui.SetNextWindowPos(new Vector2(_view.X + 8 * _ui, _view.Y + 8 * _ui));
        ImGui.SetNextWindowBgAlpha(0.75f);
        ImGui.Begin("##gizmobar", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing);
        void Mode(GizmoMode mode, string label, string help)
        {
            bool on = _gizmo == mode;
            if (on) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.25f, 0.45f, 0.8f, 1));
            if (ImGui.Button(label)) _gizmo = mode;
            if (on) ImGui.PopStyleColor();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(help);
            ImGui.SameLine();
        }
        Mode(GizmoMode.All, "Sposta + ruota (T)", "Frecce e quadratini spostano, gli anelli esterni ruotano.");
        Mode(GizmoMode.Move, "Sposta (W)", "Frecce: lungo un asse. Quadratini: su un piano. Quadrato centrale: parallelo allo schermo.");
        Mode(GizmoMode.Rotate, "Ruota (E)", "Trascina un anello: ruota attorno a quell'asse.");
        Mode(GizmoMode.Scale, "Scala (R)", "Cubetti: scala lungo un asse. Quadrato centrale: scala tutto.");
        ImGui.SetNextItemWidth(60 * S);
        ImGui.DragFloat("griglia", ref _grid, 1, 0, 2000, "%.0f");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Passo dello spostamento (0 = libero)");
        if (GizmoRotates)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(60 * S);
            ImGui.DragFloat("passo °", ref _rotStep, 0.5f, 0, 90, "%.0f");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Passo della rotazione (0 = libera)");
        }
        ImGui.End();
    }

    // ---- Right-click menu ----

    void OpenContext()
    {
        _ctxHover = _hoverId;
        _ctxVertex = _tool == Tool.Vertex ? _hoverVertex : null;
        if (Decode(_hoverId, out var kind, out int index) && Allowed(kind) && !_sel.Contains(Key(kind, index)))
        {
            _sel.Clear();
            ClickSelect(_hoverId);
        }
        _ctxOpen = true;
    }

    void ContextMenu()
    {
        if (_ctxOpen)
        {
            ImGui.OpenPopup("##ctx");
            _ctxOpen = false;
        }
        if (!ImGui.BeginPopup("##ctx")) return;
        if (_ctxVertex is { } cv && cv.Tri < _doc.Tris.Count)
        {
            if (ImGui.MenuItem("Elimina vertice (e i suoi triangoli)")) DeleteVertex(cv.Tri, cv.K);
            ImGui.Separator();
        }
        int retail = _sel.Count(k => KindOf(k) == Kind.Retail), auto = _sel.Count(k => KindOf(k) == Kind.Auto);
        int user = SelUser.Count();
        if (_sel.Count == 0) ImGui.TextDisabled("Niente selezionato");
        else
        {
            ImGui.TextDisabled($"{_sel.Count} triangoli selezionati");
            if (ImGui.MenuItem("Duplica", "Ctrl+D")) CopyAsMine(false);
            if (user > 0 && ImGui.MenuItem("Elimina", "Canc")) DeleteSelection();
            if (auto > 0)
            {
                if (ImGui.MenuItem("Elimina automatici", "Canc")) SetRepairsOff(true);
                if (_showOff && ImGui.MenuItem("Ripristina automatici eliminati")) SetRepairsOff(false);
                if (ImGui.MenuItem("Rendi modificabili")) EnsureEditable();
            }
            if (retail > 0)
            {
                if (ImGui.MenuItem("Elimina originali (anche al centro 4:3)", "Canc")) SetRemoved(true);
                if (_showOff && ImGui.MenuItem("Ripristina originali eliminati")) SetRemoved(false);
                if (ImGui.MenuItem("Nascondi solo nelle bande 16:9")) SetHidden(true);
                if (ImGui.MenuItem("Mostra nelle bande 16:9")) SetHidden(false);
            }
            if (_sel.Count == 1 && KindOf(_sel.First()) != Kind.Retail
                && ImGui.MenuItem("Seleziona tutto il pezzo", "doppio clic"))
            {
                uint id = PickId(KindOf(_sel.First()), IndexOf(_sel.First()));
                _sel.Clear();
                ClickSelect(id, piece: true);
            }
            ImGui.Separator();
            if (ImGui.BeginMenu("Ruota"))
            {
                string[] names = ["X", "Y", "Z"];
                for (int a = 0; a < 3; a++)
                    foreach (float deg in new[] { 90f, -90f, 180f })
                        if (ImGui.MenuItem($"{names[a]} {deg:+0;-0}°") && EnsureEditable()) RotateSel(Axes[a], deg);
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("Specchia"))
            {
                for (int a = 0; a < 3; a++)
                    if (ImGui.MenuItem(a == 0 ? "X (sinistra/destra)" : a == 1 ? "Y (su/giù)" : "Z (avanti/indietro)") && EnsureEditable())
                        MirrorSel(a);
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("Texture"))
            {
                if (ImGui.MenuItem("Gira angoli texture") && EnsureEditable()) CornerUvs(false);
                if (ImGui.MenuItem("Scambia angoli texture") && EnsureEditable()) CornerUvs(true);
                ImGui.Separator();
                if (ImGui.MenuItem("Ruota a sinistra") && EnsureEditable()) UvSel(st => new(st.Y, 1 - st.X));
                if (ImGui.MenuItem("Ruota a destra") && EnsureEditable()) UvSel(st => new(1 - st.Y, st.X));
                if (ImGui.MenuItem("Specchia orizzontale") && EnsureEditable()) UvSel(st => new(1 - st.X, st.Y));
                if (ImGui.MenuItem("Specchia verticale") && EnsureEditable()) UvSel(st => new(st.X, 1 - st.Y));
                ImGui.Separator();
                if (ImGui.MenuItem("Metti texture del pennello", "", false, _brush != null) && EnsureEditable()) ApplyBrush(false);
                if (ImGui.MenuItem("Metti texture e colori del pennello", "", false, _brush != null) && EnsureEditable()) ApplyBrush(true);
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("Profondità"))
            {
                if (ImGui.MenuItem("Vera profondità") && EnsureEditable()) SetBehind(false);
                if (ImGui.MenuItem("Dietro la scena (tappa solo i buchi)") && EnsureEditable()) SetBehind(true);
                ImGui.EndMenu();
            }
            ImGui.Separator();
            if (ImGui.MenuItem("Inquadra", "F")) FrameSelection();
            if (ImGui.MenuItem("Deseleziona", "Esc")) _sel.Clear();
        }
        ImGui.Separator();
        if (ImGui.MenuItem("Prendi questa texture (pennello)", "", false, _ctxHover != 0)) TakeBrush(_ctxHover);
        if (ImGui.MenuItem("Annulla", "Ctrl+Z", false, _undo.Count > 0)) Undo();
        ImGui.EndPopup();
    }

    /// <summary>Per triangle: the texture corners move one vertex on, or two of them swap
    /// (the six orders of a triangle's mapping), the shape stays where it is.</summary>
    void CornerUvs(bool swap)
    {
        BeginChange();
        foreach (int i in SelUser)
        {
            var v = _doc.Tris[i].V;
            if (swap) (v[1].Uv, v[2].Uv) = (v[2].Uv, v[1].Uv);
            else (v[0].Uv, v[1].Uv, v[2].Uv) = (v[1].Uv, v[2].Uv, v[0].Uv);
        }
        Changed();
    }

    void MirrorSel(int axis)
    {
        BeginChange();
        var c = SelPivot();
        TransformSel(p =>
        {
            var q = p;
            if (axis == 0) q.X = 2 * c.X - p.X; else if (axis == 1) q.Y = 2 * c.Y - p.Y; else q.Z = 2 * c.Z - p.Z;
            return q;
        }, mirror: true);
    }

    void SetBehind(bool behind)
    {
        BeginChange();
        foreach (int i in SelUser) _doc.Tris[i].Behind = behind;
        Changed();
    }

    void DeleteVertex(int tri, int k)
    {
        var at = _doc.Tris[tri].V[k].Pos;
        PushUndo();
        int removed = _doc.Tris.RemoveAll(t => t.V.Any(v => Vector3.DistanceSquared(v.Pos, at) < 0.25f));
        _sel.RemoveWhere(key => KindOf(key) == Kind.User);
        Changed();
        Say($"Vertice eliminato con {removed} triangoli.");
    }
}
