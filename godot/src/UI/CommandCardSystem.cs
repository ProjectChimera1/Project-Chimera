#nullable enable
using Godot;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Economy;
using ProjectChimera.Multiplayer; // LockstepManager / OrderApplier / UnitOrder (Story 2.8: Train rides the command stream)

namespace ProjectChimera.UI
{
    /// <summary>
    /// Displays a command card panel at the bottom of the screen when a building is
    /// selected in Play mode. Shows building stats and available production commands.
    ///
    /// Production buildings (Barracks/ArcheryRange/SiegeWorkshop):
    ///   One "Train [UnitName]" button with live cost/time/status from the faction definition.
    ///
    /// CommandCenter: Supply cap display.
    /// Other types:   Name + HP only.
    ///
    /// The panel uses Godot Button nodes so click events are handled by the UI system
    /// and do not propagate to SelectionSystem._UnhandledInput.
    /// </summary>
    public partial class CommandCardSystem : Node
    {
        // ── Dependencies ──────────────────────────────────────────────────────

        private SelectionSystem _selection  = null!;
        private BuildingSystem  _buildSys   = null!;
        private BuildingStore   _buildings  = null!;
        private ResourceStore   _resources  = null!;
        private EntityWorld     _world      = null!;
        private LockstepManager? _lockstep;  // Story 2.8: null offline (apply Train now) / set online (enqueue for exec-tick)

        // ── Building card UI nodes ─────────────────────────────────────────────

        private Panel  _panel              = null!;
        private Label  _titleLabel         = null!;
        private Label  _hpLabel            = null!;
        private Label  _supplyLabel        = null!;  // CommandCenter only
        // Story 2.8: per-unit production picker — a grid of train buttons (one per unit of the selected building's
        // category), replacing the single _trainBtn. Fixed-size grid; slots past the category's unit count are hidden.
        private const int MAX_TRAIN_OPTIONS = 4;
        private Button[] _trainBtns        = System.Array.Empty<Button>();  // Production buildings (per-unit picker)
        private readonly int[] _trainUnitIndices = new int[MAX_TRAIN_OPTIONS]; // button slot → Units index it trains (-1 = empty)
        private Label  _trainStatus        = null!;  // "Training…  Xs" in-flight label
        private Label  _constructionLabel  = null!;  // "Under Construction  Xs"

        // ── Worker card UI nodes ──────────────────────────────────────────────

        private Panel    _workerPanel       = null!;
        private Label    _workerTitleLabel  = null!;
        private Label    _workerHpLabel     = null!;
        private Label    _workerStatusLabel = null!;   // "Building…" or hint text
        private Button[] _buildBtns         = System.Array.Empty<Button>();

        /// <summary>Entity ID of the last worker whose card was refreshed. Used by button callbacks.</summary>
        private int _lastFocusedWorkerId = -1;

        // ── Ability card UI nodes (Story 2.4b) ────────────────────────────────

        /// <summary>The validated ability registry, injected via <see cref="SetAbilityRegistry"/> — turns a per-entity
        /// <c>AbilityId</c> index into an <see cref="AbilityDefinition"/> for labels. Empty until CameraPhase wires it.</summary>
        private AbilityRegistry _registry = AbilityRegistry.Empty;
        private Panel    _abilityPanel      = null!;
        private Label    _abilityTitleLabel = null!;
        private Button[] _abilityBtns       = System.Array.Empty<Button>();

        /// <summary>Entity ID of the last focused caster whose ability card was refreshed. Read by button callbacks.</summary>
        private int _lastFocusedCasterId = -1;

        private static readonly BuildingType[] WORKER_BUILD_TYPES =
        {
            BuildingType.CommandCenter,
            BuildingType.Barracks,
            BuildingType.ArcheryRange,
            BuildingType.SiegeWorkshop,
            BuildingType.Aviary, // Story 2.8 — Air producer is worker-buildable (AC2.2). Grows the build grid to 5 (panel widened to fit).
        };

