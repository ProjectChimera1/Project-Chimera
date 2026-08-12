#nullable enable
using ProjectChimera.Combat;            // CombatSystem, ProjectileStore, CombatEventQueue, DenialReason
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;  // FactionDefinition, BuildingDefinition
using ProjectChimera.Economy;
using ProjectChimera.Multiplayer;       // UnitOrder, OrderApplier
using Xunit;

namespace ProjectChimera.Sim.Tests.Economy
{
    /// <summary>
    /// DW-938 (builders PHASE INTO their construction site — the WC3 orc-peon model) + DW-939 (placement overlap
    /// rejection). 2026-08-12 field reports: a builder pulled off its site could never resume it (DW-937's pause
    /// had no resume path — phasing makes pulling-off impossible instead), construction needed a cancel + refund,
    /// and buildings could be stacked on top of each other. Pins: arrival sets <see cref="EntityFlags.Phased"/> and
    /// teleports the builder into the site; a phased unit is untargetable (hash + global + retained paths) and
    /// un-orderable (OrderApplier drops wire orders naming it); completion pops it back out beside the site into
    /// the gather loop; CancelConstruction refunds the EXACT debited cost, releases the builder, destroys the site,
    /// and no-ops for foreign/completed targets; overlap placement is refused atomically (nothing spent) while an
    /// adjacent clear site is accepted. Godot-free, <see cref="Fixed"/>-only.
    /// </summary>
    public class PhasedBuilderAndCancelTests
    {
        private static readonly Fixed Dt = SimulationLoop.FixedDt;
        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        private static FactionDefinition BuilderFaction()
        {
            var f = new FactionDefinition { Id = "dw938", DisplayName = "DW938" };
            f.Buildings.Add(new BuildingDefinition
            {
                Id = "barracks", Category = "Structure", CostOre = 100,
                ConstructionTime = 10f, SupplyBonus = 0, ProducesCategory = "Melee", Hp = 100f,
                NavFootprint = new float[] { 5f, 3f, 5f },
            });
            return f;
        }

        private static (EntityWorld world, BuildingStore buildings, ResourceStore resources, BuildingSystem sys)
            NewHarness()
        {
            var world     = new EntityWorld();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            resources.Ore[(int)Faction.Player1] = Fixed.FromInt(500);
            var sys = new BuildingSystem(buildings, resources, BuilderFaction());
            return (world, buildings, resources, sys);
        }

        private static int SpawnWorker(EntityWorld world, FixedVec3 pos)
        {
            int id = world.Create(pos, Faction.Player1, Fixed.FromInt(50), Fixed.FromInt(3));
            world.GatherState[id] = GatherState.Idle;
            return id;
        }

        /// <summary>Queue a build at the worker's own feet and tick once so arrival phases it in.</summary>
        private static int QueueAndPhase(BuildingSystem sys, EntityWorld world, ResourceStore res, int worker)
        {
            int b = sys.QueueWorkerBuild(worker, BuildingType.Barracks, world.Position[worker],
                                         Faction.Player1, res, world);
            Assert.True(b >= 0, "fixture assumption: the build order was accepted");
            sys.Tick(world, Dt);
            Assert.True((world.Flags[worker] & EntityFlags.Phased) != 0, "fixture assumption: the builder phased in");
            return b;
        }

        // ── DW-938: the phase-in lifecycle ──────────────────────────────────────

        [Fact]
        public void Arrival_PhasesTheBuilderIntoTheSite()
        {
            var (world, buildings, resources, sys) = NewHarness();
            int worker = SpawnWorker(world, V(10, 10));
            int b = QueueAndPhase(sys, world, resources, worker);

            Assert.Equal(buildings.Position[b], world.Position[worker]); // teleported to the site centre
            Assert.Equal(UnitCommand.Build, world.CommandState[worker]); // still owns the Build command
            Assert.Equal((EntityFlags)0, world.Flags[worker] & EntityFlags.Moving);
        }

        [Fact]
        public void PhasedBuilder_IsUntargetable_ByAcquisitionAndRetainedPaths()
        {
            var (world, buildings, resources, sys) = NewHarness();
            int worker = SpawnWorker(world, V(10, 10));
            QueueAndPhase(sys, world, resources, worker);

            // An adjacent enemy combatant: hash acquisition, the global chase, and a RETAINED target must all
            // refuse the phased builder.
            var combat = new CombatSystem(new ProjectileStore());
            int enemy = world.Create(V(11, 10), Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3));
            world.EffectiveAttackDamage[enemy] = Fixed.FromInt(20);
            world.AttackRange[enemy]  = Fixed.FromInt(10);
            world.AttackSpeed[enemy]  = Fixed.Zero;
            world.Delivery[enemy]     = AttackDelivery.Hitscan;
            world.DamageTypeOf[enemy] = DamageType.Normal;
            world.CommandState[enemy] = UnitCommand.Idle;
            world.AttackTarget[enemy] = worker; // pre-seeded retained target — must be dropped, not fired on

            Fixed hp0 = world.Health[worker];
            for (int t = 0; t < 5; t++) combat.Tick(world, Dt);

            Assert.Equal(hp0.Raw, world.Health[worker].Raw); // never hit
            Assert.Equal(-1, world.AttackTarget[enemy]);     // never re-acquired either
        }

