#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.AI;
using ProjectChimera.Combat;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Dsl;
using ProjectChimera.Economy;
using ProjectChimera.Effects;
using ProjectChimera.Navigation;

namespace ProjectChimera.Core.Sim
{
    /// <summary>
    /// Net-new, Godot-free composition root for the simulation (Story 1.8a / AR-6). It owns the SoA stores,
    /// the canonical 16-system tick order (Story 7.11 inserted <c>WinConditionSystem</c> at index 14, after
    /// <c>AiOpponentSystem</c> and immediately before <c>ScenarioDirector</c>; Story 3.13 inserted
    /// <c>HeroXpSystem</c> at index 9, after
    /// <c>ProjectileSystem</c>; Story 3.15 inserted <c>ItemSystem</c> at index 10; Story 4.9 inserted
    /// <c>ResearchSystem</c> at index 1, immediately after <c>BuildingSystem</c>, shifting everything after it down
    /// by one) — with <c>OrderQueueSystem</c> at index 4 [Story 2.12 / FR-74], then
    /// <c>AbilityCastSystem</c> at index 5 and <c>ModifierSystem</c> at index 6 — both immediately before
    /// <see cref="CombatSystem"/>; the ability-cast spine landed in Story 2.4a / FR-11, the AR-9 effective-stat
    /// recompute in Story 2.2a), the <see cref="SimulationLoop"/> it wraps, and the single checksum sink. Because it has zero Godot dependency it compiles into the Godot-free Tier-1 test
    /// project and (Story 1.9a) the headless ServerBootstrap reuses it verbatim.
    ///
    /// This is a behavior-preserving extraction: the construction performed here is byte-for-byte equivalent
    /// to the former inline construction in MainScene, pinned by the byte-identical golden-checksum suite.
    /// The host <em>composes</em> the existing <see cref="SimulationLoop"/> — that file is NOT modified.
    /// </summary>
    public sealed class SimulationHost
    {
        private readonly SimulationLoop _loop;
        private readonly ILogSink _log;
        // Held so SystemOrderTest can assert the order WITHOUT reaching into the untouched SimulationLoop.
        private readonly ISimSystem[] _systems;

        // Story 3.10 — the AI opponent holds per-match decision state (production-building ids, expansion commit,
        // attack cooldown) that is NOT in any store, so ClearForReset must reset it too or the next Play diverges.
        private readonly AiOpponentSystem _ai;

        // Story 3.13 — the host-owned transient death feed. Combat + projectile impacts push victim deaths here; the
        // HeroXpSystem (index 8) drains + clears it each tick. Per-tick transient (empty at checksum time → NOT folded);
        // ClearForReset empties it (like CombatEvents).
        private readonly DeathFeed _deathFeed;

        // Story 3.14 — the resolved revival rule (float→Fixed at the load boundary). Owned here and shared BY REFERENCE
        // with BuildingSystem (revive-order cost) + HeroXpSystem (countdown/respawn); the scenario-apply path reconfigures
        // it in place from the applied ScenarioData.RevivalRule (or Default when omitted).
        private readonly RevivalRuleRuntime _revivalRuntime;

        // Story 3.14 — the respawn spawn hook. Defaults to a host closure that reuses the SINGLE unit-spawn path
        // (World.Create + World.ApplyUnitDefinition mapper); production (the bootstrap) overrides it with the applier's
        // ScenarioApplier.SpawnUnit so a revived hero also gets its MeshType/worker wiring. Determinism-identical either
        // way (both go through the one ApplyUnitDefinition mapper).
        private System.Func<Definitions.UnitDefinition, Faction, Fixed, Fixed, int>? _reviveSpawnOverride;

