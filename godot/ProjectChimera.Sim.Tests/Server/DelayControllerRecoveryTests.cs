#nullable enable
using System;
using ProjectChimera.Multiplayer;         // DelayMath (MIN/MAX_DELAY)
using ProjectChimera.Multiplayer.Server;   // DelayController
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// DW-397 / DW-400 — the delay authority's slot-map awareness and its mid-match recovery: RTT/ACK state is
    /// indexed by TRANSPORT SLOT over an explicit active mask (a player at a high slot id is tracked, not silently
    /// dropped), a dropped-and-frozen slot is excused from the ACK quorum (<see cref="DelayController.DeactivateSlot"/> /
    /// <see cref="DelayController.TryFinalizePending"/>), and a wedged ACK from a still-connected client is bounded
    /// by the frontier-tick timeout (<see cref="DelayController.CheckAckTimeout"/>) instead of silently disabling
    /// adaptive delay for the rest of the match.
    /// </summary>
    public class DelayControllerRecoveryTests
    {
        private const int InitialDelay = 4;  // LockstepManager.INPUT_DELAY

        // ── DW-397: a non-contiguous player-slot map indexes RTT + ACKs by transport slot ──

        [Fact]
        public void SparseSlotMap_HighSlotPlayer_RttAndAckAreTracked()
        {
            // Two players on slots {0, 2} (a Story 9.15-shaped open-slot layout). The old dense controller
            // rejected slot 2 outright ((uint)slot >= Expected): its RTT never folded and its ACK never counted,
            // so AllAcked could never be true — the delay-commit wedge this pins against.
            var c = new DelayController(new[] { 0, 2 }, slotCapacity: 8, initialDelay: InitialDelay);
            Assert.Equal(2, c.Expected);
            Assert.Equal(2, c.ActiveCount);
            Assert.True(c.IsActiveSlot(0));
            Assert.False(c.IsActiveSlot(1)); // unoccupied middle slot is NOT a player
            Assert.True(c.IsActiveSlot(2));

            c.RecordRtt(2, 1000f); // the high-slot player's RTT must drive the dictate
            Assert.True(c.TryComputeDirective(100u, 0u, out int delay, out uint at));
            Assert.Equal(DelayMath.MAX_DELAY, delay);

            c.RecordAck(0, delay, at);
            Assert.False(c.AllAcked(delay, at)); // slot 2's ACK still outstanding
            c.RecordAck(2, delay, at);           // the high-slot ACK must count
            Assert.True(c.AllAcked(delay, at));
            Assert.True(c.Commit(delay, at));
            Assert.Equal(delay, c.LastCommittedDelay);
        }

        [Fact]
        public void SparseSlotMap_UnoccupiedSlot_NeverFoldsRttNorAcks()
        {
            var c = new DelayController(new[] { 0, 2 }, slotCapacity: 8, initialDelay: InitialDelay);

            c.RecordRtt(1, 1000f); // slot 1 is not a player in this match — must not fold
            Assert.False(c.TryComputeDirective(100u, 0u, out _, out _));

            c.RecordRtt(2, 1000f);
            Assert.True(c.TryComputeDirective(100u, 0u, out int delay, out uint at));
            c.RecordAck(0, delay, at);
            c.RecordAck(1, delay, at); // an unoccupied slot's ACK must not complete the quorum
            Assert.False(c.AllAcked(delay, at));
        }

        [Fact]
        public void SlotMapConstructor_RejectsEmptyDuplicateAndOutOfRange()
        {
            Assert.Throws<ArgumentException>(() =>
                new DelayController(new int[0], slotCapacity: 8, initialDelay: InitialDelay));
            Assert.Throws<ArgumentException>(() =>
                new DelayController(new[] { 0, 0 }, slotCapacity: 8, initialDelay: InitialDelay));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new DelayController(new[] { 0, 8 }, slotCapacity: 8, initialDelay: InitialDelay));
        }

        // ── DW-400: a dropped (frozen) slot is excused — a pending directive finalizes on survivor ACKs ──

        [Fact]
        public void DroppedSlot_IsExcused_AndThePendingDirectiveFinalizesOnSurvivorAcks()
        {
            var c = new DelayController(2, InitialDelay);
            c.RecordRtt(0, 1000f);
            Assert.True(c.TryComputeDirective(10u, 0u, out int delay, out uint at));
            c.RecordAck(0, delay, at);
            Assert.False(c.AllAcked(delay, at)); // slot 1 never ACKs (it is dropping)

            // Slot 1's freeze commits (Story 9.6): the delay authority excuses it. Without this the pending
            // directive could never complete — _pending wedged forever, adaptive delay dead for the match.
            c.DeactivateSlot(1);
            Assert.Equal(1, c.ActiveCount);
            Assert.False(c.IsActiveSlot(1));

            Assert.True(c.TryFinalizePending(out int fDelay, out uint fApply));
            Assert.Equal(delay, fDelay);
            Assert.Equal(at, fApply);
            Assert.False(c.DirectivePending);
            Assert.Equal(delay, c.LastCommittedDelay);
            Assert.True(c.AwaitingMaturity); // the commit still arms the maturity gate

            Assert.False(c.TryFinalizePending(out _, out _)); // idempotent — nothing pending anymore
        }

        [Fact]
        public void TryFinalizePending_WithAckStillOutstanding_DoesNothing()
        {
            var c = new DelayController(2, InitialDelay);
            c.RecordRtt(0, 1000f);
            Assert.True(c.TryComputeDirective(10u, 0u, out int delay, out uint at));
            c.RecordAck(0, delay, at); // slot 1 outstanding, still ACTIVE

            Assert.False(c.TryFinalizePending(out _, out _));
            Assert.True(c.DirectivePending);
            Assert.Equal(InitialDelay, c.LastCommittedDelay);
        }

        [Fact]
        public void DeactivatedSlots_StaleRtt_NoLongerDrivesTheDictate()
        {
            var c = new DelayController(2, InitialDelay);
            c.RecordRtt(0, 200f);  // target 4 == initial → alone dictates nothing
            c.RecordRtt(1, 1000f); // the laggard would drive MAX_DELAY

            c.DeactivateSlot(1);   // the laggard dropped and froze

            // Its stale high RTT must stop pinning the whole match's delay: with only slot 0's 200 ms sample the
            // target equals the committed baseline → no directive. (Without deactivation this emits MAX_DELAY.)
            Assert.False(c.TryComputeDirective(100u, 0u, out _, out _));
        }

        [Fact]
        public void AllSlotsDeactivated_NothingFinalizesOrDictates()
        {
            var c = new DelayController(2, InitialDelay);
            c.RecordRtt(0, 1000f);
            Assert.True(c.TryComputeDirective(10u, 0u, out int delay, out uint at));
            c.RecordAck(0, delay, at);
            c.RecordAck(1, delay, at);

            c.DeactivateSlot(0);
            c.DeactivateSlot(1);
            Assert.Equal(0, c.ActiveCount);

            // A survivor-less match commits nothing (AllAcked over zero actives is false, not vacuously true).
            Assert.False(c.AllAcked(delay, at));
            Assert.False(c.TryFinalizePending(out _, out _));
            Assert.False(c.TryComputeDirective(100u, 0u, out _, out _));
        }

        [Fact]
        public void DeactivateSlot_UnknownOrRepeated_IsIgnored()
        {
            var c = new DelayController(2, InitialDelay);
            c.DeactivateSlot(7);   // out of the player map
            c.DeactivateSlot(-1);  // defensive
            Assert.Equal(2, c.ActiveCount);
            c.DeactivateSlot(1);
            c.DeactivateSlot(1);   // repeated — must not double-decrement
            Assert.Equal(1, c.ActiveCount);
        }

        // ── DW-400: the frontier-tick ACK timeout bounds a wedged ACK from a still-connected client ──

        [Fact]
        public void AckTimeout_ForceCommitsThePendingDirective_AfterTheDeadline()
        {
            var c = new DelayController(2, InitialDelay);
            c.RecordRtt(0, 1000f);
            Assert.True(c.TryComputeDirective(100u, 0u, out int delay, out uint at));
            c.RecordAck(0, delay, at); // slot 1 stays connected but never ACKs (the fail-silent stall)

            // One tick before the deadline: still pending.
            Assert.False(c.CheckAckTimeout(100u + (uint)DelayController.ACK_TIMEOUT_TICKS - 1u, out _, out _));
            Assert.True(c.DirectivePending);

            // At the deadline: force-commit (reliable transport delivered + scheduled the directive on every
            // still-connected client; the ACK is bookkeeping). Without this the directive pends forever and
            // adaptive delay is silently disabled for the rest of the match.
            Assert.True(c.CheckAckTimeout(100u + (uint)DelayController.ACK_TIMEOUT_TICKS, out int tDelay, out uint tApply));
            Assert.Equal(delay, tDelay);
            Assert.Equal(at, tApply);
            Assert.False(c.DirectivePending);
            Assert.Equal(delay, c.LastCommittedDelay);
            Assert.True(c.AwaitingMaturity); // the next directive is STILL withheld until this one provably applied

            Assert.False(c.CheckAckTimeout(100u + 2u * (uint)DelayController.ACK_TIMEOUT_TICKS, out _, out _)); // nothing pending
        }

        [Fact]
        public void AckTimeout_WithNothingPending_IsANoOp()
        {
            var c = new DelayController(2, InitialDelay);
            Assert.False(c.CheckAckTimeout(1_000_000u, out _, out _));
            Assert.Equal(InitialDelay, c.LastCommittedDelay);
        }

        [Fact]
        public void AckTimeout_MeasuresFromTheIssueFrontier_NotFromZero()
        {
            var c = new DelayController(2, InitialDelay);
            c.RecordRtt(0, 1000f);
            uint issuedAt = 5_000u;
            Assert.True(c.TryComputeDirective(issuedAt, 0u, out _, out _));

            // A frontier ≥ ACK_TIMEOUT_TICKS in absolute terms but < issuedAt + ACK_TIMEOUT_TICKS must not fire.
            Assert.False(c.CheckAckTimeout(issuedAt + 1u, out _, out _));
            Assert.True(c.CheckAckTimeout(issuedAt + (uint)DelayController.ACK_TIMEOUT_TICKS, out _, out _));
        }
    }
}
