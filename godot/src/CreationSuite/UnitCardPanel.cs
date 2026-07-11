#nullable enable
using System.Collections.Generic;
using Godot;
using ProjectChimera.Core.Definitions;   // UnitDefinition, FactionDefinition, AbilityRegistry, UnitCardText, UnitDefinitionValidator
using ProjectChimera.UI;                  // GameState, GameMode, MeshLoader
using ProjectChimera.UI.Components;        // ChimeraComponents, ChimeraTabs, ChimeraTooltip, ChimeraValidationBadge
using ProjectChimera.UI.Theme;             // ThemeTokens, ThemeBuilder, AccentController
using GodotTheme = Godot.Theme;            // the ProjectChimera.UI.Theme namespace shadows the bare Theme type

namespace ProjectChimera.CreationSuite
{
    /// <summary>
    /// Story 3.3/3.4 — the Unit Card Editor (UX-DR77). Story 3.3 shipped the read-only card; <b>3.4 makes that same
    /// panel editable in place</b> (D-2): the readouts become <see cref="ChimeraComponents"/> inputs bound to the live
    /// <see cref="UnitDefinition"/>, and the panel gains Save / New / Duplicate / Delete, a Simple/Advanced disclosure
    /// with a raw-JSON escape hatch, fail-closed inline validation with per-field located badges (UX-DR55), undo/redo,
    /// and write-back to the faction JSON on disk. It reuses the 3.3 browse (<c>_faction.Units</c> + <c>_index</c>), the
    /// 3D preview, the kit bootstrap, and the read-only header — it does not clone them (see 3.4 story, D-2).
    ///
    /// <para><b>This file</b> is the shell + the preserved 3.3 surfaces (kit bootstrap, browse, 3D preview, read-only
    /// header, tooltips). The 3.4 edit surface — editable fields, disclosure, raw-JSON hatch, validation, persistence,
    /// undo/redo, toolbar, input — lives in the partial <see cref="UnitCardPanel"/> file <c>UnitCardPanel.Edit.cs</c>.</para>
    ///
    /// <para><b>Determinism posture — PURE AUTHORING-TIME, zero fold.</b> Editing a content POCO and rewriting a JSON
    /// file touches no <c>EntityWorld</c>/store/sim array and moves no checksum or golden (<c>CanonicalModelHash</c>
    /// folds ScenarioData by path-string + unit-id, never by unit stats). The only <c>src/Core</c> touches are the new
    /// Godot-free <see cref="UnitDefinitionValidator"/> + <see cref="FactionWriter"/>. Stamps stay 9/3/1/2 + StartStateHash 1.</para>
    /// </summary>
    public partial class UnitCardPanel : Node
    {
        // ── Layout constants (component-intrinsic dims; the spacing/color TOKENS are read from the theme) ──
        private const float PANEL_W = 480f;
        private const float PANEL_H = 700f;
        private const float MARGIN  = 12f;
        private const int   PREVIEW_W = 240;
        private const int   PREVIEW_H = 180;
        private const float TURNTABLE_SPEED = 30f; // deg/sec (AssetPreviewScene value, D-8)

        // ── Kit context (self-owned; _accent only created when this panel is the first consumer) ──
        private GodotTheme        _theme  = null!;
        private AccentController?  _accent;

        // ── Deps (wired by UnitCardPhase after AddChild) ──
        private FactionDefinition? _faction;               // the unit source (Units only — D-10)
        private GameState?         _gameState;
        private AbilityRegistry    _registry = AbilityRegistry.Empty;
        private BehaviorRegistry   _behaviorRegistry = BehaviorRegistry.Empty;   // Story 3.6 — the behavior picker + compat source
        private int                _index;                 // browse cursor into _faction.Units
        private string             _factionJsonPath = "";  // res:// path of the faction file to write edits back to (D-8)

