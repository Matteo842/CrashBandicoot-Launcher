using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Config;

namespace RecompOne.Runtime.Host;

/// <summary>
/// Silent first-run graphics setup (<see cref="ViewConfig.AutoTune"/>). Measures real
/// gameplay frames (levels and the attract demo, never menus or the map), then picks
/// widescreen, frame rate and internal resolution with no message. Order of priority:
/// 60 FPS, then widescreen, then the highest resolution that holds 60, then 120 / 240
/// up to the display refresh rate.
/// Frame rate and widescreen apply at the next level change. Resolution needs a
/// restart (VRAM is allocated at init), so it is saved for the next start and checked
/// again then (stage Verify). Stops for good as soon as the player changes one of
/// these three settings.
/// </summary>
public static class AutoTune
{
    public const int StageMeasure = 0;
    public const int StageVerify = 1;
    public const int StageDone = 2;

    /// <summary>Host: push ViewConfig to the live renderer + frame pacing and save it (game thread).</summary>
    public static Action? ApplyLive;

    /// <summary>Host: save ViewConfig only (game thread).</summary>
    public static Action? SaveConfig;

    /// <summary>Highest display refresh rate, 0 = unknown (then 60 is the ceiling).</summary>
    public static double DisplayMaxHz;

    /// <summary>Highest internal resolution the host may pick (Android 4, desktop 8).</summary>
    public static int MaxResolution = 8;

    const int SamplesNeeded = 1800;      // ~30 s of gameplay at 60 FPS
    const double SettleMs = 3000;        // ignore loading / fade after a level change
    const double HitchMs = 250;          // loading stalls, not render cost
    const double Headroom = 0.85;        // p90 frame work must fit 85% of the budget
    const double ResUpHeadroom = 0.70;   // stricter: next scale is only estimated

    static bool _active;
    static bool _started;
    static bool _pendingApply;
    static int _stage;
    static uint _levelId = uint.MaxValue;
    static double _settleLeftMs;
    static readonly float[] _busy = new float[SamplesNeeded];
    static readonly float[] _busyNoWide = new float[SamplesNeeded];
    static readonly float[] _host = new float[SamplesNeeded];
    static readonly float[] _frame = new float[SamplesNeeded];
    static bool _locked;
    static int _count;
    static bool _wideMeasured;
    static int _fpsMeasured;
    static int _resMeasured;
    static bool _pickWide;
    static int _pickFps;
    static int _pickRes;
    static int _pickStage;

    /// <summary>Called by <see cref="Diagnostics.FrameTimer"/> once per presented frame.</summary>
    internal static void OnFrame(double frameMs, double gameMs, double waitMs)
    {
        if (!_started) Start();
        if (!_active) return;

        bool levelChanged = TrackLevel(out bool gameplay);
        if (levelChanged && _pendingApply)
            Apply();
        if (_pendingApply || _count >= SamplesNeeded) return;

        if (levelChanged) { _settleLeftMs = SettleMs; return; }
        if (_settleLeftMs > 0) { _settleLeftMs -= frameMs; return; }
        // Pause menu frames run no game code.
        if (!gameplay || gameMs <= 0.05 || frameMs > HitchMs) return;

        if (_count % 60 == 0 && UserChanged()) { Finish("settings changed by the player"); return; }

        // Vblank-locked present: its wait is not GPU work, so only game time counts.
        double busy = _locked ? gameMs : Math.Max(0, frameMs - waitMs);
        double wide = Hle.GpuHle.WideAspect > 0 ? FramePacing.LastNativeWideCpuMs : 0;
        _busy[_count] = (float)busy;
        _busyNoWide[_count] = (float)Math.Max(0, busy - wide);
        _host[_count] = (float)Math.Max(0, busy - gameMs);
        _frame[_count] = (float)frameMs;
        if (++_count == SamplesNeeded)
            Decide();
    }

    static void Start()
    {
        _started = true;
        var view = ConfigManager.View;
        _stage = view.AutoTuneStage;
        if (!view.AutoTune || _stage >= StageDone) return;
        if (UserChanged())
        {
            Finish("settings already chosen by the player");
            return;
        }
        _wideMeasured = view.Widescreen;
        _fpsMeasured = view.FrameRate;
        _resMeasured = view.InternalResolution;
        _locked = view.VSync || (Hle.GpuHle.Backend is Hle.Vulkan.VkBackend && Hle.Vulkan.VkSwapchain.FifoActive);
        _active = true;
        Console.WriteLine($"[AutoTune] stage {_stage}: measuring at {_resMeasured}x, " +
            $"widescreen {(_wideMeasured ? "on" : "off")}, FrameRate {_fpsMeasured}, display {DisplayMaxHz:0} Hz" +
            (_locked ? ", vblank-locked present" : ""));
    }

    /// <summary>The player moved one of the tuned settings away from what AutoTune last set.</summary>
    static bool UserChanged()
    {
        var view = ConfigManager.View;
        return view.Widescreen != view.AutoTuneWide
            || view.FrameRate != view.AutoTuneFps
            || view.InternalResolution != view.AutoTuneRes;
    }

