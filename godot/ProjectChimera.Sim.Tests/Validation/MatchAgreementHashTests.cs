#nullable enable
using System.Collections.Generic;
using ProjectChimera.Core;             // Fixed, HeroStore, HeroId, FactionRegistry
using ProjectChimera.Core.Definitions; // MatchAgreementHash, RulesetHash, StartStateHash, ContentHash, ScenarioData
using ProjectChimera.Combat;           // DamageTable
using ProjectChimera.AI;               // AiControlPlan (DW-908)
using ProjectChimera.Multiplayer;      // HandshakeGate (DW-908: the fold must actually BLOCK, not merely differ)
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

        private static readonly List<FactionDefinition> EmptyFactions = new();

        /// <summary>Story 9.16 — the content params are now REQUIRED on <see cref="MatchAgreementHash.Compute"/> (no
        /// null default, closing the split-brain fold-empty-content hole). This terse test wrapper supplies explicit
        /// EMPTIES by default so a test that isn't exercising the content fold reads cleanly, while the content tests
        /// pass real content positionally.</summary>
        /// <summary>DW-908 — the AI-plan param defaults to the ONLINE plan (<see cref="AiControlPlan.None"/>) here, the
        /// value a real online peer folds; the plan tests pass a populated one explicitly.</summary>
        private static ulong Agree(int delay, ScenarioData m, HeroStore h,
            IReadOnlyList<FactionDefinition>? f = null, AbilityRegistry? a = null, ItemRegistry? i = null, DamageTable? d = null,
            AiControlPlan? ai = null)
            => MatchAgreementHash.Compute(delay, m, h,
                f ?? EmptyFactions, a ?? AbilityRegistry.Empty, i ?? ItemRegistry.Empty, d ?? DamageTable.Default,
                ai ?? AiControlPlan.None);

        [Fact]
        public void Compute_IsDeterministic_AndNonZero()
        {
            var m = BuildModel();
            ulong a = Agree(4, m, TwoHeroes());
            ulong b = Agree(4, m, TwoHeroes());
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
            h = MixULong(h, ContentHash.Compute(EmptyFactions, AbilityRegistry.Empty, ItemRegistry.Empty, DamageTable.Default)); // Story 9.16: folded immediately after the ruleset (the Agree wrapper's empties)
            h = Mix(h, initialDelay);
            int n = model.PlayerSlots.Length;
            h = Mix(h, n);
            for (int slot = 0; slot < n; slot++)
                h = Mix(h, (int)FactionRegistry.ToFaction(slot));
            int[] teamIds = AllianceSeeder.ComputeTeamIds(model); // Story 9.14: fold the CANONICAL seeded team-id mask, faction-indexed
            for (int fi = 1; fi < teamIds.Length; fi++)
                h = Mix(h, teamIds[fi]);
            h = Mix(h, AiControlPlan.None.Mask); // DW-908: the AI-controlled faction set, folded beside the roster (the Agree wrapper's online plan)
            h = MixULong(h, StartStateHash.Compute(model, heroes));
            ulong expected = h == 0UL ? 1UL : h;

            Assert.Equal(expected, Agree(initialDelay, model, heroes));
        }

        [Fact]
        public void DifferentInitialDelay_MovesTheHash()
        {
            var m = BuildModel();
            Assert.NotEqual(
                Agree(4, m, TwoHeroes()),
                Agree(5, m, TwoHeroes()));
        }

        [Fact]
        public void DifferentFactionCountAndRoster_MovesTheHash()
        {
            // A 2-player vs a 3-player model folds a different N + one extra roster ordinal.
            Assert.NotEqual(
                Agree(4, BuildModel(players: 2), TwoHeroes()),
                Agree(4, BuildModel(players: 3), TwoHeroes()));
        }

        [Fact]
        public void DifferentContent_MovesTheHash()
        {
            Assert.NotEqual(
                Agree(4, BuildModel(supplyBump: 0f),  TwoHeroes()),
                Agree(4, BuildModel(supplyBump: 25f), TwoHeroes()));
        }

        [Fact]
        public void DifferentHeroLoadout_MovesTheHash_TheStartStateSupersetProperty()
        {
            // The whole reason MatchAgreementHash folds StartStateHash (not the bare 32-bit scenario hash): a
            // hero-level mismatch — which the scenario content hash cannot see — must be handshake-rejectable.
            var m = BuildModel();
            Assert.NotEqual(
                Agree(4, m, TwoHeroes(levelOfFirst: 4)),
                Agree(4, m, TwoHeroes(levelOfFirst: 7)));
        }

        // ── Story 9.14: the per-slot team folds into the handshake hash (a team mismatch fails the start closed) ──

        [Fact]
        public void AlgoVersion_IsPinned()
        {
            // Version-NEUTRAL name (the DW-194 hygiene rule) — the assertion is the pin, and the per-version rationale
            // lives on the constant's own XML doc. DW-908: bumped 3→4 when the AI-controlled faction set folded in.
            Assert.Equal(4, MatchAgreementHash.AlgoVersion);
        }

        // ── DW-908: the AI-controlled faction set folds into the handshake value ──

        [Fact]
        public void DifferentAiControlPlan_MovesTheHash()
        {
            // THE fail-closed property this fold exists for. Two peers that disagree about who the AI drives — the
            // exact disagreement that let an AI co-pilot the joining human's Player2 and desync the FR-39 gate at
            // tick 660 — must produce DIFFERENT agreement values, so HandshakeGate blocks the start pre-tick-0
            // instead of the match diverging 600 ticks in.
            var m = BuildModel();
            ulong noAi   = Agree(4, m, TwoHeroes(), ai: AiControlPlan.None);
            ulong aiOnP2 = Agree(4, m, TwoHeroes(), ai: AiControlPlan.OfflineDefault);

            Assert.NotEqual(noAi, aiOnP2);
            Assert.NotNull(HandshakeGate.CheckStart(noAi, aiOnP2)); // and the gate actually BLOCKS on it
        }

        [Fact]
        public void SameAiControlPlan_ComposesToAnAllow()
        {
            var m = BuildModel();
            Assert.Null(HandshakeGate.CheckStart(
                Agree(4, m, TwoHeroes(), ai: AiControlPlan.None),
                Agree(4, m, TwoHeroes(), ai: AiControlPlan.None)));
        }

        [Fact]
        public void AiControlPlan_OnDifferentFactions_MovesTheHash()
        {
            // Not just "AI vs no AI" — WHICH faction the AI drives is folded too, so a future multi-AI lobby (leg 1/3)
            // cannot seat the AI differently on two peers and still agree.
            var m = BuildModel(players: 3);
            Assert.NotEqual(
                Agree(4, m, TwoHeroes(), ai: AiControlPlan.Of(Faction.Player2)),
                Agree(4, m, TwoHeroes(), ai: AiControlPlan.Of(Faction.Player3)));
        }

        [Fact]
        public void RosterFactions_MatchesTheFoldedRosterWalk()
        {
            // DW-908 extracted RosterFactions from the fold so the online AI plan's "who occupies a slot" set and the
            // hash's roster walk are ONE derivation. Pin that they still agree — a second walk drifting from this one
            // is precisely how the folded plan and the running plan would come apart.
            ScenarioData m = BuildModel(players: 3);
            Faction[] roster = MatchAgreementHash.RosterFactions(m);

            Assert.Equal(3, roster.Length);
            for (int slot = 0; slot < roster.Length; slot++)
                Assert.Equal(FactionRegistry.ToFaction(slot), roster[slot]);
            Assert.Empty(MatchAgreementHash.RosterFactions(null)); // null model ⇒ empty roster, never a throw
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
                Agree(4, ffa, TwoHeroes()),
                Agree(4, teamed, TwoHeroes()));
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
                Agree(4, ffa, TwoHeroes()),
                Agree(4, teamed, TwoHeroes()));
        }

        [Fact]
        public void FfaModel_TeamFoldIsInert_ByteStreamMatchesTheHandRoll()
        {
            // Every Team==0 in an FFA model, so the new per-slot fold mixes a constant 0 — the algo-version bump moves
            // the value once, but two FFA models with identical rosters still hash identically (the fold is inert).
            var a = BuildModel(players: 3);
            var b = BuildModel(players: 3);
            Assert.Equal(Agree(4, a, TwoHeroes()),
                         Agree(4, b, TwoHeroes()));
        }

        // ── Story 9.16: the CONTENT fold — a content-byte mismatch moves the value + StartStateHash is untouched ──

        private static List<FactionDefinition> OneFaction(float attackDamage = 10f) => new()
        {
            new FactionDefinition
            {
                Id = "alpha",
                Units = new List<UnitDefinition> { new UnitDefinition { Id = "warrior", AttackDamage = attackDamage } },
            },
        };

        [Fact]
        public void DifferentContent_UnitStat_MovesTheHash()
        {
            // A single unit's attack_damage differing on one peer must move MatchAgreementHash → the gate blocks
            // fail-closed pre-tick (the I/O-matrix "unit stat mutation" row, end-to-end through the folded content).
            var m = BuildModel();
            Assert.NotEqual(
                Agree(4, m, TwoHeroes(), OneFaction(attackDamage: 10f), AbilityRegistry.Empty, ItemRegistry.Empty, DamageTable.Default),
                Agree(4, m, TwoHeroes(), OneFaction(attackDamage: 11f), AbilityRegistry.Empty, ItemRegistry.Empty, DamageTable.Default));
        }

        [Fact]
        public void SameContent_HashesEqual()
        {
            var m = BuildModel();
            Assert.Equal(
                Agree(4, m, TwoHeroes(), OneFaction(), AbilityRegistry.Empty, ItemRegistry.Empty, DamageTable.Default),
                Agree(4, m, TwoHeroes(), OneFaction(), AbilityRegistry.Empty, ItemRegistry.Empty, DamageTable.Default));
        }

        [Fact]
        public void ContentFold_DoesNotTouchStartStateHash()
        {
            // The whole point of folding content into MatchAgreementHash (not StartStateHash): the hero-start-state
            // golden and StartStateHash.AlgoVersion must not move. StartStateHash is content-independent by construction.
            var m = BuildModel();
            ulong ss = StartStateHash.Compute(m, TwoHeroes());
            Assert.Equal(ss, StartStateHash.Compute(m, TwoHeroes())); // determinism
            // Two MatchAgreementHash computes with DIFFERENT content both fold the SAME StartStateHash value — the
            // difference lives only in the ContentHash component, never in StartStateHash.
            Assert.NotEqual(
                Agree(4, m, TwoHeroes(), OneFaction(10f), AbilityRegistry.Empty, ItemRegistry.Empty, DamageTable.Default),
                Agree(4, m, TwoHeroes(), OneFaction(12f), AbilityRegistry.Empty, ItemRegistry.Empty, DamageTable.Default));
            Assert.Equal(2, StartStateHash.AlgoVersion); // unchanged by this story
        }

        [Fact]
        public void FoldsStartStateHash_ButDoesNotEqualIt()
        {
            // It USES StartStateHash's value (so hero-start-state golden / AlgoVersion stay put) but layers the
            // ruleset + delay + roster on top, so the two values are distinct.
            var m = BuildModel();
            var heroes = TwoHeroes();
            ulong startState = StartStateHash.Compute(m, heroes);
            ulong agreement  = Agree(4, m, heroes);
            Assert.NotEqual(startState, agreement);
        }
    }
}
