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
        FrameClock.SkipThrottle = view.VSync;
        ConfigManager.SaveView(Array.Empty<IPanel>());
    }
}
