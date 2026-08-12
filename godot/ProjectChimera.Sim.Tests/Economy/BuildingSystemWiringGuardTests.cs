#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;
using ProjectChimera.Economy;
using ProjectChimera.Sim.Tests.Sim;   // ClearCompletenessSweep.InstanceFieldsOf (base-chain field walk)
using Xunit;

namespace ProjectChimera.Sim.Tests.Economy
{
    /// <summary>
    /// DW-517 — a COMPLETENESS guard that the production construction path actually WIRES
    /// <see cref="BuildingSystem"/>'s post-construction collaborator seams, <see cref="BuildingSystem.SetResourceNodes"/>
    /// above all.
    ///
    /// <para><b>The gap this closes.</b> DW-207 fixed a real leak: a Build command issued to a gathering worker
    /// permanently burned one of the target node's <c>MaxGatherers</c> slots, because <c>QueueWorkerBuild</c> only
    /// forgot the worker's <c>GatherTarget</c> and never released the reservation. The fix routes through
    /// <see cref="GatheringSystem.ReleaseGatherSlot"/>, which needs the <see cref="ResourceNodeStore"/> — and that
    /// store was threaded in through a NEW opt-in setter (<c>SetResourceNodes</c>) rather than a required constructor
    /// parameter, to avoid churning ~30 existing <see cref="BuildingSystem"/> ctor call sites and to match the
    /// existing <c>SetDslSimEvents</c> / <c>SetCombatEvents</c> pattern. That choice left the fix OPT-IN: a
    /// <see cref="BuildingSystem"/> constructed without the setter call silently behaves exactly as it did before
    /// DW-207 (<c>_nodes == null</c> ⇒ the release is skipped and only <c>GatherTarget</c> is cleared), and NOTHING in
    /// the suite noticed — <c>GatheringWorkerLifecycleTests</c> builds its own <see cref="BuildingSystem"/> and calls
    /// the setter itself, so it proves the RELEASE works, never that the shipped host asks for it.</para>
    ///
    /// <para><b>What this file asserts.</b> Against a REAL <see cref="SimulationHost"/> — the only production
    /// <see cref="BuildingSystem"/> construction in the codebase, and the one <c>MainScene</c> consumes via
    /// <c>_host.BuildSys</c> — that (1) the node store is wired and is the host's OWN store, (2) the shipped
    /// build-interrupt path therefore really does hand the gather slot back end-to-end, and (3) EVERY
    /// single-parameter reference-typed <c>Set*</c> seam on <see cref="BuildingSystem"/> is non-null after
    /// construction, so a FUTURE seam added the same opt-in way and forgotten by the host also fails red instead of
    /// shipping silently disabled.</para>
    ///
    /// <para>Mirrors the <c>EntityWorldClearCompletenessTests</c> reflection-guard style: an exhaustive reflection
    /// sweep, a non-vacuity pin so a rename cannot empty the swept set, and an explicit justified allowlist rather
    /// than a silent skip. Godot-free and <see cref="Fixed"/>-only; it constructs a host and issues one order but
    /// never ticks the loop, so it folds nothing into <c>SimChecksum</c> and moves no golden.</para>
    /// </summary>
    public class BuildingSystemWiringGuardTests
    {
        // ── The exclusion allowlist (explicit + justified — never a silent skip) ────────────────

        /// <summary>
        /// <c>Set*</c> seams the production <see cref="SimulationHost"/> deliberately leaves unwired. EMPTY today:
        /// the host wires all three of <c>SetDslSimEvents</c> / <c>SetCombatEvents</c> / <c>SetResourceNodes</c>.
        /// A new entry needs the same kind of written justification the DW-19 clear-sweep allowlist demands — "the
        /// host has no such collaborator" is a design decision, not a test-fixing move, and leaving a seam unwired
        /// means the feature behind it is DISABLED in the shipped game.
        /// </summary>
        private static readonly string[] Allowlist = Array.Empty<string>();

        /// <summary>The seams that exist today. Pinned so a rename/removal cannot silently empty the sweep and turn
        /// every assertion below into a vacuous pass (the failure mode the DW-19 allowlist-reconciliation guard
        /// exists for). Containment, not equality — a NEW correctly-wired seam flows into the sweep automatically
        /// and must NOT have to edit this list.</summary>
        private static readonly string[] KnownSeams =
        {
            "SetResourceNodes",  // DW-207 — the node store QueueWorkerBuild releases the interrupted gather slot through
            "SetDslSimEvents",   // Story 7.13 — unit_trained raise
            "SetCombatEvents",   // Story 11.4 — TrainingComplete presentation cue
        };

