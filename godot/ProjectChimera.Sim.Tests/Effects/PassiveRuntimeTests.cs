#nullable enable
using ProjectChimera.Combat;            // DamageContext, DamageResolver, DamageType, DamageTable
using ProjectChimera.Core;              // EntityWorld, Fixed, FixedVec3, Faction, FactionRegistry
using ProjectChimera.Core.Definitions;  // AbilityRegistry, FactionDefinition
using ProjectChimera.Core.Sim;          // SimulationHost, NullLogSink
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// Story 2.6 (PART A) — behavioral teeth for the three passive runtime drivers, driven through the real
    /// <see cref="SimulationHost"/> tick order ([3] AbilityCast → [4] Modifier → [5] Combat) so the AC1 "before
    /// CombatSystem reads Effective*" ordering is exercised end-to-end:
    ///   • AC1 aura — grants +armor to allies in radius, denies out-of-range / wrong-faction / self, and lapses
    ///     within `duration` ticks when the target leaves the radius OR the owner dies (expiry-by-non-refresh, the
    ///     no-fold design — there is NO remove bookkeeping).
    ///   • AC2 on-hit — fires its rider on a LANDED melee hit and NOT otherwise (no hit → no effect).
    ///   • AC3 self-passive — a while-alive Persistent HoT installed at the spawn seam regenerates health up to
    ///     MaxHealth; a unit without the passive does not regen (the seam is a no-op at index −1).
    ///   • the Decision #6 armor term — a flat post-matrix subtraction, floored at 0 (a hit never heals).
    /// Each driver pairs a "fires" assertion with a "stays silent without the passive" control (gate teeth).
    /// Enemies are Neutral (never Player2) so the float-scoring AI no-ops — same-machine deterministic.
    /// </summary>
    public class PassiveRuntimeTests
    {
        private static FixedVec3 V(int x, int y, int z) =>
            new FixedVec3(Fixed.FromInt(x), Fixed.FromInt(y), Fixed.FromInt(z));

        /// <summary>A fully-wired host with the given passive registry; ChecksumInterval=1 (parity with the goldens).</summary>
        private static (SimulationHost host, AbilityRegistry reg) NewHost(params AbilityDefinition[] abilities)
        {
            var reg = new AbilityRegistry(abilities);
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2),
                                             new FactionDefinition(), new FactionDefinition(), registry: reg);
            host.ChecksumInterval = 1;
            return (host, reg);
        }

        // ── AC1 — while-alive aura ────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Aura_GrantsArmorToAllyInRadius_AndDeniesOutOfRangeWrongFactionAndSelf()
        {
            var (host, reg) = NewHost(PassiveTestAbilities.AuraGuard());
            EntityWorld w = host.World;

            int owner = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.Zero);
            w.AuraAbilityIndex[owner] = reg.IndexOf("aura_guard"); // non-combatant (no attack) → combat skips it

            int allyIn  = w.Create(V(2, 0, 0),  Faction.Player1, Fixed.FromInt(100), Fixed.Zero); // in radius 5
            int allyOut = w.Create(V(50, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.Zero); // out of radius
            int enemyIn = w.Create(V(1, 0, 0),  Faction.Neutral, Fixed.FromInt(100), Fixed.Zero); // in radius, wrong faction

            host.StepOnce(); // aura[3] grants → modifier[4] recomputes EffectiveArmor the same tick

            Assert.Equal(Fixed.FromInt(5).Raw, w.EffectiveArmor[allyIn].Raw);  // +5 armor granted
            Assert.Equal(Fixed.Zero.Raw,       w.EffectiveArmor[allyOut].Raw); // out of radius → nothing
            Assert.Equal(Fixed.Zero.Raw,       w.EffectiveArmor[enemyIn].Raw); // Ally filter excludes Neutral
            Assert.Equal(Fixed.Zero.Raw,       w.EffectiveArmor[owner].Raw);   // Ally excludes the caster itself
        }

        [Fact]
        public void Aura_BuffExpires_WhenTargetLeavesRadius()
        {
            var (host, reg) = NewHost(PassiveTestAbilities.AuraGuard());
            EntityWorld w = host.World;
            int owner = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.Zero);
            w.AuraAbilityIndex[owner] = reg.IndexOf("aura_guard");
            int ally = w.Create(V(2, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.Zero);

            host.StepOnce();
            Assert.Equal(Fixed.FromInt(5).Raw, w.EffectiveArmor[ally].Raw); // granted while in range

            // Teleport the ally out of the aura radius; within `duration` (2) ticks the buff must lapse — there is
            // no "remove" pass, only the modifier expiring because it stopped being re-applied (the no-fold design).
            w.Position[ally] = V(50, 0, 0);
            for (int i = 0; i < 4; i++) host.StepOnce();
            Assert.Equal(Fixed.Zero.Raw, w.EffectiveArmor[ally].Raw); // expired
        }

        [Fact]
        public void Aura_StopsGranting_WhenOwnerDies()
        {
            var (host, reg) = NewHost(PassiveTestAbilities.AuraGuard());
            EntityWorld w = host.World;
            int owner = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.Zero);
            w.AuraAbilityIndex[owner] = reg.IndexOf("aura_guard");
            int ally = w.Create(V(2, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.Zero);

            host.StepOnce();
            Assert.Equal(Fixed.FromInt(5).Raw, w.EffectiveArmor[ally].Raw);

            w.Destroy(owner); // owner gone → the aura's IsAlive guard skips it → grant stops → buff lapses
            for (int i = 0; i < 4; i++) host.StepOnce();
            Assert.Equal(Fixed.Zero.Raw, w.EffectiveArmor[ally].Raw);
        }

        // ── AC2 — on-hit rider ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void OnHit_AddsExtraDamage_OnLandedMeleeHit()
        {
            Fixed withHit    = TargetHpLostAfterOneMeleeHit(onHit: true);
            Fixed withoutHit = TargetHpLostAfterOneMeleeHit(onHit: false);
            // The rider fires on the landed hit → strictly more damage than the identical attack without it; the
            // extra is the on-hit graph's contribution (the same gate that lands the attack drives it — no counter).
            Assert.True(withHit > withoutHit,
                $"On-hit rider added no damage: with={withHit.Raw}, without={withoutHit.Raw}");
        }

        [Fact]
        public void OnHit_DoesNotFire_WhenNoAttackLands()
        {
            var (host, reg) = NewHost(PassiveTestAbilities.OnHitSearing());
            EntityWorld w = host.World;
            int attacker = MeleeAttacker(w, reg, onHit: true);
            int target = w.Create(V(20, 0, 0), Faction.Neutral, Fixed.FromInt(1000), Fixed.Zero); // far OUT of melee range

            Fixed before = w.Health[target];
            host.StepOnce(); // attacker only chases — no hit lands → the on-hit must NOT fire
            Assert.Equal(before.Raw, w.Health[target].Raw);
        }

        private static Fixed TargetHpLostAfterOneMeleeHit(bool onHit)
        {
            var (host, reg) = NewHost(PassiveTestAbilities.OnHitSearing());
            EntityWorld w = host.World;
            int attacker = MeleeAttacker(w, reg, onHit);
            int target = w.Create(V(1, 0, 0), Faction.Neutral, Fixed.FromInt(1000), Fixed.Zero); // in melee range, 0 attack → no fight-back
            Fixed before = w.Health[target];
            host.StepOnce(); // one melee hit lands at tick 1 (cooldown starts at 0; next hit is 30 ticks away)
            return before - w.Health[target];
        }

        /// <summary>A stationary P1 melee attacker (10 dmg, range 2 ≤ the 2.5 melee threshold, 1s cadence).</summary>
        private static int MeleeAttacker(EntityWorld w, AbilityRegistry reg, bool onHit)
        {
            int a = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.Zero);
            w.BaseAttackDamage[a]      = Fixed.FromInt(10);
            w.EffectiveAttackDamage[a] = Fixed.FromInt(10);
            w.AttackRange[a]           = Fixed.FromInt(2);
            w.AttackSpeed[a]           = Fixed.FromInt(1);
            if (onHit) w.OnHitAbilityIndex[a] = reg.IndexOf("onhit_searing");
            return a; // without on-hit, OnHitAbilityIndex stays at its Create default (−1) → the rider no-ops
        }

        // ── AC3 — periodic self-passive ────────────────────────────────────────────────────────────────────

        [Fact]
        public void SelfPassive_RegeneratesHealth_UpToMaxAfterSpawnInstall()
        {
            var (host, reg) = NewHost(PassiveTestAbilities.FurnaceTrickle());
            EntityWorld w = host.World;
            int unit = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.Zero); // MaxHealth 100
            w.SelfPassiveAbilityIndex[unit] = reg.IndexOf("furnace_trickle");
            w.Health[unit] = Fixed.FromInt(50); // pre-damaged
            w.OnUnitDefinitionApplied?.Invoke(unit); // fire the spawn-install seam → InstallPersistent(HoT)

            for (int i = 0; i < 30; i++) host.StepOnce();
            Assert.True(w.Health[unit] > Fixed.FromInt(50),
                $"Self-regen did not raise health off 50: {w.Health[unit].Raw}");

            for (int i = 0; i < 400; i++) host.StepOnce(); // HealEffect clamps to EffectiveMaxHealth → caps, never overshoots
            Assert.Equal(Fixed.FromInt(100).Raw, w.Health[unit].Raw);
        }

        [Fact]
        public void SelfPassive_DoesNotRegenerate_WithoutTheWhileAlivePassive()
        {
            var (host, reg) = NewHost(PassiveTestAbilities.FurnaceTrickle());
            EntityWorld w = host.World;
            int unit = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.Zero);
            // SelfPassiveAbilityIndex stays at its Create default (−1); firing the seam must be a no-op.
            w.Health[unit] = Fixed.FromInt(50);
            w.OnUnitDefinitionApplied?.Invoke(unit);
            for (int i = 0; i < 60; i++) host.StepOnce();
            Assert.Equal(Fixed.FromInt(50).Raw, w.Health[unit].Raw); // no install → no regen
        }

        // ── Decision #6 — the armor term (DamageResolver) ────────────────────────────────────────────────────

        [Fact]
        public void Armor_ReducesDamage_ByFlatEffectiveArmor()
        {
            Fixed unarmoredLoss = MeleeLoss(targetArmor: Fixed.Zero,       amount: 20);
            Fixed armoredLoss   = MeleeLoss(targetArmor: Fixed.FromInt(5), amount: 20);
            // The − EffectiveArmor term is a flat POST-matrix subtraction → exactly 5 less (both positive),
            // independent of the damage-table multiplier (both targets share the same ArmorType/type).
            Assert.Equal((unarmoredLoss - Fixed.FromInt(5)).Raw, armoredLoss.Raw);
            Assert.True(armoredLoss < unarmoredLoss);
        }

        [Fact]
        public void Armor_FloorsDamageAtZero_NeverHeals()
        {
            // EffectiveArmor far exceeds the incoming damage → the result floors at 0 (a hit never heals the target).
            Fixed loss = MeleeLoss(targetArmor: Fixed.FromInt(1000), amount: 20);
            Assert.Equal(Fixed.Zero.Raw, loss.Raw);
        }

        private static Fixed MeleeLoss(Fixed targetArmor, int amount)
        {
            var w = new EntityWorld();
            int target = w.Create(V(0, 0, 0), Faction.Neutral, Fixed.FromInt(10000), Fixed.Zero);
            w.EffectiveArmor[target] = targetArmor;
            Fixed before = w.Health[target];
            var ctx = new DamageContext(w, target, w.ArmorTypeOf[target], Faction.Player1,
                                        DamageTable.Default, null, null);
            DamageResolver.Apply(in ctx, Fixed.FromInt(amount), DamageType.Normal);
            return before - w.Health[target];
        }
    }
}
