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

        // DW-226 — the MAX-SEED BOUNDARY, pinned as a real stream (it previously appeared only as a "deliberately
        // wrong seed" fixture in SimRngChecksumReplayTests). ulong.MaxValue is the one seed where the very first
        // `_state += GAMMA` OVERFLOWS the 64-bit state, so it is the boundary that proves the accumulate is unsigned
        // wraparound arithmetic — not a checked add, not a saturating one, and not a 63-bit/signed slip. A peer that
        // got this wrong would diverge on the first draw of any match whose seed lands near 2^64.
        //
        // Computed the same INDEPENDENT way as the constants above: a standalone SplitMix64 implementation, validated
        // against the externally-citable seed=0 / seed=12345 pins in this same block before being trusted for the max
        // seed. NOT produced by calling SimRng (the "a tautological assert proves nothing" rule, 1.1).
        private const ulong SeedMax_Draw1 = 0xE4D971771B652C20UL;
        private const ulong SeedMax_Draw2 = 0xE99FF867DBF682C9UL;
        private const ulong SeedMax_Draw3 = 0x382FF84CB27281E9UL;

        /// <summary>(2^64 − 1 + GAMMA) mod 2^64 == GAMMA − 1 — the wrapped state after one draw from the max seed.</summary>
        private const ulong SeedMax_StateAfter1Draw = 0x9E3779B97F4A7C14UL;

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

        /// <summary>
        /// DW-226 — <c>ulong.MaxValue</c> pinned as a real stream, not merely used as a wrong-seed fixture. Three draws
        /// against independently computed SplitMix64 output, plus the wrapped state after the first draw: the max seed
        /// is the ONLY seed whose first <c>_state += GAMMA</c> crosses 2^64, so this is the boundary that pins the
        /// accumulate as unsigned wraparound (a checked/saturating/signed variant fails here and nowhere else).
        /// </summary>
        [Fact]
        public void NextRaw_MatchesIndependentlyComputedSplitMix64_MaxSeed()
        {
            var rng = new SimRng(ulong.MaxValue);
            Assert.Equal(ulong.MaxValue, rng.State); // State == seed before any draw, even at the ceiling

            Assert.Equal(SeedMax_Draw1, rng.NextRaw());
            // The wrap happened on that first draw: state advanced PAST 2^64 and came back to GAMMA − 1.
            Assert.Equal(SeedMax_StateAfter1Draw, rng.State);
            Assert.Equal(SeedMax_Draw2, rng.NextRaw());
            Assert.Equal(SeedMax_Draw3, rng.NextRaw());

            // Reseeding to the ceiling reproduces the identical stream (the replay-restore contract at the boundary).
            rng.Seed(ulong.MaxValue);
            Assert.Equal(SeedMax_Draw1, rng.NextRaw());
        }

        /// <summary>
        /// DW-226 (companion) — the derived draws at the max seed. Every pinned raw has its HIGH BIT SET, which is
        /// exactly where an accidental signed conversion in <see cref="SimRng.NextInt"/> (<c>%</c> on a negative
        /// long/int instead of the unsigned raw) or in <see cref="SimRng.NextFixed"/>'s <c>&gt;&gt; 48</c> (an
        /// arithmetic shift on a signed value) would produce a negative result. Expectations derived arithmetically
        /// from the independently computed raws above, not from calling SimRng.
        /// </summary>
        [Fact]
        public void DerivedDraws_AtMaxSeed_StayUnsigned()
        {
            // 0xE4D9…%3 == 2, 0xE99F…%3 == 0, 0x382F…%3 == 1 (unsigned modulo of the pinned raws).
            var ints = new SimRng(ulong.MaxValue);
            Assert.Equal(2, ints.NextInt(3));
            Assert.Equal(0, ints.NextInt(3));
            Assert.Equal(1, ints.NextInt(3));

            // Top 16 bits of each pinned raw become the Fixed fractional part → always inside [0, ONE).
            var fixeds = new SimRng(ulong.MaxValue);
            Assert.Equal((int)(SeedMax_Draw1 >> 48), fixeds.NextFixed().Raw);
            Assert.Equal((int)(SeedMax_Draw2 >> 48), fixeds.NextFixed().Raw);
            Assert.InRange(new SimRng(ulong.MaxValue).NextFixed().Raw, 0, Fixed.ONE - 1);
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
