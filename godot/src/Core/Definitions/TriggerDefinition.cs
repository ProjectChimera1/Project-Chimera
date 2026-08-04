#nullable enable
using System;
using System.Text.Json.Serialization;
using ProjectChimera.Core;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// A complete trigger: when any listed event fires and all conditions are met,
    /// all actions execute. Stored in ScenarioData.Triggers[] and evaluated each
    /// simulation tick by ScenarioDirector.
    ///
    /// Authored via the Trigger Editor (ECA sentence builder) or via natural
    /// language → LLM → validated JSON.
    /// </summary>
    public class TriggerDefinition
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "Trigger";

        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;

        /// <summary>After firing once this trigger is permanently disabled.</summary>
        [JsonPropertyName("run_once")]
        public bool RunOnce { get; set; } = false;

        /// <summary>Minimum seconds between consecutive firings. 0 = no cooldown.</summary>
        [JsonPropertyName("cooldown_seconds")]
        public Fixed CooldownSeconds { get; set; } = Fixed.Zero;

        /// <summary>Higher values fire first when multiple triggers match the same tick.</summary>
        [JsonPropertyName("priority")]
        public int Priority { get; set; } = 0;

        [JsonPropertyName("events")]
        public TriggerEvent[] Events { get; set; } = Array.Empty<TriggerEvent>();

        [JsonPropertyName("conditions")]
        public TriggerCondition[] Conditions { get; set; } = Array.Empty<TriggerCondition>();

        [JsonPropertyName("actions")]
        public TriggerAction[] Actions { get; set; } = Array.Empty<TriggerAction>();
    }

    /// <summary>
    /// An event that can cause a trigger to fire.
    ///
    /// Supported types:
    ///   match_start        — fires on the first simulation tick
    ///   unit_dies          — fires when any unit of faction dies (faction: 0=P1, 1=P2)
    ///   building_completed — fires when a building of building_type finishes construction
    ///   timer_expires      — fires when the named timer reaches zero
    ///   resource_threshold — fires when faction ore crosses the threshold (polled each tick)
    ///   unit_count_threshold — fires when faction unit count crosses threshold (polled)
    /// </summary>
    public class TriggerEvent
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        /// <summary>Faction slot: 0 = Player1, 1 = Player2. Used by most event types.</summary>
        [JsonPropertyName("faction")]
        public int Faction { get; set; } = 0;

        /// <summary>Building reference. Used by building_completed. DW-170 — DUAL MEANING (mirroring
        /// <c>ScenarioBuilding.Type</c>): either a legacy <c>BuildingType</c> ENUM NAME ("CommandCenter" |
        /// "Barracks" | "ArcheryRange" | "SiegeWorkshop" | "Aviary"), or an AUTHORED building-def id
        /// (<c>[a-z0-9_]</c>, e.g. "watchtower") declared by the faction in the slot <see cref="Faction"/> names —
        /// that slot IS the faction qualifier, since the occurrence only ever matches its own builder slot. The
        /// gate resolves the authored form against the owner faction's <c>Buildings</c>; the director matches it
        /// against the placed building's <c>DefinitionId</c>. The bare "Custom" sentinel is rejected.</summary>
        [JsonPropertyName("building_type")]
        public string? BuildingType { get; set; }

        /// <summary>Named timer ID. Used by timer_expires.</summary>
        [JsonPropertyName("timer_name")]
        public string? TimerName { get; set; }

        /// <summary>Ore amount to compare against. Used by resource_threshold.</summary>
        [JsonPropertyName("amount")]
        public Fixed Amount { get; set; } = Fixed.Zero;

        /// <summary>Unit count to compare against. Used by unit_count_threshold.</summary>
        [JsonPropertyName("count")]
        public int Count { get; set; } = 0;

        /// <summary>Comparison operator: ">" | "<" | ">=" | "<=" | "==" | "!=". Used by threshold events.</summary>
        [JsonPropertyName("operator")]
        public string Operator { get; set; } = ">=";
    }

    /// <summary>
    /// A condition that must evaluate true for a trigger to fire.
    ///
    /// Supported types:
    ///   always             — always true (no additional fields)
    ///   building_exists    — faction has an alive, fully-built building of building_type
    ///   resource_comparison — faction ore compared with amount via operator
    ///   unit_count         — faction alive unit count compared with count via operator
    ///   variable_comparison — named integer variable compared with value via operator
    ///   unit_in_region     — a live unit of faction is inside the named region (region_id) — Story 6.4.
    ///                        Stateless Fixed inclusive point-in-rect over ascending-entity-id positions; the
    ///                        deterministic containment condition Epic 7's win-condition presets consume.
    /// </summary>
    public class TriggerCondition
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "always";

        /// <summary>Faction slot: 0 = Player1, 1 = Player2.</summary>
        [JsonPropertyName("faction")]
        public int Faction { get; set; } = 0;

        /// <summary>Building reference for building_exists. DW-170 — DUAL MEANING, identical to
        /// <see cref="TriggerEvent.BuildingType"/>: a legacy <c>BuildingType</c> enum name, or an AUTHORED
        /// building-def id declared by the faction in the slot <see cref="Faction"/> names (the qualifier — the
        /// scan only ever looks at that faction's buildings, matching their <c>DefinitionId</c>).</summary>
        [JsonPropertyName("building_type")]
        public string? BuildingType { get; set; }

        [JsonPropertyName("amount")]
        public Fixed Amount { get; set; } = Fixed.Zero;

        [JsonPropertyName("count")]
        public int Count { get; set; } = 0;

        /// <summary>Variable name for variable_comparison.</summary>
        [JsonPropertyName("variable")]
        public string? Variable { get; set; }

        /// <summary>Named region id for unit_in_region (Story 6.4). References a <see cref="ScenarioRegion.Id"/>;
        /// the validator rejects a dangling ref (an undefined region_id) fail-closed, mirroring the timer_expires
        /// dangling check.</summary>
        [JsonPropertyName("region_id")]
        public string? RegionId { get; set; }

        /// <summary>Integer value to compare variable against.</summary>
        [JsonPropertyName("value")]
        public int Value { get; set; } = 0;

        [JsonPropertyName("operator")]
        public string Operator { get; set; } = ">=";
    }

    /// <summary>
    /// An action executed when a trigger fires.
    ///
    /// Supported types:
    ///   spawn_unit      — spawns count units of unit_id for faction at (x, z); count is load-gated to
    ///                     1..EffectCaps.MaxSpawnCount (Story 7.6 — the literal 50 is retired).
    ///   display_message — shows text on screen for duration seconds
    ///   victory         — faction wins the match
    ///   defeat          — faction loses (opposite faction wins)
    ///   create_timer    — starts a countdown timer named timer_name lasting timer_seconds
    ///   add_resources   — adds amount ore to faction
    ///   set_variable    — sets named integer variable to value
    ///   play_sound      — plays the named sound asset
    /// </summary>
    public class TriggerAction
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "";

        /// <summary>Unit ID from faction JSON. Used by spawn_unit.</summary>
        [JsonPropertyName("unit_id")]
        public string? UnitId { get; set; }

        /// <summary>Faction slot: 0 = Player1, 1 = Player2. Used by spawn_unit, victory, defeat, add_resources.</summary>
        [JsonPropertyName("faction")]
        public int Faction { get; set; } = 0;

        /// <summary>Story 7.1: <see cref="Fixed"/> (not float) so a trigger spawn routes through the Fixed-native
        /// spawn primitive with NO in-tick <c>Fixed.FromFloat</c>. Quantized at the JSON boundary by
        /// <c>FixedJsonConverter</c> (registered in <c>ScenarioSerializer</c>).</summary>
        [JsonPropertyName("x")]
        public Fixed X { get; set; } = Fixed.Zero;

        [JsonPropertyName("z")]
        public Fixed Z { get; set; } = Fixed.Zero;

        /// <summary>Units to spawn. Used by spawn_unit. Story 7.6 (review P12): the literal-50 cap is RETIRED —
        /// both load gates (ScenarioValidator and the unconditional LoadScenario backstop) reject counts outside
        /// 1..<c>EffectCaps.MaxSpawnCount</c> (64), and the runtime seatbelt in <c>ScenarioDirector.ExecuteLeaf</c>
        /// clamps at that same named constant (defense-in-depth only — unreachable through any load path).</summary>
        [JsonPropertyName("count")]
        public int Count { get; set; } = 1;

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        /// <summary>Story 7.1: <see cref="Fixed"/> seconds — converted to float only at the presentation
        /// delegate boundary (never in the tick). Quantized at the JSON boundary by <c>FixedJsonConverter</c>.</summary>
        [JsonPropertyName("duration")]
        public Fixed Duration { get; set; } = Fixed.FromInt(4);

        [JsonPropertyName("timer_name")]
        public string? TimerName { get; set; }

        [JsonPropertyName("timer_seconds")]
        public Fixed TimerSeconds { get; set; } = Fixed.FromInt(30);

        [JsonPropertyName("amount")]
        public Fixed Amount { get; set; } = Fixed.Zero;

        [JsonPropertyName("variable")]
        public string? Variable { get; set; }

        [JsonPropertyName("value")]
        public int Value { get; set; } = 0;

        [JsonPropertyName("sound_id")]
        public string? SoundId { get; set; }
    }
}
