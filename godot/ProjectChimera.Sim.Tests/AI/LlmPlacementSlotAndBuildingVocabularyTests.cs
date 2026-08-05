#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using ProjectChimera.AI;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// The llm-scenario-validation-hardening bundle — two gaps between what the AI-generation gate
    /// (<see cref="LLMService.ValidateScenario"/> / <see cref="LLMService.Validate"/>) accepted and what the
    /// <see cref="ScenarioValidator"/> LOAD gate accepts. Both made the generation UX lie to the creator.
    ///
    /// <para><b>DW-542 — placement slot refs were never range-checked.</b> DW-373 hardened the player-slot
    /// DECLARATION indices only. A generated map could place a unit or a building on slot 3 of a 2-slot scenario:
    /// the LLM gate reported "validated", and the loader then rejected it with "references no declared
    /// player_slot". The gate now makes that call in the SAME pass that validates the declarations.</para>
    ///
    /// <para><b>DW-627 — the building-type vocabulary was a stale private copy.</b> Trigger <c>building_type</c>
    /// resolved against a private 4-member <c>enum BuildingType</c> declared inside <c>LLMService</c> (shadowing the
    /// real <see cref="BuildingType"/>), and scenario <c>buildings[].type</c> against a hardcoded 4-name string set.
    /// Both went stale when Story 2.8 appended <see cref="BuildingType.Aviary"/> — a legitimate BUILT-IN reference
    /// was rejected — and neither could ever know about an authored custom building, so the DW-170/Story-6.8 custom
    /// vocabulary worked for hand-authored and editor-authored scenarios but not generated ones. Both sites now call
    /// <see cref="ScenarioValidator.IsKnownBuildingType"/>, the load gate's own predicate.</para>
    /// </summary>
    public class LlmPlacementSlotAndBuildingVocabularyTests
    {
        private const string CustomId = "watchtower";

        // ── Fixtures ────────────────────────────────────────────────────────────

        private static MapGeneratorContext MapCtx()
            => new() { UnitIds = new[] { "melee" }, MapBounds = 120f };

        private static ScenarioContext TriggerCtx()
            => new() { UnitIds = new[] { "melee" }, MapBounds = 120f };

        /// <summary>A faction authoring a custom "watchtower" that has no <see cref="BuildingType"/> enum member.</summary>
        private static FactionDefinition FactionWithWatchtower()
        {
            var f = new FactionDefinition { Id = "alpha", DisplayName = "Alpha" };
            f.Units.Add(new UnitDefinition { Id = "worker", Category = "Worker", Hp = 50f });
            f.Buildings.Add(new BuildingDefinition
            {
                Id = "command_center", DisplayName = "Command Center", Category = "Structure",
                Hp = 500f, ConstructionTime = 15f, SupplyBonus = 10, ProducesCategory = "Worker",
            });
            f.Buildings.Add(new BuildingDefinition
            {
                Id = CustomId, DisplayName = "Watch Tower", Category = "Structure",
                Hp = 150f, ConstructionTime = 8f, SupplyBonus = 3, ProducesCategory = "Melee",
            });
            return f;
        }

        /// <summary>A second faction that authors NO watchtower — the fail-closed arm of the faction qualifier.</summary>
        private static FactionDefinition FactionWithoutWatchtower()
        {
            var f = new FactionDefinition { Id = "beta", DisplayName = "Beta" };
            f.Units.Add(new UnitDefinition { Id = "worker", Category = "Worker", Hp = 50f });
            f.Buildings.Add(new BuildingDefinition
            {
                Id = "command_center", DisplayName = "Command Center", Category = "Structure",
                Hp = 500f, ConstructionTime = 15f, SupplyBonus = 10, ProducesCategory = "Worker",
            });
            return f;
        }

        /// <summary>Per-slot defs indexed by <c>(int)Faction</c> (slot + 1) — the SAME array shape the load gate and
        /// the applier use, so a wrong convention here would surface as a cross-slot acceptance.</summary>
        private static FactionDefinition?[] SlotDefs(FactionDefinition? slot0, FactionDefinition? slot1)
        {
            var defs = new FactionDefinition?[FactionRegistry.FACTION_ARRAY_SIZE];
            defs[(int)Faction.Player1] = slot0;
            defs[(int)Faction.Player2] = slot1;
            return defs;
        }

        /// <summary>A structurally valid 2-slot scenario; the caller supplies the raw buildings/units array bodies so
        /// each test decides only the placement slot / building type under examination. Everything else (positions,
        /// ore spacing, combat counts, unit ids) is deliberately valid.</summary>
        private static string Scenario(string buildings, string units)
        {
            var sb = new StringBuilder();
            sb.Append("{\"player_slots\":[");
            sb.Append("{\"slot\":0,\"faction_json\":\"a.json\",\"base_x\":-45,\"base_z\":0},");
            sb.Append("{\"slot\":1,\"faction_json\":\"b.json\",\"base_x\":45,\"base_z\":0}");
            sb.Append("],\"resource_nodes\":[");
            sb.Append("{\"x\":-25,\"z\":15,\"supply\":600,\"rate\":5},{\"x\":25,\"z\":-15,\"supply\":600,\"rate\":5}");
            sb.Append("],\"buildings\":[").Append(buildings);
            sb.Append("],\"units\":[").Append(units);
            sb.Append("]}");
            return sb.ToString();
        }

        private static string Building(string type, int slot, float x = -45f, float z = 0f)
            => $"{{\"type\":\"{type}\",\"slot\":{slot},\"x\":{(int)x},\"z\":{(int)z}}}";

        private static string Unit(string unitId, int slot, float x = -42f, float z = 3f)
            => $"{{\"unit_id\":\"{unitId}\",\"slot\":{slot},\"x\":{(int)x},\"z\":{(int)z}}}";

        /// <summary>A flat trigger whose event carries the given <c>building_type</c> on the given faction slot.</summary>
        private static string EventTrigger(string buildingType, int faction)
            => "{\"name\":\"T\",\"events\":[{\"type\":\"building_completed\",\"faction\":" + faction +
               ",\"building_type\":\"" + buildingType + "\"}],\"conditions\":[]," +
               "\"actions\":[{\"type\":\"victory\",\"faction\":0}]}";

        /// <summary>The same, on the CONDITION channel (<c>building_exists</c>) — the second gated site.</summary>
        private static string ConditionTrigger(string buildingType, int faction)
            => "{\"name\":\"T\",\"events\":[{\"type\":\"match_start\"}]," +
               "\"conditions\":[{\"type\":\"building_exists\",\"faction\":" + faction +
               ",\"building_type\":\"" + buildingType + "\"}]," +
               "\"actions\":[{\"type\":\"victory\",\"faction\":0}]}";

        // ══════════════════════════════════════════════════════════════════════
        // DW-542 — placement slot references
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>RED before DW-542: a building on slot 3 of a 2-slot map was reported as "validated" and only
        /// rejected later by the loader.</summary>
        [Fact]
        public void ValidateScenario_RejectsBuildingSlotOutsideTheDeclaredSlots()
        {
            var (scenario, error) = LLMService.ValidateScenario(
                Scenario(Building("CommandCenter", slot: 3), Unit("worker", slot: 0)), MapCtx());

            Assert.Null(scenario);
            Assert.Contains("buildings[0].slot=3", error);
            Assert.Contains("references no declared player_slot", error!);
        }

        /// <summary>RED before DW-542 — the unit half. The located index names the OFFENDING row, not the first.</summary>
        [Fact]
        public void ValidateScenario_RejectsUnitSlotOutsideTheDeclaredSlots()
        {
            var (scenario, error) = LLMService.ValidateScenario(
                Scenario(Building("CommandCenter", slot: 0),
                         Unit("worker", slot: 0) + "," + Unit("melee", slot: 4, x: 10, z: 10)),
                MapCtx());

            Assert.Null(scenario);
            Assert.Contains("units[1].slot=4", error);
            Assert.Contains("references no declared player_slot", error!);
        }

        /// <summary>A negative placement slot is the same defect from the other side — it would index the applier's
        /// per-slot arrays out of range rather than merely name a missing player.</summary>
        [Fact]
        public void ValidateScenario_RejectsNegativePlacementSlot()
        {
            var (scenario, error) = LLMService.ValidateScenario(
                Scenario(Building("CommandCenter", slot: -1), Unit("worker", slot: 0)), MapCtx());

            Assert.Null(scenario);
            Assert.Contains("buildings[0].slot=-1", error);
        }

        /// <summary>The check is STRUCTURAL, so a relaxed (non-RTS) clamp context must not switch it off — the same
        /// universality contract DW-373's declaration check carries.</summary>
        [Fact]
        public void ValidateScenario_PlacementSlotCheckIsUniversal_FiresUnderRelaxedClamps()
        {
            var relaxed = new MapGeneratorContext
            {
                UnitIds = new[] { "melee" }, MapBounds = 120f,
                MinPlayerSlots = 1, MaxCombatUnitsPerSlot = 20,
            };

            var (scenario, error) = LLMService.ValidateScenario(
                Scenario(Building("CommandCenter", slot: 0), Unit("melee", slot: 9, x: 10, z: 10)), relaxed);

            Assert.Null(scenario);
            Assert.Contains("units[0].slot=9", error);
        }

        /// <summary>The green path: every placement on a DECLARED slot still passes, including when the declaration
        /// order is a permutation (slot 1 declared first) — the check is set membership, not positional.</summary>
        [Fact]
        public void ValidateScenario_AcceptsPlacementsOnEveryDeclaredSlot()
        {
            string json = Scenario(
                Building("CommandCenter", slot: 0) + "," + Building("Barracks", slot: 1, x: 45),
                Unit("worker", slot: 0) + "," + Unit("worker", slot: 1, x: 42));

            var (scenario, error) = LLMService.ValidateScenario(json, MapCtx());

            Assert.Null(error);
            Assert.NotNull(scenario);
        }

        /// <summary>The gate and the LOADER now agree: a generated map the LLM gate passes is one whose placement
        /// slot refs the <see cref="ScenarioValidator"/> also accepts. This is the "validated means loadable"
        /// property DW-542 is really about — asserted end to end rather than by message shape.</summary>
        [Fact]
        public void ValidateScenario_PassingGeneratedMap_AlsoPassesTheLoadGatesSlotRefCheck()
        {
            string json = Scenario(
                Building("CommandCenter", slot: 0) + "," + Building("CommandCenter", slot: 1, x: 45),
                Unit("worker", slot: 0) + "," + Unit("worker", slot: 1, x: 42));

            var (scenario, error) = LLMService.ValidateScenario(json, MapCtx());
            Assert.Null(error);

            ValidationResult load = new ScenarioValidator().Validate(scenario!);
            Assert.True(load.Ok, load.Error);
        }

        /// <summary>The converse arm, and the reason the entry was filed: a map with an undeclared placement slot is
        /// rejected by the LOADER, so the old LLM gate's "validated" was a promise the loader would break. Pins that
        /// the loader really does refuse it (i.e. the two gates now agree on BOTH answers, not just the happy one).</summary>
        [Fact]
        public void ValidateScenario_UndeclaredPlacementSlot_IsAlsoRejectedByTheLoadGate()
        {
            var model = new ScenarioData
            {
                MapBounds = 120f,
                WinCondition = WinCondition.DestroyAllBuildings,
                PlayerSlots = new[]
                {
                    new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json", StartOre = 200f, BaseX = -45f, BaseZ = 0f },
                    new ScenarioPlayerSlot { Slot = 1, FactionJson = "res://b.json", StartOre = 200f, BaseX =  45f, BaseZ = 0f },
                },
                ResourceNodes = new[] { new ScenarioResourceNode { X = 10f, Z = 10f, Supply = 400f, Rate = 5f, MaxGatherers = 4 } },
                Buildings = new[] { new ScenarioBuilding { Type = "CommandCenter", Slot = 3, X = -45f, Z = 0f } },
                Units = Array.Empty<ScenarioUnit>(),
            };

            ValidationResult load = new ScenarioValidator().Validate(model);

            Assert.False(load.Ok);
            Assert.Contains("references no declared player_slot", load.Error!);
        }

        // ══════════════════════════════════════════════════════════════════════
        // DW-627 — the building-type vocabulary (scenario buildings)
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>RED before DW-627: <see cref="BuildingType.Aviary"/> has existed since Story 2.8 and the loader
        /// accepts it, but the generation gate's hardcoded 4-name set rejected it — a legitimate BUILT-IN reference
        /// threw away the whole (paid) generation.</summary>
        [Fact]
        public void ValidateScenario_AcceptsTheBuiltInAviary()
        {
            var (scenario, error) = LLMService.ValidateScenario(
                Scenario(Building("Aviary", slot: 0), Unit("worker", slot: 0)), MapCtx());

            Assert.Null(error);
            Assert.NotNull(scenario);
        }

        /// <summary>Every placeable member of the real enum is accepted — derived from the enum, so a member appended
        /// later cannot silently fall out of the generation gate the way Aviary did.</summary>
        [Fact]
        public void ValidateScenario_AcceptsEveryPlaceableBuiltInBuildingType()
        {
            foreach (string name in ScenarioValidator.PlaceableBuildingTypeNames)
            {
                var (scenario, error) = LLMService.ValidateScenario(
                    Scenario(Building(name, slot: 0), Unit("worker", slot: 0)), MapCtx());

                Assert.True(scenario != null, $"built-in building type '{name}' was rejected: {error}");
            }
        }

        /// <summary>The <see cref="BuildingType.Custom"/> SENTINEL is not a placeable identity (it resolves no def →
        /// a stat-less, unrendered ghost), exactly as the load gate treats it. Guards the over-widening half: naively
        /// switching to <c>Enum.GetNames</c> would have accepted it.</summary>
        [Fact]
        public void ValidateScenario_RejectsTheCustomSentinelName()
        {
            var (scenario, error) = LLMService.ValidateScenario(
                Scenario(Building("Custom", slot: 0), Unit("worker", slot: 0)), MapCtx());

            Assert.Null(scenario);
            Assert.Contains("Unknown building type 'Custom'", error);
        }

        /// <summary>With no trusted per-slot defs threaded (the default), the check stays enum-name-only — the same
        /// amnesty the Story 6.8 load gate applies — so an authored id still fails closed.</summary>
        [Fact]
        public void ValidateScenario_RejectsAnAuthoredCustomId_WhenNoFactionDefsAreThreaded()
        {
            var (scenario, error) = LLMService.ValidateScenario(
                Scenario(Building(CustomId, slot: 0), Unit("worker", slot: 0)), MapCtx());

            Assert.Null(scenario);
            Assert.Contains($"Unknown building type '{CustomId}'", error);
            // The message names the REAL vocabulary, including the member the stale copy omitted.
            Assert.Contains(nameof(BuildingType.Aviary), error!);
        }

        /// <summary>RED before DW-627: with the trusted per-slot defs threaded, a generated map may pre-place a
        /// building the OWNING slot's faction actually authors — the same second vocabulary hand-authored maps have
        /// had since Story 6.8.</summary>
        [Fact]
        public void ValidateScenario_AcceptsAnAuthoredCustomId_WhenTheOwnerFactionAuthorsIt()
        {
            var ctx = MapCtx();
            ctx.SlotFactionDefs = SlotDefs(FactionWithWatchtower(), FactionWithoutWatchtower());

            var (scenario, error) = LLMService.ValidateScenario(
                Scenario(Building(CustomId, slot: 0), Unit("worker", slot: 0)), ctx);

            Assert.Null(error);
            Assert.NotNull(scenario);
        }

        /// <summary>The faction qualifier is load-bearing: the SAME id on the slot whose faction does NOT author it
        /// still fails closed. Also pins the slot→def indexing convention (slot + 1) — an off-by-one would accept
        /// this.</summary>
        [Fact]
        public void ValidateScenario_RejectsAnAuthoredCustomId_OnASlotWhoseFactionDoesNotAuthorIt()
        {
            var ctx = MapCtx();
            ctx.SlotFactionDefs = SlotDefs(FactionWithWatchtower(), FactionWithoutWatchtower());

            var (scenario, error) = LLMService.ValidateScenario(
                Scenario(Building(CustomId, slot: 1, x: 45), Unit("worker", slot: 0)), ctx);

            Assert.Null(scenario);
            Assert.Contains($"Unknown building type '{CustomId}'", error);
        }

        // ══════════════════════════════════════════════════════════════════════
        // DW-627 — the building-type vocabulary (trigger event / condition)
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>RED before DW-627 — the headline: the private shadow enum rejected the built-in Aviary on the
        /// trigger channel too.</summary>
        [Fact]
        public void Validate_AcceptsTheBuiltInAviary_OnTheEventChannel()
        {
            var (trigger, error) = LLMService.Validate(EventTrigger("Aviary", faction: 0), TriggerCtx());

            Assert.Null(error);
            Assert.NotNull(trigger);
        }

        /// <summary>Same, on the condition channel (<c>building_exists</c>).</summary>
        [Fact]
        public void Validate_AcceptsTheBuiltInAviary_OnTheConditionChannel()
        {
            var (trigger, error) = LLMService.Validate(ConditionTrigger("Aviary", faction: 1), TriggerCtx());

            Assert.Null(error);
            Assert.NotNull(trigger);
        }

        /// <summary>The <c>Custom</c> sentinel is not nameable from a trigger either — the shadow enum never had the
        /// member at all, so this is a NEW fail-closed guarantee rather than preserved behavior.</summary>
        [Fact]
        public void Validate_RejectsTheCustomSentinelName()
        {
            var (trigger, error) = LLMService.Validate(EventTrigger("Custom", faction: 0), TriggerCtx());

            Assert.Null(trigger);
            Assert.Contains("Unknown building_type 'Custom'", error);
        }

        /// <summary>With no defs threaded the trigger gate stays enum-only, and the rejection message now lists the
        /// REAL vocabulary (the shadow enum's message listed four names, omitting Aviary).</summary>
        [Fact]
        public void Validate_RejectsAnAuthoredCustomId_WhenNoFactionDefsAreThreaded()
        {
            var (trigger, error) = LLMService.Validate(EventTrigger(CustomId, faction: 0), TriggerCtx());

            Assert.Null(trigger);
            Assert.Contains($"Unknown building_type '{CustomId}'", error);
            Assert.Contains(nameof(BuildingType.Aviary), error!);
        }

        /// <summary>RED before DW-627: an LLM-generated trigger naming a faction-qualified custom building was
        /// rejected UPSTREAM of the DW-170 load gate that was widened to accept exactly this.</summary>
        [Fact]
        public void Validate_AcceptsAnAuthoredCustomId_WhenTheReferencingFactionAuthorsIt()
        {
            var ctx = TriggerCtx();
            ctx.SlotFactionDefs = SlotDefs(FactionWithWatchtower(), FactionWithoutWatchtower());

            var (trigger, error) = LLMService.Validate(EventTrigger(CustomId, faction: 0), ctx);

            Assert.Null(error);
            Assert.NotNull(trigger);
        }

        /// <summary>The qualifier's fail-closed arm — faction 1 does not author the watchtower, so the same id on
        /// that slot is still rejected (DW-170's own contract, now enforced at the generation gate too).</summary>
        [Fact]
        public void Validate_RejectsAnAuthoredCustomId_ForAFactionThatDoesNotAuthorIt()
        {
            var ctx = TriggerCtx();
            ctx.SlotFactionDefs = SlotDefs(FactionWithWatchtower(), FactionWithoutWatchtower());

            var (trigger, error) = LLMService.Validate(EventTrigger(CustomId, faction: 1), ctx);

            Assert.Null(trigger);
            Assert.Contains($"Unknown building_type '{CustomId}'", error);
        }

        /// <summary>A numeric string is still rejected. <c>Enum.TryParse</c> — what the shadow-enum gate used —
        /// accepts <c>"5"</c>, so switching to the load gate's NAME-only predicate closes that hole rather than
        /// merely widening the vocabulary.</summary>
        [Fact]
        public void Validate_RejectsANumericBuildingType()
        {
            var (trigger, error) = LLMService.Validate(EventTrigger("4", faction: 0), TriggerCtx());

            Assert.Null(trigger);
            Assert.Contains("Unknown building_type '4'", error);
        }

        // ══════════════════════════════════════════════════════════════════════
        // Prompt staleness guard — the request side must name what the gate enforces
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Both prompts enumerate EVERY placeable <see cref="BuildingType"/> member and never the
        /// <c>Custom</c> sentinel. Before DW-627 both hardcoded the same stale four names, so the model was told a
        /// shipped built-in did not exist. This is the staleness guard the Story 8.3 prompt/clamp tests establish as
        /// this file's convention: a member appended to the enum but not reaching the prompts fails here.
        /// </summary>
        [Fact]
        public void BothPrompts_EnumerateEveryPlaceableBuildingType_AndNeverTheCustomSentinel()
        {
            string triggerPrompt = LLMService.BuildSystemPrompt(TriggerCtx());
            string mapPrompt     = LLMService.BuildMapSystemPrompt(MapCtx());

            foreach (string name in ScenarioValidator.PlaceableBuildingTypeNames)
            {
                Assert.True(triggerPrompt.Contains($"\"{name}\""), $"trigger prompt omits building type '{name}'");
                Assert.True(mapPrompt.Contains($"\"{name}\""),     $"map prompt omits building type '{name}'");
            }

            Assert.DoesNotContain($"\"{nameof(BuildingType.Custom)}\"", triggerPrompt);
            Assert.DoesNotContain($"\"{nameof(BuildingType.Custom)}\"", mapPrompt);
        }

        /// <summary>The placeable vocabulary IS the enum minus the sentinel — asserted directly so a future edit that
        /// hand-lists it (the exact regression DW-627 names) fails here rather than silently going stale.</summary>
        [Fact]
        public void PlaceableBuildingTypeNames_AreTheEnumMinusTheCustomSentinel()
        {
            var expected = new List<string>();
            foreach (string n in Enum.GetNames(typeof(BuildingType)))
                if (n != nameof(BuildingType.Custom)) expected.Add(n);

            Assert.Equal(expected, ScenarioValidator.PlaceableBuildingTypeNames);
            Assert.Contains(nameof(BuildingType.Aviary), ScenarioValidator.PlaceableBuildingTypeNames);
        }
    }
}
