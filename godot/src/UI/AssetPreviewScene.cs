#nullable enable
using Godot;
using ProjectChimera.Core.Definitions;

namespace ProjectChimera.UI
{
    /// <summary>
    /// P0.3 Asset Preview Scene — evaluates individual AI-generated 3D assets.
    ///
    /// Usage:
    ///   1. Set ModelPath to a res:// GLB path (export var in the editor, or edit below).
    ///   2. Run the scene. The model spins on a turntable with studio lighting.
    ///   3. Check poly count, silhouette, and style.
    ///   4. Press Left/Right arrow to cycle through all units in a faction JSON.
    ///
    /// Set FactionJsonPath to load the full faction and cycle with arrow keys.
    /// </summary>
    public partial class AssetPreviewScene : Node3D
    {
        [Export] public string ModelPath { get; set; } =
            "res://assets/models/factions/alpha/infantry.glb";

        /// <summary>Optional: path to a faction JSON to enable arrow-key cycling.</summary>
        [Export] public string FactionJsonPath { get; set; } =
            "res://resources/data/factions/alpha_faction.json";

        [Export] public float TurntableSpeed { get; set; } = 30f; // degrees/sec

        // ── Private state ────────────────────────────────────────────────────────

        private Node3D _turntable = null!;
        private Label  _infoLabel = null!;

        private FactionDefinition? _faction;
        private int _currentIndex = 0;

        public override void _Ready()
        {
            SetupLighting();
            SetupCamera();
            SetupGround();
            SetupHud();

            _turntable = new Node3D();
            AddChild(_turntable);

            // Try to load faction for cycling; fall back to single ModelPath
            LoadFaction();
            ShowUnit(_currentIndex);
        }

        public override void _Process(double delta)
        {
            _turntable.RotateY(Mathf.DegToRad(TurntableSpeed * (float)delta));
        }

        public override void _Input(InputEvent @event)
        {
            if (_faction == null) return;
            int total = _faction.Units.Count + _faction.Buildings.Count;
            if (total == 0) return;

            if (@event is InputEventKey key && key.Pressed)
            {
                if (key.Keycode == Key.Right)
                {
                    _currentIndex = (_currentIndex + 1) % total;
                    ShowUnit(_currentIndex);
                }
                else if (key.Keycode == Key.Left)
                {
                    _currentIndex = (_currentIndex - 1 + total) % total;
                    ShowUnit(_currentIndex);
                }
            }
        }

        // ── Loading ──────────────────────────────────────────────────────────────

        private void LoadFaction()
        {
            if (string.IsNullOrEmpty(FactionJsonPath)) return;

            string absPath = ProjectSettings.GlobalizePath(FactionJsonPath);
            if (!System.IO.File.Exists(absPath))
            {
                GD.Print($"[AssetPreview] Faction JSON not found at {absPath}");
                return;
            }

            _faction = FactionDefinition.LoadFromFile(absPath);
            GD.Print($"[AssetPreview] Loaded faction '{_faction.DisplayName}' " +
                     $"— {_faction.Units.Count} units, {_faction.Buildings.Count} buildings. " +
                     $"Left/Right arrows to cycle.");
        }

        private void ShowUnit(int index)
        {
            // Clear any previously loaded model
            foreach (Node child in _turntable.GetChildren())
                child.QueueFree();

            string resPath = ModelPath;
            string label = "Single model";
            float scale = 1f;

            if (_faction != null)
            {
                int unitCount = _faction.Units.Count;
                UnitDefinition def = index < unitCount
                    ? _faction.Units[index]
                    : _faction.Buildings[index - unitCount];

                resPath = def.MeshPath ?? "";
                label   = $"[{index + 1}/{_faction.Units.Count + _faction.Buildings.Count}] " +
                          $"{def.DisplayName}  ({def.Category})";
                scale   = def.MeshScale;
            }

            var color = new Color(0.5f, 0.7f, 1.0f);
            var mesh = MeshLoader.LoadFromGlb(resPath, new Vector3(0.8f, 1.6f, 0.8f), color);

            var mi = new MeshInstance3D();
            mi.Mesh = mesh;
            mi.Scale = MeshLoader.ScaleFromDefinition(scale);
            _turntable.AddChild(mi);

            _infoLabel.Text = $"{label}\n\nLeft/Right — cycle assets\nF5 in MainScene — Edit/Play";
        }

        // ── Scene setup ──────────────────────────────────────────────────────────

        private void SetupLighting()
        {
            // Key light
            var key = new DirectionalLight3D();
            key.Rotation = new Vector3(Mathf.DegToRad(-45), Mathf.DegToRad(45), 0);
            key.LightEnergy = 1.4f;
            AddChild(key);

            // Fill light
            var fill = new DirectionalLight3D();
            fill.Rotation = new Vector3(Mathf.DegToRad(-20), Mathf.DegToRad(-120), 0);
            fill.LightEnergy = 0.6f;
            AddChild(fill);

            var ambient = new WorldEnvironment();
            var env = new Godot.Environment();
            env.AmbientLightSource = Godot.Environment.AmbientSource.Color;
            env.AmbientLightColor = new Color(0.35f, 0.35f, 0.4f);
            env.AmbientLightEnergy = 0.6f;
            ambient.Environment = env;
            AddChild(ambient);
        }

        private void SetupCamera()
        {
            var camera = new Camera3D();
            camera.Position = new Vector3(0, 1.5f, 5f);
            camera.LookAtFromPosition(camera.Position, new Vector3(0, 0.8f, 0));
            AddChild(camera);
        }

        private void SetupGround()
        {
            var ground = new MeshInstance3D();
            var plane = new PlaneMesh();
            plane.Size = new Vector2(6, 6);
            var mat = new StandardMaterial3D();
            mat.AlbedoColor = new Color(0.15f, 0.15f, 0.18f);
            plane.Material = mat;
            ground.Mesh = plane;
            AddChild(ground);
        }

        private void SetupHud()
        {
            _infoLabel = new Label();
            _infoLabel.Position = new Vector2(12, 12);
            _infoLabel.AddThemeColorOverride("font_color", Colors.White);
            _infoLabel.AddThemeFontSizeOverride("font_size", 18);
            var canvas = new CanvasLayer();
            canvas.AddChild(_infoLabel);
            AddChild(canvas);
        }
    }
}
