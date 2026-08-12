using System.Diagnostics;
using Android.App;
using RecompOne.Runtime.Hle;
using Silk.NET.OpenGL;
using Activity = Android.App.Activity;

namespace CrashBandicoot.AndroidRuntime;

static class GpuSyntheticBenchmark
{
    const int BaseWidth = 320;
    const int BaseHeight = 240;
    const double RunSeconds = 1.5;
    static readonly int[] Scales = [1, 2, 4, 8];

    public static GpuDiagnosticsReport Run(Activity activity, AndroidEglContext egl, GL gl,
        Action<string> progress)
    {
        var gpu = AndroidGlesInfo.Capture(
            gl, egl, activity.Intent?.GetStringExtra(AndroidGlesInfo.ForceFramebufferFetchExtra));
        var report = GpuDiagnosticsStore.CreateBaseReport(activity, gpu);
        report.Benchmarks = [];
        report.Error = null;
        var originalScale = GlVram.Scale;

        try
        {
            GpuHle.NotifyDisplay(0, 0, BaseWidth, BaseHeight);
            foreach (var scale in Scales)
            {
                progress($"Benchmark {scale}x • {BaseWidth * scale}×{BaseHeight * scale}");
                report.Benchmarks.Add(RunScale(gl, gpu, scale));
            }
        }
        catch (Exception ex)
        {
            report.Error = ex.ToString();
        }
        finally
        {
            GlVram.Scale = originalScale;
            report.Thermal = GpuDiagnosticsStore.ReadThermal(activity);
            GpuDiagnosticsStore.Save(activity, report);
        }

        return report;
    }

    static GpuBenchmarkResult RunScale(GL gl, AndroidGlesInfo gpu, int scale)
    {
        var result = new GpuBenchmarkResult
        {
            Scale = scale,
            RenderWidth = BaseWidth * scale,
            RenderHeight = BaseHeight * scale,
            FramebufferFetchPath = gpu.FramebufferFetchLabel,
        };

        GlBackend? backend = null;
        try
        {
            GlVram.Scale = scale;
            backend = new GlBackend(gl);
            backend.InitGl(gles: true, framebufferFetch: gpu.FramebufferFetchPath);
            if (!backend.Ready)
                throw new InvalidOperationException(
                    $"Renderer initialization failed: {backend.LastDiagnostic}");

            var configured = gpu.ConfigureBackend(backend, scale);
            result.TextureBarrierActive = configured.textureBarrier;
            result.CoarseShadingActive = configured.coarseShading;

            backend.SetDrawEnv(new HleDrawEnv
            {
                ClipX0 = 0,
                ClipY0 = 0,
                ClipX1 = BaseWidth - 1,
                ClipY1 = BaseHeight - 1,
                Dither = true,
            });
            backend.FillRect(0, 0, BaseWidth, BaseHeight, 0x0421);

            for (var i = 0; i < 5; i++)
            {
                DrawScene(backend, i);
                backend.PresentDisplay(0, 0, BaseWidth, BaseHeight,
                    outW: BaseWidth * 2, outH: BaseHeight * 2);
                gl.Finish();
            }

            var frameTimes = new List<double>(512);
            long flushes = 0;
            long vertices = 0;
            var runStart = Stopwatch.GetTimestamp();
            var frame = 0;
            while ((Stopwatch.GetTimestamp() - runStart) / (double)Stopwatch.Frequency < RunSeconds ||
                   frame < 8)
            {
                var frameStart = Stopwatch.GetTimestamp();
                DrawScene(backend, frame);
                backend.PresentDisplay(0, 0, BaseWidth, BaseHeight,
                    outW: BaseWidth * 2, outH: BaseHeight * 2);
                gl.Finish();
                var frameEnd = Stopwatch.GetTimestamp();
                frameTimes.Add((frameEnd - frameStart) * 1000.0 / Stopwatch.Frequency);
                flushes += backend.LastFrameFlushes;
                vertices += backend.LastFrameVertices;
                frame++;
            }

            var duration = (Stopwatch.GetTimestamp() - runStart) / (double)Stopwatch.Frequency;
            result.DurationSeconds = duration;
            result.Frames = frame;
            result.ThroughputFps = duration > 0 ? frame / duration : 0;
            result.FrameTime = GpuDiagnosticsStore.Summarize(frameTimes);
            result.AverageBatches = flushes / (double)Math.Max(1, frame);
            result.AverageVertices = vertices / (double)Math.Max(1, frame);
        }
        catch (Exception ex)
        {
            result.Error = ex.ToString();
        }
        finally
        {
            backend?.Dispose();
        }

        return result;
    }

    static void DrawScene(GlBackend backend, int frame)
    {
        var phase = frame * 0.075f;
        var opaque = new PrimFlags();

        // Dense PS1-style background: small flat primitives plus a large amount
        // of overdraw. The geometry is deterministic on every device.
        for (var y = 0; y < BaseHeight; y += 24)
        for (var x = 0; x < BaseWidth; x += 32)
        {
            var wave = (int)(MathF.Sin(phase + x * 0.025f + y * 0.04f) * 22f);
            backend.DrawRect(new HleRect
            {
                X = x,
                Y = y,
                W = 30,
                H = 22,
                R = (byte)Math.Clamp(58 + x / 2 + wave, 0, 255),
                G = (byte)Math.Clamp(38 + y / 2 - wave, 0, 255),
                B = (byte)Math.Clamp(92 + wave, 0, 255),
            }, opaque);
        }

        // Layered spinning diamonds model Crash's transparency-heavy spin. All
        // four PS1 semi-transparency equations appear in a stable draw order.
        for (var layer = 0; layer < 16; layer++)
        {
            var angle = phase + layer * 0.31f;
            var radiusX = 42f + layer * 5.5f;
            var radiusY = 26f + layer * 3.2f;
            var cx = BaseWidth * 0.5f + MathF.Sin(phase * 0.7f) * 34f;
            var cy = BaseHeight * 0.5f + MathF.Cos(phase * 0.9f) * 18f;
            var cos = MathF.Cos(angle);
            var sin = MathF.Sin(angle);

            HleVertex Vertex(float x, float y, byte r, byte g, byte b) => new()
            {
                X = cx + x * cos - y * sin,
                Y = cy + x * sin + y * cos,
                R = r,
                G = g,
                B = b,
            };

            var color = (byte)(72 + layer * 11);
            var top = Vertex(0, -radiusY, 255, color, 48);
            var right = Vertex(radiusX, 0, 56, 220, color);
            var bottom = Vertex(0, radiusY, color, 64, 255);
            var left = Vertex(-radiusX, 0, 240, 48, color);
            var flags = new PrimFlags
            {
                SemiTrans = true,
                Gouraud = true,
                TPage = (ushort)((layer & 3) << 5),
            };
            backend.DrawTri(top, right, bottom, flags);
            backend.DrawTri(bottom, left, top, flags);
        }
    }
}
