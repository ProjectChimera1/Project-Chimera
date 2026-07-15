#nullable enable
using System.Collections.Generic;
using Godot;
using ProjectChimera.Core.Definitions;
using ProjectChimera.UI;
using ProjectChimera.UI.Components;   // ChimeraTooltip

namespace ProjectChimera.CreationSuite
{
    /// <summary>
    /// Story 6.6 — the in-game named-camera tool: authors <see cref="ScenarioCamera"/> viewpoints (position + look-at
    /// target + FOV) that the in-editor "view through camera" preview uses and Epic 7's <c>MoveCamera</c> action
    /// (Story 7.13) consumes.
    ///
    /// <para>Press <b>V</b> in Edit mode to toggle the panel. "Capture current view" snapshots the live editor camera
    /// (position / look direction / FOV) into a new named camera under the panel field. The right-dock list lets you
    /// preview ("View through" — temporarily drives the editor camera; "Stop preview" restores control) and delete
    /// cameras (there is no in-place rename — capturing again adds a new uniquely-named camera). Every add/delete pushes exactly one <see cref="EditorHistory"/> (redo, undo) pair onto
    /// the SHARED editor stack, so it interleaves LIFO with entity / region / pathability / terrain undo/redo. Cameras
    /// are pure PRESENTATION — never in sim state or either checksum.</para>
    /// </summary>
    public partial class CameraTool : Node
    {
        // ── Dependencies ──────────────────────────────────────────────────────
        private RtsCameraController? _camCtrl;
        private GameState?           _gameState;
        private ScenarioData?        _scenario;
        private EditorHistory?       _history;

        // ── Tool state ────────────────────────────────────────────────────────
        private bool _toolActive = false; // toggled by V
        private bool _previewing = false;  // "view through camera" active (controller suppressed)
        private int  _selected   = -1;

        // ── Panel UI ──────────────────────────────────────────────────────────
        private CanvasLayer?    _canvas;
        private PanelContainer? _panel;
        private VBoxContainer?  _listBox;
        private LineEdit?       _nameField;
        private Label?          _statusLabel;

        /// <summary>Wire dependencies + build the panel. Called once from the CameraTool phase after camera, game
        /// state, and (loaded) scenario exist. <paramref name="history"/> is the SHARED editor stack.</summary>
        public void Initialize(RtsCameraController camCtrl, GameState gameState, ScenarioData? scenario, EditorHistory? history)
        {
            _camCtrl   = camCtrl;
            _gameState = gameState;
            _scenario  = scenario;
            _history   = history;

            BuildUi();
            RebuildCameraList();
            UpdateStatus();
        }

        // ── Godot lifecycle ───────────────────────────────────────────────────

        public override void _Process(double delta)
        {
            bool inEdit = _gameState != null && _gameState.Mode == GameMode.Edit;

            // Leaving Edit while previewing must restore camera control (chrome/preview never persist into Play).
            if (!inEdit && _previewing) StopPreview();

            if (_canvas != null) _canvas.Visible = inEdit && _toolActive;
        }

        public override void _ExitTree()
        {
            if (_previewing) StopPreview();
        }

        /// <summary>V toggles the tool. Lower priority than <see cref="_Input"/>.</summary>
        public override void _UnhandledInput(InputEvent @event)
        {
            if (_gameState == null || _gameState.Mode != GameMode.Edit) return;
            if (@event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.V)
            {
                _toolActive = !_toolActive;
                if (!_toolActive && _previewing) StopPreview();
                GD.Print(_toolActive ? "[CameraTool] Active — capture / preview named cameras | V exit" : "[CameraTool] Inactive.");
                GetViewport().SetInputAsHandled();
            }
        }

        // ── Capture / preview ─────────────────────────────────────────────────

