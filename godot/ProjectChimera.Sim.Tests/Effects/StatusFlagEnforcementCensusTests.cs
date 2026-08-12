#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using ProjectChimera.Effects;
using Xunit;

namespace ProjectChimera.Sim.Tests.Effects
{
    /// <summary>
    /// DW-786 — the one DW-324 residue that re-verification found still stale, turned into a guard.
    ///
    /// <para>DW-324 closed a doc-rot sweep "done" with named items never re-read; DW-663 recovered one of them and
    /// DW-786 asks for the remaining seven to be re-verified against current source. Six were genuinely clean
    /// (<c>EffectCaps</c>, <c>ServerChecksumCollector</c>, <c>AbilityEditorPanel.Advanced</c>,
    /// <c>ScenarioLoadPhase</c>, <c>lan-desync-smoke.ps1</c>, and the FallbackMirror/alpha_map_01 agreement test,
    /// which exists). One was not: <see cref="StatusFlags"/> still called itself a "Reserved set — the systems that
    /// honour each flag land with the ModifierStore (Story 2.2b)", years after every flag went live.</para>
    ///
    /// <para><b>Why a census rather than a string pin.</b> A comment can be corrected once and rot again. The claim
    /// the corrected doc makes — every flag is enforced by a named system — is checkable, and checking it also closes
    /// the mirror-image hazard: a flag APPENDED to this closed set with no enforcer would be authorable content that
    /// silently does nothing, which is precisely the state the stale comment described and the reason it read as
    /// plausible for so long.</para>
    ///
    /// <para>Godot-free; a source scan over <c>godot/src</c> plus the live enum, in the
    /// <c>CombatCommandSwitchCompletenessTests</c> idiom.</para>
    /// </summary>
    public class StatusFlagEnforcementCensusTests
    {
        /// <summary>
        /// Each flag and the SIM file that acts on it. Naming the file (not just "somewhere") is the point: an
        /// appended flag has to be given an enforcer here, and a deleted enforcer fails loudly instead of leaving
        /// the flag quietly inert.
        /// </summary>
        private static readonly (StatusFlags Flag, string RelPath)[] Enforcers =
        {
            (StatusFlags.Stunned,      "Combat/CombatSystem.cs"),      // no acquisition, no swing
            (StatusFlags.Rooted,       "Navigation/MovementSystem.cs"),// anchored in place
            (StatusFlags.Silenced,     "Effects/AbilityCastSystem.cs"),// no active cast
            (StatusFlags.Disarmed,     "Combat/CombatSystem.cs"),      // acquires and chases, never lands a hit
            (StatusFlags.Invulnerable, "Combat/DamageResolver.cs"),    // damage fails closed
        };

        [Fact]
        public void EveryStatusFlag_IsClassifiedWithAnEnforcingSystem()
        {
            string[] members = Enum.GetNames(typeof(StatusFlags))
                .Where(n => !string.Equals(n, nameof(StatusFlags.None), StringComparison.Ordinal))
                .ToArray();
            string[] classified = Enforcers.Select(e => e.Flag.ToString()).Distinct().ToArray();

            string[] unclassified = members.Except(classified, StringComparer.Ordinal).OrderBy(n => n).ToArray();
            Assert.True(unclassified.Length == 0,
                "DW-786: these StatusFlags members name no enforcing system: " + string.Join(", ", unclassified) +
                ". A flag with no enforcer is authorable content that silently does nothing — exactly the 'Reserved " +
                "set, the systems that honour each flag land later' state the enum's doc described for years after " +
                "it stopped being true. Give the flag a system and record it here, or do not ship the flag.");

            string[] dead = classified.Except(members, StringComparer.Ordinal).ToArray();
            Assert.True(dead.Length == 0, "DW-786: these census entries name no StatusFlags member: " + string.Join(", ", dead));
        }

        [Theory]
        [MemberData(nameof(EnforcerCases))]
        public void TheNamedEnforcer_ActuallyReadsTheFlag(string flag, string relPath)
        {
            // The claim is per-FILE, so it is checked per-file: the enforcer must reference the flag in code (not
            // only in prose), or the census is documenting an intention rather than a behaviour.
            string file = Path.Combine(SrcRoot(), relPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(file), $"DW-786 census names a missing file: {relPath}");

            string source = StripComments(File.ReadAllText(file));
            Assert.Contains($"StatusFlags.{flag}", source, StringComparison.Ordinal);
        }

        public static IEnumerable<object[]> EnforcerCases() =>
            Enforcers.Select(e => new object[] { e.Flag.ToString(), e.RelPath });

        [Fact]
        public void TheEnumDocRecordsTheCorrection_SoItCannotQuietlyRevert()
        {
            // The literal DW-786 residue: Modifier.cs described this enum as a reserved set whose enforcing systems
            // "land with the ModifierStore (Story 2.2b)" long after every flag went live. The correction is pinned by
            // its DW tag rather than by forbidding the old sentence — the corrected doc QUOTES the old sentence in
            // order to explain what was wrong with it, so a negative substring pin would forbid its own fix.
            string doc = File.ReadAllText(Path.Combine(SrcRoot(), "Effects", "Modifier.cs"));
            int enumAt = doc.IndexOf("public enum StatusFlags", StringComparison.Ordinal);
            Assert.True(enumAt > 0, "StatusFlags moved out of Modifier.cs — move this guard with it.");

            // The characters immediately above the enum are its doc block.
            string docBlock = doc.Substring(Math.Max(0, enumAt - 2500), Math.Min(2500, enumAt));
            Assert.Contains("DW-786", docBlock, StringComparison.Ordinal);
            Assert.Contains("no longer reserved", docBlock, StringComparison.Ordinal);
        }

        private static string StripComments(string text) =>
            System.Text.RegularExpressions.Regex.Replace(
                System.Text.RegularExpressions.Regex.Replace(text, @"/\*.*?\*/", " ",
                    System.Text.RegularExpressions.RegexOptions.Singleline),
                @"//[^\n]*", " ");

        /// <summary>godot/src — two directories up from this file (…/ProjectChimera.Sim.Tests/Effects/), then into src.</summary>
        private static string SrcRoot([CallerFilePath] string thisFilePath = "")
        {
            string dir = Path.GetDirectoryName(thisFilePath)
                         ?? throw new InvalidOperationException("Could not resolve this test's source directory.");
            string root = Path.GetFullPath(Path.Combine(dir, "..", "..", "src"));
            if (!Directory.Exists(root))
                throw new DirectoryNotFoundException($"DW-786 census could not locate the shipping source tree: '{root}'.");
            return root;
        }
    }
}
