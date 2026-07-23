#nullable enable
using ProjectChimera.Core; // Faction

namespace ProjectChimera.Multiplayer.Server
{
    /// <summary>
    /// Story 9.3 (SD-1 / SD-2, the build half) — the Godot-free, per-tick command fan-in that turns every
    /// player's single-faction <see cref="TickCommandPacket"/> into ONE authoritative
    /// <see cref="MergedTickPacket"/>. Lives under <c>src/Multiplayer/Server/**</c> so it compiles into the
    /// Tier-1 assembly (and the determinism analyzer): the FR-39 golden exercises the REAL merge code, while the
    /// Godot <c>DedicatedServer</c> node is a thin adapter that just feeds submissions in and broadcasts the
    /// result out.
    ///
    /// Invariants (all deterministic, all drop-not-clamp):
    ///   • Slot identity is transport-authoritative: the sub-bundle faction is ALWAYS
    ///     <c>slotFaction[sourceSlot]</c>, never a packet byte.
    ///   • A packet whose claimed faction ≠ the slot's authoritative faction (a spoof) is DROPPED whole.
    ///   • An over-count (&gt; <see cref="TickCommandPacket.MAX_ORDERS"/>) or otherwise malformed packet is
    ///     DROPPED (the read-side reject), never clamped.
    ///   • A <see cref="PacketType.TickCommandsMerged"/> packet submitted by a client is hard-rejected.
    ///   • Sub-bundles are emitted ASCENDING by faction id, so wire order is the canonical apply order.
    ///   • A merged packet for tick T is emitted EXACTLY ONCE — the moment all <see cref="Expected"/> players have
    ///     submitted T; a later/duplicate submit for an already-emitted T is ignored.
    /// </summary>
    public sealed class MergedTickBuilder
    {
        /// <summary>Ring size for buffering in-flight ticks (power of two, comfortably &gt; the max input delay of 12).</summary>
        private const int RING = 64;
        private const int MASK = RING - 1;

        /// <summary>
        /// How far ahead of the last-emitted tick a submission may key. A legitimate submission is at most ~2×the
        /// max input delay (12) ahead of the frontier; anything past this window is either genuinely stale or an
        /// aliased far-future tick (T vs T+RING land on the same ring index), so it is DROPPED rather than allowed
        /// to re-key and wipe an honest peer's in-flight arrivals. Strictly &lt; RING so an aliased tick can never
        /// be mistaken for a legitimate forward re-key.
        /// </summary>
        private const int ACCEPT_WINDOW = RING / 2;

        /// <summary>Number of players whose submissions a tick must collect before it is emitted.</summary>
        public int Expected { get; }

        private readonly Faction[] _slotFaction; // slot → authoritative faction (indexed 0..Expected-1)

        // Per (ring, slot) submission storage. Flat, indexed [(mod * Expected + slot) * MAX_ORDERS + i].
        private readonly UnitOrder[] _orders;
        private readonly int[]  _count;     // [mod * Expected + slot]
        private readonly bool[] _arrived;   // [mod * Expected + slot]
        private readonly uint[] _tickFor;   // [mod] — which tick this ring slot currently holds
        private readonly bool[] _tickValid; // [mod] — whether _tickFor[mod] holds a real (initialised) tick
        private readonly bool[] _emitted;   // [mod] — merged packet for _tickFor[mod] already emitted

        // Highest tick for which a merged packet has already been emitted; -1 = none. A submission at or below this
        // is already resolved (or stale) and is dropped — mirrors ServerChecksumCollector._resolvedThrough. Emission
        // is monotonic (reliable, in-order transport ⇒ ticks fan in ascending), so this is a true high-water.
        private long _resolvedThrough = -1;

        // Decode + assembly scratch (single-threaded server; reused across ticks — no per-submit allocation).
        private readonly UnitOrder[] _decodeBuf = new UnitOrder[TickCommandPacket.MAX_ORDERS];
        private readonly Faction[]   _sortFaction = new Faction[MergedTickPacket.MERGED_MAX_SUBBUNDLES];
        private readonly int[]       _sortSlot    = new int[MergedTickPacket.MERGED_MAX_SUBBUNDLES];
        private readonly Faction[]   _outFaction  = new Faction[MergedTickPacket.MERGED_MAX_SUBBUNDLES];
        private readonly int[]       _outCount    = new int[MergedTickPacket.MERGED_MAX_SUBBUNDLES];
        private readonly UnitOrder[] _outOrders   = new UnitOrder[MergedTickPacket.MERGED_MAX_SUBBUNDLES * TickCommandPacket.MAX_ORDERS];
        private readonly byte[]      _mergedBuf   = new byte[MergedTickPacket.MERGED_MAX_BYTES];

