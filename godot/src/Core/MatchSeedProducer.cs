#nullable enable
namespace ProjectChimera.Core
{
    /// <summary>
    /// The per-match RNG seed seam (DW-17 / DW-225). A pure, integer-only, Godot-free producer that mixes a
    /// caller-supplied <c>ulong</c> entropy value into a well-separated 64-bit match seed.
    ///
    /// <para>It lives under <c>src/Core/**</c> (the banned-API determinism analyzer): there is NO wall-clock,
    /// float, or BCL/engine RNG inside it — the ENTROPY is the caller's responsibility (the offline reset feeds
    /// it presentation-side wall-clock; the online path pins the default seed). This type only mixes.</para>
    ///
    /// <para>The mix is the canonical SplitMix64 first-draw finalizer, so
    /// <c>Produce(entropy) == new SimRng(entropy).NextRaw()</c> — it ties the match seed to the already-canonical,
    /// externally-cited SplitMix64 stream, gives a non-tautological test oracle, and guarantees good avalanche
    /// separation of near-sequential wall-clock entropy.</para>
    /// </summary>
    public static class MatchSeedProducer
    {
        /// <summary>
        /// Mix <paramref name="entropy"/> into a match seed via the canonical SplitMix64 first-draw finalizer.
        /// Total function — every input (including 0) yields a deterministic, well-mixed 64-bit seed.
        /// </summary>
        /// <param name="entropy">Caller-supplied entropy (e.g. presentation-side wall-clock ticks). Any value is valid.</param>
        /// <returns>The per-match seed; identical to <c>new SimRng(entropy).NextRaw()</c>.</returns>
        public static ulong Produce(ulong entropy)
        {
            unchecked
            {
                // canonical SplitMix64 first-draw mix (same as SimRng.NextRaw for a fresh state seeded to `entropy`)
                ulong z = entropy + 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }
    }
}
