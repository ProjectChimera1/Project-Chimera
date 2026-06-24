#nullable enable
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 1.7 (AC1) — a <see cref="Validated{T}"/> is proof of validation: <see cref="ScenarioValidator"/>
    /// mints one on success, and (belt-and-suspenders) a source scan asserts no other sim file even attempts
    /// <c>new Validated&lt;</c>. The compile-time half of the guarantee is the <see cref="ScenarioValidator.Proof"/>
    /// token's internal constructor (nothing outside the assembly can mint); this scan covers the in-assembly half.
    /// </summary>
    public class ValidatedMintingTests
    {
        [Fact]
        public void Validate_OnValidModel_MintsAValidatedCarryingTheModel()
        {
            var model = new ScenarioData
            {
                MapBounds = 120f,
                PlayerSlots = new[] { new ScenarioPlayerSlot { Slot = 0, StartOre = 100f, BaseX = 0f, BaseZ = 0f } },
            };
            ValidationResult r = new ScenarioValidator().Validate(model);
            Assert.True(r.Ok, r.Error);
            Assert.Same(model, r.Value.Value);
        }

        /// <summary>
        /// ValidatedSoleMinterTest — scan the sim source: the only file that may construct a Validated&lt;T&gt;
        /// is <c>ScenarioValidator.cs</c>. A stray <c>new Validated&lt;</c> anywhere else means a value is being
        /// labelled "validated" without passing the gate (Story 1.7, D1).
        /// </summary>
        [Fact]
        public void NewValidated_AppearsOnlyInScenarioValidator()
        {
            string srcRoot = LocateSrcRoot();
            Assert.True(Directory.Exists(srcRoot), $"Could not locate the sim source root at '{srcRoot}'.");

            // Whitespace-tolerant so `new  Validated<` / `new\nValidated<` cannot evade the scan — a missed mint
            // (not a false trip) is the dangerous failure. [Review][Patch]
            var mintPattern = new Regex(@"new\s+Validated\s*<");
            string[] offenders = Directory
                .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
                .Where(f => mintPattern.IsMatch(File.ReadAllText(f)))
                .Where(f => Path.GetFileName(f) != "ScenarioValidator.cs")
                .Select(Path.GetFileName)
                .ToArray()!;

            Assert.True(offenders.Length == 0,
                $"`new Validated<` found outside ScenarioValidator.cs: {string.Join(", ", offenders)}. " +
                $"Validated<T> is proof of validation — only ScenarioValidator may mint it (Story 1.7, D1).");
        }

        // Anchor the scan to the repo via the compile-time path of THIS file:
        // <repo>/godot/ProjectChimera.Sim.Tests/Validation/ValidatedMintingTests.cs  →  <repo>/godot/src
        private static string LocateSrcRoot([CallerFilePath] string thisFile = "")
        {
            string validationDir = Path.GetDirectoryName(thisFile)!;
            string testProj      = Path.GetDirectoryName(validationDir)!;
            string godot         = Path.GetDirectoryName(testProj)!;
            return Path.Combine(godot, "src");
        }
    }
}
