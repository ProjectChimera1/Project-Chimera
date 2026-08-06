#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Economy;

namespace ProjectChimera.AI
{
    /// <summary>Difficulty presets for the AI opponent.</summary>
    public enum AiDifficulty { Easy, Normal, Hard }

    /// <summary>
    /// Utility-scored AI opponent for Player 2.
    ///
    /// Each sim tick the AI:
    ///   1. Prunes dead buildings from its tracked production list.
    ///   2. Continuously trains from every idle production building.
    ///   3. Builds a lightweight game-state snapshot.
    ///   4. Scores every available strategic action and executes the highest scorer.
    ///
    /// Scoring is driven by real game-state signals (supply pressure, tech gaps,
    /// army size, attack cooldown) rather than a hard-coded phase sequence.
    /// Difficulty weights shift the AI's economic vs. aggressive priorities.
    ///
    /// Run AFTER BuildingSystem in the sim loop so supply caps and construction
    /// timers are already updated before the AI makes decisions.
    /// </summary>
    public class AiOpponentSystem : ISimSystem
    {
        // ── Constants ─────────────────────────────────────────────────────────

        private const Faction AI_FACTION = Faction.Player2;

        /// <summary>DW-439/DW-445 — the fixed per-faction slot span the <see cref="AllianceStore"/> mask is sized to
        /// (indices 0-8; 0 = Neutral). Only used by <see cref="HasTeammate"/>'s 8-compare scan, never per-entity.</summary>
        private const int FACTION_SLOT_COUNT = FactionRegistry.SLOT_DEFINITIONS_SIZE;

        // Ore costs — must match EntityPlacer.BUILDING_COSTS and faction JSON.
        private static readonly Fixed COST_CC       = Fixed.FromFloat(150f);
        private static readonly Fixed COST_BARRACKS  = Fixed.FromFloat(100f);
        private static readonly Fixed COST_ARCHERY   = Fixed.FromFloat(120f);
        private static readonly Fixed COST_SIEGE     = Fixed.FromFloat(200f);

        // Supply headroom thresholds that trigger expansion scoring.
        private const int SUPPLY_CRITICAL = 0;
        private const int SUPPLY_TIGHT    = 2;
        private const int SUPPLY_LOW      = 4;

        /// <summary>
        /// DW-636 — the ORE-SURPLUS bar a SECOND Barracks must clear: enough banked ore to pay for the Barracks
        /// AND still fund a further production cycle's worth of orders at the same price.
        ///
        /// This is the "production, not income, is the bottleneck" test. Ore piling up past what a single running
        /// queue can spend is the real demand signal for doubling production; ore that merely covers the Barracks
        /// itself means the AI is ORE-limited, and a second queue would just idle beside the first.
        ///
        /// Derived from <see cref="COST_BARRACKS"/> (declared above — static field initializers run in declaration
        /// order) so a Barracks-cost retune moves the demand bar with it instead of silently drifting.
        /// </summary>
        private static readonly Fixed SECOND_BARRACKS_ORE_SURPLUS = COST_BARRACKS + COST_BARRACKS;

        // Placement positions for AI-built structures (clustered near the P2 base).
        private static readonly FixedVec3 POS_BARRACKS_1   = new(Fixed.FromFloat( 36f), Fixed.Zero, Fixed.FromFloat(  6f));
        private static readonly FixedVec3 POS_BARRACKS_2   = new(Fixed.FromFloat( 36f), Fixed.Zero, Fixed.FromFloat( -6f));
        private static readonly FixedVec3 POS_ARCHERY      = new(Fixed.FromFloat( 42f), Fixed.Zero, Fixed.FromFloat( 12f));
        private static readonly FixedVec3 POS_SIEGE        = new(Fixed.FromFloat( 42f), Fixed.Zero, Fixed.FromFloat(-12f));
        private static readonly FixedVec3 POS_CC_EXPANSION = new(Fixed.FromFloat( 54f), Fixed.Zero, Fixed.Zero);

        /// <summary>
        /// The AI's hardcoded march destination — the Player1 base — written into
        /// <see cref="EntityWorld.CommandGoal"/>/<see cref="EntityWorld.MoveTarget"/> by
        /// <see cref="DoLaunchAttack"/>.
        ///
        /// DW-125: <c>internal</c> rather than <c>private</c> so the Tier-1 fixtures that must place real
        /// defenders exactly where this wave will ARRIVE reference this field symbolically instead of
        /// hand-copying <c>(-45,0,0)</c>. The Godot-free test assembly compiles these sources directly
        /// (godot/SimSources.props), so no <c>InternalsVisibleTo</c> is needed. Retuning the value here now moves
        /// those fixtures with it, instead of silently degrading them into a wave that marches to empty ground.
        /// </summary>
        internal static readonly FixedVec3 P1_BASE = new(Fixed.FromFloat(-45f), Fixed.Zero, Fixed.Zero);

        // ── Difficulty weights ────────────────────────────────────────────────

        private readonly float _aggressionWeight; // scales attack score (0–1)
        private readonly float _techWeight;        // scales tech-up scores  (0–1)
        private readonly int   _attackThreshold;  // available (Idle/Stop) units needed before considering an attack wave
        private readonly Fixed _attackCooldownMax;

        /// <summary>
        /// The per-difficulty tuning curve. The constructor consumes THIS method — it is the single source of
        /// truth, not a second copy of one — so a value read from here can never drift from the value the AI
        /// actually runs on.
        ///
        /// DW-125: <c>internal</c> so the Tier-1 fixtures that must author a wave sized exactly at the attack
        /// threshold read <c>AttackThreshold</c> from here instead of hand-copying the literal <c>5</c>;
        /// a future difficulty-curve retune then moves those fixtures with it (or fails them loudly) instead of
        /// leaving a wave that silently no longer meets the real bar. Visible without <c>InternalsVisibleTo</c>
        /// because the test project compiles these sources directly (godot/SimSources.props).
        ///
        /// The <c>float</c> weights are the AI scorer's known, deliberate float debt (D2 — same-machine
        /// determinism only, see <see cref="ScoreLaunchAttack"/>); extracting them here is behavior-neutral and
        /// moves no value, so nothing folded into <c>SimChecksum</c> changes.
        /// </summary>
        internal static (float AggressionWeight, float TechWeight, int AttackThreshold, float AttackCooldownSeconds)
            DifficultyProfile(AiDifficulty difficulty) => difficulty switch
            {
                AiDifficulty.Easy => (0.40f, 0.50f, 8, 40f),
                AiDifficulty.Hard => (0.90f, 0.90f, 3, 15f),
                _                 => (0.65f, 0.70f, 5, 25f), // Normal
            };

        // ── Dependencies ──────────────────────────────────────────────────────

