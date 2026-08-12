#nullable enable
using ProjectChimera.Core; // EntityWorld, Fixed, ISimSystem

namespace ProjectChimera.Effects
{
    /// <summary>
    /// DW-265 / Story 15.12 — the per-tick ability-resource (energy) REGEN system. Before Story 15.12 a caster
    /// started full (<c>Energy == MaxEnergy</c>) and never recovered, so every energy-cost caster was one-cast-per-life.
    /// This system restores a flat authored amount each tick, clamped into <c>[0, MaxEnergy]</c>.
    ///
    /// <para><b>Determinism &amp; the fold contract.</b> Pure C# (no <c>using Godot;</c>, no <c>float</c>/<c>Mathf</c>,
    /// no <c>System.Random</c>, <c>Fixed</c>-only), iterating ascending id — the sim-layer rules. It writes only
    /// <see cref="EntityWorld.Energy"/>, which is ALREADY folded into <see cref="SimChecksum"/> (v6), so this system
    /// adds NO new fold and does NOT bump the checksum algorithm. The regen source
    /// (<see cref="EntityWorld.RegenRate"/>) is authored + in-tick-immutable and is NOT folded (the
    /// <c>MaxEnergy</c>/<c>BaseArmor</c> posture); its effect reaches the hash transitively through the folded
    /// <see cref="EntityWorld.Energy"/> it moves.</para>
    ///
    /// <para><b>Golden-neutral by construction.</b> Every shipped unit authors <c>regen_rate = 0</c>, so
    /// <see cref="RegenPerTick"/> returns <see cref="Fixed.Zero"/> and the per-tick write is
    /// <c>Energy = min(Energy + 0, MaxEnergy) == Energy</c> — a byte-identical no-op. Regen is exercised only by NEW
    /// test fixtures / the new <c>energy-regen-scenario</c> golden, never by shipped content (so no existing per-tick
    /// golden moves — if one did, that is a bug: regen running where it should not).</para>
    ///
    /// <para><b>Placement (SimulationHost).</b> Registered immediately BEFORE <c>AbilityCastSystem</c>, so a unit can
    /// spend energy the same tick it was regenerated. The order is fixed and deterministic either way (regen touches
    /// only <c>Energy</c>, which nothing between the two systems reads); the placement is a design choice, not a
    /// correctness constraint.</para>
    /// </summary>
    public sealed class EnergyRegenSystem : ISimSystem
    {
        // Story 15-21: the hero store, for attribute-derived energy contributions (Intelligence → max energy /
        // energy regen, the DW-265 economy Alec asked for). Null (pre-15-21 callers/tests) ⇒ flat-rate only,
        // byte-identical to the Story 15.12 behaviour.
        private readonly HeroStore? _heroes;

        public EnergyRegenSystem(HeroStore? heroes = null) => _heroes = heroes;

        /// <summary>
        /// The ONE regen seam (DW-265 → Story 15-21): the per-TICK energy amount for entity <paramref name="id"/> —
        /// the authored flat rate (<c>world.RegenRate[id]</c>) PLUS, for a live linked hero, its attribute-derived
        /// <c>energy_regen</c> contribution (<c>HeroStore.AttributeStatAt</c> — base + perLevel × (Level−1), the
        /// creator's derived-stat mapping resolved at apply). No other tick site reads
        /// <see cref="EntityWorld.RegenRate"/>, so the whole regen model still has this single place to grow.
        /// Pure read; never mutates. Deterministic: HeroIndex is a packed ref, Level is folded sim truth.
        /// </summary>
        public static Fixed RegenPerTick(EntityWorld world, HeroStore? heroes, int id)
        {
            Fixed r = world.RegenRate[id];
            if (heroes != null && heroes.TryResolveRef(world.HeroIndex[id], out int slot))
                r += heroes.AttributeStatAt(slot, Core.Definitions.AttributeStats.EnergyRegen);
            return r;
        }

        /// <summary>The pre-15-21 seam shape (flat rate only) — kept so existing callers/tests compile and read
        /// the same value they always did for a hero-less entity.</summary>
        public static Fixed RegenPerTick(EntityWorld world, int id) => RegenPerTick(world, null, id);

        /// <summary>
        /// Story 15-21: the effective energy CEILING for entity <paramref name="id"/> — authored
        /// <see cref="EntityWorld.MaxEnergy"/> plus a live linked hero's attribute-derived <c>max_energy</c>
        /// contribution. MaxEnergy has no Effective* pair and no modifier channel (15.12 posture), so the
        /// attribute term is computed at the read sites: the regen clamp here, and presentation readouts.
        /// </summary>
        public static Fixed MaxEnergyOf(EntityWorld world, HeroStore? heroes, int id)
        {
            Fixed max = world.MaxEnergy[id];
            if (heroes != null && heroes.TryResolveRef(world.HeroIndex[id], out int slot))
                max += heroes.AttributeStatAt(slot, Core.Definitions.AttributeStats.MaxEnergy);
            return max;
        }

        /// <summary>
        /// Restore energy for every alive entity: <c>Energy[id] = clamp(Energy[id] + RegenPerTick, 0, MaxEnergyOf)</c>.
        /// A unit at full energy or with a zero per-tick amount is a no-op write (byte-identical). <paramref name="dt"/>
        /// is unused — the authored rate is already per-tick (the 30 Hz fixed step makes one Tick == one tick), mirroring
        /// <c>ModifierStore.Advance</c>'s tick-counted cadence.
        /// </summary>
        public void Tick(EntityWorld world, Fixed dt)
        {
            int cap = world.HighWaterMark;
            for (int i = 0; i < cap; i++)
            {
                if (!world.IsAlive(i)) continue;
                // Read the per-tick amount from the seam ONCE and early-out when it is zero. Skipping on the SEAM's
                // result (not the raw RegenRate field) keeps the attribute-driven regen honored, while making
                // regen==0 a TRUE no-op — it no longer clamps a (theoretically) Energy>MaxEnergy slot, so shipped
                // content that never regenerates does zero per-tick work and cannot move any existing golden.
                Fixed r = RegenPerTick(world, _heroes, i);
                if (r.Raw == 0) continue;
                world.Energy[i] = Fixed.Clamp(world.Energy[i] + r, Fixed.Zero, MaxEnergyOf(world, _heroes, i));
            }
        }
    }
}
