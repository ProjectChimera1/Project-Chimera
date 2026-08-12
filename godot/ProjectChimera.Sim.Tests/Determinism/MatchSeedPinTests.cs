#nullable enable
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Determinism
{
    /// <summary>
    /// DW-499 — the OPT-IN repro pin on <see cref="MatchSeedProducer"/>.
    ///
    /// <para>The gap. The offline Edit→Play reset mints a fresh wall-clock-entropy seed every launch (the intended
    /// per-match behaviour), so two runs of the same authored scenario diverge on any tick-time RNG — combat crits,
    /// DSL <c>random</c>. That is correct for play but it removes the project's in-engine A/B verification
    /// methodology, which rests on comparing two arms of a choice: an RNG-touching arm can no longer be reproduced
    /// run-to-run. The closure is an override read as the offline entropy when set, so a verification/repro run can
    /// FIX the seed while normal play stays per-match.</para>
    ///
    /// <para>These tests drive the environment-free <see cref="MatchSeedProducer.Produce(ulong,string?)"/> overload,
    /// so every decision is asserted deterministically without touching process state (the one env-var round-trip
    /// lives in <see cref="MatchSeedProducerTests"/>, whose class-level serialisation keeps it away from the other
    /// producer assertions). The FAIL-CLOSED half is the important one: anything not cleanly parseable pins nothing
    /// and the producer is byte-identical to its pre-DW-499 self — the pin can never silently change a normal launch.</para>
    /// </summary>
    public class MatchSeedPinTests
    {
        /// <summary>The pre-DW-499 producer: the canonical SplitMix64 first draw, an oracle independent of
        /// <see cref="MatchSeedProducer"/> itself (the same non-tautological cross-check MatchSeedProducerTests uses).</summary>
        private static ulong Unpinned(ulong entropy) => new SimRng(entropy).NextRaw();

        // ── The pin, when it is set ──────────────────────────────────────────────────

        [Theory]
        [InlineData("0", 0UL)]
        [InlineData("1234", 1234UL)]
        [InlineData("18446744073709551615", ulong.MaxValue)]   // ulong.MaxValue, decimal
        [InlineData("0xCAFEF00D", 0xCAFEF00DUL)]
        [InlineData("0xcafef00d", 0xCAFEF00DUL)]               // hex digits are case-insensitive
        [InlineData("0Xcafef00d", 0xCAFEF00DUL)]               // …and so is the prefix
        [InlineData("0xFFFFFFFFFFFFFFFF", ulong.MaxValue)]
        [InlineData("  0x2A  ", 42UL)]                         // surrounding whitespace is tolerated
        public void PinnedSeed_IsReturnedVerbatim_IgnoringTheEntropy(string raw, ulong expected)
        {
            Assert.Equal(expected, MatchSeedProducer.Produce(entropy: 12345UL, raw));
            // The whole point of a pin: the per-launch entropy no longer influences the seed at all.
            Assert.Equal(expected, MatchSeedProducer.Produce(entropy: 999_999UL, raw));
        }

        /// <summary>The repro property stated directly: with the pin set, two "launches" whose wall-clock entropy
        /// differs produce the SAME match seed — which is exactly what an A/B verification run needs.</summary>
        [Fact]
        public void PinnedSeed_MakesTwoLaunchesReproduceEachOther()
        {
            const string pin = "0xDEADBEEF";
            Assert.Equal(MatchSeedProducer.Produce(1UL, pin), MatchSeedProducer.Produce(2UL, pin));
            // …while WITHOUT the pin the same two launches deliberately diverge (the per-match behaviour is intact).
            Assert.NotEqual(MatchSeedProducer.Produce(1UL, null), MatchSeedProducer.Produce(2UL, null));
        }

        // ── Fail-closed: anything unparseable pins NOTHING ───────────────────────────

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("banana")]
        [InlineData("-1")]                                     // negative → not a ulong
        [InlineData("12.5")]                                   // not an integer
        [InlineData("1_000")]                                  // digit separators are source syntax, not input
        [InlineData("0x")]                                     // prefix with no digits
        [InlineData("0xZZ")]
        [InlineData("18446744073709551616")]                   // ulong.MaxValue + 1 → overflow
        [InlineData("0x1FFFFFFFFFFFFFFFF")]                    // 17 hex digits → overflow
        [InlineData("42abc")]
        [InlineData("+42")]                                    // a sign is not accepted (NumberStyles.None)
        public void UnparseablePin_FallsBackToTheNormalMix(string? raw)
        {
            Assert.False(MatchSeedProducer.TryParsePinnedSeed(raw, out ulong seed));
            Assert.Equal(0UL, seed);

            // …and the producer is byte-identical to its pre-DW-499 self on every entropy value.
            foreach (ulong entropy in new ulong[] { 0UL, 1UL, 12345UL, ulong.MaxValue })
                Assert.Equal(Unpinned(entropy), MatchSeedProducer.Produce(entropy, raw));
        }

        /// <summary>The default (nothing pinned) path is EXACTLY the pre-DW-499 producer — no golden, checksum, or
        /// recorded replay seed can move because the override exists.</summary>
        [Theory]
        [InlineData(0UL)]
        [InlineData(1UL)]
        [InlineData(0xCAFEF00DUL)]
        [InlineData(ulong.MaxValue)]
        public void NoPin_ProducesTheUnchangedSplitMix64Seed(ulong entropy)
        {
            Assert.Equal(Unpinned(entropy), MatchSeedProducer.Produce(entropy, null));
        }

        // ── The parser, asserted directly ────────────────────────────────────────────

        [Theory]
        [InlineData("7", 7UL)]
        [InlineData("0x7", 7UL)]
        [InlineData("0x10", 16UL)]                             // hex, not decimal — the prefix is honoured
        public void TryParsePinnedSeed_ReadsBothDecimalAndHex(string raw, ulong expected)
        {
            Assert.True(MatchSeedProducer.TryParsePinnedSeed(raw, out ulong seed));
            Assert.Equal(expected, seed);
        }

        /// <summary>The env-var NAME is part of the contract (it is what a repro run sets), so pin it.</summary>
        [Fact]
        public void PinnedSeedEnvVarName_IsStable()
        {
            Assert.Equal("CHIMERA_MATCH_SEED", MatchSeedProducer.PINNED_SEED_ENV);
        }
    }
}