        // ── Event ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Fired when the player clicks a build button on the worker card.
        /// Payload: (workerId, buildingType).
        /// MainScene subscribes to enter placement mode.
        /// </summary>
        public event System.Action<int, BuildingType>? OnWorkerBuildRequested;

        // ── Init ──────────────────────────────────────────────────────────────

        public void Initialize(SelectionSystem selection, BuildingSystem buildSys,
                               BuildingStore buildings, ResourceStore resources,
                               EntityWorld world)
        {
            _selection = selection;
            _buildSys  = buildSys;
            _buildings = buildings;
            _resources = resources;
            _world     = world;
        }

        /// <summary>
        /// Inject the ability registry (Story 2.4b). A setter — not a 6th <see cref="Initialize"/> arg — mirrors
        /// <c>SelectionSystem.SetLockstep</c> and keeps the CameraPhase Initialize call undisturbed. Called by CameraPhase.
        /// </summary>
        public void SetAbilityRegistry(AbilityRegistry registry) => _registry = registry;

        /// <summary>
        /// Inject the per-match lockstep manager (Story 2.8, D-1). Null offline (single-player skirmish) → Train
        /// applies immediately; set online → Train is enqueued and executed at exec-tick. Mirrors
        /// <c>SelectionSystem.SetLockstep</c>; wired per match at match start (NOT in CameraPhase, where the live
        /// per-match manager does not yet exist).
        /// </summary>
        public void SetLockstep(LockstepManager? lockstep) => _lockstep = lockstep;

        public override void _Ready()
        {
            BuildPanel();
            BuildWorkerPanel();
            BuildAbilityPanel();
        }

        // ── Per-frame ─────────────────────────────────────────────────────────

        public override void _Process(double delta)
        {
            if (GameState.Instance?.Mode != GameMode.Play)
            {
                _panel.Visible        = false;
                _workerPanel.Visible  = false;
                _abilityPanel.Visible = false;
                return;
            }

            int bId     = _selection.SelectedBuildingId;
            int focusId = _selection.FocusId;

            bool buildingSelected = bId >= 0 && bId < _buildings.Count && _buildings.Alive[bId];

            // A worker is focused when no building is selected and the focused unit
            // belongs to P1 and has a non-Inactive gather state.
            bool workerSelected = !buildingSelected
                && _world != null
                && focusId >= 0
                && _world.IsAlive(focusId)
                && _world.FactionOf[focusId] == Faction.Player1
                && _world.GatherState[focusId] != GatherState.Inactive;

            // Story 2.4b: a focused P1 combat caster (≥1 resolved ability) shows the ability card. A unit that is BOTH
            // a gatherer and ability-bearing → the worker card wins (Decision C; worker-cast is Story 2.9b), hence
            // the !workerSelected guard. Reads the per-entity AbilityCount SoA directly (set by ApplyUnitDefinition).
            bool abilitySelected = !buildingSelected
                && !workerSelected
                && _world != null
                && focusId >= 0
                && _world.IsAlive(focusId)
                && _world.FactionOf[focusId] == Faction.Player1
                && _world.AbilityCount[focusId] > 0;

            _panel.Visible        = buildingSelected;
            _workerPanel.Visible  = workerSelected;
            _abilityPanel.Visible = abilitySelected;

            if (buildingSelected) RefreshCard(bId);
            if (workerSelected)   RefreshWorkerCard(focusId);
            if (abilitySelected)  RefreshAbilityCard(focusId);
        }

        // ── Card update ───────────────────────────────────────────────────────

