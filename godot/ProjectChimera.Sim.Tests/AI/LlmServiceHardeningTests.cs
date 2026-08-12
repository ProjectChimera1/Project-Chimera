#nullable enable
using System;
using System.Globalization;
using System.Text;
using System.Threading;
using ProjectChimera.AI;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// The llm-service-hardening bundle — five ways <see cref="LLMService"/>'s generation gates and prompts
    /// contradicted either the LOAD gate that judges the same content or the rules the prompt itself prints.
    ///
    /// <list type="bullet">
    ///   <item><b>DW-742</b> — the trigger gate's pass 3 hardcoded faction slots to 0-or-1 on all three channels, so an
    ///         AI-generated trigger addressing player 3 was refused UPSTREAM of a load gate that accepts slots 0..7.
    ///         That is the same upstream-shadow-gate shape DW-627 closed, and it contradicts the recorded DW-189
    ///         intent (trigger-authored maps must support more than two players).</item>
    ///   <item><b>DW-743</b> — the scenario gate resolved a pre-placed <c>unit_id</c> against the FLAT cross-faction
    ///         union of every loaded roster, so a slot-1-faction unit placed on slot 0 "validated" and was then
    ///         refused/dropped by the DW-240 load gate. DW-542's defect class, for unit ids instead of slots.</item>
    ///   <item><b>DW-767</b> — the DW-542 block gates <c>buildings[].slot</c> and <c>units[].slot</c> and claims it
    ///         exists "so the two gates agree", but the load gate makes a THIRD identical check on an Income node's
    ///         <c>owner_slot</c> that the LLM gate never made.</item>
    ///   <item><b>DW-771</b> — the map prompt's worked example hardcoded its ore cross at ±25/±15, so under a bound
    ///         below 25 the example violated the "±MapBounds" rule printed directly above it (and failed the gate
    ///         built from the same context).</item>
    ///   <item><b>DW-772</b> — both prompts interpolated the float <c>MapBounds</c> with the AMBIENT culture, so a
    ///         comma-decimal locale printed "±120,5" — a separator the model can echo into the JSON it emits.</item>
    /// </list>
    /// Godot-free / Tier-1. Nothing here touches a value folded into SimChecksum.
    /// </summary>
    public class LlmServiceHardeningTests
    {
        // ── Fixtures ────────────────────────────────────────────────────────────

        private static MapGeneratorContext MapCtx(float bounds = 120f)
            => new() { UnitIds = new[] { "melee" }, MapBounds = bounds };

        private static ScenarioContext TriggerCtx()
            => new() { UnitIds = new[] { "melee" }, MapBounds = 120f };

        /// <summary>A faction whose roster is exactly {worker, melee} — the "alpha" slot-0 roster.</summary>
        private static FactionDefinition AlphaFaction()
        {
            var f = new FactionDefinition { Id = "alpha", DisplayName = "Alpha" };
            f.Units.Add(new UnitDefinition { Id = "worker", Category = "Worker", Hp = 50f });
            f.Units.Add(new UnitDefinition { Id = "melee",  Category = "Melee",  Hp = 120f });
            return f;
        }

        /// <summary>A faction whose roster is exactly {worker, archer} — it does NOT declare "melee", which is what
        /// makes the cross-faction placement in the DW-743 tests a real reference error rather than a naming one.</summary>
        private static FactionDefinition BetaFaction()
        {
            var f = new FactionDefinition { Id = "beta", DisplayName = "Beta" };
            f.Units.Add(new UnitDefinition { Id = "worker", Category = "Worker", Hp = 50f });
            f.Units.Add(new UnitDefinition { Id = "archer", Category = "Ranged", Hp = 90f });
            return f;
        }

        /// <summary>Per-slot defs indexed by <c>(int)Faction</c> (slot + 1) — the SAME array shape the load gate, the
        /// applier and <see cref="ScenarioValidator.OwnerFactionDef"/> use.</summary>
        private static FactionDefinition?[] SlotDefs(params FactionDefinition?[] bySlot)
        {
            var defs = new FactionDefinition?[FactionRegistry.FACTION_ARRAY_SIZE];
            for (int slot = 0; slot < bySlot.Length; slot++) defs[slot + 1] = bySlot[slot];
            return defs;
        }

        /// <summary>A structurally valid scenario declaring <paramref name="slotCount"/> player slots; the caller
        /// supplies the raw buildings/units/resource-node array bodies. Everything not under examination is valid.</summary>
        private static string Scenario(string buildings, string units, string? resourceNodes = null, int slotCount = 2)
        {
            var sb = new StringBuilder();
            sb.Append("{\"player_slots\":[");
            for (int s = 0; s < slotCount; s++)
                sb.Append(s == 0 ? "" : ",")
                  .Append($"{{\"slot\":{s},\"faction_json\":\"f{s}.json\",\"base_x\":{(s % 2 == 0 ? -45 : 45)},\"base_z\":0}}");
            sb.Append("],\"resource_nodes\":[")
              .Append(resourceNodes ?? "{\"x\":-25,\"z\":15,\"supply\":600,\"rate\":5},{\"x\":25,\"z\":-15,\"supply\":600,\"rate\":5}");
            sb.Append("],\"buildings\":[").Append(buildings);
            sb.Append("],\"units\":[").Append(units);
            sb.Append("]}");
            return sb.ToString();
        }

        private static string Building(string type, int slot, int x = -45, int z = 0)
            => $"{{\"type\":\"{type}\",\"slot\":{slot},\"x\":{x},\"z\":{z}}}";

        private static string Unit(string unitId, int slot, int x = -42, int z = 3)
            => $"{{\"unit_id\":\"{unitId}\",\"slot\":{slot},\"x\":{x},\"z\":{z}}}";

        /// <summary>A flat trigger addressing <paramref name="faction"/> on the requested channel.</summary>
        private static string TriggerOn(string channel, int faction) => channel switch
        {
            "event"     => "{\"name\":\"T\",\"events\":[{\"type\":\"unit_dies\",\"faction\":" + faction + "}]," +
                           "\"conditions\":[],\"actions\":[{\"type\":\"victory\",\"faction\":0}]}",
            "condition" => "{\"name\":\"T\",\"events\":[{\"type\":\"match_start\"}]," +
                           "\"conditions\":[{\"type\":\"unit_count\",\"faction\":" + faction + ",\"count\":1,\"operator\":\">\"}]," +
                           "\"actions\":[{\"type\":\"victory\",\"faction\":0}]}",
            _           => "{\"name\":\"T\",\"events\":[{\"type\":\"match_start\"}],\"conditions\":[]," +
                           "\"actions\":[{\"type\":\"victory\",\"faction\":" + faction + "}]}",
        };

        // ══════════════════════════════════════════════════════════════════════
        // DW-742 — the trigger gate's faction-slot range
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>With nothing threaded (every shipping caller today) the historical 0-or-1 range and its exact
        /// message survive untouched — the widening must be opt-in, never a silent behaviour change.</summary>
        [Theory]
        [InlineData("event")]
        [InlineData("condition")]
        [InlineData("action")]
        public void Validate_WithNoFactionDefsThreaded_StillRefusesSlotTwo(string channel)
        {
            var (trigger, error) = LLMService.Validate(TriggerOn(channel, faction: 2), TriggerCtx());

            Assert.Null(trigger);
            Assert.Contains("invalid faction slot 2", error);
        }

        /// <summary>The historical parenthetical is preserved character for character on the unthreaded default.</summary>
        [Fact]
        public void Validate_UnthreadedRejection_KeepsTheHistoricalMustBeZeroOrOneWording()
        {
            var (_, error) = LLMService.Validate(TriggerOn("event", faction: 2), TriggerCtx());
            Assert.Contains("(must be 0 or 1).", error);
        }

        /// <summary>RED before DW-742 on all three channels: with the trusted per-slot defs threaded, a generated
        /// trigger may address player 3 — the widened runtime DW-189 mandates, no longer shadowed by this gate.</summary>
        [Theory]
        [InlineData("event")]
        [InlineData("condition")]
        [InlineData("action")]
        public void Validate_WithFactionDefsThreaded_AcceptsASlotAboveOne(string channel)
        {
            var ctx = TriggerCtx();
            ctx.SlotFactionDefs = SlotDefs(AlphaFaction(), BetaFaction(), AlphaFaction(), BetaFaction());

            var (trigger, error) = LLMService.Validate(TriggerOn(channel, faction: 2), ctx);

            Assert.True(trigger != null, $"{channel} channel still refuses slot 2: {error}");
            Assert.Null(error);
        }

        /// <summary>The widened range agrees with the LOAD gate's own ceiling rather than inventing one: slot 7 is the
        /// last <see cref="Faction"/> the engine's per-faction arrays address, and slot 8 is refused.</summary>
        [Fact]
        public void Validate_WithFactionDefsThreaded_StopsAtTheEnginesOwnFactionCeiling()
        {
            var ctx = TriggerCtx();
            ctx.SlotFactionDefs = SlotDefs(AlphaFaction(), BetaFaction());

            Assert.Equal((int)Faction.Player8 - 1, LLMService.MaxTriggerFactionSlot(ctx));

            var (accepted, _)      = LLMService.Validate(TriggerOn("event", faction: 7), ctx);
            var (refused, refErr)  = LLMService.Validate(TriggerOn("event", faction: 8), ctx);

            Assert.NotNull(accepted);
            Assert.Null(refused);
            Assert.Contains("invalid faction slot 8", refErr);
        }

        /// <summary>A negative slot is still refused on every channel — the widening moved the ceiling, not the floor
        /// (a negative slot would index the director's per-faction arrays out of range).</summary>
        [Theory]
        [InlineData("event")]
        [InlineData("condition")]
        [InlineData("action")]
        public void Validate_RejectsANegativeFactionSlot_ThreadedOrNot(string channel)
        {
            var threaded = TriggerCtx();
            threaded.SlotFactionDefs = SlotDefs(AlphaFaction(), BetaFaction());

            Assert.Null(LLMService.Validate(TriggerOn(channel, faction: -1), TriggerCtx()).trigger);
            Assert.Null(LLMService.Validate(TriggerOn(channel, faction: -1), threaded).trigger);
        }

        /// <summary>Threading defs may only ever WIDEN. A short array (one that cannot even address slot 1) must not
        /// narrow the gate below the historical range, or a caller threading defs would start losing 1v1 triggers.</summary>
        [Fact]
        public void MaxTriggerFactionSlot_NeverNarrowsBelowTheHistoricalRange()
        {
            Assert.Equal(1, LLMService.MaxTriggerFactionSlot(new ScenarioContext()));                       // null defs
            Assert.Equal(1, LLMService.MaxTriggerFactionSlot(
                new ScenarioContext { SlotFactionDefs = Array.Empty<FactionDefinition?>() }));              // empty
            Assert.Equal(1, LLMService.MaxTriggerFactionSlot(
                new ScenarioContext { SlotFactionDefs = new FactionDefinition?[2] }));                      // addresses slot 0 only
            Assert.Equal(3, LLMService.MaxTriggerFactionSlot(
                new ScenarioContext { SlotFactionDefs = new FactionDefinition?[5] }));                      // slots 0..3
        }

        // ══════════════════════════════════════════════════════════════════════
        // DW-743 — the pre-placed unit_id vocabulary
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>RED before DW-743: "melee" is in the flat cross-faction union (alpha declares it, beta does not),
        /// so a unit placed on BETA's slot sailed through the union check and the DW-240 load gate then refused the
        /// map. The located error names the roster that was actually searched.</summary>
        [Fact]
        public void ValidateScenario_RejectsAUnitIdTheOwningSlotsFactionDoesNotDeclare()
        {
            var ctx = MapCtx();
            ctx.UnitIds         = new[] { "worker", "melee", "archer" };   // the flat union MapGeneratorPhase builds
            ctx.SlotFactionDefs = SlotDefs(AlphaFaction(), BetaFaction());

            var (scenario, error) = LLMService.ValidateScenario(
                Scenario(Building("CommandCenter", slot: 0),
                         Unit("melee", slot: 1, x: 42)),   // beta's slot; beta declares worker/archer only
                ctx);

            Assert.Null(scenario);
            Assert.Contains("units[0].unit_id='melee'", error);
            Assert.Contains("slot 1", error!);
            Assert.Contains("beta", error!);
        }

        /// <summary>The same id on the slot whose faction DOES declare it still passes — the check is a faction
        /// qualifier, not a blanket tightening.</summary>
        [Fact]
        public void ValidateScenario_AcceptsAUnitIdTheOwningSlotsFactionDeclares()
        {
            var ctx = MapCtx();
            ctx.UnitIds         = new[] { "worker", "melee", "archer" };
            ctx.SlotFactionDefs = SlotDefs(AlphaFaction(), BetaFaction());

            var (scenario, error) = LLMService.ValidateScenario(
                Scenario(Building("CommandCenter", slot: 0),
                         Unit("melee", slot: 0) + "," + Unit("archer", slot: 1, x: 42)),
                ctx);

            Assert.Null(error);
            Assert.NotNull(scenario);
        }

        /// <summary>The unconditional <c>"worker"</c> amnesty the union check carried is no longer a free pass once
        /// defs are threaded: a faction that does not declare "worker" refuses one, exactly as the applier would drop
        /// it. (Both shipping factions DO declare one — this pins the predicate, not a policy change.)</summary>
        [Fact]
        public void ValidateScenario_DoesNotHandWaveWorker_WhenTheOwningFactionHasNoWorker()
        {
            var workerless = new FactionDefinition { Id = "gamma", DisplayName = "Gamma" };
            workerless.Units.Add(new UnitDefinition { Id = "drone", Category = "Worker", Hp = 50f });

            var ctx = MapCtx();
            ctx.UnitIds         = new[] { "drone" };
            ctx.SlotFactionDefs = SlotDefs(workerless, workerless);

            var (scenario, error) = LLMService.ValidateScenario(
                Scenario(Building("CommandCenter", slot: 0), Unit("worker", slot: 0)), ctx);

            Assert.Null(scenario);
            Assert.Contains("units[0].unit_id='worker'", error);
        }

        /// <summary>With NOTHING threaded the historical union check and its exact message survive — this is the same
        /// amnesty the DW-627 building-type pass applies, so nothing shipping changes until DW-741 threads the defs.</summary>
        [Fact]
        public void ValidateScenario_WithNoFactionDefsThreaded_KeepsTheHistoricalUnionCheck()
        {
            var ctx = MapCtx();
            ctx.UnitIds = new[] { "worker", "melee", "archer" };

            // Cross-faction placement that DW-743 only rejects once defs are threaded.
            var (accepted, acceptErr) = LLMService.ValidateScenario(
                Scenario(Building("CommandCenter", slot: 0), Unit("melee", slot: 1, x: 42)), ctx);
            Assert.Null(acceptErr);
            Assert.NotNull(accepted);

            // …and an id outside the union still fails with the historical message shape.
            var (refused, refErr) = LLMService.ValidateScenario(
                Scenario(Building("CommandCenter", slot: 0), Unit("dragon", slot: 0)), ctx);
            Assert.Null(refused);
            Assert.Contains("Unknown unit_id 'dragon'", refErr);
        }

        /// <summary>DW-652's amnesty is mirrored: a unit the faction really DECLARED but that the closed-set tag
        /// validator removed is not an authoring error — the load gate drops that one entity and loads the rest, so
        /// failing the whole generation here would just point the shadow gate the other way.</summary>
        [Fact]
        public void ValidateScenario_AcceptsAUnitTheTagValidatorDropped_MirroringTheLoadGate()
        {
            FactionDefinition alpha = AlphaFaction();
            alpha.NoteTagDroppedUnit("scout");   // declared by the author, removed by the tag validator

            var ctx = MapCtx();
            ctx.UnitIds         = new[] { "worker", "melee" };
            ctx.SlotFactionDefs = SlotDefs(alpha, BetaFaction());

            var (scenario, error) = LLMService.ValidateScenario(
                Scenario(Building("CommandCenter", slot: 0), Unit("scout", slot: 0)), ctx);

            Assert.Null(error);
            Assert.NotNull(scenario);
        }

        /// <summary>The property the entry is really about, asserted end to end: a generated map the LLM gate PASSES
        /// is one whose pre-placed unit ids the <see cref="ScenarioValidator"/> load gate also resolves.</summary>
        [Fact]
        public void ValidateScenario_PassingGeneratedMap_AlsoPassesTheLoadGatesUnitIdCheck()
        {
            FactionDefinition?[] defs = SlotDefs(AlphaFaction(), BetaFaction());
            var ctx = MapCtx();
            ctx.UnitIds         = new[] { "worker", "melee", "archer" };
            ctx.SlotFactionDefs = defs;

            var (scenario, error) = LLMService.ValidateScenario(
                Scenario(Building("CommandCenter", slot: 0) + "," + Building("CommandCenter", slot: 1, x: 45),
                         Unit("melee", slot: 0) + "," + Unit("archer", slot: 1, x: 42)),
                ctx);
            Assert.Null(error);

            ValidationResult load = new ScenarioValidator().Validate(scenario!, defs);
            Assert.True(load.Ok, load.Error);
        }

        /// <summary>The converse arm — the reason the entry was filed. The cross-faction placement the OLD gate called
        /// "validated" really is refused by the loader, so the generation UX was promising something it could not keep.</summary>
        [Fact]
        public void ValidateScenario_CrossFactionUnitPlacement_IsAlsoRejectedByTheLoadGate()
        {
            FactionDefinition?[] defs = SlotDefs(AlphaFaction(), BetaFaction());
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
                Buildings = Array.Empty<ScenarioBuilding>(),
                Units = new[] { new ScenarioUnit { UnitId = "melee", Slot = 1, X = 42f, Z = 3f } },
            };

            ValidationResult load = new ScenarioValidator().Validate(model, defs);

            Assert.False(load.Ok);
            Assert.Contains("names no unit in the roster", load.Error!);
        }

        // ══════════════════════════════════════════════════════════════════════
        // DW-767 — the Income node's owner_slot
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>RED before DW-767, and reachable by pure OMISSION: the model names the collection model and leaves
        /// <c>owner_slot</c> out, which deserializes to the -1 default. The gate reported "validated" and the loader
        /// then refused it with the very message this check now mirrors.</summary>
        [Fact]
        public void ValidateScenario_RejectsAnIncomeNodeWhoseOwnerSlotIsUndeclared()
        {
            var (scenario, error) = LLMService.ValidateScenario(
                Scenario(Building("CommandCenter", slot: 0), Unit("worker", slot: 0),
                         resourceNodes: "{\"x\":0,\"z\":0,\"supply\":400,\"rate\":5,\"collection_model\":\"Income\"}"),
                MapCtx());

            Assert.Null(scenario);
            Assert.Contains("resource_nodes[0].owner_slot=-1", error);
            Assert.Contains("references no declared player_slot", error!);
            Assert.Contains("collection_model=Income", error!);
        }

        /// <summary>An out-of-range (rather than omitted) owner_slot is the same defect from the other side, and the
        /// located index names the OFFENDING row rather than the first.</summary>
        [Fact]
        public void ValidateScenario_RejectsAnIncomeNodeOnAnUndeclaredSlot_NamingTheRightRow()
        {
            var (scenario, error) = LLMService.ValidateScenario(
                Scenario(Building("CommandCenter", slot: 0), Unit("worker", slot: 0),
                         resourceNodes:
                             "{\"x\":-25,\"z\":15,\"supply\":400,\"rate\":5,\"collection_model\":\"Income\",\"owner_slot\":1}," +
                             "{\"x\":25,\"z\":-15,\"supply\":400,\"rate\":5,\"collection_model\":\"Income\",\"owner_slot\":4}"),
                MapCtx());

            Assert.Null(scenario);
            Assert.Contains("resource_nodes[1].owner_slot=4", error);
        }

        /// <summary>An Income node naming a DECLARED slot passes, and a GATHER/Streaming node ignores owner_slot
        /// entirely (it credits the gathering worker's own faction) — exactly the load gate's rule, so the check does
        /// not start rejecting the shipping shape where owner_slot is simply absent.</summary>
        [Fact]
        public void ValidateScenario_AcceptsADeclaredIncomeOwner_AndIgnoresOwnerSlotForGather()
        {
            var (income, incomeErr) = LLMService.ValidateScenario(
                Scenario(Building("CommandCenter", slot: 0), Unit("worker", slot: 0),
                         resourceNodes: "{\"x\":0,\"z\":0,\"supply\":400,\"rate\":5,\"collection_model\":\"Income\",\"owner_slot\":1}"),
                MapCtx());
            Assert.Null(incomeErr);
            Assert.NotNull(income);

            var (gather, gatherErr) = LLMService.ValidateScenario(
                Scenario(Building("CommandCenter", slot: 0), Unit("worker", slot: 0),
                         resourceNodes: "{\"x\":0,\"z\":0,\"supply\":400,\"rate\":5}"),
                MapCtx());
            Assert.Null(gatherErr);
            Assert.NotNull(gather);
            Assert.Equal(-1, gather!.ResourceNodes[0].OwnerSlot);   // the default really is the invalid-for-Income one
        }

        /// <summary>The load gate really does refuse what the old LLM gate passed — the two-gates-agree property this
        /// entry restores, asserted from the loader's side so the LLM gate's "validated" is provably honest now.</summary>
        [Fact]
        public void IncomeNodeWithNoOwnerSlot_IsRejectedByTheLoadGateToo()
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
                ResourceNodes = new[]
                {
                    new ScenarioResourceNode
                    { X = 0f, Z = 0f, Supply = 400f, Rate = 5f, MaxGatherers = 4, CollectionModel = "Income" },
                },
                Buildings = Array.Empty<ScenarioBuilding>(),
                Units = Array.Empty<ScenarioUnit>(),
            };

            ValidationResult load = new ScenarioValidator().Validate(model);

            Assert.False(load.Ok);
            Assert.Contains("owner_slot=-1 references no declared player_slot", load.Error!);
        }

        // ══════════════════════════════════════════════════════════════════════
        // DW-771 — the example ore layout follows MapBounds
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>The shipping RTS prompt must not move: at the default bound the five ore rows are the pre-DW-771
        /// literals character for character, column alignment included.</summary>
        [Fact]
        public void MapPrompt_OreRowsAreByteIdenticalAtTheDefaultBounds()
        {
            const string Historical =
                "    { \"x\": -25, \"z\":  15, \"supply\": 600, \"rate\": 5, \"max_gatherers\": 4 },\n" +
                "    { \"x\": -25, \"z\": -15, \"supply\": 600, \"rate\": 5, \"max_gatherers\": 4 },\n" +
                "    { \"x\":   0, \"z\":   0, \"supply\": 900, \"rate\": 7, \"max_gatherers\": 4 },\n" +
                "    { \"x\":  25, \"z\":  15, \"supply\": 600, \"rate\": 5, \"max_gatherers\": 4 },\n" +
                "    { \"x\":  25, \"z\": -15, \"supply\": 600, \"rate\": 5, \"max_gatherers\": 4 }";

            Assert.Contains(Historical, LLMService.BuildMapSystemPrompt(MapCtx()));
            Assert.Equal((25, 15), LLMService.PromptOreOffsets(120f));
            // A bound WIDER than the default is capped back onto the shipping layout rather than sprawling.
            Assert.Equal((25, 15), LLMService.PromptOreOffsets(400f));
        }

        /// <summary>
        /// RED before DW-771 for every bound below 25: the worked example the model is told to imitate must itself
        /// PASS the gate built from the same context — which means in-bounds (pass 5) AND ≥15u apart (pass 6). The
        /// old hardcoded ±25 cross failed pass 5 outright at bounds 20 and below.
        /// </summary>
        [Theory]
        [InlineData(120f)]
        [InlineData(60f)]
        [InlineData(30f)]
        [InlineData(24f)]
        [InlineData(20f)]
        [InlineData(16f)]
        [InlineData(13f)]   // the floor at which the ±bounds rule and the 15u rule are jointly satisfiable
        public void PromptOreExample_PassesTheGateBuiltFromTheSameContext(float bounds)
        {
            var ctx = MapCtx(bounds);
            string example = LlmMapPromptSlotParameterizationTests.ExampleJson(LLMService.BuildMapSystemPrompt(ctx));

            var (scenario, error) = LLMService.ValidateScenario(example, ctx);

            Assert.True(scenario != null, $"bounds {bounds}: the prompt's own example fails the prompt's own gate — {error}");
            Assert.Equal(5, scenario!.ResourceNodes.Length);
            foreach (ScenarioResourceNode n in scenario.ResourceNodes)
                Assert.True(Math.Abs(n.X) <= bounds && Math.Abs(n.Z) <= bounds,
                    $"bounds {bounds}: ore node ({n.X}, {n.Z}) is outside the rule the prompt prints.");
        }

        /// <summary>The derived offsets satisfy the 15-unit spacing rule directly, swept far below any bound the
        /// example round-trip covers — the lift step is what stops "scale it" from trading one self-contradiction
        /// (out of bounds) for another (overlapping ore).</summary>
        [Theory]
        [InlineData(13f)]
        [InlineData(16f)]
        [InlineData(30f)]
        [InlineData(60f)]
        [InlineData(120f)]
        public void PromptOreOffsets_SatisfyTheFifteenUnitSpacingRule(float bounds)
        {
            (int x, int z) = LLMService.PromptOreOffsets(bounds);

            Assert.True(2 * z >= 15, $"bounds {bounds}: the two vertical ore neighbours are only {2 * z}u apart.");
            Assert.True(2 * x >= 15, $"bounds {bounds}: the two horizontal ore neighbours are only {2 * x}u apart.");
            Assert.True(Math.Sqrt(x * x + z * z) >= 15,
                $"bounds {bounds}: an ore arm is only {Math.Sqrt(x * x + z * z):F1}u from the centre node.");
            Assert.True(x <= (int)bounds && z <= (int)bounds, $"bounds {bounds}: the derived cross ({x}, {z}) is outside.");
        }

        // ══════════════════════════════════════════════════════════════════════
        // DW-772 — the bound is printed with the invariant culture
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// RED before DW-772 on both prompt builders: under a comma-decimal locale the ambient culture printed
        /// "±120,5", a separator the model can copy straight into the JSON it emits. Rendered on a DEDICATED thread so
        /// the culture switch cannot leak into any other test.
        /// </summary>
        [Fact]
        public void BothPrompts_PrintTheMapBoundWithAnInvariantDecimalSeparator()
        {
            const float NonIntegralBound = 120.5f;
            string mapPrompt = "", triggerPrompt = "";

            var worker = new Thread(() =>
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");   // comma decimal separator
                mapPrompt     = LLMService.BuildMapSystemPrompt(MapCtx(NonIntegralBound));
                triggerPrompt = LLMService.BuildSystemPrompt(new ScenarioContext
                { UnitIds = new[] { "melee" }, MapBounds = NonIntegralBound });
            });
            worker.Start();
            Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "the prompt-rendering thread did not finish");

            Assert.Contains("±120.5 world units", mapPrompt);
            Assert.DoesNotContain("120,5", mapPrompt);
            Assert.Contains("±120.5 on X and Z axes", triggerPrompt);
            Assert.DoesNotContain("120,5", triggerPrompt);
        }

        /// <summary>The integral bounds every shipping caller uses are unchanged by the invariant formatting — the fix
        /// must not move a single live prompt byte.</summary>
        [Fact]
        public void IntegralBounds_RenderExactlyAsBefore()
        {
            Assert.Contains("±120 world units", LLMService.BuildMapSystemPrompt(MapCtx()));
            Assert.Contains("±128 world units", LLMService.BuildMapSystemPrompt(MapCtx(128f)));
            Assert.Contains("±120 on X and Z axes", LLMService.BuildSystemPrompt(TriggerCtx()));
        }
    }
}
