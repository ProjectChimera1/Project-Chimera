#nullable enable
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 7.7 — the FAIL-CLOSED gate tests (this file — formerly <c>ShadowModeTests</c> — tested the Story 1.7
    /// shadow/fail-closed policy; shadow mode and its <c>ScenarioGate</c>/<c>CHIMERA_VALIDATE_FAILCLOSED</c> escape
    /// hatch are REMOVED, so it now pins the inverse contract): a failed validation carries a located error and NO
    /// <see cref="Validated{T}"/> token (its <c>Value</c> is <c>default</c> — the applier's null-model guard makes
    /// consuming it a no-op), the validator stays pure (never throws), and a source scan proves the shadow-mode
    /// machinery (<c>ScenarioGate</c> / the env var) is gone from <c>godot/src</c> so it cannot quietly return.
    /// </summary>
    public class FailClosedGateTests
    {
        [Fact]
        public void FailedValidation_CarriesLocatedError_AndNoToken()
        {
            var invalid = new ScenarioData
            {
                MapBounds = -1f, // fails the first check
                PlayerSlots = new[] { new ScenarioPlayerSlot { Slot = 0 } },
            };
            ValidationResult r = new ScenarioValidator().Validate(invalid);
            Assert.False(r.Ok);
            Assert.NotNull(r.Error);
            // Proof discipline: NO token exists for a failed model — Value is default, whose model is null.
            Assert.Null(r.Value.Value);
        }

        [Fact]
        public void ValidationResult_Fail_CarriesLocatedMessage()
        {
            ValidationResult r = ValidationResult.Fail("scenario.units[3].slot=5 references no declared player_slot");
            Assert.False(r.Ok);
            Assert.Equal("scenario.units[3].slot=5 references no declared player_slot", r.Error);
            Assert.Null(r.Value.Value);
        }

        [Fact]
        public void Validator_IsPure_NeverThrows_OnInvalidInput()
        {
            // The validator returns located errors; it must NOT throw (the call site surfaces the error).
            var invalid = new ScenarioData
            {
                MapBounds = -1f,
                PlayerSlots = new[] { new ScenarioPlayerSlot { Slot = 99 } },
            };
            var ex = Record.Exception(() => new ScenarioValidator().Validate(invalid));
            Assert.Null(ex);
            Assert.False(new ScenarioValidator().Validate(invalid).Ok);
        }

        /// <summary>
        /// The no-escape-hatch teeth (mirrors the story's verification grep): neither the retired shadow-mode
        /// decision type (<c>ScenarioGate</c> / its <c>ScenarioGate.ShouldProceed</c> decision) nor the fail-closed
        /// env toggle (<c>CHIMERA_VALIDATE_FAILCLOSED</c>) may appear anywhere under <c>godot/src</c>. If either
        /// returns, an apply path has regrown a way to proceed on a failed validation. The needles are QUALIFIED
        /// (review follow-up) so an unrelated future method merely named <c>ShouldProceed</c> cannot false-positive.
        /// </summary>
        [Fact]
        public void ShadowModeMachinery_IsGoneFromTheSourceTree()
        {
            string srcRoot = LocateSrcRoot();
            // CI path-mapping / packaged-test runs can execute this assembly away from the source tree; the scan
            // is a source-hygiene guard, not a runtime contract, so pass-through when there is nothing to scan
            // (review follow-up — a missing dir used to hard-fail the suite under path mapping).
            if (!Directory.Exists(srcRoot)) return;

            string[] offenders = Directory
                .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
                .Where(f =>
                {
                    string text = File.ReadAllText(f);
                    return text.Contains("CHIMERA_VALIDATE_FAILCLOSED")
                        || text.Contains("ScenarioGate.ShouldProceed")
                        || text.Contains("class ScenarioGate");
                })
                .Select(Path.GetFileName)
                .ToArray()!;

            Assert.True(offenders.Length == 0,
                "Shadow-mode machinery (ScenarioGate / CHIMERA_VALIDATE_FAILCLOSED) found in: " +
                $"{string.Join(", ", offenders)}. Story 7.7 removed the escape hatch — proceeding requires r.Ok, " +
                "everywhere, unconditionally.");
        }

        // Anchor the scan to the repo via the compile-time path of THIS file:
        // <repo>/godot/ProjectChimera.Sim.Tests/Validation/FailClosedGateTests.cs → <repo>/godot/src
        private static string LocateSrcRoot([CallerFilePath] string thisFile = "")
        {
            string validationDir = Path.GetDirectoryName(thisFile)!;
            string testProj      = Path.GetDirectoryName(validationDir)!;
            string godot         = Path.GetDirectoryName(testProj)!;
            return Path.Combine(godot, "src");
        }
    }
}
