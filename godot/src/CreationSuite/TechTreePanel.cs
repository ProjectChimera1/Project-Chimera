#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using ProjectChimera.Core.Definitions;   // BuildingDefinition, FactionDefinition, TechTreeValidator, TechTreeLayout, FactionWriter
using ProjectChimera.UI;                  // GameState, GameMode
using ProjectChimera.UI.Components;        // ChimeraComponents
using ProjectChimera.UI.Theme;             // ThemeTokens, ThemeBuilder, AccentController
using GodotTheme = Godot.Theme;            // the ProjectChimera.UI.Theme namespace shadows the bare Theme type

namespace ProjectChimera.CreationSuite
{
    /// <summary>
    /// Story 4.6 — the Visual Tech Tree Editor: the FIRST <see cref="GraphEdit"/>/<see cref="GraphNode"/> usage
    /// anywhere in this codebase. One <see cref="GraphNode"/> per loaded building, laid out into tier columns via
    /// <see cref="TechTreeLayout.ComputeTiers"/> (a pure function over the current <c>Prerequisites</c> graph —
    /// never authored/persisted, so a reload always redraws identically). Dragging an out-port onto another node
    /// appends the source building's id to the target's <see cref="BuildingDefinition.Prerequisites"/>, validated
    /// inline by <see cref="TechTreeValidator.ValidateProposedEdge"/> (a single-edge reuse of Story 4.2's own cycle
    /// DFS — byte-identical rejection wording to the import-time lint), then persisted through
    /// <see cref="FactionWriter.SyncFactionBuildings"/> using the same self-check-reload-then-atomic-<c>File.Move</c>
    /// sequence <c>BuildingCardPanel.Edit.cs</c>'s <c>PersistSync</c> uses (duplicated locally — this codebase's
    /// existing per-editor mirroring precedent, not a shared helper). Selecting a node opens the existing
    /// <c>BuildingCardPanel</c> (Story 4.5) right-dock inspector via <see cref="BuildingCardPanel.SelectAndShow"/>.
    ///
    /// <para><b>Presentation-layer only.</b> Never touches <c>EntityWorld</c>/sim arrays/<c>BuildingSystem</c>/
    /// <c>TechTreeChecker</c>'s runtime consumption or checksums — this story only edits the SAME <c>prerequisites</c>
    /// data Story 4.2's runtime already gates on, unchanged. Never wires research/<c>ResearchDefinition</c> (Story
    /// 4.11's job). Never persists a node's dragged position (Design Notes).</para>
    /// </summary>
    public partial class TechTreePanel : Node
    {
        // ── Layout constants ──
        private const float TIER_SPACING = 260f;   // horizontal spacing between tier columns
        private const float LANE_SPACING = 160f;   // vertical spacing between lanes within a tier
        private const float MARGIN       = 24f;    // FullRect inset — a graph needs canvas space, unlike the 480px right-dock panels

        private static readonly Color PortColorIn  = new(0.55f, 0.75f, 1.00f);
        private static readonly Color PortColorOut = new(1.00f, 0.75f, 0.35f);
        // Story 4.11: research nodes' own port color (both slots), distinct from the building PortColorIn/PortColorOut
        // pair — a visual "this is a research node" cue at a glance, on the SAME port type 0 (so building→research and
        // research→research edges stay connectable under GraphEdit's default same-type connectivity — no
        // AddValidConnectionType change needed).
        private static readonly Color PortColorResearch = new(0.55f, 1.00f, 0.60f);

        // ── Kit context ──
        private GodotTheme        _theme  = null!;

        // ── Deps (wired by TechTreePhase after AddChild) ──
        private FactionDefinition? _faction;
        private GameState?         _gameState;
        private string             _factionJsonPath = "";   // res:// path of the faction file to write edits back to
        private BuildingCardPanel? _inspector;               // Story 4.5's shared right-dock inspector
        // Story 4.11: this panel's OWN research inspector (unlike _inspector, not shared with any other phase —
        // no bootstrap phase publishes a ResearchCardPanel, so this panel constructs and owns its sibling directly).
        private ResearchCardPanel  _researchInspector = null!;

