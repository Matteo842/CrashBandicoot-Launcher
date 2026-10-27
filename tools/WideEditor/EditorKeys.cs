// Shortcuts read straight from the keyboard (not through ImGui's key table), so they
// work whatever ImGui thinks has focus: one edge per press, modifiers from both sides.
using Silk.NET.Input;
using K = Silk.NET.Input.Key;

namespace WideEditor;

sealed partial class Editor
{
    IKeyboard? _keyboard;
    readonly HashSet<Key> _keysBefore = [], _keysNow = [];

    bool KeyDown(Key k) => _keyboard?.IsKeyPressed(k) == true;

    /// <summary>True on the frame the key goes down. Call it every frame for that key.</summary>
    bool KeyHit(Key k)
    {
        bool down = KeyDown(k);
        if (down) _keysNow.Add(k);
        return down && !_keysBefore.Contains(k);
    }

    void EndKeys()
    {
        _keysBefore.Clear();
        foreach (var k in _keysNow) _keysBefore.Add(k);
        _keysNow.Clear();
    }

    bool Ctrl => KeyDown(K.ControlLeft) || KeyDown(K.ControlRight) || ImGuiNET.ImGui.GetIO().KeyCtrl;
    bool Shift => KeyDown(K.ShiftLeft) || KeyDown(K.ShiftRight) || ImGuiNET.ImGui.GetIO().KeyShift;
    bool Alt => KeyDown(K.AltLeft) || KeyDown(K.AltRight) || ImGuiNET.ImGui.GetIO().KeyAlt;
}
