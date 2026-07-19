#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Combat;             // HeroXpSystem, DeathFeed, RevivalRule(Runtime)
using ProjectChimera.Core;               // Fixed, HeroStore, HeroId, EntityWorld, ItemStore, FixedVec3
using ProjectChimera.Core.Definitions;   // HeroHarvestResolver, HeroProfileLoader, PlayerProfile, ItemRegistry
using ProjectChimera.Economy;            // BuildingStore, ResourceStore
using ProjectChimera.Effects;            // ModifierStore, ModifierSystem
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// DW-27 / DW-32 — Tier-1 (Godot-free) coverage of the extracted <see cref="HeroHarvestResolver"/>: the plain-data
    /// end-of-match harvest (<see cref="HeroHarvestResolver.Capture"/>) + the picker's has-vs-fallback resolution
    /// (<see cref="HeroHarvestResolver.ResolveProgress"/> / <see cref="HeroHarvestResolver.ResolveInventory"/>). Walks the
    /// spec I/O matrix and drives the REAL death path (EntityWorld + HeroStore + HeroXpSystem) so a fallen hero whose row
    /// stays <see cref="HeroStore.Alive"/> for persistence finalizes its grown Level/Xp through the manifest-shape
    /// <see cref="HeroProfileLoader.BuildProfile"/> (FR-7a) — the two regressions a green suite would otherwise miss:
    /// dropping the harvested value and re-persisting the level-1/0 placeholder (DW-27), and an unharvestable fallen
    /// hero (DW-32).
    /// </summary>
    public class HeroHarvestResolverTests
    {
        private static readonly Fixed GrownXp = Fixed.FromRaw(786432); // 12.0 in 16.16 — a "grew in a playtest" value

        // ── Helpers ────────────────────────────────────────────────────────────────────────

        /// <summary>A deployed profile carrying just the identity fields the resolver keys on (ProfileId → MintId, HeroDefId).</summary>
        private static PlayerProfile Profile(string profileId = "grommash#1", string heroDefId = "grommash") =>
            new PlayerProfile
            {
                ProfileId        = profileId,
                HeroDefId        = heroDefId,
                FactionId        = "orc_clans",
                DisplayName      = "Grommash",
                SignatureAbility = "chaos_strike",
            };

        /// <summary>A manifest shape carrying the given attribute keys — the same seam the picker Save/Overwrite uses.</summary>
        private static PlayerProfileShape ShapeOf(params string[] keys)
        {
            var m = new PersistenceManifest { Enabled = true };
            foreach (string k in keys) m.Attributes.Add(k);
            return m.DeriveProfileShape();
        }

        /// <summary>A HeroStore with a single live row minted at <paramref name="level"/>/<paramref name="xp"/> whose id equals
        /// <see cref="HeroProfileLoader.MintId"/> of <paramref name="profile"/> (so <see cref="HeroHarvestResolver.Capture"/> matches it).</summary>
        private static HeroStore StoreWithLiveHero(PlayerProfile profile, int level, Fixed xp)
        {
            var heroes = new HeroStore();
            heroes.Mint(HeroProfileLoader.MintId(profile), entityId: 0, level: level, xp: xp);
            return heroes;
        }

        // ── I/O matrix: Capture ──────────────────────────────────────────────────────────────

        [Fact]
        public void Capture_LiveHeroWithProgress_HarvestsLevelXpAndInventory()
        {
            PlayerProfile p = Profile();
            var heroes = new HeroStore();
            var items  = new ItemStore();
            var reg    = new ItemRegistry(new[]
            {
                new ItemDefinition { Id = "ring",   Charges = 0, MaxHealthDelta = Fixed.FromInt(50) },
                new ItemDefinition { Id = "potion", Charges = 3 },
            });
            int slot = heroes.Mint(HeroProfileLoader.MintId(p), entityId: 0, level: 5, xp: GrownXp);
            heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 0] = items.Create(reg.IndexOf("ring"),   0, FixedVec3.Zero);
            heroes.Inventory[slot * HeroStore.INVENTORY_SLOTS + 1] = items.Create(reg.IndexOf("potion"), 2, FixedVec3.Zero);

            HeroHarvestResolver.HeroHarvest h = HeroHarvestResolver.Capture(heroes, items, reg, p);

            Assert.True(h.Has);
            Assert.Equal("grommash", h.HeroDefId);
            Assert.Equal(5, h.Level);
            Assert.Equal(GrownXp.Raw, h.Xp.Raw);
            Assert.NotNull(h.Inventory);
            Assert.Equal(2, h.Inventory!.Count);
            Assert.Equal("ring", h.Inventory[0].ItemId);
            Assert.Equal("potion", h.Inventory[1].ItemId);
        }

        [Fact]
        public void Capture_NoDeployedProfile_ReturnsNone()
        {
            var heroes = new HeroStore();
            heroes.Mint(new HeroId(99), entityId: 0, level: 5, xp: GrownXp); // an unrelated live hero

            HeroHarvestResolver.HeroHarvest h = HeroHarvestResolver.Capture(heroes, null, null, null);

            // Full None shape (not just Has=false): a partially-populated "None" must not slip through.
            Assert.False(h.Has);
            Assert.Null(h.HeroDefId);
            Assert.Equal(0, h.Level);
            Assert.Equal(Fixed.Zero.Raw, h.Xp.Raw);
            Assert.Null(h.Inventory);
        }

        [Fact]
        public void Capture_NoMatchingLiveRow_ReturnsNone()
        {
            PlayerProfile p = Profile();
            var heroes = new HeroStore();
            heroes.Mint(new HeroId(1234), entityId: 0, level: 5, xp: GrownXp); // a DIFFERENT id than MintId(p)

            HeroHarvestResolver.HeroHarvest h = HeroHarvestResolver.Capture(heroes, null, null, p);

            Assert.False(h.Has);
            Assert.Null(h.Inventory);
        }

        [Fact]
        public void Capture_NullItemStores_HarvestsProgressWithNullInventory()
        {
            PlayerProfile p = Profile();
            HeroStore heroes = StoreWithLiveHero(p, level: 5, xp: GrownXp);

            HeroHarvestResolver.HeroHarvest h = HeroHarvestResolver.Capture(heroes, null, null, p);

            Assert.True(h.Has);
            Assert.Equal(5, h.Level);
            Assert.Equal(GrownXp.Raw, h.Xp.Raw);
            Assert.Null(h.Inventory); // no item stores supplied → inventory unresolved, not empty-list
        }

        // ── I/O matrix: ResolveProgress / ResolveInventory ───────────────────────────────────

        [Fact]
        public void ResolveProgress_HasMatchingDef_UsesHarvested_NotFallback()
        {
            var inv = new List<ProfileInventoryItem> { new("ring", 0) };
            var h = new HeroHarvestResolver.HeroHarvest(true, "grommash", 5, GrownXp, inv);

            (int level, Fixed xp) = HeroHarvestResolver.ResolveProgress(in h, "grommash", fallbackLevel: 1, fallbackXp: Fixed.Zero);

            Assert.Equal(5, level);
            Assert.Equal(GrownXp.Raw, xp.Raw);
            Assert.Same(inv, HeroHarvestResolver.ResolveInventory(in h, "grommash"));
        }

        [Fact]
        public void Resolve_HasMismatchedDef_FallsBack_InventoryNull()
        {
            var inv = new List<ProfileInventoryItem> { new("ring", 0) };
            var h = new HeroHarvestResolver.HeroHarvest(true, "grommash", 5, GrownXp, inv);

            (int level, Fixed xp) = HeroHarvestResolver.ResolveProgress(in h, "valla", fallbackLevel: 2, fallbackXp: Fixed.FromInt(7));

            Assert.Equal(2, level);
            Assert.Equal(Fixed.FromInt(7).Raw, xp.Raw);
            Assert.Null(HeroHarvestResolver.ResolveInventory(in h, "valla"));
        }

        [Fact]
        public void Resolve_None_FallsBack_InventoryNull()
        {
            HeroHarvestResolver.HeroHarvest h = HeroHarvestResolver.HeroHarvest.None;

            (int level, Fixed xp) = HeroHarvestResolver.ResolveProgress(in h, "grommash", fallbackLevel: 3, fallbackXp: Fixed.FromInt(9));

            Assert.Equal(3, level);
            Assert.Equal(Fixed.FromInt(9).Raw, xp.Raw);
            Assert.Null(HeroHarvestResolver.ResolveInventory(in h, "grommash"));
        }

        // ── DW-27: the picker Save flow finalizes the harvested value, NOT the (1, 0) placeholder ────

        [Fact]
        public void SaveFlow_HasHarvest_FinalizesGrownLevelXp_NotLevel1Placeholder()
        {
            // Mirror HeroPickerOverlay.OnSavePressed: Capture → ResolveProgress(fallback 1/0) → BuildProfile(shape).
            PlayerProfile deployed = Profile();
            HeroStore heroes = StoreWithLiveHero(deployed, level: 5, xp: GrownXp);

            HeroHarvestResolver.HeroHarvest h = HeroHarvestResolver.Capture(heroes, null, null, deployed);
            (int level, Fixed xp) = HeroHarvestResolver.ResolveProgress(in h, deployed.HeroDefId, fallbackLevel: 1, fallbackXp: Fixed.Zero);

            PlayerProfile saved = HeroProfileLoader.BuildProfile(
                "grommash#2", deployed.HeroDefId, deployed.FactionId, deployed.DisplayName, deployed.SignatureAbility,
                level, xp, ShapeOf("hero.level", "hero.xp"));

            Assert.Equal(5, saved.Level);             // the grown value, NOT the authored level-1 placeholder (DW-27)
            Assert.Equal(GrownXp.Raw, saved.Xp.Raw);  // the grown xp, NOT 0
        }

        [Fact]
        public void SaveFlow_NoHarvest_FallsBackToAuthoredPlaceholder()
        {
            // With no live hero, the Save flow must fall back to the authored (1, 0) — proves the guard is real, not tautological.
            PlayerProfile deployed = Profile();
            var heroes = new HeroStore(); // nothing live

            HeroHarvestResolver.HeroHarvest h = HeroHarvestResolver.Capture(heroes, null, null, deployed);
            (int level, Fixed xp) = HeroHarvestResolver.ResolveProgress(in h, deployed.HeroDefId, fallbackLevel: 1, fallbackXp: Fixed.Zero);

            PlayerProfile saved = HeroProfileLoader.BuildProfile(
                "grommash#2", deployed.HeroDefId, deployed.FactionId, deployed.DisplayName, deployed.SignatureAbility,
                level, xp, ShapeOf("hero.level", "hero.xp"));

            Assert.Equal(1, saved.Level);
            Assert.Equal(Fixed.Zero.Raw, saved.Xp.Raw);
        }

        // ── DW-32 / FR-7a: a FALLEN hero (row stays Alive) finalizes its grown attributes end-to-end ──

        [Theory]
        [InlineData(false)] // revival disabled → off-field (Alive3_14 false), NOT awaiting, row stays Alive for persistence
        [InlineData(true)]  // revival enabled  → awaiting revival (Alive3_14 false, AwaitingRevival true), row stays Alive
        public void FallenHero_RowStaysAlive_CaptureRoutedThroughBuildProfile_FinalizesGrownLevelXp(bool revivalEnabled)
        {
            PlayerProfile deployed = Profile();
            var fx = MakeFallenHeroFixture(deployed, level: 5, xp: GrownXp, revivalEnabled: revivalEnabled);

            // Preconditions: the REAL death path left the row Alive (persistable) but off the field.
            Assert.True(fx.Heroes.Alive[fx.HeroSlot]);            // persisted row NOT recycled
            Assert.False(fx.Heroes.Alive3_14[fx.HeroSlot]);       // off the field (fell)
            Assert.Equal(revivalEnabled, fx.Heroes.AwaitingRevival[fx.HeroSlot]);

            // Capture keys on Alive (NOT Alive3_14), so the fallen hero is still harvestable.
            HeroHarvestResolver.HeroHarvest h = HeroHarvestResolver.Capture(fx.Heroes, null, null, deployed);
            Assert.True(h.Has);
            Assert.Equal(5, h.Level);
            Assert.Equal(GrownXp.Raw, h.Xp.Raw);

            // Route through the manifest-shape BuildProfile exactly as the picker does → grown values finalized (FR-7a).
            (int level, Fixed xp) = HeroHarvestResolver.ResolveProgress(in h, deployed.HeroDefId, fallbackLevel: 1, fallbackXp: Fixed.Zero);
            PlayerProfile finalized = HeroProfileLoader.BuildProfile(
                "grommash#3", deployed.HeroDefId, deployed.FactionId, deployed.DisplayName, deployed.SignatureAbility,
                level, xp, ShapeOf("hero.level", "hero.xp"));

            Assert.Equal(5, finalized.Level);            // grown, not the authored placeholder
            Assert.Equal(GrownXp.Raw, finalized.Xp.Raw);
        }

        // ── Fallen-hero fixture (a minimal EntityWorld + HeroStore + HeroXpSystem, as in HeroRevivalTests) ──

        private sealed class FallenHeroFixture
        {
            public HeroStore Heroes = null!;
            public int HeroSlot;
        }

        private static UnitDefinition MakeHeroDef() => new UnitDefinition
        {
            Id = "grommash", Category = "Melee", IsHero = true,
            Hp = 100, Speed = 3, AttackDamage = 5, AttackRange = 5, AttackSpeed = 1,
            Hero = new HeroDefinition { MaxLevel = 10, BaseXp = 50, XpGrowth = 1f,
                                        HealthPerLevel = 10, DamagePerLevel = 2, ArmorPerLevel = 1 },
        };

        /// <summary>Mint a single hero (id == MintId(profile)) into a live sim at the given grown Level/Xp, then drive the
        /// REAL death path (destroy the entity + tick HeroXpSystem) so the row transitions to fallen while staying Alive.</summary>
        private static FallenHeroFixture MakeFallenHeroFixture(PlayerProfile profile, int level, Fixed xp, bool revivalEnabled)
        {
            var world     = new EntityWorld();
            var modSys    = new ModifierSystem();
            var modifiers = new ModifierStore(world, modSys);
            modSys.AttachStore(modifiers);
            var deaths    = new DeathFeed();
            var heroes    = new HeroStore();
            var buildings = new BuildingStore();
            var events    = new CombatEventQueue();

            var rule    = new RevivalRule { Enabled = revivalEnabled, TimeBaseSeconds = 5f, ReviveHpFraction = 0.5f };
            var revival = new RevivalRuleRuntime(rule);

            var heroDef = MakeHeroDef();
            int ent = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            world.ApplyUnitDefinition(ent, heroDef);

            int slot = heroes.Mint(HeroProfileLoader.MintId(profile), ent, level, xp,
                maxLevel: 10, baseXp: Fixed.FromInt(50), xpGrowth: Fixed.One, xpShareRadius: Fixed.FromInt(30),
                healthPerLevel: Fixed.FromInt(10), damagePerLevel: Fixed.FromInt(2), armorPerLevel: Fixed.FromInt(1),
                sourceDef: heroDef, ownerFaction: Faction.Player1);
            world.HeroIndex[ent] = heroes.PackRef(slot);

            Func<UnitDefinition, Faction, Fixed, Fixed, int> spawn = (def, fac, x, z) =>
            {
                int id = world.Create(new FixedVec3(x, Fixed.Zero, z), fac, Fixed.FromFloat(def.Hp), Fixed.FromFloat(def.Speed));
                if (id >= 0) world.ApplyUnitDefinition(id, def);
                return id;
            };

            var sys = new HeroXpSystem(heroes, modifiers, deaths, buildings, revival, spawn, events);

            // REAL death path: destroy the entity (as combat would) + tick so death-detection transitions the persisted row.
            world.Destroy(ent);
            sys.Tick(world, SimulationLoop.FixedDt);

            return new FallenHeroFixture { Heroes = heroes, HeroSlot = slot };
        }
    }
}
