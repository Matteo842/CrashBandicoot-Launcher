using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    // #125 "Fix original oversights" (ViewConfig.FixOriginalOversights), from The
    // Cutting Room Floor's Crash Bandicoot page. Indexes are from the SCUS-94900 NSFs
    // (goocdump is NTSC-J: RivOC states there are one higher).
    //
    // 1. River fish shadow (Upstream, Up the Creek): RivOC Fish_Spawn spawns a ShadC
    //    shadow but sets statusb = 0x2010 without FLAG_HAS_SHADOW, and ShadC hides
    //    itself while its parent lacks that flag.
    // 2. Ripper Roo shadow: RRooC Roo_Spawn does the same (statusb = 0x10011).
    //    Neither object writes statusb with "=" again, so ORing the flag before each
    //    update equals the missing bit in the spawn constant.
    // 3. Lights Out sky: the tower top zone (C9_EZ) lists a sky backdrop world, but
    //    the level's dark shader (GfxTransformWorldsDark2) fades every vertex by its
    //    distance from Crash. The sky mesh sits ~20000 units away, so it is always
    //    black. Its polygons are taken out of the dark pass and drawn by the plain
    //    world transform, the way every other level draws its sky.

    const uint FlagHasShadow = 0x4000000u;
    const uint GoolTypeRivO = 28u;
    const uint GoolTypeRRoo = 37u;
    const uint LidUpstream = 15u;
    const uint LidRipperRoo = 23u;
    const uint LidUpTheCreek = 24u;
    const uint LidLightsOut = 40u;
    /// <summary>RivOC Fish_Spawn, Fish_Wait, Fish_Move, Fish_Fling (NTSC-U).</summary>
    const uint FishStateMask = (1u << 5) | (1u << 6) | (1u << 7) | (1u << 15);
    /// <summary>RRooC Roo_Spawn, Roo_Boss, Roo_Dead (3-4 are the intro Roo, no shadow).</summary>
    const uint RooStateMask = (1u << 0) | (1u << 1) | (1u << 2);
    const uint ZoneFlagDark2 = 0x400u;

    static bool _oversightSkyPass;
    static uint _oversightSkyList;
    static uint _oversightSkyOt;
    static int _oversightSkyBytes;
    static int _oversightSkyCount;
    static byte[] _oversightSkySaved = [];
    static ushort[] _oversightSkyIds = new ushort[256];

    /// <summary>PreGoolObjectUpdate: the shadow flag the spawn code forgot.</summary>
    static void ApplyOversightShadow(IMemory m, uint obj)
    {
        if (!ConfigManager.View.FixOriginalOversights) return;
        if ((obj & 0xFF000000u) != 0x80000000u) return;
        try
        {
            uint level = m.ReadU32(Catalog.LevelIdAddr);
            if (level != LidUpstream && level != LidUpTheCreek && level != LidRipperRoo) return;
            if (!TryReadGoolClass(m, obj, out uint type, out _)) return;
            uint mask = type == GoolTypeRivO ? FishStateMask : type == GoolTypeRRoo ? RooStateMask : 0u;
            uint state = m.ReadU32(obj + ObjStateOff);
            if (mask == 0 || state >= 32 || (mask & (1u << (int)state)) == 0) return;
            uint status = m.ReadU32(obj + ObjStatusBOff);
            if ((status & FlagHasShadow) == 0)
                m.WriteU32(obj + ObjStatusBOff, status | FlagHasShadow);
        }
        catch
        {
            // overlay swap
        }
    }

    /// <summary>
    /// Before a world transform: in a Lights Out dark zone with a sky backdrop, the
    /// list keeps only the castle polygons; the sky ones wait for <see cref="DrawOversightSky"/>.
    /// </summary>
    static void SplitOversightSky(CpuContext c, IMemory m)
    {
        if (_oversightSkyList != 0 || !ConfigManager.View.FixOriginalOversights) return;
        try
        {
            if (m.ReadU32(Catalog.LevelIdAddr) != LidLightsOut) return;
            uint zone = m.ReadU32(CamZoneAddr);
            if (!NativeWideGuestPointer(zone)) return;
            uint header = EntryItem(m, zone, 0);
            if (!NativeWideGuestPointer(header) || (m.ReadU32(header + 0x2FCu) & ZoneFlagDark2) == 0) return;
            Span<uint> meshes = stackalloc uint[8];
            uint sky = MapSkyMeshes(m, header, meshes);
            if (sky == 0) return;
            uint list = m.ReadU32(MapCurPolyIdsAddr);
            if (!NativeWideGuestPointer(list)) return;
            int length = m.ReadU16(list);
            int skyCount = 0;
            for (int i = 0; i < length; i++)
            {
                ushort id = m.ReadU16(list + 4u + (uint)i * 2u);
                if ((sky & (1u << ((id >> 12) & 7))) == 0) continue;
                if (skyCount == _oversightSkyIds.Length) return;
                _oversightSkyIds[skyCount++] = id;
            }
            if (skyCount == 0) return;

            int bytes = 4 + length * 2;
            if (_oversightSkySaved.Length < bytes) _oversightSkySaved = new byte[bytes];
            for (int i = 0; i < bytes; i++) _oversightSkySaved[i] = m.ReadU8(list + (uint)i);
            int kept = 0;
            for (int i = 0; i < length; i++)
            {
                ushort id = (ushort)(_oversightSkySaved[4 + i * 2] | _oversightSkySaved[5 + i * 2] << 8);
                if ((sky & (1u << ((id >> 12) & 7))) != 0) continue;
                m.WriteU16(list + 4u + (uint)kept++ * 2u, id);
            }
            m.WriteU16(list, (ushort)kept);
            _oversightSkyList = list;
            _oversightSkyBytes = bytes;
            _oversightSkyCount = skyCount;
            _oversightSkyOt = c.A0;
        }
        catch
        {
            // zone swap
        }
    }

    /// <summary>
    /// After the dark pass, before the 16:9 capture closes: the sky polygons through
    /// the plain GfxTransformWorlds (same OT, transformed last, so drawn first in
    /// their slots), then the original list back for LevelUpdate's deltas.
    /// </summary>
    static void DrawOversightSky(CpuContext c, IMemory m)
    {
        uint list = _oversightSkyList;
        if (list == 0) return;
        _oversightSkyList = 0;
        var saved = c.Snapshot();
        try
        {
            for (int i = 0; i < _oversightSkyCount; i++)
                m.WriteU16(list + 4u + (uint)i * 2u, _oversightSkyIds[i]);
            m.WriteU16(list, (ushort)_oversightSkyCount);
            _oversightSkyPass = true;
            c.A0 = _oversightSkyOt;
            Dispatcher.Call(c, m, GfxTransformWorldsAddr);
        }
        catch
        {
            // overlay swap
        }
        finally
        {
            _oversightSkyPass = false;
            c.Restore(saved);
            try
            {
                for (int i = 0; i < _oversightSkyBytes; i++) m.WriteU8(list + (uint)i, _oversightSkySaved[i]);
            }
            catch
            {
                // overlay swap
            }
        }
    }

    /// <summary>16:9 side bands: the Lights Out sky backdrop keeps its own colours, like the 4:3 pass.</summary>
    static bool OversightSkyUnlit(IMemory m, uint wgeoHeader) =>
        FastU32(m, wgeoHeader + 0x1Cu) != 0
        && m.ReadU32(Catalog.LevelIdAddr) == LidLightsOut
        && ConfigManager.View.FixOriginalOversights;
}
