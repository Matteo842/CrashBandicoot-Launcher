// Editor state: level loading, the three vertex buffers (retail scenery, automatic
// repairs, the user's own triangles), rendering like the game's 16:9 view, undo.
using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using RecompOne.Runtime.Cdrom;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;

namespace WideEditor;

enum Kind { Retail = 0, Auto = 1, User = 2 }

sealed record Brush(string Mesh, int Poly, Vector2[] Uv, Vector3[] Rgb, Vector3[]? Pos = null)
{
    public Vector2 Min => Vector2.Min(Uv[0], Vector2.Min(Uv[1], Uv[2]));
    public Vector2 Max => Vector2.Max(Uv[0], Vector2.Max(Uv[1], Uv[2]));
    public Vector3 MeanRgb => (Rgb[0] + Rgb[1] + Rgb[2]) / 3;
}

sealed partial class Editor : IDisposable
{
    readonly IWindow _window;
    readonly GL _gl;
    readonly ImGuiController _imgui;
    readonly Gfx _gfx;
    enum Screen { Pick, Loading, Edit }
    Screen _screen = Screen.Pick;

    // Disc and level list.
    string? _discPath;
    CueFs? _disc;
    string? _discError;
    readonly List<(int Id, string Name, string Kind)> _levels = [];
    List<SaveSlot> _slots = [];
    Task<(LevelData Level, AutoRepairs.Result Auto)>? _loading;
    string _loadingName = "";
    SaveSlot? _pendingSlot;

    // Loaded level.
    LevelData? L;
    AutoRepairs.Result? _auto;
    EditDoc _doc = new();
    readonly List<EditDoc> _undo = [], _redo = [];
    bool _dirty, _changeOpen;
    string _status = "";
    double _statusTime;
    VertexBuffer? _retailBuf, _autoBuf, _userBuf;
    int[] _meshBase = [];
    int _retailCount;
    readonly Dictionary<Mesh, (int Start, int Count)> _retailRange = [], _autoRange = [], _userRange = [];
    bool _retailDirty, _autoDirty, _userDirty;

    // View.
    readonly Camera _cam = new();
    readonly float _ui;
    readonly Prefs _prefs;
    string? _prefsJson;
    double _prefsCheck;
    float _text = 1;
    bool _follow, _markHidden = true, _ghostCore = true, _showOff, _flatLight;
    float _viewBright = 1;
    Zone? _zone;
    bool _lockZone;
    bool _gameView = true, _void = true, _showAuto = true, _showUser = true, _tintAuto, _tintUser = true;
    bool _wire, _allMeshes;
    float _speed = 1;
    Rect _view;
    float _fbScale = 1;
    List<Mesh> _drawMeshes = [];
    uint _hoverId;
    Vector4 _hoverPos;
    double _time;

    public Editor(IWindow window, string[] args)
    {
        int gotoArg = Array.IndexOf(args, "--goto");
        if (gotoArg >= 0 && gotoArg + 1 < args.Length) _gotoFile = args[gotoArg + 1];
        _window = window;
        _gl = GL.GetApi(window);
        var input = window.CreateInput();
        _keyboard = input.Keyboards.Count > 0 ? input.Keyboards[0] : null;
        _mouse = input.Mice.Count > 0 ? input.Mice[0] : null;
        // Interface size from the screen: 1080p = 1, 1440p = 1.33, 4K = 2.
        int screenHeight = window.Monitor?.VideoMode.Resolution?.Y ?? window.FramebufferSize.Y;
        _ui = Math.Clamp(screenHeight / 1080f, 1, 2.5f);
        string font = @"C:\Windows\Fonts\segoeui.ttf";
        float ui = _ui;
        _imgui = new ImGuiController(_gl, window, new ImGuiInputContext(input),
            File.Exists(font) ? new ImGuiFontConfig(font, (int)MathF.Round(20 * ui)) : null, () => ConfigureImGui(ui));
        _gfx = new Gfx(_gl);
        LoadLevelList();
        SetEditorMarker(true);
        _prefs = Prefs.Load();
        ApplyPrefs();
        _discPath = Env.DiscPath(_prefs.Disc);
        OpenDisc();
        _slots = SaveSlot.All();
    }

