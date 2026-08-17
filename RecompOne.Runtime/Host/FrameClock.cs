using System.Diagnostics;

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

        while (_clock.Elapsed.TotalMilliseconds < _nextFrameMs)
            Thread.SpinWait(80);
    }
}
