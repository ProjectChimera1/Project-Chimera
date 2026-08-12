#nullable enable
using System;
using System.Globalization;

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
    ///
    /// <para><b>DW-499 — the repro pin.</b> A per-match seed is the right default, but it costs the project its
    /// in-engine A/B verification methodology: two runs of the same authored scenario diverge on any tick-time RNG
    /// (combat crits, DSL <c>random</c>), so an RNG-touching arm cannot be compared against another. Setting the
    /// <see cref="PINNED_SEED_ENV"/> environment variable pins the seed for the process, making a repro/verification
    /// run reproducible while normal play stays per-match. Opt-IN and fail-CLOSED: an unset, blank, or unparseable
    /// value pins nothing and the entropy is mixed exactly as before, so no default behavior — and no golden — moves.</para>
    /// </summary>
    public static class MatchSeedProducer
    {
        /// <summary>
        /// DW-499 — the opt-in repro pin. When this environment variable holds a parseable 64-bit value, every
        /// <see cref="Produce(ulong)"/> in the process returns THAT seed verbatim and ignores the entropy, so
        /// successive launches of the same authored scenario reproduce run-to-run. Accepts decimal
        /// (<c>1234</c>) or <c>0x</c>-prefixed hex (<c>0xCAFEF00D</c>) — the form the match-seed log line prints,
        /// so a seed can be copied straight out of a run's console and pinned for the repro.
        /// </summary>
        public const string PINNED_SEED_ENV = "CHIMERA_MATCH_SEED";

        /// <summary>
        /// Mix <paramref name="entropy"/> into a match seed via the canonical SplitMix64 first-draw finalizer —
        /// UNLESS the <see cref="PINNED_SEED_ENV"/> repro pin is set to a parseable value, in which case that value
        /// is the seed (DW-499). Total function — every input (including 0) yields a deterministic, well-mixed
        /// 64-bit seed.
        /// </summary>
        /// <param name="entropy">Caller-supplied entropy (e.g. presentation-side wall-clock ticks). Any value is valid.</param>
        /// <returns>The per-match seed; identical to <c>new SimRng(entropy).NextRaw()</c> when nothing is pinned.</returns>
        public static ulong Produce(ulong entropy) => Produce(entropy, ReadPinnedSeedSetting());

        /// <summary>
        /// DW-499 — the environment-free core of <see cref="Produce(ulong)"/>: the same producer with the pin's raw
        /// text handed in explicitly, so the decision is deterministic and unit-testable without mutating process
        /// state. <paramref name="pinnedRaw"/> null/blank/unparseable ⇒ the normal SplitMix64 mix of
        /// <paramref name="entropy"/> (fail-closed: a typo'd pin never silently produces a third, arbitrary seed).
        /// </summary>
        /// <param name="entropy">Caller-supplied entropy — used only when nothing is pinned.</param>
        /// <param name="pinnedRaw">The raw pin text (typically the <see cref="PINNED_SEED_ENV"/> value).</param>
        public static ulong Produce(ulong entropy, string? pinnedRaw)
        {
            if (TryParsePinnedSeed(pinnedRaw, out ulong pinned)) return pinned;

            unchecked
            {
                // canonical SplitMix64 first-draw mix (same as SimRng.NextRaw for a fresh state seeded to `entropy`)
                ulong z = entropy + 0x9E3779B97F4A7C15UL;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                return z ^ (z >> 31);
            }
        }

        /// <summary>
        /// DW-499 — read the <see cref="PINNED_SEED_ENV"/> repro pin, if one is set and parseable. Returns
        /// <c>false</c> (and <paramref name="seed"/> 0) when the variable is absent, blank, or malformed — the
        /// fail-closed arm, so a mistyped pin falls back to normal per-match entropy instead of pinning garbage.
        /// </summary>
        public static bool TryPinnedSeed(out ulong seed) => TryParsePinnedSeed(ReadPinnedSeedSetting(), out seed);

        /// <summary>
        /// DW-499 — parse a pinned-seed string, fail-closed. Accepts an optionally <c>0x</c>-prefixed hex literal or
        /// a plain decimal <c>ulong</c>, both invariant-culture and surrounding whitespace tolerant. Anything else —
        /// null, blank, negative, overflowing, or non-numeric — yields <c>false</c>.
        /// </summary>
        public static bool TryParsePinnedSeed(string? raw, out ulong seed)
        {
            seed = 0UL;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            string s = raw.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) // covers "0X" too
            {
                string hex = s.Substring(2);
                return hex.Length > 0 &&
                       ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out seed);
            }
            return ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out seed);
        }

        /// <summary>The one place the process environment is touched — kept private and behind
        /// <see cref="Produce(ulong,string?)"/> so every decision above stays a pure function of its arguments.</summary>
        private static string? ReadPinnedSeedSetting() => Environment.GetEnvironmentVariable(PINNED_SEED_ENV);
    }
}
