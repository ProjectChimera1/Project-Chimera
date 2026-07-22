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
        /// 1. Schema — deserialization succeeds. (UNIVERSAL — always runs.)
        /// 2. Player slots — at least <see cref="MapGeneratorContext.MinPlayerSlots"/> (RTS default 2); faction paths
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
    }
}
