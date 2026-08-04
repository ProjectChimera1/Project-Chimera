#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Validation
{
    /// <summary>
    /// DW-194 regression guard — AlgoVersion test-NAME hygiene. Version-pin tests have repeatedly kept a
    /// version number in their NAME after the assertion was bumped (`AlgoVersion_IsTwelve` asserting 14,
    /// `SimChecksumAlgoVersion_Stays20` asserting 22), so a reader trusting the name misread the pin.
    /// This meta-test reflects over every [Fact]/[Theory] in this assembly whose name mentions
    /// "AlgoVersion" and fails when the name embeds a number AFTER that token (digits like `At12`/`Stays20`,
    /// or cardinal number-words like `IsTwelve`) that differs from the CURRENT pinned constant of the hash
    /// family the name refers to. Version-neutral names (`AlgoVersion_IsPinned`, `AlgoVersions_Unchanged`)
    /// embed no number and can never rot — prefer those. Numbers BEFORE the token (e.g.
    /// `ZeroOrNegativeChecksumAlgoVersion_RejectsLocated`, which describes the INPUT stamp) are ignored.
    /// </summary>
    public class AlgoVersionTestNameHygieneTests
    {
        private const string Token = "AlgoVersion";

        // ── which pinned constant a name refers to ─────────────────────────────────────────────────────
        // A mention in the METHOD name wins over the declaring type (a SimChecksum pin can live inside a
        // CanonicalModelHash*Tests class, e.g. the ButtonFold family's cross-pin).
        private static readonly (string FamilyToken, string Family, int Pinned)[] Families =
        {
            ("SimChecksum",        "SimChecksum",        SimChecksum.AlgoVersion),
            ("CanonicalModelHash", "CanonicalModelHash", CanonicalModelHash.AlgoVersion),
            ("StartStateHash",     "StartStateHash",     StartStateHash.AlgoVersion),
            ("MatchAgreementHash", "MatchAgreementHash", MatchAgreementHash.AlgoVersion),
            ("RulesetHash",        "RulesetHash",        RulesetHash.AlgoVersion),
            ("ContentHash",        "ContentHash",        ContentHash.AlgoVersion),
        };

        private static readonly Dictionary<string, int> UnitWords = new()
        {
            ["Zero"] = 0, ["One"] = 1, ["Two"] = 2, ["Three"] = 3, ["Four"] = 4,
            ["Five"] = 5, ["Six"] = 6, ["Seven"] = 7, ["Eight"] = 8, ["Nine"] = 9,
        };

        private static readonly Dictionary<string, int> TeenWords = new()
        {
            ["Ten"] = 10, ["Eleven"] = 11, ["Twelve"] = 12, ["Thirteen"] = 13, ["Fourteen"] = 14,
            ["Fifteen"] = 15, ["Sixteen"] = 16, ["Seventeen"] = 17, ["Eighteen"] = 18, ["Nineteen"] = 19,
        };

        private static readonly Dictionary<string, int> TensWords = new()
        {
            ["Twenty"] = 20, ["Thirty"] = 30, ["Forty"] = 40, ["Fifty"] = 50,
            ["Sixty"] = 60, ["Seventy"] = 70, ["Eighty"] = 80, ["Ninety"] = 90,
        };

        /// <summary>
        /// Numbers a method name claims for the version, extracted from the substring AFTER the first
        /// "AlgoVersion" token: digit runs plus cardinal number-words (PascalCase segments, with adjacent
        /// tens+unit words merged, "TwentyTwo" → 22). Ordinals ("First") and unrelated words never match.
        /// </summary>
        internal static IReadOnlyList<int> EmbeddedVersionNumbers(string methodName)
        {
            int at = methodName.IndexOf(Token, StringComparison.Ordinal);
            if (at < 0) return Array.Empty<int>();
            string tail = methodName.Substring(at + Token.Length);

            var numbers = new List<int>();

            foreach (Match m in Regex.Matches(tail, @"\d+"))
                if (int.TryParse(m.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int v))
                    numbers.Add(v);

            var segments = Regex.Matches(tail, "[A-Z][a-z]*").Select(m => m.Value).ToList();
            for (int i = 0; i < segments.Count; i++)
            {
                if (TensWords.TryGetValue(segments[i], out int tens))
                {
                    if (i + 1 < segments.Count && UnitWords.TryGetValue(segments[i + 1], out int unit) && unit != 0)
                    {
                        numbers.Add(tens + unit);
                        i++; // consume the merged unit word
                    }
                    else
                    {
                        numbers.Add(tens);
                    }
                }
                else if (TeenWords.TryGetValue(segments[i], out int teen))
                {
                    numbers.Add(teen);
                }
                else if (UnitWords.TryGetValue(segments[i], out int u))
                {
                    numbers.Add(u);
                }
            }

            return numbers;
        }

        /// <summary>Resolve which hash family (and current pin) a version-claiming name refers to.</summary>
        internal static (string Family, int Pinned)? ResolveFamily(string methodName, string declaringTypeName)
        {
            foreach (var (token, family, pinned) in Families)
                if (methodName.Contains(token, StringComparison.Ordinal))
                    return (family, pinned);
            foreach (var (token, family, pinned) in Families)
                if (declaringTypeName.StartsWith(token, StringComparison.Ordinal))
                    return (family, pinned);
            return null;
        }

        // ── the guard ──────────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void AlgoVersionBearingTestNames_EmbedNoStaleVersionNumber()
        {
            var offenders = new List<string>();

            foreach (var type in typeof(AlgoVersionTestNameHygieneTests).Assembly.GetTypes())
            {
                const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static |
                                         BindingFlags.DeclaredOnly;
                foreach (var method in type.GetMethods(all))
                {
                    // IsDefined with inherit:true also matches attributes DERIVED from Fact ([Theory], custom facts).
                    if (!method.IsDefined(typeof(FactAttribute), inherit: true)) continue;
                    if (!method.Name.Contains(Token, StringComparison.Ordinal)) continue;

                    var numbers = EmbeddedVersionNumbers(method.Name);
                    if (numbers.Count == 0) continue; // version-neutral name — can never rot

                    var resolved = ResolveFamily(method.Name, type.Name);
                    if (resolved is null)
                    {
                        offenders.Add(
                            $"{type.Name}.{method.Name}: embeds {string.Join("/", numbers)} but no hash family is " +
                            "recognizable from the name or declaring type — use a version-neutral name " +
                            "(e.g. AlgoVersion_IsPinned) or extend AlgoVersionTestNameHygieneTests.Families.");
                        continue;
                    }

                    foreach (int n in numbers.Where(n => n != resolved.Value.Pinned))
                        offenders.Add(
                            $"{type.Name}.{method.Name}: name claims {n} but {resolved.Value.Family}.AlgoVersion " +
                            $"is {resolved.Value.Pinned} — rename to a version-neutral form (e.g. AlgoVersion_IsPinned) " +
                            "so the name cannot rot on the next bump (DW-194).");
                }
            }

            Assert.True(offenders.Count == 0,
                "Stale AlgoVersion test-method name(s) — the name misleads readers about the pinned version:\n"
                + string.Join("\n", offenders));
        }

        // ── the extractor's own teeth (real names from this suite's history) ───────────────────────────

        [Theory]
        [InlineData("AlgoVersion_IsPinned", new int[0])]                                        // neutral — the preferred form
        [InlineData("AlgoVersions_Unchanged", new int[0])]
        [InlineData("HashAlgoVersions_AreUnchanged", new int[0])]
        [InlineData("AlgoVersion_MixedFirst_MovesTheValue", new int[0])]                        // ordinal words are not claims
        [InlineData("ZeroOrNegativeChecksumAlgoVersion_RejectsLocated", new int[0])]            // number BEFORE the token = input description
        [InlineData("ChecksumStays20_NoToken", new int[0])]                                     // no token → not a version-pin name
        [InlineData("AlgoVersion_IsTwelve", new[] { 12 })]                                      // the DW-194 rot class…
        [InlineData("AlgoVersion_Pinned_At12", new[] { 12 })]
        [InlineData("SimChecksumAlgoVersion_Stays20", new[] { 20 })]
        [InlineData("FeedbackProfile_IsExcludedFromSimChecksum_AndAlgoVersionIs10", new[] { 10 })]
        [InlineData("AlgoVersion_IsThree_AfterContentFold", new[] { 3 })]
        [InlineData("AlgoVersion_WentTwentyTwo", new[] { 22 })]                                 // tens+unit words merge
        public void EmbeddedVersionNumberExtraction_Works(string name, int[] expected) =>
            Assert.Equal(expected, EmbeddedVersionNumbers(name));

        [Fact]
        public void FamilyResolution_MethodNameMentionWinsOverDeclaringType()
        {
            // A SimChecksum cross-pin inside a CanonicalModelHash*Tests class must resolve to SimChecksum.
            var byName = ResolveFamily("SimChecksumAlgoVersion_IsPinned", "CanonicalModelHashButtonFoldTests");
            Assert.Equal(("SimChecksum", SimChecksum.AlgoVersion), byName);

            // No mention in the method name → the declaring type's family prefix resolves it.
            var byType = ResolveFamily("AlgoVersion_IsPinned", "CanonicalModelHashButtonFoldTests");
            Assert.Equal(("CanonicalModelHash", CanonicalModelHash.AlgoVersion), byType);
        }
    }
}
