#nullable enable
using System.IO;
using System.Linq;
using Godot;
using ProjectChimera.Core.Definitions;
using ProjectChimera.UI.Components;
using ProjectChimera.UI.Theme;

namespace ProjectChimera.CreationSuite
{
    public partial class FactionDefinerPanel
    {
        /// <summary>One pickable Okabe-Ito colorblind-safe swatch (UX-DR40) — a fixed hex + a distinguishing
        /// glyph + label pair, so color is never the only signal. The exact 8-color, 8-glyph closed set the story
        /// requires.</summary>
        private static readonly (string Hex, string Glyph, string Label)[] ColorSwatchDefs =
        {
            ("#E69F00", "◆", "Team 1"),   // Orange
            ("#56B4E9", "▲", "Team 2"),   // Sky Blue
            ("#009E73", "●", "Team 3"),   // Bluish Green
            ("#F0E442", "■", "Team 4"),   // Yellow
            ("#0072B2", "★", "Team 5"),   // Blue
            ("#D55E00", "✚", "Team 6"),   // Vermillion
            ("#CC79A7", "◐", "Team 7"),   // Reddish Purple
            ("#000000", "▣", "Team 8"),   // Black
        };

        // ── Wizard state (the FactionDefinition under construction + the scanned preset pools) ──
        private FactionDefinition _draft = new();
        private FactionPresetPool _presets = new();

        // ── Per-step state re-scan / reset ───────────────────────────────────────

        /// <summary>Start a brand-new wizard session: fresh draft, re-scanned preset pools, step 0. Called on every
        /// panel open (Story 5.5 — the wizard never carries partial state across a close, matching "always creates
        /// a brand-new file").</summary>
        private void ResetWizard()
        {
            string factionsDirAbs = ProjectSettings.GlobalizePath(FACTIONS_DIR_RES);
            var scanPaths = PresetSourceFiles.Select(f => Path.Combine(factionsDirAbs, f));
            _presets = FactionDefinerWizardCore.ScanPresets(scanPaths);

            _draft = new FactionDefinition();   // Id/DisplayName empty, Color default, AiPreset "balanced", Starting* defaults
            _draft.AiPreset = "";   // Story 5.6 (FR-18): override the FactionDefinition C# default so the AI Preset
                                     // step opens with NOTHING selected — Finish is genuinely blockable until a
                                     // creator explicitly picks one.

            _paneDirty = false;   // a fresh session never carries a "discard" warning from a prior one

            ClearStatus();
            _modeTabs.SetActive(0);   // "Toggle always resets to Simple mode" — fires OnModeTabChanged, which sets
                                      // _advancedMode = false and restores step-tabs/scroll/json-pane visibility.
            _stepTabs.SetActive(0);
            RefreshStepBody();   // SetActive(0) fires TabChanged → RefreshStepBody already, but the panel may have
                                  // been freshly built with Active already 0 (no-op change) — call explicitly too.
        }

        // ── Step body dispatch ────────────────────────────────────────────────────

        private void RefreshStepBody()
        {
            foreach (Node c in _bodyHost.GetChildren()) { _bodyHost.RemoveChild(c); c.QueueFree(); }

            switch ((FactionDefinerStep)_stepTabs.Active)
            {
                case FactionDefinerStep.NameColor: BuildNameColorStep(); break;
                case FactionDefinerStep.Roster: BuildRosterStep(); break;
                case FactionDefinerStep.BuildingsTech: BuildBuildingsTechStep(); break;
                case FactionDefinerStep.StartingConditions: BuildStartingConditionsStep(); break;
                case FactionDefinerStep.AiPreset: BuildAiPresetStep(); break;
            }
            UpdateFooterButtons();
        }

        // ── Step 0: Name & Color ──────────────────────────────────────────────────

