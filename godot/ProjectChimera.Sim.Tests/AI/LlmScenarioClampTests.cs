#nullable enable
using System.Text;
using ProjectChimera.AI;
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// Story 8.3 — the map-clamp parameterization end-to-end: the default (RTS) <see cref="MapGeneratorContext"/>
    /// rejects &gt;6 combat / &lt;2 slots exactly as before; a relaxed context accepts them; the UNIVERSAL passes
    /// (out-of-bounds positions, sub-15u ore spacing) still fire under a relaxed context; the forced faction-path
    /// resolution overwrites hallucinated paths per slot from the TRUSTED context; and
    /// <see cref="LLMService.BuildMapSystemPrompt"/> reflects the context's clamp values.
    /// </summary>
    public class LlmScenarioClampTests
    {
        // ── Fixtures ──────────────────────────────────────────────────────────

        private static MapGeneratorContext Default()
            => new() { UnitIds = new[] { "melee" }, MapBounds = 120f };

        private static MapGeneratorContext Relaxed()
            => new() { UnitIds = new[] { "melee" }, MapBounds = 120f, MinPlayerSlots = 1, MaxCombatUnitsPerSlot = 20 };

        /// <summary>Build a scenario JSON with integer positions (no locale hazard). All positions are inside ±120
        /// unless <paramref name="oobUnit"/>; ore nodes are far apart unless <paramref name="tightOre"/>.</summary>
        private static string Scenario(int slotCount, int combatInSlot0, bool tightOre = false, bool oobUnit = false)
        {
            var sb = new StringBuilder();
            sb.Append("{\"player_slots\":[");
            for (int s = 0; s < slotCount; s++)
            {
                if (s > 0) sb.Append(',');
                int bx = s == 0 ? -45 : 45;
                sb.Append($"{{\"slot\":{s},\"faction_json\":\"hallucinated_{s}.json\",\"base_x\":{bx},\"base_z\":0}}");
            }
            sb.Append("],\"resource_nodes\":[");
            sb.Append(tightOre
                ? "{\"x\":0,\"z\":0,\"supply\":600,\"rate\":5},{\"x\":0,\"z\":5,\"supply\":600,\"rate\":5}"
                : "{\"x\":-25,\"z\":15,\"supply\":600,\"rate\":5},{\"x\":25,\"z\":-15,\"supply\":600,\"rate\":5}");
            sb.Append("],\"buildings\":[{\"type\":\"CommandCenter\",\"slot\":0,\"x\":-45,\"z\":0}");
            if (slotCount > 1) sb.Append(",{\"type\":\"CommandCenter\",\"slot\":1,\"x\":45,\"z\":0}");
            sb.Append("],\"units\":[{\"unit_id\":\"worker\",\"slot\":0,\"x\":-42,\"z\":3}");
            for (int i = 0; i < combatInSlot0; i++)
                sb.Append($",{{\"unit_id\":\"melee\",\"slot\":0,\"x\":{-60 + i * 3},\"z\":10}}");
            if (oobUnit)
                sb.Append(",{\"unit_id\":\"melee\",\"slot\":0,\"x\":999,\"z\":0}");
            sb.Append("]}");
            return sb.ToString();
        }

        // ── Default RTS context — no regression ───────────────────────────────

        [Fact]
        public void DefaultContext_AcceptsValidRtsScenario()
        {
            var (scenario, error) = LLMService.ValidateScenario(Scenario(2, 3), Default());
            Assert.NotNull(scenario);
            Assert.Null(error);
        }

        [Fact]
        public void DefaultContext_RejectsTooManyCombatUnits_ExactlyAsBefore()
        {
            var (scenario, error) = LLMService.ValidateScenario(Scenario(2, 7), Default());
            Assert.Null(scenario);
            Assert.Contains("max 6", error);
        }

        [Fact]
        public void DefaultContext_RejectsTooFewSlots_ExactlyAsBefore()
        {
            var (scenario, error) = LLMService.ValidateScenario(Scenario(1, 1), Default());
            Assert.Null(scenario);
            Assert.Contains("at least 2 player slots", error);
        }

        // ── Relaxed context — non-RTS scenario passes ─────────────────────────

        [Fact]
        public void RelaxedContext_AcceptsOneSlotAndTenCombat()
        {
            var (scenario, error) = LLMService.ValidateScenario(Scenario(1, 10), Relaxed());
            Assert.NotNull(scenario);
            Assert.Null(error);
        }

        // ── Universal passes still fire under a relaxed context ───────────────

        [Fact]
        public void RelaxedContext_StillRejectsOutOfBoundsPosition()
        {
            var (scenario, error) = LLMService.ValidateScenario(Scenario(1, 1, oobUnit: true), Relaxed());
            Assert.Null(scenario);
            Assert.Contains("outside", error);
        }

        [Fact]
        public void RelaxedContext_StillRejectsSub15uOreSpacing()
        {
            var (scenario, error) = LLMService.ValidateScenario(Scenario(1, 1, tightOre: true), Relaxed());
            Assert.Null(scenario);
            Assert.Contains("minimum 15u", error);
        }

        // ── Forced faction-path resolution (from the TRUSTED context) ─────────

        [Fact]
        public void ValidateScenario_OverwritesHallucinatedFactionPaths_PerSlot()
        {
            var ctx = Default();
            var (scenario, error) = LLMService.ValidateScenario(Scenario(2, 1), ctx);
            Assert.NotNull(scenario);
            Assert.Null(error);
            // Each slot's hallucinated faction_json is overwritten from the trusted per-slot resolver (RTS default).
            Assert.Equal(ctx.Slot0FactionJson, scenario!.PlayerSlots[0].FactionJson);
            Assert.Equal(ctx.Slot1FactionJson, scenario.PlayerSlots[1].FactionJson);
        }

        [Fact]
        public void ValidateScenario_ForcedPath_HonorsCustomResolver()
        {
            var ctx = Relaxed();
            ctx.FactionJsonResolver = slot => $"res://custom/faction_{slot}.json";
            var (scenario, error) = LLMService.ValidateScenario(Scenario(1, 1), ctx);
            Assert.NotNull(scenario);
            Assert.Null(error);
            Assert.Equal("res://custom/faction_0.json", scenario!.PlayerSlots[0].FactionJson);
        }

        // ── Prompt reflects the clamp values it validates against ─────────────

        [Fact]
        public void BuildMapSystemPrompt_ReflectsDefaultClampValues()
        {
            string prompt = LLMService.BuildMapSystemPrompt(Default());
            Assert.Contains("at least 2 player slots", prompt);
            Assert.Contains("at most 6 combat", prompt);
        }

        [Fact]
        public void BuildMapSystemPrompt_ReflectsRelaxedClampValues()
        {
            string prompt = LLMService.BuildMapSystemPrompt(Relaxed());
            Assert.Contains("at least 1 player slots", prompt);
            Assert.Contains("at most 20 combat", prompt);
        }
    }
}