        // ── Stores / field-held systems, exposed so callers read host truth (no parallel copies). ──
        public EntityWorld World { get; }
        public ResourceNodeStore Nodes { get; }
        public ResourceStore Resources { get; }
        public BuildingStore Buildings { get; }
        public ProjectileStore Projectiles { get; }
        /// <summary>
        /// The Story 3.2 (AR-12) persistent hero substrate — a sparse SoA keyed by a stable cross-match
        /// <see cref="ProjectChimera.Core.HeroId"/>. DORMANT in 3.2: no system mutates it mid-match (XP is Story 3.13,
        /// load is Story 3.9), so it is NOT folded into the per-tick <see cref="SimChecksum"/> (D-1) and adding it does
        /// not move any golden. It IS hashed at match start by <see cref="Definitions.StartStateHash"/>. Exposed like
        /// <see cref="Buildings"/> so the 3.9 load path and the start-state hash read host truth (no parallel copies).
        /// </summary>
        public HeroStore Heroes { get; }
        /// <summary>Story 11.3 — the loaded ability registry (id→index over validated <c>AbilityDefinition</c>s), or
        /// <see cref="AbilityRegistry.Empty"/>. Exposed so the SP save/load path can build the
        /// <c>CanonicalEffectDescriptorTable</c> that round-trips <see cref="ModifierStore"/> descriptor slots by
        /// index. Read-only; identical on save and load (guarded by the save header's ContentHash).</summary>
        public AbilityRegistry AbilityRegistry { get; }
        /// <summary>Story 3.15 — the item-instance store (ground + held). Folded into the per-tick <see cref="SimChecksum"/>
        /// (v12) alongside the per-hero inventory; populated by scenario placement + pickups.</summary>
        public ItemStore Items { get; }
        /// <summary>Story 3.15 — the item / inventory tick system (pickup proximity claim, consumable use, drop, death-drop).
        /// Exposed so apply sites can route <c>OrderApplier.Apply(..., items: ItemSys)</c> and the applier can configure
        /// its usable-slot count.</summary>
        public ItemSystem ItemSys { get; }
        /// <summary>Story 3.15 — the loaded item registry (id→index over validated <c>ItemDefinition</c>s). The host takes
        /// a pre-built registry (or <see cref="Definitions.ItemRegistry.Empty"/>); scenario placement resolves item_ids through it.</summary>
        public Definitions.ItemRegistry ItemRegistry { get; }
        /// <summary>Story 3.14 — the resolved revival rule shared with BuildingSystem + HeroXpSystem. The scenario-apply
        /// path calls <see cref="RevivalRuleRuntime.Configure"/> on it from the applied <c>ScenarioData.RevivalRule</c>.</summary>
        public RevivalRuleRuntime RevivalRuntime => _revivalRuntime;
        /// <summary>The Story 2.2b AR-9 modifier store (driven by the index-3 ModifierSystem; folded into the checksum). Exposed like <see cref="Projectiles"/> for the 2.4 ability-cast path.</summary>
        public ModifierStore Modifiers { get; }
        public CombatEventQueue CombatEvents { get; }
        public MatchStats MatchStats { get; }
        public BuildingSystem BuildSys { get; }
        /// <summary>Story 4.9 — the faction-scoped research order path (start/cancel/tick/complete + future-spawn
        /// catch-up). Exposed like <see cref="BuildSys"/> so apply sites can route
        /// <c>OrderApplier.Apply(..., research: ResearchSys)</c>.</summary>
        public ResearchSystem ResearchSys { get; }
        /// <summary>Story 4.9 — the mid-match-mutable per-faction research substrate <see cref="ResearchSys"/> reads/
        /// writes. Folded into <see cref="SimChecksum"/> (v14, Story 4.10).</summary>
        public ResearchStore Research { get; }
        public ScenarioDirector ScenarioDirector { get; }
        /// <summary>Story 7.3 — the top-level typed/scoped DSL variable + timer store. Initialized from
        /// <c>ScenarioData</c> declarations at scenario-apply (via <c>ScenarioDirector.LoadScenario</c>) and folded
        /// into the per-tick <see cref="SimChecksum"/> (v16).</summary>
        public DslVarTable Vars { get; }
        /// <summary>Story 7.6 — the checksummed loop-layer runtime state (per-tick DSL fuel + for_each_batched
        /// continuation rows). Driven by <see cref="ScenarioDirector"/>; folded into <see cref="SimChecksum"/> (v17).</summary>
        public DslLoopState LoopState { get; }
        /// <summary>Story 7.8 — the presentation READ RAIL: a version-stamped, double-buffered COPY of already-
        /// checksummed <see cref="Vars"/> state, published once per tick at the tick boundary by
        /// <see cref="ScenarioDirector"/>. Exposed for the presentation <c>CustomUiBridge</c> to pull. Explicitly
        /// NOT folded into <see cref="SimChecksum"/> (AR-32 — a UI mismatch cannot desync).</summary>
        public DslVarReadback Readback { get; }
        /// <summary>Story 7.5 — the cross-tick custom-event queue (pending <c>next_tick</c> raises). Owned here,
        /// shared with <see cref="ScenarioDirector"/> (the only enqueue/dequeue site) and folded into the per-tick
        /// <see cref="SimChecksum"/> (v18). Cleared on <see cref="ClearForReset"/>; <c>LoadScenario</c> resets it too.</summary>
        public DslEventQueue DslEvents { get; }
        /// <summary>Story 7.11 — the per-match win-condition runtime state (per-faction KotH hold counter / survival
        /// deadline / verdict latch + scalar match-tick grace counter). Driven by <see cref="WinCon"/>; folded into
        /// the per-tick <see cref="SimChecksum"/> (v19). Cleared on <see cref="ClearForReset"/>; re-seeded by the
        /// applier's <c>WinConditionSystem.Configure</c>.</summary>
        public WinStateStore WinState { get; }
        /// <summary>Story 7.12 — the sim-owned per-faction team-id alliance mask (default FFA / teams-of-1). Read by
        /// <see cref="WinCon"/> for team-aware N-faction resolution; folded into the per-tick <see cref="SimChecksum"/>
        /// (v20). Cleared (restored to FFA) on <see cref="ClearForReset"/>. Populated from the lobby by Story 9.15;
        /// this story owns the model + the FFA default only.</summary>
        public AllianceStore Alliances { get; }
        /// <summary>Story 7.13 — the per-exec trigger-enabled runtime mask (enable_trigger/disable_trigger flip a
        /// target trigger's enabled flag mid-match). STABLE reference constructed once here and shared BY REFERENCE
        /// with <see cref="ScenarioDirector"/> (the writer) and the checksum wiring — NEVER reallocated per
        /// LoadScenario. Folded into the per-tick <see cref="SimChecksum"/> (v21); cleared on <see cref="ClearForReset"/>.</summary>
        public TriggerEnabledStore TriggerEnabled { get; }
        /// <summary>Story 7.15 — the presentation-only trigger-debugging OBSERVATION BUFFER: per-exec fire counters
        /// + a fixed-capacity tick-stamped ring of recent fires. STABLE reference constructed once here and shared
        /// BY REFERENCE with <see cref="ScenarioDirector"/> (the only writer, at the <c>FireTrigger</c> choke point).
        /// Written UNCONDITIONALLY on every fire, but NEVER folded into <see cref="SimChecksum"/> and NEVER wired into
        /// <c>EnableChecksums</c> — the exact non-folded posture as <see cref="Readback"/> (a run with it attached
        /// vs not, or the overlay open vs closed, is byte-identical). Reset by <see cref="ScenarioDirector"/> at
        /// <c>LoadScenario</c>; cleared on <see cref="ClearForReset"/>.</summary>
        public TriggerFireLog TriggerFireLog { get; }
        /// <summary>Story 7.13 — the host-owned TRANSIENT sim-event feed (unit_damaged/unit_trained/ability_cast/
        /// hero_level). The producing systems push at their tick-boundary sites; <see cref="ScenarioDirector"/> drains
        /// it each tick into its base-event buffer and clears it. NOT folded (empty at the checksum boundary, the
        /// DeathFeed posture). Cleared on <see cref="ClearForReset"/>.</summary>
        public DslSimEventFeed DslSimEvents { get; }
        /// <summary>Story 7.11 — the sim-layer win-condition evaluator. Registered AFTER <c>AiOpponentSystem</c> and
        /// immediately BEFORE <see cref="ScenarioDirector"/>. Configured at scenario-apply from the applied
        /// <c>ScenarioData</c> (built-in enum or a T1 preset). Presentation polls <see cref="WinState"/> for the
        /// verdict; the director's <c>OnVictory</c> escape hatch is unchanged.</summary>
        public WinConditionSystem WinCon { get; }
        public FogOfWarSystem Fog { get; }

