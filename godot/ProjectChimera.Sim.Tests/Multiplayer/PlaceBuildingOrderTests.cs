#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;   // FactionDefinition / BuildingDefinition — so the build actually COSTS something
using ProjectChimera.Core.Sim;           // ILogSink
using ProjectChimera.Economy;            // BuildingSystem / BuildingStore / ResourceStore / BuildingType
using ProjectChimera.Multiplayer;        // OrderApplier / UnitOrder / UnitCommand / TickCommandPacket
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>
    /// DW-405 — worker-build placement is a WIRE ORDER (<see cref="UnitCommand.PlaceBuilding"/>), not a direct sim
    /// mutation.
    ///
    /// <para><b>The defect these pin.</b> <c>MainScene</c>'s placement click called
    /// <c>BuildingSystem.QueueWorkerBuild</c> directly, bypassing the lockstep seam every other order site goes
    /// through. Online, that spent ore and created a building in the CLICKING client's simulation only — and both
    /// <c>ResourceStore</c> and <c>BuildingStore</c> fold into <c>SimChecksum</c>, so the peers diverged the instant
    /// anyone built anything. It was filed as a predicted "reachable-online SimChecksum desync" on 2026-07-30 and
    /// then observed exactly as written on the 2026-08-08 two-machine LAN run: 135 consecutive clean cross-peer
    /// windows through tick 8100, then a GLOBAL DESYNC at tick 8160 the moment a building was placed on the joining
    /// client.</para>
    ///
    /// <para>The load-bearing test is <see cref="TwoPeers_FedTheSameMergedOrder_EndWithIdenticalState"/>: it is the
    /// property the wire route buys and the direct call could never provide.</para>
    /// </summary>
    public class PlaceBuildingOrderTests
    {
        private const int START_ORE    = 5000;
        private const int BARRACKS_ORE = 160;

        private sealed class RecordingSink : ILogSink
        {
            public readonly List<string> Warns = new List<string>();
            public void Info(string message) { }
            public void Warn(string message) => Warns.Add(message);
        }

        /// <summary>One peer's world: entity store + folded resource/building stores + the system under test.</summary>
        private sealed class Peer
        {
            public EntityWorld    World     = new EntityWorld();
            public BuildingStore  Buildings = new BuildingStore();
            public ResourceStore  Resources = new ResourceStore(Fixed.FromInt(START_ORE));
            public BuildingSystem System    = null!;
        }

        /// <summary>
        /// A faction whose barracks actually COSTS ore. Without a definition wired, BuildingSystem resolves an empty
        /// cost map and placement is free — which would leave the ResourceStore half of the live desync untested,
        /// since the 2026-08-08 divergence moved BOTH the building store and the resource store.
        /// </summary>
        private static FactionDefinition CostedFaction()
        {
            var f = new FactionDefinition { Id = "test", DisplayName = "Test" };
            f.Buildings.Add(new BuildingDefinition
            {
                Id = "barracks", DisplayName = "Barracks", Category = "Structure",
                Hp = 300f, ConstructionTime = 10f, SupplyBonus = 0, ProducesCategory = "Melee",
                CostOre = BARRACKS_ORE,
            });
            return f;
        }

        private static Peer NewPeer()
        {
            var p = new Peer();
            var faction = CostedFaction();
            // Both slots share the same definition so a cross-faction order is rejected by OWNERSHIP, never by an
            // accidental missing-definition difference between the two seats.
            p.System = new BuildingSystem(p.Buildings, p.Resources, faction, faction);
            return p;
        }

        /// <summary>A live worker (GatherState != Inactive is what makes an entity a worker to QueueWorkerBuild).</summary>
        private static int NewWorker(EntityWorld world, Faction faction)
        {
            int id = world.Create(new FixedVec3(Fixed.FromInt(5), Fixed.Zero, Fixed.FromInt(5)), faction,
                                  Fixed.FromInt(100), Fixed.FromInt(3));
            world.GatherState[id] = GatherState.Idle;
            return id;
        }

        private static UnitOrder PlaceOrder(int workerId, BuildingType type, int x, int z) =>
            new UnitOrder(workerId, UnitCommand.PlaceBuilding,
                          Fixed.FromInt(x), Fixed.FromInt(z), (byte)type);

        // ── the order actually places ─────────────────────────────────────────────────────────────────────

        [Fact]
        public void PlaceBuildingOrder_CreatesTheBuilding_AndSpendsFromTheOwningFaction()
        {
            var p = NewPeer();
            int worker = NewWorker(p.World, Faction.Player1);

            OrderApplier.Apply(p.World, PlaceOrder(worker, BuildingType.Barracks, 12, 8),
                               Faction.Player1, buildings: p.System);

            Assert.Equal(1, CountBuildings(p, Faction.Player1));
            Assert.Equal(Fixed.FromInt(START_ORE - BARRACKS_ORE), p.Resources.Ore[(int)Faction.Player1]);
        }

        /// <summary>
        /// PlaceBuilding is an INTENT, like CastAbility — it must never land in CommandState itself. QueueWorkerBuild
        /// writes <see cref="UnitCommand.Build"/>, which is the state every per-tick router already cases. If
        /// PlaceBuilding ever persisted it would inherit idle auto-combat, the DW-206 defect class.
        /// </summary>
        [Fact]
        public void PlaceBuildingOrder_LeavesTheWorkerInBuildState_NeverInPlaceBuilding()
        {
            var p = NewPeer();
            int worker = NewWorker(p.World, Faction.Player1);

            OrderApplier.Apply(p.World, PlaceOrder(worker, BuildingType.Barracks, 12, 8),
                               Faction.Player1, buildings: p.System);

            Assert.Equal(UnitCommand.Build, p.World.CommandState[worker]);
            Assert.False(UnitCommandTraits.PersistsAsCommandState(UnitCommand.PlaceBuilding));
        }

        // ── the anti-cheat that decides WHERE the arm sits ────────────────────────────────────────────────

        /// <summary>
        /// The reason PlaceBuilding is dispatched AFTER the entity-ownership guard rather than with the
        /// Train/Revive/BuyItem building-command family: its UnitId names an ENTITY. Dispatched before the guard, a
        /// peer could name an ENEMY worker, spending the victim's ore and planting buildings on their behalf.
        /// </summary>
        [Fact]
        public void PlaceBuildingOrder_NamingAnotherFactionsWorker_IsRejected()
        {
            var p = NewPeer();
            int enemyWorker = NewWorker(p.World, Faction.Player2);

            // Player1 tries to make Player2's worker build.
            OrderApplier.Apply(p.World, PlaceOrder(enemyWorker, BuildingType.Barracks, 12, 8),
                               Faction.Player1, buildings: p.System);

            Assert.Equal(0, CountBuildings(p, Faction.Player1));
            Assert.Equal(0, CountBuildings(p, Faction.Player2));
            Assert.Equal(Fixed.FromInt(START_ORE), p.Resources.Ore[(int)Faction.Player2]);
            Assert.Equal(Fixed.FromInt(START_ORE), p.Resources.Ore[(int)Faction.Player1]);
            Assert.NotEqual(UnitCommand.Build, p.World.CommandState[enemyWorker]);
        }

        [Fact]
        public void PlaceBuildingOrder_NamingADeadEntity_IsANoOp()
        {
            var p = NewPeer();

            OrderApplier.Apply(p.World, PlaceOrder(unchecked(1234), BuildingType.Barracks, 12, 8),
                               Faction.Player1, buildings: p.System);

            Assert.Equal(0, CountBuildings(p, Faction.Player1));
            Assert.Equal(Fixed.FromInt(START_ORE), p.Resources.Ore[(int)Faction.Player1]);
        }

        // ── THE property the whole fix exists for ─────────────────────────────────────────────────────────

        /// <summary>
        /// Two independent peers fed the SAME authoritative order end in the SAME state. This is what the direct
        /// <c>QueueWorkerBuild</c> call could not do: only the clicking peer mutated, and the divergence showed up at
        /// the next checksum window. Both folded stores are compared, since both were part of the live desync.
        /// </summary>
        [Fact]
        public void TwoPeers_FedTheSameMergedOrder_EndWithIdenticalState()
        {
            var clicker = NewPeer();
            var remote  = NewPeer();

            int wA = NewWorker(clicker.World, Faction.Player1);
            int wB = NewWorker(remote.World,  Faction.Player1);
            Assert.Equal(wA, wB); // same deterministic spawn order ⇒ same entity id on both peers

            UnitOrder order = PlaceOrder(wA, BuildingType.Barracks, 12, 8);

            // The merged stream delivers the identical order to BOTH peers — including the one that issued it.
            OrderApplier.Apply(clicker.World, order, Faction.Player1, buildings: clicker.System);
            OrderApplier.Apply(remote.World,  order, Faction.Player1, buildings: remote.System);

            Assert.Equal(CountBuildings(clicker, Faction.Player1), CountBuildings(remote, Faction.Player1));
            Assert.Equal(1, CountBuildings(remote, Faction.Player1));
            Assert.Equal(clicker.Resources.Ore[(int)Faction.Player1],
                         remote.Resources.Ore[(int)Faction.Player1]);
            Assert.Equal(clicker.World.CommandState[wA], remote.World.CommandState[wB]);
            Assert.Equal(clicker.World.BuildTarget[wA],  remote.World.BuildTarget[wB]);
        }

        // ── wire fidelity: the Slot byte now carries a SECOND meaning ─────────────────────────────────────

        /// <summary>
        /// The building type rides the spare <c>Slot</c> byte that Story 15.11 added for the CastAbility ability
        /// index. Pin the round-trip for EVERY <see cref="BuildingType"/> member so a future renumber (or a widened
        /// enum that stops fitting a byte) fails here rather than as a mis-placed building on one peer.
        /// </summary>
        [Theory]
        [InlineData(BuildingType.CommandCenter)]
        [InlineData(BuildingType.Barracks)]
        [InlineData(BuildingType.ArcheryRange)]
        [InlineData(BuildingType.SiegeWorkshop)]
        [InlineData(BuildingType.Aviary)]
        [InlineData(BuildingType.Custom)]
        public void PlaceBuildingOrder_SurvivesTheWireRoundTrip_WithTypeAndSite(BuildingType type)
        {
            var buf     = new byte[TickCommandPacket.HEADER_BYTES + TickCommandPacket.MAX_ORDERS * UnitOrder.SIZE];
            var decoded = new UnitOrder[TickCommandPacket.MAX_ORDERS];
            var sent    = new[] { PlaceOrder(77, type, -14, 31) };

            int len = TickCommandPacket.Write(buf, tick: 900, Faction.Player1, sent, 0, 1);
            Assert.True(TickCommandPacket.TryRead(buf, len, out uint tick, out Faction faction, decoded, out int count));

            Assert.Equal(900u, tick);
            Assert.Equal(Faction.Player1, faction);
            Assert.Equal(1, count);
            Assert.Equal(UnitCommand.PlaceBuilding, decoded[0].Command);
            Assert.Equal(77, decoded[0].UnitId);
            Assert.Equal((byte)type, decoded[0].Slot);
            Assert.Equal(Fixed.FromInt(-14).Raw, decoded[0].TargetX);
            Assert.Equal(Fixed.FromInt(31).Raw,  decoded[0].TargetZ);
        }

        // ── systemless drop (DW-304 contract) ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Deliberately NOT folded into <c>OrderApplierFailLoudTests.SystemlessBuildingCommands</c>: every command in
        /// that set is dispatched BEFORE the entity guard, so its cases fire on an empty world. PlaceBuilding is
        /// dispatched AFTER the guard, so reaching its systemless drop at all requires a live, owned worker — the
        /// shared theory would have passed vacuously by returning at the guard.
        /// </summary>
        [Fact]
        public void PlaceBuildingOrder_WithNoBuildingSystem_WarnsOnce_AndStaysADeterministicNoOp()
        {
            var world = new EntityWorld();
            int worker = NewWorker(world, Faction.Player1);
            var sink   = new RecordingSink();

            OrderApplier.Apply(world, PlaceOrder(worker, BuildingType.Barracks, 12, 8),
                               Faction.Player1, buildings: null, log: sink);

            Assert.Single(sink.Warns);
            Assert.Contains(nameof(UnitCommand.PlaceBuilding), sink.Warns[0]);
            Assert.Contains(nameof(BuildingSystem), sink.Warns[0]);
            Assert.NotEqual(UnitCommand.Build, world.CommandState[worker]);
        }

        [Fact]
        public void PlaceBuildingOrder_WithNoBuildingSystem_AndNoSink_DoesNotThrow()
        {
            var world = new EntityWorld();
            int worker = NewWorker(world, Faction.Player1);

            OrderApplier.Apply(world, PlaceOrder(worker, BuildingType.Barracks, 12, 8), Faction.Player1);

            Assert.NotEqual(UnitCommand.Build, world.CommandState[worker]);
        }

        private static int CountBuildings(Peer p, Faction faction)
        {
            int n = 0;
            for (int i = 0; i < BuildingStore.MAX_BUILDINGS; i++)
                if (p.Buildings.Alive[i] && p.Buildings.FactionOf[i] == faction) n++;
            return n;
        }
    }
}
