#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Dsl;
using ProjectChimera.Economy;
using ProjectChimera.Multiplayer;
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>
    /// Story 7.9 — the custom-UI write rail's sim-side landing: the <c>OrderApplier</c> DslEvent branch decodes the
    /// 11-byte wire (eventIndex→UnitId, arg0→TargetX, arg1→TargetZ) and calls the DSL sink with the command's faction
    /// SLOT as raiser; <c>ScenarioDirector.TryEnqueueExternalDslEvent</c> authorizes it against the event's
    /// allowed_raisers (system −1 always legal), drops an unauthorized/out-of-range/at-capacity raise deterministically
    /// (no mutation, no throw), and an authorized raise enters the checksum-folded <see cref="DslEventQueue"/> and
    /// fires the subscribed trigger; and two headless runs over an identical button-command stream produce
    /// byte-identical SimChecksum sequences.
    /// </summary>
    public class DslEventCommandTests
    {
        private static ScenarioVariable IntVar(string name, int initial = 0) =>
            new() { Name = name, Type = DslValueType.Int, Scope = VarScope.Global, Initial = Fixed.FromInt(initial) };

        private static Dictionary<string, (DslValueType Type, VarScope Scope)> DeclMap(ScenarioVariable[] vars)
        {
            var map = new Dictionary<string, (DslValueType, VarScope)>(StringComparer.Ordinal);
            foreach (var v in vars) map[v.Name] = (v.Type, v.Scope);
            return map;
        }

        /// <summary>A scenario: a "buy" event (optionally 1-param) whose handler mutates <c>score</c>. The
        /// handler's value-expr is "score + 1" (0-param) or "event.amount" (1-param).</summary>
        private static ScenarioData BuyScenario(int[]? raisers, bool withArg)
        {
            ScenarioVariable[] vars = { IntVar("score") };
            var declMap = DeclMap(vars);
            var events = new[]
            {
                new ScenarioCustomEvent
                {
                    Name = "buy",
                    Params = withArg ? new[] { new ScenarioEventParam { Name = "amount", Type = DslValueType.Int } } : null,
                    AllowedRaisers = raisers,
                },
            };
            TriggerGraph handler = TriggerGraph.BuildCustomEventTrigger(
                "H", "custom_event", "buy", null, null, null, -1, false,
                "score", 0, withArg ? "event.amount" : "score + 1", declMap, events);
            return new ScenarioData { Variables = vars, CustomEvents = events, TriggerGraphJson = handler.ToCanonicalJson() };
        }

        private static (ScenarioDirector Director, DslVarTable Vars, DslEventQueue Queue) Build(ScenarioData scenario)
        {
            var vars = new DslVarTable();
            var queue = new DslEventQueue();
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), vars, eventQueue: queue);
            director.LoadScenario(scenario);
            return (director, vars, queue);
        }

        // ── OrderApplier decode branch ──────────────────────────────────────────

        [Fact]
        public void OrderApplier_DslEventBranch_DecodesWireAndCallsSink_WithFactionSlotRaiser()
        {
            (int idx, int raiser, int a0, int a1)? seen = null;
            Func<int, int, int, int, bool> sink = (i, r, x, z) => { seen = (i, r, x, z); return true; };

            // eventIndex 2 → UnitId; arg0=7 → TargetX; arg1=-3 → TargetZ. Faction Player2 → slot 1.
            var order = new UnitOrder(2, UnitCommand.DslEvent, Fixed.FromRaw(7), Fixed.FromRaw(-3));
            OrderApplier.Apply(new EntityWorld(), order, Faction.Player2, dslSink: sink);

            Assert.NotNull(seen);
            Assert.Equal((2, 1, 7, -3), seen!.Value);
        }

        [Fact]
        public void OrderApplier_NullSink_IsDeterministicNoOp()
        {
            var order = new UnitOrder(0, UnitCommand.DslEvent, Fixed.FromRaw(1), Fixed.FromRaw(2));
            // No sink wired (golden/replay-without-a-director path) → no throw, nothing happens.
            OrderApplier.Apply(new EntityWorld(), order, Faction.Player1);
        }

        [Fact]
        public void OrderApplier_NeutralRaiser_IsDropped_NotEscalatedToSystem()
        {
            bool called = false;
            Func<int, int, int, int, bool> sink = (_, _, _, _) => { called = true; return true; };
            OrderApplier.Apply(new EntityWorld(), new UnitOrder(0, UnitCommand.DslEvent, Fixed.Zero, Fixed.Zero),
                Faction.Neutral, dslSink: sink);
            Assert.False(called); // a Neutral (spectator) raiser is dropped, NOT mapped to −1/system
        }

        // ── Sim-side authorization gate ─────────────────────────────────────────

        [Fact]
        public void AuthorizedRaise_FiresSubscribedTrigger_SameTick()
        {
            (ScenarioDirector director, DslVarTable table, DslEventQueue queue) = Build(BuyScenario(new[] { 0 }, withArg: false));
            var world = new EntityWorld();

            Assert.True(director.TryEnqueueExternalDslEvent(0, 0, 0, 0)); // Player1 slot 0 ∈ allowed_raisers
            Assert.Equal(1, queue.Count);
            director.Tick(world, Fixed.One);                             // drains the queue → handler fires
            Assert.Equal(1, table.GetInt("score", 0));
        }

        [Fact]
        public void UnauthorizedRaiser_DroppedDeterministically_ZeroMutation()
        {
            (ScenarioDirector director, DslVarTable table, DslEventQueue queue) = Build(BuyScenario(new[] { 1 }, withArg: false)); // only slot 1 allowed
            var world = new EntityWorld();

            Assert.False(director.TryEnqueueExternalDslEvent(0, 0, 0, 0)); // Player1 slot 0 NOT allowed → dropped
            Assert.Equal(0, queue.Count);                                  // nothing enqueued
            director.Tick(world, Fixed.One);
            Assert.Equal(0, table.GetInt("score", 0));                     // no sim mutation
        }

        [Fact]
        public void OutOfRangeEventIndex_Dropped()
        {
            (ScenarioDirector director, _, DslEventQueue queue) = Build(BuyScenario(new[] { 0 }, withArg: false));
            Assert.False(director.TryEnqueueExternalDslEvent(99, 0, 0, 0));
            Assert.False(director.TryEnqueueExternalDslEvent(-1, 0, 0, 0));
            Assert.Equal(0, queue.Count);
        }

        [Fact]
        public void SystemRaiser_MinusOne_IsAlwaysLegal()
        {
            (ScenarioDirector director, DslVarTable table, _) = Build(BuyScenario(raisers: null, withArg: false)); // system-only
            var world = new EntityWorld();
            Assert.True(director.TryEnqueueExternalDslEvent(0, -1, 0, 0));
            director.Tick(world, Fixed.One);
            Assert.Equal(1, table.GetInt("score", 0));
        }

        [Fact]
        public void Args_AreForwardedToTheDispatchPayload()
        {
            (ScenarioDirector director, DslVarTable table, _) = Build(BuyScenario(new[] { 0 }, withArg: true));
            var world = new EntityWorld();
            Assert.True(director.TryEnqueueExternalDslEvent(0, 0, 5, 0)); // amount = arg0 = 5
            director.Tick(world, Fixed.One);
            Assert.Equal(5, table.GetInt("score", 0));
        }

        [Fact]
        public void QueueAtCapacity_DropsNewestDeterministically()
        {
            (ScenarioDirector director, _, DslEventQueue queue) = Build(BuyScenario(new[] { 0 }, withArg: false));
            for (int i = 0; i < EventBounds.MaxNextTickEventQueue; i++)
                Assert.True(director.TryEnqueueExternalDslEvent(0, 0, 0, 0));
            Assert.False(director.TryEnqueueExternalDslEvent(0, 0, 0, 0)); // full → drop-newest, no throw
            Assert.Equal(EventBounds.MaxNextTickEventQueue, queue.Count);
        }

        [Fact]
        public void OrderApplier_ThroughDirectorSink_UnauthorizedRaise_LeavesQueueEmpty()
        {
            (ScenarioDirector director, _, DslEventQueue queue) = Build(BuyScenario(new[] { 1 }, withArg: false)); // only slot 1
            Func<int, int, int, int, bool> sink = director.TryEnqueueExternalDslEvent;
            // A Player1 (slot 0) DslEvent order applied through the real sink is authorized-out and dropped.
            OrderApplier.Apply(new EntityWorld(), new UnitOrder(0, UnitCommand.DslEvent, Fixed.Zero, Fixed.Zero),
                Faction.Player1, dslSink: sink);
            Assert.Equal(0, queue.Count);
        }

        [Fact]
        public void ExternalDslEvent_WiderThanWireEvent_Dropped_QueueEmpty()
        {
            // PATCH 6 — the external (button) seam carries only 2 arg slots. An event declaring MORE than
            // EventBounds.MaxButtonEventParams params must NOT fire through TryEnqueueExternalDslEvent with
            // silently-zeroed params — it is a deterministic drop (no mutation, no throw). Triggers may still raise
            // wider events directly via RaiseEventNode.
            ScenarioVariable[] vars = { IntVar("score") };
            var declMap = DeclMap(vars);
            var events = new[]
            {
                new ScenarioCustomEvent
                {
                    Name = "wide",
                    Params = new[]
                    {
                        new ScenarioEventParam { Name = "a", Type = DslValueType.Int },
                        new ScenarioEventParam { Name = "b", Type = DslValueType.Int },
                        new ScenarioEventParam { Name = "c", Type = DslValueType.Int }, // 3 > MaxButtonEventParams (2)
                    },
                    AllowedRaisers = new[] { 0 },
                },
            };
            TriggerGraph handler = TriggerGraph.BuildCustomEventTrigger(
                "H", "custom_event", "wide", null, null, null, -1, false,
                "score", 0, "score + 1", declMap, events);
            var scenario = new ScenarioData { Variables = vars, CustomEvents = events, TriggerGraphJson = handler.ToCanonicalJson() };

            (ScenarioDirector director, DslVarTable table, DslEventQueue queue) = Build(scenario);
            var world = new EntityWorld();

            Assert.False(director.TryEnqueueExternalDslEvent(0, 0, 1, 2)); // wider-than-wire → dropped
            Assert.Equal(0, queue.Count);                                  // nothing enqueued
            director.Tick(world, Fixed.One);
            Assert.Equal(0, table.GetInt("score", 0));                     // no sim mutation
        }

        // ── Two-run determinism at host altitude over an identical button-command stream ──

        [Fact]
        public void TwoHeadlessRuns_OverIdenticalButtonStream_ProduceByteIdenticalChecksums()
        {
            List<(uint, uint)> Run()
            {
                var host = SimulationHost.Create(new NullLogSink(), new FactionRegistry(2));
                host.ChecksumInterval = 1;
                var seq = new List<(uint, uint)>();
                host.SetChecksumSink((t, h) => seq.Add((t, h)));
                host.ScenarioDirector.LoadScenario(BuyScenario(new[] { 0 }, withArg: true));
                for (int t = 0; t < 10; t++)
                {
                    if (t == 3 || t == 6) host.DslEventSink(0, 0, t, 0); // button pressed at ticks 3 & 6 (before StepOnce)
                    host.StepOnce();
                }
                return seq;
            }

            List<(uint, uint)> a = Run();
            List<(uint, uint)> b = Run();
            Assert.Equal(a, b);
        }

        [Fact]
        public void ButtonStream_MovesTheChecksum_VsNoPress()
        {
            List<(uint, uint)> Run(bool press)
            {
                var host = SimulationHost.Create(new NullLogSink(), new FactionRegistry(2));
                host.ChecksumInterval = 1;
                var seq = new List<(uint, uint)>();
                host.SetChecksumSink((t, h) => seq.Add((t, h)));
                host.ScenarioDirector.LoadScenario(BuyScenario(new[] { 0 }, withArg: true));
                for (int t = 0; t < 6; t++)
                {
                    if (press && t == 2) host.DslEventSink(0, 0, 42, 0);
                    host.StepOnce();
                }
                return seq;
            }
            Assert.NotEqual(Run(press: true), Run(press: false)); // teeth: the raise really enters the checksum
        }

        [Fact]
        public void LocalActionOnlyButton_DoesNotRaiseSimEvent_EventButtonDoes()
        {
            // PATCH 4 — the ButtonWidget.RaisesSimEvent seam is the SINGLE source of truth for whether a press can
            // touch the lockstep bus. A local-action-only button (EventName null) must report false; an event button
            // must report true. This is the teeth: if someone makes a local-action-only button raise its (null)
            // event, RaisesSimEvent would flip true here and this assertion fails.
            var localOnly = new ButtonWidget { EventName = null, LocalAction = LocalUiAction.ToggleWidgetVisible, LocalTargetWidgetId = 1 };
            Assert.False(localOnly.RaisesSimEvent);

            var eventButton = new ButtonWidget { EventName = "buy" };
            Assert.True(eventButton.RaisesSimEvent);
        }

        [Fact]
        public void NoDslEventEnqueue_LeavesChecksumUnchanged_VsBaseline()
        {
            // PATCH 4 — the determinism half: because a local-action-only press provably never calls the DslEvent sink
            // (RaisesSimEvent == false, asserted above), stepping the sim while "pressing" only local-action buttons is
            // byte-identical to stepping with no presses at all. The two runs differ ONLY by whether a would-be
            // local-action button's press path is taken; since that path never enqueues, the checksum sequence matches.
            List<(uint, uint)> Run(bool pressLocalActionOnlyButtons)
            {
                var host = SimulationHost.Create(new NullLogSink(), new FactionRegistry(2));
                host.ChecksumInterval = 1;
                var seq = new List<(uint, uint)>();
                host.SetChecksumSink((t, h) => seq.Add((t, h)));
                host.ScenarioDirector.LoadScenario(BuyScenario(new[] { 0 }, withArg: true));

                var localOnly = new ButtonWidget { EventName = null, LocalAction = LocalUiAction.ToggleWidgetVisible, LocalTargetWidgetId = 1 };
                for (int t = 0; t < 6; t++)
                {
                    // Route the "press" through the SAME predicate the bridge uses. A local-action-only button never
                    // reaches the sink — so firing it (or not) cannot move the checksum.
                    if (pressLocalActionOnlyButtons && t == 2 && localOnly.RaisesSimEvent)
                        host.DslEventSink(0, 0, 42, 0); // unreachable: proves the teeth (would desync if it ran)
                    host.StepOnce();
                }
                return seq;
            }
            Assert.Equal(Run(pressLocalActionOnlyButtons: true), Run(pressLocalActionOnlyButtons: false));
        }
    }
}