        /// <param name="expected">Number of players (sub-bundles) each tick must collect. In [1, MERGED_MAX_SUBBUNDLES].</param>
        /// <param name="slotFaction">Slot → authoritative faction. Must have at least <paramref name="expected"/> entries.</param>
        public MergedTickBuilder(int expected, Faction[] slotFaction)
        {
            if (expected < 1 || expected > MergedTickPacket.MERGED_MAX_SUBBUNDLES)
                throw new System.ArgumentOutOfRangeException(nameof(expected), expected,
                    $"expected must be in [1, {MergedTickPacket.MERGED_MAX_SUBBUNDLES}].");
            if (slotFaction == null || slotFaction.Length < expected)
                throw new System.ArgumentException("slotFaction must have at least `expected` entries.", nameof(slotFaction));

            Expected     = expected;
            _slotFaction = slotFaction;

            _orders    = new UnitOrder[RING * expected * TickCommandPacket.MAX_ORDERS];
            _count     = new int[RING * expected];
            _arrived   = new bool[RING * expected];
            _tickFor   = new uint[RING];
            _tickValid = new bool[RING];
            _emitted   = new bool[RING];
        }

        /// <summary>
        /// Fan a player's single-faction tick command packet in. Returns <c>true</c> (and the decoded
        /// <paramref name="tick"/>) when the submission was accepted and buffered; <c>false</c> when it was
        /// DROPPED. Drops (all deterministic, all grief-safe): out-of-range slot / spectator; a merged-shaped
        /// packet; malformed or over-count; faction spoof; a tick already emitted or below the resolved high-water;
        /// an out-of-window (aliased far-future or stale-past) tick colliding on a ring index; and a DUPLICATE
        /// (slot,tick) after that slot already arrived (idempotent no-op — the first bundle is NOT overwritten, so
        /// a client cannot revise its already-fanned-in orders). On acceptance the caller should immediately
        /// attempt <see cref="TryBuild"/> for <paramref name="tick"/>.
        /// </summary>
        public bool Submit(int sourceSlot, byte[] data, int len, out uint tick)
        {
            tick = 0;
            if (sourceSlot < 0 || sourceSlot >= Expected) return false; // spectators / unknown slots never merge in

            // Hard-reject a merged-shaped packet from a client — the merged type is server-authored only.
            if (len >= 1 && (PacketType)data[0] == PacketType.TickCommandsMerged) return false;

            // Read-side reject: malformed OR over-count (> MAX_ORDERS) returns false → drop (never clamp).
            if (!TickCommandPacket.TryRead(data, len, out uint decodedTick, out Faction claimedFaction,
                                           _decodeBuf, out int count))
                return false;

            // Faction spoof: the claimed faction must match the slot's authoritative faction. Drop the whole
            // bundle on a mismatch (the merged sub-bundle faction is re-stamped from the slot regardless).
            if (claimedFaction != _slotFaction[sourceSlot]) return false;

            // Already resolved / stale: a tick at or below the last-emitted high-water can never fan in again
            // (mirrors ServerChecksumCollector._resolvedThrough). Drops a replay of an emitted tick and any
            // far-past tick, and — with the window guard below — prevents a T+RING alias from re-keying an
            // in-flight T's honest arrivals.
            if ((long)decodedTick <= _resolvedThrough) return false;
            // Implausibly-far-future: beyond ACCEPT_WINDOW past the frontier is either genuinely stale or an
            // aliased far-future tick; drop it rather than let it re-key the ring index (grief/deadlock guard).
            if ((long)decodedTick > _resolvedThrough + ACCEPT_WINDOW) return false;

            int mod = (int)(decodedTick & MASK);

            if (_tickValid[mod] && _tickFor[mod] != decodedTick)
            {
                // The ring slot holds a DIFFERENT live tick. Re-key ONLY strictly forward (a newer tick overruns an
                // older incomplete slot — its honest arrivals are for a now-abandoned older tick). An OLDER tick
                // colliding with a live newer slot is stale → drop it WITHOUT touching the newer slot's arrivals.
                if (_tickFor[mod] > decodedTick) return false;
                for (int s = 0; s < Expected; s++) _arrived[mod * Expected + s] = false;
                _tickFor[mod]   = decodedTick;
                _tickValid[mod] = true;
                _emitted[mod]   = false;
            }
            else if (!_tickValid[mod])
            {
                for (int s = 0; s < Expected; s++) _arrived[mod * Expected + s] = false;
                _tickFor[mod]   = decodedTick;
                _tickValid[mod] = true;
                _emitted[mod]   = false;
            }
            else if (_emitted[mod])
            {
                return false; // late/duplicate submit for a tick already emitted — ignore (defensive; _resolvedThrough covers it)
            }

            // Duplicate (slot,tick) AFTER arrival: idempotent no-op — do NOT overwrite the first bundle (a client
            // cannot revise its already-fanned-in orders via a last-writer-wins resubmit).
            if (_arrived[mod * Expected + sourceSlot]) return false;

            // Buffer this slot's orders.
            int slotBase = (mod * Expected + sourceSlot) * TickCommandPacket.MAX_ORDERS;
            for (int i = 0; i < count; i++) _orders[slotBase + i] = _decodeBuf[i];
            _count[mod * Expected + sourceSlot]   = count;
            _arrived[mod * Expected + sourceSlot] = true;

            tick = decodedTick;
            return true;
        }

