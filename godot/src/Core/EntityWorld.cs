#nullable enable
using System;
using ProjectChimera.Combat;
using ProjectChimera.Core.Definitions; // UnitDefinition (definition→SoA copy in ApplyUnitDefinition)
using ProjectChimera.Effects;          // StatusFlags (modifier-imposed per-entity status; a value enum, same sim layer)

namespace ProjectChimera.Core
{
    /// <summary>
    /// The active command controlling a unit's autonomous behaviour.
    /// Issued by the player and respected by CombatSystem each tick.
    /// </summary>
    public enum UnitCommand : byte
    {
        Idle         = 0, // Auto-attack nearest enemy; chase globally if none in range
        Move         = 1, // Navigate to destination; ignore enemies en route
        AttackMove   = 2, // Navigate to destination; attack enemies encountered in range; resume after kill
        Stop         = 3, // Stand still; attack enemies that enter range; never chase. CAN be displaced by separation steering.
        HoldPosition = 4, // Hold ground: attack in range but NEVER chase/set MoveTarget, AND never be displaced by separation — a TRUE hold, distinct from Stop (Story 1.12).
        Build        = 5, // Worker walking to a build site; GatheringSystem skips this worker
        // ── Story 1.12 (DG-1 / FR-53): appended AFTER Build. Values 0–5 are FROZEN for replay back-compat. ──
        AttackTarget = 6, // Force-attack ONE specific enemy (CommandTarget); path to and chase only it, ignoring nearer enemies.
        Patrol       = 7, // Walk an ordered waypoint route (PatrolWaypoints ring), engaging enemies en route, reversing at both ends.
        Follow       = 8, // Track a friendly unit (CommandTarget) within a leash; re-path when beyond it, idle within it.
        PatrolAppend = 9, // WIRE-ONLY: append a waypoint to the patrol route, then rewritten to Patrol on apply (CombatSystem never sees it).
        // ── Story 2.4a (FR-11): appended AFTER PatrolAppend. Values 0–9 stay FROZEN for replay back-compat. ──
        CastAbility  = 10, // Cast the ability in slot TargetX (raw int) at target entity TargetZ (raw int, -1 = Self/None). A fire-and-forget INTENT: OrderApplier writes PendingCast*, AbilityCastSystem consumes it inside the tick — it does NOT persist as a CommandState (the caster's Move/Attack order is preserved).
        // ── Story 2.8 (D-1 / FR-11): appended AFTER CastAbility. Values 0-10 stay FROZEN for replay back-compat. ──
        Train        = 11, // Train a unit at a production BUILDING. WIRE: UnitId = buildingId, TargetX = chosen unit index (raw int, -1 = first-of-category). Handled by OrderApplier BEFORE the entity-ownership guard (UnitId names a building, not an entity); ore/supply spend happens at exec-tick via BuildingSystem.TrainUnitCommand. Never persists as a CommandState.
        // ── Story 2.9a (FR-11/FR-12): appended AFTER Train. Values 0-11 stay FROZEN for replay back-compat. ──
        AttackBuilding = 12, // Force-attack ONE specific enemy BUILDING. WIRE: UnitId = attacker entity, TargetX = building id (raw int). Handled AFTER the entity-ownership guard (UnitId names an entity); OrderApplier blind-stores the building id in CommandTarget, and CombatSystem.TickAttackBuildingCombat validates (bounds/Alive/friendly/Structure-domain) in-tick. Persists as a CommandState.
        // ── Story 2.12 (FR-74): appended AFTER AttackBuilding. Values 0-12 stay FROZEN for replay back-compat. ──
        SetRally = 13, // Set/change a production BUILDING's rally point. WIRE: UnitId = buildingId, TargetX/TargetZ = rally ground point (Fixed raw, the Move encoding). Handled by OrderApplier BEFORE the entity-ownership guard (UnitId names a building, like Train); it writes BuildingStore.RallyPoint/HasRallyPoint via BuildingSystem.SetRallyCommand (bounds + Alive + faction anti-cheat) and NEVER persists as a CommandState. Rides the queued-flag mask harmlessly (a rally is never Shift-queued; the flag is masked off before this compare).
        // ── Story 3.14 (hero death & revival): appended AFTER SetRally. Values 0-13 stay FROZEN for replay back-compat. ──
        ReviveHero = 14, // Order a REVIVE at a building flagged revives_heroes. WIRE: UnitId = buildingId, TargetX = awaiting-hero slot (raw int, Fixed.FromRaw(slot)). Handled by OrderApplier BEFORE the entity-ownership guard (UnitId names a building, like Train/SetRally); the building-ownership + affordability guards and the level-scaled spend live in BuildingSystem.ReviveHeroCommand at exec-tick. NEVER persists as a CommandState. Enum stays <= 0x3F (bits 6-7 are the wire queued flag).
        // ── Story 3.15 (item & inventory): appended AFTER ReviveHero. Values 0-14 stay FROZEN for replay back-compat. Enum stays <= 0x3F. ──
        PickupItem = 15, // Order a hero (UnitId = hero entity) onto a ground item. WIRE: TargetX = the target item's PACKED ItemStore ref (raw int). Goes through the entity-ownership guard + ApplyActiveOrder: stores the item ref in CommandTarget, sets Flags |= Moving. ItemSystem drives MoveTarget toward the item each tick and claims it on proximity (ascending-id) into the first free inventory slot, or OrderDenies when full. PERSISTS as a CommandState (CombatSystem treats it like Move — pure navigation).
        UseItem    = 16, // Use the consumable in an inventory slot. WIRE: UnitId = hero entity, TargetX = inventory slot index (raw int). Handled by OrderApplier **AFTER** the IsAlive/FactionOf entity-ownership guard — UnitId names the HERO ENTITY, NOT a building, so it is deliberately NOT the Train/Revive pre-guard pattern: dispatching before the guard would let a player force an ENEMY hero to use items (the 3.15 anti-cheat fix; pinned by ItemSystemTests' foreign-faction rejection tests). It then delegates to ItemSystem.UseItemCommand. NEVER persists as a CommandState.
        DropItem   = 17, // Drop the item in an inventory slot back onto the ground. WIRE: UnitId = hero entity, TargetX = inventory slot index (raw int). Handled by OrderApplier **AFTER** the IsAlive/FactionOf entity-ownership guard, for the same anti-cheat reason as UseItem (never the building-command pre-guard pattern), then delegates to ItemSystem.DropItemCommand. NEVER persists as a CommandState.
        // ── Story 3.16 (item authoring, shop buildings, inventory UI): appended AFTER DropItem. Values 0-17 stay FROZEN for replay back-compat. Enum stays <= 0x3F. ──
        BuyItem    = 18, // Buy a stocked item at a SHOP building (UnitId = shop buildingId, like Train/Revive). WIRE: TargetX = stock index (raw int), TargetZ = buying hero entity id (raw int). Handled by OrderApplier BEFORE the entity-ownership guard (UnitId names a building); the building-ownership + sells_items capability + stock/proximity/free-slot/affordability guards and the atomic spend + mint live in BuildingSystem.BuyItemCommand at exec-tick. NEVER persists as a CommandState. `buildings`/`items` null ⇒ deterministic no-op.
        // ── Story 4.9 (research order path): appended AFTER BuyItem. Values 0-18 stay FROZEN for replay back-compat. Enum stays <= 0x3F. ──
        StartResearch = 19, // Start a research order at a producing BUILDING (UnitId = buildingId, like Train/Revive/BuyItem). WIRE: TargetX = the chosen research's index into FactionDefinition.Research (mirrors Train's chosenUnitIndex semantics — a global list index, not building-local). Handled by OrderApplier BEFORE the entity-ownership guard; the building-ownership + AvailableResearch/in-progress/max-level/prerequisite/affordability guards and the deterministic exec-tick spend live in ResearchSystem.StartResearchCommand. NEVER persists as a CommandState.
        CancelResearch = 20, // Cancel the issuing faction's in-progress research order (UnitId = any owned building, like Train/Revive/BuyItem — research state is faction-wide, not building-scoped). TargetX is unused/reserved. Handled by OrderApplier BEFORE the entity-ownership guard; the building-ownership guard + refund + state clear live in ResearchSystem.CancelResearchCommand. NEVER persists as a CommandState.
        // ── Story 7.9 (custom-UI write rail): appended AFTER CancelResearch. Values 0-20 stay FROZEN for replay back-compat. Enum stays <= 0x3F. ──
        DslEvent = 21, // Raise a registered custom event from a custom-UI Button through the lockstep bus. WIRE (reuses the fixed 11-byte UnitOrder, no widening): UnitId = the custom-event REGISTRY INDEX (declaration order in ScenarioData.CustomEvents), TargetX = arg0 raw, TargetZ = arg1 raw (Int/Bool value or Fixed.Raw). Handled by OrderApplier BEFORE the entity-ownership guard (UnitId names an event index, not an entity); the sim-side raiser-authorization gate (ScenarioDirector.TryEnqueueExternalDslEvent, raiser = the command's faction slot) drops an unauthorized/out-of-range/at-capacity raise deterministically. NEVER persists as a CommandState.
        // ── Story 11.2 (FR-66 concede/surrender): appended AFTER DslEvent. Values 0-21 stay FROZEN for replay back-compat. Enum stays <= 0x3F (bits 6-7 are the wire queued flag). ──
        Concede = 22, // Surrender the ISSUING faction (like DslEvent, this NAMES A FACTION — the command's expectedFaction — NOT an entity). WIRE: UnitId/TargetX/TargetZ are unused/reserved (0). Handled by OrderApplier BEFORE the entity-ownership guard (there is no entity to guard); it latches WinStateStore.Verdict[expectedFaction]=VERDICT_LOST when currently VERDICT_NONE (monotone — a re-concede is a deterministic no-op). WinConditionSystem's next ApplyLastTeamStanding then awards the sole remaining live team. A null WinStateStore handle ⇒ deterministic no-op (golden/headless/replay-without-win-state). NEVER persists as a CommandState.
        // ── Story 11.6 (FR-74 production queue depth-5): appended AFTER Concede. Values 0-22 stay FROZEN for replay back-compat. Enum stays <= 0x3F (bits 6-7 are the wire queued flag). ──
        CancelTrain = 23, // Cancel a queued/in-progress production order at a BUILDING (UnitId = buildingId, like Train/SetRally/Revive). WIRE: TargetX = the queue slot index (raw int, 0-4; read directly, NEVER via .ToFloat() — the packed-int lesson). Handled by OrderApplier BEFORE the entity-ownership guard (UnitId names a building, not an entity); the building-ownership + slot-range/empty guards and the deterministic 100% refund + remove/shift live in BuildingSystem.CancelTrainCommand at exec-tick. `buildings` null ⇒ deterministic no-op (golden/replay). NEVER persists as a CommandState.
    }

    /// <summary>
    /// Wire-level flag bits OR'd onto the <see cref="UnitCommand"/> byte of a <see cref="Multiplayer.UnitOrder"/>
    /// (Story 2.12, Decision #2). <see cref="UnitCommand"/> uses values 0-13, so bits 6-7 are free; a Shift-issued
    /// (queued) order sets <see cref="Queued"/> on the wire and <c>OrderApplier</c> masks it off with
    /// <see cref="CommandMask"/> before the command→state switch. Wire-only — a flagged byte NEVER reaches
    /// <c>CommandState</c> (only 0-13 are valid enum values there) or <see cref="SimChecksum"/>.
    /// </summary>
    public static class UnitOrderFlags
    {
        /// <summary>High bit (0x80): this order is APPENDED to the entity's order queue instead of applied now.</summary>
        public const byte Queued = 0x80;
        /// <summary>Low 7 bits (0x7F): recovers the real <see cref="UnitCommand"/> from a possibly-flagged wire byte.</summary>
        public const byte CommandMask = 0x7F;
    }

    /// <summary>
    /// DW-645 — the EXECUTABLE half of the <see cref="UnitCommand"/> doc comments above: does this command PERSIST
    /// as an entity's <see cref="EntityWorld.CommandState"/>, or is it a fire-and-forget wire INTENT that is consumed
    /// at apply time and never stored?
    ///
    /// <para><b>Why this exists.</b> The two kinds look identical on the wire but have completely different downstream
    /// obligations. A command that persists becomes a per-tick control state, so EVERY system that switches on
    /// <c>CommandState</c> — above all <c>CombatSystem.Tick</c> — must give it an explicit arm. A command that does not
    /// persist is handled once by <c>OrderApplier</c> and no per-tick router ever sees it. DW-206 was exactly the
    /// failure of that distinction: <see cref="UnitCommand.Build"/> persisted but had no arm in <c>CombatSystem</c>, so
    /// it silently inherited idle auto-combat through the switch's <c>default:</c> and a building worker chased
    /// enemies. Fixing that one member closed the INSTANCE; this classification plus the guards that consume it close
    /// the CLASS — a newly appended member is unclassified here, which turns the suite red until someone decides which
    /// kind it is, and if it persists the completeness guard then demands its explicit routing arms.</para>
    ///
    /// <para><b>Appending a member?</b> Add its arm below. If it PERSISTS you must also give it an explicit case in
    /// <c>CombatSystem.Tick</c>'s command switch AND in <c>CombatSystem.TickNonCombatant</c>'s router (an intentionally
    /// inert command joins the no-op case group there) — <c>CombatCommandSwitchCompletenessTests</c> enforces both.</para>
    ///
    /// <para>Classification-only: no sim state, no <see cref="SimChecksum"/> input, never called per tick.</para>
    /// </summary>
    public static class UnitCommandTraits
    {
        /// <summary>
        /// True when <paramref name="cmd"/> is stored in <c>EntityWorld.CommandState</c> and drives per-tick behaviour;
        /// false when it is consumed at apply time (a building order, an item action, a cast intent, or a wire-only
        /// spelling that <c>OrderApplier</c> rewrites) and never reaches a per-tick router.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The value is not a declared <see cref="UnitCommand"/> member, or it is a member appended without being
        /// classified here. The throw is the DW-645 red test: classify the new member rather than deleting it.
        /// </exception>
        public static bool PersistsAsCommandState(UnitCommand cmd)
        {
            switch (cmd)
            {
                // ── PERSISTING — stored in CommandState; every per-tick command router must case it explicitly ──
                case UnitCommand.Idle:           // the resting state (also the fallback every invalid order reverts to)
                case UnitCommand.Move:           // pure navigation
                case UnitCommand.AttackMove:     // navigation + engage en route
                case UnitCommand.Stop:           // stationary, engage in range
                case UnitCommand.HoldPosition:   // stationary anchored, engage in range
                case UnitCommand.Build:          // walking to a build site — BuildingSystem drives it (DW-206)
                case UnitCommand.AttackTarget:   // force-attack one entity
                case UnitCommand.AttackBuilding: // force-attack one building
                case UnitCommand.Patrol:         // walk a waypoint ring
                case UnitCommand.Follow:         // track a friendly
                case UnitCommand.PickupItem:     // hero walking to a ground item — ItemSystem drives it
                    return true;

                // ── NOT PERSISTING — consumed by OrderApplier (or the system it delegates to) and never stored ──
                case UnitCommand.PatrolAppend:   // wire-only: rewritten to Patrol before it is stored
                case UnitCommand.CastAbility:    // intent: writes PendingCast*, restores the prior CommandState
                case UnitCommand.Train:          // building order
                case UnitCommand.SetRally:       // building order
                case UnitCommand.ReviveHero:     // building order
                case UnitCommand.UseItem:        // hero inventory action, applied immediately
                case UnitCommand.DropItem:       // hero inventory action, applied immediately
                case UnitCommand.BuyItem:        // shop-building order
                case UnitCommand.StartResearch:  // building order
                case UnitCommand.CancelResearch: // faction-wide research order
                case UnitCommand.DslEvent:       // names a custom-event registry index, not an entity
                case UnitCommand.Concede:        // names a faction, not an entity
                case UnitCommand.CancelTrain:    // building order
                    return false;

                default:
                    throw new ArgumentOutOfRangeException(nameof(cmd), cmd,
                        "DW-645: this UnitCommand is not classified as persisting/non-persisting. A newly appended " +
                        "member must be added to UnitCommandTraits.PersistsAsCommandState; if it PERSISTS as a " +
                        "CommandState it also needs explicit arms in CombatSystem.Tick's command switch and in " +
                        "CombatSystem.TickNonCombatant, or it silently inherits idle auto-combat (the DW-206 defect).");
            }
        }
    }

