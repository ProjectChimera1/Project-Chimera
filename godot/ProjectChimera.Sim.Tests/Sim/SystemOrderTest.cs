#nullable enable
using System.Collections.Generic;
using ProjectChimera.AI;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Economy;
using ProjectChimera.Navigation;
using Xunit;

namespace ProjectChimera.Sim.Tests.Sim
{
    /// <summary>
    /// Pins the canonical 9-system tick order that <see cref="SimulationHost"/> owns (Story 1.8a / AR-6).
    /// The registration order IS the determinism contract — a desync hides in any silent reorder/add/remove —
    /// so these tests FAIL loudly the moment the order drifts. They also pin the reserved index-3 slot
    /// contract: <c>ModifierSystem</c> (Epic 2) inserts immediately before <see cref="CombatSystem"/>, so
    /// today CombatSystem must be directly preceded by <see cref="MovementSystem"/>.
    /// </summary>
    public class SystemOrderTest
    {
        /// <summary>
        /// The canonical order, by runtime type. ModifierSystem is intentionally ABSENT — it is reserved for
        /// Epic 2 at index 3 (which will shift CombatSystem from [3] to [4]); inserting it now is out of scope.
        /// </summary>
        private static readonly System.Type[] ExpectedOrder =
        {
            typeof(BuildingSystem),    // [0]
            typeof(GatheringSystem),   // [1]
            typeof(MovementSystem),    // [2]
            typeof(CombatSystem),      // [3]  ← ModifierSystem inserts HERE in Epic 2 (before Combat)
            typeof(ProjectileSystem),  // [4]
            typeof(SupplySystem),      // [5]
            typeof(FogOfWarSystem),    // [6]
            typeof(AiOpponentSystem),  // [7]
            typeof(ScenarioDirector),  // [8]  runs LAST
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
        public void Systems_AreTheNineCanonicalSystems_InExactOrder()
        {
            IReadOnlyList<ISimSystem> systems = BuildHost().Systems;

            Assert.Equal(ExpectedOrder.Length, systems.Count);
            for (int i = 0; i < ExpectedOrder.Length; i++)
                Assert.Equal(ExpectedOrder[i], systems[i].GetType());
        }

        [Fact]
        public void ReservedModifierSlot_CombatSystem_IsImmediatelyPrecededByMovementSystem()
        {
            IReadOnlyList<ISimSystem> systems = BuildHost().Systems;

            int combatIdx = -1;
            for (int i = 0; i < systems.Count; i++)
            {
                if (systems[i] is CombatSystem) { combatIdx = i; break; }
            }

            Assert.True(combatIdx > 0, "CombatSystem must be present and not first (the reserved slot sits before it).");
            Assert.IsType<MovementSystem>(systems[combatIdx - 1]);
        }
    }
}
