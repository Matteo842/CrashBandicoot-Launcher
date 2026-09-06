using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using MonoMod.RuntimeDetour;
using RecompOne.Runtime;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Sdk;
using StbImageWriteSharp;

// Uses the player's own disc/recompiled assembly and a separate config/save root.
// Replays one ordering table, so animation and timing cannot invalidate the comparison.
var options = new Dictionary<string, string>();
string[] valueOptions = ["--disc", "--game", "--level", "--frame", "--fps", "--input", "--snapshots", "--output"];
for (int i = 0; i < args.Length; i++)
{
    if (args[i] is "--walk" or "--boundaries") { options[args[i]] = "true"; continue; }
    if (!valueOptions.Contains(args[i]) || i + 1 == args.Length || args[i + 1].StartsWith("--"))
        return Usage();
    options[args[i]] = args[++i];
}
if (!options.TryGetValue("--disc", out string? discArg)
    || !options.TryGetValue("--game", out string? gameArg)) return Usage();
string disc = Path.GetFullPath(discArg), game = Path.GetFullPath(gameArg);
if (!File.Exists(disc) || !File.Exists(game)) return Usage();
if (!uint.TryParse(options.GetValueOrDefault("--level", "9"), out uint level)
    || !int.TryParse(options.GetValueOrDefault("--frame", "600"), out int targetFrame) || targetFrame < 1
    || !int.TryParse(options.GetValueOrDefault("--fps", "0"), out int fps)) return Usage();
var snapshots = new Queue<int>();
if (options.TryGetValue("--snapshots", out string? snapshotArg))
{
    var requested = new SortedSet<int>();
    foreach (string value in snapshotArg.Split(','))
    {
        if (!int.TryParse(value, out int frame) || frame < 1 || frame > targetFrame) return Usage();
        requested.Add(frame);
    }
    snapshots = new Queue<int>(requested);
}
var inputs = new List<(int From, int To, ushort Buttons)>();
if (options.TryGetValue("--input", out string? inputArg))
{
    if (options.ContainsKey("--walk")) return Usage();
    var buttons = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
    {
        ["Up"] = Controller.Up, ["Down"] = Controller.Down,
        ["Left"] = Controller.Left, ["Right"] = Controller.Right,
        ["Cross"] = Controller.Cross, ["Square"] = Controller.Square,
        ["Circle"] = Controller.Circle, ["Triangle"] = Controller.Triangle,
        ["Start"] = Controller.Start, ["Select"] = Controller.Select,
    };
    try
    {
        var ranges = JsonSerializer.Deserialize<InputRange[]>(File.ReadAllText(Path.GetFullPath(inputArg)),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (ranges == null) return Usage();
        foreach (var range in ranges)
        {
            if (range == null || range.From < 0 || range.To <= range.From || range.Buttons == null) return Usage();
            ushort mask = 0;
            foreach (string button in range.Buttons)
            {
                if (button == null || !buttons.TryGetValue(button, out ushort value)) return Usage();
                mask |= value;
            }
            inputs.Add((range.From, range.To, mask));
        }
    }
    catch (Exception e) when (e is IOException or JsonException or ArgumentException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"Cannot read input script: {e.Message}");
        return Usage();
    }
}
string output = Path.GetFullPath(options.GetValueOrDefault("--output", "artifacts/wide-check"));
string state = Path.Combine(output, "state");
Directory.CreateDirectory(state);
AppPaths.SetRoot(state);
Directory.SetCurrentDirectory(state);
ConfigManager.Load();
ConfigManager.Game.CdPath = disc;
ConfigManager.Game.Muted = true;
ConfigManager.View.Widescreen = true;
ConfigManager.View.InternalResolution = 2;
ConfigManager.View.Fullscreen = false;
ConfigManager.View.FrameRate = fps;
ConfigManager.View.HideTopBar = true;
ConfigManager.SaveGame();
ConfigManager.SaveView([]);

bool booted = false, compared = false;
int comparisonExitCode = 3;
int frames = 0;
var capturedSnapshots = new List<object>();
uint drawEnvAddress = 0;
using var timeout = new Timer(_ =>
{
    Console.Error.WriteLine("No comparison completed within 120 seconds.");
    Environment.Exit(3);
}, null, TimeSpan.FromSeconds(120), Timeout.InfiniteTimeSpan);
using var boot = new Hook(typeof(FramePacing).GetMethod(nameof(FramePacing.PreNsInit))!,
    (Func<Func<CpuContext, IMemory, bool>, CpuContext, IMemory, bool>)((original, c, m) =>
    {
        if (!booted) { c.A1 = level; booted = true; }
        return original(c, m);
    }));
