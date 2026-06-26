#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The single canonical <see cref="JsonSerializerOptions"/> for ability content (Story 2.3, AC6 / AR-22) — the
    /// architecture's <c>ContentJson.Options</c> single-choke-point seed. Every ability (de)serialization MUST go
    /// through <see cref="Options"/> so the determinism + fail-closed posture is structural:
    ///
    ///   • <see cref="FixedJsonConverter"/> — the ONE quantization boundary: every gameplay number becomes a 16.16
    ///     <see cref="ProjectChimera.Core.Fixed"/> at parse, rejecting NaN/±Inf/over-range (no float reaches the sim).
    ///   • <see cref="EffectNodeJsonConverter"/> — the closed-registry <c>kind</c> dispatch that builds the runtime
    ///     effect graph and fails closed on an unknown kind / missing field / stray property / over-deep nesting.
    ///   • <see cref="JsonStringEnumConverter"/> with <c>allowIntegerValues: false</c> — enums by NAME only
    ///     (<c>"Magic"</c>, not <c>3</c>); a numeric enum value is rejected (fail-closed, no silent miscode).
    ///   • <see cref="JsonUnmappedMemberHandling.Disallow"/> — an unknown TOP-LEVEL field on the POCO is a hard
    ///     error (an authoring/LLM typo can never be silently dropped). NOTE: this governs only the reflection
    ///     (POCO) layer; unknown properties INSIDE an effect-node object are rejected by
    ///     <see cref="EffectNodeJsonConverter"/> itself (a custom converter is outside Disallow's reach).
    ///
    /// <c>[JsonPolymorphic]</c>/<c>[JsonDerivedType]</c> are deliberately NOT used (forbidden project-wide, AR-22:
    /// they are incompatible with <c>UnmappedMemberHandling.Disallow</c> and throw at runtime on the first node).
    ///
    /// SCOPE: abilities only. <see cref="ScenarioSerializer"/> / <see cref="FactionDefinition"/> are deliberately NOT
    /// migrated to this object (the D3 single-choke-point consolidation is deferred; Story 1.7 explicitly fenced
    /// "do not unify JsonSerializerOptions").
    /// </summary>
    public static class ContentJson
    {
        /// <summary>The sole options object for ability (de)serialization. Static readonly — one shared, thread-safe instance.</summary>
        public static readonly JsonSerializerOptions Options = new()
        {
            ReadCommentHandling    = JsonCommentHandling.Skip,
            AllowTrailingCommas    = true,
            // Fail-closed: an unknown top-level field on AbilityDefinition (e.g. a typo'd "cooldwn") is rejected,
            // never silently ignored. (Effect-node objects are guarded inside EffectNodeJsonConverter — see above.)
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters =
            {
                // allowIntegerValues:false → enums are authored by NAME only; a numeric enum value fails closed.
                new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false),
                new FixedJsonConverter(),
                new EffectNodeJsonConverter(),
            },
        };
    }
}
