#nullable enable
using System.Collections.Generic;
using Godot;
using ProjectChimera.Core.Definitions;
using ProjectChimera.UI;
using ProjectChimera.UI.Components;   // ChimeraTooltip

namespace ProjectChimera.CreationSuite
{
    /// <summary>
    /// Story 6.6 — the in-game water tool: drag an axis-aligned rectangle to author a cheap <see cref="ScenarioWater"/>
    /// volume (a visual plane at level <c>y</c> + an auto-impassable footprint). NO fluid sim — the rect's cells union
    /// into 6.5's <c>PathabilityGrid</c> at load (units route around, the sim honors it) and fold into
    /// <see cref="CanonicalModelHash"/>; removing a volume un-stamps for free because the grid is rebuilt from source
    /// each load.
    ///
    /// <para>Press <b>N</b> in Edit mode to toggle. Drag a rect on the ground (mouse-down corner A → motion updates
    /// the ghost → mouse-up commits corner B), which appends a volume at the panel's water level. The right-dock panel
    /// lists volumes, sets the level, and deletes the selected one. Each add/delete pushes exactly one
    /// <see cref="EditorHistory"/> (redo, undo) pair onto the SHARED editor stack. The visual planes render in BOTH
    /// Edit and Play (they are part of the map); only the tool chrome + drag ghost are Edit-only.</para>
    /// </summary>
    public partial class WaterTool : Node
    {
        private RtsCameraController? _camCtrl;
        private GameState?           _gameState;
        private ScenarioData?        _scenario;
        private EditorHistory?       _history;

        private const float MIN_EXTENT = 0.5f;

        // ── Tool state ────────────────────────────────────────────────────────
        private bool    _toolActive = false; // toggled by N
        private bool    _dragging   = false;
        private bool    _gridSnap   = false;
        private float   _waterLevel = -0.5f; // authored Y for the next volume
        private Vector3 _dragStart, _dragCurrent;
        private int     _selected   = -1;

        // ── 3D overlay ────────────────────────────────────────────────────────
        private Node3D?         _overlayRoot;
        private MeshInstance3D? _ghost;

        // ── Panel UI ──────────────────────────────────────────────────────────
        private CanvasLayer?    _canvas;
        private PanelContainer? _panel;
        private VBoxContainer?  _listBox;
        private SpinBox?        _levelSpin;
        private Label?          _statusLabel;

        public void Initialize(RtsCameraController camCtrl, GameState gameState, ScenarioData? scenario, EditorHistory? history)
        {
            _camCtrl   = camCtrl;
            _gameState = gameState;
            _scenario  = scenario;
            _history   = history;

            BuildOverlay();
            BuildUi();
            RebuildList();
            RebuildOverlay();
            UpdateStatus();
        }

        // ── Godot lifecycle ───────────────────────────────────────────────────

        public override void _Process(double delta)
        {
            bool inEdit = _gameState != null && _gameState.Mode == GameMode.Edit;
            if (!inEdit && _dragging) CancelDrag();
            if (_canvas != null) _canvas.Visible = inEdit && _toolActive;
            // The drag ghost is Edit-only; committed water planes stay visible in BOTH modes (part of the map).
            if (_ghost != null && !inEdit) _ghost.Visible = false;
        }

        public override void _ExitTree()
        {
            if (_overlayRoot != null) { _overlayRoot.QueueFree(); _overlayRoot = null; }
        }

        public override void _UnhandledInput(InputEvent @event)
        {
            if (_gameState == null || _gameState.Mode != GameMode.Edit) return;
            if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.N)
            {
                _toolActive = !_dragging && !_toolActive;
                if (!_toolActive) CancelDrag();
                GD.Print(_toolActive ? "[WaterTool] Active — drag a water rect | panel level | G grid-snap | N exit" : "[WaterTool] Inactive.");
                GetViewport().SetInputAsHandled();
            }
        }

