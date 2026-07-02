#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Economy;
using ProjectChimera.Multiplayer; // OrderApplier / UnitOrder (Task 9 — lockstep Train command)
using Xunit;

namespace ProjectChimera.Sim.Tests.Economy
{
    /// <summary>
    /// Story 2.8 — per-unit production selection + the Air producer (Tasks 1, 2, 4, 7).
    ///
    /// Proves the sim core: a building can train ANY unit of its category (not just first-of-category),
    /// the chosen-unit index is bounds- and category-guarded before any spend, the gate order/spend-last
    /// invariant is preserved, and the new Aviary building maps to the "Air" category. All Godot-free.
    /// </summary>
    public class ProductionSelectionTests
    {
        // A faction whose Melee (1,2,3) and Ranged (4,5) categories have SIBLINGS, so "train the 2nd" is
        // distinguishable. Each unit has a UNIQUE Hp so the spawned entity's identity is unambiguous.
        private static FactionDefinition TestFaction()
        {
            var f = new FactionDefinition { Id = "test", DisplayName = "Test" };
            f.Units.Add(new UnitDefinition { Id = "worker",   Category = "Worker", Hp = 50f });   // 0
            f.Units.Add(new UnitDefinition { Id = "melee_a",  Category = "Melee",  Hp = 100f });  // 1  first-of-Melee
            f.Units.Add(new UnitDefinition { Id = "melee_b",  Category = "Melee",  Hp = 110f });  // 2
            f.Units.Add(new UnitDefinition { Id = "melee_c",  Category = "Melee",  Hp = 120f });  // 3
            f.Units.Add(new UnitDefinition { Id = "ranged_a", Category = "Ranged", Hp = 80f });   // 4  first-of-Ranged
            f.Units.Add(new UnitDefinition { Id = "ranged_b", Category = "Ranged", Hp = 90f });   // 5
            f.Units.Add(new UnitDefinition { Id = "siege",    Category = "Siege",  Hp = 300f });  // 6
            f.Units.Add(new UnitDefinition { Id = "air",      Category = "Air",    Hp = 200f });  // 7
            return f;
        }

        private static (BuildingSystem sys, BuildingStore buildings, ResourceStore resources, EntityWorld world)
            Harness(FactionDefinition faction)
        {
            var world     = new EntityWorld();
            var buildings = new BuildingStore();
            // Fixed is 16.16 — integer range ±32767, so keep ore well under that (unit costs are 50-300).
            var resources = new ResourceStore(Fixed.FromInt(10000));
            resources.Ore[(int)Faction.Player1]       = Fixed.FromInt(10000);
            resources.SupplyCap[(int)Faction.Player1] = 500;
            var sys = new BuildingSystem(buildings, resources, faction);
            return (sys, buildings, resources, world);
        }

        /// <summary>Force the queued unit to spawn (one big-dt tick expires the train timer); return its id.</summary>
        private static int SpawnNext(BuildingSystem sys, EntityWorld world)
        {
            sys.Tick(world, Fixed.FromInt(100));
            for (int i = 0; i < world.HighWaterMark; i++)
                if (world.IsAlive(i)) return i;
            return -1;
        }

        // ── Task 1 — plural category lookup ────────────────────────────────────────────────────────────

        [Fact]
        public void GetUnitsByCategory_ReturnsEveryMatch_InAscendingListOrder()
        {
            var melee = TestFaction().GetUnitsByCategory("Melee");
            Assert.Equal(3, melee.Count);
            Assert.Equal((1, "melee_a"), (melee[0].Index, melee[0].Def.Id));
            Assert.Equal((2, "melee_b"), (melee[1].Index, melee[1].Def.Id));
            Assert.Equal((3, "melee_c"), (melee[2].Index, melee[2].Def.Id));
        }

        [Fact]
        public void GetUnitsByCategory_IsCaseInsensitive()
            => Assert.Equal(2, TestFaction().GetUnitsByCategory("ranged").Count);

