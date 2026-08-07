#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Effects;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// DW-900 — APPEND generated effect nodes onto an ability the author already has, instead of replacing it.
    ///
    /// <para><b>Why this exists.</b> The ability editor had exactly one AI entry point and it was unconditionally
    /// destructive: the generated <see cref="AbilityDefinition"/> went straight to <c>LoadFromRegistry</c>, which
    /// overwrites the header fields, the costs, the raw-JSON pane AND the whole effect graph. Iterating therefore
    /// meant re-describing the entire ability every time. Alec asked for an "add more" action so the model extends
    /// what is there.</para>
    ///
    /// <para><b>Why it merges on the immutable side.</b> The append builds new <see cref="EffectNode"/>s directly and
    /// never round-trips through <c>AbilityDraft</c>/<c>DraftNode</c>. That is load-bearing, not stylistic: the draft
    /// layer does not carry <c>RequireTag</c>, so merging through it would silently strip the tag gate off the
    /// author's EXISTING nodes — a data-loss bug that would look exactly like the AI "changing" untouched effects.</para>
    ///
    /// <para><b>Contract.</b> <see cref="CanAppend"/> is a cheap pre-flight meant to run BEFORE an LLM call is spent;
    /// <see cref="Append"/> is a pure function returning a NEW definition. Neither validates — the caller runs the
    /// real <see cref="AbilityValidator"/> on the result and discards the merge if it fails, so a bad suggestion can
    /// never corrupt the in-progress draft. Godot-free, so all of it is Tier-1 testable.</para>
    /// </summary>
    public static class AbilityGraphMerge
    {
        /// <summary>
        /// Can generated nodes be appended to <paramref name="current"/>? Answers with the author-facing reason when
        /// not, so the panel can explain the refusal without spending a provider call.
        /// </summary>
        public static bool CanAppend(AbilityDefinition? current, out string reason)
        {
            reason = "";
            if (current is null || current.EffectGraph is null)
            {
                reason = "There is no effect graph to add to yet — use Generate to draft one first.";
                return false;
            }

            // A passive's root shape is pinned by the validator's passive-shape rule (an aura must be a SearchArea
            // root, a while-alive a Persistent root), so wrapping it in a Sequence would turn a valid ability invalid.
            if (current.ParsedActivation is PassiveActivation.Aura or PassiveActivation.WhileAlive)
            {
                reason = $"A '{current.Activation}' ability has a fixed root shape, so effects cannot be appended to it. " +
                         "Edit it in the composer or the Raw JSON pane instead.";
                return false;
            }

            if (current.EffectGraph is SequenceEffect seq && seq.Children.Length >= EffectCaps.MaxSequenceChildren)
            {
                reason = $"This sequence already holds the maximum {EffectCaps.MaxSequenceChildren} effects.";
                return false;
            }

            int nodes = CountNodes(current.EffectGraph);
            if (nodes >= EffectCaps.MaxTotalEffectNodes)
            {
                reason = $"This ability already holds the maximum {EffectCaps.MaxTotalEffectNodes} effect nodes.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// A COPY of <paramref name="current"/> whose graph has <paramref name="additions"/> appended.
        ///
        /// <para>Root already a <see cref="SequenceEffect"/> ⇒ the additions become further children (so the graph does
        /// not gain a pointless nesting level on every round). Any other root ⇒ the existing root and the additions
        /// become the children of one new sequence. Every other field is carried across verbatim — including the ones
        /// the draft layer would drop.</para>
        /// </summary>
        public static AbilityDefinition Append(AbilityDefinition current, IReadOnlyList<EffectNode> additions)
        {
            if (current is null) throw new ArgumentNullException(nameof(current));
            if (additions is null || additions.Count == 0) throw new ArgumentException("No nodes to append.", nameof(additions));
            if (current.EffectGraph is null) throw new InvalidOperationException("Nothing to append to — the ability has no effect graph.");

            var children = new List<EffectNode>();
            if (current.EffectGraph is SequenceEffect seq) children.AddRange(seq.Children);
            else children.Add(current.EffectGraph);
            children.AddRange(additions);

            return new AbilityDefinition
            {
                Id             = current.Id,
                DisplayName    = current.DisplayName,
                Targeting      = current.Targeting,
                TargetAffinity = current.TargetAffinity,
                Activation     = current.Activation,
                CostEnergy     = current.CostEnergy,
                CostOre        = current.CostOre,
                CostCrystal    = current.CostCrystal,
                CostHealth     = current.CostHealth,
                AllowSelfLethal = current.AllowSelfLethal,
                Cooldown       = current.Cooldown,
                CombatFeedback = current.CombatFeedback,
                EffectGraph    = new SequenceEffect(children.ToArray()),
            };
        }

        /// <summary>Total nodes in the subtree — the same metric <c>EffectCaps.MaxTotalEffectNodes</c> bounds.</summary>
        public static int CountNodes(EffectNode? node)
        {
            if (node is null) return 0;
            int n = 1;
            switch (node)
            {
                case SequenceEffect s:
                    foreach (EffectNode c in s.Children) n += CountNodes(c);
                    break;
                case SearchAreaEffect a:
                    n += CountNodes(a.Child);
                    break;
                case PersistentEffect p:
                    n += CountNodes(p.InitialEffect) + CountNodes(p.PeriodEffect) + CountNodes(p.ExpireEffect);
                    break;
            }
            return n;
        }
    }
}
