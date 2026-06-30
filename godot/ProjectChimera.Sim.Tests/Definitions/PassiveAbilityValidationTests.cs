#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 2.6 AC5 — the validator's passive activation + shape rules, each with a positive case AND a negative
    /// control that is demonstrably RED without the rule (A3 "every gate ships with teeth"). Graphs are built directly
    /// in C# so the validator's own gates are under test. Every reject asserts the located error contains the offending
    /// path/keyword. Pairs with <see cref="NegativeAbilityValidationTests"/> (the 2.3 active-ability rules).
    /// </summary>
    public class PassiveAbilityValidationTests
    {
        private static readonly AbilityValidator V = new();

        private static AbilityDefinition Passive(string activation, EffectNode? graph,
                                                 string targeting = "None", string id = "ptest") =>
            new AbilityDefinition { Id = id, Targeting = targeting, Activation = activation, EffectGraph = graph };

        // The canonical aura graph: SearchArea(Ally) → ApplyModifier(+5 armor, short Refresh). The trailing armorDelta
        // ctor arg exercises the new Story-2.6 Modifier field.
        private static SearchAreaEffect AuraGraph() => new SearchAreaEffect(
            Fixed.FromInt(5), TargetFilter.Ally,
            new ApplyModifierEffect(new Modifier(2001, 2, StackRule.Refresh, 1,
                Fixed.Zero, Fixed.Zero, Fixed.Zero, StatusFlags.None, null, 0, armorDelta: Fixed.FromInt(5))));

        // ── Activation parse (closed set) ──

        [Fact]
        public void UnknownActivation_IsRejected()
        {
            var def = new AbilityDefinition { Id = "bog", Targeting = "Self", Activation = "telepathic",
                                              EffectGraph = new HealEffect(Fixed.FromInt(1)) };
            AbilityValidationResult r = V.Validate(def);
            Assert.False(r.Ok);
            Assert.Contains("activation", r.Error!);
            Assert.Contains("telepathic", r.Error!);
        }

        [Fact]
        public void OmittedActivation_DefaultsToActive_StillValidatesLikeAnActiveAbility()
        {
            // Default Activation == "active": a normal active ability (with cost + cooldown) is unaffected by 2.6.
            var def = new AbilityDefinition
            {
                Id = "act", Targeting = "Self", CostEnergy = Fixed.FromInt(20), Cooldown = Fixed.FromInt(3),
                EffectGraph = new HealEffect(Fixed.FromInt(40)),
            };
            Assert.Equal("active", def.Activation);
            Assert.True(V.Validate(def).Ok, V.Validate(def).Error);
        }

        // ── AC1 aura shape ──

        [Fact]
        public void ValidAura_Passes()
        {
            Assert.True(V.Validate(Passive("aura", AuraGraph())).Ok, V.Validate(Passive("aura", AuraGraph())).Error);
        }

        [Fact]
        public void Aura_WrongTargeting_IsRejected()
        {
            // Teeth: aura must use targeting None (remove the targeting constraint → this passes → RED).
            AbilityValidationResult r = V.Validate(Passive("aura", AuraGraph(), targeting: "Self"));
            Assert.False(r.Ok);
            Assert.Contains("targeting", r.Error!);
        }

        [Fact]
        public void Aura_NonSearchAreaRoot_IsRejected()
        {
            // Teeth: aura root must be a SearchArea (a bare ApplyModifier is not an aura).
            var apply = new ApplyModifierEffect(new Modifier(1, 2, StackRule.Refresh, 1,
                Fixed.Zero, Fixed.Zero, Fixed.Zero, StatusFlags.None, null, 0, armorDelta: Fixed.FromInt(5)));
            AbilityValidationResult r = V.Validate(Passive("aura", apply));
            Assert.False(r.Ok);
            Assert.Contains("SearchArea", r.Error!);
        }

        [Fact]
        public void Aura_SearchAreaChildNotApplyModifier_IsRejected()
        {
            // Teeth: an aura's SearchArea child must be ApplyModifier (a SearchArea → Damage is an AoE nuke, not an aura).
            var sa = new SearchAreaEffect(Fixed.FromInt(5), TargetFilter.Ally,
                new DamageEffect(Fixed.FromInt(10), DamageType.Magic));
            AbilityValidationResult r = V.Validate(Passive("aura", sa));
            Assert.False(r.Ok);
            Assert.Contains("effect.child", r.Error!);
        }

        [Fact]
        public void Aura_PermanentModifier_IsRejected()
        {
            // Teeth (Story 2.6 review): the aura grant is re-applied each tick, so it must be SHORT — a permanent
            // (duration_ticks < 0) grant never lapses when an ally leaves the radius (breaks AC1). Remove the
            // duration check → this passes → RED.
            var sa = new SearchAreaEffect(Fixed.FromInt(5), TargetFilter.Ally,
                new ApplyModifierEffect(new Modifier(2002, -1, StackRule.Refresh, 1,
                    Fixed.Zero, Fixed.Zero, Fixed.Zero, StatusFlags.None, null, 0, armorDelta: Fixed.FromInt(5))));
            AbilityValidationResult r = V.Validate(Passive("aura", sa));
            Assert.False(r.Ok);
            Assert.Contains("duration_ticks", r.Error!);
        }

        [Fact]
        public void Aura_OneShotModifier_IsRejected()
        {
            // Teeth: duration_ticks == 0 (one-shot) is also rejected — only a finite POSITIVE duration is a valid aura grant.
            var sa = new SearchAreaEffect(Fixed.FromInt(5), TargetFilter.Ally,
                new ApplyModifierEffect(new Modifier(2003, 0, StackRule.Refresh, 1,
                    Fixed.Zero, Fixed.Zero, Fixed.Zero, StatusFlags.None, null, 0, armorDelta: Fixed.FromInt(5))));
            AbilityValidationResult r = V.Validate(Passive("aura", sa));
            Assert.False(r.Ok);
            Assert.Contains("duration_ticks", r.Error!);
        }

        [Fact]
        public void Aura_StackingModifier_IsRejected()
        {
            // Teeth: the per-tick re-apply must REFRESH — a Stack rule escalates the buff every tick. Reject non-Refresh.
            var sa = new SearchAreaEffect(Fixed.FromInt(5), TargetFilter.Ally,
                new ApplyModifierEffect(new Modifier(2004, 2, StackRule.Stack, 5,
                    Fixed.Zero, Fixed.Zero, Fixed.Zero, StatusFlags.None, null, 0, armorDelta: Fixed.FromInt(5))));
            AbilityValidationResult r = V.Validate(Passive("aura", sa));
            Assert.False(r.Ok);
            Assert.Contains("Refresh", r.Error!);
        }

        // ── AC2 on-hit shape ──

        [Fact]
        public void ValidOnHit_Passes()
        {
            var graph = new DamageEffect(Fixed.FromInt(15), DamageType.Magic);
            Assert.True(V.Validate(Passive("on_hit", graph)).Ok, V.Validate(Passive("on_hit", graph)).Error);
        }

        [Fact]
        public void OnHit_WrongTargeting_IsRejected()
        {
            AbilityValidationResult r = V.Validate(Passive("on_hit",
                new DamageEffect(Fixed.FromInt(15), DamageType.Magic), targeting: "TargetUnit"));
            Assert.False(r.Ok);
            Assert.Contains("targeting", r.Error!);
        }

        // ── AC3 while_alive shape ──

        [Fact]
        public void ValidWhileAlive_PermanentApplyModifier_Passes()
        {
            // Permanent stat modifier: duration_ticks < 0.
            var perm = new ApplyModifierEffect(new Modifier(3001, -1, StackRule.Refresh, 1,
                Fixed.FromInt(5), Fixed.Zero, Fixed.Zero, StatusFlags.None, null, 0));
            Assert.True(V.Validate(Passive("while_alive", perm, targeting: "Self")).Ok,
                V.Validate(Passive("while_alive", perm, targeting: "Self")).Error);
        }

        [Fact]
        public void WhileAlive_NonPermanentApplyModifier_IsRejected()
        {
            // Teeth: a while_alive ApplyModifier that is NOT permanent (duration >= 0) is rejected.
            var temp = new ApplyModifierEffect(new Modifier(3001, 30, StackRule.Refresh, 1,
                Fixed.FromInt(5), Fixed.Zero, Fixed.Zero, StatusFlags.None, null, 0));
            AbilityValidationResult r = V.Validate(Passive("while_alive", temp, targeting: "Self"));
            Assert.False(r.Ok);
            Assert.Contains("permanent", r.Error!);
        }

        [Fact]
        public void ValidWhileAlive_PersistentHoT_Passes()
        {
            var hot = new PersistentEffect(null, new HealEffect(Fixed.FromInt(2)), null, periodTicks: 5, periodCount: 256);
            Assert.True(V.Validate(Passive("while_alive", hot, targeting: "Self")).Ok,
                V.Validate(Passive("while_alive", hot, targeting: "Self")).Error);
        }

        [Fact]
        public void WhileAlive_PersistentWithPeriodButZeroPeriodTicks_IsRejected()
        {
            // Teeth (closes 2.5b deferred #2 for passives): a period effect with period_ticks == 0 never fires.
            var dead = new PersistentEffect(null, new HealEffect(Fixed.FromInt(2)), null, periodTicks: 0, periodCount: 256);
            AbilityValidationResult r = V.Validate(Passive("while_alive", dead, targeting: "Self"));
            Assert.False(r.Ok);
            Assert.Contains("period_ticks", r.Error!);
        }

        [Fact]
        public void WhileAlive_PersistentWithPeriodButZeroPeriodCount_IsRejected()
        {
            // Teeth (Story 2.6 review — the period_count sibling of the period_ticks rule): period_count == 0 expires
            // the Persistent immediately (InstallPersistent sets _periodsRemaining = period_count) → a validated-but-dead
            // HoT. period_ticks > 0 here isolates the period_count rule.
            var dead = new PersistentEffect(null, new HealEffect(Fixed.FromInt(2)), null, periodTicks: 5, periodCount: 0);
            AbilityValidationResult r = V.Validate(Passive("while_alive", dead, targeting: "Self"));
            Assert.False(r.Ok);
            Assert.Contains("period_count", r.Error!);
        }

        [Fact]
        public void WhileAlive_EmptyPersistent_IsRejected()
        {
            // Teeth (closes 2.5b deferred #1 for passives): a Persistent with no phases is a no-op passive.
            var empty = new PersistentEffect(null, null, null, periodTicks: 0, periodCount: 0);
            AbilityValidationResult r = V.Validate(Passive("while_alive", empty, targeting: "Self"));
            Assert.False(r.Ok);
            Assert.Contains("at least one phase", r.Error!);
        }

        [Fact]
        public void WhileAlive_WrongRootShape_IsRejected()
        {
            // A bare Heal leaf is neither a permanent ApplyModifier nor a Persistent.
            AbilityValidationResult r = V.Validate(Passive("while_alive",
                new HealEffect(Fixed.FromInt(2)), targeting: "Self"));
            Assert.False(r.Ok);
            Assert.Contains("permanent ApplyModifier or a Persistent", r.Error!);
        }

        // ── Decision #4: passives carry zero cost/cooldown ──

        [Fact]
        public void PassiveWithEnergyCost_IsRejected()
        {
            var def = Passive("aura", AuraGraph());
            def.CostEnergy = Fixed.FromInt(10);
            AbilityValidationResult r = V.Validate(def);
            Assert.False(r.Ok);
            Assert.Contains("cost_energy", r.Error!);
        }

        [Fact]
        public void PassiveWithCooldown_IsRejected()
        {
            var def = Passive("while_alive",
                new PersistentEffect(null, new HealEffect(Fixed.FromInt(2)), null, 5, 256), targeting: "Self");
            def.Cooldown = Fixed.FromInt(4);
            AbilityValidationResult r = V.Validate(def);
            Assert.False(r.Ok);
            Assert.Contains("cooldown", r.Error!);
        }

        [Fact]
        public void PassiveWithCrystalCost_IsRejected()
        {
            var def = Passive("on_hit", new DamageEffect(Fixed.FromInt(15), DamageType.Magic));
            def.CostCrystal = 3;
            AbilityValidationResult r = V.Validate(def);
            Assert.False(r.Ok);
            Assert.Contains("cost_crystal", r.Error!);
        }
    }
}
