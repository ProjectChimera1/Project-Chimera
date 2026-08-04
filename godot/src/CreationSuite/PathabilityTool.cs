#nullable enable
using Godot;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Navigation;
using ProjectChimera.UI;
using ProjectChimera.UI.Components;   // ChimeraTooltip

namespace ProjectChimera.CreationSuite
{
    /// <summary>
    /// Story 6.5 — the in-game impassable-terrain paint tool + the WC3 'P' pathability overlay. Authors the
    /// per-cell blocked layer (<see cref="ScenarioData.PathabilityBlocked"/>, a base64 128²/2-unit/±128 bitset that
    /// mirrors <see cref="FlowField.WorldToCell"/>) that the deterministic sim honors and the flow field routes
    /// around.
    ///
    /// <para>Press <b>K</b> in Edit mode to toggle painting. LMB drag stamps a circular brush of blocked cells (or
    /// erases in Erase mode); <c>[</c>/<c>]</c> resize the brush; the right-dock panel toggles Paint/Erase and the
    /// per-map slope-auto-block config (toggle + threshold, written straight into the scenario). Each stroke pushes
    /// exactly one <see cref="EditorHistory"/> redo/undo pair onto the SHARED editor stack, so it interleaves LIFO
    /// with entity / terrain / region undo/redo (Ctrl+Z / Ctrl+Y) with no cross-corruption. An
    /// <see cref="IsOverPanel"/> + <c>MouseFilter.Stop</c> guard prevents painting under the panel.</para>
    ///
    /// <para>The overlay is a SINGLE <see cref="MultiMeshInstance3D"/> of red quads (never per-cell nodes) over the
    /// union of painted ∪ slope-derived cells, gated on <see cref="GameMode.Edit"/> (never visible in Play) and
    /// toggled independently by <b>P</b>. Architecture: presentation only — the sim consumes the resolved
    /// <see cref="PathabilityGrid"/> built once at scenario-load, never a Godot type.</para>
    /// </summary>
    public partial class PathabilityTool : Node
    {
        private const int GS   = FlowField.GRID_SIZE;      // 128
        private const int CS    = FlowField.CELL_SIZE_WORLD; // 2 world units/cell
        private const int HALF  = FlowField.WORLD_HALF_INT;  // 128
        private const int CELLS = FlowField.CELL_COUNT;      // 16384

        // ── Dependencies ──────────────────────────────────────────────────────
        private RtsCameraController? _camCtrl;
        private GameState?           _gameState;
        private ScenarioData?        _scenario;
        private EditorHistory?       _history;

        // ── Tool state ────────────────────────────────────────────────────────
        private bool     _toolActive     = false; // toggled by K
        private bool     _overlayVisible = true;  // toggled by P (independent of the tool)
        private bool     _eraseMode      = false;
        private int      _brushRadius    = 1;      // in cells (radius); brush diameter = 2r+1 cells
        private bool     _painting       = false;

        // The live painted mask (true = blocked) and a read-only snapshot of the slope-DERIVED cells (shown in the
        // overlay but not editable — they are recomputed at load from the terrain heightmap).
        private bool[]   _painted        = new bool[CELLS];
        private bool[]   _derived        = new bool[CELLS];
        private bool[]?  _strokeBefore   = null;   // painted snapshot captured on mouse-down

        // ── 3D overlay (single MultiMesh) ─────────────────────────────────────
        private MultiMeshInstance3D? _overlay;
        private MultiMesh?           _multiMesh;

        // ── Panel UI ──────────────────────────────────────────────────────────
        private CanvasLayer?    _canvas;
        private PanelContainer? _panel;
        private Label?          _statusLabel;
        private CheckButton?    _eraseToggle;
        private CheckBox?       _slopeToggle;
        private SpinBox?        _slopeThreshold;

        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Wire dependencies + build the overlay/panel. Called once from the PathabilityTool phase after the
        /// camera, game state, and (loaded) scenario exist. <paramref name="history"/> is the SHARED editor stack;
        /// <paramref name="initialGrid"/> is the load-time union grid (painted ∪ slope-derived) used to seed both the
        /// live painted mask and the read-only derived-cell overlay.</summary>
        public void Initialize(RtsCameraController camCtrl, GameState gameState, ScenarioData? scenario,
                               EditorHistory? history, PathabilityGrid? initialGrid)
        {
            _camCtrl   = camCtrl;
            _gameState = gameState;
            _scenario  = scenario;
            _history   = history;

            // Seed the live painted mask from the scenario's authored bitset (the source of truth the tool mutates).
            _painted = PathabilityGrid.FromBase64(scenario?.PathabilityBlocked);

            // Derived-only cells = whatever the load-time union blocked that the painted mask did NOT. Snapshot them
            // read-only so the overlay shows steep slope cells even though they are not directly editable.
            if (initialGrid != null && initialGrid.AnyBlocked)
                for (int i = 0; i < CELLS; i++)
                    _derived[i] = initialGrid.Blocked[i] && !_painted[i];

            BuildOverlay();
            BuildUi();
            SyncSlopeFields();
            RebuildOverlay();
            UpdateStatus();
        }

