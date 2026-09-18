using System.Diagnostics;
using RecompOne.Runtime.Catalogs;
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
    const uint InstantSaveMenuAddr = 0x800A264C;
    // SCUS-94900: current level ID (cbhacks / GpuHle). Prefer Catalog.LevelIdAddr at runtime.
    public const uint LevelIdAddr = 0x80056710;
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

        if ((CheatConfig.GodMode || CheatConfig.Fly) && !IsOnTitleMenuMap())
            ApplyDebugMovement(mem);
    }

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
                mem.WriteU16(MapLivesAddr, 99);
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
