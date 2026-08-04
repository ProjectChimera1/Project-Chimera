#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core;              // ScenarioDirector, WinStateStore, FactionRegistry, Faction, Fixed
using ProjectChimera.Core.Definitions;  // ScenarioData, TriggerDefinition, ScenarioValidator, Validated
using ProjectChimera.Core.Sim;          // SimulationHost, ScenarioApplier, NullLogSink, SimulationLoop
using ProjectChimera.Economy;           // BuildingStore
using ProjectChimera.Dsl;               // DslVarTable
using Xunit;

namespace ProjectChimera.Sim.Tests.WinConditions
{
    /// <summary>
    /// DW-189 / DW-383 — the DSL <c>victory</c>/<c>defeat</c> escape hatch resolves N-player-safely.
    ///
    /// <para>The defect: <c>ScenarioDirector.ExecuteLeaf</c>'s <c>defeat</c> arm computed the winner as
    /// <c>OnVictory?.Invoke(1 - a.Faction)</c> — "the other faction wins", a complement that is only meaningful for
    /// faction slots 0/1. In a 3–8-faction map an authored <c>defeat</c> on slot 2 handed presentation
    /// <c>1 - 2 == -1</c> (and slot 7 → <c>-6</c>), which flowed into <c>ShowGameOver(winnerSlot + 1)</c> as a
    /// nonsensical/zero/negative winner. Even at slot 1 the complement is a GUESS: with three factions alive,
    /// "P2 lost" does not make P1 the winner.</para>
    ///
    /// <para>The fix (recorded decision 2026-08-02, WC3-shaped): <c>victory</c> and <c>defeat</c> are INDEPENDENT
    /// per-player declarations. <c>defeat</c> now latches only its OWN faction's
    /// <see cref="WinStateStore.VERDICT_LOST"/> on the folded verdict rail — the same store
    /// <see cref="WinConditionSystem"/> and the Concede order write — and the built-in N-faction, team-aware
    /// resolver decides whether anybody has won. <c>victory</c> still names its winner explicitly (no arithmetic),
    /// so that half is unchanged and pinned here as a contract.</para>
    ///
    /// <para>Godot-free: pure <c>ScenarioDirector</c> + <c>SimulationHost</c> (NullLogSink), integer/Fixed only.</para>
    /// </summary>
    public class DslDefeatNPlayerTests
    {
        private static readonly Fixed Dt = SimulationLoop.FixedDt;

        private const int WON  = WinStateStore.VERDICT_WON;
        private const int LOST = WinStateStore.VERDICT_LOST;
        private const int NONE = WinStateStore.VERDICT_NONE;

        // ── Pure director drive (no host): one match_start trigger whose single action is victory/defeat ─────────

        /// <summary>A scenario with exactly one always-on <c>match_start</c> trigger firing
        /// <paramref name="actionType"/> at the 0-based <paramref name="slot"/>.</summary>
        private static ScenarioData OneActionScenario(string actionType, int slot) => new ScenarioData
        {
            Triggers = new[]
            {
                new TriggerDefinition
                {
                    Name       = actionType,
                    Enabled    = true,
                    Events     = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                    Conditions = Array.Empty<TriggerCondition>(),
                    Actions    = new[] { new TriggerAction { Type = actionType, Faction = slot } },
                },
            },
        };

        /// <summary>Drive a fresh director for ONE tick with a single <paramref name="actionType"/> action at
        /// <paramref name="slot"/>, capturing every <c>OnVictory</c> argument it emitted.</summary>
        private static List<int> RunOneAction(string actionType, int slot,
                                              WinStateStore? win, FactionRegistry? factions)
        {
            var director = new ScenarioDirector(new BuildingStore(), new ResourceStore(Fixed.Zero),
                                                new DslVarTable(), factions: factions);
            director.SetWinState(win);
            var seen = new List<int>();
            director.OnVictory = s => seen.Add(s);
            director.LoadScenario(OneActionScenario(actionType, slot));
            director.Tick(new EntityWorld(), Dt);
            return seen;
        }

