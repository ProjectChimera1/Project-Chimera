#nullable enable
using System;
using ProjectChimera.Combat;            // DamageType, ArmorType
using ProjectChimera.Core;              // Faction, Fixed, FixedVec3, FactionRegistry, BuildingType, GatherState
using ProjectChimera.Core.Definitions;  // ScenarioData & co, FactionDefinition, UnitDefinition, ScenarioValidator, Validated, CanonicalModelHash
using ProjectChimera.Core.Sim;          // SimulationHost, ScenarioApplier, NullLogSink
using Xunit;

namespace ProjectChimera.Sim.Tests.Builder
{
    /// <summary>
    /// Story 1.8b (AC1/AC3/AC5) — the forward proof that the net-new Godot-free <see cref="ScenarioApplier"/> writes
    /// IDENTICAL store contents from a known <see cref="Validated{ScenarioData}"/> (the byte-identical goldens are the
    /// regression net; this is the store-contents net). Mirrors <c>alpha_map_01.json</c> as an in-code model so it
    /// never touches Godot or the filesystem. Also pins a stable canonical start-state hash and asserts
    /// <see cref="ScenarioApplier.SpawnUnit"/> is allocation-free (AC3).
    ///
    /// That this whole file (and the applier it exercises) compiles + runs inside the Godot-free Tier-1 project IS
    /// the AC1 "Godot-free compile" proof, alongside <c>GodotFreeBoundaryTest</c>.
    /// </summary>
    public class ScenarioApplierTests
    {
        // ── Worker definition under test. A distinct "scout" sits at index 0 so IndexOfUnit("worker") == 1 — proving
        //    the applier computes MeshType FROM the faction def, not by defaulting to 0. Stats are deliberately
        //    non-default so every SoA write is a meaningful assertion. ──
        private const int WorkerMeshIndex = 1;

        private static UnitDefinition WorkerDef() => new UnitDefinition
        {
            Id = "worker", DisplayName = "Worker", Category = "Worker",
            Hp = 50f, Speed = 3.5f, VisionRange = 7f, AttackRange = 2f, AttackDamage = 4f,
            AttackSpeed = 1.5f, SplashRadius = 0.5f, Supply = 1,
            DamageType = "Pierce", ArmorType = "Light",
        };

        private static FactionDefinition AlphaFaction() => new FactionDefinition
        {
            Id = "alpha", DisplayName = "Alpha",
            Units =
            {
                new UnitDefinition { Id = "scout", Category = "Ranged" }, // index 0 — pushes worker to index 1
                WorkerDef(),                                              // index 1
            },
        };

        /// <summary>The per-slot faction-def array MainScene would hand the applier, seeded for P1 + P2 (length 5,
        /// matching the as-built [5] array indexed by (int)Faction).</summary>
        private static FactionDefinition?[] SlotDefs(FactionDefinition faction)
        {
            var defs = new FactionDefinition?[5];
            defs[(int)Faction.Player1] = faction;
            defs[(int)Faction.Player2] = faction;
            return defs;
        }

        // ── alpha_map_01 mirror (clean base coords) ──
        private static readonly (float X, float Z, float Supply, float Rate, int Max)[] ExpectedNodes =
        {
            ( -20f, -15f, 600f, 5f, 4 ), ( -20f,  15f, 600f, 5f, 4 ),
            (  20f, -15f, 600f, 5f, 4 ), (  20f,  15f, 600f, 5f, 4 ),
            (   0f, -25f, 400f, 5f, 4 ), (   0f,  25f, 400f, 5f, 4 ),
            ( -35f,   0f, 300f, 5f, 4 ), (  35f,   0f, 300f, 5f, 4 ),
        };

        // unit_id, slot, x, z — order matters: this IS the EntityWorld id order (0..3).
        private static readonly (int Slot, float X, float Z)[] ExpectedUnits =
        {
            ( 0, -42f, -3f ), ( 0, -42f, 3f ), ( 1, 42f, -3f ), ( 1, 42f, 3f ),
        };