    /// <summary>
    /// Worker/gatherer state machine. Inactive = non-gatherer (combat unit).
    /// </summary>
    public enum GatherState : byte
    {
        Inactive        = 0, // Not a gatherer — CombatSystem controls this unit
        Idle            = 1, // Gatherer looking for a resource node
        MovingToResource= 2, // En route to assigned resource node
        Gathering       = 3, // At node, extracting supply
        MovingToBase    = 4, // Carrying load back to faction base
    }

    /// <summary>
    /// Flags for entity alive/dead state and other per-entity flags.
    /// </summary>
    [Flags]
    public enum EntityFlags : byte
    {
        None = 0,
        Alive = 1 << 0,
        Moving = 1 << 1,
        Attacking = 1 << 2,
    }

    /// <summary>
    /// Faction identifier for entities.
    /// </summary>
    public enum Faction : byte
    {
        Neutral = 0,
        Player1 = 1,
        Player2 = 2,
        Player3 = 3,
        Player4 = 4,
        Player5 = 5,
        Player6 = 6,
        Player7 = 7,
        Player8 = 8,
    }

    /// <summary>
    /// Struct-of-Arrays entity storage for the simulation layer.
    /// All arrays are indexed by entity ID. Deterministic iteration by ascending ID.
    /// </summary>
    public class EntityWorld
    {
        public const int MAX_ENTITIES = 4096;

        /// <summary>
        /// Maximum waypoints in a single unit's patrol route (Story 1.12). The patrol-route SoA ring is a
        /// fixed-capacity flat buffer sized <see cref="MAX_ENTITIES"/> * this — the SoA-safe way to store a
        /// variable-length route without a per-entity dynamic list (which would break determinism). Named
        /// so the determinism analyzer's CHM0004 (bare-cap) advisory stays clean.
        /// </summary>
        public const int MAX_PATROL_WAYPOINTS = 8;

        /// <summary>
        /// Maximum pending orders in a single unit's shift-queue (Story 2.12, FR-74) — both the per-entity queue
        /// depth AND the stride of the flat order-ring buffers (<see cref="OrderQueueCmd"/>,
        /// <see cref="OrderQueueTargetX"/>, <see cref="OrderQueueTargetZ"/>), indexed
        /// <c>id * MAX_ORDER_QUEUE + slot</c> — the SoA-safe fixed-capacity ring, exactly like
        /// <see cref="MAX_PATROL_WAYPOINTS"/> / <see cref="MAX_ABILITIES_PER_UNIT"/>. Named so the determinism
        /// analyzer's CHM0004 (bare-cap) advisory stays clean. Freely RAISABLE later at ZERO determinism cost: the
        /// v9 order-queue fold is count-driven (it hashes each unit's <see cref="OrderQueueCount"/> + only its USED
        /// slots, never the stride or empty slots), so raising this moves no golden.
        /// </summary>
        public const int MAX_ORDER_QUEUE = 8;

        /// <summary>
        /// Maximum active abilities a single unit can carry (Story 2.4a, FR-11) — both the per-entity ability-slot
        /// count AND the stride of the flat ability buffers (<see cref="AbilityId"/>, <see cref="AbilityCooldownTicks"/>),
        /// indexed <c>id * MAX_ABILITIES_PER_UNIT + slot</c> (the SoA-safe fixed-capacity ring, like
        /// <see cref="MAX_PATROL_WAYPOINTS"/>). Named so the determinism analyzer's CHM0004 (bare-cap) advisory stays
        /// clean. Freely RAISABLE later at ZERO determinism cost: the v7 cooldown fold is count-driven (it hashes each
        /// unit's <see cref="AbilityCount"/> + only its USED cooldown slots, never the stride or empty slots), so a
        /// 2-ability unit hashes identically at cap 4 or 12 — raising this moves no golden. The wire packs the slot as
        /// a full int (no wire limit); the practical ceiling is command-card real-estate (~6–8 buttons).
        /// </summary>
        public const int MAX_ABILITIES_PER_UNIT = 4;

        /// <summary>
        /// <see cref="PendingCastSlot"/> sentinel for "no cast queued this tick" (valid slots are 0..MAX-1, so
        /// <see cref="byte.MaxValue"/> can never be a real slot). Named so the analyzer's CHM0004 magic-number
        /// advisory stays clean.
        /// </summary>
        public const byte NO_PENDING_CAST = byte.MaxValue;

        /// <summary>
        /// <see cref="HeroIndex"/> sentinel for "this entity is not a hero" (Story 3.2, D-8). −1, distinct from any
        /// valid packed <see cref="HeroStore"/> handle (a live pack is <c>(Generation &lt;&lt; 8) | slot</c> ≥ 0).
        /// Named so the determinism analyzer's CHM0004 magic-number advisory stays clean.
        /// </summary>
        public const int HERO_NONE = -1;

        /// <summary>
        /// Default per-unit separation radius (Story 1.13) applied when <c>collision_radius</c> is omitted or
        /// authored &lt;= 0. Chosen as 1.0 so two default units sum to a 2.0 contact distance — identical to the
        /// legacy flat <c>MovementSystem.SEPARATION_QUERY_RADIUS</c>, so unauthored units keep their pre-1.13
        /// separation contact (the existing goldens move ONLY from the new moving-bias, not the radius math).
        /// A named <c>static readonly Fixed</c> (not a bare literal) so the determinism analyzer's CHM0004
        /// magic-cap advisory stays clean.
        /// </summary>
        public static readonly Fixed DEFAULT_COLLISION_RADIUS = Fixed.One;

        /// <summary>
        /// Engine cap on an authored <c>collision_radius</c> (Story 1.13). 1.0 keeps the largest possible summed
        /// contact (2 * MAX = 2.0) within the UNCHANGED spatial-hash query window (<c>SEPARATION_QUERY_RADIUS</c>)
        /// and its 32-slot neighbour buffer, so a large authored radius can never make a real contact be silently
        /// MISSED by the neighbour scan. A future story wanting bigger units widens BOTH the query radius and this
        /// cap together (and re-checks the buffer). See the query-radius safety note in the Story 1.13 dev notes.
        /// </summary>
        public static readonly Fixed MAX_COLLISION_RADIUS = Fixed.One;

        // --- Determinism (shared, NOT per-entity) ---
        /// <summary>
        /// Default seed for <see cref="Rng"/> so the parameterless ctor (used widely by scenarios and
        /// tests) always yields a valid deterministic stream. The match bootstrap / replay restore
        /// reseeds via <c>world.Rng.Seed(matchSeed)</c>. Recognizable nonzero value (the SplitMix64 gamma).
        /// </summary>
        public const ulong DEFAULT_RNG_SEED = 0x9E3779B97F4A7C15UL;

        /// <summary>
        /// The single shared deterministic RNG for this world — the ONLY randomness source in the sim
        /// (AR-13). Reached by every system through the <c>world</c> they already receive, and its
        /// <see cref="SimRng.State"/> is folded into <see cref="SimChecksum"/>. A fixed reference with
        /// mutable internal state (exactly like the readonly SoA arrays): reseed via <see cref="SimRng.Seed"/>,
        /// never reassign. NOT a per-entity array.
        /// </summary>
        public SimRng Rng { get; }

        // --- SoA arrays ---
        public readonly EntityFlags[] Flags;
        public readonly FixedVec3[] Position;
        public readonly FixedVec3[] PrevPosition; // For interpolation
        public readonly FixedVec3[] Velocity;
        /// <summary>
        /// Authored base move speed — immutable within a tick; the source the
        /// <see cref="ProjectChimera.Effects.ModifierSystem"/> recompute reads (Story 2.2a / AR-9).
        /// </summary>
        public readonly Fixed[] BaseMoveSpeed;
        /// <summary>
        /// Effective max move speed = <see cref="BaseMoveSpeed"/> + active modifier deltas; the value
        /// <see cref="ProjectChimera.Navigation.MovementSystem"/> reads. Recomputed by ModifierSystem only on a tick
        /// where a delta changed. In 2.2a it always equals <see cref="BaseMoveSpeed"/> (no modifier exists yet), so
        /// it is NOT hashed — it folds into SimChecksum with the ModifierStore in 2.2b.
        /// </summary>
        public readonly Fixed[] EffectiveMoveSpeed;
        public readonly Fixed[] Health;
        /// <summary>Authored base max health — immutable in-tick; source for the effective recompute (Story 2.2a / AR-9).</summary>
        public readonly Fixed[] BaseMaxHealth;
        /// <summary>Effective max health = <see cref="BaseMaxHealth"/> + modifier deltas; the Health-clamp ceiling combat/heal read. == Base in 2.2a (unhashed; folds in 2.2b).</summary>
        public readonly Fixed[] EffectiveMaxHealth;
        public readonly Faction[] FactionOf;
        public readonly FixedVec3[] MoveTarget;   // Where entity is heading
        public readonly int[] AttackTarget;        // Entity ID of attack target (-1 = none)
        public readonly Fixed[] AttackCooldown;    // Time until next attack
        public readonly Fixed[] AttackRange;
        /// <summary>Authored base attack damage — immutable in-tick; source for the effective recompute. The ONE modifier-affected stat sourced from <see cref="ProjectChimera.Core.Definitions.UnitDefinition"/> via <see cref="ApplyUnitDefinition"/> (Story 2.2a / AR-9).</summary>
        public readonly Fixed[] BaseAttackDamage;
        /// <summary>Effective attack damage = <see cref="BaseAttackDamage"/> + modifier deltas; the value <see cref="ProjectChimera.Combat.CombatSystem"/> deals. == Base in 2.2a (unhashed; folds in 2.2b).</summary>
        public readonly Fixed[] EffectiveAttackDamage;
        /// <summary>Authored base armor — flat post-matrix damage reduction (Story 2.6, Decision #6). Immutable in-tick;
        /// the source for the effective recompute. Copied from <see cref="Core.Definitions.UnitDefinition.Armor"/> via
        /// <see cref="ApplyUnitDefinition"/>. Spawn-constant/peer-identical → NOT folded (the <see cref="BaseAttackDamage"/> posture).</summary>
        public readonly Fixed[] BaseArmor;
        /// <summary>Effective armor = <see cref="BaseArmor"/> + modifier deltas (aura/buff grants). The flat value
        /// <see cref="ProjectChimera.Combat.DamageResolver"/> subtracts from post-matrix damage (floored at 0). It mutates
        /// mid-match the moment an armor modifier exists → FOLDED into <see cref="SimChecksum"/> (v8) — the ONE intentional
        /// fold of Story 2.6.</summary>
        public readonly Fixed[] EffectiveArmor;
        public readonly Fixed[] AttackSpeed;       // Seconds between attacks

        // --- Ability resource pool (Story 2.2a substrate; ModifierStore debits it 2.2b; sourced from the def 2.4a) ---
        /// <summary>
        /// Current ability-resource (energy) pool — the single ability-cost pool (architecture: Energy + MaxEnergy,
        /// no separate Mana). Story 2.4a: <see cref="ApplyUnitDefinition"/> now sets it to <see cref="MaxEnergy"/>
        /// (start full; no regen yet) from <see cref="Core.Definitions.UnitDefinition.MaxEnergy"/>; the
        /// <see cref="ProjectChimera.Effects.AbilityCastSystem"/> debits it on cast (refuse-when-insufficient via
        /// <c>ModifierStore.TryDebitEnergy</c>). FOLDED into <see cref="SimChecksum"/> since v6 (it mutates mid-match).
        /// Create-defaulted to Zero (a unit with no def / before mapping has no pool).
        /// </summary>
        public readonly Fixed[] Energy;
        /// <summary>Maximum ability-resource capacity, authored on <see cref="Core.Definitions.UnitDefinition.MaxEnergy"/>
        /// and copied via <see cref="ApplyUnitDefinition"/> (the single-mapper rule, Story 2.4a). Spawn-constant
        /// authored data → NOT folded (peer-identical, like <c>BaseMaxHealth</c>). Create-defaulted to Zero.</summary>
        public readonly Fixed[] MaxEnergy;

        /// <summary>
        /// Per-entity boolean status the active <see cref="ProjectChimera.Effects.Modifier"/> instances impose
        /// (the OR-union over a unit's modifiers — Stunned/Rooted/Silenced/Disarmed/Invulnerable). Written by the
        /// Story 2.2b <c>ModifierStore</c> on apply/remove; <c>Create</c>-defaulted to <see cref="StatusFlags.None"/>
        /// (so a recycled slot never inherits the prior occupant's status). DW-266 wired every flag to a real
        /// runtime consumer: <c>Stunned</c> → the whole-unit gate in <c>CombatSystem.Tick</c> + the
        /// <c>MovementSystem</c> anchor + the <c>AbilityCastSystem</c> refusal; <c>Rooted</c> → the
        /// <c>MovementSystem</c> anchor; <c>Disarmed</c> → <c>CombatSystem</c>'s two damage choke points;
        /// <c>Silenced</c> → the <c>AbilityCastSystem</c> refusal; <c>Invulnerable</c> → <c>DamageResolver.Apply</c>
        /// AND (DW-620, "Invulnerable = death-immunity") <c>DamageResolver.KillEntity</c>, so the direct-kill paths
        /// that bypass Apply — ModifierStore's DW-325 ceiling collapse, a self-lethal <c>cost_health</c> cast — are
        /// refused too. Self-costs stay spend-able; only the death is refused.
        /// It is mutable sim truth
        /// the moment a status modifier exists → FOLDED into <see cref="SimChecksum"/> (v6). Lives here (not in the
        /// store) so future systems read it for free via <c>world</c>, exactly like <see cref="EffectiveAttackDamage"/>.
        /// </summary>
        public readonly StatusFlags[] StatusFlagsOf;

