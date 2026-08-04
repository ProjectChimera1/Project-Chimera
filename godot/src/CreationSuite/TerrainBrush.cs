#nullable enable
using System;
using System.Collections.Generic;
using Godot;
using ProjectChimera.Core;
using ProjectChimera.UI;
using ProjectChimera.UI.Components;   // ChimeraTooltip (Story 5.9 tooltip-gap closure)

namespace ProjectChimera.CreationSuite
{
    /// <summary>
    /// In-game terrain sculpting and texture painting tool wrapping Terrain3DEditor
    /// via dynamic GDExtension dispatch.
    ///
    /// Press T in Edit mode to toggle. Active state: LMB paints, 1-5 switch mode,
    /// [ / ] resize brush. UI panel shows current mode + size/strength sliders.
    ///
    /// Sculpt modes (key 1-4):
    ///   1 — Raise    (SCULPT + ADD)
    ///   2 — Lower    (SCULPT + SUBTRACT)
    ///   3 — Smooth   (SCULPT + AVERAGE)
    ///   4 — Flatten  (HEIGHT + ADD at height 0)
    ///
    /// Texture paint mode (key 5):
    ///   5 — Paint    (TEXTURE + REPLACE) — hard-paints selected texture layer
    ///   Layer buttons in UI: Grass (0), Dirt (1), Rock (2), Snow (3)
    ///
    /// After each stroke, a 0.5 s debounce fires NavObstacleManager.MarkDirty()
    /// (NavMesh needs rebaking only for sculpt modes that change terrain height;
    /// texture painting is a no-op for navigation — but rebake is still safe).
    ///
    /// Architecture: Presentation layer. Uses Godot API + GodotObject dynamic dispatch.
    /// No Godot types in the sim layer.
    /// </summary>
    public partial class TerrainBrush : Node
    {
        // ── Terrain3DEditor constants (Terrain3D v1.0.x C++ source) ─────────────
        // Tool enum
        private const long TOOL_SCULPT  = 1;
        private const long TOOL_HEIGHT  = 2;
        private const long TOOL_TEXTURE = 3;
        // Operation enum
        private const long OP_ADD      = 0;
        private const long OP_SUBTRACT = 1;
        // MULTIPLY=2, DIVIDE=3 occupy slots before REPLACE
        private const long OP_REPLACE  = 4;
        private const long OP_AVERAGE  = 5;

        // ── Texture layer metadata (index → display name / colour for placeholder) ─
        private static readonly string[] LAYER_NAMES   = { "Grass", "Dirt", "Rock", "Snow" };
        private static readonly Color[]  LAYER_COLOURS =
        {
            new(0.30f, 0.55f, 0.20f), // Grass — muted green
            new(0.48f, 0.35f, 0.20f), // Dirt  — brown
            new(0.50f, 0.50f, 0.50f), // Rock  — grey
            new(0.90f, 0.92f, 0.95f), // Snow  — near-white blue
        };

        // ── Dependencies ──────────────────────────────────────────────────────
        private Node3D?              _terrain      = null;
        private RtsCameraController? _camCtrl      = null;
        private NavObstacleManager?  _navObstacles = null;
        private GameState?           _gameState    = null;

        // ── Terrain3DEditor (GDExtension, no typed C# binding) ───────────────
        private GodotObject? _editor = null;

        // ── Undo/redo (Story 6.2) ────────────────────────────────────────────
        /// <summary>The SHARED editor history injected from EntityPlacer — terrain strokes push here so they
        /// interleave LIFO with entity place/delete ops under EntityPlacer's Ctrl+Z/Y handler. Null ⇒ undo disabled
        /// (no snapshotting cost paid).</summary>
        private EditorHistory? _history = null;

        /// <summary>The affected regions' PRE-stroke height+control images, captured in BeginPaint and consumed in
        /// EndPaint to build the undo command. Null between strokes.</summary>
        private List<RegionSnapshot>? _strokeBefore = null;

        /// <summary>A restorable snapshot of one Terrain3D region: its location, world origin, and duplicated
        /// height/control CPU images (null when that map was absent). <see cref="WasAbsent"/> marks a region that did
        /// NOT exist at snapshot time (DW-141) — its images are null and its restore is a <c>remove_region</c>.</summary>
        private readonly struct RegionSnapshot
        {
            public readonly Vector2I Loc;
            public readonly Vector3  OriginWorld;
            public readonly Image?   Height;
            public readonly Image?   Control;
            public readonly bool     WasAbsent;
            public RegionSnapshot(Vector2I loc, Vector3 originWorld, Image? height, Image? control, bool wasAbsent)
            {
                Loc = loc; OriginWorld = originWorld; Height = height; Control = control; WasAbsent = wasAbsent;
            }
        }

        // ── Brush state ───────────────────────────────────────────────────────
        private BrushMode _mode         = BrushMode.Raise;
        private float     _brushSize    = 20f;   // world units (5–100)
        private float     _brushStrength = 10f;  // Terrain3D strength (1–100)
        private int       _activeLayer  = 0;     // texture layer index (0–3)
        private bool      _isPainting   = false;
        private bool      _brushActive  = false; // toggled by T key