        private static ScenarioData BuildAlphaModel()
        {
            var nodes = new ScenarioResourceNode[ExpectedNodes.Length];
            for (int i = 0; i < ExpectedNodes.Length; i++)
            {
                var n = ExpectedNodes[i];
                nodes[i] = new ScenarioResourceNode { X = n.X, Z = n.Z, Supply = n.Supply, Rate = n.Rate, MaxGatherers = n.Max };
            }
            var units = new ScenarioUnit[ExpectedUnits.Length];
            for (int i = 0; i < ExpectedUnits.Length; i++)
            {
                var u = ExpectedUnits[i];
                units[i] = new ScenarioUnit { UnitId = "worker", Slot = u.Slot, X = u.X, Z = u.Z };
            }
            return new ScenarioData
            {
                Id = "alpha_map_01", DisplayName = "Alpha Skirmish", TerrainRef = "",
                MapBounds = 120f, WinCondition = WinCondition.DestroyAllBuildings,
                PlayerSlots = new[]
                {
                    new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://resources/data/factions/alpha_faction.json", StartOre = 200f, BaseX = -45f, BaseZ = 0f },
                    new ScenarioPlayerSlot { Slot = 1, FactionJson = "res://resources/data/factions/alpha_faction.json", StartOre = 200f, BaseX =  45f, BaseZ = 0f },
                },
                ResourceNodes = nodes,
                Buildings = new[]
                {
                    new ScenarioBuilding { Type = "CommandCenter", Slot = 0, X = -45f, Z = 0f, PreBuilt = true },
                    new ScenarioBuilding { Type = "CommandCenter", Slot = 1, X =  45f, Z = 0f, PreBuilt = true },
                },
                Units = units,
            };
        }

        /// <summary>Build a fresh host + applier sharing a slotDefs array carrying <see cref="AlphaFaction"/>.</summary>
        private static (SimulationHost host, ScenarioApplier applier) NewHostAndApplier()
        {
            var faction = AlphaFaction();
            var slotDefs = SlotDefs(faction);
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);
            return (host, applier);
        }

