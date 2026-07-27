using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Host.Cheats;

namespace RecompOne.Runtime.Host.Window;

/// <summary>
/// ESC pause overlay: resume, open the in-game save/exit flow, or leave the session.
/// </summary>
internal sealed class PauseMenuPopup : IPanel
{
    public string Name => "Pause";

    const float MenuWidth = 260f;

    bool _open;
    bool _focusOnce;

    public bool IsOpen
    {
        get => _open;
        set
        {
            if (_open == value) return;
            _open = value;
            _focusOnce = false;
        }
    }

    public void Draw()
    {
        var vp = ImGui.GetMainViewport();
        float ui = Math.Clamp(ImGui.GetIO().FontGlobalScale, 1f, 2.5f);
        float width = MenuWidth * ui;

        DrawDim(vp);

        ImGui.SetNextWindowSize(new Vector2(width, 0f), ImGuiCond.Always);
        ImGui.SetNextWindowSizeConstraints(
            new Vector2(width, 0f),
            new Vector2(width, vp.WorkSize.Y * 0.85f));
        ImGui.SetNextWindowPos(vp.GetCenter(), ImGuiCond.Always, new Vector2(0.5f, 0.5f));

        if (!_focusOnce)
        {
            ImGui.SetNextWindowFocus();
            _focusOnce = true;
        }

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12 * ui, 10 * ui));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.14f, 0.14f, 0.14f, 0.92f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.5f, 0.5f, 0.5f, 0.45f));
        ImGui.PushStyleColor(ImGuiCol.TitleBg, new Vector4(0.16f, 0.16f, 0.16f, 0.95f));
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, new Vector4(0.18f, 0.18f, 0.18f, 0.95f));
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.28f, 0.28f, 0.28f, 0.55f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.34f, 0.34f, 0.3f, 0.75f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0.4f, 0.38f, 0.22f, 0.85f));

        // Local copy: ImGui may clear this via the title-bar X. Menu actions must
        // mutate `open` too — assigning only IsOpen was overwritten by `IsOpen = open`.
        bool open = _open;
        var flags = ImGuiWindowFlags.NoCollapse
                    | ImGuiWindowFlags.NoDocking
                    | ImGuiWindowFlags.NoSavedSettings
                    | ImGuiWindowFlags.NoResize
                    | ImGuiWindowFlags.NoMove
                    | ImGuiWindowFlags.AlwaysAutoResize;

        if (!ImGui.Begin(Name, ref open, flags))
        {
            IsOpen = open;
            ImGui.End();
            PopChrome();
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextUnformatted("PAUSED");
        ImGui.PopStyleColor();
        ImGui.Separator();
        ImGui.Spacing();

        if (BookRow("Resume", "resume"))
            open = false;

        bool onMap = CheatManager.IsOnTitleMenuMap();
        if (onMap)
        {
            ImGui.BeginDisabled();
            BookRow("Exit to Map...", "exit-map");
            ImGui.EndDisabled();
        }
        else if (BookRow("Exit to Map...", "exit-map"))
        {
            // Native Crash 1 path: Start (pause) → Select (return to map).
            ExitToMapInjector.Begin();
            open = false;
        }

        string leaveLabel = HostWindow.IsEmbedded ? "Return to Launcher" : "Quit";
        if (BookRow(leaveLabel, "leave"))
        {
            open = false;
            // Never Close() mid-ImGui frame — defer to Present after DoRender.
            HostWindow.RequestEndSession();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var game = Config.ConfigManager.Game;
        float pct = game.MasterVolume * 100f;
        if (ImGui.SliderFloat("Volume", ref pct, 0f, 100f, "%.0f%%"))
        {
            game.MasterVolume = Math.Clamp(pct / 100f, 0f, 1f);
            Audio.SetMasterVolume(game.Muted ? 0f : game.MasterVolume);
            Config.ConfigManager.SaveGame();
        }

        bool muted = game.Muted;
        if (ImGui.Checkbox("Mute", ref muted))
        {
            game.Muted = muted;
            Audio.SetMasterVolume(muted ? 0f : game.MasterVolume);
            Config.ConfigManager.SaveGame();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGuiEx.TextDisabled("Toggle: Esc");

        IsOpen = open;
        ImGui.End();
        PopChrome();
    }

    static void DrawDim(ImGuiViewportPtr vp)
    {
        ImGui.SetNextWindowPos(vp.Pos);
        ImGui.SetNextWindowSize(vp.Size);
        const ImGuiWindowFlags dimFlags =
            ImGuiWindowFlags.NoDecoration
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoInputs
            | ImGuiWindowFlags.NoBringToFrontOnFocus;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0f, 0f, 0f, 0.45f));
        if (ImGui.Begin("##pause-dim", dimFlags))
            ImGui.End();
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }

    static void PopChrome()
    {
        ImGui.PopStyleColor(7);
        ImGui.PopStyleVar(3);
    }

    static bool BookRow(string label, string id)
    {
        bool clicked = ImGui.Selectable($"  {label}##{id}");
        if (ImGui.IsItemHovered() || ImGui.IsItemActive() || ImGui.IsItemFocused())
        {
            var min = ImGui.GetItemRectMin();
            float y = min.Y + (ImGui.GetItemRectSize().Y - ImGui.GetTextLineHeight()) * 0.5f;
            ImGui.GetWindowDrawList().AddText(
                new Vector2(min.X, y),
                ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.92f, 0.4f, 1f)),
                ">");
        }
        return clicked;
    }
}
