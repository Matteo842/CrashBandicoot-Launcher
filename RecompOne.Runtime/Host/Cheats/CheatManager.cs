namespace RecompOne.Runtime.Host.Cheats;

/// <summary>Applies cheat RAM writes for Crash Bandicoot NTSC-U (SCUS-94900).</summary>
public static class CheatManager
{
    // Per-level lives fields (GameShark). Only touched when exactly one address
    // looks like an active lives counter — writing all of them corrupts RAM.
    static readonly uint[] LevelLivesAddrs =
    [
        0x8009E808, 0x8009E584, 0x8009E88C, 0x8009E77C, 0x8009E0F8, 0x8009E5AC,
        0x8009E54C, 0x8009E828, 0x8009E64C, 0x8009E778, 0x8009E59C, 0x8009E198,
        0x8009E508, 0x8009E538, 0x8009E7D0, 0x8009E1A0, 0x8009E0D0, 0x8009E190,
        0x8009E0E0, 0x8009E620, 0x8009E610, 0x8009E5F0, 0x8009E818, 0x8009E750,
        0x8009E368, 0x8009E5C0, 0x8009E52C, 0x8009E6A4, 0x8009E4CC, 0x8009E6A0,
        0x8009E5DC,
    ];

    const uint MapLivesAddr = 0x800618EC;
    const uint LevelSelectAddr = 0x80061948;
    const uint InstantSaveMenuAddr = 0x800A264C;

    public static void Apply()
    {
        var mem = Runtime.Mem;
        if (mem == null) return;

        if (CheatConfig.InfiniteLives)
        {
            // Map / continue stock.
            mem.WriteU16(MapLivesAddr, 99);
            FreezeActiveLevelLives(mem);
        }

        if (CheatConfig.LevelSelect)
            mem.WriteU8(LevelSelectAddr, 0x40);
    }

    static void FreezeActiveLevelLives(Memory.IMemory mem)
    {
        uint best = 0;
        int matches = 0;
        int bestRank = 0;

        foreach (var addr in LevelLivesAddrs)
        {
            ushort v = mem.ReadU16(addr);
            // Active lives are a small count; 99 means we already froze this slot.
            int rank = v <= 10 ? 2 : v == 99 ? 1 : 0;
            if (rank == 0) continue;
            if (rank > bestRank)
            {
                bestRank = rank;
                best = addr;
                matches = 1;
            }
            else if (rank == bestRank)
            {
                matches++;
            }
        }

        // Exactly one candidate → safe to freeze. Ambiguous → skip (avoid corruption).
        if (matches == 1 && best != 0)
            mem.WriteU16(best, 99);
    }

    public static void Give99LivesOnMap()
    {
        Runtime.Mem?.WriteU16(MapLivesAddr, 99);
    }

    public static void OpenInstantSaveMenu()
    {
        Runtime.Mem?.WriteU16(InstantSaveMenuAddr, 4);
    }
}
