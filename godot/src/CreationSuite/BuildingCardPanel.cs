#nullable enable
using System.Collections.Generic;
using Godot;
using ProjectChimera.Core.Definitions;   // BuildingDefinition, FactionDefinition, BuildingDefinitionValidator
using ProjectChimera.UI;                  // GameState, GameMode
using ProjectChimera.UI.Components;        // ChimeraComponents, ChimeraTabs, ChimeraTooltip, ChimeraValidationBadge
using ProjectChimera.UI.Theme;             // ThemeTokens, ThemeBuilder, AccentController
using GodotTheme = Godot.Theme;            // the ProjectChimera.UI.Theme namespace shadows the bare Theme type

namespace ProjectChimera.CreationSuite
{
    /// <summary>
    /// Story 4.5 — the Building Card Editor (UX-DR74), mirroring the Story 3.3/3.4 <see cref="UnitCardPanel"/> pattern:
    /// a right-dock inspector over the current faction's <see cref="FactionDefinition.Buildings"/> list, editable in
    /// place (Simple/Advanced disclosure, per-field located badges, undo/redo, a raw-JSON escape hatch, and a
    /// Save/New/Duplicate/Delete toolbar that writes back to the faction JSON on disk).
    ///
    /// <para><b>Deliberately smaller than the Unit Card Editor.</b> No <c>SubViewport</c>/<c>Camera3D</c>/turntable —
    /// the epic's UX section scopes this editor to stats/cost/construction/inspector, not a live 3D preview (see the
    /// Design Notes in the Story 4.5 spec). The Model row's mesh-path text field is still reused (Story 3.5's
    /// <c>AddModelRow</c> pattern), just without the render. No ability/behavior registry — buildings don't author
    /// <c>abilities[]</c>/<c>behaviors[]</c>, so this panel never wires <see cref="AbilityRegistry"/>/
    /// <see cref="BehaviorRegistry"/> at all.</para>
    ///
    /// <para><b>This file</b> is the shell + read-only header (mirrors <c>UnitCardPanel.cs</c>). The editable
    /// surface — fields, disclosure, raw-JSON hatch, validation, persistence, undo/redo, toolbar, input — lives in the
    /// partial <see cref="BuildingCardPanel"/> file <c>BuildingCardPanel.Edit.cs</c>.</para>
    ///
    /// <para><b>Determinism posture — PURE AUTHORING-TIME, zero fold.</b> Editing a content POCO and rewriting a JSON
    /// file touches no <c>EntityWorld</c>/store/sim array and moves no checksum or golden — mirrors
    /// <see cref="UnitCardPanel"/>'s posture exactly. The only <c>src/Core</c> touches are the Godot-free
    /// <see cref="BuildingDefinitionValidator"/> + <see cref="FactionWriter"/>.</para>
    /// </summary>
    public partial class BuildingCardPanel : Node
    {
        // ── Layout constants (component-intrinsic dims; the spacing/color TOKENS are read from the theme) ──
        private const float PANEL_W = 480f;
        private const float PANEL_H = 700f;
        private const float MARGIN  = 12f;

        // ── Kit context (self-owned; _accent only created when this panel is the first consumer) ──
        private GodotTheme        _theme  = null!;
        private AccentController?  _accent;

        // ── Deps (wired by BuildingCardPhase after AddChild) ──
        private FactionDefinition? _faction;               // the building source (Buildings only — never _faction.Units)
        private GameState?         _gameState;
        private int                _index;                 // browse cursor into _faction.Buildings
        private string             _factionJsonPath = "";  // res:// path of the faction file to write edits back to

        // ── Edit state (Story 4.5) ──
        private BuildingDefinition?    _current;            // the building currently bound/edited (== _faction.Buildings[_index])
        private string                 _originalId = "";    // the bound building's id at Bind time — the PatchFactionBuildingJson target (survives an id rename)
        private readonly EditorHistory              _history   = new();   // own instance; reused by Ctrl+Z/Y when visible
        private readonly Dictionary<string, ChimeraValidationBadge> _badges = new();  // JSON key → located badge (UX-DR55)
        private bool _building;                            // guard: suppress live handlers while (re)building controls
        private LineEdit? _meshPathInput;                  // the Model row's text field (Browse/Box write .Text here)

