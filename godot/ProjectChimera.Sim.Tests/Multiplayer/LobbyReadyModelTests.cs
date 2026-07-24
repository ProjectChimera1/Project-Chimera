#nullable enable
using ProjectChimera.Multiplayer;
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>Story 9.7 (AC2) — the N-slot lobby readiness model (I/O row "N-slot lobby readiness").</summary>
    public class LobbyReadyModelTests
    {
        [Fact]
        public void AllReady_TrueOnlyWhenEveryPlayerSlotOccupiedAndReady()
        {
            var m = new LobbyReadyModel(8);
            Assert.False(m.AllReady(3));

            m.SetOccupied(0, true); m.SetReady(0, true);
            m.SetOccupied(1, true); m.SetReady(1, true);
            Assert.False(m.AllReady(3)); // slot 2 not occupied/ready

            m.SetOccupied(2, true);
            Assert.False(m.AllReady(3)); // occupied but not ready
            m.SetReady(2, true);
            Assert.True(m.AllReady(3));
            Assert.True(m.StartEnabled(3));
        }

        [Fact]
        public void Spectator_NeverContributesToOrBlocksAllReady()
        {
            var m = new LobbyReadyModel(8);
            for (int s = 0; s < 2; s++) { m.SetOccupied(s, true); m.SetReady(s, true); }
            // A spectator slot (index >= playerCount) is occupied but NOT ready — must not block a 2-player start.
            m.SetOccupied(2, true);
            Assert.True(m.AllReady(2));
            Assert.True(m.StartEnabled(2));
        }

        [Fact]
        public void SetReady_IgnoredForUnoccupiedSlot()
        {
            var m = new LobbyReadyModel(4);
            m.SetReady(0, true);           // slot not occupied → no-op
            Assert.False(m.IsReady(0));
        }

        [Fact]
        public void VacatingSlot_ClearsReadiness()
        {
            var m = new LobbyReadyModel(4);
            m.SetOccupied(0, true); m.SetReady(0, true);
            Assert.True(m.IsReady(0));
            m.SetOccupied(0, false);
            Assert.False(m.IsReady(0));
            Assert.False(m.IsOccupied(0));
        }

        [Fact]
        public void AllReady_FalseForNonPositiveOrOverCapacityCount()
        {
            var m = new LobbyReadyModel(4);
            Assert.False(m.AllReady(0));
            Assert.False(m.AllReady(5)); // > capacity
        }

        [Fact]
        public void Reset_ClearsEverything()
        {
            var m = new LobbyReadyModel(4);
            m.SetOccupied(0, true); m.SetReady(0, true);
            m.Reset();
            Assert.False(m.IsOccupied(0));
            Assert.False(m.IsReady(0));
        }
    }
}
