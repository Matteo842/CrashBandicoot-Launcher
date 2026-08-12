using System.Text;
using Android.App;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;

namespace CrashBandicoot.AndroidRuntime;

static class GpuLabDialog
{
    public static void Show(Activity activity) => new Controller(activity).Show();

    sealed class Controller(Activity activity) : Java.Lang.Object, TextureView.ISurfaceTextureListener
    {
        readonly Dialog _dialog = new(activity);
        readonly TextView _status = new(activity);
        readonly TextView _details = new(activity);
        readonly ProgressBar _progress = new(activity) { Indeterminate = true };
        readonly TextureView _surface = new(activity);
        readonly Button _run = new(activity) { Text = "RUN TEST" };
        readonly Button _share = new(activity) { Text = "SHARE JSON" };
        readonly Button _close = new(activity) { Text = "CLOSE" };
        bool _pendingRun;
        bool _running;

        public void Show()
        {
            _dialog.RequestWindowFeature((int)WindowFeatures.NoTitle);
            _dialog.SetCanceledOnTouchOutside(true);

            var card = new LinearLayout(activity) { Orientation = Orientation.Vertical };
            card.SetPadding(Dp(24), Dp(20), Dp(24), Dp(18));
            card.Background = RoundedBackground(Color.Rgb(9, 28, 24));

            var title = new TextView(activity)
            {
                Text = "GPU LAB",
                TextSize = 22,
            };
            title.SetTextColor(Color.Rgb(255, 157, 24));
            title.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
            card.AddView(title);

            var intro = new TextView(activity)
            {
                Text = "Driver diagnostics and a synthetic PS1 benchmark. It does not use the ROM and takes about 8 seconds.",
                TextSize = 13,
            };
            intro.SetTextColor(Color.Rgb(238, 225, 190));
            card.AddView(intro, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
            {
                TopMargin = Dp(6),
                BottomMargin = Dp(10),
            });

            _status.TextSize = 13;
            _status.SetTextColor(Color.Rgb(132, 255, 163));
            _status.Text = "Ready";
            card.AddView(_status);

            _progress.Visibility = ViewStates.Gone;
            card.AddView(_progress, new LinearLayout.LayoutParams(Dp(28), Dp(28))
            {
                TopMargin = Dp(6),
                BottomMargin = Dp(6),
            });

            _details.TextSize = 12;
            _details.SetTextColor(Color.Rgb(238, 225, 190));
            _details.SetTextIsSelectable(true);
            var scroll = new ScrollView(activity);
            scroll.AddView(_details);
            card.AddView(scroll, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent, 0, 1f)
            {
                TopMargin = Dp(4),
                BottomMargin = Dp(10),
            });

            // A real window surface is required to create EGL. It stays almost
            // transparent; all benchmark rendering happens in off-screen FBOs.
            _surface.Alpha = 0.01f;
            _surface.SurfaceTextureListener = this;
            card.AddView(_surface, new LinearLayout.LayoutParams(Dp(4), Dp(4)));

            var actions = new LinearLayout(activity)
            {
                Orientation = Orientation.Horizontal,
            };
            actions.SetGravity(GravityFlags.End);
            StyleButton(_run, Color.Rgb(171, 72, 8));
            StyleButton(_share, Color.Rgb(35, 86, 63));
            StyleButton(_close, Color.Rgb(57, 44, 33));
            actions.AddView(_run, ButtonLayout());
            actions.AddView(_share, ButtonLayout());
            actions.AddView(_close, ButtonLayout());
            card.AddView(actions);

            _run.Click += (_, _) => StartBenchmark();
            _share.Click += (_, _) => GpuDiagnosticsStore.Share(activity);
            _close.Click += (_, _) => _dialog.Dismiss();

            var previous = GpuDiagnosticsStore.Load(activity);
            _share.Enabled = previous != null;
            _details.Text = previous == null
                ? "No report saved yet. Run the test to identify the GPU, extensions, and active path."
                : BuildSummary(previous);

            _dialog.SetContentView(card);
            AndroidGamepad.BindDialog(_dialog);
            _dialog.Show();
            _dialog.Window?.SetLayout(
                Math.Min(activity.Resources?.DisplayMetrics?.WidthPixels ?? Dp(900), Dp(900)),
                Math.Min(activity.Resources?.DisplayMetrics?.HeightPixels ?? Dp(560), Dp(560)));
        }

