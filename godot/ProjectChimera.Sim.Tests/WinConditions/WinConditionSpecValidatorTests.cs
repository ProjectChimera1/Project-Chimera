#nullable enable
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.WinConditions
{
    /// <summary>
    /// Story 7.11 (AC5) — an invalid/missing preset param is rejected at LOAD with ONE located error naming the
    /// preset and the offending param (never a runtime crash, never a silently un-winnable match). A valid preset
    /// (and every pre-7.11 no-preset scenario) passes. The base model is <see cref="ScenarioData.CreateBlank"/>,
    /// which is guaranteed to pass <see cref="ScenarioValidator.Validate"/> — so the ONLY failure is the preset.
    /// </summary>
    public class WinConditionSpecValidatorTests
    {
        private static ScenarioData Base() => ScenarioData.CreateBlank("wincon-test");
        private static ValidationResult Validate(ScenarioData s) => new ScenarioValidator().Validate(s);

        private static ScenarioRegion[] Zone() =>
            new[] { new ScenarioRegion { Id = "zone", Name = "Zone", MinX = -5, MinZ = -5, MaxX = 5, MaxZ = 5 } };

        [Fact]
        public void NoPreset_Passes()
        {
            Assert.True(Validate(Base()).Ok);
            var s = Base();
            s.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.None };
            Assert.True(Validate(s).Ok);
        }

        [Fact]
        public void KotH_UndefinedRegion_Rejected()
        {
            var s = Base();
            s.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.KingOfTheHill, RegionId = "nope", HoldTicks = 300 };
            var r = Validate(s);
            Assert.False(r.Ok);
            Assert.Contains("region_id", r.Error);
        }

        [Fact]
        public void KotH_NonPositiveHoldTicks_Rejected()
        {
            var s = Base();
            s.Regions = Zone();
            s.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.KingOfTheHill, RegionId = "zone", HoldTicks = 0 };
            var r = Validate(s);
            Assert.False(r.Ok);
            Assert.Contains("hold_ticks", r.Error);
        }

        [Fact]
        public void KotH_Valid_Passes()
        {
            var s = Base();
            s.Regions = Zone();
            s.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.KingOfTheHill, RegionId = "zone", HoldTicks = 300 };
            Assert.True(Validate(s).Ok);
        }

        [Fact]
        public void Survival_UndeclaredFactionSlot_Rejected()
        {
            var s = Base();
            s.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.TimedSurvival, FactionSlot = 7, SurviveTicks = 900 };
            var r = Validate(s);
            Assert.False(r.Ok);
            Assert.Contains("faction_slot", r.Error);
        }

        [Fact]
        public void Survival_NonPositiveSurviveTicks_Rejected()
        {
            var s = Base();
            s.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.TimedSurvival, FactionSlot = 0, SurviveTicks = 0 };
            var r = Validate(s);
            Assert.False(r.Ok);
            Assert.Contains("survive_ticks", r.Error);
        }

        [Fact]
        public void Survival_Valid_Passes()
        {
            var s = Base();
            s.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.TimedSurvival, FactionSlot = 0, SurviveTicks = 900 };
            Assert.True(Validate(s).Ok);
        }

        [Fact]
        public void Assassination_LeaderIndexOutOfRange_Rejected()
        {
            var s = Base(); // no units
            s.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.Assassination, LeaderUnitIndex = 0 };
            var r = Validate(s);
            Assert.False(r.Ok);
            Assert.Contains("leader_unit_index", r.Error);
        }

        [Fact]
        public void Assassination_Valid_Passes()
        {
            var s = Base();
            s.Units = new[] { new ScenarioUnit { UnitId = "worker", Slot = 0, X = 0, Z = 0 } };
            s.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.Assassination, LeaderUnitIndex = 0 };
            Assert.True(Validate(s).Ok);
        }

        [Fact]
        public void Landmark_StructureIndexOutOfRange_Rejected()
        {
            var s = Base(); // no buildings
            s.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.LandmarkDestruction, StructureIndex = 0 };
            var r = Validate(s);
            Assert.False(r.Ok);
            Assert.Contains("structure_index", r.Error);
        }

        [Fact]
        public void Landmark_Valid_Passes()
        {
            var s = Base();
            s.Buildings = new[] { new ScenarioBuilding { Type = "CommandCenter", Slot = 0, X = 0, Z = 0 } };
            s.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.LandmarkDestruction, StructureIndex = 0 };
            Assert.True(Validate(s).Ok);
        }

        // ── Review P3: an UNKNOWN preset value (hand-edited JSON) fails closed instead of evaluating nothing ─────

        [Fact]
        public void UnknownPresetKind_Rejected_NamingTheBadValue()
        {
            var s = Base();
            s.WinConditionSpec = new WinConditionSpec { Preset = (WinPresetKind)99 };
            var r = Validate(s);
            Assert.False(r.Ok);
            Assert.Contains("preset=99", r.Error);
        }

        // ── Review P4: the ENGINE faction ceiling ([0,7] — the sim tracks Faction 1-8 after Story 9.2) on preset slots ──

        [Fact]
        public void Survival_FactionSlotBeyondEngineCeiling_Rejected()
        {
            // Story 9.2 raised the engine ceiling to Player8 ([0,7]); slot 8 can never be seeded into the length-9
            // win stores: without the ceiling check Configure would silently skip seeding and FactionAlive would
            // read false → the wrong faction wins on tick 1. The ceiling check runs BEFORE the declared-slot rule
            // so ITS located error is the one surfaced.
            var s = Base();
            s.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.TimedSurvival, FactionSlot = 8, SurviveTicks = 900 };
            var r = Validate(s);
            Assert.False(r.Ok);
            Assert.Contains("faction_slot", r.Error);
            Assert.Contains("[0,7]", r.Error);
        }

        [Fact]
        public void Assassination_DesignatedLeaderSlotBeyondEngineCeiling_Rejected()
        {
            // Today the units loop's declared-slot rule rejects this first (declared slots are themselves capped
            // at 3); the preset-located ceiling check is the load-fatal backstop for when Story 9.2 relaxes the
            // declared ceiling ahead of the length-5 win stores. Either way: fail-closed, never a tick-1 false loss.
            var s = Base();
            s.Units = new[] { new ScenarioUnit { UnitId = "worker", Slot = 4, X = 0, Z = 0 } };
            s.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.Assassination, LeaderUnitIndex = 0 };
            var r = Validate(s);
            Assert.False(r.Ok);
            Assert.Contains("slot", r.Error!);
        }

        [Fact]
        public void Landmark_DesignatedStructureSlotBeyondEngineCeiling_Rejected()
        {
            // Same shape as the Assassination case above — the buildings loop rejects first today; the
            // preset-located ceiling check is the Story-9.2 backstop.
            var s = Base();
            s.Buildings = new[] { new ScenarioBuilding { Type = "CommandCenter", Slot = 4, X = 0, Z = 0 } };
            s.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.LandmarkDestruction, StructureIndex = 0 };
            var r = Validate(s);
            Assert.False(r.Ok);
            Assert.Contains("slot", r.Error!);
        }
    }
}
