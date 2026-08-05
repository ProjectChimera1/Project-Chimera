#nullable enable
using System.Collections.Generic;
using ProjectChimera.Combat;
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

    /// <summary>
    /// The single active command-card producer surface a building renders (this story). Every building resolves to
    /// exactly ONE of these via <see cref="BuildingSystem.ResolveCommandCardSurface"/>, so the command card renders
    /// that one grid and hides the rest — no two producer grids can overlap (DW-90). <see cref="None"/> = no producer
    /// grid (a CommandCenter or an explicit <c>command_card_producer:"none"</c>).
    /// </summary>
    public enum CommandCardSurface { None, Train, Research, Shop, Revive }

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

        /// <summary>
        /// Story 11.6 (FR-74): fraction of a cancelled production order's cost refunded to the faction — 100% (WC3
        /// model). A <see cref="BuildingSystem"/> constant, NOT an authored <see cref="UnitDefinition"/> field (keeps
        /// the content loader/whitelist untouched). The refund is re-resolved from <c>def.ResolvedCost</c> at cancel
        /// time (the slot stores only the encoded unit index), so it is deterministic on every peer / in replay.
        /// </summary>
        private const int TRAIN_CANCEL_REFUND_FRACTION = 1;

        /// <summary>
        /// DW-205: the unit category whose members drive the gather loop — the same token
        /// <see cref="CategoryForBuilding(BuildingType)"/> maps a CommandCenter to and an authored custom producer
        /// declares as <c>produces_category</c>. Compared case-insensitively, matching both
        /// <see cref="TrainUnit"/>'s chosen-unit category guard and <c>ScenarioApplier.SpawnUnitAt</c>'s worker test.
        /// </summary>
        private const string WORKER_CATEGORY = "Worker";

        /// <summary>
        /// DW-205: carry capacity (world resource units per trip) a freshly trained worker enters the gather loop
        /// with — the value <c>ScenarioApplier.SpawnUnitAt</c> and <c>EntityPlacer.DoSpawnWorker</c> already give a
        /// scenario-placed / editor-placed worker, so all three spawn paths agree. A load-time constant (never a
        /// per-tick <see cref="Fixed.FromFloat"/>), so it is one quantization done once.
        /// </summary>
        private static readonly Fixed WORKER_CARRY_CAPACITY = Fixed.FromFloat(20f);

        // Spawn offset (world units) from building centre
        private static readonly Fixed SPAWN_OFFSET = Fixed.FromFloat(3f);

        // Lateral spacing between consecutive spawns from the same building
        private static readonly Fixed SPAWN_SPREAD = Fixed.FromFloat(1.5f);

        private readonly BuildingStore        _buildings;
        private readonly ResourceStore        _resources;
        // Per-faction definitions indexed by (int)Faction. Slot 0 = Neutral (unused).
        private readonly FactionDefinition?[] _factions;
        private readonly MatchStats?           _stats;
        // Story 3.14 — the persistent hero substrate + the resolved revival rule, for ReviveHeroCommand (nullable so the
        // pre-3.14 golden/test callers that pass neither still compile and no-op the revive path deterministically).
        private readonly HeroStore?            _heroes;
        private readonly RevivalRuleRuntime?   _revival;

        // Story 7.13 — the trigger-DSL sim-event feed (unit_trained raised when production completes). Wired by
        // SimulationHost after construction; null ⇒ no raise.
        private DslSimEventFeed? _dslSimEvents;
        /// <summary>Story 7.13 — wire the trigger-DSL sim-event feed so completed training raises unit_trained.</summary>
        public void SetDslSimEvents(DslSimEventFeed? feed) => _dslSimEvents = feed;

        // Story 11.4 (FR-74) — the presentation combat-event queue, so SpawnTrainedUnit can push a TrainingComplete cue
        // when production finishes. Wired by SimulationHost after construction (mirrors SetDslSimEvents); null ⇒ no cue.
        // The queue is NOT a SimChecksum input, so pushing here cannot move any golden.
        private CombatEventQueue? _combatEvents;
        /// <summary>Story 11.4 — wire the presentation combat-event queue so completed training raises a TrainingComplete cue.</summary>
        public void SetCombatEvents(CombatEventQueue? events) => _combatEvents = events;

        // DW-207 — the resource nodes, so a Build command that interrupts a gathering worker RELEASES the node's
        // reserved gatherer slot instead of only forgetting the target (which permanently burned that much of the
        // node's MaxGatherers capacity). Wired by SimulationHost after construction (mirrors SetDslSimEvents /
        // SetCombatEvents so the ~30 existing test call sites keep their signatures); null ⇒ the pre-DW-207 behaviour
        // of clearing GatherTarget only, for the isolated-store callers that own no ResourceNodeStore at all.
        private ResourceNodeStore? _nodes;
        /// <summary>DW-207 — wire the node store so <see cref="QueueWorkerBuild"/> can release the interrupted worker's
        /// reserved gather slot through the single <see cref="GatheringSystem.ReleaseGatherSlot"/> path.</summary>
        public void SetResourceNodes(ResourceNodeStore? nodes) => _nodes = nodes;

        public BuildingSystem(BuildingStore buildings, ResourceStore resources,
                              FactionDefinition? p1Faction = null,
                              FactionDefinition? p2Faction = null,
                              MatchStats?        stats     = null,
                              HeroStore?         heroes    = null,
                              RevivalRuleRuntime? revival  = null)
        {
            _buildings = buildings;
            _resources = resources;
            _stats     = stats;
            _heroes    = heroes;
            _revival   = revival;
            _factions  = new FactionDefinition?[FactionRegistry.FACTION_ARRAY_SIZE]; // 9: Neutral + Player1..Player8
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
            // Reset to base cap (Story 4.4: the resolved per-scenario starting cap, not the hardcoded constant —
            // ConfigureSupply defaults it to STARTING_SUPPLY_CAP when no scenario config is authored, so an
            // omitted `supply` block reproduces this loop byte-identically).
            // Story 9.2: iterate every playable slot (1..8), not just 1-4 — Player5-8 must reset to base too.
            for (int f = 1; f < FactionRegistry.FACTION_ARRAY_SIZE; f++)
                _resources.SupplyCap[f] = _resources.StartingSupplyCap;

            for (int i = 0; i < _buildings.Count; i++)
            {
                if (!_buildings.Alive[i]) continue;
                if (_buildings.IsUnderConstruction(i)) continue; // not functional yet
                int f = (int)_buildings.FactionOf[i];
                _resources.SupplyCap[f] += _buildings.SupplyBonus[i];
            }

            // Story 4.4: clamp to the authored hard ceiling AFTER bonuses, UNCONDITIONALLY of SupplyGatingEnabled —
            // the ceiling shapes the cap VALUE (what the HUD and AI headroom scoring read) regardless of whether the
            // gate enforces it (see spec Design Notes: avoids a hidden coupling where toggling `enabled` silently
            // changes the displayed cap too).
            if (_resources.SupplyHardCeiling is int ceiling)
            {
                // Story 9.2: clamp every playable slot (1..8), matching the base-reset loop above.
                for (int f = 1; f < FactionRegistry.FACTION_ARRAY_SIZE; f++)
                    if (_resources.SupplyCap[f] > ceiling)
                        _resources.SupplyCap[f] = ceiling;
            }
        }

        // ── Production ────────────────────────────────────────────────────────

        private void TickProduction(EntityWorld world, Fixed dt)
        {
            for (int i = 0; i < _buildings.Count; i++)
            {
                if (!_buildings.Alive[i]) continue;
                if (_buildings.IsUnderConstruction(i)) continue; // can't produce yet
                if (_buildings.ProductionQueue[_buildings.HeadIndex(i)] == 0) continue; // idle (head empty)

                // A head with a RUNNING timer accrues progress and completes only when the timer crosses zero.
                // A head whose timer is ALREADY expired falls straight through to the spawn attempt below — that is
                // the DW-479 blocked-spawn retry state (an earlier tick's spawn was refused by a full EntityWorld).
                // The old `timer <= 0 -> continue` guard skipped such a head FOREVER, freezing the whole queue
                // behind it (DW-481's symptom for a 0-TrainTime head, which content validation now rejects at the
                // authoring gate — UnitDefinitionValidator's strictly-positive train_time rule).
                if (_buildings.ProductionTimer[i] > Fixed.Zero)
                {
                    _buildings.ProductionTimer[i] = _buildings.ProductionTimer[i] - dt;
                    if (_buildings.ProductionTimer[i] > Fixed.Zero) continue; // still training
                }

                // Head training complete — spawn the head unit, then pop-and-advance: shift slots 1..4 down and
                // start the promoted head's timer from its full TrainTime (Story 11.6). An empty next head goes idle.
                // DW-479: advance ONLY on a SUCCESSFUL spawn. SpawnTrainedUnit no-ops at the EntityWorld entity cap;
                // the old unconditional AdvanceQueue then DISCARDED the paid-for head with no refund — and with the
                // depth-5 queue it burned one more paid slot on every subsequent tick the world stayed full. Leaving
                // the head in place with an expired timer parks the order until a slot frees, then it spawns.
                if (SpawnTrainedUnit(world, i))
                    AdvanceQueue(i);
            }
        }

        /// <summary>
        /// Spawn the building's HEAD production order near the building. Returns <c>true</c> when an entity was
        /// actually created (the caller may then pop the queue), <c>false</c> when <see cref="EntityWorld.Create"/>
        /// refused because the world is at its entity cap — in which case NOTHING was consumed: no spawn cue, no
        /// unit_trained event, no MatchStats credit, and no <see cref="BuildingStore.TrainedCount"/> increment, so
        /// the retry next tick reproduces this call exactly (DW-479).
        /// </summary>
        private bool SpawnTrainedUnit(EntityWorld world, int buildingId)
        {
            Faction faction = _buildings.FactionOf[buildingId];

            // Story 2.8: train the CONCRETE unit chosen at TrainUnit time — persisted in ProductionQueue as
            // (Units index + 1), or PRODUCTION_FALLBACK for an empty-category producer. Read it here instead of
            // re-deriving first-of-category. Explicitly bounds-check the List indexer: the null-conditional
            // guards only a null FactionDefinition, NOT an out-of-range index (which would throw
            // ArgumentOutOfRangeException inside the tick — a "never throw in the tick" violation).
            byte q = _buildings.ProductionQueue[_buildings.HeadIndex(buildingId)]; // Story 11.6: the head slot (slot 0)
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
            // DW-479: READ the spawn-offset cursor here but COMMIT the increment only after a successful Create
            // below. A blocked spawn must not burn a lateral-offset slot, or a retried order would land on a
            // different tile than the one this attempt computed.
            int trained = _buildings.TrainedCount[buildingId];
            Fixed offsetX = faction == Faction.Player1 ? SPAWN_OFFSET : -SPAWN_OFFSET;
            Fixed offsetZ = SPAWN_SPREAD * Fixed.FromInt((trained % 5) - 2);
            FixedVec3 spawnPos = new FixedVec3(
                _buildings.Position[buildingId].X + offsetX,
                Fixed.Zero,
                _buildings.Position[buildingId].Z + offsetZ);

            int id = world.Create(spawnPos, faction,
                Fixed.FromFloat(hp), Fixed.FromFloat(speed));

            if (id < 0) return false; // EntityWorld full — DW-479: the caller must KEEP the paid-for head and retry

            _buildings.TrainedCount[buildingId] = trained + 1; // committed only now (see the read above)

            // Story 11.4 (FR-74) — production-completion cue: push TrainingComplete at the training building's position,
            // carrying its faction, so MatchAlertBridge plays the cue ONLY for the local player. Presentation-only (the
            // queue is not a SimChecksum input) → golden-safe. Null queue (bare tests / golden headless) → no-op.
            _combatEvents?.Push(CombatEventType.TrainingComplete, _buildings.Position[buildingId], faction);

            // Story 7.13 — raise unit_trained at the production-completion site (the trained entity id + its faction
            // slot) into the trigger DSL sim-event feed. Null feed (bare tests) → no-op.
            _dslSimEvents?.Push(DslSimEventFeed.KindUnitTrained, (int)faction - 1, id, 0, 0);

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
                // Story 3.12: this def-less path bypasses ApplyUnitDefinition, so it must mirror the old range→delivery
                // inference (deleted from CombatSystem) or the fallback would inherit the Create default Hitscan and a
                // ranged (range 5 > 2.5) fallback unit would regress from projectile to instant hitscan. ProjectileSpeed
                // stays at the Create default (== the old global 18), so the inferred projectile flies as it did before.
                world.Delivery[id] = world.AttackRange[id] > UnitDefinition.LegacyDeliveryThreshold
                    ? Combat.AttackDelivery.Projectile : Combat.AttackDelivery.Hitscan;
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

            // DW-205: the caller-owned worker gather residue. GatherState/CarryCapacity are deliberately NOT part of
            // ApplyUnitDefinition (they are runtime state, not def data — see the EntityWorld residue rule), so every
            // spawn path has to write them itself; this one never did. Create() leaves a fresh slot at
            // GatherState.Inactive/CarryCapacity 0, which makes a trained worker doubly wrong: GatheringSystem.Tick
            // skips Inactive entities (it can never gather) AND CombatSystem's gatherer exemption is keyed on
            // GatherState != Inactive, so the worker instead auto-acquires and shoots anything in range. Latent until
            // Story 6.8 (the only built-in "Worker" producer is the CommandCenter, and <see cref="TrainUnit"/> rejects
            // a CommandCenter outright) but reachable now that an authored Custom building can declare
            // produces_category:"Worker" and train through this very path. Mirrors ScenarioApplier.SpawnUnitAt's
            // worker branch exactly (same OrdinalIgnoreCase category test TrainUnit's own chosen-unit guard uses, same
            // 20u capacity), so a trained worker and a scenario-placed one enter the gather loop in identical state.
            // Deliberately placed BEFORE the rally/Stop branch so BOTH branches get the residue.
            bool isWorker = def != null && string.Equals(def.Category, WORKER_CATEGORY, System.StringComparison.OrdinalIgnoreCase);
            if (isWorker)
            {
                world.GatherState[id]   = GatherState.Idle;
                world.CarryCapacity[id] = WORKER_CARRY_CAPACITY;
            }

            // Walk to rally point if the building has one set
            if (_buildings.HasRallyPoint[buildingId])
            {
                world.CommandState[id] = UnitCommand.Move;
                world.CommandGoal[id]  = _buildings.RallyPoint[buildingId];
                world.MoveTarget[id]   = _buildings.RallyPoint[buildingId];

                // DW-634 — HONOR THE RALLY FIRST, THEN AUTO-GATHER (recorded decision, 2026-08-05). Pre-fix, the
                // Move→rally above was written and then silently discarded for a WORKER: GatheringSystem.TickIdle
                // re-targeted MoveTarget to the nearest eligible node on the very next tick, so a player could never
                // rally new workers to a specific mine (rallying the hall to a far mine / a new expansion is standard
                // macro). The flag makes the idle-gather sweep stand down until this worker reaches the rally, at which
                // point the sweep clears it and the EXISTING nearest-node logic picks the node beside the rally point —
                // no new gather state, no rally-to-resource targeting (the recorded MINIMAL shape).
                //
                // WORKER-ONLY, both lines, and deliberately so:
                //   • RallyMovePending has exactly one reader (GatheringSystem, which only ticks gatherers), so a combat
                //     unit's flag would be write-only state that nothing ever clears.
                //   • EntityFlags.Moving is what actually makes MovementSystem walk the unit; this branch never set it,
                //     so a rallied unit has never physically walked to its rally in the SIM (presentation only steers
                //     units it holds a flow field / nav path for, which a trained unit has neither of). Without it the
                //     gather sweep would stand down forever and the worker would never gather at all — strictly worse
                //     than the pre-fix state. Setting it for a COMBAT unit too would be the wider (correct) fix, but
                //     that path IS golden-covered — ShiftQueueScenario trains a Melee grunt from a rallied Barracks —
                //     so it would move a recorded checksum and belongs in a deliberate re-baseline, not here.
                if (isWorker)
                {
                    world.Flags[id]           |= EntityFlags.Moving;
                    world.RallyMovePending[id] = true;
                }
            }
            else
            {
                // Hold position at the spawn point — Idle would send the unit
                // chasing the nearest enemy across the map (global chase).
                world.CommandState[id] = UnitCommand.Stop;
            }
            return true;
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

        /// <summary>
        /// Story 6.8: the production category for a PLACED building slot, generalized to honor an authored custom
        /// building. A built-in enum type resolves through the closed <see cref="CategoryForBuilding(BuildingType)"/>
        /// switch (byte-identical). A <see cref="BuildingType.Custom"/> building resolves <c>produces_category</c> from
        /// its <see cref="BuildingDefinition"/> via the placed slot's <paramref name="definitionId"/> — never the
        /// switch's <c>_ => "Melee"</c> default (which would silently train Melee at a custom Air producer). Falls back
        /// to "Melee" only when the def / its <c>produces_category</c> is genuinely absent.
        /// </summary>
        private string CategoryForBuilding(BuildingType type, Faction faction, string? definitionId)
        {
            if (type != BuildingType.Custom) return CategoryForBuilding(type);
            string? pc = GetFactionDef(faction)?.GetBuilding(definitionId ?? "")?.ProducesCategory;
            return string.IsNullOrEmpty(pc) ? "Melee" : pc;
        }

        /// <summary>The four built-in enum building types that produce trainable combat/economy units (a CommandCenter
        /// is deliberately excluded — it surfaces supply only, never a train grid). The train-surface eligibility test
        /// this story shares between the derivation and the Custom-producer widening.</summary>
        private static bool IsBuiltInProducer(BuildingType type) =>
            type == BuildingType.Barracks
            || type == BuildingType.ArcheryRange
            || type == BuildingType.SiegeWorkshop
            || type == BuildingType.Aviary;

        // ── Command-card producer surface (this story) ──────────────────────────

        /// <summary>
        /// Resolve the SINGLE active command-card producer surface for a placed building — the central authority the
        /// command card renders exactly one grid from (DW-90: no two producer grids overlap; DW-31: a dual-capability
        /// building gets an author-chosen surface). The authored <see cref="BuildingDefinition.CommandCardProducer"/>
        /// is honored (case-insensitively) ONLY when the building is actually ELIGIBLE for that surface; an ineligible
        /// or unrecognized declaration falls through to the priority derivation instead, so a mis-authored declaration
        /// never blanks the card (<c>"none"</c> is the one always-honored value — an explicit opt-out). Absent a valid
        /// declaration, the deterministic priority derivation (Train → Research → Shop → Revive → None) preserves
        /// today's single-capability behaviour byte-for-byte.
        /// Train eligibility widens to a <see cref="BuildingType.Custom"/> producer whose authored
        /// <c>produces_category</c> is non-empty and not "None" (DW-168), reading the placed slot's
        /// <see cref="BuildingStore.DefinitionId"/>. Bounds/alive-guarded → <see cref="CommandCardSurface.None"/>.
        /// Pure read-only (no store/def mutation) → no goldens move. Godot-free / unit-testable without the engine.
        /// </summary>
        public CommandCardSurface ResolveCommandCardSurface(int buildingId)
        {
            if (buildingId < 0 || buildingId >= _buildings.Count) return CommandCardSurface.None;
            if (!_buildings.Alive[buildingId]) return CommandCardSurface.None;

            BuildingType type = _buildings.Type[buildingId];
            Faction faction   = _buildings.FactionOf[buildingId];
            BuildingDefinition? bdef = GetFactionDef(faction)?.GetBuilding(_buildings.DefinitionId[buildingId] ?? "");

            string? pc = bdef?.ProducesCategory;
            bool trainEligible = IsBuiltInProducer(type)
                || (type == BuildingType.Custom && !string.IsNullOrEmpty(pc)
                    && !"None".Equals(pc, System.StringComparison.OrdinalIgnoreCase));
            bool researchEligible = (bdef?.AvailableResearch?.Length ?? 0) > 0;
            bool shopEligible     = _buildings.SellsItems[buildingId];
            bool reviveEligible   = _buildings.RevivesHeroes[buildingId];

            // Authored declaration is honored ONLY when the building is actually ELIGIBLE for that surface. Declaring
            // a surface the building can't fulfill (e.g. "revive" on a non-reviver, "shop" on a non-seller, "train" on
            // a produces_category:"None" custom building) would otherwise blank the card entirely — the per-grid
            // capability guard hides the declared grid AND every other grid is gated on surface== — a silent dead
            // building the syntactic validator can't catch. An ineligible OR unrecognized declaration therefore falls
            // through to the priority derivation instead. "none" is the one always-honored value (an explicit opt-out).
            string? declared = bdef?.CommandCardProducer;
            if (!string.IsNullOrEmpty(declared))
            {
                if ("none".Equals(declared, System.StringComparison.OrdinalIgnoreCase))
                    return CommandCardSurface.None;
                if (trainEligible && "train".Equals(declared, System.StringComparison.OrdinalIgnoreCase))
                    return CommandCardSurface.Train;
                if (researchEligible && "research".Equals(declared, System.StringComparison.OrdinalIgnoreCase))
                    return CommandCardSurface.Research;
                if (shopEligible && "shop".Equals(declared, System.StringComparison.OrdinalIgnoreCase))
                    return CommandCardSurface.Shop;
                if (reviveEligible && "revive".Equals(declared, System.StringComparison.OrdinalIgnoreCase))
                    return CommandCardSurface.Revive;
                // ineligible or unrecognized declaration → fall through to derivation (never blanks the card)
            }

            return trainEligible    ? CommandCardSurface.Train
                 : researchEligible ? CommandCardSurface.Research
                 : shopEligible     ? CommandCardSurface.Shop
                 : reviveEligible   ? CommandCardSurface.Revive
                 : CommandCardSurface.None;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the UnitDefinition that the given building type will produce for the
        /// specified faction, or null if there is no matching unit in that faction's definition.
        /// Defaults to Player1 when called from UI systems that don't have faction context.
        ///
        /// When <paramref name="definitionId"/> is non-null the category is resolved def-aware (via the placed slot's
        /// authored <c>produces_category</c> for a <see cref="BuildingType.Custom"/> producer — DW-168); null keeps the
        /// byte-identical enum-only resolution so every existing 2-arg call is unchanged.
        /// </summary>
        public UnitDefinition? GetProductionUnit(BuildingType type,
                                                  Faction faction = Faction.Player1,
                                                  string? definitionId = null)
        {
            if (type == BuildingType.CommandCenter) return null;
            string category = definitionId != null
                ? CategoryForBuilding(type, faction, definitionId)
                : CategoryForBuilding(type);
            return GetFactionDef(faction)?.GetUnitByCategory(category);
        }

        /// <summary>
        /// Every unit the given building type can train for the faction, as (Units-list index, def) pairs in
        /// deterministic list order (Story 2.8, per-unit production picker). Empty when the type produces nothing
        /// (CommandCenter) or the faction has no unit of that category. Lets the command card enumerate every
        /// trainable option without reaching into <see cref="FactionDefinition"/> directly (it holds only _buildSys).
        /// The pair index is the <see cref="FactionDefinition.IndexOfUnit"/> coordinate — the value TrainUnit stores.
        /// </summary>
        public List<(int Index, UnitDefinition Def)> GetProductionUnits(BuildingType type,
                                                                        Faction faction = Faction.Player1,
                                                                        string? definitionId = null)
        {
            if (type == BuildingType.CommandCenter)
                return new List<(int, UnitDefinition)>();
            var fdef = GetFactionDef(faction);
            if (fdef == null)
                return new List<(int, UnitDefinition)>();
            // Def-aware when definitionId is supplied (a Custom producer's authored produces_category — DW-168);
            // null keeps the byte-identical enum-only resolution for every existing 2-arg call.
            string category = definitionId != null
                ? CategoryForBuilding(type, faction, definitionId)
                : CategoryForBuilding(type);
            return fdef.GetUnitsByCategory(category);
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
        public bool TrainUnit(int buildingId, ResourceStore resources, int chosenUnitIndex = -1,
                              CombatEventQueue? events = null)
        {
            if (buildingId < 0 || buildingId >= _buildings.Count) return false;
            if (!_buildings.Alive[buildingId]) return false;
            if (_buildings.IsUnderConstruction(buildingId)) return false;
            var bType = _buildings.Type[buildingId];
            if (bType == BuildingType.CommandCenter) return false;
            // Story 11.6: append to the first empty queue slot (depth-5). With all 5 slots occupied the queue is full —
            // a reason-less denial cue at the building (the player clicked a full producer); no spend, nothing queued.
            int freeSlot = _buildings.FirstEmptySlot(buildingId);
            if (freeSlot < 0)
            {
                events?.PushDenied(_buildings.Position[buildingId], _buildings.FactionOf[buildingId], DenialReason.QueueFull);
                return false; // queue full (all QUEUE_DEPTH slots occupied)
            }

            Faction faction = _buildings.FactionOf[buildingId];
            FixedVec3 buildingPos = _buildings.Position[buildingId];

            // Story 6.8: resolve the category from the placed slot (def-aware for a Custom producer), so a custom
            // building trains its authored produces_category, not the enum switch's Melee default.
            string category = CategoryForBuilding(bType, faction, _buildings.DefinitionId[buildingId]);

            // Resolve the def to train: an explicit chosen unit (bounds- + category-checked) or, for the
            // default -1, the first-of-category unit (unchanged behaviour; may be null for an empty category).
            UnitDefinition? def;
            if (chosenUnitIndex >= 0)
            {
                var fdef = GetFactionDef(faction);
                if (fdef == null || chosenUnitIndex >= fdef.Units.Count) return false; // out-of-range → reject, no spend
                def = fdef.Units[chosenUnitIndex];
                // Category-match guard: the chosen unit must belong to the category this building produces.
                if (!string.Equals(def.Category, category, System.StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            else
            {
                def = GetFactionDef(faction)?.GetUnitByCategory(category);
            }

            // Tech tree: check prerequisites before spending resources
            if (def != null && !TechTreeChecker.AreMet(_buildings, faction, def.Prerequisites))
            {
                events?.PushDenied(buildingPos, faction, DenialReason.PrereqMissing); // Story 11.4: guard-sourced reason
                return false;
            }

            // Supply cap (DW-478, decision 2026-07-30 "reserve supply at enqueue — WC3-strict"): gate on the
            // PROJECTED supply, i.e. the live SupplyUsed PLUS the supply already committed by every queued-but-
            // unspawned order of this faction PLUS this order's own cost. Supply is still CONSUMED at spawn — the
            // reservation is a gate-time projection, never a write (see QueuedSupply). Without the queued term all
            // QUEUE_DEPTH enqueues saw the same unchanged live headroom, so a depth-5 queue could overshoot the cap
            // by up to 4 units (at depth 1 only one order was ever in flight, so the hole was unreachable).
            byte supply = (byte)(def?.Supply ?? 1);
            if (!resources.HasSupply(faction, supply + QueuedSupply(faction)))
            {
                events?.PushDenied(buildingPos, faction, DenialReason.SupplyCapped); // Story 11.4: guard-sourced reason
                return false;
            }

            // Story 4.3: sparse cost-map spend — check-all-then-spend-all over the resolved cost map (generalizes
            // the Story 2.9b ore+crystal atomicity to N resources). A null def (empty-category fallback) has no
            // FALLBACK_COST_CRYSTAL, so it falls back to a single-entry ore-only map (symmetric with the old
            // costOre null-coalesce). Every existing cost_crystal:0 unit is a no-op here (an absent/zero entry
            // never fails CanAfford) — AC2.3, byte-for-byte; a unit whose ore passes but crystal fails still spends
            // NOTHING (Spend's own check-all-then-spend-all).
            var cost = def?.ResolvedCost ?? new Dictionary<string, int> { { "ore", (int)FALLBACK_COST_ORE } };
            if (!resources.CanAfford(faction, cost))
            {
                events?.PushDenied(buildingPos, faction, DenialReasons.ForUnaffordableCost(resources, faction, cost)); // Story 11.4: which resource is short
                return false;
            }
            resources.Spend(faction, cost);

            // Persist the CONCRETE unit at the first empty slot so SpawnTrainedUnit trains exactly this one (not a
            // re-derived first-of-category). Stored as (Units index + 1) so 0 stays "empty". An empty-category default
            // (def == null) stores the reserved fallback sentinel, preserving today's graceful fallback spawn.
            _buildings.ProductionQueue[_buildings.HeadIndex(buildingId) + freeSlot] =
                ProductionQueueValue(faction, def, chosenUnitIndex);
            // Story 11.6: only the HEAD (slot 0) runs a timer. Filling an empty HEAD (freeSlot == 0 ⇒ the queue was
            // empty) starts it from full TrainTime; appending to a waiting slot leaves the running head timer untouched.
            if (freeSlot == 0)
            {
                float trainTime = def?.TrainTime ?? FALLBACK_TRAIN_TIME;
                _buildings.ProductionTimer[buildingId] = Fixed.FromFloat(trainTime);
            }
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
        public bool TrainUnitCommand(int buildingId, Faction expectedFaction, int chosenUnitIndex,
                                     CombatEventQueue? events = null)
        {
            if (buildingId < 0 || buildingId >= _buildings.Count) return false;
            if (!_buildings.Alive[buildingId]) return false;
            if (_buildings.FactionOf[buildingId] != expectedFaction) return false; // anti-cheat: own building only (SILENT)
            // Past the ownership guard this is the player's OWN building, so TrainUnit's affordability/prereq/supply
            // rejections surface a guard-sourced OrderDenied cue (Story 11.4). `events` null (golden/replay/AI) → silent.
            return TrainUnit(buildingId, _resources, chosenUnitIndex, events);
        }

        /// <summary>
        /// Apply a lockstep <see cref="UnitCommand.CancelTrain"/> command at exec-tick (Story 11.6, FR-74). Mirrors
        /// <see cref="TrainUnitCommand"/>'s ownership guard (a player may cancel ONLY at a building of their OWN faction
        /// — the anti-cheat building-command analogue; a foreign/out-of-range/empty-slot cancel is a SILENT deterministic
        /// no-op). Refunds <see cref="TRAIN_CANCEL_REFUND_FRACTION"/> (100%) of the cancelled slot's unit cost — RE-RESOLVED
        /// from <c>def.ResolvedCost</c> by the stored encoded index (the same re-resolve-from-def pattern
        /// <see cref="ResearchSystem.CancelResearchCommand"/> uses, so the refund is deterministic on every peer / in
        /// replay) — via <see cref="ResourceStore.Add"/>, then removes the slot and shifts slots <c>slot+1..4</c> down one.
        /// Cancelling the HEAD (slot 0) discards its in-progress timer (WC3: progress lost) and starts the promoted new
        /// head's timer from its full <c>TrainTime</c>; cancelling a waiting slot leaves the head timer untouched.
        /// Supply is never REFUNDED as a resource — it is consumed at spawn, not at enqueue — but removing the slot
        /// does release the enqueue-time RESERVATION that slot held, automatically: <see cref="QueuedSupply"/> is
        /// re-derived from the queue on every gate call, so a cancelled order frees its headroom with no bookkeeping
        /// (DW-478). Returns true iff a slot was cancelled.
        /// </summary>
        public bool CancelTrainCommand(int buildingId, Faction expectedFaction, int slot,
                                       CombatEventQueue? events = null)
        {
            if (buildingId < 0 || buildingId >= _buildings.Count) return false;
            if (!_buildings.Alive[buildingId]) return false;
            if (_buildings.FactionOf[buildingId] != expectedFaction) return false; // anti-cheat: own building only (SILENT)
            if (slot < 0 || slot >= BuildingStore.QUEUE_DEPTH) return false;        // out-of-range slot → no-op

            int head = _buildings.HeadIndex(buildingId);
            byte q = _buildings.ProductionQueue[head + slot];
            if (q == 0) return false; // empty slot → no-op, no refund

            Faction faction = _buildings.FactionOf[buildingId];

            // Refund 100% of the slot's cost, re-resolved from the stored encoded unit index (never the cost paid — the
            // slot stores only the index). A fallback-sentinel (empty-category) slot spent the ore-only fallback cost at
            // enqueue, so it refunds the identical ore-only fallback map — symmetric and deterministic.
            var cost = ResolveQueuedCost(faction, q);
            if (cost.Count > 0)
            {
                var refund = new Dictionary<string, int>(cost.Count);
                foreach (var (key, amount) in cost) refund[key] = amount * TRAIN_CANCEL_REFUND_FRACTION;
                _resources.Add(faction, refund);
            }

            // Remove the slot and shift the tail down one (a pure array move — waiting slots carry no running timer).
            for (int k = slot; k < BuildingStore.QUEUE_DEPTH - 1; k++)
                _buildings.ProductionQueue[head + k] = _buildings.ProductionQueue[head + k + 1];
            _buildings.ProductionQueue[head + BuildingStore.QUEUE_DEPTH - 1] = 0;

            // Cancelling the head discards its progress and (re)starts the promoted head from full time; an empty new
            // head goes idle. Cancelling a waiting slot never touches the head timer.
            if (slot == 0)
            {
                byte newHead = _buildings.ProductionQueue[head];
                _buildings.ProductionTimer[buildingId] = newHead != 0
                    ? Fixed.FromFloat(TrainTimeForQueued(faction, newHead))
                    : Fixed.Zero;
            }
            return true;
        }

        /// <summary>Story 11.6: shift building <paramref name="buildingId"/>'s queue down one after the head completed —
        /// slots 1..4 → 0..3, slot 4 cleared — then (re)start the promoted head's timer from its full <c>TrainTime</c>,
        /// or set the building idle when the queue is now empty.</summary>
        private void AdvanceQueue(int buildingId)
        {
            int head = _buildings.HeadIndex(buildingId);
            for (int k = 0; k < BuildingStore.QUEUE_DEPTH - 1; k++)
                _buildings.ProductionQueue[head + k] = _buildings.ProductionQueue[head + k + 1];
            _buildings.ProductionQueue[head + BuildingStore.QUEUE_DEPTH - 1] = 0;

            byte newHead = _buildings.ProductionQueue[head];
            _buildings.ProductionTimer[buildingId] = newHead != 0
                ? Fixed.FromFloat(TrainTimeForQueued(_buildings.FactionOf[buildingId], newHead))
                : Fixed.Zero;
        }

        /// <summary>
        /// DW-478 — the supply already COMMITTED by <paramref name="faction"/>'s queued-but-not-yet-spawned
        /// production orders: the sum, over every ALIVE building of that faction (ascending building id, then
        /// ascending queue slot), of each occupied slot's authored <see cref="UnitDefinition.Supply"/>. A slot is
        /// re-resolved from its stored encoded index exactly the way <see cref="SpawnTrainedUnit"/> resolves it; an
        /// empty-category fallback sentinel / stale index contributes 1, matching the <c>def?.Supply ?? 1</c> that
        /// <see cref="TrainUnit"/> charged when it queued that slot. Never negative, never throws.
        ///
        /// <para><b>Why a projection and not a write.</b> The reservation deliberately does NOT live in
        /// <see cref="ResourceStore.SupplyUsed"/>: <see cref="SupplySystem"/> rebuilds that array from the ALIVE
        /// entities every single tick, so a value written there is erased one tick later — and it is a
        /// <see cref="SimChecksum"/> input, so writing to it would move every recorded golden. Re-deriving the
        /// reservation from the (already folded) production queue at the gate keeps the checksum untouched and can
        /// never drift out of sync with the queue: cancelling or completing an order releases its reservation for
        /// free. Pure integer arithmetic in a fixed ascending order ⇒ identical on every peer and in replay.</para>
        ///
        /// <para>Public so a production-affordance UI can grey the train button on the SAME projection the enqueue
        /// gate uses instead of on the live-supply-only <see cref="ResourceStore.HasSupply"/>.</para>
        /// </summary>
        public int QueuedSupply(Faction faction)
        {
            int total = 0;
            for (int b = 0; b < _buildings.Count; b++)   // ascending building id — deterministic
            {
                if (!_buildings.Alive[b]) continue;
                if (_buildings.FactionOf[b] != faction) continue;
                int head = _buildings.HeadIndex(b);
                for (int k = 0; k < BuildingStore.QUEUE_DEPTH; k++)
                {
                    byte q = _buildings.ProductionQueue[head + k];
                    if (q == 0) continue;               // empty slot contributes nothing
                    total += ResolveQueuedDef(faction, q)?.Supply ?? 1;
                }
            }
            return total;
        }

        /// <summary>Story 11.6: the sparse resolved cost map for a queued slot's encoded value <paramref name="q"/>,
        /// re-resolved from the faction def by index (mirrors <see cref="SpawnTrainedUnit"/>'s read). A fallback
        /// sentinel / stale index resolves to the same ore-only fallback map <see cref="TrainUnit"/> spends for an
        /// empty-category producer, so a cancel refunds exactly what enqueue took.</summary>
        private IReadOnlyDictionary<string, int> ResolveQueuedCost(Faction faction, byte q)
        {
            UnitDefinition? def = ResolveQueuedDef(faction, q);
            return def?.ResolvedCost ?? new Dictionary<string, int> { { "ore", (int)FALLBACK_COST_ORE } };
        }

        /// <summary>Story 11.6: the full <c>TrainTime</c> for a queued slot's encoded value (re-resolved by index), or
        /// the fallback train time for a sentinel / stale index — the value a promoted head's fresh timer starts at.</summary>
        private float TrainTimeForQueued(Faction faction, byte q) =>
            ResolveQueuedDef(faction, q)?.TrainTime ?? FALLBACK_TRAIN_TIME;

        /// <summary>Story 11.6: resolve a queued slot's encoded value (<c>unitIndex+1</c>, or <see cref="PRODUCTION_FALLBACK"/>)
        /// back to its <see cref="UnitDefinition"/>, or null for the empty / fallback / stale-index cases (the graceful
        /// fallback branch, exactly like <see cref="SpawnTrainedUnit"/>). Never throws.</summary>
        private UnitDefinition? ResolveQueuedDef(Faction faction, byte q)
        {
            if (q == 0 || q == PRODUCTION_FALLBACK) return null;
            var fdef = GetFactionDef(faction);
            int idx = q - 1;
            if (fdef != null && idx >= 0 && idx < fdef.Units.Count) return fdef.Units[idx];
            return null;
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
        /// Apply a lockstep <see cref="UnitCommand.ReviveHero"/> command at exec-tick (Story 3.14, D-4). Mirrors
        /// <see cref="TrainUnitCommand"/>: it names a BUILDING (dispatched BEFORE the entity-ownership guard), validates
        /// building ownership (anti-cheat: own building only) + the <c>revives_heroes</c> capability, resolves the
        /// awaiting hero named in the wire (<paramref name="heroSlotRaw"/> = <c>Fixed.FromRaw(slot)</c>), re-checks the
        /// hero is Alive + AwaitingRevival + owned by <paramref name="expectedFaction"/> + NOT already counting
        /// (<c>RevivalLink == REVIVAL_NONE</c>), then spends the level-scaled cost check-both-then-debit-both and starts
        /// the deterministic countdown by writing <c>RevivalTimer</c>/<c>RevivalLink</c> on the hero row. The spend
        /// therefore happens HERE, at exec-tick, exactly once (never at UI click-time), on the shared OrderApplier path
        /// so live == replay == offline. Returns true when a countdown was started; any guard/affordability failure
        /// spends nothing.
        /// </summary>
        public bool ReviveHeroCommand(int buildingId, Faction expectedFaction, Fixed heroSlotRaw,
                                      CombatEventQueue? events = null)
        {
            if (_heroes == null || _revival == null) return false; // pre-3.14 caller / revival not wired → deterministic no-op
            if (buildingId < 0 || buildingId >= _buildings.Count) return false;
            if (!_buildings.Alive[buildingId]) return false;
            if (_buildings.FactionOf[buildingId] != expectedFaction) return false; // anti-cheat: own building only
            if (_buildings.IsUnderConstruction(buildingId)) return false;          // guard-parity with TrainUnit: a still-
                                                                                   // constructing building cannot revive
            if (!_buildings.RevivesHeroes[buildingId]) return false;               // building lacks the capability

            int slot = heroSlotRaw.Raw; // the awaiting-hero slot rides raw in TargetX (never via .ToFloat())
            if (slot < 0 || slot >= _heroes.Count) return false;
            if (!_heroes.Alive[slot]) return false;
            if (!_heroes.AwaitingRevival[slot]) return false;                       // hero must be fallen & awaiting
            if (_heroes.OwnerFaction[slot] != expectedFaction) return false;        // anti-cheat: own hero only
            if (_heroes.SourceDef[slot] == null) return false;                     // cannot respawn without a def → reject
                                                                                   // the order (else a spent-but-never-
                                                                                   // respawnable pay-for-nothing loop)
            if (_heroes.RevivalLink[slot] != HeroStore.REVIVAL_NONE) return false;  // already counting down → reject

            int level = _heroes.Level[slot];
            Fixed costOre     = _revival.CostOre(level);
            Fixed costCrystal = _revival.CostCrystal(level);
            // Check BOTH before debiting EITHER (the TrainUnit partial-spend contract) — an ore-passes/crystal-fails
            // order must spend nothing. All ownership/capability guards above have already passed, so an affordability
            // failure is a LEGIT player order that simply cannot be paid → surface a presentation-only OrderDenied cue
            // (Story 2.12 convention) at the building. The ownership/capability rejections above stay silent (they are
            // crafted/anti-cheat orders — no feedback, no position leak).
            if (!_resources.CanAffordOre(expectedFaction, costOre))
            {
                events?.PushDenied(_buildings.Position[buildingId], expectedFaction, DenialReason.NeedOre); // Story 11.4
                return false;
            }
            if (!_resources.CanAffordCrystal(expectedFaction, costCrystal))
            {
                events?.PushDenied(_buildings.Position[buildingId], expectedFaction, DenialReason.NeedCrystal); // Story 11.4
                return false;
            }
            _resources.SpendOre(expectedFaction, costOre);
            _resources.SpendCrystal(expectedFaction, costCrystal);

            // Start the deterministic countdown, linking the hero to THIS building (a packed ref, ABA-safe).
            _heroes.RevivalTimer[slot] = _revival.TimeSeconds(level);
            _heroes.RevivalLink[slot]  = _buildings.PackRef(buildingId);
            return true;
        }

        /// <summary>Story 3.16: fallback shop radius used when a shop building authored no (or a zero) <c>shop_radius</c>.</summary>
        private static readonly Fixed SHOP_FALLBACK_RADIUS = Fixed.FromInt(6);

        /// <summary>
        /// Apply a lockstep <see cref="UnitCommand.BuyItem"/> command at exec-tick (Story 3.16). Mirrors
        /// <see cref="ReviveHeroCommand"/>: it names a SHOP BUILDING (<paramref name="buildingId"/>, dispatched BEFORE the
        /// entity-ownership guard), and buys stock item <paramref name="stockIndex"/> for the owned hero embodied by
        /// <paramref name="heroEntityId"/>. Full guard order (any post-ownership failure ⇒ <see cref="CombatEventType.OrderDenied"/>
        /// at the shop, zero state change): building bounds+Alive; building owned by <paramref name="expectedFaction"/>
        /// (anti-cheat — SILENT reject, no position leak); not under construction; <c>SellsItems</c>; stock index in range;
        /// the stock id resolves in the <see cref="ItemRegistry"/>; the buyer is a live OWNED hero; the buyer is within
        /// <c>shop_radius</c> (in-sim proximity — anti-cheat, not just a UI gate); a free inventory slot; and affordability
        /// (check BOTH before debiting EITHER — the partial-spend contract). Only then spends ore+crystal atomically and
        /// mints the item into the hero's first free slot (reusing the <see cref="ItemSystem"/> claim block). An item-store-
        /// full mint failure after the spend REFUNDS (net-zero, deterministic). Returns true when an item was bought.
        /// <paramref name="items"/> null ⇒ deterministic no-op (golden/replay-without-items paths, like <c>buildings</c>).
        /// </summary>
        public bool BuyItemCommand(int buildingId, Faction expectedFaction, int stockIndex, int heroEntityId,
                                   ItemSystem? items, CombatEventQueue? events = null)
        {
            ResourceStore resources = _resources;
            if (items == null) return false; // pre-3.16 caller / items not wired → deterministic no-op
            if (buildingId < 0 || buildingId >= _buildings.Count) return false;
            if (!_buildings.Alive[buildingId]) return false;
            if (_buildings.FactionOf[buildingId] != expectedFaction) return false; // anti-cheat: own building only (silent)

            // Past the ownership guard the order references the player's OWN building, so a subsequent rejection surfaces
            // a presentation-only OrderDenied cue at the shop (no cross-faction position leak).
            FixedVec3 shopPos = _buildings.Position[buildingId];
            if (_buildings.IsUnderConstruction(buildingId)) { Deny(events, shopPos, expectedFaction, DenialReason.InvalidTarget); return false; }
            if (!_buildings.SellsItems[buildingId])          { Deny(events, shopPos, expectedFaction, DenialReason.InvalidTarget); return false; }

            string[] stock = _buildings.ShopStock[buildingId] ?? System.Array.Empty<string>();
            if (stockIndex < 0 || stockIndex >= stock.Length) { Deny(events, shopPos, expectedFaction, DenialReason.InvalidTarget); return false; }

            int defIndex = items.Registry.IndexOf(stock[stockIndex]);
            if (defIndex < 0) { Deny(events, shopPos, expectedFaction, DenialReason.InvalidTarget); return false; } // dangling stock id (validation should have caught it)
            ItemDefinition? def = items.Registry.TryGet(defIndex);
            if (def == null) { Deny(events, shopPos, expectedFaction, DenialReason.InvalidTarget); return false; }

            // Buyer: a live hero owned by the same faction.
            if (!items.TryResolveHero(heroEntityId, out int heroSlot)) { Deny(events, shopPos, expectedFaction, DenialReason.InvalidTarget); return false; }
            if (items.HeroFaction(heroEntityId) != expectedFaction)    { Deny(events, shopPos, expectedFaction, DenialReason.InvalidTarget); return false; }

            // Proximity: the buyer must be within shop_radius of the shop (long-widened raw squared distance — cannot
            // overflow on a map-sized separation; the ItemSystem pickup-proximity form). Anti-cheat, not just a UI gate.
            Fixed radius = _buildings.ShopRadius[buildingId];
            if (radius <= Fixed.Zero) radius = SHOP_FALLBACK_RADIUS;
            FixedVec3 hp = items.HeroPosition(heroEntityId);
            long dxr = (long)hp.X.Raw - shopPos.X.Raw;
            long dzr = (long)hp.Z.Raw - shopPos.Z.Raw;
            long sqrDist = ((dxr * dxr) >> 16) + ((dzr * dzr) >> 16);
            long rr = ((long)radius.Raw * radius.Raw) >> 16;
            if (sqrDist > rr) { Deny(events, shopPos, expectedFaction, DenialReason.OutOfRange); return false; }

            // Room BEFORE any spend, so a rejected buy is a pure no-op.
            if (!items.HeroHasFreeSlot(heroSlot)) { Deny(events, shopPos, expectedFaction, DenialReason.InventoryFull); return false; }

            // Affordability: check BOTH before debiting EITHER (the partial-spend contract).
            if (!resources.CanAffordOre(expectedFaction, def.CostOre))     { Deny(events, shopPos, expectedFaction, DenialReason.NeedOre); return false; }
            if (!resources.CanAffordCrystal(expectedFaction, def.CostCrystal)) { Deny(events, shopPos, expectedFaction, DenialReason.NeedCrystal); return false; }
            resources.SpendOre(expectedFaction, def.CostOre);
            resources.SpendCrystal(expectedFaction, def.CostCrystal);

            int itemRef = items.GrantPurchasedItem(heroEntityId, defIndex);
            if (itemRef < 0)
            {
                // Item store full (astronomically unlikely — free-slot already passed). Refund atomically → net zero.
                resources.AddOre(expectedFaction, def.CostOre);
                resources.AddCrystal(expectedFaction, def.CostCrystal);
                Deny(events, shopPos, expectedFaction, DenialReason.InventoryFull);
                return false;
            }
            events?.Push(CombatEventType.ItemPickedUp, hp); // reuse the pickup event for a bought item landing in inventory
            return true;
        }

        // Story 11.4 (FR-74): guard-sourced denial — the rejecting guard stamps the SPECIFIC reason it just computed
        // plus the acting faction onto the OrderDenied event; the reactive UI renders it, never re-derives it.
        private static void Deny(CombatEventQueue? events, FixedVec3 pos, Faction faction, DenialReason reason) =>
            events?.PushDenied(pos, faction, reason);

        /// <summary>Story 3.16: resolve the authored shop capability + stock + radius for the faction's building
        /// <paramref name="type"/> at placement (mirrors <see cref="ResolveRevivesHeroes"/>). Null faction/def ⇒ no shop.
        /// The authored float <c>shop_radius</c> is quantized to <see cref="Fixed"/> once HERE (the placement boundary),
        /// never in a tick.</summary>
        private (bool Sells, string[] Stock, Fixed Radius) ResolveSellsItems(BuildingType type, Faction faction)
        {
            var bdef = GetFactionDef(faction)?.GetBuilding(TechTreeChecker.BuildingTypeId(type));
            if (bdef == null) return (false, System.Array.Empty<string>(), Fixed.Zero);
            return (bdef.SellsItems, bdef.ShopStock ?? System.Array.Empty<string>(), Fixed.FromFloat(bdef.ShopRadius));
        }

        /// <summary>
        /// Place a building on behalf of a scenario loader or editor tool.
        /// Bypasses ore cost. When <paramref name="preBuilt"/> is true the construction
        /// timer is set to zero so the building is immediately operational.
        /// Returns the building ID, or -1 if the store is full.
        /// </summary>
        public int PlaceBuildingDirect(BuildingType type, Faction faction,
                                       FixedVec3 position, bool preBuilt, bool revivesHeroes = false)
        {
            // Story 3.14: the revive capability is AUTHORED on the building's UnitDefinition. Resolve it from the faction
            // def at placement so a scenario-placed building actually revives (callers that already know the flag —
            // Tier-1 tests — pass it explicitly and short-circuit). Without this the whole feature is unreachable in a
            // real match (no production path would ever set BuildingStore.RevivesHeroes).
            bool revives = revivesHeroes || ResolveRevivesHeroes(type, faction);
            var (sells, stock, radius) = ResolveSellsItems(type, faction); // Story 3.16: resolve the authored shop capability
            // Story 4.1 (AC1/AC2): resolve the authored BuildingDefinition (if any) and thread its Hp/SupplyBonus/
            // ConstructionTime into Create's resolved-stats params — the data-driven path. A null bdef (no matching
            // def), or one whose ConstructionTime/SupplyBonus weren't populated (a def built outside LoadFromFile's
            // validation gate — review-pass fix), null-propagates the affected param(s) to null; Create's all-three-
            // or-none gate then falls back to the untouched per-type switch — byte-identical to pre-4.1 behavior for
            // every caller with no fully-resolved def. Never force-unwrap ConstructionTime — a partially-populated
            // bdef must degrade gracefully, not throw.
            BuildingDefinition? bdef = GetFactionDef(faction)?.GetBuilding(TechTreeChecker.BuildingTypeId(type));
            int id = _buildings.Create(position, faction, type, revives, sells, stock, radius,
                buildingId: bdef?.Id,
                health: bdef != null ? Fixed.FromFloat(bdef.Hp) : (Fixed?)null,
                supplyBonus: bdef?.SupplyBonus,
                constructionDuration: bdef?.ConstructionTime is float ct ? Fixed.FromFloat(ct) : (Fixed?)null);
            if (id < 0) return -1;
            if (preBuilt)
                _buildings.ConstructionTimer[id] = Fixed.Zero;
            return id;
        }

        /// <summary>
        /// Story 6.8: place a building by its AUTHORED <see cref="BuildingDefinition.Id"/> (the data-driven identity),
        /// the by-id sibling of <see cref="PlaceBuildingDirect"/>. Resolves the def from the faction, maps the id to a
        /// dedicated enum member via <see cref="TechTreeChecker.BuildingTypeFromId"/> (or <see cref="BuildingType.Custom"/>
        /// for an authored building with no enum member), and threads the def's Hp/SupplyBonus/ConstructionTime +
        /// revive/shop capability into <see cref="BuildingStore.Create"/>'s resolved-stats params — the ONLY path to
        /// real (non-switch-default) stats for a <see cref="BuildingType.Custom"/> building. A null def (unknown id —
        /// only reachable in shadow mode, the validator fails an unknown id closed) still records the authored
        /// <c>DefinitionId</c> and degrades to the per-type switch defaults rather than throwing. Bypasses ore cost;
        /// <paramref name="preBuilt"/>=true zeroes the construction timer. Returns the building id, or -1 if full.
        /// </summary>
        public int PlaceBuildingDirectById(string buildingId, Faction faction,
                                           FixedVec3 position, bool preBuilt)
        {
            BuildingDefinition? bdef = GetFactionDef(faction)?.GetBuilding(buildingId);
            BuildingType type = TechTreeChecker.BuildingTypeFromId(buildingId) ?? BuildingType.Custom;

            bool revives = bdef?.RevivesHeroes ?? false;
            (bool sells, string[] stock, Fixed radius) shop = bdef != null
                ? (bdef.SellsItems, bdef.ShopStock ?? System.Array.Empty<string>(), Fixed.FromFloat(bdef.ShopRadius))
                : (false, System.Array.Empty<string>(), Fixed.Zero);

            int id = _buildings.Create(position, faction, type, revives, shop.sells, shop.stock, shop.radius,
                buildingId: bdef?.Id ?? buildingId, // preserve the authored id even when no def resolved
                health: bdef != null ? Fixed.FromFloat(bdef.Hp) : (Fixed?)null,
                supplyBonus: bdef?.SupplyBonus,
                constructionDuration: bdef?.ConstructionTime is float ct ? Fixed.FromFloat(ct) : (Fixed?)null);
            if (id < 0) return -1;
            if (preBuilt)
                _buildings.ConstructionTimer[id] = Fixed.Zero;
            return id;
        }

        /// <summary>Story 3.14: does the faction's authored definition for <paramref name="type"/> flag
        /// <c>revives_heroes</c>? The capability is authored on the building's <see cref="UnitDefinition"/> in
        /// <c>FactionDefinition.Buildings</c>; resolve it via the same <c>BuildingType</c>→def-id mapping the
        /// prerequisite check uses (<see cref="TechTreeChecker.BuildingTypeId"/>). Null faction/def/building ⇒ false.</summary>
        private bool ResolveRevivesHeroes(BuildingType type, Faction faction) =>
            GetFactionDef(faction)?.GetBuilding(TechTreeChecker.BuildingTypeId(type))?.RevivesHeroes ?? false;

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

        /// <summary>
        /// Clear the Build command on a worker and resume the gather loop.
        ///
        /// <para>DW-520 — a worker interrupted MID-CARRY finishes its DELIVERY. Forcing <see cref="GatherState.Idle"/>
        /// regardless of <see cref="EntityWorld.CarryAmount"/> sent a worker that was walking a load home when the build
        /// order landed back out to a node instead: <c>TickIdle</c> re-reserves one of that node's
        /// <c>MaxGatherers</c> slots, the worker walks the whole way there, gathers NOTHING (a full carry leaves
        /// <c>canCarry == 0</c>), and only then turns round and banks the load it was already carrying — a wasted round
        /// trip per interrupted worker, and a slot needlessly taken from a worker that could have used it. Routing a
        /// non-empty carry straight back to <see cref="GatherState.MovingToBase"/> resumes the leg that was in flight;
        /// the deposit itself is unchanged (<c>TickMovingToBase</c> credits by the carried
        /// <see cref="EntityWorld.CarryResourceType"/>, which survived the interrupt).</para>
        ///
        /// <para>The base position and <see cref="EntityFlags.Moving"/> are set here for the same reason
        /// <c>GatheringSystem.SetMoveToBase</c> sets them: the Build order overwrote <c>MoveTarget</c> with the build
        /// site, so the resumed leg needs its destination back. Nothing folded into <c>SimChecksum</c> is read or
        /// written by the choice itself, and no recorded golden issues a Build order at all.</para>
        /// </summary>
        private void ClearWorkerBuild(EntityWorld world, int workerId)
        {
            world.CommandState[workerId] = UnitCommand.Idle;
            world.BuildTarget[workerId]  = -1;

            if (world.CarryAmount[workerId] > Fixed.Zero)
            {
                // DW-520: still holding a load — finish the delivery that the build order interrupted.
                world.MoveTarget[workerId]  = _resources.FactionBase[(int)world.FactionOf[workerId]];
                world.Flags[workerId]      |= EntityFlags.Moving;
                world.GatherState[workerId] = GatherState.MovingToBase;
                return;
            }

            // Empty-handed — resume the gather loop; GatheringSystem will assign a node next tick.
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
                                    Faction faction, ResourceStore resources, EntityWorld world,
                                    CombatEventQueue? events = null)
        {
            if (!world.IsAlive(workerId)) return -1;
            if (world.GatherState[workerId] == GatherState.Inactive) return -1; // not a worker

            // Prerequisite check
            BuildingDefinition? bdef = GetFactionDef(faction)?.GetBuilding(TechTreeChecker.BuildingTypeId(type));
            string[]? prereqs = bdef?.Prerequisites;
            if (!TechTreeChecker.AreMet(_buildings, faction, prereqs))
            {
                events?.PushDenied(position, faction, DenialReason.PrereqMissing); // Story 11.4: guard-sourced reason
                return -1;
            }

            // Story 4.3: sparse cost-map spend (was ore-only — buildings never charged crystal, a latent gap this
            // generalization fixes as a direct consequence of routing through the real resolved cost map).
            var cost = GetBuildingCost(type, faction);
            if (!resources.CanAfford(faction, cost))
            {
                events?.PushDenied(position, faction, DenialReasons.ForUnaffordableCost(resources, faction, cost)); // Story 11.4
                return -1;
            }
            // Keep placement atomic on the actual spend: CanAfford named the denial reason above, but STILL honour
            // Spend's own check-all-then-spend-all result (P8) so a future spend-fails-after-afford change can never
            // leak a free build. A false here spends nothing (ResourceStore's contract).
            if (!resources.Spend(faction, cost))
            {
                events?.PushDenied(position, faction, DenialReasons.ForUnaffordableCost(resources, faction, cost));
                return -1;
            }

            // Place building (starts under construction). Story 3.14: carry the authored revives_heroes capability so a
            // player-built revive building (not just scenario-placed) actually works. Story 4.1 (AC1/AC2): thread the
            // resolved def's Hp/SupplyBonus/ConstructionTime the same way PlaceBuildingDirect does — a null bdef, or
            // one missing ConstructionTime/SupplyBonus (built outside LoadFromFile's gate), null-propagates to the
            // switch fallback rather than throwing (review-pass fix — never force-unwrap ConstructionTime).
            var (sells, stock, radius) = ResolveSellsItems(type, faction); // Story 3.16
            int bId = _buildings.Create(position, faction, type, ResolveRevivesHeroes(type, faction), sells, stock, radius,
                buildingId: bdef?.Id,
                health: bdef != null ? Fixed.FromFloat(bdef.Hp) : (Fixed?)null,
                supplyBonus: bdef?.SupplyBonus,
                constructionDuration: bdef?.ConstructionTime is float ct ? Fixed.FromFloat(ct) : (Fixed?)null);
            if (bId < 0)
            {
                resources.Add(faction, cost); // refund — unconditional; an empty/all-zero map is a no-op
                return -1;
            }

            // Release the current node assignment. DW-207: the old comment here promised the slot count would
            // "reconcile next gather tick" — nothing ever did, so every Build command issued to a worker that was
            // en route to / standing at a node silently and permanently consumed one of that node's MaxGatherers
            // slots. Route through the single GatheringSystem release path, which decrements AssignedGatherers only
            // for the two states that actually hold a reservation (never MovingToBase, which already gave it back).
            if (_nodes != null) GatheringSystem.ReleaseGatherSlot(world, _nodes, workerId);
            world.GatherTarget[workerId] = -1; // unconditional: the unwired (null-store) callers keep today's behaviour

            // Issue Build command — MovementSystem moves the worker, GatheringSystem skips them
            world.BuildTarget[workerId]  = _buildings.PackRef(bId); // Story 2.13 D-3: packed ref (golden-neutral at gen 0)
            world.CommandState[workerId] = UnitCommand.Build;
            world.MoveTarget[workerId]   = position;
            world.Flags[workerId]       |= EntityFlags.Moving;

            return bId;
        }

        /// <summary>
        /// Returns the sparse resolved cost map to place the given building type for the faction (Story 4.3 —
        /// was ore-only; buildings never charged crystal, a latent gap this fixes). Falls back to a fresh empty map
        /// (free placement) when no definition is found — a fresh instance per call (review patch), not a shared
        /// static: a caller downcasting the returned <see cref="IReadOnlyDictionary{TKey,TValue}"/> back to
        /// <see cref="Dictionary{TKey,TValue}"/> and mutating it can never corrupt every future no-definition-found
        /// lookup. Consumed identically at both call sites (<see cref="QueueWorkerBuild"/>'s spend/refund,
        /// <see cref="ProjectChimera.UI.CommandCardSystem"/>'s build-button display).
        /// </summary>
        public IReadOnlyDictionary<string, int> GetBuildingCost(BuildingType type, Faction faction)
        {
            string id = TechTreeChecker.BuildingTypeId(type);
            return GetFactionDef(faction)?.GetBuilding(id)?.ResolvedCost ?? new Dictionary<string, int>();
        }

        /// <summary>
        /// Returns the display name of the first unmet building prerequisite for
        /// placing the given type, or null if all prerequisites are satisfied.
        /// Used by CommandCardSystem to show "[need: X]" on build buttons.
        /// </summary>
        public string? GetBuildingPlacePrereq(BuildingType type, Faction faction)
        {
            string id = TechTreeChecker.BuildingTypeId(type);
            var fdef = GetFactionDef(faction);
            var prereqs = fdef?.GetBuilding(id)?.Prerequisites;
            string? missing = TechTreeChecker.FirstMissing(_buildings, faction, prereqs);
            return ResolveMissingDisplayName(fdef, missing);
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
            // Thread the placed slot's DefinitionId so a Custom producer resolves its OWN authored category's unit
            // (DW-168) instead of the enum-only Melee default.
            var def = GetProductionUnit(_buildings.Type[buildingId], faction, _buildings.DefinitionId[buildingId]);
            if (def == null) return null;
            string? missing = TechTreeChecker.FirstMissing(_buildings, faction, def.Prerequisites);
            return ResolveMissingDisplayName(GetFactionDef(faction), missing);
        }

        /// <summary>
        /// Story 4.2: <see cref="TechTreeChecker"/> now returns the raw missing prereq id (it has no
        /// <see cref="FactionDefinition"/> in scope) — this resolves it to the authored <c>display_name</c> at the
        /// caller, preserving today's "[need: Command Center]"-style UI text byte-for-byte for shipped content.
        /// Guards against a building whose <c>display_name</c> resolves but is empty (unauthored) — falling back to
        /// the raw id in that case too, not an empty string (review-pass fix: a bare <c>?? missing</c> only catches
        /// a NULL DisplayName, not an empty one, since <see cref="UnitDefinition.DisplayName"/> defaults to <c>""</c>).
        /// </summary>
        private string? ResolveMissingDisplayName(FactionDefinition? fdef, string? missing) =>
            missing == null
                ? null
                : (fdef?.GetBuilding(missing)?.DisplayName is { Length: > 0 } dn ? dn : missing);

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
            string? missing = TechTreeChecker.FirstMissing(_buildings, faction, fdef.Units[unitIndex].Prerequisites);
            return ResolveMissingDisplayName(fdef, missing);
        }
    }
}
