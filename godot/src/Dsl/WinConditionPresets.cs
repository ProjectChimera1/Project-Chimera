#nullable enable
using ProjectChimera.Core;             // Fixed
using ProjectChimera.Core.Definitions; // WinConditionSpec, WinPresetKind

namespace ProjectChimera.Dsl
{
    /// <summary>
    /// Story 7.11 — pure, Godot-free factory that instantiates each T1 win-condition preset as canonical public-DSL
    /// graph-IR, reusing ONLY public registry nodes (the <c>victory</c>/<c>defeat</c> action kinds already exist at
    /// <c>NodeKinds.ActionTypes</c>, plus the public event/condition kinds) — no hidden engine-only opcode. This is
    /// the authored/serialized form used to PROVE the expressibility/serialization property (AC2): each preset's
    /// graph round-trips through the <see cref="TriggerGraph"/> schema byte-identically and compiles through the
    /// validator. The actual match outcome is evaluated NATIVELY by <c>WinConditionSystem</c> from the typed
    /// <see cref="WinConditionSpec"/> — the DSL at 7.11 has no primitive to designate a specific leader/structure
    /// instance or express KotH's exclusive-hold rule (that vocabulary is Story 7.13), so this template is an
    /// expressibility witness, not the executor.
    ///
    /// <para>Canonical node-id layout for every preset (mirrors the <c>TriggerGraphCanonicalTests</c> fixture):
    /// 0 = TriggerNode, 1 = EventNode, 2 = ConditionNode, 3 = victory/defeat ActionNode. Edges: event → trigger
    /// (exec), trigger → action (exec), condition → trigger (Boolean data). <c>Fixed</c>/<c>int</c> only; engine-free.</para>
    /// </summary>
    public static class WinConditionPresets
    {
        /// <summary>
        /// Build the canonical graph-IR for <paramref name="spec"/>'s preset. <see cref="WinPresetKind.None"/>
        /// (or a null spec) has no preset template and returns null (the built-in enum path is native only).
        /// </summary>
        public static TriggerGraph? Build(WinConditionSpec? spec)
        {
            if (spec is null || spec.Preset == WinPresetKind.None) return null;

            return spec.Preset switch
            {
                WinPresetKind.KingOfTheHill       => KingOfTheHill(spec),
                WinPresetKind.TimedSurvival       => TimedSurvival(spec),
                WinPresetKind.Assassination       => Assassination(spec),
                WinPresetKind.LandmarkDestruction => LandmarkDestruction(spec),
                _                                 => null,
            };
        }

        /// <summary>King of the Hill — a faction that holds the named region wins. Event: match_start; Condition:
        /// unit_in_region (RegionId = the held zone); Action: victory.
        /// <para>Story 7.13 expressibility gaps (documented, deliberate): the public DSL cannot yet express the
        /// EXCLUSIVE-hold rule (sole holder — unit_in_region only tests presence) nor the contiguous
        /// HOLD-DURATION (<c>spec.HoldTicks</c> has no public encoding). The native evaluator owns both.</para></summary>
        private static TriggerGraph KingOfTheHill(WinConditionSpec spec)
        {
            var trigger = new TriggerNode { Id = 0, Name = "King of the Hill" };
            var evt     = new EventNode { Id = 1, Kind = "match_start" };
            var cond    = new ConditionNode { Id = 2, Kind = "unit_in_region", RegionId = spec.RegionId };
            var action  = new ActionNode { Id = 3, Kind = "victory", Faction = 0 };
            return Assemble(trigger, evt, cond, action);
        }

        /// <summary>The timer name binding Timed Survival's two triggers (create_timer → timer_expires).</summary>
        private const string SurvivalTimerName = "survival";

        /// <summary>Timed Survival — the designated faction that outlasts the timer wins, as a GENUINE two-trigger
        /// graph (review P8; a lone nameless timer_expires bound to no timer would never fire and encoded no
        /// survive_ticks). Trigger A (ids 0-3): match_start → create_timer (TimerName
        /// <see cref="SurvivalTimerName"/>, timer_seconds = <c>spec.SurviveTicks</c>). Trigger B (ids 4-7):
        /// timer_expires (<see cref="SurvivalTimerName"/>) → victory (Faction = the designated slot).
        /// <para>Story 7.13 expressibility gaps (documented, deliberate): the public create_timer vocabulary is
        /// SECONDS-typed (<c>ActionNode.TimerSeconds</c>), so the tick count is carried as the numeric payload
        /// (an exact tick-duration encoding is 7.13 vocabulary); and the "designated faction ELIMINATED before the
        /// deadline loses" half has no public encoding. The native evaluator owns both.</para></summary>
        private static TriggerGraph TimedSurvival(WinConditionSpec spec)
        {
            var startTrig = new TriggerNode { Id = 0, Name = "Timed Survival — start clock" };
            var startEvt  = new EventNode { Id = 1, Kind = "match_start" };
            var startCond = new ConditionNode { Id = 2, Kind = "always" };
            var startAct  = new ActionNode { Id = 3, Kind = "create_timer", TimerName = SurvivalTimerName,
                                             TimerSeconds = Fixed.FromInt(spec.SurviveTicks) };
            var g = Assemble(startTrig, startEvt, startCond, startAct);

            var winTrig = new TriggerNode { Id = 4, Name = "Timed Survival" };
            var winEvt  = new EventNode { Id = 5, Kind = "timer_expires", TimerName = SurvivalTimerName };
            var winCond = new ConditionNode { Id = 6, Kind = "always" };
            var winAct  = new ActionNode { Id = 7, Kind = "victory", Faction = spec.FactionSlot };
            Append(g, winTrig, winEvt, winCond, winAct);
            return g;
        }