        /// <summary>Every real faction index except <paramref name="except"/> must still read
        /// <see cref="WinStateStore.VERDICT_NONE"/> — a per-player declaration touches exactly one seat.</summary>
        private static void AssertOnlyLatched(WinStateStore win, int exceptIndex, int expectedVerdict)
        {
            Assert.Equal(expectedVerdict, win.Verdict[exceptIndex]);
            for (int f = 0; f < win.Verdict.Length; f++)
                if (f != exceptIndex)
                    Assert.Equal(NONE, win.Verdict[f]);
        }

        // ── The headline regression: a defeat latches ONE loser and derives no winner ────────────────────────────

        /// <summary>
        /// DW-189/DW-383 core: at EVERY engine slot (0-7) an authored <c>defeat</c> latches exactly that faction's
        /// VERDICT_LOST and nothing else. Pre-fix the leaf latched no verdict at all (it only fired the complement
        /// delegate), so this fails at slot 0 already — and the whole point is that it now holds identically at the
        /// slots the complement could not express.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        public void Defeat_LatchesLostForTheAuthoredFactionOnly(int slot)
        {
            var win = new WinStateStore();
            RunOneAction("defeat", slot, win, new FactionRegistry(8));

            AssertOnlyLatched(win, (int)FactionRegistry.ToFaction(slot), LOST);
        }

        /// <summary>
        /// The exact defect text: <c>defeat</c> must NEVER hand presentation a DERIVED winner slot. Pre-fix every
        /// slot emitted <c>1 - slot</c> — a bogus winner at slots 0/1 and a NEGATIVE slot (-1 … -6) from slot 2 up,
        /// which <c>ShowGameOver(winnerSlot + 1)</c> renders as a zero/negative player number.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        public void Defeat_NeverInvokesOnVictoryWithADerivedWinnerSlot(int slot)
        {
            List<int> seen = RunOneAction("defeat", slot, new WinStateStore(), new FactionRegistry(8));

            Assert.Empty(seen); // a defeat declares a LOSER; the winner is the resolver's job
        }

        /// <summary>
        /// The ledger's literal evidence, isolated: a <c>defeat</c> on faction slot 2 in a 4-faction match once
        /// produced winner slot <c>1 - 2 == -1</c>. Assert no emitted winner is negative AND that the loser really
        /// latched, so the test cannot pass by the leaf simply doing nothing.
        /// </summary>
        [Fact]
        public void Defeat_AtSlotTwo_ProducesNoNegativeWinnerSlot_AndStillLatchesTheLoser()
        {
            var win = new WinStateStore();
            List<int> seen = RunOneAction("defeat", 2, win, new FactionRegistry(4));

            Assert.All(seen, s => Assert.True(s >= 0, $"OnVictory received a negative winner slot ({s})."));
            Assert.Equal(LOST, win.Verdict[(int)Faction.Player3]);
        }

        /// <summary>Monotone latch (the store's own never-overwrite rule): a re-fired defeat, and a defeat aimed at
        /// a faction the resolver already decided, are deterministic no-ops. A winner is never flipped to a loser.</summary>
        [Fact]
        public void Defeat_IsMonotone_NeverOverwritesAnExistingVerdict()
        {
            var win = new WinStateStore();
            win.Verdict[(int)Faction.Player3] = WON;
            RunOneAction("defeat", 2, win, new FactionRegistry(4));
            Assert.Equal(WON, win.Verdict[(int)Faction.Player3]);

            var win2 = new WinStateStore();
            win2.Verdict[(int)Faction.Player1] = LOST;
            RunOneAction("defeat", 0, win2, new FactionRegistry(4));
            Assert.Equal(LOST, win2.Verdict[(int)Faction.Player1]);
        }

        /// <summary>No win state wired (goldens / headless / replay-without-win-state, and every direct test
        /// construction): the leaf is a deterministic no-op — no throw, and still no derived winner.</summary>
        [Fact]
        public void Defeat_WithNoWinStateWired_IsADeterministicNoOp()
        {
            List<int> seen = RunOneAction("defeat", 3, win: null, factions: new FactionRegistry(8));
            Assert.Empty(seen);
        }