        public readonly DamageType[] DamageTypeOf;
        public readonly ArmorType[] ArmorTypeOf;

        // --- Vision ---
        /// <summary>How far this unit can see (world units). Used by FogOfWarSystem.</summary>
        public readonly Fixed[] VisionRange;

        // --- Terrain elevation (Story 6.3) ---
        /// <summary>
        /// Per-entity terrain elevation (world Y), sampled ONCE at spawn from the injected <see cref="ElevationGrid"/>
        /// (a deterministic clamped integer cell lookup — never Godot <c>Image</c> interpolation, never an OOB read).
        /// A dedicated SoA array — NOT <c>Position.Y</c> (which is already folded as position + could leak through
        /// MovementSystem integration). Terrain-derived, so it is DEFAULTED (not written) in <see cref="ApplyUnitDefinition"/>
        /// and set from the grid in <see cref="Create"/> — a recycled slot must never inherit the prior occupant's
        /// elevation (the SoA-recycle trap). FOLDED into <see cref="SimChecksum"/> (v15) so a divergent elevation desyncs
        /// detectably. Flat/legacy maps (null grid) leave every entry at <see cref="Fixed.Zero"/>.
        /// </summary>
        public readonly Fixed[] Elevation;

        /// <summary>
        /// Sim-global creator toggle (Story 6.3): when true, <see cref="EffectiveVisionRange"/> widens a unit's stamped
        /// vision radius by an elevation-derived <see cref="Fixed"/> bonus. Default false ⇒ the fog Grid is
        /// byte-for-byte identical to the pre-feature behaviour (the bonus term is not applied at all). Set once by
        /// <c>ScenarioApplier</c> from <c>ScenarioData.HeightAdvantageVision</c>; NOT per-entity, NOT folded (the
        /// fog Grid is not in <see cref="SimChecksum"/> and no sim system consumes it — see the CanonicalModelHash note).
        /// </summary>
        public bool HeightAdvantageVision;

        /// <summary>
        /// Sim-global per-elevation-step vision bonus (Story 6.3), in world units, applied only when
        /// <see cref="HeightAdvantageVision"/> is on. Resolved once from <c>ScenarioData.HeightVisionBonusPerStep</c>
        /// (the single float→Fixed boundary) by <c>ScenarioApplier</c>. Default <see cref="Fixed.Zero"/>.
        /// </summary>
        public Fixed HeightVisionBonusPerStep;

        /// <summary>
        /// The world-unit height of ONE elevation step for the height-advantage vision bonus (Story 6.3). Fixed at 1
        /// world height-unit — a unit standing 1 world-unit higher earns one <see cref="HeightVisionBonusPerStep"/>
        /// increment. A documented named constant (can become authored data later) so the determinism analyzer's
        /// magic-number advisory stays clean; the floor-to-whole-steps math in <see cref="EffectiveVisionRange"/> is
        /// exactly <c>floor(Elevation / this)</c>.
        /// </summary>
        public const int HEIGHT_STEP_WORLD_UNITS = 1;

        /// <summary>
        /// The injected finalized-terrain elevation grid (Story 6.3), or null for a flat/legacy map. Godot builds it
        /// at load time from the restored Terrain3D heightmap and hands it in via <see cref="SetElevationGrid"/> BEFORE
        /// scenario apply, so <see cref="Create"/> samples it for every spawn path uniformly. Null ⇒ elevation is
        /// <see cref="Fixed.Zero"/> everywhere (no behaviour change vs pre-feature).
        /// </summary>
        private ElevationGrid? _elevationGrid;

        /// <summary>
        /// Story 6.5: the injected pathability grid (painted ∪ slope-derived blocked cells), or null for a
        /// flat/legacy map with nothing blocked. Godot builds it at load time (decode + slope-derive) and hands it
        /// in via <see cref="SetPathabilityGrid"/> BEFORE scenario apply. Consumed by <c>MovementSystem</c> to keep
        /// a live unit out of a blocked cell. Null / empty ⇒ an exact no-op (byte-identical to pre-feature). Never
        /// reassigned per-tick — a load-time seam only.
        /// </summary>
        public ProjectChimera.Navigation.PathabilityGrid? Pathability { get; private set; }

        // --- AoE ---
        /// <summary>
        /// Splash radius (world units) applied when a projectile from this unit hits.
        /// 0 = no splash. Set from UnitDefinition.SplashRadius; used by ProjectileSystem.
        /// </summary>
        public readonly Fixed[] SplashRadius;

        // --- Attack delivery (Story 3.12) ---
        /// <summary>
        /// Per-unit attack delivery — <see cref="AttackDelivery.Hitscan"/> (instant damage, no projectile) or
        /// <see cref="AttackDelivery.Projectile"/> (spawns a tracking projectile) — decoupled from
        /// <see cref="AttackRange"/> (Story 3.12). Set from <c>UnitDefinition.ResolveDelivery(AttackRange[id])</c> in
        /// <see cref="ApplyUnitDefinition"/> (the single mapper), read in-sim EVERY combat tick by
        /// <see cref="ProjectChimera.Combat.CombatSystem"/> to branch instant-vs-projectile (replacing the deleted
        /// range-threshold). FOLDED into <see cref="SimChecksum"/> as <c>(int)</c> (v10) — a peer divergence in which
        /// delivery a unit uses changes combat resolution and must desync detectably (the mandate of Story 3.12). The
        /// <c>Create</c> default is <see cref="AttackDelivery.Hitscan"/> (a recycled/non-def slot never inherits a prior
        /// delivery); <see cref="AttackDelivery.Hitscan"/> == 0 so <c>Array.Clear</c> restores the default.
        /// </summary>
        public readonly AttackDelivery[] Delivery;

        /// <summary>
        /// Per-unit projectile speed (world units/second, <see cref="Fixed"/>) applied to a spawned projectile when
        /// <see cref="Delivery"/> is <see cref="AttackDelivery.Projectile"/> (Story 3.12). Set from
        /// <c>Fixed.FromFloat(def.ProjectileSpeed)</c> in <see cref="ApplyUnitDefinition"/> (the single float→Fixed
        /// boundary), copied to <c>ProjectileStore.Speed</c> at fire time and honoured at the projectile advance step.
        /// FOLDED into <see cref="SimChecksum"/> as <c>.Raw</c> (v10, the story mandate) even though it is an authored
        /// spawn-constant. The <c>Create</c> default is <see cref="ProjectChimera.Combat.ProjectileSystem.PROJECTILE_SPEED"/>
        /// (18, the old global) so a recycled/non-def slot fires at the documented fallback speed; <c>Array.Clear</c>
        /// restores 0, matching a fresh (pre-<c>Create</c>) world.
        /// </summary>
        public readonly Fixed[] ProjectileSpeed;

        // --- Hero XP bounty (Story 3.13) ---
        /// <summary>
        /// Per-unit XP bounty (<see cref="Fixed"/>, world gold-equivalent) awarded to every hostile hero within that
        /// hero's <c>xp_share_radius</c> when this unit dies (Story 3.13, D7). Set from
        /// <c>Fixed.FromInt(def.ResolveXpBounty())</c> in <see cref="ApplyUnitDefinition"/> (the single mapper; authored
        /// value else <c>CostOre + CostCrystal</c>). Recorded at the death choke point (<c>DamageResolver.KillEntity</c>)
        /// into the transient <c>DeathFeed</c>, drained by <see cref="ProjectChimera.Combat.HeroXpSystem"/> which credits
        /// hostile heroes in range. FOLDED into <see cref="SimChecksum"/> as <c>.Raw</c> (v11), following the Story 3.12
        /// spawn-constant-folding convention (a peer divergence in a bounty changes XP/level outcomes). The <c>Create</c>
        /// default is <see cref="Fixed.Zero"/> (a recycled/non-def slot awards nothing); <c>Array.Clear</c> restores 0.
        /// </summary>
        public readonly Fixed[] XpBounty;

        // --- Killer attribution (Story 7.5) ---
        /// <summary>
        /// The attacker ENTITY id that landed this unit's killing blow (−1 = none/unknown — non-combat destroys
        /// leave it). WRITTEN ONLY by <c>DamageResolver.KillEntity</c> (the single death choke point, BEFORE
        /// <see cref="Destroy"/> recycles the slot); READ by <c>ScenarioDirector</c>'s death diff to fill the
        /// <c>unit_dies</c> event payload (<c>event.killer</c>). Derived attribution state — deliberately NOT
        /// folded into <see cref="SimChecksum"/> (the <c>_prevFlags</c> basis: it only feeds the trigger event
        /// payload, whose effects fold via the DSL stores). <see cref="Create"/> defaults it to −1 — a recycled
        /// slot must NEVER carry the prior occupant's killer (the SoA-recycle trap).
        /// </summary>
        public readonly int[] KillerOf;

        /// <summary>
        /// The killing blow's faction slot, SNAPSHOTTED at <c>DamageResolver.KillEntity</c> (−1 = none/Neutral;
        /// Player1 → 0). Snapshotted rather than derived from <see cref="KillerOf"/> because the attacker entity
        /// may itself die/recycle before the director's death diff reads the payload. Same NOT-folded /
        /// Create-defaults-−1 contract as <see cref="KillerOf"/>.
        /// </summary>
        public readonly int[] KillerFactionOf;

        /// <summary>
        /// DW-367 — the per-tick transient death LOG (one record per <c>DamageResolver.KillEntity</c>, the single
        /// death choke point). Unlike the per-slot <see cref="KillerOf"/>/<see cref="KillerFactionOf"/> SoA (which a
        /// same-tick die→recycle→die overwrites, losing the first kill's event and credit), the log carries EVERY
        /// combat death of the tick with its own attribution for <c>ScenarioDirector</c>'s <c>unit_dies</c> source.
        /// Same derived-attribution posture as the SoA pair: NOT folded into <see cref="SimChecksum"/> (drained +
        /// wiped by the director at its flags-snapshot horizon; capped and inert when no director reads it).
        /// </summary>
        public readonly DeathLog DeathLog;

        // --- Separation / formation (Story 1.13, DG-2 / FR-54) ---
        /// <summary>
        /// Per-unit separation radius. Summed with a neighbour's (<c>CollisionRadius[i] + CollisionRadius[j]</c>)
        /// to form the per-pair contact threshold in <see cref="ProjectChimera.Navigation.MovementSystem"/>,
        /// replacing the old flat radius. Set from UnitDefinition.collision_radius at spawn (clamped to
        /// [<see cref="DEFAULT_COLLISION_RADIUS"/> on &lt;=0, <see cref="MAX_COLLISION_RADIUS"/>]). Read in-sim
        /// every tick → FOLDED into <see cref="SimChecksum"/> (v5).
        /// </summary>
        public readonly Fixed[] CollisionRadius;

        /// <summary>
        /// Per-unit crowd-steering precedence (Yield/Normal/Push). A Push unit is not displaced by a Yield
        /// neighbour it contacts. Set from UnitDefinition.separation_priority at spawn. Read in-sim every tick by
        /// MovementSystem → FOLDED into <see cref="SimChecksum"/> (v5). The <c>*Of</c> suffix mirrors
        /// <see cref="DamageTypeOf"/>/<see cref="ArmorTypeOf"/> so the field name does not collide with the
        /// <see cref="Core.SeparationPriority"/> enum type.
        /// </summary>
        public readonly SeparationPriority[] SeparationPriorityOf;

        /// <summary>
        /// Per-unit archetype, parsed from UnitDefinition.category at spawn. Read ONLY by the presentation-side
        /// <see cref="ProjectChimera.Navigation.FormationPlanner"/> (front/back role layout). NOT folded into the
        /// determinism checksum — it is presentation-read and constant, exactly like <see cref="MeshType"/>; the
        /// formation it shapes is computed once on the issuer and transmitted as a Fixed MoveTarget, so a
        /// divergent local category cannot desync.
        /// </summary>
        public readonly UnitCategory[] CategoryOf;

        /// <summary>
        /// Per-unit attacker capability — which target domains this unit may attack (Story 2.9a, FR-11/FR-12), parsed
        /// from <c>UnitDefinition.attack_domains</c> at spawn. Read in-sim by target selection (both SpatialHash acquire
        /// paths and the anti-building tick). Default <see cref="AttackDomain.All"/>. NOT folded into the determinism
        /// checksum — it is an authored-immutable, combat-read field, exactly like <see cref="CategoryOf"/> /
        /// <see cref="DamageTypeOf"/> / <see cref="ArmorTypeOf"/> (a divergent authored choice changes which target is
        /// hit → the affected entity's already-folded Position/Health moves, caught transitively).
        /// </summary>
        public readonly AttackDomain[] AttackDomainOf;

        /// <summary>
        /// Per-unit classification tags (Organic/Mechanical/Magical) — Story 2.11 (DG-4 / FR-56), parsed from
        /// <c>UnitDefinition.tags</c> at spawn. Read in-sim by the effect engine's target selection (the
        /// <c>SearchArea.require_tag</c> predicate via <c>TargetMatcher</c>, and the single-target <c>LeafEffect</c>
        /// gate via <c>EffectExecutor</c>). Default <see cref="UnitTag.None"/> (an untagged unit is matched by no tag
        /// predicate). NOT folded into the determinism checksum — an authored-immutable, effect-read field, exactly
        /// like <see cref="AttackDomainOf"/> / <see cref="CategoryOf"/> / <see cref="DamageTypeOf"/> (a divergent
        /// authored tag changes which target an effect selects → the affected entity's already-folded
        /// Health/ModifierStore/Position moves, caught transitively).
        /// </summary>
        public readonly UnitTag[] TagsOf;

        // --- Supply ---
        /// <summary>Supply population this entity occupies (0 = workers/buildings, 1+ = combat).</summary>
        public readonly byte[] SupplyCost;

        // --- Presentation ---
        /// <summary>
        /// Index of this entity's unit definition within its faction's Units list.
        /// Purely presentational — selects which mesh MultiMeshBridge renders so each
        /// unit type looks distinct. Never read by the simulation and excluded from the
        /// determinism checksum. Defaults to 0 (the worker / first unit) for any entity
        /// a spawn site forgets to tag.
        /// </summary>
        public readonly byte[] MeshType;

