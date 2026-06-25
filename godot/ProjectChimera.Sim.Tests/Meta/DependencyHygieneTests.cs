#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Xunit;

namespace ProjectChimera.Sim.Tests.Meta
{
    /// <summary>
    /// Story 1.10a (AC2 — AR-2 / AR-35) — permanently locks the project's dependency surface so a future change
    /// cannot silently (a) drift the sole shipped NuGet pin or (b) leak a test-only dependency into the shipping
    /// game project. This is the guard-test form of the dependency hygiene AR-2 requires to keep the sim
    /// AOT-eligible and the Tier-1 split Godot-free; it mirrors the established guard-test pattern
    /// (<c>SimChecksumCoverageGuardTest</c>, <c>GodotFreeBoundaryTest</c>, <c>PhaseOrderTest</c>).
    ///
    /// It asserts the STATUS QUO — both csproj files already satisfy every assertion today; Story 1.10a changes
    /// neither. The value is that the assertions now run on every <c>dotnet test</c> (locally AND in the CI
    /// determinism gate), so the moment a dependency drifts the developer's own test run goes red, naming the
    /// offending package and file.
    ///
    /// The two csproj files are located portably via <see cref="CallerFilePathAttribute"/> — the SAME mechanism
    /// <c>GoldenChecksumReplay.GoldenSourcePath</c> uses — so there is no hardcoded absolute path. The project is
    /// compiled and run on the same machine (locally and on a CI runner), so the compile-time source path is
    /// valid at run time on the CI checkout too.
    /// </summary>
    public class DependencyHygieneTests
    {
        /// <summary>The only NuGet dependency the shipping game project (godot.csproj) is allowed to carry (AR-2).</summary>
        private const string ShippedPackageId = "NakamaClient";

        /// <summary>The exact pinned version. An intentional bump must change this constant in the SAME commit.</summary>
        private const string ShippedPackageVersion = "3.13.0";

        [Fact]
        public void GodotCsproj_PinsNakamaClient_ToExactVersion()
        {
            var refs = PackageReferences(GodotCsprojPath());
            var nakama = refs.FirstOrDefault(r => r.Include.Equals(ShippedPackageId, StringComparison.OrdinalIgnoreCase));

            Assert.True(nakama.Include is not null,
                $"{ShippedPackageId} PackageReference not found in godot.csproj. AR-2 requires the shipping game " +
                $"project to pin {ShippedPackageId} {ShippedPackageVersion} as its sole NuGet dependency.");
            Assert.True(nakama.Version == ShippedPackageVersion,
                $"godot.csproj pins {ShippedPackageId} at '{nakama.Version}', expected exactly '{ShippedPackageVersion}' " +
                $"(AR-2). If this is an INTENTIONAL upgrade, update {nameof(ShippedPackageVersion)} in this guard in the " +
                $"same commit; otherwise the pin drifted — revert it.");
        }

        [Fact]
        public void GodotCsproj_ContainsNo_TestOnlyDependencies()
        {
            string[] leaked = PackageReferences(GodotCsprojPath())
                .Where(r => IsTestOnlyPackage(r.Include))
                .Select(r => r.Include)
                .ToArray();

            Assert.True(leaked.Length == 0,
                $"Test-only dependencies leaked into the shipping game project godot.csproj: " +
                $"[{string.Join(", ", leaked)}]. Test deps (xUnit, Microsoft.NET.Test.Sdk, test runners) MUST live " +
                $"ONLY in ProjectChimera.Sim.Tests.csproj (AR-2 / AR-35 — the shipping sim stays AOT-eligible and " +
                $"Godot-free). Move them back to the test project.");
        }