        /// <summary>A slot outside the match's ACTIVE faction span is dropped rather than latching state
        /// <c>SimChecksum</c> (which folds Verdict per ACTIVE faction) could not see.</summary>
        [Fact]
        public void Defeat_SlotOutsideTheActiveSpan_IsDropped()
        {
            var win = new WinStateStore();
            RunOneAction("defeat", 5, win, new FactionRegistry(2)); // only P1/P2 active
            for (int f = 0; f < win.Verdict.Length; f++)
                Assert.Equal(NONE, win.Verdict[f]);
        }

        // ── The unchanged half: victory NAMES its winner (no complement), at every slot ──────────────────────────

        /// <summary>
        /// Contract pin for the half DW-189/DW-383 did NOT change: <c>victory</c> carries no faction arithmetic, so
        /// it already reports the AUTHORED slot at every engine seat (slot 5 declares P6 the winner, never "the
        /// other one"), and it latches no verdict of its own — presentation keeps consuming it via the delegate.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(5)]
        [InlineData(7)]
        public void Victory_ReportsTheAuthoredSlotVerbatim(int slot)
        {
            var win = new WinStateStore();
            List<int> seen = RunOneAction("victory", slot, win, new FactionRegistry(8));

            Assert.Equal(new[] { slot }, seen);
            for (int f = 0; f < win.Verdict.Length; f++)
                Assert.Equal(NONE, win.Verdict[f]);
        }

        // ── Through a REAL host: the SimulationHost wiring + the built-in resolver own the winner ────────────────

        private static UnitDefinition Grunt() => new UnitDefinition
        {
            Id = "grunt", DisplayName = "Grunt", Category = "Ranged",
            Hp = 50f, Speed = 3.5f, VisionRange = 7f, AttackRange = 2f, AttackDamage = 4f,
            AttackSpeed = 1.5f, Supply = 1, DamageType = "Pierce", ArmorType = "Light",
        };

        /// <summary>A host + applier for <paramref name="players"/> active factions. NOTE: the test NEVER calls
        /// <c>SetWinState</c> — reaching a latched verdict here proves <c>SimulationHost</c> wired the folded store
        /// into the director itself.</summary>
        private static (SimulationHost host, ScenarioApplier applier) NewHostAndApplier(int players)
        {
            var faction = new FactionDefinition { Id = "alpha", DisplayName = "Alpha", Units = { Grunt() } };
            var slotDefs = new FactionDefinition?[FactionRegistry.SLOT_DEFINITIONS_SIZE];
            for (int i = 0; i < players; i++)
                slotDefs[(int)FactionRegistry.ToFaction(i)] = faction;
            var host = SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(players), faction, faction);
            var applier = new ScenarioApplier(host, NullLogSink.Instance, slotDefs);
            return (host, applier);
        }

        /// <summary>A validated N-player scenario: one live grunt per slot (so nobody is symmetric-eliminated —
        /// every verdict must come from the triggers) plus one <c>match_start → defeat</c> trigger per named slot.
        /// Validating here ALSO proves the recorded decision's "do NOT reject slot ≥ 2 at load" half: a
        /// trigger-authored map may declare a defeat for any engine seat.</summary>
        private static Validated<ScenarioData> ValidatedWithDefeats(int players, params int[] defeatSlots)
        {
            ScenarioData s = ScenarioData.CreateBlank("dsl-defeat", suggestedPlayers: players);
            var units = new ScenarioUnit[players];
            for (int i = 0; i < players; i++)
                units[i] = new ScenarioUnit { UnitId = "grunt", Slot = i, X = -20f + 10f * i, Z = 0f };
            s.Units = units;

            var triggers = new TriggerDefinition[defeatSlots.Length];
            for (int i = 0; i < defeatSlots.Length; i++)
                triggers[i] = new TriggerDefinition
                {
                    Name       = $"defeat-{defeatSlots[i]}",
                    Enabled    = true,
                    Events     = new[] { new TriggerEvent { Type = "match_start", Faction = 0 } },
                    Conditions = Array.Empty<TriggerCondition>(),
                    Actions    = new[] { new TriggerAction { Type = "defeat", Faction = defeatSlots[i] } },
                };
            s.Triggers = triggers;

            ValidationResult r = new ScenarioValidator().Validate(s);
            Assert.True(r.Ok, r.Error);
            return r.Value;
        }

