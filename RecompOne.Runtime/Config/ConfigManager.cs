using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImGuiNET;
using RecompOne.Runtime.Host.Window;

namespace RecompOne.Runtime.Config;

static file class PanelDefaults
{
    public static bool IsOpenByDefault(IPanel p) => p.Name == "Output";
}

public static class ConfigManager
{
    static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    static string GameConfigPath => AppPaths.SettingsPath;
    static string InterfaceFile => AppPaths.InterfacePath;

    public static GameConfig Game { get; private set; } = new();
    public static ViewConfig  View { get; private set; } = new();

    static string? _pendingImGuiIni;

    public static void Load()
    {
        AppPaths.EnsureCreated();

        if (File.Exists(GameConfigPath))
        {
            try { Game = JsonSerializer.Deserialize<GameConfig>(File.ReadAllText(GameConfigPath), _opts) ?? new(); }
            catch { Game = new(); }
        }
        else
        {
            SaveGame();
        }

        if (File.Exists(InterfaceFile))
        {
            var (view, imguiIni) = ParseInterfaceFile(File.ReadAllText(InterfaceFile));
            View = view;
            _pendingImGuiIni = imguiIni;
        }
    }

    
    public static bool ApplyImGuiLayout()
    {
        if (_pendingImGuiIni == null) return false;
        ImGui.LoadIniSettingsFromMemory(_pendingImGuiIni);
        _pendingImGuiIni = null;
        return true;
    }

    public static void ApplyViewToPanels(IReadOnlyList<IPanel> panels)
    {
        foreach (var p in panels)
        {
            if (View.Panels.TryGetValue(p.Name, out var state))
                p.IsOpen = state.Open;
        }

        // Game display must always be visible — recover from older interface.ini
        // that saved Panels.Output=False after the user closed the Output window.
        foreach (var p in panels)
        {
            if (p.Name == "Output")
                p.IsOpen = true;
        }
    }

    public static void SaveView(IReadOnlyList<IPanel> panels)
    {
        foreach (var p in panels)
            View.Panels[p.Name] = new PanelState { Open = p.IsOpen };

        // Output is the game framebuffer — never persist it closed.
        View.Panels["Output"] = new PanelState { Open = true };

        // Launcher has no ImGui context — and after an embedded session a
        // dangling context pointer can still be non-zero. Only snapshot ImGui ini when
        // the in-game host is actively saving with live panels.
        string imguiIni;
        try
        {
            if (panels.Count > 0 && ImGui.GetCurrentContext() != IntPtr.Zero)
                imguiIni = ImGui.SaveIniSettingsToMemory() ?? "";
            else
                imguiIni = ReadExistingImGuiSection();
        }
        catch
        {
            imguiIni = ReadExistingImGuiSection();
        }

        var sb = new StringBuilder();
        sb.AppendLine("[RecompOne]");
        foreach (var (key, value) in View.Values)
            sb.AppendLine($"{key}={value}");
        foreach (var (name, state) in View.Panels)
            sb.AppendLine($"Panels.{name}={state.Open}");
        sb.AppendLine();
        sb.Append(imguiIni);
        File.WriteAllText(InterfaceFile, sb.ToString());
    }

    static string ReadExistingImGuiSection()
    {
        try
        {
            if (!File.Exists(InterfaceFile)) return "";
            var all = File.ReadAllText(InterfaceFile);
            // File layout: [RecompOne] … blank line … ImGui ini blob
            var marker = "\n\n";
            var idx = all.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return "";
            return all[(idx + marker.Length)..];
        }
        catch
        {
            return "";
        }
    }

    public static void ResetView(IReadOnlyList<IPanel> panels)
    {
        View = new();
        foreach (var p in panels)
            p.IsOpen = PanelDefaults.IsOpenByDefault(p);
        try
        {
            if (ImGui.GetCurrentContext() != IntPtr.Zero)
                ImGui.LoadIniSettingsFromMemory("");
        }
        catch
        {
            // Android (and any host without a live ImGui context) only resets ViewConfig.
        }
        SaveView(panels);
    }

    public static void SaveGame()
    {
        AppPaths.EnsureCreated();
        File.WriteAllText(GameConfigPath, JsonSerializer.Serialize(Game, _opts));
    }

    static (ViewConfig view, string imguiIni) ParseInterfaceFile(string content)
    {
        var view = new ViewConfig();
        var imguiLines = new List<string>();
        bool inRecompOne = false;

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            if (line == "[RecompOne]")
            {
                inRecompOne = true;
                continue;
            }

            if (line.StartsWith('['))
                inRecompOne = false;

            if (inRecompOne)
            {
                if (line.Length == 0) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq];
                var value = line[(eq + 1)..];
                if (key.StartsWith("Panels."))
                {
                    var panelName = key[7..];
                    var open = bool.TryParse(value, out var b) && b;
                    view.Panels[panelName] = new PanelState { Open = open };
                }
                else
                {
                    view.Values[key] = value;
                }
            }
            else
            {
                imguiLines.Add(line);
            }
        }

        return (view, string.Join('\n', imguiLines));
    }
}
