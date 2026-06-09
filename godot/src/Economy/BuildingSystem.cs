#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;

namespace ProjectChimera.Economy
{
    /// <summary>
    /// Handles two building responsibilities each simulation tick:
    ///
    /// 1. Supply cap — recalculates each faction's SupplyCap from alive buildings
    ///    (CommandCenter grants +10 per building).
    ///
    /// 2. Production — ticks training timers; when a timer expires, spawns the
    ///    queued unit near the building's position.
    ///
    /// Building type → unit category mapping:
    ///   Barracks      → first "Melee"  unit in faction
    ///   ArcheryRange  → first "Ranged" unit in faction
    ///   SiegeWorkshop → first "Siege"  unit in faction
    ///
    /// Run BEFORE SupplySystem so SupplyCap is up-to-date when supply is checked.
    /// </summary>
    public class BuildingSystem : ISimSystem
    {
        // Fallback stats used when no FactionDefinition is available
        private const float FALLBACK_TRAIN_TIME  = 8f;
        private const float FALLBACK_HP          = 100f;
        private const float FALLBACK_SPEED       = 4f;
        private const float FALLBACK_ATTACK_DMG  = 10f;
        private const float FALLBACK_ATTACK_RNG  = 5f;
        private const float FALLBACK_ATTACK_SPD  = 1f;
        private const int   FALLBACK_COST_ORE    = 100;

        // Spawn offset (world units) from building centre
        private static readonly Fixed SPAWN_OFFSET = Fixed.FromFloat(3f);

        // Lateral spacing between consecutive spawns from the same building
        private static readonly Fixed SPAWN_SPREAD = Fixed.FromFloat(1.5f);

        private readonly BuildingStore        _buildings;
        private readonly ResourceStore        _resources;
        // Per-faction definitions indexed by (int)Faction. Slot 0 = Neutral (unused).
        private readonly FactionDefinition?[] _factions;
        private readonly MatchStats?           _stats;

        public BuildingSystem(BuildingStore buildings, ResourceStore resources,
                              FactionDefinition? p1Faction = null,
                              FactionDefinition? p2Faction = null,
                              MatchStats?        stats     = null)
        {
            _buildings = buildings;
            _resources = resources;
            _stats     = stats;
            _factions  = new FactionDefinition?[5]; // indices 0-4; Faction enum is 0-4
            _factions[(int)Faction.Player1] = p1Faction;
            _factions[(int)Faction.Player2] = p2Faction;
        }

        private FactionDefinition? GetFactionDef(Faction faction)
        {
            int idx = (int)faction;
            if (idx < 0 || idx >= _factions.Length) return null;
            return _factions[idx];
        }

        /// <summary>
        /// Override the faction definition for a specific faction slot at runtime.
        /// Called by MainScene.ApplyScenario() when loading a scenario that specifies
        /// per-slot faction_json paths (e.g. alpha vs alpha, or beta vs beta maps).
        /// </summary>
        public void SetFactionDef(Faction faction, FactionDefinition def)
        {
            int idx = (int)faction;
            if (idx >= 0 && idx < _factions.Length)
                _factions[idx] = def;
        }

        public void Tick(EntityWorld world, Fixed dt)
        {
            TickConstruction(dt);
            RecalculateSupplyCaps();
            TickProduction(world, dt);
            TickWorkerArrival(world);
        }

        // ── Construction ──────────────────────────────────────────────────────

        private void TickConstruction(Fixed dt)
        {
            for (int i = 0; i < _buildings.Count; i++)
            {
                if (!_buildings.Alive[i]) continue;
                if (_buildings.ConstructionTimer[i] <= Fixed.Zero) continue;

                _buildings.ConstructionTimer[i] -= dt;
                if (_buildings.ConstructionTimer[i] < Fixed.Zero)
                    _buildings.ConstructionTimer[i] = Fixed.Zero;
            }
        }

        // ── Supply cap ────────────────────────────────────────────────────────

        private void RecalculateSupplyCaps()
        {
            // Reset to base cap
            for (int f = 1; f <= 4; f++)
                _resources.SupplyCap[f] = ResourceStore.STARTING_SUPPLY_CAP;

            for (int i = 0; i < _buildings.Count; i++)
            {
                if (!_buildings.Alive[i]) continue;
                if (_buildings.IsUnderConstruction(i)) continue; // not functional yet
                int f = (int)_buildings.FactionOf[i];
                _resources.SupplyCap[f] += _buildings.SupplyBonus[i];
            }
        }