        public override void _Input(InputEvent @event)
        {
            if (!_toolActive || _gameState == null || _gameState.Mode != GameMode.Edit) return;

            if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.G)
            {
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
                    if (p != null) { _dragging = true; _dragStart = p.Value; _dragCurrent = p.Value; UpdateGhost(); GetViewport().SetInputAsHandled(); }
                }
                else if (!mb.Pressed && _dragging) { CommitDrag(); GetViewport().SetInputAsHandled(); }
            }
            else if (@event is InputEventMouseMotion motion && _dragging)
            {
                var p = GroundPoint(motion.Position);
                if (p != null) { _dragCurrent = p.Value; UpdateGhost(); }
                GetViewport().SetInputAsHandled();
            }
        }

        // ── Drag → commit ─────────────────────────────────────────────────────

        private void CancelDrag() { _dragging = false; if (_ghost != null) _ghost.Visible = false; }

        private void CommitDrag()
        {
            _dragging = false;
            if (_ghost != null) _ghost.Visible = false;
            if (_scenario == null) { GD.Print("[WaterTool] No scenario loaded — water not persisted."); return; }

            float minX = Mathf.Min(_dragStart.X, _dragCurrent.X);
            float maxX = Mathf.Max(_dragStart.X, _dragCurrent.X);
            float minZ = Mathf.Min(_dragStart.Z, _dragCurrent.Z);
            float maxZ = Mathf.Max(_dragStart.Z, _dragCurrent.Z);
            if (maxX - minX < MIN_EXTENT || maxZ - minZ < MIN_EXTENT) { GD.Print("[WaterTool] Rect too small — ignored."); return; }

            float b = _scenario.MapBounds;
            if (minX < -b || maxX > b || minZ < -b || maxZ > b)
            {
                SetStatus($"Water outside map bounds (±{b:F0}) — not added.");
                return;
            }

            var water = new ScenarioWater { X = minX, Z = minZ, W = maxX - minX, H = maxZ - minZ, Y = _waterLevel };
            AddWater(water);
            int addedIndex = IndexOf(water);
            _selected = addedIndex;
            RefreshAll();

            _history?.Push(
                redo: () => { AddWater(water, addedIndex); _selected = IndexOf(water); RefreshAll(); },
                undo: () => { RemoveWater(water); _selected = -1; RefreshAll(); });

            GD.Print($"[WaterTool] Added water [{minX:F1},{minZ:F1} → {maxX:F1},{maxZ:F1}] y={_waterLevel:F1}.");
        }

        private void DeleteSelected()
        {
            var vols = _scenario?.Water;
            if (vols == null || _selected < 0 || _selected >= vols.Length) return;
            int originalIndex = _selected;
            ScenarioWater water = vols[_selected];

            RemoveWater(water);
            _selected = -1;
            RefreshAll();

            _history?.Push(
                redo: () => { RemoveWater(water); _selected = -1; RefreshAll(); },
                undo: () => { AddWater(water, originalIndex); _selected = IndexOf(water); RefreshAll(); });

            GD.Print("[WaterTool] Deleted water volume.");
        }

        // ── ScenarioData.Water mutation (empty → null so the key is omitted) ───────────────────────────────────

        private int WaterCount() => _scenario?.Water?.Length ?? 0;

        private void AddWater(ScenarioWater water, int index = -1)
        {
            if (_scenario == null) return;
            var list = new List<ScenarioWater>(_scenario.Water ?? System.Array.Empty<ScenarioWater>());
            if (index < 0 || index > list.Count) list.Add(water);
            else list.Insert(index, water);
            _scenario.Water = list.ToArray();
        }

        private void RemoveWater(ScenarioWater water)
        {
            if (_scenario?.Water == null) return;
            var list = new List<ScenarioWater>(_scenario.Water);
            list.Remove(water);
            _scenario.Water = list.Count == 0 ? null : list.ToArray();
        }

        private int IndexOf(ScenarioWater water)
        {
            var vols = _scenario?.Water;
            if (vols == null) return -1;
            for (int i = 0; i < vols.Length; i++) if (ReferenceEquals(vols[i], water)) return i;
            return -1;
        }

        // ── Raycast / snap ────────────────────────────────────────────────────

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

        private bool IsOverPanel(Vector2 screenPos) => _panel != null && _panel.GetGlobalRect().HasPoint(screenPos);

        // ── 3D overlay ────────────────────────────────────────────────────────

        private void BuildOverlay()
        {
            _overlayRoot = new Node3D { Name = "WaterOverlay" };
            GetParent()?.AddChild(_overlayRoot);

            _ghost = MakePlane(new Color(0.3f, 0.6f, 1f, 0.35f));
            _ghost.Visible = false;
            _overlayRoot.AddChild(_ghost);
        }

        private void RebuildOverlay()
        {
            if (_overlayRoot == null) return;
            foreach (Node child in _overlayRoot.GetChildren())
            {
                if (child == _ghost) continue;
                _overlayRoot.RemoveChild(child);
                child.QueueFree();
            }

            var vols = _scenario?.Water;
            if (vols == null) return;
            for (int i = 0; i < vols.Length; i++)
            {
                ScenarioWater w = vols[i];
                var mi = MakePlane(i == _selected ? new Color(0.55f, 0.8f, 1f, 0.5f) : new Color(0.2f, 0.45f, 0.9f, 0.45f));
                SetPlaneBounds(mi, w.X, w.Z, w.X + w.W, w.Z + w.H, w.Y);
                _overlayRoot.AddChild(mi);
            }
        }

        private static MeshInstance3D MakePlane(Color color)
        {
            var mi = new MeshInstance3D
            {
                CastShadow       = GeometryInstance3D.ShadowCastingSetting.Off,
                Mesh             = new PlaneMesh { Size = new Vector2(1f, 1f) },
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode  = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    AlbedoColor  = color,
                    CullMode     = BaseMaterial3D.CullModeEnum.Disabled,
                },
            };
            return mi;
        }

        private static void SetPlaneBounds(MeshInstance3D mi, float minX, float minZ, float maxX, float maxZ, float y)
        {
            float w = Mathf.Max(0.01f, maxX - minX);
            float h = Mathf.Max(0.01f, maxZ - minZ);
            mi.Mesh = new PlaneMesh { Size = new Vector2(w, h) };
            mi.Position = new Vector3((minX + maxX) * 0.5f, y, (minZ + maxZ) * 0.5f);
        }

        private void UpdateGhost()
        {
            if (_ghost == null) return;
            float minX = Mathf.Min(_dragStart.X, _dragCurrent.X);
            float maxX = Mathf.Max(_dragStart.X, _dragCurrent.X);
            float minZ = Mathf.Min(_dragStart.Z, _dragCurrent.Z);
            float maxZ = Mathf.Max(_dragStart.Z, _dragCurrent.Z);
            SetPlaneBounds(_ghost, minX, minZ, maxX, maxZ, _waterLevel);
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
                MouseFilter    = Control.MouseFilterEnum.Stop,
            };
            anchorRoot.AddChild(_panel);

            var vbox = new VBoxContainer();
            vbox.AddThemeConstantOverride("separation", 6);
            _panel.AddChild(vbox);

            var title = new Label { Text = "WATER" };
            title.AddThemeFontSizeOverride("font_size", 13);
            vbox.AddChild(title);

            var levelRow = new HBoxContainer();
            levelRow.AddChild(new Label { Text = "Level Y:", CustomMinimumSize = new Vector2(60, 0) });
            _levelSpin = new SpinBox { MinValue = -50, MaxValue = 50, Step = 0.25, Value = _waterLevel, CustomMinimumSize = new Vector2(110, 0) };
            _levelSpin.ValueChanged += v => { _waterLevel = (float)v; UpdateGhost(); };
            AttachTip(_levelSpin, "Water level", "World Y of the water surface plane for the next volume you draw.");
            levelRow.AddChild(_levelSpin);
            vbox.AddChild(levelRow);

            vbox.AddChild(new Label { Text = "Water volumes:" });
            _listBox = new VBoxContainer();
            _listBox.AddThemeConstantOverride("separation", 2);
            vbox.AddChild(_listBox);

            var delBtn = new Button { Text = "Delete selected" };
            delBtn.Pressed += DeleteSelected;
            AttachTip(delBtn, "Delete water", "Remove the selected water volume (one undo step).");
            vbox.AddChild(delBtn);

            _statusLabel = new Label { Text = "" };
            _statusLabel.AddThemeFontSizeOverride("font_size", 11);
            vbox.AddChild(_statusLabel);
        }

        private void RebuildList()
        {
            if (_listBox == null) return;
            foreach (Node c in _listBox.GetChildren()) { _listBox.RemoveChild(c); c.QueueFree(); }

            var vols = _scenario?.Water;
            if (vols == null || vols.Length == 0) { _listBox.AddChild(new Label { Text = "(none yet)" }); return; }

            var group = new ButtonGroup();
            for (int i = 0; i < vols.Length; i++)
            {
                int idx = i;
                var w = vols[i];
                var btn = new Button
                {
                    Text          = $"Water {i + 1}  [{w.W:F0}×{w.H:F0} @y{w.Y:F1}]",
                    ToggleMode    = true,
                    ButtonGroup   = group,
                    ButtonPressed = (i == _selected),
                    Alignment     = HorizontalAlignment.Left,
                };
                btn.Pressed += () => { _selected = idx; RebuildOverlay(); UpdateStatus(); };
                _listBox.AddChild(btn);
            }
        }

        private void RefreshAll()
        {
            RebuildList();
            RebuildOverlay();
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            if (_statusLabel == null) return;
            _statusLabel.Text = $"Volumes: {WaterCount()}   Grid-snap: {(_gridSnap ? "ON" : "OFF")}   [G]";
        }

        private void SetStatus(string msg) { if (_statusLabel != null) _statusLabel.Text = msg; }

        private static void AttachTip(Control target, string term, string body,
                                      ChimeraTooltip.TooltipRole role = ChimeraTooltip.TooltipRole.Pop)
            => ChimeraTooltip.AttachFocusable(target, term, body, role);
    }
}