    static bool TrackLevel(out bool gameplay)
    {
        gameplay = false;
        if (!Catalog.Levels.TryReadCurrentId(out uint id)) return false;
        gameplay = Catalog.Levels.TryGet(id, out var info) && info.Kind is LevelKind.Gameplay or LevelKind.Bonus;
        if (id == _levelId) return false;
        bool first = _levelId == uint.MaxValue;
        _levelId = id;
        return !first;
    }

    static double Budget(int fps) => 1000.0 / fps * Headroom;

    static double TargetHz(int frameRate) => frameRate switch
    {
        ViewConfig.FrameRateOriginal => 30,
        ViewConfig.FrameRatePalSpeed => 25,
        ViewConfig.FrameRateUncapped => 240,
        _ => frameRate,
    };

    static double P90(float[] v)
    {
        var s = (float[])v.Clone();
        Array.Sort(s);
        return s[(int)(s.Length * 0.9)];
    }

    static void Decide()
    {
        double busy = P90(_busy);
        double busyNoWide = P90(_busyNoWide);
        double host = P90(_host);
        double frame = P90(_frame);
        int res = _resMeasured;
        bool wide = _wideMeasured;
        int fps;
        int stage = StageDone;

        // Locked present: the GPU only shows as frames missing the measured rate.
        bool missed = _locked && frame > 1000.0 / TargetHz(_fpsMeasured) * 1.25;
        bool gpuBound = missed || (!_locked && host > busy - host);

        Console.WriteLine($"[AutoTune] p90 work {busy:0.00} ms, without 16:9 sides {busyNoWide:0.00} ms, " +
            $"present {host:0.00} ms, frame {frame:0.00} ms{(missed ? " (missed rate)" : "")}");

        if (busy > Budget(60) || missed)
        {
            if (_stage == StageVerify)
            {
                // The higher resolution tried this run cannot hold 60: go back, keep the rest.
                // Frame rate and 16:9 were measured at that lower scale last run.
                Save(wide, _fpsMeasured, Math.Max(1, res / 2), StageDone);
                return;
            }
            if (gpuBound && res > 1)
            {
                // GPU side: a lower scale helps, 16:9 stays. Measured again next start.
                Save(wide, 60, res / 2, StageMeasure);
                return;
            }
            if (wide && !missed && busyNoWide <= Budget(60))
            {
                // CPU side (phones): the 16:9 side scenery is what a lower scale cannot fix.
                wide = false;
                busy = busyNoWide;
            }
            else if (res > 1)
            {
                Save(wide, 60, res / 2, StageMeasure);
                return;
            }
            else
            {
                // Even 1x misses 60: original 30 FPS, 16:9 only if it holds 30.
                Save(wide && busy <= Budget(30), ViewConfig.FrameRateOriginal, res, StageDone);
                return;
            }
        }

        double ceilingHz = DisplayMaxHz > 0 ? DisplayMaxHz + 1 : 61;
        fps = 60;
        foreach (int f in new[] { 240, 120 })
        {
            if (f <= ceilingHz && busy <= Budget(f)) { fps = f; break; }
        }

        // Next scale ≈ 4x the pixels: estimate with the present / GPU side scaled up.
        // Locked present: GPU time unknown, so the scale never goes up blind.
        if (!_locked && res * 2 <= MaxResolution && busy - host + host * 4 <= 1000.0 / 60 * ResUpHeadroom)
        {
            res *= 2;
            stage = StageVerify;
        }
        Save(wide, fps, res, stage);
    }

    /// <summary>Holds the pick until the next level change so nothing switches mid-level.</summary>
    static void Save(bool wide, int fps, int res, int stage)
    {
        _pickWide = wide;
        _pickFps = ViewConfig.SnapFrameRate(fps);
        _pickRes = ViewConfig.SnapInternalResolution(res);
        _pickStage = stage;
        _pendingApply = true;
        Console.WriteLine($"[AutoTune] picked widescreen {(wide ? "on" : "off")}, FrameRate {_pickFps}, " +
            $"{_pickRes}x (next start), stage {stage}; applying at the next level change");
    }

    static void Apply()
    {
        _pendingApply = false;
        _active = false;
        if (UserChanged()) { Finish("settings changed by the player"); return; }

        var view = ConfigManager.View;
        bool live = view.Widescreen != _pickWide || view.FrameRate != _pickFps;
        view.Widescreen = _pickWide;
        view.FrameRate = _pickFps;
        view.InternalResolution = _pickRes;
        view.AutoTuneWide = view.Widescreen;
        view.AutoTuneFps = view.FrameRate;
        view.AutoTuneRes = view.InternalResolution;
        view.AutoTuneStage = _pickStage;
        if (live) ApplyLive?.Invoke();
        SaveConfig?.Invoke();
        Console.WriteLine("[AutoTune] applied");
    }

    static void Finish(string why)
    {
        var view = ConfigManager.View;
        view.AutoTuneStage = StageDone;
        _active = false;
        _pendingApply = false;
        SaveConfig?.Invoke();
        Console.WriteLine($"[AutoTune] off: {why}");
    }
}
