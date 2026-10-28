using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Host.Cheats;

namespace RecompOne.Runtime.Host.Window;

/// <summary>
/// Pause overlay (Esc, or Start with <see cref="Config.ViewConfig.StartPauseMenu"/>): resume,
/// settings, open the in-game save/exit flow, or leave the session. Drawn in the launcher look
/// (<see cref="CrashUi"/>) unless CrashMenuStyle is off. Settings is a second page of the same
/// card (<see cref="SettingsView"/>); each page is its own ImGui window so keyboard and pad
/// navigation start on its first item. Pad control: <see cref="PauseMenuPad"/>.
/// While open the game thread is parked in <see cref="HostWindow.HoldWhilePaused"/>.
/// </summary>
internal sealed class PauseMenuPopup : IPanel
{
    public string Name => "Pause";

    const float MenuWidth = 260f;
    const float CrashWidth = 320f;
    const float SettingsWidth = 760f;
    const float SettingsHeight = 520f;
    const float VolumeStep = 0.05f;

    bool _open;
    bool _focusOnce;
    bool _settingsPage;
    bool _focusSettingsRow;
    float _footerHeight;
    readonly SettingsView _settings = new();

    public bool IsOpen
    {
        get => _open;
        set
        {
            if (_open == value) return;
            _open = value;
            _focusOnce = false;
            _settingsPage = false;
            _focusSettingsRow = false;
        }
    }

    public bool OnSettingsPage => _open && _settingsPage;

    /// <summary>Esc: Settings page back to the pause page, otherwise toggle the overlay.</summary>
    public void HandleEscape()
    {
        if (_open && _settingsPage)
            ShowPage(settings: false);
        else
            IsOpen = !_open;
    }

    /// <summary>Pad Circle: Settings page back to the pause page, pause page resumes.</summary>
    public void Back()
    {
        if (_settingsPage)
            ShowPage(settings: false);
        else
            IsOpen = false;
    }

    /// <summary>
    /// Select while paused, like the original pause screen: back to the map from a level,
    /// to the title from the map. Nothing where the Exit row is disabled.
    /// </summary>
    public void ExitToMap()
    {
        if (!CanExit(out _) || ExitToMapInjector.Active) return;
        // Native Crash 1 path: Start (pause) → Select (return to map / to the title on the map).
        ExitToMapInjector.Begin();
        IsOpen = false;
    }

    /// <summary>
    /// In a level, or on the island map where the game's pause allows Select (it returns to
    /// the title there; Start may open this menu instead, so the same exit is offered).
    /// </summary>
    static bool CanExit(out bool mapPause)
    {
        mapPause = StartPauseMenu.MapPauseAllowed();
        return mapPause || !CheatManager.IsOnTitleMenuMap();
    }

    /// <summary>L1/R1 on the Settings page.</summary>
    public void StepSettingsTab(int direction) => _settings.StepTab(direction);

    void ShowPage(bool settings)
    {
        if (_settingsPage == settings) return;
        _settingsPage = settings;
        // Coming back from Settings the cursor returns to its row, not to Resume.
        _focusSettingsRow = !settings;
        _focusOnce = false;
    }

    /// <summary>L2/R2: master volume down/up one 5% step; up also unmutes.</summary>
    public static void StepVolume(int direction)
    {
        var game = Config.ConfigManager.Game;
        float steps = MathF.Round(game.MasterVolume / VolumeStep) + direction;
        if (direction > 0) game.Muted = false;
        SetVolume(Math.Clamp(steps * VolumeStep, 0f, 1f));
        DevHudOverlay.Flash(game.Muted ? "Volume muted" : $"Volume {MathF.Round(game.MasterVolume * 100f):0}%");
    }

    static void SetVolume(float volume)
    {
        var game = Config.ConfigManager.Game;
        game.MasterVolume = volume;
        Audio.SetMasterVolume(game.Muted ? 0f : game.MasterVolume);
        Config.ConfigManager.SaveGame();
    }