        // ── Shell ──
        private CanvasLayer    _canvas      = null!;
        private PanelContainer _panel       = null!;
        private GraphEdit      _graph       = null!;
        private Label          _statusLabel = null!;

        // ── Lifecycle ─────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public override void _Ready()
        {
            _theme = ChimeraComponents.EnsureInitialized(this);   // MUST run before any ChimeraComponents.* call, or the factory throws
            BuildUi();
        }

        /// <summary>
        /// Bind the panel to the current scenario's faction + game state + the faction file <c>res://</c> path to
        /// persist edge edits to, plus the shared <see cref="BuildingCardPanel"/> inspector node selection opens.
        /// Called by <c>TechTreePhase</c> AFTER <c>AddChild</c>. Starts hidden; shown by the <c>R</c> toggle in Edit
        /// mode.
        /// </summary>
        public void Initialize(FactionDefinition? faction, GameState gameState, string factionJsonPath, BuildingCardPanel inspector)
        {
            _faction         = faction;
            _gameState       = gameState;
            _factionJsonPath = factionJsonPath ?? "";
            _inspector       = inspector;

            _gameState.ModeChanged += OnModeChanged;   // authoring is Edit-only — hide in Play
            _panel.Visible = false;

            // Story 4.11: bind the sibling research inspector to the SAME faction/game-state/file path — mirrors
            // this panel's own binding, so a research edit and a building edit stay consistent in memory exactly
            // like BuildingCardPanel/TechTreePanel already do for buildings.
            _researchInspector.Initialize(_faction, _gameState, _factionJsonPath);
        }

        /// <summary>Toggle visibility (R key, Edit mode only). On open: recompute tiers and rebuild every node/edge
        /// fresh from the current <c>Prerequisites</c> data (never from a persisted position).</summary>
        public void Toggle()
        {
            _panel.Visible = !_panel.Visible;
            if (_panel.Visible) RebuildGraph();
        }

        /// <summary>Hide the panel.</summary>
        public void Close() => _panel.Visible = false;

        private void OnModeChanged(int mode)
        {
            if (mode == (int)GameMode.Play) Close();   // hide in Play (authoring is Edit-only)
        }

        // ── UI construction ──────────────────────────────────────────────────────

        private void BuildUi()
        {
            // Layer 9 — BELOW BuildingCardPanel's 13, so the narrow inspector floats on top when both are open.
            _canvas = new CanvasLayer { Layer = 9 };
            AddChild(_canvas);

            _panel = ChimeraComponents.Panel(ChimeraComponents.PanelVariant.Default);
            _panel.SetAnchorsPreset(Control.LayoutPreset.FullRect);
            _panel.OffsetLeft   = MARGIN;
            _panel.OffsetTop    = MARGIN;
            _panel.OffsetRight  = -MARGIN;
            _panel.OffsetBottom = -MARGIN;
            _panel.Theme = _theme;   // _panel is a Control (TechTreePanel : Node has NO Theme) — propagates to the subtree
            _canvas.AddChild(_panel);

            var root = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            root.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            _panel.AddChild(root);

            // Title + Close row.
            var titleRow = new HBoxContainer();
            titleRow.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            root.AddChild(titleRow);

            var title = new Label { Text = "Tech Tree Editor" };
            title.AddThemeFontOverride("font", _theme.GetFont(ThemeTokens.FontDisplay, ThemeTokens.Type));
            title.AddThemeFontSizeOverride("font_size", _theme.GetFontSize(ThemeTokens.T2xl, ThemeTokens.Type));
            title.AddThemeColorOverride("font_color", _theme.GetColor(ThemeTokens.TextHi, ThemeTokens.Type));
            title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            title.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            titleRow.AddChild(title);

            var closeBtn = ChimeraComponents.Button(EditorHotkeys.CloseLabel(EditorPanelId.TechTree), ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Sm);
            closeBtn.Pressed += Close;
            AttachTip(closeBtn, "Close", "Close the Tech Tree Editor (R).");
            titleRow.AddChild(closeBtn);

            // Status line (rejection messages / cycle errors).
            _statusLabel = new Label { Text = "", Visible = false, AutowrapMode = TextServer.AutowrapMode.Word };
            root.AddChild(_statusLabel);

            // The graph itself — fills the remaining space.
            _graph = new GraphEdit
            {
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical   = Control.SizeFlags.ExpandFill,
            };
            // Port type 0 (the only type every slot uses) must be explicitly whitelisted for drag-to-disconnect on
            // BOTH sides, or GraphEdit silently no-ops a drag-off-the-port gesture instead of firing
            // disconnection_request (confirmed via manual /godot-verify — dragging an edge off its port did nothing
            // until these two calls were added; same-type CONNECT already works without AddValidConnectionType since
            // matching left/right types are always connectable by default).
            _graph.AddValidLeftDisconnectType(0);
            _graph.AddValidRightDisconnectType(0);

            _graph.ConnectionRequest    += OnConnectionRequest;
            _graph.DisconnectionRequest += OnDisconnectionRequest;
            _graph.NodeSelected         += OnNodeSelected;
            root.AddChild(_graph);

            _panel.Visible = false;   // hidden until the first R toggle

            // Story 4.11: this panel's own sibling research inspector — built here (not by a bootstrap phase) so its
            // _Ready runs before Initialize() (below) ever binds it.
            _researchInspector = new ResearchCardPanel();
            AddChild(_researchInspector);
        }

