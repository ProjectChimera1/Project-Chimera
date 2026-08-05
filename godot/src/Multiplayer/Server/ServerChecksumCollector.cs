#nullable enable
using System;
using System.Collections.Generic;

namespace ProjectChimera.Multiplayer.Server
{
    /// <summary>
    /// Server-side strict-majority desync collector (AR-40 fork #2, Story 1.9a). Buffers slot-tagged 32-bit
    /// checksums per EXECUTED sim tick within a bounded window; when all expected peers have reported for a
    /// tick it declares the strict-majority (<c>&gt; N/2</c>) hash canonical and names the minority slot(s),
    /// or "no canonical" on no majority. N-shaped (any N≥2; <see cref="MaxSlots"/> — see the constant's own doc
    /// for why that ceiling is the MP SEAT ceiling and NOT the sim's larger faction ceiling).
    ///
    /// <para>This is server-side networking — NOT part of the 30 Hz sim tick — so it is exempt from the in-tick
    /// determinism rules (it allocates the minority list lazily). BUT its OUTPUT is order-stable: the minority
    /// is built by scanning slots ASCENDING, so attribution is reproducible. The wire/checksum stays 32-bit
    /// <c>uint</c> (D12 — no widening). Slot is TRANSPORT-AUTHORITATIVE; the caller never reads it from the
    /// packet payload (D5).</para>
    /// </summary>
    public sealed class ServerChecksumCollector
    {
        /// <summary>
        /// The REPORTING-PLAYER seat ceiling: an ALIAS of <c>PlayerCountPolicy.MpSeatCeiling</c> (== the Godot-coupled
        /// <c>ServerTransport.MAX_PLAYERS</c>), NOT <c>ServerTransport.MAX_SLOTS</c>. Those are different numbers:
        /// MAX_SLOTS is 8 because slots <c>MAX_PLAYERS..MAX_SLOTS-1</c> are SPECTATOR seats, and spectators are
        /// deliberately excluded from the quorum (D6) — so a collector sized to MAX_SLOTS would wait forever on
        /// reporters that never report.
        ///
        /// <para>The MP seat ceiling is deliberately SMALLER than the sim's faction ceiling (which did go to 8): see
        /// <c>PlayerCountPolicy</c>, "THE transport seat ceiling — the single documented 4→8 bump point". Because this
        /// is an alias and not a copy, that ONE bump moves this collector with it — there is no second edit to forget.
        /// No literal is restated here, so nothing on this line can go stale at the bump.</para>
        ///
        /// <para>DW-513: this WAS a hardcoded literal. <c>NoHardcodedPlayerCountTests</c>' source scan could only see
        /// <c>const int … = &lt;literal&gt;</c>, so aliasing made the scan MISS the site and fail its vacuous-pass
        /// "every allowlisted site was found" assertion — an earlier alias attempt was reverted for exactly that
        /// reason. That scan now also recognizes an alias of a sanctioned player-count constant, so the alias is the
        /// sanctioned shape here. <c>TwoCeilingPolicy_ConstantsAgree</c> still asserts the equality, so a future
        /// re-literalization that drifts is still caught.</para>
        /// </summary>
        public const int MaxSlots = PlayerCountPolicy.MpSeatCeiling;

        /// <summary>
        /// Ring of recent per-tick buckets. Only ever holds the small number of checksum ticks that are
        /// simultaneously "in flight" (in practice ≤2, since all peers report the same executed tick before
        /// any advances to the next checksum interval). A spread larger than this window means a peer is a
        /// full interval behind → its late report is genuinely stale and dropped.
        /// </summary>
        private const int Window = 8;

        /// <summary>
        /// DW-511 — how far past the SERVER-AUTHORITATIVE tick frontier a reported checksum tick may key before it
        /// is rejected as implausible (see <see cref="_tickFrontier"/>). This is the checksum-path sibling of
        /// <c>MergedTickBuilder.ACCEPT_WINDOW</c>, but it is measured from a REAL frontier rather than from
        /// <see cref="_resolvedThrough"/> — see <see cref="IsBeyondAcceptWindow"/> for why the naive copy is wrong.
        ///
        /// <para>Sizing (this is the honest-client lead, not a fudge factor). A checksum tick is an EXECUTED tick,
        /// and a lockstep client may only execute tick T once it holds the merged commands for T — EXCEPT for the
        /// ticks it SELF-SEEDS empty (<c>MergedArrivalRing.SeedBootstrap</c>: ticks <c>0..delay-1</c>, which the
        /// server never emits; and <c>SeedDelayGap</c>: the <c>newDelay-oldDelay</c> ticks a GROWING delay change
        /// opens, which no peer sends for). Both gaps are bounded by the delay clamp, so an honest client's exec
        /// frontier can lead the server's emitted frontier by at most <see cref="DelayMath.MAX_DELAY"/> ticks and
        /// never more. Aliasing the constant keeps the two from drifting if the delay clamp ever moves.</para>
        /// </summary>
        private const int ACCEPT_SLACK = DelayMath.MAX_DELAY;

