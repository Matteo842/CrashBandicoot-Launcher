// Mouse work in the 3D view: camera, selection (click, group, box), the move gizmo
// with vertex snapping, vertex dragging, drawing new quads/triangles, eyedropper.
using System.Numerics;
using ImGuiNET;

namespace WideEditor;

enum Tool { Select, Vertex, Quad, Triangle, Eyedropper }
enum DragMode { None, ClickPending, Box, Axis, Plane, PlaneAxis, Rotate, Scale, Vertex }

sealed partial class Editor
{
    readonly HashSet<long> _sel = [];
    Tool _tool = Tool.Select, _toolBefore = Tool.Select;
    DragMode _drag;
    Vector2 _dragStart;
    int _axis, _hoverAxis = -1;
    Vector3 _pivot0, _hit0;
    float _s0, _vertexDepth;
    readonly List<(int Tri, int K, Vector3 Orig)> _moving = [];
    readonly List<Vector3> _points = [];
    bool _snapVerts = true, _weld = true, _newBehind;
    float _grid;
    bool _pickRetail = true, _pickAuto = true, _pickUser = true;
    bool _looking, _panning, _rightDown, _rightMoved;
    Vector2 _rightStart;
    float _panScale = 1;
    Brush? _brush;
    (int Tri, int K)? _hoverVertex;
    Vector3? _snapHit, _pointPreview;

    // Screen positions of every vertex a point can snap to, bucketed by 16 px cells.
    readonly List<Vector2> _snapS = [];
    readonly List<Vector3> _snapP = [];
    readonly Dictionary<long, List<int>> _snapGrid = [];
    (Vector3, float, float, int)? _snapSignature;
    int _docVersion;

    static long Key(Kind k, int i) => ((long)k << 32) | (uint)i;
    static Kind KindOf(long key) => (Kind)(key >> 32);
    static int IndexOf(long key) => (int)(key & 0xFFFFFFFF);

    static bool Decode(uint id, out Kind kind, out int index)
    {
        kind = (Kind)(int)((id >> 24) - 1);
        index = (int)(id & 0xFFFFFF);
        return id != 0 && kind is Kind.Retail or Kind.Auto or Kind.User;
    }

