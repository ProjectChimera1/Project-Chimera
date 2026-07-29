#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;              // EntityWorld, Faction, Fixed, WinStateStore, FactionRegistry, MatchStats
using ProjectChimera.Core.Definitions;  // ScenarioData, ScenarioUnit, FactionDefinition, UnitDefinition, ScenarioValidator, Validated
using ProjectChimera.Core.Sim;          // SimulationHost, ScenarioApplier, NullLogSink, SimulationLoop
using ProjectChimera.Multiplayer;       // OrderApplier, UnitOrder
using Xunit;

namespace ProjectChimera.Sim.Tests.WinConditions
{
    /// <summary>
    /// Story 11.2 (FR-66) — the Concede/surrender wire command resolves through the EXISTING folded
    /// <see cref="WinStateStore.Verdict"/> + <see cref="WinConditionSystem"/> last-team-standing, with NO new folded
    /// store and NO golden re-baseline. Covers: the monotone LOST latch, the null-handle deterministic no-op, wrong-
    /// faction isolation, Neutral drop, a 1v1 resolution through a real host tick, and the determinism proof that
    /// mutating the (unfolded) score counters — and the dormant Concede enum value — moves no SimChecksum sample.
    /// </summary>
    public class ConcedeCommandTests
    {
        private static readonly Fixed Dt = SimulationLoop.FixedDt;

        private static UnitOrder ConcedeOrder() => new UnitOrder(0, UnitCommand.Concede, Fixed.Zero, Fixed.Zero);

        // ── Pure latch behavior (OrderApplier + WinStateStore, no host) ──────────────────────────────

        [Fact]
        public void Concede_LatchesLostForExpectedFactionOnly()
        {
            var world = new EntityWorld();
            var win = new WinStateStore();

            OrderApplier.Apply(world, ConcedeOrder(), Faction.Player1, winState: win);

            Assert.Equal(WinStateStore.VERDICT_LOST, win.Verdict[(int)Faction.Player1]);
            // Anti-cheat truth: only the command's own faction is latched — no cross-faction concede.
            Assert.Equal(WinStateStore.VERDICT_NONE, win.Verdict[(int)Faction.Player2]);
            Assert.Equal(WinStateStore.VERDICT_NONE, win.Verdict[(int)Faction.Player3]);
        }

        [Fact]
        public void Concede_IsMonotone_NeverOverwritesAnExistingVerdict()
        {
            var world = new EntityWorld();
            var win = new WinStateStore();

            // Already-LOST → a re-concede is a deterministic no-op (stays LOST).
            win.Verdict[(int)Faction.Player1] = WinStateStore.VERDICT_LOST;
            OrderApplier.Apply(world, ConcedeOrder(), Faction.Player1, winState: win);
            Assert.Equal(WinStateStore.VERDICT_LOST, win.Verdict[(int)Faction.Player1]);

            // Already-WON → concede must NOT flip a winner to LOST (only NONE is latchable).
            win.Verdict[(int)Faction.Player2] = WinStateStore.VERDICT_WON;
            OrderApplier.Apply(world, ConcedeOrder(), Faction.Player2, winState: win);
            Assert.Equal(WinStateStore.VERDICT_WON, win.Verdict[(int)Faction.Player2]);
        }

        [Fact]
        public void Concede_NullWinState_IsDeterministicNoOp()
        {
            var world = new EntityWorld();
            // No WinStateStore threaded (golden/headless/replay-without-win-state) → no throw, nothing to observe.
            OrderApplier.Apply(world, ConcedeOrder(), Faction.Player1, winState: null);
            // Reaching here without an exception is the assertion.
            Assert.True(true);
        }

        [Fact]
        public void Concede_NeutralFaction_IsDropped()
        {
            var world = new EntityWorld();
            var win = new WinStateStore();
            OrderApplier.Apply(world, ConcedeOrder(), Faction.Neutral, winState: win);
            Assert.Equal(WinStateStore.VERDICT_NONE, win.Verdict[(int)Faction.Neutral]);
        }

        // ── 1v1 resolution through a real host + WinConditionSystem tick ─────────────────────────────

        private static UnitDefinition Grunt() => new UnitDefinition
        {
            Id = "grunt", DisplayName = "Grunt", Category = "Ranged",
            Hp = 50f, Speed = 3.5f, VisionRange = 7f, AttackRange = 2f, AttackDamage = 4f,
            AttackSpeed = 1.5f, Supply = 1, DamageType = "Pierce", ArmorType = "Light",
        };

