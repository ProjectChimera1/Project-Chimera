#nullable enable
using Godot;
using ProjectChimera.Core;
using ProjectChimera.Economy;

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

        // ── Building card UI nodes ─────────────────────────────────────────────

        private Panel  _panel              = null!;
        private Label  _titleLabel         = null!;
        private Label  _hpLabel            = null!;
        private Label  _supplyLabel        = null!;  // CommandCenter only
        private Button _trainBtn           = null!;  // Production buildings
        private Label  _trainStatus        = null!;  // "Training…  Xs" label
        private Label  _constructionLabel  = null!;  // "Under Construction  Xs"

        // ── Worker card UI nodes ──────────────────────────────────────────────

        private Panel    _workerPanel       = null!;
        private Label    _workerTitleLabel  = null!;
        private Label    _workerHpLabel     = null!;
        private Label    _workerStatusLabel = null!;   // "Building…" or hint text
        private Button[] _buildBtns         = System.Array.Empty<Button>();

        /// <summary>Entity ID of the last worker whose card was refreshed. Used by button callbacks.</summary>
        private int _lastFocusedWorkerId = -1;

        private static readonly BuildingType[] WORKER_BUILD_TYPES =
        {
            BuildingType.CommandCenter,
            BuildingType.Barracks,
            BuildingType.ArcheryRange,
            BuildingType.SiegeWorkshop,
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

        public override void _Ready()
        {
            BuildPanel();
            BuildWorkerPanel();
        }

        // ── Per-frame ─────────────────────────────────────────────────────────

        public override void _Process(double delta)
        {
            if (GameState.Instance?.Mode != GameMode.Play)
            {
                _panel.Visible       = false;
                _workerPanel.Visible = false;
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

            _panel.Visible       = buildingSelected;
            _workerPanel.Visible = workerSelected;

            if (buildingSelected) RefreshCard(bId);
            if (workerSelected)   RefreshWorkerCard(focusId);
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
                _trainBtn.Visible          = false;
                _trainStatus.Visible       = false;
                _supplyLabel.Visible       = false;
                return;
            }

            _constructionLabel.Visible = false;

            bool isCC = bType == BuildingType.CommandCenter;
            bool canProduce = bType == BuildingType.Barracks
                           || bType == BuildingType.ArcheryRange
                           || bType == BuildingType.SiegeWorkshop;

            _supplyLabel.Visible = isCC;
            _trainBtn.Visible    = canProduce;
            _trainStatus.Visible = canProduce;

            if (isCC)
            {
                int used = _resources.SupplyUsed[(int)faction];
                int cap  = _resources.SupplyCap[(int)faction];
                _supplyLabel.Text = $"Supply: {used} / {cap}";
            }

            if (canProduce)
            {
                var unitDef      = _buildSys.GetProductionUnit(bType);
                string unitName  = unitDef?.DisplayName ?? "Unit";
                int    costOre   = unitDef?.CostOre  ?? 100;
                float  trainTime = unitDef?.TrainTime ?? 8f;
                byte   supply    = (byte)(unitDef?.Supply ?? 1);

                bool isTraining = _buildings.ProductionQueue[bId] != 0;

                if (isTraining)
                {
                    float remaining  = _buildings.ProductionTimer[bId].ToFloat();
                    _trainStatus.Text     = $"Training…  {remaining:F1}s";
                    _trainBtn.Disabled    = true;
                    _trainBtn.Text        = $"Train {unitName}\n{costOre} ore  ·  {trainTime:F0}s";
                }
                else
                {
                    _trainStatus.Text = string.Empty;
                    var   costFixed   = Fixed.FromFloat(costOre);
                    bool  canAfford   = _resources.CanAffordOre(faction, costFixed);
                    bool  hasSupply   = _resources.HasSupply(faction, supply);
                    string? missingPrereq = _buildSys.GetUnmetPrereq(bId);
                    bool  prereqsMet  = missingPrereq == null;

                    _trainBtn.Disabled = !prereqsMet || !canAfford || !hasSupply;
                    string note = !prereqsMet ? $"\n[need: {missingPrereq}]"
                                : !canAfford  ? "\n[need ore]"
                                : !hasSupply  ? "\n[supply full]"
                                : $"\n{costOre} ore  ·  {trainTime:F0}s";
                    _trainBtn.Text = $"Train {unitName}{note}";
                }
            }
        }

        // ── Button callbacks ──────────────────────────────────────────────────

        private void OnTrainBtnPressed()
        {
            int bId = _selection.SelectedBuildingId;
            if (bId < 0) return;
            _buildSys.TrainUnit(bId, _resources);
        }

        // ── Panel construction ────────────────────────────────────────────────

        private void BuildPanel()
        {
            var canvas = new CanvasLayer();
            AddChild(canvas);

            var vpSize = GetViewport().GetVisibleRect().Size;

            // ── Outer panel ───────────────────────────────────────────────────
            _panel = new Panel();
            _panel.Size     = new Vector2(420f, 140f);
            _panel.Position = new Vector2(10f, vpSize.Y - 150f);
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

            // ── Train button (Barracks / ArcheryRange / SiegeWorkshop) ────────
            _trainBtn = new Button();
            _trainBtn.Position = new Vector2(10f, 52f);
            _trainBtn.Size     = new Vector2(200f, 58f);
            _trainBtn.Text     = "Train Unit";
            _trainBtn.Visible  = false;
            _trainBtn.Pressed += OnTrainBtnPressed;
            _panel.AddChild(_trainBtn);

            // ── Training status (beside button) ───────────────────────────────
            _trainStatus = MakeLabel(new Vector2(220f, 72f), 13,
                                    new Color(0.95f, 0.75f, 0.20f));
            _trainStatus.Visible = false;
            _panel.AddChild(_trainStatus);
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
            _workerPanel.Size     = new Vector2(420f, 175f);
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
            float maxHp = _world.MaxHealth[focusId].ToFloat();

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

        // ── Shared helpers ────────────────────────────────────────────────────

        private static string BuildingTypeName(BuildingType t) => t switch
        {
            BuildingType.CommandCenter => "Command Center",
            BuildingType.Barracks      => "Barracks",
            BuildingType.ArcheryRange  => "Archery Range",
            BuildingType.SiegeWorkshop => "Siege Workshop",
            _ => "Building"
        };
    }
}
