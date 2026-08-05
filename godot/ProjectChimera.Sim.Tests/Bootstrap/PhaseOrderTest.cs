#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using ProjectChimera.Core.Bootstrap;
using Xunit;

namespace ProjectChimera.Sim.Tests.Bootstrap
{
    /// <summary>
    /// Pins the canonical composition-root phase order that <see cref="ScenePhaseRunner"/> asserts at startup
    /// (Story 1.8c / AR-3 constraint C1, enumerated by AR-35). The order IS the contract — a hidden presentation
    /// timing dependency surfaces as an NRE the golden suite cannot catch, so these tests FAIL loudly the moment
    /// <see cref="ScenePhaseOrder.Canonical"/> drifts. Mirrors the sim-side <c>SystemOrderTest</c> precedent but
    /// uses Godot-free <see cref="string"/> phase names: a concrete presentation phase type is NEVER referenced
    /// here (that would drag GodotSharp into this Godot-free assembly and break <c>GodotFreeBoundaryTest</c>).
    ///
    /// <para><b>DW-233 hardening.</b> Two holes were open in the original pin, and both widened as the sequence
    /// grew to 41 phases:</para>
    /// <list type="number">
    ///   <item><description><b>Duplicate canonical names.</b> <see cref="ScenePhaseRunner.AssertOrder"/> compares
    ///   POSITIONALLY BY NAME. If the same name ever appeared at two positions, two distinct concrete phases
    ///   sharing that name could be swapped for each other and the runner would still pass — the order contract
    ///   silently loses its discriminating power exactly where the name repeats. Nothing asserted uniqueness, and
    ///   the elementwise pin below cannot notice: a duplicate mirrored into <see cref="ExpectedOrder"/> matches
    ///   happily. <see cref="Canonical_HasNoDuplicateNames"/> closes it.</description></item>
    ///   <item><description><b>Concrete-phase <c>Name</c> drift.</b> Every test here runs
    ///   <see cref="StubPhase"/>, so it proves what the STUBS report, never what <c>HudPhase.Name</c> actually
    ///   returns. A typo'd concrete literal (<c>"HUD"</c> for <c>"Hud"</c>) sails past Tier-1 and only blows up at
    ///   startup, in-engine. The concrete phases live under <c>Bootstrap/Phases/</c>, which
    ///   <c>SimSources.props</c> deliberately <c>&lt;Compile Remove&gt;</c>s (they hold CanvasLayer/Node handles),
    ///   so they cannot be referenced from this assembly — a SOURCE scan is the only Godot-free way to read them.
    ///   <see cref="ConcretePhaseClasses_DeclareExactlyTheCanonicalNames"/> is that scan, using the same
    ///   <see cref="CallerFilePathAttribute"/> tree location as <c>NullableContextHygieneTests</c> /
    ///   <c>DependencyHygieneTests</c>. It removes the need for a Tier-2 GdUnit4 companion that instantiates the
    ///   real phases.</description></item>
    /// </list>
    /// </summary>
    public class PhaseOrderTest
    {
        /// <summary>
        /// The canonical order, hardcoded here INDEPENDENTLY of <see cref="ScenePhaseOrder.Canonical"/> so that a
        /// drift in either one fails this test. Derived from <c>MainScene._Ready</c> at Story 1.8c.
        /// </summary>
        private static readonly string[] ExpectedOrder =
        {
            "Settings", "Audio", "GameState", "Lighting", "Terrain", "Navigation", "Camera",
            "Rendering", "Hud", "CustomHudOverlay", "ObjectiveOverlay", "TriggerDebugOverlay", "Minimap", "MatchAlert", "TerrainBrush", "ScenarioLoad", "RegionTool", "PathabilityTool",
            "CameraTool", "WaterTool", "FactionVisuals",
            "FlowFieldInit", "WinConditionUi", "GameOverOverlay", "Multiplayer", "ReplayStatus",
            "ContentBrowser", "ReplayBrowser", "MainMenu", "TriggerEditor", "DslGraphEditor", "MapGenerator", "AbilityEditor",
            "UnitCard", "ItemCard", "BuildingCard", "TechTree", "PersistenceManifest", "HeroPicker",
            "FactionDefiner", "Onboarding",
        };

        /// <summary>A Godot-free stub phase that appends its name to a shared log when run (call-order proof).</summary>
        private sealed class StubPhase : ISetupPhase
        {
            private readonly List<string> _log;
            public StubPhase(string name, List<string> log) { Name = name; _log = log; }
            public string Name { get; }
            public void Run() => _log.Add(Name);
        }

