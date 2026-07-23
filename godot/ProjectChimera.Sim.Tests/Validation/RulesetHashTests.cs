#nullable enable
using ProjectChimera.Core.Definitions; // RulesetHash
using ProjectChimera.Effects;           // EffectCaps
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 9.4 — <see cref="RulesetHash"/> is a canonical FNV-64 over the <see cref="EffectCaps"/> structural
    /// caps folded into <see cref="MatchAgreementHash"/>. This pins it deterministic + non-zero and — the
    /// anti-tautology rule (1.1) — against an INDEPENDENTLY hand-rolled FNV-64 over the documented byte stream
    /// (AlgoVersion first, then every cap in file order), never re-running Compute against itself.
    /// </summary>
    public class RulesetHashTests
    {
        private const ulong Offset = 14695981039346656037UL;
        private const ulong Prime  = 1099511628211UL;

        /// <summary>Independent FNV-64 fold of a 32-bit int as 4 LE bytes (the documented MixInt).</summary>
        private static ulong Mix(ulong h, int value)
        {
            uint v = (uint)value;
            h ^= v & 0xFF;         h *= Prime;
            h ^= (v >> 8) & 0xFF;  h *= Prime;
            h ^= (v >> 16) & 0xFF; h *= Prime;
            h ^= (v >> 24) & 0xFF; h *= Prime;
            return h;
        }

        [Fact]
        public void Compute_IsDeterministic_AndNonZero()
        {
            ulong a = RulesetHash.Compute();
            ulong b = RulesetHash.Compute();
            Assert.Equal(a, b);
            Assert.NotEqual(0UL, a);
        }

        [Fact]
        public void Compute_MatchesTheIndependentlyFoldedByteStream()
        {
            // Fold AlgoVersion FIRST, then every EffectCaps cap in FILE ORDER (MaxEffectDepth … MaxTotalEffectNodes).
            ulong h = Offset;
            h = Mix(h, RulesetHash.AlgoVersion);
            h = Mix(h, EffectCaps.MaxEffectDepth);
            h = Mix(h, EffectCaps.MaxSequenceChildren);
            h = Mix(h, EffectCaps.MaxSearchTargets);
            h = Mix(h, EffectCaps.MaxHitsPerSearch);
            h = Mix(h, EffectCaps.MaxEffectFrames);
            h = Mix(h, EffectCaps.MaxSpawnCount);
            h = Mix(h, EffectCaps.MaxPersistentPeriods);
            h = Mix(h, EffectCaps.MaxModifiersPerEntity);
            h = Mix(h, EffectCaps.MaxSearchAreaDepth);
            h = Mix(h, EffectCaps.MaxTotalEffectNodes);
            ulong expected = h == 0UL ? 1UL : h;

            Assert.Equal(expected, RulesetHash.Compute());
        }

        [Fact]
        public void AlgoVersion_MixedFirst_MovesTheValue()
        {
            // Same caps, a different AlgoVersion ⇒ a different hash (the version namespaces the fold). Proven by
            // recomputing the documented stream at a bumped version and asserting it differs from the real value.
            ulong h = Offset;
            h = Mix(h, RulesetHash.AlgoVersion + 1); // pretend a bump
            h = Mix(h, EffectCaps.MaxEffectDepth);
            h = Mix(h, EffectCaps.MaxSequenceChildren);
            h = Mix(h, EffectCaps.MaxSearchTargets);
            h = Mix(h, EffectCaps.MaxHitsPerSearch);
            h = Mix(h, EffectCaps.MaxEffectFrames);
            h = Mix(h, EffectCaps.MaxSpawnCount);
            h = Mix(h, EffectCaps.MaxPersistentPeriods);
            h = Mix(h, EffectCaps.MaxModifiersPerEntity);
            h = Mix(h, EffectCaps.MaxSearchAreaDepth);
            h = Mix(h, EffectCaps.MaxTotalEffectNodes);
            ulong bumped = h == 0UL ? 1UL : h;

            Assert.NotEqual(bumped, RulesetHash.Compute());
        }
    }
}
