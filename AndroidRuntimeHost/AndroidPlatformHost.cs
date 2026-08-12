using Android.App;
using Android.Widget;
using RecompOne.Runtime;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Host.Cheats;

namespace CrashBandicoot.AndroidRuntime;

sealed class AndroidPlatformHost(
    Activity activity,
    TextView status,
    ProgressBar progress,
    AndroidEglContext egl,
    GlBackend backend,
    GameGpuDiagnosticsSession diagnostics) : IRuntimePlatformHost
{
    readonly AndroidAudioOutput _audio = new();
    bool _firstFrame = true;
    long _fpsWindow = System.Diagnostics.Stopwatch.GetTimestamp();
    int _fpsFrames;
    double _prepareMilliseconds;
    double _surfaceMilliseconds;
    double _swapMilliseconds;
    long _flushes;
    long _writebacks;
    long _vertices;
    long _lastPresentTimestamp;
    TextView? _hud;
    long _hudWindow = System.Diagnostics.Stopwatch.GetTimestamp();
    int _hudFrames;
    double _hudFps;

    public void Initialize(string title) => SetStatus($"{title}: first frame incoming…");
    public void WaitForValidDisc() { }
    public void AttachAudio(Spu? spu) => _audio.Attach(spu);
    public void SetMasterVolume(float volume) => _audio.SetMasterVolume(volume);
    public void AttachHud(TextView? hud) => _hud = hud;
    public void ShowNotice(string message) => SetStatus(message);
    public void PauseAudio()
    {
        RecompOne.Runtime.Host.FrameClock.PauseTiming();
        _audio.PauseOutput();
    }
    public void ResumeAudio()
    {
        _audio.ResumeOutput();
        RecompOne.Runtime.Host.FrameClock.ResumeTiming();
    }
    public void NotifySurfaceSize(int width, int height) => egl.SetExpectedSize(width, height);

    public void Present(Gpu? gpu)
    {
        CheatManager.Apply();
        TickHud();

        if (gpu == null || !gpu.DisplayEnabled || !backend.Ready)
            return;

        var nativeWidth = gpu.DisplayWidth;
        var nativeHeight = gpu.DisplayHeight;
        if (nativeWidth <= 0 || nativeHeight <= 0)
            return;

        // Queried per frame: the EGL window surface can be recreated with a
        // new size after the TextureView is relaid out (immersive mode, …).
        var surfaceWidth = egl.SurfaceWidth;
        var surfaceHeight = egl.SurfaceHeight;
        var phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var frameIntervalMilliseconds = _lastPresentTimestamp == 0
            ? 0
            : (phaseStart - _lastPresentTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        _lastPresentTimestamp = phaseStart;
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
        diagnostics.RecordFrame(
            frameIntervalMilliseconds,
            (prepared - phaseStart) * ticksToMilliseconds,
            (composited - prepared) * ticksToMilliseconds,
            (swapped - composited) * ticksToMilliseconds,
            backend.LastFrameFlushes,
            backend.LastFrameWritebacks,
            backend.LastFrameVertices);

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
            status.Text = $"Game running • {presented.w}×{presented.h} ({GlVram.Scale}x native)";
            status.Visibility = Android.Views.ViewStates.Gone;
            progress.Visibility = Android.Views.ViewStates.Gone;
        });
    }

    public void Shutdown()
    {
        RecompOne.Runtime.Host.FrameClock.ResumeTiming();
        _audio.Dispose();
        RecompOne.Runtime.Hardware.Controller.SetVirtualPadState(0);
        activity.RunOnUiThread(() =>
        {
            status.Text = "Session ended.";
            status.Visibility = Android.Views.ViewStates.Visible;
        });
    }

    void TickHud()
    {
        if (_hud == null || !ConfigManager.View.ShowDevHud)
            return;

        _hudFrames++;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsed = (now - _hudWindow) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (elapsed < 0.5)
            return;

        _hudFps = _hudFrames / Math.Max(elapsed, 0.001);
        _hudFrames = 0;
        _hudWindow = now;
        var fps = _hudFps;
        long workingSet = 0;
        try { workingSet = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64; }
        catch { /* HUD is best-effort */ }

        var text = $"{fps:0.0} fps\n{FormatBytes(workingSet)}";
        activity.RunOnUiThread(() =>
        {
            if (_hud == null) return;
            _hud.Text = text;
        });
    }

    static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.0} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.00} GB";
    }

    void SetStatus(string text) => activity.RunOnUiThread(() =>
    {
        status.Text = text;
        status.Visibility = Android.Views.ViewStates.Visible;
    });

}
