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

    // Display vsync grid reported by the platform (Android Choreographer).
    // The present clock stays software-timed, but its phase is pulled to the
    // middle of a vsync interval: a free-running 60 Hz timer drifts across
    // the latch point, so some images stay up 1 vblank and others 3 (judder,
    // worst at 30 FPS where each image should stay exactly 2).
    static long _vsyncTs;        // Stopwatch timestamp of a recent vsync
    static double _vsyncPeriodMs; // 0 = no vsync source
    static double _phaseErrMs;

    /// <summary>Lock the present phase to the reported vsync grid (on by default when a platform reports vsync).</summary>
    public static bool VsyncPhaseLock { get; set; } = true;

    /// <summary>
    /// Platform vsync tick: <paramref name="stopwatchTimestamp"/> in
    /// <see cref="Stopwatch.GetTimestamp"/> units, <paramref name="periodMs"/>
    /// the display refresh period. Any thread.
    /// </summary>
    public static void ReportVsync(long stopwatchTimestamp, double periodMs)
    {
        Volatile.Write(ref _vsyncTs, stopwatchTimestamp);
        Volatile.Write(ref _vsyncPeriodMs, periodMs);
    }

    /// <summary>Filtered present phase error from mid vsync, ms (diagnostics; 0 without vsync).</summary>
    public static double PhaseErrorMs => Volatile.Read(ref _vsyncPeriodMs) > 0 ? _phaseErrMs : 0;

    /// <summary>Stop phase locking (vsync source gone, e.g. app in background).</summary>
    public static void ClearVsync()
    {
        Volatile.Write(ref _vsyncPeriodMs, 0);
        _phaseErrMs = 0;
    }

    static readonly long _clockStartTs = Stopwatch.GetTimestamp() - _clock.ElapsedTicks;

    /// <summary>
    /// Nudge the next deadline so the present that just finished (Throttle
    /// entry) lands mid vsync interval. At most 0.25 ms per frame, so the
    /// vblank clock (music, PS1 pad) never jumps.
    /// </summary>
    static void LockPhase(double period, double now)
    {
        double vp = Volatile.Read(ref _vsyncPeriodMs);
        if (!VsyncPhaseLock || vp < 2.0) return;
        double ratio = period / vp;
        double n = Math.Round(ratio);
        if (n < 1 || Math.Abs(ratio - n) > 0.02) return; // cap not a multiple of the display rate

        long vts = Volatile.Read(ref _vsyncTs);
        double vsyncMs = (vts - _clockStartTs) * 1000.0 / Stopwatch.Frequency;
        if (now - vsyncMs > 1000.0) return; // stale: no vsync for a second
        double phase = (now - vsyncMs) % vp;
        if (phase < 0) phase += vp;
        double err = phase - vp * 0.5;
        _phaseErrMs += (err - _phaseErrMs) * 0.1;
        _nextFrameMs -= Math.Clamp(_phaseErrMs * 0.1, -0.25, 0.25);
    }

    public static void PauseTiming() => _runSignal.Reset();

    public static void ResumeTiming()
    {
        _nextFrameMs = _clock.Elapsed.TotalMilliseconds;
        FramePacing.Reset();
        Diagnostics.FrameTimer.Reset(); // the hold is not a frame
        _runSignal.Set();
    }

    public static void Throttle()
    {
        _runSignal.Wait();
        double period = FramePacing.NeedsOriginalVblank ? (1000.0 / 60.0) : _frameMs;
        if (SkipThrottle && !FramePacing.NeedsOriginalVblank) return;

        _nextFrameMs += period;
        double now = _clock.Elapsed.TotalMilliseconds;
        LockPhase(period, now);
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
