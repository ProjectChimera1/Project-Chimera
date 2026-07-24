#nullable enable
using System;
using ProjectChimera.Multiplayer.Matchmaking;
using Xunit;

namespace ProjectChimera.Sim.Tests.Matchmaking
{
    /// <summary>Story 9.7 — the N-player matchmaker parameterization (I/O-matrix row "Matchmaker config for N").</summary>
    public class MatchmakerConfigTests
    {
        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void ForPlayerCount_SetsMinEqualsMaxEqualsP(int p)
        {
            var cfg = MatchmakerConfig.ForPlayerCount(p);
            Assert.Equal(p, cfg.MinCount);
            Assert.Equal(p, cfg.MaxCount);
        }

        [Fact]
        public void GameKey_IsPlayerCountParameterized_NotChimera1v1()
        {
            var two  = MatchmakerConfig.ForPlayerCount(2);
            var four = MatchmakerConfig.ForPlayerCount(4);
            Assert.Equal("chimera_2p", two.GameKey);
            Assert.Equal("chimera_4p", four.GameKey);
            Assert.NotEqual("chimera_1v1", two.GameKey);         // the old 1v1 pin is gone
            Assert.NotEqual(two.GameKey, four.GameKey);          // distinct pools per player count
            Assert.Equal("chimera_2p", two.StringProperties()["game"]);
        }

        [Fact]
        public void StringProperties_CarryTheGameKey_NumericEmpty()
        {
            var cfg = MatchmakerConfig.ForPlayerCount(3);
            Assert.Equal(cfg.GameKey, cfg.StringProperties()["game"]);
            Assert.Empty(cfg.NumericProperties());
        }

        [Theory]
        [InlineData(1)]
        [InlineData(0)]
        [InlineData(-1)]
        public void ForPlayerCount_RejectsBelowTwo(int p)
        {
            // I/O matrix: P<2 → invalid config rejected.
            Assert.Throws<ArgumentOutOfRangeException>(() => MatchmakerConfig.ForPlayerCount(p));
        }

        [Fact]
        public void ForPlayerCount_RejectsAboveTransportSeatCeiling()
        {
            // P3: the matchmaker never queues more players than the transport can seat (MpSeatCeiling == MAX_PLAYERS),
            // even though the SIM faction ceiling (PLAYER_COUNT) is larger.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => MatchmakerConfig.ForPlayerCount(ProjectChimera.Multiplayer.PlayerCountPolicy.MpSeatCeiling + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => MatchmakerConfig.ForPlayerCount(8)); // 8 > seat ceiling
        }

        [Fact]
        public void CountMultiple_IsCarried_AndValidatedAgainstPlayerCount()
        {
            var cfg = MatchmakerConfig.ForPlayerCount(4, countMultiple: 2);
            Assert.Equal(2, cfg.CountMultiple);
            // A countMultiple that does not evenly divide the player count is rejected.
            Assert.Throws<ArgumentOutOfRangeException>(() => MatchmakerConfig.ForPlayerCount(3, countMultiple: 2));
        }

        [Fact]
        public void CustomGameKey_IsParameterized()
        {
            var cfg = MatchmakerConfig.ForPlayerCount(2, gameKey: "ranked");
            Assert.Equal("ranked_2p", cfg.GameKey);
        }
    }
}
