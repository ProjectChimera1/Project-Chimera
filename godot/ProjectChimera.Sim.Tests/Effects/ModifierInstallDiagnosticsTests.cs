#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;   // FactionDefinition / BuildingDefinition / ResearchDefinition / ResearchLevel
using ProjectChimera.Core.Sim;           // ILogSink — the AR-4 injected diagnostic seam
using ProjectChimera.Economy;            // BuildingStore / ResourceStore / ResearchStore / ResearchSystem
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// DW-83 — a REFUSED modifier install (the target's fixed <see cref="EffectCaps.MaxModifiersPerEntity"/>-slot
    /// ring is already full) is no longer SILENT. The refusal's BEHAVIOR is deliberately unchanged — same
    /// deterministic drop, same untouched slot state, same <c>false</c>/void return — so nothing here is a
    /// gameplay or checksum change; what changes is OBSERVABILITY: a monotonic
    /// <see cref="ModifierStore.RefusedInstallCount"/> tally plus a THROTTLED warn through the injected
    /// <see cref="ILogSink"/>, and (for the producer the ledger flagged) one aggregated per-completion
    /// <c>ResearchSystem</c> line naming the research whose earned, paid-for permanent bonus was dropped.
    ///
    /// <para>Before this, a unit holding item modifiers + a self-passive + hero-growth stacks + several completed
    /// researches could lose a bought upgrade with no event, no log and no player-visible signal.</para>
    ///
    /// <para><b>Throttling is not optional.</b> An aura RE-GRANTS its modifier every tick
    /// (<c>AbilityCastSystem.TickAuras</c>), so one ring-full aura target would emit 30 warn lines a second
    /// un-throttled — <see cref="RepeatedRefusals_AreThrottled_ToTheFirstThenOnePerLogEvery"/> pins the cadence.</para>
    /// </summary>
    public class ModifierInstallDiagnosticsTests
    {
        /// <summary>Capturing <see cref="ILogSink"/> (the NullLogSink pattern, but recording) — the DW-304 idiom.</summary>
        private sealed class RecordingSink : ILogSink
        {
            public readonly List<string> Infos = new List<string>();
            public readonly List<string> Warns = new List<string>();
            public void Info(string message) => Infos.Add(message);
            public void Warn(string message) => Warns.Add(message);
        }

        // ── Fixtures ────────────────────────────────────────────────────────────────────────────

        private static (EntityWorld world, ModifierSystem sys, ModifierStore store) Wire(ILogSink? log = null)
        {
            var world = new EntityWorld();
            var sys   = new ModifierSystem();
            var store = new ModifierStore(world, sys, null, null, null, log);
            sys.AttachStore(store);
            return (world, sys, store);
        }

        private static int Unit(EntityWorld world) =>
            world.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));

        /// <summary>A permanent, non-periodic +1 attack modifier — the cheapest thing that occupies one ring slot.</summary>
        private static Modifier PermanentAtk(int id) =>
            new Modifier(id, durationTicks: -1, StackRule.Refresh, maxStacks: 1,
                         Fixed.Zero, Fixed.FromInt(1), Fixed.Zero, StatusFlags.None, null, 0);

        /// <summary>Occupy EXACTLY <see cref="EffectCaps.MaxModifiersPerEntity"/> slots with distinct ids (100..).</summary>
        private static void FillRing(ModifierStore store, int id)
        {
            for (int k = 0; k < EffectCaps.MaxModifiersPerEntity; k++)
                Assert.True(store.Apply(id, PermanentAtk(100 + k), id, Faction.Player1),
                            "Filling the ring must ACCEPT every install up to the cap.");
            Assert.Equal(EffectCaps.MaxModifiersPerEntity, store.CountAt(id));
            Assert.Equal(0, store.RefusedInstallCount); // nothing refused yet — the counter must not be trigger-happy
        }

        // ── The refusal is counted and warned ───────────────────────────────────────────────────

        [Fact]
        public void RingFullRefusal_IsCounted_AndWarnsNamingHostDroppedModifierAndTheOccupyingIds()
        {
            var sink = new RecordingSink();
            var (world, _, store) = Wire(sink);
            int id = Unit(world);
            FillRing(store, id);

            // The (cap+1)th DISTINCT modifier: refused, dropped, ring untouched — but now observable.
            bool accepted = store.Apply(id, PermanentAtk(0x34390007), id, Faction.Player1);

            Assert.False(accepted);                                            // the pre-DW-83 contract is unchanged
            Assert.Equal(EffectCaps.MaxModifiersPerEntity, store.CountAt(id)); // …and so is the ring
            Assert.Equal(1, store.RefusedInstallCount);

            string warn = Assert.Single(sink.Warns);
            Assert.Contains("REFUSED", warn);
            Assert.Contains("0x34390007", warn);          // WHICH install was dropped (here: a research modifier id)
            Assert.Contains($"entity {id}", warn);        // WHO dropped it
            Assert.Contains("0x00000064", warn);          // the ring's occupants (id 100) — WHY it was full
            Assert.Empty(sink.Infos);                     // a lost buff is a WARNING, never an info line
        }

        [Fact]
        public void RingFullRefusal_WithoutASink_StillCounts_AndStaysSilent()
        {
            // Every golden / headless / fold-only construction passes no sink. The refusal must remain byte-identical
            // to its pre-DW-83 self (no throw, no state change) while the tally still records it for a later reader.
            var (world, _, store) = Wire(log: null);
            int id = Unit(world);
            FillRing(store, id);

            Assert.False(store.Apply(id, PermanentAtk(999), id, Faction.Player1));
            Assert.Equal(1, store.RefusedInstallCount);
            Assert.Equal(EffectCaps.MaxModifiersPerEntity, store.CountAt(id));
        }

        [Fact]
        public void InstallPersistent_OnAFullRing_IsCountedAndWarned()
        {
            // The void-returning sibling install path had NO signal at all before DW-83 (not even a bool).
            var sink = new RecordingSink();
            var (world, _, store) = Wire(sink);
            int id = Unit(world);
            FillRing(store, id);

            store.InstallPersistent(id, new PersistentEffect(null, new DirectHpDeltaEffect(Fixed.FromInt(-1)), null, 3, 5),
                                    id, Faction.Player1);

            Assert.Equal(EffectCaps.MaxModifiersPerEntity, store.CountAt(id)); // refused, never overflowed
            Assert.Equal(1, store.RefusedInstallCount);
            string warn = Assert.Single(sink.Warns);
            Assert.Contains("REFUSED", warn);
            Assert.Contains("PersistentEffect", warn);
        }

        // ── What must NOT be counted ────────────────────────────────────────────────────────────

        [Fact]
        public void AcceptedInstallsRefreshesAndStacks_NeverCountOrWarn()
        {
            var sink = new RecordingSink();
            var (world, _, store) = Wire(sink);
            int id = Unit(world);
            FillRing(store, id);

            // Re-applying an ALREADY-INSTALLED id on a full ring is a Refresh, not an install — it takes no new slot.
            Assert.True(store.Apply(id, PermanentAtk(100), id, Faction.Player1));
            // A Stack re-apply of an installed id is likewise not a new slot.
            Assert.True(store.Apply(id, new Modifier(101, durationTicks: -1, StackRule.Stack, maxStacks: 3,
                                                     Fixed.Zero, Fixed.FromInt(1), Fixed.Zero, StatusFlags.None, null, 0),
                                    id, Faction.Player1));

            Assert.Equal(0, store.RefusedInstallCount);
            Assert.Empty(sink.Warns);
        }

        [Fact]
        public void DeadOrStaleTarget_IsNotCountedAsARingFullRefusal()
        {
            // Apply also returns false for a dead/stale id. That is an ordinary race (the target died this tick), NOT
            // a lost buff — counting it would drown the real signal and mis-throttle the warn.
            var sink = new RecordingSink();
            var (world, _, store) = Wire(sink);
            int id = Unit(world);
            world.Destroy(id);

            Assert.False(store.Apply(id, PermanentAtk(1), id, Faction.Player1));
            Assert.False(store.Apply(9999, PermanentAtk(1), 9999, Faction.Player1)); // out-of-range id
            store.InstallPersistent(id, new PersistentEffect(null, null, null, 0, 0), id, Faction.Player1);

            Assert.Equal(0, store.RefusedInstallCount);
            Assert.Empty(sink.Warns);
        }

        // ── Throttle + reset ────────────────────────────────────────────────────────────────────

        [Fact]
        public void RepeatedRefusals_AreThrottled_ToTheFirstThenOnePerLogEvery()
        {
            var sink = new RecordingSink();
            var (world, _, store) = Wire(sink);
            int id = Unit(world);
            FillRing(store, id);

            // Exactly RefusedInstallLogEvery refusals: the 1st warns, then the LogEvery-th warns. Nothing between.
            for (int k = 0; k < ModifierStore.RefusedInstallLogEvery; k++)
                Assert.False(store.Apply(id, PermanentAtk(1000 + k), id, Faction.Player1));

            Assert.Equal(ModifierStore.RefusedInstallLogEvery, store.RefusedInstallCount);
            Assert.Equal(2, sink.Warns.Count);

            // …and the next block of refusals adds exactly one more line, not one per refusal.
            for (int k = 0; k < ModifierStore.RefusedInstallLogEvery; k++)
                store.Apply(id, PermanentAtk(2000 + k), id, Faction.Player1);

            Assert.Equal(2 * ModifierStore.RefusedInstallLogEvery, store.RefusedInstallCount);
            Assert.Equal(3, sink.Warns.Count);
        }

        [Fact]
        public void Clear_ResetsTheRefusalTally_SoAReplayStartsCleanAndTheThrottleReArms()
        {
            var sink = new RecordingSink();
            var (world, _, store) = Wire(sink);
            int id = Unit(world);
            FillRing(store, id);
            store.Apply(id, PermanentAtk(555), id, Faction.Player1);
            Assert.Equal(1, store.RefusedInstallCount);

            store.Clear(); // the Edit↔Play reset (SimulationHost.ClearForReset)

            Assert.Equal(0, store.RefusedInstallCount);

            // The throttle re-arms with the tally: the FIRST refusal of the new match warns again.
            int id2 = Unit(world);
            FillRing(store, id2);
            store.Apply(id2, PermanentAtk(556), id2, Faction.Player1);
            Assert.Equal(1, store.RefusedInstallCount);
            Assert.Equal(2, sink.Warns.Count); // one from before the reset, one after
        }

        // ── Determinism: the diagnostic touches nothing folded ──────────────────────────────────

        [Fact]
        public void RefusalDiagnostics_DoNotMoveTheSimChecksum()
        {
            // The tally + sink are unfolded, unread-by-sim diagnostics. A refusal must hash IDENTICALLY with and
            // without a sink wired — otherwise DW-83 would have moved every golden, which it must not.
            var buildings = new BuildingStore();
            var resources = new ResourceStore(Fixed.Zero);
            var registry  = new FactionRegistry(2);

            var sink = new RecordingSink();
            var (silentWorld, _, silentStore) = Wire(log: null);
            var (loudWorld, _, loudStore)     = Wire(sink);

            foreach ((EntityWorld w, ModifierStore s) in new[] { (silentWorld, silentStore), (loudWorld, loudStore) })
            {
                int id = Unit(w);
                FillRing(s, id);
                s.Apply(id, PermanentAtk(777), id, Faction.Player1);           // refused on both
                s.InstallPersistent(id, new PersistentEffect(null, null, null, 0, 0), id, Faction.Player1); // refused on both
            }

            Assert.NotEmpty(sink.Warns);                    // the loud store really did log
            Assert.Equal(2, loudStore.RefusedInstallCount); // …and really did count
            Assert.Equal(SimChecksum.Compute(silentWorld, buildings, resources, registry, silentStore),
                         SimChecksum.Compute(loudWorld,   buildings, resources, registry, loudStore));
        }

        // ── The producer the ledger named: research ─────────────────────────────────────────────

        private sealed class ResearchHarness
        {
            public EntityWorld World = new EntityWorld();
            public BuildingStore Buildings = new BuildingStore();
            public ResourceStore Resources = new ResourceStore(Fixed.Zero);
            public ResearchStore Research = new ResearchStore();
            public ModifierStore Modifiers = null!;
            public ResearchSystem Sys = null!;
            public int LabId;
        }

        /// <summary>A one-research faction with a pre-built lab and 1000 ore — the smallest fixture that can COMPLETE
        /// a research and drive <c>ResearchSystem</c>'s living-army application loop.</summary>
        private static ResearchHarness BuildResearch(ILogSink? sink)
        {
            var h = new ResearchHarness();
            var modSys = new ModifierSystem();
            h.Modifiers = new ModifierStore(h.World, modSys, null, null, null, sink);
            modSys.AttachStore(h.Modifiers);

            var faction = new FactionDefinition
            {
                Id = "p1",
                Buildings = new List<BuildingDefinition>
                {
                    new BuildingDefinition { Id = "lab", AvailableResearch = new[] { "armor_up" } },
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
                },
            };

            h.Sys   = new ResearchSystem(h.Buildings, h.Resources, h.Research, h.Modifiers, null, faction, null, sink);
            h.LabId = h.Buildings.Create(FixedVec3.Zero, Faction.Player1, BuildingType.Custom, buildingId: "lab");
            h.Buildings.ConstructionTimer[h.LabId] = Fixed.Zero; // operational
            h.Resources.AddOre(Faction.Player1, Fixed.FromInt(1000));
            return h;
        }

        [Fact]
        public void ResearchCompletion_OnFullRings_WarnsOnce_NamingTheResearchLevelAndAffectedUnitCount()
        {
            var sink = new RecordingSink();
            ResearchHarness h = BuildResearch(sink);

            // Two living Player1 units whose rings are ALREADY full (items + passives + earlier researches, modelled
            // here as 8 distinct permanent modifiers each) — the exact DW-83 scenario — plus a THIRD unit with room.
            // The third unit is what keeps this a PARTIAL refusal: since DW-623, a completion that reaches NOBODY is
            // voided and refunded instead of banked, so a two-of-two starved army would no longer exercise this
            // "completed anyway, but N units lost the bonus" line (see ResearchSystemTests' DW-623 coverage).
            int a = Unit(h.World), b = Unit(h.World);
            FillRing(h.Modifiers, a);
            for (int k = 0; k < EffectCaps.MaxModifiersPerEntity; k++)
                Assert.True(h.Modifiers.Apply(b, PermanentAtk(200 + k), b, Faction.Player1));
            int c = Unit(h.World); // room in its ring — one real recipient
            sink.Warns.Clear();

            Assert.True(h.Sys.StartResearchCommand(h.LabId, Faction.Player1, researchIndex: 0));
            h.Sys.Tick(h.World, Fixed.Zero); // TimeTicks 1 → completes this tick

            // The research DID complete (the level was paid for and banked, and unit c received it)…
            Assert.Equal(1, h.Research.CompletedLevels[(int)Faction.Player1][0]);
            Assert.Equal(Fixed.FromInt(2), h.World.EffectiveArmor[c]);
            // …but neither starved unit could receive its permanent bonus.
            Assert.Equal(2, h.Modifiers.RefusedInstallCount);

            string research = Assert.Single(sink.Warns, w => w.Contains("[ResearchSystem]"));
            Assert.Contains("armor_up", research);
            Assert.Contains("level 1", research);
            Assert.Contains("2 living unit(s)", research);
            Assert.Contains("DW-83", research); // the partial-refusal tail, not the DW-623 voided one
        }

        [Fact]
        public void ResearchCompletion_WithRoomInTheRing_NeverWarns()
        {
            // Negative control: the aggregate line must not fire on the ordinary path, or it is noise nobody reads.
            var sink = new RecordingSink();
            ResearchHarness h = BuildResearch(sink);
            int a = Unit(h.World);

            Assert.True(h.Sys.StartResearchCommand(h.LabId, Faction.Player1, researchIndex: 0));
            h.Sys.Tick(h.World, Fixed.Zero);

            Assert.Equal(1, h.Research.CompletedLevels[(int)Faction.Player1][0]);
            Assert.Equal(1, h.Modifiers.CountAt(a));   // the cumulative research modifier landed
            Assert.Equal(0, h.Modifiers.RefusedInstallCount);
            Assert.Empty(sink.Warns);
        }

        [Fact]
        public void ResearchCompletion_RefusedByEveryUnit_WarnsThatTheLevelWasVoidedAndRefunded()
        {
            // DW-623: when the refused count equals the eligible army, the aggregate line must say so — the DW-83
            // tail ("those units keep the research's cost but not its effect") is no longer true once the completion
            // is voided and the spend credited back, and a log that still said it would mislead the designer acting
            // on it. Behavioural coverage of the void itself lives in ResearchSystemTests.
            var sink = new RecordingSink();
            ResearchHarness h = BuildResearch(sink);
            int a = Unit(h.World);
            FillRing(h.Modifiers, a);
            sink.Warns.Clear();
            int oreBeforeStart = h.Resources.Ore[(int)Faction.Player1].ToInt();

            Assert.True(h.Sys.StartResearchCommand(h.LabId, Faction.Player1, researchIndex: 0));
            h.Sys.Tick(h.World, Fixed.Zero);

            Assert.Equal(0, h.Research.CompletedLevels[(int)Faction.Player1][0]); // un-banked
            Assert.Equal(oreBeforeStart, h.Resources.Ore[(int)Faction.Player1].ToInt()); // refunded
            Assert.Equal(1, h.Modifiers.RefusedInstallCount);

            string research = Assert.Single(sink.Warns, w => w.Contains("[ResearchSystem]"));
            Assert.Contains("armor_up", research);
            Assert.Contains("1 living unit(s)", research);
            Assert.Contains("VOIDED", research);
            Assert.Contains("DW-623", research);
            Assert.DoesNotContain("keep the research's cost", research); // the DW-83 partial tail must not fire here
        }
    }
}
