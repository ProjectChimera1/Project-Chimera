#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.AI;        // AiOpponentSystem / AiDifficulty
using ProjectChimera.Combat;    // ProjectileStore / CombatEventQueue / DeathFeed / DamageType / ArmorType
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions; // WinCondition / WinPresetKind
using ProjectChimera.Core.Sim;  // DslVarReadback / TriggerFireLog
using ProjectChimera.Dsl;       // DslVarTable / DslLoopState / DslEventQueue / DslVarDecl / DslTimerDecl
using ProjectChimera.Economy;   // BuildingSystem (AiOpponentSystem dep)
using ProjectChimera.Effects;   // ModifierStore / ModifierSystem / Modifier / PersistentEffect / StackRule
using Xunit;

namespace ProjectChimera.Sim.Tests.Sim
{
    /// <summary>
    /// DW-196 — the GENERALIZED reset-completeness sweep: every sibling store <c>SimulationHost.ClearForReset</c>
    /// (src/Core/Sim/SimulationHost.cs) fans out over is driven through the SAME exhaustive machinery DW-19 built
    /// for <c>EntityWorld</c> (<see cref="ClearCompletenessSweep"/>), one <c>[Theory]</c> case per ClearForReset
    /// line. Before this, those ~24 stores' resets were verified only by HAND-ENUMERATED assertions — the ledger
    /// demonstrated four independent mutants (<c>ProjectileStore.Clear()</c> minus <c>Array.Clear(SourceId)</c> /
    /// minus <c>Array.Clear(Speed)</c>; <c>HeroStore.Clear()</c> minus <c>Level</c>+<c>Xp</c>;
    /// <c>ItemStore.Clear()</c> minus <c>DefId</c>+<c>Charges</c>) each shipping GREEN through the entire suite.
    /// Every one of those mutants now fails its case here at the named field.
    ///
    /// <para><b>Per case:</b> a fresh and a dirty instance are built over SHARED dependency references, the dirty
    /// one is dirtied across every swept field (hand-dirty for non-array state, reflection-driven synthetic fill
    /// for arrays), per-field divergence is REQUIRED (an undirtied field is a blind spot), the store's exact
    /// ClearForReset call runs, and the cleared instance must be field-equal to the fresh one. A NEW field added
    /// to any of these stores auto-enters its sweep: forget it in the reset and the case goes red; leave it
    /// undirtiable and the precondition goes red until the fixture (or a justified allowlist entry) covers it.</para>
    ///
    /// <para><b>Allowlists are the honest edges of the guarantee.</b> Three shapes recur, each named and justified
    /// per fixture: (1) shared dependency references and construction-lifetime config a reset deliberately
    /// preserves; (2) documented COUNT-GATED buffers whose Clear only zeroes the count and whose stale tail is
    /// never read or folded (<c>CombatEventQueue</c>/<c>DeathFeed</c>/<c>DslSimEventFeed</c>/<c>DslEventQueue</c>);
    /// (3) <c>TriggerEnabledStore._enabled</c>, whose stale tail is additionally reachable through the
    /// out-of-range-returns-true <c>IsEnabled</c> — that reachability question is DW-197 (open); its allowlist
    /// entry cites it and must be removed when DW-197 closes.</para>
    ///
    /// <para><c>EntityWorld.Clear()</c> — the first ClearForReset line — is covered by its original DW-19 class,
    /// <see cref="EntityWorldClearCompletenessTests"/>, now driven through the same shared machinery.</para>
    /// </summary>
    public class StoreClearCompletenessTests
    {
        // ── The case table: one entry per ClearForReset line (SimulationHost.cs:352-381), in its order. ─────────

