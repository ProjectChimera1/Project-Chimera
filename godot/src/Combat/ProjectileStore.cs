using ProjectChimera.Core;

namespace ProjectChimera.Combat
{
    /// <summary>
    /// Struct-of-Arrays store for in-flight projectiles.
    /// Slots are recycled via a free list. Max 512 simultaneous projectiles.
    ///
    /// Projectiles track their target entity while it is alive; if the target
    /// dies before impact they fly toward its last known position.
    /// </summary>
    public class ProjectileStore
    {
        public const int MAX_PROJECTILES = 512;

        // --- SoA ---
        public readonly bool[]       Alive        = new bool[MAX_PROJECTILES];
        public readonly FixedVec3[]  Position     = new FixedVec3[MAX_PROJECTILES];
        public readonly int[]        TargetId     = new int[MAX_PROJECTILES];   // entity ID
        public readonly FixedVec3[]  LastKnownPos = new FixedVec3[MAX_PROJECTILES];
        public readonly Fixed[]      Damage       = new Fixed[MAX_PROJECTILES];
        public readonly DamageType[] DmgType       = new DamageType[MAX_PROJECTILES];
        public readonly ArmorType[]  TargetArmor   = new ArmorType[MAX_PROJECTILES]; // snapshotted at fire time
        public readonly Faction[]    Owner         = new Faction[MAX_PROJECTILES];
        /// <summary>AoE splash radius (0 = no splash). Copied from EntityWorld.SplashRadius at fire time.</summary>
        public readonly Fixed[]      SplashRadius  = new Fixed[MAX_PROJECTILES];

        private readonly int[] _freeList  = new int[MAX_PROJECTILES];
        private int            _freeCount;
        private int            _nextId;

        /// <summary>One past the highest ever-allocated slot. Use as iteration upper bound.</summary>
        public int HighWaterMark => _nextId;

        /// <summary>
        /// Spawn a projectile. Returns the slot index, or -1 if the store is full.
        /// </summary>
        public int Spawn(
            FixedVec3  position,
            int        targetId,
            FixedVec3  targetPos,
            Fixed      damage,
            DamageType dmgType,
            ArmorType  targetArmor,
            Faction    owner,
            Fixed      splashRadius = default)
        {
            int id;
            if (_freeCount > 0)
                id = _freeList[--_freeCount];
            else if (_nextId < MAX_PROJECTILES)
                id = _nextId++;
            else
                return -1; // full

            Alive[id]         = true;
            Position[id]      = position;
            TargetId[id]      = targetId;
            LastKnownPos[id]  = targetPos;
            Damage[id]        = damage;
            DmgType[id]       = dmgType;
            TargetArmor[id]   = targetArmor;
            Owner[id]         = owner;
            SplashRadius[id]  = splashRadius;
            return id;
        }

        /// <summary>Mark a projectile slot as free, returning it to the pool.</summary>
        public void Destroy(int id)
        {
            if (id < 0 || id >= _nextId || !Alive[id]) return;
            Alive[id] = false;
            _freeList[_freeCount++] = id;
        }
    }
}