        [Fact]
        public void Apply_WritesIdenticalStoreContents_FromAValidatedModel()
        {
            ScenarioData model = BuildAlphaModel();
            var (host, applier) = NewHostAndApplier();

            // A raw model cannot reach a store — it must pass the 1.7 gate first (AC4).
            ValidationResult r = new ScenarioValidator().Validate(model);
            Assert.True(r.Ok, r.Error);

            applier.Apply(r.Value);

            // ── Resources: ore per faction (ResourceStore starts at Zero on the host; Apply AddOre's 200) ──
            Assert.Equal(Fixed.FromFloat(200f), host.Resources.Ore[(int)Faction.Player1]);
            Assert.Equal(Fixed.FromFloat(200f), host.Resources.Ore[(int)Faction.Player2]);

            // ── Faction bases ──
            Assert.Equal(new FixedVec3(Fixed.FromFloat(-45f), Fixed.Zero, Fixed.Zero), host.Resources.FactionBase[(int)Faction.Player1]);
            Assert.Equal(new FixedVec3(Fixed.FromFloat( 45f), Fixed.Zero, Fixed.Zero), host.Resources.FactionBase[(int)Faction.Player2]);

            // ── Resource nodes ──
            Assert.Equal(ExpectedNodes.Length, host.Nodes.Count);
            for (int i = 0; i < ExpectedNodes.Length; i++)
            {
                var n = ExpectedNodes[i];
                Assert.True(host.Nodes.Active[i]);
                Assert.Equal(new FixedVec3(Fixed.FromFloat(n.X), Fixed.Zero, Fixed.FromFloat(n.Z)), host.Nodes.Position[i]);
                Assert.Equal(Fixed.FromFloat(n.Supply), host.Nodes.SupplyRemaining[i]);
                Assert.Equal(Fixed.FromFloat(n.Supply), host.Nodes.SupplyTotal[i]);
                Assert.Equal(Fixed.FromFloat(n.Rate),   host.Nodes.GatherRate[i]);
                Assert.Equal(n.Max,                     host.Nodes.MaxGatherers[i]);
            }

            // ── Buildings: 2 pre-built CommandCenters (pre-built ⇒ ConstructionTimer == 0) ──
            Assert.Equal(2, host.Buildings.Count);
            Assert.Equal(BuildingType.CommandCenter, host.Buildings.Type[0]);
            Assert.Equal(Faction.Player1,            host.Buildings.FactionOf[0]);
            Assert.Equal(new FixedVec3(Fixed.FromFloat(-45f), Fixed.Zero, Fixed.Zero), host.Buildings.Position[0]);
            Assert.Equal(Fixed.Zero, host.Buildings.ConstructionTimer[0]);
            Assert.Equal(BuildingType.CommandCenter, host.Buildings.Type[1]);
            Assert.Equal(Faction.Player2,            host.Buildings.FactionOf[1]);
            Assert.Equal(new FixedVec3(Fixed.FromFloat( 45f), Fixed.Zero, Fixed.Zero), host.Buildings.Position[1]);
            Assert.Equal(Fixed.Zero, host.Buildings.ConstructionTimer[1]);

            // ── Units: 4 workers in Units order → EntityWorld ids 0..3 ──
            Assert.Equal(ExpectedUnits.Length, host.World.AliveCount);
            UnitDefinition def = WorkerDef();
            for (int id = 0; id < ExpectedUnits.Length; id++)
            {
                var u = ExpectedUnits[id];
                var faction = (Faction)(u.Slot + 1);
                Assert.True(host.World.IsAlive(id));
                Assert.Equal(new FixedVec3(Fixed.FromFloat(u.X), Fixed.Zero, Fixed.FromFloat(u.Z)), host.World.Position[id]);
                Assert.Equal(faction,                   host.World.FactionOf[id]);
                Assert.Equal(Fixed.FromFloat(def.Hp),   host.World.Health[id]);
                Assert.Equal(Fixed.FromFloat(def.Hp),   host.World.EffectiveMaxHealth[id]);
                Assert.Equal(Fixed.FromFloat(def.Speed), host.World.EffectiveMoveSpeed[id]);
                Assert.Equal(GatherState.Idle,          host.World.GatherState[id]);          // Category == "Worker"
                Assert.Equal(Fixed.FromFloat(20f),      host.World.CarryCapacity[id]);
                Assert.Equal((byte)WorkerMeshIndex,     host.World.MeshType[id]);             // IndexOfUnit("worker") == 1
            }

            // SoA stat detail on the first unit (the same write path for all).
            Assert.Equal(Fixed.FromFloat(def.VisionRange),  host.World.VisionRange[0]);
            Assert.Equal(Fixed.FromFloat(def.AttackRange),  host.World.AttackRange[0]);
            Assert.Equal(Fixed.FromFloat(def.AttackDamage), host.World.EffectiveAttackDamage[0]);
            Assert.Equal(Fixed.FromFloat(def.AttackSpeed),  host.World.AttackSpeed[0]);
            Assert.Equal(Fixed.FromFloat(def.SplashRadius), host.World.SplashRadius[0]);
            Assert.Equal((byte)def.Supply,                  host.World.SupplyCost[0]);
            Assert.Equal(DamageType.Pierce,                 host.World.DamageTypeOf[0]);
            Assert.Equal(ArmorType.Light,                   host.World.ArmorTypeOf[0]);
        }

        [Fact]
        public void Apply_ThreadsStory6_3_HeightVisionConfig_AndSamplesElevationAtSpawn()
        {
            // Story 6.3 (review pass 1, VG1): the entire height-advantage feature ACTIVATES through Apply — it threads
            // ScenarioData.HeightAdvantageVision / HeightVisionBonusPerStep onto the world and pushes the injected
            // ElevationGrid so EntityWorld.Create samples it at spawn. Every OTHER 6.3 test configures a raw world by
            // hand and bypasses Apply, so a regression that dropped this threading (or inverted the toggle) would ship
            // a silently-dead feature with a green suite. This is the caller-path guard.
            ScenarioData model = BuildAlphaModel();
            model.HeightAdvantageVision    = true;
            model.HeightVisionBonusPerStep = 4f;

            var (host, applier) = NewHostAndApplier();

            // A uniform non-zero grid over a wide extent ⇒ every spawned unit samples elevation 5 regardless of its XZ.
            var heights = new[] { Fixed.FromInt(5), Fixed.FromInt(5), Fixed.FromInt(5), Fixed.FromInt(5) };
            applier.SetElevationGrid(new ElevationGrid(heights, 2, 2,
                Fixed.FromInt(-1000), Fixed.FromInt(-1000), Fixed.FromInt(1000)));

            ValidationResult r = new ScenarioValidator().Validate(model);
            Assert.True(r.Ok, r.Error);
            applier.Apply(r.Value);

            // Toggle + bonus reached the world (the single float→Fixed boundary lives in Apply).
            Assert.True(host.World.HeightAdvantageVision);
            Assert.Equal(Fixed.FromFloat(4f), host.World.HeightVisionBonusPerStep);

            // The injected grid reached Create: every spawned unit sampled elevation 5, and the effective vision =
            // base + floor(5/step)·bonus = base + 5·4.
            Assert.True(host.World.AliveCount > 0);
            for (int id = 0; id < host.World.AliveCount; id++)
                Assert.Equal(Fixed.FromInt(5), host.World.Elevation[id]);
            Assert.Equal(host.World.VisionRange[0] + Fixed.FromInt(5) * Fixed.FromFloat(4f),
                         host.World.EffectiveVisionRange(0));
        }

