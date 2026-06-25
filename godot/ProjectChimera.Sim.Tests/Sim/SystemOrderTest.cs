#nullable enable
using System.Collections.Generic;
using ProjectChimera.AI;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Economy;
using ProjectChimera.Effects;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Sim
{
    /// <summary>
    /// Pins the canonical 10-system tick order that <see cref="SimulationHost"/> owns (Story 1.8a / AR-6;
    /// <c>ModifierSystem</c> filled the index-3 slot in Story 2.2a / AR-9). The registration order IS the
    /// determinism contract — a desync hides in any silent reorder/add/remove — so these tests FAIL loudly the
    /// moment the order drifts. They also pin the AR-9 slot contract: <c>ModifierSystem</c> sits immediately
    /// before <see cref="CombatSystem"/> (so combat reads recomputed Effective* stats the same tick) and
    /// immediately after <see cref="MovementSystem"/>, strictly before <see cref="ProjectileSystem"/>.
    /// </summary>
    public class SystemOrderTest
    {
        /// <summary>
        /// The canonical order, by runtime type. <see cref="ModifierSystem"/> occupies index 3 (Story 2.2a / AR-9),
        /// immediately before <see cref="CombatSystem"/>, so combat reads recomputed Effective* stats the same tick.
        /// </summary>
        private static readonly System.Type[] ExpectedOrder =
        {
            typeof(BuildingSystem),    // [0]
            typeof(GatheringSystem),   // [1]
            typeof(MovementSystem),    // [2]
            typeof(ModifierSystem),    // [3]  ← AR-9 effective-stat recompute (Story 2.2a), immediately before Combat
            typeof(CombatSystem),      // [4]
            typeof(ProjectileSystem),  // [5]
            typeof(SupplySystem),      // [6]
            typeof(FogOfWarSystem),    // [7]
            typeof(AiOpponentSystem),  // [8]
            typeof(ScenarioDirector),  // [9]  runs LAST
        };

        /// <summary>
        /// Build a host with the same non-null faction defs the goldens use, so construction is valid and
        /// representative. NullLogSink keeps it Godot-free + silent. The test never ticks — it inspects order.
        /// </summary>
        private static SimulationHost BuildHost() => SimulationHost.Create(
            NullLogSink.Instance,
            new FactionRegistry(2),
            new FactionDefinition(),
            new FactionDefinition());

        [Fact]
        public void Systems_AreTheTenCanonicalSystems_InExactOrder()
        {
            IReadOnlyList<ISimSystem> systems = BuildHost().Systems;

            Assert.Equal(ExpectedOrder.Length, systems.Count);
            for (int i = 0; i < ExpectedOrder.Length; i++)
                Assert.Equal(ExpectedOrder[i], systems[i].GetType());
        }

        [Fact]
        public void ModifierSlot_ModifierSystem_IsImmediatelyBeforeCombat_AfterMovement_AndBeforeProjectile()
        {
            IReadOnlyList<ISimSystem> systems = BuildHost().Systems;

            int modifierIdx = -1, combatIdx = -1, movementIdx = -1, projectileIdx = -1;
            for (int i = 0; i < systems.Count; i++)
            {
                if (systems[i] is ModifierSystem)   modifierIdx   = i;
                if (systems[i] is CombatSystem)     combatIdx     = i;
                if (systems[i] is MovementSystem)   movementIdx   = i;
                if (systems[i] is ProjectileSystem) projectileIdx = i;
            }

            Assert.True(modifierIdx >= 0, "ModifierSystem must be registered (AR-9 effective-stat recompute).");
            // AR-9 contract: Movement, Modifier, Combat are contiguous, so combat reads recomputed Effective* the
            // same tick a modifier changes them; Projectile (which snapshots Effective* at spawn) runs strictly after.
            Assert.Equal(combatIdx - 1, modifierIdx);    // immediately before CombatSystem
            Assert.Equal(movementIdx + 1, modifierIdx);  // immediately after MovementSystem
            Assert.True(modifierIdx < projectileIdx, "ModifierSystem must run strictly before ProjectileSystem.");
        }
    }
}
