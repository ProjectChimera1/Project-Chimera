#nullable enable
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Combat
{
    /// <summary>
    /// Story 3.13 — direct (Godot-free) oracles for the HeroXpSystem I/O matrix: in-range grants, out-of-range no-grant,
    /// shared full-credit, friendly-no-credit, dead-hero-skip, default-bounty-from-cost, level-up growth via
    /// ModifierStore (a stat delta materializes in Effective*), deploy-at-N catch-up, max-level clamp (no overflow/throw),
    /// and the discard-drops-a-grown-run reset. Each wires a minimal EntityWorld + HeroStore + ModifierStore + DeathFeed
    /// + HeroXpSystem, mints a hero, pushes deaths, and ticks — no Godot, no goldens.
    /// </summary>
    public class HeroXpTests
    {
        private static readonly Fixed Dt = SimulationLoop.FixedDt;
        private const int MaxLevel = 5;

        /// <summary>The wired pieces + the hero's slot/entity for a single-hero fixture at level 1.</summary>
        private sealed class Fixture
        {
            public EntityWorld World = null!;
            public HeroStore Heroes = null!;
            public ModifierStore Modifiers = null!;
            public DeathFeed Deaths = null!;
            public HeroXpSystem Sys = null!;
            public int HeroEntity;
            public int HeroSlot;
        }

        /// <summary>Build a fixture with one Player1 hero at <paramref name="level"/>, curve baseXp 50 / growth 1.0 /
        /// share radius <paramref name="shareRadius"/> / +10 hp, +2 dmg, +1 armor per level, linked via HeroIndex.</summary>
        private static Fixture MakeHero(int level = 1, int shareRadius = 10, int maxLevel = MaxLevel,
                                        int baseXp = 50, Fixed? growth = null, FixedVec3 pos = default)
        {
            var world = new EntityWorld();
            var modSys = new ModifierSystem();
            var modifiers = new ModifierStore(world, modSys);
            modSys.AttachStore(modifiers);
            var deaths = new DeathFeed();
            var heroes = new HeroStore();

            int ent = world.Create(pos, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            world.BaseAttackDamage[ent] = Fixed.FromInt(5);
            world.EffectiveAttackDamage[ent] = Fixed.FromInt(5);

            int slot = heroes.Mint(new HeroId(42), ent, level, Fixed.Zero,
                maxLevel: maxLevel, baseXp: Fixed.FromInt(baseXp), xpGrowth: growth ?? Fixed.One,
                xpShareRadius: Fixed.FromInt(shareRadius),
                healthPerLevel: Fixed.FromInt(10), damagePerLevel: Fixed.FromInt(2), armorPerLevel: Fixed.FromInt(1));
            world.HeroIndex[ent] = heroes.PackRef(slot);

            return new Fixture
            {
                World = world, Heroes = heroes, Modifiers = modifiers, Deaths = deaths,
                Sys = new HeroXpSystem(heroes, modifiers, deaths), HeroEntity = ent, HeroSlot = slot,
            };
        }

        // ── Credit ────────────────────────────────────────────────────────────────

        [Fact]
        public void InRangeHostileDeath_GrantsFullBounty()
        {
            var f = MakeHero(shareRadius: 10);
            f.Deaths.Push(new FixedVec3(Fixed.FromInt(5), Fixed.Zero, Fixed.Zero), Faction.Neutral, Fixed.FromInt(30));
            f.Sys.Tick(f.World, Dt);
            Assert.Equal(Fixed.FromInt(30).Raw, f.Heroes.Xp[f.HeroSlot].Raw);
            Assert.Equal(1, f.Heroes.Level[f.HeroSlot]); // 30 < 50 → no level up yet
        }

        [Fact]
        public void OutOfRangeDeath_GrantsNothing()
        {
            var f = MakeHero(shareRadius: 3);
            f.Deaths.Push(new FixedVec3(Fixed.FromInt(50), Fixed.Zero, Fixed.Zero), Faction.Neutral, Fixed.FromInt(30));
            f.Sys.Tick(f.World, Dt);
            Assert.Equal(Fixed.Zero.Raw, f.Heroes.Xp[f.HeroSlot].Raw);
        }

        [Fact]
        public void FarOffMapDeath_DoesNotOverflowRangeCheck_GrantsNothing()
        {
            // Regression: the hero↔death squared distance is uncapped by map size (coords up to ±map_bounds), so a
            // single-axis gap past ~181 units overflows the int32 truncation in the Fixed range check (X²+Y²+Z²) and
            // wraps NEGATIVE — reading as "in range" and crediting a kill across the whole map. 240 units at radius 10
            // must stay out of range. The long-widened comparison keeps this correct.
            var f = MakeHero(shareRadius: 10);
            f.Deaths.Push(new FixedVec3(Fixed.FromInt(240), Fixed.Zero, Fixed.Zero), Faction.Neutral, Fixed.FromInt(30));
            f.Sys.Tick(f.World, Dt);
            Assert.Equal(Fixed.Zero.Raw, f.Heroes.Xp[f.HeroSlot].Raw);
        }

        [Fact]
        public void FriendlyDeath_GrantsNothing()
        {
            var f = MakeHero(shareRadius: 100);
            // Same faction as the hero (Player1) → no credit even in range.
            f.Deaths.Push(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(30));
            f.Sys.Tick(f.World, Dt);
            Assert.Equal(Fixed.Zero.Raw, f.Heroes.Xp[f.HeroSlot].Raw);
        }

        [Fact]
        public void SharedKill_EachHeroGetsFullBounty_NotSplit()
        {
            var f = MakeHero(shareRadius: 100);
            // A second Player1 hero, also in range of the same death.
            int ent2 = f.World.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.Zero),
                                      Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            int slot2 = f.Heroes.Mint(new HeroId(99), ent2, level: 1, xp: Fixed.Zero,
                maxLevel: MaxLevel, baseXp: Fixed.FromInt(50), xpGrowth: Fixed.One, xpShareRadius: Fixed.FromInt(100),
                healthPerLevel: Fixed.Zero, damagePerLevel: Fixed.Zero, armorPerLevel: Fixed.Zero);
            f.World.HeroIndex[ent2] = f.Heroes.PackRef(slot2);

            f.Deaths.Push(FixedVec3.Zero, Faction.Neutral, Fixed.FromInt(30));
            f.Sys.Tick(f.World, Dt);

            Assert.Equal(Fixed.FromInt(30).Raw, f.Heroes.Xp[f.HeroSlot].Raw); // full, not split
            Assert.Equal(Fixed.FromInt(30).Raw, f.Heroes.Xp[slot2].Raw);      // full, not split
        }

        [Fact]
        public void DeadHero_IsSkipped_NoThrow()
        {
            var f = MakeHero(shareRadius: 100);
            f.World.Destroy(f.HeroEntity); // hero entity gone; the row's link is now stale
            f.Deaths.Push(FixedVec3.Zero, Faction.Neutral, Fixed.FromInt(30));
            f.Sys.Tick(f.World, Dt); // must not throw
            Assert.Equal(Fixed.Zero.Raw, f.Heroes.Xp[f.HeroSlot].Raw); // no XP granted to a dead hero
        }

        // ── Default bounty ─────────────────────────────────────────────────────────

        [Fact]
        public void ResolveXpBounty_DefaultsToOrePlusCrystalCost()
        {
            var def = new UnitDefinition { CostOre = 40, CostCrystal = 10 }; // xp_bounty omitted (null)
            Assert.Equal(50, def.ResolveXpBounty());

            var authored = new UnitDefinition { CostOre = 40, CostCrystal = 10, XpBounty = 7 };
            Assert.Equal(7, authored.ResolveXpBounty()); // authored value wins

            // The mapper quantizes it to Fixed at the single boundary.
            var w = new EntityWorld();
            int id = w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(10), Fixed.FromInt(3));
            w.ApplyUnitDefinition(id, def);
            Assert.Equal(Fixed.FromInt(50).Raw, w.XpBounty[id].Raw);
        }

        [Fact]
        public void ResolveXpBounty_DerivedCostSum_ClampsToFixedSafeMax_NeverNegative()
        {
            // Each cost is individually validator-legal (< 32768) but their SUM (60000) would overflow Fixed.FromInt to a
            // NEGATIVE bounty. The derived path clamps to XpBountyMax and stays positive through the mapper.
            var def = new UnitDefinition { CostOre = 30000, CostCrystal = 30000 }; // xp_bounty omitted (null → derived)
            Assert.Equal(UnitDefinition.XpBountyMax, def.ResolveXpBounty());

            var w = new EntityWorld();
            int id = w.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(10), Fixed.FromInt(3));
            w.ApplyUnitDefinition(id, def);
            Assert.True(w.XpBounty[id] > Fixed.Zero); // positive Fixed, not a wrapped-negative one
        }

        [Fact]
        public void MultipleHugeDeathsInOneTick_SaturateWithoutNegativeOverflow()
        {
            // Three near-ceiling bounties credited in the SAME tick (all in step-1 before AdvanceLevels). A raw Fixed '+'
            // would wrap the running Xp NEGATIVE past int.MaxValue; the widened long add saturates instead.
            var f = MakeHero(level: 1, shareRadius: 100, maxLevel: 100, baseXp: 30000);
            for (int i = 0; i < 3; i++)
                f.Deaths.Push(FixedVec3.Zero, Faction.Neutral, Fixed.FromInt(UnitDefinition.XpBountyMax));
            f.Sys.Tick(f.World, Dt); // must not throw

            Assert.True(f.Heroes.Xp[f.HeroSlot] >= Fixed.Zero, "XP must never wrap negative");
            Assert.True(f.Heroes.Xp[f.HeroSlot] <= HeroXpSystem.XpCeiling);
        }

        // ── Level-up + growth ───────────────────────────────────────────────────────

        [Fact]
        public void CrossingThreshold_LevelsUp_AndAppliesGrowthViaModifierStore()
        {
            var f = MakeHero(shareRadius: 100);
            Fixed baseDamage = f.World.EffectiveAttackDamage[f.HeroEntity];
            Fixed baseMaxHp  = f.World.EffectiveMaxHealth[f.HeroEntity];

            // 60 XP (>= the 50 threshold) → level 2 → 1 growth stack (+2 dmg, +10 max-hp, +1 armor).
            f.Deaths.Push(FixedVec3.Zero, Faction.Neutral, Fixed.FromInt(60));
            f.Sys.Tick(f.World, Dt);

            Assert.Equal(2, f.Heroes.Level[f.HeroSlot]);
            Assert.Equal(1, f.Heroes.GrowthStacksApplied[f.HeroSlot]);
            Assert.Equal(Fixed.FromInt(10).Raw, f.Heroes.Xp[f.HeroSlot].Raw); // 60 - 50 consumed
            // The growth materialized in Effective* (ModifierStore.Apply recomputes eagerly).
            Assert.Equal((baseDamage + Fixed.FromInt(2)).Raw, f.World.EffectiveAttackDamage[f.HeroEntity].Raw);
            Assert.Equal((baseMaxHp + Fixed.FromInt(10)).Raw, f.World.EffectiveMaxHealth[f.HeroEntity].Raw);
            Assert.Equal(Fixed.FromInt(1).Raw, f.World.EffectiveArmor[f.HeroEntity].Raw);
        }

        [Fact]
        public void DeployAtLevelN_FirstTick_ReconcilesGrowthToNMinus1Stacks()
        {
            // A hero minted from a saved profile at level 5 → first tick catches growth up to 4 stacks (no deaths needed).
            var f = MakeHero(level: 5, shareRadius: 100);
            Fixed baseDamage = f.World.EffectiveAttackDamage[f.HeroEntity];

            f.Sys.Tick(f.World, Dt);

            Assert.Equal(4, f.Heroes.GrowthStacksApplied[f.HeroSlot]);       // Level-1 == 4
            Assert.Equal(5, f.Heroes.Level[f.HeroSlot]);                     // unchanged
            Assert.Equal((baseDamage + Fixed.FromInt(8)).Raw, f.World.EffectiveAttackDamage[f.HeroEntity].Raw); // 4 × +2
        }

        // ── Max-level clamp ─────────────────────────────────────────────────────────

        [Fact]
        public void AtMaxLevel_FurtherXp_Saturates_NoOverflow_NoThrow()
        {
            // MaxLevel 2, extreme growth (100) so the thresholds stress the Fixed range; huge bounties.
            var f = MakeHero(level: 1, shareRadius: 100, maxLevel: 2, baseXp: 50, growth: Fixed.FromInt(100));

            for (int i = 0; i < 5; i++)
            {
                f.Deaths.Push(FixedVec3.Zero, Faction.Neutral, Fixed.FromInt(20000));
                f.Sys.Tick(f.World, Dt); // must never throw / overflow
            }

            Assert.Equal(2, f.Heroes.Level[f.HeroSlot]);                       // clamped at MaxLevel
            Assert.True(f.Heroes.Xp[f.HeroSlot] <= HeroXpSystem.XpCeiling);    // saturated, no overflow
            Assert.True(f.Heroes.Xp[f.HeroSlot] >= Fixed.Zero);               // never negative
        }

        // ── Playtest discard drops a grown run ──────────────────────────────────────

        [Fact]
        public void Discard_ReMintsAuthoredValues_DroppingAGrownRun()
        {
            var f = MakeHero(level: 1, shareRadius: 100);
            // Grow the hero mid-"match".
            f.Deaths.Push(FixedVec3.Zero, Faction.Neutral, Fixed.FromInt(120)); // >= 2 thresholds
            f.Sys.Tick(f.World, Dt);
            Assert.True(f.Heroes.Level[f.HeroSlot] > 1, "precondition: the hero grew this run");

            // Discard (PersistenceTestMode off) = clear the store + re-mint from the AUTHORED profile (level 1, 0 XP),
            // exactly the MainScene ClearForReset → LoadInto(authored) path. The grown values must be gone.
            f.Heroes.Clear();
            int reslot = f.Heroes.Mint(new HeroId(42), f.HeroEntity, level: 1, xp: Fixed.Zero,
                maxLevel: MaxLevel, baseXp: Fixed.FromInt(50), xpGrowth: Fixed.One, xpShareRadius: Fixed.FromInt(100),
                healthPerLevel: Fixed.FromInt(10), damagePerLevel: Fixed.FromInt(2), armorPerLevel: Fixed.FromInt(1));

            Assert.Equal(1, f.Heroes.Level[reslot]);
            Assert.Equal(Fixed.Zero.Raw, f.Heroes.Xp[reslot].Raw);
            Assert.Equal(0, f.Heroes.GrowthStacksApplied[reslot]);
        }

        // ── Non-hitscan / non-auto-attack kill sources feed the XP runtime (reviewer verification gaps) ──────────

        [Fact]
        public void ProjectileKill_RecordsDeathIntoFeed_ForHeroXpCredit()
        {
            // Guards the ProjectileSystem→DeathFeed threading: a ranged (projectile-delivery) kill must record the
            // victim, or projectile-delivery heroes silently never earn XP. Reverting the `_deaths` arg fails this.
            var world  = new EntityWorld();
            var store  = new ProjectileStore();
            var deaths = new DeathFeed();
            var sys    = new ProjectileSystem(store, deaths: deaths);

            int enemy = world.Create(new FixedVec3(Fixed.FromInt(3), Fixed.Zero, Fixed.Zero),
                                     Faction.Neutral, Fixed.FromInt(1), Fixed.FromInt(3));
            world.XpBounty[enemy] = Fixed.FromInt(30);

            // A lethal shell aimed at the enemy; speed 1/tick over distance 3 CONVERGES (a fast shell overshoots the
            // 0.5 hit radius — the pre-existing ProjectileSystem non-convergence). Tick until it lands.
            store.Spawn(FixedVec3.Zero, enemy, world.Position[enemy], Fixed.FromInt(10),
                        DamageType.Normal, ArmorType.Unarmored, Faction.Player1, Fixed.One);
            for (int t = 0; t < 20 && world.IsAlive(enemy); t++)
                sys.Tick(world, Fixed.One);

            Assert.False(world.IsAlive(enemy));                       // the shell killed it
            Assert.Equal(1, deaths.Count);                            // …and recorded the death for the XP runtime
            DeathRecord rec = deaths.Get(0);
            Assert.Equal(Faction.Neutral, rec.Faction);
            Assert.Equal(Fixed.FromInt(30).Raw, rec.Bounty.Raw);
        }

        [Fact]
        public void AbilityDamageKill_RecordsDeathIntoFeed_ForHeroXpCredit()
        {
            // Guards the ability-damage (DamageEffect via EffectContext) threading: an enemy killed by an ability must
            // grant XP exactly like a hitscan/projectile kill (AC1 is not restricted to auto-attacks).
            var world    = new EntityWorld();
            var deaths   = new DeathFeed();
            var executor = new EffectExecutor();

            int caster = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            int enemy  = world.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.Zero),
                                      Faction.Neutral, Fixed.FromInt(1), Fixed.FromInt(3));
            world.XpBounty[enemy] = Fixed.FromInt(30);

            var graph = new DamageEffect(Fixed.FromInt(50), DamageType.Normal); // lethal vs 1 HP
            var ctx = new EffectContext(world, casterId: caster, primaryTargetId: enemy,
                                        casterFaction: Faction.Player1, DamageTable.Default, deaths: deaths);
            executor.Run(graph, in ctx);

            Assert.False(world.IsAlive(enemy));
            Assert.Equal(1, deaths.Count);
            Assert.Equal(Fixed.FromInt(30).Raw, deaths.Get(0).Bounty.Raw);
        }

        // ── Deploy path: LoadInto establishes the entity→hero link (without it the deployed hero never levels) ──────

        [Fact]
        public void LoadInto_WithWorld_EstablishesHeroIndexLink_AndTheHeroEarnsXp()
        {
            var world  = new EntityWorld();
            var heroes = new HeroStore();
            int ent = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));

            PlayerProfile profile = HeroProfileLoader.BuildProfile("hero#1", "hero_unit", "f", "H", null,
                level: 1, xp: Fixed.Zero, ShapeOf("hero.level", "hero.xp"));
            var placed = new[]
            {
                new HeroProfileLoader.PlacedHero(ent, "hero_unit",
                    MaxLevel: MaxLevel, BaseXp: Fixed.FromInt(50), XpGrowth: Fixed.One, XpShareRadius: Fixed.FromInt(100),
                    HealthPerLevel: Fixed.Zero, DamagePerLevel: Fixed.FromInt(2), ArmorPerLevel: Fixed.Zero),
            };

            int minted = HeroProfileLoader.LoadInto(heroes, placed, profile, log: null, world: world);
            Assert.Equal(1, minted);

            // The D-8 link is established (EntityWorld.HeroIndex is otherwise never populated).
            Assert.True(heroes.TryResolveRef(world.HeroIndex[ent], out int slot));
            Assert.Equal(ent, heroes.EntityId[slot]);

            // …and the linked hero actually earns XP through the runtime (the reason the link exists).
            var modSys = new ModifierSystem();
            var modifiers = new ModifierStore(world, modSys);
            modSys.AttachStore(modifiers);
            var deaths = new DeathFeed();
            var sys = new HeroXpSystem(heroes, modifiers, deaths);
            deaths.Push(FixedVec3.Zero, Faction.Neutral, Fixed.FromInt(60)); // >= the 50 threshold
            sys.Tick(world, Dt);
            Assert.Equal(2, heroes.Level[slot]); // gained XP in range → leveled
        }

        private static PlayerProfileShape ShapeOf(params string[] keys)
        {
            var m = new PersistenceManifest { Enabled = true };
            foreach (string k in keys) m.Attributes.Add(k);
            return m.DeriveProfileShape();
        }
    }
}
