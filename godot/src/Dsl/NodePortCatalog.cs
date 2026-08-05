#nullable enable
using System.Collections.Generic;

namespace ProjectChimera.Dsl
{
    /// <summary>
    /// One rendered graph-editor port of a node: its IR port index, exec-vs-data space, display label, and (data
    /// only) default wire type. Pure IR-side data — the T3 view maps these onto GraphNode slots; no Godot type
    /// ever appears here (the panel depends on the IR, never the reverse).
    /// </summary>
    public readonly struct GraphPortSpec
    {
        /// <summary>True for a data port; false for an exec (control) port. Exec and data ports share a numeric
        /// space and are disambiguated by this flag (the <see cref="NodePorts"/> convention).</summary>
        public readonly bool IsData;

        /// <summary>The IR port index (a <see cref="TriggerGraph"/> named-port constant).</summary>
        public readonly int Port;

        /// <summary>The short on-node label the view renders beside the pin.</summary>
        public readonly string Label;

        /// <summary>The default wire type used to color a data port before an edge stamps its own (data only).</summary>
        public readonly DataWireType Wire;

        public GraphPortSpec(bool isData, int port, string label, DataWireType wire = DataWireType.Boolean)
        {
            IsData = isData;
            Port = port;
            Label = label;
            Wire = wire;
        }
    }

