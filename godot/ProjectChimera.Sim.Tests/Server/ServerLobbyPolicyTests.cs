#nullable enable
using ProjectChimera.Core;                 // Faction
using ProjectChimera.Multiplayer;          // TickCommandPacket, HaltReason (Story 9.7 N=3/4 cases)
using ProjectChimera.Multiplayer.Server;    // ServerLobbyPolicy
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// Story 9.3 (Patches E2/E3) — the Godot-free lobby + chat policy: chat faction is re-stamped from the sender's
    /// transport-authoritative slot (player → own faction, spectator → Neutral), and the N-shaped count machine
    /// starts only at exact quorum with connected spectators excluded.
    /// </summary>
    public class ServerLobbyPolicyTests
    {
        private const int MaxPlayers = 2;                        // ServerTransport.MAX_PLAYERS
        private const int MaxSlots   = 4;                        // ServerTransport.MAX_SLOTS (2 players + 2 spectators)
        private static readonly Faction[] SlotFaction = { Faction.Player1, Faction.Player2 };

        // ── E2: chat re-stamp over every slot ─────────────────────────────────────

        [Theory]
        [InlineData(0, Faction.Player1)]  // player slot → own faction
        [InlineData(1, Faction.Player2)]  // player slot → own faction
        [InlineData(2, Faction.Neutral)]  // spectator slot → Neutral (spoof fixed)
        [InlineData(3, Faction.Neutral)]  // spectator slot → Neutral
        [InlineData(-1, Faction.Neutral)] // out-of-range → Neutral (defensive)
        public void StampChatFaction_PlayerKeepsFaction_SpectatorBecomesNeutral(int slot, Faction expected)
        {
            Assert.Equal(expected, ServerLobbyPolicy.StampChatFaction(slot, SlotFaction, MaxPlayers));
        }

        // ── E3: counting excludes spectators ──────────────────────────────────────

        [Fact]
        public void CountConnectedPlayers_ExcludesConnectedSpectators()
        {
            // Both players + both spectators "connected"; only the two player slots count.
            static bool AllConnected(int s) => s >= 0 && s < MaxSlots;
            Assert.Equal(2, ServerLobbyPolicy.CountConnectedPlayers(AllConnected, MaxPlayers));

            // One player + a spectator connected → count is 1 (the spectator is not counted).
            static bool P0AndSpectator(int s) => s == 0 || s == 3;
            Assert.Equal(1, ServerLobbyPolicy.CountConnectedPlayers(P0AndSpectator, MaxPlayers));
        }

        [Fact]
        public void CountReadyPlayers_CountsOnlyConnectedReadyPlayers()
        {
            static bool BothPlayersConnected(int s) => s < MaxPlayers;
            // Only player 0 readied → 1; a spectator readying could never be counted (out of range).
            Assert.Equal(1, ServerLobbyPolicy.CountReadyPlayers(BothPlayersConnected, s => s == 0, MaxPlayers));
            Assert.Equal(2, ServerLobbyPolicy.CountReadyPlayers(BothPlayersConnected, _ => true, MaxPlayers));
        }

        // ── E3: the start gate ────────────────────────────────────────────────────

        [Fact]
        public void ShouldStart_OnlyAtExactQuorum()
        {
            Assert.True(ServerLobbyPolicy.ShouldStart(connectedPlayers: 2, readyPlayers: 2, expected: 2)); // at quorum

            Assert.False(ServerLobbyPolicy.ShouldStart(1, 1, 2)); // under-connected
            Assert.False(ServerLobbyPolicy.ShouldStart(2, 1, 2)); // connected but not all ready
            Assert.False(ServerLobbyPolicy.ShouldStart(2, 2, 0)); // zero-expected lobby never starts
        }

        /// <summary>End-to-end of the E3 machine: a connected spectator must not let an under-quorum lobby start.</summary>
        [Fact]
        public void ConnectedSpectator_DoesNotCompleteQuorum()
        {
            // Player 0 + one spectator connected & "ready"; expected 2 players.
            static bool Connected(int s) => s == 0 || s == 2;
            static bool Ready(int s) => s == 0 || s == 2;

            int connected = ServerLobbyPolicy.CountConnectedPlayers(Connected, MaxPlayers);
            int ready     = ServerLobbyPolicy.CountReadyPlayers(Connected, Ready, MaxPlayers);

            Assert.Equal(1, connected); // the spectator is excluded
            Assert.Equal(1, ready);
            Assert.False(ServerLobbyPolicy.ShouldStart(connected, ready, MaxPlayers)); // 1 != 2 → no start
        }

        // ── Story 9.7: N=3 / N=4 raised-count cases ────────────────────────────────

        [Theory]
        [InlineData(3, 3, 3, true)]
        [InlineData(4, 4, 4, true)]
        [InlineData(3, 2, 3, false)]  // one player not yet connected
        [InlineData(4, 3, 4, false)]  // one player not yet ready
        [InlineData(4, 4, 3, false)]  // more connected than expected (over-fill) → no start
        public void ShouldStart_ScalesToN(int connected, int ready, int expected, bool starts)
        {
            Assert.Equal(starts, ServerLobbyPolicy.ShouldStart(connected, ready, expected));
        }

        [Theory]
        [InlineData(3)]
        [InlineData(4)]
        public void CheckStartStateAgreement_AllAgreeAtN_Allows(int n)
        {
            var hashes   = new ulong[n];
            var versions = new ushort[n];
            for (int i = 0; i < n; i++) { hashes[i] = 0xABCDEF12u; versions[i] = TickCommandPacket.PROTOCOL_VERSION; }
            Assert.Null(ServerLobbyPolicy.CheckStartStateAgreement(hashes, versions, n));
        }

        [Fact]
        public void CheckStartStateAgreement_OneDisagreeingHashAtN4_Blocks()
        {
            var hashes   = new ulong[] { 0xAAAAu, 0xAAAAu, 0xBBBBu, 0xAAAAu }; // slot 2 diverges
            var versions = new ushort[4];
            for (int i = 0; i < 4; i++) versions[i] = TickCommandPacket.PROTOCOL_VERSION;
            Assert.Equal(HaltReason.StartStateDisagreement,
                ServerLobbyPolicy.CheckStartStateAgreement(hashes, versions, 4));
        }

        [Fact]
        public void CountConnectedReadyPlayers_SpanToExpected_AtN4()
        {
            static bool Connected(int s) => s < 4;                 // slots 0..3 players
            static bool Ready(int s) => s is 0 or 1 or 2 or 3;
            Assert.Equal(4, ServerLobbyPolicy.CountConnectedPlayers(Connected, 4));
            Assert.Equal(4, ServerLobbyPolicy.CountReadyPlayers(Connected, Ready, 4));
        }
    }
}
