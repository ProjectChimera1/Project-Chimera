#nullable enable
using System.Linq;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// DW-442 — scenario TEAM authoring feedback. Before this, <c>ScenarioValidator</c>'s player-slots loop validated
    /// <c>slot</c> / ore / coords / uniqueness and never looked at <c>team</c> at all, and
    /// <c>AllianceSeeder.ComputeTeamIds</c> silently dropped an out-of-range member and was silently last-write-wins on
    /// a duplicated <c>.Slot</c> — so four degenerate layouts shipped with zero author-facing feedback.
    ///
    /// <para>The split these pin: a NEGATIVE ordinal is malformed and fails the load gate closed; the degenerate but
    /// well-formed POSITIVE layouts (all slots one team, inert team-of-one) are NON-FATAL advisories, because a
    /// trigger-driven co-op map whose enemies spawn on an undeclared faction slot is legitimately all-allied among its
    /// declared slots. Godot-free, integer-only, deterministic.</para>
    /// </summary>
    public class ScenarioTeamValidationTests
    {
        /// <summary>A loadable N-slot map; <paramref name="teams"/> supplies each slot's team ordinal in order.</summary>
        private static ScenarioData MapWithTeams(params int[] teams)
        {
            var slots = new ScenarioPlayerSlot[teams.Length];
            for (int i = 0; i < teams.Length; i++)
                slots[i] = new ScenarioPlayerSlot
                {
                    Slot = i, FactionJson = "res://a.json", StartOre = 200f,
                    BaseX = -50f + i * 20f, BaseZ = 0f, Team = teams[i],
                };
            return new ScenarioData
            {
                Id = "m", DisplayName = "Map", MapBounds = 120f,
                WinCondition = WinCondition.DestroyAllBuildings,
                PlayerSlots   = slots,
                ResourceNodes = System.Array.Empty<ScenarioResourceNode>(),
                Buildings     = System.Array.Empty<ScenarioBuilding>(),
                Units         = System.Array.Empty<ScenarioUnit>(),
                Triggers      = System.Array.Empty<TriggerDefinition>(),
            };
        }

        private static ScenarioData ModelOf(params (int slot, int team)[] entries)
        {
            var arr = new ScenarioPlayerSlot[entries.Length];
            for (int i = 0; i < entries.Length; i++)
                arr[i] = new ScenarioPlayerSlot { Slot = entries[i].slot, Team = entries[i].team };
            return new ScenarioData { PlayerSlots = arr };
        }

        // ── HARD gate: a negative ordinal is malformed and fails closed ──────────────────────────────────

        [Fact]
        public void NegativeTeam_FailsClosed_Located()
        {
            var m = MapWithTeams(1, -1);

            ValidationResult r = new ScenarioValidator().Validate(m);

            Assert.False(r.Ok);
            Assert.Contains("player_slots[1].team=-1", r.Error);
            Assert.Contains("must be >= 0", r.Error);
        }

        [Fact]
        public void NegativeTeam_IsRejected_EvenThoughTheSeederWouldTreatItAsFfa()
        {
            // The defect the gate closes: the seeder maps `team <= 0` to UNASSIGNED, so -1 and -2 seed the SAME
            // (byte-identical FFA) mask while the two files differ — a match-agreement mismatch over nothing.
            var a = new AllianceStore();
            var b = new AllianceStore();
            AllianceSeeder.Seed(a, ModelOf((0, -1), (1, -1)));
            AllianceSeeder.Seed(b, ModelOf((0, -2), (1, -2)));
            for (int f = 0; f < FactionRegistry.SLOT_DEFINITIONS_SIZE; f++) Assert.Equal(a.TeamId[f], b.TeamId[f]);

            Assert.False(new ScenarioValidator().Validate(MapWithTeams(-1, -2)).Ok);
        }

        [Fact]
        public void ZeroTeam_FfaMap_StillPasses_WithNoTeamAdvisory()
        {
            var m = MapWithTeams(0, 0, 0, 0);

            Assert.True(new ScenarioValidator().Validate(m).Ok);
            Assert.Empty(new ScenarioValidator().CollectAdvisories(m));
        }

        [Fact]
        public void PositiveOrdinalsAreNotRangeCapped_ArbitraryLabelsStillLoad()
        {
            // Ordinals are authoring labels, never sim values (AllianceSeederTests pins 5/9 → canonical ids 1/3), so a
            // well-formed 2v2 using far-apart ordinals must still LOAD — the gate rejects only a negative ordinal.
            var m = MapWithTeams(5, 5, 9, 9);

            Assert.True(new ScenarioValidator().Validate(m).Ok);
            Assert.Empty(new ScenarioValidator().CollectAdvisories(m));
        }

        // ── SOFT advisories: the degenerate-but-loadable layouts ────────────────────────────────────────

        [Fact]
        public void AllSlotsOnOneTeam_IsAdvisory_NotFatal()
        {
            var m = MapWithTeams(1, 1, 1, 1);

            Assert.True(new ScenarioValidator().Validate(m).Ok); // loadable: enemies may arrive from triggers
            Assert.Contains(new ScenarioValidator().CollectAdvisories(m),
                            a => a.Contains("All 4 start positions are on the same team"));
        }

        [Fact]
        public void AllSlotsOnOneTeam_IsExactlyTheAllAlliedMaskTheSimSeeds()
        {
            // The advisory's premise, proven against the production mapping: every pair really is allied, so nothing
            // can auto-acquire, force-fire or be eliminated.
            var a = new AllianceStore();
            AllianceSeeder.Seed(a, MapWithTeams(1, 1, 1, 1));
            Assert.True(a.AreAllied(Faction.Player1, Faction.Player2));
            Assert.True(a.AreAllied(Faction.Player1, Faction.Player4));
            Assert.True(a.AreAllied(Faction.Player2, Faction.Player3));
        }

        [Fact]
        public void TwoRealTeams_ProduceNoAdvisory()
        {
            Assert.Empty(new ScenarioValidator().CollectAdvisories(MapWithTeams(1, 1, 2, 2)));
        }

        [Fact]
        public void MixedTeamedAndUnassigned_IsTwoSides_NoAdvisory()
        {
            // {0,1}=team1 vs an UNASSIGNED slot 2: FFA gives slot 2 its own side, so the map is contested.
            Assert.Empty(new ScenarioValidator().CollectAdvisories(MapWithTeams(1, 1, 0)));
        }

        [Fact]
        public void SingleSlotMap_NeverTripsTheAllAlliedAdvisory()
        {
            Assert.DoesNotContain(new ScenarioValidator().CollectAdvisories(MapWithTeams(0)),
                                  a => a.Contains("same team"));
        }

        [Fact]
        public void InertTeamOfOne_IsAdvisory_NotFatal()
        {
            // Two solo ordinals: two sides (so no all-allied advisory) but neither team buys an ally.
            var m = MapWithTeams(1, 2);

            Assert.True(new ScenarioValidator().Validate(m).Ok);
            var advisories = new ScenarioValidator().CollectAdvisories(m);
            Assert.Equal(2, advisories.Count(a => a.Contains("is the only member of team")));
            Assert.Contains(advisories, a => a.Contains("player_slots[0]") && a.Contains("team=1"));
            Assert.Contains(advisories, a => a.Contains("player_slots[1]") && a.Contains("team=2"));
        }

        [Fact]
        public void TeamOfOne_SeedsTheFfaDefault_WhichIsWhyTheAdvisoryCallsItInert()
        {
            var teamed = AllianceSeeder.ComputeTeamIds(ModelOf((0, 1), (1, 2)));
            var ffa    = AllianceSeeder.ComputeTeamIds(ModelOf((0, 0), (1, 0)));
            Assert.Equal(ffa, teamed); // byte-identical to unassigned — the team bought nothing
        }

        [Fact]
        public void ThreeVsOne_SoloOpponent_IsStillFlaggedInert_ButNotAllAllied()
        {
            var advisories = new ScenarioValidator().CollectAdvisories(MapWithTeams(1, 1, 1, 2));
            Assert.DoesNotContain(advisories, a => a.Contains("same team"));
            Assert.Contains(advisories, a => a.Contains("player_slots[3]") && a.Contains("only member of team=2"));
        }

        // ── AllianceSeeder diagnostics: what the mapping silently does ──────────────────────────────────

        [Fact]
        public void OutOfRangeTeamMember_IsDiagnosed_InsteadOfSilentlyDropped()
        {
            // slot 8 → faction index 9, outside [1,9): ComputeTeamIds `continue`s past it, so it is never allied with
            // the teammate it declares. Pre-DW-442 this happened with no diagnostic anywhere.
            var model = ModelOf((0, 1), (8, 1));

            // The mask the declared 2-man team actually gets is byte-identical to FFA — the alliance evaporated.
            Assert.Equal(AllianceSeeder.ComputeTeamIds(ModelOf((0, 0), (8, 0))),
                         AllianceSeeder.ComputeTeamIds(model));

            Assert.Contains(AllianceSeeder.CollectDiagnostics(model),
                            d => d.Contains("player_slots[1].slot=8") && d.Contains("dropped"));
        }

        [Fact]
        public void DuplicateSlot_IsDiagnosed_AsOrderDependent()
        {
            // slot 1 is declared twice — once on team 1 (with slot 0) and once on team 2 (with slot 3).
            var forward  = ModelOf((1, 1), (1, 2), (0, 1), (3, 2));
            var reversed = ModelOf((1, 2), (1, 1), (0, 1), (3, 2)); // the SAME declarations, reordered

            Assert.Contains(AllianceSeeder.CollectDiagnostics(forward),
                            d => d.Contains("player_slots[1] and player_slots[0] both declare slot=1"));

            // …and the diagnostic's claim is true: the same declarations in a different ORDER seed a different mask.
            int[] a = AllianceSeeder.ComputeTeamIds(forward);
            int[] b = AllianceSeeder.ComputeTeamIds(reversed);
            Assert.Equal((int)Faction.Player2, a[(int)Faction.Player2]); // last write wins: P2 lands on team 2
            Assert.Equal((int)Faction.Player1, b[(int)Faction.Player2]); // reordered: P2 lands on team 1 instead
            Assert.NotEqual(a, b);
        }

        [Fact]
        public void DuplicateSlot_IsStillHardRejectedByTheLoadGate()
        {
            // The file path was already closed (the uniqueness check pre-dates DW-442); the seeder diagnostic exists
            // for the models that never pass through Validate — the lobby's own ComputeTeamIds call.
            var m = MapWithTeams(1, 1);
            m.PlayerSlots[1].Slot = 0;

            ValidationResult r = new ScenarioValidator().Validate(m);
            Assert.False(r.Ok);
            Assert.Contains("is a duplicate", r.Error);
        }

        [Fact]
        public void DuplicateSlotWithNoTeams_IsNotASeederDiagnostic()
        {
            // Both entries unassigned ⇒ the mapping writes nothing ⇒ the seeder has no behavior to account for. The
            // duplicate is still an authoring error, and still fails the load gate above.
            Assert.Empty(AllianceSeeder.CollectDiagnostics(ModelOf((0, 0), (0, 0))));
        }

        [Fact]
        public void DuplicatedSlotOnOneTeam_IsNotMistakenForATwoMemberTeam()
        {
            // One faction listed twice is ONE member — the inert-team-of-one finding must not hide behind the typo.
            Assert.Contains(AllianceSeeder.CollectDiagnostics(ModelOf((0, 1), (0, 1))),
                            d => d.Contains("only member of team=1"));
        }

        [Fact]
        public void WellFormedAndFfaModels_ProduceNoDiagnostics()
        {
            Assert.Empty(AllianceSeeder.CollectDiagnostics(null));
            Assert.Empty(AllianceSeeder.CollectDiagnostics(new ScenarioData()));
            Assert.Empty(AllianceSeeder.CollectDiagnostics(ModelOf((0, 0), (1, 0), (2, 0), (3, 0))));
            Assert.Empty(AllianceSeeder.CollectDiagnostics(ModelOf((0, 1), (1, 1), (2, 2), (3, 2))));
        }

        [Fact]
        public void Diagnostics_AreNullElementSafe_AndAscending()
        {
            var model = ModelOf((0, 1), (1, 2));
            model.PlayerSlots = new[] { model.PlayerSlots[0], null!, model.PlayerSlots[1] };

            var diagnostics = AllianceSeeder.CollectDiagnostics(model); // must not NRE ahead of ScenarioValidator
            Assert.Equal(2, diagnostics.Count);
            Assert.Contains("player_slots[0]", diagnostics[0]);
            Assert.Contains("player_slots[2]", diagnostics[1]);
        }

        // ── Non-regression: the mapping itself is untouched (no checksum/hash surface moves) ────────────

        [Fact]
        public void CollectingDiagnostics_DoesNotDisturbTheSeededMask()
        {
            var model = ModelOf((0, 1), (1, 1), (2, 2), (3, 2));
            var before = new AllianceStore();
            AllianceSeeder.Seed(before, model);

            AllianceSeeder.CollectDiagnostics(model);
            var after = new AllianceStore();
            AllianceSeeder.Seed(after, model);

            for (int f = 0; f < FactionRegistry.SLOT_DEFINITIONS_SIZE; f++) Assert.Equal(before.TeamId[f], after.TeamId[f]);
            Assert.Equal((int)Faction.Player1, after.TeamId[(int)Faction.Player2]); // the 9.14 canonical mapping, unchanged
            Assert.Equal((int)Faction.Player3, after.TeamId[(int)Faction.Player4]);
        }

        [Fact]
        public void BlankMap_StillHasNoAdvisories()
        {
            // The New-Map surface runs CollectAdvisories on a freshly created blank; the team block must add no noise.
            foreach (MapSize size in new[] { MapSize.Small, MapSize.Medium, MapSize.Large })
                for (int players = 2; players <= 4; players++)
                    Assert.Empty(new ScenarioValidator().CollectAdvisories(
                        ScenarioData.CreateBlank("m", suggestedPlayers: players, size: size)));
        }
    }
}
