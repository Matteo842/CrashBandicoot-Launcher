using Android.App;
using Android.Views;
using RecompOne.Runtime.Host;

namespace CrashBandicoot.AndroidRuntime;

/// <summary>
/// Feeds the display vsync grid (Choreographer) to <see cref="FrameClock"/>,
/// so the software present clock keeps a fixed phase to the screen. Without
/// it the 60 Hz timer drifts across the latch point and 30 FPS images stay
/// up 1 or 3 vblanks instead of 2 (#248). Runs on the UI thread while the
/// activity is resumed.
/// </summary>
sealed class AndroidVsync : Java.Lang.Object, Choreographer.IFrameCallback
{
    readonly Activity _activity;
    bool _running;
    int _framesUntilRefresh;
    double _periodMs = 1000.0 / 60.0;

    public AndroidVsync(Activity activity) => _activity = activity;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _framesUntilRefresh = 0;
        Choreographer.Instance?.PostFrameCallback(this);
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        Choreographer.Instance?.RemoveFrameCallback(this);
        FrameClock.ClearVsync();
    }

    public void DoFrame(long frameTimeNanos)
    {
        if (!_running) return;
        Choreographer.Instance?.PostFrameCallback(this);

        if (--_framesUntilRefresh <= 0)
        {
            _framesUntilRefresh = 60;
            try
            {
                var display = OperatingSystem.IsAndroidVersionAtLeast(30)
                    ? _activity.Display
                    : _activity.WindowManager?.DefaultDisplay;
                float hz = display?.RefreshRate ?? 0f;
                if (hz >= 20f) _periodMs = 1000.0 / hz;
            }
            catch
            {
                // keep the last period
            }
        }

        // frameTimeNanos is System.nanoTime (CLOCK_MONOTONIC); map it onto the
        // Stopwatch timebase through a fresh sample of both clocks.
        long nowJava = Java.Lang.JavaSystem.NanoTime();
        long nowTs = System.Diagnostics.Stopwatch.GetTimestamp();
        long agoTs = (long)((nowJava - frameTimeNanos) * (System.Diagnostics.Stopwatch.Frequency / 1e9));
        FrameClock.ReportVsync(nowTs - agoTs, _periodMs);
    }
}
