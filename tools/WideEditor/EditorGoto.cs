// F12 in the game (FramePacing.OpenWideEditor): the game writes its camera to
// WideEdits/editor_goto.json and starts the editor with --goto, or, when the editor is
// already open, leaves the file for it to pick up. The editor opens that level at that
// camera and comes to the front.
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WideEditor;

sealed partial class Editor
{
    string? _gotoFile;
    double _gotoCheck;

    void PollGoto()
    {
        if (_time - _gotoCheck < 0.3 && _gotoFile == null) return;
        _gotoCheck = _time;
        string? file = _gotoFile ?? Env.GameDirs().Select(d => Path.Combine(d, "WideEdits", "editor_goto.json"))
            .FirstOrDefault(File.Exists);
        _gotoFile = null;
        if (file == null || !File.Exists(file)) return;
        // A request left over from an earlier session is not a jump the player just asked for.
        if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(file)).TotalSeconds > 30)
        {
            try { File.Delete(file); } catch (IOException) { }
            return;
        }
        SaveSlot? slot = null;
        try
        {
            using (var d = JsonDocument.Parse(File.ReadAllText(file)))
            {
                var r = d.RootElement;
                var cam = r.GetProperty("cam");
                slot = new SaveSlot(file, "F12", r.GetProperty("level").GetInt32(),
                    new Vector3(cam[0].GetSingle(), cam[1].GetSingle(), cam[2].GetSingle()),
                    [.. r.GetProperty("matrix").EnumerateArray().Select(e => (short)e.GetInt32())],
                    Eid.Str(r.GetProperty("zone").GetUInt32()));
            }
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or KeyNotFoundException
            or FormatException or UnauthorizedAccessException)
        {
            // Still being written: next check.
            return;
        }
        ToFront();
        _follow = false;
        if (_screen == Screen.Edit && L != null && L.Id == slot.Level)
        {
            ApplySlot(slot);
            Say("Camera presa dal gioco (F12).");
            return;
        }
        if (_screen == Screen.Loading) return;
        var level = _levels.FirstOrDefault(l => l.Id == slot.Level);
        if (level.Name == null || _disc == null)
        {
            Say($"Il gioco è nel livello {slot.Level}: non si può aprire qui.");
            return;
        }
        if (_dirty) Save();
        StartLoad(level.Id, level.Name, slot);
    }

    void ToFront()
    {
        if (!OperatingSystem.IsWindows() || _window.Native?.Win32 is not { } win) return;
        if (IsIconic(win.Hwnd)) ShowWindow(win.Hwnd, 9);
        SetForegroundWindow(win.Hwnd);
    }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(nint hwnd);
}
