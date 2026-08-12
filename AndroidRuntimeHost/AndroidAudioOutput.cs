using Android.Media;
using RecompOne.Runtime;

namespace CrashBandicoot.AndroidRuntime;

/// <summary>
/// Streams the emulated SPU/XA output to an Android <see cref="AudioTrack"/>.
/// The blocking Write() call paces the mixer thread the same way the OpenAL
/// buffer queue paces the desktop host.
/// </summary>
sealed class AndroidAudioOutput : IDisposable
{
    const string Tag = "CrashAudio";
    const int SampleRate = 44100;
    const int FramesPerBuffer = 1024; // ~23 ms per chunk, same as the desktop host

    readonly short[] _sampleBuf = new short[FramesPerBuffer * 2];
    readonly object _sync = new();
    readonly ManualResetEventSlim _resumeSignal = new(initialState: true);

    AudioTrack? _track;
    Thread? _mixerThread;
    volatile bool _running;
    volatile bool _paused;
    Spu? _spu;
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
                int chunkBytes = FramesPerBuffer * 2 * sizeof(short);
                int minBuffer = AudioTrack.GetMinBufferSize(SampleRate, ChannelOut.Stereo, Encoding.Pcm16bit);
                int bufferBytes = Math.Max(minBuffer, chunkBytes * 6);

                var attributes = new AudioAttributes.Builder()!
                    .SetUsage(AudioUsageKind.Game)!
                    .SetContentType(AudioContentType.Music)!
                    .Build()!;
                var format = new AudioFormat.Builder()!
                    .SetSampleRate(SampleRate)!
                    .SetEncoding(Encoding.Pcm16bit)!
                    .SetChannelMask(ChannelOut.Stereo)!
                    .Build()!;
                var track = new AudioTrack.Builder()!
                    .SetAudioAttributes(attributes)!
                    .SetAudioFormat(format)!
                    .SetTransferMode(AudioTrackMode.Stream)!
                    .SetBufferSizeInBytes(bufferBytes)!
                    .Build()!;

                if (track.State != AudioTrackState.Initialized)
                    throw new InvalidOperationException($"AudioTrack failed to initialize (state={track.State}).");

                track.SetVolume(_masterVolume);
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
                    $"AudioTrack started: {SampleRate} Hz stereo, buffer {bufferBytes} B (min {minBuffer} B).");
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

    void MixerLoop()
    {
        while (_running)
        {
            _resumeSignal.Wait();
            if (!_running) break;

            var spu = _spu;
            var track = _track;
            if (spu == null || track == null)
            {
                Thread.Sleep(5);
                continue;
            }

            spu.Mix(_sampleBuf, FramesPerBuffer);
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
