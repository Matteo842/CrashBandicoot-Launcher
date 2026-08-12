using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Activity = Android.App.Activity;

namespace CrashBandicoot.AndroidRuntime;

sealed class GpuDiagnosticsReport
{
    public int SchemaVersion { get; set; } = 1;
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DeviceDiagnostics Device { get; set; } = new();
    public GpuDriverDiagnostics Gpu { get; set; } = new();
    public ThermalDiagnostics Thermal { get; set; } = new();
    public List<GpuBenchmarkResult> Benchmarks { get; set; } = [];
    public GameSessionDiagnostics? LastGameSession { get; set; }
    public string? Error { get; set; }
}

sealed class DeviceDiagnostics
{
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string Device { get; set; } = "";
    public string Hardware { get; set; } = "";
    public string AndroidRelease { get; set; } = "";
    public int AndroidSdk { get; set; }
    public string AppVersion { get; set; } = "";
}

sealed class GpuDriverDiagnostics
{
    public string Vendor { get; set; } = "";
    public string Renderer { get; set; } = "";
    public string Version { get; set; } = "";
    public string ShadingLanguageVersion { get; set; } = "";
    public string FramebufferFetchPath { get; set; } = "fallback";
    public string TextureBarrierPath { get; set; } = "flush fallback";
    public bool QcomShadingRateAvailable { get; set; }
    public string[] Extensions { get; set; } = [];
}

sealed class ThermalDiagnostics
{
    public string Status { get; set; } = "unavailable";
    public int? StatusCode { get; set; }
    public double? HeadroomNow { get; set; }
    public double? BatteryTemperatureC { get; set; }
}

sealed class FrameTimeDiagnostics
{
    public int Samples { get; set; }
    public double AverageMs { get; set; }
    public double P50Ms { get; set; }
    public double P95Ms { get; set; }
    public double P99Ms { get; set; }
    public double MaximumMs { get; set; }
}

sealed class GpuBenchmarkResult
{
    public int Scale { get; set; }
    public int RenderWidth { get; set; }
    public int RenderHeight { get; set; }
    public string FramebufferFetchPath { get; set; } = "fallback";
    public bool TextureBarrierActive { get; set; }
    public bool CoarseShadingActive { get; set; }
    public double DurationSeconds { get; set; }
    public int Frames { get; set; }
    public double ThroughputFps { get; set; }
    public FrameTimeDiagnostics FrameTime { get; set; } = new();
    public double AverageBatches { get; set; }
    public double AverageVertices { get; set; }
    public string? Error { get; set; }
}

sealed class GameSessionDiagnostics
{
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public int InternalScale { get; set; }
    public string FramebufferFetchPath { get; set; } = "fallback";
    public bool TextureBarrierActive { get; set; }
    public bool CoarseShadingActive { get; set; }
    public int Frames { get; set; }
    public double DurationSeconds { get; set; }
    public double AverageFps { get; set; }
    public FrameTimeDiagnostics FrameTime { get; set; } = new();
    public double AveragePrepareMs { get; set; }
    public double AverageSurfaceMs { get; set; }
    public double AverageSwapMs { get; set; }
    public double AverageBatches { get; set; }
    public double AverageWritebacks { get; set; }
    public double AverageVertices { get; set; }
    public ThermalDiagnostics ThermalAtStart { get; set; } = new();
    public ThermalDiagnostics ThermalLatest { get; set; } = new();
    public string ThermalPeak { get; set; } = "unavailable";
}

static class GpuDiagnosticsStore
{
    static readonly object Gate = new();
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string ReportPath(Activity activity)
    {
        var root = Path.Combine(activity.FilesDir!.AbsolutePath, "runtime", "diagnostics");
        Directory.CreateDirectory(root);
        return Path.Combine(root, "gpu-report-latest.json");
    }

    public static GpuDiagnosticsReport CreateBaseReport(Activity activity, AndroidGlesInfo gpu)
    {
        var existing = Load(activity) ?? new GpuDiagnosticsReport();
        existing.UpdatedAtUtc = DateTimeOffset.UtcNow;
        existing.Device = ReadDevice(activity);
        existing.Gpu = ReadGpu(gpu);
        existing.Thermal = ReadThermal(activity);
        existing.Error = null;
        return existing;
    }

