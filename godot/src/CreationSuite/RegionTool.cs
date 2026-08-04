#nullable enable
using System.Collections.Generic;
using Godot;
using ProjectChimera.Core.Definitions;
using ProjectChimera.UI;
using ProjectChimera.UI.Components;   // ChimeraTabs, ChimeraTooltip

namespace ProjectChimera.CreationSuite
{
    /// <summary>
    /// Story 6.4 — the in-game Region draw tool: authors named, rectangular map areas (<see cref="ScenarioRegion"/>)
    /// that a <c>unit_in_region</c> trigger condition references and Epic 7's win-condition presets bind to.
    ///
    /// <para>Press <b>I</b> in Edit mode to toggle. Active state: drag a rectangle on the ground (mouse-down corner A
    /// → motion updates the ghost rect → mouse-up commits corner B), which appends a <see cref="ScenarioRegion"/> to
    /// <see cref="ScenarioData.Regions"/> under the name in the panel field. The right-dock panel lists regions, lets
    /// you rename/resize/delete the selected one (Simple = name + delete; Advanced = raw min/max coords), and shares
    /// the SINGLE injected <see cref="EditorHistory"/> so add/delete/resize interleave LIFO with entity place/delete
    /// undo/redo (Ctrl+Z / Ctrl+Y, handled by <see cref="ProjectChimera.UI.EntityPlacer"/>) with no cross-corruption.
    /// <c>G</c> toggles grid-snap (mirrors the placer). An <see cref="IsOverPanel"/> + <c>MouseFilter.Stop</c> guard
    /// prevents drawing under the panel.</para>
    ///
    /// <para>Each region renders as a labeled rectangle outline — a <see cref="MeshInstance3D"/> <see cref="ImmediateMesh"/>
    /// line-loop (four corners at Y≈0, unshaded emissive per-region color) plus a <see cref="Label3D"/> name — gated on
    /// <see cref="GameMode.Edit"/> (hidden in Play). Architecture: presentation layer only; the sim consumes the
    /// resolved <c>RegionStore</c> built once at scenario-apply, never a Godot type.</para>
    /// </summary>
    public partial class RegionTool : Node
    {
        // ── Dependencies ──────────────────────────────────────────────────────
        private RtsCameraController? _camCtrl;
        private GameState?           _gameState;
        private ScenarioData?        _scenario;
        private EditorHistory?       _history;

        // Minimum rect extent on each axis (world units). Centralized so CommitDrag and ApplyAdvancedResize agree on
        // the same reject threshold; the validator independently requires the stronger MinX < MaxX && MinZ < MaxZ.
        private const float MIN_EXTENT = 0.01f;

        // ── Tool state ────────────────────────────────────────────────────────
        private bool    _toolActive     = false; // toggled by I
        private bool    _dragging       = false;
        private bool    _gridSnap       = false;
        private Vector3 _dragStart;               // corner A (world, Y=0)
        private Vector3 _dragCurrent;             // live corner B during a drag
        private int     _selected       = -1;     // index into _scenario.Regions of the selected region (-1 = none)

        // ── 3D overlay ────────────────────────────────────────────────────────
        private Node3D?         _overlayRoot;     // parent of the per-region line-loops + labels + the drag ghost
        private MeshInstance3D? _ghost;           // live drag preview rect

        // Per-region emissive colors, cycled by index so adjacent regions are distinguishable.
        private static readonly Color[] REGION_COLORS =
        {
            new(0.30f, 0.80f, 1.00f), // cyan
            new(1.00f, 0.75f, 0.25f), // amber
            new(0.55f, 1.00f, 0.45f), // green
            new(1.00f, 0.45f, 0.75f), // pink
            new(0.80f, 0.55f, 1.00f), // violet
        };