    static void ConfigureImGui(float ui)
    {
        var io = ImGui.GetIO();
        unsafe { io.NativePtr->IniFilename = null; }
        ApplyTheme();
        ImGui.GetStyle().ScaleAllSizes(ui);
        LoadIconFont(ui);
    }

    void LoadLevelList()
    {
        using var stream = typeof(CueFs).Assembly.GetManifestResourceStream("RecompOne.Runtime.Catalog.data.levels.scus94900.json");
        if (stream == null) return;
        using var doc = JsonDocument.Parse(stream);
        foreach (var e in doc.RootElement.GetProperty("levels").EnumerateArray())
        {
            string kind = e.GetProperty("kind").GetString() ?? "";
            if (kind is not ("gameplay" or "bonus")) continue;
            _levels.Add((e.GetProperty("id").GetInt32(), e.GetProperty("name").GetString() ?? "", kind));
        }
    }

    void OpenDisc()
    {
        _disc?.Dispose();
        _disc = null;
        _discError = null;
        if (_discPath == null) { _discError = "Nessun disco trovato: sceglilo."; return; }
        try
        {
            string path = _discPath;
            if (Path.GetExtension(path).Equals(".bin", StringComparison.OrdinalIgnoreCase)
                && File.Exists(Path.ChangeExtension(path, ".cue")))
                path = Path.ChangeExtension(path, ".cue");
            _disc = CueFs.Open(path);
            _disc.ReadFileWithoutOverlay("SCUS_949.00");
        }
        catch (Exception e)
        {
            _discError = $"Disco non leggibile: {e.Message}";
            _disc?.Dispose();
            _disc = null;
        }
    }

    void StartLoad(int id, string name, SaveSlot? slot = null)
    {
        if (_disc == null) return;
        var disc = _disc;
        _loadingName = name;
        _pendingSlot = slot;
        _screen = Screen.Loading;
        _loading = Task.Run(() =>
        {
            var level = LevelData.Load(disc, id, name);
            return (level, AutoRepairs.Compute(level));
        });
    }

    void FinishLoad()
    {
        if (_loading == null || !_loading.IsCompleted) return;
        var task = _loading;
        _loading = null;
        if (task.IsFaulted)
        {
            _screen = Screen.Pick;
            _discError = $"Errore nel caricare il livello: {task.Exception?.GetBaseException().Message}";
            return;
        }
        (L, _auto) = task.Result;
        _gfx.UploadPages(L);
        _retailBuf ??= new VertexBuffer(_gl);
        _autoBuf ??= new VertexBuffer(_gl);
        _userBuf ??= new VertexBuffer(_gl);
        _doc = LoadDoc(L);
        _undo.Clear();
        _redo.Clear();
        _dirty = false;
        _sel.Clear();
        _brush = null;
        _points.Clear();
        _retailDirty = _autoDirty = _userDirty = true;
        _autoPieces = null;
        _zone = null;
        _lockZone = false;
        var first = L.Zones.Values.FirstOrDefault(z => z.Paths.Any(p => p.Points.Count > 0));
        if (first != null)
        {
            _zone = first;
            _pathZone = first.Name;
            _pathIndex = 0;
            _pathPoint = 0;
            var pt = first.Paths.First(p => p.Points.Count > 0).Points[0];
            _cam.SetPathPoint(pt.Pos, pt.Pitch, pt.Yaw);
            _pathIndex = first.Paths.FindIndex(p => p.Points.Count > 0);
        }
        if (_prefs.Cameras.TryGetValue(L.Id.ToString(), out var saved) && saved.Pos.Length == 3)
        {
            _cam.Pos = new(saved.Pos[0], saved.Pos[1], saved.Pos[2]);
            _cam.Pitch = saved.Pitch;
            _cam.Yaw = saved.Yaw;
            if (saved.Zone != null && L.Zones.TryGetValue(saved.Zone, out var zone))
            {
                _zone = zone;
                _pathZone = zone.Name;
                _lockZone = saved.LockZone;
            }
        }
        if (_pendingSlot is { } slot) ApplySlot(slot);
        _pendingSlot = null;
        _screen = Screen.Edit;
        if (_auto.Error != null) Say($"Riparazioni automatiche non calcolate: {_auto.Error}");
        else Say($"{L.Name}: {L.Meshes.Count} mesh, {_auto.Tris.Count} triangoli automatici, {_doc.Tris.Count} tuoi.");
    }