        /// <summary>Snapshot the live editor camera into a new named <see cref="ScenarioCamera"/> (one shared-history
        /// pair). No-op when no scenario is loaded (a fallback map cannot persist cameras).</summary>
        private void CaptureCurrentView()
        {
            if (_scenario == null) { GD.Print("[CameraTool] No scenario loaded — camera not persisted."); SetStatus("No scenario loaded."); return; }
            Camera3D? cam = _camCtrl?.GetCamera();
            if (cam == null) return;

            Vector3 pos     = cam.GlobalPosition;
            Vector3 forward = -cam.GlobalTransform.Basis.Z;
            Vector3 target  = pos + forward * 30f; // a look-at point 30 units ahead

            string name = _nameField?.Text.Trim() ?? "";
            if (string.IsNullOrEmpty(name)) name = UniqueName($"Camera {CameraCount() + 1}");
            else name = UniqueName(name);

            var camera = new ScenarioCamera
            {
                Name = name,
                X = pos.X, Y = pos.Y, Z = pos.Z,
                TargetX = target.X, TargetY = target.Y, TargetZ = target.Z,
                Fov = cam.Fov,
            };

            AddCamera(camera);
            int addedIndex = IndexOf(camera);
            _selected = addedIndex;
            RefreshAll();

            _history?.Push(
                redo: () => { AddCamera(camera, addedIndex); _selected = IndexOf(camera); RefreshAll(); },
                // Review fix: stop any active "view through" preview first — otherwise undoing a capture while previewing
                // removes the camera but leaves the RTS controller suppressed, freezing the editor camera.
                undo: () => { if (_previewing) StopPreview(); RemoveCamera(camera); _selected = -1; RefreshAll(); });

            GD.Print($"[CameraTool] Captured camera '{camera.Name}' at ({pos.X:F1},{pos.Y:F1},{pos.Z:F1}) fov={camera.Fov:F0}.");
        }

        /// <summary>Drive the editor camera to the selected stored camera (suppresses the RTS controller so the view
        /// holds). "Stop preview" re-enables the controller, which snaps back to its own rig on the next frame.</summary>
        private void PreviewSelected()
        {
            var cams = _scenario?.Cameras;
            if (cams == null || _selected < 0 || _selected >= cams.Length) return;
            Camera3D? cam = _camCtrl?.GetCamera();
            if (cam == null || _camCtrl == null) return;

            ScenarioCamera c = cams[_selected];
            _camCtrl.SetProcess(false); // suppress the rig so our transform holds
            _previewing = true;
            Vector3 eye = new Vector3(c.X, c.Y, c.Z);
            Vector3 tgt = new Vector3(c.TargetX, c.TargetY, c.TargetZ);
            Vector3 dir = tgt - eye;
            // Review fix: guard Godot's LookAt against a colinear up vector. A camera looking straight down/up (dir
            // parallel to Vector3.Up) yields an undefined/NaN basis; a degenerate eye==target has no direction at all.
            Vector3 up = Vector3.Up;
            if (dir.LengthSquared() < 1e-6f) tgt = eye + Vector3.Forward;
            else if (Mathf.Abs(dir.Normalized().Y) > 0.999f) up = Vector3.Forward;
            cam.GlobalPosition = eye;
            cam.LookAt(tgt, up);
            cam.Fov = c.Fov;
            UpdateStatus();
            GD.Print($"[CameraTool] Previewing '{c.Name}'.");
        }

        private void StopPreview()
        {
            _previewing = false;
            _camCtrl?.SetProcess(true); // restore RTS control (rig snaps back next frame)
            UpdateStatus();
        }

        private void DeleteSelected()
        {
            var cams = _scenario?.Cameras;
            if (cams == null || _selected < 0 || _selected >= cams.Length) return;
            int originalIndex = _selected;
            ScenarioCamera camera = cams[_selected];

            if (_previewing) StopPreview();
            RemoveCamera(camera);
            _selected = -1;
            RefreshAll();

            _history?.Push(
                redo: () => { RemoveCamera(camera); _selected = -1; RefreshAll(); },
                undo: () => { AddCamera(camera, originalIndex); _selected = IndexOf(camera); RefreshAll(); });

            GD.Print($"[CameraTool] Deleted camera '{camera.Name}'.");
        }

        // ── ScenarioData.Cameras mutation (empty → null so the key is omitted) ─────────────────────────────────

        private int CameraCount() => _scenario?.Cameras?.Length ?? 0;

        private void AddCamera(ScenarioCamera camera, int index = -1)
        {
            if (_scenario == null) return;
            var list = new List<ScenarioCamera>(_scenario.Cameras ?? System.Array.Empty<ScenarioCamera>());
            if (index < 0 || index > list.Count) list.Add(camera);
            else list.Insert(index, camera);
            _scenario.Cameras = list.ToArray();
        }

        private void RemoveCamera(ScenarioCamera camera)
        {
            if (_scenario?.Cameras == null) return;
            var list = new List<ScenarioCamera>(_scenario.Cameras);
            list.Remove(camera);
            _scenario.Cameras = list.Count == 0 ? null : list.ToArray();
        }