        // ── Graph (re)build ──────────────────────────────────────────────────────

        /// <summary>Clear every <see cref="GraphNode"/> and rebuild the whole graph fresh: recompute tiers via
        /// <see cref="TechTreeLayout.ComputeTiers"/>, one node per building (stable ascending-id lane order within a
        /// shared tier), then one edge per resolvable <c>Prerequisites</c> entry. Deterministic — the same
        /// <c>Prerequisites</c> data always redraws the identical nodes/tiers/edges (AC: Reload).</summary>
        private void RebuildGraph()
        {
            foreach (Node child in _graph.GetChildren().ToList())
            {
                if (child is GraphNode) { _graph.RemoveChild(child); child.QueueFree(); }
            }

            if (_faction == null) return;

            Dictionary<string, int> tiers = TechTreeLayout.ComputeTiers(_faction.Buildings);

            var buildingById = new Dictionary<string, BuildingDefinition>();
            foreach (BuildingDefinition b in _faction.Buildings)
                if (!string.IsNullOrEmpty(b.Id)) buildingById[b.Id] = b;

            // Lane index = stable ascending-id order within a shared tier.
            var laneCounters = new Dictionary<int, int>();
            foreach (BuildingDefinition b in _faction.Buildings.OrderBy(b => b.Id, StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(b.Id)) continue;
                int tier = tiers.TryGetValue(b.Id, out int t) ? t : 0;
                int lane = laneCounters.TryGetValue(tier, out int l) ? l : 0;
                laneCounters[tier] = lane + 1;

                var node = new GraphNode
                {
                    Name  = b.Id,
                    Title = string.IsNullOrEmpty(b.DisplayName) ? b.Id : b.DisplayName,
                };
                node.SetSlot(0, true, 0, PortColorIn, true, 0, PortColorOut);
                node.AddChild(new Label { Text = b.Id });
                node.PositionOffset = new Vector2(tier * TIER_SPACING, lane * LANE_SPACING);
                AttachTip(node, node.Title, "Click to edit this building in the Building Card inspector. Drag from its right port to another node's left port to add a prerequisite edge.");
                _graph.AddChild(node);
            }

            foreach (BuildingDefinition b in _faction.Buildings)
            {
                if (string.IsNullOrEmpty(b.Id)) continue;
                foreach (string prereqId in b.Prerequisites ?? Array.Empty<string>())
                {
                    if (!buildingById.ContainsKey(prereqId)) continue;   // unresolvable id — skipped, mirrors TechTreeLayout
                    _graph.ConnectNode(prereqId, 0, b.Id, 0);
                }
            }

            // ── Story 4.11: research nodes ─────────────────────────────────────────
            // No TechTreeLayout equivalent exists for the mixed building-or-research prerequisite graph, so research
            // nodes lay out in their own single lane column, one tier-slot to the right of every building tier
            // (stable ascending-id order, mirroring the building lane's own ordering convention) — simple and
            // deterministic (a reload always redraws identically), not a full tiered layout.
            List<ResearchDefinition> researchList = _faction.Research ?? new List<ResearchDefinition>();
            var researchById = new Dictionary<string, ResearchDefinition>();
            foreach (ResearchDefinition? r in researchList)
                if (r != null && !string.IsNullOrEmpty(r.Id)) researchById[r.Id] = r;

            int buildingMaxTier = tiers.Count > 0 ? tiers.Values.Max() : -1;
            float researchColumnX = (buildingMaxTier + 1) * TIER_SPACING;
            int researchLane = 0;
            foreach (ResearchDefinition r in researchById.Values.OrderBy(r => r.Id, StringComparer.Ordinal))
            {
                var node = new GraphNode
                {
                    Name  = r.Id,
                    Title = string.IsNullOrEmpty(r.DisplayName) ? r.Id : r.DisplayName,
                };
                node.SetSlot(0, true, 0, PortColorResearch, true, 0, PortColorResearch);
                node.AddChild(new Label { Text = r.Id });
                node.PositionOffset = new Vector2(researchColumnX, researchLane * LANE_SPACING);
                AttachTip(node, node.Title, "Click to edit this research entry in the Research Card inspector. Drag from its right port to another node's left port to add a prerequisite edge.");
                _graph.AddChild(node);
                researchLane++;
            }

            foreach (ResearchDefinition? r in researchList)
            {
                if (r == null || string.IsNullOrEmpty(r.Id)) continue;
                foreach (string prereqId in r.Prerequisites ?? Array.Empty<string>())
                {
                    // A research prerequisite resolves against the UNION of building ids and research ids
                    // (ResearchSystem.PrerequisitesMet's resolution order) — either is a valid edge source.
                    if (!buildingById.ContainsKey(prereqId) && !researchById.ContainsKey(prereqId)) continue;
                    // Review-pass fix: a self-referencing prerequisite (only reachable via hand-edited JSON — the
                    // panel's own drop-time validation already rejects this as a 1-node cycle) would otherwise
                    // connect a GraphNode to itself on the same slot, which is undefined GraphEdit behavior.
                    if (prereqId == r.Id) continue;
                    _graph.ConnectNode(prereqId, 0, r.Id, 0);
                }
            }
        }

