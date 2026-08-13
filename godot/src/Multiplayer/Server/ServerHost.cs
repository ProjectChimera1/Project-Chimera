#nullable enable
using System;
using System.Collections.Generic;   // IReadOnlyList — the re-tallied window list a quorum reduction returns
using ProjectChimera.Core.Sim;   // ILogSink — the Godot-free logging seam (also used by SimulationHost/ServerBootstrap)

namespace ProjectChimera.Multiplayer.Server
{
    /// <summary>
    /// Server-authority core (AR-38, Story 1.9a) extracted from <see cref="DedicatedServer"/> so it is Godot-free
    /// and Tier-1-testable. Owns the <see cref="ServerChecksumCollector"/> and turns its verdicts into wire actions
    /// over INJECTED transport seams — never the concrete <c>ServerTransport</c> and never Godot:
    /// <list type="bullet">
    ///   <item>all peers agree ⇒ a clean window — tally it and log the per-window PASS line (Story 1.9b);</item>
    ///   <item>strict majority with a minority ⇒ a <c>DesyncAlert</c> (carrying the canonical hash) to each minority slot, then
    ///         (DW-237) that slot is DROPPED from the quorum — the alert is terminal client-side, so the alerted peer
    ///         stops reporting while staying CONNECTED and would otherwise freeze the survivors' quorum forever;</item>
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

        /// <summary>
        /// DW-239 — comparison windows the collector's ring ABANDONED before every expected peer reported (a peer
        /// fell a full ring-window behind, so a newer tick re-keyed its bucket). An abandoned window was never
        /// compared, so it is NOT a desync and does NOT flip <see cref="Passing"/> — but it is also NOT part of
        /// <see cref="WindowsCompared"/>, so without this counter a stretch of never-verified match time was
        /// invisible in the FR-39 readout. Reported by <see cref="LogSummary"/> alongside the verdict.
        /// </summary>
        public int AbandonedWindows { get; private set; }

        /// <summary>
        /// DW-414 — completed windows whose quorum was a SINGLE reporter (the post-drop 1v1 floor). A lone reporter
        /// is trivially its own strict majority, so these windows are pure LIVENESS/observability — proof the
        /// survivor's sim is still advancing — NOT cross-peer determinism attestation (no comparison peer remains).
        /// They still count in <see cref="WindowsCompared"/> and never flip <see cref="Passing"/>, but the human
        /// surface must not read them as PASS: the per-window line says "attestation suspended", and
        /// <see cref="LogSummary"/> reports them separately from the cross-peer-attested count (an all-single-reporter
        /// match is INCONCLUSIVE, not PASS).
        /// </summary>
        public int SingleReporterWindows { get; private set; }

        /// <summary>
        /// DW-511 — checksum reports the collector DROPPED for keying implausibly far past the server-authoritative
        /// tick frontier. A correct lockstep client never produces one: a non-zero count means a peer tried to evict
        /// the honest quorum's in-flight comparison windows (and blind the desync guard) with a fabricated tick.
        /// Nothing was compared and nothing was lost, so this is neither a desync nor an abandoned window — it is a
        /// MISBEHAVIOUR counter, reported by <see cref="LogSummary"/> so the grief attempt is not invisible.
        /// </summary>
        public int FarFutureChecksumRejections { get; private set; }

        // DW-511: one console line per offending slot. The rejection is cheap to drive at wire rate, so an
        // unbounded log line here would be the soft log-write DoS DW-392 already had to fix on the command path.
        private readonly bool[] _farFutureLogged = new bool[ServerChecksumCollector.MaxSlots];

