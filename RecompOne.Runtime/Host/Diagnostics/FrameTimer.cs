using System.Diagnostics;
using System.Runtime;

namespace RecompOne.Runtime.Host.Diagnostics;

/// <summary>
/// Per-frame time split for the Dev HUD. <see cref="Mark"/> charges the time since
/// the previous mark to a phase, so phases never overlap and always sum to the frame.
/// All calls come from the game thread (OnRender runs inside DoRender).
/// </summary>
public static class FrameTimer
{
    public enum Phase
    {
        Game,   // recompiled game code + GPU command building
        Events, // window events, input, menus
        Render, // host GL present (last batch flush + output pass)
        Ui,     // ImGui menus / HUD build + draw
        Swap,   // SwapBuffers (VSync / waiting on the GPU)
        Wait,   // FrameClock throttle
        Count,
    }

    const double WindowMs = 500;
    const double PeakHoldMs = 5000;

    static readonly double[] _acc = new double[(int)Phase.Count];
    static readonly double[] _avg = new double[(int)Phase.Count];
    static long _mark;
    static long _frameStart;
    static long _windowStart;
    static int _frames;
    static double _worst;
    static double _lastJitMs = -1;
    static double _lastGcMs;
    static long _peakTs;
    // This frame only, for AutoTune.
    static double _frameGame;
    static double _frameWait;

    public static double Average(Phase p) => _avg[(int)p];
    public static double AverageFrameMs { get; private set; }
    /// <summary>Slowest single frame in the last window — spikes / hitches.</summary>
    public static double WorstFrameMs { get; private set; }

    /// <summary>Slowest frame of the last 5 s, held so it can be read / screenshotted.</summary>
    public static double PeakFrameMs { get; private set; }
    /// <summary>Of <see cref="PeakFrameMs"/>: time .NET spent JIT-compiling code first run.</summary>
    public static double PeakJitMs { get; private set; }
    /// <summary>Of <see cref="PeakFrameMs"/>: garbage-collector pause time.</summary>
    public static double PeakGcMs { get; private set; }

    public static void Mark(Phase p)
    {
        long now = Stopwatch.GetTimestamp();
        if (_mark != 0)
        {
            double ms = ToMs(now - _mark);
            _acc[(int)p] += ms;
            if (p == Phase.Game) _frameGame += ms;
            else if (p == Phase.Wait) _frameWait += ms;
        }
        _mark = now;
    }

    public static void EndFrame()
    {
        long now = Stopwatch.GetTimestamp();
        // Game thread only: background tier-up and JitWarmup compile elsewhere and
        // never stall a frame, so counting all threads overstated hitches.
        double jitMs = JitInfo.GetCompilationTime(currentThread: true).TotalMilliseconds;
        double gcMs = GC.GetTotalPauseDuration().TotalMilliseconds;
        if (_frameStart != 0 && _lastJitMs >= 0)
        {
            double frame = ToMs(now - _frameStart);
            if (frame > _worst) _worst = frame;
            _frames++;
            AutoTune.OnFrame(frame, _frameGame, _frameWait);

            if (frame > PeakFrameMs || ToMs(now - _peakTs) > PeakHoldMs)
            {
                PeakFrameMs = frame;
                PeakJitMs = jitMs - _lastJitMs;
                PeakGcMs = gcMs - _lastGcMs;
                _peakTs = now;
            }
        }
        _frameGame = 0;
        _frameWait = 0;
        _lastJitMs = jitMs;
        _lastGcMs = gcMs;
        _frameStart = now;
        if (_windowStart == 0) _windowStart = now;

        double windowMs = ToMs(now - _windowStart);
        if (windowMs < WindowMs || _frames == 0) return;

        for (int i = 0; i < _acc.Length; i++)
        {
            _avg[i] = _acc[i] / _frames;
            _acc[i] = 0;
        }
        AverageFrameMs = windowMs / _frames;
        WorstFrameMs = _worst;
        _worst = 0;
        _frames = 0;
        _windowStart = now;
    }

    /// <summary>
    /// Forget the frame in flight (game held at the frame clock: app in background,
    /// Android pause screen), so the hold is not reported as one huge frame.
    /// </summary>
    public static void Reset()
    {
        _mark = 0;
        _frameStart = 0;
        _windowStart = 0;
        _frames = 0;
        _worst = 0;
        _frameGame = 0;
        _frameWait = 0;
        Array.Clear(_acc);
    }

    static double ToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
