#nullable enable
using System.Text;
using ProjectChimera.AI;
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// DW-373 — <see cref="LLMService.ValidateScenario"/> Pass 2 slot-index hardening: declared player-slot indices
    /// must be UNIQUE and within [0, PlayerSlots.Length). Before this gate, an (untrusted) scenario declaring two
    /// slots both <c>"slot":0</c> passed the length-based min-slots check, both resolved to the SAME faction via the
    /// per-slot resolver, and Pass 7 merged their combat counts under one key — a degenerate one-faction scenario
    /// sailed past the gate. The check is UNIVERSAL (structural), so it fires under a relaxed clamp context too.
    /// </summary>
    public class LlmScenarioSlotValidationTests
    {
        private static MapGeneratorContext Default()
            => new() { UnitIds = new[] { "melee" }, MapBounds = 120f };

        /// <summary>Build a scenario JSON declaring exactly the given player-slot indices (integer positions — no
        /// locale hazard). Positions/ore spacing/combat counts are all valid so ONLY the slot indices decide.</summary>
        private static string Scenario(params int[] slotIndices)
        {
            var sb = new StringBuilder();
            sb.Append("{\"player_slots\":[");
            for (int i = 0; i < slotIndices.Length; i++)
            {
                if (i > 0) sb.Append(',');
                int bx = -45 + i * 30; // distinct in-bounds base positions regardless of slot index
                sb.Append($"{{\"slot\":{slotIndices[i]},\"faction_json\":\"hallucinated_{i}.json\",\"base_x\":{bx},\"base_z\":0}}");
            }
            sb.Append("],\"resource_nodes\":[");
            sb.Append("{\"x\":-25,\"z\":15,\"supply\":600,\"rate\":5},{\"x\":25,\"z\":-15,\"supply\":600,\"rate\":5}");
            sb.Append("],\"buildings\":[{\"type\":\"CommandCenter\",\"slot\":0,\"x\":-45,\"z\":0}");
            sb.Append("],\"units\":[{\"unit_id\":\"worker\",\"slot\":0,\"x\":-42,\"z\":3}");
            sb.Append("]}");
            return sb.ToString();
        }

        // ── Rejects (each passed the gate before the DW-373 fix) ──────────────

        [Fact]
        public void ValidateScenario_RejectsDuplicateSlotIndices()
        {
            // Two slots both "slot":0 — the exact degenerate shape from the ledger entry.
            var (scenario, error) = LLMService.ValidateScenario(Scenario(0, 0), Default());
            Assert.Null(scenario);
            Assert.Contains("player_slots[1].slot=0", error);
            Assert.Contains("unique", error);
        }

        [Fact]
        public void ValidateScenario_RejectsSlotIndexAboveRange()
        {
            // Length 2 ⇒ valid indices are [0, 2); a declared slot 5 is out of range.
            var (scenario, error) = LLMService.ValidateScenario(Scenario(0, 5), Default());
            Assert.Null(scenario);
            Assert.Contains("player_slots[1].slot=5", error);
            Assert.Contains("outside [0, 2)", error);
        }

        [Fact]
        public void ValidateScenario_RejectsNegativeSlotIndex()
        {
            var (scenario, error) = LLMService.ValidateScenario(Scenario(-1, 1), Default());
            Assert.Null(scenario);
            Assert.Contains("player_slots[0].slot=-1", error);
            Assert.Contains("outside [0, 2)", error);
        }

        [Fact]
        public void ValidateScenario_SlotCheckIsUniversal_FiresUnderRelaxedClamps()
        {
            // A relaxed clamp context (the Story 8.3 non-RTS path) must NOT relax the structural slot check.
            var relaxed = new MapGeneratorContext
                { UnitIds = new[] { "melee" }, MapBounds = 120f, MinPlayerSlots = 1, MaxCombatUnitsPerSlot = 20 };
            var (scenario, error) = LLMService.ValidateScenario(Scenario(0, 0), relaxed);
            Assert.Null(scenario);
            Assert.Contains("unique", error);
        }

        // ── Accepts ───────────────────────────────────────────────────────────

        [Fact]
        public void ValidateScenario_AcceptsPermutedDistinctInRangeSlots()
        {
            // Declaration order need not match the index: [1, 0] is a valid permutation of 0..1, and each slot's
            // faction path is forced from its own declared index (slot 1 → Slot1FactionJson even though it is
            // declared first).
            var ctx = Default();
            var (scenario, error) = LLMService.ValidateScenario(Scenario(1, 0), ctx);
            Assert.NotNull(scenario);
            Assert.Null(error);
            Assert.Equal(ctx.Slot1FactionJson, scenario!.PlayerSlots[0].FactionJson);
            Assert.Equal(ctx.Slot0FactionJson, scenario.PlayerSlots[1].FactionJson);
        }
    }
}