        private readonly BuildingStore  _buildings;
        private readonly ResourceStore  _resources;
        private readonly BuildingSystem _buildSys;

        /// <summary>
        /// DW-439 / DW-445 — the sim-owned team mask (Story 7.12 / 9.14), threaded in so the AI's hostility test
        /// asks the SAME question <c>CombatSystem</c>/<c>ProjectileSystem</c>/<c>SpatialHash</c> ask. OPTIONAL: a
        /// null store means "no mask" and <see cref="IsHostile"/> degenerates to the pre-fix
        /// <c>f != AI_FACTION &amp;&amp; f != Faction.Neutral</c> test, so every existing caller (and every golden)
        /// stays byte-identical. The FFA default (<c>TeamId[f]==f</c>) is likewise byte-identical, because
        /// <see cref="AllianceStore.AreAllied"/> is false for every pair of DISTINCT factions under it.
        /// </summary>
        private readonly AllianceStore? _alliances;

        // ── Tracked state ─────────────────────────────────────────────────────

        /// <summary>IDs of all AI-owned production buildings (Barracks / ArcheryRange / SiegeWorkshop).</summary>
        private readonly List<int> _productionBuildingIds = new();

        /// <summary>ID of the supply-expansion CommandCenter (-1 = not yet committed).</summary>
        private int _cmdCenterExpId = -1;

        private Fixed _attackCooldown = Fixed.Zero;

        // ── Constructor ───────────────────────────────────────────────────────

        public AiOpponentSystem(BuildingStore buildings, ResourceStore resources,
                                BuildingSystem buildSys,
                                AiDifficulty difficulty = AiDifficulty.Normal,
                                AllianceStore? alliances = null)
        {
            _buildings = buildings;
            _resources = resources;
            _buildSys  = buildSys;
            // DW-439/DW-445 — optional team mask. null (or the FFA default) ⇒ the pre-fix hostility test exactly,
            // so no shipped scenario, test fixture or golden changes behavior.
            _alliances = alliances;

            // DW-125: read the curve from the shared DifficultyProfile above rather than inlining the tuple here,
            // so the values the tests reference and the values this instance runs on are literally the same ones.
            (_aggressionWeight, _techWeight, _attackThreshold, float cooldownS) = DifficultyProfile(difficulty);
            _attackCooldownMax = Fixed.FromFloat(cooldownS);

            // Adopt any production buildings the scenario pre-placed for the AI.
            AdoptPreplacedBuildings();
        }

        /// <summary>
        /// Story 3.10 — restore this system to its post-construction (fresh-boot) state for the in-place Edit↔Play
        /// reset. <see cref="ProjectChimera.Core.Sim.SimulationHost.ClearForReset"/> calls this AFTER the stores are
        /// cleared but BEFORE the scenario is re-applied, so <see cref="AdoptPreplacedBuildings"/> sees the
        /// just-cleared (empty) building store — exactly as the ctor did at boot. Without this, the AI's per-match
        /// decision state (<see cref="_productionBuildingIds"/>, <see cref="_cmdCenterExpId"/>,
        /// <see cref="_attackCooldown"/>) survives the reset and makes the next Play diverge from a fresh boot,
        /// desyncing the folded SimChecksum. Difficulty weights are readonly and intentionally preserved.
        /// </summary>
        public void ResetForMatch()
        {
            _productionBuildingIds.Clear();
            _cmdCenterExpId = -1;
            _attackCooldown = Fixed.Zero;
            AdoptPreplacedBuildings();
        }

        // ── Story 11.3 (SP save/load): per-match decision-state capture/restore ─────────────────────────────────
        // The AI holds decision state OUTSIDE every store (_productionBuildingIds, the _cmdCenterExpId expansion latch,
        // the _attackCooldown). A mid-match save that omitted it would resume with a fresh-boot AI (re-adopting only
        // pre-placed buildings, an un-latched expansion, a zero cooldown), diverging the folded SimChecksum on the
        // first tick the AI decides. These capture/restore the exact state (the same three fields ResetForMatch resets).

        /// <summary>Story 11.3: capture the per-match AI decision state for a save. <paramref name="attackCooldownRaw"/>
        /// is <see cref="Fixed.Raw"/> (no float ever). The returned production-building array is a fresh copy.</summary>
        public int[] CaptureProductionBuildingIds() => _productionBuildingIds.ToArray();

        /// <summary>Story 11.3: the supply-expansion CommandCenter packed ref (-1 = not committed) for a save.</summary>
        public int CaptureCmdCenterExpId() => _cmdCenterExpId;

        /// <summary>Story 11.3: the remaining attack-cooldown as <see cref="Fixed.Raw"/> for a save (no float).</summary>
        public int CaptureAttackCooldownRaw() => _attackCooldown.Raw;

        /// <summary>Story 11.3: overlay the per-match AI decision state from a save (SP load), replacing whatever
        /// <see cref="ResetForMatch"/> re-adopted from the freshly re-applied scenario. <paramref name="attackCooldownRaw"/>
        /// is <see cref="Fixed.Raw"/>.</summary>
        public void RestoreSaveState(int[] productionBuildingIds, int cmdCenterExpId, int attackCooldownRaw)
        {
            _productionBuildingIds.Clear();
            if (productionBuildingIds != null)
                _productionBuildingIds.AddRange(productionBuildingIds);
            _cmdCenterExpId = cmdCenterExpId;
            _attackCooldown = Fixed.FromRaw(attackCooldownRaw);
        }

        // ── ISimSystem ────────────────────────────────────────────────────────

        public void Tick(EntityWorld world, Fixed dt)
        {
            PruneDeadBuildings();

            // Continuous training — always drain idle production buildings first.
            foreach (int packed in _productionBuildingIds)
            {
                // Story 2.13 (AC3.4): resolve the PACKED ref (bounds + Alive + generation) so a recycled production
                // slot — now a different or even enemy building — is NEVER trained from.
                if (_buildings.TryResolveRef(packed, out int slot) && !_buildings.IsUnderConstruction(slot))
                    _buildSys.TrainUnit(slot, _resources);
            }

            // Tick attack cooldown.
            if (_attackCooldown > Fixed.Zero)
                _attackCooldown -= dt;

            // Score and execute the best strategic action this tick.
            AiSnapshot snap = BuildSnapshot(world);
            ExecuteBestAction(snap, world);
        }

        // ── Alliance awareness (DW-439 / DW-445) ──────────────────────────────

