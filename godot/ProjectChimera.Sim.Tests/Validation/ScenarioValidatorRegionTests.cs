#nullable enable
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 6.4 — <see cref="ScenarioValidator"/> gates regions fail-closed (unique non-empty id; a proper rect
    /// MinX&lt;MaxX &amp;&amp; MinZ&lt;MaxZ; all four corners within MapBounds) and rejects a <c>unit_in_region</c>
    /// condition that references an undefined <c>region_id</c> (dangling-ref, mirroring the timer_expires check).
    /// Every rejection names the offending field path, matching the located-error style of the sibling loops.
    /// </summary>
    public class ScenarioValidatorRegionTests
    {
        private static ScenarioValidator NewValidator() => new();

        /// <summary>A minimal VALID model: one declared slot, one well-formed region inside MapBounds.</summary>
        private static ScenarioData ValidModel() => new ScenarioData
        {
            MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[]
            {
                new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json", StartOre = 200f, BaseX = -45f, BaseZ = 0f },
            },
            Regions = new[]
            {
                new ScenarioRegion { Id = "hill", Name = "Hill", MinX = -10f, MinZ = -10f, MaxX = 10f, MaxZ = 10f },
            },
        };

        [Fact]
        public void WellFormedRegion_Passes()
        {
            ValidationResult r = NewValidator().Validate(ValidModel());
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void NullRegions_IsValid_NoRegionsToCheck()
        {
            var m = ValidModel();
            m.Regions = null; // omit-when-null: every existing scenario
            ValidationResult r = NewValidator().Validate(m);
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void EmptyId_IsRejected_LocatingRegionId()
        {
            var m = ValidModel();
            m.Regions![0].Id = "";
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("scenario.regions[0].id", r.Error!);
        }

        [Fact]
        public void DuplicateId_IsRejected_LocatingRegionId()
        {
            var m = ValidModel();
            m.Regions = new[]
            {
                new ScenarioRegion { Id = "dup", Name = "A", MinX = -10f, MinZ = -10f, MaxX = 0f, MaxZ = 0f },
                new ScenarioRegion { Id = "dup", Name = "B", MinX = 1f, MinZ = 1f, MaxX = 10f, MaxZ = 10f },
            };
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("scenario.regions[1].id", r.Error!);
        }

        [Fact]
        public void InvertedRectOnX_IsRejected()
        {
            var m = ValidModel();
            m.Regions![0].MinX = 10f;
            m.Regions[0].MaxX = -10f; // MinX >= MaxX
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("min_x", r.Error!);
        }

        [Fact]
        public void DegenerateRectOnZ_MinEqualsMax_IsRejected()
        {
            var m = ValidModel();
            m.Regions![0].MinZ = 5f;
            m.Regions[0].MaxZ = 5f; // MinZ == MaxZ (not strictly less)
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("min_z", r.Error!);
        }

        [Fact]
        public void SubResolutionRect_ValidInFloatButCollapsesAtFixed_IsRejected()
        {
            // Review patch (follow-up): a rect narrower than the Fixed (16.16) step is min<max in float (so the
            // float checks pass) yet collapses to min==max after Fixed.FromFloat — the applier would silently drop it
            // from the RegionStore, orphaning any unit_in_region trigger that names it. The validator now rejects it
            // in the SAME (Fixed) domain the sim resolves, so a passing region is guaranteed to survive the store.
            var m = ValidModel();
            m.Regions![0].MinX = 0f;
            m.Regions[0].MaxX = 1e-6f; // < 1/65536 ⇒ both corners quantize to the same Fixed
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("degenerate rect at 16.16 resolution", r.Error!);
        }

        [Fact]
        public void CornerOutsideMapBounds_IsRejected()
        {
            var m = ValidModel();
            m.Regions![0].MaxX = 200f; // beyond ±120 map bounds
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("scenario.regions[0].max_x", r.Error!);
        }

        [Fact]
        public void UnitInRegionCondition_ReferencingDeclaredRegion_Passes()
        {
            var m = ValidModel();
            m.Triggers = new[]
            {
                new TriggerDefinition
                {
                    Name = "koth",
                    Events = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                    Conditions = new[] { new TriggerCondition { Type = "unit_in_region", Faction = 0, RegionId = "hill" } },
                    Actions = new[] { new TriggerAction { Type = "victory", Faction = 0 } },
                },
            };
            ValidationResult r = NewValidator().Validate(m);
            Assert.True(r.Ok, r.Error);
        }

        [Fact]
        public void UnitInRegionCondition_WithDanglingRegionId_IsRejected()
        {
            var m = ValidModel();
            m.Triggers = new[]
            {
                new TriggerDefinition
                {
                    Name = "koth",
                    Events = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                    Conditions = new[] { new TriggerCondition { Type = "unit_in_region", Faction = 0, RegionId = "nowhere" } },
                    Actions = new[] { new TriggerAction { Type = "victory", Faction = 0 } },
                },
            };
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("region_id", r.Error!);
        }

        [Fact]
        public void UnitInRegionCondition_WithNoRegionId_IsRejected()
        {
            var m = ValidModel();
            m.Triggers = new[]
            {
                new TriggerDefinition
                {
                    Name = "koth",
                    Events = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                    Conditions = new[] { new TriggerCondition { Type = "unit_in_region", Faction = 0, RegionId = null } },
                    Actions = new[] { new TriggerAction { Type = "victory", Faction = 0 } },
                },
            };
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("region_id", r.Error!);
        }
    }
}
