using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

/// <summary>
/// Gem hints (#131): what a level's gem needs. Read-only, the game is not changed.
/// On the map: a card for the level Crash stands on. In a level: a reminder at the
/// start, the gem an inactive gem platform near Crash needs, and a notice when a death
/// loses the gem. Off by default (ViewConfig.GemHints); the desktop overlay and the
/// Android HUD draw the card.
/// </summary>
public static class GemHints
{
    /// <summary>Gem colors in GemsC spawn subtype order (S_GEM_CLEAR .. S_GEM_ORANGE).</summary>
    public enum Gem : byte { Clear, Red, Blue, Green, Purple, Yellow, Orange }

    public enum Tone : byte { Text, Good, Bad }

    public readonly record struct Line(string Text, Tone Tone = Tone.Text);

    /// <param name="Owned">The gem in <paramref name="Icon"/> is collected (solid icon).</param>
    /// <param name="TitleTone"><see cref="Tone.Bad"/> = warning title (gem lost).</param>
    public sealed record Card(string Title, Gem Icon, bool Owned, Line[] Lines, Tone TitleTone = Tone.Text);

    // c1 globals.h item_pool1/2: one gem bit per map slot (goolstdlib ITEM_GEMn);
    // slots 40 and 50 use item_pool2.
    const uint ItemPool1Addr = 0x80061988u;
    const uint ItemPool2Addr = 0x800619ACu;
    const uint GameStateAddr = 0x800618D0u;
    // Attract demo playback (c1 pbak.c); the demos run real levels.
    const uint PbakStateAddr = 0x80061A30u;
    const uint CrashPtrAddr = 0x800566B4u;
    // Screens with no level running (see BoxCounter).
    const uint StateGameOver = 0x200u, StateContinue = 0x300u, StateNewGem = 0x500u, StateTitle = 0x600u;

    // Object tree: c1 handles, each a list of objects with child/sibling links.
    const uint HandlesAddr = 0x80060DB8u;
    const int HandleCount = 8;
    const uint HandleChildrenOff = 4u;
    const uint HandleListType = 2u;
    const uint ObjSiblingOff = 0x68u;
    const uint ObjChildrenOff = 0x6Cu;
    const uint ObjStateOff = 0x2Cu;
    const uint ObjTransOff = 0x80u;
    const uint ObjEntityOff = 0x110u;
    // GOOL process vars (WillC FruitCount, GamOC LevelBoxCount).
    const uint ObjVarsOff = 0x160u;
    const int ObjectPoolMax = 96;
    // c1 zdat.h zone_entity: init_flags_b, type, subtype.
    const uint EntityFlagsBOff = 0x0Eu;
    const uint EntityTypeOff = 0x12u;
    const uint EntitySubtypeOff = 0x13u;

    const uint GoolTypeGems = 58u;
    const uint GoolTypeIsld = 59u;
    // GemsC Gem_Inactive: a gem platform whose gem is not owned yet (drawn see-through).
    const uint StateGemInactive = 1u;
    // GemsC Gem_Spawn: a clear gem platform needs init_flags_b clear gems, 6 when unset.
    const int DefaultClearNeed = 6;
    // 6 m from the gem platform (GOOL 1 m = 0x19000).
    const int NearDistance = 6 * 0x19000;
    // IsldC var IslandProgress (map slot), then IslandLevel (its level ID), in the
    // NTSC-J var list. TryMapSlot looks for the pair, so another index still works.
    const int IslandProgressVar = 16;

    const uint LidMap = 25u;
    const uint LidGreatHall = 44u;
    const int GemTotal = 26;
    const int ClearGemTotal = 20;

    const double RefreshSeconds = 0.1;
    const double BannerSeconds = 9;
    const double LostSeconds = 9;
    const double NearLingerSeconds = 2;

    /// <param name="Path">Color of the gem platforms in the level, if any.</param>
    readonly record struct LevelGem(uint Slot, uint Lid, Gem Color, Gem? Path = null);