        // ── Shell ──
        private CanvasLayer    _canvas = null!;
        private PanelContainer _panel  = null!;
        private VBoxContainer  _headerHost = null!;        // read-only header (refilled per building)
        private VBoxContainer  _bodyHost   = null!;        // editable fields (refilled per building)
        private Label          _counterLabel = null!;
        private Godot.Button   _prevBtn = null!;
        private Godot.Button   _nextBtn = null!;

        // ── Disclosure + edit chrome (built once) ──
        private ChimeraTabs    _segment = null!;           // Simple / Advanced (Segment)
        private VBoxContainer?  _advancedHost;             // advanced-fields + raw-JSON subtree (rebuilt per building; visibility = segment)
        private TextEdit?       _jsonPane;                 // raw-JSON escape hatch over the single building
        private bool            _paneDirty;                // the raw-JSON pane has manual edits not yet folded back
        private bool            _suppressPaneDirty;        // a programmatic SetPaneText must not mark the pane dirty
        private Label           _statusLabel = null!;      // save/validation status line
        private Godot.Button    _saveBtn   = null!;
        private Godot.Button    _newBtn    = null!;
        private Godot.Button    _dupBtn    = null!;
        private Godot.Button    _deleteBtn = null!;

        // ── Lifecycle ─────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public override void _Ready()
        {
            EnsureKitInitialized();   // MUST run before any ChimeraComponents.* call, or the factory throws
            BuildUi();
        }

        /// <summary>
        /// Bind the panel to the current scenario's faction + game state + the faction file <c>res://</c> path to
        /// persist edits to. Called by <c>BuildingCardPhase</c> AFTER <c>AddChild</c>. Starts hidden; shown by the
        /// <c>C</c> toggle in Edit mode. No ability/behavior registry (buildings don't author abilities[]/behaviors[]).
        /// </summary>
        public void Initialize(FactionDefinition? faction, GameState gameState, string factionJsonPath = "")
        {
            _faction          = faction;
            _gameState        = gameState;
            _factionJsonPath  = factionJsonPath ?? "";
            _index           = 0;

            _gameState.ModeChanged += OnModeChanged;   // authoring is Edit-only — hide in Play
            _panel.Visible = false;
        }

        /// <summary>
        /// Standalone-harness entry point (<c>/godot-verify</c>): load a faction JSON by <c>res://</c> path, rebind
        /// the card to it (Buildings only), and set it as the write-back target. Presentation-only.
        /// </summary>
        public void LoadFactionFromPath(string resPath)
        {
            string abs = ProjectSettings.GlobalizePath(resPath);
            if (!System.IO.File.Exists(abs))
            {
                GD.PrintErr($"[BuildingCard] LoadFactionFromPath: '{abs}' not found.");
                return;
            }
            _faction         = FactionDefinition.LoadFromFile(abs);
            _factionJsonPath = resPath;   // persist edits back to the same file
            _index           = 0;
            _panel.Visible   = true;
            Refresh();
        }

        /// <summary>Toggle visibility (C key, Edit mode only). On open: (re)bind the current building.</summary>
        public void Toggle()
        {
            _panel.Visible = !_panel.Visible;
            if (_panel.Visible) Refresh();
        }

        /// <summary>Hide the panel.</summary>
        public void Close()
        {
            _panel.Visible = false;
        }

        private void OnModeChanged(int mode)
        {
            if (mode == (int)GameMode.Play) Close();   // hide in Play (authoring is Edit-only)
        }

        // ── Kit bootstrap ─────────────────────────────────────────────────────────

        private void EnsureKitInitialized()
        {
            // ALWAYS load the theme (the inner PanelContainer.Theme needs it regardless of factory state).
            _theme = ResourceLoader.Load<GodotTheme>(ThemeBuilder.ThemePath, cacheMode: ResourceLoader.CacheMode.Ignore)
                     ?? ThemeBuilder.Build();

            // Guard ONLY the one-time factory bootstrap so a future startup phase makes this a clean no-op.
            if (!ChimeraComponents.IsInitialized)
            {
                _accent = new AccentController { Name = "AccentController" };
                AddChild(_accent);
                _accent.Initialize(_theme);
                ChimeraComponents.Initialize(_theme, _accent);
            }
        }

        // ── UI construction ──────────────────────────────────────────────────────