        // ── Edit state (Story 3.4) ──
        private UnitDefinition?    _current;               // the unit currently bound/edited (== _faction.Units[_index])
        private string             _originalId = "";       // the bound unit's id at Bind time — the PatchFactionJson target (survives an id rename)
        private readonly EditorHistory            _history   = new();   // own instance (D-6); reused by Ctrl+Z/Y when visible
        private readonly UnitDefinitionValidator  _validator = new();   // the Godot-free AR-39 gate (D-9)
        // JSON key → located badge(s) (UX-DR55). A List, not a single badge, because Story 5.9 added a second
        // Simple-mode row for a field (Ultimate ability) that already had an Advanced-mode row under the SAME
        // key — a single-value map let the second MakeBadge call silently overwrite the first, so the Simple row
        // never reflected a validation error even while visible. ShowBadge now fans an error out to every badge
        // registered under that key.
        private readonly Dictionary<string, List<ChimeraValidationBadge>> _badges = new();
        private bool _building;                            // guard: suppress live handlers while (re)building controls
        private LineEdit? _meshPathInput;                  // the Model row's text field (Story 3.5 — Browse/Box write .Text here)
        private bool _lastMeshMissing;                     // last UpdatePreview fell back to the box for a NON-blank path (missing OR failed-to-load — D-3)

        // ── Shell ──
        private CanvasLayer    _canvas = null!;
        private PanelContainer _panel  = null!;
        private VBoxContainer  _headerHost = null!;        // read-only header (refilled per unit)
        private VBoxContainer  _bodyHost   = null!;        // editable fields (refilled per unit)
        private Label          _counterLabel = null!;
        private Godot.Button   _prevBtn = null!;
        private Godot.Button   _nextBtn = null!;

        // ── Disclosure + edit chrome (built once) ──
        private ChimeraTabs    _segment = null!;           // Simple / Advanced (UX-DR54, Segment — D-3)
        private VBoxContainer?  _advancedHost;             // advanced-fields + raw-JSON subtree (rebuilt per unit; visibility = segment)
        private TextEdit?       _jsonPane;                 // raw-JSON escape hatch over the single unit (D-5)
        private bool            _paneDirty;                // the raw-JSON pane has manual edits not yet folded back
        private bool            _suppressPaneDirty;        // a programmatic SetPaneText must not mark the pane dirty
        private Label           _statusLabel = null!;      // save/validation status line
        private Godot.Button    _saveBtn   = null!;
        private Godot.Button    _newBtn    = null!;
        private Godot.Button    _dupBtn    = null!;
        private Godot.Button    _deleteBtn = null!;

        // ── Preview (built ONCE; only the mesh swaps per unit) ──
        private SubViewport _subViewport = null!;
        private Camera3D    _camera      = null!;
        private Node3D      _turntable   = null!;

        // ── Lifecycle ─────────────────────────────────────────────────────────────

        /// <inheritdoc/>
        public override void _Ready()
        {
            EnsureKitInitialized();   // MUST run before any ChimeraComponents.* call, or the factory throws
            BuildUi();
        }

        /// <summary>
        /// Bind the panel to the current scenario's faction + game state + validated ability registry + the faction
        /// file <c>res://</c> path to persist edits to (D-8). Called by <c>UnitCardPhase</c> AFTER <c>AddChild</c>.
        /// Starts hidden; shown by the <c>J</c> toggle in Edit mode.
        /// </summary>
        public void Initialize(FactionDefinition? faction, GameState gameState, AbilityRegistry registry,
                               BehaviorRegistry behaviorRegistry, string factionJsonPath = "")
        {
            _faction          = faction;
            _gameState        = gameState;
            _registry         = registry ?? AbilityRegistry.Empty;
            _behaviorRegistry = behaviorRegistry ?? BehaviorRegistry.Empty;
            _factionJsonPath  = factionJsonPath ?? "";
            _index           = 0;

            _gameState.ModeChanged += OnModeChanged;   // authoring is Edit-only — hide in Play
            _panel.Visible = false;
        }

        /// <summary>
        /// Standalone-harness entry point (D-1 / <c>/godot-verify</c>): load a faction JSON by <c>res://</c> path,
        /// rebind the card to it (Units only), and set it as the write-back target. Presentation-only.
        /// </summary>
        public void LoadFactionFromPath(string resPath)
        {
            string abs = ProjectSettings.GlobalizePath(resPath);
            if (!System.IO.File.Exists(abs))
            {
                GD.PrintErr($"[UnitCard] LoadFactionFromPath: '{abs}' not found.");
                return;
            }
            _faction         = FactionDefinition.LoadFromFile(abs);
            _factionJsonPath = resPath;   // persist edits back to the same file
            _index           = 0;
            _panel.Visible   = true;
            _subViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
            Refresh();
        }

        /// <summary>Toggle visibility (J key, Edit mode only). On open: enable the preview render + (re)bind the current unit.</summary>
        public void Toggle()
        {
            _panel.Visible = !_panel.Visible;
            if (_panel.Visible)
            {
                _subViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
                Refresh();
            }
            else
            {
                _subViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
            }
        }

