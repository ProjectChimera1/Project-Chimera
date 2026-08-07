#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Meta
{
    /// <summary>
    /// DW-648 — the POSITION-WRITER AUDIT, made permanent.
    ///
    /// <para><b>The defect.</b> The deterministic blocked-cell rejection (Story 6.5, made SWEPT by DW-147 and
    /// confinement-correct by DW-148) lived INLINE in <c>MovementSystem</c>. It therefore protected exactly ONE writer
    /// of <c>EntityWorld.Position</c>. Any other code that assigned a position and meant "this entity moved" bypassed
    /// both the endpoint check and the sweep, and could deposit a unit inside — or across — a blocked region with
    /// nothing in the suite able to see it. The ledger's prescribed closure was: audit every <c>Position</c> writer,
    /// route the ones that represent MOVEMENT through a shared checked-step helper, and leave authored/scenario
    /// placement to the <c>ScenarioValidator</c> diagnostic.</para>
    ///
    /// <para><b>Why a source scan.</b> The audit itself is a one-time act; what rots is the INVARIANT it establishes.
    /// A future writer added to a spawn path, an ability effect or a persistence restore is invisible to every
    /// behavioural test in the suite (there is no behaviour to regress — the writer is NEW). Only a scan of the
    /// shipping tree can notice that a position write appeared somewhere the audit never sanctioned. This is the same
    /// mechanism <c>NoHardcodedPlayerCountTests</c> and <c>NullableContextHygieneTests</c> use, and the tree is located
    /// portably via <see cref="CallerFilePathAttribute"/> so it runs unchanged on a CI checkout.</para>
    ///
    /// <para><b>What turns it red.</b> (1) a NEW file writing <c>Position[…] =</c>; (2) a NEW write inside an
    /// already-sanctioned file; (3) the sweep <c>PathabilityGrid.IsBlockedOnSegmentOutside</c> being called from
    /// anywhere except the shared <c>CheckedStep</c> helper (i.e. a hand-copied rejection); (4) <c>MovementSystem</c>
    /// no longer routing through <c>CheckedStep</c> at all. Each is a real way the DW-648 hole reopens.</para>
    /// </summary>
    public class PositionWriterGuardTests
    {
        /// <summary>
        /// The SANCTIONED writers of a <c>Position[…]</c> SoA slot across <c>godot/src/**</c>, keyed by repo-relative
        /// path, carrying the exact write COUNT and the reason that file is allowed to write a position WITHOUT
        /// routing through <see cref="ProjectChimera.Navigation.CheckedStep"/>.
        ///
        /// <para>Adding a write means editing this table — the deliberate friction DW-648 asks for. Before doing so,
        /// answer the audit's question: does the new write represent an entity MOVING (⇒ route it through
        /// <c>CheckedStep.Resolve</c> and leave the count here alone), or authored/restored PLACEMENT (⇒ raise the
        /// count and say why the pathability sweep must not apply)?</para>
        /// </summary>
        private static readonly Dictionary<string, (int Count, string Why)> Sanctioned = new(StringComparer.Ordinal)
        {
            ["Navigation/MovementSystem.cs"] = (1,
                "THE per-tick unit MOVEMENT integrator — the one write that represents a unit moving, and it is " +
                "routed through CheckedStep.Resolve (asserted separately below)."),

            ["Core/EntityWorld.cs"] = (1,
                "Create(): entity SPAWN placement (scenario apply, production, hero revival, editor undo-restore). " +
                "Authored placement, not movement — there is no previous position to sweep from. Blocked-cell " +
                "placement is owned by the load-time ScenarioValidator.CheckSpawnsNotBlocked diagnostic (DW-148)."),

            ["Core/Persistence/SaveGameState.cs"] = (4,
                "Save RESTORE of units / buildings / resource nodes / projectiles from a previously-played match. " +
                "Placement of an already-simulated state, not a step: sweeping it would relocate a legitimately " +
                "saved unit. (The restore path has no blocked-cell diagnostic of its own — recorded as a follow-up, " +
                "not fixed here.)"),

            ["Core/BuildingStore.cs"] = (1,
                "Building placement at Spawn. Buildings never move; their footprint is stamped INTO the pathability " +
                "union (PathabilityGrid.StampPropInto / BuildBlockingFootprint), it is not constrained by it."),

            ["Core/ResourceNodeStore.cs"] = (1,
                "Resource-node placement at Spawn. Static authored world content — never moves."),

            ["Combat/ProjectileStore.cs"] = (1,
                "Projectile spawn position. A ProjectileStore slot is not an EntityWorld entity."),

            ["Combat/ProjectileSystem.cs"] = (2,
                "In-flight shell advance (the step-to-goal snap and the normal advance). Deliberately NOT swept: " +
                "pathability is a GROUND constraint and a projectile flies over walls — routing shells through " +
                "CheckedStep would make arrows stop at cliffs."),

            ["UI/EntityPlacer.cs"] = (2,
                "Editor undo/redo restoring a BUILDING slot's authored position. Editor-authored placement, " +
                "Godot-side; the map editor's blocked-cell feedback is the pathability overlay + validator."),

            ["Effects/TeleportEffect.cs"] = (1,
                "Teleport blink: authored/instant PLACEMENT, not a swept step — a blink deliberately bypasses walls " +
                "between origin and destination, so CheckedStep.Resolve must NOT apply. Destination validity is the " +
                "ground-cast RaycastGround gate (MVP)."),
        };

        /// <summary>
        /// A write to a SoA slot of an array named exactly <c>Position</c> — bare (<c>Position[id] =</c>, inside the
        /// store that owns it) or qualified (<c>world.Position[i] =</c>, <c>_store.Position[i] +=</c>). The negative
        /// lookbehind on a word character is what keeps sibling arrays whose names merely END in "Position"
        /// (<c>PrevPosition</c>, <c>StartedAtPosition</c>) out of the audit — they are interpolation/bookkeeping
        /// mirrors, not the authoritative simulated position. Compound assignment is matched so a future
        /// <c>Position[i] += delta</c> cannot slip past; <c>==</c>, <c>!=</c>, <c>&gt;=</c> and <c>&lt;=</c> cannot.
        /// </summary>
        private static readonly Regex PositionWrite = new(
            @"(?<![A-Za-z0-9_])Position\s*\[[^\]\n]*\]\s*(?:[-+*/%|&^]?=)(?!=)",
            RegexOptions.Compiled);

        [Fact]
        public void EveryPositionWriter_IsOnTheAuditedAllowlist()
        {
            var actual = ScanPositionWrites();
            var problems = new List<string>();

            foreach ((string file, int count) in actual.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (!Sanctioned.TryGetValue(file, out var entry))
                {
                    problems.Add($"UNAUDITED writer: '{file}' writes Position[…] {count}x and is not on the DW-648 allowlist.");
                    continue;
                }
                if (entry.Count != count)
                    problems.Add($"WRITE COUNT CHANGED: '{file}' now writes Position[…] {count}x, audited at {entry.Count}x.");
            }

            Assert.True(problems.Count == 0,
                "The DW-648 position-writer audit is out of date:" + Environment.NewLine + "  " +
                string.Join(Environment.NewLine + "  ", problems) + Environment.NewLine +
                "If the new write represents an entity MOVING, route it through " +
                "ProjectChimera.Navigation.CheckedStep.Resolve(world.Pathability, from, desired) so it inherits the " +
                "swept blocked-cell rejection + wall-slide (DW-147/DW-148) instead of bypassing it. If it is authored " +
                "or restored PLACEMENT, update the Sanctioned table in this test with the new count AND a " +
                "justification — that edit is the audit.");
        }

        [Fact]
        public void EverySanctionedWriter_StillExists()
        {
            // Defeats a vacuous pass: a broken regex, a wrong src path or a silently deleted/renamed writer would
            // otherwise leave the guard green while checking nothing.
            var actual = ScanPositionWrites();
            foreach (string file in Sanctioned.Keys.OrderBy(k => k, StringComparer.Ordinal))
                Assert.True(actual.ContainsKey(file),
                    $"Audited position writer '{file}' was not observed by the scan — the regex, the src path, or the " +
                    $"file itself drifted. If the writer was intentionally removed or moved, update the Sanctioned table.");
        }

        [Fact]
        public void TheSweptRejection_IsCalledOnlyFromTheSharedCheckedStepHelper()
        {
            // DW-648's headline invariant. PathabilityGrid.cs is excluded because that is where the method is
            // DECLARED; every other src file that names it is CALLING it, and the only sanctioned caller is the
            // shared helper. A hand-copied sweep in a second system is exactly the drift this forbids.
            const string sweep = "IsBlockedOnSegmentOutside";
            var callers = ScanFilesContaining(sweep)
                .Where(f => f != "Navigation/PathabilityGrid.cs")
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();

            Assert.True(callers.SequenceEqual(new[] { "Navigation/CheckedStep.cs" }),
                $"The swept blocked-cell rejection ({sweep}) must be invoked from the ONE shared helper " +
                $"Navigation/CheckedStep.cs and nowhere else (DW-648). Observed callers: " +
                $"[{string.Join(", ", callers)}]. Call CheckedStep.Resolve instead of re-implementing the " +
                $"sweep-and-slide sequence.");
        }

        [Fact]
        public void MovementSystem_RoutesItsWriteThroughCheckedStep()
        {
            // The other half of the invariant: the one MOVEMENT writer must actually USE the helper. Without this a
            // dev could satisfy the caller check above by deleting the guard from MovementSystem entirely.
            string movement = File.ReadAllText(Path.Combine(SrcRoot(), "Navigation", "MovementSystem.cs"));
            Assert.Contains("CheckedStep.Resolve(", StripCommentsAndLiterals(movement), StringComparison.Ordinal);
        }

        // ── scanning ─────────────────────────────────────────────────────────────

        /// <summary>Repo-relative (forward-slash, rooted at <c>godot/src/</c>) file ⇒ number of Position writes.</summary>
        private static Dictionary<string, int> ScanPositionWrites()
        {
            string root = SrcRoot();
            var found = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                int n = PositionWrite.Matches(StripCommentsAndLiterals(File.ReadAllText(path))).Count;
                if (n > 0) found[Relative(root, path)] = n;
            }
            return found;
        }

        /// <summary>Repo-relative paths of every <c>src/**.cs</c> whose CODE (comments and string literals removed)
        /// contains <paramref name="token"/>.</summary>
        private static IEnumerable<string> ScanFilesContaining(string token)
        {
            string root = SrcRoot();
            foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
                if (StripCommentsAndLiterals(File.ReadAllText(path)).Contains(token, StringComparison.Ordinal))
                    yield return Relative(root, path);
        }

        /// <summary>
        /// Blank out block comments, line comments and string/char literals so neither prose (this codebase documents
        /// its invariants heavily — <c>PathabilityGrid</c>'s own XML doc names the sweep a dozen times) nor an
        /// assertion message can masquerade as code. Whitespace is NOT collapsed: the patterns above are
        /// whitespace-tolerant already and keeping the layout keeps a failure message readable.
        /// </summary>
        private static string StripCommentsAndLiterals(string text)
        {
            text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);   // block comments
            text = Regex.Replace(text, @"//[^\n]*", " ");                             // line comments (incl. /// XML doc)
            text = Regex.Replace(text, @"@""(?:[^""]|"""")*""", @""" """);            // verbatim strings
            text = Regex.Replace(text, @"""(?:\\.|[^""\\\n])*""", @""" """);          // regular / interpolated strings
            text = Regex.Replace(text, @"'(?:\\.|[^'\\\n])'", "' '");                 // char literals
            return text;
        }

        private static string Relative(string root, string path) =>
            path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Replace('\\', '/');

        /// <summary>godot/src — two directories up from this file (…/ProjectChimera.Sim.Tests/Meta/), then into src.</summary>
        private static string SrcRoot([CallerFilePath] string thisFilePath = "")
        {
            string dir = Path.GetDirectoryName(thisFilePath)
                         ?? throw new InvalidOperationException("Could not resolve this test's source directory via [CallerFilePath].");
            string root = Path.GetFullPath(Path.Combine(dir, "..", "..", "src"));
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException(
                    $"DW-648 position-writer guard could not locate the shipping source tree. Resolved path: '{root}'. " +
                    $"This path is derived from [CallerFilePath]; if the project layout moved, update this guard.");
            return root;
        }
    }
}
