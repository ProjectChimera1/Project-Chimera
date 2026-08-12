#nullable enable
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Bootstrap
{
    /// <summary>
    /// DW-639 — the BOOT MATCH-LOAD leg of DW-327 has no executable coverage, so deleting one argument would
    /// silently re-open half of that fix with the whole suite green.
    ///
    /// <para><b>The gap.</b> DW-327 closed the "selectable but not launchable" split by threading the ability
    /// registry into all three client <c>ValidateComplete</c> sites, so a dangling
    /// <c>signature_mechanic_effect_id</c> is reported at DISCOVERY and at MATCH LOAD rather than surfacing for the
    /// first time as a hard Edit→Play veto. Two of those sites live in Godot-free code and are covered by real
    /// tests; the third, <c>SlotFactionResolver</c>, sits under <c>src/Core/Bootstrap/Phases/**</c>, which
    /// <c>SimSources.props</c> explicitly <c>&lt;Compile Remove&gt;</c>s from this assembly (it owns the
    /// scenario-apply path's <c>ProjectSettings.GlobalizePath</c> and its <c>GD.PrintErr</c> diagnostics). Its
    /// registry threading was therefore verified by CODE READ ONLY.</para>
    ///
    /// <para><b>What this does instead.</b> The established source-pin shape for a Godot-coupled call site — the
    /// DW-86/DW-626 <c>CommandApplyParityTests</c> pattern: resolve the file portably via
    /// <see cref="CallerFilePathAttribute"/>, strip comments so prose can neither satisfy nor hide the assertion,
    /// and assert the ARGUMENT is actually present at the call site (not merely mentioned in a comment). Extracting a
    /// new Godot-free type for a one-line diagnostic was judged out of proportion when DW-639 was filed; this is the
    /// proportionate guard.</para>
    /// </summary>
    public class SlotFactionResolverRegistryPinTests
    {
        [Fact]
        public void Resolve_StillTakesTheAbilityRegistry()
        {
            // Vacuous-pass guard: every assertion below is about an argument NAMED abilityRegistry. If the parameter
            // is renamed or dropped, this fires first with a message that says so, rather than the scans passing
            // trivially against a signature that no longer has a registry at all.
            string blob = ResolverSource();

            Assert.Matches(@"\bpublic static void Resolve\(", blob);
            Assert.Matches(@"\bAbilityRegistry abilityRegistry\b", blob);
        }

        [Fact]
        public void BootMatchLoad_ValidateComplete_StillCarriesTheAbilityRegistry()
        {
            string blob = ResolverSource();

            var sites = Regex.Matches(blob, @"\bFactionValidator\.ValidateComplete\(");
            Assert.True(sites.Count == 1,
                $"Expected exactly ONE FactionValidator.ValidateComplete call in SlotFactionResolver.cs, found " +
                $"{sites.Count}. The boot match-load shadow leg changed shape — re-point this DW-639 pin at the new " +
                "site rather than deleting it.");

            string args = ArgumentList(blob, sites[0].Index + sites[0].Length - 1);
            Assert.True(Regex.IsMatch(args, @"\babilityRegistry\b"),
                "The boot match-load leg calls FactionValidator.ValidateComplete WITHOUT the ability registry. " +
                "DW-327/DW-639: with no registry the signature_mechanic_effect_id resolution check is DORMANT there, " +
                "so a faction with a typo'd signature id loads silently at match start and is then hard-vetoed at " +
                "Edit→Play by an error the boot console never showed. Call args: " + args);
        }

        [Fact]
        public void BootMatchLoad_AbilityResolution_StillCarriesTheRegistry()
        {
            // The sibling half of the same threading: the per-slot defs' ability ids must be back-filled to registry
            // indices BEFORE the applier spawns units, or every ability silently resolves to nothing on this leg.
            string blob = ResolverSource();

            var sites = Regex.Matches(blob, @"\bResolveAbilities\(");
            Assert.True(sites.Count == 1,
                $"Expected exactly ONE ResolveAbilities call in SlotFactionResolver.cs, found {sites.Count}.");

            string args = ArgumentList(blob, sites[0].Index + sites[0].Length - 1);
            Assert.True(Regex.IsMatch(args, @"\babilityRegistry\b"),
                "The boot match-load leg calls ResolveAbilities without the registry. Call args: " + args);
        }

        // ── Plumbing (mirrors CommandApplyParityTests) ──────────────────────────────────────────────────

        private static string ResolverSource()
        {
            string path = ResolverFile();
            Assert.True(File.Exists(path),
                $"SlotFactionResolver.cs not found at '{path}' (via [CallerFilePath]). If the Bootstrap phase moved, " +
                "re-point this DW-639 pin.");
            return StripCommentsAndNormalize(File.ReadAllText(path));
        }

        /// <summary>This file lives in godot/ProjectChimera.Sim.Tests/Bootstrap/ →
        /// ../../src/Core/Bootstrap/Phases/SlotFactionResolver.cs.</summary>
        private static string ResolverFile([CallerFilePath] string thisFilePath = "")
        {
            string dir = Path.GetDirectoryName(thisFilePath)
                         ?? throw new InvalidOperationException("Could not resolve this test's source dir via [CallerFilePath].");
            return Path.GetFullPath(Path.Combine(dir, "..", "..", "src", "Core", "Bootstrap", "Phases", "SlotFactionResolver.cs"));
        }

        /// <summary>The balanced parenthesised argument list that STARTS at <paramref name="openParen"/> (which must
        /// index the '(' itself), exclusive of the outer parentheses.</summary>
        private static string ArgumentList(string blob, int openParen)
        {
            int depth = 0;
            for (int i = openParen; i < blob.Length; i++)
            {
                if (blob[i] == '(') depth++;
                else if (blob[i] == ')')
                {
                    depth--;
                    if (depth == 0) return blob.Substring(openParen + 1, i - openParen - 1);
                }
            }
            throw new InvalidOperationException("Unbalanced parentheses while scanning a source-pinned call site.");
        }

        /// <summary>Strip block/line comments then collapse whitespace, so comment prose can never satisfy (or hide)
        /// the pins above — DW-327's rationale is written out at length right beside the call site.</summary>
        private static string StripCommentsAndNormalize(string text)
        {
            text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            text = Regex.Replace(text, @"//[^\n]*", " ");
            return Regex.Replace(text, @"\s+", " ");
        }
    }
}
