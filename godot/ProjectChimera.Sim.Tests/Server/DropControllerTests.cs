#nullable enable
using System.Linq;
using ProjectChimera.Multiplayer.Server;
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// Story 9.6 — the ACK-gated freeze state machine. Proves: a drop sets one pending directive; the freeze
    /// commits ONLY when every survivor ACKs the same (droppedSlot, applyAtTick); stale/mismatched/non-survivor ACKs
    /// are ignored; commit marks the slot frozen (in FrozenSlots, with its applyTick) and clears pending so a later
    /// drop can be issued; and the guards (already-dropped / directive-pending / out-of-range) hold.
    /// </summary>
    public class DropControllerTests
    {
        [Fact]
        public void NotifyDrop_SetsPendingDirective()
        {
            var c = new DropController(2);
            Assert.False(c.DirectivePending);

            Assert.True(c.NotifyDrop(1, applyAtTick: 42u, survivorSlots: new[] { 0 }));
            Assert.True(c.DirectivePending);
            Assert.Equal(1, c.PendingDroppedSlot);
            Assert.Equal(42u, c.PendingApplyTick);
            Assert.False(c.IsFrozen(1));
        }

        [Fact]
        public void Commit_OnlyAfterEverySurvivorAcks_MarksFrozen()
        {
            var c = new DropController(2);
            c.NotifyDrop(1, 42u, new[] { 0 });

            Assert.False(c.AllAcked());   // no ACK yet
            Assert.False(c.Commit());     // cannot commit un-ACKed

            c.RecordAck(survivorSlot: 0, droppedSlot: 1, applyAtTick: 42u);
            Assert.True(c.AllAcked());
            Assert.True(c.Commit());

            Assert.True(c.IsFrozen(1));
            Assert.Equal(42u, c.FrozenApplyTick(1));
            Assert.Equal(new[] { 1 }, c.FrozenSlots.ToArray());
            Assert.False(c.DirectivePending); // pending cleared → next directive may issue
            Assert.Equal(-1, c.PendingDroppedSlot);
        }

        [Fact]
        public void RecordAck_IgnoresStaleAndMismatchedAndNonSurvivor()
        {
            var c = new DropController(3);
            c.NotifyDrop(2, 100u, new[] { 0, 1 });

            c.RecordAck(0, droppedSlot: 2, applyAtTick: 999u); // wrong applyAtTick → ignored
            c.RecordAck(0, droppedSlot: 1, applyAtTick: 100u); // wrong droppedSlot → ignored
            c.RecordAck(2, droppedSlot: 2, applyAtTick: 100u); // slot 2 is the DROPPED slot, not a survivor → ignored
            Assert.False(c.AllAcked());

            c.RecordAck(0, 2, 100u);
            Assert.False(c.AllAcked()); // still waiting on survivor 1
            c.RecordAck(1, 2, 100u);
            Assert.True(c.AllAcked());
        }

        [Fact]
        public void NotifyDrop_RejectsSecondPending_AndAlreadyDropped()
        {
            var c = new DropController(3);
            Assert.True(c.NotifyDrop(2, 10u, new[] { 0, 1 }));
            Assert.False(c.NotifyDrop(1, 20u, new[] { 0 })); // a directive is already pending → rejected

            c.RecordAck(0, 2, 10u);
            c.RecordAck(1, 2, 10u);
            Assert.True(c.Commit());

            Assert.False(c.NotifyDrop(2, 30u, new[] { 0, 1 })); // slot 2 already dropped → rejected
            Assert.True(c.NotifyDrop(1, 30u, new[] { 0 }));     // a DIFFERENT slot after commit → accepted
        }

        [Fact]
        public void NotifyDrop_OutOfRangeSlot_Rejected()
        {
            var c = new DropController(2);
            Assert.False(c.NotifyDrop(5, 1u, new[] { 0 }));
            Assert.False(c.NotifyDrop(-1, 1u, new[] { 0 }));
            Assert.False(c.DirectivePending);
        }

        [Fact]
        public void AllAcked_FalseWhenNoSurvivors()
        {
            // A survivor-less drop must never auto-commit (the adapter ends the match instead).
            var c = new DropController(2);
            c.NotifyDrop(1, 5u, survivorSlots: new int[0]);
            Assert.False(c.AllAcked());
            Assert.False(c.Commit());
        }

        // ── DW-409: RemoveSurvivor — reconciling the pending ACK set with a later disconnect ────────────────────

        [Fact]
        public void RemoveSurvivor_PrunesPendingAckSet_SoRemainingAcksComplete()
        {
            // N=3: slot 2 drops, survivors {0,1}. Slot 0 ACKs; slot 1 then DISCONNECTS before ACKing. Pre-fix,
            // slot 1 stayed in the recorded survivor set forever, AllAcked() could never return true, the freeze
            // never committed, and the merge fan-in stalled permanently (the DW-409 deadlock). RemoveSurvivor must
            // prune it so the remaining survivor's ACK completes the directive.
            var c = new DropController(3);
            c.NotifyDrop(2, 100u, new[] { 0, 1 });
            c.RecordAck(0, 2, 100u);
            Assert.False(c.AllAcked());              // still waiting on survivor 1

            Assert.True(c.RemoveSurvivor(1));        // survivor 1 disconnected — the set changed
            Assert.True(c.AllAcked());               // the remaining survivor (0) has already ACKed
            Assert.True(c.Commit());
            Assert.True(c.IsFrozen(2));
        }

        [Fact]
        public void RemoveSurvivor_ClearsTheAckToo_SoTheOtherSurvivorStillGates()
        {
            // The pruned survivor's ACK must not linger: with only slot 1's ACK recorded, pruning slot 1 leaves
            // slot 0 un-ACKed — the directive must still wait on slot 0 (never commit off a ghost ACK).
            var c = new DropController(3);
            c.NotifyDrop(2, 100u, new[] { 0, 1 });
            c.RecordAck(1, 2, 100u);

            Assert.True(c.RemoveSurvivor(1));
            Assert.False(c.AllAcked());              // slot 0 has not ACKed yet
            c.RecordAck(0, 2, 100u);
            Assert.True(c.AllAcked());
        }

        [Fact]
        public void RemoveSurvivor_NoPending_OutOfRange_NonSurvivor_AreNoOps()
        {
            var c = new DropController(3);
            Assert.False(c.RemoveSurvivor(0));       // nothing pending

            c.NotifyDrop(2, 10u, new[] { 0, 1 });
            Assert.False(c.RemoveSurvivor(5));       // out of range
            Assert.False(c.RemoveSurvivor(-1));      // out of range
            Assert.False(c.RemoveSurvivor(2));       // the pending DROPPED slot is not a survivor
            Assert.True(c.DirectivePending);

            Assert.True(c.RemoveSurvivor(1));
            Assert.False(c.RemoveSurvivor(1));       // idempotent — already pruned
        }

        [Fact]
        public void RemoveSurvivor_EmptyingTheSet_NeverAutoCommits()
        {
            // If EVERY survivor disconnects the pending directive must stay un-committable (AllAcked false) — the
            // adapter's match-over path owns that state; a survivor-less freeze commit would be meaningless.
            var c = new DropController(2);
            c.NotifyDrop(1, 5u, new[] { 0 });
            Assert.True(c.RemoveSurvivor(0));
            Assert.False(c.AllAcked());
            Assert.False(c.Commit());
            Assert.True(c.DirectivePending);         // parked; the adapter ends the match instead
        }
    }
}
