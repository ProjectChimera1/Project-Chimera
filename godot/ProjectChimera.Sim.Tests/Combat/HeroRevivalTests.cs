#nullable enable
using System;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Economy;
using ProjectChimera.Effects;
using ProjectChimera.Multiplayer;
using Xunit;

namespace ProjectChimera.Sim.Tests.Combat
{
    /// <summary>
    /// Story 3.14 — direct (Godot-free) oracles for the hero death &amp; revival I/O matrix: death→awaiting transition
    /// (enabled) and death→off-field (disabled, row stays Alive for persistence); the revive order via
    /// <see cref="OrderApplier"/>/<see cref="BuildingSystem.ReviveHeroCommand"/> (building-ownership + capability +
    /// hero-ownership + affordability + already-counting guards); countdown→respawn with retained Level/Xp + the authored
    /// HP fraction + <c>GrowthStacksApplied</c> reset re-materializing growth; building-destroyed-mid-countdown cancel;
    /// and the four reserved revival fields folding into the checksum. Each wires a minimal sim (no Godot, no goldens).
    /// </summary>
    public class HeroRevivalTests
    {
        private static readonly Fixed Dt = SimulationLoop.FixedDt;

        /// <summary>The wired pieces for a single-hero + one-revive-building fixture.</summary>
        private sealed class Fixture
        {
            public EntityWorld World = null!;
            public HeroStore Heroes = null!;
            public ModifierStore Modifiers = null!;
            public DeathFeed Deaths = null!;
            public BuildingStore Buildings = null!;
            public ResourceStore Resources = null!;
            public RevivalRuleRuntime Revival = null!;
            public BuildingSystem BuildSys = null!;
            public HeroXpSystem Sys = null!;
            public CombatEventQueue Events = null!;
            public int HeroEntity;
            public int HeroSlot;
            public int BuildingId;
            public UnitDefinition HeroDef = null!;
        }

        /// <summary>A hero unit def (Melee) with base combat stats + a leveling curve/growth, so respawn + growth work.</summary>
        private static UnitDefinition MakeHeroDef() => new UnitDefinition
        {
            Id = "hero_unit", Category = "Melee", IsHero = true,
            Hp = 100, Speed = 3, AttackDamage = 5, AttackRange = 5, AttackSpeed = 1,
            Hero = new HeroDefinition { MaxLevel = 10, BaseXp = 50, XpGrowth = 1f,
                                        HealthPerLevel = 10, DamagePerLevel = 2, ArmorPerLevel = 1 },
        };