    public void Draw()
    {
        var vp = ImGui.GetMainViewport();
        float ui = Math.Clamp(ImGui.GetIO().FontGlobalScale, 1f, 2.5f);
        bool crash = CrashUi.Enabled;
        bool settingsPage = _settingsPage;

        CrashUi.Backdrop("##pause-dim", vp, ui);

        if (settingsPage)
        {
            ImGui.SetNextWindowSize(new Vector2(
                Math.Min(SettingsWidth * ui, vp.WorkSize.X * 0.95f),
                Math.Min(SettingsHeight * ui, vp.WorkSize.Y * 0.9f)), ImGuiCond.Always);
        }
        else
        {
            float width = (crash ? CrashWidth : MenuWidth) * ui;
            ImGui.SetNextWindowSize(new Vector2(width, 0f), ImGuiCond.Always);
            ImGui.SetNextWindowSizeConstraints(
                new Vector2(width, 0f),
                new Vector2(width, vp.WorkSize.Y * 0.9f));
        }
        ImGui.SetNextWindowPos(vp.GetCenter(), ImGuiCond.Always, new Vector2(0.5f, 0.5f));

        if (!_focusOnce)
        {
            ImGui.SetNextWindowFocus();
            _focusOnce = true;
            // Pause page rows span the card (nothing to their left); Settings opens on its
            // tabs with nothing focusable above them once the X is hidden for the pad.
            PauseMenuPad.PageShown(settingsPage ? ImGuiKey.GamepadDpadUp : ImGuiKey.GamepadDpadLeft);
        }

        using var chrome = CrashUi.Card(ui, new Vector2(12 * ui, 10 * ui), 0.92f);

        // Local copy: ImGui may clear this via the title-bar X. Menu actions must
        // mutate `open` too — assigning only IsOpen was overwritten by `IsOpen = open`.
        bool open = _open;
        var flags = ImGuiWindowFlags.NoCollapse
                    | ImGuiWindowFlags.NoDocking
                    | ImGuiWindowFlags.NoSavedSettings
                    | ImGuiWindowFlags.NoResize
                    | ImGuiWindowFlags.NoMove
                    // Auto-fit at fractional UI scales showed an always-full scrollbar.
                    | ImGuiWindowFlags.NoScrollbar;
        if (!settingsPage) flags |= ImGuiWindowFlags.AlwaysAutoResize;
        if (crash) flags |= ImGuiWindowFlags.NoTitleBar;

        if (!ImGui.Begin(settingsPage ? "Settings###pause-settings" : "Pause###pause-main", ref open, flags))
        {
            IsOpen = open;
            ImGui.End();
            return;
        }

        CrashUi.CardDecor(ui);
        if (settingsPage)
        {
            DrawSettingsPage(ui, crash);
            IsOpen = open;
            ImGui.End();
            return;
        }

        CrashUi.Header("Crash Recompiled", "Paused", ui);

        if (CrashUi.Row("Resume", "resume", ui, icon: CrashUi.RowIcon.Resume))
            open = false;

        bool saveStates = Config.ConfigManager.View.SaveStates;
        if (saveStates && DrawSaveStates(ui))
            open = false;

        // Settings replaces the rows inside this card, so gameplay input stays muted.
        if (CrashUi.Row("Settings", "settings", ui, icon: CrashUi.RowIcon.Settings))
            ShowPage(settings: true);
        if (_focusSettingsRow)
        {
            ImGui.SetItemDefaultFocus();
            _focusSettingsRow = false;
        }

        bool canExit = CanExit(out bool mapPause);
        if (CrashUi.Row(mapPause ? "Exit to Title..." : "Exit to Map...", "exit-map", ui,
                enabled: canExit, icon: CrashUi.RowIcon.Map))
        {
            // Native Crash 1 path: Start (pause) → Select (return to map / to the title on the map).
            ExitToMapInjector.Begin();
            open = false;
        }

        string leaveLabel = HostWindow.IsEmbedded ? "Return to Launcher" : "Quit";
        if (CrashUi.Row(leaveLabel, "leave", ui, icon: CrashUi.RowIcon.Leave))
        {
            open = false;
            // Never Close() mid-ImGui frame — defer to Present after DoRender.
            HostWindow.RequestEndSession();
        }

        CrashUi.Divider(ui);

        var game = Config.ConfigManager.Game;
        float pct = game.MasterVolume * 100f;
        if (crash)
        {
            CrashUi.Caption("Volume");
            ImGui.SetNextItemWidth(-1f);
        }
        if (ImGui.SliderFloat(crash ? "##volume" : "Volume", ref pct, 0f, 100f, "%.0f%%"))
            SetVolume(Math.Clamp(pct / 100f, 0f, 1f));

        bool muted = game.Muted;
        if (ImGui.Checkbox("Mute", ref muted))
        {
            game.Muted = muted;
            Audio.SetMasterVolume(muted ? 0f : game.MasterVolume);
            Config.ConfigManager.SaveGame();
        }

        CrashUi.Divider(ui);
        if (PauseMenuPad.PadDriven && canExit)
            CrashUi.Hints(ui, ("Cross", "Choose"), ("Circle", "Resume"), ("Select", mapPause ? "Title" : "Map"),
                ("L2/R2", "Volume"));
        else if (PauseMenuPad.PadDriven)
            CrashUi.Hints(ui, ("Cross", "Choose"), ("Circle", "Resume"), ("L2/R2", "Volume"));
        else if (saveStates)
            CrashUi.Hints(ui, ("Esc", "Resume"), ("F5", "Save"), ("F8", "Load"), ("F6/F7", "Slot"));
        else
            CrashUi.Hints(ui, ("Esc", "Resume"));

        IsOpen = open;
        ImGui.End();
    }

