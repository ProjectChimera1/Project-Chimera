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

        // ── Story 3.17 — editor delete→undo restore re-installs the while-alive self-passive ──────────────────

        [Fact]
        public void SelfPassive_ReInstalledExactlyOnce_AfterDeleteUndoRestore()
        {
            // The headline behavior of Story 3.17: RestoreUnit routes a def-based unit through ApplyUnitDefinition,
            // which re-fires the OnUnitDefinitionApplied install seam — so a restored unit's while-alive self-passive
            // is re-installed. Proven end-to-end through a WIRED SimulationHost (the seam is a null no-op under a bare
            // EntityWorld). Destroy cleared the old modifiers, so the re-install must land EXACTLY ONCE.
            var (host, reg) = NewHost(PassiveTestAbilities.FurnaceTrickle());
            EntityWorld w = host.World;

            var def = new UnitDefinition { Id = "regen_unit", DisplayName = "Regen Unit", Category = "Melee",
                                           Hp = 100f, Speed = 0f, Abilities = new[] { "furnace_trickle" } };
            def.ResolveAbilities(reg); // partition furnace_trickle (while_alive) → SelfPassiveAbilityIndex

            // Original: spawn + install the HoT through the mapper (fires the seam), then snapshot / destroy / restore.
            int original = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.Zero);
            w.ApplyUnitDefinition(original, def);
            UnitSnapshot snap = w.SnapshotUnit(original);
            w.Destroy(original);              // OnDestroy → ModifierStore.ClearEntity removes the installed persistent
            int restored = w.RestoreUnit(snap); // Create + ApplyUnitDefinition → seam RE-installs the HoT

            // Control: a freshly spawned unit with the SAME passive — the single-install regen baseline.
            int control = w.Create(V(10, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.Zero);
            w.ApplyUnitDefinition(control, def);

            // Damage both equally, then tick: the restored unit must regen (seam re-fired on restore) at EXACTLY the
            // control's rate — a double-install would heal ~2×, a dropped install would leave it pinned at 50.
            w.Health[restored] = Fixed.FromInt(50);
            w.Health[control]  = Fixed.FromInt(50);
            for (int i = 0; i < 30; i++) host.StepOnce();

            Assert.True(w.Health[restored] > Fixed.FromInt(50),
                $"Restored unit did not regen — self-passive was NOT re-installed on restore: {w.Health[restored].Raw}");
            Assert.Equal(w.Health[control].Raw, w.Health[restored].Raw); // exactly one install (no double, no drop)
        }

        // ── DW-300 — the spawn install is IDEMPOTENT against a live re-ApplyUnitDefinition ────────────────────

        /// <summary>Build the def for a while-alive passive unit and resolve its passive index against the registry.</summary>
        private static UnitDefinition PassiveDef(AbilityRegistry reg, string id, string abilityId)
        {
            var def = new UnitDefinition { Id = id, DisplayName = id, Category = "Melee",
                                           Hp = 100f, Speed = 0f, Abilities = new[] { abilityId } };
            def.ResolveAbilities(reg); // partition the while_alive ability → SelfPassiveAbilityIndex
            return def;
        }

        [Fact]
        public void SelfPassive_PersistentInstalledExactlyOnce_WhenTheMapperReFiresOnALiveUnit()
        {
            // DW-300: ApplyUnitDefinition fires OnUnitDefinitionApplied → InstallSelfPassive. "Once per spawn" used to
            // rest ENTIRELY on the precondition that every mapper caller runs on a fresh Create slot. An in-place
            // re-apply on a LIVE unit (upgrade/morph/tech re-map) re-fires the seam, and InstallPersistent has no
            // same-id dedup — so each re-fire landed ANOTHER concurrent HoT until the 8-slot ring saturated.
            var (host, reg) = NewHost(PassiveTestAbilities.FurnaceTrickle());
            EntityWorld w = host.World;
            UnitDefinition def = PassiveDef(reg, "regen_unit", "furnace_trickle");

            int unit = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.Zero);
            w.ApplyUnitDefinition(unit, def);                      // the genuine spawn install
            Assert.Equal(1, host.Modifiers.CountAt(unit));

            for (int i = 0; i < 10; i++) w.ApplyUnitDefinition(unit, def); // live re-apply, ×10
            // Pre-fix this was 8 (EffectCaps.MaxModifiersPerEntity — the ring saturated and would evict real buffs).
            Assert.Equal(1, host.Modifiers.CountAt(unit));

            // Behavioral teeth: a control spawned once must regenerate at EXACTLY the same rate as the re-applied unit.
            int control = w.Create(V(10, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.Zero);
            w.ApplyUnitDefinition(control, def);
            w.Health[unit]    = Fixed.FromInt(50);
            w.Health[control] = Fixed.FromInt(50);
            for (int i = 0; i < 30; i++) host.StepOnce();

            Assert.True(w.Health[unit] > Fixed.FromInt(50),
                $"The re-applied unit lost its self-passive entirely: {w.Health[unit].Raw}");
            Assert.Equal(w.Health[control].Raw, w.Health[unit].Raw); // pre-fix: ~8× the control's heal rate
        }

        [Fact]
        public void SelfPassive_PermanentModifierNotReStacked_WhenTheMapperReFiresOnALiveUnit()
        {
            // The ApplyModifier half of the validated while_alive root shapes. ModifierStore.Apply DOES dedup by
            // Modifier.Id — but a StackRule.Stack modifier's dedup path ADDS A STACK, so a re-fired seam multiplied
            // the passive's stat bonus. DW-300 skips the whole re-run instead.
            var (host, reg) = NewHost(PassiveTestAbilities.IronSkin());
            EntityWorld w = host.World;
            UnitDefinition def = PassiveDef(reg, "ironclad", "iron_skin");
            Fixed oneStack = Fixed.FromInt(PassiveTestAbilities.IronSkinArmorPerStack);

            int unit = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.Zero);
            w.ApplyUnitDefinition(unit, def);
            host.StepOnce(); // ModifierSystem[4] recomputes EffectiveArmor from BaseArmor + the modifier bonus
            Assert.Equal(oneStack.Raw, w.EffectiveArmor[unit].Raw);

            for (int i = 0; i < 3; i++) w.ApplyUnitDefinition(unit, def); // live re-apply, ×3
            host.StepOnce();

            Assert.Equal(1, host.Modifiers.CountAt(unit));
            Assert.Equal(1, host.Modifiers.StackCountAt(unit, 0));      // pre-fix: 4 (MaxStacks) — and _stackCount IS folded
            // The mapper re-mirrored BaseArmor(0) → EffectiveArmor on every re-apply; the skipped install re-derives it
            // from the untouched accumulator, so the passive is still worth EXACTLY one stack (pre-fix: 4 × +4 = +16).
            Assert.Equal(oneStack.Raw, w.EffectiveArmor[unit].Raw);
        }

        [Fact]
        public void SelfPassive_ReInstalls_AfterTheInstalledInstanceIsGone()
        {
            // The guard must be a live-instance probe, NOT a one-shot latch: once the installed instance is gone
            // (death/recycle → ClearEntity, or an explicit removal), a re-apply must install it AGAIN.
            var (host, reg) = NewHost(PassiveTestAbilities.IronSkin());
            EntityWorld w = host.World;
            UnitDefinition def = PassiveDef(reg, "ironclad", "iron_skin");

            int unit = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.Zero);
            w.ApplyUnitDefinition(unit, def);
            Assert.Equal(1, host.Modifiers.CountAt(unit));

            host.Modifiers.RemoveByModifierId(unit, PassiveTestAbilities.IronSkinModifierId); // the instance is gone
            Assert.Equal(0, host.Modifiers.CountAt(unit));

            w.ApplyUnitDefinition(unit, def); // nothing to duplicate → the install must run
            Assert.Equal(1, host.Modifiers.CountAt(unit));
            host.StepOnce();
            Assert.Equal(Fixed.FromInt(PassiveTestAbilities.IronSkinArmorPerStack).Raw, w.EffectiveArmor[unit].Raw);
        }

        [Fact]
        public void HostsInstanceFrom_IsFalse_ForAnUnrelatedPassiveOnTheSameHost()
        {
            // Identity teeth: the probe matches the SPECIFIC root (Persistent by descriptor reference, ApplyModifier by
            // Modifier.Id) — it must never suppress a DIFFERENT passive just because the host already carries one.
            var (host, reg) = NewHost(PassiveTestAbilities.FurnaceTrickle(), PassiveTestAbilities.IronSkin());
            EntityWorld w = host.World;

            int unit = w.Create(V(0, 0, 0), Faction.Player1, Fixed.FromInt(100), Fixed.Zero);
            w.ApplyUnitDefinition(unit, PassiveDef(reg, "regen_unit", "furnace_trickle")); // Persistent installed
            Assert.Equal(1, host.Modifiers.CountAt(unit));

            // A morph to a def whose while-alive passive is a DIFFERENT ability → not a duplicate → it installs.
            w.ApplyUnitDefinition(unit, PassiveDef(reg, "ironclad", "iron_skin"));
            Assert.Equal(2, host.Modifiers.CountAt(unit));

            Assert.True(host.Modifiers.HostsInstanceFrom(unit, reg.Get(reg.IndexOf("furnace_trickle")).EffectGraph));
            Assert.True(host.Modifiers.HostsInstanceFrom(unit, reg.Get(reg.IndexOf("iron_skin")).EffectGraph));
            Assert.False(host.Modifiers.HostsInstanceFrom(unit, null));           // null root → never a duplicate
            Assert.False(host.Modifiers.HostsInstanceFrom(9999, null));           // out-of-range id → no throw
            // A root shape the probe does not recognize (here a bare Damage leaf) fails OPEN — the caller installs
            // exactly as it did before the probe existed.
            Assert.False(host.Modifiers.HostsInstanceFrom(unit, PassiveTestAbilities.OnHitSearing().EffectGraph));
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
