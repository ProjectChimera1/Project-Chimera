#nullable enable
using Godot;
using ProjectChimera.Core.Definitions;   // UnitDefinition, FactionDefinition, AbilityRegistry, UnitCardText
using ProjectChimera.UI;                  // GameState, GameMode, MeshLoader
using ProjectChimera.UI.Components;        // ChimeraComponents, ChimeraListRow, ChimeraTooltip
using ProjectChimera.UI.Theme;             // ThemeTokens, ThemeBuilder, AccentController
using GodotTheme = Godot.Theme;            // the ProjectChimera.UI.Theme namespace shadows the bare Theme type
using System.Globalization;

namespace ProjectChimera.CreationSuite
{
    /// <summary>
    /// Story 3.3 — the READ-ONLY Unit Card panel (UX-DR77). A single consolidated WC3-style card that renders an
    /// existing <see cref="UnitDefinition"/>'s stats, combat type, archetype, model preview, and attached abilities.
    /// It <b>mutates nothing</b>: editing / Save / model-browse / archetype-and-ability authoring / Promote-to-Hero all
    /// arrive in later stories (3.4–3.7). Built ENTIRELY from the Story-3.1 component kit (<see cref="ChimeraComponents"/>
    /// + <see cref="ChimeraListRow"/> + <see cref="ChimeraTooltip"/>) styled by <c>res://assets/ui/main.tres</c> — no
    /// hardcoded house palette (the clone target, AbilityEditorPanel, predates the Theme; only its lifecycle SHAPE is reused).
    ///
    /// <para><b>Determinism posture — PURE PRESENTATION, zero fold.</b> The card reads a content POCO (further from the
    /// sim than a live entity), touches no <c>EntityWorld</c>/store/sim array, and moves no golden or checksum. The only
    /// <c>src/Core</c> touch is the Godot-free <see cref="UnitCardText"/> formatter (so Tier-1 can compile it). Stamps stay
    /// 9/3/1/2 + StartStateHash 1.</para>
    ///
    /// <para><b>Kit bootstrap (D-2).</b> This panel is the FIRST in-scene consumer of the kit — <c>ChimeraComponents.Initialize</c>
    /// ran only in the two 3.1 demo scenes before now, never in <c>MainScene</c>. So the panel self-initializes the factory:
    /// it ALWAYS loads <c>main.tres</c> (the inner <see cref="PanelContainer"/> needs it regardless), and guards ONLY the
    /// one-time <see cref="AccentController"/> + <c>Initialize</c> behind <c>!IsInitialized</c> so a future startup phase
    /// (Story 3.11) makes it a clean no-op.</para>
    /// </summary>
    public partial class UnitCardPanel : Node
    {
        // ── Layout constants (component-intrinsic dims; the spacing/color TOKENS are read from the theme, per AC4) ──
        private const float PANEL_W = 460f;
        private const float PANEL_H = 640f;
        private const float MARGIN  = 12f;
        private const int   PREVIEW_W = 260;
        private const int   PREVIEW_H = 200;
        private const float TURNTABLE_SPEED = 30f; // deg/sec (AssetPreviewScene value, D-8)

        // ── Kit context (self-owned; _accent only created when this panel is the first consumer) ──
        private GodotTheme        _theme  = null!;
        private AccentController?  _accent;

        // ── Deps (wired by UnitCardPhase after AddChild) ──
        private FactionDefinition? _faction;               // the unit source (Units only — D-10); the panel never reaches _ctx
        private GameState?         _gameState;
        private AbilityRegistry    _registry = AbilityRegistry.Empty;
        private int                _index;                 // browse cursor into _faction.Units

        // ── Shell ──
        private CanvasLayer    _canvas = null!;
        private PanelContainer _panel  = null!;
        private VBoxContainer  _headerHost = null!;        // refilled per unit (above the preview)
        private VBoxContainer  _bodyHost   = null!;        // refilled per unit (model-ref + combat + stats + economy + abilities)
        private Label          _counterLabel = null!;
        private Godot.Button   _prevBtn = null!;
        private Godot.Button   _nextBtn = null!;

