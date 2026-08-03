#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core.Sim;
using ProjectChimera.Multiplayer;
using ProjectChimera.Multiplayer.Server;
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>
    /// DW-447 — the headless "N independent sims compute + compare their OWN checksums" artifact the ledger
    /// records as missing. Story 9.15's e2e feeds ONE host's single checksum to all four ServerHost slots
    /// (unanimous by construction) and the loopback self-test used to send a hardcoded <c>GOOD</c> constant —
    /// so no automated path could ever observe a real divergence between independently-computed checksums.
    ///
    /// Here FOUR fully independent <see cref="LoopbackPeerSim"/> instances (own SimulationHost + stores each —
    /// the same class the rewritten in-Godot <c>LoopbackDesyncSelfTest</c> peers step) are advanced in lockstep;
    /// each slot reports ITS OWN sim's real per-tick hash into a real <see cref="ServerHost"/>(4) quorum. The
    /// positive arm proves genuine 4-way agreement (and that the hash stream is non-degenerate — the sims
    /// actually evolve); the negative arm proves the quorum DETECTS a diverging peer fed real-but-tampered
    /// hashes, which the hardcoded-GOOD scheme made structurally impossible.
    /// </summary>
    public class IndependentPeerSimQuorumTests
    {
        private const int Peers = 4;
        private const int Ticks = 300; // the FR-39 "300+ ticks, 0 desync" shape, one window per tick

        [Fact]
        public void FourIndependentSims_OwnComputedChecksums_AgreeForAllWindows_QuorumGreen()
        {
            var sims = new LoopbackPeerSim[Peers];
            for (int s = 0; s < Peers; s++) sims[s] = new LoopbackPeerSim();

            var server = new ServerHost(Peers, NullLogSink.Instance,
                sendReliableTo: (_, _) => { }, broadcastReliable: _ => { });

            var slot0Hashes = new HashSet<uint>();
            for (int i = 0; i < Ticks; i++)
            {
                uint tick0 = 0, hash0 = 0;
                for (int s = 0; s < Peers; s++)
                {
                    (uint tick, uint hash) = sims[s].Step();
                    if (s == 0) { tick0 = tick; hash0 = hash; slot0Hashes.Add(hash); }

                    // Direct cross-sim agreement (crisper diagnostics than the quorum alone): every independent
                    // instance must be at the same tick with the same real checksum, or determinism regressed.
                    Assert.Equal(tick0, tick);
                    Assert.Equal(hash0, hash);

                    server.OnChecksum(s, tick, hash);
                }
                Assert.Equal((uint)(i + 1), tick0); // ChecksumInterval=1 → the sink reports every tick, in order
            }

            // The real quorum over 4 independently-computed hash streams: every window compared, zero desync.
            Assert.Equal(Ticks, server.WindowsCompared);
            Assert.Equal(0, server.DesyncCount);
            Assert.True(server.Passing, "4 independent sims' real checksums must agree (zero desync)");
            Assert.False(server.Halted);

            // Non-degenerate: the checksum stream must actually EVOLVE (a frozen/empty sim agreeing with itself
            // forever would be a vacuous pass — the hardcoded-GOOD failure mode this test exists to close).
            Assert.True(slot0Hashes.Count > Ticks / 2,
                $"expected an evolving hash stream, got only {slot0Hashes.Count} distinct hashes over {Ticks} ticks");
        }

        [Fact]
        public void TamperedPeer_RealChecksumStream_IsDetected_AlertsThatSlot_AndFailsPassing()
        {
            var sims = new LoopbackPeerSim[Peers];
            for (int s = 0; s < Peers; s++) sims[s] = new LoopbackPeerSim();

            var alerts = new List<int>();
            var server = new ServerHost(Peers, NullLogSink.Instance,
                sendReliableTo: (slot, _) => alerts.Add(slot), broadcastReliable: _ => { });

            // 5 clean windows first (the self-test's clean-phase shape) …
            for (int i = 0; i < 5; i++)
                for (int s = 0; s < Peers; s++)
                {
                    (uint tick, uint hash) = sims[s].Step();
                    server.OnChecksum(s, tick, hash);
                }
            Assert.True(server.Passing);
            Assert.Equal(5, server.WindowsCompared);

            // … then slot 3's sim diverges (its real hash is corrupted — a stand-in for any determinism bug in
            // ONE peer's sim). A strict 3-vs-1 majority must flag the window and alert the diverged slot.
            for (int s = 0; s < Peers; s++)
            {
                (uint tick, uint hash) = sims[s].Step();
                if (s == 3) hash ^= 0xDEADBEEFu;
                server.OnChecksum(s, tick, hash);
            }

            Assert.True(server.DesyncCount > 0, "a diverging peer's real checksum must be counted as a desync window");
            Assert.False(server.Passing, "Passing must drop once a real divergence is observed");
            Assert.False(server.Halted, "a strict 3-vs-1 majority exists — the match must NOT halt");
            Assert.Contains(3, alerts); // the DesyncAlert went to the diverged slot
        }
    }
}