        private void BuildUi()
        {
            _canvas = new CanvasLayer { Layer = 13 };
            AddChild(_canvas);

            _panel = ChimeraComponents.Panel(ChimeraComponents.PanelVariant.Default);
            _panel.SetAnchorsPreset(Control.LayoutPreset.CenterRight);
            _panel.CustomMinimumSize = new Vector2(PANEL_W, PANEL_H);
            _panel.Position = new Vector2(-(PANEL_W + MARGIN), -PANEL_H * 0.5f);
            _panel.Theme = _theme;   // _panel is a Control (BuildingCardPanel : Node has NO Theme) — propagates to the subtree
            _canvas.AddChild(_panel);

            var root = new VBoxContainer();
            root.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S3));
            _panel.AddChild(root);

            // Title + browse + close row.
            var titleRow = new HBoxContainer();
            titleRow.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            root.AddChild(titleRow);

            var titleLbl = Heading("Building Editor", ThemeTokens.Tlg);
            titleLbl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            titleLbl.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            titleRow.AddChild(titleLbl);

            _prevBtn = ChimeraComponents.IconButton("◀");
            _prevBtn.Pressed += () => Browse(-1);
            titleRow.AddChild(_prevBtn);

            _counterLabel = Body("—", ThemeTokens.TextMid);
            _counterLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _counterLabel.CustomMinimumSize = new Vector2(88, 0);
            _counterLabel.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            titleRow.AddChild(_counterLabel);

            _nextBtn = ChimeraComponents.IconButton("▶");
            _nextBtn.Pressed += () => Browse(1);
            titleRow.AddChild(_nextBtn);

            var closeBtn = ChimeraComponents.Button("Close [C]", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Sm);
            closeBtn.Pressed += Close;
            closeBtn.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            titleRow.AddChild(closeBtn);

            // Scrollable body (the card can be taller than the panel).
            var scroll = new ScrollContainer
            {
                SizeFlagsHorizontal  = Control.SizeFlags.ExpandFill,
                SizeFlagsVertical    = Control.SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            };
            root.AddChild(scroll);