        private void RefreshCard(int bId)
        {
            var bType   = _buildings.Type[bId];
            var faction = _buildings.FactionOf[bId];
            float hp    = _buildings.Health[bId].ToFloat();
            float maxHp = _buildings.MaxHealth[bId].ToFloat();

            string typeName = bType switch
            {
                BuildingType.CommandCenter => "Command Center",
                BuildingType.Barracks      => "Barracks",
                BuildingType.ArcheryRange  => "Archery Range",
                BuildingType.SiegeWorkshop => "Siege Workshop",
                BuildingType.Aviary        => "Aviary",
                _ => "Building"
            };

            _titleLabel.Text = $"{typeName}  [{(faction == Faction.Player1 ? "P1" : "P2")}]";
            _hpLabel.Text    = $"HP: {(int)hp} / {(int)maxHp}";

            // While under construction, show only the construction progress
            if (_buildings.IsUnderConstruction(bId))
            {
                float duration  = _buildings.ConstructionDuration[bId].ToFloat();
                float remaining = _buildings.ConstructionTimer[bId].ToFloat();
                float progress  = duration > 0f ? (1f - remaining / duration) * 100f : 100f;
                _constructionLabel.Text    = $"Under Construction  {remaining:F1}s  ({progress:F0}%)";
                _constructionLabel.Visible = true;
                HideTrainButtons();
                _trainStatus.Visible       = false;
                _supplyLabel.Visible       = false;
                return;
            }

            _constructionLabel.Visible = false;

            bool isCC = bType == BuildingType.CommandCenter;
            bool canProduce = bType == BuildingType.Barracks
                           || bType == BuildingType.ArcheryRange
                           || bType == BuildingType.SiegeWorkshop
                           || bType == BuildingType.Aviary;

            _supplyLabel.Visible = isCC;

            if (isCC)
            {
                int used = _resources.SupplyUsed[(int)faction];
                int cap  = _resources.SupplyCap[(int)faction];
                _supplyLabel.Text = $"Supply: {used} / {cap}";
            }

            if (canProduce)
            {
                // One button per unit of THIS building's category, for its ACTUAL faction (not the P1 default) — so a
                // selected P2 producer shows P2's roster. Order follows the faction Units JSON.
                var options = _buildSys.GetProductionUnits(bType, faction);
                bool isTraining = _buildings.ProductionQueue[bId] != 0;

                _trainStatus.Visible = isTraining;
                if (isTraining)
                    _trainStatus.Text = $"Training…  {_buildings.ProductionTimer[bId].ToFloat():F1}s";

                for (int i = 0; i < _trainBtns.Length; i++)
                {
                    if (i >= options.Count)
                    {
                        _trainBtns[i].Visible = false;
                        _trainUnitIndices[i]  = -1;
                        continue;
                    }

                    var (unitIndex, def) = options[i];
                    _trainUnitIndices[i]  = unitIndex;

                    int   costOre   = def.CostOre;
                    float trainTime = def.TrainTime;
                    byte  supply    = (byte)def.Supply;

                    bool    canAfford     = _resources.CanAffordOre(faction, Fixed.FromFloat(costOre));
                    bool    hasSupply     = _resources.HasSupply(faction, supply);
                    string? missingPrereq = _buildSys.GetUnmetPrereq(bId, unitIndex); // per-candidate prereq
                    bool    prereqsMet    = missingPrereq == null;

                    // Same predicate TrainUnit uses (prereq → supply → ore), plus the single in-flight job. The
                    // spend itself happens deterministically at exec-tick, not here (this grey-out is prediction only).
                    _trainBtns[i].Disabled = isTraining || !prereqsMet || !canAfford || !hasSupply;
                    // Dim prereq-locked options (don't hide them) so the player sees what unlocks later.
                    _trainBtns[i].Modulate  = prereqsMet ? Colors.White : new Color(1f, 1f, 1f, 0.6f);

                    string note = !prereqsMet ? $"[need: {missingPrereq}]"
                                : !canAfford  ? "[need ore]"
                                : !hasSupply  ? "[supply full]"
                                : $"{costOre} ore · {trainTime:F0}s";
                    _trainBtns[i].Text        = $"{def.DisplayName}\n{note}";
                    _trainBtns[i].TooltipText = $"{def.DisplayName} — {costOre} ore, {trainTime:F0}s train"; // NFR-2
                    _trainBtns[i].Visible     = true;
                }
            }
            else
            {
                HideTrainButtons();
                _trainStatus.Visible = false;
            }
        }

