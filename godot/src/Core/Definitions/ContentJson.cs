#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The single declaration site for every content <see cref="JsonSerializerOptions"/> in the project (Story 2.3,
    /// AC6 / AR-22 — the architecture's <c>ContentJson</c> single-choke-point). Three postures share ONE base:
    ///
    ///   • <see cref="Options"/>        — STRICT: abilities / items / LLM ability drafts.
    ///   • <see cref="ScenarioOptions"/> — the scenario file format (<see cref="ScenarioSerializer"/>).
    ///   • <see cref="LenientOptions"/>  — the faction/unit loader (<see cref="FactionDefinition.JsonOptions"/>).
    ///
    /// <para>
    /// DW-274 (Story 15.6): <see cref="ScenarioSerializer"/> and <see cref="FactionDefinition"/> used to declare
    /// their own PRIVATE option objects while this class's posture spread to items / the trigger IR
    /// (<c>ProjectChimera.Dsl.DslJson</c>) / LLM drafts. Two hand-maintained replicas of a determinism-critical
    /// setting is a drift surface — a converter added here reached neither loader. Both now derive from
    /// <see cref="Base"/> below, so the shared half can never drift again and each posture's DELTA from the
    /// canonical one is visible in a single file. (The remaining private sets — <c>ContentPackager</c>'s pack
    /// manifest, <c>PlayerProfile</c>/<c>SettingsData</c>, <c>DamageTable</c> — are non-content formats, out of
    /// this choke point's scope.)
    /// </para>
    ///
    /// <para><b>The shared base</b> (<see cref="Base"/>) — authoring ergonomics every content file gets:
    /// <c>ReadCommentHandling.Skip</c> + <c>AllowTrailingCommas</c>. Nothing determinism-bearing lives here; each
    /// posture opts into its own converters/strictness below.</para>
    ///
    /// <para><b><see cref="Options"/> — the strict ability/item posture:</b>
    ///   • <see cref="FixedJsonConverter"/> — the ONE quantization boundary: every gameplay number becomes a 16.16
    ///     <see cref="ProjectChimera.Core.Fixed"/> at parse, rejecting NaN/±Inf/over-range (no float reaches the sim).
    ///   • <see cref="EffectNodeJsonConverter"/> — the closed-registry <c>kind</c> dispatch that builds the runtime
    ///     effect graph and fails closed on an unknown kind / missing field / stray property / over-deep nesting.
    ///   • <see cref="JsonStringEnumConverter"/> with <c>allowIntegerValues: false</c> — enums by NAME only
    ///     (<c>"Magic"</c>, not <c>3</c>); a numeric enum value is rejected (fail-closed, no silent miscode).
    ///   • <see cref="JsonUnmappedMemberHandling.Disallow"/> — an unknown TOP-LEVEL field on the POCO is a hard
    ///     error (an authoring/LLM typo can never be silently dropped). NOTE: this governs only the reflection
    ///     (POCO) layer; unknown properties INSIDE an effect-node object are rejected by
    ///     <see cref="EffectNodeJsonConverter"/> itself (a custom converter is outside Disallow's reach).</para>
    ///
    /// <c>[JsonPolymorphic]</c>/<c>[JsonDerivedType]</c> are deliberately NOT used (forbidden project-wide, AR-22:
    /// they are incompatible with <c>UnmappedMemberHandling.Disallow</c> and throw at runtime on the first node).
    /// </summary>
    public static class ContentJson
    {
        /// <summary>
        /// The posture EVERY content option set below shares: comments skipped and trailing commas tolerated, so a
        /// hand-authored file can carry notes and a trailing comma in ANY content format. A fresh instance per call —
        /// a <see cref="JsonSerializerOptions"/> becomes read-only on first use, so each posture must own its object.
        /// </summary>
        private static JsonSerializerOptions Base() => new()
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        /// <summary>The sole options object for ability / item (de)serialization — the STRICT posture (Disallow +
        /// Fixed + effect graph + name-only enums). Static readonly — one shared, thread-safe instance.</summary>
        public static readonly JsonSerializerOptions Options = BuildStrict();

        /// <summary>
        /// The sole options object for the scenario file format — read (<see cref="ScenarioSerializer.LoadFromFile"/>)
        /// AND write (<see cref="ScenarioSerializer.Serialize"/>). Deltas from <see cref="Options"/>, each load-bearing:
        ///   • <c>WriteIndented</c> — a scenario is a human-editable authoring artifact, and its INDENTED bytes are
        ///     what every scenario golden hash (Story 1.11 AC4, <c>CanonicalScenarioTests</c>) is pinned on. Never
        ///     point the serializer at <see cref="Options"/> instead: that would minify the output and move goldens.
        ///   • <see cref="WidgetBaseJsonConverter"/> instead of <see cref="EffectNodeJsonConverter"/> — the scenario
        ///     tree carries a custom-UI widget graph (Story 7.8), not an effect graph (its trigger IR travels as an
        ///     opaque <c>trigger_graph_json</c> string, parsed by <c>DslJson</c>).
        ///   • NO <c>UnmappedMemberHandling.Disallow</c> — an unknown top-level scenario key must stay a forward-compat
        ///     no-op (a v1 build opening a map saved by a newer build rejects on the <c>schema_version</c> stamp with a
        ///     located message, not on a stray key), so the strictness lives in <see cref="ScenarioValidator"/>.
        /// Enums are name-only here exactly as in <see cref="Options"/>: every shipped map authors
        /// <c>"win_condition": "DestroyAllBuildings"</c>, and a hand-edited numeric enum now fails closed at parse
        /// instead of silently resolving to whichever member happens to hold that ordinal (DW-274).
        /// </summary>
        public static readonly JsonSerializerOptions ScenarioOptions = BuildScenario();

        /// <summary>
        /// The sole options object for the faction/unit content loader (aliased as
        /// <see cref="FactionDefinition.JsonOptions"/>) — the <see cref="Base"/> posture and nothing else.
        /// Deliberately LENIENT, and that is a contract, not an omission:
        ///   • NO <c>Disallow</c> — a faction file legitimately carries keys a given build does not model yet
        ///     (descriptor fields land content-first, story-by-story), and the same <c>UnitDefinition</c>/
        ///     <c>BuildingDefinition</c> POCOs are edited mid-authoring by the card panels. Faction strictness lives
        ///     in <see cref="FactionValidator"/>, which reports EVERY located error at once.
        ///   • NO converters — <c>UnitDefinition</c> deliberately stores plain <c>float</c> and plain <c>string</c>
        ///     categories (quantized to <see cref="ProjectChimera.Core.Fixed"/> downstream by
        ///     <c>EntityWorld.ApplyUnitDefinition</c>, parsed to enums by its computed <c>Parsed*</c> props), so a
        ///     <see cref="FixedJsonConverter"/>/enum converter here would have nothing to bind and adding one would
        ///     break the dual-path DTO constraint (a POCO read by BOTH this loader and the strict ability loader).
        /// </summary>
        public static readonly JsonSerializerOptions LenientOptions = Base();

        /// <summary>Build <see cref="Options"/>: the shared base plus the strict ability/item posture. Converter ORDER
        /// is significant (first match wins) and is preserved verbatim from the pre-DW-274 declaration.</summary>
        private static JsonSerializerOptions BuildStrict()
        {
            JsonSerializerOptions o = Base();
            // Fail-closed: an unknown top-level field on AbilityDefinition (e.g. a typo'd "cooldwn") is rejected,
            // never silently ignored. (Effect-node objects are guarded inside EffectNodeJsonConverter — see above.)
            o.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            // allowIntegerValues:false → enums are authored by NAME only; a numeric enum value fails closed.
            o.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
            o.Converters.Add(new FixedJsonConverter());
            o.Converters.Add(new EffectNodeJsonConverter());
            return o;
        }

        /// <summary>Build <see cref="ScenarioOptions"/>: the shared base plus indentation and the scenario converter
        /// set. Converter ORDER is preserved verbatim from <see cref="ScenarioSerializer"/>'s pre-DW-274 private
        /// declaration (enum → Fixed → widget), so the emitted bytes — and every golden pinned on them — are
        /// unchanged for every enum value the enums actually define.</summary>
        private static JsonSerializerOptions BuildScenario()
        {
            JsonSerializerOptions o = Base();
            o.WriteIndented = true;
            o.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
            o.Converters.Add(new FixedJsonConverter());
            o.Converters.Add(new WidgetBaseJsonConverter());
            return o;
        }
    }
}
