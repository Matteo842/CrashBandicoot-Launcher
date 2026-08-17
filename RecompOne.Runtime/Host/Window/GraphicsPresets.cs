using RecompOne.Runtime.Config;

namespace RecompOne.Runtime.Host.Window;

/// <summary>One-click graphics bundles players actually feel.</summary>
internal static class GraphicsPresets
{
    public static readonly string[] Labels =
    [
        "Crisp Retro",
        "Smooth HD",
        "Widescreen HD",
    ];

    public static void Apply(int index)
    {
        var view = ConfigManager.View;
        switch (index)
        {
            case 0: // Crisp Retro — native look, sharp pixels
                view.Widescreen = false;
                view.TextureFilter = ViewConfig.TextureFilterOff;
                view.Dedither = false;
                view.Dejitter = false;
                view.IntegerScale = true;
                view.PresentNearest = true;
                break;

            case 1: // Smooth HD — filters on, 4:3
                view.Widescreen = false;
                view.TextureFilter = ViewConfig.TextureFilterSoftSmooth;
                view.TextureFilterStrength = 0.55f;
                view.Dedither = true;
                view.Dejitter = true;
                view.IntegerScale = false;
                view.PresentNearest = false;
                break;

            case 2: // Widescreen HD — 16:9 + smooth
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

        ApplyLive(view);
        ConfigManager.SaveView(PanelManager.Panels);
    }

    /// <summary>Push ViewConfig graphics toggles into the live GPU / host path.</summary>
    public static void ApplyLive(ViewConfig view)
    {
        HostWindow.ApplyWidescreen(view.Widescreen);
        Hle.GpuHle.TextureFilter = view.TextureFilter;
        Hle.GpuHle.TextureFilterStrength = view.TextureFilterStrength;
        Hle.GpuHle.Dedither = view.Dedither;
        Hle.GpuHle.Dejitter = view.Dejitter;
        Hle.GpuHle.PresentNearest = view.PresentNearest;
        HostWindow.ApplyFramePacing();
    }
}
