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

        // Story 3.10: re-entrancy guard so the invalid-scenario VETO (which reverts the mode via SetMode, re-emitting
        // ModeChanged) does not recursively re-run the reset. Set only for the brief revert, cleared immediately after.
        private bool _suppressReset;

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

            // Story 5.9 review pass: this panel's radios were a boot-time snapshot with nothing to re-sync them if
            // another surface (OnboardingPanel's win-condition step) mutates ScenarioData.WinCondition externally —
            // SetPressedNoSignal avoids re-emitting Toggled (which would just re-write the same value right back).
            _ctx.WinConditionUiRefresh = () =>
            {
                bool destroy = (_ctx.Scenario?.WinCondition ?? WinCondition.DestroyAllBuildings) == WinCondition.DestroyAllBuildings;
                btnBuildings.SetPressedNoSignal(destroy);
                btnUnits.SetPressedNoSignal(!destroy);
            };

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

            // Story 3.10 (NFR-1 / UX-DR62): route BOTH F5 edges through the in-place reset-to-authored-start (the
            // reset lives on MainScene — it touches match-lifecycle state MainScene keeps). Edit→Play starts the sim
            // from a clean authored board reflecting Edit-side trigger edits; Play→Edit restores the authored board.
            // On an invalid edited scenario the Edit→Play reset returns false → veto the toggle (revert to Edit,
            // surface the located error), never entering Play on invalid content. The panel shows only in Edit.
            _ctx.GameState.ModeChanged += (mode) =>
            {
                _ctx.WinConditionPanel.Visible = (mode == (int)GameMode.Edit);
                if (_suppressReset) return; // ignore the re-emit from the veto's SetMode revert

                // Story 3.10 — the authored-start round-trip reset is the OFFLINE editor playtest loop ONLY. Online
                // match-start (MatchLifecycleController.OnMatchStart) and replay playback (TryLoadReplay) also flip to
                // Play via GameState.SetMode(Play), emitting this same signal — they must NOT clear+re-apply (that
                // would re-apply mid-online-match and clobber the replay's restored RNG seed → desync). For those, keep
                // the pre-3.10 behavior: lifecycle-only reset on return to Edit, nothing on entering Play.
                bool offlineEditorLoop = _ctx.ReplayPlayer == null && !_ctx.Lockstep.IsOnline;

                if (mode == (int)GameMode.Play)
                {
                    if (offlineEditorLoop && !_ctx.Scene.ResetToAuthoredStart(_ctx.PersistenceTestMode))
                    {
                        // The set-true → revert → set-false bracket is correct ONLY because GameState.SetMode emits
                        // ModeChanged SYNCHRONOUSLY (GameState.cs) — the re-emitted Edit signal runs this handler and
                        // hits the `_suppressReset` guard above BEFORE control returns here to clear the flag. If mode
                        // emission ever becomes deferred/queued (CallDeferred), the flag would clear first and the
                        // re-emit's → Edit branch would run ResetToAuthoredStart, clearing the world the veto protects.
                        _suppressReset = true;
                        _ctx.GameState.SetMode(GameMode.Edit); // veto: stay in Edit (world already left unchanged)
                        _suppressReset = false;
                    }
                }
                else // → Edit
                {
                    // Offline editor loop: restore the authored board. If re-validation somehow fails on the return
                    // path (unreachable today — no editing happens during Play), still reset lifecycle state so we
                    // never leave a played-out board in Edit. Online/replay: pre-3.10 lifecycle-only reset.
                    if (!offlineEditorLoop || !_ctx.Scene.ResetToAuthoredStart(_ctx.PersistenceTestMode))
                        _ctx.Scene.ResetMatchOnReturnToEdit();
                }
            };

            _ctx.WinConditionPanel.Visible = (_ctx.GameState.Mode == GameMode.Edit);
        }

        private void ExportMapPackage(string mapName, string author, Label statusLabel)
        {
            if (_ctx.Scenario == null) { statusLabel.Text = "No scenario loaded."; return; }

            // Save current scenario state to disk first.
            string scenAbs = ProjectSettings.GlobalizePath(_ctx.Scene.ScenarioPath);

            // Story 6.2: persist the LIVE terrain beside the scenario and stamp TerrainRef BEFORE serializing, so the
            // saved JSON carries the ref and the package below can bundle the region files. Returns the terrain
            // folder's absolute path (null when there is no terrain / on a save failure — TerrainRef is cleared in
            // that case so a stale ref can't make the next load restore outdated terrain).
            string? terrainDir = SaveTerrainBesideScenario(scenAbs);

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
                var manifest = ContentPackager.Pack(scenAbs, outZip, opts, terrainDir);
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

        /// <summary>
        /// Story 6.2 — persist the live Terrain3D region data (height + control + color, captured together by
        /// Terrain3DData.save_directory) into a "{stem}_terrain/" folder beside the scenario JSON, and stamp the
        /// scenario's TerrainRef with that folder's res:// path (so ScenarioLoadPhase can resolve it back on load).
        /// Returns the folder's absolute OS path for packaging, or null when there is nothing to save.
        ///
        /// Guards (review pass 1): the folder is cleared/recreated first so orphaned .res from a prior, larger map
        /// are never packed or restored; on any failure TerrainRef is reset to "" (never left stale) so the next
        /// load falls back to flat rather than restoring outdated terrain.
        /// </summary>
        private string? SaveTerrainBesideScenario(string scenarioAbsPath)
        {
            if (_ctx.Terrain == null || _ctx.Scenario == null) return null; // PlaneMesh fallback / no scenario

            string stem       = System.IO.Path.GetFileNameWithoutExtension(scenarioAbsPath);
            string scenDir    = System.IO.Path.GetDirectoryName(scenarioAbsPath)!;
            string terrainAbs = System.IO.Path.Combine(scenDir, ContentPackager.TerrainFolderName(stem));

            try
            {
                // Clear/recreate so stale region files from a prior (possibly larger) save are not carried forward.
                if (System.IO.Directory.Exists(terrainAbs))
                    System.IO.Directory.Delete(terrainAbs, recursive: true);
                System.IO.Directory.CreateDirectory(terrainAbs);

                var data = _ctx.Terrain.Get("data").AsGodotObject();
                if (data == null) { _ctx.Scenario.TerrainRef = ""; return null; }

                // save_directory writes one terrain3d_XX_YY.res per active region (height+control+color together).
                data.Call("save_directory", terrainAbs);

                // Review pass 2 (EC9): a terrain node with zero active regions writes an empty folder. Keep the
                // empty-TerrainRef path byte-identical to today (no stamped ref pointing at an empty folder, no
                // spurious "folder has no region files" log on every subsequent load) by treating that as no terrain.
                if (System.IO.Directory.GetFiles(terrainAbs, "*.res").Length == 0)
                {
                    System.IO.Directory.Delete(terrainAbs, recursive: true);
                    _ctx.Scenario.TerrainRef = "";
                    return null;
                }

                // Stamp the res:// path so the load path resolves it independent of the absolute install location.
                _ctx.Scenario.TerrainRef = ProjectSettings.LocalizePath(terrainAbs);
                GD.Print($"[MapIO] Saved terrain → {_ctx.Scenario.TerrainRef}");
                return terrainAbs;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[MapIO] Terrain save failed ({ex.Message}) — clearing TerrainRef (map will load flat).");
                _ctx.Scenario.TerrainRef = "";
                return null;
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

                // Story 6.2: copy the bundled terrain region files into a folder tracked by manifest.Id and rewrite
                // the imported scenario's TerrainRef to point at it. The scenario is renamed to {id}.json on import,
                // so the terrain folder name must track manifest.Id (not the author's original stem).
                if (result.TerrainFiles.Count > 0)
                {
                    string terrainResDir = $"res://resources/data/scenarios/{ContentPackager.TerrainFolderName(manifest.Id)}/";
                    string terrainDestAbs = ProjectSettings.GlobalizePath(terrainResDir);
                    // Review pass 2 (F2): clear/recreate the destination first, mirroring the export save path. Re-
                    // importing a revised same-id package that dropped a region would otherwise leave the earlier
                    // import's orphaned terrain3d_*.res behind, and the load-time glob would restore that stale region.
                    if (System.IO.Directory.Exists(terrainDestAbs))
                        System.IO.Directory.Delete(terrainDestAbs, recursive: true);
                    System.IO.Directory.CreateDirectory(terrainDestAbs);
                    foreach (var tf in result.TerrainFiles)
                        System.IO.File.Copy(tf,
                            System.IO.Path.Combine(terrainDestAbs, System.IO.Path.GetFileName(tf)), overwrite: true);

                    var imported = ScenarioSerializer.LoadFromFile(destScenario);
                    if (imported != null)
                    {
                        imported.TerrainRef = terrainResDir;
                        ScenarioSerializer.SaveToFile(imported, destScenario);
                    }
                    else
                    {
                        GD.PrintErr("[MapIO] Terrain files copied but TerrainRef could not be rewritten " +
                                    "(scenario reload returned null) — imported map will load flat.");
                    }
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