        // ── The DW-517 guard proper ────────────────────────────────────────────────────────────

        [Fact]
        public void ProductionHost_WiresTheResourceNodeStore_IntoBuildingSystem()
        {
            SimulationHost host = NewProductionHost();

            FieldInfo field = RequireSoleFieldOfType(typeof(ResourceNodeStore));
            object? wired = field.GetValue(host.BuildSys);

            Assert.True(wired is not null,
                "SimulationHost constructed a BuildingSystem WITHOUT calling SetResourceNodes(Nodes). The DW-207 " +
                "build-interrupt fix is opt-in: with a null node store, QueueWorkerBuild silently falls back to the " +
                "pre-DW-207 behaviour and every Build command issued to a gathering worker permanently burns one of " +
                "that node's MaxGatherers slots. Re-add the wiring in the SimulationHost ctor — do not relax this test.");

            // …and it must be the host's OWN store, not some private copy: GatheringSystem reserves slots on
            // SimulationHost.Nodes, so a release against any other instance decrements a counter nobody reads.
            Assert.Same(host.Nodes, wired);
        }

        [Fact]
        public void ProductionHost_BuildInterruptingAGatheringWorker_ReleasesTheNodeSlot_EndToEnd()
        {
            SimulationHost host = NewProductionHost();
            host.Resources.Ore[(int)Faction.Player1] = Fixed.FromInt(500);

            // maxGatherers: 1 makes the leak observable — a burned slot leaves the node permanently un-gatherable
            // while still looking perfectly full.
            int node = host.Nodes.Create(V(2, 0), Fixed.FromInt(1000), Fixed.FromInt(5), maxGatherers: 1);

            // The exact reservation state GatheringSystem's Idle -> MovingToResource -> Gathering walk produces
            // (set directly so the guard pins the WIRING, not the gather state machine that GatheringWorkerLifecycle
            // tests already cover): the worker occupies the node and the node's counter records that reservation.
            int worker = host.World.Create(V(0, 0), Faction.Player1, Fixed.FromInt(50), Fixed.FromInt(3));
            host.World.GatherState[worker]   = GatherState.Gathering;
            host.World.CarryCapacity[worker] = Fixed.FromInt(20);
            host.World.GatherTarget[worker]  = node;
            host.Nodes.AssignedGatherers[node] = 1;

            int bId = host.BuildSys.QueueWorkerBuild(worker, BuildingType.Barracks, V(30, 30),
                                                     Faction.Player1, host.Resources, host.World);
            Assert.True(bId >= 0, "fixture assumption: the production host accepted the build order");

            // Fails red the moment SimulationHost stops calling SetResourceNodes: an unwired BuildingSystem clears
            // GatherTarget only and this counter stays at 1 forever.
            Assert.Equal(0, host.Nodes.AssignedGatherers[node]);
            Assert.Equal(-1, host.World.GatherTarget[worker]); // unchanged DW-207 contract: the target is still forgotten
            Assert.Equal(UnitCommand.Build, host.World.CommandState[worker]);
        }

        // ── The exhaustive sweep: EVERY opt-in seam, not just today's ──────────────────────────

        /// <summary>
        /// The generalization of the guard above. <see cref="BuildingSystem"/> injects collaborators through
        /// post-construction setters instead of ctor parameters, so "the host forgot to wire it" is a whole CLASS of
        /// silently-disabled-feature bug, not a one-off. Sweep every such seam and require the production host left
        /// none of them null.
        /// </summary>
        [Fact]
        public void ProductionHost_WiresEveryPostConstructionSeam_ExhaustiveReflectionSweep()
        {
            SimulationHost host = NewProductionHost();

            var unwired = new List<string>();
            foreach ((MethodInfo setter, FieldInfo field) in WiringSeams())
            {
                if (IsAllowlisted(setter.Name)) continue;
                if (field.GetValue(host.BuildSys) is null)
                    unwired.Add($"{setter.Name} -> {field.Name} ({field.FieldType.Name})");
            }

            Assert.True(unwired.Count == 0,
                "SimulationHost left BuildingSystem seam(s) unwired: [" + string.Join(", ", unwired) + "]. Each of " +
                "these is an opt-in collaborator whose feature is DISABLED while the field is null (DW-517, generalized " +
                "from the DW-207 gather-slot leak). Wire it in the SimulationHost ctor beside the other " +
                "BuildSys.Set* calls, or add a justified entry to this file's Allowlist explaining why the shipped " +
                "game runs without it.");
        }

