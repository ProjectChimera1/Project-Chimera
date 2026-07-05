using ProjectChimera.Multiplayer; // OrderApplier.ApplyActiveOrder — the SINGLE command→CommandState dispatch (Decision #4)

namespace ProjectChimera.Core
{
    /// <summary>
    /// Story 2.12 (FR-74, AC1) — advances each unit's shift-queued order ring. Every tick, for each alive entity
    /// with pending orders, if its ACTIVE order has COMPLETED it pops the head of the ring and dispatches it through
    /// the shared <see cref="OrderApplier.ApplyActiveOrder"/> core (no second command→state path — the 1.12 rule).
    ///
    /// Registered at tick index 3 (immediately AFTER <see cref="ProjectChimera.Navigation.MovementSystem"/> so arrival
    /// is fresh THIS tick, and BEFORE <c>AbilityCastSystem</c> so a popped <see cref="UnitCommand.CastAbility"/> fires
    /// the same tick). Pure sim: <see cref="Fixed"/> math, ascending-id iteration, allocation-free — no
    /// <c>float</c>/<c>Mathf</c>/<c>System.Random</c>/wall-clock/<c>Dictionary</c> enumeration.
    ///
    /// <para><b>The completion signal is PURE SIM (AC1.4 / Decision #3).</b> It NEVER reads the presentation
    /// <c>Move→Stop</c> transition (<c>PathRequestSystem</c>/<c>FlowFieldBridge</c>, both <c>src/UI</c>) nor
    /// <see cref="EntityFlags.Moving"/> (also presentation-written), which would diverge headless-golden vs live-client.
    /// For a movement order it is the queue's OWN <c>SqrDistance(Position, CommandGoal) &lt;= ORDER_ARRIVE_SQR</c> test
    /// (mirroring <c>CombatSystem.ResumeAttackMove</c> / <c>BuildingSystem.ClearWorkerBuild</c>); an <see cref="UnitCommand.Idle"/>
    /// state (the resting / target-loss state the sim itself writes) is "ready for the next order"; every other active
    /// state STALLS the queue (Decision #5 — a queued Stop/Hold/Patrol/Follow simply becomes the persistent state and
    /// the remaining queue never pops, WC3-consistent). An <see cref="UnitCommand.AttackTarget"/>/<see cref="UnitCommand.AttackBuilding"/>
    /// completes when combat flips it to Idle on target-loss (one tick later, since this runs before CombatSystem).</para>
    /// </summary>
    public sealed class OrderQueueSystem : ISimSystem
    {
        /// <summary>
        /// Squared arrive threshold for a queued MOVEMENT order's completion. Story 2.13 (AC2, D-1): widened from 0.5u
        /// to the shared 2u goal-arrival radius (single-sourced with <c>CombatSystem.AMOVE_ARRIVE_SQR</c> via
        /// <see cref="ArrivalTuning.GoalArriveRadiusSqr"/> so they cannot drift), so a queued Move behind a wave — or a
        /// wave of queued Moves — completes at the ~1.0u equilibrium ring instead of stranding on the old 0.5u radius
        /// (INSIDE the ring). This gates on distance to the sim-owned CommandGoal, so it does NOT depend on
        /// MovementSystem's physical stop (which stays 0.5u to preserve melee — see <see cref="ArrivalTuning"/>): the
        /// unit passes through the 2u zone on its approach and the pop fires there. Built from <see cref="Fixed.FromInt"/>
        /// (exact 16.16), no <c>Fixed.FromFloat</c> in this tick-reachable file (CHM0005-clean).
        /// </summary>
        private static readonly Fixed ORDER_ARRIVE_SQR = ArrivalTuning.GoalArriveRadiusSqr;

        public void Tick(EntityWorld world, Fixed dt)
        {
            int count = world.HighWaterMark;
            for (int i = 0; i < count; i++)
            {
                if (!world.IsAlive(i)) continue;
                if (world.OrderQueueCount[i] == 0) continue;   // no pending orders → nothing to advance
                if (!CurrentOrderComplete(world, i)) continue; // active order still running → keep waiting

                PopAndDispatchHead(world, i);
            }
        }