        /// <summary>
        /// The SINGLE "is <paramref name="f"/> an ENEMY of the AI?" test — shared by the snapshot's threat and
        /// raze-target classification, the building picker, and the wave's march destination, so those four sites can
        /// never disagree about who the enemy is.
        ///
        /// <para><b>The defect this closes (DW-439 / DW-445).</b> The AI used to spell hostility as
        /// <c>f != AI_FACTION &amp;&amp; f != Faction.Neutral</c> at each site independently and never consulted the
        /// <see cref="AllianceStore"/>. In a teamed match (an AI on a team with a player — WC3's most-played
        /// "2 players + 2 computers" setup) that made an ALLIED faction a target: the AI ordered
        /// <see cref="UnitCommand.AttackBuilding"/> onto its ally's base, <c>CombatSystem.TickAttackBuildingCombat</c>'s
        /// Story-9.14 allied guard (<c>CombatSystem.cs:435</c>) rejected the order and reverted the unit to Idle, the AI
        /// re-issued the identical order the next tick, and its whole force sat at Idle forever instead of engaging the
        /// real enemy. The mirror-image half also stalled it: an ALLIED unit counted as a live "enemy defender", so
        /// <see cref="ScoreRazeBuildings"/> never committed a raze while any teammate unit was alive.</para>
        ///
        /// <para><b>Golden-neutral.</b> A null store, or the FFA default (<c>TeamId[f]==f</c>), makes
        /// <see cref="AllianceStore.AreAllied"/> false for every pair of distinct factions — so this returns exactly
        /// what the old inline test returned. Neutral is never hostile (unchanged), and a faction is always allied with
        /// itself, so <c>AI_FACTION</c> is never hostile either.</para>
        ///
        /// <para>CONSTRAINT (recorded with the decision): this makes SINGLE-PLAYER skirmish correct. It does NOT unblock
        /// teamed AI in lockstep MP — the scorer still runs on <c>float</c> (D2), which stays gated on the
        /// float→Fixed migration (DW-204 / Story 10.11).</para>
        /// </summary>
        private bool IsHostile(Faction f)
        {
            if (f == AI_FACTION || f == Faction.Neutral) return false;
            return _alliances == null || !_alliances.AreAllied(AI_FACTION, f);
        }

        /// <summary>
        /// True iff the AI shares a team with at least one OTHER faction — the cheap gate that keeps the ally-aware
        /// target-priority path (<see cref="BuildAllyFocusMask"/>) out of every FFA tick entirely. Eight integer
        /// compares over the fixed 9-slot mask; no allocation, no per-entity work, and provably false under a null
        /// store or the FFA default, so an FFA/golden tick never even allocates the mask.
        /// </summary>
        private bool HasTeammate()
        {
            if (_alliances == null) return false;
            for (int f = 0; f < FACTION_SLOT_COUNT; f++)
            {
                var faction = (Faction)f;
                if (faction == AI_FACTION || faction == Faction.Neutral) continue;
                if (_alliances.AreAllied(AI_FACTION, faction)) return true;
            }
            return false;
        }

        // ── Snapshot ──────────────────────────────────────────────────────────

        private struct AiSnapshot
        {
            public int  SupplyHeadroom;
            /// <summary>DW-63 — mirrors <see cref="ResourceStore.SupplyGatingEnabled"/> for this tick. When false the
            /// supply gate never blocks training, so <see cref="SupplyHeadroom"/> is an unbounded, uninformative
            /// number and <see cref="ScoreExpandSupply"/> must not read its magnitude.</summary>
            public bool SupplyGatingEnabled;
            public int  AvailableCombatUnits; // Idle or Stop — not under orders, conscriptable into a wave
            /// <summary>
            /// DW-644 — the subset of <see cref="AvailableCombatUnits"/> that can actually EXECUTE a raze order:
            /// conscriptable AND <see cref="AttackDomain.Structure"/>-capable, the exact pair of tests
            /// <see cref="DoRazeBuildings"/> applies before it issues an <see cref="UnitCommand.AttackBuilding"/>.
            ///
            /// <para><see cref="ScoreRazeBuildings"/> reads THIS, not the raw availability count. Scoring on the raw
            /// count let an AI whose free units are all air-only / anti-unit-only pin RazeBuildings at 0.90 every tick
            /// while the dispatcher skipped every one of them — an indefinite no-op that ALSO starved BuildBarracks
            /// (0.85) and the whole tech chain, all of which score below 0.90, so the AI stopped producing entirely.</para>
            ///
            /// <para>Always equal to <see cref="AvailableCombatUnits"/> for any unit with the unauthored default
            /// <see cref="AttackDomain.All"/> — i.e. every shipped scenario and every golden, since no faction JSON
            /// authors <c>attack_domains</c>. The divergence needs an authored restriction (or a save-restore overlay),
            /// so no recorded checksum sequence moves.</para>
            /// </summary>
            public int  AvailableRazeCapableUnits;
            public bool EnemyThreatRemains;   // Story 2.13 — any alive enemy (non-Neutral) combat unit still defends
            public bool EnemyBuildingExists;  // Story 2.13 — any alive enemy (non-Neutral) building left to raze
            // DW-838 removed HasLiveCommandCenter: it existed solely as the below-threshold raze stall-breaker's
            // gate, and once that term was deleted nothing read it. A write-only snapshot field is the same dead-
            // guard class the removal closed, so the write in BuildSnapshot went with it.
            public bool HasLiveBarracks;
            public bool HasCompleteBarracks;
            public bool HasLiveArcheryRange;
            public bool HasCompleteArcheryRange;
            public bool HasLiveSiegeWorkshop;
            public bool HasCompleteSiegeWorkshop;
            /// <summary>DW-636 — AI-owned Barracks that are ALIVE, whether complete or still a construction site.
            /// <see cref="ScoreBuildSecondBarracks"/> gates on this rather than on a complete-only count: a
            /// complete-only test lets the AI re-enter the build action on every tick while its second Barracks is
            /// still going up, stacking duplicates on the same tile for as long as it can afford them.</summary>
            public int  LiveBarracksCount;
            public bool CanAffordCC;
            public bool CanAffordBarracks;
            public bool CanAffordArchery;
            public bool CanAffordSiege;
            /// <summary>DW-636 — ore banked at or above <see cref="SECOND_BARRACKS_ORE_SURPLUS"/>: the
            /// production-demand signal that replaced the second Barracks' old supply-expansion gate.</summary>
            public bool HasOreSurplusForSecondBarracks;
        }