        // ── Godot lifecycle ───────────────────────────────────────────────────

        /// <summary>Whether the pathability overlay is currently shown (the independent <c>P</c> toggle — the tool
        /// itself does not have to be active). Read by the Edit-mode hotkey strip so the toggle reports live state
        /// instead of being invisible: the feature shipped working but unadvertised, and Alec asked for a WC3-style
        /// pathing view on 2026-08-04 not knowing it was already bound.</summary>
        public bool OverlayVisible => _overlayVisible;

        public override void _Process(double delta)
        {
            bool inEdit = _gameState != null && _gameState.Mode == GameMode.Edit;

            if (!inEdit && _painting) CancelStroke(); // leaving Edit mid-stroke cancels it

            if (_canvas != null) _canvas.Visible = inEdit && _toolActive;
            // Overlay is Edit-mode-ONLY (never visible in Play) AND gated on the independent P toggle.
            if (_overlay != null) _overlay.Visible = inEdit && _overlayVisible;
        }

        /// <summary>The overlay is parented under the scene (not this tool), so free it here to avoid leaking geometry
        /// across repeated Edit-mode entry / scene reloads.</summary>
        public override void _ExitTree()
        {
            if (_overlay != null) { _overlay.QueueFree(); _overlay = null; }
        }

        /// <summary>K toggles the paint tool; P toggles the overlay (independent of the tool). Lower priority than
        /// <see cref="_Input"/> so UI sees events first.</summary>
        public override void _UnhandledInput(InputEvent @event)
        {
            if (_gameState == null || _gameState.Mode != GameMode.Edit) return;
            if (@event is InputEventKey ck && ck.Pressed && ck.CtrlPressed
                && ck.Keycode >= Key.A && ck.Keycode <= Key.Z) return;   // Ctrl+<letter> = editor tier
            if (@event is not InputEventKey key || !key.Pressed || key.Echo) return;

            if (key.Keycode == Key.K && !key.CtrlPressed) // Ctrl+K belongs to the Ability Editor
            {
                _toolActive = !_painting && !_toolActive; // never toggle mid-stroke
                if (!_toolActive) CancelStroke();
                GD.Print(_toolActive
                    ? "[PathabilityTool] Active — LMB paint | panel Erase | [ ] brush | P overlay | K exit"
                    : "[PathabilityTool] Inactive.");
                GetViewport().SetInputAsHandled();
            }
            else if (key.Keycode == Key.P)
            {
                _overlayVisible = !_overlayVisible;
                UpdateStatus();
                GetViewport().SetInputAsHandled();
            }
        }

