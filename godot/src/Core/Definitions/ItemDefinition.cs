#nullable enable
using System.Text.Json.Serialization;
using ProjectChimera.Effects; // EffectNode (the consumable effect-graph root)

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Data-driven item definition loaded from JSON (Story 3.15, FR-64/FR-7a). One entry per item type. Mirrors
    /// <see cref="AbilityDefinition"/> (PascalCase auto-props + snake_case <c>[JsonPropertyName]</c>, <see cref="Fixed"/>
    /// gameplay numbers quantized once at parse by <c>FixedJsonConverter</c> via <c>ContentJson.Options</c> — never
    /// <c>float</c> in a tick). <see cref="Charges"/> selects the effect-graph behaviour, and the four stat deltas are an
    /// INDEPENDENT axis — either archetype may carry them:
    ///   • <b>stat item</b> (<c>charges == 0</c>): applies its <see cref="MaxHealthDelta"/>/<see cref="AttackDamageDelta"/>/
    ///     <see cref="MoveSpeedDelta"/>/<see cref="ArmorDelta"/> as a permanent <c>Modifier</c> while carried (removed on drop);
    ///   • <b>charged consumable</b> (<c>charges &gt; 0</c>): fires its <see cref="EffectGraph"/> through the SAME
    ///     <c>EffectExecutor</c> abilities use, decrements a charge, and is deleted at zero — AND may ALSO carry the four
    ///     stat deltas as a permanent carried modifier (a WC3-style HYBRID buff-consumable, e.g. a potion that buffs while
    ///     held and heals on use). There is no XOR between charges and stat deltas; the carried modifier materializes on
    ///     pickup and is removed when the last charge is consumed (see <c>ItemSystem</c>).
    ///
    /// Deserialize ONLY through <c>ContentJson.Options</c> and validate through <see cref="ItemDefinitionValidator"/>
    /// before any use (nothing runnable escapes the gate).
    /// </summary>
    public class ItemDefinition
    {
        /// <summary>Stable id used for references, scenario placement, and located error messages.</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        /// <summary>Human-readable name shown in the UI (presentation only — never a gameplay key).</summary>
        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = "";

        /// <summary>Optional presentation icon path (presentation only).</summary>
        [JsonPropertyName("icon")]
        public string Icon { get; set; } = "";

        /// <summary>Number of consumable charges. 0 ⇒ a non-consumable STAT item (no effect fires); &gt;0 ⇒ a charged
        /// consumable whose <see cref="EffectGraph"/> fires per use, decrementing this until deletion at zero.</summary>
        [JsonPropertyName("charges")]
        public int Charges { get; set; } = 0;

        /// <summary>Flat max-health modifier granted while carried (reuses the <c>Modifier</c> channel). Default 0.</summary>
        [JsonPropertyName("max_health_delta")]
        public Fixed MaxHealthDelta { get; set; } = Fixed.Zero;

        /// <summary>Flat attack-damage modifier granted while carried. Default 0.</summary>
        [JsonPropertyName("attack_damage_delta")]
        public Fixed AttackDamageDelta { get; set; } = Fixed.Zero;

        /// <summary>Flat move-speed modifier granted while carried. Default 0.</summary>
        [JsonPropertyName("move_speed_delta")]
        public Fixed MoveSpeedDelta { get; set; } = Fixed.Zero;

        /// <summary>Flat armor modifier granted while carried. Default 0.</summary>
        [JsonPropertyName("armor_delta")]
        public Fixed ArmorDelta { get; set; } = Fixed.Zero;

        /// <summary>Optional consumable effect-graph root (the <c>"effect"</c> payload), deserialized by the existing
        /// <c>EffectNodeJsonConverter</c> into the runtime 2.1 <see cref="EffectNode"/> types. Null for a pure stat item.
        /// A charged consumable requires it (the validator rejects <c>charges &gt; 0</c> with no effect).</summary>
        [JsonPropertyName("effect")]
        public EffectNode? EffectGraph { get; set; }

        /// <summary>Optional ore purchase cost (Story 3.16 shops). Present here for the content model but NOT spent
        /// anywhere in Story 3.15. Default 0.</summary>
        [JsonPropertyName("cost_ore")]
        public Fixed CostOre { get; set; } = Fixed.Zero;

        /// <summary>Optional crystal purchase cost (Story 3.16 shops). Unspent in 3.15. Default 0.</summary>
        [JsonPropertyName("cost_crystal")]
        public Fixed CostCrystal { get; set; } = Fixed.Zero;

        /// <summary>True when any of the four stat deltas is non-zero — i.e. carrying this item grants a modifier.</summary>
        [JsonIgnore]
        public bool HasStatModifier =>
            MaxHealthDelta.Raw != 0 || AttackDamageDelta.Raw != 0 || MoveSpeedDelta.Raw != 0 || ArmorDelta.Raw != 0;
    }
}
