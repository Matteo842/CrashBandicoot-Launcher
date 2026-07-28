namespace RecompOne.Runtime.Catalogs;

/// <summary>Stable texture id matched by VRAM Load rect and/or pixel hash.</summary>
public sealed class TextureInfo
{
    public required string Id { get; init; }
    public int X { get; init; }
    public int Y { get; init; }
    public int W { get; init; }
    public int H { get; init; }
    /// <summary>Optional SHA256 prefix (16 hex) of BGR555 upload pixels.</summary>
    public string? PixelHash { get; init; }
    /// <summary>When set, match only while the current level id is in this set.</summary>
    public IReadOnlyList<uint>? LevelIds { get; init; }
}
