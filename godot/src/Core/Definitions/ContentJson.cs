#nullable enable
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// The single declaration site for every content <see cref="JsonSerializerOptions"/> in the project (Story 2.3,
    /// AC6 / AR-22 — the architecture's <c>ContentJson</c> single-choke-point). Four postures share ONE base:
    ///
    ///   • <see cref="Options"/>        — STRICT: abilities / items / LLM ability drafts.
    ///   • <see cref="ScenarioOptions"/> — the scenario file format (<see cref="ScenarioSerializer"/>).
    ///   • <see cref="LenientOptions"/>  — the faction/unit loader (<see cref="FactionDefinition.JsonOptions"/>).
    ///   • <see cref="ModelOutputOptions"/> — untrusted LLM output (<c>ProjectChimera.AI.LLMService</c>), DW-526.
    ///
    /// <para>Two more postures live OUTSIDE this file because they are the strict posture PLUS a write/graph delta,
    /// and both DERIVE from <see cref="NewStrict"/> rather than hand-copying its converter list (DW-524):
    /// <see cref="ItemWriter.Options"/> (strict + indentation + omit-defaults) and <c>ProjectChimera.Dsl.DslJson.Options</c>
    /// (strict + indentation + the trigger-IR node/edge converters). A converter added to <see cref="BuildStrict"/>
    /// therefore reaches every one of them.</para>
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

        /// <summary>
        /// DW-524 — a FRESH, still-mutable copy of the shared authoring <see cref="Base"/> posture, for a content
        /// posture that must live in its own file (a writer, or one carrying a foreign converter set). The caller
        /// OWNS the returned object: mutate it, then publish it as a <c>static readonly</c>. Never returns a shared
        /// instance — a <see cref="JsonSerializerOptions"/> becomes read-only on first use.
        /// </summary>
        public static JsonSerializerOptions NewBase() => Base();

        /// <summary>
        /// DW-524 — a FRESH, still-mutable copy of the STRICT posture (<see cref="Options"/>'s exact settings and
        /// converter ORDER: enum → Fixed → effect node, plus <c>Disallow</c>). This is the derivation seam that
        /// keeps <see cref="ItemWriter.Options"/> and <c>DslJson.Options</c> from hand-copying the converter list:
        /// a converter added in <see cref="BuildStrict"/> reaches them the next time they build. The caller OWNS the
        /// returned object (see <see cref="NewBase"/>).
        /// </summary>
        public static JsonSerializerOptions NewStrict() => BuildStrict();

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

        /// <summary>
        /// DW-526 — the sole options object for parsing UNTRUSTED MODEL OUTPUT (<c>LLMService.Validate</c>,
        /// <c>ValidateScenario</c>, <c>ValidateBalanceReport</c>). Before it, each of those three sites built its own
        /// <see cref="JsonSerializerOptions"/> PER CALL, off this choke point, with a different converter set — and the
        /// balance-report site registered NO <see cref="FixedJsonConverter"/> at all, so that path could not read a
        /// <see cref="ProjectChimera.Core.Fixed"/>-typed field the way the rest of the pipeline does. LLM output is the
        /// least trustworthy input in the project (it lands in draft units/abilities/heroes/factions, generated maps and
        /// balance reports), so it is the LAST place a hand-rolled option set belongs.
        ///
        /// <para>Derived from <see cref="NewStrict"/> — same converters, same order — with exactly two documented
        /// deltas, each load-bearing:</para>
        ///   • <c>PropertyNameCaseInsensitive</c> — a model is not a JSON serializer; it capitalizes at random. This
        ///     widens NAME matching only, never a value.
        ///   • NO <c>UnmappedMemberHandling.Disallow</c> (explicitly <c>Skip</c>) — the draft-parse posture DW-526 asks
        ///     for is unmapped-TOLERANT, matching <see cref="ScenarioOptions"/> rather than <see cref="Options"/>: the
        ///     generated artifact is a scenario/report that the (forward-compat, non-Disallow) loaders must be able to
        ///     read back, so rejecting a stray key here would make generation stricter than the format it targets and
        ///     throw away a whole paid generation over a key the load path ignores. Every CONTENT decision is still made
        ///     by the validators' located passes downstream — leniency here widens the SYNTAX accepted, never the values
        ///     trusted. (Ability drafts are deliberately NOT on this posture: they parse through <see cref="Options"/>,
        ///     because an ability is authored against a closed POCO where a typo'd field IS the error to report.)
        ///
        /// <para>Everything fail-closed about the strict posture survives: enums by NAME only (a numeric enum is
        /// rejected, no silent miscode), the <see cref="FixedJsonConverter"/> quantization boundary (NaN/±Inf/over-range
        /// rejected before any generated number can reach the sim), comment/trailing-comma tolerance from
        /// <see cref="Base"/> (a model's two most common syntax deviations), and the closed-registry effect converter.
        /// Deliberately NO <see cref="WidgetBaseJsonConverter"/>: no LLM prompt authors a custom-UI tree and no
        /// validator pass gates one, so a model-emitted <c>custom_ui</c> stays a hard parse reject rather than an
        /// ungated widget graph.</para>
        /// </summary>
        public static readonly JsonSerializerOptions ModelOutputOptions = BuildModelOutput();

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

        /// <summary>Build <see cref="ModelOutputOptions"/> (DW-526): the STRICT posture verbatim — same converters, same
        /// order — plus case-insensitive property matching, minus <c>Disallow</c>. See that field's docs for why each
        /// delta is there.</summary>
        private static JsonSerializerOptions BuildModelOutput()
        {
            JsonSerializerOptions o = BuildStrict();
            o.PropertyNameCaseInsensitive = true;
            // Explicit, not inherited-by-omission: the unmapped-tolerant half of the draft posture is a DECISION.
            o.UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip;
            return o;
        }
    }
}