        // ── Panel UI ──────────────────────────────────────────────────────────
        private CanvasLayer?    _canvas;
        private PanelContainer? _panel;
        private VBoxContainer?  _listBox;
        private LineEdit?       _nameField;
        private ChimeraTabs?    _tabs;            // Simple / Advanced
        private VBoxContainer?  _advancedBox;     // raw min/max coord editors (Advanced only)
        private SpinBox?        _minXSpin, _minZSpin, _maxXSpin, _maxZSpin;
        private Label?          _statusLabel;

        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Wire dependencies and build the overlay + panel. Called once from the RegionTool phase after the
        /// camera, game state, and (loaded) scenario exist. <paramref name="history"/> is the SHARED editor stack.</summary>
        public void Initialize(RtsCameraController camCtrl, GameState gameState,
                               ScenarioData? scenario, EditorHistory? history)
        {
            _camCtrl   = camCtrl;
            _gameState = gameState;
            _scenario  = scenario;
            _history   = history;

            BuildOverlay();
            BuildUi();
            RebuildRegionList();
            RebuildOverlay();
        }

        // ── Godot lifecycle ───────────────────────────────────────────────────

        public override void _Process(double delta)
        {
            bool inEdit = _gameState != null && _gameState.Mode == GameMode.Edit;

            // Review patch P9(a): leaving Edit mode mid-drag must cancel the in-progress drag, so returning to Edit
            // does not commit a stale rect drawn against the previous mode.
            if (!inEdit && _dragging) CancelDrag();

            if (_canvas != null)
                _canvas.Visible = inEdit && _toolActive;

            // Overlay is Edit-mode-ONLY (hidden entirely in Play).
            if (_overlayRoot != null)
                _overlayRoot.Visible = inEdit;
        }

        /// <summary>Review patch P7: <see cref="_overlayRoot"/> is parented under <c>GetParent()</c> (the scene), NOT
        /// under this tool, so it is not auto-freed when the tool leaves the tree. Free it here so repeated Edit-mode
        /// entry / scene reloads don't leak overlay geometry.</summary>
        public override void _ExitTree()
        {
            if (_overlayRoot != null)
            {
                _overlayRoot.QueueFree();
                _overlayRoot = null;
            }
        }

        /// <summary>I toggles the tool on/off. Lower priority than <see cref="_Input"/> so UI sees events first.</summary>
        public override void _UnhandledInput(InputEvent @event)
        {
            if (_gameState == null || _gameState.Mode != GameMode.Edit) return;

            if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.I && !key.CtrlPressed) // Ctrl+I = Item editor
            {
                _toolActive = !_dragging && !_toolActive; // never toggle mid-drag
                if (!_toolActive) CancelDrag();
                GD.Print(_toolActive
                    ? "[RegionTool] Active — drag a rect | name in panel | G grid-snap | I exit"
                    : "[RegionTool] Inactive.");
                GetViewport().SetInputAsHandled();
            }
        }

        /// <summary>
        /// While active, intercept the drag (LMB) and grid-snap (G) before the placer/selection see them. Uses
        /// <see cref="_Input"/> (fires before <c>_UnhandledInput</c>) exactly like <see cref="TerrainBrush"/>.
        /// </summary>
        public override void _Input(InputEvent @event)
        {
            if (!_toolActive || _gameState == null || _gameState.Mode != GameMode.Edit) return;

            if (@event is InputEventKey && ProjectChimera.UI.TextFocusGuard.IsTyping(this)) return; // hotkeys must not fire while typing
            if (@event is InputEventKey ck && ck.Pressed && ck.CtrlPressed
                && ck.Keycode >= Key.A && ck.Keycode <= Key.Z) return;   // Ctrl+<letter> = editor tier
            if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.G)
            {
                // Review patch P9(b): ignore the grid-snap toggle mid-drag so an in-progress drag keeps a
                // consistent snap state for BOTH corners (still consume it so the placer doesn't toggle either).
                if (_dragging) { GetViewport().SetInputAsHandled(); return; }
                _gridSnap = !_gridSnap;
                UpdateStatus();
                GetViewport().SetInputAsHandled();
                return;
            }