        /// <summary>DW-144: true while a stroke's live operate() is in flight (BeginPaint→EndPaint). EntityPlacer reads
        /// this to swallow Ctrl+Z/Y during a stroke so History.Undo/Redo can't race the captured after-snapshot.</summary>
        public bool IsPainting => _isPainting;

        private Image?    _brushImage   = null;
        private Texture2D? _brushTexture = null;

        // Debounce: seconds until NavMesh rebake. -1 = idle.
        private float _rebakeTimer = -1f;

        // ── UI ────────────────────────────────────────────────────────────────
        private CanvasLayer?     _canvas      = null;
        private PanelContainer?  _brushPanel  = null; // used to block paint-on-slider-click
        private Label?           _modeLabel   = null;
        private HSlider?         _sizeSlider  = null;
        private HSlider?         _strSlider   = null;
        private HBoxContainer?   _layerBox    = null; // visible only in Paint mode

        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Wire dependencies and set up the Terrain3DEditor + texture assets.
        /// Call once from MainScene._Ready() after terrain and nav are initialised.
        /// </summary>
        public void Initialize(Node3D? terrain, RtsCameraController camCtrl,
                               NavObstacleManager navObstacles, GameState gameState,
                               EditorHistory? history = null)
        {
            _terrain      = terrain;
            _camCtrl      = camCtrl;
            _navObstacles = navObstacles;
            _gameState    = gameState;
            _history      = history;   // Story 6.2: shared undo stack (null ⇒ stroke undo disabled)

            if (_terrain == null)
            {
                GD.Print("[TerrainBrush] No Terrain3D node — brush tools disabled.");
                return;
            }

            SetupEditor();
            SetupTextureAssets();
            LoadBrushTexture();
            BuildUi();
        }

        // ── Godot lifecycle ───────────────────────────────────────────────────

        public override void _Process(double delta)
        {
            if (_terrain == null || _editor == null || _gameState == null) return;

            // Debounce timer → NavMesh rebake after sculpt stroke ends
            if (_rebakeTimer >= 0f)
            {
                _rebakeTimer -= (float)delta;
                if (_rebakeTimer < 0f)
                    _navObstacles?.MarkDirty();
            }

            bool inEdit = _gameState.Mode == GameMode.Edit;

            // DW-144 safety net: a stroke can be left in-flight by a context the paint-input path can't observe —
            // an Edit→Play switch mid-stroke (_Input early-returns before EndPaint), the brush deactivated by a
            // non-T route, or a missed mouse-up / focus loss during the drag. A stranded _isPainting would then make
            // EntityPlacer swallow every Ctrl+Z/Y until the next completed stroke, so finalize it here. During a
            // genuine drag the LMB is held (IsMouseButtonPressed true) and _Process leaves it alone.
            if (_isPainting && (!inEdit || !_brushActive || !Input.IsMouseButtonPressed(MouseButton.Left)))
                EndPaint();

            if (_canvas != null)
                _canvas.Visible = inEdit && _brushActive;

            // Keep terrain's internal camera up-to-date so get_intersection()
            // and the built-in brush cursor decal use the correct viewpoint.
            if (inEdit && _brushActive && _camCtrl != null)
                _terrain.Call("set_camera", _camCtrl.GetCamera());
        }

        /// <summary>T key toggles brush on/off — lower priority than _Input so UI sees events first.</summary>
        public override void _UnhandledInput(InputEvent @event)
        {
            if (_terrain == null || _editor == null || _gameState == null) return;
            if (_gameState.Mode != GameMode.Edit) return;

            if (@event is InputEventKey key && key.Pressed && !key.Echo && !key.CtrlPressed  // Ctrl+T = Tech Tree editor
                && key.Keycode == Key.T)
            {
                _brushActive = !_brushActive;
                // DW-142: toggling the brush off mid-drag must finalize the in-flight stroke via EndPaint. Otherwise
                // _isPainting/_strokeBefore strand — leaking the pending snapshot + undo entry and causing buttonless
                // painting the next time the brush is re-activated (mouse-motion paints with no LMB down).
                if (!_brushActive && _isPainting)
                    EndPaint();
                GD.Print(_brushActive
                    ? "[TerrainBrush] Active — LMB paint | 1-5 mode | [/] size | T exit"
                    : "[TerrainBrush] Inactive.");
                GetViewport().SetInputAsHandled();
            }
        }

