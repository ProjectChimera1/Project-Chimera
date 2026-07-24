#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ProjectChimera.Core;                 // FactionRegistry, Faction
using ProjectChimera.Multiplayer;          // PlayerCountPolicy
using ProjectChimera.Multiplayer.Server;   // ServerChecksumCollector
using Xunit;

namespace ProjectChimera.Sim.Tests.Meta
{
    /// <summary>
    /// Story 9.15 — the no-hardcoded-player-count guard. Two halves:
    ///
    /// <para><b>(1) Source scan.</b> Sweeps every <c>src/**/*.cs</c> for a player-count-SEMANTIC integer constant
    /// (<c>const int</c> / <c>static readonly int</c> whose NAME denotes a slot / seat / player / peer / capacity /
    /// ceiling / faction count) assigned the raw literal <c>4</c> or <c>9</c>, and asserts every such site is on the
    /// sanctioned allowlist. This is the future-proofing the <c>MpSeatCeiling 4→8</c> bump rests on: when a dev raises
    /// the seat ceiling they change the ONE sanctioned constant, not a scattered set of stray 4s. A NEW hardcoded
    /// player-count constant (the regression this guards) turns the test RED with a pointer to route through
    /// <see cref="PlayerCountPolicy.MpSeatCeiling"/> / <see cref="FactionRegistry.FACTION_ARRAY_SIZE"/> instead.</para>
    ///
    /// <para><b>(2) Bump-invariant.</b> Pins the FactionRegistry chain
    /// (<c>PLAYER_COUNT + 1 == FACTION_ARRAY_SIZE == (int)Player8 + 1 == SLOT_DEFINITIONS_SIZE</c>) and the two-ceiling
    /// policy constants, and asserts <see cref="PlayerCountPolicy"/> DOCUMENTS the 8-player bump — so the raise stays a
    /// single, documented constant edit re-verified by <c>PlayerCountPolicyTests</c> / <c>MatchmakerConfigTests</c> /
    /// <c>MultiFactionExpansionTests.EightFactions…</c>.</para>
    ///
    /// The scan deliberately targets NAMED player-count constants, not every bare <c>4</c>: wire-format byte offsets
    /// (<c>tick(4 LE)</c>), input-delay ticks (<c>INPUT_DELAY = 4</c>), the enum ordinal <c>Player4 = 4</c>, and
    /// story tags (<c>Story 9.4</c>) are NOT player-count declarations and are correctly ignored. The enum ordinal is
    /// pinned by the bump-invariant instead.
    /// </summary>
    public class NoHardcodedPlayerCountTests
    {
        /// <summary>
        /// The SANCTIONED player-count constants, keyed by FULLY-QUALIFIED site (<c>FileName.cs::CONST_NAME</c>) so a
        /// stray same-named constant in an UNRELATED file is NOT excused by a bare-name collision. Adding a new
        /// player-count constant means adding it HERE with its justification — the deliberate friction that keeps stray
        /// player-count literals from accreting.
        /// </summary>
        private static readonly Dictionary<string, string> Allowlist = new()
        {
            ["FactionRegistry.cs::PLAYER_COUNT"]           = "The playable-faction ceiling (8); the sim-side count.",
            ["FactionRegistry.cs::FACTION_ARRAY_SIZE"]     = "Neutral + Player1..Player8 array size (the sanctioned 9).",
            ["PlayerCountPolicy.cs::MpSeatCeiling"]        = "THE transport seat ceiling — the single documented 4→8 bump point.",
            ["ServerChecksumCollector.cs::MaxSlots"]       = "The collector seat ceiling; mirrors MpSeatCeiling.",
            ["StartPositionBridge.cs::MAX_SLOTS"]          = "The editor start-position slot cap; mirrors the seat ceiling.",
            ["ServerTransport.cs::MAX_SLOTS"]              = "The transport slot ceiling (already raised to 8, the bump target).",
            ["EntityPlacer.cs::START_SLOT_CEILING"]        = "The editor start-position ceiling ((int)Faction.Player4).",
            ["EntityPlacer.cs::START_SLOT_MIN"]            = "The editor start-position floor (2).",
            ["PartyState.cs::DefaultCapacity"]             = "Default party capacity (a full 4-player match; architected for 8).",
            ["LoopbackDesyncSelfTest.cs::PlayerCount"]     = "The N=4 real-transport smoke-test peer count.",
            ["MatchmakerConfig.cs::MinPlayers"]            = "The matchmaker minimum (2) — the MP floor.",
            ["DslVarTable.cs::PlayerSlots"]                = "The per-player DSL var-table slot count (= PLAYER_COUNT, 8).",
        };

