using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Events;

/// <summary>
/// Shared pad filter for BIOS and LibPad paths.
/// <see cref="PadReadEvent.Buttons"/> uses the host <c>Controller</c> layout
/// (active-low: bit cleared = pressed) — not the byte-swapped BIOS buffer form.
/// </summary>
public static class PadInput
{
    static readonly PadReadEvent EventInstance = new();

    public static ushort Filter(IMemory m, int port, ushort buttons)
    {
        if (!Event.HasAnyListeners<PadReadEvent>()) return buttons;
        var e = EventInstance;
        e.Context = Runtime.Cpu!;
        e.Memory = m;
        e.Port = port;
        e.Buttons = buttons;
        Event.Dispatch(e);
        return e.Buttons;
    }
}
