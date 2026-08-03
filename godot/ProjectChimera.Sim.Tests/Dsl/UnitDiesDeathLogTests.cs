#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Dsl
{
    /// <summary>
    /// DW-367 — the per-tick KillEntity death LOG as the director's <c>unit_dies</c> source. The legacy
    /// <c>_prevFlags</c> Alive-diff merged a same-tick die→recycle→die on one entity slot into a single event
    /// carrying only the last killer's attribution (the first kill's event and credit silently lost), and dropped
    /// the death entirely when the recycled occupant was still alive at collect time. The log surfaces every combat
    /// death with the attribution snapshotted at ITS kill — while preserving the legacy horizon exactly: the
    /// director's own trigger-phase kills never emit (and never ghost-emit after the slot recycles), non-combat
    /// destroys still emit through the flags-diff fallback with −1/−1 attribution, and a log overflow degrades
    /// per-slot to the legacy diff.
    /// </summary>
    public class UnitDiesDeathLogTests
    {
        // ── Fixture helpers (the CustomEventDispatchTests posture) ──────────────

        private static ScenarioVariable IntVar(string name, int initial = 0) =>
            new() { Name = name, Type = DslValueType.Int, Scope = VarScope.Global, Initial = Fixed.FromInt(initial) };

        private static Dictionary<string, (DslValueType Type, VarScope Scope)> DeclMap(ScenarioVariable[] vars)
        {
            var map = new Dictionary<string, (DslValueType, VarScope)>(StringComparer.Ordinal);
            foreach (var v in vars) map[v.Name] = (v.Type, v.Scope);
            return map;
        }

        private static (ScenarioDirector Director, DslVarTable Vars) Build(ScenarioData scenario)
        {
            var vars = new DslVarTable();
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero), vars);
            director.LoadScenario(scenario);
            return (director, vars);
        }

        /// <summary>A param-reading unit_dies counter trigger: <c>cond</c> gates on the occurrence payload and the
        /// counter var increments once per MATCHING occurrence (per-occurrence dispatch semantics).</summary>
        private static TriggerGraph Counter(string name, string cond, string var,
            Dictionary<string, (DslValueType Type, VarScope Scope)> declMap) =>
            TriggerGraph.BuildCustomEventTrigger(
                name, "unit_dies", null, cond,
                null, null, -1, false, var, 0, var + " + 1", declMap, null);

        // ── The DW-367 defect: same-tick die → recycle → die on one slot ────────

        [Fact]
        public void SameTickDieRecycleDie_EmitsBothDeaths_WithPerKillAttribution()
        {
            ScenarioVariable[] vars = { IntVar("total"), IntVar("byA"), IntVar("byB") };
            var declMap = DeclMap(vars);
            TriggerGraph g = Counter("reader", "event.victim >= 0", "total", declMap);
            g.Merge(Counter("creditA", "event.killer == 1", "byA", declMap));
            g.Merge(Counter("creditB", "event.killer == 2", "byB", declMap));

            (ScenarioDirector director, DslVarTable table) = Build(new ScenarioData
            {
                Variables = vars, TriggerGraphJson = g.ToCanonicalJson(),
            });

            var world = new EntityWorld();
            int victim = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(1), Fixed.One);  // id 0
            int atkA   = world.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(10), Fixed.One); // id 1
            int atkB   = world.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(10), Fixed.One); // id 2

            director.Tick(world, Fixed.One); // snapshot all alive

            // Between director ticks (systems 0-14 territory): kill → the free list recycles the slot → kill again.
            DamageResolver.KillEntity(world, victim, Faction.Player2, null, null, null, attackerId: atkA);
            int recycled = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(1), Fixed.One);
            Assert.Equal(victim, recycled); // same slot — the merge the ledger names
            DamageResolver.KillEntity(world, recycled, Faction.Player2, null, null, null, attackerId: atkB);

            director.Tick(world, Fixed.One);

            Assert.Equal(2, table.GetInt("total", 0)); // BOTH deaths surface (legacy diff merged them into one)
            Assert.Equal(1, table.GetInt("byA", 0));   // the first kill's credit is no longer lost
            Assert.Equal(1, table.GetInt("byB", 0));   // and the second kill keeps its own
        }

        [Fact]
        public void DieThenRecycleStillAlive_EmitsTheLostDeath_WithSnapshotFactionAndKiller()
        {
            ScenarioVariable[] vars = { IntVar("p1deaths"), IntVar("credited") };
            var declMap = DeclMap(vars);
            // The counter subscribes to faction-slot-0 (Player1) unit_dies — the emitted Slot must be the VICTIM's
            // faction snapshotted at the kill, not the recycled occupant's (Player2) live SoA value.
            TriggerGraph g = Counter("reader", "event.victim >= 0", "p1deaths", declMap);
            g.Merge(Counter("credit", "event.killer == 1", "credited", declMap));

            (ScenarioDirector director, DslVarTable table) = Build(new ScenarioData
            {
                Variables = vars, TriggerGraphJson = g.ToCanonicalJson(),
            });

            var world = new EntityWorld();
            int victim = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(1), Fixed.One);  // id 0
            int atk    = world.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(10), Fixed.One); // id 1

            director.Tick(world, Fixed.One); // snapshot alive

            DamageResolver.KillEntity(world, victim, Faction.Player2, null, null, null, attackerId: atk);
            int recycled = world.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(5), Fixed.One);
            Assert.Equal(victim, recycled); // recycled occupant is ALIVE at collect time (a different faction, too)

            director.Tick(world, Fixed.One);

            Assert.True(world.IsAlive(recycled));           // the new occupant lives on untouched
            Assert.Equal(1, table.GetInt("p1deaths", 0));   // legacy diff saw alive→alive and lost this death entirely
            Assert.Equal(1, table.GetInt("credited", 0));   // with the killer snapshotted at the kill
        }

        // ── Legacy-horizon parity: the director's own trigger-phase kills ───────

        [Fact]
        public void TriggerPhaseKill_NeverSurfacesAsUnitDies_AndNeverGhostsAfterRecycle()
        {
            // A match_start trigger whose run_effect deals 1000 matrix damage to the lowest-id alive entity — the
            // kill happens DURING the director's trigger phase, AFTER this tick's event collection. The legacy diff
            // never surfaced such kills (the end-of-tick snapshot records them dead before the next collect); the
            // death log must preserve that horizon: no emission this tick, next tick, or — the ghost hazard — after
            // the slot recycles back to a live unit and later ticks re-snapshot it alive.
            ScenarioVariable[] vars = { IntVar("deaths") };
            var declMap = DeclMap(vars);
            TriggerGraph g = TriggerGraph.BuildRunEffectTrigger(
                "killer", "match_start", new DamageEffect(Fixed.FromInt(1000), DamageType.Normal));
            g.Merge(Counter("reader", "event.victim >= 0", "deaths", declMap));

            (ScenarioDirector director, DslVarTable table) = Build(new ScenarioData
            {
                Variables = vars, TriggerGraphJson = g.ToCanonicalJson(),
            });

            var world = new EntityWorld();
            int e = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(10), Fixed.One);

            director.Tick(world, Fixed.One);            // match_start → run_effect kills e in the trigger phase
            Assert.False(world.IsAlive(e));             // the kill really went through KillEntity
            Assert.Equal(0, table.GetInt("deaths", 0)); // collected before the kill — parity with the legacy diff

            director.Tick(world, Fixed.One);            // snapshot already recorded it dead; log wiped end-of-tick
            Assert.Equal(0, table.GetInt("deaths", 0));

            int recycled = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(5), Fixed.One);
            Assert.Equal(e, recycled);
            director.Tick(world, Fixed.One);            // re-snapshots the slot ALIVE — a stale record would arm here
            director.Tick(world, Fixed.One);
            Assert.Equal(0, table.GetInt("deaths", 0)); // no ghost emission from the trigger-phase kill

            // The path still works normally afterwards: a real between-ticks kill emits exactly once.
            DamageResolver.KillEntity(world, recycled, Faction.Player2, null, null, null);
            director.Tick(world, Fixed.One);
            Assert.Equal(1, table.GetInt("deaths", 0));
        }

        // ── Fallback parity: non-combat destroys and log overflow ───────────────

        [Fact]
        public void NonCombatDestroy_StillEmitsThroughTheDiffFallback_WithMinusOneAttribution()
        {
            ScenarioVariable[] vars = { IntVar("deaths"), IntVar("anonymous") };
            var declMap = DeclMap(vars);
            TriggerGraph g = Counter("reader", "event.victim >= 0", "deaths", declMap);
            g.Merge(Counter("anon", "event.killer == 0 - 1 && event.killer_faction == 0 - 1", "anonymous", declMap));

            (ScenarioDirector director, DslVarTable table) = Build(new ScenarioData
            {
                Variables = vars, TriggerGraphJson = g.ToCanonicalJson(),
            });

            var world = new EntityWorld();
            int unit = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(5), Fixed.One);

            director.Tick(world, Fixed.One); // snapshot alive
            world.Destroy(unit);             // editor delete / scripted removal — never KillEntity, no log record
            director.Tick(world, Fixed.One);

            Assert.Equal(1, table.GetInt("deaths", 0));    // the flags-diff fallback still emits it
            Assert.Equal(1, table.GetInt("anonymous", 0)); // with the legacy −1/−1 attribution
        }

        [Fact]
        public void LogOverflow_DegradesPerSlotToTheLegacyDiff_NoDeathLost()
        {
            // CAPACITY victims fill the log; the extras drop their records and must STILL emit via the per-slot
            // flags-diff fallback (the pre-log behavior) — an overflow never loses a death the old diff surfaced.
            ScenarioVariable[] vars = { IntVar("kills") };
            var declMap = DeclMap(vars);
            TriggerGraph g = Counter("reader", "event.killer == 0", "kills", declMap);

            (ScenarioDirector director, DslVarTable table) = Build(new ScenarioData
            {
                Variables = vars, TriggerGraphJson = g.ToCanonicalJson(),
            });

            var world = new EntityWorld();
            int attacker = world.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(100), Fixed.One); // id 0
            int victims  = DeathLog.CAPACITY + 2;
            for (int i = 0; i < victims; i++)
                world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(1), Fixed.One); // ids 1..victims

            director.Tick(world, Fixed.One); // snapshot all alive

            for (int id = 1; id <= victims; id++)
                DamageResolver.KillEntity(world, id, Faction.Player2, null, null, null, attackerId: attacker);
            Assert.Equal(DeathLog.CAPACITY, world.DeathLog.Count); // the last two records deterministically dropped

            director.Tick(world, Fixed.One);
            Assert.Equal(victims, table.GetInt("kills", 0)); // every death emitted, all credited to the attacker
        }
    }
}