        // ── Production ────────────────────────────────────────────────────────

        private void TickProduction(EntityWorld world, Fixed dt)
        {
            for (int i = 0; i < _buildings.Count; i++)
            {
                if (!_buildings.Alive[i]) continue;
                if (_buildings.IsUnderConstruction(i)) continue; // can't produce yet
                if (_buildings.ProductionQueue[i] == 0) continue;
                if (_buildings.ProductionTimer[i] <= Fixed.Zero) continue;

                _buildings.ProductionTimer[i] = _buildings.ProductionTimer[i] - dt;

                if (_buildings.ProductionTimer[i] <= Fixed.Zero)
                {
                    // Training complete — spawn unit
                    SpawnTrainedUnit(world, i);
                    _buildings.ProductionQueue[i] = 0;
                    _buildings.ProductionTimer[i] = Fixed.Zero;
                }
            }
        }

        private void SpawnTrainedUnit(EntityWorld world, int buildingId)
        {
            Faction faction = _buildings.FactionOf[buildingId];
            var def = GetProductionUnit(_buildings.Type[buildingId], faction);

            float hp          = def?.Hp           ?? FALLBACK_HP;
            float speed       = def?.Speed        ?? FALLBACK_SPEED;
            float atkDmg      = def?.AttackDamage ?? FALLBACK_ATTACK_DMG;
            float atkRng      = def?.AttackRange  ?? FALLBACK_ATTACK_RNG;
            float atkSpd      = def?.AttackSpeed  ?? FALLBACK_ATTACK_SPD;
            float vision      = def?.VisionRange  ?? 8f;
            float splashRadius = def?.SplashRadius ?? 0f;
            byte  supply      = (byte)(def?.Supply ?? 1);
            var   dmgType     = def?.ParsedDamageType ?? Combat.DamageType.Normal;
            var   armType     = def?.ParsedArmorType  ?? Combat.ArmorType.Light;

            // Place unit slightly in front of building (along +X for P1, -X for P2).
            // The Z offset cycles per trained unit: units that hold position (Stop)
            // would otherwise spawn on the exact same fixed-point coordinate, and
            // MovementSystem separation skips exactly-overlapping pairs.
            int trained = _buildings.TrainedCount[buildingId]++;
            Fixed offsetX = faction == Faction.Player1 ? SPAWN_OFFSET : -SPAWN_OFFSET;
            Fixed offsetZ = SPAWN_SPREAD * Fixed.FromInt((trained % 5) - 2);
            FixedVec3 spawnPos = new FixedVec3(
                _buildings.Position[buildingId].X + offsetX,
                Fixed.Zero,
                _buildings.Position[buildingId].Z + offsetZ);

            int id = world.Create(spawnPos, faction,
                Fixed.FromFloat(hp), Fixed.FromFloat(speed));

            if (id < 0) return; // EntityWorld full

            _stats?.RecordUnitBuilt(faction);

            world.SupplyCost[id]    = supply;
            world.AttackRange[id]   = Fixed.FromFloat(atkRng);
            world.AttackDamage[id]  = Fixed.FromFloat(atkDmg);
            world.AttackSpeed[id]   = Fixed.FromFloat(atkSpd);
            world.DamageTypeOf[id]  = dmgType;
            world.ArmorTypeOf[id]   = armType;
            world.VisionRange[id]   = Fixed.FromFloat(vision);
            world.SplashRadius[id]  = Fixed.FromFloat(splashRadius);

            // Walk to rally point if the building has one set
            if (_buildings.HasRallyPoint[buildingId])
            {
                world.CommandState[id] = UnitCommand.Move;
                world.CommandGoal[id]  = _buildings.RallyPoint[buildingId];
                world.MoveTarget[id]   = _buildings.RallyPoint[buildingId];
            }
            else
            {
                // Hold position at the spawn point — Idle would send the unit
                // chasing the nearest enemy across the map (global chase).
                world.CommandState[id] = UnitCommand.Stop;
            }
        }

        // ── Category mapping ──────────────────────────────────────────────────

