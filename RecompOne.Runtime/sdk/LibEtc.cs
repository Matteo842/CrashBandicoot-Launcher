using System.Diagnostics;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Sdk;

public static class LibEtc
{
    static int _vcount;
    static readonly VSyncEvent _vsyncEvent = new();
    static readonly long VblankTicks = Stopwatch.Frequency / 60;
    static long _lastVblankTs;
    static int _catchUpGuard;

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

        while (count < target)
        {
            count = AdvanceVBlank(c, m);
        }

        c.V0 = 0;
    }

    /// <summary>
    /// Keep Crash's VBlank-driven sequencer moving during long CPU work
    /// (decompress, copies) without presenting or sleeping — loads stay
    /// instant, the display stays on the last frame, music does not freeze.
    /// </summary>
    public static void MaybeCatchUpVBlank()
    {
        if (_catchUpGuard != 0 || Runtime.Mem == null) return;
        if (!CpuLooksSafeForIrq()) return;
        long now = Stopwatch.GetTimestamp();
        if (_lastVblankTs == 0)
        {
            _lastVblankTs = now;
            return;
        }
        if (now - _lastVblankTs < VblankTicks) return;

        _catchUpGuard++;
        try
        {
            var m = Runtime.Mem;
            if (m != null)
                TickSequencer(Runtime.Cpu, m);
        }
        finally
        {
            _catchUpGuard--;
        }
    }

    /// <summary>
    /// Full emulated VBlank used by VSync: present, throttle, IRQ, counters.
    /// </summary>
    public static uint AdvanceVBlank(CpuContext? c, IMemory m)
    {
        _catchUpGuard++;
        try
        {
            Runtime.PresentFrame();
            return TickCounters(c, m);
        }
        finally
        {
            _catchUpGuard--;
        }
    }

    static uint TickSequencer(CpuContext? c, IMemory m)
    {
        _lastVblankTs = Stopwatch.GetTimestamp();
        Runtime.DispatchIrq(0);
        return TickCounters(c, m);
    }

    static uint TickCounters(CpuContext? c, IMemory m)
    {
        _lastVblankTs = Stopwatch.GetTimestamp();
        uint count = m.ReadU32(VBlankCountAddr) + 1;
        m.WriteU32(VBlankCountAddr, count);
        m.WriteU32(TicksElapsedAddr, m.ReadU32(TicksElapsedAddr) + TicksPerVBlank);
        _vcount = (int)count;

        if (c != null && Event.HasAnyListeners<VSyncEvent>())
        {
            var e = _vsyncEvent;
            e.Context = c;
            e.Memory = m;
            e.Frame = _vcount;
            Event.Dispatch(e);
        }
        return count;
    }

    static bool CpuLooksSafeForIrq()
    {
        var c = Runtime.Cpu;
        if (c == null) return false;
        return (c.GP & 0xFF000000u) == 0x80000000u
            && (c.SP & 0xFF000000u) == 0x80000000u;
    }
}
