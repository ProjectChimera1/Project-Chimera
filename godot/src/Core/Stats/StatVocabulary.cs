#nullable enable
using System.Collections.Generic;

namespace ProjectChimera.Core.Stats
{
    /// <summary>
    /// Story 15-24a — THE STAT REGISTRY: one <see cref="StatDefinition"/> per <see cref="StatId"/>, the
    /// single authority every stat surface derives from (validators, editor dropdowns, affix eligibility,
    /// the LLM-draft vocabulary, attribute-model targets, recompute bounds, the tripwire evidence).
    ///
    /// <para><b>THE ADD-A-STAT RECIPE:</b> append a <see cref="StatId"/> member, add its row to
    /// <see cref="All"/> (same index — guard-tested), write its ONE consumer read at the declared site.
    /// See the recipe walkthrough on <see cref="StatId"/>. The vocabulary GROWS by conscious addition,
    /// never opens to reflection (the closed-vocabulary rule).</para>
    ///
    /// <para><b>Determinism.</b> The registry is compile-time constant data — identical on every peer by
    /// construction, so it folds into no hash (the same posture as the <see cref="StatId"/> enum itself;
    /// authored VALUES that flow through it are folded by their own lanes). All iteration is by ascending
    /// array index; the name dictionary is lookup-only (never enumerated — CHM0002).</para>
    ///
    /// <para><b>Bounds are named-constant-derived</b> (CHM0004) and deliberately NOT
    /// <c>EffectCaps</c> entries: EffectCaps folds into the RulesetHash wire fingerprint and these are
    /// authoring/recompute clamps, not execution caps (the <c>Modifier.MaxStatDeltaTotalRaw</c> precedent).</para>
    /// </summary>
    public static class StatVocabulary
    {
        // ── Named bound derivations (raw 16.16) ─────────────────────────────────────────────────────────
        // Percent-family per-delta authoring cap and sum clamps. 8.0 = ±800%: orders of magnitude beyond any
        // sane design and still ~500x below the DW-488 wrap ceiling, so the clamp is a semantic bound, not
        // an overflow defense (DW-488 stays the overflow defense).
        private const int PercentDeltaCapRaw = 8 * Fixed.ONE;
        /// <summary>Percent-sibling Σ clamp: [−8, +8]. The recompute additionally floors the realized
        /// multiplier (1 + Σ) at zero, so a deep debuff zeroes a stat instead of sign-flipping it.</summary>
        public const int PercentSumMinRaw = -PercentDeltaCapRaw;
        /// <inheritdoc cref="PercentSumMinRaw"/>
        public const int PercentSumMaxRaw = PercentDeltaCapRaw;
        /// <summary>attack_speed Σ clamp: [−0.9, +9]. The lower bound keeps (1 + Σ) strictly positive so the
        /// interval division can never divide by ≤ 0 (a −100% attack-speed debuff is "10× slower", not a
        /// division blow-up; "cannot attack" stays <c>StatusFlags.Disarmed</c>'s job).</summary>
        public const int AttackSpeedSumMinRaw = -(Fixed.ONE * 9 / 10);
        /// <inheritdoc cref="AttackSpeedSumMinRaw"/>
        public const int AttackSpeedSumMaxRaw = 9 * Fixed.ONE;
        /// <summary>cooldown_reduction Σ clamp: [−4, +0.8]. The 0.8 cap floors armed cooldowns at 20% of
        /// authored (the machine-gun guard's cooldown twin); negative = cooldown-increase debuffs, bounded
        /// at 5× longer.</summary>
        public const int CooldownReductionSumMinRaw = -(4 * Fixed.ONE);
        /// <inheritdoc cref="CooldownReductionSumMinRaw"/>
        public const int CooldownReductionSumMaxRaw = Fixed.ONE * 4 / 5;

