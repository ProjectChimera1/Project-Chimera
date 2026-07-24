#nullable enable
using ProjectChimera.Core;             // Fixed, HeroStore, HeroId, FactionRegistry
using ProjectChimera.Core.Definitions; // MatchAgreementHash, RulesetHash, StartStateHash, ScenarioData
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// Story 9.4 — <see cref="MatchAgreementHash"/> is the single 64-bit start-state-agreement value on the widened
    /// Ready packet. It folds AlgoVersion + <see cref="RulesetHash"/> + the initial delay + faction-count + roster +
    /// <see cref="StartStateHash"/>. These tests pin it deterministic + non-zero and BEHAVIORALLY sensitive: a
    /// change to the initial delay, the player count/roster, OR the start-state (content / hero loadout — the
    /// superset property) MUST move the hash, so a mismatch on any of them is handshake-rejectable.
    /// </summary>
    public class MatchAgreementHashTests
    {
        private static ScenarioData BuildModel(int players = 2, float supplyBump = 0f)
        {
            var slots = new ScenarioPlayerSlot[players];
            for (int i = 0; i < players; i++)
                slots[i] = new ScenarioPlayerSlot
                {
                    Slot = i, FactionJson = $"res://f{i}.json",
                    StartOre = 200f, StartCrystal = 50f, BaseX = -45f + i * 30f, BaseZ = 0f,
                };
            return new ScenarioData
            {
                Id = "cosmetic", DisplayName = "cosmetic", TerrainRef = "res://t.tres", MapBounds = 120f,
                WinCondition = WinCondition.EliminateAllUnits,
                PlayerSlots = slots,
                ResourceNodes = new[] { new ScenarioResourceNode { X = -20f, Z = -10f, Supply = 400f + supplyBump, Rate = 5f, MaxGatherers = 4 } },
            };
        }

        private static HeroStore TwoHeroes(int levelOfFirst = 4)
        {
            var s = new HeroStore();
            s.Mint(new HeroId(1_000_000_007UL), entityId: 3, level: levelOfFirst, xp: Fixed.FromInt(250));
            s.Mint(new HeroId(2_000_000_011UL), entityId: 8, level: 1, xp: Fixed.FromInt(0));
            return s;
        }

        [Fact]
        public void Compute_IsDeterministic_AndNonZero()
        {
            var m = BuildModel();
            ulong a = MatchAgreementHash.Compute(4, m, TwoHeroes());
            ulong b = MatchAgreementHash.Compute(4, m, TwoHeroes());
            Assert.Equal(a, b);
            Assert.NotEqual(0UL, a);
        }

        // ── Independent-fold pin: every component is actually folded, in order ──────

        private const ulong Offset = 14695981039346656037UL;
        private const ulong Prime  = 1099511628211UL;

        private static ulong Mix(ulong h, int value)
        {
            uint v = (uint)value;
            h ^= v & 0xFF;         h *= Prime;
            h ^= (v >> 8) & 0xFF;  h *= Prime;
            h ^= (v >> 16) & 0xFF; h *= Prime;
            h ^= (v >> 24) & 0xFF; h *= Prime;
            return h;
        }

        private static ulong MixULong(ulong h, ulong value)
        {
            h = Mix(h, (int)(value & 0xFFFFFFFFUL));
            h = Mix(h, (int)(value >> 32));
            return h;
        }

        [Fact]
        public void Compute_MatchesTheIndependentlyFoldedByteStream()
        {
            // Anti-tautology pin (1.1): hand-roll the documented fold in order — AlgoVersion → RulesetHash →
            // initialDelay → N → each roster ordinal → StartStateHash — and assert equality with Compute. This
            // catches the silent-drop failure the "value moves" tests cannot: RulesetHash / AlgoVersion / the roster
            // loop are CONSTANT across fixtures, so deleting any of those mixes would still pass those tests but
            // WOULD break this one (dropping the effect-caps ruleset protection off the wire).
            var model  = BuildModel(players: 3);
            var heroes = TwoHeroes();
            const int initialDelay = 4;

            ulong h = Offset;
            h = Mix(h, MatchAgreementHash.AlgoVersion);
            h = MixULong(h, RulesetHash.Compute());
            h = Mix(h, initialDelay);
            int n = model.PlayerSlots.Length;
            h = Mix(h, n);
            for (int slot = 0; slot < n; slot++)
                h = Mix(h, (int)FactionRegistry.ToFaction(slot));
            int[] teamIds = AllianceSeeder.ComputeTeamIds(model); // Story 9.14: fold the CANONICAL seeded team-id mask, faction-indexed
            for (int fi = 1; fi < teamIds.Length; fi++)
                h = Mix(h, teamIds[fi]);
            h = MixULong(h, StartStateHash.Compute(model, heroes));
            ulong expected = h == 0UL ? 1UL : h;

            Assert.Equal(expected, MatchAgreementHash.Compute(initialDelay, model, heroes));
        }

        [Fact]
        public void DifferentInitialDelay_MovesTheHash()
        {
            var m = BuildModel();
            Assert.NotEqual(
                MatchAgreementHash.Compute(4, m, TwoHeroes()),
                MatchAgreementHash.Compute(5, m, TwoHeroes()));
        }

        [Fact]
        public void DifferentFactionCountAndRoster_MovesTheHash()
        {
            // A 2-player vs a 3-player model folds a different N + one extra roster ordinal.
            Assert.NotEqual(
                MatchAgreementHash.Compute(4, BuildModel(players: 2), TwoHeroes()),
                MatchAgreementHash.Compute(4, BuildModel(players: 3), TwoHeroes()));
        }

        [Fact]
        public void DifferentContent_MovesTheHash()
        {
            Assert.NotEqual(
                MatchAgreementHash.Compute(4, BuildModel(supplyBump: 0f),  TwoHeroes()),
                MatchAgreementHash.Compute(4, BuildModel(supplyBump: 25f), TwoHeroes()));
        }

        [Fact]
        public void DifferentHeroLoadout_MovesTheHash_TheStartStateSupersetProperty()
        {
            // The whole reason MatchAgreementHash folds StartStateHash (not the bare 32-bit scenario hash): a
            // hero-level mismatch — which the scenario content hash cannot see — must be handshake-rejectable.
            var m = BuildModel();
            Assert.NotEqual(
                MatchAgreementHash.Compute(4, m, TwoHeroes(levelOfFirst: 4)),
                MatchAgreementHash.Compute(4, m, TwoHeroes(levelOfFirst: 7)));
        }

        // ── Story 9.14: the per-slot team folds into the handshake hash (a team mismatch fails the start closed) ──

        [Fact]
        public void AlgoVersion_IsTwo_AfterTeamFold()
        {
            Assert.Equal(2, MatchAgreementHash.AlgoVersion);
        }

        [Fact]
        public void DifferentTeamAssignment_MovesTheHash()
        {
            // FFA vs a REAL 2v2 (slots {0,1} and {2,3} paired): the canonical seeded ids differ from FFA (P2→1, P4→3),
            // so the handshake value MUST differ — peers that disagree on the team layout fail closed pre-tick-0 (the
            // fail-closed team-mismatch matrix row). A lone-slot "team" would map back to its own faction (FFA-equal),
            // which is exactly the canonical-encoding semantics; a divergent MASK needs a genuine multi-member team.
            var ffa = BuildModel(players: 4);
            var teamed = BuildModel(players: 4);
            teamed.PlayerSlots[0].Team = 1;
            teamed.PlayerSlots[1].Team = 1; // {P1,P2} → P2's canonical id becomes 1 (was 2 in FFA)
            teamed.PlayerSlots[2].Team = 2;
            teamed.PlayerSlots[3].Team = 2; // {P3,P4} → P4's canonical id becomes 3 (was 4 in FFA)
            Assert.NotEqual(
                MatchAgreementHash.Compute(4, ffa, TwoHeroes()),
                MatchAgreementHash.Compute(4, teamed, TwoHeroes()));
        }

        [Fact]
        public void GappedRoster_TeamMismatch_StillMovesTheHash_FailsClosed()
        {
            // Regression (9.14 review): a NON-CONTIGUOUS roster (a removed middle slot → slots {0,1,3,4}) with a team on
            // the two GAPPED-region high slots {3,4} (factions {4,5}). A positional team fold folded only teamIds[1..n]
            // and MISSED faction 5's moved canonical id, so peers that disagreed on that team hashed identically and the
            // start failed OPEN. The faction-indexed mask fold catches it: peer A (FFA) and peer B (teamed) MUST diverge.
            var ffa = BuildModel(players: 5);
            ffa.RemoveStartSlot(2);                   // gap at slot 2 → {0,1,3,4}
            var teamed = BuildModel(players: 5);
            teamed.RemoveStartSlot(2);
            foreach (var s in teamed.PlayerSlots)
                if (s.Slot == 3 || s.Slot == 4) s.Team = 1; // team the gapped-region pair → P5's canonical id moves 5→4
            Assert.NotEqual(
                MatchAgreementHash.Compute(4, ffa, TwoHeroes()),
                MatchAgreementHash.Compute(4, teamed, TwoHeroes()));
        }

        [Fact]
        public void FfaModel_TeamFoldIsInert_ByteStreamMatchesTheHandRoll()
        {
            // Every Team==0 in an FFA model, so the new per-slot fold mixes a constant 0 — the algo-version bump moves
            // the value once, but two FFA models with identical rosters still hash identically (the fold is inert).
            var a = BuildModel(players: 3);
            var b = BuildModel(players: 3);
            Assert.Equal(MatchAgreementHash.Compute(4, a, TwoHeroes()),
                         MatchAgreementHash.Compute(4, b, TwoHeroes()));
        }

        [Fact]
        public void FoldsStartStateHash_ButDoesNotEqualIt()
        {
            // It USES StartStateHash's value (so hero-start-state golden / AlgoVersion stay put) but layers the
            // ruleset + delay + roster on top, so the two values are distinct.
            var m = BuildModel();
            var heroes = TwoHeroes();
            ulong startState = StartStateHash.Compute(m, heroes);
            ulong agreement  = MatchAgreementHash.Compute(4, m, heroes);
            Assert.NotEqual(startState, agreement);
        }
    }
}
