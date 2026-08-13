#nullable enable
using System.Collections.Generic;
using System.Text.Json.Serialization;
using ProjectChimera.Core.Stats;  // Story 15-24a — StatDelta / StatVocabulary (the sparse authoring lane)
using ProjectChimera.Effects;     // EffectNode (the consumable effect-graph root)

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

        /// <summary>
        /// Story 15-24a — the SPARSE authoring lane for every non-legacy stat the carried modifier grants:
        /// <c>"stat_deltas": { "attack_speed": 0.15, "health_regen": 0.5 }</c>. Keys are registry
        /// <c>JsonName</c>s (validator fail-closes unknown names, duplicates of a legacy key, and
        /// non-modifier-authorable stats); values quantize at parse via <c>FixedJsonConverter</c>. The four
        /// legacy keys above stay the canonical spelling for their stats (authoring both lanes for one stat
        /// is a validation error). Null/empty = no extra stats — pre-15-24a JSON is untouched, and
        /// <c>ItemWriter</c>'s omit-default discipline keeps round-trips byte-stable.
        /// </summary>
        [JsonPropertyName("stat_deltas")]
        public Dictionary<string, Fixed>? StatDeltas { get; set; }

        /// <summary>
        /// Story 15-24a — the CANONICAL sparse vector of everything this item grants (legacy four + the
        /// <see cref="StatDeltas"/> lane, merged/sorted/zero-dropped). The single shape <c>ItemSystem</c>'s
        /// mint, <c>ItemDefinitionValidator.CarriedModifier</c>'s probe, and <c>ContentHash.FoldItems</c> all
        /// consume — so the three can never disagree about what the item does. Allocates; call at load/mint
        /// boundaries, never per tick (mint caches nothing today: pickup/drop frequency is human-scale).
        /// </summary>
        public StatDelta[] BuildStatDeltaVector()
        {
            var scratch = new List<StatDelta>(4 + (StatDeltas?.Count ?? 0))
            {
                new StatDelta(StatId.MaxHealth, MaxHealthDelta),
                new StatDelta(StatId.AttackDamage, AttackDamageDelta),
                new StatDelta(StatId.Armor, ArmorDelta),
                new StatDelta(StatId.MoveSpeed, MoveSpeedDelta),
            };
            AppendNamedDeltas(scratch, StatDeltas);
            return StatVocabulary.Canonicalize(scratch);
        }

        /// <summary>
        /// Story 15-24a shared helper — append a <c>stat_deltas</c> authoring dictionary onto a scratch
        /// vector IN SORTED ORDINAL KEY ORDER (deterministic whatever the dictionary's insertion order; the
        /// FoldAttrValues rule — a Dictionary walk is never a hash/behavior input). Unknown names are
        /// SKIPPED here — the validator is the fail-closed gate; this helper must stay total so a validator
        /// can build the vector to inspect it.
        /// </summary>
        internal static void AppendNamedDeltas(List<StatDelta> scratch, Dictionary<string, Fixed>? named)
        {
            if (named == null || named.Count == 0) return;
            var keys = new List<string>(named.Keys);
            keys.Sort(System.StringComparer.Ordinal);
            foreach (string key in keys)
                if (StatVocabulary.TryByJsonName(key, out var def))
                    scratch.Add(new StatDelta(def.Id, named[key]));
        }

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

        /// <summary>True when any authored stat delta is non-zero (legacy four OR the 15-24a sparse lane) —
        /// i.e. carrying this item grants a modifier. Defined AS the canonical vector's non-emptiness so this
        /// predicate and the mint can never partition differently (the CumulativeCarriesPayload lesson);
        /// allocates a scratch list, so it stays off the per-tick path (pickup/buy/drop are human-scale).</summary>
        [JsonIgnore]
        public bool HasStatModifier => BuildStatDeltaVector().Length != 0;
    }
}
