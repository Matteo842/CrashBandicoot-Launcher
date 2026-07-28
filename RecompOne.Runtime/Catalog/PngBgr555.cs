using StbImageSharp;

namespace RecompOne.Runtime.Catalogs;

/// <summary>Decode PNG (RGBA8) into PS1-style BGR555 words (R in bits 0–4, G 5–9, B 10–14).</summary>
public static class PngBgr555
{
    /// <summary>
    /// Decode PNG bytes to a BGR555 buffer sized <paramref name="destW"/>×<paramref name="destH"/>.
    /// Nearest-neighbor scales when the PNG size differs. Alpha &lt; 128 → transparent (0).
    /// </summary>
    public static bool TryDecode(
        ReadOnlySpan<byte> pngBytes, int destW, int destH, Span<ushort> dest)
    {
        if (destW <= 0 || destH <= 0 || dest.Length < destW * destH) return false;
        if (pngBytes.IsEmpty) return false;

        ImageResult image;
        try
        {
            image = ImageResult.FromMemory(pngBytes.ToArray(), ColorComponents.RedGreenBlueAlpha);
        }
        catch
        {
            return false;
        }

        if (image.Width <= 0 || image.Height <= 0 || image.Data == null) return false;
        return BlitRgbaToBgr555(image.Data, image.Width, image.Height, destW, destH, dest);
    }

    public static bool TryDecodeFile(string path, int destW, int destH, Span<ushort> dest)
    {
        try
        {
            if (!File.Exists(path)) return false;
            return TryDecode(File.ReadAllBytes(path), destW, destH, dest);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Decode PNG to native size (caller owns the returned buffer).</summary>
    public static bool TryDecodeNative(ReadOnlySpan<byte> pngBytes, out ushort[] pixels, out int w, out int h)
    {
        pixels = [];
        w = h = 0;
        if (pngBytes.IsEmpty) return false;

        ImageResult image;
        try
        {
            image = ImageResult.FromMemory(pngBytes.ToArray(), ColorComponents.RedGreenBlueAlpha);
        }
        catch
        {
            return false;
        }

        if (image.Width <= 0 || image.Height <= 0 || image.Data == null) return false;
        w = image.Width;
        h = image.Height;
        pixels = new ushort[w * h];
        return BlitRgbaToBgr555(image.Data, w, h, w, h, pixels);
    }

    static bool BlitRgbaToBgr555(
        byte[] rgba, int srcW, int srcH, int destW, int destH, Span<ushort> dest)
    {
        for (int y = 0; y < destH; y++)
        {
            int sy = srcH == destH ? y : y * srcH / destH;
            for (int x = 0; x < destW; x++)
            {
                int sx = srcW == destW ? x : x * srcW / destW;
                int si = (sy * srcW + sx) * 4;
                byte r = rgba[si];
                byte g = rgba[si + 1];
                byte b = rgba[si + 2];
                byte a = rgba[si + 3];
                dest[y * destW + x] = a < 128
                    ? (ushort)0
                    : PackBgr555(r, g, b);
            }
        }
        return true;
    }

    /// <summary>Pack 8-bit RGB into a PS1 halfword (same layout as soft VRAM / stubs).</summary>
    public static ushort PackBgr555(byte r, byte g, byte b)
    {
        int r5 = r >> 3;
        int g5 = g >> 3;
        int b5 = b >> 3;
        return (ushort)(r5 | (g5 << 5) | (b5 << 10));
    }
}
