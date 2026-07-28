namespace RecompOne.Runtime.Catalogs;

/// <summary>Stable sound id matched by SPU DMA address/length and/or payload hash.</summary>
public sealed class SoundInfo
{
    public required string Id { get; init; }
    public uint SpuAddr { get; init; }
    public int Length { get; init; }
    /// <summary>Optional SHA256 prefix (16 hex) of the DMA payload.</summary>
    public string? PayloadHash { get; init; }
}
