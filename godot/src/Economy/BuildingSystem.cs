#nullable enable
using System.Collections.Generic;
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
    /// Building type → unit category mapping (Story 2.8: the player picks WHICH unit of the
    /// category to train via the command card, not just the first):
    ///   Barracks      → "Melee"  units
    ///   ArcheryRange  → "Ranged" units
    ///   SiegeWorkshop → "Siege"  units
    ///   Aviary        → "Air"    units
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

        /// <summary>
        /// ProductionQueue sentinel (Story 2.8). A producer whose category has ZERO units still trains a graceful
        /// FALLBACK unit — that case is persisted as this reserved value, NOT 0, because 0 must keep meaning "idle"
        /// (the already-training guard at TrainUnit / the skip at TickProduction). Real chosen units are stored as
        /// (Units-list index + 1); the category guard bounds real indices well under 254, so index+1 never collides
        /// with 255.
        /// </summary>
        private const byte PRODUCTION_FALLBACK = byte.MaxValue;

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

            // Story 2.8: train the CONCRETE unit chosen at TrainUnit time — persisted in ProductionQueue as
            // (Units index + 1), or PRODUCTION_FALLBACK for an empty-category producer. Read it here instead of
            // re-deriving first-of-category. Explicitly bounds-check the List indexer: the null-conditional
            // guards only a null FactionDefinition, NOT an out-of-range index (which would throw
            // ArgumentOutOfRangeException inside the tick — a "never throw in the tick" violation).
            byte q = _buildings.ProductionQueue[buildingId];
            var fdef = GetFactionDef(faction);
            UnitDefinition? def = null;
            int meshType = -1;
            if (q != PRODUCTION_FALLBACK && q != 0 && fdef != null)
            {
                int idx = q - 1;
                if (idx >= 0 && idx < fdef.Units.Count)
                {
                    def = fdef.Units[idx];
                    meshType = idx; // the Units index IS the MeshType coordinate (== IndexOfUnit)
                }
            }
            // def == null → the graceful FALLBACK branch below (the 255 sentinel, or a stale index after a
            // SetFactionDef faction swap). Never throws.

            float hp    = def?.Hp    ?? FALLBACK_HP;
            float speed = def?.Speed ?? FALLBACK_SPEED;

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

            // Copy the production def's per-entity fields via the SINGLE shared mapper (Story 1.13 review fix) — so a
            // trained unit gets its authored collision_radius / separation_priority / Category instead of the Create()
            // defaults, exactly like a scenario-placed unit. When no def resolves (missing/degenerate faction data),
            // keep the legacy fallback combat stats; the separation/formation fields then stay at their Create defaults.
            if (def != null)
            {
                world.ApplyUnitDefinition(id, def);
            }
            else
            {
                world.SupplyCost[id]   = 1;
                world.AttackRange[id]  = Fixed.FromFloat(FALLBACK_ATTACK_RNG);
                // Story 2.2a (A2): non-mapper write sets BOTH Base (authored source) and Effective, so a later
                // modifier recomputes Effective = Base + delta correctly instead of from a stale Zero base.
                world.BaseAttackDamage[id]      = Fixed.FromFloat(FALLBACK_ATTACK_DMG);
                world.EffectiveAttackDamage[id] = world.BaseAttackDamage[id];
                world.AttackSpeed[id]  = Fixed.FromFloat(FALLBACK_ATTACK_SPD);
                world.DamageTypeOf[id] = Combat.DamageType.Normal;
                world.ArmorTypeOf[id]  = Combat.ArmorType.Light;
                world.VisionRange[id]  = Fixed.FromFloat(8f);
                world.SplashRadius[id] = Fixed.FromFloat(0f);
            }

            // Presentation: tag the unit type so MultiMeshBridge renders the right mesh (meshType resolved above
            // from the persisted Units index — the same coordinate IndexOfUnit returns).
            world.MeshType[id] = (byte)(meshType < 0 ? 0 : meshType);

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
            BuildingType.Aviary        => "Air",       // Story 2.8 — MUST exist or the _ => "Melee" default silently trains Melee at the Aviary.
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
        /// Every unit the given building type can train for the faction, as (Units-list index, def) pairs in
        /// deterministic list order (Story 2.8, per-unit production picker). Empty when the type produces nothing
        /// (CommandCenter) or the faction has no unit of that category. Lets the command card enumerate every
        /// trainable option without reaching into <see cref="FactionDefinition"/> directly (it holds only _buildSys).
        /// The pair index is the <see cref="FactionDefinition.IndexOfUnit"/> coordinate — the value TrainUnit stores.
        /// </summary>
        public List<(int Index, UnitDefinition Def)> GetProductionUnits(BuildingType type,
                                                                        Faction faction = Faction.Player1)
        {
            if (type == BuildingType.CommandCenter)
                return new List<(int, UnitDefinition)>();
            var fdef = GetFactionDef(faction);
            if (fdef == null)
                return new List<(int, UnitDefinition)>();
            return fdef.GetUnitsByCategory(CategoryForBuilding(type));
        }

        /// <summary>
        /// Queue a unit for training at the given production building.
        /// Returns true if training was accepted (building alive, idle, chosen unit valid, prereqs met, faction can afford).
        ///
        /// <paramref name="chosenUnitIndex"/> selects WHICH unit of the building's category to train, as an index
        /// into the faction's full <see cref="FactionDefinition.Units"/> list (Story 2.8). The default -1 means
        /// "first-of-category" — today's behaviour — so the AI caller and any 2-arg call compile and behave
        /// byte-identically. A chosen index that is out of range, or whose unit's category does not match the one
        /// this building produces, is HARD-REJECTED (returns false, spends nothing, queues nothing) — never train
        /// cross-category from a crafted or stale index (the Story 1.12 faction-guard lesson).
        /// </summary>
        public bool TrainUnit(int buildingId, ResourceStore resources, int chosenUnitIndex = -1)
        {
            if (buildingId < 0 || buildingId >= _buildings.Count) return false;
            if (!_buildings.Alive[buildingId]) return false;
            if (_buildings.IsUnderConstruction(buildingId)) return false;
            var bType = _buildings.Type[buildingId];
            if (bType == BuildingType.CommandCenter) return false;
            if (_buildings.ProductionQueue[buildingId] != 0) return false; // already training

            Faction faction = _buildings.FactionOf[buildingId];

            // Resolve the def to train: an explicit chosen unit (bounds- + category-checked) or, for the
            // default -1, the first-of-category unit (unchanged behaviour; may be null for an empty category).
            UnitDefinition? def;
            if (chosenUnitIndex >= 0)
            {
                var fdef = GetFactionDef(faction);
                if (fdef == null || chosenUnitIndex >= fdef.Units.Count) return false; // out-of-range → reject, no spend
                def = fdef.Units[chosenUnitIndex];
                // Category-match guard: the chosen unit must belong to the category this building produces.
                if (!string.Equals(def.Category, CategoryForBuilding(bType), System.StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            else
            {
                def = GetProductionUnit(bType, faction);
            }

            // Tech tree: check prerequisites before spending resources
            if (def != null && !TechTreeChecker.AreMet(_buildings, faction, def.Prerequisites))
                return false;

            // Supply cap: don't queue if the faction is already at cap
            byte supply = (byte)(def?.Supply ?? 1);
            if (!resources.HasSupply(faction, supply)) return false;

            // Story 2.9b (AC2): multi-resource cost — check BOTH ore and crystal BEFORE spending EITHER, mirroring
            // AbilityCastSystem.TryCast's check-all-then-debit-all contract. A unit whose ore passes but crystal
            // fails must spend NOTHING (the partial-spend bug this ordering prevents). No FALLBACK_COST_CRYSTAL
            // exists, so a null def defaults crystal to 0 (symmetric with the costOre null-coalesce — defensive; an
            // unresolvable/empty-category def already returns before this point in practice). Every existing
            // cost_crystal:0 unit is a no-op here (CanAfford/Spend of 0 always succeeds) — AC2.3, byte-for-byte.
            float costOre     = def?.CostOre     ?? FALLBACK_COST_ORE;
            float costCrystal = def?.CostCrystal ?? 0f;
            if (!resources.CanAffordOre(faction, Fixed.FromFloat(costOre)))         return false;
            if (!resources.CanAffordCrystal(faction, Fixed.FromFloat(costCrystal))) return false;
            resources.SpendOre(faction, Fixed.FromFloat(costOre));
            resources.SpendCrystal(faction, Fixed.FromFloat(costCrystal));

            // Persist the CONCRETE unit so SpawnTrainedUnit trains exactly this one (not a re-derived
            // first-of-category). Stored as (Units index + 1) so 0 stays "idle". An empty-category default
            // (def == null) stores the reserved fallback sentinel, preserving today's graceful fallback spawn.
            _buildings.ProductionQueue[buildingId] = ProductionQueueValue(faction, def, chosenUnitIndex);
            float trainTime = def?.TrainTime ?? FALLBACK_TRAIN_TIME;
            _buildings.ProductionTimer[buildingId] = Fixed.FromFloat(trainTime);
            return true;
        }

        /// <summary>
        /// Encode the trained unit as the <see cref="BuildingStore.ProductionQueue"/> byte (Story 2.8): the chosen
        /// unit's <see cref="FactionDefinition.Units"/> index + 1 (so 0 stays "idle"), or <see cref="PRODUCTION_FALLBACK"/>
        /// when no def resolved (an empty-category producer's graceful fallback). The chosen path already
        /// bounds-checked the index; the default path's def came from this faction's Units list so its index is
        /// valid — the defensive clamp only guards a data-integrity impossibility, never a real training request.
        /// </summary>
        private byte ProductionQueueValue(Faction faction, UnitDefinition? def, int chosenUnitIndex)
        {
            if (def == null) return PRODUCTION_FALLBACK;
            int storeIdx = chosenUnitIndex >= 0
                ? chosenUnitIndex
                : (GetFactionDef(faction)?.IndexOfUnit(def.Id) ?? -1);
            if (storeIdx < 0 || storeIdx >= PRODUCTION_FALLBACK - 1) return PRODUCTION_FALLBACK;
            return (byte)(storeIdx + 1);
        }

        /// <summary>
        /// Apply a lockstep <see cref="UnitCommand.Train"/> command at exec-tick (Story 2.8, D-1). Validates
        /// BUILDING ownership — a player may train ONLY at a building of their OWN faction (the anti-cheat
        /// building-command analogue of the entity-ownership guard, which is meaningless for a building id) —
        /// then runs the normal <see cref="TrainUnit"/> gate/spend against this system's own BuildingStore and
        /// ResourceStore. Called from the shared <c>OrderApplier</c> so live == replay == offline all execute this
        /// one method; the deterministic ore/supply spend therefore happens HERE, at exec-tick, exactly once
        /// (never at UI click-time). Returns true if training was queued.
        /// </summary>
        public bool TrainUnitCommand(int buildingId, Faction expectedFaction, int chosenUnitIndex)
        {
            if (buildingId < 0 || buildingId >= _buildings.Count) return false;
            if (!_buildings.Alive[buildingId]) return false;
            if (_buildings.FactionOf[buildingId] != expectedFaction) return false; // anti-cheat: own building only
            return TrainUnit(buildingId, _resources, chosenUnitIndex);
        }

        /// <summary>
        /// Apply a lockstep <see cref="UnitCommand.SetRally"/> command at exec-tick (Story 2.12, AC3). Validates
        /// BUILDING ownership — a player may set a rally point ONLY on a building of their OWN faction (the anti-cheat
        /// building-command analogue of the entity-ownership guard, meaningless for a building id) — then writes the
        /// canonical <see cref="BuildingStore.RallyPoint"/>/<see cref="BuildingStore.HasRallyPoint"/>. Called from the
        /// shared <c>OrderApplier</c> so live == replay == offline all execute this one method; the deterministic rally
        /// write therefore happens HERE, at exec-tick, on the folded (v9) store — never as a direct presentation write
        /// (the pre-2.12 desync path). The rally point is a ground point (X/Z from the wire, Y anchored to 0, exactly
        /// like <c>SpawnTrainedUnit</c> reads it). Returns true when the rally was written.
        /// </summary>
        public bool SetRallyCommand(int buildingId, Faction expectedFaction, Fixed x, Fixed z)
        {
            if (buildingId < 0 || buildingId >= _buildings.Count) return false;
            if (!_buildings.Alive[buildingId]) return false;
            if (_buildings.FactionOf[buildingId] != expectedFaction) return false; // anti-cheat: own building only
            _buildings.RallyPoint[buildingId]    = new FixedVec3(x, Fixed.Zero, z);
            _buildings.HasRallyPoint[buildingId] = true;
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

                // Story 2.13 (AC3.4): BuildTarget holds a PACKED building ref — TryResolveRef validates bounds + Alive
                // + GENERATION, so a worker whose under-construction target was razed AND its slot recycled into a new
                // building cancels cleanly instead of "arriving" at the new occupant. The -1 sentinel resolves false.
                if (!_buildings.TryResolveRef(world.BuildTarget[i], out int bId))
                {
                    ClearWorkerBuild(world, i); // target destroyed / recycled — cancel silently
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
            world.BuildTarget[workerId]  = _buildings.PackRef(bId); // Story 2.13 D-3: packed ref (golden-neutral at gen 0)
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

        /// <summary>
        /// Display name of the first unmet tech prerequisite for a SPECIFIC candidate unit (by its
        /// <see cref="FactionDefinition.Units"/> index), or null if all prerequisites are satisfied / the index is
        /// invalid. Used by the Story 2.8 per-unit production picker so each unit button shows its own "[need: X]"
        /// (the parameterless overload re-derives first-of-category and can't distinguish siblings).
        /// </summary>
        public string? GetUnmetPrereq(int buildingId, int unitIndex)
        {
            if (buildingId < 0 || buildingId >= _buildings.Count) return null;
            Faction faction = _buildings.FactionOf[buildingId];
            var fdef = GetFactionDef(faction);
            if (fdef == null || unitIndex < 0 || unitIndex >= fdef.Units.Count) return null;
            return TechTreeChecker.FirstMissing(_buildings, faction, fdef.Units[unitIndex].Prerequisites);
        }
    }
}
