using Android.App;
using Android.Widget;
using RecompOne.Runtime;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Host.Cheats;

using FrameTimer = RecompOne.Runtime.Host.Diagnostics.FrameTimer;

namespace CrashBandicoot.AndroidRuntime;

sealed class AndroidPlatformHost(
    Activity activity,
    TextView status,
    ProgressBar progress,
    IAndroidVideo video,
    GameGpuDiagnosticsSession? diagnostics) : IRuntimePlatformHost
{
    readonly IGpuBackend backend = video.Backend;
    readonly AndroidAudioOutput _audio = new();
    bool _firstFrame = true;
    long _fpsWindow = System.Diagnostics.Stopwatch.GetTimestamp();
    int _fpsFrames;
    long _logDrawn = -1;
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
    long _hudDrawn = -1;
    double _hudGameFps;
    TextView? _boxHud;
    string? _boxText;
    int _boxTone = -1;
    TextView? _gemHud;
    RecompOne.Runtime.Host.GemHints.Card? _gemCard;
    string? _gemHtml;

    public static double LastFps { get; private set; }

    public void Initialize(string title) => SetStatus($"{title}: first frame incoming…");
    public void WaitForValidDisc() { }
    public void AttachAudio(Spu? spu) => _audio.Attach(spu);
    public void SetMasterVolume(float volume) => _audio.SetMasterVolume(volume);
    public void AttachHud(TextView? hud) => _hud = hud;
    public void AttachBoxHud(TextView? hud) => _boxHud = hud;
    public void AttachGemHud(TextView? hud) => _gemHud = hud;
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
    public void NotifySurfaceSize(int width, int height) => video.SetExpectedSize(width, height);

    public void Present(Gpu? gpu)
    {
        CheatManager.Apply();
        Runtime.RamLog.Tick();
        TickHud();
        TickBoxHud();
        TickGemHud();

        if (gpu == null || !gpu.DisplayEnabled || !backend.Ready)
            return;

        var nativeWidth = gpu.DisplayWidth;
        var nativeHeight = gpu.DisplayHeight;
        if (nativeWidth <= 0 || nativeHeight <= 0)
            return;

        // Queried per frame: the EGL window surface can be recreated with a
        // new size after the TextureView is relaid out (immersive mode, …).
        var surfaceWidth = video.SurfaceWidth;
        var surfaceHeight = video.SurfaceHeight;
        var phaseStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var frameIntervalMilliseconds = _lastPresentTimestamp == 0
            ? 0
            : (phaseStart - _lastPresentTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        _lastPresentTimestamp = phaseStart;
        if (frameIntervalMilliseconds > 200)
            Android.Util.Log.Warn("CrashGPU",
                $"frame hitch {frameIntervalMilliseconds:F0} ms, wideCpu {RecompOne.Runtime.Host.FramePacing.LastNativeWideCpuMs:F1} ms");
        var presented = video.Prepare(
            gpu.DisplayX, gpu.DisplayY, nativeWidth, nativeHeight, gpu.Display24Bit,
            surfaceWidth, surfaceHeight);
        AndroidVramCapture.CaptureFromGlThread(backend);
        var prepared = System.Diagnostics.Stopwatch.GetTimestamp();
        video.Composite(surfaceWidth, surfaceHeight, presented.aspect);
        var composited = System.Diagnostics.Stopwatch.GetTimestamp();
        video.Swap();
        var swapped = System.Diagnostics.Stopwatch.GetTimestamp();

        double ticksToMilliseconds = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        _prepareMilliseconds += (prepared - phaseStart) * ticksToMilliseconds;
        _surfaceMilliseconds += (composited - prepared) * ticksToMilliseconds;
        _swapMilliseconds += (swapped - composited) * ticksToMilliseconds;
        _flushes += backend.LastFrameFlushes;
        _writebacks += backend.LastFrameWritebacks;
        _vertices += backend.LastFrameVertices;
        diagnostics?.RecordFrame(
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
            long drawnNow = backend.DrawnFrames;
            double drawnPerSec = _logDrawn >= 0 ? (drawnNow - _logDrawn) / elapsed : 0;
            _logDrawn = drawnNow;
            Android.Util.Log.Info("CrashGPU", $"{_fpsFrames / elapsed:F1} FPS ({drawnPerSec:F1} drawn), surface {surfaceWidth}x{surfaceHeight}, " +
                                               $"present {presented.w}x{presented.h}, CPU submit " +
                                               $"{_prepareMilliseconds / frames:F2}+{_surfaceMilliseconds / frames:F2} ms, " +
                                               $"swap {_swapMilliseconds / frames:F2} ms, " +
                                               $"wideCpu {RecompOne.Runtime.Host.FramePacing.LastNativeWideCpuMs:F1} ms " +
                                               $"(peak {RecompOne.Runtime.Host.FramePacing.ConsumeNativeWideCpuPeakMs():F1}), " +
                                               $"vsync phase {RecompOne.Runtime.Host.FrameClock.PhaseErrorMs:+0.0;-0.0} ms, " +
                                               $"scale {GlVram.Scale}x, " +
                                               $"batches {_flushes / (double)frames:F1}, writes {_writebacks / (double)frames:F1}, " +
                                               $"verts {_vertices / (double)frames:F0}, {video.Name} {backend.LastDiagnostic}");
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
            status.Text = $"Game running • {presented.w}×{presented.h} ({GlVram.Scale}x native, {video.Name})";
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
        {
            _hudDrawn = -1; // HUD off: no stale count when it comes back
            return;
        }

        _hudFrames++;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsed = (now - _hudWindow) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (elapsed < 0.5)
            return;

        _hudFps = _hudFrames / Math.Max(elapsed, 0.001);
        LastFps = _hudFps;
        _hudFrames = 0;
        _hudWindow = now;
        // #192: presents run at the display rate (30 FPS mode shows each image
        // twice or more), so the plain counter shows new images/s instead;
        // kept while nothing new is drawn (game frozen in pause).
        long drawn = backend.DrawnFrames;
        if (_hudDrawn >= 0)
        {
            double drawnFps = (drawn - _hudDrawn) / Math.Max(elapsed, 0.001);
            if (drawnFps >= 0.5) _hudGameFps = drawnFps;
        }
        _hudDrawn = drawn;
        var fps = _hudFps;
        string text;
        if (!ConfigManager.View.DevHudDetails)
            text = $"{(_hudGameFps > 0 ? _hudGameFps : fps):0} FPS";
        else
        {
            long workingSet = 0;
            try { workingSet = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64; }
            catch { /* HUD is best-effort */ }
            text = DetailedHud(fps, _hudGameFps > 0 ? _hudGameFps : fps, workingSet);
        }
        activity.RunOnUiThread(() =>
        {
            if (_hud == null) return;
            _hud.Text = text;
        });
    }

    /// <summary>
    /// Detailed HUD: the desktop numbers (frame phases, peaks, batches) plus
    /// who limits the frame rate, so a CPU limit can be told from a GPU one.
    /// On Android "game" = recompiled game + PS1 GPU commands + 16:9 sides,
    /// "present" = final pass + submit/swap (GPU back-pressure shows here),
    /// "wait" = idle time left before the next present.
    /// </summary>
    string DetailedHud(double presentsFps, double drawnFps, long workingSet)
    {
        double frame = FrameTimer.AverageFrameMs;
        double game = FrameTimer.Average(FrameTimer.Phase.Game);
        double swap = FrameTimer.Average(FrameTimer.Phase.Swap);
        double wait = FrameTimer.Average(FrameTimer.Phase.Wait);
        double wide = RecompOne.Runtime.Host.FramePacing.LastNativeWideCpuMs;
        int gpuBusy = ReadGpuBusyPercent();

        // Limit: on time with idle left = the cap; else whichever side ate the frame.
        // Menus / original 30 run on the 60 Hz vblank whatever the cap (FrameClock.Throttle).
        double periodMs = RecompOne.Runtime.Host.FramePacing.NeedsOriginalVblank
            ? 1000.0 / 60.0
            : 1000.0 / Math.Max(1.0, RecompOne.Runtime.Host.FrameClock.TargetHz);
        bool onTime = frame <= periodMs * 1.03;
        string limit = onTime ? "cap (spare time)"
            : gpuBusy >= 90 || swap > game ? "GPU"
            : "CPU";

        var sb = new System.Text.StringBuilder(320);
        sb.Append($"{presentsFps:0.0} fps · {drawnFps:0.0} drawn/s\n");
        sb.Append($"limit: {limit}\n");
        sb.Append($"frame {frame,6:0.00} ms   worst {FrameTimer.WorstFrameMs,6:0.00}\n");
        sb.Append($"peak 5s {FrameTimer.PeakFrameMs,6:0.00}   jit {FrameTimer.PeakJitMs:0.0}  gc {FrameTimer.PeakGcMs:0.0}\n");
        sb.Append($"game {game,6:0.00}   16:9 sides {wide,5:0.0}\n");
        sb.Append($"present {swap,6:0.00}   wait {wait,6:0.00}\n");
        sb.Append(gpuBusy >= 0 ? $"GPU busy {gpuBusy}%\n" : "GPU busy n/a\n");
        sb.Append($"{video.Name} {GlVram.Scale}x · batches {backend.LastFrameFlushes} · verts {backend.LastFrameVertices}\n");
        sb.Append($"cpu reads {backend.LastFrameCpuReads} · rt copies {backend.LastFrameWritebacks}\n");
        sb.Append($"{AndroidDisplayPacing.Describe()}\n");
        sb.Append($"jit warmup {RecompOne.Runtime.Host.JitWarmup.Status} · {FormatBytes(workingSet)}");
        return sb.ToString();
    }

    static string? _gpuBusyPath;
    static bool _gpuBusyMissing;

    /// <summary>GPU load in %, -1 when the driver does not expose it to apps (Adreno kgsl, Mali utilisation).</summary>
    static int ReadGpuBusyPercent()
    {
        if (_gpuBusyMissing) return -1;
        try
        {
            if (_gpuBusyPath == null)
            {
                foreach (var path in new[]
                {
                    "/sys/class/kgsl/kgsl-3d0/gpu_busy_percentage",
                    "/sys/kernel/gpu/gpu_busy",
                    "/sys/class/misc/mali0/device/utilization",
                })
                    if (File.Exists(path)) { _gpuBusyPath = path; break; }
                if (_gpuBusyPath == null) { _gpuBusyMissing = true; return -1; }
            }
            var raw = File.ReadAllText(_gpuBusyPath).Trim().TrimEnd('%').Trim();
            int space = raw.IndexOf(' ');
            if (space > 0) raw = raw[..space];
            return int.TryParse(raw, out int v) ? Math.Clamp(v, 0, 100) : -1;
        }
        catch
        {
            _gpuBusyMissing = true; // SELinux denies the node: stop trying
            return -1;
        }
    }

    /// <summary>Box counter (#131); the view is touched only when the text or color changes.</summary>
    void TickBoxHud()
    {
        if (_boxHud == null) return;
        string? text = null;
        int tone = 0;
        if (RecompOne.Runtime.Host.BoxCounter.TryRead(out var r))
        {
            text = $"{r.Broken}/{r.Total}";
            tone = r.Lost ? 2 : r.Complete ? 1 : 0;
        }
        if (text == _boxText && tone == _boxTone) return;
        _boxText = text;
        _boxTone = tone;

        activity.RunOnUiThread(() =>
        {
            var view = _boxHud;
            if (view == null) return;
            if (text == null)
            {
                view.Visibility = Android.Views.ViewStates.Gone;
                return;
            }
            float density = view.Resources?.DisplayMetrics?.Density ?? 1f;
            int size = (int)(22 * density);
            var crate = new Android.Graphics.Drawables.GradientDrawable();
            crate.SetColor(tone == 2 ? Android.Graphics.Color.Argb(120, 196, 112, 36) : Android.Graphics.Color.Rgb(196, 112, 36));
            crate.SetStroke(Math.Max(2, (int)(3 * density)), tone == 2 ? Android.Graphics.Color.Argb(120, 58, 32, 8) : Android.Graphics.Color.Rgb(58, 32, 8));
            crate.SetSize(size, size);
            view.SetCompoundDrawablesWithIntrinsicBounds(crate, null, null, null);
            view.SetTextColor(tone switch
            {
                1 => Android.Graphics.Color.Rgb(150, 215, 255),
                2 => Android.Graphics.Color.Rgb(150, 144, 132),
                _ => Android.Graphics.Color.Rgb(255, 176, 32),
            });
            view.Text = text;
            view.Visibility = Android.Views.ViewStates.Visible;
        });
    }

    /// <summary>Gem hints (#131) card; the view is touched only when the text changes.</summary>
    void TickGemHud()
    {
        if (_gemHud == null) return;
        RecompOne.Runtime.Host.GemHints.TryGet(out var card);
        if (ReferenceEquals(card, _gemCard)) return;
        _gemCard = card;
        string? html = card == null ? null : GemHtml(card);
        if (html == _gemHtml) return;
        _gemHtml = html;

        activity.RunOnUiThread(() =>
        {
            var view = _gemHud;
            if (view == null) return;
            if (html == null)
            {
                view.Visibility = Android.Views.ViewStates.Gone;
                return;
            }
            view.TextFormatted = Android.Text.Html.FromHtml(html, Android.Text.FromHtmlOptions.ModeLegacy);
            view.Visibility = Android.Views.ViewStates.Visible;
        });
    }

    static string GemHtml(RecompOne.Runtime.Host.GemHints.Card card)
    {
        static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s);
        static string ToneHex(RecompOne.Runtime.Host.GemHints.Tone tone) => tone switch
        {
            RecompOne.Runtime.Host.GemHints.Tone.Good => "#8CE178",
            RecompOne.Runtime.Host.GemHints.Tone.Bad => "#FF8269",
            _ => "#F4E4BC",
        };
        string gem = card.Icon switch
        {
            RecompOne.Runtime.Host.GemHints.Gem.Red => "#FF463C",
            RecompOne.Runtime.Host.GemHints.Gem.Blue => "#468CFF",
            RecompOne.Runtime.Host.GemHints.Gem.Green => "#46D25A",
            RecompOne.Runtime.Host.GemHints.Gem.Purple => "#B45FFF",
            RecompOne.Runtime.Host.GemHints.Gem.Yellow => "#FFE13C",
            RecompOne.Runtime.Host.GemHints.Gem.Orange => "#FF961E",
            _ => "#AAD7FF",
        };
        string title = card.TitleTone == RecompOne.Runtime.Host.GemHints.Tone.Bad ? ToneHex(card.TitleTone) : gem;
        var sb = new System.Text.StringBuilder();
        sb.Append($"<font color=\"{gem}\">{(card.Owned ? "◆" : "◇")}</font> ");
        sb.Append($"<font color=\"{title}\"><b>{Esc(card.Title.ToUpperInvariant())}</b></font>");
        foreach (var line in card.Lines)
            sb.Append($"<br><font color=\"{ToneHex(line.Tone)}\">{Esc(line.Text)}</font>");
        return sb.ToString();
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
