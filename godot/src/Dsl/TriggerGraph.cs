#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProjectChimera.Core.Definitions;   // TriggerDefinition, TriggerEvent, TriggerCondition, TriggerAction

namespace ProjectChimera.Dsl
{
    /// <summary>
    /// Story 7.2 — the graph-canonical trigger IR container: an id-keyed <see cref="Nodes"/> list plus two sparse
    /// typed edge-lists (<see cref="ExecEdges"/> control flow, <see cref="DataEdges"/> dataflow). All four authoring
    /// tiers (T1/T2/T3/T4) share this ONE representation.
    ///
    /// <see cref="FromFlat"/>/<see cref="ToFlat"/> are an EXACT round-trip identity for the legacy flat
    /// <c>TriggerDefinition[]</c> (all field values AND array/intra-trigger order): the "T2 sentence list is a
    /// linear projection of an exec-edge chain." Node ids are assigned deterministically from flat array order;
    /// graph→flat reconstruction is driven by ascending node id + edge topology, reproducing the original order.
    /// <see cref="ToCanonicalJson"/> emits nodes sorted by ascending id and each edge list sorted by
    /// <c>(Src,SrcPort,Dst,DstPort)</c>, so two structurally-equal graphs serialize byte-identically.
    /// </summary>
    public sealed class TriggerGraph
    {
        // ── Named ports (the fixed pin layout the flat↔graph mapping wires) ──────
        /// <summary>TriggerNode: the event-in port EventNodes fire into (exec edge Dst).</summary>
        public const int TriggerEventInPort     = 0;
        /// <summary>TriggerNode: the condition-in port ConditionNodes gate through (data edge Dst).</summary>
        public const int TriggerConditionInPort = 1;
        /// <summary>TriggerNode: the exec-out port that starts the action chain (exec edge Src).</summary>
        public const int TriggerExecOutPort     = 0;
        /// <summary>ActionNode: the exec-in port (exec edge Dst).</summary>
        public const int ActionExecInPort       = 0;
        /// <summary>ActionNode: the exec-out port that continues the chain (exec edge Src).</summary>
        public const int ActionExecOutPort      = 0;
        /// <summary>EventNode: the exec-out port that fires the trigger (exec edge Src).</summary>
        public const int EventExecOutPort       = 0;
        /// <summary>ConditionNode: the Boolean data-out port that gates the trigger (data edge Src).</summary>
        public const int ConditionDataOutPort   = 0;

        public List<NodeBase> Nodes     { get; } = new();
        public List<ExecEdge> ExecEdges { get; } = new();
        public List<DataEdge> DataEdges { get; } = new();

        /// <summary>
        /// Migrate the flat <see cref="TriggerDefinition"/>[] into the graph IR. Ids are assigned by a single
        /// ascending counter walking triggers in array order; per trigger it emits the TriggerNode, then its
        /// EventNodes, then ConditionNodes, then ActionNodes (e.g. two 1-event/1-cond/2-action triggers →
        /// T0=0,E=1,C=2,A0=3,A1=4 ; T1=5,E=6,C=7,A0=8,A1=9). Wires: EventNode --exec--&gt; Trigger (event-in);
        /// Trigger --exec--&gt; Action0 --exec--&gt; … (the linear action chain); ConditionNode --data(Boolean)--&gt;
        /// Trigger (condition-in gate). Field values are copied verbatim, so <see cref="ToFlat"/> is exact.
        /// </summary>
        public static TriggerGraph FromFlat(TriggerDefinition[] triggers)
        {
            var graph = new TriggerGraph();
            int nextId = 0;

            foreach (TriggerDefinition t in triggers)
            {
                int triggerId = nextId++;
                graph.Nodes.Add(new TriggerNode
                {
                    Id              = triggerId,
                    Name            = t.Name,
                    Enabled         = t.Enabled,
                    RunOnce         = t.RunOnce,
                    CooldownSeconds = t.CooldownSeconds,
                    Priority        = t.Priority,
                });

                foreach (TriggerEvent e in t.Events)
                {
                    int eid = nextId++;
                    graph.Nodes.Add(new EventNode
                    {
                        Id           = eid,
                        Kind         = e.Type,
                        Faction      = e.Faction,
                        BuildingType = e.BuildingType,
                        TimerName    = e.TimerName,
                        Amount       = e.Amount,
                        Count        = e.Count,
                        Operator     = e.Operator,
                    });
                    graph.ExecEdges.Add(new ExecEdge(eid, EventExecOutPort, triggerId, TriggerEventInPort));
                }

                foreach (TriggerCondition c in t.Conditions)
                {
                    int cid = nextId++;
                    graph.Nodes.Add(new ConditionNode
                    {
                        Id           = cid,
                        Kind         = c.Type,
                        Faction      = c.Faction,
                        BuildingType = c.BuildingType,
                        Amount       = c.Amount,
                        Count        = c.Count,
                        Variable     = c.Variable,
                        RegionId     = c.RegionId,
                        Value        = c.Value,
                        Operator     = c.Operator,
                    });
                    graph.DataEdges.Add(new DataEdge(cid, ConditionDataOutPort, triggerId, TriggerConditionInPort, DataWireType.Boolean));
                }

                // The linear action chain: Trigger(ExecOut) → Action0(ExecIn), Action0(ExecOut) → Action1(ExecIn), …
                int prevId   = triggerId;
                int prevPort = TriggerExecOutPort;
                foreach (TriggerAction a in t.Actions)
                {
                    int aid = nextId++;
                    graph.Nodes.Add(new ActionNode
                    {
                        Id           = aid,
                        Kind         = a.Type,
                        UnitId       = a.UnitId,
                        Faction      = a.Faction,
                        X            = a.X,
                        Z            = a.Z,
                        Count        = a.Count,
                        Text         = a.Text,
                        Duration     = a.Duration,
                        TimerName    = a.TimerName,
                        TimerSeconds = a.TimerSeconds,
                        Amount       = a.Amount,
                        Value        = a.Value,
                        Variable     = a.Variable,
                        SoundId      = a.SoundId,
                    });
                    graph.ExecEdges.Add(new ExecEdge(prevId, prevPort, aid, ActionExecInPort));
                    prevId   = aid;
                    prevPort = ActionExecOutPort;
                }
            }

            return graph;
        }

