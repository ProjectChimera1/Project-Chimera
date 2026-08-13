#nullable enable
using ProjectChimera.Core; // EntityWorld, Fixed, ISimSystem

namespace ProjectChimera.Effects
{
    /// <summary>
    /// Story 15-24a — the per-tick HEALTH regen system: the health_regen stat's ONE consumer. Restores
    /// <see cref="EntityWorld.EffectiveHealthRegen"/> HP each tick, clamped into
    /// <c>[0, EffectiveMaxHealth]</c> — the <c>EnergyRegenSystem</c> pattern applied to Health.
    ///
    /// <para><b>Determinism &amp; the fold contract.</b> Pure C# (no <c>using Godot;</c>, no floats, no
    /// <c>System.Random</c>, <c>Fixed</c>-only), iterating ascending id. It writes only
    /// <see cref="EntityWorld.Health"/>, which is ALREADY folded into <see cref="SimChecksum"/>, so the
    /// SYSTEM adds no new fold (the new <c>EffectiveHealthRegen</c> channel folds separately, bounded, as
    /// part of the 15-24a v26 fold set). The effective rate is the recompute's output: authored
    /// <c>BaseHealthRegen</c> + Σ health_regen modifier deltas, FLOORED AT 0 — degeneration is the DoT
    /// graph's job (an authored <c>damage</c> period routes through <c>DamageResolver</c>'s kill semantics);
    /// a regen stat must never be a silent unaudited drain, so this system only ever ADDS.</para>
    ///
    /// <para><b>Golden-neutral by construction.</b> Every shipped unit authors <c>health_regen = 0</c> and no
    /// shipped content grants the stat, so the early-out makes the whole Tick a byte-identical no-op — no
    /// existing golden moves (the EnergyRegenSystem precedent; if one did, that is a bug: regen running
    /// where it should not). NEVER LETHAL: a clamp-add cannot lower Health, so this system needs no
    /// DeathFeed and its SimulationHost slot has no before-DeathFeedDrain constraint.</para>
    ///
    /// <para><b>Placement (SimulationHost).</b> Registered immediately AFTER <c>EnergyRegenSystem</c> (the
    /// two regens tick as neighbors) and before <c>ModifierSystem</c>/<c>CombatSystem</c>, so a heal lands
    /// before this tick's combat resolves — a design choice, not a correctness constraint (regen touches
    /// only Health, and the systems between read it, so the ORDER is pinned by SystemOrderTest like every
    /// other slot).</para>
    /// </summary>
    public sealed class HealthRegenSystem : ISimSystem
    {
        /// <summary>
        /// Restore health for every alive entity: <c>Health[id] = clamp(Health[id] + EffectiveHealthRegen[id],
        /// 0, EffectiveMaxHealth[id])</c>. A unit at full health or with a zero effective rate is skipped
        /// entirely (the golden-neutrality early-out). <paramref name="dt"/> is unused — the rate is
        /// per-TICK (the regen_rate convention; the 30 Hz fixed step makes one Tick == one tick).
        /// </summary>
        public void Tick(EntityWorld world, Fixed dt)
        {
            int cap = world.HighWaterMark;
            for (int i = 0; i < cap; i++)
            {
                if (!world.IsAlive(i)) continue;
                Fixed r = world.EffectiveHealthRegen[i];
                if (r.Raw == 0) continue; // shipped content authors 0 ⇒ a true no-op (no golden can move)
                world.Health[i] = Fixed.Clamp(world.Health[i] + r, Fixed.Zero, world.EffectiveMaxHealth[i]);
            }
        }
    }
}