        [Fact]
        public void GetUnitsByCategory_EmptyForUnknownCategory()
            => Assert.Empty(TestFaction().GetUnitsByCategory("Hero"));

        [Fact]
        public void GetProductionUnits_MapsBuildingToItsCategory()
        {
            var (sys, _, _, _) = Harness(TestFaction());
            Assert.Equal(3, sys.GetProductionUnits(BuildingType.Barracks).Count);     // Melee
            Assert.Equal(2, sys.GetProductionUnits(BuildingType.ArcheryRange).Count); // Ranged
            Assert.Single(sys.GetProductionUnits(BuildingType.Aviary));               // Air (Task 4 arm has teeth)
            Assert.Empty(sys.GetProductionUnits(BuildingType.CommandCenter));         // trains nothing
        }

        // ── Task 2 — per-unit selection: trains the CHOSEN unit, not first-of-category ─────────────────

        [Fact]
        public void TrainUnit_ChoosingSecondMelee_SpawnsThatExactUnit_NotFirst()
        {
            var (sys, _, resources, world) = Harness(TestFaction());
            int b = sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, FixedVec3.Zero, preBuilt: true);

            Assert.True(sys.TrainUnit(b, resources, chosenUnitIndex: 2)); // melee_b
            int u = SpawnNext(sys, world);

            Assert.True(u >= 0);
            Assert.Equal(UnitCategory.Melee, world.CategoryOf[u]);
            Assert.Equal((byte)2, world.MeshType[u]);                                   // the Units index == mesh coordinate
            Assert.Equal(Fixed.FromFloat(110f).Raw, world.EffectiveMaxHealth[u].Raw);   // melee_b's Hp, NOT melee_a's 100
        }

        [Fact]
        public void TrainUnit_DefaultIndex_SpawnsFirstOfCategory_ByteIdenticalToLegacy()
        {
            var (sys, _, resources, world) = Harness(TestFaction());
            int b = sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, FixedVec3.Zero, preBuilt: true);

            Assert.True(sys.TrainUnit(b, resources)); // chosenUnitIndex defaults to -1 (the AI / 2-arg path)
            int u = SpawnNext(sys, world);

