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
