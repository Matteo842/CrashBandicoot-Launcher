using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RecompOne.Runtime.Host;

public static class FrameClock
{
    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static readonly ManualResetEventSlim _runSignal = new(initialState: true);
    static double _frameMs = 1000.0 / 60.0;
    static double _nextFrameMs;

    /// <summary>Software present cap in Hz. Ignored when <see cref="SkipThrottle"/> is set.</summary>
    public static double TargetHz
    {
        get => 1000.0 / _frameMs;
        set
        {
            double hz = Math.Clamp(value, 1.0, 500.0);
            _frameMs = 1000.0 / hz;
            _nextFrameMs = _clock.Elapsed.TotalMilliseconds;
        }
    }

    /// <summary>When true, skip software throttle (display VSync paces the frame).</summary>
    public static bool SkipThrottle { get; set; }

    public static void PauseTiming() => _runSignal.Reset();

    public static void ResumeTiming()
    {
        _nextFrameMs = _clock.Elapsed.TotalMilliseconds;
        FramePacing.Reset();
        _runSignal.Set();
    }

    public static void Throttle()
    {
        _runSignal.Wait();
        double period = FramePacing.NeedsOriginalVblank ? (1000.0 / 60.0) : _frameMs;
        if (SkipThrottle && !FramePacing.NeedsOriginalVblank) return;

        _nextFrameMs += period;
        double now = _clock.Elapsed.TotalMilliseconds;
        double waitMs = _nextFrameMs - now;

        if (waitMs < 0)
        {
            // Late: drop the missed time. Do not burst extra VBlanks — that
            // fast-forwards Crash's sequencer and the music speeds up.
            if (waitMs < -period)
                _nextFrameMs = now;
            return;
        }

        // Sleep most of the wait, spin only the tail. A full-frame spin pins a
        // core at 100%: on Steam Deck / laptops that eats the shared CPU+GPU
        // power budget, and on 2-core PCs it starves the audio / CD threads.
        if (waitMs > SpinMarginMs)
            PreciseSleep.Sleep(waitMs - SpinMarginMs);

        while (_clock.Elapsed.TotalMilliseconds < _nextFrameMs)
            Thread.SpinWait(80);
    }

    /// <summary>
    /// Final stretch of each wait that is spun instead of slept. Covers timer
    /// wake-up jitter (Windows high-res timer ~0.5 ms, Wine/Proton ~1 ms).
    /// </summary>
    const double SpinMarginMs = 2.0;
}

/// <summary>Sub-millisecond-ish sleep that does not burn a core.</summary>
static class PreciseSleep
{
    // Windows: high-resolution waitable timer (Win10 1803+). Falls back to
    // timeBeginPeriod(1) + Sleep(1) when the flag is rejected (older Windows, some Wine).
    // Elsewhere Thread.Sleep is nanosleep-backed and already ~1 ms accurate.
    const uint CreateWaitableTimerHighResolution = 0x00000002;
    const uint TimerAllAccess = 0x1F0003;
    const uint Infinite = 0xFFFFFFFF;

    static nint _timer;
    static bool _initDone;
    static bool _periodRaised;

    public static void Sleep(double ms)
    {
        if (ms <= 0) return;
        if (!OperatingSystem.IsWindows())
        {
            SleepCoarse(ms);
            return;
        }

        if (!_initDone) Init();
        if (_timer != 0)
        {
            long due = -(long)(ms * 10_000.0); // relative, 100 ns units
            if (SetWaitableTimer(_timer, ref due, 0, 0, 0, false))
            {
                WaitForSingleObject(_timer, Infinite);
                return;
            }
        }
        SleepCoarse(ms);
    }

    /// <summary>Thread.Sleep(1) steps; stops before overshooting <paramref name="ms"/>.</summary>
    static void SleepCoarse(double ms)
    {
        long end = Stopwatch.GetTimestamp() + (long)(ms * Stopwatch.Frequency / 1000.0);
        long oneMs = Stopwatch.Frequency / 1000;
        while (end - Stopwatch.GetTimestamp() > oneMs)
            Thread.Sleep(1);
    }

    static void Init()
    {
        _initDone = true;
        try
        {
            _timer = CreateWaitableTimerExW(0, 0, CreateWaitableTimerHighResolution, TimerAllAccess);
        }
        catch
        {
            _timer = 0;
        }
        if (_timer != 0) return;

        // Default Windows tick is 15.6 ms — Sleep(1) would overshoot a whole frame.
        try { _periodRaised = timeBeginPeriod(1) == 0; }
        catch { _periodRaised = false; }
        Console.WriteLine($"[FrameClock] high-res timer unavailable; timeBeginPeriod(1)={_periodRaised}");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern nint CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetWaitableTimer(nint timer, ref long dueTime, int period,
        nint completionRoutine, nint completionArg, bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("winmm.dll")]
    static extern uint timeBeginPeriod(uint period);
}
