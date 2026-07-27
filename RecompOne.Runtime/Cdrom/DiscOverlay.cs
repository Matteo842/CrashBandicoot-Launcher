using System.IO.Compression;
using RecompOne.Runtime.Modding;

namespace RecompOne.Runtime.Cdrom;

/// <summary>
/// Redirects ISO path / LBA reads to host files under <c>mods/&lt;id&gt;/disc/…</c>.
/// First registered path wins (mod load order). Never writes the user's disc image.
/// </summary>
public static class DiscOverlay
{
    const int SectorUserBytes = 2048;
    /// <summary>Virtual LBAs sit far above any real PS1 data track.</summary>
    const int VirtualLbaBase = 0x200000;

    sealed class OverlayFile
    {
        public required string ModId { get; init; }
        public required string IsoPath { get; init; }
        public required int StartLba { get; init; }
        public required uint Size { get; init; }
        public required int SectorCount { get; init; }
        /// <summary>Host path for folder mods; null when <see cref="Cached"/> is set (zip).</summary>
        public string? HostPath { get; init; }
        public byte[]? Cached { get; init; }
        public readonly object Gate = new();
    }

    static readonly object _gate = new();
    static readonly List<OverlayFile> _byLba = [];
    static readonly Dictionary<string, OverlayFile> _byPath = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, OverlayFile> _byBaseName = new(StringComparer.OrdinalIgnoreCase);
    static int _nextVirtualLba = VirtualLbaBase;
    /// <summary>While building the table, ISO walks must hit the real disc (not this overlay).</summary>
    static volatile bool _building;

    public static int RemapCount
    {
        get { lock (_gate) return _byPath.Count; }
    }

    public static void Reset()
    {
        lock (_gate)
        {
            _byLba.Clear();
            _byPath.Clear();
            _byBaseName.Clear();
            _nextVirtualLba = VirtualLbaBase;
            _building = false;
        }
    }

    /// <summary>
    /// Build remap table from loaded mods (already filtered / topo-sorted).
    /// Missing <c>disc/</c> or ISO paths fail soft.
    /// </summary>
    public static void Initialize(CueFs fs, IReadOnlyList<ModInfo> loadedMods)
    {
        Reset();
        if (loadedMods.Count == 0) return;

        lock (_gate)
        {
            _building = true;
            try
            {
                foreach (var mod in loadedMods)
                    RegisterMod(fs, mod);

                if (_byPath.Count > 0)
                    Console.WriteLine($"[Disc] {_byPath.Count} disc remap(s) active");
            }
            finally
            {
                _building = false;
            }
        }
    }