        private AiSnapshot BuildSnapshot(EntityWorld world)
        {
            var snap = new AiSnapshot();

            int fIdx = (int)AI_FACTION;
            snap.SupplyHeadroom = _resources.SupplyCap[fIdx] - _resources.SupplyUsed[fIdx];
            // DW-63: carry the per-scenario supply-gate state alongside the headroom so the scorer can tell an
            // informative headroom from an unenforced (and therefore meaningless) one. Story 4.4 resolves this once
            // per scenario-apply — never inside a tick — so reading it here is a plain instance-state read.
            snap.SupplyGatingEnabled = _resources.SupplyGatingEnabled;

            // Scan buildings for tech coverage (and, Story 2.13, whether any enemy base remains to raze).
            for (int i = 0; i < _buildings.Count; i++)
            {
                if (!_buildings.Alive[i]) continue;
                Faction bf = _buildings.FactionOf[i];
                if (bf != AI_FACTION)
                {
                    // DW-439: a real ENEMY base — a raze target. An ALLIED faction's base is not one; counting it here
                    // made ScoreRazeBuildings commit a wave that FindNearestEnemyBuilding could then find no target
                    // for, re-choosing RazeBuildings every tick and doing nothing (a permanent decision stall).
                    if (IsHostile(bf)) snap.EnemyBuildingExists = true;
                    continue;
                }
                bool complete = !_buildings.IsUnderConstruction(i);
                // DW-838: no CommandCenter arm — the below-threshold raze gate that read it is gone, so recording it
                // would only re-create a write-only field. The AI's own base is not a scoring input anywhere else.
                switch (_buildings.Type[i])
                {
                    case BuildingType.Barracks:
                        snap.HasLiveBarracks = true;
                        snap.LiveBarracksCount++;                 // DW-636 — complete OR under construction
                        if (complete) snap.HasCompleteBarracks = true;
                        break;
                    case BuildingType.ArcheryRange:
                        snap.HasLiveArcheryRange = true;
                        if (complete) snap.HasCompleteArcheryRange = true;
                        break;
                    case BuildingType.SiegeWorkshop:
                        snap.HasLiveSiegeWorkshop = true;
                        if (complete) snap.HasCompleteSiegeWorkshop = true;
                        break;
                }
            }

            // Count P2 COMBAT units (non-workers, damage-bearing) available for a wave.
            // Freshly trained units hold position (Stop) at the spawn point;
            // veterans whose AttackMove completed sit at Idle wherever the
            // wave ended. Both are conscriptable into the next wave.
            int hwm = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
            {
                if (!world.IsAlive(i)) continue;
                Faction uf = world.FactionOf[i];
                if (uf != AI_FACTION)
                {
                    // Story 2.13 (AC1.5) — a live enemy defender (hostile, non-gatherer, damage-bearing). While
                    // ANY remains, the AI fights the army rather than tunnel-visioning a building past live defenders.
                    // DW-439: an ALLIED unit is not a defender to fight. Counting one kept EnemyThreatRemains latched
                    // for as long as any teammate unit drew breath, so a teamed AI never reached the raze fallback and
                    // a DestroyAllBuildings match with an AI on the team could not conclude.
                    if (IsHostile(uf) && world.GatherState[i] == GatherState.Inactive
                        && world.CanDealDamage(i))   // DW-643 — the shared predicate, complement of CombatSystem's
                        snap.EnemyThreatRemains = true;
                    continue;
                }
                if (!IsConscriptable(world, i)) continue;
                snap.AvailableCombatUnits++;
                // DW-644: the raze-capable subset, counted with the SAME predicate DoRazeBuildings filters on, so the
                // count that gates a raze wave and the units that wave actually orders can never disagree.
                if (CanRazeStructures(world, i)) snap.AvailableRazeCapableUnits++;
            }

            snap.CanAffordCC       = _resources.CanAffordOre(AI_FACTION, COST_CC);
            snap.CanAffordBarracks  = _resources.CanAffordOre(AI_FACTION, COST_BARRACKS);
            snap.CanAffordArchery  = _resources.CanAffordOre(AI_FACTION, COST_ARCHERY);
            snap.CanAffordSiege    = _resources.CanAffordOre(AI_FACTION, COST_SIEGE);
            // DW-636: the ore-SURPLUS read that replaced the second Barracks' supply-expansion gate. Same
            // CanAffordOre comparator as the rows above (Fixed, no float), just at a higher bar.
            snap.HasOreSurplusForSecondBarracks =
                _resources.CanAffordOre(AI_FACTION, SECOND_BARRACKS_ORE_SURPLUS);

            return snap;
        }

        /// <summary>
        /// Story 15.4 (DW-202) — the SINGLE "can this unit be conscripted into a wave?" test, shared by
        /// <see cref="BuildSnapshot"/>'s availability count and BOTH wave dispatchers
        /// (<see cref="DoLaunchAttack"/> / <see cref="DoRazeBuildings"/>), so the count that gates a wave and the
        /// units that wave actually orders can never disagree.
        ///
        /// The damage-bearing term is the fix: a zero-damage unit is not a combat unit. CombatSystem excludes it
        /// from every engagement path, so conscripting one adds nothing to the assault while inflating the count
        /// that gates <see cref="ScoreLaunchAttack"/>/<see cref="ScoreRazeBuildings"/> into launching an
        /// under-strength wave. Worse, before Story 15.4 flipping one to AttackMove LEAKED it permanently: a
        /// non-combatant received no combat tick at all, so nothing ever moved it back to Idle/Stop and it was
        /// never counted again. Reachable via a trigger <c>spawn_unit</c> of a zero-damage authored unit into the
        /// AI slot. Same <see cref="EntityWorld.CanDealDamage"/> test the enemy-defender branch of
        /// <see cref="BuildSnapshot"/> uses.
        ///
        /// DW-643: that damage-bearing term is now the SHARED <see cref="EntityWorld.CanDealDamage"/> predicate, whose
        /// complement <see cref="EntityWorld.IsNonCombatant"/> is what CombatSystem's non-combatant gate asks. The two
        /// used to be spelled independently (<c>&gt; Fixed.Zero</c> here, <c>== Fixed.Zero</c> there) and were exact
        /// complements only because ModifierSystem floors the stat at zero — which the save-restore overlay bypassed,
        /// so a negative value loaded from a save made the two systems disagree about one unit.
        /// </summary>
        private static bool IsConscriptable(EntityWorld world, int i) =>
            world.IsAlive(i)
            && world.FactionOf[i]   == AI_FACTION
            && world.GatherState[i] == GatherState.Inactive
            && world.CanDealDamage(i)   // DW-202/DW-643 — a non-combatant is not a wave unit
            && (world.CommandState[i] == UnitCommand.Idle || world.CommandState[i] == UnitCommand.Stop);

        /// <summary>
        /// DW-644 — the SINGLE "can this unit actually carry out a raze order?" test, shared by
        /// <see cref="BuildSnapshot"/>'s <see cref="AiSnapshot.AvailableRazeCapableUnits"/> count and the
        /// <see cref="DoRazeBuildings"/> dispatcher, so the strength the scorer measures is the strength the wave
        /// actually fields.
        ///
        /// A unit whose authored <c>attack_domains</c> exclude <see cref="AttackDomain.Structure"/> can never damage a
        /// building: <c>CombatSystem.TickAttackBuildingCombat</c> rejects its <see cref="UnitCommand.AttackBuilding"/>
        /// order outright (CombatSystem.cs:436) and reverts it to Idle, and
        /// <c>CombatSystem.FindNearestEnemyBuildingInRange</c> never auto-acquires one for it either. This is the same
        /// bit test both of those sites spell, so all three agree by construction.
        ///
        /// Pure integer bit-AND on an authored, UNFOLDED field — no <c>Fixed</c>, no float, nothing hashed.
        /// </summary>
        private static bool CanRazeStructures(EntityWorld world, int i) =>
            (world.AttackDomainOf[i] & AttackDomain.Structure) != AttackDomain.None;

