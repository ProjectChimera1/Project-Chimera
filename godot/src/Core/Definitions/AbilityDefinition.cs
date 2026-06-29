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
