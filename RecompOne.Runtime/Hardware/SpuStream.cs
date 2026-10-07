using System.Diagnostics;

namespace RecompOne.Runtime;

/// <summary>
/// Vblank-clocked SPU output. On hardware the libsnd sequencer (SS_TICK60) is
/// ticked by the 60 Hz VBlank IRQ while the SPU plays continuously, so each
/// sequencer tick lands exactly 735 samples (44100 / 60) after the previous one.
///
/// The pull mixer sampled SPU state whenever the audio device asked for data
/// (bursts of ~60 ms on Android deep-buffer outputs) and VBlank IRQs were tied
/// to presents, so a late frame lost sequencer ticks: notes drifted and the
/// tempo wobbled (#23).
///
/// With <see cref="Enabled"/> VBlank IRQs follow the wall clock and late ones
/// are caught up instead of dropped. Right after IRQ k (the sequencer tick) a
/// worker renders vblank k — exactly 735 frames — under the SPU lock and
/// queues it, so the game thread does not pay for mixing. Game writes made
/// while the block renders wait on that lock and land in the next block.
/// The host drains the queue with <see cref="ReadResampled"/>, which converts
/// to the device rate and nudges that rate by at most ±0.5 % to hold the
/// queue near its target fill.
/// </summary>
public static class SpuStream
{
    public const int SampleRate = 44100;
    public const int FramesPerVblank = SampleRate / 60;

    /// <summary>
    /// Queue fill the consumer steers towards (4 vblanks ≈ 67 ms): rides out
    /// the 50–70 ms game-thread hitches seen on a Galaxy S24.
    /// </summary>
    const int TargetFrames = FramesPerVblank * 4;
    const int Capacity = 1 << 14; // frames, ~370 ms
    const int Mask = Capacity - 1;
    /// <summary>Late vblanks rendered in one go after a hitch; longer stalls resync.</summary>
    const int MaxCatchUp = 6;
    const double MaxRateAdjust = 0.005;

    static readonly long VblankPeriod = Stopwatch.Frequency / 60;
    static readonly short[] _ring = new short[Capacity * 2];
    static readonly short[] _vblank = new short[FramesPerVblank * 2];
    static long _written;
    static long _read;
    static long _nextVblank;

    /// <summary>Set by hosts that drain the queue (Android). Off: legacy pull mixer.</summary>
    public static bool Enabled { get; set; }

    /// <summary>Frames queued and not yet consumed.</summary>
    public static int Buffered => (int)(Volatile.Read(ref _written) - Volatile.Read(ref _read));

    // ---- game thread ----

    /// <summary>
    /// Renders and dispatches every VBlank IRQ that is due by the wall clock.
    /// Called on each present and from the long-CPU-work catch-up path.
    /// </summary>
    public static void PumpVblanks()
    {
        long now = Stopwatch.GetTimestamp();
        if (_nextVblank == 0 || now - _nextVblank > VblankPeriod * MaxCatchUp)
            _nextVblank = now;
        for (int n = 0; n < MaxCatchUp && now >= _nextVblank; n++)
        {
            _nextVblank += VblankPeriod;
            // Block k-1 must be mixed before tick k's key-ons touch the SPU.
            _renderIdle.Wait();
            Runtime.DispatchIrq(0);
            StartRender();
        }
    }

    static Thread? _renderThread;
    static Spu? _renderSpu;
    static readonly SemaphoreSlim _renderStart = new(0);
    static readonly ManualResetEventSlim _renderIdle = new(initialState: true);

    static void StartRender()
    {
        var spu = Runtime.Spu;
        if (spu == null) return;
        if (_renderThread == null)
        {
            _renderThread = new Thread(RenderLoop)
            {
                IsBackground = true,
                Name = "spu-vblank",
                Priority = ThreadPriority.AboveNormal,
            };
            _renderThread.Start();
        }
        _renderSpu = spu;
        _renderIdle.Reset();
        _renderStart.Release();
    }

    static void RenderLoop()
    {
        while (true)
        {
            _renderStart.Wait();
            try
            {
                if (_renderSpu != null)
                    RenderVblank(_renderSpu);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[SpuStream] render failed: {e.Message}");
            }
            finally
            {
                _renderIdle.Set();
            }
        }
    }

