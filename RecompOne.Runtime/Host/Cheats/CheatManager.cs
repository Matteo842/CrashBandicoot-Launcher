namespace RecompOne.Runtime.Host.Cheats;

/// <summary>Applies cheat RAM writes for Crash Bandicoot NTSC-U (SCUS-94900).</summary>
public static class CheatManager
{
    // Global lives on the world map. Per-level GameShark addresses must NOT be
    // frozen every frame — when that level is not loaded they alias other RAM and
    // corrupt pointers (seen as "unmapped address: 0x81xxxxxx").
    const uint MapLivesAddr = 0x800618EC;
    const uint LevelSelectAddr = 0x80061948;
    const uint InstantSaveMenuAddr = 0x800A264C;

    public static void Apply()
    {
        var mem = Runtime.Mem;
        if (mem == null) return;

        if (CheatConfig.InfiniteLives)
            mem.WriteU16(MapLivesAddr, 99);

        if (CheatConfig.LevelSelect)
            mem.WriteU8(LevelSelectAddr, 0x40);
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
