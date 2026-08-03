#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ProjectChimera.AI.Providers;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl; // NodeKinds — the closed flat trigger vocabulary (internal, same assembly)

namespace ProjectChimera.AI
{
    /// <summary>
    /// Context injected into LLM prompts so the model knows which units and factions
    /// are available in the current scenario.
    /// </summary>
    public class ScenarioContext
    {
        /// <summary>All unit IDs available across both factions (e.g. "worker", "melee", "archer").</summary>
        public string[] UnitIds { get; set; } = Array.Empty<string>();

        /// <summary>Half-width of the playable map in world units. Spawn points must be within ±Bounds.</summary>
        public float MapBounds { get; set; } = 120f;
    }

    /// <summary>
    /// Context injected into map generation prompts — tells the LLM which factions
    /// and unit types are available, the playable bounds, and the faction JSON paths.
    /// </summary>
    public class MapGeneratorContext
    {
        /// <summary>All unit IDs available across both factions (e.g. "worker", "melee").</summary>
        public string[] UnitIds { get; set; } = Array.Empty<string>();

        /// <summary>Half-width of the playable map in world units. Positions must be within ±MapBounds.</summary>
        public float MapBounds { get; set; } = 120f;

        /// <summary>res:// path to the faction JSON for slot 0 (Player 1).</summary>
        public string Slot0FactionJson { get; set; } = "res://resources/data/factions/alpha_faction.json";

        /// <summary>res:// path to the faction JSON for slot 1 (Player 2).</summary>
        public string Slot1FactionJson { get; set; } = "res://resources/data/factions/beta_faction.json";

        // ── Story 8.3 — the three RTS clamps, parameterized out of ValidateScenario/BuildMapSystemPrompt onto this
        //    TRUSTED context (editor/caller-supplied). Defaults reproduce today's RTS behavior exactly, so RTS output
        //    is byte-for-byte unchanged; a non-RTS caller supplies relaxed values. NONE of these is ever sourced from
        //    the parsed (untrusted) scenario file — that would weaken the validation gate circularly. A future
        //    ScenarioType registry can populate them per type (see deferred-work DW note). ──

        /// <summary>Story 8.3: the minimum number of player slots a valid scenario must declare. RTS default 2;
        /// <see cref="ValidateScenario"/> rejects fewer. Trusted (never read from the scenario file).</summary>
        public int MinPlayerSlots { get; set; } = 2;

        /// <summary>Story 8.3: the maximum pre-placed combat (non-worker) units allowed per faction slot. RTS default
        /// 6; <see cref="ValidateScenario"/> rejects more. Trusted (never read from the scenario file).</summary>
        public int MaxCombatUnitsPerSlot { get; set; } = 6;

        /// <summary>Story 8.3: the TRUSTED per-slot faction-JSON resolver. NULL ⇒ the RTS default (slot 0 →
        /// <see cref="Slot0FactionJson"/>, every other slot → <see cref="Slot1FactionJson"/>) — identical to today.
        /// A non-RTS caller supplies its own trusted mapping. <see cref="ValidateScenario"/> OVERWRITES each slot's
        /// hallucinated <c>faction_json</c> from this resolver, so the untrusted file never dictates the path.</summary>
        public Func<int, string>? FactionJsonResolver { get; set; }

        /// <summary>Resolve the trusted faction-JSON path for the given 0-based <paramref name="slot"/>, honoring
        /// <see cref="FactionJsonResolver"/> when set else the RTS slot-0/slot-1 default mapping.</summary>
        public string ResolveFactionJson(int slot)
            => FactionJsonResolver != null
                ? FactionJsonResolver(slot)
                : (slot == 0 ? Slot0FactionJson : Slot1FactionJson);
    }

    /// <summary>
    /// Translates natural language descriptions into validated TriggerDefinition / ScenarioData JSON, routing every
    /// generation call through the Story 8.2 <see cref="ILLMProvider"/> stack via
    /// <see cref="LlmProviderFactory.TryCreate"/>. The selected provider is AUTHORITATIVE — on failure that provider's
    /// error is surfaced and NO other provider is attempted (the old implicit Claude→Ollama fallback is gone).
    ///
    /// Pure C# — no Godot dependency. The API key is read ONLY through <see cref="ISecretStore"/> (via the factory),
    /// never a property / <c>[Export]</c> field / settings. The owned <see cref="HttpClient"/> is built with
    /// <c>AllowAutoRedirect=false</c> (a real key now flows through it — closes the cross-host redirect key-leak).
    /// Follows the ConcurrentQueue/DrainEvents pattern used by NakamaService and ModIoService.
    /// Call DrainEvents() once per _Process frame to marshal callbacks to the main thread.
    /// </summary>
    public class LLMService
    {
        // ── Configuration ─────────────────────────────────────────────────────

        private const int    MAX_TOKENS    = 2048;
        // A faction draft echoes the full unit schema for EVERY unit in a playable roster (plus buildings), so the
        // single-entity 2048 budget truncates it mid-JSON (→ a generic "Invalid JSON" reject). Give it more headroom.
        private const int    FACTION_DRAFT_MAX_TOKENS = 8192;
        private const int    TIMEOUT_MS    = 30_000;

        // Safety cap: spawn_unit count is clamped to this in validation, independently
        // of the schema comment in the prompt.
        private const int    MAX_SPAWN_COUNT = 50;

        // ── Internal state ────────────────────────────────────────────────────

        private readonly HttpClient _http;
        private readonly Func<SettingsData> _getSettings;
        private readonly ISecretStore _secretStore;
        private readonly ConcurrentQueue<Action> _queue = new();
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _mapCts;

        // ── Construction ──────────────────────────────────────────────────────

        /// <summary>
        /// Story 8.3: construct the service with the Godot-free seams the provider stack needs — a settings accessor
        /// (the authoritative selected provider/model/base-URL), the <see cref="ISecretStore"/> (the ONLY key source),
        /// and an optional injected <see cref="HttpClient"/> (the unit-test seam over a stub handler). When
        /// <paramref name="http"/> is null an owned client is built with <c>AllowAutoRedirect=false</c>.
        /// </summary>
        public LLMService(Func<SettingsData> getSettings, ISecretStore secretStore, HttpClient? http = null)
        {
            _getSettings = getSettings ?? throw new ArgumentNullException(nameof(getSettings));
            _secretStore = secretStore ?? throw new ArgumentNullException(nameof(secretStore));

            if (http != null)
            {
                _http = http;
            }
            else
            {
                _http = new HttpClient(BuildOwnedHttpHandler())
                { Timeout = TimeSpan.FromMilliseconds(TIMEOUT_MS) };
                _http.DefaultRequestHeaders.UserAgent.ParseAdd("ProjectChimera/1.0");
            }
        }

        /// <summary>
        /// Story 8.3: the owned-client message handler, factored out so the <c>AllowAutoRedirect=false</c> hardening is
        /// directly Tier-1 assertable (the owned path is never taken when a stub client is injected). A real key now
        /// flows through this client via the provider adapters, so redirects are refused: the host allowlist is enforced
        /// only against the initial base URL, and .NET does NOT strip a custom <c>x-api-key</c> header on a cross-host
        /// redirect — mirrors the evaluator client 8.2 hardened.
        /// </summary>
        internal static HttpClientHandler BuildOwnedHttpHandler() => new() { AllowAutoRedirect = false };

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Asynchronously generates a trigger from a natural language description.
        /// The callback is marshalled to the main thread via DrainEvents().
        /// On success: callback(trigger, null). On failure: callback(null, errorMessage).
        /// </summary>
        public void GenerateTriggerAsync(
            string description,
            ScenarioContext context,
            Action<TriggerDefinition?, string?> onComplete)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            // Snapshot the authoritative settings on the caller thread; the factory reads the provider/model/base-URL
            // from it and the key from the secret store — no fallback, no other provider attempted.
            SettingsData settings = _getSettings();

            Task.Run(async () =>
            {
                try
                {
                    string prompt = BuildSystemPrompt(context);
                    string msg    = $"Create a trigger for: {description}";

                    // Route through the selected provider only (Story 8.3). A synchronous-unavailable case
                    // (no provider / no key / bad host) short-circuits with the four-state message and NO network call.
                    if (!LlmProviderFactory.TryCreate(settings, _secretStore, _http,
                            out ILLMProvider? provider, out AiAvailability failure))
                    {
                        _queue.Enqueue(() => onComplete(null, AiAvailabilityMessages.Describe(failure)));
                        return;
                    }

                    NormalizedResult result = await provider!.GenerateAsync(
                        new NormalizedRequest(prompt, msg, MAX_TOKENS), token);

                    if (!result.Ok)
                    {
                        // The selected provider's failure is surfaced — never masked by another provider — and voiced
                        // with the SAME four-state microcopy Test-connection uses (Story 8.3), not a raw adapter string,
                        // so the async failure half of the four-state availability UX matches the synchronous half.
                        _queue.Enqueue(() => onComplete(null,
                            AiAvailabilityMessages.Describe(AiAvailabilityMap.FromFailure(result.Failure))));
                        return;
                    }

                    // Validate the generated JSON.
                    var (trigger, validationError) = Validate(StripMarkdown(result.Text), context);
                    if (trigger == null)
                    {
                        _queue.Enqueue(() => onComplete(null,
                            $"Generated trigger failed validation: {validationError}"));
                        return;
                    }

                    _queue.Enqueue(() => onComplete(trigger, null));
                }
                catch (OperationCanceledException) { /* silently dropped */ }
                catch (Exception ex)
                {
                    _queue.Enqueue(() => onComplete(null, ex.Message));
                }
            }, token);
        }