        /// <summary>Story 7.9 — the sim-side raiser-authorization DSL sink (a narrow handle to
        /// <see cref="ScenarioDirector"/>.<c>TryEnqueueExternalDslEvent</c>): (eventIndex, raiserSlot, arg0, arg1) →
        /// applied bool. <c>LockstepManager</c>/<c>ReplayPlayer</c> hold it so the single <c>OrderApplier.Apply</c>
        /// lands a button-originated DslEvent into the checksum-folded <see cref="DslEvents"/> queue identically on
        /// every peer and in replay. Reads only existing plan state — adds NO new folded sim state.</summary>
        public Func<int, int, int, int, bool> DslEventSink => ScenarioDirector.TryEnqueueExternalDslEvent;

        // ── Loop pass-throughs (SimulationLoop is untouched; the host wraps it). ──
        public uint CurrentTick => _loop.CurrentTick;
        public uint LastChecksum => _loop.LastChecksum;
        public float InterpolationAlpha => _loop.InterpolationAlpha;
        public int ChecksumInterval { get => _loop.ChecksumInterval; set => _loop.ChecksumInterval = value; }

        /// <summary>
        /// Construct a fully-wired sim. The injected <paramref name="log"/> is the host's ONLY logging path
        /// (never GD.Print/Console). <paramref name="checksumFactions"/> is supplied by the caller — the
        /// 2-faction callers pass <c>new FactionRegistry(2)</c>, the 4-faction golden passes its own — so the
        /// host never hard-codes the faction count. <paramref name="damageTable"/> defaults to null, which the
        /// combat ctors resolve to <c>DamageTable.Default</c>; this is exactly what keeps the goldens' 3-arg
        /// CombatSystem/ProjectileSystem construction byte-identical to the host's 4-arg-with-null call.
        /// </summary>
        public static SimulationHost Create(
            ILogSink log,
            FactionRegistry checksumFactions,
            FactionDefinition? factionDef1 = null,
            FactionDefinition? factionDef2 = null,
            DamageTable? damageTable = null,
            AiDifficulty aiLevel = AiDifficulty.Normal,
            AbilityRegistry? registry = null,
            ItemRegistry? itemRegistry = null)
            => new SimulationHost(log, checksumFactions, factionDef1, factionDef2, damageTable, aiLevel, registry, itemRegistry);

