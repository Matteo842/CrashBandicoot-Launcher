using Android.App;
using Android.Widget;
using RecompOne.Runtime;
using RecompOne.Runtime.Hle;

namespace CrashBandicoot.AndroidRuntime;

sealed class AndroidPlatformHost(
    Activity activity,
    TextView status,
    ProgressBar progress,
    AndroidEglContext egl,
    GlBackend backend) : IRuntimePlatformHost
{
    bool _firstFrame = true;
    long _fpsWindow = System.Diagnostics.Stopwatch.GetTimestamp();
    int _fpsFrames;
    double _prepareMilliseconds;
    double _surfaceMilliseconds;
    double _swapMilliseconds;
    long _flushes;
    long _writebacks;
    long _vertices;

    public void Initialize(string title) => SetStatus($"{title}: primo frame in arrivo…");
    public void WaitForValidDisc() { }
    public void AttachAudio(Spu? spu) { }
    public void SetMasterVolume(float volume) { }
    public void ShowNotice(string message) => SetStatus(message);

    public void Present(Gpu? gpu)
    {
        if (gpu == null || !gpu.DisplayEnabled || !backend.Ready)
            return;

        var nativeWidth = gpu.DisplayWidth;
        var nativeHeight = gpu.DisplayHeight;
        if (nativeWidth <= 0 || nativeHeight <= 0)
            return;

        var surfaceWidth = egl.SurfaceWidth;
        var surfaceHeight = egl.SurfaceHeight;
        var phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var presented = backend.PresentDisplay(
            gpu.DisplayX, gpu.DisplayY, nativeWidth, nativeHeight, gpu.Display24Bit,
            surfaceWidth, surfaceHeight);
        var prepared = System.Diagnostics.Stopwatch.GetTimestamp();
        backend.PresentToDefaultFramebuffer(surfaceWidth, surfaceHeight, presented.aspect);
        var composited = System.Diagnostics.Stopwatch.GetTimestamp();
        egl.SwapBuffers();
        var swapped = System.Diagnostics.Stopwatch.GetTimestamp();

        double ticksToMilliseconds = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        _prepareMilliseconds += (prepared - phaseStart) * ticksToMilliseconds;
        _surfaceMilliseconds += (composited - prepared) * ticksToMilliseconds;
        _swapMilliseconds += (swapped - composited) * ticksToMilliseconds;
        _flushes += backend.LastFrameFlushes;
        _writebacks += backend.LastFrameWritebacks;
        _vertices += backend.LastFrameVertices;

        _fpsFrames++;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsed = (now - _fpsWindow) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (elapsed >= 2.0)
        {
            var frames = Math.Max(1, _fpsFrames);
            Android.Util.Log.Info("CrashGPU", $"{_fpsFrames / elapsed:F1} FPS, surface {surfaceWidth}x{surfaceHeight}, " +
                                               $"present {presented.w}x{presented.h}, CPU submit " +
                                               $"{_prepareMilliseconds / frames:F2}+{_surfaceMilliseconds / frames:F2} ms, " +
                                               $"swap {_swapMilliseconds / frames:F2} ms, " +
                                               $"batches {_flushes / (double)frames:F1}, writes {_writebacks / (double)frames:F1}, " +
                                               $"verts {_vertices / (double)frames:F0}, GL {backend.LastDiagnostic}");
            _fpsFrames = 0;
            _fpsWindow = now;
            _prepareMilliseconds = 0;
            _surfaceMilliseconds = 0;
            _swapMilliseconds = 0;
            _flushes = 0;
            _writebacks = 0;
            _vertices = 0;
        }

        if (!_firstFrame) return;
        _firstFrame = false;
        activity.RunOnUiThread(() =>
        {
            status.Text = $"Gioco in esecuzione • {presented.w}×{presented.h} ({GlVram.Scale}x reale)";
            status.Visibility = Android.Views.ViewStates.Gone;
            progress.Visibility = Android.Views.ViewStates.Gone;
        });
    }

    public void Shutdown()
    {
        RecompOne.Runtime.Hardware.Controller.SetVirtualPadState(0);
        activity.RunOnUiThread(() =>
        {
            status.Text = "Sessione terminata.";
            status.Visibility = Android.Views.ViewStates.Visible;
        });
    }

    void SetStatus(string text) => activity.RunOnUiThread(() =>
    {
        status.Text = text;
        status.Visibility = Android.Views.ViewStates.Visible;
    });

}
