namespace RecompOne.Runtime.Catalogs;

public enum LevelKind
{
    Empty,
    Gameplay,
    TitleMap,
    Complete,
    Cinema,
    Bonus,
    Unused,
}

/// <summary>Stable level entry from the levels catalog (SCUS-94900).</summary>
public sealed class LevelInfo
{
    public required uint Id { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public required LevelKind Kind { get; init; }
    /// <summary>Single-character NSD/NSF code from cbhacks (e.g. "9", "p", "U").</summary>
    public string? Code { get; init; }

    public bool IsUiOrCinema =>
        Kind is LevelKind.TitleMap or LevelKind.Complete or LevelKind.Cinema;
}