        /// <summary>
        /// Lower the graph back to the flat <see cref="TriggerDefinition"/>[]. TriggerNodes are ordered by ascending
        /// <see cref="NodeBase.Id"/> → array order. Per trigger: events = EventNodes with an exec edge into its
        /// event-in port (ascending id); conditions = ConditionNodes with a data edge into its condition-in port
        /// (ascending id); actions = follow the exec chain out of it. On a graph produced by <see cref="FromFlat"/>
        /// this reproduces the original array exactly (the losslessness core).
        /// </summary>
        public TriggerDefinition[] ToFlat()
        {
            // Keyed lookup by id (indexer access only — never enumerated, so deterministic order is preserved).
            var byId = new Dictionary<int, NodeBase>(Nodes.Count);
            foreach (NodeBase n in Nodes)
                byId[n.Id] = n;

            List<TriggerNode> triggerNodes = Nodes.OfType<TriggerNode>().OrderBy(n => n.Id).ToList();
            var result = new List<TriggerDefinition>(triggerNodes.Count);

            // Review patch (Story 7.2): sort the exec edges ONCE (the tuple order is loop-invariant), not per
            // action hop. Deterministic first-match on this stable list drives every chain walk below.
            List<ExecEdge> sortedExec = ExecEdges.OrderBy(x => x).ToList();

            foreach (TriggerNode tn in triggerNodes)
            {
                var def = new TriggerDefinition
                {
                    Name            = tn.Name,
                    Enabled         = tn.Enabled,
                    RunOnce         = tn.RunOnce,
                    CooldownSeconds = tn.CooldownSeconds,
                    Priority        = tn.Priority,
                };

                // Events: exec edges into the trigger's event-in port, source EventNodes ascending by id.
                def.Events = ExecEdges
                    .Where(e => e.Dst == tn.Id && e.DstPort == TriggerEventInPort && byId.ContainsKey(e.Src))
                    .Select(e => byId[e.Src])
                    .OfType<EventNode>()
                    .OrderBy(n => n.Id)
                    .Select(n => new TriggerEvent
                    {
                        Type         = n.Kind,
                        Faction      = n.Faction,
                        BuildingType = n.BuildingType,
                        TimerName    = n.TimerName,
                        Amount       = n.Amount,
                        Count        = n.Count,
                        Operator     = n.Operator,
                    })
                    .ToArray();

                // Conditions: data edges into the trigger's condition-in port, source ConditionNodes ascending by id.
                def.Conditions = DataEdges
                    .Where(e => e.Dst == tn.Id && e.DstPort == TriggerConditionInPort && byId.ContainsKey(e.Src))
                    .Select(e => byId[e.Src])
                    .OfType<ConditionNode>()
                    .OrderBy(n => n.Id)
                    .Select(n => new TriggerCondition
                    {
                        Type         = n.Kind,
                        Faction      = n.Faction,
                        BuildingType = n.BuildingType,
                        Amount       = n.Amount,
                        Count        = n.Count,
                        Variable     = n.Variable,
                        RegionId     = n.RegionId,
                        Value        = n.Value,
                        Operator     = n.Operator,
                    })
                    .ToArray();

                // Actions: walk the exec chain out of the trigger (Trigger → Action0 → … → Action_n).
                var actions = new List<TriggerAction>();
                int currentId   = tn.Id;
                int currentPort = TriggerExecOutPort;
                // Review patch (Story 7.2): fail-closed cycle guard. FromFlat never emits a cyclic action chain, but
                // ToFlat/FromJson are public and later stories walk authored graphs — a hand-built/JSON cycle
                // (A→B→A or self-loop) would otherwise spin this `while (true)` unbounded (hang/OOM). Track the exec
                // nodes already visited on THIS chain (seeded with the trigger head) and reject a revisit with a
                // located error, consistent with the module's fail-closed posture (never a silent hang).
                var visited = new HashSet<int> { currentId };
                while (true)
                {
                    ActionNode? next = null;
                    foreach (ExecEdge e in sortedExec)   // deterministic: exactly one match in a FromFlat graph
                    {
                        if (e.Src == currentId && e.SrcPort == currentPort
                            && byId.TryGetValue(e.Dst, out NodeBase? nb) && nb is ActionNode an)
                        {
                            next = an;
                            break;
                        }
                    }
                    if (next is null) break;
                    if (!visited.Add(next.Id))
                        throw new JsonException(
                            $"exec chain cycle at node {next.Id} (trigger '{tn.Name}', id {tn.Id}): the action chain must be acyclic.");
                    actions.Add(new TriggerAction
                    {
                        Type         = next.Kind,
                        UnitId       = next.UnitId,
                        Faction      = next.Faction,
                        X            = next.X,
                        Z            = next.Z,
                        Count        = next.Count,
                        Text         = next.Text,
                        Duration     = next.Duration,
                        TimerName    = next.TimerName,
                        TimerSeconds = next.TimerSeconds,
                        Amount       = next.Amount,
                        Value        = next.Value,
                        Variable     = next.Variable,
                        SoundId      = next.SoundId,
                    });
                    currentId   = next.Id;
                    currentPort = ActionExecOutPort;
                }
                def.Actions = actions.ToArray();

                result.Add(def);
            }

            return result.ToArray();
        }

