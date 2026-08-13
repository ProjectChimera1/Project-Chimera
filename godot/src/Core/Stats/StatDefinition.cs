#nullable enable

namespace ProjectChimera.Core.Stats
{
    /// <summary>How a stat's contributions combine in the generalized recompute.</summary>
    public enum StatAggregation : byte
    {
        /// <summary>Contributions sum in stat units; <c>Effective = clamp(saturate(Base + Σflat) × (1 + Σ paired %))</c>.</summary>
        Flat = 0,
        /// <summary>Contributions are fractions that SUM (order-independent — never sequential multiplication),
        /// then apply once: either into a paired value stat's recompute (<see cref="StatDefinition.PercentTarget"/>)
        /// or as the stat's own clamped value (attack_speed, cooldown_reduction).</summary>
        Percent = 1,
        /// <summary>A probability in [0,1] (crit/dodge — the 15-24b dice ride these; clamp is [0, 1]).</summary>
        Chance = 2,
        /// <summary>A per-hit magnitude consumed at a proc site (lifesteal, cleave — 15-24b+).</summary>
        PerHitMagnitude = 3,
        /// <summary>A radius for an aura-tier stat (the four [AURA] catalog entries — effect-graph groundwork).</summary>
        AuraRadius = 4,
    }

    /// <summary>Which implementation lane a stat's consumer lives in (recorded per stat in the registry).</summary>
    public enum StatTier : byte
    {
        /// <summary>Pure stat math: <c>ModifierSystem.RecomputeEntity</c> writes an Effective channel a
        /// consumer reads (most of the vocabulary).</summary>
        Recompute = 0,
        /// <summary>Computed at an existing read seam instead of an Effective array (the 15.12 energy pair:
        /// <c>EnergyRegenSystem.MaxEnergyOf</c>/<c>RegenPerTick</c>).</summary>
        ReadSite = 1,
        /// <summary>An on-hit/on-kill proc site (needs the 15-24b deterministic dice where flagged).</summary>
        Proc = 2,
        /// <summary>Radiates to nearby allies (the effect-graph groundwork in stat form — 15-24 later legs).</summary>
        Aura = 3,
        /// <summary>A 15-24c derivation shape (thresholds), resolve-time math.</summary>
        Threshold = 4,
    }

    /// <summary>
    /// Story 15-24a — one stat's complete declaration. Everything the pipeline knows about a stat derives
    /// from this row: validator gating and bounds, editor dropdowns and spinner ranges, item-affix
    /// eligibility (15-24g), the LLM-draft vocabulary, attribute-model targetability, and the tripwire
    /// evidence the guard tests grep for. Pure immutable data — no behavior, no Godot, no floats.
    /// </summary>
    public sealed class StatDefinition
    {
        /// <summary>Identity; equals this row's index in <see cref="StatVocabulary.All"/> (guard-tested).</summary>
        public readonly StatId Id;

        /// <summary>The canonical authoring token: JSON keys (<c>stat_deltas</c>, attribute-model
        /// <c>derived[].stat</c>), validator messages, and the content hash all use exactly this string.
        /// Renaming one is a lobby-handshake break — never do it.</summary>
        public readonly string JsonName;

        /// <summary>Human display name for editors and tooltips.</summary>
        public readonly string DisplayName;

        /// <summary>How contributions combine (see <see cref="StatAggregation"/>).</summary>
        public readonly StatAggregation Aggregation;

        /// <summary>Which implementation lane the consumer lives in (see <see cref="StatTier"/>).</summary>
        public readonly StatTier Tier;

        /// <summary>For a <see cref="StatAggregation.Percent"/> stat that multiplies a VALUE stat's
        /// recompute (the % siblings): the target stat. Null for every other stat, including percent stats
        /// consumed as their own clamped value (attack_speed, cooldown_reduction).</summary>
        public readonly StatId? PercentTarget;

        /// <summary>Inclusive clamp applied to the stat's EFFECTIVE value by its recompute/consumer, in raw
        /// 16.16 units. Legacy stats use [0, int.MaxValue] — exactly the Story 2.2b zero-floor, so the
        /// generalized recompute is byte-identical for them.</summary>
        public readonly int EffectiveMinRaw;
        /// <inheritdoc cref="EffectiveMinRaw"/>
        public readonly int EffectiveMaxRaw;

        /// <summary>Optional per-delta authoring cap (|delta| in raw units) applied ON TOP of the DW-488
        /// accumulator bound. 0 = no per-stat cap (the DW-488 bound alone gates, exactly today's rule for
        /// the legacy four — a tighter new cap would reject previously-valid content). Percent stats carry
        /// real caps here because the DW-488 bound (~4096 stat units) is meaningless for a fraction.</summary>
        public readonly int MaxAbsDeltaRaw;

        /// <summary>May a faction attribute model derive this stat (<c>derived[].stat</c>)? Feeds the
        /// validator gate and the 15-21 mapping editor's dropdown through the <c>AttributeStats</c> facade.</summary>
        public readonly bool AttributeTargetable;

        /// <summary>May a Modifier carry a delta for this stat? False for the energy pair until the 15.12
        /// read seams gain a ModifierSystem reference (recorded 15-24a seam) — the validator fail-closes.</summary>
        public readonly bool ModifierAuthorable;

        /// <summary>Is this stat on the 15-24g item-affix menu when the affix system lands? (Declared now so
        /// the shared vocabulary ships complete; nothing reads it until 15-24g.)</summary>
        public readonly bool AffixEligible;

        /// <summary>The DECLARED-BUT-NEVER-CONSUMED tripwire's evidence: a literal source token that exists
        /// in <c>godot/src/**</c> iff this stat's consumer is wired (an Effective array name, or a seam
        /// method name). <c>StatConsumerTripwireTests</c> greps for it — a registry row whose token vanishes
        /// from the tree fails loudly (the computed-but-never-consumed class, made a gate).</summary>
        public readonly string ConsumerEvidence;

        /// <summary>Human hint: where the consumer read lives ("CombatSystem.TryDealDamage re-arm").</summary>
        public readonly string ConsumerSite;

        public StatDefinition(
            StatId id, string jsonName, string displayName, StatAggregation aggregation, StatTier tier,
            int effectiveMinRaw, int effectiveMaxRaw, string consumerEvidence, string consumerSite,
            StatId? percentTarget = null, int maxAbsDeltaRaw = 0,
            bool attributeTargetable = true, bool modifierAuthorable = true, bool affixEligible = true)
        {
            Id = id;
            JsonName = jsonName;
            DisplayName = displayName;
            Aggregation = aggregation;
            Tier = tier;
            PercentTarget = percentTarget;
            EffectiveMinRaw = effectiveMinRaw;
            EffectiveMaxRaw = effectiveMaxRaw;
            MaxAbsDeltaRaw = maxAbsDeltaRaw;
            AttributeTargetable = attributeTargetable;
            ModifierAuthorable = modifierAuthorable;
            AffixEligible = affixEligible;
            ConsumerEvidence = consumerEvidence;
            ConsumerSite = consumerSite;
        }
    }
}
