#nullable enable
using Godot;
using ProjectChimera.Core;
using ProjectChimera.UI;

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

        // ── Brush state ───────────────────────────────────────────────────────
        private BrushMode _mode         = BrushMode.Raise;
        private float     _brushSize    = 20f;   // world units (5–100)
        private float     _brushStrength = 10f;  // Terrain3D strength (1–100)
        private int       _activeLayer  = 0;     // texture layer index (0–3)
        private bool      _isPainting   = false;
        private bool      _brushActive  = false; // toggled by T key

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
                               NavObstacleManager navObstacles, GameState gameState)
        {
            _terrain      = terrain;
            _camCtrl      = camCtrl;
            _navObstacles = navObstacles;
            _gameState    = gameState;

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

            if (@event is InputEventKey key && key.Pressed && !key.Echo
                && key.Keycode == Key.T)
            {
                _brushActive = !_brushActive;
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

            _isPainting = true;
            ApplyBrushSettings();
            // start_operation is required by C++ before any operate() call.
            // _store_undo inside it may fail silently at runtime (no EditorPlugin),
            // but the sculpt/paint state is still set up correctly.
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
            if (_editor != null && _editor.Call("is_operating").AsBool())
                _editor.Call("stop_operation");
            _rebakeTimer = 0.5f; // debounce NavMesh rebake
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

            foreach (var (label, mode) in new (string, BrushMode)[]
            {
                ("1 Raise",   BrushMode.Raise),
                ("2 Lower",   BrushMode.Lower),
                ("3 Smooth",  BrushMode.Smooth),
                ("4 Flatten", BrushMode.Flatten),
                ("5 Paint",   BrushMode.Paint),
            })
            {
                var btn          = new Button { Text = label };
                var capturedMode = mode;
                btn.Pressed     += () => SetMode(capturedMode);
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
            strRow.AddChild(_strSlider);
        }
    }

    /// <summary>Terrain sculpt/paint mode for <see cref="TerrainBrush"/>.</summary>
    public enum BrushMode { Raise, Lower, Smooth, Flatten, Paint }
}
