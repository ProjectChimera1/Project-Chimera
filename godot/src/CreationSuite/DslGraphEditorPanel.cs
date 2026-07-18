#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using ProjectChimera.Core.Definitions;   // ScenarioData, ScenarioVariable
using ProjectChimera.Dsl;                 // TriggerGraph, NodeBase, GraphStructureGate, NodeEditorAnnotation, DataWireColorPalette
using ProjectChimera.UI;                  // GameState, GameMode
using ProjectChimera.UI.Components;        // ChimeraComponents, ChimeraValidationBadge, ChimeraTooltip
using ProjectChimera.UI.Theme;             // ThemeTokens, ThemeBuilder, AccentController
using GodotTheme = Godot.Theme;

namespace ProjectChimera.CreationSuite
{
    /// <summary>
    /// Story 7.10 — the T3 visual node-graph editor: a GraphEdit-based, REPLACEABLE VIEW over the shared graph IR
    /// (no GraphEdit/Godot type ever enters <c>src/Dsl/**</c>; the panel depends on the IR, never the reverse).
    /// Modelled on <see cref="TechTreePanel"/> (the codebase's GraphEdit precedent: kit bootstrap, drag-to-wire
    /// validate-before-<c>ConnectNode</c>, the AddValid*DisconnectType workaround).
    ///
    /// <para>Two DISJOINT rendered graphs (Design Notes): the EDITABLE graph channel
    /// (<c>FromJson(TriggerGraphJson)</c>, prefix "g") and the READ-ONLY flat channel (<c>FromFlat(Triggers)</c>,
    /// prefix "f", auto-laid-out, dimmed). Save re-canonicalizes ONLY the graph channel back to
    /// <c>TriggerGraphJson</c>; <c>Triggers[]</c> is never rewritten, so the T2↔T3 round-trip preserves the IR by
    /// persistent-node-id equality with zero content migration. Node canvas positions persist verbatim in each
    /// node's hash-excluded <c>_editor</c> bag via <see cref="NodeEditorAnnotation"/>. Data-wire color =
    /// <see cref="DataWireType"/> (<see cref="DataWireColorPalette"/>); load-time structural errors route onto the
    /// offending node via <see cref="GraphStructureGate.CheckGraphLocated"/> + <see cref="ChimeraValidationBadge"/>.</para>
    /// </summary>
    public partial class DslGraphEditorPanel : Node
    {
        private const float MARGIN     = 24f;
        private const float COL_SPACING = 260f;
        private const float ROW_SPACING = 130f;
        private const int   EXEC_TYPE  = 0;   // GraphEdit port type for exec (control) ports
        private const int   DATA_TYPE  = 1;   // GraphEdit port type for data ports (same-type-connect keeps exec/data apart)

        private static readonly Color ExecColor   = Color.FromHtml(DataWireColorPalette.ExecHex);
        private static readonly Color DataInColor = new(0.60f, 0.60f, 0.60f);
        private static readonly Color Transparent = new(0f, 0f, 0f, 0f);

        // ── Deps ──
        private ScenarioData? _scenario;
        private GameState?    _gameState;

        // ── Kit context ──
        private GodotTheme        _theme = null!;
        private AccentController? _accent;

        // ── Shell ──
        private CanvasLayer    _canvas      = null!;
        private PanelContainer _panel       = null!;
        private GraphEdit      _graph       = null!;
        private Label          _statusLabel = null!;
        private VBoxContainer  _varsList    = null!;
        private OptionButton   _palette     = null!;

        // ── Live editable model (the graph channel) ──
        private TriggerGraph _editGraph = new();

        // The scenario TriggerGraphJson last parsed into _editGraph. ReloadModel re-parses ONLY when the scenario's
        // stored JSON differs from this (an external T2 / raw-IR edit), so a hide/show never discards unsaved
        // topology/position edits. Updated on both load (ReloadModel) and save (Save).
        private string? _lastLoadedJson;

        // ── Per-render port maps (keyed by GraphNode Name) ──
        private readonly Dictionary<string, List<PortDef>> _inByOrdinal  = new();
        private readonly Dictionary<string, List<PortDef>> _outByOrdinal = new();
        private readonly Dictionary<string, Dictionary<(bool IsData, int IrPort), int>> _inOrdinalOf  = new();
        private readonly Dictionary<string, Dictionary<(bool IsData, int IrPort), int>> _outOrdinalOf = new();
        private readonly Dictionary<string, Dictionary<(bool IsData, int IrPort), int>> _outSlotOf    = new();

        /// <summary>A single GraphNode port: its IR port index, exec-vs-data space, wire type (data only), label.</summary>
        private readonly struct PortDef
        {
            public readonly bool IsData;
            public readonly int IrPort;
            public readonly DataWireType Wire;
            public readonly string Label;
            public PortDef(bool isData, int irPort, string label, DataWireType wire = DataWireType.Boolean)
            { IsData = isData; IrPort = irPort; Label = label; Wire = wire; }
        }

        // ── Palette kinds — EXACTLY the closed NodeKinds union (the spec's Never-clause contract), served by the
        //    Godot-free NodePaletteFactory seam (default construction is Tier-1-tested to round-trip per kind). ──
        private static readonly IReadOnlyList<string> PaletteKinds = NodePaletteFactory.PaletteKinds;

