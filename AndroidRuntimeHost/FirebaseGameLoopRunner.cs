using System.Text;
using System.Text.Json;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;

namespace CrashBandicoot.AndroidRuntime;

/// <summary>
/// Unattended ROM-free GPU compatibility loop for Firebase Test Lab. Test Lab
/// supplies an output URI, launches this activity, and collects the JSON after
/// the activity finishes.
/// </summary>
sealed class FirebaseGameLoopRunner(
    Activity activity,
    Intent launchIntent) : Java.Lang.Object, TextureView.ISurfaceTextureListener
{
    public const string Action = "com.google.intent.action.TEST_LOOP";

    readonly TextureView _surface = new(activity);
    readonly TextView _status = new(activity);
    int _started;

    public static bool IsRequested(Intent? intent) =>
        string.Equals(intent?.Action, Action, StringComparison.Ordinal);

    public void Start()
    {
        var root = new FrameLayout(activity);
        root.SetBackgroundColor(Color.Rgb(6, 16, 24));

        _status.Text = "Firebase GPU compatibility loop";
        _status.TextSize = 18;
        _status.Gravity = GravityFlags.Center;
        _status.SetTextColor(Color.Rgb(238, 225, 190));
        root.AddView(_status, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent));

        // EGL needs a real Android window surface even though the benchmark
        // renders into its own off-screen PS1 framebuffers.
        _surface.Alpha = 0.01f;
        _surface.SurfaceTextureListener = this;
        root.AddView(_surface, new FrameLayout.LayoutParams(4, 4));
        activity.SetContentView(root);

        if (_surface.IsAvailable && _surface.SurfaceTexture != null)
            RunOnce(_surface.SurfaceTexture);
    }

    void RunOnce(SurfaceTexture texture)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        var scenario = launchIntent.GetIntExtra("scenario", 1);

        _ = Task.Run(() =>
        {
            GpuDiagnosticsReport? report = null;
            Exception? failure = null;
            try
            {
                using var nativeSurface = new Surface(texture);
                using var egl = new AndroidEglContext(nativeSurface,
                    () => new Surface(_surface.SurfaceTexture
                        ?? throw new InvalidOperationException("SurfaceTexture non disponibile.")));
                using var gl = Silk.NET.OpenGL.GL.GetApi(egl);
                report = GpuSyntheticBenchmark.Run(
                    activity,
                    egl,
                    gl,
                    message => activity.RunOnUiThread(() => _status.Text = message));
                WriteResult(report, scenario);
            }
            catch (Exception ex)
            {
                failure = ex;
                WriteFailure(ex, scenario);
                Android.Util.Log.Error("CrashGPU", $"Firebase game loop failed: {ex}");
            }

            activity.RunOnUiThread(() =>
            {
                _status.Text = failure == null && report?.Error == null
                    ? "GPU loop completed"
                    : "GPU loop completed with errors";
                activity.Finish();
            });
        });
    }

    void WriteResult(GpuDiagnosticsReport report, int scenario)
    {
        long elapsedMicroseconds = 0;
        var frameStats = report.Benchmarks.Select(item =>
        {
            elapsedMicroseconds += (long)(item.DurationSeconds * 1_000_000.0);
            return new Dictionary<string, object?>
            {
                ["timestamp"] = elapsedMicroseconds,
                ["avg_frame_time"] = (long)(item.FrameTime.AverageMs * 1_000_000.0),
                ["nb_swap"] = item.Frames,
                ["scale"] = item.Scale,
                ["render_width"] = item.RenderWidth,
                ["render_height"] = item.RenderHeight,
                ["throughput_fps"] = item.ThroughputFps,
                ["p50_frame_time_ms"] = item.FrameTime.P50Ms,
                ["p95_frame_time_ms"] = item.FrameTime.P95Ms,
                ["p99_frame_time_ms"] = item.FrameTime.P99Ms,
                ["framebuffer_fetch"] = item.FramebufferFetchPath,
                ["texture_barrier_active"] = item.TextureBarrierActive,
                ["coarse_shading_active"] = item.CoarseShadingActive,
                ["error"] = item.Error,
            };
        }).ToArray();

        var output = new
        {
            name = "Crash Bandicoot ROM-free GPU compatibility",
            start_timestamp = 0,
            driver_info = $"{report.Gpu.Vendor} / {report.Gpu.Renderer} / {report.Gpu.Version}",
            frame_stats = frameStats,
            scenario,
            report.Device,
            report.Gpu,
            report.Thermal,
            report.Benchmarks,
            report.Error,
        };
        WriteOutput(JsonSerializer.Serialize(output, JsonOptions));
    }

    void WriteFailure(Exception exception, int scenario)
    {
        var output = new
        {
            name = "Crash Bandicoot ROM-free GPU compatibility",
            start_timestamp = 0,
            driver_info = "unavailable",
            frame_stats = Array.Empty<object>(),
            scenario,
            error = exception.ToString(),
        };
        WriteOutput(JsonSerializer.Serialize(output, JsonOptions));
    }

    void WriteOutput(string json)
    {
        var outputUri = launchIntent.Data;
        if (outputUri == null) return;
        using var stream = activity.ContentResolver?.OpenOutputStream(outputUri, "wt")
                           ?? throw new IOException("Firebase result URI is not writable.");
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(json);
    }

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height) =>
        RunOnce(surface);

    public bool OnSurfaceTextureDestroyed(SurfaceTexture surface) => true;
    public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height) { }
    public void OnSurfaceTextureUpdated(SurfaceTexture surface) { }
}
