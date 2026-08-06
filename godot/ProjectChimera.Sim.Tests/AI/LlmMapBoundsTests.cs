#nullable enable
using System.Linq;
using ProjectChimera.AI;
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// Story 15.2 (Route C item 4) — the LLM placement gates bound content by the PLAYABLE <c>map_bounds</c>, never
    /// <c>map_bounds + border_extent</c>. Locks both halves of exposure V1(2):
    ///   • STRUCTURAL: neither <see cref="MapGeneratorContext"/> nor <see cref="ScenarioContext"/> carries a
    ///     <c>BorderExtent</c> member, so a visual border can never leak into the AI's clamp values;
    ///   • BEHAVIOURAL: with a bordered map's context (<c>MapBounds = 128</c>), a position at 140 — inside the would-be
    ///     visual border of 160, outside the playable 128 — is REJECTED; a position inside 128 is accepted.
    /// </summary>
    public class LlmMapBoundsTests
    {
        private static MapGeneratorContext BorderedMapContext()
            // What MapGeneratorPhase builds from a bordered scenario: MapBounds is map_bounds ALONE (128), never
            // map_bounds + border_extent (160). There is no border field to add even if one wanted to.
            => new() { UnitIds = new[] { "melee" }, MapBounds = 128f };

        private static string Scenario(int nodeX)
            => "{\"player_slots\":[" +
               "{\"slot\":0,\"faction_json\":\"f0.json\",\"base_x\":-45,\"base_z\":0}," +
               "{\"slot\":1,\"faction_json\":\"f1.json\",\"base_x\":45,\"base_z\":0}]," +
               $"\"resource_nodes\":[{{\"x\":{nodeX},\"z\":0,\"supply\":600,\"rate\":5}}]," +
               "\"buildings\":[{\"type\":\"CommandCenter\",\"slot\":0,\"x\":-45,\"z\":0}," +
               "{\"type\":\"CommandCenter\",\"slot\":1,\"x\":45,\"z\":0}]," +
               "\"units\":[{\"unit_id\":\"worker\",\"slot\":0,\"x\":-42,\"z\":3}]}";

        // ── Structural lock: border_extent is absent from the AI context types by design ──

        [Fact]
        public void MapGeneratorContext_HasNoBorderExtentMember()
            => Assert.DoesNotContain(typeof(MapGeneratorContext).GetMembers(),
                                     m => m.Name.Contains("Border"));

        [Fact]
        public void ScenarioContext_HasNoBorderExtentMember()
            => Assert.DoesNotContain(typeof(ScenarioContext).GetMembers(),
                                     m => m.Name.Contains("Border"));

        // ── Behavioural lock: the placement gate clamps to map_bounds (128), NOT map_bounds + border (160) ──

        [Fact]
        public void PlacementInsideTheBorderButOutsidePlayable_IsRejected()
        {
            // x=140 is inside 128+32=160 (the visual border) but outside the playable 128. If border ever reached the
            // clamp this would pass — the whole point of Route C is that it does not.
            var (scenario, error) = LLMService.ValidateScenario(Scenario(140), BorderedMapContext());
            Assert.Null(scenario);
            Assert.Contains("outside", error);
            Assert.Contains("128", error);
        }

        [Fact]
        public void PlacementInsidePlayable_IsAccepted()
        {
            var (scenario, error) = LLMService.ValidateScenario(Scenario(100), BorderedMapContext());
            Assert.NotNull(scenario);
            Assert.Null(error);
        }

        // ── The generated prompt advertises the playable bound (128), never the bordered 160 ──

        [Fact]
        public void MapPrompt_AdvertisesThePlayableBoundOnly()
        {
            string prompt = LLMService.BuildMapSystemPrompt(BorderedMapContext());
            Assert.Contains("±128", prompt);
            Assert.DoesNotContain("±160", prompt);
        }
    }
}
