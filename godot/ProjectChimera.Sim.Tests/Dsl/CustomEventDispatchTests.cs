#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// Story 7.5 — director-driven custom-event dispatch: define + raise + subscribe with same-tick delivery and
    /// evaluated payloads; per-occurrence semantics (mass deaths ×N for param-reading handlers vs once-per-tick
    /// legacy parity); run-once re-raise; cooldown same-tick suppression; next-tick A→B→A feedback alternation
    /// through the checksummed <see cref="DslEventQueue"/>; queue-overflow determinism; drain ordering
    /// (dequeued-before-base-raises); kill-credit payloads on <c>unit_dies</c>; two-run byte-identical checksum
    /// sequences at HOST altitude with live cascades; and the zero-alloc warmed-up cascade tick.
    /// </summary>
    public class CustomEventDispatchTests
    {
        // ── Fixture helpers ─────────────────────────────────────────────────────

        private static ScenarioVariable IntVar(string name, int initial = 0) =>
            new() { Name = name, Type = DslValueType.Int, Scope = VarScope.Global, Initial = Fixed.FromInt(initial) };

        private static ScenarioCustomEvent Ev(string name, params (string Name, DslValueType Type)[] ps) =>
            new()
            {
                Name = name,
                Params = ps.Length == 0 ? null : Array.ConvertAll(ps, p => new ScenarioEventParam { Name = p.Name, Type = p.Type }),
            };

        private static Dictionary<string, (DslValueType Type, VarScope Scope)> DeclMap(ScenarioVariable[] vars)
        {
            var map = new Dictionary<string, (DslValueType, VarScope)>(StringComparer.Ordinal);
            foreach (var v in vars) map[v.Name] = (v.Type, v.Scope);
            return map;
        }

        private static (ScenarioDirector Director, DslVarTable Vars, DslEventQueue Queue) Build(ScenarioData scenario)
        {
            var vars  = new DslVarTable();
            var queue = new DslEventQueue();
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), vars, eventQueue: queue);
            director.LoadScenario(scenario);
            return (director, vars, queue);
        }

        // ── Define + raise + subscribe (the I/O matrix happy path) ──────────────

        [Fact]
        public void RaiseAndSubscribe_DispatchSameTick_WithEvaluatedPayload()
        {
            ScenarioVariable[] vars = { IntVar("gold", 4), IntVar("score") };
            var events = new[] { Ev("wave_start", ("count", DslValueType.Int)) };
            var declMap = DeclMap(vars);

            // Trigger A (built-in event) raises wave_start(gold + 1); handler H gates on event.count > 2 and
            // consumes event.count * 10 into score.
            TriggerGraph raiser = TriggerGraph.BuildCustomEventTrigger(
                "A", "match_start", null, null,
                "wave_start", new[] { "gold + 1" }, raiser: -1, raiseNextTick: false,
                null, 0, null, declMap, events);
            TriggerGraph handler = TriggerGraph.BuildCustomEventTrigger(
                "H", "custom_event", "wave_start", "event.count > 2",
                null, null, raiser: -1, raiseNextTick: false,
                "score", 0, "event.count * 10", declMap, events);
            raiser.Merge(handler);

            (ScenarioDirector director, DslVarTable table, _) = Build(new ScenarioData
            {
                Variables = vars, CustomEvents = events, TriggerGraphJson = raiser.ToCanonicalJson(),
            });

            director.Tick(new EntityWorld(), Fixed.One);
            Assert.Equal(50, table.GetInt("score", 0)); // fired WITHIN the same tick, payload = gold + 1 = 5
        }

        [Fact]
        public void HandlerConditionGatesOnThePayload()
        {
            ScenarioVariable[] vars = { IntVar("gold", 1), IntVar("score") }; // count = 2 → `> 2` is false
            var events = new[] { Ev("wave_start", ("count", DslValueType.Int)) };
            var declMap = DeclMap(vars);
            TriggerGraph raiser = TriggerGraph.BuildCustomEventTrigger(
                "A", "match_start", null, null,
                "wave_start", new[] { "gold + 1" }, -1, false, null, 0, null, declMap, events);
            TriggerGraph handler = TriggerGraph.BuildCustomEventTrigger(
                "H", "custom_event", "wave_start", "event.count > 2",
                null, null, -1, false, "score", 0, "event.count * 10", declMap, events);
            raiser.Merge(handler);

            (ScenarioDirector director, DslVarTable table, _) = Build(new ScenarioData
            {
                Variables = vars, CustomEvents = events, TriggerGraphJson = raiser.ToCanonicalJson(),
            });
            director.Tick(new EntityWorld(), Fixed.One);
            Assert.Equal(0, table.GetInt("score", 0)); // gated out — payload did not pass the condition
        }

        // ── Per-occurrence vs legacy once-per-tick (mass deaths) ────────────────

        [Fact]
        public void MassDeaths_DispatchParamReaderPerOccurrence_AndLegacyOnce()
        {
            ScenarioVariable[] vars = { IntVar("hits"), IntVar("legacy") };
            var declMap = DeclMap(vars);

            // Param-reading unit_dies handler (dispatches once per death) vs a legacy-semantics trigger whose
            // programs read NO event params (fires once per tick, the Block-If parity).
            TriggerGraph reader = TriggerGraph.BuildCustomEventTrigger(
                "reader", "unit_dies", null, "event.victim >= 0",
                null, null, -1, false, "hits", 0, "hits + 1", declMap, null);
            TriggerGraph legacy = TriggerGraph.BuildCustomEventTrigger(
                "legacy", "unit_dies", null, null,
                null, null, -1, false, "legacy", 0, "legacy + 1", declMap, null);
            reader.Merge(legacy);

            (ScenarioDirector director, DslVarTable table, _) = Build(new ScenarioData
            {
                Variables = vars, TriggerGraphJson = reader.ToCanonicalJson(),
            });

            var world = new EntityWorld();
            int a = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(10), Fixed.One);
            int b = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(10), Fixed.One);
            int c = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(10), Fixed.One);

            director.Tick(world, Fixed.One); // snapshot alive
            world.Destroy(a); world.Destroy(b); world.Destroy(c);
            director.Tick(world, Fixed.One); // 3 unit_dies occurrences this tick

            Assert.Equal(3, table.GetInt("hits", 0));   // per-occurrence (ascending entity id emission)
            Assert.Equal(1, table.GetInt("legacy", 0)); // once per tick — legacy parity
        }

        // ── Run-once re-raise + cooldown same-tick suppression ──────────────────

        [Fact]
        public void RunOnceHandler_FiresExactlyOnce_EvenWhenReRaisedSameTickAndNextTick()
        {
            ScenarioVariable[] vars = { IntVar("n") };
            var events = new[] { Ev("ping") };
            var declMap = DeclMap(vars);

            // Two raisers fire EVERY tick (resource_threshold ≥ 0), so ping is raised twice per tick, every tick.
            TriggerGraph g = TriggerGraph.BuildCustomEventTrigger(
                "r1", "resource_threshold", null, null, "ping", null, -1, false, null, 0, null, declMap, events);
            g.Merge(TriggerGraph.BuildCustomEventTrigger(
                "r2", "resource_threshold", null, null, "ping", null, -1, false, null, 0, null, declMap, events));
            TriggerGraph handler = TriggerGraph.BuildCustomEventTrigger(
                "once", "custom_event", "ping", null, null, null, -1, false, "n", 0, "n + 1", declMap, events,
                runOnce: true);
            g.Merge(handler);

            (ScenarioDirector director, DslVarTable table, _) = Build(new ScenarioData
            {
                Variables = vars, CustomEvents = events, TriggerGraphJson = g.ToCanonicalJson(),
            });
            var world = new EntityWorld();
            director.Tick(world, Fixed.One);
            director.Tick(world, Fixed.One);
            director.Tick(world, Fixed.One);
            Assert.Equal(1, table.GetInt("n", 0)); // at most once per match, however often re-raised
        }

        [Fact]
        public void CooldownHandler_SuppressesSameTickReEntry_AndReArmsAfterExpiry()
        {
            ScenarioVariable[] vars = { IntVar("n") };
            var events = new[] { Ev("ping") };
            var declMap = DeclMap(vars);

            TriggerGraph g = TriggerGraph.BuildCustomEventTrigger(
                "r1", "resource_threshold", null, null, "ping", null, -1, false, null, 0, null, declMap, events);
            g.Merge(TriggerGraph.BuildCustomEventTrigger(
                "r2", "resource_threshold", null, null, "ping", null, -1, false, null, 0, null, declMap, events));
            TriggerGraph handler = TriggerGraph.BuildCustomEventTrigger(
                "cool", "custom_event", "ping", null, null, null, -1, false, "n", 0, "n + 1", declMap, events);
            ((TriggerNode)handler.Nodes[0]).CooldownSeconds = Fixed.One; // 1s = 30 ticks
            g.Merge(handler);

            (ScenarioDirector director, DslVarTable table, _) = Build(new ScenarioData
            {
                Variables = vars, CustomEvents = events, TriggerGraphJson = g.ToCanonicalJson(),
            });
            var world = new EntityWorld();

            director.Tick(world, Fixed.One); // two ping occurrences: first fires, second is cooldown-suppressed
            Assert.Equal(1, table.GetInt("n", 0));
            director.Tick(world, Fixed.One); // still cooling
            Assert.Equal(1, table.GetInt("n", 0));
            for (int i = 0; i < 40; i++) director.Tick(world, Fixed.One); // well past the 30-tick cooldown
            Assert.Equal(2, table.GetInt("n", 0)); // fired exactly once more after expiry (then re-armed)
        }

        // ── Next-tick feedback (A→B→A through the checksummed queue) ────────────

        [Fact]
        public void NextTickFeedback_AlternatesAcrossTicks_ThroughTheQueue()
        {
            ScenarioVariable[] vars = { IntVar("a"), IntVar("b") };
            var events = new[] { Ev("e1"), Ev("e2") };
            var declMap = DeclMap(vars);

            TriggerGraph g = TriggerGraph.BuildCustomEventTrigger(
                "seed", "match_start", null, null, "e1", null, -1, false, null, 0, null, declMap, events);
            g.Merge(TriggerGraph.BuildCustomEventTrigger(
                "A", "custom_event", "e1", null, "e2", null, -1, raiseNextTick: true, "a", 0, "a + 1", declMap, events));
            g.Merge(TriggerGraph.BuildCustomEventTrigger(
                "B", "custom_event", "e2", null, "e1", null, -1, raiseNextTick: true, "b", 0, "b + 1", declMap, events));

            (ScenarioDirector director, DslVarTable table, DslEventQueue queue) = Build(new ScenarioData
            {
                Variables = vars, CustomEvents = events, TriggerGraphJson = g.ToCanonicalJson(),
            });
            var world = new EntityWorld();

            director.Tick(world, Fixed.One); // seed raises e1 same-tick → A fires, enqueues e2
            Assert.Equal(1, table.GetInt("a", 0));
            Assert.Equal(0, table.GetInt("b", 0));
            Assert.Equal(1, queue.Count); // e2 pending at the checksum boundary (the folded state)
            Assert.Equal(1, queue.EventIndexAt(0));

            director.Tick(world, Fixed.One); // dequeue e2 → B fires, enqueues e1
            Assert.Equal(1, table.GetInt("a", 0));
            Assert.Equal(1, table.GetInt("b", 0));
            Assert.Equal(1, queue.Count);
            Assert.Equal(0, queue.EventIndexAt(0));

            director.Tick(world, Fixed.One); // dequeue e1 → A fires again — feedback alternates
            Assert.Equal(2, table.GetInt("a", 0));
            Assert.Equal(1, table.GetInt("b", 0));
        }

        // ── Queue overflow determinism ───────────────────────────────────────────

        [Fact]
        public void NextTickQueueOverflow_DropsNewestDeterministically()
        {
            var events = new[] { Ev("flood") };
            var declMap = new Dictionary<string, (DslValueType, VarScope)>(StringComparer.Ordinal);

            // One match_start trigger whose chain raises `flood` next-tick MORE times than the queue holds.
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "flooder" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            int prev = 0, prevPort = TriggerGraph.TriggerExecOutPort;
            int raises = EventBounds.MaxNextTickEventQueue + 8;
            for (int i = 0; i < raises; i++)
            {
                int id = 2 + i;
                g.Nodes.Add(new RaiseEventNode { Id = id, Name = "flood", NextTick = true });
                g.ExecEdges.Add(new ExecEdge(prev, prevPort, id, TriggerGraph.ActionExecInPort));
                prev = id; prevPort = TriggerGraph.ActionExecOutPort;
            }

            var scenario = new ScenarioData { CustomEvents = events, TriggerGraphJson = g.ToCanonicalJson() };

            (ScenarioDirector d1, _, DslEventQueue q1) = Build(scenario);
            d1.Tick(new EntityWorld(), Fixed.One);
            Assert.Equal(EventBounds.MaxNextTickEventQueue, q1.Count); // drop-newest at capacity, no crash

            // Determinism: the folded queue state is identical across two fresh runs.
            (ScenarioDirector d2, _, DslEventQueue q2) = Build(scenario);
            d2.Tick(new EntityWorld(), Fixed.One);
            uint h1 = 0x811C9DC5, h2 = 0x811C9DC5;
            static uint Mix(uint h, int v) { unchecked { h ^= (uint)v; h *= 16777619u; } return h; }
            q1.FoldInto(ref h1, Mix);
            q2.FoldInto(ref h2, Mix);
            Assert.Equal(h1, h2);
        }

        // ── Drain ordering: dequeued events dispatch BEFORE base-sweep raises ────

        [Fact]
        public void DequeuedEvents_DispatchBeforeBaseSweepRaises()
        {
            ScenarioVariable[] vars = { IntVar("seq") };
            var events = new[] { Ev("e_next"), Ev("e_base") };
            var declMap = DeclMap(vars);

            TriggerGraph g = TriggerGraph.BuildCustomEventTrigger(
                "enq", "match_start", null, null, "e_next", null, -1, raiseNextTick: true, null, 0, null, declMap, events);
            g.Merge(TriggerGraph.BuildCustomEventTrigger(
                "base", "resource_threshold", null, null, "e_base", null, -1, false, null, 0, null, declMap, events));
            g.Merge(TriggerGraph.BuildCustomEventTrigger(
                "hNext", "custom_event", "e_next", null, null, null, -1, false, "seq", 0, "seq * 10 + 1", declMap, events));
            g.Merge(TriggerGraph.BuildCustomEventTrigger(
                "hBase", "custom_event", "e_base", null, null, null, -1, false, "seq", 0, "seq * 10 + 2", declMap, events));

            (ScenarioDirector director, DslVarTable table, _) = Build(new ScenarioData
            {
                Variables = vars, CustomEvents = events, TriggerGraphJson = g.ToCanonicalJson(),
            });
            var world = new EntityWorld();

            director.Tick(world, Fixed.One); // tick 1: e_next enqueued; e_base raised+drained → seq = 2
            Assert.Equal(2, table.GetInt("seq", 0));
            director.Tick(world, Fixed.One); // tick 2: dequeued e_next dispatches BEFORE tick-2's e_base raise
            Assert.Equal(212, table.GetInt("seq", 0)); // 2 → 21 (e_next first) → 212 (then e_base)
        }

        // ── Kill credit through the unit_dies payload ────────────────────────────

        [Fact]
        public void KillCredit_GatesOnKillerFaction_AndNonCombatDestroyYieldsMinusOne()
        {
            ScenarioVariable[] vars = { IntVar("credit"), IntVar("uncredited") };
            var declMap = DeclMap(vars);

            TriggerGraph g = TriggerGraph.BuildCustomEventTrigger(
                "credit", "unit_dies", null, "event.killer_faction == 1",
                null, null, -1, false, "credit", 0, "credit + 1", declMap, null);
            g.Merge(TriggerGraph.BuildCustomEventTrigger(
                "uncredited", "unit_dies", null, "event.killer_faction == 0 - 1 && event.killer == 0 - 1",
                null, null, -1, false, "uncredited", 0, "uncredited + 1", declMap, null));

            (ScenarioDirector director, DslVarTable table, _) = Build(new ScenarioData
            {
                Variables = vars, TriggerGraphJson = g.ToCanonicalJson(),
            });

            var world = new EntityWorld();
            int victim1  = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(1), Fixed.One);
            int attacker = world.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(10), Fixed.One);
            int victim2  = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(1), Fixed.One);

            director.Tick(world, Fixed.One); // snapshot alive

            // A Player2 (slot 1) combat kill via the single death choke point, and a NON-COMBAT destroy.
            DamageResolver.KillEntity(world, victim1, Faction.Player2, null, null, null, attackerId: attacker);
            world.Destroy(victim2);
            director.Tick(world, Fixed.One);

            Assert.Equal(1, table.GetInt("credit", 0));     // event.killer_faction == 1 matched exactly once
            Assert.Equal(1, table.GetInt("uncredited", 0)); // the non-combat death carried −1 / −1
        }

        // ── Two-run byte-identical checksum sequences (host altitude, live cascades) ──

        private static ScenarioData LiveCascadeScenario()
        {
            ScenarioVariable[] vars = { IntVar("gold", 3), IntVar("s1"), IntVar("s2") };
            var events = new[] { Ev("c1", ("count", DslValueType.Int)), Ev("c2", ("count", DslValueType.Int)) };
            var declMap = DeclMap(vars);

            // Every tick: raise c1(gold + 1); h1 consumes + raises c2(event.count * 2) same-tick; h2 consumes +
            // feeds back c1 NEXT tick — a live same-tick cascade plus pending cross-tick state every checksum.
            TriggerGraph g = TriggerGraph.BuildCustomEventTrigger(
                "raiser", "resource_threshold", null, null, "c1", new[] { "gold + 1" }, -1, false, null, 0, null, declMap, events);
            g.Merge(TriggerGraph.BuildCustomEventTrigger(
                "h1", "custom_event", "c1", "event.count > 0", "c2", new[] { "event.count * 2" }, -1, false,
                "s1", 0, "s1 + event.count", declMap, events));
            g.Merge(TriggerGraph.BuildCustomEventTrigger(
                "h2", "custom_event", "c2", null, "c1", new[] { "event.count" }, -1, raiseNextTick: true,
                "s2", 0, "s2 + event.count", declMap, events));

            return new ScenarioData
            {
                Variables = vars, CustomEvents = events, TriggerGraphJson = g.ToCanonicalJson(),
            };
        }

        [Fact]
        public void TwoHeadlessHostRuns_WithLiveCascades_ProduceByteIdenticalChecksumSequences()
        {
            static List<(uint Tick, uint Hash)> Run()
            {
                var host = SimulationHost.Create(new NullLogSink(), new FactionRegistry(2));
                host.ChecksumInterval = 1;
                var seq = new List<(uint, uint)>();
                host.SetChecksumSink((tick, hash) => seq.Add((tick, hash)));
                host.ScenarioDirector.LoadScenario(LiveCascadeScenario());
                for (int i = 0; i < 30; i++) host.StepOnce();
                return seq;
            }

            List<(uint Tick, uint Hash)> run1 = Run();
            List<(uint Tick, uint Hash)> run2 = Run();
            Assert.Equal(30, run1.Count);
            Assert.Equal(run1, run2); // byte-identical SimChecksum sequences (AC3)

            // The cascade is genuinely LIVE: state mutates across ticks, so consecutive hashes differ.
            Assert.NotEqual(run1[0].Hash, run1[5].Hash);
        }

        [Fact]
        public void PendingQueue_MovesTheHostChecksum()
        {
            // A tick with a pending next-tick event folds differently from one without — the v17 queue fold.
            var host = SimulationHost.Create(new NullLogSink(), new FactionRegistry(2));
            host.ScenarioDirector.LoadScenario(LiveCascadeScenario());
            host.StepOnce(); // the cascade leaves c1 pending in the queue every tick
            Assert.True(host.DslEvents.Count > 0, "fixture must leave a pending next-tick event");

            uint withPending = SimChecksum.Compute(host.World, host.Buildings, host.Resources,
                new FactionRegistry(2), host.Modifiers, host.Heroes, host.Items, host.Nodes, host.Research,
                host.Vars, host.LoopState, host.DslEvents);
            uint withoutPending = SimChecksum.Compute(host.World, host.Buildings, host.Resources,
                new FactionRegistry(2), host.Modifiers, host.Heroes, host.Items, host.Nodes, host.Research,
                host.Vars, host.LoopState, new DslEventQueue());
            Assert.NotEqual(withPending, withoutPending);
        }

        // ── Zero-alloc: a warmed-up tick with a live cascade allocates nothing ───

        [Fact]
        public void WarmedUpCascadeTick_IsZeroAlloc()
        {
            (ScenarioDirector director, _, _) = Build(LiveCascadeScenario());
            var world = new EntityWorld();

            director.Tick(world, Fixed.One); // warm up (match_start path, JIT, first-call statics)
            director.Tick(world, Fixed.One);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 16; i++)
                director.Tick(world, Fixed.One); // raise + same-tick dispatch + payload reads + next-tick feedback
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(0, after - before);
        }

        // ── The LoadScenario backstop throws located (gate parity spot checks) ───

        [Fact]
        public void LoadScenarioBackstop_RejectsUndeclaredRaise_Located()
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.Nodes.Add(new RaiseEventNode { Id = 2, Name = "ghost" });
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));

            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), new DslVarTable());
            var ex = Assert.Throws<System.Text.Json.JsonException>(() =>
                director.LoadScenario(new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() }));
            Assert.Contains("ghost", ex.Message);
        }

        [Fact]
        public void LoadScenarioBackstop_RejectsSameTickCycle_NamingThePath()
        {
            var events = new[] { Ev("e1"), Ev("e2") };
            var declMap = new Dictionary<string, (DslValueType, VarScope)>(StringComparer.Ordinal);
            TriggerGraph g = TriggerGraph.BuildCustomEventTrigger(
                "h1", "custom_event", "e1", null, "e2", null, -1, false, null, 0, null, declMap, events);
            g.Merge(TriggerGraph.BuildCustomEventTrigger(
                "h2", "custom_event", "e2", null, "e1", null, -1, false, null, 0, null, declMap, events));

            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), new DslVarTable());
            var ex = Assert.Throws<System.Text.Json.JsonException>(() =>
                director.LoadScenario(new ScenarioData { CustomEvents = events, TriggerGraphJson = g.ToCanonicalJson() }));
            Assert.Contains("e1→e2→e1", ex.Message);
        }

        [Fact]
        public void FailedLoad_IsFailureAtomic_PreviousScenarioKeepsTicking()
        {
            // Load a good scenario, then fail a re-load: the director must keep the previous coherent state.
            ScenarioVariable[] vars = { IntVar("n") };
            var events = new[] { Ev("ping") };
            var declMap = DeclMap(vars);
            TriggerGraph good = TriggerGraph.BuildCustomEventTrigger(
                "r", "resource_threshold", null, null, "ping", null, -1, false, null, 0, null, declMap, events);
            good.Merge(TriggerGraph.BuildCustomEventTrigger(
                "h", "custom_event", "ping", null, null, null, -1, false, "n", 0, "n + 1", declMap, events));
            (ScenarioDirector director, DslVarTable table, _) = Build(new ScenarioData
            {
                Variables = vars, CustomEvents = events, TriggerGraphJson = good.ToCanonicalJson(),
            });
            var world = new EntityWorld();
            director.Tick(world, Fixed.One);
            Assert.Equal(1, table.GetInt("n", 0));

            var bad = new TriggerGraph();
            bad.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            bad.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            bad.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            bad.Nodes.Add(new RaiseEventNode { Id = 2, Name = "undeclared" });
            bad.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            Assert.Throws<System.Text.Json.JsonException>(() =>
                director.LoadScenario(new ScenarioData { TriggerGraphJson = bad.ToCanonicalJson() }));

            director.Tick(world, Fixed.One); // the PREVIOUS scenario still runs coherently
            Assert.Equal(2, table.GetInt("n", 0));
        }
    }
}
