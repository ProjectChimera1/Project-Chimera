#nullable enable
using System;
using System.Text.Json.Serialization;

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
        public float CooldownSeconds { get; set; } = 0f;

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

        /// <summary>Building type name ("CommandCenter" | "Barracks" | "ArcheryRange" | "SiegeWorkshop"). Used by building_completed.</summary>
        [JsonPropertyName("building_type")]
        public string? BuildingType { get; set; }

        /// <summary>Named timer ID. Used by timer_expires.</summary>
        [JsonPropertyName("timer_name")]
        public string? TimerName { get; set; }

        /// <summary>Ore amount to compare against. Used by resource_threshold.</summary>
        [JsonPropertyName("amount")]
        public float Amount { get; set; } = 0f;

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
    /// </summary>
    public class TriggerCondition
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "always";

        /// <summary>Faction slot: 0 = Player1, 1 = Player2.</summary>
        [JsonPropertyName("faction")]
        public int Faction { get; set; } = 0;

        [JsonPropertyName("building_type")]
        public string? BuildingType { get; set; }

        [JsonPropertyName("amount")]
        public float Amount { get; set; } = 0f;

        [JsonPropertyName("count")]
        public int Count { get; set; } = 0;

        /// <summary>Variable name for variable_comparison.</summary>
        [JsonPropertyName("variable")]
        public string? Variable { get; set; }

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
    ///   spawn_unit      — spawns count units of unit_id for faction at (x, z). Max 50 per action.
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

        [JsonPropertyName("x")]
        public float X { get; set; } = 0f;

        [JsonPropertyName("z")]
        public float Z { get; set; } = 0f;

        /// <summary>Units to spawn. Capped at 50. Used by spawn_unit.</summary>
        [JsonPropertyName("count")]
        public int Count { get; set; } = 1;

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("duration")]
        public float Duration { get; set; } = 4f;

        [JsonPropertyName("timer_name")]
        public string? TimerName { get; set; }

        [JsonPropertyName("timer_seconds")]
        public float TimerSeconds { get; set; } = 30f;

        [JsonPropertyName("amount")]
        public float Amount { get; set; } = 0f;

        [JsonPropertyName("variable")]
        public string? Variable { get; set; }

        [JsonPropertyName("value")]
        public int Value { get; set; } = 0;

        [JsonPropertyName("sound_id")]
        public string? SoundId { get; set; }
    }
}
