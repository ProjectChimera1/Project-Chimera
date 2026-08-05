#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;   // FactionDefinition / BuildingDefinition / ResearchDefinition / ResearchLevel
using ProjectChimera.Core.Sim;           // ILogSink / SimulationHost
using ProjectChimera.Economy;            // ResearchStore / ResearchSystem
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Economy
{
    /// <summary>
    /// DW-624 — the future-spawn research catch-up path (<see cref="ResearchSystem.ApplyCompletedResearch"/>, wired
    /// to <c>EntityWorld.OnUnitDefinitionApplied</c>) now has a designer-facing diagnostic with RESEARCH-NAME
    /// attribution, without per-spawn spam.
    ///
    /// <para>Before this, a unit that spawned into a full <see cref="EffectCaps.MaxModifiersPerEntity"/>-slot
    /// modifier ring silently lost a banked, already-paid-for research bonus (and unlike the completion path there
    /// is nothing to refund — DW-679). It was not strictly SILENT: <see cref="ModifierStore"/>'s own throttled warn
    /// still fired, naming a bare <c>0x3439_00xx</c> modifier id. What was missing is the research NAME, and the
    /// reason it was missing is that this hook fires once PER SPAWN — the aggregate line
    /// <c>CompleteResearch</c> emits would have become 200 lines on a 200-unit scenario load.</para>
    ///
    /// <para>The fix accumulates refusals per match (per faction, per research) and emits ONE line per faction from
    /// <see cref="ResearchSystem.FlushSpawnCatchUpDiagnostics"/>, which <c>SimulationHost.ClearForReset</c> calls at
    /// the per-match teardown and <c>SimulationHost.FlushMatchDiagnostics</c> exposes for an explicit end-of-match /
    /// end-of-load flush. <b>Anti-spam is the load-bearing half of this entry</b>, so
    /// <see cref="TwoHundredStarvedSpawns_EmitNoResearchLine_UntilTheSingleFlush"/> pins the cadence directly.</para>
    /// </summary>
    public class ResearchSpawnCatchUpDiagnosticsTests
    {
        /// <summary>Capturing <see cref="ILogSink"/> (the DW-304 / DW-83 idiom — NullLogSink, but recording).</summary>
        private sealed class RecordingSink : ILogSink
        {
            public readonly List<string> Infos = new List<string>();
            public readonly List<string> Warns = new List<string>();
            public void Info(string message) => Infos.Add(message);
            public void Warn(string message) => Warns.Add(message);
        }

        private const int ArmorUpIdx  = 0;
        private const int DamageUpIdx = 1;

        // ── Fixture ─────────────────────────────────────────────────────────────────────────────

        private sealed class Harness
        {
            public EntityWorld World = new EntityWorld();
            public BuildingStore Buildings = new BuildingStore();
            public ResourceStore Resources = new ResourceStore(Fixed.Zero);
            public ResearchStore Research = new ResearchStore();
            public ModifierStore Modifiers = null!;
            public ResearchSystem Sys = null!;
            public int LabId;
        }

        /// <summary>A TWO-research faction (so the per-research breakdown has something to distinguish) with an
        /// operational lab and ore to spend. Both ladders are one level, one tick.</summary>
        private static FactionDefinition TwoResearchFaction() => new FactionDefinition
        {
            Id = "p1",
            Buildings = new List<BuildingDefinition>
            {
                new BuildingDefinition { Id = "lab", AvailableResearch = new[] { "armor_up", "damage_up" } },
            },
            Units = new List<UnitDefinition>
            {
                new UnitDefinition { Id = "worker", DisplayName = "Worker", Category = "Worker", Hp = 100f, Speed = 3f },
            },
            Research = new List<ResearchDefinition>
            {
                new ResearchDefinition
                {
                    Id = "armor_up",
                    Prerequisites = System.Array.Empty<string>(),
                    Levels = new List<ResearchLevel>
                    {
                        new ResearchLevel { Cost = new Dictionary<string, int> { { "ore", 10 } }, TimeTicks = 1,
                                            ModifierDelta = new ResearchModifierDelta { ArmorDelta = 2f } },
                    },
                },
                new ResearchDefinition
                {
                    Id = "damage_up",
                    Prerequisites = System.Array.Empty<string>(),
                    Levels = new List<ResearchLevel>
                    {
                        new ResearchLevel { Cost = new Dictionary<string, int> { { "ore", 10 } }, TimeTicks = 1,
                                            ModifierDelta = new ResearchModifierDelta { AttackDamageDelta = 3f } },
                    },
                },
            },
        };

        private static Harness Build(ILogSink? sink)
        {
            var h = new Harness();
            var modSys = new ModifierSystem();
            h.Modifiers = new ModifierStore(h.World, modSys, null, null, null, sink);
            modSys.AttachStore(h.Modifiers);

            FactionDefinition faction = TwoResearchFaction();
            h.Sys   = new ResearchSystem(h.Buildings, h.Resources, h.Research, h.Modifiers, null, faction, null, sink);
            h.LabId = h.Buildings.Create(FixedVec3.Zero, Faction.Player1, BuildingType.Custom, buildingId: "lab");
            h.Buildings.ConstructionTimer[h.LabId] = Fixed.Zero; // operational
            h.Resources.AddOre(Faction.Player1, Fixed.FromInt(1000));
            return h;
        }

        /// <summary>Bank both research levels with an EMPTY army — the DW-623 void/refund rule explicitly exempts a
        /// factionless-of-units completion (nothing was dropped, and every FUTURE spawn is meant to receive it via the
        /// catch-up path this file is about), so both levels really are banked and paid for.</summary>
        private static void CompleteBothResearches(Harness h)
        {
            Assert.Empty(Alive(h.World)); // the DW-623 empty-army exemption is what makes this bank, not void
            foreach (int ri in new[] { ArmorUpIdx, DamageUpIdx })
            {
                Assert.True(h.Sys.StartResearchCommand(h.LabId, Faction.Player1, ri));
                h.Sys.Tick(h.World, Fixed.Zero); // TimeTicks 1 → completes this tick
                Assert.Equal(1, h.Research.CompletedLevels[(int)Faction.Player1][ri]);
            }
        }

        private static List<int> Alive(EntityWorld world)
        {
            var live = new List<int>();
            for (int i = 0; i < world.HighWaterMark; i++)
                if (world.IsAlive(i)) live.Add(i);
            return live;
        }

        private static int Unit(EntityWorld world) =>
            world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));

        /// <summary>A permanent, non-periodic +1 attack modifier — the cheapest thing that occupies one ring slot.</summary>
        private static Modifier PermanentAtk(int id) =>
            new Modifier(id, durationTicks: -1, StackRule.Refresh, maxStacks: 1,
                         Fixed.Zero, Fixed.FromInt(1), Fixed.Zero, StatusFlags.None, null, 0);

        /// <summary>Occupy <paramref name="slots"/> of the entity's ring with distinct non-research ids.</summary>
        private static void FillRing(ModifierStore store, int id, int slots = EffectCaps.MaxModifiersPerEntity)
        {
            for (int k = 0; k < slots; k++)
                Assert.True(store.Apply(id, PermanentAtk(100 + k), id, Faction.Player1),
                            "Filling the ring must ACCEPT every install up to the cap.");
            Assert.Equal(slots, store.CountAt(id));
        }

        /// <summary>Spawn a unit whose ring is ALREADY full, then run the catch-up hook exactly as
        /// <c>EntityWorld.OnUnitDefinitionApplied</c> does.</summary>
        private static int StarvedSpawn(Harness h)
        {
            int id = Unit(h.World);
            FillRing(h.Modifiers, id);
            h.Sys.ApplyCompletedResearch(h.World, id);
            return id;
        }

        private static List<string> ResearchLines(RecordingSink sink) =>
            sink.Warns.Where(w => w.Contains("[ResearchSystem]")).ToList();

        // ── The aggregate line: one per faction, naming every research ──────────────────────────

        [Fact]
        public void StarvedSpawns_AreAggregated_IntoOneLineNamingEveryResearchAndTheSpawnCount()
        {
            var sink = new RecordingSink();
            Harness h = Build(sink);
            CompleteBothResearches(h);
            sink.Warns.Clear();

            for (int i = 0; i < 3; i++) StarvedSpawn(h);

            // Nothing from ResearchSystem YET — the whole point is that the per-spawn hook stays quiet.
            Assert.Empty(ResearchLines(sink));
            Assert.Equal(6, h.Modifiers.RefusedInstallCount); // 3 spawns x 2 completed researches

            int flushed = h.Sys.FlushSpawnCatchUpDiagnostics();

            Assert.Equal(6, flushed);
            string line = Assert.Single(ResearchLines(sink));
            Assert.Contains("armor_up", line);          // research-NAME attribution — the thing DW-624 adds
            Assert.Contains("damage_up", line);
            Assert.Contains("3 spawn(s) for Player1", line);
            Assert.Contains("6 refused install(s)", line);
            Assert.Contains("DW-624", line);
            Assert.Empty(sink.Infos);                   // a lost, paid-for upgrade is a WARNING, never an info line
        }

        [Fact]
        public void OnlyTheRefusedResearchIsNamed_NotEveryCompletedOne()
        {
            // Attribution must be PER RESEARCH, not per spawn: a unit with exactly one free slot receives the first
            // research (ascending index) and refuses only the second. A tally that just counted spawns would name
            // both and send the designer after the wrong upgrade.
            var sink = new RecordingSink();
            Harness h = Build(sink);
            CompleteBothResearches(h);
            sink.Warns.Clear();

            int id = Unit(h.World);
            FillRing(h.Modifiers, id, EffectCaps.MaxModifiersPerEntity - 1); // ONE free slot
            h.Sys.ApplyCompletedResearch(h.World, id);

            Assert.Equal(Fixed.FromInt(2), h.World.EffectiveArmor[id]);        // armor_up landed in the last slot
            Assert.Equal(1, h.Modifiers.RefusedInstallCount);                  // damage_up did not

            Assert.Equal(1, h.Sys.FlushSpawnCatchUpDiagnostics());
            string line = Assert.Single(ResearchLines(sink));
            Assert.Contains("damage_up' on 1 spawn(s)", line);
            Assert.DoesNotContain("armor_up", line); // the research that DID land must not be blamed
        }

        // ── Anti-spam: the reason this is aggregated at all ─────────────────────────────────────

        [Fact]
        public void TwoHundredStarvedSpawns_EmitNoResearchLine_UntilTheSingleFlush()
        {
            // The ledger's own example: "a 200-unit scenario load with full rings would emit 200 lines". The whole
            // reason DW-83 left this path alone. One line at the end, never one per spawn.
            var sink = new RecordingSink();
            Harness h = Build(sink);
            CompleteBothResearches(h);
            sink.Warns.Clear();

            for (int i = 0; i < 200; i++) StarvedSpawn(h);

            Assert.Empty(ResearchLines(sink));
            // ModifierStore's own generic warn stays throttled (first + one per RefusedInstallLogEvery) — this
            // diagnostic must not have re-introduced spam through that path either.
            Assert.True(sink.Warns.Count <= 2 + 400 / ModifierStore.RefusedInstallLogEvery,
                        $"ModifierStore's throttled warn must stay throttled; saw {sink.Warns.Count} lines.");

            Assert.Equal(400, h.Sys.FlushSpawnCatchUpDiagnostics()); // 200 spawns x 2 researches
            string line = Assert.Single(ResearchLines(sink));
            Assert.Contains("200 spawn(s)", line);
            Assert.Contains("armor_up' on 200 spawn(s)", line);
        }

        // ── Negative controls + per-match semantics ─────────────────────────────────────────────

        [Fact]
        public void SpawnsWithRoomInTheRing_FlushNothing()
        {
            // If the ordinary path ever emitted this line it would be noise nobody reads.
            var sink = new RecordingSink();
            Harness h = Build(sink);
            CompleteBothResearches(h);
            sink.Warns.Clear();

            int id = Unit(h.World);
            h.Sys.ApplyCompletedResearch(h.World, id);

            Assert.Equal(2, h.Modifiers.CountAt(id));  // both cumulative research modifiers landed
            Assert.Equal(0, h.Modifiers.RefusedInstallCount);
            Assert.Equal(0, h.Sys.FlushSpawnCatchUpDiagnostics());
            Assert.Empty(sink.Warns);
        }

        [Fact]
        public void Flush_IsIdempotent_AndTheTallyIsPerMatch()
        {
            var sink = new RecordingSink();
            Harness h = Build(sink);
            CompleteBothResearches(h);
            sink.Warns.Clear();

            StarvedSpawn(h);
            Assert.Equal(2, h.Sys.FlushSpawnCatchUpDiagnostics());
            Assert.Single(ResearchLines(sink));

            // A second flush with nothing accumulated is silent — the tally was ZEROED, not merely reported, so the
            // next match cannot inherit the previous one's count.
            Assert.Equal(0, h.Sys.FlushSpawnCatchUpDiagnostics());
            Assert.Single(ResearchLines(sink));

            // …and a fresh refusal after the flush is counted from zero, not added onto the old total.
            StarvedSpawn(h);
            Assert.Equal(2, h.Sys.FlushSpawnCatchUpDiagnostics());
            List<string> lines = ResearchLines(sink);
            Assert.Equal(2, lines.Count);
            Assert.Contains("1 spawn(s) for Player1", lines[1]); // 1, not 2 — the tally really restarted
        }

        [Fact]
        public void WithoutASink_RefusalsAreStillTalliedAndFlushed_Silently()
        {
            // Every golden / headless / Tier-1 host passes no sink. The accounting must still work (so a later
            // reader — or this return value — can see it) while emitting absolutely nothing.
            Harness h = Build(sink: null);
            CompleteBothResearches(h);
            StarvedSpawn(h);
            StarvedSpawn(h);

            Assert.Equal(4, h.Sys.FlushSpawnCatchUpDiagnostics());
            Assert.Equal(0, h.Sys.FlushSpawnCatchUpDiagnostics());
        }

        // ── The real wiring: SimulationHost ─────────────────────────────────────────────────────

        [Fact]
        public void RealSimulationHost_FlushesTheEndingMatchTally_OnClearForReset()
        {
            // Drives the ACTUAL EntityWorld.OnUnitDefinitionApplied subscription SimulationHost wires at
            // construction, and the ACTUAL per-match teardown — so a dropped flush call (or a dropped hook) fails
            // here even though the standalone-harness tests above would still pass.
            var sink = new RecordingSink();
            FactionDefinition faction = TwoResearchFaction();
            var host = SimulationHost.Create(sink, new FactionRegistry(2), faction, faction);

            int lab = host.Buildings.Create(FixedVec3.Zero, Faction.Player1, BuildingType.Custom, buildingId: "lab");
            host.Buildings.ConstructionTimer[lab] = Fixed.Zero;
            host.Resources.AddOre(Faction.Player1, Fixed.FromInt(1000));
            Assert.True(host.ResearchSys.StartResearchCommand(lab, Faction.Player1, ArmorUpIdx));
            host.StepOnce(); // TimeTicks=1 → banked with an empty army (DW-623 exemption)
            Assert.Equal(1, host.Research.CompletedLevels[(int)Faction.Player1][ArmorUpIdx]);
            sink.Warns.Clear();

            // A spawn through the REAL path: Create, then a full ring (items/passives already installed), then
            // ApplyUnitDefinition — which is what fires the catch-up hook.
            int spawned = host.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            FillRing(host.Modifiers, spawned);
            host.World.ApplyUnitDefinition(spawned, faction.Units[0]);

            Assert.Equal(Fixed.Zero, host.World.EffectiveArmor[spawned]); // the banked +2 armor was DROPPED
            Assert.Empty(ResearchLines(sink));                            // …and stayed unattributed during the match

            host.ClearForReset(); // the per-match teardown IS the match-end flush

            string line = Assert.Single(ResearchLines(sink));
            Assert.Contains("armor_up", line);
            Assert.Contains("1 spawn(s) for Player1", line);
            Assert.Contains("DW-624", line);

            // The tally is per-match: tearing down again reports nothing, and the explicit host hook agrees.
            host.ClearForReset();
            Assert.Single(ResearchLines(sink));
            Assert.Equal(0, host.FlushMatchDiagnostics());
        }

        // ── Determinism: the diagnostic touches nothing folded ──────────────────────────────────

        [Fact]
        public void CatchUpAggregation_DoesNotMoveTheSimChecksum()
        {
            // The tally, the flush and the sink are unfolded, unread-by-sim diagnostics. A run that accumulates and
            // FLUSHES them must hash identically to one that never had a sink at all — otherwise DW-624 would move
            // every golden, which it must not.
            var registry = new FactionRegistry(2);
            var sink = new RecordingSink();
            Harness loud   = Build(sink);
            Harness silent = Build(sink: null);

            foreach (Harness h in new[] { loud, silent })
            {
                CompleteBothResearches(h);
                StarvedSpawn(h);
                StarvedSpawn(h);
                h.Sys.FlushSpawnCatchUpDiagnostics();
            }

            Assert.NotEmpty(ResearchLines(sink));                  // the loud harness really did report
            Assert.Equal(4, silent.Modifiers.RefusedInstallCount); // …and both really did refuse the same installs
            Assert.Equal(
                SimChecksum.Compute(silent.World, silent.Buildings, silent.Resources, registry, silent.Modifiers,
                                    research: silent.Research),
                SimChecksum.Compute(loud.World, loud.Buildings, loud.Resources, registry, loud.Modifiers,
                                    research: loud.Research));
        }
    }
}
