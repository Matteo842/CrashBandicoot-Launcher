namespace RecompOne.Runtime.Events;

/// <summary>Dispatches <see cref="VramTransferEvent"/> for GPU image transfers.</summary>
public static class VramTransfers
{
    static readonly VramTransferEvent Instance = new();

    public static bool HasListeners => Event.HasAnyListeners<VramTransferEvent>();

    public static void NotifyLoad(int x, int y, int w, int h, ushort[] pixels, int count)
        => Dispatch(VramTransfer.Load, x, y, w, h, 0, 0, pixels, count);

    public static void NotifyStore(int x, int y, int w, int h, ushort[] pixels, int count)
        => Dispatch(VramTransfer.Store, x, y, w, h, 0, 0, pixels, count);

    public static void NotifyMove(int sx, int sy, int dx, int dy, int w, int h)
        => Dispatch(VramTransfer.Move, dx, dy, w, h, sx, sy, null, 0);

    static void Dispatch(
        VramTransfer dir, int x, int y, int w, int h, int srcX, int srcY,
        ushort[]? pixels, int count)
    {
        if (!HasListeners) return;
        var e = Instance;
        e.Context = Runtime.Cpu!;
        e.Memory = Runtime.Mem!;
        e.Direction = dir;
        e.X = x;
        e.Y = y;
        e.W = w;
        e.H = h;
        e.SrcX = srcX;
        e.SrcY = srcY;
        e.Pixels = pixels;
        e.PixelCount = count;
        Event.Dispatch(e);
    }
}
