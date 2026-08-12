#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// DW-754 — <see cref="DataEdge.CompareTo"/> must be a TOTAL order, so no consumer's <c>OrderBy</c> can fall
    /// back to LINQ sort stability (i.e. to AUTHORING order).
    ///
    /// <para><b>The defect.</b> The comparer stopped at the <c>(Src,SrcPort,Dst,DstPort)</c> topology tuple while
    /// <see cref="DataEdge.Equals(DataEdge)"/> also compared <see cref="DataEdge.Wire"/>. Two data edges sharing a
    /// topology tuple but differing in wire therefore compared EQUAL while being unequal VALUES — an inconsistent
    /// <c>IComparable</c>/<c>IEquatable</c> pair, and a silent one. Every <c>DataEdges.OrderBy(e =&gt; e)</c> in the
    /// module (canonical emission, the expression compiler's resolution order, the structural gate's first-fail
    /// order, the loop gate, the validator, the director) inherited it: on such a pair their relative order was
    /// decided by insertion order. <c>TriggerGraph.ToCanonicalJson</c> is where that becomes visible — two
    /// structurally-equal graphs built in different orders could serialize to DIFFERENT bytes, directly weakening
    /// the documented byte-identity claim that the persistence + editor-diff format rests on.</para>
    ///
    /// <para><b>Why the comparer and not just a <c>.ThenBy</c> at the emitter.</b>
    /// <c>CanonicalModelHash.MixTriggerGraph</c> had already hand-broken the tie with <c>.ThenBy(x =&gt; x.Wire)</c>
    /// — which is precisely how the hash stayed safe while the serializer did not. A footgun that every future
    /// order-sensitive consumer has to remember is the defect; making the comparer total removes it once.</para>
    ///
    /// <para>Godot-free, no <c>Fixed</c>, no sim state.</para>
    /// </summary>
    public class DataEdgeTotalOrderTests
    {
        /// <summary>The DW-754 pair: identical topology, different wire — the only shape that could tie.</summary>
        private static readonly DataEdge BooleanArm = new(1, 0, 2, 3, DataWireType.Boolean);
        private static readonly DataEdge IntArm     = new(1, 0, 2, 3, DataWireType.Int);

        // ── The comparer itself ───────────────────────────────────────────────────────────────────────────

        [Fact]
        public void TwoEdgesDifferingOnlyInWire_DoNotCompareEqual()
        {
            // RED pre-fix: 0. That single zero is the whole defect — it is what let sort stability decide.
            Assert.True(BooleanArm.CompareTo(IntArm) < 0);
            Assert.True(IntArm.CompareTo(BooleanArm) > 0);
        }

        [Fact]
        public void CompareToIsConsistentWithEquals_OverTheWholeWireVocabulary()
        {
            // The .NET contract an inconsistent pair breaks: CompareTo == 0 exactly when Equals is true. Swept over
            // every wire type so a future appended member cannot re-open the hole for itself alone.
            DataWireType[] wires = (DataWireType[])Enum.GetValues(typeof(DataWireType));
            foreach (DataWireType a in wires)
                foreach (DataWireType b in wires)
                {
                    var ea = new DataEdge(4, 1, 5, 2, a);
                    var eb = new DataEdge(4, 1, 5, 2, b);
                    Assert.Equal(ea.Equals(eb), ea.CompareTo(eb) == 0);
                }
        }

        [Fact]
        public void TheTopologyTupleStillDominatesTheWire()
        {
            // Tooth against the wrong fix (sorting by wire first): the wire is the LAST key, so canonical emission
            // order is unchanged for every graph that has no duplicate-topology pair — i.e. every shipped graph.
            var lowTopologyHighWire  = new DataEdge(1, 0, 2, 3, DataWireType.Point);
            var highTopologyLowWire  = new DataEdge(1, 0, 2, 4, DataWireType.Boolean);
            Assert.True(lowTopologyHighWire.CompareTo(highTopologyLowWire) < 0);
        }

        [Fact]
        public void SortingIsAntisymmetricAndTransitive_AcrossAFullDuplicateTopologyFan()
        {
            // A total order must survive being sorted from an arbitrary permutation: build all four wires on ONE
            // topology, sort each permutation, and require the identical result every time.
            var fan = ((DataWireType[])Enum.GetValues(typeof(DataWireType)))
                .Select(w => new DataEdge(7, 0, 8, 1, w))
                .ToArray();

            DataEdge[] expected = fan.OrderBy(e => (int)e.Wire).ToArray();
            foreach (IEnumerable<DataEdge> permutation in Permutations(fan))
                Assert.Equal(expected, permutation.OrderBy(e => e).ToArray());
        }

        // ── The build-order-independence pin the ledger asks for ──────────────────────────────────────────

        [Fact]
        public void TwoGraphsWithTheSameDuplicateTopologyEdges_SerializeByteIdentically_RegardlessOfBuildOrder()
        {
            // THE closure test. Same graph, edges appended in opposite orders. RED pre-fix: the two canonical JSON
            // strings differ, because OrderBy was stable and the pair tied — the "two structurally-equal graphs
            // serialize byte-identically" claim in ToCanonicalJson's own doc was false for this shape.
            string forward  = CanonicalOf(BooleanArm, IntArm);
            string reversed = CanonicalOf(IntArm, BooleanArm);

            Assert.Equal(forward, reversed);
            // And the surviving order is the DECLARED one (Boolean before Int, the enum ordinal), not "whichever
            // was appended first" — pinned so a future comparer edit that reverses the tiebreak is deliberate.
            Assert.True(forward.IndexOf("\"Boolean\"", StringComparison.Ordinal)
                      < forward.IndexOf("\"Int\"", StringComparison.Ordinal));
        }

        [Fact]
        public void TheCanonicalRoundTrip_SurvivesTheDuplicateTopologyPair()
        {
            // Both edges must still be PRESENT after the sort — a "tiebreak" implemented as a dedup would silently
            // drop one and pass every ordering assertion above.
            TriggerGraph back = TriggerGraph.FromJson(CanonicalOf(IntArm, BooleanArm));

            Assert.Equal(2, back.DataEdges.Count);
            Assert.Contains(BooleanArm, back.DataEdges);
            Assert.Contains(IntArm, back.DataEdges);
            Assert.Equal(CanonicalOf(IntArm, BooleanArm), back.ToCanonicalJson()); // idempotent re-emission
        }

        // ── helpers ───────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A minimal graph carrying <paramref name="edges"/> in the order given, canonically serialized.
        /// Nodes are deliberately absent: <c>ToCanonicalJson</c> is a pure serializer (the structural rulebook is
        /// <c>GraphStructureGate</c>, run at the load gates), so this isolates the edge sort.</summary>
        private static string CanonicalOf(params DataEdge[] edges)
        {
            var g = new TriggerGraph();
            foreach (DataEdge e in edges) g.DataEdges.Add(e);
            return g.ToCanonicalJson();
        }

        /// <summary>Every permutation of <paramref name="items"/> (4! = 24 here — cheap and exhaustive).</summary>
        private static IEnumerable<IEnumerable<T>> Permutations<T>(IReadOnlyList<T> items)
        {
            if (items.Count <= 1)
            {
                yield return items;
                yield break;
            }
            for (int i = 0; i < items.Count; i++)
            {
                T head = items[i];
                var rest = items.Where((_, j) => j != i).ToList();
                foreach (IEnumerable<T> tail in Permutations(rest))
                    yield return new[] { head }.Concat(tail);
            }
        }
    }
}