        // ── Story 15-24b: the combat-dice bounds ──
        /// <summary>crit_chance Σ clamp: [0, 1] — the full probability domain (100% = every weapon hit crits;
        /// balance is authoring's job, the domain is the registry's). Debuff-negative sums floor at 0.</summary>
        public const int CritChanceSumMinRaw = 0;
        /// <inheritdoc cref="CritChanceSumMinRaw"/>
        public const int CritChanceSumMaxRaw = Fixed.ONE;
        /// <summary>dodge_chance Σ clamp: [0, 0.75] — the ARPG-standard hard cap (a 100% dodge would be
        /// <c>StatusFlags.Invulnerable</c> with extra steps and no counterplay; 25% of hits always land).</summary>
        public const int DodgeChanceSumMinRaw = 0;
        /// <inheritdoc cref="DodgeChanceSumMinRaw"/>
        public const int DodgeChanceSumMaxRaw = Fixed.ONE * 3 / 4;
        /// <summary>crit_multiplier Σ clamp: [−0.5, +8]. Added to the ×1.5 base
        /// (<c>EntityWorld.CritBaseMultiplierRaw</c>), so the TOTAL crit multiplier spans [1.0, 9.5] — a crit
        /// never deals less than a normal hit. The bound is SEMANTIC (the multiplier VALUE itself can never
        /// overflow anything); overflow of the amplified DAMAGE is owned downstream by the saturating
        /// multiplies at the crit scale and inside <c>DamageTable.FinalDamage</c>'s matrix stage.</summary>
        public const int CritBonusSumMinRaw = -(Fixed.ONE / 2);
        /// <inheritdoc cref="CritBonusSumMinRaw"/>
        public const int CritBonusSumMaxRaw = 8 * Fixed.ONE;

