#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectChimera.Core.Definitions;   // ContentJson (the choke point), FixedJsonConverter, EffectNodeJsonConverter

namespace ProjectChimera.Dsl
{
    /// <summary>
    /// Story 7.2 — the single canonical <see cref="JsonSerializerOptions"/> for the graph-canonical trigger IR.
    /// DW-524: it no longer MIRRORS <c>ContentJson.Options</c> by hand — it DERIVES from
    /// <see cref="ContentJson.NewStrict"/> and appends the IR's own converters, so the determinism + fail-closed
    /// posture is structural for graph JSON and a converter added at the choke point reaches the trigger IR too:
    ///
    ///   • <see cref="FixedJsonConverter"/> — the ONE quantization boundary (every gameplay number → 16.16 Fixed at
    ///     parse; NaN/±Inf/over-range rejected).                                              [from the strict posture]
    ///   • <see cref="EffectNodeJsonConverter"/> — reused VERBATIM so a <c>run_effect</c> node's embedded
    ///     <c>EffectNode</c> subgraph (de)serializes through the existing closed-registry effect converter (no second
    ///     effect system).                                                                    [from the strict posture]
    ///   • <see cref="JsonStringEnumConverter"/> (<c>allowIntegerValues:false</c>) — enums (e.g. the data-edge wire
    ///     type) by NAME only; a numeric value fails closed.                                  [from the strict posture]
    ///   • <see cref="JsonUnmappedMemberHandling.Disallow"/> — an unknown top-level POCO field is a hard error (the
    ///     custom converters guard the members INSIDE a node object themselves — Disallow does not reach into them).
    ///                                                                                        [from the strict posture]
    ///   • <see cref="NodeBaseJsonConverter"/> — the closed-registry <c>kind</c> dispatch for the IR's own nodes;
    ///     fails closed on unknown kind / stray / duplicate / missing property.                [IR-only, appended]
    ///   • <see cref="JsonSerializerOptions.WriteIndented"/> — human-readable graph JSON (authoring surface).
    ///
    /// <para>Converter ORDER is behavior (first match wins) and is preserved exactly as it was hand-declared:
    /// enum → Fixed → effect node → IR node → data edge → exec edge. The strict posture supplies the first three in
    /// that order; the IR converters are APPENDED, never interleaved, so the emitted graph bytes — and the
    /// CanonicalModelHash pinned on the scenario's <c>trigger_graph_json</c> — are unchanged.</para>
    ///
    /// <c>[JsonPolymorphic]</c>/<c>[JsonDerivedType]</c> are deliberately NOT used (forbidden project-wide, AR-22).
    /// </summary>
    public static class DslJson
    {
        /// <summary>The sole options object for graph IR (de)serialization. Static readonly — one shared, thread-safe instance.</summary>
        public static readonly JsonSerializerOptions Options = BuildGraphOptions();

        /// <summary>The strict content posture (shared, from <see cref="ContentJson.NewStrict"/>) + indentation + the
        /// three trigger-IR converters, appended in their original order.</summary>
        private static JsonSerializerOptions BuildGraphOptions()
        {
            JsonSerializerOptions o = ContentJson.NewStrict();
            o.WriteIndented = true;
            o.Converters.Add(new NodeBaseJsonConverter());
            // Story 7.7 — a data edge MISSING its 'wire' is now a located parse reject (it silently defaulted
            // to Boolean through the [JsonConstructor] before); Write emits the identical byte layout.
            o.Converters.Add(new DataEdgeJsonConverter());
            // DW-357 — the symmetric exec-edge strictness: a hand-authored exec_edge omitting src/dst used to
            // silently default the endpoint to node 0 (which usually exists) and pass GraphStructureGate
            // mis-wired; now every missing/duplicate/unknown key is a located parse reject. Identical Write layout.
            o.Converters.Add(new ExecEdgeJsonConverter());
            return o;
        }
    }
}