        // DW-239: a ring overrun (a newer tick re-keying a still-incomplete older bucket) ABANDONS that older
        // comparison window — its partial reports are discarded and no verdict can ever be emitted for it. Nothing
        // recorded that before, so windows silently vanished from the FR-39 evidence trail (a "300 windows compared,
        // 0 desync — PASS" readout could hide any number of never-compared windows). The abandonment is now counted,
        // surfaced through this seam, and made MONOTONIC via _resolvedThrough (below).
        private readonly Action<uint, int>? _onWindowAbandoned;

        /// <summary>
        /// The verdict returned by <see cref="Record"/> once a tick's bucket fills (all expected peers reported).
        /// Until then <see cref="Complete"/> is false (see <see cref="Pending"/>).
        /// </summary>
        public readonly struct Verdict
        {
            /// <summary>True once all expected peers have reported for this tick.</summary>
            public bool Complete { get; }
            /// <summary>True when a strict majority (<c>&gt; N/2</c>) agreed on a single hash.</summary>
            public bool HasMajority { get; }
            /// <summary>The strict-majority hash (meaningful only when <see cref="HasMajority"/> is true).</summary>
            public uint Canonical { get; }
            /// <summary>Reported slots whose hash != <see cref="Canonical"/>, in ASCENDING slot order (stable attribution).</summary>
            public IReadOnlyList<int> Minority { get; }

            public Verdict(bool complete, bool hasMajority, uint canonical, IReadOnlyList<int> minority)
            {
                Complete = complete;
                HasMajority = hasMajority;
                Canonical = canonical;
                Minority = minority;
            }

            /// <summary>The incomplete result: the tick is still waiting on at least one expected peer (or the input was stale/duplicate).</summary>
            public static readonly Verdict Pending = new(false, false, 0u, Array.Empty<int>());
        }

        /// <summary>One per-tick bucket: a hash per slot + a reported flag per slot.</summary>
        private sealed class Bucket
        {
            public bool Active;
            public uint TickOf;
            public readonly uint[] Hash = new uint[MaxSlots];
            public readonly bool[] Got = new bool[MaxSlots];
            public int Count;

            public void Reset(uint tick)
            {
                Active = true;
                TickOf = tick;
                Count = 0;
                for (int i = 0; i < MaxSlots; i++) { Hash[i] = 0u; Got[i] = false; }
            }
        }

        // Mutable since Story 9.6: a mid-match disconnect lowers the quorum via DropExpectedReporter (floor 1). The
        // ctor invariant [2, MaxSlots] still holds at construction — only the drop path can reach 1.
        private int _expected;
        private readonly Bucket[] _ring;

        // Story 9.6: reporters removed from the quorum on disconnect. An excluded slot's (stale) reports are
        // ignored by Record, and its contribution is cleared from any active bucket by DropExpectedReporter.
        private readonly bool[] _excluded = new bool[MaxSlots];

        // Highest tick this collector will ever act on again; -1 = none. Advanced when a verdict is emitted AND
        // (DW-239) when a ring overrun abandons an older incomplete bucket. Any incoming tick at or below this is
        // already resolved / abandoned / stale and is dropped — this implements the "stale checksums for
        // non-matching ticks are dropped" rule, prevents an evicted bucket from being re-completed twice, and keeps
        // verdict emission MONOTONIC (a window older than an abandoned one can no longer surface after it, which
        // would hand the caller an out-of-order verdict for a window the ring had already given up on).
        private long _resolvedThrough = -1;

        // DW-511: the SERVER-AUTHORITATIVE tick frontier a reported checksum tick is bounded against (−1 = nothing
        // confirmed yet). Null = no frontier wired ⇒ unbounded acceptance (the pre-DW-511 behavior), which is only
        // safe for a trusted in-process harness where every reporter is our own code. Production (DedicatedServer)
        // wires MergedTickBuilder.EmittedThrough — see IsBeyondAcceptWindow.
        private readonly Func<long>? _tickFrontier;

