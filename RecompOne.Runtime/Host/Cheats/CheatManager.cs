using System.Diagnostics;
using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Memory;

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
    // GOOL globals (wurlyfox/c1 globals.h): item_pool2 holds the map keys,
    // map_key_links only draws the key paths and is copied from item_pool2 on map spawn.
    const uint ItemPool2Addr = 0x800619AC;
    const uint MapKeyLinksAddr = 0x800619B8;
    const uint KeyBits = 1u << 10 | 1u << 20; // Jaws of Darkness key | Sunset Vista key
    // goolstdlib ITEM_GEM1..26: item_pool1 holds 24 of them (colored: green 12, red 24,
    // orange 20, blue 21, purple 25, yellow 29), item_pool2 the last two clear gems.
    const uint ItemPool1Addr = 0x80061988u;
    const uint GemBits1 =
        1u << 1 | 1u << 2 | 1u << 3 | 1u << 4 | 1u << 5 | 1u << 7 | 1u << 8 | 1u << 9
        | 1u << 10 | 1u << 12 | 1u << 13 | 1u << 14 | 1u << 15 | 1u << 16 | 1u << 18 | 1u << 19
        | 1u << 20 | 1u << 21 | 1u << 23 | 1u << 24 | 1u << 25 | 1u << 26 | 1u << 27 | 1u << 29;
    const uint GemBits2 = 1u << 8 | 1u << 18;
    // Checkpoint copies of the pools (c1 globals.h) — restored on death.
    const uint SavedItemPool1Addr = 0x80061A1Cu;
    const uint SavedItemPool2Addr = 0x80061A20u;
    // GOOL counters are 24.8 fixed-point (HUD prints value >> 8): 99 lives = 0x6300.
    const uint One = 0x100u;
    const uint Count99 = 99u * One;
    const uint InstantSaveMenuAddr = 0x800A264C;
    // SCUS-94900: current level ID (cbhacks / GpuHle). Prefer Catalog.LevelIdAddr at runtime.
    public const uint LevelIdAddr = 0x80056710;
    // c1 ns.c next_lid: GOOL loadlevel writes it, the main loop loads it when != -1.
    const uint NextLevelIdAddr = 0x80056714u;
    // GOOL globals the warp map sets before loadlevel (IsldC) and the engine
    // resets when loading LID_TITLE (c1 main.c) — a direct warp skips both.
    const uint RespawnCountAddr = 0x800618A0u;
    const uint TitleStateAddr = 0x800618D4u;
    const uint SavedTitleStateAddr = 0x800618D8u;
    const uint CurMapLevelAddr = 0x800618DCu;
    const uint CortexCountAddr = 0x800618F8u;
    const uint BrioCountAddr = 0x800618FCu;
    const uint TawnaCountAddr = 0x80061900u;
    const uint MapLevelEnteredAddr = 0x8006194Cu;
    const uint BonusRoundAddr = 0x8006197Cu;
    const uint CheckpointIdAddr = 0x800619A0u;
    const uint DeathCountAddr = 0x80061A3Cu;
    const uint TitleStateMap = 15u;
    // Stormy Ascent: cut level, still on the disc. Map slots 32–39 have no map node
    // (IsldC GotoIslandProg skips them) and no gem of their own, so finishing it
    // neither unlocks levels nor takes another level's gem.
    public const uint LidStormyAscent = 34u;
    public const uint StormyAscentMapLevel = 34u;
    const uint LidMap = 25u;
    const uint LidLevelEnd = 45u;
    // Its Cortex tokens have no round in DispC SelectBonusRound: use Slippery Climb's
    // Brio round (BonoC layout 20) — no save, key or progress attached.
    const uint LidBonusBrio = 37u;
    const uint StormyBonusRound = 20u;
    // c1 level.c: a new game starts the map at 99 (IsldC turns it into slot 1).
    const uint NewGameMapLevel = 99u;
    const uint CrashPtrAddr = 0x800566B4u;
    const uint FramesElapsedAddr = 0x80060E04u;
    const uint ObjStateOff = 0x2Cu;
    const uint ObjTransOff = 0x80u;
    const uint ObjRotOff = 0x8Cu;
    const uint ObjVelXOff = 0xA4u;
    const uint ObjVelYOff = 0xA8u;
    const uint ObjVelZOff = 0xACu;
    const uint ObjStatusBOff = 0xCCu;
    const uint ObjStateFlagsOff = 0x120u;
    const uint ObjInvincibleOff = 0x128u;
    const uint ObjInvincibleStampOff = 0x12Cu;
    const uint FlagGravity = 0x20u;
    const uint FlagDpadControl = 0x80u;
    const uint FlagStateDeathCine = 0x4000u;
    /// <summary>WillC engine hit-block (states 2–4). Also sends EventHitInvincible to bats.</summary>
    const uint InvincibleHit = 4u;
    const int Meter = 0x19000;
    const int FlyMetersPerSecond = 8;

    // Hold the instant-save poke for a few frames — a single write can be overwritten.
    static int _instantSaveHoldFrames;
    static long _flyTs;
    static bool _warpPending;
    static uint _warpLevelId;
    static uint _warpMapLevel;
    static uint _warpLoadLid = uint.MaxValue;
    static uint _returnMapLevel = NewGameMapLevel;

    public static bool WarpPending => _warpPending;

    /// <summary>
    /// Queue a direct level load. <paramref name="mapLevel"/> is the warp-map slot
    /// (IsldC MapSetLevelParams: 1–31, 40 Fumbling in the Dark, 50 Whole Hog,
    /// <see cref="StormyAscentMapLevel"/> for the cut level).
    /// </summary>
    public static void RequestWarp(uint levelId, uint mapLevel)
    {
        _warpLevelId = levelId;
        _warpMapLevel = mapLevel;
        _warpPending = true;
    }

    public static void Apply()
    {
        var mem = Runtime.Mem;
        if (mem == null) return;

        if (_warpPending)
            ApplyWarp(mem);

        if (_instantSaveHoldFrames > 0)
        {
            mem.WriteU16(InstantSaveMenuAddr, 4);
            _instantSaveHoldFrames--;
        }

        if (CheatConfig.InfiniteLives)
        {
            // Map / continue stock.
            mem.WriteU32(MapLivesAddr, Count99);
            if (TryFindActiveLevelLives(mem, out uint livesAddr))
                mem.WriteU32(livesAddr, Count99);
        }

        if (CheatConfig.InfiniteWumpa)
        {
            // GameShark: Wumpa is typically 4 bytes before the per-level lives field.
            if (TryFindActiveLevelLives(mem, out uint livesAddr))
                mem.WriteU32(livesAddr - 4, Count99);
        }

        if (CheatConfig.LevelSelect)
        {
            mem.WriteU8(LevelSelectAddr, 0x40);
            // Secret levels (Fumbling in the Dark, Whole Hog) are gated by the keys, not by the unlock count.
            mem.WriteU32(ItemPool2Addr, mem.ReadU32(ItemPool2Addr) | KeyBits);
            mem.WriteU32(MapKeyLinksAddr, mem.ReadU32(MapKeyLinksAddr) | KeyBits);
        }

        if ((CheatConfig.GodMode || CheatConfig.Fly) && !IsOnTitleMenuMap())
            ApplyDebugMovement(mem);
    }

    static void ApplyWarp(IMemory mem)
    {
        // Another load is already queued (death, level end, map) — retry next frame.
        if (mem.ReadU32(NextLevelIdAddr) != uint.MaxValue) return;
        _warpPending = false;

        // Where to put Willy back when leaving a level that has no map node.
        uint fromMapLevel = mem.ReadU32(CurMapLevelAddr);
        if (!IsNodelessMapLevel(fromMapLevel))
            _returnMapLevel = fromMapLevel;

        // Same bookkeeping as the warp map's confirm button (SAVEDSCREEN = GAMESCREEN,
        // GLOBAL_48 = CURRENTLEVEL = slot); title_state 15 = map, so leaving the level
        // lands on the map at this slot even when warping from the title menu.
        mem.WriteU32(TitleStateAddr, TitleStateMap);
        mem.WriteU32(SavedTitleStateAddr, TitleStateMap);
        mem.WriteU32(MapLevelEnteredAddr, _warpMapLevel);
        mem.WriteU32(CurMapLevelAddr, _warpMapLevel);

        // Level → level never passes the map: a stale checkpoint would respawn Crash
        // at the previous level's checkpoint coordinates after a death.
        mem.WriteU32(CheckpointIdAddr, uint.MaxValue);
        mem.WriteU32(RespawnCountAddr, 0);
        mem.WriteU32(DeathCountAddr, 0);
        mem.WriteU32(CortexCountAddr, 0);
        mem.WriteU32(BrioCountAddr, 0);
        mem.WriteU32(TawnaCountAddr, 0);
        mem.WriteU32(BonusRoundAddr, 0);

        _warpLoadLid = _warpLevelId;
        mem.WriteU32(NextLevelIdAddr, _warpLevelId);
    }

    /// <summary>
    /// NSInit pre-hook: A1 is the level about to load, cur_lid still the one being left.
    /// </summary>
    public static void OnLevelLoad(CpuContext c, IMemory m)
    {
        uint lid = c.A1;
        // Kept on a match: the hook may run twice for one NSInit (jal + dispatcher).
        bool warp = lid == _warpLoadLid;
        if (!warp)
            _warpLoadLid = uint.MaxValue;

        // Third Cortex token in Stormy Ascent: SelectBonusRound has no branch for it,
        // so loadlevel(BonusLevel) reads a stale field (NSInit on garbage = retail crash).
        // Map, level end and a restart are the only loads the level makes on its own.
        if (m.ReadU32(LevelIdAddr) == LidStormyAscent && !warp
            && lid is not (LidMap or LidLevelEnd or LidStormyAscent))
        {
            c.A1 = LidBonusBrio;
            m.WriteU32(BonusRoundAddr, StormyBonusRound);
        }

        // The level end screen still needs slot 34 (gem bit); the map does not.
        if (lid == LidMap && IsNodelessMapLevel(m.ReadU32(CurMapLevelAddr)))
        {
            m.WriteU32(CurMapLevelAddr, _returnMapLevel);
            m.WriteU32(MapLevelEnteredAddr, _returnMapLevel);
        }
    }

    static bool IsNodelessMapLevel(uint mapLevel) => mapLevel is >= 32 and <= 39;

    static void ApplyDebugMovement(IMemory mem)
    {
        try
        {
            uint crash = mem.ReadU32(CrashPtrAddr);
            if (crash == 0 || (crash & 0xFF000000u) != 0x80000000u) return;

            uint state = mem.ReadU32(crash + ObjStateOff);
            uint flags = mem.ReadU32(crash + ObjStateFlagsOff);
            // Invincible 2–4 ORs 0x1002 into status_c and skips GoolObjectChangeState
            // when the target state's flags overlap — including Warp_In (0x1022).
            // Keeping it on through a drown/fall cine loops the death anim forever.
            bool cine = (flags & FlagStateDeathCine) != 0 || IsDeathOrWarpState(state);
            if (CheatConfig.GodMode)
                mem.WriteU32(MapLivesAddr, Count99);
            if (cine)
            {
                _flyTs = 0;
                return;
            }

            if (CheatConfig.GodMode)
            {
                mem.WriteU32(crash + ObjInvincibleOff, InvincibleHit);
                mem.WriteU32(crash + ObjInvincibleStampOff, mem.ReadU32(FramesElapsedAddr));
            }

            if (!CheatConfig.Fly)
            {
                _flyTs = 0;
                return;
            }

            uint statusB = mem.ReadU32(crash + ObjStatusBOff);
            // Own XZ: GOOL air states barely strafe, and death cine clears DPAD.
            mem.WriteU32(crash + ObjStatusBOff, statusB & ~FlagGravity & ~FlagDpadControl);
            mem.WriteU32(crash + ObjVelXOff, 0);
            mem.WriteU32(crash + ObjVelYOff, 0);
            mem.WriteU32(crash + ObjVelZOff, 0);

            long now = Stopwatch.GetTimestamp();
            double sec = _flyTs == 0 ? 0 : (now - _flyTs) / (double)Stopwatch.Frequency;
            _flyTs = now;
            if (sec < 0) sec = 0;
            if (sec > 0.05) sec = 0.05;
            int step = (int)Math.Round(sec * Meter * FlyMetersPerSecond);
            if (step < 1) step = 1;

            int y = (int)mem.ReadU32(crash + ObjTransOff + 4);
            if (Held(Controller.Cross) || Held(Controller.R1))
                mem.WriteU32(crash + ObjTransOff + 4, (uint)(y + step));
            else if (Held(Controller.L2) || Held(Controller.Triangle))
                mem.WriteU32(crash + ObjTransOff + 4, (uint)(y - step));

            ReadFlyPlanar(out float forward, out float strafe);
            if (forward == 0 && strafe == 0) return;

            int yaw = (int)mem.ReadU32(crash + ObjRotOff + 4) & 0xFFF;
            double a = yaw * (Math.PI / 2048.0);
            float sin = (float)Math.Sin(a);
            float cos = (float)Math.Cos(a);
            int dx = (int)Math.Round(step * (forward * sin + strafe * cos));
            int dz = (int)Math.Round(step * (forward * cos - strafe * sin));
            if (dx != 0)
                mem.WriteU32(crash + ObjTransOff, (uint)((int)mem.ReadU32(crash + ObjTransOff) + dx));
            if (dz != 0)
                mem.WriteU32(crash + ObjTransOff + 8, (uint)((int)mem.ReadU32(crash + ObjTransOff + 8) + dz));
        }
        catch
        {
            // overlay swap / crash ptr recycled
        }
    }

    static bool IsDeathOrWarpState(uint state) =>
        state is >= 22 and <= 31 or 40 or 41;

    static bool Held(ushort button) =>
        (Controller.State & button) == 0
        || (Controller.VirtualButtons & button) != 0
        || (Controller.PhysicalButtons & button) != 0;

    static void ReadFlyPlanar(out float forward, out float strafe)
    {
        forward = 0;
        strafe = 0;
        if (Held(Controller.Up)) forward += 1;
        if (Held(Controller.Down)) forward -= 1;
        if (Held(Controller.Right)) strafe += 1;
        if (Held(Controller.Left)) strafe -= 1;

        float ax = StickAxis(Controller.LeftX);
        float ay = StickAxis(Controller.LeftY);
        if (Controller.PhysicalConnected)
        {
            ax = AbsMax(ax, StickAxis(Controller.PhysicalLeftX));
            ay = AbsMax(ay, StickAxis(Controller.PhysicalLeftY));
        }

        // SDL left-Y: up is negative → byte < 0x80 after AxisToByte.
        strafe += ax;
        forward -= ay;

        float mag = MathF.Sqrt(forward * forward + strafe * strafe);
        if (mag > 1f)
        {
            forward /= mag;
            strafe /= mag;
        }
    }

    static float StickAxis(byte v)
    {
        int d = v - 0x80;
        if (d is > -24 and < 24) return 0;
        return Math.Clamp(d / 128f, -1f, 1f);
    }

    static float AbsMax(float a, float b) =>
        Math.Abs(a) >= Math.Abs(b) ? a : b;

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
            uint v = mem.ReadU32(candidate);
            // 99.0 means we already froze this slot (Willy also spawns with the frozen
            // map stock); otherwise accept only a whole count of lives below 99.
            int rank = v == Count99 ? 2 : v != 0 && v < Count99 && v % One == 0 ? 1 : 0;
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
        Runtime.Mem?.WriteU32(MapLivesAddr, Count99);
    }

    /// <summary>Reset to 2nd Aku Aku mask on the warp map (GameShark 800618F0 0200).</summary>
    public static void Give2ndMaskOnMap()
    {
        Runtime.Mem?.WriteU32(MapMaskAddr, 2u * One);
    }

    /// <summary>
    /// One-shot 99 Wumpa on the active level (livesAddr − 4). No-op if ambiguous.
    /// </summary>
    public static bool Give99Wumpa()
    {
        var mem = Runtime.Mem;
        if (mem == null) return false;
        if (!TryFindActiveLevelLives(mem, out uint livesAddr)) return false;
        mem.WriteU32(livesAddr - 4, Count99);
        return true;
    }

    /// <summary>
    /// Sets or clears every gem bit. Gem objects read the pools when they spawn
    /// (GemsC Gem_Spawn), so reload the level to see the change.
    /// </summary>
    public static void SetAllGems(bool owned)
    {
        var mem = Runtime.Mem;
        if (mem == null) return;
        SetBits(mem, ItemPool1Addr, GemBits1, owned);
        SetBits(mem, ItemPool2Addr, GemBits2, owned);
        SetBits(mem, SavedItemPool1Addr, GemBits1, owned);
        SetBits(mem, SavedItemPool2Addr, GemBits2, owned);
    }

    static void SetBits(IMemory mem, uint addr, uint bits, bool on)
    {
        uint v = mem.ReadU32(addr);
        mem.WriteU32(addr, on ? v | bits : v & ~bits);
    }

    public static void OpenInstantSaveMenu()
    {
        _instantSaveHoldFrames = 8;
        Runtime.Mem?.WriteU16(InstantSaveMenuAddr, 4);
    }

    public static bool TryGetLevelId(out uint levelId)
        => Catalog.Levels.TryReadCurrentId(out levelId);

    /// <summary>True on title / menus / warp map / game over (level kind titleMap).</summary>
    public static bool IsOnTitleMenuMap()
    {
        if (!TryGetLevelId(out uint id)) return true;
        return Catalog.Levels.TryGet(id, out var info)
            ? info.Kind == LevelKind.TitleMap
            : id == 0x19u;
    }
}