        /// <summary>Build a fixture with one Player1 hero at <paramref name="level"/>, linked, plus a Player1 building
        /// that (by default) revives heroes, and a resource bank with <paramref name="ore"/> ore. The revival rule uses
        /// <paramref name="timeBase"/> seconds and 0.5 HP fraction. Wires HeroXpSystem with the full revival deps.</summary>
        private static Fixture Make(int level = 1, bool revivesFlag = true, bool enabled = true,
                                    int ore = 10000, float timeBase = 0.1f, float hpFraction = 0.5f,
                                    int costCrystalBase = 0, int costCrystalPerLevel = 0, float timePerLevel = 0f,
                                    int crystal = 0, bool mintSourceDef = true)
        {
            var world = new EntityWorld();
            var modSys = new ModifierSystem();
            var modifiers = new ModifierStore(world, modSys);
            modSys.AttachStore(modifiers);
            var deaths = new DeathFeed();
            var heroes = new HeroStore();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            var events = new CombatEventQueue();

            var rule = new RevivalRule
            {
                Enabled = enabled,
                CostOreBase = 100, CostOrePerLevel = 25,
                CostCrystalBase = costCrystalBase, CostCrystalPerLevel = costCrystalPerLevel,
                TimeBaseSeconds = timeBase, TimePerLevelSeconds = timePerLevel, ReviveHpFraction = hpFraction,
            };
            var revival = new RevivalRuleRuntime(rule);
            var buildSys = new BuildingSystem(buildings, resources, null, null, null, heroes, revival);

            resources.AddOre(Faction.Player1, Fixed.FromInt(ore));
            if (crystal > 0) resources.AddCrystal(Faction.Player1, Fixed.FromInt(crystal));

            var heroDef = MakeHeroDef();
            int ent = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            world.ApplyUnitDefinition(ent, heroDef); // sets Base/Effective combat stats so growth is observable

            int slot = heroes.Mint(new HeroId(42), ent, level, Fixed.Zero,
                maxLevel: 10, baseXp: Fixed.FromInt(50), xpGrowth: Fixed.One, xpShareRadius: Fixed.FromInt(30),
                healthPerLevel: Fixed.FromInt(10), damagePerLevel: Fixed.FromInt(2), armorPerLevel: Fixed.FromInt(1),
                sourceDef: mintSourceDef ? heroDef : null, ownerFaction: Faction.Player1);
            world.HeroIndex[ent] = heroes.PackRef(slot);

            // Revive building at (20, 0, 5), Player1, pre-built, flagged (or not) revives_heroes.
            int bId = buildSys.PlaceBuildingDirect(BuildingType.CommandCenter, Faction.Player1,
                new FixedVec3(Fixed.FromInt(20), Fixed.Zero, Fixed.FromInt(5)), preBuilt: true, revivesHeroes: revivesFlag);

            // The respawn spawn delegate reuses the mapper (world.Create + ApplyUnitDefinition), like SpawnUnit.
            Func<UnitDefinition, Faction, Fixed, Fixed, int> spawn = (def, fac, x, z) =>
            {
                int id = world.Create(new FixedVec3(x, Fixed.Zero, z), fac,
                                      Fixed.FromFloat(def.Hp), Fixed.FromFloat(def.Speed));
                if (id < 0) return id;
                world.ApplyUnitDefinition(id, def);
                return id;
            };

            var sys = new HeroXpSystem(heroes, modifiers, deaths, buildings, revival, spawn, events);

            return new Fixture
            {
                World = world, Heroes = heroes, Modifiers = modifiers, Deaths = deaths, Buildings = buildings,
                Resources = resources, Revival = revival, BuildSys = buildSys, Sys = sys, Events = events,
                HeroEntity = ent, HeroSlot = slot, BuildingId = bId, HeroDef = heroDef,
            };
        }

        /// <summary>Kill the hero entity (as combat would) and tick so death-detection transitions the row.</summary>
        private static void KillHeroAndDetect(Fixture f)
        {
            f.World.Destroy(f.HeroEntity);
            f.Sys.Tick(f.World, Dt);
        }

        // ── Death transition ────────────────────────────────────────────────────────

        [Fact]
        public void HeroDies_RevivalEnabled_TransitionsToAwaiting_SlotNotRecycled()
        {
            var f = Make(level: 3, enabled: true);
            KillHeroAndDetect(f);

            Assert.False(f.Heroes.Alive3_14[f.HeroSlot]);
            Assert.True(f.Heroes.AwaitingRevival[f.HeroSlot]);
            Assert.Equal(Fixed.Zero.Raw, f.Heroes.RevivalTimer[f.HeroSlot].Raw);
            Assert.Equal(HeroStore.REVIVAL_NONE, f.Heroes.RevivalLink[f.HeroSlot]);
            Assert.True(f.Heroes.Alive[f.HeroSlot]);          // the persisted row is NOT recycled
            Assert.Equal(3, f.Heroes.Level[f.HeroSlot]);      // identity + level retained
        }

        [Fact]
        public void HeroDies_RevivalDisabled_LeavesField_NotAwaiting_RowStaysAliveForPersistence()
        {
            var f = Make(level: 4, enabled: false);
            KillHeroAndDetect(f);

            Assert.False(f.Heroes.Alive3_14[f.HeroSlot]);     // off the field
            Assert.False(f.Heroes.AwaitingRevival[f.HeroSlot]); // NOT awaiting revival
            Assert.True(f.Heroes.Alive[f.HeroSlot]);          // row stays Alive → persistence finalizes Level/Xp (FR-7a)
            Assert.Equal(4, f.Heroes.Level[f.HeroSlot]);
        }

        // ── Revive order guards ───────────────────────────────────────────────────────

