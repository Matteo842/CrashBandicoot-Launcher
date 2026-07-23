using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RecompOne.Runtime.Host;

namespace RecompOne.Runtime.Modding;

public static class ModLoader
{
    sealed record Candidate(ModInfo Info, List<(string Path, string Text)> Sources);
    static readonly List<(ModInfo Info, IMod[] Instances)> _loaded = [];
    static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
    
    public static IReadOnlyList<ModInfo> LoadedMods
    {
        get { lock (_loaded) return _loaded.Select(l => l.Info).ToArray(); }
    }


    public static void LoadAll(string? root = null)
    {
        root ??= Path.GetFullPath("mods");
        Directory.CreateDirectory(root);

        var candidates = Discover(root);
        if (candidates.Count == 0) return;

        var ordered = Order(candidates);
        if (ordered.Count == 0) return;

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

        while (!work.IsCompleted)
        {
            HostWindow.Pump();
            Thread.Sleep(16);
        }
        ModLoadingPopup.End();

        Console.WriteLine($"[Mods] loaded {_loaded.Count}/{ordered.Count} mod(s), {HookManager.HookedFunctionCount} function(s) hooked");
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

    static ModInfo? ParseInfo(string json, string sourcePath)
    {
        try
        {
            var info = JsonSerializer.Deserialize<ModInfo>(json, _json);
            if (info == null || string.IsNullOrWhiteSpace(info.Id))
            {
                Console.Error.WriteLine($"[Mods] malformed mod.json for {Path.GetFileName(sourcePath)}: missing id, skipping");
                return null;
            }
            if (string.IsNullOrWhiteSpace(info.Name)) info.Name = info.Id;
            info.SourcePath = sourcePath;
            return info;
        }
        catch (Exception ex)
        {
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
                Console.Error.WriteLine($"[Mods] {mod.Info.Id}: no source files, skipping");
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
            foreach (var inst in instances) inst.OnLoad();
            Console.WriteLine($"[Mods] {mod.Info.Id} v{mod.Info.Version}: {hooks} hook(s)");
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
}
