#nullable enable
using System.Text.Json.Serialization;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The authored hero definition for a unit marked <see cref="UnitDefinition.IsHero"/> (Story 3.7) — the leveling
    /// curve, XP-gain rule, and signature/ultimate ability slots that Epic 3's "Author Units &amp; Heroes" feature
    /// exposes in the Unit Card Editor. A nullable nested <see cref="UnitDefinition.Hero"/> block (JSON <c>hero</c>):
    /// null on a non-hero unit, instantiated (with the <c>Standard</c> leveling preset) when the creator flips the
    /// Promote-to-Hero switch on.
    ///
    /// <para><b>Authoring-only (D-2, mirrors <see cref="UnitDefinition.Behaviors"/>).</b> This is PURE AUTHORING DATA.
    /// No sim system resolves or consumes it yet — the XP/leveling <b>runtime</b> (kill-credit XP, level-up, stat
    /// growth, ability unlock) is Story 3.13. So there is deliberately NO <c>Parsed*</c>/<c>Resolve</c>/<c>[JsonIgnore]</c>
    /// index and NO per-entity SoA fold: an unread nullable POCO moves no golden (<c>CanonicalModelHash</c> hashes by
    /// path + id string) and no checksum. Story 3.13 adds the resolve+fold when it builds the runtime.</para>
    ///
    /// <para><b>Determinism.</b> Godot-free (<c>src/Core/Definitions</c>), plain <c>int</c>/<c>float</c> authoring
    /// numbers (the 3.13 consumer quantizes to <see cref="ProjectChimera.Core.Fixed"/> at the single boundary, like
    /// <see cref="UnitDefinition.MaxEnergy"/>). The <see cref="UnitDefinitionValidator"/> range/ref/composition rules
    /// gate it at authoring time.</para>
    /// </summary>
    public sealed class HeroDefinition
    {
        /// <summary>The maximum level this hero can reach. Validated to <c>[HeroLevelMin, HeroLevelMax]</c>.</summary>
        [JsonPropertyName("max_level")]
        public int MaxLevel { get; set; } = 10;

        /// <summary>XP required for the first level-up (the base of the geometric curve). Validated finite &amp; &gt; 0.</summary>
        [JsonPropertyName("base_xp")]
        public float BaseXp { get; set; } = 100f;

        /// <summary>Per-level geometric multiplier on the XP requirement. Validated finite &amp; &gt;= 1.</summary>
        [JsonPropertyName("xp_growth")]
        public float XpGrowth { get; set; } = 1.15f;

        /// <summary>Per-hero XP-gain multiplier, as a PERCENTAGE, layered on the victim's <c>xp_bounty</c>. Validated
        /// finite &amp; &gt;= 0. The default 100 = 100% = a neutral ×1.0.
        /// <para>DW-26: repurposed from the pre-Story-3.13 "flat XP per kill" (which the victim-centric runtime never
        /// consumed) into a live per-hero gain scalar. The runtime is STILL victim-<c>xp_bounty</c> driven (each enemy
        /// carries its own bounty on <see cref="UnitDefinition.XpBounty"/>); this factor scales what THIS hero banks
        /// from a kill: each credit becomes <c>victim.XpBounty × (xp_per_kill / 100)</c>. 100 credits the full bounty
        /// (bit-identical to the old runtime); 200 = double; 50 = half; 0 = earns no kill XP. Resolved float→
        /// <see cref="ProjectChimera.Core.Fixed"/> at the single applier load boundary (like <see cref="BaseXp"/>/
        /// <see cref="XpGrowth"/>) and consumed by <see cref="ProjectChimera.Combat.HeroXpSystem"/> as the non-folded
        /// per-hero <c>XpGainFactorOf</c>; a divergence surfaces transitively through the folded <c>Xp</c>/<c>Level</c>.</para></summary>
        [JsonPropertyName("xp_per_kill")]
        public float XpPerKill { get; set; } = 100f;

        /// <summary>Story 3.13: radius (world units) within which a hostile unit's death credits this hero its XP bounty
        /// (proximity credit, not split — every hero in range gets the full bounty). Validated finite &amp; in
        /// <c>[0, 128)</c> — TIGHTER than the generic stat Range so the runtime's squared-distance test (<c>r*r</c>) cannot
        /// overflow 16.16 Fixed. Quantized to <see cref="ProjectChimera.Core.Fixed"/> at the single load boundary (the
        /// applier's PlacedHero capture), never inside a tick. Default 12.</summary>
        [JsonPropertyName("xp_share_radius")]
        public float XpShareRadius { get; set; } = 12f;

        /// <summary>Story 3.13: flat max-health added per hero level above 1 (applied through the folded
        /// <c>ModifierStore</c> as a permanent, non-dispellable stacked modifier — total growth = <c>(Level-1)</c> stacks).
        /// Validated finite &amp; in <c>[0, 256)</c> — TIGHTER than the generic Range so the up-to-99-stack sum cannot
        /// overflow the Effective stat. Default 0 (no growth).</summary>
        [JsonPropertyName("health_per_level")]
        public float HealthPerLevel { get; set; } = 0f;

        /// <summary>Story 3.13: flat attack-damage added per hero level above 1 (see <see cref="HealthPerLevel"/>).
        /// Validated finite &amp; in <c>[0, 256)</c>. Default 0.</summary>
        [JsonPropertyName("damage_per_level")]
        public float DamagePerLevel { get; set; } = 0f;

        /// <summary>Story 3.13: flat armor added per hero level above 1 (see <see cref="HealthPerLevel"/>).
        /// Validated finite &amp; in <c>[0, 256)</c>. Default 0.</summary>
        [JsonPropertyName("armor_per_level")]
        public float ArmorPerLevel { get; set; } = 0f;

        /// <summary>The signature ability id. Authoring only — the ability-UNLOCK-on-level-up runtime is NOT implemented by
        /// Story 3.13 (which owns only numeric leveling + stat growth); it is chartered to a later story. Null/empty = not
        /// authored yet (valid); a set-but-undefined ref is rejected by the validator.</summary>
        [JsonPropertyName("signature_ability")]
        public string? SignatureAbility { get; set; }

        /// <summary>The ultimate ability id. Authoring only — unlock-on-level-up is NOT implemented by Story 3.13 (see
        /// <see cref="SignatureAbility"/>); chartered to a later story. Null/empty = not authored yet (valid); a
        /// set-but-undefined ref, or one equal to <see cref="SignatureAbility"/>, is rejected by the validator.</summary>
        [JsonPropertyName("ultimate_ability")]
        public string? UltimateAbility { get; set; }

        /// <summary>A member-wise copy (all fields are value types or immutable strings) — the Duplicate path deep-copies
        /// <see cref="UnitDefinition.Hero"/> so a clone and its source validate independently.</summary>
        public HeroDefinition Clone() => new HeroDefinition
        {
            MaxLevel = MaxLevel,
            BaseXp = BaseXp,
            XpGrowth = XpGrowth,
            XpPerKill = XpPerKill,
            XpShareRadius = XpShareRadius,      // Story 3.13
            HealthPerLevel = HealthPerLevel,    // Story 3.13
            DamagePerLevel = DamagePerLevel,    // Story 3.13
            ArmorPerLevel = ArmorPerLevel,      // Story 3.13
            SignatureAbility = SignatureAbility,
            UltimateAbility = UltimateAbility,
        };
    }
}
