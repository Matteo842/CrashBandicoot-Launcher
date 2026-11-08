using Silk.NET.OpenAL;
using ALDevice = Silk.NET.OpenAL.Device;
using ALCtx = Silk.NET.OpenAL.Context;

namespace RecompOne.Runtime.Host;

internal static unsafe class Audio
{
    static ALContext? _alc;
    static AL? _al;
    static ALDevice* _device;
    static ALCtx* _context;

    // ~186 ms of queued audio at 44.1 kHz — margin for GC / timer coalescing on the
    // mixer thread. SpuStream adds its own ~67 ms queue, so total latency stays at
    // the ~255 ms of the old 12-buffer pull mixer.
    const int NumBuffers = 8;
    const int FramesPerBuffer = 1024;
    const int SampleRate = 44100;

    static uint _source;
    static uint[] _buffers = new uint[NumBuffers];
    static short[] _sampleBuf = new short[FramesPerBuffer * 2];

    static Thread? _mixerThread;
    static Spu? _spu;
    static volatile bool _running;
    static float _masterVolume = 1.0f;
    static bool _ready;
    static volatile bool _paused;

    public static bool IsReady => _ready;

    /// <summary>
    /// Host pause menu: stop the source and stop pulling the SPU, so held notes freeze
    /// instead of droning while the game thread is parked. Resume continues the queue.
    /// </summary>
    public static bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    public static void Initialize()
    {
        const int MaxAttempts = 3;
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                if (TryInitialize())
                    return;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[Host] audio init attempt {attempt}/{MaxAttempts} failed: {e.Message}");
                CleanupPartialInit();
                if (attempt == MaxAttempts)
                    return;
                Thread.Sleep(100 * attempt);
            }
        }
    }

    static bool TryInitialize()
    {
        _alc = ALContext.GetApi(true);
        _al = AL.GetApi(true);

        _device = OpenDeviceWithFallback(_alc);
        if (_device == null)
        {
            Console.Error.WriteLine("[Host] no audio device, audio disabled");
            return false;
        }

        _context = _alc.CreateContext(_device, null);
        if (_context == null || !_alc.MakeContextCurrent(_context))
        {
            Console.Error.WriteLine("[Host] failed to create OpenAL context, audio disabled");
            _alc.CloseDevice(_device);
            _device = null;
            return false;
        }

        _source = _al.GenSource();
        _al.SetSourceProperty(_source, SourceFloat.Gain, _masterVolume);

        fixed (uint* ptr = _buffers)
            _al.GenBuffers(NumBuffers, ptr);

        for (int i = 0; i < _buffers.Length; i++)
        {
            _al.BufferData(_buffers[i], BufferFormat.Stereo16, _sampleBuf, SampleRate);
            uint b = _buffers[i];
            _al.SourceQueueBuffers(_source, 1, &b);
        }

        _al.SourcePlay(_source);

        // #215: render the SPU per VBlank (exactly 735 frames per sequencer tick)
        // instead of mixing 1024-frame blocks on pull. The pull mixer heard each
        // tick at the next block start, so ticks 16.7 ms apart came out 0 / 23 / 46 ms
        // apart and the music sped up and slowed down. Same path as Android (#23).
        SpuStream.ResetConsumer();
        SpuStream.Enabled = true;

        _running = true;
        _ready = true;
        _mixerThread = new Thread(MixerLoop)
        {
            IsBackground = true,
            Name = "spu-mixer",
            Priority = ThreadPriority.AboveNormal,
        };
        _mixerThread.Start();
        return true;
    }

    static void CleanupPartialInit()
    {
        SpuStream.Enabled = false;
        _running = false;
        _ready = false;
        try { _mixerThread?.Join(200); } catch { /* ignore */ }
        _mixerThread = null;
        try
        {
            if (_al != null && _source != 0)
            {
                _al.SourceStop(_source);
                _al.DeleteSource(_source);
                _al.DeleteBuffers(_buffers);
            }
        }
        catch { /* ignore */ }
        _source = 0;
        try
        {
            if (_alc != null)
            {
                if (_context != null) _alc.DestroyContext(_context);
                if (_device != null) _alc.CloseDevice(_device);
            }
        }
        catch { /* ignore */ }
        _context = null;
        _device = null;
        _alc = null;
        _al = null;
    }

    static ALDevice* OpenDeviceWithFallback(ALContext alc)
    {
        // Retry: single-file first launch can race Soft native extract / device ready.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            ALDevice* device = alc.OpenDevice("");
            if (device != null) return device;
            Thread.Sleep(50 * (attempt + 1));
        }

        return null;
    }

    public static void Attach(Spu? spu)
    {
        if (spu != null) _spu = spu;
    }

    public static void SetMasterVolume(float volume)
    {
        _masterVolume = Math.Clamp(volume, 0f, 1f);
        if (_ready && _al != null && _source != 0)
            _al.SetSourceProperty(_source, SourceFloat.Gain, _masterVolume);
    }

    static void MixerLoop()
    {
        // One buffer ≈ 23 ms; wake often enough to refill before the queue drains.
        const int IdleSleepMs = 5;
        const int BusySleepMs = 1;

        while (_running)
        {
            var spu = _spu;
            int processed = 0;
            if (_ready && _paused)
                HoldSource();
            else if (spu != null && _ready)
                processed = FillBuffers(spu);

            // Sleep longer when the queue is full; stay tight when catching up after underrun.
            Thread.Sleep(processed > 0 ? BusySleepMs : IdleSleepMs);
        }
    }

    /// <summary>Paused: FillBuffers sees the source stopped on resume and plays it again.</summary>
    static void HoldSource()
    {
        _al!.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
        if (state == (int)SourceState.Playing)
            _al.SourcePause(_source);
    }

    static int FillBuffers(Spu spu)
    {
        _al!.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int processed);
        int filled = 0;
        while (processed > 0)
        {
            uint buf = 0;
            _al.SourceUnqueueBuffers(_source, 1, &buf);

            if (SpuStream.Enabled)
                SpuStream.ReadResampled(_sampleBuf, FramesPerBuffer, SampleRate);
            else
                spu.Mix(_sampleBuf, FramesPerBuffer);

            _al.BufferData(buf, BufferFormat.Stereo16, _sampleBuf, SampleRate);
            _al.SourceQueueBuffers(_source, 1, &buf);
            processed--;
            filled++;
        }

        _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int queued);
        _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
        if (queued > 0 && state != (int)SourceState.Playing)
            _al.SourcePlay(_source);

        return filled;
    }

    public static void Shutdown()
    {
        if (_alc == null) return;
        SpuStream.Enabled = false;
        _running = false;
        _ready = false;
        _mixerThread?.Join(500);
        if (_al != null && _source != 0)
        {
            _al.SourceStop(_source);
            _al.DeleteSource(_source);
            _al.DeleteBuffers(_buffers);
            _source = 0;
        }
        if (_context != null) _alc.DestroyContext(_context);
        if (_device != null) _alc.CloseDevice(_device);
        _context = null;
        _device = null;
    }
}
