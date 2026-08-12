using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RecompOne.Runtime.Catalogs;
using RecompOne.Runtime.Cdrom;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Host;

namespace RecompOne.Runtime.Modding;

public static class ModLoader
{
    sealed record Candidate(ModInfo Info, List<(string Path, string Text)> Sources);
    static readonly List<(ModInfo Info, IMod[] Instances)> _loaded = [];
    static readonly object _reloadGate = new();
    static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>UTC of the last successful <see cref="ReloadAssets"/> (null if never).</summary>
    public static DateTime? LastAssetReloadUtc { get; private set; }

    /// <summary>Texture replacements registered by the last <see cref="ReloadAssets"/>.</summary>
    public static int LastAssetReloadTextureCount { get; private set; }

    /// <summary>Disc remaps active after the last <see cref="ReloadAssets"/>.</summary>
    public static int LastAssetReloadDiscCount { get; private set; }

    public static IReadOnlyList<ModInfo> LoadedMods
    {
        get { lock (_loaded) return _loaded.Select(l => l.Info).ToArray(); }
    }

    /// <summary>Read mod.json manifests under mods/ without compiling sources.</summary>
    public static IReadOnlyList<ModInfo> DiscoverInfos(string? root = null)
    {
        root ??= AppPaths.ModsDir;
        if (!Directory.Exists(root)) return [];

        var list = new List<ModInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (Path.GetFileName(dir).StartsWith('.')) continue;
            var jsonPath = Path.Combine(dir, "mod.json");
            if (!File.Exists(jsonPath)) continue;
            var info = ParseInfo(File.ReadAllText(jsonPath), dir, logErrors: false);
            if (info == null || !seen.Add(info.Id)) continue;
            list.Add(info);
        }