        // const int Name = N;  /  static readonly int Name = N;  — Name must denote a player-count, and N must be one of
        // the player-count-relevant literals {2 (floor), 4 (current seat ceiling), 8 (documented bump target), 9 (faction
        // array size)}. Matching value 8 (not just the old [49]) is what lets the guard SURVIVE the 4→8 bump — the flipped
        // MpSeatCeiling=8 still matches and stays found — while still catching a NEW hardcoded `= 8` player-count constant.
        // The value set is bounded to these four (not all integers) so the allowlist stays about PLAYER counts, not every
        // sized constant (INVENTORY_SLOTS=6, RingCapacity=256, MaxArrayCapacity=64, …).
        private static readonly Regex PlayerCountConst = new(
            @"(?:const\s+int|static\s+readonly\s+int)\s+(\w*(?:slot|seat|player|peer|capacity|ceiling|faction|lobby|opponent|human)\w*)\s*=\s*(2|4|8|9)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        [Fact]
        public void SourceScan_OnlyAllowlistedPlayerCountConstantsExist()
        {
            string srcDir = SrcDir();
            Assert.True(Directory.Exists(srcDir), $"src directory not found at '{srcDir}' (via [CallerFilePath]).");

            var found = new HashSet<string>();
            var stray = new List<string>();

            foreach (string file in Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories))
            {
                // Strip block + line comments, then collapse ALL whitespace to single spaces, so a declaration split
                // across lines (or oddly spaced) is still seen and comment text can never masquerade as code.
                string blob = StripCommentsAndNormalize(File.ReadAllText(file));
                string fileName = Path.GetFileName(file);

                foreach (Match m in PlayerCountConst.Matches(blob))
                {
                    string name = m.Groups[1].Value;
                    string key  = $"{fileName}::{name}";
                    found.Add(key);
                    if (!Allowlist.ContainsKey(key))
                        stray.Add($"{key} = {m.Groups[2].Value}");
                }
            }

            Assert.True(stray.Count == 0,
                "New hardcoded player-count constant(s) found — route the count through PlayerCountPolicy.MpSeatCeiling " +
                "(seats) or FactionRegistry.PLAYER_COUNT / FACTION_ARRAY_SIZE (faction counts) instead, or add the site to " +
                "the NoHardcodedPlayerCountTests allowlist (keyed File.cs::NAME) WITH a justification if it is genuinely " +
                "sanctioned:\n  " + string.Join("\n  ", stray));

            // Defeat a vacuous pass (a broken regex / wrong dir): every sanctioned SITE must actually be observed.
            foreach (string sanctioned in Allowlist.Keys)
                Assert.True(found.Contains(sanctioned),
                    $"Expected sanctioned player-count constant '{sanctioned}' was not found by the scan — the regex, the " +
                    $"src path, or the constant itself drifted (a vacuous-pass hazard). If the constant was intentionally " +
                    $"renamed/moved/removed, update the allowlist to match.");
        }

        /// <summary>Remove block comments (<c>/* … */</c>) and line comments (<c>// …</c>), then collapse every run of
        /// whitespace to a single space — so the const-declaration regex sees code only, spacing-insensitively, and a
        /// declaration split across lines is still matched.</summary>
        private static string StripCommentsAndNormalize(string text)
        {
            text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline); // block comments
            text = Regex.Replace(text, @"//[^\n]*", " ");                            // line comments
            return Regex.Replace(text, @"\s+", " ");                                 // collapse whitespace
        }

        [Fact]
        public void FactionRegistry_BumpInvariantChain_Holds()
        {
            Assert.Equal(FactionRegistry.FACTION_ARRAY_SIZE, FactionRegistry.PLAYER_COUNT + 1);
            Assert.Equal(FactionRegistry.FACTION_ARRAY_SIZE, (int)Faction.Player8 + 1);
            Assert.Equal(FactionRegistry.FACTION_ARRAY_SIZE, FactionRegistry.SLOT_DEFINITIONS_SIZE);
            Assert.Equal(9, FactionRegistry.FACTION_ARRAY_SIZE);
            Assert.Equal(8, FactionRegistry.PLAYER_COUNT);
        }

        [Fact]
        public void TwoCeilingPolicy_ConstantsAgree()
        {
            Assert.Equal(2, PlayerCountPolicy.MpFloor);
            Assert.Equal(4, PlayerCountPolicy.MpSeatCeiling);
            // The collector's seat ceiling must track the transport seat ceiling (so the 4→8 bump moves them together).
            Assert.Equal(PlayerCountPolicy.MpSeatCeiling, ServerChecksumCollector.MaxSlots);
            // The sim ceiling is deliberately LARGER than the MP seat ceiling (offline 5–8-slot skirmish is playable).
            Assert.True(FactionRegistry.PLAYER_COUNT >= PlayerCountPolicy.MpSeatCeiling);
        }

        [Fact]
        public void PlayerCountPolicy_DocumentsTheEightPlayerBump()
        {
            string src = File.ReadAllText(PolicyFile());
            Assert.Contains("8", src);
            Assert.Contains("bump", src, StringComparison.OrdinalIgnoreCase);
        }

        // ── path helpers (this file lives in godot/ProjectChimera.Sim.Tests/Meta/) ────────────────
        private static string SrcDir([CallerFilePath] string thisFilePath = "") =>
            ResolveFromHere(thisFilePath, "..", "..", "src");

        private static string PolicyFile([CallerFilePath] string thisFilePath = "") =>
            ResolveFromHere(thisFilePath, "..", "..", "src", "Multiplayer", "PlayerCountPolicy.cs");

        private static string ResolveFromHere(string thisFilePath, params string[] segments)
        {
            string dir = Path.GetDirectoryName(thisFilePath)
                         ?? throw new InvalidOperationException("Could not resolve this test's source dir via [CallerFilePath].");
            string[] parts = new string[segments.Length + 1];
            parts[0] = dir;
            Array.Copy(segments, 0, parts, 1, segments.Length);
            return Path.GetFullPath(Path.Combine(parts));
        }
    }
}
