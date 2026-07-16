#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Combat;            // DamageType
using ProjectChimera.Core;              // Fixed
using ProjectChimera.Dsl;
using ProjectChimera.Effects;           // DamageEffect
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// Story 7.4 (pass-2 review) — the "extended in lockstep" enforcement net over the closed kind registry.
    /// <see cref="NodeKinds"/> documents pairwise disjointness of the kind sets and the AC requires NodeKinds +
    /// the converter allow-lists to move together, yet no test referenced NodeKinds at all — a kind added to one
    /// side but not the other regressed silently. The test assembly compiles the sim sources directly
    /// (SimSources.props), so the internal registry is visible here without InternalsVisibleTo.
    /// </summary>
    public class NodeKindsLockstepTests
    {
        private static IEnumerable<(string SetName, string Kind)> AllKinds()
        {
            yield return ("structural", NodeKinds.Trigger);
            yield return ("structural", NodeKinds.RunEffect);
            // ── Story 7.5 — the three custom-event kinds (custom_event is the graph-only EVENT kind: the graph
            //    event vocabulary is EventTypes ∪ {custom_event}, the sanctioned graph⊃flat divergence). ──
            yield return ("structural", NodeKinds.RaiseEvent);
            yield return ("event", NodeKinds.CustomEvent);
            yield return ("expr", NodeKinds.ExprEventParam);
            yield return ("expr", NodeKinds.ExprLiteral);
            yield return ("expr", NodeKinds.ExprVar);
            yield return ("expr", NodeKinds.ExprUnary);
            yield return ("expr", NodeKinds.ExprBinary);
            yield return ("expr", NodeKinds.ExprCall);
            foreach (string k in NodeKinds.EventTypes)     yield return ("event", k);
            foreach (string k in NodeKinds.ConditionTypes) yield return ("condition", k);
            foreach (string k in NodeKinds.ActionTypes)    yield return ("action", k);
        }

        [Fact]
        public void GraphEventTypes_IsExactlyEventTypesPlusCustomEvent()
        {
            // The Story 7.5 sanctioned graph⊃flat divergence, pinned: the graph event set is the frozen flat set
            // plus custom_event and NOTHING else — a drift on either side goes red here.
            Assert.Equal(
                NodeKinds.EventTypes.Concat(new[] { NodeKinds.CustomEvent }).OrderBy(k => k, System.StringComparer.Ordinal),
                NodeKinds.GraphEventTypes.OrderBy(k => k, System.StringComparer.Ordinal));
        }

        [Fact]
        public void EveryKindString_IsGloballyUnique_AcrossAllRegistrySets()
        {
            // Pairwise disjointness (the documented invariant): a kind string maps to exactly ONE node type.
            List<(string SetName, string Kind)> all = AllKinds().ToList();
            var seen = new Dictionary<string, string>(System.StringComparer.Ordinal);
            foreach ((string setName, string kind) in all)
            {
                Assert.False(seen.TryGetValue(kind, out string? firstSet),
                    $"kind '{kind}' appears in both the {firstSet} and {setName} sets — the registry must stay pairwise disjoint");
                seen[kind] = setName;
            }
        }

        [Fact]
        public void ExprOpAndFnVocabularies_AreDisjointFromEachOther()
        {
            string[] unary  = NodeKinds.ExprUnaryOps;
            string[] binary = NodeKinds.ExprBinaryOps;
            string[] fns    = NodeKinds.ExprCallFns;
            Assert.Empty(unary.Intersect(binary, System.StringComparer.Ordinal));
            Assert.Empty(unary.Intersect(fns,    System.StringComparer.Ordinal));
            Assert.Empty(binary.Intersect(fns,   System.StringComparer.Ordinal));
        }

        /// <summary>A minimal serializable node instance for each registered kind.</summary>
        private static NodeBase MinimalNode(string kind, int id)
        {
            if (kind == NodeKinds.Trigger)     return new TriggerNode { Id = id };
            if (kind == NodeKinds.RunEffect)   return new EffectActionNode { Id = id, Effect = new DamageEffect(Fixed.FromInt(1), DamageType.Normal) };
            if (kind == NodeKinds.ExprLiteral) return new ExprLiteralNode { Id = id, ValueType = DslValueType.Int, Raw = 1 };
            if (kind == NodeKinds.ExprVar)     return new ExprVarNode { Id = id, Name = "v" };
            if (kind == NodeKinds.ExprUnary)   return new ExprUnaryNode { Id = id, Op = "not" };
            if (kind == NodeKinds.ExprBinary)  return new ExprBinaryNode { Id = id, Op = "add" };
            if (kind == NodeKinds.ExprCall)    return new ExprCallNode { Id = id, Fn = "count" };
            if (kind == NodeKinds.RaiseEvent)     return new RaiseEventNode { Id = id, Name = "ev" };
            if (kind == NodeKinds.CustomEvent)    return new EventNode { Id = id, Kind = NodeKinds.CustomEvent, EventName = "ev" };
            if (kind == NodeKinds.ExprEventParam) return new ExprEventParamNode { Id = id, Name = "p" };
            if (NodeKinds.InSet(NodeKinds.EventTypes, kind))     return new EventNode { Id = id, Kind = kind };
            if (NodeKinds.InSet(NodeKinds.ConditionTypes, kind)) return new ConditionNode { Id = id, Kind = kind };
            /* action */                                          return new ActionNode { Id = id, Kind = kind };
        }

        [Fact]
        public void EveryRegisteredKind_RoundTripsThroughTheConverter_ToItsOwnKindString()
        {
            // The lockstep net: every kind NodeKinds registers must survive Write→Read through the converter and
            // come back as the same runtime type carrying the same kind string. A kind added to NodeKinds without
            // its converter branch (or vice versa) turns this RED instead of failing at some later authored graph.
            var graph = new TriggerGraph();
            int id = 0;
            foreach ((_, string kind) in AllKinds())
                graph.Nodes.Add(MinimalNode(kind, id++));

            string json = graph.ToCanonicalJson();
            TriggerGraph back = TriggerGraph.FromJson(json);
            Assert.Equal(graph.Nodes.Count, back.Nodes.Count);

            List<(string, string)> kinds = AllKinds().ToList();
            for (int i = 0; i < kinds.Count; i++)
            {
                NodeBase original = graph.Nodes[i];
                NodeBase roundtripped = back.Nodes.Single(n => n.Id == original.Id);
                Assert.Equal(original.GetType(), roundtripped.GetType());
            }

            // And the canonical form is stable across the round-trip (the byte-identity core).
            Assert.Equal(json, back.ToCanonicalJson());
        }
    }
}
