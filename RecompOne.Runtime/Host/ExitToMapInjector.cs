using RecompOne.Runtime.Hardware;

namespace RecompOne.Runtime.Host;

/// <summary>
/// Crash 1 native exit: Start (pause) then Select (return to map).
/// Also re-applies host pause mute after every Poll — BiosB.PadRead polls again
/// at frame end and would otherwise let gameplay input through the Esc overlay.
/// </summary>
internal static class ExitToMapInjector
{
    enum Phase : byte
    {
        Idle,
        StartDown,
        StartUp,
        SelectDown,
        SelectUp,
    }

    static Phase _phase;
    static int _framesLeft;

    /// <summary>When true and not injecting, force all pad bits released (Esc pause menu).</summary>
    public static bool MuteGameplay { get; set; }

    public static bool Active => _phase != Phase.Idle;

    public static void Begin()
    {
        _phase = Phase.StartDown;
        // Short pulse — pause is edge-triggered; holding Start too long is unnecessary.
        _framesLeft = 2;
        MuteGameplay = false;
    }

    public static void Cancel()
    {
        _phase = Phase.Idle;
        _framesLeft = 0;
    }

    /// <summary>Re-apply inject or mute (call after every Poll).</summary>
    public static void ApplyOverlay()
    {
        if (_phase != Phase.Idle)
        {
            Controller.State = _phase switch
            {
                Phase.StartDown => (ushort)(0xFFFF & ~Controller.Start),
                Phase.SelectDown => (ushort)(0xFFFF & ~Controller.Select),
                _ => (ushort)0xFFFF,
            };
            Controller.State2 = 0xFFFF;
            return;
        }

        if (!MuteGameplay) return;
        Controller.State = 0xFFFF;
        Controller.State2 = 0xFFFF;
    }

    /// <summary>Advance the sequence once per presented frame.</summary>
    public static void Tick()
    {
        if (_phase == Phase.Idle)
        {
            ApplyOverlay();
            return;
        }

        ApplyOverlay();

        if (--_framesLeft > 0) return;
        Advance();
        ApplyOverlay();
    }

    static void Advance()
    {
        switch (_phase)
        {
            case Phase.StartDown:
                // Wait for in-game pause to open before Select.
                _phase = Phase.StartUp;
                _framesLeft = 12;
                break;
            case Phase.StartUp:
                _phase = Phase.SelectDown;
                _framesLeft = 4;
                break;
            case Phase.SelectDown:
                _phase = Phase.SelectUp;
                _framesLeft = 4;
                break;
            default:
                _phase = Phase.Idle;
                _framesLeft = 0;
                break;
        }
    }
}