        /// <summary>Build one stub per canonical phase, in canonical order, sharing the given run-log.</summary>
        private static List<ISetupPhase> CanonicalStubs(List<string> log)
        {
            var phases = new List<ISetupPhase>(ScenePhaseOrder.Canonical.Length);
            foreach (string name in ScenePhaseOrder.Canonical)
                phases.Add(new StubPhase(name, log));
            return phases;
        }

        [Fact]
        public void PhaseOrder_IsTheCanonicalSequence_InExactOrder()
        {
            Assert.Equal(ExpectedOrder.Length, ScenePhaseOrder.Canonical.Length);
            for (int i = 0; i < ExpectedOrder.Length; i++)
                Assert.Equal(ExpectedOrder[i], ScenePhaseOrder.Canonical[i]);
        }

        [Fact]
        public void Runner_RunsPhasesInCanonicalOrder_WhenCorrect()
        {
            var log = new List<string>();
            new ScenePhaseRunner(CanonicalStubs(log)).Run();
            Assert.Equal(ScenePhaseOrder.Canonical, log.ToArray());
        }

        [Fact]
        public void Runner_Throws_WhenAPhaseIsReordered()
        {
            var log = new List<string>();
            List<ISetupPhase> phases = CanonicalStubs(log);
            (phases[0], phases[1]) = (phases[1], phases[0]); // swap first two out of order

            Assert.Throws<InvalidOperationException>(() => new ScenePhaseRunner(phases).Run());
            Assert.Empty(log); // AssertOrder must throw BEFORE any phase body runs
        }

        [Fact]
        public void Runner_Throws_WhenAPhaseIsRemoved()
        {
            var log = new List<string>();
            List<ISetupPhase> phases = CanonicalStubs(log);
            phases.RemoveAt(phases.Count - 1);

            Assert.Throws<InvalidOperationException>(() => new ScenePhaseRunner(phases).AssertOrder());
        }

        [Fact]
        public void Runner_Throws_WhenAPhaseIsAdded()
        {
            var log = new List<string>();
            List<ISetupPhase> phases = CanonicalStubs(log);
            phases.Add(new StubPhase("Extra", log));

            Assert.Throws<InvalidOperationException>(() => new ScenePhaseRunner(phases).AssertOrder());
        }

        // ── DW-233: the two holes the stub-only pin above cannot see ─────────────────────────────

        /// <summary>
        /// DW-233 (hole 1). A phase NAME is the phase's whole identity to <see cref="ScenePhaseRunner"/>, which
        /// matches positionally by name — so a name that appears twice makes those two positions
        /// interchangeable: swap the concrete phases and the startup assert still passes while <c>_Ready</c> runs
        /// them in the wrong order. Asserted on BOTH arrays: the independently-hardcoded
        /// <see cref="ExpectedOrder"/> is checked too, so "fix the red order test by mirroring the duplicate"
        /// cannot launder a duplicate into the contract.
        /// </summary>
        [Fact]
        public void Canonical_HasNoDuplicateNames()
        {
            Assert.Equal(Array.Empty<string>(), Duplicates(ScenePhaseOrder.Canonical));
            Assert.Equal(Array.Empty<string>(), Duplicates(ExpectedOrder));
        }

        /// <summary>
        /// DW-233 (hole 1, corollary). A blank or whitespace-only phase name collides with every other blank name
        /// under the positional match AND renders the runner's expected-vs-actual diff unreadable — the one
        /// diagnostic AR-3/C1 promises when the order drifts.
        /// </summary>
        [Fact]
        public void Canonical_NamesAreNonBlank()
        {
            for (int i = 0; i < ScenePhaseOrder.Canonical.Length; i++)
                Assert.False(string.IsNullOrWhiteSpace(ScenePhaseOrder.Canonical[i]),
                    $"ScenePhaseOrder.Canonical[{i}] is blank. A phase name is its identity to ScenePhaseRunner's " +
                    "positional match (and the only label in its drift diagnostic) — it must be a real name.");
        }

