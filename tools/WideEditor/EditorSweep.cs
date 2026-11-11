// "Mai visti dalla camera": the level is rendered as in the game from every point of
// every camera path (all zones), and the automatic repairs and own triangles that never
// reach a pixel are selected, ready to be deleted with Canc.
using System.Diagnostics;
using System.Numerics;
using ImGuiNET;

namespace WideEditor;

sealed partial class Editor
{
    sealed class Sweep
    {
        public readonly List<(Zone Zone, int Path, int Point)> Points = [];
        public int Done;
        public readonly HashSet<uint> Seen = [];
        // View state given back at the end.
        public Vector3 Pos;
        public float Pitch, Yaw;
        public Zone? Zone;
        public bool LockZone, GameView, ShowAuto, ShowUser, ShowOff, AllMeshes;
    }

    Sweep? _sweep;
    // Field of view widened by this much, so pieces just past the screen edge (camera
    // between two path points, small shakes) are kept.
    float _sweepWiden = 1.1f;
    const int SweepW = 1920, SweepH = 1080;

    void StartSweep()
    {
        if (L == null || _auto == null) return;
        var s = new Sweep
        {
            Pos = _cam.Pos, Pitch = _cam.Pitch, Yaw = _cam.Yaw, Zone = _zone, LockZone = _lockZone,
            GameView = _gameView, ShowAuto = _showAuto, ShowUser = _showUser, ShowOff = _showOff, AllMeshes = _allMeshes,
        };
        foreach (var z in L.Zones.Values)
            for (int p = 0; p < z.Paths.Count; p++)
                for (int i = 0; i < z.Paths[p].Points.Count; i++)
                    s.Points.Add((z, p, i));
        if (s.Points.Count == 0)
        {
            Say("Questo livello non ha percorsi camera.");
            return;
        }
        // As the game draws it: switched-off pieces are not there and hide nothing.
        if (_showOff) _retailDirty = _autoDirty = true;
        _gameView = _showAuto = _showUser = true;
        _showOff = _allMeshes = false;
        _sweep = s;
    }

    void SweepFrame()
    {
        var s = _sweep!;
        var clock = Stopwatch.StartNew();
        _gfx.SweepIds = true;
        try
        {
            while (s.Done < s.Points.Count && clock.ElapsedMilliseconds < 40)
            {
                var (z, p, i) = s.Points[s.Done++];
                var pt = z.Paths[p].Points[i];
                _cam.SetPathPoint(pt.Pos, pt.Pitch, pt.Yaw);
                _zone = z;
                _lockZone = true;
                UpdateDrawMeshes();
                RenderScene(SweepW, SweepH, _sweepWiden);
                uint last = 0;
                foreach (uint id in _gfx.ReadIds(0, 0, _gfx.Width, _gfx.Height))
                    if (id != last) s.Seen.Add(last = id);
            }
        }
        finally { _gfx.SweepIds = false; }

        bool cancel = false;
        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(vp.GetCenter(), ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(420 * _ui, 0));
        ImGui.Begin("Mai visti dalla camera", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings);
        ImGui.TextWrapped("Guardo il livello da ogni punto di ogni percorso camera...");
        ImGui.ProgressBar((float)s.Done / s.Points.Count, new Vector2(-1, 0), $"{s.Done} / {s.Points.Count}");
        if (ImGui.Button("Annulla")) cancel = true;
        ImGui.End();

        if (cancel) EndSweep(false);
        else if (s.Done >= s.Points.Count) EndSweep(true);
    }

    void EndSweep(bool select)
    {
        var s = _sweep!;
        _sweep = null;
        _cam.Pos = s.Pos;
        _cam.Pitch = s.Pitch;
        _cam.Yaw = s.Yaw;
        _zone = s.Zone;
        _lockZone = s.LockZone;
        _gameView = s.GameView;
        _showAuto = s.ShowAuto;
        _showUser = s.ShowUser;
        _allMeshes = s.AllMeshes;
        if (s.ShowOff) _retailDirty = _autoDirty = true;
        _showOff = s.ShowOff;
        if (!select)
        {
            Say("Controllo annullato.");
            return;
        }

        // See-through pieces leave no id (what is under them keeps it): they stay.
        bool Semi(Mesh m, int poly) => poly >= 0 && poly < m.PolyCount && L!.MaterialOf(m, poly).Semi;
        var off = RepairOffTest();
        // An automatic is switched off by polygon + centre (HiddenRepair), which also
        // switches off any other automatic lying on it: a covered copy of a seen one stays.
        var seenAt = new Dictionary<(Mesh, int), List<Vector3>>();
        for (int i = 0; i < _auto!.Tris.Count; i++)
        {
            if (!s.Seen.Contains(PickId(Kind.Auto, i))) continue;
            var t = _auto.Tris[i];
            if (!seenAt.TryGetValue((t.Mesh, t.Poly), out var list)) seenAt[(t.Mesh, t.Poly)] = list = [];
            list.Add(t.Center);
        }
        bool TwinSeen(AutoTri t) => seenAt.TryGetValue((t.Mesh, t.Poly), out var list)
            && list.Exists(c => Vector3.DistanceSquared(c, t.Center) < 4);
        int autos = 0, mine = 0;
        _sel.Clear();
        for (int i = 0; i < _auto.Tris.Count; i++)
        {
            var t = _auto.Tris[i];
            if (off(t) || Semi(t.Mesh, t.Poly) || s.Seen.Contains(PickId(Kind.Auto, i)) || TwinSeen(t)) continue;
            _sel.Add(Key(Kind.Auto, i));
            autos++;
        }
        for (int i = 0; i < _doc.Tris.Count; i++)
        {
            var t = _doc.Tris[i];
            if (!L!.MeshByName.TryGetValue(t.Mesh, out var m) || Semi(m, t.Poly)
                || s.Seen.Contains(PickId(Kind.User, i))) continue;
            _sel.Add(Key(Kind.User, i));
            mine++;
        }
        Say(autos + mine == 0
            ? "Tutto quello che non è originale si vede da almeno un punto camera."
            : $"Mai visti: {autos} automatici e {mine} tuoi, ora selezionati. Canc per eliminarli.");
    }
}