        /// <summary>
        /// Emit the merged packet for <paramref name="tick"/> — but only once ALL <see cref="Expected"/> players
        /// have submitted it and it has not already been emitted. Sub-bundles are written ascending by faction id
        /// (the byte-ceiling / sub-bundle-count ceilings drop the overflowing sub-bundle deterministically during
        /// the ascending scan). Returns <c>false</c> (fan-in incomplete / already emitted / unknown tick) without
        /// writing anything.
        /// </summary>
        public bool TryBuild(uint tick, out byte[] merged, out int len)
        {
            merged = _mergedBuf; len = 0;
            int mod = (int)(tick & MASK);
            if (!_tickValid[mod] || _tickFor[mod] != tick) return false;
            if (_emitted[mod]) return false;

            for (int s = 0; s < Expected; s++)
                if (!_arrived[mod * Expected + s]) return false; // fan-in incomplete → buffer, wait

            // Collect (faction, slot) for each arrived slot, then insertion-sort ascending by faction id so the
            // wire order is the canonical apply order (deterministic; never an unstable Array.Sort).
            for (int s = 0; s < Expected; s++)
            {
                _sortFaction[s] = _slotFaction[s];
                _sortSlot[s]    = s;
            }
            for (int i = 1; i < Expected; i++)
            {
                Faction fk = _sortFaction[i];
                int     sk = _sortSlot[i];
                int j = i - 1;
                while (j >= 0 && (byte)_sortFaction[j] > (byte)fk)
                {
                    _sortFaction[j + 1] = _sortFaction[j];
                    _sortSlot[j + 1]    = _sortSlot[j];
                    j--;
                }
                _sortFaction[j + 1] = fk;
                _sortSlot[j + 1]    = sk;
            }

            // Assemble ascending, applying the drop-not-clamp ceilings (sub-bundle count + total bytes).
            int included = 0;
            int runningBytes = MergedTickPacket.HEADER_BYTES;
            for (int k = 0; k < Expected; k++)
            {
                int slot     = _sortSlot[k];
                int count    = _count[mod * Expected + slot];
                int bundleBytes = MergedTickPacket.SUBBUNDLE_HEADER_BYTES + count * UnitOrder.SIZE;

                if (included >= MergedTickPacket.MERGED_MAX_SUBBUNDLES) break;               // sub-bundle-count ceiling
                if (runningBytes + bundleBytes > MergedTickPacket.MERGED_MAX_BYTES) continue; // byte ceiling — drop this one

                _outFaction[included] = _slotFaction[slot];
                _outCount[included]   = count;
                int srcBase = (mod * Expected + slot) * TickCommandPacket.MAX_ORDERS;
                int dstBase = included * TickCommandPacket.MAX_ORDERS;
                for (int i = 0; i < count; i++) _outOrders[dstBase + i] = _orders[srcBase + i];

                runningBytes += bundleBytes;
                included++;
            }

            len = MergedTickPacket.Write(_mergedBuf, tick, _outFaction, _outCount, _outOrders, included);
            _emitted[mod] = true;
            if ((long)tick > _resolvedThrough) _resolvedThrough = tick; // advance the emitted high-water (monotonic)
            return true;
        }
    }
}