        // ── Scoring ───────────────────────────────────────────────────────────
        //
        // Each function returns a priority score in [0, 1].
        // Gate conditions that make the action impossible return 0.
        // The highest-scoring action is executed once per tick.

        /// <summary>
        /// Score the one-shot supply-expansion CommandCenter.
        ///
        /// DW-63: the headroom ladder below is only meaningful while the supply gate is actually ENFORCED. When a
        /// scenario authors <c>supply.enabled:false</c> the gate never blocks training, <c>SupplyUsed</c> runs away
        /// past <c>SupplyCap</c>, and the headroom saturates deeply negative — permanently pinning the ladder's
        /// worst tier (0.95) on an action whose payoff (a bigger cap) nobody enforces.
        ///
        /// DW-636 (the "revisit" half of the recorded decision): the ungated case now SKIPS the expansion outright
        /// instead of taking DW-63's deprioritized-but-still-committed 0.25. DW-63 could not skip it, because the
        /// expansion CommandCenter was also the gate <see cref="ScoreBuildSecondBarracks"/> read — a hard 0 would
        /// have permanently cost an ungated-scenario AI its second production building. That coupling is gone, and
        /// with it the last reason to buy a 150-ore building whose ONLY remaining payoff is +10 to a cap nothing
        /// enforces (it is neither a drop-off nor a producer — see DW-637). The 150 ore is better left for
        /// production the AI can actually use.
        ///
        /// Golden-neutral: this branch is reachable only when a scenario authors <c>supply.enabled:false</c>, and
        /// no shipped scenario or golden does (<c>SupplyConfig.Resolve(null)</c> yields <c>enabled:true</c>).
        /// </summary>
        private float ScoreExpandSupply(in AiSnapshot s)
        {
            if (_cmdCenterExpId >= 0) return 0f; // already committed (building or built)
            if (!s.CanAffordCC) return 0f;
            if (!s.SupplyGatingEnabled) return 0f; // DW-636 — an unenforced cap is not worth 150 ore
            if (s.SupplyHeadroom <= SUPPLY_CRITICAL) return 0.95f;
            if (s.SupplyHeadroom <= SUPPLY_TIGHT)    return 0.80f;
            if (s.SupplyHeadroom <= SUPPLY_LOW)      return 0.55f;
            return 0f;
        }

        private float ScoreBuildBarracks(in AiSnapshot s)
        {
            if (s.HasLiveBarracks)   return 0f; // already live or under construction
            if (!s.CanAffordBarracks) return 0f;
            return 0.85f;
        }

        private float ScoreBuildArcheryRange(in AiSnapshot s)
        {
            if (!s.HasCompleteBarracks)  return 0f; // gate: need a live Barracks first
            if (s.HasLiveArcheryRange)   return 0f;
            if (!s.CanAffordArchery)     return 0f;
            return 0.70f * _techWeight;
        }

        private float ScoreBuildSiegeWorkshop(in AiSnapshot s)
        {
            if (!s.HasCompleteArcheryRange) return 0f; // gate: need ArcheryRange first
            if (s.HasLiveSiegeWorkshop)     return 0f;
            if (!s.CanAffordSiege)          return 0f;
            return 0.60f * _techWeight;
        }

        /// <summary>
        /// Score a SECOND Barracks — doubling unit production.
        ///
        /// DW-636: this used to be gated on the supply-expansion CommandCenter (<c>if (!s.HasCCExpansion) return
        /// 0f;</c>), wiring an economic/production decision to a supply device — the AI could double production
        /// only after committing a CommandCenter whose real payoff is +10 supply. The gate is now genuine
        /// production DEMAND, in the order the cheapest test comes first:
        ///
        ///   • <see cref="AiSnapshot.LiveBarracksCount"/> &lt; 2 — not already doubled, AND no second Barracks
        ///     already going up. Counting LIVE (not complete) Barracks is what stops the AI re-entering this
        ///     action every tick while its second Barracks is a construction site, stacking duplicate Barracks on
        ///     the same <see cref="POS_BARRACKS_2"/> tile for as long as it can afford them.
        ///   • A COMPLETE first Barracks — a queue that is not yet running cannot be saturated, so there is no
        ///     production bottleneck to relieve yet.
        ///   • An ORE SURPLUS past <see cref="SECOND_BARRACKS_ORE_SURPLUS"/> — merely affording the Barracks means
        ///     the AI is income-limited; ore banking up beyond what one queue spends is the production-limited
        ///     signal that actually justifies a second queue.
        ///   • ARMY DEMAND — an available force already at the decisive size <see cref="ScoreLaunchAttack"/> tops
        ///     its ratio out at (<c>_attackThreshold * 2</c>) does not need more production, it needs to attack.
        ///
        /// Score value unchanged at 0.50 (the recorded decision replaces the GATE, not the tuning weight).
        ///
        /// Golden-neutral: every AI-bearing golden either starves the AI of ore (GoldenScenario /
        /// MultiFactionScenario give Player2 0 ore ⇒ <c>CanAffordBarracks</c> false) or never completes a first
        /// Barracks inside its horizon (AiActiveScenario builds one on tick 1 with a 10s construction timer and
        /// runs exactly 300 ticks = 10s, so <c>HasCompleteBarracks</c> is false for the whole recording).
        /// </summary>
        private float ScoreBuildSecondBarracks(in AiSnapshot s)
        {
            if (s.LiveBarracksCount >= 2) return 0f; // already doubled, or the second is already going up
            if (!s.HasCompleteBarracks)   return 0f; // no running queue yet ⇒ nothing to be saturated
            if (!s.CanAffordBarracks)     return 0f;
            if (!s.HasOreSurplusForSecondBarracks) return 0f; // ore-limited, not production-limited
            if (s.AvailableCombatUnits >= _attackThreshold * 2) return 0f; // army already decisive — attack instead
            return 0.50f;
        }

        private float ScoreLaunchAttack(in AiSnapshot s)
        {
            if (_attackCooldown > Fixed.Zero)                return 0f;
            if (s.AvailableCombatUnits < _attackThreshold)   return 0f;

            // Score scales up as the army grows beyond the minimum threshold.
            float ratio = Math.Min(1f, (float)s.AvailableCombatUnits / (_attackThreshold * 2));
            return _aggressionWeight * ratio;
        }