    static void RenderVblank(Spu spu)
    {
        spu.Mix(_vblank, FramesPerVblank);

        long w = _written;
        if (w + FramesPerVblank - Volatile.Read(ref _read) > Capacity)
            return; // nobody draining (output paused or failed)
        int at = (int)(w & Mask);
        int first = Math.Min(FramesPerVblank, Capacity - at);
        Array.Copy(_vblank, 0, _ring, at * 2, first * 2);
        if (first < FramesPerVblank)
            Array.Copy(_vblank, first * 2, _ring, 0, (FramesPerVblank - first) * 2);
        Volatile.Write(ref _written, w + FramesPerVblank);
    }

    // ---- audio thread ----

    static bool _priming = true;
    static double _fill = TargetFrames;
    static double _pos = 1.0;
    static int _x0L, _x0R, _x1L, _x1R, _x2L, _x2R, _x3L, _x3R;
    static int _lastL, _lastR;

    /// <summary>New output stream: drop stale audio and prime again (before the consumer runs).</summary>
    public static void ResetConsumer()
    {
        Volatile.Write(ref _read, Volatile.Read(ref _written));
        Restart();
    }

    /// <summary>Wait for a full target before playing again.</summary>
    static void Restart()
    {
        _priming = true;
        _pos = 1.0;
        _x0L = _x0R = _x1L = _x1R = _x2L = _x2R = _x3L = _x3R = 0;
        _lastL = _lastR = 0;
    }

    /// <summary>
    /// Fills <paramref name="dst"/> with <paramref name="frames"/> stereo frames
    /// at <paramref name="outRate"/> (Catmull-Rom). Silence while priming or
    /// after the queue runs dry.
    /// </summary>
    public static void ReadResampled(short[] dst, int frames, int outRate)
    {
        long w = Volatile.Read(ref _written);
        long r = _read;
        int buffered = (int)(w - r);

        if (_priming)
        {
            if (buffered < TargetFrames)
            {
                FadeOut(dst, 0, frames);
                _lastL = _lastR = 0;
                return;
            }
            _priming = false;
            _fill = buffered;
        }

        // Way over target (output stalled, burst after resume): jump back.
        if (buffered > TargetFrames * 4)
        {
            r = w - TargetFrames;
            buffered = TargetFrames;
            _fill = TargetFrames;
        }

        _fill += (buffered - _fill) * 0.02;
        double adjust = Math.Clamp((_fill - TargetFrames) / TargetFrames * MaxRateAdjust,
            -MaxRateAdjust, MaxRateAdjust);
        double step = (double)SampleRate / outRate * (1.0 + adjust);

        for (int i = 0; i < frames; i++)
        {
            while (_pos >= 1.0)
            {
                if (r >= w)
                {
                    FadeOut(dst, i, frames);
                    Volatile.Write(ref _read, r);
                    Restart();
                    return;
                }
                int at = (int)(r & Mask) * 2;
                _x0L = _x1L; _x1L = _x2L; _x2L = _x3L; _x3L = _ring[at];
                _x0R = _x1R; _x1R = _x2R; _x2R = _x3R; _x3R = _ring[at + 1];
                r++;
                _pos -= 1.0;
            }
            double t = _pos;
            dst[i * 2] = CatmullRom(_x0L, _x1L, _x2L, _x3L, t);
            dst[i * 2 + 1] = CatmullRom(_x0R, _x1R, _x2R, _x3R, t);
            _pos += step;
        }
        _lastL = dst[frames * 2 - 2];
        _lastR = dst[frames * 2 - 1];
        Volatile.Write(ref _read, r);
    }

    /// <summary>Ramp from the last output frame to silence instead of a hard click.</summary>
    static void FadeOut(short[] dst, int from, int frames)
    {
        int lastL = from > 0 ? dst[from * 2 - 2] : _lastL;
        int lastR = from > 0 ? dst[from * 2 - 1] : _lastR;
        const int Ramp = 64;
        for (int i = from; i < frames; i++)
        {
            int k = Math.Max(0, Ramp - (i - from + 1));
            dst[i * 2] = (short)(lastL * k / Ramp);
            dst[i * 2 + 1] = (short)(lastR * k / Ramp);
        }
    }

    static short CatmullRom(int x0, int x1, int x2, int x3, double t)
    {
        double c1 = 0.5 * (x2 - x0);
        double c2 = x0 - 2.5 * x1 + 2.0 * x2 - 0.5 * x3;
        double c3 = 0.5 * (x3 - x0) + 1.5 * (x1 - x2);
        double y = ((c3 * t + c2) * t + c1) * t + x1;
        return (short)Math.Clamp((int)Math.Round(y), -32768, 32767);
    }
}
