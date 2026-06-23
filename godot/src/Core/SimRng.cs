#nullable enable
namespace ProjectChimera.Core
{
    /// <summary>
    /// The ONLY source of randomness in the simulation (AR-13 / AR-15). Seeded, deterministic, and
    /// INTEGER-ONLY (SplitMix64) — the same seed yields a bit-identical stream on every machine, across
    /// replays, and across platforms.
    ///
    /// One shared instance lives on <see cref="EntityWorld.Rng"/> and is referenced (never copied) by
    /// every caller, so a draw in one place advances the single stream everyone sees. Its <see cref="State"/>
    /// is folded into <see cref="SimChecksum"/> (so a divergent draw stream desyncs detectably), and its
    /// match-start seed is persisted by ReplayRecorder / restored by ReplayPlayer (so a replay regenerates
    /// the identical stream).
    ///
    /// RULES for callers:
    ///   • NEVER use the BCL or engine random generators, any non-integer numeric type, or wall-clock time in
    ///     sim code — every one of those diverges across machines and silently breaks lockstep.
    ///   • Random selection MUST collect its candidates in ascending-entity-id order BEFORE calling
    ///     <see cref="NextInt"/> — iteration order is part of the deterministic contract (AR-15).
    ///   • This is a reference type (class) on purpose: a struct copied into a tick/effect context would
    ///     lose its draw advances and desync silently.
    /// </summary>
    public sealed class SimRng
    {
        // SplitMix64 (Sebastiano Vigna, public domain). Single ulong state; full 2^64 period; tolerates
        // any seed including 0 (no zero-state trap, unlike raw xorshift). ~5 integer ops per draw.
        private const ulong GAMMA = 0x9E3779B97F4A7C15UL;

        private ulong _state;

        /// <param name="seed">Initial stream state. Any value is valid (including 0).</param>
        public SimRng(ulong seed = 0UL) => _state = seed;

        /// <summary>Current internal state — the value folded into <see cref="SimChecksum"/> (the desync tripwire).</summary>
        public ulong State => _state;

        /// <summary>
        /// Reset the stream to <paramref name="seed"/> (match start / replay restore). Deterministic.
        /// Mutates state in place — the <see cref="SimRng"/> reference itself stays fixed.
        /// </summary>
        public void Seed(ulong seed) => _state = seed;

        /// <summary>Advance the stream and return 64 raw bits — the single primitive draw.</summary>
        public ulong NextRaw()
        {
            unchecked
            {
                ulong z = (_state += GAMMA);
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        /// <summary>
        /// Non-negative int in <c>[0, countExclusive)</c>. Modulo bias is deterministic (identical on every
        /// peer) so it does NOT break lockstep — a uniform draw is unnecessary for determinism.
        /// </summary>
        /// <param name="countExclusive">Exclusive upper bound; must be &gt; 0.</param>
        /// <exception cref="System.ArgumentOutOfRangeException">If <paramref name="countExclusive"/> &lt;= 0.</exception>
        public int NextInt(int countExclusive)
        {
            if (countExclusive <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(countExclusive), "must be > 0");
            return (int)(NextRaw() % (ulong)countExclusive);
        }

        /// <summary>
        /// <see cref="Fixed"/> in <c>[0, 1)</c>: the top 16 bits of a raw draw become the 16 fractional bits.
        /// Integer-only — <see cref="Fixed"/> has no [0,1) helper, so <see cref="SimRng"/> owns this mapping.
        /// </summary>
        public Fixed NextFixed() => Fixed.FromRaw((int)(NextRaw() >> 48));
    }
}