        [Fact]
        public void Apply_ThreadsStory4_7Fields_IntoResourceNodeStore()
        {
            // Story 4.7 — the single float→Fixed/string→enum load boundary for resource nodes: collection_model,
            // resource_type, requires_structure(+radius), owner_slot(→Faction), and income_period_ticks must all
            // reach ResourceNodeStore.Create unchanged.
            ScenarioData model = BuildAlphaModel();
            model.ResourceNodes = new[]
            {
                new ScenarioResourceNode
                {
                    X = 5f, Z = -5f, Supply = 300f, Rate = 12f, MaxGatherers = 2,
                    CollectionModel = "Income", ResourceType = "Crystal",
                    RequiresStructure = "watchtower", RequiresStructureRadius = 8f,
                    OwnerSlot = 1, IncomePeriodTicks = 45,
                },
            };
            var (host, applier) = NewHostAndApplier();

            ValidationResult r = new ScenarioValidator().Validate(model);
            Assert.True(r.Ok, r.Error);
            applier.Apply(r.Value);

            Assert.Equal(1, host.Nodes.Count);
            Assert.Equal(ResourceCollectionModel.Income, host.Nodes.CollectionModel[0]);
            Assert.Equal(ResourceKind.Crystal,            host.Nodes.ResourceType[0]);
            Assert.Equal("watchtower",                    host.Nodes.RequiresStructureId[0]);
            Assert.Equal(Fixed.FromFloat(8f),              host.Nodes.RequiresStructureRadius[0]);
            Assert.Equal(Faction.Player2,                  host.Nodes.OwnerFaction[0]); // slot 1 -> Player2
            Assert.Equal(45,                               host.Nodes.IncomePeriodTicks[0]);
            Assert.Equal(0,                                host.Nodes.IncomeTicksElapsed[0]); // fresh mutable counter
        }

        [Fact]
        public void Apply_DefaultResourceNode_MaterializesGatherOreDefaults_IntoResourceNodeStore()
        {
            // The omitted-field default path: a node that never sets the 6 new fields must land on the exact
            // GATHER-preserving defaults (Create's own defaults), never null/zero-by-accident.
            ScenarioData model = BuildAlphaModel(); // ExpectedNodes never set the new fields
            var (host, applier) = NewHostAndApplier();

            ValidationResult r = new ScenarioValidator().Validate(model);
            Assert.True(r.Ok, r.Error);
            applier.Apply(r.Value);

            for (int i = 0; i < ExpectedNodes.Length; i++)
            {
                Assert.Equal(ResourceCollectionModel.Gather, host.Nodes.CollectionModel[i]);
                Assert.Equal(ResourceKind.Ore,                host.Nodes.ResourceType[i]);
                Assert.Equal("",                              host.Nodes.RequiresStructureId[i]);
                Assert.Equal(Faction.Neutral,                 host.Nodes.OwnerFaction[i]);
            }
        }

