#nullable enable
using System.Collections.Generic;
using System.IO;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Persistence;
using ProjectChimera.Core.Sim;
using ProjectChimera.Effects;
using ProjectChimera.Core.Skirmish;
using ProjectChimera.Multiplayer;
using ProjectChimera.Multiplayer.Server;
using Xunit;

namespace ProjectChimera.Sim.Tests.Combat
{
    /// <summary>
    /// Story 15-24e — the creator-toggled hero attribute SPEND MODE, Godot-free.
    ///
    /// <para>Covers every row of the story's I/O matrix (default content, auto level-up, player-spent level-up,
    /// player-spent on an AI slot, a valid spend, a spend with zero points, a spend on a foreign hero, an unknown
    /// attribute index, a multi-level tick, a save/load mid-bank, an unknown authored token, and a hero holding
    /// points across death+revive) plus the four proofs the spec names separately: two identical runs are
    /// byte-identical, command 26 survives the wire round-trip, and the AI arm and the human arm are compared on
    /// the SAME model at the same tick.</para>
    ///
    /// <para>The fixture is the WC3 shape the shipped preset uses: strength→max_health 25/pt, agility→armor
    /// 0.3/pt, intelligence→max_energy 15/pt, primary→attack_damage 1/pt, over a hero with base
    /// {str 20, agi 10, int 16} and per-level {str 2, agi 1, int 1.5}. Every asserted number is that authored
    /// arithmetic, never a captured value.</para>
    /// </summary>
    public class SpendModeTests
    {
        private static readonly Fixed Dt = SimulationLoop.FixedDt;

        // ── Authored fixture ────────────────────────────────────────────────────────────────────────────────

        private static AttributeModelDefinition Wc3Model(string? spendMode) => new AttributeModelDefinition
        {
            SpendMode = spendMode,
            Attributes = new List<AttributeDeclaration>
            {
                new() { Id = "strength", Name = "Strength" },
                new() { Id = "agility", Name = "Agility" },
                new() { Id = "intelligence", Name = "Intelligence" },
            },
            Derived = new List<DerivedStatRule>
            {
                new() { Attribute = "strength", Stat = "max_health", PerPoint = 25f },
                new() { Attribute = "agility", Stat = "armor", PerPoint = 0.3f },
                new() { Attribute = "intelligence", Stat = "max_energy", PerPoint = 15f },
                new() { Attribute = "primary", Stat = "attack_damage", PerPoint = 1f },
            },
        };

        private static HeroAttributesDefinition HeroAttrs() => new HeroAttributesDefinition
        {
            Primary  = "strength",
            Base     = new Dictionary<string, float> { ["strength"] = 20f, ["agility"] = 10f, ["intelligence"] = 16f },
            PerLevel = new Dictionary<string, float> { ["strength"] = 2f, ["agility"] = 1f, ["intelligence"] = 1.5f },
        };

        private const int STRENGTH = 0, AGILITY = 1, INTELLIGENCE = 2;

        private sealed class Fixture
        {
            public EntityWorld World = null!;
            public HeroStore Heroes = null!;
            public ModifierStore Modifiers = null!;
            public DeathFeed Deaths = null!;
            public CombatEventQueue Events = null!;
            public HeroXpSystem Sys = null!;
            public BuildingStore Buildings = null!;
            public ResourceStore Resources = null!;
            public FactionRegistry Registry = null!;
            public int HeroEntity;
            public int HeroSlot;

            public uint Checksum() => SimChecksum.Compute(World, Buildings, Resources, Registry, Modifiers, Heroes);
        }

        /// <summary>One Player1 hero at level 1 under <paramref name="spendMode"/>, curve baseXp 50 × growth 1.0 so
        /// every 50 XP is exactly one level, flat growth +10 hp / +2 dmg / +1 armor per level.</summary>
        private static Fixture MakeHero(string? spendMode, Faction faction = Faction.Player1, int aiMask = 0,
                                        int maxLevel = 5, int level = 1)
        {
            var world = new EntityWorld();
            var modSys = new ModifierSystem();
            var modifiers = new ModifierStore(world, modSys);
            modSys.AttachStore(modifiers);
            var deaths = new DeathFeed();
            var heroes = new HeroStore { AiControlMask = aiMask };
            var events = new CombatEventQueue();

            int ent = world.Create(FixedVec3.Zero, faction, Fixed.FromInt(100), Fixed.FromInt(3));
            world.BaseAttackDamage[ent] = Fixed.FromInt(5);
            world.EffectiveAttackDamage[ent] = Fixed.FromInt(5);

            AttributeModelDefinition model = Wc3Model(spendMode);
            HeroAttributesDefinition attrs = HeroAttrs();
            var (cBase, cPerLevel) = HeroAttributeResolver.Resolve(model, attrs);
            var def = new UnitDefinition
            {
                Id = "hero", DisplayName = "Hero", Category = "Melee", IsHero = true,
                Hp = 100, Speed = 3, AttackDamage = 5, AttackRange = 2, AttackSpeed = 1,
                Hero = new HeroDefinition { MaxLevel = maxLevel, BaseXp = 50f, XpGrowth = 1f, Attributes = attrs },
            };

            int slot = heroes.Mint(new HeroId(42), ent, level, Fixed.Zero,
                maxLevel: maxLevel, baseXp: Fixed.FromInt(50), xpGrowth: Fixed.One, xpShareRadius: Fixed.FromInt(10),
                healthPerLevel: Fixed.FromInt(10), damagePerLevel: Fixed.FromInt(2), armorPerLevel: Fixed.FromInt(1),
                sourceDef: def, ownerFaction: faction,
                attrStatBase: cBase, attrStatPerLevel: cPerLevel, attrModel: model);
            world.HeroIndex[ent] = heroes.PackRef(slot);

            return new Fixture
            {
                World = world, Heroes = heroes, Modifiers = modifiers, Deaths = deaths, Events = events,
                Sys = new HeroXpSystem(heroes, modifiers, deaths, events: events),
                Buildings = new BuildingStore(), Resources = new ResourceStore(Fixed.Zero),
                Registry = new FactionRegistry(2),
                HeroEntity = ent, HeroSlot = slot,
            };
        }

        /// <summary>Credit enough XP for <paramref name="levels"/> level-ups, then tick once.</summary>
        private static void LevelUp(Fixture f, int levels = 1)
        {
            f.Heroes.Xp[f.HeroSlot] = f.Heroes.Xp[f.HeroSlot] + Fixed.FromInt(50 * levels);
            f.Sys.Tick(f.World, Dt);
        }

        private static UnitOrder SpendOrder(EntityWorld world, int heroEntity, int attrIndex)
            => new UnitOrder(world.PackRef(heroEntity), UnitCommand.SpendAttributePoint,
                             Fixed.FromRaw(attrIndex), Fixed.Zero);

        // ── Matrix row 1: default content parses to auto and folds nothing ───────────────────────────────────

        [Fact]
        public void NoAuthoredSpendMode_ParsesToAuto_AndBothNewLanesStayZero()
        {
            var model = Wc3Model(null);
            Assert.Null(model.SpendMode);
            Assert.Equal(AttributeSpendMode.Auto, model.ParsedSpendMode);

            var f = MakeHero(null);
            f.Sys.Tick(f.World, Dt);
            LevelUp(f, 3);

            Assert.Equal(4, f.Heroes.Level[f.HeroSlot]);
            Assert.Equal(0, f.Heroes.UnspentPoints[f.HeroSlot]);
            for (int s = 0; s < AttributeStats.Count; s++)
                Assert.Equal(0, f.Heroes.AttrStatSpent[f.HeroSlot * AttributeStats.Count + s].Raw);
        }

        /// <summary>The bound the whole story's golden-neutrality rests on: with both lanes at zero the v29 arms
        /// contribute NOTHING, so a store carrying a default hero folds exactly as it would with the lanes absent.
        /// Proved by perturbing each lane and watching the fold move — a fold that never moved would be vacuous.</summary>
        [Fact]
        public void SimChecksumFold_IsBounded_ZeroLanesFoldNothing_NonZeroMoves()
        {
            var f = MakeHero(null);
            f.Sys.Tick(f.World, Dt);
            uint atZero = f.Checksum();

            f.Heroes.UnspentPoints[f.HeroSlot] = 2;
            uint withPoints = f.Checksum();
            f.Heroes.UnspentPoints[f.HeroSlot] = 0;
            Assert.Equal(atZero, f.Checksum());               // back to the identity value — the arm is truly gated

            f.Heroes.AttrStatSpent[f.HeroSlot * AttributeStats.Count + AttributeStats.MaxHealth] = Fixed.FromInt(25);
            uint withSpent = f.Checksum();

            Assert.NotEqual(atZero, withPoints);
            Assert.NotEqual(atZero, withSpent);
            Assert.NotEqual(withPoints, withSpent);
        }

        /// <summary>Two stats holding the SAME spent value must not alias — the fold mixes the stat index too.</summary>
        [Fact]
        public void SpentLaneFold_MixesTheStatIndex_SoEqualValuesCannotAlias()
        {
            var f = MakeHero(null);
            f.Sys.Tick(f.World, Dt);
            int aBase = f.HeroSlot * AttributeStats.Count;

            f.Heroes.AttrStatSpent[aBase + AttributeStats.MaxHealth] = Fixed.FromInt(7);
            uint onHealth = f.Checksum();
            f.Heroes.AttrStatSpent[aBase + AttributeStats.MaxHealth] = Fixed.Zero;
            f.Heroes.AttrStatSpent[aBase + AttributeStats.Armor] = Fixed.FromInt(7);
            uint onArmor = f.Checksum();

            Assert.NotEqual(onHealth, onArmor);
        }