        /// <summary>
        /// Per-unit combat-feedback override resolved from <c>UnitDefinition.CombatFeedback</c> (Story 2.7).
        /// Presentation-read ONLY — the simulation never reads it; NOT folded into the determinism checksum
        /// (exactly like <see cref="MeshType"/>/<see cref="CategoryOf"/>). This is EntityWorld's FIRST reference-typed
        /// per-entity SoA array: a recycled slot MUST be null-reset in <see cref="Create"/> so it can never inherit a
        /// prior occupant's profile (the 1.12/1.13/2.6 SoA-recycle trap). Elements are null when the unit authored no
        /// override (⇒ the tuned event-type defaults play). Written via the single <see cref="ApplyUnitDefinition"/>
        /// mapper (A2). DW-213: the element type is nullable-ANNOTATED (<c>CombatFeedbackProfile?[]</c>) now that this
        /// file is inside the <c>#nullable enable</c> context — null elements are the natural reference default and
        /// every read site null-guards, so the annotation documents the contract the code already honours.
        /// </summary>
        public readonly CombatFeedbackProfile?[] FeedbackProfile;

        /// <summary>
        /// Per-unit reference to the <see cref="Core.Definitions.UnitDefinition"/> this entity was spawned from
        /// (null for a def-less spawn / a fresh or recycled slot). Story 3.17: the editor delete→undo restore path
        /// re-derives every def-derived authored field by routing this def back through the single
        /// <see cref="ApplyUnitDefinition"/> mapper — so a widened snapshot never has to hand-copy the ~30 authored
        /// fields (the recurring RestoreUnit drop-debt). The only VALUE-writing site is <see cref="ApplyUnitDefinition"/>
        /// (the A2 channel every def-based spawn already uses, so it captures scenario/production/editor spawns
        /// uniformly); it is null-reset in <see cref="Create"/> (so a recycled slot never carries a prior occupant's
        /// def — the SoA-recycle trap) and bulk-cleared in <see cref="Clear"/>. A NON-FOLDED reference SoA — EXCLUDED
        /// from <see cref="SimChecksum"/> / <c>CanonicalModelHash</c>
        /// exactly like <see cref="FeedbackProfile"/> (editor-only state flow; the def is spawn-constant and
        /// peer-identical, and every gameplay-affecting field it derives is already folded on its own). DW-213:
        /// nullable-ANNOTATED (<c>UnitDefinition?[]</c>) now that this file is inside the <c>#nullable enable</c>
        /// context; null elements are the natural reference default and every read site null-guards.
        /// </summary>
        public readonly UnitDefinition?[] SourceDefinition;

        // --- Command state ---
        /// <summary>Active order governing autonomous combat behaviour (set by player commands).</summary>
        public readonly UnitCommand[] CommandState;

        /// <summary>
        /// Final destination for Move and AttackMove orders.
        /// CombatSystem steers toward this after an AttackMove engagement ends.
        /// Patrol drives this (and MoveTarget) from its current waypoint each leg.
        /// </summary>
        public readonly FixedVec3[] CommandGoal;

        // --- Persistent command targets / patrol route (Story 1.12, DG-1 / FR-53) ---
        /// <summary>
        /// PERSISTENT, player-issued target this unit's command references: the enemy id for AttackTarget,
        /// the friendly id for Follow (-1 = none). Distinct from <see cref="AttackTarget"/>, which is the
        /// TRANSIENT live combat target recomputed each tick by the spatial hash. One array serves both
        /// command states because a unit is in exactly one CommandState at a time, so the two uses never
        /// overlap. Folded into <see cref="SimChecksum"/> (v4).
        /// </summary>
        public readonly int[] CommandTarget;

        /// <summary>
        /// Flat patrol-route ring, indexed <c>id * MAX_PATROL_WAYPOINTS + k</c> — the ordered waypoints a
        /// Patrol unit walks. Slots at <c>k &gt;= PatrolCount</c> are unread (and never hashed), so a
        /// recycled slot needs no waypoint reset (only <see cref="PatrolCount"/> is reset in Create).
        /// </summary>
        public readonly FixedVec3[] PatrolWaypoints;
        /// <summary>Patrol route length (0 = no route). Folded into <see cref="SimChecksum"/> (v4).</summary>
        public readonly byte[] PatrolCount;
        /// <summary>Current patrol-leg target waypoint index. Folded into <see cref="SimChecksum"/> (v4).</summary>
        public readonly byte[] PatrolIndex;
        /// <summary>Patrol walk direction: +1 forward / -1 back (reverse-at-ends). Folded into <see cref="SimChecksum"/> (v4).</summary>
        public readonly sbyte[] PatrolDir;

        // --- Shift-queued order ring (Story 2.12, FR-74) — flat per-slot buffers indexed id * MAX_ORDER_QUEUE + slot ---
        /// <summary>
        /// Flat order-queue ring (the masked <see cref="UnitCommand"/> per pending order), indexed
        /// <c>id * MAX_ORDER_QUEUE + slot</c>. A consume-once FIFO (distinct from the looping <see cref="PatrolWaypoints"/>
        /// route): <c>OrderApplier</c> APPENDS a Shift-issued order here, and <c>OrderQueueSystem</c> POPS the head when
        /// the active order completes and dispatches it through the shared <c>OrderApplier.ApplyActiveOrder</c> core.
        /// Runtime-mutable mid-match → <b>FOLDED into <see cref="SimChecksum"/> (v9)</b>, count-driven by
        /// <see cref="OrderQueueCount"/> (slots past the count are unread/unhashed, the <see cref="PatrolCount"/>
        /// discipline). Only the low-7-bit command is stored (the wire's 0x80 queued flag is masked before append).
        /// </summary>
        public readonly byte[] OrderQueueCmd;
        /// <summary>Flat order-queue target X per pending order (<see cref="Fixed"/> raw / packed id, the wire's TargetX),
        /// indexed <c>id * MAX_ORDER_QUEUE + slot</c>. Folded v9, count-driven (Story 2.12).</summary>
        public readonly int[] OrderQueueTargetX;
        /// <summary>Flat order-queue target Z per pending order (<see cref="Fixed"/> raw, the wire's TargetZ), indexed
        /// <c>id * MAX_ORDER_QUEUE + slot</c>. Folded v9, count-driven (Story 2.12).</summary>
        public readonly int[] OrderQueueTargetZ;
        /// <summary>
        /// Number of pending queued orders per entity (0 = empty). Runtime-mutable mid-match → FOLDED into
        /// <see cref="SimChecksum"/> (v9) as the count itself AND as the cross-platform-safe count-driven loop bound
        /// (exactly like <see cref="PatrolCount"/> / <see cref="AbilityCount"/>). MANDATORY <see cref="Create"/> reset:
        /// a recycled slot must never inherit the prior occupant's queued orders (the SoA-recycle trap).
        /// </summary>
        public readonly byte[] OrderQueueCount;

        /// <summary>
        /// Story 2.12 (R1 fix): the sim-authoritative ACTIVE order per entity — the last order dispatched by
        /// <c>OrderApplier.ApplyActiveOrder</c>, stored as the final masked 0-13 <see cref="UnitCommand"/> byte.
        /// <see cref="OrderQueueSystem"/> keys completion on THIS, never on <see cref="CommandState"/>, because
        /// presentation (FlowFieldBridge/PathRequestSystem, <c>src/UI</c>) mutates <see cref="CommandState"/> off-tick —
        /// it flips an arrived Move→Stop at the flow field's wider 1.5u radius, BEFORE the queue's 0.5u pop — which would
        /// strand every order queued behind a plain Move. Written ONLY by the deterministic dispatch (same on every peer),
        /// so — exactly like <see cref="CommandState"/>/<see cref="CommandGoal"/>/<see cref="MoveTarget"/> — it is NOT
        /// folded into <see cref="SimChecksum"/> (a divergence is caught transitively via the folded
        /// <see cref="OrderQueueCount"/> + <see cref="Position"/>). MANDATORY <see cref="Create"/> reset (0 = Idle = ready).
        /// </summary>
        public readonly byte[] ActiveOrderCmd;

        // --- Abilities (Story 2.4a, FR-11) — flat per-slot buffers indexed id * MAX_ABILITIES_PER_UNIT + slot ---
        /// <summary>
        /// Registry index of the ability in each slot (−1 = empty), indexed <c>id * MAX_ABILITIES_PER_UNIT + slot</c>.
        /// Copied from <see cref="Core.Definitions.UnitDefinition.AbilityIndices"/> by <see cref="ApplyUnitDefinition"/>.
        /// Spawn-constant authored data → peer-identical, NOT folded into <see cref="SimChecksum"/> (like
        /// <see cref="MeshType"/>/<see cref="CategoryOf"/>).
        /// </summary>
        public readonly int[] AbilityId;
        /// <summary>
        /// Remaining cooldown in INTEGER ticks per ability slot (0 = ready), indexed
        /// <c>id * MAX_ABILITIES_PER_UNIT + slot</c>. The FIRST per-entity ability array that MUTATES mid-match (a
        /// cast starts it; <see cref="ProjectChimera.Effects.AbilityCastSystem"/> ticks it down each frame) →
        /// FOLDED into <see cref="SimChecksum"/> (v7, count-driven by <see cref="AbilityCount"/>).
        /// </summary>
        public readonly int[] AbilityCooldownTicks;
        /// <summary>
        /// Number of populated ability slots per entity (0 = none). Spawn-constant/peer-identical, so NOT folded as
        /// data — but it IS mixed into the v7 fold as the cross-platform-safe count-driven loop bound (exactly like
        /// <see cref="PatrolCount"/> bounds the patrol-waypoint fold).
        /// </summary>
        public readonly byte[] AbilityCount;
        /// <summary>
        /// Transient cast intent: the slot a queued cast targets (<see cref="NO_PENDING_CAST"/> = none). Written by
        /// <see cref="Multiplayer.OrderApplier"/> at input time (pre-tick), consumed + cleared by
        /// <see cref="ProjectChimera.Effects.AbilityCastSystem"/> the SAME tick — so it is ALWAYS
        /// <see cref="NO_PENDING_CAST"/> at the checksum boundary → NOT folded (transient, like <see cref="PrevPosition"/>).
        /// </summary>
        public readonly byte[] PendingCastSlot;
        /// <summary>
        /// Transient cast intent: the target entity id a queued cast acts on (−1 = none/self). Same transient
        /// treatment as <see cref="PendingCastSlot"/> (consumed + cleared before the checksum) → NOT folded.
        /// </summary>
        public readonly int[] PendingCastTarget;

        // --- Passive ability registration (Story 2.6, FR-9) — per-entity AbilityRegistry indices partitioned by
        //     activation; each is one index or −1 (none). Spawn-constant authored data → NOT folded (the AbilityId
        //     posture); written ONLY via ApplyUnitDefinition (A2). Read by the passive drivers: the aura pass
        //     (AbilityCastSystem) reads AuraAbilityIndex, the on-hit rider (CombatSystem) reads OnHitAbilityIndex,
        //     the spawn-install seam reads SelfPassiveAbilityIndex.
        /// <summary>Registry index of this unit's while-alive AURA passive (−1 = none). NOT folded (authored).</summary>
        public readonly int[] AuraAbilityIndex;
        /// <summary>Registry index of this unit's ON-HIT rider passive (−1 = none). NOT folded (authored).</summary>
        public readonly int[] OnHitAbilityIndex;
        /// <summary>Registry index of this unit's WHILE-ALIVE self-passive installed at spawn (−1 = none). NOT folded (authored).</summary>
        public readonly int[] SelfPassiveAbilityIndex;

        // --- Hero link (Story 3.2, D-8 / AR-12) ---
        /// <summary>
        /// Per-entity link to this entity's persistent <see cref="HeroStore"/> row — a GENERATION-STAMPED packed
        /// handle (<see cref="HeroStore.PackRef"/>), or <see cref="HERO_NONE"/> (−1) for a non-hero. Established when
        /// a hero unit spawns and a HeroStore row is minted (the load path is Story 3.9); resolved back to a live slot
        /// via <see cref="HeroStore.TryResolveRef"/>, so a reference that outlived a recycle resolves FALSE instead of
        /// ABA-retargeting a DIFFERENT hero — entity ids have no generation counter of their own, so a bare slot would
        /// be ABA-unsafe across recycle (the exact hazard 2.13's PackRef solved). RUNTIME state, NOT def-derived:
        /// defaulted in <see cref="Create"/> (the mandatory recycle-trap reset), set by the spawn/mint path — it does
        /// NOT go through <see cref="ApplyUnitDefinition"/> (the PatrolWaypoints/OrderQueue posture, NOT the A2
        /// def-mapper posture). NOT folded into <see cref="SimChecksum"/> in Story 3.2 (the store is dormant — D-1);
        /// its SOLE recycle coverage is the RecycledSlot_CarriesNoPriorHeroLink guard test.
        /// </summary>
        public readonly int[] HeroIndex;

        // --- Gatherer data (workers only; Inactive for all other units) ---
        public readonly GatherState[] GatherState;
        public readonly int[]         GatherTarget;   // ResourceNodeStore index (-1 = none)
        public readonly Fixed[]       CarryAmount;    // Current amount being carried
        // Story 4.7 (follow-up review patch): which resource the current carry is (Ore/Crystal). Snapshotted at
        // gather time and dispatched at deposit, independent of GatherTarget — BuildingSystem clears GatherTarget
        // when a Build command interrupts a returning worker, so resolving the deposited kind from GatherTarget
        // would mis-credit a Crystal load as Ore. Transient carry state (paired with CarryAmount); NOT folded into
        // SimChecksum and NOT snapshot residue, exactly like CarryAmount.
        public readonly ResourceKind[] CarryResourceType;
        public readonly Fixed[]       CarryCapacity;  // Max carry per trip
        /// <summary>
        /// DW-80 — CONSECUTIVE whole simulation ticks this worker has sat in <see cref="Core.GatherState.Gathering"/>
        /// at a Streaming node whose <c>requires_structure</c> gate is CLOSED. Reset to 0 the moment the gate reopens
        /// (so the streak is genuinely consecutive), on every fresh node assignment, and on the re-idle itself; once it
        /// reaches <c>GatheringSystem.STREAMING_GATE_GRACE_TICKS</c> the worker hands its gather slot back and re-idles
        /// to seek a different eligible node instead of producing zero forever (the recorded 2026-07-30 decision).
        ///
        /// <para>Whole ticks, never dt-accumulated and never wall-clock (the <c>IncomeTicksElapsed</c> discipline), so
        /// it is cross-platform-identical. NOT folded into <see cref="SimChecksum"/> and NOT persisted by
        /// <c>SaveGameState</c> — deliberately the exact posture of every other field of this worker state machine
        /// (<see cref="GatherState"/>/<see cref="GatherTarget"/>/<see cref="CarryAmount"/> are all unfolded; only the
        /// node-side <c>AssignedGatherers</c> is). A resumed save therefore restarts the grace window, which is
        /// behaviourally indistinguishable from a fresh boot re-seeking the same node. RUNTIME state, NOT def-derived:
        /// defaulted in <see cref="Create"/> (the mandatory recycle-trap reset), and NOT snapshot residue (the
        /// <see cref="CarryAmount"/> posture — a delete→undo restarts the window).</para>
        /// </summary>
        public readonly int[]         GateClosedTicks;

