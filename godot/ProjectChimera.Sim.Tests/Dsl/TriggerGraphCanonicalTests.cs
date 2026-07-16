#nullable enable
using System.Collections.Generic;
using System.Text.Json;
using ProjectChimera.Core;   // Fixed
using ProjectChimera.Dsl;    // TriggerGraph, node/edge types
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// Story 7.2 — canonical serialization: <see cref="TriggerGraph.ToCanonicalJson"/> emits nodes sorted by
    /// ascending id and each edge list sorted by <c>(Src,SrcPort,Dst,DstPort)</c>, so two structurally-equal graphs
    /// built in different construction order serialize BYTE-IDENTICALLY.
    /// </summary>
    public class TriggerGraphCanonicalTests
    {
        // Build a small graph, adding nodes and edges deliberately OUT of id/tuple order.
        private static TriggerGraph BuildScrambled()
        {
            var g = new TriggerGraph();
            // Nodes out of id order.
            g.Nodes.Add(new ActionNode  { Id = 3, Kind = "display_message", Text = "hi" });
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "T", Priority = 1 });
            g.Nodes.Add(new ActionNode  { Id = 4, Kind = "victory", Faction = 0 });
            g.Nodes.Add(new EventNode   { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new ConditionNode { Id = 2, Kind = "always" });

            // Edges out of tuple order.
            g.ExecEdges.Add(new ExecEdge(3, 0, 4, 0)); // Action0 → Action1
            g.ExecEdges.Add(new ExecEdge(0, 0, 3, 0)); // Trigger → Action0
            g.ExecEdges.Add(new ExecEdge(1, 0, 0, 0)); // Event → Trigger

            g.DataEdges.Add(new DataEdge(2, 0, 0, 1, DataWireType.Boolean)); // Condition → Trigger
            return g;
        }

        [Fact]
        public void CanonicalJson_EmitsNodesByAscendingId()
        {
            string json = BuildScrambled().ToCanonicalJson();
            using JsonDocument doc = JsonDocument.Parse(json);
            var ids = new List<int>();
            foreach (JsonElement n in doc.RootElement.GetProperty("nodes").EnumerateArray())
                ids.Add(n.GetProperty("id").GetInt32());

            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, ids);
        }

        [Fact]
        public void CanonicalJson_EmitsEachEdgeListSortedByTuple()
        {
            string json = BuildScrambled().ToCanonicalJson();
            using JsonDocument doc = JsonDocument.Parse(json);

            AssertEdgesSorted(doc.RootElement.GetProperty("exec_edges"));
            AssertEdgesSorted(doc.RootElement.GetProperty("data_edges"));
        }

        [Fact]
        public void TwoGraphs_EqualUpToConstructionOrder_SerializeByteIdentically()
        {
            string a = BuildScrambled().ToCanonicalJson();

            // Same content, added in a DIFFERENT order.
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "T", Priority = 1 });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new ConditionNode { Id = 2, Kind = "always" });
            g.Nodes.Add(new ActionNode { Id = 3, Kind = "display_message", Text = "hi" });
            g.Nodes.Add(new ActionNode { Id = 4, Kind = "victory", Faction = 0 });
            g.ExecEdges.Add(new ExecEdge(1, 0, 0, 0));
            g.ExecEdges.Add(new ExecEdge(0, 0, 3, 0));
            g.ExecEdges.Add(new ExecEdge(3, 0, 4, 0));
            g.DataEdges.Add(new DataEdge(2, 0, 0, 1, DataWireType.Boolean));
            string b = g.ToCanonicalJson();

            Assert.Equal(a, b);
        }

        [Fact]
        public void CanonicalJson_RoundTrips_StructurallyIdentical()
        {
            TriggerGraph g = BuildScrambled();
            string once = g.ToCanonicalJson();
            string twice = TriggerGraph.FromJson(once).ToCanonicalJson();
            Assert.Equal(once, twice);
        }

        private static void AssertEdgesSorted(JsonElement edges)
        {
            (int, int, int, int) prev = (int.MinValue, int.MinValue, int.MinValue, int.MinValue);
            foreach (JsonElement e in edges.EnumerateArray())
            {
                var cur = (
                    e.GetProperty("src").GetInt32(),
                    e.GetProperty("src_port").GetInt32(),
                    e.GetProperty("dst").GetInt32(),
                    e.GetProperty("dst_port").GetInt32());
                Assert.True(Compare(prev, cur) <= 0, $"edges not sorted: {prev} then {cur}");
                prev = cur;
            }
        }

        private static int Compare((int, int, int, int) a, (int, int, int, int) b)
        {
            int c = a.Item1.CompareTo(b.Item1); if (c != 0) return c;
            c = a.Item2.CompareTo(b.Item2); if (c != 0) return c;
            c = a.Item3.CompareTo(b.Item3); if (c != 0) return c;
            return a.Item4.CompareTo(b.Item4);
        }
    }
}
