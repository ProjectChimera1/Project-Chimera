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

                    // Try Claude API first.
                    if (!string.IsNullOrEmpty(AnthropicApiKey))
                        (json, error) = await TryClaudeAsync(prompt, description, token);

                    // Fallback: local Ollama.
                    if (json == null)
                        (json, error) = await TryOllamaAsync(prompt, description, token);

                    if (json == null)
                    {
                        string msg = error ?? "Both Claude and Ollama are unavailable.";
                        _queue.Enqueue(() => onComplete(null, msg));
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
            string systemPrompt, string userDescription, CancellationToken ct)
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
                        new { role = "user", content = $"Create a trigger for: {userDescription}" }
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
            string systemPrompt, string userDescription, CancellationToken ct)
        {
            try
            {
                var body = new
                {
                    model  = OLLAMA_MODEL,
                    prompt = $"{systemPrompt}\n\nCreate a trigger for: {userDescription}",
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
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
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
                if (a.Type == "create_timer" && a.TimerSeconds <= 0)
                    return (null, $"create_timer '{a.TimerName}' has invalid duration {a.TimerSeconds}s.");
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
    }
}
