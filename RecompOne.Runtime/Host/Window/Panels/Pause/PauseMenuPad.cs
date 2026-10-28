using System.Numerics;
using ImGuiNET;
using MenuPad = RecompOne.Runtime.Host.InputManager.MenuPad;

namespace RecompOne.Runtime.Host.Window;

/// <summary>
/// Gamepad control of the pause menu in the launcher layout: D-pad or left stick moves,
/// Cross picks, Circle goes back, L1/R1 switch the Settings tabs, L2/R2 change the volume.
/// Moves and picks go through ImGui gamepad navigation. Start (resume) is handled together
/// with the game's Start in <see cref="StartPauseMenu"/>.
/// </summary>
internal static class PauseMenuPad
{
    const long RepeatDelayMs = 400;
    const long RepeatMs = 90;

    static MenuPad _held;
    static MenuPad _live;
    static bool _padDriven;
    static int _revealIn;
    static ImGuiKey _revealKey;
    static long _volumeNext;

    /// <summary>The last input came from a gamepad, not the mouse.</summary>
    public static bool PadDriven => _padDriven;

    /// <summary>
    /// A menu page appeared. ImGui puts its nav cursor on the first item but keeps it hidden
    /// after mouse use, and then Cross does nothing. A press of a direction with no item that
    /// way shows it in place: <paramref name="emptyDirection"/> is such a direction.
    /// </summary>
    public static void PageShown(ImGuiKey emptyDirection)
    {
        if (!_padDriven) return;
        // The nav cursor lands on the next frame; press after that.
        _revealIn = 2;
        _revealKey = emptyDirection;
    }

    /// <summary>Once per UI frame, before ImGui's NewFrame.</summary>
    public static void Update()
    {
        if (ImGui.GetCurrentContext() == IntPtr.Zero) return;
        var io = ImGui.GetIO();
        var pause = PanelManager.Get<PauseMenuPopup>();
        var now = InputManager.ReadMenuPad();
        var pressed = now & ~_held;
        _held = now;
        if (pressed != MenuPad.None) _padDriven = true;
        if (io.MouseDelta != Vector2.Zero || io.MouseWheel != 0f) _padDriven = false;

        // Binding a pad button in Settings → Input: every press belongs to the binding.
        bool active = pause is { IsOpen: true } && !InputSettingsSection.PadCaptureActive;
        if (active)
        {
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableGamepad;
            io.BackendFlags |= ImGuiBackendFlags.HasGamepad;
        }
        else
        {
            io.ConfigFlags &= ~ImGuiConfigFlags.NavEnableGamepad;
            io.BackendFlags &= ~ImGuiBackendFlags.HasGamepad;
        }

        // Only buttons pressed while the menu takes the pad reach ImGui: a Cross still down
        // after binding it in Settings → Input must not press the focused button again.
        _live = active ? (_live & now) | pressed : MenuPad.None;
        // Held state every frame (ImGui drops repeats and does its own key repeat).
        io.AddKeyEvent(ImGuiKey.GamepadDpadUp, _live.HasFlag(MenuPad.Up));
        io.AddKeyEvent(ImGuiKey.GamepadDpadDown, _live.HasFlag(MenuPad.Down));
        io.AddKeyEvent(ImGuiKey.GamepadDpadLeft, _live.HasFlag(MenuPad.Left));
        io.AddKeyEvent(ImGuiKey.GamepadDpadRight, _live.HasFlag(MenuPad.Right));
        io.AddKeyEvent(ImGuiKey.GamepadFaceDown, _live.HasFlag(MenuPad.Cross));
        if (!active)
        {
            _revealIn = 0;
            return;
        }

        if (_revealIn > 0 && --_revealIn == 0 && (now & (MenuPad.Up | MenuPad.Down | MenuPad.Left | MenuPad.Right)) == 0)
            Tap(io, _revealKey);

        if (pressed.HasFlag(MenuPad.Circle))
        {
            // An open list or a slider being tweaked closes first (ImGui cancel), then the page.
            if (ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel)
                || ImGui.IsAnyItemActive())
                Tap(io, ImGuiKey.GamepadFaceRight);
            else
                pause!.Back();
        }

        if (pause!.OnSettingsPage)
        {
            if (pressed.HasFlag(MenuPad.L1)) pause.StepSettingsTab(-1);
            if (pressed.HasFlag(MenuPad.R1)) pause.StepSettingsTab(1);
        }

        int dir = (_live.HasFlag(MenuPad.R2) ? 1 : 0) - (_live.HasFlag(MenuPad.L2) ? 1 : 0);
        if (dir != 0)
        {
            long t = Environment.TickCount64;
            if ((pressed & (MenuPad.L2 | MenuPad.R2)) != 0)
            {
                _volumeNext = t + RepeatDelayMs;
                PauseMenuPopup.StepVolume(dir);
            }
            else if (t >= _volumeNext)
            {
                _volumeNext = t + RepeatMs;
                PauseMenuPopup.StepVolume(dir);
            }
        }
    }

    /// <summary>Press and release in one frame; ImGui's trickled input queue sees both.</summary>
    static void Tap(ImGuiIOPtr io, ImGuiKey key)
    {
        io.AddKeyEvent(key, true);
        io.AddKeyEvent(key, false);
    }
}
