#nullable enable
using ProjectChimera.Core;         // Faction
using ProjectChimera.Multiplayer;  // PacketType, TickCommandPacket, LobbyHelloPolicy, LobbyHelloKind
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>
    /// DW-419 / DW-420 — the Hello sender-role discriminator + the lobby-chat local-echo decision.
    ///
    /// DW-420: a dedicated server's SPECTATOR Hello carries Faction.Neutral, which the lobby used to read
    /// unconditionally as a P2P "Host confirmed — click Ready" 2-player confirmation. The Hello now carries a
    /// role-flags byte (dedicated / spectator) and <see cref="LobbyHelloPolicy.Classify"/> routes any flagged
    /// Neutral Hello to a spectator view, never to the P2P host-confirm.
    ///
    /// DW-419: on the dedicated path the server re-stamps and rebroadcasts a LobbyChat to every peer INCLUDING
    /// the sender, so the client's unconditional optimistic echo rendered the sender's own line twice.
    /// <see cref="LobbyHelloPolicy.ShouldLocalEchoLobbyChat"/> suppresses the echo whenever the Hello carried a
    /// role flag, and keeps it on the P2P path (where no peer rebroadcasts).
    ///
    /// Every case here fails against the pre-fix code: the flags byte did not exist on the wire, and the lobby
    /// had no discriminator — a Neutral Hello was ALWAYS the 2-slot host-confirm, and the echo was unconditional.
    /// </summary>
    public class LobbyHelloPolicyTests
    {
        // ── Wire: the flags byte round-trips ───────────────────────────────────────

        [Theory]
        [InlineData((byte)Faction.Neutral, (byte)0)]                                    // P2P host confirm
        [InlineData((byte)Faction.Player1, TickCommandPacket.HELLO_FLAG_DEDICATED)]     // dedicated player
        [InlineData((byte)Faction.Neutral,
            TickCommandPacket.HELLO_FLAG_DEDICATED | TickCommandPacket.HELLO_FLAG_SPECTATOR)] // dedicated spectator
        public void Hello_RoundTrips_FactionVersionAndFlags(byte factionByte, byte flags)
        {
            var faction = (Faction)factionByte;
            byte[] b = TickCommandPacket.MakeHello(faction, flags);
            Assert.Equal(5, b.Length);
            Assert.Equal((byte)PacketType.Hello, b[0]);

            Assert.True(TickCommandPacket.TryReadHello(b, b.Length, out Faction f, out ushort v, out byte rf));
            Assert.Equal(faction, f);
            Assert.Equal(TickCommandPacket.PROTOCOL_VERSION, v);
            Assert.Equal(flags, rf);
        }

        [Fact]
        public void Hello_DefaultMake_CarriesNoRoleFlags()
        {
            // The P2P host's MakeHello() (no args) must read back as flags 0 → the unchanged P2P interpretation.
            byte[] b = TickCommandPacket.MakeHello();
            Assert.True(TickCommandPacket.TryReadHello(b, b.Length, out Faction f, out _, out byte rf));
            Assert.Equal(Faction.Neutral, f);
            Assert.Equal(0, rf);
        }

        [Fact]
        public void Hello_LegacyFourBytePacket_ReadsFlagsAsZero()
        {
            // A pre-flag 4-byte Hello (type + version(2) + faction) — flags default to 0, so an old sender's
            // packet keeps its old meaning (behavior-neutral additive extension, no PROTOCOL_VERSION bump).
            var legacy = new byte[] {
                (byte)PacketType.Hello,
                (byte)TickCommandPacket.PROTOCOL_VERSION, (byte)(TickCommandPacket.PROTOCOL_VERSION >> 8),
                (byte)Faction.Player2
            };
            Assert.True(TickCommandPacket.TryReadHello(legacy, legacy.Length, out Faction f, out ushort v, out byte rf));
            Assert.Equal(Faction.Player2, f);
            Assert.Equal(TickCommandPacket.PROTOCOL_VERSION, v);
            Assert.Equal(0, rf);

            // And the pre-existing overloads still parse the new 5-byte packet (additive both directions).
            byte[] b = TickCommandPacket.MakeHello(Faction.Player3, TickCommandPacket.HELLO_FLAG_DEDICATED);
            Assert.True(TickCommandPacket.TryReadHello(b, b.Length, out Faction f2));
            Assert.Equal(Faction.Player3, f2);
        }

        [Fact]
        public void Hello_TruncatedOrWrongType_FailsClosed_WithZeroedOuts()
        {
            byte[] b = TickCommandPacket.MakeHello(Faction.Player1, TickCommandPacket.HELLO_FLAG_DEDICATED);
            Assert.False(TickCommandPacket.TryReadHello(b, 2, out _, out _, out byte rfShort)); // < 3 header bytes
            Assert.Equal(0, rfShort);

            var notHello = new byte[5];
            notHello[0] = (byte)PacketType.Ready;
            Assert.False(TickCommandPacket.TryReadHello(notHello, notHello.Length, out _, out _, out _));
        }

        [Fact]
        public void HelloFlags_AreDistinctBits()
        {
            Assert.NotEqual(TickCommandPacket.HELLO_FLAG_DEDICATED, TickCommandPacket.HELLO_FLAG_SPECTATOR);
            Assert.Equal(0, TickCommandPacket.HELLO_FLAG_DEDICATED & TickCommandPacket.HELLO_FLAG_SPECTATOR);
        }

        // ── DW-420: classification ─────────────────────────────────────────────────

        [Fact]
        public void Classify_DedicatedSpectatorHello_IsNotP2pHostConfirm()
        {
            // THE DW-420 defect pin: the server's spectator Hello (Neutral + dedicated|spectator flags) must
            // render a spectator view — the pre-fix lobby read every Neutral Hello as the bogus 2-player
            // "Host confirmed — click Ready" P2P confirmation.
            var kind = LobbyHelloPolicy.Classify(Faction.Neutral,
                TickCommandPacket.HELLO_FLAG_DEDICATED | TickCommandPacket.HELLO_FLAG_SPECTATOR);
            Assert.Equal(LobbyHelloKind.DedicatedSpectator, kind);
        }

        [Fact]
        public void Classify_NeutralOnDedicatedPath_IsSpectator_NeverHostConfirm()
        {
            // Defensive: a dedicated server never P2P-host-confirms, so Neutral + DEDICATED (even without the
            // spectator bit) must not produce the 2-slot confirmation.
            Assert.Equal(LobbyHelloKind.DedicatedSpectator,
                LobbyHelloPolicy.Classify(Faction.Neutral, TickCommandPacket.HELLO_FLAG_DEDICATED));
        }

        [Fact]
        public void Classify_P2pNeutralHello_StaysHostConfirm()
        {
            // The unchanged P2P path (flags 0) — and the reading a legacy 4-byte Hello gets (flags parse as 0).
            Assert.Equal(LobbyHelloKind.P2pHostConfirm, LobbyHelloPolicy.Classify(Faction.Neutral, 0));
        }

        [Theory]
        [InlineData((byte)Faction.Player1)]
        [InlineData((byte)Faction.Player2)]
        [InlineData((byte)Faction.Player4)]
        public void Classify_AssignedFaction_IsAssignedPlayer_WithOrWithoutDedicatedFlag(byte factionByte)
        {
            var faction = (Faction)factionByte;
            Assert.Equal(LobbyHelloKind.AssignedPlayer,
                LobbyHelloPolicy.Classify(faction, TickCommandPacket.HELLO_FLAG_DEDICATED));
            // A legacy dedicated server's player Hello (no flags byte → 0) still assigns the faction.
            Assert.Equal(LobbyHelloKind.AssignedPlayer, LobbyHelloPolicy.Classify(faction, 0));
        }

        [Fact]
        public void Classify_SpectatorFlag_IsDecisive_OverAFactionByte()
        {
            // The explicit seat classification wins even if a faction byte is (wrongly) present.
            Assert.Equal(LobbyHelloKind.DedicatedSpectator,
                LobbyHelloPolicy.Classify(Faction.Player1,
                    TickCommandPacket.HELLO_FLAG_DEDICATED | TickCommandPacket.HELLO_FLAG_SPECTATOR));
        }

        // ── DW-419: lobby-chat local-echo decision ─────────────────────────────────

        [Fact]
        public void ChatEcho_SuppressedOnTheDedicatedPath()
        {
            // THE DW-419 defect pin: the dedicated server rebroadcasts LobbyChat to the sender too, so the
            // optimistic local echo (pre-fix: unconditional) would render the sender's own line twice.
            Assert.False(LobbyHelloPolicy.ShouldLocalEchoLobbyChat(TickCommandPacket.HELLO_FLAG_DEDICATED));
            Assert.False(LobbyHelloPolicy.ShouldLocalEchoLobbyChat(
                (byte)(TickCommandPacket.HELLO_FLAG_DEDICATED | TickCommandPacket.HELLO_FLAG_SPECTATOR)));
            Assert.False(LobbyHelloPolicy.ShouldLocalEchoLobbyChat(TickCommandPacket.HELLO_FLAG_SPECTATOR));
        }

        [Fact]
        public void ChatEcho_KeptOnTheP2pPath()
        {
            // P2P has no rebroadcast — the optimistic echo is the ONLY way the sender sees its own line.
            Assert.True(LobbyHelloPolicy.ShouldLocalEchoLobbyChat(0));
        }

        // ── End-to-end: the server-shaped packets drive the right client decisions ─

        [Fact]
        public void ServerShapedHellos_DriveSpectatorViewAndEchoSuppression()
        {
            // Exactly what DedicatedServer.HandleConnect now sends for a spectator slot…
            byte[] spec = TickCommandPacket.MakeHello(Faction.Neutral,
                (byte)(TickCommandPacket.HELLO_FLAG_DEDICATED | TickCommandPacket.HELLO_FLAG_SPECTATOR));
            Assert.True(TickCommandPacket.TryReadHello(spec, spec.Length, out Faction sf, out _, out byte sFlags));
            Assert.Equal(LobbyHelloKind.DedicatedSpectator, LobbyHelloPolicy.Classify(sf, sFlags));
            Assert.False(LobbyHelloPolicy.ShouldLocalEchoLobbyChat(sFlags));

            // …and for a player slot: assigned faction + no local chat echo (the server echoes back).
            byte[] player = TickCommandPacket.MakeHello(Faction.Player1, TickCommandPacket.HELLO_FLAG_DEDICATED);
            Assert.True(TickCommandPacket.TryReadHello(player, player.Length, out Faction pf, out _, out byte pFlags));
            Assert.Equal(LobbyHelloKind.AssignedPlayer, LobbyHelloPolicy.Classify(pf, pFlags));
            Assert.False(LobbyHelloPolicy.ShouldLocalEchoLobbyChat(pFlags));

            // …while the P2P host's default Hello keeps the pre-fix behavior end-to-end.
            byte[] p2p = TickCommandPacket.MakeHello();
            Assert.True(TickCommandPacket.TryReadHello(p2p, p2p.Length, out Faction hf, out _, out byte hFlags));
            Assert.Equal(LobbyHelloKind.P2pHostConfirm, LobbyHelloPolicy.Classify(hf, hFlags));
            Assert.True(LobbyHelloPolicy.ShouldLocalEchoLobbyChat(hFlags));
        }
    }
}
