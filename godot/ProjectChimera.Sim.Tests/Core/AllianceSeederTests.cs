#nullable enable
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Core
{
    /// <summary>
    /// Story 9.14 — <see cref="AllianceSeeder"/> maps a scenario's per-slot TEAM ordinals into the sim-owned
    /// <see cref="AllianceStore"/> team-id mask. These pin the load-bearing canonical-id invariant: each team's
    /// members share the LOWEST-faction-slot id in <c>[1,8]</c>, FFA degenerates to <c>TeamId[f]==f</c>, and Neutral
    /// (index 0) is never touched. Godot-free, integer-only, deterministic.
    /// </summary>
    public class AllianceSeederTests
    {
        private static ScenarioData Model(params (int slot, int team)[] slots)
        {
            var arr = new ScenarioPlayerSlot[slots.Length];
            for (int i = 0; i < slots.Length; i++)
                arr[i] = new ScenarioPlayerSlot { Slot = slots[i].slot, Team = slots[i].team };
            return new ScenarioData { PlayerSlots = arr };
        }

        // ── FFA: no teams → the byte-identical default (TeamId[f]==f) ──

        [Fact]
        public void Ffa_NoTeams_KeepsDefaultSelfTeam()
        {
            var a = new AllianceStore();
            AllianceSeeder.Seed(a, Model((0, 0), (1, 0), (2, 0), (3, 0)));
            for (int f = 0; f < FactionRegistry.SLOT_DEFINITIONS_SIZE; f++)
                Assert.Equal(f, a.TeamId[f]); // every faction its own team — no two distinct factions allied
            Assert.False(a.AreAllied(Faction.Player1, Faction.Player2));
        }

        [Fact]
        public void NullModel_And_EmptySlots_LeaveFfaDefault()
        {
            var a = new AllianceStore();
            a.TeamId[(int)Faction.Player2] = (int)Faction.Player1; // dirty it first
            AllianceSeeder.Seed(a, null); // Seed restores FFA even from a null model
            for (int f = 0; f < FactionRegistry.SLOT_DEFINITIONS_SIZE; f++) Assert.Equal(f, a.TeamId[f]);
        }

        // ── 2v2: {0,1}=teamA → canonical id 1; {2,3}=teamB → canonical id 3 ──

        [Fact]
        public void TwoVsTwo_GroupsByTeam_CanonicalIsLowestFactionSlot()
        {
            var a = new AllianceStore();
            AllianceSeeder.Seed(a, Model((0, 1), (1, 1), (2, 2), (3, 2)));

            Assert.Equal((int)Faction.Player1, a.TeamId[(int)Faction.Player1]);
            Assert.Equal((int)Faction.Player1, a.TeamId[(int)Faction.Player2]); // P2 folded into team A (id 1)
            Assert.Equal((int)Faction.Player3, a.TeamId[(int)Faction.Player3]);
            Assert.Equal((int)Faction.Player3, a.TeamId[(int)Faction.Player4]); // P4 folded into team B (id 3)

            Assert.True(a.AreAllied(Faction.Player1, Faction.Player2));
            Assert.True(a.AreAllied(Faction.Player3, Faction.Player4));
            Assert.False(a.AreAllied(Faction.Player1, Faction.Player3));
            Assert.False(a.AreAllied(Faction.Player2, Faction.Player4));
        }

        [Fact]
        public void TwoVsTwo_TeamOrdinalValues_DoNotLeakIntoIds()
        {
            // Team ordinals 5 and 9 (arbitrary authoring values) must NOT become team ids — the canonical id is a
            // faction slot, never the ordinal. This is exactly the WinConditionSystem out-of-range-drop trap.
            var a = new AllianceStore();
            AllianceSeeder.Seed(a, Model((0, 5), (1, 5), (2, 9), (3, 9)));
            Assert.Equal((int)Faction.Player1, a.TeamId[(int)Faction.Player2]); // id 1, not 5
            Assert.Equal((int)Faction.Player3, a.TeamId[(int)Faction.Player4]); // id 3, not 9
        }

        // ── 3v1: {0,1,2}=teamA → id 1; {3}=teamB → id 4 ──

        [Fact]
        public void ThreeVsOne_TeamSharesMinSlot_SoloKeepsOwnId()
        {
            var a = new AllianceStore();
            AllianceSeeder.Seed(a, Model((0, 1), (1, 1), (2, 1), (3, 2)));

            Assert.Equal((int)Faction.Player1, a.TeamId[(int)Faction.Player1]);
            Assert.Equal((int)Faction.Player1, a.TeamId[(int)Faction.Player2]);
            Assert.Equal((int)Faction.Player1, a.TeamId[(int)Faction.Player3]);
            Assert.Equal((int)Faction.Player4, a.TeamId[(int)Faction.Player4]); // solo team = its own id

            Assert.True(a.AreAllied(Faction.Player1, Faction.Player3));
            Assert.False(a.AreAllied(Faction.Player1, Faction.Player4));
        }

        // ── Invariant: every seeded id stays in [0, FACTION_COUNT); Neutral (0) untouched ──

        [Fact]
        public void AllTeamIds_StayInDomain_And_NeutralUntouched()
        {
            var a = new AllianceStore();
            AllianceSeeder.Seed(a, Model((0, 3), (1, 3), (2, 3), (3, 3), (4, 7), (5, 7), (6, 7), (7, 7)));
            for (int f = 0; f < FactionRegistry.SLOT_DEFINITIONS_SIZE; f++)
                Assert.InRange(a.TeamId[f], 0, FactionRegistry.SLOT_DEFINITIONS_SIZE - 1);
            Assert.Equal(0, a.TeamId[(int)Faction.Neutral]); // Neutral slot 0 never written

            // 4v4 layout: {0..3}=id1, {4..7}=id5.
            Assert.Equal((int)Faction.Player1, a.TeamId[(int)Faction.Player4]);
            Assert.Equal((int)Faction.Player5, a.TeamId[(int)Faction.Player8]);
        }

        [Fact]
        public void Seed_IsIdempotent_RestoresFfaBeforeReseeding()
        {
            var a = new AllianceStore();
            AllianceSeeder.Seed(a, Model((0, 1), (1, 1), (2, 2), (3, 2))); // 2v2
            AllianceSeeder.Seed(a, Model((0, 0), (1, 0), (2, 0), (3, 0))); // re-seed as FFA
            for (int f = 0; f < FactionRegistry.SLOT_DEFINITIONS_SIZE; f++)
                Assert.Equal(f, a.TeamId[f]); // no residue from the prior 2v2 seed
        }
    }
}