        // ── Button callbacks ──────────────────────────────────────────────────

        private void OnTrainSlotPressed(int slot)
        {
            // Resolve the button slot → the Units index it currently trains (mapped in RefreshCard). A hidden/empty
            // slot holds -1 and can't be pressed, but guard anyway.
            if (slot < 0 || slot >= _trainUnitIndices.Length) return;
            int unitIndex = _trainUnitIndices[slot];
            if (unitIndex < 0) return;
            IssueTrainCommand(_selection.SelectedBuildingId, unitIndex);
        }

        /// <summary>Hide every train-picker button (used when the selection is not a ready producer).</summary>
        private void HideTrainButtons()
        {
            for (int i = 0; i < _trainBtns.Length; i++)
                _trainBtns[i].Visible = false;
        }

        /// <summary>
        /// Issue a Train command for <paramref name="chosenUnitIndex"/> (−1 = first-of-category) at building
        /// <paramref name="bId"/> (Story 2.8, D-1). Routes through the shared lockstep seam — online it is ENQUEUED
        /// (executed at exec-tick by LockstepManager, so the ore/supply spend happens once, THERE — the picker's
        /// grey-out is only a local prediction); offline it applies immediately via the SAME OrderApplier the
        /// replay/online paths use (structural parity). Only the LOCAL player's (Player1) own building trains,
        /// mirroring SelectionSystem's cast/selection convention.
        /// </summary>
        private void IssueTrainCommand(int bId, int chosenUnitIndex)
        {
            if (bId < 0 || bId >= _buildings.Count) return;
            if (!_buildings.Alive[bId] || _buildings.FactionOf[bId] != Faction.Player1) return;
            // Online: EnqueueOrder returns false (queued). Offline (_lockstep == null): the ?? true yields apply-now.
            bool applyNow = _lockstep?.EnqueueOrder(bId, UnitCommand.Train,
                                                    Fixed.FromRaw(chosenUnitIndex), Fixed.Zero) ?? true;
            if (!applyNow) return; // online: LockstepManager.Flush applies it at exec-tick (spend happens THERE, once)
            var order = new UnitOrder(bId, UnitCommand.Train, Fixed.FromRaw(chosenUnitIndex), Fixed.Zero);
            OrderApplier.Apply(_world, in order, Faction.Player1, buildings: _buildSys);
        }

        // ── Panel construction ────────────────────────────────────────────────

