// The runtime's own automatic 16:9 repairs (FramePacing.NativeWideSceneRepairs), run on
// the disc meshes without the game, like tools/widescreen/RepairDump.
using System.Numerics;
using System.Reflection;
using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Memory;

namespace WideEditor;

struct EdVertex
{
    public Vector3 Pos;
    public Vector2 Uv;
    public Vector3 Rgb;
}

sealed class AutoTri
{
    public required Mesh Mesh;
    public int Poly;
    public EdVertex A, B, C;
    public bool Behind, Culled;
    public float Rank;
    public Vector3 Center => (A.Pos + B.Pos + C.Pos) / 3;
}

static class AutoRepairs
{
    const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Static;

    public sealed class Result
    {
        public List<AutoTri> Tris = [];
        public Dictionary<Mesh, (int Start, int Count)> Ranges = [];
        public Dictionary<Mesh, HashSet<int>> HiddenSides = [];
        public bool NeighbourRepairs;
        public string? Error;
    }

    public static Result Compute(LevelData level)
    {
        var result = new Result();
        try
        {
            var memory = new PSMemory();
            memory.LoadBytes(level.ExeTaddr, level.Exe[0x800..]);
            memory.WriteU32(Catalog.LevelIdAddr, (uint)level.Id);
            var pacing = typeof(FramePacing);
            var worldType = pacing.GetNestedType("NativeWideWorld", BindingFlags.NonPublic)!;
            var repairsOf = pacing.GetMethod("NativeWideSceneRepairs", Private)!;
            var behindOf = pacing.GetMethod("NativeWideRepairsBehind", Private)!;
            var hiddenOf = pacing.GetMethod("NativeWideHiddenSides", Private);
            var neighbour = pacing.GetMethod("NativeWideNeighborRepairs", Private);
            result.NeighbourRepairs = neighbour != null && (bool)neighbour.Invoke(null, [memory])!;
            void Set(object o, string field, object value) => worldType.GetField(field)!.SetValue(o, value);

            foreach (var mesh in level.Meshes)
            {
                int start = result.Tris.Count;
                const uint header = 0x80100000;
                byte[] blob = [.. mesh.Header, .. mesh.PolyBytes, .. mesh.VertBytes];
                uint polygons = header + (uint)mesh.Header.Length, vertices = polygons + (uint)mesh.PolyBytes.Length;
                uint tpages = (vertices + (uint)mesh.VertBytes.Length + 15) & ~15u;
                if (tpages + 64 >= 0x80200000) continue;
                memory.LoadBytes(header, blob);
                memory.ZeroRange(tpages, 32);
                var world = Activator.CreateInstance(worldType, nonPublic: true)!;
                Set(world, "PolyCount", mesh.PolyCount);
                Set(world, "VertexCount", mesh.VertexCount);
                Set(world, "TexinfoCount", mesh.Texinfo.Length);
                Set(world, "TpageCount", mesh.Tpages.Length);
                Set(world, "Header", header);
                Set(world, "Polygons", polygons);
                Set(world, "Vertices", vertices);
                Set(world, "Texinfos", header + 0x40);
                Set(world, "Tpages", tpages);
                bool behind = (bool)behindOf.Invoke(null, [memory, world])!;
                if (hiddenOf?.Invoke(null, [memory, world]) is int[] hidden)
                    result.HiddenSides[mesh] = [.. hidden];
                var repairs = (System.Collections.IEnumerable)repairsOf.Invoke(null, [memory, world])!;
                foreach (var repair in repairs)
                {
                    var t = repair.GetType();
                    T Get<T>(string name) => (T)t.GetProperty(name)!.GetValue(repair)!;
                    EdVertex V(string name)
                    {
                        var v = t.GetProperty(name)!.GetValue(repair)!;
                        var vt = v.GetType();
                        float F(string n) => (float)vt.GetProperty(n)!.GetValue(v)!;
                        return new EdVertex
                        {
                            Pos = new Vector3(F("X"), F("Y"), F("Z")) + mesh.Trans,
                            Uv = new(F("U"), (int)F("V") & 127),
                            Rgb = new(F("R"), F("G"), F("B")),
                        };
                    }
                    bool boundary = Get<bool>("Boundary");
                    result.Tris.Add(new AutoTri
                    {
                        Mesh = mesh, Poly = Get<int>("Polygon"), A = V("A"), B = V("B"), C = V("C"),
                        Behind = behind && !boundary, Culled = Get<bool>("Culled"), Rank = Get<float>("Rank"),
                    });
                }
                result.Ranges[mesh] = (start, result.Tris.Count - start);
            }
        }
        catch (Exception e)
        {
            result.Error = (e is TargetInvocationException { InnerException: { } inner } ? inner : e).Message;
        }
        return result;
    }
}
