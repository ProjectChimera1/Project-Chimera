#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Economy;
using Xunit;

namespace ProjectChimera.Sim.Tests.Core
{
    /// <summary>
    /// Story 4.1 (AC2/AC3) — <see cref="BuildingStore.Create"/>'s new additive resolved-stats path. For each of the
    /// five enum-backed types (the four AC2 showcase types plus Aviary, which the spec's Boundaries also require
    /// corrected since <c>BuildingSystem</c> resolves-and-passes a def for whichever type is placed), the
    /// switch-fallback (no def) and the def-resolved path (explicit health/supplyBonus/constructionDuration) must
    /// produce IDENTICAL Health/SupplyBonus/ConstructionTimer — proving the data-driven path reproduces today's
    /// baked constants exactly (AC1's "byte-identical" requirement, exercised directly at the store level rather
    /// than through a loaded faction). Also proves <see cref="BuildingType.Custom"/> — a building with no matching
    /// enum member — is creatable via the resolved-stats params alone (AC2's core claim: the switch has no case for
    /// Custom, so this is its ONLY path to non-default stats).
    /// </summary>
    public class BuildingStoreDataDrivenTests
    {
        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        [Theory]
        [InlineData(BuildingType.CommandCenter, 500f, 10, 15f)]
        [InlineData(BuildingType.Barracks,      300f, 0,  10f)]
        [InlineData(BuildingType.ArcheryRange,  300f, 0,  10f)]
        [InlineData(BuildingType.SiegeWorkshop, 400f, 0,  12f)]
        [InlineData(BuildingType.Aviary,        350f, 0,  12f)]
        public void DefResolvedStats_MatchSwitchFallback_ForEachShowcaseType(
            BuildingType type, float hp, int supplyBonus, float constructionSeconds)
        {
            var store = new BuildingStore();

            // Legacy path: no def params → the untouched per-type switch (today's baked constants).
            int legacy = store.Create(V(0, 0), Faction.Player1, type);

            // Data-driven path: explicit resolved stats (as BuildingSystem now threads from a loaded BuildingDefinition).
            int dataDriven = store.Create(V(5, 0), Faction.Player1, type,
                health: Fixed.FromFloat(hp), supplyBonus: supplyBonus,
                constructionDuration: Fixed.FromFloat(constructionSeconds));

            Assert.Equal(store.Health[legacy].Raw, store.Health[dataDriven].Raw);
            Assert.Equal(store.MaxHealth[legacy].Raw, store.MaxHealth[dataDriven].Raw);
            Assert.Equal(store.SupplyBonus[legacy], store.SupplyBonus[dataDriven]);
            Assert.Equal(store.ConstructionTimer[legacy].Raw, store.ConstructionTimer[dataDriven].Raw);
            Assert.Equal(store.ConstructionDuration[legacy].Raw, store.ConstructionDuration[dataDriven].Raw);

            // The data-driven slot's authored values also match the expected baked constants directly (not just
            // parity with the legacy slot) — belt-and-suspenders against a theoretical bug shared by both branches.
            Assert.Equal(Fixed.FromFloat(hp).Raw, store.Health[dataDriven].Raw);
            Assert.Equal(supplyBonus, store.SupplyBonus[dataDriven]);
            Assert.Equal(Fixed.FromFloat(constructionSeconds).Raw, store.ConstructionTimer[dataDriven].Raw);
        }

        [Fact]
        public void Custom_NoEnumEntry_CreatesWithResolvedStatsAndDefinitionId()
        {
            var store = new BuildingStore();

            int id = store.Create(V(0, 0), Faction.Player1, BuildingType.Custom,
                buildingId: "watchtower",
                health: Fixed.FromFloat(150f), supplyBonus: 0, constructionDuration: Fixed.FromFloat(8f));

            Assert.True(id >= 0);
            Assert.True(store.Alive[id]);
            Assert.Equal(BuildingType.Custom, store.Type[id]);
            Assert.Equal("watchtower", store.DefinitionId[id]);
            Assert.Equal(Fixed.FromFloat(150f).Raw, store.Health[id].Raw);
            Assert.Equal(0, store.SupplyBonus[id]);
            Assert.Equal(Fixed.FromFloat(8f).Raw, store.ConstructionTimer[id].Raw);
        }

        [Fact]
        public void LegacyCall_NoNewParams_StillCompilesAndBehavesUnchanged()
        {
            // AC4 — the existing bare positional overload (position, faction, type) keeps compiling and behaving
            // identically: no def params supplied → the switch fallback, exactly as before this story.
            var store = new BuildingStore();
            int id = store.Create(V(1, 1), Faction.Player2, BuildingType.Barracks);
            Assert.True(id >= 0);
            Assert.Equal(Fixed.FromFloat(300f).Raw, store.Health[id].Raw);
            Assert.Equal(0, store.SupplyBonus[id]);
            Assert.Equal(Fixed.FromFloat(10f).Raw, store.ConstructionTimer[id].Raw);
        }

        [Fact]
        public void DefinitionId_LegacyPath_DefaultsToTechTreeCheckerId()
        {
            var store = new BuildingStore();
            int id = store.Create(V(0, 0), Faction.Player1, BuildingType.Aviary);
            Assert.Equal(TechTreeChecker.BuildingTypeId(BuildingType.Aviary), store.DefinitionId[id]);
        }

