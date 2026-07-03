#nullable enable
using ProjectChimera.Combat;            // DamageType, ArmorType (Parsed* comparisons)
using ProjectChimera.Core;              // EntityWorld, Fixed, FixedVec3, Faction, FactionRegistry, UnitCategory, SeparationPriority
using ProjectChimera.Core.Definitions;  // UnitDefinition
using ProjectChimera.Core.Sim;          // SimulationHost, ScenarioApplier, NullLogSink
using Xunit;

namespace ProjectChimera.Sim.Tests.Core
{
    /// <summary>
    /// Story 2.2a (AC4 / retro action item A2) — the single-mapper SoA guard. Every per-unit field that derives
    /// from a <see cref="UnitDefinition"/> MUST be written through <see cref="EntityWorld.ApplyUnitDefinition"/>
    /// (the one def→SoA mapper), never hand-copied in a spawn path — this closes the 1.12/1.13 spawn-path defect
    /// class. The guards fail RED if a Godot-free def-based spawn path forgets a field (leaving it at its
    /// <see cref="EntityWorld.Create"/> default):
    ///   • <see cref="ApplyUnitDefinition_WritesEveryDefDerivedField_OffItsCreateDefault"/> — the mapper itself;
    ///   • <see cref="SpawnUnit_DefDerivedFields_MatchCreatePlusApplyUnitDefinition"/> — the public Godot-free
    ///     spawn path (<see cref="ScenarioApplier.SpawnUnit"/>) routes through that mapper.
    ///
    /// Out of Tier-1 scope (each is <c>using Godot;</c> or a private path): the primary in-match source
    /// <c>BuildingSystem.SpawnTrainedUnit</c> and <c>EntityPlacer.{DoSpawnCombatUnit,DoSpawnWorker,RestoreUnit}</c>
    /// — covered by the compiler-forced Base+Effective edits (Story 2.2a Task 1.6: a forgotten field is a compile
    /// error, not a silent gap) plus the written single-mapper rule in project-context.md / godot/CLAUDE.md.
    /// </summary>
    public class ApplyUnitDefinitionGuardTest
    {
        // A combat def whose EVERY mapped field differs from the Create() default, so each assertion is meaningful
        // and the "moved off default" teeth bite. (Create defaults: AttackRange/AttackDamage/AttackSpeed = 0,
        // VisionRange = 8, SplashRadius = 0, SupplyCost = 0, DamageType = Normal, ArmorType = Unarmored,
        // CollisionRadius = 1.0, SeparationPriority = Normal, Category = Melee, Armor = 0 [Story 2.6],
        // Aura/OnHit/SelfPassiveAbilityIndex = -1 [Story 2.6].)
        private static UnitDefinition CombatDef() => new UnitDefinition
        {
            Id = "test_combatant", DisplayName = "Test Combatant", Category = "Ranged",
            Hp = 123f, Speed = 4.25f, VisionRange = 11f, AttackRange = 6f, AttackDamage = 17f,
            AttackSpeed = 1.25f, SplashRadius = 2.5f, Supply = 3,
            DamageType = "Pierce", ArmorType = "Heavy", Armor = 7f,
            CollisionRadius = 0.5f, SeparationPriority = "Push",
            // Story 2.7: a non-null presentation override so the FeedbackProfile mapper teeth bite (Create default = null).
            CombatFeedback = new CombatFeedbackProfile { HitFreezeFrames = 4 },
            // Story 2.9a: a restricted attack-domain so the AttackDomainOf mapper teeth bite (Create default = All).
            AttackDomains = new[] { "Air" },
            // Story 2.11: a classification tag so the TagsOf mapper teeth bite (Create default = None).
            Tags = new[] { "Mechanical" },
        };

        [Fact]
        public void ApplyUnitDefinition_WritesEveryDefDerivedField_OffItsCreateDefault()
        {
            UnitDefinition def = CombatDef();
            var w = new EntityWorld();
            int id = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromFloat(def.Hp), Fixed.FromFloat(def.Speed));

            w.ApplyUnitDefinition(id, def);

