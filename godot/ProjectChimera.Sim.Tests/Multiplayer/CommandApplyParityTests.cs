#nullable enable
using System;
using System.Collections.Generic;   // DW-86: research level cost / ladder authoring
using System.IO;
using System.Runtime.CompilerServices; // DW-86: [CallerFilePath] for the Godot-coupled LockstepManager source pin
using System.Text.RegularExpressions;  // DW-86: source pin over LockstepManager's forwarding call sites
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions; // ItemRegistry / ItemDefinition (Story 3.15: item-command replay parity)
using ProjectChimera.Combat;    // ItemSystem / ItemStore / ModifierStore (Story 3.15)
using ProjectChimera.Economy;   // BuildingSystem (Story 2.12: SetRally replay round-trip)
using ProjectChimera.Effects;   // HealEffect / ModifierSystem (Story 3.15 consumable graph)
using ProjectChimera.Multiplayer;
using ProjectChimera.Multiplayer.Server; // DW-86: MergedTickBuilder / MergedTickApplier (the ONLINE exec-tick apply core)
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>
    /// Story 1.12 (AC6a/AC6b) — the command→world apply step and wire serialization for the new orders.
    ///
    /// AC6a (both paths agree): <c>LockstepManager.ApplyOrders</c> (live) and <c>ReplayPlayer.ApplyOrders</c>
    /// (playback) BOTH delegate to the single shared <see cref="OrderApplier.Apply"/> (Story 1.12 extracted the
    /// formerly-duplicated switch). So replay-vs-live parity is STRUCTURAL — there is exactly one switch, and a
    /// command handled in one path but not the other (the epic's #1 desync trap) is impossible. These tests pin
    /// the shared applier's documented post-state for each new order, and exercise it through a real production
    /// path (a recorded .chmr replayed by <see cref="ReplayPlayer"/>). <c>LockstepManager</c> is Godot-coupled and
    /// not constructible in this Tier-1 assembly, but it calls the identical <see cref="OrderApplier.Apply"/> line.
    ///
    /// AC6b (serialization round-trip): the new orders ride the UNCHANGED 11-byte UnitOrder wire — the target
    /// entity id packs into TargetX as a RAW int (Fixed.FromRaw), read back as o.TargetX (never via float). The
    /// command wire is unchanged; the replay CONTAINER format, however, is now <see cref="ReplayRecorder.VERSION"/>
    /// == 3 (bumped independently of these commands). The assertions below pin that value and the wire's stability.
    /// </summary>
    public class CommandApplyParityTests
    {
        private static FixedVec3 V(int x, int y, int z)
            => new FixedVec3(Fixed.FromInt(x), Fixed.FromInt(y), Fixed.FromInt(z));

        // ── AC6a — OrderApplier post-state contract for the new commands ───────────────────────────────────

        [Fact]
        public void OrderApplier_AttackTarget_StoresForcedTargetAndClearsAttacking()
        {
            var w = new EntityWorld();
            int u = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.Flags[u] |= EntityFlags.Attacking;

            // Forced enemy id packed in TargetX as a RAW int.
            OrderApplier.Apply(w, new UnitOrder(u, UnitCommand.AttackTarget, Fixed.FromRaw(2), Fixed.Zero), Faction.Player1);

            Assert.Equal(UnitCommand.AttackTarget, w.CommandState[u]);
            Assert.Equal(2, w.CommandTarget[u]);
            Assert.Equal(2, w.AttackTarget[u]);
            Assert.True((w.Flags[u] & EntityFlags.Attacking) == 0);
        }

        [Fact]
        public void OrderApplier_Follow_StoresFriendlyTarget()
        {
            var w = new EntityWorld();
            int u = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));

            OrderApplier.Apply(w, new UnitOrder(u, UnitCommand.Follow, Fixed.FromRaw(7), Fixed.Zero), Faction.Player1);

            Assert.Equal(UnitCommand.Follow, w.CommandState[u]);
            Assert.Equal(7, w.CommandTarget[u]);
        }

        [Fact]
        public void OrderApplier_Patrol_StartsFreshTwoPointRoute()
        {
            var w = new EntityWorld();
            int u = w.Create(V(3, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));

            OrderApplier.Apply(w, new UnitOrder(u, UnitCommand.Patrol, Fixed.FromInt(10), Fixed.FromInt(-4)), Faction.Player1);

            Assert.Equal(UnitCommand.Patrol, w.CommandState[u]);
            Assert.Equal(2, w.PatrolCount[u]);
            Assert.Equal(1, w.PatrolIndex[u]);
            Assert.Equal(1, w.PatrolDir[u]);
            int b = u * EntityWorld.MAX_PATROL_WAYPOINTS;
            Assert.Equal(Fixed.FromInt(3).Raw, w.PatrolWaypoints[b + 0].X.Raw);   // leg 0 = current position (return anchor)
            Assert.Equal(Fixed.FromInt(10).Raw, w.PatrolWaypoints[b + 1].X.Raw);  // leg 1 = clicked point
            Assert.Equal(Fixed.FromInt(-4).Raw, w.PatrolWaypoints[b + 1].Z.Raw);
            Assert.True((w.Flags[u] & EntityFlags.Moving) != 0);
        }

        [Fact]
        public void OrderApplier_PatrolThenAppend_BuildsMultiWaypointRoute_RewrittenToPatrol()
        {
            var w = new EntityWorld();
            int u = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));

            OrderApplier.Apply(w, new UnitOrder(u, UnitCommand.Patrol, Fixed.FromInt(10), Fixed.Zero), Faction.Player1);
            OrderApplier.Apply(w, new UnitOrder(u, UnitCommand.PatrolAppend, Fixed.FromInt(20), Fixed.Zero), Faction.Player1);
            OrderApplier.Apply(w, new UnitOrder(u, UnitCommand.PatrolAppend, Fixed.FromInt(30), Fixed.Zero), Faction.Player1);

            Assert.Equal(UnitCommand.Patrol, w.CommandState[u]); // PatrolAppend never persists as the state
            Assert.Equal(4, w.PatrolCount[u]);                   // W0(start) + 10 + 20 + 30
            Assert.Equal(1, w.PatrolIndex[u]);                   // appends never disturb the current leg
            int b = u * EntityWorld.MAX_PATROL_WAYPOINTS;
            Assert.Equal(Fixed.FromInt(0).Raw,  w.PatrolWaypoints[b + 0].X.Raw);
            Assert.Equal(Fixed.FromInt(10).Raw, w.PatrolWaypoints[b + 1].X.Raw);
            Assert.Equal(Fixed.FromInt(20).Raw, w.PatrolWaypoints[b + 2].X.Raw);
            Assert.Equal(Fixed.FromInt(30).Raw, w.PatrolWaypoints[b + 3].X.Raw);
        }

        [Fact]
        public void OrderApplier_PatrolAppend_PastCap_SilentlyIgnored_NoOverflow()
        {
            var w = new EntityWorld();
            int u = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));

            OrderApplier.Apply(w, new UnitOrder(u, UnitCommand.Patrol, Fixed.FromInt(1), Fixed.Zero), Faction.Player1);
            for (int k = 2; k < EntityWorld.MAX_PATROL_WAYPOINTS + 5; k++) // append well past the cap
                OrderApplier.Apply(w, new UnitOrder(u, UnitCommand.PatrolAppend, Fixed.FromInt(k), Fixed.Zero), Faction.Player1);

            Assert.Equal(EntityWorld.MAX_PATROL_WAYPOINTS, w.PatrolCount[u]); // capped — appends past full are no-ops
        }

        [Fact]
        public void OrderApplier_PatrolAppend_WhenNotPatrolling_StartsFreshRoute()
        {
            var w = new EntityWorld();
            int u = w.Create(V(5, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));

            // PatrolAppend with no existing route behaves exactly like a fresh Patrol.
            OrderApplier.Apply(w, new UnitOrder(u, UnitCommand.PatrolAppend, Fixed.FromInt(12), Fixed.Zero), Faction.Player1);

            Assert.Equal(UnitCommand.Patrol, w.CommandState[u]);
            Assert.Equal(2, w.PatrolCount[u]);
            Assert.Equal(1, w.PatrolIndex[u]);
        }

        [Fact]
        public void OrderApplier_IgnoresOrdersForUnownedOrDeadUnits()
        {
            var w = new EntityWorld();
            int mine    = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            int enemys  = w.Create(V(1, 0, 0), Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3));

            // Wrong owner → ignored (anti-cheat guard shared by both paths).
            OrderApplier.Apply(w, new UnitOrder(enemys, UnitCommand.Follow, Fixed.FromRaw(mine), Fixed.Zero), Faction.Player1);
            Assert.NotEqual(UnitCommand.Follow, w.CommandState[enemys]);

            // Dead unit → ignored.
            w.Destroy(mine);
            OrderApplier.Apply(w, new UnitOrder(mine, UnitCommand.AttackTarget, Fixed.FromRaw(enemys), Fixed.Zero), Faction.Player1);
            Assert.Equal(-1, w.CommandTarget[mine]);
        }

        // ── AC6b — wire serialization round-trip (UnitOrder packet) ────────────────────────────────────────

        [Fact]
        public void TickCommandPacket_RoundTrips_NewCommands_WithPackedTarget()
        {
            var orders = new[]
            {
                new UnitOrder(5, UnitCommand.AttackTarget, Fixed.FromRaw(42), Fixed.Zero),  // packed enemy id
                new UnitOrder(6, UnitCommand.Follow,       Fixed.FromRaw(7),  Fixed.Zero),  // packed friendly id
                new UnitOrder(7, UnitCommand.Patrol,       Fixed.FromInt(12), Fixed.FromInt(-3)), // ground point
                new UnitOrder(8, UnitCommand.PatrolAppend, Fixed.FromInt(4),  Fixed.FromInt(9)),
            };
            var buf = new byte[256];
            int n = TickCommandPacket.Write(buf, tick: 99, Faction.Player1, orders, orders.Length);

            var outOrders = new UnitOrder[TickCommandPacket.MAX_ORDERS];
            bool ok = TickCommandPacket.TryRead(buf, n, out uint tick, out Faction faction, outOrders, out int count);

            Assert.True(ok);
            Assert.Equal(99u, tick);
            Assert.Equal(Faction.Player1, faction);
            Assert.Equal(orders.Length, count);
            for (int i = 0; i < count; i++)
            {
                Assert.Equal(orders[i].Command, outOrders[i].Command);
                Assert.Equal(orders[i].UnitId, outOrders[i].UnitId);
                Assert.Equal(orders[i].TargetX, outOrders[i].TargetX); // raw int (packed id or ground point) survives exactly
                Assert.Equal(orders[i].TargetZ, outOrders[i].TargetZ);
            }
        }

        // ── AC6b — replay-file round-trip through the production ReplayPlayer apply path ───────────────────

        [Fact]
        public void ReplayFile_RoundTrips_NewCommands_AndAppliesThroughSharedApplier()
        {
            string path = Path.GetTempFileName();
            try
            {
                // Record a tick carrying AttackTarget + Patrol for Player1.
                using (var rec = new ReplayRecorder(path, "test://command-vocabulary", EntityWorld.DEFAULT_RNG_SEED, 0x11UL, 0x22UL, CanonicalModelHash.AlgoVersion, new[] { Faction.Player1, Faction.Player2 }))
                {
                    var orders = new[]
                    {
                        new UnitOrder(0, UnitCommand.AttackTarget, Fixed.FromRaw(2), Fixed.Zero),
                        new UnitOrder(1, UnitCommand.Patrol,       Fixed.FromInt(10), Fixed.Zero),
                    };
                    rec.RecordTick(1, Faction.Player1, orders, 0, orders.Length);
                }

                var world = new EntityWorld();
                int u0 = world.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
                int u1 = world.Create(V(1, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
                int u2 = world.Create(V(5, 0, 0), Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3));
                Assert.Equal(0, u0); Assert.Equal(1, u1); Assert.Equal(2, u2);

                var player = new ReplayPlayer(path, world);
                player.Flush(1); // applies the recorded tick via ReplayPlayer.ApplyOrders → OrderApplier.Apply

                Assert.Equal(UnitCommand.AttackTarget, world.CommandState[u0]);
                Assert.Equal(2, world.CommandTarget[u0]); // packed id round-tripped through the file
                Assert.Equal(UnitCommand.Patrol, world.CommandState[u1]);
                Assert.Equal(2, world.PatrolCount[u1]);
                Assert.Equal(Fixed.FromInt(10).Raw,
                    world.PatrolWaypoints[u1 * EntityWorld.MAX_PATROL_WAYPOINTS + 1].X.Raw);
            }
            finally
            {
                File.Delete(path);
            }
        }

        // ── Story 2.4a (AC5) — CastAbility rides the same shared applier + the same 11-byte wire ──────────────

        [Fact]
        public void OrderApplier_CastAbility_QueuesPendingIntent_AndPreservesCommandState()
        {
            var w = new EntityWorld();
            int u = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.AbilityCount[u] = 1;                 // slot 0 exists, so the applier accepts the cast
            w.CommandState[u] = UnitCommand.Move;  // the unit is mid-move

            // Self cast of slot 0: slot packed in TargetX (raw), target -1 (Self) in TargetZ (raw).
            OrderApplier.Apply(w, new UnitOrder(u, UnitCommand.CastAbility, Fixed.FromRaw(0), Fixed.FromRaw(-1)), Faction.Player1);

            Assert.Equal((byte)0, w.PendingCastSlot[u]);        // intent queued
            Assert.Equal(-1, w.PendingCastTarget[u]);
            Assert.Equal(UnitCommand.Move, w.CommandState[u]);  // a fire-and-forget cast does NOT clobber the order
        }

        [Fact]
        public void OrderApplier_CastAbility_UnknownSlot_IsANoOp()
        {
            var w = new EntityWorld();
            int u = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            // AbilityCount defaults to 0 → no slot exists → the cast is a deterministic no-op (no intent queued).
            OrderApplier.Apply(w, new UnitOrder(u, UnitCommand.CastAbility, Fixed.FromRaw(0), Fixed.FromRaw(-1)), Faction.Player1);
            Assert.Equal(EntityWorld.NO_PENDING_CAST, w.PendingCastSlot[u]);
        }

        [Fact]
        public void ReplayFile_RoundTrips_CastAbility_ThroughSharedApplier_WireUnchanged()
        {
            // Story 15.11 (DW-280): the command wire is now the 12-byte UnitOrder (ability slot in its own byte); the
            // replay container format is VERSION 6 (Story 15-23 packed entity-target payloads; 15.11 bumped for the stride).
            Assert.Equal(6, ReplayRecorder.VERSION);

            string path = Path.GetTempFileName();
            try
            {
                using (var rec = new ReplayRecorder(path, "test://ability-cast", EntityWorld.DEFAULT_RNG_SEED, 0x11UL, 0x22UL, CanonicalModelHash.AlgoVersion, new[] { Faction.Player1, Faction.Player2 }))
                {
                    var orders = new[]
                    {
                        new UnitOrder(0, UnitCommand.CastAbility, Fixed.FromRaw(0), Fixed.FromRaw(3)), // slot 0, target id 3
                    };
                    rec.RecordTick(1, Faction.Player1, orders, 0, orders.Length);
                }

                // Replayed world.
                var world = new EntityWorld();
                int caster = world.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
                world.AbilityCount[caster] = 1; // slot 0 exists
                Assert.Equal(0, caster);

                // LIVE reference: apply the identical order directly through the shared applier.
                var live = new EntityWorld();
                int liveCaster = live.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
                live.AbilityCount[liveCaster] = 1;
                OrderApplier.Apply(live, new UnitOrder(0, UnitCommand.CastAbility, Fixed.FromRaw(0), Fixed.FromRaw(3)), Faction.Player1);

                var player = new ReplayPlayer(path, world);
                player.Flush(1); // ReplayPlayer.ApplyOrders → OrderApplier.Apply (the SAME switch the live path uses)

                // Byte-identical post-state: the replayed cast intent matches the live one (structural parity).
                Assert.Equal(live.PendingCastSlot[liveCaster],   world.PendingCastSlot[caster]);
                Assert.Equal(live.PendingCastTarget[liveCaster], world.PendingCastTarget[caster]);
                Assert.Equal((byte)0, world.PendingCastSlot[caster]);
                Assert.Equal(3, world.PendingCastTarget[caster]); // packed target id round-tripped through the file
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ReplayFile_RoundTrips_ShiftQueue_ThroughSharedApplier_WireUnchanged()
        {
            // Story 2.12: the queued flag + SetRally ride the command wire (now the 12-byte UnitOrder — Story 15.11);
            // the replay container format is VERSION 6 (Story 15-23 packed entity-target payloads).
            Assert.Equal(6, ReplayRecorder.VERSION);

            string path = Path.GetTempFileName();
            try
            {
                // Record a tick carrying a QUEUED Move (0x80-flagged, id 0) and a SetRally on building 0.
                var queuedMove = new UnitOrder(0, (UnitCommand)((byte)UnitCommand.Move | UnitOrderFlags.Queued),
                                               Fixed.FromInt(12), Fixed.FromInt(-3));
                var setRally   = new UnitOrder(0, UnitCommand.SetRally, Fixed.FromInt(16), Fixed.FromInt(-4));
                using (var rec = new ReplayRecorder(path, "test://shift-queue", EntityWorld.DEFAULT_RNG_SEED, 0x11UL, 0x22UL, CanonicalModelHash.AlgoVersion, new[] { Faction.Player1, Faction.Player2 }))
                {
                    var orders = new[] { queuedMove, setRally };
                    rec.RecordTick(1, Faction.Player1, orders, 0, orders.Length);
                }

                // Replayed world + its own building store/system (ReplayPlayer threads Buildings into OrderApplier).
                var world  = new EntityWorld();
                int rUnit  = world.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
                Assert.Equal(0, rUnit);
                var rBuildings = new BuildingStore();
                var rBuildSys  = new BuildingSystem(rBuildings, new ResourceStore(Fixed.Zero));
                int rB = rBuildSys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, V(10, 0, -10), preBuilt: true);
                Assert.Equal(0, rB);

                var player = new ReplayPlayer(path, world) { Buildings = rBuildSys };
                player.Flush(1); // ReplayPlayer.ApplyOrders → OrderApplier.Apply (the SAME switch the live path uses)

                // LIVE reference: apply the identical orders directly through the shared applier.
                var live  = new EntityWorld();
                int lUnit = live.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
                var lBuildings = new BuildingStore();
                var lBuildSys  = new BuildingSystem(lBuildings, new ResourceStore(Fixed.Zero));
                int lB = lBuildSys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, V(10, 0, -10), preBuilt: true);
                OrderApplier.Apply(live, queuedMove, Faction.Player1);
                OrderApplier.Apply(live, setRally, Faction.Player1, buildings: lBuildSys);

                // Byte-identical post-state: the queued Move appended (count + slot) and the rally wrote, on BOTH paths.
                Assert.Equal(live.OrderQueueCount[lUnit], world.OrderQueueCount[rUnit]);
                Assert.Equal((byte)1, world.OrderQueueCount[rUnit]);
                Assert.Equal(live.OrderQueueCmd[lUnit * EntityWorld.MAX_ORDER_QUEUE + 0],
                             world.OrderQueueCmd[rUnit * EntityWorld.MAX_ORDER_QUEUE + 0]);
                Assert.Equal(live.OrderQueueTargetX[lUnit * EntityWorld.MAX_ORDER_QUEUE + 0],
                             world.OrderQueueTargetX[rUnit * EntityWorld.MAX_ORDER_QUEUE + 0]);
                Assert.Equal(lBuildings.HasRallyPoint[lB], rBuildings.HasRallyPoint[rB]);
                Assert.True(rBuildings.HasRallyPoint[rB]);
                Assert.Equal(lBuildings.RallyPoint[lB].X.Raw, rBuildings.RallyPoint[rB].X.Raw);
                Assert.Equal(lBuildings.RallyPoint[lB].Z.Raw, rBuildings.RallyPoint[rB].Z.Raw);
                Assert.Equal(UnitCommand.Idle, world.CommandState[rUnit]); // queued → CommandState untouched on both paths
            }
            finally
            {
                File.Delete(path);
            }
        }

        // ── Story 3.15 (P3) — UseItem / DropItem apply IDENTICALLY through the live + replay paths ──────────────

        /// <summary>A world wired with an <see cref="ItemSystem"/> + one hero carrying a 3-charge potion (inv slot 0)
        /// and a stat ring (inv slot 1) — the identical setup both the live and replay paths run against.</summary>
        private sealed class ItemParityWorld
        {
            public EntityWorld World = null!;
            public ItemStore   Items = null!;
            public HeroStore   Heroes = null!;
            public ItemSystem  Sys = null!;
            public int Hero, HeroSlot, PotionRef, RingRef;
        }

        private static readonly UnitDefinition ParityHeroDef = new UnitDefinition
        {
            Id = "hero", Category = "Melee", IsHero = true,
            Hp = 100, Speed = 3, AttackDamage = 20, AttackRange = 5, AttackSpeed = 1, Armor = 0,
        };

        private static ItemParityWorld BuildItemParityWorld()
        {
            var w = new ItemParityWorld
            {
                World = new EntityWorld(),
                Items = new ItemStore(),
                Heroes = new HeroStore(),
            };
            var modSys = new ModifierSystem();
            var modifiers = new ModifierStore(w.World, modSys);
            modSys.AttachStore(modifiers);
            var registry = new ItemRegistry(new[]
            {
                new ItemDefinition { Id = "potion", Charges = 3, EffectGraph = new HealEffect(Fixed.FromInt(75)) },
                new ItemDefinition { Id = "ring",   Charges = 0, MaxHealthDelta = Fixed.FromInt(50) },
            });
            w.Sys = new ItemSystem(w.World, w.Heroes, w.Items, modifiers, registry, events: null);

            // Mint a Player1 hero at (7,8) with 20/100 health (so the potion heal is observable).
            w.Hero = w.World.Create(new FixedVec3(Fixed.FromInt(7), Fixed.Zero, Fixed.FromInt(8)),
                                    Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.World.ApplyUnitDefinition(w.Hero, ParityHeroDef);
            w.HeroSlot = w.Heroes.Mint(new HeroId(1), w.Hero, level: 1, xp: Fixed.Zero,
                                       sourceDef: ParityHeroDef, ownerFaction: Faction.Player1);
            w.World.HeroIndex[w.Hero] = w.Heroes.PackRef(w.HeroSlot);
            w.World.Health[w.Hero] = Fixed.FromInt(20);

            // Potion → inventory slot 0 (charges 3); ring → inventory slot 1 (held).
            w.PotionRef = w.Items.Create(registry.IndexOf("potion"), 3, new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero));
            w.Items.TryResolveRef(w.PotionRef, out int ps); w.Items.Held[ps] = true;
            w.Heroes.Inventory[w.HeroSlot * HeroStore.INVENTORY_SLOTS + 0] = w.PotionRef;

            w.RingRef = w.Items.Create(registry.IndexOf("ring"), 0, new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero));
            w.Items.TryResolveRef(w.RingRef, out int rs); w.Items.Held[rs] = true;
            w.Heroes.Inventory[w.HeroSlot * HeroStore.INVENTORY_SLOTS + 1] = w.RingRef;
            return w;
        }

        [Fact]
        public void ReplayVsLive_UseAndDropItem_ApplyIdentically_ThroughSharedApplier()
        {
            // Story 3.15 (P3): the regression guard for P2 — UseItem decrements a consumable's charge + fires its heal,
            // and DropItem returns the item to the ground, IDENTICALLY through the live apply site (OrderApplier.Apply
            // with items:, the exact line LockstepManager.ApplyOrders calls) and the replay site (ReplayPlayer, Items
            // wired). If EITHER apply site stops forwarding `items`, the item command becomes a no-op and this fails.
            Assert.Equal(6, ReplayRecorder.VERSION); // Story 15-23: packed entity-target payloads → container VERSION 6 (15.11 took it to 5)

            var useOrder  = new UnitOrder(0, UnitCommand.UseItem,  Fixed.FromRaw(0), Fixed.Zero); // inv slot 0 = potion
            var dropOrder = new UnitOrder(0, UnitCommand.DropItem, Fixed.FromRaw(1), Fixed.Zero); // inv slot 1 = ring

            // LIVE: apply both orders directly through the shared applier with the ItemSystem wired.
            var live = BuildItemParityWorld();
            Assert.Equal(0, live.Hero); // hero is entity 0 (order UnitId matches)
            OrderApplier.Apply(live.World, useOrder,  Faction.Player1, items: live.Sys);
            OrderApplier.Apply(live.World, dropOrder, Faction.Player1, items: live.Sys);

            // REPLAY: record the same two orders, then replay them through ReplayPlayer with Items wired.
            string path = Path.GetTempFileName();
            try
            {
                using (var rec = new ReplayRecorder(path, "test://item-use-drop", EntityWorld.DEFAULT_RNG_SEED, 0x11UL, 0x22UL, CanonicalModelHash.AlgoVersion, new[] { Faction.Player1, Faction.Player2 }))
                {
                    var orders = new[] { useOrder, dropOrder };
                    rec.RecordTick(1, Faction.Player1, orders, 0, orders.Length);
                }

                var rep = BuildItemParityWorld();
                var player = new ReplayPlayer(path, rep.World) { Items = rep.Sys };
                player.Flush(1); // ReplayPlayer.ApplyOrders → OrderApplier.Apply(..., items: Items)

                // UseItem: the potion's charge decremented 3 → 2 and the heal (20 + 75 = 95) fired — on BOTH paths.
                Assert.True(live.Items.TryResolveRef(live.PotionRef, out int lps));
                Assert.True(rep.Items.TryResolveRef(rep.PotionRef, out int rps));
                Assert.Equal(2, live.Items.Charges[lps]);
                Assert.Equal(live.Items.Charges[lps], rep.Items.Charges[rps]);
                Assert.Equal(Fixed.FromInt(95), live.World.Health[live.Hero]);
                Assert.Equal(live.World.Health[live.Hero], rep.World.Health[rep.Hero]);

                // DropItem: the ring returned to the ground at the hero's position + the inv slot cleared — on BOTH paths.
                Assert.True(live.Items.TryResolveRef(live.RingRef, out int lrs));
                Assert.True(rep.Items.TryResolveRef(rep.RingRef, out int rrs));
                Assert.False(live.Items.Held[lrs]);
                Assert.Equal(live.Items.Held[lrs], rep.Items.Held[rrs]);
                Assert.Equal(Fixed.FromInt(7), live.Items.PosX[lrs]);
                Assert.Equal(live.Items.PosX[lrs], rep.Items.PosX[rrs]);
                Assert.Equal(live.Items.PosZ[lrs], rep.Items.PosZ[rrs]);
                Assert.Equal(HeroStore.INVENTORY_EMPTY,
                             live.Heroes.Inventory[live.HeroSlot * HeroStore.INVENTORY_SLOTS + 1]);
                Assert.Equal(HeroStore.INVENTORY_EMPTY,
                             rep.Heroes.Inventory[rep.HeroSlot * HeroStore.INVENTORY_SLOTS + 1]);
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>A world wired with an <see cref="ItemSystem"/> + a <see cref="BuildingSystem"/> shop + an owned hero
        /// in range with a starting ore balance — the identical setup both the live and replay BuyItem paths run against.</summary>
        private sealed class BuyParityWorld
        {
            public EntityWorld World = null!;
            public ItemStore   Items = null!;
            public HeroStore   Heroes = null!;
            public ItemSystem  Sys = null!;
            public BuildingStore Buildings = null!;
            public BuildingSystem BuildSys = null!;
            public ResourceStore Resources = null!;
            public int Hero, HeroSlot, ShopId;
        }

        private static BuyParityWorld BuildBuyParityWorld()
        {
            var w = new BuyParityWorld
            {
                World = new EntityWorld(),
                Items = new ItemStore(),
                Heroes = new HeroStore(),
                Buildings = new BuildingStore(),
                Resources = new ResourceStore(Fixed.Zero),
            };
            var modSys = new ModifierSystem();
            var modifiers = new ModifierStore(w.World, modSys);
            modSys.AttachStore(modifiers);
            var registry = new ItemRegistry(new[]
            {
                new ItemDefinition { Id = "ring", Charges = 0, MaxHealthDelta = Fixed.FromInt(50), CostOre = Fixed.FromInt(100) },
            });
            w.Sys = new ItemSystem(w.World, w.Heroes, w.Items, modifiers, registry, events: null);
            w.BuildSys = new BuildingSystem(w.Buildings, w.Resources, null, null, null, w.Heroes, null);

            w.Hero = w.World.Create(new FixedVec3(Fixed.FromInt(2), Fixed.Zero, Fixed.Zero),
                                    Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.World.ApplyUnitDefinition(w.Hero, ParityHeroDef);
            w.HeroSlot = w.Heroes.Mint(new HeroId(1), w.Hero, level: 1, xp: Fixed.Zero,
                                       sourceDef: ParityHeroDef, ownerFaction: Faction.Player1);
            w.World.HeroIndex[w.Hero] = w.Heroes.PackRef(w.HeroSlot);

            w.ShopId = w.Buildings.Create(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero),
                                          Faction.Player1, BuildingType.CommandCenter, revivesHeroes: false,
                                          sellsItems: true, shopStock: new[] { "ring" }, shopRadius: Fixed.FromInt(10));
            w.Buildings.ConstructionTimer[w.ShopId] = Fixed.Zero;
            w.Resources.AddOre(Faction.Player1, Fixed.FromInt(300));
            return w;
        }

        [Fact]
        public void ReplayVsLive_BuyItem_ApplyIdentically_ThroughSharedApplier()
        {
            // Story 3.16: BuyItem spends ore + mints the item into the hero's inventory IDENTICALLY through the live apply
            // site (OrderApplier.Apply with buildings: + items:) and the replay site (ReplayPlayer with Buildings + Items).
            var buy = new UnitOrder(0, UnitCommand.BuyItem, Fixed.FromRaw(0), Fixed.FromRaw(0)); // shop id 0, stock 0, hero entity 0

            var live = BuildBuyParityWorld();
            Assert.Equal(0, live.ShopId);
            Assert.Equal(0, live.Hero);
            OrderApplier.Apply(live.World, buy, Faction.Player1, buildings: live.BuildSys, items: live.Sys);

            string path = Path.GetTempFileName();
            try
            {
                using (var rec = new ReplayRecorder(path, "test://buy-item", EntityWorld.DEFAULT_RNG_SEED, 0x11UL, 0x22UL, CanonicalModelHash.AlgoVersion, new[] { Faction.Player1, Faction.Player2 }))
                {
                    var orders = new[] { buy };
                    rec.RecordTick(1, Faction.Player1, orders, 0, orders.Length);
                }

                var rep = BuildBuyParityWorld();
                var player = new ReplayPlayer(path, rep.World) { Items = rep.Sys, Buildings = rep.BuildSys };
                player.Flush(1);

                // Both paths spent 100 ore and minted the ring into inventory slot 0.
                Assert.Equal(live.Resources.Ore[(int)Faction.Player1].Raw, rep.Resources.Ore[(int)Faction.Player1].Raw);
                Assert.Equal(200, live.Resources.Ore[(int)Faction.Player1].ToInt());
                int liveRef = live.Heroes.Inventory[live.HeroSlot * HeroStore.INVENTORY_SLOTS + 0];
                int repRef  = rep.Heroes.Inventory[rep.HeroSlot * HeroStore.INVENTORY_SLOTS + 0];
                Assert.NotEqual(HeroStore.INVENTORY_EMPTY, liveRef);
                Assert.Equal(liveRef, repRef);
                Assert.Equal(live.World.EffectiveMaxHealth[live.Hero], rep.World.EffectiveMaxHealth[rep.Hero]);
            }
            finally
            {
                File.Delete(path);
            }
        }

        // ── DW-86 (Story 4.9) — StartResearch / CancelResearch replay-vs-live round-trip ──────────────────────
        //
        // Story 4.9 threaded a `research:` handle into BOTH apply sites, but every test that drove
        // StartResearch/CancelResearch through OrderApplier.Apply injected `research:` BY HAND — none went through
        // ReplayPlayer or the online merged-tick apply core. Deleting the `Research` forwarding argument at either
        // site therefore left the whole suite green while making recorded replays (and online matches) silently
        // stop applying research: the exact AR-17 replay-vs-live divergence class this file exists to prevent.
        // The three arms below are the three REAL production apply paths for a research command:
        //   • offline / F5      → OrderApplier.Apply(..., research:)                (CommandCardSystem's direct apply)
        //   • online exec-tick  → MergedTickApplier.Apply(..., research:)           (LockstepManager.ApplyMerged's core)
        //   • replay playback   → ReplayPlayer { Research = ... }.Flush(tick)       (ReplayPlayer.ApplyOrders)
        // All three must land byte-identical ResearchStore/ResourceStore/effective-stat state.

        // Research-list indices within ParityResearchFaction (declaration order below).
        private const int ParityArmorUpIdx  = 0; // armor_up:  100 ore, 6 ticks, +2 armor, 50% cancel refund
        private const int ParityDamageUpIdx = 1; // damage_up: 150 ore, 3 ticks, +5 attack damage — a NONZERO index,
                                                 // so a mis-sourced/zeroed raw TargetX decode cannot pass vacuously.
        private const int ParityDamageUpTicks = 3;

        /// <summary>DW-86 — a world wired with a <see cref="ResearchSystem"/>, one OPERATIONAL Player1 lab that offers
        /// both researches, an ore balance and one living Player1 unit (so a completion's cumulative modifier is
        /// observable). Built identically for the live, online-merged and replay arms.</summary>
        private sealed class ResearchParityWorld
        {
            public EntityWorld   World = null!;
            public BuildingStore Buildings = null!;
            public ResourceStore Resources = null!;
            public ResearchStore Research = null!;
            public ResearchSystem Sys = null!;
            public int LabId, Unit;
        }

        /// <summary>The authored faction both arms share (mirrors production's shared-faction-json setup).</summary>
        private static FactionDefinition ParityResearchFaction() => new FactionDefinition
        {
            Id = "parity_research",
            Buildings = new List<BuildingDefinition>
            {
                new BuildingDefinition { Id = "lab", AvailableResearch = new[] { "armor_up", "damage_up" } },
            },
            Research = new List<ResearchDefinition>
            {
                new ResearchDefinition
                {
                    Id = "armor_up",
                    CancelRefundFraction = 0.5f,
                    Prerequisites = Array.Empty<string>(),
                    Levels = new List<ResearchLevel>
                    {
                        new ResearchLevel { Cost = new Dictionary<string, int> { { "ore", 100 } }, TimeTicks = 6,
                                            ModifierDelta = new ResearchModifierDelta { ArmorDelta = 2f } },
                    },
                },
                new ResearchDefinition
                {
                    Id = "damage_up",
                    CancelRefundFraction = 0.5f,
                    Prerequisites = Array.Empty<string>(),
                    Levels = new List<ResearchLevel>
                    {
                        new ResearchLevel { Cost = new Dictionary<string, int> { { "ore", 150 } }, TimeTicks = ParityDamageUpTicks,
                                            ModifierDelta = new ResearchModifierDelta { AttackDamageDelta = 5f } },
                    },
                },
            },
        };

        private static ResearchParityWorld BuildResearchParityWorld(int startingOre = 1000)
        {
            var w = new ResearchParityWorld
            {
                World     = new EntityWorld(),
                Buildings = new BuildingStore(),
                Resources = new ResourceStore(Fixed.Zero),
                Research  = new ResearchStore(),
            };
            var modSys = new ModifierSystem();
            var modifiers = new ModifierStore(w.World, modSys);
            modSys.AttachStore(modifiers);

            w.Sys = new ResearchSystem(w.Buildings, w.Resources, w.Research, modifiers,
                                       events: null, p1Faction: ParityResearchFaction(), p2Faction: null);

            w.LabId = w.Buildings.Create(FixedVec3.Zero, Faction.Player1, BuildingType.Custom, buildingId: "lab");
            w.Buildings.ConstructionTimer[w.LabId] = Fixed.Zero; // pre-built / operational (a constructing lab silent-rejects)
            w.Resources.AddOre(Faction.Player1, Fixed.FromInt(startingOre));

            // One living Player1 unit with a real attack stat, so a completed damage_up is observable on the world too.
            w.Unit = w.World.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.World.ApplyUnitDefinition(w.Unit, ParityResearchUnitDef);
            return w;
        }

        private static readonly UnitDefinition ParityResearchUnitDef = new UnitDefinition
        {
            Id = "grunt", Category = "Melee",
            Hp = 100, Speed = 3, AttackDamage = 20, AttackRange = 5, AttackSpeed = 1, Armor = 0,
        };

        private static int Ore(ResearchParityWorld w) => w.Resources.Ore[(int)Faction.Player1].ToInt();
        private static int InProgress(ResearchParityWorld w) => w.Research.InProgressIndex[(int)Faction.Player1];

        /// <summary>Record <paramref name="orders"/> as Player1's tick-1 bundle, replay them into a fresh parity world
        /// with ONLY <c>Research</c> wired (exactly what MatchLifecycleController wires for playback), and return it.</summary>
        private static ResearchParityWorld ReplayResearchOrders(string label, params UnitOrder[] orders)
        {
            string path = Path.GetTempFileName();
            try
            {
                using (var rec = new ReplayRecorder(path, label, EntityWorld.DEFAULT_RNG_SEED, 0x11UL, 0x22UL,
                                                    CanonicalModelHash.AlgoVersion, new[] { Faction.Player1, Faction.Player2 }))
                {
                    rec.RecordTick(1, Faction.Player1, orders, 0, orders.Length);
                }

                var rep = BuildResearchParityWorld();
                var player = new ReplayPlayer(path, rep.World) { Research = rep.Sys };
                player.Flush(1); // ReplayPlayer.ApplyOrders → OrderApplier.Apply(..., research: Research)
                return rep;
            }
            finally
            {
                File.Delete(path);
            }
        }

        /// <summary>Fan <paramref name="orders"/> through the REAL server merge, then apply the merged packet through
        /// <see cref="MergedTickApplier"/> with <c>research:</c> wired — the exact core LockstepManager.ApplyMerged
        /// calls for an online exec-tick — into a fresh parity world.</summary>
        private static ResearchParityWorld ApplyMergedResearchOrders(params UnitOrder[] orders)
        {
            var buf = new byte[TickCommandPacket.HEADER_BYTES + TickCommandPacket.MAX_ORDERS * UnitOrder.SIZE];
            int len = TickCommandPacket.Write(buf, 1u, Faction.Player1, orders, orders.Length);

            var builder = new MergedTickBuilder(1, new[] { Faction.Player1 });
            Assert.True(builder.Submit(0, buf, len, out _));
            Assert.True(builder.TryBuild(1u, out byte[] merged, out int mergedLen));

            var online = BuildResearchParityWorld();
            MergedTickApplier.Apply(merged, mergedLen, online.World, research: online.Sys);
            return online;
        }

        [Fact]
        public void ReplayVsLive_StartResearch_ApplyIdentically_ThroughSharedApplier()
        {
            // DW-86: a NONZERO research index rides TargetX as a RAW int (never via .ToFloat()) — index 0 would pass
            // vacuously against a dropped/zeroed decode.
            var start = new UnitOrder(0, UnitCommand.StartResearch, Fixed.FromRaw(ParityDamageUpIdx), Fixed.Zero);

            // LIVE (offline / F5): the exact OrderApplier line CommandCardSystem.IssueResearchCommand calls.
            var live = BuildResearchParityWorld();
            Assert.Equal(0, live.LabId); // the order's UnitId names building 0
            OrderApplier.Apply(live.World, start, Faction.Player1, research: live.Sys);

            // Non-vacuity: the live arm genuinely started + spent, so the equality assertions below have teeth.
            Assert.Equal(ParityDamageUpIdx, InProgress(live));
            Assert.Equal(1000 - 150, Ore(live));

            // REPLAY: the same recorded order through ReplayPlayer with Research wired.
            var rep = ReplayResearchOrders("test://start-research", start);

            Assert.Equal(InProgress(live), InProgress(rep));
            Assert.Equal(live.Research.RemainingTicks[(int)Faction.Player1], rep.Research.RemainingTicks[(int)Faction.Player1]);
            Assert.Equal(live.Resources.Ore[(int)Faction.Player1].Raw, rep.Resources.Ore[(int)Faction.Player1].Raw);

            // …and the whole downstream chain (countdown → completion → cumulative modifier on every living unit)
            // lands identically, so a dropped forwarding arg cannot hide behind an unobserved store field.
            for (int i = 0; i < ParityDamageUpTicks; i++)
            {
                live.Sys.Tick(live.World, Fixed.Zero);
                rep.Sys.Tick(rep.World, Fixed.Zero);
            }
            Assert.Equal(1, live.Research.CompletedLevels[(int)Faction.Player1][ParityDamageUpIdx]); // non-vacuity
            Assert.Equal(Fixed.FromInt(25), live.World.EffectiveAttackDamage[live.Unit]);            // 20 base + 5
            Assert.Equal(live.Research.CompletedLevels[(int)Faction.Player1][ParityDamageUpIdx],
                         rep.Research.CompletedLevels[(int)Faction.Player1][ParityDamageUpIdx]);
            Assert.Equal(live.World.EffectiveAttackDamage[live.Unit].Raw, rep.World.EffectiveAttackDamage[rep.Unit].Raw);
        }

        [Fact]
        public void ReplayVsLive_CancelResearch_ApplyIdentically_ThroughSharedApplier()
        {
            // CancelResearch's TargetX is unused/reserved; the refund is 50% of the IN-PROGRESS level's cost.
            var cancel = new UnitOrder(0, UnitCommand.CancelResearch, Fixed.Zero, Fixed.Zero);

            // LIVE: seed an in-progress armor_up (directly, not the thing under test), then cancel via the applier.
            var live = BuildResearchParityWorld();
            Assert.True(live.Sys.StartResearchCommand(live.LabId, Faction.Player1, ParityArmorUpIdx));
            int liveOreAfterStart = Ore(live);
            OrderApplier.Apply(live.World, cancel, Faction.Player1, research: live.Sys);

            // Non-vacuity: the live arm genuinely refunded and went idle.
            Assert.Equal(-1, InProgress(live));
            Assert.Equal(liveOreAfterStart + 50, Ore(live)); // 0.5 × 100

            // REPLAY: an identically seeded world, cancelled by the RECORDED order through ReplayPlayer.
            string path = Path.GetTempFileName();
            try
            {
                using (var rec = new ReplayRecorder(path, "test://cancel-research", EntityWorld.DEFAULT_RNG_SEED, 0x11UL, 0x22UL,
                                                    CanonicalModelHash.AlgoVersion, new[] { Faction.Player1, Faction.Player2 }))
                {
                    var orders = new[] { cancel };
                    rec.RecordTick(1, Faction.Player1, orders, 0, orders.Length);
                }

                var rep = BuildResearchParityWorld();
                Assert.True(rep.Sys.StartResearchCommand(rep.LabId, Faction.Player1, ParityArmorUpIdx));
                var player = new ReplayPlayer(path, rep.World) { Research = rep.Sys };
                player.Flush(1);

                Assert.Equal(InProgress(live), InProgress(rep));
                Assert.Equal(live.Research.RemainingTicks[(int)Faction.Player1], rep.Research.RemainingTicks[(int)Faction.Player1]);
                Assert.Equal(live.Resources.Ore[(int)Faction.Player1].Raw, rep.Resources.Ore[(int)Faction.Player1].Raw);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void OnlineMergedVsReplayVsLive_ResearchCommands_ApplyIdentically()
        {
            // The ONLINE arm: LockstepManager.ApplyMerged's sole command source is MergedTickApplier, which forwards
            // its own `research` handle down to the same OrderApplier switch. A dropped forwarding arg THERE desyncs
            // every online peer from the offline/replay result while the rest of the suite stays green.
            var start = new UnitOrder(0, UnitCommand.StartResearch, Fixed.FromRaw(ParityDamageUpIdx), Fixed.Zero);

            var live   = BuildResearchParityWorld();
            OrderApplier.Apply(live.World, start, Faction.Player1, research: live.Sys);
            var online = ApplyMergedResearchOrders(start);
            var rep    = ReplayResearchOrders("test://merged-research", start);

            Assert.Equal(ParityDamageUpIdx, InProgress(live)); // non-vacuity
            Assert.Equal(InProgress(live), InProgress(online));
            Assert.Equal(InProgress(live), InProgress(rep));
            Assert.Equal(live.Resources.Ore[(int)Faction.Player1].Raw, online.Resources.Ore[(int)Faction.Player1].Raw);
            Assert.Equal(live.Resources.Ore[(int)Faction.Player1].Raw, rep.Resources.Ore[(int)Faction.Player1].Raw);
            Assert.Equal(live.Research.RemainingTicks[(int)Faction.Player1], online.Research.RemainingTicks[(int)Faction.Player1]);
            Assert.Equal(live.Research.RemainingTicks[(int)Faction.Player1], rep.Research.RemainingTicks[(int)Faction.Player1]);
        }

        /// <summary>
        /// DW-86 (the Godot-coupled half) — <c>LockstepManager</c> is outside the Godot-free compile set, so its own
        /// forwarding cannot be executed here; pin it at source level instead (the <c>LocalFactionSingleSourceTests</c>
        /// / <c>FallbackMirrorParityTests</c> precedent). EVERY apply call it makes — the two offline
        /// <c>OrderApplier.Apply</c> sites (EnqueueDslEvent/EnqueueConcede) and the online <c>MergedTickApplier.Apply</c>
        /// in <c>ApplyMerged</c> — must pass its <c>Research</c> handle, or a research command issued on that path
        /// becomes a silent deterministic no-op that diverges from replay.
        /// </summary>
        [Fact]
        public void LockstepManager_ForwardsResearch_AtEveryApplySite()
        {
            string blob = StripCommentsAndNormalize(File.ReadAllText(LockstepManagerFile()));

            // The handle must still be declared (vacuous-pass guard: a rename would make the scan meaningless).
            Assert.Matches(@"public ProjectChimera\.Economy\.ResearchSystem\?? Research;", blob);

            var sites = Regex.Matches(blob, @"\b(?:OrderApplier|MergedTickApplier)\.Apply\(");
            Assert.True(sites.Count >= 3,
                $"Expected LockstepManager to keep at least 3 order-apply call sites (2 offline + ApplyMerged); found {sites.Count}. " +
                "If the shape changed, re-point this DW-86 pin at the new forwarding sites.");

            foreach (Match site in sites)
            {
                string args = ArgumentList(blob, site.Index + site.Length - 1);
                Assert.True(Regex.IsMatch(args, @"\bResearch\b"),
                    "A LockstepManager order-apply call site does NOT forward its Research handle — a StartResearch/" +
                    "CancelResearch command applied there becomes a silent no-op while replay still applies it (DW-86). " +
                    "Call site args: " + args);
            }
        }

        /// <summary>
        /// DW-626 — the OFFLINE/F5 half of the DW-86 pin. <c>CommandCardSystem</c> (src/UI) is Godot-coupled and
        /// therefore outside this Godot-free assembly's compile set, so its two direct apply sites —
        /// <c>IssueResearchCommand</c> (StartResearch) and <c>IssueCancelResearchCommand</c> (CancelResearch) — cannot
        /// be executed here. They are the ONLY production path a research command takes offline: online it is enqueued
        /// (LockstepManager → MergedTickApplier, pinned above) and playback goes through ReplayPlayer (executed above).
        /// Dropping <c>research:</c> at either site would make offline/F5 research a silent deterministic no-op while
        /// online AND replay still apply it — the mirror image of the replay-vs-live divergence DW-86 closed, and
        /// invisible to every executable test in this file (they all inject <c>research:</c> by hand).
        /// </summary>
        [Fact]
        public void CommandCardSystem_ForwardsResearch_AtEveryOfflineResearchApplySite()
        {
            string blob = StripCommentsAndNormalize(File.ReadAllText(CommandCardSystemFile()));

            // Vacuous-pass guard #1: the offline handle must still be DECLARED under the name the scan asserts on —
            // a rename would otherwise make every `research: _research` check below unfalsifiable.
            Assert.Matches(@"private (?:ProjectChimera\.Economy\.)?ResearchSystem\?? _research;", blob);

            // Every offline command in this file has the shape `var order = new UnitOrder(<cmd>, …);` immediately
            // followed by the `OrderApplier.Apply(_world, in order, …);` that consumes it, so pair each constructed
            // order with the first apply that follows it and precedes the NEXT constructed order.
            var ctors   = Regex.Matches(blob, @"\bnew UnitOrder\(");
            var applies = Regex.Matches(blob, @"\bOrderApplier\.Apply\(");
            Assert.True(ctors.Count > 0 && applies.Count > 0,
                $"Found {ctors.Count} UnitOrder construction(s) and {applies.Count} OrderApplier.Apply call site(s) in " +
                "CommandCardSystem.cs — the offline apply shape changed; re-point this DW-626 pin at the new sites.");

            int researchSites = 0;
            for (int c = 0; c < ctors.Count; c++)
            {
                string orderArgs = ArgumentList(blob, ctors[c].Index + ctors[c].Length - 1);
                if (!Regex.IsMatch(orderArgs, @"\bUnitCommand\.(?:Start|Cancel)Research\b")) continue;

                int nextCtor = (c + 1 < ctors.Count) ? ctors[c + 1].Index : blob.Length;
                Match? site = null;
                foreach (Match m in applies)
                    if (m.Index > ctors[c].Index && m.Index < nextCtor) { site = m; break; }

                Assert.True(site != null,
                    "A research UnitOrder is constructed in CommandCardSystem.cs with no OrderApplier.Apply consuming " +
                    "it before the next order is built — the offline apply shape changed (DW-626). Order args: " + orderArgs);

                string args = ArgumentList(blob, site!.Index + site.Length - 1);
                Assert.True(Regex.IsMatch(args, @"\bresearch:\s*_research\b"),
                    "The OFFLINE (F5) research apply site in CommandCardSystem does NOT forward its `research: _research` " +
                    "handle — a StartResearch/CancelResearch issued offline becomes a silent deterministic no-op while " +
                    "the online (MergedTickApplier) and replay (ReplayPlayer) paths still apply it (DW-626). " +
                    "Order args: " + orderArgs + " | Apply args: " + args);
                researchSites++;
            }

            // Vacuous-pass guard #2: BOTH offline research apply sites must have been reached. If the loop above
            // matched nothing (regex drift, a renamed command, the sites moved out of this file) it would pass
            // trivially and pin nothing at all.
            Assert.True(researchSites >= 2,
                $"Expected CommandCardSystem to keep at least 2 offline research apply sites (StartResearch + " +
                $"CancelResearch); the scan checked {researchSites}. If the shape changed, re-point this DW-626 pin.");
        }

        /// <summary>
        /// DW-763 — the same DW-626 pin, one handle over: the OFFLINE apply sites of the <c>items:</c> handle.
        /// <c>CommandCardSystem.IssueBuyCommand</c> (BuyItem) and <c>SelectionSystem</c>'s three issue methods
        /// (PickupItem / UseItem / DropItem) are the ONLY production path an item command takes offline — online it
        /// is enqueued (LockstepManager → MergedTickApplier) and playback goes through ReplayPlayer. Both files live
        /// under src/UI, so they are Godot-coupled and outside this Godot-free assembly's compile set; dropping
        /// <c>items:</c> at any of the four sites would make an offline buy/pickup/use/drop a silent deterministic
        /// no-op while online AND replay still applied it. The executable siblings
        /// (<c>ReplayVsLive_BuyItem_…</c> / <c>ReplayVsLive_UseAndDropItem_…</c>) cannot see that regression — like
        /// the research tests before DW-626 they inject <c>items:</c> BY HAND at the live arm, so the suite stays
        /// green straight through it. Hence a SOURCE pin, spanning both files.
        /// </summary>
        [Fact]
        public void OfflineItemCommands_ForwardTheItemSystem_AtEveryApplySite()
        {
            // ── CommandCardSystem: the single BuyItem apply site (the file also applies research/train orders that
            //    must NOT be required to carry `items:`, so pair by the CONSTRUCTED command like the DW-626 pin). ──
            string cardBlob = StripCommentsAndNormalize(File.ReadAllText(CommandCardSystemFile()));

            // Vacuous-pass guard #1 (rename canary): the offline handle must still be DECLARED under the asserted name.
            Assert.Matches(@"private (?:ProjectChimera\.Combat\.)?ItemSystem\??\s+_itemSys;", cardBlob);

            int buySites = AssertHandleForwardedAtOfflineApplySites(
                cardBlob, @"\bUnitCommand\.BuyItem\b", @"\bitems:\s*_itemSys\b", "CommandCardSystem.cs");
            Assert.True(buySites >= 1,
                $"Expected CommandCardSystem to keep at least 1 offline BuyItem apply site; the scan checked {buySites}. " +
                "If the shape changed, re-point this DW-763 pin.");

            // ── SelectionSystem: the three hero-item apply sites (Pickup / Use / Drop). ──
            string selBlob = StripCommentsAndNormalize(File.ReadAllText(SelectionSystemFile()));

            // Vacuous-pass guard #2 (rename canary) — same declaration shape, second file.
            Assert.Matches(@"private (?:ProjectChimera\.Combat\.)?ItemSystem\??\s+_itemSys;", selBlob);

            int heroItemSites = AssertHandleForwardedAtOfflineApplySites(
                selBlob, @"\bUnitCommand\.(?:PickupItem|UseItem|DropItem)\b", @"\bitems:\s*_itemSys\b", "SelectionSystem.cs");
            Assert.True(heroItemSites >= 3,
                $"Expected SelectionSystem to keep at least 3 offline item apply sites (PickupItem + UseItem + " +
                $"DropItem); the scan checked {heroItemSites}. If the shape changed, re-point this DW-763 pin.");
        }

        /// <summary>
        /// DW-763 — the DW-626 pairing walk, extracted so a second handle in a second file reuses it verbatim: every
        /// <c>new UnitOrder(</c> whose arguments match <paramref name="commandPattern"/> must be consumed by an
        /// <c>OrderApplier.Apply(</c> before the NEXT constructed order, and that apply's argument list must match
        /// <paramref name="handlePattern"/>. Returns how many matching sites were checked (the caller asserts a floor,
        /// so regex drift can never make the pin vacuous).
        /// </summary>
        private static int AssertHandleForwardedAtOfflineApplySites(string blob, string commandPattern,
                                                                    string handlePattern, string fileLabel)
        {
            var ctors   = Regex.Matches(blob, @"\bnew UnitOrder\(");
            var applies = Regex.Matches(blob, @"\bOrderApplier\.Apply\(");
            Assert.True(ctors.Count > 0 && applies.Count > 0,
                $"Found {ctors.Count} UnitOrder construction(s) and {applies.Count} OrderApplier.Apply call site(s) in " +
                $"{fileLabel} — the offline apply shape changed; re-point this DW-763 pin at the new sites.");

            int checkedSites = 0;
            for (int c = 0; c < ctors.Count; c++)
            {
                string orderArgs = ArgumentList(blob, ctors[c].Index + ctors[c].Length - 1);
                if (!Regex.IsMatch(orderArgs, commandPattern)) continue;

                int nextCtor = (c + 1 < ctors.Count) ? ctors[c + 1].Index : blob.Length;
                Match? site = null;
                foreach (Match m in applies)
                    if (m.Index > ctors[c].Index && m.Index < nextCtor) { site = m; break; }

                Assert.True(site != null,
                    $"An item UnitOrder is constructed in {fileLabel} with no OrderApplier.Apply consuming it before " +
                    "the next order is built — the offline apply shape changed (DW-763). Order args: " + orderArgs);

                string args = ArgumentList(blob, site!.Index + site.Length - 1);
                Assert.True(Regex.IsMatch(args, handlePattern),
                    $"An OFFLINE item apply site in {fileLabel} does NOT forward its `items: _itemSys` handle — that " +
                    "BuyItem/PickupItem/UseItem/DropItem becomes a silent deterministic no-op offline while the online " +
                    "(MergedTickApplier) and replay (ReplayPlayer) paths still apply it (DW-763). " +
                    "Order args: " + orderArgs + " | Apply args: " + args);
                checkedSites++;
            }
            return checkedSites;
        }

        /// <summary>Return the balanced parenthesised argument list that STARTS at <paramref name="openParen"/>
        /// (which must index the '(' itself), exclusive of the outer parentheses.</summary>
        private static string ArgumentList(string blob, int openParen)
        {
            int depth = 0;
            for (int i = openParen; i < blob.Length; i++)
            {
                if (blob[i] == '(') depth++;
                else if (blob[i] == ')')
                {
                    depth--;
                    if (depth == 0) return blob.Substring(openParen + 1, i - openParen - 1);
                }
            }
            throw new InvalidOperationException("Unbalanced parentheses while scanning a source-pinned call site.");
        }

        /// <summary>Strip block/line comments then collapse whitespace, so comment prose can never satisfy (or hide)
        /// the pin above. Mirrors <c>LocalFactionSingleSourceTests.StripCommentsAndNormalize</c>.</summary>
        private static string StripCommentsAndNormalize(string text)
        {
            text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            text = Regex.Replace(text, @"//[^\n]*", " ");
            return Regex.Replace(text, @"\s+", " ");
        }

        // This file lives in godot/ProjectChimera.Sim.Tests/Multiplayer/ → ../../src/Multiplayer/LockstepManager.cs.
        private static string LockstepManagerFile([CallerFilePath] string thisFilePath = "")
            => ResolveFromHere(thisFilePath, "..", "..", "src", "Multiplayer", "LockstepManager.cs");

        // DW-626 — the OFFLINE/F5 apply site: ../../src/UI/CommandCardSystem.cs.
        private static string CommandCardSystemFile([CallerFilePath] string thisFilePath = "")
            => ResolveFromHere(thisFilePath, "..", "..", "src", "UI", "CommandCardSystem.cs");

        // DW-763 — the second OFFLINE apply file (PickupItem/UseItem/DropItem): ../../src/UI/SelectionSystem.cs.
        private static string SelectionSystemFile([CallerFilePath] string thisFilePath = "")
            => ResolveFromHere(thisFilePath, "..", "..", "src", "UI", "SelectionSystem.cs");

        /// <summary>Resolve a repo-relative path from THIS test file's own directory (portable across checkouts —
        /// no dependency on the runner's working directory). Mirrors <c>LocalFactionSingleSourceTests</c>.</summary>
        private static string ResolveFromHere(string thisFilePath, params string[] parts)
        {
            string dir = Path.GetDirectoryName(thisFilePath)
                         ?? throw new InvalidOperationException("Could not resolve this test's source dir via [CallerFilePath].");
            string[] segments = new string[parts.Length + 1];
            segments[0] = dir;
            Array.Copy(parts, 0, segments, 1, parts.Length);
            return Path.GetFullPath(Path.Combine(segments));
        }
    }
}
