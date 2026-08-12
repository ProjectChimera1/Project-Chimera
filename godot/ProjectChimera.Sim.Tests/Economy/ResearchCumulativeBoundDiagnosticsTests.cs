#nullable enable
using System.Collections.Generic;
using System.Linq;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;   // FactionDefinition / BuildingDefinition / ResearchDefinition / ResearchLevel
using ProjectChimera.Core.Sim;           // ILogSink
using ProjectChimera.Economy;            // ResearchStore / ResearchSystem
using ProjectChimera.Effects;            // Modifier (MaxStatDeltaTotalRaw)
using Xunit;

namespace ProjectChimera.Sim.Tests.Economy
{
    /// <summary>
    /// DW-751 — a research cumulative above DW-488's per-modifier bound is TRUNCATED on delivery, and the divergence
    /// is now reported instead of being silent.
    ///
    /// <para>DW-650 deliberately bounded only the <see cref="Modifier"/> BUILT from the cumulative, never the banked
    /// cumulative itself (<c>SaturatingAdd</c>'s full-Fixed-range semantics are persisted, folded into SimChecksum and
    /// load-bearing for DW-623's void/rollback snapshot). The consequence is a silent split: the store can bank a
    /// total far past ≈4096 stat units, but each unit receives at most <see cref="Modifier.MaxStatDeltaTotalRaw"/>
    /// — so a designer who authors a repeatable ladder past the bound reads one number in the research store while
    /// the army receives another, with nothing logged. The gap was always intentional; what was missing is
    /// OBSERVABILITY, which is what this file pins.</para>
    ///
    /// <para>The report uses DW-624's aggregation treatment for DW-624's reason: the clamp happens inside
    /// <c>BuildCumulativeModifier</c>, which runs once per AFFECTED UNIT (the completion walk sweeps the living army;
    /// the catch-up hook fires once per spawn), so an inline warn on a repeatable ladder would emit one line per unit
    /// per completion. <see cref="ManyClampedDeliveries_EmitNoLine_UntilTheSingleFlush"/> pins that cadence
    /// directly.</para>
    /// </summary>
    public class ResearchCumulativeBoundDiagnosticsTests
    {
        /// <summary>Capturing <see cref="ILogSink"/> (the DW-304 / DW-83 idiom — NullLogSink, but recording).</summary>
        private sealed class RecordingSink : ILogSink
        {
            public readonly List<string> Infos = new List<string>();
            public readonly List<string> Warns = new List<string>();
            public void Info(string message) => Infos.Add(message);
            public void Warn(string message) => Warns.Add(message);
        }

        private const int MegaUpIdx = 0;
        private const int SmallUpIdx = 1;

        /// <summary>Per-level MaxHealth bonus of the OVER-BOUND ladder. Two levels sum to 6000 stat units, comfortably
        /// past the ≈4096 delivery bound, while each INDIVIDUAL level stays inside the ±32768 range
        /// <c>ResearchValidator</c> checks — i.e. the exact authoring shape the entry describes (every level passes
        /// validation; only the running sum outruns the bound).</summary>
        private const float MegaLevelDelta = 3000f;

        /// <summary>Per-level bonus of the IN-BOUNDS control ladder (two levels = 200 stat units, delivered exactly).</summary>
        private const float SmallLevelDelta = 100f;

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

        /// <summary>A faction with TWO two-level repeatable ladders: one whose running total blows past the delivery
        /// bound, one that never does (so the per-research breakdown can be shown to name only the offender).</summary>
        private static FactionDefinition TwoLadderFaction() => new FactionDefinition
        {
            Id = "p1",
            Buildings = new List<BuildingDefinition>
            {
                new BuildingDefinition { Id = "lab", AvailableResearch = new[] { "mega_up", "small_up" } },
            },
            Units = new List<UnitDefinition>
            {
                new UnitDefinition { Id = "worker", DisplayName = "Worker", Category = "Worker", Hp = 100f, Speed = 3f },
            },
            Research = new List<ResearchDefinition>
            {
                Ladder("mega_up", MegaLevelDelta),
                Ladder("small_up", SmallLevelDelta),
            },
        };

        private static ResearchDefinition Ladder(string id, float perLevelMaxHealth) => new ResearchDefinition
        {
            Id = id,
            Prerequisites = System.Array.Empty<string>(),
            Levels = new List<ResearchLevel>
            {
                Level(perLevelMaxHealth),
                Level(perLevelMaxHealth),
            },
        };

        private static ResearchLevel Level(float maxHealthDelta) => new ResearchLevel
        {
            Cost = new Dictionary<string, int> { { "ore", 10 } },
            TimeTicks = 1,
            ModifierDelta = new ResearchModifierDelta { MaxHealthDelta = maxHealthDelta },
        };