        private static readonly Dictionary<string, Func<StoreResetFixture>> CaseTable = new()
        {
            ["Nodes.Clear() — ResourceNodeStore"]        = NodesCase,
            ["Resources.Clear() — ResourceStore"]        = ResourcesCase,
            ["Buildings.Clear() — BuildingStore"]        = BuildingsCase,
            ["Projectiles.Clear() — ProjectileStore"]    = ProjectilesCase,
            ["Heroes.Clear() — HeroStore"]               = HeroesCase,
            ["Items.Clear() — ItemStore"]                = ItemsCase,
            ["Modifiers.Clear() — ModifierStore"]        = ModifiersCase,
            ["Research.Clear() — ResearchStore"]         = ResearchCase,
            ["Vars.Clear() — DslVarTable"]               = VarsCase,
            ["LoopState.Clear() — DslLoopState"]         = LoopStateCase,
            ["Readback.Clear() — DslVarReadback"]        = ReadbackCase,
            ["DslEvents.Clear() — DslEventQueue"]        = DslEventsCase,
            ["WinState.Clear() — WinStateStore"]         = WinStateCase,
            ["Alliances.Clear() — AllianceStore"]        = AlliancesCase,
            ["TriggerEnabled.Clear() — TriggerEnabledStore"] = TriggerEnabledCase,
            ["TriggerFireLog.Clear() — TriggerFireLog"]  = TriggerFireLogCase,
            ["DslSimEvents.Clear() — DslSimEventFeed"]   = DslSimEventsCase,
            ["WinCon.ResetConfig() — WinConditionSystem"] = WinConCase,
            ["CombatEvents.Clear() — CombatEventQueue"]  = CombatEventsCase,
            ["_deathFeed.Clear() — DeathFeed"]           = DeathFeedCase,
            ["Fog.Reset() — FogOfWarSystem"]             = FogCase,
            ["MatchStats.Reset() — MatchStats"]          = MatchStatsCase,
            ["_ai.ResetForMatch() — AiOpponentSystem"]   = AiCase,
            ["_loop.ResetTick() — SimulationLoop"]       = LoopCase,
        };

        public static TheoryData<string> CaseNames()
        {
            var data = new TheoryData<string>();
            foreach (string name in CaseTable.Keys) data.Add(name);
            return data;
        }

        [Theory]
        [MemberData(nameof(CaseNames))]
        public void Reset_LeavesStoreEqualToFreshlyConstructed_ExhaustiveReflectionSweep(string caseName)
            => ClearCompletenessSweep.AssertClearRestoresFreshState(CaseTable[caseName]());

        /// <summary>
        /// The table above must stay in lockstep with <c>SimulationHost.ClearForReset</c>'s fan-out: 24 sibling
        /// resets today (every line of the method body except <c>World.Clear()</c>, which
        /// <see cref="EntityWorldClearCompletenessTests"/> owns). A NEW store added to ClearForReset without a
        /// sweep case here re-opens exactly the hand-enumerated blind spot DW-196 closed — this count is the
        /// tripwire that forces the addition. (Deliberately a count, not a parse of the method body: the store
        /// set changes a few times per epic at most, and a count fails loudly and cheaply.)
        /// </summary>
        [Fact]
        public void CaseTable_CoversEveryClearForResetSibling()
        {
            Assert.Equal(24, CaseTable.Count);
        }

        // ── Fixtures, in ClearForReset order ───────────────────────────────────────────────────

