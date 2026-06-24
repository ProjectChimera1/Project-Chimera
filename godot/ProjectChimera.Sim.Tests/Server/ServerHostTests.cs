#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Multiplayer;          // PacketType, TickCommandPacket, HaltReason
using ProjectChimera.Multiplayer.Server;   // ServerHost
using Xunit;

namespace ProjectChimera.Sim.Tests.Server
{
    /// <summary>
    /// Story 1.9a (AC3/AC4, D5) — ServerHost turns collector verdicts into wire actions over INJECTED transport
    /// seams (no ENet, no Godot). Proves: a majority emits one parseable DesyncAlert (carrying the canonical hash)
    /// to EACH minority slot; a no-majority broadcasts a Halt(NoMajority) and sets the terminal Halted flag; once
    /// Halted, later checksums are ignored; a clean majority emits nothing.
    /// </summary>
    public class ServerHostTests
    {
        /// <summary>Captures everything the host would have put on the wire.</summary>
        private sealed class Captured
        {
            public readonly List<(int slot, byte[] pkt)> Sent = new();
            public readonly List<byte[]> Broadcast = new();
        }

        private static (ServerHost host, Captured cap) Make(int expectedPeers)
        {
            var cap = new Captured();
            var host = new ServerHost(expectedPeers,
                (slot, pkt) => cap.Sent.Add((slot, pkt)),
                pkt => cap.Broadcast.Add(pkt));
            return (host, cap);
        }

        [Fact]
        public void Majority_AlertsEachMinoritySlot_WithCanonicalHash()
        {
            var (host, cap) = Make(3);
            host.OnChecksum(0, 10u, 0xAAAAu);
            host.OnChecksum(1, 10u, 0xAAAAu);
            host.OnChecksum(2, 10u, 0xBBBBu); // slot 2 diverged

            Assert.Single(cap.Sent);
            Assert.Empty(cap.Broadcast);
            Assert.Equal(2, cap.Sent[0].slot);
            Assert.True(TickCommandPacket.TryReadDesyncAlert(cap.Sent[0].pkt, cap.Sent[0].pkt.Length,
                out uint tick, out uint canonical));
            Assert.Equal(10u, tick);
            Assert.Equal(0xAAAAu, canonical);
            Assert.False(host.Halted);
        }

        [Fact]
        public void Majority_NoMinority_EmitsNothing()
        {
            var (host, cap) = Make(3);
            host.OnChecksum(0, 5u, 0x1u);
            host.OnChecksum(1, 5u, 0x1u);
            host.OnChecksum(2, 5u, 0x1u);

            Assert.Empty(cap.Sent);
            Assert.Empty(cap.Broadcast);
            Assert.False(host.Halted);
        }

        [Fact]
        public void NoMajority_BroadcastsHalt_AndSetsHalted()
        {
            var (host, cap) = Make(2);
            host.OnChecksum(0, 7u, 0x1u);
            host.OnChecksum(1, 7u, 0x2u); // 1-vs-1 → no majority

            Assert.Empty(cap.Sent);
            Assert.Single(cap.Broadcast);
            Assert.True(TickCommandPacket.TryReadHalt(cap.Broadcast[0], cap.Broadcast[0].Length,
                out uint tick, out HaltReason reason));
            Assert.Equal(7u, tick);
            Assert.Equal(HaltReason.NoMajority, reason);
            Assert.True(host.Halted);
        }

        [Fact]
        public void Halted_IsTerminal_IgnoresLaterChecksums()
        {
            var (host, cap) = Make(2);
            host.OnChecksum(0, 7u, 0x1u);
            host.OnChecksum(1, 7u, 0x2u); // HALT
            Assert.True(host.Halted);
            int broadcastsAfterHalt = cap.Broadcast.Count;

            // A later, perfectly-agreeing tick must be ignored — HALT is terminal.
            host.OnChecksum(0, 8u, 0x9u);
            host.OnChecksum(1, 8u, 0x9u);

            Assert.Equal(broadcastsAfterHalt, cap.Broadcast.Count);
            Assert.Empty(cap.Sent);
        }

        [Fact]
        public void Ctor_NullSeams_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => new ServerHost(2, null!, _ => { }));
            Assert.Throws<ArgumentNullException>(() => new ServerHost(2, (_, _) => { }, null!));
        }
    }
}
