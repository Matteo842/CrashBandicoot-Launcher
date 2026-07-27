using RecompOne.Runtime.Events;
using RecompOne.Runtime.Modding;

/// <summary>
/// Demonstrates <see cref="SpuDmaEvent"/> — logs a few main-RAM → SPU RAM DMA uploads.
/// This is the hook for custom SFX / sample patches (your own audio data only).
/// </summary>
public sealed class SpuDmaStubMod : IMod
{
    const int LogLimit = 16;
    int _logged;

    public void OnLoad()
    {
        Event.AddListener<SpuDmaEvent>(OnDma);
        Console.WriteLine("[Mods] spu-dma-stub: listening for SpuDmaEvent (sample uploads to SPU RAM)");
    }

    public void OnUnload() => Event.RemoveListener<SpuDmaEvent>(OnDma);

    void OnDma(SpuDmaEvent e)
    {
        if (_logged >= LogLimit) return;
        _logged++;
        Console.WriteLine(
            $"[SPU] DMA → spu=0x{e.SpuAddr:X5} main=0x{e.MainRamAddr:X8} len={e.Length}");

        // Example (disabled): silence this upload (proves the hook — sounds may glitch).
        // Array.Clear(e.Data, 0, e.Length);
    }
}
