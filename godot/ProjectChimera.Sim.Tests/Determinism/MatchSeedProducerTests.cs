#nullable enable
using System.Linq;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Determinism
{
    /// <summary>
    /// DW-17 / DW-225 — proves <see cref="MatchSeedProducer"/> is a pure, deterministic, well-mixing per-match
    /// seed producer. The oracle is INDEPENDENT of the producer: it reads the already-canonical
    /// <see cref="SimRng"/> first-draw stream, so <c>Produce(e) == new SimRng(e).NextRaw()</c> is a
    /// non-tautological cross-check (the "a tautological assert proves nothing" rule, 1.1), and the same
    /// property guarantees good avalanche separation of near-sequential wall-clock entropy.
    /// </summary>
    public class MatchSeedProducerTests
    {
        [Theory]
        [InlineData(0UL)]
        [InlineData(1UL)]
        [InlineData(12345UL)]
        [InlineData(0xCAFEF00DUL)]
        [InlineData(0x0123456789ABCDEFUL)]
        [InlineData(ulong.MaxValue)]
        public void Produce_MatchesFreshSimRngFirstDraw(ulong entropy)
        {
            // Independent oracle: the canonical SplitMix64 first draw of a fresh SimRng seeded to `entropy`.
            ulong oracle = new SimRng(entropy).NextRaw();
            Assert.Equal(oracle, MatchSeedProducer.Produce(entropy));
        }

        [Theory]
        [InlineData(0UL)]
        [InlineData(42UL)]
        [InlineData(0xDEADBEEFUL)]
        public void Produce_IsDeterministic_SameInputSameOutput(ulong entropy)
        {
            Assert.Equal(MatchSeedProducer.Produce(entropy), MatchSeedProducer.Produce(entropy));
        }

        [Fact]
        public void Produce_ZeroEntropy_YieldsNonZeroWellMixedSeed()
        {
            ulong seed = MatchSeedProducer.Produce(0UL);
            Assert.NotEqual(0UL, seed);
            // Zero entropy must NOT collapse to the default seed (the SplitMix64 gamma) — it is mixed, not passed through.
            Assert.NotEqual(EntityWorld.DEFAULT_RNG_SEED, seed);
        }

        [Theory]
        [InlineData(0UL)]
        [InlineData(1000UL)]
        [InlineData(0xFFFFFFFFUL)]
        public void Produce_SequentialInputs_YieldDistinctWellSeparatedSeeds(ulong start)
        {
            // Near-sequential wall-clock entropy (n, n+1, n+2, ...) must avalanche into distinct, well-separated seeds.
            ulong a = MatchSeedProducer.Produce(start);
            ulong b = MatchSeedProducer.Produce(start + 1UL);
            ulong c = MatchSeedProducer.Produce(start + 2UL);

            Assert.NotEqual(a, b);
            Assert.NotEqual(b, c);
            Assert.NotEqual(a, c);
        }

        [Fact]
        public void Produce_ManyDistinctInputs_YieldDistinctSeeds()
        {
            // A block of consecutive entropy values maps to an all-distinct set of seeds (no near-input collisions).
            ulong[] seeds = Enumerable.Range(0, 1000)
                .Select(i => MatchSeedProducer.Produce((ulong)i))
                .ToArray();
            Assert.Equal(seeds.Length, seeds.Distinct().Count());
        }
    }
}
