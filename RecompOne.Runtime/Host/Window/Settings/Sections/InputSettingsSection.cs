using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using RecompOne.Runtime.Config;

namespace RecompOne.Runtime.Host.Window;

internal sealed class InputSettingsSection : ISettingsSection
{
    public string Id => "input";
    public string Title => "Input";
    public int Order => 0;

    static readonly (string Label, Func<KeyBindings, string> GetKey, Action<KeyBindings, string> SetKey, Func<GamepadBindings, int[]> GetPad, Action<GamepadBindings, int[]> SetPad)[] _rows =
    [
        ("Cross", b => b.Cross, (b,v) => b.Cross = v, p => p.Cross, (p,v) => p.Cross = v),
        ("Circle", b => b.Circle, (b,v) => b.Circle = v, p => p.Circle, (p,v) => p.Circle = v),
        ("Square", b => b.Square, (b,v) => b.Square = v, p => p.Square, (p,v) => p.Square = v),
        ("Triangle", b => b.Triangle, (b,v) => b.Triangle = v, p => p.Triangle, (p,v) => p.Triangle = v),
        ("L1", b => b.L1, (b,v) => b.L1 = v, p => p.L1, (p,v) => p.L1 = v),
        ("R1", b => b.R1, (b,v) => b.R1 = v, p => p.R1, (p,v) => p.R1 = v),
        ("L2", b => b.L2, (b,v) => b.L2 = v, p => p.L2, (p,v) => p.L2 = v),
        ("R2", b => b.R2, (b,v) => b.R2 = v, p => p.R2, (p,v) => p.R2 = v),
        ("L3", b => b.L3, (b,v) => b.L3 = v, p => p.L3, (p,v) => p.L3 = v),
        ("R3", b => b.R3, (b,v) => b.R3 = v, p => p.R3, (p,v) => p.R3 = v),
        ("Start", b => b.Start, (b,v) => b.Start = v, p => p.Start, (p,v) => p.Start = v),
        ("Select", b => b.Select, (b,v) => b.Select = v, p => p.Select, (p,v) => p.Select = v),
        ("Up", b => b.Up, (b,v) => b.Up = v, p => p.Up, (p,v) => p.Up = v),
        ("Down", b => b.Down, (b,v) => b.Down = v, p => p.Down, (p,v) => p.Down = v),
        ("Left", b => b.Left, (b,v) => b.Left = v, p => p.Left, (p,v) => p.Left = v),
        ("Right", b => b.Right, (b,v) => b.Right = v, p => p.Right, (p,v) => p.Right = v),
    ];

    bool _gamepadMode;
    int _padIndex;
    int _remapRow = -1;
    bool _remapAdd;
    bool _remapCheatMenu;
    /// <summary>
    /// Capture takes a press only after everything was let go once: Cross or Enter that
    /// picked the row is still down on the next frames and would bind itself.
    /// </summary>
    bool _captureArmed;

    static long _padCaptureUntil;

    /// <summary>Waiting for a pad button to bind: the pause menu leaves the pad alone.</summary>
    public static bool PadCaptureActive => Environment.TickCount64 < _padCaptureUntil;

