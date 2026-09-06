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
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--walk") { options[args[i]] = "true"; continue; }
    if (!args[i].StartsWith("--") || i + 1 == args.Length)
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
int frames = 0;
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
        if (compared || frames < targetFrame) return;
        compared = true;
        var backend = (GlBackend)GpuHle.Backend!;
        var env = Runtime.Gpu!.CurrentHleDrawEnv;
        int width = env.ClipX1 - env.ClipX0 + 1, height = env.ClipY1 - env.ClipY0 + 1;
        backend.PresentDisplay(env.ClipX0, env.ClipY0, width, height);
        var native = Capture(backend, Path.Combine(output, "native.png"));

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
            level, frame = frames, fps = ConfigManager.View.FrameRate, wide,
            nativeWidth = native.W, originalWidth = reference.W, height = reference.H,
            changedPixels, maxChannelDifference, passed = wide && changedPixels == 0,
        };
        string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(output, "result.json"), json);
        Console.WriteLine(json);
        Environment.Exit(result.passed ? 0 : 1);
    }));

var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(game);
assembly.GetType("Recompiled.Entry")!.GetMethod("Run")!.Invoke(null, [new PSMemory(), disc]);
return 3;

static int Usage()
{
    Console.Error.WriteLine("WideCheck --disc <game.cue> --game <game.recomp.dll> [--level 9] [--frame 600] [--fps 0] [--walk] [--output <folder>]");
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
