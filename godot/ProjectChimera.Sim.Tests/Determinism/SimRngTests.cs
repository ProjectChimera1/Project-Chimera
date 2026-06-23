#nullable enable
using System;
using System.Linq;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Determinism
{
    /// <summary>
    /// Story 1.5 (AC1) — proves <see cref="SimRng"/> is seeded, bit-identical across instances and across a
    /// <see cref="SimRng.Seed"/> restore, integer-only, and produces the canonical SplitMix64 stream.
    ///
    /// The integer-only guarantee (no non-integer numeric types, BCL/engine RNG, or wall-clock anywhere in the
    /// type) is enforced statically by the grep gate in Story 1.5 Task 8 and the determinism analyzer (1.10b);
    /// these tests exercise the behavioral contract that rides on it.
    /// </summary>
    public class SimRngTests
    {
        // ── Independently-computed expectations (the "a tautological assert proves nothing" rule, 1.1) ──
        // These are NOT produced by calling SimRng. They are the well-known canonical SplitMix64 outputs for
        // the given seeds, reproduced from the algorithm definition (Vigna's reference + a standalone Python
        // computation). seed=0 → 0xE220A8397B1DCDAF is the textbook first SplitMix64 value, externally citable.
        private const ulong Seed0_Draw1 = 0xE220A8397B1DCDAFUL;
        private const ulong Seed0_Draw2 = 0x6E789E6AA1B965F4UL;
        private const ulong Seed0_Draw3 = 0x06C45D188009454FUL;
        private const ulong Seed12345_Draw1 = 0x22118258A9D111A0UL;

        [Fact]
        public void NextRaw_MatchesIndependentlyComputedSplitMix64_Seed0()
        {
            var rng = new SimRng(0UL);
            Assert.Equal(Seed0_Draw1, rng.NextRaw());
            Assert.Equal(Seed0_Draw2, rng.NextRaw());
            Assert.Equal(Seed0_Draw3, rng.NextRaw());
        }

        [Fact]
        public void NextRaw_MatchesIndependentlyComputedSplitMix64_NonZeroSeed()
        {
            var rng = new SimRng(12345UL);
            Assert.Equal(Seed12345_Draw1, rng.NextRaw());
        }

        [Fact]
        public void SameSeed_TwoInstances_ProduceBitIdenticalStreams()
        {
            var a = new SimRng(0xCAFEF00DUL);
            var b = new SimRng(0xCAFEF00DUL);

            ulong[] streamA = Enumerable.Range(0, 1000).Select(_ => a.NextRaw()).ToArray();
            ulong[] streamB = Enumerable.Range(0, 1000).Select(_ => b.NextRaw()).ToArray();

            Assert.Equal(streamA, streamB);
            // Sanity: the stream is not a degenerate constant.
            Assert.True(streamA.Distinct().Count() > 900, "SplitMix64 stream is suspiciously non-unique.");
        }

        [Fact]
        public void Seed_RestoresStreamFromThatPoint()
        {
            var rng = new SimRng(1UL);
            for (int i = 0; i < 50; i++) rng.NextRaw(); // advance somewhere

            const ulong restorePoint = 0x123456789ABCDEF0UL;
            rng.Seed(restorePoint);
            ulong[] afterRestore = Enumerable.Range(0, 100).Select(_ => rng.NextRaw()).ToArray();

            // A fresh instance seeded to the same value must reproduce the exact same continuation.
            var fresh = new SimRng(restorePoint);
            ulong[] fromFresh = Enumerable.Range(0, 100).Select(_ => fresh.NextRaw()).ToArray();

            Assert.Equal(fromFresh, afterRestore);
        }

        [Fact]
        public void DifferentSeeds_Diverge()
        {
            var a = new SimRng(1UL);
            var b = new SimRng(2UL);

            ulong[] streamA = Enumerable.Range(0, 100).Select(_ => a.NextRaw()).ToArray();
            ulong[] streamB = Enumerable.Range(0, 100).Select(_ => b.NextRaw()).ToArray();

            Assert.NotEqual(streamA, streamB);
        }

        [Fact]
        public void State_TracksSeedAndAdvances()
        {
            var rng = new SimRng(777UL);
            Assert.Equal(777UL, rng.State);  // State == seed before any draw

            rng.NextRaw();
            Assert.NotEqual(777UL, rng.State); // a draw advances the folded-into-checksum state

            rng.Seed(777UL);
            Assert.Equal(777UL, rng.State);  // reseed resets it
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(6)]
        [InlineData(100)]
        [InlineData(int.MaxValue)]
        public void NextInt_AlwaysInRange(int countExclusive)
        {
            var rng = new SimRng(42UL);
            for (int i = 0; i < 10000; i++)
            {
                int v = rng.NextInt(countExclusive);
                Assert.InRange(v, 0, countExclusive - 1);
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        public void NextInt_ZeroOrNegative_Throws(int countExclusive)
        {
            var rng = new SimRng(42UL);
            Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(countExclusive));
        }

        [Fact]
        public void NextFixed_AlwaysInUnitInterval()
        {
            var rng = new SimRng(0xDEADBEEFUL);
            for (int i = 0; i < 10000; i++)
            {
                Fixed f = rng.NextFixed();
                // Built from the top 16 bits as the fractional part → Raw is always in [0, 65535] (< ONE).
                Assert.InRange(f.Raw, 0, Fixed.ONE - 1);
            }
        }

        [Fact]
        public void NextFixed_Reproduces_ForSameSeed()
        {
            var a = new SimRng(99UL);
            var b = new SimRng(99UL);
            for (int i = 0; i < 200; i++)
                Assert.Equal(a.NextFixed().Raw, b.NextFixed().Raw);
        }
    }
}