        private SimulationHost(ILogSink log, FactionRegistry checksumFactions,
            FactionDefinition? factionDef1, FactionDefinition? factionDef2,
            DamageTable? damageTable, AiDifficulty aiLevel, AbilityRegistry? registry, ItemRegistry? itemRegistry)
        {
            _log = log;

            // Stores — constructed exactly as the former inline block. EntityWorld is default-seeded
            // (DEFAULT_RNG_SEED inside its ctor); NO match-seed plumbing here — that is forward-looking and
            // would move the golden. (D3/D4)
            World            = new EntityWorld();
            Nodes            = new ResourceNodeStore();
            Resources        = new ResourceStore(Fixed.Zero);
            Buildings        = new BuildingStore();
            Projectiles      = new ProjectileStore();
            Heroes           = new HeroStore();   // Story 3.2 — the AR-12 hero substrate; folded into the per-tick checksum from Story 3.13 (XP runtime); populated by Story 3.9.
            Items            = new ItemStore();    // Story 3.15 — item instances (ground + held); folded into the per-tick checksum (v12).
            ItemRegistry     = itemRegistry ?? Definitions.ItemRegistry.Empty; // Story 3.15 — a null registry → Empty, so existing callers stay scenario-identical.
            AbilityRegistry  = registry ?? Definitions.AbilityRegistry.Empty;  // Story 11.3 — stored so save/load can build the canonical descriptor table.
            CombatEvents     = new CombatEventQueue();
            _deathFeed       = new DeathFeed();    // Story 3.13 — transient per-tick death buffer for the XP runtime
            _revivalRuntime  = new RevivalRuleRuntime(); // Story 3.14 — resolved from RevivalRule.Default until a scenario reconfigures it
            MatchStats       = new MatchStats();
            Alliances        = new AllianceStore();     // Story 7.12 — per-faction team-id mask (default FFA); folded into SimChecksum (v20). Story 9.14: built BEFORE Fog/Combat/Projectile so they can read the mask (shared vision + allied combat exclusion).
            Fog              = new FogOfWarSystem(Faction.Player1, Alliances); // Story 9.14 — shared-team vision unions allied sight (presentation-only, unfolded)
            BuildSys         = new BuildingSystem(Buildings, Resources, factionDef1, factionDef2, MatchStats, Heroes, _revivalRuntime);
            Research         = new ResearchStore(); // Story 4.9 — mid-match-mutable; folded into SimChecksum (v14, Story 4.10)
            Vars             = new DslVarTable();     // Story 7.3 — typed/scoped variables + timers; folded into SimChecksum (v16); init from ScenarioData at apply
            LoopState        = new DslLoopState();    // Story 7.6 — loop fuel + batched continuation rows; folded into SimChecksum (v17)
            Readback         = new DslVarReadback();  // Story 7.8 — presentation read rail (version-stamped copy of Vars); NOT folded into SimChecksum
            DslEvents        = new DslEventQueue();   // Story 7.5 — pending next-tick custom events; folded into SimChecksum (v18)
            WinState         = new WinStateStore();    // Story 7.11 — win-condition runtime state; folded into SimChecksum (v19)
            TriggerEnabled   = new TriggerEnabledStore(); // Story 7.13 — per-exec trigger-enabled mask; folded into SimChecksum (v21); STABLE reference
            TriggerFireLog   = new TriggerFireLog();       // Story 7.15 — trigger-debug observation buffer (fire counts + tick-stamped ring); STABLE reference; NEVER folded
            DslSimEvents     = new DslSimEventFeed();      // Story 7.13 — transient sim-event feed (unit_damaged/unit_trained/ability_cast/hero_level); NOT folded
            WinCon           = new WinConditionSystem(WinState, World, Buildings, checksumFactions, Alliances); // Story 7.11/7.12 — team-aware sim-layer win evaluator (DW-184: World for generation-stamped leader refs)
            ScenarioDirector = new ScenarioDirector(Buildings, Resources, Vars, LoopState, DslEvents, TriggerEnabled, DslSimEvents, TriggerFireLog, checksumFactions); // Story 9.2 — active-count drives the threshold poll span
            ScenarioDirector.SetReadback(Readback);   // Story 7.8 — the director publishes into it once per tick at the tick boundary

            // AR-9 effective-stat recompute (Story 2.2a), the Story 2.2b ModifierStore it drives, and the Story 2.4a
            // ability-cast system. Construct the systems + store FIRST — the store ctor takes modSys, and
            // AbilityCastSystem takes the store — then AttachStore closes the system↔store cycle, THEN build the
            // ordered array (so abilitySys can be slotted at index 3). The store needs the same damage table / event +
            // stats sinks combat uses (null → DamageTable.Default); it subscribes World.OnDestroy += ClearEntity in its
            // ctor (recycle safety). A null registry → the Empty registry, so existing callers stay scenario-identical.
            var modSys = new ModifierSystem();
            // DW-83: the live host wires its ILogSink into the store so a REFUSED (8-slot ring full) install — an
            // earned item / hero-growth / self-passive / research buff silently dropped — warns instead of vanishing.
            Modifiers = new ModifierStore(World, modSys, damageTable, CombatEvents, MatchStats, log);
            // DW-285: the same host ILogSink is threaded in as the cast system's diagnostic seam, so an unresolvable
            // cast / aura / self-passive WARNS instead of vanishing on every host that already owns a real sink
            // (MainScene's GodotLogSink, the dedicated server's) while the golden/Tier-1 NullLogSink stays silent.
            // Diagnostics only — the sink never mutates sim state, so the tick stays byte-identical either way.
            var abilitySys = new AbilityCastSystem(registry ?? AbilityRegistry.Empty, Resources, Modifiers,
                                                   damageTable, CombatEvents, MatchStats, _deathFeed, Alliances, // Story 9.14: team-aware ability targeting
                                                   _log);
            modSys.AttachStore(Modifiers);

            // Story 7.3 — wire the run_effect runtime into the ScenarioDirector now that the stores exist. An
            // EffectActionNode (run_effect) in a trigger executes its embedded D1 subgraph via the director's OWN
            // EffectExecutor (the shared class, not a second implementation) against these shared sinks.
            ScenarioDirector.SetEffectRuntime(damageTable, Modifiers, CombatEvents, MatchStats, _deathFeed);

            // Story 3.15 — the item / inventory tick system. Constructed AFTER Modifiers (it applies/removes carried stat
            // modifiers) and it subscribes World.OnDestroy for the death-drop AFTER ModifierStore.ClearEntity (so a hero's
            // stat modifiers are already reverted when its items drop — a harmless no-op removal). Uses the shared registry.
            ItemSys = new ItemSystem(World, Heroes, Items, Modifiers, ItemRegistry, CombatEvents, damageTable);

            // Story 4.9 — the research order path. Constructed AFTER Modifiers (completion applies a permanent
            // cumulative Modifier through the same store). Registered into _systems immediately after BuildSys below.
            // DW-83: the sink also reaches ResearchSystem, which reports the AGGREGATE (per-completion) count of
            // living units whose full ring dropped an earned, paid-for permanent research bonus.
            ResearchSys = new ResearchSystem(Buildings, Resources, Research, Modifiers, CombatEvents, factionDef1, factionDef2, log);

            // Story 2.6 — wire the WHILE-ALIVE self-passive installer to the spawn seam. EntityWorld fires
            // OnUnitDefinitionApplied once per def-based spawn (after the SoA is written); the cast system installs the
            // unit's self-passive (a Persistent HoT or a permanent stat modifier) through its executor + store. One
            // closure alloc at construction (never per-tick); symmetric with the OnDestroy → ClearEntity wire. A unit
            // with no self-passive (SelfPassiveAbilityIndex = -1) is a no-op, so existing scenarios stay identical.
            World.OnUnitDefinitionApplied += id => abilitySys.InstallSelfPassive(World, id);
            // Story 4.9 — future-spawn catch-up: every future spawn of a faction with a completed research (training,
            // scenario placement, hero respawn, editor restore/placement) also picks up its cumulative modifier(s).
            World.OnUnitDefinitionApplied += id => ResearchSys.ApplyCompletedResearch(World, id);

            // ── The canonical 16-system tick order (Story 7.11 inserted WinConditionSystem at index 14, after AI /
            //    before ScenarioDirector; Story 2.12 inserted OrderQueueSystem at index 3; Story 3.13
            //    inserted HeroXpSystem at index 9 [now 9, was 8]; Story 4.9 inserted ResearchSystem at index 1,
            //    immediately after BuildingSystem, shifting GatheringSystem and everything after down by one). The
            //    registration order IS the determinism contract; SystemOrderTest FAILS on any reorder/add/remove. ──
            _systems = new ISimSystem[]
            {
                BuildSys,                                                                 // [0] BuildingSystem    (Economy)
                // ── Story 4.9 research order path. At index 1, immediately AFTER BuildingSystem — an Economy-tier
                //    system that spends resources and times an order, structurally parallel to BuildingSystem's own
                //    production timer. ──
                ResearchSys,                                                              // [1] ResearchSystem    (Economy)
                // DW-207: World is passed so the system can subscribe World.OnDestroy and release a dying worker's
                // reserved gatherer slot (the leak the skip-dead main loop structurally cannot see).
                new GatheringSystem(Nodes, Resources, Buildings, MatchStats, World),       // [2] GatheringSystem   (Economy) — Buildings (4.7): requires_structure gate
                new MovementSystem(),                                                     // [3] MovementSystem    (Navigation)
                // ── Story 2.12 shift-queue advance. Immediately AFTER MovementSystem so a queued
                //    movement order's arrival is detected fresh THIS tick, and BEFORE AbilityCastSystem so a popped
                //    CastAbility order fires the same tick. Pops the head of each unit's completed order and dispatches
                //    it through the shared OrderApplier.ApplyActiveOrder (no second command→state path — FR-74/AC1). ──
                new OrderQueueSystem(),                                                    // [4] OrderQueueSystem  (Core, FR-74)
                // ── Story 2.4a ability-cast spine. Immediately BEFORE ModifierSystem, so a cast that
                //    installs a buff is recomputed by ModifierSystem and read by CombatSystem the
                //    SAME tick. Ticks per-slot cooldowns down, consumes the pending-cast intent, runs the effect graph. ──
                abilitySys,                                                               // [5] AbilityCastSystem  (Effects, FR-11)
                // ── AR-9 effective-stat recompute. Immediately before CombatSystem, so combat & projectile-spawn
                //    damage read freshly-recomputed Effective* stats the SAME tick a modifier changes them. Drives the
                //    ModifierStore (Story 2.2b) each tick (periods/expiry) then recomputes. ──
                modSys,                                                                   // [6] ModifierSystem    (Effects, AR-9)
                // Story 2.6: the on-hit rider needs the ability registry (index→graph) + the ModifierStore (apply leaf).
                // Story 3.13: the DeathFeed threads a lethal hitscan's victim to the XP runtime.
                new CombatSystem(Projectiles, CombatEvents, MatchStats, damageTable,
                                 registry ?? AbilityRegistry.Empty, Modifiers, Buildings, _deathFeed, Alliances), // [7] Buildings (2.9a): anti-building combat; DeathFeed (3.13); Alliances (9.14): allied acquisition/force-fire exclusion
                new ProjectileSystem(Projectiles, CombatEvents, MatchStats, damageTable,
                                     Buildings, _deathFeed, Alliances),                  // [8] Buildings (2.9a): ranged shells; DeathFeed (3.13); Alliances (9.14): allied splash exclusion
                // ── Story 3.13 hero XP runtime. Immediately AFTER ProjectileSystem so it drains the SAME
                //    tick's recorded deaths (combat + projectile impacts) → credits hostile heroes in range → advances
                //    level → reconciles growth via the folded ModifierStore. Clears the feed at end-of-tick. ──
                // Story 3.14: also drives hero death-detection, the revival countdown, and respawn (via the shared spawn
                // hook + the resolved revival rule + BuildingStore); announcements ride CombatEvents.
                new HeroXpSystem(Heroes, Modifiers, _deathFeed, Buildings, _revivalRuntime, ReviveSpawn, CombatEvents), // [9] HeroXpSystem (Combat, FR-7)
                // ── Story 3.15 item / inventory. AFTER the combat/projectile/hero-XP cluster: death-drops
                //    happen synchronously at KillEntity (via the OnDestroy hook, during the combat/projectile indices) and
                //    hero respawn happens in HeroXpSystem, so a revived hero is already empty when this resolves pickups.
                //    Runs after MovementSystem so it steers a pickup-bound hero from a current position. ──
                ItemSys,                                                                  // [10] ItemSystem       (Combat, FR-64)
                new SupplySystem(Resources),                                              // [11] SupplySystem      (Economy)
                Fog,                                                                      // [12] FogOfWarSystem    (Core)
                _ai = new AiOpponentSystem(Buildings, Resources, BuildSys, aiLevel),      // [13] AI opponent (plays Player2)
                // ── Story 7.11 win-condition evaluator. Immediately AFTER AiOpponentSystem (so it sees post-death
                //    alive counts) and immediately BEFORE ScenarioDirector (so the director's OnVictory escape hatch
                //    still runs last). Reads final entity/building state, writes the folded WinStateStore verdict. ──
                WinCon,                                                                   // [14] WinConditionSystem (Core, FR-win)
                ScenarioDirector,                                                         // [15] ScenarioDirector — runs LAST
            };

            // ── Story 7.13 — wire the transient sim-event feed to its four PRODUCERS (all tick before the director,
            //    index 15). Setters (not ctor params) keep the systems' construction signatures untouched (no test/
            //    golden churn). CombatSystem [7] / ProjectileSystem [8] / HeroXpSystem [9] are retrieved from the
            //    fixed-order array (SystemOrderTest pins the indices); the two field-held systems wire directly. ──
            BuildSys.SetDslSimEvents(DslSimEvents);
            BuildSys.SetCombatEvents(CombatEvents); // Story 11.4 (FR-74): production-completion cue rides the non-folded queue
            BuildSys.SetResourceNodes(Nodes);       // DW-207: QueueWorkerBuild releases the interrupted worker's gather slot
            abilitySys.SetDslSimEvents(DslSimEvents);
            ((CombatSystem)_systems[7]).SetDslSimEvents(DslSimEvents);
            ((ProjectileSystem)_systems[8]).SetDslSimEvents(DslSimEvents);
            ((HeroXpSystem)_systems[9]).SetDslSimEvents(DslSimEvents);

            _loop = new SimulationLoop(World, _systems);
            _loop.EnableChecksums(Buildings, Resources, checksumFactions, Modifiers, Heroes, Items, Nodes, Research, Vars, LoopState, DslEvents, WinState, Alliances, TriggerEnabled); // fold modifier state (v6) + ability cooldowns (v7) + mutable HeroStore (v11) + ItemStore/inventory (v12) + ResourceNodeStore (v13) + ResearchStore (v14) + DslVarTable (v16) + DslLoopState (v17) + DslEventQueue (v18) + WinStateStore (v19) + AllianceStore (v20) + TriggerEnabledStore (v21)

            // The sim spine's only host-side log in 1.8a: a one-shot construction diagnostic through the
            // injected seam. NullLogSink no-ops it (tests/server → zero effect on the golden); GodotLogSink
            // prints it for MainScene. NEVER a per-tick log (D6).
            _log.Info("[SimulationHost] Sim spine constructed (16 systems; ResearchSystem at index 1, OrderQueueSystem at index 4, AbilityCastSystem at index 5, ModifierSystem at index 6, HeroXpSystem at index 9, ItemSystem at index 10, WinConditionSystem at index 14).");
        }

