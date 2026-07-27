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
        _references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToList();
        return _references;
    }
}
