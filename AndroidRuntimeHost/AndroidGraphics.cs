using RecompOne.Runtime.Config;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Host.Window;

namespace CrashBandicoot.AndroidRuntime;

/// <summary>Pushes ViewConfig graphics toggles into the live GLES path.</summary>
static class AndroidGraphics
{
    public static readonly string[] PresetLabels =
    [
        "Crisp Retro",
        "Smooth HD",
        "Widescreen HD",
    ];

    public static void ApplyPreset(int index)
    {
        var view = ConfigManager.View;
        switch (index)
        {
            case 0:
                view.Widescreen = false;
                view.TextureFilter = ViewConfig.TextureFilterOff;
                view.Dedither = false;
                view.Dejitter = false;
                view.IntegerScale = true;
                view.PresentNearest = true;
                break;
            case 1:
                view.Widescreen = false;
                view.TextureFilter = ViewConfig.TextureFilterSoftSmooth;
                view.TextureFilterStrength = 0.55f;
                view.Dedither = true;
                view.Dejitter = true;
                view.IntegerScale = false;
                view.PresentNearest = false;
                break;
            case 2:
                view.Widescreen = true;
                view.TextureFilter = ViewConfig.TextureFilterSharpBilinear;
                view.TextureFilterStrength = 0.5f;
                view.Dedither = true;
                view.Dejitter = true;
                view.IntegerScale = false;
                view.PresentNearest = false;
                break;
            default:
                return;
        }

        ApplyLive();
    }

    public static void ApplyLive()
    {
        var view = ConfigManager.View;
        GpuHle.WideAspect = view.Widescreen ? 16f / 9f : 0f;
        GpuHle.RefreshWideFov();
        GpuHle.TextureFilter = view.TextureFilter;
        GpuHle.TextureFilterStrength = view.TextureFilterStrength;
        GpuHle.Dedither = view.Dedither;
        GpuHle.Dejitter = view.Dejitter;
        GpuHle.PresentNearest = view.PresentNearest;
        GpuHle.IntegerScale = view.IntegerScale;
        GpuHle.NativeResolution = view.InternalResolution <= 1;
        ApplyFramePacing(reset: false);
        ConfigManager.SaveView(Array.Empty<IPanel>());
    }

    /// <summary>Window / surface must follow present-Hz changes (UI thread).</summary>
    public static Action<double>? PresentHzChanged;

    /// <summary>
    /// FrameRate drives delta-time unlock; the software present clock always
    /// stays on so SPU music cannot run away (EGL swap interval is 0).
    /// </summary>
    public static void ApplyFramePacing(bool reset = true)
    {
        int rate = ConfigManager.View.FrameRate;
        FrameClock.SkipThrottle = false;
        FramePacing.ForceOriginal = rate == ViewConfig.FrameRateOriginal;
        double hz = PresentHz(rate);
        if (Math.Abs(FrameClock.TargetHz - hz) > 0.5)
            FrameClock.TargetHz = hz;
        if (reset)
            FramePacing.Reset();
        AndroidEglContext.Current?.SetPresentHz(hz);
        PresentHzChanged?.Invoke(hz);
        Android.Util.Log.Info("CrashGPU",
            $"FrameRate={rate} ForceOriginal={FramePacing.ForceOriginal} " +
            $"TargetHz={FrameClock.TargetHz:0} WantsUnlock={FramePacing.WantsUnlock} " +
            $"{AndroidDisplayPacing.Describe()}");
    }

    public static double PresentHz(int rate) => rate switch
    {
        120 => 120,
        240 => 240,
        ViewConfig.FrameRateUncapped => 240,
        _ => 60,
    };
}
