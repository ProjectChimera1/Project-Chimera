#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;      // Fixed
using ProjectChimera.Effects;   // DirectHpDeltaEffect (the minimal default run_effect payload)

namespace ProjectChimera.Dsl
{
    /// <summary>
    /// Story 7.10 (follow-up review) — the Godot-free T3 PALETTE seam: the full palette-kind list and the
    /// default-construction of a new node per kind. The palette is EXACTLY the existing closed
    /// <see cref="NodeKinds"/> union (the spec's Never-clause contract) — every kind the registry admits is
    /// addable, none beyond it, no reflection, no new kind. Defaults are chosen so a freshly-added node
    /// SERIALIZES (<c>ToCanonicalJson</c>) and RE-PARSES (<c>FromJson</c>) cleanly: closed-vocabulary fields
    /// (op / fn / source) get a valid member, free-form names get an empty placeholder the load-time validator
    /// rejects located (a badge, not an unparseable graph). That round-trip guarantee is what keeps a T3 save of
    /// work-in-progress nodes from ever bricking the stored graph channel, and it is Tier-1 unit-tested per kind.
    /// </summary>
    public static class NodePaletteFactory
    {
        /// <summary>The palette kinds, in stable display order — exactly the closed <see cref="NodeKinds"/> union:
        /// trigger, the graph event vocabulary, the condition and action vocabularies, the exec containers, the
        /// 7.5 custom-event kinds, run_effect, and the expression kinds.</summary>
        public static IReadOnlyList<string> PaletteKinds { get; } = BuildKinds();

        private static string[] BuildKinds()
        {
            var kinds = new List<string> { NodeKinds.Trigger };
            kinds.AddRange(NodeKinds.GraphEventTypes);
            kinds.AddRange(NodeKinds.ConditionTypes);
            kinds.AddRange(NodeKinds.ActionTypes);
            kinds.Add(NodeKinds.Branch);
            kinds.Add(NodeKinds.ForEach);
            kinds.Add(NodeKinds.ForEachBatched);
            kinds.Add(NodeKinds.RaiseEvent);
            kinds.Add(NodeKinds.RunEffect);
            kinds.Add(NodeKinds.ExprLiteral);
            kinds.Add(NodeKinds.ExprVar);
            kinds.Add(NodeKinds.ExprUnary);
            kinds.Add(NodeKinds.ExprBinary);
            kinds.Add(NodeKinds.ExprCall);
            kinds.Add(NodeKinds.ExprArrayGet);
            kinds.Add(NodeKinds.ExprArrayLen);
            kinds.Add(NodeKinds.ExprEventParam);
            return kinds.ToArray();
        }

        /// <summary>
        /// Construct a default node of <paramref name="kind"/> with persistent id <paramref name="id"/>, or null
        /// for a kind outside the closed registry. Explicit per-kind construction (no reflection). Free-form
        /// fields default to placeholders the validator gate rejects LOCATED (work-in-progress is a badge);
        /// closed-vocabulary fields default to a valid member so the node always survives the
        /// serialize→re-parse round-trip.
        /// </summary>
        public static NodeBase? Create(string kind, int id)
        {
            if (kind == NodeKinds.Trigger) return new TriggerNode { Id = id, Name = "New Trigger" };

            if (NodeKinds.InSet(NodeKinds.GraphEventTypes, kind))
                return new EventNode
                {
                    Id = id,
                    Kind = kind,
                    // custom_event REQUIRES a NON-EMPTY event_name at both serialize and parse (fail-closed), so
                    // the placeholder must be a real name; the validator gate still rejects it located until it
                    // matches a declared custom_events registry entry.
                    EventName = kind == NodeKinds.CustomEvent ? "new_event" : null,
                };

            if (NodeKinds.InSet(NodeKinds.ConditionTypes, kind))
                return new ConditionNode { Id = id, Kind = kind };

            if (NodeKinds.InSet(NodeKinds.ActionTypes, kind))
                return new ActionNode { Id = id, Kind = kind, Text = kind == "display_message" ? "" : null };

            if (kind == NodeKinds.Branch) return new BranchNode { Id = id };
            if (kind == NodeKinds.ForEach) return new ForEachNode { Id = id, Source = "faction_units", Faction = -1, UpTo = 1 };
            if (kind == NodeKinds.ForEachBatched) return new ForEachBatchedNode { Id = id, Source = "faction_units", Faction = -1, BatchSize = 1 };
            // raise_event's name is REQUIRED non-empty at parse (fail-closed) — same placeholder posture as
            // custom_event above. (The pre-review palette seeded "" here, so an added-then-saved raise_event
            // made the stored graph channel unparseable — the exact brick this factory's round-trip test blocks.)
            if (kind == NodeKinds.RaiseEvent) return new RaiseEventNode { Id = id, Name = "new_event" };
            // The minimal deterministic effect payload: a zero HP delta leaf (run_effect cannot serialize a null
            // embedded effect — the converter fails closed on it).
            if (kind == NodeKinds.RunEffect) return new EffectActionNode { Id = id, Effect = new DirectHpDeltaEffect(Fixed.Zero) };

            if (kind == NodeKinds.ExprLiteral) return new ExprLiteralNode { Id = id, ValueType = DslValueType.Int, Raw = 0 };
            if (kind == NodeKinds.ExprVar) return new ExprVarNode { Id = id, Name = "" };
            if (kind == NodeKinds.ExprUnary) return new ExprUnaryNode { Id = id, Op = "neg" };
            if (kind == NodeKinds.ExprBinary) return new ExprBinaryNode { Id = id, Op = "add" };
            if (kind == NodeKinds.ExprCall) return new ExprCallNode { Id = id, Fn = "count" };
            if (kind == NodeKinds.ExprArrayGet) return new ExprArrayGetNode { Id = id, Name = "" };
            if (kind == NodeKinds.ExprArrayLen) return new ExprArrayLenNode { Id = id, Name = "" };
            // expr_event_param's name is REQUIRED non-empty at parse — same placeholder posture.
            if (kind == NodeKinds.ExprEventParam) return new ExprEventParamNode { Id = id, Name = "param" };

            return null;
        }
    }
}
