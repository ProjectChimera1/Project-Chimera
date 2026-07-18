#nullable enable
using System;
using ProjectChimera.Combat;            // DamageResolver / DamageContext / DamageTable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using ProjectChimera.Effects;         // DirectHpDeltaEffect / EffectActionNode (batched-body test)
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// Story 7.13 (bump arms) — headless coverage for the checksum-bumping vocabulary: <c>random_choice</c>
    /// (SimRng-drawn, deterministic), <c>enable_trigger</c>/<c>disable_trigger</c> (folded runtime enabled mask +
    /// firing latch), <c>run_trigger</c> (synchronous run + load-time cycle reject), and the new sim-driven built-in
    /// event sources (each drains the sim-event feed → a subscribed trigger fires; unit_damaged is exercised
    /// end-to-end through the real DamageResolver raise site).
    /// </summary>
    public class RandomChoiceEnableRunEventTests
    {
        private static (ScenarioDirector Director, DslVarTable Vars) Build(
            ScenarioData scenario, TriggerEnabledStore? enabled = null, DslSimEventFeed? feed = null)
        {
            var vars = new DslVarTable();
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), vars,
                triggerEnabled: enabled, simEventFeed: feed);
            director.LoadScenario(scenario);
            return (director, vars);
        }

        private static ScenarioVariable IntVar(string name) => new() { Name = name, Type = DslValueType.Int, Scope = VarScope.Global };

        // ── random_choice ──────────────────────────────────────────────────────

        /// <summary>match_start → random_choice(weights) with one set_variable(branchK = 1) per branch.</summary>
        private static TriggerGraph RandomChoiceGraph(int[] weights)
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new RandomChoiceNode { Id = 2, Weights = weights });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            int nextId = 3;
            for (int k = 0; k < weights.Length; k++)
            {
                g.Nodes.Add(new ActionNode { Id = nextId, Kind = "set_variable", Variable = "branch" + k, Value = 1 });
                g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.RandomChoiceBranchOutPort0 + k, nextId, TriggerGraph.ActionExecInPort));
                nextId++;
            }
            return g;
        }

        private static int RunRandomChoiceOnce(int[] weights)
        {
            var vars = new[] { IntVar("branch0"), IntVar("branch1"), IntVar("branch2") };
            var scenario = new ScenarioData { Variables = vars, TriggerGraphJson = RandomChoiceGraph(weights).ToCanonicalJson() };
            (ScenarioDirector director, DslVarTable vt) = Build(scenario);
            director.Tick(new EntityWorld(), Fixed.One);
            for (int k = 0; k < weights.Length; k++)
                if (vt.GetInt("branch" + k, 0) == 1) return k;
            return -1;
        }

        [Fact]
        public void RandomChoice_SelectsExactlyOneBranch_DeterministicAcrossRuns()
        {
            int a = RunRandomChoiceOnce(new[] { 1, 2, 1 });
            int b = RunRandomChoiceOnce(new[] { 1, 2, 1 });
            Assert.InRange(a, 0, 2);         // exactly one branch selected
            Assert.Equal(a, b);              // two seeded runs pick the identical branch (SimRng-last draw)
        }

        [Fact]
        public void RandomChoice_DrawsFromSharedSimRng_MatchingNextInt()
        {
            // The selection must equal a hand-rolled NextInt(totalWeight) subtract-down on the SAME seed stream.
            int[] weights = { 3, 5, 2 };
            var world = new EntityWorld();
            int draw = world.Rng.NextInt(10); // consume exactly as the director will (fresh world, same seed)
            int expected = -1, acc = draw;
            for (int k = 0; k < weights.Length; k++) { if (acc < weights[k]) { expected = k; break; } acc -= weights[k]; }

            int actual = RunRandomChoiceOnce(weights);
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void RandomChoice_ZeroTotalWeight_RejectsAtLoad()
        {
            var scenario = new ScenarioData
            {
                Variables = new[] { IntVar("branch0"), IntVar("branch1") },
                TriggerGraphJson = RandomChoiceGraph(new[] { 0, 0 }).ToCanonicalJson(),
            };
            var ex = Assert.ThrowsAny<Exception>(() => Build(scenario));
            Assert.Contains("random_choice", ex.Message);
        }

        [Fact]
        public void RandomChoice_EmptyBranches_RejectsAtLoad()
        {
            var scenario = new ScenarioData { TriggerGraphJson = RandomChoiceGraph(Array.Empty<int>()).ToCanonicalJson() };
            var ex = Assert.ThrowsAny<Exception>(() => Build(scenario));
            Assert.Contains("random_choice", ex.Message);
        }

        [Fact]
        public void RandomChoice_WeightSumOverflowsInt_RejectsAtLoad()
        {
            // Story 7.13 (review PATCH 4) — a weight set whose LONG sum exceeds int.MaxValue must reject at the load
            // gate: the runtime weight-sum (ExecuteRandomChoice) is a 32-bit int that would otherwise WRAP to a
            // wrong/negative total and violate the authored weights. {2^31-1, 2^31-1, 4} sums well past int.MaxValue.
            var scenario = new ScenarioData
            {
                Variables = new[] { IntVar("branch0"), IntVar("branch1"), IntVar("branch2") },
                TriggerGraphJson = RandomChoiceGraph(new[] { int.MaxValue, int.MaxValue, 4 }).ToCanonicalJson(),
            };
            var ex = Assert.ThrowsAny<Exception>(() => Build(scenario));
            Assert.Contains("random_choice", ex.Message);   // located at the offending node
            Assert.Contains("overflow",      ex.Message);   // the overflow reason, not the zero-total one
        }

        // ── enable_trigger / disable_trigger ─────────────────────────────────────

        [Fact]
        public void DisableTrigger_SuppressesTargetSameTick_AndFoldsIntoEnabledMask()
        {
            // Trigger A (priority 10, evaluated first) disables trigger B (priority 0). B never fires this tick.
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "A", Priority = 10 });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new DisableTriggerNode { Id = 2, TargetTriggerId = 3 });
            g.Nodes.Add(new TriggerNode { Id = 3, Name = "B", Priority = 0 });
            g.Nodes.Add(new EventNode { Id = 4, Kind = "match_start" });
            g.Nodes.Add(new ActionNode { Id = 5, Kind = "set_variable", Variable = "bFired", Value = 1 });
            g.ExecEdges.Add(new ExecEdge(1, 0, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(4, 0, 3, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(3, TriggerGraph.TriggerExecOutPort, 5, TriggerGraph.ActionExecInPort));

            var enabled = new TriggerEnabledStore();
            var scenario = new ScenarioData { Variables = new[] { IntVar("bFired") }, TriggerGraphJson = g.ToCanonicalJson() };
            (ScenarioDirector director, DslVarTable vt) = Build(scenario, enabled);
            director.Tick(new EntityWorld(), Fixed.One);

            Assert.Equal(0, vt.GetInt("bFired", 0)); // B suppressed by A's disable_trigger
            // B's exec index: execs are ordered priority-desc → A=idx0, B=idx1. The folded mask marks B disabled.
            Assert.False(enabled.IsEnabled(1));
            Assert.True(enabled.IsEnabled(0));
        }

        [Fact]
        public void EnableDisableTrigger_UnresolvedTarget_RejectsAtLoad()
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "A" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new EnableTriggerNode { Id = 2, TargetTriggerId = 99 }); // no such trigger
            g.ExecEdges.Add(new ExecEdge(1, 0, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            var scenario = new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() };
            var ex = Assert.ThrowsAny<Exception>(() => Build(scenario));
            Assert.Contains("enable_trigger", ex.Message);
        }

        [Fact]
        public void EnableTrigger_TurnsOnAuthoredDisabledTarget_SameTick()
        {
            // Story 7.13 (review PATCH 1) — the POSITIVE mirror of the disable test. Trigger A (priority 10, swept
            // first) enable_trigger → B, which is authored Enabled=false (so it never fires on its own). Pre-fix the
            // firing gate short-circuited on the redundant `!t.Enabled`, making enable_trigger a DEAD no-op for
            // exactly the dormant-until-activated triggers it exists to turn on. Post-fix the gate reads the runtime
            // mask alone, so A's enable takes effect and B fires the SAME tick (A precedes B in the sweep).
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "A", Priority = 10 });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new EnableTriggerNode { Id = 2, TargetTriggerId = 3 });
            g.Nodes.Add(new TriggerNode { Id = 3, Name = "B", Priority = 0, Enabled = false });
            g.Nodes.Add(new EventNode { Id = 4, Kind = "match_start" });
            g.Nodes.Add(new ActionNode { Id = 5, Kind = "set_variable", Variable = "bFired", Value = 1 });
            g.ExecEdges.Add(new ExecEdge(1, 0, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(4, 0, 3, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(3, TriggerGraph.TriggerExecOutPort, 5, TriggerGraph.ActionExecInPort));

            var enabled = new TriggerEnabledStore();
            var scenario = new ScenarioData { Variables = new[] { IntVar("bFired") }, TriggerGraphJson = g.ToCanonicalJson() };
            (ScenarioDirector director, DslVarTable vt) = Build(scenario, enabled);

            // execs order priority-desc → A=idx0, B=idx1. The mask is SEEDED from the authored flags at load.
            Assert.True(enabled.IsEnabled(0));   // A authored ON
            Assert.False(enabled.IsEnabled(1));  // B authored OFF

            director.Tick(new EntityWorld(), Fixed.One);

            Assert.True(enabled.IsEnabled(1));       // A's enable_trigger flipped B's runtime mask ON
            Assert.Equal(1, vt.GetInt("bFired", 0)); // …and B fired — enable_trigger turned on an authored-disabled trigger
        }

        // ── run_trigger ──────────────────────────────────────────────────────────

        [Fact]
        public void RunTrigger_RunsDisabledTargetChain_Synchronously()
        {
            // A (match_start) run_trigger→B. B is authored DISABLED, so it never fires on its own — but run_trigger
            // forces its action chain to execute synchronously in place.
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "A" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new RunTriggerNode { Id = 2, TargetTriggerId = 3 });
            g.Nodes.Add(new TriggerNode { Id = 3, Name = "B", Enabled = false });
            g.Nodes.Add(new EventNode { Id = 4, Kind = "match_start" });
            g.Nodes.Add(new ActionNode { Id = 5, Kind = "set_variable", Variable = "ran", Value = 7 });
            g.ExecEdges.Add(new ExecEdge(1, 0, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(4, 0, 3, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(3, TriggerGraph.TriggerExecOutPort, 5, TriggerGraph.ActionExecInPort));

            var scenario = new ScenarioData { Variables = new[] { IntVar("ran") }, TriggerGraphJson = g.ToCanonicalJson() };
            (ScenarioDirector director, DslVarTable vt) = Build(scenario);
            director.Tick(new EntityWorld(), Fixed.One);
            Assert.Equal(7, vt.GetInt("ran", 0)); // B's chain ran via run_trigger despite B being disabled
        }

        [Fact]
        public void RunTrigger_SelfCycle_RejectsAtLoad()
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "A" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new RunTriggerNode { Id = 2, TargetTriggerId = 0 }); // A runs itself
            g.ExecEdges.Add(new ExecEdge(1, 0, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            var scenario = new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() };
            var ex = Assert.ThrowsAny<Exception>(() => Build(scenario));
            Assert.Contains("run_trigger cycle", ex.Message);
        }

        [Fact]
        public void RunTrigger_MutualCycle_RejectsAtLoad()
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "A" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new RunTriggerNode { Id = 2, TargetTriggerId = 3 }); // A → B
            g.Nodes.Add(new TriggerNode { Id = 3, Name = "B" });
            g.Nodes.Add(new EventNode { Id = 4, Kind = "match_start" });
            g.Nodes.Add(new RunTriggerNode { Id = 5, TargetTriggerId = 0 }); // B → A
            g.ExecEdges.Add(new ExecEdge(1, 0, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(4, 0, 3, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(3, TriggerGraph.TriggerExecOutPort, 5, TriggerGraph.ActionExecInPort));
            var scenario = new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() };
            var ex = Assert.ThrowsAny<Exception>(() => Build(scenario));
            Assert.Contains("run_trigger cycle", ex.Message);
        }

        /// <summary>
        /// AC #4 / matrix row 6 (error-handling): a DEEP ACYCLIC run_trigger chain LONGER than
        /// <see cref="EventBounds.MaxRunTriggerDepth"/> halts DETERMINISTICALLY at the cap, at a whole-trigger
        /// boundary — every body up to and including depth == cap runs; the first run_trigger that would exceed the
        /// cap is a no-op (never mid-Sequence); and the whole tick terminates without throwing or recursing
        /// unboundedly. (Self/mutual cycles reject at load, so only an acyclic chain can reach the runtime seatbelt.)
        /// </summary>
        [Fact]
        public void RunTrigger_DeepAcyclicChain_HaltsDeterministicallyAtDepthCap()
        {
            int cap = EventBounds.MaxRunTriggerDepth;
            int n = cap + 2; // T0..T(cap+1): enough triggers that the CAP — not the chain end — stops the recursion.

            var g = new TriggerGraph();
            var vars = new System.Collections.Generic.List<ScenarioVariable>(n);
            int[] trig = new int[n];
            int id = 0;
            // Trigger nodes first so their ids are known run_trigger targets. Only T0 is enabled (fired by
            // match_start); the rest are disabled and run ONLY via run_trigger, so d{i} reflects run_trigger depth alone.
            for (int i = 0; i < n; i++) { trig[i] = id; g.Nodes.Add(new TriggerNode { Id = id++, Name = "T" + i, Enabled = i == 0 }); }
            for (int i = 0; i < n; i++)
            {
                int ev = id++; g.Nodes.Add(new EventNode { Id = ev, Kind = "match_start" });
                g.ExecEdges.Add(new ExecEdge(ev, TriggerGraph.EventExecOutPort, trig[i], TriggerGraph.TriggerEventInPort));
                int set = id++; g.Nodes.Add(new ActionNode { Id = set, Kind = "set_variable", Variable = "d" + i, Value = 1 });
                vars.Add(IntVar("d" + i));
                g.ExecEdges.Add(new ExecEdge(trig[i], TriggerGraph.TriggerExecOutPort, set, TriggerGraph.ActionExecInPort));
                if (i < n - 1)
                {
                    int run = id++; g.Nodes.Add(new RunTriggerNode { Id = run, TargetTriggerId = trig[i + 1] });
                    g.ExecEdges.Add(new ExecEdge(set, TriggerGraph.ActionExecOutPort, run, TriggerGraph.ActionExecInPort));
                }
            }

            var scenario = new ScenarioData { Variables = vars.ToArray(), TriggerGraphJson = g.ToCanonicalJson() };
            (ScenarioDirector director, DslVarTable vt) = Build(scenario);
            director.Tick(new EntityWorld(), Fixed.One); // MUST terminate — a broken seatbelt would stack-overflow / hang.

            // T0 runs at depth 0 (base sweep); each run_trigger increments depth BEFORE the target body, so the body
            // of T{k} runs at depth k. The run_trigger ISSUED FROM the depth==cap body is the one blocked.
            Assert.Equal(1, vt.GetInt("d0", 0));                // chain started
            Assert.Equal(1, vt.GetInt("d" + cap, 0));           // the depth==cap body ran (boundary reached)
            Assert.Equal(0, vt.GetInt("d" + (cap + 1), 0));     // the body BEYOND the cap was halted (seatbelt)
        }

        // ── new built-in event sources ───────────────────────────────────────────

        /// <summary>A single trigger subscribing to <paramref name="eventKind"/> for faction slot 0 → set_variable(got=1).</summary>
        private static ScenarioData EventTriggerScenario(string eventKind)
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = eventKind, Faction = 0 });
            g.Nodes.Add(new ActionNode { Id = 2, Kind = "set_variable", Variable = "got", Value = 1 });
            g.ExecEdges.Add(new ExecEdge(1, 0, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            return new ScenarioData { Variables = new[] { IntVar("got") }, TriggerGraphJson = g.ToCanonicalJson() };
        }

        [Theory]
        [InlineData("unit_damaged", DslSimEventFeed.KindUnitDamaged)]
        [InlineData("unit_trained", DslSimEventFeed.KindUnitTrained)]
        [InlineData("ability_cast", DslSimEventFeed.KindAbilityCast)]
        [InlineData("hero_level",   DslSimEventFeed.KindHeroLevel)]
        public void SimEvent_DrainsToBaseBuffer_AndFiresSubscribedTrigger(string eventKind, int feedCode)
        {
            var feed = new DslSimEventFeed();
            (ScenarioDirector director, DslVarTable vt) = Build(EventTriggerScenario(eventKind), feed: feed);
            // First tick fires match_start-less; push the sim occurrence for faction slot 0 before this tick.
            feed.Push(feedCode, factionSlot: 0, p0: 42, p1: 1, p2: 0);
            director.Tick(new EntityWorld(), Fixed.One);
            Assert.Equal(1, vt.GetInt("got", 0));
        }

        [Fact]
        public void SimEvent_WrongFactionSlot_DoesNotFire()
        {
            var feed = new DslSimEventFeed();
            (ScenarioDirector director, DslVarTable vt) = Build(EventTriggerScenario("unit_trained"), feed: feed);
            feed.Push(DslSimEventFeed.KindUnitTrained, factionSlot: 1, p0: 5, p1: 0, p2: 0); // slot 1, trigger wants slot 0
            director.Tick(new EntityWorld(), Fixed.One);
            Assert.Equal(0, vt.GetInt("got", 0));
        }

        [Fact]
        public void PlayerChat_TypeRegistered_TriggerLoadsButNoRaiseSiteThisCommit()
        {
            // player_chat is a registered built-in event type: a trigger subscribing to it LOADS cleanly (its raise
            // wire is Arm D, a later commit — so it never fires here). Loading without throwing is the assertion.
            var feed = new DslSimEventFeed();
            (ScenarioDirector director, DslVarTable vt) = Build(EventTriggerScenario("player_chat"), feed: feed);
            director.Tick(new EntityWorld(), Fixed.One);
            Assert.Equal(0, vt.GetInt("got", 0)); // no raise site this commit
        }

        [Fact]
        public void UnitDamaged_EndToEnd_ThroughDamageResolverRaiseSite()
        {
            // A live victim (faction slot 0) takes non-lethal damage through the real DamageResolver.Apply site,
            // which raises unit_damaged into the shared feed; the director drains it and the subscribed trigger fires.
            var world = new EntityWorld();
            int victim = world.Create(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.Zero),
                Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            int attacker = world.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.Zero),
                Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3));

            var feed = new DslSimEventFeed();
            (ScenarioDirector director, DslVarTable vt) = Build(EventTriggerScenario("unit_damaged"), feed: feed);

            var ctx = new DamageContext(world, victim, world.ArmorTypeOf[victim], Faction.Player2,
                DamageTable.Default, null, null, null, attackerId: attacker, dslSimEvents: feed);
            DamageResolver.Apply(in ctx, Fixed.FromInt(10), DamageType.Normal);

            director.Tick(world, Fixed.One);
            Assert.Equal(1, vt.GetInt("got", 0)); // victim faction slot 0 → the subscribed trigger fires
        }

        // ── run_trigger execution-model hardening (follow-up review) ───────────────

        [Fact]
        public void RunTrigger_TargetReadsEventParam_AsSentinelZero_NotCallerFrame()
        {
            // Frame-isolation (P1): A subscribes to unit_damaged (a param-bearing event) and, inside its live dispatch
            // frame, run_triggers B. B is authored DISABLED (so it runs ONLY via run_trigger, never on its own) and
            // reads event.amount into "seen". A run target is a synchronous GOSUB with NO event frame of its own, so B
            // must read the defined sentinel 0 — not BLEED A's dispatch frame (which carries amount=42). Before the
            // frame reset in ExecuteRunTrigger, B read A's frame and seen==42.
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "A" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "unit_damaged", Faction = 0 });
            g.Nodes.Add(new RunTriggerNode { Id = 2, TargetTriggerId = 3 });
            g.Nodes.Add(new TriggerNode { Id = 3, Name = "B", Enabled = false });
            g.Nodes.Add(new EventNode { Id = 4, Kind = "unit_damaged", Faction = 0 });
            g.Nodes.Add(new ActionNode { Id = 5, Kind = "set_variable", Variable = "seen" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(4, TriggerGraph.EventExecOutPort, 3, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(3, TriggerGraph.TriggerExecOutPort, 5, TriggerGraph.ActionExecInPort));

            var noVars = new System.Collections.Generic.Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal);
            (int amountRoot, _) = ExprParser.Parse("event.amount", g, noVars,
                eventParams: EventDispatchPlan.BuiltinEventParams["unit_damaged"]);
            g.DataEdges.Add(new DataEdge(amountRoot, TriggerGraph.ExprDataOutPort, 5, TriggerGraph.ActionValueInPort, DataWireType.Int));

            var feed = new DslSimEventFeed();
            var scenario = new ScenarioData { Variables = new[] { IntVar("seen") }, TriggerGraphJson = g.ToCanonicalJson() };
            (ScenarioDirector director, DslVarTable vt) = Build(scenario, feed: feed);
            feed.Push(DslSimEventFeed.KindUnitDamaged, factionSlot: 0, p0: 7, p1: 9, p2: 42); // amount = slot 2 = 42
            director.Tick(new EntityWorld(), Fixed.One);

            Assert.Equal(0, vt.GetInt("seen", 0)); // B ran at frame 0 → event.amount is the sentinel, NOT A's 42
        }

        [Fact]
        public void RunTrigger_DoesNotReSnapshotAnActiveBatchedRow_NoDoubleProcessing()
        {
            // Batched re-entrancy (P2): B is a DISABLED batched trigger (for_each_batched over faction_units, batch 10,
            // body: −1 HP) that runs ONLY via run_trigger. A subscribes to unit_damaged and run_triggers B whenever it
            // fires. Tick 1 snapshots B's 25-unit row; on tick 2 (mid-drain) A fires AGAIN and run_triggers B — its
            // continuation row is still ACTIVE, so run_trigger must be suppressed exactly as every other trigger-entry
            // path is (:1342). Without the guard the second SnapshotBatched resets the row cursor and re-processes the
            // already-drained units, taking some below −1 HP.
            var world = new EntityWorld();
            var units = new int[25];
            for (int i = 0; i < units.Length; i++)
                units[i] = world.Create(new FixedVec3(Fixed.FromInt(i), Fixed.Zero, Fixed.Zero),
                    Faction.Player1, Fixed.FromInt(10), Fixed.One);

            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "B", Enabled = false });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new ForEachBatchedNode { Id = 2, Source = "faction_units", Faction = 0, BatchSize = 10 });
            g.Nodes.Add(new EffectActionNode { Id = 3, Effect = new DirectHpDeltaEffect(Fixed.FromInt(-1)) });
            g.Nodes.Add(new TriggerNode { Id = 4, Name = "A" });
            g.Nodes.Add(new EventNode { Id = 5, Kind = "unit_damaged", Faction = 0 });
            g.Nodes.Add(new RunTriggerNode { Id = 6, TargetTriggerId = 0 });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ForEachBodyOutPort, 3, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(5, TriggerGraph.EventExecOutPort, 4, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(4, TriggerGraph.TriggerExecOutPort, 6, TriggerGraph.ActionExecInPort));

            var feed = new DslSimEventFeed();
            var vars = new DslVarTable();
            var loop = new DslLoopState();
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), vars, loop, simEventFeed: feed);
            director.LoadScenario(new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() });

            // tick 1: A fires (unit_damaged) → run_trigger B → snapshot 25 (row active); no body work yet.
            feed.Clear(); feed.Push(DslSimEventFeed.KindUnitDamaged, 0, 0, 0, 0);
            director.Tick(world, Fixed.One);
            // tick 2: drain 10 → A fires AGAIN → run_trigger B → GUARD skips (row still active).
            feed.Clear(); feed.Push(DslSimEventFeed.KindUnitDamaged, 0, 0, 0, 0);
            director.Tick(world, Fixed.One);
            // ticks 3-6: no push (A never fires) → the ORIGINAL drip finishes (10 + 5) and completes; no re-snapshot.
            feed.Clear(); director.Tick(world, Fixed.One);
            feed.Clear(); director.Tick(world, Fixed.One);
            feed.Clear(); director.Tick(world, Fixed.One);
            feed.Clear(); director.Tick(world, Fixed.One);

            // Every unit was damaged EXACTLY once by the single drip (10 → 9). A re-snapshot would leave some at ≤ 8.
            foreach (int u in units)
                Assert.Equal(Fixed.FromInt(9).Raw, world.Health[u].Raw);
        }
    }
}
