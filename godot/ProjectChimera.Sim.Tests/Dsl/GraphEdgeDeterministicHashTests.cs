#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// DW-337 — <see cref="ExecEdge"/>/<see cref="DataEdge"/> hash determinism.
    ///
    /// Both structs used to hash through <c>System.HashCode.Combine</c>, which mixes in a per-PROCESS random
    /// seed: the same build handed the same edge a different hash code on every launch. Nothing hashed edges
    /// (all ordering goes through <c>IComparable.CompareTo</c>), so it was latent — but the moment graph
    /// editing/dedup keys a <c>HashSet</c>/<c>Dictionary</c> on edges, bucket layout becomes run-dependent and
    /// a lockstep sim cannot tolerate that. The fix is a seed-free FNV-1a-32 fold in a fixed field order.
    ///
    /// A single-process test cannot observe the process seed directly (it is a static readonly initialized from
    /// randomness before any test runs), so the failing-without-the-fix assertion is EQUALITY AGAINST AN
    /// INDEPENDENTLY-COMPUTED FNV FOLD plus a table of PINNED digests: <c>HashCode.Combine</c> cannot reproduce
    /// either. The pinned digests are the cross-process/cross-runtime contract — they were computed outside C#
    /// from the FNV-1a-32 specification, so they also prove the production fold is the real algorithm and not a
    /// self-consistent variant. Changing them is a deliberate act, never a "make the test pass" edit.
    ///
    /// Scope note: these hashes are NOT folded into <c>SimChecksum</c>, <c>CanonicalModelHash</c> or
    /// <c>StartStateHash</c> (those hash the edge FIELDS in sorted order), so no golden moves with this change.
    /// </summary>
    public class GraphEdgeDeterministicHashTests
    {
        // ── An independent re-implementation of FNV-1a-32 over 4 little-endian bytes per field. Deliberately
        //    NOT a call into the production helper: the test must be able to disagree with it. ──────────────
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

        private static int ExpectedExecHash(int src, int srcPort, int dst, int dstPort)
        {
            uint h = FnvOffset;
            h = MixLe(h, src);
            h = MixLe(h, srcPort);
            h = MixLe(h, dst);
            h = MixLe(h, dstPort);
            return unchecked((int)h);
        }

        private static int ExpectedDataHash(int src, int srcPort, int dst, int dstPort, DataWireType wire) =>
            unchecked((int)MixLe((uint)ExpectedExecHash(src, srcPort, dst, dstPort), (int)wire));

        // ── The fold IS the deterministic FNV, not the process-seeded HashCode.Combine ──────────────────────

        /// <summary>Every exec edge hashes to the seed-free FNV fold of its four topology fields, in order.</summary>
        [Theory]
        [InlineData(0, 0, 0, 0)]
        [InlineData(1, 0, 2, 3)]
        [InlineData(3, 2, 0, 1)]
        [InlineData(7, 1, 7, 1)]
        [InlineData(-1, 7, int.MaxValue, int.MinValue)]
        public void ExecEdgeHash_IsTheSeedFreeFnvFold(int src, int srcPort, int dst, int dstPort)
        {
            var e = new ExecEdge(src, srcPort, dst, dstPort);
            Assert.Equal(ExpectedExecHash(src, srcPort, dst, dstPort), e.GetHashCode());
        }

        /// <summary>Every data edge hashes to the exec fold with the wire ordinal appended — no process seed.</summary>
        [Theory]
        [InlineData(0, 0, 0, 0, DataWireType.Boolean)]
        [InlineData(1, 0, 2, 3, DataWireType.Boolean)]
        [InlineData(1, 0, 2, 3, DataWireType.Int)]
        [InlineData(1, 0, 2, 3, DataWireType.Fixed)]
        [InlineData(1, 0, 2, 3, DataWireType.Point)]
        [InlineData(-1, 7, int.MaxValue, int.MinValue, DataWireType.Fixed)]
        public void DataEdgeHash_IsTheSeedFreeFnvFold(int src, int srcPort, int dst, int dstPort, DataWireType wire)
        {
            var e = new DataEdge(src, srcPort, dst, dstPort, wire);
            Assert.Equal(ExpectedDataHash(src, srcPort, dst, dstPort, wire), e.GetHashCode());
        }

        // ── The cross-process / cross-runtime pin ───────────────────────────────────────────────────────────

        /// <summary>
        /// PINNED digests, computed from the FNV-1a-32 spec outside C#. These are the values every process on
        /// every platform must produce. A change here means the edge hash moved — investigate, do not re-record.
        /// </summary>
        [Theory]
        [InlineData(0, 0, 0, 0, 0x69691905u)]
        [InlineData(1, 0, 2, 3, 0x9FA415F5u)]
        [InlineData(3, 2, 0, 1, 0x46D7F9F5u)]
        [InlineData(-1, 7, int.MaxValue, int.MinValue, 0x36B7CB32u)]
        public void ExecEdgeHash_MatchesThePinnedDigest(int src, int srcPort, int dst, int dstPort, uint pinned)
        {
            Assert.Equal(unchecked((int)pinned), new ExecEdge(src, srcPort, dst, dstPort).GetHashCode());
        }

        /// <summary>PINNED data-edge digests (same provenance as the exec table, with the wire ordinal folded last).</summary>
        [Theory]
        [InlineData(0, 0, 0, 0, DataWireType.Boolean, 0xB9FEE455u)]
        [InlineData(1, 0, 2, 3, DataWireType.Boolean, 0x5168C045u)]
        [InlineData(1, 0, 2, 3, DataWireType.Int, 0x013C8134u)]
        [InlineData(1, 0, 2, 3, DataWireType.Fixed, 0xF1C13E67u)]
        public void DataEdgeHash_MatchesThePinnedDigest(
            int src, int srcPort, int dst, int dstPort, DataWireType wire, uint pinned)
        {
            Assert.Equal(unchecked((int)pinned), new DataEdge(src, srcPort, dst, dstPort, wire).GetHashCode());
        }

        // ── Equals/GetHashCode contract (the fold must still cover exactly what Equals compares) ────────────

        /// <summary>Equal exec edges hash equally, and each topology field participates in the fold.</summary>
        [Fact]
        public void ExecEdgeHash_AgreesWithEquals_AndEveryFieldParticipates()
        {
            var baseline = new ExecEdge(1, 0, 2, 3);
            Assert.Equal(baseline.GetHashCode(), new ExecEdge(1, 0, 2, 3).GetHashCode());

            var perturbations = new[]
            {
                new ExecEdge(9, 0, 2, 3),
                new ExecEdge(1, 9, 2, 3),
                new ExecEdge(1, 0, 9, 3),
                new ExecEdge(1, 0, 2, 9),
                new ExecEdge(0, 1, 2, 3), // src/srcPort swapped — position-sensitive, not a commutative sum
                new ExecEdge(2, 3, 1, 0), // src/dst pairs swapped — direction is part of the identity
            };
            foreach (ExecEdge p in perturbations)
            {
                Assert.NotEqual(baseline, p);
                Assert.NotEqual(baseline.GetHashCode(), p.GetHashCode());
            }
        }

        /// <summary>
        /// The wire type participates: <see cref="DataEdge.Equals(DataEdge)"/> compares it, so the hash must fold
        /// it or two unequal edges would collide by construction (and a dedup set would silently drop a wire).
        /// </summary>
        [Fact]
        public void DataEdgeHash_FoldsTheWireType()
        {
            var boolean = new DataEdge(1, 0, 2, 3, DataWireType.Boolean);
            foreach (DataWireType w in new[] { DataWireType.Int, DataWireType.Fixed, DataWireType.Point })
            {
                var other = new DataEdge(1, 0, 2, 3, w);
                Assert.NotEqual(boolean, other);
                Assert.NotEqual(boolean.GetHashCode(), other.GetHashCode());
            }
            Assert.Equal(boolean.GetHashCode(), new DataEdge(1, 0, 2, 3, DataWireType.Boolean).GetHashCode());
        }

        /// <summary>
        /// A data edge and the exec edge with the same topology are different identities; folding the wire last
        /// keeps their digests apart even for <see cref="DataWireType.Boolean"/> (ordinal 0), which a plain
        /// "skip the zero field" fold would have collided.
        /// </summary>
        [Fact]
        public void DataEdgeHash_IsNotTheExecEdgeHashForTheSameTopology()
        {
            Assert.NotEqual(
                new ExecEdge(1, 0, 2, 3).GetHashCode(),
                new DataEdge(1, 0, 2, 3, DataWireType.Boolean).GetHashCode());
        }

        // ── Hash-keyed containers behave (the use case DW-337 pre-empts) ────────────────────────────────────

        /// <summary>
        /// Dedup through a hash set still collapses duplicates and keeps distinct edges — the fold is a valid
        /// hash, not just a stable one. Enumeration is SORTED, matching the documented rule that hash-container
        /// order is never a contract.
        /// </summary>
        [Fact]
        public void EdgeSets_DedupCorrectly_AndSortForOrderSensitiveOutput()
        {
            var exec = new HashSet<ExecEdge>
            {
                new ExecEdge(3, 0, 4, 0),
                new ExecEdge(1, 0, 2, 0),
                new ExecEdge(3, 0, 4, 0), // duplicate
                new ExecEdge(1, 0, 2, 1),
            };
            Assert.Equal(3, exec.Count);
            Assert.Equal(
                new[] { new ExecEdge(1, 0, 2, 0), new ExecEdge(1, 0, 2, 1), new ExecEdge(3, 0, 4, 0) },
                exec.OrderBy(x => x).ToArray());

            var data = new HashSet<DataEdge>
            {
                new DataEdge(1, 0, 2, 3, DataWireType.Boolean),
                new DataEdge(1, 0, 2, 3, DataWireType.Boolean), // duplicate
                new DataEdge(1, 0, 2, 3, DataWireType.Int),     // same topology, different wire — distinct
            };
            Assert.Equal(2, data.Count);
        }

        /// <summary>
        /// The fold is a pure function of the field values: recomputing it from freshly constructed structs
        /// (and via a boxed <c>object</c> dispatch) yields the identical value, so nothing per-instance or
        /// per-reference leaks in.
        /// </summary>
        [Fact]
        public void EdgeHashes_ArePureFunctionsOfTheFieldValues()
        {
            for (int i = 0; i < 8; i++)
            {
                var e = new ExecEdge(i, i % 3, i + 1, (i + 2) % 4);
                object boxed = new ExecEdge(i, i % 3, i + 1, (i + 2) % 4);
                Assert.Equal(e.GetHashCode(), boxed.GetHashCode());

                var d = new DataEdge(i, i % 3, i + 1, (i + 2) % 4, (DataWireType)(i % 4));
                object boxedData = new DataEdge(i, i % 3, i + 1, (i + 2) % 4, (DataWireType)(i % 4));
                Assert.Equal(d.GetHashCode(), boxedData.GetHashCode());
            }
        }

        // ── The other half of DW-337: ToCanonicalJson is not a hash source ──────────────────────────────────

        /// <summary>
        /// <c>CanonicalModelHash</c> must fold the graph's TYPED FIELDS, never <c>ToCanonicalJson</c>'s bytes
        /// (System.Text.Json indentation/number formatting is not a cross-runtime contract). Proven behaviorally:
        /// two graphs whose canonical JSON is byte-identical hash identically, while a graph differing ONLY in a
        /// data edge's wire type — a field the JSON carries and the typed fold folds — hashes differently. A
        /// hash that ignored the wire (or that keyed on incidental text) fails one of these.
        /// </summary>
        [Fact]
        public void CanonicalModelHash_FoldsGraphFields_NotCanonicalJsonBytes()
        {
            static ProjectChimera.Core.Definitions.ScenarioData Model(DataWireType wire)
            {
                var g = new TriggerGraph();
                g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
                g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
                g.Nodes.Add(new ActionNode { Id = 2, Kind = "display_message", Text = "hi" });
                g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
                g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
                g.DataEdges.Add(new DataEdge(2, TriggerGraph.ExprDataOutPort, 0, TriggerGraph.TriggerConditionInPort, wire));
                return new ProjectChimera.Core.Definitions.ScenarioData { TriggerGraphJson = g.ToCanonicalJson() };
            }

            ProjectChimera.Core.Definitions.ScenarioData a = Model(DataWireType.Boolean);
            ProjectChimera.Core.Definitions.ScenarioData b = Model(DataWireType.Boolean);
            ProjectChimera.Core.Definitions.ScenarioData c = Model(DataWireType.Int);

            Assert.Equal(a.TriggerGraphJson, b.TriggerGraphJson);
            Assert.NotEqual(a.TriggerGraphJson, c.TriggerGraphJson);

            ulong ha = ProjectChimera.Core.Definitions.CanonicalModelHash.Compute(a);
            ulong hb = ProjectChimera.Core.Definitions.CanonicalModelHash.Compute(b);
            ulong hc = ProjectChimera.Core.Definitions.CanonicalModelHash.Compute(c);

            Assert.Equal(ha, hb);
            Assert.NotEqual(ha, hc);
        }
    }
}
