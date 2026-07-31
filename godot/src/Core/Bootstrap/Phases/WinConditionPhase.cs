#nullable enable
using Godot;
using System;
using ProjectChimera.Core.Definitions;
using ProjectChimera.UGC;
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

            // Story 7.11: the picker expands from 2 built-in toggles to ALL SIX options (2 built-in + 4 T1 presets),
            // each preset's required param fields shown inline. Selecting an option writes ScenarioData.WinCondition /
            // WinConditionSpec so a Save/reload restores the same selection + params.
            var group = new ButtonGroup();

            WinPresetKind curPreset  = _ctx.Scenario?.WinConditionSpec?.Preset ?? WinPresetKind.None;
            WinCondition  curBuiltin = _ctx.Scenario?.WinCondition ?? WinCondition.DestroyAllBuildings;
            WinConditionSpec? curSpec = _ctx.Scenario?.WinConditionSpec;

            // ── Preset param field controls (built first so the toggle handlers can capture them). ──
            SpinBox NewIntSpin(int min, int max, int val)
                => new SpinBox { MinValue = min, MaxValue = max, Step = 1, Value = val,
                                 CustomMinimumSize = new Vector2(96, 26) };

            var kothRegion    = new LineEdit { PlaceholderText = "region id", Text = curSpec?.RegionId ?? "",
                                               CustomMinimumSize = new Vector2(120, 26) };
            var kothHold      = NewIntSpin(1, 1_000_000, System.Math.Max(1, curSpec?.HoldTicks ?? 300));
            // Review P4 — the survival slot is capped at the ENGINE faction ceiling (slot 3 / Faction.Player4,
            // the same CheckFactionSlot bound the validator enforces): the sim's win stores track only factions
            // 1-4, so slots 4-7 would author a scenario the validator rejects at load.
            var survSlot      = NewIntSpin(0, 3, System.Math.Max(0, curSpec?.FactionSlot ?? 0));
            var survTicks     = NewIntSpin(1, 100_000_000, System.Math.Max(1, curSpec?.SurviveTicks ?? 900));
            var assassinIndex = NewIntSpin(0, 100_000, System.Math.Max(0, curSpec?.LeaderUnitIndex ?? 0));
            var landmarkIndex = NewIntSpin(0, 100_000, System.Math.Max(0, curSpec?.StructureIndex ?? 0));

            Control ParamRow(string label, Control field)
            {
                var row = new HBoxContainer();
                var l = new Label { Text = label, CustomMinimumSize = new Vector2(110, 0) };
                l.AddThemeFontSizeOverride("font_size", 11);
                row.AddChild(l);
                field.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                row.AddChild(field);
                return row;
            }

            var kothPanel = new VBoxContainer();
            kothPanel.AddChild(ParamRow("Region id", kothRegion));
            kothPanel.AddChild(ParamRow("Hold ticks", kothHold));
            var survPanel = new VBoxContainer();
            survPanel.AddChild(ParamRow("Faction slot", survSlot));
            survPanel.AddChild(ParamRow("Survive ticks", survTicks));
            var assassinPanel = new VBoxContainer();
            assassinPanel.AddChild(ParamRow("Leader unit #", assassinIndex));
            var landmarkPanel = new VBoxContainer();
            landmarkPanel.AddChild(ParamRow("Structure #", landmarkIndex));

            Button MakeToggle(string text, bool pressed)
            {
                var b = new Button { Text = text, ToggleMode = true, ButtonPressed = pressed };
                b.AddThemeFontSizeOverride("font_size", 12);
                b.ButtonGroup = group;
                vbox.AddChild(b);
                return b;
            }

            var btnBuildings = MakeToggle("Destroy All Buildings",
                curPreset == WinPresetKind.None && curBuiltin == WinCondition.DestroyAllBuildings);
            var btnUnits = MakeToggle("Eliminate All Units",
                curPreset == WinPresetKind.None && curBuiltin == WinCondition.EliminateAllUnits);
            var btnKoth = MakeToggle("King of the Hill", curPreset == WinPresetKind.KingOfTheHill);
            vbox.AddChild(kothPanel);
            var btnSurv = MakeToggle("Timed Survival", curPreset == WinPresetKind.TimedSurvival);
            vbox.AddChild(survPanel);
            var btnAssassin = MakeToggle("Assassination", curPreset == WinPresetKind.Assassination);
            vbox.AddChild(assassinPanel);
            var btnLandmark = MakeToggle("Landmark Destruction", curPreset == WinPresetKind.LandmarkDestruction);
            vbox.AddChild(landmarkPanel);

            WinConditionSpec EnsureSpec()
            {
                _ctx.Scenario!.WinConditionSpec ??= new WinConditionSpec();
                return _ctx.Scenario.WinConditionSpec;
            }

            void RefreshPanels()
            {
                kothPanel.Visible     = btnKoth.ButtonPressed;
                survPanel.Visible     = btnSurv.ButtonPressed;
                assassinPanel.Visible = btnAssassin.ButtonPressed;
                landmarkPanel.Visible = btnLandmark.ButtonPressed;
            }

            // A built-in toggle clears the preset spec (null → the bare enum path); a preset toggle writes its kind +
            // current param values. Only the ON edge acts (a ButtonGroup emits an OFF edge on the deselected button).
            btnBuildings.Toggled += on => { if (!on || _ctx.Scenario == null) return;
                _ctx.Scenario.WinCondition = WinCondition.DestroyAllBuildings; _ctx.Scenario.WinConditionSpec = null; RefreshPanels(); };
            btnUnits.Toggled += on => { if (!on || _ctx.Scenario == null) return;
                _ctx.Scenario.WinCondition = WinCondition.EliminateAllUnits; _ctx.Scenario.WinConditionSpec = null; RefreshPanels(); };
            btnKoth.Toggled += on => { if (!on || _ctx.Scenario == null) return;
                var sp = EnsureSpec(); sp.Preset = WinPresetKind.KingOfTheHill; sp.RegionId = kothRegion.Text; sp.HoldTicks = (int)kothHold.Value; RefreshPanels(); };
            btnSurv.Toggled += on => { if (!on || _ctx.Scenario == null) return;
                var sp = EnsureSpec(); sp.Preset = WinPresetKind.TimedSurvival; sp.FactionSlot = (int)survSlot.Value; sp.SurviveTicks = (int)survTicks.Value; RefreshPanels(); };
            btnAssassin.Toggled += on => { if (!on || _ctx.Scenario == null) return;
                var sp = EnsureSpec(); sp.Preset = WinPresetKind.Assassination; sp.LeaderUnitIndex = (int)assassinIndex.Value; RefreshPanels(); };
            btnLandmark.Toggled += on => { if (!on || _ctx.Scenario == null) return;
                var sp = EnsureSpec(); sp.Preset = WinPresetKind.LandmarkDestruction; sp.StructureIndex = (int)landmarkIndex.Value; RefreshPanels(); };

            // Live param edits persist into the active preset's spec.
            kothRegion.TextChanged     += t => { if (btnKoth.ButtonPressed && _ctx.Scenario?.WinConditionSpec is { } s) s.RegionId = t; };
            kothHold.ValueChanged      += v => { if (btnKoth.ButtonPressed && _ctx.Scenario?.WinConditionSpec is { } s) s.HoldTicks = (int)v; };
            survSlot.ValueChanged      += v => { if (btnSurv.ButtonPressed && _ctx.Scenario?.WinConditionSpec is { } s) s.FactionSlot = (int)v; };
            survTicks.ValueChanged     += v => { if (btnSurv.ButtonPressed && _ctx.Scenario?.WinConditionSpec is { } s) s.SurviveTicks = (int)v; };
            assassinIndex.ValueChanged += v => { if (btnAssassin.ButtonPressed && _ctx.Scenario?.WinConditionSpec is { } s) s.LeaderUnitIndex = (int)v; };
            landmarkIndex.ValueChanged += v => { if (btnLandmark.ButtonPressed && _ctx.Scenario?.WinConditionSpec is { } s) s.StructureIndex = (int)v; };

            RefreshPanels();

            // Story 5.9 review pass: re-sync the picker if another surface mutates the model externally.
            // SetPressedNoSignal / SetValueNoSignal avoid re-emitting handlers that would just re-write the value.
            _ctx.WinConditionUiRefresh = () =>
            {
                WinPresetKind p = _ctx.Scenario?.WinConditionSpec?.Preset ?? WinPresetKind.None;
                WinCondition  b = _ctx.Scenario?.WinCondition ?? WinCondition.DestroyAllBuildings;
                btnBuildings.SetPressedNoSignal(p == WinPresetKind.None && b == WinCondition.DestroyAllBuildings);
                btnUnits.SetPressedNoSignal(p == WinPresetKind.None && b == WinCondition.EliminateAllUnits);
                btnKoth.SetPressedNoSignal(p == WinPresetKind.KingOfTheHill);
                btnSurv.SetPressedNoSignal(p == WinPresetKind.TimedSurvival);
                btnAssassin.SetPressedNoSignal(p == WinPresetKind.Assassination);
                btnLandmark.SetPressedNoSignal(p == WinPresetKind.LandmarkDestruction);
                if (_ctx.Scenario?.WinConditionSpec is { } sp)
                {
                    kothRegion.Text = sp.RegionId ?? "";
                    if (sp.HoldTicks > 0)    kothHold.SetValueNoSignal(sp.HoldTicks);
                    survSlot.SetValueNoSignal(sp.FactionSlot);
                    if (sp.SurviveTicks > 0) survTicks.SetValueNoSignal(sp.SurviveTicks);
                    assassinIndex.SetValueNoSignal(sp.LeaderUnitIndex);
                    landmarkIndex.SetValueNoSignal(sp.StructureIndex);
                }
                else
                {
                    // Review P11 — no active spec (an external surface cleared it, e.g. a built-in selection):
                    // restore the initial-build defaults, otherwise the stale params of a previously-selected
                    // preset silently re-enter the spec on the next preset toggle.
                    kothRegion.Text = "";
                    kothHold.SetValueNoSignal(300);
                    survSlot.SetValueNoSignal(0);
                    survTicks.SetValueNoSignal(900);
                    assassinIndex.SetValueNoSignal(0);
                    landmarkIndex.SetValueNoSignal(0);
                }
                RefreshPanels();
            };

            // ── Map I/O section ────────────────────────────────────────────────
            vbox.AddChild(new HSeparator());

            var ioTitle = new Label { Text = "Map Properties" };
            ioTitle.AddThemeFontSizeOverride("font_size", 13);
            vbox.AddChild(ioTitle);

            // Story 6.7 — "New Map" affordance: opens the design-system New-Map modal, which builds a blank map via
            // ScenarioData.CreateBlank and writes it to the scenarios folder (the same "set ScenarioPath to…" hand-off
            // the Import flow uses — no risky live-swap of the applied scenario mid-edit).
            var newMapBtn = new Button { Text = "New Map…", CustomMinimumSize = new Vector2(160, 28) };
            newMapBtn.AddThemeFontSizeOverride("font_size", 12);
            vbox.AddChild(newMapBtn);

            // Story 6.7 — the editable Map-Properties panel (name/author/description/suggested-players/size) bound
            // LIVE to the applied ScenarioData, so edits persist on Save and feed the export options below directly
            // (no placeholder LineEdits). Only shown when a scenario is loaded.
            if (_ctx.Scenario != null)
                vbox.AddChild(ProjectChimera.CreationSuite.MapPropertiesPanel.BuildPropertiesEditor(_ctx.Scenario));

            vbox.AddChild(new HSeparator());
            var pkgTitle = new Label { Text = "Map Package" };
            pkgTitle.AddThemeFontSizeOverride("font_size", 13);
            vbox.AddChild(pkgTitle);

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

            newMapBtn.Pressed += () => ProjectChimera.CreationSuite.MapPropertiesPanel.OpenNewMapDialog(
                _ctx.UiCanvas, blank => CreateNewMap(blank, ioStatusLabel));
            // Story 6.7 (patch 7) — disable the button for the export duration so a double-click cannot spawn
            // overlapping preview renders / concurrent packaging.
            exportBtn.Pressed += async () =>
            {
                exportBtn.Disabled = true;
                try { await ExportMapPackage(ioStatusLabel); }
                finally { exportBtn.Disabled = false; }
            };
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
                // DW-22: the routing decision is the pure, Tier-1-tested ModeTransitionResetPolicy.Decide — the single
                // source of truth. AuthoredStart ⟺ offline editor loop (!isOnline && !hasReplay), both directions.
                var resetAction = ModeTransitionResetPolicy.Decide(
                    _ctx.Lockstep.IsOnline, _ctx.ReplayPlayer != null, mode == (int)GameMode.Play);

                if (mode == (int)GameMode.Play)
                {
                    if (resetAction == ModeResetAction.AuthoredStart && !_ctx.Scene.ResetToAuthoredStart(_ctx.PersistenceTestMode))
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
                    if (resetAction != ModeResetAction.AuthoredStart || !_ctx.Scene.ResetToAuthoredStart(_ctx.PersistenceTestMode))
                        _ctx.Scene.ResetMatchOnReturnToEdit();
                }
            };

            _ctx.WinConditionPanel.Visible = (_ctx.GameState.Mode == GameMode.Edit);
        }

        /// <summary>
        /// Story 6.7 — the New-Map hand-off: persist the freshly-built blank scenario to the scenarios folder and
        /// tell the author how to load it (mirroring the Import flow's "set ScenarioPath to…" UX rather than a risky
        /// mid-edit live-swap of the applied scenario).
        /// </summary>
        private void CreateNewMap(ScenarioData blank, Label statusLabel)
        {
            try
            {
                string slug = ContentPackager.Slugify(
                    string.IsNullOrEmpty(blank.DisplayName) ? "new-map" : blank.DisplayName);
                if (string.IsNullOrEmpty(slug)) slug = "new-map";
                blank.Id = slug;
                string dest = ProjectSettings.GlobalizePath($"res://resources/data/scenarios/{slug}.json");
                // Story 6.7 (patch 5) — never silently overwrite an existing scenario file.
                if (System.IO.File.Exists(dest))
                {
                    statusLabel.Text = $"A map named '{blank.DisplayName}' already exists — choose a different name.";
                    return;
                }
                // Story 14.7 (DW-164) — HARD Validate before any write; nothing partial on disk on failure. This is a
                // hard gate distinct from the non-fatal CollectAdvisories below; do NOT weaken it to an advisory. The
                // blank has no pre-placed custom buildings → null faction defs is correct here.
                string? gateError = MapWriteGate.Check(blank);
                if (gateError != null)
                {
                    statusLabel.Text = $"New map blocked — validation failed: {gateError}";
                    GD.PrintErr($"[MapIO] New map blocked — validation failed: {gateError}");
                    return;
                }
                ScenarioSerializer.SaveToFile(blank, dest);
                // Story 6.7 (patch 12) — route the size display through the one MapSize helper.
                statusLabel.Text = $"Created blank {blank.SuggestedPlayers}-player map " +
                                   $"({MapSizes.Label(MapSizes.FromBounds(blank.MapBounds))}).\n" +
                                   $"Set ScenarioPath to: res://resources/data/scenarios/{slug}.json";
                // Story 6.7 (patch 4) — surface non-blocking authoring advisories on creation too.
                var advisories = new ScenarioValidator().CollectAdvisories(blank);
                if (advisories.Count > 0)
                    statusLabel.Text += "\n⚠ " + string.Join("; ", advisories);
                GD.Print($"[MapIO] New map created → {dest}");
            }
            catch (Exception ex)
            {
                statusLabel.Text = $"New map failed: {ex.Message}";
                GD.PrintErr($"[MapIO] New map error: {ex}");
            }
        }

        private async System.Threading.Tasks.Task ExportMapPackage(Label statusLabel)
        {
            if (_ctx.Scenario == null) { statusLabel.Text = "No scenario loaded."; return; }

            // Story 14.7 (DW-164) — HARD Validate BEFORE any disk mutation. This must be the first statement after
            // the null check so a rejected export leaves nothing partial on disk — no terrain files (this precedes
            // SaveTerrainBesideScenario, which writes region files first), no scenario.json overwrite, no
            // .chimera.zip. Pass the resolved per-slot faction defs so the verdict matches what reload produces
            // (mirrors ScenarioLoadPhase.ValidateBeforeApply). This is a hard gate distinct from the non-fatal
            // post-write CollectAdvisories; do NOT weaken it to an advisory.
            string? gateError = MapWriteGate.Check(_ctx.Scenario, _ctx.SlotFactionDefs);
            if (gateError != null)
            {
                statusLabel.Text = $"Export blocked — validation failed: {gateError}";
                GD.PrintErr($"[MapIO] Export blocked — validation failed: {gateError}");
                return;
            }

            // Save current scenario state to disk first.
            string scenAbs = ProjectSettings.GlobalizePath(_ctx.Scene.ScenarioPath);

            // Story 6.2: persist the LIVE terrain beside the scenario and stamp TerrainRef BEFORE serializing, so the
            // saved JSON carries the ref and the package below can bundle the region files. Returns the terrain
            // folder's absolute path (null when there is no terrain / on a save failure — TerrainRef is cleared in
            // that case so a stale ref can't make the next load restore outdated terrain).
            string? terrainDir = SaveTerrainBesideScenario(scenAbs);

            try { ScenarioSerializer.SaveToFile(_ctx.Scenario, scenAbs); }
            catch (Exception ex) { statusLabel.Text = $"Save failed: {ex.Message}"; return; }

            // Story 6.7 — auto-generate the top-down minimap preview into the package. Null on any render failure ⇒
            // the preview slot is simply omitted (pre-6.7 package parity). Rendered BEFORE Pack so the bytes are ready.
            byte[]? previewPng = await RenderMinimapPreview();

            // Story 6.7 — read the real authored metadata off the live ScenarioData (not placeholder LineEdits): the
            // Map-Properties panel bound DisplayName/Author/Description/SuggestedPlayers directly onto this model.
            string mapName = string.IsNullOrEmpty(_ctx.Scenario.DisplayName) ? "My Map" : _ctx.Scenario.DisplayName;
            int playerCount = _ctx.Scenario.PlayerSlots?.Length ?? 2;

            // Determine output path: same directory as scenario, same slug name.
            string slug   = ContentPackager.Slugify(mapName);
            string outDir = System.IO.Path.GetDirectoryName(scenAbs)!;
            string outZip = System.IO.Path.Combine(outDir, $"{slug}.chimera.zip");

            // Story 9.8 — load the proof-of-play token (minted on a prior self-victory, keyed by scenario identity)
            // and capture ≥1 screenshot, so the packaged manifest carries the token + screenshots the publish gate
            // requires. Both are best-effort: a missing token / failed grab simply yields a package the gate later
            // refuses (with the specific reason), never a failed export.
            string scenarioId = ProofOfPlayMint.ResolveScenarioId(_ctx.Scenario);
            ProofOfPlayToken? token = null;
            try { new ProofOfPlayStore(ProjectSettings.GlobalizePath(ProofOfPlayMint.TokenDirGodotPath)).TryLoad(scenarioId, out token); }
            catch (Exception ex) { GD.PrintErr($"[MapIO] Proof-of-play load failed: {ex.Message}"); }

            var screenshotPaths = new System.Collections.Generic.List<string>();
            string? shot = CaptureScreenshot(scenarioId);
            if (shot != null) screenshotPaths.Add(shot);

            var opts = new ContentPackager.PackOptions
            {
                DisplayName     = mapName,
                Author          = string.IsNullOrEmpty(_ctx.Scenario.Author) ? "Unknown" : _ctx.Scenario.Author!,
                Description     = _ctx.Scenario.Description ?? "",
                PlayerCount     = playerCount,
                PreviewPngBytes = previewPng,
                Token           = token,
                ScreenshotPaths = screenshotPaths,
                Tags            = new System.Collections.Generic.List<string>
                {
                    playerCount switch { 4 => "2v2", 3 => "ffa3", _ => "1v1" }
                },
            };

            try
            {
                var manifest = ContentPackager.Pack(scenAbs, outZip, opts, terrainDir);
                statusLabel.Text = $"Exported: {System.IO.Path.GetFileName(outZip)}\n" +
                                   $"Hash: 0x{manifest.ScenarioHash:X8}" +
                                   (previewPng != null ? "\nPreview: preview/preview.png" : "\n(no preview)");
                // Story 6.7 (patch 4) — surface AC2's non-blocking authoring advisories after a successful export.
                var advisories = new ScenarioValidator().CollectAdvisories(_ctx.Scenario);
                if (advisories.Count > 0)
                    statusLabel.Text += "\n⚠ " + string.Join("; ", advisories);
                GD.Print($"[MapIO] Exported package: {outZip}");
            }
            catch (Exception ex)
            {
                statusLabel.Text = $"Export failed: {ex.Message}";
                GD.PrintErr($"[MapIO] Export error: {ex}");
            }
        }

        /// <summary>
        /// Story 6.7 — render the top-down minimap preview PNG for the current map. Returns null (no preview) on any
        /// failure so the export still produces a valid package.
        /// </summary>
        private async System.Threading.Tasks.Task<byte[]?> RenderMinimapPreview()
        {
            try
            {
                var renderer = new UI.MinimapPreviewRenderer();
                _ctx.Scene.AddChild(renderer);
                // Story 6.7 (patch 6) — free the outer renderer node on ALL paths (the catch used to leak it).
                try
                {
                    World3D world = _ctx.Scene.GetViewport().World3D;
                    float half = _ctx.Scenario?.MapBounds ?? 128f;
                    return await renderer.RenderPreviewPngAsync(world, half);
                }
                finally { renderer.QueueFree(); }
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[MapIO] Preview render failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Story 9.8 — capture the current viewport frame to a PNG under <c>user://tokens/screenshots/</c> and return
        /// its absolute path (null on any failure ⇒ no screenshot bundled, the gate then refuses with the specific
        /// reason). One shot satisfies the ≥1-screenshot min-quality floor; the creator can add more out-of-band.
        /// </summary>
        private string? CaptureScreenshot(string scenarioId)
        {
            try
            {
                Image img = _ctx.Scene.GetViewport().GetTexture().GetImage();
                if (img == null) return null;
                string dirAbs = ProjectSettings.GlobalizePath(ProofOfPlayMint.TokenDirGodotPath + "/screenshots");
                System.IO.Directory.CreateDirectory(dirAbs);
                string shotAbs = System.IO.Path.Combine(dirAbs,
                    $"{ProofOfPlayStore.Sanitize(scenarioId)}_shot.png");
                Error err = img.SavePng(shotAbs);
                if (err != Error.Ok) { GD.PrintErr($"[MapIO] Screenshot save failed: {err}"); return null; }
                return shotAbs;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[MapIO] Screenshot capture failed: {ex.Message}");
                return null;
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
