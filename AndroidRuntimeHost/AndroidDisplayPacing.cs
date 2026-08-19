using Android.App;
using Android.Views;
using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Host;

namespace CrashBandicoot.AndroidRuntime;

/// <summary>
/// Many 90/120 Hz phones keep the app in a 60 Hz display mode unless the
/// window and the EGL surface both ask for a higher rate. Original/60 must
/// stay at 60 or the classic pad runs 2×.
/// </summary>
static class AndroidDisplayPacing
{
    public static void ApplyWindow(Activity activity, double targetHz)
    {
        if (activity.Window == null) return;
        var want = (float)Math.Clamp(targetHz, 30, 240);
        var lp = activity.Window.Attributes;
        lp.PreferredRefreshRate = want;

        var display = OperatingSystem.IsAndroidVersionAtLeast(30)
            ? activity.Display
            : activity.WindowManager?.DefaultDisplay;
        var modes = display?.GetSupportedModes();
        if (modes is { Length: > 0 } && PickMode(modes, want) is { } mode)
        {
            lp.PreferredDisplayModeId = mode.ModeId;
            Android.Util.Log.Info("CrashGPU",
                $"display mode {mode.ModeId} {mode.PhysicalWidth}x{mode.PhysicalHeight} @{mode.RefreshRate:0.#} Hz (want {want:0.#})");
        }
        else
            Android.Util.Log.Info("CrashGPU", $"display preferredRefreshRate={want:0.#}");

        activity.Window.Attributes = lp;
    }

    public static void ApplySurface(Surface surface, double targetHz)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30)) return;
        try
        {
            var hz = (float)Math.Clamp(targetHz, 30, 240);
            var compat = (int)SurfaceFrameRateCompatibility.Default;
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
                surface.SetFrameRate(hz, compat, (int)SurfaceChangeFrameRate.Always);
            else
                surface.SetFrameRate(hz, compat);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("CrashGPU", $"Surface.setFrameRate failed: {ex.Message}");
        }
    }

    public static string Describe()
    {
        var rate = ConfigManager.View.FrameRate;
        var setting = rate == ViewConfig.FrameRateOriginal ? "original 30"
            : rate == ViewConfig.FrameRateUncapped ? "uncapped"
            : $"{rate} dt";
        var where = Place();
        if (FramePacing.NeedsOriginalVblank)
            return $"{setting} · lock 60 · {where}";
        if (FramePacing.IsActive(RecompOne.Runtime.Runtime.Mem))
            return $"{setting} · dt ON · {where}";
        if (FramePacing.WantsUnlock)
            return $"{setting} · waiting · {where}";
        return $"{setting} · {where}";
    }

    static string Place()
    {
        try
        {
            if (Catalog.Levels.TryGetCurrent(out var info))
                return info.Name;
            if (Catalog.Levels.TryReadCurrentId(out var id))
                return $"id {id}";
        }
        catch
        {
            // guest RAM not mapped yet
        }
        return "—";
    }

    static Display.Mode? PickMode(Display.Mode[] modes, float want)
    {
        Display.Mode? best = null;
        if (want <= 60.5f)
        {
            foreach (var mode in modes)
            {
                var hz = mode.RefreshRate;
                if (hz < 30f || hz > 61.5f) continue;
                if (best == null || Math.Abs(hz - want) < Math.Abs(best.RefreshRate - want))
                    best = mode;
            }
            if (best != null) return best;
        }
        else
        {
            foreach (var mode in modes)
            {
                if (mode.RefreshRate + 0.5f < want) continue;
                if (best == null || mode.RefreshRate < best.RefreshRate)
                    best = mode;
            }
            if (best != null) return best;
        }

        foreach (var mode in modes)
        {
            if (best == null || mode.RefreshRate > best.RefreshRate)
                best = mode;
        }
        return best;
    }
}
