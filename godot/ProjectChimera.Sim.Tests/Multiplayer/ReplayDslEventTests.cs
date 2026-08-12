#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
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
    /// Story 7.9 / Story 9.11 — replay-v4 ("replay v2"): a button-originated <see cref="UnitCommand.DslEvent"/> order
    /// rides the existing fixed 11-byte per-order record inside the frozen <see cref="MergedTickPacket"/> envelope,
    /// so recording + replaying a button stream through the SHARED <c>OrderApplier</c> (wired to the director's DSL
    /// sink) reproduces the identical tick-by-tick SimChecksum a live apply produces. <see cref="ReplayRecorder.VERSION"/>
    /// is 4; pre-v4 files are hard-rejected (that reject lives in <c>SimRngChecksumReplayTests</c>).
    /// </summary>
    public class ReplayDslEventTests
    {
        private static readonly Faction[] Roster2 = { Faction.Player1, Faction.Player2 };

        private static ReplayRecorder NewRec(string path)
            => new(path, "test://buy", EntityWorld.DEFAULT_RNG_SEED,
                   scenarioHash: 0x11UL, rulesetHash: 0x22UL, modelAlgoVersion: CanonicalModelHash.AlgoVersion, roster: Roster2);

        [Fact]
        public void ReplayFormatVersion_IsCurrent() => Assert.Equal(7, ReplayRecorder.VERSION); // DW-945 bumped 6→7 (14-byte stride, packed subject; 15-23 took it to 6)

        private static ScenarioVariable IntVar(string name) =>
            new() { Name = name, Type = DslValueType.Int, Scope = VarScope.Global, Initial = Fixed.Zero };

        private static ScenarioData BuyScenario()
        {
            ScenarioVariable[] vars = { IntVar("score") };
            var declMap = new Dictionary<string, (DslValueType Type, VarScope Scope)>(StringComparer.Ordinal) { ["score"] = (DslValueType.Int, VarScope.Global) };
            var events = new[]
            {
                new ScenarioCustomEvent
                {
                    Name = "buy",
                    Params = new[] { new ScenarioEventParam { Name = "amount", Type = DslValueType.Int } },
                    AllowedRaisers = new[] { 0 }, // Player1 slot
                },
            };
            TriggerGraph handler = TriggerGraph.BuildCustomEventTrigger(
                "H", "custom_event", "buy", null, null, null, -1, false,
                "score", 0, "event.amount", declMap, events);
            return new ScenarioData { Variables = vars, CustomEvents = events, TriggerGraphJson = handler.ToCanonicalJson() };
        }

        private static SimulationHost NewHost(out List<(uint, uint)> seq)
        {
            var host = SimulationHost.Create(new NullLogSink(), new FactionRegistry(2));
            host.ChecksumInterval = 1;
            var captured = new List<(uint, uint)>();
            host.SetChecksumSink((t, h) => captured.Add((t, h)));
            host.ScenarioDirector.LoadScenario(BuyScenario());
            seq = captured;
            return host;
        }

        [Fact]
        public void RecordThenReplay_ButtonStream_ReproducesIdenticalChecksums()
        {
            const int ticks = 10;
            // A DslEvent order: eventIndex 0 (buy), amount = 42 in TargetX, issued by Player1 at tick 3.
            var buyOrder = new UnitOrder(0, UnitCommand.DslEvent, Fixed.FromRaw(42), Fixed.Zero);

            string path = Path.GetTempFileName();
            try
            {
                // ── Record a v4 replay of the button press. ──
                using (var rec = NewRec(path))
                    rec.RecordTick(3, Faction.Player1, new[] { buyOrder }, 0, 1);

                // ── LIVE reference run: apply the identical order through the shared applier before StepOnce. ──
                SimulationHost live = NewHost(out List<(uint, uint)> liveSeq);
                for (uint t = 0; t < ticks; t++)
                {
                    if (t == 3)
                        OrderApplier.Apply(live.World, buyOrder, Faction.Player1, dslSink: live.DslEventSink);
                    live.StepOnce();
                }

                // ── REPLAY run: ReplayPlayer feeds the SAME order via OrderApplier → the director sink. ──
                SimulationHost rep = NewHost(out List<(uint, uint)> repSeq);
                var player = new ReplayPlayer(path, rep.World) { DslEventSink = rep.DslEventSink };
                for (uint t = 0; t < ticks; t++)
                {
                    player.Flush(t);
                    rep.StepOnce();
                }

                Assert.Equal(liveSeq, repSeq);          // byte-identical SimChecksum sequences
                Assert.Equal(42, rep.Vars.GetInt("score", 0)); // the replayed button actually fired the handler
                Assert.Equal(live.Vars.GetInt("score", 0), rep.Vars.GetInt("score", 0));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void V4Replay_PlaysRecordedMoveOrder()
        {
            // Record a v4 replay carrying a single ordinary Move order via the tagged MergedTickPacket body, then
            // load + replay it: the Move applies through the shared OrderApplier (parity with the live path).
            string path = Path.GetTempFileName();
            try
            {
                var move = new UnitOrder(0, UnitCommand.Move, Fixed.FromInt(5), Fixed.FromInt(7));
                using (var rec = NewRec(path))
                    rec.RecordTick(1, Faction.Player1, new[] { move }, 0, 1);

                var world = new EntityWorld();
                int u = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.One);
                Assert.Equal(0, u);
                var player = new ReplayPlayer(path, world);  // no throw — v4 is accepted
                Assert.Equal(EntityWorld.DEFAULT_RNG_SEED, player.Seed);
                player.Flush(1);                              // the Move order applies through the shared applier
                Assert.True((world.Flags[u] & EntityFlags.Moving) != 0);
            }
            finally { File.Delete(path); }
        }
    }
}