        /// <summary>
        /// Create a collector expecting <paramref name="expectedPeerCount"/> reporting player peers (spectators
        /// are excluded — D6). N=2 ⇒ a 1-vs-1 mismatch is NOT a majority. Throws if the count is outside [2, MaxSlots].
        /// </summary>
        /// <param name="expectedPeerCount">Reporting player peers to quorum over, in [2, <see cref="MaxSlots"/>].</param>
        /// <param name="onWindowAbandoned">
        /// DW-239 — optional observability seam invoked as <c>(abandonedTick, reportsLost)</c> when a ring overrun
        /// discards a still-incomplete comparison window (a peer fell a full <see cref="Window"/> behind). Never
        /// fires for an empty bucket (nothing was lost). The collector owns no log sink, so the caller
        /// (<see cref="ServerHost"/>) turns this into the console line + the MATCH SUMMARY count.
        /// </param>
        /// <param name="tickFrontier">
        /// DW-511 — the SERVER-AUTHORITATIVE confirmed tick high-water (<c>MergedTickBuilder.EmittedThrough</c>;
        /// −1 when nothing has been confirmed yet). Supplying it ARMS the far-future acceptance bound: a reported
        /// checksum tick more than <see cref="ACCEPT_SLACK"/> past this frontier is impossible for an honest client
        /// and is DROPPED (see <see cref="IsBeyondAcceptWindow"/>). Omitting it keeps the legacy UNBOUNDED
        /// acceptance and is only appropriate where every reporter is trusted in-process code (unit tests, the
        /// loopback self-test) — a real transport MUST wire it, or one client can blind the whole desync guard.
        /// </param>
        public ServerChecksumCollector(int expectedPeerCount, Action<uint, int>? onWindowAbandoned = null,
                                       Func<long>? tickFrontier = null)
        {
            if (expectedPeerCount < 2 || expectedPeerCount > MaxSlots)
                throw new ArgumentOutOfRangeException(nameof(expectedPeerCount),
                    $"expectedPeerCount must be in [2, {MaxSlots}] (got {expectedPeerCount}).");
            _expected = expectedPeerCount;
            _onWindowAbandoned = onWindowAbandoned;
            _tickFrontier = tickFrontier;
            _ring = new Bucket[Window];
            for (int i = 0; i < Window; i++) _ring[i] = new Bucket();
        }

        /// <summary>Number of reporting peers this collector quorums over.</summary>
        public int ExpectedPeerCount => _expected;

        /// <summary>
        /// DW-239 — comparison windows the ring ABANDONED before every expected peer reported (a newer tick overran
        /// a still-incomplete older bucket). These windows are never compared and never yield a verdict, so they are
        /// NOT part of the compared/desync totals; they are the "how much did we fail to verify" counter.
        /// </summary>
        public int AbandonedWindows { get; private set; }

        /// <summary>
        /// DW-511 — reports DROPPED for keying implausibly far past the server-authoritative tick frontier (only
        /// ever non-zero once a <c>tickFrontier</c> is wired). A correct lockstep client can never produce one, so
        /// a non-zero count is misbehaviour — the grief attempt this guard exists to stop. Nothing was compared and
        /// nothing was lost, so it is neither a desync nor an abandoned window; the caller
        /// (<see cref="ServerHost"/>) turns it into a BOUNDED console line + a MATCH SUMMARY note.
        /// </summary>
        public int FarFutureRejections { get; private set; }

        /// <summary>
        /// Record one peer's checksum for an EXECUTED tick. Stale inputs (a tick already resolved, or an older
        /// tick colliding with a live newer bucket), implausibly-far-future inputs (DW-511) and duplicate
        /// <c>(slot,tick)</c> inputs are ignored. Returns <see cref="Verdict.Pending"/> until every expected peer
        /// has reported for <paramref name="tick"/>, then returns the completed verdict exactly once and evicts
        /// the bucket.
        /// </summary>
        public Verdict Record(uint tick, int slot, uint hash)
        {
            if ((uint)slot >= MaxSlots) return Verdict.Pending;        // defensive: slot is transport-authoritative
            if (_excluded[slot]) return Verdict.Pending;               // Story 9.6: a dropped reporter's stale reports are ignored
            if ((long)tick <= _resolvedThrough) return Verdict.Pending; // already resolved / stale → drop

            // DW-511: the FUTURE bound, mirroring the stale-past bound above. A tick no honest client can have
            // executed yet must never reach the ring — see IsBeyondAcceptWindow for the attack it stops.
            if (IsBeyondAcceptWindow(tick)) { FarFutureRejections++; return Verdict.Pending; }

            int idx = (int)(tick % Window);
            Bucket b = _ring[idx];

            if (b.Active && b.TickOf != tick)
            {
                if (b.TickOf > tick) return Verdict.Pending; // older tick colliding with a live newer bucket → stale
                AbandonWindow(b);                            // DW-239: record + close out the window we are about to lose
                b.Reset(tick);                               // newer tick overruns an older incomplete bucket → reuse
            }
            else if (!b.Active)
            {
                b.Reset(tick);
            }

            if (b.Got[slot]) return Verdict.Pending;         // duplicate (slot,tick) → idempotent no-op

            b.Hash[slot] = hash;
            b.Got[slot] = true;
            b.Count++;

            if (b.Count < _expected) return Verdict.Pending; // still waiting on peers

            Verdict v = Tally(b);
            b.Active = false;                                // evict the completed bucket
            _resolvedThrough = tick;                         // a verdict was emitted for this tick
            return v;
        }

