#nullable enable

namespace ProjectChimera.Multiplayer.Server
{
    /// <summary>
    /// DW-392 — the dedicated server's per-peer protocol-violation counter. Lives under
    /// <c>src/Multiplayer/Server/**</c> (Godot-free, Tier-1 + determinism-analyzer globbed) like
    /// <see cref="CommandRateLimiter"/>; the Godot <c>DedicatedServer</c> node is a thin adapter feeding it the
    /// transport-authoritative slot.
    ///
    /// Purpose: BOUND attacker-triggerable log writes. Before this class, every misbehavior arm on the server's
    /// packet dispatch (a client-sent <c>TickCommandsMerged</c> spoof, an undecodable Chat/LobbyChat) wrote one
    /// <c>GD.PrintErr</c> line PER PACKET — a soft log-write DoS a malicious client could drive at wire rate —
    /// while the merged fan-in's drop arms (faction spoof / over-count / malformed / replayed tick →
    /// <c>MergedTickBuilder.Submit</c> false) were fully SILENT (server-invisible griefing). Each such arm now
    /// calls <see cref="Record"/>, which tallies the violation per peer and returns whether THIS one should be
    /// logged — the 1st, then every <see cref="LOG_EVERY"/>-th — so a flood of V violations writes only
    /// 1 + V/<see cref="LOG_EVERY"/> lines while low-volume misbehavior still leaves an immediate trace.
    ///
    /// The tally is diagnostic-only: it never enters the simulation, the merged builder, <c>SimChecksum</c>, or
    /// any determinism path. Integer-only, allocation-free after construction, keyed by the
    /// transport-authoritative slot (never a packet byte). The count is per CONNECTION: <see cref="Reset"/> clears
    /// a slot on connect so a recycled slot's new occupant never inherits (nor is judged by) the prior occupant's
    /// tally — which also readies the counter for a future disconnect-after-N escalation policy (the DW-392
    /// ledger's "and/or" arm; a deliberate non-goal today under the trusted-friends EA posture).
    /// </summary>
    public sealed class ProtocolViolationTracker
    {
        /// <summary>Log cadence: a violation is logged when it is the slot's 1st, then every LOG_EVERY-th —
        /// matching the Story-9.13 throttled-drop diagnostic cadence (<c>RATE_LIMIT_LOG_EVERY</c>). A wire-rate
        /// flood of V violations therefore writes only 1 + V/128 log lines.</summary>
        public const long LOG_EVERY = 128;

        private readonly int    _slots;
        private readonly long[] _count; // violations recorded this connection, per slot

        /// <param name="slots">Number of transport slots to track (sized to <c>ServerTransport.MAX_SLOTS</c>).</param>
        public ProtocolViolationTracker(int slots)
        {
            if (slots < 1)
                throw new System.ArgumentOutOfRangeException(nameof(slots), slots, "slots must be >= 1.");
            _slots = slots;
            _count = new long[slots];
        }

        /// <summary>Number of slots this tracker covers.</summary>
        public int Slots => _slots;

        /// <summary>
        /// Record one protocol violation by <paramref name="slot"/>. Returns <c>true</c> when THIS violation should
        /// be logged (the slot's 1st, then every <see cref="LOG_EVERY"/>-th) — the caller writes its diagnostic
        /// only on <c>true</c>, bounding attacker-triggerable log writes. An out-of-range slot records nothing and
        /// never logs (fail-quiet: the transport only hands the adapter an in-range slot).
        /// </summary>
        public bool Record(int slot)
        {
            if ((uint)slot >= (uint)_slots) return false;
            long c = ++_count[slot];
            return c == 1 || c % LOG_EVERY == 0;
        }

        /// <summary>Violations recorded for <paramref name="slot"/> this connection (0 for an out-of-range slot).</summary>
        public long Count(int slot) => (uint)slot < (uint)_slots ? _count[slot] : 0L;

        /// <summary>
        /// Clear <paramref name="slot"/>'s tally so a recycled slot's NEW occupant starts clean (SoA-recycle-trap
        /// discipline; called from <c>HandleConnect</c>). Unlike <see cref="CommandRateLimiter.DroppedCount"/>'s
        /// lifetime tally this one IS cleared: the count attributes misbehavior to a PEER, not a slot, and a future
        /// disconnect-after-N policy must never judge a fresh occupant by its predecessor. Out-of-range: no-op.
        /// </summary>
        public void Reset(int slot)
        {
            if ((uint)slot >= (uint)_slots) return;
            _count[slot] = 0;
        }
    }
}
