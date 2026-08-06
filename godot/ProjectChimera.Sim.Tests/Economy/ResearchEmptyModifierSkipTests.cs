#nullable enable
using System.Collections.Generic;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Economy;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Economy
{
    /// <summary>
    /// DW-678 — a research whose cumulative modifier carries NOTHING must not consume a modifier-ring slot.
    ///
    /// <para>Two shapes reach that state: a pure TECH GATE (a research whose levels author no
    /// <c>modifier_delta</c> at all — its whole value is the banked level <c>PrerequisitesMet</c> reads), and a
    /// ladder whose authored deltas NET BACK TO ZERO. Before the fix both built an all-zero <see cref="Modifier"/>
    /// and installed it into EVERY living faction unit plus every future spawn, burning 1 of the
    /// <see cref="EffectCaps.MaxModifiersPerEntity"/> slots for exactly zero effect — actively worsening the ring
    /// starvation DW-83/DW-623/DW-625 are about, and (because <c>ModifierStore</c>'s per-slot state is folded)
    /// occupying a folded slot with a descriptor that could never change an outcome.</para>
    ///
    /// <para>Deliberately separate from <c>ResearchSystemTests</c>' shared harness: this file owns a faction whose
    /// ladder nets to zero, which is a shape no other test wants in its content.</para>
    /// </summary>
    public class ResearchEmptyModifierSkipTests
    {
        // Research-list indices within Harness.Faction.Research (declaration order below).
        private const int TechGateIdx = 0;  // 1 level, NO ModifierDelta at all — the pure tech gate
        private const int SwingIdx = 1;     // 2 levels, +50 then -50 max health — the ladder that nets back to zero
        private const int ArmorUpIdx = 2;   // 1 level, +2 armor — the control that MUST still install

        private sealed class Harness
        {
            public EntityWorld World = new EntityWorld();
            public BuildingStore Buildings = new BuildingStore();
            public ResourceStore Resources = new ResourceStore(Fixed.Zero);
            public ResearchStore Research = new ResearchStore();
            public CombatEventQueue Events = new CombatEventQueue();
            public ModifierStore Modifiers = null!;
            public ResearchSystem Sys = null!;
            public FactionDefinition Faction = null!;
            public int LabId;
        }

        private static Harness Build()
        {
            var h = new Harness();
            var modSys = new ModifierSystem();
            h.Modifiers = new ModifierStore(h.World, modSys);
            modSys.AttachStore(h.Modifiers);

            h.Faction = new FactionDefinition
            {
                Id = "p1",
                Buildings = new List<BuildingDefinition>
                {
                    new BuildingDefinition { Id = "lab", AvailableResearch = new[] { "tech_gate", "hp_swing", "armor_up" } },
                },
                Research = new List<ResearchDefinition>
                {
                    new ResearchDefinition
                    {
                        // The harness's pure tech gate: cost + time, no ModifierDelta on any level. Its whole value is
                        // the banked CompletedLevels entry other research/units gate on.
                        Id = "tech_gate",
                        Prerequisites = System.Array.Empty<string>(),
                        Levels = new List<ResearchLevel>
                        {
                            new ResearchLevel { Cost = new Dictionary<string, int> { { "ore", 10 } }, TimeTicks = 1 },
                        },
                    },
                    new ResearchDefinition
                    {
                        // Authorable and validated: ResearchValidator range-checks each level's |delta| < 32768, so a
                        // ladder is free to give and then take back. After level 2 the CUMULATIVE total is zero — the
                        // level-1 instance must come OFF and nothing may replace it.
                        Id = "hp_swing",
                        Prerequisites = System.Array.Empty<string>(),
                        Levels = new List<ResearchLevel>
                        {
                            new ResearchLevel { Cost = new Dictionary<string, int> { { "ore", 10 } }, TimeTicks = 1,
                                                ModifierDelta = new ResearchModifierDelta { MaxHealthDelta = 50f } },
                            new ResearchLevel { Cost = new Dictionary<string, int> { { "ore", 10 } }, TimeTicks = 1,
                                                ModifierDelta = new ResearchModifierDelta { MaxHealthDelta = -50f } },
                        },
                    },
                    new ResearchDefinition
                    {
                        // The control: a research that DOES carry a payload must keep installing exactly as before, or
                        // the skip has over-reached.
                        Id = "armor_up",
                        Prerequisites = System.Array.Empty<string>(),
                        Levels = new List<ResearchLevel>
                        {
                            new ResearchLevel { Cost = new Dictionary<string, int> { { "ore", 10 } }, TimeTicks = 1,
                                                ModifierDelta = new ResearchModifierDelta { ArmorDelta = 2f } },
                        },
                    },
                },
            };

            h.Sys = new ResearchSystem(h.Buildings, h.Resources, h.Research, h.Modifiers, h.Events, h.Faction, null);
            h.LabId = h.Buildings.Create(FixedVec3.Zero, Faction.Player1, BuildingType.Custom, buildingId: "lab");
            h.Buildings.ConstructionTimer[h.LabId] = Fixed.Zero; // pre-built (operational)
            h.Resources.AddOre(Faction.Player1, Fixed.FromInt(1000));
            return h;
        }

        private static int Unit(Harness h) =>
            h.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));

        /// <summary>Run <paramref name="researchIndex"/> from Start to completion (every level here is 1 tick).</summary>
        private static void CompleteOneLevel(Harness h, int researchIndex, int timeTicks = 1)
        {
            Assert.True(h.Sys.StartResearchCommand(h.LabId, Faction.Player1, researchIndex),
                        "The research order must start — the empty-modifier skip changes the INSTALL, never a Start gate.");
            for (int i = 0; i < timeTicks; i++) h.Sys.Tick(h.World, Fixed.Zero);
            Assert.Equal(-1, h.Research.InProgressIndex[(int)Faction.Player1]);
        }

        /// <summary>Occupy every slot of <paramref name="id"/>'s ring with distinct permanent +1-attack modifiers —
        /// the starved unit of DW-83/DW-623, modelled with the cheapest thing that occupies a slot.</summary>
        private static void FillRing(ModifierStore store, int id)
        {
            for (int k = 0; k < EffectCaps.MaxModifiersPerEntity; k++)
                Assert.True(store.Apply(id, new Modifier(9000 + k, durationTicks: -1, StackRule.Refresh, maxStacks: 1,
                                                         Fixed.Zero, Fixed.FromInt(1), Fixed.Zero, StatusFlags.None, null, 0),
                                        id, Faction.Player1));
            Assert.Equal(EffectCaps.MaxModifiersPerEntity, store.CountAt(id));
        }

        // ── The living-army completion path ───────────────────────────────────

        [Fact]
        public void Complete_PureTechGate_BanksTheLevel_ButBurnsNoRingSlot()
        {
            var h = Build();
            int unit = Unit(h);
            Assert.Equal(0, h.Modifiers.CountAt(unit));

            CompleteOneLevel(h, TechGateIdx);

            // The unlock itself is unaffected — a tech gate's value is the banked level, and it is still banked.
            Assert.Equal(1, h.Research.CompletedLevels[(int)Faction.Player1][TechGateIdx]);
            // DW-678: and it costs the unit NOTHING. Pre-fix this was 1: an all-zero permanent modifier.
            Assert.Equal(0, h.Modifiers.CountAt(unit));
        }

        [Fact]
        public void Complete_ResearchWithAPayload_StillInstalls()
        {
            // The over-reach control: the skip must key on the BUILT modifier being empty, never on "it is research".
            var h = Build();
            int unit = Unit(h);

            CompleteOneLevel(h, ArmorUpIdx);

            Assert.Equal(1, h.Modifiers.CountAt(unit));
            Assert.Equal(ResearchSystem.ResearchModifierId(ArmorUpIdx), h.Modifiers.ModifierIdAt(unit, 0));
            Assert.Equal(Fixed.FromInt(2), h.World.EffectiveArmor[unit]);
        }

        [Fact]
        public void Complete_LadderThatNetsBackToZero_RemovesTheStaleInstance_AndInstallsNothingInItsPlace()
        {
            // The reason the REMOVE stays unconditional and above the skip: level 1 left a live, non-zero instance
            // behind. Skipping the remove along with the install would strand a +50 max-health buff on the unit for a
            // research whose cumulative total is now exactly zero.
            var h = Build();
            int unit = Unit(h);

            CompleteOneLevel(h, SwingIdx);
            Assert.Equal(1, h.Modifiers.CountAt(unit));
            Assert.Equal(Fixed.FromInt(150), h.World.EffectiveMaxHealth[unit]);
            Assert.Equal(Fixed.FromInt(100), h.World.Health[unit]); // DW-85: the ceiling grows, current Health does not

            CompleteOneLevel(h, SwingIdx); // level 2: -50 → cumulative back to zero

            Assert.Equal(2, h.Research.CompletedLevels[(int)Faction.Player1][SwingIdx]);
            Assert.Equal(Fixed.Zero, h.Research.CumulativeMaxHealthDelta[(int)Faction.Player1][SwingIdx]);
            Assert.Equal(0, h.Modifiers.CountAt(unit));                              // DW-678: pre-fix this was 1
            Assert.Equal(Fixed.FromInt(100), h.World.EffectiveMaxHealth[unit]);      // and the stale +50 really came off
            Assert.Equal(Fixed.FromInt(100), h.World.Health[unit]);
        }

        [Fact]
        public void Complete_PureTechGate_OnAFullRing_IsNotCountedAsARefusal()
        {
            // Pre-fix a starved unit "refused" the empty install, so DW-83's warn claimed an earned bonus had been
            // DROPPED when there was no bonus at all. A skipped install is not a refusal.
            var h = Build();
            int starved = Unit(h);
            FillRing(h.Modifiers, starved);
            int refusedBefore = h.Modifiers.RefusedInstallCount;

            CompleteOneLevel(h, TechGateIdx);

            Assert.Equal(1, h.Research.CompletedLevels[(int)Faction.Player1][TechGateIdx]);
            Assert.Equal(refusedBefore, h.Modifiers.RefusedInstallCount); // pre-fix: refusedBefore + 1
            Assert.Equal(EffectCaps.MaxModifiersPerEntity, h.Modifiers.CountAt(starved)); // ring untouched
        }

        // ── The future-spawn catch-up path ────────────────────────────────────

        [Fact]
        public void FutureSpawnCatchUp_PureTechGate_LeavesTheSpawnsRingEmpty()
        {
            // The catch-up hook fires for EVERY spawn (training, scenario placement, hero respawn, editor restore), so
            // an empty install here is the half that scales with army size.
            var h = Build();
            CompleteOneLevel(h, TechGateIdx); // completed with no living units

            int spawned = Unit(h);
            h.Sys.ApplyCompletedResearch(h.World, spawned);

            Assert.Equal(0, h.Modifiers.CountAt(spawned)); // pre-fix: 1
        }

        [Fact]
        public void FutureSpawnCatchUp_PureTechGate_OnAFullRing_TalliesNoRefusal()
        {
            // DW-624's per-match tally names researches whose banked, already-paid-for bonus a full ring dropped.
            // A tech gate has no bonus to drop, so it must never appear there.
            var h = Build();
            CompleteOneLevel(h, TechGateIdx);

            int spawned = Unit(h);
            FillRing(h.Modifiers, spawned);
            h.Sys.ApplyCompletedResearch(h.World, spawned);

            Assert.Equal(0, h.Sys.FlushSpawnCatchUpDiagnostics()); // pre-fix: 1
        }

        [Fact]
        public void FutureSpawnCatchUp_MixedLadder_InstallsOnlyThePayingResearch()
        {
            // Two banked researches, one empty and one not: the spawn must end up holding exactly the paying one, in
            // one slot — proving the skip is per-research and not a blanket "research installs nothing" regression.
            var h = Build();
            CompleteOneLevel(h, TechGateIdx);
            CompleteOneLevel(h, ArmorUpIdx);

            int spawned = Unit(h);
            h.Sys.ApplyCompletedResearch(h.World, spawned);

            Assert.Equal(1, h.Modifiers.CountAt(spawned)); // pre-fix: 2
            Assert.Equal(ResearchSystem.ResearchModifierId(ArmorUpIdx), h.Modifiers.ModifierIdAt(spawned, 0));
            Assert.Equal(Fixed.FromInt(2), h.World.EffectiveArmor[spawned]);
        }

        // ── The predicate itself ──────────────────────────────────────────────

        [Fact]
        public void HasNoEffect_IsTrueOnlyWhenEveryObservableChannelIsEmpty()
        {
            Assert.True(Empty().HasNoEffect());
            // Duration and period LENGTH are not observable channels on their own: a one-tick all-zero modifier is as
            // inert as a permanent one, and a period with no effect to pulse runs nothing.
            Assert.True(new Modifier(1, durationTicks: 0, StackRule.Refresh, 1, Fixed.Zero, Fixed.Zero, Fixed.Zero,
                                     StatusFlags.None, null, periodTicks: 5).HasNoEffect());

            Assert.False(With(maxHealth: Fixed.FromInt(1)).HasNoEffect());
            Assert.False(With(attack: Fixed.FromInt(-1)).HasNoEffect());
            Assert.False(With(moveSpeed: Fixed.FromRaw(1)).HasNoEffect());   // a single raw tick counts
            Assert.False(With(armor: Fixed.FromRaw(-1)).HasNoEffect());
            Assert.False(new Modifier(1, -1, StackRule.Refresh, 1, Fixed.Zero, Fixed.Zero, Fixed.Zero,
                                      StatusFlags.Stunned, null, 0).HasNoEffect());
            Assert.False(new Modifier(1, -1, StackRule.Refresh, 1, Fixed.Zero, Fixed.Zero, Fixed.Zero,
                                      StatusFlags.None, new DamageEffect(Fixed.FromInt(1), DamageType.Normal), 1).HasNoEffect());
        }

        private static Modifier Empty() =>
            new Modifier(1, -1, StackRule.Refresh, 1, Fixed.Zero, Fixed.Zero, Fixed.Zero, StatusFlags.None, null, 0);

        private static Modifier With(Fixed maxHealth = default, Fixed attack = default,
                                     Fixed moveSpeed = default, Fixed armor = default) =>
            new Modifier(1, -1, StackRule.Refresh, 1, maxHealth, attack, moveSpeed,
                         StatusFlags.None, null, 0, armor);
    }
}
