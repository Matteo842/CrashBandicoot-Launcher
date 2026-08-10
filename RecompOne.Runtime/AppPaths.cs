namespace RecompOne.Runtime;

/// <summary>
/// Portable install root = folder of the real .exe (not the single-file extract temp).
/// User data (save/, game/, settings.json) must live here so it survives across runs.
/// </summary>
public static class AppPaths
{
    static string? _rootOverride;

    /// <summary>
    /// Overrides the writable application root. Android hosts must call this
    /// before the runtime is initialized because the APK install directory is
    /// read-only.
    /// </summary>
    public static void SetRoot(string? root)
    {
        _rootOverride = string.IsNullOrWhiteSpace(root)
            ? null
            : Path.GetFullPath(root);
    }

    public static string Root
    {
        get
        {
            if (_rootOverride != null)
                return _rootOverride;

            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                var dir = Path.GetDirectoryName(exe);
                if (!string.IsNullOrWhiteSpace(dir))
                    return Path.GetFullPath(dir);
            }

            return Path.GetFullPath(AppContext.BaseDirectory);
        }
    }

    public static string SaveDir => Path.Combine(Root, "save");
    public static string GameDir => Path.Combine(Root, "game");
    public static string ModsDir => Path.Combine(Root, "mods");
    public static string LogsDir => Path.Combine(Root, "logs");
    /// <summary>Optional loose catalog JSON overrides (levels/textures/sounds.scus94900.json).</summary>
    public static string CatalogDir => Path.Combine(Root, "catalog");
    /// <summary>CatalogDiscovery texture PNG dumps (local only; do not ship).</summary>
    public static string CapturesDir => Path.Combine(Root, "captures");
    public static string TextureCapturesDir => Path.Combine(CapturesDir, "textures");
    public static string SettingsPath => Path.Combine(Root, "settings.json");
    public static string InterfacePath => Path.Combine(Root, "interface.ini");
    public static string CardAPath => Path.Combine(SaveDir, "carda.sav");
    public static string CardBPath => Path.Combine(SaveDir, "cardb.sav");

    /// <summary>True when the exe lives on the Desktop (or inside a folder on the Desktop).</summary>
    public static bool IsOnDesktop
    {
        get
        {
            var root = Root;
            foreach (var desk in DesktopRoots())
            {
                if (string.IsNullOrWhiteSpace(desk)) continue;
                if (IsSameOrUnder(root, desk))
                    return true;
            }
            return false;
        }
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(SaveDir);
        Directory.CreateDirectory(GameDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(ModsDir);
        SeedExampleMods();
        MigrateLegacySaves();
    }

    /// <summary>
    /// Copy bundled example mods into mods/. Missing mods are always seeded.
    /// Stock samples (author CrashBandicoot.Launcher) are refreshed when the
    /// bundled mod.json version is newer, so bugfixes land without manual delete.
    /// </summary>
    static void SeedExampleMods()
    {
        try
        {
            var examples = Path.Combine(Root, "examples", "mods");
            if (!Directory.Exists(examples)) return;

            foreach (var dir in Directory.EnumerateDirectories(examples))
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(name) || name.StartsWith('.')) continue;
                var dest = Path.Combine(ModsDir, name);
                if (File.Exists(dest + ".zip")) continue;

                if (!Directory.Exists(dest))
                {
                    CopyDirectory(dir, dest, overwrite: false);
                    continue;
                }

                if (ShouldRefreshStockSample(dir, dest))
                {
                    CopyDirectory(dir, dest, overwrite: true);
                    ClearModCache(name);
                }
            }

            foreach (var zip in Directory.EnumerateFiles(examples, "*.zip"))
            {
                var dest = Path.Combine(ModsDir, Path.GetFileName(zip));
                if (File.Exists(dest)) continue;
                var folderSibling = Path.Combine(ModsDir, Path.GetFileNameWithoutExtension(zip));
                if (Directory.Exists(folderSibling)) continue;
                File.Copy(zip, dest);
            }
        }
        catch
        {
            // best-effort
        }
    }

    static bool ShouldRefreshStockSample(string srcDir, string destDir)
    {
        try
        {
            var srcJson = Path.Combine(srcDir, "mod.json");
            var destJson = Path.Combine(destDir, "mod.json");
            if (!File.Exists(srcJson) || !File.Exists(destJson)) return false;

            using var srcDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(srcJson));
            using var destDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(destJson));
            var src = srcDoc.RootElement;
            var dest = destDoc.RootElement;

            var author = dest.TryGetProperty("author", out var a) ? a.GetString() : null;
            if (!string.Equals(author, "CrashBandicoot.Launcher", StringComparison.Ordinal))
                return false;

            var srcVer = src.TryGetProperty("version", out var sv) ? sv.GetString() ?? "" : "";
            var destVer = dest.TryGetProperty("version", out var dv) ? dv.GetString() ?? "" : "";
            return !string.Equals(srcVer, destVer, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    static void ClearModCache(string modId)
    {
        try
        {
            var cache = Path.Combine(ModsDir, ".cache");
            if (!Directory.Exists(cache)) return;
            foreach (var stale in Directory.EnumerateFiles(cache, $"{modId}-*.dll"))
                File.Delete(stale);
        }
        catch
        {
            // ignore
        }
    }

    static void CopyDirectory(string src, string dest, bool overwrite)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite);
        }
    }

    static IEnumerable<string> DesktopRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

        string? publicDesk = null;
        try
        {
            publicDesk = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments),
                "..", "Desktop"));
        }
        catch
        {
            // ignore
        }

        if (!string.IsNullOrWhiteSpace(publicDesk))
            yield return publicDesk;
    }

    static bool IsSameOrUnder(string path, string parent)
    {
        try
        {
            var a = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var b = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (a.Equals(b, StringComparison.OrdinalIgnoreCase))
                return true;
            var prefix = b + Path.DirectorySeparatorChar;
            return a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Move root-level carda/b.sav into save/ once.</summary>
    public static void MigrateLegacySaves()
    {
        MigrateOne(Path.Combine(Root, "carda.sav"), CardAPath);
        MigrateOne(Path.Combine(Root, "cardb.sav"), CardBPath);
    }

    static void MigrateOne(string legacy, string dest)
    {
        try
        {
            if (!File.Exists(legacy) || File.Exists(dest)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Move(legacy, dest);
        }
        catch
        {
            // best-effort
        }
    }
}
