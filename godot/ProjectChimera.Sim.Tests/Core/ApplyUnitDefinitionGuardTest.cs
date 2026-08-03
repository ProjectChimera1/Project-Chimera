#nullable enable
using ProjectChimera.Combat;            // DamageType, ArmorType (Parsed* comparisons)
using ProjectChimera.Core;              // EntityWorld, Fixed, FixedVec3, Faction, FactionRegistry, UnitCategory, SeparationPriority
using ProjectChimera.Core.Definitions;  // UnitDefinition
using ProjectChimera.Core.Sim;          // SimulationHost, ScenarioApplier, NullLogSink
using ProjectChimera.Economy;           // GatheringSystem.STREAMING_GATE_GRACE_TICKS (DW-80 recycle guard)
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
            // Story 3.12: an explicit Projectile delivery + a custom speed so the Delivery/ProjectileSpeed mapper teeth
            // bite (Create defaults = Hitscan / 18). ResolveDelivery("Projectile") wins regardless of AttackRange.
            Delivery = "Projectile", ProjectileSpeed = 6f,
            // Story 3.13: an authored xp_bounty so the XpBounty mapper teeth bite (Create default = 0). 42 != 0.
            XpBounty = 42,
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
            // Story 3.12: the authored delivery + projectile speed are written through the single mapper.
            Assert.Equal(def.ResolveDelivery(w.AttackRange[id]),   w.Delivery[id]);
            Assert.Equal(Fixed.FromFloat(def.ProjectileSpeed).Raw, w.ProjectileSpeed[id].Raw);
            // Story 3.13: the resolved XP bounty (authored, else cost) is written through the single mapper.
            Assert.Equal(Fixed.FromInt(def.ResolveXpBounty()).Raw, w.XpBounty[id].Raw);

            // Teeth: prove the mapped values are NOT coincidentally the Create defaults.
            Assert.NotEqual(Fixed.Zero.Raw,            w.BaseAttackDamage[id].Raw);    // default 0
            Assert.NotEqual(Fixed.Zero.Raw,            w.BaseArmor[id].Raw);           // default 0 (Story 2.6)
            Assert.NotEqual(UnitCategory.Melee,        w.CategoryOf[id]);              // default Melee
            Assert.NotEqual(SeparationPriority.Normal, w.SeparationPriorityOf[id]);    // default Normal
            Assert.NotEqual(AttackDomain.All,          w.AttackDomainOf[id]);          // default All (Story 2.9a)
            Assert.NotEqual(UnitTag.None,              w.TagsOf[id]);                  // default None (Story 2.11)
            Assert.NotNull(w.FeedbackProfile[id]);                                     // default null (Story 2.7)
            Assert.NotEqual(AttackDelivery.Hitscan,          w.Delivery[id]);          // default Hitscan (Story 3.12)
            Assert.NotEqual(ProjectileSystem.PROJECTILE_SPEED.Raw, w.ProjectileSpeed[id].Raw); // default 18 (Story 3.12)
            Assert.NotEqual(Fixed.Zero.Raw,                  w.XpBounty[id].Raw);      // default 0 (Story 3.13)
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
            // Story 3.12: SpawnUnit routes the delivery + projectile speed through the same single mapper.
            Assert.Equal(refWorld.Delivery[refId],             w.Delivery[id]);
            Assert.Equal(refWorld.ProjectileSpeed[refId].Raw,  w.ProjectileSpeed[id].Raw);
            // Story 3.13: SpawnUnit routes the XP bounty through the same single mapper.
            Assert.Equal(refWorld.XpBounty[refId].Raw,         w.XpBounty[id].Raw);
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

        // ── Story 2.12 — the shift-queue order ring is runtime state (Decision #1: NOT def-derived, so it is NOT written
        //    by ApplyUnitDefinition and NOT guarded by the mapper tests above — it is defaulted in Create and mutated by
        //    OrderApplier/OrderQueueSystem, the PatrolWaypoints posture). A recycled slot must be reset in Create() so a
        //    new occupant never inherits a prior unit's queued orders (the SoA-recycle trap). This is the ONLY teeth on
        //    that mandatory Create() reset: the SimChecksum fold teeth run on FRESH zero-init slots and pass even if the
        //    reset line were omitted (default(byte)==0), and a stale non-zero count would drive ghost orders + a desync. ──
        [Fact]
        public void RecycledSlot_CarriesNoPriorOrderQueue()
        {
            var w = new EntityWorld();
            int first = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            // Dirty the ring on the first occupant, as if a Shift order had been appended.
            int slot0 = first * EntityWorld.MAX_ORDER_QUEUE + 0;
            w.OrderQueueCount[first]   = 1;
            w.OrderQueueCmd[slot0]     = (byte)UnitCommand.Move;
            w.OrderQueueTargetX[slot0] = 42;
            w.OrderQueueTargetZ[slot0] = -17;
            Assert.Equal((byte)1, w.OrderQueueCount[first]);

            w.Destroy(first);
            int reused = w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(50), Fixed.FromInt(3));
            Assert.Equal(first, reused); // same id off the free list

            // The new occupant must carry NO prior queued orders — proves the mandatory Create() recycle-reset. The
            // count is the load-bearing field (the fold + OrderQueueSystem are both count-driven, so slots past the
            // count are unread); with count == 0 the stale Cmd/Target slots are inert.
            Assert.Equal((byte)0, w.OrderQueueCount[reused]);
        }

        // ── Story 3.2 — HeroIndex (the EntityWorld↔HeroStore link, D-8) is RUNTIME state (established when a hero
        //    spawns + a HeroStore row is minted), NOT def-derived — so, like the shift-queue ring, it is defaulted in
        //    Create (to HERO_NONE) and NOT written by ApplyUnitDefinition. A recycled slot must be reset in Create so a
        //    new occupant never inherits a stale packed hero handle (which would ABA-alias a hero row). This is the SOLE
        //    teeth on that mandatory reset: HeroIndex is UNFOLDED (the store is dormant in 3.2 — D-1), so no checksum
        //    fold catches an omission, and default(int)==0 would silently alias HeroStore slot 0. ──
        [Fact]
        public void RecycledSlot_CarriesNoPriorHeroLink()
        {
            var w = new EntityWorld();
            int first = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            // Dirty the link on the first occupant, as if a hero row had been minted + PackRef-linked at spawn.
            w.HeroIndex[first] = 12345; // a non-sentinel packed handle
            Assert.NotEqual(EntityWorld.HERO_NONE, w.HeroIndex[first]);

            w.Destroy(first);
            int reused = w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(50), Fixed.FromInt(3));
            Assert.Equal(first, reused); // same id off the free list

            // The new occupant must carry NO prior hero link — proves the mandatory Create() recycle-reset to HERO_NONE.
            Assert.Equal(EntityWorld.HERO_NONE, w.HeroIndex[reused]);
        }

        // ── Story 6.3 — Elevation is TERRAIN-derived runtime state (sampled in Create from the injected ElevationGrid),
        //    NOT def-derived — so, like HeroIndex/the shift-queue ring, it is NOT written by ApplyUnitDefinition. A
        //    recycled slot must be re-sampled by Create so a new occupant never inherits a prior occupant's elevation
        //    (the SoA-recycle trap). With no grid injected, Create writes Fixed.Zero — so a dirtied slot that reverts to
        //    Zero on recycle proves Create re-writes the field rather than leaking the stale value. Elevation IS folded
        //    (v15), but the fold teeth run on FRESH zero-init slots and pass even if the recycle re-write regressed, so
        //    this guard is the dedicated recycle coverage. ──
        [Fact]
        public void RecycledSlot_CarriesNoPriorElevation()
        {
            var w = new EntityWorld();
            int first = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            // Dirty the elevation on the first occupant, as if it had spawned on a sculpted hill.
            w.Elevation[first] = Fixed.FromInt(9);
            Assert.NotEqual(Fixed.Zero.Raw, w.Elevation[first].Raw);

            w.Destroy(first);
            int reused = w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(50), Fixed.FromInt(3));
            Assert.Equal(first, reused); // same id off the free list

            // No grid injected ⇒ Create re-samples to Fixed.Zero; the new occupant must carry NO prior elevation.
            Assert.Equal(Fixed.Zero.Raw, w.Elevation[reused].Raw);
        }

        // ── DW-80 — GateClosedTicks is the Streaming requires_structure closed-gate streak: RUNTIME state written only by
        //    GatheringSystem (never def-derived, so ApplyUnitDefinition does not touch it — the HeroIndex/order-ring
        //    posture). A recycled slot must be reset in Create so a new worker never inherits a corpse's partial grace
        //    window, which would evict it from a perfectly open node after only a tick or two of its OWN closure. This is
        //    the SOLE teeth on that mandatory reset: the field is UNFOLDED (the GatherState/GatherTarget/CarryAmount
        //    posture), so no checksum fold catches an omission, and default(int)==0 makes the reset line look redundant. ──
        [Fact]
        public void RecycledSlot_CarriesNoPriorGateClosedStreak()
        {
            var w = new EntityWorld();
            int first = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            // Dirty the streak on the first occupant, as if it had sat at a gate-closed Streaming node for a while.
            w.GateClosedTicks[first] = GatheringSystem.STREAMING_GATE_GRACE_TICKS - 1;
            Assert.NotEqual(0, w.GateClosedTicks[first]);

            w.Destroy(first);
            int reused = w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(50), Fixed.FromInt(3));
            Assert.Equal(first, reused); // same id off the free list

            // The new occupant must start its own grace window from zero — proves the mandatory Create() recycle-reset.
            Assert.Equal(0, w.GateClosedTicks[reused]);
        }

        // ── Story 3.17 — editor delete→undo restore fidelity. SnapshotUnit + RestoreUnit route a def-based unit back
        //    through ApplyUnitDefinition (the A2 mapper), so every def-derived authored field is re-derived — never a
        //    hand-copy that silently drops fields (the recurring RestoreUnit drop-debt). This Tier-1 round-trip guard
        //    fails RED if a field would be dropped: it captures the post-ApplyUnitDefinition truth, destroys, restores,
        //    and asserts byte-equality on every authored field PLUS NotEqual(CreateDefault) teeth so a dropped field
        //    (which reverts to its Create default) is caught even where it would coincidentally match. ────────────────
        [Fact]
        public void SnapshotRestore_ReproducesEveryAuthoredField_OffCreateDefault()
        {
            // A def with the full authored surface: CombatDef's stats/armor/domain/tags/feedback/delivery/xp PLUS an
            // active ability, all three passive kinds, and an energy pool — so passive indices + abilities + Energy are
            // exercised too.
            AbilityRegistry registry = PassiveRegistry();
            UnitDefinition def = CombatDef();
            def.MaxEnergy = 75f;
            def.Abilities = new[] { "active_x", "aura_x", "onhit_x", "selfreg_x" };
            def.ResolveAbilities(registry);

            var w = new EntityWorld();
            int original = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromFloat(def.Hp), Fixed.FromFloat(def.Speed));
            w.ApplyUnitDefinition(original, def);

            // Caller-owned residue set OFF its Create default (mirrors DoSpawnWorker's post-mapper overrides + MeshType):
            // SupplyCost 0 overrides def.Supply (3); GatherState/CarryCapacity are the worker gather state; MeshType 5.
            w.SupplyCost[original]    = 0;
            w.GatherState[original]   = GatherState.Idle;
            w.CarryCapacity[original] = Fixed.FromFloat(20f);
            w.MeshType[original]      = 5;

            // Capture the post-ApplyUnitDefinition truth for EVERY authored field BEFORE destroy (the id is recycled,
            // so its arrays are overwritten by Create+Restore — the expected values must live in locals).
            int slot0 = original * EntityWorld.MAX_ABILITIES_PER_UNIT + 0;
            long eBaseAtk = w.BaseAttackDamage[original].Raw,  eEffAtk = w.EffectiveAttackDamage[original].Raw;
            long eBaseArm = w.BaseArmor[original].Raw,         eEffArm = w.EffectiveArmor[original].Raw;
            long eRange   = w.AttackRange[original].Raw,       eAtkSpd = w.AttackSpeed[original].Raw;
            long eVision  = w.VisionRange[original].Raw,       eSplash = w.SplashRadius[original].Raw;
            long eColl    = w.CollisionRadius[original].Raw,   eProjSp = w.ProjectileSpeed[original].Raw;
            long eXp      = w.XpBounty[original].Raw;
            long eMaxEn   = w.MaxEnergy[original].Raw,         eEnergy = w.Energy[original].Raw;
            long eMaxHp   = w.EffectiveMaxHealth[original].Raw, eSpeed = w.EffectiveMoveSpeed[original].Raw;
            long eCarry   = w.CarryCapacity[original].Raw;
            DamageType eDmgT = w.DamageTypeOf[original];       ArmorType eArmT = w.ArmorTypeOf[original];
            SeparationPriority eSep = w.SeparationPriorityOf[original];
            UnitCategory eCat = w.CategoryOf[original];        AttackDomain eDom = w.AttackDomainOf[original];
            UnitTag eTags = w.TagsOf[original];                AttackDelivery eDel = w.Delivery[original];
            var eFeedback = w.FeedbackProfile[original];       var eDef = w.SourceDefinition[original];
            byte eAbCount = w.AbilityCount[original];          int eAbId0 = w.AbilityId[slot0];
            int eAura = w.AuraAbilityIndex[original], eOnHit = w.OnHitAbilityIndex[original], eSelf = w.SelfPassiveAbilityIndex[original];
            byte eSupply = w.SupplyCost[original],   eMesh = w.MeshType[original];
            GatherState eGather = w.GatherState[original];
            Faction eFaction = w.FactionOf[original];

            UnitSnapshot snap = w.SnapshotUnit(original);
            w.Destroy(original);
            int restored = w.RestoreUnit(snap);
            Assert.True(restored >= 0);
            Assert.Equal(original, restored); // free-list reuse of the same id

            // ── Byte-identical round-trip on every authored field (these ARE the teeth: a dropped field reverts to its
            //    Create default, which differs from every captured value because CombatDef is hostile to defaults). ──
            Assert.Equal(eBaseAtk, w.BaseAttackDamage[restored].Raw);
            Assert.Equal(eEffAtk,  w.EffectiveAttackDamage[restored].Raw);
            Assert.Equal(eBaseArm, w.BaseArmor[restored].Raw);
            Assert.Equal(eEffArm,  w.EffectiveArmor[restored].Raw);
            Assert.Equal(eRange,   w.AttackRange[restored].Raw);
            Assert.Equal(eAtkSpd,  w.AttackSpeed[restored].Raw);
            Assert.Equal(eVision,  w.VisionRange[restored].Raw);
            Assert.Equal(eSplash,  w.SplashRadius[restored].Raw);
            Assert.Equal(eColl,    w.CollisionRadius[restored].Raw);
            Assert.Equal(eProjSp,  w.ProjectileSpeed[restored].Raw);
            Assert.Equal(eXp,      w.XpBounty[restored].Raw);
            Assert.Equal(eMaxEn,   w.MaxEnergy[restored].Raw);
            Assert.Equal(eEnergy,  w.Energy[restored].Raw);
            Assert.Equal(eMaxHp,   w.EffectiveMaxHealth[restored].Raw);
            Assert.Equal(eSpeed,   w.EffectiveMoveSpeed[restored].Raw);
            Assert.Equal(eDmgT,    w.DamageTypeOf[restored]);
            Assert.Equal(eArmT,    w.ArmorTypeOf[restored]);
            Assert.Equal(eSep,     w.SeparationPriorityOf[restored]);
            Assert.Equal(eCat,     w.CategoryOf[restored]);
            Assert.Equal(eDom,     w.AttackDomainOf[restored]);
            Assert.Equal(eTags,    w.TagsOf[restored]);
            Assert.Equal(eDel,     w.Delivery[restored]);
            Assert.Same(eFeedback, w.FeedbackProfile[restored]);
            Assert.Same(eDef,      w.SourceDefinition[restored]);
            Assert.Equal(eAbCount, w.AbilityCount[restored]);
            Assert.Equal(eAbId0,   w.AbilityId[restored * EntityWorld.MAX_ABILITIES_PER_UNIT + 0]);
            Assert.Equal(eAura,    w.AuraAbilityIndex[restored]);
            Assert.Equal(eOnHit,   w.OnHitAbilityIndex[restored]);
            Assert.Equal(eSelf,    w.SelfPassiveAbilityIndex[restored]);
            Assert.Equal(eFaction, w.FactionOf[restored]);
            // Caller-owned residue replayed verbatim (worker overrides + MeshType survive the mapper).
            Assert.Equal(eSupply,  w.SupplyCost[restored]);
            Assert.Equal(eMesh,    w.MeshType[restored]);
            Assert.Equal(eGather,  w.GatherState[restored]);
            Assert.Equal(eCarry,   w.CarryCapacity[restored].Raw);

            // ── Explicit teeth: prove the restored values are NOT coincidentally the Create defaults, so a silently
            //    dropped field goes RED here (belt-and-suspenders over the byte-equality above). ──
            Assert.NotEqual(Fixed.Zero.Raw,            w.BaseAttackDamage[restored].Raw);   // default 0
            Assert.NotEqual(Fixed.Zero.Raw,            w.BaseArmor[restored].Raw);          // default 0
            Assert.NotEqual(UnitCategory.Melee,        w.CategoryOf[restored]);             // default Melee
            Assert.NotEqual(SeparationPriority.Normal, w.SeparationPriorityOf[restored]);   // default Normal
            Assert.NotEqual(AttackDomain.All,          w.AttackDomainOf[restored]);         // default All
            Assert.NotEqual(UnitTag.None,              w.TagsOf[restored]);                 // default None
            Assert.NotNull(w.FeedbackProfile[restored]);                                    // default null
            Assert.NotEqual(AttackDelivery.Hitscan,    w.Delivery[restored]);               // default Hitscan
            Assert.NotEqual(ProjectileSystem.PROJECTILE_SPEED.Raw, w.ProjectileSpeed[restored].Raw); // default 18
            Assert.NotEqual(Fixed.Zero.Raw,            w.XpBounty[restored].Raw);           // default 0
            Assert.NotEqual(EntityWorld.DEFAULT_COLLISION_RADIUS.Raw, w.CollisionRadius[restored].Raw); // default 1.0
            Assert.NotEqual(Fixed.Zero.Raw,            w.MaxEnergy[restored].Raw);          // default 0
            Assert.NotEqual((byte)0,                   w.AbilityCount[restored]);           // default 0
            Assert.NotEqual(-1,                        w.AuraAbilityIndex[restored]);       // default -1
            Assert.NotEqual(-1,                        w.OnHitAbilityIndex[restored]);      // default -1
            Assert.NotEqual(-1,                        w.SelfPassiveAbilityIndex[restored]);// default -1
            // Residue teeth: SupplyCost 0 (worker override) != def.Supply (3); MeshType 5 != 0; GatherState != Inactive.
            Assert.NotEqual((byte)def.Supply,          w.SupplyCost[restored]);
            Assert.NotEqual((byte)0,                   w.MeshType[restored]);
            Assert.NotEqual(GatherState.Inactive,      w.GatherState[restored]);
        }

        // ── Story 3.17 — SourceDefinition is a NON-FOLDED reference SoA (the FeedbackProfile precedent). A recycled
        //    slot must be null-reset in Create() so a new (no-def) occupant never carries a prior occupant's def — else
        //    a later restore would re-derive a STALE unit's authored state (the SoA-recycle trap). Unfolded ⇒ this
        //    guard IS the coverage. ──
        [Fact]
        public void RecycledSlot_CarriesNoPriorSourceDefinition()
        {
            UnitDefinition def = CombatDef();

            var w = new EntityWorld();
            int first = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.ApplyUnitDefinition(first, def);
            Assert.Same(def, w.SourceDefinition[first]); // populated for the first occupant

            w.Destroy(first);
            int reused = w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(50), Fixed.FromInt(3));
            Assert.Equal(first, reused); // same id off the free list

            // The new occupant (NO def applied) must carry NO prior def — proves the mandatory Create() recycle-reset.
            Assert.Null(w.SourceDefinition[reused]);
        }

        // ── Story 3.17 — the def-less restore branch (SourceDefinition == null). A unit placed via the def-less spawn
        //    fallback has no def to route through ApplyUnitDefinition, so RestoreUnit replays the snapshot's raw combat
        //    stats + caller-owned residue (today's behavior — no regression). Covers the def-less row of the I/O matrix. ──
        [Fact]
        public void SnapshotRestore_DefLessUnit_RestoresRawStatsFromSnapshot()
        {
            var w = new EntityWorld();
            int original = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(80), Fixed.FromInt(4));
            // NO ApplyUnitDefinition ⇒ SourceDefinition stays null (a def-less spawn). Hand-set the raw combat stats +
            // caller-owned residue OFF their Create defaults, mirroring the def-less DoSpawnCombatUnit fallback branch.
            w.AttackRange[original]           = Fixed.FromInt(6);
            w.BaseAttackDamage[original]      = Fixed.FromInt(13);
            w.EffectiveAttackDamage[original] = Fixed.FromInt(13);
            w.AttackSpeed[original]           = Fixed.FromFloat(1.5f);
            w.DamageTypeOf[original]          = DamageType.Pierce;
            w.ArmorTypeOf[original]           = ArmorType.Heavy;
            w.VisionRange[original]           = Fixed.FromInt(12);
            w.SplashRadius[original]          = Fixed.FromFloat(2.5f);
            w.MeshType[original]              = 7;
            w.GatherState[original]           = GatherState.Idle;
            w.CarryCapacity[original]         = Fixed.FromInt(15);
            w.SupplyCost[original]            = 4;
            Assert.Null(w.SourceDefinition[original]); // def-less

            long eRange = w.AttackRange[original].Raw,  eDmg = w.EffectiveAttackDamage[original].Raw;
            long eSpd = w.AttackSpeed[original].Raw, eVis = w.VisionRange[original].Raw, eSplash = w.SplashRadius[original].Raw;

            UnitSnapshot snap = w.SnapshotUnit(original);
            Assert.Null(snap.Def); // def-less snapshot ⇒ RestoreUnit takes the raw-stat branch
            w.Destroy(original);
            int restored = w.RestoreUnit(snap);
            Assert.True(restored >= 0);

            // Raw combat stats replayed from the snapshot (the def-less branch), residue too.
            Assert.Equal(eRange, w.AttackRange[restored].Raw);
            Assert.Equal(eDmg,   w.BaseAttackDamage[restored].Raw);
            Assert.Equal(eDmg,   w.EffectiveAttackDamage[restored].Raw);
            Assert.Equal(eSpd,   w.AttackSpeed[restored].Raw);
            Assert.Equal(DamageType.Pierce, w.DamageTypeOf[restored]);
            Assert.Equal(ArmorType.Heavy,   w.ArmorTypeOf[restored]);
            Assert.Equal(eVis,    w.VisionRange[restored].Raw);
            Assert.Equal(eSplash, w.SplashRadius[restored].Raw);
            Assert.Equal((byte)7, w.MeshType[restored]);
            Assert.Equal(GatherState.Idle,      w.GatherState[restored]);
            Assert.Equal(Fixed.FromInt(15).Raw, w.CarryCapacity[restored].Raw);
            Assert.Equal((byte)4, w.SupplyCost[restored]);
            // Health/speed flow through Create's ctor args on the def-less branch (no explicit Base/Effective write) —
            // pin them so a future change to Create's ctor-arg handling can't silently regress def-less restore.
            Assert.Equal(Fixed.FromInt(80).Raw, w.BaseMaxHealth[restored].Raw);
            Assert.Equal(Fixed.FromInt(80).Raw, w.EffectiveMaxHealth[restored].Raw);
            Assert.Equal(Fixed.FromInt(4).Raw,  w.BaseMoveSpeed[restored].Raw);
            Assert.Null(w.SourceDefinition[restored]); // still def-less — no def fabricated on restore
        }

        // ── Story 3.17 — RestoreUnit is graceful when the world is at capacity: Create returns −1, so RestoreUnit
        //    returns −1 with no partial state. Covers the world-full error-handling row of the I/O matrix. ──
        [Fact]
        public void RestoreUnit_WhenWorldFull_ReturnsMinusOneWithoutPartialState()
        {
            var w = new EntityWorld();
            int seed = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            w.ApplyUnitDefinition(seed, CombatDef());
            UnitSnapshot snap = w.SnapshotUnit(seed);

            // Fill the world to capacity so the next Create (inside RestoreUnit) fails.
            int created = 1; // seed already took one slot
            while (w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(1)) >= 0)
                created++;
            Assert.Equal(EntityWorld.MAX_ENTITIES, created); // world is now full

            int restored = w.RestoreUnit(snap);
            Assert.Equal(-1, restored); // graceful — no slot to allocate, no partial state
        }
    }
}