        // ── Lifecycle ──

        public override void _Ready()
        {
            EnsureKitInitialized();   // MUST precede any ChimeraComponents.* call
            BuildUi();
        }

        /// <summary>Bind the panel to the live scenario + game state. Called by <c>DslGraphEditorPhase</c> after
        /// AddChild. Starts hidden; shown by the T3 hotkey in Edit mode.</summary>
        public void Initialize(ScenarioData? scenario, GameState gameState)
        {
            _scenario  = scenario;
            _gameState = gameState;
            _gameState.ModeChanged += OnModeChanged;
            _panel.Visible = false;
        }

        /// <summary>Re-bind on scenario reload (Import / scene restart), mirroring TriggerEditorPanel.SetScenario.
        /// Drops the previous scenario's in-memory model and clears the reload guard, so the panel can never
        /// render — or Save — a stale graph into the newly bound scenario.</summary>
        public void SetScenario(ScenarioData? scenario)
        {
            _scenario = scenario;
            _lastLoadedJson = null;
            _editGraph = new TriggerGraph();
            if (_panel.Visible) { ReloadModel(); RebuildGraph(); }
        }

        /// <summary>Toggle visibility (T3 hotkey, Edit mode). On open: reload (unchanged JSON keeps unsaved edits)
        /// + rebuild. On hide: capture live canvas positions into the model first, so a pure drag survives a
        /// hide/show like topology edits do.</summary>
        public void Toggle()
        {
            if (_panel.Visible) { Close(); return; }
            _panel.Visible = true;
            ReloadModel();
            RebuildGraph();
        }

        /// <summary>Open the panel (used by the T2 "edit in graph view" fallback).</summary>
        public void Open()
        {
            _panel.Visible = true;
            ReloadModel();
            RebuildGraph();
        }

        /// <summary>Hide the panel, first capturing live canvas positions into the in-memory model so unsaved
        /// drags survive a hide/show (they persist to the scenario only on Save).</summary>
        public void Close()
        {
            if (_panel.Visible) CapturePositions();
            _panel.Visible = false;
        }

        private void OnModeChanged(int mode)
        {
            if (mode == (int)GameMode.Play) Close();   // authoring is Edit-only
        }

        /// <summary>Unsubscribe the ModeChanged handler bound in <see cref="Initialize"/> so a freed panel never
        /// leaks a live subscription on the surviving GameState (symmetry with Initialize).</summary>
        public override void _ExitTree()
        {
            if (_gameState != null) _gameState.ModeChanged -= OnModeChanged;
        }

        // ── Kit bootstrap (mirrors TechTreePanel.EnsureKitInitialized) ──

        private void EnsureKitInitialized()
        {
            _theme = ResourceLoader.Load<GodotTheme>(ThemeBuilder.ThemePath, cacheMode: ResourceLoader.CacheMode.Ignore)
                     ?? ThemeBuilder.Build();
            if (!ChimeraComponents.IsInitialized)
            {
                _accent = new AccentController { Name = "AccentController" };
                AddChild(_accent);
                _accent.Initialize(_theme);
                ChimeraComponents.Initialize(_theme, _accent);
            }
        }

        // ── UI construction ──

        private void BuildUi()
        {
            _canvas = new CanvasLayer { Layer = 9 };   // below the narrow inspectors, like TechTreePanel
            AddChild(_canvas);

            _panel = ChimeraComponents.Panel(ChimeraComponents.PanelVariant.Default);
            _panel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _panel.OffsetLeft = MARGIN; _panel.OffsetTop = MARGIN; _panel.OffsetRight = -MARGIN; _panel.OffsetBottom = -MARGIN;
            _panel.Theme = _theme;
            _canvas.AddChild(_panel);

            var root = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            root.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            _panel.AddChild(root);

            // Title + palette + save + close row.
            var titleRow = new HBoxContainer();
            titleRow.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            root.AddChild(titleRow);

            var title = new Label { Text = "Node Graph Editor (T3)" };
            title.AddThemeFontOverride("font", _theme.GetFont(ThemeTokens.FontDisplay, ThemeTokens.Type));
            title.AddThemeFontSizeOverride("font_size", _theme.GetFontSize(ThemeTokens.T2xl, ThemeTokens.Type));
            title.AddThemeColorOverride("font_color", _theme.GetColor(ThemeTokens.TextHi, ThemeTokens.Type));
            title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            titleRow.AddChild(title);

            _palette = new OptionButton();
            foreach (string k in PaletteKinds) _palette.AddItem(k);
            titleRow.AddChild(_palette);

            var addBtn = ChimeraComponents.Button("Add node", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Sm);
            addBtn.Pressed += OnAddNodePressed;
            titleRow.AddChild(addBtn);

            var saveBtn = ChimeraComponents.Button("Save", ChimeraComponents.ButtonVariant.Primary, ChimeraComponents.ButtonSize.Sm);
            saveBtn.Pressed += Save;
            titleRow.AddChild(saveBtn);

            var closeBtn = ChimeraComponents.Button("Close", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Sm);
            closeBtn.Pressed += Close;
            titleRow.AddChild(closeBtn);

            _statusLabel = new Label { Text = "", Visible = false, AutowrapMode = TextServer.AutowrapMode.Word };
            root.AddChild(_statusLabel);

            // Body: variables side table (left) + graph (fills).
            var body = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            root.AddChild(body);

            var side = new VBoxContainer { CustomMinimumSize = new Vector2(200, 0) };
            side.AddChild(new Label { Text = "Variables" });
            var sideScroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(200, 0) };
            _varsList = new VBoxContainer();
            sideScroll.AddChild(_varsList);
            side.AddChild(sideScroll);
            body.AddChild(side);

