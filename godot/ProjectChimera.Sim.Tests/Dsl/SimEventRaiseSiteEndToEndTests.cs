#nullable enable
using ProjectChimera.Combat;            // HeroXpSystem, DeathFeed
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using ProjectChimera.Economy;           // BuildingSystem
using ProjectChimera.Effects;           // ModifierSystem / ModifierStore / AbilityCastSystem
using ProjectChimera.Sim.Tests.Effects; // CastHarness / AbilityTestAbilities
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// Story 7.13 (review PATCH 3) — END-TO-END coverage of the three sim-event raise sites that previously had NO
    /// real-producer test (only <c>unit_damaged</c> was driven through <c>DamageResolver.Apply</c>). Each test wires a
    /// real <see cref="DslSimEventFeed"/> into the actual producer via its <c>SetDslSimEvents</c> setter, drives the
    /// real path (a completed unit train in <see cref="BuildingSystem"/>, a committed cast in
    /// <see cref="AbilityCastSystem"/>, a hero level-up in <see cref="HeroXpSystem"/>), then ticks a
    /// <see cref="ScenarioDirector"/> holding the SAME feed and asserts the subscribed trigger fires with the CORRECT
    /// faction slot AND payload — the subscribing trigger READS an event param, so a wrong <c>(int)faction - 1</c>
    /// offset (trigger keyed on slot 0 would not fire) or a transposed payload slot (the read param carries the wrong
    /// value) turns the test RED.
    /// </summary>
    public class SimEventRaiseSiteEndToEndTests
    {
        private static ScenarioVariable IntVar(string name) =>
            new() { Name = name, Type = DslValueType.Int, Scope = VarScope.Global };

        /// <summary>A single trigger subscribing to <paramref name="eventKind"/> for <paramref name="factionSlot"/>
        /// that READS <c>event.<paramref name="paramName"/></c> into the global "got" variable (so the assertion
        /// checks the PAYLOAD, not merely that the trigger fired).</summary>
        private static ScenarioData EventParamScenario(string eventKind, int factionSlot, string paramName)
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "t" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = eventKind, Faction = factionSlot });
            g.Nodes.Add(new ExprEventParamNode { Id = 2, Name = paramName });
            g.Nodes.Add(new ActionNode { Id = 3, Kind = "set_variable", Variable = "got", Faction = 0 });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 3, TriggerGraph.ActionExecInPort));
            g.DataEdges.Add(new DataEdge(2, TriggerGraph.ExprDataOutPort, 3, TriggerGraph.ActionValueInPort, DataWireType.Int));
            return new ScenarioData { Variables = new[] { IntVar("got") }, TriggerGraphJson = g.ToCanonicalJson() };
        }

        private static (ScenarioDirector Director, DslVarTable Vars) BuildDirector(ScenarioData scenario, DslSimEventFeed feed)
        {
            var vars = new DslVarTable();
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), vars, simEventFeed: feed);
            director.LoadScenario(scenario);
            return (director, vars);
        }

        // ── unit_trained (BuildingSystem) ────────────────────────────────────────────

        /// <summary>A minimal faction whose Barracks category (Melee) has a trainable unit at index 1.</summary>
        private static FactionDefinition TrainFaction()
        {
            var f = new FactionDefinition { Id = "test", DisplayName = "Test" };
            f.Units.Add(new UnitDefinition { Id = "worker", Category = "Worker", Hp = 50f });  // 0
            f.Units.Add(new UnitDefinition { Id = "melee",  Category = "Melee",  Hp = 100f }); // 1 — first-of-Melee (Barracks)
            return f;
        }

        [Fact]
        public void UnitTrained_EndToEnd_ThroughBuildingSystemCompletionSite()
        {
            var world     = new EntityWorld();
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.FromInt(10000));
            resources.Ore[(int)Faction.Player1]       = Fixed.FromInt(10000);
            resources.SupplyCap[(int)Faction.Player1] = 500;
            var sys = new BuildingSystem(buildings, resources, TrainFaction());

            var feed = new DslSimEventFeed();
            sys.SetDslSimEvents(feed);

            // Bump the trained unit's entity id off 0 (a filler) so event.unit is a DISCRIMINATING non-zero value.
            int filler = world.Create(FixedVec3.Zero, Faction.Neutral, Fixed.FromInt(1), Fixed.FromInt(1));
            Assert.Equal(0, filler);

            int b = sys.PlaceBuildingDirect(BuildingType.Barracks, Faction.Player1, FixedVec3.Zero, preBuilt: true);
            Assert.True(sys.TrainUnit(b, resources, chosenUnitIndex: 1)); // queue the Melee unit
            sys.Tick(world, Fixed.FromInt(100));                          // one big-dt tick completes training → raises unit_trained

            // The trained unit is the second entity created → id 1.
            int trained = -1;
            for (int i = 0; i < world.HighWaterMark; i++)
                if (world.IsAlive(i) && world.FactionOf[i] == Faction.Player1) { trained = i; break; }
            Assert.True(trained > 0, "precondition: a Player1 unit was trained with a non-zero id");

            // The director (faction slot 0, reading event.unit) fires from the drained occurrence.
            (ScenarioDirector director, DslVarTable vt) = BuildDirector(EventParamScenario("unit_trained", 0, "unit"), feed);
            director.Tick(world, Fixed.One);
            Assert.Equal(trained, vt.GetInt("got", 0)); // correct slot (Player1→0) AND payload (event.unit == trained id)
        }

        // ── ability_cast (AbilityCastSystem) ─────────────────────────────────────────

        [Fact]
        public void AbilityCast_EndToEnd_ThroughAbilityCastSystemCommitSite()
        {
            var harness = new CastHarness(AbilityTestAbilities.BattleFury());

            var feed = new DslSimEventFeed();
            harness.Cast.SetDslSimEvents(feed);

            // Two fillers so the caster's id (2) is DISTINCT from both its ability registry index (0) and the faction
            // slot (0) — so reading event.caster (payload p0) catches a transposed (p0/p1) payload, not just a fire.
            harness.World.Create(FixedVec3.Zero, Faction.Neutral, Fixed.FromInt(1), Fixed.FromInt(1));
            harness.World.Create(FixedVec3.Zero, Faction.Neutral, Fixed.FromInt(1), Fixed.FromInt(1));
            int caster = harness.Caster("battle_fury", energy: 100);
            Assert.Equal(2, caster);

            harness.IssueAndTick(caster, caster);                        // Self cast → commits, raises ability_cast (slot0, p0=caster, p1=regIdx)
            Assert.True(harness.World.Energy[caster] < Fixed.FromInt(100), "precondition: the cast committed (energy debited)");

            (ScenarioDirector director, DslVarTable vt) = BuildDirector(EventParamScenario("ability_cast", 0, "caster"), feed);
            director.Tick(harness.World, Fixed.One);
            Assert.Equal(caster, vt.GetInt("got", -1)); // correct slot (Player1→0) AND payload (event.caster == caster id, not the regIdx)
        }

        // ── hero_level (HeroXpSystem) ────────────────────────────────────────────────

        [Fact]
        public void HeroLevel_EndToEnd_ThroughHeroXpSystemLevelAdvanceSite()
        {
            var world     = new EntityWorld();
            var modSys    = new ModifierSystem();
            var modifiers = new ModifierStore(world, modSys);
            modSys.AttachStore(modifiers);
            var deaths = new DeathFeed();
            var heroes = new HeroStore();

            int ent = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            int slot = heroes.Mint(new HeroId(42), ent, level: 1, xp: Fixed.Zero,
                maxLevel: 5, baseXp: Fixed.FromInt(50), xpGrowth: Fixed.One, xpShareRadius: Fixed.FromInt(100),
                healthPerLevel: Fixed.Zero, damagePerLevel: Fixed.Zero, armorPerLevel: Fixed.Zero);
            world.HeroIndex[ent] = heroes.PackRef(slot);

            var sys = new HeroXpSystem(heroes, modifiers, deaths);
            var feed = new DslSimEventFeed();
            sys.SetDslSimEvents(feed);

            deaths.Push(FixedVec3.Zero, Faction.Neutral, Fixed.FromInt(60)); // >= the 50 threshold → level 1 → 2
            sys.Tick(world, SimulationLoop.FixedDt);                          // raises hero_level (new level = 2)
            Assert.Equal(2, heroes.Level[slot]);                             // precondition: the hero leveled up

            // Read event.LEVEL (slot 1): value 2, distinct from the hero id (event.hero, slot 0) — a transposed
            // payload would surface the hero id instead of 2.
            (ScenarioDirector director, DslVarTable vt) = BuildDirector(EventParamScenario("hero_level", 0, "level"), feed);
            director.Tick(world, Fixed.One);
            Assert.Equal(2, vt.GetInt("got", -1)); // correct slot (Player1→0) AND payload (event.level == new level)
        }
    }
}