        private void BuildNameColorStep()
        {
            _bodyHost.AddChild(ChimeraComponents.FieldLabel("Faction ID (used for the output filename)"));
            var idInput = ChimeraComponents.Input("e.g. crimson_order", _draft.Id);
            idInput.TextChanged += t => { _draft.Id = t; ClearStatus(); };
            AttachTip(idInput, "Faction ID", "A unique [a-z0-9_] id — becomes the output filename ({id}_faction.json). Must not collide with an existing faction.", ChimeraTooltip.TooltipRole.Field);
            _bodyHost.AddChild(idInput);

            _bodyHost.AddChild(ChimeraComponents.FieldLabel("Display Name"));
            var nameInput = ChimeraComponents.Input("e.g. The Crimson Order", _draft.DisplayName);
            nameInput.TextChanged += t => _draft.DisplayName = t;
            AttachTip(nameInput, "Display Name", "The human-readable name shown in menus and skirmish setup.", ChimeraTooltip.TooltipRole.Field);
            _bodyHost.AddChild(nameInput);

            _bodyHost.AddChild(ChimeraComponents.FieldLabel("Faction Color"));
            var grid = new GridContainer { Columns = 4 };
            grid.AddThemeConstantOverride("h_separation", ChimeraComponents.Const(ThemeTokens.S2));
            grid.AddThemeConstantOverride("v_separation", ChimeraComponents.Const(ThemeTokens.S2));
            foreach ((string hex, string glyph, string label) in ColorSwatchDefs)
            {
                float[] rgba = HexToRgba(hex);
                bool isActive = ColorsEqual(_draft.Color, rgba);
                var swatchBtn = ChimeraComponents.Button($"{glyph} {label}",
                    isActive ? ChimeraComponents.ButtonVariant.Primary : ChimeraComponents.ButtonVariant.Secondary,
                    ChimeraComponents.ButtonSize.Sm);
                swatchBtn.Pressed += () => { _draft.Color = rgba; RefreshStepBody(); };
                AttachTip(swatchBtn, label, $"Okabe-Ito colorblind-safe team color, paired with the {glyph} glyph so identity never relies on color alone.");
                grid.AddChild(swatchBtn);
            }
            _bodyHost.AddChild(grid);
        }

        private static float[] HexToRgba(string hex)
        {
            var c = new Color(hex);
            return new[] { c.R, c.G, c.B, 1f };
        }

        private static bool ColorsEqual(float[]? a, float[] b)
        {
            if (a == null || a.Length != 4) return false;
            for (int i = 0; i < 4; i++)
                if (Mathf.Abs(a[i] - b[i]) > 0.001f) return false;
            return true;
        }

        // ── Step 1: Roster ────────────────────────────────────────────────────────

        private void BuildRosterStep()
        {
            _bodyHost.AddChild(ChimeraComponents.FieldLabel(
                "Roster — pick units from existing factions (needs >=1 Worker + >=1 combat unit)"));

            if (_presets.Units.Count == 0)
            {
                _bodyHost.AddChild(Body("No units found in the scanned faction files.", ThemeTokens.TextLo));
                return;
            }

            foreach (FactionPresetOption<UnitDefinition> opt in _presets.Units)
                _bodyHost.AddChild(BuildPickRow(
                    $"[{opt.SourceFactionId}] {LabelFor(opt.Def)} — {opt.Def.Category}",
                    isChecked: _draft.Units.Contains(opt.Def),
                    onToggled: on =>
                    {
                        if (on) { if (!_draft.Units.Contains(opt.Def)) _draft.Units.Add(opt.Def); }
                        else _draft.Units.Remove(opt.Def);
                    },
                    tip: $"Include '{opt.Def.Id}' (from {opt.SourceFactionId}) in this faction's roster."));
        }

        // ── Step 2: Buildings & Tech (combined per spec) ─────────────────────────

        private void BuildBuildingsTechStep()
        {
            _bodyHost.AddChild(ChimeraComponents.FieldLabel("Buildings — pick from existing factions"));
            if (_presets.Buildings.Count == 0)
            {
                _bodyHost.AddChild(Body("No buildings found in the scanned faction files.", ThemeTokens.TextLo));
            }
            else
            {
                foreach (FactionPresetOption<BuildingDefinition> opt in _presets.Buildings)
                    _bodyHost.AddChild(BuildPickRow(
                        $"[{opt.SourceFactionId}] {LabelFor(opt.Def)}",
                        isChecked: _draft.Buildings.Contains(opt.Def),
                        onToggled: on =>
                        {
                            if (on) { if (!_draft.Buildings.Contains(opt.Def)) _draft.Buildings.Add(opt.Def); }
                            else _draft.Buildings.Remove(opt.Def);
                        },
                        tip: $"Include '{opt.Def.Id}' (from {opt.SourceFactionId}) in this faction's building roster."));
            }

            _bodyHost.AddChild(ChimeraComponents.FieldLabel("Research — pick from existing factions"));
            if (_presets.Research.Count == 0)
            {
                _bodyHost.AddChild(Body("No research entries found in the scanned faction files.", ThemeTokens.TextLo));
            }
            else
            {
                foreach (FactionPresetOption<ResearchDefinition> opt in _presets.Research)
                    _bodyHost.AddChild(BuildPickRow(
                        $"[{opt.SourceFactionId}] {(string.IsNullOrEmpty(opt.Def.DisplayName) ? opt.Def.Id : opt.Def.DisplayName)}",
                        isChecked: _draft.Research.Contains(opt.Def),
                        onToggled: on =>
                        {
                            if (on) { if (!_draft.Research.Contains(opt.Def)) _draft.Research.Add(opt.Def); }
                            else _draft.Research.Remove(opt.Def);
                        },
                        tip: $"Include '{opt.Def.Id}' (from {opt.SourceFactionId}) in this faction's research list."));
            }
        }

