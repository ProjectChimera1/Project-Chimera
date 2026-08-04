#nullable enable

namespace ProjectChimera.Multiplayer.Server
{
    /// <summary>
    /// Story 9.13 — the dedicated server's per-slot command-rate throttle (anti-spam, NOT anti-cheat). Lives under
    /// <c>src/Multiplayer/Server/**</c> so it compiles into the Tier-1 assembly (and the determinism / banned-API
    /// analyzer) like <see cref="MergedTickBuilder"/> / <see cref="DropController"/>; the Godot
    /// <c>DedicatedServer</c> node is a thin adapter that feeds it the transport slot + an injected wall-clock ms.
    ///
    /// It is a pure receive-edge validation layer: <see cref="TryAdmit"/> returns <c>false</c> to DROP a client's
    /// <c>TickCommands</c> packet silently once that slot exceeds a fixed per-window cap set comfortably above
    /// worst-case legitimate play. Its output is only drop/accept and NEVER enters the simulation, the merged
    /// builder, <c>SimChecksum</c>, or any determinism path — so, deliberately unlike the tick-counted
    /// <see cref="DropController"/>, it is clocked by injected wall-clock milliseconds (spam is intrinsically a
    /// real-time-rate phenomenon, and a valid-submit-gated tick counter like <c>_latestSeenTick</c> has zero
    /// resolution to tell a flood of dropped same-tick packets from legit play).
    ///
    /// Everything is integer-only (no <c>float</c>/<c>double</c>, <c>System.Random</c>, <c>DateTime</c>, or
    /// <c>Dictionary</c> enumeration) and allocation-free on the hot path: per-slot parallel arrays keyed off the
    /// transport-authoritative slot, never a packet byte.
    ///
    /// <para>DW-434: the cap/window are now PER-INSTANCE (ctor-parameterized) so this same tested window mechanism
    /// also serves as the dedicated server's SHARED receive-edge throttle — one
    /// <see cref="MAX_RECEIVE_PER_WINDOW"/>-capped instance gating EVERY inbound client packet before type dispatch
    /// (the Chat/LobbyChat/MapPing broadcast amplifiers, pings/acks/checksums, and unknown/malformed types), while
    /// the original <see cref="MAX_COMMANDS_PER_WINDOW"/>-capped instance keeps its tighter per-arm gate on the
    /// TickCommands command stream. The <c>(slots)</c>-only ctor is unchanged Story-9.13 behavior.</para>
    /// </summary>
    public sealed class CommandRateLimiter
    {
        /// <summary>
        /// Sliding fixed-window length in milliseconds. A slot's admission count resets the first time a packet
        /// arrives at least this long after the window opened.
        /// </summary>
        public const int WINDOW_MS = 1000;

        /// <summary>
        /// Max <c>TickCommands</c> packets admitted per slot per <see cref="WINDOW_MS"/> window.
        ///
        /// Floor derivation (anti-spam cap MUST sit above worst-case legitimate play): legit sustained play is
        /// 1 packet/slot/tick at 30 tps = 30/sec (the builder already idempotently drops a duplicate
        /// <c>(slot,tick)</c>, <see cref="MergedTickBuilder"/>). 60 gives 2× that sustained rate — well above the
        /// [2,12] (<c>DelayMath.MIN_DELAY</c>/<c>MAX_DELAY</c>) delay-pipeline catch-up burst (a lockstep client is
        /// at most MAX_DELAY ticks ahead, so its in-flight backlog is bounded far below the cap), each packet itself
        /// capped at 32 orders (<c>TickCommandPacket.MAX_ORDERS</c>) — while still stopping a real flood
        /// (hundreds-to-thousands/sec). A fixed window's ≤2× boundary burst is fine for anti-spam.
        /// </summary>
        public const int MAX_COMMANDS_PER_WINDOW = 60;

        /// <summary>
        /// DW-434: cap for the SHARED receive-edge instance that gates ALL client-sendable packet types per slot at
        /// the top of the dedicated server's dispatch (the <see cref="MAX_COMMANDS_PER_WINDOW"/> command-stream gate
        /// still applies inside the TickCommands arm). Derivation — must sit above the worst-case COMBINED
        /// legitimate per-slot receive rate: the command stream contributes at most its own 2×-headroom cap
        /// (60/window); every other client-sendable type combined stays well under an equal 60 budget even at its
        /// worst (the in-process loopback self-test's per-pump Checksums ≈20/sec — production sends one per
        /// <c>ChecksumInterval</c>=60 ticks ≈0.5/sec — plus ~1/sec Pong echoes of the server's 1s RTT probes,
        /// event-gated Ready/DelayAck/DropAck, and human-rate Chat/LobbyChat/MapPing ≈10/sec even mashing).
        /// 2 × 60 = 120 keeps ≥2.7× margin over every real profile (~42/sec production, ~41/sec loopback) while
        /// still bounding a flood (hundreds-to-thousands/sec) to 120 dispatched packets/slot/sec. A fixed window's
        /// ≤2× boundary burst is fine for anti-spam.
        /// </summary>
        public const int MAX_RECEIVE_PER_WINDOW = 2 * MAX_COMMANDS_PER_WINDOW;

