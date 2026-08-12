#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.Determinism
{
    /// <summary>
    /// DW-753 — <see cref="FixedVec3"/> hash determinism, the SIMULATION-layer instance of the DW-337 defect class.
    ///
    /// <para>The struct used to hash through <c>System.HashCode.Combine</c>, which mixes in a per-PROCESS random
    /// seed: the same build handed the same position a different hash code on every launch. Nothing keys a
    /// <c>HashSet</c>/<c>Dictionary</c> on a position today (audited across <c>godot/src</c> and this suite when the
    /// fold landed), so it was latent — but this is sim-layer state, so the moment one does, bucket layout and any
    /// enumeration order surviving a remove/re-add become run-dependent, i.e. a lockstep desync source rather than an
    /// editor wart. The fix is the same seed-free FNV-1a-32 fold, over <c>X.Raw, Y.Raw, Z.Raw</c> in that fixed
    /// order.</para>
    ///
    /// <para>A single-process test cannot observe the process seed directly (it is a static readonly initialized from
    /// randomness before any test runs), so the failing-without-the-fix assertion is EQUALITY AGAINST AN
    /// INDEPENDENTLY-COMPUTED FNV FOLD plus a table of PINNED digests: <c>HashCode.Combine</c> can reproduce
    /// neither, in any process. The digests are the cross-process / cross-runtime / cross-machine contract.
    /// Changing them is a deliberate act, never a "make the test pass" edit.</para>
    ///
    /// <para>Scope: these hashes are NOT folded into <c>SimChecksum</c>, <c>CanonicalModelHash</c> or
    /// <c>StartStateHash</c> — those fold <c>Fixed.Raw</c> component-wise and never call <c>GetHashCode</c> — so no
    /// golden moves with this change.</para>
    /// </summary>
    public class FixedVec3DeterministicHashTests
    {
        // ── An independent re-implementation of FNV-1a-32 over 4 little-endian bytes per field. Deliberately NOT a
        //    call into the production fold: the test must be able to disagree with it. ────────────────────────────
        private const uint FnvOffset = 2166136261u;
        private const uint FnvPrime = 16777619u;

        private static uint MixLe(uint h, int value)
        {
            unchecked
            {
                uint v = (uint)value;
                for (int shift = 0; shift < 32; shift += 8)
                {
                    h ^= (v >> shift) & 0xFF;
                    h *= FnvPrime;
                }
                return h;
            }
        }

        private static int ExpectedHash(int xRaw, int yRaw, int zRaw)
        {
            uint h = FnvOffset;
            h = MixLe(h, xRaw);
            h = MixLe(h, yRaw);
            h = MixLe(h, zRaw);
            return unchecked((int)h);
        }

        private static FixedVec3 Raw(int xRaw, int yRaw, int zRaw) =>
            new FixedVec3(Fixed.FromRaw(xRaw), Fixed.FromRaw(yRaw), Fixed.FromRaw(zRaw));

        // ── The fold IS the deterministic FNV, not the process-seeded HashCode.Combine ──────────────────────────

        /// <summary>Every vector hashes to the seed-free FNV fold of its three raw components, in order.</summary>
        [Theory]
        [InlineData(0, 0, 0)]
        [InlineData(65536, 0, 0)]                                   // (1, 0, 0)
        [InlineData(-655360, 0, 262144)]                            // (-10, 0, 4) — a real golden spawn position
        [InlineData(1, 2, 3)]                                       // sub-unit raws
        [InlineData(int.MaxValue, int.MinValue, -1)]                // the wrap-around extremes
        public void Hash_IsTheSeedFreeFnvFold(int xRaw, int yRaw, int zRaw)
        {
            Assert.Equal(ExpectedHash(xRaw, yRaw, zRaw), Raw(xRaw, yRaw, zRaw).GetHashCode());
        }

        // ── The cross-process / cross-runtime pin ───────────────────────────────────────────────────────────────

        /// <summary>
        /// PINNED digests, computed from the FNV-1a-32 specification OUTSIDE C#. These are the values every process
        /// on every platform must produce, which is precisely what the old <c>HashCode.Combine</c> could not promise.
        /// A change here means the position hash moved — investigate, do not re-record.
        /// </summary>
        [Theory]
        [InlineData(0, 0, 0, 0xE23C62B5u)]
        [InlineData(65536, 0, 0, 0x89B4A42Cu)]
        [InlineData(-655360, 0, 262144, 0xA270A91Eu)]
        [InlineData(1, 2, 3, 0x794671B5u)]
        [InlineData(int.MaxValue, int.MinValue, -1, 0xC2E24E6Du)]
        public void Hash_MatchesThePinnedDigest(int xRaw, int yRaw, int zRaw, uint pinned)
        {
            Assert.Equal(unchecked((int)pinned), Raw(xRaw, yRaw, zRaw).GetHashCode());
        }

        // ── Equals/GetHashCode contract (the fold must cover exactly what Equals compares) ──────────────────────

        /// <summary>Equal vectors hash equally — including across construction routes and a boxed dispatch, so
        /// nothing per-instance or per-reference leaks into the fold.</summary>
        [Fact]
        public void Hash_IsAPureFunctionOfTheComponents()
        {
            var a = new FixedVec3(Fixed.FromInt(-10), Fixed.Zero, Fixed.FromInt(4));
            var b = Raw(-655360, 0, 262144);
            object boxed = new FixedVec3(Fixed.FromInt(-10), Fixed.Zero, Fixed.FromInt(4));

            Assert.Equal(a, b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.Equal(a.GetHashCode(), boxed.GetHashCode());
        }

        /// <summary>
        /// Every component participates, POSITIONALLY: a value moved between axes must change the digest. A
        /// commutative fold (an XOR/sum of per-component hashes) would collide these, which is how a "deterministic"
        /// rewrite silently loses axis information.
        /// </summary>
        [Fact]
        public void Hash_IsPositionSensitiveAcrossTheThreeAxes()
        {
            var baseline = Raw(65536, 0, 0);
            var perturbations = new[]
            {
                Raw(0, 65536, 0),   // same value, Y axis
                Raw(0, 0, 65536),   // same value, Z axis
                Raw(65537, 0, 0),   // one raw tick apart
                Raw(65536, 1, 0),
                Raw(65536, 0, 1),
            };

            foreach (FixedVec3 p in perturbations)
            {
                Assert.NotEqual(baseline, p);
                Assert.NotEqual(baseline.GetHashCode(), p.GetHashCode());
            }
        }

        /// <summary>The use case DW-753 pre-empts: a position-keyed hash container dedups correctly and keeps
        /// distinct positions apart — the fold is a valid hash, not merely a stable one. Enumeration is SORTED
        /// (component-wise), matching the rule that hash-container order is never a contract.</summary>
        [Fact]
        public void PositionSets_DedupCorrectly()
        {
            var set = new HashSet<FixedVec3>
            {
                new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(2)),
                new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(2)), // duplicate
                new FixedVec3(Fixed.FromInt(2), Fixed.Zero, Fixed.FromInt(1)), // axis swap — distinct
                new FixedVec3(Fixed.FromInt(1), Fixed.FromRaw(1), Fixed.FromInt(2)), // one raw tick of Y — distinct
            };

            Assert.Equal(3, set.Count);
            Assert.Equal(
                new[]
                {
                    new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(2)),
                    new FixedVec3(Fixed.FromInt(1), Fixed.FromRaw(1), Fixed.FromInt(2)),
                    new FixedVec3(Fixed.FromInt(2), Fixed.Zero, Fixed.FromInt(1)),
                },
                set.OrderBy(v => v.X.Raw).ThenBy(v => v.Y.Raw).ThenBy(v => v.Z.Raw).ToArray());
        }

        /// <summary>
        /// Non-vacuity fence for the whole family: <see cref="Fixed"/> itself hashes to its raw value, so a fold over
        /// the three components is the ONLY seed-bearing surface a position ever had. If this stops holding, the
        /// scalar type has grown a hash of its own and needs the same treatment.
        /// </summary>
        [Fact]
        public void TheScalarFixedHash_IsStillItsRawValue()
        {
            foreach (int raw in new[] { 0, 1, -1, 65536, int.MaxValue, int.MinValue })
                Assert.Equal(raw, Fixed.FromRaw(raw).GetHashCode());
        }
    }
}