    public void Draw()
    {
        // Refreshed while the row waits; lapses on its own when Settings closes.
        if (_gamepadMode && _remapRow >= 0)
            _padCaptureUntil = Environment.TickCount64 + 250;

        DrawDeviceSelector();
        ImGui.Spacing();
        DrawPadSelector();
        ImGui.Spacing();

        if (_gamepadMode && !InputManager.IsPadConnected(_padIndex))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.75f, 0.3f, 1f));
            ImGui.TextUnformatted("No gamepad connected.");
            ImGui.PopStyleColor();
            ImGui.Spacing();
        }

        DrawBindings();

        if (!_gamepadMode)
        {
            ImGui.Spacing();
            DrawHostHotkeys();
        }
        else if (_padIndex == 0)
        {
            ImGui.Spacing();
            DrawAnalogMovement();
        }

        ImGui.Spacing();
        if (ImGui.Button("Reset to Defaults"))
        {
            if (!_gamepadMode)
            {
                if (_padIndex == 0) ConfigManager.Game.Keys = new KeyBindings();
                else ConfigManager.Game.Keys2 = KeyBindings.Empty();
                ConfigManager.View.CheatMenuKey = "F3";
                ConfigManager.View.FrameRateHotkeys = true;
                ConfigManager.View.SaveStates = true;
                ConfigManager.SaveView(PanelManager.Panels);
            }
            else
            {
                if (_padIndex == 0) ConfigManager.Game.Pad = new GamepadBindings();
                else ConfigManager.Game.Pad2 = GamepadBindings.Empty();
            }
            _remapRow = -1;
            _remapCheatMenu = false;
            ConfigManager.SaveGame();
        }
    }

    void DrawDeviceSelector()
    {
        if (ImGui.BeginTabBar("##input-device"))
        {
            if (ImGui.BeginTabItem("Keyboard"))
            {
                if (_gamepadMode) { _remapRow = -1; _remapCheatMenu = false; }
                _gamepadMode = false;
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Gamepad"))
            {
                if (!_gamepadMode) { _remapRow = -1; _remapCheatMenu = false; }
                _gamepadMode = true;
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    void DrawPadSelector()
    {
        if (ImGui.BeginTabBar("##input-pad"))
        {
            if (ImGui.BeginTabItem("Pad 1"))
            {
                if (_padIndex != 0) _remapRow = -1;
                _padIndex = 0;
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Pad 2"))
            {
                if (_padIndex != 1) _remapRow = -1;
                _padIndex = 1;
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }
    }

    void DrawBindings()
    {
        if (!ImGui.BeginTable("##bindings", 2,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Button", ImGuiTableColumnFlags.WidthFixed, 90);
        ImGui.TableSetupColumn(_gamepadMode ? "Gamepad" : "Keyboard", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        var keys = _padIndex == 0 ? ConfigManager.Game.Keys : ConfigManager.Game.Keys2;
        var pad = _padIndex == 0 ? ConfigManager.Game.Pad : ConfigManager.Game.Pad2;

        for (int i = 0; i < _rows.Length; i++)
        {
            var (label, getKey, setKey, getPad, setPad) = _rows[i];
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(label);

            ImGui.TableSetColumnIndex(1);
            bool awaiting = _remapRow == i;

            if (_gamepadMode)
            {
                var bindings = getPad(pad);
                string text = awaiting
                    ? _remapAdd ? "[press button to add...]" : "[press button...]"
                    : bindings.Length == 0 ? "unbound" : string.Join(" | ", bindings.Select(PadLabel));

                float plusW = ImGui.GetFrameHeight();
                float spacing = ImGui.GetStyle().ItemSpacing.X;
                if (ImGui.Button($"{text}##p{i}", new Vector2(-plusW - spacing, 0)))
                {
                    _remapRow = i;
                    _remapAdd = false;
                    _captureArmed = false;
                }
                ImGui.SameLine();
                if (ImGui.Button($"+##add{i}", new Vector2(plusW, 0)))
                {
                    _remapRow = i;
                    _remapAdd = true;
                    _captureArmed = false;
                }

                if (awaiting)
                {
                    var p = InputManager.GetFirstPressedPadButton(_padIndex);
                    if (!_captureArmed)
                        _captureArmed = !p.HasValue;
                    else if (p.HasValue)
                    {
                        if (_remapAdd)
                        {
                            if (!bindings.Contains(p.Value)) setPad(pad, [.. bindings, p.Value]);
                        }
                        else setPad(pad, [p.Value]);
                        _remapRow = -1;
                        ConfigManager.SaveGame();
                    }
                }
            }
            else
            {
                var key = getKey(keys);
                var text = awaiting ? "[press key...]" : $"{(key.Length == 0 ? "unbound" : key)}##k{i}";
                if (ImGui.Button(text, new Vector2(-1, 0)))
                {
                    _remapRow = i;
                    _remapCheatMenu = false;
                    _captureArmed = false;
                }
                if (awaiting)
                {
                    var p = GetPressedKey();
                    if (!_captureArmed)
                        _captureArmed = p == null;
                    else if (p != null) { setKey(keys, p); _remapRow = -1; ConfigManager.SaveGame(); }
                }
            }
        }

        ImGui.EndTable();
    }

    void DrawHostHotkeys()
    {
        ImGui.TextUnformatted("Host hotkeys");
        if (!ImGui.BeginTable("##host-hotkeys", 2,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 120);
        ImGui.TableSetupColumn("Keyboard", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Developer menu");
        ImGui.TableSetColumnIndex(1);

        var key = ConfigManager.View.CheatMenuKey;
        var text = _remapCheatMenu ? "[press key...]" : $"{key}##cheatMenu";
        if (ImGui.Button(text, new Vector2(-1, 0)))
        {
            _remapCheatMenu = true;
            _remapRow = -1;
            _captureArmed = false;
        }
        if (_remapCheatMenu)
        {
            var p = GetPressedKey();
            if (!_captureArmed)
                _captureArmed = p == null;
            else if (p != null)
            {
                ConfigManager.View.CheatMenuKey = p;
                _remapCheatMenu = false;
                ConfigManager.SaveView(PanelManager.Panels);
            }
        }

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("FPS keys 1-5");
        ImGui.TableSetColumnIndex(1);
        bool fpsKeys = ConfigManager.View.FrameRateHotkeys;
        if (ImGui.Checkbox("Enabled##fpsHotkeys", ref fpsKeys))
        {
            ConfigManager.View.FrameRateHotkeys = fpsKeys;
            ConfigManager.SaveView(PanelManager.Panels);
        }

        ImGui.TableNextRow();
        ImGui.TableSetColumnIndex(0);
        ImGui.TextUnformatted("Quick save F5-F8");
        ImGui.TableSetColumnIndex(1);
        bool saveStates = ConfigManager.View.SaveStates;
        if (ImGui.Checkbox("Enabled##saveStates", ref saveStates))
        {
            ConfigManager.View.SaveStates = saveStates;
            ConfigManager.SaveView(PanelManager.Panels);
        }

        ImGui.EndTable();
        ImGuiEx.TextDisabled("1 original, 2 60, 3 120, 4 240, 5 uncapped");
        ImGuiEx.TextDisabled("F5 save state, F8 load, F6/F7 slot (also in the pause menu)");
    }

    static void DrawAnalogMovement()
    {
        bool analog = ConfigManager.View.AnalogMovement;
        if (ImGui.Checkbox("Analog movement (left stick)", ref analog))
        {
            ConfigManager.View.AnalogMovement = analog;
            ConfigManager.SaveView(PanelManager.Panels);
        }
        ImGuiEx.TextDisabled("Left stick steers Crash in any direction, not only 8. D-pad unchanged.");
    }

    static string? GetPressedKey()
    {
        foreach (Key k in Enum.GetValues<Key>())
        {
            if (k is Key.Unknown or Key.Menu) continue;
            if (InputManager.IsKeyDown(k)) return k.ToString();
        }
        return null;
    }

    static string PadLabel(int b) => b switch
    {
        0 => "Cross (A)",
        1 => "Circle (B)",
        2 => "Square (X)",
        3 => "Triangle (Y)",
        4 => "Select (Back)",
        5 => "Guide",
        6 => "Start",
        7 => "L3 (LStick)",
        8 => "R3 (RStick)",
        9 => "L1 (LBumper)",
        10 => "R1 (RBumper)",
        11 => "D-Up",
        12 => "D-Down",
        13 => "D-Left",
        14 => "D-Right",
        100 => "L2 (LTrigger)",
        101 => "R2 (RTrigger)",
        102 => "LStick Left",
        103 => "LStick Right",
        104 => "LStick Up",
        105 => "LStick Down",
        106 => "RStick Left",
        107 => "RStick Right",
        108 => "RStick Up",
        109 => "RStick Down",
        _ => $"Btn {b}",
    };
}