        /// <summary>Hide the panel and stop rendering the preview.</summary>
        public void Close()
        {
            _panel.Visible = false;
            if (_subViewport != null!) _subViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        }

        // ── Story 5.9 (onboarding) ────────────────────────────────────────────────

        /// <summary>The curated fixed list of existing unit ids offered as onboarding "starter templates" (spec
        /// Boundaries/Never: a small fixed list via the existing Duplicate path, not a gallery UI). Ids must exist
        /// in the bound faction's roster (both shipped factions carry all three) — an id that doesn't resolve
        /// still opens the panel (see <see cref="StartFromTemplate"/>), it just skips the duplicate.</summary>
        public static readonly (string Id, string Label)[] CuratedTemplateUnits =
        {
            ("worker",   "Worker (Economy)"),
            ("infantry", "Infantry (Melee)"),
            ("archer",   "Archer (Ranged)"),
        };

        /// <summary>Ensure the panel is visible without changing which unit is bound — a no-op if already open.
        /// Backs onboarding steps 2/3, which revisit the SAME unit <see cref="StartFromTemplate"/> created in step 1
        /// rather than duplicating again.</summary>
        public void EnsureVisible()
        {
            if (!_panel.Visible) Toggle();
        }

        /// <summary>Onboarding step 1 (Story 5.9): open the panel and duplicate a curated template unit, selected
        /// and ready for editing. Drives the SAME <c>Duplicate</c> path a manual toolbar click uses (D-2 — never
        /// re-implemented here). Returns whether the duplicate actually happened; a template id that isn't in the
        /// bound faction's roster still opens the panel on whatever unit is currently browsed (never a silent
        /// no-op window), but the caller can now tell the two cases apart instead of assuming success (Story 5.9
        /// review pass — was: the onboarding overlay always claimed "Created a copy…" even on this fallback).</summary>
        public bool StartFromTemplate(string templateUnitId)
        {
            _panel.Visible = true;
            _subViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;

            int idx = _faction?.Units.FindIndex(u => u.Id == templateUnitId) ?? -1;
            if (idx >= 0)
            {
                _index = idx;
                Refresh();
                DoDuplicate();   // clones + selects + pushes undo, exactly like the manual "Duplicate" button
                return true;
            }

            Refresh();
            return false;
        }

        private void OnModeChanged(int mode)
        {
            if (mode == (int)GameMode.Play) Close();   // hide in Play (authoring is Edit-only)
        }

        /// <inheritdoc/>
        public override void _Process(double delta)
        {
            if (_panel is null || !_panel.Visible) return;
            _turntable.RotateY(Mathf.DegToRad(TURNTABLE_SPEED * (float)delta));   // slow live turntable (D-8)
        }

        // ── Kit bootstrap (D-2) ──────────────────────────────────────────────────

        private void EnsureKitInitialized()
        {
            // ALWAYS load the theme (the inner PanelContainer.Theme needs it regardless of factory state).
            _theme = ResourceLoader.Load<GodotTheme>(ThemeBuilder.ThemePath, cacheMode: ResourceLoader.CacheMode.Ignore)
                     ?? ThemeBuilder.Build();

            // Guard ONLY the one-time factory bootstrap so a future startup phase (3.11) makes this a clean no-op.
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
            _canvas = new CanvasLayer { Layer = 11 };
            AddChild(_canvas);

            _panel = ChimeraComponents.Panel(ChimeraComponents.PanelVariant.Default);
            _panel.SetAnchorsPreset(Control.LayoutPreset.CenterRight);
            _panel.CustomMinimumSize = new Vector2(PANEL_W, PANEL_H);
            _panel.Position = new Vector2(-(PANEL_W + MARGIN), -PANEL_H * 0.5f);
            _panel.Theme = _theme;   // _panel is a Control (UnitCardPanel : Node has NO Theme) — propagates to the subtree
            _canvas.AddChild(_panel);

            var root = new VBoxContainer();
            root.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S3));
            _panel.AddChild(root);

            // Title + browse + close row.
            var titleRow = new HBoxContainer();
            titleRow.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            root.AddChild(titleRow);

            var titleLbl = Heading("Unit Editor", ThemeTokens.Tlg);
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

            var closeBtn = ChimeraComponents.Button("Close [J]", ChimeraComponents.ButtonVariant.Secondary, ChimeraComponents.ButtonSize.Sm);
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

