using System.Reflection;
using System.Text.Json;
using RecompOne.Runtime;
using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Memory;

// Diagnostic observations, not a gameplay regression suite. No disc or GL/audio
// initialization: synthetic guest objects exercise the actual clock method.
if (args.Length != 0 && !(args.Length == 2 && args[0] == "--output"))
{
    Console.Error.WriteLine("Usage: CrashBandicoot.TimingProbe [--output report.json]");
    return 2;
}
string? output = args.Length == 2 ? Path.GetFullPath(args[1]) : null;
AppPaths.SetRoot(Path.Combine(AppContext.BaseDirectory, "isolated-state"));
ConfigManager.View.FrameRate = 120;
const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
FieldInfo Field(string name) => typeof(FramePacing).GetField(name, flags)
    ?? throw new MissingFieldException(typeof(FramePacing).FullName, name);
void Set(string name, object value) => Field(name).SetValue(null, value);
uint Constant(string name) => (uint)Field(name).GetRawConstantValue()!;
object? Call(string name, params object[] values) => (typeof(FramePacing).GetMethod(name, flags)
    ?? throw new MissingMethodException(typeof(FramePacing).FullName, name)).Invoke(null, values);

var observations = new List<object>();
foreach (var (level, name) in new (uint, string)[] {
    (32, "The Lost City"), (35, "Sunset Vista"), (6, "Heavy Machinery"),
    (31, "Dr. Neo Cortex"), (44, "The Great Hall") })
foreach (bool requestedPause in new[] { false, true })
{
    var m = new PSMemory();
    const uint pause = 0x80070000, entry = 0x80071000, header = 0x80071100;
    m.WriteU32(Catalog.LevelIdAddr, level);
    m.WriteU32(Constant("PausedAddr"), requestedPause ? 1u : 0u);
    m.WriteU32(Constant("PauseObjAddr"), pause);
    m.WriteU32(pause, 1); // live object
    m.WriteU32(pause + Constant("ObjGlobalOff"), entry);
    m.WriteU32(entry + 16, header);
    m.WriteU32(header, Constant("GoolTypeDisp"));
    m.WriteU32(pause + Constant("ObjSubtypeOff"), Constant("PauseDispSubtype"));
    Set("_levelReady", true);
    Set("_inNsInit", false);
    Set("_saveUiPad", false);
    Set("_waterArmed", true);
    Set("_worldDrawPauseHold", false);
    Set("_worldDraw", 100u);
    Set("_worldDrawBase", 100u);
    Set("_worldDrawGuest0", 3400u);
    Set("_guestTicks", 3400u);
    bool paused = (bool)Call("GamePaused", m)!;
    bool active = FramePacing.IsActive(m);
    if (paused != requestedPause || !active)
        throw new InvalidOperationException($"Invalid fixture for {name}");
    Call("SyncWorldDraw", m);
    uint before = m.ReadU32(Constant("DrawCountAddr"));
    // Feed one simulated second. No sleeps and no dependence on host speed.
    // Advancing guest ticks is an input to the probe, not a gameplay replay.
    for (uint step = 1; step <= 30; step++)
    {
        Set("_guestTicks", 3400u + step * FramePacing.RefTicks);
        Call("SyncWorldDraw", m);
    }
    uint after = m.ReadU32(Constant("DrawCountAddr"));
    observations.Add(new { level, name, paused, active, before, after, delta = after - before });
}
string json = JsonSerializer.Serialize(new {
    probe = "SyncWorldDraw with synthetic guest memory",
    simulatedSeconds = 1,
    configuredFps = ConfigManager.View.FrameRate,
    gameplayReproduced = false,
    observations
}, new JsonSerializerOptions { WriteIndented = true });
if (output != null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    File.WriteAllText(output, json + Environment.NewLine);
}
Console.WriteLine(json);
// Zero means the probe completed. It is not a claim that pause behavior is correct.
return 0;
