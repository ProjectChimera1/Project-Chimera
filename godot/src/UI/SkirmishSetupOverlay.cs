#nullable enable
using System;
using System.Collections.Generic;
using Godot;
using ProjectChimera.AI;                    // AiDifficulty
using ProjectChimera.Core;                  // MainScene
using ProjectChimera.Core.Definitions;       // ScenarioData, ScenarioSerializer
using ProjectChimera.Core.Skirmish;          // SkirmishSetup, SkirmishCatalog, SkirmishSetupValidator, SkirmishSetupToScenario
using ProjectChimera.UI.Theme;               // ThemeTokens, ThemeBuilder
using GodotTheme = Godot.Theme;              // the ProjectChimera.UI.Theme namespace shadows the bare Theme type

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 11.1 — the real skirmish setup screen. A topmost <see cref="CanvasLayer"/> reached from the menu's "Play":
    /// the player picks a shipped map (left) and configures each of that map's player slots (right — Open/Closed/Human/
    /// AI+difficulty, faction, team). The screen live-runs <see cref="SkirmishSetupValidator"/>, listing every located
    /// error and disabling Launch while any stands (Epic-11.7 honesty: only a config the runtime can pilot launches).
    /// On Launch it builds an in-memory <see cref="ScenarioData"/> (<see cref="SkirmishSetupToScenario"/>) and hands it
    /// to <see cref="MainScene.LaunchSkirmish"/> — the same <c>PendingGeneratedScenario</c> + <c>ReloadCurrentScene</c>
    /// path the AI map generator uses, so the skirmish flows through the identical fail-closed apply pipeline.
    ///
    /// <para>Faction choices are committed as <c>res://</c> <c>FactionJson</c> paths (never in-memory defs), so the
    /// existing <c>ResolveSlotFactionDefs</c> resolves abilities + drops unknown-tag units at load (DW-121 closed by
    /// construction). Map thumbnails, a color picker, and mod.io maps are out of scope (textual + shipped only).</para>
    /// </summary>
    public partial class SkirmishSetupOverlay : CanvasLayer
    {
        // res:// content directories scanned for the selectable catalog.
        private const string ScenariosResDir = "res://resources/data/scenarios";
        private const string FactionsResDir  = "res://resources/data/factions";

        private readonly SkirmishSetupValidator _validator = new();

        // ── Deps ──
        private MainScene _scene = null!;
        private Action    _onBack = () => { };
        private GodotTheme _theme = null!;

        // ── Catalog ──
        private IReadOnlyList<MapEntry>     _maps     = System.Array.Empty<MapEntry>();
        private IReadOnlyList<FactionEntry> _factions = System.Array.Empty<FactionEntry>();
        private MapEntry? _selectedMap;

        // ── Nodes ──
        private CanvasLayer   _canvas   = null!;
        private VBoxContainer _mapListHost = null!;
        private Label         _mapPropsLabel = null!;
        private VBoxContainer _slotHost = null!;
        private Label         _errorLabel = null!;
        private Godot.Button  _launchBtn = null!;

        private readonly List<SlotRow> _slotRows = new();

        // ── Init / lifecycle ──────────────────────────────────────────────────────

        /// <summary>Wire the overlay to the owning scene and a Back handler (shows the main menu). Scans the shipped
        /// map + faction catalog once (shipped content is static). Hidden until <see cref="Open"/>.</summary>
        public void Initialize(MainScene scene, Action onBack)
        {
            _scene  = scene;
            _onBack = onBack ?? (() => { });

            _maps     = SkirmishCatalog.ScanMaps(ProjectSettings.GlobalizePath(ScenariosResDir), ScenariosResDir);
            _factions = SkirmishCatalog.ScanFactions(ProjectSettings.GlobalizePath(FactionsResDir), FactionsResDir);

            BuildUi();
        }

        /// <summary>Show the setup screen. When <paramref name="prefill"/> is supplied (the fail-safe re-open after a
        /// boot exception) the map + slots are restored from it; otherwise the first map with a sensible default 1v1
        /// config is selected. An <paramref name="error"/> is surfaced in the error strip.</summary>
        public void Open(SkirmishSetup? prefill = null, string? error = null)
        {
            RebuildMapList();

            if (prefill != null && TryFindMap(prefill.MapId, out MapEntry? m))
                SelectMap(m!, prefill);
            else if (_maps.Count > 0)
                SelectMap(_maps[0], null);
            else
                SelectMap(null, null); // empty catalog → "No maps found"

            if (!string.IsNullOrEmpty(error))
            {
                _errorLabel.Visible = true;
                _errorLabel.Text = $"Launch failed: {error}";
            }

            _canvas.Visible = true;
        }

        private void CloseToMenu()
        {
            _canvas.Visible = false;
            _onBack();
        }

        // ── UI construction ────────────────────────────────────────────────────────

        private void BuildUi()
        {
            _theme = ResourceLoader.Load<GodotTheme>(ThemeBuilder.ThemePath, cacheMode: ResourceLoader.CacheMode.Ignore)
                     ?? ThemeBuilder.Build();

            _canvas = new CanvasLayer { Layer = 21 }; // just above the main menu (20)
            AddChild(_canvas);

            var root = new Control { Theme = _theme };
            root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            root.MouseFilter = Control.MouseFilterEnum.Stop;
            _canvas.AddChild(root);

            var backdrop = new ColorRect { Color = _theme.GetColor(ThemeTokens.SurfaceVoid, ThemeTokens.Type) };
            backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            backdrop.MouseFilter = Control.MouseFilterEnum.Ignore;
            root.AddChild(backdrop);

            var margin = new MarginContainer();
            margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            foreach (string side in new[] { "margin_left", "margin_right", "margin_top", "margin_bottom" })
                margin.AddThemeConstantOverride(side, 32);
            root.AddChild(margin);

            var outer = new VBoxContainer();
            outer.AddThemeConstantOverride("separation", 12);
            margin.AddChild(outer);

            outer.AddChild(MakeLabel("Skirmish Setup", ThemeTokens.FontDisplay, ThemeTokens.T2xl, ThemeTokens.TextHi));

            // ── Two-column body: map list | slot config ──
            var body = new HBoxContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
            body.AddThemeConstantOverride("separation", 24);
            outer.AddChild(body);

            // Left: map list.
            var leftCol = new VBoxContainer { CustomMinimumSize = new Vector2(280, 0) };
            leftCol.AddThemeConstantOverride("separation", 6);
            body.AddChild(leftCol);
            leftCol.AddChild(MakeLabel("Maps", ThemeTokens.FontUi, ThemeTokens.Tlg, ThemeTokens.TextMid));

            var mapScroll = new ScrollContainer
            {
                SizeFlagsVertical    = Control.SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            };
            leftCol.AddChild(mapScroll);
            _mapListHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _mapListHost.AddThemeConstantOverride("separation", 4);
            mapScroll.AddChild(_mapListHost);

            // Right: selected-map properties + slot grid.
            var rightCol = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            rightCol.AddThemeConstantOverride("separation", 8);
            body.AddChild(rightCol);

            _mapPropsLabel = MakeLabel("", ThemeTokens.FontUi, ThemeTokens.Tsm, ThemeTokens.TextMid);
            _mapPropsLabel.AutowrapMode = TextServer.AutowrapMode.Word;
            rightCol.AddChild(_mapPropsLabel);

            rightCol.AddChild(MakeLabel("Player slots", ThemeTokens.FontUi, ThemeTokens.Tlg, ThemeTokens.TextMid));

            var slotScroll = new ScrollContainer
            {
                SizeFlagsVertical    = Control.SizeFlags.ExpandFill,
                SizeFlagsHorizontal  = Control.SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            };
            rightCol.AddChild(slotScroll);
            _slotHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _slotHost.AddThemeConstantOverride("separation", 6);
            slotScroll.AddChild(_slotHost);

            // ── Error strip + footer ──
            _errorLabel = MakeLabel("", ThemeTokens.FontUi, ThemeTokens.Tsm, ThemeTokens.TextHi);
            _errorLabel.AutowrapMode = TextServer.AutowrapMode.Word;
            _errorLabel.AddThemeColorOverride("font_color", new Color(1.0f, 0.5f, 0.4f));
            _errorLabel.Visible = false;
            outer.AddChild(_errorLabel);

            var footer = new HBoxContainer();
            footer.AddThemeConstantOverride("separation", 12);
            outer.AddChild(footer);

            var backBtn = new Godot.Button { Text = "Back" };
            backBtn.Pressed += CloseToMenu;
            footer.AddChild(backBtn);

            footer.AddChild(new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill });

            _launchBtn = new Godot.Button { Text = "Launch" };
            _launchBtn.Pressed += OnLaunchPressed;
            footer.AddChild(_launchBtn);

            _canvas.Visible = false;
        }

        // ── Map list ────────────────────────────────────────────────────────────────

        private void RebuildMapList()
        {
            foreach (Node c in _mapListHost.GetChildren()) { _mapListHost.RemoveChild(c); c.QueueFree(); }

            if (_maps.Count == 0)
            {
                _mapListHost.AddChild(MakeLabel("No maps found.", ThemeTokens.FontUi, ThemeTokens.Tsm, ThemeTokens.TextLo));
                return;
            }

            foreach (MapEntry map in _maps)
            {
                MapEntry captured = map;
                var btn = new Godot.Button
                {
                    Text = string.IsNullOrEmpty(map.DisplayName) ? map.Id : map.DisplayName,
                    ToggleMode = true,
                    Alignment = HorizontalAlignment.Left,
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                };
                btn.Pressed += () => SelectMap(captured, null);
                _mapListHost.AddChild(btn);
            }
        }

        private bool TryFindMap(string id, out MapEntry? entry)
        {
            foreach (MapEntry m in _maps)
                if (m.Id == id) { entry = m; return true; }
            entry = null;
            return false;
        }

        private void SelectMap(MapEntry? map, SkirmishSetup? prefill)
        {
            _selectedMap = map;

            // Reflect the toggle state on the map buttons.
            int idx = 0;
            foreach (Node c in _mapListHost.GetChildren())
            {
                if (c is Godot.Button b) b.ButtonPressed = map != null && idx < _maps.Count && _maps[idx] == map;
                idx++;
            }

            if (map == null)
            {
                _mapPropsLabel.Text = _maps.Count == 0 ? "No maps found in the scenarios folder." : "Select a map.";
                BuildSlotRows(null, null);
                Revalidate();
                return;
            }

            _mapPropsLabel.Text =
                $"{(string.IsNullOrEmpty(map.DisplayName) ? map.Id : map.DisplayName)}\n" +
                $"Start positions: {map.StartPositionCount}   Suggested players: {(map.SuggestedPlayers > 0 ? map.SuggestedPlayers.ToString() : "unspecified")}\n" +
                $"Map bounds: {map.MapBounds:0}   Author: {(string.IsNullOrEmpty(map.Author) ? "—" : map.Author)}";

            BuildSlotRows(map, prefill);
            Revalidate();
        }

        // ── Slot grid ─────────────────────────────────────────────────────────────

        private void BuildSlotRows(MapEntry? map, SkirmishSetup? prefill)
        {
            foreach (Node c in _slotHost.GetChildren()) { _slotHost.RemoveChild(c); c.QueueFree(); }
            _slotRows.Clear();
            if (map == null) return;

            int count = Math.Max(0, map.StartPositionCount);
            for (int i = 0; i < count; i++)
            {
                var row = new SlotRow(i, _factions, map.StartPositionCount, _theme, SlotColorFor(i), Revalidate);
                _slotHost.AddChild(row.Root);
                _slotRows.Add(row);
            }

            // Apply prefill, else a sensible default 1v1: slot0 Human, slot1 Ai, others Open.
            if (prefill != null)
            {
                foreach (SetupSlot s in prefill.Slots)
                    if (s.Slot >= 0 && s.Slot < _slotRows.Count) _slotRows[s.Slot].Apply(s);
            }
            else
            {
                if (_slotRows.Count > 0) _slotRows[0].SetDefault(SlotKind.Human);
                if (_slotRows.Count > 1) _slotRows[1].SetDefault(SlotKind.Ai);
                for (int i = 2; i < _slotRows.Count; i++) _slotRows[i].SetDefault(SlotKind.Open);
            }
        }

        // PATCH 7: single source of truth — the in-match team palette. The setup swatch can never drift from it.
        private static Color SlotColorFor(int i) => ProjectChimera.Core.Bootstrap.FactionVisualsPhase.SlotColorAt(i);

        // ── Validation ──────────────────────────────────────────────────────────────

        private SkirmishSetup ReadSetup()
        {
            var setup = new SkirmishSetup { MapId = _selectedMap?.Id ?? "" };
            foreach (SlotRow r in _slotRows) setup.Slots.Add(r.Read());
            return setup;
        }

        private void Revalidate()
        {
            if (_selectedMap == null)
            {
                _launchBtn.Disabled = true;
                _errorLabel.Visible = _maps.Count == 0;
                if (_maps.Count == 0) _errorLabel.Text = "No maps found — add a scenario to launch a skirmish.";
                return;
            }

            // PATCH 6: clamp every team spinner's max to the live active-slot count so the UI never offers an
            // out-of-range team ordinal the validator would reject.
            int activeCount = 0;
            foreach (SlotRow r in _slotRows)
                if (r.CurrentKind == SlotKind.Human || r.CurrentKind == SlotKind.Ai) activeCount++;
            foreach (SlotRow r in _slotRows) r.SetTeamMax(activeCount);

            IReadOnlyList<string> errors = _validator.Validate(ReadSetup(), _selectedMap, _factions);
            if (errors.Count == 0)
            {
                _launchBtn.Disabled = false;
                _errorLabel.Visible = false;
            }
            else
            {
                _launchBtn.Disabled = true;
                _errorLabel.Visible = true;
                _errorLabel.Text = "• " + string.Join("\n• ", errors);
            }
        }

        // ── Launch ────────────────────────────────────────────────────────────────

        private void OnLaunchPressed()
        {
            if (_selectedMap == null) return;
            SkirmishSetup setup = ReadSetup();

            IReadOnlyList<string> errors = _validator.Validate(setup, _selectedMap, _factions);
            if (errors.Count > 0) { Revalidate(); return; } // defensive: Launch is disabled while errors stand

            string absMap = ProjectSettings.GlobalizePath(_selectedMap.ResPath);
            ScenarioData? baseMap = ScenarioSerializer.LoadFromFile(absMap);
            if (baseMap == null)
            {
                _errorLabel.Visible = true;
                _errorLabel.Text = $"Could not load the map file: {_selectedMap.ResPath}";
                return;
            }

            ScenarioData built = SkirmishSetupToScenario.Build(setup, baseMap, _factions);

            // The single launchable AI opponent's difficulty (validation guarantees exactly one Ai slot).
            AiDifficulty ai = AiDifficulty.Normal;
            foreach (SetupSlot s in setup.Slots)
                if (s.Kind == SlotKind.Ai) { ai = s.Ai; break; }

            _canvas.Visible = false;
            _scene.LaunchSkirmish(built, ai, setup);
        }

        // ── Small builders ──────────────────────────────────────────────────────────

        private Label MakeLabel(string text, StringName fontToken, StringName sizeToken, StringName colorToken)
        {
            var l = new Label { Text = text };
            l.AddThemeFontOverride("font", _theme.GetFont(fontToken, ThemeTokens.Type));
            l.AddThemeFontSizeOverride("font_size", _theme.GetFontSize(sizeToken, ThemeTokens.Type));
            l.AddThemeColorOverride("font_color", _theme.GetColor(colorToken, ThemeTokens.Type));
            return l;
        }

        // ── Per-slot row ─────────────────────────────────────────────────────────────

        /// <summary>One player-slot row: a color swatch + Kind / AI-difficulty / faction option buttons + a team spinner.
        /// Reads/writes the pure <see cref="SetupSlot"/>; re-validates the screen on any change.</summary>
        private sealed class SlotRow
        {
            public HBoxContainer Root { get; }
            private readonly int _slot;
            private readonly IReadOnlyList<FactionEntry> _factions;
            private readonly OptionButton _kind;
            private readonly OptionButton _ai;
            private readonly OptionButton _faction;
            private readonly SpinBox _team;

            public SlotRow(int slot, IReadOnlyList<FactionEntry> factions, int startPositions,
                           GodotTheme theme, Color color, Action onChanged)
            {
                _slot = slot;
                _factions = factions;

                Root = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                Root.AddThemeConstantOverride("separation", 8);

                var swatch = new ColorRect { Color = color, CustomMinimumSize = new Vector2(16, 16),
                                             SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
                Root.AddChild(swatch);

                var slotLbl = new Label { Text = $"Slot {slot + 1}", CustomMinimumSize = new Vector2(56, 0) };
                slotLbl.AddThemeColorOverride("font_color", theme.GetColor(ThemeTokens.TextMid, ThemeTokens.Type));
                Root.AddChild(slotLbl);

                _kind = new OptionButton();
                _kind.AddItem("Open",   (int)SlotKind.Open);
                _kind.AddItem("Closed", (int)SlotKind.Closed);
                _kind.AddItem("Human",  (int)SlotKind.Human);
                _kind.AddItem("AI",     (int)SlotKind.Ai);
                Root.AddChild(_kind);

                _ai = new OptionButton();
                _ai.AddItem("Easy",   (int)AiDifficulty.Easy);
                _ai.AddItem("Normal", (int)AiDifficulty.Normal);
                _ai.AddItem("Hard",   (int)AiDifficulty.Hard);
                Select(_ai, (int)AiDifficulty.Normal);
                Root.AddChild(_ai);

                _faction = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                if (factions.Count == 0)
                    _faction.AddItem("(no factions)", -1);
                else
                    for (int i = 0; i < factions.Count; i++)
                        _faction.AddItem(string.IsNullOrEmpty(factions[i].DisplayName) ? factions[i].Id : factions[i].DisplayName, i);
                Root.AddChild(_faction);

                var teamLbl = new Label { Text = "Team" };
                teamLbl.AddThemeColorOverride("font_color", theme.GetColor(ThemeTokens.TextLo, ThemeTokens.Type));
                Root.AddChild(teamLbl);

                _team = new SpinBox { MinValue = 0, MaxValue = Math.Max(1, startPositions), Step = 1,
                                      CustomMinimumSize = new Vector2(64, 0) };
                Root.AddChild(_team);

                // Any change re-validates + re-syncs enable states.
                _kind.ItemSelected    += _ => { SyncEnabled(); onChanged(); };
                _ai.ItemSelected      += _ => onChanged();
                _faction.ItemSelected += _ => onChanged();
                _team.ValueChanged    += _ => onChanged();

                SyncEnabled();
            }

            /// <summary>The currently-selected kind — lets the screen count active slots for the team-max clamp.</summary>
            public SlotKind CurrentKind => (SlotKind)_kind.GetItemId(_kind.Selected);

            /// <summary>PATCH 6: clamp the team spinner's max to the current active-slot count, so the UI never offers a
            /// team ordinal the validator rejects ("team must be between 0 and N"). Value is auto-clamped by the SpinBox.</summary>
            public void SetTeamMax(int activeCount)
            {
                double max = Math.Max(0, activeCount);
                if (_team.MaxValue != max) _team.MaxValue = max;
            }

            /// <summary>Enable the AI/faction/team controls only for an active (Human/Ai) slot.</summary>
            private void SyncEnabled()
            {
                SlotKind kind = (SlotKind)_kind.GetItemId(_kind.Selected);
                bool active = kind == SlotKind.Human || kind == SlotKind.Ai;
                _ai.Disabled = kind != SlotKind.Ai;
                _faction.Disabled = !active;
                _team.Editable = active;
            }

            public SetupSlot Read()
            {
                var kind = (SlotKind)_kind.GetItemId(_kind.Selected);
                string? factionId = null;
                if (_factions.Count > 0)
                {
                    int fi = _faction.GetItemId(_faction.Selected);
                    if (fi >= 0 && fi < _factions.Count) factionId = _factions[fi].Id;
                }
                return new SetupSlot
                {
                    Slot      = _slot,
                    Kind      = kind,
                    Ai        = (AiDifficulty)_ai.GetItemId(_ai.Selected),
                    FactionId = factionId,
                    Team      = (int)_team.Value,
                };
            }

            public void Apply(SetupSlot s)
            {
                Select(_kind, (int)s.Kind);
                Select(_ai, (int)s.Ai);
                if (s.FactionId != null)
                    for (int i = 0; i < _factions.Count; i++)
                        if (_factions[i].Id == s.FactionId) { Select(_faction, i); break; }
                _team.Value = s.Team;
                SyncEnabled();
            }

            /// <summary>Default a fresh row to a kind, faction slot 0, team 0.</summary>
            public void SetDefault(SlotKind kind)
            {
                Select(_kind, (int)kind);
                if (_factions.Count > 0) Select(_faction, 0);
                _team.Value = 0;
                SyncEnabled();
            }

            private static void Select(OptionButton ob, int id)
            {
                for (int i = 0; i < ob.ItemCount; i++)
                    if (ob.GetItemId(i) == id) { ob.Selected = i; return; }
            }
        }
    }
}
