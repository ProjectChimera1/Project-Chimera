#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 1.7 (AC2) — <see cref="ScenarioValidator"/> rejects out-of-range / non-finite values and dangling
    /// references with a LOCATED error (naming the offending field path), and accepts a valid model (returning a
    /// <see cref="Validated{T}"/> whose Value is the same instance). The validator is pure: it never throws and
    /// never logs — every call site is fail-closed since Story 7.7 (see FailClosedGateTests).
    ///
    /// There is intentionally NO test for the AR-13 forbidden-until-SimRng rule: SimRng shipped in Story 1.5 and
    /// is unconditionally present, and no effect schema exists until Epic 2, so the rule has no reachable failing
    /// case in 1.7 (D4). The mature rule is enforced by Epic 2's effect-validator (Story 2.3). AC2's testable
    /// weight therefore rests on the out-of-range and dangling-reference checks below.
    /// </summary>
    public class NegativeValidationTests
    {
        private static ScenarioValidator NewValidator() => new();

        /// <summary>A minimal VALID model: two declared slots, an in-bounds node, building, and unit.</summary>
        private static ScenarioData ValidModel() => new ScenarioData
        {
            MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[]
            {
                new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json", StartOre = 200f, BaseX = -45f, BaseZ = 0f },
                new ScenarioPlayerSlot { Slot = 1, FactionJson = "res://b.json", StartOre = 200f, BaseX =  45f, BaseZ = 0f },
            },
            ResourceNodes = new[] { new ScenarioResourceNode { X = 10f, Z = 10f, Supply = 400f, Rate = 5f, MaxGatherers = 4 } },
            Buildings = new[] { new ScenarioBuilding { Type = "CommandCenter", Slot = 0, X = -45f, Z = 0f, PreBuilt = true } },
            Units = new[] { new ScenarioUnit { UnitId = "worker", Slot = 1, X = 42f, Z = 3f } },
        };

        [Fact]
        public void ValidModel_Passes_AndWrapsTheSameInstance()
        {
            var model = ValidModel();
            ValidationResult r = NewValidator().Validate(model);
            Assert.True(r.Ok, r.Error);
            Assert.Null(r.Error);
            Assert.Same(model, r.Value.Value); // the Validated<T> carries the very instance that was validated
        }

        [Fact]
        public void NaNStartOre_IsRejected_LocatingStartOre()
        {
            var m = ValidModel();
            m.PlayerSlots[0].StartOre = float.NaN;
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("player_slots[0].start_ore", r.Error!);
        }

        [Fact]
        public void NegativeStartCrystal_IsRejected_LocatingStartCrystal()
        {
            // start_crystal reuses the same CheckNonNeg guard as start_ore (rejects negative AND NaN). Slot [1] proves
            // the crystal check reports the correct per-slot location, not a hardcoded index.
            var m = ValidModel();
            m.PlayerSlots[1].StartCrystal = -1f;
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("player_slots[1].start_crystal", r.Error!);
        }

        [Fact]
        public void InfiniteBaseX_IsRejected_LocatingBaseX()
        {
            var m = ValidModel();
            m.PlayerSlots[0].BaseX = float.PositiveInfinity;
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("player_slots[0].base_x", r.Error!);
        }

        [Fact]
        public void OverRangePosition_IsRejected_ViaTheRangeBranch()
        {
            var m = ValidModel();
            m.PlayerSlots[0].BaseX = 40000f; // beyond the 16.16 range (>= 32768) — would wrap Fixed.FromFloat
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("base_x", r.Error!);
            Assert.Contains("16.16 range", r.Error!); // the range branch, NOT the map_bounds branch (distinct reasons)
        }

        [Fact]
        public void NodePositionOutsideMapBounds_IsRejected_LocatingTheNode()
        {
            var m = ValidModel();
            m.ResourceNodes[0].X = 200f; // inside the Fixed range but outside map_bounds 120
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("resource_nodes[0].x", r.Error!);
            Assert.Contains("map_bounds", r.Error!);
        }

        [Fact]
        public void NegativeSupply_IsRejected()
        {
            var m = ValidModel();
            m.ResourceNodes[0].Supply = -50f;
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("resource_nodes[0].supply", r.Error!);
        }

        [Fact]
        public void NegativeRate_IsRejected()
        {
            var m = ValidModel();
            m.ResourceNodes[0].Rate = -1f;
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("resource_nodes[0].rate", r.Error!);
        }

        [Fact]
        public void SlotAboveEngineCeiling_IsRejected()
        {
            var m = ValidModel();
            m.PlayerSlots[1].Slot = 5; // < PLAYER_COUNT(8) but exceeds the as-built Faction enum (Player4 → max slot 3)
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("player_slots[1].slot", r.Error!);
        }

        [Fact]
        public void SlotOutOfPlayerCountRange_IsRejected()
        {
            var m = ValidModel();
            m.PlayerSlots[1].Slot = 99;
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("player_slots[1].slot", r.Error!);
        }

        [Fact]
        public void NegativeSlot_IsRejected()
        {
            var m = ValidModel();
            m.PlayerSlots[0].Slot = -1;
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("player_slots[0].slot", r.Error!);
        }

        [Fact]
        public void DuplicateSlot_IsRejected()
        {
            var m = ValidModel();
            m.PlayerSlots[1].Slot = 0; // collides with slot[0]
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("duplicate", r.Error!);
        }

        [Fact]
        public void BuildingWithDanglingSlot_IsRejected()
        {
            var m = ValidModel();
            m.Buildings[0].Slot = 3; // no PlayerSlot declares slot 3 (declared: {0,1})
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("buildings[0].slot", r.Error!);
        }

        [Fact]
        public void UnitWithDanglingSlot_IsRejected()
        {
            var m = ValidModel();
            m.Units[0].Slot = 3; // no PlayerSlot declares slot 3
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("units[0].slot", r.Error!);
        }

        [Fact]
        public void UnknownBuildingType_IsRejected_NotSilentlyDefaulted()
        {
            var m = ValidModel();
            m.Buildings[0].Type = "Frost"; // unknown — the applier would silently default to CommandCenter; the validator must reject
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("buildings[0].type", r.Error!);
        }

        [Fact]
        public void NumericBuildingTypeString_IsRejected()
        {
            var m = ValidModel();
            m.Buildings[0].Type = "5"; // Enum.TryParse would accept "5" as (BuildingType)5; the name-set check must NOT
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("buildings[0].type", r.Error!);
        }

        [Fact]
        public void NonPositiveMapBounds_IsRejected()
        {
            var m = ValidModel();
            m.MapBounds = 0f;
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("map_bounds", r.Error!);
        }

        [Fact]
        public void Validate_NeverThrows_OnGrosslyInvalidModel()
        {
            // Purity: a model full of non-finite values returns a located Fail, never throws.
            var m = new ScenarioData
            {
                MapBounds = float.NaN,
                PlayerSlots = new[] { new ScenarioPlayerSlot { Slot = 0, StartOre = float.NaN, BaseX = float.PositiveInfinity } },
            };
            var ex = Record.Exception(() => NewValidator().Validate(m));
            Assert.Null(ex);
        }

        [Fact]
        public void NullModel_IsRejected()
        {
            ValidationResult r = NewValidator().Validate(null!);
            Assert.False(r.Ok);
            Assert.Contains("null", r.Error!);
        }

        [Theory]
        [InlineData("player_slots")]
        [InlineData("resource_nodes")]
        [InlineData("buildings")]
        [InlineData("units")]
        public void NullCollection_IsRejected_LocatingTheField(string field)
        {
            // A null array is malformed input the applier would NRE on; the validator must reject it (located),
            // not silently treat it as empty. [Review][Patch]
            var m = ValidModel();
            switch (field)
            {
                case "player_slots":   m.PlayerSlots = null!;   break;
                case "resource_nodes": m.ResourceNodes = null!; break;
                case "buildings":      m.Buildings = null!;     break;
                case "units":          m.Units = null!;         break;
            }
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains(field, r.Error!);
        }

        // ── revival_rule (Story 3.14) — fail-closed range checks; a null rule (every existing scenario) passes ──

        [Fact]
        public void ValidRevivalRule_Passes()
        {
            var m = ValidModel();
            m.RevivalRule = new RevivalRule(); // defaults are all in-range
            Assert.True(NewValidator().Validate(m).Ok);
        }

        [Fact]
        public void NegativeRevivalCost_IsRejected_LocatingTheField()
        {
            var m = ValidModel();
            m.RevivalRule = new RevivalRule { CostOreBase = -1 };
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("revival_rule.cost_ore_base", r.Error!);
        }

        [Fact]
        public void NaNRevivalTime_IsRejected_LocatingTheField()
        {
            var m = ValidModel();
            m.RevivalRule = new RevivalRule { TimeBaseSeconds = float.NaN };
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("revival_rule.time_base_seconds", r.Error!);
        }

        [Theory]
        [InlineData(0f)]     // (0, 1] excludes 0 — a 0-HP spawn would be dead on arrival
        [InlineData(1.5f)]   // > 1 — an over-max spawn
        [InlineData(float.NaN)]
        public void OutOfRangeReviveHpFraction_IsRejected_LocatingTheField(float fraction)
        {
            var m = ValidModel();
            m.RevivalRule = new RevivalRule { ReviveHpFraction = fraction };
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("revival_rule.revive_hp_fraction", r.Error!);
        }

        [Fact]
        public void ReviveHpFractionOfOne_IsValid()
        {
            var m = ValidModel();
            m.RevivalRule = new RevivalRule { ReviveHpFraction = 1f }; // (0, 1] includes 1
            Assert.True(NewValidator().Validate(m).Ok);
        }

        [Fact]
        public void RevivalOreCostCurve_OverflowsAtMaxLevel_IsRejected()
        {
            // Each field is individually non-negative, but base + perLevel*100 = 40000 exceeds the 16.16 range and would
            // wrap NEGATIVE at runtime (free-money exploit) — the composed-curve check must fail closed.
            var m = ValidModel();
            m.RevivalRule = new RevivalRule { CostOreBase = 0, CostOrePerLevel = 400 };
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("ore cost", r.Error!);
        }

        [Fact]
        public void RevivalTimeCurve_OverflowsAtMaxLevel_IsRejected()
        {
            var m = ValidModel();
            m.RevivalRule = new RevivalRule { TimeBaseSeconds = 0f, TimePerLevelSeconds = 400f }; // 400*100 = 40000 > range
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("time", r.Error!);
        }

        [Fact]
        public void ReviveHpFraction_PositiveButQuantizesToZero_IsRejected()
        {
            // 1e-5 is > 0 and <= 1 (passes the raw float bound) but quantizes to Fixed.Zero → a 0-HP dead-on-arrival
            // hero. The QUANTIZED value must be validated, not just the float.
            var m = ValidModel();
            m.RevivalRule = new RevivalRule { ReviveHpFraction = 1e-5f };
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("revive_hp_fraction", r.Error!);
        }

        // ── Resource registry (Story 4.3, AC4) — fail-closed range checks; a null registry (every existing
        //    scenario) passes; a well-formed multi-entry registry passes ─────────────────────────────────────────

        [Fact]
        public void NullResources_Passes_ExistingScenarioPathUnchanged()
        {
            var m = ValidModel();
            m.Resources = null;
            Assert.True(NewValidator().Validate(m).Ok);
        }

        [Fact]
        public void WellFormedThreeEntryResourceRegistry_Passes()
        {
            var m = ValidModel();
            m.Resources = new[]
            {
                new ResourceDefinition { Id = "ore",     DisplayName = "Ore",     StartingAmount = 200f },
                new ResourceDefinition { Id = "crystal", DisplayName = "Crystal", StartingAmount = 0f },
                new ResourceDefinition { Id = "gems",    DisplayName = "Gems",    StartingAmount = 0f },
            };
            Assert.True(NewValidator().Validate(m).Ok);
        }

        [Fact]
        public void DuplicateResourceId_IsRejected_LocatingTheIndex()
        {
            var m = ValidModel();
            m.Resources = new[]
            {
                new ResourceDefinition { Id = "ore" },
                new ResourceDefinition { Id = "ore" },
            };
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("resources[1].id", r.Error!);
            Assert.Contains("duplicate", r.Error!);
        }

        [Fact]
        public void EmptyResourceId_IsRejected_LocatingTheIndex()
        {
            var m = ValidModel();
            m.Resources = new[] { new ResourceDefinition { Id = "" } };
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("resources[0].id", r.Error!);
        }

        [Fact]
        public void WhitespaceOnlyResourceId_IsRejected_LocatingTheIndex()
        {
            // Review patch: IsNullOrEmpty alone let a whitespace-only id ("   ") through as a distinct,
            // "unique" id — closing that gap without weakening the empty-id check above.
            var m = ValidModel();
            m.Resources = new[] { new ResourceDefinition { Id = "   " } };
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("resources[0].id", r.Error!);
        }

        [Fact]
        public void UnknownCollectionModel_IsRejected_LocatingTheIndex()
        {
            // Review patch: collection_model was authored-but-unvalidated; a typo would load clean and go
            // undetected, since this per-resource-ID field stays inert (Story 4.7 wired the field of the same name
            // on ScenarioResourceNode instead — see ResourceDefinition.CollectionModel's doc comment). Validate
            // against the closed set now instead, so a typo is rejected at import either way.
            var m = ValidModel();
            m.Resources = new[] { new ResourceDefinition { Id = "gems", CollectionModel = "Gathr" } };
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("resources[0].collection_model", r.Error!);
        }

        [Fact]
        public void NegativeStartingAmount_IsRejected_LocatingTheField()
        {
            var m = ValidModel();
            m.Resources = new[] { new ResourceDefinition { Id = "gems", StartingAmount = -1f } };
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("resources[0].starting_amount", r.Error!);
        }

        [Fact]
        public void NonFiniteStartingAmount_IsRejected()
        {
            var m = ValidModel();
            m.Resources = new[] { new ResourceDefinition { Id = "gems", StartingAmount = float.NaN } };
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("resources[0].starting_amount", r.Error!);
        }

        [Fact]
        public void NullResourceEntry_IsRejected_NotAnNRE()
        {
            var m = ValidModel();
            m.Resources = new ResourceDefinition[] { null! };
            var ex = Record.Exception(() => NewValidator().Validate(m));
            Assert.Null(ex);
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("resources[0]", r.Error!);
        }

        // ── Supply config (Story 4.4) — fail-closed range checks; a null config (every existing scenario) passes ──

        [Fact]
        public void NullSupply_Passes_ExistingScenarioPathUnchanged()
        {
            var m = ValidModel();
            m.Supply = null;
            Assert.True(NewValidator().Validate(m).Ok);
        }

        [Fact]
        public void ValidSupplyConfig_Passes()
        {
            var m = ValidModel();
            m.Supply = new SupplyConfig { StartingCap = 30, HardCeiling = 50, Enabled = true };
            Assert.True(NewValidator().Validate(m).Ok);
        }

        [Fact]
        public void SupplyConfigWithNullHardCeiling_Passes()
        {
            var m = ValidModel();
            m.Supply = new SupplyConfig { StartingCap = 10, HardCeiling = null, Enabled = false };
            Assert.True(NewValidator().Validate(m).Ok);
        }

        [Fact]
        public void NegativeStartingCap_IsRejected_LocatingTheField()
        {
            var m = ValidModel();
            m.Supply = new SupplyConfig { StartingCap = -1 };
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("scenario.supply.starting_cap", r.Error!);
        }

        [Fact]
        public void OutOfRangeStartingCap_IsRejected_LocatingTheField()
        {
            var m = ValidModel();
            m.Supply = new SupplyConfig { StartingCap = 32768 }; // >= the 16.16 range ceiling
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("scenario.supply.starting_cap", r.Error!);
        }

        [Fact]
        public void NegativeHardCeiling_IsRejected_LocatingTheField()
        {
            var m = ValidModel();
            m.Supply = new SupplyConfig { StartingCap = 0, HardCeiling = -1 };
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("scenario.supply.hard_ceiling", r.Error!);
        }

        [Fact]
        public void HardCeilingBelowStartingCap_IsRejected_NamingBothValues()
        {
            var m = ValidModel();
            m.Supply = new SupplyConfig { StartingCap = 20, HardCeiling = 5 };
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("scenario.supply.hard_ceiling", r.Error!);
            Assert.Contains("20", r.Error!);
            Assert.Contains("5", r.Error!);
        }

        [Fact]
        public void HardCeilingEqualToStartingCap_Passes()
        {
            // The ceiling clamps unconditionally, but hard_ceiling == starting_cap is a valid (if inert-against-
            // building-bonus) authored config — not a validation failure.
            var m = ValidModel();
            m.Supply = new SupplyConfig { StartingCap = 15, HardCeiling = 15 };
            Assert.True(NewValidator().Validate(m).Ok);
        }
    }
}
