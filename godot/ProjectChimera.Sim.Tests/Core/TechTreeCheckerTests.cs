#nullable enable
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Core
{
    /// <summary>
    /// Story 4.2 — <see cref="TechTreeChecker"/>'s generalization from the old hardcoded
    /// <c>ParseBuildingType</c>/<c>DisplayName</c> switches (covering only the 5 legacy enum-backed building ids)
    /// to a pure string match against <see cref="BuildingStore.DefinitionId"/>. Proves ANY authored building id —
    /// including a <see cref="BuildingType.Custom"/> building with no dedicated enum member — can now be
    /// satisfied/named as a prerequisite (previously impossible: <c>ParseBuildingType("watchtower")</c> would
    /// have returned null and the prereq could never be met).
    /// </summary>
    public class TechTreeCheckerTests
    {
        [Fact]
        public void AreMet_CustomBuildingId_SatisfiesPrereq_PreviouslyImpossibleUnderParseBuildingType()
        {
            var store = new BuildingStore();
            var faction = Faction.Player1;

            int id = store.Create(FixedVec3.Zero, faction, BuildingType.Custom,
                buildingId: "watchtower",
                health: Fixed.FromFloat(150f), supplyBonus: 0, constructionDuration: Fixed.FromFloat(8f));
            Assert.True(id >= 0);
            store.ConstructionTimer[id] = Fixed.Zero; // construction complete

            Assert.True(TechTreeChecker.AreMet(store, faction, new[] { "watchtower" }));
        }

        [Fact]
        public void AreMet_CustomBuildingId_StillUnderConstruction_NotYetMet()
        {
            var store = new BuildingStore();
            var faction = Faction.Player1;

            int id = store.Create(FixedVec3.Zero, faction, BuildingType.Custom,
                buildingId: "watchtower",
                health: Fixed.FromFloat(150f), supplyBonus: 0, constructionDuration: Fixed.FromFloat(8f));
            Assert.True(id >= 0);
            // ConstructionTimer left at its full duration (not yet complete).

            Assert.False(TechTreeChecker.AreMet(store, faction, new[] { "watchtower" }));
        }

        [Fact]
        public void FirstMissing_NoMatchingBuilding_ReturnsRawIdUnchanged_NoDisplayNameLookup()
        {
            // TechTreeChecker has no FactionDefinition in scope — FirstMissing must return the RAW prereq id, not
            // a resolved display name (that resolution moved out to BuildingSystem/EntityPlacer, Story 4.2).
            var store = new BuildingStore();
            Assert.Equal("watchtower", TechTreeChecker.FirstMissing(store, Faction.Player1, new[] { "watchtower" }));
        }

        [Fact]
        public void FirstMissing_AllMet_ReturnsNull()
        {
            var store = new BuildingStore();
            var faction = Faction.Player1;
            int id = store.Create(FixedVec3.Zero, faction, BuildingType.Custom,
                buildingId: "watchtower",
                health: Fixed.FromFloat(150f), supplyBonus: 0, constructionDuration: Fixed.FromFloat(8f));
            store.ConstructionTimer[id] = Fixed.Zero;

            Assert.Null(TechTreeChecker.FirstMissing(store, faction, new[] { "watchtower" }));
        }

        [Fact]
        public void AreMet_NullOrEmptyPrereqs_AlwaysTrue()
        {
            var store = new BuildingStore();
            Assert.True(TechTreeChecker.AreMet(store, Faction.Player1, null));
            Assert.True(TechTreeChecker.AreMet(store, Faction.Player1, System.Array.Empty<string>()));
        }

        [Fact]
        public void AreMet_LegacyEnumBackedId_StillResolves_ByStringMatch()
        {
            // Enum-backed ids still work — now via DefinitionId string match (BuildingStore.Create's legacy
            // no-def path sets DefinitionId to TechTreeChecker.BuildingTypeId(type)), not the retired enum switch.
            var store = new BuildingStore();
            var faction = Faction.Player1;
            int id = store.Create(FixedVec3.Zero, faction, BuildingType.Barracks);
            store.ConstructionTimer[id] = Fixed.Zero;

            Assert.True(TechTreeChecker.AreMet(store, faction, new[] { "barracks" }));
        }

        [Fact]
        public void AreMet_WrongFaction_NotMet()
        {
            var store = new BuildingStore();
            int id = store.Create(FixedVec3.Zero, Faction.Player1, BuildingType.Custom,
                buildingId: "watchtower",
                health: Fixed.FromFloat(150f), supplyBonus: 0, constructionDuration: Fixed.FromFloat(8f));
            store.ConstructionTimer[id] = Fixed.Zero;

            Assert.False(TechTreeChecker.AreMet(store, Faction.Player2, new[] { "watchtower" }));
        }
    }
}
