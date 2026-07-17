#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 7.7 — the lobby-handshake perf budget on a CAPS-SATURATED scenario (max units / buildings /
    /// resource nodes, plus a dense region/variable/timer surface and a <c>DslBounds.MaxDslOpsPerTrigger</c>-scale
    /// trigger graph, ~4000 nodes + ~4000 exec edges — the worst-case parse+fold the v8 hash walks).
    ///
    /// <para><b>Cost structure (verified against the real consumer).</b> The wire hash is computed EXACTLY ONCE per
    /// load — <c>MainScene</c> folds <c>CanonicalModelHash.Compute(appliedModel)</c> into <c>LobbyUi.ScenarioHash</c>
    /// right after apply (<c>MainScene.cs:479-480</c>). The lobby Ready handler then only SENDS and COMPARES that
    /// cached <c>uint</c> via <c>HandshakeGate.CheckStart</c> — it never calls <c>Compute</c>. So the handshake path
    /// itself pays ZERO hash time, and the graph parse the first Compute pays is a parse the load ALREADY pays in
    /// <c>ScenarioDirector.LoadScenario</c> (it lowers the same <c>TriggerGraphJson</c> to build the execution graph).</para>
    ///
    /// <para>This test therefore pins the budget where it recurs and hides nothing:
    /// (1) the WARM / amortized median (the memoized path every repeated Compute in a session hits — map compare,
    /// edit re-hash) stays within the <b>low-tens-of-ms</b> handshake budget (<see cref="WarmBudgetMillis"/>);
    /// (2) the <c>_graphMemo</c> demonstrably collapses cost (cold ≫ warm), which is exactly what makes MainScene's
    /// one precompute + the handshake's cached compare cheap; and
    /// (3) the COLD one-time max-caps compute stays under a generous REGRESSION ceiling
    /// (<see cref="ColdRegressionCeilingMillis"/>) so the worst case is bounded and visible, not buried behind the memo.
    /// The cold ceiling is a regression guard on a one-time LOAD cost (~96 ms on the dev box for the all-caps ceiling
    /// no real scenario approaches), NOT the "low tens of ms" claim — tightening it below the warm budget means
    /// sharing LoadScenario's single parse or a streaming <c>Utf8JsonReader</c> converter, tracked as deferred work.</para>
    /// </summary>
    public class CanonicalModelHashPerfTests
    {
        /// <summary>Low-tens-of-ms handshake budget for the amortized (memoized) compute the session repeatedly pays.</summary>
        private const int WarmBudgetMillis = 50;

        /// <summary>Regression ceiling for the one-time COLD max-caps load compute (dev-box median ~96 ms; generous
        /// headroom for a loaded CI runner). Bounds the pathological worst case; not the handshake budget.</summary>
        private const int ColdRegressionCeilingMillis = 250;

        /// <summary>
        /// Environment-scoped ceiling multiplier from <c>CHIMERA_PERF_CEILING_SCALE</c> (default 1.0 — the local
        /// ceilings above stay tight). Shared CI runners are noisy/oversubscribed: the determinism-gate workflow
        /// sets 2.0 there after a run overshot the cold ceiling by ~3.5% with no code change (run 29617704476),
        /// because hardware noise must not red a DETERMINISM gate — while a real regression, which blows straight
        /// past 2x, still fails everywhere. Invariant-parsed; missing/invalid/non-positive falls back to 1.0.
        /// </summary>
        private static readonly double CeilingScale =
            double.TryParse(Environment.GetEnvironmentVariable("CHIMERA_PERF_CEILING_SCALE"),
                            NumberStyles.Float, CultureInfo.InvariantCulture, out double s) && s > 0 ? s : 1.0;

        /// <summary>Median-of-5 so a single GC/scheduler hiccup cannot flake the gate.</summary>
        private const int Runs = 5;

        private static ScenarioData BuildMaxCapsScenario()
        {
            const float bounds = 500f;

            var slots = new ScenarioPlayerSlot[4]; // the engine ceiling (slots 0..3)
            for (int i = 0; i < slots.Length; i++)
                slots[i] = new ScenarioPlayerSlot { Slot = i, StartOre = 200f, StartCrystal = 100f, BaseX = -100f + i * 50f, BaseZ = -100f };

            var units = new ScenarioUnit[EntityWorld.MAX_ENTITIES]; // 4096 — the world capacity
            for (int i = 0; i < units.Length; i++)
                units[i] = new ScenarioUnit { UnitId = "worker", Slot = i % 4, X = (i % 200) - 100f, Z = (i / 200) - 100f };

            var buildings = new ScenarioBuilding[BuildingStore.MAX_BUILDINGS]; // 64
            for (int i = 0; i < buildings.Length; i++)
                buildings[i] = new ScenarioBuilding { Type = "Barracks", Slot = i % 4, X = i - 32f, Z = 40f };

            var nodes = new ScenarioResourceNode[ResourceNodeStore.MAX_NODES]; // 64
            for (int i = 0; i < nodes.Length; i++)
                nodes[i] = new ScenarioResourceNode { X = i - 32f, Z = -40f, Supply = 400f + i, Rate = 5f, MaxGatherers = 4 };

            var regions = new ScenarioRegion[256];
            for (int i = 0; i < regions.Length; i++)
                regions[i] = new ScenarioRegion { Id = $"r{i}", Name = $"R{i}", MinX = -100f + i, MinZ = -100f, MaxX = -90f + i, MaxZ = -90f };

            var variables = new ScenarioVariable[256];
            for (int i = 0; i < variables.Length; i++)
                variables[i] = new ScenarioVariable { Name = $"v{i}", Type = DslValueType.Int, Scope = VarScope.Global, Initial = Fixed.FromInt(i) };

            var timers = new ScenarioTimer[64];
            for (int i = 0; i < timers.Length; i++)
                timers[i] = new ScenarioTimer { Name = $"t{i}", Seconds = Fixed.FromInt(10 + i) };

            // Flat triggers: a dense authored set.
            var triggers = new TriggerDefinition[64];
            for (int i = 0; i < triggers.Length; i++)
                triggers[i] = new TriggerDefinition
                {
                    Name = $"flat{i}",
                    Events = new[] { new TriggerEvent { Type = "resource_threshold", Faction = i % 4, Amount = Fixed.FromInt(100 + i) } },
                    Conditions = new[] { new TriggerCondition { Type = "unit_count", Faction = i % 4, Count = i } },
                    Actions = new[]
                    {
                        new TriggerAction { Type = "add_resources", Faction = i % 4, Amount = Fixed.FromInt(i) },
                        new TriggerAction { Type = "display_message", Text = $"trigger {i}" },
                    },
                };

            // Graph channel: 8 triggers × a 500-action chain ≈ MaxDslOpsPerTrigger-scale static cost each,
            // ~4000 nodes + ~4000 exec edges total — the worst-case parse+fold the v8 hash pays.
            var g = new TriggerGraph();
            int id = 0;
            for (int t = 0; t < 8; t++)
            {
                int trig = id++;
                g.Nodes.Add(new TriggerNode { Id = trig, Name = $"graph{t}" });
                int ev = id++;
                g.Nodes.Add(new EventNode { Id = ev, Kind = "match_start" });
                g.ExecEdges.Add(new ExecEdge(ev, TriggerGraph.EventExecOutPort, trig, TriggerGraph.TriggerEventInPort));
                int prev = trig, prevPort = TriggerGraph.TriggerExecOutPort;
                for (int a = 0; a < 500; a++)
                {
                    int act = id++;
                    g.Nodes.Add(new ActionNode { Id = act, Kind = "display_message", Text = $"m{t}.{a}" });
                    g.ExecEdges.Add(new ExecEdge(prev, prevPort, act, TriggerGraph.ActionExecInPort));
                    prev = act;
                    prevPort = TriggerGraph.ActionExecOutPort;
                }
            }

            return new ScenarioData
            {
                Id = "max-caps", DisplayName = "Max Caps Perf Fixture", TerrainRef = "",
                MapBounds = bounds, WinCondition = WinCondition.DestroyAllBuildings,
                PlayerSlots = slots, Units = units, Buildings = buildings, ResourceNodes = nodes,
                Regions = regions, Variables = variables, Timers = timers, Triggers = triggers,
                TriggerGraphJson = g.ToCanonicalJson(),
            };
        }

        /// <summary>A small (non-max-caps) model with a tiny graph, used ONLY to warm the JIT so the measured
        /// samples pay parse/fold cost, never first-call compilation.</summary>
        private static ScenarioData BuildJitWarmupScenario() => new ScenarioData
        {
            Id = "warmup", DisplayName = "Warmup", TerrainRef = "", MapBounds = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[] { new ScenarioPlayerSlot { Slot = 0 } },
            Regions   = new[] { new ScenarioRegion { Id = "r", MinX = -1f, MinZ = -1f, MaxX = 1f, MaxZ = 1f } },
            Variables = new[] { new ScenarioVariable { Name = "v", Type = DslValueType.Int, Scope = VarScope.Global } },
            Timers    = new[] { new ScenarioTimer { Name = "t", Seconds = Fixed.FromInt(5) } },
            TriggerGraphJson =
                "{\"nodes\":[{\"id\":0,\"kind\":\"trigger\",\"name\":\"w\"},{\"id\":1,\"kind\":\"match_start\"}]," +
                "\"exec_edges\":[{\"src\":1,\"src_port\":0,\"dst\":0,\"dst_port\":0}],\"data_edges\":[]}",
        };

        private static double MedianComputeMs(ScenarioData model, HeroStore heroes, bool cold)
        {
            var samples = new List<double>(Runs);
            for (int i = 0; i < Runs; i++)
            {
                // COLD: drop the parse memo so this Compute pays the full graph parse (the load's real first-compute
                // worst case). WARM: leave the memo seeded so it hits the cache, as every repeated in-session Compute
                // does. Distinct string instances cannot defeat the memo — it keys on ordinal CONTENT equality.
                if (cold) CanonicalModelHash.ClearGraphMemoForTests();
                var sw = Stopwatch.StartNew();
                ulong h1 = CanonicalModelHash.Compute(model);
                ulong h2 = StartStateHash.Compute(model, heroes);
                sw.Stop();
                Assert.NotEqual(0UL, h1);
                Assert.NotEqual(0UL, h2);
                samples.Add(sw.Elapsed.TotalMilliseconds);
            }
            samples.Sort();
            return samples[Runs / 2];
        }

        [Fact]
        public void MaxCapsScenario_WarmComputeMedian_WithinTheHandshakeBudget()
        {
            ScenarioData model = BuildMaxCapsScenario();
            var heroes = new HeroStore();
            heroes.Mint(new HeroId(1_000_000_007UL), entityId: 3, level: 4, xp: Fixed.FromInt(250));

            // JIT warm-up on a SEPARATE small model so first-call compilation is not measured.
            ScenarioData warmup = BuildJitWarmupScenario();
            for (int i = 0; i < 3; i++)
                Assert.NotEqual(0UL, CanonicalModelHash.Compute(warmup) ^ StartStateHash.Compute(warmup, heroes));

            // Seed the memo with the max-caps graph, then measure the amortized (warm) compute the session repeats.
            CanonicalModelHash.ClearGraphMemoForTests();
            CanonicalModelHash.Compute(model);
            double warmMedian = MedianComputeMs(model, heroes, cold: false);

            Assert.True(warmMedian <= WarmBudgetMillis * CeilingScale,
                $"Warm median-of-{Runs} Compute+StartStateHash {warmMedian:F2} ms exceeds the " +
                $"{WarmBudgetMillis * CeilingScale:F0} ms (= {WarmBudgetMillis} ms x{CeilingScale:0.##}) " +
                "low-tens-of-ms handshake budget — the memoized/amortized path regressed.");
        }

        [Fact]
        public void MaxCapsScenario_ColdCompute_StaysUnderTheRegressionCeiling_AndTheMemoCollapsesIt()
        {
            ScenarioData model = BuildMaxCapsScenario();
            var heroes = new HeroStore();
            heroes.Mint(new HeroId(1_000_000_007UL), entityId: 3, level: 4, xp: Fixed.FromInt(250));

            ScenarioData warmup = BuildJitWarmupScenario();
            for (int i = 0; i < 3; i++)
                Assert.NotEqual(0UL, CanonicalModelHash.Compute(warmup) ^ StartStateHash.Compute(warmup, heroes));

            double coldMedian = MedianComputeMs(model, heroes, cold: true);
            double warmMedian = MedianComputeMs(model, heroes, cold: false); // memo now seeded

            // The one-time cold LOAD compute is bounded (regression guard on the pathological all-caps ceiling).
            Assert.True(coldMedian <= ColdRegressionCeilingMillis * CeilingScale,
                $"Cold median-of-{Runs} Compute+StartStateHash {coldMedian:F2} ms exceeds the " +
                $"{ColdRegressionCeilingMillis * CeilingScale:F0} ms (= {ColdRegressionCeilingMillis} ms " +
                $"x{CeilingScale:0.##}) one-time-load regression ceiling. The handshake compares a cached " +
                "hash and never recomputes (MainScene.cs:479); if this must drop, share LoadScenario's graph parse " +
                "or stream the converter (deferred work) rather than loosening the ceiling.");

            // The memo must demonstrably collapse the cold parse — this is what keeps MainScene's precompute + the
            // handshake's cached compare cheap, and it is why the warm path meets the low-tens-of-ms budget.
            Assert.True(warmMedian < coldMedian,
                $"Warm compute {warmMedian:F2} ms was not below cold {coldMedian:F2} ms — the graph parse memo is not effective.");
        }
    }
}
