using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Sdk;

public static class LibEtc
{
    static int _vcount;
    static readonly VSyncEvent _vsyncEvent = new();

    // PsyQ polls this; Crash's VSync(0) waits until count >= target.
    const uint VBlankCountAddr = 0x800549F0u;
    // Crash drives GOOL / frame delta off ticks_elapsed → draw_stamp → frames_elapsed.
    const uint TicksElapsedAddr = 0x80034520u;
    // ~1/60s in Crash's tick units. Game loop pads a second VSync(0) when delta < 25,
    // locking gameplay to ~30fps. 34 was ~two vblanks and skipped that pad → 2x speed.
    const uint TicksPerVBlank = 17u;

    /// <summary>
    /// HLE for the PsyQ vblank wait at 0x8003E638 (not the public VSync entry).
    /// A0 = target vblank count; presents/throttles once per vblank until reached.
    /// </summary>
    public static void VSync(CpuContext c, IMemory m)
    {
        uint target = c.A0;
        uint count = m.ReadU32(VBlankCountAddr);
        Log.Sdk($"WaitVBlank(target={target}, count={count})");

        while (count < target)
        {
            Runtime.PresentFrame();
            count++;
            m.WriteU32(VBlankCountAddr, count);
            m.WriteU32(TicksElapsedAddr, m.ReadU32(TicksElapsedAddr) + TicksPerVBlank);
            _vcount = (int)count;

            if (Event.HasAnyListeners<VSyncEvent>())
            {
                var e = _vsyncEvent;
                e.Context = c;
                e.Memory = m;
                e.Frame = _vcount;
                Event.Dispatch(e);
            }
        }

        c.V0 = 0;
    }
}
