using ProjectChimera.Core;
using ProjectChimera.Economy;

namespace ProjectChimera.AI
{
    /// <summary>
    /// Difficulty presets for the AI opponent.
    /// Controls attack threshold, attack cooldown, and economic aggression.
    /// </summary>
    public enum AiDifficulty { Easy, Normal, Hard }

    /// <summary>
    /// Rule-based AI opponent for Player 2.
    ///
    /// State machine:
    ///   EarlyEconomy    — workers gather; wait for enough ore to build a Barracks.
    ///   BuildingBarracks — wait for Barracks construction to complete.
    ///   Training         — continuous training loop with three sub-behaviours:
    ///                       1. Supply expansion: build a CommandCenter when supply runs low.
    ///                       2. Second Barracks:  build a 2nd Barracks once CC expansion is live.
    ///                       3. Attack waves:     dispatch AttackMove when enough idle units
    ///                                            are staged and the cooldown has elapsed.
    ///
    /// Difficulty (Easy / Normal / Hard) adjusts attack threshold and cooldown.
    ///
    /// Run AFTER BuildingSystem in the sim loop so SupplyCap and construction timers
    /// are already updated before the AI makes decisions.
    /// </summary>
    public class AiOpponentSystem : ISimSystem
    {
        // ── Fixed constants ───────────────────────────────────────────────────

        private const Faction AI_FACTION     = Faction.Player2;
        private const float   BARRACKS_COST  = 100f;   // ore cost for any Barracks placement
        private const float   CC_EXPAND_COST = 150f;   // ore cost for the expansion CC
        private const int     SUPPLY_HEADROOM = 4;     // expand supply when (cap − used) ≤ this

        /// <summary>First Barracks placement — near the P2 base.</summary>
        private static readonly FixedVec3 BARRACKS_POS  = new(Fixed.FromFloat(36f),  Fixed.Zero, Fixed.FromFloat( 6f));
        /// <summary>Second Barracks — mirrored Z from the first.</summary>
        private static readonly FixedVec3 BARRACKS2_POS = new(Fixed.FromFloat(36f),  Fixed.Zero, Fixed.FromFloat(-6f));
        /// <summary>Expansion CommandCenter — behind the P2 main base.</summary>
        private static readonly FixedVec3 CC_EXPAND_POS = new(Fixed.FromFloat(54f),  Fixed.Zero, Fixed.Zero);
        /// <summary>Target for attack waves — the P1 base.</summary>
        private static readonly FixedVec3 P1_BASE       = new(Fixed.FromFloat(-45f), Fixed.Zero, Fixed.Zero);

        // ── State machine ─────────────────────────────────────────────────────

        private enum AiPhase { EarlyEconomy, BuildingBarracks, Training }

        // ── Fields ────────────────────────────────────────────────────────────

        private readonly BuildingStore  _buildings;
        private readonly ResourceStore  _resources;
        private readonly BuildingSystem _buildSys;

        // Per-difficulty settings (set in constructor)
        private readonly int   _attackThreshold;    // idle combat units required to trigger a wave
        private readonly Fixed _attackCooldownMax;  // seconds between waves

        // State
        private AiPhase _phase          = AiPhase.EarlyEconomy;
        private int     _barracksId     = -1;  // primary Barracks (-1 = not started)
        private int     _barracks2Id    = -1;  // second  Barracks (-1 = not started)
        private int     _cmdCenterExpId = -1;  // expansion CC     (-1 = not started)
        private Fixed   _attackCooldown = Fixed.Zero;

        // ── Constructor ───────────────────────────────────────────────────────

        public AiOpponentSystem(BuildingStore buildings, ResourceStore resources,
                                BuildingSystem buildSys,
                                AiDifficulty difficulty = AiDifficulty.Normal)
        {
            _buildings = buildings;
            _resources = resources;
            _buildSys  = buildSys;

            (int threshold, float cooldownS) = difficulty switch
            {
                AiDifficulty.Easy => (8, 40f),
                AiDifficulty.Hard => (3, 15f),
                _                 => (5, 25f),  // Normal
            };
            _attackThreshold  = threshold;
            _attackCooldownMax = Fixed.FromFloat(cooldownS);
        }

        // ── ISimSystem ────────────────────────────────────────────────────────

        public void Tick(EntityWorld world, Fixed dt)
        {
            switch (_phase)
            {
                case AiPhase.EarlyEconomy:     TickEarlyEconomy();       break;
                case AiPhase.BuildingBarracks:  TickBuildingBarracks();   break;
                case AiPhase.Training:          TickTraining(world, dt);  break;
            }
        }

        // ── Phase handlers ────────────────────────────────────────────────────

        /// <summary>Wait until we can afford a Barracks, then place it.</summary>
        private void TickEarlyEconomy()
        {
            if (!_resources.SpendOre(AI_FACTION, Fixed.FromFloat(BARRACKS_COST))) return;

            _barracksId = _buildings.Create(BARRACKS_POS, AI_FACTION, BuildingType.Barracks);
            if (_barracksId < 0)
            {
                // BuildingStore full — refund and wait
                _resources.AddOre(AI_FACTION, Fixed.FromFloat(BARRACKS_COST));
                return;
            }
            _phase = AiPhase.BuildingBarracks;
        }