        /// <summary>
        /// Intercepts keyboard and mouse events while brush is active.
        /// Using _Input (fires before _UnhandledInput) so EntityPlacer and
        /// SelectionSystem never see LMB / 1-5 / bracket events during brush use.
        /// </summary>
        public override void _Input(InputEvent @event)
        {
            if (!_brushActive || _terrain == null || _editor == null || _gameState == null)
                return;
            if (_gameState.Mode != GameMode.Edit) return;

            // ── Key shortcuts ─────────────────────────────────────────────────
            if (@event is InputEventKey && ProjectChimera.UI.TextFocusGuard.IsTyping(this)) return; // hotkeys must not fire while typing
            if (@event is InputEventKey ck && ck.Pressed && ck.CtrlPressed
                && ck.Keycode >= Key.A && ck.Keycode <= Key.Z) return;   // Ctrl+<letter> = editor tier
            if (@event is InputEventKey key && key.Pressed && !key.Echo)
            {
                switch (key.Keycode)
                {
                    case Key.Key1: SetMode(BrushMode.Raise);   GetViewport().SetInputAsHandled(); return;
                    case Key.Key2: SetMode(BrushMode.Lower);   GetViewport().SetInputAsHandled(); return;
                    case Key.Key3: SetMode(BrushMode.Smooth);  GetViewport().SetInputAsHandled(); return;
                    case Key.Key4: SetMode(BrushMode.Flatten); GetViewport().SetInputAsHandled(); return;
                    case Key.Key5: SetMode(BrushMode.Paint);   GetViewport().SetInputAsHandled(); return;
                    case Key.Bracketleft:
                        _brushSize = Mathf.Max(5f, _brushSize - 5f);
                        if (_sizeSlider != null) _sizeSlider.Value = _brushSize;
                        GetViewport().SetInputAsHandled(); return;
                    case Key.Bracketright:
                        _brushSize = Mathf.Min(100f, _brushSize + 5f);
                        if (_sizeSlider != null) _sizeSlider.Value = _brushSize;
                        GetViewport().SetInputAsHandled(); return;
                }
            }

            // ── Mouse paint ───────────────────────────────────────────────────
            if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
            {
                // Do NOT paint if the cursor is over the brush panel — let the GUI
                // handle it (sliders, buttons). IsOverPanel checks the panel's screen rect.
                if (mb.Pressed && !IsOverPanel(mb.Position))
                    BeginPaint(mb.Position);
                else if (!mb.Pressed && _isPainting)
                    EndPaint();

                // Only consume the event when we're actually painting; otherwise let
                // the GUI control (slider, button) process it normally.
                if (_isPainting || (!mb.Pressed && !IsOverPanel(mb.Position)))
                    GetViewport().SetInputAsHandled();
            }
            else if (@event is InputEventMouseMotion motion && _isPainting)
            {
                ContinuePaint(motion.Position);
                GetViewport().SetInputAsHandled();
            }
        }

        // ── Paint operations ──────────────────────────────────────────────────

        private void BeginPaint(Vector2 viewportPos)
        {
            var pos = GetTerrainPoint(viewportPos);
            if (pos == null) return;

            // Story 6.2: capture the affected regions' PRE-stroke height+control for undo (skipped when no shared
            // history is wired, so the snapshot cost is only paid when it can be used).
            _strokeBefore = SnapshotRegions(pos.Value);

            _isPainting = true;
            ApplyBrushSettings();

            // Story 6.2: re-assert the editor↔terrain wiring immediately before start_operation (mirrors the addon's
            // _edit() wiring). SetupEditor did this once at init, but 6.1 still saw Terrain3DEditor treat _terrain as
            // uninitialized at operation time — re-issuing here keeps operate() writing to the correct region.
            _terrain!.Call("set_editor", _editor!);
            _editor!.Call("set_terrain", _terrain);

            // start_operation is required by C++ before any operate() call.
            _editor!.Call("start_operation", pos.Value);
            _editor!.Call("operate",         pos.Value, GetCameraY());
        }

        private void ContinuePaint(Vector2 viewportPos)
        {
            var pos = GetTerrainPoint(viewportPos);
            if (pos == null) return;
            // Re-apply brush settings each sample so slider changes during a stroke
            // take effect immediately (size / strength are read from the dictionary
            // each time operate() is called).
            ApplyBrushSettings();
            _editor!.Call("operate", pos.Value, GetCameraY());
        }

        private void EndPaint()
        {
            _isPainting = false;

            // ── _store_undo disposition (Story 6.2) ──────────────────────────────────────────────────────────
            // We DELIBERATELY do NOT call Terrain3DEditor.stop_operation here. stop_operation is the SOLE trigger of
            // the compiled-in `Terrain3DEditor:_store_undo: _terrain isn't initialized` push_error red line: at
            // runtime there is no EditorPlugin host and no public EditorUndoRedoManager setter to satisfy the native
            // backup path, so it errors on every stroke end. This story supplies its OWN snapshot-based undo (below),
            // which makes Terrain3D's internal operation-undo redundant — so skipping stop_operation loses nothing
            // and silences the red line. Tradeoff (reviewer-noted, intentionally accepted): is_operating stays true
            // and the native backup may churn; the next stroke re-issues start_operation after re-asserting the
            // wiring (BeginPaint), and our snapshot reads the region data operate() already wrote, so the restore
            // payload is unaffected. Live-verified (6.2 review): no per-stroke red lines with this route-around.

            // Push the completed stroke onto the shared editor history (no-op when no history / nothing snapshotted).
            PushStrokeUndo();

            _rebakeTimer = 0.5f; // debounce NavMesh rebake
        }

