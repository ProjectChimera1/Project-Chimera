#nullable enable
using System;
using System.Linq;
using ProjectChimera.AI;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Sim.Tests.Golden;
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// DW-908 — the AI must follow slot OCCUPANCY, not a hardcoded faction constant.
    ///
    /// <para><b>The live defect these pin closed.</b> During the first successful FR-39 two-machine LAN run
    /// (2026-08-07/08, PC hosting Player1 + laptop joining as Player2), the dedicated server logged 10 clean
    /// comparison windows and then <c>tick 660: GLOBAL DESYNC</c>. Cause: <c>AiOpponentSystem</c> was bound to
    /// <c>AI_FACTION = Faction.Player2</c> and ticked unconditionally, so the AI co-piloted the JOINING HUMAN's own
    /// faction — and because its scorer is float (DW-204), the two machines' AIs completed the same build at
    /// different ticks. Both halves are covered here: the ownership rule (Resolve) and the gate that makes an
    /// unowned AI a true whole-system no-op (Tick).</para>
    ///
    /// <para>Godot-free — <c>src/AI/**</c> is globbed into this assembly by <c>SimSources.props</c>.</para>
    /// </summary>
    public class AiControlPlanTests
    {
        // ── The rule: marked-AI MINUS human-occupied ──────────────────────────────────────────────

        [Fact]
        public void HumanOccupancy_Wins_OverAnAiMarkedSlot()
        {
            // THE defect, reduced to one assertion. Player2 is marked AI by the launch path AND occupied by a human
            // (online, slot 1 = an arriving peer = Player2). The AI must not get it.
            AiControlPlan plan = AiControlPlan.Resolve(
                markedAi:      new[] { Faction.Player2 },
                humanOccupied: new[] { Faction.Player1, Faction.Player2 });

            Assert.False(plan.Controls(Faction.Player2));
            Assert.False(plan.Any);
            Assert.Equal(AiControlPlan.None.Mask, plan.Mask);
        }

        [Fact]
        public void AnAiMarkedVacantSlot_IsStillPlayedByTheAi()
        {
            // The desired end state is NOT "switch the AI off". Two humans plus a genuinely vacant AI seat: the AI
            // plays it. A fix that merely disabled the AI online would fail this.
            AiControlPlan plan = AiControlPlan.Resolve(
                markedAi:      new[] { Faction.Player3 },
                humanOccupied: new[] { Faction.Player1, Faction.Player2 });

            Assert.True(plan.Controls(Faction.Player3));
            Assert.False(plan.Controls(Faction.Player1));
            Assert.False(plan.Controls(Faction.Player2));
            Assert.True(plan.Any);
        }

        [Fact]
        public void NoMarkedAi_ResolvesToNone_RegardlessOfOccupancy()
        {
            Assert.Equal(AiControlPlan.None.Mask,
                AiControlPlan.Resolve(Array.Empty<Faction>(), new[] { Faction.Player1, Faction.Player2 }).Mask);
            Assert.Equal(AiControlPlan.None.Mask, AiControlPlan.Resolve(null, null).Mask);
        }

        [Fact]
        public void Neutral_IsNeverAiControlled()
        {
            // Neutral is not a playable slot; a launch path that leaked it into the marked set must not arm the AI on
            // creeps/neutral buildings.
            Assert.False(AiControlPlan.Of(Faction.Neutral).Any);
            Assert.False(AiControlPlan.Resolve(new[] { Faction.Neutral }, null).Controls(Faction.Neutral));
        }

        // ── The two named plans ───────────────────────────────────────────────────────────────────

        [Fact]
        public void OfflineDefault_IsPlayer2Only_ThePreDw908Behaviour()
        {
            // The construction-time default: offline behaviour is UNCHANGED by DW-908, which is why no golden moves.
            Assert.True(AiControlPlan.OfflineDefault.Controls(Faction.Player2));
            Assert.False(AiControlPlan.OfflineDefault.Controls(Faction.Player1));
            for (int slot = 2; slot < FactionRegistry.PLAYER_COUNT; slot++)
                Assert.False(AiControlPlan.OfflineDefault.Controls(FactionRegistry.ToFaction(slot)));
        }

        [Fact]
        public void ForOnlineMatch_WithTodaysLobby_IsNone()
        {
            // The production online call: the lobby marks NO seat as AI (AssignedRoster models arrival order only) and
            // every active slot is occupied by a peer. This is the value MainScene folds into MatchAgreementHash and
            // MatchLifecycleController pushes into the sim.
            Faction[] roster = MatchAgreementHash.RosterFactions(BuildModel(players: 2));
            AiControlPlan plan = AiControlPlan.ForOnlineMatch(Array.Empty<Faction>(), roster);

            Assert.Equal(AiControlPlan.None.Mask, plan.Mask);
            Assert.False(plan.Controls(Faction.Player2)); // the faction the joining human occupied on the desynced run
        }

        [Fact]
        public void Mask_IsOrderFree_SoTwoPeersFoldTheSameValue()
        {
            // The mask is what the handshake folds, so it must not depend on the ORDER the launch path enumerated the
            // slots in — otherwise two peers with the same plan could still mismatch at the gate.
            Assert.Equal(
                AiControlPlan.Of(Faction.Player2, Faction.Player4).Mask,
                AiControlPlan.Of(Faction.Player4, Faction.Player2).Mask);
            Assert.Equal(
                AiControlPlan.Resolve(new[] { Faction.Player3, Faction.Player2 }, null).Mask,
                AiControlPlan.Resolve(new[] { Faction.Player2, Faction.Player3 }, null).Mask);
        }

        // ── The gate: an unowned AI is a whole-system no-op ────────────────────────────────────────

        [Fact]
        public void DisarmedAi_BuildsNothing_SpendsNothing_CommandsNothing()
        {
            // AiActiveScenario is the fixture built precisely so the AI ACTS (300 ore, a 5-unit idle wave): under the
            // default plan it razes on tick 1 and builds a Barracks on tick 2. With the online plan it must do
            // literally nothing — no building, no ore spend, no orders. Those three are the writes that both
            // co-piloted the human's faction AND fed the float scorer that desynced the LAN gate.
            GoldenHarness armed = AiActiveScenario.Build();
            GoldenHarness disarmed = AiActiveScenario.Build();
            disarmed.Host.SetAiControlPlan(AiControlPlan.None);

            Fixed oreBefore = disarmed.Resources.Ore[(int)Faction.Player2];
            int buildingsBefore = AliveBuildings(disarmed, Faction.Player2);

            for (int i = 0; i < 60; i++) { armed.Host.StepOnce(); disarmed.Host.StepOnce(); }

            // The disarmed run is inert…
            Assert.Equal(oreBefore.Raw, disarmed.Resources.Ore[(int)Faction.Player2].Raw);
            Assert.Equal(buildingsBefore, AliveBuildings(disarmed, Faction.Player2));
            Assert.All(P2Units(disarmed), u => Assert.Equal(UnitCommand.Idle, disarmed.World.CommandState[u]));

            // …and the armed run is NOT, so the assertions above are a real gate and not a dead fixture.
            Assert.True(AliveBuildings(armed, Faction.Player2) > buildingsBefore,
                "the armed AI must still build — otherwise the disarmed assertions above prove nothing.");
        }

        [Fact]
        public void DisarmedAi_MovesTheChecksumStream_TheDesyncItPrevents()
        {
            // The determinism statement of the same fact: an AI that runs on one peer and not the other produces a
            // DIFFERENT folded SimChecksum. That is why the plan must be handshake-agreed (MatchAgreementHash v4) and
            // not merely a local toggle — and it is why the two LAN machines diverged.
            GoldenHarness armed = AiActiveScenario.Build();
            GoldenHarness disarmed = AiActiveScenario.Build();
            disarmed.Host.SetAiControlPlan(AiControlPlan.None);

            for (int i = 0; i < 60; i++) { armed.Host.StepOnce(); disarmed.Host.StepOnce(); }

            Assert.NotEqual(armed.Host.LastChecksum, disarmed.Host.LastChecksum);
        }

        [Fact]
        public void DefaultPlan_IsTheOfflinePairing_SoNoGoldenMoves()
        {
            // Every golden, every Tier-1 fixture and the headless server build a host WITHOUT touching the plan. Pin
            // that the untouched default is the pre-DW-908 behaviour — this is the assertion standing between this
            // change and a 25-file golden re-record.
            GoldenHarness h = AiActiveScenario.Build();

            Assert.Equal(AiControlPlan.OfflineDefault.Mask, h.Host.Ai.ControlPlan.Mask);
            Assert.True(h.Host.Ai.IsActive);
        }

        // ── The reset seam: ClearForReset must NOT re-arm the AI on a human's faction ───────────────

        [Fact]
        public void ClearForReset_PreservesTheControlPlan()
        {
            // The online entry path reaches SimulationHost.ClearForReset AFTER OnMatchStart established the plan. If
            // AiOpponentSystem.ResetForMatch cleared it, the AI would be re-armed on the joining human's faction on
            // the very transition this fix exists to prevent — a silent regression that no offline test would catch.
            GoldenHarness h = AiActiveScenario.Build();
            h.Host.SetAiControlPlan(AiControlPlan.None);

            h.Host.ClearForReset();

            Assert.Equal(AiControlPlan.None.Mask, h.Host.Ai.ControlPlan.Mask);
            Assert.False(h.Host.Ai.IsActive);
        }

        // ── helpers ────────────────────────────────────────────────────────────────────────────────

        private static int AliveBuildings(GoldenHarness h, Faction f)
        {
            int n = 0;
            for (int i = 0; i < BuildingStore.MAX_BUILDINGS; i++)
                if (h.Buildings.Alive[i] && h.Buildings.FactionOf[i] == f) n++;
            return n;
        }

        private static System.Collections.Generic.IEnumerable<int> P2Units(GoldenHarness h)
            => Enumerable.Range(0, EntityWorld.MAX_ENTITIES)
                         .Where(i => h.World.IsAlive(i) && h.World.FactionOf[i] == Faction.Player2);

        /// <summary>A minimal N-player model — only <c>PlayerSlots</c> is read by <c>RosterFactions</c>.</summary>
        private static ScenarioData BuildModel(int players)
        {
            var slots = new ScenarioPlayerSlot[players];
            for (int i = 0; i < players; i++) slots[i] = new ScenarioPlayerSlot { Slot = i };
            return new ScenarioData { PlayerSlots = slots };
        }
    }
}