        /// <summary>While active, intercept the paint drag + brush-resize before the placer/selection see them (uses
        /// <see cref="_Input"/>, which fires before <c>_UnhandledInput</c> — the TerrainBrush/RegionTool pattern).</summary>
        public override void _Input(InputEvent @event)
        {
            if (!_toolActive || _gameState == null || _gameState.Mode != GameMode.Edit) return;

            if (@event is InputEventKey && ProjectChimera.UI.TextFocusGuard.IsTyping(this)) return; // hotkeys must not fire while typing
            if (@event is InputEventKey key && key.Pressed && !key.Echo)
            {
                if (key.Keycode == Key.Bracketleft)  { _brushRadius = Mathf.Max(0, _brushRadius - 1); UpdateStatus(); GetViewport().SetInputAsHandled(); return; }
                if (key.Keycode == Key.Bracketright) { _brushRadius = Mathf.Min(16, _brushRadius + 1); UpdateStatus(); GetViewport().SetInputAsHandled(); return; }
            }

            if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed && !IsOverPanel(mb.Position))
                {
                    var p = GroundPoint(mb.Position);
                    if (p != null)
                    {
                        _painting     = true;
                        _strokeBefore = (bool[])_painted.Clone(); // snapshot BEFORE for the single undo pair
                        StampAt(p.Value);
                        GetViewport().SetInputAsHandled();
                    }
                }
                else if (!mb.Pressed && _painting)
                {
                    CommitStroke();
                    GetViewport().SetInputAsHandled();
                }
            }
            else if (@event is InputEventMouseMotion motion && _painting)
            {
                var p = GroundPoint(motion.Position);
                if (p != null) StampAt(p.Value);
                GetViewport().SetInputAsHandled();
            }
        }

        // ── Stroke → commit ───────────────────────────────────────────────────

        private void CancelStroke()
        {
            _painting     = false;
            _strokeBefore = null;
        }

        /// <summary>Stamp the circular brush at world <paramref name="hit"/> into the painted mask (set in Paint mode,
        /// clear in Erase mode) and refresh the overlay live during the drag.</summary>
        private void StampAt(Vector3 hit)
        {
            WorldToCell(hit.X, hit.Z, out int cc, out int cr);
            int r = _brushRadius;
            for (int dr = -r; dr <= r; dr++)
            {
                for (int dc = -r; dc <= r; dc++)
                {
                    if (dc * dc + dr * dr > r * r) continue; // circular stamp
                    int nc = cc + dc, nr = cr + dr;
                    if ((uint)nc >= (uint)GS || (uint)nr >= (uint)GS) continue;
                    _painted[nr * GS + nc] = !_eraseMode;
                }
            }
            RebuildOverlay();
        }

        /// <summary>Commit the stroke: persist the painted mask into the scenario and push exactly one shared-history
        /// redo/undo pair. A no-op (nothing changed, or no scenario to persist into) pushes nothing.</summary>
        private void CommitStroke()
        {
            _painting = false;
            bool[]? before = _strokeBefore;
            _strokeBefore = null;
            if (before == null) return;
            if (_scenario == null) { GD.Print("[PathabilityTool] No scenario loaded — paint not persisted."); return; }

            bool[] after = (bool[])_painted.Clone();
            if (MasksEqual(before, after)) return; // stroke changed nothing (e.g. erasing already-clear cells)

            ApplyPaintedMask(after);

            _history?.Push(
                redo: () => { ApplyPaintedMask((bool[])after.Clone());  RebuildOverlay(); },
                undo: () => { ApplyPaintedMask((bool[])before.Clone()); RebuildOverlay(); });

            GD.Print($"[PathabilityTool] {( _eraseMode ? "Erased" : "Painted")} stroke committed " +
                     $"({CountBlocked(after)} blocked cells total).");
        }

        /// <summary>Set the live painted mask + write it back to <see cref="ScenarioData.PathabilityBlocked"/> as
        /// base64 (all-clear ⇒ null so the key is omitted, byte-identical to a flat map).</summary>
        private void ApplyPaintedMask(bool[] mask)
        {
            _painted = mask;
            if (_scenario != null) _scenario.PathabilityBlocked = PathabilityGrid.ToBase64(mask);
        }

        // ── Raycast / cell mapping ────────────────────────────────────────────

        /// <summary>Cast from the camera through <paramref name="screenPos"/> onto the Y=0 ground plane. Returns null
        /// on a miss / parallel ray (the EntityPlacer / RegionTool approach).</summary>
        private Vector3? GroundPoint(Vector2 screenPos)
        {
            var cam = _camCtrl?.GetCamera();
            if (cam == null) return null;
            var origin = cam.ProjectRayOrigin(screenPos);
            var dir    = cam.ProjectRayNormal(screenPos);
            if (Mathf.Abs(dir.Y) < 0.0001f) return null;
            float t = -origin.Y / dir.Y;
            if (t < 0f) return null;
            return origin + dir * t;
        }

        /// <summary>World XZ → clamped grid (col, row), mirroring <see cref="FlowField.WorldToCell"/>'s 128²/2-unit/±128
        /// mapping so the painted cell the author sees matches the cell the validator and sim enforce.</summary>
        private static void WorldToCell(float wx, float wz, out int col, out int row)
        {
            int ix = Mathf.FloorToInt(wx) + HALF;
            int iz = Mathf.FloorToInt(wz) + HALF;
            col = Mathf.Clamp(ix / CS, 0, GS - 1);
            row = Mathf.Clamp(iz / CS, 0, GS - 1);
        }

        private bool IsOverPanel(Vector2 screenPos)
            => _panel != null && _panel.GetGlobalRect().HasPoint(screenPos);

        // ── Overlay (single MultiMesh of red quads) ───────────────────────────

        private void BuildOverlay()
        {
            var quad = new QuadMesh { Size = new Vector2(CS, CS), Orientation = PlaneMesh.OrientationEnum.Y };
            _multiMesh = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = quad };

            _overlay = new MultiMeshInstance3D
            {
                Name       = "PathabilityOverlay",
                Multimesh  = _multiMesh,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D
                {
                    ShadingMode      = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    Transparency     = BaseMaterial3D.TransparencyEnum.Alpha,
                    AlbedoColor      = new Color(1f, 0.15f, 0.15f, 0.45f), // WC3 red
                    CullMode         = BaseMaterial3D.CullModeEnum.Disabled,
                    NoDepthTest      = false,
                },
            };
            GetParent()?.AddChild(_overlay);
        }

        /// <summary>Rebuild the MultiMesh instances from the union (painted ∪ derived). One instance per blocked cell,
        /// a flat quad at the cell centre just above ground.</summary>
        private void RebuildOverlay()
        {
            if (_multiMesh == null) return;

            // Count first so we set an exact instance count (allocation-stable per rebuild).
            int count = 0;
            for (int i = 0; i < CELLS; i++) if (_painted[i] || _derived[i]) count++;

            _multiMesh.InstanceCount = count;
            if (count == 0) return;

            int k = 0;
            for (int row = 0; row < GS; row++)
            {
                for (int col = 0; col < GS; col++)
                {
                    int idx = row * GS + col;
                    if (!_painted[idx] && !_derived[idx]) continue;
                    float cx = col * CS + 1 - HALF; // cell centre (mirrors FlowField.CellCenter)
                    float cz = row * CS + 1 - HALF;
                    _multiMesh.SetInstanceTransform(k++, new Transform3D(Basis.Identity, new Vector3(cx, 0.1f, cz)));
                }
            }
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
                MouseFilter    = Control.MouseFilterEnum.Stop, // guard: never paint under the panel
            };
            anchorRoot.AddChild(_panel);

            var vbox = new VBoxContainer();
            vbox.AddThemeConstantOverride("separation", 6);
            _panel.AddChild(vbox);

            var title = new Label { Text = "IMPASSABLE TERRAIN" };
            title.AddThemeFontSizeOverride("font_size", 13);
            vbox.AddChild(title);

            // Paint / Erase toggle.
            _eraseToggle = new CheckButton { Text = "Erase mode" };
            _eraseToggle.Toggled += on => { _eraseMode = on; UpdateStatus(); };
            AttachTip(_eraseToggle, "Erase mode", "When on, LMB clears blocked cells instead of painting them.");
            vbox.AddChild(_eraseToggle);

            // Slope-auto-block toggle + threshold (written straight into the scenario; takes effect on next load).
            vbox.AddChild(new HSeparator());
            _slopeToggle = new CheckBox { Text = "Slope auto-block" };
            _slopeToggle.Toggled += on =>
            {
                if (_scenario != null) _scenario.SlopeAutoBlock = on;
                UpdateStatus();
            };
            AttachTip(_slopeToggle, "Slope auto-block",
                "Auto-block steep cells (derived from terrain height at load) at or above the threshold below.");
            vbox.AddChild(_slopeToggle);

            var thrRow = new HBoxContainer();
            thrRow.AddChild(new Label { Text = "Threshold:", CustomMinimumSize = new Vector2(72, 0) });
            _slopeThreshold = new SpinBox { MinValue = 0, MaxValue = 100, Step = 0.1, CustomMinimumSize = new Vector2(110, 0) };
            _slopeThreshold.ValueChanged += v => { if (_scenario != null) _scenario.SlopeBlockThreshold = (float)v; };
            AttachTip(_slopeThreshold, "Slope threshold",
                "Minimum neighbour rise/run (world Y per world unit) at which a cell auto-blocks.");
            thrRow.AddChild(_slopeThreshold);
            vbox.AddChild(thrRow);

            _statusLabel = new Label { Text = "" };
            _statusLabel.AddThemeFontSizeOverride("font_size", 11);
            vbox.AddChild(_statusLabel);
        }

        /// <summary>Sync the slope panel controls from the loaded scenario (so re-opening the panel reflects the
        /// authored config).</summary>
        private void SyncSlopeFields()
        {
            if (_scenario == null) return;
            if (_slopeToggle != null)    _slopeToggle.ButtonPressed = _scenario.SlopeAutoBlock;
            if (_slopeThreshold != null) _slopeThreshold.Value       = _scenario.SlopeBlockThreshold;
        }

        private void UpdateStatus()
        {
            if (_statusLabel == null) return;
            _statusLabel.Text =
                $"Mode: {(_eraseMode ? "Erase" : "Paint")}   Brush: {_brushRadius * 2 + 1} cells   " +
                $"Blocked: {CountBlocked(_painted)}   Overlay[P]: {(_overlayVisible ? "ON" : "OFF")}";
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static bool MasksEqual(bool[] a, bool[] b)
        {
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static int CountBlocked(bool[] mask)
        {
            int n = 0;
            for (int i = 0; i < mask.Length; i++) if (mask[i]) n++;
            return n;
        }

        private static void AttachTip(Control target, string term, string body,
                                      ChimeraTooltip.TooltipRole role = ChimeraTooltip.TooltipRole.Pop)
            => ChimeraTooltip.AttachFocusable(target, term, body, role);
    }
}
