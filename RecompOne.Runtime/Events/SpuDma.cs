namespace RecompOne.Runtime.Events;

/// <summary>Dispatches <see cref="SpuDmaEvent"/> for SPU DMA uploads.</summary>
public static class SpuDma
{
    static readonly SpuDmaEvent Instance = new();

    public static bool HasListeners => Event.HasAnyListeners<SpuDmaEvent>();

    /// <summary>
    /// Notify listeners and return the (possibly mutated) buffer to write into SPU RAM.
    /// </summary>
    public static byte[] Filter(uint spuAddr, uint mainRamAddr, byte[] data)
    {
        if (!HasListeners || data.Length == 0) return data;
        var e = Instance;
        e.Context = Runtime.Cpu!;
        e.Memory = Runtime.Mem!;
        e.SpuAddr = spuAddr;
        e.MainRamAddr = mainRamAddr;
        e.Data = data;
        e.Length = data.Length;
        Event.Dispatch(e);
        return e.Data ?? data;
    }
}