        [Fact]
        public void Apply_RecordsScenarioPlacedHeroes_NotNonHeroes_AndClearsOnReapply()
        {
            // Story 3.9: LastAppliedHeroes is the load-bearing bridge feeding HeroProfileLoader.LoadInto — it must
            // record EXACTLY the scenario-placed IsHero units (entity id + unit id) and be replaced (not appended) on
            // re-Apply. A faction with a hero unit beside the scout/worker; ScenarioValidator does not deep-validate the
            // placed unit's hero block (only position/slot), so a minimal IsHero+Hero pairing is enough.
            var faction = new FactionDefinition
            {
                Id = "alpha", DisplayName = "Alpha",
                Units =
                {
                    new UnitDefinition { Id = "scout", Category = "Ranged" },
                    WorkerDef(),
                    new UnitDefinition
                    {
                        Id = "warchief", DisplayName = "War Chief", Category = "Ranged",
                        Hp = 200f, Speed = 3f, IsHero = true,
                        Hero = new HeroDefinition { MaxLevel = 5, BaseXp = 100f, XpGrowth = 1.5f, XpPerKill = 10f },
                    },
                },
            };
            var slotDefs = SlotDefs(faction);
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);

            ScenarioData model = BuildAlphaModel();
            // One non-hero worker (entity 0) then the hero (entity 1), both in slot 0.
            model.Units = new[]
            {
                new ScenarioUnit { UnitId = "worker",   Slot = 0, X = -40f, Z = 0f },
                new ScenarioUnit { UnitId = "warchief", Slot = 0, X = -38f, Z = 0f },
            };

            ValidationResult r = new ScenarioValidator().Validate(model);
            Assert.True(r.Ok, r.Error);
            applier.Apply(r.Value);

            // Exactly the hero is recorded (the worker is not), with its entity id (spawned 2nd → id 1) + unit id.
            Assert.Single(applier.LastAppliedHeroes);
            Assert.Equal("warchief", applier.LastAppliedHeroes[0].UnitId);
            Assert.Equal(1,          applier.LastAppliedHeroes[0].EntityId);