        private static StoreResetFixture NodesCase()
        {
            var dirty = new ResourceNodeStore();
            return new StoreResetFixture("Nodes.Clear()", new ResourceNodeStore(), dirty, dirty.Clear)
            {
                // Create drives the real _count path; the synthetic fill then dirties every array element.
                DirtyNonArrayState = () =>
                {
                    dirty.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.FromInt(2)),
                                 Fixed.FromInt(100), Fixed.One, 3);
                    dirty.Create(new FixedVec3(Fixed.FromInt(4), Fixed.Zero, Fixed.FromInt(5)),
                                 Fixed.FromInt(200), Fixed.One, 2);
                },
            };
        }

        private static StoreResetFixture ResourcesCase()
        {
            Fixed startingOre = Fixed.FromInt(50); // identical ctor arg on both sides (construction config)
            var dirty = new ResourceStore(startingOre);
            return new StoreResetFixture("Resources.Clear()", new ResourceStore(startingOre), dirty, dirty.Clear)
            {
                // The three Story-4.4 supply-config properties are normally written by ConfigureSupply at
                // scenario-apply; poked directly here so the sweep pins that Clear() restores their defaults.
                DirtyNonArrayState = () =>
                {
                    ClearCompletenessSweep.Poke(dirty, "StartingSupplyCap", 55);
                    ClearCompletenessSweep.Poke(dirty, "SupplyHardCeiling", (int?)99);
                    ClearCompletenessSweep.Poke(dirty, "SupplyGatingEnabled", false);
                },
                Allowlist = new[]
                {
                    // Ctor-lifetime config: Clear() re-seeds the P1/P2 starting ore FROM this field (the inverse-of-
                    // ctor contract) — it is construction state, not match state, and both instances share its value.
                    "_startingOre",
                },
            };
        }

        private static StoreResetFixture BuildingsCase()
        {
            var dirty = new BuildingStore();
            return new StoreResetFixture("Buildings.Clear()", new BuildingStore(), dirty, dirty.Clear)
            {
                DirtyNonArrayState = () =>
                {
                    int a = dirty.Create(new FixedVec3(Fixed.FromInt(1), Fixed.Zero, Fixed.Zero),
                                         Faction.Player1, BuildingType.CommandCenter);
                    dirty.Create(new FixedVec3(Fixed.FromInt(9), Fixed.Zero, Fixed.Zero),
                                 Faction.Player2, BuildingType.Barracks);
                    dirty.Destroy(a); // free-list + _freeCount off their fresh values
                    // ShopStock is JAGGED with all-null fresh slots; the in-place fill cannot invent inner arrays,
                    // so seed one non-null stock (as a shop placement would) for the fill to overwrite.
                    dirty.ShopStock[0] = new[] { "potion" };
                },
            };
        }

        private static StoreResetFixture ProjectilesCase()
        {
            var dirty = new ProjectileStore();
            return new StoreResetFixture("Projectiles.Clear()", new ProjectileStore(), dirty, dirty.Clear)
            {
                DirtyNonArrayState = () =>
                {
                    // Spawn drives the real allocation path (_nextId); Destroy drives the free-list (_freeCount).
                    dirty.Spawn(new FixedVec3(Fixed.One, Fixed.Zero, Fixed.Zero), 1,
                                new FixedVec3(Fixed.FromInt(5), Fixed.Zero, Fixed.Zero),
                                Fixed.FromInt(9), DamageType.Normal, ArmorType.Light, Faction.Player1,
                                Fixed.FromInt(20));
                    dirty.Spawn(new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.One), 2,
                                new FixedVec3(Fixed.Zero, Fixed.Zero, Fixed.FromInt(5)),
                                Fixed.FromInt(4), DamageType.Pierce, ArmorType.Heavy, Faction.Player2,
                                Fixed.FromInt(18));
                    dirty.Destroy(0);
                },
            };
        }

        private static StoreResetFixture HeroesCase()
        {
            var dirty = new HeroStore();
            return new StoreResetFixture("Heroes.Clear()", new HeroStore(), dirty, dirty.Clear)
            {
                DirtyNonArrayState = () =>
                {
                    dirty.Mint(new HeroId(11), entityId: 3, level: 4, xp: Fixed.FromInt(12));
                    dirty.Mint(new HeroId(22), entityId: 5, level: 2, xp: Fixed.FromInt(6));
                    dirty.Destroy(0); // free-list + _freeCount off their fresh values
                },
            };
        }

        private static StoreResetFixture ItemsCase()
        {
            var dirty = new ItemStore();
            return new StoreResetFixture("Items.Clear()", new ItemStore(), dirty, dirty.Clear)
            {
                DirtyNonArrayState = () =>
                {
                    dirty.Create(defId: 1, charges: 3, new FixedVec3(Fixed.FromInt(2), Fixed.Zero, Fixed.FromInt(3)));
                    dirty.Create(defId: 2, charges: 1, new FixedVec3(Fixed.FromInt(4), Fixed.Zero, Fixed.FromInt(5)));
                    dirty.Destroy(0);
                },
            };
        }

        private static StoreResetFixture ModifiersCase()
        {
            // Shared deps: the store's wired references must be the SAME instances on both sides so the
            // (allowlisted) dependency fields stay reference-equal and the sweep compares only owned state.
            var world  = new EntityWorld();
            var system = new ModifierSystem();
            var events = new CombatEventQueue();
            var stats  = new MatchStats();
            var fresh  = new ModifierStore(world, system, null, events, stats);
            var dirty  = new ModifierStore(world, system, null, events, stats);

            // Descriptor instances for the two reference-typed slot arrays (no parameterless ctors).
            var mod = new Modifier(1, durationTicks: 100, StackRule.Refresh, maxStacks: 1,
                                   Fixed.Zero, Fixed.FromInt(5), Fixed.Zero, StatusFlags.None, null, 0);
            var pe  = new PersistentEffect(null, null, null, periodTicks: 0, periodCount: 0);

            return new StoreResetFixture("Modifiers.Clear()", fresh, dirty, dirty.Clear)
            {
                // DW-83: `_refusedInstalls` (the ring-full refusal tally) is non-array per-match diagnostic state
                // with no public write path, so the synthetic array fill cannot reach it — poke it, per the sweep's
                // own instruction. Clear() must zero it: a re-Play that inherits the prior match's refusal count
                // would mis-throttle (and mis-report) the next match's diagnostics.
                // DW-662: `_skippedPulses` (the dead-host/dead-target pulse-refusal tally) is the same shape and
                // carries the same per-match contract — a re-Play must start it clean or the "shipped paths never
                // trip the guard" reading is inherited from the previous match instead of measured.
                DirtyNonArrayState = () =>
                {
                    ClearCompletenessSweep.Poke(dirty, "_refusedInstalls", 3);
                    ClearCompletenessSweep.Poke(dirty, "_skippedPulses", 5);
                },
                Allowlist = new[]
                {
                    // Host-lifetime wiring (readonly ctor deps, shared between fresh and dirty; Clear() preserves
                    // them by omission — clearing them would orphan the system↔store cycle the host wired once).
                    // `_log` (DW-83) is the same shape: an injected AR-4 diagnostic sink, construction-lifetime.
                    "_world", "_system", "_damageTable", "_events", "_stats", "_log",
                    // The store's OWN dedicated EffectExecutor: a construction-owned helper whose only mutable
                    // state (pre-allocated work stack + LastPeakStackDepth diagnostic) self-refreshes per Run and
                    // is unfolded — per-instance identity, preserved across reset by design (DW-20 investigation).
                    "_executor",
                },
                ElementFiller = (t, _) => t == typeof(Modifier) ? mod
                                        : t == typeof(PersistentEffect) ? (object)pe
                                        : null,
            };
        }

        private static StoreResetFixture ResearchCase()
        {
            var fresh = new ResearchStore();
            var dirty = new ResearchStore();
            return new StoreResetFixture("Research.Clear()", fresh, dirty, dirty.Clear)
            {
                // Grow one faction's inner arrays so the jagged in-place fill has elements to dirty.
                DirtyNonArrayState = () => dirty.EnsureCapacity(Faction.Player1, 2),
                // Clear() deliberately never SHRINKS the grown inner arrays (see its doc) — grow the fresh side to
                // the same capacity so the final compare isolates "did every value reset", not an incidental
                // length mismatch (the exact idiom SimResetTests' keystone uses for this store).
                NormalizeFresh = () => fresh.EnsureCapacity(Faction.Player1, 2),
            };
        }

        private static StoreResetFixture VarsCase()
        {
            var dirty = new DslVarTable();
            return new StoreResetFixture("Vars.Clear()", new DslVarTable(), dirty, dirty.Clear)
            {
                DirtyNonArrayState = () =>
                {
                    // One declaration of every scope + an array + a timer, so every scalar-store list, every
                    // per-scope dense array, the array store, and the timer lists all leave their fresh state.
                    dirty.InitFromDeclarations(
                        new[]
                        {
                            new DslVarDecl("g", DslValueType.Int, VarScope.Global,       raw0: 5),
                            new DslVarDecl("p", DslValueType.Int, VarScope.PerPlayer,    raw0: 3),
                            new DslVarDecl("t", DslValueType.Int, VarScope.TriggerLocal, raw0: 2),
                            new DslVarDecl("a", DslValueType.Array, VarScope.Global, raw0: 0, raw1: 0,
                                           elementType: DslValueType.Int, capacity: 4),
                        },
                        new[] { new DslTimerDecl("tm", 30) });
                    dirty.Enter(); // _inTrigger true (the trigger-local scope flag Clear() must drop)
                },
            };
        }

        private static StoreResetFixture LoopStateCase()
        {
            var dirty = new DslLoopState();
            return new StoreResetFixture("LoopState.Clear()", new DslLoopState(), dirty, dirty.Clear)
            {
                DirtyNonArrayState = () =>
                {
                    dirty.ConfigureRows(new[] { 2 }); // one row → all row arrays allocated (fill dirties them)
                    dirty.BeginSnapshot(0);
                    dirty.SnapshotAppend(0, 5);
                    dirty.Charge(9); // FuelConsumed off 0
                },
            };
        }

        private static StoreResetFixture ReadbackCase()
        {
            var dirty = new DslVarReadback();
            return new StoreResetFixture("Readback.Clear()", new DslVarReadback(), dirty, dirty.Clear)
            {
                DirtyNonArrayState = () => dirty.InitFromDeclarations(new[]
                {
                    new DslVarDecl("g", DslValueType.Int, VarScope.Global,    raw0: 5),
                    new DslVarDecl("p", DslValueType.Int, VarScope.PerPlayer, raw0: 3),
                    new DslVarDecl("a", DslValueType.Array, VarScope.Global, raw0: 0, raw1: 0,
                                   elementType: DslValueType.Int, capacity: 4),
                }),
            };
        }

        private static StoreResetFixture DslEventsCase()
        {
            var dirty = new DslEventQueue();
            return new StoreResetFixture("DslEvents.Clear()", new DslEventQueue(), dirty, dirty.Clear)
            {
                DirtyNonArrayState = () =>
                {
                    dirty.Enqueue(3, 1, new[] { 9, 9 }, 2);
                    dirty.Enqueue(4, -1, null!, 0);
                },
                Allowlist = new[]
                {
                    // COUNT-GATED by design (Clear() doc: "Count-driven reads make a per-slot wipe unnecessary;
                    // slots past Count are never read or folded" — FoldInto iterates strictly below _count and
                    // every accessor throws at/past it). The stale tail past a zeroed _count is unreachable.
                    "_eventIndex", "_raiser", "_params",
                },
            };
        }

        private static StoreResetFixture WinStateCase()
        {
            var dirty = new WinStateStore();
            return new StoreResetFixture("WinState.Clear()", new WinStateStore(), dirty, dirty.Clear)
            {
                DirtyNonArrayState = () => dirty.MatchTicks = 4242, // the one non-array field
            };
        }

        private static StoreResetFixture AlliancesCase()
        {
            var dirty = new AllianceStore();
            // TeamId is the store's only field; the synthetic fill dirties it (7 != the FFA identity seed).
            return new StoreResetFixture("Alliances.Clear()", new AllianceStore(), dirty, dirty.Clear);
        }

        private static StoreResetFixture TriggerEnabledCase()
        {
            var dirty = new TriggerEnabledStore();
            return new StoreResetFixture("TriggerEnabled.Clear()", new TriggerEnabledStore(), dirty, dirty.Clear)
            {
                DirtyNonArrayState = () => dirty.Reset(3), // Count 3, buffer grown + seeded all-true
                Allowlist = new[]
                {
                    // Clear() zeroes ONLY Count; the _enabled buffer keeps its stale tail by design (the folded
                    // surface is Count-gated: a cleared store folds nothing, and the next LoadScenario's
                    // Reset(count) re-seeds every live entry). NOTE the tail IS still observable through the
                    // out-of-range-returns-true IsEnabled — that reachability question is DW-197 (open, medium).
                    // When DW-197 closes (bounding IsEnabled or wiping the tail), REMOVE this entry so the sweep
                    // pins the strengthened contract.
                    "_enabled",
                },
            };
        }

        private static StoreResetFixture TriggerFireLogCase()
        {
            var dirty = new TriggerFireLog();
            return new StoreResetFixture("TriggerFireLog.Clear()", new TriggerFireLog(), dirty, dirty.Clear)
            {
                DirtyNonArrayState = () =>
                {
                    dirty.Reset(2);      // grows _counts/_execToAuthored, bumps _generation
                    dirty.Record(0, 5);  // _counts[0]++, ring append, _totalRecorded++
                    dirty.Record(1, 6);
                },
                Allowlist = new[]
                {
                    // Count/length-gated observation buffers by design: Clear() zeroes _execCount/_ringHead/
                    // _ringLen/_totalRecorded and every read path (Count/Recent/AuthoredIndex) bounds itself by
                    // those — the stale buffer content past them is unreachable.
                    "_counts", "_execToAuthored", "_ring",
                    // Deliberately MONOTONIC across resets: Clear() BUMPS the generation (never zeroes it) so
                    // presentation detects a sim reset unambiguously (see the field doc). fresh==cleared can
                    // therefore never hold for it — the divergence IS the designed behavior.
                    "_generation",
                },
            };
        }

        private static StoreResetFixture DslSimEventsCase()
        {
            var dirty = new DslSimEventFeed();
            return new StoreResetFixture("DslSimEvents.Clear()", new DslSimEventFeed(), dirty, dirty.Clear)
            {
                DirtyNonArrayState = () => dirty.Push(DslSimEventFeed.KindUnitDamaged, 0, 9, 9, 9),
                Allowlist = new[]
                {
                    // COUNT-GATED by design (transient per-tick feed; the director drains [0,_count) and Clear()
                    // zeroes only the count — mirrors CombatEventQueue/DeathFeed).
                    "_kind", "_slot", "_p0", "_p1", "_p2",
                },
            };
        }

        private static StoreResetFixture WinConCase()
        {
            // Shared deps (allowlisted): the evaluator reads/writes these stores; ResetConfig touches only the
            // apply-time config fields.
            var winState  = new WinStateStore();
            var world     = new EntityWorld(); // DW-184: Configure packs the leader ref against the live world's generations
            var buildings = new BuildingStore();
            var factions  = new FactionRegistry(2);
            var alliances = new AllianceStore();
            var fresh = new WinConditionSystem(winState, world, buildings, factions, alliances);
            var dirty = new WinConditionSystem(winState, world, buildings, factions, alliances);

            return new StoreResetFixture("WinCon.ResetConfig()", fresh, dirty, dirty.ResetConfig)
            {
                // The config fields are written by Configure() at scenario-apply, but no single Configure call can
                // populate every preset's fields at once (each preset writes its own subset and Configure resets
                // first) — poke them all directly so the sweep pins that ResetConfig restores EVERY one.
                DirtyNonArrayState = () =>
                {
                    ClearCompletenessSweep.Poke(dirty, "_regions",
                        new RegionStore(new[] { "r1" },
                                        new[] { new FixedRect(Fixed.Zero, Fixed.Zero, Fixed.One, Fixed.One) }));
                    ClearCompletenessSweep.Poke(dirty, "_preset", WinPresetKind.KingOfTheHill);
                    ClearCompletenessSweep.Poke(dirty, "_builtin", WinCondition.EliminateAllUnits);
                    ClearCompletenessSweep.Poke(dirty, "_regionIndex", 3);
                    ClearCompletenessSweep.Poke(dirty, "_holdTicks", 42);
                    ClearCompletenessSweep.Poke(dirty, "_survivalFaction", Faction.Player2);
                    ClearCompletenessSweep.Poke(dirty, "_leaderRef", 5); // DW-184: generation-stamped PackRef (gen 0 ⇒ ref == id)
                    ClearCompletenessSweep.Poke(dirty, "_leaderFaction", Faction.Player1);
                    ClearCompletenessSweep.Poke(dirty, "_landmarkRef", 9);
                    ClearCompletenessSweep.Poke(dirty, "_landmarkFaction", Faction.Player2);
                },
                Allowlist = new[]
                {
                    // Host-lifetime wiring (readonly ctor deps, shared between fresh and dirty).
                    "_store", "_world", "_buildings", "_factions", "_alliances",
                    // Per-tick scratch, documented "cleared per use" — every scan clears it before reading, so
                    // stale content is unreachable; ResetConfig deliberately leaves it alone.
                    "_teamScratch", "_eliminatedThisTick",
                },
            };
        }

        private static StoreResetFixture CombatEventsCase()
        {
            var dirty = new CombatEventQueue();
            return new StoreResetFixture("CombatEvents.Clear()", new CombatEventQueue(), dirty, dirty.Clear)
            {
                DirtyNonArrayState = () => dirty.Push(CombatEventType.MeleeHit, FixedVec3.Zero),
                Allowlist = new[]
                {
                    // COUNT-GATED by design: the bridge drains [0,_count) each frame and Clear() zeroes only the
                    // count; the queue is never folded and slots past _count are never read.
                    "_buf",
                },
            };
        }

        private static StoreResetFixture DeathFeedCase()
        {
            var dirty = new DeathFeed();
            return new StoreResetFixture("_deathFeed.Clear()", new DeathFeed(), dirty, dirty.Clear)
            {
                DirtyNonArrayState = () => dirty.Push(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(5)),
                Allowlist = new[]
                {
                    // COUNT-GATED by design (the HeroXpSystem drains [0,_count) then clears; mirrors CombatEventQueue).
                    "_buf",
                },
            };
        }

        private static StoreResetFixture FogCase()
        {
            var alliances = new AllianceStore(); // shared dep
            var fresh = new FogOfWarSystem(Faction.Player1, alliances);
            var dirty = new FogOfWarSystem(Faction.Player1, alliances);
            return new StoreResetFixture("Fog.Reset()", fresh, dirty, dirty.Reset)
            {
                // Grid is the system's only per-match state; the synthetic fill dirties every cell.
                Allowlist = new[]
                {
                    // Per-client viewer identity (SetViewer) — presentation config the reset deliberately does NOT
                    // re-target (a reset must not flip whose vision the local fog shows).
                    "_faction",
                    // Host-lifetime wiring (readonly ctor dep, shared between fresh and dirty).
                    "_alliances",
                    // Per-client presentation setting (unfolded; toggling it can never desync) — preserved across
                    // the reset by design, like _faction.
                    ClearCompletenessSweep.BackingField("SharedTeamVision"),
                },
            };
        }

        private static StoreResetFixture MatchStatsCase()
        {
            var dirty = new MatchStats();
            // Six per-faction counter arrays and nothing else; the synthetic fill dirties them all.
            return new StoreResetFixture("MatchStats.Reset()", new MatchStats(), dirty, dirty.Reset);
        }

        private static StoreResetFixture AiCase()
        {
            // Shared deps (allowlisted). The BuildingStore stays EMPTY so AdoptPreplacedBuildings — run by both
            // the ctor and ResetForMatch — adopts nothing on either side (the fixture pins the reset mechanics,
            // not the adoption scan).
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            var buildSys  = new BuildingSystem(buildings, resources);
            var fresh = new AiOpponentSystem(buildings, resources, buildSys, AiDifficulty.Normal);
            var dirty = new AiOpponentSystem(buildings, resources, buildSys, AiDifficulty.Normal);

            return new StoreResetFixture("_ai.ResetForMatch()", fresh, dirty, dirty.ResetForMatch)
            {
                // The per-match decision state ResetForMatch owns (the same three fields Story 11.3's save
                // capture/restore names). No public dirty path exists outside a full scripted match, so the
                // fixture writes them directly.
                DirtyNonArrayState = () =>
                {
                    ClearCompletenessSweep.GetPrivate<List<int>>(dirty, "_productionBuildingIds").Add(7);
                    ClearCompletenessSweep.Poke(dirty, "_cmdCenterExpId", 5);
                    ClearCompletenessSweep.Poke(dirty, "_attackCooldown", Fixed.FromInt(3));
                },
                Allowlist = new[]
                {
                    // Host-lifetime wiring (readonly ctor deps, shared between fresh and dirty).
                    // DW-439/DW-445: _alliances joined them — the team mask is a host-lifetime dependency the AI
                    // READS and never owns (SimulationHost.ClearForReset resets the store itself via
                    // Alliances.Clear(), which AlliancesCase already sweeps), exactly like FogCase's _alliances.
                    "_buildings", "_resources", "_buildSys", "_alliances",
                    // Readonly difficulty-derived config — "Difficulty weights are readonly and intentionally
                    // preserved" (ResetForMatch doc); both instances derive identical values from the same level.
                    "_aggressionWeight", "_techWeight", "_attackThreshold", "_attackCooldownMax",
                },
            };
        }

        private static StoreResetFixture LoopCase()
        {
            // Shared world; zero systems (ResetTick's contract is the tick/checksum/accumulator counters only).
            var world = new EntityWorld();
            var fresh = new SimulationLoop(world);
            var dirty = new SimulationLoop(world);

            return new StoreResetFixture("_loop.ResetTick()", fresh, dirty, dirty.ResetTick)
            {
                DirtyNonArrayState = () =>
                {
                    dirty.StepOnce();     // CurrentTick 0 → 1
                    dirty.Update(0.02f);  // _accumulator + InterpolationAlpha off 0 (below one tick, no step)
                    // LastChecksum is only written on a checksum-interval boundary against wired stores; poke it
                    // so the sweep pins that ResetTick zeroes it.
                    ClearCompletenessSweep.Poke(dirty, "LastChecksum", 123u);
                },
                Allowlist = new[]
                {
                    // Host-lifetime wiring: the world/systems the loop wraps, the single checksum sink, and the
                    // EnableChecksums store refs — ResetTick's doc pins "WITHOUT disturbing the EnableChecksums
                    // store wiring (those refs are host-lifetime)".
                    ClearCompletenessSweep.BackingField("World"), "_systems", "OnChecksum",
                    "_checksumBuildings", "_checksumResources", "_checksumFactions", "_checksumModifiers",
                    "_checksumHeroes", "_checksumItems", "_checksumNodes", "_checksumResearch", "_checksumVars",
                    "_checksumLoopState", "_checksumDslEvents", "_checksumWinState", "_checksumAlliances",
                    "_checksumTriggerEnabled",
                    // Caller config (SimulationHost exposes it as a pass-through property); a reset preserves it —
                    // every reset test relies on ChecksumInterval=1 surviving ClearForReset.
                    ClearCompletenessSweep.BackingField("ChecksumInterval"),
                },
            };
        }
    }
}
