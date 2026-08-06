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
    /// Pins the canonical 17-system tick order that <see cref="SimulationHost"/> owns (Story 1.8a / AR-6;
    /// DW-766 appended <see cref="DeathFeedDrainSystem"/> at index 16, past the LAST <c>DeathFeed</c> producer;
    /// <c>WinConditionSystem</c> was inserted at index 14 in Story 7.11, after <c>AiOpponentSystem</c> and immediately
    /// before <c>ScenarioDirector</c>;
    /// <c>ModifierSystem</c> filled the AR-9 slot in Story 2.2a; <c>AbilityCastSystem</c> was inserted at index 3 in
    /// Story 2.4a / FR-11, then bumped to index 4 when <c>OrderQueueSystem</c> was inserted at index 3 in Story 2.12 /
    /// FR-74; <c>ResearchSystem</c> was inserted at index 1, immediately after <c>BuildingSystem</c>, in Story 4.9,
    /// shifting every system after it down by one). The registration order IS the determinism contract — a desync
    /// hides in any silent reorder/add/remove — so these tests FAIL loudly the moment the order drifts. They also pin
    /// the queue/cast/AR-9 slot contract: <c>OrderQueueSystem</c> sits immediately after <see cref="MovementSystem"/>
    /// and immediately before <see cref="AbilityCastSystem"/>, which sits immediately before
    /// <see cref="ModifierSystem"/>, which sits immediately before <see cref="CombatSystem"/> (so an arrival is
    /// fresh, a popped cast fires same-tick, a cast's buff is recomputed, and combat reads the recomputed Effective*
    /// stats the SAME tick), all strictly before <see cref="ProjectileSystem"/>.
    /// </summary>
    public class SystemOrderTest
    {
        /// <summary>
        /// The canonical order, by runtime type. <see cref="ResearchSystem"/> occupies index 1 (Story 4.9), immediately
        /// after <see cref="BuildingSystem"/>. <see cref="AbilityCastSystem"/> occupies index 5 (Story 2.4a / FR-11)
        /// and <see cref="ModifierSystem"/> index 6 (Story 2.2a / AR-9), both immediately before
        /// <see cref="CombatSystem"/>, so a cast's buff is recomputed and combat reads the recomputed Effective* stats
        /// the same tick.
        /// </summary>
        private static readonly System.Type[] ExpectedOrder =
        {
            typeof(BuildingSystem),    // [0]
            typeof(ResearchSystem),    // [1]  ← Story 4.9 research order path, immediately after BuildingSystem
            typeof(GatheringSystem),   // [2]
            typeof(MovementSystem),    // [3]
            typeof(OrderQueueSystem),  // [4]  ← Story 2.12 shift-queue advance (FR-74), after Movement / before AbilityCast
            typeof(AbilityCastSystem), // [5]  ← Story 2.4a ability-cast spine (FR-11), immediately before ModifierSystem
            typeof(ModifierSystem),    // [6]  ← AR-9 effective-stat recompute (Story 2.2a), immediately before Combat
            typeof(CombatSystem),      // [7]
            typeof(ProjectileSystem),  // [8]
            typeof(HeroXpSystem),      // [9]  ← Story 3.13 hero XP runtime, immediately after ProjectileSystem
            typeof(ItemSystem),        // [10] ← Story 3.15 item / inventory, after the combat/projectile/hero-XP cluster
            typeof(SupplySystem),      // [11]
            typeof(FogOfWarSystem),    // [12]
            typeof(AiOpponentSystem),  // [13]
            typeof(WinConditionSystem),// [14] ← Story 7.11 win-condition evaluator, after AI / before ScenarioDirector
            typeof(ScenarioDirector),  // [15]  the LAST DeathFeed producer (run_effect graphs kill through its EffectContext)
            typeof(DeathFeedDrainSystem), // [16] ← DW-766 end-of-tick DeathFeed drain — runs LAST, past every producer
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
        public void Systems_AreTheSeventeenCanonicalSystems_InExactOrder()
        {
            IReadOnlyList<ISimSystem> systems = BuildHost().Systems;

            Assert.Equal(ExpectedOrder.Length, systems.Count);
            for (int i = 0; i < ExpectedOrder.Length; i++)
                Assert.Equal(ExpectedOrder[i], systems[i].GetType());
        }

        /// <summary>
        /// DW-766 — the drain's whole correctness argument is its POSITION: it must run strictly after every system
        /// that can reach <c>DamageResolver.KillEntity</c>, which includes <see cref="ScenarioDirector"/> (its
        /// <c>run_effect</c> graphs execute against an <c>EffectContext</c> holding the shared feed) and
        /// <see cref="ItemSystem"/> (its pickup reaches the store's ceiling-collapse kill). Pinned separately from the
        /// exact-order list above so the INTENT — "last, past every producer" — survives a future insertion that
        /// legitimately shifts the indices.
        /// </summary>
        [Fact]
        public void DeathFeedDrain_RunsLast_StrictlyAfterEveryDeathFeedProducer()
        {
            IReadOnlyList<ISimSystem> systems = BuildHost().Systems;

            int drainIdx = -1, directorIdx = -1, itemIdx = -1, heroXpIdx = -1;
            for (int i = 0; i < systems.Count; i++)
            {
                if (systems[i] is DeathFeedDrainSystem) drainIdx    = i;
                if (systems[i] is ScenarioDirector)     directorIdx = i;
                if (systems[i] is ItemSystem)           itemIdx     = i;
                if (systems[i] is HeroXpSystem)         heroXpIdx   = i;
            }

            Assert.True(drainIdx >= 0, "DeathFeedDrainSystem must be registered (DW-766 end-of-tick drain).");
            Assert.Equal(systems.Count - 1, drainIdx);          // LAST — nothing may push after it
            Assert.True(directorIdx < drainIdx, "ScenarioDirector (run_effect kills) must run before the drain.");
            Assert.True(itemIdx     < drainIdx, "ItemSystem (ceiling-collapse pickup kills) must run before the drain.");
            Assert.True(heroXpIdx   < drainIdx, "HeroXpSystem's own drain must run before the residue drain.");
        }

        [Fact]
        public void CastAndModifierSlots_AreContiguousBetweenMovementAndCombat_AndBeforeProjectile()
        {
            IReadOnlyList<ISimSystem> systems = BuildHost().Systems;

            int orderQueueIdx = -1, abilityIdx = -1, modifierIdx = -1, combatIdx = -1, movementIdx = -1, projectileIdx = -1;
            for (int i = 0; i < systems.Count; i++)
            {
                if (systems[i] is OrderQueueSystem)   orderQueueIdx = i;
                if (systems[i] is AbilityCastSystem)  abilityIdx    = i;
                if (systems[i] is ModifierSystem)     modifierIdx   = i;
                if (systems[i] is CombatSystem)       combatIdx     = i;
                if (systems[i] is MovementSystem)     movementIdx   = i;
                if (systems[i] is ProjectileSystem)   projectileIdx = i;
            }

            Assert.True(orderQueueIdx >= 0, "OrderQueueSystem must be registered (Story 2.12 shift-queue advance).");
            Assert.True(abilityIdx >= 0,  "AbilityCastSystem must be registered (Story 2.4a ability-cast spine).");
            Assert.True(modifierIdx >= 0, "ModifierSystem must be registered (AR-9 effective-stat recompute).");
            // Story 2.12 contract: OrderQueueSystem sits immediately AFTER MovementSystem (arrival detected fresh) and
            // immediately BEFORE AbilityCastSystem (a popped CastAbility fires the same tick). Story 2.4a / AR-9
            // contract: AbilityCast, Modifier, Combat are then contiguous, so a cast's buff is recomputed by
            // ModifierSystem and read by combat the SAME tick; Projectile (snapshots Effective* at spawn) runs after.
            Assert.Equal(movementIdx + 1, orderQueueIdx); // OrderQueue immediately after MovementSystem
            Assert.Equal(orderQueueIdx + 1, abilityIdx);  // AbilityCast immediately after OrderQueue
            Assert.Equal(abilityIdx + 1, modifierIdx);    // ModifierSystem immediately after AbilityCast
            Assert.Equal(combatIdx - 1, modifierIdx);     // ModifierSystem immediately before CombatSystem
            Assert.True(modifierIdx < projectileIdx, "ModifierSystem must run strictly before ProjectileSystem.");
        }
    }
}
