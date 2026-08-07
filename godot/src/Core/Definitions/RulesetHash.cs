#nullable enable
using ProjectChimera.Effects; // EffectCaps — the structural caps this hash fingerprints

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Story 9.4 — a canonical FNV-64 over the closed Effect-Graph's structural caps (<see cref="EffectCaps"/>),
    /// the net-new "ruleset fingerprint" folded into <see cref="MatchAgreementHash"/>. Two clients whose builds
    /// disagree on any cap (a modded/mismatched executor) would run divergent effect graphs and desync in-sim, so
    /// the caps must be handshake-rejectable — this is the value that makes them so.
    ///
    /// Effectively constant today (the caps are <c>const</c>), but versioned + wire-foldable so a future cap change
    /// (or an <see cref="AlgoVersion"/> bump) moves the value and rejects at the lobby instead of desyncing.
    ///
    /// Godot-free (src/Core/Definitions) so Tier-1 computes it headless; same FNV-64 primitive as
    /// <see cref="StartStateHash"/> / <see cref="CanonicalModelHash"/> (int/ulong only — analyzer-clean).
    /// </summary>
    public static class RulesetHash
    {
        /// <summary>Algorithm version of THIS hash. Mixed FIRST so a bump moves the value even with no cap change.
        /// Bump only when the folded cap set/order changes.
        /// <para>v1 = initial (Story 9.4): AlgoVersion then the ten structural caps in file order.
        /// v2 = DW-534: <see cref="EffectCaps.MaxSearchRadius"/> joins the fold as an eleventh cap (the authored
        /// SearchArea radius ceiling), so a build that bounds the radius and one that does not are now
        /// handshake-incompatible rather than silently running different work per cast.
        /// v3 = DW-272 / Story 15.12: <see cref="EffectCaps.MaxPeriodicStackScale"/> joins the fold as a twelfth cap
        /// (the stacked-periodic-pulse scaling ceiling), so two builds that disagree on how far a stacked DoT/HoT
        /// pulse may scale are handshake-incompatible rather than desyncing on the first stacked periodic cast.</para></summary>
        public const int AlgoVersion = 3;

        private const ulong Offset = 14695981039346656037UL; // FNV-64 offset basis (same primitive as StartStateHash)
        private const ulong Prime  = 1099511628211UL;        // FNV-64 prime

        /// <summary>
        /// Fold <see cref="AlgoVersion"/> then every <see cref="EffectCaps"/> cap in FILE ORDER
        /// (<see cref="EffectCaps.MaxEffectDepth"/> … <see cref="EffectCaps.MaxPeriodicStackScale"/>). Never returns 0
        /// (sentinel), so a valid ruleset never collides with the fail-open "no hash" value.
        /// </summary>
        public static ulong Compute()
        {
            ulong h = Offset;

            h = MixInt(h, AlgoVersion); // namespaces the hash; a bump moves the value alone

            // Every EffectCaps cap in file order — a change to any one moves the hash.
            h = MixInt(h, EffectCaps.MaxEffectDepth);
            h = MixInt(h, EffectCaps.MaxSequenceChildren);
            h = MixInt(h, EffectCaps.MaxSearchTargets);
            h = MixInt(h, EffectCaps.MaxHitsPerSearch);
            h = MixInt(h, EffectCaps.MaxEffectFrames);
            h = MixInt(h, EffectCaps.MaxSpawnCount);
            h = MixInt(h, EffectCaps.MaxPersistentPeriods);
            h = MixInt(h, EffectCaps.MaxModifiersPerEntity);
            h = MixInt(h, EffectCaps.MaxSearchAreaDepth);
            h = MixInt(h, EffectCaps.MaxTotalEffectNodes);
            h = MixInt(h, EffectCaps.MaxSearchRadius); // DW-534 (AlgoVersion 2)
            h = MixInt(h, EffectCaps.MaxPeriodicStackScale); // DW-272 / Story 15.12 (AlgoVersion 3)

            return h == 0UL ? 1UL : h;
        }

        /// <summary>FNV-64 fold of a 32-bit int as 4 little-endian bytes (mirrors <see cref="StartStateHash"/>).</summary>
        private static ulong MixInt(ulong h, int value)
        {
            uint v = (uint)value;
            h ^= v & 0xFF;         h *= Prime;
            h ^= (v >> 8) & 0xFF;  h *= Prime;
            h ^= (v >> 16) & 0xFF; h *= Prime;
            h ^= (v >> 24) & 0xFF; h *= Prime;
            return h;
        }
    }
}
