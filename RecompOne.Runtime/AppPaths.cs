namespace RecompOne.Runtime;

/// <summary>
/// Portable install root = folder of the real .exe (not the single-file extract temp).
/// User data (save/, game/, settings.json) must live here so it survives across runs.
/// </summary>
public static class AppPaths
{
    public static string Root
    {
        get
        {
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
        // mods/ intentionally not created while the Mods menu is disabled
        MigrateLegacySaves();
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