        // ── Stroke undo/redo (Story 6.2) ──────────────────────────────────────

        /// <summary>
        /// Capture a restorable per-region snapshot (duplicated height + control CPU images) of every Terrain3D
        /// region the brush at <paramref name="centre"/> can touch. A circular brush of radius r centred near a
        /// four-way region junction paints a quarter-disk into the DIAGONAL region as well as the two axis-adjacent
        /// ones, so probing must cover all nine points of the ±r box (centre, the four axis points, AND the four
        /// corners) — an axis-only cross would miss the diagonal region and leave un-undoable residue there (review
        /// pass 2, finding F1). Duplicate locations are de-duped via <c>seen</c>, so on a single-region map this
        /// still snapshots exactly one region. Returns null when no shared history is wired (undo disabled) or
        /// nothing was captured, so the cost is only paid when it can be used.
        /// </summary>
        private List<RegionSnapshot>? SnapshotRegions(Vector3 centre)
        {
            if (_history == null || _terrain == null) return null;
            var data = _terrain.Get("data").AsGodotObject();
            if (data == null) return null;

            float span  = GetRegionSpan();
            var   snaps = new List<RegionSnapshot>();
            var   seen  = new HashSet<Vector2I>();
            float r     = _brushSize;

            foreach (var probe in new[]
            {
                centre,
                centre + new Vector3( r, 0f, 0f), centre + new Vector3(-r, 0f, 0f),
                centre + new Vector3(0f, 0f,  r), centre + new Vector3(0f, 0f, -r),
                centre + new Vector3( r, 0f,  r), centre + new Vector3( r, 0f, -r),
                centre + new Vector3(-r, 0f,  r), centre + new Vector3(-r, 0f, -r),
            })
            {
                var loc = data.Call("get_region_location", probe).AsVector2I();
                if (!seen.Add(loc)) continue;

                var origin = new Vector3(loc.X * span, 0f, loc.Y * span);

                var region = data.Call("get_region", loc).AsGodotObject();
                if (region == null)
                {
                    // DW-141: region absent pre-stroke (empty space the stroke may auto-create, or off the map edge).
                    // Record a WasAbsent snapshot with null images so `before` is non-null and, if the stroke creates
                    // this region, undo can remove_region it. An off-edge loc the brush never reaches stays absent in
                    // `after`, so the no-op check ignores it and its remove_region is a guarded no-op (Design Notes).
                    snaps.Add(new RegionSnapshot(loc, origin, null, null, true));
                    continue;
                }

                var height  = region.Call("get_height_map").As<Image>();
                var control = region.Call("get_control_map").As<Image>();
                snaps.Add(new RegionSnapshot(loc, origin,
                    height  != null ? (Image)height.Duplicate(true)  : null,
                    control != null ? (Image)control.Duplicate(true) : null,
                    false));
            }
            return snaps.Count > 0 ? snaps : null;
        }

        /// <summary>
        /// Snapshot the POST-stroke state of the same regions captured in BeginPaint, then push a (redo = restore
        /// after, undo = restore before) command onto the shared history. No-op when nothing was snapshotted.
        /// </summary>
        private void PushStrokeUndo()
        {
            var before = _strokeBefore;
            _strokeBefore = null;
            if (_history == null || before == null || before.Count == 0) return;
            if (_terrain?.Get("data").AsGodotObject() is not GodotObject data) return;

            var after = new List<RegionSnapshot>(before.Count);
            foreach (var b in before)
            {
                var region = data.Call("get_region", b.Loc).AsGodotObject();
                bool wasAbsent = region == null;   // post-stroke absence — WasAbsent=true means the stroke created nothing here
                Image? h = null, c = null;
                if (region != null)
                {
                    var hm = region.Call("get_height_map").As<Image>();
                    var cm = region.Call("get_control_map").As<Image>();
                    h = hm != null ? (Image)hm.Duplicate(true) : null;
                    c = cm != null ? (Image)cm.Duplicate(true) : null;
                }
                after.Add(new RegionSnapshot(b.Loc, b.OriginWorld, h, c, wasAbsent));
            }

            // DW-143: skip pushing an undo command when the stroke changed nothing. `before` and `after` are
            // index-aligned (both iterate `before`). A region "changed" iff it was created (absent before, present
            // now) OR — both present — its Height OR Control bytes differ. A region absent in both is ignored (the
            // harmless over-approximation from the 9-point probe box). No change ⇒ return before _history.Push so a
            // later Ctrl+Z undoes the previous REAL op instead of being silently absorbed by an empty stroke.
            bool changed = false;
            for (int i = 0; i < before.Count; i++)
            {
                var bi = before[i];
                var ai = after[i];
                if (bi.WasAbsent && !ai.WasAbsent) { changed = true; break; }        // region created
                if (!bi.WasAbsent && !ai.WasAbsent
                    && (!ImageBytesEqual(bi.Height, ai.Height)
                     || !ImageBytesEqual(bi.Control, ai.Control))) { changed = true; break; }
            }
            if (!changed) return; // _strokeBefore already cleared above

            // DW-140: weigh the stroke by its snapshot memory cost (before + after height/control Images) so the
            // shared history's byte cap bounds real terrain-undo memory. Cheap entity ops pass 0 (the default).
            long estimatedBytes = SnapshotBytes(before) + SnapshotBytes(after);

            _history.Push(
                redo: () => RestoreRegions(after),
                undo: () => RestoreRegions(before),
                estimatedBytes: estimatedBytes);
        }

