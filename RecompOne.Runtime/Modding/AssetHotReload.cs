using RecompOne.Runtime.Config;

namespace RecompOne.Runtime.Modding;

/// <summary>
/// Optional <see cref="FileSystemWatcher"/> over <c>mods/</c> that triggers
/// <see cref="ModLoader.ReloadAssets"/> when texture / disc / <c>mod.json</c> files change.
/// C# sources are ignored (no Roslyn recompile).
/// </summary>
public static class AssetHotReload
{
    const int DebounceMs = 600;

    static readonly object Gate = new();
    static FileSystemWatcher? _watcher;
    static CancellationTokenSource? _debounce;
    static bool _started;

    public static bool IsWatching
    {
        get { lock (Gate) return _watcher != null; }
    }

    /// <summary>Start watching when <see cref="GameConfig.AssetHotWatch"/> is true.</summary>
    public static void Start()
    {
        if (OperatingSystem.IsAndroid())
            return;

        bool enabled;
        try { enabled = ConfigManager.Game.AssetHotWatch; }
        catch { enabled = true; }

        if (!enabled)
        {
            Stop();
            return;
        }

        lock (Gate)
        {
            if (_started && _watcher != null) return;
            StopUnlocked();

            var root = AppPaths.ModsDir;
            try
            {
                Directory.CreateDirectory(root);
                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName
                                   | NotifyFilters.DirectoryName
                                   | NotifyFilters.LastWrite
                                   | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                w.Changed += OnFsEvent;
                w.Created += OnFsEvent;
                w.Deleted += OnFsEvent;
                w.Renamed += OnRenamed;
                w.Error += (_, e) =>
                    Console.Error.WriteLine($"[Mods] asset watch error: {e.GetException().Message}");
                w.EnableRaisingEvents = true;
                _watcher = w;
                _started = true;
                Console.WriteLine("[Mods] asset hot-watch on (mods/ textures+disc+mod.json)");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Mods] asset hot-watch failed to start: {ex.Message}");
                StopUnlocked();
            }
        }
    }

    public static void Stop()
    {
        lock (Gate) StopUnlocked();
    }

    static void StopUnlocked()
    {
        _debounce?.Cancel();
        _debounce?.Dispose();
        _debounce = null;
        if (_watcher != null)
        {
            try
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnFsEvent;
                _watcher.Created -= OnFsEvent;
                _watcher.Deleted -= OnFsEvent;
                _watcher.Renamed -= OnRenamed;
                _watcher.Dispose();
            }
            catch { /* ignore */ }
            _watcher = null;
        }
        _started = false;
    }

    static void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (IsInteresting(e.FullPath) || IsInteresting(e.OldFullPath))
            ScheduleReload($"rename {Path.GetFileName(e.FullPath)}");
    }

    static void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        if (IsInteresting(e.FullPath))
            ScheduleReload($"{e.ChangeType.ToString().ToLowerInvariant()} {RelDisplay(e.FullPath)}");
    }

    static void ScheduleReload(string reason)
    {
        CancellationTokenSource cts;
        lock (Gate)
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
            cts = new CancellationTokenSource();
            _debounce = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceMs, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Console.WriteLine($"[Mods] asset change detected ({reason}) — reloading packs…");
            ModLoader.ReloadAssets();
        });
    }

    /// <summary>
    /// True for <c>mod.json</c>, PNGs under <c>textures/</c>, and files under <c>disc/</c>.
    /// Ignores <c>.cache</c>, C# sources, and junk.
    /// </summary>
    internal static bool IsInteresting(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return false;
        try
        {
            var full = Path.GetFullPath(fullPath);
            var mods = Path.GetFullPath(AppPaths.ModsDir);
            if (!full.StartsWith(mods + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !full.Equals(mods, StringComparison.OrdinalIgnoreCase))
                return false;

            var rel = Path.GetRelativePath(mods, full).Replace('\\', '/');
            if (rel.StartsWith(".cache/", StringComparison.OrdinalIgnoreCase)
                || rel.Equals(".cache", StringComparison.OrdinalIgnoreCase))
                return false;

            var name = Path.GetFileName(rel);
            if (string.IsNullOrEmpty(name) || name.StartsWith('.')) return false;
            if (name.Equals(".gitkeep", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return false;
            if (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return false;

            if (name.Equals("mod.json", StringComparison.OrdinalIgnoreCase)) return true;

            var parts = rel.Split('/');
            // mods/<id>/textures/*.png or mods/<id>.zip (zip rewrite is rare; still watch)
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Equals("textures", StringComparison.OrdinalIgnoreCase))
                    return name.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
                if (parts[i].Equals("disc", StringComparison.OrdinalIgnoreCase))
                    return i < parts.Length - 1; // any file under disc/
                if (parts[i].Equals("audio", StringComparison.OrdinalIgnoreCase))
                    return false; // WAV→SPU not wired
            }

            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && parts.Length == 1)
                return true;

            return false;
        }
        catch
        {
            return false;
        }
    }

    static string RelDisplay(string fullPath)
    {
        try
        {
            return Path.GetRelativePath(AppPaths.ModsDir, fullPath).Replace('\\', '/');
        }
        catch
        {
            return Path.GetFileName(fullPath);
        }
    }
}
