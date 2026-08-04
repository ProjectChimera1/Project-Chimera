#nullable enable
using System.Collections.Generic;
using ProjectChimera.AI;                 // AiDifficulty (AI expansion-latch reset guard)
using ProjectChimera.Combat;             // ProjectileStore (DW-20 direct post-clear projectile assertion)
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Dsl;                // TriggerGraph / DslLoopState (Story 7.6 batched-row reset guard)
using ProjectChimera.Effects;            // ModifierStore / ModifierSystem / Modifier (ClearAll coverage)
using ProjectChimera.Sim.Tests.Golden;   // GoldenApplierScenario + AiActiveScenario fixtures
using Xunit;

namespace ProjectChimera.Sim.Tests.Sim
{
    /// <summary>
    /// Story 3.10 (NFR-1 / UX-DR62 / UX-DR83) — Tier-1 coverage of the in-place Edit↔Play reset core: the Godot-free
    /// pieces the presentation <c>MainScene.ResetToAuthoredStart</c> composes (<see cref="SimulationHost.ClearForReset"/>,
    /// <see cref="ScenarioApplier"/>, <see cref="HeroProfileLoader"/>, <see cref="StartStateHash"/>). The determinism
    /// keystone (D-2): clear + re-apply reproduces a BYTE-IDENTICAL SimChecksum run and StartStateHash versus a fresh
    /// boot-and-apply — the guard that a store's <c>Clear()</c> is complete (a missed field diverges the checksum).
    ///
    /// All Godot-free: it composes the sim spine directly and never touches presentation. It asserts the hash-version
    /// stamps stay UNBUMPED (this story folds no new field into any hash).
    /// </summary>
    public class SimResetTests
    {
        // ── Fixtures ────────────────────────────────────────────────────────────────────────

        private static readonly Fixed SampleXp = Fixed.FromRaw(786432); // 12.0 in 16.16

        /// <summary>Build a fully-wired host + applier over the alpha_map_01-mirroring golden fixture, and APPLY it
        /// (validated). Returns both so a test can clear the host and re-apply the SAME model.</summary>
        private static (SimulationHost host, ScenarioApplier applier, ScenarioData model) BuildApplied()
        {
            FactionDefinition faction = GoldenApplierScenario.BuildFaction();
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = faction;
            slotDefs[(int)Faction.Player2] = faction;

            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            host.ChecksumInterval = 1; // checksum every tick → an exact located divergence
            var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);

            ScenarioData model = GoldenApplierScenario.BuildModel();
            ApplyValidated(applier, model);
            return (host, applier, model);
        }

        /// <summary>Validate + apply a model through the applier (mirrors the boot gate + the reset re-apply).</summary>
        private static void ApplyValidated(ScenarioApplier applier, ScenarioData model)
        {
            ValidationResult r = new ScenarioValidator().Validate(model);
            Assert.True(r.Ok, r.Error);
            applier.Apply(r.Value);
        }

        /// <summary>Step the host <paramref name="ticks"/> times, capturing the per-tick (tick, checksum) sequence.</summary>
        private static List<(uint tick, uint hash)> RunTicks(SimulationHost host, int ticks)
        {
            var seq = new List<(uint, uint)>(ticks);
            host.SetChecksumSink((t, h) => seq.Add((t, h)));
            for (int i = 0; i < ticks; i++) host.StepOnce();
            return seq;
        }

        // ── I/O matrix: reset reproduces a run (the determinism keystone, D-2) ─────────────────

        [Fact]
        public void ClearAndReapply_ReproducesByteIdenticalChecksumRun()
        {
            const int N = 150;

            var (host, applier, model) = BuildApplied();
            List<(uint, uint)> run1 = RunTicks(host, N);   // first run on the fresh host

            host.ClearForReset();
            ApplyValidated(applier, model);                 // re-apply the SAME authored model
            List<(uint, uint)> run2 = RunTicks(host, N);   // run after the in-place reset

            // A truly independent fresh boot-and-apply, for the "reset == fresh boot" guarantee.
            var (host0, _, _) = BuildApplied();
            List<(uint, uint)> run0 = RunTicks(host0, N);

            Assert.Equal(run0, run1); // sanity: two fresh boots agree (determinism)
            Assert.Equal(run1, run2); // KEYSTONE: clear + re-apply reproduces the run byte-for-byte
            Assert.Equal(run0, run2);
        }

        // ── DW-20: the same keystone, but over a FIGHTING scenario (projectiles + a live cast + two-sided combat) ──
        // The keystone above runs the economy golden fixture, in which nothing ever fights — so the per-match state
        // owned by ProjectileSystem / AbilityCastSystem / CombatSystem is entirely unpinned by it. None of those three
        // holds mutable per-match state today (their SpatialHash/EffectExecutor self-refresh each Tick, and
        // EffectExecutor.LastPeakStackDepth is diagnostic-only and unfolded), so this CANNOT fail against current
        // production code. It is a TRIPWIRE: the first future story that folds a per-match field into one of them
        // without adding it to ClearForReset diverges here, at a located tick.

        [Fact]
        public void ClearAndReapply_ReproducesByteIdenticalRun_OverAFightingScenario()
        {
            const int N = 40; // long enough to fire, short enough that nothing dies and shots are still in flight

            SimulationHost host = CombatResetScenario.Build();
            CombatResetScenario.CastAt(host);                                  // consumed in tick 1
            IReadOnlyList<GoldenChecksumReplay.Sample> run1 = RunSamples(host, N);

            // ── TEETH: the fight preconditions must actually have held, or the reproduction below is vacuous. ──
            Assert.Equal(N, run1.Count); // an unfired checksum sink yields an EMPTY sequence, which compares as "same"
            AssertFightPreconditionsHeld(host, "run1 (the pre-reset host)");

            host.ClearForReset();

            // ProjectileStore is NOT folded into SimChecksum (the checksum only sees projectiles indirectly, via the
            // damage they land on folded Health), so a leaked in-flight projectile would NOT show up in the sequence
            // comparison below. Assert the wipe DIRECTLY.
            Assert.Equal(0, host.Projectiles.HighWaterMark);
            // Iterate the ACTUAL slot array, not the MAX_PROJECTILES constant: were capacity ever decoupled from that
            // constant, a constant-bounded loop would silently under-scan and miss surviving slots past its end.
            for (int i = 0; i < host.Projectiles.Alive.Length; i++)
                Assert.False(host.Projectiles.Alive[i], $"Projectile slot {i} survived ClearForReset.");

            CombatResetScenario.Populate(host);                                // identical authored start
            CombatResetScenario.CastAt(host);
            IReadOnlyList<GoldenChecksumReplay.Sample> run2 = RunSamples(host, N);

            // An independent fresh boot, for the "reset == fresh boot" half of the guarantee.
            SimulationHost host0 = CombatResetScenario.Build();
            CombatResetScenario.CastAt(host0);
            IReadOnlyList<GoldenChecksumReplay.Sample> run0 = RunSamples(host0, N);

            Assert.Equal(N, run2.Count);
            Assert.Equal(N, run0.Count);

            // ── DW-198: the SAME fight preconditions must hold on BOTH remaining hosts. Asserting them against
            //    run1 alone leaves a hole: a fixture change that goes inert exclusively on the re-apply path
            //    (Populate on a just-cleared host) or the fresh-boot path would leave run2/run0 quietly empty
            //    fights whose checksum sequences still "agree" with each other — three sequences proving nothing
            //    about the reset. Each host must have genuinely FOUGHT for the comparisons below to have teeth. ──
            AssertFightPreconditionsHeld(host,  "run2 (the post-reset re-populated host)");
            AssertFightPreconditionsHeld(host0, "run0 (the independent fresh boot)");

            AssertSameSequence(run0, run1, "two fresh fighting boots disagree (the fixture is nondeterministic)");
            AssertSameSequence(run1, run2, "clear + re-apply did NOT reproduce the fighting run");
            AssertSameSequence(run0, run2, "the post-reset fighting run diverges from a fresh boot");
        }