        [Fact]
        public void DefinitionId_ResetOnRecycle()
        {
            var store = new BuildingStore();
            int a = store.Create(V(0, 0), Faction.Player1, BuildingType.Custom,
                buildingId: "watchtower", health: Fixed.FromFloat(150f), supplyBonus: 0,
                constructionDuration: Fixed.FromFloat(8f));
            store.Destroy(a);

            // Recycled into a plain enum-backed building with no explicit id → DefinitionId must NOT carry the prior
            // occupant's "watchtower" (the SoA-recycle contract).
            int b = store.Create(V(0, 0), Faction.Player1, BuildingType.Barracks);
            Assert.Equal(a, b); // same slot reused (LIFO free-list)
            Assert.Equal(TechTreeChecker.BuildingTypeId(BuildingType.Barracks), store.DefinitionId[b]);
        }

        [Fact]
        public void Clear_ResetsDefinitionId()
        {
            var store = new BuildingStore();
            store.Create(V(0, 0), Faction.Player1, BuildingType.Custom,
                buildingId: "watchtower", health: Fixed.FromFloat(150f), supplyBonus: 0,
                constructionDuration: Fixed.FromFloat(8f));
            store.Clear();

            var fresh = new BuildingStore();
            Assert.Equal(fresh.DefinitionId, store.DefinitionId);
        }

        /// <summary>
        /// Review pass (Verification Gap finding): every OTHER test authors building stats that numerically equal
        /// <see cref="BuildingStore.Create"/>'s switch-fallback constants, so a silently-broken
        /// <c>BuildingSystem</c>-level threading path (e.g. the <c>health:</c>/<c>supplyBonus:</c>/
        /// <c>constructionDuration:</c> args accidentally dropped back to null in <c>PlaceBuildingDirect</c>) would
        /// still produce byte-identical results and go undetected. This test authors a command_center whose stats
        /// deliberately DIFFER from the switch's CommandCenter case (500/10/15s) and asserts the DEF's values — not
        /// the switch's — land in the store, proving the threading is genuinely exercised through
        /// <see cref="BuildingSystem.PlaceBuildingDirect"/>, not just coincidentally correct.
        /// </summary>
        [Fact]
        public void PlaceBuildingDirect_ThreadsDefStats_NotSwitchDefaults()
        {
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            var buildSys  = new BuildingSystem(buildings, resources);

            var fdef = new FactionDefinition();
            fdef.Buildings.Add(new BuildingDefinition
            {
                Id = "command_center", Category = "Structure",
                Hp = 999f, ConstructionTime = 33f, SupplyBonus = 42, ProducesCategory = "Worker",
            });
            buildSys.SetFactionDef(Faction.Player1, fdef);

            int id = buildSys.PlaceBuildingDirect(BuildingType.CommandCenter, Faction.Player1, V(0, 0), preBuilt: false);

            Assert.True(id >= 0);
            Assert.Equal(Fixed.FromFloat(999f).Raw, buildings.Health[id].Raw);
            Assert.Equal(Fixed.FromFloat(999f).Raw, buildings.MaxHealth[id].Raw);
            Assert.Equal(42, buildings.SupplyBonus[id]);
            Assert.Equal(Fixed.FromFloat(33f).Raw, buildings.ConstructionTimer[id].Raw);
            Assert.Equal(Fixed.FromFloat(33f).Raw, buildings.ConstructionDuration[id].Raw);

            // Sanity: these values are NOT the switch's CommandCenter constants (500/10/15s) — if they were, this
            // test would pass even with the threading silently broken.
            Assert.NotEqual(Fixed.FromFloat(500f).Raw, buildings.Health[id].Raw);
            Assert.NotEqual(10, buildings.SupplyBonus[id]);
            Assert.NotEqual(Fixed.FromFloat(15f).Raw, buildings.ConstructionTimer[id].Raw);
        }

        /// <summary>
        /// Review pass: a <c>bdef</c> resolved from a <see cref="FactionDefinition"/> built outside
        /// <see cref="FactionDefinition.LoadFromFile"/>'s validation gate (e.g. hand-constructed in a test or
        /// editor tool) can have a null <see cref="BuildingDefinition.ConstructionTime"/>/<see
        /// cref="BuildingDefinition.SupplyBonus"/> even though its id matches a real building — this must degrade
        /// gracefully to the switch fallback (matching the documented null-bdef contract), never throw.
        /// </summary>
        [Fact]
        public void PlaceBuildingDirect_PartiallyPopulatedDef_FallsBackToSwitch_NoThrow()
        {
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            var buildSys  = new BuildingSystem(buildings, resources);

            var fdef = new FactionDefinition();
            fdef.Buildings.Add(new BuildingDefinition { Id = "command_center", Category = "Structure" }); // no ConstructionTime/SupplyBonus/ProducesCategory
            buildSys.SetFactionDef(Faction.Player1, fdef);

            int id = buildSys.PlaceBuildingDirect(BuildingType.CommandCenter, Faction.Player1, V(0, 0), preBuilt: false);

            Assert.True(id >= 0);
            Assert.Equal(Fixed.FromFloat(500f).Raw, buildings.Health[id].Raw); // switch fallback, not a crash
            Assert.Equal(10, buildings.SupplyBonus[id]);
            Assert.Equal(Fixed.FromFloat(15f).Raw, buildings.ConstructionTimer[id].Raw);
        }
    }
}