    public static GpuDiagnosticsReport? Load(Activity activity)
    {
        lock (Gate)
        {
            try
            {
                var path = ReportPath(activity);
                return File.Exists(path)
                    ? JsonSerializer.Deserialize<GpuDiagnosticsReport>(File.ReadAllText(path), JsonOptions)
                    : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public static void Save(Activity activity, GpuDiagnosticsReport report)
    {
        lock (Gate)
        {
            report.UpdatedAtUtc = DateTimeOffset.UtcNow;
            var path = ReportPath(activity);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
            File.Move(temporary, path, true);
        }
    }

    public static void Share(Activity activity)
    {
        var path = ReportPath(activity);
        if (!File.Exists(path)) return;
        var send = new Intent(Intent.ActionSend);
        send.SetType("application/json");
        send.PutExtra(Intent.ExtraText, File.ReadAllText(path));
        activity.StartActivity(Intent.CreateChooser(send, "Condividi report GPU JSON"));
    }

    public static DeviceDiagnostics ReadDevice(Activity activity)
    {
        string appVersion = "";
        try
        {
            appVersion = activity.PackageManager?
                .GetPackageInfo(activity.PackageName!, PackageInfoFlags.MatchAll)?.VersionName ?? "";
        }
        catch
        {
            // Package metadata is useful but not required for a diagnostic run.
        }

        return new DeviceDiagnostics
        {
            Manufacturer = Build.Manufacturer ?? "",
            Model = Build.Model ?? "",
            Device = Build.Device ?? "",
            Hardware = Build.Hardware ?? "",
            AndroidRelease = Build.VERSION.Release ?? "",
            AndroidSdk = (int)Build.VERSION.SdkInt,
            AppVersion = appVersion,
        };
    }

    public static GpuDriverDiagnostics ReadGpu(AndroidGlesInfo gpu) => new()
    {
        Vendor = gpu.Vendor,
        Renderer = gpu.Renderer,
        Version = gpu.Version,
        ShadingLanguageVersion = gpu.ShadingLanguageVersion,
        FramebufferFetchPath = gpu.FramebufferFetchLabel,
        TextureBarrierPath = gpu.TextureBarrierFunction ?? "flush fallback",
        QcomShadingRateAvailable = gpu.QcomShadingRateAvailable,
        Extensions = gpu.Extensions,
    };

    public static ThermalDiagnostics ReadThermal(Activity activity)
    {
        int? status = null;
        double? headroom = null;
        double? batteryTemperature = null;

        try
        {
            using var battery = activity.RegisterReceiver(
                null, new IntentFilter(Intent.ActionBatteryChanged));
            var tenths = battery?.GetIntExtra(BatteryManager.ExtraTemperature, int.MinValue)
                         ?? int.MinValue;
            if (tenths != int.MinValue) batteryTemperature = tenths / 10.0;
        }
        catch
        {
            // Some vendor builds can hide battery extras from non-system apps.
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            try
            {
                var power = (PowerManager?)activity.GetSystemService(Android.Content.Context.PowerService);
                if (power != null)
                {
                    status = (int)power.CurrentThermalStatus;
                    if (OperatingSystem.IsAndroidVersionAtLeast(30))
                    {
                        var value = power.GetThermalHeadroom(0);
                        if (float.IsFinite(value)) headroom = value;
                    }
                }
            }
            catch
            {
                // Thermal headroom is optional and not implemented by every OEM.
            }
        }

        return new ThermalDiagnostics
        {
            StatusCode = status,
            Status = ThermalStatusName(status),
            HeadroomNow = headroom,
            BatteryTemperatureC = batteryTemperature,
        };
    }

    public static string ThermalStatusName(int? status) => status switch
    {
        0 => "none",
        1 => "light",
        2 => "moderate",
        3 => "severe",
        4 => "critical",
        5 => "emergency",
        6 => "shutdown",
        _ => "unavailable",
    };

    public static FrameTimeDiagnostics Summarize(IReadOnlyCollection<double> samples)
    {
        if (samples.Count == 0) return new FrameTimeDiagnostics();
        var sorted = samples.OrderBy(x => x).ToArray();
        return new FrameTimeDiagnostics
        {
            Samples = sorted.Length,
            AverageMs = sorted.Average(),
            P50Ms = Percentile(sorted, 0.50),
            P95Ms = Percentile(sorted, 0.95),
            P99Ms = Percentile(sorted, 0.99),
            MaximumMs = sorted[^1],
        };
    }

    static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 1) return sorted[0];
        var position = (sorted.Count - 1) * percentile;
        var low = (int)Math.Floor(position);
        var high = (int)Math.Ceiling(position);
        if (low == high) return sorted[low];
        var fraction = position - low;
        return sorted[low] + (sorted[high] - sorted[low]) * fraction;
    }
}

sealed class GameGpuDiagnosticsSession
{
    const int MaxFrameSamples = 8192;

