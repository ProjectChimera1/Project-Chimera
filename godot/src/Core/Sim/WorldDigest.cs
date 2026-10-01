#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ProjectChimera.Combat; // ProjectileStore

namespace ProjectChimera.Core.Sim
{
    /// <summary>
    /// Unreal trial A3 (plan A §3.2): two read-only 64-bit FNV-1a digests of the live world, wider than
    /// <see cref="SimChecksum"/>, used by every trial leg to localise a divergence that the folded checksum would only
    /// show ticks later (<see cref="SimChecksum"/> folds no <c>Velocity</c>, <c>Flags</c>, <c>CommandState</c>,
    /// <c>AttackCooldown</c>, <c>MoveTarget</c>, <c>PrevPosition</c> or projectiles; plan A F40).
    ///
    /// <list type="bullet">
    ///   <item><see cref="UnitsDigest"/> folds, for every id in <c>[0, HighWaterMark)</c> (dead slots included, as the
    ///         native <c>chimera_read_units</c> copies them), exactly the fields a <c>ChimeraUnit</c> row carries, in
    ///         row order: id, packed ref, Position xyz, PrevPosition xyz, Velocity xyz (all <see cref="Fixed.Raw"/>),
    ///         Health raw, EffectiveMaxHealth raw, faction, mesh type, flags, command state. Every value is folded as
    ///         a little-endian int32, so a host that holds the rows can recompute it.</item>
    ///   <item><see cref="WideDigest"/> folds every public array of <see cref="EntityWorld"/> (over the
    ///         <c>HighWaterMark</c> prefix, times the array's per-entity stride) and of <see cref="ProjectileStore"/>
    ///         (over its own high-water mark), plus both high-water marks, the alive count and the RNG state. Value
    ///         arrays fold their raw element bytes; the two reference arrays per store (content definitions and
    ///         feedback profiles, never mutated sim state) fold only a present/absent byte per slot. A Tier-1 test
    ///         pins, by reflection over the public fields, that no array is left out.</item>
    /// </list>
    ///
    /// <para>Godot-free, allocation-free on the hot path, no reflection (NativeAOT-safe), and a pure read: neither
    /// digest mutates anything, so a run that computes them is byte-identical to one that does not.</para>
    /// </summary>
    public static class WorldDigest
    {
        private const ulong FnvOffset = 14695981039346656037UL; // FNV-1a 64 offset basis
        private const ulong FnvPrime  = 1099511628211UL;        // FNV-1a 64 prime

        /// <summary>Digest of the per-unit rows the render host reads (see the class doc for the exact field order).</summary>
        public static ulong UnitsDigest(EntityWorld world)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            ulong h = FnvOffset;
            int n = world.HighWaterMark;
            for (int id = 0; id < n; id++)
            {
                h = MixInt(h, id);
                h = MixInt(h, world.PackRef(id));
                h = MixVec(h, world.Position[id]);
                h = MixVec(h, world.PrevPosition[id]);
                h = MixVec(h, world.Velocity[id]);
                h = MixInt(h, world.Health[id].Raw);
                h = MixInt(h, world.EffectiveMaxHealth[id].Raw);
                h = MixInt(h, (byte)world.FactionOf[id]);
                h = MixInt(h, world.MeshType[id]);
                h = MixInt(h, (byte)world.Flags[id]);
                h = MixInt(h, (byte)world.CommandState[id]);
            }
            return h;
        }

        /// <summary>Digest of every public <see cref="EntityWorld"/> and <see cref="ProjectileStore"/> array (see the
        /// class doc).</summary>
        public static ulong WideDigest(EntityWorld world, ProjectileStore projectiles)
            => WideDigest(world, projectiles, null);

        /// <summary>
        /// <see cref="WideDigest(EntityWorld, ProjectileStore)"/>, recording each folded array's
        /// <c>"&lt;Type&gt;.&lt;Field&gt;"</c> name into <paramref name="visited"/> when it is non-null. The completeness
        /// test uses the record, so the list it checks is the list actually folded.
        /// </summary>
        internal static ulong WideDigest(EntityWorld world, ProjectileStore projectiles, List<string>? visited)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (projectiles == null) throw new ArgumentNullException(nameof(projectiles));

