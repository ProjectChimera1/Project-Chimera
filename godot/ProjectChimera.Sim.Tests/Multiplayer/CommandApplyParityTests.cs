#nullable enable
using System.IO;
using ProjectChimera.Core;
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
    }
}