        /// <summary>
        /// Story 3.10 (NFR-1 / UX-DR62): restore EVERY owned store + the wrapped loop to its exact post-construction
        /// state IN PLACE — the inverse of the constructor's store-build block above (<c>:88-151</c>), without
        /// reconstructing the host. Reconstructing would orphan the ~30 capture-once aliases every presentation bridge
        /// / UI system / <c>SceneContext</c> holds (they capture their store reference once at Initialize), so the
        /// reset MUST mutate the store objects in place. After this call every store is byte-for-byte equal to a
        /// freshly-constructed one and the loop is at tick 0 / checksum 0 — so a re-apply of the same authored
        /// <c>ScenarioData</c> reproduces a from-boot run byte-for-byte (D-2, the determinism keystone).
        ///
        /// <para>Note: the ability cooldowns the <c>AbilityCastSystem</c> manages live in
        /// <see cref="EntityWorld.AbilityCooldownTicks"/> (folded v7), so <see cref="EntityWorld.Clear"/> resets them;
        /// the cast system itself holds no per-match state. <see cref="ScenarioDirector"/> trigger/timer/variable state
        /// is NOT reset here — it is rebuilt by the re-apply's <c>ScenarioApplier.Apply → ScenarioDirector.LoadScenario</c>.</para>
        /// </summary>
        public void ClearForReset()
        {
            World.Clear();          // entity SoA + free-list + RNG re-seed (also zeroes AbilityCooldownTicks / StatusFlagsOf)
            Nodes.Clear();
            Resources.Clear();
            Buildings.Clear();
            Projectiles.Clear();
            Heroes.Clear();         // Story 3.9 gap: bulk-empty so the re-mint after clear is non-additive
            Items.Clear();          // Story 3.15 — folded ItemStore; bulk-empty so a re-apply re-places items non-additively
            Modifiers.Clear();      // folded — also zeroes the ModifierSystem accumulators it drives
            Research.Clear();       // Story 4.9 — mid-match-mutable; bulk-empty so a re-apply starts every faction idle again
            Vars.Clear();           // Story 7.3 — folded DslVarTable; bulk-empty so a re-apply re-inits declarations non-additively
            LoopState.Clear();      // Story 7.6 — folded DslLoopState; bulk-empty so a re-apply reconfigures rows non-additively
            Readback.Clear();       // Story 7.8 — presentation read rail (unfolded); bulk-empty so a re-apply re-inits declarations non-additively
            DslEvents.Clear();      // Story 7.5 — folded next-tick event queue; empty so a re-apply starts with no pending feedback
            WinState.Clear();       // Story 7.11 — folded win-condition state; empty so a re-apply re-seeds counters/verdict non-additively
            Alliances.Clear();      // Story 7.12 — folded team-id mask; restore FFA so a re-apply starts from the default (9.15 re-seeds teams)
            TriggerEnabled.Clear(); // Story 7.13 — folded trigger-enabled mask; empty so a re-apply's LoadScenario re-seeds it non-additively (Count 0 → folds nothing until then)
            TriggerFireLog.Clear(); // Story 7.15 — non-folded observation buffer; empty so a re-apply's LoadScenario re-seeds fire counts/ring non-additively
            DslSimEvents.Clear();   // Story 7.13 — transient sim-event feed (empty at reset)
            WinCon.ResetConfig();   // Review P10 — the win-condition APPLY-TIME config lives outside every store; a Clear
                                    // without a re-Configure must not leave a stale preset pointed at zeroed counters
                                    // (e.g. _preset=TimedSurvival + SurvivalRemaining=0 → instant false win next tick)
            CombatEvents.Clear();
            _deathFeed.Clear();     // Story 3.13 — transient per-tick death buffer (empty at reset)
            Fog.Reset();
            MatchStats.Reset();
            _ai.ResetForMatch();    // Story 3.10 — AI per-match decision state is not in any store; reset it too or the next Play desyncs
            _loop.ResetTick();      // CurrentTick + LastChecksum → 0 (checksum store wiring untouched)
        }

