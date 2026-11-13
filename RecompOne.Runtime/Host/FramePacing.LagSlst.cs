using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Host;

public static partial class FramePacing
{
    // #220: black wedges along the bottom of the screen on the hog levels above
    // 30 FPS. The view is drawn one 30 Hz step behind: unlocked, the hog camera
    // lerps the last two step poses (ApplyHogCamPose); "Exact 30 FPS logic"
    // draw passes lerp every camera the same way (InterpCamera). The world draw
    // list (SLST) is the newest step's, picked by LevelUpdate from progress >> 8.
    // When that step crossed a camera path point, the new list has already
    // dropped the ground right under the camera, but the lagging view still
    // shows it at the bottom edge, so the clear colour shows through. Retail
    // draws a view between points N and N+1 with list N. While the drawn
    // progress is still before the newest point, decode that point's list with
    // the game's own SlstUpdate into the spare poly-id buffer and point the
    // transform at it; LevelUpdate gets the exact list back right after.
    // Same path only (a path switch rebuilds the list from the other path).

    readonly record struct CamPoint(uint Zone, uint Path, int Progress);

    /// <summary>Camera points of the hog lerp (unlocked) and the interp camera lerp.</summary>
    static CamPoint _hogPointFrom, _hogPointTo, _interpPointFrom, _interpPointTo;
    static double _hogPoseS;

    /// <summary>List pointer to put back (0 = not patched).</summary>
    static uint _lagSlstCur;
    /// <summary>Buffer whose bytes were overwritten by a multi-step decode (0 = none).</summary>
    static uint _lagSlstBytesAddr;
    static int _lagSlstBytes;
    static byte[] _lagSlstSaved = [];
    const int LagSlstMaxSteps = 3;

    static CamPoint ReadCamPoint(IMemory m) =>
        new(m.ReadU32(CamZoneAddr), m.ReadU32(CamPathAddr), Math.Abs((int)m.ReadU32(CamProgressAddr)));

    /// <summary>Before a world transform: the list of the point the lagging view is at.</summary>
    static void PatchLagSlst(CpuContext c, IMemory m)
    {
        RestoreLagSlst(m);
        CamPoint from, to;
        double t;
        if (_hogPosePatched)
            (from, to, t) = (_hogPointFrom, _hogPointTo, _hogPoseS);
        else if (_interpRender && _interpCamHasFrom && _interpCamStamp == _interpDone)
            (from, to, t) = (_interpPointFrom, _interpPointTo, _interpT);
        else
            return;
        if (from.Zone != to.Zone || from.Path != to.Path || from.Progress == to.Progress) return;
        try
        {
            if (IslandMapShowing(m)) return; // MapSkyList handles the map
            if (ReadCamPoint(m) != to) return; // the list belongs to the newest pose
            uint path = to.Path;
            if (!NativeWideGuestPointer(to.Zone) || !NativeWideGuestPointer(path)) return;
            int ptFrom = from.Progress >> 8, ptTo = to.Progress >> 8;
            double drawn = from.Progress + (to.Progress - from.Progress) * Math.Clamp(t, 0, 1);
            int pt = (int)Math.Floor(drawn / 256.0);
            if (pt == ptTo || pt < Math.Min(ptFrom, ptTo) || pt > Math.Max(ptFrom, ptTo)) return;
            int steps = Math.Abs(pt - ptTo);
            int length = m.ReadU16(path + ZonePathLengthOff);
            if (steps > LagSlstMaxSteps || pt < 0 || pt >= length || ptTo >= length) return;
            uint slst = MapResidentEntry(m, path);
            if (slst == 0) return;

            // Point k-1 <-> k is items[k] (dir 0 back, dir 1 forward); item 0 and
            // item length are the raw end lists.
            Span<(uint Item, uint Dir)> chain = stackalloc (uint, uint)[LagSlstMaxSteps];
            for (int i = 0; i < steps; i++)
            {
                int k = pt < ptTo ? ptTo - i : ptTo + 1 + i;
                if (k < 1 || k >= length) return;
                uint item = EntryItem(m, slst, k);
                if (!NativeWideGuestPointer(item)) return;
                chain[i] = (item, pt < ptTo ? 0u : 1u);
            }

            uint cur = m.ReadU32(MapCurPolyIdsAddr);
            uint spare = m.ReadU32(MapSparePolyIdsAAddr);
            if (spare == cur) spare = m.ReadU32(MapSparePolyIdsBAddr);
            if (spare == cur || !NativeWideGuestPointer(cur) || !NativeWideGuestPointer(spare)) return;

            // More than one step ping-pongs through the real list's buffer: keep its bytes.
            int bytes = 4 + Math.Min((int)m.ReadU16(cur), 0x2000) * 2;
            if (steps > 1)
            {
                if (_lagSlstSaved.Length < bytes) _lagSlstSaved = new byte[bytes];
                for (int i = 0; i < bytes; i++) _lagSlstSaved[i] = m.ReadU8(cur + (uint)i);
            }
            uint src = cur, dst = spare;
            var saved = c.Snapshot();
            try
            {
                for (int i = 0; i < steps; i++)
                {
                    c.A0 = chain[i].Item;
                    c.A1 = src;
                    c.A2 = dst;
                    c.A3 = chain[i].Dir;
                    Dispatcher.Call(c, m, MapSlstUpdateAddr);
                    (src, dst) = (dst, src);
                }
            }
            finally
            {
                c.Restore(saved);
            }

            if (src == cur)
            {
                // Even step count: the result sits in the real buffer until PostWorlds.
                _lagSlstBytesAddr = cur;
                _lagSlstBytes = bytes;
                return;
            }
            if (steps > 1)
                for (int i = 0; i < bytes; i++) m.WriteU8(cur + (uint)i, _lagSlstSaved[i]);
            m.WriteU32(MapCurPolyIdsAddr, spare);
            _lagSlstCur = cur;
        }
        catch
        {
            // zone swap
        }
    }

    /// <summary>After the world transform, and before anything else reads the list.</summary>
    static void RestoreLagSlst(IMemory m)
    {
        if (_lagSlstCur == 0 && _lagSlstBytesAddr == 0) return;
        try
        {
            if (_lagSlstCur != 0)
                m.WriteU32(MapCurPolyIdsAddr, _lagSlstCur);
            if (_lagSlstBytesAddr != 0)
                for (int i = 0; i < _lagSlstBytes; i++) m.WriteU8(_lagSlstBytesAddr + (uint)i, _lagSlstSaved[i]);
        }
        catch
        {
            // overlay swap
        }
        _lagSlstCur = 0;
        _lagSlstBytesAddr = 0;
    }
}
