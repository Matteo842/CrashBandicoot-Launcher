using System.Diagnostics;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime.Sdk;

public static class LibEtc
{
    static int _vcount;
    static readonly VSyncEvent _vsyncEvent = new();
    static readonly long VblankTicks = Stopwatch.Frequency / 60;
    static long _lastVblankTs;
    static int _catchUpGuard;

    const uint VBlankCountAddr = 0x800549F0u;
    const uint TicksElapsedAddr = 0x80034520u;
    const uint TicksPerVBlank = 17u;

    /// <summary>
    /// HLE for the PsyQ vblank wait at 0x8003E638 (not the public VSync entry).
    /// Unlocked gameplay skips the immediate pad wait so the loop can run at 60/120/240.
    /// ticks_elapsed advances by real dt (1020 ticks/s) per present.
    /// </summary>
    public static void VSync(CpuContext c, IMemory m)
    {
        if (FramePacing.IsActive(m))
        {
            UnlockedVSync(c, m);
            return;
        }

        uint target = c.A0;
        uint count = m.ReadU32(VBlankCountAddr);
        while (count < target)
            count = AdvanceVBlank(c, m);
        c.V0 = 0;
    }

    static void UnlockedVSync(CpuContext c, IMemory m)
    {
        if (FramePacing.IsPadCall())
        {
            c.V0 = 0;
            return;
        }

        uint target = c.A0;
        uint count = m.ReadU32(VBlankCountAddr);
        if (count >= target)
        {
            c.V0 = 0;
            return;
        }

        while (count < target)
            count = AdvanceUnlocked(c, m);

        FramePacing.MarkPrimaryEnd();
        c.V0 = 0;
    }

    static uint AdvanceUnlocked(CpuContext? c, IMemory m)
    {
        _catchUpGuard++;
        try
        {
            uint ticks = FramePacing.AdvanceWallClock(m);
            m.WriteU32(TicksElapsedAddr, ticks);
            Runtime.PresentFrame();
            return TickCounters(c, m, addTicks: false);
        }
        finally
        {
            _catchUpGuard--;
        }
    }

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
            if (m == null) return;
            // Unlocked: keep SPU/sequencer alive during long CPU work, but never
            // advance ticks_elapsed — that is owned by the present step.
            if (FramePacing.IsActive(m))
            {
                _lastVblankTs = now;
                Runtime.DispatchIrq(0);
                FramePacing.NoteVblankIrq();
                return;
            }
            TickSequencer(Runtime.Cpu, m);
        }
        finally
        {
            _catchUpGuard--;
        }
    }

    public static uint AdvanceVBlank(CpuContext? c, IMemory m)
    {
        _catchUpGuard++;
        try
        {
            Runtime.PresentFrame();
            return TickCounters(c, m, addTicks: true);
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
        FramePacing.NoteVblankIrq();
        return TickCounters(c, m, addTicks: true);
    }

    static uint TickCounters(CpuContext? c, IMemory m, bool addTicks)
    {
        _lastVblankTs = Stopwatch.GetTimestamp();
        uint count = m.ReadU32(VBlankCountAddr) + 1;
        m.WriteU32(VBlankCountAddr, count);
        if (addTicks)
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
