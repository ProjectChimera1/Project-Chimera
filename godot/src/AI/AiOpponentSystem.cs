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

        // Ore costs — must match EntityPlacer.BUILDING_COSTS and faction JSON.
        private static readonly Fixed COST_CC       = Fixed.FromFloat(150f);
        private static readonly Fixed COST_BARRACKS  = Fixed.FromFloat(100f);
        private static readonly Fixed COST_ARCHERY   = Fixed.FromFloat(120f);
        private static readonly Fixed COST_SIEGE     = Fixed.FromFloat(200f);

        // Supply headroom thresholds that trigger expansion scoring.
        private const int SUPPLY_CRITICAL = 0;
        private const int SUPPLY_TIGHT    = 2;
        private const int SUPPLY_LOW      = 4;

        // Placement positions for AI-built structures (clustered near the P2 base).
        private static readonly FixedVec3 POS_BARRACKS_1   = new(Fixed.FromFloat( 36f), Fixed.Zero, Fixed.FromFloat(  6f));
        private static readonly FixedVec3 POS_BARRACKS_2   = new(Fixed.FromFloat( 36f), Fixed.Zero, Fixed.FromFloat( -6f));
        private static readonly FixedVec3 POS_ARCHERY      = new(Fixed.FromFloat( 42f), Fixed.Zero, Fixed.FromFloat( 12f));
        private static readonly FixedVec3 POS_SIEGE        = new(Fixed.FromFloat( 42f), Fixed.Zero, Fixed.FromFloat(-12f));
        private static readonly FixedVec3 POS_CC_EXPANSION = new(Fixed.FromFloat( 54f), Fixed.Zero, Fixed.Zero);
        private static readonly FixedVec3 P1_BASE          = new(Fixed.FromFloat(-45f), Fixed.Zero, Fixed.Zero);

        // ── Difficulty weights ────────────────────────────────────────────────

        private readonly float _aggressionWeight; // scales attack score (0–1)
        private readonly float _techWeight;        // scales tech-up scores  (0–1)
        private readonly int   _attackThreshold;  // available (Idle/Stop) units needed before considering an attack wave
        private readonly Fixed _attackCooldownMax;

        // ── Dependencies ──────────────────────────────────────────────────────

        private readonly BuildingStore  _buildings;
        private readonly ResourceStore  _resources;
        private readonly BuildingSystem _buildSys;

        // ── Tracked state ─────────────────────────────────────────────────────

        /// <summary>IDs of all AI-owned production buildings (Barracks / ArcheryRange / SiegeWorkshop).</summary>
        private readonly List<int> _productionBuildingIds = new();

        /// <summary>ID of the supply-expansion CommandCenter (-1 = not yet committed).</summary>
        private int _cmdCenterExpId = -1;

        private Fixed _attackCooldown = Fixed.Zero;

        // ── Constructor ───────────────────────────────────────────────────────

        public AiOpponentSystem(BuildingStore buildings, ResourceStore resources,
                                BuildingSystem buildSys,
                                AiDifficulty difficulty = AiDifficulty.Normal)
        {
            _buildings = buildings;
            _resources = resources;
            _buildSys  = buildSys;

            (_aggressionWeight, _techWeight, _attackThreshold, float cooldownS) = difficulty switch
            {
                AiDifficulty.Easy => (0.40f, 0.50f, 8, 40f),
                AiDifficulty.Hard => (0.90f, 0.90f, 3, 15f),
                _                 => (0.65f, 0.70f, 5, 25f), // Normal
            };
            _attackCooldownMax = Fixed.FromFloat(cooldownS);

            // Adopt any production buildings the scenario pre-placed for the AI.
            AdoptPreplacedBuildings();
        }

        // ── ISimSystem ────────────────────────────────────────────────────────

        public void Tick(EntityWorld world, Fixed dt)
        {
            PruneDeadBuildings();

            // Continuous training — always drain idle production buildings first.
            foreach (int id in _productionBuildingIds)
            {
                if (_buildings.Alive[id] && !_buildings.IsUnderConstruction(id))
                    _buildSys.TrainUnit(id, _resources);
            }

            // Tick attack cooldown.
            if (_attackCooldown > Fixed.Zero)
                _attackCooldown -= dt;

            // Score and execute the best strategic action this tick.
            AiSnapshot snap = BuildSnapshot(world);
            ExecuteBestAction(snap, world);
        }

        // ── Snapshot ──────────────────────────────────────────────────────────

        private struct AiSnapshot
        {
            public int  SupplyHeadroom;
            public int  AvailableCombatUnits; // Idle or Stop — not under orders, conscriptable into a wave
            public bool HasLiveBarracks;
            public bool HasCompleteBarracks;
            public bool HasLiveArcheryRange;
            public bool HasCompleteArcheryRange;
            public bool HasLiveSiegeWorkshop;
            public bool HasCompleteSiegeWorkshop;
            public bool HasSecondBarracks;       // two or more complete Barracks
            public bool HasCCExpansion;          // supply expansion CC alive
            public bool CanAffordCC;
            public bool CanAffordBarracks;
            public bool CanAffordArchery;
            public bool CanAffordSiege;
        }

        private AiSnapshot BuildSnapshot(EntityWorld world)
        {
            var snap = new AiSnapshot();

            int fIdx = (int)AI_FACTION;
            snap.SupplyHeadroom = _resources.SupplyCap[fIdx] - _resources.SupplyUsed[fIdx];

            // Scan buildings for tech coverage.
            int barracksComplete = 0;
            for (int i = 0; i < _buildings.Count; i++)
            {
                if (!_buildings.Alive[i] || _buildings.FactionOf[i] != AI_FACTION) continue;
                bool complete = !_buildings.IsUnderConstruction(i);
                switch (_buildings.Type[i])
                {
                    case BuildingType.Barracks:
                        snap.HasLiveBarracks = true;
                        if (complete) { snap.HasCompleteBarracks = true; barracksComplete++; }
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
            snap.HasSecondBarracks = barracksComplete >= 2;
            snap.HasCCExpansion    = _cmdCenterExpId >= 0 && _buildings.Alive[_cmdCenterExpId];

            // Count P2 combat units (non-workers) available for a wave.
            // Freshly trained units hold position (Stop) at the spawn point;
            // veterans whose AttackMove completed sit at Idle wherever the
            // wave ended. Both are conscriptable into the next wave.
            int hwm = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
            {
                if (!world.IsAlive(i)) continue;
                if (world.FactionOf[i]   != AI_FACTION)         continue;
                if (world.GatherState[i] != GatherState.Inactive) continue;
                if (world.CommandState[i] == UnitCommand.Idle ||
                    world.CommandState[i] == UnitCommand.Stop)
                    snap.AvailableCombatUnits++;
            }

            snap.CanAffordCC       = _resources.CanAffordOre(AI_FACTION, COST_CC);
            snap.CanAffordBarracks  = _resources.CanAffordOre(AI_FACTION, COST_BARRACKS);
            snap.CanAffordArchery  = _resources.CanAffordOre(AI_FACTION, COST_ARCHERY);
            snap.CanAffordSiege    = _resources.CanAffordOre(AI_FACTION, COST_SIEGE);

            return snap;
        }

        // ── Scoring ───────────────────────────────────────────────────────────
        //
        // Each function returns a priority score in [0, 1].
        // Gate conditions that make the action impossible return 0.
        // The highest-scoring action is executed once per tick.

        private float ScoreExpandSupply(in AiSnapshot s)
        {
            if (_cmdCenterExpId >= 0) return 0f; // already committed (building or built)
            if (!s.CanAffordCC) return 0f;
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

        private float ScoreBuildSecondBarracks(in AiSnapshot s)
        {
            if (s.HasSecondBarracks)     return 0f;
            if (!s.HasCCExpansion)       return 0f; // double production only after supply expands
            if (!s.CanAffordBarracks)    return 0f;
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

            switch (chosen)
            {
                case StrategicAction.ExpandSupplyCap:    DoExpandSupplyCap();    break;
                case StrategicAction.BuildBarracks:       DoBuildBarracks(false); break;
                case StrategicAction.BuildArcheryRange:   DoBuildArcheryRange();  break;
                case StrategicAction.BuildSiegeWorkshop:  DoBuildSiege();         break;
                case StrategicAction.BuildSecondBarracks: DoBuildBarracks(true);  break;
                case StrategicAction.LaunchAttack:        DoLaunchAttack(world);  break;
            }
        }

        // ── Action implementations ────────────────────────────────────────────

        private void DoExpandSupplyCap()
        {
            if (!_resources.SpendOre(AI_FACTION, COST_CC)) return;
            int id = _buildings.Create(POS_CC_EXPANSION, AI_FACTION, BuildingType.CommandCenter);
            if (id < 0) _resources.AddOre(AI_FACTION, COST_CC); // store full — refund
            else        _cmdCenterExpId = id;
        }

        private void DoBuildBarracks(bool isSecond)
        {
            if (!_resources.SpendOre(AI_FACTION, COST_BARRACKS)) return;
            FixedVec3 pos = isSecond ? POS_BARRACKS_2 : POS_BARRACKS_1;
            int id = _buildings.Create(pos, AI_FACTION, BuildingType.Barracks);
            if (id < 0) _resources.AddOre(AI_FACTION, COST_BARRACKS);
            else        _productionBuildingIds.Add(id);
        }

        private void DoBuildArcheryRange()
        {
            if (!_resources.SpendOre(AI_FACTION, COST_ARCHERY)) return;
            int id = _buildings.Create(POS_ARCHERY, AI_FACTION, BuildingType.ArcheryRange);
            if (id < 0) _resources.AddOre(AI_FACTION, COST_ARCHERY);
            else        _productionBuildingIds.Add(id);
        }

        private void DoBuildSiege()
        {
            if (!_resources.SpendOre(AI_FACTION, COST_SIEGE)) return;
            int id = _buildings.Create(POS_SIEGE, AI_FACTION, BuildingType.SiegeWorkshop);
            if (id < 0) _resources.AddOre(AI_FACTION, COST_SIEGE);
            else        _productionBuildingIds.Add(id);
        }

        private void DoLaunchAttack(EntityWorld world)
        {
            int hwm = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
            {
                if (!world.IsAlive(i))                              continue;
                if (world.FactionOf[i]   != AI_FACTION)            continue;
                if (world.GatherState[i] != GatherState.Inactive)  continue;
                if (world.CommandState[i] != UnitCommand.Idle &&
                    world.CommandState[i] != UnitCommand.Stop)     continue;
                world.CommandState[i] = UnitCommand.AttackMove;
                world.CommandGoal[i]  = P1_BASE;
                world.MoveTarget[i]   = P1_BASE;
            }
            _attackCooldown = _attackCooldownMax;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void PruneDeadBuildings()
        {
            for (int i = _productionBuildingIds.Count - 1; i >= 0; i--)
            {
                if (!_buildings.Alive[_productionBuildingIds[i]])
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
                    _productionBuildingIds.Add(i);
            }
        }
    }
}
