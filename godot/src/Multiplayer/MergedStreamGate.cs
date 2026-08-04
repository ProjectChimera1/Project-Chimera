#nullable enable

namespace ProjectChimera.Multiplayer
{
    /// <summary>
    /// DW-417 (Story 9.6 hardening) — the surviving CLIENT's merged-arrival ring + Flush stall gate, extracted
    /// verbatim from <c>LockstepManager</c> so it is Godot-free and Tier-1-testable. This IS the production gate:
    /// <c>LockstepManager.Flush</c> (player AND spectator paths), <c>HandleMergedTick</c>, and the bootstrap /
    /// delay-gap pre-seeds all delegate here — a test that stalls THIS gate across a mid-match drop and unstalls it
    /// with the server's injected merged stream is exercising the literal Flush surface, not a copy.
    ///
    /// <para>Semantics (unchanged from the Story 9.3 in-manager ring): ONE authoritative
    /// <c>TickCommandsMerged</c> per tick, keyed by <c>tick % RING_SIZE</c> into preallocated byte buffers (the
    /// receive path never allocates). <see cref="TryConsume"/> is the Flush gate — it yields the stored packet
    /// exactly once when the packet FOR that tick has arrived, else the caller stalls. A SEEDED tick
    /// (<see cref="SeedEmpty"/> — match-start bootstrap and the delay-change gap, the ONLY pre-seed paths) stores
    /// length 0, which <c>MergedTickApplier</c> treats as a deterministic no-op. A mid-match DROP deliberately has
    /// NO seed path: the server freezes the dropped slot and keeps injecting empty commands into the merged stream
    /// (<c>FrozenSlotInjector</c>), so the gate fills and unstalls from ARRIVING packets alone — the asymmetry with
    /// a delay change (which does need a local pre-seed) that DW-417's regression test pins.</para>
    /// </summary>
    public sealed class MergedStreamGate
    {
        /// <summary>Ring slots (power of two, &gt; the max input delay + 1 — mirrors the manager's BUFFER_SIZE).</summary>
        public const int RING_SIZE = 16;
        private const int MASK = RING_SIZE - 1;

        private readonly bool[]   _arrived = new bool[RING_SIZE];
        private readonly uint[]   _tickFor = new uint[RING_SIZE];
        private readonly byte[][] _bytes;
        private readonly int[]    _len     = new int[RING_SIZE];

        public MergedStreamGate()
        {
            _bytes = new byte[RING_SIZE][];
            for (int i = 0; i < RING_SIZE; i++)
                _bytes[i] = new byte[MergedTickPacket.MERGED_MAX_BYTES];
        }

        /// <summary>
        /// Receive a server-authoritative merged packet: key it by its own tick into the ring and copy the raw
        /// bytes into the preallocated slot buffer (no per-packet allocation). Returns <c>false</c> (no state
        /// change) for a non-merged/truncated packet or one over <see cref="MergedTickPacket.MERGED_MAX_BYTES"/>
        /// (defensive — the codec also rejects). Extracted from <c>LockstepManager.HandleMergedTick</c>.
        /// </summary>
        public bool TryReceive(byte[] data, int len)
        {
            if (!MergedTickPacket.TryPeekTick(data, len, out uint tick)) return false;
            if (len > MergedTickPacket.MERGED_MAX_BYTES) return false; // over-ceiling → drop

            int mod = (int)(tick & MASK);
            System.Array.Copy(data, _bytes[mod], len);
            _len[mod]     = len;
            _arrived[mod] = true;
            _tickFor[mod] = tick;
            return true;
        }

        /// <summary>True when the merged packet FOR <paramref name="tick"/> has arrived and not yet been consumed
        /// (the Flush gate predicate, side-effect-free).</summary>
        public bool IsReady(uint tick)
        {
            int mod = (int)(tick & MASK);
            return _arrived[mod] && _tickFor[mod] == tick;
        }

        /// <summary>
        /// The Flush gate: if the merged packet for <paramref name="tick"/> has arrived, yield its ring buffer +
        /// length (length 0 = a seeded bootstrap/gap tick → the applier no-ops), mark the slot consumed, and return
        /// <c>true</c>; otherwise return <c>false</c> — the caller stalls this frame and retries. The returned
        /// buffer is the ring's own storage: valid until the same ring slot is rewritten (RING_SIZE ticks later),
        /// exactly as the in-manager ring behaved.
        /// </summary>
        public bool TryConsume(uint tick, out byte[] buf, out int len)
        {
            int mod = (int)(tick & MASK);
            if (!(_arrived[mod] && _tickFor[mod] == tick))
            {
                buf = System.Array.Empty<byte>();
                len = 0;
                return false;
            }
            buf = _bytes[mod];
            len = _len[mod];
            _arrived[mod] = false;
            return true;
        }

        /// <summary>
        /// Pre-seed <paramref name="tick"/> as an EMPTY merged packet (length 0 → the applier is a deterministic
        /// no-op). The ONLY two production callers are the match-start bootstrap gap (ticks 0..delay−1) and a
        /// delay-INCREASE gap — a mid-match drop must NOT be seeded (the server's injected merged stream fills the
        /// gate normally; see the class doc and DW-417's regression test).
        /// </summary>
        public void SeedEmpty(uint tick)
        {
            int mod = (int)(tick & MASK);
            _arrived[mod] = true;
            _tickFor[mod] = tick;
            _len[mod]     = 0;
        }
    }
}
