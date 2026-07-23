#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core;                 // EntityWorld, Faction, UnitOrder, UnitCommand, Fixed, FixedVec3
using ProjectChimera.Core.Definitions;     // FactionDefinition, ScenarioData, ScenarioVariable, ScenarioCustomEvent
using ProjectChimera.Core.Sim;             // SimulationHost, NullLogSink
using ProjectChimera.Dsl;                  // DslValueType, VarScope, TriggerGraph
using ProjectChimera.Multiplayer;          // TickCommandPacket, OrderApplier
using ProjectChimera.Multiplayer.Server;   // MergedTickBuilder, MergedTickApplier

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 9.3 — the N=2 FR-39 regression scenario. A fixed 2-faction world (2 movable Player1 units + 2 movable
    /// Player2 units) is driven by a DETERMINISTIC per-tick P1+P2 <see cref="UnitOrder"/> stream fed two ways:
    ///   • MERGED path: each faction's orders → a single-faction <see cref="TickCommandPacket"/> → the REAL
    ///     <see cref="MergedTickBuilder"/> → the emitted <see cref="MergedTickPacket"/> applied via the REAL
    ///     <see cref="MergedTickApplier"/> — the full server + client wire round-trip.
    ///   • DIRECT path: the SAME orders applied straight through <see cref="OrderApplier.Apply"/> per faction (in
    ///     a chosen order) with no merge/serialize round-trip — the pre-rewrite intra-tick semantics.
    ///
    /// <para><b>Order-SENSITIVE by construction (Patch C).</b> Each faction ALSO raises a custom <c>bump</c> event
    /// carrying a per-faction payload; a handler folds it into a Global via the NON-COMMUTATIVE accumulation
    /// <c>g = g * 3 + event.v</c>. Because the per-occurrence handler dispatches the two raises in APPLY ORDER,
    /// P1-then-P2 and P2-then-P1 leave <c>g</c> at different values → a different SimChecksum. So the golden +
    /// baseline can now genuinely FAIL on an apply-order flip (a reversed applier would diverge — proven directly
    /// by <c>MergedTickGoldenTests.AscendingVsDescendingDirectApply_Diverges</c>), and the merged path reproducing
    /// the ascending baseline locks that the server-authoritative merge uses ascending-faction order.</para>
    /// </summary>
    public static class MergedTickN2Scenario
    {
        /// <summary>300 ticks = 10s at 30 tps; ChecksumInterval=1 → 300 samples.</summary>
        public const int DefaultTicks = 300;

        /// <summary>The NEW golden file — distinct from every existing committed golden (never re-recorded here).</summary>
        public const string GoldenFileName = "golden-merged-n2.golden.txt";

        private static readonly Faction[] SlotFaction = { Faction.Player1, Faction.Player2 };
        private static readonly int[] P1Units = { 0, 1 };
        private static readonly int[] P2Units = { 2, 3 };

        /// <summary>Registry index (declaration order in CustomEvents) of the order-sensitivity <c>bump</c> event.</summary>
        private const int BumpEventIndex = 0;

        /// <summary>Self-identifying header so the golden declares its own re-baseline recipe (its own filter).</summary>
        public static GoldenChecksumReplay.GoldenHeader Header => new(
            "server-authoritative merged-tick N=2 golden (Story 9.3)",
            "Pins the SimChecksum sequence for a fixed 2-faction world driven by a deterministic per-tick P1+P2 " +
            "UnitOrder stream (movable units + an order-SENSITIVE bump-event fold g=g*3+v) through the REAL " +
            "MergedTickBuilder + MergedTickApplier (the full wire merge round-trip).",
            $"set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~MergedTick`, " +
            "then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit. NEVER record the existing goldens.");

        /// <summary>
        /// Construct the fixed 2-faction world: Player1 units at ids 0,1 (created FIRST) and Player2 units at ids
        /// 2,3, all movable. Also loads a scenario declaring a Global <c>g</c>, a <c>bump</c> custom event
        /// (raisable by slots 0 and 1), and a per-occurrence handler <c>g = g * 3 + event.v</c> — the order-sensitive
        /// fold. Fresh stores on every call — no static/shared state. Drive it via <see cref="RunMerged"/> /
        /// <see cref="RunDirect"/>, which wire the host's <c>DslEventSink</c> into the applied orders.
        /// </summary>
        public static GoldenHarness BuildHost()
        {
            var host = SimulationHost.Create(
                NullLogSink.Instance, new FactionRegistry(2), new FactionDefinition(), new FactionDefinition());
            host.ChecksumInterval = 1;

            int a = host.World.Create(new FixedVec3(Fixed.FromInt(-10), Fixed.Zero, Fixed.FromInt(0)), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            int b = host.World.Create(new FixedVec3(Fixed.FromInt(-10), Fixed.Zero, Fixed.FromInt(4)), Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            int c = host.World.Create(new FixedVec3(Fixed.FromInt( 10), Fixed.Zero, Fixed.FromInt(0)), Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3));
            int d = host.World.Create(new FixedVec3(Fixed.FromInt( 10), Fixed.Zero, Fixed.FromInt(4)), Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3));
            if (a != 0 || b != 1 || c != 2 || d != 3)
                throw new InvalidOperationException(
                    $"MergedTickN2Scenario invariant broken: unit ids were {a},{b},{c},{d}, expected 0,1,2,3.");

            host.ScenarioDirector.LoadScenario(BuildOrderSensitiveScenario());
            return new GoldenHarness(host, 0);
        }

        /// <summary>The Global <c>g</c> + <c>bump</c> event + order-sensitive handler that make apply order observable.</summary>
        private static ScenarioData BuildOrderSensitiveScenario()
        {
            var vars = new[]
            {
                new ScenarioVariable { Name = "g", Type = DslValueType.Int, Scope = VarScope.Global, Initial = Fixed.Zero },
            };
            var events = new[]
            {
                new ScenarioCustomEvent
                {
                    Name = "bump",
                    Params = new[] { new ScenarioEventParam { Name = "v", Type = DslValueType.Int } },
                    AllowedRaisers = new[] { 0, 1 }, // Player1 (slot 0) and Player2 (slot 1) may raise it
                },
            };
            var declMap = new Dictionary<string, (DslValueType, VarScope)> { ["g"] = (DslValueType.Int, VarScope.Global) };
            // Per-occurrence (reads event.v) handler → dispatches once per queued raise in APPLY order; the
            // NON-COMMUTATIVE fold makes P1-then-P2 differ from P2-then-P1.
            TriggerGraph handler = TriggerGraph.BuildCustomEventTrigger(
                "bump_fold", "custom_event", "bump", null, null, null, -1, false,
                "g", 0, "g * 3 + event.v", declMap, events);

            return new ScenarioData { Variables = vars, CustomEvents = events, TriggerGraphJson = handler.ToCanonicalJson() };
        }

        /// <summary>
        /// Fill <paramref name="buf"/> with the deterministic orders for <paramref name="faction"/>'s
        /// <paramref name="units"/> at loop index <paramref name="i"/>: a Move per unit (oscillating targets, pure
        /// integer math) plus ONE <c>bump</c> raise carrying a faction-distinct payload (so apply order is
        /// observable). Byte-identical function of (i, faction) across processes and platforms.
        /// </summary>
        private static int FillFactionOrders(int i, Faction faction, int[] units, UnitOrder[] buf)
        {
            int n = 0;
            for (int k = 0; k < units.Length; k++)
            {
                int id = units[k];
                int tx = ((i * 3 + id * 7)  % 30) - 15;
                int tz = ((i * 5 + id * 11) % 24) - 12;
                buf[n++] = new UnitOrder(id, UnitCommand.Move, Fixed.FromInt(tx), Fixed.FromInt(tz));
            }
            // The bump raise: UnitId = event registry index; TargetX = payload v (raw int, read directly). Player1's
            // payload and Player2's differ every tick so the g=g*3+v fold is genuinely order-sensitive.
            int v = faction == Faction.Player1 ? (i % 40) + 1 : (i % 40) + 101;
            buf[n++] = new UnitOrder(BumpEventIndex, UnitCommand.DslEvent, Fixed.FromRaw(v), Fixed.FromRaw(0));
            return n;
        }

        // ── Drivers ─────────────────────────────────────────────────────────────

        /// <summary>The MERGED driver — one real <see cref="MergedTickBuilder"/> for the run (fresh per driver).</summary>
        public sealed class MergedDriver
        {
            private readonly MergedTickBuilder _builder = new(2, SlotFaction);
            private readonly Func<int, int, int, int, bool> _dslSink;
            private readonly UnitOrder[] _p1 = new UnitOrder[TickCommandPacket.MAX_ORDERS];
            private readonly UnitOrder[] _p2 = new UnitOrder[TickCommandPacket.MAX_ORDERS];
            private readonly byte[] _packBuf = new byte[TickCommandPacket.HEADER_BYTES + TickCommandPacket.MAX_ORDERS * UnitOrder.SIZE];

            public MergedDriver(Func<int, int, int, int, bool> dslSink) => _dslSink = dslSink;

            public void ApplyTick(int i, EntityWorld world)
            {
                uint tick = (uint)i;
                int n1 = FillFactionOrders(i, Faction.Player1, P1Units, _p1);
                int l1 = TickCommandPacket.Write(_packBuf, tick, Faction.Player1, _p1, n1);
                _builder.Submit(0, _packBuf, l1, out _);

                int n2 = FillFactionOrders(i, Faction.Player2, P2Units, _p2);
                int l2 = TickCommandPacket.Write(_packBuf, tick, Faction.Player2, _p2, n2);
                _builder.Submit(1, _packBuf, l2, out _);

                if (_builder.TryBuild(tick, out byte[] merged, out int len))
                    MergedTickApplier.Apply(merged, len, world, null, null, null, null, null, null, null, _dslSink);
            }
        }

        /// <summary>
        /// The DIRECT-apply baseline — the SAME order stream applied straight through <see cref="OrderApplier.Apply"/>
        /// per faction, no merge/serialize round-trip. <paramref name="ascending"/> = true applies Player1 then
        /// Player2 (the merge's canonical order); false reverses it (the order-sensitivity control).
        /// </summary>
        public static void ApplyDirectTick(int i, EntityWorld world, Func<int, int, int, int, bool> dslSink, bool ascending)
        {
            var b1 = new UnitOrder[TickCommandPacket.MAX_ORDERS];
            var b2 = new UnitOrder[TickCommandPacket.MAX_ORDERS];
            int n1 = FillFactionOrders(i, Faction.Player1, P1Units, b1);
            int n2 = FillFactionOrders(i, Faction.Player2, P2Units, b2);

            if (ascending)
            {
                for (int k = 0; k < n1; k++) OrderApplier.Apply(world, in b1[k], Faction.Player1, null, null, null, null, null, null, null, dslSink);
                for (int k = 0; k < n2; k++) OrderApplier.Apply(world, in b2[k], Faction.Player2, null, null, null, null, null, null, null, dslSink);
            }
            else
            {
                for (int k = 0; k < n2; k++) OrderApplier.Apply(world, in b2[k], Faction.Player2, null, null, null, null, null, null, null, dslSink);
                for (int k = 0; k < n1; k++) OrderApplier.Apply(world, in b1[k], Faction.Player1, null, null, null, null, null, null, null, dslSink);
            }
        }

        // ── Runners (custom loop — the perturb hook needs the host's DslEventSink) ──

        /// <summary>Run the scenario through the REAL merge path with a FRESH builder (no cross-run state).</summary>
        public static IReadOnlyList<GoldenChecksumReplay.Sample> RunMerged(int ticks = DefaultTicks)
        {
            GoldenHarness h = BuildHost();
            var driver = new MergedDriver(h.Host.DslEventSink);
            return Run(h, ticks, (i, w) => driver.ApplyTick(i, w));
        }

        /// <summary>Run the same order stream through the direct-apply baseline (ascending or reversed).</summary>
        public static IReadOnlyList<GoldenChecksumReplay.Sample> RunDirect(int ticks = DefaultTicks, bool ascending = true)
        {
            GoldenHarness h = BuildHost();
            Func<int, int, int, int, bool> sink = h.Host.DslEventSink;
            return Run(h, ticks, (i, w) => ApplyDirectTick(i, w, sink, ascending));
        }

        private static IReadOnlyList<GoldenChecksumReplay.Sample> Run(GoldenHarness h, int ticks, Action<int, EntityWorld> perturb)
        {
            var seq = new List<GoldenChecksumReplay.Sample>(ticks);
            h.Host.SetChecksumSink((tick, hash) => seq.Add(new GoldenChecksumReplay.Sample(tick, hash)));
            for (int i = 0; i < ticks; i++)
            {
                perturb(i, h.World);
                h.Host.StepOnce();
            }
            return seq;
        }
    }
}
