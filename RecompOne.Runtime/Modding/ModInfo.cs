using System.Text.Json.Serialization;

namespace RecompOne.Runtime.Modding;

public sealed class ModInfo
{
    /// <summary>Unique id of the mod</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    /// <summary>Display name of the mod</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>Version string of the mod</summary>
    [JsonPropertyName("version")] public string Version { get; set; } = "";

    /// <summary>Author of the mod</summary>
    [JsonPropertyName("author")] public string Author { get; set; } = "";

    /// <summary>Ids of mods that must load before this one</summary>
    [JsonPropertyName("dependencies")] public string[] Dependencies { get; set; } = [];

    /// <summary>
    /// Optional launcher group: <c>gameplay</c>, <c>assets</c>, <c>stub</c> (samples), etc.
    /// When empty, the launcher infers <c>stub</c> from id/name, else <c>installed</c>.
    /// </summary>
    [JsonPropertyName("category")] public string Category { get; set; } = "";

    /// <summary>
    /// Optional declarative asset pack. When present, only listed textures / disc / audio
    /// are registered (no <c>textures/</c> or <c>disc/</c> folder scan). When null/absent,
    /// legacy folder scan applies.
    /// </summary>
    [JsonPropertyName("assets")] public ModAssets? Assets { get; set; }

    /// <summary>Folder or zip this mod was loaded from</summary>
    [JsonIgnore] public string SourcePath { get; internal set; } = "";

    /// <summary>Resolved launcher category (explicit or inferred).</summary>
    [JsonIgnore]
    public string ResolvedCategory
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Category))
                return Category.Trim().ToLowerInvariant();
            if (Id.EndsWith("-stub", StringComparison.OrdinalIgnoreCase)
                || Name.Contains("Stub", StringComparison.OrdinalIgnoreCase))
                return "stub";
            return "installed";
        }
    }
}
