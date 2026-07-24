#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Dsl;
using ProjectChimera.Multiplayer;
using Xunit;

namespace ProjectChimera.Sim.Tests.Multiplayer
{
    /// <summary>
    /// Story 7.13 (Arm D) — the headless covering test for the <c>player_chat</c> replicated, tick-stamped rail.
    /// player_chat rides the EXISTING 11-byte <see cref="UnitCommand.DslEvent"/> order (the Story 7.9 button wire):
    /// <see cref="EventBounds.PlayerChatRailCode"/> in the order's UnitId, the bounded chat code in TargetX. So
    /// recording + replaying a chat-code injection through the SHARED <c>OrderApplier</c> (wired to the director's DSL
    /// sink) reproduces the identical tick-by-tick SimChecksum a live apply produces — and the subscribed player_chat
    /// trigger fires on the same tick in both. <see cref="ReplayRecorder.VERSION"/> STAYS 3 (no wire/stride change).
    /// This satisfies the Matrix <c>player_chat</c> row; the two-client same-tick claim is a MANUAL godot-verify check
    /// (see the spec resolution protocol), not a headless row.
    /// </summary>
    public class ReplayPlayerChatTests
    {
        private const int ChatCode = 7; // a bounded code in [0, EventBounds.MaxChatCode)

        private static ScenarioVariable IntVar(string name) =>
            new() { Name = name, Type = DslValueType.Int, Scope = VarScope.Global, Initial = Fixed.Zero };

        /// <summary>
        /// A single trigger subscribed to <c>player_chat</c> for sender faction slot 0 (Player1) whose action writes
        /// <c>score = event.code</c> — hand-built (the shared BuildCustomEventTrigger helper only threads the param map
        /// for unit_dies; the REAL LoadScenario compile derives player_chat's {sender,code} map via
        /// <c>EventDispatchPlan.BuiltinParamMapOf</c>, so <c>event.code</c> resolves).
        /// </summary>
        private static ScenarioData ChatScenario()
        {
            ScenarioVariable[] vars = { IntVar("score") };

            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "H", Enabled = true });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "player_chat", Faction = 0 }); // sender slot 0
            g.Nodes.Add(new ExprEventParamNode { Id = 2, Name = "code" });
            g.Nodes.Add(new ActionNode { Id = 3, Kind = "set_variable", Variable = "score", Faction = 0 });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 3, TriggerGraph.ActionExecInPort));
            g.DataEdges.Add(new DataEdge(2, TriggerGraph.ExprDataOutPort, 3, TriggerGraph.ActionValueInPort, DataWireType.Int));

            return new ScenarioData { Variables = vars, TriggerGraphJson = g.ToCanonicalJson() };
        }

        private static SimulationHost NewHost(out List<(uint, uint)> seq)
        {
            var host = SimulationHost.Create(new NullLogSink(), new FactionRegistry(2));
            host.ChecksumInterval = 1;
            var captured = new List<(uint, uint)>();
            host.SetChecksumSink((t, h) => captured.Add((t, h)));
            host.ScenarioDirector.LoadScenario(ChatScenario());
            seq = captured;
            return host;
        }

        [Fact]
        public void RecordThenReplay_PlayerChat_ReproducesIdenticalChecksums()
        {
            const int ticks = 10;
            // A player_chat DslEvent order: UnitId = the reserved rail sentinel, TargetX = the chat code, issued by
            // Player1 at tick 3. This is byte-identical to what LockstepManager.SendPlayerChat buffers online.
            var chatOrder = new UnitOrder(EventBounds.PlayerChatRailCode, UnitCommand.DslEvent,
                Fixed.FromRaw(ChatCode), Fixed.Zero);

            string path = Path.GetTempFileName();
            try
            {
                // ── Record a v4 replay of the chat-code injection. ──
                using (var rec = new ReplayRecorder(path, "test://chat", EntityWorld.DEFAULT_RNG_SEED,
                           0x11UL, 0x22UL, CanonicalModelHash.AlgoVersion, new[] { Faction.Player1, Faction.Player2 }))
                    rec.RecordTick(3, Faction.Player1, new[] { chatOrder }, 0, 1);

                // ── LIVE reference run: apply the identical order through the shared applier before StepOnce. ──
                SimulationHost live = NewHost(out List<(uint, uint)> liveSeq);
                for (uint t = 0; t < ticks; t++)
                {
                    if (t == 3)
                        OrderApplier.Apply(live.World, chatOrder, Faction.Player1, dslSink: live.DslEventSink);
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

                Assert.Equal(liveSeq, repSeq);                       // byte-identical SimChecksum sequences
                Assert.Equal(ChatCode, rep.Vars.GetInt("score", 0)); // the replayed chat actually fired the handler w/ the code
                Assert.Equal(live.Vars.GetInt("score", 0), rep.Vars.GetInt("score", 0));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void PlayerChat_OutOfRangeCode_IsDeterministicNoOp()
        {
            SimulationHost host = NewHost(out _);
            // A code at the ceiling is out of [0, MaxChatCode) → deterministic drop (returns false, no enqueue).
            Assert.False(host.DslEventSink(EventBounds.PlayerChatRailCode, 0, EventBounds.MaxChatCode, 0));
            host.StepOnce();
            Assert.Equal(0, host.Vars.GetInt("score", 0)); // handler never fired
        }

        [Fact]
        public void PlayerChat_SystemSender_IsDeterministicNoOp()
        {
            SimulationHost host = NewHost(out _);
            // A -1 (system / non-player) sender is not a real player slot → deterministic drop.
            Assert.False(host.DslEventSink(EventBounds.PlayerChatRailCode, -1, ChatCode, 0));
            host.StepOnce();
            Assert.Equal(0, host.Vars.GetInt("score", 0)); // handler never fired
        }
    }
}