        // ── Connection handling ──────────────────────────────────────────────────

        /// <summary>Dragging an out-port onto another node. Validates BEFORE drawing the edge or mutating any data —
        /// a rejected drop never reaches <see cref="GraphEdit.ConnectNode"/>.</summary>
        private void OnConnectionRequest(StringName fromNode, long fromPort, StringName toNode, long toPort)
        {
            if (_faction == null) return;
            string sourceId = fromNode.ToString();
            string targetId = toNode.ToString();

            // Story 4.11: research-id resolution checked FIRST (the more specific match, mirrors
            // ResearchSystem.PrerequisitesMet's own resolution order) — a dragged edge landing on a RESEARCH node
            // appends to ITS Prerequisites, never a BuildingDefinition's.
            ResearchDefinition? researchTarget = _faction.Research?.FirstOrDefault(r => r != null && r.Id == targetId);
            if (researchTarget != null)
            {
                OnResearchConnectionRequest(fromNode, fromPort, toNode, toPort, sourceId, targetId, researchTarget);
                return;
            }

            BuildingDefinition? target = _faction.Buildings.FirstOrDefault(b => b.Id == targetId);
            if (target == null) return;   // defensive — every node in the graph resolves to a loaded building or research entry

            // Review-pass fix (Story 4.11): TechTreeValidator.ValidateProposedEdge now rejects a non-building
            // sourceId inline (research nodes share this graph's port type, so a research-sourced edge can land
            // here) — see its own doc comment for why.
            string? cycleError = TechTreeValidator.ValidateProposedEdge(_faction, sourceId, targetId);
            if (cycleError != null)
            {
                ShowError(cycleError);
                return;   // rejected inline — no connection drawn, no data mutated
            }

            bool already = (target.Prerequisites ?? Array.Empty<string>()).Contains(sourceId);
            if (!already)
            {
                string[]? beforeEdit = target.Prerequisites;
                target.Prerequisites = (target.Prerequisites ?? Array.Empty<string>()).Append(sourceId).ToArray();
                if (!Persist())
                {
                    // Roll back so in-memory state never outruns what's actually on disk (e.g. a transient disk I/O
                    // failure) — otherwise the next RebuildGraph() would draw an edge that was never actually saved.
                    // null-forgiving: restores the exact original reference (including null), matching
                    // ValidateProposedEdge's identical restore convention.
                    target.Prerequisites = beforeEdit!;
                    return;   // persistence failure already surfaced via ShowError
                }
            }

            _graph.ConnectNode(fromNode, (int)fromPort, toNode, (int)toPort);
            ClearStatus();
        }