        /// <summary>DW-143: byte-equality of two CPU Images (both null ⇒ equal; one null ⇒ differ). Runs once per
        /// region at stroke END only (never on the hot per-sample ContinuePaint path) to decide whether a stroke
        /// changed anything worth pushing an undo command for.</summary>
        private static bool ImageBytesEqual(Image? a, Image? b)
            => (a == null && b == null) || (a != null && b != null
               && a.GetData().AsSpan().SequenceEqual(b.GetData()));

        /// <summary>Sum the estimated CPU-memory cost of a region-snapshot list — Height + Control Image of every
        /// region (null-safe). Feeds the shared history's DW-140 byte cap so a long sculpt can't pin unbounded undo
        /// memory.</summary>
        private static long SnapshotBytes(List<RegionSnapshot> snaps)
        {
            long total = 0;
            foreach (var s in snaps)
                total += EstimateImageBytes(s.Height) + EstimateImageBytes(s.Control);
            return total;
        }

        /// <summary>Estimate one Image's CPU-memory footprint as width × height × bytes-per-pixel(format). Null ⇒ 0.
        /// Covers the formats Terrain3D height (Rf) and control (Rf/Rgf) maps use; unknown formats assume 4 bpp.</summary>
        private static long EstimateImageBytes(Image? img)
        {
            if (img == null) return 0;
            int bpp = img.GetFormat() switch
            {
                Image.Format.Rf   => 4,
                Image.Format.Rgf  => 8,
                Image.Format.Rgba8 => 4,
                Image.Format.Rgb8 => 3,
                Image.Format.R8   => 1,
                _                 => 4,
            };
            return (long)img.GetWidth() * img.GetHeight() * bpp;
        }

        /// <summary>
        /// Write a set of region snapshots back into the live terrain via import_images (the same call family
        /// TerrainPhase uses to import the flat region), recompute the height range, and MarkDirty the NavMesh so a
        /// bake reflects the restored height.
        /// </summary>
        private void RestoreRegions(List<RegionSnapshot> snaps)
        {
            if (_terrain == null) return;
            var data = _terrain.Get("data").AsGodotObject();
            if (data == null) return;

            foreach (var s in snaps)
            {
                if (s.WasAbsent)
                {
                    // DW-141: this region did not exist at this snapshot's time. Restoring "absent" means removing it,
                    // so a create-and-undo removes the auto-created region instead of leaving un-undoable residue.
                    // Guarded: over-approximation on the probe box can list a loc no region ever occupied, so only
                    // remove when a region is actually present now. remove_region(region, true) self-updates the maps
                    // (mirrors the addon importer.gd's remove_region + update_maps) — no TYPE_MAX enum needed from C#.
                    var region = data.Call("get_region", s.Loc).AsGodotObject();
                    if (region != null)
                        data.Call("remove_region", region, true);
                    continue;
                }

                // import_images([height, control, color], regionOriginWorldPos, offset=0, scale=1). Color is left
                // null (unchanged) — this story round-trips height + control only.
                var images = new Godot.Collections.Array
                {
                    s.Height  != null ? Variant.From(s.Height)  : new Variant(),
                    s.Control != null ? Variant.From(s.Control) : new Variant(),
                    new Variant(),
                };
                data.Call("import_images", images, s.OriginWorld, 0f, 1f);
            }
            data.Call("calc_height_range", true);
            _navObstacles?.MarkDirty();
        }

        /// <summary>World units spanned by one region edge = region_size × vertex_spacing. Both are Terrain3D node
        /// properties; falls back to the flat-region defaults (256 × 1.0) TerrainPhase imports with.</summary>
        private float GetRegionSpan()
        {
            if (_terrain == null) return 256f;
            int regionSize = _terrain.Get("region_size").AsInt32();
            if (regionSize <= 0) regionSize = 256;
            float spacing = 1f;
            var vs = _terrain.Get("vertex_spacing");
            if (vs.VariantType is Variant.Type.Float or Variant.Type.Int)
            {
                float s = (float)vs.AsDouble();
                if (s > 0f) spacing = s;
            }
            return regionSize * spacing;
        }

        // ── Brush helpers ─────────────────────────────────────────────────────

        private void SetMode(BrushMode mode)
        {
            _mode = mode;
            // Show layer picker only in Paint mode
            if (_layerBox != null)
                _layerBox.Visible = (mode == BrushMode.Paint);
            UpdateModeLabel();
        }

