#nullable enable
using System.Text.Json;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 1.11 (AC3 — Decision #1) — the LLM-Trigger validated-only smoke test. Proves the AR-39 gate now
    /// inspects <c>Triggers[]</c> (it previously did not — an accepted LLM/editor trigger reached
    /// <c>ScenarioDirector</c> entirely unvalidated). A well-formed trigger passes and mints a
    /// <see cref="Validated{T}"/> wrapping the same model; each malformed case (unknown event/condition/action
    /// type, invalid faction slot, unknown building_type, invalid operator, out-of-range spawn coordinate,
    /// dangling timer reference) is rejected with a single LOCATED <c>scenario.triggers[...]</c> error and never
    /// reaches the tick.
    ///
    /// AC3c — no LLM is invoked anywhere: crafted <see cref="TriggerDefinition"/>s are fed through the pure-C#
    /// gate. The no-bypass guarantee (a <see cref="Validated{T}"/> is sole-minted by
    /// <see cref="ScenarioValidator"/>) is covered structurally by <c>ValidatedMintingTests</c>' source scan —
    /// this change adds NO new <c>new Validated&lt;</c> (it reuses the validator's single mint), so that scan
    /// stays green. The residual editor-accept routing seam (<c>TriggerEditorPanel.OnAcceptPressed</c> appends
    /// without the applier) is the documented Decision #1 follow-up.
    /// </summary>
    public class TriggerValidationTests
    {
        private static ScenarioValidator NewValidator() => new();

        /// <summary>
        /// A minimal VALID model (mirrors <c>NegativeValidationTests.ValidModel</c>) carrying one well-formed
        /// trigger that exercises an event (with operator), a building_type condition, a spawn action, and a
        /// create_timer / timer_expires pair so the dangling-timer check has a SATISFIED reference.
        /// </summary>
        private static ScenarioData ValidModelWithTrigger() => new ScenarioData
        {
            MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[]
            {
                new ScenarioPlayerSlot { Slot = 0, FactionJson = "res://a.json", StartOre = 200f, BaseX = -45f, BaseZ = 0f },
                new ScenarioPlayerSlot { Slot = 1, FactionJson = "res://b.json", StartOre = 200f, BaseX =  45f, BaseZ = 0f },
            },
            ResourceNodes = new[] { new ScenarioResourceNode { X = 10f, Z = 10f, Supply = 400f, Rate = 5f, MaxGatherers = 4 } },
            Buildings = new[] { new ScenarioBuilding { Type = "CommandCenter", Slot = 0, X = -45f, Z = 0f, PreBuilt = true } },
            Units = new[] { new ScenarioUnit { UnitId = "worker", Slot = 1, X = 42f, Z = 3f } },
            Triggers = new[]
            {
                new TriggerDefinition
                {
                    Name = "wave",
                    Events = new[]
                    {
                        new TriggerEvent { Type = "resource_threshold", Faction = 1, Amount = Fixed.FromInt(500), Operator = ">=" },
                        new TriggerEvent { Type = "timer_expires", TimerName = "spawn_clock" },
                    },
                    Conditions = new[]
                    {
                        new TriggerCondition { Type = "building_exists", Faction = 1, BuildingType = "Barracks", Operator = ">=" },
                    },
                    Actions = new[]
                    {
                        new TriggerAction { Type = "create_timer", TimerName = "spawn_clock", TimerSeconds = Fixed.FromInt(30) },
                        new TriggerAction { Type = "spawn_unit", UnitId = "soldier", Faction = 1, X = Fixed.FromInt(40), Z = Fixed.FromInt(5), Count = 3 },
                    },
                },
            },
        };

        [Fact]
        public void ValidTrigger_Passes_AndMintsValidatedWrappingSameModel()
        {
            var model = ValidModelWithTrigger();
            ValidationResult r = NewValidator().Validate(model);
            Assert.True(r.Ok, r.Error);
            Assert.Null(r.Error);
            Assert.Same(model, r.Value.Value); // AC3b: the validator is the minter; it wraps the very instance validated
        }

        [Fact]
        public void EventFactionSlotAboveEngineCeiling_IsRejected_LocatingTheEventFaction()
        {
            var m = ValidModelWithTrigger();
            m.Triggers[0].Events[0].Faction = 5; // ScenarioDirector would do (Faction)6 → Ore[6], an OOB crash
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("triggers[0].events[0].faction", r.Error!);
        }

        [Fact]
        public void NegativeFactionSlotInCondition_IsRejected()
        {
            var m = ValidModelWithTrigger();
            m.Triggers[0].Conditions[0].Faction = -1;
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("triggers[0].conditions[0].faction", r.Error!);
        }

        [Fact]
        public void UnknownBuildingTypeInCondition_IsRejected_NotSilentlyInert()
        {
            var m = ValidModelWithTrigger();
            m.Triggers[0].Conditions[0].BuildingType = "Frost"; // building_exists would silently never match → dead trigger
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("triggers[0].conditions[0].building_type", r.Error!);
        }

        [Fact]
        public void InvalidOperator_IsRejected()
        {
            var m = ValidModelWithTrigger();
            m.Triggers[0].Events[0].Operator = "=>"; // not in {>,<,>=,<=,==,!=} → Compare returns false forever
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("triggers[0].events[0].operator", r.Error!);
        }

        [Fact]
        public void OutOfRangeSpawnCoordinate_IsRejectedAtTheJsonBoundary_ByFixedJsonConverter()
        {
            // Story 7.1: TriggerAction.X/Z are now Fixed, quantized at the JSON boundary. An out-of-16.16-range
            // spawn coordinate can no longer even be constructed in code (a Fixed cannot hold ±40000); the finite/
            // range gate MOVED to FixedJsonConverter, which rejects it at deserialize time with a located
            // "16.16 range" error — exactly where the spec relocates the check. The validator's own coordinate gate
            // now covers only map_bounds (proven by SpawnCoordinateOutsideMapBounds_IsRejected below).
            var options = new JsonSerializerOptions { Converters = { new FixedJsonConverter() } };
            const string json = "{\"type\":\"spawn_unit\",\"unit_id\":\"soldier\",\"x\":40000,\"z\":5}";
            var ex = Assert.Throws<JsonException>(
                () => JsonSerializer.Deserialize<TriggerAction>(json, options));
            Assert.Contains("16.16 range", ex.Message);
        }

        [Fact]
        public void SpawnCoordinateOutsideMapBounds_IsRejected()
        {
            var m = ValidModelWithTrigger();
            m.Triggers[0].Actions[1].Z = Fixed.FromInt(200); // inside the Fixed range but outside map_bounds 120
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("triggers[0].actions[1].z", r.Error!);
            Assert.Contains("map_bounds", r.Error!);
        }

        [Fact]
        public void DanglingTimerReference_IsRejected()
        {
            var m = ValidModelWithTrigger();
            m.Triggers[0].Events[1].TimerName = "ghost_clock"; // no create_timer creates "ghost_clock" → dangling
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("triggers[0].events[1].timer_name", r.Error!);
        }

        [Fact]
        public void UnknownEventType_IsRejected()
        {
            var m = ValidModelWithTrigger();
            m.Triggers[0].Events[0].Type = "on_eclipse"; // not a known event type — would silently never fire
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("triggers[0].events[0].type", r.Error!);
        }

        [Fact]
        public void UnknownActionType_IsRejected()
        {
            var m = ValidModelWithTrigger();
            m.Triggers[0].Actions[1].Type = "nuke_everything"; // break the spawn action (not the create_timer, to keep the timer ref satisfied)
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("triggers[0].actions[1].type", r.Error!);
        }

        [Fact]
        public void NullTriggersArray_IsRejected()
        {
            var m = ValidModelWithTrigger();
            m.Triggers = null!;
            ValidationResult r = NewValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("triggers", r.Error!);
        }

        [Fact]
        public void Validate_NeverThrows_OnTriggerWithNullSubArrays()
        {
            // Purity: null Events/Conditions/Actions inside a trigger must NOT throw (the validator treats them as
            // empty via `?? Array.Empty`), so a partially-deserialized trigger yields a located result, not an NRE.
            var m = ValidModelWithTrigger();
            m.Triggers[0].Events = null!;
            m.Triggers[0].Conditions = null!;
            m.Triggers[0].Actions = null!;
            var ex = Record.Exception(() => NewValidator().Validate(m));
            Assert.Null(ex);
        }

        // AC3c — AR-13 (a random effect is valid only if it draws from SimRng) stays RESERVED: no random
        // trigger-effect TYPE exists pre-Epic-2, so there is nothing to validate yet. This is the documented
        // pending case; the mature rule is enforced by Epic 2's effect-validator (Story 2.3) the first moment an
        // effect schema exists. Do NOT fabricate a random effect type here, and never invoke a real LLM in a test.
        [Fact(Skip = "AR-13 reserved until the Epic 2 effect schema (Story 2.3) — no random trigger-effect type exists pre-Epic-2.")]
        public void RandomEffect_MustDrawFromSimRng_ReservedUntilStory2_3() { }
    }
}
