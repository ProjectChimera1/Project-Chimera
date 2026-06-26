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
        // CollisionRadius = 1.0, SeparationPriority = Normal, Category = Melee.)
        private static UnitDefinition CombatDef() => new UnitDefinition
        {
            Id = "test_combatant", DisplayName = "Test Combatant", Category = "Ranged",
            Hp = 123f, Speed = 4.25f, VisionRange = 11f, AttackRange = 6f, AttackDamage = 17f,
            AttackSpeed = 1.25f, SplashRadius = 2.5f, Supply = 3,
            DamageType = "Pierce", ArmorType = "Heavy",
            CollisionRadius = 0.5f, SeparationPriority = "Push",
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

            // Teeth: prove the mapped values are NOT coincidentally the Create defaults.
            Assert.NotEqual(Fixed.Zero.Raw,            w.BaseAttackDamage[id].Raw);    // default 0
            Assert.NotEqual(UnitCategory.Melee,        w.CategoryOf[id]);              // default Melee
            Assert.NotEqual(SeparationPriority.Normal, w.SeparationPriorityOf[id]);    // default Normal
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
        }
    }
}