        /// <summary>
        /// DW-532 — the <see cref="Core.GatherState.MovingToResource"/> leg's STALL bookkeeping, in two ranges:
        /// <list type="bullet">
        ///   <item><c>&gt;= 0</c> — CONSECUTIVE whole ticks this worker has been unable to advance toward its reserved
        ///         node at all (the <see cref="Pathability"/> grid hard-stops its whole step, both wall-slide axes
        ///         included). Reset to 0 the instant it makes ground, on every fresh node assignment, on arrival, and on
        ///         every <c>GatheringSystem.ReleaseGatherSlot</c>. While in this range the worker HOLDS one of its
        ///         node's <c>ResourceNodeStore.AssignedGatherers</c> slots.</item>
        ///   <item><c>&lt; 0</c> (<c>GatheringSystem.SLOT_YIELDED</c>) — the streak reached
        ///         <c>GatheringSystem.WALK_STALL_GRACE_TICKS</c> and the worker HANDED THE RESERVATION BACK while
        ///         staying en route. A stranded worker (spawned inside a blocked region the validator only warns about,
        ///         then confined by DW-148) therefore stops consuming a node's capacity forever; it re-claims a slot
        ///         only if it actually arrives, which is why it keeps walking rather than re-idling into a churn loop.</item>
        /// </list>
        ///
        /// <para>Whole ticks, never dt-accumulated and never wall-clock (the <c>IncomeTicksElapsed</c> discipline), and
        /// the stall test itself is the shared pure-<see cref="Fixed"/> <c>Navigation.CheckedStep.Resolve</c>, so it is
        /// cross-platform-identical. NOT folded into <see cref="SimChecksum"/> and NOT persisted by
        /// <c>SaveGameState</c> — the exact <see cref="GateClosedTicks"/> posture (only the node-side
        /// <c>AssignedGatherers</c> is folded). RUNTIME state, NOT def-derived: defaulted in <see cref="Create"/> (the
        /// mandatory recycle-trap reset), and NOT snapshot residue (a delete→undo restarts the window).</para>
        ///
        /// <para>The one KNOWN residual of not persisting it: a save taken while a worker is stranded records the node
        /// counter WITHOUT that worker (it had yielded), but the restore re-applies the world first, so the worker comes
        /// back at 0 — "holding" — and re-yields a second later, decrementing a slot it never took. Floor-guarded, so
        /// the counter cannot go negative; the effect is bounded at one extra gatherer admitted to that node, versus the
        /// permanent capacity LOSS on every map that not bounding the leg at all produced. Closing it properly means a
        /// save lane for this array (the DW-581 family), deliberately out of scope here.</para>
        /// </summary>
        public readonly int[]         GatherWalkStallTicks;

        /// <summary>
        /// DW-634 — TRUE while this worker still owes its production building's RALLY POINT a first leg, i.e. it was
        /// trained from a building carrying a rally point and has not yet reached it. <c>GatheringSystem.TickIdle</c>
        /// skips (does NOT re-target) a worker whose flag is set, so an explicit player rally beats the automatic
        /// idle-gather sweep; the flag is a ONE-SHOT — the sweep itself clears it the moment the worker arrives at its
        /// <see cref="CommandGoal"/> (or the rally <see cref="UnitCommand.Move"/> is superseded by any other command
        /// state), after which the EXISTING nearest-node logic runs normally and picks the node beside the rally point.
        ///
        /// <para>Arrival is tested against <see cref="CommandGoal"/> with <see cref="ArrivalTuning.GoalArriveRadiusSqr"/>
        /// — the PURE-SIM goal-arrival signal <c>OrderQueueSystem</c> already uses — never <see cref="EntityFlags.Moving"/>
        /// or the presentation <c>Move→Stop</c> flip, both of which are presentation-written and would diverge
        /// headless-golden vs live-client.</para>
        ///
        /// <para>Written ONLY by <c>BuildingSystem.SpawnTrainedUnit</c>'s rally branch (and only for a WORKER — a
        /// combat unit is never ticked by <c>GatheringSystem</c>, so it has no reader and gets no flag) and cleared
        /// ONLY by <c>GatheringSystem.TickIdle</c>. NOT folded into <see cref="SimChecksum"/> and NOT persisted by
        /// <c>SaveGameState</c> — deliberately the exact posture of every other field of this worker state machine
        /// (<see cref="GatherState"/>/<see cref="GatherTarget"/>/<see cref="CarryAmount"/>/<see cref="GateClosedTicks"/>
        /// are all unfolded; only the node-side <c>AssignedGatherers</c> is). A resumed save therefore forgets an
        /// in-flight rally leg and auto-gathers, which is what a pre-DW-634 save already did. RUNTIME state, NOT
        /// def-derived: defaulted in <see cref="Create"/> (the mandatory recycle-trap reset), and NOT snapshot residue
        /// (the <see cref="CarryAmount"/>/<see cref="GateClosedTicks"/> posture — a delete→undo drops the pending leg).</para>
        /// </summary>
        public readonly bool[]        RallyMovePending;

        // --- Worker construction ---
        /// <summary>
        /// Building ID the worker is walking to construct.
        /// Valid only when CommandState == Build; -1 otherwise.
        /// </summary>
        public readonly int[] BuildTarget;

        // --- Management ---
        /// <summary>
        /// Generic per-id lifecycle callback fired synchronously inside <see cref="Destroy"/>, before the id returns
        /// to the free list. The Story 2.2b <c>ModifierStore</c> subscribes its <c>ClearEntity</c> here so an entity's
        /// active modifiers + external stat-bonus accumulators are reverted on death/recycle (the SoA-recycle trap an
        /// external store cannot close via <see cref="Create"/> alone). Deliberately an <c>Action&lt;int&gt;</c>, NOT a
        /// typed store ref: <c>EntityWorld</c> (Core) stays free of any dependency on the modifier store — clean
        /// layering (Effects depends on Core, never the reverse). Fires in the same deterministic order
        /// <see cref="Destroy"/> is called on every peer; zero-alloc once bound. (DW-213: nullable-ANNOTATED now that
        /// this file is inside the <c>#nullable enable</c> context — a delegate field is null until bound, and the
        /// <c>?.Invoke</c> call site guards it.)
        /// </summary>
        public Action<int>? OnDestroy;

        /// <summary>
        /// Per-id lifecycle callback fired synchronously at the END of <see cref="ApplyUnitDefinition"/> — AFTER every
        /// def-derived field (including the passive-registration indices) is written. The Story 2.6 passive
        /// spawn-installer (subscribed in <see cref="Core.Sim.SimulationHost"/>) installs a unit's <c>while_alive</c>
        /// self-passive exactly once per def-based spawn, through the SINGLE def→SoA mapper — so it can never be
        /// forgotten in a spawn path (the A2 rule, symmetric with <see cref="OnDestroy"/>). A bare
        /// <c>Action&lt;int&gt;</c> so <c>EntityWorld</c> (Core) keeps zero dependency on the Effects layer. NOTE:
        /// Story 3.17 routes the editor <see cref="RestoreUnit"/> path THROUGH <see cref="ApplyUnitDefinition"/> for a
        /// def-based unit, so a restored unit's while-alive self-passive IS re-installed via this seam (the prior
        /// carve-off that skipped restore is closed). <see cref="Destroy"/> cleared the entity's modifiers on delete,
        /// so there is no double-install.
        /// </summary>
        public Action<int>? OnUnitDefinitionApplied;

        // ── Generation counter (DW-184 — the BuildingStore/HeroStore Story 2.13 D-3 pattern, applied to entities) ──
        // Per-slot recycle generation, bumped each time Create() reuses a freed slot. Entity ids previously had NO
        // generation counter, so any cross-tick holder of a bare id was ABA-unsafe: Destroy() frees the slot to the
        // LIFO free-list immediately and a same-tick Create() pops it, so a raw-id + faction check can mistake the NEW
        // occupant for the old one (the WinConditionSystem assassination-target masking DW-184 records). A cross-tick
        // reference that must survive recycles is PACKED as (Generation[id] << REF_SLOT_BITS) | id via PackRef and
        // validated on deref by TryResolveRef, so a stale ref reverts CLEANLY instead of ABA-retargeting.
        //
        // UNFOLDED — never a SimChecksum input (the exact BuildingStore.Generation posture): the recycle sequence is
        // itself deterministic (driven by the deterministic Create/Destroy order), so peers agree without folding, and
        // the folded Flags/alive state already covers divergence transitively. Golden-neutral: generation starts at 0,
        // so PackRef(id) == id for every never-recycled entity.
        //
        // PERSISTED by SaveGameState as an entity lane (DW-581), joining the already-persisted BuildingStore /
        // HeroStore / ItemStore generations. The generation IS the ABA-safety of a packed ref, so a save boundary that
        // dropped it resumed the world with every slot back at generation 0 — a stale packed ref then resolved against
        // whatever now occupies the slot, the exact retarget PackRef exists to prevent. That was harmless only by
        // construction (the one cross-tick holder of a packed ENTITY ref was WinConditionSystem's apply-time
        // _leaderRef, which the load path re-packs at generation 0 during its fresh re-apply — matching a live leader,
        // whose slot is necessarily generation 0 — and a resolved leader's death latches its folded WinStateStore
        // verdict on the same tick it dies). Persisting the lane retires that argument instead of resting on it, so a
        // future story MAY store a packed entity ref in persisted or folded state. NOTE that this array is the one
        // per-slot field Create() does NOT re-default on the fresh-append path (it relies on "generation 0 past the
        // high-water mark"), so the restore overlay must be TOTAL — see SaveGameState.RestoreEntities.
        public readonly int[] Generation;

        /// <summary>Bit width of the id (slot) half of a packed entity reference — <see cref="MAX_ENTITIES"/> (4096)
        /// is exactly <c>1 &lt;&lt; 12</c>, so every id fits in the low 12 bits and the generation occupies the rest.</summary>
        public const int REF_SLOT_BITS = 12;

        /// <summary>DW-581 — the largest <see cref="Generation"/> value <see cref="PackRef"/> can encode and
        /// <see cref="TryResolveRef"/> read back: anything larger shifts into the sign bit, so a live entity's own
        /// freshly-packed ref would stop resolving. Unreachable in the sim (a slot would have to be recycled 2^19
        /// times in one match), but it is the fail-closed bound the persisted generation lane is validated against.</summary>
        public const int MAX_PACKABLE_GENERATION = int.MaxValue >> REF_SLOT_BITS;
        private const int REF_SLOT_MASK = (1 << REF_SLOT_BITS) - 1;

        private int _nextId;
        private readonly int[] _freeList;
        private int _freeCount;

        /// <summary>Number of currently alive entities.</summary>
        public int AliveCount { get; private set; }

        /// <summary>Highest entity ID that has ever been allocated + 1. Use for iteration bounds.</summary>
        public int HighWaterMark => _nextId;

