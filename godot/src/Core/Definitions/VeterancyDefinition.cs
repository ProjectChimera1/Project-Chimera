#nullable enable
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Story 15-24d — one VETERANCY RANK: the kill count that unlocks it, plus the <b>cumulative</b> stat vector a
    /// unit carries once it has reached that rank. Cumulative, never incremental: rank 2's <c>stat_deltas</c> is the
    /// TOTAL a rank-2 unit gets, not an increment on top of rank 1's. That is what lets the runtime hold ONE
    /// swap-on-change modifier slot (the <c>HeroXpSystem.ReconcileThresholds</c> / <c>ResearchSystem</c> cumulative
    /// pattern) instead of one modifier per rank, which would exhaust the 8-slot ring.
    ///
    /// <para><b>Lenient-path POCO (the <see cref="HeroDefinition"/> shape).</b> This rides ONLY the lenient faction
    /// loader (never the strict ability path), so: no enums, no <see cref="Fixed"/>, plain <c>float</c> authoring
    /// numbers, settable auto-props. The single float→<see cref="Fixed"/> quantize happens at the runtime's build
    /// boundary (<c>VeterancySystem.BuildRankVector</c>), never in-tick per delta.</para>
    ///
    /// <para><b>Closed vocabulary.</b> Every <see cref="StatDeltas"/> key must resolve in
    /// <see cref="ProjectChimera.Core.Stats.StatVocabulary"/> AND be
    /// <c>ModifierAuthorable</c>; <see cref="UnitDefinitionValidator"/> fail-closes on anything else. There is no
    /// reflection and no open string channel.</para>
    /// </summary>
    public sealed class VeterancyRank
    {
        /// <summary>Cumulative hostile kills required to hold this rank. Validated <c>&gt;= 1</c> and STRICTLY
        /// ascending across <see cref="VeterancyDefinition.Ranks"/> (a flat or descending ladder is unorderable, so
        /// the unit is rejected rather than silently resolved by list position).</summary>
        [JsonPropertyName("kills")]
        public int Kills { get; set; }

        /// <summary>The CUMULATIVE stat vector at this rank, keyed by <c>StatDefinition.JsonName</c>
        /// (<c>"max_health"</c>, <c>"attack_damage"</c>, <c>"attack_speed"</c>, …). Null/empty is legal and installs
        /// nothing (the DW-678 rule — never burn a ring slot on an inert modifier).</summary>
        [JsonPropertyName("stat_deltas")]
        public Dictionary<string, float>? StatDeltas { get; set; }

        /// <summary>A deep copy — the dictionary must NOT alias, so a clone and its source validate/edit
        /// independently (the <see cref="HeroAttributesDefinition"/> rule).</summary>
        public VeterancyRank Clone() => new VeterancyRank
        {
            Kills = Kills,
            StatDeltas = StatDeltas == null ? null : new Dictionary<string, float>(StatDeltas),
        };
    }

    /// <summary>
    /// Story 15-24d — the authored VETERANCY block on a <see cref="UnitDefinition"/> (JSON <c>veterancy</c>): the
    /// creator's opt-in that turns a plain unit into one that grows from kills. Null on every unit that does not
    /// opt in — which is every shipped unit today, and that is load-bearing: the opt-in gates the kill-count
    /// INCREMENT itself (<c>DamageResolver.KillEntity</c>), so a non-opted unit's <c>EntityWorld.VeterancyKills</c>
    /// stays 0 across every recorded scenario, the bounded <c>SimChecksum</c> v28 arm folds zero <c>Mix</c> calls,
    /// and no golden moves.
    ///
    /// <para><b>Determinism.</b> Godot-free (<c>src/Core/Definitions</c>), plain <c>int</c>/<c>float</c> authoring
    /// numbers quantized once at the runtime build boundary. Folded into <see cref="ContentHash"/> (v5) behind a
    /// presence bit with ordinal-sorted delta keys, so a peer with a divergent ladder is rejected at the lobby
    /// rather than desyncing at the first rank-up.</para>
    ///
    /// <para><b>Out of scope here (leg f):</b> the authoring UI. This block is written/read by
    /// <see cref="FactionWriter"/> and gated by <see cref="UnitDefinitionValidator"/>; no editor panel surfaces it
    /// yet.</para>
    /// </summary>
    public sealed class VeterancyDefinition
    {
        /// <summary>The rank ladder in ASCENDING <see cref="VeterancyRank.Kills"/> order. Validated non-empty,
        /// bounded in count, and strictly ascending; the runtime picks the HIGHEST rank whose threshold the unit's
        /// kill count has reached (kills past the last threshold keep counting but change nothing).</summary>
        [JsonPropertyName("ranks")]
        public List<VeterancyRank>? Ranks { get; set; }

        /// <summary>A deep copy (each rank cloned) so a duplicated unit's ladder validates/edits independently.</summary>
        public VeterancyDefinition Clone()
        {
            var copy = new VeterancyDefinition();
            if (Ranks != null)
            {
                copy.Ranks = new List<VeterancyRank>(Ranks.Count);
                for (int i = 0; i < Ranks.Count; i++) copy.Ranks.Add(Ranks[i]?.Clone()!);
            }
            return copy;
        }
    }
}
