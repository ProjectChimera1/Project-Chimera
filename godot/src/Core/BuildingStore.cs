namespace ProjectChimera.Core
{
    /// <summary>
    /// Type of building. Determines stats, production, and supply contribution.
    /// </summary>
    public enum BuildingType : byte
    {
        CommandCenter = 0,  // +10 supply cap; no production
        Barracks      = 1,  // Produces infantry combat units
        ArcheryRange  = 2,  // Produces ranged combat units (future)
        SiegeWorkshop = 3,  // Produces siege units (future)
    }

    /// <summary>
    /// Struct-of-Arrays storage for placed buildings.
    ///
    /// Buildings are static (non-moving). They contribute supply cap and/or
    /// serve as production queues. Each faction can have up to MAX_BUILDINGS total.
    ///
    /// Indexed by building ID (0 … Count-1).
    /// </summary>
    public class BuildingStore
    {
        public const int MAX_BUILDINGS = 64;

        // ── Core data ──────────────────────────────────────────────────────────
        public readonly bool[]         Alive;
        public readonly FixedVec3[]    Position;
        public readonly Faction[]      FactionOf;
        public readonly BuildingType[] Type;
        public readonly Fixed[]        Health;
        public readonly Fixed[]        MaxHealth;

        // ── Supply ─────────────────────────────────────────────────────────────
        /// <summary>Amount this building adds to its faction's SupplyCap when alive.</summary>
        public readonly int[]          SupplyBonus;

        // ── Construction ───────────────────────────────────────────────────────
        /// <summary>Seconds remaining until construction finishes (0 = complete).</summary>
        public readonly Fixed[]        ConstructionTimer;
        /// <summary>Total construction duration for the building type (seconds).</summary>
        public readonly Fixed[]        ConstructionDuration;

        // ── Production ─────────────────────────────────────────────────────────
        /// <summary>Seconds remaining until the current training job finishes (0 = idle).</summary>
        public readonly Fixed[]        ProductionTimer;
        /// <summary>Entity archetype being trained (0 = nothing queued).</summary>
        public readonly byte[]         ProductionQueue;

        // ── Rally point ─────────────────────────────────────────────────────────
        /// <summary>World position where newly trained units walk to after spawning.</summary>
        public readonly FixedVec3[]    RallyPoint;
        /// <summary>True when the player (or AI) has explicitly set a rally point for this building.</summary>
        public readonly bool[]         HasRallyPoint;

        /// <summary>Lifetime count of units trained here — cycles the spawn offset so
        /// held (no-rally) units never stack on the exact same fixed-point position.</summary>
        public readonly int[]          TrainedCount;

        // ── Management ─────────────────────────────────────────────────────────
        public int Count { get; private set; }

        public BuildingStore()
        {
            Alive                = new bool[MAX_BUILDINGS];
            Position             = new FixedVec3[MAX_BUILDINGS];
            FactionOf            = new Faction[MAX_BUILDINGS];
            Type                 = new BuildingType[MAX_BUILDINGS];
            Health               = new Fixed[MAX_BUILDINGS];
            MaxHealth            = new Fixed[MAX_BUILDINGS];
            SupplyBonus          = new int[MAX_BUILDINGS];
            ConstructionTimer    = new Fixed[MAX_BUILDINGS];
            ConstructionDuration = new Fixed[MAX_BUILDINGS];
            ProductionTimer      = new Fixed[MAX_BUILDINGS];
            ProductionQueue      = new byte[MAX_BUILDINGS];
            RallyPoint           = new FixedVec3[MAX_BUILDINGS];
            HasRallyPoint        = new bool[MAX_BUILDINGS];
            TrainedCount         = new int[MAX_BUILDINGS];
        }

        /// <summary>Returns true while the building is still being constructed.</summary>
        public bool IsUnderConstruction(int id) => ConstructionTimer[id] > Fixed.Zero;

        /// <summary>
        /// Place a new building. Returns its ID, or -1 if the store is full.
        /// </summary>
        public int Create(FixedVec3 position, Faction faction, BuildingType type)
        {
            if (Count >= MAX_BUILDINGS) return -1;

            int id = Count++;

            Alive[id]           = true;
            Position[id]        = position;
            FactionOf[id]       = faction;
            Type[id]            = type;
            ProductionTimer[id] = Fixed.Zero;
            ProductionQueue[id] = 0;
            TrainedCount[id]    = 0;

            // Per-type defaults
            switch (type)
            {
                case BuildingType.CommandCenter:
                    Health[id]              = Fixed.FromFloat(500f);
                    MaxHealth[id]           = Fixed.FromFloat(500f);
                    SupplyBonus[id]         = 10;
                    ConstructionDuration[id] = Fixed.FromFloat(15f);
                    break;
                case BuildingType.Barracks:
                    Health[id]              = Fixed.FromFloat(300f);
                    MaxHealth[id]           = Fixed.FromFloat(300f);
                    SupplyBonus[id]         = 0;
                    ConstructionDuration[id] = Fixed.FromFloat(10f);
                    break;
                case BuildingType.ArcheryRange:
                    Health[id]              = Fixed.FromFloat(300f);
                    MaxHealth[id]           = Fixed.FromFloat(300f);
                    SupplyBonus[id]         = 0;
                    ConstructionDuration[id] = Fixed.FromFloat(10f);
                    break;
                case BuildingType.SiegeWorkshop:
                    Health[id]              = Fixed.FromFloat(400f);
                    MaxHealth[id]           = Fixed.FromFloat(400f);
                    SupplyBonus[id]         = 0;
                    ConstructionDuration[id] = Fixed.FromFloat(12f);
                    break;
                default:
                    Health[id]              = Fixed.FromFloat(200f);
                    MaxHealth[id]           = Fixed.FromFloat(200f);
                    ConstructionDuration[id] = Fixed.FromFloat(10f);
                    break;
            }

            ConstructionTimer[id] = ConstructionDuration[id];

            return id;
        }

        /// <summary>Destroy a building (marks slot as dead; slot is not reused in Phase 1).</summary>
        public void Destroy(int id)
        {
            if (id < 0 || id >= Count) return;
            Alive[id] = false;
        }
    }
}
