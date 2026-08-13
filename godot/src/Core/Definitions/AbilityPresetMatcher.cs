#nullable enable
using ProjectChimera.Combat;   // DamageType
using ProjectChimera.Effects;  // the closed 2.1 effect shapes + Modifier/StackRule/StatusFlags/TargetFilter

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Godot-free, Tier-1-testable LOSSLESS preset detection for the Ability Editor (Story 2.5a). Decides whether a
    /// parsed <see cref="AbilityDefinition"/> can be represented by one of the closed <see cref="AbilityPresets"/>
    /// WITHOUT losing data — so the Simple form may edit it and re-save it without silently dropping or rewriting a
    /// field. Extracted from the Godot <c>AbilityEditorPanel</c> (gds-code-review of story-2.5a) so this data-loss
    /// guard has automated coverage; the panel AND Story 2.5b both call this single source of truth.
    ///
    /// A preset matches only when the graph carries EXACTLY the shape AND the fixed (non-tunable) field values that
    /// <see cref="AbilityPresets.Build"/> would emit (a non-Magic damage type, a self-buff's move-speed delta or
    /// modifier id, a non-Enemy AoE filter, … all disqualify). A graph that can't be faithfully represented returns
    /// false and stays in the raw-JSON (Advanced) pane — its only safe editor. Only the genuinely tunable fields
    /// (amount, radius, duration, costs, cooldown) vary across a preset.
    /// </summary>
    public static class AbilityPresetMatcher
    {
        /// <summary>
        /// Try to recognise <paramref name="def"/>'s effect graph as a closed preset. On success returns the
        /// <paramref name="kind"/> plus a <paramref name="p"/> carrying the header + the recovered TUNABLE values, so
        /// <see cref="AbilityPresets.Build"/>(<paramref name="kind"/>, <paramref name="p"/>) reproduces the identical
        /// graph. Returns false (no lossless Simple form) for anything else.
        /// </summary>
        public static bool TryDetectPreset(AbilityDefinition def, out AbilityPresets.Kind kind, out AbilityPresets.Params p)
        {
            p = new AbilityPresets.Params
            {
                Id = def.Id, DisplayName = def.DisplayName,
                CostEnergy = def.CostEnergy, CostOre = def.CostOre, CostCrystal = def.CostCrystal, Cooldown = def.Cooldown,
            };
            switch (def.EffectGraph)
            {
                // Targeted Damage: a single damage leaf — Magic only (the preset forces Magic; another type would be lost).
                case DamageEffect { Type: DamageType.Magic } d:
                    kind = AbilityPresets.Kind.TargetedDamage; p.Amount = d.Amount; return true;

                // Heal: a single heal leaf (Amount is its only field — always faithfully representable).
                case HealEffect h:
                    kind = AbilityPresets.Kind.Heal; p.Amount = h.Amount; return true;

                // Self Buff: apply_modifier, but only if every NON-tunable modifier field matches the preset's fixed shape.
                case ApplyModifierEffect am when IsSimpleSelfBuff(am.Modifier):
                    kind = AbilityPresets.Kind.SelfBuff;
                    p.Amount = am.Modifier.AttackDamageDelta; p.DurationTicks = am.Modifier.DurationTicks; return true;

                // AoE Nuke: search_area(Enemy) → damage(Magic); both the filter and the child damage type must match.
                case SearchAreaEffect { Filter: TargetFilter.Enemy, Child: DamageEffect { Type: DamageType.Magic } cd } sa:
                    kind = AbilityPresets.Kind.AoeNuke; p.Amount = cd.Amount; p.Radius = sa.Radius; return true;

                default:
                    kind = AbilityPresets.Kind.TargetedDamage; return false;   // no lossless Simple form → edit via raw JSON
            }
        }

        /// <summary>True only when the modifier carries EXACTLY the Self Buff preset's fixed (non-tunable) fields, so
        /// reflecting it into Simple and re-saving preserves it byte-for-byte (only attack-damage delta + duration tune).
        /// Guards the data-loss footgun: a modifier with a different id, a move-speed/max-health delta, stacking, status,
        /// a periodic effect, OR (15-24a) ANY sparse stat_deltas entry beyond attack_damage is NOT Simple-representable
        /// and must stay in raw JSON — the DW-903 class: a Simple re-save that cannot express a field would drop it.</summary>
        public static bool IsSimpleSelfBuff(Modifier m)
        {
            if (m == null
                || m.Id != AbilityPresets.SelfBuffModifierId
                || m.MaxStacks != 1
                || m.Stacking != StackRule.Refresh
                || m.Status != StatusFlags.None
                || m.PeriodEffect is not null
                || m.PeriodTicks != 0)
                return false;
            // 15-24a: the WHOLE canonical vector must be expressible by the Simple form's one attack-damage
            // spinner — i.e. every entry (if any) is AttackDamage. This subsumes the old three named-zero checks
            // AND closes the same hole for every future registry stat with no further edit here.
            for (int i = 0; i < m.StatDeltas.Length; i++)
                if (m.StatDeltas[i].Stat != ProjectChimera.Core.Stats.StatId.AttackDamage) return false;
            return true;
        }
    }
}
