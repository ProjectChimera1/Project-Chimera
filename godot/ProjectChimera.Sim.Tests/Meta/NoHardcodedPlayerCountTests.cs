#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
    /// <para><b>(1b) Aliases count as observed (DW-513).</b> The scan reads TWO declaration shapes, not one: a raw
    /// player-count LITERAL (the regression) and an ALIAS of a sanctioned player-count constant
    /// (<c>= PlayerCountPolicy.MpSeatCeiling</c> — the anti-drift form this guard's own message tells authors to
    /// write). Before DW-513 only the literal shape was visible, so taking the guard's advice at an ALLOWLISTED site
    /// made the scan miss it and fail the vacuous-pass "every sanctioned site was found" assertion below — i.e. the
    /// guard structurally forbade the aliasing its allowlist justifications assumed
    /// (<c>ServerChecksumCollector.MaxSlots</c> "mirrors MpSeatCeiling"), and the mirror was kept honest only by a
    /// hand-written equality assert. Aliases are OBSERVED (they satisfy the vacuous-pass half) but never STRAY: an
    /// alias needs no allowlist entry, because routing through the sanctioned constant is the outcome the guard
    /// wants. An alias of an UNSANCTIONED symbol is ignored entirely — it is not a player-count declaration and this
    /// guard has no opinion on it.</para>
    ///
    /// <para><b>(1c) A declaration is its WHOLE initializer (DW-582).</b> Both halves require the initializer to END
    /// after the thing they recognize — a literal terminated by <c>;</c> (or the multi-declarator <c>,</c>), an alias
    /// terminated by <c>;</c>. An ARITHMETIC initializer is therefore matched by NEITHER half, deliberately: a constant
    /// computed from something else is a DERIVED SIZE, not a restated player count, which is the non-goal named below.
    /// Before DW-582 the literal half ended at a bare <c>\b</c>, so it also matched the leading OPERAND of an
    /// expression and reported <c>Capacity = 2 * EntityWorld.MAX_ENTITIES</c> as a hardcoded player count; the only way
    /// out was to reorder the operands, an arbitrary-looking style rule imposed on unrelated code. The residual blind
    /// spot is accepted and small: a player count FOLDED into arithmetic (<c>= 2 * 4</c>) is now invisible — but the
    /// regression this guard exists to catch, a new count restated by hand, is always written <c>= 4;</c>.</para>
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
            ["ServerChecksumCollector.cs::MaxSlots"]       = "The collector seat ceiling; ALIASES MpSeatCeiling (DW-513).",
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
        //
        // DW-582 — the literal must be the WHOLE initializer, so it is terminated by `;` (or `,`, the multi-declarator
        // separator), NOT by a bare `\b`. Under `\b` the pattern also matched the LEADING OPERAND of an arithmetic
        // initializer, so a sized-buffer constant written literal-first (`Capacity = 2 * EntityWorld.MAX_ENTITIES`) was
        // reported as a hardcoded player count — precisely the RingCapacity class the paragraph above calls a non-goal.
        // It forced authors to reorder the operands (`= EntityWorld.MAX_ENTITIES * 2`) purely to appease the scan, a
        // rule that reads as arbitrary at the call site. This terminator is why the value set can stay bounded to four
        // literals AND be trusted: `= 42` / `= 256` were already excluded, and now so is every `= 4 <op> …` form.
        private static readonly Regex PlayerCountConst = new(
            @"(?:const\s+int|static\s+readonly\s+int)\s+(\w*(?:slot|seat|player|peer|capacity|ceiling|faction|lobby|opponent|human)\w*)\s*=\s*(2|4|8|9)\s*[;,]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // DW-513 — the ALIAS shape: `const int Name = <symbol chain>;` where Name denotes a player count. The
        // initializer must be a bare/dotted identifier chain terminated by `;`, so it can only ever be a plain
        // reference — an EXPRESSION (`= 2 * OTHER`, `= OTHER - 1`) is NOT an alias and is deliberately not matched
        // here. Whether the referenced symbol is SANCTIONED is decided separately (SanctionedSourceTexts); this
        // regex only recognizes the shape.
        private static readonly Regex PlayerCountAliasConst = new(
            @"(?:const\s+int|static\s+readonly\s+int)\s+(\w*(?:slot|seat|player|peer|capacity|ceiling|faction|lobby|opponent|human)\w*)\s*=\s*([A-Za-z_]\w*(?:\s*\.\s*[A-Za-z_]\w*)*)\s*;",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// The constants a player-count site is ALLOWED to alias — the sanctioned single sources of truth this guard
        /// exists to funnel every count through. Written as (owner type, <c>nameof</c>) pairs, NOT strings, so a
        /// rename breaks the BUILD instead of silently rotting the table into names that match nothing (which would
        /// quietly re-open DW-513: the alias half would stop observing anything and the vacuous-pass assertion would
        /// fail with a misleading "the constant drifted" message).
        /// </summary>
        private static readonly (Type Owner, string Member)[] SanctionedSourceSymbols =
        {
            (typeof(PlayerCountPolicy), nameof(PlayerCountPolicy.MpFloor)),
            (typeof(PlayerCountPolicy), nameof(PlayerCountPolicy.MpSeatCeiling)),
            (typeof(FactionRegistry),   nameof(FactionRegistry.PLAYER_COUNT)),
            (typeof(FactionRegistry),   nameof(FactionRegistry.FACTION_ARRAY_SIZE)),
            (typeof(FactionRegistry),   nameof(FactionRegistry.SLOT_DEFINITIONS_SIZE)),
        };

        /// <summary>The accepted alias TEXTS: each sanctioned symbol in both the qualified (<c>Owner.MEMBER</c>) and
        /// the bare (<c>MEMBER</c>, i.e. referenced from inside its own declaring type) form. Ordinal — C# is
        /// case-sensitive, so a differently-cased spelling is a different (non-existent) symbol, not an alias.</summary>
        private static readonly HashSet<string> SanctionedSourceTexts = BuildSanctionedSourceTexts();

        private static HashSet<string> BuildSanctionedSourceTexts()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach ((Type owner, string member) in SanctionedSourceSymbols)
            {
                set.Add(member);
                set.Add($"{owner.Name}.{member}");
            }
            return set;
        }

        /// <summary>One player-count-semantic constant declaration observed by the scan.</summary>
        private readonly struct ConstSite
        {
            public ConstSite(string key, string initializer, bool isLiteral)
            {
                Key = key;
                Initializer = initializer;
                IsLiteral = isLiteral;
            }

            /// <summary>The allowlist key: <c>File.cs::NAME</c>.</summary>
            public string Key { get; }

            /// <summary>The initializer as observed — a literal (<c>4</c>) or the NORMALIZED alias target
            /// (<c>PlayerCountPolicy.MpSeatCeiling</c>).</summary>
            public string Initializer { get; }

            /// <summary>True = a raw player-count LITERAL (stray unless allowlisted — the regression this guards);
            /// false = an ALIAS of a sanctioned constant (always legal, never stray).</summary>
            public bool IsLiteral { get; }
        }

        /// <summary>
        /// The whole scan, over ONE file's text, factored out of the sweep so it can be driven against synthetic
        /// source in a unit test (and so the sweep and those tests can never classify differently). Strips block +
        /// line comments, then collapses ALL whitespace to single spaces, so a declaration split across lines (or
        /// oddly spaced) is still seen and comment text can never masquerade as code.
        /// </summary>
        private static List<ConstSite> ScanSource(string fileName, string sourceText)
        {
            string blob = StripCommentsAndNormalize(sourceText);
            var sites = new List<ConstSite>();

            foreach (Match m in PlayerCountConst.Matches(blob))
                sites.Add(new ConstSite($"{fileName}::{m.Groups[1].Value}", m.Groups[2].Value, isLiteral: true));

            foreach (Match m in PlayerCountAliasConst.Matches(blob))
            {
                string target = NormalizeAliasTarget(m.Groups[2].Value);
                if (SanctionedSourceTexts.Contains(target))
                    sites.Add(new ConstSite($"{fileName}::{m.Groups[1].Value}", target, isLiteral: false));
            }

            return sites;
        }

        /// <summary>Reduce an alias initializer to at most its last two dotted segments (<c>Owner.MEMBER</c>), so a
        /// fully-qualified reference (<c>ProjectChimera.Multiplayer.PlayerCountPolicy.MpSeatCeiling</c>) and the
        /// usual short one compare equal, while an unrelated type's same-named member
        /// (<c>SomethingElse.MpSeatCeiling</c>) still does NOT.</summary>
        private static string NormalizeAliasTarget(string raw)
        {
            string compact = Regex.Replace(raw, @"\s+", "");
            string[] parts = compact.Split('.');
            return parts.Length >= 2 ? $"{parts[parts.Length - 2]}.{parts[parts.Length - 1]}" : compact;
        }

        [Fact]
        public void SourceScan_OnlyAllowlistedPlayerCountConstantsExist()
        {
            string srcDir = SrcDir();
            Assert.True(Directory.Exists(srcDir), $"src directory not found at '{srcDir}' (via [CallerFilePath]).");

            var found = new HashSet<string>();
            var stray = new List<string>();

            foreach (string file in Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories))
            {
                foreach (ConstSite site in ScanSource(Path.GetFileName(file), File.ReadAllText(file)))
                {
                    found.Add(site.Key);
                    // Only a raw LITERAL can be stray; an alias of a sanctioned constant is the desired form (DW-513).
                    if (site.IsLiteral && !Allowlist.ContainsKey(site.Key))
                        stray.Add($"{site.Key} = {site.Initializer}");
                }
            }

            Assert.True(stray.Count == 0,
                "New hardcoded player-count constant(s) found — route the count through PlayerCountPolicy.MpSeatCeiling " +
                "(seats) or FactionRegistry.PLAYER_COUNT / FACTION_ARRAY_SIZE (faction counts) instead — writing " +
                "`= PlayerCountPolicy.MpSeatCeiling` is recognized by this scan and needs no allowlist entry (DW-513) — " +
                "or add the site to the NoHardcodedPlayerCountTests allowlist (keyed File.cs::NAME) WITH a justification " +
                "if it is genuinely sanctioned:\n  " + string.Join("\n  ", stray));

            // Defeat a vacuous pass (a broken regex / wrong dir): every sanctioned SITE must actually be observed —
            // as a literal OR as an alias of a sanctioned constant (DW-513: aliasing must never make a site vanish).
            foreach (string sanctioned in Allowlist.Keys)
                Assert.True(found.Contains(sanctioned),
                    $"Expected sanctioned player-count constant '{sanctioned}' was not found by the scan — the regexes, the " +
                    $"src path, or the constant itself drifted (a vacuous-pass hazard). If the constant was intentionally " +
                    $"renamed/moved/removed, update the allowlist to match.");
        }

        /// <summary>
        /// DW-513, over the REAL source: the collector seat ceiling must be observed as an ALIAS of the sanctioned
        /// ceiling, not as a restated literal. This is the fix's regression proof in both directions — it fails if the
        /// alias shape stops being recognized (the guard would then structurally force the literal back, which is the
        /// defect), and it fails if someone re-literalizes the constant (the drift the alias exists to prevent).
        /// </summary>
        [Fact]
        public void SourceScan_CollectorSeatCeiling_IsObservedAsAnAliasOfTheSanctionedCeiling()
        {
            string file = Path.Combine(SrcDir(), "Multiplayer", "Server", "ServerChecksumCollector.cs");
            Assert.True(File.Exists(file), $"ServerChecksumCollector.cs not found at '{file}'.");

            List<ConstSite> sites = ScanSource("ServerChecksumCollector.cs", File.ReadAllText(file))
                .FindAll(s => s.Key == "ServerChecksumCollector.cs::MaxSlots");

            Assert.True(sites.Count == 1,
                $"Expected exactly one ServerChecksumCollector.cs::MaxSlots declaration site, saw {sites.Count}.");
            Assert.False(sites[0].IsLiteral,
                "ServerChecksumCollector.MaxSlots is a restated player-count LITERAL again — it must alias " +
                "PlayerCountPolicy.MpSeatCeiling so the documented 4→8 seat bump is a single constant edit (DW-513).");
            Assert.Equal("PlayerCountPolicy.MpSeatCeiling", sites[0].Initializer);
        }

        /// <summary>DW-513 — the alias shape is recognized in every spelling a real call site can use: qualified, bare
        /// (from inside the declaring type), fully namespace-qualified, and for a faction-count source too.</summary>
        [Theory]
        [InlineData("PlayerCountPolicy.MpSeatCeiling", "PlayerCountPolicy.MpSeatCeiling")]
        [InlineData("MpSeatCeiling", "MpSeatCeiling")]
        [InlineData("ProjectChimera.Multiplayer.PlayerCountPolicy.MpSeatCeiling", "PlayerCountPolicy.MpSeatCeiling")]
        [InlineData("FactionRegistry.FACTION_ARRAY_SIZE", "FactionRegistry.FACTION_ARRAY_SIZE")]
        public void Scan_RecognizesAnAliasOfASanctionedConstant(string initializer, string expectedTarget)
        {
            List<ConstSite> sites = ScanSource("Fake.cs", $"public const int MaxSlots = {initializer};");

            Assert.True(sites.Count == 1, $"Expected one site for `= {initializer};`, saw {sites.Count}.");
            Assert.Equal("Fake.cs::MaxSlots", sites[0].Key);
            Assert.False(sites[0].IsLiteral);
            Assert.Equal(expectedTarget, sites[0].Initializer);
        }

        /// <summary>DW-513 — an alias site is OBSERVED but never STRAY: routing through the sanctioned constant is the
        /// outcome the guard asks for, so it must not need an allowlist entry to stay green.</summary>
        [Fact]
        public void Scan_AnAliasIsNeverStray_EvenWhenNotAllowlisted()
        {
            List<ConstSite> sites = ScanSource("Unlisted.cs", "static readonly int PeerCeiling = PlayerCountPolicy.MpSeatCeiling;");

            Assert.True(sites.Count == 1, $"Expected one site, saw {sites.Count}.");
            Assert.False(Allowlist.ContainsKey(sites[0].Key), "fixture assumption: this key is deliberately unlisted.");
            Assert.False(sites[0].IsLiteral, "An alias of a sanctioned constant must not be classified as a literal.");
        }

        /// <summary>The scan must have NO opinion on a symbolic initializer that is not a sanctioned player-count
        /// source — matching those would make the alias half a rubber stamp (and would drag sized-buffer constants
        /// into a guard that explicitly documents them as a non-goal).</summary>
        [Theory]
        [InlineData("EntityWorld.MAX_ENTITIES")]
        [InlineData("SomethingElse.MpSeatCeiling")]
        [InlineData("MPSEATCEILING")]
        public void Scan_IgnoresAnAliasOfAnUnsanctionedSymbol(string initializer)
        {
            List<ConstSite> sites = ScanSource("Fake.cs", $"private const int SlotCapacity = {initializer};");

            Assert.True(sites.Count == 0, $"`= {initializer};` must not be read as a player-count declaration.");
        }

        /// <summary>The literal half is untouched by the DW-513 alias work: a raw player-count literal at an
        /// unlisted site is still observed AS a literal, i.e. still reported stray by the sweep above.</summary>
        [Fact]
        public void Scan_StillFlagsARawPlayerCountLiteral()
        {
            List<ConstSite> sites = ScanSource("Fake.cs", "private const int LobbySeatCap = 4;");

            Assert.True(sites.Count == 1, $"Expected one site, saw {sites.Count}.");
            Assert.True(sites[0].IsLiteral, "A raw `= 4` player-count constant must still classify as a LITERAL.");
            Assert.Equal("4", sites[0].Initializer);
            Assert.False(Allowlist.ContainsKey(sites[0].Key), "fixture assumption: this key is deliberately unlisted.");
        }

        /// <summary>
        /// DW-582 — the defect: an ARITHMETIC initializer whose LEADING operand happens to be a player-count literal is
        /// a derived SIZE, not a restated player count, and must be matched by neither half. Every operator form is
        /// covered, not just <c>*</c>: the rule is "the literal is the whole initializer", so nothing that continues
        /// into an expression can be mistaken for a count.
        /// </summary>
        [Theory]
        [InlineData("public const int Capacity = 2 * EntityWorld.MAX_ENTITIES;")]   // the real hit (DslSimEventFeed)
        [InlineData("private const int SlotBuffer = 4 * MaxRows;")]
        [InlineData("static readonly int PeerHeadroom = 8 * ChunkSize;")]
        [InlineData("private const int FactionMaskWidth = 2 << ShiftBits;")]
        [InlineData("private const int LobbyPadding = 4 + Overhead;")]
        [InlineData("private const int SeatStride = 9 - Slack;")]
        [InlineData("private const int PlayerBytes = 4 / Divisor;")]
        public void Scan_IgnoresASizedBufferExpressionWithALeadingPlayerCountLiteral(string declaration)
        {
            List<ConstSite> sites = ScanSource("Fake.cs", declaration);

            Assert.True(sites.Count == 0,
                $"`{declaration}` is a sized-buffer constant, not a hardcoded player count — the scan must not " +
                $"classify it (DW-582). Saw {sites.Count} site(s).");
        }

        /// <summary>
        /// The pre-DW-582 literal pattern, frozen verbatim as history. Its only job is to prove, inside the suite, that
        /// DW-582's defect was REAL and that the current regex is what removes it — so a revert to the `\b`-terminated
        /// form cannot pass unnoticed.
        /// </summary>
        private static readonly Regex PreDw582LiteralConst = new(
            @"(?:const\s+int|static\s+readonly\s+int)\s+(\w*(?:slot|seat|player|peer|capacity|ceiling|faction|lobby|opponent|human)\w*)\s*=\s*(2|4|8|9)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>DW-582 — the change is SURGICAL: the old `\b`-terminated pattern and the current one disagree on
        /// exactly the expression shape and agree everywhere else. Guards both directions at once — a revert re-fails
        /// the first assert, and an over-tightening that stopped seeing plain literals re-fails the last.</summary>
        [Fact]
        public void LiteralRegex_DiffersFromThePreDw582PatternOnlyOnExpressionInitializers()
        {
            const string sizedBuffer = "public const int Capacity = 2 * EntityWorld.MAX_ENTITIES;";
            const string plainLiteral = "private const int LobbySeatCap = 4;";

            // Hoisted out of the asserts so xUnit2008 ("use Assert.Matches") does not fire on an inline IsMatch — these
            // are agreement/disagreement checks between two patterns, not string-matches on one.
            bool oldOnBuffer = PreDw582LiteralConst.IsMatch(sizedBuffer);
            bool newOnBuffer = PlayerCountConst.IsMatch(sizedBuffer);
            bool oldOnLiteral = PreDw582LiteralConst.IsMatch(plainLiteral);
            bool newOnLiteral = PlayerCountConst.IsMatch(plainLiteral);

            Assert.True(oldOnBuffer,
                "fixture assumption: the pre-DW-582 pattern is the one that false-positived on a sized-buffer const.");
            Assert.False(newOnBuffer,
                "The current literal regex still matches a sized-buffer expression — DW-582 has been reverted.");

            Assert.True(oldOnLiteral, "fixture assumption: both patterns are supposed to see a plain `= 4;`.");
            Assert.True(newOnLiteral,
                "The DW-582 tightening must not cost the guard its actual target: a plain `= 4;` player-count literal.");
        }

        /// <summary>DW-582 — the tightening must not blunt the literal half. Every sanctioned value, in every spelling a
        /// real declaration can use (trailing comment, split across lines, first of a multi-declarator field), is still
        /// observed as a LITERAL; a non-player-count value is still ignored.</summary>
        [Theory]
        [InlineData("private const int LobbySeatCap = 2;", "2")]
        [InlineData("private const int LobbySeatCap = 4;", "4")]
        [InlineData("private const int LobbySeatCap = 8;", "8")]
        [InlineData("private const int LobbySeatCap = 9;", "9")]
        [InlineData("private const int LobbySeatCap = 4; // the seat cap", "4")]
        [InlineData("private const int LobbySeatCap =\n            4;", "4")]
        [InlineData("private const int LobbySeatCap = 4, Unrelated = 3;", "4")]
        [InlineData("static readonly int LobbySeatCap = 8;", "8")]
        public void Scan_StillFlagsAWholeInitializerLiteral_InEverySpelling(string declaration, string expected)
        {
            List<ConstSite> sites = ScanSource("Fake.cs", declaration);

            Assert.True(sites.Count == 1, $"Expected one site for `{declaration}`, saw {sites.Count}.");
            Assert.True(sites[0].IsLiteral, "A whole-initializer player-count literal must classify as a LITERAL.");
            Assert.Equal(expected, sites[0].Initializer);
        }

        /// <summary>A value outside the bounded player-count set stays invisible — including one whose FIRST DIGIT is a
        /// sanctioned value, the near-miss the terminator must not admit.</summary>
        [Theory]
        [InlineData("private const int LobbySeatCap = 6;")]
        [InlineData("private const int LobbySeatCap = 42;")]
        [InlineData("private const int LobbySeatCap = 256;")]
        public void Scan_IgnoresANonPlayerCountValue(string declaration)
        {
            Assert.True(ScanSource("Fake.cs", declaration).Count == 0,
                $"`{declaration}` is not a player-count declaration and must not be classified.");
        }

        /// <summary>
        /// DW-582 over the REAL source, at the site that actually forced the workaround: <c>DslSimEventFeed.Capacity</c>
        /// is a sized buffer whose name trips the player-count NAME filter. The scan must stay silent on it as written,
        /// AND on the literal-first spelling of the same declaration — the natural ordering the old regex rejected, and
        /// the whole point of the fix (the operand order is now the author's choice, not the guard's).
        /// </summary>
        [Fact]
        public void SourceScan_TheRealSizedBufferConstant_StaysUnflaggedInEitherOperandOrder()
        {
            string file = Path.Combine(SrcDir(), "Core", "DslSimEventFeed.cs");
            Assert.True(File.Exists(file), $"DslSimEventFeed.cs not found at '{file}'.");

            string source = File.ReadAllText(file);
            Assert.True(ScanSource("DslSimEventFeed.cs", source).Count == 0,
                "DslSimEventFeed declares no player count — the scan must classify nothing in it (DW-582).");

            Match decl = Regex.Match(StripCommentsAndNormalize(source), @"const\s+int\s+Capacity\s*=\s*([^;]+);");
            Assert.True(decl.Success,
                "DW-582's live anchor moved: DslSimEventFeed no longer declares `const int Capacity`. Repoint this test " +
                "at another sized-buffer constant whose name trips the player-count name filter, or delete it — the " +
                "shape-level teeth live in Scan_IgnoresASizedBufferExpressionWithALeadingPlayerCountLiteral.");

            string[] operands = decl.Groups[1].Value.Split('*');
            Assert.True(operands.Length == 2, $"expected a two-operand product, saw '{decl.Groups[1].Value.Trim()}'.");

            // Order-agnostic: whichever operand is the literal is put FIRST, so this keeps testing the rejected
            // spelling even if a future author flips the real declaration around.
            string literal = operands[0].Trim(), symbol = operands[1].Trim();
            if (!Regex.IsMatch(literal, @"^\d+$")) (literal, symbol) = (symbol, literal);
            Assert.Matches(@"^(2|4|8|9)$", literal);

            string literalFirst = $"public const int Capacity = {literal} * {symbol};";
            Assert.True(ScanSource("DslSimEventFeed.cs", literalFirst).Count == 0,
                $"`{literalFirst}` was classified as a hardcoded player count — DW-582's false positive is back, and " +
                $"authors are again forced to order the literal last to appease the scan.");
        }

        /// <summary>
        /// DW-789 — the two halves must never double-classify one declaration. The literal and alias regexes are
        /// disjoint by construction (one requires a digit after <c>=</c>, the other an identifier), and this pins that
        /// over the REAL tree: no file may yield the same site key twice, which is what a literal counted as an alias
        /// (or vice versa) would look like to the sweep.
        /// </summary>
        [Fact]
        public void SourceScan_NoDeclarationIsClassifiedTwice()
        {
            var collisions = new List<string>();
            int observed = 0;

            foreach (string file in Directory.GetFiles(SrcDir(), "*.cs", SearchOption.AllDirectories))
            {
                var seen = new Dictionary<string, bool>();
                foreach (ConstSite site in ScanSource(Path.GetFileName(file), File.ReadAllText(file)))
                {
                    observed++;
                    if (seen.TryGetValue(site.Key, out bool priorWasLiteral))
                        collisions.Add($"{site.Key} (literal={priorWasLiteral} and literal={site.IsLiteral})");
                    else
                        seen[site.Key] = site.IsLiteral;
                }
            }

            Assert.True(collisions.Count == 0,
                "A player-count declaration was classified more than once — the literal and alias halves overlap " +
                "(DW-789):\n  " + string.Join("\n  ", collisions));
            Assert.True(observed > 0, "the scan observed nothing at all — a vacuous pass (bad src path or dead regex).");
        }

        /// <summary>Anti-rot for the sanctioned-source table: every entry must still resolve to a PUBLIC STATIC int on
        /// its owner — i.e. a constant another file can actually alias. <c>nameof</c> already breaks the build on a
        /// rename; this catches the subtler drifts (made private, made instance, retyped) that would silently shrink
        /// the set of aliases the scan can see.</summary>
        [Fact]
        public void SanctionedSourceSymbols_AllResolveToPublicStaticIntConstants()
        {
            Assert.NotEmpty(SanctionedSourceSymbols);

            foreach ((Type owner, string member) in SanctionedSourceSymbols)
            {
                FieldInfo? field = owner.GetField(member, BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                Assert.True(field != null, $"Sanctioned source '{owner.Name}.{member}' no longer resolves to a public static field.");
                Assert.Equal(typeof(int), field!.FieldType);
                Assert.True(SanctionedSourceTexts.Contains($"{owner.Name}.{member}"), "qualified form missing from the accepted texts");
                Assert.True(SanctionedSourceTexts.Contains(member), "bare form missing from the accepted texts");
            }
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
            // Since DW-513 that is STRUCTURAL — MaxSlots aliases MpSeatCeiling — so this assert is now the backstop
            // that turns red if the alias is ever replaced by a copied literal that drifts, not the only thing
            // holding the two together. SourceScan_CollectorSeatCeiling_… pins the alias shape itself.
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