        private void UpdateModeLabel()
        {
            if (_modeLabel == null) return;
            string hint = _mode == BrushMode.Paint
                ? $"Paint layer: {LAYER_NAMES[_activeLayer]}   [T=off | 1-5=mode]"
                : $"Brush: {_mode}   [T=off | 1-5=mode | [/]=size]";
            _modeLabel.Text = hint;
        }

        private float GetCameraY()
            => _camCtrl != null ? _camCtrl.GetCamera().GlobalRotation.Y : 0f;

        /// <summary>
        /// Returns true if <paramref name="screenPos"/> falls inside the brush panel's
        /// screen-space rectangle. Used to prevent terrain painting when the user
        /// clicks UI controls (sliders, buttons) inside the panel.
        /// </summary>
        private bool IsOverPanel(Vector2 screenPos)
        {
            if (_brushPanel == null) return false;
            return _brushPanel.GetGlobalRect().HasPoint(screenPos);
        }

        /// <summary>
        /// Cast a ray from the camera through <paramref name="viewportPos"/> and return
        /// the Terrain3D surface hit position, or null on miss.
        ///
        /// Sentinel per Terrain3D source: miss = NaN in Y, or Z > 3.4e38 (max double).
        /// </summary>
        private Vector3? GetTerrainPoint(Vector2 viewportPos)
        {
            if (_camCtrl == null || _terrain == null) return null;

            var cam    = _camCtrl.GetCamera();
            var origin = cam.ProjectRayOrigin(viewportPos);
            var dir    = cam.ProjectRayNormal(viewportPos);

            var hit = _terrain.Call("get_intersection", origin, dir, true).AsVector3();

            // Miss sentinels: NaN y-component, or astronomically large Z (> 3.4e38)
            if (float.IsNaN(hit.Y) || hit.Z > 3.4e38f) return null;

            return hit;
        }

        /// <summary>
        /// Push current mode, size, strength, and layer to Terrain3DEditor before a stroke.
        /// </summary>
        private void ApplyBrushSettings()
        {
            if (_editor == null || _brushTexture == null) return;

            (long tool, long op) = _mode switch
            {
                BrushMode.Raise   => (TOOL_SCULPT,  OP_ADD),
                BrushMode.Lower   => (TOOL_SCULPT,  OP_SUBTRACT),
                BrushMode.Smooth  => (TOOL_SCULPT,  OP_AVERAGE),
                BrushMode.Flatten => (TOOL_HEIGHT,  OP_ADD),
                BrushMode.Paint   => (TOOL_TEXTURE, OP_REPLACE),
                _                 => (TOOL_SCULPT,  OP_ADD),
            };

            _editor.Call("set_tool",      tool);
            _editor.Call("set_operation", op);

            // brush must be [Image, ImageTexture] — Terrain3DEditor C++ reads [0] as the Image
            var brushArr = new Godot.Collections.Array { Variant.From(_brushImage), Variant.From(_brushTexture) };
            var data = new Godot.Collections.Dictionary
            {
                ["brush"]                      = brushArr,
                ["size"]                       = _brushSize,
                ["strength"]                   = _brushStrength,
                ["mouse_pressure"]             = 1.0f,
                ["height"]                     = 0.0f,      // target Y for Flatten
                ["color"]                      = Colors.White,
                ["roughness"]                  = 0.5f,
                ["asset_id"]                   = _activeLayer, // texture layer 0-3
                ["align_to_view"]              = false,
                ["show_cursor_while_painting"] = true,
                ["gradient_points"]            = new Godot.Collections.Array(),
                ["drawable"]                   = true,
            };

            _editor.Call("set_brush_data", data);
        }

        // ── Initialisation helpers ────────────────────────────────────────────

        private void SetupEditor()
        {
            if (!ClassDB.ClassExists("Terrain3DEditor") ||
                !ClassDB.CanInstantiate("Terrain3DEditor"))
            {
                GD.PrintErr("[TerrainBrush] Terrain3DEditor class not available.");
                return;
            }

            var obj = ClassDB.Instantiate("Terrain3DEditor").AsGodotObject();
            if (obj == null)
            {
                GD.PrintErr("[TerrainBrush] Terrain3DEditor instantiation returned null.");
                return;
            }

            _editor = obj;
            _terrain!.Call("set_editor", _editor);
            _editor.Call("set_terrain", _terrain);
            GD.Print("[TerrainBrush] Terrain3DEditor wired to terrain.");
        }