        /// <param name="tickFrontier">
        /// DW-511 — the server-authoritative confirmed tick high-water (<c>MergedTickBuilder.EmittedThrough</c>).
        /// Supplying it ARMS the collector's far-future acceptance bound; omitting it keeps the legacy unbounded
        /// acceptance, which is only safe when every reporter is trusted in-process code. Any host fed by a real
        /// transport MUST pass it — see <see cref="ServerChecksumCollector"/>'s ctor doc.
        /// </param>
        public ServerHost(int expectedPeerCount, ILogSink log,
                          Action<int, byte[]> sendReliableTo, Action<byte[]> broadcastReliable,
                          Func<long>? tickFrontier = null)
        {
            _collector = new ServerChecksumCollector(expectedPeerCount, OnWindowAbandoned, tickFrontier);
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

            // DW-511: the collector counts a far-future rejection rather than raising a seam — Record takes exactly
            // one report per call, so a change in the counter across THIS call names THIS (slot, tick). Reading the
            // delta keeps the drop observable without a fourth collector ctor seam for what is one log line.
            int rejectedBefore = _collector.FarFutureRejections;

            ServerChecksumCollector.Verdict v = _collector.Record(tick, slot, hash);

            if (_collector.FarFutureRejections != rejectedBefore)
            {
                OnFarFutureChecksum(slot, tick);
                return;
            }

            if (!v.Complete) return;
            ProcessVerdict(tick, v);
        }

        /// <summary>
        /// DW-511 — a peer reported a checksum for a tick no honest client can have executed yet (implausibly far
        /// past the server's own confirmed frontier). The report was DROPPED before it could evict an honest
        /// in-flight comparison window or poison the collector's resolved high-water. Count it always; log it ONCE
        /// per offending slot (an unbounded line here would be a client-drivable log-write DoS — the DW-392 lesson).
        /// </summary>
        private void OnFarFutureChecksum(int slot, uint tick)
        {
            FarFutureChecksumRejections++;
            if ((uint)slot >= ServerChecksumCollector.MaxSlots || _farFutureLogged[slot]) return;
            _farFutureLogged[slot] = true;
            _log.Warn($"[Determinism] slot {slot} reported a checksum for tick {tick} — implausibly far past the " +
                      "confirmed tick frontier; DROPPED (a correct client cannot produce this). Further such " +
                      "reports from this slot are counted but not logged.");
        }

        /// <summary>
        /// DW-239 — the collector abandoned an in-flight comparison window (a peer fell a full ring-window behind so
        /// a newer tick re-keyed its bucket). Count it and say so on the console: the window is gone, it will never
        /// yield a verdict, and it must not hide inside a "k windows compared, 0 desync — PASS" readout.
        /// </summary>
        private void OnWindowAbandoned(uint tick, int reportsLost)
        {
            AbandonedWindows++;
            _log.Warn($"[Determinism] tick {tick}: comparison window ABANDONED — a reporter fell a full window behind " +
                      $"({reportsLost} of {ExpectedPeerCount} reports discarded); this tick is never compared.");
        }

