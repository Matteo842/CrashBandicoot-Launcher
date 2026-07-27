namespace RecompOne.Runtime.Events;

/// <summary>the game reads a controller port (Controller bit layout, active-low)</summary>
public sealed class PadReadEvent : GameEvent
{
    public int Port;
    public ushort Buttons;
}
