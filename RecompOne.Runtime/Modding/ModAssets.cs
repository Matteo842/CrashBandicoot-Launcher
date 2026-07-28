using System.IO.Compression;
using System.Text.Json.Serialization;

namespace RecompOne.Runtime.Modding;

/// <summary>Declarative asset pack in <c>mod.json</c> (<see cref="ModInfo.Assets"/>).</summary>
public sealed class ModAssets
{
    [JsonPropertyName("textures")] public ModTextureAsset[]? Textures { get; set; }
    [JsonPropertyName("audio")] public ModAudioAsset[]? Audio { get; set; }
    [JsonPropertyName("disc")] public ModDiscAsset[]? Disc { get; set; }

    [JsonIgnore]
    public bool HasAnyDeclared =>
        (Textures is { Length: > 0 }) ||
        (Audio is { Length: > 0 }) ||
        (Disc is { Length: > 0 });
}

public sealed class ModTextureAsset
{
    /// <summary>Catalog texture id.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    /// <summary>Path relative to the mod root (e.g. <c>textures/demo_tile_a.png</c>).</summary>
    [JsonPropertyName("file")] public string File { get; set; } = "";
}

public sealed class ModAudioAsset
{
    /// <summary>Catalog sound id.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    /// <summary>Path relative to the mod root (e.g. <c>audio/spin.wav</c>). WAV→SPU not wired yet.</summary>
    [JsonPropertyName("file")] public string File { get; set; } = "";
}

public sealed class ModDiscAsset
{
    /// <summary>ISO-relative path (e.g. <c>S0/FOO.NSD</c>).</summary>
    [JsonPropertyName("path")] public string Path { get; set; } = "";

    /// <summary>Host file relative to the mod root (e.g. <c>disc/S0/FOO.NSD</c>).</summary>
    [JsonPropertyName("file")] public string File { get; set; } = "";
}

/// <summary>Resolve and read files declared in a mod asset pack (folder or zip).</summary>
public static class ModAssetFiles
{
    /// <summary>
    /// Normalize a mod-relative path: forward slashes, no leading slash, reject <c>..</c>.
    /// Returns empty string if invalid.
    /// </summary>
    public static string NormalizeRel(string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel)) return "";
        var n = rel.Replace('\\', '/').Trim();
        while (n.StartsWith("./")) n = n[2..];
        n = n.Trim('/');
        if (n.Length == 0) return "";
        var parts = n.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            if (p == "." || p == "..") return "";
        }
        return string.Join('/', parts);
    }

    /// <summary>Try read a file under a mod folder or zip. <paramref name="display"/> is a short log path.</summary>
    public static bool TryRead(string sourcePath, string relative, out byte[] data, out string display)
    {
        data = null!;
        display = "";
        var rel = NormalizeRel(relative);
        if (rel.Length == 0 || string.IsNullOrWhiteSpace(sourcePath)) return false;

        if (Directory.Exists(sourcePath))
        {
            var full = Path.GetFullPath(Path.Combine(sourcePath, rel.Replace('/', Path.DirectorySeparatorChar)));
            var root = Path.GetFullPath(sourcePath);
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !full.Equals(root, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!File.Exists(full)) return false;
            try
            {
                data = File.ReadAllBytes(full);
                display = $"{Path.GetFileName(sourcePath)}/{rel}";
                return true;
            }
            catch
            {
                return false;
            }
        }

        if (!File.Exists(sourcePath) || !sourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            using var zip = ZipFile.OpenRead(sourcePath);
            var entry = zip.GetEntry(rel) ?? zip.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').Equals(rel, StringComparison.OrdinalIgnoreCase));
            if (entry == null || entry.FullName.EndsWith('/')) return false;
            using var ms = new MemoryStream((int)entry.Length);
            using (var s = entry.Open()) s.CopyTo(ms);
            data = ms.ToArray();
            display = $"{Path.GetFileName(sourcePath)}:{rel}";
            return true;
        }
        catch
        {
            return false;
        }
    }
}