            _graph = new GraphEdit
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical   = Control.SizeFlags.ExpandFill,
            };
            // Drag-off-the-port disconnect must be whitelisted per type on BOTH sides (the TechTreePanel lesson).
            _graph.AddValidLeftDisconnectType(EXEC_TYPE);
            _graph.AddValidRightDisconnectType(EXEC_TYPE);
            _graph.AddValidLeftDisconnectType(DATA_TYPE);
            _graph.AddValidRightDisconnectType(DATA_TYPE);
            _graph.ConnectionRequest    += OnConnectionRequest;
            _graph.DisconnectionRequest += OnDisconnectionRequest;
            _graph.DeleteNodesRequest   += OnDeleteNodesRequest;
            body.AddChild(_graph);

            _panel.Visible = false;
        }

        // ── Model load / save ──

        private void ReloadModel()
        {
            string? json = _scenario?.TriggerGraphJson;

            // Preserve unsaved edits across a hide/show: only re-parse when the scenario's stored graph JSON
            // actually changed out from under us (an external T2 / raw-IR edit). An unchanged string keeps the
            // current _editGraph (its topology and unsaved node positions intact).
            if (string.Equals(json, _lastLoadedJson, StringComparison.Ordinal))
                return;

            // The stored JSON changed out from under us — the external edit wins (T2 / raw-IR are authoritative),
            // but if this model held unsaved work, say so instead of silently discarding it.
            bool hadUnsavedEdits = false;
            if (_lastLoadedJson != null)
            {
                try { hadUnsavedEdits = !string.Equals(_editGraph.ToCanonicalJson(), _lastLoadedJson, StringComparison.Ordinal); }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException) { hadUnsavedEdits = true; }
            }

            _lastLoadedJson = json;
            _editGraph = new TriggerGraph();
            if (!string.IsNullOrWhiteSpace(json))
            {
                try { _editGraph = TriggerGraph.FromJson(json!); }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
                {
                    // Fail-open canvas: an unparseable/absent graph opens EMPTY and editable (no throw).
                    _editGraph = new TriggerGraph();
                    ShowStatus($"Graph JSON could not be parsed — opened an empty canvas. ({ex.Message})", danger: true);
                    return;
                }
                // Duplicate node ids are a load-gate reject (TriggerGraph.FromJson permits them; GraphStructureGate
                // rejects). Still open the graph for repair, but say so loudly rather than silently.
                var seen = new HashSet<int>();
                foreach (NodeBase n in _editGraph.Nodes)
                    if (!seen.Add(n.Id))
                    {
                        ShowStatus("This graph has duplicate node ids — it is load-gate-invalid and will be rejected at load. Opened for repair.", danger: true);
                        break;
                    }
            }
            if (hadUnsavedEdits)
                ShowStatus("The stored graph changed externally (T2 / raw-IR edit) — your unsaved graph edits were replaced by the stored version.", danger: true);
        }

        /// <summary>Canonicalize the edited graph channel back into <see cref="ScenarioData.TriggerGraphJson"/>
        /// (in-memory, mirroring how TriggerEditorPanel mutates the shared ScenarioData — disk persistence stays
        /// with the scenario-save path). Captures live canvas positions into each node's <c>_editor</c> bag first.
        /// The flat <c>Triggers[]</c> channel is never touched.</summary>
        private void Save()
        {
            if (_scenario == null) { ShowStatus("No scenario loaded.", danger: true); return; }
            CapturePositions();
            try
            {
                _scenario.TriggerGraphJson = _editGraph.ToCanonicalJson();
                _lastLoadedJson = _scenario.TriggerGraphJson; // keep the reload-guard in sync with what we persisted

                // Persist the in-progress JSON even when invalid (never lose work), but do NOT claim a clean save —
                // route the structural gate's first-fail error onto the status line so the author knows the
                // scenario will be REJECTED at load until it is fixed.
                GraphNodeError? problem = FirstLocatedProblem();
                if (problem is { } p)
                    ShowStatus($"Saved in-progress graph, but it is INVALID and will be rejected at load: {p.Message}", danger: true);
                else
                    ShowStatus("Saved graph channel (flat triggers untouched).", danger: false);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
            {
                ShowStatus($"Save rejected: {ex.Message}", danger: true);
            }
        }

        /// <summary>Write every graph-channel GraphNode's current canvas position into its model node's
        /// <c>_editor</c> bag (int-rounded, merge-preserving). Cap-overflow is swallowed (positions are tiny —
        /// unreachable — and a cosmetic position must never block a save).</summary>
        private void CapturePositions()
        {
            var byId = new Dictionary<int, NodeBase>();
            foreach (NodeBase n in _editGraph.Nodes) byId[n.Id] = n;
            foreach (Node child in _graph.GetChildren())
            {
                if (child is not GraphNode gn) continue;
                string name = gn.Name;
                if (!name.StartsWith("g", StringComparison.Ordinal)) continue;   // flat "f" positions are never persisted
                if (!int.TryParse(name.AsSpan(1), out int id) || !byId.TryGetValue(id, out NodeBase? node)) continue;
                Vector2 p = gn.PositionOffset;
                try { NodeEditorAnnotation.SetPosition(node, ClampRound(p.X), ClampRound(p.Y)); }
                catch (System.Text.Json.JsonException ex)
                {
                    // Over-cap / non-object bag: keep the prior bag and never block the save — but say so, or the
                    // node silently jumps back on the next reopen.
                    ShowStatus($"Node {id}: position not persisted ({ex.Message})", danger: true);
                }
            }
        }

        /// <summary>Round a canvas coordinate to int, clamping to the int range first so an extreme scroll offset
        /// can never overflow-wrap the persisted position (a float past int range casts to garbage otherwise).</summary>
        private static int ClampRound(float v)
        {
            double d = Math.Round((double)v);
            if (double.IsNaN(d)) return 0; // NaN fails both clamp comparisons and would cast to garbage
            if (d >= int.MaxValue) return int.MaxValue;
            if (d <= int.MinValue) return int.MinValue;
            return (int)d;
        }

        // ── Graph (re)build ──

        private void RebuildGraph()
        {
            foreach (Node child in _graph.GetChildren().ToList())
                if (child is GraphNode) { _graph.RemoveChild(child); child.QueueFree(); }
            _inByOrdinal.Clear(); _outByOrdinal.Clear();
            _inOrdinalOf.Clear(); _outOrdinalOf.Clear(); _outSlotOf.Clear();

            RefreshVarsList();

            // Editable graph channel (prefix "g", positioned from _editor or auto-laid-out).
            RenderChannel(_editGraph, "g", readOnly: false);

            // Read-only flat channel (prefix "f", dimmed, auto-laid-out to the right).
            if (_scenario?.Triggers is { Length: > 0 } triggers)
            {
                TriggerGraph flat = TriggerGraph.FromFlat(triggers);
                RenderChannel(flat, "f", readOnly: true, xOffset: 8f * COL_SPACING);
            }

            DrawEdges(_editGraph, "g");
            if (_scenario?.Triggers is { Length: > 0 } t2)
                DrawEdges(TriggerGraph.FromFlat(t2), "f");

            RefreshErrorBadges();
        }

        private void RenderChannel(TriggerGraph graph, string prefix, bool readOnly, float xOffset = 0f)
        {
            // Deterministic auto-layout fallback: ascending id in a single column (only used when a node has no
            // persisted _editor position). Flat nodes never persist positions, so they always auto-lay-out.
            int autoRow = 0;
            foreach (NodeBase n in graph.Nodes.OrderBy(x => x.Id))
            {
                string name = prefix + n.Id;
                var gn = new GraphNode { Name = name, Title = TitleFor(n) + (readOnly ? "  [read-only]" : "") };
                if (readOnly)
                {
                    gn.Modulate = new Color(0.72f, 0.72f, 0.78f);
                    gn.Draggable = false; // the matrix's "not movable" — flat (T2) nodes are view-only here
                }

                BuildPorts(gn, name, n);

                (int X, int Y)? pos = readOnly ? null : NodeEditorAnnotation.GetPosition(n);
                if (pos is { } p)
                    gn.PositionOffset = new Vector2(p.X, p.Y);
                else
                {
                    // Only auto-laid-out nodes advance the fallback grid (a positioned node must not leave a hole).
                    gn.PositionOffset = new Vector2(xOffset + (autoRow % 3) * COL_SPACING, (autoRow / 3) * ROW_SPACING + autoRow * 8f);
                    autoRow++;
                }

                AttachTip(gn, gn.Title, readOnly
                    ? "A flat (T2) trigger, shown read-only for context. Edit it in the T2 Trigger Editor (L)."
                    : "Drag a right port onto a left port to wire. Drag an edge off its port to disconnect. Select + Delete to remove.");
                _graph.AddChild(gn);
            }
        }

        /// <summary>Build one slot per port (inputs first as left-only slots, then outputs as right-only slots),
        /// recording the IR-port↔Godot-ordinal maps this panel translates connection signals through.</summary>
        private void BuildPorts(GraphNode gn, string name, NodeBase n)
        {
            PortsOf(n, out List<PortDef> inputs, out List<PortDef> outputs);
            var inList = new List<PortDef>();
            var outList = new List<PortDef>();
            var inOrd = new Dictionary<(bool, int), int>();
            var outOrd = new Dictionary<(bool, int), int>();
            var outSlot = new Dictionary<(bool, int), int>();

            int slot = 0;
            foreach (PortDef pd in inputs)
            {
                gn.AddChild(new Label { Text = pd.Label });
                Color c = pd.IsData ? DataInColor : ExecColor;
                gn.SetSlot(slot, true, pd.IsData ? DATA_TYPE : EXEC_TYPE, c, false, 0, Transparent);
                inOrd[(pd.IsData, pd.IrPort)] = inList.Count;
                inList.Add(pd);
                slot++;
            }
            foreach (PortDef pd in outputs)
            {
                gn.AddChild(new Label { Text = pd.Label, HorizontalAlignment = HorizontalAlignment.Right });
                Color c = pd.IsData ? Color.FromHtml(DataWireColorPalette.HexFor(pd.Wire)) : ExecColor;
                gn.SetSlot(slot, false, 0, Transparent, true, pd.IsData ? DATA_TYPE : EXEC_TYPE, c);
                outOrd[(pd.IsData, pd.IrPort)] = outList.Count;
                outSlot[(pd.IsData, pd.IrPort)] = slot;
                outList.Add(pd);
                slot++;
            }
            if (slot == 0) gn.AddChild(new Label { Text = "•" }); // ensure the node has a body

            _inByOrdinal[name]  = inList;
            _outByOrdinal[name] = outList;
            _inOrdinalOf[name]  = inOrd;
            _outOrdinalOf[name] = outOrd;
            _outSlotOf[name]    = outSlot;
        }

        private void DrawEdges(TriggerGraph graph, string prefix)
        {
            foreach (ExecEdge e in graph.ExecEdges)
            {
                if (!TryOrdinals(prefix, e.Src, false, e.SrcPort, e.Dst, e.DstPort, out int fp, out int tp, out string from, out string to))
                {
                    ShowStatus($"An exec edge ({e.Src}:{e.SrcPort} → {e.Dst}:{e.DstPort}) references a port not rendered for its node kind — it is not drawn (fix or delete it).", danger: true);
                    continue;
                }
                _graph.ConnectNode(from, fp, to, tp);
            }
            foreach (DataEdge e in graph.DataEdges)
            {
                if (!TryOrdinals(prefix, e.Src, true, e.SrcPort, e.Dst, e.DstPort, out int fp, out int tp, out string from, out string to))
                {
                    ShowStatus($"A data edge ({e.Src}:{e.SrcPort} → {e.Dst}:{e.DstPort}) references a port not rendered for its node kind — it is not drawn (fix or delete it).", danger: true);
                    continue;
                }
                // Wire color = type: recolor the source out-port to the edge's DataWireType before drawing.
                if (_outSlotOf.TryGetValue(from, out var slots) && slots.TryGetValue((true, e.SrcPort), out int slotIdx))
                    RecolorOutSlot(from, slotIdx, Color.FromHtml(DataWireColorPalette.HexFor(e.Wire)));
                _graph.ConnectNode(from, fp, to, tp);
            }
        }

        private bool TryOrdinals(string prefix, int srcId, bool isData, int srcPort, int dstId, int dstPort,
            out int fromPort, out int toPort, out string from, out string to)
        {
            fromPort = toPort = 0;
            from = prefix + srcId; to = prefix + dstId;
            return _outOrdinalOf.TryGetValue(from, out var oo) && oo.TryGetValue((isData, srcPort), out fromPort)
                && _inOrdinalOf.TryGetValue(to, out var io) && io.TryGetValue((isData, dstPort), out toPort);
        }

        private void RecolorOutSlot(string nodeName, int slotIndex, Color color)
        {
            if (_graph.GetNodeOrNull<GraphNode>(nodeName) is GraphNode gn)
                gn.SetSlot(slotIndex, false, 0, Transparent, true, DATA_TYPE, color);
        }

        // ── Connection handling (validate BEFORE mutating the model / drawing) ──

        private void OnConnectionRequest(StringName fromNode, long fromPort, StringName toNode, long toPort)
        {
            string from = fromNode.ToString(), to = toNode.ToString();
            if (from == to) { ShowStatus("A node cannot wire to itself.", danger: true); return; }
            if (IsFlat(from) || IsFlat(to)) { ShowStatus("Flat (T2) triggers are read-only here — edit them in the T2 editor (L).", danger: true); return; }
            if (!TryPortDefs(from, (int)fromPort, to, (int)toPort, out PortDef src, out PortDef dst, out int srcId, out int dstId))
            { ShowStatus("Ports could not be resolved for this drag — re-open the panel and try again.", danger: true); return; }
            if (src.IsData != dst.IsData) { ShowStatus("Cannot wire an exec port to a data port.", danger: true); return; }

            // Build the candidate edge + graph, then authority-check via the load gate.
            ExecEdge? ex = null; DataEdge? da = null;
            if (src.IsData)
            {
                // A condition-in / branch-cond-in sink is Boolean by port contract: when the source's produced
                // type is KNOWN and not Boolean, reject pre-draw (the matrix's type-mismatch row) instead of
                // stamping a coerced Boolean wire the load gate then rejects. An UNKNOWN source type (a
                // work-in-progress expression) stays admissible — the load gate remains authoritative.
                bool condSink = IsCondSink(dstId, dst);
                if (condSink)
                {
                    BuildDeclMaps(out var dm, out var ad);
                    DataWireType? srcType = DataWireInference.TryInferSourceType(_editGraph, srcId, dm, ad);
                    if (srcType is { } st && st != DataWireType.Boolean)
                    { ShowStatus($"Rejected: a condition input requires a Boolean source (this source produces {st}).", danger: true); return; }
                }
                DataWireType wire = InferWire(srcId, dstId, dst);
                da = new DataEdge(srcId, src.IrPort, dstId, dst.IrPort, wire);
                // Same endpoints = same connection regardless of stamped wire (a re-drag after a variable's
                // declared type changed must not stack a near-duplicate edge into a fan-in port).
                if (_editGraph.DataEdges.Any(x =>
                        x.Src == srcId && x.SrcPort == src.IrPort && x.Dst == dstId && x.DstPort == dst.IrPort))
                { ShowStatus("That wire already exists.", danger: false); return; }
            }
            else
            {
                ex = new ExecEdge(srcId, src.IrPort, dstId, dst.IrPort);
                if (_editGraph.ExecEdges.Any(x => x.Equals(ex.Value)))
                { ShowStatus("That wire already exists.", danger: false); return; }
            }

            // Validate ONLY the proposed edge against the existing graph (not a two-full-Check string-diff, which
            // could admit an illegal edge shadowed by a pre-existing error or reject a legal edge that reorders
            // which pre-existing error sorts first).
            string? edgeErr = da.HasValue
                ? GraphStructureGate.TryValidateNewEdge(_editGraph, true, da.Value.Src, da.Value.SrcPort, da.Value.Dst, da.Value.DstPort, da.Value.Wire)
                : GraphStructureGate.TryValidateNewEdge(_editGraph, false, ex!.Value.Src, ex.Value.SrcPort, ex.Value.Dst, ex.Value.DstPort, default);
            if (edgeErr != null) { ShowStatus($"Rejected: {edgeErr}", danger: true); return; }

            if (ex.HasValue) _editGraph.ExecEdges.Add(ex.Value);
            if (da.HasValue) _editGraph.DataEdges.Add(da.Value);
            CapturePositions();
            RebuildGraph();
            ShowStatus("Wired.", danger: false);
        }

        private void OnDisconnectionRequest(StringName fromNode, long fromPort, StringName toNode, long toPort)
        {
            string from = fromNode.ToString(), to = toNode.ToString();
            if (IsFlat(from) || IsFlat(to)) return; // flat edges are read-only
            if (!TryPortDefs(from, (int)fromPort, to, (int)toPort, out PortDef src, out PortDef dst, out int srcId, out int dstId))
            { ShowStatus("Ports could not be resolved for this disconnect — re-open the panel and try again.", danger: true); return; }
            if (src.IsData)
                _editGraph.DataEdges.RemoveAll(e => e.Src == srcId && e.SrcPort == src.IrPort && e.Dst == dstId && e.DstPort == dst.IrPort);
            else
                _editGraph.ExecEdges.RemoveAll(e => e.Src == srcId && e.SrcPort == src.IrPort && e.Dst == dstId && e.DstPort == dst.IrPort);
            CapturePositions();
            RebuildGraph();
            ShowStatus("Disconnected.", danger: false);
        }

        private void OnDeleteNodesRequest(Godot.Collections.Array<StringName> nodes)
        {
            bool any = false;
            foreach (StringName sn in nodes)
            {
                string name = sn.ToString();
                if (IsFlat(name)) { ShowStatus("Flat (T2) triggers cannot be deleted here.", danger: true); continue; }
                if (!int.TryParse(name.AsSpan(1), out int id)) continue;
                _editGraph.Nodes.RemoveAll(n => n.Id == id);
                _editGraph.ExecEdges.RemoveAll(e => e.Src == id || e.Dst == id);
                _editGraph.DataEdges.RemoveAll(e => e.Src == id || e.Dst == id);
                any = true;
            }
            if (any) { CapturePositions(); RebuildGraph(); ShowStatus("Deleted node(s) and incident edges.", danger: false); }
        }

        private void OnAddNodePressed()
        {
            if (_palette.Selected < 0) return;
            string kind = PaletteKinds[_palette.Selected];
            int id = _editGraph.Nodes.Count == 0 ? 0 : _editGraph.Nodes.Max(n => n.Id) + 1;
            NodeBase? node = NodePaletteFactory.Create(kind, id);
            if (node == null) { ShowStatus($"Unknown palette kind '{kind}'.", danger: true); return; }
            // Seed a canvas position on a 6×10 grid so successive adds spread out instead of stacking.
            try { NodeEditorAnnotation.SetPosition(node, 40 + (id % 6) * 180, 40 + ((id / 6) % 10) * 90); }
            catch (System.Text.Json.JsonException) { /* unreachable for a fresh node */ }
            _editGraph.Nodes.Add(node);
            CapturePositions();
            RebuildGraph();
            ShowStatus($"Added '{kind}' (id {id}).", danger: false);
        }

        // ── Error routing onto nodes ──

        /// <summary>The first located problem the LOAD PATH would reject: the structural gate's first-fail error,
        /// else an exec-edge cycle (which the gate has no rule for — at load it rejects later inside
        /// <c>TriggerGraph.WalkChain</c>, so a clean gate pass alone must not report a clean save).</summary>
        private GraphNodeError? FirstLocatedProblem()
        {
            BuildDeclMaps(out var declMap, out var arrayDecls);
            IReadOnlyList<GraphNodeError> errs = GraphStructureGate.CheckGraphLocated(_editGraph, declMap, arrayDecls);
            if (errs.Count > 0) return errs[0];
            return GraphStructureGate.FindExecCycle(_editGraph);
        }

        private void RefreshErrorBadges()
        {
            GraphNodeError? problem = FirstLocatedProblem();
            if (problem is not { } first) return;
            GraphNode? gn = first.NodeId >= 0 ? _graph.GetNodeOrNull<GraphNode>("g" + first.NodeId) : null;
            if (gn != null)
            {
                ChimeraValidationBadge badge = ChimeraValidationBadge.Create();
                gn.AddChild(badge);
                badge.ShowError(first.Message);
            }
            else
            {
                ShowStatus(first.Message, danger: true); // no on-canvas locus → the status line
            }
        }

        // ── Variables side table (read-only reflection of ScenarioData.Variables — editing stays in T2) ──

        private void RefreshVarsList()
        {
            foreach (Node c in _varsList.GetChildren().ToList()) { _varsList.RemoveChild(c); c.QueueFree(); }
            ScenarioVariable[]? vars = _scenario?.Variables;
            if (vars == null || vars.Length == 0) { _varsList.AddChild(new Label { Text = "(none)" }); return; }
            foreach (ScenarioVariable v in vars)
            {
                string typeText = v.Type == DslValueType.Array && v.ElementType is DslValueType et
                    ? $"Array<{et}>[{v.Capacity ?? 0}]" : v.Type.ToString();
                _varsList.AddChild(new Label { Text = $"{v.Name} : {typeText} / {v.Scope}", AutowrapMode = TextServer.AutowrapMode.Word });
            }
        }

        // ── Helpers ──

        private static bool IsFlat(string name) => name.StartsWith("f", StringComparison.Ordinal);

        private bool TryPortDefs(string from, int fromPort, string to, int toPort,
            out PortDef src, out PortDef dst, out int srcId, out int dstId)
        {
            src = default; dst = default; srcId = dstId = -1;
            if (!_outByOrdinal.TryGetValue(from, out var outs) || fromPort < 0 || fromPort >= outs.Count) return false;
            if (!_inByOrdinal.TryGetValue(to, out var ins) || toPort < 0 || toPort >= ins.Count) return false;
            src = outs[fromPort]; dst = ins[toPort];
            return int.TryParse(from.AsSpan(1), out srcId) && int.TryParse(to.AsSpan(1), out dstId);
        }

        /// <summary>Typed wire for a NEW data edge, delegated to the Godot-free <see cref="DataWireInference"/>
        /// seam (the authoritative type check remains the pre-tick validator). Passes the source id, whether the
        /// destination is a condition-in / branch-cond-in sink, and the declared-variable/array maps.</summary>
        private DataWireType InferWire(int srcId, int dstId, PortDef dst)
        {
            BuildDeclMaps(out var declMap, out var arrayDecls);
            return DataWireInference.InferWireType(_editGraph, srcId, IsCondSink(dstId, dst), declMap, arrayDecls);
        }

        /// <summary>True when the destination is a Boolean-by-contract sink: a trigger's condition-in port or a
        /// branch's cond-in port (destination node KIND + port — never the bare port number, which collides with
        /// <c>ActionValueInPort</c>).</summary>
        private bool IsCondSink(int dstId, PortDef dst)
        {
            NodeBase? dstNode = _editGraph.Nodes.FirstOrDefault(n => n.Id == dstId);
            return dst.IsData
                && ((dstNode is TriggerNode && dst.IrPort == TriggerGraph.TriggerConditionInPort)
                    || (dstNode is BranchNode && dst.IrPort == TriggerGraph.BranchCondInPort));
        }

        private void BuildDeclMaps(
            out Dictionary<string, (DslValueType Type, VarScope Scope)> declMap,
            out Dictionary<string, (DslValueType Elem, int Capacity)> arrayDecls)
        {
            declMap = new Dictionary<string, (DslValueType, VarScope)>(StringComparer.Ordinal);
            arrayDecls = new Dictionary<string, (DslValueType, int)>(StringComparer.Ordinal);
            if (_scenario?.Variables == null) return;
            foreach (ScenarioVariable v in _scenario.Variables)
            {
                if (string.IsNullOrWhiteSpace(v.Name)) continue;
                declMap[v.Name] = (v.Type, v.Scope);
                if (v.Type == DslValueType.Array && v.ElementType is DslValueType elem && v.Capacity is int cap)
                    arrayDecls[v.Name] = (elem, cap);
            }
        }

        /// <summary>The input/output IR ports of a node kind, mapped to GraphNode slots. Exec and data ports share
        /// a numeric space but are disambiguated by <see cref="PortDef.IsData"/>.</summary>
        private static void PortsOf(NodeBase n, out List<PortDef> inputs, out List<PortDef> outputs)
        {
            inputs = new List<PortDef>();
            outputs = new List<PortDef>();
            switch (n)
            {
                case TriggerNode:
                    inputs.Add(new PortDef(false, TriggerGraph.TriggerEventInPort, "event"));
                    inputs.Add(new PortDef(true, TriggerGraph.TriggerConditionInPort, "cond", DataWireType.Boolean));
                    outputs.Add(new PortDef(false, TriggerGraph.TriggerExecOutPort, "then"));
                    break;
                case EventNode:
                    outputs.Add(new PortDef(false, TriggerGraph.EventExecOutPort, "fire"));
                    break;
                case ConditionNode:
                    outputs.Add(new PortDef(true, TriggerGraph.ConditionDataOutPort, "bool", DataWireType.Boolean));
                    break;
                case ActionNode a:
                    inputs.Add(new PortDef(false, TriggerGraph.ActionExecInPort, "in"));
                    inputs.Add(new PortDef(true, TriggerGraph.ActionValueInPort, "val", DataWireType.Int));
                    if (a.Kind == "array_set")
                        inputs.Add(new PortDef(true, TriggerGraph.ActionIndexInPort, "idx", DataWireType.Int));
                    outputs.Add(new PortDef(false, TriggerGraph.ActionExecOutPort, "out"));
                    break;
                case EffectActionNode:
                    inputs.Add(new PortDef(false, TriggerGraph.ActionExecInPort, "in"));
                    outputs.Add(new PortDef(false, TriggerGraph.ActionExecOutPort, "out"));
                    break;
                case RaiseEventNode:
                    inputs.Add(new PortDef(false, TriggerGraph.ActionExecInPort, "in"));
                    inputs.Add(new PortDef(true, TriggerGraph.RaiseArgInPort0, "arg0", DataWireType.Int));
                    inputs.Add(new PortDef(true, TriggerGraph.RaiseArgInPort1, "arg1", DataWireType.Int));
                    inputs.Add(new PortDef(true, TriggerGraph.RaiseArgInPort2, "arg2", DataWireType.Int));
                    inputs.Add(new PortDef(true, TriggerGraph.RaiseArgInPort3, "arg3", DataWireType.Int));
                    outputs.Add(new PortDef(false, TriggerGraph.ActionExecOutPort, "out"));
                    break;
                case ForEachNode:
                case ForEachBatchedNode:
                    inputs.Add(new PortDef(false, TriggerGraph.ActionExecInPort, "in"));
                    outputs.Add(new PortDef(false, TriggerGraph.ActionExecOutPort, "next"));
                    outputs.Add(new PortDef(false, TriggerGraph.ForEachBodyOutPort, "body"));
                    break;
                case BranchNode:
                    inputs.Add(new PortDef(false, TriggerGraph.ActionExecInPort, "in"));
                    inputs.Add(new PortDef(true, TriggerGraph.BranchCondInPort, "cond", DataWireType.Boolean));
                    outputs.Add(new PortDef(false, TriggerGraph.ActionExecOutPort, "next"));
                    outputs.Add(new PortDef(false, TriggerGraph.BranchThenOutPort, "then"));
                    outputs.Add(new PortDef(false, TriggerGraph.BranchElseOutPort, "else"));
                    break;
                case ExprUnaryNode:
                case ExprArrayGetNode:
                    inputs.Add(new PortDef(true, TriggerGraph.ExprOperandPort0, "a", DataWireType.Int));
                    outputs.Add(new PortDef(true, TriggerGraph.ExprDataOutPort, "out", DataWireType.Int));
                    break;
                case ExprBinaryNode:
                case ExprCallNode:
                    inputs.Add(new PortDef(true, TriggerGraph.ExprOperandPort0, "a", DataWireType.Int));
                    inputs.Add(new PortDef(true, TriggerGraph.ExprOperandPort1, "b", DataWireType.Int));
                    outputs.Add(new PortDef(true, TriggerGraph.ExprDataOutPort, "out", DataWireType.Int));
                    break;
                case ExprLiteralNode:
                case ExprVarNode:
                case ExprArrayLenNode:
                case ExprEventParamNode:
                    outputs.Add(new PortDef(true, TriggerGraph.ExprDataOutPort, "out", DataWireType.Int));
                    break;
            }
        }

        private static string TitleFor(NodeBase n) => n switch
        {
            TriggerNode t        => $"Trigger: {t.Name}",
            EventNode e          => $"Event: {(e.Kind == "custom_event" ? e.EventName : e.Kind)}",
            ConditionNode c      => $"Cond: {c.Kind}",
            ActionNode a         => $"Action: {a.Kind}",
            EffectActionNode     => "run_effect",
            RaiseEventNode r     => $"raise: {r.Name}",
            ForEachNode          => "for_each",
            ForEachBatchedNode   => "for_each_batched",
            BranchNode           => "branch",
            ExprLiteralNode l    => $"lit: {l.Raw}",
            ExprVarNode v        => $"var: {v.Name}",
            ExprUnaryNode u      => $"unary: {u.Op}",
            ExprBinaryNode b     => $"binop: {b.Op}",
            ExprCallNode fn      => $"call: {fn.Fn}",
            ExprArrayGetNode ag  => $"get: {ag.Name}",
            ExprArrayLenNode al  => $"len: {al.Name}",
            ExprEventParamNode p => $"event.{p.Name}",
            _                    => n.GetType().Name,
        };

        private void ShowStatus(string msg, bool danger)
        {
            _statusLabel.Visible = true;
            _statusLabel.Text = msg;
            _statusLabel.AddThemeColorOverride("font_color",
                _theme.GetColor(danger ? ThemeTokens.Danger : ThemeTokens.TextHi, ThemeTokens.Type));
        }

        private void AttachTip(Control target, string term, string body)
            => ChimeraTooltip.AttachFocusable(target, term, body, ChimeraTooltip.TooltipRole.Pop);
    }
}
