using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RecompOne.Runtime.Catalogs;

/// <summary>Content fingerprints for catalog discovery / Resolve matching.</summary>
public static class Fingerprint
{
    public const int HexLength = 16;

    /// <summary>First 16 hex chars of SHA256 over little-endian BGR555 pixels.</summary>
    public static string Sha256Hex(ReadOnlySpan<ushort> pixels)
    {
        if (pixels.IsEmpty) return new string('0', HexLength);
        Span<byte> bytes = pixels.Length <= 4096
            ? stackalloc byte[pixels.Length * 2]
            : new byte[pixels.Length * 2];
        for (int i = 0; i < pixels.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(i * 2, 2), pixels[i]);
        return Sha256Hex(bytes);
    }

    /// <summary>First 16 hex chars of SHA256 over raw bytes.</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data, hash);
        var sb = new StringBuilder(HexLength);
        for (int i = 0; i < HexLength / 2; i++)
            sb.Append(hash[i].ToString("x2"));
        return sb.ToString();
    }
}
