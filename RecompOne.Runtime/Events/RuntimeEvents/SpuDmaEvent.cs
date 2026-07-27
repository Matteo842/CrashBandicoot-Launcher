namespace RecompOne.Runtime.Events;

/// <summary>
/// Fires on DMA channel 4 main-RAM → SPU RAM uploads (sample / VAB / sequence data).
/// Mutate <see cref="Data"/> in place before it is written into SPU RAM.
/// </summary>
public sealed class SpuDmaEvent : GameEvent
{
    /// <summary>Destination byte address in the 512 KiB SPU RAM.</summary>
    public uint SpuAddr;

    /// <summary>Source address in main RAM (DMA MADR).</summary>
    public uint MainRamAddr;

    /// <summary>Upload payload (mutable). Shared buffer — copy if you need to keep it.</summary>
    public byte[] Data = null!;

    public int Length;
}
