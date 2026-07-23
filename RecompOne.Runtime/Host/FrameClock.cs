using System.Diagnostics;

namespace RecompOne.Runtime.Host;

internal static class FrameClock
{
    const double FrameMs = 1000.0 / 60.0;

    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static double _nextFrameMs;

    //maybe not the best but it seens to work for now
    public static void Throttle()
    {
        _nextFrameMs += FrameMs;
        double now = _clock.Elapsed.TotalMilliseconds;
        double wait = _nextFrameMs - now;
        if (wait > 1) Thread.Sleep((int)wait);
        else if (wait < -100) _nextFrameMs = now;
    }
}
