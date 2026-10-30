using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    /// <summary>
    /// #186 pity mask (ViewConfig.PityMask), Crash 2/3 style: after
    /// <see cref="PityMaskDeaths"/> deaths in a row without a new checkpoint, Crash
    /// respawns with an Aku Aku mask, and again on every later death there.
    /// Retail already does it in boss levels: WillC <c>Willy_Warp_In</c> sends Event6
    /// to its <c>creator</c> (DoctC sets <c>collider-&gt;creator = self</c> on spawn)
    /// when RESPAWNCOUNT is set. DoctC <c>Witch_Doctor_Dead</c> takes Event6 like a
    /// mask box: Event40 to Crash, health 1, moves to <c>a[0]</c>. We send the same
    /// event, with Crash as sender and a[0], on the frame of the respawn.
    /// </summary>
    const int PityMaskDeaths = 3;
    /// <summary>c1 gool.c GoolSendEvent(sender, recipient, event, argc, argv); argv is the 5th (stack) arg.</summary>
    const uint GoolSendEventAddr = 0x80024040u;
    const uint EventDoctorUp = 0x600u;
    const uint GoolTypeDoct = 5u;
    /// <summary>c1 globals: respawn_count (+0x100 per LevelRestart after a death), checkpoint_id (-1 = level start).</summary>
    const uint RespawnCountAddr = 0x800618A0u;
    const uint CheckpointIdAddr = 0x800619A0u;
    const uint OneRespawn = 0x100u;

    static uint _pityLid = uint.MaxValue;
    static uint _pityCheckpoint;
    static uint _pityRespawns;
    static int _pityDeaths;

    /// <summary>PostGpuUpdate, once per frame, outside any object update.</summary>
    static void UpdatePityMask(CpuContext c, IMemory m)
    {
        if (!ConfigManager.View.PityMask)
        {
            _pityLid = uint.MaxValue;
            return;
        }
        try
        {
            uint lid = m.ReadU32(Catalog.LevelIdAddr);
            if (!PityMaskLevel(lid))
            {
                _pityLid = uint.MaxValue;
                return;
            }

            uint checkpoint = m.ReadU32(CheckpointIdAddr);
            uint respawns = m.ReadU32(RespawnCountAddr);
            if (lid != _pityLid || checkpoint != _pityCheckpoint)
            {
                // New level or new checkpoint: progress, the count starts over.
                _pityLid = lid;
                _pityCheckpoint = checkpoint;
                _pityRespawns = respawns;
                _pityDeaths = 0;
                return;
            }
            if (respawns == _pityRespawns) return;
            // One death = one LevelRestart. Any other jump is a loaded save state.
            bool died = respawns - _pityRespawns == OneRespawn;
            _pityRespawns = respawns;
            if (!died || ++_pityDeaths < PityMaskDeaths) return;

            // loadcheckpoint and changestate(Willy_Warp_In) run in the same death
            // code, so Crash is already respawning (Warp_In / Force_Fall) here.
            if (!TryReadCrash(m, out uint crash)) return;
            uint state = m.ReadU32(crash + ObjStateOff);
            if (state is >= StateDeathFall and <= StateDeathFlat) return;

            // Only a dead Aku Aku (Witch_Doctor_Dead hides him): a living one would
            // take Event6 as one more mask.
            uint doctor = m.ReadU32(crash + ObjCreatorOff);
            if ((doctor & 0xFF000000u) != 0x80000000u
                || !TryReadGoolClass(m, doctor, out uint type, out _) || type != GoolTypeDoct
                || (m.ReadU32(doctor + ObjStatusBOff) & FlagInvisible) == 0)
                return;
            SendDoctorUp(c, m, crash, doctor);
            PaceLog($"pity mask after {_pityDeaths} deaths (lid {lid}, checkpoint 0x{checkpoint:X})");
        }
        catch
        {
            // level loading / objects recycled
        }
    }

    // Bosses respawn Aku Aku on their own (see above); on the hog he never rides along.
    static bool PityMaskLevel(uint lid) =>
        lid is not (8u or 10u or 23u or 27u or 31u or 33u or 17u or 30u)
        && Catalog.Levels.TryGet(lid, out var info) && info.Kind == LevelKind.Gameplay;

    static void SendDoctorUp(CpuContext c, IMemory m, uint crash, uint doctor)
    {
        var saved = c.Snapshot();
        try
        {
            // Outgoing args area below the current frame: argv pointer at SP+0x10
            // (5th arg), argv[0] = Crash.
            uint sp = (c.SP - 0x40u) & ~7u;
            uint argv = sp + 0x20u;
            m.WriteU32(argv, crash);
            m.WriteU32(sp + 0x10u, argv);
            c.SP = sp;
            c.A0 = crash;
            c.A1 = doctor;
            c.A2 = EventDoctorUp;
            c.A3 = 1u;
            Dispatcher.Call(c, m, GoolSendEventAddr);
        }
        finally
        {
            c.Restore(saved);
        }
    }
}
