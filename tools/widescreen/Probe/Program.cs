// Native 16:9 probe for any Crash 1 level (generalised from the #87-#101 probes).
// Boots straight into a level, optionally flies Crash through waypoints (the camera
// follows its path), and captures frames plus the exact camera of each one, so
// `wide.py view <level> log:<run>:<render>` reproduces the view offline.
//
// WideProbe <label> <level> <seconds> [--script S] [--snap N] [--fps F] [--aspect A] [--out DIR]
//   --script "path:<startFrame>:<unitsPerSecond>:x,y,z[,speed];..."  (wide.py route prints one)
//            or "<from>-<to>:<Button>+<Button>,..." pad ranges in frames
// Environment: VOID=1 clears the side bands to magenta; NOFIX=1 drops every scene
// repair (before/after pairs); NOCANOPY=1 drops the jungle canopy backdrop; NOSKY=1 drops the sky arc repairs; NOEARLY=1 disables early ruins-object spawns; OBJLOG=<type> logs live objects of a GOOL type; DUMPVRAM=<render,...> dumps VRAM; BLOCK=<hex,...> limits
// which GOOL events to Crash are dropped while flying (default: all). STATE=<slotN.state> loads
// a save state at frame STATEAT (default 120): the exact place and camera of a player's screenshot.
// PACELOG=1 turns on the FramePacing diagnostics log. CAMTRACE=1 logs every CamFollow. SNAPFROM=<frame> skips captures before that frame.
// Uses its own config/save directory under the output folder; GodMode stays off.
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using MonoMod.RuntimeDetour;
using RecompOne.Runtime;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Host.Cheats;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Sdk;
using StbImageWriteSharp;

string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
if (!File.Exists(Path.Combine(repo, "CrashBandicoot-Launcher.sln"))) repo = @"D:\GitHub\CrashBandicoot-Launcher";
if (args.Length < 3)
{
    Console.Error.WriteLine("WideProbe <label> <level> <seconds> [--script S] [--snap N] [--fps F] [--aspect A] [--out DIR]");
    return;
}
string label = args[0];
uint level = uint.Parse(args[1]);
int seconds = int.Parse(args[2]);
var opt = new Dictionary<string, string>();
for (int i = 3; i + 1 < args.Length; i += 2) opt[args[i].TrimStart('-')] = args[i + 1];
string script = opt.GetValueOrDefault("script", "");
int snapEvery = int.Parse(opt.GetValueOrDefault("snap", "30"));
// SNAPFROM=<frame>: no captures before that frame (fine snaps of one spot).
int snapFrom = int.TryParse(Environment.GetEnvironmentVariable("SNAPFROM"), out int sf) ? sf : 0;
int fps = int.Parse(opt.GetValueOrDefault("fps", "0"));
var Invariant = System.Globalization.CultureInfo.InvariantCulture;
float aspect = float.Parse(opt.GetValueOrDefault("aspect", "0"), Invariant);
string output = Path.GetFullPath(Path.Combine(opt.GetValueOrDefault("out", Path.Combine(repo, "artifacts", "widescreen", "runs")), label));
string disc = opt.GetValueOrDefault("disc", Path.Combine(repo, "Crash Bandicoot (USA).chd"));
string gameDll = opt.GetValueOrDefault("game", Path.Combine(repo,
    @"CrashBandicoot.Launcher\bin\Release\net10.0-windows\game\927A98F3F74CD9C1\game.recomp.dll"));

Directory.CreateDirectory(output);
AppPaths.SetRoot(Path.Combine(output, "state")); Directory.CreateDirectory(AppPaths.Root);
Directory.SetCurrentDirectory(AppPaths.Root); ConfigManager.Load();
ConfigManager.Game.CdPath = disc; ConfigManager.Game.Muted = true;
// WIDE=0 runs the original 4:3 view (retail comparison frames).
ConfigManager.View.Widescreen = Environment.GetEnvironmentVariable("WIDE") != "0"; ConfigManager.View.InternalResolution = 2;
ConfigManager.View.Fullscreen = false; ConfigManager.View.FrameRate = fps;
ConfigManager.View.HideTopBar = true;
CheatConfig.GodMode = false; CheatConfig.Fly = false;
// PACELOG=1 writes state/logs/pacing.txt (unlock arm, land-lock gates).
if (Environment.GetEnvironmentVariable("PACELOG") == "1") ConfigManager.View.SetBool("Diagnostics.FramePacing", true);
// CAMTRACE=1 writes state/logs/camtrace.txt (one line per CamFollow: progress, LevelUpdate branch, cam_speed).
if (Environment.GetEnvironmentVariable("CAMTRACE") == "1") FramePacing.CameraTrace = true;
ConfigManager.SaveGame(); ConfigManager.SaveView([]);
string? stateFile = Environment.GetEnvironmentVariable("STATE");
int stateAt = int.TryParse(Environment.GetEnvironmentVariable("STATEAT"), out int sa) ? sa : 120;
bool stateRequested = false;
if (stateFile != null)
{
    Directory.CreateDirectory(AppPaths.StatesDir);
    File.Copy(stateFile, Path.Combine(AppPaths.StatesDir, "slot1.state"), true);
}

