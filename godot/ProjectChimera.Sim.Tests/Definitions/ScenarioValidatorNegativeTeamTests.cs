#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using ProjectChimera.Combat;             // DamageTable
using ProjectChimera.Core;               // AllianceSeeder, FactionRegistry, HeroStore, HeroId, Fixed
using ProjectChimera.Core.Definitions;   // ScenarioData, ScenarioValidator, MatchAgreementHash
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// DW-835 — the RATIONALE pin for DW-442's negative-team reject in <see cref="ScenarioValidator"/>.
    ///
    /// <para><b>The defect.</b> The reject shipped justified by a divergence that cannot occur: the comment above it
    /// claimed that "because Team folds into the match-agreement hash … two peers whose files differ only in WHICH
    /// negative ordinal they carry fail the start handshake over alliance masks that are byte-identical". That is not
    /// what <see cref="MatchAgreementHash"/> folds. It folds
    /// <see cref="AllianceSeeder.ComputeTeamIds(ScenarioData?)"/> — the CANONICAL faction-keyed team-id mask —
    /// deliberately NOT the positional per-slot <c>.Team</c> ordinal, precisely so a reordered or gapped roster cannot
    /// slip past. <c>ComputeTeamIds</c> skips every non-positive ordinal, so -1, -2 and 0 all fold the identical FFA
    /// mask and therefore the identical agreement hash. The gate is still defensible on the OTHER half of the same
    /// comment — a negative ordinal is an authoring lie, since the file says "team" while the seeder silently means
    /// FFA — but that is an argument about author intent, not about a handshake, and the recorded reason is what will
    /// mislead the next reader deciding whether to relax the gate.</para>
    ///
    /// <para><b>What this file pins.</b> (1) The empirical premise, so the corrected comment is checkable rather than
    /// asserted: two models differing ONLY in which negative ordinal they carry compute the SAME agreement hash and
    /// the SAME seeded mask, and 0 joins them. (2) A comment-hygiene guard over <c>ScenarioValidator.cs</c>, in the
    /// <c>ModifierStoreReentrancyDocTests</c> shape, so the divergence claim cannot come back. (3) The gate's own
    /// behaviour, unchanged — this entry corrects documentation, never the reject.</para>
    ///
    /// <para>Godot-free; integer/hash arithmetic only. Nothing here is folded into any golden, checksum or
    /// <c>AlgoVersion</c>.</para>
    /// </summary>
    public class ScenarioValidatorNegativeTeamTests
    {
        /// <summary>The match's initial input delay. A literal, not <c>LockstepManager.INPUT_DELAY</c> (Godot-coupled);
        /// both "peers" here fold the same value, so it can never be the source of a difference.</summary>
        private const int InitialDelay = 4;

        private static readonly List<FactionDefinition> NoFactions = new();

        // ── fixtures ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>A two-slot model whose ONLY varying field is the per-slot <c>team</c> ordinal. Fresh slot objects
        /// on every call so one model's teams can never leak into another's.</summary>
        private static ScenarioData ModelWithTeam(int team)
        {
            var slots = new ScenarioPlayerSlot[2];
            for (int i = 0; i < slots.Length; i++)
                slots[i] = new ScenarioPlayerSlot
                {
                    Slot = i, FactionJson = $"res://f{i}.json", StartOre = 200f, StartCrystal = 50f,
                    BaseX = -40f + i * 40f, BaseZ = 0f, Team = team,
                };
            return new ScenarioData
            {
                Id = "dw835", DisplayName = "dw835", TerrainRef = "res://t.tres", MapBounds = 120f,
                WinCondition = WinCondition.EliminateAllUnits,
                PlayerSlots   = slots,
                ResourceNodes = Array.Empty<ScenarioResourceNode>(),
                Buildings     = Array.Empty<ScenarioBuilding>(),
                Units         = Array.Empty<ScenarioUnit>(),
                Triggers      = Array.Empty<TriggerDefinition>(),
            };
        }

        private static HeroStore Heroes()
        {
            var s = new HeroStore();
            s.Mint(new HeroId(1_000_000_007UL), entityId: 3, level: 4, xp: Fixed.FromInt(250));
            return s;
        }

        /// <summary>The value a peer puts on its Ready packet for <paramref name="model"/>. Content, heroes and delay
        /// are identical across calls, so the team ordinal is the only thing that could move it.</summary>
        private static ulong Agreement(ScenarioData model) =>
            MatchAgreementHash.Compute(InitialDelay, model, Heroes(),
                NoFactions, AbilityRegistry.Empty, ItemRegistry.Empty, DamageTable.Default,
                ProjectChimera.AI.AiControlPlan.None);

        // ── 1. the premise: every non-positive ordinal is the SAME match, byte for byte ────────────────────

        [Theory]
        [InlineData(-1, -2)]
        [InlineData(-1, 0)]
        [InlineData(-2, 0)]
        [InlineData(-1, -8191)]
        public void NonPositiveTeamOrdinals_AreIndistinguishableToTheMatchAgreementHash(int a, int b)
        {
            // The claim the old comment made was that these two files CANNOT agree. They agree exactly: the fold is
            // the canonical mask, and the mask is the FFA default for every ordinal <= 0.
            Assert.Equal(Agreement(ModelWithTeam(a)), Agreement(ModelWithTeam(b)));
        }

        [Theory]
        [InlineData(-1, -2)]
        [InlineData(-1, 0)]
        [InlineData(-2, 0)]
        public void NonPositiveTeamOrdinals_SeedTheIdenticalCanonicalMask(int a, int b)
        {
            // Read through the SAME mapping the sim runs (AllianceSeeder), not through a hash — so this is the
            // real-consequence half of the premise rather than a hash tautology.
            Assert.Equal(AllianceSeeder.ComputeTeamIds(ModelWithTeam(a)),
                         AllianceSeeder.ComputeTeamIds(ModelWithTeam(b)));

            var storeA = new AllianceStore();
            var storeB = new AllianceStore();
            AllianceSeeder.Seed(storeA, ModelWithTeam(a));
            AllianceSeeder.Seed(storeB, ModelWithTeam(b));
            for (int f = 0; f < FactionRegistry.SLOT_DEFINITIONS_SIZE; f++)
                Assert.Equal(storeA.TeamId[f], storeB.TeamId[f]);
        }

        [Fact]
        public void APositiveTeam_DoesMoveTheAgreementHash_SoTheFoldIsNotInert()
        {
            // The counterfactual that keeps the theory above from being vacuous: the agreement hash really is
            // sensitive to a team layout — just not to WHICH non-positive ordinal spells "no team".
            Assert.NotEqual(Agreement(ModelWithTeam(0)), Agreement(ModelWithTeam(1)));
        }

        // ── 2. the gate itself is unchanged — DW-835 corrects prose, never behaviour ───────────────────────

        [Theory]
        [InlineData(-1)]
        [InlineData(-2)]
        public void NegativeTeam_StillFailsClosedLocated(int team)
        {
            ValidationResult r = new ScenarioValidator().Validate(ModelWithTeam(team));

            Assert.False(r.Ok);
            Assert.Contains($"team={team}", r.Error);
            Assert.Contains("must be >= 0", r.Error);
        }

        [Fact]
        public void ZeroTeam_StillLoads_TheOmitWhenDefaultFfaValueEveryShippedMapCarries()
        {
            Assert.True(new ScenarioValidator().Validate(ModelWithTeam(0)).Ok);
        }

        // ── 3. the comment-hygiene guard: the divergence claim cannot come back ────────────────────────────

        /// <summary>The retired claim, in the forms it could plausibly be re-typed: a negative ordinal FAILING /
        /// BREAKING / DIVERGING at the handshake or the match-agreement hash. Deliberately NOT a bare "handshake"
        /// ban — the corrected comment has to be able to SAY what the reject is not.</summary>
        private static readonly Regex RetiredDivergenceClaim = new(
            @"\b(?:fail|fails|break|breaks|diverge|diverges|mismatch|disagree|disagrees)\b[^.]{0,80}\b(?:handshake|match-agreement|agreement hash)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        [Fact]
        public void TheNegativeTeamRejectComment_DoesNotJustifyItselfWithAHandshakeDivergence()
        {
            string block = NegativeTeamCommentBlock();

            Assert.False(RetiredDivergenceClaim.IsMatch(block),
                "The DW-442 negative-team reject in ScenarioValidator.cs is justified again by a start-handshake / "
                + "match-agreement divergence. That divergence cannot occur (DW-835): MatchAgreementHash folds "
                + "AllianceSeeder.ComputeTeamIds — the canonical faction-keyed mask — not the positional per-slot "
                + ".Team ordinal, and ComputeTeamIds skips every non-positive ordinal, so -1, -2 and 0 fold the "
                + "identical FFA mask (pinned by the theories above). Justify the gate on AUTHOR INTENT — the file "
                + "says \"team\" while the seeder silently means FFA — or drop it to a CollectAdvisories advisory:\n"
                + block);
        }

        [Fact]
        public void TheNegativeTeamRejectComment_NamesWhatTheHashActuallyFolds()
        {
            // Defeat a vacuous pass: deleting the paragraph rather than correcting it must not turn this guard green.
            string block = NegativeTeamCommentBlock();

            Assert.Contains("DW-835", block, StringComparison.Ordinal);
            Assert.Contains("ComputeTeamIds", block, StringComparison.Ordinal);
            Assert.Contains("MatchAgreementHash", block, StringComparison.Ordinal);
            Assert.Contains("ScenarioValidatorNegativeTeamTests", block, StringComparison.Ordinal);
        }

        /// <summary>The contiguous <c>//</c> comment run that opens with the DW-442 team note, taken straight out of
        /// <c>ScenarioValidator.cs</c>.</summary>
        private static string NegativeTeamCommentBlock()
        {
            string[] lines = File.ReadAllLines(ValidatorSourceFile());

            int start = -1;
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].Contains("DW-442", StringComparison.Ordinal)
                    && lines[i].Contains("team", StringComparison.OrdinalIgnoreCase))
                { start = i; break; }

            Assert.True(start >= 0,
                "ScenarioValidator.cs no longer carries the DW-442 team note at all (vacuous-pass hazard). If the "
                + "reject was removed, retire this guard deliberately rather than letting it pass on absence.");

            var block = new System.Text.StringBuilder();
            for (int i = start; i < lines.Length && lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal); i++)
                block.Append(lines[i].Trim()).Append('\n');
            return block.ToString();
        }

        /// <summary>This file lives in godot/ProjectChimera.Sim.Tests/Definitions/ ⇒ ../../src/Core/Definitions/.</summary>
        private static string ValidatorSourceFile(
            [System.Runtime.CompilerServices.CallerFilePath] string thisFilePath = "")
        {
            string dir = Path.GetDirectoryName(thisFilePath)
                         ?? throw new InvalidOperationException("Could not resolve this test's source dir via [CallerFilePath].");
            string path = Path.GetFullPath(Path.Combine(dir, "..", "..", "src", "Core", "Definitions", "ScenarioValidator.cs"));
            Assert.True(File.Exists(path), $"source file not found at '{path}' (via [CallerFilePath]).");
            return path;
        }
    }
}
