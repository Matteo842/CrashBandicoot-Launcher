namespace RecompOne.Runtime;

/// <summary>
/// Minimal platform boundary for hosts that cannot use the desktop Silk.NET
/// window and OpenAL stack. The emulated CPU/GPU/CD code remains shared.
/// </summary>
public interface IRuntimePlatformHost
{
    void Initialize(string title);
    void WaitForValidDisc();
    void Present(Gpu? gpu);
    void AttachAudio(Spu? spu);
    void SetMasterVolume(float volume);
    void ShowNotice(string message);
    void Shutdown();
}