    readonly Activity _activity;
    readonly GpuDiagnosticsReport _report;
    readonly GameSessionDiagnostics _session;
    readonly Queue<double> _frameTimes = new(MaxFrameSamples);
    readonly long _started = Stopwatch.GetTimestamp();
    long _lastSave;
    long _lastThermalSample;
    double _prepareTotal;
    double _surfaceTotal;
    double _swapTotal;
    long _flushes;
    long _writebacks;
    long _vertices;
    int _thermalPeak = -1;
    bool _completed;

    public GameGpuDiagnosticsSession(Activity activity, AndroidGlesInfo gpu, int scale,
        bool textureBarrier, bool coarseShading)
    {
        _activity = activity;
        _report = GpuDiagnosticsStore.CreateBaseReport(activity, gpu);
        var thermal = GpuDiagnosticsStore.ReadThermal(activity);
        _thermalPeak = thermal.StatusCode ?? -1;
        _session = new GameSessionDiagnostics
        {
            StartedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            InternalScale = scale,
            FramebufferFetchPath = gpu.FramebufferFetchLabel,
            TextureBarrierActive = textureBarrier,
            CoarseShadingActive = coarseShading,
            ThermalAtStart = thermal,
            ThermalLatest = thermal,
            ThermalPeak = GpuDiagnosticsStore.ThermalStatusName(thermal.StatusCode),
        };
        _report.LastGameSession = _session;
        _lastSave = _started;
        _lastThermalSample = _started;
        GpuDiagnosticsStore.Save(activity, _report);
    }

    public void RecordFrame(double frameIntervalMs, double prepareMs, double surfaceMs,
        double swapMs, int flushes, int writebacks, int vertices)
    {
        if (_completed) return;
        _session.Frames++;
        if (frameIntervalMs > 0)
        {
            if (_frameTimes.Count == MaxFrameSamples) _frameTimes.Dequeue();
            _frameTimes.Enqueue(frameIntervalMs);
        }
        _prepareTotal += prepareMs;
        _surfaceTotal += surfaceMs;
        _swapTotal += swapMs;
        _flushes += flushes;
        _writebacks += writebacks;
        _vertices += vertices;

        var now = Stopwatch.GetTimestamp();
        var secondsSinceThermal = (now - _lastThermalSample) / (double)Stopwatch.Frequency;
        if (secondsSinceThermal >= 10)
        {
            _session.ThermalLatest = GpuDiagnosticsStore.ReadThermal(_activity);
            _thermalPeak = Math.Max(_thermalPeak, _session.ThermalLatest.StatusCode ?? -1);
            _session.ThermalPeak = GpuDiagnosticsStore.ThermalStatusName(
                _thermalPeak < 0 ? null : _thermalPeak);
            _lastThermalSample = now;
        }

        if ((now - _lastSave) / (double)Stopwatch.Frequency >= 5)
        {
            Snapshot(now);
            GpuDiagnosticsStore.Save(_activity, _report);
            _lastSave = now;
        }
    }

    public void Complete()
    {
        if (_completed) return;
        _completed = true;
        _session.ThermalLatest = GpuDiagnosticsStore.ReadThermal(_activity);
        _thermalPeak = Math.Max(_thermalPeak, _session.ThermalLatest.StatusCode ?? -1);
        _session.ThermalPeak = GpuDiagnosticsStore.ThermalStatusName(
            _thermalPeak < 0 ? null : _thermalPeak);
        Snapshot(Stopwatch.GetTimestamp());
        GpuDiagnosticsStore.Save(_activity, _report);
    }

    void Snapshot(long now)
    {
        var frames = Math.Max(1, _session.Frames);
        _session.UpdatedAtUtc = DateTimeOffset.UtcNow;
        _session.DurationSeconds = (now - _started) / (double)Stopwatch.Frequency;
        _session.AverageFps = _session.DurationSeconds > 0
            ? _session.Frames / _session.DurationSeconds
            : 0;
        _session.FrameTime = GpuDiagnosticsStore.Summarize(_frameTimes);
        _session.AveragePrepareMs = _prepareTotal / frames;
        _session.AverageSurfaceMs = _surfaceTotal / frames;
        _session.AverageSwapMs = _swapTotal / frames;
        _session.AverageBatches = _flushes / (double)frames;
        _session.AverageWritebacks = _writebacks / (double)frames;
        _session.AverageVertices = _vertices / (double)frames;
        _report.Thermal = _session.ThermalLatest;
    }
}
