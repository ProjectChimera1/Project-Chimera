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
        /// Squared arrive threshold for a queued MOVEMENT order's completion (0.5 world units, so 0.25 squared) —
        /// equal to <c>CombatSystem.AMOVE_ARRIVE_SQR</c> and <c>MovementSystem.ARRIVE_THRESHOLD_SQR</c>, so a unit that
        /// MovementSystem has stopped at its goal reliably satisfies this the same tick. Built from
        /// <see cref="Fixed.Half"/> (the exact 16.16 half — byte-identical to <c>FromFloat(0.5f)*FromFloat(0.5f)</c>)
        /// so no <c>Fixed.FromFloat</c> appears in this tick-reachable sim file (CHM0005-clean). Named constant, not a
        /// bare literal (CHM0004-clean).
        /// </summary>
        private static readonly Fixed ORDER_ARRIVE_SQR = Fixed.Half * Fixed.Half;

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
        /// True when entity <paramref name="i"/>'s ACTIVE order has finished, so the queue's head may dispatch. Keyed
        /// on <see cref="EntityWorld.CommandState"/> only (pure sim): Idle = ready; Move/AttackMove = arrived within
        /// <see cref="ORDER_ARRIVE_SQR"/> of <see cref="EntityWorld.CommandGoal"/>; everything else stalls (Decision #5).
        /// </summary>
        private static bool CurrentOrderComplete(EntityWorld world, int i)
        {
            switch (world.CommandState[i])
            {
                case UnitCommand.Idle:
                    return true; // resting / target-loss → dispatch the next queued order
                case UnitCommand.Move:
                case UnitCommand.AttackMove:
                    return FixedVec3.SqrDistance(world.Position[i], world.CommandGoal[i]) <= ORDER_ARRIVE_SQR;
                default:
                    // Stop / HoldPosition / Patrol / Follow / AttackTarget / AttackBuilding / Build / CastAbility:
                    // terminal, looping, tracking, or transient — the queue does not advance past them (they complete
                    // by flipping CommandState to Idle when the sim itself decides, which the Idle case above catches).
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
