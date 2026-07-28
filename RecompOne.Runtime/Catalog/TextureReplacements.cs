namespace RecompOne.Runtime.Catalogs;

/// <summary>
/// Runtime PNG→BGR555 replacements keyed by catalog texture id.
/// Applied on VRAM Load after discovery / Resolve; see <see cref="TexturesCatalog.Replace"/>.
/// </summary>
public static class TextureReplacements
{
    sealed class Entry
    {
        public required ushort[] Pixels;
        public required int W;
        public required int H;
        public string? Source;
    }

    static readonly object Gate = new();
    static readonly Dictionary<string, Entry> ById = new(StringComparer.OrdinalIgnoreCase);

    public static bool HasAny
    {
        get { lock (Gate) return ById.Count > 0; }
    }

    public static int Count
    {
        get { lock (Gate) return ById.Count; }
    }

    public static void Clear()
    {
        lock (Gate) ById.Clear();
    }

    /// <summary>Register a decoded BGR555 image (native size; scaled to Load rect at apply time).</summary>
    public static void Register(string id, ushort[] pixels, int w, int h, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(id) || pixels == null || w <= 0 || h <= 0) return;
        if (pixels.Length < w * h) return;
        var copy = new ushort[w * h];
        Array.Copy(pixels, copy, w * h);
        lock (Gate)
        {
            ById[id] = new Entry { Pixels = copy, W = w, H = h, Source = source };
        }
    }

    /// <summary>Register only if <paramref name="id"/> is not already taken (folder scan / first mod wins).</summary>
    public static bool TryRegisterFirst(string id, ushort[] pixels, int w, int h, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(id) || pixels == null || w <= 0 || h <= 0) return false;
        if (pixels.Length < w * h) return false;
        var copy = new ushort[w * h];
        Array.Copy(pixels, copy, w * h);
        lock (Gate)
        {
            if (ById.ContainsKey(id)) return false;
            ById[id] = new Entry { Pixels = copy, W = w, H = h, Source = source };
            return true;
        }
    }

    public static bool TryRegisterPng(string id, ReadOnlySpan<byte> pngBytes, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (!PngBgr555.TryDecodeNative(pngBytes, out var pixels, out int w, out int h))
            return false;
        Register(id, pixels, w, h, source);
        return true;
    }

    static bool TryRegisterPngFirst(string id, ReadOnlySpan<byte> pngBytes, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (!PngBgr555.TryDecodeNative(pngBytes, out var pixels, out int w, out int h))
            return false;
        return TryRegisterFirst(id, pixels, w, h, source);
    }

    public static bool TryRegisterPngFile(string id, string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            return TryRegisterPng(id, File.ReadAllBytes(path), path);
        }
        catch
        {
            return false;
        }
    }

    static bool TryRegisterPngFileFirst(string id, string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            return TryRegisterPngFirst(id, File.ReadAllBytes(path), path);
        }
        catch
        {
            return false;
        }
    }

    public static bool Remove(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (Gate) return ById.Remove(id);
    }

    /// <summary>Peek a registered replacement (copy). Used by demos / tooling.</summary>
    public static bool TryGet(string id, out ushort[] pixels, out int w, out int h)
    {
        pixels = [];
        w = h = 0;
        if (string.IsNullOrWhiteSpace(id)) return false;
        lock (Gate)
        {
            if (!ById.TryGetValue(id, out var entry)) return false;
            w = entry.W;
            h = entry.H;
            pixels = new ushort[w * h];
            Array.Copy(entry.Pixels, pixels, w * h);
            return true;
        }
    }

    /// <summary>
    /// If the Load resolves to a catalog id with a registered replacement, overwrite
    /// <paramref name="pixels"/>[0..<paramref name="count"/>) in place (nearest-neighbor scale).
    /// </summary>
    public static bool TryApply(int x, int y, int w, int h, ushort[]? pixels, int count)
    {
        if (pixels == null || count <= 0 || w <= 0 || h <= 0) return false;
        if (count < w * h) return false;
        if (!HasAny) return false;

        if (!Catalog.Textures.TryResolve(x, y, w, h, pixels, count, out var info))
            return false;

        Entry? entry;
        lock (Gate)
        {
            if (!ById.TryGetValue(info.Id, out entry))
                return false;
        }

        var dest = pixels.AsSpan(0, w * h);
        if (entry.W == w && entry.H == h)
        {
            entry.Pixels.AsSpan(0, w * h).CopyTo(dest);
            return true;
        }

        // Nearest-neighbor scale from registered native size into the Load rect.
        for (int row = 0; row < h; row++)
        {
            int sy = row * entry.H / h;
            for (int col = 0; col < w; col++)
            {
                int sx = col * entry.W / w;
                dest[row * w + col] = entry.Pixels[sy * entry.W + sx];
            }
        }
        return true;
    }

    /// <summary>Scan <c>textures/*.png</c> under a mod folder (or zip) and register by file stem = catalog id.</summary>
    public static int RegisterFromModFolder(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return 0;
        int n = 0;
        if (Directory.Exists(sourcePath))
        {
            var texDir = Path.Combine(sourcePath, "textures");
            if (!Directory.Exists(texDir)) return 0;
            foreach (var file in Directory.EnumerateFiles(texDir, "*.png", SearchOption.TopDirectoryOnly))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (TryRegisterPngFileFirst(id, file))
                {
                    n++;
                    Console.WriteLine($"[Textures] replace ← {Path.GetFileName(sourcePath)}/textures/{Path.GetFileName(file)}");
                }
                else if (File.Exists(file))
                {
                    // Decode failure vs already claimed — only log decode failures.
                    lock (Gate)
                    {
                        if (ById.ContainsKey(id)) continue;
                    }
                    Console.Error.WriteLine($"[Textures] failed to decode {file}");
                }
            }
            return n;
        }

        if (!File.Exists(sourcePath) || !sourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return 0;

        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(sourcePath);
            foreach (var e in zip.Entries)
            {
                var name = e.FullName.Replace('\\', '/');
                if (!name.StartsWith("textures/", StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) continue;
                var rest = name["textures/".Length..];
                if (rest.Contains('/')) continue; // top-level only
                var id = Path.GetFileNameWithoutExtension(rest);
                if (string.IsNullOrWhiteSpace(id)) continue;
                using var ms = new MemoryStream();
                using (var s = e.Open()) s.CopyTo(ms);
                if (TryRegisterPngFirst(id, ms.ToArray(), $"{Path.GetFileName(sourcePath)}:{name}"))
                {
                    n++;
                    Console.WriteLine($"[Textures] replace ← {Path.GetFileName(sourcePath)}/{name}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Textures] zip scan failed for {Path.GetFileName(sourcePath)}: {ex.Message}");
        }
        return n;
    }
}
