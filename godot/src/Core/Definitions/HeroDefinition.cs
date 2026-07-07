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

        /// <summary>XP granted per enemy kill credited to this hero. Validated finite &amp; &gt;= 0.</summary>
        [JsonPropertyName("xp_per_kill")]
        public float XpPerKill { get; set; } = 100f;

        /// <summary>The signature ability id (unlocked on level-up by 3.13). Null/empty = not authored yet (valid);
        /// a set-but-undefined ref is rejected by the validator.</summary>
        [JsonPropertyName("signature_ability")]
        public string? SignatureAbility { get; set; }

        /// <summary>The ultimate ability id (unlocked at a high level by 3.13). Null/empty = not authored yet (valid);
        /// a set-but-undefined ref, or one equal to <see cref="SignatureAbility"/>, is rejected by the validator.</summary>
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
            SignatureAbility = SignatureAbility,
            UltimateAbility = UltimateAbility,
        };
    }
}
