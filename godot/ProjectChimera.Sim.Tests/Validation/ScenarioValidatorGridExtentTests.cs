#nullable enable
using System.IO;
using System.Runtime.CompilerServices;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// DW-158 — <c>map_bounds</c> must fit the FIXED sim-grid half-extent (±<see cref="MapSizes.MaxHalfExtent"/> ==
    /// <see cref="FlowField.WORLD_HALF_INT"/> == 128), not merely the 16.16 representable range.
    ///
    /// Pre-fix the validator's only ceiling was the 16.16 range (32768), so an authored half-extent above 128 admitted
    /// coordinates the grid cannot represent: <see cref="FlowField.WorldToCell"/> CLAMPS them, so two distinct far-out
    /// positions ALIAS onto one cell — a blocking prop outside the grid stamped an edge-cell footprint it does not
    /// occupy, which then false-flagged an UNRELATED in-grid start position as blocked. Deterministic but semantically
    /// wrong. <c>map_bounds == 128</c> (MapSize.Large) stays legal — the exact +128 boundary line's clamp is the
    /// deliberately-documented playable ceiling (DW-162), a separate concern.
    /// </summary>
    public class ScenarioValidatorGridExtentTests
    {
        private static readonly ScenarioValidator Validator = new();

        /// <summary>A minimal valid model at the given half-extent (one declared slot, in-grid base).</summary>
        private static ScenarioData ModelWithBounds(float bounds) => new ScenarioData
        {
            MapBounds = bounds,
            PlayerSlots = new[] { new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json", StartOre = 200f, BaseX = -45f, BaseZ = 0f } },
        };

        [Theory]
        [InlineData(80f)]   // MapSize.Small
        [InlineData(90f)]   // legacy hand-authored extent (map_03_the_narrows)
        [InlineData(120f)]  // MapSize.Medium — the historical default
        [InlineData(128f)]  // MapSize.Large — exactly the fixed grid extent, the shipped maximum
        public void BoundsWithinTheFixedGridExtent_Pass(float bounds)
        {
            ValidationResult r = Validator.Validate(ModelWithBounds(bounds));
            Assert.True(r.Ok, r.Error);
        }

        [Theory]
        [InlineData(128.5f)] // the first rejected value above the extent
        [InlineData(129f)]
        [InlineData(130f)]   // map_10_mirror_lake's pre-fix authored value
        [InlineData(160f)]   // map_12_the_frontier's pre-fix authored value
        [InlineData(1000f)]
        public void BoundsBeyondTheFixedGridExtent_FailClosed(float bounds)
        {
            ValidationResult r = Validator.Validate(ModelWithBounds(bounds));
            Assert.False(r.Ok, $"map_bounds={bounds} is beyond the fixed ±{MapSizes.MaxHalfExtent} grid and must fail closed.");
            Assert.Contains("map_bounds", r.Error!);
            Assert.Contains("128", r.Error!); // the message names the extent the author must fit inside
        }

        [Fact]
        public void GrosslyOversizedBounds_StillReportsTheRangeBranch_DistinctReasons()
        {
            // The 16.16 range branch must keep its own message (the two ceilings are distinct reasons, as
            // NegativeValidationTests.OverRangePosition_IsRejected_ViaTheRangeBranch pins for coordinates).
            var m = ModelWithBounds(40000f);
            ValidationResult r = Validator.Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("16.16 range", r.Error!);
        }

        [Fact]
        public void FarOutBlockingProp_CanNoLongerFalseFlag_AnUnrelatedInGridStart()
        {
            // THE DW-158 defect, expressed end-to-end. With map_bounds=250 a blocking prop at x=200 is "in bounds",
            // but WorldToCell clamps it to column 127 — the SAME cell as a perfectly legal start position at x=127.
            // Pre-fix the validator therefore rejected the model with a "blocked cell" error aimed at the innocent
            // START POSITION (the false flag). Post-fix the authoring lie itself is rejected first, naming map_bounds,
            // so no in-grid placement can ever be false-flagged by an out-of-grid footprint again.
            var m = ModelWithBounds(250f);
            m.PlayerSlots[0].BaseX = 127f;
            m.PlayerSlots[0].BaseZ = 0f;
            m.Props = new[] { new ScenarioProp { PropId = "rock", X = 200f, Z = 0f, BlocksPathing = true } };

            // Cell identity proof: the far-out prop and the in-grid start really do resolve to one cell.
            Assert.Equal(PathabilityGrid.CellOf(Fixed.FromInt(200), Fixed.Zero),
                         PathabilityGrid.CellOf(Fixed.FromInt(127), Fixed.Zero));

            ValidationResult r = Validator.Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("map_bounds", r.Error!);
            Assert.DoesNotContain("player_slots", r.Error!); // never blamed on the innocent start position
        }

        [Fact]
        public void EveryShippedScenario_FitsTheFixedGridExtent()
        {
            // The content half of the fix: two shipped maps carried map_bounds 130 / 160 (playable areas the grids
            // cannot represent) and would now fail to load. They were corrected to 128 (Large); this guard keeps a
            // future authored map from re-introducing an unloadable extent.
            int checkedCount = 0;
            foreach (string path in Directory.EnumerateFiles(ScenariosDir(), "*.json"))
            {
                ScenarioData? s = ScenarioSerializer.LoadFromFile(path);
                if (s == null) continue;
                checkedCount++;
                Assert.True(s.MapBounds > 0f && s.MapBounds <= MapSizes.MaxHalfExtent,
                    $"{Path.GetFileName(path)}: map_bounds={s.MapBounds} is outside (0, {MapSizes.MaxHalfExtent}] — " +
                    "the fixed flow/fog/pathability grid extent; this map would fail the load gate.");
            }
            Assert.True(checkedCount > 0, "no shipped scenarios were checked — the scenarios directory resolution broke.");
        }

        /// <summary>Resolve <c>godot/resources/data/scenarios</c> from this file's compile-time path (mirrors
        /// PersistenceManifestTests.ScenariosDir).</summary>
        private static string ScenariosDir([CallerFilePath] string thisFile = "")
        {
            string godot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(thisFile)!)!)!;
            return Path.Combine(godot, "resources", "data", "scenarios");
        }
    }
}