    // NTSC-U map slots (IsldC MapSetLevelParams, level IDs from levels.scus94900.json).
    // A level's gem bit is its slot; the colored ones are GemsC SetColorGemID (NTSC-U).
    // Gem paths: the GemsC platforms in each level NSF (artifacts/gemhints/tools/gemscan.py).
    static readonly LevelGem[] Levels =
    [
        new(1, 9, Gem.Clear),                 // N. Sanity Beach
        new(2, 12, Gem.Clear, Gem.Green),     // Jungle Rollers
        new(3, 18, Gem.Clear, Gem.Yellow),    // The Great Gate
        new(4, 14, Gem.Clear),                // Boulders
        new(5, 15, Gem.Clear, Gem.Orange),    // Upstream
        new(7, 21, Gem.Clear, Gem.Blue),      // Rolling Stones
        new(8, 17, Gem.Clear),                // Hog Wild
        new(9, 26, Gem.Clear, Gem.Red),       // Native Fortress
        new(10, 24, Gem.Clear),               // Up The Creek
        new(12, 32, Gem.Green),               // The Lost City
        new(13, 28, Gem.Clear),               // Temple Ruins
        new(14, 20, Gem.Clear, Gem.Red),      // Road to Nowhere
        new(15, 19, Gem.Clear, Gem.Purple),   // Boulder Dash
        new(50, 30, Gem.Clear),               // Whole Hog
        new(16, 35, Gem.Clear),               // Sunset Vista
        new(18, 6, Gem.Clear),                // Heavy Machinery
        new(19, 3, Gem.Clear, Gem.Blue),      // Cortex Power
        new(20, 5, Gem.Orange),               // Generator Room
        new(21, 7, Gem.Blue),                 // Toxic Waste
        new(23, 22, Gem.Clear),               // The High Road
        new(24, 46, Gem.Red),                 // Slippery Climb
        new(25, 40, Gem.Purple, Gem.Yellow),  // Lights Out
        new(40, 42, Gem.Clear),               // Fumbling in the Dark
        new(26, 29, Gem.Clear, Gem.Blue),     // Jaws of Darkness
        new(27, 55, Gem.Clear, Gem.Green),    // Castle Machinery
        new(29, 41, Gem.Yellow),              // The Lab
    ];

    // Map slots without a gem of their own (bosses, The Great Hall), and the second
    // nodes of Lights Out (41, Fumbling side) and Boulder Dash (51, Whole Hog side).
    static readonly Dictionary<uint, uint> OtherSlots = new()
    {
        [6] = 10, [11] = 23, [17] = 33, [22] = 8, [28] = 27, [30] = LidGreatHall, [31] = 31,
        [41] = 40, [51] = 19,
    };

    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static readonly List<uint> Objects = new();
    static double _nextRefresh;
    static Card? _card;

    // The level run the in-level hints belong to. Bonus rounds keep it.
    static uint _lastLid = uint.MaxValue;
    static double _bannerUntil, _lostUntil, _nearUntil;
    static bool _lostSeen;
    static Card? _nearCard;
    static int _islandVar = -1;

    /// <summary>The card to show now, or false: setting off, nothing to tell, demo.</summary>
    public static bool TryGet([NotNullWhen(true)] out Card? card)
    {
        if (!ConfigManager.View.GemHints)
        {
            _card = null;
            card = null;
            return false;
        }
        double now = Clock.Elapsed.TotalSeconds;
        if (now >= _nextRefresh)
        {
            _nextRefresh = now + RefreshSeconds;
            _card = Compute(now);
        }
        card = _card;
        return card != null;
    }