        /// <summary>Assassination — the death of the designated leader loses the match for its owner. Event:
        /// unit_dies; Condition: always; Action: defeat.
        /// <para>Story 7.13 expressibility gap (documented, deliberate): the public DSL cannot DESIGNATE a
        /// specific unit INSTANCE (<c>spec.LeaderUnitIndex</c> has no public encoding — unit_dies fires for any
        /// unit). The native evaluator owns the instance binding.</para></summary>
        private static TriggerGraph Assassination(WinConditionSpec spec)
        {
            var trigger = new TriggerNode { Id = 0, Name = "Assassination" };
            var evt     = new EventNode { Id = 1, Kind = "unit_dies" };
            var cond    = new ConditionNode { Id = 2, Kind = "always" };
            var action  = new ActionNode { Id = 3, Kind = "defeat", Faction = 0 };
            return Assemble(trigger, evt, cond, action);
        }

        /// <summary>Landmark Destruction — the destruction of the designated structure loses the match for its
        /// owner. Event: match_start; Condition: building_exists (the owning faction still holds it); Action:
        /// defeat.
        /// <para>Story 7.13 expressibility gap (documented, deliberate): same instance-designation gap as
        /// Assassination — <c>spec.StructureIndex</c> has no public encoding. The native evaluator owns it.</para></summary>
        private static TriggerGraph LandmarkDestruction(WinConditionSpec spec)
        {
            var trigger = new TriggerNode { Id = 0, Name = "Landmark Destruction" };
            var evt     = new EventNode { Id = 1, Kind = "match_start" };
            var cond    = new ConditionNode { Id = 2, Kind = "building_exists" };
            var action  = new ActionNode { Id = 3, Kind = "defeat", Faction = 0 };
            return Assemble(trigger, evt, cond, action);
        }

        /// <summary>Wire the four canonical nodes into a graph: event → trigger (exec), trigger → action (exec),
        /// condition → trigger (Boolean data) — the exact shape <see cref="TriggerGraph.FromFlat"/> emits.</summary>
        private static TriggerGraph Assemble(TriggerNode trigger, EventNode evt, ConditionNode cond, ActionNode action)
        {
            var g = new TriggerGraph();
            g.Nodes.Add(trigger);
            g.Nodes.Add(evt);
            g.Nodes.Add(cond);
            g.Nodes.Add(action);
            g.ExecEdges.Add(new ExecEdge(evt.Id, TriggerGraph.EventExecOutPort, trigger.Id, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(trigger.Id, TriggerGraph.TriggerExecOutPort, action.Id, TriggerGraph.ActionExecInPort));
            g.DataEdges.Add(new DataEdge(cond.Id, TriggerGraph.ConditionDataOutPort, trigger.Id, TriggerGraph.TriggerConditionInPort, DataWireType.Boolean));
            return g;
        }

        /// <summary>Append a SECOND canonical trigger chain (the same four-node wiring shape as
        /// <see cref="Assemble"/>) to an existing graph — the two-trigger TimedSurvival witness (review P8) is the
        /// only multi-trigger preset. Node ids must already be unique across the whole graph (caller-owned
        /// canonical layout: 0-3 first trigger, 4-7 second).</summary>
        private static void Append(TriggerGraph g, TriggerNode trigger, EventNode evt, ConditionNode cond, ActionNode action)
        {
            g.Nodes.Add(trigger);
            g.Nodes.Add(evt);
            g.Nodes.Add(cond);
            g.Nodes.Add(action);
            g.ExecEdges.Add(new ExecEdge(evt.Id, TriggerGraph.EventExecOutPort, trigger.Id, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(trigger.Id, TriggerGraph.TriggerExecOutPort, action.Id, TriggerGraph.ActionExecInPort));
            g.DataEdges.Add(new DataEdge(cond.Id, TriggerGraph.ConditionDataOutPort, trigger.Id, TriggerGraph.TriggerConditionInPort, DataWireType.Boolean));
        }
    }
}