        /// <summary>
        /// DW-233 (hole 2). Reads the <c>Name</c> literal every concrete <c>ISetupPhase</c> under
        /// <c>godot/src/Core/Bootstrap/Phases/</c> actually declares and asserts that set is EXACTLY
        /// <see cref="ScenePhaseOrder.Canonical"/> — no drifted spelling, no phase missing a class, no orphan
        /// class, and no two classes claiming the same name (which would defeat the positional assert exactly as
        /// a duplicated canonical entry does). A source scan rather than a type scan because those classes are
        /// Godot-coupled and <c>SimSources.props</c> excludes them from this Godot-free assembly by design.
        /// Ordering is NOT checked here — <c>MainScene._Ready</c>'s literal owns that, and
        /// <see cref="ScenePhaseRunner.AssertOrder"/> plus the pins above already guard it.
        /// </summary>
        [Fact]
        public void ConcretePhaseClasses_DeclareExactlyTheCanonicalNames()
        {
            IReadOnlyDictionary<string, List<string>> declared = ScanConcretePhaseNames(PhasesRoot());

            string[] duplicated = declared
                .Where(kv => kv.Value.Count > 1)
                .Select(kv => $"'{kv.Key}' declared by {string.Join(" + ", kv.Value)}")
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();
            Assert.True(duplicated.Length == 0,
                $"Two or more concrete setup phases declare the SAME Name (DW-233):{Environment.NewLine}  " +
                $"{string.Join(Environment.NewLine + "  ", duplicated)}{Environment.NewLine}" +
                "ScenePhaseRunner matches positionally by name, so same-named phases are interchangeable to it — " +
                "_Ready could run them in the wrong order with the startup assert still green. Give each phase a " +
                "unique Name and add it to ScenePhaseOrder.Canonical.");

            var canonical = new HashSet<string>(ScenePhaseOrder.Canonical, StringComparer.Ordinal);

            string[] orphans = declared.Keys.Where(n => !canonical.Contains(n))
                .OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.True(orphans.Length == 0,
                "Concrete setup phase(s) declare a Name that is NOT in ScenePhaseOrder.Canonical (DW-233): " +
                $"{string.Join(", ", orphans.Select(n => $"'{n}' ({string.Join(",", declared.TryGetValue(n, out List<string>? f) ? f : new List<string>())})"))}. " +
                "Either the concrete literal drifted from the canonical spelling, or a new phase was written " +
                "without being added to ScenePhaseOrder.Canonical (+ ExpectedOrder + the _Ready literal). " +
                "Until it is, ScenePhaseRunner throws at startup — this test is the Tier-1 eye that sees it first.");

            string[] unbacked = canonical.Where(n => !declared.ContainsKey(n))
                .OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.True(unbacked.Length == 0,
                "Canonical phase name(s) have no concrete ISetupPhase class declaring them (DW-233): " +
                $"{string.Join(", ", unbacked.Select(n => $"'{n}'"))}. " +
                "The canonical entry drifted from the class that implements it, or the class was deleted/renamed " +
                "without reconciling ScenePhaseOrder.Canonical.");
        }

        // ── DW-233 scanning helpers ──────────────────────────────────────────────────────────────

        /// <summary>Every value that occurs more than once, in first-occurrence order, formatted with its count.</summary>
        private static string[] Duplicates(IReadOnlyList<string> names) =>
            names.Select((n, i) => (Name: n, Index: i))
                 .GroupBy(x => x.Name, StringComparer.Ordinal)
                 .Where(g => g.Count() > 1)
                 .OrderBy(g => g.Min(x => x.Index))
                 .Select(g => $"'{g.Key}' x{g.Count()} at [{string.Join(",", g.Select(x => x.Index))}]")
                 .ToArray();

        /// <summary>A class declaration whose base list names <c>ISetupPhase</c> (possibly among other bases).</summary>
        private static readonly Regex PhaseClassDeclaration = new(
            @"\bclass\s+(?<class>[A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^<>]*>)?\s*:\s*(?<bases>[^{\r\n]*)",
            RegexOptions.Compiled);

        /// <summary>
        /// The <c>Name</c> property's literal, in either house form: <c>public string Name =&gt; "X";</c> (used by
        /// all 41 phases today) or <c>public string Name { get; } = "X";</c>. A computed/interpolated Name is
        /// deliberately unreadable to this scan and reported as such rather than silently skipped.
        /// </summary>
        private static readonly Regex NameLiteral = new(
            @"public\s+string\s+Name\s*(?:=>|\{\s*get;\s*\}\s*=)\s*""(?<name>[^""]*)""\s*;",
            RegexOptions.Compiled);

