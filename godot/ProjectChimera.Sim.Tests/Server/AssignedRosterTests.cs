#nullable enable
using ProjectChimera.Core;                 // Faction, FactionRegistry
using ProjectChimera.Multiplayer.Server;   // AssignedRoster
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>Story 9.7 (AC1) — the frozen server-authoritative slot→faction roster (I/O row "Server-side slot→faction").</summary>
    public class AssignedRosterTests
    {
        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void Freeze_MapsEachSlotToToFaction(int n)
        {
            var slots = new int[n];
            for (int i = 0; i < n; i++) slots[i] = i;

            Assert.True(AssignedRoster.TryFreeze(slots, n, out var roster, out string? err));
            Assert.Null(err);
            Assert.NotNull(roster);
            Assert.Equal(n, roster!.PlayerCount);
            for (int slot = 0; slot < n; slot++)
                Assert.Equal(FactionRegistry.ToFaction(slot), roster.FactionForSlot(slot));
        }

        [Fact]
        public void Freeze_IsIndependentOfArrivalOrder()
        {
            Assert.True(AssignedRoster.TryFreeze(new[] { 2, 0, 1 }, 3, out var roster, out _));
            Assert.Equal(Faction.Player1, roster!.FactionForSlot(0));
            Assert.Equal(Faction.Player2, roster.FactionForSlot(1));
            Assert.Equal(Faction.Player3, roster.FactionForSlot(2));
        }

        [Fact]
        public void Freeze_RejectsDuplicateSlot()
        {
            Assert.False(AssignedRoster.TryFreeze(new[] { 0, 0, 1 }, 3, out var roster, out string? err));
            Assert.Null(roster);
            Assert.Contains("duplicate", err);
        }

        [Fact]
        public void Freeze_RejectsAbsentSlot()
        {
            // Count matches playerCount but a slot is out of range → the covered set is not 0..N-1 (slot 3 absent).
            Assert.False(AssignedRoster.TryFreeze(new[] { 0, 1, 3 }, 3, out var roster, out string? err));
            Assert.Null(roster);
            Assert.NotNull(err);
        }

        [Fact]
        public void Freeze_RejectsWrongCount()
        {
            Assert.False(AssignedRoster.TryFreeze(new[] { 0, 1 }, 3, out _, out _));
        }

        [Fact]
        public void SlotFactions_ReturnsAscendingArray_OfPlayerCountLength()
        {
            AssignedRoster.TryFreeze(new[] { 0, 1, 2, 3 }, 4, out var roster, out _);
            var f = roster!.SlotFactions();
            Assert.Equal(4, f.Length);
            Assert.Equal(new[] { Faction.Player1, Faction.Player2, Faction.Player3, Faction.Player4 }, f);
        }
    }
}