        [Fact]
        public void PhasedBuilder_DropsWireOrders()
        {
            var (world, buildings, resources, sys) = NewHarness();
            int worker = SpawnWorker(world, V(10, 10));
            QueueAndPhase(sys, world, resources, worker);

            // A Move order naming the phased builder (stale same-tick click, or crafted) must be dropped whole —
            // applying it would flip CommandState off Build and leave an invisible walking unit.
            var order = new UnitOrder(worker, UnitCommand.Move, Fixed.FromInt(0), Fixed.FromInt(0));
            OrderApplier.Apply(world, in order, Faction.Player1);

            Assert.Equal(UnitCommand.Build, world.CommandState[worker]);
            Assert.True((world.Flags[worker] & EntityFlags.Phased) != 0);
        }

        [Fact]
        public void Completion_PopsTheBuilderOut_BesideTheSite()
        {
            var (world, buildings, resources, sys) = NewHarness();
            int worker = SpawnWorker(world, V(10, 10));
            int b = QueueAndPhase(sys, world, resources, worker);

            buildings.ConstructionTimer[b] = Dt; // fast-forward to the final tick
            sys.Tick(world, Dt);

            Assert.False(buildings.IsUnderConstruction(b));
            Assert.Equal((EntityFlags)0, world.Flags[worker] & EntityFlags.Phased); // popped out…
            Assert.NotEqual(buildings.Position[b], world.Position[worker]);         // …beside, not inside
            Assert.Equal(UnitCommand.Idle, world.CommandState[worker]);
            Assert.Equal(GatherState.Idle, world.GatherState[worker]);              // back to the gather loop
        }

        // ── DW-938: cancel construction ─────────────────────────────────────────

        [Fact]
        public void Cancel_RefundsTheExactCost_ReleasesTheBuilder_DestroysTheSite()
        {
            var (world, buildings, resources, sys) = NewHarness();
            Fixed oreBefore = resources.Ore[(int)Faction.Player1];
            int worker = SpawnWorker(world, V(10, 10));
            int b = QueueAndPhase(sys, world, resources, worker);
            Assert.True(resources.Ore[(int)Faction.Player1] < oreBefore); // premise: the build debited

            Assert.True(sys.CancelConstructionCommand(b, Faction.Player1, world));

            Assert.Equal(oreBefore.Raw, resources.Ore[(int)Faction.Player1].Raw); // EXACT 100% refund
            Assert.False(buildings.Alive[b]);                                     // site gone
            Assert.Equal((EntityFlags)0, world.Flags[worker] & EntityFlags.Phased);
            Assert.Equal(UnitCommand.Idle, world.CommandState[worker]);           // builder free again
        }

        [Fact]
        public void Cancel_Guards_ForeignAndCompletedTargets_AreSilentNoOps()
        {
            var (world, buildings, resources, sys) = NewHarness();
            int worker = SpawnWorker(world, V(10, 10));
            int b = QueueAndPhase(sys, world, resources, worker);
            Fixed oreAfterBuild = resources.Ore[(int)Faction.Player1];

            // Foreign faction cannot cancel (anti-cheat) — nothing refunded, site intact.
            Assert.False(sys.CancelConstructionCommand(b, Faction.Player2, world));
            Assert.Equal(oreAfterBuild.Raw, resources.Ore[(int)Faction.Player1].Raw);
            Assert.True(buildings.Alive[b]);

            // A COMPLETED building is not cancellable (no demolish-for-full-refund exploit).
            buildings.ConstructionTimer[b] = Dt;
            sys.Tick(world, Dt);
            Assert.False(buildings.IsUnderConstruction(b));
            Assert.False(sys.CancelConstructionCommand(b, Faction.Player1, world));
            Assert.True(buildings.Alive[b]);
        }

        // ── DW-939: placement overlap rejection ─────────────────────────────────

        [Fact]
        public void OverlappingPlacement_IsRefusedAtomically()
        {
            var (world, buildings, resources, sys) = NewHarness();
            int first = sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, V(20, 20), preBuilt: true);
            Assert.True(first >= 0);

            var events = new CombatEventQueue();
            Fixed ore0 = resources.Ore[(int)Faction.Player1];
            int worker = SpawnWorker(world, V(0, 0));

            // Dead centre on the existing building (5×5 footprints → any |dx|,|dz| < 5 overlaps).
            int rejected = sys.QueueWorkerBuild(worker, BuildingType.Barracks, V(22, 20),
                                                Faction.Player1, resources, world, events);

            Assert.Equal(-1, rejected);
            Assert.Equal(ore0.Raw, resources.Ore[(int)Faction.Player1].Raw);       // nothing spent
            Assert.NotEqual(UnitCommand.Build, world.CommandState[worker]);        // no command written
            Assert.Equal(1, events.Count);
            Assert.Equal(DenialReason.InvalidLocation, events.Get(0).Reason);      // the player is told why

            // The same order on CLEAR ground (5 + 5 half-extents → 10u apart is clear) is accepted.
            int accepted = sys.QueueWorkerBuild(worker, BuildingType.Barracks, V(31, 20),
                                                Faction.Player1, resources, world, events);
            Assert.True(accepted >= 0);
        }
    }
}