        /// <summary>Cancel any in-flight generation request.</summary>
        public void Cancel() => _cts?.Cancel();

        /// <summary>Drain queued main-thread callbacks. Call once per _Process frame.</summary>
        public void DrainEvents()
        {
            while (_queue.TryDequeue(out var action))
                action();
        }

        // ── Validation pipeline ───────────────────────────────────────────────

        /// <summary>
        /// Six-pass validation:
        /// 1. Schema — can JSON be deserialized to TriggerDefinition?
        /// 2. Construct membership (Story 8.3) — every event/condition/action Type is a member of the closed flat
        ///    <see cref="NodeKinds"/> vocabulary; an unknown or graph-only construct is rejected with a LOCATED error.
        /// 3. Faction slots — 0 or 1 only
        /// 4. BuildingType strings — must match BuildingType enum
        /// 5. Operators — only the six standard comparison symbols
        /// 6. Range / safety — counts ≤ 50, durations > 0, spawn inside bounds
        /// Returns (null, errorMessage) on failure, (trigger, null) on success.
        /// </summary>
        public static (TriggerDefinition? trigger, string? error) Validate(
            string json, ScenarioContext context)
        {
            // Pass 1 — schema.
            TriggerDefinition trigger;
            try
            {
                trigger = JsonSerializer.Deserialize<TriggerDefinition>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new FixedJsonConverter() } })
                    ?? throw new InvalidOperationException("Deserialised to null.");
            }
            catch (Exception ex)
            {
                return (null, $"Invalid JSON: {ex.Message}");
            }

            // Pass 2 — construct membership (Story 8.3). Reject any event/condition/action whose Type is outside the
            // closed FLAT NodeKinds vocabulary, with a LOCATED error (path + offending value) matching the shape
            // ScenarioValidator uses. Driven off the single NodeKinds registry so the LLM gate and the load gate never
            // diverge — a graph-only construct (e.g. custom_event, for_each, order_units) is a flat-channel unknown and
            // is rejected here, not silently accepted.
            var knownEvents     = new HashSet<string>(NodeKinds.EventTypes, StringComparer.Ordinal);
            var knownConditions = new HashSet<string>(NodeKinds.ConditionTypes, StringComparer.Ordinal);
            var knownActions    = new HashSet<string>(NodeKinds.FlatActionTypes, StringComparer.Ordinal);
            for (int j = 0; j < trigger.Events.Length; j++)
                if (!knownEvents.Contains(trigger.Events[j].Type))
                    return (null, $"events[{j}].type='{trigger.Events[j].Type}' is not a known trigger event type.");
            for (int j = 0; j < trigger.Conditions.Length; j++)
                if (!knownConditions.Contains(trigger.Conditions[j].Type))
                    return (null, $"conditions[{j}].type='{trigger.Conditions[j].Type}' is not a known trigger condition type.");
            for (int j = 0; j < trigger.Actions.Length; j++)
                if (!knownActions.Contains(trigger.Actions[j].Type))
                    return (null, $"actions[{j}].type='{trigger.Actions[j].Type}' is not a known trigger action type.");

            // Pass 3 — faction slots.
            foreach (var ev in trigger.Events)
                if (ev.Faction is not (0 or 1))
                    return (null, $"Event '{ev.Type}' has invalid faction slot {ev.Faction} (must be 0 or 1).");
            foreach (var c in trigger.Conditions)
                if (c.Faction is not (0 or 1))
                    return (null, $"Condition '{c.Type}' has invalid faction slot {c.Faction}.");
            foreach (var a in trigger.Actions)
                if (a.Faction is not (0 or 1))
                    return (null, $"Action '{a.Type}' has invalid faction slot {a.Faction}.");

            // Pass 4 — building type strings.
            foreach (var ev in trigger.Events)
                if (!string.IsNullOrEmpty(ev.BuildingType)
                    && !Enum.TryParse<BuildingType>(ev.BuildingType, out _))
                    return (null, $"Unknown building_type '{ev.BuildingType}'. " +
                        $"Valid: {string.Join(", ", Enum.GetNames(typeof(BuildingType)))}");
            foreach (var c in trigger.Conditions)
                if (!string.IsNullOrEmpty(c.BuildingType)
                    && !Enum.TryParse<BuildingType>(c.BuildingType, out _))
                    return (null, $"Unknown building_type '{c.BuildingType}'.");

            // Pass 5 — operator strings.
            var validOps = new HashSet<string> { ">", "<", ">=", "<=", "==", "!=" };
            foreach (var ev in trigger.Events)
                if (!string.IsNullOrEmpty(ev.Operator) && !validOps.Contains(ev.Operator))
                    return (null, $"Invalid operator '{ev.Operator}' in event '{ev.Type}'.");
            foreach (var c in trigger.Conditions)
                if (!string.IsNullOrEmpty(c.Operator) && !validOps.Contains(c.Operator))
                    return (null, $"Invalid operator '{c.Operator}' in condition '{c.Type}'.");

            // Pass 6 — range and safety.
            foreach (var a in trigger.Actions)
            {
                if (a.Type == "spawn_unit")
                {
                    if (a.Count <= 0 || a.Count > MAX_SPAWN_COUNT)
                        a.Count = Math.Clamp(a.Count, 1, MAX_SPAWN_COUNT); // auto-clamp rather than reject
                    // Story 7.1: a.X/a.Z are now Fixed. Quantize the map bound to Fixed once (the sanctioned AI
                    // authoring float→Fixed boundary) and compare Fixed-vs-Fixed — no float comparison here.
                    Fixed b = Fixed.FromFloat(context.MapBounds);
                    if (a.X < -b || a.X > b || a.Z < -b || a.Z > b)
                        return (null, $"spawn_unit position ({a.X}, {a.Z}) is outside map bounds ±{context.MapBounds}.");
                }
                if (a.Type == "create_timer" && a.TimerSeconds <= Fixed.Zero)
                    return (null, $"create_timer '{a.TimerName}' has invalid duration {a.TimerSeconds.ToFloat()}s.");
                if (a.Type == "display_message" && a.Duration <= Fixed.Zero)
                    a.Duration = Fixed.FromInt(4); // auto-fix
            }

            return (trigger, null);
        }

        // ── Prompt builder ────────────────────────────────────────────────────

        // Story 8.3: internal (not private) so the Tier-1 staleness-guard test can assert the prompt enumerates every
        // flat NodeKinds construct — a future flat construct added to NodeKinds but not described here fails the guard.
        internal static string BuildSystemPrompt(ScenarioContext ctx)
        {
            var sb = new StringBuilder();
            sb.AppendLine(
                "You are a trigger authoring assistant for Project Chimera, a real-time strategy game.");
            sb.AppendLine(
                "Convert the user's description into a valid JSON TriggerDefinition object.");
            sb.AppendLine();
            sb.AppendLine("=== TRIGGER SCHEMA ===");
            sb.AppendLine(@"{
  ""name"": ""string"",
  ""enabled"": true,
  ""run_once"": false,
  ""cooldown_seconds"": 0.0,
  ""priority"": 0,
  ""events"": [ TriggerEvent ],
  ""conditions"": [ TriggerCondition ],
  ""actions"": [ TriggerAction ]
}");
            sb.AppendLine();
            // NOTE: every construct string below is a member of NodeKinds.EventTypes / ConditionTypes /
            // FlatActionTypes (Story 7.13 appended the 5 built-in event sources; Story 6.4 added unit_in_region). This
            // description block is HAND-AUTHORED (not derived from NodeKinds) precisely so the staleness-guard test can
            // catch a future NodeKinds addition that was not documented here.
            sb.AppendLine("=== VALID EVENT TYPES ===");
            sb.AppendLine(@"match_start              — no additional fields
unit_dies               — faction (0=Player1, 1=Player2)
building_completed      — faction, building_type (""CommandCenter""|""Barracks""|""ArcheryRange""|""SiegeWorkshop"")
timer_expires           — timer_name (string)
resource_threshold      — faction, amount (float), operator
unit_count_threshold    — faction, count (int), operator
unit_damaged            — faction — fires when a unit of faction takes damage
unit_trained            — faction — fires when a unit of faction finishes training
ability_cast            — faction — fires when a unit of faction casts an ability
hero_level              — faction — fires when a hero of faction gains a level
player_chat             — faction — fires when a player sends a chat message");
            sb.AppendLine();
            sb.AppendLine("=== VALID CONDITION TYPES ===");
            sb.AppendLine(@"always                  — always true
building_exists         — faction, building_type
resource_comparison     — faction, amount (float), operator
unit_count              — faction, count (int), operator
variable_comparison     — variable (string), value (int), operator
unit_in_region          — faction, region_id (string) — true while a live unit of faction is inside the named region");
            sb.AppendLine();
            sb.AppendLine("=== VALID ACTION TYPES ===");
            sb.AppendLine(@"spawn_unit      — unit_id (string), faction, x (float), z (float), count (int, max 50)
display_message — text (string), duration (float seconds)
victory         — faction (this faction wins)
defeat          — faction (this faction loses, other wins)
create_timer    — timer_name (string), timer_seconds (float)
add_resources   — faction, amount (float ore)
set_variable    — variable (string), value (int)
play_sound      — sound_id (string)");
            sb.AppendLine();
            sb.AppendLine("=== VALID OPERATORS ===");
            sb.AppendLine(@""">"" | ""<"" | "">="" | ""<="" | ""=="" | ""!=""");
            sb.AppendLine();
            sb.AppendLine("=== SCENARIO CONTEXT ===");
            sb.AppendLine($"Available unit IDs: {string.Join(", ", ctx.UnitIds.Select(id => $"\"{id}\""))}");
            sb.AppendLine($"Map bounds: positions must be within ±{ctx.MapBounds} on X and Z axes.");
            sb.AppendLine();
            sb.AppendLine("=== EXAMPLES ===");
            sb.AppendLine(@"Example 1 — ""When the match starts, give Player 1 an extra 200 ore"":
{
  ""name"": ""Bonus Starting Resources"",
  ""enabled"": true,
  ""run_once"": true,
  ""cooldown_seconds"": 0,
  ""priority"": 0,
  ""events"": [{""type"": ""match_start""}],
  ""conditions"": [],
  ""actions"": [{""type"": ""add_resources"", ""faction"": 0, ""amount"": 200}]
}");
            sb.AppendLine();
            sb.AppendLine(@"Example 2 — ""When Player 2 builds a Barracks, spawn 5 enemy soldiers near Player 1's base"":
{
  ""name"": ""Enemy Vanguard"",
  ""enabled"": true,
  ""run_once"": true,
  ""cooldown_seconds"": 0,
  ""priority"": 0,
  ""events"": [{""type"": ""building_completed"", ""faction"": 1, ""building_type"": ""Barracks""}],
  ""conditions"": [],
  ""actions"": [
    {""type"": ""spawn_unit"", ""unit_id"": ""melee"", ""faction"": 1, ""x"": -30, ""z"": 0, ""count"": 5},
    {""type"": ""display_message"", ""text"": ""Enemy reinforcements spotted!"", ""duration"": 5}
  ]
}");
            sb.AppendLine();
            sb.AppendLine("=== INSTRUCTIONS ===");
            sb.AppendLine("Return ONLY valid JSON. No markdown fences, no explanation, no extra text.");
            return sb.ToString();
        }

        // ── Utilities ─────────────────────────────────────────────────────────

        /// <summary>Strip ```json ... ``` markdown fences that some models add.</summary>
        private static string StripMarkdown(string? text)
        {
            text = (text ?? "").Trim();
            if (text.StartsWith("```"))
            {
                int start = text.IndexOf('\n') + 1;
                int end   = text.LastIndexOf("```");
                if (start > 0 && end > start)
                    text = text.Substring(start, end - start).Trim();
            }
            return text;
        }

        // Helper type alias — BuildingType is defined in ProjectChimera.Core namespace.
        private enum BuildingType { CommandCenter, Barracks, ArcheryRange, SiegeWorkshop }

        // ── Scenario generation ───────────────────────────────────────────────

        /// <summary>
        /// Asynchronously generates a full ScenarioData from a natural language map brief.
        /// Callback is marshalled to the main thread via DrainEvents().
        /// On success: callback(scenario, null). On failure: callback(null, errorMessage).
        /// </summary>
        public void GenerateScenarioAsync(
            string description,
            MapGeneratorContext context,
            Action<ScenarioData?, string?> onComplete)
        {
            _mapCts?.Cancel();
            _mapCts = new CancellationTokenSource();
            var token = _mapCts.Token;

            // Snapshot the authoritative settings on the caller thread (see GenerateTriggerAsync). No fallback.
            SettingsData settings = _getSettings();

            Task.Run(async () =>
            {
                try
                {
                    string prompt = BuildMapSystemPrompt(context);
                    string msg    = $"Create a map scenario for: {description}";

                    // Route through the selected provider only (Story 8.3). Synchronous-unavailable short-circuits
                    // with the four-state message and NO network call.
                    if (!LlmProviderFactory.TryCreate(settings, _secretStore, _http,
                            out ILLMProvider? provider, out AiAvailability failure))
                    {
                        _queue.Enqueue(() => onComplete(null, AiAvailabilityMessages.Describe(failure)));
                        return;
                    }

                    NormalizedResult result = await provider!.GenerateAsync(
                        new NormalizedRequest(prompt, msg, MAX_TOKENS), token);

                    if (!result.Ok)
                    {
                        // Story 8.3: voice the runtime failure with the shared four-state microcopy (see the trigger path).
                        _queue.Enqueue(() => onComplete(null,
                            AiAvailabilityMessages.Describe(AiAvailabilityMap.FromFailure(result.Failure))));
                        return;
                    }

                    var (scenario, validationError) = ValidateScenario(StripMarkdown(result.Text), context);
                    if (scenario == null)
                    {
                        _queue.Enqueue(() => onComplete(null,
                            $"Generated map failed validation: {validationError}"));
                        return;
                    }

                    _queue.Enqueue(() => onComplete(scenario, null));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _queue.Enqueue(() => onComplete(null, ex.Message));
                }
            }, token);
        }

        /// <summary>Cancel any in-flight scenario generation request.</summary>
        public void CancelScenario() => _mapCts?.Cancel();

        /// <summary>
        /// Validate a generated ScenarioData JSON through seven passes:
        /// 1. Schema — the DW-366 upstream byte-size guard (≤ <see cref="ScenarioSerializer.MaxScenarioFileBytes"/>,
        ///    checked BEFORE deserialization), then deserialization succeeds. (UNIVERSAL — always runs.)
        /// 2. Player slots — at least <see cref="MapGeneratorContext.MinPlayerSlots"/> (RTS default 2); slot indices
        ///    unique and within [0, PlayerSlots.Length) (UNIVERSAL — always runs; DW-373); faction paths
        ///    forced from the TRUSTED per-slot <see cref="MapGeneratorContext.ResolveFactionJson"/> mapping.
        /// 3. Building types — only valid BuildingType enum names.
        /// 4. Unit IDs — only IDs present in MapGeneratorContext.UnitIds.
        /// 5. Position bounds — all X/Z within ±MapBounds. (UNIVERSAL — always runs.)
        /// 6. Ore node spacing — every pair at least 15 units apart. (UNIVERSAL — always runs.)
        /// 7. Pre-placed unit count — at most <see cref="MapGeneratorContext.MaxCombatUnitsPerSlot"/> (RTS default 6)
        ///    non-worker units per faction slot.
        /// Story 8.3: the three RTS clamps (passes 2 min-slots / 2 faction-path / 7 max-combat) are parameterized from
        /// the TRUSTED <paramref name="context"/> (RTS defaults preserve today's behavior exactly); the universal passes
        /// (1/5/6) always run regardless of clamp values. NO clamp value is ever sourced from the parsed (untrusted)
        /// scenario file. Returns (null, errorMessage) on failure, (scenario, null) on success.
        /// </summary>
        public static (ScenarioData? scenario, string? error) ValidateScenario(
            string json, MapGeneratorContext context)
        {
            // Pass 1 — schema (UNIVERSAL).
            ScenarioData scenario;
            try
            {
                // DW-366 — upstream size guard, uniform with ScenarioSerializer.LoadFromFile: an AI-generated
                // scenario string is untrusted input, so cap its byte size BEFORE deserialization materializes any
                // collection (the per-collection gates all run post-parse). Over-cap → the guard's JsonException is
                // caught below and surfaced as the pass-1 validation error.
                ScenarioSerializer.GuardScenarioInputSize(Encoding.UTF8.GetByteCount(json), "generated scenario");
                scenario = JsonSerializer.Deserialize<ScenarioData>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, Converters = { new FixedJsonConverter() } })
                    ?? throw new InvalidOperationException("Deserialised to null.");
            }
            catch (Exception ex)
            {
                return (null, $"Invalid JSON: {ex.Message}");
            }

            // Pass 2 — player slots (Story 8.3: min-slots clamp from the trusted context; RTS default 2).
            if (scenario.PlayerSlots.Length < context.MinPlayerSlots)
                return (null, $"Expected at least {context.MinPlayerSlots} player slots, got {scenario.PlayerSlots.Length}.");

            // DW-373 — slot-index uniqueness + range (UNIVERSAL — structural, independent of the trusted clamp
            // values). Distinct + within [0, PlayerSlots.Length) ⇒ the declared indices form a permutation of
            // 0..Length-1, so the min-slots length check above really guarantees that many DISTINCT players.
            // Without this, an (untrusted) scenario declaring two slots both "slot":0 passes the length-based
            // check, both resolve to the SAME faction via the per-slot resolver below, and Pass 7 merges their
            // combat counts under one key — a degenerate one-faction scenario sails past the gate.
            var declaredSlots = new HashSet<int>();
            for (int i = 0; i < scenario.PlayerSlots.Length; i++)
            {
                int slotIndex = scenario.PlayerSlots[i].Slot;
                if (slotIndex < 0 || slotIndex >= scenario.PlayerSlots.Length)
                    return (null, $"player_slots[{i}].slot={slotIndex} is outside [0, {scenario.PlayerSlots.Length}).");
                if (!declaredSlots.Add(slotIndex))
                    return (null, $"player_slots[{i}].slot={slotIndex} duplicates another player slot — slot indices must be unique.");
            }

            // Force faction JSON paths from the TRUSTED per-slot resolver — LLMs often hallucinate these, and the
            // untrusted file must never dictate the path. RTS default = the existing slot-0/slot-1 mapping.
            foreach (var slot in scenario.PlayerSlots)
                slot.FactionJson = context.ResolveFactionJson(slot.Slot);

            // Pass 3 — building types.
            var validBuildings = new HashSet<string>
                { "CommandCenter", "Barracks", "ArcheryRange", "SiegeWorkshop" };
            foreach (var b in scenario.Buildings)
                if (!validBuildings.Contains(b.Type))
                    return (null, $"Unknown building type '{b.Type}'. " +
                        $"Valid: {string.Join(", ", validBuildings)}");

            // Pass 4 — unit IDs.
            var validUnits = new HashSet<string>(context.UnitIds, StringComparer.OrdinalIgnoreCase);
            validUnits.Add("worker"); // always present in every faction
            foreach (var u in scenario.Units)
                if (!validUnits.Contains(u.UnitId))
                    return (null, $"Unknown unit_id '{u.UnitId}'. " +
                        $"Valid: {string.Join(", ", context.UnitIds)}");

            // Pass 5 — position bounds (UNIVERSAL — always runs regardless of the clamp values).
            float bounds = context.MapBounds;
            foreach (var slot in scenario.PlayerSlots)
                if (Math.Abs(slot.BaseX) > bounds || Math.Abs(slot.BaseZ) > bounds)
                    return (null, $"Slot {slot.Slot} base ({slot.BaseX}, {slot.BaseZ}) " +
                        $"is outside map bounds ±{bounds}.");

            foreach (var node in scenario.ResourceNodes)
            {
                if (Math.Abs(node.X) > bounds || Math.Abs(node.Z) > bounds)
                    return (null, $"Resource node at ({node.X}, {node.Z}) is outside ±{bounds}.");
                if (node.Supply <= 0) node.Supply = 400f;
                if (node.Rate   <= 0) node.Rate   = 5f;
            }

            foreach (var b in scenario.Buildings)
                if (Math.Abs(b.X) > bounds || Math.Abs(b.Z) > bounds)
                    return (null, $"Building '{b.Type}' at ({b.X}, {b.Z}) is outside ±{bounds}.");

            foreach (var u in scenario.Units)
                if (Math.Abs(u.X) > bounds || Math.Abs(u.Z) > bounds)
                    return (null, $"Unit '{u.UnitId}' at ({u.X}, {u.Z}) is outside ±{bounds}.");

            // Pass 6 — ore node spacing ≥ 15u (UNIVERSAL — always runs regardless of the clamp values).
            for (int i = 0; i < scenario.ResourceNodes.Length; i++)
                for (int j = i + 1; j < scenario.ResourceNodes.Length; j++)
                {
                    float dx = scenario.ResourceNodes[i].X - scenario.ResourceNodes[j].X;
                    float dz = scenario.ResourceNodes[i].Z - scenario.ResourceNodes[j].Z;
                    float dist = (float)Math.Sqrt(dx * dx + dz * dz);
                    if (dist < 15f)
                        return (null, $"Ore nodes {i} and {j} are {dist:F1}u apart (minimum 15u).");
                }

            // Pass 7 — pre-placed combat units per slot (Story 8.3: max-combat clamp from the trusted context; RTS
            // default 6).
            var combatCount = new Dictionary<int, int>();
            foreach (var u in scenario.Units)
                if (!string.Equals(u.UnitId, "worker", StringComparison.OrdinalIgnoreCase))
                    combatCount[u.Slot] = combatCount.GetValueOrDefault(u.Slot) + 1;
            foreach (var kv in combatCount)
                if (kv.Value > context.MaxCombatUnitsPerSlot)
                    return (null, $"Slot {kv.Key} has {kv.Value} pre-placed combat units (max {context.MaxCombatUnitsPerSlot}).");

            return (scenario, null);
        }

        // ── Map system prompt ─────────────────────────────────────────────────

        // Story 8.3: internal (not private) so the Tier-1 clamp test can assert the prompt reflects the SAME clamp
        // values ValidateScenario gates against (min player slots + max combat units per slot).
        internal static string BuildMapSystemPrompt(MapGeneratorContext ctx)
        {
            var sb = new StringBuilder();
            sb.AppendLine(
                "You are a scenario designer for Project Chimera, a real-time strategy game.");
            sb.AppendLine(
                "Convert the user's map brief into a valid JSON ScenarioData object.");
            sb.AppendLine();
            sb.AppendLine("=== SCENARIO SCHEMA ===");
            sb.AppendLine($@"{{
  ""id"": ""string (lowercase_snake_case, e.g. my_map)"",
  ""display_name"": ""string"",
  ""terrain_ref"": """",
  ""map_bounds"": 120.0,
  ""win_condition"": ""DestroyAllBuildings"" | ""EliminateAllUnits"",
  ""player_slots"": [
    {{ ""slot"": 0, ""faction_json"": ""{ctx.Slot0FactionJson}"", ""start_ore"": 200.0, ""base_x"": -45.0, ""base_z"": 0.0 }},
    {{ ""slot"": 1, ""faction_json"": ""{ctx.Slot1FactionJson}"", ""start_ore"": 200.0, ""base_x"":  45.0, ""base_z"": 0.0 }}
  ],
  ""resource_nodes"": [
    {{ ""x"": float, ""z"": float, ""supply"": 400.0, ""rate"": 5.0, ""max_gatherers"": 4 }}
  ],
  ""buildings"": [
    {{ ""type"": ""CommandCenter""|""Barracks""|""ArcheryRange""|""SiegeWorkshop"", ""slot"": 0|1, ""x"": float, ""z"": float, ""pre_built"": true }}
  ],
  ""units"": [
    {{ ""unit_id"": ""string"", ""slot"": 0|1, ""x"": float, ""z"": float }}
  ],
  ""triggers"": []
}}");
            sb.AppendLine();
            sb.AppendLine("=== PLACEMENT RULES ===");
            sb.AppendLine($"- All x/z positions MUST be within ±{ctx.MapBounds} world units.");
            // Story 8.3: reflect the SAME min-player-slots clamp ValidateScenario gates against (RTS default 2).
            sb.AppendLine($"- Provide at least {ctx.MinPlayerSlots} player slots. Player 1 (slot 0): base near X=-45, Z=0. Player 2 (slot 1): base near X=45, Z=0.");
            sb.AppendLine("- Each slot MUST have a CommandCenter (pre_built=true) at its base position.");
            sb.AppendLine("- Ore nodes must be spaced at least 15 units apart from every other ore node.");
            sb.AppendLine("- Use 4–12 resource nodes. Supply 200–2000, rate 3–10.");
            // Story 8.3: reflect the SAME max-combat clamp ValidateScenario gates against (RTS default 6).
            sb.AppendLine($"- Pre-place at most {ctx.MaxCombatUnitsPerSlot} combat (non-worker) units per faction slot.");
            sb.AppendLine("- Start workers 3–5 units from their CommandCenter.");
            sb.AppendLine();
            sb.AppendLine("=== AVAILABLE UNIT IDs ===");
            sb.AppendLine(string.Join(", ", ctx.UnitIds.Select(id => $"\"{id}\"")));
            sb.AppendLine();
            sb.AppendLine("=== EXAMPLE OUTPUT ===");
            sb.AppendLine($@"{{
  ""id"": ""contested_valley"",
  ""display_name"": ""Contested Valley"",
  ""terrain_ref"": """",
  ""map_bounds"": 120,
  ""win_condition"": ""DestroyAllBuildings"",
  ""player_slots"": [
    {{ ""slot"": 0, ""faction_json"": ""{ctx.Slot0FactionJson}"", ""start_ore"": 200, ""base_x"": -45, ""base_z"": 0 }},
    {{ ""slot"": 1, ""faction_json"": ""{ctx.Slot1FactionJson}"", ""start_ore"": 200, ""base_x"":  45, ""base_z"": 0 }}
  ],
  ""resource_nodes"": [
    {{ ""x"": -25, ""z"":  15, ""supply"": 600, ""rate"": 5, ""max_gatherers"": 4 }},
    {{ ""x"": -25, ""z"": -15, ""supply"": 600, ""rate"": 5, ""max_gatherers"": 4 }},
    {{ ""x"":   0, ""z"":   0, ""supply"": 900, ""rate"": 7, ""max_gatherers"": 4 }},
    {{ ""x"":  25, ""z"":  15, ""supply"": 600, ""rate"": 5, ""max_gatherers"": 4 }},
    {{ ""x"":  25, ""z"": -15, ""supply"": 600, ""rate"": 5, ""max_gatherers"": 4 }}
  ],
  ""buildings"": [
    {{ ""type"": ""CommandCenter"", ""slot"": 0, ""x"": -45, ""z"": 0, ""pre_built"": true }},
    {{ ""type"": ""CommandCenter"", ""slot"": 1, ""x"":  45, ""z"": 0, ""pre_built"": true }}
  ],
  ""units"": [
    {{ ""unit_id"": ""worker"", ""slot"": 0, ""x"": -42, ""z"": -3 }},
    {{ ""unit_id"": ""worker"", ""slot"": 0, ""x"": -42, ""z"":  3 }},
    {{ ""unit_id"": ""worker"", ""slot"": 1, ""x"":  42, ""z"": -3 }},
    {{ ""unit_id"": ""worker"", ""slot"": 1, ""x"":  42, ""z"":  3 }}
  ],
  ""triggers"": []
}}");
            sb.AppendLine();
            sb.AppendLine("=== INSTRUCTIONS ===");
            sb.AppendLine(
                "Create an interesting, balanced, playable map based on the user's description.");
            sb.AppendLine(
                "Return ONLY valid JSON. No markdown fences, no explanation, no extra text.");
            return sb.ToString();
        }

        // ══════════════════════════════════════════════════════════════════════
        // Story 8.4 — AI entity drafts (unit / ability / hero / faction)
        //
        // A provider-backed EDITABLE-DRAFT framework mirroring GenerateTriggerAsync/GenerateScenarioAsync: the four
        // kinds share the SAME no-fallback / four-state / StripMarkdown pipeline (see RunDraftAsync) and each draft is
        // gated by the EXISTING per-kind validator (UnitDefinitionValidator / AbilityLoader+AbilityValidator /
        // FactionValidator) — never a fork, never a weakened gate. No second float→Fixed path is introduced:
        //   • ability drafts deserialize through ContentJson.Options (FixedJsonConverter quantizes at parse, rejects
        //     |v| >= 32768 / NaN / Inf) — the same boundary hand-authored abilities use;
        //   • unit / hero / faction drafts deserialize through the lenient FactionDefinition.JsonOptions (plain float)
        //     and are range-gated to the Fixed-safe [0, 32768) by UnitDefinitionValidator; quantization to Fixed still
        //     happens ONLY later at EntityWorld.ApplyUnitDefinition (the single def→SoA boundary), so a draft hashes
        //     identically to an equivalent hand-authored def BY CONSTRUCTION.
        // Stays Godot-free (no Godot dependency) — Tier-1 + analyzer covered.
        // ══════════════════════════════════════════════════════════════════════

        private CancellationTokenSource? _unitCts;
        private CancellationTokenSource? _abilityCts;
        private CancellationTokenSource? _heroCts;
        private CancellationTokenSource? _factionCts;
        private CancellationTokenSource? _balanceCts;   // Story 8.5 — balance-analysis flow (own CTS)

        // ── Public draft API ──────────────────────────────────────────────────

        /// <summary>
        /// Asynchronously generate an editable <see cref="UnitDefinition"/> draft from a natural-language prompt. The
        /// draft is gated by the SAME <see cref="UnitDefinitionValidator"/> hand-authored units pass; the callback is
        /// marshalled to the main thread via <see cref="DrainEvents"/>. On success: callback(def, null). On any failure
        /// (unavailable provider / provider error / failed validation): callback(null, message).
        /// </summary>
        public void GenerateUnitDraftAsync(string prompt, UnitDraftContext ctx, Action<UnitDefinition?, string?> onComplete)
        {
            _unitCts?.Cancel();
            _unitCts = new CancellationTokenSource();
            RunDraftAsync(BuildUnitDraftPrompt(ctx), $"Create a unit for: {prompt}",
                json => ValidateUnitDraft(json, ctx), _unitCts.Token, onComplete);
        }

        /// <summary>
        /// Asynchronously generate an editable <see cref="AbilityDefinition"/> draft. Gated by the SAME
        /// <see cref="AbilityLoader"/>/<see cref="AbilityValidator"/> path hand-authored abilities pass (numbers land as
        /// <see cref="Fixed"/> via <see cref="ContentJson.Options"/>). See <see cref="GenerateUnitDraftAsync"/> for the callback contract.
        /// </summary>
        public void GenerateAbilityDraftAsync(string prompt, AbilityDraftContext ctx, Action<AbilityDefinition?, string?> onComplete)
        {
            _abilityCts?.Cancel();
            _abilityCts = new CancellationTokenSource();
            RunDraftAsync(BuildAbilityDraftPrompt(ctx), $"Create an ability for: {prompt}",
                json => ValidateAbilityDraft(json, ctx), _abilityCts.Token, onComplete);
        }

        /// <summary>
        /// Asynchronously generate an editable HERO draft — a <see cref="UnitDefinition"/> with <c>is_hero:true</c> and a
        /// <c>hero</c> block. <see cref="ValidateHeroDraft"/> requires the hero designation before running the shared unit
        /// gate. See <see cref="GenerateUnitDraftAsync"/> for the callback contract.
        /// </summary>
        public void GenerateHeroDraftAsync(string prompt, UnitDraftContext ctx, Action<UnitDefinition?, string?> onComplete)
        {
            _heroCts?.Cancel();
            _heroCts = new CancellationTokenSource();
            RunDraftAsync(BuildHeroDraftPrompt(ctx), $"Create a hero for: {prompt}",
                json => ValidateHeroDraft(json, ctx), _heroCts.Token, onComplete);
        }

        /// <summary>
        /// Asynchronously generate an editable <see cref="FactionDefinition"/> draft. Gated by
        /// <see cref="FactionValidator.Validate"/> AND a per-unit <see cref="UnitDefinitionValidator"/> pass (closing the
        /// bare-faction-load deep-validation gap); roster-completeness (<c>ValidateComplete</c>) is deliberately NOT run,
        /// so an incomplete-but-well-formed draft still loads for further editing. See <see cref="GenerateUnitDraftAsync"/>
        /// for the callback contract.
        /// </summary>
        public void GenerateFactionDraftAsync(string prompt, FactionDraftContext ctx, Action<FactionDefinition?, string?> onComplete)
        {
            _factionCts?.Cancel();
            _factionCts = new CancellationTokenSource();
            RunDraftAsync(BuildFactionDraftPrompt(ctx), $"Create a faction for: {prompt}",
                json => ValidateFactionDraft(json, ctx), _factionCts.Token, onComplete,
                maxTokens: FACTION_DRAFT_MAX_TOKENS);
        }

        /// <summary>Cancel any in-flight draft generation (all four kinds).</summary>
        public void CancelDrafts()
        {
            _unitCts?.Cancel();
            _abilityCts?.Cancel();
            _heroCts?.Cancel();
            _factionCts?.Cancel();
            _balanceCts?.Cancel();
        }

        // ══════════════════════════════════════════════════════════════════════
        // Story 8.5 — AI balance analysis (editable, per-field suggestions)
        //
        // A provider-backed CRITIQUE flow mirroring the 8.4 draft framework: it rides the SAME no-fallback / four-state /
        // StripMarkdown pipeline (RunDraftAsync) and returns an EDITABLE BalanceReport of per-field BalanceSuggestions.
        // Nothing is auto-applied — the creator reviews/edits/discards each suggestion, and applying one routes the
        // proposed value through BalanceSuggestionApplier.TryApply, which re-gates it with the EXISTING
        // UnitDefinitionValidator on a clone (no second float→Fixed path, no bare-definition hash). Stays Godot-free.
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Asynchronously request an AI balance analysis of a faction's roster. The focus <paramref name="prompt"/> is
        /// the creator's steer ("melee feels weak", …); <paramref name="ctx"/> carries the roster unit ids the router
        /// validates suggestions against and the closed tunable-field vocabulary. The callback is marshalled to the main
        /// thread via <see cref="DrainEvents"/>. On success: callback(report, null) with a non-null
        /// <see cref="BalanceReport"/> whose every suggestion names an existing unit id + tunable field + proposed value
        /// + rationale. On any failure (unavailable provider / provider error / unparseable-or-invalid report):
        /// callback(null, message). A full-roster analysis needs a large budget — reuse the faction 8192-token figure.
        /// </summary>
        public void GenerateBalanceAnalysisAsync(
            string prompt, BalanceAnalysisContext ctx, Action<BalanceReport?, string?> onComplete)
        {
            _balanceCts?.Cancel();
            _balanceCts = new CancellationTokenSource();
            RunDraftAsync(
                BuildBalanceAnalysisPrompt(ctx),
                $"Analyze this faction for balance. Focus: {prompt}",
                json => ValidateBalanceReport(json, ctx),
                _balanceCts.Token, onComplete, maxTokens: FACTION_DRAFT_MAX_TOKENS);
        }

        /// <summary>Cancel any in-flight balance-analysis request.</summary>
        public void CancelBalanceAnalysis() => _balanceCts?.Cancel();

        /// <summary>Story 8.5: internal so the staleness-guard test can assert the prompt enumerates every member of
        /// <see cref="BalanceSuggestionApplier.TunableFields"/> (each heads its own line — exact-token match) and states
        /// the Fixed-safe range. A tunable-field member absent from this builder fails the guard.</summary>
        internal static string BuildBalanceAnalysisPrompt(BalanceAnalysisContext ctx)
        {
            IReadOnlyList<string> fields = ctx?.TunableFields != null && ctx.TunableFields.Count > 0
                ? ctx.TunableFields
                : BalanceSuggestionApplier.TunableFields;
            IReadOnlyList<string> unitIds = ctx?.UnitIds ?? System.Array.Empty<string>();

            var sb = new StringBuilder();
            sb.AppendLine("You are a balance analyst for Project Chimera, a real-time strategy game.");
            sb.AppendLine("Critique the balance of the faction roster below and propose concrete, field-specific tuning");
            sb.AppendLine("suggestions the creator (address them as \"Commander\") can review, edit, and apply.");
            sb.AppendLine();
            sb.AppendLine("=== EXISTING UNIT IDS (a suggestion's unit_id MUST be one of these) ===");
            sb.AppendLine(unitIds.Count > 0 ? string.Join(", ", unitIds) : "(none)");
            sb.AppendLine();
            sb.AppendLine("=== TUNABLE FIELDS (a suggestion's field MUST be exactly one of these) ===");
            foreach (string f in fields)
                sb.AppendLine(f);   // each field HEADS its own line (exact-token staleness guard)
            sb.AppendLine();
            sb.AppendLine("=== OUTPUT SCHEMA ===");
            sb.AppendLine(@"{
  ""suggestions"": [
    {
      ""unit_id"": ""<an existing unit id>"",
      ""field"": ""<one tunable field name from the list above>"",
      ""current"": <the field's current value, for display>,
      ""proposed"": <the new numeric value you recommend>,
      ""rationale"": ""<one terse sentence explaining the change>""
    }
  ]
}");
            sb.AppendLine();
            sb.AppendLine("=== INSTRUCTIONS ===");
            sb.AppendLine($"Every proposed value MUST be finite and within [0, {DraftFixedRange}) (the Fixed-safe range).");
            sb.AppendLine("Only propose changes to the listed tunable fields on the listed unit ids. Prefer a handful of");
            sb.AppendLine("high-impact, actionable suggestions over an exhaustive dump.");
            sb.AppendLine("Return ONLY valid JSON. No markdown fences, no explanation, no extra text.");
            return sb.ToString();
        }

        /// <summary>
        /// Parse a generated balance analysis into an editable <see cref="BalanceReport"/>, list-first-failing with a
        /// LOCATED error on any of: malformed JSON, a missing <c>suggestions</c> array, a suggestion whose <c>unit_id</c>
        /// is not in <see cref="BalanceAnalysisContext.UnitIds"/>, or whose <c>field</c> is not in the closed tunable set
        /// (<see cref="BalanceAnalysisContext.TunableFields"/>, defaulted from <see cref="BalanceSuggestionApplier"/>).
        /// Never mutates any faction/unit data — applying a suggestion is a separate, explicit, per-suggestion action
        /// through <see cref="BalanceSuggestionApplier.TryApply"/>.
        /// </summary>
        public static (BalanceReport? report, string? error) ValidateBalanceReport(string json, BalanceAnalysisContext ctx)
        {
            BalanceReport report;
            try
            {
                report = JsonSerializer.Deserialize<BalanceReport>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidOperationException("Deserialised to null.");
            }
            catch (Exception ex)
            {
                return (null, $"Invalid JSON: {ex.Message}");
            }

            if (report.Suggestions == null)
                return (null, "Balance report has no 'suggestions' array.");

            var knownFields = new HashSet<string>(
                ctx?.TunableFields != null && ctx.TunableFields.Count > 0 ? ctx.TunableFields : BalanceSuggestionApplier.TunableFields,
                StringComparer.Ordinal);
            var knownIds = new HashSet<string>(ctx?.UnitIds ?? System.Array.Empty<string>(), StringComparer.Ordinal);

            for (int i = 0; i < report.Suggestions.Count; i++)
            {
                BalanceSuggestion s = report.Suggestions[i];
                if (s == null)
                    return (null, $"suggestions[{i}] is null.");
                if (!knownIds.Contains(s.UnitId ?? ""))
                    return (null, $"suggestions[{i}].unit_id='{s.UnitId}' is not a unit in this faction's roster.");
                if (!knownFields.Contains(s.Field ?? ""))
                    return (null, $"suggestions[{i}].field='{s.Field}' is not a tunable balance field.");
            }

            return (report, null);
        }

        /// <summary>
        /// The shared draft pipeline (factored from <see cref="GenerateTriggerAsync"/>): snapshot settings on the caller
        /// thread, then on a worker thread run <c>TryCreate</c> (four-state + NO request on false) → <c>GenerateAsync</c>
        /// → <c>!Ok</c> four-state via <see cref="AiAvailabilityMap.FromFailure"/> → <see cref="StripMarkdown"/> →
        /// <paramref name="validate"/> → enqueue the callback. The selected provider is authoritative — no fallback.
        /// Every callback is marshalled through the existing <see cref="_queue"/>/<see cref="DrainEvents"/> seam.
        /// </summary>
        private void RunDraftAsync<T>(
            string systemPrompt,
            string userMsg,
            Func<string, (T? def, string? error)> validate,
            CancellationToken token,
            Action<T?, string?> onComplete,
            int maxTokens = MAX_TOKENS) where T : class
        {
            // Snapshot the authoritative settings on the caller thread (see GenerateTriggerAsync). No fallback.
            SettingsData settings = _getSettings();

            Task.Run(async () =>
            {
                try
                {
                    // Synchronous-unavailable (no provider / no key / bad host) short-circuits with the four-state
                    // message and NO network call.
                    if (!LlmProviderFactory.TryCreate(settings, _secretStore, _http,
                            out ILLMProvider? provider, out AiAvailability failure))
                    {
                        _queue.Enqueue(() => onComplete(null, AiAvailabilityMessages.Describe(failure)));
                        return;
                    }

                    NormalizedResult result = await provider!.GenerateAsync(
                        new NormalizedRequest(systemPrompt, userMsg, maxTokens), token);

                    if (!result.Ok)
                    {
                        // The selected provider's failure is surfaced (never masked) with the shared four-state microcopy.
                        _queue.Enqueue(() => onComplete(null,
                            AiAvailabilityMessages.Describe(AiAvailabilityMap.FromFailure(result.Failure))));
                        return;
                    }

                    (T? def, string? error) = validate(StripMarkdown(result.Text));
                    _queue.Enqueue(() => onComplete(def, def == null ? error : null));
                }
                catch (OperationCanceledException) { /* silently dropped */ }
                catch (Exception ex)
                {
                    _queue.Enqueue(() => onComplete(null, ex.Message));
                }
            }, token);
        }

        // ── Validate routers (public-static; each routes through the EXISTING per-kind gate) ──

        /// <summary>
        /// Deserialize a generated unit through the lenient <see cref="FactionDefinition.JsonOptions"/> (plain float — the
        /// SAME options hand-authored units load with) and gate it with <see cref="UnitDefinitionValidator"/>. On any
        /// located error returns <c>(null, message)</c> naming the field path + offending value; never quantizes here
        /// (quantization stays at <c>EntityWorld.ApplyUnitDefinition</c>).
        /// </summary>
        public static (UnitDefinition? def, string? error) ValidateUnitDraft(string json, UnitDraftContext ctx)
        {
            if (!TryDeserializeUnit(json, out UnitDefinition? def, out string? jsonError))
                return (null, jsonError);
            return GateUnit(def!, ctx);
        }

        /// <summary>
        /// Like <see cref="ValidateUnitDraft"/> but additionally requires the HERO designation (<c>is_hero:true</c> + a
        /// non-null <c>hero</c> block) BEFORE the shared unit gate — a well-formed non-hero unit is rejected with a
        /// located error so <see cref="GenerateHeroDraftAsync"/> never silently yields a plain unit.
        /// </summary>
        public static (UnitDefinition? def, string? error) ValidateHeroDraft(string json, UnitDraftContext ctx)
        {
            if (!TryDeserializeUnit(json, out UnitDefinition? def, out string? jsonError))
                return (null, jsonError);
            if (!def!.IsHero || def.Hero == null)
                return (null, $"hero '{def.Id}'.is_hero: a hero draft requires is_hero:true and a 'hero' block " +
                    $"(got is_hero={def.IsHero}, hero={(def.Hero == null ? "null" : "present")}).");
            return GateUnit(def, ctx);
        }

        /// <summary>
        /// Route a generated ability through the EXACT hand-authored path: <see cref="AbilityLoader.Load"/> deserializes
        /// via <see cref="ContentJson.Options"/> (<see cref="FixedJsonConverter"/> quantizes each number to
        /// <see cref="Fixed"/> and rejects <c>|v| &gt;= 32768</c>/NaN/Inf at parse) and runs <see cref="AbilityValidator"/>.
        /// On failure returns <c>(null, located error)</c>.
        /// </summary>
        public static (AbilityDefinition? def, string? error) ValidateAbilityDraft(string json, AbilityDraftContext ctx)
        {
            AbilityValidationResult r = AbilityLoader.Load(json, "ai-draft");
            return r.Ok ? (r.Value.Value, null) : (null, r.Error);
        }

        /// <summary>
        /// Deserialize a generated faction through <see cref="FactionDefinition.JsonOptions"/>, run the structural
        /// <see cref="FactionValidator.Validate"/> gate, AND loop <see cref="UnitDefinitionValidator"/> over
        /// <c>def.Units</c> (the deep per-unit validation bare faction load skips — closing that gap). Does NOT run
        /// <see cref="FactionValidator.ValidateComplete"/> (roster-completeness stays at the selectable boundary), so an
        /// incomplete-but-well-formed draft still loads for editing.
        /// </summary>
        public static (FactionDefinition? def, string? error) ValidateFactionDraft(string json, FactionDraftContext ctx)
        {
            FactionDefinition def;
            try
            {
                def = JsonSerializer.Deserialize<FactionDefinition>(json, FactionDefinition.JsonOptions)
                    ?? throw new InvalidOperationException("Deserialised to null.");
            }
            catch (Exception ex)
            {
                return (null, $"Invalid JSON: {ex.Message}");
            }

            FactionValidationResult structural = FactionValidator.Validate(def);
            if (!structural.Ok)
                return (null, JoinErrors(structural.Errors));

            // Deep-validate each unit (the gap bare faction load leaves — see Design Notes). A per-unit failure names
            // the offending unit + field.
            var validator = new UnitDefinitionValidator();
            if (def.Units != null)
            {
                foreach (UnitDefinition u in def.Units)
                {
                    if (u == null) continue;
                    UnitValidationResult ur = validator.Validate(
                        u, ctx.AbilityRegistry, ctx.BehaviorRegistry, ctx.ItemRegistry, def.Units, "unit");
                    if (!ur.Ok)
                        return (null, JoinErrors(ur.Errors));
                }
            }

            return (def, null);
        }

        // ── Router helpers ────────────────────────────────────────────────────

        private static bool TryDeserializeUnit(string json, out UnitDefinition? def, out string? error)
        {
            try
            {
                def = JsonSerializer.Deserialize<UnitDefinition>(json, FactionDefinition.JsonOptions)
                    ?? throw new InvalidOperationException("Deserialised to null.");
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                def = null;
                error = $"Invalid JSON: {ex.Message}";
                return false;
            }
        }

        private static (UnitDefinition? def, string? error) GateUnit(UnitDefinition def, UnitDraftContext ctx)
        {
            UnitValidationResult result = new UnitDefinitionValidator().Validate(
                def, ctx.AbilityRegistry, ctx.BehaviorRegistry, ctx.ItemRegistry, ctx.Siblings, "unit");
            return result.Ok ? (def, null) : (null, JoinErrors(result.Errors));
        }

        /// <summary>Join every located field error into one message (list-all validators return several); each message
        /// already carries its path + offending value.</summary>
        private static string JoinErrors(IReadOnlyList<(string FieldPath, string Message)> errors)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < errors.Count; i++)
            {
                if (i > 0) sb.Append(" | ");
                sb.Append(errors[i].Message);
            }
            return sb.ToString();
        }

        // ── Draft prompt builders (internal-static; staleness-guardable) ──────

        /// <summary>The Fixed-safe numeric ceiling every generated stat must stay below (mirrors
        /// <c>FixedJsonConverter.FixedRangeLimit</c> / <c>UnitDefinitionValidator</c>'s Range).</summary>
        private const int DraftFixedRange = 32768;

        /// <summary>Story 8.4: internal so the staleness-guard test can assert the prompt states the Fixed-safe range and
        /// the archetype+ability composition guidance (a draft is archetype + ability/behavior composition, never a bespoke
        /// subclass).</summary>
        internal static string BuildUnitDraftPrompt(UnitDraftContext ctx)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are a unit-authoring assistant for Project Chimera, a real-time strategy game.");
            sb.AppendLine("Convert the user's description into a valid JSON UnitDefinition object (snake_case keys).");
            sb.AppendLine();
            AppendUnitSchema(sb);
            sb.AppendLine();
            sb.AppendLine("=== ARCHETYPE + ABILITY COMPOSITION ===");
            sb.AppendLine("Express behavior via archetype (category) + ability/behavior composition — NEVER a bespoke");
            sb.AppendLine("subclass. A \"healer\" is a Ranged archetype composed with a heal ability + a support behavior.");
            sb.AppendLine("Only reference ability/behavior ids from the AVAILABLE lists below; an unknown id is rejected.");
            sb.AppendLine();
            AppendUnitContext(sb, ctx);
            sb.AppendLine("=== INSTRUCTIONS ===");
            sb.AppendLine($"Every numeric stat MUST be finite and within [0, {DraftFixedRange}) (the Fixed-safe range).");
            sb.AppendLine("Return ONLY valid JSON. No markdown fences, no explanation, no extra text.");
            return sb.ToString();
        }

        /// <summary>Story 8.4: internal so the staleness-guard test can assert the HERO prompt states the Fixed-safe range,
        /// composition guidance, AND that a hero requires <c>is_hero:true</c> + a <c>hero</c> block.</summary>
        internal static string BuildHeroDraftPrompt(UnitDraftContext ctx)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are a hero-authoring assistant for Project Chimera, a real-time strategy game.");
            sb.AppendLine("A hero is a UnitDefinition with is_hero:true AND a 'hero' block (leveling curve + ability slots).");
            sb.AppendLine("Convert the user's description into a valid JSON UnitDefinition object (snake_case keys).");
            sb.AppendLine();
            AppendUnitSchema(sb);
            sb.AppendLine("The 'hero' block (REQUIRED — is_hero MUST be true):");
            sb.AppendLine(@"{
  ""max_level"": 5,          // 2..100
  ""base_xp"": 100.0,        // > 0
  ""xp_growth"": 1.4,        // 1..100
  ""xp_per_kill"": 40.0,
  ""xp_share_radius"": 20.0, // < 128
  ""health_per_level"": 25.0,
  ""damage_per_level"": 4.0,
  ""armor_per_level"": 1.0,
  ""signature_ability"": ""<ability_id or empty>"",
  ""ultimate_ability"": ""<ability_id or empty>""
}");
            sb.AppendLine();
            sb.AppendLine("=== ARCHETYPE + ABILITY COMPOSITION ===");
            sb.AppendLine("Express behavior via archetype (category) + ability/behavior composition — NEVER a bespoke");
            sb.AppendLine("subclass. Signature and ultimate ability ids must differ and reference AVAILABLE ability ids.");
            sb.AppendLine();
            AppendUnitContext(sb, ctx);
            sb.AppendLine("=== INSTRUCTIONS ===");
            sb.AppendLine("Set is_hero:true and author the 'hero' block — a hero draft is rejected without both.");
            sb.AppendLine($"Every numeric stat MUST be finite and within [0, {DraftFixedRange}) (the Fixed-safe range).");
            sb.AppendLine("Return ONLY valid JSON. No markdown fences, no explanation, no extra text.");
            return sb.ToString();
        }

        /// <summary>Story 8.4: internal so the staleness-guard test can assert the prompt enumerates every closed-set member
        /// of the effect-kind, targeting, and activation vocabularies (each heads its own line — exact-token match).</summary>
        internal static string BuildAbilityDraftPrompt(AbilityDraftContext ctx)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are an ability-authoring assistant for Project Chimera, a real-time strategy game.");
            sb.AppendLine("Convert the user's description into a valid JSON AbilityDefinition object (snake_case keys).");
            sb.AppendLine();
            sb.AppendLine("=== ABILITY SCHEMA ===");
            sb.AppendLine(@"{
  ""id"": ""string (lowercase_snake_case)"",
  ""display_name"": ""string"",
  ""targeting"": ""None"" | ""Self"" | ""TargetUnit"" | ""GroundPoint"",
  ""activation"": ""active"" | ""aura"" | ""on_hit"" | ""while_alive"",
  ""cost_energy"": 0.0,
  ""cost_ore"": 0,
  ""cost_crystal"": 0,
  ""cost_health"": 0,
  ""cooldown"": 0.0,
  ""effect"": { EffectNode }
}");
            sb.AppendLine();
            // Each targeting/activation token HEADS its own line so the staleness guard's exact-line-token match holds.
            sb.AppendLine("=== VALID TARGETING TYPES ===");
            sb.AppendLine("None        — no target (passive)");
            sb.AppendLine("Self        — the caster is the target");
            sb.AppendLine("TargetUnit  — the player selects one unit");
            sb.AppendLine("GroundPoint — the player picks a ground point");
            sb.AppendLine();
            sb.AppendLine("=== VALID ACTIVATION TYPES ===");
            sb.AppendLine("active      — player-cast ability (default)");
            sb.AppendLine("aura        — while-alive aura (SearchArea → apply_modifier, refreshed each tick)");
            sb.AppendLine("on_hit      — rider that fires when this unit's attack lands");
            sb.AppendLine("while_alive — permanent/periodic self-passive installed at spawn");
            sb.AppendLine();
            sb.AppendLine("=== VALID EFFECT KINDS (the 'kind' field on every effect node) ===");
            sb.AppendLine("direct_hp_delta — { \"kind\": \"direct_hp_delta\", \"delta\": float }");
            sb.AppendLine("heal            — { \"kind\": \"heal\", \"amount\": float }");
            sb.AppendLine("damage          — { \"kind\": \"damage\", \"amount\": float, \"damage_type\": \"Normal\" }");
            sb.AppendLine("apply_modifier  — { \"kind\": \"apply_modifier\", \"modifier\": { … } }");
            sb.AppendLine("sequence        — { \"kind\": \"sequence\", \"children\": [ EffectNode, … ] }");
            sb.AppendLine("search_area     — { \"kind\": \"search_area\", \"radius\": float, \"child\": EffectNode }");
            sb.AppendLine("persistent      — { \"kind\": \"persistent\", \"period_ticks\": int, \"period_count\": int, … }");
            sb.AppendLine();
            if (ctx?.ExistingAbilityIds != null && ctx.ExistingAbilityIds.Count > 0)
            {
                sb.AppendLine("=== EXISTING ABILITY IDS (avoid colliding) ===");
                sb.AppendLine(string.Join(", ", ctx.ExistingAbilityIds));
                sb.AppendLine();
            }
            sb.AppendLine("=== INSTRUCTIONS ===");
            sb.AppendLine($"Every numeric value MUST be finite and within [0, {DraftFixedRange}) (the Fixed-safe range).");
            sb.AppendLine("An ability must declare at least one effect node. Costs/cooldown must be >= 0.");
            sb.AppendLine("Return ONLY valid JSON. No markdown fences, no explanation, no extra text.");
            return sb.ToString();
        }

        /// <summary>Story 8.4: internal so the staleness-guard test can assert the prompt enumerates every closed-set
        /// <c>ai_preset</c> member (from the trusted context, seeded from <c>FactionValidator.KnownAiPresets</c>).</summary>
        internal static string BuildFactionDraftPrompt(FactionDraftContext ctx)
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are a faction-authoring assistant for Project Chimera, a real-time strategy game.");
            sb.AppendLine("Convert the user's description into a valid JSON FactionDefinition object (snake_case keys).");
            sb.AppendLine();
            sb.AppendLine("=== FACTION SCHEMA ===");
            sb.AppendLine(@"{
  ""id"": ""string (lowercase_snake_case)"",
  ""display_name"": ""string"",
  ""color"": [0.2, 0.5, 1.0, 1.0],   // [r, g, b, a] in [0, 1]
  ""ai_preset"": ""balanced"",
  ""units"": [ UnitDefinition, … ],
  ""buildings"": [ BuildingDefinition, … ]
}");
            sb.AppendLine();
            sb.AppendLine("=== VALID AI PRESETS ===");
            IReadOnlyList<string> presets = ctx?.AiPresets != null && ctx.AiPresets.Count > 0
                ? ctx.AiPresets
                : new[] { "balanced" };
            foreach (string p in presets)
                sb.AppendLine($"{p}");   // each preset HEADS its own line (exact-token staleness guard)
            sb.AppendLine();
            sb.AppendLine("=== UNIT SCHEMA (per entry in 'units') ===");
            AppendUnitSchema(sb);
            sb.AppendLine("=== ARCHETYPE + ABILITY COMPOSITION ===");
            sb.AppendLine("Express each unit's behavior via archetype (category) + ability/behavior composition — NEVER a");
            sb.AppendLine("bespoke subclass. Include at least one Worker unit and one combat unit for a playable roster.");
            sb.AppendLine();
            AppendUnitContext(sb, new UnitDraftContext
            {
                AbilityRegistry = ctx?.AbilityRegistry,
                BehaviorRegistry = ctx?.BehaviorRegistry,
                ItemRegistry = ctx?.ItemRegistry,
            });
            sb.AppendLine("=== INSTRUCTIONS ===");
            sb.AppendLine($"Every numeric stat MUST be finite and within [0, {DraftFixedRange}) (the Fixed-safe range).");
            sb.AppendLine("Return ONLY valid JSON. No markdown fences, no explanation, no extra text.");
            return sb.ToString();
        }

        /// <summary>The shared UnitDefinition JSON schema block (snake_case), reused by the unit/hero/faction prompts.</summary>
        private static void AppendUnitSchema(StringBuilder sb)
        {
            sb.AppendLine("=== UNIT SCHEMA ===");
            sb.AppendLine(@"{
  ""id"": ""string (lowercase_snake_case)"",
  ""display_name"": ""string"",
  ""category"": ""Worker"" | ""Melee"" | ""Ranged"" | ""Siege"" | ""Air"" | ""Structure"",
  ""hp"": 100.0,
  ""speed"": 4.0,
  ""attack_damage"": 10.0,
  ""attack_range"": 5.0,
  ""attack_speed"": 1.0,
  ""damage_type"": ""Normal"" | ""Pierce"" | ""Siege"" | ""Magic"",
  ""armor_type"": ""Unarmored"" | ""Light"" | ""Medium"" | ""Heavy"" | ""Fortified"",
  ""armor"": 0.0,
  ""cost_ore"": 50,
  ""cost_crystal"": 0,
  ""supply"": 1,
  ""vision_range"": 8.0,
  ""abilities"": [ ""<ability_id>"", … ],
  ""behaviors"": [ ""<behavior_id>"", … ]
}");
        }

        /// <summary>Append the AVAILABLE ability/behavior/item ids from the draft context so the model composes only with
        /// resolvable references (an unknown ref is a located reject at the validate gate).</summary>
        private static void AppendUnitContext(StringBuilder sb, UnitDraftContext ctx)
        {
            sb.AppendLine("=== AVAILABLE COMPOSITION IDS ===");
            sb.AppendLine("Ability ids: " + IdList(ctx?.AbilityRegistry));
            sb.AppendLine("Behavior ids: " + BehaviorIdList(ctx?.BehaviorRegistry));
            sb.AppendLine("Item ids: " + ItemIdList(ctx?.ItemRegistry));
            sb.AppendLine();
        }

        private static string IdList(AbilityRegistry? reg)
        {
            if (reg == null || reg.Count == 0) return "(none)";
            var ids = new string[reg.Count];
            for (int i = 0; i < reg.Count; i++) ids[i] = reg.Get(i).Id;
            return string.Join(", ", ids);
        }

        private static string BehaviorIdList(BehaviorRegistry? reg)
        {
            if (reg == null || reg.Count == 0) return "(none)";
            var ids = new string[reg.Count];
            for (int i = 0; i < reg.Count; i++) ids[i] = reg.Get(i).Id;
            return string.Join(", ", ids);
        }

        private static string ItemIdList(ItemRegistry? reg)
        {
            if (reg == null || reg.Count == 0) return "(none)";
            var ids = new string[reg.Count];
            for (int i = 0; i < reg.Count; i++) ids[i] = reg.Get(i).Id;
            return string.Join(", ", ids);
        }
    }

    // ── Story 8.4 draft context DTOs (Godot-free) ──────────────────────────────

    /// <summary>
    /// Context for a UNIT (and HERO) draft — the loaded registries the validator gates ability/behavior/item refs
    /// against, plus the faction's existing units (for the uniqueness rule). All optional: a null registry SKIPS its
    /// reference check (the validator's documented null-registry semantics), exactly as hand-authored editor validation.
    /// </summary>
    public sealed class UnitDraftContext
    {
        /// <summary>Loaded abilities the unit may compose (null skips the ability-ref check).</summary>
        public AbilityRegistry? AbilityRegistry { get; set; }

        /// <summary>Loaded behaviors the unit may compose (null skips the behavior-ref check).</summary>
        public BehaviorRegistry? BehaviorRegistry { get; set; }

        /// <summary>Loaded items a shop unit may stock (null skips the shop-stock-ref check).</summary>
        public ItemRegistry? ItemRegistry { get; set; }

        /// <summary>The faction's existing units, for the duplicate-id rule (null skips the uniqueness check).</summary>
        public IReadOnlyList<UnitDefinition>? Siblings { get; set; }
    }

    /// <summary>
    /// Context for an ABILITY draft. Ability validation is self-contained (<see cref="AbilityLoader"/>/
    /// <see cref="AbilityValidator"/> need no external registry), so this carries only optional existing-id hints the
    /// prompt lists so the model avoids id collisions.
    /// </summary>
    public sealed class AbilityDraftContext
    {
        /// <summary>Existing ability ids the prompt lists as "avoid colliding" (purely a prompt hint; not a gate).</summary>
        public IReadOnlyList<string>? ExistingAbilityIds { get; set; }
    }

    /// <summary>
    /// Context for a FACTION draft — the registries used to deep-validate each generated unit, plus the closed-set
    /// <c>ai_preset</c> members (seeded from <c>FactionValidator.KnownAiPresets</c>) and signature-mechanic id hints the
    /// prompt enumerates.
    /// </summary>
    public sealed class FactionDraftContext
    {
        /// <summary>Loaded abilities each generated unit may compose (deep per-unit validation).</summary>
        public AbilityRegistry? AbilityRegistry { get; set; }

        /// <summary>Loaded behaviors each generated unit may compose.</summary>
        public BehaviorRegistry? BehaviorRegistry { get; set; }

        /// <summary>Loaded items a shop unit may stock.</summary>
        public ItemRegistry? ItemRegistry { get; set; }

        /// <summary>The closed set of recognized <c>ai_preset</c> ids the prompt enumerates (staleness-guarded).</summary>
        public IReadOnlyList<string> AiPresets { get; set; } = System.Array.Empty<string>();

        /// <summary>Optional signature-mechanic id hints the prompt may list (not a gate this story).</summary>
        public IReadOnlyList<string> SignatureIds { get; set; } = System.Array.Empty<string>();
    }

    // ── Story 8.5 balance-analysis DTOs (Godot-free) ───────────────────────────

    /// <summary>
    /// Context for a balance analysis — the faction's live roster ids the router validates every suggestion's
    /// <c>unit_id</c> against, plus the closed tunable-field vocabulary (defaulted from
    /// <see cref="BalanceSuggestionApplier.TunableFields"/> — the single source of truth shared by the prompt builder,
    /// the validate router, and the apply mapper).
    /// </summary>
    public sealed class BalanceAnalysisContext
    {
        /// <summary>The roster unit ids a suggestion may target; a suggestion citing an id outside this set is a located reject.</summary>
        public IReadOnlyList<string> UnitIds { get; set; } = System.Array.Empty<string>();

        /// <summary>The closed set of tunable field names (defaults to <see cref="BalanceSuggestionApplier.TunableFields"/>).</summary>
        public IReadOnlyList<string> TunableFields { get; set; } = BalanceSuggestionApplier.TunableFields;
    }

    /// <summary>One editable, per-field balance suggestion: a target unit id, a tunable field (snake_case), the proposed
    /// new value, the current value (advisory/display only), and a one-line rationale. Nothing is auto-applied — the
    /// creator reviews/edits/discards it, and an applied value is re-gated through
    /// <see cref="BalanceSuggestionApplier.TryApply"/>.</summary>
    public sealed class BalanceSuggestion
    {
        [JsonPropertyName("unit_id")]   public string UnitId { get; set; } = "";
        [JsonPropertyName("field")]     public string Field { get; set; } = "";
        [JsonPropertyName("proposed")]  public double Proposed { get; set; }
        [JsonPropertyName("current")]   public double Current { get; set; }
        [JsonPropertyName("rationale")] public string Rationale { get; set; } = "";
    }

    /// <summary>The parsed, editable result of a balance analysis — a flat list of per-field <see cref="BalanceSuggestion"/>s.</summary>
    public sealed class BalanceReport
    {
        [JsonPropertyName("suggestions")]
        public List<BalanceSuggestion> Suggestions { get; set; } = new();
    }
}