        /// <summary>Story 4.11: the research counterpart of the building-target half of <see cref="OnConnectionRequest"/>
        /// above — validates via <see cref="ResearchValidator.ValidateProposedEdge"/> (cycle OR unknown-id rejection,
        /// identical wording to the import-time lint) BEFORE drawing the edge or mutating any data.</summary>
        private void OnResearchConnectionRequest(StringName fromNode, long fromPort, StringName toNode, long toPort,
            string sourceId, string targetId, ResearchDefinition target)
        {
            string? error = ResearchValidator.ValidateProposedEdge(_faction!, sourceId, targetId);
            if (error != null)
            {
                ShowError(error);
                return;   // rejected inline — no connection drawn, no data mutated
            }

            bool already = (target.Prerequisites ?? Array.Empty<string>()).Contains(sourceId);
            if (!already)
            {
                string[]? beforeEdit = target.Prerequisites;
                target.Prerequisites = (target.Prerequisites ?? Array.Empty<string>()).Append(sourceId).ToArray();
                if (!Persist())
                {
                    target.Prerequisites = beforeEdit!;   // roll back — mirrors OnConnectionRequest's reasoning
                    return;
                }
            }

            _graph.ConnectNode(fromNode, (int)fromPort, toNode, (int)toPort);
            ClearStatus();
        }

        /// <summary>Deleting an existing edge (drag the connection away from its port).</summary>
        private void OnDisconnectionRequest(StringName fromNode, long fromPort, StringName toNode, long toPort)
        {
            if (_faction == null) return;
            string sourceId = fromNode.ToString();
            string targetId = toNode.ToString();

            // Story 4.11: research-id resolution checked first — mirrors OnConnectionRequest's routing.
            ResearchDefinition? researchTarget = _faction.Research?.FirstOrDefault(r => r != null && r.Id == targetId);
            if (researchTarget != null)
            {
                if (researchTarget.Prerequisites != null)
                {
                    string[] beforeEdit = researchTarget.Prerequisites;
                    researchTarget.Prerequisites = researchTarget.Prerequisites.Where(id => id != sourceId).ToArray();
                    if (!Persist())
                    {
                        researchTarget.Prerequisites = beforeEdit;
                        return;
                    }
                }
                _graph.DisconnectNode(fromNode, (int)fromPort, toNode, (int)toPort);
                ClearStatus();
                return;
            }

            BuildingDefinition? target = _faction.Buildings.FirstOrDefault(b => b.Id == targetId);
            if (target != null && target.Prerequisites != null)
            {
                string[] beforeEdit = target.Prerequisites;
                target.Prerequisites = target.Prerequisites.Where(id => id != sourceId).ToArray();
                if (!Persist())
                {
                    // Roll back — same reasoning as OnConnectionRequest: never let in-memory drift from disk.
                    target.Prerequisites = beforeEdit;
                    return;
                }
            }

            _graph.DisconnectNode(fromNode, (int)fromPort, toNode, (int)toPort);
            ClearStatus();
        }

