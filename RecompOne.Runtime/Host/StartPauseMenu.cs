using RecompOne.Runtime.Config;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Host.Window;

namespace RecompOne.Runtime.Host;

/// <summary>
/// Start opens the pause menu (<see cref="PauseMenuPopup"/>) instead of the game's pause
/// screen (<see cref="ViewConfig.StartPauseMenu"/>), and the pad Start closes it again.
/// Only where retail CoreLoop would pause, so Start still starts the game on the title
/// screen, skips the intro and leaves demos. Runs at the end of every
/// <see cref="InputManager.Poll"/>, before <see cref="ExitToMapInjector.ApplyOverlay"/>,
/// so the injected Start of Exit to Map is never caught.
/// </summary>
internal static class StartPauseMenu
{
    // NTSC-U globals of CoreLoop's can_pause (wurlyfox/c1 main.c; retail 0x80012030).
    const uint PausedAddr = 0x80056400u;
    /// <summary><c>ns.ldat</c>; the level id CoreLoop tests is at +4.</summary>
    const uint NsLdatAddr = 0x8005C53Cu;
    /// <summary>GOOL TITLEPAUSESTATE: IsldC sets it on the map, CardC −1 on the save screens.</summary>
    const uint TitlePauseStateAddr = 0x800619B4u;
    const uint PbakStateAddr = 0x80061A30u;
    /// <summary>Title, menus and the island map.</summary>
    const uint LidTitle = 0x19u;
    const uint LidLevelEnd = 0x2Du;
    const uint LidIntro = 0x38u;

    /// <summary>Everything but the D-pad: held directions keep Crash running after a resume.</summary>
    const ushort LatchButtons = unchecked((ushort)~(Controller.Up | Controller.Down | Controller.Left | Controller.Right));

    static bool _startHeld;
    static bool _padStartHeld;
    static bool _selectHeld;
    static bool _wasOpen;
    static ushort _latch;
    static bool _toggle;
    static bool _exit;

    /// <summary>Start asked to open or close the pause menu (handled in Present).</summary>
    public static bool ConsumeToggle() { var v = _toggle; _toggle = false; return v; }

    /// <summary>Select in the open menu: exit like the original pause screen (handled in Present).</summary>
    public static bool ConsumeExit() { var v = _exit; _exit = false; return v; }

    /// <param name="padStart">Pad 1 Start binding is down (keyboard Start = Enter also picks menu rows).</param>
    public static void Filter(bool padStart)
    {
        // Desktop only: Android has no pause card.
        if (PanelManager.Get<PauseMenuPopup>() is not { } pause) return;

        ushort state = Controller.State;
        bool start = (state & Controller.Start) == 0;
        bool startEdge = start && !_startHeld;
        bool padEdge = padStart && !_padStartHeld;
        bool select = (state & Controller.Select) == 0;
        bool selectEdge = select && !_selectHeld;
        _startHeld = start;
        _padStartHeld = padStart;
        _selectHeld = select;

        // Buttons held when the menu closes stay released for the game until let go: the
        // Cross that picked Resume must not jump, the Start that closed it must not pause.
        bool open = pause.IsOpen;
        _latch &= (ushort)~state;
        if (_wasOpen && !open) _latch |= (ushort)(~state & LatchButtons);
        _wasOpen = open;

        if (!ExitToMapInjector.Active
            && (open
                ? padEdge && !InputSettingsSection.PadCaptureActive
                : startEdge && ConfigManager.View.StartPauseMenu && NativePauseAllowed()))
        {
            _toggle = true;
            _latch |= Controller.Start;
        }

        // Retail pause + Select returns to the map (to the title on the map): same here.
        if (open && selectEdge && !ExitToMapInjector.Active && !InputSettingsSection.PadCaptureActive)
            _exit = true;

        Controller.State = (ushort)(state | _latch);
    }

    /// <summary>
    /// Retail can_pause: no demo playing, and a level that pauses (not title, level end or
    /// intro) or a screen that allows it (the island map). False while the game's own pause
    /// screen is up, so Start closes it as usual.
    /// </summary>
    public static bool NativePauseAllowed() => NativePauseAllowed(out _);

    /// <summary>On the island map with pause allowed: native pause + Select returns to the title.</summary>
    public static bool MapPauseAllowed() => NativePauseAllowed(out uint lid) && lid == LidTitle;

    static bool NativePauseAllowed(out uint lid)
    {
        lid = 0;
        var m = Runtime.Mem;
        if (m == null) return false;
        try
        {
            uint ldat = m.ReadU32(NsLdatAddr);
            if ((ldat & 0xFF000000u) != 0x80000000u) return false;
            lid = m.ReadU32(ldat + 4);
            if (m.ReadU32(PausedAddr) != 0) return false;
            int titlePause = (int)m.ReadU32(TitlePauseStateAddr);
            bool pauseLid = lid is not (LidTitle or LidLevelEnd or LidIntro);
            return m.ReadU32(PbakStateAddr) == 0
                && ((pauseLid && titlePause != -1) || titlePause > 0);
        }
        catch
        {
            return false;
        }
    }
}