        /// <summary>
        /// DW-198 — the DW-20 fight preconditions, extracted so they run against EVERY host the fighting-reset
        /// keystone compares (pre-reset run1, post-reset run2, fresh-boot run0), not the pre-reset host alone.
        /// Call AFTER the host has run its sampled window. Verifies the fixture actually fought on <paramref name="host"/>:
        /// a projectile in flight AND one landed, the cast committed (cooldown ticking + battle_fury installed),
        /// and BOTH attacker/target pairs exchanging damage. <paramref name="run"/> names the failing host.
        /// </summary>
        private static void AssertFightPreconditionsHeld(SimulationHost host, string run)
        {
            int inFlight = CombatResetScenario.LiveProjectiles(host);
            Assert.True(inFlight > 0,
                $"Precondition failed on {run}: no projectile is in flight at sampling end — the fixture went " +
                "hitscan (check Delivery = AttackDelivery.Projectile) or the shots already landed, so this test " +
                "proves nothing.");
            // …and one must actually have LANDED. ProjectileStore is unfolded, so projectiles reach the checksum ONLY
            // via the damage they deal. If every shot is still airborne when sampling stops, the projectile half of
            // this guard contributes nothing to the sequence comparison. The P2 targets take damage exclusively from
            // the P1 projectile attackers, so a dented target IS a folded impact. BOTH halves must hold.
            Assert.True(host.World.Health[CombatResetScenario.Target1] <
                        host.World.EffectiveMaxHealth[CombatResetScenario.Target1],
                $"Precondition failed on {run}: no projectile has LANDED on Target1 within the sampled window — " +
                "projectiles are not folded into SimChecksum directly, so with no impact the projectile path pins " +
                "nothing. Raise CombatResetScenario's ProjectileSpeed or lengthen N.");
            Assert.True(host.World.AbilityCooldownTicks[CombatResetScenario.CasterAbilitySlot0] > 0,
                $"Precondition failed on {run}: the caster's ability cooldown is not ticking — the cast never went through.");
            Assert.True(host.World.EffectiveAttackDamage[CombatResetScenario.Caster] >
                        host.World.BaseAttackDamage[CombatResetScenario.Caster],
                $"Precondition failed on {run}: battle_fury's modifier is not installed on the caster.");
            Assert.True(host.World.Health[CombatResetScenario.Attacker1] < host.World.EffectiveMaxHealth[CombatResetScenario.Attacker1],
                $"Precondition failed on {run}: no damage was landed — CombatSystem never engaged.");
            // The SECOND attacker/target pair too: the fixture creates four combatants, and asserting only pair 1
            // would let pair 2 sit inert (out of range, never acquiring) while this guard still passed — half the
            // fixture silently contributing nothing to the run it claims to reproduce.
            Assert.True(host.World.Health[CombatResetScenario.Target2] <
                        host.World.EffectiveMaxHealth[CombatResetScenario.Target2],
                $"Precondition failed on {run}: Target2 is undamaged — the second attacker/target pair never engaged, " +
                "so only half the fighting fixture is actually exercised.");
            Assert.True(host.World.Health[CombatResetScenario.Attacker2] <
                        host.World.EffectiveMaxHealth[CombatResetScenario.Attacker2],
                $"Precondition failed on {run}: Attacker2 is undamaged — Target2 never returned fire.");
        }

        // ── DW-193: ClearForReset + re-apply re-seeds the folded TriggerEnabledStore NON-ADDITIVELY ─────────────
        // TriggerEnabledStore.Clear() only zeroes Count — the _enabled buffer keeps its stale flags. Non-additivity
        // comes entirely from the re-apply's LoadScenario → Reset(count) all-true re-seed, which SetInitial then
        // overwrites with the authored per-trigger state. The disable must actually have taken effect BEFORE the
        // reset or the assertion has no teeth.

