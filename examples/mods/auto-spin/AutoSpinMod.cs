using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

/// <summary>
/// While Cross (jump) is held in a real level, also presses Square (spin).
/// Disabled on title / warp map / menus / cinema — Cross there means "confirm"
/// and forcing Square blocked level select.
/// </summary>
public sealed class AutoSpinMod : IMod
{
    public void OnLoad()
    {
        Event.AddListener<PadReadEvent>(OnPad);
        Console.WriteLine("[auto-spin] Cross→Square only in gameplay levels (off on map/menus)");
    }

    public void OnUnload()
    {
        Event.RemoveListener<PadReadEvent>(OnPad);
    }

    static void OnPad(PadReadEvent e)
    {
        if (e.Port != 0) return;
        if (!InGameplayLevel(e.Memory)) return;
        // Active-low: bit cleared = pressed.
        if ((e.Buttons & Controller.Cross) != 0) return;
        e.Buttons = (ushort)(e.Buttons & ~Controller.Square);
    }

    static bool InGameplayLevel(IMemory m)
    {
        uint level = m.ReadU32(Catalog.LevelIdAddr);
        // Same gate as GpuHle widescreen / filters (titleMap / complete / cinema).
        return !Catalog.Levels.IsUiOrCinema(level);
    }
}