        /// <summary>Wait until Barracks construction finishes, then begin training.</summary>
        private void TickBuildingBarracks()
        {
            if (_barracksId < 0 || !_buildings.Alive[_barracksId])
            {
                _phase = AiPhase.EarlyEconomy;
                return;
            }
            if (!_buildings.IsUnderConstruction(_barracksId))
                _phase = AiPhase.Training;
        }

        /// <summary>
        /// Main training loop: supply expansion, second Barracks, unit training, attack waves.
        /// Resets to EarlyEconomy if the primary Barracks is destroyed.
        /// </summary>
        private void TickTraining(EntityWorld world, Fixed dt)
        {
            if (_barracksId < 0 || !_buildings.Alive[_barracksId])
            {
                _phase = AiPhase.EarlyEconomy;
                return;
            }

            TryExpandSupplyCap();
            TryBuildSecondBarracks();

            // Train from primary Barracks (no-op if already training, supply full, or can't afford)
            _buildSys.TrainUnit(_barracksId, _resources);

            // Train from second Barracks if it's alive and complete
            if (_barracks2Id >= 0
                && _buildings.Alive[_barracks2Id]
                && !_buildings.IsUnderConstruction(_barracks2Id))
                _buildSys.TrainUnit(_barracks2Id, _resources);

            // Tick attack cooldown
            if (_attackCooldown > Fixed.Zero)
                _attackCooldown -= dt;

            // Dispatch wave when threshold met and cooldown elapsed
            if (_attackCooldown <= Fixed.Zero && CountIdleCombatUnits(world) >= _attackThreshold)
            {
                SendAttackWave(world);
                _attackCooldown = _attackCooldownMax;
            }
        }

        // ── Expansion sub-behaviours ──────────────────────────────────────────

        /// <summary>
        /// Build an expansion CommandCenter when supply is running low.
        /// Only one expansion CC is ever attempted; destruction doesn't trigger a rebuild
        /// (keeps the AI from haemorrhaging ore in the late game when the CC gets targeted).
        /// </summary>
        private void TryExpandSupplyCap()
        {
            if (_cmdCenterExpId >= 0) return;  // already placed or in progress

            int used = _resources.SupplyUsed[(int)AI_FACTION];
            int cap  = _resources.SupplyCap[(int)AI_FACTION];
            if (cap - used > SUPPLY_HEADROOM) return;  // supply not tight yet

            if (!_resources.SpendOre(AI_FACTION, Fixed.FromFloat(CC_EXPAND_COST))) return;

            _cmdCenterExpId = _buildings.Create(CC_EXPAND_POS, AI_FACTION, BuildingType.CommandCenter);
            if (_cmdCenterExpId < 0)
                _resources.AddOre(AI_FACTION, Fixed.FromFloat(CC_EXPAND_COST));  // BuildingStore full
        }

        /// <summary>
        /// Build a second Barracks once the expansion CC is alive and fully constructed.
        /// Double production rate is more cost-effective than any other spend at this point.
        /// </summary>
        private void TryBuildSecondBarracks()
        {
            if (_barracks2Id >= 0) return;  // already placed

            // Gate: CC expansion must be complete first
            if (_cmdCenterExpId < 0) return;
            if (!_buildings.Alive[_cmdCenterExpId]) return;
            if (_buildings.IsUnderConstruction(_cmdCenterExpId)) return;

            if (!_resources.SpendOre(AI_FACTION, Fixed.FromFloat(BARRACKS_COST))) return;

            _barracks2Id = _buildings.Create(BARRACKS2_POS, AI_FACTION, BuildingType.Barracks);
            if (_barracks2Id < 0)
                _resources.AddOre(AI_FACTION, Fixed.FromFloat(BARRACKS_COST));
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Count alive P2 combat units (non-workers) that are currently Idle.</summary>
        private int CountIdleCombatUnits(EntityWorld world)
        {
            int count = 0;
            int hwm   = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
            {
                if (!world.IsAlive(i))                              continue;
                if (world.FactionOf[i]   != AI_FACTION)            continue;
                if (world.GatherState[i] != GatherState.Inactive)  continue; // skip workers
                if (world.CommandState[i] == UnitCommand.Idle)     count++;
            }
            return count;
        }

        /// <summary>
        /// Issue an AttackMove order toward the P1 base for every idle P2 combat unit.
        /// Units already in motion (Move / AttackMove / Stop / Hold) are left undisturbed.
        /// </summary>
        private void SendAttackWave(EntityWorld world)
        {
            int hwm = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
            {
                if (!world.IsAlive(i))                              continue;
                if (world.FactionOf[i]   != AI_FACTION)            continue;
                if (world.GatherState[i] != GatherState.Inactive)  continue; // skip workers
                if (world.CommandState[i] != UnitCommand.Idle)     continue; // already ordered

                world.CommandState[i] = UnitCommand.AttackMove;
                world.CommandGoal[i]  = P1_BASE;
                world.MoveTarget[i]   = P1_BASE;
            }
        }
    }
}