        /// <summary>Returns the unit category string produced by the given building type.</summary>
        private static string CategoryForBuilding(BuildingType type) => type switch
        {
            BuildingType.Barracks      => "Melee",
            BuildingType.ArcheryRange  => "Ranged",
            BuildingType.SiegeWorkshop => "Siege",
            BuildingType.CommandCenter => "Worker",
            _                          => "Melee",
        };

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the UnitDefinition that the given building type will produce for the
        /// specified faction, or null if there is no matching unit in that faction's definition.
        /// Defaults to Player1 when called from UI systems that don't have faction context.
        /// </summary>
        public UnitDefinition? GetProductionUnit(BuildingType type,
                                                  Faction faction = Faction.Player1)
        {
            if (type == BuildingType.CommandCenter) return null;
            return GetFactionDef(faction)?.GetUnitByCategory(CategoryForBuilding(type));
        }

        /// <summary>
        /// Queue a unit for training at the given production building.
        /// Returns true if training was accepted (building alive, idle, prereqs met, faction can afford).
        /// Automatically picks the correct unit type for the building (Barracks→Melee,
        /// ArcheryRange→Ranged, SiegeWorkshop→Siege).
        /// </summary>
        public bool TrainUnit(int buildingId, ResourceStore resources)
        {
            if (buildingId < 0 || buildingId >= _buildings.Count) return false;
            if (!_buildings.Alive[buildingId]) return false;
            if (_buildings.IsUnderConstruction(buildingId)) return false;
            var bType = _buildings.Type[buildingId];
            if (bType == BuildingType.CommandCenter) return false;
            if (_buildings.ProductionQueue[buildingId] != 0) return false; // already training

            Faction faction = _buildings.FactionOf[buildingId];
            var def = GetProductionUnit(bType, faction);

            // Tech tree: check prerequisites before spending resources
            if (def != null && !TechTreeChecker.AreMet(_buildings, faction, def.Prerequisites))
                return false;

            // Supply cap: don't queue if the faction is already at cap
            byte supply = (byte)(def?.Supply ?? 1);
            if (!resources.HasSupply(faction, supply)) return false;

            float costOre = def?.CostOre ?? FALLBACK_COST_ORE;
            if (!resources.SpendOre(faction, Fixed.FromFloat(costOre))) return false;

            _buildings.ProductionQueue[buildingId] = 1;
            float trainTime = def?.TrainTime ?? FALLBACK_TRAIN_TIME;
            _buildings.ProductionTimer[buildingId] = Fixed.FromFloat(trainTime);
            return true;
        }

        /// <summary>
        /// Place a building on behalf of a scenario loader or editor tool.
        /// Bypasses ore cost. When <paramref name="preBuilt"/> is true the construction
        /// timer is set to zero so the building is immediately operational.
        /// Returns the building ID, or -1 if the store is full.
        /// </summary>
        public int PlaceBuildingDirect(BuildingType type, Faction faction,
                                       FixedVec3 position, bool preBuilt)
        {
            int id = _buildings.Create(position, faction, type);
            if (id < 0) return -1;
            if (preBuilt)
                _buildings.ConstructionTimer[id] = Fixed.Zero;
            return id;
        }

        // ── Worker construction ───────────────────────────────────────────────

        /// <summary>
        /// Squared distance threshold for "worker has arrived at build site".
        /// 3 world-unit radius — generous so the worker doesn't need to step
        /// into the exact centre of a 4×4 building footprint.
        /// </summary>
        private static readonly Fixed WORKER_BUILD_ARRIVE_SQR =
            Fixed.FromFloat(3f) * Fixed.FromFloat(3f);

        /// <summary>
        /// Each tick: check workers whose CommandState is Build. If they have arrived
        /// at their target site (or the target was destroyed), clear the Build command
        /// and return them to gathering (GatherState.Idle).
        /// </summary>
        private void TickWorkerArrival(EntityWorld world)
        {
            int hwm = world.HighWaterMark;
            for (int i = 0; i < hwm; i++)
            {
                if (!world.IsAlive(i)) continue;
                if (world.CommandState[i] != UnitCommand.Build) continue;

                int bId = world.BuildTarget[i];

                // Target building destroyed — cancel silently
                if (bId < 0 || !_buildings.Alive[bId])
                {
                    ClearWorkerBuild(world, i);
                    continue;
                }

                // Check proximity to building centre
                Fixed sqr = FixedVec3.SqrDistance(world.Position[i], _buildings.Position[bId]);
                if (sqr <= WORKER_BUILD_ARRIVE_SQR)
                    ClearWorkerBuild(world, i);
            }
        }

