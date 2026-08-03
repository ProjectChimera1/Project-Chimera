#nullable enable
using System;
using System.Collections.Generic;

namespace ProjectChimera.Multiplayer.Server
{
    /// <summary>
    /// Server-side strict-majority desync collector (AR-40 fork #2, Story 1.9a). Buffers slot-tagged 32-bit
    /// checksums per EXECUTED sim tick within a bounded window; when all expected peers have reported for a
    /// tick it declares the strict-majority (<c>&gt; N/2</c>) hash canonical and names the minority slot(s),
    /// or "no canonical" on no majority. N-shaped (any N≥2; <see cref="MaxSlots"/>=4 — see the constant's own doc
    /// for why that 4 is the MP SEAT ceiling and NOT the sim's 8-faction ceiling).
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
        /// The REPORTING-PLAYER seat ceiling: mirrors <c>PlayerCountPolicy.MpSeatCeiling</c> (== the Godot-coupled
        /// <c>ServerTransport.MAX_PLAYERS</c>), NOT <c>ServerTransport.MAX_SLOTS</c>. Those are different numbers:
        /// MAX_SLOTS is 8 because slots <c>MAX_PLAYERS..MAX_SLOTS-1</c> are SPECTATOR seats, and spectators are
        /// deliberately excluded from the quorum (D6) — so a collector sized to 8 would wait forever on reporters
        /// that never report.
        ///
        /// <para>The 4 is therefore NOT stale-pending-a-bump: the sim's faction ceiling did go to 8, but the MP seat
        /// ceiling was deliberately LEFT at 4 (see <c>PlayerCountPolicy</c>, "THE transport seat ceiling — the single
        /// documented 4→8 bump point"). Raise it there and raise this with it —
        /// <c>NoHardcodedPlayerCountTests.TwoCeilingPolicy_ConstantsAgree</c> asserts the two are EQUAL, so they
        /// cannot silently drift.</para>
        ///
        /// <para>Deliberately a LITERAL rather than <c>= PlayerCountPolicy.MpSeatCeiling</c>: the same test's
        /// <c>SourceScan_OnlyAllowlistedPlayerCountConstantsExist</c> half requires this site to be observable as an
        /// allowlisted <c>const int … = 4</c> declaration, and aliasing the constant makes the scan miss it and fail
        /// its vacuous-pass guard. Equality is enforced by assertion instead of by aliasing.</para>
        /// </summary>
        public const int MaxSlots = 4;

        /// <summary>
        /// Ring of recent per-tick buckets. Only ever holds the small number of checksum ticks that are
        /// simultaneously "in flight" (in practice ≤2, since all peers report the same executed tick before
        /// any advances to the next checksum interval). A spread larger than this window means a peer is a
        /// full interval behind → its late report is genuinely stale and dropped.
        /// </summary>
        private const int Window = 8;

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

        // Highest tick for which a verdict has already been emitted; -1 = none. Any incoming tick at or below
        // this is already resolved (or stale) and is dropped — this both implements the "stale checksums for
        // non-matching ticks are dropped" rule and prevents an evicted bucket from being re-completed twice.
        private long _resolvedThrough = -1;

        /// <summary>
        /// Create a collector expecting <paramref name="expectedPeerCount"/> reporting player peers (spectators
        /// are excluded — D6). N=2 ⇒ a 1-vs-1 mismatch is NOT a majority. Throws if the count is outside [2, MaxSlots].
        /// </summary>
        public ServerChecksumCollector(int expectedPeerCount)
        {
            if (expectedPeerCount < 2 || expectedPeerCount > MaxSlots)
                throw new ArgumentOutOfRangeException(nameof(expectedPeerCount),
                    $"expectedPeerCount must be in [2, {MaxSlots}] (got {expectedPeerCount}).");
            _expected = expectedPeerCount;
            _ring = new Bucket[Window];
            for (int i = 0; i < Window; i++) _ring[i] = new Bucket();
        }

        /// <summary>Number of reporting peers this collector quorums over.</summary>
        public int ExpectedPeerCount => _expected;

        /// <summary>
        /// Record one peer's checksum for an EXECUTED tick. Stale inputs (a tick already resolved, or an older
        /// tick colliding with a live newer bucket) and duplicate <c>(slot,tick)</c> inputs are ignored. Returns
        /// <see cref="Verdict.Pending"/> until every expected peer has reported for <paramref name="tick"/>, then
        /// returns the completed verdict exactly once and evicts the bucket.
        /// </summary>
        public Verdict Record(uint tick, int slot, uint hash)
        {
            if ((uint)slot >= MaxSlots) return Verdict.Pending;        // defensive: slot is transport-authoritative
            if (_excluded[slot]) return Verdict.Pending;               // Story 9.6: a dropped reporter's stale reports are ignored
            if ((long)tick <= _resolvedThrough) return Verdict.Pending; // already resolved / stale → drop

            int idx = (int)(tick % Window);
            Bucket b = _ring[idx];

            if (b.Active && b.TickOf != tick)
            {
                if (b.TickOf > tick) return Verdict.Pending; // older tick colliding with a live newer bucket → stale
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
