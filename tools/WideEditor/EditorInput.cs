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
    Vector3? _snapHit;

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
        if (_tool is Tool.Quad or Tool.Triangle && _snapVerts)
        {
            EnsureSnapTargets([]);
            if (FindSnap(mouse, 12, out var p)) _snapHit = p;
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
        if (_snapVerts && FindSnap(mouse, 12, out var target))
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
        Vector3 p;
        if (_snapVerts && _snapHit is { } snap) p = snap;
        else if (_hoverPos.W > 0.5f) p = new(_hoverPos.X, _hoverPos.Y, _hoverPos.Z);
        else p = _cam.AtDepth(mouse, _view, _points.Count > 0 ? _cam.Depth(_points[^1]) : 2000);
        _points.Add(p);
        if (_points.Count == (_tool == Tool.Quad ? 4 : 3))
        {
            CreateFromPoints();
            _points.Clear();
        }
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
                _brush = new(m.Name, p, L.UvsOf(m, p), [.. Enumerable.Range(0, 3).Select(k => m.Rgb[m.Tri[p * 3 + k]])]);
                break;
            }
            case Kind.Auto:
            {
                var t = _auto!.Tris[index];
                if (t.Poly < 0 || t.Mesh.Backdrop) { Say("Questo triangolo non ha una texture utilizzabile."); return; }
                _brush = new(t.Mesh.Name, t.Poly, [t.A.Uv, t.B.Uv, t.C.Uv], [t.A.Rgb, t.B.Rgb, t.C.Rgb]);
                break;
            }
            default:
            {
                var t = _doc.Tris[index];
                _brush = new(t.Mesh, t.Poly, [.. t.V.Select(v => v.Uv)], [.. t.V.Select(v => v.Rgb)]);
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
        var size = Vector2.Max(hi - lo, new Vector2(1e-3f));
        foreach (int i in idx)
            for (int k = 0; k < 3; k++)
            {
                var st = (_doc.Tris[i].V[k].Uv - lo) / size;
                _doc.Tris[i].V[k].Uv = lo + f(st) * size;
            }
        Changed();
    }

    void ApplyBrush(bool colours)
    {
        var b = _brush;
        var idx = SelUser.ToList();
        if (b == null || idx.Count == 0) return;
        PushUndo();
        Vector2 lo = new(float.MaxValue), hi = new(float.MinValue);
        foreach (int i in idx)
            foreach (var v in _doc.Tris[i].V) { lo = Vector2.Min(lo, v.Uv); hi = Vector2.Max(hi, v.Uv); }
        var size = Vector2.Max(hi - lo, new Vector2(1e-3f));
        Vector2 blo = b.Min, bsize = b.Max - b.Min;
        foreach (int i in idx)
        {
            var t = _doc.Tris[i];
            if (idx.Count == 1)
                for (int k = 0; k < 3; k++) t.V[k].Uv = b.Uv[k];
            else
                for (int k = 0; k < 3; k++) t.V[k].Uv = blo + (t.V[k].Uv - lo) / size * bsize;
            if (colours) for (int k = 0; k < 3; k++) t.V[k].Rgb = idx.Count == 1 ? b.Rgb[k] : b.MeanRgb;
            t.Mesh = b.Mesh;
            t.Poly = b.Poly;
        }
        Changed();
    }
}
