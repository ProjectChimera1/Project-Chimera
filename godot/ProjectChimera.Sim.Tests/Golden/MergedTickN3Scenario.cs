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
    /// Story 9.7 — the N=3 / N=4 FR-39 regression scenario, proving the RAISED player count merges deterministically.
    /// Structurally identical to <see cref="MergedTickN2Scenario"/> (the same order-SENSITIVE bump-event fold
    /// <c>g = g * 3 + v</c>), generalized to N factions: N factions each own 2 movable units and raise a per-faction
    /// bump payload; the per-tick stream is fed through the REAL <see cref="MergedTickBuilder"/>(N) +
    /// <see cref="MergedTickApplier"/> (merged path) and the direct per-faction apply (ascending baseline). Because
    /// the fold is non-commutative, the merged path reproducing the ascending baseline locks that the N-way
    /// server-authoritative merge uses ascending-faction order at the raised count.
    /// </summary>
    public static class MergedTickN3Scenario
    {
        /// <summary>300 ticks = 10s at 30 tps; ChecksumInterval=1 → 300 samples.</summary>
        public const int DefaultTicks = 300;

        /// <summary>The NEW golden files — distinct from every existing committed golden (never re-recorded here).</summary>
        public static string GoldenFileName(int n) => $"golden-merged-n{n}.golden.txt";

        /// <summary>Registry index (declaration order in CustomEvents) of the order-sensitivity <c>bump</c> event.</summary>
        private const int BumpEventIndex = 0;

        /// <summary>Self-identifying header so the golden declares its own re-baseline recipe.</summary>
        public static GoldenChecksumReplay.GoldenHeader Header(int n) => new(
            $"server-authoritative merged-tick N={n} golden (Story 9.7)",
            $"Pins the SimChecksum sequence for a fixed {n}-faction world driven by a deterministic per-tick " +
            "N-faction UnitOrder stream (movable units + an order-SENSITIVE bump-event fold g=g*3+v) through the " +
            "REAL MergedTickBuilder + MergedTickApplier (the full N-way wire merge round-trip).",
            $"set {GoldenChecksumReplay.RecordEnvVar}=1, run `dotnet test --filter FullyQualifiedName~MergedTickN3`, " +
            "then `dotnet build` (refreshes the embedded copy) and commit. DO NOT hand-edit. NEVER record the existing goldens.");

        private static Faction[] SlotFactions(int n)
        {
            var a = new Faction[n];
            for (int i = 0; i < n; i++) a[i] = FactionRegistry.ToFaction(i);
            return a;
        }

        /// <summary>Faction i owns units at ids [i*2, i*2+1].</summary>
        private static int[] UnitsFor(int factionSlot) => new[] { factionSlot * 2, factionSlot * 2 + 1 };

        /// <summary>
        /// Construct the fixed N-faction world: each faction's 2 units created in ascending slot order (ids 0..2N-1),
        /// all movable. Also loads a scenario declaring a Global <c>g</c>, a <c>bump</c> event raisable by every slot,
        /// and the per-occurrence order-sensitive handler <c>g = g * 3 + event.v</c>.
        /// </summary>
        public static GoldenHarness BuildHost(int n)
        {
            var host = SimulationHost.Create(
                NullLogSink.Instance, new FactionRegistry(n), new FactionDefinition(), new FactionDefinition());
            host.ChecksumInterval = 1;

            int nextId = 0;
            for (int slot = 0; slot < n; slot++)
            {
                Faction f = FactionRegistry.ToFaction(slot);
                for (int u = 0; u < 2; u++)
                {
                    int x = (slot % 2 == 0 ? -10 : 10) + slot;
                    int id = host.World.Create(
                        new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(u * 4)),
                        f, Fixed.FromInt(100), Fixed.FromInt(3));
                    if (id != nextId)
                        throw new InvalidOperationException(
                            $"MergedTickN3Scenario invariant broken: unit id was {id}, expected {nextId}.");
                    nextId++;
                }
            }

            host.ScenarioDirector.LoadScenario(BuildOrderSensitiveScenario(n));
            return new GoldenHarness(host, 0);
        }

        private static ScenarioData BuildOrderSensitiveScenario(int n)
        {
            var vars = new[]
            {
                new ScenarioVariable { Name = "g", Type = DslValueType.Int, Scope = VarScope.Global, Initial = Fixed.Zero },
            };
            var allowed = new int[n];
            for (int i = 0; i < n; i++) allowed[i] = i;
            var events = new[]
            {
                new ScenarioCustomEvent
                {
                    Name = "bump",
                    Params = new[] { new ScenarioEventParam { Name = "v", Type = DslValueType.Int } },
                    AllowedRaisers = allowed,
                },
            };
            var declMap = new Dictionary<string, (DslValueType, VarScope)> { ["g"] = (DslValueType.Int, VarScope.Global) };
            TriggerGraph handler = TriggerGraph.BuildCustomEventTrigger(
                "bump_fold", "custom_event", "bump", null, null, null, -1, false,
                "g", 0, "g * 3 + event.v", declMap, events);

            return new ScenarioData { Variables = vars, CustomEvents = events, TriggerGraphJson = handler.ToCanonicalJson() };
        }

        /// <summary>Fill deterministic orders for one faction: a Move per unit + one bump raise (faction-distinct payload).</summary>
        private static int FillFactionOrders(int i, int factionSlot, int[] units, UnitOrder[] buf)
        {
            int n = 0;
            for (int k = 0; k < units.Length; k++)
            {
                int id = units[k];
                int tx = ((i * 3 + id * 7)  % 30) - 15;
                int tz = ((i * 5 + id * 11) % 24) - 12;
                buf[n++] = new UnitOrder(id, UnitCommand.Move, Fixed.FromInt(tx), Fixed.FromInt(tz));
            }
            // Faction-distinct, per-tick-varying payload so the g=g*3+v fold is genuinely order-sensitive across slots.
            int v = (i % 40) + 1 + factionSlot * 100;
            buf[n++] = new UnitOrder(BumpEventIndex, UnitCommand.DslEvent, Fixed.FromRaw(v), Fixed.FromRaw(0));
            return n;
        }

        // ── Drivers ─────────────────────────────────────────────────────────────

        /// <summary>The MERGED driver — one real <see cref="MergedTickBuilder"/>(N) for the run (fresh per driver).</summary>
        public sealed class MergedDriver
        {
            private readonly int _n;
            private readonly Faction[] _slotFactions;
            private readonly MergedTickBuilder _builder;
            private readonly Func<int, int, int, int, bool> _dslSink;
            private readonly UnitOrder[] _orders = new UnitOrder[TickCommandPacket.MAX_ORDERS];
            private readonly byte[] _packBuf = new byte[TickCommandPacket.HEADER_BYTES + TickCommandPacket.MAX_ORDERS * UnitOrder.SIZE];

            public MergedDriver(int n, Func<int, int, int, int, bool> dslSink)
            {
                _n = n;
                _slotFactions = SlotFactions(n);
                _builder = new MergedTickBuilder(n, _slotFactions);
                _dslSink = dslSink;
            }

            public void ApplyTick(int i, EntityWorld world)
            {
                uint tick = (uint)i;
                for (int slot = 0; slot < _n; slot++)
                {
                    int count = FillFactionOrders(i, slot, UnitsFor(slot), _orders);
                    int len   = TickCommandPacket.Write(_packBuf, tick, _slotFactions[slot], _orders, count);
                    _builder.Submit(slot, _packBuf, len, out _);
                }
                if (_builder.TryBuild(tick, out byte[] merged, out int mergedLen))
                    MergedTickApplier.Apply(merged, mergedLen, world, null, null, null, null, null, null, null, _dslSink);
            }
        }

        /// <summary>The DIRECT-apply baseline — the SAME stream applied per faction ASCENDING (the merge's canonical order).</summary>
        public static void ApplyDirectTick(int i, int n, EntityWorld world, Func<int, int, int, int, bool> dslSink, bool ascending)
        {
            var buf = new UnitOrder[TickCommandPacket.MAX_ORDERS];
            if (ascending)
            {
                for (int slot = 0; slot < n; slot++)
                    ApplyOne(i, slot, world, dslSink, buf);
            }
            else
            {
                for (int slot = n - 1; slot >= 0; slot--)
                    ApplyOne(i, slot, world, dslSink, buf);
            }
        }

        private static void ApplyOne(int i, int slot, EntityWorld world, Func<int, int, int, int, bool> dslSink, UnitOrder[] buf)
        {
            Faction f = FactionRegistry.ToFaction(slot);
            int count = FillFactionOrders(i, slot, UnitsFor(slot), buf);
            for (int k = 0; k < count; k++)
                OrderApplier.Apply(world, in buf[k], f, null, null, null, null, null, null, null, dslSink);
        }

        // ── Runners ─────────────────────────────────────────────────────────────

        public static IReadOnlyList<GoldenChecksumReplay.Sample> RunMerged(int n, int ticks = DefaultTicks)
        {
            GoldenHarness h = BuildHost(n);
            var driver = new MergedDriver(n, h.Host.DslEventSink);
            return Run(h, ticks, (i, w) => driver.ApplyTick(i, w));
        }

        public static IReadOnlyList<GoldenChecksumReplay.Sample> RunDirect(int n, int ticks = DefaultTicks, bool ascending = true)
        {
            GoldenHarness h = BuildHost(n);
            Func<int, int, int, int, bool> sink = h.Host.DslEventSink;
            return Run(h, ticks, (i, w) => ApplyDirectTick(i, n, w, sink, ascending));
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
