#nullable enable
using System.Collections.Generic;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Economy;
using ProjectChimera.Effects;
using ProjectChimera.Multiplayer;
using Xunit;

namespace ProjectChimera.Sim.Tests.Economy
{
    /// <summary>
    /// Story 4.9 — Godot-free oracles for <see cref="ResearchSystem"/>'s order path: every I/O-matrix row (start/
    /// complete/cancel, every Start guard, future-spawn catch-up, a repeatable ladder) plus a live-vs-OrderApplier
    /// parity check (StartResearch/CancelResearch ride the shared applier). Mirrors <c>BuyItemCommandTests</c>'
    /// harness conventions.
    /// </summary>
    public class ResearchSystemTests
    {
        // Research-list indices within Harness.Faction.Research (declaration order below).
        private const int ArmorUpIdx = 0; // 2 levels, no prerequisites — the happy-path / repeatable ladder
        private const int AdvancedIdx = 1; // 1 level, Prerequisites = ["armor_up"] (a RESEARCH prerequisite)
        private const int GatedIdx = 2;    // 1 level, Prerequisites = ["req_building"] (a BUILDING prerequisite)
        private const int OverflowIdx = 3; // 2 levels, each ArmorDelta individually valid but summing past the Fixed ceiling
        private const int HpUpIdx = 4;     // 2 levels, each +50 MaxHealth — DW-85 heal-suppression coverage
        private const int HpDownIdx = 5;   // 1 level, -50 MaxHealth (authorable) — DW-85 down-clamp branch coverage

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
            public int P2LabId; // only set by BuildTwoFaction
        }