        [Fact]
        public void ReviveOrder_Valid_SpendsLevelScaledCostOnce_StartsCountdown()
        {
            var f = Make(level: 2, enabled: true);
            KillHeroAndDetect(f);
            Fixed oreBefore = f.Resources.Ore[(int)Faction.Player1];

            IssueRevive(f);

            // cost(level 2) = 100 + 25*2 = 150 ore spent once.
            Assert.Equal((oreBefore - Fixed.FromInt(150)).Raw, f.Resources.Ore[(int)Faction.Player1].Raw);
            Assert.True(f.Heroes.RevivalTimer[f.HeroSlot] > Fixed.Zero);
            Assert.NotEqual(HeroStore.REVIVAL_NONE, f.Heroes.RevivalLink[f.HeroSlot]);
        }

        [Fact]
        public void ReviveOrder_Unaffordable_Rejected_NothingSpent()
        {
            var f = Make(level: 2, enabled: true, ore: 10); // < 150 cost
            KillHeroAndDetect(f);

            bool ok = f.BuildSys.ReviveHeroCommand(f.BuildingId, Faction.Player1, Fixed.FromRaw(f.HeroSlot), f.Events);
            Assert.False(ok);
            Assert.Equal(Fixed.FromInt(10).Raw, f.Resources.Ore[(int)Faction.Player1].Raw); // untouched
            Assert.Equal(HeroStore.REVIVAL_NONE, f.Heroes.RevivalLink[f.HeroSlot]);           // no countdown
            // A legit-but-unaffordable order (ownership already validated) surfaces an OrderDenied cue.
            Assert.True(HasEvent(f.Events, CombatEventType.OrderDenied));
        }

        [Fact]
        public void ReviveOrder_NotOwnerFaction_Rejected_NothingSpent()
        {
            var f = Make(level: 2, enabled: true);
            KillHeroAndDetect(f);
            Fixed oreBefore = f.Resources.Ore[(int)Faction.Player1];

            // A Player2 order against a Player1 building/hero must reject (anti-cheat).
            bool ok = f.BuildSys.ReviveHeroCommand(f.BuildingId, Faction.Player2, Fixed.FromRaw(f.HeroSlot), f.Events);
            Assert.False(ok);
            Assert.Equal(oreBefore.Raw, f.Resources.Ore[(int)Faction.Player1].Raw);
            Assert.Equal(HeroStore.REVIVAL_NONE, f.Heroes.RevivalLink[f.HeroSlot]);
            // Anti-cheat rejections stay SILENT — no OrderDenied cue (no feedback / position leak to a crafted order).
            Assert.False(HasEvent(f.Events, CombatEventType.OrderDenied));
        }

        [Fact]
        public void ReviveOrder_BuildingLacksCapability_Rejected()
        {
            var f = Make(level: 2, enabled: true, revivesFlag: false);
            KillHeroAndDetect(f);

            bool ok = f.BuildSys.ReviveHeroCommand(f.BuildingId, Faction.Player1, Fixed.FromRaw(f.HeroSlot));
            Assert.False(ok);
            Assert.Equal(HeroStore.REVIVAL_NONE, f.Heroes.RevivalLink[f.HeroSlot]);
        }

        [Fact]
        public void ReviveOrder_HeroNotAwaiting_Rejected()
        {
            var f = Make(level: 2, enabled: true); // hero still on the field (not killed)
            bool ok = f.BuildSys.ReviveHeroCommand(f.BuildingId, Faction.Player1, Fixed.FromRaw(f.HeroSlot));
            Assert.False(ok);
        }

        [Fact]
        public void ReviveOrder_AlreadyCounting_SecondOrderRejected_NoDoubleSpend()
        {
            var f = Make(level: 2, enabled: true);
            KillHeroAndDetect(f);
            IssueRevive(f);
            Fixed oreAfterFirst = f.Resources.Ore[(int)Faction.Player1];

            bool ok = f.BuildSys.ReviveHeroCommand(f.BuildingId, Faction.Player1, Fixed.FromRaw(f.HeroSlot));
            Assert.False(ok); // RevivalLink != NONE → already counting
            Assert.Equal(oreAfterFirst.Raw, f.Resources.Ore[(int)Faction.Player1].Raw); // no second spend
        }

        // ── Countdown → respawn ───────────────────────────────────────────────────────