        private readonly int _slots;
        private readonly int _maxPerWindow;      // admission cap for THIS instance (DW-434 parameterization)
        private readonly int _windowMs;          // window length for THIS instance (DW-434 parameterization)
        private readonly long[]  _count;         // admissions in the current window, per slot
        private readonly ulong[] _windowStartMs; // ms at which the current window opened, per slot
        private readonly long[]  _dropped;       // lifetime dropped tally, per slot (diagnostic only)

        /// <param name="slots">Number of transport slots to track (sized to <c>ServerTransport.MAX_SLOTS</c>).</param>
        public CommandRateLimiter(int slots)
            : this(slots, MAX_COMMANDS_PER_WINDOW, WINDOW_MS)
        {
        }

        /// <summary>
        /// DW-434: cap/window-parameterized ctor so the shared receive-edge instance reuses this tested mechanism
        /// with a different budget. The <c>(slots)</c>-only ctor keeps the Story-9.13 command-stream defaults.
        /// </summary>
        /// <param name="slots">Number of transport slots to track (sized to <c>ServerTransport.MAX_SLOTS</c>).</param>
        /// <param name="maxPerWindow">Max packets admitted per slot per window (≥ 1).</param>
        /// <param name="windowMs">Fixed-window length in milliseconds (≥ 1).</param>
        public CommandRateLimiter(int slots, int maxPerWindow, int windowMs)
        {
            if (slots < 1)
                throw new System.ArgumentOutOfRangeException(nameof(slots), slots, "slots must be >= 1.");
            if (maxPerWindow < 1)
                throw new System.ArgumentOutOfRangeException(nameof(maxPerWindow), maxPerWindow, "maxPerWindow must be >= 1.");
            if (windowMs < 1)
                throw new System.ArgumentOutOfRangeException(nameof(windowMs), windowMs, "windowMs must be >= 1.");

            _slots         = slots;
            _maxPerWindow  = maxPerWindow;
            _windowMs      = windowMs;
            _count         = new long[slots];
            _windowStartMs = new ulong[slots];
            _dropped       = new long[slots];
        }

        /// <summary>Number of slots this limiter tracks.</summary>
        public int Slots => _slots;

        /// <summary>Max packets admitted per slot per window for THIS instance (DW-434 parameterization).</summary>
        public int MaxPerWindow => _maxPerWindow;

        /// <summary>Fixed-window length in milliseconds for THIS instance (DW-434 parameterization).</summary>
        public int WindowMs => _windowMs;

        /// <summary>
        /// Decide whether a packet from <paramref name="slot"/> at wall-clock
        /// <paramref name="nowMs"/> is admitted. Returns <c>true</c> to admit (fan in), <c>false</c> to DROP
        /// silently. An out-of-range slot always returns <c>false</c>. When at least <see cref="WindowMs"/> has
        /// elapsed since the slot's window opened, the window resets (fresh count from this packet). Per-slot state
        /// is fully independent — one slot at its cap never affects another slot's admissions.
        /// </summary>
        public bool TryAdmit(int slot, ulong nowMs)
        {
            if ((uint)slot >= (uint)_slots) return false;

            // Fixed-window roll-over: the window opened at _windowStartMs[slot]; once _windowMs has elapsed, start a
            // fresh window anchored at this packet's arrival. (Unsigned subtraction is safe: nowMs is monotonically
            // non-decreasing across a match — it is Time.GetTicksMsec() from the adapter.)
            if (nowMs - _windowStartMs[slot] >= (ulong)_windowMs)
            {
                _windowStartMs[slot] = nowMs;
                _count[slot]         = 0;
            }

            if (_count[slot] < _maxPerWindow)
            {
                _count[slot]++;
                return true;
            }

            // At the cap for this window — drop silently and tally (diagnostic only; never enters the sim).
            _dropped[slot]++;
            return false;
        }

        /// <summary>
        /// Reset <paramref name="slot"/>'s throttle window + count so a recycled slot never inherits the prior
        /// occupant's admission count (SoA-recycle-trap discipline). Called from <c>HandleConnect</c>. The lifetime
        /// <see cref="DroppedCount"/> tally is deliberately NOT cleared — it is a monotonic diagnostic. An
        /// out-of-range slot is a no-op.
        /// </summary>
        public void Reset(int slot)
        {
            if ((uint)slot >= (uint)_slots) return;
            _windowStartMs[slot] = 0;
            _count[slot]         = 0;
        }

        /// <summary>Lifetime count of packets this limiter has dropped for <paramref name="slot"/> (diagnostic;
        /// 0 for an out-of-range slot). Not reset by <see cref="Reset"/>.</summary>
        public long DroppedCount(int slot) => (uint)slot < (uint)_slots ? _dropped[slot] : 0L;
    }
}
