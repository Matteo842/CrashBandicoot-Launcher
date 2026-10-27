using System.Numerics;
using System.Text.Json;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    // Hand-made 16:9 fixes from the Wide Editor (tools/WideEditor), one JSON file
    // per level: extra triangles textured like a polygon of their mesh, retail
    // polygons hidden in the side bands, and automatic repairs switched off.
    // The built-in copy (WideEdits/level_NN.json, embedded) is replaced by a loose
    // WideEdits/level_NN.json next to the executable, re-read when it changes, so
    // an edit saved in the editor shows in the running game.
    sealed class NativeWideUserMesh
    {
        public readonly List<NativeWideRepair> Triangles = [];
        public readonly List<int> Hidden = [];
        public readonly HashSet<int> Removed = [];
        public readonly List<(int Polygon, Vector3 Center)> HiddenRepairs = [];
    }

    static Dictionary<NativeWideSceneryKey, NativeWideUserMesh> _nativeWideUserEdits = [];
    static uint _nativeWideUserLevel = uint.MaxValue;
    static DateTime _nativeWideUserStamp;
    static long _nativeWideUserNextCheck;
    static int _nativeWideUserVersion;
    static readonly Dictionary<(NativeWideSceneryKey, bool), (int Version, IReadOnlyList<NativeWideRepair> Source,
        IReadOnlyList<NativeWideRepair> Result)> _nativeWideUserRepairs = [];
    static readonly Dictionary<NativeWideSceneryKey, (int Version, int[]? Source, int[]? Result)> _nativeWideUserHidden = [];

    static void NativeWideUserEditsRefresh(uint level)
    {
        long now = Environment.TickCount64;
        if (level == _nativeWideUserLevel && now < _nativeWideUserNextCheck) return;
        _nativeWideUserNextCheck = now + 500;
        string path = Path.Combine(AppContext.BaseDirectory, "WideEdits", $"level_{level:D2}.json");
        DateTime stamp = default;
        try { if (File.Exists(path)) stamp = File.GetLastWriteTimeUtc(path); }
        catch (IOException) { }
        if (level == _nativeWideUserLevel && stamp == _nativeWideUserStamp) return;
        _nativeWideUserLevel = level;
        _nativeWideUserStamp = stamp;
        string? json = null;
        try
        {
            if (stamp != default) json = File.ReadAllText(path);
            else
            {
                using var stream = typeof(FramePacing).Assembly.GetManifestResourceStream(
                    $"RecompOne.Runtime.WideEdits.level_{level:D2}.json");
                if (stream != null) json = new StreamReader(stream).ReadToEnd();
            }
        }
        catch (IOException)
        {
            // Saved by the editor this instant: read it on the next check.
            _nativeWideUserStamp = default;
            return;
        }
        var edits = new Dictionary<NativeWideSceneryKey, NativeWideUserMesh>();
        if (json != null)
        {
            try { NativeWideParseUserEdits(level, json, edits); }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException
                or FormatException or IndexOutOfRangeException)
            {
                PaceLog($"native-wide user edits level={level}: unreadable ({e.Message})");
                edits.Clear();
            }
        }
        _nativeWideUserEdits = edits;
        _nativeWideUserVersion++;
        if (edits.Count > 0)
            PaceLog($"native-wide user edits level={level} meshes={edits.Count} "
                + $"triangles={edits.Values.Sum(e => e.Triangles.Count)} {(stamp != default ? path : "built-in")}");
    }

    static void NativeWideParseUserEdits(uint level, string json, Dictionary<NativeWideSceneryKey, NativeWideUserMesh> edits)
    {
        using var doc = JsonDocument.Parse(json);
        foreach (var mesh in doc.RootElement.GetProperty("meshes").EnumerateArray())
        {
            int x = mesh.GetProperty("x").GetInt32(), y = mesh.GetProperty("y").GetInt32(), z = mesh.GetProperty("z").GetInt32();
            var key = new NativeWideSceneryKey(level, (uint)x, (uint)y, (uint)z,
                mesh.GetProperty("polys").GetInt32(), mesh.GetProperty("verts").GetInt32());
            var user = new NativeWideUserMesh();
            var origin = new Vector3(x, y, z);
            if (mesh.TryGetProperty("triangles", out var triangles))
                foreach (var t in triangles.EnumerateArray())
                {
                    var v = t.GetProperty("v");
                    NativeWideClipVertex Corner(int i) => new(
                        v[i * 8].GetSingle() - origin.X, v[i * 8 + 1].GetSingle() - origin.Y, v[i * 8 + 2].GetSingle() - origin.Z,
                        v[i * 8 + 5].GetSingle(), v[i * 8 + 6].GetSingle(), v[i * 8 + 7].GetSingle(),
                        v[i * 8 + 3].GetSingle(), v[i * 8 + 4].GetSingle());
                    bool behind = t.TryGetProperty("behind", out var b) && b.GetBoolean();
                    user.Triangles.Add(new(t.GetProperty("poly").GetInt32(), Corner(0), Corner(1), Corner(2),
                        Boundary: !behind, Culled: t.TryGetProperty("culled", out var c) && c.GetBoolean(),
                        Rank: t.TryGetProperty("rank", out var r) ? r.GetSingle() : 0, Behind: behind));
                }
            if (mesh.TryGetProperty("hidden", out var hidden))
                foreach (var h in hidden.EnumerateArray()) user.Hidden.Add(h.GetInt32());
            if (mesh.TryGetProperty("removed", out var removed))
                foreach (var r in removed.EnumerateArray()) user.Removed.Add(r.GetInt32());
            if (mesh.TryGetProperty("hiddenRepairs", out var off))
                foreach (var h in off.EnumerateArray())
                {
                    var c = h.GetProperty("c");
                    user.HiddenRepairs.Add((h.GetProperty("poly").GetInt32(),
                        new Vector3(c[0].GetSingle(), c[1].GetSingle(), c[2].GetSingle()) - origin));
                }
            edits[key] = user;
        }
    }

    // While the Wide Editor is open (it leaves WideEdits/editor_open next to the
    // game) the game writes its camera ten times a second, so the editor can show
    // exactly the view on screen.
    static long _nativeWideEditorCheck, _nativeWideEditorWrite;
    static bool _nativeWideEditorOpen;

    static void NativeWideEditorCameraSync(IMemory m)
    {
        long now = Environment.TickCount64;
        string dir = Path.Combine(AppContext.BaseDirectory, "WideEdits");
        if (now >= _nativeWideEditorCheck)
        {
            _nativeWideEditorCheck = now + 1000;
            try { _nativeWideEditorOpen = File.Exists(Path.Combine(dir, "editor_open")); }
            catch (IOException) { _nativeWideEditorOpen = false; }
        }
        if (!_nativeWideEditorOpen || now < _nativeWideEditorWrite) return;
        _nativeWideEditorWrite = now + 100;
        NativeWideWriteCamera(m, Path.Combine(dir, "camera.json"));
    }

    static void NativeWideWriteCamera(IMemory m, string file)
    {
        uint zoneEntry = m.ReadU32(CamZoneAddr);
        uint zone = NativeWideGuestPointer(zoneEntry) ? m.ReadU32(zoneEntry + 4) : 0;
        var text = new System.Text.StringBuilder();
        text.Append(System.Globalization.CultureInfo.InvariantCulture,
            $"{{\"level\":{m.ReadU32(Catalogs.Catalog.LevelIdAddr)},\"zone\":{zone},");
        text.Append(System.Globalization.CultureInfo.InvariantCulture,
            $"\"cam\":[{(int)m.ReadU32(NativeWideCamTransAddr) >> 8},{(int)m.ReadU32(NativeWideCamTransAddr + 4) >> 8},"
            + $"{(int)m.ReadU32(NativeWideCamTransAddr + 8) >> 8}],\"matrix\":[");
        for (int i = 0; i < 9; i++)
            text.Append(i > 0 ? "," : "").Append((short)m.ReadU16(NativeWideMatrixAddr + (uint)i * 2));
        text.Append("]}");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temp = file + ".tmp";
            File.WriteAllText(temp, text.ToString());
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// F12 held for 1.5 s (development only): opens the Wide Editor at the camera on screen. The
    /// camera goes to WideEdits/editor_goto.json; an open editor picks it up and comes
    /// to the front, otherwise tools/WideEditor's build is started with it. Players
    /// have no editor next to the game, so nothing happens for them.
    /// </summary>
    public static void OpenWideEditor()
    {
        if (Runtime.Mem is not { } m) return;
        string dir = Path.Combine(AppContext.BaseDirectory, "WideEdits");
        string request = Path.Combine(dir, "editor_goto.json");
        bool open = File.Exists(Path.Combine(dir, "editor_open"));
        string? exe = open ? null : NativeWideEditorExe();
        if (!open && exe == null)
        {
            PaceLog("wide editor: tools/WideEditor build not found");
            return;
        }
        NativeWideWriteCamera(m, request);
        try
        {
            if (OperatingSystem.IsWindows()) AllowSetForegroundWindow(-1);
            if (exe != null)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
                {
                    ArgumentList = { "--goto", request },
                    WorkingDirectory = Path.GetDirectoryName(exe)!,
                    UseShellExecute = false,
                });
            PaceLog($"wide editor: {(exe != null ? "started" : "jump")} level={m.ReadU32(Catalogs.Catalog.LevelIdAddr)}");
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            PaceLog($"wide editor: {e.Message}");
        }
    }

    static string? NativeWideEditorExe()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            foreach (var config in new[] { "Release", "Debug" })
            {
                string exe = Path.Combine(d.FullName, "tools", "WideEditor", "bin", config, "net10.0", "WideEditor.exe");
                if (File.Exists(exe)) return exe;
            }
        return null;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);

    // Removed polygons are gone everywhere, 4:3 core included: while the mesh is
    // resident each one is collapsed onto its first corner in the WGEO polygon words
    // (drawing only; collision lives elsewhere), so neither the game nor the side
    // pass draws it. The original words are kept to undo it when the edit goes away.
    sealed class NativeWideRemovedState
    {
        public int Version = -1;
        public readonly Dictionary<int, (uint P0, uint P1)> Applied = [];
    }
    static readonly Dictionary<NativeWideSceneryKey, NativeWideRemovedState> _nativeWideRemoved = [];

    static (uint P0, uint P1) NativeWideCollapsed((uint P0, uint P1) p)
    {
        uint a = (p.P1 >> 20) & 0xFFFu;
        return ((p.P0 & ~(0xFFFu << 20)) | (a << 20), (p.P1 & ~(0xFFFu << 8)) | (a << 8));
    }

    static void NativeWideApplyRemoved(IMemory m, NativeWideWorld world)
    {
        if (world.PolyCount == 0 || (_nativeWideUserEdits.Count == 0 && _nativeWideRemoved.Count == 0)) return;
        var key = NativeWideUserKey(m, world);
        var removed = _nativeWideUserEdits.TryGetValue(key, out var user) && user.Removed.Count > 0 ? user.Removed : null;
        _nativeWideRemoved.TryGetValue(key, out var state);
        if (removed == null && state == null) return;
        state ??= _nativeWideRemoved[key] = new NativeWideRemovedState();
        if (state.Version != _nativeWideUserVersion)
        {
            state.Version = _nativeWideUserVersion;
            foreach (var (poly, original) in state.Applied.ToArray())
            {
                if (removed != null && removed.Contains(poly)) continue;
                uint address = world.Polygons + (uint)poly * 8u;
                var collapsed = NativeWideCollapsed(original);
                if (m.ReadU32(address) == collapsed.P0 && m.ReadU32(address + 4u) == collapsed.P1)
                {
                    m.WriteU32(address, original.P0);
                    m.WriteU32(address + 4u, original.P1);
                }
                state.Applied.Remove(poly);
            }
        }
        if (removed != null)
            foreach (int poly in removed)
            {
                if ((uint)poly >= (uint)world.PolyCount) continue;
                uint address = world.Polygons + (uint)poly * 8u;
                uint p0 = m.ReadU32(address), p1 = m.ReadU32(address + 4u);
                if (state.Applied.TryGetValue(poly, out var original) && NativeWideCollapsed(original) == (p0, p1)) continue;
                // First time, or the mesh was paged in again from disc.
                state.Applied[poly] = (p0, p1);
                var collapsed = NativeWideCollapsed((p0, p1));
                m.WriteU32(address, collapsed.P0);
                m.WriteU32(address + 4u, collapsed.P1);
            }
        if (state.Applied.Count == 0) _nativeWideRemoved.Remove(key);
    }

    static NativeWideSceneryKey NativeWideUserKey(IMemory m, NativeWideWorld world) =>
        new(m.ReadU32(Catalogs.Catalog.LevelIdAddr), m.ReadU32(world.Header), m.ReadU32(world.Header + 4),
            m.ReadU32(world.Header + 8), world.PolyCount, world.VertexCount);

    static IReadOnlyList<NativeWideRepair> NativeWideWithUserEdits(IMemory m, NativeWideWorld world,
        IReadOnlyList<NativeWideRepair> repairs)
    {
        if (_nativeWideUserEdits.Count == 0 || world.PolyCount == 0) return repairs;
        var key = NativeWideUserKey(m, world);
        if (!_nativeWideUserEdits.TryGetValue(key, out var user)) return repairs;
        if (_nativeWideUserRepairs.TryGetValue((key, world.Neighbor), out var cached)
            && cached.Version == _nativeWideUserVersion && ReferenceEquals(cached.Source, repairs))
            return cached.Result;
        var result = new List<NativeWideRepair>(repairs.Count + user.Triangles.Count);
        foreach (var repair in repairs)
        {
            bool off = false;
            if (user.HiddenRepairs.Count > 0)
            {
                var center = (Position(repair.A) + Position(repair.B) + Position(repair.C)) / 3;
                foreach (var (polygon, at) in user.HiddenRepairs)
                    if (polygon == repair.Polygon && Vector3.DistanceSquared(center, at) < 4) { off = true; break; }
            }
            if (!off) result.Add(repair);
        }
        foreach (var t in user.Triangles)
        {
            if ((uint)t.Polygon >= (uint)world.PolyCount) continue;
            if (!TryNativeWideMaterial(m, world, t.Polygon, 0, out _, out _, out _, out short v0, out _, out _, out _, out _))
                continue;
            // The editor keeps V inside the 128-row texture page; the mesh's page
            // word says which half of the VRAM page it was loaded into.
            int half = v0 & 0x80;
            result.Add(t with
            {
                A = t.A with { V = ((int)t.A.V & 0x7F) | half },
                B = t.B with { V = ((int)t.B.V & 0x7F) | half },
                C = t.C with { V = ((int)t.C.V & 0x7F) | half },
            });
        }
        _nativeWideUserRepairs[(key, world.Neighbor)] = (_nativeWideUserVersion, repairs, result);
        return result;
    }

    static int[]? NativeWideWithUserHidden(IMemory m, NativeWideWorld world, int[]? hidden)
    {
        if (_nativeWideUserEdits.Count == 0 || world.PolyCount == 0) return hidden;
        var key = NativeWideUserKey(m, world);
        if (!_nativeWideUserEdits.TryGetValue(key, out var user) || user.Hidden.Count == 0) return hidden;
        if (_nativeWideUserHidden.TryGetValue(key, out var cached) && cached.Version == _nativeWideUserVersion
            && ReferenceEquals(cached.Source, hidden))
            return cached.Result;
        int[] result = [.. (hidden ?? []).Concat(user.Hidden).Distinct()];
        _nativeWideUserHidden[key] = (_nativeWideUserVersion, hidden, result);
        return result;
    }
}
