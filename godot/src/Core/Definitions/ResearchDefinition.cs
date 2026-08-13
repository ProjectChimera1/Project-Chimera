#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// A faction-wide, timed, repeatable (leveled) research/upgrade authored as data (Story 4.8) — the content
    /// model half of the epic's research system. Declared on <see cref="FactionDefinition.Research"/>, mirroring
    /// <see cref="BuildingDefinition"/>'s place on <see cref="FactionDefinition.Buildings"/>. Import-time gated by
    /// <see cref="ResearchValidator"/> (wired additively into <see cref="FactionDefinition.LoadFromFile"/>, the
    /// same throw-based aggregate-error gate <see cref="TechTreeValidator"/>/<see cref="ResourceCostValidator"/>
    /// already use — NOT a <see cref="Validated{T}"/> mint; see the spec's Design Notes).
    ///
    /// <para>Deliberately content-only: no runtime order path (<c>ResearchSystem</c> — Story 4.9), no
    /// <c>SimChecksum</c> fold (Story 4.10), no command-card UI (Story 4.11). Numeric fields stay authoring-time
    /// <c>float</c>/<c>int</c> — no <see cref="Fixed"/> conversion happens in this story; that single load-boundary
    /// quantization is 4.9's job (mirrors <see cref="UnitDefinition"/>'s own float-authoring/Fixed-at-spawn split).</para>
    /// </summary>
    public class ResearchDefinition
    {
        /// <summary>Stable id referenced by other research's <see cref="Prerequisites"/> and by
        /// <see cref="BuildingDefinition.AvailableResearch"/>.</summary>
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = "";

        /// <summary>
        /// Fraction of the current level's authored <see cref="ResearchLevel.Cost"/> refunded when an in-progress
        /// order is cancelled (4.9: refund = <c>CancelRefundFraction × currentLevelCost</c>). Definition-level
        /// (not per-level) since only one level can ever be in progress at a time. Must lie within <c>[0, 1]</c> —
        /// enforced by <see cref="ResearchValidator"/>.
        /// </summary>
        [JsonPropertyName("cancel_refund_fraction")]
        public float CancelRefundFraction { get; set; } = 0f;

        /// <summary>
        /// Ids (of buildings OR other research entries) that must be satisfied before this research can be
        /// started. Mirrors <see cref="UnitDefinition.Prerequisites"/>'s shape exactly, but resolves against the
        /// UNION of building ids and research ids (a research can gate on a building OR on a prior research level
        /// having completed). Empty array = no prerequisites; a null array (malformed JSON <c>"prerequisites":
        /// null</c>) is treated as empty everywhere it is read — never an NRE (mirrors
        /// <see cref="TechTreeValidator"/>'s <c>?? Array.Empty&lt;string&gt;()</c> idiom).
        /// </summary>
        [JsonPropertyName("prerequisites")]
        public string[] Prerequisites { get; set; } = Array.Empty<string>();

        /// <summary>
        /// The repeatable level ladder — each entry is one purchasable step, carrying its own
        /// <see cref="ResearchLevel.Cost"/>/<see cref="ResearchLevel.TimeTicks"/>/<see cref="ResearchLevel.ModifierDelta"/>
        /// (4.9 applies levels in order; "the next level's cost/time apply" after each completion). Must declare
        /// at least one level — an empty (or omitted/null) ladder is a located <see cref="ResearchValidator"/>
        /// error. A null list (malformed JSON <c>"levels": null</c>) is treated as empty, never an NRE.
        /// </summary>
        [JsonPropertyName("levels")]
        public List<ResearchLevel> Levels { get; set; } = new();
    }

    /// <summary>
    /// One purchasable step of a <see cref="ResearchDefinition"/>'s repeatable ladder (Story 4.8). Pure authoring
    /// data — no runtime behaviour lives here; 4.9's order path reads these fields to charge/time the order and
    /// applies <see cref="ModifierDelta"/> as a permanent faction-scoped stat delta on completion.
    /// </summary>
    public class ResearchLevel
    {
        /// <summary>
        /// The sparse resource cost map for this level, mirroring <see cref="UnitDefinition.Cost"/>'s shape and
        /// semantics: keys are resource ids (today only those in <see cref="ResourceCostValidator.KnownResourceIds"/>
        /// have runtime backing), values are amounts. Null (unauthored) means "free" for THIS field — unlike
        /// <see cref="UnitDefinition.Cost"/>, research has no legacy <c>cost_ore</c>/<c>cost_crystal</c> fallback to
        /// derive from, so a null/omitted map is simply an empty cost, not an error.
        /// </summary>
        [JsonPropertyName("cost")]
        public Dictionary<string, int>? Cost { get; set; }

        /// <summary>Duration of this level's research order in sim ticks. Must be positive — a located
        /// <see cref="ResearchValidator"/> error otherwise (a non-positive/instant/negative research order has no
        /// meaningful progress semantics for 4.9's order path).</summary>
        [JsonPropertyName("time_ticks")]
        public int TimeTicks { get; set; }

        /// <summary>
        /// The permanent stat delta this level applies to every current AND future faction unit once completed
        /// (4.9, via the existing Epic 2 modifier pipeline). Null = a level with no stat effect (e.g. a
        /// cost/time-only gate, or a level that only unlocks something else). Optional — omitting it is not an
        /// error.
        /// </summary>
        [JsonPropertyName("modifier_delta")]
        public ResearchModifierDelta? ModifierDelta { get; set; }
    }

    /// <summary>
    /// The authoring-time stat delta one <see cref="ResearchLevel"/> applies (Story 4.8) — mirrors
    /// <see cref="ProjectChimera.Effects.Modifier"/>'s four additive <see cref="Fixed"/> stat fields
    /// (<c>MaxHealthDelta</c>/<c>AttackDamageDelta</c>/<c>MoveSpeedDelta</c>/<c>ArmorDelta</c>) as authoring-time
    /// <c>float</c>s — the single <see cref="Fixed"/> quantization boundary is 4.9's, not this content model's.
    /// A repeatable research keeps ONE cumulative modifier slot per <see cref="ResearchDefinition"/> (the sum of
    /// every completed level's delta), never one slot per level (4.9's job; not modeled here).
    /// </summary>
    public class ResearchModifierDelta
    {
        /// <summary>Flat max-health delta this level adds, mirroring <see cref="ProjectChimera.Effects.Modifier.MaxHealthDelta"/>.</summary>
        [JsonPropertyName("max_health_delta")]
        public float MaxHealthDelta { get; set; } = 0f;

        /// <summary>Flat attack-damage delta this level adds, mirroring <see cref="ProjectChimera.Effects.Modifier.AttackDamageDelta"/>.</summary>
        [JsonPropertyName("attack_damage_delta")]
        public float AttackDamageDelta { get; set; } = 0f;

        /// <summary>Flat move-speed delta this level adds, mirroring <see cref="ProjectChimera.Effects.Modifier.MoveSpeedDelta"/>.</summary>
        [JsonPropertyName("move_speed_delta")]
        public float MoveSpeedDelta { get; set; } = 0f;

        /// <summary>Flat armor delta this level adds, mirroring <see cref="ProjectChimera.Effects.Modifier.ArmorDelta"/>.</summary>
        [JsonPropertyName("armor_delta")]
        public float ArmorDelta { get; set; } = 0f;

        /// <summary>
        /// Story 15-24a — the SPARSE authoring lane for every non-legacy stat this level adds:
        /// <c>"stat_deltas": { "attack_speed": 0.05 }</c>. Keys are StatVocabulary registry JsonNames
        /// (<c>ResearchValidator</c> fail-closes unknown names, legacy-stat names — those author through their
        /// flat keys above — and non-modifier-authorable stats); values are authoring-time floats exactly like
        /// the four flat props (dual-path DTO constraint: floats/dicts only, quantization stays 4.9's single
        /// boundary at completion-time accumulate). Null/empty = no extra stats; <c>FactionWriter.PutLevels</c>
        /// omits the key, so pre-15-24a faction JSON round-trips byte-stable.
        /// </summary>
        [JsonPropertyName("stat_deltas")]
        public System.Collections.Generic.Dictionary<string, float>? StatDeltas { get; set; }

        /// <summary>
        /// Story 15-24a — this level's grant as the CANONICAL sparse vector (legacy four + the
        /// <see cref="StatDeltas"/> lane, quantized float→Fixed HERE — the fold/accumulate boundary, mirroring
        /// 4.9's completion-time quantize; sorted ascending StatId, zero entries dropped, unknown names skipped —
        /// <c>ResearchValidator</c> is the fail-closed gate). Consumed by <c>ContentHash.FoldResearch</c> and
        /// <c>ResearchSystem</c>'s completion accumulate, so hash and behavior can never disagree.
        /// </summary>
        public ProjectChimera.Core.Stats.StatDelta[] BuildStatDeltaVector()
        {
            var scratch = new System.Collections.Generic.List<ProjectChimera.Core.Stats.StatDelta>(4 + (StatDeltas?.Count ?? 0))
            {
                new ProjectChimera.Core.Stats.StatDelta(ProjectChimera.Core.Stats.StatId.MaxHealth, Fixed.FromFloat(MaxHealthDelta)),
                new ProjectChimera.Core.Stats.StatDelta(ProjectChimera.Core.Stats.StatId.AttackDamage, Fixed.FromFloat(AttackDamageDelta)),
                new ProjectChimera.Core.Stats.StatDelta(ProjectChimera.Core.Stats.StatId.Armor, Fixed.FromFloat(ArmorDelta)),
                new ProjectChimera.Core.Stats.StatDelta(ProjectChimera.Core.Stats.StatId.MoveSpeed, Fixed.FromFloat(MoveSpeedDelta)),
            };
            if (StatDeltas != null && StatDeltas.Count > 0)
            {
                var keys = new System.Collections.Generic.List<string>(StatDeltas.Keys);
                keys.Sort(System.StringComparer.Ordinal); // deterministic — a Dictionary walk is never a hash/behavior input
                foreach (string key in keys)
                    if (ProjectChimera.Core.Stats.StatVocabulary.TryByJsonName(key, out var def))
                        scratch.Add(new ProjectChimera.Core.Stats.StatDelta(def.Id, Fixed.FromFloat(StatDeltas[key])));
            }
            return ProjectChimera.Core.Stats.StatVocabulary.Canonicalize(scratch);
        }
    }
}
