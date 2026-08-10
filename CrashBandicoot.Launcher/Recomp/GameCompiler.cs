using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace CrashBandicoot.Launcher.Recomp;

public static class GameCompiler
{
    const string GlobalUsingsSource =
        """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    public static void CompileToDll(
        string sourcesDir,
        string outputDll,
        IProgress<string>? progress = null,
        string? metadataReferencesDir = null,
        bool concurrentBuild = true)
    {
        progress?.Report("Compiling recompiled game…");
        var files = Directory.GetFiles(sourcesDir, "*.cs")
            .Where(f => !Path.GetFileName(f).StartsWith('_'))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
            throw new InvalidOperationException("No generated .cs sources to compile.");

        var parse = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        var trees = new List<SyntaxTree>
        {
            CSharpSyntaxTree.ParseText(SourceText.From(GlobalUsingsSource, Encoding.UTF8), parse, "GlobalUsings.g.cs"),
        };
        trees.AddRange(files.Select(f =>
            CSharpSyntaxTree.ParseText(SourceText.From(File.ReadAllText(f), Encoding.UTF8), parse, f)));

        var refs = BuildReferences(metadataReferencesDir);
        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithAllowUnsafe(true)
            .WithOptimizationLevel(OptimizationLevel.Release)
            .WithConcurrentBuild(concurrentBuild);

        var compilation = CSharpCompilation.Create("CrashBandicoot.Game", trees, refs, options);
        Directory.CreateDirectory(Path.GetDirectoryName(outputDll)!);

        using var fs = File.Create(outputDll);
        var result = compilation.Emit(fs, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded));
        if (!result.Success)
        {
            var errors = result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Take(20)
                .Select(d => d.ToString());
            throw new InvalidOperationException("Game compile failed:\n" + string.Join("\n", errors));
        }

        progress?.Report("Compile OK.");
    }

    static ImmutableArray<MetadataReference> BuildReferences(string? metadataReferencesDir)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<MetadataReference>();

        void Add(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            path = Path.GetFullPath(path);
            if (!set.Add(path)) return;
            list.Add(MetadataReference.CreateFromFile(path));
        }

        if (!string.IsNullOrWhiteSpace(metadataReferencesDir) && Directory.Exists(metadataReferencesDir))
        {
            foreach (var path in Directory.EnumerateFiles(metadataReferencesDir, "*.dll"))
                Add(path);
        }

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;
            try { Add(asm.Location); } catch { /* ignore */ }
        }

        // Ensure runtime + BCL essentials even if not yet loaded
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

        return list.ToImmutableArray();
    }
}
