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
    const uint MapMaskAddr = 0x800618F0;
    const uint LevelSelectAddr = 0x80061948;
    const uint InstantSaveMenuAddr = 0x800A264C;
    // SCUS-94900: current level ID (cbhacks / GpuHle).
    public const uint LevelIdAddr = 0x80056710;
    const uint TitleMenuMapLevel = 0x19;

    // Hold the instant-save poke for a few frames — a single write can be overwritten.
    static int _instantSaveHoldFrames;

    public static void Apply()
    {
        var mem = Runtime.Mem;
        if (mem == null) return;

        if (_instantSaveHoldFrames > 0)
        {
            mem.WriteU16(InstantSaveMenuAddr, 4);
            _instantSaveHoldFrames--;
        }

        if (CheatConfig.InfiniteLives)
        {
            // Map / continue stock.
            mem.WriteU16(MapLivesAddr, 99);
            if (TryFindActiveLevelLives(mem, out uint livesAddr))
                mem.WriteU16(livesAddr, 99);
        }

        if (CheatConfig.InfiniteWumpa)
        {
            // GameShark: Wumpa is typically 4 bytes before the per-level lives field.
            if (TryFindActiveLevelLives(mem, out uint livesAddr))
                mem.WriteU16(livesAddr - 4, 99);
        }

        if (CheatConfig.LevelSelect)
            mem.WriteU8(LevelSelectAddr, 0x40);
    }

    /// <summary>
    /// Finds the single active per-level lives counter. Ambiguous → false (avoid corruption).
    /// </summary>
    static bool TryFindActiveLevelLives(Memory.IMemory mem, out uint addr)
    {
        addr = 0;
        uint best = 0;
        int matches = 0;
        int bestRank = 0;

        foreach (var candidate in LevelLivesAddrs)
        {
            ushort v = mem.ReadU16(candidate);
            // Active lives are a small count; 99 means we already froze this slot.
            int rank = v <= 10 ? 2 : v == 99 ? 1 : 0;
            if (rank == 0) continue;
            if (rank > bestRank)
            {
                bestRank = rank;
                best = candidate;
                matches = 1;
            }
            else if (rank == bestRank)
            {
                matches++;
            }
        }

        if (matches != 1 || best == 0) return false;
        addr = best;
        return true;
    }

    public static void Give99LivesOnMap()
    {
        Runtime.Mem?.WriteU16(MapLivesAddr, 99);
    }

    /// <summary>Reset to 2nd Aku Aku mask on the warp map (GameShark 800618F0 0200).</summary>
    public static void Give2ndMaskOnMap()
    {
        Runtime.Mem?.WriteU16(MapMaskAddr, 2);
    }

    /// <summary>
    /// One-shot 99 Wumpa on the active level (livesAddr − 4). No-op if ambiguous.
    /// </summary>
    public static bool Give99Wumpa()
    {
        var mem = Runtime.Mem;
        if (mem == null) return false;
        if (!TryFindActiveLevelLives(mem, out uint livesAddr)) return false;
        mem.WriteU16(livesAddr - 4, 99);
        return true;
    }

    public static void OpenInstantSaveMenu()
    {
        _instantSaveHoldFrames = 8;
        Runtime.Mem?.WriteU16(InstantSaveMenuAddr, 4);
    }

    public static bool TryGetLevelId(out uint levelId)
    {
        levelId = 0;
        var mem = Runtime.Mem;
        if (mem == null) return false;
        try
        {
            levelId = mem.ReadU32(LevelIdAddr);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True on title / menus / warp map / game over (level ID 0x19).</summary>
    public static bool IsOnTitleMenuMap()
    {
        if (!TryGetLevelId(out uint id)) return true;
        return id == TitleMenuMapLevel;
    }
}