        private static Harness Build(ILogSink? sink)
        {
            var h = new Harness();
            var modSys = new ModifierSystem();
            h.Modifiers = new ModifierStore(h.World, modSys, null, null, null, sink);
            modSys.AttachStore(h.Modifiers);

            FactionDefinition faction = TwoLadderFaction();
            h.Sys   = new ResearchSystem(h.Buildings, h.Resources, h.Research, h.Modifiers, null, faction, null, sink);
            h.LabId = h.Buildings.Create(FixedVec3.Zero, Faction.Player1, BuildingType.Custom, buildingId: "lab");
            h.Buildings.ConstructionTimer[h.LabId] = Fixed.Zero; // operational
            h.Resources.AddOre(Faction.Player1, Fixed.FromInt(1000));
            return h;
        }

        /// <summary>Bank BOTH levels of <paramref name="researchIndex"/> with an EMPTY army. DW-623's void rule
        /// explicitly exempts an army-less completion (nothing was dropped and every FUTURE spawn receives it via
        /// the catch-up path), so the levels really are banked — and, with no living unit to walk, the completion
        /// path never builds a modifier, which keeps the tally attributable to the catch-up deliveries below.</summary>
        private static void BankBothLevels(Harness h, int researchIndex)
        {
            Assert.Empty(Alive(h.World));
            for (int level = 0; level < 2; level++)
            {
                Assert.True(h.Sys.StartResearchCommand(h.LabId, Faction.Player1, researchIndex));
                h.Sys.Tick(h.World, Fixed.Zero); // TimeTicks 1 → completes this tick
            }
            Assert.Equal(2, h.Research.CompletedLevels[(int)Faction.Player1][researchIndex]);
        }

        private static List<int> Alive(EntityWorld world)
        {
            var live = new List<int>();
            for (int i = 0; i < world.HighWaterMark; i++)
                if (world.IsAlive(i)) live.Add(i);
            return live;
        }

        /// <summary>Spawn a unit and run the catch-up hook exactly as <c>EntityWorld.OnUnitDefinitionApplied</c> does.</summary>
        private static int CatchUpSpawn(Harness h)
        {
            int id = h.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            h.Sys.ApplyCompletedResearch(h.World, id);
            return id;
        }

        private static List<string> BoundLines(RecordingSink sink) =>
            sink.Warns.Where(w => w.Contains("EXCEEDED the per-modifier delivery bound")).ToList();

        // ── The divergence itself: banked ≫ delivered ────────────────────────────────────────────

        [Fact]
        public void OverBoundLadder_BanksTheFullTotal_ButDeliversOnlyTheBound()
        {
            var sink = new RecordingSink();
            Harness h = Build(sink);
            BankBothLevels(h, MegaUpIdx);

            Fixed banked = h.Research.CumulativeMaxHealthDelta[(int)Faction.Player1][MegaUpIdx];
            Assert.Equal(Fixed.FromFloat(2 * MegaLevelDelta).Raw, banked.Raw);   // the store keeps the FULL total…
            Assert.True(banked.Raw > Modifier.MaxStatDeltaTotalRaw);             // …which is past the delivery bound

            int unit = CatchUpSpawn(h);

            // The unit received the CLAMPED delta, not the banked one — the silent divergence this entry is about.
            Fixed ceiling = h.World.EffectiveMaxHealth[unit];
            Assert.True(ceiling.Raw < Fixed.FromInt(100).Raw + banked.Raw,
                        "The unit received the full banked cumulative — DW-650's BoundedDelta clamp is gone, so this " +
                        "file's whole premise (banked ≠ delivered) no longer holds.");
            Assert.Equal(Fixed.FromInt(100).Raw + Modifier.MaxStatDeltaTotalRaw, ceiling.Raw);
        }

        // ── The report: one aggregated line naming the research ─────────────────────────────────

        [Fact]
        public void ClampedDelivery_IsReported_AtTheMatchBoundary_NamingTheResearch()
        {
            var sink = new RecordingSink();
            Harness h = Build(sink);
            BankBothLevels(h, MegaUpIdx);
            sink.Warns.Clear();

            CatchUpSpawn(h);

            Assert.Empty(BoundLines(sink)); // per-delivery silence — the whole point of the aggregation

            int flushed = h.Sys.FlushCumulativeBoundDiagnostics();

            Assert.Equal(1, flushed);
            string line = Assert.Single(BoundLines(sink));
            Assert.Contains("'mega_up'", line);          // research-NAME attribution, not a bare modifier id
            Assert.Contains("Player1", line);            // per-faction
            Assert.Contains("1 deliver(ies)", line);     // how many units were short-changed
            Assert.Contains("6000 stat unit(s)", line);  // the banked magnitude behind the clamp
            Assert.Contains("DW-751", line);
        }