        private static string LabelFor(UnitDefinition def) =>
            string.IsNullOrEmpty(def.DisplayName) ? def.Id : def.DisplayName;

        private HBoxContainer BuildPickRow(string label, bool isChecked, System.Action<bool> onToggled, string? tip = null)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));

            var cb = new CheckBox { ButtonPressed = isChecked };
            cb.Toggled += on => onToggled(on);
            if (tip != null) AttachTip(cb, label, tip, ChimeraTooltip.TooltipRole.Field);
            row.AddChild(cb);

            var lbl = Body(label, ThemeTokens.TextHi);
            row.AddChild(lbl);
            return row;
        }

        // ── Step 3: Starting Conditions ───────────────────────────────────────────

        private void BuildStartingConditionsStep()
        {
            _bodyHost.AddChild(ChimeraComponents.FieldLabel("Starting Ore"));
            var oreInput = ChimeraComponents.NumInput(_draft.StartingOre, 0, 100000, 10);
            oreInput.ValueChanged += v => _draft.StartingOre = (float)v;
            AttachTip(oreInput, "Starting Ore", "Ore this faction starts a match with.", ChimeraTooltip.TooltipRole.Field);
            _bodyHost.AddChild(oreInput);

            _bodyHost.AddChild(ChimeraComponents.FieldLabel("Starting Crystal"));
            var crystalInput = ChimeraComponents.NumInput(_draft.StartingCrystal, 0, 100000, 10);
            crystalInput.ValueChanged += v => _draft.StartingCrystal = (float)v;
            AttachTip(crystalInput, "Starting Crystal", "Crystal (the scarce resource) this faction starts a match with.", ChimeraTooltip.TooltipRole.Field);
            _bodyHost.AddChild(crystalInput);

            _bodyHost.AddChild(Body(
                "Descriptor-only this story — not yet wired into match-start economy (a future story extends ScenarioApplier).",
                ThemeTokens.TextLo));
        }

        // ── Step 4: AI Preset + Hero/Persistence (Story 5.6, FR-18/AR-12) ─────────

        private void BuildAiPresetStep()
        {
            // Defensive clear (early UI feedback only — TryFinish's OWN call to this same method, run just before
            // ValidateComplete, is the actual enforcement point; see FactionDefinerWizardCore.TryFinish's comment
            // and the Spec Change Log). Catches a hero picked here, then unpicked via Back → Roster → uncheck,
            // BEFORE this step re-renders — so the picker never shows a stale selection.
            FactionDefinerWizardCore.ClearStaleHeroReference(_draft);

            // ── AI Preset: a real picker over the closed FactionValidator.KnownAiPresets set. No auto-pin — the
            // step opens with nothing selected (ResetWizard sets _draft.AiPreset = ""), so Finish is genuinely
            // blockable via the located "ai_preset" validator error until a creator clicks a button.
            _bodyHost.AddChild(ChimeraComponents.FieldLabel("AI Preset"));
            if (string.IsNullOrEmpty(_draft.AiPreset))
                _bodyHost.AddChild(Body("No preset selected — Finish is blocked until you pick one.", ThemeTokens.TextLo));

            var presetGrid = new GridContainer { Columns = 3 };
            presetGrid.AddThemeConstantOverride("h_separation", ChimeraComponents.Const(ThemeTokens.S2));
            presetGrid.AddThemeConstantOverride("v_separation", ChimeraComponents.Const(ThemeTokens.S2));
            foreach (string preset in FactionValidator.KnownAiPresets)
            {
                bool isActive = string.Equals(_draft.AiPreset, preset, System.StringComparison.OrdinalIgnoreCase);
                var presetBtn = ChimeraComponents.Button(preset,
                    isActive ? ChimeraComponents.ButtonVariant.Primary : ChimeraComponents.ButtonVariant.Secondary,
                    ChimeraComponents.ButtonSize.Sm);
                presetBtn.Pressed += () => { _draft.AiPreset = preset; RefreshStepBody(); };
                AttachTip(presetBtn, preset, $"Assign the '{preset}' AI preset so this faction is playable against/with the AI opponent.");
                presetGrid.AddChild(presetBtn);
            }
            _bodyHost.AddChild(presetGrid);

            // ── Hero / Persistence (AR-12) ──
            _bodyHost.AddChild(ChimeraComponents.FieldLabel("Hero Unit"));
            var heroCandidates = _draft.Units.Where(u => u != null && u.IsHero).ToList();
            if (heroCandidates.Count == 0)
                _bodyHost.AddChild(Body("No hero-flagged unit in the picked roster.", ThemeTokens.TextLo));

            var heroGrid = new GridContainer { Columns = 3 };
            heroGrid.AddThemeConstantOverride("h_separation", ChimeraComponents.Const(ThemeTokens.S2));
            heroGrid.AddThemeConstantOverride("v_separation", ChimeraComponents.Const(ThemeTokens.S2));

            bool noneActive = string.IsNullOrEmpty(_draft.HeroUnitId);
            var noneBtn = ChimeraComponents.Button("(none)",
                noneActive ? ChimeraComponents.ButtonVariant.Primary : ChimeraComponents.ButtonVariant.Secondary,
                ChimeraComponents.ButtonSize.Sm);
            noneBtn.Pressed += () => { _draft.HeroUnitId = null; RefreshStepBody(); };
            AttachTip(noneBtn, "(none)", "This faction has no hero unit reference.");
            heroGrid.AddChild(noneBtn);

            foreach (UnitDefinition hero in heroCandidates)
            {
                bool isActive = _draft.HeroUnitId == hero.Id;
                var heroBtn = ChimeraComponents.Button(LabelFor(hero),
                    isActive ? ChimeraComponents.ButtonVariant.Primary : ChimeraComponents.ButtonVariant.Secondary,
                    ChimeraComponents.ButtonSize.Sm);
                heroBtn.Pressed += () => { _draft.HeroUnitId = hero.Id; RefreshStepBody(); };
                AttachTip(heroBtn, LabelFor(hero), $"Reference '{hero.Id}' as this faction's persistent hero unit.");
                heroGrid.AddChild(heroBtn);
            }
            _bodyHost.AddChild(heroGrid);

            var persistCb = new CheckBox { ButtonPressed = _draft.PersistenceEnabled, Text = "Enable cross-match persistence" };
            persistCb.Toggled += on => _draft.PersistenceEnabled = on;
            AttachTip(persistCb, "Cross-match persistence", "Let this faction's hero attributes carry across matches via the persistence manifest.", ChimeraTooltip.TooltipRole.Field);
            _bodyHost.AddChild(persistCb);
        }

        // ── Finish/save ───────────────────────────────────────────────────────────

        private void OnFinishPressed()
        {
            string factionsDirAbs = ProjectSettings.GlobalizePath(FACTIONS_DIR_RES);
            FactionDefinerFinishResult result = _advancedMode
                ? FactionDefinerWizardCore.TryFinishFromRawJson(_jsonPane.Text, factionsDirAbs)
                : FactionDefinerWizardCore.TryFinish(_draft, factionsDirAbs);

            if (!result.Ok)
            {
                if (result.Errors.Count == 0)
                {
                    ShowError("Save failed.");
                    return;
                }
                (string fieldPath, string message) = result.Errors[0];
                int extra = result.Errors.Count - 1;
                ShowError(extra > 0 ? $"{message} (+{extra} more issue{(extra == 1 ? "" : "s")})" : message);
                // Advanced mode has no step to jump to (its raw pane never renders a per-step body).
                if (result.Step.HasValue && !_advancedMode) _stepTabs.SetActive((int)result.Step.Value);
                return;
            }

            // Review pass 2: a successful Advanced-mode Finish USED the pane's current text to write the file — it
            // was not discarded. Clear the dirty flag so a later Advanced→Simple switch never shows the "edits were
            // discarded" note for content that was, in fact, saved.
            if (_advancedMode) _paneDirty = false;

            ShowOk($"Saved — {result.WrittenPath}");
            GD.Print($"[FactionDefiner] Wrote {result.WrittenPath}.");
        }
    }
}
