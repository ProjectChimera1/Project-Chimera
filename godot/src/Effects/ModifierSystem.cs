#nullable enable
using ProjectChimera.Core;

namespace ProjectChimera.Effects
{
    /// <summary>
    /// The AR-9 effective-stat recompute (Story 2.2a). For every entity whose dirty flag is set, it recomputes
    /// the three modifier-affected effective stats as <c>Effective = Base + Σ active modifier deltas</c> and clears
    /// the flag. Registered at <see cref="ProjectChimera.Core.Sim.SimulationHost"/> index 3 — immediately before
    /// <see cref="ProjectChimera.Combat.CombatSystem"/> and <see cref="ProjectChimera.Combat.ProjectileSystem"/> —
    /// so combat reads freshly-recomputed effective stats the SAME tick a modifier changes them.
    ///
    /// <para>Story 2.2a builds the PIPELINE; Story 2.2b builds the <c>ModifierStore</c> that drives it (apply /
    /// remove / stack / expire / DoT-HoT / energy cost). In 2.2a nothing sets a dirty flag, so <see cref="Tick"/> is
    /// a no-op every tick → every <c>Effective* == Base*</c> → combat and movement are byte-identical to pre-story,
    /// and the goldens do not move.</para>
    ///
    /// <para><b>Determinism (why the dirty flag + bonuses are private and UNHASHED).</b> They are a transient
    /// recompute optimisation, not sim truth: the recompute is idempotent (<c>Effective = Base + bonus</c> regardless
    /// of the prior <c>Effective</c> value or of WHEN the flag was last set), so a peer difference in dirty timing
    /// cannot diverge the <c>Effective*</c> a peer ultimately reads. Keeping them private guarantees they can never
    /// be accidentally folded into <see cref="SimChecksum"/>. The <c>Effective*</c>/<c>Energy</c> arrays themselves
    /// fold into the checksum in 2.2b, when the store first MUTATES them mid-match.</para>
    ///
    /// <para>Pure C#: no <c>using Godot;</c>, ascending-id iteration, no <c>float</c>/<c>FromFloat</c>, zero-alloc
    /// <see cref="Tick"/> (only writes into the pre-allocated arrays).</para>
    /// </summary>
    public sealed class ModifierSystem : ISimSystem
    {
        // Private + UNHASHED (see class remarks). All default false/Zero in 2.2a → Tick is a no-op. The bonuses are
        // the NET modifier deltas the Story 2.2b ModifierStore drives via AccumulateBonus on apply/remove.
        private readonly bool[]  _dirty                 = new bool[EntityWorld.MAX_ENTITIES];
        private readonly Fixed[] _flatAttackDamageBonus = new Fixed[EntityWorld.MAX_ENTITIES];
        private readonly Fixed[] _flatMaxHealthBonus    = new Fixed[EntityWorld.MAX_ENTITIES];
        private readonly Fixed[] _flatMoveSpeedBonus    = new Fixed[EntityWorld.MAX_ENTITIES];

        /// <summary>
        /// Recompute every dirty entity's effective stats from its base + net modifier bonus, then clear the flag.
        /// Ascending-id (the deterministic contract). A clean entity is left untouched — that gate is what makes a
        /// recompute happen ONLY when a modifier changed (and is the AC2 "no recompute when clean" teeth).
        /// </summary>
        public void Tick(EntityWorld world, Fixed dt)
        {
            int cap = world.HighWaterMark;
            for (int i = 0; i < cap; i++)
            {
                // Recompute ONLY for live, dirty entities. The IsAlive guard keeps a future caller that dirtied a
                // since-recycled slot from writing stats onto a dead entity (the SoA-recycle trap).
                if (!world.IsAlive(i) || !_dirty[i]) continue;

                world.EffectiveAttackDamage[i] = world.BaseAttackDamage[i] + _flatAttackDamageBonus[i];
                world.EffectiveMaxHealth[i]    = world.BaseMaxHealth[i]    + _flatMaxHealthBonus[i];
                world.EffectiveMoveSpeed[i]    = world.BaseMoveSpeed[i]    + _flatMoveSpeedBonus[i];
                _dirty[i] = false;
            }
        }

        /// <summary>
        /// Add (signed) net deltas to an entity's modifier-bonus accumulators and mark it dirty for the next
        /// <see cref="Tick"/>. The seam the AC2 test and (Story 2.2b) <c>ModifierStore.Apply</c>/<c>Remove</c> drive
        /// — apply adds <c>+delta</c>, remove adds <c>-delta</c>. The three parameters mirror
        /// <see cref="Modifier.AttackDamageDelta"/>/<see cref="Modifier.MaxHealthDelta"/>/<see cref="Modifier.MoveSpeedDelta"/>.
        /// Because the accumulators sum, applying two deltas in either order yields the identical effective stat
        /// (order-independent, AC2). Internal: the sim source compiles INTO the Tier-1 test assembly, so the test and
        /// the 2.2b store reach this without <c>InternalsVisibleTo</c>.
        /// </summary>
        /// <param name="id">Target entity id. Out-of-range ids are ignored (defensive; future callers may pass stale ids).</param>
        internal void AccumulateBonus(int id, Fixed attackDamageDelta, Fixed maxHealthDelta, Fixed moveSpeedDelta)
        {
            if (id < 0 || id >= EntityWorld.MAX_ENTITIES) return; // defensive bounds guard (no throw on a bad id)

            _flatAttackDamageBonus[id] += attackDamageDelta;
            _flatMaxHealthBonus[id]    += maxHealthDelta;
            _flatMoveSpeedBonus[id]    += moveSpeedDelta;
            _dirty[id] = true;
        }
    }
}
