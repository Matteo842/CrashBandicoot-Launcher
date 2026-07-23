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

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(SaveDir);
        Directory.CreateDirectory(GameDir);
        Directory.CreateDirectory(ModsDir);
        MigrateLegacySaves();
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
