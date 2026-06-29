#nullable enable
using ProjectChimera.Core;              // EntityWorld, Fixed, FixedVec3, Faction
using ProjectChimera.Core.Definitions;  // UnitDefinition, AbilityRegistry, AbilityDefinition
using Xunit;

namespace ProjectChimera.Sim.Tests.Core
{
    /// <summary>
    /// Story 2.4b (Task 7.3 / AC3 / AC5) — the WIRING-CHAIN teeth-test. 2.4b makes the 2.4a cast spine reachable in
    /// a real match by (a) building an <see cref="AbilityRegistry"/> from disk, (b) calling
    /// <see cref="UnitDefinition.ResolveAbilities"/> on every loaded unit at scenario link, then (c) letting
    /// <see cref="EntityWorld.ApplyUnitDefinition"/> copy the resolved indices into the per-entity ability SoA the
    /// command card reads. If any link regresses (registry left <see cref="AbilityRegistry.Empty"/>, or the resolve
    /// step dropped), every ability silently no-casts in-game (the 2.4a code-review defer item 3) while every
    /// determinism test still passes — a green-masks-a-gap trap. These guards bite that exact failure mode:
    ///   • a unit referencing "fireball" MUST end up with AbilityCount==1 + the resolved index in AbilityId[slot 0];
    ///   • SKIPPING the resolve MUST leave AbilityCount==0 (proves the resolve step is load-bearing, not vacuous);
    ///   • an unknown ability id is dropped, never a crash.
    /// In-memory registry (hermetic; the real-file <see cref="AbilityRegistry.LoadFromDirectory"/> + JSON parse path
    /// is exercised live by /godot-verify, Task 7.1). Mirrors <c>ApplyUnitDefinitionGuardTest</c>'s resolve→apply shape.
    /// </summary>
    public class AbilityWiringTeethTest
    {
        /// <summary>A registry indexing the three shipped ability ids (sorted ascending: battle_fury=0, fireball=1, minor_heal=2).</summary>
        private static AbilityRegistry ShippedRegistry() => new AbilityRegistry(new[]
        {
            new AbilityDefinition { Id = "battle_fury" },
            new AbilityDefinition { Id = "fireball"    },
            new AbilityDefinition { Id = "minor_heal"  },
        });

        /// <summary>The alpha_faction.json mage shape (Task 2): a Ranged caster referencing "fireball" with an energy pool.</summary>
        private static UnitDefinition MageDef() => new UnitDefinition
        {
            Id = "mage", DisplayName = "Circle Savant", Category = "Ranged",
            Hp = 65f, Speed = 3.5f, MaxEnergy = 100f,
            Abilities = new[] { "fireball" },
        };

        [Fact]
        public void ResolveThenApply_PutsTheResolvedRegistryIndex_IntoTheAbilitySoA()
        {
            AbilityRegistry registry = ShippedRegistry();
            UnitDefinition def = MageDef();
            def.ResolveAbilities(registry); // the scenario-link step the 2.4b wiring runs on every loaded unit

            var w = new EntityWorld();
            int id = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromFloat(def.Hp), Fixed.FromFloat(def.Speed));
            w.ApplyUnitDefinition(id, def);

            int slot0 = id * EntityWorld.MAX_ABILITIES_PER_UNIT + 0;
            Assert.True(registry.IndexOf("fireball") >= 0);             // fireball is actually in the registry (index 1)
            Assert.Equal((byte)1, w.AbilityCount[id]);                  // one resolved ability slot
            Assert.Equal(registry.IndexOf("fireball"), w.AbilityId[slot0]); // the resolved registry index landed in the SoA
            Assert.Equal("fireball", registry.Get(w.AbilityId[slot0]).Id);  // and resolves back to the right ability (the card's read)

            // Teeth: the mapped values are NOT the Create defaults (AbilityCount 0, AbilityId -1) — a wiring slip
            // that left them at the default goes RED here.
            Assert.NotEqual((byte)0, w.AbilityCount[id]);
            Assert.NotEqual(-1,      w.AbilityId[slot0]);
        }

        [Fact]
        public void SkippingResolve_LeavesAbilityCountZero_TheSilentNoCastRegression()
        {
            // Same def, but DO NOT call ResolveAbilities — exactly what a wiring slip (forgotten resolve, or an Empty
            // registry whose IndexOf returns -1) produces. The command card then renders NO ability button and every
            // cast is a deterministic no-op. This guard documents that the resolve step is what makes it non-zero.
            UnitDefinition def = MageDef(); // AbilityIndices stays empty (never resolved)

            var w = new EntityWorld();
            int id = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromFloat(def.Hp), Fixed.FromFloat(def.Speed));
            w.ApplyUnitDefinition(id, def);

            Assert.Equal((byte)0, w.AbilityCount[id]);
            Assert.Equal(-1, w.AbilityId[id * EntityWorld.MAX_ABILITIES_PER_UNIT + 0]);
        }

        [Fact]
        public void UnknownAbilityId_IsDropped_NeverCrashes()
        {
            // A unit referencing an ability the registry does not contain resolves to FEWER slots (the id is dropped),
            // never a crash — the validator already guaranteed each registry ability is valid.
            AbilityRegistry registry = ShippedRegistry();
            UnitDefinition def = MageDef();
            def.Abilities = new[] { "no_such_ability" };
            def.ResolveAbilities(registry);

            var w = new EntityWorld();
            int id = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromFloat(def.Hp), Fixed.FromFloat(def.Speed));
            w.ApplyUnitDefinition(id, def);

            Assert.Equal((byte)0, w.AbilityCount[id]);
        }
    }
}
