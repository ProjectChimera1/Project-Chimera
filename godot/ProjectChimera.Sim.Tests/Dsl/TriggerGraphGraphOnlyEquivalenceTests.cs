#nullable enable
using System.Linq;
using ProjectChimera.Core.Definitions;   // TriggerDefinition (the flat POCO ToFlat lowers to)
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// Story 7.13 — the missing tier-drift closer the <see cref="NodeKindsLockstepTests"/> lockstep net does NOT
    /// cover (DW ~1728/1876): a node kind is GRAPH-ONLY iff it has no flat <c>TriggerDefinition</c> form, so a
    /// graph-only addition can never silently read as flat-editable in the T2 sentence editor.
    ///
    /// The equivalence is proven from BEHAVIOR, not restated literals: for every registered ACTION kind we build a
    /// minimal trigger→action graph and run the real <see cref="TriggerGraph.ToFlat"/>, then compare whether the
    /// action survives the flat lowering against <see cref="TriggerGraph.IsGraphOnlyKind"/>. If a future action kind
    /// (e.g. a Story 7.13 <c>order_units</c>/<c>move_camera</c>) is added to <c>IsGraphOnlyKind</c> but its
    /// <c>ToFlat</c> skip is forgotten (or vice-versa), this goes RED instead of silently mis-rendering a
    /// graph-only construct as an editable T2 sentence.
    ///
    /// Scope note: the equivalence is asserted over the ACTION vocabulary (<see cref="NodeKinds.ActionTypes"/>) —
    /// the exact universe the T2 flat sentence editor renders and the exact drift class the story's checklist
    /// flags. <c>run_effect</c> (an embed seam) and the expr/condition leaves are intentionally out of this scope:
    /// they are not action-chain kinds and <c>IsGraphOnlyKind</c> deliberately excludes <c>run_effect</c> even
    /// though <c>ToFlat</c> drops it. The three Story 7.5 graph-only EVENT/expr kinds
    /// (<c>raise_event</c>/<c>custom_event</c>/<c>expr_event_param</c>) are covered separately below via their
    /// documented <c>ToFlat</c> fail-closed throw.
    /// </summary>
    public class TriggerGraphGraphOnlyEquivalenceTests
    {
        /// <summary>Build a minimal single-trigger graph whose one action node carries <paramref name="kind"/>,
        /// wired event→trigger→action exactly as <see cref="TriggerGraph.FromFlat"/> would.</summary>
        private static TriggerGraph MinimalActionGraph(string kind)
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "T" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            // Variable is required only by the array actions; harmless on the others (ToFlat copies fields verbatim).
            g.Nodes.Add(new ActionNode { Id = 2, Kind = kind, Variable = "arr" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            return g;
        }

        [Theory]
        [MemberData(nameof(ActionKindCases))]
        public void ActionKind_IsGraphOnly_IffItHasNoFlatForm(string kind)
        {
            TriggerDefinition[] flat = MinimalActionGraph(kind).ToFlat();

            // "Has a flat form" == the action survived the lowering as a flat TriggerAction of the same type.
            bool hasFlatForm = flat.Length == 1 && flat[0].Actions.Any(a => a.Type == kind);

            // Graph-only ⇔ NO flat form. Derived from real ToFlat behavior, not a restated allow-list.
            Assert.Equal(TriggerGraph.IsGraphOnlyKind(kind), !hasFlatForm);
        }

        public static System.Collections.Generic.IEnumerable<object[]> ActionKindCases() =>
            NodeKinds.ActionTypes.Select(k => new object[] { k });

        [Fact]
        public void FlatActionKinds_AllSurviveToFlat()
        {
            // Cross-check the derived-set contract from the other direction: exactly the FlatActionTypes lower to a
            // flat TriggerAction, and none of them are classified graph-only.
            foreach (string k in NodeKinds.FlatActionTypes)
            {
                Assert.False(TriggerGraph.IsGraphOnlyKind(k), $"flat action kind '{k}' must not be graph-only");
                TriggerDefinition[] flat = MinimalActionGraph(k).ToFlat();
                Assert.True(flat.Length == 1 && flat[0].Actions.Any(a => a.Type == k),
                    $"flat action kind '{k}' should survive ToFlat as a flat TriggerAction");
            }
        }

        [Fact]
        public void GraphChannelOnlyKinds_FailClosedInToFlat()
        {
            // The Story 7.5 graph-only kinds have no flat form and ToFlat fails CLOSED (located throw) rather than
            // lowering lossily — and each is classified graph-only. (A container/array action is dropped silently;
            // these three throw. Both are "no flat form", the property IsGraphOnlyKind encodes.)

            // raise_event
            Assert.True(TriggerGraph.IsGraphOnlyKind(NodeKinds.RaiseEvent));
            {
                var g = new TriggerGraph();
                g.Nodes.Add(new TriggerNode { Id = 0 });
                g.Nodes.Add(new RaiseEventNode { Id = 1, Name = "ev" });
                Assert.ThrowsAny<System.Text.Json.JsonException>(() => g.ToFlat());
            }

            // custom_event subscription
            Assert.True(TriggerGraph.IsGraphOnlyKind(NodeKinds.CustomEvent));
            {
                var g = new TriggerGraph();
                g.Nodes.Add(new TriggerNode { Id = 0 });
                g.Nodes.Add(new EventNode { Id = 1, Kind = NodeKinds.CustomEvent, EventName = "ev" });
                Assert.ThrowsAny<System.Text.Json.JsonException>(() => g.ToFlat());
            }

            // expr_event_param
            Assert.True(TriggerGraph.IsGraphOnlyKind(NodeKinds.ExprEventParam));
            {
                var g = new TriggerGraph();
                g.Nodes.Add(new TriggerNode { Id = 0 });
                g.Nodes.Add(new ExprEventParamNode { Id = 1, Name = "p" });
                Assert.ThrowsAny<System.Text.Json.JsonException>(() => g.ToFlat());
            }
        }

        [Fact]
        public void Story713ActionLeafKinds_FailClosedInToFlat_AndAreGraphOnly()
        {
            // Story 7.13 — the four action-leaf kinds are graph-channel-only (no flat TriggerDefinition form) and
            // ToFlat fails CLOSED (located throw) on each, rather than lowering to a T2-editable sentence.
            NodeBase[] leaves =
            {
                new OrderUnitsNode { Id = 1, Command = "move" },
                new MoveCameraNode { Id = 1, CameraName = "cam" },
                new CinematicModeNode { Id = 1 },
                new PlayVfxNode { Id = 1, VfxId = "vfx" },
            };
            foreach (NodeBase leaf in leaves)
            {
                Assert.True(TriggerGraph.IsGraphOnlyKind(NodeKinds.KindOf(leaf)),
                    $"{NodeKinds.KindOf(leaf)} must be graph-only");
                var g = new TriggerGraph();
                g.Nodes.Add(new TriggerNode { Id = 0 });
                g.Nodes.Add(leaf);
                Assert.ThrowsAny<System.Text.Json.JsonException>(() => g.ToFlat());
            }
        }
    }
}
