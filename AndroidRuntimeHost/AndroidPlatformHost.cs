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

        var presented = backend.PresentDisplay(
            gpu.DisplayX, gpu.DisplayY, nativeWidth, nativeHeight, gpu.Display24Bit);
        backend.PresentToDefaultFramebuffer(egl.SurfaceWidth, egl.SurfaceHeight, presented.aspect);
        egl.SwapBuffers();

        _fpsFrames++;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsed = (now - _fpsWindow) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (elapsed >= 2.0)
        {
            Android.Util.Log.Info("CrashGPU", $"{_fpsFrames / elapsed:F1} FPS, surface {egl.SurfaceWidth}x{egl.SurfaceHeight}, " +
                                               $"present {presented.w}x{presented.h}, GL {backend.LastDiagnostic}");
            _fpsFrames = 0;
            _fpsWindow = now;
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
