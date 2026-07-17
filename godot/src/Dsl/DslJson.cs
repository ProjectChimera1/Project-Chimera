#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectChimera.Core.Definitions;   // FixedJsonConverter, EffectNodeJsonConverter

namespace ProjectChimera.Dsl
{
    /// <summary>
    /// Story 7.2 — the single canonical <see cref="JsonSerializerOptions"/> for the graph-canonical trigger IR,
    /// mirroring <c>ContentJson.Options</c> (the effect-graph choke point) so the determinism + fail-closed posture
    /// is structural for graph JSON:
    ///
    ///   • <see cref="FixedJsonConverter"/> — the ONE quantization boundary (every gameplay number → 16.16 Fixed at
    ///     parse; NaN/±Inf/over-range rejected).
    ///   • <see cref="EffectNodeJsonConverter"/> — reused VERBATIM so a <c>run_effect</c> node's embedded
    ///     <c>EffectNode</c> subgraph (de)serializes through the existing closed-registry effect converter (no second
    ///     effect system).
    ///   • <see cref="NodeBaseJsonConverter"/> — the closed-registry <c>kind</c> dispatch for the IR's own nodes;
    ///     fails closed on unknown kind / stray / duplicate / missing property.
    ///   • <see cref="JsonStringEnumConverter"/> (<c>allowIntegerValues:false</c>) — enums (e.g. the data-edge wire
    ///     type) by NAME only; a numeric value fails closed.
    ///   • <see cref="JsonUnmappedMemberHandling.Disallow"/> — an unknown top-level POCO field is a hard error (the
    ///     custom converters guard the members INSIDE a node object themselves — Disallow does not reach into them).
    ///   • <see cref="JsonSerializerOptions.WriteIndented"/> — human-readable graph JSON (authoring surface).
    ///
    /// <c>[JsonPolymorphic]</c>/<c>[JsonDerivedType]</c> are deliberately NOT used (forbidden project-wide, AR-22).
    /// </summary>
    public static class DslJson
    {
        /// <summary>The sole options object for graph IR (de)serialization. Static readonly — one shared, thread-safe instance.</summary>
        public static readonly JsonSerializerOptions Options = new()
        {
            ReadCommentHandling    = JsonCommentHandling.Skip,
            AllowTrailingCommas    = true,
            WriteIndented          = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters =
            {
                new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false),
                new FixedJsonConverter(),
                new EffectNodeJsonConverter(),
                new NodeBaseJsonConverter(),
                // Story 7.7 — a data edge MISSING its 'wire' is now a located parse reject (it silently defaulted
                // to Boolean through the [JsonConstructor] before); Write emits the identical byte layout.
                new DataEdgeJsonConverter(),
            },
        };
    }
}
