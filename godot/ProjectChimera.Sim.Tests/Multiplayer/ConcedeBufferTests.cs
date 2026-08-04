#nullable enable
using ProjectChimera.Multiplayer;
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>
    /// DW-464 — the guaranteed-delivery online-Concede policy (<see cref="ConcedeBuffer"/>), extracted from
    /// <c>LockstepManager</c>'s enqueue/send path so it is Tier-1 testable (the DelayMath/HandshakeGate/
    /// DslEventRateLimit precedent).
    ///
    /// REGRESSION PINNED: the old online path pushed the Concede straight into the pending-order batch and
    /// SILENTLY DROPPED it when the batch was already full (<c>_pendingCount &gt;= TickCommandPacket.MAX_ORDERS</c>)
    /// — a rare, high-intent surrender lost forever on one busy tick, with no retry and no feedback. The buffer
    /// makes the intent sticky: a full-batch send leaves it queued for the next send (the batch drains to zero
    /// between sends), and while queued the last batch slot is reserved so normal-order spam can never starve it.
    ///
    /// Client-SEND-side only — nothing here is folded into SimChecksum, so no golden is involved.
    /// </summary>
    public class ConcedeBufferTests
    {
        private const int MaxOrders = TickCommandPacket.MAX_ORDERS; // 32 — the real packet budget the manager passes

        [Fact]
        public void FullBatch_DoesNotDropTheConcede_ItClaimsOnTheNextSend()
        {
            // THE DW-464 regression: concede queued while this tick's batch is already full.
            var buf = new ConcedeBuffer();
            buf.Queue();

            // Send 1 — batch full: no slot claimed, and (unlike the old code) the intent SURVIVES.
            Assert.False(buf.TryClaimSlot(MaxOrders, MaxOrders));
            Assert.True(buf.IsPending);

            // Send 2 — the batch drained to zero between sends: the retry claims a slot and clears.
            Assert.True(buf.TryClaimSlot(0, MaxOrders));
            Assert.False(buf.IsPending);
        }

        [Fact]
        public void FreeSlot_ClaimsOnTheSameSend_AndClears()
        {
            // The common case: room in this tick's batch → the concede ships immediately (no added latency).
            var buf = new ConcedeBuffer();
            buf.Queue();

            Assert.True(buf.TryClaimSlot(MaxOrders - 1, MaxOrders)); // exactly one slot left → it is the concede's
            Assert.False(buf.IsPending);
        }

        [Fact]
        public void OrderBudget_ReservesTheLastSlot_OnlyWhileQueued()
        {
            var buf = new ConcedeBuffer();

            // Idle: normal orders get the full packet budget (unchanged behavior).
            Assert.Equal(MaxOrders, buf.OrderBudget(MaxOrders));

            // Queued: one slot is held back for the concede.
            buf.Queue();
            Assert.Equal(MaxOrders - 1, buf.OrderBudget(MaxOrders));

            // Claimed: the reservation lifts again.
            Assert.True(buf.TryClaimSlot(0, MaxOrders));
            Assert.Equal(MaxOrders, buf.OrderBudget(MaxOrders));
        }

        [Fact]
        public void ReservedBudget_GuaranteesTheClaim_EvenUnderPerTickOrderSpam()
        {
            // The starvation proof: every inter-send window the client fills the batch to the enqueue budget the
            // manager grants it (OrderBudget). While the concede is queued that budget is MAX-1, so at every send
            // the claim slot is free — the surrender can never be starved out of the stream indefinitely.
            var buf = new ConcedeBuffer();
            buf.Queue();

            int filled = buf.OrderBudget(MaxOrders); // a full tick of spam admitted by the reserving budget
            Assert.True(buf.TryClaimSlot(filled, MaxOrders));
            Assert.False(buf.IsPending);
        }

        [Fact]
        public void Queue_IsIdempotent_ADoubleConfirmClaimsExactlyOneSlot()
        {
            var buf = new ConcedeBuffer();
            buf.Queue();
            buf.Queue(); // a second confirmed concede (same match) must not claim a second slot

            Assert.True(buf.TryClaimSlot(0, MaxOrders));
            Assert.False(buf.TryClaimSlot(0, MaxOrders)); // nothing left to claim
        }

        [Fact]
        public void NothingQueued_NeverClaims_AndNeverReserves()
        {
            var buf = new ConcedeBuffer();

            Assert.False(buf.IsPending);
            Assert.False(buf.TryClaimSlot(0, MaxOrders));
            Assert.False(buf.TryClaimSlot(MaxOrders, MaxOrders));
            Assert.Equal(MaxOrders, buf.OrderBudget(MaxOrders));
        }

        [Fact]
        public void Reset_DropsAQueuedConcede_ForANewSession()
        {
            // GoOnline/GoSpectate/GoOffline reset the buffer: a new session never inherits a stale surrender.
            var buf = new ConcedeBuffer();
            buf.Queue();
            buf.Reset();

            Assert.False(buf.IsPending);
            Assert.False(buf.TryClaimSlot(0, MaxOrders));
        }
    }
}
