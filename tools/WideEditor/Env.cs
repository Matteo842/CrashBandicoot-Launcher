// Where things are: the repository, the launcher's settings (disc path), the
// game's output folders (for live reload) and its quick-save slots.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json;

namespace WideEditor;

static class Env
{
    public static readonly string? Repo = FindRepo();

    static string? FindRepo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CrashBandicoot-Launcher.sln"))) return dir.FullName;
        return null;
    }

    /// <summary>The game's build folders (Release first): live edits are copied there.</summary>
    public static IEnumerable<string> GameDirs()
    {
        if (Repo == null) yield break;
        foreach (var config in new[] { "Release", "Debug" })
        {
            string dir = Path.Combine(Repo, "CrashBandicoot.Launcher", "bin", config, "net10.0-windows");
            if (File.Exists(Path.Combine(dir, "CrashBandicoot.exe")) || File.Exists(Path.Combine(dir, "CrashBandicoot.dll")))
                yield return dir;
        }
    }

    /// <summary>Built into the game (embedded resource of RecompOne.Runtime).</summary>
    public static string EditsFile(int level) => Repo != null
        ? Path.Combine(Repo, "RecompOne.Runtime", "WideEdits", $"level_{level:D2}.json")
        : Path.Combine(AppContext.BaseDirectory, "WideEdits", $"level_{level:D2}.json");

    public static IEnumerable<string> LiveFiles(int level) =>
        GameDirs().Select(d => Path.Combine(d, "WideEdits", $"level_{level:D2}.json"));

    public static string? DiscPath(string? saved)
    {
        if (saved != null && File.Exists(saved)) return saved;
        foreach (var dir in GameDirs())
        {
            try
            {
                string file = Path.Combine(dir, "settings.json");
                if (!File.Exists(file)) continue;
                using var d = JsonDocument.Parse(File.ReadAllText(file));
                if (d.RootElement.TryGetProperty("CdPath", out var cd) && File.Exists(cd.GetString())) return cd.GetString();
            }
            catch (Exception e) when (e is IOException or JsonException) { }
        }
        if (Repo != null)
            foreach (var f in Directory.EnumerateFiles(Repo, "*.chd").Concat(Directory.EnumerateFiles(Repo, "*.cue")))
                return f;
        return null;
    }
}

/// <summary>Camera of a game quick-save slot (same fields as tools state.py).</summary>
sealed record SaveSlot(string File, string Name, int Level, Vector3 Cam, short[] Matrix, string Zone)
{
    public static List<SaveSlot> All()
    {
        var slots = new List<SaveSlot>();
        foreach (var dir in Env.GameDirs())
        {
            string states = Path.Combine(dir, "save", "states");
            if (!Directory.Exists(states)) continue;
            foreach (var f in Directory.EnumerateFiles(states, "*.state").OrderBy(f => f))
                if (Read(f) is { } s) slots.Add(s);
        }
        return slots;
    }

    static SaveSlot? Read(string file)
    {
        try
        {
            byte[] data = System.IO.File.ReadAllBytes(file);
            int level = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
            using var z = new ZLibStream(new MemoryStream(data, 24, data.Length - 24), CompressionMode.Decompress);
            using var raw = new MemoryStream();
            z.CopyTo(raw);
            byte[] r = raw.ToArray();
            const int off = 156;
            int ramLength = BinaryPrimitives.ReadInt32LittleEndian(r.AsSpan(off));
            byte[] ram = r.AsSpan(off + 4, ramLength).ToArray();
            uint U32(uint a) => BinaryPrimitives.ReadUInt32LittleEndian(ram.AsSpan((int)(a & 0x1FFFFF)));
            int S32(uint a) => (int)U32(a);
            short S16(uint a) => BinaryPrimitives.ReadInt16LittleEndian(ram.AsSpan((int)(a & 0x1FFFFF)));
            uint ze = U32(0x80057914);
            string zone = (ze & 0xFFE00000) == 0x80000000 ? Eid.Str(U32(ze + 4)) : "";
            var cam = new Vector3(S32(0x80057864) >> 8, S32(0x80057868) >> 8, S32(0x8005786C) >> 8);
            var matrix = new short[9];
            for (int i = 0; i < 9; i++) matrix[i] = S16(0x800577E4u + (uint)i * 2);
            var info = new FileInfo(file);
            return new SaveSlot(file, $"{Path.GetFileNameWithoutExtension(file)} ({info.LastWriteTime:dd/MM HH:mm})",
                level, cam, matrix, zone);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentOutOfRangeException
            or IndexOutOfRangeException)
        {
            return null;
        }
    }
}