        /// <summary>
        /// Story 3.14 — the respawn hook HeroXpSystem calls when a revival countdown completes. Routes to the
        /// bootstrap-supplied override (<see cref="SetReviveSpawn"/> → <c>ScenarioApplier.SpawnUnit</c>, which also wires
        /// MeshType) when present, else a host-default closure that reuses the SINGLE spawn path (<see cref="EntityWorld.Create"/>
        /// + the <see cref="EntityWorld.ApplyUnitDefinition"/> mapper — never a duplicated mapper). Both are
        /// determinism-identical for the folded SoA; MeshType is presentation-only (unfolded). Returns the new entity id
        /// or -1 when the world is full.
        /// </summary>
        private int ReviveSpawn(Definitions.UnitDefinition def, Faction faction, Fixed x, Fixed z)
        {
            if (_reviveSpawnOverride != null) return _reviveSpawnOverride(def, faction, x, z);
            int id = World.Create(new FixedVec3(x, Fixed.Zero, z), faction,
                                  Fixed.FromFloat(def.Hp), Fixed.FromFloat(def.Speed));
            if (id < 0) return id;
            World.ApplyUnitDefinition(id, def); // the one shared mapper — never re-implemented
            return id;
        }

        /// <summary>Story 3.14 — override the revive respawn hook (the bootstrap wires it to
        /// <c>ScenarioApplier.SpawnUnit</c> so a revived hero also gets MeshType/worker wiring). Idempotent; safe to call
        /// once after the applier is built.</summary>
        public void SetReviveSpawn(System.Func<Definitions.UnitDefinition, Faction, Fixed, Fixed, int> fn)
            => _reviveSpawnOverride = fn;

