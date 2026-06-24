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
                Assert.Equal(Fixed.FromFloat(def.Hp),   host.World.MaxHealth[id]);
                Assert.Equal(Fixed.FromFloat(def.Speed), host.World.Speed[id]);
                Assert.Equal(GatherState.Idle,          host.World.GatherState[id]);          // Category == "Worker"
                Assert.Equal(Fixed.FromFloat(20f),      host.World.CarryCapacity[id]);
                Assert.Equal((byte)WorkerMeshIndex,     host.World.MeshType[id]);             // IndexOfUnit("worker") == 1
            }

            // SoA stat detail on the first unit (the same write path for all).
            Assert.Equal(Fixed.FromFloat(def.VisionRange),  host.World.VisionRange[0]);
            Assert.Equal(Fixed.FromFloat(def.AttackRange),  host.World.AttackRange[0]);
            Assert.Equal(Fixed.FromFloat(def.AttackDamage), host.World.AttackDamage[0]);
            Assert.Equal(Fixed.FromFloat(def.AttackSpeed),  host.World.AttackSpeed[0]);
            Assert.Equal(Fixed.FromFloat(def.SplashRadius), host.World.SplashRadius[0]);
            Assert.Equal((byte)def.Supply,                  host.World.SupplyCost[0]);
            Assert.Equal(DamageType.Pierce,                 host.World.DamageTypeOf[0]);
            Assert.Equal(ArmorType.Light,                   host.World.ArmorTypeOf[0]);
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

        // Pinned canonical-model hash of BuildAlphaModel(). Recorded once from CanonicalModelHash.Compute; an
        // accidental change to the in-code model (or a hash-algorithm change) flips this and the test fails.
        private const ulong ExpectedCanonicalHash = 12401609732849360762UL;
    }
}