            var contentCol = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            contentCol.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S3));
            scroll.AddChild(contentCol);

            // Read-only header → disclosure Segment → editable body (refilled per building). No preview host
            // (Design Notes: the epic's UX section scopes this editor to stats/cost/inspector, not a 3D preview).
            _headerHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _headerHost.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S1));
            contentCol.AddChild(_headerHost);

            // Simple / Advanced disclosure (Segment), built once above the fields.
            _segment = ChimeraTabs.Create(ChimeraComponents.TabsVariant.Segment, "Simple", "Advanced");
            _segment.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
            _segment.TabChanged += OnSegmentChanged;
            contentCol.AddChild(_segment);

            _bodyHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _bodyHost.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            contentCol.AddChild(_bodyHost);

            // Status line + toolbar (fixed below the scroll — the Unit Card save-row shape).
            _statusLabel = Body("", ThemeTokens.TextLo);
            _statusLabel.AutowrapMode = TextServer.AutowrapMode.Word;
            _statusLabel.Visible = false;
            root.AddChild(_statusLabel);

            root.AddChild(BuildToolbar());

            _panel.Visible = false;   // hidden until the first C toggle
        }

        // ── Per-building binding ─────────────────────────────────────────────────

        /// <summary>Rebuild every region for <paramref name="def"/>: read-only header + editable body.</summary>
        private void Bind(BuildingDefinition def)
        {
            _current    = def;
            _originalId = def.Id;
            ClearHosts();
            BuildHeader(def);
            BuildEditableBody(def);   // BuildingCardPanel.Edit.cs
            RevalidateAndReflect();   // paint any badges + set the Save/Delete enabled state for the freshly-bound building
        }

        /// <summary>Bind the building at <see cref="_index"/>, or show an empty state if the faction has no buildings.</summary>
        private void Refresh()
        {
            if (_faction is null || _faction.Buildings.Count == 0)
            {
                _current = null;
                ClearHosts();
                BuildEmptyState();
                UpdateCounter(0, 0);
                UpdateToolbarEnabled();
                return;
            }
            if (_index < 0 || _index >= _faction.Buildings.Count) _index = 0;
            Bind(_faction.Buildings[_index]);
            UpdateCounter(_index + 1, _faction.Buildings.Count);
        }

        /// <summary>Cycle the browse cursor over <c>_faction.Buildings</c> (never <c>_faction.Units</c>), wrapping both ways.</summary>
        private void Browse(int dir)
        {
            if (_faction is null || _faction.Buildings.Count == 0) return;
            int n = _faction.Buildings.Count;
            _index = ((_index + dir) % n + n) % n;
            Refresh();
        }

        private void UpdateCounter(int i, int n)
        {
            _counterLabel.Text = n == 0 ? "—" : $"BUILDING {i} / {n}";
            _prevBtn.Disabled = n <= 1;
            _nextBtn.Disabled = n <= 1;
        }

        private void ClearHosts()
        {
            foreach (Node c in _headerHost.GetChildren()) { _headerHost.RemoveChild(c); c.QueueFree(); }
            foreach (Node c in _bodyHost.GetChildren())   { _bodyHost.RemoveChild(c);   c.QueueFree(); }
            // The badge/pane/advanced-host nodes lived under _bodyHost — they are freed above; drop the stale refs.
            _badges.Clear();
            _advancedHost = null;
            _jsonPane = null;
            _meshPathInput = null;   // the Model row's LineEdit was freed with the body subtree
            _paneDirty = false;
        }

        private void BuildEmptyState()
        {
            _segment.Visible = false;
            _headerHost.AddChild(Heading("Building Editor", ThemeTokens.Txl));
            _bodyHost.AddChild(Body(_faction is null ? "No faction bound." : "This faction has no buildings — press New to add one.", ThemeTokens.TextLo));
        }

        // ── Read-only header ──────────────────────────────────────────────────────

        private void BuildHeader(BuildingDefinition def)
        {
            _segment.Visible = true;

            var title = Heading(string.IsNullOrEmpty(def.DisplayName) ? def.Id : def.DisplayName, ThemeTokens.T2xl);
            title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            title.AutowrapMode = TextServer.AutowrapMode.Word;
            _headerHost.AddChild(title);

            var id = Body(def.Id, ThemeTokens.TextLo);
            id.AddThemeFontSizeOverride("font_size", _theme.GetFontSize(ThemeTokens.Txs, ThemeTokens.Type));
            _headerHost.AddChild(id);

            var tags = new HBoxContainer();
            tags.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));

            var arch = ChimeraComponents.Tag(def.Category);
            AttachTip(arch, "Archetype", "The building's category — edit it in the Archetype field below.");
            tags.AddChild(arch);

            if (!string.IsNullOrEmpty(def.ProducesCategory) && def.ProducesCategory != "None")
            {
                var produces = ChimeraComponents.Tag($"Produces: {def.ProducesCategory}", ChimeraComponents.TagVariant.Accent);
                AttachTip(produces, "Produces", "The unit category this building trains — edit it in the Produces field below.");
                tags.AddChild(produces);
            }
            _headerHost.AddChild(tags);
        }

        // ── Small shared builders (mirror UnitCardPanel's) ───────────────────────

        private Label Heading(string text, StringName sizeToken)
        {
            var l = new Label { Text = text };
            l.AddThemeFontOverride("font", _theme.GetFont(ThemeTokens.FontDisplay, ThemeTokens.Type));
            l.AddThemeFontSizeOverride("font_size", _theme.GetFontSize(sizeToken, ThemeTokens.Type));
            l.AddThemeColorOverride("font_color", Tok(ThemeTokens.TextHi));
            return l;
        }

        private Label Body(string text, StringName colorToken)
        {
            var l = new Label { Text = text };
            l.AddThemeColorOverride("font_color", Tok(colorToken));
            l.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            return l;
        }

        private Color Tok(StringName token) => _theme.GetColor(token, ThemeTokens.Type);

        /// <summary>Attach a hover-AND-keyboard-focus tooltip (mirrors UnitCardPanel's AttachTip). The keyboard half
        /// needs <c>FocusMode.All</c> (a Readout/Tag defaults to None); the descendants are made mouse-transparent so
        /// the composite itself is the unambiguous hover target.</summary>
        private void AttachTip(Control target, string term, string body, ChimeraTooltip.TooltipRole role = ChimeraTooltip.TooltipRole.Pop)
        {
            target.MouseFilter = Control.MouseFilterEnum.Stop;
            target.FocusMode = Control.FocusModeEnum.All;
            MakeChildrenMouseIgnore(target);
            ChimeraTooltip.Attach(target, term, body, role);
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