        /// <summary>The registry. Index == <c>(int)StatId</c> (guard-tested by StatVocabularyGuardTests).</summary>
        public static readonly StatDefinition[] All =
        {
            // ── Legacy modifier channels (Story 2.2a/2.6): bounds are exactly the 2.2b zero-floor ──
            new StatDefinition(StatId.MaxHealth, "max_health", "Max Health",
                StatAggregation.Flat, StatTier.Recompute, 0, int.MaxValue,
                consumerEvidence: "EffectiveMaxHealth", consumerSite: "Health clamp ceiling (HealEffect/DirectHpDelta/ModifierStore) + ceiling-collapse death"),
            new StatDefinition(StatId.AttackDamage, "attack_damage", "Attack Damage",
                StatAggregation.Flat, StatTier.Recompute, 0, int.MaxValue,
                consumerEvidence: "EffectiveAttackDamage", consumerSite: "CombatSystem damage snapshot at swing/launch"),
            new StatDefinition(StatId.Armor, "armor", "Armor",
                StatAggregation.Flat, StatTier.Recompute, 0, int.MaxValue,
                consumerEvidence: "EffectiveArmor", consumerSite: "DamageResolver flat armor subtraction (live per impact)"),
            new StatDefinition(StatId.MoveSpeed, "move_speed", "Move Speed",
                StatAggregation.Flat, StatTier.Recompute, 0, int.MaxValue,
                consumerEvidence: "EffectiveMoveSpeed", consumerSite: "MovementSystem seek/clamp + GatheringSystem walk"),

            // ── The 15.12 energy pair: consumed at read seams; modifier deltas NOT yet authorable
            //    (the static seams hold no ModifierSystem ref — recorded 15-24a seam, own DW) ──
            new StatDefinition(StatId.MaxEnergy, "max_energy", "Max Energy",
                StatAggregation.Flat, StatTier.ReadSite, 0, int.MaxValue,
                consumerEvidence: "MaxEnergyOf", consumerSite: "EnergyRegenSystem.MaxEnergyOf (15.12 seam)",
                modifierAuthorable: false),
            new StatDefinition(StatId.EnergyRegen, "energy_regen", "Energy Regen",
                StatAggregation.Flat, StatTier.ReadSite, 0, int.MaxValue,
                consumerEvidence: "RegenPerTick", consumerSite: "EnergyRegenSystem.RegenPerTick (15.12 seam)",
                modifierAuthorable: false),

            // ── Story 15-24a consumer stats ──
            new StatDefinition(StatId.AttackSpeed, "attack_speed", "Attack Speed",
                StatAggregation.Percent, StatTier.Recompute, AttackSpeedSumMinRaw, AttackSpeedSumMaxRaw,
                consumerEvidence: "AttackIntervalOf", consumerSite: "CombatSystem swing re-arm via EntityWorld.AttackIntervalOf (interval = authored / factor)",
                maxAbsDeltaRaw: PercentDeltaCapRaw),
            new StatDefinition(StatId.HealthRegen, "health_regen", "Health Regen",
                StatAggregation.Flat, StatTier.Recompute, 0, int.MaxValue,
                consumerEvidence: "EffectiveHealthRegen", consumerSite: "HealthRegenSystem per-tick clamp-add"),
            new StatDefinition(StatId.VisionRange, "vision_range", "Vision Range",
                StatAggregation.Flat, StatTier.Recompute, 0, int.MaxValue,
                consumerEvidence: "VisionBonusFlat", consumerSite: "EntityWorld.VisionWithElevation merge → FogOfWarSystem"),
            new StatDefinition(StatId.CooldownReduction, "cooldown_reduction", "Cooldown Reduction",
                StatAggregation.Percent, StatTier.Recompute, CooldownReductionSumMinRaw, CooldownReductionSumMaxRaw,
                consumerEvidence: "EffectiveCooldownReduction", consumerSite: "AbilityCastSystem cooldown arming (cd × (1 − CDR))",
                maxAbsDeltaRaw: PercentDeltaCapRaw),

            // ── Percent siblings: consumed BY the recompute itself (PercentTarget pairing) ──
            new StatDefinition(StatId.MaxHealthPercent, "max_health_percent", "Max Health %",
                StatAggregation.Percent, StatTier.Recompute, PercentSumMinRaw, PercentSumMaxRaw,
                consumerEvidence: "MaxHealthPercent", consumerSite: "RecomputeEntity pairing → EffectiveMaxHealth",
                percentTarget: StatId.MaxHealth, maxAbsDeltaRaw: PercentDeltaCapRaw),
            new StatDefinition(StatId.AttackDamagePercent, "attack_damage_percent", "Attack Damage %",
                StatAggregation.Percent, StatTier.Recompute, PercentSumMinRaw, PercentSumMaxRaw,
                consumerEvidence: "AttackDamagePercent", consumerSite: "RecomputeEntity pairing → EffectiveAttackDamage",
                percentTarget: StatId.AttackDamage, maxAbsDeltaRaw: PercentDeltaCapRaw),
            new StatDefinition(StatId.MoveSpeedPercent, "move_speed_percent", "Move Speed %",
                StatAggregation.Percent, StatTier.Recompute, PercentSumMinRaw, PercentSumMaxRaw,
                consumerEvidence: "MoveSpeedPercent", consumerSite: "RecomputeEntity pairing → EffectiveMoveSpeed",
                percentTarget: StatId.MoveSpeed, maxAbsDeltaRaw: PercentDeltaCapRaw),
            new StatDefinition(StatId.VisionPercent, "vision_percent", "Vision %",
                StatAggregation.Percent, StatTier.Recompute, PercentSumMinRaw, PercentSumMaxRaw,
                consumerEvidence: "VisionBonusPct", consumerSite: "EntityWorld.VisionWithElevation merge (× (1 + Σ))",
                percentTarget: StatId.VisionRange, maxAbsDeltaRaw: PercentDeltaCapRaw),

            // ── Story 15-24b: the deterministic combat dice (SimRng draws at the two documented roll points;
            //    a zero chance NEVER draws, so content without these stats leaves the RNG stream untouched) ──
            new StatDefinition(StatId.CritChance, "crit_chance", "Critical Chance",
                StatAggregation.Chance, StatTier.Recompute, CritChanceSumMinRaw, CritChanceSumMaxRaw,
                consumerEvidence: "EffectiveCritChance", consumerSite: "CombatSystem attack-commit roll (hitscan swing / projectile launch)",
                maxAbsDeltaRaw: Fixed.ONE),
            new StatDefinition(StatId.DodgeChance, "dodge_chance", "Dodge Chance",
                StatAggregation.Chance, StatTier.Recompute, DodgeChanceSumMinRaw, DodgeChanceSumMaxRaw,
                consumerEvidence: "EffectiveDodgeChance", consumerSite: "DamageResolver.Apply weapon-hit arrival roll (victim-side)",
                maxAbsDeltaRaw: Fixed.ONE),
            new StatDefinition(StatId.CritMultiplier, "crit_multiplier", "Critical Damage %",
                StatAggregation.Percent, StatTier.Recompute, CritBonusSumMinRaw, CritBonusSumMaxRaw,
                consumerEvidence: "CritMultiplierOf", consumerSite: "EntityWorld.CritMultiplierOf (1.5 base + Σ) at the crit-commit scale",
                maxAbsDeltaRaw: PercentDeltaCapRaw),
        };

        /// <summary>Number of declared stats. Strides the hero attribute lanes (HeroStore + save v10).</summary>
        public static int Count => All.Length;

        /// <summary>The shared empty vector (a zero-delta modifier holds this — never a fresh allocation).</summary>
        public static readonly StatDelta[] EmptyDeltas = System.Array.Empty<StatDelta>();

        /// <summary>Registry row for <paramref name="id"/> (direct index — ids equal indices, guard-tested).</summary>
        public static StatDefinition Get(StatId id) => All[(int)id];

