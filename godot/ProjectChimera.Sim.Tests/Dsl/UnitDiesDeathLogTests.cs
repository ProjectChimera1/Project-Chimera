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
    /// The <c>unit_dies</c> emission horizon, end to end.
    ///
    /// <para><b>DW-367 (the origin).</b> The per-tick <c>KillEntity</c> death LOG replaced the <c>_prevFlags</c>
    /// Alive-diff as the director's primary <c>unit_dies</c> source. The diff merged a same-tick die→recycle→die on
    /// one entity slot into a single event carrying only the last killer's attribution (the first kill's event and
    /// credit silently lost), and dropped the death entirely when the recycled occupant was still alive at collect
    /// time. The log surfaces every combat death with the attribution snapshotted at ITS kill.</para>
    ///
    /// <para><b>DW-548 (the deferred rail).</b> DW-367 deliberately preserved the legacy HORIZON, so a kill the
    /// director's own triggers caused — a <c>run_effect</c> that damages a unit to death during the trigger phase,
    /// i.e. AFTER <c>CollectEvents</c> already ran — was invisible to <c>unit_dies</c> forever: <c>UpdateSnapshots</c>
    /// wiped the log and snapshotted the slot dead before the next collect could see it. A scenario author
    /// subscribing <c>unit_dies</c> never saw the deaths their own triggers caused. Those records are now DEFERRED
    /// onto a director-owned rail and emitted on the FOLLOWING tick. Deliberately not left in the log: "the log is
    /// empty at the tick boundary" is what keeps it out of <c>SimChecksum</c> and is what
    /// <c>SaveGameState.CaptureFrom</c> asserts (DW-551), and that invariant is pinned here too.</para>
    ///
    /// <para><b>DW-549 (the loosened gate).</b> Emitting a deferred record requires dropping the <c>_prevFlags</c>
    /// alive gate on LOGGED records: the victim's slot was snapshotted dead at the end of the kill tick, so the gate
    /// swallowed exactly what DW-548 set out to surface — including the residual "trigger-phase kill at T, then
    /// recycle+die at T+1" case, which lost BOTH deaths. A record on the log or the rail is itself proof the entity
    /// was alive and died, so it emits unconditionally. The flags-diff FALLBACK keeps its gate (a never-alive dead
    /// slot is indistinguishable from a never-used one).</para>
    ///
    /// <para><b>DW-674 (lossless log).</b> The log used to drop deterministically at 256 records, arguing the
    /// per-slot flags-diff fallback still surfaced the dropped death. That argument is falsified below: for a
    /// die→recycle-into-a-live-occupant slot — the exact case the log was added to cover — the fallback sees
    /// alive→alive and emits NOTHING, so an overflow lost a whole occurrence, and <c>unit_dies</c> triggers mutate
    /// folded sim state. The log now grows instead (the DW-616 treatment).</para>
    ///
    /// Godot-free, Tier-1.
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

        /// <summary>An ORDER-recording unit_dies trigger: each occurrence appends <c>event.killer + 1</c> as a
        /// decimal digit (<c>var * 10 + event.killer + 1</c>), so the final value spells the emission sequence and a
        /// reordered drain reads differently rather than merely counting the same.</summary>
        private static TriggerGraph Sequencer(string name, string var,
            Dictionary<string, (DslValueType Type, VarScope Scope)> declMap) =>
            TriggerGraph.BuildCustomEventTrigger(
                name, "unit_dies", null, "event.victim >= 0",
                null, null, -1, false, var, 0, var + " * 10 + event.killer + 1", declMap, null);

        /// <summary>A match_start trigger whose run_effect deals lethal matrix damage to the run_effect anchor (the
        /// lowest-id alive entity, which is also the caster — so the logged killer id equals the victim id). The kill
        /// lands DURING the director's trigger phase, after that tick's event collection.</summary>
        private static TriggerGraph TriggerPhaseKiller(string name) =>
            TriggerGraph.BuildRunEffectTrigger(name, "match_start",
                new DamageEffect(Fixed.FromInt(1000), DamageType.Normal));

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

        // ── DW-548 — the director's own trigger-phase kills surface on the FOLLOWING tick ──

        /// <summary>
        /// The DW-548 defect and its bound. A trigger-phase kill is collected too late to emit on its own tick (the
        /// collect already ran), so it emits on the NEXT one — and then exactly once, never again, even after the
        /// slot recycles back to a live unit and later ticks re-snapshot it alive (the ghost hazard the deferred rail
        /// has to avoid). RED before the fix at the second assertion: the legacy horizon emitted 0, forever.
        /// </summary>
        [Fact]
        public void TriggerPhaseKill_SurfacesAsUnitDies_OnTheFollowingTick_AndNeverGhostsAfterRecycle()
        {
            ScenarioVariable[] vars = { IntVar("deaths") };
            var declMap = DeclMap(vars);
            TriggerGraph g = TriggerPhaseKiller("killer");
            g.Merge(Counter("reader", "event.victim >= 0", "deaths", declMap));

            (ScenarioDirector director, DslVarTable table) = Build(new ScenarioData
            {
                Variables = vars, TriggerGraphJson = g.ToCanonicalJson(),
            });

            var world = new EntityWorld();
            int e = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(10), Fixed.One);

            director.Tick(world, Fixed.One);            // match_start → run_effect kills e in the trigger phase
            Assert.False(world.IsAlive(e));             // the kill really went through KillEntity
            Assert.Equal(0, table.GetInt("deaths", 0)); // collected BEFORE the kill — still not this tick

            director.Tick(world, Fixed.One);            // …and the deferred rail surfaces it here
            Assert.Equal(1, table.GetInt("deaths", 0));

            int recycled = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(5), Fixed.One);
            Assert.Equal(e, recycled);
            director.Tick(world, Fixed.One);            // re-snapshots the slot ALIVE — a stale record would arm here
            director.Tick(world, Fixed.One);
            Assert.Equal(1, table.GetInt("deaths", 0)); // consumed exactly once: no ghost, no repeat

            // The path still works normally afterwards: a real between-ticks kill emits exactly once.
            DamageResolver.KillEntity(world, recycled, Faction.Player2, null, null, null);
            director.Tick(world, Fixed.One);
            Assert.Equal(2, table.GetInt("deaths", 0));
        }

        /// <summary>
        /// The invariant the deferred rail exists to protect: the records ride the DIRECTOR, not the log. "The
        /// DeathLog is empty at every tick boundary" is what keeps it out of <c>SimChecksum</c> and is what
        /// <c>SaveGameState.CaptureFrom</c> asserts (DW-551) — carrying trigger-phase records inside the log would
        /// have created unfolded state that crosses the checksum boundary and broken every between-ticks save.
        /// </summary>
        [Fact]
        public void TriggerPhaseKill_LeavesTheDeathLogEmptyAtTheTickBoundary()
        {
            ScenarioVariable[] vars = { IntVar("deaths") };
            var declMap = DeclMap(vars);
            TriggerGraph g = TriggerPhaseKiller("killer");
            g.Merge(Counter("reader", "event.victim >= 0", "deaths", declMap));

            (ScenarioDirector director, DslVarTable table) = Build(new ScenarioData
            {
                Variables = vars, TriggerGraphJson = g.ToCanonicalJson(),
            });

            var world = new EntityWorld();
            world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(10), Fixed.One);

            director.Tick(world, Fixed.One);                 // the trigger-phase kill lands…
            Assert.Equal(0, world.DeathLog.Count);           // …and the log is STILL drained at the boundary
            director.Tick(world, Fixed.One);
            Assert.Equal(0, world.DeathLog.Count);
            Assert.Equal(1, table.GetInt("deaths", 0));      // the record was deferred, not discarded
        }

        // ── DW-549 — the loosened alive gate ────────────────────────────────────

        /// <summary>
        /// The exact residual class DW-549 names: a trigger-phase kill at T whose slot is recycled and killed AGAIN
        /// at T+1 before the director runs. Pre-fix BOTH deaths were lost — the T kill because the log was wiped, the
        /// T+1 kill because <c>_prevFlags</c> had already snapshotted the slot dead so the alive gate swallowed its
        /// record. Both must now surface, each with its own attribution, oldest first.
        ///
        /// <para><b>Story 15-23 (DW-775) attribution update.</b> The deferred record's killer here is slot 0
        /// itself (the run_effect caster == the victim), and slot 0 is RECYCLED before the record emits — so its
        /// <c>event.killer</c> now degrades to −1 (unknown) instead of naming id 0, which by emit time denotes the
        /// slot's NEW occupant (the T+1 victim). Emitting 0 would let a creator's <c>event.killer == 0</c>
        /// comparison match a unit that never killed anything — the exact ABA misattribution 15-23 closes. The
        /// between-ticks attacker (id 1, never recycled) keeps its credit unchanged.</para>
        /// </summary>
        [Fact]
        public void TriggerPhaseKillAtT_ThenRecycleAndDieAtT1_SurfacesBothDeathsInKillOrder()
        {
            ScenarioVariable[] vars = { IntVar("total"), IntVar("byTrigger"), IntVar("byAttacker"), IntVar("seq") };
            var declMap = DeclMap(vars);
            TriggerGraph g = TriggerPhaseKiller("killer");
            g.Merge(Counter("reader", "event.victim >= 0", "total", declMap));
            g.Merge(Counter("trig", "event.killer == -1", "byTrigger", declMap));  // run_effect killer's slot RECYCLED → degraded to unknown (DW-775)
            g.Merge(Counter("atk",  "event.killer == 1", "byAttacker", declMap));  // the between-ticks attacker, id 1 (never recycled — credit kept)
            g.Merge(Sequencer("order", "seq", declMap));

            (ScenarioDirector director, DslVarTable table) = Build(new ScenarioData
            {
                Variables = vars, TriggerGraphJson = g.ToCanonicalJson(),
            });

            var world = new EntityWorld();
            int v   = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(10), Fixed.One); // id 0 — the anchor
            int atk = world.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(10), Fixed.One); // id 1
            Assert.Equal(0, v);
            Assert.Equal(1, atk);

            director.Tick(world, Fixed.One);   // match_start → run_effect kills slot 0 in the trigger phase
            Assert.False(world.IsAlive(v));
            Assert.Equal(0, table.GetInt("total", 0));

            // …and before the next director tick the slot is recycled and its new occupant is killed too.
            int recycled = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(1), Fixed.One);
            Assert.Equal(v, recycled);
            DamageResolver.KillEntity(world, recycled, Faction.Player2, null, null, null, attackerId: atk);

            director.Tick(world, Fixed.One);

            Assert.Equal(2, table.GetInt("total", 0));      // RED pre-fix: 0 — both deaths were lost
            Assert.Equal(1, table.GetInt("byTrigger", 0));  // the deferred kill emits, killer degraded to −1 (recycled)
            Assert.Equal(1, table.GetInt("byAttacker", 0)); // the recycled occupant's kill keeps its own live killer
            // Order: deferred (killer −1 → digit 0) BEFORE this tick's log record (killer id 1 → digit 2): "02" = 2.
            Assert.Equal(2, table.GetInt("seq", 0));
        }

        /// <summary>
        /// The other half of the loosened gate, stated on its own: an entity created and killed entirely before the
        /// director's FIRST tick was never snapshotted alive, so the legacy horizon suppressed it. A logged record is
        /// proof the entity lived and died; it emits.
        /// </summary>
        [Fact]
        public void KillBeforeTheFirstDirectorTick_StillEmits_EvenThoughTheSlotWasNeverSnapshottedAlive()
        {
            ScenarioVariable[] vars = { IntVar("deaths"), IntVar("credited") };
            var declMap = DeclMap(vars);
            TriggerGraph g = Counter("reader", "event.victim >= 0", "deaths", declMap);
            g.Merge(Counter("credit", "event.killer == 0", "credited", declMap));

            (ScenarioDirector director, DslVarTable table) = Build(new ScenarioData
            {
                Variables = vars, TriggerGraphJson = g.ToCanonicalJson(),
            });

            var world = new EntityWorld();
            int atk = world.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(10), Fixed.One); // id 0
            int v   = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(1), Fixed.One);  // id 1
            DamageResolver.KillEntity(world, v, Faction.Player2, null, null, null, attackerId: atk);

            director.Tick(world, Fixed.One); // the FIRST tick: _prevFlags is all-clear, nothing was ever "was alive"

            Assert.Equal(1, table.GetInt("deaths", 0));   // RED pre-fix: 0
            Assert.Equal(1, table.GetInt("credited", 0)); // with the real attribution, not the −1/−1 fallback
        }

        /// <summary>Anti-false-positive fence on the loosened gate: with no deaths at all, no slot — alive, never
        /// used, or past the high-water mark — may emit anything.</summary>
        [Fact]
        public void NoDeaths_EmitsNothing_AcrossManyTicks()
        {
            ScenarioVariable[] vars = { IntVar("deaths") };
            var declMap = DeclMap(vars);
            TriggerGraph g = Counter("reader", "event.victim >= 0", "deaths", declMap);

            (ScenarioDirector director, DslVarTable table) = Build(new ScenarioData
            {
                Variables = vars, TriggerGraphJson = g.ToCanonicalJson(),
            });

            var world = new EntityWorld();
            world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(10), Fixed.One);
            world.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(10), Fixed.One);

            for (int i = 0; i < 5; i++) director.Tick(world, Fixed.One);
            Assert.Equal(0, table.GetInt("deaths", 0));
        }

        // ── Fallback parity: non-combat destroys ────────────────────────────────

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

        /// <summary>The fallback's OWN gate must stay: a slot that was never snapshotted alive and carries no log
        /// record is indistinguishable from a never-used slot, so it must emit nothing. (DW-549 loosened the gate on
        /// LOGGED records only.)</summary>
        [Fact]
        public void NonCombatDestroy_BeforeTheFirstTick_EmitsNothing_TheFallbackKeepsItsAliveGate()
        {
            ScenarioVariable[] vars = { IntVar("deaths") };
            var declMap = DeclMap(vars);
            TriggerGraph g = Counter("reader", "event.victim >= 0", "deaths", declMap);

            (ScenarioDirector director, DslVarTable table) = Build(new ScenarioData
            {
                Variables = vars, TriggerGraphJson = g.ToCanonicalJson(),
            });

            var world = new EntityWorld();
            int unit = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(5), Fixed.One);
            world.Destroy(unit);             // no KillEntity → no record, and never snapshotted alive

            director.Tick(world, Fixed.One);
            director.Tick(world, Fixed.One);
            Assert.Equal(0, table.GetInt("deaths", 0));
        }

        // ── DW-674 — the log is lossless, not capped ────────────────────────────

        /// <summary>
        /// The claim the old cap rested on ("a dropped record still surfaces through the per-slot flags diff, so only
        /// the attribution refinement is lost") is FALSE for the very case the log was added to cover. Fill the log to
        /// the old cap with unrelated deaths, then kill one more unit and recycle its slot into a LIVE occupant: the
        /// diff sees alive→alive for that slot and emits nothing at all, so the overflow lost a whole <c>unit_dies</c>
        /// occurrence — and unit_dies triggers mutate folded sim state.
        /// </summary>
        [Fact]
        public void OverflowedRecord_OnARecycledSlot_IsNotCoveredByTheDiffFallback_SoTheLogMustNotDrop()
        {
            ScenarioVariable[] vars = { IntVar("kills") };
            var declMap = DeclMap(vars);
            TriggerGraph g = Counter("reader", "event.killer == 0", "kills", declMap);

            (ScenarioDirector director, DslVarTable table) = Build(new ScenarioData
            {
                Variables = vars, TriggerGraphJson = g.ToCanonicalJson(),
            });

            var world = new EntityWorld();
            int attacker = world.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(100), Fixed.One); // id 0
            int fillers  = DeathLog.INITIAL_CAPACITY;                                                    // ids 1..256
            for (int i = 0; i < fillers; i++)
                world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(1), Fixed.One);
            int v = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(1), Fixed.One);           // id 257

            director.Tick(world, Fixed.One); // snapshot all alive

            for (int id = 1; id <= fillers; id++)   // exactly fills the pre-fix ring
                DamageResolver.KillEntity(world, id, Faction.Player2, null, null, null, attackerId: attacker);
            DamageResolver.KillEntity(world, v, Faction.Player2, null, null, null, attackerId: attacker);

            int recycled = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(1), Fixed.One);
            Assert.Equal(v, recycled);          // the free list is LIFO — the slot comes straight back, ALIVE
            Assert.True(world.IsAlive(v));

            director.Tick(world, Fixed.One);

            // RED pre-fix: 256 — the 257th record was dropped and the flags diff saw the slot alive→alive.
            Assert.Equal(fillers + 1, table.GetInt("kills", 0));
        }

        /// <summary>A tick with more deaths than the old cap records — and emits — every one of them.</summary>
        [Fact]
        public void OverCapacityTick_RecordsAndEmitsEveryDeath_InsteadOfDroppingAt256()
        {
            ScenarioVariable[] vars = { IntVar("kills") };
            var declMap = DeclMap(vars);
            TriggerGraph g = Counter("reader", "event.killer == 0", "kills", declMap);

            (ScenarioDirector director, DslVarTable table) = Build(new ScenarioData
            {
                Variables = vars, TriggerGraphJson = g.ToCanonicalJson(),
            });

            var world = new EntityWorld();
            int attacker = world.Create(FixedVec3.Zero, Faction.Player2, Fixed.FromInt(100), Fixed.One); // id 0
            int victims  = DeathLog.INITIAL_CAPACITY + 2;
            for (int i = 0; i < victims; i++)
                world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(1), Fixed.One); // ids 1..victims

            director.Tick(world, Fixed.One); // snapshot all alive

            for (int id = 1; id <= victims; id++)
                DamageResolver.KillEntity(world, id, Faction.Player2, null, null, null, attackerId: attacker);

            Assert.Equal(victims, world.DeathLog.Count);                          // RED pre-fix: 256 (two dropped)
            Assert.True(world.DeathLog.Capacity > DeathLog.INITIAL_CAPACITY);     // it grew rather than dropped

            director.Tick(world, Fixed.One);
            Assert.Equal(victims, table.GetInt("kills", 0)); // every death emitted, all credited to the attacker
            Assert.Equal(0, world.DeathLog.Count);           // and drained at the boundary as always
        }

        // ── DW-674 — the DeathLog buffer itself (unit level) ────────────────────

        /// <summary>The no-golden-move pin (the DW-616 posture): a tick at or under the old cap appends at exactly
        /// the same indices with no reallocation, so it is bit-identical to the pre-fix fixed buffer. Growth is
        /// observable only on a tick that would previously have overflowed.</summary>
        [Fact]
        public void Push_AtInitialCapacity_DoesNotGrow_SoSubCapTicksAreIdenticalToThePreFixBuffer()
        {
            var log = new DeathLog();
            Assert.Equal(DeathLog.INITIAL_CAPACITY, log.Capacity);

            for (int i = 0; i < DeathLog.INITIAL_CAPACITY; i++) log.Push(i, 0, i + 1000, 1);

            Assert.Equal(DeathLog.INITIAL_CAPACITY, log.Count);
            Assert.Equal(DeathLog.INITIAL_CAPACITY, log.Capacity); // no reallocation on the whole common path
        }

        /// <summary>Growth preserves push order index-for-index across the boundary, on every lane — the entire
        /// contract the drain (which reads <c>[0, Count)</c>) depends on.</summary>
        [Fact]
        public void Push_PastInitialCapacity_GrowsAndKeepsEveryLaneInPushOrder()
        {
            var log = new DeathLog();
            const int n = DeathLog.INITIAL_CAPACITY + 37;
            for (int i = 0; i < n; i++) log.Push(i, i % 4, i + 1000, (i + 1) % 4);

            Assert.Equal(n, log.Count);
            Assert.True(log.Capacity >= n);
            for (int i = 0; i < n; i++)
            {
                Assert.Equal(i, log.VictimAt(i));
                Assert.Equal(i % 4, log.VictimSlotAt(i));
                Assert.Equal(i + 1000, log.KillerAt(i));
                Assert.Equal((i + 1) % 4, log.KillerSlotAt(i));
            }
        }

        /// <summary>Clear is a count-only reset that RETAINS the grown capacity (re-shrinking would reallocate every
        /// busy tick), and capacity is unobservable to the sim — two peers that grew differently drain identically.
        /// </summary>
        [Fact]
        public void Clear_ResetsCountButRetainsGrownCapacity()
        {
            var log = new DeathLog();
            for (int i = 0; i < DeathLog.INITIAL_CAPACITY + 1; i++) log.Push(i, 0, -1, -1);
            int grown = log.Capacity;
            Assert.True(grown > DeathLog.INITIAL_CAPACITY);

            log.Clear();
            Assert.Equal(0, log.Count);
            Assert.Equal(grown, log.Capacity);

            var fresh = new DeathLog();
            for (int i = 0; i < 3; i++) { log.Push(i, 0, -1, -1); fresh.Push(i, 0, -1, -1); }
            Assert.Equal(fresh.Count, log.Count);
            for (int i = 0; i < fresh.Count; i++) Assert.Equal(fresh.VictimAt(i), log.VictimAt(i)); // capacity is inert
        }
    }
}
