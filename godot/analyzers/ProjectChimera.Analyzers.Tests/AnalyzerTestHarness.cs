#nullable enable
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ProjectChimera.Analyzers.Tests
{
    /// <summary>
    /// Minimal in-process driver for a <see cref="DiagnosticAnalyzer"/>: parse a C# source string, build a
    /// net8 compilation referencing the real framework (via the host's TRUSTED_PLATFORM_ASSEMBLIES — no extra
    /// NuGet test-SDK dependency), run the analyzer, and return ONLY the analyzer diagnostics (compiler
    /// diagnostics are excluded by <see cref="CompilationWithAnalyzers.GetAnalyzerDiagnosticsAsync()"/>).
    /// </summary>
    internal static class AnalyzerTestHarness
    {
        private static readonly ImmutableArray<MetadataReference> FrameworkReferences = BuildFrameworkReferences();

        private static ImmutableArray<MetadataReference> BuildFrameworkReferences()
        {
            string tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty;
            return tpa.Split(Path.PathSeparator)
                      .Where(p => p.Length > 0 && File.Exists(p))
                      .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
                      .ToImmutableArray();
        }

        /// <summary>Run <paramref name="analyzer"/> over <paramref name="source"/> and return its diagnostics.</summary>
        public static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(string source, DiagnosticAnalyzer analyzer)
        {
            SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
            var compilation = CSharpCompilation.Create(
                assemblyName: "ChimeraAnalyzerTestAsm",
                syntaxTrees: new[] { tree },
                references: FrameworkReferences,
                options: new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    nullableContextOptions: NullableContextOptions.Enable));

            CompilationWithAnalyzers withAnalyzers =
                compilation.WithAnalyzers(ImmutableArray.Create(analyzer));

            return await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        }

        /// <summary>Convenience: the distinct diagnostic IDs the analyzer raised over <paramref name="source"/>.</summary>
        public static async Task<string[]> GetIdsAsync(string source, DiagnosticAnalyzer analyzer)
            => (await GetDiagnosticsAsync(source, analyzer))
               .Select(d => d.Id)
               .ToArray();
    }
}
