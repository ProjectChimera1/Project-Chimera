#nullable enable
using System;

namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// DW-390 — the Godot-free client-side merged-arrival ring/gate/seed decision, extracted from
    /// <see cref="LockstepManager"/> (the DelayMath / MergedTickBuilder-Applier extraction precedent) so the
    /// Story-9.3 client gate is Tier-1 unit-testable without Godot/ENet. <see cref="LockstepManager"/> now
    /// delegates to this — a behavior-neutral extraction; the ring semantics are byte-identical:
    ///
    /// <list type="bullet">
    ///   <item><b>Keying</b> (<see cref="TryStore"/>, formerly <c>HandleMergedTick</c>'s body): one authoritative
    ///     <c>TickCommandsMerged</c> per tick, keyed by <c>tick &amp; (BUFFER_SIZE-1)</c>. Raw bytes are copied
    ///     into preallocated ring buffers — the receive path never allocates.</item>
    ///   <item><b>Gate</b> (<see cref="IsReady"/>, formerly <c>Flush</c>'s stall/apply predicate): the sim may
    ///     advance through tick T only when the slot is occupied AND the occupant is FOR tick T. A wrong-tick
    ///     occupant (ring wraparound: a slot still holding tick T−BUFFER_SIZE, or already overwritten by
    ///     T+BUFFER_SIZE) therefore demotes to a STALL — never a mis-apply. Max delay
    ///     (<see cref="DelayMath.MAX_DELAY"/> = 12) &lt; <see cref="BUFFER_SIZE"/> (16) on a reliable-ordered
    ///     channel keeps a live slot from being overwritten before it executes.</item>
    ///   <item><b>Seeding</b> (<see cref="SeedBootstrap"/> / <see cref="SeedDelayGap"/>, formerly
    ///     <c>SeedInitialTicks</c> and <c>CommitDelayChange</c>'s gap loop): a seeded tick stores length 0 — an
    ///     empty merged packet, which <c>MergedTickApplier</c> decodes to nothing (a deterministic no-op) — and is
    ///     marked locally-sent so the client never emits a bundle for it.</item>
    /// </list>
    ///
    /// Godot-free (System.* + the <see cref="MergedTickPacket"/> codec only); single-file-included in
    /// SimSources.props so MergedArrivalRingTests pin stall-on-missing, apply-on-arrival, ring-wraparound, and
    /// the empty-seed no-op in Tier-1.
    /// </summary>
    internal sealed class MergedArrivalRing
    {
        /// <summary>
        /// Circular buffer slots. MUST stay a power of two strictly greater than <see cref="DelayMath.MAX_DELAY"/> + 1
        /// (the constraint formerly documented on LockstepManager's private const; MergedArrivalRingTests pins it) —
        /// otherwise a max-delay issue tick and its exec tick could collide in one slot.
        /// </summary>
        internal const int BUFFER_SIZE = 16;
        private  const int BUFFER_MASK = BUFFER_SIZE - 1;

        // ── Send-tracking (Story 9.3) — one "already sent for this issue tick" guard per ring slot. ──
        private readonly bool[] _localSent = new bool[BUFFER_SIZE];

        // ── Merged-arrival ring (Story 9.3) — one authoritative TickCommandsMerged per tick. ──
        private readonly bool[]   _arrived = new bool[BUFFER_SIZE];
        private readonly uint[]   _tickFor = new uint[BUFFER_SIZE];
        private readonly byte[][] _bytes;
        private readonly int[]    _len     = new int[BUFFER_SIZE];

        internal MergedArrivalRing()
        {
            _bytes = new byte[BUFFER_SIZE][];
            for (int i = 0; i < BUFFER_SIZE; i++)
                _bytes[i] = new byte[MergedTickPacket.MERGED_MAX_BYTES];
        }

        // ── Send tracking ─────────────────────────────────────────────────────

        /// <summary>True once <see cref="MarkSent"/> ran for <paramref name="issueTick"/>'s slot (one send per issue tick).</summary>
        internal bool HasSent(uint issueTick) => _localSent[Mod(issueTick)];

        /// <summary>Record that the local single-faction bundle for <paramref name="issueTick"/> was sent.</summary>
        internal void MarkSent(uint issueTick) => _localSent[Mod(issueTick)] = true;

        /// <summary>Release <paramref name="tick"/>'s send guard after its merged echo was applied (the slot recycles).</summary>
        internal void ClearSent(uint tick) => _localSent[Mod(tick)] = false;

        // ── Arrival keying (formerly HandleMergedTick's body) ─────────────────

        /// <summary>
        /// Key a received server-authoritative merged packet into the ring by its OWN tick. The raw bytes are
        /// copied into the preallocated slot buffer (no per-packet allocation) and applied later when the sim
        /// reaches that tick. Returns false — storing nothing — on an unpeekable packet (wrong type / truncated)
        /// or an over-ceiling length (defensive; the codec also rejects). A newer wrapped tick OVERWRITES the
        /// slot's prior occupant: the gate then stalls (not mis-applies) the older tick, which on the
        /// reliable-ordered channel can no longer be needed.
        /// </summary>
        internal bool TryStore(byte[] data, int len)
        {
            if (!MergedTickPacket.TryPeekTick(data, len, out uint tick)) return false;
            if (len > MergedTickPacket.MERGED_MAX_BYTES) return false; // over-ceiling → drop (defensive; the codec also rejects)

            int mod = Mod(tick);
            Array.Copy(data, _bytes[mod], len);
            _len[mod]     = len;
            _arrived[mod] = true;
            _tickFor[mod] = tick;
            return true;
        }

        // ── Gate (formerly Flush's stall/apply predicate) ─────────────────────

        /// <summary>
        /// The stall/apply predicate: true only when <paramref name="tick"/>'s slot is occupied AND its occupant
        /// is for exactly this tick. A missing packet or a wrong-tick (wrapped) occupant → false → the caller
        /// STALLS. Never true for a tick other than the occupant's own.
        /// </summary>
        internal bool IsReady(uint tick)
        {
            int mod = Mod(tick);
            return _arrived[mod] && _tickFor[mod] == tick;
        }

        /// <summary>The stored merged payload buffer for <paramref name="tick"/>'s slot (valid when <see cref="IsReady"/>).</summary>
        internal byte[] BytesFor(uint tick) => _bytes[Mod(tick)];

        /// <summary>The stored merged payload length for <paramref name="tick"/>'s slot (0 = a seeded empty tick).</summary>
        internal int LenFor(uint tick) => _len[Mod(tick)];

        /// <summary>Release <paramref name="tick"/>'s arrival flag after its payload was applied (the slot recycles).</summary>
        internal void ConsumeArrival(uint tick) => _arrived[Mod(tick)] = false;

        // ── Seeding (formerly SeedInitialTicks + CommitDelayChange's gap loop) ─

        /// <summary>
        /// Seed one tick as "sent + arrived-empty": length 0 stores an empty merged payload, which the applier
        /// decodes to nothing — a deterministic no-op — and the send guard is set so the client never emits a
        /// bundle for it. Both peers seed identically, so the server never expects (nor emits) a packet for a
        /// seeded tick.
        /// </summary>
        internal void SeedEmpty(uint tick)
        {
            int mod = Mod(tick);
            _localSent[mod] = true;   // "sent" — both peers treat as empty
            _arrived[mod]   = true;   // "received" — empty merged
            _tickFor[mod]   = tick;
            _len[mod]       = 0;
        }

        /// <summary>
        /// Story 9.3 bootstrap: the first REAL merged packet the server can emit is for tick == delay (the first
        /// issue tick the clients send), so ticks 0..<paramref name="delayTicks"/>−1 are pre-seeded empty and the
        /// sim can advance through the bootstrap gap. <paramref name="delayTicks"/> ≤ 0 seeds nothing (a no-op).
        /// </summary>
        internal void SeedBootstrap(int delayTicks)
        {
            for (int i = 0; i < delayTicks; i++)
                SeedEmpty((uint)i);
        }

        /// <summary>
        /// A GROWING delay change maturing at <paramref name="currentTick"/> opens a gap no peer sends for:
        /// ticks <paramref name="currentTick"/>+<paramref name="oldDelay"/>+1 ..
        /// <paramref name="currentTick"/>+<paramref name="newDelay"/> (inclusive). Both peers pre-seed the gap as
        /// empty so the client self-fills where the server never emits. A shrink (<paramref name="newDelay"/> ≤
        /// <paramref name="oldDelay"/>) seeds nothing — the already-buffered ticks execute normally.
        /// </summary>
        internal void SeedDelayGap(uint currentTick, int oldDelay, int newDelay)
        {
            for (uint gap = currentTick + (uint)oldDelay + 1;
                 gap <= currentTick + (uint)newDelay; gap++)
                SeedEmpty(gap);
        }

        private static int Mod(uint tick) => (int)(tick & BUFFER_MASK);
    }
}