            // BaseAttackDamage is the mapper-sourced stat; Effective mirrors it (no modifier yet).
            Assert.Equal(Fixed.FromFloat(def.AttackDamage).Raw, w.BaseAttackDamage[id].Raw);
            Assert.Equal(w.BaseAttackDamage[id].Raw,            w.EffectiveAttackDamage[id].Raw);
            // Story 2.6: BaseArmor is mapper-sourced; Effective mirrors it (no modifier yet).
            Assert.Equal(Fixed.FromFloat(def.Armor).Raw,        w.BaseArmor[id].Raw);
            Assert.Equal(w.BaseArmor[id].Raw,                   w.EffectiveArmor[id].Raw);

            // Every other def-derived field is written.
            Assert.Equal(Fixed.FromFloat(def.AttackRange).Raw,  w.AttackRange[id].Raw);
            Assert.Equal(Fixed.FromFloat(def.AttackSpeed).Raw,  w.AttackSpeed[id].Raw);
            Assert.Equal(Fixed.FromFloat(def.VisionRange).Raw,  w.VisionRange[id].Raw);
            Assert.Equal(Fixed.FromFloat(def.SplashRadius).Raw, w.SplashRadius[id].Raw);
            Assert.Equal((byte)def.Supply,                      w.SupplyCost[id]);
            Assert.Equal(def.ParsedDamageType,                  w.DamageTypeOf[id]);
            Assert.Equal(def.ParsedArmorType,                   w.ArmorTypeOf[id]);
            Assert.Equal(EntityWorld.ClampCollisionRadius(def.CollisionRadius).Raw, w.CollisionRadius[id].Raw);
            Assert.Equal(def.ParsedSeparationPriority,          w.SeparationPriorityOf[id]);
            Assert.Equal(def.ParsedCategory,                    w.CategoryOf[id]);
            // Story 2.9a: the authored attack-domain capability is written through the single mapper.
            Assert.Equal(def.ParsedAttackDomains,               w.AttackDomainOf[id]);
            // Story 2.11: the authored classification tags are written through the single mapper.
            Assert.Equal(def.ParsedTags,                        w.TagsOf[id]);
            // Story 2.7: the presentation-read feedback override is copied (by reference) through the single mapper.
            Assert.Same(def.CombatFeedback,                     w.FeedbackProfile[id]);

            // Teeth: prove the mapped values are NOT coincidentally the Create defaults.
            Assert.NotEqual(Fixed.Zero.Raw,            w.BaseAttackDamage[id].Raw);    // default 0
            Assert.NotEqual(Fixed.Zero.Raw,            w.BaseArmor[id].Raw);           // default 0 (Story 2.6)
            Assert.NotEqual(UnitCategory.Melee,        w.CategoryOf[id]);              // default Melee
            Assert.NotEqual(SeparationPriority.Normal, w.SeparationPriorityOf[id]);    // default Normal
            Assert.NotEqual(AttackDomain.All,          w.AttackDomainOf[id]);          // default All (Story 2.9a)
            Assert.NotEqual(UnitTag.None,              w.TagsOf[id]);                  // default None (Story 2.11)
            Assert.NotNull(w.FeedbackProfile[id]);                                     // default null (Story 2.7)
        }