        /// <summary>
        /// Canonical serialization: nodes emitted sorted by ascending id; exec/data edges each sorted by
        /// <c>(Src,SrcPort,Dst,DstPort)</c>. Uses <see cref="DslJson.Options"/> (the closed-registry
        /// <see cref="NodeBaseJsonConverter"/> + <c>FixedJsonConverter</c>). Two structurally-equal graphs (nodes/
        /// edges added out of order) serialize byte-identically.
        /// </summary>
        public string ToCanonicalJson()
        {
            var shape = new GraphJsonShape
            {
                Nodes     = Nodes.OrderBy(n => n.Id).ToList(),
                ExecEdges = ExecEdges.OrderBy(e => e).ToList(),
                DataEdges = DataEdges.OrderBy(e => e).ToList(),
            };
            return JsonSerializer.Serialize(shape, DslJson.Options);
        }

        /// <summary>Deserialize a graph from canonical (or any) JSON via <see cref="DslJson.Options"/>.</summary>
        public static TriggerGraph FromJson(string json)
        {
            GraphJsonShape shape = JsonSerializer.Deserialize<GraphJsonShape>(json, DslJson.Options)
                ?? throw new JsonException("Graph JSON deserialized to null.");
            var graph = new TriggerGraph();
            if (shape.Nodes is not null)     graph.Nodes.AddRange(shape.Nodes);
            if (shape.ExecEdges is not null) graph.ExecEdges.AddRange(shape.ExecEdges);
            if (shape.DataEdges is not null) graph.DataEdges.AddRange(shape.DataEdges);
            return graph;
        }

        /// <summary>The on-the-wire JSON shape: the three canonical lists. Nodes (de)serialize through the registered
        /// <see cref="NodeBaseJsonConverter"/>; edges through their JSON-property layout.</summary>
        private sealed class GraphJsonShape
        {
            [JsonPropertyName("nodes")]      public List<NodeBase> Nodes     { get; set; } = new();
            [JsonPropertyName("exec_edges")] public List<ExecEdge> ExecEdges { get; set; } = new();
            [JsonPropertyName("data_edges")] public List<DataEdge> DataEdges { get; set; } = new();
        }
    }
}
