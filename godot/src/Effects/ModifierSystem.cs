#nullable enable
using System;
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
    /// <para>Story 2.2a built the PIPELINE; Story 2.2b adds the <c>ModifierStore</c> that DRIVES it (apply / remove /
    /// stack / expire / DoT-HoT / energy cost). Once a store is attached (<see cref="AttachStore"/>), <see cref="Tick"/>
    /// first calls <c>ModifierStore.Advance</c> (periods fire, expiries revert their bonuses) and THEN recomputes —
    /// so combat at index 4 reads fresh effective stats the same tick. With NO store attached (the pure-pipeline
    /// unit tests) <see cref="Tick"/> is still a no-op every tick (nothing dirties), so those tests and the
    /// store-free goldens behave exactly as in 2.2a.</para>
    ///
    /// <para><b>Story 2.2b — Zero-floor.</b> The recompute now floors each <c>Effective*</c> at <see cref="Fixed.Zero"/>:
    /// a debuff can never drive damage/maxhealth/speed negative (a negative effective damage would HEAL through
    /// <c>DamageResolver</c>'s matrix; a negative speed would reverse movement; a negative maxhealth would invert the
    /// Health clamp). "Cannot attack" is modeled by <see cref="StatusFlags.Disarmed"/> — read by <c>CombatSystem</c>'s
    /// damage choke points since DW-266 — never by a sub-zero stat.
    ///
    /// <para><b>DW-664 — the floor is INDISTINGUISHABLE from an authored zero, so nothing may make an irreversible
    /// decision from it.</b> A debuff with <c>AttackDamageDelta &lt;= -BaseAttackDamage</c> (authorable:
    /// <c>Modifier.CheckDelta</c> bounds <c>|delta|</c>) drives <c>EffectiveAttackDamage</c> to exactly the same
    /// <see cref="Fixed.Zero"/> a worker is authored with, so every reader of the effective stat sees a live
    /// combatant as a non-combatant for the debuff's duration. That is correct for per-tick ROUTING and wrong for
    /// anything permanent: <c>CombatSystem.TickNonCombatant</c> used to CANCEL a force-attack order on it. Ask
    /// <c>EntityWorld.IsPermanentNonCombatant</c> (the authored <c>BaseAttackDamage</c>) for that class of
    /// decision — this floor is a clamp, not a classification.</para>
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
        private readonly Fixed[] _flatArmorBonus        = new Fixed[EntityWorld.MAX_ENTITIES]; // Story 2.6

        /// <summary>
        /// The Story 2.2b store this system drives each tick (null until <see cref="AttachStore"/>). Held, not
        /// hashed — the store folds its OWN state into <see cref="SimChecksum"/>; this is just the per-tick driver ref.
        /// </summary>
        private ModifierStore? _store;

        /// <summary>
        /// Recompute every dirty entity's effective stats from its base + net modifier bonus, then clear the flag.
        /// Ascending-id (the deterministic contract). A clean entity is left untouched — that gate is what makes a
        /// recompute happen ONLY when a modifier changed (and is the AC2 "no recompute when clean" teeth).
        ///
        /// <para><b>DW-492 — this loop is a LETHAL path.</b> The DW-325/DW-491 ceiling-collapse death used to live only
        /// inside <c>ModifierStore.ApplyStatDeltas</c>, so a bonus that reached <c>EffectiveMaxHealth</c> through any
        /// OTHER producer — an SP load's <c>ModifierStore.RestoreSlot</c> re-accumulation, or an
        /// <see cref="AccumulateBonus"/> caller outside the store — could recompute a live unit's ceiling down to zero
        /// and leave it standing (the 0-HP zombie DW-325 advertises as impossible, and a divergence between a
        /// freshly-applied and a loaded match). Every such entity is by definition dirty-but-not-yet-recomputed, i.e.
        /// exactly this loop's population, so the ceiling is snapshotted before each recompute and handed to
        /// <c>ModifierStore.RaiseExternalCeilingCollapse</c> after it. Behaviour-preserving for every store-driven
        /// change: those recompute EAGERLY and clear the flag, so they never reach this loop at all — which is why no
        /// recorded golden (none of which loads a save or accumulates outside the store) enters this body.</para>
        /// </summary>
        public void Tick(EntityWorld world, Fixed dt)
        {
            // Story 2.2b: drive the store FIRST (periods pulse; expiries RemoveSlot → AccumulateBonus(−delta), all
            // ascending-id) so the recompute below picks up every bonus/status change this tick. No-op when no store.
            _store?.Advance(world, dt);

            int cap = world.HighWaterMark;
            for (int i = 0; i < cap; i++)
            {
                // Recompute ONLY for live, dirty entities. The IsAlive guard keeps a future caller that dirtied a
                // since-recycled slot from writing stats onto a dead entity (the SoA-recycle trap). The dirty gate is
                // the 2.2a "no recompute when clean" teeth; entities the store already eager-recomputed are clean here.
                if (!world.IsAlive(i) || !_dirty[i]) continue;
                // DW-492: snapshot the PRIOR ceiling so the reconciliation below tests the same downward TRANSITION the
                // apply path does (>0 → 0), never an absolute reading — a host that legitimately sits at ceiling 0 must
                // survive a recompute here exactly as it survives a modifier apply (DW-491).
                Fixed ceilingBefore = world.EffectiveMaxHealth[i];
                RecomputeEntity(world, i);
                // Re-clamp Health under a moved ceiling and raise the collapse death if this recompute floored it. Runs
                // AFTER RecomputeEntity (which cleared the dirty flag), so a kill's OnDestroy→ClearEntity cascade finds
                // no stale flag; the walk then continues to i+1 — Destroy never allocates, so no id is skipped.
                _store?.RaiseExternalCeilingCollapse(i, ceilingBefore);
            }
        }

        /// <summary>
        /// Recompute a SINGLE entity's effective stats from base + net bonus, Zero-floored, and clear its dirty flag.
        /// Idempotent (<c>Effective = max(0, Base + Σbonus)</c> regardless of prior value), so the Story 2.2b store
        /// calls it EAGERLY right after an apply/remove — making <c>EffectiveMaxHealth</c> fresh for the same-tick
        /// MaxHealth clamp and guaranteeing combat at index 4 reads the buffed stat the tick a modifier changes it —
        /// while <see cref="Tick"/>'s dirty loop remains the catch-all for any bonus dirtied outside the store.
        /// </summary>
        internal void RecomputeEntity(EntityWorld world, int id)
        {
            if (id < 0 || id >= EntityWorld.MAX_ENTITIES) return;
            if (!world.IsAlive(id)) { _dirty[id] = false; return; }

            // Zero-floor (Story 2.2b): a debuff can never drive a stat below zero (which would heal/reverse/invert).
            // DW-28: sum with Fixed.AddSaturating (widen-then-clamp) so a pathological base + large bonus saturates at
            // Fixed.MaxValue instead of the unchecked-int-add WRAP that could wrap negative and collapse to the floor.
            world.EffectiveAttackDamage[id] = Fixed.Max(Fixed.Zero, Fixed.AddSaturating(world.BaseAttackDamage[id], _flatAttackDamageBonus[id]));
            world.EffectiveMaxHealth[id]    = Fixed.Max(Fixed.Zero, Fixed.AddSaturating(world.BaseMaxHealth[id],    _flatMaxHealthBonus[id]));
            world.EffectiveMoveSpeed[id]    = Fixed.Max(Fixed.Zero, Fixed.AddSaturating(world.BaseMoveSpeed[id],    _flatMoveSpeedBonus[id]));
            // Story 2.6: EffectiveArmor = max(0, BaseArmor + Σ armor deltas) — DamageResolver subtracts it (floored at 0).
            world.EffectiveArmor[id]        = Fixed.Max(Fixed.Zero, Fixed.AddSaturating(world.BaseArmor[id],         _flatArmorBonus[id]));
            _dirty[id] = false;
        }

        /// <summary>
        /// Wire the Story 2.2b <see cref="ModifierStore"/> this system drives each <see cref="Tick"/>. Called once at
        /// <see cref="ProjectChimera.Core.Sim.SimulationHost"/> construction AFTER both objects exist (the store's
        /// ctor needs this system, and this system needs the store — <see cref="AttachStore"/> breaks the cycle).
        /// </summary>
        internal void AttachStore(ModifierStore store) => _store = store;

        /// <summary>
        /// Zero this entity's external stat-bonus accumulators and dirty flag. Called by
        /// <see cref="ModifierStore.ClearEntity"/> on the destroy hook, because these accumulators live OUTSIDE
        /// <see cref="EntityWorld"/> and so <see cref="EntityWorld.Create"/> cannot reset them on recycle — the exact
        /// gap the Story 2.2a code review flagged. Bounds-guarded (no throw on a bad id).
        /// </summary>
        internal void ClearEntity(int id)
        {
            if (id < 0 || id >= EntityWorld.MAX_ENTITIES) return;
            _flatAttackDamageBonus[id] = Fixed.Zero;
            _flatMaxHealthBonus[id]    = Fixed.Zero;
            _flatMoveSpeedBonus[id]    = Fixed.Zero;
            _flatArmorBonus[id]        = Fixed.Zero;   // Story 2.6
            _dirty[id] = false;
        }

        /// <summary>
        /// Story 3.10 (UX-DR62): zero EVERY entity's external stat-bonus accumulators + dirty flags for the Edit↔Play
        /// reset. These accumulators live OUTSIDE <see cref="EntityWorld"/> (so <see cref="EntityWorld.Clear"/> cannot
        /// reach them — the same gap <see cref="ClearEntity"/> closes per-entity on recycle); the bulk reset is the
        /// counterpart <see cref="ModifierStore.Clear"/> invokes so a cleared store's driver is also fresh. Not folded
        /// into SimChecksum (a transient recompute optimisation), so this is behaviour-preserving for the tick loop.
        /// </summary>
        internal void ClearAll()
        {
            Array.Clear(_dirty);
            Array.Clear(_flatAttackDamageBonus);
            Array.Clear(_flatMaxHealthBonus);
            Array.Clear(_flatMoveSpeedBonus);
            Array.Clear(_flatArmorBonus);
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
        internal void AccumulateBonus(int id, Fixed attackDamageDelta, Fixed maxHealthDelta, Fixed moveSpeedDelta,
                                      Fixed armorDelta = default)
        {
            if (id < 0 || id >= EntityWorld.MAX_ENTITIES) return; // defensive bounds guard (no throw on a bad id)

            _flatAttackDamageBonus[id] += attackDamageDelta;
            _flatMaxHealthBonus[id]    += maxHealthDelta;
            _flatMoveSpeedBonus[id]    += moveSpeedDelta;
            _flatArmorBonus[id]        += armorDelta;   // Story 2.6 (optional trailing param → pre-2.6 callers unchanged)
            _dirty[id] = true;
        }
    }
}