        [Fact]
        public void Countdown_Completes_RespawnsHero_RetainsLevelXp_HpFraction_ReLinks_ResetsGrowth()
        {
            var f = Make(level: 3, enabled: true, timeBase: 0.1f, hpFraction: 0.5f);
            // Grow while on the field (deploy-at-3 → 2 stacks), then kill.
            f.Sys.Tick(f.World, Dt);
            Assert.Equal(2, f.Heroes.GrowthStacksApplied[f.HeroSlot]);
            KillHeroAndDetect(f);
            IssueRevive(f);

            // Run the countdown to completion (0.1s ≈ 3 ticks) + one more tick to re-materialize growth.
            int respawned = -1;
            for (int t = 0; t < 10; t++)
            {
                f.Sys.Tick(f.World, Dt);
                if (f.Heroes.Alive3_14[f.HeroSlot]) { respawned = f.Heroes.EntityId[f.HeroSlot]; break; }
            }
            Assert.True(respawned >= 0, "hero did not respawn within the countdown");
            Assert.True(HasEvent(f.Events, CombatEventType.HeroRevived)); // revival announced on the event bus

            // Identity + level retained; the entity is a FRESH live one (its slot may be recycled from the dead hero's
            // — an EntityWorld id is a slot — but it is a new occupant, alive, and re-linked to THIS hero row).
            Assert.Equal(3, f.Heroes.Level[f.HeroSlot]);
            Assert.True(f.World.IsAlive(respawned));
            Assert.True(f.Heroes.TryResolveRef(f.World.HeroIndex[respawned], out int lslot) && lslot == f.HeroSlot);
            Assert.False(f.Heroes.AwaitingRevival[f.HeroSlot]);

            // Respawn HP = GROWN max × 0.5. Grown max = base 100 + (Level 3 - 1) × HealthPerLevel 10 = 120 → 60.
            // Growth is re-materialized IN the respawn tick (not deferred), so EffectiveMaxHealth is already the grown
            // max here and the fraction applies to the hero's actual max, not its base max.
            Fixed expectedHp = (Fixed.FromInt(100) + Fixed.FromInt(20)) * Fixed.FromFloat(0.5f); // 60
            Assert.Equal(expectedHp.Raw, f.World.Health[respawned].Raw);
            Assert.Equal(2, f.Heroes.GrowthStacksApplied[f.HeroSlot]); // growth applied in-tick (reset-then-reconcile)
            // +2 damage/level × 2 stacks over the authored base 5 — grown stats are live the same tick.
            Assert.Equal((Fixed.FromInt(5) + Fixed.FromInt(4)).Raw, f.World.EffectiveAttackDamage[respawned].Raw);

            // The authored fraction is STABLE across further ticks: the (Level-1) growth heals do NOT inflate current
            // Health above fraction × grown max (regression guard — applying the fraction before growth would settle the
            // hero near full HP instead of at the authored 50%).
            f.Sys.Tick(f.World, Dt);
            Assert.Equal(2, f.Heroes.GrowthStacksApplied[f.HeroSlot]); // next tick is a no-op
            Assert.Equal(expectedHp.Raw, f.World.Health[respawned].Raw); // still 60, not inflated to 80
        }

        [Fact]
        public void ReviveBuildingDestroyedMidCountdown_CancelsDeterministically_NoRefund_StaysAwaiting()
        {
            var f = Make(level: 2, enabled: true, timeBase: 5f); // long countdown so we can destroy first
            KillHeroAndDetect(f);
            IssueRevive(f);
            Fixed oreAfterOrder = f.Resources.Ore[(int)Faction.Player1];

            f.Buildings.Destroy(f.BuildingId); // razed mid-countdown
            f.Sys.Tick(f.World, Dt);

            Assert.Equal(HeroStore.REVIVAL_NONE, f.Heroes.RevivalLink[f.HeroSlot]); // cancelled
            Assert.Equal(Fixed.Zero.Raw, f.Heroes.RevivalTimer[f.HeroSlot].Raw);
            Assert.True(f.Heroes.AwaitingRevival[f.HeroSlot]);                       // still awaiting → can re-order
            Assert.Equal(oreAfterOrder.Raw, f.Resources.Ore[(int)Faction.Player1].Raw); // NO refund
        }

        // ── The revive order rides the shared OrderApplier path ───────────────────────

