#nullable enable
using System.IO;
using ProjectChimera.Core;
using ProjectChimera.Economy;   // BuildingSystem (Story 2.12: SetRally replay round-trip)
using ProjectChimera.Multiplayer;
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
    /// entity id packs into TargetX as a RAW int (Fixed.FromRaw), read back as o.TargetX (never via float). No
    /// format change, no <see cref="ReplayRecorder.VERSION"/> bump.
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
                using (var rec = new ReplayRecorder(path, "test://command-vocabulary", EntityWorld.DEFAULT_RNG_SEED))
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
        public void ReplayFile_RoundTrips_CastAbility_ThroughSharedApplier_NoVersionBump()
        {
            // The cast reuses the unchanged 11-byte wire — NO replay-format bump (the Story 1.12 precedent).
            Assert.Equal(2, ReplayRecorder.VERSION);

            string path = Path.GetTempFileName();
            try
            {
                using (var rec = new ReplayRecorder(path, "test://ability-cast", EntityWorld.DEFAULT_RNG_SEED))
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
        public void ReplayFile_RoundTrips_ShiftQueue_ThroughSharedApplier_NoVersionBump()
        {
            // Story 2.12: the queued flag + SetRally reuse the unchanged 11-byte wire — NO replay-format bump.
            Assert.Equal(2, ReplayRecorder.VERSION);

            string path = Path.GetTempFileName();
            try
            {
                // Record a tick carrying a QUEUED Move (0x80-flagged, id 0) and a SetRally on building 0.
                var queuedMove = new UnitOrder(0, (UnitCommand)((byte)UnitCommand.Move | UnitOrderFlags.Queued),
                                               Fixed.FromInt(12), Fixed.FromInt(-3));
                var setRally   = new UnitOrder(0, UnitCommand.SetRally, Fixed.FromInt(16), Fixed.FromInt(-4));
                using (var rec = new ReplayRecorder(path, "test://shift-queue", EntityWorld.DEFAULT_RNG_SEED))
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
    }
}