        /// <summary>
        /// Create a Terrain3DAssets resource with 4 placeholder texture layers
        /// (solid-colour albedo — no art assets required). This lets the TEXTURE
        /// brush write meaningful layer data to the control map right away.
        ///
        /// Real .tres texture assets can be dropped into the Terrain3D node via the
        /// Godot editor asset dock once art is ready; this setup is overwritten by
        /// that workflow.
        ///
        /// If Terrain3DAssets or Terrain3DTexture are not available (unexpected
        /// runtime mismatch), the method exits without error — painting still
        /// modifies the control map, it just won't show visible colour differences.
        /// </summary>
        private void SetupTextureAssets()
        {
            if (_terrain == null) return;

            // In Terrain3D v1.0.x the texture asset class was renamed Terrain3DTexture → Terrain3DTextureAsset
            string texClassName = ClassDB.ClassExists("Terrain3DTextureAsset")
                ? "Terrain3DTextureAsset" : "Terrain3DTexture";

            if (!ClassDB.ClassExists("Terrain3DAssets") ||
                (!ClassDB.ClassExists("Terrain3DTextureAsset") && !ClassDB.ClassExists("Terrain3DTexture")))
            {
                GD.Print("[TerrainBrush] Terrain3DAssets/Terrain3DTextureAsset not found — skipping placeholder texture setup.");
                return;
            }

            try
            {
                var assets = ClassDB.Instantiate("Terrain3DAssets").AsGodotObject();
                if (assets == null) return;

                for (int i = 0; i < LAYER_NAMES.Length; i++)
                {
                    var texObj = ClassDB.Instantiate(texClassName).AsGodotObject();
                    if (texObj == null) continue;

                    texObj.Set("name",      LAYER_NAMES[i]);
                    texObj.Set("color",     LAYER_COLOURS[i]);
                    texObj.Set("roughness", 0.8f);

                    // Placeholder albedo: 64×64 solid-colour Rgb8 image
                    var img    = Image.CreateEmpty(64, 64, false, Image.Format.Rgb8);
                    img.Fill(LAYER_COLOURS[i]);
                    var albedo = ImageTexture.CreateFromImage(img);
                    texObj.Call("set_albedo_texture", albedo);

                    assets.Call("set_texture", i, texObj);
                }

                _terrain.Set("assets", assets);

                // Verify round-trip: read back the assets property to confirm Terrain3D accepted it.
                var readBack = _terrain.Get("assets").AsGodotObject();
                if (readBack == null)
                    GD.PrintErr("[TerrainBrush] WARNING: Terrain3D did not accept the procedural assets. " +
                                "Paint will write to the control map but NO color will show. " +
                                "Fix: In the Godot editor, select the Terrain3D node → Inspector → " +
                                "Assets → Terrain 3D Assets, and add textures there manually.");
                else
                    GD.Print($"[TerrainBrush] {LAYER_NAMES.Length} placeholder texture layers created " +
                             "(Grass/Dirt/Rock/Snow). If colors still don't appear, set real textures " +
                             "via Terrain3D node → Inspector → Assets → Terrain 3D Assets.");
            }
            catch (System.Exception ex)
            {
                GD.PrintErr($"[TerrainBrush] Texture asset setup failed ({ex.Message}) — " +
                            "painting writes to control map but no color will be visible. " +
                            "Set textures manually: Terrain3D node → Inspector → Assets → Terrain 3D Assets.");
            }
        }

        private void LoadBrushTexture()
        {
            // ResourceLoader can't load files in gdignored folders (brushes/.gdignore).
            // Use Image.LoadFromFile() which reads the file directly — no import needed.
            const string BRUSH_RES = "res://addons/terrain_3d/brushes/circle0.exr";
            string brushAbs = ProjectSettings.GlobalizePath(BRUSH_RES);

            if (System.IO.File.Exists(brushAbs))
            {
                var img = Image.LoadFromFile(brushAbs);
                if (img != null)
                {
                    img.Convert(Image.Format.Rf); // Terrain3DEditor expects RF format
                    _brushImage   = img;
                    _brushTexture = ImageTexture.CreateFromImage(img);
                    GD.Print("[TerrainBrush] Brush texture: circle0.exr");
                    return;
                }
            }

            // Fallback: procedural radial gradient (soft circle)
            const int SZ = 64;
            var fallback = Image.CreateEmpty(SZ, SZ, false, Image.Format.Rf);
            for (int y = 0; y < SZ; y++)
            for (int x = 0; x < SZ; x++)
            {
                float dx = (x - SZ * 0.5f) / (SZ * 0.5f);
                float dy = (y - SZ * 0.5f) / (SZ * 0.5f);
                float v  = Mathf.Clamp(1f - Mathf.Sqrt(dx * dx + dy * dy), 0f, 1f);
                fallback.SetPixel(x, y, new Color(v, v, v, 1f));
            }
            _brushImage   = fallback;
            _brushTexture = ImageTexture.CreateFromImage(fallback);
            GD.Print("[TerrainBrush] Brush texture: procedural fallback circle.");
        }

        // ── UI construction ───────────────────────────────────────────────────

