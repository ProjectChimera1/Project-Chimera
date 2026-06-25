#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Xunit;

namespace ProjectChimera.Sim.Tests.Meta
{
    /// <summary>
    /// Story 1.10b guard tests — keep the determinism analyzer gate's CONFIGURATION honest as Tier-1 invariants
    /// (the 1.10a convention: assert structure as tests that run on every <c>dotnet test</c>, never as CI-only
    /// shell steps). These do not RUN the analyzers (the gate is the analysis-project build); they assert the
    /// wiring that makes the analyzers cover the right sources, stay AOT-meaningful, and stay reproducible.
    ///
    /// Paths are resolved portably from this source file via <see cref="CallerFilePathAttribute"/> (the same
    /// mechanism <c>DependencyHygieneTests</c> and <c>GoldenChecksumReplay</c> use).
    /// </summary>
    public class AnalyzerGateGuardTests
    {
        [Fact]
        public void SimSourcesProps_Exists()
        {
            string path = SimSourcesPropsPath();
            Assert.True(File.Exists(path),
                $"godot/SimSources.props (the single sim-source-of-truth shared by the Tier-1 test project and the " +
                $"analyzer gate) was not found at '{path}'. Story 1.10b Task 1 created it; if it moved, the analyzer's " +
                $"coverage and the tested source set can silently diverge.");
        }

        [Fact]
        public void TestAndAnalysisProjects_BothImportSimSourcesProps()
        {
            // The whole point of SimSources.props: the analyzed source set and the TESTED source set are the same
            // file, so a sim folder added in one is covered by both. If either project stops importing it, coverage
            // can drift — this guard fails the moment that happens.
            AssertImportsSimSources(TestCsprojPath(), "ProjectChimera.Sim.Tests.csproj");
            AssertImportsSimSources(AnalysisCsprojPath(), "ProjectChimera.Sim.Analysis.csproj");
        }

        [Fact]
        public void BannedSymbols_Exists_AndIsReferencedAsAdditionalFile()
        {
            string txt = Path.Combine(Path.GetDirectoryName(AnalysisCsprojPath())!, "BannedSymbols.txt");
            Assert.True(File.Exists(txt),
                $"BannedSymbols.txt (the RS0030 determinism ban-list) was not found at '{txt}'.");

            XDocument doc = XDocument.Load(AnalysisCsprojPath());
            bool referenced = doc.Descendants("AdditionalFiles")
                .Any(e => string.Equals((string?)e.Attribute("Include"), "BannedSymbols.txt", StringComparison.OrdinalIgnoreCase));
            Assert.True(referenced,
                "ProjectChimera.Sim.Analysis.csproj must reference BannedSymbols.txt via <AdditionalFiles Include=\"BannedSymbols.txt\"/> " +
                "or BannedApiAnalyzers (RS0030) reads no bans and the gate silently passes everything.");
        }

        [Fact]
        public void AnalysisProject_EnablesAotAnalyzer()
        {
            XDocument doc = XDocument.Load(AnalysisCsprojPath());
            bool aot = doc.Descendants("IsAotCompatible").Any(e => string.Equals(e.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
            Assert.True(aot,
                "ProjectChimera.Sim.Analysis.csproj must set <IsAotCompatible>true</IsAotCompatible> — that umbrella enables the " +
                "trim/AOT analyzers (IL2xxx/IL3xxx) over the Godot-free sim compilation (AC3). Without it the AOT verdict is absent.");
        }

        [Fact]
        public void AnalysisProject_ConsumesTheCustomAnalyzer_AsAnalyzer()
        {
            XDocument doc = XDocument.Load(AnalysisCsprojPath());
            bool wired = doc.Descendants("ProjectReference").Any(e =>
                ((string?)e.Attribute("Include") ?? string.Empty).IndexOf("ProjectChimera.Analyzers", StringComparison.OrdinalIgnoreCase) >= 0
                && string.Equals((string?)e.Attribute("OutputItemType") ?? e.Element("OutputItemType")?.Value, "Analyzer", StringComparison.OrdinalIgnoreCase));
            Assert.True(wired,
                "ProjectChimera.Sim.Analysis.csproj must reference the custom analyzer via " +
                "<ProjectReference ... OutputItemType=\"Analyzer\"/> so CHM0001..CHM0005 actually run over the sim sources.");
        }

        // ── path helpers (this file lives in godot/ProjectChimera.Sim.Tests/Meta/) ────────────────

        private static string SimSourcesPropsPath([CallerFilePath] string p = "") =>
            ResolveFromHere(p, "..", "..", "SimSources.props");

        private static string TestCsprojPath([CallerFilePath] string p = "") =>
            ResolveFromHere(p, "..", "ProjectChimera.Sim.Tests.csproj");

        private static string AnalysisCsprojPath([CallerFilePath] string p = "") =>
            ResolveFromHere(p, "..", "..", "ProjectChimera.Sim.Analysis", "ProjectChimera.Sim.Analysis.csproj");

        private static void AssertImportsSimSources(string csprojPath, string label)
        {
            Assert.True(File.Exists(csprojPath), $"Guard could not locate {label} at '{csprojPath}'.");
            XDocument doc = XDocument.Load(csprojPath);
            bool imports = doc.Descendants("Import")
                .Any(e => ((string?)e.Attribute("Project") ?? string.Empty)
                    .Replace('\\', '/').EndsWith("SimSources.props", StringComparison.OrdinalIgnoreCase));
            Assert.True(imports,
                $"{label} must <Import Project=\"..\\SimSources.props\"/>. Without it the analyzed/tested sim source sets " +
                $"can diverge (Story 1.10b Task 1).");
        }

        private static string ResolveFromHere(string thisFilePath, params string[] segments)
        {
            string dir = Path.GetDirectoryName(thisFilePath)
                         ?? throw new InvalidOperationException("Could not resolve this test's source directory via [CallerFilePath].");
            string[] parts = new string[segments.Length + 1];
            parts[0] = dir;
            Array.Copy(segments, 0, parts, 1, segments.Length);
            return Path.GetFullPath(Path.Combine(parts));
        }
    }
}