        /// <summary>
        /// Turn ONE completed collector verdict into wire actions + the Story-1.9b observability counters. Extracted
        /// from <see cref="OnChecksum"/> (Story 9.6) so the disconnect-driven re-tally path (<see cref="DropReporter"/>)
        /// routes its now-complete windows through the SAME clean/alert/HALT logic — keeping
        /// <see cref="WindowsCompared"/>/<see cref="DesyncCount"/>/<see cref="Passing"/> alive over the reduced quorum.
        /// </summary>
        private void ProcessVerdict(uint tick, ServerChecksumCollector.Verdict v)
        {
            // Story 1.9b review: every completed comparison window counts toward the total the MATCH SUMMARY reports
            // (clean + diverged); DesyncCount tracks the diverged subset (clean = WindowsCompared − DesyncCount).
            WindowsCompared++;

            if (v.HasMajority && v.Minority.Count == 0)
            {
                if (ExpectedPeerCount == 1)
                {
                    // DW-414: the quorum has floored to a single reporter (the post-drop 1v1 state). The window
                    // completing is LIVENESS only — a lone reporter is trivially its own majority, so nothing was
                    // cross-attested. Say so explicitly instead of emitting a PASS-shaped line a human cannot
                    // distinguish from genuine cross-peer agreement.
                    SingleReporterWindows++;
                    _log.Info($"[Determinism] tick {tick}: single reporter 0x{v.Canonical:X8} — attestation " +
                              $"suspended (no comparison peer; liveness only, INCONCLUSIVE) (window #{WindowsCompared}).");
                }
                else
                {
                    // Story 1.9b: ALL expected peers agreed → the positive PASS evidence (FR-39). This is the line a
                    // human reads on the dedicated-server console to confirm the match is still in lockstep.
                    _log.Info($"[Determinism] tick {tick}: all {ExpectedPeerCount} peers matched 0x{v.Canonical:X8} (window #{WindowsCompared}).");
                }
            }
            else if (v.HasMajority)
            {
                // Majority + named minority (N≥3): alert each diverged peer in ascending (stable) slot order. The
                // match is NOT halted here — the majority plays on — but the verdict is now FAIL.
                DesyncCount++;
                foreach (int s in v.Minority)
                    _sendReliableTo(s, TickCommandPacket.MakeDesyncAlert(tick, v.Canonical));
                _log.Warn($"[Determinism] tick {tick}: DESYNC — minority slot(s) {string.Join(",", v.Minority)} diverged from canonical 0x{v.Canonical:X8}.");

                // DW-237 — REBASE THE QUORUM ONTO THE SURVIVING MAJORITY. A DesyncAlert is TERMINAL client-side
                // (LockstepManager.HandlePacket → RaiseHalt): the alerted peer stops advancing its sim and therefore
                // stops sending checksums, yet it stays CONNECTED — so the disconnect-driven DropReporter never runs,
                // the collector keeps counting it as an expected reporter, and NO later bucket can ever complete. The
                // survivors' desync guard would be silently dead for the rest of the match (self-healing only if the
                // human closes the halt overlay and the peer actually disconnects). Dropping the alerted reporter here
                // keeps the majority's guard live at the reduced quorum, and re-tallies any window that is now
                // complete without it. Idempotent with the later disconnect-driven DropReporter for the same slot.
                // NOTE: this is bound to the alert being terminal — if a DesyncAlert ever becomes recoverable
                // (rejoin/resync), the alerted reporter must be re-admitted instead of dropped here.
                foreach (int s in v.Minority)
                {
                    if (Halted) break; // a HALT (from a re-tally below) is terminal — stop rebasing
                    IReadOnlyList<(uint tick, ServerChecksumCollector.Verdict v)> reTallied =
                        _collector.DropExpectedReporter(s);
                    _log.Warn($"[Determinism] tick {tick}: slot {s} dropped from the checksum quorum (an alerted " +
                              $"minority HALTs locally but stays connected) — quorum rebased to {ExpectedPeerCount} reporter(s).");
                    RouteReTalliedWindows(reTallied);
                }
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
        /// Story 9.6 — drop <paramref name="slot"/> from the checksum quorum on a mid-match disconnect. Lowers the
        /// collector's expected-reporter count (floor 1), ignores that reporter's later stale reports, and re-tallies
        /// any in-flight window the reduced quorum now completes — routing each through <see cref="ProcessVerdict"/>
        /// so <see cref="WindowsCompared"/>/<see cref="Passing"/> keep advancing over the survivors (no silent quorum
        /// freeze, no false HALT). A no-op once <see cref="Halted"/>.
        ///
        /// <para>When the drop floors the quorum to a single reporter (the 1v1 case), the windows that keep completing
        /// are LIVENESS / observability — proof the lone survivor's sim is still advancing — NOT cross-peer
        /// attestation: a lone reporter is trivially its own majority, so there is no comparison peer left to attest
        /// agreement against. See <see cref="ServerChecksumCollector.DropExpectedReporter"/>. DW-414: those windows
        /// are SURFACED as such — the per-window line reads "attestation suspended" (not the PASS-shaped peer-match
        /// line), they are counted in <see cref="SingleReporterWindows"/>, and <see cref="LogSummary"/> reports them
        /// separately so the MATCH SUMMARY cannot over-claim enforcement.</para>
        /// </summary>
        public void DropReporter(int slot)
        {
            if (Halted) return;
            RouteReTalliedWindows(_collector.DropExpectedReporter(slot));
        }

        /// <summary>
        /// Story 15-1 (D-4) — re-admit a REJOINED player to the checksum quorum, keyed to the first window at/after
        /// <paramref name="fromTick"/> (its resume boundary): the exact dual of <see cref="DropReporter"/>, called
        /// at resume commit. Windows below the boundary keep quoruming over the survivors; the rejoiner's first
        /// live window then re-proves state agreement end-to-end (D-1 — a corrupt donor snapshot desyncs LOUDLY
        /// here rather than silently). Adding a reporter can never complete an in-flight window (it only raises
        /// the bar), so there is nothing to re-tally. No-op once <see cref="Halted"/> (nothing left to re-prove).
        /// </summary>
        public void AddReporter(int slot, uint fromTick)
        {
            if (Halted) return;
            if (_collector.AddExpectedReporter(slot, fromTick))
                _log.Info($"[Determinism] slot {slot} re-admitted to the checksum quorum from tick {fromTick} " +
                          $"(rejoin) — quorum re-raised to {ExpectedPeerCount} reporter(s).");
        }

        /// <summary>
        /// Route the windows a quorum reduction just completed (ascending by tick) through the shared
        /// <see cref="ProcessVerdict"/> logic. Shared by the disconnect path (<see cref="DropReporter"/>) and the
        /// DW-237 alerted-minority rebase so both keep the observability counters and the alert/HALT behavior
        /// identical. Stops at the first HALT — a HALT is terminal.
        /// </summary>
        private void RouteReTalliedWindows(IReadOnlyList<(uint tick, ServerChecksumCollector.Verdict v)> windows)
        {
            foreach ((uint tick, ServerChecksumCollector.Verdict v) in windows)
            {
                if (Halted) break; // a HALT is terminal — stop routing further re-tallied windows
                ProcessVerdict(tick, v);
            }
        }

        /// <summary>
        /// Emit the terminal FR-39 verdict line. Call on match end / player disconnect / server shutdown so a human
        /// reading the dedicated-server console sees the summary: "{N} windows compared, {D} desync, {A} abandoned —
        /// PASS|FAIL|INCONCLUSIVE". INCONCLUSIVE when no window was ever completed — nothing was actually compared, so
        /// it is NOT a clean pass (Story 1.9b review: a 0-window match must not masquerade as PASS in a console/log
        /// scan). DW-239: the abandoned count is reported too — those windows were never compared, so a non-zero
        /// abandoned count qualifies how much of the match the PASS actually covers (it is NOT a desync and does not
        /// flip the verdict).
        ///
        /// <para>DW-414: single-reporter (post-drop floor-1) windows are broken out of the compared total — they are
        /// liveness, not attestation. When EVERY completed window was single-reporter the verdict is INCONCLUSIVE
        /// (nothing was ever cross-attested); when a zero-desync match mixes attested and single-reporter windows the
        /// PASS names the attested count and marks the rest attestation-suspended, so the summary can no longer
        /// over-claim determinism enforcement after a 1v1 drop. A match with no drop keeps the exact legacy format.</para>
        ///
        /// <para>DW-511: a non-zero <see cref="FarFutureChecksumRejections"/> is appended too — those reports were
        /// dropped, not compared, so they do not move the verdict, but a human must see that a peer tried to blind
        /// the guard. Zero rejections (every honest match) leaves the line byte-identical to the legacy format.</para>
        /// </summary>
        public void LogSummary()
        {
            int attested = WindowsCompared - SingleReporterWindows; // windows with >= 2 comparing reporters
            string counts = SingleReporterWindows > 0
                ? $"{WindowsCompared} windows compared ({attested} cross-peer attested, {SingleReporterWindows} single-reporter)"
                : $"{WindowsCompared} windows compared";
            string verdict =
                WindowsCompared == 0 ? "INCONCLUSIVE"
                : !Passing ? "FAIL"
                : attested == 0 ? "INCONCLUSIVE (single reporter — attestation suspended, liveness only)"
                : SingleReporterWindows > 0
                    ? $"PASS over the {attested} attested window(s) ({SingleReporterWindows} single-reporter window(s) are liveness, not attestation)"
                    : "PASS";
            // DW-511: only surfaces when a peer actually tried it, so a clean match keeps the exact legacy format.
            string rejected = FarFutureChecksumRejections > 0
                ? $", {FarFutureChecksumRejections} far-future report(s) rejected"
                : "";
            _log.Info($"[Determinism] MATCH SUMMARY: {counts}, {DesyncCount} desync, " +
                      $"{AbandonedWindows} abandoned{rejected} — {verdict}.");
        }
    }
}
