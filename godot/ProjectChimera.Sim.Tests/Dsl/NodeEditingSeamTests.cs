#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// DW-179 + DW-195 — the Godot-free seams behind T3 per-node EDITING:
    ///
    /// <para><b>NodePortCatalog</b> (DW-195): every palette kind renders ports — including the dedicated leaf
    /// kinds (order_units / move_camera / cinematic_mode / play_vfx / random_choice / enable_trigger /
    /// disable_trigger / run_trigger / show_objective / complete_objective / fail_objective), which previously
    /// fell through the panel's switch to ZERO ports, making a palette-dragged leaf unwireable. The catalog's
    /// exec layout is pinned to EXACT parity with the <see cref="NodePorts"/> legality table, so a future kind
    /// added to one but not the other fails Tier-1 instead of shipping an unwireable palette entry.</para>
    ///
    /// <para><b>NodeFieldCatalog</b> (DW-179 + DW-195's target-field half): every palette kind's editable fields,
    /// with validating Set accessors. The core safety property: an ACCEPTED field value always survives the
    /// canonical serialize→re-parse round-trip (a value that would brick the stored graph channel — empty
    /// raise_event name, negative target_trigger, out-of-range expr_var slot… — is a field-level reject that
    /// leaves the node untouched).</para>
    /// </summary>
    public class NodeEditingSeamTests
    {
        private const int MaxProbePort = 12; // strictly above every named TriggerGraph port constant

        /// <summary>The dedicated leaf kinds DW-195 names — the exact set that rendered zero ports pre-fix.</summary>
        private static readonly string[] DedicatedLeafKinds =
        {
            "order_units", "move_camera", "cinematic_mode", "play_vfx",
            "enable_trigger", "disable_trigger", "run_trigger",
            "show_objective", "complete_objective", "fail_objective",
        };

        private static (List<GraphPortSpec> Ins, List<GraphPortSpec> Outs) PortsOf(NodeBase n)
        {
            var ins = new List<GraphPortSpec>();
            var outs = new List<GraphPortSpec>();
            NodePortCatalog.PortsOf(n, ins, outs);
            return (ins, outs);
        }

        private static NodeBase MustCreate(string kind, int id = 0)
        {
            NodeBase? n = NodePaletteFactory.Create(kind, id);
            Assert.NotNull(n);
            return n!;
        }

        // ══ DW-195 — NodePortCatalog ═══════════════════════════════════════════════════════════════════════════

        [Fact]
        public void PortCatalog_EveryPaletteKind_RendersAtLeastOnePort()
        {
            foreach (string kind in NodePaletteFactory.PaletteKinds)
            {
                (List<GraphPortSpec> ins, List<GraphPortSpec> outs) = PortsOf(MustCreate(kind));
                Assert.True(ins.Count + outs.Count > 0,
                    $"palette kind '{kind}' renders NO ports — a palette-dragged node of it is unwireable (the DW-195 defect).");
            }
        }

        [Fact]
        public void PortCatalog_DedicatedLeafKinds_RenderExecInAndExecOut()
        {
            foreach (string kind in DedicatedLeafKinds.Concat(new[] { "random_choice" }))
            {
                (List<GraphPortSpec> ins, List<GraphPortSpec> outs) = PortsOf(MustCreate(kind));
                Assert.True(ins.Any(p => !p.IsData && p.Port == TriggerGraph.ActionExecInPort),
                    $"'{kind}' renders no exec-in port.");
                Assert.True(outs.Any(p => !p.IsData && p.Port == TriggerGraph.ActionExecOutPort),
                    $"'{kind}' renders no exec-out (continuation) port.");
            }
        }

        /// <summary>The catalog's EXEC layout is exactly the <see cref="NodePorts"/> legality table, per kind —
        /// including random_choice's weight-derived branch-port range.</summary>
        [Fact]
        public void PortCatalog_ExecPorts_ExactParityWithNodePortsLegality()
        {
            var nodes = NodePaletteFactory.PaletteKinds.Select(k => MustCreate(k)).ToList();
            nodes.Add(new RandomChoiceNode { Id = 99, Weights = new[] { 2, 1, 1 } }); // weighted branch range

            foreach (NodeBase n in nodes)
            {
                (List<GraphPortSpec> ins, List<GraphPortSpec> outs) = PortsOf(n);
                for (int p = 0; p <= MaxProbePort; p++)
                {
                    Assert.Equal(NodePorts.IsExecIn(n, p), ins.Any(x => !x.IsData && x.Port == p));
                    Assert.Equal(NodePorts.IsExecOut(n, p), outs.Any(x => !x.IsData && x.Port == p));
                }
            }
        }

        /// <summary>Every rendered data port is legal, and every legal data port is rendered — with the ONE
        /// sanctioned curation: an ActionNode's index-in renders only for array_set.</summary>
        [Fact]
        public void PortCatalog_DataPorts_SoundAndComplete_ExceptActionIndexCuration()
        {
            foreach (string kind in NodePaletteFactory.PaletteKinds)
            {
                NodeBase n = MustCreate(kind);
                (List<GraphPortSpec> ins, List<GraphPortSpec> outs) = PortsOf(n);

                foreach (GraphPortSpec p in ins.Where(x => x.IsData))
                    Assert.True(NodePorts.IsDataIn(n, p.Port), $"'{kind}' renders illegal data-in port {p.Port}.");
                foreach (GraphPortSpec p in outs.Where(x => x.IsData))
                    Assert.True(NodePorts.IsDataOut(n, p.Port), $"'{kind}' renders illegal data-out port {p.Port}.");

                for (int p = 0; p <= MaxProbePort; p++)
                {
                    bool curatedAway = n is ActionNode a && a.Kind != "array_set" && p == TriggerGraph.ActionIndexInPort;
                    if (NodePorts.IsDataIn(n, p) && !curatedAway)
                        Assert.True(ins.Any(x => x.IsData && x.Port == p),
                            $"'{kind}' does not render its legal data-in port {p}.");
                    if (NodePorts.IsDataOut(n, p))
                        Assert.True(outs.Any(x => x.IsData && x.Port == p),
                            $"'{kind}' does not render its legal data-out port {p}.");
                }
            }
        }

        [Fact]
        public void PortCatalog_RandomChoice_BranchPortsFollowWeights()
        {
            var rc = new RandomChoiceNode { Id = 0, Weights = Array.Empty<int>() };
            (_, List<GraphPortSpec> outs) = PortsOf(rc);
            Assert.Single(outs); // continuation only — no weights, no branch ports (parity with IsExecOut)

            rc.Weights = new[] { 5, 3 };
            (_, outs) = PortsOf(rc);
            Assert.Equal(3, outs.Count(p => !p.IsData));
            Assert.Contains(outs, p => !p.IsData && p.Port == TriggerGraph.RandomChoiceBranchOutPort0);
            Assert.Contains(outs, p => !p.IsData && p.Port == TriggerGraph.RandomChoiceBranchOutPort0 + 1);
        }

        /// <summary>End-to-end: a palette-dragged objective leaf is wireable through the catalog's advertised
        /// ports — the edges validate, the located structural gate stays clean, and the graph round-trips.</summary>
        [Fact]
        public void DedicatedLeaf_WiredThroughCatalogPorts_ValidatesAndRoundTrips()
        {
            var g = new TriggerGraph();
            g.Nodes.Add(MustCreate("trigger", 0));
            g.Nodes.Add(MustCreate("match_start", 1));
            g.Nodes.Add(MustCreate("show_objective", 2));
            g.Nodes.Add(MustCreate("complete_objective", 3));

            (List<GraphPortSpec> _, List<GraphPortSpec> evOuts) = PortsOf(g.Nodes[1]);
            (List<GraphPortSpec> trigIns, List<GraphPortSpec> trigOuts) = PortsOf(g.Nodes[0]);
            (List<GraphPortSpec> leafIns, List<GraphPortSpec> leafOuts) = PortsOf(g.Nodes[2]);
            (List<GraphPortSpec> leaf2Ins, _) = PortsOf(g.Nodes[3]);

            int evFire   = evOuts.Single(p => !p.IsData).Port;
            int trigEvIn = trigIns.Single(p => !p.IsData).Port;
            int trigThen = trigOuts.Single(p => !p.IsData).Port;
            int leafIn   = leafIns.Single(p => !p.IsData).Port;
            int leafOut  = leafOuts.Single(p => !p.IsData).Port;
            int leaf2In  = leaf2Ins.Single(p => !p.IsData).Port;

            var noVars = new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal);
            var noArrays = new Dictionary<string, (DslValueType Elem, int Capacity)>(StringComparer.Ordinal);

            // Each drag validates against the gate BEFORE landing (the panel's flow), then lands.
            Assert.Null(GraphStructureGate.TryValidateNewEdge(g, isData: false, 1, evFire, 0, trigEvIn, default));
            g.ExecEdges.Add(new ExecEdge(1, evFire, 0, trigEvIn));
            Assert.Null(GraphStructureGate.TryValidateNewEdge(g, isData: false, 0, trigThen, 2, leafIn, default));
            g.ExecEdges.Add(new ExecEdge(0, trigThen, 2, leafIn));
            Assert.Null(GraphStructureGate.TryValidateNewEdge(g, isData: false, 2, leafOut, 3, leaf2In, default));
            g.ExecEdges.Add(new ExecEdge(2, leafOut, 3, leaf2In));

            Assert.Empty(GraphStructureGate.CheckGraphLocated(g, noVars, noArrays));

            TriggerGraph reparsed = TriggerGraph.FromJson(g.ToCanonicalJson());
            Assert.Equal(4, reparsed.Nodes.Count);
            Assert.Equal(3, reparsed.ExecEdges.Count);
            Assert.IsType<ShowObjectiveNode>(reparsed.Nodes.Single(n => n.Id == 2));
        }

        // ══ DW-179 — NodeFieldCatalog ══════════════════════════════════════════════════════════════════════════

        [Fact]
        public void FieldCatalog_CoversEveryPaletteKind()
        {
            foreach (string kind in NodePaletteFactory.PaletteKinds)
            {
                IReadOnlyList<NodeFieldDef> fields = NodeFieldCatalog.FieldsOf(MustCreate(kind));
                if (kind == "branch" || kind == "run_effect")
                {
                    Assert.Empty(fields); // no editable payload (run_effect's effect tree is out of inspector scope)
                    continue;
                }
                Assert.True(fields.Count > 0, $"palette kind '{kind}' exposes NO editable fields (the DW-179 defect).");
                Assert.Equal(fields.Count, fields.Select(f => f.Key).Distinct(StringComparer.Ordinal).Count());
                foreach (NodeFieldDef f in fields.Where(x => x.Editor == NodeFieldEditorKind.Choice))
                {
                    Assert.NotNull(f.Choices);
                    Assert.NotEmpty(f.Choices!);
                }
            }
        }

        /// <summary>Get output is always re-Set-able (the inspector's no-op apply on focus-out must never error),
        /// for every field of every palette default.</summary>
        [Fact]
        public void FieldCatalog_GetOutput_IsAlwaysReSettable()
        {
            foreach (string kind in NodePaletteFactory.PaletteKinds)
            {
                NodeBase n = MustCreate(kind);
                foreach (NodeFieldDef f in NodeFieldCatalog.FieldsOf(n))
                    Assert.Null(f.Set(f.Get()));
            }
        }

        /// <summary>THE core DW-179 safety property: an accepted Set lands on the node AND survives the canonical
        /// serialize→re-parse round-trip byte-faithfully (per-field, fresh node per field so cases are isolated).
        /// A field whose accepted value did not persist — or bricked the parse — fails here.</summary>
        [Fact]
        public void FieldCatalog_AcceptedValue_Applies_AndSurvivesCanonicalReparse()
        {
            foreach (string kind in NodePaletteFactory.PaletteKinds)
            {
                string[] keys = NodeFieldCatalog.FieldsOf(MustCreate(kind, 5)).Select(f => f.Key).ToArray();
                foreach (string key in keys)
                {
                    NodeBase node = MustCreate(kind, 5);
                    NodeFieldDef f = NodeFieldCatalog.FieldsOf(node).Single(x => x.Key == key);

                    string sample = SampleFor(f);
                    string? err = f.Set(sample);
                    Assert.True(err == null, $"{kind}.{key}: sample '{sample}' rejected: {err}");
                    string applied = f.Get();

                    var g = new TriggerGraph();
                    g.Nodes.Add(node);
                    TriggerGraph reparsed = TriggerGraph.FromJson(g.ToCanonicalJson()); // must not throw (no brick)

                    NodeFieldDef back = NodeFieldCatalog.FieldsOf(reparsed.Nodes.Single()).Single(x => x.Key == key);
                    Assert.True(applied == back.Get(),
                        $"{kind}.{key}: value '{applied}' did not survive the canonical round-trip (read back '{back.Get()}').");
                }
            }
        }

        private static string SampleFor(NodeFieldDef f) => f.Editor switch
        {
            NodeFieldEditorKind.Int     => "3",
            NodeFieldEditorKind.Bool    => f.Get() == "true" ? "false" : "true",
            NodeFieldEditorKind.Fixed   => "2.5",
            NodeFieldEditorKind.IntList => "1, 2",
            NodeFieldEditorKind.Choice  => PickOtherChoice(f),
            _                           => TextSampleFor(f.Key),
        };

        private static string PickOtherChoice(NodeFieldDef f)
        {
            IReadOnlyList<string> c = f.Choices!;
            string last = c[c.Count - 1];
            return last != f.Get() ? last : c[0];
        }

        private static string TextSampleFor(string key) => key switch
        {
            "objective_id" => "obj_main",
            _              => "sample_name",
        };

        /// <summary>Values the canonical serialize→re-parse round-trip would REJECT must be field-level rejects
        /// that leave the node untouched — otherwise an inspector edit bricks the stored graph channel.</summary>
        [Theory]
        [InlineData("raise_event", "name", "")]
        [InlineData("raise_event", "name", "   ")]
        [InlineData("raise_event", "raiser", "8")]     // outside -1..PlayerSlots-1 (parse reject)
        [InlineData("raise_event", "raiser", "-2")]
        [InlineData("expr_var", "faction", "8")]       // outside -1..PlayerSlots-1 (parse reject)
        [InlineData("expr_event_param", "name", "")]
        [InlineData("custom_event", "event_name", " ")]
        [InlineData("show_objective", "objective_id", "  ")]
        [InlineData("enable_trigger", "target_trigger", "-1")] // parse rejects a negative target
        [InlineData("enable_trigger", "target_trigger", "abc")]
        [InlineData("match_start", "operator", "~=")]  // not in the closed Operators vocabulary
        [InlineData("expr_unary", "op", "abs")]        // not a unary op
        [InlineData("order_units", "command", "patrol")]
        [InlineData("random_choice", "weights", "1, -2")]
        [InlineData("random_choice", "weights", "x")]
        [InlineData("for_each", "up_to", "100")]       // above DslBounds.MaxForEachItems
        [InlineData("for_each", "up_to", "-1")]
        [InlineData("spawn_unit", "count", "99999999999999999999")] // not a 32-bit int
        [InlineData("spawn_unit", "x", "abc")]
        [InlineData("spawn_unit", "x", "40000")]       // out of the 16.16 range
        [InlineData("trigger", "cooldown_seconds", "32767.9999")] // above the content float-save ceiling
        public void FieldCatalog_RejectsBrickingValues_AndLeavesTheNodeUntouched(string kind, string key, string bad)
        {
            NodeBase node = MustCreate(kind, 7);
            NodeFieldDef f = NodeFieldCatalog.FieldsOf(node).Single(x => x.Key == key);
            string before = f.Get();

            Assert.NotNull(f.Set(bad));
            Assert.Equal(before, f.Get()); // untouched on reject

            // And the untouched node still round-trips (sanity: the reject really did protect the channel).
            var g = new TriggerGraph();
            g.Nodes.Add(node);
            TriggerGraph.FromJson(g.ToCanonicalJson());
        }

        [Fact]
        public void FieldCatalog_ExprLiteral_FixedValue_RejectsExponentAndOverlongFractions()
        {
            var l = new ExprLiteralNode { Id = 0, ValueType = DslValueType.Fixed, Raw = 0 };
            NodeFieldDef value = NodeFieldCatalog.FieldsOf(l).Single(f => f.Key == "value");
            Assert.NotNull(value.Set("1e3"));
            Assert.NotNull(value.Set("40000"));
            Assert.NotNull(value.Set("1.12345678901234567")); // 17 fraction digits (exact 16.16 needs ≤16)
            Assert.Equal(0, l.Raw);

            Assert.Null(value.Set("2.5"));
            Assert.Equal(163840, l.Raw); // 2.5 * 65536
            Assert.Equal("2.5", value.Get());
        }

        [Fact]
        public void FieldCatalog_ExprLiteral_TypeSwitch_PreservesTheValue()
        {
            // Int → Fixed re-encodes the integer.
            var l = new ExprLiteralNode { Id = 0, ValueType = DslValueType.Int, Raw = 5 };
            NodeFieldDef type = NodeFieldCatalog.FieldsOf(l).Single(f => f.Key == "type");
            Assert.Null(type.Set("Fixed"));
            Assert.Equal(DslValueType.Fixed, l.ValueType);
            Assert.Equal(5 << 16, l.Raw);
            Assert.Equal("5", NodeFieldCatalog.FieldsOf(l).Single(f => f.Key == "value").Get());

            // Fixed → Int rounds half away from zero.
            Assert.Null(NodeFieldCatalog.FieldsOf(l).Single(f => f.Key == "value").Set("2.5"));
            Assert.Null(NodeFieldCatalog.FieldsOf(l).Single(f => f.Key == "type").Set("Int"));
            Assert.Equal(DslValueType.Int, l.ValueType);
            Assert.Equal(3, l.Raw);

            // nonzero → Bool is true; Bool → Fixed is 1.
            Assert.Null(NodeFieldCatalog.FieldsOf(l).Single(f => f.Key == "type").Set("Bool"));
            Assert.Equal(1, l.Raw);
            Assert.Equal("true", NodeFieldCatalog.FieldsOf(l).Single(f => f.Key == "value").Get());
            Assert.Null(NodeFieldCatalog.FieldsOf(l).Single(f => f.Key == "type").Set("Fixed"));
            Assert.Equal(1 << 16, l.Raw);

            // An Int outside the 16.16 integer range cannot silently re-encode — reject, unchanged.
            var big = new ExprLiteralNode { Id = 1, ValueType = DslValueType.Int, Raw = 40000 };
            Assert.NotNull(NodeFieldCatalog.FieldsOf(big).Single(f => f.Key == "type").Set("Fixed"));
            Assert.Equal(DslValueType.Int, big.ValueType);
            Assert.Equal(40000, big.Raw);
        }

        /// <summary>expr_call's selector field follows the fn: a closed choice list for the tag/category/resource
        /// reads (each member pinned against the compiler's resolver — the drift guard), free text for the
        /// runtime-resolved region read, ABSENT for the selector-less builtins; switching fn clears a stale
        /// selector so the node never strands a value the compiler would reject.</summary>
        [Fact]
        public void FieldCatalog_ExprCall_SelectorFollowsFn_AndMatchesResolvers()
        {
            var ec = new ExprCallNode { Id = 0, Fn = "count" };
            Assert.DoesNotContain(NodeFieldCatalog.FieldsOf(ec), f => f.Key == "selector");

            Assert.Null(NodeFieldCatalog.FieldsOf(ec).Single(f => f.Key == "fn").Set("unit_count_tag"));
            NodeFieldDef sel = NodeFieldCatalog.FieldsOf(ec).Single(f => f.Key == "selector");
            Assert.Equal(NodeFieldEditorKind.Choice, sel.Editor);
            foreach (string choice in sel.Choices!)
                Assert.True(NodeKinds.TryResolveTagSelector(choice, out _), $"tag selector '{choice}' does not resolve.");
            Assert.Null(sel.Set("magical"));
            Assert.Equal("magical", ec.Selector);

            // Round-trips with the selector intact.
            var g = new TriggerGraph();
            g.Nodes.Add(ec);
            var back = (ExprCallNode)TriggerGraph.FromJson(g.ToCanonicalJson()).Nodes.Single();
            Assert.Equal("unit_count_tag", back.Fn);
            Assert.Equal("magical", back.Selector);

            // The category/resource lists are pinned to their resolvers too.
            ec.Fn = "unit_count_category";
            foreach (string choice in NodeFieldCatalog.FieldsOf(ec).Single(f => f.Key == "selector").Choices!)
                Assert.True(NodeKinds.TryResolveCategorySelector(choice, out _), $"category selector '{choice}' does not resolve.");
            ec.Fn = "player_resource";
            foreach (string choice in NodeFieldCatalog.FieldsOf(ec).Single(f => f.Key == "selector").Choices!)
                Assert.True(NodeKinds.TryResolveResourceSelector(choice, out _), $"resource selector '{choice}' does not resolve.");

            // region_unit_count: free text (runtime-resolved).
            ec.Fn = "region_unit_count";
            NodeFieldDef region = NodeFieldCatalog.FieldsOf(ec).Single(f => f.Key == "selector");
            Assert.Equal(NodeFieldEditorKind.Text, region.Editor);
            Assert.Null(region.Set("north"));
            Assert.Equal("north", ec.Selector);

            // Switching to a selector-less fn clears the stale selector.
            Assert.Null(NodeFieldCatalog.FieldsOf(ec).Single(f => f.Key == "fn").Set("count"));
            Assert.Equal("", ec.Selector);
        }

        /// <summary>DW-179 meets DW-195: editing random_choice weights through the inspector GROWS the node's
        /// rendered branch ports (weight-derived exec-outs), still in exact NodePorts parity.</summary>
        [Fact]
        public void FieldCatalog_WeightsEdit_GrowsRandomChoiceBranchPorts()
        {
            var rc = (RandomChoiceNode)MustCreate("random_choice");
            (_, List<GraphPortSpec> outs) = PortsOf(rc);
            Assert.Single(outs); // factory default: empty weights → continuation only

            NodeFieldDef weights = NodeFieldCatalog.FieldsOf(rc).Single(f => f.Key == "weights");
            Assert.Null(weights.Set("2, 3, 4"));
            Assert.Equal(new[] { 2, 3, 4 }, rc.Weights);

            (_, outs) = PortsOf(rc);
            Assert.Equal(4, outs.Count); // continuation + one branch per weight
            foreach (GraphPortSpec p in outs)
                Assert.True(NodePorts.IsExecOut(rc, p.Port));

            // And the weights persist through the canonical round-trip.
            var g = new TriggerGraph();
            g.Nodes.Add(rc);
            var back = (RandomChoiceNode)TriggerGraph.FromJson(g.ToCanonicalJson()).Nodes.Single();
            Assert.Equal(new[] { 2, 3, 4 }, back.Weights);
        }

        [Fact]
        public void FieldCatalog_OptionalString_EmptyClears_AndOmitsFromJson()
        {
            NodeBase ev = MustCreate("building_completed");
            NodeFieldDef bt = NodeFieldCatalog.FieldsOf(ev).Single(f => f.Key == "building_type");

            Assert.Null(bt.Set("barracks"));
            var g = new TriggerGraph();
            g.Nodes.Add(ev);
            Assert.Contains("building_type", g.ToCanonicalJson(), StringComparison.Ordinal);

            Assert.Null(bt.Set("")); // empty clears → the canonical missing==null mirror
            Assert.DoesNotContain("building_type", g.ToCanonicalJson(), StringComparison.Ordinal);
            NodeBase back = TriggerGraph.FromJson(g.ToCanonicalJson()).Nodes.Single();
            Assert.Equal("", NodeFieldCatalog.FieldsOf(back).Single(f => f.Key == "building_type").Get());
        }

        /// <summary>A content Fixed edit is quantized once at Set (exact integer math) and then holds byte-stable
        /// through the float-writing save boundary — no drift between what the inspector shows and what reloads.</summary>
        [Fact]
        public void FieldCatalog_ContentFixed_HoldsStableThroughTheFloatSaveBoundary()
        {
            NodeBase a = MustCreate("spawn_unit");
            NodeFieldDef x = NodeFieldCatalog.FieldsOf(a).Single(f => f.Key == "x");
            Assert.Null(x.Set("0.1"));
            string shown = x.Get();
            Assert.Equal("0.100006103515625", shown); // raw 6554 — the exact 16.16 quantization of 0.1

            var g = new TriggerGraph();
            g.Nodes.Add(a);
            NodeBase back = TriggerGraph.FromJson(g.ToCanonicalJson()).Nodes.Single();
            Assert.Equal(shown, NodeFieldCatalog.FieldsOf(back).Single(f => f.Key == "x").Get());
        }
    }
}