        private static Harness Build(int oreBalance = 1000)
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
                    new BuildingDefinition { Id = "lab", AvailableResearch = new[] { "armor_up", "advanced", "gated", "overflow_test", "hp_up", "hp_down" } },
                    new BuildingDefinition { Id = "no_research_shop", AvailableResearch = System.Array.Empty<string>() },
                },
                Research = new List<ResearchDefinition>
                {
                    new ResearchDefinition
                    {
                        Id = "armor_up",
                        CancelRefundFraction = 0.5f,
                        Prerequisites = System.Array.Empty<string>(),
                        Levels = new List<ResearchLevel>
                        {
                            new ResearchLevel { Cost = new Dictionary<string, int> { { "ore", 100 } }, TimeTicks = 3,
                                                 ModifierDelta = new ResearchModifierDelta { ArmorDelta = 2f } },
                            new ResearchLevel { Cost = new Dictionary<string, int> { { "ore", 200 } }, TimeTicks = 2,
                                                 ModifierDelta = new ResearchModifierDelta { ArmorDelta = 3f } },
                        },
                    },
                    new ResearchDefinition
                    {
                        Id = "advanced",
                        Prerequisites = new[] { "armor_up" },
                        Levels = new List<ResearchLevel>
                        {
                            new ResearchLevel { Cost = new Dictionary<string, int> { { "ore", 50 } }, TimeTicks = 1,
                                                 ModifierDelta = new ResearchModifierDelta { AttackDamageDelta = 5f } },
                        },
                    },
                    new ResearchDefinition
                    {
                        Id = "gated",
                        Prerequisites = new[] { "req_building" },
                        Levels = new List<ResearchLevel>
                        {
                            new ResearchLevel { Cost = new Dictionary<string, int> { { "ore", 10 } }, TimeTicks = 1 },
                        },
                    },
                    new ResearchDefinition
                    {
                        // Each level's ArmorDelta (20000) individually passes 4.8's ResearchValidator's per-level
                        // ±32768 range check, but the CUMULATIVE sum (40000) exceeds the Fixed ceiling — exercises
                        // CompleteResearch's SaturatingAdd guard (review finding [medium] 2).
                        Id = "overflow_test",
                        Prerequisites = System.Array.Empty<string>(),
                        Levels = new List<ResearchLevel>
                        {
                            new ResearchLevel { Cost = new Dictionary<string, int> { { "ore", 1 } }, TimeTicks = 1,
                                                 ModifierDelta = new ResearchModifierDelta { ArmorDelta = 20000f } },
                            new ResearchLevel { Cost = new Dictionary<string, int> { { "ore", 1 } }, TimeTicks = 1,
                                                 ModifierDelta = new ResearchModifierDelta { ArmorDelta = 20000f } },
                        },
                    },
                    new ResearchDefinition
                    {
                        // DW-85: a repeatable +MaxHealth ladder. Completing a level must raise the ceiling on living
                        // faction units WITHOUT burst-healing their current Health (the living-army heal exploit).
                        Id = "hp_up",
                        Prerequisites = System.Array.Empty<string>(),
                        Levels = new List<ResearchLevel>
                        {
                            new ResearchLevel { Cost = new Dictionary<string, int> { { "ore", 10 } }, TimeTicks = 1,
                                                 ModifierDelta = new ResearchModifierDelta { MaxHealthDelta = 50f } },
                            new ResearchLevel { Cost = new Dictionary<string, int> { { "ore", 10 } }, TimeTicks = 1,
                                                 ModifierDelta = new ResearchModifierDelta { MaxHealthDelta = 50f } },
                        },
                    },
                    new ResearchDefinition
                    {
                        // DW-85 (review): a NET-NEGATIVE max_health research is authorable (ResearchValidator rejects
                        // only |delta| >= 32768). Completing it on the living-army path must clamp current Health DOWN
                        // to the reduced ceiling — exercises the down-clamp branch of the preserve-health restore that
                        // every positive-delta test leaves as a pass-through.
                        Id = "hp_down",
                        Prerequisites = System.Array.Empty<string>(),
                        Levels = new List<ResearchLevel>
                        {
                            new ResearchLevel { Cost = new Dictionary<string, int> { { "ore", 10 } }, TimeTicks = 1,
                                                 ModifierDelta = new ResearchModifierDelta { MaxHealthDelta = -50f } },
                        },
                    },
                },
            };

            h.Sys = new ResearchSystem(h.Buildings, h.Resources, h.Research, h.Modifiers, h.Events, h.Faction, null);
            h.LabId = h.Buildings.Create(FixedVec3.Zero, Faction.Player1, BuildingType.Custom,
                                         buildingId: "lab");
            h.Buildings.ConstructionTimer[h.LabId] = Fixed.Zero; // pre-built (operational)
            h.Resources.AddOre(Faction.Player1, Fixed.FromInt(oreBalance));
            return h;
        }

        /// <summary>Two-faction harness (Story 4.9 review finding [medium] 4 — cross-faction isolation coverage):
        /// Player1 AND Player2 share the SAME authored faction content (mirrors production's shared-faction-json
        /// setup), each with its OWN lab building, so a per-faction command/completion can be exercised on either
        /// side while asserting the OTHER faction's <see cref="ResearchStore"/> row and units stay untouched.</summary>
        private static Harness BuildTwoFaction(int oreBalance = 1000)
        {
            var h = Build(oreBalance);
            h.Sys = new ResearchSystem(h.Buildings, h.Resources, h.Research, h.Modifiers, h.Events, h.Faction, h.Faction);
            h.P2LabId = h.Buildings.Create(FixedVec3.Zero, Faction.Player2, BuildingType.Custom, buildingId: "lab");
            h.Buildings.ConstructionTimer[h.P2LabId] = Fixed.Zero;
            h.Resources.AddOre(Faction.Player2, Fixed.FromInt(oreBalance));
            return h;
        }

        private static int Ore(Harness h) => h.Resources.Ore[(int)Faction.Player1].ToInt();
        private static int OreP2(Harness h) => h.Resources.Ore[(int)Faction.Player2].ToInt();

        // ── Happy path start ─────────────────────────────────────────────────
        [Fact]
        public void Start_HappyPath_SpendsCostAndBeginsCountdown()
        {
            var h = Build();
            int oreBefore = Ore(h);

            bool ok = h.Sys.StartResearchCommand(h.LabId, Faction.Player1, ArmorUpIdx);

            Assert.True(ok);
            Assert.Equal(oreBefore - 100, Ore(h));
            Assert.Equal(ArmorUpIdx, h.Research.InProgressIndex[(int)Faction.Player1]);
            Assert.Equal(3, h.Research.RemainingTicks[(int)Faction.Player1]);
        }

        // ── Happy path complete ──────────────────────────────────────────────
        [Fact]
        public void Complete_AppliesModifierToAliveUnits_PushesEvent_ReturnsToIdle()
        {
            var h = Build();
            int unit = h.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));

            Assert.True(h.Sys.StartResearchCommand(h.LabId, Faction.Player1, ArmorUpIdx));
            for (int i = 0; i < 3; i++) h.Sys.Tick(h.World, Fixed.Zero);

            Assert.Equal(-1, h.Research.InProgressIndex[(int)Faction.Player1]);
            Assert.Equal(1, h.Research.CompletedLevels[(int)Faction.Player1][ArmorUpIdx]);
            Assert.Equal(Fixed.FromInt(2), h.World.EffectiveArmor[unit]);

            bool sawComplete = false;
            for (int i = 0; i < h.Events.Count; i++)
                if (h.Events.Get(i).Type == CombatEventType.ResearchComplete) sawComplete = true;
            Assert.True(sawComplete);
        }

        // ── Future-spawn catch-up ────────────────────────────────────────────
        [Fact]
        public void FutureSpawn_ReceivesCumulativeModifier_ViaApplyCompletedResearch()
        {
            var h = Build();
            Assert.True(h.Sys.StartResearchCommand(h.LabId, Faction.Player1, ArmorUpIdx));
            for (int i = 0; i < 3; i++) h.Sys.Tick(h.World, Fixed.Zero);

            // A unit spawned AFTER completion never goes through ApplyUnitDefinition in this Godot-free harness (no
            // UnitDefinition-driven spawn path here), so call the catch-up hook directly — exactly what
            // SimulationHost wires to World.OnUnitDefinitionApplied.
            int newUnit = h.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            h.Sys.ApplyCompletedResearch(h.World, newUnit);

            Assert.Equal(Fixed.FromInt(2), h.World.EffectiveArmor[newUnit]);
        }

        // ── Cancel with refund ───────────────────────────────────────────────
        [Fact]
        public void Cancel_InProgress_RefundsFractionAndClearsState()
        {
            var h = Build();
            Assert.True(h.Sys.StartResearchCommand(h.LabId, Faction.Player1, ArmorUpIdx));
            int oreAfterStart = Ore(h);

            bool ok = h.Sys.CancelResearchCommand(h.LabId, Faction.Player1);

            Assert.True(ok);
            Assert.Equal(oreAfterStart + 50, Ore(h)); // 0.5 * 100
            Assert.Equal(-1, h.Research.InProgressIndex[(int)Faction.Player1]);
        }

        // ── Cancel when idle ─────────────────────────────────────────────────
        [Fact]
        public void Cancel_WhenIdle_IsSilentNoOp()
        {
            var h = Build();
            int oreBefore = Ore(h);

            bool ok = h.Sys.CancelResearchCommand(h.LabId, Faction.Player1);

            Assert.False(ok);
            Assert.Equal(oreBefore, Ore(h));
            Assert.Equal(0, h.Events.Count);
        }

        // ── Start while already in progress ──────────────────────────────────
        [Fact]
        public void Start_AlreadyInProgress_DeniesAndSpendsNothing()
        {
            var h = Build();
            Assert.True(h.Sys.StartResearchCommand(h.LabId, Faction.Player1, ArmorUpIdx));
            int oreAfterFirst = Ore(h);

            bool ok = h.Sys.StartResearchCommand(h.LabId, Faction.Player1, AdvancedIdx);

            Assert.False(ok);
            Assert.Equal(oreAfterFirst, Ore(h));
            Assert.True(HasOrderDenied(h));
        }

        // ── Start at max level ───────────────────────────────────────────────
        [Fact]
        public void Start_AtMaxLevel_DeniesAndSpendsNothing()
        {
            var h = Build();
            h.Research.CompletedLevels[(int)Faction.Player1][ArmorUpIdx] = 2; // both levels already completed
            int oreBefore = Ore(h);

            bool ok = h.Sys.StartResearchCommand(h.LabId, Faction.Player1, ArmorUpIdx);

            Assert.False(ok);
            Assert.Equal(oreBefore, Ore(h));
            Assert.True(HasOrderDenied(h));
        }

        // ── Start with unmet prerequisites (research prerequisite) ──────────
        [Fact]
        public void Start_UnmetResearchPrerequisite_DeniesAndSpendsNothing()
        {
            var h = Build();
            int oreBefore = Ore(h);

            bool ok = h.Sys.StartResearchCommand(h.LabId, Faction.Player1, AdvancedIdx); // armor_up not completed yet

            Assert.False(ok);
            Assert.Equal(oreBefore, Ore(h));
            Assert.True(HasOrderDenied(h));
        }

        // ── Start with unmet prerequisites (building prerequisite) ──────────
        [Fact]
        public void Start_UnmetBuildingPrerequisite_DeniesAndSpendsNothing()
        {
            var h = Build();
            int oreBefore = Ore(h);

            bool ok = h.Sys.StartResearchCommand(h.LabId, Faction.Player1, GatedIdx); // req_building not built

            Assert.False(ok);
            Assert.Equal(oreBefore, Ore(h));
            Assert.True(HasOrderDenied(h));
        }

        // ── Start with a met building prerequisite passes ────────────────────
        [Fact]
        public void Start_MetBuildingPrerequisite_Succeeds()
        {
            var h = Build();
            int req = h.Buildings.Create(FixedVec3.Zero, Faction.Player1, BuildingType.Custom, buildingId: "req_building");
            h.Buildings.ConstructionTimer[req] = Fixed.Zero;
            int oreBefore = Ore(h);

            bool ok = h.Sys.StartResearchCommand(h.LabId, Faction.Player1, GatedIdx);

            Assert.True(ok);
            Assert.Equal(oreBefore - 10, Ore(h)); // "gated"'s level-0 cost — same resource-balance-delta convention as every other "succeeds" test
        }

        // ── Start not offered at this building ───────────────────────────────
        [Fact]
        public void Start_NotOfferedAtBuilding_DeniesAndSpendsNothing()
        {
            var h = Build();
            int shop = h.Buildings.Create(FixedVec3.Zero, Faction.Player1, BuildingType.Custom, buildingId: "no_research_shop");
            h.Buildings.ConstructionTimer[shop] = Fixed.Zero;
            int oreBefore = Ore(h);

            bool ok = h.Sys.StartResearchCommand(shop, Faction.Player1, ArmorUpIdx);

            Assert.False(ok);
            Assert.Equal(oreBefore, Ore(h));
            Assert.True(HasOrderDenied(h));
        }

        // ── Start insufficient resources ─────────────────────────────────────
        [Fact]
        public void Start_Unaffordable_DeniesAndSpendsNothing()
        {
            var h = Build(oreBalance: 10); // < 100 cost
            int oreBefore = Ore(h);

            bool ok = h.Sys.StartResearchCommand(h.LabId, Faction.Player1, ArmorUpIdx);

            Assert.False(ok);
            Assert.Equal(oreBefore, Ore(h));
            Assert.True(HasOrderDenied(h));
        }

        // ── Start at a building still under construction (review finding [high] 3) ──────
        [Fact]
        public void Start_BuildingUnderConstruction_SilentNoOpNoEvent()
        {
            var h = Build();
            h.Buildings.ConstructionTimer[h.LabId] = Fixed.FromInt(5); // still constructing
            int oreBefore = Ore(h);

            bool ok = h.Sys.StartResearchCommand(h.LabId, Faction.Player1, ArmorUpIdx);

            Assert.False(ok);
            Assert.Equal(oreBefore, Ore(h));
            Assert.Equal(0, h.Events.Count); // silent — matches TrainUnit's guard-parity convention, no OrderDenied
        }

        // ── Start/Cancel building not owned ──────────────────────────────────
        [Fact]
        public void Start_BuildingNotOwned_SilentNoOpNoEvent()
        {
            var h = Build();
            int oreBefore = Ore(h);

            bool ok = h.Sys.StartResearchCommand(h.LabId, Faction.Player2, ArmorUpIdx);

            Assert.False(ok);
            Assert.Equal(oreBefore, Ore(h));
            Assert.Equal(0, h.Events.Count); // silent — no OrderDenied leak on a crafted cross-faction id
        }

        [Fact]
        public void Cancel_BuildingNotOwned_SilentNoOpNoEvent()
        {
            var h = Build();
            Assert.True(h.Sys.StartResearchCommand(h.LabId, Faction.Player1, ArmorUpIdx));
            int oreAfterStart = Ore(h);

            bool ok = h.Sys.CancelResearchCommand(h.LabId, Faction.Player2);

            Assert.False(ok);
            Assert.Equal(oreAfterStart, Ore(h));
            Assert.Equal(0, h.Events.Count);
        }

        // ── Cancel from a DIFFERENT owned building (review finding [low] 8) ─────────────
        // Proves the doc comment's "any owned building may cancel — research state is faction-wide, not
        // building-scoped" claim actually holds: every other Cancel_* test cancels from the SAME building that
        // started the order.
        [Fact]
        public void Cancel_FromDifferentOwnedBuilding_SucceedsWithRefund()
        {
            var h = Build();
            int otherBuilding = h.Buildings.Create(FixedVec3.Zero, Faction.Player1, BuildingType.Custom, buildingId: "req_building");
            h.Buildings.ConstructionTimer[otherBuilding] = Fixed.Zero;

            Assert.True(h.Sys.StartResearchCommand(h.LabId, Faction.Player1, ArmorUpIdx)); // started at the lab
            int oreAfterStart = Ore(h);

            bool ok = h.Sys.CancelResearchCommand(otherBuilding, Faction.Player1); // cancelled from a DIFFERENT owned building

            Assert.True(ok);
            Assert.Equal(oreAfterStart + 50, Ore(h)); // 0.5 * 100 — same refund as cancelling from the starting building
            Assert.Equal(-1, h.Research.InProgressIndex[(int)Faction.Player1]);
        }

        // ── Repeatable re-research ────────────────────────────────────────────
        [Fact]
        public void RepeatableLadder_NextStartUsesLevelOnesOwnCostAndTime()
        {
            var h = Build();
            Assert.True(h.Sys.StartResearchCommand(h.LabId, Faction.Player1, ArmorUpIdx));
            for (int i = 0; i < 3; i++) h.Sys.Tick(h.World, Fixed.Zero); // completes level 0
            int oreAfterLevel0 = Ore(h);

            bool ok = h.Sys.StartResearchCommand(h.LabId, Faction.Player1, ArmorUpIdx);
            Assert.True(ok);
            Assert.Equal(oreAfterLevel0 - 200, Ore(h)); // level 1's OWN cost, not level 0's
            Assert.Equal(2, h.Research.RemainingTicks[(int)Faction.Player1]); // level 1's OWN time

            int unit = h.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            h.Sys.ApplyCompletedResearch(h.World, unit); // only level 0 completed so far
            Assert.Equal(Fixed.FromInt(2), h.World.EffectiveArmor[unit]);

            for (int i = 0; i < 2; i++) h.Sys.Tick(h.World, Fixed.Zero); // completes level 1
            Assert.Equal(2, h.Research.CompletedLevels[(int)Faction.Player1][ArmorUpIdx]);
            // Cumulative delta is now level0 (2) + level1 (3) = 5.
            Assert.Equal(Fixed.FromInt(5), h.World.EffectiveArmor[unit]);
        }

        // ── DW-85: +MaxHealth research must NOT burst-heal the living army ─────────────────────────
        // Matrix row 1: a damaged living unit whose faction completes a positive-MaxHealth research level gets the
        // raised ceiling but keeps its current Health (no heal on the remove-then-reapply of the cumulative slot).
        [Fact]
        public void Complete_HpUp_DamagedUnit_RaisesCeilingButDoesNotHeal()
        {
            var h = Build();
            int unit = h.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            h.World.Health[unit] = Fixed.FromInt(10); // damaged: 10/100

            Assert.True(h.Sys.StartResearchCommand(h.LabId, Faction.Player1, HpUpIdx));
            h.Sys.Tick(h.World, Fixed.Zero); // TimeTicks=1 → level 0 (+50 max) completes

            Assert.Equal(Fixed.FromInt(150), h.World.EffectiveMaxHealth[unit]);
            Assert.Equal(Fixed.FromInt(10), h.World.Health[unit]); // NOT healed by +50
        }

        // Matrix row 2: a repeatable +MaxHealth ladder must not re-heal by the FULL cumulative on every completion.
        // After level 0 the unit is 10/150; completing level 1 raises the ceiling to 200 but current Health stays 10
        // (the pre-DW-85 bug jumped it to 110 — healed the whole +100 cumulative).
        [Fact]
        public void Complete_HpUp_Repeatable_DoesNotReHealFullCumulative()
        {
            var h = Build();
            int unit = h.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            h.World.Health[unit] = Fixed.FromInt(10);

            Assert.True(h.Sys.StartResearchCommand(h.LabId, Faction.Player1, HpUpIdx));
            h.Sys.Tick(h.World, Fixed.Zero); // level 0 completes → 10/150
            Assert.Equal(Fixed.FromInt(150), h.World.EffectiveMaxHealth[unit]);
            Assert.Equal(Fixed.FromInt(10), h.World.Health[unit]);

            Assert.True(h.Sys.StartResearchCommand(h.LabId, Faction.Player1, HpUpIdx));
            h.Sys.Tick(h.World, Fixed.Zero); // level 1 completes → ceiling 200

            Assert.Equal(Fixed.FromInt(200), h.World.EffectiveMaxHealth[unit]);
            Assert.Equal(Fixed.FromInt(10), h.World.Health[unit]); // still 10 — NOT +100 healed
        }

        // Matrix row 3: a full-health unit is not topped up to the new ceiling — it stays at its old current Health.
        [Fact]
        public void Complete_HpUp_FullHealthUnit_IsNotToppedUp()
        {
            var h = Build();
            int unit = h.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3)); // 100/100

            Assert.True(h.Sys.StartResearchCommand(h.LabId, Faction.Player1, HpUpIdx));
            h.Sys.Tick(h.World, Fixed.Zero); // level 0 completes

            Assert.Equal(Fixed.FromInt(150), h.World.EffectiveMaxHealth[unit]);
            Assert.Equal(Fixed.FromInt(100), h.World.Health[unit]); // now 100/150, NOT topped to 150
        }

        // Matrix row 4: the future-spawn catch-up path STILL heals — a unit trained AFTER completion spawns at full
        // upgraded HP (both raised ceiling and full current Health). DW-85 suppresses ONLY the living-army path.
        [Fact]
        public void FutureSpawn_HpUp_StillHealsToFullUpgradedHealth()
        {
            var h = Build();
            Assert.True(h.Sys.StartResearchCommand(h.LabId, Faction.Player1, HpUpIdx));
            h.Sys.Tick(h.World, Fixed.Zero); // level 0 completes (no units alive)

            int newUnit = h.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3)); // 100/100
            h.Sys.ApplyCompletedResearch(h.World, newUnit); // future-spawn catch-up (preserveCurrentHealth: false)

            Assert.Equal(Fixed.FromInt(150), h.World.EffectiveMaxHealth[newUnit]);
            Assert.Equal(Fixed.FromInt(150), h.World.Health[newUnit]); // full upgraded HP — catch-up heal preserved
        }

        // Matrix row 5 (regression guard): an armor-only completion carries maxHealthChange==0, so the snapshot/restore
        // is a no-op — a damaged unit's current Health is left exactly as-is (no incidental heal or clamp).
        [Fact]
        public void Complete_ArmorOnly_LeavesDamagedHealthUntouched()
        {
            var h = Build();
            int unit = h.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            h.World.Health[unit] = Fixed.FromInt(10); // damaged

            Assert.True(h.Sys.StartResearchCommand(h.LabId, Faction.Player1, ArmorUpIdx));
            for (int i = 0; i < 3; i++) h.Sys.Tick(h.World, Fixed.Zero); // armor_up level 0 (+2 armor) completes

            Assert.Equal(Fixed.FromInt(2), h.World.EffectiveArmor[unit]);
            Assert.Equal(Fixed.FromInt(10), h.World.Health[unit]);       // Health untouched by a non-MaxHealth research
            Assert.Equal(Fixed.FromInt(100), h.World.EffectiveMaxHealth[unit]); // ceiling unchanged
        }

        // Review (down-clamp branch): a NET-NEGATIVE +MaxHealth research LOWERS the ceiling on the living-army path.
        // The preserve-health restore must clamp current Health DOWN to the reduced EffectiveMaxHealth — proving the
        // restore respects the ceiling (a bare `Health = healthBefore` would leave Health above max, breaking the
        // Health <= EffectiveMaxHealth invariant). Every positive-delta test leaves this branch a pass-through.
        [Fact]
        public void Complete_HpDown_FullHealthUnit_ClampsHealthDownToReducedCeiling()
        {
            var h = Build();
            int unit = h.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3)); // 100/100

            Assert.True(h.Sys.StartResearchCommand(h.LabId, Faction.Player1, HpDownIdx));
            h.Sys.Tick(h.World, Fixed.Zero); // -50 max completes → ceiling drops to 50

            Assert.Equal(Fixed.FromInt(50), h.World.EffectiveMaxHealth[unit]);
            Assert.Equal(Fixed.FromInt(50), h.World.Health[unit]); // clamped DOWN from 100 to the new ceiling, not left at 100
        }

        // ── Parity: StartResearch/CancelResearch through the shared OrderApplier ────
        [Fact]
        public void Start_ThroughOrderApplier_ExecutesLikeDirectCommand()
        {
            var h = Build();
            int oreBefore = Ore(h);

            var order = new UnitOrder(h.LabId, UnitCommand.StartResearch, Fixed.FromRaw(ArmorUpIdx), Fixed.Zero);
            OrderApplier.Apply(h.World, in order, Faction.Player1, research: h.Sys, events: h.Events);

            Assert.Equal(oreBefore - 100, Ore(h));
            Assert.Equal(ArmorUpIdx, h.Research.InProgressIndex[(int)Faction.Player1]);
        }

        [Fact]
        public void Cancel_ThroughOrderApplier_ExecutesLikeDirectCommand()
        {
            var h = Build();
            Assert.True(h.Sys.StartResearchCommand(h.LabId, Faction.Player1, ArmorUpIdx));
            int oreAfterStart = Ore(h);

            var order = new UnitOrder(h.LabId, UnitCommand.CancelResearch, Fixed.Zero, Fixed.Zero);
            OrderApplier.Apply(h.World, in order, Faction.Player1, research: h.Sys, events: h.Events);

            Assert.Equal(oreAfterStart + 50, Ore(h));
            Assert.Equal(-1, h.Research.InProgressIndex[(int)Faction.Player1]);
        }

        // Follow-up review: the two parity tests above both use ArmorUpIdx=0, so a mis-sourced/quantized decode of
        // the raw-int TargetX wire field (o.TargetX — NEVER via .ToFloat()) would still yield 0 and pass vacuously.
        // Drive a NONZERO research index through the applier so the wire decode is genuinely exercised.
        [Fact]
        public void Start_ThroughOrderApplier_NonZeroIndex_DecodesTargetXToTheChosenResearch()
        {
            var h = Build();
            var order = new UnitOrder(h.LabId, UnitCommand.StartResearch, Fixed.FromRaw(OverflowIdx), Fixed.Zero);
            OrderApplier.Apply(h.World, in order, Faction.Player1, research: h.Sys, events: h.Events);

            // The command routed to OverflowIdx (3), not index 0 — proves TargetX carries the exact research index.
            Assert.Equal(OverflowIdx, h.Research.InProgressIndex[(int)Faction.Player1]);
        }

        // ── Cumulative delta overflow saturates instead of wrapping (review finding [medium] 2) ─────
        [Fact]
        public void Complete_CumulativeDeltaOverflow_SaturatesInsteadOfWrapping()
        {
            var h = Build();
            Assert.True(h.Sys.StartResearchCommand(h.LabId, Faction.Player1, OverflowIdx));
            h.Sys.Tick(h.World, Fixed.Zero); // level 0 completes (TimeTicks=1) — cumulative ArmorDelta = 20000

            Assert.True(h.Sys.StartResearchCommand(h.LabId, Faction.Player1, OverflowIdx));
            h.Sys.Tick(h.World, Fixed.Zero); // level 1 completes — cumulative would be 40000, past the ±32768 Fixed ceiling

            Assert.Equal(2, h.Research.CompletedLevels[(int)Faction.Player1][OverflowIdx]);
            // Saturated at the representable ceiling, NOT wrapped into a nonsensical/negative value.
            Assert.Equal(Fixed.MaxValue, h.Research.CumulativeArmorDelta[(int)Faction.Player1][OverflowIdx]);
            Assert.True(h.Research.CumulativeArmorDelta[(int)Faction.Player1][OverflowIdx] > Fixed.Zero);
        }

        // ── Null FactionDefinition.Research never NREs (review finding [high] 1) ────────────────────
        // FactionDefinition.GetResearch/IndexOfResearch and ResearchValidator all explicitly tolerate an authored
        // "research": null faction file (load succeeds, treated as empty) — ResearchSystem must match that
        // tolerance across every direct fdef.Research access, never NRE.
        [Fact]
        public void NullResearchList_NeverThrows_AcrossStartCompleteAndFutureSpawn()
        {
            var nullResearchFaction = new FactionDefinition
            {
                Id = "null_research",
                Buildings = new List<BuildingDefinition>
                {
                    new BuildingDefinition { Id = "lab", AvailableResearch = new[] { "armor_up" } },
                },
                Research = null!, // malformed-but-tolerated JSON "research": null
            };

            var world = new EntityWorld();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            var research = new ResearchStore();
            var modSys = new ModifierSystem();
            var modifiers = new ModifierStore(world, modSys);
            modSys.AttachStore(modifiers);
            var events = new CombatEventQueue();

            // Construction itself must not NRE (ctor lines ~62-63).
            var sys = new ResearchSystem(buildings, resources, research, modifiers, events, nullResearchFaction, null);

            int lab = buildings.Create(FixedVec3.Zero, Faction.Player1, BuildingType.Custom, buildingId: "lab");
            buildings.ConstructionTimer[lab] = Fixed.Zero;
            resources.AddOre(Faction.Player1, Fixed.FromInt(1000));

            // StartResearchCommand must not NRE — denies cleanly (no research to start).
            bool started = sys.StartResearchCommand(lab, Faction.Player1, 0);
            Assert.False(started);

            // CancelResearchCommand must not NRE either (idle no-op).
            Assert.False(sys.CancelResearchCommand(lab, Faction.Player1));

            // ApplyCompletedResearch (future-spawn catch-up) must not NRE.
            int unit = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            sys.ApplyCompletedResearch(world, unit); // no-op — nothing completed, nothing to apply

            // A tick with nothing in progress must not NRE either (CompleteResearch is unreachable here since
            // InProgressIndex stays -1, but Tick's per-faction loop must still run cleanly).
            sys.Tick(world, Fixed.Zero);
        }

        // Follow-up review: a null ELEMENT inside a research's Levels list ("levels": [ null ]) also loads cleanly —
        // ResearchValidator skips null level entries without erroring and the empty-ladder check sees Count=1 — so the
        // order path must guard the level dereference rather than NRE mid-sim, the same null-tolerance the null-list
        // case above already carries.
        [Fact]
        public void Start_NullLadderElement_DeniesWithoutThrowing()
        {
            var brokenFaction = new FactionDefinition
            {
                Id = "broken_ladder",
                Buildings = new List<BuildingDefinition>
                {
                    new BuildingDefinition { Id = "lab", AvailableResearch = new[] { "broken" } },
                },
                Research = new List<ResearchDefinition>
                {
                    new ResearchDefinition
                    {
                        Id = "broken",
                        Prerequisites = System.Array.Empty<string>(),
                        Levels = new List<ResearchLevel> { null! }, // the malformed-but-tolerated ladder element
                    },
                },
            };

            var world = new EntityWorld();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            var research = new ResearchStore();
            var modSys = new ModifierSystem();
            var modifiers = new ModifierStore(world, modSys);
            modSys.AttachStore(modifiers);
            var events = new CombatEventQueue();

            var sys = new ResearchSystem(buildings, resources, research, modifiers, events, brokenFaction, null);
            int lab = buildings.Create(FixedVec3.Zero, Faction.Player1, BuildingType.Custom, buildingId: "lab");
            buildings.ConstructionTimer[lab] = Fixed.Zero;
            resources.AddOre(Faction.Player1, Fixed.FromInt(1000));

            // Passes every gate up to the level dereference, where the null element must DENY, not NRE.
            bool started = sys.StartResearchCommand(lab, Faction.Player1, 0);
            Assert.False(started);
            Assert.Equal(-1, research.InProgressIndex[(int)Faction.Player1]); // nothing started, nothing spent
            Assert.Equal(1000, resources.Ore[(int)Faction.Player1].ToInt());
        }

        // ── Cross-faction isolation (review finding [medium] 4) ─────────────────────────────────────
        [Fact]
        public void Complete_CrossFaction_ModifierAppliesOnlyToOwningFactionsUnits()
        {
            var h = BuildTwoFaction();
            int p1Unit = h.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            int p2Unit = h.World.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(100), Fixed.FromInt(3));

            Assert.True(h.Sys.StartResearchCommand(h.P2LabId, Faction.Player2, ArmorUpIdx));
            for (int i = 0; i < 3; i++) h.Sys.Tick(h.World, Fixed.Zero); // Player2 completes armor_up level 0

            Assert.Equal(Fixed.Zero, h.World.EffectiveArmor[p1Unit]);       // Player1 NOT affected by Player2's research
            Assert.Equal(Fixed.FromInt(2), h.World.EffectiveArmor[p2Unit]); // Player2's own unit gets it

            // Reverse direction: Player1 now completes its OWN armor_up — Player2 must stay unaffected by it.
            Assert.True(h.Sys.StartResearchCommand(h.LabId, Faction.Player1, ArmorUpIdx));
            for (int i = 0; i < 3; i++) h.Sys.Tick(h.World, Fixed.Zero);

            Assert.Equal(Fixed.FromInt(2), h.World.EffectiveArmor[p1Unit]);
            Assert.Equal(Fixed.FromInt(2), h.World.EffectiveArmor[p2Unit]); // unchanged by Player1's completion
        }

        [Fact]
        public void Start_CrossFaction_InProgressIndexIsolated()
        {
            var h = BuildTwoFaction();
            Assert.True(h.Sys.StartResearchCommand(h.P2LabId, Faction.Player2, ArmorUpIdx)); // Player2 busy

            int oreBefore = Ore(h); // Player1's own balance, untouched by Player2's start

            bool ok = h.Sys.StartResearchCommand(h.LabId, Faction.Player1, ArmorUpIdx); // Player1's OWN order

            Assert.True(ok); // proves the per-faction InProgressIndex array is genuinely isolated, not shared/mis-indexed
            Assert.Equal(oreBefore - 100, Ore(h));
            Assert.Equal(ArmorUpIdx, h.Research.InProgressIndex[(int)Faction.Player1]);
            Assert.Equal(ArmorUpIdx, h.Research.InProgressIndex[(int)Faction.Player2]); // Player2's own order untouched
        }

        // ── Future-spawn catch-up through the REAL wired SimulationHost (review finding [medium] 5) ──
        // FutureSpawn_ReceivesCumulativeModifier_ViaApplyCompletedResearch (above) only ever calls
        // ApplyCompletedResearch directly. This test instead drives a real SimulationHost end-to-end so the
        // ACTUAL EntityWorld.OnUnitDefinitionApplied subscription SimulationHost wires at construction is what
        // fires — proving AC3 ("any spawn path picks up completed research") holds through the real hook, not
        // just the bare method. Mirrors PassiveRuntimeTests.cs's real-host pattern.
        [Fact]
        public void FutureSpawn_ThroughRealSimulationHost_ReceivesCumulativeModifier()
        {
            var faction = new FactionDefinition
            {
                Id = "wired",
                Buildings = new List<BuildingDefinition>
                {
                    new BuildingDefinition { Id = "lab", AvailableResearch = new[] { "armor_up" } },
                },
                Units = new List<UnitDefinition>
                {
                    new UnitDefinition { Id = "worker", DisplayName = "Worker", Category = "Worker", Hp = 100f, Speed = 3f },
                },
                Research = new List<ResearchDefinition>
                {
                    new ResearchDefinition
                    {
                        Id = "armor_up",
                        Prerequisites = System.Array.Empty<string>(),
                        Levels = new List<ResearchLevel>
                        {
                            new ResearchLevel { Cost = new Dictionary<string, int> { { "ore", 10 } }, TimeTicks = 1,
                                                 ModifierDelta = new ResearchModifierDelta { ArmorDelta = 4f } },
                        },
                    },
                },
            };

            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            int lab = host.Buildings.Create(FixedVec3.Zero, Faction.Player1, BuildingType.Custom, buildingId: "lab");
            host.Buildings.ConstructionTimer[lab] = Fixed.Zero;
            host.Resources.AddOre(Faction.Player1, Fixed.FromInt(100));

            Assert.True(host.ResearchSys.StartResearchCommand(lab, Faction.Player1, 0));
            host.StepOnce(); // TimeTicks=1 → completes this tick (no units alive yet to apply it to)

            // Spawn a NEW unit through the REAL wired spawn path (World.Create + ApplyUnitDefinition) — this fires
            // EntityWorld.OnUnitDefinitionApplied, which SimulationHost wires to ResearchSys.ApplyCompletedResearch
            // at construction. If that wiring were ever dropped, only THIS assertion (not the direct-call variant
            // above) would catch the regression.
            int newUnit = host.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            host.World.ApplyUnitDefinition(newUnit, faction.Units[0]);

            Assert.Equal(Fixed.FromInt(4), host.World.EffectiveArmor[newUnit]);
        }

        private static bool HasOrderDenied(Harness h)
        {
            for (int i = 0; i < h.Events.Count; i++)
                if (h.Events.Get(i).Type == CombatEventType.OrderDenied) return true;
            return false;
        }
    }
}
