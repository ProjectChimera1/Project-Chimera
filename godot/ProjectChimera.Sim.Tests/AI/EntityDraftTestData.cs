#nullable enable
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using ProjectChimera.AI;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// Story 8.4 — shared fixtures + the <c>DrainEvents()</c> pump for the entity-draft tests (mirrors
    /// <c>LlmServiceRepointTests</c>'s helpers). Reused by <see cref="EntityDraftGenerationTests"/>,
    /// <see cref="EntityDraftValidationTests"/>, and <see cref="EntityDraftQuantizeTests"/>. Godot-free / Tier-1.
    /// </summary>
    internal static class EntityDraftTestData
    {
        // ── Settings + provider-response fixtures ──────────────────────────────

        public static SettingsData Settings(string provider = "anthropic", string baseUrl = "")
            => new() { LlmProvider = provider, LlmModel = "m", LlmBaseUrl = baseUrl };

        /// <summary>Wrap raw model text in an Anthropic Messages-API response body (the shape AnthropicProvider parses).</summary>
        public static string AnthropicBody(string text)
            => JsonSerializer.Serialize(new { content = new[] { new { type = "text", text } } });

        // ── Valid draft JSON per kind (each passes the SAME gate hand-authored data uses) ──

        public const string ValidUnitJson =
            "{\"id\":\"grunt\",\"display_name\":\"Grunt\",\"category\":\"Melee\"," +
            "\"hp\":120,\"speed\":4,\"attack_damage\":12,\"attack_range\":1.5,\"attack_speed\":1," +
            "\"cost_ore\":60,\"supply\":2}";

        public const string ValidHeroJson =
            "{\"id\":\"champion\",\"display_name\":\"Champion\",\"category\":\"Melee\"," +
            "\"hp\":300,\"speed\":3.5,\"attack_damage\":25,\"attack_range\":1.5,\"attack_speed\":1.2," +
            "\"is_hero\":true,\"hero\":{\"max_level\":5,\"base_xp\":100,\"xp_growth\":1.4,\"xp_per_kill\":40," +
            "\"xp_share_radius\":20,\"health_per_level\":25,\"damage_per_level\":4,\"armor_per_level\":1}}";

        public const string ValidAbilityJson =
            "{\"id\":\"minor_heal\",\"display_name\":\"Minor Heal\",\"targeting\":\"Self\"," +
            "\"cost_energy\":20,\"cooldown\":3,\"effect\":{\"kind\":\"heal\",\"amount\":40}}";

        public const string ValidFactionJson =
            "{\"id\":\"emberkin\",\"display_name\":\"Emberkin\",\"color\":[0.8,0.3,0.2,1.0],\"ai_preset\":\"balanced\"," +
            "\"units\":[" +
            "{\"id\":\"worker\",\"display_name\":\"Worker\",\"category\":\"Worker\",\"hp\":60,\"speed\":4}," +
            "{\"id\":\"grunt\",\"display_name\":\"Grunt\",\"category\":\"Melee\",\"hp\":120,\"speed\":4,\"attack_damage\":12,\"attack_range\":1.5}" +
            "],\"buildings\":[]}";

        // ── Contexts ───────────────────────────────────────────────────────────

        public static UnitDraftContext UnitCtx() => new();
        public static AbilityDraftContext AbilityCtx() => new();
        public static FactionDraftContext FactionCtx() => new() { AiPresets = FactionValidator.KnownAiPresets };

        // ── The DrainEvents pump (bounded wait) ────────────────────────────────

        public static void Pump(LLMService svc, Func<bool> done)
        {
            var sw = Stopwatch.StartNew();
            while (!done() && sw.ElapsedMilliseconds < 5000)
            {
                svc.DrainEvents();
                Thread.Sleep(5);
            }
            svc.DrainEvents();
            Assert.True(done(), "generation callback did not fire within the timeout.");
        }
    }
}