        [Fact]
        public void CleanMatch_ReportsNothing_AndFlushIsIdempotent()
        {
            var sink = new RecordingSink();
            Harness h = Build(sink);
            BankBothLevels(h, SmallUpIdx); // an IN-BOUNDS ladder — delivered exactly
            sink.Warns.Clear();

            int unit = CatchUpSpawn(h);
            Assert.Equal(Fixed.FromInt(100).Raw + Fixed.FromFloat(2 * SmallLevelDelta).Raw,
                         h.World.EffectiveMaxHealth[unit].Raw); // nothing was clamped

            Assert.Equal(0, h.Sys.FlushCumulativeBoundDiagnostics());
            Assert.Equal(0, h.Sys.FlushCumulativeBoundDiagnostics()); // idempotent
            Assert.Empty(BoundLines(sink));
        }

        [Fact]
        public void OnlyTheOffendingResearch_IsNamed_WhenBothLaddersAreBanked()
        {
            var sink = new RecordingSink();
            Harness h = Build(sink);
            BankBothLevels(h, MegaUpIdx);
            BankBothLevels(h, SmallUpIdx);
            sink.Warns.Clear();

            CatchUpSpawn(h); // one spawn receives BOTH researches; only one of them clamps

            Assert.Equal(1, h.Sys.FlushCumulativeBoundDiagnostics());
            string line = Assert.Single(BoundLines(sink));
            Assert.Contains("'mega_up'", line);
            Assert.DoesNotContain("'small_up'", line);
        }

        // ── Anti-spam: the load-bearing half (one line per match, never one per affected unit) ───

        [Fact]
        public void ManyClampedDeliveries_EmitNoLine_UntilTheSingleFlush()
        {
            var sink = new RecordingSink();
            Harness h = Build(sink);
            BankBothLevels(h, MegaUpIdx);
            sink.Warns.Clear();

            for (int i = 0; i < 50; i++) CatchUpSpawn(h);

            Assert.Empty(BoundLines(sink)); // 50 clamped deliveries, ZERO lines — not 50

            Assert.Equal(50, h.Sys.FlushCumulativeBoundDiagnostics());
            string line = Assert.Single(BoundLines(sink)); // still exactly ONE line
            Assert.Contains("50 deliver(ies)", line);
        }

        [Fact]
        public void Flush_ZeroesThePerMatchTally()
        {
            var sink = new RecordingSink();
            Harness h = Build(sink);
            BankBothLevels(h, MegaUpIdx);

            CatchUpSpawn(h);
            Assert.Equal(1, h.Sys.FlushCumulativeBoundDiagnostics());
            Assert.Equal(0, h.Sys.FlushCumulativeBoundDiagnostics()); // the NEXT match starts from zero

            CatchUpSpawn(h);
            Assert.Equal(1, h.Sys.FlushCumulativeBoundDiagnostics()); // and re-arms
        }

        // ── No sink wired (goldens / Tier-1 hosts): still tallied, still silent, still byte-identical ──

        [Fact]
        public void NoSink_StillCountsButBuildsNoLine()
        {
            Harness h = Build(sink: null);
            BankBothLevels(h, MegaUpIdx);

            CatchUpSpawn(h);

            Assert.Equal(1, h.Sys.FlushCumulativeBoundDiagnostics()); // the counter is sink-independent
        }

        // ── The living-army completion path clamps and reports too (not just the spawn catch-up) ──

        [Fact]
        public void LivingArmyCompletion_PastTheBound_IsAlsoTallied()
        {
            var sink = new RecordingSink();
            Harness h = Build(sink);

            // Level 1 with an empty army banks 3000 (in bounds). Then spawn the army and complete level 2 → the
            // completion walk delivers the now-6000 cumulative to every living unit, clamped.
            Assert.True(h.Sys.StartResearchCommand(h.LabId, Faction.Player1, MegaUpIdx));
            h.Sys.Tick(h.World, Fixed.Zero);

            for (int i = 0; i < 3; i++)
                h.World.Create(FixedVec3.Zero, Faction.Player1, Fixed.FromInt(100), Fixed.FromInt(3));
            sink.Warns.Clear();

            Assert.True(h.Sys.StartResearchCommand(h.LabId, Faction.Player1, MegaUpIdx));
            h.Sys.Tick(h.World, Fixed.Zero);

            Assert.Empty(BoundLines(sink));                            // silent per unit…
            Assert.Equal(3, h.Sys.FlushCumulativeBoundDiagnostics());  // …one delivery tallied per living unit
            Assert.Contains("3 deliver(ies)", Assert.Single(BoundLines(sink)));
        }
    }
}