        // Lookup-only (TryGetValue; never enumerated — CHM0002). Ordinal, case-sensitive: JsonNames are
        // exact authoring tokens, exactly like every other closed vocabulary in the content lane.
        private static readonly Dictionary<string, StatDefinition> ByName = BuildByName();

        private static Dictionary<string, StatDefinition> BuildByName()
        {
            var map = new Dictionary<string, StatDefinition>(All.Length, System.StringComparer.Ordinal);
            for (int i = 0; i < All.Length; i++) map.Add(All[i].JsonName, All[i]);
            return map;
        }

        /// <summary>The ONLY string → stat mapping in the platform (fail-closed: false = outside the
        /// closed vocabulary). Validators locate their own errors around it.</summary>
        public static bool TryByJsonName(string? name, out StatDefinition def)
        {
            if (name != null && ByName.TryGetValue(name, out def!)) return true;
            def = null!;
            return false;
        }

        /// <summary>
        /// Canonicalize a scratch list into THE vector representation every consumer agrees on: ascending
        /// <see cref="StatId"/>, duplicate ids merged by summation (wrapping int add — the accumulator's own
        /// convention; DW-488 bounds the authored magnitudes long before a merge can wrap), zero entries
        /// dropped. Load/authoring-time only (allocates); returns <see cref="EmptyDeltas"/> for an
        /// all-zero result so inert vectors are reference-shared.
        /// </summary>
        public static StatDelta[] Canonicalize(List<StatDelta> scratch)
        {
            if (scratch.Count == 0) return EmptyDeltas;

            // Insertion sort by ascending stat id (stable, comparer-free — CHM0003-clean, tiny N).
            for (int i = 1; i < scratch.Count; i++)
            {
                StatDelta cur = scratch[i];
                int j = i - 1;
                while (j >= 0 && (int)scratch[j].Stat > (int)cur.Stat)
                {
                    scratch[j + 1] = scratch[j];
                    j--;
                }
                scratch[j + 1] = cur;
            }

            // Merge duplicates + count survivors.
            int survivors = 0;
            for (int i = 0; i < scratch.Count;)
            {
                StatId stat = scratch[i].Stat;
                Fixed sum = scratch[i].Delta;
                int j = i + 1;
                while (j < scratch.Count && scratch[j].Stat == stat)
                {
                    sum += scratch[j].Delta;
                    j++;
                }
                scratch[survivors++] = new StatDelta(stat, sum);
                i = j;
            }

            int nonZero = 0;
            for (int i = 0; i < survivors; i++)
                if (scratch[i].Delta.Raw != 0) nonZero++;
            if (nonZero == 0) return EmptyDeltas;

            var result = new StatDelta[nonZero];
            int w = 0;
            for (int i = 0; i < survivors; i++)
                if (scratch[i].Delta.Raw != 0) result[w++] = scratch[i];
            return result;
        }

        /// <summary>
        /// Canonical vector from the four legacy channel values (the compatibility ctor's builder — the
        /// StatId order MaxHealth &lt; AttackDamage &lt; Armor &lt; MoveSpeed is already ascending).
        /// Returns <see cref="EmptyDeltas"/> when all four are zero.
        /// </summary>
        public static StatDelta[] FromLegacyFour(Fixed maxHealth, Fixed attackDamage, Fixed armor, Fixed moveSpeed)
        {
            int n = (maxHealth.Raw != 0 ? 1 : 0) + (attackDamage.Raw != 0 ? 1 : 0)
                  + (armor.Raw != 0 ? 1 : 0) + (moveSpeed.Raw != 0 ? 1 : 0);
            if (n == 0) return EmptyDeltas;
            var result = new StatDelta[n];
            int w = 0;
            if (maxHealth.Raw != 0) result[w++] = new StatDelta(StatId.MaxHealth, maxHealth);
            if (attackDamage.Raw != 0) result[w++] = new StatDelta(StatId.AttackDamage, attackDamage);
            if (armor.Raw != 0) result[w++] = new StatDelta(StatId.Armor, armor);
            if (moveSpeed.Raw != 0) result[w++] = new StatDelta(StatId.MoveSpeed, moveSpeed);
            return result;
        }

        /// <summary>Linear scan of a canonical vector for one stat's delta (Zero when absent). Tiny N —
        /// cheaper than any map, allocation-free, and callers on hot paths iterate the vector instead.</summary>
        public static Fixed DeltaOf(StatDelta[] deltas, StatId stat)
        {
            for (int i = 0; i < deltas.Length; i++)
                if (deltas[i].Stat == stat) return deltas[i].Delta;
            return Fixed.Zero;
        }
    }
}