        /// <summary>
        /// Story 9.6 — drop <paramref name="slot"/> from the reporting quorum on a mid-match disconnect. Marks the
        /// slot excluded (its later stale reports are ignored by <see cref="Record"/>), clears its contribution from
        /// every ACTIVE bucket (<c>Got</c>/<c>Hash</c>/<c>Count</c>), lowers the expected reporter count (floor 1),
        /// then re-tallies any active bucket that is now complete under the reduced quorum — returning those
        /// verdicts (ascending by tick) so the caller can route them exactly as if <see cref="Record"/> had
        /// completed them. Idempotent: dropping an already-excluded (or out-of-range) slot is a no-op returning an
        /// empty list.
        ///
        /// <para><b>What "continued PASS windows" mean once the quorum floors to 1.</b> After a 1v1 drop the survivor
        /// is the ONLY reporter, and a lone reporter is TRIVIALLY its own strict majority (<c>needed = 1/2+1 = 1</c>).
        /// So the windows that keep completing are LIVENESS / observability — proof the survivor's sim is still
        /// advancing and the collector did not silently freeze on a reporter that will never report again — NOT
        /// cross-peer determinism ATTESTATION (there is no second peer left to compare against). This avoids the false
        /// HALT / silent-stall failure mode, but it does not (and cannot) attest agreement with a peer that is gone.</para>
        /// </summary>
        public IReadOnlyList<(uint tick, Verdict v)> DropExpectedReporter(int slot)
        {
            var results = new List<(uint tick, Verdict v)>();
            if ((uint)slot >= MaxSlots || _excluded[slot]) return results;

            _excluded[slot] = true;
            if (_expected > 1) _expected--; // floor 1 (the drop path is the ONLY way below 2)

            long floor = _resolvedThrough; // capture once — advancing it mid-scan could wrongly evict a lower-tick bucket
            long maxResolved = _resolvedThrough;
            for (int i = 0; i < Window; i++)
            {
                Bucket b = _ring[i];
                if (!b.Active) continue;

                // Remove the dropped reporter's contribution from this in-flight bucket.
                if (b.Got[slot])
                {
                    b.Got[slot]  = false;
                    b.Hash[slot] = 0u;
                    b.Count--;
                }

                // A bucket already resolved (or stale, vs the pre-scan floor) is never re-completed; a bucket with
                // no remaining reporters stays pending. Otherwise the reduced quorum may now complete it. NOTE: a
                // bucket whose Count fell to 0 here (only the dropped slot had reported it) is left Active-but-
                // HARMLESS — Tally requires Count >= _expected >= 1 to ever fire, so a 0-count bucket can never
                // falsely tally; it simply waits for the survivor's own report (or is re-keyed forward by Record).
                if ((long)b.TickOf <= floor) { b.Active = false; continue; }
                if (b.Count > 0 && b.Count >= _expected)
                {
                    Verdict v = Tally(b);
                    b.Active = false;
                    if ((long)b.TickOf > maxResolved) maxResolved = b.TickOf;
                    results.Add((b.TickOf, v));
                }
            }

            _resolvedThrough = maxResolved;
            // Ascending by tick so the caller routes re-tallied windows in a stable, monotonic order.
            results.Sort((x, y) => x.tick.CompareTo(y.tick));
            return results;
        }