        public EntityWorld()
        {
            Flags = new EntityFlags[MAX_ENTITIES];
            Position = new FixedVec3[MAX_ENTITIES];
            PrevPosition = new FixedVec3[MAX_ENTITIES];
            Velocity = new FixedVec3[MAX_ENTITIES];
            BaseMoveSpeed = new Fixed[MAX_ENTITIES];
            EffectiveMoveSpeed = new Fixed[MAX_ENTITIES];
            Health = new Fixed[MAX_ENTITIES];
            BaseMaxHealth = new Fixed[MAX_ENTITIES];
            EffectiveMaxHealth = new Fixed[MAX_ENTITIES];
            FactionOf = new Faction[MAX_ENTITIES];
            MoveTarget = new FixedVec3[MAX_ENTITIES];
            AttackTarget = new int[MAX_ENTITIES];
            AttackCooldown = new Fixed[MAX_ENTITIES];
            AttackRange = new Fixed[MAX_ENTITIES];
            BaseAttackDamage = new Fixed[MAX_ENTITIES];
            EffectiveAttackDamage = new Fixed[MAX_ENTITIES];
            BaseArmor      = new Fixed[MAX_ENTITIES];   // Story 2.6 (authored — NOT folded)
            EffectiveArmor = new Fixed[MAX_ENTITIES];   // Story 2.6 (folded v8)
            AttackSpeed = new Fixed[MAX_ENTITIES];
            Energy         = new Fixed[MAX_ENTITIES];
            MaxEnergy      = new Fixed[MAX_ENTITIES];
            StatusFlagsOf  = new StatusFlags[MAX_ENTITIES];     // Story 2.2b (folded v6); modifier-imposed status
            DamageTypeOf = new DamageType[MAX_ENTITIES];
            ArmorTypeOf = new ArmorType[MAX_ENTITIES];

            VisionRange    = new Fixed[MAX_ENTITIES];
            Elevation      = new Fixed[MAX_ENTITIES];                    // Story 6.3 (folded v15; Create-sampled from the injected grid, else Zero)
            SplashRadius   = new Fixed[MAX_ENTITIES];
            Delivery       = new AttackDelivery[MAX_ENTITIES];          // Story 3.12 (folded v10; Hitscan == 0, no Array.Fill needed)
            ProjectileSpeed = new Fixed[MAX_ENTITIES];                  // Story 3.12 (folded v10; Create-defaulted to the 18 fallback)
            XpBounty       = new Fixed[MAX_ENTITIES];                    // Story 3.13 (folded v11; Create-defaulted to Zero)
            KillerOf        = new int[MAX_ENTITIES];                     // Story 7.5 (NOT folded — derived attribution; sentinel −1 via Array.Fill below)
            KillerFactionOf = new int[MAX_ENTITIES];                     // Story 7.5 (NOT folded; sentinel −1 via Array.Fill below)
            DeathLog        = new DeathLog();                            // DW-367 (NOT folded — per-tick transient death log, wiped by the director)
            CollisionRadius      = new Fixed[MAX_ENTITIES];              // Story 1.13 (folded v5)
            SeparationPriorityOf = new SeparationPriority[MAX_ENTITIES]; // Story 1.13 (folded v5)
            CategoryOf           = new UnitCategory[MAX_ENTITIES];       // Story 1.13 (NOT folded — presentation-read)
            AttackDomainOf       = new AttackDomain[MAX_ENTITIES];       // Story 2.9a (NOT folded — authored combat-read)
            TagsOf               = new UnitTag[MAX_ENTITIES];            // Story 2.11 (NOT folded — authored effect-read; UnitTag.None == 0, no Array.Fill needed)
            SupplyCost     = new byte[MAX_ENTITIES];
            MeshType       = new byte[MAX_ENTITIES];
            FeedbackProfile = new CombatFeedbackProfile[MAX_ENTITIES];   // Story 2.7 (presentation-read — NOT folded; first ref-typed SoA)
            SourceDefinition = new UnitDefinition[MAX_ENTITIES];          // Story 3.17 (restore-fidelity def ref — NOT folded; the FeedbackProfile precedent)
            CommandState   = new UnitCommand[MAX_ENTITIES];
            CommandGoal    = new FixedVec3[MAX_ENTITIES];
            CommandTarget   = new int[MAX_ENTITIES];
            PatrolWaypoints = new FixedVec3[MAX_ENTITIES * MAX_PATROL_WAYPOINTS];
            PatrolCount     = new byte[MAX_ENTITIES];
            PatrolIndex     = new byte[MAX_ENTITIES];
            PatrolDir       = new sbyte[MAX_ENTITIES];
            OrderQueueCmd     = new byte[MAX_ENTITIES * MAX_ORDER_QUEUE];  // Story 2.12 (folded v9; 0 = empty, no Array.Fill needed)
            OrderQueueTargetX = new int[MAX_ENTITIES * MAX_ORDER_QUEUE];   // Story 2.12 (folded v9)
            OrderQueueTargetZ = new int[MAX_ENTITIES * MAX_ORDER_QUEUE];   // Story 2.12 (folded v9)
            OrderQueueCount   = new byte[MAX_ENTITIES];                    // Story 2.12 (folded v9; count-driven loop bound)
            ActiveOrderCmd    = new byte[MAX_ENTITIES];                    // Story 2.12 R1 (sim-authoritative active order; NOT folded — the CommandState posture)
            AbilityId            = new int[MAX_ENTITIES * MAX_ABILITIES_PER_UNIT];  // Story 2.4a (authored — NOT folded)
            AbilityCooldownTicks = new int[MAX_ENTITIES * MAX_ABILITIES_PER_UNIT];  // Story 2.4a (folded v7)
            AbilityCount         = new byte[MAX_ENTITIES];                          // Story 2.4a (v7 fold loop bound)
            PendingCastSlot      = new byte[MAX_ENTITIES];                          // Story 2.4a (transient — NOT folded)
            PendingCastTarget    = new int[MAX_ENTITIES];                           // Story 2.4a (transient — NOT folded)
            AuraAbilityIndex        = new int[MAX_ENTITIES];                        // Story 2.6 (authored — NOT folded)
            OnHitAbilityIndex       = new int[MAX_ENTITIES];                        // Story 2.6 (authored — NOT folded)
            SelfPassiveAbilityIndex = new int[MAX_ENTITIES];                        // Story 2.6 (authored — NOT folded)
            HeroIndex               = new int[MAX_ENTITIES];                        // Story 3.2 (packed HeroStore handle; sentinel −1 via Array.Fill below; NOT folded)
            GatherState    = new GatherState[MAX_ENTITIES];
            GatherTarget   = new int[MAX_ENTITIES];
            CarryAmount    = new Fixed[MAX_ENTITIES];
            CarryResourceType = new ResourceKind[MAX_ENTITIES]; // Story 4.7 — defaults to Ore (0)
            CarryCapacity  = new Fixed[MAX_ENTITIES];
            GateClosedTicks = new int[MAX_ENTITIES];            // DW-80 — closed-gate streak (NOT folded; 0 == fresh, no Array.Fill needed)
            GatherWalkStallTicks = new int[MAX_ENTITIES];       // DW-532 — MovingToResource stall streak / yielded sentinel (NOT folded; 0 == fresh)
            RallyMovePending = new bool[MAX_ENTITIES];          // DW-634 — outstanding rally first leg (NOT folded; false == fresh, no Array.Fill needed)
            BuildTarget    = new int[MAX_ENTITIES];

            Generation = new int[MAX_ENTITIES]; // DW-184 — per-slot recycle generation (UNFOLDED; 0 == never recycled)
            _freeList = new int[MAX_ENTITIES];
            _freeCount = 0;
            _nextId = 0;

            // Single shared deterministic RNG (AR-13). Reseeded at match start / replay restore.
            Rng = new SimRng(DEFAULT_RNG_SEED);

            // Initialize sentinels
            Array.Fill(AttackTarget,  -1);
            Array.Fill(CommandTarget, -1);
            Array.Fill(GatherTarget,  -1);
            Array.Fill(BuildTarget,   -1);
            Array.Fill(AbilityId,         -1);              // Story 2.4a: -1 = empty ability slot
            Array.Fill(PendingCastSlot,   NO_PENDING_CAST); // Story 2.4a: 255 = no cast queued
            Array.Fill(PendingCastTarget, -1);
            Array.Fill(HeroIndex, HERO_NONE);               // Story 3.2: −1 = "not a hero" (default int 0 would falsely alias HeroStore slot 0)
            Array.Fill(KillerOf,        -1);                // Story 7.5: −1 = no killer (default 0 would falsely alias entity 0)
            Array.Fill(KillerFactionOf, -1);                // Story 7.5: −1 = no killer faction (default 0 would falsely alias slot 0 / Player1)
        }

        /// <summary>
        /// Allocate a new entity. Returns the entity ID, or -1 if full.
        /// </summary>
        public int Create(FixedVec3 position, Faction faction, Fixed health, Fixed speed)
        {
            int id;
            if (_freeCount > 0)
            {
                id = _freeList[--_freeCount];
                Generation[id]++; // DW-184: bump so stale packed refs to the prior occupant fail TryResolveRef
            }
            else if (_nextId < MAX_ENTITIES)
            {
                id = _nextId++;   // fresh slot — Generation stays 0 (never recycled)
            }
            else
            {
                return -1; // Full
            }

            Flags[id] = EntityFlags.Alive;
            Position[id] = position;
            PrevPosition[id] = position;
            Velocity[id] = FixedVec3.Zero;
            BaseMoveSpeed[id] = speed;
            EffectiveMoveSpeed[id] = speed;
            Health[id] = health;
            BaseMaxHealth[id] = health;
            EffectiveMaxHealth[id] = health;
            FactionOf[id] = faction;
            MoveTarget[id] = position;
            AttackTarget[id]  = -1;
            AttackCooldown[id] = Fixed.Zero;
            AttackRange[id]   = Fixed.Zero;
            BaseAttackDamage[id]       = Fixed.Zero;
            EffectiveAttackDamage[id]  = Fixed.Zero;
            BaseArmor[id]              = Fixed.Zero;   // Story 2.6: ApplyUnitDefinition sets from def.Armor for def units
            EffectiveArmor[id]         = Fixed.Zero;
            AttackSpeed[id]   = Fixed.Zero;
            // Story 2.2a substrate / 2.4a: ability-resource pool defaults to empty here; ApplyUnitDefinition sets it
            // from UnitDefinition.MaxEnergy (start full) for ability-bearing units. A unit with no def stays at 0.
            Energy[id]        = Fixed.Zero;
            MaxEnergy[id]     = Fixed.Zero;
            // Story 2.2b: a (re)allocated slot carries NO modifier-imposed status — the ModifierStore ORs flags in on
            // apply and recomputes the union on remove; defaulting here closes the SoA-recycle trap for this field.
            StatusFlagsOf[id] = StatusFlags.None;
            DamageTypeOf[id]  = DamageType.Normal;
            ArmorTypeOf[id]   = ArmorType.Unarmored;
            VisionRange[id]   = Fixed.FromFloat(8f);
            // Story 6.3: sample terrain elevation for THIS spawn from the injected grid (deterministic clamped-cell
            // lookup). Done in Create — which every spawn path funnels through and which has the spawn `position` — so
            // scenario load, SpawnUnitAt (trigger/hero respawn), production, and editor placement all get uniform,
            // correct elevation with no per-callsite edit. A recycled slot MUST be re-sampled (never inherit the prior
            // occupant's elevation — the SoA-recycle trap); a null grid (flat/legacy map) yields Fixed.Zero.
            Elevation[id]     = _elevationGrid != null ? _elevationGrid.Sample(position.X, position.Z) : Fixed.Zero;
            SplashRadius[id]  = Fixed.Zero;
            // Story 3.12: default attack-delivery fields on (re)allocation. A recycled/non-def slot must never carry the
            // prior occupant's delivery/speed (the SoA-recycle trap). Hitscan == instant (the safe default for a unit
            // with no def); ProjectileSpeed defaults to the documented 18 fallback (== the old global). ApplyUnitDefinition
            // overwrites both from the def (Delivery via ResolveDelivery, speed via the float→Fixed boundary).
            Delivery[id]        = AttackDelivery.Hitscan;
            ProjectileSpeed[id] = ProjectileSystem.PROJECTILE_SPEED;
            // Story 3.13: a recycled/non-def slot awards no XP bounty on death. ApplyUnitDefinition overwrites it from
            // the def (authored xp_bounty else ore+crystal cost, quantized at the single boundary).
            XpBounty[id]        = Fixed.Zero;
            // Story 7.5: a (re)allocated slot has no killer — a recycled slot must NEVER carry the prior occupant's
            // attribution (the SoA-recycle trap: a spawned unit would credit the previous corpse's killer to its own
            // future death payload). Written only by DamageResolver.KillEntity.
            KillerOf[id]        = -1;
            KillerFactionOf[id] = -1;
            // Story 1.13: default separation/formation fields on (re)allocation. A recycled slot must never carry
            // the previous unit's radius/priority/category (the classic SoA bug — cf. the 1.12 zombie-route fix).
            // SpawnUnit overwrites these from the def; Create must default them for any spawn site that forgets.
            CollisionRadius[id]      = DEFAULT_COLLISION_RADIUS;
            SeparationPriorityOf[id] = SeparationPriority.Normal;
            CategoryOf[id]           = UnitCategory.Melee;
            AttackDomainOf[id]       = AttackDomain.All;   // Story 2.9a: recycled/non-def slots attack every domain (as before).
            TagsOf[id]               = UnitTag.None;       // Story 2.11: MANDATORY recycle-reset — a reused slot must never inherit the prior occupant's tag (SoA-recycle trap; the ONLY teeth are RecycledSlot_CarriesNoPriorTag, since default(UnitTag)==None makes this line look redundant).
            SupplyCost[id]    = 0;
            MeshType[id]      = 0;
            FeedbackProfile[id] = null;  // Story 2.7: ref-typed SoA — clear on (re)alloc so a recycled slot never inherits a prior occupant's profile.
            SourceDefinition[id] = null; // Story 3.17: ref-typed SoA — clear on (re)alloc so a recycled slot never carries a prior occupant's def (restore would re-derive stale authored state). ApplyUnitDefinition sets it for def-based spawns.
            CommandState[id]  = UnitCommand.Idle;
            CommandGoal[id]   = position;
            // Story 1.12: reset persistent command target + patrol route on (re)allocation. Skipping this is
            // the classic SoA bug — a recycled slot would carry the previous unit's forced target / route and
            // drive nondeterministic ghost behavior. PatrolWaypoints needs no reset: PatrolCount=0 makes its
            // slots unread (and unhashed) until a Patrol command writes them.
            CommandTarget[id] = -1;
            PatrolCount[id]   = 0;
            PatrolIndex[id]   = 0;
            PatrolDir[id]     = 1;
            // Story 2.12: MANDATORY recycle-reset of the shift-queue ring. A recycled slot must NEVER inherit the prior
            // occupant's queued orders (the 1.12/1.13/2.6 SoA-recycle defect class). Count-only reset suffices — slots
            // past OrderQueueCount are unread and unhashed (the PatrolCount discipline), so the ring buffer needs no
            // per-slot clear. The ONLY teeth on this line are RecycledSlot_CarriesNoPriorOrderQueue (default(byte)==0
            // makes it look redundant, but a reused slot with a stale count would drive ghost orders + a desync).
            OrderQueueCount[id] = 0;
            ActiveOrderCmd[id]  = (byte)UnitCommand.Idle; // Story 2.12 R1: a recycled slot starts "ready" (Idle), never inheriting a prior active order (SoA-recycle rule)
            // Story 2.4a: reset the per-entity ability slots + cooldowns + transient cast intent on (re)allocation —
            // a recycled slot must NEVER carry the prior occupant's abilities/cooldowns (the SoA-recycle trap the A2
            // guard catches). ApplyUnitDefinition overwrites AbilityId/AbilityCount/MaxEnergy/Energy for def-based units.
            AbilityCount[id]      = 0;
            PendingCastSlot[id]   = NO_PENDING_CAST;
            PendingCastTarget[id] = -1;
            // Story 2.6: a recycled slot must NEVER carry the prior occupant's passive registration (the SoA-recycle
            // trap). ApplyUnitDefinition re-partitions these from the def for def-based units; default −1 (none) here.
            AuraAbilityIndex[id]        = -1;
            OnHitAbilityIndex[id]       = -1;
            SelfPassiveAbilityIndex[id] = -1;
            // Story 3.2 (D-8): a recycled slot must NEVER inherit the prior occupant's HeroStore link — a stale
            // packed handle would ABA-alias a hero row. HERO_NONE (−1) = not a hero. Set by the spawn/mint path for
            // hero units (Story 3.9), NOT by ApplyUnitDefinition (runtime state, not def data). The load-bearing
            // recycle-trap line: its ONLY teeth are RecycledSlot_CarriesNoPriorHeroLink (unfolded → the guard IS the coverage).
            HeroIndex[id] = HERO_NONE;
            int abResetBase = id * MAX_ABILITIES_PER_UNIT;
            for (int s = 0; s < MAX_ABILITIES_PER_UNIT; s++)
            {
                AbilityId[abResetBase + s]            = -1;
                AbilityCooldownTicks[abResetBase + s] = 0;
            }
            GatherState[id]   = Core.GatherState.Inactive;
            GatherTarget[id]  = -1;
            CarryAmount[id]   = Fixed.Zero;
            CarryResourceType[id] = ResourceKind.Ore; // Story 4.7 — recycled slot must not inherit the prior occupant's carry kind
            CarryCapacity[id] = Fixed.Zero;
            // DW-80: MANDATORY recycle-reset of the closed-gate streak. A recycled slot must NEVER inherit the prior
            // occupant's partial grace window (the 1.12/1.13/2.6 SoA-recycle defect class) — it would re-idle a brand-new
            // worker early, or (worse) never, depending on the corpse. Unfolded, so this line's ONLY teeth are
            // RecycledSlot_CarriesNoPriorGateClosedStreak.
            GateClosedTicks[id] = 0;
            // DW-532: MANDATORY recycle-reset of the walk-stall streak / yielded sentinel. A recycled slot must NEVER
            // inherit the prior occupant's partial stall window — and, far worse, must never inherit its SLOT_YIELDED
            // sentinel, which would make the brand-new worker's very first ReleaseGatherSlot skip a decrement it really
            // does owe (the 1.12/1.13/2.6 SoA-recycle defect class, here leaking the counter DW-207 exists to protect).
            // Unfolded, so this line's ONLY teeth are RecycledSlot_CarriesNoPriorWalkStallState.
            GatherWalkStallTicks[id] = 0;
            // DW-634: MANDATORY recycle-reset of the outstanding-rally-leg flag. A recycled slot must NEVER inherit the
            // prior occupant's pending rally (the 1.12/1.13/2.6 SoA-recycle defect class) — a brand-new worker spawned
            // WITHOUT a rally would inherit a corpse's true flag and then refuse to gather until it happened to stand
            // within the goal-arrive radius of whatever CommandGoal it was given. Unfolded, so this line's ONLY teeth
            // are RecycledSlot_CarriesNoPriorRallyMovePending.
            RallyMovePending[id] = false;
            BuildTarget[id]   = -1;

            AliveCount++;
            return id;
        }

