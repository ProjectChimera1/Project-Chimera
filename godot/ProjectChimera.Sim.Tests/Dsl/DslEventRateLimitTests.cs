#nullable enable
using ProjectChimera.Dsl;
using ProjectChimera.Multiplayer;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// Story 7.9 (PATCH 5) — the executed boundary test for the extracted per-tick DslEvent admission seam
    /// (<see cref="DslEventRateLimit.CanAccept"/>), which <c>LockstepManager.EnqueueDslEvent</c> now delegates to.
    /// Mirrors the DelayMath/HandshakeGate precedent: the guard logic is Godot-free and lives in <c>src/Dsl</c>
    /// (globbed into SimSources.props), so the drop-newest boundary is executable in the Tier-1 set rather than
    /// only reachable through the Godot-coupled manager.
    /// </summary>
    public class DslEventRateLimitTests
    {
        // The REAL packet budget — TickCommandPacket lives in NetworkCommand.cs, which IS in the Tier-1 source set,
        // so no mirrored literal is needed: if the budget ever changes, these boundaries move with it.
        private const int MaxOrders = TickCommandPacket.MAX_ORDERS;

        [Fact]
        public void AcceptsWhileUnderTheDslEventCap()
        {
            // Under both bounds (DslEvent count < 8, order count has room) → accept.
            for (int pending = 0; pending < EventBounds.MaxDslEventsPerTick; pending++)
                Assert.True(DslEventRateLimit.CanAccept(pending, pendingOrderCount: 0, MaxOrders));
        }

        [Fact]
        public void RejectsAtTheDslEventCap_TheNinth()
        {
            // The 9th (count already == 8) is rejected even with plenty of order-packet room.
            Assert.Equal(8, EventBounds.MaxDslEventsPerTick);
            Assert.True(DslEventRateLimit.CanAccept(EventBounds.MaxDslEventsPerTick - 1, 0, MaxOrders)); // 8th admitted
            Assert.False(DslEventRateLimit.CanAccept(EventBounds.MaxDslEventsPerTick, 0, MaxOrders));    // 9th rejected
        }

        [Fact]
        public void RejectsWhenOrderBudgetFull_IndependentOfDslEventCount()
        {
            // The shared packet budget gates independently: a full order buffer rejects even with zero DslEvents.
            Assert.False(DslEventRateLimit.CanAccept(pendingDslEventCount: 0, pendingOrderCount: MaxOrders, MaxOrders));
            Assert.False(DslEventRateLimit.CanAccept(pendingDslEventCount: 0, pendingOrderCount: MaxOrders + 5, MaxOrders));
            Assert.True(DslEventRateLimit.CanAccept(pendingDslEventCount: 0, pendingOrderCount: MaxOrders - 1, MaxOrders));
        }
    }
}
