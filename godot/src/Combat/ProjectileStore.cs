#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions; // CombatFeedbackProfile (presentation-only ref snapshotted at fire time — Story 2.7 SD-4)

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
        /// <summary>
        /// Per-projectile travel speed (world units/second, Story 3.12). Copied from <c>EntityWorld.ProjectileSpeed</c>
        /// (the firing unit's authored speed) at Spawn, honoured at the <see cref="ProjectileSystem"/> advance step
        /// instead of the old global constant. NOT folded — <see cref="ProjectileStore"/> is never a SimChecksum input
        /// (the source SoA <c>EntityWorld.ProjectileSpeed</c> IS folded, so speed divergence still desyncs).
        /// </summary>
        public readonly Fixed[]      Speed         = new Fixed[MAX_PROJECTILES];
        /// <summary>
        /// Presentation-only feedback override snapshotted from the FIRING unit at Spawn time (Story 2.7 SD-4). The
        /// attacker entity id is unrecoverable at impact (ProjectileSystem.ApplyHit has only projId/targetId), so the
        /// ranged/splash hit event reads this slot to honour a ranged unit's override. Null ⇒ default look. NOT folded
        /// (ProjectileStore is never an input to SimChecksum).
        /// </summary>
        public readonly CombatFeedbackProfile?[] Feedback = new CombatFeedbackProfile?[MAX_PROJECTILES];
        /// <summary>
        /// Story 2.9a (D-4): true ⇒ <see cref="TargetId"/> is a BUILDING id (into BuildingStore), false ⇒ an entity id
        /// (into EntityWorld) — the existing default. Set at Spawn; a recycled slot is always overwritten (no stale
        /// flag). NOT folded (ProjectileStore is never an input to SimChecksum), so this discriminator is fold-neutral.
        /// </summary>
        public readonly bool[]       TargetIsBuilding = new bool[MAX_PROJECTILES];

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
            Fixed      speed,          // Story 3.12 — required per-projectile travel speed (the firing unit's ProjectileSpeed)
            Fixed      splashRadius = default,
            CombatFeedbackProfile? feedback = null,
            bool       targetIsBuilding = false)
        {
            int id;
            if (_freeCount > 0)
                id = _freeList[--_freeCount];
            else if (_nextId < MAX_PROJECTILES)
                id = _nextId++;
            else
                return -1; // full

            Alive[id]            = true;
            Position[id]         = position;
            TargetId[id]         = targetId;
            LastKnownPos[id]     = targetPos;
            Damage[id]           = damage;
            DmgType[id]          = dmgType;
            TargetArmor[id]      = targetArmor;
            Owner[id]            = owner;
            Speed[id]            = speed;           // Story 3.12 — always overwritten (recycled slot never inherits)
            SplashRadius[id]     = splashRadius;
            Feedback[id]         = feedback;
            TargetIsBuilding[id] = targetIsBuilding; // Story 2.9a — always overwritten (recycled slot never inherits)
            return id;
        }

        /// <summary>
        /// Story 3.10 (UX-DR62): restore this store to its EXACT post-construction state for the Edit↔Play reset —
        /// zero every SoA array + the free-list and reset the high-water mark to 0. Projectiles are runtime-only (a
        /// fresh authored start has none) and are NOT folded into SimChecksum, but the "cleared == fresh" contract
        /// still requires a complete wipe. A cleared store equals a newly-constructed one.
        /// </summary>
        public void Clear()
        {
            System.Array.Clear(Alive);         System.Array.Clear(Position);      System.Array.Clear(TargetId);
            System.Array.Clear(LastKnownPos);  System.Array.Clear(Damage);        System.Array.Clear(DmgType);
            System.Array.Clear(TargetArmor);   System.Array.Clear(Owner);         System.Array.Clear(SplashRadius);
            System.Array.Clear(Speed);         // Story 3.12
            System.Array.Clear(Feedback);      System.Array.Clear(TargetIsBuilding); System.Array.Clear(_freeList);
            _freeCount = 0;
            _nextId    = 0;
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
