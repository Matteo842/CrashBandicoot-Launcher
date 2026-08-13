using System.IO.Compression;
using System.Text.Json;
using Android.App;
using RecompOne.Runtime;
using RecompOne.Runtime.Modding;

namespace CrashBandicoot.AndroidRuntime;

/// <summary>
/// Seeds bundled samples and installs user zips into the private mods/ folder.
/// </summary>
static class AndroidMods
{
    const string StockAuthor = "CrashBandicoot.Launcher";

    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static void SeedBundledSamples(Activity activity)
    {
        try
        {
            var dest = Path.Combine(AppPaths.Root, "examples", "mods", "auto-spin");
            Directory.CreateDirectory(dest);
            CopyAsset(activity, "ExampleMods/auto-spin/mod.json", Path.Combine(dest, "mod.json"));
            CopyAsset(activity, "ExampleMods/auto-spin/AutoSpinMod.cs", Path.Combine(dest, "AutoSpinMod.cs"));
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("CrashMods", $"Failed to extract bundled mods: {ex.Message}");
        }
    }

    static void CopyAsset(Activity activity, string assetPath, string destPath)
    {
        using var input = activity.Assets?.Open(assetPath)
                          ?? throw new IOException($"Missing asset {assetPath}");
        using var output = File.Create(destPath);
        input.CopyTo(output);
    }

    public static ModInfo ImportFromPath(Activity activity, string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("The selected zip is not readable.", path);
        var temp = Path.Combine(activity.CacheDir!.AbsolutePath, "import-mod.zip");
        File.Copy(path, temp, overwrite: true);
        return ImportTempZip(temp);
    }

    public static ModInfo ImportZip(Activity activity, Android.Net.Uri uri)
    {
        var path = AndroidStorageAccess.TryFilesystemPath(activity, uri);
        if (path != null)
            return ImportFromPath(activity, path);

        var temp = Path.Combine(activity.CacheDir!.AbsolutePath, "import-mod.zip");
        using (var input = activity.ContentResolver?.OpenInputStream(uri)
                           ?? throw new IOException("Unable to open the selected file."))
        using (var output = File.Create(temp))
            input.CopyTo(output);

        return ImportTempZip(temp);
    }

    static ModInfo ImportTempZip(string temp)
    {
        ModInfo info;
        string prefix;
        bool copyAsZip;
        using (var zip = ZipFile.OpenRead(temp))
        {
            foreach (var entry in zip.Entries)
            {
                if (!IsSafeZipPath(entry.FullName))
                    throw new InvalidOperationException("The zip contains an unsafe path.");
            }

            var jsonEntry = FindModJson(zip)
                ?? throw new InvalidOperationException(
                    "No mod.json at the zip root (or in a single top-level folder).");
            string json;
            using (var reader = new StreamReader(jsonEntry.Open()))
                json = reader.ReadToEnd();

            info = JsonSerializer.Deserialize<ModInfo>(json, Json)
                   ?? throw new InvalidOperationException("mod.json is missing a valid id.");
            if (string.IsNullOrWhiteSpace(info.Id))
                throw new InvalidOperationException("mod.json is missing a valid id.");
            info.Id = info.Id.Trim();
            prefix = JsonPrefix(jsonEntry.FullName);
            copyAsZip = string.IsNullOrEmpty(prefix)
                        && jsonEntry.FullName.Equals("mod.json", StringComparison.OrdinalIgnoreCase);

            RemoveExisting(info.Id);
            Directory.CreateDirectory(AppPaths.ModsDir);

            if (!copyAsZip)
                ExtractPrefixed(zip, Path.Combine(AppPaths.ModsDir, info.Id), prefix);
        }

        if (copyAsZip)
            File.Copy(temp, Path.Combine(AppPaths.ModsDir, info.Id + ".zip"), overwrite: true);

        try { File.Delete(temp); } catch { /* ignore */ }
        return info;
    }

    static void ExtractPrefixed(ZipArchive zip, string dest, string prefix)
    {
        Directory.CreateDirectory(dest);
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
                continue;
            var relative = StripPrefix(entry.FullName, prefix);
            if (string.IsNullOrEmpty(relative) || relative.EndsWith('/'))
            {
                if (relative.Length > 1)
                    Directory.CreateDirectory(Path.Combine(dest, relative.TrimEnd('/')));
                continue;
            }

            var target = Path.Combine(dest, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var src = entry.Open();
            using var dst = File.Create(target);
            src.CopyTo(dst);
        }
    }

    public static bool IsStockSample(ModInfo info) =>
        string.Equals(info.Author, StockAuthor, StringComparison.Ordinal);

    public static void Remove(ModInfo info)
    {
        if (IsStockSample(info)) return;
        RemoveExisting(info.Id);
        try
        {
            if (!string.IsNullOrWhiteSpace(info.SourcePath)
                && !string.Equals(Path.GetFileName(info.SourcePath), info.Id, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(info.SourcePath)) File.Delete(info.SourcePath);
                else if (Directory.Exists(info.SourcePath)) Directory.Delete(info.SourcePath, true);
            }
        }
        catch
        {
            // best-effort
        }
    }

    static void RemoveExisting(string id)
    {
        var dir = Path.Combine(AppPaths.ModsDir, id);
        var zip = Path.Combine(AppPaths.ModsDir, id + ".zip");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        if (File.Exists(zip)) File.Delete(zip);

        var cache = Path.Combine(AppPaths.ModsDir, ".cache");
        if (!Directory.Exists(cache)) return;
        foreach (var stale in Directory.EnumerateFiles(cache, $"{id}-*.dll"))
            File.Delete(stale);
    }

    static ZipArchiveEntry? FindModJson(ZipArchive zip)
    {
        var root = zip.GetEntry("mod.json");
        if (root != null) return root;

        ZipArchiveEntry? nested = null;
        string? folder = null;
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (!name.EndsWith("mod.json", StringComparison.OrdinalIgnoreCase)) continue;
            var slash = name.IndexOf('/');
            if (slash < 0 || name.IndexOf('/', slash + 1) >= 0) continue;
            var top = name[..slash];
            if (folder != null && !string.Equals(folder, top, StringComparison.OrdinalIgnoreCase))
                return null;
            folder = top;
            nested = entry;
        }

        return nested;
    }

    static string JsonPrefix(string fullName)
    {
        var name = fullName.Replace('\\', '/').TrimStart('/');
        var slash = name.LastIndexOf('/');
        return slash < 0 ? "" : name[..(slash + 1)];
    }

    static string StripPrefix(string fullName, string prefix)
    {
        var name = fullName.Replace('\\', '/').TrimStart('/');
        if (prefix.Length == 0) return name;
        return name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? name[prefix.Length..]
            : name;
    }

    static bool IsSafeZipPath(string name)
    {
        var n = name.Replace('\\', '/');
        if (n.StartsWith('/') || n.StartsWith("../", StringComparison.Ordinal) || n.Contains("/../"))
            return false;
        return n != ".." && !n.Contains(':');
    }
}