    /// <summary>Settings page: header with back button, sections, key hints.</summary>
    void DrawSettingsPage(float ui, bool crash)
    {
        // Pad: Circle goes back, and a focusable X above the tabs would take the first Up.
        bool pad = PauseMenuPad.PadDriven;
        if (CrashUi.Header("Paused", "Settings", ui, closeButton: !pad))
            ShowPage(settings: false);
        if (!crash && ImGui.Button("< Back"))
            ShowPage(settings: false);

        // Footer height from the last frame keeps the hints pinned to the card bottom.
        float spacing = ImGui.GetStyle().ItemSpacing.Y;
        float footer = _footerHeight > 0f ? _footerHeight : 34f * ui;
        float body = Math.Max(80f * ui, ImGui.GetContentRegionAvail().Y - footer - spacing);
        _settings.Draw(ui, body);

        float start = ImGui.GetCursorPosY();
        CrashUi.Divider(ui);
        if (pad)
            CrashUi.Hints(ui, ("Cross", "Choose"), ("Circle", "Back"), ("L1/R1", "Tab"), ("L2/R2", "Volume"));
        else
            CrashUi.Hints(ui, ("Esc", "Back"));
        _footerHeight = ImGui.GetCursorPosY() - start;
    }

    /// <summary>Quick save rows (#130). True when an action should close the menu.</summary>
    static bool DrawSaveStates(float ui)
    {
        int slot = SaveStates.Slot;
        var info = SaveStates.GetInfo(slot);
        bool close = false;
        string tag = $"Slot {slot}";

        if (CrashUi.Row("Save State", "state-save", ui, tag: tag, icon: CrashUi.RowIcon.SaveState))
        {
            SaveStates.RequestSave(slot);
            close = true;
        }

        if (CrashUi.Row("Load State", "state-load", ui, enabled: info is { Compatible: true }, tag: tag,
            icon: CrashUi.RowIcon.LoadState))
        {
            SaveStates.RequestLoad(slot);
            close = true;
        }

        // Crash style: line up with the row labels (past the Wumpa cursor gutter, 1.45 × 20 px).
        float indent = CrashUi.Enabled ? 29f * ui : ImGui.GetStyle().IndentSpacing;
        ImGui.Indent(indent);
        if (ImGui.ArrowButton("##state-prev", ImGuiDir.Left)) SaveStates.StepSlot(-1);
        ImGui.SameLine();
        if (ImGui.ArrowButton("##state-next", ImGuiDir.Right)) SaveStates.StepSlot(1);
        ImGui.SameLine();
        ImGui.TextUnformatted($"Slot {slot}");
        ImGui.PushTextWrapPos(0f);
        ImGuiEx.TextDisabled(info?.Describe() ?? "Empty");
        ImGui.PopTextWrapPos();
        ImGui.Unindent(indent);
        return close;
    }
}