        [Fact]
        public void ReviveOrder_ThroughOrderApplier_ExecutesLikeDirectCommand()
        {
            var f = Make(level: 1, enabled: true);
            KillHeroAndDetect(f);
            Fixed oreBefore = f.Resources.Ore[(int)Faction.Player1];

            var order = new UnitOrder(f.BuildingId, UnitCommand.ReviveHero, Fixed.FromRaw(f.HeroSlot), Fixed.Zero);
            OrderApplier.Apply(f.World, in order, Faction.Player1, buildings: f.BuildSys);

            // cost(level 1) = 100 + 25 = 125 spent; countdown started.
            Assert.Equal((oreBefore - Fixed.FromInt(125)).Raw, f.Resources.Ore[(int)Faction.Player1].Raw);
            Assert.NotEqual(HeroStore.REVIVAL_NONE, f.Heroes.RevivalLink[f.HeroSlot]);
        }

        // ── Cost/time curve terms that the happy-path fixtures leave at zero ──────────

        [Fact]
        public void ReviveOrder_ChargesLevelScaledCrystal_CheckBothDebitBoth()
        {
            // Non-zero crystal cost: cost_crystal(level 2) = 20 + 5*2 = 30. Ore cost(2) = 150.
            var f = Make(level: 2, enabled: true, costCrystalBase: 20, costCrystalPerLevel: 5, crystal: 1000);
            KillHeroAndDetect(f);
            Fixed oreBefore = f.Resources.Ore[(int)Faction.Player1];
            Fixed crystalBefore = f.Resources.Crystal[(int)Faction.Player1];

            IssueRevive(f);

            Assert.Equal((oreBefore - Fixed.FromInt(150)).Raw, f.Resources.Ore[(int)Faction.Player1].Raw);
            Assert.Equal((crystalBefore - Fixed.FromInt(30)).Raw, f.Resources.Crystal[(int)Faction.Player1].Raw);
        }

        [Fact]
        public void ReviveOrder_AffordableOre_UnaffordableCrystal_SpendsNothing()
        {
            // Ore covers 150 but crystal bank (10) < crystal cost 30 → the check-both-then-debit-both contract spends 0.
            var f = Make(level: 2, enabled: true, costCrystalBase: 20, costCrystalPerLevel: 5, crystal: 10);
            KillHeroAndDetect(f);
            Fixed oreBefore = f.Resources.Ore[(int)Faction.Player1];

            bool ok = f.BuildSys.ReviveHeroCommand(f.BuildingId, Faction.Player1, Fixed.FromRaw(f.HeroSlot), f.Events);
            Assert.False(ok);
            Assert.Equal(oreBefore.Raw, f.Resources.Ore[(int)Faction.Player1].Raw);        // ore untouched
            Assert.Equal(Fixed.FromInt(10).Raw, f.Resources.Crystal[(int)Faction.Player1].Raw); // crystal untouched
            Assert.True(HasEvent(f.Events, CombatEventType.OrderDenied));
        }

        [Fact]
        public void ReviveOrder_CountdownScalesWithLevel_PerLevelTimeTerm()
        {
            // time(level 4) = base 1 + perLevel 2 * 4 = 9 seconds. Assert the folded RevivalTimer matches (raw Fixed).
            var f = Make(level: 4, enabled: true, timeBase: 1f, timePerLevel: 2f);
            KillHeroAndDetect(f);
            IssueRevive(f);
            Assert.Equal(Fixed.FromInt(9).Raw, f.Heroes.RevivalTimer[f.HeroSlot].Raw);
        }

        // ── Guard parity + robustness ─────────────────────────────────────────────────

        [Fact]
        public void ReviveOrder_BuildingUnderConstruction_Rejected_NothingSpent()
        {
            var f = Make(level: 2, enabled: true);
            KillHeroAndDetect(f);
            f.Buildings.ConstructionTimer[f.BuildingId] = Fixed.FromInt(5); // still building
            Fixed oreBefore = f.Resources.Ore[(int)Faction.Player1];

            bool ok = f.BuildSys.ReviveHeroCommand(f.BuildingId, Faction.Player1, Fixed.FromRaw(f.HeroSlot), f.Events);
            Assert.False(ok); // guard-parity with TrainUnit — a constructing building cannot revive
            Assert.Equal(oreBefore.Raw, f.Resources.Ore[(int)Faction.Player1].Raw);
            Assert.Equal(HeroStore.REVIVAL_NONE, f.Heroes.RevivalLink[f.HeroSlot]);
        }

