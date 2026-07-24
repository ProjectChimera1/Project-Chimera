#nullable enable
using ProjectChimera.Multiplayer.Server;
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>Story 9.7 (SD-9) — the dynamic player/spectator slot classifier (I/O-matrix row "Slot classification").</summary>
    public class SlotAllocationTests
    {
        [Theory]
        // slot, playerCount, ceiling → expected role
        [InlineData(0, 2, 8, SlotRole.Player)]     // i < P
        [InlineData(1, 2, 8, SlotRole.Player)]
        [InlineData(2, 2, 8, SlotRole.Spectator)]  // P <= i < S
        [InlineData(7, 2, 8, SlotRole.Spectator)]
        [InlineData(8, 2, 8, SlotRole.Rejected)]   // i >= S
        [InlineData(-1, 2, 8, SlotRole.Rejected)]  // negative slot
        [InlineData(3, 4, 8, SlotRole.Player)]     // dynamic split — 4 players now
        [InlineData(4, 4, 8, SlotRole.Spectator)]
        public void Classify_SplitsDynamicallyByPlayerCount(int slot, int p, int s, SlotRole expected)
        {
            Assert.Equal(expected, SlotAllocation.Classify(slot, p, s));
        }

        [Fact]
        public void Classify_SplitIsNotFixed2v2()
        {
            // The same slot is a Spectator at P=2 but a Player at P=4 — the split is per-match, not a hard 2/2 partition.
            Assert.Equal(SlotRole.Spectator, SlotAllocation.Classify(3, 2, 8));
            Assert.Equal(SlotRole.Player,    SlotAllocation.Classify(3, 4, 8));
        }
    }
}
