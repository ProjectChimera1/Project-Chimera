#nullable enable
using System;
using System.Linq;
using System.Text;
using ProjectChimera.AI;
using ProjectChimera.Core;
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// DW-372 — the half-applied map-clamp parameterization, finished.
    ///
    /// Story 8.3 parameterized ONE line of the map system prompt (the "at least N player slots" placement rule)
    /// against <see cref="MapGeneratorContext.MinPlayerSlots"/> and left the SCHEMA and EXAMPLE blocks hardcoding
    /// exactly two <c>player_slots</c>. A caller raising the floor was therefore told "at least 4 player slots"
    /// while being SHOWN a 2-slot schema and a 2-slot worked example — the model copies the example, emits 2, and
    /// <see cref="LLMService.ValidateScenario"/> rejects every generation. Two smaller holes rode along: the default
    /// <see cref="MapGeneratorContext.ResolveFactionJson"/> collapsed every slot ≥ 1 onto slot 1's faction (a silent
    /// 1-vs-3 on any 4-slot map), and neither integer clamp had a lower bound (<c>MinPlayerSlots = 0</c> validated a
    /// scenario with NO players; a negative combat cap produced the message "max -1").
    ///
    /// The load-bearing property these tests pin is that the prompt and the gate cannot disagree: the worked example
    /// the model is told to imitate must itself PASS the gate that will judge the model's answer.
    /// </summary>
    public class LlmMapPromptSlotParameterizationTests
    {
        // ── Fixtures / helpers ────────────────────────────────────────────────

        private static MapGeneratorContext Ctx(int minSlots = 2)
            => new() { UnitIds = new[] { "melee" }, MapBounds = 120f, MinPlayerSlots = minSlots };

        /// <summary>The worked example the prompt tells the model to imitate, lifted back out of the prompt text.
        /// Public because <see cref="ScenarioTypeRegistryTests"/> runs the same round-trip per scenario-type preset.
        /// </summary>
        public static string ExampleJson(string prompt)
        {
            const string Start = "=== EXAMPLE OUTPUT ===";
            const string End   = "=== INSTRUCTIONS ===";
            int a = prompt.IndexOf(Start, StringComparison.Ordinal);
            int b = prompt.IndexOf(End, StringComparison.Ordinal);
            Assert.True(a >= 0 && b > a, "the map prompt has no EXAMPLE OUTPUT block");
            return prompt.Substring(a + Start.Length, b - a - Start.Length).Trim();
        }

        /// <summary>The SCHEMA block — everything between the two section headers.</summary>
        private static string SchemaBlock(string prompt)
        {
            const string Start = "=== SCENARIO SCHEMA ===";
            const string End   = "=== PLACEMENT RULES ===";
            int a = prompt.IndexOf(Start, StringComparison.Ordinal);
            int b = prompt.IndexOf(End, StringComparison.Ordinal);
            Assert.True(a >= 0 && b > a, "the map prompt has no SCENARIO SCHEMA block");
            return prompt.Substring(a + Start.Length, b - a - Start.Length).Trim();
        }

        private static int Count(string haystack, string needle)
        {
            int n = 0, i = 0;
            while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }

        // ══════════════════════════════════════════════════════════════════════
        // 1. The two-slot rendering is byte-for-byte the hand-written text it replaced
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// The generator is only safe to adopt if it reproduces the shipped prompt EXACTLY at the default floor —
        /// otherwise "parameterize the example" silently becomes "rewrite every live map request". These are the
        /// pre-DW-372 literals, character for character, including the column alignment (<c>"base_x":  45</c> has two
        /// spaces so it lines up under <c>-45</c>). Joined with "\n" on purpose: the blocks are spliced into verbatim
        /// literals whose newlines come from the source file, so the prompt must not pick up the host's line ending.
        /// </summary>
        [Fact]
        public void TwoSlotPrompt_ReproducesTheHistoricalBlocksByteForByte()
        {
            string prompt = LLMService.BuildMapSystemPrompt(Ctx());
            const string Alpha = "res://resources/data/factions/alpha_faction.json";
            const string Beta  = "res://resources/data/factions/beta_faction.json";

            Assert.Contains(string.Join("\n", new[]
            {
                "  \"player_slots\": [",
                $"    {{ \"slot\": 0, \"faction_json\": \"{Alpha}\", \"start_ore\": 200.0, \"base_x\": -45.0, \"base_z\": 0.0 }},",
                $"    {{ \"slot\": 1, \"faction_json\": \"{Beta}\", \"start_ore\": 200.0, \"base_x\":  45.0, \"base_z\": 0.0 }}",
                "  ],",
            }), prompt);

            Assert.Contains(string.Join("\n", new[]
            {
                "  \"player_slots\": [",
                $"    {{ \"slot\": 0, \"faction_json\": \"{Alpha}\", \"start_ore\": 200, \"base_x\": -45, \"base_z\": 0 }},",
                $"    {{ \"slot\": 1, \"faction_json\": \"{Beta}\", \"start_ore\": 200, \"base_x\":  45, \"base_z\": 0 }}",
                "  ],",
            }), prompt);

            Assert.Contains(string.Join("\n", new[]
            {
                "  \"buildings\": [",
                "    { \"type\": \"CommandCenter\", \"slot\": 0, \"x\": -45, \"z\": 0, \"pre_built\": true },",
                "    { \"type\": \"CommandCenter\", \"slot\": 1, \"x\":  45, \"z\": 0, \"pre_built\": true }",
                "  ],",
            }), prompt);

            Assert.Contains(string.Join("\n", new[]
            {
                "  \"units\": [",
                "    { \"unit_id\": \"worker\", \"slot\": 0, \"x\": -42, \"z\": -3 },",
                "    { \"unit_id\": \"worker\", \"slot\": 0, \"x\": -42, \"z\":  3 },",
                "    { \"unit_id\": \"worker\", \"slot\": 1, \"x\":  42, \"z\": -3 },",
                "    { \"unit_id\": \"worker\", \"slot\": 1, \"x\":  42, \"z\":  3 }",
                "  ],",
            }), prompt);

            Assert.Contains(
                "- Provide at least 2 player slots. Player 1 (slot 0): base near X=-45, Z=0. " +
                "Player 2 (slot 1): base near X=45, Z=0.", prompt);

            Assert.Contains("\"slot\": 0|1,", prompt);   // the schema's slot-choice lists
        }

        /// <summary>A floor BELOW two still renders the two-slot schema and example — two slots satisfy "at least 1",
        /// so the Survival/Sandbox prompts do not move either. Only a floor ABOVE two changes the blocks. (The
        /// one-line "at least N" placement rule of course differs; Story 8.3 already parameterized that.)</summary>
        [Fact]
        public void FloorOfOne_StillRendersTheUnchangedTwoSlotSchemaAndExample()
        {
            string two = LLMService.BuildMapSystemPrompt(Ctx());
            string one = LLMService.BuildMapSystemPrompt(Ctx(1));

            Assert.Equal(SchemaBlock(two),  SchemaBlock(one));
            Assert.Equal(ExampleJson(two),  ExampleJson(one));
            Assert.Equal(2, LLMService.PromptSlotCount(Ctx(1)));
        }

        // ══════════════════════════════════════════════════════════════════════
        // 2. THE defect: a raised floor is now SHOWN, not only stated
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// The regression test for the entry itself. Before the fix the prompt said "at least 4 player slots" and
        /// showed a hardcoded 2-slot schema + 2-slot example; the example — the thing the model is explicitly told to
        /// imitate — failed the very gate the same context enforces, with "Expected at least 4 player slots, got 2".
        /// </summary>
        [Fact]
        public void RaisedFloor_IsShownInTheSchemaAndTheExample_NotOnlyStated()
        {
            var ctx = Ctx(4);
            string prompt = LLMService.BuildMapSystemPrompt(ctx);

            Assert.Contains("- Provide at least 4 player slots.", prompt);
            Assert.Equal(4, LLMService.PromptSlotCount(ctx));

            // Schema: four declared rows and a four-way slot-choice list (never the stale "0|1").
            string schema = SchemaBlock(prompt);
            for (int s = 0; s < 4; s++) Assert.Contains($"\"slot\": {s},", schema);
            Assert.Contains("\"slot\": 0|1|2|3,", schema);
            Assert.DoesNotContain("\"slot\": 0|1,", schema);

            // Example: four player_slots, four CommandCenters, two workers each.
            string example = ExampleJson(prompt);
            Assert.Equal(4, Count(example, "\"faction_json\""));
            Assert.Equal(4, Count(example, "\"type\": \"CommandCenter\""));
            Assert.Equal(8, Count(example, "\"unit_id\": \"worker\""));
            for (int s = 0; s < 4; s++) Assert.Contains($"\"slot\": {s},", example);

            // And every base hint the placement rules give names a real slot.
            for (int s = 0; s < 4; s++) Assert.Contains($"Player {s + 1} (slot {s}): base near X=", prompt);
        }

        /// <summary>
        /// The invariant behind the whole entry, swept across every reachable floor: the worked example must PASS the
        /// gate built from the same context. Pre-fix this failed for every floor above two.
        /// </summary>
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        public void PromptExample_PassesTheGateBuiltFromTheSameContext(int floor)
        {
            var ctx = Ctx(floor);
            var (scenario, error) = LLMService.ValidateScenario(ExampleJson(LLMService.BuildMapSystemPrompt(ctx)), ctx);

            Assert.True(scenario != null, $"floor {floor}: the prompt's own example fails the prompt's own gate — {error}");
            Assert.Null(error);
            Assert.Equal(Math.Max(2, floor), scenario!.PlayerSlots.Length);
        }

        /// <summary>The example bases stay inside a caller's TIGHTER bounds too — the placement rule and the worked
        /// example are generated from one radius, so the example can never violate the rule printed above it.</summary>
        [Theory]
        [InlineData(120f)]
        [InlineData(60f)]
        [InlineData(30f)]
        public void PromptExample_StaysInsideTheContextBounds(float bounds)
        {
            var ctx = new MapGeneratorContext { UnitIds = new[] { "melee" }, MapBounds = bounds, MinPlayerSlots = 4 };
            var (scenario, error) = LLMService.ValidateScenario(ExampleJson(LLMService.BuildMapSystemPrompt(ctx)), ctx);

            Assert.True(scenario != null, $"bounds {bounds}: the prompt's own example fails its own gate — {error}");
            foreach (var slot in scenario!.PlayerSlots)
                Assert.True(Math.Abs(slot.BaseX) <= bounds && Math.Abs(slot.BaseZ) <= bounds,
                    $"bounds {bounds}: slot {slot.Slot} base ({slot.BaseX}, {slot.BaseZ}) is outside.");
        }

        // ══════════════════════════════════════════════════════════════════════
        // 3. The per-slot faction resolver is TOTAL
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// The default mapping used to be <c>slot == 0 ? Slot0 : Slot1</c> — correct for a 1v1 and silently wrong for
        /// anything wider, because slots 1, 2, 3 … all resolved to ONE faction. It now alternates, so it agrees with
        /// the old mapping on slots 0 and 1 (every two-slot map is untouched) and is total above them.
        /// </summary>
        [Fact]
        public void DefaultResolver_AlternatesPastSlotOne_InsteadOfCollapsing()
        {
            var ctx = Ctx();
            Assert.Null(ctx.FactionJsonResolver);

            Assert.Equal(ctx.Slot0FactionJson, ctx.ResolveFactionJson(0));
            Assert.Equal(ctx.Slot1FactionJson, ctx.ResolveFactionJson(1));
            for (int slot = 2; slot < FactionRegistry.PLAYER_COUNT; slot++)
                Assert.Equal(slot % 2 == 0 ? ctx.Slot0FactionJson : ctx.Slot1FactionJson, ctx.ResolveFactionJson(slot));

            // Not every slot past 0 on one faction — the concrete symptom.
            Assert.NotEqual(ctx.ResolveFactionJson(2), ctx.ResolveFactionJson(3));
        }

        /// <summary>A 4-slot map generated WITHOUT a caller-supplied resolver comes out 2v2, not 1v3.</summary>
        [Fact]
        public void FourSlotMap_WithNoCustomResolver_IsNotSilentlyOneVersusThree()
        {
            var ctx = Ctx(4);
            var (scenario, error) = LLMService.ValidateScenario(ExampleJson(LLMService.BuildMapSystemPrompt(ctx)), ctx);

            Assert.NotNull(scenario);
            Assert.Null(error);
            Assert.Equal(ctx.Slot0FactionJson, scenario!.PlayerSlots[0].FactionJson);
            Assert.Equal(ctx.Slot1FactionJson, scenario.PlayerSlots[1].FactionJson);
            Assert.Equal(ctx.Slot0FactionJson, scenario.PlayerSlots[2].FactionJson);
            Assert.Equal(ctx.Slot1FactionJson, scenario.PlayerSlots[3].FactionJson);
            Assert.Equal(2, scenario.PlayerSlots.Count(s => s.FactionJson == ctx.Slot0FactionJson));
        }

        /// <summary>The prompt NAMES the per-slot path the gate will force, so a non-default resolver is visible to
        /// the model instead of being silently rewritten after generation.</summary>
        [Fact]
        public void Prompt_NamesThePerSlotFactionPathTheGateWillForce()
        {
            var ctx = Ctx(4);
            ctx.FactionJsonResolver = slot => $"res://custom/faction_{slot}.json";
            string prompt = LLMService.BuildMapSystemPrompt(ctx);

            for (int s = 0; s < 4; s++) Assert.Contains($"res://custom/faction_{s}.json", prompt);
            Assert.DoesNotContain(ctx.Slot0FactionJson, prompt);

            // …and what the prompt showed is exactly what the gate writes back.
            var (scenario, _) = LLMService.ValidateScenario(ExampleJson(prompt), ctx);
            Assert.NotNull(scenario);
            for (int s = 0; s < 4; s++)
                Assert.Equal($"res://custom/faction_{s}.json", scenario!.PlayerSlots[s].FactionJson);
        }

        // ══════════════════════════════════════════════════════════════════════
        // 4. Lower-bound guards on the two integer clamps
        // ══════════════════════════════════════════════════════════════════════

        [Theory]
        [InlineData(0, 1)]
        [InlineData(-7, 1)]
        [InlineData(1, 1)]
        [InlineData(3, 3)]
        [InlineData(8, 8)]
        [InlineData(9, 8)]          // above the sim's player ceiling → unsatisfiable, so clamp down
        [InlineData(int.MaxValue, 8)]
        public void MinPlayerSlots_IsClampedIntoTheAuthorableRange(int set, int expected)
            => Assert.Equal(expected, new MapGeneratorContext { MinPlayerSlots = set }.MinPlayerSlots);

        [Theory]
        [InlineData(-1, 0)]
        [InlineData(int.MinValue, 0)]
        [InlineData(0, 0)]          // zero IS meaningful — "no pre-placed combat units at all"
        [InlineData(6, 6)]
        public void MaxCombatUnitsPerSlot_IsClampedAtZero(int set, int expected)
            => Assert.Equal(expected, new MapGeneratorContext { MaxCombatUnitsPerSlot = set }.MaxCombatUnitsPerSlot);

        /// <summary>
        /// The concrete symptom of the missing floor: with <c>MinPlayerSlots = 0</c> a scenario declaring NO players
        /// satisfied "0 >= 0", skipped the DW-373 index loop (nothing to iterate) and the DW-542 reference checks
        /// (nothing placed), and validated clean — handing every downstream faction/spawn path a playerless map.
        /// </summary>
        [Fact]
        public void ZeroFloor_NoLongerValidatesAPlayerlessScenario()
        {
            var ctx = new MapGeneratorContext { UnitIds = new[] { "melee" }, MapBounds = 120f, MinPlayerSlots = 0 };
            Assert.Equal(1, ctx.MinPlayerSlots);

            var (scenario, error) = LLMService.ValidateScenario(
                "{\"player_slots\":[],\"resource_nodes\":[],\"buildings\":[],\"units\":[]}", ctx);

            Assert.Null(scenario);
            Assert.Contains("at least 1 player slots, got 0", error);
        }

        /// <summary>A negative cap used to reach both the gate's reject message ("max -1") and the prompt ("at most
        /// -1 combat"). Clamped at the property, the two still agree — on 0.</summary>
        [Fact]
        public void NegativeCombatCap_NoLongerReachesTheMessageOrThePrompt()
        {
            var ctx = new MapGeneratorContext
                { UnitIds = new[] { "melee" }, MapBounds = 120f, MaxCombatUnitsPerSlot = -1 };

            Assert.Contains("at most 0 combat", LLMService.BuildMapSystemPrompt(ctx));
            Assert.DoesNotContain("at most -1", LLMService.BuildMapSystemPrompt(ctx));

            var json = new StringBuilder()
                .Append("{\"player_slots\":[")
                .Append("{\"slot\":0,\"faction_json\":\"x\",\"base_x\":-45,\"base_z\":0},")
                .Append("{\"slot\":1,\"faction_json\":\"x\",\"base_x\":45,\"base_z\":0}")
                .Append("],\"resource_nodes\":[],\"buildings\":[],\"units\":[")
                .Append("{\"unit_id\":\"melee\",\"slot\":0,\"x\":-40,\"z\":0}")
                .Append("]}")
                .ToString();

            var (scenario, error) = LLMService.ValidateScenario(json, ctx);
            Assert.Null(scenario);
            Assert.Contains("(max 0)", error);
            Assert.DoesNotContain("max -1", error);
        }

        /// <summary>The clamped value is what the PROMPT states as well as what the gate enforces — a clamp applied
        /// at only one of the two ends is the exact divergence this entry is about.</summary>
        [Fact]
        public void ClampedValues_ReachThePromptAndTheGateIdentically()
        {
            var ctx = new MapGeneratorContext
            {
                UnitIds = new[] { "melee" }, MapBounds = 120f,
                MinPlayerSlots = 0, MaxCombatUnitsPerSlot = -5,
            };
            string prompt = LLMService.BuildMapSystemPrompt(ctx);

            Assert.Contains($"- Provide at least {ctx.MinPlayerSlots} player slots.", prompt);
            Assert.Contains($"- Pre-place at most {ctx.MaxCombatUnitsPerSlot} combat", prompt);
        }
    }
}