        [Fact]
        public void ClearForReset_ThenReapplyTriggerCarryingScenario_ReseedsEnabledMaskNonAdditively()
        {
            const int N = 10;
            const int aIdx = 0, bIdx = 1, cIdx = 2; // execs are ordered PRIORITY-DESCENDING: A(10), B(5), C(0)

            ScenarioData scenario = BuildEnableMaskScenario();

            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2));
            host.ChecksumInterval = 1;
            host.ScenarioDirector.LoadScenario(scenario);

            // Authored seed: A + B enabled, C authored Enabled = false.
            Assert.Equal(3, host.TriggerEnabled.Count);
            Assert.True(host.TriggerEnabled.IsEnabled(aIdx));
            Assert.True(host.TriggerEnabled.IsEnabled(bIdx));
            Assert.False(host.TriggerEnabled.IsEnabled(cIdx));

            IReadOnlyList<GoldenChecksumReplay.Sample> run1 = RunSamples(host, N);
            Assert.Equal(N, run1.Count); // an unfired checksum sink yields an EMPTY sequence, which compares as "same"

            // TEETH: A's disable_trigger must have flipped B to runtime-disabled before the reset, otherwise the
            // "B is enabled again after re-apply" assertion below would hold trivially.
            Assert.False(host.TriggerEnabled.IsEnabled(bIdx),
                "Precondition failed: trigger B was never runtime-disabled, so the re-seed assertion has no teeth.");

            host.ClearForReset();
            Assert.Equal(0, host.TriggerEnabled.Count); // a cleared store folds NOTHING until the next LoadScenario

            // A FRESHLY-BUILT scenario instance, exactly as Edit→Play supplies one — never the object already fed to
            // the first LoadScenario. Re-feeding the same instance would hide a LoadScenario that mutates its input
            // (e.g. writing runtime-disabled state back onto the authored ScenarioData), which is precisely the class
            // of additive leak this test exists to rule out.
            host.ScenarioDirector.LoadScenario(BuildEnableMaskScenario()); // the re-apply path

            Assert.Equal(3, host.TriggerEnabled.Count);
            Assert.True(host.TriggerEnabled.IsEnabled(aIdx),
                "Trigger A's authored-enabled state did not survive the reset + re-seed. A is the trigger that DRIVES " +
                "the disable, so a regression silently disabling it would leave B enabled for the wrong reason and " +
                "the B assertion below would still pass.");
            Assert.True(host.TriggerEnabled.IsEnabled(bIdx),
                "Reset.Reset()'s all-true re-seed did not restore B — the runtime disable leaked across the reset.");
            Assert.False(host.TriggerEnabled.IsEnabled(cIdx),
                "SetInitial's authored re-seed did not survive Reset()'s all-true seed — C is wrongly permissive.");

            IReadOnlyList<GoldenChecksumReplay.Sample> run2 = RunSamples(host, N);
            Assert.Equal(N, run2.Count);
            AssertSameSequence(run1, run2, "the re-seeded trigger-enabled mask did not reproduce the run");
        }

        /// <summary>Three match_start triggers, distinct priorities so the priority-descending exec order is
        /// unambiguous: A (10) <c>disable_trigger</c>s B (5); C (0) is authored <c>Enabled = false</c>.
        ///
        /// <para>B and C each carry a <c>set_variable</c> action onto a folded DSL variable, but NEITHER ever runs:
        /// A disables B before B evaluates, and C is authored disabled — so <c>bFired</c>/<c>cFired</c> stay 0 in every
        /// run and contribute nothing to the checksum. They are shape, not signal. The assertions in this test rest
        /// entirely on the direct <c>TriggerEnabled.IsEnabled</c> mask reads.</para></summary>
        private static ScenarioData BuildEnableMaskScenario()
        {
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "A", Priority = 10 });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "match_start" });
            g.Nodes.Add(new DisableTriggerNode { Id = 2, TargetTriggerId = 3 });
            g.Nodes.Add(new TriggerNode { Id = 3, Name = "B", Priority = 5 });
            g.Nodes.Add(new EventNode { Id = 4, Kind = "match_start" });
            g.Nodes.Add(new ActionNode { Id = 5, Kind = "set_variable", Variable = "bFired", Value = 1 });
            g.Nodes.Add(new TriggerNode { Id = 6, Name = "C", Priority = 0, Enabled = false });
            g.Nodes.Add(new EventNode { Id = 7, Kind = "match_start" });
            g.Nodes.Add(new ActionNode { Id = 8, Kind = "set_variable", Variable = "cFired", Value = 1 });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(4, TriggerGraph.EventExecOutPort, 3, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(3, TriggerGraph.TriggerExecOutPort, 5, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(7, TriggerGraph.EventExecOutPort, 6, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(6, TriggerGraph.TriggerExecOutPort, 8, TriggerGraph.ActionExecInPort));

            return new ScenarioData
            {
                Variables = new[]
                {
                    new ScenarioVariable { Name = "bFired", Type = DslValueType.Int, Scope = VarScope.Global },
                    new ScenarioVariable { Name = "cFired", Type = DslValueType.Int, Scope = VarScope.Global },
                },
                TriggerGraphJson = g.ToCanonicalJson(),
            };
        }

        /// <summary>Step the host <paramref name="ticks"/> times, capturing the per-tick checksum as
        /// <see cref="GoldenChecksumReplay.Sample"/>s so a divergence can be reported at its located tick.</summary>
        private static IReadOnlyList<GoldenChecksumReplay.Sample> RunSamples(SimulationHost host, int ticks)
        {
            var seq = new List<GoldenChecksumReplay.Sample>(ticks);
            host.SetChecksumSink((t, h) => seq.Add(new GoldenChecksumReplay.Sample(t, h)));
            for (int i = 0; i < ticks; i++) host.StepOnce();
            return seq;
        }

        /// <summary>Assert two checksum sequences are byte-identical, naming the FIRST divergent tick on failure.</summary>
        private static void AssertSameSequence(IReadOnlyList<GoldenChecksumReplay.Sample> expected,
                                               IReadOnlyList<GoldenChecksumReplay.Sample> actual, string what)
        {
            GoldenChecksumReplay.Divergence? d = GoldenChecksumReplay.CompareSequences(expected, actual);
            Assert.True(d is null, d is null ? "" : $"{what}: {GoldenChecksumReplay.DescribeDivergence(d.Value)}");
        }

        // ── I/O matrix: a trigger edited in Edit is live on the next Play (re-apply's LoadScenario), no reload ──

        [Fact]
        public void EditedTrigger_IsLiveAfterReset_ViaReapplyLoadScenario()
        {
            const int N = 20;
            Fixed bounty = Fixed.FromInt(777); // a distinctive one-shot ore grant

            // Control: apply the unmodified authored model and run — no bonus trigger.
            var (host, applier, model) = BuildApplied();
            for (int i = 0; i < N; i++) host.StepOnce();
            Fixed oreControl = host.Resources.Ore[(int)Faction.Player1];

            // Simulate an Edit-side trigger ADD on the live scenario model — a match_start trigger granting
            // Player1 a one-shot ore bounty (mirrors TriggerEditorPanel reassigning _scenario.Triggers in place).
            model.Triggers = new[]
            {
                new TriggerDefinition
                {
                    Name    = "edit-added-bounty",
                    RunOnce = true,
                    Events  = new[] { new TriggerEvent  { Type = "match_start",   Faction = 0 } },
                    Actions = new[] { new TriggerAction { Type = "add_resources", Faction = 0, Amount = bounty } },
                },
            };

            // In-place reset + re-apply the EDITED model — NO scene reload. The re-apply's
            // ScenarioDirector.LoadScenario rebuilds director state from the edited Triggers, so the new
            // trigger fires this Play.
            host.ClearForReset();
            ApplyValidated(applier, model);
            for (int i = 0; i < N; i++) host.StepOnce();
            Fixed oreEdited = host.Resources.Ore[(int)Faction.Player1];

            // Gathering over N ticks is identical (byte-identical authored start), so the delta is exactly the
            // edited trigger's one-shot bounty — proving the edit is live after the no-reload round-trip.
            Assert.Equal(bounty, oreEdited - oreControl);
        }

        // ── Determinism (AI path): ClearForReset resets AiOpponentSystem's per-match _cmdCenterExpId LATCH ──
        // Review finding (Edge Case Hunter): the AI holds per-match decision state outside every store. Most of it
        // self-heals on re-apply (PruneDeadBuildings re-derives _productionBuildingIds), but _cmdCenterExpId is a
        // PERSISTENT LATCH: once the AI expands, ScoreExpandSupply returns 0 forever (AiOpponentSystem.cs:244 checks
        // the raw id, NOT whether it still resolves). So without AiOpponentSystem.ResetForMatch, a playtest where the
        // AI expanded would leave it permanently unable to expand on the next Play — diverging from a fresh boot.
        // This scenario forces expansion on tick 1 (supply headroom 0 + 300 ore) so the latch is exercised.

        [Fact]
        public void ClearForReset_ResetsAiExpansionLatch_SoAiExpandsAgainAfterRoundTrip()
        {
            SimulationHost host = BuildAiExpansionHost();

            for (int i = 0; i < 30; i++) host.StepOnce();      // AI commits a supply expansion → _cmdCenterExpId latches
            Assert.Equal(2, P2CommandCenters(host));           // base CC + expansion CC

            host.ClearForReset();                              // must ResetForMatch → _cmdCenterExpId back to -1
            PopulateAiExpansion(host);                         // identical authored start
            for (int i = 0; i < 30; i++) host.StepOnce();

            // WITHOUT the latch reset the AI would never expand again (stays at 1 CC). The reset restores expansion.
            Assert.Equal(2, P2CommandCenters(host));
        }

        /// <summary>A minimal AI-active host under immediate supply pressure (headroom 0) with ore to expand — so the
        /// AiOpponentSystem commits a supply-expansion CommandCenter on tick 1, latching _cmdCenterExpId.</summary>
        private static SimulationHost BuildAiExpansionHost()
        {
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2),
                                             new FactionDefinition(), new FactionDefinition(),
                                             damageTable: null, aiLevel: AiDifficulty.Normal);
            host.ChecksumInterval = 1;
            PopulateAiExpansion(host);
            return host;
        }

        private static void PopulateAiExpansion(SimulationHost host)
        {
            EntityWorld world = host.World; BuildingStore buildings = host.Buildings; ResourceStore resources = host.Resources;

            int p2cc = buildings.Create(new FixedVec3(Fixed.FromInt(45), Fixed.Zero, Fixed.Zero), Faction.Player2, BuildingType.CommandCenter);
            buildings.ConstructionTimer[p2cc] = Fixed.Zero; // complete
            resources.FactionBase[(int)Faction.Player2] = new FixedVec3(Fixed.FromInt(45), Fixed.Zero, Fixed.Zero);

            // 5 combat units at 4 supply each = SupplyUsed 20 vs cap 20 (starting 10 + the base CC's +10) → headroom 0
            // → ScoreExpandSupply = 0.95, which beats ScoreBuildBarracks (0.85), so the AI expands on tick 1.
            for (int i = 0; i < 5; i++)
            {
                int u = world.Create(new FixedVec3(Fixed.FromInt(40), Fixed.Zero, Fixed.FromInt(i * 2 - 4)), Faction.Player2, Fixed.FromInt(80), Fixed.FromInt(3));
                world.SupplyCost[u] = 4;
            }
            resources.AddOre(Faction.Player2, Fixed.FromInt(300)); // > COST_CC (150)

            int p1cc = buildings.Create(new FixedVec3(Fixed.FromInt(-45), Fixed.Zero, Fixed.Zero), Faction.Player1, BuildingType.CommandCenter);
            buildings.ConstructionTimer[p1cc] = Fixed.Zero;
            resources.FactionBase[(int)Faction.Player1] = new FixedVec3(Fixed.FromInt(-45), Fixed.Zero, Fixed.Zero);

            host.ScenarioDirector.LoadScenario(new ScenarioData());
        }

        private static int P2CommandCenters(SimulationHost host)
        {
            int n = 0; BuildingStore b = host.Buildings;
            for (int i = 0; i < b.Count; i++)
                if (b.Alive[i] && b.FactionOf[i] == Faction.Player2 && b.Type[i] == BuildingType.CommandCenter) n++;
            return n;
        }

        // ── Determinism completeness (modifier path): ModifierStore.Clear zeroes the ModifierSystem accumulators ──
        // Review gap (Verification Gap): the reset fixtures carry no modifiers, so a no-op ClearAll would pass every
        // reproduce-run test. The accumulators are INCREMENTAL (ModifierSystem.Apply does += ; ModifierStore.cs), and
        // ClearForReset -> Modifiers.Clear() -> _system.ClearAll() zeroes them. Probe it directly: a fresh modifier
        // after Clear must recompute from base alone, not base + the prior (stale) accumulator.

        [Fact]
        public void ModifierStoreClear_ZeroesAccumulators_SoRecomputeDropsStaleBonus()
        {
            var world = new EntityWorld();
            var sys   = new ModifierSystem();
            var store = new ModifierStore(world, sys);

            int e = world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            Fixed baseAtk = world.BaseAttackDamage[e];

            // Persistent +50 attack modifier → accumulator non-zero, Effective recomputed eagerly.
            store.Apply(e, PersistentAtk(id: 1, atk: 50), e, Faction.Player1);
            Assert.Equal(baseAtk + Fixed.FromInt(50), world.EffectiveAttackDamage[e]);

            store.Clear();   // ClearForReset's ModifierStore.Clear → ClearAll: accumulators MUST return to zero

            // A fresh +10 modifier must recompute to base + 10 (not base + 50 + 10) — proving the stale +50 was cleared.
            store.Apply(e, PersistentAtk(id: 2, atk: 10), e, Faction.Player1);
            Assert.Equal(baseAtk + Fixed.FromInt(10), world.EffectiveAttackDamage[e]);
        }

        private static Modifier PersistentAtk(int id, int atk) =>
            new Modifier(id, durationTicks: 9999, StackRule.Refresh, maxStacks: 1,
                         Fixed.Zero, Fixed.FromInt(atk), Fixed.Zero, StatusFlags.None, null, 0);

        // ── I/O matrix: cleared store == freshly-constructed store ─────────────────────────────

        [Fact]
        public void ClearForReset_LeavesEveryStoreEqualToFreshlyConstructed()
        {
            var (host, applier, model) = BuildApplied();
            RunTicks(host, 60); // run a match: units move, ore spent, RNG advances

            // Story 3.15: the golden fixture places no items, so a missing Items.Clear() (or a dropped
            // HeroStore.Inventory reset) in ClearForReset would still leave every OTHER store equal to fresh and pass
            // vacuously. Populate the v12-folded item state directly so this keystone has teeth on it — a live
            // ItemStore instance + a non-default per-hero inventory ref that ClearForReset MUST wipe back to fresh.
            host.Items.Create(defId: 0, charges: 3, new FixedVec3(Fixed.FromInt(5), Fixed.Zero, Fixed.FromInt(7)));
            host.Heroes.Inventory[0] = 7; // non-default marker; HeroStore.Clear's Array.Clear(Inventory) must zero it

            // Story 4.9 (review finding [medium] 6): the golden fixture's faction authors no research, so a missing
            // Research.Clear() (or an incomplete one) in ClearForReset would still leave every OTHER store equal to
            // fresh and pass vacuously. Grow + populate host.Research DIRECTLY (bypassing ResearchSystem, which the
            // golden faction's empty Research list would deny) so this keystone has teeth on the 4.9 per-faction
            // research substrate too — every field ResearchStore owns, non-default.
            host.Research.EnsureCapacity(Faction.Player1, 2);
            host.Research.InProgressIndex[(int)Faction.Player1]   = 1;
            host.Research.RemainingTicks[(int)Faction.Player1]    = 5;
            host.Research.StartedAtPosition[(int)Faction.Player1] = new FixedVec3(Fixed.FromInt(3), Fixed.Zero, Fixed.FromInt(4));
            host.Research.CompletedLevels[(int)Faction.Player1][0]             = 2;
            host.Research.CumulativeMaxHealthDelta[(int)Faction.Player1][0]    = Fixed.FromInt(10);
            host.Research.CumulativeAttackDamageDelta[(int)Faction.Player1][0] = Fixed.FromInt(5);
            host.Research.CumulativeMoveSpeedDelta[(int)Faction.Player1][0]    = Fixed.FromInt(2);
            host.Research.CumulativeArmorDelta[(int)Faction.Player1][0]        = Fixed.FromInt(1);

            // Story 6.3 (review pass 1, VG2): the golden fixture injects no elevation grid and never enables the
            // height-vision toggle, so a dropped reset of the three new sim-globals (or the Elevation array) in
            // ClearForReset would leave every OTHER store equal to fresh and pass vacuously. Populate them directly so
            // this keystone has teeth: a stale toggle/bonus/grid + a dirtied Elevation slot that ClearForReset MUST
            // wipe back to fresh — otherwise a subsequent flat-map match reuses the prior map's height-vision config
            // (reset != fresh boot).
            host.World.HeightAdvantageVision    = true;
            host.World.HeightVisionBonusPerStep = Fixed.FromInt(9);
            host.World.SetElevationGrid(new ElevationGrid(new[] { Fixed.FromInt(3) }, 1, 1,
                Fixed.Zero, Fixed.Zero, Fixed.One));
            if (host.World.HighWaterMark > 0) host.World.Elevation[0] = Fixed.FromInt(7);

            // Story 7.11 (review P9): the golden fixture runs a built-in win condition and the 60-tick run above
            // stays un-resolved, so a dropped WinState.Clear() in ClearForReset would leave every OTHER store equal
            // to fresh and pass vacuously (only MatchTicks would differ). Dirty every v19-folded WinStateStore
            // field directly so this keystone has teeth on the win-state substrate too.
            host.WinState.MatchTicks = 4242;
            host.WinState.KothHoldTicks[(int)Faction.Player1]     = 7;
            host.WinState.SurvivalRemaining[(int)Faction.Player2] = 55;
            host.WinState.Verdict[(int)Faction.Player1]           = WinStateStore.VERDICT_WON;
            host.WinState.Verdict[(int)Faction.Player2]           = WinStateStore.VERDICT_LOST;

            // Story 7.12: dirty the v20-folded AllianceStore team mask (ally P2 onto P1's team) so a dropped
            // Alliances.Clear() in ClearForReset would leave a stale team assignment (reset != fresh boot); the
            // FFA-restore must wipe it back so the next match starts teams-of-1.
            host.Alliances.TeamId[(int)Faction.Player2] = (int)Faction.Player1;

            host.ClearForReset();

            // A newly-constructed host (NOT applied) is the byte-for-byte reference.
            FactionDefinition faction = GoldenApplierScenario.BuildFaction();
            var fresh = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);

            // ResearchStore.Clear() never SHRINKS the per-faction-per-research inner arrays (by design — see its
            // doc comment), it only zeroes them; the golden faction's Research list is empty so `fresh.Research`
            // was never grown. Growing `fresh.Research` to the SAME size here isolates the assertion below to "did
            // every value zero", not an incidental array-length mismatch from the direct EnsureCapacity above.
            fresh.Research.EnsureCapacity(Faction.Player1, 2);

            // Loop / management counters.
            Assert.Equal(0u, host.CurrentTick);
            Assert.Equal(0u, host.LastChecksum);
            Assert.Equal(0, host.World.HighWaterMark);
            Assert.Equal(0, host.World.AliveCount);
            Assert.Equal(EntityWorld.DEFAULT_RNG_SEED, host.World.Rng.State);
            Assert.Equal(0, host.Buildings.Count);
            Assert.Equal(0, host.Nodes.Count);
            Assert.Equal(0, host.Heroes.Count);
            Assert.Equal(0, host.Projectiles.HighWaterMark);

            // Entity SoA (incl. the sentinel-filled arrays) equal the fresh world's, element-for-element.
            Assert.Equal(fresh.World.Flags,           host.World.Flags);
            Assert.Equal(fresh.World.Position,        host.World.Position);
            Assert.Equal(fresh.World.Health,          host.World.Health);
            Assert.Equal(fresh.World.FactionOf,       host.World.FactionOf);
            Assert.Equal(fresh.World.StatusFlagsOf,   host.World.StatusFlagsOf);
            Assert.Equal(fresh.World.AttackTarget,    host.World.AttackTarget);    // sentinel −1
            Assert.Equal(fresh.World.CommandTarget,   host.World.CommandTarget);   // sentinel −1
            Assert.Equal(fresh.World.HeroIndex,       host.World.HeroIndex);       // sentinel HERO_NONE
            Assert.Equal(fresh.World.AbilityId,       host.World.AbilityId);       // sentinel −1
            Assert.Equal(fresh.World.PendingCastSlot, host.World.PendingCastSlot); // sentinel NO_PENDING_CAST
            Assert.Equal(fresh.World.AbilityCooldownTicks, host.World.AbilityCooldownTicks); // folded v7 — cast cooldowns

            // Resource / building / node / hero / fog SoA equal the fresh stores'.
            Assert.Equal(fresh.Resources.Ore,         host.Resources.Ore);
            Assert.Equal(fresh.Resources.Crystal,     host.Resources.Crystal);
            Assert.Equal(fresh.Resources.SupplyCap,   host.Resources.SupplyCap);   // ctor re-seeds P1/P2 = 10
            Assert.Equal(fresh.Resources.SupplyUsed,  host.Resources.SupplyUsed);
            Assert.Equal(fresh.Resources.FactionBase, host.Resources.FactionBase);
            Assert.Equal(fresh.Buildings.Alive,       host.Buildings.Alive);
            Assert.Equal(fresh.Buildings.Generation,  host.Buildings.Generation);
            Assert.Equal(fresh.Nodes.Active,          host.Nodes.Active);
            Assert.Equal(fresh.Heroes.Alive,          host.Heroes.Alive);
            Assert.Equal(fresh.Fog.Grid,              host.Fog.Grid);

            // Story 3.15: the v12-folded ItemStore + per-hero inventory reset to fresh (teeth for the Items.Clear()
            // and the HeroStore.Inventory reset in ClearForReset — see the direct population above; without either,
            // host would diverge from a fresh boot and the "reset == fresh boot" determinism guarantee would break).
            Assert.Equal(0, host.Items.Count);
            Assert.Equal(fresh.Items.Alive,           host.Items.Alive);
            Assert.Equal(fresh.Items.Generation,      host.Items.Generation);
            Assert.Equal(fresh.Heroes.Inventory,      host.Heroes.Inventory);

            // Story 4.9: the ResearchStore substrate populated above resets to fresh too — teeth for the
            // Research.Clear() call in ClearForReset (see the direct population above; without it, host would
            // diverge from a fresh boot and the "reset == fresh boot" determinism guarantee would break).
            Assert.Equal(fresh.Research.InProgressIndex,            host.Research.InProgressIndex);
            Assert.Equal(fresh.Research.RemainingTicks,             host.Research.RemainingTicks);
            Assert.Equal(fresh.Research.StartedAtPosition,          host.Research.StartedAtPosition);
            Assert.Equal(fresh.Research.CompletedLevels,            host.Research.CompletedLevels);
            Assert.Equal(fresh.Research.CumulativeMaxHealthDelta,   host.Research.CumulativeMaxHealthDelta);
            Assert.Equal(fresh.Research.CumulativeAttackDamageDelta,host.Research.CumulativeAttackDamageDelta);
            Assert.Equal(fresh.Research.CumulativeMoveSpeedDelta,   host.Research.CumulativeMoveSpeedDelta);
            Assert.Equal(fresh.Research.CumulativeArmorDelta,       host.Research.CumulativeArmorDelta);

            // Story 6.3 (VG2): the three height-vision sim-globals + the Elevation SoA reset to fresh (teeth for the
            // ClearForReset resets — see the direct population above; without them a reused world carries a prior map's
            // elevation config, breaking the "reset == fresh boot" determinism guarantee).
            Assert.Equal(fresh.World.HeightAdvantageVision,    host.World.HeightAdvantageVision);
            Assert.Equal(fresh.World.HeightVisionBonusPerStep, host.World.HeightVisionBonusPerStep);
            Assert.Equal(fresh.World.Elevation,                host.World.Elevation);

            // Story 7.11 (review P9): the v19-folded WinStateStore resets to fresh (teeth for the WinState.Clear()
            // call in ClearForReset — see the direct dirtying above; without it a re-Play would carry a latched
            // verdict / stale counters into the next match, breaking the "reset == fresh boot" guarantee).
            Assert.Equal(fresh.WinState.MatchTicks,        host.WinState.MatchTicks);
            Assert.Equal(fresh.WinState.KothHoldTicks,     host.WinState.KothHoldTicks);
            Assert.Equal(fresh.WinState.SurvivalRemaining, host.WinState.SurvivalRemaining);
            Assert.Equal(fresh.WinState.Verdict,           host.WinState.Verdict);
            Assert.False(host.WinState.IsResolved());
            // Story 7.12: the v20-folded AllianceStore team mask resets to fresh FFA (teeth for the Alliances.Clear()
            // call in ClearForReset — see the direct dirtying above).
            Assert.Equal(fresh.Alliances.TeamId, host.Alliances.TeamId);
            // The private _elevationGrid also reset: a fresh spawn on the cleared world samples the null grid ⇒
            // Fixed.Zero. A leaked grid (FromInt(3) above) would make this probe non-zero. (Done last — it mutates the world.)
            int probe = host.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(1), Fixed.FromInt(1));
            Assert.Equal(Fixed.Zero, host.World.Elevation[probe]);
        }

        // ── I/O matrix: HeroStore non-additive on re-deploy (the 3.9 deferred gap) ─────────────

        [Fact]
        public void HeroStore_NonAdditive_AcrossRepeatedRedeploy()
        {
            (SimulationHost host, ScenarioApplier applier, ScenarioData model) = BuildHeroApplied();
            PlayerProfile profile = BuildHeroProfile(level: 3, xp: SampleXp);

            ulong Deploy()
            {
                int minted = HeroProfileLoader.LoadInto(host.Heroes, applier.LastAppliedHeroes, profile);
                Assert.Equal(1, minted);                  // exactly one row minted
                Assert.Equal(1, LiveHeroCount(host.Heroes)); // never accumulating stale rows
                return StartStateHash.Compute(model, host.Heroes);
            }

            ulong h1 = Deploy();

            host.ClearForReset();
            ApplyValidated(applier, model);
            ulong h2 = Deploy();

            host.ClearForReset();
            ApplyValidated(applier, model);
            ulong h3 = Deploy();

            // Re-deploying is idempotent (no stale live rows) → the StartStateHash is identical across deploys.
            Assert.Equal(h1, h2);
            Assert.Equal(h2, h3);
        }

        [Fact]
        public void StartStateHash_AfterReset_EqualsFreshBootDeploy()
        {
            PlayerProfile profile = BuildHeroProfile(level: 5, xp: SampleXp);

            // Fresh boot + deploy.
            var (hostA, applierA, model) = BuildHeroApplied();
            HeroProfileLoader.LoadInto(hostA.Heroes, applierA.LastAppliedHeroes, profile);
            ulong fresh = StartStateHash.Compute(model, hostA.Heroes);

            // Reset + re-deploy on a DIFFERENT host.
            var (hostB, applierB, _) = BuildHeroApplied();
            RunTicks(hostB, 30);
            hostB.ClearForReset();
            ApplyValidated(applierB, model);
            HeroProfileLoader.LoadInto(hostB.Heroes, applierB.LastAppliedHeroes, profile);
            ulong afterReset = StartStateHash.Compute(model, hostB.Heroes);

            Assert.Equal(fresh, afterReset);
        }

        // ── I/O matrix: discard vs preserve playtest hero progress ─────────────────────────────

        [Fact]
        public void Reset_DiscardsHeroProgress_WhenNotPreserving()
        {
            var (host, applier, model) = BuildHeroApplied();
            PlayerProfile profile = BuildHeroProfile(level: 1, xp: Fixed.Zero); // authored base

            HeroProfileLoader.LoadInto(host.Heroes, applier.LastAppliedHeroes, profile);
            SimulateHeroGrowth(host.Heroes, HeroProfileLoader.MintId(profile), level: 5, xp: SampleXp);

            // Discard path (preserveHeroProgress = false): re-mint the profile's AUTHORED level/xp.
            host.ClearForReset();
            ApplyValidated(applier, model);
            HeroProfileLoader.LoadInto(host.Heroes, applier.LastAppliedHeroes, profile);

            Assert.True(TryGetLiveHero(host.Heroes, HeroProfileLoader.MintId(profile), out int level, out Fixed xp));
            Assert.Equal(1, level);            // growth discarded
            Assert.Equal(Fixed.Zero.Raw, xp.Raw);
        }

        [Fact]
        public void Reset_PreservesHeroProgress_WhenPersistenceTestMode()
        {
            var (host, applier, model) = BuildHeroApplied();
            PlayerProfile profile = BuildHeroProfile(level: 1, xp: Fixed.Zero);

            HeroProfileLoader.LoadInto(host.Heroes, applier.LastAppliedHeroes, profile);
            HeroId id = HeroProfileLoader.MintId(profile);
            SimulateHeroGrowth(host.Heroes, id, level: 5, xp: SampleXp);

            // Preserve path: snapshot live level/xp BEFORE the clear (mirrors MainScene.ResetToAuthoredStart step 1).
            Assert.True(TryGetLiveHero(host.Heroes, id, out int snapLevel, out Fixed snapXp));
            Assert.Equal(5, snapLevel);

            host.ClearForReset();
            ApplyValidated(applier, model);

            // Re-mint a profile carrying the SNAPSHOT values (same ProfileId → same HeroId).
            PlayerProfile snapProfile = BuildHeroProfile(level: snapLevel, xp: snapXp);
            HeroProfileLoader.LoadInto(host.Heroes, applier.LastAppliedHeroes, snapProfile);

            Assert.True(TryGetLiveHero(host.Heroes, id, out int level, out Fixed xp));
            Assert.Equal(5, level);            // progress kept
            Assert.Equal(SampleXp.Raw, xp.Raw);
        }

        // ── DW-13 (review): the applier→ownerSlot wiring seam, exercised against an applier-PRODUCED placed list ──

        [Fact]
        public void Applier_RecordsPlacedHeroOwnerFaction_AndOwnerSlotFilterMintsLocalHeroOnly()
        {
            // The DW-13 filter is only correct if the applier populates PlacedHero.OwnerFaction from the scenario slot
            // AND the production ownerSlot (LocalFaction) matches it. The fixture places the champion at slot 0 → Player1,
            // mirroring an offline skirmish (LocalFaction defaults to Player1). Verified against the applier's real output,
            // not a hand-built PlacedHero, so a drift in ScenarioApplier's slot→faction mapping would fail here.
            var (_, applier, _) = BuildHeroApplied();
            Assert.NotEmpty(applier.LastAppliedHeroes);
            Assert.Equal(Faction.Player1, applier.LastAppliedHeroes[0].OwnerFaction); // slot 0 → Player1

            PlayerProfile profile = BuildHeroProfile(level: 1, xp: Fixed.Zero);

            // ownerSlot = the owning (local) slot → mints the local player's placed hero.
            var localStore = new HeroStore();
            Assert.Equal(1, HeroProfileLoader.LoadInto(localStore, applier.LastAppliedHeroes, profile,
                                                       ownerSlot: Faction.Player1));
            // ownerSlot = a different slot → the placement is owner-filtered out → 0 minted (no cross-slot mint).
            var enemyStore = new HeroStore();
            Assert.Equal(0, HeroProfileLoader.LoadInto(enemyStore, applier.LastAppliedHeroes, profile,
                                                       ownerSlot: Faction.Player2));
        }

        // ── DW-15 (review): preserve routes through the manifest shape, so it carries ONLY the selected attributes ──

        [Fact]
        public void Reset_PreserveThroughPartialManifestShape_CarriesOnlySelectedAttributes()
        {
            // DW-15: the preserve snapshot now flows through PersistenceManifest.DeriveProfileShape() (the same seam Save
            // uses), so it honors WHICH attributes the manifest selects. A manifest carrying only hero.level re-mints the
            // grown level but DROPS xp — the old hardcoded-Values preserve path would have carried both regardless.
            var (host, applier, model) = BuildHeroApplied();
            PlayerProfile deployed = BuildHeroProfile(level: 1, xp: Fixed.Zero);
            HeroProfileLoader.LoadInto(host.Heroes, applier.LastAppliedHeroes, deployed);
            HeroId id = HeroProfileLoader.MintId(deployed);
            SimulateHeroGrowth(host.Heroes, id, level: 5, xp: SampleXp);

            Assert.True(TryGetLiveHero(host.Heroes, id, out int snapLevel, out Fixed snapXp));
            Assert.Equal(5, snapLevel);

            host.ClearForReset();
            ApplyValidated(applier, model);

            // Build the preserve snapshot through a manifest shape that selects ONLY hero.level (same ProfileId → same id).
            var levelOnly = new PersistenceManifest { Enabled = true };
            levelOnly.Attributes.Add("hero.level");
            PlayerProfile snapProfile = HeroProfileLoader.BuildProfile("champion#1", "champion", "alpha", "Champion", null,
                                                                       snapLevel, snapXp, levelOnly.DeriveProfileShape());
            HeroProfileLoader.LoadInto(host.Heroes, applier.LastAppliedHeroes, snapProfile);

            Assert.True(TryGetLiveHero(host.Heroes, id, out int level, out Fixed xp));
            Assert.Equal(5, level);               // hero.level IS in the shape → carried
            Assert.Equal(Fixed.Zero.Raw, xp.Raw); // hero.xp omitted from the shape → NOT preserved
        }

        // ── I/O matrix: invalid edited scenario blocks Play (fail-closed re-validation) ─────────

        [Fact]
        public void Reset_ReValidation_RejectsInvalidEditedScenario()
        {
            // The gate the reset relies on: an Edit-side change that makes the scenario invalid is rejected, located.
            ScenarioData invalid = GoldenApplierScenario.BuildModel();
            invalid.MapBounds = -1f; // out of range → validation fails

            ValidationResult r = new ScenarioValidator().Validate(invalid);
            Assert.False(r.Ok);
            Assert.NotNull(r.Error);           // located error surfaced

            // A valid edited scenario still passes (the reset would proceed).
            Assert.True(new ScenarioValidator().Validate(GoldenApplierScenario.BuildModel()).Ok);
        }

        // ── I/O matrix: fallback (no-JSON) scenario round-trips cleanly ─────────────────────────

        [Fact]
        public void ClearForReset_ThenApplyFallback_ReproducesByteIdenticalRun()
        {
            const int N = 90;

            FactionDefinition faction = GoldenApplierScenario.BuildFaction(); // has a Worker unit (the fallback spawns it)
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = faction;
            slotDefs[(int)Faction.Player2] = faction;

            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            host.ChecksumInterval = 1;
            var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);

            // Story 7.7: the fallback routes through the SAME validated mirror + Apply writer path as boot (the
            // legacy un-tokened ApplyFallback is retired) — the reset round-trip contract is unchanged.
            ApplyValidated(applier, ScenarioApplier.BuildFallbackMirror());
            List<(uint, uint)> run1 = RunTicks(host, N);

            host.ClearForReset();
            ApplyValidated(applier, ScenarioApplier.BuildFallbackMirror());
            List<(uint, uint)> run2 = RunTicks(host, N);

            Assert.Equal(run1, run2);
        }

        // ── Story 7.6 (review P8): a host reset mid-drain clears the batched continuation row, the unguarded
        //    window between ClearForReset and the re-apply's LoadScenario is safe (the director's stale
        //    _batchRowOfTrigger bookkeeping must never index the cleared LoopState rows), and after re-apply
        //    the trigger is NOT suppressed. ──

        [Fact]
        public void ResetMidDrain_ClearsTheContinuationRow_AndTheReappliedTriggerFiresAgain()
        {
            // unit_count_threshold(P1 ≥ 1) → for_each_batched(P1, batch 1) [body: display_message].
            var g = new TriggerGraph();
            g.Nodes.Add(new TriggerNode { Id = 0, Name = "drip" });
            g.Nodes.Add(new EventNode { Id = 1, Kind = "unit_count_threshold", Faction = 0, Count = 1, Operator = ">=" });
            g.Nodes.Add(new ForEachBatchedNode { Id = 2, Source = "faction_units", Faction = 0, BatchSize = 1 });
            g.Nodes.Add(new ActionNode { Id = 3, Kind = "display_message", Text = "x" });
            g.ExecEdges.Add(new ExecEdge(1, TriggerGraph.EventExecOutPort, 0, TriggerGraph.TriggerEventInPort));
            g.ExecEdges.Add(new ExecEdge(0, TriggerGraph.TriggerExecOutPort, 2, TriggerGraph.ActionExecInPort));
            g.ExecEdges.Add(new ExecEdge(2, TriggerGraph.ForEachBodyOutPort, 3, TriggerGraph.ActionExecInPort));
            var scenario = new ScenarioData { TriggerGraphJson = g.ToCanonicalJson() };

            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2));
            host.ChecksumInterval = 1;
            void SpawnUnits(int n)
            {
                for (int i = 0; i < n; i++)
                    host.World.Create(new FixedVec3(Fixed.FromInt(i), Fixed.Zero, Fixed.Zero),
                        Faction.Player1, Fixed.FromInt(100), Fixed.One);
            }

            SpawnUnits(3);
            host.ScenarioDirector.LoadScenario(scenario);
            host.StepOnce(); // fire + snapshot: row active, len 3
            host.StepOnce(); // drain 1 of 3 → MID-DRAIN
            Assert.True(host.LoopState.RowActive(0));
            Assert.Equal(1, host.LoopState.RowCursor(0));

            // The reset clears the continuation row storage entirely — no active rows survive.
            host.ClearForReset();
            Assert.Equal(0, host.LoopState.RowCount);

            // The unguarded WINDOW: a tick between ClearForReset and the re-apply, with the stale director
            // state still holding batched-row bookkeeping — and a live unit so the trigger actually FIRES into
            // the cleared row storage. The P8 guards (suppression bound + SnapshotBatched bound) make this a
            // deterministic no-op instead of an index-out-of-range crash.
            SpawnUnits(1);
            host.StepOnce();
            Assert.Equal(0, host.LoopState.RowCount); // still no rows — the fire snapshotted nothing

            // Re-apply (the normal reset path): rows reconfigure and the trigger is NOT suppressed — it fires
            // and snapshots a fresh full row.
            host.ClearForReset();
            SpawnUnits(3);
            host.ScenarioDirector.LoadScenario(scenario);
            host.StepOnce();
            Assert.Equal(1, host.LoopState.RowCount);
            Assert.True(host.LoopState.RowActive(0));
            Assert.Equal(3, host.LoopState.RowLength(0));
        }

        // ── Hash-version stamps: pinned so any UNSANCTIONED bump is loud (the 7.5 merge re-baseline is the
        //    sanctioned one — v18 sim fold + v10 canonical fold, both landed with the single golden re-record) ─

        [Fact]
        public void HashAlgoVersions_AreUnchanged()
        {
            Assert.Equal(22, SimChecksum.AlgoVersion);   // v21 = Story 7.13 TriggerEnabledStore; v22 = Story 11.6 production-queue + head-timer fold
            Assert.Equal(14, CanonicalModelHash.AlgoVersion); // v9 = Story 7.8 custom-UI fold; v10 = Story 7.5 custom-event registry fold (merge); v11 = Story 7.9 Button fold
            Assert.Equal(2, StartStateHash.AlgoVersion);
        }

        // ── Hero fixture + helpers ─────────────────────────────────────────────────────────────

        /// <summary>A faction with a placed HERO unit ("champion", <see cref="UnitDefinition.IsHero"/>) plus a worker,
        /// and a scenario that places the hero — so <see cref="ScenarioApplier.LastAppliedHeroes"/> records it.</summary>
        private static (SimulationHost host, ScenarioApplier applier, ScenarioData model) BuildHeroApplied()
        {
            var faction = new FactionDefinition
            {
                Id = "alpha", DisplayName = "Alpha",
                Units =
                {
                    new UnitDefinition { Id = "champion", DisplayName = "Champion", Category = "Melee", Hp = 200f, Speed = 3f, IsHero = true },
                    new UnitDefinition { Id = "worker",   DisplayName = "Worker",   Category = "Worker", Hp = 50f,  Speed = 4f },
                },
            };
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = faction;
            slotDefs[(int)Faction.Player2] = faction;

            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);

            var model = new ScenarioData
            {
                Id = "hero-reset", DisplayName = "Hero Reset Fixture", TerrainRef = "",
                MapBounds = 120f, WinCondition = WinCondition.EliminateAllUnits,
                PlayerSlots = new[]
                {
                    new ScenarioPlayerSlot { Slot = 0, FactionJson = "a.json", StartOre = 200f, BaseX = -45f, BaseZ = 0f },
                    new ScenarioPlayerSlot { Slot = 1, FactionJson = "a.json", StartOre = 200f, BaseX =  45f, BaseZ = 0f },
                },
                Units = new[]
                {
                    new ScenarioUnit { UnitId = "champion", Slot = 0, X = -40f, Z = 0f },
                },
            };
            ApplyValidated(applier, model);
            return (host, applier, model);
        }

        private static PlayerProfile BuildHeroProfile(int level, Fixed xp)
        {
            var shape = new PersistenceManifest { Enabled = true };
            shape.Attributes.Add("hero.level");
            shape.Attributes.Add("hero.xp");
            return HeroProfileLoader.BuildProfile("champion#1", "champion", "alpha", "Champion", null,
                                                  level, xp, shape.DeriveProfileShape());
        }

        /// <summary>Count live rows in the store (a fresh/cleared store returns 0; a single deploy returns 1).</summary>
        private static int LiveHeroCount(HeroStore heroes)
        {
            int n = 0;
            for (int slot = 0; slot < heroes.Count; slot++)
                if (heroes.Alive[slot]) n++;
            return n;
        }

        private static bool TryGetLiveHero(HeroStore heroes, HeroId id, out int level, out Fixed xp)
        {
            for (int slot = 0; slot < heroes.Count; slot++)
            {
                if (!heroes.Alive[slot] || heroes.Id[slot] != id) continue;
                level = heroes.Level[slot];
                xp    = heroes.Xp[slot];
                return true;
            }
            level = 0; xp = Fixed.Zero;
            return false;
        }

        /// <summary>Simulate Story-3.13 runtime XP growth by mutating the live row in place (pre-3.13 there is no such
        /// growth, so this stands in for it — the preserve vs discard seam is what is under test).</summary>
        private static void SimulateHeroGrowth(HeroStore heroes, HeroId id, int level, Fixed xp)
        {
            for (int slot = 0; slot < heroes.Count; slot++)
            {
                if (!heroes.Alive[slot] || heroes.Id[slot] != id) continue;
                heroes.Level[slot] = level;
                heroes.Xp[slot]    = xp;
                return;
            }
            Assert.Fail("live hero row not found for growth simulation");
        }
    }
}
