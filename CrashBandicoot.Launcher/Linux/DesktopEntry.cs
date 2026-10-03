using System.Text;

namespace CrashBandicoot.Launcher.Linux;

/// <summary>
/// "Add to app menu": a freedesktop .desktop entry for this binary, so it shows up
/// in the applications menu and in Steam's "Add a Non-Steam Game" list.
/// </summary>
static class DesktopEntry
{
    const string Id = "crash-bandicoot-recompiled";

    public static bool Install(out string detail)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            {
                detail = "the program path is unknown.";
                return false;
            }

            var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (string.IsNullOrWhiteSpace(data))
                data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

            var iconPath = Path.Combine(data, "icons", "hicolor", "256x256", "apps", Id + ".png");
            if (LinuxGui.IconPng() is { } png)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
                File.WriteAllBytes(iconPath, png);
            }

            var appsDir = Path.Combine(data, "applications");
            Directory.CreateDirectory(appsDir);
            var entryPath = Path.Combine(appsDir, Id + ".desktop");
            var sb = new StringBuilder();
            sb.Append("[Desktop Entry]\n");
            sb.Append("Type=Application\n");
            sb.Append("Name=Crash Bandicoot: Recompiled\n");
            sb.Append("Comment=Unofficial fan launcher for Crash Bandicoot (PS1, NTSC-U)\n");
            sb.Append("Exec=").Append(QuoteExec(exe)).Append('\n');
            sb.Append("Path=").Append(EscapeString(Path.GetDirectoryName(exe)!)).Append('\n');
            if (File.Exists(iconPath))
                sb.Append("Icon=").Append(EscapeString(iconPath)).Append('\n');
            sb.Append("Terminal=false\n");
            sb.Append("Categories=Game;\n");
            File.WriteAllText(entryPath, sb.ToString(), new UTF8Encoding(false));

            Console.WriteLine($"[Launcher] wrote {entryPath}");
            detail = entryPath;
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Launcher] desktop entry failed: {ex.Message}");
            detail = ex.Message;
            return false;
        }
    }

    /// <summary>Exec key quoting from the Desktop Entry spec (quote, then escape for a string value).</summary>
    static string QuoteExec(string path)
    {
        var inner = new StringBuilder();
        foreach (var c in path)
        {
            if (c is '"' or '`' or '$' or '\\') inner.Append('\\');
            inner.Append(c == '%' ? "%%" : c.ToString());
        }
        return EscapeString("\"" + inner + "\"");
    }

    static string EscapeString(string value) => value.Replace("\\", "\\\\");
}