    (Mesh Mesh, int Poly) RetailOf(int global)
    {
        int lo = 0, hi = _meshBase.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_meshBase[mid] <= global) lo = mid; else hi = mid - 1;
        }
        return (L!.Meshes[lo], global - _meshBase[lo]);
    }

    bool Valid(long key) => KindOf(key) switch
    {
        Kind.Retail => IndexOf(key) < _retailCount,
        Kind.Auto => IndexOf(key) < _auto!.Tris.Count,
        _ => IndexOf(key) < _doc.Tris.Count,
    };

    /// <summary>World corners of any triangle (backdrops placed around the camera).</summary>
    bool Corners(long key, Span<Vector3> c)
    {
        if (L == null || !Valid(key)) return false;
        int i = IndexOf(key);
        switch (KindOf(key))
        {
            case Kind.Retail:
            {
                var (m, p) = RetailOf(i);
                for (int k = 0; k < 3; k++) c[k] = m.Backdrop ? m.Corner(p, k) - m.Trans + _cam.Pos : m.Corner(p, k);
                return true;
            }
            case Kind.Auto:
            {
                var t = _auto!.Tris[i];
                var off = t.Mesh.Backdrop ? _cam.Pos - t.Mesh.Trans : Vector3.Zero;
                c[0] = t.A.Pos + off; c[1] = t.B.Pos + off; c[2] = t.C.Pos + off;
                return true;
            }
            default:
            {
                var t = _doc.Tris[i];
                for (int k = 0; k < 3; k++) c[k] = t.V[k].Pos;
                return true;
            }
        }
    }

    bool Allowed(Kind k) => k switch { Kind.Retail => _pickRetail, Kind.Auto => _pickAuto, _ => _pickUser };

    IEnumerable<int> SelUser => _sel.Where(k => KindOf(k) == Kind.User && IndexOf(k) < _doc.Tris.Count).Select(IndexOf);

    Vector3 SelPivot()
    {
        Vector3 sum = Vector3.Zero;
        int n = 0;
        foreach (int i in SelUser)
            foreach (var v in _doc.Tris[i].V) { sum += v.Pos; n++; }
        return n > 0 ? sum / n : Vector3.Zero;
    }

    void Changed()
    {
        _userDirty = true;
        _docVersion++;
        _dirty = true;
    }

    // ---- Per-frame input ----

    void ViewInput(float dt)
    {
        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        bool over = _view.Contains(mouse) && !io.WantCaptureMouse;

        // Every key is polled each frame (edges need it), then acted on.
        bool z = KeyHit(Silk.NET.Input.Key.Z), y = KeyHit(Silk.NET.Input.Key.Y), s = KeyHit(Silk.NET.Input.Key.S);
        bool d = KeyHit(Silk.NET.Input.Key.D), del = KeyHit(Silk.NET.Input.Key.Delete), esc = KeyHit(Silk.NET.Input.Key.Escape);
        bool f = KeyHit(Silk.NET.Input.Key.F), w = KeyHit(Silk.NET.Input.Key.W), e = KeyHit(Silk.NET.Input.Key.E);
        bool r = KeyHit(Silk.NET.Input.Key.R), t = KeyHit(Silk.NET.Input.Key.T);
        if (!io.WantTextInput && _drag == DragMode.None)
        {
            if (Ctrl && z) { if (Shift) Redo(); else Undo(); }
            if (Ctrl && y) Redo();
            if (Ctrl && s) Save();
            if (Ctrl && d) CopyAsMine(false);
            if (del) DeleteSelection();
            if (esc)
            {
                if (_points.Count > 0) _points.Clear();
                else if (_tool != Tool.Select) _tool = Tool.Select;
                else _sel.Clear();
            }
            if (!Ctrl && !_looking)
            {
                if (f) FrameSelection();
                if (w) _gizmo = GizmoMode.Move;
                if (e) _gizmo = GizmoMode.Rotate;
                if (r) _gizmo = GizmoMode.Scale;
                if (t) _gizmo = GizmoMode.All;
            }
        }

        // Camera: right drag looks, middle drag pans, wheel flies toward the mouse.
        // A right click without dragging opens the menu instead.
        if (over && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            _rightDown = true;
            _rightMoved = false;
            _rightStart = mouse;
        }
        if (_rightDown && !ImGui.IsMouseDown(ImGuiMouseButton.Right))
        {
            _rightDown = false;
            if (!_rightMoved && _drag == DragMode.None) OpenContext();
        }
        _looking = _rightDown;
        if (_looking)
        {
            bool keys = ImGui.IsKeyDown(ImGuiKey.W) || ImGui.IsKeyDown(ImGuiKey.A) || ImGui.IsKeyDown(ImGuiKey.S)
                || ImGui.IsKeyDown(ImGuiKey.D) || ImGui.IsKeyDown(ImGuiKey.Q) || ImGui.IsKeyDown(ImGuiKey.E);
            if (Vector2.Distance(mouse, _rightStart) > 4 || keys) _rightMoved = true;
            if (_rightMoved) StopFollow();
            if (_rightMoved)
            {
                _cam.Yaw -= io.MouseDelta.X * 0.004f;
                _cam.Pitch -= io.MouseDelta.Y * 0.004f;
                _cam.ClampPitch();
            }
            float step = 3000 * _speed * dt * (Shift ? 4 : 1);
            if (ImGui.IsKeyDown(ImGuiKey.W)) _cam.Pos += _cam.Forward * step;
            if (ImGui.IsKeyDown(ImGuiKey.S)) _cam.Pos -= _cam.Forward * step;
            if (ImGui.IsKeyDown(ImGuiKey.D)) _cam.Pos += _cam.Right * step;
            if (ImGui.IsKeyDown(ImGuiKey.A)) _cam.Pos -= _cam.Right * step;
            if (ImGui.IsKeyDown(ImGuiKey.E)) _cam.Pos += Vector3.UnitY * step;
            if (ImGui.IsKeyDown(ImGuiKey.Q)) _cam.Pos -= Vector3.UnitY * step;
        }
        if (over && ImGui.IsMouseClicked(ImGuiMouseButton.Middle))
        {
            _panning = true;
            var focus = _hoverPos.W > 0.5f ? new Vector3(_hoverPos.X, _hoverPos.Y, _hoverPos.Z) : _cam.Pos + _cam.Forward * 2000;
            _panScale = _cam.PixelSize(focus, _view);
        }
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Middle)) _panning = false;
        CaptureCursor((_looking && _rightMoved) || _panning);
        if (_panning) StopFollow();
        if (_panning) _cam.Pos += (-_cam.Right * io.MouseDelta.X + _cam.Up * io.MouseDelta.Y) * _panScale;
        if (over && io.MouseWheel != 0)
        {
            float dist = _hoverPos.W > 0.5f ? Vector3.Distance(_cam.Pos, new(_hoverPos.X, _hoverPos.Y, _hoverPos.Z)) : 2000;
            StopFollow();
            _cam.Pos += _cam.Ray(mouse, _view) * io.MouseWheel * Math.Max(dist * 0.15f, 40) * _speed;
        }

        UpdateHover(mouse, over);

        if (over && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && _drag == DragMode.None) LeftPress(mouse);
        if (_drag != DragMode.None)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left)) LeftDrag(mouse);
            else LeftRelease();
        }
    }

    void StopFollow()
    {
        if (!_follow) return;
        _follow = false;
        Say("Camera libera: non segue più il gioco.");
    }

    void UpdateHover(Vector2 mouse, bool over)
    {
        _hoverAxis = -1;
        _hoverVertex = null;
        if (_drag == DragMode.None) _snapHit = null;
        if (!over || _drag != DragMode.None) return;
        if (_tool == Tool.Select && _sel.Count > 0) _hoverAxis = GizmoHit(mouse);
        if (_tool == Tool.Vertex)
        {
            float best = 10;
            var drawn = _drawMeshes.Select(m => m.Name).ToHashSet();
            for (int i = 0; i < _doc.Tris.Count; i++)
            {
                if (!drawn.Contains(_doc.Tris[i].Mesh)) continue;
                for (int k = 0; k < 3; k++)
                    if (_cam.Project(_doc.Tris[i].V[k].Pos, _view, out var s) && Vector2.Distance(s, mouse) < best)
                    {
                        best = Vector2.Distance(s, mouse);
                        _hoverVertex = (i, k);
                    }
            }
        }
        if (_tool is Tool.Quad or Tool.Triangle)
        {
            if (_snapVerts)
            {
                EnsureSnapTargets([]);
                if (FindSnap(mouse, 12, out var p)) _snapHit = p;
            }
            _pointPreview = PointFor(mouse);
        }
    }

    void LeftPress(Vector2 mouse)
    {
        _dragStart = mouse;
        switch (_tool)
        {
            case Tool.Select:
                // With Ctrl or Shift a click always selects, even over the gizmo.
                if (_hoverAxis >= 0 && !Ctrl && !Shift) StartGizmo(_hoverAxis, mouse);
                else
                {
                    _drag = DragMode.ClickPending;
                    _clickPiece = ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) || Alt;
                }
                break;
            case Tool.Vertex:
                if (_hoverVertex is { } hv) StartVertexDrag(hv.Tri, hv.K);
                else _drag = DragMode.ClickPending;
                break;
            case Tool.Quad:
            case Tool.Triangle:
                AddPoint(mouse);
                break;
            case Tool.Eyedropper:
                TakeBrush(_hoverId);
                _tool = _toolBefore;
                break;
        }
    }

    void LeftDrag(Vector2 mouse)
    {
        switch (_drag)
        {
            case DragMode.ClickPending:
                if (Vector2.Distance(mouse, _dragStart) > 5) _drag = DragMode.Box;
                break;
            case DragMode.Axis:
            case DragMode.Plane:
            case DragMode.PlaneAxis:
            case DragMode.Rotate:
            case DragMode.Scale:
                UpdateGizmo(mouse);
                break;
            case DragMode.Vertex:
                UpdateVertexDrag(mouse);
                break;
        }
    }

    void LeftRelease()
    {
        var mouse = ImGui.GetIO().MousePos;
        if (_drag == DragMode.ClickPending) ClickSelect(_hoverId, _clickPiece);
        else if (_drag == DragMode.Box) BoxSelect(_dragStart, mouse);
        _drag = DragMode.None;
        _moving.Clear();
        _snapHit = null;
    }

    // ---- Selection ----

    bool _clickPiece;

    /// <summary>A click takes one triangle; <paramref name="piece"/> (double click, Alt)
    /// takes the whole piece: every automatic triangle made from the same polygon, or
    /// the user's triangles made together.</summary>
    void ClickSelect(uint id, bool piece = false)
    {
        var io = ImGui.GetIO();
        bool add = Shift, toggle = Ctrl;
        if (!Decode(id, out var kind, out int index) || !Allowed(kind))
        {
            if (!add && !toggle) _sel.Clear();
            return;
        }
        var keys = new List<long> { Key(kind, index) };
        if (kind == Kind.Auto && piece)
            foreach (int i in AutoPiece(index))
                if (i != index) keys.Add(Key(Kind.Auto, i));
        if (kind == Kind.User && piece && index < _doc.Tris.Count && _doc.Tris[index].Group != 0)
        {
            int g = _doc.Tris[index].Group;
            for (int i = 0; i < _doc.Tris.Count; i++)
                if (i != index && _doc.Tris[i].Group == g) keys.Add(Key(Kind.User, i));
        }
        if (!add && !toggle) _sel.Clear();
        bool remove = toggle && !piece && _sel.Contains(keys[0]);
        foreach (var k in keys)
            if (remove) _sel.Remove(k); else _sel.Add(k);
    }

    Dictionary<(Mesh, int), List<int>>? _autoPieces;

    List<int> AutoPiece(int index)
    {
        if (_autoPieces == null)
        {
            _autoPieces = [];
            for (int i = 0; i < _auto!.Tris.Count; i++)
            {
                var key = (_auto.Tris[i].Mesh, _auto.Tris[i].Poly);
                if (!_autoPieces.TryGetValue(key, out var list)) _autoPieces[key] = list = [];
                list.Add(i);
            }
        }
        var t = _auto!.Tris[index];
        return _autoPieces.TryGetValue((t.Mesh, t.Poly), out var piece) ? piece : [index];
    }

    void BoxSelect(Vector2 a, Vector2 b)
    {
        var io = ImGui.GetIO();
        var min = Vector2.Max(Vector2.Min(a, b), new Vector2(_view.X, _view.Y));
        var max = Vector2.Min(Vector2.Max(a, b), new Vector2(_view.X + _view.W - 1, _view.Y + _view.H - 1));
        if (max.X <= min.X || max.Y <= min.Y) return;
        var (x0, y1) = ToTarget(min);
        var (x1, y0) = ToTarget(max);
        var ids = _gfx.ReadIds(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
        // Shift or Ctrl: add to the selection, Alt: take away from it.
        if (!Shift && !Ctrl && !Alt) _sel.Clear();
        foreach (uint id in ids.Distinct())
            if (Decode(id, out var kind, out int index) && Allowed(kind))
            {
                if (Alt) _sel.Remove(Key(kind, index));
                else _sel.Add(Key(kind, index));
            }
    }

    void FrameSelection()
    {
        Span<Vector3> c = stackalloc Vector3[3];
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        int n = 0;
        foreach (var key in _sel)
            if (Corners(key, c))
                for (int k = 0; k < 3; k++) { min = Vector3.Min(min, c[k]); max = Vector3.Max(max, c[k]); n++; }
        if (n == 0) return;
        var center = (min + max) / 2;
        float size = Vector3.Distance(min, max);
        _cam.Pos = center - _cam.Forward * Math.Max(size * 1.2f, 500);
    }

    void StartVertexDrag(int tri, int k)
    {
        BeginChange();
        _moving.Clear();
        var at = _doc.Tris[tri].V[k].Pos;
        var exclude = new HashSet<(int, int)>();
        for (int i = 0; i < _doc.Tris.Count; i++)
            for (int j = 0; j < 3; j++)
                if ((i == tri && j == k) || (_weld && Vector3.DistanceSquared(_doc.Tris[i].V[j].Pos, at) < 0.25f))
                {
                    _moving.Add((i, j, _doc.Tris[i].V[j].Pos));
                    exclude.Add((i, j));
                }
        _pivot0 = at;
        _vertexDepth = _cam.Depth(at);
        _drag = DragMode.Vertex;
        BuildSnapTargets(exclude);
    }

    void UpdateVertexDrag(Vector2 mouse)
    {
        var p = _cam.AtDepth(mouse, _view, _vertexDepth);
        if (_grid > 0) p = new(MathF.Round(p.X / _grid) * _grid, MathF.Round(p.Y / _grid) * _grid, MathF.Round(p.Z / _grid) * _grid);
        _snapHit = null;
        if (_snapVerts && FindSnap(mouse, 12, out var target, out _, p, 16 * _ui * _cam.PixelSize(p, _view)))
        {
            p = target;
            _snapHit = target;
        }
        foreach (var (tri, k, _) in _moving) _doc.Tris[tri].V[k].Pos = p;
        Changed();
    }

    // ---- Snapping ----

    void EnsureSnapTargets(HashSet<(int, int)> exclude)
    {
        var sig = (_cam.Pos, _cam.Pitch, _cam.Yaw, _docVersion * 31 + _drawMeshes.Count);
        if (_snapSignature == sig) return;
        BuildSnapTargets(exclude);
        _snapSignature = sig;
    }

    void BuildSnapTargets(HashSet<(int, int)> exclude)
    {
        _snapSignature = null;
        _snapS.Clear();
        _snapP.Clear();
        _snapGrid.Clear();
        void Add(Vector3 p)
        {
            if (!_cam.Project(p, _view, out var s)) return;
            if (s.X < _view.X - 20 || s.Y < _view.Y - 20 || s.X > _view.X + _view.W + 20 || s.Y > _view.Y + _view.H + 20) return;
            long cell = ((long)(int)MathF.Floor(s.X / 16) << 32) | (uint)(int)MathF.Floor(s.Y / 16);
            if (!_snapGrid.TryGetValue(cell, out var list)) _snapGrid[cell] = list = [];
            list.Add(_snapS.Count);
            _snapS.Add(s);
            _snapP.Add(p);
        }
        foreach (var m in _drawMeshes)
        {
            if (m.Backdrop) continue;
            foreach (var p in m.Pos) Add(p);
            if (_showAuto && _auto!.Ranges.TryGetValue(m, out var r))
                for (int i = r.Start; i < r.Start + r.Count; i++)
                {
                    var t = _auto.Tris[i];
                    if (_doc.IsRepairHidden(t)) continue;
                    Add(t.A.Pos); Add(t.B.Pos); Add(t.C.Pos);
                }
        }
        for (int i = 0; i < _doc.Tris.Count; i++)
            for (int k = 0; k < 3; k++)
                if (!exclude.Contains((i, k))) Add(_doc.Tris[i].V[k].Pos);
    }

    bool FindSnap(Vector2 s, float radius, out Vector3 p) => FindSnap(s, radius, out p, out _);

    bool FindSnap(Vector2 s, float radius, out Vector3 p, out float dist) =>
        FindSnap(s, radius, out p, out dist, Vector3.Zero, float.PositiveInfinity);

    /// <summary>Nearest snap vertex on screen, only among those within <paramref name="near"/>
    /// of <paramref name="from"/> in the world.</summary>
    bool FindSnap(Vector2 s, float radius, out Vector3 p, out float dist, Vector3 from, float near)
    {
        p = default;
        dist = radius;
        bool found = false;
        int cx = (int)MathF.Floor(s.X / 16), cy = (int)MathF.Floor(s.Y / 16), r = (int)MathF.Ceiling(radius / 16);
        for (int x = cx - r; x <= cx + r; x++)
            for (int y = cy - r; y <= cy + r; y++)
                if (_snapGrid.TryGetValue(((long)x << 32) | (uint)y, out var list))
                    foreach (int i in list)
                    {
                        if (near < float.PositiveInfinity && Vector3.DistanceSquared(_snapP[i], from) > near * near) continue;
                        float d = Vector2.Distance(_snapS[i], s);
                        // Nearest on screen; among equals, the one nearest the camera.
                        if (d < dist || (found && MathF.Abs(d - dist) < 0.5f && _cam.Depth(_snapP[i]) < _cam.Depth(p)))
                        {
                            dist = d;
                            p = _snapP[i];
                            found = true;
                        }
                    }
        return found;
    }

    // ---- Drawing new geometry ----

    void AddPoint(Vector2 mouse)
    {
        if (_brush == null)
        {
            Say("Prima scegli una texture: strumento Contagocce, poi clic su un triangolo.");
            return;
        }
        _points.Add(PointFor(mouse));
        if (_points.Count == (_tool == Tool.Quad ? 4 : 3))
        {
            CreateFromPoints();
            _points.Clear();
        }
    }

    /// <summary>Where a click of the quad/triangle tools puts its point. The first one goes
    /// on a vertex or on the surface clicked. Later ones go on a nearby vertex or surface,
    /// otherwise on the plane of the points so far (facing the camera until there are
    /// three), so a click past the shape no longer lands at the far end of the map.
    /// Shift: always on that plane.</summary>
    Vector3 PointFor(Vector2 mouse)
    {
        Vector3? surface = _hoverPos.W > 0.5f ? new Vector3(_hoverPos.X, _hoverPos.Y, _hoverPos.Z) : null;
        if (_points.Count == 0)
        {
            if (_snapVerts && _snapHit is { } first) return first;
            return surface ?? _cam.AtDepth(mouse, _view, 2000);
        }
        var last = _points[^1];
        float span = _points.Max(p => Vector3.Distance(p, _points[0]));
        float reach = Math.Max(span * 3, 800);
        if (!Shift && _snapVerts && _snapHit is { } snap && Vector3.Distance(snap, last) < reach) return snap;
        if (!Shift && surface is { } q && Vector3.Distance(q, last) < reach) return q;
        var through = last;
        var normal = _cam.Forward;
        if (_points.Count >= 3)
        {
            var n = Vector3.Cross(_points[1] - _points[0], _points[2] - _points[0]);
            if (n.LengthSquared() > 1e-3f)
            {
                normal = Vector3.Normalize(n);
                through = _points[0];
            }
        }
        return PlaneHit(mouse, through, normal);
    }

    void CreateFromPoints()
    {
        var b = _brush!;
        PushUndo();
        int group = _doc.NextGroup++;
        var made = new List<UserTri>();
        UserTri Tri(Vector3[] p, Vector2[] uv, Vector3[] rgb) => new()
        {
            Mesh = b.Mesh, Poly = b.Poly, Behind = _newBehind, Group = group,
            V = [.. Enumerable.Range(0, 3).Select(k => new EdVertex { Pos = p[k], Uv = uv[k], Rgb = rgb[k] })],
        };
        if (_points.Count == 4)
        {
            Vector2 lo = b.Min, hi = b.Max;
            Vector2[] uv = [lo, new(hi.X, lo.Y), hi, new(lo.X, hi.Y)];
            var rgb = b.MeanRgb;
            made.Add(Tri([_points[0], _points[1], _points[2]], [uv[0], uv[1], uv[2]], [rgb, rgb, rgb]));
            made.Add(Tri([_points[0], _points[2], _points[3]], [uv[0], uv[2], uv[3]], [rgb, rgb, rgb]));
        }
        else made.Add(Tri([_points[0], _points[1], _points[2]], b.Uv, b.Rgb));
        _sel.Clear();
        foreach (var t in made)
        {
            _sel.Add(Key(Kind.User, _doc.Tris.Count));
            _doc.Tris.Add(t);
        }
        Changed();
        Say(made.Count == 2 ? "Quad creato." : "Triangolo creato.");
    }

    void TakeBrush(uint id)
    {
        if (!Decode(id, out var kind, out int index) || L == null) { Say("Niente sotto il mouse."); return; }
        switch (kind)
        {
            case Kind.Retail:
            {
                var (m, p) = RetailOf(index);
                if (m.Backdrop) { Say("Le texture del cielo non si possono usare."); return; }
                _brush = new(m.Name, p, L.UvsOf(m, p), [.. Enumerable.Range(0, 3).Select(k => m.Rgb[m.Tri[p * 3 + k]])],
                    [m.Corner(p, 0), m.Corner(p, 1), m.Corner(p, 2)]);
                break;
            }
            case Kind.Auto:
            {
                var t = _auto!.Tris[index];
                if (t.Poly < 0 || t.Mesh.Backdrop) { Say("Questo triangolo non ha una texture utilizzabile."); return; }
                _brush = new(t.Mesh.Name, t.Poly, [t.A.Uv, t.B.Uv, t.C.Uv], [t.A.Rgb, t.B.Rgb, t.C.Rgb],
                    [t.A.Pos, t.B.Pos, t.C.Pos]);
                break;
            }
            default:
            {
                var t = _doc.Tris[index];
                _brush = new(t.Mesh, t.Poly, [.. t.V.Select(v => v.Uv)], [.. t.V.Select(v => v.Rgb)],
                    [.. t.V.Select(v => v.Pos)]);
                break;
            }
        }
        _brushThumbKey = null;
        Say($"Pennello: mesh {_brush.Mesh} poligono {_brush.Poly}.");
    }

    // ---- Edits ----

    UserTri? AsUser(long key, int group)
    {
        if (L == null || !Valid(key)) return null;
        int i = IndexOf(key);
        switch (KindOf(key))
        {
            case Kind.Retail:
            {
                var (m, p) = RetailOf(i);
                if (m.Backdrop) return null;
                var uv = L.UvsOf(m, p);
                return new UserTri
                {
                    Mesh = m.Name, Poly = p, Behind = _newBehind, Group = group,
                    V = [.. Enumerable.Range(0, 3).Select(k => new EdVertex { Pos = m.Corner(p, k), Uv = uv[k], Rgb = m.Rgb[m.Tri[p * 3 + k]] })],
                };
            }
            case Kind.Auto:
            {
                var t = _auto!.Tris[i];
                if (t.Poly < 0 || t.Mesh.Backdrop) return null;
                return new UserTri
                {
                    Mesh = t.Mesh.Name, Poly = t.Poly, Behind = t.Behind, Rank = t.Rank, Culled = t.Culled, Group = group,
                    V = [t.A, t.B, t.C],
                };
            }
            default:
            {
                var c = _doc.Tris[i].Clone();
                c.Group = group;
                return c;
            }
        }
    }

    /// <summary>Copies the selection as the user's own triangles (Ctrl+D); with
    /// <paramref name="replace"/> the automatic repairs copied are switched off.</summary>
    void CopyAsMine(bool replace)
    {
        if (_sel.Count == 0) return;
        BeginChange();
        int group = _doc.NextGroup++;
        var made = new List<long>();
        foreach (var key in _sel.OrderBy(k => k))
        {
            if (AsUser(key, group) is not { } t) continue;
            if (replace && KindOf(key) == Kind.Auto)
            {
                var a = _auto!.Tris[IndexOf(key)];
                if (!_doc.IsRepairHidden(a)) _doc.HiddenRepairs.Add(new(a.Mesh.Name, a.Poly, a.Center));
                _autoDirty = true;
            }
            made.Add(Key(Kind.User, _doc.Tris.Count));
            _doc.Tris.Add(t);
        }
        _sel.Clear();
        foreach (var k in made) _sel.Add(k);
        Changed();
        Say($"{made.Count} triangoli copiati come tuoi: trascina le frecce per spostarli.");
    }

    /// <summary>Deletes the user's triangles and switches off automatic repairs (they are
    /// made by the game's code every time, so "deleting" one means switching it off).</summary>
    void DeleteSelection()
    {
        var idx = SelUser.OrderByDescending(i => i).ToList();
        int autos = _sel.Count(k => KindOf(k) == Kind.Auto), retail = _sel.Count(k => KindOf(k) == Kind.Retail);
        if (_sel.Count == 0) return;
        BeginChange();
        if (autos > 0) SetRepairsOff(true);
        if (retail > 0) SetRemoved(true);
        foreach (int i in idx) _doc.Tris.RemoveAt(i);
        _sel.RemoveWhere(k => KindOf(k) == Kind.User);
        Changed();
        Say($"{idx.Count + autos + retail} triangoli eliminati.");
    }

    void SetHidden(bool hide)
    {
        PushUndo();
        foreach (var key in _sel.Where(k => KindOf(k) == Kind.Retail))
        {
            var (m, p) = RetailOf(IndexOf(key));
            if (hide) _doc.Hidden.Add((m.Name, p)); else _doc.Hidden.Remove((m.Name, p));
        }
        _retailDirty = true;
        Changed();
    }

    /// <summary>Original polygons removed everywhere (4:3 core too) or given back.</summary>
    void SetRemoved(bool remove)
    {
        BeginChange();
        foreach (var key in _sel.Where(k => KindOf(k) == Kind.Retail).ToList())
        {
            var (m, p) = RetailOf(IndexOf(key));
            if (m.Backdrop) continue;
            if (remove) _doc.Removed.Add((m.Name, p)); else _doc.Removed.Remove((m.Name, p));
        }
        if (remove && !_showOff) _sel.RemoveWhere(k => KindOf(k) == Kind.Retail);
        _retailDirty = true;
        Changed();
    }

    /// <summary>Selected original polygons drawn with the brush texture (same mesh only:
    /// texture tables are per mesh), or given back their own.</summary>
    void SetRetexture(bool on)
    {
        var retail = _sel.Where(k => KindOf(k) == Kind.Retail).Select(k => RetailOf(IndexOf(k))).ToList();
        if (retail.Count == 0) return;
        if (on)
        {
            if (_brush == null) { Say("Prima prendi un pennello col contagocce."); return; }
            if (retail.Any(r => r.Mesh.Name != _brush.Mesh))
            {
                Say($"Il pennello deve venire dalla stessa mesh ({retail[0].Mesh.Name}): ogni mesh ha le sue texture.");
                return;
            }
        }
        BeginChange();
        foreach (var (m, p) in retail)
            if (on) _doc.Retex[(m.Name, p)] = (_brush!.Poly, 0);
            else _doc.Retex.Remove((m.Name, p));
        _retailDirty = true;
        Changed();
        Say(on ? "Texture cambiata sugli originali: vale anche al centro 4:3. \"Gira angoli\" la gira." : "Texture originale rimessa.");
    }

    void SetRepairsOff(bool off)
    {
        BeginChange();
        foreach (var key in _sel.Where(k => KindOf(k) == Kind.Auto))
        {
            var t = _auto!.Tris[IndexOf(key)];
            bool hidden = _doc.IsRepairHidden(t);
            if (off && !hidden) _doc.HiddenRepairs.Add(new(t.Mesh.Name, t.Poly, t.Center));
            if (!off && hidden)
                _doc.HiddenRepairs.RemoveAll(h => h.Poly == t.Poly && h.Mesh == t.Mesh.Name
                    && Vector3.DistanceSquared(h.Center, t.Center) < 4);
        }
        if (off && !_showOff) _sel.RemoveWhere(k => KindOf(k) == Kind.Auto);
        _autoDirty = true;
        Changed();
    }

    void TransformSel(Func<Vector3, Vector3> f, bool mirror = false)
    {
        foreach (int i in SelUser)
        {
            var t = _doc.Tris[i];
            for (int k = 0; k < 3; k++) t.V[k].Pos = f(t.V[k].Pos);
            if (mirror) (t.V[1], t.V[2]) = (t.V[2], t.V[1]);
        }
        Changed();
    }

    void RotateSel(Vector3 axis, float degrees)
    {
        var pivot = SelPivot();
        var q = Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180);
        TransformSel(p => pivot + Vector3.Transform(p - pivot, q));
    }

    /// <summary>Texture coordinates of the selection moved inside their common box.</summary>
    void UvSel(Func<Vector2, Vector2> f)
    {
        var idx = SelUser.ToList();
        if (idx.Count == 0) return;
        Vector2 lo = new(float.MaxValue), hi = new(float.MinValue);
        foreach (int i in idx)
            foreach (var v in _doc.Tris[i].V) { lo = Vector2.Min(lo, v.Uv); hi = Vector2.Max(hi, v.Uv); }
        // A side shorter than a texel stays as it is (dividing by it blew up).
        var size = hi - lo;
        if (size.X < 1 || size.Y < 1) { Say("La texture di questi triangoli è troppo piccola da girare: prima mettici la texture del pennello."); return; }
        foreach (int i in idx)
            for (int k = 0; k < 3; k++)
            {
                var st = (_doc.Tris[i].V[k].Uv - lo) / size;
                _doc.Tris[i].V[k].Uv = lo + f(st) * size;
            }
        Changed();
    }

    /// <summary>Directions in the world of the brush texture's +U and +V (one texel each),
    /// worked out from the triangle the brush was taken from; null without one.</summary>
    (Vector3 U, Vector3 V)? BrushTexelAxes(Brush b)
    {
        if (b.Pos is not { Length: 3 } p) return null;
        Vector3 e1 = p[1] - p[0], e2 = p[2] - p[0];
        Vector2 f1 = b.Uv[1] - b.Uv[0], f2 = b.Uv[2] - b.Uv[0];
        float det = f1.X * f2.Y - f1.Y * f2.X;
        if (MathF.Abs(det) < 1e-4f) return null;
        // P - P0 = [e1 e2] F^-1 (uv - uv0), F = [f1 f2].
        var u = (e1 * f2.Y - e2 * f1.Y) / det;
        var v = (e2 * f1.X - e1 * f2.X) / det;
        return (u, v);
    }

    /// <summary>The plane frame of the selection for laying the brush on it: "right" along
    /// the brush texture's U and "down" along its V as on the triangle it came from (world
    /// up for a brush without one), both in the selection's plane.</summary>
    bool BrushFrame(List<int> idx, Brush b, out Vector3 right, out Vector3 down, out float texelU, out float texelV)
    {
        var normal = Vector3.Zero;
        foreach (int i in idx)
        {
            var v = _doc.Tris[i].V;
            var n = Vector3.Cross(v[1].Pos - v[0].Pos, v[2].Pos - v[0].Pos);
            if (Vector3.Dot(n, normal) < 0) n = -n;
            normal += n;
        }
        normal = normal.LengthSquared() > 1e-6f ? Vector3.Normalize(normal) : -_cam.Forward;
        Vector3 Flat(Vector3 a) => a - normal * Vector3.Dot(a, normal);
        texelU = texelV = 0;
        if (BrushTexelAxes(b) is { } axes && Flat(axes.U).Length() > axes.U.Length() * 0.3f
            && Flat(axes.V).Length() > axes.V.Length() * 0.3f)
        {
            texelU = axes.U.Length();
            texelV = axes.V.Length();
            right = Vector3.Normalize(Flat(axes.U));
            var d = Flat(axes.V);
            down = Vector3.Normalize(d - right * Vector3.Dot(d, right));
            return true;
        }
        var upRef = MathF.Abs(normal.Y) > 0.8f ? -Vector3.UnitZ : Vector3.UnitY;
        var up = Vector3.Normalize(upRef - normal * Vector3.Dot(upRef, normal));
        right = Vector3.Normalize(Vector3.Cross(up, normal));
        if (Vector3.Dot(right, _cam.Right) < 0) right = -right;
        down = -up;
        return false;
    }

    /// <summary>The brush tile laid once over the whole selection, in the brush's own
    /// direction. The old texture coordinates play no part.</summary>
    void ApplyBrush(bool colours)
    {
        var b = _brush;
        var idx = SelUser.ToList();
        if (b == null || idx.Count == 0) return;
        BeginChange();
        BrushFrame(idx, b, out var right, out var down, out _, out _);
        float r0 = float.MaxValue, r1 = float.MinValue, d0 = float.MaxValue, d1 = float.MinValue;
        foreach (int i in idx)
            foreach (var v in _doc.Tris[i].V)
            {
                float r = Vector3.Dot(v.Pos, right), d = Vector3.Dot(v.Pos, down);
                r0 = Math.Min(r0, r); r1 = Math.Max(r1, r); d0 = Math.Min(d0, d); d1 = Math.Max(d1, d);
            }
        float width = Math.Max(r1 - r0, 1e-3f), height = Math.Max(d1 - d0, 1e-3f);
        Vector2 blo = b.Min, bsize = b.Max - b.Min;
        foreach (int i in idx)
        {
            var t = _doc.Tris[i];
            for (int k = 0; k < 3; k++)
            {
                float s = (Vector3.Dot(t.V[k].Pos, right) - r0) / width, tt = (Vector3.Dot(t.V[k].Pos, down) - d0) / height;
                t.V[k].Uv = blo + new Vector2(s, tt) * bsize;
            }
            if (colours) for (int k = 0; k < 3; k++) t.V[k].Rgb = b.MeanRgb;
            t.Mesh = b.Mesh;
            t.Poly = b.Poly;
        }
        Changed();
    }

    int _cellsX, _cellsY;

    /// <summary>Cells the brush would need on the selected quad to keep the original's
    /// texture size (0 when the selection is not a quad).</summary>
    (int X, int Y) BrushCells()
    {
        var b = _brush;
        var idx = SelUser.ToList();
        if (b == null || !QuadOf(idx, b, out var c, out _, out _)) return (0, 0);
        BrushFrame(idx, b, out _, out _, out float tu, out float tv);
        var size = b.Max - b.Min;
        if (tu <= 0 || tv <= 0) return (1, 1);
        float width = (Vector3.Distance(c[0], c[3]) + Vector3.Distance(c[1], c[2])) / 2;
        float height = (Vector3.Distance(c[0], c[1]) + Vector3.Distance(c[3], c[2])) / 2;
        return (Math.Clamp((int)MathF.Round(width / (tu * Math.Max(size.X, 1))), 1, 64),
            Math.Clamp((int)MathF.Round(height / (tv * Math.Max(size.Y, 1))), 1, 64));
    }

    /// <summary>The 4 corners of a selection of 2 triangles forming a quad, ordered
    /// top-left, bottom-left, bottom-right, top-right in the brush frame, with their colours.</summary>
    bool QuadOf(List<int> idx, Brush b, out Vector3[] corners, out Vector3[] colours, out string mesh)
    {
        corners = []; colours = []; mesh = "";
        if (idx.Count != 2) return false;
        var pts = new List<(Vector3 P, Vector3 Rgb)>();
        foreach (int i in idx)
            foreach (var v in _doc.Tris[i].V)
                if (!pts.Any(q => Vector3.DistanceSquared(q.P, v.Pos) < 1)) pts.Add((v.Pos, v.Rgb));
        if (pts.Count != 4) return false;
        BrushFrame(idx, b, out var right, out var down, out _, out _);
        var centre = (pts[0].P + pts[1].P + pts[2].P + pts[3].P) / 4;
        // Around the centre, starting top-left, going down the left side first.
        var ordered = pts.OrderBy(q => MathF.Atan2(Vector3.Dot(q.P - centre, down), -Vector3.Dot(q.P - centre, right))).ToList();
        int start = 0;
        float best = float.MaxValue;
        for (int i = 0; i < 4; i++)
        {
            float score = Vector3.Dot(ordered[i].P - centre, right) + Vector3.Dot(ordered[i].P - centre, down);
            if (score < best) { best = score; start = i; }
        }
        var loop = Enumerable.Range(0, 4).Select(i => ordered[(start + i) % 4]).ToList();
        // Second corner must be below the first (left side), else the loop runs the other way.
        if (Vector3.Dot(loop[1].P - loop[0].P, down) < Vector3.Dot(loop[3].P - loop[0].P, down))
            loop = [loop[0], loop[3], loop[2], loop[1]];
        corners = [.. loop.Select(q => q.P)];
        colours = [.. loop.Select(q => q.Rgb)];
        mesh = _doc.Tris[idx[0]].Mesh;
        return true;
    }

    /// <summary>Replaces the selected quad with a grid of cells, the whole brush tile on
    /// each, so the texture keeps the size it has on the original (UVs past a tile would
    /// show other textures of the page).</summary>
    void ApplyBrushCells(bool colours)
    {
        var b = _brush;
        var idx = SelUser.ToList();
        if (b == null) return;
        if (!QuadOf(idx, b, out var c, out var rgb, out _))
        {
            Say("A celle serve un quad: seleziona i 2 triangoli di un rettangolo.");
            return;
        }
        var (autoX, autoY) = BrushCells();
        int nx = _cellsX > 0 ? _cellsX : autoX, ny = _cellsY > 0 ? _cellsY : autoY;
        BeginChange();
        var template = _doc.Tris[idx[0]];
        Vector3 At(Vector3[] q, float s, float t) => Vector3.Lerp(Vector3.Lerp(q[0], q[3], s), Vector3.Lerp(q[1], q[2], s), t);
        Vector2 lo = b.Min, hi = b.Max;
        var made = new List<UserTri>();
        int group = _doc.NextGroup++;
        for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                float s0 = (float)i / nx, s1 = (float)(i + 1) / nx, t0 = (float)j / ny, t1 = (float)(j + 1) / ny;
                EdVertex V(float s, float t, Vector2 uv) => new()
                {
                    Pos = At(c, s, t), Uv = uv, Rgb = colours ? b.MeanRgb : At(rgb, s, t),
                };
                var tl = V(s0, t0, lo);
                var bl = V(s0, t1, new(lo.X, hi.Y));
                var br = V(s1, t1, hi);
                var tr = V(s1, t0, new(hi.X, lo.Y));
                UserTri T(EdVertex a, EdVertex bb, EdVertex cc) => new()
                {
                    Mesh = b.Mesh, Poly = b.Poly, Behind = template.Behind, Rank = template.Rank,
                    Culled = template.Culled, Group = group, V = [a, bb, cc],
                };
                made.Add(T(tl, bl, br));
                made.Add(T(tl, br, tr));
            }
        foreach (int i in idx.OrderByDescending(i => i)) _doc.Tris.RemoveAt(i);
        _sel.Clear();
        foreach (var t in made)
        {
            _sel.Add(Key(Kind.User, _doc.Tris.Count));
            _doc.Tris.Add(t);
        }
        Changed();
        Say($"Quad diviso in {nx} x {ny} celle con la texture del pennello.");
    }
}