        private static (SimulationHost host, ScenarioApplier applier) NewHostAndApplier()
        {
            var faction = new FactionDefinition { Id = "alpha", DisplayName = "Alpha", Units = { Grunt() } };
            var slotDefs = new FactionDefinition?[5];
            slotDefs[(int)Faction.Player1] = faction;
            slotDefs[(int)Faction.Player2] = faction;
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), faction, faction);
            var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);
            return (host, applier);
        }

        private static Validated<ScenarioData> ValidatedTwoPlayer()
        {
            var s = ScenarioData.CreateBlank("concede-1v1", suggestedPlayers: 2);
            // A live unit for BOTH factions so neither is symmetric-eliminated — the win must come from the concede.
            s.Units = new[]
            {
                new ScenarioUnit { UnitId = "grunt", Slot = 0, X = -5, Z = 0 }, // P1
                new ScenarioUnit { UnitId = "grunt", Slot = 1, X =  5, Z = 0 }, // P2
            };
            ValidationResult r = new ScenarioValidator().Validate(s);
            Assert.True(r.Ok, r.Error);
            return r.Value;
        }

        [Fact]
        public void Concede_1v1_ResolvesOpponentWon_ThroughWinConditionTick()
        {
            var (host, applier) = NewHostAndApplier();
            applier.Apply(ValidatedTwoPlayer());

            // Both alive → no winner yet.
            host.WinCon.Tick(host.World, Dt);
            Assert.Equal(0, host.WinState.WinnerFaction());

            // P1 concedes through the SHARED applier onto the host's folded WinStateStore.
            OrderApplier.Apply(host.World, ConcedeOrder(), Faction.Player1, winState: host.WinState);
            Assert.Equal(WinStateStore.VERDICT_LOST, host.WinState.Verdict[(int)Faction.Player1]);

            // The very next WinConditionSystem tick awards the sole remaining live team (P2) — last team standing.
            host.WinCon.Tick(host.World, Dt);
            Assert.Equal((int)Faction.Player2, host.WinState.WinnerFaction());
            Assert.Equal(WinStateStore.VERDICT_WON, host.WinState.Verdict[(int)Faction.Player2]);
        }

        [Fact]
        public void Concede_1v1_KotH_ResolvesOpponentWon_AndFullyResolved()
        {
            // Fix #1 — under King-of-the-Hill the resolver used to `return` BEFORE last-team-standing, so a CONCEDE
            // (the only way a KotH faction latches LOST short of the hold-win) dead-ended the match forever. With the
            // fall-through fix, a KotH concede resolves the survivor exactly like the built-in preset.
            var (host, applier) = NewHostAndApplier();
            var s = ScenarioData.CreateBlank("concede-koth", suggestedPlayers: 2);
            s.Regions = new[] { new ScenarioRegion { Id = "zone", Name = "Zone", MinX = -5, MinZ = -5, MaxX = 5, MaxZ = 5 } };
            // Both factions sit INSIDE the zone → contested every tick → the hold counter never advances, so a huge
            // hold_ticks guarantees NO hold-win can pre-empt the concede resolution.
            s.Units = new[]
            {
                new ScenarioUnit { UnitId = "grunt", Slot = 0, X = 0, Z = 0 }, // P1 in the zone
                new ScenarioUnit { UnitId = "grunt", Slot = 1, X = 1, Z = 0 }, // P2 in the zone
            };
            s.WinConditionSpec = new WinConditionSpec { Preset = WinPresetKind.KingOfTheHill, RegionId = "zone", HoldTicks = 100000 };
            ValidationResult r = new ScenarioValidator().Validate(s);
            Assert.True(r.Ok, r.Error);
            applier.Apply(r.Value);

            // No hold-win (contested + huge hold_ticks), no concede yet → unresolved.
            host.WinCon.Tick(host.World, Dt);
            Assert.False(host.WinState.IsResolved());

            // P1 concedes → the very next KotH tick falls through to last-team-standing and awards P2.
            OrderApplier.Apply(host.World, ConcedeOrder(), Faction.Player1, winState: host.WinState);
            host.WinCon.Tick(host.World, Dt);

            Assert.Equal((int)Faction.Player2, host.WinState.WinnerFaction());
            Assert.Equal(WinStateStore.VERDICT_WON, host.WinState.Verdict[(int)Faction.Player2]);
            Assert.True(host.WinCon.IsFullyResolved());
        }

        // ── Determinism: the unfolded counters + dormant Concede enum move no SimChecksum sample ─────

        [Fact]
        public void UnfoldedCounters_AndDormantConcede_LeaveTheSimChecksumStreamUnchanged()
        {
            List<uint> Run(bool pokeCounters)
            {
                var (host, applier) = NewHostAndApplier();
                applier.Apply(ValidatedTwoPlayer());
                host.ChecksumInterval = 1;
                var seq = new List<uint>();
                host.SetChecksumSink((tick, hash) => seq.Add(hash));
                for (int t = 0; t < 60; t++)
                {
                    // Mutating the OBSERVATIONAL (unfolded) MatchStats mid-run must not perturb the folded checksum.
                    if (pokeCounters)
                    {
                        host.MatchStats.RecordCrystalMined(Faction.Player1, Fixed.FromInt(3));
                        host.MatchStats.RecordBuildingRazed(Faction.Player2);
                    }
                    host.StepOnce();
                }
                return seq;
            }

            List<uint> baseline = Run(pokeCounters: false);
            List<uint> poked    = Run(pokeCounters: true);

            Assert.Equal(baseline, poked); // byte-identical stream → MatchStats is unfolded; no golden moves
        }
    }
}