            // Read-only header → Preview (persistent) → disclosure Segment → editable body (refilled per unit).
            _headerHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _headerHost.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S1));
            contentCol.AddChild(_headerHost);

            contentCol.AddChild(BuildPreviewHost());

            // Simple / Advanced disclosure (UX-DR54 Segment — D-3), built once above the fields.
            _segment = ChimeraTabs.Create(ChimeraComponents.TabsVariant.Segment, "Simple", "Advanced");
            _segment.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
            _segment.TabChanged += OnSegmentChanged;
            contentCol.AddChild(_segment);

            _bodyHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _bodyHost.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            contentCol.AddChild(_bodyHost);

            // Status line + toolbar (fixed below the scroll — the AbilityEditor save-row shape).
            _statusLabel = Body("", ThemeTokens.TextLo);
            _statusLabel.AutowrapMode = TextServer.AutowrapMode.Word;
            _statusLabel.Visible = false;
            root.AddChild(_statusLabel);

            root.AddChild(BuildToolbar());

            _panel.Visible = false;   // hidden until the first J toggle
        }

        /// <summary>Build the isolated 3D preview host ONCE. The mesh inside swaps per unit; the host persists.</summary>
        private Control BuildPreviewHost()
        {
            _subViewport = new SubViewport
            {
                Size                   = new Vector2I(PREVIEW_W, PREVIEW_H),
                RenderTargetClearMode  = SubViewport.ClearMode.Always,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled, // starts hidden → renders nothing
                OwnWorld3D             = true, // ISOLATED world — do NOT share the game world
            };

            _camera = new Camera3D { Position = new Vector3(0f, 1.2f, 3.5f) };
            _subViewport.AddChild(_camera);

            var key = new DirectionalLight3D { LightEnergy = 1.4f };
            key.RotationDegrees = new Vector3(-45f, 45f, 0f);
            _subViewport.AddChild(key);

            var fill = new DirectionalLight3D { LightEnergy = 0.6f, LightSpecular = 0f };
            fill.RotationDegrees = new Vector3(-20f, -120f, 0f);
            _subViewport.AddChild(fill);

            var worldEnv = new WorldEnvironment();
            var env = new Godot.Environment
            {
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor  = new Color(0.35f, 0.35f, 0.4f),
                AmbientLightEnergy = 0.6f,
                BackgroundMode     = Godot.Environment.BGMode.Color,
                BackgroundColor    = Tok(ThemeTokens.Surface0),
            };
            worldEnv.Environment = env;
            _subViewport.AddChild(worldEnv);

            _turntable = new Node3D();
            _subViewport.AddChild(_turntable);

            var container = new SubViewportContainer
            {
                Stretch       = true,
                StretchShrink = 1,
                MouseFilter   = Control.MouseFilterEnum.Ignore,
                SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter,
            };
            container.CustomMinimumSize = new Vector2(PREVIEW_W, PREVIEW_H);
            container.AddChild(_subViewport);
            return container;
        }

        // ── Per-unit binding ─────────────────────────────────────────────────────

        /// <summary>Rebuild every region for <paramref name="def"/>: read-only header, editable body, and the 3D preview.</summary>
        private void Bind(UnitDefinition def)
        {
            _current    = def;
            _originalId = def.Id;
            ClearHosts();
            BuildHeader(def);
            BuildEditableBody(def);   // Story 3.4 (UnitCardPanel.Edit.cs) — replaces the 3.3 read-only readouts
            UpdatePreview(def);
            RevalidateAndReflect();   // paint any badges + set the Save/Delete enabled state for the freshly-bound unit
        }

        /// <summary>Bind the unit at <see cref="_index"/>, or show an empty state if the faction has no units.</summary>
        private void Refresh()
        {
            if (_faction is null || _faction.Units.Count == 0)
            {
                _current = null;
                ClearHosts();
                BuildEmptyState();
                ClearPreview();
                UpdateCounter(0, 0);
                UpdateToolbarEnabled();
                return;
            }
            if (_index < 0 || _index >= _faction.Units.Count) _index = 0;
            Bind(_faction.Units[_index]);
            UpdateCounter(_index + 1, _faction.Units.Count);
        }

        /// <summary>Cycle the browse cursor over <c>_faction.Units</c> (Units only — D-10), wrapping both ways.</summary>
        private void Browse(int dir)
        {
            if (_faction is null || _faction.Units.Count == 0) return;
            int n = _faction.Units.Count;
            _index = ((_index + dir) % n + n) % n;
            Refresh();
        }

        private void UpdateCounter(int i, int n)
        {
            _counterLabel.Text = n == 0 ? "—" : $"UNIT {i} / {n}";
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
            _meshPathInput = null;   // the Model row's LineEdit was freed with the body subtree (Story 3.5)
            _paneDirty = false;
        }

        private void BuildEmptyState()
        {
            _segment.Visible = false;
            _headerHost.AddChild(Heading("Unit Editor", ThemeTokens.Txl));
            _bodyHost.AddChild(Body(_faction is null ? "No faction bound." : "This faction has no units — press New to add one.", ThemeTokens.TextLo));
        }

        // ── Read-only header (kept from 3.3; HERO tag stays read-only — Promote-to-Hero is 3.7) ──

        private void BuildHeader(UnitDefinition def)
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
            AttachTip(arch, "Archetype", "The unit's movement & role class — edit it in the Archetype field below.");
            tags.AddChild(arch);

            if (def.IsHero)   // passive, read-only HERO tag (added 3.2; the Promote-to-Hero switch is 3.7)
            {
                var hero = ChimeraComponents.Tag("HERO", ChimeraComponents.TagVariant.Accent);
                AttachTip(hero, "Hero", "A hero unit — mints a persistent, cross-match hero identity when it spawns.");
                tags.AddChild(hero);
            }
            _headerHost.AddChild(tags);
        }

        // ── 3D preview (kept from 3.3; D-8) ──────────────────────────────────────

        private void UpdatePreview(UnitDefinition def)
        {
            ClearPreview();
            Color tint = FactionTint();
            // Crash-proof: MeshLoader returns a box placeholder when the path is empty/missing/unloadable.
            // usedPlaceholder distinguishes a real load from a fallback so the badge can flag a model that
            // fails to load, not just a missing path (Story 3.5, D-3).
            Mesh mesh = MeshLoader.LoadFromGlb(def.MeshPath ?? "", new Vector3(0.8f, 1.6f, 0.8f), tint, out bool usedPlaceholder);
            _lastMeshMissing = usedPlaceholder && !string.IsNullOrEmpty(def.MeshPath);
            var mi = new MeshInstance3D { Mesh = mesh };
            mi.Scale = MeshLoader.ScaleFromDefinition(def.MeshScale);
            _turntable.AddChild(mi);
            FitCamera(mesh.GetAabb(), def.MeshScale);
        }

        private void ClearPreview()
        {
            if (_turntable is null) return;
            foreach (Node c in _turntable.GetChildren()) c.QueueFree();
            _turntable.Rotation = Vector3.Zero;
        }

        /// <summary>Frame the mesh with a minimal AABB-based camera fit so large meshes don't clip the fixed frame (D-8).</summary>
        private void FitCamera(Aabb aabb, float meshScale)
        {
            Vector3 size   = aabb.Size * meshScale;
            Vector3 center = (aabb.Position + aabb.Size * 0.5f) * meshScale;
            float radius   = Mathf.Max(0.1f, size.Length() * 0.5f);
            float fovRad   = Mathf.DegToRad(_camera.Fov);
            float dist     = radius / Mathf.Sin(fovRad * 0.5f) * 1.2f;
            Vector3 camPos = center + new Vector3(0f, size.Y * 0.15f, dist);
            _camera.LookAtFromPosition(camPos, center, Vector3.Up);
            _camera.Near = 0.05f;
            _camera.Far  = dist + radius * 4f + 10f;
        }

        private Color FactionTint()
        {
            if (_faction?.Color is { Length: >= 3 } c)
                return new Color(c[0], c[1], c[2]);
            return new Color(0.6f, 0.7f, 0.9f);
        }

        // ── Small shared builders (kept from 3.3) ────────────────────────────────

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

        private static string FileNameOf(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash >= 0 ? path[(slash + 1)..] : path;
        }

        /// <summary>Attach a hover-AND-keyboard-focus tooltip (AC3 / UX-DR53 / NFR-2). Thin forwarder to the
        /// centralized <see cref="ChimeraTooltip.AttachFocusable"/> (Story 5.9 review pass — kept as a local
        /// wrapper so existing unqualified call sites in this file don't need touching).</summary>
        private void AttachTip(Control target, string term, string body, ChimeraTooltip.TooltipRole role = ChimeraTooltip.TooltipRole.Pop)
            => ChimeraTooltip.AttachFocusable(target, term, body, role);
    }
}