    static EditDoc LoadDoc(LevelData level)
    {
        string? file = File.Exists(Env.EditsFile(level.Id)) ? Env.EditsFile(level.Id)
            : Env.LiveFiles(level.Id).FirstOrDefault(File.Exists);
        if (file == null) return new EditDoc();
        try { return EditDoc.FromJson(File.ReadAllText(file), level); }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return new EditDoc();
        }
    }

    void Save()
    {
        if (L == null) return;
        string json = _doc.ToJson(L);
        var written = new List<string>();
        foreach (var file in Env.LiveFiles(L.Id).Prepend(Env.EditsFile(L.Id)))
        {
            if (_doc.Empty && !File.Exists(file)) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, json);
                written.Add(file);
            }
            catch (IOException e) { Say($"Non salvato in {file}: {e.Message}"); return; }
        }
        _dirty = false;
        Say(written.Count > 0 ? $"Salvato ({_doc.Tris.Count} triangoli). Il gioco aperto lo ricarica da solo."
            : "Niente da salvare.");
    }

    void Say(string text)
    {
        _status = text;
        _statusTime = _time;
    }

    // ---- Undo ----

    void PushUndo()
    {
        _undo.Add(_doc.Clone());
        if (_undo.Count > 300) _undo.RemoveAt(0);
        _redo.Clear();
        _dirty = true;
    }

    /// <summary>First change of a drag or of a held widget: one undo step for all of it.</summary>
    void BeginChange()
    {
        if (_changeOpen) return;
        PushUndo();
        _changeOpen = true;
    }

    void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Add(_doc);
        _doc = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        AfterDocSwap();
        Say("Annullato.");
    }

    void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(_doc);
        _doc = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        AfterDocSwap();
        Say("Ripristinato.");
    }

    void AfterDocSwap()
    {
        _dirty = true;
        _sel.RemoveWhere(k => KindOf(k) == Kind.User && IndexOf(k) >= _doc.Tris.Count);
        _retailDirty = _autoDirty = _userDirty = true;
    }

    // ---- Buffers ----

    static uint PickId(Kind kind, int index) => ((uint)kind + 1) << 24 | (uint)index;

    bool RuntimeHidden(Mesh m, int poly) => _auto != null && _auto.HiddenSides.TryGetValue(m, out var h) && h.Contains(poly);

    void RebuildBuffers()
    {
        if (L == null || _auto == null) return;
        if (_retailDirty)
        {
            _retailDirty = false;
            var list = new List<GpuVertex>(L.Meshes.Sum(m => m.PolyCount) * 3);
            _retailRange.Clear();
            _meshBase = new int[L.Meshes.Count];
            int global = 0;
            foreach (var m in L.Meshes)
            {
                _meshBase[m.Index] = global;
                int start = list.Count;
                for (int p = 0; p < m.PolyCount; p++)
                {
                    var mat = L.MaterialOf(m, p);
                    var uv = L.UvsOf(m, p);
                    if (_doc.Retex.TryGetValue((m.Name, p), out var retex) && retex.Source < m.PolyCount)
                    {
                        // As the runtime: corner slot k (vertex (k + turn) % 3) carries texture corner k.
                        mat = L.MaterialOf(m, retex.Source);
                        var su = L.UvsOf(m, retex.Source);
                        uv = [.. Enumerable.Range(0, 3).Select(j => su[(j - retex.Turn % 3 + 3) % 3])];
                    }
                    bool removed = _doc.Removed.Contains((m.Name, p));
                    if (removed && !_showOff) continue;
                    bool hidden = removed || _doc.Hidden.Contains((m.Name, p)) || RuntimeHidden(m, p);
                    var (info, clut) = Bits.Of(mat, (int)Kind.Retail, hidden, false, 0, m.Backdrop);
                    uint id = PickId(Kind.Retail, global + p);
                    for (int k = 0; k < 3; k++)
                    {
                        int vi = m.Tri[p * 3 + k];
                        list.Add(new GpuVertex
                        {
                            Pos = m.Backdrop ? m.Pos[vi] - m.Trans : m.Pos[vi], Uv = uv[k], Rgb = m.Rgb[vi],
                            Info = info, Clut = clut, Id = id,
                        });
                    }
                }
                global += m.PolyCount;
                _retailRange[m] = (start, list.Count - start);
            }
            _retailCount = global;
            _retailBuf!.Upload(list);
        }
        if (_autoDirty)
        {
            _autoDirty = false;
            var list = new List<GpuVertex>(_auto.Tris.Count * 3);
            _autoRange.Clear();
            var repairOff = RepairOffTest();
            foreach (var m in L.Meshes)
            {
                int start = list.Count;
                if (_auto.Ranges.TryGetValue(m, out var range))
                    for (int i = range.Start; i < range.Start + range.Count; i++)
                    {
                        var t = _auto.Tris[i];
                        bool off = repairOff(t);
                        if (off && !_showOff) continue;
                        var mat = t.Poly >= 0 && t.Poly < m.PolyCount ? L.MaterialOf(m, t.Poly) : default;
                        var (info, clut) = Bits.Of(mat, (int)Kind.Auto, off, t.Behind, t.Rank, m.Backdrop);
                        uint id = PickId(Kind.Auto, i);
                        foreach (var v in new[] { t.A, t.B, t.C })
                            list.Add(new GpuVertex
                            {
                                Pos = m.Backdrop ? v.Pos - m.Trans : v.Pos, Uv = v.Uv, Rgb = v.Rgb,
                                Info = info, Clut = clut, Id = id,
                            });
                    }
                _autoRange[m] = (start, list.Count - start);
            }
            _autoBuf!.Upload(list);
        }
        if (_userDirty)
        {
            _userDirty = false;
            var list = new List<GpuVertex>(_doc.Tris.Count * 3);
            _userRange.Clear();
            foreach (var group in Enumerable.Range(0, _doc.Tris.Count).GroupBy(i => _doc.Tris[i].Mesh))
            {
                if (!L.MeshByName.TryGetValue(group.Key, out var m)) continue;
                int start = list.Count;
                foreach (int i in group)
                {
                    var t = _doc.Tris[i];
                    var mat = t.Poly >= 0 && t.Poly < m.PolyCount ? L.MaterialOf(m, t.Poly) : default;
                    var (info, clut) = Bits.Of(mat, (int)Kind.User, false, t.Behind, t.Rank, m.Backdrop);
                    uint id = PickId(Kind.User, i);
                    foreach (var v in t.V)
                        list.Add(new GpuVertex
                        {
                            Pos = m.Backdrop ? v.Pos - m.Trans : v.Pos, Uv = v.Uv, Rgb = v.Rgb,
                            Info = info, Clut = clut, Id = id,
                        });
                }
                _userRange[m] = (start, list.Count - start);
            }
            _userBuf!.Upload(list);
        }
    }

    void UpdateDrawMeshes()
    {
        if (L == null) return;
        if (!_lockZone && L.ZoneAt(_cam.Pos) is { } z) _zone = z;
        if (_allMeshes || _zone == null)
        {
            var own = _zone != null ? L.ZoneMeshes(_zone).Where(m => m.Backdrop).ToList() : [];
            _drawMeshes = [.. L.Meshes.Where(m => !m.Backdrop), .. own];
        }
        else _drawMeshes = L.ZoneMeshes(_zone);
    }

    bool OwnMesh(Mesh m) => _zone == null || _allMeshes || _zone.Worlds.Contains(m.Name);

    // ---- Rendering ----

    void DrawRetail(bool backdrops)
    {
        foreach (var m in _drawMeshes)
            if (m.Backdrop == backdrops && _retailRange.TryGetValue(m, out var r)) _retailBuf!.Draw(r.Start, r.Count);
    }

    void DrawUserOnly()
    {
        foreach (var m in _drawMeshes)
            if (_userRange.TryGetValue(m, out var u)) _userBuf!.Draw(u.Start, u.Count);
    }

    /// <summary>Repairs and user triangles; those of backdrops (sky extensions) only
    /// with <paramref name="backdrops"/>, since the game draws them under everything.</summary>
    void DrawExtras() => DrawExtras(false);

    void DrawExtras(bool backdrops)
    {
        foreach (var m in _drawMeshes)
        {
            if (m.Backdrop != backdrops) continue;
            if (_showAuto && (!_gameView || OwnMesh(m) || _auto!.NeighbourRepairs) && _autoRange.TryGetValue(m, out var a))
                _autoBuf!.Draw(a.Start, a.Count);
            if (_showUser && _userRange.TryGetValue(m, out var u)) _userBuf!.Draw(u.Start, u.Count);
        }
    }

    void Passes(Action draw, bool depth = true)
    {
        _gfx.Pass(0);
        draw();
        _gfx.Depth(depth, false);
        for (int abr = 0; abr < 4; abr++)
        {
            _gfx.Pass(1, abr);
            draw();
        }
        _gfx.Pass(0);
        _gfx.Depth(depth, depth);
    }

    void RenderScene()
    {
        if (L == null) return;
        int w = (int)(_view.W * _fbScale), h = (int)(_view.H * _fbScale);
        _gfx.EnsureTarget(w, h);
        w = _gfx.Width;
        h = _gfx.Height;
        _gfx.Begin(_cam.View, Camera.Proj, _cam.Pos, Vector3.Zero);
        _gfx.Light(_flatLight, _viewBright);
        int tint = (_tintAuto ? 1 : 0) | (_tintUser ? 2 : 0) | (!_gameView && _markHidden ? 4 : 0);

        _gfx.State(tint, false, -1);
        _gfx.Depth(false, false);
        Passes(() => DrawRetail(true), depth: false);
        // Sky extensions: no depth, under all scenery, like the game's backdrop prims
        // (in the game view only in the side bands).
        if (_gameView)
        {
            int cw = (int)MathF.Round(w * Camera.CoreFraction), cx = (w - cw) / 2;
            foreach (var (x, bw) in new[] { (0, cx), (cx + cw, w - cx - cw) })
            {
                _gfx.Scissor(x, 0, bw, h);
                Passes(() => DrawExtras(true), depth: false);
            }
            _gfx.NoScissor();
        }
        else Passes(() => DrawExtras(true), depth: false);
        _gfx.Depth(true, true);

        if (_gameView)
        {
            int coreW = (int)MathF.Round(w * Camera.CoreFraction), coreX = (w - coreW) / 2;
            _gfx.Scissor(coreX, 0, coreW, h);
            _gfx.State(tint, false, -1);
            Passes(() => DrawRetail(false));
            if (_ghostCore && _showUser)
            {
                _gfx.Ghost(true);
                Passes(DrawUserOnly);
                _gfx.Ghost(false);
            }
            foreach (var (x, bw) in new[] { (0, coreX), (coreX + coreW, w - coreX - coreW) })
            {
                _gfx.Scissor(x, 0, bw, h);
                _gfx.State(tint, true, 1);
                Passes(DrawExtras);
                _gfx.ClearDepth();
                _gfx.State(tint, true, 0);
                Passes(() => { DrawRetail(false); DrawExtras(); });
            }
            _gfx.NoScissor();
        }
        else
        {
            // Everything everywhere, still layered like the side bands: fills
            // behind the scenery first, then the scenery over them.
            _gfx.State(tint, false, 1);
            Passes(DrawExtras);
            _gfx.ClearDepth();
            _gfx.State(tint, false, 0);
            Passes(() => { DrawRetail(false); DrawExtras(); });
        }
        if (_wire)
        {
            _gfx.NoScissor();
            _gfx.State(0, false, -1);
            _gfx.Depth(true, false);
            _gfx.Wire(true);
            _gfx.Pass(0);
            DrawRetail(false);
            DrawExtras();
            _gfx.Wire(false);
            _gfx.Depth(true, true);
        }
        if (_void) _gfx.VoidPass(new Vector3(1, 0, 1));
        _gfx.End();
        var size = _window.FramebufferSize;
        _gfx.Blit((int)(_view.X * _fbScale), size.Y - (int)(_view.Y * _fbScale) - h, w, h);

        var mouse = ImGui.GetIO().MousePos;
        if (_view.Contains(mouse))
        {
            var (fx, fy) = ToTarget(mouse);
            _hoverId = _gfx.ReadId(fx, fy);
            _hoverPos = _gfx.ReadPos(fx, fy);
        }
        else
        {
            _hoverId = 0;
            _hoverPos = default;
        }
    }

    (int X, int Y) ToTarget(Vector2 p) =>
        ((int)((p.X - _view.X) * _fbScale), _gfx.Height - 1 - (int)((p.Y - _view.Y) * _fbScale));

    // ---- Frame ----

    public void Frame(double dt)
    {
        _time += dt;
        if (_time - _prefsCheck > 2)
        {
            _prefsCheck = _time;
            SavePrefs();
        }
        _imgui.Update((float)dt);
        ImGui.GetIO().FontGlobalScale = _text;
        var size = _window.FramebufferSize;
        var io = ImGui.GetIO();
        _fbScale = io.DisplaySize.X > 0 ? size.X / io.DisplaySize.X : 1;
        _gl.Viewport(0, 0, (uint)size.X, (uint)size.Y);
        _gl.ClearColor(0.15f, 0.15f, 0.15f, 1);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        try
        {
            PollGoto();
            switch (_screen)
            {
                case Screen.Pick: PickScreen(); break;
                case Screen.Loading:
                    LoadingScreen();
                    FinishLoad();
                    break;
                case Screen.Edit:
                    Layout();
                    FollowGame();
                    UpdateDrawMeshes();
                    ViewInput((float)dt);
                    RebuildBuffers();
                    RenderScene();
                    Panels();
                    Overlay();
                    if (!ImGui.IsAnyItemActive() && _drag == DragMode.None) _changeOpen = false;
                    EndKeys();
                    break;
            }
        }
        catch (Exception e)
        {
            Say($"Errore: {e.Message}");
            try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "wideeditor.log"), $"{DateTime.Now}: {e}\n"); }
            catch (IOException) { }
        }
        // The 3D pass leaves its own viewport and framebuffer bound: ImGui draws
        // over the whole window.
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)size.X, (uint)size.Y);
        _gl.Disable(EnableCap.ScissorTest);
        _imgui.Render();
    }

    void Layout()
    {
        var io = ImGui.GetIO();
        float left = ToolWidth, right = RightWidth, top = TopHeight, bottom = StatusHeight;
        float aw = Math.Max(io.DisplaySize.X - left - right, 64), ah = Math.Max(io.DisplaySize.Y - top - bottom, 36);
        float w = aw, h = w * 9 / 16;
        if (h > ah) { h = ah; w = h * 16 / 9; }
        _view = new Rect(MathF.Floor(left + (aw - w) / 2), MathF.Floor(top + (ah - h) / 2), MathF.Floor(w), MathF.Floor(h));
    }

    public void Close()
    {
        if (_dirty) Save();
        SavePrefs();
        SetEditorMarker(false);
    }

    /// <summary>Tells a running game to write its camera (FramePacing.NativeWideEditorCameraSync).</summary>
    static void SetEditorMarker(bool open)
    {
        foreach (var dir in Env.GameDirs())
            try
            {
                string file = Path.Combine(dir, "WideEdits", "editor_open");
                if (open)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllText(file, "");
                }
                else if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    double _followRead;
    string _followNote = "";

    /// <summary>Camera of the running game, written by it ten times a second.</summary>
    void FollowGame()
    {
        if (!_follow || L == null || _time - _followRead < 0.05) return;
        _followRead = _time;
        string? file = Env.GameDirs().Select(d => Path.Combine(d, "WideEdits", "camera.json")).Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (file == null || (DateTime.UtcNow - File.GetLastWriteTimeUtc(file)).TotalSeconds > 3)
        {
            _followNote = "Il gioco non manda la camera: aprilo in 16:9, dentro un livello.";
            return;
        }
        try
        {
            using var d = JsonDocument.Parse(File.ReadAllText(file));
            var r = d.RootElement;
            int level = r.GetProperty("level").GetInt32();
            if (level != L.Id)
            {
                _followNote = $"Il gioco è nel livello {level}, l'editor nel {L.Id}.";
                return;
            }
            var cam = r.GetProperty("cam");
            var matrix = r.GetProperty("matrix").EnumerateArray().Select(e => (short)e.GetInt32()).ToArray();
            _cam.SetMatrix(new(cam[0].GetSingle(), cam[1].GetSingle(), cam[2].GetSingle()), matrix);
            string zone = Eid.Str(r.GetProperty("zone").GetUInt32());
            if (L.Zones.TryGetValue(zone, out var z))
            {
                _zone = z;
                _lockZone = true;
            }
            _followNote = $"Segue il gioco (zona {zone}).";
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or KeyNotFoundException
            or FormatException) { }
    }

    void ApplyPrefs()
    {
        var p = _prefs;
        _text = Math.Clamp(p.Text, 0.7f, 2f);
        _gameView = p.GameView; _void = p.Void; _showAuto = p.ShowAuto; _showUser = p.ShowUser;
        _tintAuto = p.TintAuto; _tintUser = p.TintUser; _wire = p.Wire; _allMeshes = p.AllMeshes;
        _speed = Math.Clamp(p.Speed, 0.1f, 5f);
        _snapVerts = p.SnapVerts; _weld = p.Weld; _grid = Math.Max(p.Grid, 0); _rotStep = Math.Max(p.RotStep, 0);
        _newBehind = p.NewBehind; _pickRetail = p.PickRetail; _pickAuto = p.PickAuto; _pickUser = p.PickUser;
        _gizmo = Enum.TryParse<GizmoMode>(p.GizmoMode, out var g) ? g : GizmoMode.All;
        _showOff = p.ShowOff;
        _flatLight = p.FlatLight;
        _viewBright = Math.Clamp(p.ViewBright, 0.5f, 4f);
        _markHidden = p.MarkHidden;
        _ghostCore = p.GhostCore;
        _boxThrough = p.BoxThrough;
        _extendLone = p.ExtendLone;
        _bridgeCurve = p.BridgeCurve;
        _sculptMode = Enum.TryParse<SculptMode>(p.SculptMode, out var sm) ? sm : SculptMode.Smooth;
        _sculptRadius = Math.Clamp(p.SculptRadius, 50, 8000);
        _sculptStrength = Math.Clamp(p.SculptStrength, 0.05f, 2);
        _sculptAutos = p.SculptAutos;
        _sculptLockOriginals = p.SculptLockOriginals;
        _extendSteps = Math.Clamp(p.ExtendSteps, 1, 30);
        _extendSlope = Enum.TryParse<ExtendSlope>(p.ExtendSlope, out var es) ? es : ExtendSlope.Fade;
        _extendSide = Enum.TryParse<ExtendSide>(p.ExtendSide, out var ed) ? ed : ExtendSide.Bands;
    }

    /// <summary>Writes the settings file when anything in it changed.</summary>
    void SavePrefs()
    {
        var p = _prefs;
        p.Disc = _discPath;
        p.Text = _text;
        p.GameView = _gameView; p.Void = _void; p.ShowAuto = _showAuto; p.ShowUser = _showUser;
        p.TintAuto = _tintAuto; p.TintUser = _tintUser; p.Wire = _wire; p.AllMeshes = _allMeshes; p.Speed = _speed;
        p.SnapVerts = _snapVerts; p.Weld = _weld; p.Grid = _grid; p.RotStep = _rotStep;
        p.NewBehind = _newBehind; p.PickRetail = _pickRetail; p.PickAuto = _pickAuto; p.PickUser = _pickUser;
        p.GizmoMode = _gizmo.ToString();
        p.ShowOff = _showOff;
        p.FlatLight = _flatLight;
        p.ViewBright = _viewBright;
        p.MarkHidden = _markHidden;
        p.GhostCore = _ghostCore;
        p.BoxThrough = _boxThrough;
        p.ExtendLone = _extendLone;
        p.BridgeCurve = _bridgeCurve;
        p.SculptMode = _sculptMode.ToString();
        p.SculptRadius = _sculptRadius;
        p.SculptStrength = _sculptStrength;
        p.SculptAutos = _sculptAutos;
        p.SculptLockOriginals = _sculptLockOriginals;
        p.ExtendSteps = _extendSteps;
        p.ExtendSlope = _extendSlope.ToString();
        p.ExtendSide = _extendSide.ToString();
        if (L != null && _screen == Screen.Edit)
            p.Cameras[L.Id.ToString()] = new CameraPref
            {
                Pos = [_cam.Pos.X, _cam.Pos.Y, _cam.Pos.Z], Pitch = _cam.Pitch, Yaw = _cam.Yaw,
                Zone = _zone?.Name, LockZone = _lockZone,
            };
        string json = p.Json();
        if (json == _prefsJson) return;
        _prefsJson = json;
        Prefs.Write(json);
    }

    public void Dispose()
    {
        _retailBuf?.Dispose();
        _autoBuf?.Dispose();
        _userBuf?.Dispose();
        _gfx.Dispose();
        _imgui.Dispose();
        _disc?.Dispose();
    }
}
