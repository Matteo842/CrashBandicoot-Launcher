// Runs the runtime's own native-wide scene repairs (FramePacing.NativeWideSceneRepairs)
// on meshes exported by `wide.py export`, without the game: each WGEO is placed in an
// empty PSMemory with the level id and the executable (for the UV map), and the repair
// triangles are written as absolute positions for `wide.py view/scan --repairs`.
// Usage: RepairDump <export dir> <out.json> [mesh ...]
using System.Reflection;
using System.Text.Json;
using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Memory;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: RepairDump <export dir> <out.json> [mesh ...]");
    return 2;
}
string dir = args[0];
var index = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "index.json"))).RootElement;
uint level = index.GetProperty("level").GetUInt32();
var only = args.Skip(2).ToHashSet();

var memory = new PSMemory();
byte[] exe = File.ReadAllBytes(Path.Combine(dir, "exe.bin"));
memory.LoadBytes(index.GetProperty("exe_taddr").GetUInt32(), exe[0x800..]);
memory.WriteU32(Catalog.LevelIdAddr, level);

const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Static;
var pacing = typeof(FramePacing);
var worldType = pacing.GetNestedType("NativeWideWorld", BindingFlags.NonPublic)!;
var repairsOf = pacing.GetMethod("NativeWideSceneRepairs", Private)!;
var behindOf = pacing.GetMethod("NativeWideRepairsBehind", Private)!;
bool underBackdrop = (bool)pacing.GetMethod("NativeWideFillsUnderBackdrop", Private)!.Invoke(null, [memory])!;
void Set(object o, string field, object value) => worldType.GetField(field)!.SetValue(o, value);
static float F(object o, string name) => (float)o.GetType().GetProperty(name)!.GetValue(o)!;

uint next = 0x80100000;
var output = new Dictionary<string, object>();
foreach (var mesh in index.GetProperty("meshes").EnumerateArray())
{
    string name = mesh.GetProperty("name").GetString()!;
    if (only.Count > 0 && !only.Contains(name)) continue;
    byte[] blob = File.ReadAllBytes(Path.Combine(dir, mesh.GetProperty("file").GetString()!));
    int headerSize = mesh.GetProperty("header").GetInt32(), polySize = mesh.GetProperty("polys").GetInt32();
    uint header = next, polygons = header + (uint)headerSize, vertices = polygons + (uint)polySize;
    uint tpages = (vertices + (uint)(blob.Length - headerSize - polySize) + 15) & ~15u;
    memory.LoadBytes(header, blob);
    memory.ZeroRange(tpages, 32);
    next = (tpages + 32 + 0xFFF) & ~0xFFFu;

    var world = Activator.CreateInstance(worldType, nonPublic: true)!;
    Set(world, "PolyCount", (int)memory.ReadU32(header + 0x0C));
    Set(world, "VertexCount", (int)memory.ReadU32(header + 0x10));
    Set(world, "TexinfoCount", (int)memory.ReadU32(header + 0x14));
    Set(world, "TpageCount", (int)memory.ReadU32(header + 0x18));
    Set(world, "Header", header);
    Set(world, "Polygons", polygons);
    Set(world, "Vertices", vertices);
    Set(world, "Texinfos", header + 0x40);
    Set(world, "Tpages", tpages);

    // Warm, uncached timing: the runtime computes a mesh's repairs once, when it first shows.
    var cache = (System.Collections.IDictionary)pacing.GetField("_nativeWideSceneryRepairs", Private)!.GetValue(null)!;
    double ms = double.MaxValue;
    System.Collections.IEnumerable repairs = Array.Empty<object>();
    for (int run = 0; run < 3; run++)
    {
        cache.Clear();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        repairs = (System.Collections.IEnumerable)repairsOf.Invoke(null, [memory, world])!;
        ms = Math.Min(ms, clock.Elapsed.TotalMilliseconds);
    }
    // PROFILE=1: time The Lab's repair passes separately.
    if (Environment.GetEnvironmentVariable("PROFILE") == "1" && level == 41)
    {
        var repairList = Activator.CreateInstance(typeof(List<>).MakeGenericType(pacing.GetNestedType("NativeWideRepair", BindingFlags.NonPublic)!))!;
        var origin = new System.Numerics.Vector3((int)memory.ReadU32(header), (int)memory.ReadU32(header + 4), (int)memory.ReadU32(header + 8));
        var towers = pacing.GetMethod("NativeWideLabTowers", Private)!.Invoke(null,
            [(int)memory.ReadU32(header + 0x0C), (int)memory.ReadU32(header + 0x10), (int)origin.X, (int)origin.Z]);
        var faces = pacing.GetMethod("NativeWideFacesOf", Private)!.Invoke(null, [memory, world]);
        var rowSpecs = pacing.GetField("NativeWideLabRows", Private)!.GetValue(null);
        foreach (var (pass, call) in new (string, Action)[]
        {
            ("faces", () => pacing.GetMethod("NativeWideFacesOf", Private)!.Invoke(null, [memory, world])),
            ("towers", () => { if (towers != null) pacing.GetMethod("NativeWideTowerRepairs", Private)!.Invoke(null, [faces, origin, towers, repairList]); }),
            ("rows", () => pacing.GetMethod("NativeWideRowRepairs", Private)!.Invoke(null, [faces, rowSpecs, repairList])),
            ("periodic", () => pacing.GetMethod("NativeWidePeriodicRepairs", Private)!.Invoke(null, [faces, 0, 2, 2400f, 2400f, 4800f, repairList])),
        })
        {
            double best = double.MaxValue;
            for (int run = 0; run < 3; run++)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                call();
                best = Math.Min(best, clock.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine($"  {name} {pass}: {best:0.0} ms");
        }
    }
    bool behindScenery = (bool)behindOf.Invoke(null, [memory, world])!;
    float tx = (int)memory.ReadU32(header), ty = (int)memory.ReadU32(header + 4), tz = (int)memory.ReadU32(header + 8);
    var rows = new List<object[]>();
    foreach (var repair in repairs)
    {
        var t = repair.GetType();
        int polygon = (int)t.GetProperty("Polygon")!.GetValue(repair)!;
        bool boundary = (bool)t.GetProperty("Boundary")!.GetValue(repair)!;
        var row = new List<object> { polygon };
        var corners = new[] { "A", "B", "C" }.Select(c => t.GetProperty(c)!.GetValue(repair)!).ToArray();
        foreach (var v in corners) row.AddRange([F(v, "X") + tx, F(v, "Y") + ty, F(v, "Z") + tz]);
        row.Add(behindScenery && !boundary);
        foreach (var v in corners) row.AddRange([F(v, "U"), F(v, "V"), F(v, "R"), F(v, "G"), F(v, "B")]);
        rows.Add([.. row]);
    }
    if (rows.Count > 0) Console.WriteLine($"{name}: {rows.Count} repair triangles, {ms:0.0} ms");
    output[name] = new { repairs = rows };
}
File.WriteAllText(args[1], JsonSerializer.Serialize(new { level, under_backdrop = underBackdrop, meshes = output }));
return 0;