        void StartBenchmark()
        {
            if (_running) return;
            if (!_surface.IsAvailable || _surface.SurfaceTexture == null)
            {
                _pendingRun = true;
                _status.Text = "Preparing the GPU context…";
                return;
            }

            _pendingRun = false;
            _running = true;
            _run.Enabled = false;
            _share.Enabled = false;
            _close.Enabled = false;
            _progress.Visibility = ViewStates.Visible;
            _details.Text = "The test uses the same GLES pipeline as the game, with opaque and transparent primitives at 1x, 2x, 4x, and 8x.";
            _dialog.SetCancelable(false);

            var texture = _surface.SurfaceTexture;
            _ = Task.Run(() =>
            {
                try
                {
                    using var nativeSurface = new Surface(texture!);
                    using var egl = new AndroidEglContext(nativeSurface,
                        () => new Surface(_surface.SurfaceTexture
                            ?? throw new InvalidOperationException("SurfaceTexture is not available.")));
                    using var gl = Silk.NET.OpenGL.GL.GetApi(egl);
                    var report = GpuSyntheticBenchmark.Run(activity, egl, gl, SetProgress);
                    activity.RunOnUiThread(() => Finish(report));
                }
                catch (Exception ex)
                {
                    activity.RunOnUiThread(() => Fail(ex));
                }
            });
        }

        void SetProgress(string message) => activity.RunOnUiThread(() => _status.Text = message);

        void Finish(GpuDiagnosticsReport report)
        {
            _running = false;
            _status.Text = report.Error == null ? "Test completed" : "Test completed with errors";
            _details.Text = BuildSummary(report);
            _progress.Visibility = ViewStates.Gone;
            _run.Enabled = true;
            _share.Enabled = true;
            _close.Enabled = true;
            _dialog.SetCancelable(true);
        }

        void Fail(Exception ex)
        {
            _running = false;
            _status.Text = "Test failed";
            _details.Text = ex.GetBaseException().Message;
            _progress.Visibility = ViewStates.Gone;
            _run.Enabled = true;
            _share.Enabled = GpuDiagnosticsStore.Load(activity) != null;
            _close.Enabled = true;
            _dialog.SetCancelable(true);
        }

        static string BuildSummary(GpuDiagnosticsReport report)
        {
            var text = new StringBuilder();
            text.AppendLine($"{report.Device.Manufacturer} {report.Device.Model} • Android {report.Device.AndroidRelease}");
            text.AppendLine(report.Gpu.Renderer);
            text.AppendLine($"Driver: {report.Gpu.Version}");
            text.AppendLine($"Framebuffer fetch: {report.Gpu.FramebufferFetchPath}");
            text.AppendLine($"Texture barrier: {report.Gpu.TextureBarrierPath}");
            text.AppendLine($"QCOM shading rate: {(report.Gpu.QcomShadingRateAvailable ? "available" : "unavailable")}");
            text.AppendLine($"Thermal: {report.Thermal.Status}" +
                            (report.Thermal.BatteryTemperatureC is { } t ? $" • battery {t:F1} °C" : ""));

            if (report.Benchmarks.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("BENCHMARK ROM-FREE (throughput not display-limited)");
                foreach (var item in report.Benchmarks)
                {
                    if (item.Error != null)
                    {
                        text.AppendLine($"{item.Scale}x • error: {FirstLine(item.Error)}");
                        continue;
                    }
                    text.AppendLine($"{item.Scale}x {item.RenderWidth}×{item.RenderHeight} • " +
                                    $"{item.ThroughputFps:F1} frame/s • " +
                                    $"p50 {item.FrameTime.P50Ms:F2} ms • p95 {item.FrameTime.P95Ms:F2} ms • " +
                                    $"p99 {item.FrameTime.P99Ms:F2} ms");
                }
            }

            if (report.LastGameSession is { } game)
            {
                text.AppendLine();
                text.AppendLine("LAST REAL SESSION");
                text.AppendLine($"{game.InternalScale}x • {game.AverageFps:F1} avg FPS • " +
                                $"p95 {game.FrameTime.P95Ms:F2} ms • thermal peak {game.ThermalPeak}");
            }

            if (report.Error != null)
            {
                text.AppendLine();
                text.AppendLine($"Error: {FirstLine(report.Error)}");
            }
            return text.ToString().TrimEnd();
        }

        static string FirstLine(string text) =>
            text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? text;

        void StyleButton(Button button, Color color)
        {
            button.SetTextColor(Color.White);
            button.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
            button.TextSize = 11;
            button.SetMinWidth(0);
            button.SetMinHeight(0);
            button.StateListAnimator = null;
            button.Background = RoundedBackground(color);
        }

        LinearLayout.LayoutParams ButtonLayout() => new(0, Dp(42), 1f)
        {
            LeftMargin = Dp(4),
            RightMargin = Dp(4),
        };

        GradientDrawable RoundedBackground(Color color)
        {
            var background = new GradientDrawable();
            background.SetColor(color);
            background.SetCornerRadius(Dp(12));
            background.SetStroke(Dp(1), Color.Argb(170, 255, 157, 24));
            return background;
        }

        int Dp(int value) => (int)(value * (activity.Resources?.DisplayMetrics?.Density ?? 1f) + 0.5f);

        public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
        {
            if (_pendingRun) StartBenchmark();
        }

        public bool OnSurfaceTextureDestroyed(SurfaceTexture surface) => true;
        public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height) { }
        public void OnSurfaceTextureUpdated(SurfaceTexture surface) { }
    }
}