        /// <summary>
        /// 1v1 PARITY: the outcome the old complement produced ("the other faction wins") is preserved — but it is
        /// now DERIVED by the N-faction resolver from the latched loser, one tick later, instead of guessed by the
        /// leaf. Pre-fix P1 never latched LOST and P2 never latched WON (the old path only poked presentation).
        /// </summary>
        [Fact]
        public void Defeat_1v1_ResolvesTheOpponentWon_ThroughTheWinConditionTick()
        {
            var (host, applier) = NewHostAndApplier(2);
            applier.Apply(ValidatedWithDefeats(2, 0));

            // Canonical tick order: WinConditionSystem (index 14) then ScenarioDirector (index 15).
            host.WinCon.Tick(host.World, Dt);
            Assert.Equal(0, host.WinState.WinnerFaction()); // nothing decided before the trigger runs

            host.ScenarioDirector.Tick(host.World, Dt);      // match_start → defeat(slot 0)
            Assert.Equal(LOST, host.WinState.Verdict[(int)Faction.Player1]);
            Assert.Equal(NONE, host.WinState.Verdict[(int)Faction.Player2]); // the leaf declared no winner

            host.WinCon.Tick(host.World, Dt);                // last team standing awards the survivor
            Assert.Equal((int)Faction.Player2, host.WinState.WinnerFaction());
            Assert.Equal(WON, host.WinState.Verdict[(int)Faction.Player2]);
            Assert.True(host.WinCon.IsFullyResolved());
        }

        /// <summary>
        /// The N-player rule the complement could not express: in a 3-faction FFA a single authored <c>defeat</c>
        /// on slot 2 (Player3) eliminates ONLY Player3 and declares NO winner — two factions are still live, so the
        /// match continues. Pre-fix the same map immediately reported winner slot <c>1 - 2 == -1</c>.
        /// </summary>
        [Fact]
        public void Defeat_ThreeFfa_AtSlot2_EliminatesOnlyThatFaction_AndDeclaresNoWinner()
        {
            var (host, applier) = NewHostAndApplier(3);
            applier.Apply(ValidatedWithDefeats(3, 2));

            var seen = new List<int>();
            host.ScenarioDirector.OnVictory = s => seen.Add(s);

            host.WinCon.Tick(host.World, Dt);
            host.ScenarioDirector.Tick(host.World, Dt);
            host.WinCon.Tick(host.World, Dt);

            Assert.Empty(seen);                                                  // no guessed winner
            Assert.Equal(LOST, host.WinState.Verdict[(int)Faction.Player3]);
            Assert.Equal(NONE, host.WinState.Verdict[(int)Faction.Player1]);
            Assert.Equal(NONE, host.WinState.Verdict[(int)Faction.Player2]);
            Assert.Equal(0, host.WinState.WinnerFaction());
            Assert.False(host.WinCon.IsFullyResolved());
        }

        /// <summary>
        /// …and once the authored defeats leave exactly one faction standing, the resolver awards the REAL survivor.
        /// Defeating slots 1 and 2 in a 3-faction match must crown Player1 — a winner the 2-faction complement
        /// could never have computed from either loser.
        /// </summary>
        [Fact]
        public void Defeat_ThreeFfa_TwoAuthoredDefeats_AwardTheRealSurvivor()
        {
            var (host, applier) = NewHostAndApplier(3);
            applier.Apply(ValidatedWithDefeats(3, 1, 2));

            host.WinCon.Tick(host.World, Dt);
            host.ScenarioDirector.Tick(host.World, Dt);   // both defeats fire on the same match_start pass
            Assert.Equal(LOST, host.WinState.Verdict[(int)Faction.Player2]);
            Assert.Equal(LOST, host.WinState.Verdict[(int)Faction.Player3]);

            host.WinCon.Tick(host.World, Dt);
            Assert.Equal((int)Faction.Player1, host.WinState.WinnerFaction());
            Assert.Equal(WON, host.WinState.Verdict[(int)Faction.Player1]);
            Assert.True(host.WinCon.IsFullyResolved());
        }
    }
}
