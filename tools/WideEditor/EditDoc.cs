// The hand-made fixes of one level, as the runtime reads them
// (FramePacing.NativeWideUserEdits.cs): per mesh, extra triangles, retail polygons
// hidden in the 16:9 bands and automatic repairs switched off.
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace WideEditor;

sealed class UserTri
{
    public string Mesh = "";
    public int Poly;
    public EdVertex[] V = new EdVertex[3];
    public bool Behind, Culled;
    public float Rank;
    public int Group;

    public UserTri Clone() => new()
    {
        Mesh = Mesh, Poly = Poly, V = [.. V], Behind = Behind, Culled = Culled, Rank = Rank, Group = Group,
    };

    public Vector3 Center => (V[0].Pos + V[1].Pos + V[2].Pos) / 3;
}

readonly record struct HiddenRepair(string Mesh, int Poly, Vector3 Center);

sealed class EditDoc
{
    public List<UserTri> Tris = [];
    public HashSet<(string Mesh, int Poly)> Hidden = [];
    public List<HiddenRepair> HiddenRepairs = [];
    public int NextGroup = 1;

    public EditDoc Clone() => new()
    {
        Tris = [.. Tris.Select(t => t.Clone())],
        Hidden = [.. Hidden],
        HiddenRepairs = [.. HiddenRepairs],
        NextGroup = NextGroup,
    };

    public bool IsRepairHidden(AutoTri t)
    {
        foreach (var h in HiddenRepairs)
            if (h.Poly == t.Poly && h.Mesh == t.Mesh.Name && Vector3.DistanceSquared(h.Center, t.Center) < 4) return true;
        return false;
    }

    public bool Empty => Tris.Count == 0 && Hidden.Count == 0 && HiddenRepairs.Count == 0;

    public string ToJson(LevelData level)
    {
        var names = Tris.Select(t => t.Mesh).Concat(Hidden.Select(h => h.Mesh)).Concat(HiddenRepairs.Select(h => h.Mesh))
            .Where(level.MeshByName.ContainsKey).Distinct().OrderBy(n => n, StringComparer.Ordinal);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WriteNumber("level", level.Id);
            w.WriteString("name", level.Name);
            w.WriteNumber("nextGroup", NextGroup);
            w.WriteStartArray("meshes");
            foreach (var name in names)
            {
                var m = level.MeshByName[name];
                w.WriteStartObject();
                w.WriteString("name", name);
                w.WriteNumber("x", m.TransX);
                w.WriteNumber("y", m.TransY);
                w.WriteNumber("z", m.TransZ);
                w.WriteNumber("polys", m.PolyCount);
                w.WriteNumber("verts", m.VertexCount);
                w.WriteStartArray("triangles");
                foreach (var t in Tris.Where(t => t.Mesh == name))
                {
                    w.WriteStartObject();
                    w.WriteNumber("poly", t.Poly);
                    w.WriteBoolean("behind", t.Behind);
                    if (t.Rank != 0) w.WriteNumber("rank", t.Rank);
                    if (t.Culled) w.WriteBoolean("culled", true);
                    w.WriteNumber("group", t.Group);
                    w.WriteStartArray("v");
                    foreach (var v in t.V)
                    {
                        w.WriteNumberValue(MathF.Round(v.Pos.X, 2));
                        w.WriteNumberValue(MathF.Round(v.Pos.Y, 2));
                        w.WriteNumberValue(MathF.Round(v.Pos.Z, 2));
                        w.WriteNumberValue(MathF.Round(v.Uv.X, 2));
                        w.WriteNumberValue(MathF.Round(v.Uv.Y, 2));
                        w.WriteNumberValue(MathF.Round(v.Rgb.X));
                        w.WriteNumberValue(MathF.Round(v.Rgb.Y));
                        w.WriteNumberValue(MathF.Round(v.Rgb.Z));
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteStartArray("hidden");
                foreach (var h in Hidden.Where(h => h.Mesh == name).OrderBy(h => h.Poly)) w.WriteNumberValue(h.Poly);
                w.WriteEndArray();
                w.WriteStartArray("hiddenRepairs");
                foreach (var h in HiddenRepairs.Where(h => h.Mesh == name))
                {
                    w.WriteStartObject();
                    w.WriteNumber("poly", h.Poly);
                    w.WriteStartArray("c");
                    w.WriteNumberValue(MathF.Round(h.Center.X, 2));
                    w.WriteNumberValue(MathF.Round(h.Center.Y, 2));
                    w.WriteNumberValue(MathF.Round(h.Center.Z, 2));
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static EditDoc FromJson(string json, LevelData level)
    {
        var doc = new EditDoc();
        using var d = JsonDocument.Parse(json);
        if (d.RootElement.TryGetProperty("nextGroup", out var ng)) doc.NextGroup = ng.GetInt32();
        foreach (var mesh in d.RootElement.GetProperty("meshes").EnumerateArray())
        {
            string name = mesh.GetProperty("name").GetString() ?? "";
            if (!level.MeshByName.ContainsKey(name))
            {
                // Same mesh by origin and size if the name is unknown.
                var match = level.Meshes.FirstOrDefault(m => m.TransX == mesh.GetProperty("x").GetInt32()
                    && m.TransY == mesh.GetProperty("y").GetInt32() && m.TransZ == mesh.GetProperty("z").GetInt32()
                    && m.PolyCount == mesh.GetProperty("polys").GetInt32());
                if (match == null) continue;
                name = match.Name;
            }
            if (mesh.TryGetProperty("triangles", out var tris))
                foreach (var t in tris.EnumerateArray())
                {
                    var v = t.GetProperty("v");
                    var tri = new UserTri
                    {
                        Mesh = name,
                        Poly = t.GetProperty("poly").GetInt32(),
                        Behind = t.TryGetProperty("behind", out var b) && b.GetBoolean(),
                        Culled = t.TryGetProperty("culled", out var c) && c.GetBoolean(),
                        Rank = t.TryGetProperty("rank", out var r) ? r.GetSingle() : 0,
                        Group = t.TryGetProperty("group", out var g) ? g.GetInt32() : 0,
                    };
                    for (int i = 0; i < 3; i++)
                        tri.V[i] = new EdVertex
                        {
                            Pos = new(v[i * 8].GetSingle(), v[i * 8 + 1].GetSingle(), v[i * 8 + 2].GetSingle()),
                            Uv = new(v[i * 8 + 3].GetSingle(), v[i * 8 + 4].GetSingle()),
                            Rgb = new(v[i * 8 + 5].GetSingle(), v[i * 8 + 6].GetSingle(), v[i * 8 + 7].GetSingle()),
                        };
                    doc.Tris.Add(tri);
                    doc.NextGroup = Math.Max(doc.NextGroup, tri.Group + 1);
                }
            if (mesh.TryGetProperty("hidden", out var hidden))
                foreach (var h in hidden.EnumerateArray()) doc.Hidden.Add((name, h.GetInt32()));
            if (mesh.TryGetProperty("hiddenRepairs", out var off))
                foreach (var h in off.EnumerateArray())
                {
                    var c = h.GetProperty("c");
                    doc.HiddenRepairs.Add(new(name, h.GetProperty("poly").GetInt32(),
                        new(c[0].GetSingle(), c[1].GetSingle(), c[2].GetSingle())));
                }
        }
        return doc;
    }
}