        /// <summary>Clicking a node opens the shared Building Card inspector — or, for a research node (Story
        /// 4.11), this panel's own sibling <see cref="ResearchCardPanel"/> — bound to it. Research-id resolution
        /// checked first, mirroring <see cref="OnConnectionRequest"/>'s routing.</summary>
        private void OnNodeSelected(Node node)
        {
            if (_faction == null) return;
            string id = node.Name.ToString();

            // DW-89: resolve last-wins (LastOrDefault) so a clicked node binds the SAME def RebuildGraph rendered —
            // RebuildGraph builds its researchById/buildingById dictionaries with a last-wins indexer assignment, so a
            // FirstOrDefault here would mismatch it when two entries share an id (a state the cross-namespace/duplicate
            // validators now block on save, but which can still occur mid-edit before a Save).
            ResearchDefinition? research = _faction.Research?.LastOrDefault(r => r != null && r.Id == id);
            if (research != null) { _researchInspector.SelectAndShow(research); return; }

            if (_inspector == null) return;
            BuildingDefinition? building = _faction.Buildings?.LastOrDefault(b => b != null && b.Id == id);
            if (building != null) _inspector.SelectAndShow(building);
        }

        // ── Persistence (mirrors BuildingCardPanel.Edit.cs's PersistSync — duplicated, not shared, matching this
        //    codebase's existing per-editor mirroring precedent) ──────────────────────────────────────────────────

        private bool Persist()
        {
            if (_faction == null) return false;
            if (string.IsNullOrEmpty(_factionJsonPath)) { ShowError("No faction file is bound — cannot save."); return false; }

            string abs = ProjectSettings.GlobalizePath(_factionJsonPath);
            string tmp = abs + ".tmp";
            try
            {
                string current = File.ReadAllText(abs);
                string patched = FactionWriter.SyncFactionBuildings(current, _faction.Buildings);
                // Story 4.11: also sync any research-prerequisite edge edit in the SAME write (one self-check, one
                // atomic move) — SyncFactionResearch only rewrites when a research entry's fields actually differ,
                // so a pure building-edge edit here is a no-op pass over root["research"].
                patched = FactionWriter.SyncFactionResearch(patched, _faction.Research ?? new List<ResearchDefinition>());
                File.WriteAllText(tmp, patched);
                _ = FactionDefinition.LoadFromFile(tmp);   // self-check: refuse to report success for a file that won't reload
                File.Move(tmp, abs, overwrite: true);
                GD.Print($"[TechTree] Saved {abs} ({_faction.Buildings.Count} buildings, {_faction.Research?.Count ?? 0} research).");
                return true;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* leave no stray .tmp */ }
                ShowError($"Save failed: {ex.Message}");
                return false;
            }
        }

        // ── Status line ──────────────────────────────────────────────────────────

        private void ShowError(string msg)
        {
            _statusLabel.Visible = true;
            _statusLabel.Text = msg;
            _statusLabel.AddThemeColorOverride("font_color", _theme.GetColor(ThemeTokens.Danger, ThemeTokens.Type));
        }

        private void ClearStatus()
        {
            _statusLabel.Visible = false;
        }

        /// <summary>Attach a hover-AND-keyboard-focus tooltip (AC3 / UX-DR53 / NFR-2). Thin forwarder to the
        /// centralized <see cref="ChimeraTooltip.AttachFocusable"/> (Story 5.9 review pass).</summary>
        private void AttachTip(Control target, string term, string body, ChimeraTooltip.TooltipRole role = ChimeraTooltip.TooltipRole.Pop)
            => ChimeraTooltip.AttachFocusable(target, term, body, role);
    }
}