        // ── Preview (built ONCE; only the mesh swaps per unit) ──
        private SubViewport _subViewport = null!;
        private Camera3D    _camera      = null!;
        private Node3D      _turntable   = null!;

        // ── Lifecycle (mirrors AbilityEditorPanel's SHAPE: _Ready builds, the phase calls Initialize after AddChild) ──

        /// <inheritdoc/>
        public override void _Ready()
        {
            EnsureKitInitialized();   // MUST run before any ChimeraComponents.* call, or the factory throws
            BuildUi();
        }

        /// <summary>
        /// Bind the panel to the current scenario's faction + game state + validated ability registry. Called by
        /// <c>UnitCardPhase</c> AFTER <c>AddChild</c> (so the UI built in <see cref="_Ready"/> exists). Starts hidden;
        /// shown by the <c>J</c> toggle in Edit mode. The unit source is the <see cref="FactionDefinition"/> itself — a
        /// plain <see cref="Node"/> panel can't reach the SceneContext, and <c>ScenarioData</c> carries only a faction
        /// JSON <em>path</em>, not parsed units.
        /// </summary>
        public void Initialize(FactionDefinition? faction, GameState gameState, AbilityRegistry registry)
        {
            _faction   = faction;
            _gameState = gameState;
            _registry  = registry ?? AbilityRegistry.Empty;
            _index     = 0;

            _gameState.ModeChanged += OnModeChanged;   // authoring is Edit-only — hide in Play
            _panel.Visible = false;
        }

        /// <summary>
        /// D-1 standalone-harness entry point: load a faction JSON by <c>res://</c> path and rebind the card to it
        /// (Units only — D-10), opening at the first unit. This is the vehicle D-1/Task-8 call for so the
        /// <c>/godot-verify</c> harness can feed the <c>_unitcard_sample</c> fixture and demonstrate the AC2
        /// box-placeholder branch without the (3.4+) real select flow. Presentation-only — it loads a content POCO
        /// through the same <see cref="FactionDefinition.LoadFromFile"/> path the game uses; it mutates no sim state.
        /// </summary>
        public void LoadFactionFromPath(string resPath)
        {
            string abs = ProjectSettings.GlobalizePath(resPath);
            if (!System.IO.File.Exists(abs))
            {
                GD.PrintErr($"[UnitCard] LoadFactionFromPath: '{abs}' not found.");
                return;
            }
            _faction = FactionDefinition.LoadFromFile(abs);
            _index = 0;
            _panel.Visible = true;
            _subViewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
            Refresh();
        }

        /// <summary>Toggle visibility (J key, Edit mode only). On open: enable the preview render + (re)bind the current
        /// unit. On close: disable the preview render so a hidden card renders no 3D frame.</summary>
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
            // ALWAYS load the theme (the inner PanelContainer.Theme needs it regardless of factory state). CacheMode.Ignore
            // so we never mutate/re-mint the committed main.tres; ThemeBuilder.Build() is the in-memory fallback.
            _theme = ResourceLoader.Load<GodotTheme>(ThemeBuilder.ThemePath, cacheMode: ResourceLoader.CacheMode.Ignore)
                     ?? ThemeBuilder.Build();

            // Guard ONLY the one-time factory bootstrap so a future startup phase (3.11) that already initialized the kit
            // makes this a clean no-op (ChimeraComponents.Initialize also guards double-init, but skipping the extra
            // AccentController node keeps the tree clean). This panel is the FIRST in-scene consumer today.
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
            _canvas = new CanvasLayer { Layer = 11 };   // 11 verified free (16 also free)
            AddChild(_canvas);

            // Root surface = the kit's chamfered panel (AC4 — no hand-rolled house palette).
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

            var titleLbl = Heading("Unit Card", ThemeTokens.Tlg);
            titleLbl.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            titleLbl.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            titleRow.AddChild(titleLbl);

            _prevBtn = ChimeraComponents.IconButton("◀");
            _prevBtn.Pressed += () => Browse(-1);
            titleRow.AddChild(_prevBtn);