        [Fact]
        public void ReviveOrder_HeroHasNoSourceDef_Rejected_NothingSpent()
        {
            // A hero minted without a respawn def cannot be revived → reject the ORDER (never spend) rather than spend and
            // fail forever at respawn (the pay-for-nothing loop).
            var f = Make(level: 2, enabled: true, mintSourceDef: false);
            KillHeroAndDetect(f);
            Fixed oreBefore = f.Resources.Ore[(int)Faction.Player1];

            bool ok = f.BuildSys.ReviveHeroCommand(f.BuildingId, Faction.Player1, Fixed.FromRaw(f.HeroSlot), f.Events);
            Assert.False(ok);
            Assert.Equal(oreBefore.Raw, f.Resources.Ore[(int)Faction.Player1].Raw);
            Assert.Equal(HeroStore.REVIVAL_NONE, f.Heroes.RevivalLink[f.HeroSlot]);
        }

        [Fact]
        public void PlaceBuildingDirect_ResolvesRevivesHeroesFromFactionDef()
        {
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            var buildSys  = new BuildingSystem(buildings, resources, null, null, null, new HeroStore(), new RevivalRuleRuntime());

            // A faction whose authored command_center building is flagged revives_heroes. Story 4.1: BuildingDefinition
            // fields ConstructionTime/SupplyBonus are required by the new resolved-stats Create() path (bdef.
            // ConstructionTime!.Value is dereferenced unconditionally once bdef resolves) — authored here so
            // PlaceBuildingDirect's def-driven threading doesn't NRE; their values are irrelevant to this RevivesHeroes-only test.
            var fdef = new FactionDefinition();
            fdef.Buildings.Add(new BuildingDefinition { Id = "command_center", Category = "Structure", RevivesHeroes = true,
                ConstructionTime = 15f, SupplyBonus = 10, ProducesCategory = "Worker" });
            buildSys.SetFactionDef(Faction.Player1, fdef);

            // Placed WITHOUT an explicit flag → the capability must be resolved from the faction def (else the whole
            // feature is unreachable in a real match).
            int bId = buildSys.PlaceBuildingDirect(BuildingType.CommandCenter, Faction.Player1, FixedVec3.Zero, preBuilt: true);
            Assert.True(buildings.RevivesHeroes[bId]);

            // A faction without the flag resolves to false.
            var plain = new FactionDefinition();
            plain.Buildings.Add(new BuildingDefinition { Id = "command_center", Category = "Structure",
                ConstructionTime = 15f, SupplyBonus = 10, ProducesCategory = "Worker" });
            buildSys.SetFactionDef(Faction.Player2, plain);
            int bId2 = buildSys.PlaceBuildingDirect(BuildingType.CommandCenter, Faction.Player2, FixedVec3.Zero, preBuilt: true);
            Assert.False(buildings.RevivesHeroes[bId2]);
        }

        [Fact]
        public void QueueWorkerBuild_ResolvesRevivesHeroesFromFactionDef()
        {
            var world     = new EntityWorld();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            var buildSys  = new BuildingSystem(buildings, resources, null, null, null, new HeroStore(), new RevivalRuleRuntime());

            // A faction whose authored command_center is flagged revives_heroes, free to build. Story 4.1: authored with
            // ConstructionTime/SupplyBonus/ProducesCategory so QueueWorkerBuild's def-driven Create() threading doesn't NRE.
            var fdef = new FactionDefinition();
            fdef.Buildings.Add(new BuildingDefinition { Id = "command_center", Category = "Structure", RevivesHeroes = true, CostOre = 0,
                ConstructionTime = 15f, SupplyBonus = 10, ProducesCategory = "Worker" });
            buildSys.SetFactionDef(Faction.Player1, fdef);

            // A live worker (GatherState != Inactive) constructs the building. The PLAYER-BUILT path must resolve the
            // capability from the faction def too — a dropped 4th arg to _buildings.Create would leave revive dead on
            // player-built (not just scenario-placed) buildings while every scenario-placed test still passes green.
            int worker = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(50), Fixed.FromInt(3));
            world.GatherState[worker] = GatherState.Idle;