    static void RegisterMod(CueFs fs, ModInfo mod)
    {
        var source = mod.SourcePath;
        if (string.IsNullOrWhiteSpace(source)) return;

        if (Directory.Exists(source))
        {
            var discRoot = Path.Combine(source, "disc");
            if (!Directory.Exists(discRoot)) return;

            foreach (var file in Directory.EnumerateFiles(discRoot, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(discRoot, file).Replace('\\', '/');
                if (ShouldIgnoreHostFile(rel)) continue;
                TryRegister(fs, mod.Id, rel, hostPath: file, cached: null);
            }
            return;
        }

        if (!File.Exists(source) || !source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            using var zip = ZipFile.OpenRead(source);
            foreach (var entry in zip.Entries)
            {
                if (entry.Length == 0 && string.IsNullOrEmpty(entry.Name)) continue;
                var full = entry.FullName.Replace('\\', '/');
                if (!full.StartsWith("disc/", StringComparison.OrdinalIgnoreCase)) continue;
                var rel = full["disc/".Length..];
                if (string.IsNullOrEmpty(rel) || rel.EndsWith('/')) continue;
                if (ShouldIgnoreHostFile(rel)) continue;

                using var stream = entry.Open();
                using var ms = new MemoryStream((int)entry.Length);
                stream.CopyTo(ms);
                TryRegister(fs, mod.Id, rel, hostPath: null, cached: ms.ToArray());
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Disc] {mod.Id}: failed to read zip disc overlay: {ex.Message}");
        }
    }

    static void TryRegister(CueFs fs, string modId, string isoRel, string? hostPath, byte[]? cached)
    {
        var isoPath = NormalizeIsoPath(isoRel);
        if (isoPath.Length == 0) return;

        if (_byPath.ContainsKey(isoPath))
        {
            Console.WriteLine($"[Disc] skip '{isoPath}' from {modId}: already remapped");
            return;
        }

        if (!fs.LocateWithoutOverlay(isoPath, out int isoLba, out uint isoSize))
        {
            Console.WriteLine($"[Disc] {modId}: '{isoPath}' not on disc, skipped");
            return;
        }

        uint size = cached != null ? (uint)cached.Length : (uint)new FileInfo(hostPath!).Length;
        if (size == 0)
        {
            Console.WriteLine($"[Disc] {modId}: '{isoPath}' is empty, skipped");
            return;
        }

        int overlaySectors = SectorCount(size);
        int isoSectors = SectorCount(isoSize);
        // Same footprint → keep real LBA (Setloc/CdRead stay on the ISO range).
        // Larger files get a virtual LBA so we never collide with neighbors.
        int startLba = overlaySectors <= isoSectors ? isoLba : _nextVirtualLba;
        if (startLba == _nextVirtualLba)
            _nextVirtualLba += overlaySectors + 1;

        var entry = new OverlayFile
        {
            ModId = modId,
            IsoPath = isoPath,
            StartLba = startLba,
            Size = size,
            SectorCount = overlaySectors,
            HostPath = hostPath,
            Cached = cached,
        };

        _byPath[isoPath] = entry;
        _byLba.Add(entry);

        var baseName = BaseName(isoPath);
        _byBaseName.TryAdd(baseName, entry);

        var where = hostPath != null ? RelDisplay(hostPath) : $"zip:{isoPath}";
        Console.WriteLine($"[Disc] remap '{isoPath}' ← {where} ({modId}) lba={startLba} size={size}");
    }

    public static bool TryLocate(string name, out int lba, out uint size)
    {
        lba = 0;
        size = 0;
        if (_building) return false;
        var entry = FindByName(name);
        if (entry == null) return false;
        lba = entry.StartLba;
        size = entry.Size;
        return true;
    }

    public static bool TryReadFile(string path, out byte[] data)
    {
        data = null!;
        if (_building) return false;
        var entry = FindByName(path);
        if (entry == null) return false;
        data = ReadAll(entry);
        return true;
    }

    public static bool TryReadSectorData(int lba, int size, out byte[] data)
    {
        data = null!;
        if (_building) return false;
        var entry = FindByLba(lba);
        if (entry == null) return false;

        int sectorIndex = lba - entry.StartLba;
        data = new byte[size];
        if (sectorIndex < 0 || sectorIndex >= entry.SectorCount)
            return true; // zero-fill past EOF within reserved range

        long fileOff = (long)sectorIndex * SectorUserBytes;
        int userOff = size > SectorUserBytes ? 8 : 0;
        int want = Math.Min(SectorUserBytes, size - userOff);
        if (want <= 0) return true;

        Span<byte> dest = data.AsSpan(userOff, want);
        ReadSlice(entry, fileOff, dest);
        return true;
    }

    static OverlayFile? FindByName(string name)
    {
        lock (_gate)
        {
            if (_byPath.Count == 0) return null;
            var norm = NormalizeIsoPath(name);
            if (norm.Length == 0) return null;
            if (_byPath.TryGetValue(norm, out var hit)) return hit;
            return _byBaseName.TryGetValue(BaseName(norm), out hit) ? hit : null;
        }
    }

    static OverlayFile? FindByLba(int lba)
    {
        lock (_gate)
        {
            foreach (var e in _byLba)
            {
                if (lba >= e.StartLba && lba < e.StartLba + e.SectorCount)
                    return e;
            }
            return null;
        }
    }

    static byte[] ReadAll(OverlayFile entry)
    {
        if (entry.Cached != null) return (byte[])entry.Cached.Clone();
        lock (entry.Gate)
            return File.ReadAllBytes(entry.HostPath!);
    }

    static void ReadSlice(OverlayFile entry, long fileOff, Span<byte> dest)
    {
        if (fileOff >= entry.Size) return;
        int n = (int)Math.Min(dest.Length, entry.Size - fileOff);
        if (n <= 0) return;

        if (entry.Cached != null)
        {
            entry.Cached.AsSpan((int)fileOff, n).CopyTo(dest);
            return;
        }

        lock (entry.Gate)
        {
            using var handle = File.OpenHandle(
                entry.HostPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            RandomAccess.Read(handle, dest[..n], fileOff);
        }
    }

    static int SectorCount(uint size) => (int)((size + SectorUserBytes - 1) / SectorUserBytes);

    static bool ShouldIgnoreHostFile(string rel)
    {
        var name = Path.GetFileName(rel);
        if (string.IsNullOrEmpty(name) || name.StartsWith('.')) return true;
        if (name.Equals(".gitkeep", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("README", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal static string NormalizeIsoPath(string path)
    {
        path = path.Trim();
        int colon = path.IndexOf(':');
        if (colon >= 0) path = path[(colon + 1)..];
        path = path.Replace('\\', '/').Trim('/');
        int semi = path.IndexOf(';');
        if (semi >= 0) path = path[..semi];
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            int s = parts[i].IndexOf(';');
            if (s >= 0) parts[i] = parts[i][..s];
        }
        return string.Join('/', parts);
    }

    static string BaseName(string isoPath)
    {
        int slash = isoPath.LastIndexOf('/');
        return slash >= 0 ? isoPath[(slash + 1)..] : isoPath;
    }

    static string RelDisplay(string hostPath)
    {
        try
        {
            var mods = AppPaths.ModsDir;
            var full = Path.GetFullPath(hostPath);
            if (full.StartsWith(Path.GetFullPath(mods), StringComparison.OrdinalIgnoreCase))
                return Path.GetRelativePath(mods, full).Replace('\\', '/');
        }
        catch { /* fall through */ }
        return hostPath;
    }
}
