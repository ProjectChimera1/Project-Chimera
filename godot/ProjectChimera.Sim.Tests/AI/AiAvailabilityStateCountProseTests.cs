#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ProjectChimera.AI.Providers;
using Xunit;

namespace ProjectChimera.Sim.Tests.AI
{
    /// <summary>
    /// DW-800 — the AI-availability surface must be described by its SPLIT, never by a member COUNT.
    ///
    /// <para><b>The rot.</b> <see cref="AiAvailability"/> shipped with four members and the whole AI stack — the
    /// evaluator, <c>LLMService</c>, six bootstrap phases, five creation-suite panels and the settings panel — called
    /// it "four-state" in prose. DW-370 added <c>HostRestricted</c> (five) and DW-589 added
    /// <c>HostNotAllowlisted</c> (seven) without touching a word of it, so 36 comment sites misdescribed the surface
    /// for the next reader deciding whether a new state was safe to add. No behaviour depended on the phrase, which
    /// is exactly why nothing caught it.</para>
    ///
    /// <para><b>Why a count BAN and not a re-pin at seven.</b> Re-pinning only resets the rot clock — the eighth
    /// state would land the same way. Dropping the number entirely makes the prose un-rottable: the split it
    /// describes (config-derived + synchronous vs network round-trip) is a property of the design, not of the
    /// member list. That is the entry's recorded closure, and this is the guard that keeps it.</para>
    ///
    /// <para>Godot-free: guard 1 reads <c>godot/src/**</c> as TEXT (it never loads the Godot-coupled panels it
    /// scans); guard 2 reflects over the enum. Nothing here folds into a checksum or golden.</para>
    /// </summary>
    public class AiAvailabilityStateCountProseTests
    {
        /// <summary>
        /// A COUNT-scoped description of a state machine, in its ADJECTIVAL form: "four-state", "7-state". That is
        /// the exact shape the rot took, and restricting to the hyphenated compound is what lets this scan cover ALL
        /// of <c>src</c> — including a consumer added after this guard — without false positives on the legitimate
        /// SPACED prose that surrounds it ("two state-mutating events", "the Story 7.13 state reads", "the two states
        /// that hold a reservation", "DW-678 states the same partition"), none of which describes a surface by its
        /// member count.
        /// </summary>
        private static readonly Regex StateCountProse = new(
            @"\b(?:one|two|three|four|five|six|seven|eight|nine|ten|[0-9]+)-state(?:s)?\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>The files the phrase actually infected, kept as an anchor set: the scan below covers ALL of
        /// <c>src</c>, and these are the ones whose existence proves the scan is looking in the right place.</summary>
        private static readonly string[] AnchorFiles =
        {
            Path.Combine("AI", "Providers", "AiAvailability.cs"),
            Path.Combine("AI", "Providers", "AiAvailabilityEvaluator.cs"),
            Path.Combine("AI", "LLMService.cs"),
        };

        [Fact]
        public void NoSourceFile_DescribesTheAvailabilitySurfaceByStateCount()
        {
            string src = Path.Combine(LocateGodotRoot(), "src");

            // Defeat a vacuous pass: a broken locator or a renamed file would otherwise report zero offenders.
            foreach (string anchor in AnchorFiles)
                Assert.True(File.Exists(Path.Combine(src, anchor)),
                    $"anchor file '{anchor}' not found under {src} — the scan is not looking where it thinks it is.");

            var offenders = new List<string>();
            int scanned = 0;

            foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
            {
                scanned++;
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    Match m = StateCountProse.Match(lines[i]);
                    if (m.Success)
                        offenders.Add($"{Path.GetRelativePath(src, file)}:{i + 1}: …{m.Value}… — {lines[i].Trim()}");
                }
            }

            Assert.True(scanned > 100, $"only {scanned} source files scanned under {src}; the walk is wrong.");
            Assert.True(offenders.Count == 0,
                "A comment describes a state machine by how many states it has. DW-800: the AI-availability surface "
                + "was documented as \"four-state\" in 36 places and silently became five (DW-370 HostRestricted) and "
                + "then seven (DW-589 HostNotAllowlisted) with the prose untouched, misdescribing the surface for "
                + "anyone deciding whether an eighth was safe to add. Describe the SPLIT — config-derived and "
                + "synchronous vs a network round-trip — not the count; re-pinning the number only resets the clock:"
                + "\n  " + string.Join("\n  ", offenders));
        }

        [Fact]
        public void EveryAvailabilityState_HasItsOwnDistinctMessage_WhateverTheCountIs()
        {
            // The positive half, and the reason a count is never needed in prose: the surface is enumerable at
            // runtime, and adding a member is a one-arm change with nothing to keep in sync but this assertion.
            AiAvailability[] states = Enum.GetValues<AiAvailability>();

            Assert.True(states.Length >= 7, "the availability enum shrank; re-read DW-370/DW-589 before adjusting.");
            foreach (AiAvailability s in states)
                Assert.False(string.IsNullOrWhiteSpace(AiAvailabilityMessages.Describe(s)),
                    $"AiAvailability.{s} has no creator-facing message.");
            Assert.Equal(states.Length,
                         states.Select(AiAvailabilityMessages.Describe).Distinct(StringComparer.Ordinal).Count());
        }

        /// <summary>Walk up from the test-assembly directory to the <c>godot/</c> dir (the one holding both
        /// <c>src</c> and <c>scenes</c>) — the <c>StartPathTeamAgreementTests.LocateGodotRoot</c> precedent.</summary>
        private static string LocateGodotRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "src")) &&
                    Directory.Exists(Path.Combine(dir.FullName, "scenes")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException(
                $"Could not locate the godot/ root (a dir with both src/ and scenes/) above {AppContext.BaseDirectory}");
        }
    }
}
