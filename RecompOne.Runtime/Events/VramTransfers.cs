using RecompOne.Runtime.Catalogs;

namespace RecompOne.Runtime.Events;

/// <summary>Dispatches <see cref="VramTransferEvent"/> for GPU image transfers.</summary>
public static class VramTransfers
{
    static readonly VramTransferEvent Instance = new();

    public static bool HasListeners => Event.HasAnyListeners<VramTransferEvent>();

    /// <summary>
    /// Soft/HLE path must buffer CPU→VRAM Loads when mods listen, discovery is on,
    /// or PNG replacements are registered.
    /// </summary>
    public static bool NeedsLoadIntercept =>
        HasListeners || Catalog.DiscoveryEnabled || TextureReplacements.HasAny;

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
        bool discover = dir == VramTransfer.Load && Catalog.DiscoveryEnabled;
        bool replace = dir == VramTransfer.Load && TextureReplacements.HasAny;
        if (!HasListeners && !discover && !replace) return;

        // Discovery on original pixels (before PNG replace).
        if (discover)
            Catalog.ObserveTextureLoad(x, y, w, h, pixels, count);

        // Catalog id → registered PNG (mutates buffer before mod listeners / WriteVram).
        if (replace)
            TextureReplacements.TryApply(x, y, w, h, pixels, count);

        if (HasListeners)
        {
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
}