            int bId = buildSys.QueueWorkerBuild(worker, BuildingType.CommandCenter,
                new FixedVec3(Fixed.FromInt(10), Fixed.Zero, Fixed.FromInt(10)), Faction.Player1, resources, world);

            Assert.True(bId >= 0, "expected the worker build to be placed");
            Assert.True(buildings.RevivesHeroes[bId]); // resolved from the faction def on the player-built path
        }

        // ── Death is announced via CombatEventQueue (the real KillEntity path) ────────

        [Fact]
        public void HeroDies_ThroughKillEntity_ThenTick_TransitionsToAwaiting()
        {
            // End-to-end across the REAL combat death surface: KillEntity (which Destroys the entity) followed by a
            // HeroXpSystem tick must both announce the fall AND drive the persisted row into awaiting-revival — the two
            // halves the other tests exercise in isolation (announce via KillEntity; transition via world.Destroy).
            var f = Make(level: 3, enabled: true);
            DamageResolver.KillEntity(f.World, f.HeroEntity, Faction.Player2, f.Events, null, null);
            Assert.True(HasEvent(f.Events, CombatEventType.HeroFell)); // announced at the kill site

            f.Sys.Tick(f.World, Dt); // the link-stale scan transitions the row
            Assert.False(f.Heroes.Alive3_14[f.HeroSlot]);
            Assert.True(f.Heroes.AwaitingRevival[f.HeroSlot]);
            Assert.True(f.Heroes.Alive[f.HeroSlot]);      // row retained (identity + Level/Xp intact)
            Assert.Equal(3, f.Heroes.Level[f.HeroSlot]);
        }

        [Fact]
        public void HeroDies_ThroughKillEntity_PushesHeroFell_ButNonHeroDoesNot()
        {
            var f = Make(level: 2, enabled: true);

            // A plain (non-hero) unit dying through the real damage path must NOT announce a hero fall.
            int grunt = f.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(10), Fixed.FromInt(3));
            DamageResolver.KillEntity(f.World, grunt, Faction.Player2, f.Events, null, null);
            Assert.False(HasEvent(f.Events, CombatEventType.HeroFell));

            // The hero dying through the SAME real path announces HeroFell (AC1) at its death position.
            DamageResolver.KillEntity(f.World, f.HeroEntity, Faction.Player2, f.Events, null, null);
            Assert.True(HasEvent(f.Events, CombatEventType.HeroFell));
        }

        // ── The four reserved revival fields fold into the checksum ───────────────────

        [Fact]
        public void ReservedRevivalFields_FoldIntoChecksum()
        {
            var registry = new FactionRegistry(2);
            var world = new EntityWorld();
            var resources = new ResourceStore(Fixed.Zero);
            var buildings = new BuildingStore();
            var heroes = new HeroStore();

            int slot = heroes.Mint(new HeroId(7), entityId: 3, level: 1, xp: Fixed.Zero);
            uint baseline = SimChecksum.Compute(world, buildings, resources, registry, null, heroes);

            heroes.Alive3_14[slot] = false;
            uint a = SimChecksum.Compute(world, buildings, resources, registry, null, heroes);
            Assert.NotEqual(baseline, a);

            heroes.AwaitingRevival[slot] = true;
            uint b = SimChecksum.Compute(world, buildings, resources, registry, null, heroes);
            Assert.NotEqual(a, b);

            heroes.RevivalTimer[slot] = Fixed.FromInt(9);
            uint c = SimChecksum.Compute(world, buildings, resources, registry, null, heroes);
            Assert.NotEqual(b, c);

            heroes.RevivalLink[slot] = 5;
            uint d = SimChecksum.Compute(world, buildings, resources, registry, null, heroes);
            Assert.NotEqual(c, d);
        }

        private static void IssueRevive(Fixture f)
        {
            bool ok = f.BuildSys.ReviveHeroCommand(f.BuildingId, Faction.Player1, Fixed.FromRaw(f.HeroSlot), f.Events);
            Assert.True(ok, "expected the revive order to be accepted");
        }

        /// <summary>True if the transient event queue holds at least one event of the given type.</summary>
        private static bool HasEvent(CombatEventQueue q, CombatEventType type)
        {
            for (int i = 0; i < q.Count; i++)
                if (q.Get(i).Type == type) return true;
            return false;
        }
    }
}