        /// <summary>
        /// The sweep must keep SEEING the seams — otherwise renaming <c>SetResourceNodes</c> (or dropping the
        /// <c>Set*</c> convention) would empty the swept set and turn the guard above into a vacuous pass, the exact
        /// silent-widening failure mode the DW-19 clear-sweep allowlist reconciliation exists to prevent. Also
        /// reconciles the allowlist: an exemption naming a seam that no longer exists exempts nothing and hides a
        /// real hole.
        /// </summary>
        [Fact]
        public void WiringSweep_StillSeesTheKnownSeams_AndTheAllowlistNamesOnlyRealOnes()
        {
            var discovered = new List<string>();
            foreach ((MethodInfo setter, FieldInfo _) in WiringSeams()) discovered.Add(setter.Name);

            foreach (string seam in KnownSeams)
                Assert.True(discovered.Contains(seam),
                    $"BuildingSystem seam '{seam}' is no longer discovered by the wiring sweep (found: " +
                    $"[{string.Join(", ", discovered)}]). Either it was renamed/removed — update KnownSeams " +
                    "deliberately — or the sweep's discovery rule stopped matching it, which would silently empty " +
                    "the completeness guard.");

            foreach (string allowed in Allowlist)
                Assert.True(discovered.Contains(allowed),
                    $"Wiring-sweep allowlist entry '{allowed}' no longer resolves to a BuildingSystem seam. Remove " +
                    "the stale exemption rather than leaving it to exempt nothing.");
        }

        // ── Discovery + fixture ────────────────────────────────────────────────────────────────

        private static FixedVec3 V(int x, int z) => new FixedVec3(Fixed.FromInt(x), Fixed.Zero, Fixed.FromInt(z));

        /// <summary>A faction authoring the one building the end-to-end guard places (no prerequisites, cheap).</summary>
        private static FactionDefinition BuilderFaction()
        {
            var f = new FactionDefinition { Id = "builder", DisplayName = "Builder" };
            f.Buildings.Add(new BuildingDefinition
            {
                Id = "barracks", Category = "Structure", CostOre = 10, ConstructionTime = 10f,
                SupplyBonus = 0, ProducesCategory = "Melee", Hp = 100f,
            });
            return f;
        }

        /// <summary>The REAL production composition root — the same call ServerBootstrap / the scene bootstrap make.
        /// Nothing here is test-special: the wiring under assertion happens inside the host's own ctor.</summary>
        private static SimulationHost NewProductionHost() =>
            SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), BuilderFaction(), BuilderFaction());

        /// <summary>
        /// Every post-construction collaborator seam on <see cref="BuildingSystem"/>, paired with the private field it
        /// fills. Discovery rule: a DECLARED public instance method returning void, named <c>Set*</c>, taking exactly
        /// ONE reference-typed parameter — which is precisely the shape of an injected collaborator, and deliberately
        /// excludes <c>SetFactionDef(Faction, def)</c> (a two-arg per-slot override, not a seam) and
        /// <c>SetRallyCommand(...)</c> (a bool-returning player ORDER). The parameter type resolves the field, so a
        /// field RENAME cannot break the guard.
        ///
        /// <para>DW-672 lifted the discovery rule itself into the shared <see cref="WiringSeamSweep"/> so the FOUR
        /// other systems <c>SimulationHost</c> wires post-construction (AbilityCastSystem / CombatSystem /
        /// ProjectileSystem / HeroXpSystem) are swept by the same rule in <c>PostConstructionWiringGuardTests</c>
        /// rather than being covered for BuildingSystem only. This file keeps the BuildingSystem-specific teeth (the
        /// node store's identity and the end-to-end release).</para>
        /// </summary>
        private static IReadOnlyList<(MethodInfo Setter, FieldInfo Field)> WiringSeams() =>
            WiringSeamSweep.Seams(typeof(BuildingSystem));

        /// <summary>The single <see cref="BuildingSystem"/> instance field of the given type — the field a
        /// <c>Set*</c> seam of that parameter type fills.</summary>
        private static FieldInfo RequireSoleFieldOfType(Type fieldType) =>
            WiringSeamSweep.RequireSoleFieldOfType(typeof(BuildingSystem), fieldType);

        private static bool IsAllowlisted(string setterName) => WiringSeamSweep.Contains(Allowlist, setterName);
    }
}