        /// <summary>
        /// Story 2.13 (AC1.5) — the RAZE fallback. When the enemy army is gone (no live defenders) but an enemy
        /// base still stands, send free units to raze it — otherwise an assault that killed every defender stands
        /// INERT next to enemy structures forever (deferred-work #7), and a DestroyAllBuildings match never
        /// concludes. Flat-high so it dominates build/expand once razing is possible (the game is won — finish it);
        /// returns 0 while any defender remains (fight the army first) or nothing is left to raze.
        ///
        /// Two commit bars: a FULL WAVE at/above the attack threshold (the common case), OR — Story 2.13 review
        /// (Alec) — a below-threshold STALL-BREAKER for a winning AI that is genuinely stuck (a remnant below the
        /// wave threshold, no production building and no ore to build one), so a starved-but-winning AI still
        /// concludes instead of hanging next to the last enemy base. DW-838: the stall-breaker used to ALSO require
        /// a live CommandCenter, sold as a determinism fence for the starved cross-platform goldens; that gate
        /// excluded the base-less remnant the stall-breaker exists for, and was deleted. AiActiveScenario is
        /// unaffected either way (it has a barracks + a full wave → it razes via the >= threshold path above,
        /// never this branch).
        ///
        /// <para><b>DW-644 — both bars count RAZE-CAPABLE units, not raw availability.</b> They used to read
        /// <see cref="AiSnapshot.AvailableCombatUnits"/> while <see cref="DoRazeBuildings"/> then skipped every unit
        /// whose <see cref="AttackDomain"/> lacks <see cref="AttackDomain.Structure"/>. An AI whose free units were all
        /// air-only or anti-unit-only therefore scored 0.90 here every single tick and issued ZERO orders — an
        /// indefinite no-op that also out-bid <see cref="ScoreBuildBarracks"/> (0.85) and the entire tech chain, so the
        /// AI stopped producing as well and could never grow a force that COULD raze. Measuring the wave in units that
        /// can actually hit a structure makes an unexecutable raze score 0 and lets the AI fall through to building.
        /// Same family as DW-202 (which fixed the zero-damage half of the count/dispatch mismatch), and the shared
        /// <see cref="CanRazeStructures"/> predicate is what keeps the two halves from drifting apart again.</para>
        ///
        /// <para>Golden-neutral: <c>attack_domains</c> is unauthored everywhere in shipped content, so
        /// <see cref="AttackDomain.All"/> is every unit's value and the two counts are identical in every golden.</para>
        /// </summary>
        private float ScoreRazeBuildings(in AiSnapshot s)
        {
            if (s.EnemyThreatRemains)   return 0f; // fight live defenders first — never tunnel-vision a building
            if (!s.EnemyBuildingExists) return 0f; // nothing left to raze

            // Full-strength raze wave — commit at ATTACK strength (same bar as ScoreLaunchAttack), measured in units
            // that can actually damage a structure (DW-644). The starved core goldens hold 3 < the Normal threshold
            // of 5, so they never reach this line.
            if (s.AvailableRazeCapableUnits >= _attackThreshold) return 0.90f;

            // Below-threshold STALL-BREAKER (Story 2.13 review, Alec): a remnant below the wave threshold, with NO
            // production building and no ore to build one → the AI can never grow back to a full wave, so it commits
            // its remnant to raze rather than stall forever (the >= threshold gate alone leaves this exact case
            // hanging).
            // DW-838 (decision 2026-08-06, Alec): the branch used to ALSO require `s.HasLiveCommandCenter`, which
            // inverted its own purpose. The comment called that term a determinism FENCE (a real base always has its
            // CC; the starved cross-platform goldens' Player2 has none → they stayed inert), but as a gameplay rule
            // it excluded the single case the stall-breaker exists for: an AI whose CC is already destroyed is
            // strictly LESS able to grow back than one that still has it. A CC alone cannot rebuild without income
            // either — the ore/production terms below are the real "can never grow back" test — so the term is
            // REMOVED, not negated, and every stalled remnant now commits. The fence it was doing double duty as is
            // not needed: the starved goldens' fodder is scored here only once no enemy defender remains, and their
            // sequences were re-verified against this branch rather than assumed inert.
            // DW-644: "a remnant" means a remnant that can RAZE — an all-air-only remnant commits nothing, so pinning
            // 0.90 on it merely froze the AI here instead of letting it fall through.
            if (s.AvailableRazeCapableUnits > 0
                && !s.HasLiveBarracks && !s.HasLiveArcheryRange && !s.HasLiveSiegeWorkshop
                && !s.CanAffordBarracks && !s.CanAffordArchery && !s.CanAffordSiege)
                return 0.90f;

            return 0f;
        }

        // ── Action dispatch ───────────────────────────────────────────────────

        private enum StrategicAction
        {
            Nothing,
            ExpandSupplyCap,
            BuildBarracks,
            BuildArcheryRange,
            BuildSiegeWorkshop,
            BuildSecondBarracks,
            LaunchAttack,
            RazeBuildings,   // Story 2.13 — send free units to raze the undefended enemy base
        }

        private void ExecuteBestAction(in AiSnapshot snap, EntityWorld world)
        {
            float best   = 0.01f; // floor — do nothing for near-zero scores
            var   chosen = StrategicAction.Nothing;

            void Consider(StrategicAction action, float score)
            {
                if (score > best) { best = score; chosen = action; }
            }

            Consider(StrategicAction.ExpandSupplyCap,    ScoreExpandSupply(snap));
            Consider(StrategicAction.BuildBarracks,       ScoreBuildBarracks(snap));
            Consider(StrategicAction.BuildArcheryRange,   ScoreBuildArcheryRange(snap));
            Consider(StrategicAction.BuildSiegeWorkshop,  ScoreBuildSiegeWorkshop(snap));
            Consider(StrategicAction.BuildSecondBarracks, ScoreBuildSecondBarracks(snap));
            Consider(StrategicAction.LaunchAttack,        ScoreLaunchAttack(snap));
            Consider(StrategicAction.RazeBuildings,        ScoreRazeBuildings(snap)); // Story 2.13

            switch (chosen)
            {
                case StrategicAction.ExpandSupplyCap:    DoExpandSupplyCap();    break;
                case StrategicAction.BuildBarracks:       DoBuildBarracks(false); break;
                case StrategicAction.BuildArcheryRange:   DoBuildArcheryRange();  break;
                case StrategicAction.BuildSiegeWorkshop:  DoBuildSiege();         break;
                case StrategicAction.BuildSecondBarracks: DoBuildBarracks(true);  break;
                case StrategicAction.LaunchAttack:        DoLaunchAttack(world);  break;
                case StrategicAction.RazeBuildings:        DoRazeBuildings(world); break; // Story 2.13
            }
        }