        // ── Matrix rows 2-4: the three level-up arms, compared on the SAME model ─────────────────────────────

        [Fact]
        public void AutoMode_LevelUp_AppliesTheAttributeVectorImmediately_AndBanksNothing()
        {
            var f = MakeHero("auto");
            f.Sys.Tick(f.World, Dt);
            // Level 1: base contributions only — 100 authored hp + 25×20 str; 5 authored damage + 1×20 primary.
            Assert.Equal(Fixed.FromInt(600).Raw, f.World.EffectiveMaxHealth[f.HeroEntity].Raw);
            Assert.Equal(Fixed.FromInt(25).Raw, f.World.EffectiveAttackDamage[f.HeroEntity].Raw);

            LevelUp(f);

            // Level 2 under auto: flat growth (+10 hp, +2 dmg) PLUS the per-level attribute vector (+50 hp from
            // 25×2 str/level, +2 dmg from the primary rule).
            Assert.Equal(2, f.Heroes.Level[f.HeroSlot]);
            Assert.Equal(0, f.Heroes.UnspentPoints[f.HeroSlot]);
            Assert.Equal(Fixed.FromInt(660).Raw, f.World.EffectiveMaxHealth[f.HeroEntity].Raw);
            Assert.Equal(Fixed.FromInt(29).Raw, f.World.EffectiveAttackDamage[f.HeroEntity].Raw);
        }

        [Fact]
        public void PlayerSpentMode_LevelUp_BanksAPoint_WithholdsOnlyTheAttributeTerm()
        {
            var f = MakeHero("player_spent");
            f.Sys.Tick(f.World, Dt);
            Assert.Equal(Fixed.FromInt(600).Raw, f.World.EffectiveMaxHealth[f.HeroEntity].Raw); // BASE still applies

            LevelUp(f);

            Assert.Equal(2, f.Heroes.Level[f.HeroSlot]);
            Assert.Equal(1, f.Heroes.UnspentPoints[f.HeroSlot]);
            // The FLAT hero stat-growth lanes are NOT attributes and keep applying: +10 hp, +2 dmg. The attribute
            // per-level vector (+50 hp / +2 dmg) is withheld.
            Assert.Equal(Fixed.FromInt(610).Raw, f.World.EffectiveMaxHealth[f.HeroEntity].Raw);
            Assert.Equal(Fixed.FromInt(27).Raw, f.World.EffectiveAttackDamage[f.HeroEntity].Raw);
        }

        /// <summary>The two arms on the SAME authored model, compared at the same tick — the story's headline AC,
        /// and the shape the in-engine gate repeats. Only the mode differs between them.</summary>
        [Fact]
        public void BothModeArms_OnTheSameModel_DivergeExactlyByTheAttributeTerm()
        {
            var auto  = MakeHero("auto");
            var spent = MakeHero("player_spent");
            LevelUp(auto);
            LevelUp(spent);

            Fixed autoHp  = auto.World.EffectiveMaxHealth[auto.HeroEntity];
            Fixed spentHp = spent.World.EffectiveMaxHealth[spent.HeroEntity];
            Assert.Equal(Fixed.FromInt(50).Raw, (autoHp - spentHp).Raw); // exactly the withheld 25 × 2 str/level
            Assert.Equal(0, auto.Heroes.UnspentPoints[auto.HeroSlot]);
            Assert.Equal(1, spent.Heroes.UnspentPoints[spent.HeroSlot]);
        }

        [Fact]
        public void PlayerSpentModel_OnAnAiControlledSlot_BehavesExactlyAsAuto()
        {
            // The SAME authored model and the SAME faction; only the match-agreed AiControlPlan mask differs.
            var human = MakeHero("player_spent", Faction.Player2, aiMask: 0);
            var ai    = MakeHero("player_spent", Faction.Player2, aiMask: 1 << (int)Faction.Player2);
            LevelUp(human);
            LevelUp(ai);

            Assert.Equal(1, human.Heroes.UnspentPoints[human.HeroSlot]);
            Assert.Equal(0, ai.Heroes.UnspentPoints[ai.HeroSlot]);          // an AI never banks
            Assert.Equal(Fixed.FromInt(660).Raw, ai.World.EffectiveMaxHealth[ai.HeroEntity].Raw);    // the auto number
            Assert.Equal(Fixed.FromInt(610).Raw, human.World.EffectiveMaxHealth[human.HeroEntity].Raw);
            Assert.False(ai.Heroes.IsPlayerSpent(ai.HeroSlot));
            Assert.True(human.Heroes.IsPlayerSpent(human.HeroSlot));
        }

        // ── Matrix row 5: a valid spend ───────────────────────────────────────────────────────────────────────

        [Fact]
        public void SpendOrder_DecrementsThePoint_CreditsTheSpentLane_AndRecomputesEffectiveStats()
        {
            var f = MakeHero("player_spent");
            LevelUp(f);
            Fixed hpBefore  = f.World.EffectiveMaxHealth[f.HeroEntity];
            Fixed dmgBefore = f.World.EffectiveAttackDamage[f.HeroEntity];

            OrderApplier.Apply(f.World, SpendOrder(f.World, f.HeroEntity, STRENGTH), Faction.Player1, heroXp: f.Sys);

            Assert.Equal(0, f.Heroes.UnspentPoints[f.HeroSlot]);
            int aBase = f.HeroSlot * AttributeStats.Count;
            // One point of strength = the model's own numbers: +25 max_health, +1 attack_damage (primary = strength).
            Assert.Equal(Fixed.FromInt(25).Raw, f.Heroes.AttrStatSpent[aBase + AttributeStats.MaxHealth].Raw);
            Assert.Equal(Fixed.FromInt(1).Raw, f.Heroes.AttrStatSpent[aBase + AttributeStats.AttackDamage].Raw);
            Assert.Equal((hpBefore + Fixed.FromInt(25)).Raw, f.World.EffectiveMaxHealth[f.HeroEntity].Raw);
            Assert.Equal((dmgBefore + Fixed.FromInt(1)).Raw, f.World.EffectiveAttackDamage[f.HeroEntity].Raw);
        }

        [Fact]
        public void RepeatedSpends_Accumulate_OnOneModifierSlot()
        {
            var f = MakeHero("player_spent");
            LevelUp(f, 3);
            Assert.Equal(3, f.Heroes.UnspentPoints[f.HeroSlot]);
            Fixed hpBefore = f.World.EffectiveMaxHealth[f.HeroEntity];

            for (int i = 0; i < 3; i++)
                OrderApplier.Apply(f.World, SpendOrder(f.World, f.HeroEntity, STRENGTH), Faction.Player1, heroXp: f.Sys);

            Assert.Equal(0, f.Heroes.UnspentPoints[f.HeroSlot]);
            Assert.Equal((hpBefore + Fixed.FromInt(75)).Raw, f.World.EffectiveMaxHealth[f.HeroEntity].Raw);
            // ONE slot for the whole spent total (the swap idiom), never one modifier per point.
            Assert.Equal(1, CountModifier(f, HeroXpSystem.HeroSpentModifierId));
        }

        /// <summary>DW-85: a spend RE-STATES a total, so the remove+re-apply must not act as a free heal.</summary>
        [Fact]
        public void Spend_DoesNotHealTheHero_TheDw85Snapshot()
        {
            var f = MakeHero("player_spent");
            LevelUp(f, 2);
            f.World.Health[f.HeroEntity] = Fixed.FromInt(120); // wounded, well below the grown ceiling
            OrderApplier.Apply(f.World, SpendOrder(f.World, f.HeroEntity, STRENGTH), Faction.Player1, heroXp: f.Sys);
            Assert.Equal(Fixed.FromInt(120).Raw, f.World.Health[f.HeroEntity].Raw);
            OrderApplier.Apply(f.World, SpendOrder(f.World, f.HeroEntity, STRENGTH), Faction.Player1, heroXp: f.Sys);
            Assert.Equal(Fixed.FromInt(120).Raw, f.World.Health[f.HeroEntity].Raw);
        }

        // ── Matrix rows 6-8: the three denials ────────────────────────────────────────────────────────────────

        [Fact]
        public void SpendWithZeroBankedPoints_IsASilentNoOp()
        {
            var f = MakeHero("player_spent");
            f.Sys.Tick(f.World, Dt);
            Assert.Equal(0, f.Heroes.UnspentPoints[f.HeroSlot]);
            uint before = f.Checksum();

            OrderApplier.Apply(f.World, SpendOrder(f.World, f.HeroEntity, STRENGTH), Faction.Player1, heroXp: f.Sys);

            Assert.Equal(before, f.Checksum());
            Assert.Equal(0, f.Heroes.UnspentPoints[f.HeroSlot]);
        }