        foreach (var zipPath in Directory.EnumerateFiles(root, "*.zip"))
        {
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                var entry = zip.GetEntry("mod.json");
                if (entry == null) continue;
                using var reader = new StreamReader(entry.Open());
                var info = ParseInfo(reader.ReadToEnd(), zipPath, logErrors: false);
                if (info == null || !seen.Add(info.Id)) continue;
                list.Add(info);
            }
            catch
            {
                // ignore unreadable zips in the launcher list
            }
        }

        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    public static void LoadAll(string? root = null)
    {
        root ??= AppPaths.ModsDir;
        Directory.CreateDirectory(root);
        Catalog.Initialize();
        TextureReplacements.Clear();
        DiscOverlay.Reset();

        var candidates = Discover(root);
        if (candidates.Count == 0)
        {
            InitDiscOverlay();
            AssetHotReload.Start();
            return;
        }

        candidates = FilterActive(candidates);
        if (candidates.Count == 0)
        {
            InitDiscOverlay();
            AssetHotReload.Start();
            return;
        }

        var ordered = Order(candidates);
        if (ordered.Count == 0)
        {
            InitDiscOverlay();
            AssetHotReload.Start();
            return;
        }

        var cacheDir = Path.Combine(root, ".cache");

        ModLoadingPopup.Begin(ordered.Count);

        //load on back thread
        var work = Task.Run(() =>
        {
            for (int i = 0; i < ordered.Count; i++)
            {
                Console.WriteLine($"[Mods] loading {ordered[i].Info.Name}");
                ModLoadingPopup.Update(i, ordered[i].Info.Name);
                LoadMod(ordered[i], cacheDir);
            }
            ModLoadingPopup.Update(ordered.Count, "");
            try { HookManager.Commit(); }
            catch (Exception ex) { Console.Error.WriteLine($"[Mods] hook install failed: {ex.Message}"); }
        });

        if (OperatingSystem.IsAndroid())
        {
            work.GetAwaiter().GetResult();
        }
        else
        {
            while (!work.IsCompleted)
            {
                HostWindow.Pump();
                Thread.Sleep(16);
            }
        }
        ModLoadingPopup.End();

        Console.WriteLine($"[Mods] loaded {_loaded.Count}/{ordered.Count} mod(s), {HookManager.HookedFunctionCount} function(s) hooked");
        InitDiscOverlay();
        AssetHotReload.Start();
    }

    /// <summary>
    /// Re-read <c>mod.json</c> assets for already-loaded mods, clear and re-register
    /// PNG texture replacements and disc overlays (first-wins / load order preserved).
    /// Does <b>not</b> recompile C# or reinstall hooks. Safe to call mid-session.
    /// </summary>
    /// <returns>True when reload completed (even if zero assets).</returns>
    public static bool ReloadAssets()
    {
        lock (_reloadGate)
        {
            ModInfo[] mods;
            lock (_loaded) mods = _loaded.Select(l => l.Info).ToArray();

            foreach (var mod in mods)
                RefreshManifestAssets(mod);

            TextureReplacements.Clear();
            int tex = 0;
            foreach (var mod in mods)
            {
                try { tex += TextureReplacements.RegisterFromMod(mod); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Mods] {mod.Id}: texture reload failed: {ex.Message}");
                }
                WarnAudioAssets(mod);
            }

            InitDiscOverlay();
            int disc = DiscOverlay.RemapCount;

            LastAssetReloadUtc = DateTime.UtcNow;
            LastAssetReloadTextureCount = tex;
            LastAssetReloadDiscCount = disc;

            Console.WriteLine(
                $"[Mods] assets reloaded: {tex} texture replace(s), {disc} disc remap(s)" +
                $" ({mods.Length} mod(s))");

            try
            {
                Event.Dispatch(new AssetsReloadedEvent
                {
                    TextureCount = tex,
                    DiscRemapCount = disc,
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Mods] AssetsReloadedEvent failed: {ex.Message}");
            }

            return true;
        }
    }

    /// <summary>Re-parse <c>mod.json</c> from disk/zip so <see cref="ModInfo.Assets"/> picks up edits.</summary>
    static void RefreshManifestAssets(ModInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.SourcePath)) return;
        try
        {
            string? json = null;
            if (Directory.Exists(info.SourcePath))
            {
                var path = Path.Combine(info.SourcePath, "mod.json");
                if (!File.Exists(path)) return;
                json = File.ReadAllText(path);
            }
            else if (File.Exists(info.SourcePath)
                     && info.SourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var zip = ZipFile.OpenRead(info.SourcePath);
                var entry = zip.GetEntry("mod.json");
                if (entry == null) return;
                using var reader = new StreamReader(entry.Open());
                json = reader.ReadToEnd();
            }

            if (json == null) return;
            var fresh = JsonSerializer.Deserialize<ModInfo>(json, _json);
            if (fresh == null) return;

            // Keep id / SourcePath stable; refresh declarative pack + metadata.
            info.Assets = fresh.Assets;
            if (!string.IsNullOrWhiteSpace(fresh.Name)) info.Name = fresh.Name;
            if (!string.IsNullOrWhiteSpace(fresh.Version)) info.Version = fresh.Version;
            if (fresh.Author != null) info.Author = fresh.Author;
            if (fresh.Dependencies != null) info.Dependencies = fresh.Dependencies;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Mods] {info.Id}: failed to refresh mod.json: {ex.Message}");
        }
    }

    static void InitDiscOverlay()
    {
        var cd = Runtime.Cd;
        if (cd == null) return;
        try { DiscOverlay.Initialize(cd.Fs, LoadedMods); }
        catch (Exception ex) { Console.Error.WriteLine($"[Disc] overlay init failed: {ex.Message}"); }
    }

    /// <summary>
    /// Until the launcher saves Mods once (<see cref="GameConfig.ModsConfigured"/>),
    /// load everything. After that, only ids in ActiveMods (empty = none).
    /// Dependencies of enabled mods are kept so Order can still resolve them.
    /// </summary>
    static List<Candidate> FilterActive(List<Candidate> candidates)
    {
        if (!ConfigManager.Game.ModsConfigured)
            return candidates;

        var active = ConfigManager.Game.ActiveMods ?? [];
        var enabled = new HashSet<string>(
            active.Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.OrdinalIgnoreCase);

        if (enabled.Count == 0)
        {
            foreach (var c in candidates)
                Console.WriteLine($"[Mods] {c.Info.Id}: disabled in ActiveMods, skipping");
            return [];
        }

        var byId = candidates.ToDictionary(c => c.Info.Id, StringComparer.OrdinalIgnoreCase);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in enabled)
        {
            if (!byId.ContainsKey(id))
            {
                Console.WriteLine($"[Mods] active id '{id}' not found under mods/, ignoring");
                continue;
            }
            CollectWithDeps(id, byId, keep);
        }

        var filtered = new List<Candidate>();
        foreach (var c in candidates)
        {
            if (keep.Contains(c.Info.Id))
            {
                filtered.Add(c);
                continue;
            }
            Console.WriteLine($"[Mods] {c.Info.Id}: disabled in ActiveMods, skipping");
        }
        return filtered;
    }

    static void CollectWithDeps(
        string id,
        Dictionary<string, Candidate> byId,
        HashSet<string> keep)
    {
        if (!keep.Add(id)) return;
        if (!byId.TryGetValue(id, out var mod)) return;
        foreach (var dep in mod.Info.Dependencies)
        {
            if (byId.ContainsKey(dep))
                CollectWithDeps(dep, byId, keep);
        }
    }

    static List<Candidate> Discover(string root)
    {
        var list = new List<Candidate>();

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (Path.GetFileName(dir).StartsWith('.')) continue;
            var jsonPath = Path.Combine(dir, "mod.json");
            if (!File.Exists(jsonPath))
            {
                Console.Error.WriteLine($"[Mods] mod.json not found for {Path.GetFileName(dir)}, skipping");
                continue;
            }
            var info = ParseInfo(File.ReadAllText(jsonPath), dir);
            if (info == null) continue;

            var sources = new List<(string, string)>();
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(dir, file);
                if (rel.Split(Path.DirectorySeparatorChar).Any(p => p is "obj" or "bin" || p.StartsWith('.'))) continue;
                sources.Add((file, File.ReadAllText(file)));
            }
            list.Add(new Candidate(info, sources));
        }

        foreach (var zipPath in Directory.EnumerateFiles(root, "*.zip"))
        {
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                var entry = zip.GetEntry("mod.json");
                if (entry == null)
                {
                    Console.Error.WriteLine($"[Mods] mod.json not found for {Path.GetFileName(zipPath)}, skipping");
                    continue;
                }
                using var reader = new StreamReader(entry.Open());
                var info = ParseInfo(reader.ReadToEnd(), zipPath);
                if (info == null) continue;

                var sources = new List<(string, string)>();
                foreach (var e in zip.Entries)
                {
                    if (!e.FullName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                    if (e.FullName.Split('/').Any(p => p is "obj" or "bin" || p.StartsWith('.'))) continue;
                    using var sr = new StreamReader(e.Open());
                    sources.Add((e.FullName, sr.ReadToEnd()));
                }
                list.Add(new Candidate(info, sources));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Mods] failed to read {Path.GetFileName(zipPath)}: {ex.Message}");
            }
        }

        return list;
    }

    static ModInfo? ParseInfo(string json, string sourcePath, bool logErrors = true)
    {
        try
        {
            var info = JsonSerializer.Deserialize<ModInfo>(json, _json);
            if (info == null || string.IsNullOrWhiteSpace(info.Id))
            {
                if (logErrors)
                    Console.Error.WriteLine($"[Mods] malformed mod.json for {Path.GetFileName(sourcePath)}: missing id, skipping");
                return null;
            }
            if (string.IsNullOrWhiteSpace(info.Name)) info.Name = info.Id;
            info.SourcePath = sourcePath;
            return info;
        }
        catch (Exception ex)
        {
            if (logErrors)
                Console.Error.WriteLine($"[Mods] malformed mod.json for {Path.GetFileName(sourcePath)}: {ex.Message}");
            return null;
        }
    }

    static List<Candidate> Order(List<Candidate> mods)
    {
        var byId = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in mods)
        {
            if (!byId.TryAdd(mod.Info.Id, mod))
                Console.Error.WriteLine($"[Mods] duplicate mod id {mod.Info.Id} at {mod.Info.SourcePath}, skipping");
        }

        var queue = byId.Values.OrderBy(m => m.Info.Id, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var mod in queue.ToList())
        {
            var missing = mod.Info.Dependencies.FirstOrDefault(d => !byId.ContainsKey(d));
            if (missing != null)
            {
                Console.Error.WriteLine($"[Mods] {mod.Info.Id}: missing dependency {missing}, skipping");
                queue.Remove(mod);
            }
        }

        var result = new List<Candidate>();
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0)
        {
            var next = queue.FirstOrDefault(m => m.Info.Dependencies.All(placed.Contains));
            if (next == null)
            {
                foreach (var mod in queue)
                    Console.Error.WriteLine($"[Mods] {mod.Info.Id}: dependency cycle, skipping");
                break;
            }
            queue.Remove(next);
            placed.Add(next.Info.Id);
            result.Add(next);
        }
        return result;
    }

    static void LoadMod(Candidate mod, string cacheDir)
    {
        try
        {
            if (mod.Sources.Count == 0)
            {
                if (!HasLoadableAssets(mod.Info))
                {
                    Console.Error.WriteLine($"[Mods] {mod.Info.Id}: no source files, skipping");
                    return;
                }

                lock (_loaded) _loaded.Add((mod.Info, []));
                int assetTex = TextureReplacements.RegisterFromMod(mod.Info);
                WarnAudioAssets(mod.Info);
                string kind = DescribeAssetKind(mod.Info);
                Console.WriteLine($"[Mods] {mod.Info.Id} v{mod.Info.Version}: asset-only ({kind})" +
                    (assetTex > 0 ? $", {assetTex} texture replace(s)" : ""));
                return;
            }

            var cachePath = Path.Combine(cacheDir, $"{mod.Info.Id}-{CacheKey(mod)}.dll");
            byte[]? bytes;
            if (File.Exists(cachePath))
            {
                Console.WriteLine($"[Mods] {mod.Info.Id} is already cached");
                bytes = File.ReadAllBytes(cachePath);
            }
            else
            {
                Console.WriteLine($"[Mods] building {mod.Info.Id}...");
                bytes = ModCompiler.Compile(mod.Info.Id, mod.Sources);
                if (bytes == null) return;
                try
                {
                    Directory.CreateDirectory(cacheDir);
                    foreach (var stale in Directory.EnumerateFiles(cacheDir, $"{mod.Info.Id}-*.dll"))
                        File.Delete(stale);
                    File.WriteAllBytes(cachePath, bytes);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Mods] failed to cache {mod.Info.Id}: {ex.Message}");
                }
            }

            var alc = new AssemblyLoadContext($"mod-{mod.Info.Id}", isCollectible: true);
            using var ms = new MemoryStream(bytes);
            var asm = alc.LoadFromStream(ms);

            int hooks = RegisterHooks(mod.Info, asm);
            var instances = CreateInstances(mod.Info, asm);
            lock (_loaded) _loaded.Add((mod.Info, instances));
            int codeTex = TextureReplacements.RegisterFromMod(mod.Info);
            WarnAudioAssets(mod.Info);
            foreach (var inst in instances) inst.OnLoad();
            Console.WriteLine($"[Mods] {mod.Info.Id} v{mod.Info.Version}: {hooks} hook(s)" +
                (codeTex > 0 ? $", {codeTex} texture replace(s)" : ""));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Mods] failed to load {mod.Info.Id}: {ex.Message}");
        }
    }

    static int RegisterHooks(ModInfo info, Assembly asm)
    {
        int count = 0;
        foreach (var type in asm.GetTypes())
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        foreach (var attr in method.GetCustomAttributes<FunctionHookAttribute>())
        {
            var target = SymbolRegistry.Resolve(attr.Overlay, attr.Function, attr.Address);
            if (target == null)
            {
                var what = attr.Function ?? $"0x{attr.Address:X8}";
                Console.Error.WriteLine($"[Mods] {info.Id}: function not found: {attr.Overlay}/{what}");
                continue;
            }

            bool ok = attr switch
            {
                ReplaceAttribute => HookManager.AddReplace(info, target, method),
                PreHookAttribute => HookManager.AddPre(info, target, method),
                PostHookAttribute => HookManager.AddPost(info, target, method),
                _ => false
            };
            if (ok) count++;
        }
        return count;
    }

    static string CacheKey(Candidate mod)
    {
        var sb = new StringBuilder();
        sb.Append(typeof(ModLoader).Assembly.ManifestModule.ModuleVersionId);
        var entry = Assembly.GetEntryAssembly();
        if (entry != null) sb.Append(entry.ManifestModule.ModuleVersionId);
        foreach (var (path, text) in mod.Sources.OrderBy(s => s.Path, StringComparer.Ordinal))
        {
            sb.Append(Path.GetFileName(path));
            sb.Append(text);
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    static IMod[] CreateInstances(ModInfo info, Assembly asm)
    {
        var instances = new List<IMod>();
        foreach (var type in asm.GetTypes())
        {
            if (type.IsAbstract || !typeof(IMod).IsAssignableFrom(type)) continue;
            try
            {
                if (Activator.CreateInstance(type) is IMod mod) instances.Add(mod);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Mods] {info.Id}: failed to create {type.Name}: {ex.Message}");
            }
        }
        return instances.ToArray();
    }

    static bool HasLoadableAssets(ModInfo info)
    {
        if (info.Assets != null)
            return info.Assets.HasAnyDeclared;
        return HasDiscFolder(info.SourcePath) || HasTexturesFolder(info.SourcePath);
    }

    static string DescribeAssetKind(ModInfo info)
    {
        if (info.Assets != null)
        {
            bool t = info.Assets.Textures is { Length: > 0 };
            bool d = info.Assets.Disc is { Length: > 0 };
            bool a = info.Assets.Audio is { Length: > 0 };
            var parts = new List<string>();
            if (t) parts.Add("textures");
            if (d) parts.Add("disc");
            if (a) parts.Add("audio");
            return parts.Count > 0 ? "manifest: " + string.Join('+', parts) : "manifest";
        }

        bool hasDisc = HasDiscFolder(info.SourcePath);
        bool hasTex = HasTexturesFolder(info.SourcePath);
        if (hasDisc && hasTex) return "disc+textures";
        if (hasTex) return "textures";
        return "disc overlay";
    }

    static void WarnAudioAssets(ModInfo info)
    {
        var audio = info.Assets?.Audio;
        if (audio == null || audio.Length == 0) return;
        Console.WriteLine(
            $"[Mods] {info.Id}: {audio.Length} audio asset(s) declared — WAV→SPU not implemented yet (ignored)");
    }

    static bool HasDiscFolder(string sourcePath)
        => HasAssetFolder(sourcePath, "disc");

    static bool HasTexturesFolder(string sourcePath)
        => HasAssetFolder(sourcePath, "textures");

    static bool HasAssetFolder(string sourcePath, string folder)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return false;
        if (Directory.Exists(sourcePath))
        {
            var dir = Path.Combine(sourcePath, folder);
            if (!Directory.Exists(dir)) return false;
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any();
        }
        if (!File.Exists(sourcePath) || !sourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return false;
        string prefix = folder + "/";
        try
        {
            using var zip = ZipFile.OpenRead(sourcePath);
            return zip.Entries.Any(e =>
            {
                var n = e.FullName.Replace('\\', '/');
                return n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !n.EndsWith('/');
            });
        }
        catch
        {
            return false;
        }
    }
}