var buttonNames = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
{
    ["Up"] = Controller.Up, ["Down"] = Controller.Down, ["Left"] = Controller.Left, ["Right"] = Controller.Right,
    ["Cross"] = Controller.Cross, ["Square"] = Controller.Square, ["Circle"] = Controller.Circle,
    ["Triangle"] = Controller.Triangle, ["Start"] = Controller.Start,
};
var inputs = new List<(int From, int To, ushort Mask)>();
string[]? path = script.StartsWith("path:") ? script.Split(':') : null;
(double X, double Y, double Z, double S)[] waypoints = path == null ? [] : [.. path[3].Split(';')
    .Select(w => w.Split(',').Select(v => double.Parse(v, Invariant)).ToArray())
    .Select(w => (w[0], w[1], w[2], w.Length > 3 ? w[3] : double.Parse(path[2], Invariant)))];
if (path == null)
    foreach (string part in script.Split(',', StringSplitOptions.RemoveEmptyEntries))
    {
        var (range, names) = (part.Split(':')[0], part.Split(':')[1]);
        ushort mask = 0;
        foreach (string n in names.Split('+')) mask |= buttonNames[n];
        inputs.Add((int.Parse(range.Split('-')[0]), int.Parse(range.Split('-')[1]), mask));
    }

var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(gameDll);
var game = assembly.GetType("Recompiled.CrashBandicoot")!;
const uint CrashPointer = 0x800566B4u, CamZone = 0x80057914u;
// While flying, hazards must not kill Crash: drop GOOL events sent to him and any
// change into a death/warp state.
bool flying = false;
var blockEvents = (Environment.GetEnvironmentVariable("BLOCK") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
    .Select(e => Convert.ToUInt32(e, 16)).ToHashSet();
using var noEvents = new Hook(game.GetMethod("func_80024040")!,
    (Action<Action<CpuContext, IMemory>, CpuContext, IMemory>)((original, c, m) =>
    {
        if (flying && c.A1 == m.ReadU32(CrashPointer) && (blockEvents.Count == 0 || blockEvents.Contains(c.A2)))
        {
            c.V0 = 0;
            return;
        }
        original(c, m);
    }));
using var noDeath = new Hook(game.GetMethod("func_8001D698")!,
    (Action<Action<CpuContext, IMemory>, CpuContext, IMemory>)((original, c, m) =>
    {
        if (flying && c.A0 == m.ReadU32(CrashPointer) && c.A1 is (>= 22 and <= 31) or 40 or 41) return;
        original(c, m);
    }));
// OBJHIDE=<entity id,...> skips drawing those objects' SVTX meshes; OBJSIZE=<entity id>:<value>
// draws those objects (ids comma-separated) with another OT bias (obj->size, 0x138, the GOOL zindex).
var hideIds = (Environment.GetEnvironmentVariable("OBJHIDE") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
    .Select(int.Parse).ToHashSet();
string[] sizeSpec = (Environment.GetEnvironmentVariable("OBJSIZE") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries);
var sizeIds = sizeSpec.Length == 2 ? sizeSpec[0].Split(',').Select(int.Parse).ToHashSet() : [];
int EntityId(IMemory m, uint obj)
{
    uint entity = m.ReadU32(obj + 0x110);
    return (entity & 0xFFE00000u) == 0x80000000u ? m.ReadU16(entity + 8) : -1;
}
using var svtxHook = hideIds.Count > 0 || sizeSpec.Length == 2
    ? new Hook(game.GetMethod("func_80018964")!,
        (Action<Action<CpuContext, IMemory>, CpuContext, IMemory>)((original, c, m) =>
        {
            int id = (c.A2 & 0xFFE00000u) == 0x80000000u ? EntityId(m, c.A2) : -1;
            if (hideIds.Contains(id)) return;
            if (sizeIds.Contains(id))
            {
                uint saved = m.ReadU32(c.A2 + 0x138);
                m.WriteU32(c.A2 + 0x138, (uint)int.Parse(sizeSpec[1]));
                original(c, m);
                m.WriteU32(c.A2 + 0x138, saved);
                return;
            }
            original(c, m);
        }))
    : null;
const string Alpha = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_!";
static string Eid(uint e) => new([.. new[] { 25, 19, 13, 7, 1 }.Select(s => Alpha[(int)(e >> s) & 63])]);

bool booted = false;
int frames = 0, renders = 0;
int[]? origin = null;
var frameLog = new List<object>();
void Save() => File.WriteAllText(Path.Combine(output, "log.json"),
    JsonSerializer.Serialize(frameLog, new JsonSerializerOptions { WriteIndented = true }));

using var boot = new Hook(typeof(FramePacing).GetMethod(nameof(FramePacing.PreNsInit))!,
    (Func<Func<CpuContext, IMemory, bool>, CpuContext, IMemory, bool>)((original, c, m) =>
    {
        if (!booted) { c.A1 = level; booted = true; }
        CheatManager.SetAllGems(true);
        return original(c, m);
    }));
Event.AddListener<VSyncEvent>(_ => frames++);
Event.AddListener<PadReadEvent>(e =>
{
    if (e.Port != 0) return;
    ushort mask = 0;
    foreach (var r in inputs) if (frames >= r.From && frames < r.To) mask |= r.Mask;
    e.Buttons = (ushort)~mask;
});

int[] CrashPos(IMemory m)
{
    uint crash = m.ReadU32(CrashPointer);
    if ((crash & 0xFF000000u) != 0x80000000u) return [];
    return [(int)m.ReadU32(crash + 0x80) >> 8, (int)m.ReadU32(crash + 0x84) >> 8, (int)m.ReadU32(crash + 0x88) >> 8];
}

// OBJLOG=<gool type> logs that type's live objects per frame: entity id, spawn, state, anim frame, x/y/z.
int objLogType = int.TryParse(Environment.GetEnvironmentVariable("OBJLOG"), out int olt) ? olt : -1;
List<int[]> LiveObjects(IMemory m)
{
    var found = new List<int[]>();
    void Walk(uint obj, int depth)
    {
        for (int n = 0; (obj & 0xFFE00000u) == 0x80000000u && n < 96 && depth < 8; n++)
        {
            if (m.ReadU32(obj) == 2) Walk(m.ReadU32(obj + 4), depth + 1);
            else
            {
                uint entity = m.ReadU32(obj + 0x110);
                if ((entity & 0xFFE00000u) == 0x80000000u && m.ReadU8(entity + 18) == objLogType)
                    found.Add([m.ReadU16(entity + 8), m.ReadU8(entity + 19), (int)m.ReadU32(obj + 0x2C),
                        (int)m.ReadU32(obj + 0x10C) >> 8, (int)m.ReadU32(obj + 0x80) >> 8, (int)m.ReadU32(obj + 0x84) >> 8,
                        (int)m.ReadU32(obj + 0x88) >> 8]);
                Walk(m.ReadU32(obj + 0x6C), depth + 1);
            }
            obj = m.ReadU32(obj + 0x68);
        }
    }
    try { for (int h = 0; h < 8; h++) Walk(m.ReadU32(0x80060DB8u + (uint)h * 8 + 4), 0); } catch { }
    return found;
}

object Describe(IMemory m)
{
    uint zoneEntry = m.ReadU32(CamZone);
    var worlds = new List<object>();
    string zoneName = "";
    try
    {
        if ((zoneEntry & 0xFFE00000u) == 0x80000000u)
        {
            zoneName = Eid(m.ReadU32(zoneEntry + 4));
            uint zone = m.ReadU32(zoneEntry + 0x10);
            if ((zone & 0xFFE00000u) != 0x80000000u) zone += zoneEntry;
            int count = (int)m.ReadU32(zone);
            for (int i = 0; i < count && i < 8; i++)
            {
                uint w = zone + 4 + (uint)i * 0x40;
                uint header = m.ReadU32(w + 0x10);
                worlds.Add(new
                {
                    eid = Eid(m.ReadU32(w)), polys = (int)m.ReadU32(header + 0x0C), backdrop = m.ReadU32(header + 0x1C),
                    hx = (int)m.ReadU32(header), hy = (int)m.ReadU32(header + 4), hz = (int)m.ReadU32(header + 8),
                });
            }
        }
    }
    catch { }
    var mat = Enumerable.Range(0, 9).Select(i => (int)(short)m.ReadU16(0x800577E4u + (uint)i * 2)).ToArray();
    return new
    {
        render = renders, frame = frames, zone = zoneName, crash = CrashPos(m),
        cam = new[] { (int)m.ReadU32(0x80057864u) >> 8, (int)m.ReadU32(0x80057868u) >> 8, (int)m.ReadU32(0x8005786Cu) >> 8 },
        proj = (int)m.ReadU32(0x800578D0u), matrix = mat, worlds, cpu = FramePacing.LastNativeWideCpuMs,
        objs = objLogType >= 0 ? LiveObjects(m) : null,
    };
}

var fogField = typeof(FramePacing).GetField("_nativeWideFogBackground", BindingFlags.Static | BindingFlags.NonPublic)!;
using var voidHook = Environment.GetEnvironmentVariable("VOID") == "1"
    ? new Hook(typeof(FramePacing).GetMethod(nameof(FramePacing.DrawNativeWideWorld))!,
        (Action<Action>)(original => { fogField.SetValue(null, (uint?)0xFF00FFu); original(); }))
    : null;
var repairType = typeof(FramePacing).GetNestedType("NativeWideRepair", BindingFlags.NonPublic)!;
using var noFix = Environment.GetEnvironmentVariable("NOFIX") == "1"
    ? new Hook(typeof(FramePacing).GetMethod("NativeWideSceneRepairs", BindingFlags.Static | BindingFlags.NonPublic)!,
        typeof(NoFix).GetMethod(nameof(NoFix.Empty))!.MakeGenericMethod(repairType))
    : null;
// NOEARLY=1 keeps the game's own spawn timing for ruins objects (no early 16:9 spawns).
var ruinsRadius = typeof(FramePacing).GetMethod("NativeWideRuinsRadius", BindingFlags.Static | BindingFlags.NonPublic);
using var noEarly = Environment.GetEnvironmentVariable("NOEARLY") == "1" && ruinsRadius != null
    ? new Hook(ruinsRadius, (Func<Func<byte, byte, int>, byte, byte, int>)((original, type, spawn) => 0))
    : null;
var canopyOf = typeof(FramePacing).GetMethod("NativeWideCanopyOf", BindingFlags.Static | BindingFlags.NonPublic)!;
using var noCanopy = Environment.GetEnvironmentVariable("NOCANOPY") == "1"
    ? new Hook(canopyOf, typeof(NoFix).GetMethod(nameof(NoFix.None))!.MakeGenericMethod(canopyOf.ReturnType))
    : null;
// NOSKY=1 drops the sky arc repairs, so with VOID=1 holes the extended sky hid show magenta.
using var noSky = Environment.GetEnvironmentVariable("NOSKY") == "1"
    ? new Hook(typeof(FramePacing).GetMethod("NativeWideSkyArc", BindingFlags.Static | BindingFlags.NonPublic)!,
        typeof(NoFix).GetMethod(nameof(NoFix.NoArc))!)
    : null;

using var draw = new Hook(typeof(LibGpu).GetMethod(nameof(LibGpu.DrawOTag))!,
    (Action<Action<CpuContext, IMemory>, CpuContext, IMemory>)((original, c, m) =>
    {
        if (aspect > 0 && GpuHle.WideAspect != aspect) { GpuHle.WideAspect = aspect; GpuHle.RefreshWideFov(); }
        original(c, m); renders++;
        if (stateFile != null && !stateRequested && frames >= stateAt) { SaveStates.RequestLoad(1); stateRequested = true; }
        if (path != null && frames >= int.Parse(path[1]))
        {
            flying = true;
            uint crash = m.ReadU32(CrashPointer);
            if ((crash & 0xFF000000u) == 0x80000000u)
            {
                origin ??= [(int)m.ReadU32(crash + 0x80), (int)m.ReadU32(crash + 0x84), (int)m.ReadU32(crash + 0x88)];
                double time = (frames - int.Parse(path[1])) / 60.0;
                var at = (X: origin[0] / 256.0, Y: origin[1] / 256.0, Z: origin[2] / 256.0);
                (double X, double Y, double Z) dir = (0, 0, 0);
                foreach (var w in waypoints)
                {
                    double len = Math.Sqrt((w.X - at.X) * (w.X - at.X) + (w.Y - at.Y) * (w.Y - at.Y) + (w.Z - at.Z) * (w.Z - at.Z));
                    if (len < 1) continue;
                    double d = time * w.S, t = Math.Min(d, len) / len;
                    if (d > 0 && d < len) dir = ((w.X - at.X) / len, (w.Y - at.Y) / len, (w.Z - at.Z) / len);
                    at = (at.X + (w.X - at.X) * t, at.Y + (w.Y - at.Y) * t, at.Z + (w.Z - at.Z) * t);
                    time -= Math.Min(d, len) / w.S;
                }
                // A small velocity keeps StopAtZone re-homing Crash's zone.
                m.WriteU32(crash + 0xCC, m.ReadU32(crash + 0xCC) & ~0x20u);
                m.WriteU32(crash + 0xA4, (uint)(int)(dir.X * 0x8000)); m.WriteU32(crash + 0xA8, (uint)(int)(dir.Y * 0x8000));
                m.WriteU32(crash + 0xAC, (uint)(int)(dir.Z * 0x8000));
                m.WriteU32(crash + 0x80, (uint)(int)(at.X * 256)); m.WriteU32(crash + 0x84, (uint)(int)(at.Y * 256));
                m.WriteU32(crash + 0x88, (uint)(int)(at.Z * 256));
                // As the Fly cheat does: Crash's zone follows the camera's.
                uint zone = m.ReadU32(CamZone);
                if ((zone & 0xFF000000u) == 0x80000000u) m.WriteU32(crash + 0x28, zone);
            }
        }
        if (renders % snapEvery == 0 && frames >= snapFrom && GpuHle.Backend is GlBackend backend)
        {
            var env = Runtime.Gpu!.CurrentHleDrawEnv;
            backend.PresentDisplay(env.ClipX0, env.ClipY0, env.ClipX1 - env.ClipX0 + 1, env.ClipY1 - env.ClipY0 + 1);
            Capture(backend, Path.Combine(output, $"r{renders:D5}.png"));
            frameLog.Add(Describe(m));
            Save();
        }
        if (Environment.GetEnvironmentVariable("DUMPVRAM") is string dump && dump.Split(',').Select(int.Parse).Contains(renders))
        {
            var vram = new ushort[1024 * 512];
            if (GpuHle.Backend is GlBackend vb) vb.ReadVram(0, 0, 1024, 512, vram); else Runtime.Gpu!.Vram.CopyTo(vram, 0);
            var bytes = new byte[vram.Length * 2];
            Buffer.BlockCopy(vram, 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(Path.Combine(output, $"vram{renders:D5}.bin"), bytes);
        }
        if (frames >= seconds * 60) { Save(); throw new Finished(); }
    }));

try { assembly.GetType("Recompiled.Entry")!.GetMethod("Run")!.Invoke(null, [new PSMemory(), disc]); }
catch (TargetInvocationException ex) when (ex.InnerException is Finished) { }
catch (Finished) { }
catch (Exception ex) { File.WriteAllText(Path.Combine(output, "error.txt"), ex.ToString()); }
finally { Save(); Runtime.Shutdown(); }

static void Capture(GlBackend backend, string file)
{
    const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
    int width = (int)typeof(GlBackend).GetField("_presentW", fields)!.GetValue(backend)!;
    int height = (int)typeof(GlBackend).GetField("_presentH", fields)!.GetValue(backend)!;
    var pixels = new int[width * height];
    if (!backend.ReadPresentArgb(pixels)) return;
    var rgba = new byte[pixels.Length * 4];
    for (int i = 0; i < pixels.Length; i++)
    {
        rgba[i * 4] = (byte)(pixels[i] >> 16);
        rgba[i * 4 + 1] = (byte)(pixels[i] >> 8);
        rgba[i * 4 + 2] = (byte)pixels[i];
        rgba[i * 4 + 3] = 255;
    }
    using var stream = File.Create(file);
    new ImageWriter().WritePng(rgba, width, height, ColorComponents.RedGreenBlueAlpha, stream);
}

sealed class Finished : Exception { }

static class NoFix
{
    public static IReadOnlyList<T> Empty<T>(Func<IMemory, object, IReadOnlyList<T>> original, IMemory m, object world) => [];
    public static T? None<T>(Func<uint, T> original, uint level) => default;
    public static bool NoArc(Func<IMemory, object, uint, bool> original, IMemory m, object world, uint level) => false;
}