        [Fact]
        public void SpawnUnit_DefDerivedFields_MatchCreatePlusApplyUnitDefinition()
        {
            UnitDefinition def = CombatDef();

            // Reference: the canonical Create + ApplyUnitDefinition mapping.
            var refWorld = new EntityWorld();
            int refId = refWorld.Create(FixedVec3.Zero, Faction.Player1,
                                        Fixed.FromFloat(def.Hp), Fixed.FromFloat(def.Speed));
            refWorld.ApplyUnitDefinition(refId, def);

            // Actual: the public Godot-free def-based spawn path. It must produce the same def-derived SoA fields,
            // so a path that forgets a (new) mapped field — leaving it at the Create default — goes RED here.
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2),
                                             new FactionDefinition(), new FactionDefinition());
            var applier = new ScenarioApplier(host, NullLogSink.Instance, new FactionDefinition?[5]);
            int id = applier.SpawnUnit(def, Faction.Player1, 0f, 0f);
            Assert.True(id >= 0);
            EntityWorld w = host.World;

            // Stats sourced from the Create ctor args (Hp/Speed → Base + Effective for health/move-speed).
            Assert.Equal(refWorld.BaseMaxHealth[refId].Raw,         w.BaseMaxHealth[id].Raw);
            Assert.Equal(refWorld.EffectiveMaxHealth[refId].Raw,    w.EffectiveMaxHealth[id].Raw);
            Assert.Equal(refWorld.BaseMoveSpeed[refId].Raw,         w.BaseMoveSpeed[id].Raw);
            Assert.Equal(refWorld.EffectiveMoveSpeed[refId].Raw,    w.EffectiveMoveSpeed[id].Raw);

            // The mapper-sourced attack damage (Base + mirrored Effective) — the field added in this story.
            Assert.Equal(refWorld.BaseAttackDamage[refId].Raw,      w.BaseAttackDamage[id].Raw);
            Assert.Equal(refWorld.EffectiveAttackDamage[refId].Raw, w.EffectiveAttackDamage[id].Raw);
            // Story 2.6 armor (Base + mirrored Effective) routes through the same single mapper.
            Assert.Equal(refWorld.BaseArmor[refId].Raw,             w.BaseArmor[id].Raw);
            Assert.Equal(refWorld.EffectiveArmor[refId].Raw,        w.EffectiveArmor[id].Raw);

            // Every other def-derived field.
            Assert.Equal(refWorld.AttackRange[refId].Raw,      w.AttackRange[id].Raw);
            Assert.Equal(refWorld.AttackSpeed[refId].Raw,      w.AttackSpeed[id].Raw);
            Assert.Equal(refWorld.VisionRange[refId].Raw,      w.VisionRange[id].Raw);
            Assert.Equal(refWorld.SplashRadius[refId].Raw,     w.SplashRadius[id].Raw);
            Assert.Equal(refWorld.CollisionRadius[refId].Raw,  w.CollisionRadius[id].Raw);
            Assert.Equal(refWorld.SupplyCost[refId],           w.SupplyCost[id]);
            Assert.Equal(refWorld.DamageTypeOf[refId],         w.DamageTypeOf[id]);
            Assert.Equal(refWorld.ArmorTypeOf[refId],          w.ArmorTypeOf[id]);
            Assert.Equal(refWorld.SeparationPriorityOf[refId], w.SeparationPriorityOf[id]);
            Assert.Equal(refWorld.CategoryOf[refId],           w.CategoryOf[id]);
            // Story 2.9a: SpawnUnit routes the attack-domain capability through the same single mapper.
            Assert.Equal(refWorld.AttackDomainOf[refId],       w.AttackDomainOf[id]);
            // Story 2.11: SpawnUnit routes the classification tags through the same single mapper.
            Assert.Equal(refWorld.TagsOf[refId],               w.TagsOf[id]);
            // Story 2.7: SpawnUnit routes the feedback override through the mapper (same def instance ⇒ same reference).
            Assert.Same(refWorld.FeedbackProfile[refId],       w.FeedbackProfile[id]);
        }

        // ── Story 2.4a — the FIRST per-entity ability state flows through ApplyUnitDefinition (A2), and a recycled
        //    slot never carries a prior occupant's ability/cooldown (the SoA-recycle trap). ─────────────────────

        /// <summary>A registry with one ability (only its Id matters — the registry indexes by Id).</summary>
        private static AbilityRegistry OneAbilityRegistry()
            => new AbilityRegistry(new[] { new AbilityDefinition { Id = "test_ability" } });

        /// <summary>The combat def + an energy pool + a referenced ability, with AbilityIndices resolved (the link step).</summary>
        private static UnitDefinition AbilityDef(AbilityRegistry registry)
        {
            UnitDefinition def = CombatDef();
            def.MaxEnergy = 75f;
            def.Abilities = new[] { "test_ability" };
            def.ResolveAbilities(registry); // back-fill AbilityIndices once at scenario link
            return def;
        }

        [Fact]
        public void ApplyUnitDefinition_WritesMaxEnergyAndAbilitySlots_OffTheirCreateDefault()
        {
            AbilityRegistry registry = OneAbilityRegistry();
            UnitDefinition def = AbilityDef(registry);

            var w = new EntityWorld();
            int id = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromFloat(def.Hp), Fixed.FromFloat(def.Speed));
            w.ApplyUnitDefinition(id, def);

            int slot0 = id * EntityWorld.MAX_ABILITIES_PER_UNIT + 0;
            // MaxEnergy mapped; Energy started FULL (Decision #5); ability slot 0 carries the resolved registry index.
            Assert.Equal(Fixed.FromFloat(75f).Raw, w.MaxEnergy[id].Raw);
            Assert.Equal(w.MaxEnergy[id].Raw,       w.Energy[id].Raw);
            Assert.Equal((byte)1,                   w.AbilityCount[id]);
            Assert.Equal(registry.IndexOf("test_ability"), w.AbilityId[slot0]);

            // Teeth: prove the mapped values are NOT the Create defaults (MaxEnergy 0, AbilityCount 0, AbilityId -1) —
            // a spawn path that left these at the Create default goes RED here (the retro-A2 guard).
            Assert.NotEqual(Fixed.Zero.Raw, w.MaxEnergy[id].Raw);
            Assert.NotEqual((byte)0,        w.AbilityCount[id]);
            Assert.NotEqual(-1,             w.AbilityId[slot0]);
        }

        [Fact]
        public void RecycledSlot_CarriesNoPriorAbilityOrCooldown()
        {
            AbilityRegistry registry = OneAbilityRegistry();
            UnitDefinition def = AbilityDef(registry);

            var w = new EntityWorld();
            int first = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.ApplyUnitDefinition(first, def);
            int slot0 = first * EntityWorld.MAX_ABILITIES_PER_UNIT + 0;
            w.AbilityCooldownTicks[slot0] = 99; // dirty the cooldown slot (as if a cast had started one)
            Assert.Equal((byte)1, w.AbilityCount[first]);

            w.Destroy(first);
            int reused = w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(50), Fixed.FromInt(3));
            Assert.Equal(first, reused); // free-list reuse of the same id

            // The new occupant (NO def applied) must carry NONE of the prior ability/cooldown/energy state.
            Assert.Equal((byte)0, w.AbilityCount[reused]);
            Assert.Equal(-1,      w.AbilityId[reused * EntityWorld.MAX_ABILITIES_PER_UNIT + 0]);
            Assert.Equal(0,       w.AbilityCooldownTicks[reused * EntityWorld.MAX_ABILITIES_PER_UNIT + 0]);
            Assert.Equal(EntityWorld.NO_PENDING_CAST, w.PendingCastSlot[reused]);
            Assert.Equal(Fixed.Zero.Raw, w.MaxEnergy[reused].Raw); // Create default (no def applied)
        }

        // ── Story 2.6 — passive registration: ResolveAbilities partitions by activation, ApplyUnitDefinition copies
        //    the passive indices (A2), and a recycled slot carries none of them (the SoA-recycle trap). ────────────

        /// <summary>A registry with one ability of each activation kind (only Id + Activation matter for the partition).</summary>
        private static AbilityRegistry PassiveRegistry() => new AbilityRegistry(new[]
        {
            new AbilityDefinition { Id = "active_x",  Activation = "active" },
            new AbilityDefinition { Id = "aura_x",    Activation = "aura" },
            new AbilityDefinition { Id = "onhit_x",   Activation = "on_hit" },
            new AbilityDefinition { Id = "selfreg_x", Activation = "while_alive" },
        });

        [Fact]
        public void ApplyUnitDefinition_PartitionsPassivesIntoTheirSlots_OffTheCreateDefault()
        {
            AbilityRegistry registry = PassiveRegistry();
            UnitDefinition def = CombatDef();
            def.Abilities = new[] { "active_x", "aura_x", "onhit_x", "selfreg_x" };
            def.ResolveAbilities(registry); // partition by activation at scenario link

            var w = new EntityWorld();
            int id = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromFloat(def.Hp), Fixed.FromFloat(def.Speed));
            w.ApplyUnitDefinition(id, def);

            // The ACTIVE ability fills a single cast slot; the three passives fill their dedicated slots — and a
            // passive is NEVER exposed as a castable slot (AbilityCount == 1 proves it).
            Assert.Equal((byte)1, w.AbilityCount[id]);
            Assert.Equal(registry.IndexOf("active_x"),  w.AbilityId[id * EntityWorld.MAX_ABILITIES_PER_UNIT + 0]);
            Assert.Equal(registry.IndexOf("aura_x"),    w.AuraAbilityIndex[id]);
            Assert.Equal(registry.IndexOf("onhit_x"),   w.OnHitAbilityIndex[id]);
            Assert.Equal(registry.IndexOf("selfreg_x"), w.SelfPassiveAbilityIndex[id]);

            // Teeth: each passive slot moved off its Create default (−1) — a path that forgot to copy goes RED.
            Assert.NotEqual(-1, w.AuraAbilityIndex[id]);
            Assert.NotEqual(-1, w.OnHitAbilityIndex[id]);
            Assert.NotEqual(-1, w.SelfPassiveAbilityIndex[id]);
        }

        [Fact]
        public void RecycledSlot_CarriesNoPriorPassiveRegistration()
        {
            AbilityRegistry registry = PassiveRegistry();
            UnitDefinition def = CombatDef();
            def.Abilities = new[] { "aura_x", "onhit_x", "selfreg_x" };
            def.ResolveAbilities(registry);

            var w = new EntityWorld();
            int first = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.ApplyUnitDefinition(first, def);
            Assert.NotEqual(-1, w.AuraAbilityIndex[first]); // populated for the first occupant

            w.Destroy(first);
            int reused = w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(50), Fixed.FromInt(3));
            Assert.Equal(first, reused); // same id off the free list

            // The new occupant (NO def applied) must carry NONE of the prior passive registration / armor.
            Assert.Equal(-1, w.AuraAbilityIndex[reused]);
            Assert.Equal(-1, w.OnHitAbilityIndex[reused]);
            Assert.Equal(-1, w.SelfPassiveAbilityIndex[reused]);
            Assert.Equal(Fixed.Zero.Raw, w.BaseArmor[reused].Raw);
            Assert.Equal(Fixed.Zero.Raw, w.EffectiveArmor[reused].Raw);
        }

        // ── Story 2.7 — FeedbackProfile is EntityWorld's FIRST reference-typed per-entity SoA. A recycled slot must be
        //    null-reset in Create() so a new occupant can never inherit (and render) a prior unit's feedback override —
        //    the same SoA-recycle trap that bit 1.12/1.13/2.6, now for a reference field (a stale ref would also leak GC). ──

        [Fact]
        public void RecycledSlot_CarriesNoPriorFeedbackProfile()
        {
            UnitDefinition def = CombatDef(); // carries a non-null CombatFeedback override

            var w = new EntityWorld();
            int first = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.ApplyUnitDefinition(first, def);
            Assert.Same(def.CombatFeedback, w.FeedbackProfile[first]); // populated for the first occupant

            w.Destroy(first);
            int reused = w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(50), Fixed.FromInt(3));
            Assert.Equal(first, reused); // same id off the free list

            // The new occupant (NO def applied) must carry NO prior feedback profile.
            Assert.Null(w.FeedbackProfile[reused]);
        }

        // ── Story 2.11 — TagsOf is the authored-immutable classification SoA. A recycled slot must be reset in Create()
        //    so a new (no-def) occupant never inherits a prior unit's tag (the SoA-recycle trap). This is the ONLY test
        //    with teeth on the mandatory Create() reset: the mapper-coverage + spawn-parity tests run on FRESH (zero-init
        //    None) slots and pass even if the Create() line were omitted, and default(UnitTag)==None makes that line look
        //    redundant — so without this test an omission would ship silently (mis-targeting require_tag effects on a
        //    recycled slot). This is the fold-substitute: TagsOf is not in SimChecksum, so the guard IS the coverage. ──
        [Fact]
        public void RecycledSlot_CarriesNoPriorTag()
        {
            UnitDefinition def = CombatDef(); // carries Tags = ["Mechanical"] ⇒ ParsedTags = Mechanical

            var w = new EntityWorld();
            int first = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.ApplyUnitDefinition(first, def);
            Assert.Equal(UnitTag.Mechanical, w.TagsOf[first]); // populated for the first occupant

            w.Destroy(first);
            int reused = w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(50), Fixed.FromInt(3));
            Assert.Equal(first, reused); // same id off the free list

            // The new occupant (NO def applied) must carry NO prior tag — proves the mandatory Create() recycle-reset.
            Assert.Equal(UnitTag.None, w.TagsOf[reused]);
        }
    }
}