        /// <summary>Story 11.3 — the per-match AI decision state (production-building refs, expansion latch, attack
        /// cooldown) that lives outside every store. Exposed so the SP save/load path can capture/restore it (the
        /// same state <see cref="ClearForReset"/> resets via <c>ResetForMatch</c>). Read-only handle.</summary>
        public AiOpponentSystem Ai => _ai;

        /// <summary>Story 11.3 — restore the wrapped loop's tick counter to a saved value on load (SP save/load).
        /// Delegates to <see cref="SimulationLoop.RestoreTick"/>; the checksum store wiring is untouched.</summary>
        public void RestoreTick(uint tick) => _loop.RestoreTick(tick);

        /// <summary>Advance exactly one tick (lockstep / replay / golden path). Wraps SimulationLoop.StepOnce.</summary>
        public void StepOnce() => _loop.StepOnce();

        /// <summary>Advance by a real-time delta (offline free-run path). Returns the number of ticks processed.</summary>
        public int Update(float realDelta) => _loop.Update(realDelta);

        /// <summary>
        /// The SINGLE checksum-sink owner (D5). Replaces the former scattered
        /// <c>SimulationLoop.OnChecksum</c> assignments: each caller now sets the sink exactly once here.
        /// </summary>
        public void SetChecksumSink(Action<uint, uint> sink) => _loop.OnChecksum = sink;

        /// <summary>
        /// The ordered systems, for <c>SystemOrderTest</c> only. Internal: the sim source is compiled INTO
        /// the Tier-1 test assembly (and the game assembly), so the test sees this without InternalsVisibleTo.
        /// </summary>
        internal IReadOnlyList<ISimSystem> Systems => _systems;
    }
}