Event.AddListener<VSyncEvent>(_ => frames++);
Event.AddListener<DrawEnvEvent>(e => drawEnvAddress = e.Context.A0);
Event.AddListener<PadReadEvent>(e =>
{
    if (e.Port == 0 && options.ContainsKey("--input"))
    {
        ushort mask = 0;
        foreach (var range in inputs)
            if (frames >= range.From && frames < range.To) mask |= range.Buttons;
        e.Buttons = (ushort)~mask;
        return;
    }
    if (!options.ContainsKey("--walk") || e.Port != 0 || frames < 320) return;
    ushort pressed = frames < 1200 ? Controller.Up : (ushort)0;
    if (frames % 90 < 30) pressed |= Controller.Square;
    if (frames % 150 < 45) pressed |= Controller.Cross;
    e.Buttons = (ushort)~pressed;
});
using var compare = new Hook(typeof(LibGpu).GetMethod(nameof(LibGpu.DrawOTag))!,
    (Action<Action<CpuContext, IMemory>, CpuContext, IMemory>)((original, c, m) =>
    {
        original(c, m);
        if (compared || (frames < targetFrame && (!snapshots.TryPeek(out int next) || frames < next))) return;
        var backend = (GlBackend)GpuHle.Backend!;
        var env = Runtime.Gpu!.CurrentHleDrawEnv;
        int width = env.ClipX1 - env.ClipX0 + 1, height = env.ClipY1 - env.ClipY0 + 1;
        backend.PresentDisplay(env.ClipX0, env.ClipY0, width, height);
        // Earlier snapshots only read the framebuffer. Replay the OT once, at
        // the final sample, so the comparison cannot affect later gameplay.
        while (snapshots.TryPeek(out int requested) && frames >= requested)
        {
            snapshots.Dequeue();
            string file = $"native-{requested:D6}.png";
            var snapshot = Capture(backend, Path.Combine(output, file));
            if (options.ContainsKey("--boundaries"))
                SceneBoundaries.Write(m, Path.Combine(output, $"boundaries-{requested:D6}.svg"), file,
                    snapshot.W / (double)snapshot.H * height, width, height, env.ClipX0, env.ClipY0);
            capturedSnapshots.Add(new { requestedFrame = requested, frame = frames, file,
                level = m.ReadU32(RecompOne.Runtime.Catalogs.Catalog.LevelIdAddr), width = snapshot.W, height = snapshot.H });
            File.WriteAllText(Path.Combine(output, "snapshots.json"), JsonSerializer.Serialize(capturedSnapshots,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        if (frames < targetFrame) return;
        compared = true;
        var native = Capture(backend, Path.Combine(output, "native.png"));
        if (options.ContainsKey("--boundaries"))
            SceneBoundaries.Write(m, Path.Combine(output, "boundaries.svg"), "native.png",
                native.W / (double)native.H * height, width, height, env.ClipX0, env.ClipY0);

        // Restore the original draw state as well as the background. The death
        // fade changes draw mode (including dithering) at the end of its OT.
        backend.FillRect(env.ClipX0, env.ClipY0, width, height, 0);
        GpuHle.NativeWideRendererActive = false;
        uint ot = c.A0;
        c.A0 = drawEnvAddress;
        LibGpu.PutDrawEnv(c, m);
        c.A0 = ot;
        original(c, m);
        backend.PresentDisplay(env.ClipX0, env.ClipY0, width, height);
        var reference = Capture(backend, Path.Combine(output, "original.png"));

        bool wide = native.W > reference.W && native.H == reference.H;
        int changedPixels = 0, maxChannelDifference = 0;
        int margin = (native.W - reference.W) / 2;
        if (wide)
            for (int y = 0; y < reference.H; y++)
                for (int x = 0; x < reference.W; x++)
                {
                    int a = native.Pixels[y * native.W + x + margin], b = reference.Pixels[y * reference.W + x];
                    if (((a ^ b) & 0xFFFFFF) != 0) changedPixels++;
                    for (int shift = 0; shift < 24; shift += 8)
                        maxChannelDifference = Math.Max(maxChannelDifference,
                            Math.Abs(((a >> shift) & 255) - ((b >> shift) & 255)));
                }
        var result = new
        {
            level, actualLevel = m.ReadU32(RecompOne.Runtime.Catalogs.Catalog.LevelIdAddr),
            frame = frames, fps = ConfigManager.View.FrameRate, wide,
            nativeWidth = native.W, originalWidth = reference.W, height = reference.H,
            changedPixels, maxChannelDifference, passed = wide && changedPixels == 0,
        };
        string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(output, "result.json"), json);
        Console.WriteLine(json);
        comparisonExitCode = result.passed ? 0 : 1;
        timeout.Change(Timeout.Infinite, Timeout.Infinite);
        // Unwind the game and detour before disposing native GL/audio state.
        // Environment.Exit inside the draw hook can fast-fail during teardown.
        throw new ComparisonFinishedException();
    }));

var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(game);
try
{
    assembly.GetType("Recompiled.Entry")!.GetMethod("Run")!.Invoke(null, [new PSMemory(), disc]);
}
catch (TargetInvocationException e) when (e.InnerException is ComparisonFinishedException)
{
    // Expected completion of the isolated verification session.
}
finally
{
    Runtime.Shutdown();
}
return comparisonExitCode;

static int Usage()
{
    Console.Error.WriteLine("WideCheck --disc <game.cue> --game <game.recomp.dll> [--level 9] [--frame 600] [--fps 0] [--walk | --input <script.json>] [--snapshots 400,500,600] [--boundaries] [--output <folder>]");
    return 2;
}

static (int W, int H, int[] Pixels) Capture(GlBackend backend, string path)
{
    const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
    int width = (int)typeof(GlBackend).GetField("_presentW", fields)!.GetValue(backend)!;
    int height = (int)typeof(GlBackend).GetField("_presentH", fields)!.GetValue(backend)!;
    var pixels = new int[width * height];
    if (!backend.ReadPresentArgb(pixels)) throw new InvalidOperationException("Framebuffer capture failed.");
    var rgba = new byte[pixels.Length * 4];
    for (int i = 0; i < pixels.Length; i++)
    {
        rgba[i * 4] = (byte)(pixels[i] >> 16);
        rgba[i * 4 + 1] = (byte)(pixels[i] >> 8);
        rgba[i * 4 + 2] = (byte)pixels[i];
        rgba[i * 4 + 3] = 255;
    }
    using var stream = File.Create(path);
    new ImageWriter().WritePng(rgba, width, height, ColorComponents.RedGreenBlueAlpha, stream);
    return (width, height, pixels);
}

sealed record InputRange(int From, int To, string[] Buttons);
sealed class ComparisonFinishedException : Exception { }
