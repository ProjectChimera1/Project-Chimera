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
    /// never logs — the call site decides shadow vs fail-closed (see ShadowModeTests).
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
    }
}
