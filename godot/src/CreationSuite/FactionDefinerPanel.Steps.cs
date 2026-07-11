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

            ClearStatus();
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
            _bodyHost.AddChild(idInput);

            _bodyHost.AddChild(ChimeraComponents.FieldLabel("Display Name"));
            var nameInput = ChimeraComponents.Input("e.g. The Crimson Order", _draft.DisplayName);
            nameInput.TextChanged += t => _draft.DisplayName = t;
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
                    }));
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
                        }));
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
                        }));
            }
        }

        private static string LabelFor(UnitDefinition def) =>
            string.IsNullOrEmpty(def.DisplayName) ? def.Id : def.DisplayName;

        private HBoxContainer BuildPickRow(string label, bool isChecked, System.Action<bool> onToggled)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));

            var cb = new CheckBox { ButtonPressed = isChecked };
            cb.Toggled += on => onToggled(on);
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
            _bodyHost.AddChild(oreInput);

            _bodyHost.AddChild(ChimeraComponents.FieldLabel("Starting Crystal"));
            var crystalInput = ChimeraComponents.NumInput(_draft.StartingCrystal, 0, 100000, 10);
            crystalInput.ValueChanged += v => _draft.StartingCrystal = (float)v;
            _bodyHost.AddChild(crystalInput);

            _bodyHost.AddChild(Body(
                "Descriptor-only this story — not yet wired into match-start economy (a future story extends ScenarioApplier).",
                ThemeTokens.TextLo));
        }

        // ── Step 4: AI Preset (non-interactive stub this story) ──────────────────

        private void BuildAiPresetStep()
        {
            _draft.AiPreset = "balanced";   // Finish always writes this — pin it here so the panel reflects the real output

            _bodyHost.AddChild(ChimeraComponents.FieldLabel("AI Preset"));
            _bodyHost.AddChild(ChimeraComponents.Tag("balanced — selected", ChimeraComponents.TagVariant.Accent));
            _bodyHost.AddChild(Body(
                "Preset choice lands in Story 5.6 — Finish always writes ai_preset: \"balanced\" for now.",
                ThemeTokens.TextLo));
        }

        // ── Finish/save ───────────────────────────────────────────────────────────

        private void OnFinishPressed()
        {
            string factionsDirAbs = ProjectSettings.GlobalizePath(FACTIONS_DIR_RES);
            FactionDefinerFinishResult result = FactionDefinerWizardCore.TryFinish(_draft, factionsDirAbs);

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
                if (result.Step.HasValue) _stepTabs.SetActive((int)result.Step.Value);
                return;
            }

            ShowOk($"Saved — {result.WrittenPath}");
            GD.Print($"[FactionDefiner] Wrote {result.WrittenPath}.");
        }
    }
}
