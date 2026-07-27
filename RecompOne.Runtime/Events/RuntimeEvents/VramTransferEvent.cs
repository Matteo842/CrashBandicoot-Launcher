namespace RecompOne.Runtime.Events;

public enum VramTransfer { Load, Store, Move }

/// <summary>
/// Fires on CPU↔VRAM / VRAM↔VRAM transfers (GP0 LoadImage, StoreImage, MoveImage).
/// For <see cref="VramTransfer.Load"/> and <see cref="VramTransfer.Store"/>, mutate
/// <see cref="Pixels"/> in place (BGR555, row-major, length <see cref="PixelCount"/>)
/// before the runtime commits them to soft VRAM and/or the HLE/GL backend.
/// </summary>
public sealed class VramTransferEvent : GameEvent
{
    public int X, Y, W, H;
    /// <summary>Source rect for <see cref="VramTransfer.Move"/> only.</summary>
    public int SrcX, SrcY;
    public VramTransfer Direction;

    /// <summary>
    /// Pixel payload for Load/Store (null for Move). Row-major, <c>W * H</c> entries.
    /// Shared buffer — copy if you need to keep data after the listener returns.
    /// </summary>
    public ushort[]? Pixels;
    public int PixelCount;
}
