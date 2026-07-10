#nullable enable
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 4.7 — <see cref="ScenarioValidator"/>'s resource-node loop gates the 6 new fields
    /// (<c>collection_model</c>, <c>resource_type</c>, <c>requires_structure_radius</c>, <c>income_period_ticks</c>,
    /// and the <c>owner_slot</c>-required-for-Income cross-reference). Mirrors <c>NegativeValidationTests</c>'
    /// located-error style: every rejection names the offending field path.
    /// </summary>
    public class ScenarioValidatorResourceNodeTests
    {
        private static ScenarioValidator NewValidator() => new();

        /// <summary>A minimal VALID model: one declared slot, one default (Gather/Ore) node.</summary>
        private static ScenarioData ValidModel() => new ScenarioData
        {
            MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[]
            {
                new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json", StartOre = 200f, BaseX = -45f, BaseZ = 0f },
            },
            ResourceNodes = new[] { new ScenarioResourceNode { X = 10f, Z = 10f, Supply = 400f, Rate = 5f, MaxGatherers = 4 } },
        };

        [Fact]
        public void DefaultNode_OmittingAllSixFields_Passes()
        {
            ValidationResult r = NewValidator().Validate(ValidModel());
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void UnknownCollectionModel_IsRejected_LocatingCollectionModel()
        {
            var m = ValidModel();
            m.ResourceNodes[0].CollectionModel = "Trickle";
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("resource_nodes[0].collection_model", r.Error!);
        }

        [Fact]
        public void UnknownResourceType_IsRejected_LocatingResourceType()
        {
            var m = ValidModel();
            m.ResourceNodes[0].ResourceType = "Gems";
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("resource_nodes[0].resource_type", r.Error!);
        }

        [Fact]
        public void IncomeCollectionModel_WithOmittedOwnerSlot_IsRejected()
        {
            var m = ValidModel();
            m.ResourceNodes[0].CollectionModel = "Income"; // owner_slot stays at its -1 default
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("resource_nodes[0].owner_slot", r.Error!);
        }

        [Fact]
        public void IncomeCollectionModel_WithUndeclaredOwnerSlot_IsRejected()
        {
            var m = ValidModel();
            m.ResourceNodes[0].CollectionModel = "Income";
            m.ResourceNodes[0].OwnerSlot = 7; // no player_slot declares 7
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("resource_nodes[0].owner_slot", r.Error!);
        }

        [Fact]
        public void IncomeCollectionModel_WithDeclaredOwnerSlot_Passes()
        {
            var m = ValidModel();
            m.ResourceNodes[0].CollectionModel = "Income";
            m.ResourceNodes[0].OwnerSlot = 0; // declared by PlayerSlots[0]
            ValidationResult r = NewValidator().Validate(m);
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void GatherCollectionModel_WithOmittedOwnerSlot_IsNotRejected()
        {
            // owner_slot is only load-bearing for Income; GATHER/Streaming credit the gathering worker's own
            // faction, so an unset (-1) owner_slot must never fail validation for those models.
            var m = ValidModel(); // CollectionModel stays "Gather", OwnerSlot stays -1
            ValidationResult r = NewValidator().Validate(m);
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void NegativeRequiresStructureRadius_IsRejected_LocatingRequiresStructureRadius()
        {
            var m = ValidModel();
            m.ResourceNodes[0].RequiresStructureRadius = -1f;
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("resource_nodes[0].requires_structure_radius", r.Error!);
        }

        [Fact]
        public void NegativeIncomePeriodTicks_IsRejected_LocatingIncomePeriodTicks()
        {
            var m = ValidModel();
            m.ResourceNodes[0].IncomePeriodTicks = -5;
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("resource_nodes[0].income_period_ticks", r.Error!);
        }

        [Fact]
        public void IncomeCollectionModel_WithZeroIncomePeriodTicks_IsRejected()
        {
            // Review patch: income_period_ticks=0 only failed the bare non-negative check, but combined with
            // collection_model=Income it produces a degenerate "credit every tick" mode (IncomeTicksElapsed's
            // `< 0` comparison is never true), not the intended periodic trickle.
            var m = ValidModel();
            m.ResourceNodes[0].CollectionModel = "Income";
            m.ResourceNodes[0].OwnerSlot = 0;
            m.ResourceNodes[0].IncomePeriodTicks = 0;
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("resource_nodes[0].income_period_ticks", r.Error!);
        }

        [Fact]
        public void GatherCollectionModel_WithZeroIncomePeriodTicks_IsNotRejected()
        {
            // income_period_ticks is only load-bearing for Income; GATHER/Streaming ignore the field entirely, so
            // a 0 there is inert, not an authoring error.
            var m = ValidModel(); // CollectionModel stays "Gather"
            m.ResourceNodes[0].IncomePeriodTicks = 0;
            ValidationResult r = NewValidator().Validate(m);
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void ArbitraryRequiresStructureString_IsAccepted_CreatorExtensibleId()
        {
            // requires_structure matches BuildingStore.DefinitionId (a creator-extensible string), NOT the closed
            // BuildingType enum — any non-empty id is valid content at validation time (existence is a runtime,
            // not authoring-time, concern).
            var m = ValidModel();
            m.ResourceNodes[0].RequiresStructure = "my_custom_watchtower";
            ValidationResult r = NewValidator().Validate(m);
            Assert.True(r.Ok, r.Error);
        }
    }
}