        private int IndexOf(ScenarioCamera camera)
        {
            var cams = _scenario?.Cameras;
            if (cams == null) return -1;
            for (int i = 0; i < cams.Length; i++) if (ReferenceEquals(cams[i], camera)) return i;
            return -1;
        }

        /// <summary>Return a name not colliding with any existing camera (the validator rejects duplicates).</summary>
        private string UniqueName(string desired)
        {
            var existing = new HashSet<string>();
            if (_scenario?.Cameras != null) foreach (var c in _scenario.Cameras) existing.Add(c.Name);
            if (!existing.Contains(desired)) return desired;
            int n = 2;
            string name;
            do { name = $"{desired} ({n++})"; } while (existing.Contains(name));
            return name;
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

            var title = new Label { Text = "NAMED CAMERAS" };
            title.AddThemeFontSizeOverride("font_size", 13);
            vbox.AddChild(title);

            var nameRow = new HBoxContainer();
            nameRow.AddChild(new Label { Text = "Name:", CustomMinimumSize = new Vector2(48, 0) });
            _nameField = new LineEdit { PlaceholderText = "camera name", CustomMinimumSize = new Vector2(180, 0) };
            AttachTip(_nameField, "Camera name", "The name applied to the next captured camera (unique; used by MoveCamera).", ChimeraTooltip.TooltipRole.Field);
            nameRow.AddChild(_nameField);
            vbox.AddChild(nameRow);

            var captureBtn = new Button { Text = "Capture current view" };
            captureBtn.Pressed += CaptureCurrentView;
            AttachTip(captureBtn, "Capture camera", "Snapshot the current editor camera (position/target/FOV) as a named camera (one undo step).");
            vbox.AddChild(captureBtn);

            vbox.AddChild(new Label { Text = "Cameras:" });
            _listBox = new VBoxContainer();
            _listBox.AddThemeConstantOverride("separation", 2);
            vbox.AddChild(_listBox);

            var previewRow = new HBoxContainer();
            var previewBtn = new Button { Text = "View through" };
            previewBtn.Pressed += PreviewSelected;
            AttachTip(previewBtn, "View through camera", "Drive the editor camera to the selected camera's viewpoint.");
            previewRow.AddChild(previewBtn);
            var stopBtn = new Button { Text = "Stop preview" };
            stopBtn.Pressed += StopPreview;
            AttachTip(stopBtn, "Stop preview", "Return control to the free editor camera.");
            previewRow.AddChild(stopBtn);
            vbox.AddChild(previewRow);

            var delBtn = new Button { Text = "Delete selected" };
            delBtn.Pressed += DeleteSelected;
            AttachTip(delBtn, "Delete camera", "Remove the selected camera (one undo step).");
            vbox.AddChild(delBtn);

            _statusLabel = new Label { Text = "" };
            _statusLabel.AddThemeFontSizeOverride("font_size", 11);
            vbox.AddChild(_statusLabel);
        }

        private void RebuildCameraList()
        {
            if (_listBox == null) return;
            foreach (Node c in _listBox.GetChildren()) { _listBox.RemoveChild(c); c.QueueFree(); }

            var cams = _scenario?.Cameras;
            if (cams == null || cams.Length == 0) { _listBox.AddChild(new Label { Text = "(none yet)" }); return; }

            var group = new ButtonGroup();
            for (int i = 0; i < cams.Length; i++)
            {
                int idx = i;
                var c = cams[i];
                var btn = new Button
                {
                    Text          = $"{c.Name}  (fov {c.Fov:F0})",
                    ToggleMode    = true,
                    ButtonGroup   = group,
                    ButtonPressed = (i == _selected),
                    Alignment     = HorizontalAlignment.Left,
                };
                btn.Pressed += () => { _selected = idx; UpdateStatus(); };
                _listBox.AddChild(btn);
            }
        }

        private void RefreshAll()
        {
            RebuildCameraList();
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            if (_statusLabel == null) return;
            _statusLabel.Text = $"Cameras: {CameraCount()}   Preview: {(_previewing ? "ON" : "OFF")}";
        }

        private void SetStatus(string msg) { if (_statusLabel != null) _statusLabel.Text = msg; }

        private static void AttachTip(Control target, string term, string body,
                                      ChimeraTooltip.TooltipRole role = ChimeraTooltip.TooltipRole.Pop)
            => ChimeraTooltip.AttachFocusable(target, term, body, role);
    }
}
