using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

/// <summary>
/// #173 Cortex Power first (ViewConfig.CortexPowerFirst). IsldC MapSetLevelParams gives
/// map slot 18 (path 30 point 0, Crash in front of the power plant) Heavy Machinery and
/// slot 19 Cortex Power. Only the level ID and the name frame of the two slots swap:
/// the slot stays the progress index (CURRENTLEVEL), so the gem bit, the unlock of the
/// next slot (WillC <c>LEVELSUNLOCKED = CURRENTLEVEL+1</c>), the return to the map and
/// the save follow the map node. DispC picks bonus rounds by level ID, so they stay
/// with their level.
/// </summary>
public static class MapLevelSwap
{
    public const uint SlotFirst = 18u;
    public const uint SlotSecond = 19u;
    // goolstdlib LEVEL_HeavyMachinery / LEVEL_CortexPower (levels.scus94900.json).
    public const uint LidHeavyMachinery = 6u;
    public const uint LidCortexPower = 3u;

    public static bool On => ConfigManager.View.CortexPowerFirst;

    /// <summary>Retail level ID of a map slot, as the swap loads it.</summary>
    public static uint Lid(uint slot, uint retailLid)
    {
        if (!On) return retailLid;
        if (slot == SlotFirst && retailLid == LidHeavyMachinery) return LidCortexPower;
        if (slot == SlotSecond && retailLid == LidCortexPower) return LidHeavyMachinery;
        return retailLid;
    }

    /// <summary>Map slot (gem bit) of a level, as the swap plays it.</summary>
    public static uint Slot(uint retailSlot, uint lid)
    {
        if (!On) return retailSlot;
        if (retailSlot == SlotFirst && lid == LidHeavyMachinery) return SlotSecond;
        if (retailSlot == SlotSecond && lid == LidCortexPower) return SlotFirst;
        return retailSlot;
    }
}

public static partial class FramePacing
{
    // IsldC vars (NTSC-J list): IslandProgress, IslandLevel, IslandCanMove,
    // IslandDirections, IslandForceProg, IslandEndOfPath, IslandTextFrame.
    const int IsldProgressVar = 16;
    const int IsldMaxVar = 32;
    const uint IsldLevelOff = 4u;
    const uint IsldTextFrameOff = 6u * 4u;
    const uint IsldVarsOff = 0x160u;
    const uint LidIsland = 25u;
    // MapSetLevelParams text_frame 17.0 (Heavy Machinery) and 18.0 (Cortex Power).
    const uint TextHeavyMachinery = 17u << 8;
    const uint TextCortexPower = 18u << 8;

    static uint _mapSwapUpdateObj;
    static int _mapSwapVar = -1;

    static void NoteMapSwapUpdate(uint obj) => _mapSwapUpdateObj = obj;

    /// <summary>
    /// After the traveler's own update (GotoIslandProg already wrote the slot's
    /// params), before its Island_Text child reads IslandTextFrame and before the
    /// confirm's <c>loadlevel(IslandLevel)</c> on a later update.
    /// </summary>
    static void ApplyMapLevelSwap(IMemory m)
    {
        uint obj = _mapSwapUpdateObj;
        _mapSwapUpdateObj = 0;
        if (!MapLevelSwap.On || (obj & 0xFF000000u) != 0x80000000u) return;
        try
        {
            if (m.ReadU32(Catalog.LevelIdAddr) != LidIsland
                || !TryReadGoolClass(m, obj, out uint type, out _) || type != GoolTypeIsld)
                return;
            if (_mapSwapVar >= 0)
            {
                TrySwapSlot(m, obj, _mapSwapVar);
                return;
            }
            // Another region's var list: look for the slot/level/text triple.
            if (TrySwapSlot(m, obj, IsldProgressVar))
            {
                _mapSwapVar = IsldProgressVar;
                return;
            }
            for (int k = 0; k < IsldMaxVar; k++)
            {
                if (!TrySwapSlot(m, obj, k)) continue;
                _mapSwapVar = k;
                return;
            }
        }
        catch
        {
        }
    }

    /// <summary>True when var <paramref name="k"/> is IslandProgress (slot 18 or 19, its pair).</summary>
    static bool TrySwapSlot(IMemory m, uint obj, int k)
    {
        uint at = obj + IsldVarsOff + (uint)(4 * k);
        uint slot = m.ReadU32(at);
        if (slot != MapLevelSwap.SlotFirst && slot != MapLevelSwap.SlotSecond) return false;
        uint lid = m.ReadU32(at + IsldLevelOff);
        uint text = m.ReadU32(at + IsldTextFrameOff);
        bool first = slot == MapLevelSwap.SlotFirst;
        // IslandLevel holds the loadlevel operand: level ID << 8.
        uint retailLid = (first ? MapLevelSwap.LidHeavyMachinery : MapLevelSwap.LidCortexPower) << 8;
        uint swapLid = (first ? MapLevelSwap.LidCortexPower : MapLevelSwap.LidHeavyMachinery) << 8;
        uint retailText = first ? TextHeavyMachinery : TextCortexPower;
        uint swapText = first ? TextCortexPower : TextHeavyMachinery;
        // Already swapped (every update until Crash walks on).
        if (lid == swapLid && text == swapText) return true;
        if (lid != retailLid || text != retailText) return false;
        m.WriteU32(at + IsldLevelOff, swapLid);
        m.WriteU32(at + IsldTextFrameOff, swapText);
        return true;
    }
}