            if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed && !IsOverPanel(mb.Position))
                {
                    var p = GroundPoint(mb.Position);
                    if (p != null)
                    {
                        _dragging    = true;
                        _dragStart   = p.Value;
                        _dragCurrent = p.Value;
                        UpdateGhost();
                        GetViewport().SetInputAsHandled();
                    }
                }
                else if (!mb.Pressed && _dragging)
                {
                    CommitDrag();
                    GetViewport().SetInputAsHandled();
                }
            }
            else if (@event is InputEventMouseMotion motion && _dragging)
            {
                var p = GroundPoint(motion.Position);
                if (p != null)
                {
                    _dragCurrent = p.Value;
                    UpdateGhost();
                }
                GetViewport().SetInputAsHandled();
            }
        }

        // ── Drag → commit ─────────────────────────────────────────────────────

        private void CancelDrag()
        {
            _dragging = false;
            if (_ghost != null) _ghost.Visible = false;
        }

        /// <summary>Commit the current drag as a new region (undo/redo as one shared-history pair). No-op when the
        /// rect is degenerate (zero area) or no scenario is loaded (a fallback map cannot persist regions).</summary>
        private void CommitDrag()
        {
            _dragging = false;
            if (_ghost != null) _ghost.Visible = false;

            if (_scenario == null) { GD.Print("[RegionTool] No scenario loaded — region not persisted."); return; }

            float minX = Mathf.Min(_dragStart.X, _dragCurrent.X);
            float maxX = Mathf.Max(_dragStart.X, _dragCurrent.X);
            float minZ = Mathf.Min(_dragStart.Z, _dragCurrent.Z);
            float maxZ = Mathf.Max(_dragStart.Z, _dragCurrent.Z);

            // Reject a zero-area rect (validator requires min < max on both axes).
            if (maxX - minX < MIN_EXTENT || maxZ - minZ < MIN_EXTENT) { GD.Print("[RegionTool] Rect too small — ignored."); return; }

            // Review patch P8: reject a rect whose corners fall outside the scenario's ±MapBounds — the SAME bound
            // ScenarioValidator.CheckCoord enforces. Storing an out-of-bounds region would make the whole map
            // unsaveable (the validator later rejects it); surface inline panel feedback instead of silently keeping it.
            if (!WithinMapBounds(minX, minZ, maxX, maxZ))
            {
                SetStatus($"Region outside map bounds (±{_scenario.MapBounds:F0}) — not added.");
                GD.Print("[RegionTool] Rect outside map bounds — ignored.");
                return;
            }

            string name = _nameField?.Text.Trim() ?? "";
            if (string.IsNullOrEmpty(name)) name = $"Region {RegionCount() + 1}";

            var region = new ScenarioRegion
            {
                Id   = GenerateRegionId(),
                Name = name,
                MinX = minX, MinZ = minZ, MaxX = maxX, MaxZ = maxZ,
            };

            AddRegion(region);
            int addedIndex = IndexOf(region); // Review patch P10: capture the ORIGINAL index for order-faithful redo
            _selected = addedIndex;
            RefreshAll();

            _history?.Push(
                redo: () => { AddRegion(region, addedIndex); _selected = IndexOf(region); RefreshAll(); },
                undo: () => { RemoveRegion(region); _selected = -1; RefreshAll(); });

            GD.Print($"[RegionTool] Added region '{region.Name}' ({region.Id}) " +
                     $"[{minX:F1},{minZ:F1} → {maxX:F1},{maxZ:F1}]");
        }

        // ── ScenarioData.Regions mutation (rebuilds the array; empty → null so the key is omitted) ────────────

        private int RegionCount() => _scenario?.Regions?.Length ?? 0;

        /// <summary>Insert <paramref name="region"/> at <paramref name="index"/> (or append when index is out of
        /// range / -1). Review patch P10: an explicit index lets undo/redo restore a region at its ORIGINAL
        /// position, so index-derived overlay colors and on-disk order stay stable across undo/redo.</summary>
        private void AddRegion(ScenarioRegion region, int index = -1)
        {
            if (_scenario == null) return;
            var list = new List<ScenarioRegion>(_scenario.Regions ?? System.Array.Empty<ScenarioRegion>());
            if (index < 0 || index > list.Count) list.Add(region);
            else list.Insert(index, region);
            _scenario.Regions = list.ToArray();
        }

        private void RemoveRegion(ScenarioRegion region)
        {
            if (_scenario?.Regions == null) return;
            var list = new List<ScenarioRegion>(_scenario.Regions);
            list.Remove(region);
            // Empty collection → null so it is OMITTED from serialization (byte-identical to pre-feature).
            _scenario.Regions = list.Count == 0 ? null : list.ToArray();
        }

        private int IndexOf(ScenarioRegion region)
        {
            var regions = _scenario?.Regions;
            if (regions == null) return -1;
            for (int i = 0; i < regions.Length; i++)
                if (ReferenceEquals(regions[i], region)) return i;
            return -1;
        }

        /// <summary>Generate a unique region id ("region_N") not colliding with any existing one.</summary>
        private string GenerateRegionId()
        {
            var existing = new HashSet<string>();
            if (_scenario?.Regions != null)
                foreach (var r in _scenario.Regions) existing.Add(r.Id);
            int n = existing.Count + 1;
            string id;
            do { id = $"region_{n++}"; } while (existing.Contains(id));
            return id;
        }

        // ── Delete / resize the selected region (each one shared-history pair) ─────────────────────────────────

        private void DeleteSelected()
        {
            var regions = _scenario?.Regions;
            if (regions == null || _selected < 0 || _selected >= regions.Length) return;
            int originalIndex = _selected; // Review patch P10: restore at the ORIGINAL index on undo, not the end
            ScenarioRegion region = regions[_selected];

            RemoveRegion(region);
            _selected = -1;
            RefreshAll();

            _history?.Push(
                redo: () => { RemoveRegion(region); _selected = -1; RefreshAll(); },
                undo: () => { AddRegion(region, originalIndex); _selected = IndexOf(region); RefreshAll(); });

            GD.Print($"[RegionTool] Deleted region '{region.Name}' ({region.Id}).");
        }

        /// <summary>Apply the Advanced-tab min/max coords to the selected region as a resize (undo restores the
        /// prior bounds). No-op when nothing is selected or the resulting rect is inverted/degenerate.</summary>
        private void ApplyAdvancedResize()
        {
            var regions = _scenario?.Regions;
            if (regions == null || _selected < 0 || _selected >= regions.Length) return;
            if (_minXSpin == null || _minZSpin == null || _maxXSpin == null || _maxZSpin == null) return;

            ScenarioRegion region = regions[_selected];
            float beforeMinX = region.MinX, beforeMinZ = region.MinZ, beforeMaxX = region.MaxX, beforeMaxZ = region.MaxZ;
            float minX = (float)_minXSpin.Value, minZ = (float)_minZSpin.Value;
            float maxX = (float)_maxXSpin.Value, maxZ = (float)_maxZSpin.Value;

            if (maxX - minX < MIN_EXTENT || maxZ - minZ < MIN_EXTENT) { GD.Print("[RegionTool] Resize rejected — min must be < max."); SetStatus("Resize rejected — min must be < max."); return; }

            // Review patch P8: reject a resize whose corners fall outside the scenario's ±MapBounds (the validator's
            // CheckCoord bound) with inline panel feedback, so a resized region can't quietly make the map unsaveable.
            if (!WithinMapBounds(minX, minZ, maxX, maxZ))
            {
                SetStatus($"Resize outside map bounds (±{_scenario!.MapBounds:F0}) — rejected.");
                GD.Print("[RegionTool] Resize outside map bounds — rejected.");
                return;
            }

            void Set(float aMinX, float aMinZ, float aMaxX, float aMaxZ)
            {
                region.MinX = aMinX; region.MinZ = aMinZ; region.MaxX = aMaxX; region.MaxZ = aMaxZ;
                RefreshAll();
            }

            Set(minX, minZ, maxX, maxZ);
            _history?.Push(
                redo: () => Set(minX, minZ, maxX, maxZ),
                undo: () => Set(beforeMinX, beforeMinZ, beforeMaxX, beforeMaxZ));

            GD.Print($"[RegionTool] Resized region '{region.Name}' → [{minX:F1},{minZ:F1} → {maxX:F1},{maxZ:F1}].");
        }

        // ── Raycast / snap ────────────────────────────────────────────────────

        /// <summary>Cast from the camera through <paramref name="screenPos"/> onto the Y=0 ground plane (the
        /// EntityPlacer approach: <c>origin + dir·(−origin.Y/dir.Y)</c>). Returns null on a miss / parallel ray.</summary>
        private Vector3? GroundPoint(Vector2 screenPos)
        {
            var cam = _camCtrl?.GetCamera();
            if (cam == null) return null;
            var origin = cam.ProjectRayOrigin(screenPos);
            var dir    = cam.ProjectRayNormal(screenPos);
            if (Mathf.Abs(dir.Y) < 0.0001f) return null;
            float t = -origin.Y / dir.Y;
            if (t < 0f) return null;
            var hit = origin + dir * t;
            return new Vector3(Snap(hit.X), 0f, Snap(hit.Z));
        }

        private float Snap(float v) => _gridSnap ? Mathf.Round(v) : v;

        private bool IsOverPanel(Vector2 screenPos)
            => _panel != null && _panel.GetGlobalRect().HasPoint(screenPos);

        // ── 3D overlay construction ───────────────────────────────────────────

        private void BuildOverlay()
        {
            _overlayRoot = new Node3D { Name = "RegionOverlay" };
            GetParent()?.AddChild(_overlayRoot);

            _ghost = MakeRectMesh(new Color(1f, 1f, 1f, 1f));
            _ghost.Visible = false;
            _overlayRoot.AddChild(_ghost);
        }

        /// <summary>Rebuild every region's outline + label from the live <see cref="ScenarioData.Regions"/>.</summary>
        private void RebuildOverlay()
        {
            if (_overlayRoot == null) return;

            // Remove all children except the persistent ghost, then rebuild.
            foreach (Node child in _overlayRoot.GetChildren())
            {
                if (child == _ghost) continue;
                _overlayRoot.RemoveChild(child);
                child.QueueFree();
            }

            var regions = _scenario?.Regions;
            if (regions == null) return;

            for (int i = 0; i < regions.Length; i++)
            {
                ScenarioRegion r = regions[i];
                Color col = REGION_COLORS[i % REGION_COLORS.Length];
                bool sel  = i == _selected;

                var mi = MakeRectMesh(sel ? Colors.White : col);
                SetRectMeshBounds(mi, r.MinX, r.MinZ, r.MaxX, r.MaxZ);
                _overlayRoot.AddChild(mi);

                var label = new Label3D
                {
                    Text                = r.Name,
                    Position            = new Vector3((r.MinX + r.MaxX) * 0.5f, 0.5f, (r.MinZ + r.MaxZ) * 0.5f),
                    Billboard           = BaseMaterial3D.BillboardModeEnum.Enabled,
                    Modulate            = sel ? Colors.White : col,
                    NoDepthTest         = true,
                    FontSize            = 48,
                    PixelSize           = 0.02f,
                };
                _overlayRoot.AddChild(label);
            }
        }

        /// <summary>Create an unshaded emissive line-loop rectangle mesh (a unit 0..1 quad on X/Z; bounds are set
        /// via <see cref="SetRectMeshBounds"/>).</summary>
        private static MeshInstance3D MakeRectMesh(Color color)
        {
            var mi = new MeshInstance3D
            {
                CastShadow      = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode         = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    AlbedoColor         = color,
                    EmissionEnabled     = true,
                    Emission            = color,
                    NoDepthTest         = true,
                },
            };
            SetRectMeshBoundsOn(mi, 0f, 0f, 1f, 1f);
            return mi;
        }

        private void SetRectMeshBounds(MeshInstance3D mi, float minX, float minZ, float maxX, float maxZ)
            => SetRectMeshBoundsOn(mi, minX, minZ, maxX, maxZ);

        /// <summary>Rebuild the line-loop geometry of <paramref name="mi"/> as the rectangle
        /// (minX,minZ)→(maxX,maxZ) at Y≈0.05 (just above ground to avoid z-fighting).</summary>
        private static void SetRectMeshBoundsOn(MeshInstance3D mi, float minX, float minZ, float maxX, float maxZ)
        {
            const float y = 0.05f;
            var mesh = new ImmediateMesh();
            mesh.SurfaceBegin(Mesh.PrimitiveType.LineStrip);
            mesh.SurfaceAddVertex(new Vector3(minX, y, minZ));
            mesh.SurfaceAddVertex(new Vector3(maxX, y, minZ));
            mesh.SurfaceAddVertex(new Vector3(maxX, y, maxZ));
            mesh.SurfaceAddVertex(new Vector3(minX, y, maxZ));
            mesh.SurfaceAddVertex(new Vector3(minX, y, minZ)); // close the loop
            mesh.SurfaceEnd();
            mi.Mesh = mesh;
        }

        private void UpdateGhost()
        {
            if (_ghost == null) return;
            float minX = Mathf.Min(_dragStart.X, _dragCurrent.X);
            float maxX = Mathf.Max(_dragStart.X, _dragCurrent.X);
            float minZ = Mathf.Min(_dragStart.Z, _dragCurrent.Z);
            float maxZ = Mathf.Max(_dragStart.Z, _dragCurrent.Z);
            SetRectMeshBoundsOn(_ghost, minX, minZ, maxX, maxZ);
            _ghost.Visible = true;
        }

        // ── Panel UI ──────────────────────────────────────────────────────────

        private void BuildUi()
        {
            _canvas = new CanvasLayer { Visible = false, Layer = 5 };
            AddChild(_canvas);

            var anchorRoot = new Control();
            anchorRoot.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            anchorRoot.MouseFilter = Control.MouseFilterEnum.Ignore;
            _canvas.AddChild(anchorRoot);

            _panel = new PanelContainer
            {
                AnchorLeft     = 1f,
                AnchorRight    = 1f,
                OffsetLeft     = -300f,
                OffsetRight    = -4f,
                OffsetTop      = 200f,
                GrowHorizontal = Control.GrowDirection.Begin,
                MouseFilter    = Control.MouseFilterEnum.Stop, // guard: never draw a region under the panel
            };
            anchorRoot.AddChild(_panel);

            var vbox = new VBoxContainer();
            vbox.AddThemeConstantOverride("separation", 6);
            _panel.AddChild(vbox);

            var title = new Label { Text = "REGIONS" };
            title.AddThemeFontSizeOverride("font_size", 13);
            vbox.AddChild(title);

            // Name field for the next drawn region.
            var nameRow = new HBoxContainer();
            nameRow.AddChild(new Label { Text = "Name:", CustomMinimumSize = new Vector2(48, 0) });
            _nameField = new LineEdit { PlaceholderText = "region name", CustomMinimumSize = new Vector2(180, 0) };
            AttachTip(_nameField, "Region name", "The display name applied to the next region you draw.", ChimeraTooltip.TooltipRole.Field);
            nameRow.AddChild(_nameField);
            vbox.AddChild(nameRow);

            // Simple / Advanced disclosure.
            _tabs = ChimeraTabs.Create(ChimeraComponents.TabsVariant.Segment, "Simple", "Advanced");
            _tabs.TabChanged += _ => RefreshAdvancedVisibility();
            vbox.AddChild(_tabs);

            // Region list (selectable).
            vbox.AddChild(new Label { Text = "Drawn regions:" });
            _listBox = new VBoxContainer();
            _listBox.AddThemeConstantOverride("separation", 2);
            vbox.AddChild(_listBox);

            // Advanced coord editors (Advanced tab only).
            _advancedBox = new VBoxContainer { Visible = false };
            _advancedBox.AddThemeConstantOverride("separation", 4);
            vbox.AddChild(_advancedBox);

            _minXSpin = MakeCoordRow(_advancedBox, "Min X");
            _minZSpin = MakeCoordRow(_advancedBox, "Min Z");
            _maxXSpin = MakeCoordRow(_advancedBox, "Max X");
            _maxZSpin = MakeCoordRow(_advancedBox, "Max Z");

            var applyBtn = new Button { Text = "Apply resize" };
            applyBtn.Pressed += ApplyAdvancedResize;
            AttachTip(applyBtn, "Apply resize", "Resize the selected region to the min/max coords above (one undo step).");
            _advancedBox.AddChild(applyBtn);

            // Delete selected.
            var delBtn = new Button { Text = "Delete selected" };
            delBtn.Pressed += DeleteSelected;
            AttachTip(delBtn, "Delete region", "Remove the selected region (one undo step).");
            vbox.AddChild(delBtn);

            _statusLabel = new Label { Text = "" };
            _statusLabel.AddThemeFontSizeOverride("font_size", 11);
            vbox.AddChild(_statusLabel);
            UpdateStatus();
        }

        private SpinBox MakeCoordRow(Container parent, string label)
        {
            var row = new HBoxContainer();
            row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(48, 0) });
            var spin = new SpinBox
            {
                MinValue          = -100000,
                MaxValue          =  100000,
                Step              = 0.5,
                CustomMinimumSize = new Vector2(110, 0),
            };
            row.AddChild(spin);
            parent.AddChild(row);
            return spin;
        }

        private void RefreshAdvancedVisibility()
        {
            bool advanced = _tabs != null && _tabs.Active == 1;
            if (_advancedBox != null) _advancedBox.Visible = advanced;
        }

        private void RebuildRegionList()
        {
            if (_listBox == null) return;
            foreach (Node c in _listBox.GetChildren()) { _listBox.RemoveChild(c); c.QueueFree(); }

            var regions = _scenario?.Regions;
            if (regions == null || regions.Length == 0)
            {
                _listBox.AddChild(new Label { Text = "(none yet)" });
                return;
            }

            var group = new ButtonGroup();
            for (int i = 0; i < regions.Length; i++)
            {
                int idx = i;
                var r = regions[i];
                var btn = new Button
                {
                    Text          = $"{r.Name}  [{r.Id}]",
                    ToggleMode    = true,
                    ButtonGroup   = group,
                    ButtonPressed = (i == _selected),
                    Alignment     = HorizontalAlignment.Left,
                };
                btn.Pressed += () => { _selected = idx; SyncAdvancedFields(); RebuildOverlay(); };
                _listBox.AddChild(btn);
            }
        }

        private void SyncAdvancedFields()
        {
            var regions = _scenario?.Regions;
            if (regions == null || _selected < 0 || _selected >= regions.Length) return;
            if (_minXSpin == null || _minZSpin == null || _maxXSpin == null || _maxZSpin == null) return;
            ScenarioRegion r = regions[_selected];
            _minXSpin.Value = r.MinX;
            _minZSpin.Value = r.MinZ;
            _maxXSpin.Value = r.MaxX;
            _maxZSpin.Value = r.MaxZ;
        }

        /// <summary>Refresh every surface that reflects <see cref="ScenarioData.Regions"/>: list, overlay, advanced
        /// fields, status.</summary>
        private void RefreshAll()
        {
            RebuildRegionList();
            RebuildOverlay();
            SyncAdvancedFields();
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            if (_statusLabel == null) return;
            _statusLabel.Text = $"Regions: {RegionCount()}   Grid-snap: {(_gridSnap ? "ON" : "OFF")}   [G]";
        }

        /// <summary>Review patch P8: show a transient message on the panel status line (e.g. an out-of-bounds
        /// rejection). Overwritten by the next <see cref="UpdateStatus"/> / <see cref="RefreshAll"/>.</summary>
        private void SetStatus(string msg)
        {
            if (_statusLabel != null) _statusLabel.Text = msg;
        }

        /// <summary>Review patch P8: true when all four corners lie within the loaded scenario's ±MapBounds — the
        /// SAME bound <see cref="ScenarioValidator"/>'s CheckCoord enforces. Only reached with a non-null scenario
        /// (both call sites bail earlier when none is loaded).</summary>
        private bool WithinMapBounds(float minX, float minZ, float maxX, float maxZ)
        {
            float b = _scenario?.MapBounds ?? 0f;
            return minX >= -b && maxX <= b && minZ >= -b && maxZ <= b;
        }

        private static void AttachTip(Control target, string term, string body,
                                      ChimeraTooltip.TooltipRole role = ChimeraTooltip.TooltipRole.Pop)
            => ChimeraTooltip.AttachFocusable(target, term, body, role);
    }
}
