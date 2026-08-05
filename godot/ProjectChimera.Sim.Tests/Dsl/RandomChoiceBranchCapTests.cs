#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using ProjectChimera.Core.Definitions;   // DslLoopGate
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// DW-579 — the <c>random_choice</c> BRANCH-COUNT cap, enforced at every entrance a
    /// <see cref="RandomChoiceNode"/> can arrive through, all reading the ONE
    /// <see cref="EventBounds.MaxRandomChoiceBranches"/> constant.
    ///
    /// <para>The defect: <c>weights</c> maps one-to-one onto RENDERED branch ports
    /// (<see cref="NodePortCatalog"/> / <see cref="NodePorts.IsExecOut"/>), but the parser accepted an array of
    /// any length — so a hand-authored or hostile raw-IR graph could make the T3 editor draw as many ports as the
    /// file asked for, long before the load gate (which DID cap it) ever ran. The field-level Set deliberately
    /// mirrored parse, so it invented no cap either, making the gap reachable from two directions.</para>
    ///
    /// <para>The property under test is a boundary AGREEMENT, not three independent limits: for every candidate
    /// width, parse accepts it exactly when the inspector accepts it, and the load gate rejects what neither
    /// admits. Tightening or raising the cap on one path only fails here.</para>
    ///
    /// <para>Deliberately NOT a second constant: the closure text for DW-579 named a
    /// <c>DslBounds.MaxRandomChoiceBranches</c>-style cap, but Story 7.13's caps already live in
    /// <see cref="EventBounds"/> and the load gate already enforced this one. Introducing a DslBounds twin would
    /// be two dials for one limit — the exact drift those bounds files exist to prevent.</para>
    /// </summary>
    public class RandomChoiceBranchCapTests
    {
        private const int Cap = EventBounds.MaxRandomChoiceBranches;

        private static readonly Dictionary<string, (DslValueType Type, VarScope Scope)> NoVars =
            new(StringComparer.Ordinal);
        private static readonly Dictionary<string, (DslValueType Elem, int Capacity)> NoArrays =
            new(StringComparer.Ordinal);

        /// <summary>A weights array of <paramref name="count"/> ones (a legal, positive-total weight set at any
        /// width — so width is the ONLY thing under test; no other gate rule can mask the result).</summary>
        private static int[] Ones(int count) => Enumerable.Repeat(1, count).ToArray();

        /// <summary>A raw-IR single-node graph carrying a <c>random_choice</c> with <paramref name="count"/>
        /// weights — hand-built JSON, the entrance an authored/hostile file actually uses (never
        /// <c>ToCanonicalJson</c>, which is now capped on the write side too).</summary>
        private static string RawGraphWithWeights(int count)
        {
            var sb = new StringBuilder();
            sb.Append(@"{ ""nodes"": [ { ""id"": 0, ""kind"": ""random_choice"", ""weights"": [");
            for (int i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('1');
            }
            sb.Append(@"] } ], ""exec_edges"": [], ""data_edges"": [] }");
            return sb.ToString();
        }

        private static bool ParseAccepts(int count)
        {
            try
            {
                var rc = (RandomChoiceNode)TriggerGraph.FromJson(RawGraphWithWeights(count)).Nodes.Single();
                Assert.Equal(count, rc.Weights.Length);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool InspectorAccepts(int count)
        {
            var rc = new RandomChoiceNode { Id = 0, Weights = Array.Empty<int>() };
            NodeFieldDef weights = NodeFieldCatalog.FieldsOf(rc).Single(f => f.Key == "weights");
            string? err = weights.Set(string.Join(", ", Enumerable.Repeat("1", count)));
            if (err == null) Assert.Equal(count, rc.Weights.Length);
            else Assert.Empty(rc.Weights);           // rejected → node left untouched
            return err == null;
        }

        private static int BranchPortCount(NodeBase n)
        {
            var ins = new List<GraphPortSpec>();
            var outs = new List<GraphPortSpec>();
            NodePortCatalog.PortsOf(n, ins, outs);
            return outs.Count(p => !p.IsData && p.Port >= TriggerGraph.RandomChoiceBranchOutPort0);
        }

        // ── PARSE (the DW-579 entrance) ────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Parse_AtTheCap_Succeeds_AndRendersExactlyThatManyBranchPorts()
        {
            var rc = (RandomChoiceNode)TriggerGraph.FromJson(RawGraphWithWeights(Cap)).Nodes.Single();
            Assert.Equal(Cap, rc.Weights.Length);
            Assert.Equal(Cap, BranchPortCount(rc));   // the cap IS the rendered fan-out bound
        }

        [Fact]
        public void Parse_OneOverTheCap_IsALocatedReject()
        {
            var ex = Assert.Throws<JsonException>(() => TriggerGraph.FromJson(RawGraphWithWeights(Cap + 1)));
            Assert.Contains("weights", ex.Message, StringComparison.Ordinal);            // located at the field
            Assert.Contains("MaxRandomChoiceBranches", ex.Message, StringComparison.Ordinal); // names the constant
            Assert.Contains((Cap + 1).ToString(), ex.Message, StringComparison.Ordinal);      // and the offense
        }

        /// <summary>The headline DW-579 scenario: an ARBITRARILY long weights array. Pre-fix this parsed happily
        /// and handed the editor a node asking for 50,000 branch ports; now it is a located reject and no node is
        /// produced at all.</summary>
        [Fact]
        public void Parse_ArbitrarilyLongWeightsArray_IsRejected_AndYieldsNoNode()
        {
            const int Absurd = 50_000;
            var ex = Assert.Throws<JsonException>(() => TriggerGraph.FromJson(RawGraphWithWeights(Absurd)));
            Assert.Contains("MaxRandomChoiceBranches", ex.Message, StringComparison.Ordinal);
            Assert.Contains(Absurd.ToString(), ex.Message, StringComparison.Ordinal);
        }

        /// <summary>An over-cap array must be refused from its DECLARED length — never element-by-element into a
        /// buffer sized by the file. A 200k-element array is rejected without the parser ever materialising it.</summary>
        [Fact]
        public void Parse_OverCapArray_IsRefusedWithoutAllocatingTheFileSizedBuffer()
        {
            const int Huge = 200_000;
            var ex = Assert.Throws<JsonException>(() => TriggerGraph.FromJson(RawGraphWithWeights(Huge)));
            Assert.Contains($"{Huge} entries exceed", ex.Message, StringComparison.Ordinal);
        }

        // ── INSPECTOR (the mirrored entrance) ──────────────────────────────────────────────────────────────────

        [Fact]
        public void Inspector_AtTheCap_Succeeds()
        {
            Assert.True(InspectorAccepts(Cap));
        }

        [Fact]
        public void Inspector_OverTheCap_RejectsAndLeavesTheNodeUntouched()
        {
            var rc = new RandomChoiceNode { Id = 3, Weights = new[] { 2, 3 } };
            NodeFieldDef weights = NodeFieldCatalog.FieldsOf(rc).Single(f => f.Key == "weights");

            string? err = weights.Set(string.Join(", ", Enumerable.Repeat("1", Cap + 1)));
            Assert.NotNull(err);
            Assert.Contains("MaxRandomChoiceBranches", err!, StringComparison.Ordinal);
            Assert.Equal(new[] { 2, 3 }, rc.Weights);      // untouched on reject
            Assert.Equal(2, BranchPortCount(rc));          // and the rendered ports did not grow
        }

        // ── The AGREEMENT the bundle exists to establish ───────────────────────────────────────────────────────

        /// <summary>Parse and the inspector accept EXACTLY the same widths — the DW-579 requirement that the two
        /// authoring entrances stay in agreement (an inspector-accepted value must survive the canonical
        /// serialize→re-parse round-trip, and a parse-legal file must stay editable).</summary>
        [Theory]
        [InlineData(1)]
        [InlineData(Cap - 1)]
        [InlineData(Cap)]
        [InlineData(Cap + 1)]
        [InlineData(Cap + 7)]
        public void ParseAndInspector_AgreeOnEveryWidth(int count)
        {
            Assert.Equal(ParseAccepts(count), InspectorAccepts(count));
        }

        /// <summary>An inspector-accepted weight set really does survive the canonical round-trip at the widest
        /// legal width — the seam's core guarantee, at the exact boundary the new cap creates.</summary>
        [Fact]
        public void Inspector_MaxWidthEdit_SurvivesTheCanonicalRoundTrip()
        {
            var rc = new RandomChoiceNode { Id = 0, Weights = Array.Empty<int>() };
            NodeFieldDef weights = NodeFieldCatalog.FieldsOf(rc).Single(f => f.Key == "weights");
            Assert.Null(weights.Set(string.Join(", ", Enumerable.Range(1, Cap))));

            var g = new TriggerGraph();
            g.Nodes.Add(rc);
            var back = (RandomChoiceNode)TriggerGraph.FromJson(g.ToCanonicalJson()).Nodes.Single();
            Assert.Equal(Enumerable.Range(1, Cap).ToArray(), back.Weights);
            Assert.Equal(Cap, BranchPortCount(back));
        }

        // ── The remaining entrance: a node built in CODE (neither parsed nor inspector-edited) ─────────────────

        /// <summary>The load gate keeps its own cap check — it is the only guard left for a graph assembled
        /// programmatically, and it must reject at the SAME width, naming the same constant.</summary>
        [Fact]
        public void LoadGate_StillRejectsAnOverCapNode_BuiltInCode()
        {
            string? err = CheckBuiltGraph(Cap + 1);
            Assert.NotNull(err);
            Assert.Contains("random_choice", err!, StringComparison.Ordinal);
            Assert.Contains("MaxRandomChoiceBranches", err!, StringComparison.Ordinal);

            Assert.Null(CheckBuiltGraph(Cap));   // and the widest legal node still passes
        }

        /// <summary>Write stays the exact inverse of Read: an over-cap node built in code cannot be emitted as
        /// JSON the parser would then refuse (the persist-then-cannot-load class).</summary>
        [Fact]
        public void Serialize_OverCapNode_ThrowsRatherThanEmittingUnreadableJson()
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new RandomChoiceNode { Id = 0, Weights = Ones(Cap + 1) });

            var ex = Assert.ThrowsAny<JsonException>(() => g.ToCanonicalJson());
            Assert.Contains("MaxRandomChoiceBranches", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>match_start → trigger → random_choice(<paramref name="branches"/> weights), one
        /// set_variable-free action leaf per branch, run through the REAL shared load gate.</summary>
        private static string? CheckBuiltGraph(int branches)
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new RandomChoiceNode { Id = 2, Weights = Ones(branches) });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            for (int k = 0; k < branches; k++)
            {
                int id = 3 + k;
                g.Nodes.Add(new ActionNode { Id = id, Kind = "display_message", Text = "b" + k });
                g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.RandomChoiceBranchOutPort0 + k, id, TriggerGraph.ActionExecInPort));
            }
            return DslLoopGate.CheckGraph(g, g.BuildExecutionOrder(), NoVars, NoArrays, _ => false);
        }
    }
}
