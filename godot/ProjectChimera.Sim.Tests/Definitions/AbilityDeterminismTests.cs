#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 2.3 AC1 — the two-run determinism proof (a unit test, NO persisted golden artifact, NO AlgoVersion
    /// change). A JSON ability is loaded → compiled → executed against two identical fresh worlds; the real
    /// <see cref="SimChecksum.Compute"/> is byte-identical across the two runs. A structurally DIFFERENT ability
    /// yields a DIFFERENT hash (the negative control proving the test isn't vacuous).
    /// </summary>
    public class AbilityDeterminismTests
    {
        // damage 25 (Normal) then heal 10 to the target → a deterministic, self-contained HP mutation.
        private const string AbilityA = """
        {
          "id": "a", "targeting": "TargetUnit",
          "effect": { "kind": "sequence", "children": [
            { "kind": "damage", "amount": 25, "damage_type": "Normal" },
            { "kind": "heal", "amount": 10 }
          ] }
        }
        """;

        // Structurally different: damage 50 instead of 25 → different post-execution HP → different hash.
        private const string AbilityB = """
        {
          "id": "b", "targeting": "TargetUnit",
          "effect": { "kind": "sequence", "children": [
            { "kind": "damage", "amount": 50, "damage_type": "Normal" },
            { "kind": "heal", "amount": 10 }
          ] }
        }
        """;

        private static uint RunAndHash(string abilityJson)
        {
            AbilityValidationResult r = AbilityLoader.Load(abilityJson, "test");
            Assert.True(r.Ok, r.Error);
            EffectNode graph = r.Value.Value.EffectGraph!;

            var w = new EntityWorld();
            int caster = w.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            int target = w.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.Zero),
                                   Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3));

            var store = new ModifierStore(w);
            var ex = new EffectExecutor();
            var ctx = new EffectContext(w, caster, target, Faction.Player1, DamageTable.Default,
                                        spatial: null, events: null, stats: null, modifierStore: store);
            ex.Run(graph, in ctx);

            // The dormant-state Compute (2.2b 5-arg signature); the empty store hashes consistently.
            return SimChecksum.Compute(w, new BuildingStore(), new ResourceStore(Fixed.Zero),
                                       new FactionRegistry(2), store);
        }

        [Fact]
        public void IdenticalAbilityAndWorld_ProduceByteIdenticalChecksum()
        {
            uint a1 = RunAndHash(AbilityA);
            uint a2 = RunAndHash(AbilityA);
            Assert.Equal(a1, a2);
        }

        [Fact]
        public void StructurallyDifferentAbility_ProducesDifferentChecksum()
        {
            // Non-vacuity: if the graph weren't actually executed, both would hash the untouched world identically.
            Assert.NotEqual(RunAndHash(AbilityA), RunAndHash(AbilityB));
        }
    }
}
