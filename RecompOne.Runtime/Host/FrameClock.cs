using System.Diagnostics;

namespace RecompOne.Runtime.Host;

public static class FrameClock
{
    const double FrameMs = 1000.0 / 60.0;

    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static readonly ManualResetEventSlim _runSignal = new(initialState: true);
    static double _nextFrameMs;

    /// <summary>When true, skip software throttle (display VSync paces the frame).</summary>
    public static bool SkipThrottle { get; set; }

    public static void PauseTiming() => _runSignal.Reset();

    public static void ResumeTiming()
    {
        _nextFrameMs = _clock.Elapsed.TotalMilliseconds;
        _runSignal.Set();
    }

    public static void Throttle()
    {
        _runSignal.Wait();
        if (SkipThrottle) return;

        _nextFrameMs += FrameMs;
        double now = _clock.Elapsed.TotalMilliseconds;
        double waitMs = _nextFrameMs - now;

        if (waitMs < 0)
        {
            // Late: drop the missed time. Do not burst extra VBlanks — that
            // fast-forwards Crash's sequencer and the music speeds up.
            if (waitMs < -FrameMs)
                _nextFrameMs = now;
            return;
        }

        // Spin the remainder. Thread.Sleep on Android is often 8–16 ms and
        // turns a 4 ms wait into a hitch that then gets "caught up".
        while (_clock.Elapsed.TotalMilliseconds < _nextFrameMs)
            Thread.SpinWait(80);
    }
}