        /// <summary>
        /// Story 6.3: inject the finalized-terrain <see cref="ElevationGrid"/> (or null for a flat/legacy map) BEFORE
        /// any spawn, so <see cref="Create"/> samples it. Godot builds the grid at load time from the restored Terrain3D
        /// heightmap (one <see cref="Fixed.FromFloat"/> per cell) and calls this via <c>ScenarioApplier</c>. Never
        /// reassigned per-tick — a load-time seam only. Null ⇒ every spawn's <see cref="Elevation"/> is <see cref="Fixed.Zero"/>.
        /// </summary>
        public void SetElevationGrid(ElevationGrid? grid) => _elevationGrid = grid;

        /// <summary>
        /// DW-153 — deterministic READ of the injected <see cref="ElevationGrid"/> terrain height at a world XZ (the
        /// same grid <see cref="Elevation"/> is stamped from at spawn). Returns <see cref="Fixed.Zero"/> when no grid
        /// is present (a flat/legacy map). Pure read — never mutates sim state, so it folds into no checksum. Used by
        /// the editor's marquee hit-test and 3D selection markers so they sit on Story-6.3 raised ground.
        /// </summary>
        public Fixed SampleElevation(Fixed x, Fixed z) => _elevationGrid != null ? _elevationGrid.Sample(x, z) : Fixed.Zero;

        /// <summary>
        /// Story 6.5: inject the load-time pathability grid (painted ∪ slope-derived blocked cells) BEFORE any
        /// spawn, so the fixed sim tick honors it. Godot decodes the painted bitset + derives slope cells and calls
        /// this via <c>ScenarioApplier</c>. Never reassigned per-tick — a load-time seam only. Null ⇒ blocking is a
        /// no-op (byte-identical to pre-feature). Godot-free type, so this setter keeps the sim Godot-free.
        /// </summary>
        public void SetPathabilityGrid(ProjectChimera.Navigation.PathabilityGrid? grid) => Pathability = grid;

        /// <summary>
        /// Story 6.3: the vision radius <see cref="ProjectChimera.Core.FogOfWarSystem"/> stamps for this unit — the base
        /// authored <see cref="VisionRange"/> plus, ONLY when <see cref="HeightAdvantageVision"/> is enabled, an
        /// elevation-derived <see cref="Fixed"/> bonus. Computed entirely in <see cref="Fixed"/> so it can merge BEFORE
        /// the existing per-tick <c>.ToFloat()</c> boundary in FogOfWarSystem — no new float on any sim path. Steps =
        /// <c>floor(Elevation / HEIGHT_STEP_WORLD_UNITS)</c>, clamped ≥ 0 (a sub-step or negative elevation earns no
        /// bonus). Toggle OFF ⇒ returns the base range unchanged, so the stamped fog Grid stays byte-identical to
        /// pre-feature.
        /// </summary>
        public Fixed EffectiveVisionRange(int id)
        {
            Fixed baseR = VisionRange[id];
            if (!HeightAdvantageVision) return baseR;                 // toggle OFF ⇒ byte-identical fog
            // Whole-step count = floor(Elevation / HEIGHT_STEP_WORLD_UNITS), clamped ≥ 0 (a sub-step or negative
            // elevation earns no bonus). ToInt() == Raw >> FRACTIONAL_BITS (a deterministic floor); dividing by the
            // NAMED step keeps the constant load-bearing (a future step != 1 reconfigures the math, not just the doc —
            // review pass 1, F4). Height vision is an ADVANTAGE: a GIGO negative bonus must never reduce a unit's
            // stamped radius below its base (nor drive a negative StampCircle radius), so the bonus term is clamped
            // ≥ 0 (review pass 1, EC1). A well-authored non-negative bonus never trips the clamp.
            int steps = Elevation[id].Raw > 0 ? Elevation[id].ToInt() / HEIGHT_STEP_WORLD_UNITS : 0;
            Fixed bonus = Fixed.FromInt(steps) * HeightVisionBonusPerStep;
            if (bonus.Raw < 0) bonus = Fixed.Zero;
            return baseR + bonus;
        }

        /// <summary>
        /// Copy a unit <see cref="UnitDefinition"/>'s per-entity fields onto an already-<see cref="Create"/>d slot.
        /// This is the SINGLE place definition→SoA field mapping lives, so every spawn path (scenario apply,
        /// building production, editor placement) shares one copy — a new per-unit field can never again be wired
        /// into one path and silently forgotten in the others (the Story 1.13 review found exactly that gap, where
        /// trained/placed units kept the <see cref="Create"/> defaults for the new separation/formation fields).
        /// Sets the combat stats, supply, and the Story 1.13 separation/formation fields (with the documented
        /// collision-radius clamp). Does NOT set <c>Health</c>/<c>Speed</c> (those are <see cref="Create"/> ctor
        /// args) nor <c>MeshType</c> (its faction-def index differs per caller), and does NOT touch worker gather
        /// state — callers own those. Allocation-free: value-type writes only, no LINQ/closures/boxing.
        /// </summary>
        public void ApplyUnitDefinition(int id, UnitDefinition def)
        {
            VisionRange[id]  = Fixed.FromFloat(def.VisionRange);
            AttackRange[id]  = Fixed.FromFloat(def.AttackRange);
            // Story 2.2a (A2): AttackDamage is the one modifier-affected stat sourced from the def. Write Base (the
            // authored, in-tick-immutable source) then mirror into Effective (== Base until a modifier applies in 2.2b).
            BaseAttackDamage[id]      = Fixed.FromFloat(def.AttackDamage);
            EffectiveAttackDamage[id] = BaseAttackDamage[id];
            // Story 2.6 (A2): armor mirrors the AttackDamage Base→Effective pattern. Base is authored (def.Armor,
            // float→Fixed at this single boundary like the other stats); Effective == Base until a modifier applies.
            BaseArmor[id]      = Fixed.FromFloat(def.Armor);
            EffectiveArmor[id] = BaseArmor[id];
            AttackSpeed[id]  = Fixed.FromFloat(def.AttackSpeed);
            DamageTypeOf[id] = def.ParsedDamageType;
            ArmorTypeOf[id]  = def.ParsedArmorType;
            SplashRadius[id] = Fixed.FromFloat(def.SplashRadius);
            SupplyCost[id]   = (byte)def.Supply;

            // Story 3.12 (A2 single mapper): the authorable attack delivery + per-unit projectile speed. Delivery is
            // resolved from the ALREADY-quantized AttackRange[id] above (so a null/unknown string infers the exact old
            // MELEE_THRESHOLD partition — byte-identical to legacy behaviour). ProjectileSpeed is the one float→Fixed
            // boundary here (default 18f == the old global). Both are FOLDED into SimChecksum (v10).
            Delivery[id]        = def.ResolveDelivery(AttackRange[id]);
            ProjectileSpeed[id] = Fixed.FromFloat(def.ProjectileSpeed);

            // Story 3.13 (A2 single mapper): the per-unit XP bounty this unit awards on death. ResolveXpBounty()
            // returns the authored xp_bounty else CostOre+CostCrystal; this is the single int→Fixed boundary for it
            // (never quantized inside a tick). FOLDED into SimChecksum (v11).
            XpBounty[id]        = Fixed.FromInt(def.ResolveXpBounty());

            // Story 1.13 (DG-2 / FR-54): per-unit separation/formation fields. CollisionRadius mirrors the
            // SplashRadius float→Fixed load conversion, then is clamped (see ClampCollisionRadius). SeparationPriority
            // is folded into SimChecksum (in-sim read); Category is presentation-read (formation planning), NOT folded.
            CollisionRadius[id]      = ClampCollisionRadius(def.CollisionRadius);
            SeparationPriorityOf[id] = def.ParsedSeparationPriority;
            CategoryOf[id]           = def.ParsedCategory;
            AttackDomainOf[id]       = def.ParsedAttackDomains;   // Story 2.9a (A2 single mapper; authored, NOT folded).
            TagsOf[id]               = def.ParsedTags;            // Story 2.11 (A2 single mapper; authored effect-read, NOT folded).

            // Story 2.7 (A2): the per-unit combat-feedback override — presentation-read, NOT folded (the
            // MeshType/CategoryOf posture). Reaches built armies automatically because the primary in-match spawn
            // (BuildingSystem.SpawnTrainedUnit) routes through this single mapper. Null def.CombatFeedback ⇒ defaults.
            FeedbackProfile[id]      = def.CombatFeedback;

            // Story 3.17 (A2): remember the def this entity was spawned from so the editor delete→undo restore path can
            // re-derive every def-derived authored field by routing it back through THIS mapper (never a hand-copy).
            // Non-folded reference SoA (the FeedbackProfile precedent); null-reset in Create for recycle safety.
            SourceDefinition[id]     = def;

            // Story 2.4a (A2): the FIRST per-entity ability state flows through this single mapper — never hand-copied
            // in a spawn path (the retro-A2 rule that closed the 1.12/1.13 spawn-path defect class). MaxEnergy is the
            // one float→Fixed boundary here (the CHM0005-allow-listed mapper site, like VisionRange/AttackRange above);
            // the unit starts FULL (Decision #5 — immediately castable, no regen in 2.4a). Ability slots copy from the
            // registry-resolved indices (UnitDefinition.ResolveAbilities ran once at scenario link). Cooldown slots
            // stay 0 from Create (ready). Excess ids beyond the cap were already dropped by ResolveAbilities.
            MaxEnergy[id] = Fixed.FromFloat(def.MaxEnergy);
            Energy[id]    = MaxEnergy[id];
            byte abN = (byte)System.Math.Min(def.AbilityIndices.Length, MAX_ABILITIES_PER_UNIT);
            AbilityCount[id] = abN;
            int abApplyBase = id * MAX_ABILITIES_PER_UNIT;
            for (int s = 0; s < abN; s++)
                AbilityId[abApplyBase + s] = def.AbilityIndices[s];

            // Story 2.6 (A2): the per-entity passive registration, partitioned by activation at scenario link
            // (UnitDefinition.ResolveAbilities). Authored/peer-identical → NOT folded (the AbilityId posture).
            AuraAbilityIndex[id]        = def.AuraAbilityIndex;
            OnHitAbilityIndex[id]       = def.OnHitAbilityIndex;
            SelfPassiveAbilityIndex[id] = def.SelfPassiveAbilityIndex;

            // Story 2.6: fire the spawn-install seam LAST — every field the installer reads (SelfPassiveAbilityIndex,
            // faction, position) is now set. The subscriber (wired in SimulationHost) installs a while_alive
            // self-passive exactly once per def-based spawn. Symmetric with the OnDestroy seam; A2-safe (single mapper).
            OnUnitDefinitionApplied?.Invoke(id);
        }

        /// <summary>
        /// Resolve an authored <c>collision_radius</c> (a raw float from the unit definition) to the clamped
        /// <see cref="Fixed"/> stored in the SoA: omitted/&lt;= 0 → <see cref="DEFAULT_COLLISION_RADIUS"/> (Story 1.13
        /// AC3 — no zero-radius divide), and &gt; <see cref="MAX_COLLISION_RADIUS"/> → the cap (AC2b query-window
        /// safety). Shared by <see cref="ApplyUnitDefinition"/> and the worker spawn path so the clamp rule lives once.
        /// </summary>
        public static Fixed ClampCollisionRadius(float authoredRadius)
        {
            Fixed r = Fixed.FromFloat(authoredRadius);
            if (r <= Fixed.Zero) r = DEFAULT_COLLISION_RADIUS;
            if (r > MAX_COLLISION_RADIUS) r = MAX_COLLISION_RADIUS;
            return r;
        }

        /// <summary>
        /// Story 3.17: capture the minimal residue needed to faithfully restore a live entity — the def reference
        /// (<see cref="SourceDefinition"/>, null for a def-less spawn), the <see cref="Create"/> ctor-arg fields, the
        /// caller-owned fields the def mapper does not write (MeshType / gather state / carry capacity / supply), and
        /// the raw combat stats read ONLY by the def-less restore branch. A def-based unit needs no per-field combat
        /// capture: <see cref="RestoreUnit"/> re-derives all of it from the def through <see cref="ApplyUnitDefinition"/>.
        /// Godot-free (Core layer) so it is reachable from a Tier-1 test. No allocation beyond the returned value.
        /// </summary>
        public UnitSnapshot SnapshotUnit(int id) => new UnitSnapshot
        {
            Def           = SourceDefinition[id],
            Position      = Position[id],
            Faction       = FactionOf[id],
            // Capture the authored BASE health/speed (NOT Effective): RestoreUnit feeds these to Create (which sets
            // Base = Effective = arg), and for a def-based unit the mapper's re-installed while-alive self-passive then
            // recomputes Effective = Base + delta. Capturing Effective here would bake a live modifier's delta into the
            // restored Base and the re-install would double-count it. Base == Effective when no modifier is active
            // (every editor-placed unit today), so this is also correct for the def-less branch.
            MaxHealth     = BaseMaxHealth[id],
            Speed         = BaseMoveSpeed[id],
            MeshType      = MeshType[id],
            GatherState   = GatherState[id],
            CarryCapacity = CarryCapacity[id],
            SupplyCost    = SupplyCost[id],
            // Raw combat stats — read ONLY by the def-less restore branch (a def-based unit re-derives these).
            AttackRange  = AttackRange[id],
            AttackDamage = EffectiveAttackDamage[id],
            AttackSpeed  = AttackSpeed[id],
            DamageType   = DamageTypeOf[id],
            ArmorType    = ArmorTypeOf[id],
            VisionRange  = VisionRange[id],
            SplashRadius = SplashRadius[id],
        };

