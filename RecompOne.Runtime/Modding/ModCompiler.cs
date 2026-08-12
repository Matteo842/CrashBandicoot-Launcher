using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace RecompOne.Runtime.Modding;

public static class ModCompiler
{
    static List<MetadataReference>? _references;

    /// <summary>SDK-style implicit usings so sample/user mods need fewer boilerplate imports.</summary>
    const string GlobalUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.Linq;
        global using System.Threading.Tasks;
        """;

    public static byte[]? Compile(string modId, IReadOnlyList<(string Path, string Text)> sources)
    {
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var trees = new List<SyntaxTree>
        {
            CSharpSyntaxTree.ParseText(SourceText.From(GlobalUsings, Encoding.UTF8), parseOptions, "GlobalUsings.g.cs"),
        };
        trees.AddRange(sources.Select(s =>
            CSharpSyntaxTree.ParseText(SourceText.From(s.Text, Encoding.UTF8), parseOptions, s.Path)));

        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithAllowUnsafe(true)
            .WithOptimizationLevel(OptimizationLevel.Release);

        var compilation = CSharpCompilation.Create($"mod-{modId}", trees, References(), options);
        using var ms = new MemoryStream();
        var result = compilation.Emit(ms, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded));
        if (!result.Success)
        {
            foreach (var diag in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                Console.Error.WriteLine($"[Mods] {modId}: {diag}");
            return null;
        }

        return ms.ToArray();
    }

    static List<MetadataReference> References()
    {
        if (_references != null) return _references;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<MetadataReference>();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            path = Path.GetFullPath(path);
            if (!set.Add(path)) return;
            list.Add(MetadataReference.CreateFromFile(path));
        }

        // Android packs assemblies inside the APK, so Assembly.Location is empty.
        // The host copies BCL + RecompOne.Runtime into compiler-refs for Roslyn.
        var refsDir = Path.Combine(AppPaths.Root, "compiler-refs");
        if (Directory.Exists(refsDir))
        {
            foreach (var path in Directory.EnumerateFiles(refsDir, "*.dll"))
                Add(path);
        }

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;
            try { Add(asm.Location); } catch { /* ignore */ }
        }

        Add(typeof(object).Assembly.Location);
        Add(typeof(Console).Assembly.Location);
        Add(typeof(Enumerable).Assembly.Location);
        Add(typeof(RecompOne.Runtime.Runtime).Assembly.Location);

        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (!string.IsNullOrEmpty(tpa))
        {
            foreach (var path in tpa.Split(Path.PathSeparator))
                Add(path);
        }

        if (list.Count == 0)
        {
            Console.Error.WriteLine("[Mods] no compiler references found (compiler-refs missing?)");
            return list;
        }

        _references = list;
        return _references;
    }
}
