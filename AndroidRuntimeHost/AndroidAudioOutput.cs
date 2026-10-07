using Android.Media;
using RecompOne.Runtime;

namespace CrashBandicoot.AndroidRuntime;

/// <summary>
/// Streams the emulated SPU/XA output to an Android <see cref="AudioTrack"/>.
/// Audio is rendered per VBlank into <see cref="SpuStream"/>; this thread only
/// resamples that queue to the device rate and writes it, so the device's
/// pull pattern no longer decides when notes start (#23).
/// </summary>
sealed class AndroidAudioOutput : IDisposable
{
    const string Tag = "CrashAudio";
    const int FramesPerWrite = 256;  // ~5 ms at 48 kHz
    /// <summary>Device-side buffer: covers GC pauses without the 200+ ms deep-buffer latency.</summary>
    const int TrackBufferMs = 40;

    readonly short[] _sampleBuf = new short[FramesPerWrite * 2];
    readonly object _sync = new();
    readonly ManualResetEventSlim _resumeSignal = new(initialState: true);

    AudioTrack? _track;
    Thread? _mixerThread;
    volatile bool _running;
    volatile bool _paused;
    Spu? _spu;
    int _outRate = 48000;
    float _masterVolume = 1f;
    bool _initFailed;

    public void Attach(Spu? spu)
    {
        if (spu == null || ReferenceEquals(_spu, spu))
            return;
        _spu = spu;
        EnsureStarted();
    }

    public void SetMasterVolume(float volume)
    {
        _masterVolume = Math.Clamp(volume, 0f, 1f);
        lock (_sync)
            _track?.SetVolume(_masterVolume);
    }

    public void PauseOutput()
    {
        _paused = true;
        _resumeSignal.Reset();
        lock (_sync) { try { _track?.Pause(); } catch { /* shutting down */ } }
    }

    public void ResumeOutput()
    {
        lock (_sync)
        {
            try { if (_running) _track?.Play(); }
            catch { /* shutting down */ }
        }
        _paused = false;
        _resumeSignal.Set();
    }

    void EnsureStarted()
    {
        lock (_sync)
        {
            if (_running || _initFailed)
                return;
            try
            {
                _outRate = NativeOutputRate();
                int frameBytes = 2 * sizeof(short);
                int minBuffer = AudioTrack.GetMinBufferSize(_outRate, ChannelOut.Stereo, Encoding.Pcm16bit);
                int bufferBytes = Math.Max(minBuffer, _outRate / 10 * frameBytes); // capacity 100 ms

                var attributes = new AudioAttributes.Builder()!
                    .SetUsage(AudioUsageKind.Game)!
                    .SetContentType(AudioContentType.Music)!
                    .Build()!;
                var format = new AudioFormat.Builder()!
                    .SetSampleRate(_outRate)!
                    .SetEncoding(Encoding.Pcm16bit)!
                    .SetChannelMask(ChannelOut.Stereo)!
                    .Build()!;
                var builder = new AudioTrack.Builder()!
                    .SetAudioAttributes(attributes)!
                    .SetAudioFormat(format)!
                    .SetTransferMode(AudioTrackMode.Stream)!
                    .SetBufferSizeInBytes(bufferBytes)!;
                // Default media tracks land on the deep-buffer output (~90 ms
                // HAL latency on Snapdragon). Low-latency keeps SFX responsive.
                if (OperatingSystem.IsAndroidVersionAtLeast(26))
                    builder.SetPerformanceMode(AudioTrackPerformanceMode.LowLatency);
                var track = builder.Build()!;

                if (track.State != AudioTrackState.Initialized)
                    throw new InvalidOperationException($"AudioTrack failed to initialize (state={track.State}).");

                track.SetVolume(_masterVolume);
                if (OperatingSystem.IsAndroidVersionAtLeast(24))
                    track.SetBufferSizeInFrames(Math.Max(FramesPerWrite * 2, _outRate * TrackBufferMs / 1000));
                SpuStream.ResetConsumer();
                SpuStream.Enabled = true;
                _track = track;
                _running = true;
                _mixerThread = new Thread(MixerLoop)
                {
                    IsBackground = true,
                    Name = "spu-mixer-android",
                    Priority = ThreadPriority.AboveNormal,
                };
                _mixerThread.Start();
                track.Play();
                Android.Util.Log.Info(Tag,
                    $"AudioTrack started: {_outRate} Hz stereo, capacity {bufferBytes} B (min {minBuffer} B), " +
                    $"buffer {(OperatingSystem.IsAndroidVersionAtLeast(24) ? track.BufferSizeInFrames : -1)} frames, " +
                    $"perf {(OperatingSystem.IsAndroidVersionAtLeast(26) ? track.PerformanceMode.ToString() : "?")}.");
            }
            catch (Exception ex)
            {
                _initFailed = true;
                _running = false;
                try { _track?.Release(); } catch { /* ignore */ }
                _track = null;
                Android.Util.Log.Error(Tag, $"Audio init failed, game stays silent: {ex}");
            }
        }
    }

    static int NativeOutputRate()
    {
        try
        {
            var manager = Android.App.Application.Context.GetSystemService(Android.Content.Context.AudioService) as AudioManager;
            if (int.TryParse(manager?.GetProperty(AudioManager.PropertyOutputSampleRate), out int rate) &&
                rate >= 8000 && rate <= 192000)
                return rate;
        }
        catch
        {
            // fall back below
        }
        return 48000;
    }

    void MixerLoop()
    {
        try { Android.OS.Process.SetThreadPriority(Android.OS.ThreadPriority.UrgentAudio); }
        catch { /* best effort */ }

        while (_running)
        {
            _resumeSignal.Wait();
            if (!_running) break;

            var track = _track;
            if (track == null)
            {
                Thread.Sleep(5);
                continue;
            }

            SpuStream.ReadResampled(_sampleBuf, FramesPerWrite, _outRate);
            try
            {
                int written = 0;
                while (written < _sampleBuf.Length && _running)
                {
                    int n = track.Write(_sampleBuf, written, _sampleBuf.Length - written);
                    if (n <= 0)
                    {
                        if (_paused) _resumeSignal.Wait();
                        else Thread.Sleep(1);
                        break;
                    }
                    written += n;
                }
            }
            catch (Exception)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        SpuStream.Enabled = false;
        _running = false;
        _resumeSignal.Set();
        lock (_sync)
        {
            try { _mixerThread?.Join(500); } catch { /* ignore */ }
            _mixerThread = null;
            if (_track == null)
                return;
            try { _track.Stop(); } catch { /* ignore */ }
            try { _track.Release(); } catch { /* ignore */ }
            _track = null;
        }
    }
}
