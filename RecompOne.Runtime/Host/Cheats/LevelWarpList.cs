using RecompOne.Runtime.Catalogs;

namespace RecompOne.Runtime.Host.Cheats;

/// <summary>Warp to Level entries, shared by the desktop and Android dev menus.</summary>
public static class LevelWarpList
{
    /// <summary>
    /// <paramref name="MapSlot"/> is the warp-map slot of <paramref name="LevelId"/>.
    /// Bonus entries warp to that level (the one holding the tokens), then load
    /// <paramref name="BonusLevelId"/> with BonoC layout <paramref name="BonusRound"/>.
    /// </summary>
    public sealed record Entry(uint MapSlot, uint LevelId, string? Note = null,
        uint BonusLevelId = 0, uint BonusRound = 0)
    {
        public bool IsBonus => BonusLevelId != 0;

        public string Label
        {
            get
            {
                string name = Catalog.Levels.TryGet(LevelId, out var info) ? info.Name : $"Level {LevelId}";
                return Note == null ? name : $"{name}  ({Note})";
            }
        }

        public bool IsCurrent(uint currentLevelId)
        {
            if (!IsBonus)
                return currentLevelId == LevelId;
            return currentLevelId == BonusLevelId
                && CheatManager.TryGetBonusRound(out uint round) && round == BonusRound;
        }

        public void Warp()
        {
            if (IsBonus)
                CheatManager.RequestBonusWarp(LevelId, MapSlot, BonusLevelId, BonusRound);
            else
                CheatManager.RequestWarp(LevelId, MapSlot);
        }
    }

    public sealed record Group(string Name, Entry[] Entries);

    const string Secret = "secret";

    // goolstdlib LEVEL_BonusTawna1 'A', LEVEL_BonusTawna2 'P', LEVEL_BonusBrio 'B', LEVEL_BonusCortex 'Q'.
    const uint Tawna1 = 36, Tawna2 = 51, Brio = 37, Cortex = 52;

    static Entry Bonus(uint mapSlot, uint levelId, uint bonusLevelId, uint round) =>
        new(mapSlot, levelId, null, bonusLevelId, round);

    // Warp-map order (IsldC MapSetLevelParams); IDs from levels.scus94900.json.
    // The goocdump IsldC is NTSC-J, which swaps Sunset Vista and Slippery Climb:
    // on NTSC-U slot 16 is Sunset Vista (GamOC gives its key at slot 16) and
    // slot 24 Slippery Climb (red gem).
    // Bonus rounds: NTSC-U DispC SelectBonusRound (read from the disc). NTSC-J adds
    // Tawna rounds 22–29 (Temple Ruins, Cortex Power, Slippery Climb, Castle
    // Machinery) that NTSC-U does not have.
    public static IReadOnlyList<Group> Groups { get; } =
    [
        new("N. SANITY ISLAND",
        [
            new(1, 9), new(2, 12), new(3, 18), new(4, 14), new(5, 15),
            new(6, 10), new(7, 21), new(8, 17), new(9, 26),
        ]),
        new("WUMPA ISLAND",
        [
            new(10, 24), new(11, 23), new(12, 32), new(13, 28), new(14, 20),
            new(15, 19), new(50, 30, Secret), new(16, 35), new(17, 33),
        ]),
        new("CORTEX ISLAND",
        [
            new(18, 6), new(19, 3), new(20, 5), new(21, 7), new(22, 8),
            new(23, 22), new(24, 46), new(25, 40), new(40, 42, Secret),
            new(26, 29), new(27, 55), new(28, 27), new(29, 41), new(30, 44), new(31, 31),
        ]),
        new("TAWNA BONUS ROUNDS",
        [
            Bonus(2, 12, Tawna1, 0), Bonus(3, 18, Tawna2, 4), Bonus(5, 15, Tawna1, 10),
            Bonus(7, 21, Tawna2, 8), Bonus(9, 26, Tawna1, 1), Bonus(10, 24, Tawna2, 11),
            Bonus(12, 32, Tawna1, 12), Bonus(14, 20, Tawna2, 13), Bonus(16, 35, Tawna1, 5),
            Bonus(18, 6, Tawna1, 18), Bonus(20, 5, Tawna1, 2), Bonus(21, 7, Tawna1, 6),
            Bonus(23, 22, Tawna2, 3), Bonus(25, 40, Tawna2, 16), Bonus(26, 29, Tawna2, 17),
            Bonus(29, 41, Tawna1, 19),
        ]),
        new("BRIO BONUS ROUNDS",
        [
            Bonus(7, 21, Brio, 14), Bonus(12, 32, Brio, 7), Bonus(18, 6, Brio, 21),
            Bonus(24, 46, Brio, 20),
        ]),
        new("CORTEX BONUS ROUNDS",
        [
            Bonus(16, 35, Cortex, 9), Bonus(26, 29, Cortex, 15),
        ]),
        new("CUT CONTENT",
        [
            new(CheatManager.StormyAscentMapLevel, CheatManager.LidStormyAscent, "cut level"),
        ]),
    ];
}
