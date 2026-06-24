#nullable enable
using System;

namespace ProjectChimera.Multiplayer.Server
{
    /// <summary>
    /// Server-authority core (AR-38, Story 1.9a) extracted from <see cref="DedicatedServer"/> so it is Godot-free
    /// and Tier-1-testable. Owns the <see cref="ServerChecksumCollector"/> and turns its verdicts into wire actions
    /// over INJECTED transport seams — never the concrete <c>ServerTransport</c> and never Godot:
    /// <list type="bullet">
    ///   <item>strict majority with a minority ⇒ a <c>DesyncAlert</c> (carrying the canonical hash) to each minority slot;</item>
    ///   <item>no strict majority ⇒ a broadcast <c>Halt</c> and a terminal <see cref="Halted"/> flag.</item>
    /// </list>
    /// In production <see cref="DedicatedServer"/> injects <c>_transport.SendReliableTo</c> / <c>BroadcastReliable</c>
    /// (wrapped in lambdas, since those take an optional length arg); tests inject closures that capture the emitted
    /// packets without ENet. HALT is TERMINAL in 1.9a — recovery/rejoin policy is deferred (game-architecture.md:2332).
    /// Slot is TRANSPORT-AUTHORITATIVE: <see cref="OnChecksum"/> receives it from the ENet peer→slot map, never the
    /// packet payload (which carries only tick+hash) — a client cannot spoof another slot's checksum (D5).
    /// </summary>
    public sealed class ServerHost
    {
        private readonly ServerChecksumCollector _collector;
        private readonly Action<int, byte[]> _sendReliableTo;   // (slot, packet)
        private readonly Action<byte[]> _broadcastReliable;     // (packet)

        /// <summary>Terminal once a no-majority HALT has been broadcast. Further checksums are ignored.</summary>
        public bool Halted { get; private set; }

        /// <summary>Reporting player peers this host quorums over (excludes spectators — D6).</summary>
        public int ExpectedPeerCount => _collector.ExpectedPeerCount;

        public ServerHost(int expectedPeerCount, Action<int, byte[]> sendReliableTo, Action<byte[]> broadcastReliable)
        {
            _collector = new ServerChecksumCollector(expectedPeerCount);
            _sendReliableTo = sendReliableTo ?? throw new ArgumentNullException(nameof(sendReliableTo));
            _broadcastReliable = broadcastReliable ?? throw new ArgumentNullException(nameof(broadcastReliable));
        }

        /// <summary>
        /// Feed one peer's checksum into the collector. <paramref name="slot"/> is transport-authoritative. On a
        /// completed tick: alert each minority slot (majority case) or broadcast a terminal HALT (no-majority case).
        /// A no-op once <see cref="Halted"/>.
        /// </summary>
        public void OnChecksum(int slot, uint tick, uint hash)
        {
            if (Halted) return;

            ServerChecksumCollector.Verdict v = _collector.Record(tick, slot, hash);
            if (!v.Complete) return;

            if (v.HasMajority)
            {
                // Name the diverged peer(s): one DesyncAlert per minority slot, in ascending (stable) slot order.
                foreach (int s in v.Minority)
                    _sendReliableTo(s, TickCommandPacket.MakeDesyncAlert(tick, v.Canonical));
            }
            else
            {
                // Global desync, no canonical hash → terminal HALT for everyone.
                _broadcastReliable(TickCommandPacket.MakeHalt(tick, HaltReason.NoMajority));
                Halted = true;
            }
        }
    }
}
