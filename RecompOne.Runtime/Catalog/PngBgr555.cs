using StbImageSharp;
using StbImageWriteSharp;

namespace RecompOne.Runtime.Catalogs;

/// <summary>Decode/encode PNG (RGBA8) ↔ PS1-style BGR555 words (R in bits 0–4, G 5–9, B 10–14).</summary>
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
            image = ImageResult.FromMemory(pngBytes.ToArray(), StbImageSharp.ColorComponents.RedGreenBlueAlpha);
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
            image = ImageResult.FromMemory(pngBytes.ToArray(), StbImageSharp.ColorComponents.RedGreenBlueAlpha);
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

    /// <summary>Unpack a PS1 BGR555 halfword to 8-bit RGB (replicate high bits into low).</summary>
    public static void UnpackBgr555(ushort pixel, out byte r, out byte g, out byte b)
    {
        int r5 = pixel & 0x1F;
        int g5 = (pixel >> 5) & 0x1F;
        int b5 = (pixel >> 10) & 0x1F;
        r = (byte)((r5 << 3) | (r5 >> 2));
        g = (byte)((g5 << 3) | (g5 >> 2));
        b = (byte)((b5 << 3) | (b5 >> 2));
    }

    /// <summary>
    /// Encode BGR555 pixels to PNG bytes (RGB8). Returns null on failure.
    /// Transparent (0) pixels become black RGB.
    /// </summary>
    public static byte[]? TryEncode(ReadOnlySpan<ushort> pixels, int w, int h)
    {
        if (w <= 0 || h <= 0 || pixels.Length < w * h) return null;
        try
        {
            var rgba = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                UnpackBgr555(pixels[i], out byte r, out byte g, out byte b);
                int o = i * 4;
                rgba[o] = r;
                rgba[o + 1] = g;
                rgba[o + 2] = b;
                rgba[o + 3] = 255;
            }

            using var ms = new MemoryStream();
            var writer = new ImageWriter();
            writer.WritePng(rgba, w, h, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Encode BGR555 to a PNG file. Returns false on failure.</summary>
    public static bool TryEncodeFile(string path, ReadOnlySpan<ushort> pixels, int w, int h)
    {
        var png = TryEncode(pixels, w, h);
        if (png == null || png.Length == 0) return false;
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, png);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