            int n = world.HighWaterMark;
            ulong h = FnvOffset;
            h = MixInt(h, n);
            h = MixInt(h, world.AliveCount);
            h = MixULong(h, world.Rng.State);

            const string W = "EntityWorld.";
            h = Values(h, world.Flags, n, W + nameof(EntityWorld.Flags), visited);
            h = Values(h, world.Position, n, W + nameof(EntityWorld.Position), visited);
            h = Values(h, world.PrevPosition, n, W + nameof(EntityWorld.PrevPosition), visited);
            h = Values(h, world.Velocity, n, W + nameof(EntityWorld.Velocity), visited);
            h = Values(h, world.BaseMoveSpeed, n, W + nameof(EntityWorld.BaseMoveSpeed), visited);
            h = Values(h, world.EffectiveMoveSpeed, n, W + nameof(EntityWorld.EffectiveMoveSpeed), visited);
            h = Values(h, world.Health, n, W + nameof(EntityWorld.Health), visited);
            h = Values(h, world.BaseMaxHealth, n, W + nameof(EntityWorld.BaseMaxHealth), visited);
            h = Values(h, world.EffectiveMaxHealth, n, W + nameof(EntityWorld.EffectiveMaxHealth), visited);
            h = Values(h, world.FactionOf, n, W + nameof(EntityWorld.FactionOf), visited);
            h = Values(h, world.MoveTarget, n, W + nameof(EntityWorld.MoveTarget), visited);
            h = Values(h, world.AttackTarget, n, W + nameof(EntityWorld.AttackTarget), visited);
            h = Values(h, world.AttackCooldown, n, W + nameof(EntityWorld.AttackCooldown), visited);
            h = Values(h, world.AttackRange, n, W + nameof(EntityWorld.AttackRange), visited);
            h = Values(h, world.BaseAttackDamage, n, W + nameof(EntityWorld.BaseAttackDamage), visited);
            h = Values(h, world.EffectiveAttackDamage, n, W + nameof(EntityWorld.EffectiveAttackDamage), visited);
            h = Values(h, world.BaseArmor, n, W + nameof(EntityWorld.BaseArmor), visited);
            h = Values(h, world.EffectiveArmor, n, W + nameof(EntityWorld.EffectiveArmor), visited);
            h = Values(h, world.AttackSpeed, n, W + nameof(EntityWorld.AttackSpeed), visited);
            h = Values(h, world.EffectiveAttackSpeedFactor, n, W + nameof(EntityWorld.EffectiveAttackSpeedFactor), visited);
            h = Values(h, world.EffectiveCooldownReduction, n, W + nameof(EntityWorld.EffectiveCooldownReduction), visited);
            h = Values(h, world.EffectiveCritChance, n, W + nameof(EntityWorld.EffectiveCritChance), visited);
            h = Values(h, world.EffectiveDodgeChance, n, W + nameof(EntityWorld.EffectiveDodgeChance), visited);
            h = Values(h, world.EffectiveCritBonus, n, W + nameof(EntityWorld.EffectiveCritBonus), visited);
            h = Values(h, world.Energy, n, W + nameof(EntityWorld.Energy), visited);
            h = Values(h, world.MaxEnergy, n, W + nameof(EntityWorld.MaxEnergy), visited);
            h = Values(h, world.RegenRate, n, W + nameof(EntityWorld.RegenRate), visited);
            h = Values(h, world.BaseHealthRegen, n, W + nameof(EntityWorld.BaseHealthRegen), visited);
            h = Values(h, world.EffectiveHealthRegen, n, W + nameof(EntityWorld.EffectiveHealthRegen), visited);
            h = Values(h, world.StatusFlagsOf, n, W + nameof(EntityWorld.StatusFlagsOf), visited);
            h = Values(h, world.DamageTypeOf, n, W + nameof(EntityWorld.DamageTypeOf), visited);
            h = Values(h, world.ArmorTypeOf, n, W + nameof(EntityWorld.ArmorTypeOf), visited);
            h = Values(h, world.VisionRange, n, W + nameof(EntityWorld.VisionRange), visited);
            h = Values(h, world.VisionBonusFlat, n, W + nameof(EntityWorld.VisionBonusFlat), visited);
            h = Values(h, world.VisionBonusPct, n, W + nameof(EntityWorld.VisionBonusPct), visited);
            h = Values(h, world.Elevation, n, W + nameof(EntityWorld.Elevation), visited);
            h = Values(h, world.SplashRadius, n, W + nameof(EntityWorld.SplashRadius), visited);
            h = Values(h, world.Delivery, n, W + nameof(EntityWorld.Delivery), visited);
            h = Values(h, world.ProjectileSpeed, n, W + nameof(EntityWorld.ProjectileSpeed), visited);
            h = Values(h, world.XpBounty, n, W + nameof(EntityWorld.XpBounty), visited);
            h = Values(h, world.KillerOf, n, W + nameof(EntityWorld.KillerOf), visited);
            h = Values(h, world.KillerFactionOf, n, W + nameof(EntityWorld.KillerFactionOf), visited);
            h = Values(h, world.VeterancyKills, n, W + nameof(EntityWorld.VeterancyKills), visited);
            h = Values(h, world.CollisionRadius, n, W + nameof(EntityWorld.CollisionRadius), visited);
            h = Values(h, world.SeparationPriorityOf, n, W + nameof(EntityWorld.SeparationPriorityOf), visited);
            h = Values(h, world.CategoryOf, n, W + nameof(EntityWorld.CategoryOf), visited);
            h = Values(h, world.AttackDomainOf, n, W + nameof(EntityWorld.AttackDomainOf), visited);
            h = Values(h, world.TagsOf, n, W + nameof(EntityWorld.TagsOf), visited);
            h = Values(h, world.SupplyCost, n, W + nameof(EntityWorld.SupplyCost), visited);
            h = Values(h, world.MeshType, n, W + nameof(EntityWorld.MeshType), visited);
            h = Presence(h, world.FeedbackProfile, n, W + nameof(EntityWorld.FeedbackProfile), visited);
            h = Presence(h, world.SourceDefinition, n, W + nameof(EntityWorld.SourceDefinition), visited);
            h = Values(h, world.CommandState, n, W + nameof(EntityWorld.CommandState), visited);
            h = Values(h, world.CommandGoal, n, W + nameof(EntityWorld.CommandGoal), visited);
            h = Values(h, world.CommandTarget, n, W + nameof(EntityWorld.CommandTarget), visited);
            h = Values(h, world.PatrolWaypoints, n, W + nameof(EntityWorld.PatrolWaypoints), visited);
            h = Values(h, world.PatrolCount, n, W + nameof(EntityWorld.PatrolCount), visited);
            h = Values(h, world.PatrolIndex, n, W + nameof(EntityWorld.PatrolIndex), visited);
            h = Values(h, world.PatrolDir, n, W + nameof(EntityWorld.PatrolDir), visited);
            h = Values(h, world.OrderQueueCmd, n, W + nameof(EntityWorld.OrderQueueCmd), visited);
            h = Values(h, world.OrderQueueTargetX, n, W + nameof(EntityWorld.OrderQueueTargetX), visited);
            h = Values(h, world.OrderQueueTargetZ, n, W + nameof(EntityWorld.OrderQueueTargetZ), visited);
            h = Values(h, world.OrderQueueCount, n, W + nameof(EntityWorld.OrderQueueCount), visited);
            h = Values(h, world.ActiveOrderCmd, n, W + nameof(EntityWorld.ActiveOrderCmd), visited);
            h = Values(h, world.AbilityId, n, W + nameof(EntityWorld.AbilityId), visited);
            h = Values(h, world.AbilityCooldownTicks, n, W + nameof(EntityWorld.AbilityCooldownTicks), visited);
            h = Values(h, world.AbilityCount, n, W + nameof(EntityWorld.AbilityCount), visited);
            h = Values(h, world.PendingCastSlot, n, W + nameof(EntityWorld.PendingCastSlot), visited);
            h = Values(h, world.PendingCastTarget, n, W + nameof(EntityWorld.PendingCastTarget), visited);
            h = Values(h, world.PendingCastPointX, n, W + nameof(EntityWorld.PendingCastPointX), visited);
            h = Values(h, world.PendingCastPointZ, n, W + nameof(EntityWorld.PendingCastPointZ), visited);
            h = Values(h, world.AuraAbilityIndex, n, W + nameof(EntityWorld.AuraAbilityIndex), visited);
            h = Values(h, world.OnHitAbilityIndex, n, W + nameof(EntityWorld.OnHitAbilityIndex), visited);
            h = Values(h, world.SelfPassiveAbilityIndex, n, W + nameof(EntityWorld.SelfPassiveAbilityIndex), visited);
            h = Values(h, world.HeroIndex, n, W + nameof(EntityWorld.HeroIndex), visited);
            h = Values(h, world.GatherState, n, W + nameof(EntityWorld.GatherState), visited);
            h = Values(h, world.GatherTarget, n, W + nameof(EntityWorld.GatherTarget), visited);
            h = Values(h, world.CarryAmount, n, W + nameof(EntityWorld.CarryAmount), visited);
            h = Values(h, world.CarryResourceType, n, W + nameof(EntityWorld.CarryResourceType), visited);
            h = Values(h, world.CarryCapacity, n, W + nameof(EntityWorld.CarryCapacity), visited);
            h = Values(h, world.GateClosedTicks, n, W + nameof(EntityWorld.GateClosedTicks), visited);
            h = Values(h, world.GatherWalkStallTicks, n, W + nameof(EntityWorld.GatherWalkStallTicks), visited);
            h = Values(h, world.RallyMovePending, n, W + nameof(EntityWorld.RallyMovePending), visited);
            h = Values(h, world.RallyStandDownTicks, n, W + nameof(EntityWorld.RallyStandDownTicks), visited);
            h = Values(h, world.RallyGoalBestSqr, n, W + nameof(EntityWorld.RallyGoalBestSqr), visited);
            h = Values(h, world.BuildTarget, n, W + nameof(EntityWorld.BuildTarget), visited);
            h = Values(h, world.Generation, n, W + nameof(EntityWorld.Generation), visited);