        private void BuildPanel()
        {
            var canvas = new CanvasLayer();
            AddChild(canvas);

            var vpSize = GetViewport().GetVisibleRect().Size;

            // ── Outer panel ───────────────────────────────────────────────────
            _panel = new Panel();
            // Story 2.8: taller (140 → 175) and raised (−150 → −185) to hold the per-unit train grid, matching the
            // worker/ability cards (which sit at −185 for their 175px height and share this HUD region).
            _panel.Size     = new Vector2(420f, 175f);
            _panel.Position = new Vector2(10f, vpSize.Y - 185f);
            _panel.Visible  = false;
            // Consume mouse events — prevent clicks inside the card from deselecting
            _panel.MouseFilter = Control.MouseFilterEnum.Stop;

            var bgStyle = new StyleBoxFlat();
            bgStyle.BgColor      = new Color(0.05f, 0.07f, 0.05f, 0.88f);
            bgStyle.BorderColor  = new Color(0.25f, 0.45f, 0.25f, 0.9f);
            bgStyle.BorderWidthTop = bgStyle.BorderWidthBottom =
            bgStyle.BorderWidthLeft = bgStyle.BorderWidthRight = 2;
            bgStyle.CornerRadiusTopLeft = bgStyle.CornerRadiusTopRight =
            bgStyle.CornerRadiusBottomLeft = bgStyle.CornerRadiusBottomRight = 4;
            _panel.AddThemeStyleboxOverride("panel", bgStyle);
            canvas.AddChild(_panel);

            // ── Title ─────────────────────────────────────────────────────────
            _titleLabel = MakeLabel(new Vector2(10f, 8f), 16,
                                   new Color(0.95f, 0.90f, 0.60f));
            _panel.AddChild(_titleLabel);

            // ── HP ────────────────────────────────────────────────────────────
            _hpLabel = MakeLabel(new Vector2(10f, 30f), 13,
                                 new Color(0.65f, 0.90f, 0.65f));
            _panel.AddChild(_hpLabel);

            // ── Construction status ───────────────────────────────────────────
            _constructionLabel = MakeLabel(new Vector2(10f, 58f), 13,
                                           new Color(0.95f, 0.80f, 0.20f));
            _constructionLabel.Visible = false;
            _panel.AddChild(_constructionLabel);

            // ── Supply label (CommandCenter) ──────────────────────────────────
            _supplyLabel = MakeLabel(new Vector2(10f, 58f), 13,
                                    new Color(0.75f, 0.80f, 1.00f));
            _supplyLabel.Visible = false;
            _panel.AddChild(_supplyLabel);

            // ── Training status (in-flight "Training… Xs", above the grid) ────
            _trainStatus = MakeLabel(new Vector2(10f, 52f), 13,
                                    new Color(0.95f, 0.75f, 0.20f));
            _trainStatus.Visible = false;
            _panel.AddChild(_trainStatus);

            // ── Train buttons — per-unit production picker (Story 2.8) ─────────
            // One button per unit of the selected building's category (grid mirrors the worker/ability grids:
            // 10 + i*102 wide 98). Each slot's target unit index is resolved per-refresh into _trainUnitIndices,
            // so the captured-loop-var lambda carries only the BUTTON slot (the ability-grid pattern).
            _trainBtns = new Button[MAX_TRAIN_OPTIONS];
            for (int i = 0; i < MAX_TRAIN_OPTIONS; i++)
            {
                var btn = new Button();
                btn.Position     = new Vector2(10f + i * 102f, 74f);
                btn.Size         = new Vector2(98f, 70f);
                btn.Visible      = false;
                btn.AutowrapMode = TextServer.AutowrapMode.WordSmart; // long unit names wrap instead of clipping
                int slot = i; // capture per-iteration for the lambda
                btn.Pressed += () => OnTrainSlotPressed(slot);
                _panel.AddChild(btn);
                _trainBtns[i] = btn;
                _trainUnitIndices[i] = -1;
            }
        }

        private static Label MakeLabel(Vector2 pos, int fontSize, Color color)
        {
            var lbl = new Label();
            lbl.Position = pos;
            lbl.AddThemeColorOverride("font_color", color);
            lbl.AddThemeFontSizeOverride("font_size", fontSize);
            return lbl;
        }

        // ── Worker card construction ──────────────────────────────────────────

