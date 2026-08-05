#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Combat;            // DamageType
using ProjectChimera.Core;              // Fixed
using ProjectChimera.Dsl;
using ProjectChimera.Effects;           // DamageEffect
using ProjectChimera.Sim.Tests.Golden;  // DW-501 — ReflectionProbe (null-CHECKED white-box lookups)
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
            // Story 7.6 — the three exec containers + the two array expression kinds.
            yield return ("structural", NodeKinds.ForEach);
            yield return ("structural", NodeKinds.ForEachBatched);
            yield return ("structural", NodeKinds.Branch);
            yield return ("expr", NodeKinds.ExprArrayGet);
            yield return ("expr", NodeKinds.ExprArrayLen);
            // Story 7.13 — the four graph-channel-only action-leaf kinds (dedicated node classes, like raise_event).
            yield return ("structural", NodeKinds.OrderUnits);
            yield return ("structural", NodeKinds.MoveCamera);
            yield return ("structural", NodeKinds.CinematicMode);
            yield return ("structural", NodeKinds.PlayVfx);
            // Story 7.13 — the weighted container + the three trigger-control leaves.
            yield return ("structural", NodeKinds.RandomChoice);
            yield return ("structural", NodeKinds.EnableTrigger);
            yield return ("structural", NodeKinds.DisableTrigger);
            yield return ("structural", NodeKinds.RunTrigger);
            // Story 7.14 — the three graph-channel-only objective action-leaf kinds.
            yield return ("structural", NodeKinds.ShowObjective);
            yield return ("structural", NodeKinds.CompleteObjective);
            yield return ("structural", NodeKinds.FailObjective);
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
        public void ScenarioValidator_ConsumesNodeKinds_ByReference() // Story 7.7 — vocabulary unification teeth
        {
            // The validator's private vocabulary fields must ALIAS the NodeKinds arrays (same object, not a copy):
            // a re-introduced hand-kept string list would pass a value-equality check today and silently drift on
            // the next vocabulary extension — reference identity cannot.
            //
            // DW-501: the four lookups below go through ReflectionProbe (null-CHECKED), never the old
            // `t.GetField("_x", F)!.GetValue(null)` idiom. That `!` did nothing at runtime, so RENAMING a validator
            // vocabulary field turned this test into an opaque NullReferenceException on line 94 — no owner type,
            // no member name, no hint that the test (not the validator) had gone stale.
            Assert.Same(NodeKinds.EventTypes,      Vocabulary("_triggerEventTypes"));
            Assert.Same(NodeKinds.ConditionTypes,  Vocabulary("_conditionTypes"));
            Assert.Same(NodeKinds.FlatActionTypes, Vocabulary("_actionTypes"));
            // Story 7.7 review follow-up: the comparison-operator vocabulary is unified too — the flat gate's
            // _operators aliases NodeKinds.Operators (the same set NodeBaseJsonConverter enforces at graph parse).
            Assert.Same(NodeKinds.Operators,       Vocabulary("_operators"));
        }

        /// <summary>
        /// DW-501 — one validator vocabulary table, read through the null-checked probe. A renamed or retyped field
        /// fails here with a diagnostic naming <c>ScenarioValidator</c> and the member, instead of an NRE at the
        /// <see cref="Assert.Same(object, object)"/> call site.
        /// </summary>
        private static string[] Vocabulary(string fieldName)
        {
            System.Type validator = typeof(ProjectChimera.Core.Definitions.ScenarioValidator);
            return ReflectionProbe.ReadStatic<string[]>(ReflectionProbe.StaticField(validator, fieldName));
        }

        [Fact]
        public void ProbingARenamedVocabularyField_FailsWithAnActionableDiagnostic_NotAnOpaqueNre() // DW-501
        {
            // The regression teeth for the migration above: the value of ScenarioValidator_ConsumesNodeKinds_ByReference
            // rests entirely on a rename producing an ACTIONABLE failure. Restore the `GetField(...)!` idiom and the
            // failure mode silently reverts to a NullReferenceException that names nothing.
            System.Type validator = typeof(ProjectChimera.Core.Definitions.ScenarioValidator);

            var renamed = Assert.Throws<System.InvalidOperationException>(
                () => ReflectionProbe.StaticField(validator, "_triggerEventTypes_RENAMED"));
            Assert.Contains("ScenarioValidator", renamed.Message);          // names the OWNER...
            Assert.Contains("_triggerEventTypes_RENAMED", renamed.Message); // ...and the MEMBER

            // A static probe pointed at an INSTANCE field is the other stale-probe shape (the vocabulary moving off
            // the type's shared state). GetValue(null) would throw a bare TargetException naming neither.
            System.Reflection.FieldInfo instanceField =
                ReflectionProbe.Field(typeof(ProjectChimera.Core.ScenarioDirector), "_execs");
            var mistyped = Assert.Throws<System.InvalidOperationException>(
                () => ReflectionProbe.ReadStatic<string[]>(instanceField));
            Assert.Contains("_execs", mistyped.Message);

            // Not vacuous: the real members still resolve, so the negative half above is not passing because the
            // probe rejects everything.
            Assert.NotNull(Vocabulary("_triggerEventTypes"));
            Assert.NotNull(Vocabulary("_operators"));
        }

        [Fact]
        public void FlatActionTypes_AreExactlyTheGraphSetMinusArrayKinds() // the derived-set contract
        {
            foreach (string k in NodeKinds.FlatActionTypes)
            {
                Assert.Contains(k, NodeKinds.ActionTypes);
                Assert.False(NodeKinds.IsArrayActionKind(k), $"graph-channel-only kind '{k}' leaked into the flat set");
            }
            foreach (string k in NodeKinds.ActionTypes)
                if (!NodeKinds.IsArrayActionKind(k))
                    Assert.Contains(k, NodeKinds.FlatActionTypes);
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
            if (kind == NodeKinds.ForEach)        return new ForEachNode { Id = id, Source = "faction_units", UpTo = 4 };
            if (kind == NodeKinds.ForEachBatched) return new ForEachBatchedNode { Id = id, Source = "faction_units", BatchSize = 4 };
            if (kind == NodeKinds.Branch)         return new BranchNode { Id = id };
            if (kind == NodeKinds.ExprArrayGet)   return new ExprArrayGetNode { Id = id, Name = "arr" };
            if (kind == NodeKinds.ExprArrayLen)   return new ExprArrayLenNode { Id = id, Name = "arr" };
            if (kind == NodeKinds.RaiseEvent)     return new RaiseEventNode { Id = id, Name = "ev" };
            if (kind == NodeKinds.CustomEvent)    return new EventNode { Id = id, Kind = NodeKinds.CustomEvent, EventName = "ev" };
            if (kind == NodeKinds.ExprEventParam) return new ExprEventParamNode { Id = id, Name = "p" };
            if (kind == NodeKinds.OrderUnits)     return new OrderUnitsNode { Id = id, Command = "move", Faction = -1 };
            if (kind == NodeKinds.MoveCamera)     return new MoveCameraNode { Id = id, CameraName = "cam" };
            if (kind == NodeKinds.CinematicMode)  return new CinematicModeNode { Id = id, Enabled = true };
            if (kind == NodeKinds.PlayVfx)        return new PlayVfxNode { Id = id, VfxId = "vfx" };
            if (kind == NodeKinds.RandomChoice)   return new RandomChoiceNode { Id = id, Weights = new[] { 1, 1 } };
            if (kind == NodeKinds.EnableTrigger)  return new EnableTriggerNode { Id = id, TargetTriggerId = 0 };
            if (kind == NodeKinds.DisableTrigger) return new DisableTriggerNode { Id = id, TargetTriggerId = 0 };
            if (kind == NodeKinds.RunTrigger)     return new RunTriggerNode { Id = id, TargetTriggerId = 0 };
            if (kind == NodeKinds.ShowObjective)     return new ShowObjectiveNode { Id = id, ObjectiveId = "obj" };
            if (kind == NodeKinds.CompleteObjective) return new CompleteObjectiveNode { Id = id, ObjectiveId = "obj" };
            if (kind == NodeKinds.FailObjective)     return new FailObjectiveNode { Id = id, ObjectiveId = "obj" };
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