    /// <summary>
    /// DW-195 (with DW-179) — the Godot-free T3 PORT-LAYOUT seam: the input/output ports the visual graph editor
    /// renders per node kind, extracted from <c>DslGraphEditorPanel.PortsOf</c> so it is Tier-1-testable and can
    /// no longer silently drop a kind. Every kind <see cref="NodePaletteFactory"/> exposes MUST have a case here —
    /// the pre-existing hole this seam closes is the dedicated-leaf classes (order_units / move_camera /
    /// cinematic_mode / play_vfx / random_choice / enable_trigger / disable_trigger / run_trigger and the 7.14
    /// show/complete/fail_objective leaves), which fell through the panel's switch to ZERO ports, making a
    /// palette-dragged leaf unwireable.
    ///
    /// <para>Consistency contract (Tier-1-pinned): the EXEC ports emitted here are EXACTLY the ports
    /// <see cref="NodePorts"/> declares legal (per-kind parity), and every DATA port emitted is legal per
    /// <see cref="NodePorts"/>. Two sanctioned data-side curations render FEWER pins than the legality table
    /// admits, both because the narrower rule is enforced downstream and is worth surfacing while wiring:
    /// <list type="bullet">
    /// <item>an <see cref="ActionNode"/>'s index-in port renders only for <c>array_set</c> (the only action kind
    /// that reads it), although the legality table admits it on every action;</item>
    /// <item>DW-578 — an <see cref="ExprCallNode"/>'s operand ports render only up to the built-in's arity
    /// (<see cref="NodeKinds.ExprCallArity"/>), although the legality table admits ports 0/1 on every call.</item>
    /// </list></para>
    /// </summary>
    public static class NodePortCatalog
    {
        /// <summary>Fill <paramref name="inputs"/>/<paramref name="outputs"/> with the ports the T3 view renders
        /// for <paramref name="n"/> (inputs = left slots in list order, outputs = right slots). The lists are
        /// cleared first. An unknown node type yields no ports (unreachable for the closed registry).</summary>
        public static void PortsOf(NodeBase n, List<GraphPortSpec> inputs, List<GraphPortSpec> outputs)
        {
            inputs.Clear();
            outputs.Clear();
            switch (n)
            {
                case TriggerNode:
                    inputs.Add(new GraphPortSpec(false, TriggerGraph.TriggerEventInPort, "event"));
                    inputs.Add(new GraphPortSpec(true, TriggerGraph.TriggerConditionInPort, "cond", DataWireType.Boolean));
                    outputs.Add(new GraphPortSpec(false, TriggerGraph.TriggerExecOutPort, "then"));
                    break;
                case EventNode:
                    outputs.Add(new GraphPortSpec(false, TriggerGraph.EventExecOutPort, "fire"));
                    break;
                case ConditionNode:
                    outputs.Add(new GraphPortSpec(true, TriggerGraph.ConditionDataOutPort, "bool", DataWireType.Boolean));
                    break;
                case ActionNode a:
                    inputs.Add(new GraphPortSpec(false, TriggerGraph.ActionExecInPort, "in"));
                    inputs.Add(new GraphPortSpec(true, TriggerGraph.ActionValueInPort, "val", DataWireType.Int));
                    if (a.Kind == "array_set")
                        inputs.Add(new GraphPortSpec(true, TriggerGraph.ActionIndexInPort, "idx", DataWireType.Int));
                    outputs.Add(new GraphPortSpec(false, TriggerGraph.ActionExecOutPort, "out"));
                    break;
                case EffectActionNode:
                    inputs.Add(new GraphPortSpec(false, TriggerGraph.ActionExecInPort, "in"));
                    outputs.Add(new GraphPortSpec(false, TriggerGraph.ActionExecOutPort, "out"));
                    break;
                case RaiseEventNode:
                    inputs.Add(new GraphPortSpec(false, TriggerGraph.ActionExecInPort, "in"));
                    inputs.Add(new GraphPortSpec(true, TriggerGraph.RaiseArgInPort0, "arg0", DataWireType.Int));
                    inputs.Add(new GraphPortSpec(true, TriggerGraph.RaiseArgInPort1, "arg1", DataWireType.Int));
                    inputs.Add(new GraphPortSpec(true, TriggerGraph.RaiseArgInPort2, "arg2", DataWireType.Int));
                    inputs.Add(new GraphPortSpec(true, TriggerGraph.RaiseArgInPort3, "arg3", DataWireType.Int));
                    outputs.Add(new GraphPortSpec(false, TriggerGraph.ActionExecOutPort, "out"));
                    break;
                case ForEachNode:
                case ForEachBatchedNode:
                    inputs.Add(new GraphPortSpec(false, TriggerGraph.ActionExecInPort, "in"));
                    outputs.Add(new GraphPortSpec(false, TriggerGraph.ActionExecOutPort, "next"));
                    outputs.Add(new GraphPortSpec(false, TriggerGraph.ForEachBodyOutPort, "body"));
                    break;
                case BranchNode:
                    inputs.Add(new GraphPortSpec(false, TriggerGraph.ActionExecInPort, "in"));
                    inputs.Add(new GraphPortSpec(true, TriggerGraph.BranchCondInPort, "cond", DataWireType.Boolean));
                    outputs.Add(new GraphPortSpec(false, TriggerGraph.ActionExecOutPort, "next"));
                    outputs.Add(new GraphPortSpec(false, TriggerGraph.BranchThenOutPort, "then"));
                    outputs.Add(new GraphPortSpec(false, TriggerGraph.BranchElseOutPort, "else"));
                    break;
                case ExprUnaryNode:
                case ExprArrayGetNode:
                    inputs.Add(new GraphPortSpec(true, TriggerGraph.ExprOperandPort0, "a", DataWireType.Int));
                    outputs.Add(new GraphPortSpec(true, TriggerGraph.ExprDataOutPort, "out", DataWireType.Int));
                    break;
                case ExprBinaryNode:
                    inputs.Add(new GraphPortSpec(true, TriggerGraph.ExprOperandPort0, "a", DataWireType.Int));
                    inputs.Add(new GraphPortSpec(true, TriggerGraph.ExprOperandPort1, "b", DataWireType.Int));
                    outputs.Add(new GraphPortSpec(true, TriggerGraph.ExprDataOutPort, "out", DataWireType.Int));
                    break;

                // ── DW-578 — expr_call renders EXACTLY the operand pins its built-in's ARITY admits
                //    (NodeKinds.ExprCallArity — the same table ExprCompiler gates operand edges against), so the
                //    arity rule is visible while wiring instead of only at compile time via the located badge: a
                //    zero-arity state read (region_unit_count) draws NO operand pin, and a one-arity read draws
                //    only "a". NodePorts legality is deliberately UNCHANGED (ports 0/1 stay legal for the kind) —
                //    this is a rendered-port curation, exactly like the array_set-only index-in pin above. ──
                case ExprCallNode ec:
                {
                    int operands = NodeKinds.ExprCallArity(ec.Fn);
                    // An unknown fn is unreachable (parse AND the inspector both membership-check ExprCallFns);
                    // fall back to the full legal pair rather than hiding an already-drawn wire.
                    if (operands < 0) operands = 2;
                    // Legality clamp: NodePorts admits operand ports 0/1 only — never render past the table.
                    if (operands > 2) operands = 2;
                    if (operands > 0)
                        inputs.Add(new GraphPortSpec(true, TriggerGraph.ExprOperandPort0, "a", DataWireType.Int));
                    if (operands > 1)
                        inputs.Add(new GraphPortSpec(true, TriggerGraph.ExprOperandPort1, "b", DataWireType.Int));
                    outputs.Add(new GraphPortSpec(true, TriggerGraph.ExprDataOutPort, "out", DataWireType.Int));
                    break;
                }
                case ExprLiteralNode:
                case ExprVarNode:
                case ExprArrayLenNode:
                case ExprEventParamNode:
                    outputs.Add(new GraphPortSpec(true, TriggerGraph.ExprDataOutPort, "out", DataWireType.Int));
                    break;

                // ── DW-195 — the dedicated action-leaf kinds (Story 7.13 + 7.14): exec-in/exec-out exactly like
                //    an action (the NodePorts contract), so a palette-dragged leaf is wireable into a chain. ──
                case OrderUnitsNode:
                case MoveCameraNode:
                case CinematicModeNode:
                case PlayVfxNode:
                case EnableTriggerNode:
                case DisableTriggerNode:
                case RunTriggerNode:
                case ShowObjectiveNode:
                case CompleteObjectiveNode:
                case FailObjectiveNode:
                    inputs.Add(new GraphPortSpec(false, TriggerGraph.ActionExecInPort, "in"));
                    outputs.Add(new GraphPortSpec(false, TriggerGraph.ActionExecOutPort, "out"));
                    break;

                // ── DW-195 — random_choice: continuation on port 0 plus ONE branch port per authored weight
                //    (RandomChoiceBranchOutPort0 + k), mirroring NodePorts.IsExecOut's weight-derived range. ──
                case RandomChoiceNode rc:
                    inputs.Add(new GraphPortSpec(false, TriggerGraph.ActionExecInPort, "in"));
                    outputs.Add(new GraphPortSpec(false, TriggerGraph.ActionExecOutPort, "next"));
                    for (int k = 0; k < rc.Weights.Length; k++)
                        outputs.Add(new GraphPortSpec(
                            false, TriggerGraph.RandomChoiceBranchOutPort0 + k, $"case {k} (w{rc.Weights[k]})"));
                    break;
            }
        }
    }
}