        [Fact]
        public void SpendOnAnotherFactionsHero_IsRejectedByTheOwnershipGuard()
        {
            var f = MakeHero("player_spent"); // the hero is Player1's
            LevelUp(f);
            uint before = f.Checksum();

            // A Player2 command stream naming a Player1 hero — the 3.15 anti-cheat case, and the reason this
            // command sits in the POST-guard arm.
            OrderApplier.Apply(f.World, SpendOrder(f.World, f.HeroEntity, STRENGTH), Faction.Player2, heroXp: f.Sys);

            Assert.Equal(1, f.Heroes.UnspentPoints[f.HeroSlot]); // untouched
            Assert.Equal(before, f.Checksum());
        }

        [Theory]
        [InlineData(3)]     // one past the last declared attribute
        [InlineData(99)]
        [InlineData(-1)]
        public void SpendOnAnUnknownAttributeIndex_IsASilentNoOp(int index)
        {
            var f = MakeHero("player_spent");
            LevelUp(f);
            uint before = f.Checksum();

            OrderApplier.Apply(f.World, SpendOrder(f.World, f.HeroEntity, index), Faction.Player1, heroXp: f.Sys);

            Assert.Equal(1, f.Heroes.UnspentPoints[f.HeroSlot]);
            Assert.Equal(before, f.Checksum());
        }

        /// <summary>An attribute the model derives NOTHING from must not silently eat the point — there is no
        /// respec, so a consumed point with no effect would be unrecoverable.</summary>
        [Fact]
        public void SpendOnAnAttributeWithNoDerivedRule_RefusesRatherThanBurningThePoint()
        {
            var f = MakeHero("player_spent");
            f.Heroes.AttrModelOf[f.HeroSlot]!.Attributes!.Add(new AttributeDeclaration { Id = "luck", Name = "Luck" });
            LevelUp(f);

            OrderApplier.Apply(f.World, SpendOrder(f.World, f.HeroEntity, 3), Faction.Player1, heroXp: f.Sys);

            Assert.Equal(1, f.Heroes.UnspentPoints[f.HeroSlot]);
        }

        [Fact]
        public void SpendOnAnAutoModel_IsASilentNoOp_EvenWithAHandPlantedPoint()
        {
            var f = MakeHero("auto");
            f.Sys.Tick(f.World, Dt);
            f.Heroes.UnspentPoints[f.HeroSlot] = 1; // could only arrive by corruption; the mode gate still refuses
            OrderApplier.Apply(f.World, SpendOrder(f.World, f.HeroEntity, STRENGTH), Faction.Player1, heroXp: f.Sys);
            Assert.Equal(1, f.Heroes.UnspentPoints[f.HeroSlot]);
        }

        [Fact]
        public void SpendOrder_WithNoHeroRuntimeWired_IsADeterministicNoOp()
        {
            var f = MakeHero("player_spent");
            LevelUp(f);
            OrderApplier.Apply(f.World, SpendOrder(f.World, f.HeroEntity, STRENGTH), Faction.Player1); // heroXp: null
            Assert.Equal(1, f.Heroes.UnspentPoints[f.HeroSlot]);
        }

        [Fact]
        public void SpendOrder_OnANonHeroEntity_IsASilentNoOp()
        {
            var f = MakeHero("player_spent");
            LevelUp(f);
            int plain = f.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(50), Fixed.FromInt(3));
            uint before = f.Checksum();
            OrderApplier.Apply(f.World, SpendOrder(f.World, plain, STRENGTH), Faction.Player1, heroXp: f.Sys);
            Assert.Equal(before, f.Checksum());
        }

        // ── Matrix row 9: a multi-level tick ──────────────────────────────────────────────────────────────────

        [Fact]
        public void MultiLevelTick_BanksExactlyOnePointPerThresholdCrossed_AndAppliesNoVector()
        {
            var f = MakeHero("player_spent");
            f.Sys.Tick(f.World, Dt);
            Fixed hpAtLevel1 = f.World.EffectiveMaxHealth[f.HeroEntity];

            LevelUp(f, 3); // one XP burst across three thresholds

            Assert.Equal(4, f.Heroes.Level[f.HeroSlot]);
            Assert.Equal(3, f.Heroes.UnspentPoints[f.HeroSlot]);
            // Only the flat lane moved: 3 levels × +10 hp.
            Assert.Equal((hpAtLevel1 + Fixed.FromInt(30)).Raw, f.World.EffectiveMaxHealth[f.HeroEntity].Raw);
        }

        // ── Matrix row 12: banked points are HERO identity, not entity state ─────────────────────────────────

        [Fact]
        public void BankedPointsAndSpentTotals_SurviveTheHerosDeathAndRelink()
        {
            var f = MakeHero("player_spent");
            LevelUp(f, 2);
            OrderApplier.Apply(f.World, SpendOrder(f.World, f.HeroEntity, STRENGTH), Faction.Player1, heroXp: f.Sys);
            Assert.Equal(1, f.Heroes.UnspentPoints[f.HeroSlot]);
            int aBase = f.HeroSlot * AttributeStats.Count;
            int spentHpRaw = f.Heroes.AttrStatSpent[aBase + AttributeStats.MaxHealth].Raw;
            Assert.Equal(Fixed.FromInt(25).Raw, spentHpRaw);

            // The entity dies; the persisted HeroStore row (and its two lanes) is untouched.
            f.World.Destroy(f.HeroEntity);
            f.Sys.Tick(f.World, Dt);
            Assert.Equal(1, f.Heroes.UnspentPoints[f.HeroSlot]);
            Assert.Equal(spentHpRaw, f.Heroes.AttrStatSpent[aBase + AttributeStats.MaxHealth].Raw);

            // A revive re-links a FRESH entity to the same row and resets GrowthStacksApplied — the same three
            // writes RespawnHero performs. ReconcileGrowth must then re-materialize the SPENT total onto it too.
            int revived = f.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            f.World.BaseAttackDamage[revived] = Fixed.FromInt(5);
            f.World.EffectiveAttackDamage[revived] = Fixed.FromInt(5);
            f.Heroes.EntityId[f.HeroSlot] = revived;
            f.World.HeroIndex[revived] = f.Heroes.PackRef(f.HeroSlot);
            f.Heroes.GrowthStacksApplied[f.HeroSlot] = 0;
            f.Sys.Tick(f.World, Dt);

            // base 500 + flat 2×10 + spent 25 on top of the authored 100 hp.
            Assert.Equal(Fixed.FromInt(645).Raw, f.World.EffectiveMaxHealth[revived].Raw);
            Assert.Equal(1, f.Heroes.UnspentPoints[f.HeroSlot]);
        }

        // ── Matrix row 10: save/load mid-bank ────────────────────────────────────────────────────────────────

        [Fact]
        public void SaveLoadMidBank_RoundTripsBothLanes_AndRestoresIdenticalEffectiveStats()
        {
            FactionDefinition faction = SaveFaction();
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = faction;

            SimulationHost host = NewHost(faction);
            (int ent, int slot) = MintInto(host, faction);
            host.StepOnce();
            // Bank two points, spend one into strength — the matrix's "2 unspent, spent in STR" state.
            host.Heroes.Xp[slot] = Fixed.FromInt(150);
            host.StepOnce();
            OrderApplier.Apply(host.World, SpendOrder(host.World, ent, STRENGTH), Faction.Player1, heroXp: host.HeroXp);
            Assert.Equal(2, host.Heroes.UnspentPoints[slot]);
            Fixed hpBefore  = host.World.EffectiveMaxHealth[ent];
            Fixed dmgBefore = host.World.EffectiveAttackDamage[ent];
            int spentHpRaw  = host.Heroes.AttrStatSpent[slot * AttributeStats.Count + AttributeStats.MaxHealth].Raw;
            Assert.Equal(Fixed.FromInt(25).Raw, spentHpRaw);

            var table = CanonicalEffectDescriptorTable.Build(host.AbilityRegistry, host.ItemRegistry);
            SaveGameState state = SaveGameState.CaptureFrom(host, table);
            using var ms = new MemoryStream();
            SaveGameFile.Write(ms, state, new SaveGameHeaderData
            {
                CanonicalModelHash = 0, ContentHash = 0, Tick = host.CurrentTick, MapId = "spend",
                Slots = new List<ProjectChimera.Core.Skirmish.SetupSlot>(),
            });

            SimulationHost loaded = NewHost(faction);
            using var read = new MemoryStream(ms.ToArray());
            (SaveGameHeaderData _, SaveGameState st) = SaveGameFile.Read(read);
            st.RestoreInto(loaded, CanonicalEffectDescriptorTable.Build(loaded.AbilityRegistry, loaded.ItemRegistry), slotDefs);

            Assert.Equal(2, loaded.Heroes.UnspentPoints[slot]);
            Assert.Equal(spentHpRaw, loaded.Heroes.AttrStatSpent[slot * AttributeStats.Count + AttributeStats.MaxHealth].Raw);
            Assert.Equal(hpBefore.Raw,  loaded.World.EffectiveMaxHealth[ent].Raw);
            Assert.Equal(dmgBefore.Raw, loaded.World.EffectiveAttackDamage[ent].Raw);
            // The restored model is re-resolved from the slot faction def, so the MODE survives too.
            Assert.True(loaded.Heroes.IsPlayerSpent(slot));

            // …and the resumed host produces a BYTE-IDENTICAL checksum stream against the uninterrupted run —
            // the story's acceptance criterion, not merely equal stats at the resume instant.
            var refSeq = new List<uint>();
            var resumeSeq = new List<uint>();
            host.SetChecksumSink((_, h) => refSeq.Add(h));
            loaded.SetChecksumSink((_, h) => resumeSeq.Add(h));
            for (int i = 0; i < 120; i++) { host.StepOnce(); loaded.StepOnce(); }
            Assert.NotEmpty(refSeq);
            Assert.Equal(refSeq, resumeSeq);
        }