            // Re-Apply replaces the record (Clear at the top of Apply), it does not append.
            applier.Apply(r.Value);
            Assert.Single(applier.LastAppliedHeroes);
            Assert.Equal("warchief", applier.LastAppliedHeroes[0].UnitId);
        }

        [Fact]
        public void Apply_CapturesHeroCurveAndGrowthFields_QuantizedOntoPlacedHero()
        {
            // Story 3.13: the applier is the single production float→Fixed boundary for a deployed hero's curve/growth
            // params (MaxLevel/BaseXp/XpGrowth/XpShareRadius/*PerLevel). LoadInto feeds these straight into
            // HeroStore.Mint and HeroXpSystem reads them, so a dropped or transposed field silently ships heroes that
            // level/grow wrong. Distinct non-default values per field make a swap detectable; each must arrive as
            // Fixed.FromFloat(authored).
            var faction = new FactionDefinition
            {
                Id = "alpha", DisplayName = "Alpha",
                Units =
                {
                    new UnitDefinition { Id = "scout", Category = "Ranged" },
                    WorkerDef(),
                    new UnitDefinition
                    {
                        Id = "warchief", DisplayName = "War Chief", Category = "Ranged",
                        Hp = 200f, Speed = 3f, IsHero = true,
                        Hero = new HeroDefinition
                        {
                            MaxLevel = 7, BaseXp = 111f, XpGrowth = 1.25f, XpShareRadius = 13f,
                            HealthPerLevel = 17f, DamagePerLevel = 3f, ArmorPerLevel = 2f, XpPerKill = 10f,
                        },
                    },
                },
            };
            var slotDefs = SlotDefs(faction);
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);

            ScenarioData model = BuildAlphaModel();
            model.Units = new[] { new ScenarioUnit { UnitId = "warchief", Slot = 0, X = -38f, Z = 0f } };

            ValidationResult r = new ScenarioValidator().Validate(model);
            Assert.True(r.Ok, r.Error);
            applier.Apply(r.Value);

            var placed = Assert.Single(applier.LastAppliedHeroes);
            Assert.Equal(7,                       placed.MaxLevel);
            Assert.Equal(Fixed.FromFloat(111f).Raw,  placed.BaseXp.Raw);
            Assert.Equal(Fixed.FromFloat(1.25f).Raw, placed.XpGrowth.Raw);
            Assert.Equal(Fixed.FromFloat(13f).Raw,   placed.XpShareRadius.Raw);
            Assert.Equal(Fixed.FromFloat(17f).Raw,   placed.HealthPerLevel.Raw);
            Assert.Equal(Fixed.FromFloat(3f).Raw,    placed.DamagePerLevel.Raw);
            Assert.Equal(Fixed.FromFloat(2f).Raw,    placed.ArmorPerLevel.Raw);
        }

        [Fact]
        public void Apply_ModelProducesStableNonZeroCanonicalHash()
        {
            ScenarioData model = BuildAlphaModel();
            ulong h1 = CanonicalModelHash.Compute(model);
            ulong h2 = CanonicalModelHash.Compute(BuildAlphaModel()); // freshly rebuilt identical model

            Assert.NotEqual(0UL, h1);
            Assert.Equal(h1, h2); // deterministic: identical models hash identically (stable start-state baseline)

            // Pinned baseline — fails if the in-code alpha model layout drifts (recorded once from the live hash).
            Assert.Equal(ExpectedCanonicalHash, h1);

            // Sensitivity: a real gameplay change moves the hash.
            ScenarioData changed = BuildAlphaModel();
            changed.PlayerSlots[0].StartOre += 1f;
            Assert.NotEqual(h1, CanonicalModelHash.Compute(changed));
        }

        [Fact]
        public void Apply_SeedsStartingCrystal_PerSlot_FromStartCrystalField()
        {
            // Starting crystal is now data-driven per slot (so worker abilities like matter_infusion are affordable at
            // match start). Asymmetric values prove the value is mapped per-slot, not a shared constant.
            ScenarioData model = BuildAlphaModel();
            model.PlayerSlots[0].StartCrystal = 100f;
            model.PlayerSlots[1].StartCrystal =  50f;
            var (host, applier) = NewHostAndApplier();

            ValidationResult r = new ScenarioValidator().Validate(model);
            Assert.True(r.Ok, r.Error);
            applier.Apply(r.Value);

            Assert.Equal(Fixed.FromFloat(100f), host.Resources.Crystal[(int)Faction.Player1]);
            Assert.Equal(Fixed.FromFloat( 50f), host.Resources.Crystal[(int)Faction.Player2]);
            // Ore is unaffected by the crystal seed (independent resource pool).
            Assert.Equal(Fixed.FromFloat(200f), host.Resources.Ore[(int)Faction.Player1]);
        }

        [Fact]
        public void Apply_DefaultStartCrystalIsZero_AddsNoCrystal_GoldenSafetyInvariant()
        {
            // The applier golden's in-code model omits StartCrystal (defaults to 0). This pins that a 0 seed is a
            // no-op AddCrystal, so that golden's SimChecksum cannot move. If this ever fails, the golden — and any
            // other crystal-free pinned baseline — must be deliberately re-recorded.
            ScenarioData model = BuildAlphaModel(); // StartCrystal unset → 0f on both slots
            var (host, applier) = NewHostAndApplier();

            ValidationResult r = new ScenarioValidator().Validate(model);
            Assert.True(r.Ok, r.Error);
            applier.Apply(r.Value);

            Assert.Equal(Fixed.Zero, host.Resources.Crystal[(int)Faction.Player1]);
            Assert.Equal(Fixed.Zero, host.Resources.Crystal[(int)Faction.Player2]);
        }

        [Fact]
        public void Apply_PlacesScenarioItem_AndConfiguresUsableSlotCap()
        {
            // Story 3.15 (P6): the production item-placement path (registry lookup → Items.Create → ConfigureUsableSlots)
            // is otherwise reached only via direct ItemStore.Create in unit tests. Drive it through ScenarioApplier.Apply:
            // a model carrying one ScenarioItem (a valid registered item_id) + InventorySlotCount = 3 must place the item
            // with the registry's DefId/Charges at the authored position AND cap ItemSystem.UsableSlots at 3.
            var faction = AlphaFaction();
            var slotDefs = SlotDefs(faction);
            var itemRegistry = new ItemRegistry(new[]
            {
                new ItemDefinition { Id = "ring_of_vigor", Charges = 5, MaxHealthDelta = Fixed.FromInt(50) },
            });
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction,
                                             itemRegistry: itemRegistry);
            var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);

            ScenarioData model = BuildAlphaModel();
            model.InventorySlotCount = 3;
            model.Items = new[] { new ScenarioItem { ItemId = "ring_of_vigor", X = 12f, Z = -7f } };

            ValidationResult r = new ScenarioValidator().Validate(model);
            Assert.True(r.Ok, r.Error);
            applier.Apply(r.Value);

            // Exactly one item placed on the ground, carrying the registry's DefId + Charges at the authored position.
            Assert.Equal(1, host.Items.Count);
            int expectedDefId = itemRegistry.IndexOf("ring_of_vigor");
            Assert.True(host.Items.Alive[0]);
            Assert.False(host.Items.Held[0]);
            Assert.Equal(expectedDefId, host.Items.DefId[0]);
            Assert.Equal(5, host.Items.Charges[0]);
            Assert.Equal(Fixed.FromFloat(12f), host.Items.PosX[0]);
            Assert.Equal(Fixed.FromFloat(-7f), host.Items.PosZ[0]);

            // The usable-slot cap was configured from inventory_slot_count.
            Assert.Equal(3, host.ItemSys.UsableSlots);
        }

        [Fact]
        public void SpawnUnit_AllocatesZeroBytes_AfterWarmup()
        {
            var (_, applier) = NewHostAndApplier();
            UnitDefinition def = WorkerDef();

            // Warm up so the method (and IndexOfUnit / string.Equals) is fully JITed before measuring.
            for (int i = 0; i < 4; i++) applier.SpawnUnit(def, Faction.Player1, i, i);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 256; i++)
                applier.SpawnUnit(def, Faction.Player1, i, i);
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.True(after - before == 0L,
                $"ScenarioApplier.SpawnUnit allocated {after - before} bytes across 256 calls — the spawn path must be allocation-free (AC3).");
        }

        // Pinned canonical-model hash of BuildAlphaModel(). Recorded from CanonicalModelHash.Compute; an accidental
        // change to the in-code model (or a hash-algorithm change) flips this and the test fails. Re-recorded
        // 2026-07-08 for AlgoVersion 4 (Story 4.4: Supply's resolved values folded into the canonical hash — the
        // model omits `supply`, but the AlgoVersion bump alone moves every hash, same as the prior StartCrystal
        // re-record). Re-recorded again same day (review-pass-2 patch): Supply's HardCeiling presence is now folded
        // as an explicit bit before its value instead of a `?? -1` sentinel (fixes a hash collision between an
        // omitted ceiling and an authored, shadow-mode-reachable invalid `-1`), which shifted every hash again.
        // [Story 4.4] Re-recorded again 2026-07-09 for AlgoVersion 5 (Story 4.7: ScenarioResourceNode's 6 new
        // fields folded — the model's nodes omit them, but the AlgoVersion bump alone moves every hash, same as
        // every prior bump above).
        // Re-recorded again 2026-07-14 for AlgoVersion 6 (Story 6.5: the authored pathability layer + slope config
        // folded — the alpha model paints nothing and leaves slope-auto-block off, so the new fields fold as
        // 0/0/0, but the AlgoVersion 5→6 bump alone moves every hash, same as every prior bump above).
        // Re-recorded again 2026-07-14 for AlgoVersion 7 (Story 6.6: the blocking-prop + water footprint digest
        // folded — the alpha model has no props/water, so the new field folds as 0, but the AlgoVersion 6→7 bump
        // alone moves every hash, same as every prior bump above).
        // Re-recorded again 2026-07-17 for AlgoVersion 8 (Story 7.7: the trigger/DSL model folded — the alpha
        // model's triggers now fold too, and the AlgoVersion 7→8 bump alone moves every hash; part of the ONE
        // named 7.7 re-baseline).
        // Re-recorded again 2026-07-17 for AlgoVersion 9 (Story 7.8: the custom-UI widget tree folded — the alpha
        // model has no custom_ui, so it folds as a single 0 marker, but the AlgoVersion 8→9 bump alone moves every
        // hash, same as every prior bump above; part of the ONE named 7.8 re-baseline).
        private const ulong ExpectedCanonicalHash = 14797314539278078280UL; // recomputed at CanonicalModelHash v13 (Story 7.13 new-node-kind graph-walk fold)
    }
}
