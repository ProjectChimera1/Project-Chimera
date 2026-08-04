#nullable enable
namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// DW-464 — the Godot-free guaranteed-delivery buffer for an ONLINE Concede/surrender, extracted from
    /// <c>LockstepManager</c>'s enqueue/send path (the <c>DelayMath</c>/<c>HandshakeGate</c>/<c>DslEventRateLimit</c>
    /// precedent: pure decision logic lifted into a single Godot-free file included by <c>SimSources.props</c> so it
    /// is Tier-1 unit-testable).
    ///
    /// A Concede is a rare, HIGH-INTENT order: unlike a droppable Move spam-click it must never be lost on a busy
    /// tick. The old online path pushed it straight into the pending-order batch and SILENTLY dropped it when the
    /// batch was already full (<c>_pendingCount &gt;= TickCommandPacket.MAX_ORDERS</c>). This buffer replaces that
    /// with a sticky queued flag the send path drains:
    ///   • <see cref="Queue"/> latches the intent (idempotent — a double confirm never claims two slots).
    ///   • <see cref="TryClaimSlot"/> is asked once per send; it claims a batch slot iff one is free, otherwise the
    ///     flag SURVIVES to retry on the next send (the batch always drains to zero between sends, so the retry
    ///     lands one tick later at the latest).
    ///   • <see cref="OrderBudget"/> reserves the LAST batch slot while a concede is queued, so a client spamming
    ///     normal orders every inter-send window can never starve the concede out of the stream.
    ///
    /// Client-SEND-side only: this shapes what the local client SUBMITS to the server, never how the authoritative
    /// merged stream is applied — nothing here is folded into SimChecksum and no golden moves.
    /// </summary>
    public sealed class ConcedeBuffer
    {
        /// <summary>True while a confirmed Concede is queued and has not yet claimed a batch slot.</summary>
        public bool IsPending { get; private set; }

        /// <summary>Latch a confirmed Concede for the next send with a free batch slot. Idempotent.</summary>
        public void Queue() => IsPending = true;

        /// <summary>Clear any queued Concede (match start / teardown — a new session must never inherit a stale
        /// surrender from a prior match).</summary>
        public void Reset() => IsPending = false;

        /// <summary>The order-batch budget NORMAL enqueues may fill: while a Concede is queued the last slot is
        /// reserved for it (<paramref name="maxOrders"/> − 1), otherwise the full packet budget.</summary>
        public int OrderBudget(int maxOrders) => IsPending ? maxOrders - 1 : maxOrders;

        /// <summary>
        /// Called once per send: claim a batch slot for the queued Concede when the batch currently holds
        /// <paramref name="pendingOrderCount"/> of <paramref name="maxOrders"/> orders. True → the caller appends
        /// the Concede order and the queued flag clears; false → nothing is queued, or the batch is full and the
        /// flag survives to retry on the next send.
        /// </summary>
        public bool TryClaimSlot(int pendingOrderCount, int maxOrders)
        {
            if (!IsPending || pendingOrderCount >= maxOrders) return false;
            IsPending = false;
            return true;
        }
    }
}
