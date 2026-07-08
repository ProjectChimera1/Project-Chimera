#nullable enable
using System;
using System.Collections.Generic;
using Godot;
using ProjectChimera.Core;                 // Faction
using ProjectChimera.Core.Definitions;      // ScenarioData, PlayerProfile, LocalProfileSource, HeroProfileLoader, FactionDefinition, UnitDefinition
using ProjectChimera.UI.Components;          // ChimeraComponents, ChimeraListRow, ListRowGroup, ChimeraMark, ChimeraDialog, ChimeraToastHost, ChimeraTooltip
using ProjectChimera.UI.Theme;               // ThemeTokens, ThemeBuilder, AccentController
using GodotTheme = Godot.Theme;              // the ProjectChimera.UI.Theme namespace shadows the bare Theme type

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 3.9 — the OFFLINE, player-facing hero picker (UX-DR75, AR-12 M2 / FR-7d / FR-7e). Shown at the offline
    /// "Play Skirmish" start ONLY when the loaded scenario's <see cref="PersistenceManifest.Enabled"/> is true (else the
    /// skirmish launches straight to Play). It lists the player's saved heroes as slot cards built purely from the 3.1
    /// kit (<see cref="ChimeraMark"/> portrait + level/XP <c>Readout</c> + <c>Progress(Xp)</c> + faction <c>Tag</c> +
    /// signature), single-select via a <see cref="ListRowGroup"/>, and offers Deploy / Save / Overwrite / Delete plus a
    /// non-blocking "Play without a saved hero".
    ///
    /// <para><b>Determinism boundary.</b> The picker only chooses WHICH profile to deploy; the actual mint into
    /// <see cref="HeroStore"/> as deterministic initial state (and the byte-identical <see cref="StartStateHash"/>) is
    /// done by <see cref="HeroProfileLoader.LoadInto"/> through the phase's launch callback. Deploy stashes the chosen
    /// profile and launches; Overwrite/Delete are <see cref="ChimeraDialog"/>-confirmed (danger). Every new control gets
    /// <see cref="AttachFieldTip"/> (UX-DR53, hover + keyboard focus). A live GLB portrait is NOT required this story —
    /// the asset-free <see cref="ChimeraMark"/> sigil is the placeholder.</para>
    /// </summary>
    public partial class HeroPickerOverlay : Node
    {
        // ── Layout constants (component-intrinsic; spacing/color TOKENS come from the theme) ──
        private const float PANEL_W = 640f;
        private const float PANEL_H = 560f;
        private const int   PORTRAIT = 48;

        // ── Kit context (self-owned; _accent only created when this overlay is the first kit consumer) ──
        private GodotTheme        _theme  = null!;
        private AccentController?  _accent;

        // ── Deps (wired by HeroPickerPhase after AddChild) ──
        private ScenarioData?          _scenario;
        private LocalProfileSource?    _source;
        private FactionDefinition?[]   _slotFactionDefs = System.Array.Empty<FactionDefinition?>();
        private Action<PlayerProfile?> _launch = _ => { };

        /// <summary>Story 3.13 (D6): supplies the harvested end-of-match Level/Xp for a hero unit id — <c>Has</c> true
        /// when the last playtest grew that hero, so Save/Overwrite persist the REAL grown values instead of the authored
        /// placeholders. Wired by <c>HeroPickerPhase</c> to read the <c>SceneContext</c> harvest; null ⇒ placeholders.</summary>
        public System.Func<string, (bool Has, int Level, Core.Fixed Xp)>? HeroProgressProvider;

        // ── Nodes ──
        private CanvasLayer     _canvas   = null!;
        private ColorRect       _scrim    = null!;
        private PanelContainer  _panel    = null!;
        private VBoxContainer   _listHost = null!;   // the slot-card list
        private Label           _statusLabel = null!;
        private Godot.Button    _deployBtn    = null!;
        private Godot.Button    _saveBtn      = null!;
        private Godot.Button    _overwriteBtn = null!;
        private Godot.Button    _deleteBtn    = null!;
        private ChimeraToastHost _toastHost   = null!;

        private readonly ListRowGroup _group = new();
        private PlayerProfile? _selected;

        // ── Lifecycle ─────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public override void _Ready()
        {
            EnsureKitInitialized();   // MUST run before any ChimeraComponents.* call, or the factory throws
            BuildUi();
        }

        /// <summary>Bind the overlay to the live scenario + the offline disk rail + per-slot faction defs (for card
        /// metadata) + the phase's launch callback. Called by <c>HeroPickerPhase</c> after <c>AddChild</c>. Hidden by
        /// default; shown by <see cref="RequestSkirmishLaunch"/>.</summary>
        public void Initialize(ScenarioData? scenario, LocalProfileSource source,
                               FactionDefinition?[] slotFactionDefs, Action<PlayerProfile?> launch)
        {
            _scenario        = scenario;
            _source          = source;
            _slotFactionDefs = slotFactionDefs ?? System.Array.Empty<FactionDefinition?>();
            _launch          = launch ?? (_ => { });
        }

        /// <summary>
        /// The offline Play-Skirmish launch authority (D-4). When the loaded scenario's persistence manifest is Enabled,
        /// show the picker; otherwise launch straight to Play exactly as today (minting nothing). Single authority — the
        /// caller (<c>MainMenuPhase.OnPlaySkirmish</c>) never toggles Play itself.
        /// </summary>
        public void RequestSkirmishLaunch()
        {
            if (_scenario?.PersistenceManifest is { Enabled: true })
                Show();
            else
                _launch(null); // persistence not configured / disabled → straight to Play (nothing minted)
        }

        private void Show()
        {
            _selected = null;
            Refresh();
            _canvas.Visible = true;
        }

        private void Hide() => _canvas.Visible = false;

        // ── Kit bootstrap (mirrors PersistenceManifestPanel.EnsureKitInitialized) ──────────

        private void EnsureKitInitialized()
        {
            _theme = ResourceLoader.Load<GodotTheme>(ThemeBuilder.ThemePath, cacheMode: ResourceLoader.CacheMode.Ignore)
                     ?? ThemeBuilder.Build();

            if (!ChimeraComponents.IsInitialized)
            {
                _accent = new AccentController { Name = "AccentController" };
                AddChild(_accent);
                _accent.Initialize(_theme);
                ChimeraComponents.Initialize(_theme, _accent);
            }
        }

        // ── UI construction ────────────────────────────────────────────────────────

        private void BuildUi()
        {
            _canvas = new CanvasLayer { Layer = 20 }; // mirrors MainMenuOverlay (topmost) — NOT the Edit-mode idiom
            AddChild(_canvas);

            // Scrim eats input so clicks don't fall through to the game while the picker is open.
            _scrim = new ColorRect { Color = new Color(0.04f, 0.05f, 0.08f, 0.72f), MouseFilter = Control.MouseFilterEnum.Stop };
            _scrim.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _canvas.AddChild(_scrim);

            var center = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
            center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _canvas.AddChild(center);

            _panel = ChimeraComponents.Panel(ChimeraComponents.PanelVariant.Default);
            _panel.CustomMinimumSize = new Vector2(PANEL_W, PANEL_H);
            _panel.Theme = _theme;   // _panel is a Control (this Node has no Theme) — propagates to the subtree
            center.AddChild(_panel);

            var root = new VBoxContainer();
            root.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S3));
            _panel.AddChild(root);

            // ── Title ──
            var title = Heading("Choose Your Hero", ThemeTokens.Tlg);
            root.AddChild(title);

            var subtitle = Body("Deploy a saved hero as your starting state, save the current hero, or play without one.",
                                ThemeTokens.TextMid);
            subtitle.AutowrapMode = TextServer.AutowrapMode.Word;
            root.AddChild(subtitle);

            // ── Slot-card list ──
            var scroll = new ScrollContainer
            {
                SizeFlagsHorizontal  = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical    = Control.SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            };
            root.AddChild(scroll);

            _listHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _listHost.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            scroll.AddChild(_listHost);

            _statusLabel = Body("", ThemeTokens.TextLo);
            _statusLabel.AutowrapMode = TextServer.AutowrapMode.Word;
            _statusLabel.Visible = false;
            root.AddChild(_statusLabel);

            // ── Action footer ──
            var footer = new HBoxContainer();
            footer.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            root.AddChild(footer);

            _deployBtn = ChimeraComponents.Button("Deploy", ChimeraComponents.ButtonVariant.Primary);
            _deployBtn.Pressed += OnDeployPressed;
            AttachFieldTip(_deployBtn, "Deploy",
                "Start the match with the selected hero minted as deterministic starting state (its level and XP).");
            footer.AddChild(_deployBtn);

            _saveBtn = ChimeraComponents.Button("Save", ChimeraComponents.ButtonVariant.Secondary);
            _saveBtn.Pressed += OnSavePressed;
            AttachFieldTip(_saveBtn, "Save",
                "Save this scenario's hero into a new slot (captures the manifest's selected attributes — level, XP).");
            footer.AddChild(_saveBtn);

            _overwriteBtn = ChimeraComponents.Button("Overwrite", ChimeraComponents.ButtonVariant.Secondary);
            _overwriteBtn.Pressed += OnOverwritePressed;
            AttachFieldTip(_overwriteBtn, "Overwrite",
                "Replace the selected saved hero with the current hero's attributes. Requires confirmation.");
            footer.AddChild(_overwriteBtn);

            _deleteBtn = ChimeraComponents.Button("Delete", ChimeraComponents.ButtonVariant.Danger);
            _deleteBtn.Pressed += OnDeletePressed;
            AttachFieldTip(_deleteBtn, "Delete",
                "Permanently delete the selected saved hero from disk. Requires confirmation.");
            footer.AddChild(_deleteBtn);

            // Spacer pushes the non-blocking "play without a hero" to the right.
            footer.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

            var playWithout = ChimeraComponents.Button("Play without a saved hero", ChimeraComponents.ButtonVariant.Ghost);
            playWithout.Pressed += () => { Hide(); _launch(null); };
            AttachFieldTip(playWithout, "Play without a saved hero",
                "Start the match now with no saved hero — nothing is minted. Always available.");
            footer.AddChild(playWithout);

            // ── Toast host for "Saved" feedback ──
            _toastHost = ChimeraToastHost.Create();
            AddChild(_toastHost);

            _canvas.Visible = false;   // hidden until RequestSkirmishLaunch shows it
        }

        // ── Reflect saved profiles into slot cards ──────────────────────────────────

        private void Refresh()
        {
            foreach (Node c in _listHost.GetChildren()) { _listHost.RemoveChild(c); c.QueueFree(); }
            // The group's stale reference to a now-freed row is guarded by IsInstanceValid on the next Select — safe to
            // leave; the authoritative selection is _selected, reset here.
            _selected = null;

            IReadOnlyList<PlayerProfile> profiles = _source?.LoadAll() ?? System.Array.Empty<PlayerProfile>();

            if (profiles.Count == 0)
            {
                _statusLabel.Visible = true;
                _statusLabel.Text = "No saved heroes yet. Save this scenario's hero, or play without one.";
            }
            else
            {
                _statusLabel.Visible = false;
                foreach (PlayerProfile p in profiles)
                    _listHost.AddChild(BuildCard(p, IsCompatible(p)));
            }

            UpdateButtonStates();
        }

        /// <summary>A profile is compatible (selectable) when the scenario PLACES a hero unit whose id matches the
        /// profile's <see cref="PlayerProfile.HeroDefId"/> (D-3). Apply is the authoritative gate (it mints only for
        /// spawned <see cref="UnitDefinition.IsHero"/> entities); the card grey is the softer up-front signal.</summary>
        private bool IsCompatible(PlayerProfile profile)
        {
            if (_scenario == null) return false;
            foreach (ScenarioUnit u in _scenario.Units)
            {
                if (u.UnitId != profile.HeroDefId) continue;
                // Resolve the placed unit to its faction def and require IsHero — the SAME gate the authoritative mint
                // uses (LoadInto mints only IsHero placed units). This keeps the card's deployable-greying honest: a
                // non-hero unit sharing the id no longer shows deployable then mints nothing (D-3).
                int fIdx = u.Slot + 1; // slot 0 → Player1
                if (fIdx < 0 || fIdx >= _slotFactionDefs.Length) continue;
                UnitDefinition? def = _slotFactionDefs[fIdx]?.GetUnit(u.UnitId);
                if (def != null && def.IsHero) return true;
            }
            return false;
        }

        private ChimeraListRow BuildCard(PlayerProfile profile, bool compatible)
        {
            ChimeraListRow row = ChimeraListRow.Create("", _group);
            // Drop Create's placeholder label so the card composes cleanly.
            foreach (Node c in row.Content.GetChildren()) { row.Content.RemoveChild(c); c.QueueFree(); }
            row.Content.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S3));

            // Portrait (procedural sigil placeholder — no GLB required this story).
            var mark = ChimeraMark.Create(PORTRAIT);
            mark.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            row.Content.AddChild(mark);

            // Name + signature column.
            var nameCol = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            nameCol.AddThemeConstantOverride("separation", 2);
            var nameLbl = Heading(string.IsNullOrEmpty(profile.DisplayName) ? profile.HeroDefId : profile.DisplayName, ThemeTokens.Tmd);
            nameCol.AddChild(nameLbl);
            string sig = string.IsNullOrEmpty(profile.SignatureAbility) ? "No signature ability" : $"Signature: {profile.SignatureAbility}";
            nameCol.AddChild(Body(sig, ThemeTokens.TextLo));
            row.Content.AddChild(nameCol);

            // Level readout.
            Color accent = _theme.GetColor(ThemeTokens.Accent, ThemeTokens.Type);
            var lvl = ChimeraComponents.Readout(accent, profile.Level.ToString(), "LVL");
            lvl.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            row.Content.AddChild(lvl);

            // XP readout + decorative XP bar.
            var xpCol = new VBoxContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            xpCol.AddThemeConstantOverride("separation", 2);
            xpCol.AddChild(ChimeraComponents.Readout(_theme.GetColor(ThemeTokens.AccentDim, ThemeTokens.Type), profile.Xp.ToInt().ToString(), "XP"));
            var bar = ChimeraComponents.Progress(ChimeraComponents.ProgressVariant.Xp, XpBarValue(profile));
            bar.CustomMinimumSize = new Vector2(96, ChimeraComponents.Const(ThemeTokens.S2) + 6);
            xpCol.AddChild(bar);
            row.Content.AddChild(xpCol);

            // Faction tag (card metadata — display only).
            if (!string.IsNullOrEmpty(profile.FactionId))
            {
                var tag = ChimeraComponents.Tag(profile.FactionId, ChimeraComponents.TagVariant.Neutral);
                tag.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
                row.Content.AddChild(tag);
            }

            if (!compatible)
            {
                // Greyed / non-deployable: the scenario places no hero with this id.
                row.SetLocked(true);
                AttachFieldTip(row, profile.DisplayName,
                    "This scenario does not place this hero, so it cannot be deployed here.");
            }
            else
            {
                PlayerProfile captured = profile;
                row.Selected += () => OnRowSelected(captured);
                AttachFieldTip(row, profile.DisplayName,
                    $"Level {profile.Level} • {profile.Xp.ToInt()} XP. Select, then Deploy to start with this hero.");
            }
            return row;
        }

        // A decorative XP-bar fill (the real per-level leveling curve is Story 3.13). Presentation-only. MONOTONIC in
        // XP and saturating toward 100 — so it never resets and can never contradict the numeric XP readout beside it
        // (a higher XP never shows a shorter bar), unlike a raw `xp % 100` which emptied at every round hundred.
        private static double XpBarValue(PlayerProfile profile)
        {
            double xp = Math.Max(0, profile.Xp.ToInt());
            return 100.0 * xp / (xp + 100.0); // 0→0, strictly increasing, asymptotic to 100
        }

        private void OnRowSelected(PlayerProfile profile)
        {
            _selected = profile; // ChimeraListRow emits Selected only on selection (never deselection)
            UpdateButtonStates();
        }

        private void UpdateButtonStates()
        {
            bool hasSelection = _selected != null;
            _deployBtn.Disabled    = !hasSelection;
            _overwriteBtn.Disabled = !hasSelection;
            _deleteBtn.Disabled    = !hasSelection;
            // Save only needs a placeable hero in the scenario (no selection required).
            _saveBtn.Disabled = !TryResolvePrimaryHero(out _, out _, out _, out _);
        }

        // ── Actions ─────────────────────────────────────────────────────────────────

        private void OnDeployPressed()
        {
            if (_selected == null) return;
            PlayerProfile chosen = _selected;
            Hide();
            _launch(chosen); // stash-and-launch: the phase sets PendingHeroProfile + mints before the hash
        }

        /// <summary>Story 3.13 (D6): the harvested end-of-match Level/Xp for <paramref name="heroDefId"/> if the last
        /// playtest grew that hero, else the supplied fallback (authored base for a fresh Save, or the profile's own
        /// values for Overwrite). Keeps Save/Overwrite reading real grown values without coupling the picker to HeroStore.</summary>
        private (int Level, Fixed Xp) ResolveHeroProgress(string heroDefId, int fallbackLevel, Fixed fallbackXp)
        {
            if (HeroProgressProvider != null)
            {
                (bool has, int level, Fixed xp) = HeroProgressProvider(heroDefId);
                if (has) return (level, xp);
            }
            return (fallbackLevel, fallbackXp);
        }

        private void OnSavePressed()
        {
            if (_source == null || _scenario?.PersistenceManifest == null) return;
            if (!TryResolvePrimaryHero(out string unitId, out string display, out string? sig, out string factionId))
            {
                ShowStatus("This scenario places no hero to save.");
                return;
            }

            // Story 3.13 (D6): source the harvested end-of-match Level/Xp for this hero (if the last playtest grew it),
            // else the authored base (level 1, 0 XP). Routed through the manifest shape → BuildProfile.
            (int level, Fixed xp) = ResolveHeroProgress(unitId, fallbackLevel: 1, fallbackXp: Fixed.Zero);
            string profileId = _source.NextProfileId(unitId);
            PlayerProfile profile = HeroProfileLoader.BuildProfile(
                profileId, unitId, factionId, display, sig, level, xp,
                _scenario.PersistenceManifest.DeriveProfileShape());
            _source.Save(profile);

            _toastHost.Show("Saved", $"{display} saved as a new hero slot.", ChimeraToastHost.ToastVariant.Ok);
            Refresh();
        }

        private void OnOverwritePressed()
        {
            if (_source == null || _selected == null || _scenario?.PersistenceManifest == null) return;
            PlayerProfile target = _selected;

            var dialog = ChimeraDialog.Create("Overwrite saved hero?",
                $"Replace \"{(string.IsNullOrEmpty(target.DisplayName) ? target.HeroDefId : target.DisplayName)}\" " +
                "with the current hero's attributes? This cannot be undone.");
            dialog.AddConfirm("Overwrite", danger: true);
            dialog.AddCancel("Cancel");
            dialog.Confirmed += () =>
            {
                // Story 3.13 (D6): rebuild the SAME profile id, capturing the harvested end-of-match Level/Xp for this
                // hero (if the last playtest grew it), else the profile's own values — through the manifest shape.
                (int level, Fixed xp) = ResolveHeroProgress(target.HeroDefId, fallbackLevel: target.Level, fallbackXp: target.Xp);
                PlayerProfile rebuilt = HeroProfileLoader.BuildProfile(
                    target.ProfileId, target.HeroDefId, target.FactionId, target.DisplayName, target.SignatureAbility,
                    level, xp, _scenario.PersistenceManifest.DeriveProfileShape());
                _source.Save(rebuilt);
                _toastHost.Show("Saved", "Saved hero overwritten.", ChimeraToastHost.ToastVariant.Ok);
                Refresh();
            };
            dialog.Open(this);
        }

        private void OnDeletePressed()
        {
            if (_source == null || _selected == null) return;
            PlayerProfile target = _selected;

            var dialog = ChimeraDialog.Create("Delete saved hero?",
                $"Permanently delete \"{(string.IsNullOrEmpty(target.DisplayName) ? target.HeroDefId : target.DisplayName)}\"? " +
                "This cannot be undone.");
            dialog.AddConfirm("Delete", danger: true);
            dialog.AddCancel("Cancel");
            dialog.Confirmed += () =>
            {
                _source.Delete(target.ProfileId);
                _toastHost.Show("Deleted", "Saved hero removed.", ChimeraToastHost.ToastVariant.Warn);
                Refresh();
            };
            dialog.Open(this);
        }

        /// <summary>Resolve the scenario's first PLACED hero unit (for the Save metadata): its unit id, display name,
        /// signature ability, and owning faction id. Returns false when the scenario places no <see cref="UnitDefinition.IsHero"/>
        /// unit.</summary>
        private bool TryResolvePrimaryHero(out string unitId, out string display, out string? signature, out string factionId)
        {
            unitId = ""; display = ""; signature = null; factionId = "";
            if (_scenario == null) return false;

            foreach (ScenarioUnit u in _scenario.Units)
            {
                int fIdx = u.Slot + 1; // slot 0 → Player1
                if (fIdx < 0 || fIdx >= _slotFactionDefs.Length) continue;
                FactionDefinition? fdef = _slotFactionDefs[fIdx];
                UnitDefinition? def = fdef?.GetUnit(u.UnitId);
                if (def == null || !def.IsHero) continue;

                unitId    = def.Id;
                display   = string.IsNullOrEmpty(def.DisplayName) ? def.Id : def.DisplayName;
                signature = def.Hero?.SignatureAbility;
                factionId = fdef?.Id ?? "";
                return true;
            }
            return false;
        }

        private void ShowStatus(string text)
        {
            _statusLabel.Text = text;
            _statusLabel.Visible = true;
        }

        // ── Small shared builders (mirror PersistenceManifestPanel) ────────────────────

        private Label Heading(string text, StringName sizeToken)
        {
            var l = new Label { Text = text };
            l.AddThemeFontOverride("font", _theme.GetFont(ThemeTokens.FontDisplay, ThemeTokens.Type));
            l.AddThemeFontSizeOverride("font_size", _theme.GetFontSize(sizeToken, ThemeTokens.Type));
            l.AddThemeColorOverride("font_color", _theme.GetColor(ThemeTokens.TextHi, ThemeTokens.Type));
            return l;
        }

        private Label Body(string text, StringName colorToken)
        {
            var l = new Label { Text = text };
            l.AddThemeColorOverride("font_color", _theme.GetColor(colorToken, ThemeTokens.Type));
            l.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            return l;
        }

        /// <summary>Attach a hover-AND-keyboard-focus tooltip (UX-DR53 / NFR-2). The keyboard half needs
        /// <c>FocusMode.All</c>; descendants are made mouse-transparent so the composite is the unambiguous hover target.</summary>
        private void AttachFieldTip(Control target, string term, string body)
        {
            target.MouseFilter = Control.MouseFilterEnum.Stop;
            target.FocusMode = Control.FocusModeEnum.All;
            MakeChildrenMouseIgnore(target);
            ChimeraTooltip.Attach(target, term, body, ChimeraTooltip.TooltipRole.Field);
        }

        private static void MakeChildrenMouseIgnore(Node node)
        {
            foreach (Node child in node.GetChildren())
            {
                if (child is Control c) c.MouseFilter = Control.MouseFilterEnum.Ignore;
                MakeChildrenMouseIgnore(child);
            }
        }
    }
}
