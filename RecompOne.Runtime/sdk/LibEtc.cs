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
    /// GpuUpdate's 30 FPS limiter: <c>if (ticks_elapsed - c2_p-&gt;draw_stamp &lt; 25) VSync(0)</c>.
    /// Return address of that second VSync(0) (the first returns to 0x800170FC).
    /// </summary>
    const uint GpuLimiterVSyncRa = 0x8001712Cu;
    /// <summary>PsyQ VSync (0x8003E4F0) does SP -= 0x20 and saves its RA at SP+0x18.</summary>
    const uint PsyqVSyncRaSlot = 0x18u;

    /// <summary>
    /// HLE for the PsyQ vblank wait at 0x8003E638 (not the public VSync entry).
    /// Unlocked gameplay skips the immediate pad wait so the loop can run at 60/120/240.
    /// ticks_elapsed advances by real dt (1020 ticks/s) per present.
    /// </summary>
    public static void VSync(CpuContext c, IMemory m)
    {
        // Unlocked: the limiter wait was a second present + throttle per loop,
        // so every preset simulated half its rate ("60 FPS" = 30 Hz loop with
        // dt≈34 flapping on the RefTicks-0.01 gates; 240 = 120 Hz).
        if (FramePacing.IsActive(m) && IsGpuLimiterWait(c, m))
        {
            c.V0 = 0;
            return;
        }
        FramePacing.NoteSoftwareVblank();
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

    static bool IsGpuLimiterWait(CpuContext c, IMemory m)
    {
        if ((c.SP & 0xFF000000u) != 0x80000000u) return false;
        try { return m.ReadU32(c.SP + PsyqVSyncRaSlot) == GpuLimiterVSyncRa; }
        catch { return false; }
    }

    static void UnlockedVSync(CpuContext c, IMemory m)
    {
        FramePacing.NoteGpuVSync();
        if (FramePacing.IsPadCall())
        {
            FramePacing.FinishGpuUpdate(m);
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
            FramePacing.NoteGpuPresent();
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
                DispatchVblankIrq();
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
        DispatchVblankIrq();
        FramePacing.NoteVblankIrq();
        return TickCounters(c, m, addTicks: true);
    }

    /// <summary>Vblank-clocked audio renders and dispatches whatever IRQs are due.</summary>
    static void DispatchVblankIrq()
    {
        if (SpuStream.Enabled) SpuStream.PumpVblanks();
        else Runtime.DispatchIrq(0);
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
