using System;
using ProjectChimera.Combat;

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
        Stop         = 3, // Stand still; attack enemies that enter range; never chase
        HoldPosition = 4, // Same as Stop (Phase 1); reserved for stricter non-bumping in future
        Build        = 5, // Worker walking to a build site; GatheringSystem skips this worker
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
        /// </summary>
        public readonly FixedVec3[] CommandGoal;

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
            SupplyCost     = new byte[MAX_ENTITIES];
            MeshType       = new byte[MAX_ENTITIES];
            CommandState   = new UnitCommand[MAX_ENTITIES];
            CommandGoal    = new FixedVec3[MAX_ENTITIES];
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
            SupplyCost[id]    = 0;
            MeshType[id]      = 0;
            CommandState[id]  = UnitCommand.Idle;
            CommandGoal[id]   = position;
            GatherState[id]   = Core.GatherState.Inactive;
            GatherTarget[id]  = -1;
            CarryAmount[id]   = Fixed.Zero;
            CarryCapacity[id] = Fixed.Zero;
            BuildTarget[id]   = -1;

            AliveCount++;
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