        // ── Action implementations ────────────────────────────────────────────

        private void DoExpandSupplyCap()
        {
            if (!_resources.SpendOre(AI_FACTION, COST_CC)) return;
            int id = _buildings.Create(POS_CC_EXPANSION, AI_FACTION, BuildingType.CommandCenter);
            if (id < 0) _resources.AddOre(AI_FACTION, COST_CC); // store full — refund
            else        _cmdCenterExpId = _buildings.PackRef(id); // Story 2.13: packed ref (golden-neutral at gen 0)
        }

        private void DoBuildBarracks(bool isSecond)
        {
            if (!_resources.SpendOre(AI_FACTION, COST_BARRACKS)) return;
            FixedVec3 pos = isSecond ? POS_BARRACKS_2 : POS_BARRACKS_1;
            int id = _buildings.Create(pos, AI_FACTION, BuildingType.Barracks);
            if (id < 0) _resources.AddOre(AI_FACTION, COST_BARRACKS);
            else        _productionBuildingIds.Add(_buildings.PackRef(id));
        }

        private void DoBuildArcheryRange()
        {
            if (!_resources.SpendOre(AI_FACTION, COST_ARCHERY)) return;
            int id = _buildings.Create(POS_ARCHERY, AI_FACTION, BuildingType.ArcheryRange);
            if (id < 0) _resources.AddOre(AI_FACTION, COST_ARCHERY);
            else        _productionBuildingIds.Add(_buildings.PackRef(id));
        }

        private void DoBuildSiege()
        {
            if (!_resources.SpendOre(AI_FACTION, COST_SIEGE)) return;
            int id = _buildings.Create(POS_SIEGE, AI_FACTION, BuildingType.SiegeWorkshop);
            if (id < 0) _resources.AddOre(AI_FACTION, COST_SIEGE);
            else        _productionBuildingIds.Add(_buildings.PackRef(id));
        }

        private void DoLaunchAttack(EntityWorld world)
        {
            // DW-439 — the march DESTINATION has to be hostile ground. The hardcoded P1_BASE is only the enemy's base
            // while Player1 IS the enemy; on a team with Player1 the wave marched straight into its ally's base and
            // stood there (combat correctly refuses to fire on an ally) while the real enemy went untouched.
            if (!TryResolveWaveDestination(world, out FixedVec3 dest))
            {
                // Teamed AI with nothing hostile left to march on. Burn the cooldown anyway so the (still positive)
                // attack score stops out-bidding the tech/production actions every tick on an order it cannot issue.
                _attackCooldown = _attackCooldownMax;
                return;
            }

            int hwm = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
            {
                if (!IsConscriptable(world, i)) continue; // DW-202 — same bar BuildSnapshot counted
                world.CommandState[i] = UnitCommand.AttackMove;
                world.CommandGoal[i]  = dest;
                world.MoveTarget[i]   = dest;
            }
            _attackCooldown = _attackCooldownMax;
        }

        /// <summary>
        /// DW-439 — where this wave marches.
        ///
        /// <para>The wave marches at the nearest HOSTILE structure to its own vanguard (the lowest-id conscript —
        /// an ascending-id pick, so the choice is deterministic), falling back to the nearest hostile UNIT when
        /// every enemy structure is already down but its army is not. With neither, there is nothing to attack and
        /// the caller skips the wave rather than marching onto an ally.</para>
        ///
        /// <para><b>DW-783 / DW-738 (Phase B re-baseline).</b> This method used to short-circuit with
        /// <c>if (IsHostile(Faction.Player1)) { dest = P1_BASE; return true; }</c>, which is EVERY free-for-all
        /// match — so the general resolution below was dead in every shipped scenario and every wave marched at the
        /// hardcoded <see cref="P1_BASE"/> (−45, 0, 0) regardless of the map actually loaded. The short-circuit was
        /// kept deliberately (its own doc said so) because deleting it moves recorded checksum sequences, which the
        /// burn-down forbade. This IS that deliberate re-baseline, so the guard is gone and the correct path now
        /// runs everywhere. <see cref="P1_BASE"/> survives only as the AI's own-base heuristic elsewhere.</para>
        /// </summary>
        private bool TryResolveWaveDestination(EntityWorld world, out FixedVec3 dest)
        {
            dest = default;

            // The wave's vanguard: the lowest-id conscript (ascending scan ⇒ deterministic). ScoreLaunchAttack only
            // fires above the availability threshold, so a miss here is defensive.
            int anchor = -1;
            int hwm    = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
                if (IsConscriptable(world, i)) { anchor = i; break; }
            if (anchor < 0) return false;

            FixedVec3 from = world.Position[anchor];

            // Same DW-445 ally-focus preference the raze picker applies, so the wave rallies on a structure the team
            // is not already working on rather than piling onto the ally's current siege.
            int b = FindNearestEnemyBuilding(from, BuildAllyFocusMask(world));
            if (b >= 0) { dest = _buildings.Position[b]; return true; }

            int u = FindNearestEnemyUnit(world, from);
            if (u >= 0) { dest = world.Position[u]; return true; }

            return false;
        }

        /// <summary>
        /// DW-439 — nearest alive HOSTILE unit to <paramref name="from"/> by <see cref="Fixed"/> squared distance,
        /// ascending-id tie-break; -1 if none. Consulted by <see cref="TryResolveWaveDestination"/> (in EVERY match
        /// since DW-783 removed the FFA short-circuit), and deliberately NOT filtered by <c>CanDealDamage</c>: an
        /// enemy base defended only by workers is still somewhere worth marching.
        /// DW-764: the ranking is only correct because <see cref="FixedVec3.SqrDistance"/> now accumulates in
        /// <c>long</c> and saturates — before that fix a hostile past ~181 units wrapped NEGATIVE and won the
        /// argmin, so the wave picked the FARTHEST enemy.
        /// </summary>
        private int FindNearestEnemyUnit(EntityWorld world, FixedVec3 from)
        {
            int   best    = -1;
            Fixed bestSqr = Fixed.Zero;
            int   hwm     = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
            {
                if (!world.IsAlive(i)) continue;
                if (!IsHostile(world.FactionOf[i])) continue;
                Fixed sqrDist = FixedVec3.SqrDistance(from, world.Position[i]);
                if (best < 0 || sqrDist < bestSqr) { best = i; bestSqr = sqrDist; } // strict < ⇒ ascending-id tie-break
            }
            return best;
        }

