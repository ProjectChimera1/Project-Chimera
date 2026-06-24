#nullable enable
using System;
using System.Collections.Generic;

namespace ProjectChimera.Multiplayer.Server
{
    /// <summary>
    /// Server-side strict-majority desync collector (AR-40 fork #2, Story 1.9a). Buffers slot-tagged 32-bit
    /// checksums per EXECUTED sim tick within a bounded window; when all expected peers have reported for a
    /// tick it declares the strict-majority (<c>&gt; N/2</c>) hash canonical and names the minority slot(s),
    /// or "no canonical" on no majority. N-shaped (any N≥2; <see cref="MaxSlots"/>=4 in 1.0 — 8 is a constant
    /// bump + the Faction-enum extension in Story 9.2, not a rewrite).
    ///
    /// <para>This is server-side networking — NOT part of the 30 Hz sim tick — so it is exempt from the in-tick
    /// determinism rules (it allocates the minority list lazily). BUT its OUTPUT is order-stable: the minority
    /// is built by scanning slots ASCENDING, so attribution is reproducible. The wire/checksum stays 32-bit
    /// <c>uint</c> (D12 — no widening). Slot is TRANSPORT-AUTHORITATIVE; the caller never reads it from the
    /// packet payload (D5).</para>
    /// </summary>
    public sealed class ServerChecksumCollector
    {
        /// <summary>ServerTransport ceiling in 1.0 (N≤4; 8 = a constant bump, Story 9.2). Mirrors ServerTransport.MAX_SLOTS.</summary>
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

        private readonly int _expected;
        private readonly Bucket[] _ring;

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