        /// <summary>Clear the Build command on a worker and resume gathering.</summary>
        private static void ClearWorkerBuild(EntityWorld world, int workerId)
        {
            world.CommandState[workerId] = UnitCommand.Idle;
            world.BuildTarget[workerId]  = -1;
            // Resume the gather loop; GatheringSystem will assign a node next tick.
            world.GatherState[workerId]  = GatherState.Idle;
        }

        /// <summary>
        /// Command a worker to construct a building at <paramref name="position"/>.
        ///
        /// Deducts ore, places the building under construction, assigns the Build
        /// command to the worker, and sets MoveTarget so MovementSystem walks them
        /// to the site. Construction ticks automatically in TickConstruction —
        /// the worker just needs to arrive to clear the command.
        ///
        /// Returns the new building ID, or -1 if placement failed (full store,
        /// insufficient ore, unmet prerequisites, or invalid worker).
        /// </summary>
        public int QueueWorkerBuild(int workerId, BuildingType type, FixedVec3 position,
                                    Faction faction, ResourceStore resources, EntityWorld world)
        {
            if (!world.IsAlive(workerId)) return -1;
            if (world.GatherState[workerId] == GatherState.Inactive) return -1; // not a worker

            // Prerequisite check
            string[]? prereqs = GetFactionDef(faction)
                ?.GetBuilding(TechTreeChecker.BuildingTypeId(type))?.Prerequisites;
            if (!TechTreeChecker.AreMet(_buildings, faction, prereqs)) return -1;

            // Ore cost
            float costOre = GetBuildingCost(type, faction);
            if (costOre > 0f && !resources.SpendOre(faction, Fixed.FromFloat(costOre))) return -1;

            // Place building (starts under construction)
            int bId = _buildings.Create(position, faction, type);
            if (bId < 0)
            {
                if (costOre > 0f) resources.AddOre(faction, Fixed.FromFloat(costOre)); // refund
                return -1;
            }

            // Release current node assignment (slot count will reconcile next gather tick)
            world.GatherTarget[workerId] = -1;

            // Issue Build command — MovementSystem moves the worker, GatheringSystem skips them
            world.BuildTarget[workerId]  = bId;
            world.CommandState[workerId] = UnitCommand.Build;
            world.MoveTarget[workerId]   = position;
            world.Flags[workerId]       |= EntityFlags.Moving;

            return bId;
        }

        /// <summary>
        /// Returns the ore cost to place the given building type for the faction.
        /// Falls back to 0 when no definition is found (allowing free placement).
        /// </summary>
        public float GetBuildingCost(BuildingType type, Faction faction)
        {
            string id = TechTreeChecker.BuildingTypeId(type);
            return GetFactionDef(faction)?.GetBuilding(id)?.CostOre ?? 0f;
        }

        /// <summary>
        /// Returns the display name of the first unmet building prerequisite for
        /// placing the given type, or null if all prerequisites are satisfied.
        /// Used by CommandCardSystem to show "[need: X]" on build buttons.
        /// </summary>
        public string? GetBuildingPlacePrereq(BuildingType type, Faction faction)
        {
            string id = TechTreeChecker.BuildingTypeId(type);
            var prereqs = GetFactionDef(faction)?.GetBuilding(id)?.Prerequisites;
            return TechTreeChecker.FirstMissing(_buildings, faction, prereqs);
        }

        /// <summary>
        /// Returns the human-readable name of the first unmet tech prerequisite for
        /// the production unit of this building, or null if all prerequisites are satisfied.
        /// Used by CommandCardSystem to show "[need: X]" feedback.
        /// </summary>
        public string? GetUnmetPrereq(int buildingId)
        {
            if (buildingId < 0 || buildingId >= _buildings.Count) return null;
            Faction faction = _buildings.FactionOf[buildingId];
            var def = GetProductionUnit(_buildings.Type[buildingId], faction);
            if (def == null) return null;
            return TechTreeChecker.FirstMissing(_buildings, faction, def.Prerequisites);
        }
    }
}
