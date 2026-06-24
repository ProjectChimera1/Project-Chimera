#nullable enable
using Godot;
using System;
using ProjectChimera.Core.Definitions;
using ProjectChimera.UI;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 1.8c "WinConditionUi" phase (runtime position 15) + the Map-I/O controller. Builds the Edit-mode
    /// win-condition panel (Destroy-All-Buildings / Eliminate-All-Units radio + map-package name/author fields and
    /// export/import buttons) and owns ExportMapPackage / ImportMapPackage / DoImport. The return-to-Edit reset is
    /// delegated to MainScene.ResetMatchOnReturnToEdit (it touches the match-lifecycle state MainScene keeps).
    /// Publishes ctx.WinConditionPanel. Behavior-identical to MainScene.SetupWinConditionUi + the Map-I/O methods.
    /// </summary>
    public sealed class WinConditionPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public WinConditionPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "WinConditionUi";

        public void Run()
        {
            float vpWidth = _ctx.Scene.GetViewport().GetVisibleRect().Size.X;

            var panel = new PanelContainer
            {
                Position          = new Vector2(vpWidth - 360f, 330f),
                CustomMinimumSize = new Vector2(350f, 0f),
            };

            var vbox = new VBoxContainer();
            panel.AddChild(vbox);

            var title = new Label { Text = "Win Condition" };
            title.AddThemeFontSizeOverride("font_size", 14);
            vbox.AddChild(title);

            var group = new ButtonGroup();
            var current = _ctx.Scenario?.WinCondition ?? WinCondition.DestroyAllBuildings;

            var btnBuildings = new Button
            {
                Text          = "Destroy All Buildings",
                ToggleMode    = true,
                ButtonPressed = current == WinCondition.DestroyAllBuildings,
            };
            btnBuildings.ButtonGroup = group;
            vbox.AddChild(btnBuildings);

            var btnUnits = new Button
            {
                Text          = "Eliminate All Units",
                ToggleMode    = true,
                ButtonPressed = current == WinCondition.EliminateAllUnits,
            };
            btnUnits.ButtonGroup = group;
            vbox.AddChild(btnUnits);

            btnBuildings.Toggled += (on) => { if (on && _ctx.Scenario != null) _ctx.Scenario.WinCondition = WinCondition.DestroyAllBuildings; };
            btnUnits.Toggled     += (on) => { if (on && _ctx.Scenario != null) _ctx.Scenario.WinCondition = WinCondition.EliminateAllUnits; };

            // ── Map I/O section ────────────────────────────────────────────────
            vbox.AddChild(new HSeparator());

            var ioTitle = new Label { Text = "Map Package" };
            ioTitle.AddThemeFontSizeOverride("font_size", 13);
            vbox.AddChild(ioTitle);

            // Map name field (pre-filled from scenario display name)
            var nameRow = new HBoxContainer();
            nameRow.AddChild(new Label { Text = "Name:", CustomMinimumSize = new Vector2(54, 0) });
            var mapNameField = new LineEdit
            {
                Text                = _ctx.Scenario?.DisplayName ?? "My Map",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                MaxLength           = 64,
            };
            mapNameField.AddThemeFontSizeOverride("font_size", 12);
            nameRow.AddChild(mapNameField);
            vbox.AddChild(nameRow);

            // Author field
            var authorRow = new HBoxContainer();
            authorRow.AddChild(new Label { Text = "Author:", CustomMinimumSize = new Vector2(54, 0) });
            var authorField = new LineEdit
            {
                Text                = "Unknown",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                MaxLength           = 40,
            };
            authorField.AddThemeFontSizeOverride("font_size", 12);
            authorRow.AddChild(authorField);
            vbox.AddChild(authorRow);

            // Export / Import buttons
            var btnRow = new HBoxContainer();
            btnRow.AddThemeConstantOverride("separation", 6);
            var exportBtn = new Button { Text = "Export .chimera.zip",
                                         CustomMinimumSize = new Vector2(160, 30) };
            var importBtn = new Button { Text = "Import .chimera.zip",
                                         CustomMinimumSize = new Vector2(160, 30) };
            exportBtn.AddThemeFontSizeOverride("font_size", 12);
            importBtn.AddThemeFontSizeOverride("font_size", 12);
            btnRow.AddChild(exportBtn);
            btnRow.AddChild(importBtn);
            vbox.AddChild(btnRow);

            var ioStatusLabel = new Label { Text = "" };
            ioStatusLabel.AddThemeFontSizeOverride("font_size", 11);
            ioStatusLabel.AutowrapMode = TextServer.AutowrapMode.Word;
            vbox.AddChild(ioStatusLabel);

            exportBtn.Pressed += () => ExportMapPackage(
                mapNameField.Text.Trim(), authorField.Text.Trim(), ioStatusLabel);
            importBtn.Pressed += () => ImportMapPackage(ioStatusLabel);

            _ctx.WinConditionPanel = panel;
            _ctx.UiCanvas.AddChild(panel);

            // Show only in Edit mode; reset game state on return from Play (the reset lives on MainScene — it
            // touches the match-lifecycle state MainScene keeps).
            _ctx.GameState.ModeChanged += (mode) =>
            {
                _ctx.WinConditionPanel.Visible = (mode == (int)GameMode.Edit);
                if (mode == (int)GameMode.Edit)
                    _ctx.Scene.ResetMatchOnReturnToEdit();
            };

            _ctx.WinConditionPanel.Visible = (_ctx.GameState.Mode == GameMode.Edit);
        }

        private void ExportMapPackage(string mapName, string author, Label statusLabel)
        {
            if (_ctx.Scenario == null) { statusLabel.Text = "No scenario loaded."; return; }

            // Save current scenario state to disk first.
            string scenAbs = ProjectSettings.GlobalizePath(_ctx.Scene.ScenarioPath);
            try { ScenarioSerializer.SaveToFile(_ctx.Scenario, scenAbs); }
            catch (Exception ex) { statusLabel.Text = $"Save failed: {ex.Message}"; return; }

            // Determine output path: same directory as scenario, same slug name.
            string slug   = ContentPackager.Slugify(
                string.IsNullOrEmpty(mapName) ? _ctx.Scenario.DisplayName : mapName);
            string outDir = System.IO.Path.GetDirectoryName(scenAbs)!;
            string outZip = System.IO.Path.Combine(outDir, $"{slug}.chimera.zip");

            var opts = new ContentPackager.PackOptions
            {
                DisplayName   = string.IsNullOrEmpty(mapName) ? _ctx.Scenario.DisplayName : mapName,
                Author        = string.IsNullOrEmpty(author) ? "Unknown" : author,
                Description   = _ctx.Scenario.DisplayName,
                PlayerCount   = _ctx.Scenario.PlayerSlots?.Length ?? 2,
                Tags          = new System.Collections.Generic.List<string>
                {
                    _ctx.Scenario.PlayerSlots?.Length == 4 ? "2v2" : "1v1"
                },
            };

            try
            {
                var manifest = ContentPackager.Pack(scenAbs, outZip, opts);
                statusLabel.Text = $"Exported: {System.IO.Path.GetFileName(outZip)}\n" +
                                   $"Hash: 0x{manifest.ScenarioHash:X8}";
                GD.Print($"[MapIO] Exported package: {outZip}");
            }
            catch (Exception ex)
            {
                statusLabel.Text = $"Export failed: {ex.Message}";
                GD.PrintErr($"[MapIO] Export error: {ex}");
            }
        }

        private void ImportMapPackage(Label statusLabel)
        {
            // Open a native file dialog via Godot's FileDialog node.
            var dlg = new FileDialog
            {
                FileMode  = FileDialog.FileModeEnum.OpenFile,
                Access    = FileDialog.AccessEnum.Filesystem,
                Title     = "Import Map Package",
                Filters   = new[] { "*.chimera.zip ; Chimera Map Package" },
            };
            dlg.FileSelected += (path) =>
            {
                dlg.QueueFree();
                DoImport(path, statusLabel);
            };
            dlg.Canceled += () => dlg.QueueFree();
            _ctx.Scene.AddChild(dlg);
            dlg.PopupCentered(new Vector2I(900, 600));
        }

        private void DoImport(string zipPath, Label statusLabel)
        {
            // Extract to user://imported_maps/<slug>/
            var manifest = ContentPackager.ReadManifest(zipPath);
            if (manifest == null) { statusLabel.Text = "Invalid package (no manifest)."; return; }

            string extractDir = ProjectSettings.GlobalizePath(
                $"user://imported_maps/{manifest.Id}/");
            try
            {
                var result = ContentPackager.Unpack(zipPath, extractDir);
                // Copy the scenario to the project's scenarios directory so it can be selected.
                string destScenario = ProjectSettings.GlobalizePath(
                    $"res://resources/data/scenarios/{manifest.Id}.json");
                System.IO.File.Copy(result.ScenarioPath, destScenario, overwrite: true);

                // Copy any custom faction files.
                foreach (var fp in result.FactionPaths)
                {
                    string destFaction = ProjectSettings.GlobalizePath(
                        $"res://resources/data/factions/{System.IO.Path.GetFileName(fp)}");
                    System.IO.File.Copy(fp, destFaction, overwrite: true);
                }

                statusLabel.Text = $"Imported: {manifest.DisplayName}\n" +
                                   $"by {manifest.Author} v{manifest.Version}\n" +
                                   $"Set ScenarioPath to: res://resources/data/scenarios/{manifest.Id}.json";
                GD.Print($"[MapIO] Imported '{manifest.DisplayName}' → {destScenario}");
            }
            catch (Exception ex)
            {
                statusLabel.Text = $"Import failed: {ex.Message}";
                GD.PrintErr($"[MapIO] Import error: {ex}");
            }
        }
    }
}