        private void BuildWorkerPanel()
        {
            var canvas = new CanvasLayer();
            AddChild(canvas);

            var vpSize = GetViewport().GetVisibleRect().Size;

            _workerPanel = new Panel();
            // Story 2.8: widened 420 → 530 so the 5th build button (Aviary) fits. Buttons lay at 10 + i*102 (width
            // 98), so button i=4 spans [418, 516] — the old 420-wide panel clipped it past the border.
            _workerPanel.Size     = new Vector2(530f, 175f);
            _workerPanel.Position = new Vector2(10f, vpSize.Y - 185f);
            _workerPanel.Visible  = false;
            _workerPanel.MouseFilter = Control.MouseFilterEnum.Stop;

            var bgStyle = new StyleBoxFlat();
            bgStyle.BgColor      = new Color(0.05f, 0.06f, 0.08f, 0.88f);
            bgStyle.BorderColor  = new Color(0.25f, 0.40f, 0.65f, 0.9f);
            bgStyle.BorderWidthTop = bgStyle.BorderWidthBottom =
            bgStyle.BorderWidthLeft = bgStyle.BorderWidthRight = 2;
            bgStyle.CornerRadiusTopLeft = bgStyle.CornerRadiusTopRight =
            bgStyle.CornerRadiusBottomLeft = bgStyle.CornerRadiusBottomRight = 4;
            _workerPanel.AddThemeStyleboxOverride("panel", bgStyle);
            canvas.AddChild(_workerPanel);

            _workerTitleLabel = MakeLabel(new Vector2(10f, 8f), 16,
                                          new Color(0.70f, 0.85f, 1.00f));
            _workerPanel.AddChild(_workerTitleLabel);

            _workerHpLabel = MakeLabel(new Vector2(10f, 30f), 13,
                                       new Color(0.65f, 0.90f, 0.65f));
            _workerPanel.AddChild(_workerHpLabel);

            _workerStatusLabel = MakeLabel(new Vector2(10f, 50f), 12,
                                           new Color(0.95f, 0.80f, 0.20f));
            _workerPanel.AddChild(_workerStatusLabel);

            // ── Build buttons — one per buildable type ─────────────────────────
            _buildBtns = new Button[WORKER_BUILD_TYPES.Length];
            for (int i = 0; i < WORKER_BUILD_TYPES.Length; i++)
            {
                var btn = new Button();
                btn.Position = new Vector2(10f + i * 102f, 74f);
                btn.Size     = new Vector2(98f, 70f);

                var bType = WORKER_BUILD_TYPES[i]; // capture for lambda
                btn.Pressed += () => OnBuildBtnPressed(bType);

                _workerPanel.AddChild(btn);
                _buildBtns[i] = btn;
            }
        }

        // ── Worker card refresh ───────────────────────────────────────────────

        private void RefreshWorkerCard(int focusId)
        {
            _lastFocusedWorkerId = focusId;

            float hp    = _world.Health[focusId].ToFloat();
            float maxHp = _world.EffectiveMaxHealth[focusId].ToFloat();

            _workerTitleLabel.Text = "Worker  [P1]";
            _workerHpLabel.Text    = $"HP: {(int)hp} / {(int)maxHp}";

            bool isBuilding = _world.CommandState[focusId] == UnitCommand.Build;
            if (isBuilding)
            {
                int bId    = _world.BuildTarget[focusId];
                string bName = (bId >= 0 && bId < _buildings.Count)
                    ? BuildingTypeName(_buildings.Type[bId]) : "building";
                _workerStatusLabel.Text = $"Building {bName}…";
            }
            else
            {
                _workerStatusLabel.Text = _world.GatherState[focusId] switch
                {
                    GatherState.Idle             => "Idle",
                    GatherState.MovingToResource => "→ Resource",
                    GatherState.Gathering        => "Gathering",
                    GatherState.MovingToBase     => "→ Base",
                    _                            => "Idle",
                };
            }

            // Refresh build buttons
            var faction = _world.FactionOf[focusId];
            for (int i = 0; i < _buildBtns.Length; i++)
            {
                var bType   = WORKER_BUILD_TYPES[i];
                float cost  = _buildSys.GetBuildingCost(bType, faction);
                string? pre = _buildSys.GetBuildingPlacePrereq(bType, faction);

                bool canAfford = _resources.CanAffordOre(faction, Fixed.FromFloat(cost));
                bool prereqMet = pre == null;

                _buildBtns[i].Disabled = isBuilding || !prereqMet || !canAfford;

                string note = !prereqMet ? $"\n[need: {pre}]"
                            : !canAfford  ? "\n[need ore]"
                            : cost > 0f   ? $"\n{(int)cost} ore"
                            : "\n(free)";
                _buildBtns[i].Text = BuildingTypeName(bType) + note;
            }
        }

        // ── Worker build button callback ──────────────────────────────────────

        private void OnBuildBtnPressed(BuildingType bType)
        {
            if (_lastFocusedWorkerId < 0) return;
            OnWorkerBuildRequested?.Invoke(_lastFocusedWorkerId, bType);
        }

