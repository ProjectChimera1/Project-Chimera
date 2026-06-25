using System;
using ProjectChimera.Combat;
using ProjectChimera.Core.Definitions; // UnitDefinition (definition→SoA copy in ApplyUnitDefinition)

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
        public readonly Fixed[] Speed;            // Max movement speed
        public readonly Fixed[] Health;
        public readonly Fixed[] MaxHealth;
        public readonly Faction[] FactionOf;
        public readonly FixedVec3[] MoveTarget;   // Where entity is heading
        public readonly int[] AttackTarget;        // Entity ID of attack target (-1 = none)
        public readonly Fixed[] AttackCooldown;    // Time until next attack
        public readonly Fixed[] AttackRange;
        public readonly Fixed[] AttackDamage;
        public readonly Fixed[] AttackSpeed;       // Seconds between attacks
        public readonly DamageType[] DamageTypeOf;
        public readonly ArmorType[] ArmorTypeOf;

        // --- Vision ---
        /// <summary>How far this unit can see (world units). Used by FogOfWarSystem.</summary>
        public readonly Fixed[] VisionRange;

        // --- AoE ---
        /// <summary>
        /// Splash radius (world units) applied when a projectile from this unit hits.
        /// 0 = no splash. Set from UnitDefinition.SplashRadius; used by ProjectileSystem.
        /// </summary>
        public readonly Fixed[] SplashRadius;

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

        // --- Gatherer data (workers only; Inactive for all other units) ---
        public readonly GatherState[] GatherState;
        public readonly int[]         GatherTarget;   // ResourceNodeStore index (-1 = none)
        public readonly Fixed[]       CarryAmount;    // Current ore being carried
        public readonly Fixed[]       CarryCapacity;  // Max carry per trip

        // --- Worker construction ---
        /// <summary>
        /// Building ID the worker is walking to construct.
        /// Valid only when CommandState == Build; -1 otherwise.
        /// </summary>
        public readonly int[] BuildTarget;

        // --- Management ---
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
            Speed = new Fixed[MAX_ENTITIES];
            Health = new Fixed[MAX_ENTITIES];
            MaxHealth = new Fixed[MAX_ENTITIES];
            FactionOf = new Faction[MAX_ENTITIES];
            MoveTarget = new FixedVec3[MAX_ENTITIES];
            AttackTarget = new int[MAX_ENTITIES];
            AttackCooldown = new Fixed[MAX_ENTITIES];
            AttackRange = new Fixed[MAX_ENTITIES];
            AttackDamage = new Fixed[MAX_ENTITIES];
            AttackSpeed = new Fixed[MAX_ENTITIES];
            DamageTypeOf = new DamageType[MAX_ENTITIES];
            ArmorTypeOf = new ArmorType[MAX_ENTITIES];

            VisionRange    = new Fixed[MAX_ENTITIES];
            SplashRadius   = new Fixed[MAX_ENTITIES];
            CollisionRadius      = new Fixed[MAX_ENTITIES];              // Story 1.13 (folded v5)
            SeparationPriorityOf = new SeparationPriority[MAX_ENTITIES]; // Story 1.13 (folded v5)
            CategoryOf           = new UnitCategory[MAX_ENTITIES];       // Story 1.13 (NOT folded — presentation-read)
            SupplyCost     = new byte[MAX_ENTITIES];
            MeshType       = new byte[MAX_ENTITIES];
            CommandState   = new UnitCommand[MAX_ENTITIES];
            CommandGoal    = new FixedVec3[MAX_ENTITIES];
            CommandTarget   = new int[MAX_ENTITIES];
            PatrolWaypoints = new FixedVec3[MAX_ENTITIES * MAX_PATROL_WAYPOINTS];
            PatrolCount     = new byte[MAX_ENTITIES];
            PatrolIndex     = new byte[MAX_ENTITIES];
            PatrolDir       = new sbyte[MAX_ENTITIES];
            GatherState    = new GatherState[MAX_ENTITIES];
            GatherTarget   = new int[MAX_ENTITIES];
            CarryAmount    = new Fixed[MAX_ENTITIES];
            CarryCapacity  = new Fixed[MAX_ENTITIES];
            BuildTarget    = new int[MAX_ENTITIES];

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
            }
            else if (_nextId < MAX_ENTITIES)
            {
                id = _nextId++;
            }
            else
            {
                return -1; // Full
            }

            Flags[id] = EntityFlags.Alive;
            Position[id] = position;
            PrevPosition[id] = position;
            Velocity[id] = FixedVec3.Zero;
            Speed[id] = speed;
            Health[id] = health;
            MaxHealth[id] = health;
            FactionOf[id] = faction;
            MoveTarget[id] = position;
            AttackTarget[id]  = -1;
            AttackCooldown[id] = Fixed.Zero;
            AttackRange[id]   = Fixed.Zero;
            AttackDamage[id]  = Fixed.Zero;
            AttackSpeed[id]   = Fixed.Zero;
            DamageTypeOf[id]  = DamageType.Normal;
            ArmorTypeOf[id]   = ArmorType.Unarmored;
            VisionRange[id]   = Fixed.FromFloat(8f);
            SplashRadius[id]  = Fixed.Zero;
            // Story 1.13: default separation/formation fields on (re)allocation. A recycled slot must never carry
            // the previous unit's radius/priority/category (the classic SoA bug — cf. the 1.12 zombie-route fix).
            // SpawnUnit overwrites these from the def; Create must default them for any spawn site that forgets.
            CollisionRadius[id]      = DEFAULT_COLLISION_RADIUS;
            SeparationPriorityOf[id] = SeparationPriority.Normal;
            CategoryOf[id]           = UnitCategory.Melee;
            SupplyCost[id]    = 0;
            MeshType[id]      = 0;
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
            GatherState[id]   = Core.GatherState.Inactive;
            GatherTarget[id]  = -1;
            CarryAmount[id]   = Fixed.Zero;
            CarryCapacity[id] = Fixed.Zero;
            BuildTarget[id]   = -1;

            AliveCount++;
            return id;
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
            AttackDamage[id] = Fixed.FromFloat(def.AttackDamage);
            AttackSpeed[id]  = Fixed.FromFloat(def.AttackSpeed);
            DamageTypeOf[id] = def.ParsedDamageType;
            ArmorTypeOf[id]  = def.ParsedArmorType;
            SplashRadius[id] = Fixed.FromFloat(def.SplashRadius);
            SupplyCost[id]   = (byte)def.Supply;

            // Story 1.13 (DG-2 / FR-54): per-unit separation/formation fields. CollisionRadius mirrors the
            // SplashRadius float→Fixed load conversion, then is clamped (see ClampCollisionRadius). SeparationPriority
            // is folded into SimChecksum (in-sim read); Category is presentation-read (formation planning), NOT folded.
            CollisionRadius[id]      = ClampCollisionRadius(def.CollisionRadius);
            SeparationPriorityOf[id] = def.ParsedSeparationPriority;
            CategoryOf[id]           = def.ParsedCategory;
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
        /// Destroy an entity, returning its ID to the free list.
        /// </summary>
        public void Destroy(int id)
        {
            if (id < 0 || id >= _nextId) return;
            if ((Flags[id] & EntityFlags.Alive) == 0) return;

            Flags[id] = EntityFlags.None;
            _freeList[_freeCount++] = id;
            AliveCount--;
        }

        /// <summary>
        /// Check if an entity ID is alive.
        /// </summary>
        public bool IsAlive(int id) =>
            id >= 0 && id < _nextId && (Flags[id] & EntityFlags.Alive) != 0;

        /// <summary>
        /// Snapshot previous positions for interpolation (call at start of each sim tick).
        /// </summary>
        public void SnapshotPositions()
        {
            Array.Copy(Position, PrevPosition, _nextId);
        }
    }
}
