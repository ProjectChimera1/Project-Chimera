#nullable enable
using ProjectChimera.Multiplayer.Party;
using Xunit;

namespace ProjectChimera.Sim.Tests.Party
{
    /// <summary>Story 9.7 (AC1) — the pure party model (I/O row "Party lifecycle").</summary>
    public class PartyStateTests
    {
        [Fact]
        public void FirstMember_BecomesLeader()
        {
            var p = new PartyState(4);
            Assert.True(p.TryAdd("alice"));
            Assert.Equal("alice", p.LeaderId);
            Assert.True(p.TryAdd("bob"));
            Assert.Equal("alice", p.LeaderId); // unchanged
            Assert.Equal(2, p.Count);
        }

        [Fact]
        public void TryAdd_RejectsDuplicateEmptyAndOverCapacity()
        {
            var p = new PartyState(2);
            Assert.True(p.TryAdd("alice"));
            Assert.False(p.TryAdd("alice"));  // duplicate
            Assert.False(p.TryAdd(""));       // empty id
            Assert.True(p.TryAdd("bob"));
            Assert.False(p.TryAdd("carol"));  // join beyond capacity → rejected
            Assert.Equal(2, p.Count);
        }

        [Fact]
        public void RemovingLeader_PromotesFirstRemainingMember()
        {
            var p = new PartyState(4);
            p.TryAdd("alice"); p.TryAdd("bob"); p.TryAdd("carol");
            Assert.True(p.Remove("alice"));
            Assert.Equal("bob", p.LeaderId); // first remaining, deterministic
            Assert.True(p.Remove("bob"));
            Assert.Equal("carol", p.LeaderId);
            Assert.True(p.Remove("carol"));
            Assert.Null(p.LeaderId);          // empty party has no leader
        }

        [Fact]
        public void CanStartMatchmaking_OnlyLeader()
        {
            var p = new PartyState(4);
            p.TryAdd("alice"); p.TryAdd("bob");
            Assert.True(p.CanStartMatchmaking("alice"));
            Assert.False(p.CanStartMatchmaking("bob"));  // non-leader start → rejected
            Assert.False(p.CanStartMatchmaking("mallory"));
        }

        [Fact]
        public void EmptyParty_CannotStartMatchmaking()
        {
            var p = new PartyState(4);
            Assert.False(p.CanStartMatchmaking("anyone"));
        }

        [Fact]
        public void SetLeader_RejectsNonMember_ReadyTracksPerMember()
        {
            var p = new PartyState(4);
            p.TryAdd("alice"); p.TryAdd("bob");
            Assert.False(p.TrySetLeader("mallory"));
            Assert.True(p.TrySetLeader("bob"));
            Assert.Equal("bob", p.LeaderId);

            Assert.False(p.AllReady());
            Assert.True(p.SetReady("alice", true));
            Assert.False(p.AllReady());
            Assert.True(p.SetReady("bob", true));
            Assert.True(p.AllReady());
            Assert.False(p.SetReady("mallory", true)); // non-member
        }

        [Fact]
        public void Clear_ResetsMembersAndLeader()
        {
            var p = new PartyState(4);
            p.TryAdd("alice"); p.TryAdd("bob");
            p.Clear();
            Assert.Equal(0, p.Count);
            Assert.Null(p.LeaderId);
        }
    }
}