        /// <summary>
        /// Story 3.17: re-create an entity captured by <see cref="SnapshotUnit"/> (the editor delete→undo path).
        /// Returns the new entity id, or −1 if the world is full (graceful — no partial state). For a def-based unit
        /// every def-derived authored field (armor, passives, abilities/Energy, feedback, tags, attack domain,
        /// delivery/projectile speed, collision radius, separation priority, category, XP bounty, base/effective
        /// stats) is re-derived by routing the def back through the single <see cref="ApplyUnitDefinition"/> mapper
        /// (the A2 rule) — so this restore is future-proof against any new def-derived field. A def-less unit is
        /// restored from the snapshot's raw stats (today's behavior). The caller-owned residue (MeshType / gather
        /// state / carry capacity / supply) is replayed verbatim AFTER the mapper, so worker overrides survive —
        /// identical to the <c>DoSpawnWorker</c>/<c>DoSpawnCombatUnit</c> place path.
        /// </summary>
        public int RestoreUnit(in UnitSnapshot snap)
        {
            int id = Create(snap.Position, snap.Faction, snap.MaxHealth, snap.Speed);
            if (id < 0) return -1; // World full — caller logs; matches Create's contract.

            if (snap.Def != null)
            {
                // A2: re-derive every def-derived authored field through the single mapper (fires the passive-install
                // seam; Destroy already cleared the old modifiers, so no double-install).
                ApplyUnitDefinition(id, snap.Def);
            }
            else
            {
                // Def-less fallback: restore the raw combat stats captured at delete time (today's behavior). Set BOTH
                // Base and Effective for the modifier-affected stats (the A2 non-mapper-write rule).
                AttackRange[id]           = snap.AttackRange;
                BaseAttackDamage[id]      = snap.AttackDamage;
                EffectiveAttackDamage[id] = snap.AttackDamage;
                AttackSpeed[id]           = snap.AttackSpeed;
                DamageTypeOf[id]          = snap.DamageType;
                ArmorTypeOf[id]           = snap.ArmorType;
                VisionRange[id]           = snap.VisionRange;
                SplashRadius[id]          = snap.SplashRadius;
            }

            // Replay the caller-owned residue verbatim (worker SupplyCost=0/GatherState/CarryCapacity + MeshType) so
            // it OVERRIDES the mapper — exactly as the editor place path applies these after ApplyUnitDefinition.
            MeshType[id]      = snap.MeshType;
            GatherState[id]   = snap.GatherState;
            CarryCapacity[id] = snap.CarryCapacity;
            SupplyCost[id]    = snap.SupplyCost;

            return id;
        }

        /// <summary>
        /// Destroy an entity, returning its ID to the free list.
        /// </summary>
        public void Destroy(int id)
        {
            if (id < 0 || id >= _nextId) return;
            if ((Flags[id] & EntityFlags.Alive) == 0) return;

            Flags[id] = EntityFlags.None;
            // Story 2.2b: fire the per-id destroy hook BEFORE the id returns to the free list, so subscribers
            // (the ModifierStore) revert this entity's modifiers + external accumulators while the id is still its
            // own. Synchronous + deterministic order on every peer; a no-op when nothing is subscribed.
            OnDestroy?.Invoke(id);
            _freeList[_freeCount++] = id;
            AliveCount--;
        }

        /// <summary>
        /// Story 11.3 (SP save/load): restore the private allocation bookkeeping after a save-load overlay has written
        /// the SoA arrays directly. The persistence layer writes every per-entity array element for <c>[0, nextId)</c>
        /// (and the reference arrays via <see cref="ApplyUnitDefinition"/>'s def re-resolution), then calls this to set
        /// the high-water mark, alive count, and the free list so <see cref="IsAlive"/>/<see cref="Create"/>/
        /// <see cref="HighWaterMark"/> agree with the saved world. Copies at most <see cref="MAX_ENTITIES"/> free-list
        /// entries (defensive). Godot-free integer bookkeeping only.
        /// </summary>
        /// <summary>Story 11.3 (SP save/load): the active portion of the recycle free-list (LIFO order preserved), so a
        /// save round-trips the EXACT next-slot a <see cref="Create"/> will pop — a different order would land a
        /// resumed match's next spawn at a different id and desync. Returns a fresh copy.</summary>
        public int[] CaptureFreeList()
        {
            var copy = new int[_freeCount];
            Array.Copy(_freeList, copy, _freeCount);
            return copy;
        }

        public void RestoreAllocation(int nextId, int aliveCount, int[] freeList, int freeCount)
        {
            _nextId    = nextId < 0 ? 0 : (nextId > MAX_ENTITIES ? MAX_ENTITIES : nextId);
            AliveCount = aliveCount;
            Array.Clear(_freeList);
            int n = freeCount < 0 ? 0 : (freeCount > MAX_ENTITIES ? MAX_ENTITIES : freeCount);
            if (freeList != null)
                for (int i = 0; i < n && i < freeList.Length; i++) _freeList[i] = freeList[i];
            _freeCount = n;
        }

        /// <summary>
        /// Check if an entity ID is alive.
        /// </summary>
        public bool IsAlive(int id) =>
            id >= 0 && id < _nextId && (Flags[id] & EntityFlags.Alive) != 0;

        /// <summary>
        /// DW-643 — the SINGLE "can this unit deal damage?" test, so the two systems that partition the roster on it
        /// can never disagree about the same unit.
        ///
        /// <para><see cref="ProjectChimera.Combat.CombatSystem"/> excludes a NON-combatant from every acquisition /
        /// engagement path and <see cref="ProjectChimera.AI.AiOpponentSystem"/> refuses to conscript one into a wave.
        /// Those two used to be spelled differently — <c>== Fixed.Zero</c> vs <c>&gt; Fixed.Zero</c> — which are exact
        /// complements ONLY while <see cref="EffectiveAttackDamage"/> is non-negative. That held by accident, resting
        /// on <see cref="ProjectChimera.Effects.ModifierSystem.RecomputeEntity"/>'s zero-floor, which the SoA-direct
        /// writers (notably the save-restore overlay) do not run. Under a negative value the old pair CONTRADICTED:
        /// combat saw a combatant (negative != zero) while the AI saw a non-combatant. Routing both through this pair
        /// makes the partition structural, so a future unclamped writer can produce a wrong classification but never
        /// two DIFFERENT ones.</para>
        ///
        /// <para>Bounds-guarded like <see cref="IsAlive"/>, with no liveness term of its own (every caller already
        /// gates on <see cref="IsAlive"/>) — the stat, not the entity, is what this answers.</para>
        /// </summary>
        public bool CanDealDamage(int id) =>
            id >= 0 && id < MAX_ENTITIES && EffectiveAttackDamage[id] > Fixed.Zero;

        /// <summary>
        /// The exact complement of <see cref="CanDealDamage"/> (DW-643): a unit that cannot deal damage, so combat
        /// routes it through the movement-only path instead of any engagement branch. Out-of-range ids answer
        /// <c>true</c> — the conservative side, matching <see cref="CanDealDamage"/>'s <c>false</c>.
        /// </summary>
        public bool IsNonCombatant(int id) => !CanDealDamage(id);

        /// <summary>
        /// DW-184 (the Story 2.13 AC3.4 pattern, applied to entities) — pack a live entity id into a
        /// generation-stamped CROSS-TICK reference <c>(Generation[id] &lt;&lt; 12) | id</c> (id 0–4095 = low
        /// <see cref="REF_SLOT_BITS"/> bits; generation = the upper bits). A holder that carries an entity id across
        /// ticks — and must detect the slot recycling, not silently retarget the new occupant — stores this instead
        /// of the bare id and validates it via <see cref="TryResolveRef"/> on deref. GOLDEN-NEUTRAL: at generation 0,
        /// <c>PackRef(id) == id</c>.
        /// </summary>
        public int PackRef(int id) => (Generation[id] << REF_SLOT_BITS) | id;

        /// <summary>
        /// DW-184 — resolve a packed entity reference back to a live id. Returns true and the id iff the slot is in
        /// bounds, ALIVE, and the generation still matches (the SAME entity occupies it). A stale ref — the entity
        /// died, or its slot was recycled (generation bumped, even same-tick and same-faction) — returns false, so
        /// the holder observes the death/recycle instead of ABA-retargeting the new occupant. The -1 "unresolved"
        /// sentinel resolves false: its masked id is 4095 with an arithmetic-shift generation of -1, which no slot
        /// ever holds (generations are non-negative).
        /// </summary>
        public bool TryResolveRef(int packed, out int id)
        {
            id = packed & REF_SLOT_MASK; // low 12 bits — always ≥ 0
            return IsAlive(id) && Generation[id] == (packed >> REF_SLOT_BITS);
        }

        /// <summary>
        /// Snapshot previous positions for interpolation (call at start of each sim tick).
        /// </summary>
        public void SnapshotPositions()
        {
            Array.Copy(Position, PrevPosition, _nextId);
        }

        /// <summary>
        /// Story 3.10 (NFR-1 / UX-DR62): restore this world to its EXACT post-construction state for the in-place
        /// Edit↔Play reset — the inverse of the constructor, WITHOUT reallocating (so every capture-once alias held by
        /// a presentation bridge / system stays valid). Zeroes every SoA array, re-applies the ctor sentinel fills,
        /// empties the free-list, resets <see cref="Count"/>/<see cref="HighWaterMark"/>/<see cref="AliveCount"/> to 0,
        /// and RE-SEEDS the shared <see cref="Rng"/> to <see cref="DEFAULT_RNG_SEED"/> (the ctor value) so the folded
        /// RNG state matches a fresh world. A cleared world is byte-for-byte equal to <c>new EntityWorld()</c>.
        ///
        /// Deterministically fires NOTHING: it does NOT call <see cref="Destroy"/> (so <see cref="OnDestroy"/> never
        /// runs mid-clear) and it deliberately leaves the host-lifetime <see cref="OnDestroy"/> /
        /// <see cref="OnUnitDefinitionApplied"/> subscriptions attached — they belong to the host, not the match.
        /// Pure C# / integer-only (Array ops + a re-seed); ascending-order irrelevant (a bulk wipe).
        /// </summary>
        public void Clear()
        {
            // Zero every SoA array (matches the default-initialized ctor arrays).
            Array.Clear(Flags);                 Array.Clear(Position);              Array.Clear(PrevPosition);
            Array.Clear(Velocity);              Array.Clear(BaseMoveSpeed);         Array.Clear(EffectiveMoveSpeed);
            Array.Clear(Health);                Array.Clear(BaseMaxHealth);         Array.Clear(EffectiveMaxHealth);
            Array.Clear(FactionOf);             Array.Clear(MoveTarget);            Array.Clear(AttackTarget);
            Array.Clear(AttackCooldown);        Array.Clear(AttackRange);           Array.Clear(BaseAttackDamage);
            Array.Clear(EffectiveAttackDamage); Array.Clear(BaseArmor);             Array.Clear(EffectiveArmor);
            Array.Clear(AttackSpeed);           Array.Clear(Energy);                Array.Clear(MaxEnergy);
            Array.Clear(StatusFlagsOf);         Array.Clear(DamageTypeOf);          Array.Clear(ArmorTypeOf);
            Array.Clear(VisionRange);           Array.Clear(SplashRadius);          Array.Clear(CollisionRadius);
            Array.Clear(Elevation);             // Story 6.3 (0 == the fresh-ctor state; re-sampled at the next spawn)
            Array.Clear(Delivery);              Array.Clear(ProjectileSpeed);       // Story 3.12 (Hitscan==0 / 0 speed == the fresh-ctor state)
            Array.Clear(XpBounty);              // Story 3.13 (0 == the fresh-ctor state)
            Array.Clear(KillerOf);              Array.Clear(KillerFactionOf); // Story 7.5 (re-filled to −1 below)
            Array.Clear(SeparationPriorityOf);  Array.Clear(CategoryOf);            Array.Clear(AttackDomainOf);
            Array.Clear(TagsOf);                Array.Clear(SupplyCost);            Array.Clear(MeshType);
            Array.Clear(FeedbackProfile);       Array.Clear(SourceDefinition);      Array.Clear(CommandState);
            Array.Clear(CommandGoal);
            Array.Clear(CommandTarget);         Array.Clear(PatrolWaypoints);       Array.Clear(PatrolCount);
            Array.Clear(PatrolIndex);           Array.Clear(PatrolDir);             Array.Clear(OrderQueueCmd);
            Array.Clear(OrderQueueTargetX);     Array.Clear(OrderQueueTargetZ);     Array.Clear(OrderQueueCount);
            Array.Clear(ActiveOrderCmd);        Array.Clear(AbilityId);             Array.Clear(AbilityCooldownTicks);
            Array.Clear(AbilityCount);          Array.Clear(PendingCastSlot);       Array.Clear(PendingCastTarget);
            Array.Clear(AuraAbilityIndex);      Array.Clear(OnHitAbilityIndex);     Array.Clear(SelfPassiveAbilityIndex);
            Array.Clear(HeroIndex);             Array.Clear(GatherState);           Array.Clear(GatherTarget);
            Array.Clear(CarryAmount);           Array.Clear(CarryResourceType);     Array.Clear(CarryCapacity);         Array.Clear(BuildTarget);
            Array.Clear(GateClosedTicks);       // DW-80 (0 == the fresh-ctor state)
            Array.Clear(GatherWalkStallTicks);  // DW-532 (0 == the fresh-ctor state: no streak, reservation held)
            Array.Clear(RallyMovePending);      // DW-634 (false == the fresh-ctor state)
            Array.Clear(Generation);            // DW-184 — recycle generations restart at 0 (the fresh-ctor state)
            Array.Clear(_freeList);

            // Re-apply the ctor sentinel fills (a default 0 would falsely alias slot/id 0 for these).
            Array.Fill(AttackTarget,  -1);
            Array.Fill(CommandTarget, -1);
            Array.Fill(GatherTarget,  -1);
            Array.Fill(BuildTarget,   -1);
            Array.Fill(AbilityId,         -1);
            Array.Fill(PendingCastSlot,   NO_PENDING_CAST);
            Array.Fill(PendingCastTarget, -1);
            Array.Fill(HeroIndex, HERO_NONE);
            Array.Fill(KillerOf,        -1);    // Story 7.5 sentinel (matches the ctor fill)
            Array.Fill(KillerFactionOf, -1);    // Story 7.5 sentinel (matches the ctor fill)
            DeathLog.Clear();                   // DW-367: count-only reset (records past Count are unread — the PatrolWaypoints discipline)

            _freeCount = 0;
            _nextId    = 0;
            AliveCount = 0;

            // Story 6.3: reset the sim-global elevation config to the fresh-ctor defaults (a cleared world must equal
            // new EntityWorld()). ScenarioApplier re-injects the grid + toggle/bonus on the next scenario apply.
            HeightAdvantageVision    = false;
            HeightVisionBonusPerStep = Fixed.Zero;
            _elevationGrid           = null;
            // Story 6.5: a cleared world must equal a fresh EntityWorld() — drop any prior load's pathability grid.
            // ScenarioApplier re-injects it on the next scenario apply.
            Pathability              = null;

            // Re-seed the single shared deterministic RNG to the ctor seed (the folded state must equal a fresh world).
            Rng.Seed(DEFAULT_RNG_SEED);
        }
    }
}