    static Card? Compute(double now)
    {
        var mem = Runtime.Mem;
        if (mem == null) return null;
        try
        {
            // Keeps BoxCounter's run (and Lost) in step with level changes even when
            // the counter is hidden.
            bool hasRun = BoxCounter.TryReadRun(out var run);
            if (mem.ReadU32(PbakStateAddr) != 0) return null;
            uint lid = mem.ReadU32(Catalog.LevelIdAddr);
            uint pool1 = mem.ReadU32(ItemPool1Addr);
            uint pool2 = mem.ReadU32(ItemPool2Addr);

            if (lid == LidMap)
            {
                _lastLid = lid;
                return MapCard(mem, pool1, pool2);
            }
            if (lid == LidGreatHall)
            {
                _lastLid = lid;
                return NearCard(mem, now, pool1, pool2);
            }
            if (!TryLevelByLid(lid, out var level))
            {
                // A bonus round belongs to the level's run; anything else ends it.
                if (!IsBonus(lid)) _lastLid = lid;
                return null;
            }
            uint state = mem.ReadU32(GameStateAddr);
            if (state is StateGameOver or StateContinue or StateNewGem or StateTitle) return null;
            return LevelCard(mem, now, lid, level, pool1, pool2, hasRun && run.Lost);
        }
        catch
        {
            return null;
        }
    }

    static Card? LevelCard(IMemory mem, double now, uint lid, LevelGem level, uint pool1, uint pool2, bool lost)
    {
        bool owned = Owns(pool1, pool2, level.Slot);
        if (lid != _lastLid)
        {
            _lastLid = lid;
            _bannerUntil = owned ? 0 : now + BannerSeconds;
            _lostUntil = 0;
            _nearUntil = 0;
            _lostSeen = false;
        }

        if (lost && !_lostSeen)
        {
            _lostSeen = true;
            if (!owned)
            {
                _lostUntil = now + LostSeconds;
                _bannerUntil = 0;
            }
        }
        if (now < _lostUntil)
        {
            return new Card("Gem lost", level.Color, false,
            [
                new("A death after a checkpoint: the boxes broken before it no longer count.", Tone.Bad),
                new("Return to the map and replay the level to get the gem."),
            ], Tone.Bad);
        }

        if (NearCard(mem, now, pool1, pool2) is { } near) return near;
        if (now < _bannerUntil) return LevelInfoCard(level, pool1, pool2, owned, onMap: false);
        return null;
    }

    /// <summary>An inactive gem platform near Crash, kept a moment after he leaves it.</summary>
    static Card? NearCard(IMemory mem, double now, uint pool1, uint pool2)
    {
        if (TryNearInactiveGem(mem, out var color, out int need))
        {
            _nearCard = color == Gem.Clear ? BridgeStepCard(need, pool1, pool2) : PathCard(color, pool1, pool2);
            _nearUntil = now + NearLingerSeconds;
        }
        return now < _nearUntil ? _nearCard : null;
    }

    static Card PathCard(Gem color, uint pool1, uint pool2)
    {
        var source = SourceOf(color);
        var lines = new List<Line> { new($"Needs the {color} gem.", Tone.Bad) };
        string from = $"Get it in {Name(source.Lid)}";
        lines.Add(BoxCounter.TryGetTotal(source.Lid, out int total)
            ? new($"{from}: break all {total} boxes without dying.")
            : new($"{from}."));
        return new Card($"{color} gem path", color, Owns(pool1, pool2, source.Slot), lines.ToArray());
    }

    static Card BridgeStepCard(int need, uint pool1, uint pool2)
    {
        int have = CountGems(pool1, pool2, clearOnly: true);
        return new Card("Gem bridge", Gem.Clear, have >= need,
        [
            new($"This step needs {need} clear gems.", have >= need ? Tone.Good : Tone.Bad),
            new($"You have {have} of {ClearGemTotal}."),
        ]);
    }