        /// <summary>
        /// DW-511 — true when <paramref name="tick"/> keys implausibly far past the server-authoritative frontier
        /// and must be DROPPED at the door.
        ///
        /// <para><b>The attack.</b> <see cref="Record"/> used to accept ANY tick a client claimed. A single
        /// misbehaving client reporting a far-future tick (a) EVICTS whichever in-flight bucket its ring index
        /// lands on — destroying an honest comparison window the rest of the quorum was mid-way through — and,
        /// worse, (b) drags <see cref="_resolvedThrough"/> up to that far-future value, after which EVERY honest
        /// report is dropped by the cheap stale check for the rest of the match: the desync guard is silently dead
        /// and the FR-39 evidence trail flatlines. It is the checksum-path sibling of the command-path grief
        /// vectors, and it needs no majority — one client is enough.</para>
        ///
        /// <para><b>Why the frontier is REAL and not <see cref="_resolvedThrough"/>.</b> Copying
        /// <c>MergedTickBuilder.ACCEPT_WINDOW</c> (a window measured from that builder's own emitted high-water)
        /// is WRONG here: the builder's high-water tracks a frontier that advances every tick, while this
        /// collector's <see cref="_resolvedThrough"/> starts at −1 and only moves when a checksum window resolves —
        /// and real checksum ticks start at the checksum interval (60), not at 0. A window measured from −1 would
        /// therefore reject the FIRST legitimate report of every match and the guard would deadlock the quorum it
        /// was meant to protect. Instead the bound is measured against an externally supplied, server-owned
        /// frontier (<c>MergedTickBuilder.EmittedThrough</c> — the highest tick whose merged commands the server
        /// actually broadcast), which no client can move on its own: a merged tick is emitted only when ALL
        /// expected players have submitted it. A checksum tick is an EXECUTED tick and a client can only execute
        /// what it has been given, so <c>tick ≤ frontier + ACCEPT_SLACK</c> holds for every honest report (the
        /// slack covers the client-self-seeded bootstrap/delay-growth gaps — see <see cref="ACCEPT_SLACK"/>).</para>
        ///
        /// <para>With no frontier wired the bound is disabled and acceptance stays unbounded (the documented
        /// trusted-harness default) — a real transport must supply one.</para>
        /// </summary>
        private bool IsBeyondAcceptWindow(uint tick)
        {
            if (_tickFrontier == null) return false; // no server-authoritative frontier wired → legacy unbounded accept
            return (long)tick > _tickFrontier() + ACCEPT_SLACK;
        }

        /// <summary>
        /// DW-239 — close out a comparison window the ring is about to discard (a newer tick overran this still-
        /// incomplete older bucket, so its partial reports are lost and no verdict can ever be emitted for it).
        ///
        /// <para>Two effects. (1) OBSERVABILITY: counts it in <see cref="AbandonedWindows"/> and notifies the
        /// injected seam — before this, an abandoned window vanished silently, so the FR-39 evidence trail could
        /// read "k windows compared, 0 desync — PASS" while quietly never comparing a stretch of the match. An
        /// EMPTY bucket (a <see cref="DropExpectedReporter"/> leftover whose only reporter was the dropped slot)
        /// lost nothing, so it is closed out without being counted or reported. (2) MONOTONICITY: advances
        /// <see cref="_resolvedThrough"/> to the abandoned tick, so every later report at or below it is dropped by
        /// the cheap stale check instead of resurrecting a window OLDER than one the ring already gave up on (which
        /// would hand the caller a verdict out of tick order). Any bucket still sitting at or below the new
        /// high-water is by definition ≥ <see cref="Window"/> ticks behind the reporting frontier — the documented
        /// "genuinely stale" case — and is swept on the next <see cref="DropExpectedReporter"/>.</para>
        /// </summary>
        private void AbandonWindow(Bucket b)
        {
            if (b.Count > 0)
            {
                AbandonedWindows++;
                _onWindowAbandoned?.Invoke(b.TickOf, b.Count);
            }
            if ((long)b.TickOf > _resolvedThrough) _resolvedThrough = (long)b.TickOf;
        }

        /// <summary>
        /// Tally a full bucket: find a hash held by a strict majority (<c>&gt; N/2</c>) of reporting slots and name
        /// the minority. Scans slots ASCENDING for a deterministic minority order (no Dictionary enumeration).
        /// </summary>
        private Verdict Tally(Bucket b)
        {
            int needed = _expected / 2 + 1; // strict majority of N: count > N/2  ⟺  count ≥ floor(N/2)+1, for any N

            for (int i = 0; i < MaxSlots; i++)
            {
                if (!b.Got[i]) continue;
                uint candidate = b.Hash[i];
                int count = 0;
                for (int j = 0; j < MaxSlots; j++)
                    if (b.Got[j] && b.Hash[j] == candidate) count++;

                if (count >= needed)
                {
                    // Build the minority (reported slots disagreeing with the canonical), ascending.
                    var minority = new List<int>();
                    for (int j = 0; j < MaxSlots; j++)
                        if (b.Got[j] && b.Hash[j] != candidate) minority.Add(j);
                    return new Verdict(true, true, candidate, minority);
                }
            }

            // No hash reached a strict majority → global desync, no canonical.
            return new Verdict(true, false, 0u, Array.Empty<int>());
        }
    }
}