            Assert.Equal(UnitCategory.Melee, world.CategoryOf[u]);
            Assert.Equal((byte)1, world.MeshType[u]);                                  // melee_a = first-of-Melee
            Assert.Equal(Fixed.FromFloat(100f).Raw, world.EffectiveMaxHealth[u].Raw);
        }

        // ── Task 2 — guards: out-of-range + wrong-category are HARD-REJECTED with no spend ─────────────

        [Fact]
        public void TrainUnit_OutOfRangeIndex_Rejects_SpendsNothing_QueuesNothing()
        {
            var (sys, buildings, resources, _) = Harness(TestFaction());
            int b = sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, FixedVec3.Zero, preBuilt: true);
            Fixed oreBefore = resources.Ore[(int)Faction.Player1];

            Assert.False(sys.TrainUnit(b, resources, chosenUnitIndex: 99));
            Assert.Equal(oreBefore.Raw, resources.Ore[(int)Faction.Player1].Raw);
            Assert.Equal(0, buildings.ProductionQueue[b]);
        }

        [Fact]
        public void TrainUnit_WrongCategoryIndex_Rejects_NoCrossCategoryTraining()
        {
            var (sys, buildings, resources, _) = Harness(TestFaction());
            int b = sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, FixedVec3.Zero, preBuilt: true);
            Fixed oreBefore = resources.Ore[(int)Faction.Player1];

            // Index 4 = ranged_a — a valid unit, but NOT a Melee unit the Barracks produces.
            Assert.False(sys.TrainUnit(b, resources, chosenUnitIndex: 4));
            Assert.Equal(oreBefore.Raw, resources.Ore[(int)Faction.Player1].Raw);
            Assert.Equal(0, buildings.ProductionQueue[b]);
        }

        // ── Task 2 — gate preservation: prereq / supply / ore each reject with spend-LAST intact ───────

        [Fact]
        public void TrainUnit_UnmetPrereq_SpendsNoOre()
        {
            var faction = TestFaction();
            faction.Units[2].Prerequisites = new[] { "archery_range" }; // melee_b now gated behind an un-built ArcheryRange
            var (sys, buildings, resources, _) = Harness(faction);
            int b = sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, FixedVec3.Zero, preBuilt: true);
            Fixed oreBefore = resources.Ore[(int)Faction.Player1];

            Assert.False(sys.TrainUnit(b, resources, chosenUnitIndex: 2));
            Assert.Equal(oreBefore.Raw, resources.Ore[(int)Faction.Player1].Raw);
            Assert.Equal(0, buildings.ProductionQueue[b]);
            // And the per-candidate prereq helper reports the specific unmet building for that unit.
            Assert.NotNull(sys.GetUnmetPrereq(b, 2));
            Assert.Null(sys.GetUnmetPrereq(b, 1)); // melee_a has no prereq
        }

        [Fact]
        public void TrainUnit_OverSupply_SpendsNoOre()
        {
            var (sys, buildings, resources, _) = Harness(TestFaction());
            int b = sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, FixedVec3.Zero, preBuilt: true);
            resources.SupplyUsed[(int)Faction.Player1] = 999; // already over any cap
            Fixed oreBefore = resources.Ore[(int)Faction.Player1];

            Assert.False(sys.TrainUnit(b, resources, chosenUnitIndex: 1));
            Assert.Equal(oreBefore.Raw, resources.Ore[(int)Faction.Player1].Raw);
            Assert.Equal(0, buildings.ProductionQueue[b]);
        }

        [Fact]
        public void TrainUnit_InsufficientOre_ReturnsFalse_QueuesNothing()
        {
            var (sys, buildings, resources, _) = Harness(TestFaction());
            int b = sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, FixedVec3.Zero, preBuilt: true);
            resources.Ore[(int)Faction.Player1] = Fixed.FromInt(10); // melee_a costs 50

            Assert.False(sys.TrainUnit(b, resources, chosenUnitIndex: 1));
            Assert.Equal(0, buildings.ProductionQueue[b]);
        }

        // ── Task 4 — Air production building trains the Air unit ───────────────────────────────────────

        [Fact]
        public void TrainUnit_Aviary_TrainsChosenAirUnit()
        {
            var (sys, _, resources, world) = Harness(TestFaction());
            int b = sys.PlaceBuildingDirect(BuildingType.Aviary, Faction.Player1, FixedVec3.Zero, preBuilt: true);

            Assert.True(sys.TrainUnit(b, resources, chosenUnitIndex: 7)); // air
            int u = SpawnNext(sys, world);

            Assert.Equal(UnitCategory.Air, world.CategoryOf[u]);
            Assert.Equal((byte)7, world.MeshType[u]);
            Assert.Equal(Fixed.FromFloat(200f).Raw, world.EffectiveMaxHealth[u].Raw);
        }

        [Fact]
        public void TrainUnit_AviaryDefault_SpawnsAir_NotMelee()
        {
            // Teeth for the CategoryForBuilding(Aviary => "Air") arm: with chosenUnitIndex = -1 the Aviary must
            // resolve first-of-AIR (index 7), NOT first-of-Melee (index 1). If the arm were missing (default
            // "Melee"), this spawns melee_a and the assertions fail.
            var (sys, _, resources, world) = Harness(TestFaction());
            int b = sys.PlaceBuildingDirect(BuildingType.Aviary, Faction.Player1, FixedVec3.Zero, preBuilt: true);

            Assert.True(sys.TrainUnit(b, resources));
            int u = SpawnNext(sys, world);

            Assert.Equal(UnitCategory.Air, world.CategoryOf[u]);
            Assert.Equal((byte)7, world.MeshType[u]);
        }

        // ── Empty-category producer: still trains a graceful FALLBACK (sentinel != 0) ──────────────────

        [Fact]
        public void TrainUnit_EmptyCategory_TrainsFallback_QueueSentinelNotIdle()
        {
            // A faction with NO Air unit: an Aviary default-trains the graceful fallback. ProductionQueue must
            // become the reserved sentinel (non-zero), never 0 — else the already-training guard breaks.
            var faction = TestFaction();
            faction.Units.RemoveAt(7); // drop the only Air unit
            var (sys, buildings, resources, world) = Harness(faction);
            int b = sys.PlaceBuildingDirect(BuildingType.Aviary, Faction.Player1, FixedVec3.Zero, preBuilt: true);

            Assert.True(sys.TrainUnit(b, resources));              // empty category → fallback path
            Assert.NotEqual(0, buildings.ProductionQueue[b]);      // busy, not idle
            Assert.False(sys.TrainUnit(b, resources));             // already-training guard holds (no double-queue)

            int u = SpawnNext(sys, world);
            Assert.True(u >= 0);                                   // a fallback unit still spawns
        }

        // ── Task 5 — the Aviary BuildingType round-trips through TechTreeChecker + resolves a display name ──

        [Fact]
        public void TechTreeChecker_Aviary_RoundTripsIdAndResolvesDisplayName()
        {
            // enum → snake_case json id.
            Assert.Equal("aviary", TechTreeChecker.BuildingTypeId(BuildingType.Aviary));

            // An UNMET "aviary" prereq surfaces the DISPLAY name "Aviary" (not the raw id) — proving BOTH the
            // ParseBuildingType("aviary") arm resolved AND the DisplayName(Aviary) arm is non-null. A missing arm
            // would fall back to the raw "aviary".
            var buildings = new BuildingStore();
            Assert.Equal("Aviary", TechTreeChecker.FirstMissing(buildings, Faction.Player1, new[] { "aviary" }));

            // A completed Aviary satisfies the prereq (round-trip closes: id → enum → alive-building check).
            int b = buildings.Create(FixedVec3.Zero, Faction.Player1, BuildingType.Aviary);
            buildings.ConstructionTimer[b] = Fixed.Zero; // mark construction complete
            Assert.True(TechTreeChecker.AreMet(buildings, Faction.Player1, new[] { "aviary" }));
        }

        // ── Task 9 — lockstep Train command (D-1): rides the shared OrderApplier, spends once at exec-tick ──

        [Fact]
        public void OrderApplier_Train_TrainsChosenUnit_IdenticallyToDirectCall()
        {
            // Route A — direct TrainUnit (today's AI / offline path).
            var a = Harness(TestFaction());
            int ba = a.sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, FixedVec3.Zero, preBuilt: true);
            Fixed aOreBefore = a.resources.Ore[(int)Faction.Player1];
            Assert.True(a.sys.TrainUnit(ba, a.resources, chosenUnitIndex: 2));
            int ua = SpawnNext(a.sys, a.world);

            // Route B — the SAME train issued as a UnitCommand.Train through the shared OrderApplier.
            var b = Harness(TestFaction());
            int bb = b.sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, FixedVec3.Zero, preBuilt: true);
            Fixed bOreBefore = b.resources.Ore[(int)Faction.Player1];
            OrderApplier.Apply(b.world, new UnitOrder(bb, UnitCommand.Train, Fixed.FromRaw(2), Fixed.Zero),
                               Faction.Player1, buildings: b.sys);
            int ub = SpawnNext(b.sys, b.world);

            // Byte-identical outcome — same unit (mesh + category + hp) and the same single ore spend.
            Assert.Equal(a.world.MeshType[ua],               b.world.MeshType[ub]);
            Assert.Equal(a.world.CategoryOf[ua],             b.world.CategoryOf[ub]);
            Assert.Equal(a.world.EffectiveMaxHealth[ua].Raw, b.world.EffectiveMaxHealth[ub].Raw);
            Assert.Equal(aOreBefore.Raw - a.resources.Ore[(int)Faction.Player1].Raw,
                         bOreBefore.Raw - b.resources.Ore[(int)Faction.Player1].Raw);
        }

        [Fact]
        public void OrderApplier_Train_SpendsOreExactlyOnce()
        {
            var (sys, _, resources, world) = Harness(TestFaction());
            int b = sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, FixedVec3.Zero, preBuilt: true);
            Fixed before = resources.Ore[(int)Faction.Player1];

            OrderApplier.Apply(world, new UnitOrder(b, UnitCommand.Train, Fixed.FromRaw(1), Fixed.Zero),
                               Faction.Player1, buildings: sys);

            // melee_a costs 50 → exactly one 50 debit (never two — no click-time + exec-tick double-spend).
            Assert.Equal(Fixed.FromInt(50).Raw, (before - resources.Ore[(int)Faction.Player1]).Raw);
        }

        [Fact]
        public void OrderApplier_Train_WrongFaction_RejectedByOwnershipGuard()
        {
            var (sys, buildings, resources, world) = Harness(TestFaction());
            int b = sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, FixedVec3.Zero, preBuilt: true);
            Fixed before = resources.Ore[(int)Faction.Player1];

            // Player2 tries to train at a Player1 building → rejected (anti-cheat), no spend, no queue.
            OrderApplier.Apply(world, new UnitOrder(b, UnitCommand.Train, Fixed.FromRaw(1), Fixed.Zero),
                               Faction.Player2, buildings: sys);

            Assert.Equal(before.Raw, resources.Ore[(int)Faction.Player1].Raw);
            Assert.Equal(0, buildings.ProductionQueue[b]);
        }

        [Fact]
        public void OrderApplier_Train_NullBuildings_IsDeterministicNoOp()
        {
            // The headless / golden / replay-without-buildings path passes no BuildingSystem → Train must no-op
            // without throwing (goldens never train via the wire).
            var world = new EntityWorld();
            var ex = Record.Exception(() =>
                OrderApplier.Apply(world, new UnitOrder(0, UnitCommand.Train, Fixed.FromRaw(1), Fixed.Zero),
                                   Faction.Player1)); // buildings defaults to null
            Assert.Null(ex);
        }

        [Fact]
        public void OrderApplier_Train_DoesNotClobberEntitySharingTheBuildingIndex()
        {
            // The Train branch runs BEFORE the entity-ownership guard, so it must never touch world.CommandState at
            // the building id — a live entity can share that slot index in a real match (separate stores).
            var (sys, _, _, world) = Harness(TestFaction());
            int b = sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, FixedVec3.Zero, preBuilt: true);
            int e = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            Assert.Equal(b, e); // both index 0
            world.CommandState[e] = UnitCommand.Move;

            OrderApplier.Apply(world, new UnitOrder(b, UnitCommand.Train, Fixed.FromRaw(1), Fixed.Zero),
                               Faction.Player1, buildings: sys);

            Assert.Equal(UnitCommand.Move, world.CommandState[e]); // untouched: Train never wrote CommandState[b]
        }

        // ── Story 2.9b (AC2) — TrainUnit multi-resource (ore + crystal) atomicity ───────────────────────
        // A unit that costs BOTH ore and crystal must be refused unless the faction can afford BOTH, and must debit
        // both exactly once on success — with NO partial spend if either is short (check-both-before-spend-either).

        /// <summary>A faction whose Melee category has a crystal-costed unit (index 1) AND a crystal-free one
        /// (index 2), so both the multi-resource spend and the cost_crystal:0 regression are exercisable.</summary>
        private static FactionDefinition CrystalFaction()
        {
            var f = new FactionDefinition { Id = "cf", DisplayName = "Crystal" };
            f.Units.Add(new UnitDefinition { Id = "worker",        Category = "Worker", Hp = 50f });                                   // 0
            f.Units.Add(new UnitDefinition { Id = "crystal_melee", Category = "Melee",  Hp = 100f, CostOre = 50, CostCrystal = 30 });  // 1
            f.Units.Add(new UnitDefinition { Id = "free_melee",    Category = "Melee",  Hp = 100f, CostOre = 50, CostCrystal = 0 });   // 2
            return f;
        }

        private static (BuildingSystem sys, BuildingStore buildings, ResourceStore resources)
            CrystalHarness(int ore, int crystal)
        {
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            resources.Ore[(int)Faction.Player1]       = Fixed.FromInt(ore);
            resources.Crystal[(int)Faction.Player1]   = Fixed.FromInt(crystal);
            resources.SupplyCap[(int)Faction.Player1] = 500;
            var sys = new BuildingSystem(buildings, resources, CrystalFaction());
            return (sys, buildings, resources);
        }

        [Fact]
        public void TrainUnit_AffordingOreAndCrystal_TrainsAndSpendsBothExactlyOnce()
        {
            var (sys, buildings, resources) = CrystalHarness(ore: 100, crystal: 100);
            int b = sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, FixedVec3.Zero, preBuilt: true);

            Assert.True(sys.TrainUnit(b, resources, chosenUnitIndex: 1)); // crystal_melee: 50 ore + 30 crystal
            Assert.NotEqual(0, buildings.ProductionQueue[b]);                                          // queued
            Assert.Equal(Fixed.FromInt(50).Raw, resources.Ore[(int)Faction.Player1].Raw);     // 100 - 50 (exactly once)
            Assert.Equal(Fixed.FromInt(70).Raw, resources.Crystal[(int)Faction.Player1].Raw); // 100 - 30 (exactly once)
        }

        [Fact]
        public void TrainUnit_SufficientOre_InsufficientCrystal_Refuses_SpendsNEITHER()
        {
            // The partial-spend regression this task's ordering prevents: ore passes, crystal fails → spend NOTHING.
            // A test that only checked "training refused" without checking "ore stayed unspent" would miss this bug.
            var (sys, buildings, resources) = CrystalHarness(ore: 100, crystal: 10); // 10 < 30 crystal
            int b = sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, FixedVec3.Zero, preBuilt: true);

            Assert.False(sys.TrainUnit(b, resources, chosenUnitIndex: 1));
            Assert.Equal(Fixed.FromInt(100).Raw, resources.Ore[(int)Faction.Player1].Raw);      // ore NOT spent
            Assert.Equal(Fixed.FromInt(10).Raw,  resources.Crystal[(int)Faction.Player1].Raw);  // crystal NOT spent
            Assert.Equal(0, buildings.ProductionQueue[b]);                                       // nothing queued
        }

        [Fact]
        public void TrainUnit_SufficientCrystal_InsufficientOre_Refuses_SpendsNeither()
        {
            var (sys, buildings, resources) = CrystalHarness(ore: 20, crystal: 100); // 20 < 50 ore
            int b = sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, FixedVec3.Zero, preBuilt: true);

            Assert.False(sys.TrainUnit(b, resources, chosenUnitIndex: 1));
            Assert.Equal(Fixed.FromInt(20).Raw,  resources.Ore[(int)Faction.Player1].Raw);      // ore NOT spent
            Assert.Equal(Fixed.FromInt(100).Raw, resources.Crystal[(int)Faction.Player1].Raw);  // crystal NOT spent
            Assert.Equal(0, buildings.ProductionQueue[b]);
        }

        [Fact]
        public void TrainUnit_ZeroCrystalUnit_UnaffectedByNewCheck_EvenWithZeroCrystalBank_Regression()
        {
            // AC2.3: a cost_crystal:0 unit trains exactly as before even with an empty crystal bank — the new check is
            // a no-op for it (CanAffordCrystal(0) is always true, SpendCrystal(0) debits nothing).
            var (sys, buildings, resources) = CrystalHarness(ore: 100, crystal: 0);
            int b = sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, FixedVec3.Zero, preBuilt: true);

            Assert.True(sys.TrainUnit(b, resources, chosenUnitIndex: 2)); // free_melee: 50 ore, 0 crystal
            Assert.NotEqual(0, buildings.ProductionQueue[b]);
            Assert.Equal(Fixed.FromInt(50).Raw, resources.Ore[(int)Faction.Player1].Raw);   // 100 - 50 ore
            Assert.Equal(Fixed.Zero.Raw,        resources.Crystal[(int)Faction.Player1].Raw); // crystal untouched
        }
    }
}