        // ── Ability card construction (Story 2.4b) ────────────────────────────

        private void BuildAbilityPanel()
        {
            var canvas = new CanvasLayer();
            AddChild(canvas);

            var vpSize = GetViewport().GetVisibleRect().Size;

            // Same command-card region as the worker panel — only one of building/worker/ability shows at a time.
            _abilityPanel = new Panel();
            _abilityPanel.Size     = new Vector2(420f, 175f);
            _abilityPanel.Position = new Vector2(10f, vpSize.Y - 185f);
            _abilityPanel.Visible  = false;
            _abilityPanel.MouseFilter = Control.MouseFilterEnum.Stop; // consume clicks so they don't deselect

            var bgStyle = new StyleBoxFlat();
            bgStyle.BgColor      = new Color(0.07f, 0.05f, 0.09f, 0.88f);
            bgStyle.BorderColor  = new Color(0.55f, 0.35f, 0.75f, 0.9f);
            bgStyle.BorderWidthTop = bgStyle.BorderWidthBottom =
            bgStyle.BorderWidthLeft = bgStyle.BorderWidthRight = 2;
            bgStyle.CornerRadiusTopLeft = bgStyle.CornerRadiusTopRight =
            bgStyle.CornerRadiusBottomLeft = bgStyle.CornerRadiusBottomRight = 4;
            _abilityPanel.AddThemeStyleboxOverride("panel", bgStyle);
            canvas.AddChild(_abilityPanel);

            _abilityTitleLabel = MakeLabel(new Vector2(10f, 8f), 16,
                                           new Color(0.85f, 0.70f, 1.00f));
            _abilityPanel.AddChild(_abilityTitleLabel);

            // ── Ability buttons — one per slot (MAX_ABILITIES_PER_UNIT) ────────
            _abilityBtns = new Button[EntityWorld.MAX_ABILITIES_PER_UNIT];
            for (int i = 0; i < _abilityBtns.Length; i++)
            {
                var btn = new Button();
                btn.Position = new Vector2(10f + i * 102f, 40f);
                btn.Size     = new Vector2(98f, 70f);

                int slot = i; // capture for lambda (per-slot index, not the loop variable)
                btn.Pressed += () => OnAbilityBtnPressed(slot);

                _abilityPanel.AddChild(btn);
                _abilityBtns[i] = btn;
            }
        }

        // ── Ability card refresh (Story 2.4b) ─────────────────────────────────

        private void RefreshAbilityCard(int focusId)
        {
            _lastFocusedCasterId = focusId;
            _abilityTitleLabel.Text = "Abilities  [P1]";

            var faction = _world.FactionOf[focusId];
            int count   = _world.AbilityCount[focusId];
            if (count > EntityWorld.MAX_ABILITIES_PER_UNIT) count = EntityWorld.MAX_ABILITIES_PER_UNIT;

            for (int slot = 0; slot < _abilityBtns.Length; slot++)
            {
                var btn = _abilityBtns[slot];

                // Hide unused buttons (slot >= AbilityCount) and any empty/out-of-range slot.
                if (slot >= count)
                {
                    btn.Visible = false;
                    continue;
                }
                int regIdx = _world.AbilityId[focusId * EntityWorld.MAX_ABILITIES_PER_UNIT + slot];
                if (regIdx < 0 || regIdx >= _registry.Count)
                {
                    btn.Visible = false;
                    continue;
                }
                btn.Visible = true;
                AbilityDefinition ab = _registry.Get(regIdx);

                // Affordability — IDENTICAL to the sim's refusal (AbilityCastSystem.cs:112-121); do NOT re-derive, so
                // the greyed-out button never diverges from what the sim would refuse (AC1). READS sim arrays only.
                int  cdTicks  = _world.AbilityCooldownTicks[focusId * EntityWorld.MAX_ABILITIES_PER_UNIT + slot];
                bool onCd     = cdTicks > 0;
                bool energyOk = _world.Energy[focusId] >= ab.CostEnergy;
                bool oreOk    = _resources.CanAffordOre(faction, Fixed.FromInt(ab.CostOre));
                bool crysOk   = _resources.CanAffordCrystal(faction, Fixed.FromInt(ab.CostCrystal));

                bool groundCast = ab.ParsedTargeting == AbilityTargeting.GroundPoint;
                bool unknownTgt = ab.ParsedTargeting == null;

                // GroundPoint → disabled with a "coming soon" note (the 2.4a fence — no ground reticle is built here);
                // an unknown targeting string → disabled (fail-closed). Otherwise the button is disabled iff the sim
                // would refuse the cast. cdTicks/30f is presentation display math (no-float rule is src/Core+Effects only).
                btn.Disabled = groundCast || unknownTgt || onCd || !energyOk || !oreOk || !crysOk;

                string note = groundCast ? "[ground-cast: coming soon]"
                            : unknownTgt ? "[unsupported]"
                            : onCd       ? $"[on CD {cdTicks / 30f:F1}s]"
                            : !energyOk  ? "[need energy]"
                            : !oreOk     ? "[need ore]"
                            : !crysOk    ? "[need crystal]"
                            : CostSummary(ab);
                btn.Text = $"{ab.DisplayName}\n{note}";
            }
        }

