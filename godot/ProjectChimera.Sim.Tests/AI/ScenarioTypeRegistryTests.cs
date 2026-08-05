#nullable enable
using System;
using System.Linq;
using System.Text;
using ProjectChimera.AI;
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// DW-371 — the ScenarioType registry: the enum + per-type map-clamp preset table + the single
    /// <see cref="ScenarioTypeRegistry.Apply"/> populator the Map Generator's picker drives.
    ///
    /// Story 8.3 parameterized the three RTS clamps onto the TRUSTED <see cref="MapGeneratorContext"/> but shipped
    /// no caller able to relax them, so every generated map was still gated by RTS rules ("non-RTS scenario types
    /// wrongly rejected"). These tests pin the closure end-to-end: a preset genuinely changes what
    /// <see cref="LLMService.ValidateScenario"/> accepts, the RTS preset is provably a no-op against the historical
    /// behavior (clamps AND prompt bytes), the per-slot faction resolution is TOTAL for slot counts &gt; 2 instead of
    /// collapsing onto slot 1's faction, and toggling back to RTS restores the old gate exactly.
    /// </summary>
    public class ScenarioTypeRegistryTests
    {
        // ── Fixtures ──────────────────────────────────────────────────────────

        /// <summary>A fresh context with the boot phase's shape (unit ids + bounds + the two faction paths) and NO
        /// scenario type applied — i.e. exactly the pre-registry RTS context.</summary>
        private static MapGeneratorContext Fresh()
            => new()
            {
                UnitIds          = new[] { "melee" },
                MapBounds        = 120f,
                Slot0FactionJson = "res://resources/data/factions/alpha_faction.json",
                Slot1FactionJson = "res://resources/data/factions/beta_faction.json",
            };

        /// <summary>Build a scenario JSON with <paramref name="slotCount"/> player slots (indices 0..N-1) and
        /// <paramref name="combatPerSlot"/> pre-placed combat units on EVERY slot. Integer positions only (no
        /// locale hazard), all inside ±120, ore nodes far apart — so ONLY the clamps under test decide the
        /// outcome.</summary>
        private static string Scenario(int slotCount, int combatPerSlot)
        {
            var sb = new StringBuilder();
            sb.Append("{\"player_slots\":[");
            for (int s = 0; s < slotCount; s++)
            {
                if (s > 0) sb.Append(',');
                sb.Append($"{{\"slot\":{s},\"faction_json\":\"hallucinated_{s}.json\"," +
                          $"\"base_x\":{-45 + s * 30},\"base_z\":0}}");
            }
            sb.Append("],\"resource_nodes\":[");
            sb.Append("{\"x\":-25,\"z\":15,\"supply\":600,\"rate\":5},{\"x\":25,\"z\":-15,\"supply\":600,\"rate\":5}");
            sb.Append("],\"buildings\":[");
            for (int s = 0; s < slotCount; s++)
            {
                if (s > 0) sb.Append(',');
                sb.Append($"{{\"type\":\"CommandCenter\",\"slot\":{s},\"x\":{-45 + s * 30},\"z\":0}}");
            }
            sb.Append("],\"units\":[");
            bool first = true;
            for (int s = 0; s < slotCount; s++)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append($"{{\"unit_id\":\"worker\",\"slot\":{s},\"x\":{-42 + s * 30},\"z\":3}}");
                for (int i = 0; i < combatPerSlot; i++)
                    // Grid inside ±120: 20 columns 10u apart, rows 5u apart, one band per slot.
                    sb.Append($",{{\"unit_id\":\"melee\",\"slot\":{s}," +
                              $"\"x\":{-100 + (i % 20) * 10},\"z\":{-100 + s * 20 + (i / 20) * 5}}}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // ── Table completeness ────────────────────────────────────────────────

        [Fact]
        public void Registry_CoversEveryScenarioTypeExactlyOnce()
        {
            var declared = Enum.GetValues<ScenarioType>();
            Assert.Equal(declared.Length, ScenarioTypeRegistry.All.Count);

            foreach (var type in declared)
            {
                Assert.True(ScenarioTypeRegistry.TryGet(type, out var preset), $"No preset for {type}.");
                Assert.NotNull(preset);
                Assert.Equal(type, preset!.Type);
            }

            // No duplicate rows and no duplicate picker labels (the picker keys items by the enum value).
            Assert.Equal(declared.Length, ScenarioTypeRegistry.All.Select(p => p.Type).Distinct().Count());
            Assert.Equal(declared.Length, ScenarioTypeRegistry.All.Select(p => p.DisplayName).Distinct().Count());
        }

        [Fact]
        public void Registry_DefaultTypeIsRts_AndIsTheFirstPickerItem()
        {
            Assert.Equal(ScenarioType.Rts, ScenarioTypeRegistry.DefaultType);
            Assert.Equal(ScenarioType.Rts, ScenarioTypeRegistry.All[0].Type);
            Assert.Equal(ScenarioType.Rts, Fresh().ScenarioType); // a fresh context is already the default type
        }

        [Fact]
        public void EveryPreset_HasSaneClampsAndCopy()
        {
            foreach (var p in ScenarioTypeRegistry.All)
            {
                Assert.True(p.MinPlayerSlots >= 1, $"{p.Type}: a scenario needs at least one player slot.");
                Assert.True(p.MaxCombatUnitsPerSlot >= 1, $"{p.Type}: a zero combat cap is not authorable.");
                Assert.False(string.IsNullOrWhiteSpace(p.DisplayName), $"{p.Type}: picker label is blank.");
                Assert.False(string.IsNullOrWhiteSpace(p.Summary), $"{p.Type}: UI summary is blank.");

                // RTS carries NO guidance (its prompt must stay byte-identical); every other type must say
                // something to the model, or selecting it would change the gate without changing the request.
                if (p.Type == ScenarioType.Rts) Assert.Equal("", p.PromptGuidance);
                else Assert.False(string.IsNullOrWhiteSpace(p.PromptGuidance), $"{p.Type}: no prompt guidance.");
            }
        }

        [Fact]
        public void EveryPreset_IsShownAsManySlotsAsItDemands()
        {
            // Was the DW-372 tripwire "MinPlayerSlots <= 2", because BuildMapSystemPrompt's SCHEMA and EXAMPLE
            // blocks hardcoded exactly two player_slots: a preset demanding MORE told the model "at least N" while
            // showing it a 2-slot example, so it emitted 2 and the gate rejected every generation. DW-372
            // parameterized both blocks, so the invariant is now the REAL one — the request shows at least as many
            // slots as it demands — and a preset may raise its floor.
            foreach (var p in ScenarioTypeRegistry.All)
            {
                var ctx = ScenarioTypeRegistry.Apply(Fresh(), p.Type);
                Assert.True(LLMService.PromptSlotCount(ctx) >= ctx.MinPlayerSlots,
                    $"{p.Type}: prompt renders {LLMService.PromptSlotCount(ctx)} slots but demands {ctx.MinPlayerSlots}.");

                // …and the worked example the model copies really does satisfy that preset's own gate.
                var (ok, error) = LLMService.ValidateScenario(
                    LlmMapPromptSlotParameterizationTests.ExampleJson(LLMService.BuildMapSystemPrompt(ctx)), ctx);
                Assert.True(ok != null, $"{p.Type}: its own prompt example fails its own gate — {error}");
            }
        }

        [Fact]
        public void Get_ThrowsOnAnUnregisteredType_RatherThanSilentlyFallingBackToRts()
        {
            var bogus = (ScenarioType)99;
            Assert.False(ScenarioTypeRegistry.TryGet(bogus, out var preset));
            Assert.Null(preset);
            Assert.Throws<ArgumentOutOfRangeException>(() => ScenarioTypeRegistry.Get(bogus));
            Assert.Throws<ArgumentOutOfRangeException>(() => ScenarioTypeRegistry.Apply(Fresh(), bogus));
        }

        [Fact]
        public void Apply_RejectsANullContext()
            => Assert.Throws<ArgumentNullException>(() => ScenarioTypeRegistry.Apply(null!, ScenarioType.Rts));

        // ── RTS preset = provably the historical behavior ─────────────────────

        [Fact]
        public void RtsPreset_ReproducesTheHistoricalClamps()
        {
            var ctx = ScenarioTypeRegistry.Apply(Fresh(), ScenarioType.Rts);

            Assert.Equal(2, ctx.MinPlayerSlots);
            Assert.Equal(6, ctx.MaxCombatUnitsPerSlot);
            // NULL resolver ⇒ ResolveFactionJson's own default branch, not a re-implementation.
            Assert.Null(ctx.FactionJsonResolver);
            Assert.Equal(ctx.Slot0FactionJson, ctx.ResolveFactionJson(0));
            Assert.Equal(ctx.Slot1FactionJson, ctx.ResolveFactionJson(1));
            // DW-372: slot 2 used to collapse onto slot 1's faction (a silent 1-vs-3 the moment anything declared
            // more than two slots). The default now alternates, so it is TOTAL; slots 0 and 1 — every scenario an
            // RTS map declares — are untouched, which is what "reproduces the historical clamps" actually means.
            Assert.Equal(ctx.Slot0FactionJson, ctx.ResolveFactionJson(2));
        }

        [Fact]
        public void RtsPrompt_IsUnchangedByApplyingTheDefaultType()
        {
            string before = LLMService.BuildMapSystemPrompt(Fresh());
            string after  = LLMService.BuildMapSystemPrompt(ScenarioTypeRegistry.Apply(Fresh(), ScenarioType.Rts));

            Assert.Equal(before, after);                      // byte-for-byte
            Assert.DoesNotContain("=== SCENARIO TYPE ===", after);
        }

        [Fact]
        public void RtsGate_IsUnchanged_AcceptsSixCombatRejectsSevenAndOneSlot()
        {
            var ctx = ScenarioTypeRegistry.Apply(Fresh(), ScenarioType.Rts);

            Assert.NotNull(LLMService.ValidateScenario(Scenario(2, 6), ctx).scenario);
            Assert.Contains("max 6", LLMService.ValidateScenario(Scenario(2, 7), ctx).error);
            Assert.Contains("at least 2 player slots", LLMService.ValidateScenario(Scenario(1, 1), ctx).error);
        }

        // ── Non-RTS presets genuinely relax the gate ──────────────────────────

        [Fact]
        public void SurvivalPreset_AcceptsTheSingleSlotHordeMap_ThatRtsRejects()
        {
            // 1 player slot, 25 pre-placed hostiles — the shape the RTS gate wrongly rejected.
            string json = Scenario(1, 25);

            var rts = ScenarioTypeRegistry.Apply(Fresh(), ScenarioType.Rts);
            var (rejected, rtsError) = LLMService.ValidateScenario(json, rts);
            Assert.Null(rejected);
            Assert.Contains("at least 2 player slots", rtsError);

            var survival = ScenarioTypeRegistry.Apply(Fresh(), ScenarioType.Survival);
            var (accepted, error) = LLMService.ValidateScenario(json, survival);
            Assert.NotNull(accepted);
            Assert.Null(error);
        }

        [Fact]
        public void ArenaPreset_AcceptsThirtyPreplacedCombatUnitsPerSlot()
        {
            string json = Scenario(2, 30);

            Assert.Contains("max 6",
                LLMService.ValidateScenario(json, ScenarioTypeRegistry.Apply(Fresh(), ScenarioType.Rts)).error);

            var arena = ScenarioTypeRegistry.Apply(Fresh(), ScenarioType.Arena);
            Assert.NotNull(LLMService.ValidateScenario(json, arena).scenario);
            // One over the preset's cap still rejects — relaxing is not removing the gate.
            Assert.Contains("max 30", LLMService.ValidateScenario(Scenario(2, 31), arena).error);
        }

        [Fact]
        public void EveryPreset_AcceptsItsOwnMaximalMap_AndRejectsOneOver()
        {
            // Self-consistency: for each shipped type, a scenario built at exactly that preset's floor (slots) and
            // ceiling (combat units) must pass its own gate — a preset whose own maximal map is rejected would be
            // an unusable menu entry.
            foreach (var p in ScenarioTypeRegistry.All)
            {
                var ctx  = ScenarioTypeRegistry.Apply(Fresh(), p.Type);
                var (ok, error) = LLMService.ValidateScenario(Scenario(p.MinPlayerSlots, p.MaxCombatUnitsPerSlot), ctx);
                Assert.True(ok != null, $"{p.Type}: its own maximal map was rejected — {error}");

                var (over, overError) =
                    LLMService.ValidateScenario(Scenario(p.MinPlayerSlots, p.MaxCombatUnitsPerSlot + 1), ctx);
                Assert.Null(over);
                Assert.Contains($"max {p.MaxCombatUnitsPerSlot}", overError);
            }
        }

        // ── Faction-path resolution is TOTAL past two slots ───────────────────

        [Fact]
        public void NoShippedPolicy_CollapsesEverySlotPastOneOntoSlot1sFaction()
        {
            var rts = ScenarioTypeRegistry.Apply(Fresh(), ScenarioType.Rts);
            var ffa = ScenarioTypeRegistry.Apply(Fresh(), ScenarioType.FreeForAll);

            // DW-372 closed the last collapsing mapping: the RTS default (a NULL resolver, i.e. the context's own
            // branch) now alternates exactly like the explicit mirrored policy, so slots 2/3 are no longer silently
            // fused onto slot 1's faction under EITHER policy. The two agree by construction — RtsDefault delegates
            // to the same total default — which is the property worth pinning.
            Assert.Null(rts.FactionJsonResolver);
            Assert.NotNull(ffa.FactionJsonResolver);
            for (int slot = 0; slot < 8; slot++)
                Assert.Equal(ffa.ResolveFactionJson(slot), rts.ResolveFactionJson(slot));

            Assert.Equal(ffa.Slot0FactionJson, ffa.ResolveFactionJson(2));
            Assert.Equal(ffa.Slot1FactionJson, ffa.ResolveFactionJson(3));
        }

        [Fact]
        public void FreeForAll_ForcesAlternatingFactionPathsThroughValidateScenario()
        {
            var ffa = ScenarioTypeRegistry.Apply(Fresh(), ScenarioType.FreeForAll);
            var (scenario, error) = LLMService.ValidateScenario(Scenario(4, 2), ffa);

            Assert.NotNull(scenario);
            Assert.Null(error);
            // Every hallucinated faction_json is overwritten from the TRUSTED per-slot resolver.
            Assert.Equal(ffa.Slot0FactionJson, scenario!.PlayerSlots[0].FactionJson);
            Assert.Equal(ffa.Slot1FactionJson, scenario.PlayerSlots[1].FactionJson);
            Assert.Equal(ffa.Slot0FactionJson, scenario.PlayerSlots[2].FactionJson);
            Assert.Equal(ffa.Slot1FactionJson, scenario.PlayerSlots[3].FactionJson);
        }

        [Fact]
        public void SandboxPolicy_ResolvesEverySlotToTheSingleFaction()
        {
            var sandbox = ScenarioTypeRegistry.Apply(Fresh(), ScenarioType.Sandbox);
            for (int slot = 0; slot < 4; slot++)
                Assert.Equal(sandbox.Slot0FactionJson, sandbox.ResolveFactionJson(slot));
        }

        [Fact]
        public void AppliedResolver_TracksALaterFactionPathEdit()
        {
            // The boot phase seeds the faction paths from the loaded scenario; a re-seed after the user picked a
            // type must be honored, not frozen into the closure's captured strings.
            var ctx = ScenarioTypeRegistry.Apply(Fresh(), ScenarioType.FreeForAll);
            ctx.Slot0FactionJson = "res://custom/alpha.json";
            ctx.Slot1FactionJson = "res://custom/beta.json";

            Assert.Equal("res://custom/alpha.json", ctx.ResolveFactionJson(0));
            Assert.Equal("res://custom/beta.json",  ctx.ResolveFactionJson(1));
            Assert.Equal("res://custom/alpha.json", ctx.ResolveFactionJson(2));
        }

        // ── Apply is idempotent and fully reversible ──────────────────────────

        [Fact]
        public void Apply_IsIdempotent()
        {
            var once  = ScenarioTypeRegistry.Apply(Fresh(), ScenarioType.Arena);
            var twice = ScenarioTypeRegistry.Apply(ScenarioTypeRegistry.Apply(Fresh(), ScenarioType.Arena),
                                                   ScenarioType.Arena);

            Assert.Equal(once.MinPlayerSlots, twice.MinPlayerSlots);
            Assert.Equal(once.MaxCombatUnitsPerSlot, twice.MaxCombatUnitsPerSlot);
            Assert.Equal(once.ScenarioType, twice.ScenarioType);
            Assert.Equal(once.ResolveFactionJson(2), twice.ResolveFactionJson(2));
        }

        [Fact]
        public void SwitchingBackToRts_RestoresTheHistoricalGateExactly()
        {
            // The picker is a toggle: a user who tries Survival and returns to RTS must get the RTS gate back,
            // including a NULLED resolver — a stale relaxed clamp would silently widen the RTS gate.
            var ctx = Fresh();
            ScenarioTypeRegistry.Apply(ctx, ScenarioType.Survival);
            Assert.Equal(1, ctx.MinPlayerSlots);
            Assert.NotNull(ctx.FactionJsonResolver);

            ScenarioTypeRegistry.Apply(ctx, ScenarioType.Rts);
            Assert.Equal(2, ctx.MinPlayerSlots);
            Assert.Equal(6, ctx.MaxCombatUnitsPerSlot);
            Assert.Null(ctx.FactionJsonResolver);
            Assert.Equal(ScenarioType.Rts, ctx.ScenarioType);
            Assert.Equal(ctx.Slot0FactionJson, ctx.ResolveFactionJson(2)); // DW-372: the total default, not a collapse
            Assert.Contains("at least 2 player slots", LLMService.ValidateScenario(Scenario(1, 1), ctx).error);
            Assert.Equal(LLMService.BuildMapSystemPrompt(Fresh()), LLMService.BuildMapSystemPrompt(ctx));
        }

        [Fact]
        public void Apply_TouchesOnlyTheClampFields()
        {
            var ctx = Fresh();
            var unitIds = ctx.UnitIds;

            ScenarioTypeRegistry.Apply(ctx, ScenarioType.Coop);

            Assert.Same(unitIds, ctx.UnitIds);
            Assert.Equal(120f, ctx.MapBounds);
            Assert.Equal("res://resources/data/factions/alpha_faction.json", ctx.Slot0FactionJson);
            Assert.Equal("res://resources/data/factions/beta_faction.json", ctx.Slot1FactionJson);
        }

        // ── Prompt + UI copy carry the selected type ──────────────────────────

        [Fact]
        public void NonRtsPrompt_CarriesTheTypeGuidanceAndTheRelaxedClampValues()
        {
            var survival = ScenarioTypeRegistry.Apply(Fresh(), ScenarioType.Survival);
            string prompt = LLMService.BuildMapSystemPrompt(survival);

            Assert.Contains("=== SCENARIO TYPE ===", prompt);
            Assert.Contains(ScenarioTypeRegistry.Get(ScenarioType.Survival).PromptGuidance, prompt);
            // The clamp lines still come from the context, so prompt and gate agree on the SAME numbers.
            Assert.Contains("at least 1 player slots", prompt);
            Assert.Contains("at most 40 combat", prompt);
        }

        [Fact]
        public void EveryNonRtsType_ReachesThePrompt()
        {
            foreach (var p in ScenarioTypeRegistry.All)
            {
                string prompt = LLMService.BuildMapSystemPrompt(ScenarioTypeRegistry.Apply(Fresh(), p.Type));
                if (p.Type == ScenarioType.Rts) Assert.DoesNotContain("=== SCENARIO TYPE ===", prompt);
                else Assert.Contains(p.PromptGuidance, prompt);
            }
        }

        [Fact]
        public void DescribeClamps_StatesTheNumbersTheGateWillEnforce()
        {
            string rts = ScenarioTypeRegistry.DescribeClamps(ScenarioType.Rts);
            Assert.Contains("2 player slots", rts);
            Assert.Contains("6 pre-placed combat units", rts);
            Assert.Contains("slot 0 vs. every other slot", rts);

            string survival = ScenarioTypeRegistry.DescribeClamps(ScenarioType.Survival);
            Assert.Contains("1 player slot", survival);
            Assert.Contains("40 pre-placed combat units", survival);
            Assert.Contains("alternating", survival);

            Assert.Contains("one faction on every slot",
                ScenarioTypeRegistry.DescribeClamps(ScenarioType.Sandbox));
        }

        [Fact]
        public void DescribeFactionPaths_ThrowsOnAnUnknownPolicy()
            => Assert.Throws<ArgumentOutOfRangeException>(
                () => ScenarioTypeRegistry.DescribeFactionPaths((FactionPathPolicy)77));
    }
}