        /// <summary>The lane-length contract is fail-closed: a truncated spent ring is rejected, never restored
        /// half-length (which would silently zero part of a hero's allocation).</summary>
        [Fact]
        public void SaveWithAMismatchedSpentLaneLength_FailsClosed()
        {
            FactionDefinition faction = SaveFaction();
            SimulationHost host = NewHost(faction);
            MintInto(host, faction);
            host.StepOnce();

            var table = CanonicalEffectDescriptorTable.Build(host.AbilityRegistry, host.ItemRegistry);
            SaveGameState state = SaveGameState.CaptureFrom(host, table);
            using var good = new MemoryStream();
            SaveGameFile.Write(good, state, new SaveGameHeaderData
            {
                CanonicalModelHash = 0, ContentHash = 0, Tick = host.CurrentTick, MapId = "spend",
                Slots = new List<ProjectChimera.Core.Skirmish.SetupSlot>(),
            });
            byte[] blob = good.ToArray();

            // Corrupt the declared HERO lane count so the strict per-lane length check has something to catch.
            // (The body is length-prefixed per lane; flipping any hero lane's length trips Validate on Read.)
            using var reader = new MemoryStream(blob);
            (SaveGameHeaderData _, SaveGameState parsed) = SaveGameFile.Read(reader); // the honest blob parses
            Assert.NotNull(parsed);

            // Now assert the guard exists for the NEW lane specifically, by shortening it in a re-serialized state.
            Assert.Throws<InvalidDataException>(() =>
            {
                using var ms = new MemoryStream();
                SaveGameState broken = SaveGameState.CaptureFrom(host, table);
                ShortenSpentLane(broken);
                SaveGameFile.Write(ms, broken, new SaveGameHeaderData
                {
                    CanonicalModelHash = 0, ContentHash = 0, Tick = host.CurrentTick, MapId = "spend",
                    Slots = new List<ProjectChimera.Core.Skirmish.SetupSlot>(),
                });
                ms.Position = 0;
                SaveGameFile.Read(ms);
            });
        }

        // ── Matrix row 11: an unknown authored token fails CLOSED at validation ──────────────────────────────