        /// <summary>
        /// Map every concrete <c>ISetupPhase</c>'s declared <c>Name</c> literal to the file(s) declaring it. Each
        /// phase file must hold exactly one <c>ISetupPhase</c> class and exactly one readable <c>Name</c> literal;
        /// anything else fails loudly, because a scan that quietly fails to parse a file is a guard that quietly
        /// stops guarding.
        /// </summary>
        private static IReadOnlyDictionary<string, List<string>> ScanConcretePhaseNames(string phasesRoot)
        {
            var byName = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            foreach (string path in Directory
                         .EnumerateFiles(phasesRoot, "*.cs", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                string file = Path.GetFileName(path);
                string code = StripComments(File.ReadAllText(path));

                string[] phaseClasses = PhaseClassDeclaration.Matches(code)
                    .Where(m => Regex.IsMatch(m.Groups["bases"].Value, @"(^|[\s,:])ISetupPhase(\s|,|\{|$)"))
                    .Select(m => m.Groups["class"].Value)
                    .ToArray();
                if (phaseClasses.Length == 0) continue;   // SceneContext / binders / resolvers live here too.

                Assert.True(phaseClasses.Length == 1,
                    $"'{file}' declares {phaseClasses.Length} ISetupPhase classes ({string.Join(", ", phaseClasses)}). " +
                    "The DW-233 Name guard attributes one Name literal per file — split them into one class per " +
                    "file (the house layout) or extend ScanConcretePhaseNames to scope by class body.");

                string[] literals = NameLiteral.Matches(code).Select(m => m.Groups["name"].Value).ToArray();
                Assert.True(literals.Length == 1,
                    $"'{file}' implements ISetupPhase but the DW-233 guard found {literals.Length} readable Name " +
                    "literals in it (expected exactly 1). Declare it as `public string Name => \"X\";` with a " +
                    "constant literal — a computed Name cannot be pinned against ScenePhaseOrder.Canonical at Tier-1, " +
                    "which is the whole point of this guard.");

                if (!byName.TryGetValue(literals[0], out List<string>? files))
                    byName[literals[0]] = files = new List<string>();
                files.Add(file);
            }

            Assert.True(byName.Count > 0,
                $"The DW-233 guard found no concrete ISetupPhase classes under '{phasesRoot}'. The scan is broken " +
                "or the phases moved — a silently empty scan would let every concrete Name drift unguarded.");

            return byName;
        }

        /// <summary>
        /// Blank out line and block comments (preserving newlines) so a <c>Name =&gt; "..."</c> written inside a
        /// doc comment — several phase files quote the contract in their summaries — is never read as a real
        /// declaration. String literals are deliberately PRESERVED: they are what the scan reads.
        /// </summary>
        private static string StripComments(string text)
        {
            var sb = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') { sb.Append(' '); i++; }
                    continue;
                }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    sb.Append("  "); i += 2;
                    while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                    {
                        sb.Append(text[i] == '\n' ? '\n' : ' '); i++;
                    }
                    if (i < text.Length) { sb.Append("  "); i += 2; }
                    continue;
                }
                // Copy string/char literals verbatim so a '//' inside one cannot open a phantom comment.
                if (c == '"' || c == '\'')
                {
                    char quote = c;
                    sb.Append(c); i++;
                    while (i < text.Length && text[i] != quote && text[i] != '\n')
                    {
                        if (text[i] == '\\' && i + 1 < text.Length) { sb.Append(text[i]).Append(text[i + 1]); i += 2; continue; }
                        sb.Append(text[i]); i++;
                    }
                    if (i < text.Length && text[i] == quote) { sb.Append(text[i]); i++; }
                    continue;
                }

                sb.Append(c); i++;
            }
            return sb.ToString();
        }

        /// <summary>
        /// <c>godot/src/Core/Bootstrap/Phases</c> — two directories up from this file
        /// (…/ProjectChimera.Sim.Tests/Bootstrap/), then down the shipping tree. Located via
        /// <see cref="CallerFilePathAttribute"/>, the same portable mechanism <c>NullableContextHygieneTests</c>
        /// and <c>DependencyHygieneTests</c> use, so there is no hardcoded absolute path.
        /// </summary>
        private static string PhasesRoot([CallerFilePath] string thisFilePath = "")
        {
            string dir = Path.GetDirectoryName(thisFilePath)
                         ?? throw new InvalidOperationException("Could not resolve this test's source directory via [CallerFilePath].");
            string root = Path.GetFullPath(Path.Combine(dir, "..", "..", "src", "Core", "Bootstrap", "Phases"));
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException(
                    "The DW-233 concrete-phase Name guard could not locate the phase source tree. Resolved path: " +
                    $"'{root}'. This path is derived from [CallerFilePath]; if the project layout moved, update this guard.");
            return root;
        }
    }
}
