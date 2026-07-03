#nullable enable
using ProjectChimera.Combat;   // CombatEventQueue / CombatEventType (OrderDenied)
using ProjectChimera.Core;
using ProjectChimera.Economy;  // BuildingSystem (SetRally exec-tick handler)
using ProjectChimera.Multiplayer;
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>
    /// Story 2.12 (AC1–AC4) — the shift-queue + rally command path, proven at the sim layer (Godot-free Tier-1).
    /// Covers: the queued-flag append vs plain-order clear (AC1.2), the wire round-trip of the 0x80 flag, the
    /// <see cref="OrderQueueSystem"/> completion-gated dispatch with teeth (AC1.3/1.4), the full-ring reject + denial
    /// event (AC4), and the SetRally wire command's store write + faction anti-cheat (AC3). The v9 fold / pin / recycle
    /// teeth live in <c>SimChecksumCoverageGuardTest</c> + <c>ApplyUnitDefinitionGuardTest</c>; the golden/loopback in
    /// <c>ShiftQueueGoldenTests</c>.
    /// </summary>
    public class OrderQueueTests
    {
        private static FixedVec3 V(int x, int y, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.FromInt(y), Fixed.FromInt(z));

        private static UnitOrder Queued(int id, UnitCommand cmd, Fixed tx, Fixed tz)
            => new UnitOrder(id, (UnitCommand)((byte)cmd | UnitOrderFlags.Queued), tx, tz);

        // ── AC1.2 — queued APPENDS (no CommandState touch); plain CLEARS the ring + applies ───────────────────

        [Fact]
        public void QueuedOrder_Appends_WithoutTouchingCommandState()
        {
            var w = new EntityWorld();
            int u = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            Assert.Equal(UnitCommand.Idle, w.CommandState[u]); // resting

            OrderApplier.Apply(w, Queued(u, UnitCommand.Move, Fixed.FromInt(10), Fixed.FromInt(-4)), Faction.Player1);

            // Appended to slot 0, count == 1, and CommandState is UNTOUCHED (still Idle — the queue does not activate here).
            Assert.Equal((byte)1, w.OrderQueueCount[u]);
            Assert.Equal((byte)UnitCommand.Move, w.OrderQueueCmd[u * EntityWorld.MAX_ORDER_QUEUE + 0]);
            Assert.Equal(Fixed.FromInt(10).Raw, w.OrderQueueTargetX[u * EntityWorld.MAX_ORDER_QUEUE + 0]);
            Assert.Equal(Fixed.FromInt(-4).Raw, w.OrderQueueTargetZ[u * EntityWorld.MAX_ORDER_QUEUE + 0]);
            Assert.Equal(UnitCommand.Idle, w.CommandState[u]);
        }

        [Fact]
        public void PlainOrder_ClearsTheRing_AndAppliesImmediately()
        {
            var w = new EntityWorld();
            int u = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            // Pre-load two queued orders...
            OrderApplier.Apply(w, Queued(u, UnitCommand.Move, Fixed.FromInt(5), Fixed.Zero), Faction.Player1);
            OrderApplier.Apply(w, Queued(u, UnitCommand.Move, Fixed.FromInt(9), Fixed.Zero), Faction.Player1);
            Assert.Equal((byte)2, w.OrderQueueCount[u]);

            // ...then a PLAIN Move must clear the ring (replace) and apply the active order now.
            OrderApplier.Apply(w, new UnitOrder(u, UnitCommand.Move, Fixed.FromInt(20), Fixed.Zero), Faction.Player1);

            Assert.Equal((byte)0, w.OrderQueueCount[u]);          // ring cleared
            Assert.Equal(UnitCommand.Move, w.CommandState[u]);    // applied immediately
            Assert.Equal(Fixed.FromInt(20).Raw, w.CommandGoal[u].X.Raw);
        }

        [Fact]
        public void QueuedFlag_RoundTripsThroughTheWire_AndIsMaskedOnApply()
        {
            // The flagged command rides the UNCHANGED 11-byte wire as (byte)(cmd | 0x80).
            var orders = new[] { Queued(0, UnitCommand.AttackMove, Fixed.FromInt(7), Fixed.FromInt(3)) };
            var buf = new byte[TickCommandPacket.HEADER_BYTES + UnitOrder.SIZE];
            TickCommandPacket.Write(buf, 1u, Faction.Player1, orders, orders.Length);

            var outOrders = new UnitOrder[TickCommandPacket.MAX_ORDERS];
            Assert.True(TickCommandPacket.TryRead(buf, buf.Length, out _, out _, outOrders, out int n));
            Assert.Equal(1, n);
            // The high bit survives the wire, and masking recovers AttackMove.
            Assert.Equal((byte)UnitCommand.AttackMove | UnitOrderFlags.Queued, (byte)outOrders[0].Command);
            Assert.Equal(UnitCommand.AttackMove, (UnitCommand)((byte)outOrders[0].Command & UnitOrderFlags.CommandMask));

            // Applying the round-tripped order appends (does not activate) — the flag drove the append branch.
            var w = new EntityWorld();
            int u = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            Assert.Equal(0, u);
            OrderApplier.Apply(w, outOrders[0], Faction.Player1);
            Assert.Equal((byte)1, w.OrderQueueCount[u]);
            Assert.Equal(UnitCommand.Idle, w.CommandState[u]); // masked flag never reached CommandState
        }

        // ── AC1.3 / AC1.4 — OrderQueueSystem dispatches on the pure-sim completion signal, with teeth ──────────

        [Fact]
        public void OrderQueueSystem_DispatchesQueuedMoves_OnArrival_InOrder()
        {
            var w = new EntityWorld();
            var sys = new OrderQueueSystem();
            int u = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            OrderApplier.Apply(w, Queued(u, UnitCommand.Move, Fixed.FromInt(10), Fixed.Zero), Faction.Player1);
            OrderApplier.Apply(w, Queued(u, UnitCommand.Move, Fixed.FromInt(20), Fixed.Zero), Faction.Player1);
            Assert.Equal((byte)2, w.OrderQueueCount[u]);

            // Tick 1: CommandState is Idle → the FIRST queued order dispatches; goal = (10,0,0), count drops to 1.
            sys.Tick(w, Fixed.Zero);
            Assert.Equal(UnitCommand.Move, w.CommandState[u]);
            Assert.Equal(Fixed.FromInt(10).Raw, w.CommandGoal[u].X.Raw);
            Assert.Equal((byte)1, w.OrderQueueCount[u]);

            // Still far from goal → the next order MUST NOT dispatch (completion gates it).
            sys.Tick(w, Fixed.Zero);
            Assert.Equal((byte)1, w.OrderQueueCount[u]);
            Assert.Equal(Fixed.FromInt(10).Raw, w.CommandGoal[u].X.Raw);

            // Simulate arrival at (10,0,0), then tick → the SECOND order dispatches (goal (20,0,0), ring empty).
            w.Position[u] = V(10, 0, 0);
            sys.Tick(w, Fixed.Zero);
            Assert.Equal(Fixed.FromInt(20).Raw, w.CommandGoal[u].X.Raw);
            Assert.Equal((byte)0, w.OrderQueueCount[u]);
        }

        [Fact]
        public void OrderQueueSystem_Teeth_DoesNotAdvance_WhileActiveOrderIncomplete()
        {
            var w = new EntityWorld();
            var sys = new OrderQueueSystem();
            int u = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            OrderApplier.Apply(w, Queued(u, UnitCommand.Move, Fixed.FromInt(10), Fixed.Zero), Faction.Player1);
            OrderApplier.Apply(w, Queued(u, UnitCommand.Move, Fixed.FromInt(20), Fixed.Zero), Faction.Player1);

            sys.Tick(w, Fixed.Zero);                 // pops order 1 → Move to (10,0,0)
            Assert.Equal((byte)1, w.OrderQueueCount[u]);

            // The unit never arrives (stays at origin, goal far away). Many ticks → the queue must NOT advance.
            for (int t = 0; t < 50; t++) sys.Tick(w, Fixed.Zero);
            Assert.Equal((byte)1, w.OrderQueueCount[u]); // proves the completion predicate gates dispatch
        }

        [Fact]
        public void OrderQueueSystem_Teeth_TerminalOrder_StallsTheQueue()
        {
            // A queued Stop (terminal) becomes the persistent state and the remaining queue never pops (Decision #5).
            var w = new EntityWorld();
            var sys = new OrderQueueSystem();
            int u = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            OrderApplier.Apply(w, Queued(u, UnitCommand.Stop, Fixed.Zero, Fixed.Zero), Faction.Player1);
            OrderApplier.Apply(w, Queued(u, UnitCommand.Move, Fixed.FromInt(9), Fixed.Zero), Faction.Player1);

            sys.Tick(w, Fixed.Zero);                 // Idle complete → pop Stop → CommandState = Stop
            Assert.Equal(UnitCommand.Stop, w.CommandState[u]);
            Assert.Equal((byte)1, w.OrderQueueCount[u]);

            for (int t = 0; t < 20; t++) sys.Tick(w, Fixed.Zero);
            Assert.Equal((byte)1, w.OrderQueueCount[u]); // Stop never completes → the Move never pops
        }

        [Fact]
        public void OrderQueueSystem_CompletesMove_EvenWhenPresentationFlipsCommandStateToStop()
        {
            // R1 (Option B — sim-owned completion): the queue keys on the sim-authoritative ActiveOrderCmd, NOT the
            // presentation-mutable CommandState. In the live client a plain Move is flow-field-steered and FlowFieldBridge
            // (presentation) flips its CommandState Move→Stop at the WIDER 1.5u arrival — BEFORE the unit reaches the
            // queue's 0.5u pop. Simulating that flip, the queue MUST still complete the Move on the pure-sim arrival and
            // pop the next order. Pre-fix (keying on CommandState==Stop) this stranded the queue forever.
            var w = new EntityWorld();
            var sys = new OrderQueueSystem();
            int u = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));

            // Active plain Move to (10,0,0), then a queued Move to (20,0,0) behind it.
            OrderApplier.Apply(w, new UnitOrder(u, UnitCommand.Move, Fixed.FromInt(10), Fixed.Zero), Faction.Player1);
            OrderApplier.Apply(w, Queued(u, UnitCommand.Move, Fixed.FromInt(20), Fixed.Zero), Faction.Player1);
            Assert.Equal(UnitCommand.Move, w.CommandState[u]);
            Assert.Equal((byte)UnitCommand.Move, w.ActiveOrderCmd[u]); // dispatch stamped the sim-authoritative active order
            Assert.Equal((byte)1, w.OrderQueueCount[u]);

            // Presentation flips the arrived Move → Stop at 1.5u (the bug trigger). ActiveOrderCmd stays Move.
            w.CommandState[u] = UnitCommand.Stop;

            // Not yet at the goal → the queue must NOT advance (the Stop must not masquerade as completion, and the
            // terminal-Stop stall must NOT fire for a presentation-flipped Move — teeth on the "arrived" gate).
            sys.Tick(w, Fixed.Zero);
            Assert.Equal((byte)1, w.OrderQueueCount[u]);

            // The sim finishes the approach (MovementSystem hand-off). The queue completes on the pure-sim
            // SqrDistance(Position, CommandGoal) DESPITE CommandState==Stop, and pops the queued Move.
            w.Position[u] = V(10, 0, 0);
            sys.Tick(w, Fixed.Zero);
            Assert.Equal(Fixed.FromInt(20).Raw, w.CommandGoal[u].X.Raw); // queued Move dispatched
            Assert.Equal((byte)0, w.OrderQueueCount[u]);
        }

        // ── AC4 — full ring rejects deterministically + emits one OrderDenied ─────────────────────────────────

        [Fact]
        public void FullRing_RejectsTheNinthOrder_AndEmitsOneDenial()
        {
            var w = new EntityWorld();
            var events = new CombatEventQueue();
            int u = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));

            // Fill the ring to exactly MAX_ORDER_QUEUE (= 8).
            for (int k = 0; k < EntityWorld.MAX_ORDER_QUEUE; k++)
                OrderApplier.Apply(w, Queued(u, UnitCommand.Move, Fixed.FromInt(k), Fixed.Zero), Faction.Player1, events: events);
            Assert.Equal((byte)EntityWorld.MAX_ORDER_QUEUE, w.OrderQueueCount[u]);
            Assert.Equal(0, events.Count); // no denial while there was room

            // The 9th is rejected: count unchanged, no throw, exactly one OrderDenied event at the unit's position.
            OrderApplier.Apply(w, Queued(u, UnitCommand.Move, Fixed.FromInt(99), Fixed.Zero), Faction.Player1, events: events);
            Assert.Equal((byte)EntityWorld.MAX_ORDER_QUEUE, w.OrderQueueCount[u]);
            Assert.Equal(1, events.Count);
            Assert.Equal(CombatEventType.OrderDenied, events.Get(0).Type);
        }

        [Fact]
        public void FullRing_Rejects_DeterministicallyWithoutAnEventSink()
        {
            // The reject DECISION reads the folded OrderQueueCount, so a null event sink (replay) rejects identically.
            var w = new EntityWorld();
            int u = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            for (int k = 0; k < EntityWorld.MAX_ORDER_QUEUE + 3; k++)
                OrderApplier.Apply(w, Queued(u, UnitCommand.Move, Fixed.FromInt(k), Fixed.Zero), Faction.Player1); // events = null
            Assert.Equal((byte)EntityWorld.MAX_ORDER_QUEUE, w.OrderQueueCount[u]); // clamped at the cap, no overflow/crash
        }

        // ── AC3 — SetRally rides the wire: store write + faction anti-cheat ────────────────────────────────────

        [Fact]
        public void SetRally_OnTheWire_WritesTheStore_AfterTheFactionGuard()
        {
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            var buildSys  = new BuildingSystem(buildings, resources);
            int b = buildSys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, V(10, 0, -10), preBuilt: true);
            Assert.False(buildings.HasRallyPoint[b]);

            // A correctly-owned SetRally writes the rally point (UnitId = the BUILDING id, targets = Fixed raw).
            OrderApplier.Apply(new EntityWorld(), new UnitOrder(b, UnitCommand.SetRally, Fixed.FromInt(16), Fixed.FromInt(-4)),
                Faction.Player1, buildings: buildSys);
            Assert.True(buildings.HasRallyPoint[b]);
            Assert.Equal(Fixed.FromInt(16).Raw, buildings.RallyPoint[b].X.Raw);
            Assert.Equal(Fixed.FromInt(-4).Raw, buildings.RallyPoint[b].Z.Raw);
        }

        [Fact]
        public void SetRally_WrongFaction_IsRejected_AntiCheat()
        {
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            var buildSys  = new BuildingSystem(buildings, resources);
            int b = buildSys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, V(10, 0, -10), preBuilt: true);

            // Player2 tries to rally Player1's building → rejected, no store write.
            OrderApplier.Apply(new EntityWorld(), new UnitOrder(b, UnitCommand.SetRally, Fixed.FromInt(3), Fixed.FromInt(3)),
                Faction.Player2, buildings: buildSys);
            Assert.False(buildings.HasRallyPoint[b]);

            // The building's own faction succeeds (proves the guard, not a blanket refusal).
            Assert.True(buildSys.SetRallyCommand(b, Faction.Player1, Fixed.FromInt(3), Fixed.FromInt(3)));
            Assert.True(buildings.HasRallyPoint[b]);
        }
    }
}