    /// <summary>What the level's gem needs: the map card and the level-start reminder.</summary>
    static Card LevelInfoCard(LevelGem level, uint pool1, uint pool2, bool owned, bool onMap)
    {
        var lines = new List<Line>();
        if (onMap) lines.Add(owned ? new("Collected!", Tone.Good) : new("Not collected yet."));
        if (BoxCounter.TryGetTotal(level.Lid, out int total))
            lines.Add(new($"Break all {total} boxes without dying."));
        if (level.Path is Gem path)
        {
            var source = SourceOf(path);
            lines.Add(Owns(pool1, pool2, source.Slot)
                ? new($"{path} gem path: open.", Tone.Good)
                : new($"{path} gem path: needs the {path} gem ({Name(source.Lid)}).", Tone.Bad));
        }
        if (level.Color != Gem.Clear)
        {
            var opens = Levels.Where(l => l.Path == level.Color).Select(l => Name(l.Lid)).ToArray();
            if (opens.Length > 0)
                lines.Add(new($"Opens the {level.Color} gem {(opens.Length == 1 ? "path" : "paths")} in {JoinNames(opens)}."));
        }
        return new Card($"{level.Color} gem", level.Color, owned, lines.ToArray());
    }

    static Card? MapCard(IMemory mem, uint pool1, uint pool2)
    {
        if (!TryMapSlot(mem, out uint slot) || !TrySlotLid(slot, out uint lid)) return null;
        int gems = CountGems(pool1, pool2, clearOnly: false);
        if (lid == LidGreatHall)
        {
            int have = CountGems(pool1, pool2, clearOnly: true);
            return new Card("Gem bridge", Gem.Clear, have >= ClearGemTotal,
            [
                new($"Each step needs one more clear gem: {ClearGemTotal} for the last."),
                new($"You have {have} of {ClearGemTotal} clear gems.", have >= ClearGemTotal ? Tone.Good : Tone.Text),
                new($"Gems: {gems}/{GemTotal}"),
            ]);
        }
        if (!TryLevelByLid(lid, out var level)) return null;
        var card = LevelInfoCard(level, pool1, pool2, Owns(pool1, pool2, level.Slot), onMap: true);
        return card with { Lines = [.. card.Lines, new($"Gems: {gems}/{GemTotal}")] };
    }

    /// <summary>Map slot of the level Crash stands on (or walks to) on the map.</summary>
    static bool TryMapSlot(IMemory mem, out uint slot)
    {
        slot = 0;
        CollectObjects(mem);
        foreach (uint obj in Objects)
        {
            if (!FramePacing.TryGetGoolType(mem, obj, out uint type) || type != GoolTypeIsld) continue;
            if (_islandVar >= 0 && TryIslandPair(mem, obj, _islandVar, out slot)) return true;
            if (TryIslandPair(mem, obj, IslandProgressVar, out slot))
            {
                _islandVar = IslandProgressVar;
                return true;
            }
            for (int k = 0; k < 32; k++)
            {
                if (!TryIslandPair(mem, obj, k, out slot)) continue;
                _islandVar = k;
                return true;
            }
        }
        return false;
    }

    static bool TryIslandPair(IMemory mem, uint obj, int var, out uint slot)
    {
        uint at = obj + ObjVarsOff + (uint)(4 * var);
        slot = mem.ReadU32(at);
        return TrySlotLid(slot, out uint lid) && mem.ReadU32(at + 4) == lid;
    }

