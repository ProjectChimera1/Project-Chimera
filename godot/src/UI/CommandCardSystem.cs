#nullable enable
using System.Collections.Generic;
using System.Linq;
using Godot;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Combat;       // ItemSystem (Story 3.16 shop/inventory affordances)
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

        // Story 9.5: the local player's faction, late-bound. Defaults to Player1 so every offline/single-player path and
        // any un-wired instance stay byte-identical to today; CameraPhase wires it to _ctx.Lockstep?.EffectiveLocalFaction
        // (offline/spectator clamps to Player1) so a client assigned Player2..Player8 acts on its OWN buildings/units/hero.
        // Presentation-only — no sim fold.
        private System.Func<Faction> _localFaction = () => Faction.Player1;

        /// <summary>Story 9.5: inject the live local-faction getter (see <see cref="CameraPhase"/>).</summary>
        public void SetLocalFaction(System.Func<Faction> getter) => _localFaction = getter;

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
        // Story 2.8 review: log the "category exceeds MAX_TRAIN_OPTIONS" creator warning once per (faction, building
        // type), not every RefreshCard frame. Presentation-only — never read by the sim, so a HashSet is fine here.
        private readonly System.Collections.Generic.HashSet<(Faction, BuildingType)> _trainCapWarned = new();
        // Story 2.8 review (AC4): shared font with OpenType tabular figures so the numeric columns and the ticking
        // countdown use fixed-width digits and don't jitter. Built in BuildPanel, applied to the train buttons +
        // status label. FontVariation with no BaseFont derives from the default project font (documented fallback).
        private FontVariation _tabularFont       = null!;
        // Story 11.6 (FR-74): the depth-5 production queue strip — one clickable slot button per queued order (head =
        // slot 0 with live progress, waiting slots 1-4 as unit chips). Replaces the single "Training… Xs" label; clicking
        // a slot issues CancelTrain for that slot index. Hidden when the selection is not a ready producer.
        private const int MAX_QUEUE_SLOTS = BuildingStore.QUEUE_DEPTH;
        private Button[] _queueBtns        = System.Array.Empty<Button>();
        private Label  _constructionLabel  = null!;  // "Under Construction  Xs"

        // ── Hero revival (Story 3.14) ──────────────────────────────────────────
        // Injected via SetReviveDeps (like SetLockstep). Null until wired → the revive affordance is inert.
        private HeroStore?          _heroes;
        private RevivalRuleRuntime? _revival;
        // A revive-button grid overlaying the (unused-for-a-revive-building) train grid area. One button per awaiting
        // Player1 hero, mapped per-refresh to its HeroStore slot (the captured-loop-var lambda carries the BUTTON slot).
        private Button[] _reviveBtns       = System.Array.Empty<Button>();
        private readonly int[] _reviveHeroSlots = new int[MAX_TRAIN_OPTIONS]; // button slot → HeroStore slot it revives (-1 = empty)

        // ── Item shops + inventory (Story 3.16) ────────────────────────────────
        // Injected via SetShopDeps (like SetReviveDeps). Null until wired → the shop/inventory affordances stay inert.
        private ItemSystem?   _itemSys;
        private ItemStore?    _items;
        private ItemRegistry  _itemRegistry = ItemRegistry.Empty;
        // Shop Buy grid — one button per shop-stock item, overlaying the train grid (a shop building is not a producer).
        private Button[] _shopBtns          = System.Array.Empty<Button>();
        private readonly int[] _shopStockIndices = new int[MAX_TRAIN_OPTIONS]; // button slot → ShopStock index (-1 = empty)
        // Inventory panel (a focused P1 hero): a 6-slot grid with per-slot Use + Drop.
        private const int INV_SLOTS = HeroStore.INVENTORY_SLOTS;
        private Panel    _inventoryPanel    = null!;
        private Label    _inventoryTitle    = null!;
        private Button[] _invUseBtns        = System.Array.Empty<Button>();
        private Button[] _invDropBtns       = System.Array.Empty<Button>();
        private int      _lastFocusedHeroId = -1; // entity id whose inventory the grid last rendered (for callbacks)

        // ── Research (Story 4.11) ────────────────────────────────────────────────
        // Injected via SetResearchDeps (like SetReviveDeps/SetShopDeps). Null until wired → the research affordance
        // stays inert (no buttons). `_research` (a ResearchSystem) is the offline OrderApplier.Apply(..., research:)
        // apply site — mirrors `_itemSys`; `_researchStore` is the read-only per-faction state the dim predicate and
        // in-progress status text read — mirrors `_items`.
        private ResearchSystem? _research;
        private ResearchStore?  _researchStore;

        /// <summary>Story 11.4 (FR-74): the presentation combat-event sink, so an OFFLINE Train/Buy rejection surfaces a
        /// guard-sourced OrderDenied cue (online routes through LockstepManager, which already threads its own queue).
        /// Injected via <see cref="SetCombatEvents"/>; null until wired → offline denials stay silent (no crash).</summary>
        private CombatEventQueue? _combatEvents;
        /// <summary>Shared empty cost map for a maxed/in-progress/prereq-locked slot (never mutated) — mirrors
        /// <see cref="ResearchSystem.EmptyCost"/>'s own private field, kept here since this file has no access to it.</summary>
        private static readonly Dictionary<string, int> EmptyResearchCost = new();
        private static readonly List<ResearchLevel> EmptyResearchLevels = new();
        // A research-picker grid overlaying the (unused-for-a-non-producer) train grid area, beside the revive/shop
        // grids. One button per the selected building's BuildingDefinition.AvailableResearch entry, mapped per-refresh
        // to its FactionDefinition.Research index (the captured-loop-var lambda carries the BUTTON slot).
        private Button[] _researchBtns     = System.Array.Empty<Button>();
        private readonly int[] _researchIndices = new int[MAX_TRAIN_OPTIONS]; // button slot → Research list index (-1 = empty)
        private Label   _researchStatus    = null!;  // "{DisplayName}  Lv{level}  {s}s" in-flight label (mirrors _trainStatus)
        private Button  _researchCancelBtn = null!;  // Cancel the faction's in-progress research order

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

        /// <summary>Cached ability-panel positions (Story 2.9b). Normal = the shared HUD slot (a standalone combat
        /// caster keeps this). Stacked = raised one worker-card height + an 8px gap so the ability card sits ABOVE the
        /// co-displayed worker (build) card. Computed ONCE in <see cref="BuildAbilityPanel"/>, not per-frame.</summary>
        // Story 11.7 (AC-5): the ability panel is now anchored to the bottom-left corner (reflows on resize / UI-
        // scale); its normal/stacked toggle is expressed as two OffsetTop values from that bottom anchor (height is a
        // fixed 175, so OffsetBottom = top + 175). Both are negative (above the bottom edge).
        private float _abilityPanelNormalTop;
        private float _abilityPanelStackedTop;

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

        /// <summary>
        /// Inject the hero substrate + resolved revival rule (Story 3.14) so a <c>revives_heroes</c> building's card can
        /// enumerate awaiting heroes and price a revive. A setter (like <see cref="SetLockstep"/>) — wired by CameraPhase
        /// off the host. Null until wired → the revive affordance stays inert (no buttons).
        /// </summary>
        public void SetReviveDeps(HeroStore heroes, RevivalRuleRuntime revival)
        {
            _heroes  = heroes;
            _revival = revival;
        }

        /// <summary>Story 3.16: inject the item system + store + registry so a <c>sells_items</c> building's card can list
        /// stock + issue Buy, and a focused hero's card can render its inventory grid. A setter (like <see cref="SetReviveDeps"/>);
        /// wired by CameraPhase off the host. Null until wired → the shop/inventory affordances stay inert.</summary>
        public void SetShopDeps(ItemSystem itemSys, ItemStore items, ItemRegistry registry)
        {
            _itemSys      = itemSys;
            _items        = items;
            _itemRegistry = registry ?? ItemRegistry.Empty;
        }

        /// <summary>Story 4.11: inject the research runtime + its mid-match-mutable state so a research-offering
        /// building's card can list <c>AvailableResearch</c> options, dim them exactly as the sim would refuse, show
        /// in-progress countdown, and dispatch Start/Cancel. A setter (like <see cref="SetShopDeps"/>); wired by
        /// CameraPhase off the host. Null until wired → the research affordance stays inert (no buttons).</summary>
        public void SetResearchDeps(ResearchSystem researchSys, ResearchStore researchStore)
        {
            _research      = researchSys;
            _researchStore = researchStore;
        }

        /// <summary>Story 11.4 (FR-74): inject the presentation combat-event sink so an OFFLINE Train/Buy rejection
        /// surfaces a guard-sourced OrderDenied cue. A setter (like <see cref="SetResearchDeps"/>); wired off the
        /// scene context. Null until wired → offline denials stay silent.</summary>
        public void SetCombatEvents(CombatEventQueue events) => _combatEvents = events;

        public override void _Ready()
        {
            BuildPanel();
            BuildWorkerPanel();
            BuildAbilityPanel();
            BuildInventoryPanel();
        }

        // ── Per-frame ─────────────────────────────────────────────────────────

        public override void _Process(double delta)
        {
            if (GameState.Instance?.Mode != GameMode.Play)
            {
                _panel.Visible          = false;
                _workerPanel.Visible    = false;
                _abilityPanel.Visible   = false;
                _inventoryPanel.Visible = false;
                return;
            }

            int bId     = _selection.SelectedBuildingId;
            int focusId = _selection.FocusId;

            bool buildingSelected = bId >= 0 && bId < _buildings.Count && _buildings.Alive[bId];

            // Story 9.5: hoist the local-faction read once per UpdatePanels (invariant across this frame), matching
            // SelectionSystem/MinimapBridge — one consistent read instead of three separate delegate calls below.
            Faction me = _localFaction();

            // A worker is focused when no building is selected and the focused unit
            // belongs to the local faction and has a non-Inactive gather state.
            bool workerSelected = !buildingSelected
                && _world != null
                && focusId >= 0
                && _world.IsAlive(focusId)
                && _world.FactionOf[focusId] == me
                && _world.GatherState[focusId] != GatherState.Inactive;

            // Story 2.9b (AC1.1): a focused local-faction unit with ≥1 resolved ability shows the ability card — INCLUDING a
            // worker. Decision C is reversed now that worker-cast ships: the old `&& !workerSelected` term (which
            // suppressed a worker's ability card in favour of its build card) is dropped, so a worker that is BOTH a
            // gatherer and ability-bearing shows the ability card TOGETHER WITH the worker card (stacked, not
            // overlapping — see the reposition below). Reads the per-entity AbilityCount SoA directly (set by ApplyUnitDefinition).
            bool abilitySelected = !buildingSelected
                && _world != null
                && focusId >= 0
                && _world.IsAlive(focusId)
                && _world.FactionOf[focusId] == me
                && _world.AbilityCount[focusId] > 0;

            _panel.Visible        = buildingSelected;
            _workerPanel.Visible  = workerSelected;
            // Story 2.9b (AC1.1): when co-displayed with the worker card, stack the ability card ABOVE it (non-
            // overlapping); a standalone combat caster keeps it at the normal HUD slot. Repositioned only while
            // visible, right before the visibility flip.
            if (abilitySelected)
            {
                // Story 11.7 (AC-5): the panel is bottom-left-anchored, so the stacked/normal swap sets the two
                // vertical offsets from the bottom anchor rather than an absolute Position (height fixed at 175).
                float top = workerSelected ? _abilityPanelStackedTop : _abilityPanelNormalTop;
                _abilityPanel.OffsetTop    = top;
                _abilityPanel.OffsetBottom = top + 175f;
            }
            _abilityPanel.Visible = abilitySelected;

            // Story 3.16: a focused local-faction HERO shows its inventory grid (independent of the ability card — a hero often has both).
            bool inventorySelected = !buildingSelected
                && _world != null
                && _items != null
                && _heroes != null
                && focusId >= 0
                && _world.IsAlive(focusId)
                && _world.FactionOf[focusId] == me
                && _world.HeroIndex[focusId] != EntityWorld.HERO_NONE;
            _inventoryPanel.Visible = inventorySelected;

            if (buildingSelected) RefreshCard(bId);
            if (workerSelected)   RefreshWorkerCard(focusId);
            if (abilitySelected)  RefreshAbilityCard(focusId);
            if (inventorySelected) RefreshInventoryCard(focusId);
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
                HideReviveButtons();       // Story 3.14: also clear any revive buttons left over from a prior selection
                HideShopButtons();         // Story 3.16: clear any shop buttons left over from a prior selection
                HideResearchButtons();     // Story 4.11: clear any research buttons left over from a prior selection
                HideQueueStrip();          // Story 11.6: clear any queue-slot buttons left over from a prior selection
                _supplyLabel.Visible       = false;
                return;
            }

            _constructionLabel.Visible = false;

            bool isCC = bType == BuildingType.CommandCenter;
            // The single active producer surface (this story): the sim resolves ONE of Train/Research/Shop/Revive/None
            // (authored command_card_producer authoritative, else derived) so exactly one grid renders and the others
            // hide — no overlap (DW-90), and a dual-capability building gets its author-chosen affordance (DW-31).
            CommandCardSurface surface = _buildSys.ResolveCommandCardSurface(bId);

            _supplyLabel.Visible = isCC;

            if (isCC)
            {
                int used = _resources.SupplyUsed[(int)faction];
                int cap  = _resources.SupplyCap[(int)faction];
                _supplyLabel.Text = $"Supply: {used} / {cap}";
            }

            if (surface == CommandCardSurface.Train)
            {
                // One button per unit of THIS building's category, for its ACTUAL faction (not the P1 default) — so a
                // selected P2 producer shows P2's roster. Order follows the faction Units JSON. Pass the placed slot's
                // DefinitionId so a Custom producer lists its authored produces_category roster, not Melee (DW-168).
                var options = _buildSys.GetProductionUnits(bType, faction, _buildings.DefinitionId[bId]);
                // Story 2.8 review (creator-content guard): the picker renders a fixed MAX_TRAIN_OPTIONS-slot grid, so
                // a category defining more units than that silently loses the extras. Warn once per (faction, building
                // type) so a creator is told rather than losing a unit invisibly. Presentation-only, no sim impact.
                if (options.Count > _trainBtns.Length && _trainCapWarned.Add((faction, bType)))
                    GD.PrintErr($"[CommandCard] {faction} {bType} defines {options.Count} trainable units but the " +
                                $"production picker shows only {_trainBtns.Length}; units beyond the first " +
                                $"{_trainBtns.Length} are not reachable via the command card " +
                                $"(raise MAX_TRAIN_OPTIONS or split the category).");
                // Story 11.6: the queue is full when all QUEUE_DEPTH slots are occupied (no free append slot). The
                // picker's per-button disable predicate swaps 2.8's "already training" for "queue full (5)".
                bool queueFull = _buildings.FirstEmptySlot(bId) < 0;
                RefreshQueueStrip(bId, faction); // depth-5 strip: head progress + waiting chips, click to cancel

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

                    // Story 4.3: the resolved sparse cost map (legacy cost_ore/cost_crystal derivation when no
                    // authored `cost` — ore-then-crystal insertion order, matching today's text byte-for-byte).
                    var   cost        = def.ResolvedCost;
                    int   costOre     = cost.TryGetValue("ore", out int o) ? o : 0;
                    int   costCrystal = cost.TryGetValue("crystal", out int c) ? c : 0;   // Story 2.9b (AC2.2)
                    float trainTime   = def.TrainTime;
                    byte  supply      = (byte)def.Supply;

                    bool    canAfford     = _resources.CanAffordOre(faction, Fixed.FromInt(costOre));
                    // Story 2.9b (AC2.2): crystal-affordability preview — IDENTICAL to TrainUnit's sim check (which
                    // now spends via the same resolved cost map, quantized the same way — Fixed.FromInt), so the
                    // greyed-out button never diverges from what the sim would refuse.
                    bool    crystalOk     = _resources.CanAffordCrystal(faction, Fixed.FromInt(costCrystal));
                    bool    hasSupply     = _resources.HasSupply(faction, supply);
                    string? missingPrereq = _buildSys.GetUnmetPrereq(bId, unitIndex); // per-candidate prereq
                    bool    prereqsMet    = missingPrereq == null;

                    // Same predicate TrainUnit uses (prereq → supply → ore → crystal), plus the queue-full gate (Story
                    // 11.6: was 2.8's single-in-flight "already training"). The spend itself happens deterministically at
                    // exec-tick, not here (this grey-out is prediction only).
                    _trainBtns[i].Disabled = queueFull || !prereqsMet || !canAfford || !hasSupply || !crystalOk;
                    // Dim prereq-locked options (don't hide them) so the player sees what unlocks later.
                    _trainBtns[i].Modulate  = prereqsMet ? Colors.White : new Color(1f, 1f, 1f, 0.6f);

                    string costText = FormatCost(cost, emptyText: "(free)");
                    string note = !prereqsMet ? $"[need: {missingPrereq}]"
                                : !canAfford  ? "[need ore]"
                                : !crystalOk  ? "[need crystal]"
                                : !hasSupply  ? "[supply full]"
                                : $"{costText} · {trainTime:F0}s";
                    _trainBtns[i].Text        = $"{def.DisplayName}\n{note}";
                    // Story 2.9b (review patch): surface cost in the tooltip too, mirroring the button-face text,
                    // so a multi-resource unit's hover text matches what the sim charges.
                    _trainBtns[i].TooltipText = $"{def.DisplayName} — {costText}, {trainTime:F0}s train"; // NFR-2
                    _trainBtns[i].Visible     = true;
                }
            }
            else
            {
                HideTrainButtons();
                HideQueueStrip();
            }

            // ── Hero revival (Story 3.14): the revive grid renders ONLY when the resolved surface is Revive and the
            //    building actually revives + deps are wired (DW-31: an author can select this on a dual-capability
            //    building via command_card_producer:"revive"). Single-grid: any other surface hides it. ──
            if (surface == CommandCardSurface.Revive && _buildings.RevivesHeroes[bId] && _heroes != null && _revival != null)
                RefreshReviveButtons(bId, faction);
            else
                HideReviveButtons();

            // ── Item shop (Story 3.16): the Buy grid renders ONLY when the resolved surface is Shop and the building
            //    sells + deps are wired. Single-grid: any other surface hides it. ──
            if (surface == CommandCardSurface.Shop && _buildings.SellsItems[bId] && _items != null && _itemSys != null)
                RefreshShopButtons(bId, faction);
            else
                HideShopButtons();

            // ── Research (Story 4.11): the research grid renders ONLY when the resolved surface is Research and the
            //    deps are wired. Single-grid: any other surface hides it (RefreshResearchButtons still self-hides when
            //    the building's AvailableResearch is empty). ──
            if (surface == CommandCardSurface.Research && _research != null && _researchStore != null)
                RefreshResearchButtons(bId, faction);
            else
                HideResearchButtons();
        }

        /// <summary>Populate the research picker: one button per the selected building's
        /// <c>BuildingDefinition.AvailableResearch</c> entry, resolved against <c>FactionDefinition.Research</c>/
        /// <c>IndexOfResearch</c>. The dim predicate re-derives, read-only, the SAME ordered gates
        /// <see cref="ResearchSystem.StartResearchCommand"/> checks (already in progress → maxed → prerequisite →
        /// affordability) so a greyed-out button never diverges from what the sim would refuse (the 2.4b pattern
        /// <see cref="RefreshAbilityCard"/> already follows). Faction-wide: exactly one order in progress at a time,
        /// so ANY in-progress order (not just this building's own research) dims every option button here — the
        /// SEPARATE <see cref="_researchCancelBtn"/> is the only affordance that stays live during that state.</summary>
        private void RefreshResearchButtons(int bId, Faction faction)
        {
            FactionDefinition? fdef = _research!.GetFactionDefinition(faction);
            BuildingDefinition? bdef = fdef?.GetBuilding(_buildings.DefinitionId[bId]);
            string[] offered = bdef?.AvailableResearch ?? System.Array.Empty<string>();
            if (fdef == null || offered.Length == 0)
            {
                HideResearchButtons();
                return;
            }

            ResearchStore store = _researchStore!;
            int f = (int)faction;
            int inProgressIdx = (f >= 0 && f < store.InProgressIndex.Length) ? store.InProgressIndex[f] : -1;
            bool anyInProgress = inProgressIdx >= 0;

            if (anyInProgress && inProgressIdx < ResearchCount(fdef))
            {
                ResearchDefinition activeDef = fdef.Research[inProgressIdx];
                int activeLevel = CompletedLevelsOf(store, f, inProgressIdx) + 1;
                float remaining = store.RemainingTicks[f] / 30f;
                _researchStatus.Text     = $"{activeDef.DisplayName}  Lv{activeLevel}  {remaining:F1}s";
                _researchStatus.Visible  = true;
                _researchCancelBtn.Visible = true;
            }
            else if (anyInProgress)
            {
                // Review-pass fix: a stale/out-of-range in-progress index (e.g. the faction's research list
                // shrank underneath an already-running order) must still expose the Cancel affordance — the
                // options grid below correctly stays disabled either way, but losing Cancel here would strand
                // the player with an order they can never clear from this card.
                _researchStatus.Text     = "[research in progress]";
                _researchStatus.Visible  = true;
                _researchCancelBtn.Visible = true;
            }
            else
            {
                _researchStatus.Visible    = false;
                _researchCancelBtn.Visible = false;
            }

            int shown = 0;
            for (int i = 0; i < offered.Length && shown < _researchBtns.Length; i++)
            {
                int ri = fdef.IndexOfResearch(offered[i]);
                if (ri < 0) continue; // dangling available_research id — validator should have caught it; defensive skip

                ResearchDefinition rdef = fdef.Research[ri];
                Button btn = _researchBtns[shown];
                _researchIndices[shown] = ri;

                // Review-pass fix: Levels is a non-nullable-typed property that malformed hand-edited JSON
                // ("levels": null) can still leave null at runtime (same class ResearchValidator.Validate already
                // guards against) — never NRE the command card's refresh over it.
                List<ResearchLevel> levels = rdef.Levels ?? EmptyResearchLevels;

                int completedLevels = CompletedLevelsOf(store, f, ri);
                bool maxed = completedLevels >= levels.Count;
                string? missingPrereq = (!anyInProgress && !maxed) ? FirstUnmetResearchPrereq(fdef, faction, f, rdef.Prerequisites) : null;
                bool prereqsMet = missingPrereq == null;

                IReadOnlyDictionary<string, int> cost = (!maxed && completedLevels < levels.Count && levels[completedLevels] != null)
                    ? (IReadOnlyDictionary<string, int>)(levels[completedLevels].Cost ?? EmptyResearchCost)
                    : EmptyResearchCost;
                bool canAfford = (anyInProgress || maxed || !prereqsMet) || _resources.CanAfford(faction, cost);

                bool enabled = !anyInProgress && !maxed && prereqsMet && canAfford;
                btn.Disabled  = !enabled;
                // Locked-but-visible, not hidden — dim exactly like Train's prereq dimming.
                btn.Modulate  = enabled ? Colors.White : new Color(1f, 1f, 1f, 0.6f);

                int timeTicks = (!maxed && completedLevels < levels.Count && levels[completedLevels] != null)
                    ? levels[completedLevels].TimeTicks : 0;
                float timeSeconds = timeTicks / 30f;
                string costText = FormatCost(cost, emptyText: "(free)");

                string note = anyInProgress ? "[in progress]"
                            : maxed         ? "[maxed]"
                            : !prereqsMet   ? $"[need: {missingPrereq}]"
                            : !canAfford    ? $"[need {FirstUnaffordableResource(faction, cost) ?? "resources"}]"
                            : $"{costText} · {timeSeconds:F0}s";
                btn.Text        = $"{rdef.DisplayName}\n{note}";
                // Clamp the displayed "next level" to the ladder length so a maxed research reads "Lv2/2", not "Lv3/2".
                int shownLevel  = System.Math.Min(completedLevels + 1, levels.Count);
                btn.TooltipText = $"{rdef.DisplayName} — {costText}, {timeSeconds:F0}s research (Lv{shownLevel}/{levels.Count}).";
                btn.Visible     = true;
                shown++;
            }
            for (int i = shown; i < _researchBtns.Length; i++)
            {
                _researchBtns[i].Visible = false;
                _researchIndices[i]      = -1;
            }
        }

        /// <summary>Null-safe count of a faction's authored research list — mirrors
        /// <see cref="ResearchSystem"/>'s own private <c>ResearchCount</c> tolerance for an authored
        /// <c>"research": null</c> faction file.</summary>
        private static int ResearchCount(FactionDefinition fdef) => fdef.Research?.Count ?? 0;

        /// <summary>Read <c>ResearchStore.CompletedLevels[f][ri]</c> defensively — the store's inner per-faction
        /// array is grown lazily by <see cref="ResearchSystem.EnsureCapacity"/> calls this read-only card never
        /// triggers itself, so a not-yet-grown slot reads as 0 completed levels (never an index-out-of-range).</summary>
        private static int CompletedLevelsOf(ResearchStore store, int f, int researchIndex)
        {
            if (f < 0 || f >= store.CompletedLevels.Length) return 0;
            int[] levels = store.CompletedLevels[f];
            return (researchIndex >= 0 && researchIndex < levels.Length) ? levels[researchIndex] : 0;
        }

        /// <summary>The first unmet prerequisite id, or null if all are satisfied — re-derives, read-only,
        /// <see cref="ResearchSystem.PrerequisitesMet"/>'s EXACT resolution order (research id first — the more
        /// specific match — else a building id via <see cref="TechTreeChecker.AreMet"/>), so this button grid's
        /// grey-out never diverges from what the sim would refuse.</summary>
        private string? FirstUnmetResearchPrereq(FactionDefinition fdef, Faction faction, int f, string[]? prereqs)
        {
            if (prereqs == null || prereqs.Length == 0) return null;
            foreach (string id in prereqs)
            {
                int prereqResearchIdx = fdef.IndexOfResearch(id);
                if (prereqResearchIdx >= 0)
                {
                    if (CompletedLevelsOf(_researchStore!, f, prereqResearchIdx) <= 0) return id;
                }
                else if (!TechTreeChecker.AreMet(_buildings, faction, new[] { id }))
                {
                    return id;
                }
            }
            return null;
        }

        /// <summary>The first cost-map resource key the faction cannot afford, or null (defensive — <see cref="ResourceStore.CanAfford"/>
        /// already failed for the whole map when this is called). Named per-resource, mirroring the Train button's
        /// own "[need ore]"/"[need crystal]" note convention.</summary>
        private string? FirstUnaffordableResource(Faction faction, IReadOnlyDictionary<string, int> cost)
        {
            foreach (var (key, amount) in cost)
            {
                bool ok = key switch
                {
                    "ore"     => _resources.CanAffordOre(faction, Fixed.FromInt(amount)),
                    "crystal" => _resources.CanAffordCrystal(faction, Fixed.FromInt(amount)),
                    _         => false,
                };
                if (!ok) return key;
            }
            return null;
        }

        /// <summary>Hide every research-picker button + the in-progress status/cancel affordances (used when the
        /// selection can't research, offers none, or deps aren't wired).</summary>
        private void HideResearchButtons()
        {
            for (int i = 0; i < _researchBtns.Length; i++)
            {
                _researchBtns[i].Visible = false;
                _researchIndices[i]      = -1;
            }
            _researchStatus.Visible    = false;
            _researchCancelBtn.Visible = false;
        }

        /// <summary>Populate the shop Buy picker: one button per <c>ShopStock</c> item, priced from the item def, greyed
        /// when unaffordable or no owned hero is in <c>shop_radius</c>. Slots past the grid cap are dropped (UI-only).</summary>
        private void RefreshShopButtons(int bId, Faction faction)
        {
            string[] stock = _buildings.ShopStock[bId] ?? System.Array.Empty<string>();
            Fixed radius = _buildings.ShopRadius[bId];
            if (radius <= Fixed.Zero) radius = Fixed.FromInt(6);
            int buyer = FindNearestOwnedHero(_buildings.Position[bId], radius, faction);
            bool haveBuyer = buyer >= 0;
            int shown = 0;
            for (int i = 0; i < stock.Length && shown < _shopBtns.Length; i++)
            {
                int defIndex = _itemRegistry.IndexOf(stock[i]);
                if (defIndex < 0) continue; // dangling stock id — skip (validation should have caught it)
                var def = _itemRegistry.Get(defIndex);

                int oreDisp     = (int)def.CostOre.ToFloat();
                int crystalDisp = (int)def.CostCrystal.ToFloat();
                bool affordable = _resources.CanAffordOre(faction, def.CostOre)
                               && _resources.CanAffordCrystal(faction, def.CostCrystal);
                string costSuffix = crystalDisp > 0 ? $" · {crystalDisp} crystal" : "";
                string note = !haveBuyer  ? "[no hero in range]"
                            : !affordable ? "[need resources]"
                            : $"{oreDisp} ore{costSuffix}";

                Button btn = _shopBtns[shown];
                _shopStockIndices[shown] = i;
                string name = string.IsNullOrEmpty(def.DisplayName) ? def.Id : def.DisplayName;
                btn.Text        = $"{name}\n{note}";
                btn.TooltipText = $"Buy {name} for {oreDisp} ore{costSuffix} for a nearby hero.";
                btn.Disabled    = !haveBuyer || !affordable;
                btn.Visible     = true;
                shown++;
            }
            for (int i = shown; i < _shopBtns.Length; i++)
            {
                _shopBtns[i].Visible    = false;
                _shopStockIndices[i]    = -1;
            }
        }

        /// <summary>Hide every shop Buy button.</summary>
        private void HideShopButtons()
        {
            for (int i = 0; i < _shopBtns.Length; i++)
            {
                _shopBtns[i].Visible = false;
                _shopStockIndices[i] = -1;
            }
        }

        /// <summary>The nearest alive Player-owned HERO entity within <paramref name="radius"/> of <paramref name="pos"/>,
        /// or -1. Presentation-only (the sim re-checks proximity at exec-tick) — a linear scan of the entity high-water mark.</summary>
        private int FindNearestOwnedHero(FixedVec3 pos, Fixed radius, Faction faction)
        {
            long rr = ((long)radius.Raw * radius.Raw) >> 16;
            int best = -1; long bestSqr = long.MaxValue;
            int hwm = _world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
            {
                if (!_world.IsAlive(i)) continue;
                if (_world.FactionOf[i] != faction) continue;
                if (_world.HeroIndex[i] == EntityWorld.HERO_NONE) continue;
                long dxr = (long)_world.Position[i].X.Raw - pos.X.Raw;
                long dzr = (long)_world.Position[i].Z.Raw - pos.Z.Raw;
                long sqr = ((dxr * dxr) >> 16) + ((dzr * dzr) >> 16);
                if (sqr <= rr && sqr < bestSqr) { bestSqr = sqr; best = i; }
            }
            return best;
        }

        /// <summary>Populate the revive-picker: one button per awaiting hero owned by <paramref name="faction"/>, priced
        /// at the level-scaled cost, greyed when unaffordable. Slots past <see cref="MAX_TRAIN_OPTIONS"/> are dropped
        /// (deterministic UI cap; the sim is unaffected).</summary>
        private void RefreshReviveButtons(int bId, Faction faction)
        {
            HeroStore heroes = _heroes!;
            RevivalRuleRuntime revival = _revival!;
            int shown = 0;
            for (int slot = 0; slot < heroes.Count && shown < _reviveBtns.Length; slot++)
            {
                if (!heroes.Alive[slot] || !heroes.AwaitingRevival[slot]) continue;
                if (heroes.OwnerFaction[slot] != faction) continue;

                int level = heroes.Level[slot];
                Fixed costOre     = revival.CostOre(level);
                Fixed costCrystal = revival.CostCrystal(level);
                bool counting = heroes.RevivalLink[slot] != HeroStore.REVIVAL_NONE;
                bool affordable = _resources.CanAffordOre(faction, costOre)
                               && _resources.CanAffordCrystal(faction, costCrystal);

                int oreDisp = (int)costOre.ToFloat();
                int crystalDisp = (int)costCrystal.ToFloat();
                string costSuffix = crystalDisp > 0 ? $" · {crystalDisp} crystal" : "";
                string note = counting   ? $"reviving… {heroes.RevivalTimer[slot].ToFloat():F1}s"
                            : !affordable ? "[need ore]"
                            : $"{oreDisp} ore{costSuffix}";

                Button btn = _reviveBtns[shown];
                _reviveHeroSlots[shown] = slot;
                btn.Text        = $"Revive Lv{level}\n{note}";
                btn.TooltipText = $"Revive this fallen hero here for {oreDisp} ore{costSuffix} after a countdown.";
                btn.Disabled    = counting || !affordable; // a counting revival can't be re-ordered; unaffordable is blocked
                btn.Visible     = true;
                shown++;
            }
            for (int i = shown; i < _reviveBtns.Length; i++)
            {
                _reviveBtns[i].Visible = false;
                _reviveHeroSlots[i]    = -1;
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
        /// replay/online paths use (structural parity). Only the LOCAL player's own building trains (the local faction,
        /// resolved late-bound via <see cref="_localFaction"/>), mirroring SelectionSystem's cast/selection convention.
        /// </summary>
        private void IssueTrainCommand(int bId, int chosenUnitIndex)
        {
            if (bId < 0 || bId >= _buildings.Count) return;
            if (!_buildings.Alive[bId] || _buildings.FactionOf[bId] != _localFaction()) return;
            // Online: EnqueueOrder returns false (queued). Offline (_lockstep == null): the ?? true yields apply-now.
            bool applyNow = _lockstep?.EnqueueOrder(bId, UnitCommand.Train,
                                                    Fixed.FromRaw(chosenUnitIndex), Fixed.Zero) ?? true;
            if (!applyNow) return; // online: LockstepManager.Flush applies it at exec-tick (spend happens THERE, once)
            var order = new UnitOrder(bId, UnitCommand.Train, Fixed.FromRaw(chosenUnitIndex), Fixed.Zero);
            OrderApplier.Apply(_world, in order, _localFaction(), buildings: _buildSys, events: _combatEvents); // Story 11.4: offline denial cue
        }

        // ── Production queue strip (Story 11.6, FR-74) ────────────────────────

        /// <summary>Render the depth-5 queue: the head (slot 0) as "name  Xs" with live countdown, waiting slots as
        /// unit-name chips, empty slots hidden. Reads the folded <see cref="BuildingStore.ProductionQueue"/>/
        /// <see cref="BuildingStore.ProductionTimer"/> directly (presentation-only). Names resolve from the building's
        /// category roster; a fallback-sentinel slot shows a generic label.</summary>
        private void RefreshQueueStrip(int bId, Faction faction)
        {
            // Pass the placed slot's DefinitionId so a Custom producer's queued chips resolve their authored-category
            // roster names, not Melee (DW-168) — matching the train grid above.
            var options = _buildSys.GetProductionUnits(_buildings.Type[bId], faction, _buildings.DefinitionId[bId]); // (Units index, def) of this category
            int head = _buildings.HeadIndex(bId);
            for (int k = 0; k < _queueBtns.Length; k++)
            {
                byte q = _buildings.ProductionQueue[head + k];
                if (q == 0)
                {
                    _queueBtns[k].Visible = false;
                    continue;
                }
                string name = QueueSlotName(options, q);
                _queueBtns[k].Text = k == 0
                    // head: unit + live countdown. Ceiling (not :F0 nearest-rounding) so a head still in production
                    // never displays "0s" — it counts 8→7→…→1 and only the completion tick removes it from the head.
                    ? $"{name}  {(int)System.Math.Ceiling(_buildings.ProductionTimer[bId].ToFloat())}s"
                    : name;                                                     // waiting: just the unit
                _queueBtns[k].TooltipText = k == 0
                    ? $"{name} — in production. Click to cancel (full refund; progress lost)."
                    : $"{name} — queued. Click to cancel (full refund).";
                _queueBtns[k].Visible = true;
            }
        }

        /// <summary>Resolve a queued slot's encoded byte (<c>unitIndex+1</c>, or 255 = empty-category fallback) to a
        /// display name via the building's category roster. Presentation-only; a sentinel / unresolved index reads "Unit".</summary>
        private static string QueueSlotName(System.Collections.Generic.List<(int Index, UnitDefinition Def)> options, byte q)
        {
            if (q == byte.MaxValue) return "Unit"; // PRODUCTION_FALLBACK sentinel (empty-category producer)
            int idx = q - 1;
            foreach (var (index, def) in options)
                if (index == idx)
                    return string.IsNullOrEmpty(def.DisplayName) ? def.Id : def.DisplayName;
            return "Unit";
        }

        /// <summary>Hide every queue-strip slot button (used when the selection is not a ready producer).</summary>
        private void HideQueueStrip()
        {
            for (int i = 0; i < _queueBtns.Length; i++)
                _queueBtns[i].Visible = false;
        }

        private void OnQueueSlotPressed(int slot)
        {
            if (slot < 0 || slot >= _queueBtns.Length) return;
            IssueCancelTrainCommand(_selection.SelectedBuildingId, slot);
        }

        /// <summary>
        /// Issue a CancelTrain command for queue <paramref name="slot"/> at building <paramref name="bId"/>
        /// (Story 11.6). Mirrors <see cref="IssueTrainCommand"/> exactly: online it is ENQUEUED (the deterministic
        /// exec-tick refund happens once, THERE); offline it applies immediately via the SAME OrderApplier the
        /// replay/online paths use. Only the LOCAL player's own building cancels. WIRE: TargetX = slot index (raw int).
        /// </summary>
        private void IssueCancelTrainCommand(int bId, int slot)
        {
            if (bId < 0 || bId >= _buildings.Count) return;
            if (!_buildings.Alive[bId] || _buildings.FactionOf[bId] != _localFaction()) return;
            bool applyNow = _lockstep?.EnqueueOrder(bId, UnitCommand.CancelTrain,
                                                    Fixed.FromRaw(slot), Fixed.Zero) ?? true;
            if (!applyNow) return; // online: LockstepManager.Flush applies it at exec-tick (refund happens THERE, once)
            var order = new UnitOrder(bId, UnitCommand.CancelTrain, Fixed.FromRaw(slot), Fixed.Zero);
            OrderApplier.Apply(_world, in order, _localFaction(), buildings: _buildSys, events: _combatEvents);
        }

        private void OnReviveSlotPressed(int slot)
        {
            if (slot < 0 || slot >= _reviveHeroSlots.Length) return;
            int heroSlot = _reviveHeroSlots[slot];
            if (heroSlot < 0) return;
            IssueReviveCommand(_selection.SelectedBuildingId, heroSlot);
        }

        /// <summary>
        /// Issue a ReviveHero command for the awaiting hero at <paramref name="heroSlot"/> from building
        /// <paramref name="bId"/> (Story 3.14, D-4). Mirrors <see cref="IssueTrainCommand"/>: online it is ENQUEUED
        /// (executed at exec-tick by LockstepManager, so the level-scaled spend happens once, THERE); offline it applies
        /// immediately via the SAME OrderApplier the replay/online paths use (structural parity). Only the LOCAL player's
        /// own building (the local faction) revives its own hero.
        /// </summary>
        private void IssueReviveCommand(int bId, int heroSlot)
        {
            if (bId < 0 || bId >= _buildings.Count) return;
            if (!_buildings.Alive[bId] || _buildings.FactionOf[bId] != _localFaction()) return;
            bool applyNow = _lockstep?.EnqueueOrder(bId, UnitCommand.ReviveHero,
                                                    Fixed.FromRaw(heroSlot), Fixed.Zero) ?? true;
            if (!applyNow) return; // online: LockstepManager.Flush applies it at exec-tick (spend happens THERE, once)
            var order = new UnitOrder(bId, UnitCommand.ReviveHero, Fixed.FromRaw(heroSlot), Fixed.Zero);
            OrderApplier.Apply(_world, in order, _localFaction(), buildings: _buildSys);
        }

        private void OnShopSlotPressed(int slot)
        {
            if (slot < 0 || slot >= _shopStockIndices.Length) return;
            int stockIndex = _shopStockIndices[slot];
            if (stockIndex < 0) return;
            int bId = _selection.SelectedBuildingId;
            if (bId < 0 || bId >= _buildings.Count) return;
            Fixed radius = _buildings.ShopRadius[bId];
            if (radius <= Fixed.Zero) radius = Fixed.FromInt(6);
            int buyer = FindNearestOwnedHero(_buildings.Position[bId], radius, _localFaction());
            if (buyer < 0) return; // no owned hero in range → no buyer
            IssueBuyCommand(bId, stockIndex, buyer);
        }

        /// <summary>Issue a BuyItem command for <paramref name="stockIndex"/> at shop <paramref name="bId"/> for the hero
        /// entity <paramref name="heroEntity"/> (Story 3.16). Mirrors <see cref="IssueReviveCommand"/>: online ENQUEUED
        /// (spend + mint happen once at exec-tick); offline applied via the SAME OrderApplier the replay/online paths use,
        /// passing BOTH <c>buildings</c> and <c>items</c> so the offline mint fires. WIRE: TargetX = stock index (raw int),
        /// TargetZ = buying hero entity id (raw int). Only the LOCAL player's own shop (the local faction).</summary>
        private void IssueBuyCommand(int bId, int stockIndex, int heroEntity)
        {
            if (bId < 0 || bId >= _buildings.Count) return;
            if (!_buildings.Alive[bId] || _buildings.FactionOf[bId] != _localFaction()) return;
            bool applyNow = _lockstep?.EnqueueOrder(bId, UnitCommand.BuyItem,
                                                    Fixed.FromRaw(stockIndex), Fixed.FromRaw(heroEntity)) ?? true;
            if (!applyNow) return; // online: LockstepManager.Flush applies at exec-tick (spend + mint happen THERE, once)
            var order = new UnitOrder(bId, UnitCommand.BuyItem, Fixed.FromRaw(stockIndex), Fixed.FromRaw(heroEntity));
            OrderApplier.Apply(_world, in order, _localFaction(), buildings: _buildSys, items: _itemSys, events: _combatEvents); // Story 11.4: offline denial cue
        }

        private void OnResearchSlotPressed(int slot)
        {
            if (slot < 0 || slot >= _researchIndices.Length) return;
            int researchIndex = _researchIndices[slot];
            if (researchIndex < 0) return;
            IssueResearchCommand(_selection.SelectedBuildingId, researchIndex);
        }

        private void OnResearchCancelPressed()
        {
            IssueCancelResearchCommand(_selection.SelectedBuildingId);
        }

        /// <summary>
        /// Issue a StartResearch command for <paramref name="researchIndex"/> at building <paramref name="bId"/>
        /// (Story 4.11, D-1 lockstep seam parity). Mirrors <see cref="IssueTrainCommand"/> exactly: online it is
        /// ENQUEUED (the deterministic exec-tick spend + gate chain happens once, THERE — the picker's grey-out is
        /// only a local prediction); offline it applies immediately via the SAME OrderApplier the replay/online
        /// paths use, passing <c>research: _research</c> so the offline apply routes to
        /// <see cref="ResearchSystem.StartResearchCommand"/>. Only the LOCAL player's own building (the local faction).
        /// </summary>
        private void IssueResearchCommand(int bId, int researchIndex)
        {
            if (bId < 0 || bId >= _buildings.Count) return;
            if (!_buildings.Alive[bId] || _buildings.FactionOf[bId] != _localFaction()) return;
            bool applyNow = _lockstep?.EnqueueOrder(bId, UnitCommand.StartResearch,
                                                    Fixed.FromRaw(researchIndex), Fixed.Zero) ?? true;
            if (!applyNow) return; // online: LockstepManager.Flush applies it at exec-tick (spend happens THERE, once)
            var order = new UnitOrder(bId, UnitCommand.StartResearch, Fixed.FromRaw(researchIndex), Fixed.Zero);
            OrderApplier.Apply(_world, in order, _localFaction(), buildings: _buildSys, research: _research);
        }

        /// <summary>
        /// Issue a CancelResearch command from building <paramref name="bId"/> (Story 4.11). Mirrors
        /// <see cref="IssueResearchCommand"/> — research state is faction-wide, not building-scoped, so any owned
        /// building may cancel (mirrors <see cref="ResearchSystem.CancelResearchCommand"/>'s own ownership guard).
        /// </summary>
        private void IssueCancelResearchCommand(int bId)
        {
            if (bId < 0 || bId >= _buildings.Count) return;
            if (!_buildings.Alive[bId] || _buildings.FactionOf[bId] != _localFaction()) return;
            bool applyNow = _lockstep?.EnqueueOrder(bId, UnitCommand.CancelResearch,
                                                    Fixed.Zero, Fixed.Zero) ?? true;
            if (!applyNow) return; // online: LockstepManager.Flush applies it at exec-tick (refund happens THERE, once)
            var order = new UnitOrder(bId, UnitCommand.CancelResearch, Fixed.Zero, Fixed.Zero);
            OrderApplier.Apply(_world, in order, _localFaction(), buildings: _buildSys, research: _research);
        }

        // ── Inventory grid (Story 3.16) ───────────────────────────────────────

        /// <summary>Render the focused hero's 6-slot inventory grid: each filled slot resolves
        /// <c>Inventory[]</c>→<c>ItemStore</c>→<c>ItemRegistry</c> for name/charges; per-slot Use + Drop issue the sim
        /// command on that EXACT slot (not a hard-coded slot 0). Empty slots render blank + disabled.</summary>
        private void RefreshInventoryCard(int focusId)
        {
            _lastFocusedHeroId = focusId;
            if (_items == null || _heroes == null || !_heroes.TryResolveRef(_world.HeroIndex[focusId], out int heroSlot))
            {
                for (int s = 0; s < _invUseBtns.Length; s++) { _invUseBtns[s].Disabled = true; _invUseBtns[s].Text = "—"; _invDropBtns[s].Disabled = true; }
                return;
            }
            _inventoryTitle.Text = "Inventory";
            int baseIdx = heroSlot * INV_SLOTS;
            for (int s = 0; s < INV_SLOTS; s++)
            {
                int refPacked = _heroes.Inventory[baseIdx + s];
                if (refPacked == HeroStore.INVENTORY_EMPTY || !_items.TryResolveRef(refPacked, out int itemSlot))
                {
                    _invUseBtns[s].Text        = "—";
                    _invUseBtns[s].TooltipText  = "Empty slot";
                    _invUseBtns[s].Disabled     = true;
                    _invDropBtns[s].Disabled    = true;
                    continue;
                }
                ItemDefinition? def = _itemRegistry.TryGet(_items.DefId[itemSlot]);
                string name = def == null ? "?" : (string.IsNullOrEmpty(def.DisplayName) ? def.Id : def.DisplayName);
                int charges = _items.Charges[itemSlot];
                bool consumable = def?.EffectGraph != null && charges > 0;
                _invUseBtns[s].Text        = charges > 0 ? $"{name}\nx{charges}" : name;
                _invUseBtns[s].TooltipText  = def == null ? name
                    : consumable ? $"{name} — click to USE (charges: {charges})"
                                 : $"{name} — carried stat item (no use)";
                // Story 3.16 review: enable Use ONLY for a consumable (charged + effect graph). A stat item's Use is a sim
                // no-op, so enabling it flooded discarded lockstep orders — disable it; Drop stays enabled for every item.
                _invUseBtns[s].Disabled     = !consumable;
                _invDropBtns[s].Disabled    = false;
            }
        }

        private void OnInventoryUsePressed(int slot)
        {
            _selection.SetSelectedInventorySlot(slot);
            if (_lastFocusedHeroId >= 0) _selection.IssueUseItemCommand(_lastFocusedHeroId, slot);
        }

        private void OnInventoryDropPressed(int slot)
        {
            if (_lastFocusedHeroId >= 0) _selection.IssueDropItemCommand(_lastFocusedHeroId, slot);
        }

        // ── Panel construction ────────────────────────────────────────────────

        private void BuildPanel()
        {
            var canvas = new CanvasLayer();
            AddChild(canvas);

            // Story 2.8 review (AC4): tabular-figures font shared by the train grid + countdown so digits are
            // fixed-width and don't jitter. No BaseFont → derives from the default project font (documented
            // fallback), so text still renders even if the font lacks the feature.
            _tabularFont = new FontVariation
            {
                OpentypeFeatures = new Godot.Collections.Dictionary
                {
                    { TextServerManager.GetPrimaryInterface().NameToTag("tnum"), 1 },
                },
            };

            // ── Outer panel ───────────────────────────────────────────────────
            _panel = new Panel();
            // Story 2.8: taller (140 → 175) and raised (−150 → −185) to hold the per-unit train grid, matching the
            // worker/ability cards (which sit at −185 for their 175px height and share this HUD region).
            // Story 11.7 (AC-5): pinned to the bottom-left corner via anchors (not a cached viewport size), so Godot
            // reflows it on window resize and UI-scale change. Offsets reproduce the former 420×175 rect at (10, −185).
            _panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft);
            _panel.OffsetLeft   = 10f;
            _panel.OffsetTop    = -185f;
            _panel.OffsetRight  = 10f + 420f;
            _panel.OffsetBottom = -185f + 175f;
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
            // DW-917: the CommandCenter now renders a TRAIN grid (worker production), whose queue strip occupies
            // y 48-70 and whose picker buttons start at y 74 — so the supply readout moved off the old (10, 58) slot
            // it shared with _constructionLabel and onto the free right half of the title row.
            _supplyLabel = MakeLabel(new Vector2(270f, 10f), 13,
                                    new Color(0.75f, 0.80f, 1.00f));
            _supplyLabel.Visible = false;
            _panel.AddChild(_supplyLabel);

            // ── Production queue strip (Story 11.6) — a row of QUEUE_DEPTH clickable slot chips above the picker
            //    grid. Head (slot 0) shows the unit + live countdown; waiting slots show the unit name. Clicking a
            //    slot issues CancelTrain for that slot index. Fixed-width tabular font so the head countdown doesn't jitter.
            _queueBtns = new Button[MAX_QUEUE_SLOTS];
            for (int i = 0; i < MAX_QUEUE_SLOTS; i++)
            {
                var btn = new Button();
                btn.Position = new Vector2(10f + i * 82f, 48f);
                btn.Size     = new Vector2(78f, 22f);
                btn.Visible  = false;
                btn.ClipText = true; // a long unit name is clipped, never overflowing the compact chip
                btn.AddThemeFontOverride("font", _tabularFont); // AC4: fixed-width ticking countdown
                btn.AddThemeFontSizeOverride("font_size", 10);
                int slot = i; // capture per-iteration for the lambda (carries the SLOT index)
                btn.Pressed += () => OnQueueSlotPressed(slot);
                _panel.AddChild(btn);
                _queueBtns[i] = btn;
            }

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
                btn.AddThemeFontOverride("font", _tabularFont); // AC4: tabular figures so cost/train-time align
                int slot = i; // capture per-iteration for the lambda
                btn.Pressed += () => OnTrainSlotPressed(slot);
                _panel.AddChild(btn);
                _trainBtns[i] = btn;
                _trainUnitIndices[i] = -1;
            }

            // ── Revive buttons (Story 3.14) — overlay the train grid (a revive building is not a producer, so the two
            //    are never both visible). One per awaiting Player1 hero, mapped to its HeroStore slot per-refresh. ──
            _reviveBtns = new Button[MAX_TRAIN_OPTIONS];
            for (int i = 0; i < MAX_TRAIN_OPTIONS; i++)
            {
                var btn = new Button();
                btn.Position     = new Vector2(10f + i * 102f, 74f);
                btn.Size         = new Vector2(98f, 70f);
                btn.Visible      = false;
                btn.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                btn.AddThemeFontOverride("font", _tabularFont);
                int slot = i; // capture per-iteration for the lambda (carries the BUTTON slot, not the hero slot)
                btn.Pressed += () => OnReviveSlotPressed(slot);
                _panel.AddChild(btn);
                _reviveBtns[i] = btn;
                _reviveHeroSlots[i] = -1;
            }

            // ── Shop Buy buttons (Story 3.16) — overlay the train grid (a shop building is not a producer). One per
            //    stock item, mapped to its ShopStock index per-refresh (the captured lambda carries the BUTTON slot). ──
            _shopBtns = new Button[MAX_TRAIN_OPTIONS];
            for (int i = 0; i < MAX_TRAIN_OPTIONS; i++)
            {
                var btn = new Button();
                btn.Position     = new Vector2(10f + i * 102f, 74f);
                btn.Size         = new Vector2(98f, 70f);
                btn.Visible      = false;
                btn.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                btn.AddThemeFontOverride("font", _tabularFont);
                int slot = i;
                btn.Pressed += () => OnShopSlotPressed(slot);
                _panel.AddChild(btn);
                _shopBtns[i] = btn;
                _shopStockIndices[i] = -1;
            }

            // ── Research buttons (Story 4.11) — overlay the train grid (a research-offering building is not
            //    necessarily a producer). One per BuildingDefinition.AvailableResearch entry, mapped to its
            //    FactionDefinition.Research index per-refresh (the captured lambda carries the BUTTON slot). ──
            _researchBtns = new Button[MAX_TRAIN_OPTIONS];
            for (int i = 0; i < MAX_TRAIN_OPTIONS; i++)
            {
                var btn = new Button();
                btn.Position     = new Vector2(10f + i * 102f, 74f);
                btn.Size         = new Vector2(98f, 70f);
                btn.Visible      = false;
                btn.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                btn.AddThemeFontOverride("font", _tabularFont); // AC4-style: tabular figures so cost/time align
                int slot = i;
                btn.Pressed += () => OnResearchSlotPressed(slot);
                _panel.AddChild(btn);
                _researchBtns[i] = btn;
                _researchIndices[i] = -1;
            }

            // In-progress status text (mirrors _trainStatus) + a dedicated Cancel button (Matrix: "Cancel pressed" —
            // research's ONE in-progress order is faction-wide, so Cancel lives beside the status text, not on any
            // one option button, which stays disabled like every other option button while an order is running).
            _researchStatus = MakeLabel(new Vector2(10f, 52f), 13, new Color(0.75f, 0.85f, 0.95f));
            _researchStatus.AddThemeFontOverride("font", _tabularFont);
            _researchStatus.Visible = false;
            _panel.AddChild(_researchStatus);

            // Plain hand-built Button — this file's own convention (no ChimeraComponents dependency anywhere in
            // CommandCardSystem; see the class doc).
            _researchCancelBtn = new Button { Text = "Cancel" };
            _researchCancelBtn.Position = new Vector2(330f, 50f);
            _researchCancelBtn.Size     = new Vector2(80f, 20f);
            _researchCancelBtn.AddThemeFontSizeOverride("font_size", 11);
            _researchCancelBtn.Visible  = false;
            _researchCancelBtn.Pressed += OnResearchCancelPressed;
            _panel.AddChild(_researchCancelBtn);
        }

        // ── Inventory panel construction (Story 3.16) ─────────────────────────

        private void BuildInventoryPanel()
        {
            var canvas = new CanvasLayer();
            AddChild(canvas);

            _inventoryPanel = new Panel();
            // Story 11.7 (AC-5): pinned to the bottom-right corner via anchors so it reflows on resize / UI-scale.
            // Offsets reproduce the former (6*96+16)×118 rect inset 10px from the right and bottom edges.
            const float invW = 6 * 96f + 16f;
            _inventoryPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);
            _inventoryPanel.OffsetRight  = -10f;
            _inventoryPanel.OffsetLeft   = -10f - invW;
            _inventoryPanel.OffsetTop    = -128f;
            _inventoryPanel.OffsetBottom = -128f + 118f;
            _inventoryPanel.Visible  = false;
            _inventoryPanel.MouseFilter = Control.MouseFilterEnum.Stop;
            var bg = new StyleBoxFlat();
            bg.BgColor     = new Color(0.05f, 0.07f, 0.05f, 0.88f);
            bg.BorderColor = new Color(0.25f, 0.45f, 0.25f, 0.9f);
            bg.BorderWidthTop = bg.BorderWidthBottom = bg.BorderWidthLeft = bg.BorderWidthRight = 2;
            bg.CornerRadiusTopLeft = bg.CornerRadiusTopRight = bg.CornerRadiusBottomLeft = bg.CornerRadiusBottomRight = 4;
            _inventoryPanel.AddThemeStyleboxOverride("panel", bg);
            canvas.AddChild(_inventoryPanel);

            _inventoryTitle = MakeLabel(new Vector2(10f, 6f), 14, new Color(0.95f, 0.90f, 0.60f));
            _inventoryTitle.Text = "Inventory";
            _inventoryPanel.AddChild(_inventoryTitle);

            _invUseBtns  = new Button[INV_SLOTS];
            _invDropBtns = new Button[INV_SLOTS];
            for (int i = 0; i < INV_SLOTS; i++)
            {
                var use = new Button();
                use.Position     = new Vector2(8f + i * 96f, 28f);
                use.Size         = new Vector2(90f, 58f);
                use.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                use.AddThemeFontOverride("font", _tabularFont);
                use.Text = "—";
                int s1 = i;
                use.Pressed += () => OnInventoryUsePressed(s1);
                _inventoryPanel.AddChild(use);
                _invUseBtns[i] = use;

                var drop = new Button();
                drop.Position = new Vector2(8f + i * 96f, 88f);
                drop.Size     = new Vector2(90f, 22f);
                drop.Text     = "Drop";
                drop.AddThemeFontSizeOverride("font_size", 11);
                int s2 = i;
                drop.Pressed += () => OnInventoryDropPressed(s2);
                _inventoryPanel.AddChild(drop);
                _invDropBtns[i] = drop;
            }
        }

        /// <summary>Hide every revive-picker button (used when the selection can't revive or has no awaiting heroes).</summary>
        private void HideReviveButtons()
        {
            for (int i = 0; i < _reviveBtns.Length; i++)
            {
                _reviveBtns[i].Visible = false;
                _reviveHeroSlots[i]    = -1;
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

            _workerPanel = new Panel();
            // Story 2.8: widened 420 → 530 so the 5th build button (Aviary) fits. Buttons lay at 10 + i*102 (width
            // 98), so button i=4 spans [418, 516] — the old 420-wide panel clipped it past the border.
            // Story 11.7 (AC-5): pinned to the bottom-left corner via anchors so it reflows on resize / UI-scale.
            _workerPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft);
            _workerPanel.OffsetLeft   = 10f;
            _workerPanel.OffsetTop    = -185f;
            _workerPanel.OffsetRight  = 10f + 530f;
            _workerPanel.OffsetBottom = -185f + 175f;
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
                // DW-921: a 98 px button holds far less text than these labels carry ("Command Center (free)",
                // "Siege Workshop [need: Sigil Forge]"), and a Godot Button neither wraps nor clips by default — the
                // overflow drew straight across the neighbouring buttons and disappeared under their opaque
                // backgrounds, so the worker card read as one smear of overlapping words. Wrap inside the button's
                // own 70 px height, then clip whatever still will not fit. The four producer grids and the Story
                // 11.6 queue strip already did exactly this; the build and ability grids were the two that missed it.
                btn.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                btn.ClipText     = true;

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
                // Story 2.13 (AC3.4): BuildTarget is a PACKED building ref — resolve it (bounds + Alive + generation)
                // for the display, so a stale/recycled ref just shows the generic label instead of misreading a slot.
                string bName = _buildings.TryResolveRef(_world.BuildTarget[focusId], out int bSlot)
                    ? BuildingTypeName(_buildings.Type[bSlot]) : "building";
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
                // Story 4.3: the resolved sparse cost map (was ore-only; buildings never charged crystal, a latent
                // gap this generalization fixes — the affordability preview now checks every resource the sim
                // actually spends, so a crystal-costed building can never show enabled when unaffordable).
                var     cost = _buildSys.GetBuildingCost(bType, faction);
                string? pre  = _buildSys.GetBuildingPlacePrereq(bType, faction);

                bool canAfford = _resources.CanAfford(faction, cost);
                bool prereqMet = pre == null;

                _buildBtns[i].Disabled = isBuilding || !prereqMet || !canAfford;

                // Story 4.3 (review patch): "[need ore]" assumed ore was always the only spendable resource — now
                // that a building's cost map can include crystal too, name the shortfall generically (mirrors the
                // shop button's own "[need resources]" phrasing a few methods above, the established precedent for
                // a canAfford that aggregates more than one resource without attributing the specific one short).
                string note = !prereqMet ? $"\n[need: {pre}]"
                            : !canAfford  ? "\n[need resources]"
                            : $"\n{FormatCost(cost, emptyText: "(free)")}";
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

            // Story 2.9b: cache the ability panel's two Y positions once (not per-frame). Normal = the shared HUD slot
            // (the sole pre-2.9b position — a standalone combat caster keeps it). Stacked = raised one worker-card
            // height (175) + an 8px gap (D-3) so the ability card sits ABOVE a co-displayed worker (build) card
            // instead of overlapping it. (Pre-2.9b only ONE of building/worker/ability showed at a time; worker+ability now co-display.)
            // Story 11.7 (AC-5): expressed as OffsetTop values from the bottom-left anchor so the panel reflows on
            // resize / UI-scale; the toggle swaps OffsetTop/OffsetBottom (see the visibility flip in _Process).
            _abilityPanelNormalTop  = -185f;
            _abilityPanelStackedTop = -185f - 175f - 8f; // D-3: 8px gap above the worker card
            _abilityPanel = new Panel();
            _abilityPanel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomLeft);
            _abilityPanel.OffsetLeft   = 10f;
            _abilityPanel.OffsetRight  = 10f + 420f;
            _abilityPanel.OffsetTop    = _abilityPanelNormalTop;
            _abilityPanel.OffsetBottom = _abilityPanelNormalTop + 175f;
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
                // DW-921: see the build grid — "Matter Infusion [need crystal]" overran its 98 px button and was
                // painted over by the next ability's background, which is the overlap in the lower-left card.
                btn.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                btn.ClipText     = true;

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

                // Story 15.11 (DW-290): the SINGLE shared is-castable predicate consulted by BOTH this disable-gate and
                // OnAbilityBtnPressed — so the enabled state and the press action can never diverge as targeting modes
                // are added (GroundPoint is now castable; only an unknown/unparseable targeting string is not). The
                // GroundPoint "coming soon" fence is removed — the press arms the ground reticle (SelectionSystem).
                bool castableTgt = IsTargetingCastable(ab.ParsedTargeting);

                // Disabled iff the sim would refuse the cast (unknown targeting fails closed). cdTicks/30f is
                // presentation display math (no-float rule is src/Core+Effects only).
                btn.Disabled = !castableTgt || onCd || !energyOk || !oreOk || !crysOk;

                string note = !castableTgt ? "[unsupported]"
                            : onCd          ? $"[on CD {cdTicks / 30f:F1}s]"
                            : !energyOk     ? "[need energy]"
                            : !oreOk        ? "[need ore]"
                            : !crysOk       ? "[need crystal]"
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

            // Story 15.11 (DW-290): consult the SAME predicate the disable-gate uses, so an unknown targeting mode is
            // handled identically in both (the card greys it out; a press is a no-op). GroundPoint is castable in both.
            if (!IsTargetingCastable(ab.ParsedTargeting)) return;

            // Branch on targeting. The card READS _world for display only; the cast leaves as an intent via _selection.
            switch (ab.ParsedTargeting)
            {
                case AbilityTargeting.Self:
                case AbilityTargeting.None:
                    _selection.IssueCastAbilityCommand(focusId, slot, -1); // instant self-cast, no targeting click
                    break;
                case AbilityTargeting.TargetUnit:
                    // Story 15.11 (DW-286): pass the ability's affinity so the click-picker selects an ally (heal-other),
                    // an enemy (default), or anyone. Absent affinity → Enemy (the historical pick), so shipped content is unchanged.
                    _selection.ArmCastTargeting(focusId, slot, ab.ParsedTargetAffinity ?? TargetAffinity.Enemy);
                    break;
                case AbilityTargeting.GroundPoint:
                    _selection.ArmCastGroundTargeting(focusId, slot); // Story 15.11: next left-click picks the ground point
                    break;
                default:
                    break; // unreachable (IsTargetingCastable already returned above), defensive
            }
        }

        /// <summary>
        /// Story 15.11 (DW-290): the SINGLE is-castable-targeting predicate shared by <c>RefreshAbilityCard</c>'s
        /// disable-gate and <see cref="OnAbilityBtnPressed"/>. Forwards to the Godot-free
        /// <see cref="CastTargetPicker.IsTargetingCastable"/> core so the predicate is Tier-1 unit-testable and BOTH
        /// call sites resolve through the identical function — the enabled state and the press action can never diverge.
        /// </summary>
        private static bool IsTargetingCastable(AbilityTargeting? targeting) =>
            CastTargetPicker.IsTargetingCastable(targeting);

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

        /// <summary>
        /// Story 4.3: format a resolved sparse cost map for display — empty ⇒ <paramref name="emptyText"/> (both
        /// call sites use <c>"(free)"</c>: the build-button site's pre-4.3 phrasing, and the train-button site's
        /// review-pass fix — the old unconditional <c>"{costOre} ore"</c> text implied every unit cost at least
        /// some ore, which stopped being true the moment a unit could author an explicit empty <c>cost</c> map or a
        /// legacy-derived cost of 0/0), else each entry as <c>"{amount} {resourceId}"</c> joined by <c>" · "</c>.
        /// For the legacy-derived map (no authored <c>cost</c> key) the dictionary's insertion order is
        /// ore-then-crystal (<see cref="UnitDefinition.ResolvedCost"/>'s <c>LegacyCost</c> derivation), matching
        /// today's hardcoded "{ore} ore · {crystal} crystal" text byte-for-byte for existing (nonzero-cost) content.
        /// </summary>
        private static string FormatCost(IReadOnlyDictionary<string, int> cost, string emptyText) =>
            cost.Count == 0 ? emptyText : string.Join(" · ", cost.Select(kv => $"{kv.Value} {kv.Key}"));
    }
}
