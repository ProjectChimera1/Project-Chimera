#nullable enable

namespace ProjectChimera.Core.Stats
{
    /// <summary>
    /// Story 15-24a — the CLOSED stat vocabulary's identity space. One member per stat the platform knows;
    /// every other stat surface (modifier deltas, attribute derivations, item/research authoring, validator
    /// gating, editor dropdowns, the LLM-draft vocabulary, save lanes, checksum folds) keys off this enum
    /// through <see cref="StatVocabulary"/> — never off a bare string or a hand-kept parallel list.
    ///
    /// <para><b>Values are EXPLICIT and APPEND-ONLY, forever.</b> They enter the save format (hero attribute
    /// lanes are strided by <see cref="StatVocabulary.Count"/>; research cumulative lanes are per-stat) and
    /// the canonical content fold (<c>CanonicalFold.MixModifier</c> folds <c>(int)Stat</c> per sparse delta),
    /// so renumbering or inserting silently retargets persisted data and breaks the lobby handshake. A new
    /// stat takes the next free value and appends. Members 0-5 pin the Story 15-21 <c>AttributeStats</c>
    /// order exactly — those indices were load-bearing before this enum existed.</para>
    ///
    /// <para><b>THE ADD-A-STAT RECIPE</b> (the 15-24a deliverable — "adding stat #66 later = one registry
    /// entry + one consumer read, nothing else to touch"):
    /// (1) append a member here; (2) add its <see cref="StatDefinition"/> row in
    /// <see cref="StatVocabulary"/>; (3) write its ONE consumer read at the site the definition's
    /// <c>ConsumerSite</c> names — a Recompute-tier stat's consumer is its line in
    /// <c>ModifierSystem.RecomputeEntity</c> (plus the Effective array that line writes, wired through
    /// EntityWorld's Create/ApplyUnitDefinition/Clear + a save lane + a bounded SimChecksum fold);
    /// a ReadSite-tier stat's consumer is a read at an existing seam. The guard rails then hold you honest:
    /// <c>StatVocabularyGuardTests</c> (registry completeness), the DECLARED-BUT-NEVER-CONSUMED tripwire
    /// (<c>StatConsumerTripwireTests</c> greps for the declared evidence token), and
    /// <c>StatRecomputeCoverageTests</c> (a modifier carrying only the new stat must move its declared
    /// observable). Nothing else changes: validators, editors, drafts, affix eligibility and bounds all
    /// derive from the registry row.</para>
    /// </summary>
    public enum StatId
    {
        // ── The Story 15-21 AttributeStats order, pinned (indices 0-5 are load-bearing) ──
        MaxHealth = 0,
        AttackDamage = 1,
        Armor = 2,
        MoveSpeed = 3,
        MaxEnergy = 4,
        EnergyRegen = 5,

        // ── Story 15-24a additions ──
        /// <summary>Percent: +0.15 = attacks 15% faster. Consumer divides the authored attack interval.</summary>
        AttackSpeed = 6,
        /// <summary>Flat HP restored per SIM TICK (the <c>regen_rate</c> convention: 2 = 60 HP/s at 30 tps).</summary>
        HealthRegen = 7,
        /// <summary>Flat vision radius, world units (the authored <c>vision_range</c> channel's modifier arm).</summary>
        VisionRange = 8,
        /// <summary>Percent of a cooldown removed: 0.3 = 30% shorter. Negative = longer (a debuff).</summary>
        CooldownReduction = 9,
        /// <summary>Percent sibling of <see cref="MaxHealth"/>: multiplies (base + flat) by (1 + Σ).</summary>
        MaxHealthPercent = 10,
        /// <summary>Percent sibling of <see cref="AttackDamage"/>.</summary>
        AttackDamagePercent = 11,
        /// <summary>Percent sibling of <see cref="MoveSpeed"/> (catalog #31).</summary>
        MoveSpeedPercent = 12,
        /// <summary>Percent sibling of <see cref="VisionRange"/> (catalog #34, "vision_percent").</summary>
        VisionPercent = 13,

        // ── Story 15-24b additions (the deterministic combat dice) ──
        /// <summary>Chance in [0,1] that a WEAPON hit crits (×<c>CritMultiplierOf</c> damage). Rolled at the
        /// attack COMMIT (hitscan swing / projectile launch) on the shared <c>SimRng</c> stream.</summary>
        CritChance = 14,
        /// <summary>Chance in [0, 0.75] that the VICTIM negates a weapon hit entirely. Rolled at the damage
        /// ARRIVAL (the <c>DamageResolver.Apply</c> single point); splash and ability damage never roll.</summary>
        DodgeChance = 15,
        /// <summary>Percent added to the crit damage multiplier: total = 1.5 (the base) + Σ, floored so a
        /// crit never deals less than a normal hit.</summary>
        CritMultiplier = 16,
    }
}
