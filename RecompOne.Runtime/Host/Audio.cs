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

    // ~280 ms of queued audio at 44.1 kHz — enough margin for GC / timer coalescing after long idle.
    const int NumBuffers = 12;
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

    public static bool IsReady => _ready;

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
            if (spu != null && _ready)
                processed = FillBuffers(spu);

            // Sleep longer when the queue is full; stay tight when catching up after underrun.
            Thread.Sleep(processed > 0 ? BusySleepMs : IdleSleepMs);
        }
    }

    static int FillBuffers(Spu spu)
    {
        _al!.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int processed);
        int filled = 0;
        while (processed > 0)
        {
            uint buf = 0;
            _al.SourceUnqueueBuffers(_source, 1, &buf);

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