            _counterLabel = Body("—", ThemeTokens.TextMid);
            _counterLabel.HorizontalAlignment = HorizontalAlignment.Center;
            _counterLabel.CustomMinimumSize = new Vector2(96, 0);
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

            // Header (refilled per unit) → Preview (persistent) → Body (refilled per unit).
            _headerHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _headerHost.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S1));
            contentCol.AddChild(_headerHost);

            contentCol.AddChild(BuildPreviewHost());

            _bodyHost = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _bodyHost.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            contentCol.AddChild(_bodyHost);

            _panel.Visible = false;   // hidden until the first J toggle (avoids a one-frame flash before Initialize)
        }

        /// <summary>Build the isolated 3D preview host ONCE (Task 5). The mesh inside swaps per unit; the host persists.</summary>
        private Control BuildPreviewHost()
        {
            _subViewport = new SubViewport
            {
                Size                   = new Vector2I(PREVIEW_W, PREVIEW_H),
                RenderTargetClearMode  = SubViewport.ClearMode.Always,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled, // starts hidden → renders nothing
                OwnWorld3D             = true, // ISOLATED world — do NOT share GetViewport().World3D (that renders the game)
            };

            _camera = new Camera3D { Position = new Vector3(0f, 1.2f, 3.5f) };
            _subViewport.AddChild(_camera);

            // Studio key + fill directional lights (values from AssetPreviewScene). A fill opposing the key lifts the
            // shadow side without SDFGI (the isolated world has none), per the godot-mcp lighting note.
            var key = new DirectionalLight3D { LightEnergy = 1.4f };
            key.RotationDegrees = new Vector3(-45f, 45f, 0f);
            _subViewport.AddChild(key);

            var fill = new DirectionalLight3D { LightEnergy = 0.6f, LightSpecular = 0f };
            fill.RotationDegrees = new Vector3(-20f, -120f, 0f);
            _subViewport.AddChild(fill);

            // Ambient + solid themed backdrop so shadowed sides aren't pure black.
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

        /// <summary>Rebuild every region for <paramref name="def"/> and refresh the 3D preview.</summary>
        private void Bind(UnitDefinition def)
        {
            ClearHosts();
            BuildHeader(def);
            BuildBody(def);
            UpdatePreview(def);
        }

        /// <summary>Bind the unit at <see cref="_index"/>, or show an empty state if the faction has no units (D-1/D-10 guard).</summary>
        private void Refresh()
        {
            if (_faction is null || _faction.Units.Count == 0)
            {
                ClearHosts();
                BuildEmptyState();
                ClearPreview();
                UpdateCounter(0, 0);
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
        }

        private void BuildEmptyState()
        {
            _headerHost.AddChild(Heading("Unit Card", ThemeTokens.Txl));
            _bodyHost.AddChild(Body(_faction is null ? "No faction bound." : "This faction has no units.", ThemeTokens.TextLo));
        }

        // ── Header region (AC1, AC3; D-6) ────────────────────────────────────────

        private void BuildHeader(UnitDefinition def)
        {
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
            AttachTip(arch, "Archetype", "The unit's movement & role class — one of six (Worker, Melee, Ranged, Siege, Air, Structure).");
            tags.AddChild(arch);

            if (def.IsHero)   // D-6 — passive, non-interactive HERO tag when the def is flagged (added 3.2)
            {
                var hero = ChimeraComponents.Tag("HERO", ChimeraComponents.TagVariant.Accent);
                AttachTip(hero, "Hero", "A hero unit — mints a persistent, cross-match hero identity when it spawns.");
                tags.AddChild(hero);
            }
            _headerHost.AddChild(tags);
        }

        // ── Body regions: model ref + combat + stats + economy + abilities (AC1, AC3; D-3/D-4/D-5/D-7/D-9) ──

        private void BuildBody(UnitDefinition def)
        {
            // ── Model reference (AC1 "model reference" item) — matches what the preview actually shows. ──
            _bodyHost.AddChild(ChimeraComponents.FieldLabel("Model"));
            string meshPath = def.MeshPath ?? "";
            bool   resolves = meshPath.Length > 0 && ResourceLoader.Exists(meshPath);
            string modelValue = resolves ? FileNameOf(meshPath) : "— (box placeholder)";
            string modelBody = resolves
                ? $"Renders {meshPath}."
                : (meshPath.Length == 0
                    ? "No mesh assigned — showing a box placeholder."
                    : $"Mesh '{meshPath}' isn't imported — showing a box placeholder.");
            var modelReadout = ChimeraComponents.Readout(Tok(ThemeTokens.Info), modelValue, "Mesh");
            AttachTip(modelReadout, "Model", modelBody);
            _bodyHost.AddChild(modelReadout);

            // ── Combat: damage/armor type tags + attack/range/interval readouts. ──
            _bodyHost.AddChild(ChimeraComponents.FieldLabel("Combat"));
            var combatTags = new HBoxContainer();
            combatTags.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S2));
            var dmg = ChimeraComponents.Tag(def.DamageType);
            AttachTip(dmg, "Damage Type", $"{def.DamageType} damage — the matrix row that picks the multiplier vs each armor class.");
            combatTags.AddChild(dmg);
            var arm = ChimeraComponents.Tag(def.ArmorType);
            AttachTip(arm, "Armor Type", $"{def.ArmorType} armor — the matrix column that scales the damage this unit takes by type.");
            combatTags.AddChild(arm);
            _bodyHost.AddChild(combatTags);

            var combatStats = ReadoutRow();
            combatStats.AddChild(StatReadout(ThemeTokens.Danger, def.AttackDamage, "Attack", "Attack Damage", "Base damage per hit, before the type matrix and armor."));
            combatStats.AddChild(StatReadout(ThemeTokens.Info, def.AttackRange, "Range", "Attack Range", "0 ≈ melee. Distance the unit can strike from."));
            combatStats.AddChild(IntervalReadout(def.AttackSpeed));   // D-7
            _bodyHost.AddChild(combatStats);

            // ── Stats. ──
            _bodyHost.AddChild(ChimeraComponents.FieldLabel("Stats"));
            var stats = ReadoutRow();
            stats.AddChild(StatReadout(ThemeTokens.Ok, def.Hp, "HP", "Hit Points", "The unit's health pool. It dies at 0."));
            stats.AddChild(StatReadout(ThemeTokens.Info, def.Speed, "Speed", "Move Speed", "World units per second the unit travels."));
            stats.AddChild(StatReadout(ThemeTokens.Accent, def.Supply, "Supply", "Supply", "Population this unit draws from your supply cap."));
            stats.AddChild(StatReadout(ThemeTokens.Info, def.VisionRange, "Vision", "Vision Range", "How far the unit reveals the map."));
            _bodyHost.AddChild(stats);

            // ── Economy (D-4: ore + crystal, both shown WC3-style; crystal shown even at 0). ──
            _bodyHost.AddChild(ChimeraComponents.FieldLabel("Economy"));
            var econ = ReadoutRow();
            econ.AddChild(StatReadout(ThemeTokens.Warn, def.CostOre, "Ore", "Ore Cost", "Ore spent to train this unit."));
            econ.AddChild(StatReadout(ThemeTokens.Info, def.CostCrystal, "Crystal", "Crystal Cost", "Crystal spent to train this unit — crystal is the scarce resource."));
            _bodyHost.AddChild(econ);

            // ── Attached abilities (D-3: resolve id→name, include ALL incl. passives; D-9: inert rows). ──
            _bodyHost.AddChild(ChimeraComponents.FieldLabel("Abilities"));
            string[] labels = UnitCardText.ResolveAbilityLabels(def.Abilities, _registry);
            if (labels.Length == 0)
            {
                _bodyHost.AddChild(Body("No abilities", ThemeTokens.TextLo));   // never leave the region blank
            }
            else
            {
                var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                list.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S1));
                foreach (string label in labels)
                {
                    var row = ChimeraListRow.Create(label);
                    // D-9 "inert": no group, no selection wiring — AND mouse-inert, so these read-only rows can't
                    // hover-highlight or latch a click "selected" ring (ChimeraListRow is interactive by default).
                    row.MouseFilter = Control.MouseFilterEnum.Ignore;
                    list.AddChild(row);
                }
                _bodyHost.AddChild(list);
            }
        }

        // ── 3D preview (AC2; D-8) ────────────────────────────────────────────────

        private void UpdatePreview(UnitDefinition def)
        {
            ClearPreview();
            Color tint = FactionTint();
            // Crash-proof: MeshLoader returns a box placeholder when the path is empty/missing/unloadable (AC2 clause 2).
            Mesh mesh = MeshLoader.LoadFromGlb(def.MeshPath ?? "", new Vector3(0.8f, 1.6f, 0.8f), tint);
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

        /// <summary>Frame the mesh with a minimal AABB-based camera fit so large meshes (Siege/Structure) don't clip the
        /// fixed frame (D-8). Uses the bounding-sphere radius so the framing stays correct as the turntable rotates.</summary>
        private void FitCamera(Aabb aabb, float meshScale)
        {
            Vector3 size   = aabb.Size * meshScale;
            Vector3 center = (aabb.Position + aabb.Size * 0.5f) * meshScale;   // MeshInstance3D scales about its origin
            float radius   = Mathf.Max(0.1f, size.Length() * 0.5f);
            float fovRad   = Mathf.DegToRad(_camera.Fov);
            float dist     = radius / Mathf.Sin(fovRad * 0.5f) * 1.2f;         // bounding-sphere fit + 20% margin
            Vector3 camPos = center + new Vector3(0f, size.Y * 0.15f, dist);   // slightly above center, backed off +Z
            _camera.LookAtFromPosition(camPos, center, Vector3.Up);
            _camera.Near = 0.05f;
            _camera.Far  = dist + radius * 4f + 10f;
        }

        private Color FactionTint()
        {
            if (_faction?.Color is { Length: >= 3 } c)
                return new Color(c[0], c[1], c[2]);
            return new Color(0.6f, 0.7f, 0.9f);   // neutral fallback tint for the box placeholder
        }

        // ── Small shared builders ────────────────────────────────────────────────

        private HBoxContainer ReadoutRow()
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", ChimeraComponents.Const(ThemeTokens.S5)); // 24, matches the gallery
            return row;
        }

        /// <summary>A float-stat mono readout (UX-DR34) + hover/focus tooltip (AC3).</summary>
        private HBoxContainer StatReadout(StringName iconToken, float value, string label, string term, string body)
        {
            var r = ChimeraComponents.Readout(Tok(iconToken), UnitCardText.FormatStat(value), label);
            AttachTip(r, term, body);
            return r;
        }

        /// <summary>An int-stat mono readout (ore/crystal/supply — no decimals) + tooltip.</summary>
        private HBoxContainer StatReadout(StringName iconToken, int value, string label, string term, string body)
        {
            var r = ChimeraComponents.Readout(Tok(iconToken), value.ToString(CultureInfo.InvariantCulture), label);
            AttachTip(r, term, body);
            return r;
        }

        /// <summary>The attack-interval readout (D-7): seconds between attacks, "s" suffix, clarifying tooltip.</summary>
        private HBoxContainer IntervalReadout(float attackSpeed)
        {
            var r = ChimeraComponents.Readout(Tok(ThemeTokens.Warn), UnitCardText.FormatStat(attackSpeed) + "s", "Atk Interval");
            AttachTip(r, "Attack Interval", "Seconds between attacks — lower is faster.");
            return r;
        }

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

        /// <summary>
        /// Attach a hover-AND-keyboard-focus tooltip (AC3 / UX-DR53 / NFR-2). <see cref="ChimeraTooltip"/> reveals on
        /// <c>MouseEntered</c> (needs <c>MouseFilter = Stop</c>) and on <c>FocusEntered</c> (needs <c>FocusMode = All</c> —
        /// a Readout HBox / Tag PanelContainer defaults to <c>FocusMode.None</c>, so without this the keyboard half of AC3
        /// is silently dead). The composite's descendants are made mouse-transparent so the composite itself is the
        /// unambiguous hover target (a display child at its default Stop would otherwise eat the hover).
        /// </summary>
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