    /// <summary>
    /// Nearest inactive gem platform within <see cref="NearDistance"/> of Crash:
    /// its color (entity subtype) and, for a clear one, the clear gems it needs.
    /// </summary>
    static bool TryNearInactiveGem(IMemory mem, out Gem color, out int need)
    {
        color = Gem.Clear;
        need = 0;
        uint crash = mem.ReadU32(CrashPtrAddr);
        if (!IsGuestPointer(crash)) return false;
        int cx = (int)mem.ReadU32(crash + ObjTransOff);
        int cy = (int)mem.ReadU32(crash + ObjTransOff + 4);
        int cz = (int)mem.ReadU32(crash + ObjTransOff + 8);

        CollectObjects(mem);
        double best = double.MaxValue;
        foreach (uint obj in Objects)
        {
            if (mem.ReadU32(obj + ObjStateOff) != StateGemInactive) continue;
            if (!FramePacing.TryGetGoolType(mem, obj, out uint type) || type != GoolTypeGems) continue;
            uint entity = mem.ReadU32(obj + ObjEntityOff);
            if (!IsGuestPointer(entity) || mem.ReadU8(entity + EntityTypeOff) != GoolTypeGems) continue;
            byte sub = mem.ReadU8(entity + EntitySubtypeOff);
            if (sub > (byte)Gem.Orange) continue;
            double dx = (int)mem.ReadU32(obj + ObjTransOff) - (double)cx;
            double dy = (int)mem.ReadU32(obj + ObjTransOff + 4) - (double)cy;
            double dz = (int)mem.ReadU32(obj + ObjTransOff + 8) - (double)cz;
            double d2 = dx * dx + dy * dy + dz * dz;
            if (d2 >= best) continue;
            best = d2;
            color = (Gem)sub;
            short flagsB = (short)mem.ReadU16(entity + EntityFlagsBOff);
            need = flagsB != 0 ? flagsB : DefaultClearNeed;
        }
        return best <= (double)NearDistance * NearDistance;
    }

    static void CollectObjects(IMemory mem)
    {
        Objects.Clear();
        int left = ObjectPoolMax * 2;
        for (int h = 0; h < HandleCount && left > 0; h++)
            Collect(mem, mem.ReadU32(HandlesAddr + (uint)(h * 8) + HandleChildrenOff), ref left, 0);
    }

    static void Collect(IMemory mem, uint obj, ref int left, int depth)
    {
        if (depth > 8) return;
        while (IsGuestPointer(obj) && left-- > 0)
        {
            if (mem.ReadU32(obj) == HandleListType)
                Collect(mem, mem.ReadU32(obj + HandleChildrenOff), ref left, depth + 1);
            else
            {
                Objects.Add(obj);
                Collect(mem, mem.ReadU32(obj + ObjChildrenOff), ref left, depth + 1);
            }
            obj = mem.ReadU32(obj + ObjSiblingOff);
        }
    }

    static bool IsGuestPointer(uint p) => (p & 0xFF000000u) == 0x80000000u;

    // goolstdlib LEVEL_BonusTawna1 'A', LEVEL_BonusBrio 'B', LEVEL_BonusTawna2 'P', LEVEL_BonusCortex 'Q'.
    static bool IsBonus(uint lid) => lid is 36 or 37 or 51 or 52;

    static bool Owns(uint pool1, uint pool2, uint slot) =>
        slot >= 32 ? (pool2 >> (int)(slot - 32) & 1) != 0 : (pool1 >> (int)slot & 1) != 0;

    static int CountGems(uint pool1, uint pool2, bool clearOnly)
    {
        int n = 0;
        foreach (var l in Levels)
            if ((!clearOnly || l.Color == Gem.Clear) && Owns(pool1, pool2, l.Slot)) n++;
        return n;
    }

    static LevelGem SourceOf(Gem color) => Levels.First(l => l.Color == color);

    static bool TryLevelByLid(uint lid, out LevelGem level)
    {
        foreach (var l in Levels)
        {
            if (l.Lid != lid) continue;
            // #173: with Cortex Power first, a level's gem bit is the slot it plays from.
            level = l with { Slot = MapLevelSwap.Slot(l.Slot, l.Lid) };
            return true;
        }
        level = default;
        return false;
    }

    static bool TrySlotLid(uint slot, out uint lid)
    {
        foreach (var l in Levels)
        {
            if (l.Slot != slot) continue;
            lid = MapLevelSwap.Lid(slot, l.Lid);
            return true;
        }
        return OtherSlots.TryGetValue(slot, out lid);
    }

    static string JoinNames(string[] names) =>
        names.Length < 2 ? string.Concat(names) : $"{string.Join(", ", names[..^1])} and {names[^1]}";

    static string Name(uint lid) =>
        Catalog.Levels.TryGet(lid, out var info) ? info.Name : $"level {lid}";
}
