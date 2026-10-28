// Editor settings kept between sessions (wideeditor.settings.json next to the exe):
// disc, text size, view and snap options, last camera of every level.
using System.Text.Json;

namespace WideEditor;

sealed class CameraPref
{
    public float[] Pos { get; set; } = [0, 0, 0];
    public float Pitch { get; set; }
    public float Yaw { get; set; }
    public string? Zone { get; set; }
    public bool LockZone { get; set; }
}

sealed class Prefs
{
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    static string File => Path.Combine(AppContext.BaseDirectory, "wideeditor.settings.json");

    public string? Disc { get; set; }
    public float Text { get; set; } = 1;
    public bool GameView { get; set; } = true;
    public bool Void { get; set; } = true;
    public bool ShowAuto { get; set; } = true;
    public bool ShowUser { get; set; } = true;
    public bool TintAuto { get; set; }
    public bool TintUser { get; set; } = true;
    public bool Wire { get; set; }
    public bool AllMeshes { get; set; }
    public float Speed { get; set; } = 1;
    public bool SnapVerts { get; set; } = true;
    public bool Weld { get; set; } = true;
    public float Grid { get; set; }
    public float RotStep { get; set; } = 15;
    public bool NewBehind { get; set; }
    public bool PickRetail { get; set; } = true;
    public bool PickAuto { get; set; } = true;
    public bool PickUser { get; set; } = true;
    public string GizmoMode { get; set; } = nameof(WideEditor.GizmoMode.All);
    public bool ShowOff { get; set; }
    public bool FlatLight { get; set; }
    public float ViewBright { get; set; } = 1;
    public bool MarkHidden { get; set; } = true;
    public bool GhostCore { get; set; } = true;
    public bool BoxThrough { get; set; }
    public bool ExtendLone { get; set; }
    public bool BridgeCurve { get; set; } = true;
    public string SculptMode { get; set; } = nameof(WideEditor.SculptMode.Smooth);
    public float SculptRadius { get; set; } = 600;
    public float SculptStrength { get; set; } = 0.5f;
    public bool SculptAutos { get; set; } = true;
    public bool SculptLockOriginals { get; set; } = true;
    public int ExtendSteps { get; set; } = 3;
    public string ExtendSlope { get; set; } = nameof(WideEditor.ExtendSlope.Fade);
    public string ExtendSide { get; set; } = nameof(WideEditor.ExtendSide.Bands);
    public Dictionary<string, CameraPref> Cameras { get; set; } = [];

    public static Prefs Load()
    {
        try
        {
            if (System.IO.File.Exists(File))
                return JsonSerializer.Deserialize<Prefs>(System.IO.File.ReadAllText(File), Options) ?? new Prefs();
        }
        catch (Exception e) when (e is IOException or JsonException) { }
        return new Prefs();
    }

    public string Json() => JsonSerializer.Serialize(this, Options);

    public static void Write(string json)
    {
        try { System.IO.File.WriteAllText(File, json); }
        catch (IOException) { }
    }
}