        /// <summary>
        /// True when entity <paramref name="i"/>'s ACTIVE order has finished, so the queue's head may dispatch. Keyed on
        /// the sim-authoritative <see cref="EntityWorld.ActiveOrderCmd"/> (NOT the presentation-mutable CommandState — R1):
        /// Idle = ready; Move/AttackMove = arrived within <see cref="ORDER_ARRIVE_SQR"/> of <see cref="EntityWorld.CommandGoal"/>;
        /// a tracking order (AttackTarget/AttackBuilding/Follow) = the sim wrote CommandState=Idle on target-loss;
        /// everything else (Stop/Hold/Patrol) stalls (Decision #5).
        /// </summary>
        private static bool CurrentOrderComplete(EntityWorld world, int i)
        {
            // Key on ActiveOrderCmd, NOT CommandState: presentation (FlowFieldBridge/PathRequestSystem, src/UI) flips an
            // arrived Move→Stop at the flow field's wider 1.5u radius BEFORE the unit reaches this 0.5u pop, so reading
            // CommandState would strand every order queued behind a plain Move (R1). ActiveOrderCmd is written only by
            // the deterministic OrderApplier dispatch, so it survives the presentation flip.
            switch ((UnitCommand)world.ActiveOrderCmd[i])
            {
                case UnitCommand.Idle:
                    return true; // resting → dispatch the next queued order
                case UnitCommand.Move:
                case UnitCommand.AttackMove:
                    // Pure-sim arrival: Position vs the sim-owned CommandGoal (presentation-immune). MovementSystem
                    // drives the unit into ORDER_ARRIVE_SQR even after the presentation Stop-flip (FlowFieldBridge hands
                    // the last stretch to the sim while orders are queued), so a movement order reliably completes.
                    return FixedVec3.SqrDistance(world.Position[i], world.CommandGoal[i]) <= ORDER_ARRIVE_SQR;
                case UnitCommand.AttackTarget:
                case UnitCommand.AttackBuilding:
                case UnitCommand.Follow:
                    // A tracking order completes when the sim (CombatSystem) writes CommandState=Idle on target-loss.
                    // That transition is sim-owned (presentation never flips a tracking order), so it is safe to read.
                    return world.CommandState[i] == UnitCommand.Idle;
                default:
                    // Stop / HoldPosition / Patrol / Build: terminal or looping — the queue STALLS past them (Decision #5).
                    // (CastAbility never lands here: ApplyActiveOrder restores CommandState=prior for a transient cast,
                    // so ActiveOrderCmd reflects the prior order, not the cast.)
                    return false;
            }
        }

        /// <summary>
        /// Pop the head order (shift the remaining orders down by one, decrement the count) and dispatch it through the
        /// shared <see cref="OrderApplier.ApplyActiveOrder"/> — NOT the flag-wrapping <see cref="OrderApplier.Apply"/>,
        /// which would re-clear the ring. Allocation-free; slots past the new count stay stale but are unread/unhashed
        /// (the count-driven fold discipline). Reads the head BEFORE dispatch so a popped order can safely rewrite state.
        /// </summary>
        private static void PopAndDispatchHead(EntityWorld world, int i)
        {
            int baseIdx = i * EntityWorld.MAX_ORDER_QUEUE;
            int count   = world.OrderQueueCount[i];

            byte cmdByte = world.OrderQueueCmd[baseIdx];
            int  tx      = world.OrderQueueTargetX[baseIdx];
            int  tz      = world.OrderQueueTargetZ[baseIdx];

            for (int s = 1; s < count; s++)
            {
                world.OrderQueueCmd[baseIdx + s - 1]     = world.OrderQueueCmd[baseIdx + s];
                world.OrderQueueTargetX[baseIdx + s - 1] = world.OrderQueueTargetX[baseIdx + s];
                world.OrderQueueTargetZ[baseIdx + s - 1] = world.OrderQueueTargetZ[baseIdx + s];
            }
            world.OrderQueueCount[i] = (byte)(count - 1);

            // The stored command is already the masked 0-13 value (AppendOrder strips the 0x80 flag), so it is a valid
            // UnitCommand. Dispatch through the shared active-order core — single-sourced with the wire-entry apply.
            OrderApplier.ApplyActiveOrder(world, i, (UnitCommand)cmdByte, tx, tz);
        }
    }
}
