#nullable enable
namespace ProjectChimera.Dsl
{
    /// <summary>
    /// Story 7.9 — the Godot-free per-tick admission decision for a button-originated <c>DslEvent</c> raise, extracted
    /// from <c>LockstepManager.EnqueueDslEvent</c> so it is executable in the Tier-1 test set (the
    /// <c>DelayMath</c>/<c>HandshakeGate</c> precedent: pure logic lifted out of a Godot-coupled class into a
    /// Godot-free single file globbed into <c>SimSources.props</c>).
    ///
    /// A raise is admitted only while BOTH bounds hold: this tick's buffered DslEvent count is under
    /// <see cref="EventBounds.MaxDslEventsPerTick"/> (the per-player anti-spam cap) AND the shared order buffer has a
    /// free slot (<c>pendingOrderCount &lt; maxOrders</c> — the <c>TickCommandPacket.MAX_ORDERS</c> packet budget).
    /// Overflow is deterministic drop-newest at the call site (never a throw).
    /// </summary>
    public static class DslEventRateLimit
    {
        /// <summary>True iff another button-originated <c>DslEvent</c> may be buffered this tick: under the per-tick
        /// DslEvent cap AND under the shared order-packet budget.</summary>
        public static bool CanAccept(int pendingDslEventCount, int pendingOrderCount, int maxOrders) =>
            pendingDslEventCount < EventBounds.MaxDslEventsPerTick && pendingOrderCount < maxOrders;
    }
}