        private void BuildUi()
        {
            // Layer 5: above HUD (1) and palette (1), below content browser (10) and settings (15).
            _canvas = new CanvasLayer { Visible = false, Layer = 5 };
            AddChild(_canvas);

            // Anchor root: a full-viewport Control so its children can use reliable
            // anchor/offset layout independent of the CanvasLayer coordinate origin.
            // MouseFilter.Ignore: does not block input itself — only the panel does.
            var anchorRoot = new Control();
            anchorRoot.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            anchorRoot.MouseFilter = Control.MouseFilterEnum.Ignore;
            _canvas.AddChild(anchorRoot);

            // Panel on the left side, below the HUD area.
            // HUD panel: Y=4, ~3 lines @ font 15 ≈ 72 px tall → ends ~Y=76.
            // Resource label: Y=80, 3 lines @ font 14 ≈ 60 px tall → ends ~Y=140.
            // Y=155 gives a comfortable 15 px gap below the resource strip.
            _brushPanel = new PanelContainer
            {
                Position          = new Vector2(10f, 155f),
                CustomMinimumSize = new Vector2(350f, 0),
                MouseFilter       = Control.MouseFilterEnum.Stop,
            };
            anchorRoot.AddChild(_brushPanel);
            var panel = _brushPanel;

            var vbox = new VBoxContainer();
            vbox.AddThemeConstantOverride("separation", 6);
            panel.AddChild(vbox);

            // ── Mode label ────────────────────────────────────────────────────
            _modeLabel = new Label();
            _modeLabel.AddThemeFontSizeOverride("font_size", 13);
            vbox.AddChild(_modeLabel);
            UpdateModeLabel();

            // ── Sculpt mode buttons (1-5) ──────────────────────────────────────
            var modeBox = new HBoxContainer();
            vbox.AddChild(modeBox);

            foreach (var (label, mode, tip) in new (string, BrushMode, string)[]
            {
                ("1 Raise",   BrushMode.Raise,   "Raise terrain height under the brush."),
                ("2 Lower",   BrushMode.Lower,   "Lower terrain height under the brush."),
                ("3 Smooth",  BrushMode.Smooth,  "Average nearby heights to smooth bumps."),
                ("4 Flatten", BrushMode.Flatten, "Flatten terrain to height 0 under the brush."),
                ("5 Paint",   BrushMode.Paint,   "Switch to texture painting (pick a layer below)."),
            })
            {
                var btn          = new Button { Text = label };
                var capturedMode = mode;
                btn.Pressed     += () => SetMode(capturedMode);
                AttachTip(btn, label, tip);
                modeBox.AddChild(btn);
            }

            // ── Texture layer picker (visible only in Paint mode) ─────────────
            _layerBox = new HBoxContainer { Visible = false };
            vbox.AddChild(_layerBox);

            var layerLabel = new Label { Text = "Layer:" };
            layerLabel.AddThemeFontSizeOverride("font_size", 13);
            _layerBox.AddChild(layerLabel);

            for (int i = 0; i < LAYER_NAMES.Length; i++)
            {
                int capturedLayer = i;
                var btn = new Button
                {
                    Text              = LAYER_NAMES[i],
                    CustomMinimumSize = new Vector2(50, 0),
                };
                btn.Pressed += () =>
                {
                    _activeLayer = capturedLayer;
                    UpdateModeLabel();
                };
                AttachTip(btn, LAYER_NAMES[i], $"Paint the '{LAYER_NAMES[i]}' texture layer under the brush.");
                _layerBox.AddChild(btn);
            }

            // ── Size slider ───────────────────────────────────────────────────
            var sizeRow = new HBoxContainer();
            vbox.AddChild(sizeRow);
            sizeRow.AddChild(new Label { Text = "Size: ", CustomMinimumSize = new Vector2(45, 0) });
            _sizeSlider = new HSlider
            {
                MinValue          = 5,
                MaxValue          = 100,
                Step              = 1,
                Value             = _brushSize,
                CustomMinimumSize = new Vector2(160, 0),
            };
            _sizeSlider.ValueChanged += v => _brushSize = (float)v;
            AttachTip(_sizeSlider, "Brush size", "World-unit radius of the brush ([ / ] also resizes it).", ChimeraTooltip.TooltipRole.Field);
            sizeRow.AddChild(_sizeSlider);

            // ── Strength slider ───────────────────────────────────────────────
            var strRow = new HBoxContainer();
            vbox.AddChild(strRow);
            strRow.AddChild(new Label { Text = "Str:  ", CustomMinimumSize = new Vector2(45, 0) });
            _strSlider = new HSlider
            {
                MinValue          = 1,
                MaxValue          = 100,
                Step              = 1,
                Value             = _brushStrength,
                CustomMinimumSize = new Vector2(160, 0),
            };
            _strSlider.ValueChanged += v => _brushStrength = (float)v;
            AttachTip(_strSlider, "Brush strength", "How much each stroke sample raises/lowers/paints per pass.", ChimeraTooltip.TooltipRole.Field);
            strRow.AddChild(_strSlider);
        }

        /// <summary>Attach a hover-AND-keyboard-focus tooltip (AC3 / UX-DR53 / NFR-2). Thin forwarder to the
        /// centralized <see cref="ChimeraTooltip.AttachFocusable"/> (Story 5.9 review pass).</summary>
        private static void AttachTip(Control target, string term, string body, ChimeraTooltip.TooltipRole role = ChimeraTooltip.TooltipRole.Pop)
            => ChimeraTooltip.AttachFocusable(target, term, body, role);
    }

    /// <summary>Terrain sculpt/paint mode for <see cref="TerrainBrush"/>.</summary>
    public enum BrushMode { Raise, Lower, Smooth, Flatten, Paint }
}