        /// <summary>
        /// Story 2.13 (AC1.5) — issue an explicit <see cref="UnitCommand.AttackBuilding"/> at the nearest enemy
        /// base for every free Structure-capable unit, so the assault razes it instead of standing inert. Sets
        /// <see cref="EntityWorld.CommandTarget"/> (NOT CommandGoal — <c>TickAttackBuildingCombat</c> reads
        /// CommandTarget); the combat tick then chases into range and applies Fortified matrix damage. Runs inside
        /// the existing float scorer — the building PICK is <see cref="Fixed"/> (deterministic) but no float→Fixed
        /// migration of the scorer itself (Decision 4; the AC1.6 test asserts termination, not byte-determinism).
        /// </summary>
        private void DoRazeBuildings(EntityWorld world)
        {
            // DW-445 (the "modern execution" half of the recorded decision) — computed ONCE per raze action, and only
            // in a teamed match: null in FFA, so this whole path is free and byte-identical there.
            bool[]? allyFocused = BuildAllyFocusMask(world);

            int hwm = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
            {
                if (!IsConscriptable(world, i)) continue; // DW-202 — same bar BuildSnapshot counted
                // Prefer Structure-capable units; one that cannot hit structures would just self-revert via the
                // AttackBuilding guard, so skip it and leave it available (default AttackDomain is All).
                // DW-644: the SHARED predicate — the same one BuildSnapshot counts AvailableRazeCapableUnits with,
                // which is the count ScoreRazeBuildings gates on, so scorer and dispatcher can never disagree again.
                if (!CanRazeStructures(world, i)) continue;

                int b = FindNearestEnemyBuilding(world.Position[i], allyFocused);
                if (b < 0) continue; // no enemy base left for this unit to raze
                world.CommandState[i]  = UnitCommand.AttackBuilding;
                world.CommandTarget[i] = _buildings.PackRef(b); // Story 2.13 D-3: packed building ref (golden-neutral at gen 0)
                world.AttackTarget[i]  = -1;   // a building id must never enter entity-space AttackTarget
            }
        }

        /// <summary>
        /// Story 2.13 — nearest alive ENEMY building to <paramref name="from"/>, by <see cref="Fixed"/> squared
        /// distance, ascending-id tie-break; -1 if none. DW-439/DW-445: "enemy" is now <see cref="IsHostile"/>, so an
        /// ALLIED faction's base is never returned (it used to be, and the order was then rejected by combat's allied
        /// guard, reverting the unit to Idle every tick).
        ///
        /// <para>DW-445's optional ALLY-FOCUS de-duplication rides the same scan: when
        /// <paramref name="allyFocused"/> is non-null (a teamed match only), a structure a teammate is already
        /// force-attacking is de-prioritized, so the team razes two buildings in parallel instead of stacking on one.
        /// It is a PREFERENCE, never a filter — if every remaining hostile structure is already ally-focused the
        /// nearest one is returned anyway, so this can never strand the AI with "no target" (the exact failure mode
        /// DW-445 is about).</para>
        /// </summary>
        private int FindNearestEnemyBuilding(FixedVec3 from, bool[]? allyFocused)
        {
            int   best        = -1;  // nearest hostile building, ally-focused or not — the guaranteed fallback
            Fixed bestSqr     = Fixed.Zero;
            int   bestFree    = -1;  // nearest hostile building NO teammate is already working on
            Fixed bestFreeSqr = Fixed.Zero;

            for (int b = 0; b < _buildings.Count; b++)
            {
                if (!_buildings.Alive[b]) continue;
                if (!IsHostile(_buildings.FactionOf[b])) continue;
                Fixed sqrDist = FixedVec3.SqrDistance(from, _buildings.Position[b]);
                if (best < 0 || sqrDist < bestSqr) { best = b; bestSqr = sqrDist; } // strict < ⇒ ascending-id tie-break

                if (allyFocused == null) continue;                                  // FFA — no preference pass at all
                if (b < allyFocused.Length && allyFocused[b]) continue;              // a teammate already razes this one
                if (bestFree < 0 || sqrDist < bestFreeSqr) { bestFree = b; bestFreeSqr = sqrDist; }
            }

            return bestFree >= 0 ? bestFree : best;
        }

        /// <summary>
        /// DW-445 — mark every building slot a TEAMMATE unit (allied, but not the AI's own faction) is currently
        /// force-attacking, so <see cref="FindNearestEnemyBuilding(FixedVec3, bool[])"/> can prefer a structure the
        /// team is not already chewing on. Returns null when the AI has no teammate — i.e. in EVERY FFA match, every
        /// golden and every existing fixture — which is both the byte-identical fast path and the reason this
        /// allocates nothing there.
        ///
        /// <para>The AI's OWN units are deliberately excluded: their focus is what keeps a wave concentrated, and
        /// de-duplicating against it would scatter one AI's army across a whole base. Scans ascending id and resolves
        /// the packed <see cref="EntityWorld.CommandTarget"/> ref through <c>TryResolveRef</c>, so a stale ref to a
        /// recycled slot marks nothing.</para>
        /// </summary>
        private bool[]? BuildAllyFocusMask(EntityWorld world)
        {
            if (!HasTeammate()) return null;

            int count = _buildings.Count;
            if (count <= 0) return null;

            // One small allocation per raze ACTION (not per tick, and never in FFA) — the raze fallback is a
            // terminal, low-frequency decision, so this stays off every hot path.
            var focused = new bool[count];

            int hwm = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
            {
                if (!world.IsAlive(i)) continue;
                Faction f = world.FactionOf[i];
                if (f == AI_FACTION || f == Faction.Neutral) continue;
                if (_alliances == null || !_alliances.AreAllied(AI_FACTION, f)) continue; // not a teammate
                if (world.CommandState[i] != UnitCommand.AttackBuilding) continue;
                if (_buildings.TryResolveRef(world.CommandTarget[i], out int b) && b < count) focused[b] = true;
            }

            return focused;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void PruneDeadBuildings()
        {
            for (int i = _productionBuildingIds.Count - 1; i >= 0; i--)
            {
                // Story 2.13 (AC3.4): drop any ref that no longer RESOLVES — dead OR recycled (generation bumped) — so
                // the training loop never fires on a slot the AI no longer owns.
                if (!_buildings.TryResolveRef(_productionBuildingIds[i], out _))
                    _productionBuildingIds.RemoveAt(i);
            }
        }

        /// <summary>
        /// Pick up any production buildings the scenario pre-placed for the AI
        /// so they are immediately included in the training loop.
        /// </summary>
        private void AdoptPreplacedBuildings()
        {
            for (int i = 0; i < _buildings.Count; i++)
            {
                if (!_buildings.Alive[i] || _buildings.FactionOf[i] != AI_FACTION) continue;
                var t = _buildings.Type[i];
                if (t == BuildingType.Barracks || t == BuildingType.ArcheryRange || t == BuildingType.SiegeWorkshop)
                    _productionBuildingIds.Add(_buildings.PackRef(i)); // Story 2.13: packed ref (gen 0 at adopt ⇒ == i)
            }
        }
    }
}
