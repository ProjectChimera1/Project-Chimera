#nullable enable
using System.Text.Json.Serialization;
using ProjectChimera.Effects; // EffectNode (the compiled effect-graph root)

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Data-driven active-ability definition loaded from JSON (Story 2.3, FR-12 / AR-22). One entry per ability;
    /// its <see cref="EffectGraph"/> is the compiled root of the closed 2.1 effect vocabulary, deserialized by
    /// <see cref="EffectNodeJsonConverter"/> directly into the runtime <see cref="EffectNode"/> types (Decision #1
    /// = A: the runtime graph IS the deserialization target — no parallel DTO tree).
    ///
    /// Mirrors <see cref="UnitDefinition"/> (PascalCase auto-props + snake_case <c>[JsonPropertyName]</c>), with one
    /// determinism upgrade: gameplay numbers are <see cref="ProjectChimera.Core.Fixed"/>, quantized once at parse by
    /// <see cref="FixedJsonConverter"/> — never <c>float</c>. Deserialize ONLY through <see cref="ContentJson.Options"/>
    /// and validate through <see cref="AbilityValidator"/> before any use (nothing runnable escapes the gate).
    /// 2.3 owns the model + converter + validator + loader; the runtime cast path (cooldown SoA, cost debit, command
    /// card) lands in Story 2.4.
    /// </summary>
    public class AbilityDefinition
    {
        /// <summary>Stable id used for references, command cards, and located error messages.</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        /// <summary>Human-readable name shown in the UI (presentation only — never a gameplay key).</summary>
        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = "";

        /// <summary>One of <see cref="AbilityTargeting"/> by NAME: None | Self | TargetUnit | GroundPoint.</summary>
        [JsonPropertyName("targeting")]
        public string Targeting { get; set; } = "Self";

        /// <summary>
        /// Story 15.11 (DW-286) — OPTIONAL allegiance hint for the click-picker of a <c>TargetUnit</c> (or
        /// <c>GroundPoint</c>) ability, one of <see cref="TargetAffinity"/> by NAME: Enemy | Ally | Any. It is a HINT
        /// on the existing targeting mode, NOT a 5th <see cref="AbilityTargeting"/> value. <b>Nullable and defaults to
        /// null</b> so an absent value serializes identically to today — every shipped ability's <c>ContentHash</c>/
        /// <c>CanonicalModelHash</c> is unchanged (Block-If) and <see cref="ParsedTargetAffinity"/> reads back as the
        /// historical enemy-only default. Deserializes only through <c>ContentJson.Options</c>
        /// (<c>UnmappedMemberHandling.Disallow</c>); the parse GETTER is <c>[JsonIgnore]</c> (never re-emitted).
        /// </summary>
        [JsonPropertyName("target_affinity")]
        public string? TargetAffinity { get; set; } = null;

        /// <summary>
        /// How this ability ACTIVATES (Story 2.6, FR-9): one of <see cref="PassiveActivation"/> by NAME —
        /// active | aura | on_hit | while_alive. Default <c>"active"</c> so every pre-2.6 ability (and every existing
        /// golden) is unaffected — the field is omittable and an absent value reads back as "active".
        /// </summary>
        [JsonPropertyName("activation")]
        public string Activation { get; set; } = "active";

        /// <summary>Energy pool cost (Fixed; matches the Energy SoA added in 2.2a). 2.4 debits it on cast.</summary>
        [JsonPropertyName("cost_energy")]
        public Fixed CostEnergy { get; set; } = Fixed.Zero;

        /// <summary>Ore cost (mirrors <see cref="UnitDefinition.CostOre"/>; an int resource).</summary>
        [JsonPropertyName("cost_ore")]
        public int CostOre { get; set; } = 0;

        /// <summary>Crystal cost (scarce resource; advanced abilities only).</summary>
        [JsonPropertyName("cost_crystal")]
        public int CostCrystal { get; set; } = 0;

        /// <summary>
        /// Story 2.13 (AC5.2, D-4) — self HP-cost, parallel to <see cref="CostEnergy"/>/<see cref="CostOre"/>/
        /// <see cref="CostCrystal"/> (unfolded authored int). The cast debits it AFTER the effect graph resolves; a
        /// cast whose debit would bring the caster to ≤0 HP is REFUSED unless <see cref="AllowSelfLethal"/>. This is the
        /// LETHAL-capable self-cost; <c>direct_hp_delta</c> stays the non-lethal Story-2.1 primitive. Default 0 ⇒ no cost.
        /// </summary>
        [JsonPropertyName("cost_health")]
        public int CostHealth { get; set; } = 0;

        /// <summary>
        /// Story 2.13 (AC5.1, D-4) — when true, a cast whose <see cref="CostHealth"/> would kill the caster is ALLOWED
        /// (an intentional "suicide-bomber"): the effect graph runs, THEN the caster dies via the existing entity-death
        /// sequence. Default false ⇒ every existing ability is PROTECTED (the min-HP gate refuses a would-be-lethal
        /// self-cast, closing the §2.10 0-HP-alive strand). Unfolded / peer-identical (the CostEnergy/Cooldown posture).
        /// </summary>
        [JsonPropertyName("allow_self_lethal")]
        public bool AllowSelfLethal { get; set; } = false;

        /// <summary>Cooldown in SECONDS (Fixed). Story 2.4 converts seconds→ticks and owns the per-ability cooldown SoA.</summary>
        [JsonPropertyName("cooldown")]
        public Fixed Cooldown { get; set; } = Fixed.Zero;

        /// <summary>
        /// The compiled effect-graph root (the <c>"effect"</c> payload). Deserialized by
        /// <see cref="EffectNodeJsonConverter"/> into the runtime 2.1 <see cref="EffectNode"/> types. Null when the
        /// JSON omits <c>"effect"</c> — <see cref="AbilityValidator"/> rejects that (an ability needs ≥1 effect node).
        /// </summary>
        [JsonPropertyName("effect")]
        public EffectNode? EffectGraph { get; set; }

        /// <summary>
        /// Optional per-ability combat-feedback override (Story 2.7, SD-3/SD-6) — the look/sound/shake/freeze the
        /// cast plays (consumed by Story 2.10). Presentation-domain — EXCLUDED from the determinism hash; the cast
        /// only pushes the reference on an <c>AbilityCast</c> <c>CombatEvent</c>, never reading the values. A declared
        /// flat auto-prop, so it is <c>UnmappedMemberHandling.Disallow</c>-safe and round-trips through
        /// <c>ContentJson.Options</c> in the 2.5a editor (NOT a computed getter — those need <c>[JsonIgnore]</c>).
        /// Every sub-field of the profile is a declared property, so Disallow accepts the nested POCO.
        /// </summary>
        [JsonPropertyName("combat_feedback")]
        public CombatFeedbackProfile? CombatFeedback { get; set; }

        // ── Enum conversion ─────────────────────────────────────────────────────

        /// <summary>
        /// <see cref="Targeting"/> string resolved to the closed <see cref="AbilityTargeting"/> set by EXACT name.
        /// Returns <c>null</c> for an unknown string so <see cref="AbilityValidator"/> can reject it located (NOT
        /// silently default it the way <see cref="UnitDefinition.ParsedCategory"/> defaults to Melee — an ability's
        /// targeting mode changes how it is cast, so an unknown value must fail closed).
        /// </summary>
        // [JsonIgnore]: computed/derived from Targeting. It must NEVER be serialized — the authoring round-trip
        // (Story 2.5a) serializes through ContentJson.Options whose UnmappedMemberHandling.Disallow would hard-reject
        // a re-emitted "ParsedTargeting" member on reload (and it would pollute authored files with a redundant key).
        [JsonIgnore]
        public AbilityTargeting? ParsedTargeting => Targeting switch
        {
            "None"        => AbilityTargeting.None,
            "Self"        => AbilityTargeting.Self,
            "TargetUnit"  => AbilityTargeting.TargetUnit,
            "GroundPoint" => AbilityTargeting.GroundPoint,
            _             => null,
        };

        /// <summary>
        /// Story 15.11 (DW-286) — <see cref="TargetAffinity"/> string resolved to the closed
        /// <see cref="Core.Definitions.TargetAffinity"/> set by EXACT name. Returns <c>null</c> when the string is
        /// ABSENT (the enemy-only default — <see cref="AbilityValidator"/> accepts it) OR UNKNOWN; the validator tells
        /// them apart by checking the RAW string (non-null string + null parse = unparseable → reject), the
        /// <see cref="ParsedTargeting"/> fail-closed posture. Consumers (the click-picker) treat null as
        /// <see cref="Core.Definitions.TargetAffinity.Enemy"/>, so an absent hint behaves exactly as today.
        /// <c>[JsonIgnore]</c> for the same round-trip reason as <see cref="ParsedTargeting"/>.
        /// </summary>
        [JsonIgnore]
        public TargetAffinity? ParsedTargetAffinity => this.TargetAffinity switch
        {
            null    => null, // absent → the picker's historical enemy-only default (never a reject)
            "Enemy" => Core.Definitions.TargetAffinity.Enemy,
            "Ally"  => Core.Definitions.TargetAffinity.Ally,
            "Any"   => Core.Definitions.TargetAffinity.Any,
            _       => null, // unknown non-null string → null; AbilityValidator rejects it (raw non-null + parse null)
        };

        /// <summary>
        /// <see cref="Activation"/> string resolved to the closed <see cref="PassiveActivation"/> set by EXACT name
        /// (snake_case JSON: <c>on_hit</c>/<c>while_alive</c>). Returns <c>null</c> for an unknown string so
        /// <see cref="AbilityValidator"/> rejects it located. <c>[JsonIgnore]</c> for the same round-trip reason as
        /// <see cref="ParsedTargeting"/> (UnmappedMemberHandling.Disallow would reject a re-emitted computed getter).
        /// </summary>
        [JsonIgnore]
        public PassiveActivation? ParsedActivation => Activation switch
        {
            "active"      => PassiveActivation.Active,
            "aura"        => PassiveActivation.Aura,
            "on_hit"      => PassiveActivation.OnHit,
            "while_alive" => PassiveActivation.WhileAlive,
            _             => null,
        };
    }
}
