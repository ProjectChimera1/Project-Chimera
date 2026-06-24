#nullable enable
using System;
using ProjectChimera.Core.Sim;   // ILogSink — the Godot-free logging seam (also used by SimulationHost/ServerBootstrap)

namespace ProjectChimera.Multiplayer.Server
{
    /// <summary>
    /// Server-authority core (AR-38, Story 1.9a) extracted from <see cref="DedicatedServer"/> so it is Godot-free
    /// and Tier-1-testable. Owns the <see cref="ServerChecksumCollector"/> and turns its verdicts into wire actions
    /// over INJECTED transport seams — never the concrete <c>ServerTransport</c> and never Godot:
    /// <list type="bullet">
    ///   <item>all peers agree ⇒ a clean window — tally it and log the per-window PASS line (Story 1.9b);</item>
    ///   <item>strict majority with a minority ⇒ a <c>DesyncAlert</c> (carrying the canonical hash) to each minority slot;</item>
    ///   <item>no strict majority ⇒ a broadcast <c>Halt</c> and a terminal <see cref="Halted"/> flag.</item>
    /// </list>
    /// In production <see cref="DedicatedServer"/> injects <c>_transport.SendReliableTo</c> / <c>BroadcastReliable</c>
    /// (wrapped in lambdas, since those take an optional length arg) and an <see cref="ILogSink"/>; tests inject
    /// closures that capture the emitted packets + a capturing/Null log sink — no ENet, no Godot.
    /// HALT is TERMINAL in 1.9a — recovery/rejoin policy is deferred (game-architecture.md:2332).
    /// Slot is TRANSPORT-AUTHORITATIVE: <see cref="OnChecksum"/> receives it from the ENet peer→slot map, never the
    /// packet payload (which carries only tick+hash) — a client cannot spoof another slot's checksum (D5).
    ///
    /// <para>Story 1.9b adds the POSITIVE determinism verdict the 1.9a host lacked: <see cref="WindowsCompared"/> /
    /// <see cref="DesyncCount"/> / <see cref="Passing"/> + a per-clean-window <c>Info</c> line + <see cref="LogSummary"/>.
    /// This is the FR-39 "300+ ticks, 0 desync — PASS" evidence trail a human reads on the dedicated-server console.
    /// It only ADDS counters + log lines; the alert/HALT behavior is unchanged.</para>
    /// </summary>
    public sealed class ServerHost
    {
        private readonly ServerChecksumCollector _collector;
        private readonly ILogSink _log;
        private readonly Action<int, byte[]> _sendReliableTo;   // (slot, packet)
        private readonly Action<byte[]> _broadcastReliable;     // (packet)

        /// <summary>Terminal once a no-majority HALT has been broadcast. Further checksums are ignored.</summary>
        public bool Halted { get; private set; }

        /// <summary>Reporting player peers this host quorums over (excludes spectators — D6).</summary>
        public int ExpectedPeerCount => _collector.ExpectedPeerCount;

        // ── Story 1.9b: positive determinism observability (the FR-39 PASS evidence) ──────────────────────────

        /// <summary>ALL completed comparison windows (clean + diverged) — the total the MATCH SUMMARY reports.
        /// Clean (unanimous) windows = <see cref="WindowsCompared"/> − <see cref="DesyncCount"/>. (Story 1.9b review:
        /// counts every completed window, not just clean ones, so "windows compared" is the true total.)</summary>
        public int WindowsCompared { get; private set; }

        /// <summary>Completed windows that diverged — a named minority (majority case) or a no-majority HALT.</summary>
        public int DesyncCount { get; private set; }

        /// <summary>The running FR-39 verdict: true while no desync window has been observed.</summary>
        public bool Passing => DesyncCount == 0;

        public ServerHost(int expectedPeerCount, ILogSink log,
                          Action<int, byte[]> sendReliableTo, Action<byte[]> broadcastReliable)
        {
            _collector = new ServerChecksumCollector(expectedPeerCount);
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _sendReliableTo = sendReliableTo ?? throw new ArgumentNullException(nameof(sendReliableTo));
            _broadcastReliable = broadcastReliable ?? throw new ArgumentNullException(nameof(broadcastReliable));
        }

        /// <summary>
        /// Feed one peer's checksum into the collector. <paramref name="slot"/> is transport-authoritative. On a
        /// completed tick: tally a clean window (all agree), alert each minority slot (majority case), or broadcast
        /// a terminal HALT (no-majority case). A no-op once <see cref="Halted"/>.
        /// </summary>
        public void OnChecksum(int slot, uint tick, uint hash)
        {
            if (Halted) return;

            ServerChecksumCollector.Verdict v = _collector.Record(tick, slot, hash);
            if (!v.Complete) return;

            // Story 1.9b review: every completed comparison window counts toward the total the MATCH SUMMARY reports
            // (clean + diverged); DesyncCount tracks the diverged subset (clean = WindowsCompared − DesyncCount).
            WindowsCompared++;

            if (v.HasMajority && v.Minority.Count == 0)
            {
                // Story 1.9b: ALL expected peers agreed → the positive PASS evidence (FR-39). This is the line a
                // human reads on the dedicated-server console to confirm the match is still in lockstep.
                _log.Info($"[Determinism] tick {tick}: all {ExpectedPeerCount} peers matched 0x{v.Canonical:X8} (window #{WindowsCompared}).");
            }
            else if (v.HasMajority)
            {
                // Majority + named minority (N≥3): alert each diverged peer in ascending (stable) slot order. The
                // match is NOT halted here — the majority plays on — but the verdict is now FAIL.
                DesyncCount++;
                foreach (int s in v.Minority)
                    _sendReliableTo(s, TickCommandPacket.MakeDesyncAlert(tick, v.Canonical));
                _log.Warn($"[Determinism] tick {tick}: DESYNC — minority slot(s) {string.Join(",", v.Minority)} diverged from canonical 0x{v.Canonical:X8}.");
            }
            else
            {
                // No strict majority → global desync, no canonical hash → terminal HALT for everyone.
                DesyncCount++;
                _broadcastReliable(TickCommandPacket.MakeHalt(tick, HaltReason.NoMajority));
                Halted = true;
                _log.Warn($"[Determinism] tick {tick}: GLOBAL DESYNC — no canonical hash. Broadcasting terminal HALT.");
            }
        }

        /// <summary>
        /// Emit the terminal FR-39 verdict line. Call on match end / player disconnect / server shutdown so a human
        /// reading the dedicated-server console sees the summary: "{N} windows compared, {D} desync — PASS|FAIL|INCONCLUSIVE".
        /// INCONCLUSIVE when no window was ever completed — nothing was actually compared, so it is NOT a clean pass
        /// (Story 1.9b review: a 0-window match must not masquerade as PASS in a console/log scan).
        /// </summary>
        public void LogSummary()
        {
            string verdict = WindowsCompared == 0 ? "INCONCLUSIVE" : Passing ? "PASS" : "FAIL";
            _log.Info($"[Determinism] MATCH SUMMARY: {WindowsCompared} windows compared, {DesyncCount} desync — {verdict}.");
        }
    }
}