        [Fact]
        public void UnknownSpendModeToken_IsRejectedByTheValidator_WithALocatedError()
        {
            List<string> errors = ValidatorErrors(Wc3Model("wizard"));
            Assert.Contains(errors, e => e.Contains("is not a spend mode"));
            Assert.Contains(errors, e => e.Contains("attribute_model.spend_mode"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("auto")]
        [InlineData("AUTO")]
        [InlineData("player_spent")]
        [InlineData("Player_Spent")]
        public void EveryLegalToken_Validates_AndParsesCaseInsensitively(string? token)
        {
            Assert.DoesNotContain(ValidatorErrors(Wc3Model(token)), e => e.Contains("is not a spend mode"));
            AttributeSpendMode expected = token != null && token.ToLowerInvariant() == "player_spent"
                ? AttributeSpendMode.PlayerSpent : AttributeSpendMode.Auto;
            Assert.Equal(expected, Wc3Model(token).ParsedSpendMode);
        }

        /// <summary>The fail-OPEN accessor and the fail-CLOSED validator are two halves of one rule: an unknown
        /// token never silently becomes a MODE, it is refused at load.</summary>
        [Fact]
        public void UnknownToken_FailsOpenToAuto_ForTheRuntime()
            => Assert.Equal(AttributeSpendMode.Auto, Wc3Model("wizard").ParsedSpendMode);

        // ── Authoring plumbing: Clone, ContentHash, the writer ───────────────────────────────────────────────

        [Fact]
        public void Clone_CarriesTheSpendMode_TheStory45SilentDropClass()
        {
            AttributeModelDefinition c = Wc3Model("player_spent").Clone();
            Assert.Equal("player_spent", c.SpendMode);
            Assert.Equal(AttributeSpendMode.PlayerSpent, c.ParsedSpendMode);
            Assert.Null(Wc3Model(null).Clone().SpendMode);
        }

        [Fact]
        public void ContentHash_MovesOnSpendMode_ButNotOnACaseRespelling()
        {
            ulong auto        = ContentHash.Compute(new[] { MakeFaction(Wc3Model(null)) }, null, null, null);
            ulong autoExplicit = ContentHash.Compute(new[] { MakeFaction(Wc3Model("auto")) }, null, null, null);
            ulong spent       = ContentHash.Compute(new[] { MakeFaction(Wc3Model("player_spent")) }, null, null, null);
            ulong spentUpper  = ContentHash.Compute(new[] { MakeFaction(Wc3Model("Player_Spent")) }, null, null, null);

            Assert.NotEqual(auto, spent);        // a divergent MODE must reject at the lobby handshake
            Assert.Equal(auto, autoExplicit);    // absent ≡ "auto" — folding the raw string would break this
            Assert.Equal(spent, spentUpper);     // a cosmetic re-spelling is not a content mismatch
        }

        [Fact]
        public void Writer_OmitsSpendModeWhenUnauthored_SoShippedJsonStaysByteStable()
        {
            const string original = "{\n  \"id\": \"f\",\n  \"units\": []\n}";
            string written = FactionWriter.SyncFactionAttributeModel(original, Wc3Model(null));
            Assert.DoesNotContain("spend_mode", written);

            string withMode = FactionWriter.SyncFactionAttributeModel(original, Wc3Model("player_spent"));
            Assert.Contains("\"spend_mode\": \"player_spent\"", withMode);
        }

        // ── The wire: command 26 ────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void SpendAttributePointOrder_SurvivesTheWireRoundTrip()
        {
            var buf     = new byte[TickCommandPacket.HEADER_BYTES + TickCommandPacket.MAX_ORDERS * UnitOrder.SIZE];
            var decoded = new UnitOrder[TickCommandPacket.MAX_ORDERS];
            var sent    = new[] { new UnitOrder(77, UnitCommand.SpendAttributePoint, Fixed.FromRaw(INTELLIGENCE), Fixed.Zero) };

            int len = TickCommandPacket.Write(buf, tick: 900, Faction.Player1, sent, 0, 1);
            Assert.True(TickCommandPacket.TryRead(buf, len, out uint tick, out Faction faction, decoded, out int count));

            Assert.Equal(900u, tick);
            Assert.Equal(Faction.Player1, faction);
            Assert.Equal(1, count);
            Assert.Equal(UnitCommand.SpendAttributePoint, decoded[0].Command);
            Assert.Equal(77, decoded[0].UnitId);
            Assert.Equal(INTELLIGENCE, decoded[0].TargetX); // a RAW int index, never a Fixed value
        }

        [Fact]
        public void TheCommandByte_StaysInsideTheWireBudget_AndIsClassifiedNonPersisting()
        {
            Assert.Equal(26, (byte)UnitCommand.SpendAttributePoint);
            Assert.True((byte)UnitCommand.SpendAttributePoint <= 0x3F);
            Assert.False(UnitCommandTraits.PersistsAsCommandState(UnitCommand.SpendAttributePoint));
            Assert.Equal(14, UnitOrder.SIZE); // no wire widening
        }

        [Fact]
        public void SpendOrder_NeverLandsInCommandState()
        {
            var f = MakeHero("player_spent");
            LevelUp(f);
            f.World.CommandState[f.HeroEntity] = UnitCommand.Move;
            OrderApplier.Apply(f.World, SpendOrder(f.World, f.HeroEntity, STRENGTH), Faction.Player1, heroXp: f.Sys);
            Assert.Equal(UnitCommand.Move, f.World.CommandState[f.HeroEntity]); // the prior order is preserved
        }

        // ── Determinism ─────────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void TwoIdenticalRuns_WithSpendingInPlay_AreByteIdentical()
        {
            static List<uint> Run()
            {
                var f = MakeHero("player_spent");
                var seq = new List<uint>();
                for (int t = 0; t < 40; t++)
                {
                    if (t == 5)  f.Heroes.Xp[f.HeroSlot] = f.Heroes.Xp[f.HeroSlot] + Fixed.FromInt(150);
                    f.Sys.Tick(f.World, Dt);
                    if (t == 10) OrderApplier.Apply(f.World, SpendOrder(f.World, f.HeroEntity, STRENGTH), Faction.Player1, heroXp: f.Sys);
                    if (t == 20) OrderApplier.Apply(f.World, SpendOrder(f.World, f.HeroEntity, INTELLIGENCE), Faction.Player1, heroXp: f.Sys);
                    if (t == 30) OrderApplier.Apply(f.World, SpendOrder(f.World, f.HeroEntity, AGILITY), Faction.Player1, heroXp: f.Sys);
                    seq.Add(f.Checksum());
                }
                return seq;
            }

            List<uint> a = Run(), b = Run();
            Assert.Equal(a, b);
            Assert.NotEqual(a[0], a[^1]); // non-vacuous: the run really moved the checksum
        }

        /// <summary>Story 15-24e's own contract: Clear() preserves the launch-owned AI mask (the positive assertion
        /// behind the StoreClearCompletenessTests allowlist entry).</summary>
        [Fact]
        public void Clear_PreservesTheAiControlMask()
        {
            var heroes = new HeroStore { AiControlMask = 1 << (int)Faction.Player3 };
            heroes.Mint(new HeroId(1), 0, 1, Fixed.Zero);
            heroes.Clear();
            Assert.Equal(1 << (int)Faction.Player3, heroes.AiControlMask);
            Assert.Equal(0, heroes.Count); // …and it really cleared everything else
        }

        [Fact]
        public void SetAiControlPlan_ArmsTheHeroStoreAndTheAiSystem_FromOneValue()
        {
            SimulationHost host = NewHost(SaveFaction());
            host.SetAiControlPlan(ProjectChimera.AI.AiControlPlan.Of(Faction.Player3));
            Assert.Equal(host.Ai.ControlPlan.Mask, host.Heroes.AiControlMask);
        }

        /// <summary>The mode oracle lives in ONE function; the read seam and the withholding site must agree.</summary>
        [Fact]
        public void AttributeStatAt_WithholdsThePerLevelTerm_UnderPlayerSpent_AndAddsTheSpentTerm()
        {
            var auto  = MakeHero("auto");
            var spent = MakeHero("player_spent");
            LevelUp(auto);
            LevelUp(spent);

            // intelligence → max_energy 15/pt: base 16 int = 240, +1.5 int/level × 15 = +22.5 at level 2.
            Assert.Equal(Fixed.FromFloat(262.5f).Raw, auto.Heroes.AttributeStatAt(auto.HeroSlot, AttributeStats.MaxEnergy).Raw);
            Assert.Equal(Fixed.FromInt(240).Raw, spent.Heroes.AttributeStatAt(spent.HeroSlot, AttributeStats.MaxEnergy).Raw);

            OrderApplier.Apply(spent.World, SpendOrder(spent.World, spent.HeroEntity, INTELLIGENCE), Faction.Player1, heroXp: spent.Sys);
            Assert.Equal(Fixed.FromInt(255).Raw, spent.Heroes.AttributeStatAt(spent.HeroSlot, AttributeStats.MaxEnergy).Raw);
        }

        // ── The level-up cue (there was none before this story) ──────────────────────────────────────────────

        [Fact]
        public void LevelUp_PushesExactlyOneHeroLeveledCue_PerLevelGained_StampedWithTheHerosFaction()
        {
            var f = MakeHero("player_spent");
            f.Sys.Tick(f.World, Dt);
            f.Events.Clear();
            LevelUp(f, 3);

            int cues = 0;
            for (int i = 0; i < f.Events.Count; i++)
            {
                CombatEvent e = f.Events.Get(i);
                if (e.Type != CombatEventType.HeroLeveled) continue;
                cues++;
                Assert.Equal(Faction.Player1, e.Faction); // local-only feedback needs the owner stamped
            }
            Assert.Equal(3, cues);
            Assert.False(CombatEventQueue.IsAmbient(CombatEventType.HeroLeveled)); // a notification, not battle juice
        }

        // ── Review P1: the deploy-at-level-N credit ──────────────────────────────────────────────────────────

        [Fact]
        public void HeroMintedAtLevelN_UnderPlayerSpent_ArrivesWithNMinusOneBankedPoints()
        {
            var deployed = MakeHero("player_spent", maxLevel: 10, level: 5);
            deployed.Sys.Tick(deployed.World, Dt);
            Assert.Equal(4, deployed.Heroes.UnspentPoints[deployed.HeroSlot]); // levels 2..5 each earned one
        }

        /// <summary>The two ways to reach level 5 must agree. Without the Mint credit, a hero deployed from a saved
        /// profile at level 5 arrived with level-1 attributes, ZERO points and no recovery path (no respec).</summary>
        [Fact]
        public void MintAtLevelFive_AndAFiveLevelClimb_AgreeOnPointsAndEffectiveStats()
        {
            var climbed = MakeHero("player_spent", maxLevel: 10);
            climbed.Sys.Tick(climbed.World, Dt);
            LevelUp(climbed, 4); // 1 -> 5 in match

            var deployed = MakeHero("player_spent", maxLevel: 10, level: 5);
            deployed.Sys.Tick(deployed.World, Dt);

            Assert.Equal(5, climbed.Heroes.Level[climbed.HeroSlot]);
            Assert.Equal(4, deployed.Heroes.UnspentPoints[deployed.HeroSlot]); // non-vacuous
            Assert.Equal(climbed.Heroes.UnspentPoints[climbed.HeroSlot],
                         deployed.Heroes.UnspentPoints[deployed.HeroSlot]);
            Assert.Equal(climbed.World.EffectiveMaxHealth[climbed.HeroEntity].Raw,
                         deployed.World.EffectiveMaxHealth[deployed.HeroEntity].Raw);
            Assert.Equal(climbed.World.EffectiveAttackDamage[climbed.HeroEntity].Raw,
                         deployed.World.EffectiveAttackDamage[deployed.HeroEntity].Raw);
        }

        [Fact]
        public void MintAtLevelN_UnderAuto_OrOnAnAiSlot_BanksNothing()
        {
            var auto = MakeHero("auto", maxLevel: 10, level: 5);
            Assert.Equal(0, auto.Heroes.UnspentPoints[auto.HeroSlot]);

            var ai = MakeHero("player_spent", Faction.Player2, aiMask: 1 << (int)Faction.Player2, maxLevel: 10, level: 5);
            Assert.Equal(0, ai.Heroes.UnspentPoints[ai.HeroSlot]);
        }

        // ── Review P2: accumulated spends saturate instead of wrapping ────────────────────────────────────────

        [Fact]
        public void AccumulatedSpends_SaturateAtTheFixedCeiling_NeverWrapNegative()
        {
            var f = MakeHero("player_spent", maxLevel: 10);
            LevelUp(f, 2);
            int i = f.HeroSlot * AttributeStats.Count + AttributeStats.MaxHealth;
            // Park the lane one raw tick below the 16.16 ceiling: the next +25.0 grant would wrap to a huge NEGATIVE
            // max_health and ceiling-collapse the hero (the swept SqrDistance overflow class, DW-325 downstream).
            f.Heroes.AttrStatSpent[i] = Fixed.FromRaw(int.MaxValue - 1);

            OrderApplier.Apply(f.World, SpendOrder(f.World, f.HeroEntity, STRENGTH), Faction.Player1, heroXp: f.Sys);

            Assert.Equal(int.MaxValue, f.Heroes.AttrStatSpent[i].Raw); // clamped, not wrapped
            Assert.True(f.Heroes.AttrStatSpent[i].Raw > 0);
            Assert.True(f.World.IsAlive(f.HeroEntity));                // and the hero did not ceiling-collapse
        }

        [Fact]
        public void AccumulatedSpends_SaturateAtTheNegativeBound_Too()
        {
            var f = MakeHero("player_spent", maxLevel: 10);
            LevelUp(f, 2);
            int i = f.HeroSlot * AttributeStats.Count + AttributeStats.MaxHealth;
            f.Heroes.AttrStatSpent[i] = Fixed.FromRaw(int.MinValue + 1);
            // A drawback row the validator rejects at authoring; the clamp is the runtime backstop for it.
            f.Heroes.AttrModelOf[f.HeroSlot]!.Derived![0].PerPoint = -25f;

            OrderApplier.Apply(f.World, SpendOrder(f.World, f.HeroEntity, STRENGTH), Faction.Player1, heroXp: f.Sys);

            Assert.Equal(int.MinValue, f.Heroes.AttrStatSpent[i].Raw);
        }

        // ── Review P6: a matched-but-zero grant must not burn the point ───────────────────────────────────────

        [Fact]
        public void ARuleAuthoredAtZeroPerPoint_RefusesTheSpend_RatherThanConsumingThePoint()
        {
            var f = MakeHero("player_spent");
            // agility -> armor exists but grants 0 per point: the attribute IS named by a rule, yet buys nothing.
            f.Heroes.AttrModelOf[f.HeroSlot]!.Derived![1].PerPoint = 0f;
            LevelUp(f);

            OrderApplier.Apply(f.World, SpendOrder(f.World, f.HeroEntity, AGILITY), Faction.Player1, heroXp: f.Sys);

            Assert.Equal(1, f.Heroes.UnspentPoints[f.HeroSlot]); // still bankable
            Assert.Null(HeroAttributeResolver.ResolvePointGrant(
                f.Heroes.AttrModelOf[f.HeroSlot], HeroAttrs(), "agility"));
        }

        [Fact]
        public void TwoRulesThatCancelOnOneStat_AlsoRefuse()
        {
            var model = Wc3Model("player_spent");
            model.Derived!.Add(new DerivedStatRule { Attribute = "agility", Stat = "armor", PerPoint = -0.3f });
            Assert.Null(HeroAttributeResolver.ResolvePointGrant(model, HeroAttrs(), "agility"));
            Assert.NotNull(HeroAttributeResolver.ResolvePointGrant(model, HeroAttrs(), "strength")); // non-vacuity
        }

        [Fact]
        public void HasPointGrant_IsTheSameGateTheApplierUses()
        {
            AttributeModelDefinition model = Wc3Model("player_spent");
            model.Attributes!.Add(new AttributeDeclaration { Id = "luck", Name = "Luck" });
            Assert.True(HeroAttributeResolver.HasPointGrant(model, HeroAttrs(), "strength"));
            Assert.False(HeroAttributeResolver.HasPointGrant(model, HeroAttrs(), "luck"));
            Assert.False(HeroAttributeResolver.HasPointGrant(model, HeroAttrs(), "nonexistent"));
        }

        // ── Review P7: the player_spent x threshold-row contract, pinned in BOTH directions ───────────────────

        /// <summary>
        /// The contract 15-24e documents but nothing pinned: a step/gate row follows the hero's AUTHORED auto totals
        /// (base + per_level x (L-1)) in EVERY mode, and a SPEND never moves it. Both halves went green under the
        /// opposite implementation before this test existed — an IsPlayerSpent gate inside ReconcileThresholds, or
        /// thresholds reading the spent lane, were equally invisible.
        /// </summary>
        [Fact]
        public void ThresholdRows_FollowTheAuthoredAutoTotals_InBothModes_AndDoNotFollowSpends()
        {
            static Fixture WithThreshold(string? mode)
            {
                var f = MakeHero(mode, maxLevel: 10);
                // "every 10 points of strength -> +7 armor". At level 1 strength is 20 -> 2 steps -> +14 armor;
                // at level 6 it is 20 + 5x2 = 30 -> 3 steps -> +21. Functions of the AUTHORED totals only.
                f.Heroes.AttrModelOf[f.HeroSlot]!.Derived!.Add(new DerivedStatRule
                {
                    Attribute = "strength", Stat = "armor", PerPoint = 7f, Shape = "per_step", Threshold = 10f,
                });
                return f;
            }

            var auto  = WithThreshold("auto");
            var spent = WithThreshold("player_spent");
            auto.Sys.Tick(auto.World, Dt);
            spent.Sys.Tick(spent.World, Dt);
            LevelUp(auto, 5);
            LevelUp(spent, 5);
            Assert.Equal(6, auto.Heroes.Level[auto.HeroSlot]);

            // Both arms crossed the SAME step boundary at the same level: the threshold term is identical even though
            // the LINEAR attribute term was withheld from the player-spent arm.
            Fixed autoArmor  = auto.World.EffectiveArmor[auto.HeroEntity];
            Fixed spentArmor = spent.World.EffectiveArmor[spent.HeroEntity];
            // The gap is EXACTLY 5 stacks of the withheld per-level agility→armor term — read from the resolved lane
            // itself rather than recomputed from 0.3f, because the growth modifier applies the once-quantized
            // per-level value five times (5 x 19660), which is not the same raw as quantizing 1.5 once.
            int perLevelArmorRaw = auto.Heroes.AttrStatPerLevel[
                auto.HeroSlot * AttributeStats.Count + AttributeStats.Armor].Raw;
            Assert.True(perLevelArmorRaw > 0);                                          // non-vacuity
            Assert.Equal(perLevelArmorRaw * 5, (autoArmor - spentArmor).Raw);           // ONLY the linear term differs
            Assert.True(spentArmor > Fixed.FromInt(20));                                // the threshold really applied

            // …and a SPEND does not move the threshold term (spends feed linear rows only, by contract).
            Assert.Equal(5, spent.Heroes.UnspentPoints[spent.HeroSlot]);
            Fixed before = spent.World.EffectiveArmor[spent.HeroEntity];
            OrderApplier.Apply(spent.World, SpendOrder(spent.World, spent.HeroEntity, AGILITY), Faction.Player1, heroXp: spent.Sys);
            Assert.Equal((before + Fixed.FromFloat(0.3f)).Raw, spent.World.EffectiveArmor[spent.HeroEntity].Raw);
        }

        /// <summary>An attribute served ONLY by threshold rows grants nothing to a spend — so the affordance must
        /// refuse it rather than offer a button that banks points with nowhere to go (the P7/P8 pair).</summary>
        [Fact]
        public void AnAttributeServedOnlyByThresholdRows_IsNotSpendable()
        {
            var model = Wc3Model("player_spent");
            model.Attributes!.Add(new AttributeDeclaration { Id = "vitality", Name = "Vitality" });
            model.Derived!.Add(new DerivedStatRule
            {
                Attribute = "vitality", Stat = "max_health", PerPoint = 40f, Shape = "at_least", Threshold = 5f,
            });
            HeroAttributesDefinition hero = HeroAttrs();
            hero.Base!["vitality"] = 10f;
            hero.PerLevel!["vitality"] = 1f;

            Assert.False(HeroAttributeResolver.HasPointGrant(model, hero, "vitality"));
            Assert.True(HeroAttributeResolver.HasPointGrant(model, hero, "strength"));
        }

        // ── Review P13: the flat hero stat-growth lanes keep applying in EVERY mode ───────────────────────────

        [Fact]
        public void FlatStatGrowthLanes_KeepApplying_UnderPlayerSpent()
        {
            var f = MakeHero("player_spent", maxLevel: 10);
            f.Sys.Tick(f.World, Dt);
            Fixed hp0    = f.World.EffectiveMaxHealth[f.HeroEntity];
            Fixed dmg0   = f.World.EffectiveAttackDamage[f.HeroEntity];
            Fixed armor0 = f.World.EffectiveArmor[f.HeroEntity];

            LevelUp(f, 3);

            // The fixture authors +10 hp / +2 dmg / +1 armor per level. These are hero STAT growth, not attributes,
            // and the spec's boundary says they apply in EVERY mode — which nothing proved before this test.
            Assert.Equal((hp0    + Fixed.FromInt(30)).Raw, f.World.EffectiveMaxHealth[f.HeroEntity].Raw);
            Assert.Equal((dmg0   + Fixed.FromInt(6)).Raw,  f.World.EffectiveAttackDamage[f.HeroEntity].Raw);
            Assert.Equal((armor0 + Fixed.FromInt(3)).Raw,  f.World.EffectiveArmor[f.HeroEntity].Raw);
            Assert.Equal(3, f.Heroes.UnspentPoints[f.HeroSlot]); // …and the ATTRIBUTE half was still withheld
        }

        // ── Review P9: the declared-attribute cap is a CONTENT gate, not a silent presentation truncation ─────

        [Fact]
        public void AModelDeclaringMoreAttributesThanTheAffordanceCanShow_IsRejectedAtValidation()
        {
            var model = Wc3Model(null);
            for (int i = 0; i < AttributeModelDefinition.MaxDeclaredAttributes; i++)
                model.Attributes!.Add(new AttributeDeclaration { Id = $"extra{i}", Name = $"Extra {i}" });

            Assert.Contains(ValidatorErrors(model), e => e.Contains("at most"));
            // Exactly at the cap is fine (and every shipped preset declares 3-5).
            var atCap = Wc3Model(null);
            while (atCap.Attributes!.Count < AttributeModelDefinition.MaxDeclaredAttributes)
                atCap.Attributes.Add(new AttributeDeclaration { Id = $"x{atCap.Attributes.Count}", Name = "X" });
            Assert.DoesNotContain(ValidatorErrors(atCap), e => e.Contains("at most"));
        }

        // ── Review P8: every refusal cues; a success does not ─────────────────────────────────────────────────

        [Fact]
        public void ARefusedSpend_PushesADenialCue_AndASuccessfulOneDoesNot()
        {
            var f = MakeHero("player_spent");
            LevelUp(f);
            f.Events.Clear();

            // Refusal 1: an unknown attribute index.
            OrderApplier.Apply(f.World, SpendOrder(f.World, f.HeroEntity, 99), Faction.Player1,
                               events: f.Events, heroXp: f.Sys);
            Assert.Equal(1, CountDenials(f));

            // Success: no cue (a "level up" toast would be a lie, and the card refresh IS the feedback).
            OrderApplier.Apply(f.World, SpendOrder(f.World, f.HeroEntity, STRENGTH), Faction.Player1,
                               events: f.Events, heroXp: f.Sys);
            Assert.Equal(1, CountDenials(f));
            Assert.Equal(0, f.Heroes.UnspentPoints[f.HeroSlot]);

            // Refusal 2: the bank is now empty — a stale click, or the order ring dropped an earlier spend.
            OrderApplier.Apply(f.World, SpendOrder(f.World, f.HeroEntity, STRENGTH), Faction.Player1,
                               events: f.Events, heroXp: f.Sys);
            Assert.Equal(2, CountDenials(f));
        }

        private static int CountDenials(Fixture f)
        {
            int n = 0;
            for (int i = 0; i < f.Events.Count; i++)
                if (f.Events.Get(i).Type == CombatEventType.OrderDenied) n++;
            return n;
        }

        // ── Review P5: the three APPLY PATHS must land identically ────────────────────────────────────────────

        /// <summary>
        /// The DW-86 three-arm parity shape. Before this test, deleting the <c>heroXp</c> forwarding argument from
        /// <c>ReplayPlayer</c>, either <c>MergedTickApplier</c> overload, or <c>LockstepManager.ApplyMerged</c> left
        /// the WHOLE SUITE GREEN — every other case calls <c>OrderApplier.Apply</c> directly and hands in the handle
        /// itself, so online and replay spends could vanish silently while offline applied them (the exact failure
        /// DW-86's own comment records for the <c>Research</c> argument).
        /// </summary>
        [Fact]
        public void SpendOrder_AppliesIdentically_Offline_Online_AndInReplay()
        {
            UnitOrder Order(Fixture f) => SpendOrder(f.World, f.HeroEntity, STRENGTH);

            // OFFLINE (F5): the exact line SelectionSystem.IssueSpendAttributePointCommand runs.
            var live = MakeHero("player_spent", maxLevel: 10);
            LevelUp(live, 2);
            Assert.Equal(2, live.Heroes.UnspentPoints[live.HeroSlot]);          // non-vacuity: something to spend
            OrderApplier.Apply(live.World, Order(live), Faction.Player1, heroXp: live.Sys);
            Assert.Equal(1, live.Heroes.UnspentPoints[live.HeroSlot]);          // …and it really applied

            // ONLINE: through the REAL server merge into MergedTickApplier — the exact core ApplyMerged calls.
            var online = MakeHero("player_spent", maxLevel: 10);
            LevelUp(online, 2);
            var buf = new byte[TickCommandPacket.HEADER_BYTES + TickCommandPacket.MAX_ORDERS * UnitOrder.SIZE];
            int len = TickCommandPacket.Write(buf, 1u, Faction.Player1, new[] { Order(online) }, 0, 1);
            var builder = new MergedTickBuilder(1, new[] { Faction.Player1 });
            Assert.True(builder.Submit(0, buf, len, out _));
            Assert.True(builder.TryBuild(1u, out byte[] merged, out int mergedLen));
            MergedTickApplier.Apply(merged, mergedLen, online.World, heroXp: online.Sys);

            // REPLAY: the same recorded order through ReplayPlayer with HeroXp wired.
            var rep = MakeHero("player_spent", maxLevel: 10);
            LevelUp(rep, 2);
            string path = Path.GetTempFileName();
            try
            {
                using (var recorder = new ReplayRecorder(path, "test://spend-attribute-point",
                                                         EntityWorld.DEFAULT_RNG_SEED, 0x11UL, 0x22UL,
                                                         CanonicalModelHash.AlgoVersion,
                                                         new[] { Faction.Player1, Faction.Player2 }))
                {
                    recorder.RecordTick(1, Faction.Player1, new[] { Order(rep) }, 0, 1);
                }
                var player = new ReplayPlayer(path, rep.World) { HeroXp = rep.Sys };
                player.Flush(1);
            }
            finally { File.Delete(path); }

            // All three land the SAME banked count, the SAME spent lane and the SAME effective ceiling.
            int aLive = live.HeroSlot * AttributeStats.Count, aOn = online.HeroSlot * AttributeStats.Count,
                aRep  = rep.HeroSlot  * AttributeStats.Count;
            Assert.Equal(live.Heroes.UnspentPoints[live.HeroSlot],   online.Heroes.UnspentPoints[online.HeroSlot]);
            Assert.Equal(live.Heroes.UnspentPoints[live.HeroSlot],   rep.Heroes.UnspentPoints[rep.HeroSlot]);
            for (int s = 0; s < AttributeStats.Count; s++)
            {
                Assert.Equal(live.Heroes.AttrStatSpent[aLive + s].Raw, online.Heroes.AttrStatSpent[aOn + s].Raw);
                Assert.Equal(live.Heroes.AttrStatSpent[aLive + s].Raw, rep.Heroes.AttrStatSpent[aRep + s].Raw);
            }
            Assert.Equal(live.World.EffectiveMaxHealth[live.HeroEntity].Raw,
                         online.World.EffectiveMaxHealth[online.HeroEntity].Raw);
            Assert.Equal(live.World.EffectiveMaxHealth[live.HeroEntity].Raw,
                         rep.World.EffectiveMaxHealth[rep.HeroEntity].Raw);
            // Non-vacuity on the VALUE itself: a dropped forwarding arg would leave all three at the pre-spend
            // ceiling and every equality above would still hold.
            Assert.Equal(Fixed.FromInt(25).Raw, live.Heroes.AttrStatSpent[aLive + AttributeStats.MaxHealth].Raw);
        }

        // ── Review P3: the spend MODE is reconstructible on load, or the load fails closed ────────────────────

        [Fact]
        public void TheAiControlMask_RoundTripsThroughASave_SoTheModeIsReconstructible()
        {
            FactionDefinition faction = SaveFaction();
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = faction;

            SimulationHost host = NewHost(faction);
            (int ent, int slot) = MintInto(host, faction);
            host.SetAiControlPlan(ProjectChimera.AI.AiControlPlan.Of(Faction.Player4)); // a NON-default mask
            host.StepOnce();
            host.Heroes.Xp[slot] = Fixed.FromInt(100);
            host.StepOnce();
            Assert.True(host.Heroes.UnspentPoints[slot] > 0); // non-vacuity

            SimulationHost loaded = RoundTrip(host, faction, slotDefs);

            Assert.Equal(host.Heroes.AiControlMask, loaded.Heroes.AiControlMask);
            Assert.Equal(1 << (int)Faction.Player4, loaded.Heroes.AiControlMask);
            Assert.True(loaded.Heroes.IsPlayerSpent(slot)); // Player1 is not AI-driven → the mode survived
            Assert.Equal(host.Heroes.UnspentPoints[slot], loaded.Heroes.UnspentPoints[slot]);
            _ = ent;
        }

        /// <summary>The fail-CLOSED half: a hero holding banked points whose mode cannot be reconstructed must
        /// REFUSE to load rather than silently resume as `auto` (restarting withheld growth and stranding the
        /// points). Both unreconstructible shapes are covered: the model is gone, and the mask now marks the
        /// owner AI-driven.</summary>
        [Fact]
        public void ASaveWhoseSpendModeCannotBeReconstructed_FailsClosed()
        {
            FactionDefinition faction = SaveFaction();
            SimulationHost host = NewHost(faction);
            (_, int slot) = MintInto(host, faction);
            host.StepOnce();
            host.Heroes.Xp[slot] = Fixed.FromInt(100);
            host.StepOnce();
            Assert.True(host.Heroes.UnspentPoints[slot] > 0);

            byte[] blob = Serialize(host);

            // (a) the faction no longer declares an attribute model → AttrModelOf re-resolves to null.
            var strippedDefs = new FactionDefinition?[5];
            strippedDefs[(int)Faction.Player1] = MakeFaction(Wc3Model("player_spent"));
            strippedDefs[(int)Faction.Player1]!.AttributeModel = null;
            var ex = Assert.Throws<InvalidDataException>(() => Deserialize(blob, faction, strippedDefs));
            Assert.Contains("spend mode cannot be reconstructed", ex.Message);

            // (b) the model now authors `auto`.
            var autoDefs = new FactionDefinition?[5];
            autoDefs[(int)Faction.Player1] = MakeFaction(Wc3Model("auto"));
            Assert.Throws<InvalidDataException>(() => Deserialize(blob, faction, autoDefs));

            // (c) the persisted mask marks the owner AI-driven.
            SimulationHost aiHost = NewHost(faction);
            (_, int aiSlot) = MintInto(aiHost, faction);
            aiHost.StepOnce();
            aiHost.Heroes.Xp[aiSlot] = Fixed.FromInt(100);
            aiHost.StepOnce();
            aiHost.Heroes.AiControlMask = 1 << (int)Faction.Player1; // flip AFTER the points were banked
            var aiEx = Assert.Throws<InvalidDataException>(() => Deserialize(Serialize(aiHost), faction, SlotDefs(faction)));
            Assert.Contains("AI-driven", aiEx.Message);

            // …and the honest blob still loads (non-vacuity: the guard is not rejecting everything).
            SimulationHost ok = Deserialize(blob, faction, SlotDefs(faction));
            Assert.True(ok.Heroes.UnspentPoints[slot] > 0);
        }

        [Fact]
        public void AReplayHeader_CarriesTheAiControlMask_SoPlaybackReproducesTheRecordingsPlan()
        {
            string path = Path.GetTempFileName();
            try
            {
                int mask = ProjectChimera.AI.AiControlPlan.Of(Faction.Player3, Faction.Player4).Mask;
                using (var rec = new ReplayRecorder(path, "test://ai-mask", EntityWorld.DEFAULT_RNG_SEED,
                                                    0x11UL, 0x22UL, CanonicalModelHash.AlgoVersion,
                                                    new[] { Faction.Player1, Faction.Player2 }, mask))
                {
                    rec.RecordTick(1, Faction.Player1,
                                   new[] { new UnitOrder(0, UnitCommand.Stop, Fixed.Zero, Fixed.Zero) }, 0, 1);
                }

                var player = new ReplayPlayer(path, new EntityWorld());
                Assert.Equal(mask, player.AiControlMask);
                // …and the mask rebuilds the plan playback actually installs.
                ProjectChimera.AI.AiControlPlan plan = ProjectChimera.AI.AiControlPlan.FromMask(player.AiControlMask);
                Assert.True(plan.Controls(Faction.Player3));
                Assert.True(plan.Controls(Faction.Player4));
                Assert.False(plan.Controls(Faction.Player2)); // NOT the OfflineDefault playback used to assert
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void FromMask_DropsBitsOutsideThePlayableFactionRange()
        {
            ProjectChimera.AI.AiControlPlan plan =
                ProjectChimera.AI.AiControlPlan.FromMask(unchecked((int)0xFFFF_FFFF));
            Assert.True(plan.Controls(Faction.Player1));
            Assert.True(plan.Controls(Faction.Player8));
            Assert.False(plan.Controls(Faction.Neutral)); // bit 0 is never a playable slot
            Assert.Equal(0, plan.Mask & ~0x1FE);          // only bits 1..8 survive
        }

        // ── Review P4: the offline plan follows SLOT OCCUPANCY, not the {Player2} constant ────────────────────

        [Fact]
        public void OfflineAiPlan_FollowsSlotOccupancy_NotTheHardcodedPlayer2()
        {
            // A 1v3: the human takes launch index 0 (Player1); the three AI rows take Player2/3/4.
            var slots = new List<SetupSlot>
            {
                new() { Slot = 0, Kind = SlotKind.Human, FactionId = "alpha" },
                new() { Slot = 1, Kind = SlotKind.Ai,    FactionId = "alpha" },
                new() { Slot = 2, Kind = SlotKind.Ai,    FactionId = "alpha" },
                new() { Slot = 3, Kind = SlotKind.Ai,    FactionId = "alpha" },
            };
            IReadOnlyList<Faction> ai = SkirmishSetupToScenario.AiFactionsInLaunchOrder(slots);
            Assert.Equal(new[] { Faction.Player2, Faction.Player3, Faction.Player4 }, ai);

            // The human in a LATER row still lands at Player1 (the launch order sorts Human first), so the AI set
            // is not simply "the non-first slots".
            var humanLast = new List<SetupSlot>
            {
                new() { Slot = 0, Kind = SlotKind.Ai,    FactionId = "alpha" },
                new() { Slot = 1, Kind = SlotKind.Human, FactionId = "alpha" },
            };
            Assert.Equal(new[] { Faction.Player2 }, SkirmishSetupToScenario.AiFactionsInLaunchOrder(humanLast));

            // Closed/open rows contribute nothing.
            var sparse = new List<SetupSlot>
            {
                new() { Slot = 0, Kind = SlotKind.Human,  FactionId = "alpha" },
                new() { Slot = 1, Kind = SlotKind.Closed, FactionId = "alpha" },
                new() { Slot = 2, Kind = SlotKind.Ai,     FactionId = "alpha" },
            };
            Assert.Equal(new[] { Faction.Player2 }, SkirmishSetupToScenario.AiFactionsInLaunchOrder(sparse));
        }

        /// <summary>The behavioural consequence: a hero on an AI-HELD Player3 behaves as `auto`. Under the old
        /// hardcoded {Player2} mask it would have been classed player-spent, banking points nobody can spend and
        /// withholding its attribute growth for the whole match.</summary>
        [Fact]
        public void AHeroOnAnAiHeldPlayer3_BehavesAsAuto_UnderTheDerivedPlan()
        {
            var slots = new List<SetupSlot>
            {
                new() { Slot = 0, Kind = SlotKind.Human, FactionId = "alpha" },
                new() { Slot = 1, Kind = SlotKind.Ai,    FactionId = "alpha" },
                new() { Slot = 2, Kind = SlotKind.Ai,    FactionId = "alpha" },
            };
            int derived = ProjectChimera.AI.AiControlPlan.Of(
                System.Linq.Enumerable.ToArray(SkirmishSetupToScenario.AiFactionsInLaunchOrder(slots))).Mask;

            var underDerived = MakeHero("player_spent", Faction.Player3, aiMask: derived, maxLevel: 10);
            var underLegacy  = MakeHero("player_spent", Faction.Player3,
                                        aiMask: ProjectChimera.AI.AiControlPlan.OfflineDefault.Mask, maxLevel: 10);
            LevelUp(underDerived);
            LevelUp(underLegacy);

            Assert.False(underDerived.Heroes.IsPlayerSpent(underDerived.HeroSlot));
            Assert.Equal(0, underDerived.Heroes.UnspentPoints[underDerived.HeroSlot]); // behaves as auto
            Assert.Equal(Fixed.FromInt(660).Raw, underDerived.World.EffectiveMaxHealth[underDerived.HeroEntity].Raw);
            // The legacy constant is the bug this replaced: Player3 was NOT in {Player2}, so it banked.
            Assert.Equal(1, underLegacy.Heroes.UnspentPoints[underLegacy.HeroSlot]);
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────

        private static FactionDefinition?[] SlotDefs(FactionDefinition faction)
        {
            var d = new FactionDefinition?[5];
            d[(int)Faction.Player1] = faction;
            return d;
        }

        private static byte[] Serialize(SimulationHost host)
        {
            var table = CanonicalEffectDescriptorTable.Build(host.AbilityRegistry, host.ItemRegistry);
            SaveGameState state = SaveGameState.CaptureFrom(host, table);
            using var ms = new MemoryStream();
            SaveGameFile.Write(ms, state, new SaveGameHeaderData
            {
                CanonicalModelHash = 0, ContentHash = 0, Tick = host.CurrentTick, MapId = "spend",
                Slots = new List<SetupSlot>(),
            });
            return ms.ToArray();
        }

        private static SimulationHost Deserialize(byte[] blob, FactionDefinition hostFaction,
                                                  FactionDefinition?[] slotDefs)
        {
            SimulationHost loaded = NewHost(hostFaction);
            using var read = new MemoryStream(blob);
            (SaveGameHeaderData _, SaveGameState st) = SaveGameFile.Read(read);
            st.RestoreInto(loaded, CanonicalEffectDescriptorTable.Build(loaded.AbilityRegistry, loaded.ItemRegistry), slotDefs);
            return loaded;
        }

        private static SimulationHost RoundTrip(SimulationHost host, FactionDefinition faction,
                                                FactionDefinition?[] slotDefs)
            => Deserialize(Serialize(host), faction, slotDefs);


        private static int CountModifier(Fixture f, int modifierId)
        {
            int n = 0;
            for (int s = 0; s < f.Modifiers.CountAt(f.HeroEntity); s++)
                if (f.Modifiers.ModifierIdAt(f.HeroEntity, s) == modifierId) n++;
            return n;
        }

        private static FactionDefinition MakeFaction(AttributeModelDefinition model) => new FactionDefinition
        {
            Id = "test_faction", DisplayName = "Test", AttributeModel = model,
            Units = new List<UnitDefinition>
            {
                new()
                {
                    Id = "hero", DisplayName = "Hero", Category = "Melee", IsHero = true,
                    Hp = 100, Speed = 3, AttackDamage = 10, AttackRange = 2, AttackSpeed = 1,
                    Hero = new HeroDefinition { Attributes = HeroAttrs() },
                },
            },
            Buildings = new List<BuildingDefinition>(),
        };

        private static List<string> ValidatorErrors(AttributeModelDefinition model)
        {
            FactionValidationResult result = FactionValidator.Validate(MakeFaction(model));
            var list = new List<string>();
            foreach ((string _, string msg) in result.Errors) list.Add(msg);
            return list;
        }

        /// <summary>The faction the save/load cases resolve their attribute model back out of on restore.</summary>
        private static FactionDefinition SaveFaction() => MakeFaction(Wc3Model("player_spent"));

        private static SimulationHost NewHost(FactionDefinition faction)
        {
            SimulationHost host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            host.ChecksumInterval = 1;
            return host;
        }

        /// <summary>Mint the fixture hero straight into a host's stores (no scenario needed — the save/load cases
        /// exercise the persistence lanes, not the applier).</summary>
        private static (int Entity, int Slot) MintInto(SimulationHost host, FactionDefinition faction)
        {
            UnitDefinition def = faction.Units[0];
            int ent = host.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            host.World.BaseAttackDamage[ent] = Fixed.FromInt(5);
            host.World.EffectiveAttackDamage[ent] = Fixed.FromInt(5);
            var (cBase, cPerLevel) = HeroAttributeResolver.Resolve(faction.AttributeModel, def.Hero!.Attributes);
            int slot = host.Heroes.Mint(new HeroId(42), ent, 1, Fixed.Zero,
                maxLevel: 5, baseXp: Fixed.FromInt(50), xpGrowth: Fixed.One, xpShareRadius: Fixed.FromInt(10),
                healthPerLevel: Fixed.FromInt(10), damagePerLevel: Fixed.FromInt(2), armorPerLevel: Fixed.FromInt(1),
                sourceDef: def, ownerFaction: Faction.Player1,
                attrStatBase: cBase, attrStatPerLevel: cPerLevel, attrModel: faction.AttributeModel);
            host.World.HeroIndex[ent] = host.Heroes.PackRef(slot);
            host.Heroes.AiControlMask = 0; // a human seat, so the authored player_spent model is in force
            return (ent, slot);
        }

        /// <summary>Truncate the captured SPENT lane so the strict per-lane length validation has a target. Indexed
        /// by the lane's own enum position, NOT by "the last row": HA is append-only by design, so the next appended
        /// hero lane would silently re-aim this at something else and the test would stop proving anything.</summary>
        private static void ShortenSpentLane(SaveGameState state)
            => state.Hero[SaveGameState.HeroLaneIndexAttrStatSpent] = System.Array.Empty<int>();
    }
}
