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
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;

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
    }

    /// <summary>
    /// Translates natural language descriptions into validated TriggerDefinition JSON
    /// using the Claude API (cloud) with an optional Ollama fallback (local).
    ///
    /// Pure C# — no Godot dependency. Uses System.Net.Http.HttpClient directly.
    /// Follows the ConcurrentQueue/DrainEvents pattern used by NakamaService and ModIoService.
    /// Call DrainEvents() once per _Process frame to marshal callbacks to the main thread.
    /// </summary>
    public class LLMService
    {
        // ── Configuration ─────────────────────────────────────────────────────

        private const string CLAUDE_URL    = "https://api.anthropic.com/v1/messages";
        private const string CLAUDE_MODEL  = "claude-sonnet-4-6";
        private const string CLAUDE_VERSION = "2023-06-01";

        private const string OLLAMA_URL    = "http://localhost:11434/api/generate";
        private const string OLLAMA_MODEL  = "llama3.1:8b";

        private const int    MAX_TOKENS    = 2048;
        private const int    TIMEOUT_MS    = 30_000;

        // Safety cap: spawn_unit count is clamped to this in validation, independently
        // of the schema comment in the prompt.
        private const int    MAX_SPAWN_COUNT = 50;

        // ── Internal state ────────────────────────────────────────────────────

        private readonly HttpClient _http;
        private readonly ConcurrentQueue<Action> _queue = new();
        private CancellationTokenSource? _cts;
        private CancellationTokenSource? _mapCts;

        /// <summary>Set before calling GenerateTriggerAsync. Empty string disables cloud.</summary>
        public string AnthropicApiKey { get; set; } = "";

        // ── Construction ──────────────────────────────────────────────────────

        public LLMService()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(TIMEOUT_MS) };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("ProjectChimera/1.0");
        }

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

            Task.Run(async () =>
            {
                try
                {
                    string prompt  = BuildSystemPrompt(context);
                    string? json   = null;
                    string? error  = null;
                    string msg     = $"Create a trigger for: {description}";

                    // Try Claude API first.
                    if (!string.IsNullOrEmpty(AnthropicApiKey))
                        (json, error) = await TryClaudeAsync(prompt, msg, token);

                    // Fallback: local Ollama.
                    if (json == null)
                        (json, error) = await TryOllamaAsync(prompt, msg, token);

                    if (json == null)
                    {
                        string fallback = error ?? "Both Claude and Ollama are unavailable.";
                        _queue.Enqueue(() => onComplete(null, fallback));
                        return;
                    }

                    // Validate the generated JSON.
                    var (trigger, validationError) = Validate(json, context);
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

        // ── Claude API ────────────────────────────────────────────────────────

        private async Task<(string? json, string? error)> TryClaudeAsync(
            string systemPrompt, string userMessage, CancellationToken ct)
        {
            try
            {
                var body = new
                {
                    model      = CLAUDE_MODEL,
                    max_tokens = MAX_TOKENS,
                    system     = systemPrompt,
                    messages   = new[]
                    {
                        new { role = "user", content = userMessage }
                    }
                };

                var req = new HttpRequestMessage(HttpMethod.Post, CLAUDE_URL)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(body),
                        Encoding.UTF8, "application/json")
                };
                req.Headers.Add("x-api-key", AnthropicApiKey);
                req.Headers.Add("anthropic-version", CLAUDE_VERSION);

                var resp = await _http.SendAsync(req, ct);
                string raw = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                    return (null, $"Claude API error {(int)resp.StatusCode}: {raw}");

                // Extract text from content[0].text
                using var doc = JsonDocument.Parse(raw);
                string text = doc.RootElement
                    .GetProperty("content")[0]
                    .GetProperty("text").GetString() ?? "";

                return (StripMarkdown(text), null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (null, $"Claude unreachable: {ex.Message}");
            }
        }

        // ── Ollama fallback ───────────────────────────────────────────────────

        private async Task<(string? json, string? error)> TryOllamaAsync(
            string systemPrompt, string userMessage, CancellationToken ct)
        {
            try
            {
                var body = new
                {
                    model  = OLLAMA_MODEL,
                    prompt = $"{systemPrompt}\n\n{userMessage}",
                    stream = false
                };

                var req = new HttpRequestMessage(HttpMethod.Post, OLLAMA_URL)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(body),
                        Encoding.UTF8, "application/json")
                };

                var resp = await _http.SendAsync(req, ct);
                string raw = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                    return (null, $"Ollama error {(int)resp.StatusCode}");

                using var doc = JsonDocument.Parse(raw);
                string text = doc.RootElement.GetProperty("response").GetString() ?? "";
                return (StripMarkdown(text), null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (null, $"Ollama unreachable: {ex.Message}");
            }
        }

        // ── Validation pipeline ───────────────────────────────────────────────

        /// <summary>
        /// Five-pass validation:
        /// 1. Schema — can JSON be deserialized to TriggerDefinition?
        /// 2. Faction slots — 0 or 1 only
        /// 3. BuildingType strings — must match BuildingType enum
        /// 4. Operators — only the six standard comparison symbols
        /// 5. Range / safety — counts ≤ 50, durations > 0, spawn inside bounds
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

            // Pass 2 — faction slots.
            foreach (var ev in trigger.Events)
                if (ev.Faction is not (0 or 1))
                    return (null, $"Event '{ev.Type}' has invalid faction slot {ev.Faction} (must be 0 or 1).");
            foreach (var c in trigger.Conditions)
                if (c.Faction is not (0 or 1))
                    return (null, $"Condition '{c.Type}' has invalid faction slot {c.Faction}.");
            foreach (var a in trigger.Actions)
                if (a.Faction is not (0 or 1))
                    return (null, $"Action '{a.Type}' has invalid faction slot {a.Faction}.");

            // Pass 3 — building type strings.
            foreach (var ev in trigger.Events)
                if (!string.IsNullOrEmpty(ev.BuildingType)
                    && !Enum.TryParse<BuildingType>(ev.BuildingType, out _))
                    return (null, $"Unknown building_type '{ev.BuildingType}'. " +
                        $"Valid: {string.Join(", ", Enum.GetNames(typeof(BuildingType)))}");
            foreach (var c in trigger.Conditions)
                if (!string.IsNullOrEmpty(c.BuildingType)
                    && !Enum.TryParse<BuildingType>(c.BuildingType, out _))
                    return (null, $"Unknown building_type '{c.BuildingType}'.");

            // Pass 4 — operator strings.
            var validOps = new HashSet<string> { ">", "<", ">=", "<=", "==", "!=" };
            foreach (var ev in trigger.Events)
                if (!string.IsNullOrEmpty(ev.Operator) && !validOps.Contains(ev.Operator))
                    return (null, $"Invalid operator '{ev.Operator}' in event '{ev.Type}'.");
            foreach (var c in trigger.Conditions)
                if (!string.IsNullOrEmpty(c.Operator) && !validOps.Contains(c.Operator))
                    return (null, $"Invalid operator '{c.Operator}' in condition '{c.Type}'.");

            // Pass 5 — range and safety.
            foreach (var a in trigger.Actions)
            {
                if (a.Type == "spawn_unit")
                {
                    if (a.Count <= 0 || a.Count > MAX_SPAWN_COUNT)
                        a.Count = Math.Clamp(a.Count, 1, MAX_SPAWN_COUNT); // auto-clamp rather than reject
                    float b = context.MapBounds;
                    if (a.X < -b || a.X > b || a.Z < -b || a.Z > b)
                        return (null, $"spawn_unit position ({a.X}, {a.Z}) is outside map bounds ±{b}.");
                }
                if (a.Type == "create_timer" && a.TimerSeconds <= Fixed.Zero)
                    return (null, $"create_timer '{a.TimerName}' has invalid duration {a.TimerSeconds.ToFloat()}s.");
                if (a.Type == "display_message" && a.Duration <= 0)
                    a.Duration = 4f; // auto-fix
            }

            return (trigger, null);
        }

        // ── Prompt builder ────────────────────────────────────────────────────

        private static string BuildSystemPrompt(ScenarioContext ctx)
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
            sb.AppendLine("=== VALID EVENT TYPES ===");
            sb.AppendLine(@"match_start              — no additional fields
unit_dies               — faction (0=Player1, 1=Player2)
building_completed      — faction, building_type (""CommandCenter""|""Barracks""|""ArcheryRange""|""SiegeWorkshop"")
timer_expires           — timer_name (string)
resource_threshold      — faction, amount (float), operator
unit_count_threshold    — faction, count (int), operator");
            sb.AppendLine();
            sb.AppendLine("=== VALID CONDITION TYPES ===");
            sb.AppendLine(@"always                  — always true
building_exists         — faction, building_type
resource_comparison     — faction, amount (float), operator
unit_count              — faction, count (int), operator
variable_comparison     — variable (string), value (int), operator");
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
        private static string StripMarkdown(string text)
        {
            text = text.Trim();
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

            Task.Run(async () =>
            {
                try
                {
                    string prompt = BuildMapSystemPrompt(context);
                    string? json  = null;
                    string? error = null;
                    string msg    = $"Create a map scenario for: {description}";

                    if (!string.IsNullOrEmpty(AnthropicApiKey))
                        (json, error) = await TryClaudeAsync(prompt, msg, token);

                    if (json == null)
                        (json, error) = await TryOllamaAsync(prompt, msg, token);

                    if (json == null)
                    {
                        string fallback = error ?? "Both Claude and Ollama are unavailable.";
                        _queue.Enqueue(() => onComplete(null, fallback));
                        return;
                    }

                    var (scenario, validationError) = ValidateScenario(json, context);
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
        /// 1. Schema — deserialization succeeds.
        /// 2. Player slots — exactly 2; faction paths forced to known values.
        /// 3. Building types — only valid BuildingType enum names.
        /// 4. Unit IDs — only IDs present in MapGeneratorContext.UnitIds.
        /// 5. Position bounds — all X/Z within ±MapBounds.
        /// 6. Ore node spacing — every pair at least 15 units apart.
        /// 7. Pre-placed unit count — at most 6 non-worker units per faction slot.
        /// Returns (null, errorMessage) on failure, (scenario, null) on success.
        /// </summary>
        public static (ScenarioData? scenario, string? error) ValidateScenario(
            string json, MapGeneratorContext context)
        {
            // Pass 1 — schema.
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

            // Pass 2 — player slots.
            if (scenario.PlayerSlots.Length < 2)
                return (null, $"Expected 2 player slots, got {scenario.PlayerSlots.Length}.");

            // Force faction JSON paths to known valid values — LLMs often hallucinate these.
            foreach (var slot in scenario.PlayerSlots)
            {
                slot.FactionJson = slot.Slot == 0
                    ? context.Slot0FactionJson
                    : context.Slot1FactionJson;
            }

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

            // Pass 5 — position bounds.
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

            // Pass 6 — ore node spacing ≥ 15u.
            for (int i = 0; i < scenario.ResourceNodes.Length; i++)
                for (int j = i + 1; j < scenario.ResourceNodes.Length; j++)
                {
                    float dx = scenario.ResourceNodes[i].X - scenario.ResourceNodes[j].X;
                    float dz = scenario.ResourceNodes[i].Z - scenario.ResourceNodes[j].Z;
                    float dist = (float)Math.Sqrt(dx * dx + dz * dz);
                    if (dist < 15f)
                        return (null, $"Ore nodes {i} and {j} are {dist:F1}u apart (minimum 15u).");
                }

            // Pass 7 — pre-placed combat units per slot ≤ 6.
            var combatCount = new Dictionary<int, int>();
            foreach (var u in scenario.Units)
                if (!string.Equals(u.UnitId, "worker", StringComparison.OrdinalIgnoreCase))
                    combatCount[u.Slot] = combatCount.GetValueOrDefault(u.Slot) + 1;
            foreach (var kv in combatCount)
                if (kv.Value > 6)
                    return (null, $"Slot {kv.Key} has {kv.Value} pre-placed combat units (max 6).");

            return (scenario, null);
        }

        // ── Map system prompt ─────────────────────────────────────────────────

        private static string BuildMapSystemPrompt(MapGeneratorContext ctx)
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
            sb.AppendLine("- Player 1 (slot 0): base near X=-45, Z=0. Player 2 (slot 1): base near X=45, Z=0.");
            sb.AppendLine("- Each slot MUST have a CommandCenter (pre_built=true) at its base position.");
            sb.AppendLine("- Ore nodes must be spaced at least 15 units apart from every other ore node.");
            sb.AppendLine("- Use 4–12 resource nodes. Supply 200–2000, rate 3–10.");
            sb.AppendLine("- Pre-place at most 6 combat (non-worker) units per faction slot.");
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