            // Projectiles: own high-water mark, own capacity as the stride base.
            int pn = projectiles.HighWaterMark;
            h = MixInt(h, pn);
            const string P = "ProjectileStore.";
            const int PCAP = ProjectileStore.MAX_PROJECTILES;
            h = Values(h, projectiles.Alive, pn, PCAP, P + nameof(ProjectileStore.Alive), visited);
            h = Values(h, projectiles.Position, pn, PCAP, P + nameof(ProjectileStore.Position), visited);
            h = Values(h, projectiles.TargetId, pn, PCAP, P + nameof(ProjectileStore.TargetId), visited);
            h = Values(h, projectiles.LastKnownPos, pn, PCAP, P + nameof(ProjectileStore.LastKnownPos), visited);
            h = Values(h, projectiles.Damage, pn, PCAP, P + nameof(ProjectileStore.Damage), visited);
            h = Values(h, projectiles.DmgType, pn, PCAP, P + nameof(ProjectileStore.DmgType), visited);
            h = Values(h, projectiles.TargetArmor, pn, PCAP, P + nameof(ProjectileStore.TargetArmor), visited);
            h = Values(h, projectiles.Owner, pn, PCAP, P + nameof(ProjectileStore.Owner), visited);
            h = Values(h, projectiles.SplashRadius, pn, PCAP, P + nameof(ProjectileStore.SplashRadius), visited);
            h = Values(h, projectiles.Speed, pn, PCAP, P + nameof(ProjectileStore.Speed), visited);
            h = Presence(h, projectiles.Feedback, pn, PCAP, P + nameof(ProjectileStore.Feedback), visited);
            h = Values(h, projectiles.TargetIsBuilding, pn, PCAP, P + nameof(ProjectileStore.TargetIsBuilding), visited);
            h = Values(h, projectiles.SourceId, pn, PCAP, P + nameof(ProjectileStore.SourceId), visited);
            h = Values(h, projectiles.Age, pn, PCAP, P + nameof(ProjectileStore.Age), visited);
            return h;
        }

        // ── Folding primitives ─────────────────────────────────────────────────────────────────────────

        /// <summary>Fold the raw bytes of the first <c>count × stride</c> elements of an entity-indexed array, where
        /// stride = <c>array.Length / EntityWorld.MAX_ENTITIES</c> (1 for plain SoA arrays, the per-unit capacity for
        /// the flattened ring/slot arrays).</summary>
        private static ulong Values<T>(ulong h, T[] array, int count, string name, List<string>? visited)
            where T : unmanaged
            => Values(h, array, count, EntityWorld.MAX_ENTITIES, name, visited);

        /// <summary>Fold the raw bytes of the first <c>count × (array.Length / capacity)</c> elements.</summary>
        private static ulong Values<T>(ulong h, T[] array, int count, int capacity, string name, List<string>? visited)
            where T : unmanaged
        {
            visited?.Add(name);
            int len = PrefixLength(array.Length, count, capacity);
            h = MixInt(h, len);
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<T>(array, 0, len));
            for (int i = 0; i < bytes.Length; i++)
                h = (h ^ bytes[i]) * FnvPrime;
            return h;
        }

        /// <summary>Fold one present (1) / absent (0) byte per slot of an entity-indexed reference array.</summary>
        private static ulong Presence<T>(ulong h, T?[] array, int count, string name, List<string>? visited)
            where T : class
            => Presence(h, array, count, EntityWorld.MAX_ENTITIES, name, visited);

        /// <summary>Fold one present (1) / absent (0) byte per slot of a reference array.</summary>
        private static ulong Presence<T>(ulong h, T?[] array, int count, int capacity, string name, List<string>? visited)
            where T : class
        {
            visited?.Add(name);
            int len = PrefixLength(array.Length, count, capacity);
            h = MixInt(h, len);
            for (int i = 0; i < len; i++)
                h = (h ^ (array[i] != null ? 1UL : 0UL)) * FnvPrime;
            return h;
        }

        /// <summary><c>count × stride</c>, clamped to the array, where stride = <c>length / capacity</c> (min 1).</summary>
        private static int PrefixLength(int length, int count, int capacity)
        {
            int stride = capacity > 0 ? length / capacity : 1;
            if (stride < 1) stride = 1;
            long len = (long)count * stride;
            if (len < 0) return 0;
            return len > length ? length : (int)len;
        }

        private static ulong MixVec(ulong h, FixedVec3 v)
        {
            h = MixInt(h, v.X.Raw);
            h = MixInt(h, v.Y.Raw);
            return MixInt(h, v.Z.Raw);
        }

        /// <summary>FNV-1a 64 over the 4 little-endian bytes of <paramref name="value"/>.</summary>
        private static ulong MixInt(ulong h, int value)
        {
            unchecked
            {
                uint u = (uint)value;
                h = (h ^ (u & 0xFF)) * FnvPrime;
                h = (h ^ ((u >> 8) & 0xFF)) * FnvPrime;
                h = (h ^ ((u >> 16) & 0xFF)) * FnvPrime;
                h = (h ^ (u >> 24)) * FnvPrime;
                return h;
            }
        }

        /// <summary>FNV-1a 64 over the 8 little-endian bytes of <paramref name="value"/>.</summary>
        private static ulong MixULong(ulong h, ulong value)
        {
            h = MixInt(h, unchecked((int)value));
            return MixInt(h, unchecked((int)(value >> 32)));
        }
    }
}