        [Fact]
        public void GodotCsproj_CarriesExactly_TheSingleShippedPackage()
        {
            // Allowlist counterpart to the denylist above (1.10a review hardening). AR-2 makes NakamaClient the
            // SOLE shipped NuGet dependency, so ANY other PackageReference is a leak — including a non-xUnit test
            // framework (coverlet, Moq, NUnit, FluentAssertions, …) that the IsTestOnlyPackage heuristic would miss.
            // This assertion closes that blind spot: it fails on anything that is not exactly NakamaClient.
            string[] unexpected = PackageReferences(GodotCsprojPath())
                .Select(r => r.Include)
                .Where(id => !id.Equals(ShippedPackageId, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            Assert.True(unexpected.Length == 0,
                $"godot.csproj carries unexpected PackageReference(s): [{string.Join(", ", unexpected)}]. " +
                $"AR-2 requires {ShippedPackageId} to be the SOLE NuGet dependency of the shipping game project; " +
                $"this allowlist catches ANY extra dependency the {nameof(IsTestOnlyPackage)} denylist might miss. " +
                $"Move it to ProjectChimera.Sim.Tests.csproj, or — if it is a deliberate new shipped dependency — " +
                $"update AR-2 and this guard in the same commit.");
        }

        [Fact]
        public void TestCsproj_OwnsTheTestOnlyDependencies()
        {
            string[] ids = PackageReferences(TestCsprojPath()).Select(r => r.Include).ToArray();

            foreach (string expected in new[] { "xunit", "Microsoft.NET.Test.Sdk", "xunit.runner.visualstudio" })
                Assert.True(ids.Any(id => id.Equals(expected, StringComparison.OrdinalIgnoreCase)),
                    $"Expected test dependency '{expected}' was not found in ProjectChimera.Sim.Tests.csproj " +
                    $"(found: [{string.Join(", ", ids)}]). The test-only deps must be isolated HERE, not in the " +
                    $"shipping project — this assertion proves the isolation lives where it should.");
        }

        // ── helpers ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Read all &lt;PackageReference&gt; (Include, Version) pairs from a csproj. Version may be an attribute
        /// (the convention in this repo) or a child element; both are handled. SDK-style csproj files have no
        /// default XML namespace, so element names are unqualified.
        /// </summary>
        private static List<(string Include, string? Version)> PackageReferences(string csprojPath)
        {
            if (!File.Exists(csprojPath))
                throw new FileNotFoundException(
                    $"Dependency-hygiene guard could not locate the csproj to inspect. Resolved path: '{csprojPath}'. " +
                    $"This path is derived from [CallerFilePath]; if the test-project layout moved, update the guard.",
                    csprojPath);

            XDocument doc = XDocument.Load(csprojPath);
            var result = new List<(string, string?)>();
            foreach (XElement pr in doc.Descendants("PackageReference"))
            {
                string? include = pr.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include)) continue;
                string? version = pr.Attribute("Version")?.Value ?? pr.Element("Version")?.Value;
                result.Add((include!, version));
            }
            return result;
        }

        /// <summary>True for the test-harness package families that must never appear in the shipping project.</summary>
        private static bool IsTestOnlyPackage(string include) =>
            include.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)
            || include.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)
            || include.Contains(".runner.", StringComparison.OrdinalIgnoreCase)
            || include.Contains("TestPlatform", StringComparison.OrdinalIgnoreCase);

        /// <summary>godot/godot.csproj — two directories up from this file (…/ProjectChimera.Sim.Tests/Meta/).</summary>
        private static string GodotCsprojPath([CallerFilePath] string thisFilePath = "") =>
            ResolveFromHere(thisFilePath, "..", "..", "godot.csproj");

        /// <summary>The sibling test csproj — one directory up from this file.</summary>
        private static string TestCsprojPath([CallerFilePath] string thisFilePath = "") =>
            ResolveFromHere(thisFilePath, "..", "ProjectChimera.Sim.Tests.csproj");

        /// <summary>Resolve a path relative to THIS source file's directory and normalize away the '..' segments.</summary>
        private static string ResolveFromHere(string thisFilePath, params string[] segments)
        {
            string dir = Path.GetDirectoryName(thisFilePath)
                         ?? throw new InvalidOperationException(
                             "Could not resolve this test's source directory via [CallerFilePath].");
            string[] parts = new string[segments.Length + 1];
            parts[0] = dir;
            Array.Copy(segments, 0, parts, 1, segments.Length);
            return Path.GetFullPath(Path.Combine(parts));
        }
    }
}