        /// <summary>
        /// Compact cost/cooldown summary for an enabled ability button (presentation-only display math — the
        /// <c>ToInt()</c>/<c>ToFloat()</c> conversions are display-side, the analyzer's no-float rule applies to
        /// <c>src/Core</c>/<c>src/Effects</c> only, exactly like the train-timer's <c>ToFloat()</c> in RefreshCard).
        /// </summary>
        private static string CostSummary(AbilityDefinition ab)
        {
            var parts = new System.Collections.Generic.List<string>(4);
            int energy = ab.CostEnergy.ToInt();
            if (energy > 0)         parts.Add($"{energy} energy");
            if (ab.CostOre > 0)     parts.Add($"{ab.CostOre} ore");
            if (ab.CostCrystal > 0) parts.Add($"{ab.CostCrystal} crystal");
            float cd = ab.Cooldown.ToFloat();
            if (cd > 0f)            parts.Add($"{cd:F0}s CD");
            return parts.Count > 0 ? string.Join("  ·  ", parts) : "ready";
        }

        // ── Ability button callback (Story 2.4b) ──────────────────────────────

        private void OnAbilityBtnPressed(int slot)
        {
            int focusId = _lastFocusedCasterId;
            if (focusId < 0 || !_world.IsAlive(focusId)) return;
            if (slot < 0 || slot >= _world.AbilityCount[focusId]) return;

            int regIdx = _world.AbilityId[focusId * EntityWorld.MAX_ABILITIES_PER_UNIT + slot];
            if (regIdx < 0 || regIdx >= _registry.Count) return;
            AbilityDefinition ab = _registry.Get(regIdx);

            // Branch on targeting. The card READS _world for display only; the cast leaves as an intent via _selection.
            switch (ab.ParsedTargeting)
            {
                case AbilityTargeting.Self:
                case AbilityTargeting.None:
                    _selection.IssueCastAbilityCommand(focusId, slot, -1); // instant self-cast, no targeting click
                    break;
                case AbilityTargeting.TargetUnit:
                    _selection.ArmCastTargeting(focusId, slot);            // next left-click picks the enemy target
                    break;
                // GroundPoint / null are rendered Disabled in RefreshAbilityCard, so a press can't reach here — defensive.
                default:
                    break;
            }
        }

        // ── Shared helpers ────────────────────────────────────────────────────

        private static string BuildingTypeName(BuildingType t) => t switch
        {
            BuildingType.CommandCenter => "Command Center",
            BuildingType.Barracks      => "Barracks",
            BuildingType.ArcheryRange  => "Archery Range",
            BuildingType.SiegeWorkshop => "Siege Workshop",
            BuildingType.Aviary        => "Aviary",
            _ => "Building"
        };
    }
}
