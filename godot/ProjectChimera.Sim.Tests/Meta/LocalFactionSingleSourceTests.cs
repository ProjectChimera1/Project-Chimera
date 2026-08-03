#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ProjectChimera.Core;   // Faction, LocalFactionPolicy
using Xunit;

namespace ProjectChimera.Sim.Tests.Meta
{
    /// <summary>
    /// DW-407 — the "one clamped local-faction source of truth" guard. Story 9.5 introduced
    /// <c>LockstepManager.EffectiveLocalFaction</c> (offline/spectator → Player1) but left the pre-existing consumers
    /// (personalised DSL-readback overlays, hero-owner minting, the local-defeat watcher) reading RAW
    /// <c>LockstepManager.LocalFaction</c> — which <c>GoOnline</c>/<c>GoSpectate</c> mutate — so a same-process offline
    /// F5 after an online/spectate match resolved them to the stale Player2/Neutral. DW-407 closes both tiers:
    /// every presentation consumer routes through a clamped accessor (<c>EffectiveLocalFaction</c>, or
    /// <c>HeroMintOwnerSlot</c> for the mint owner filter), AND <c>GoOffline()</c> re-clamps the stored value to
    /// <see cref="LocalFactionPolicy.OfflineLocalFaction"/>.
    ///
    /// <para><b>(1) Source scan.</b> Sweeps <c>src/**/*.cs</c> (comments stripped) for a raw member read
    /// <c>.LocalFaction</c> outside <c>LockstepManager.cs</c> (the declaring class — its own online send-path uses are
    /// the sanctioned raw accesses). A reintroduced raw consumer turns this RED with a pointer to the clamped
    /// accessors. This is the regression net for the whole DW-407 consumer class, not just the eight sites fixed.</para>
    ///
    /// <para><b>(2) Source pins.</b> Godot-coupled wiring the Tier-1 suite cannot execute
    /// (<c>LockstepManager</c>/<c>MainScene</c> are outside the Godot-free compile set), pinned at source level —
    /// the <c>PlayerCountPolicy_DocumentsTheEightPlayerBump</c>/<c>FallbackMirrorParityTests</c> precedent:
    /// the <c>GoOffline</c> reset, the <c>HeroMintOwnerSlot</c> spectator carve-out (a spectator must keep minting
    /// NOTHING — clamping it to Player1 would locally mint a stale offline profile into Player1's placed hero row,
    /// sim state no other client builds), and the defeat-watcher's explicit spectator skip (previously implicit in the
    /// stale raw-Neutral guard).</para>
    ///
    /// <para><b>(3) Pure invariants.</b> The reset value is the fixed point of the
    /// <see cref="LocalFactionPolicy.Effective"/> clamp — the stored tier and the clamp tier agree offline, so the
    /// two-tier source of truth cannot reappear by value drift.</para>
    /// </summary>
    public class LocalFactionSingleSourceTests
    {
        /// <summary>The one file allowed to touch the raw stored faction: the class that declares and owns it.</summary>
        private const string DeclaringFile = "LockstepManager.cs";

        // A raw member read of the stored faction: `.LocalFaction` NOT followed by a further identifier char
        // (so `.LocalFactionPolicy`-style longer names never match; `.EffectiveLocalFaction`/`.OfflineLocalFaction`
        // have a different char after the dot and never match; unqualified uses inside LockstepManager have no dot).
        private static readonly Regex RawLocalFactionRead = new(@"\.LocalFaction\b", RegexOptions.Compiled);

        [Fact]
        public void SourceScan_NoRawLocalFactionConsumerOutsideLockstepManager()
        {
            string srcDir = SrcDir();
            Assert.True(Directory.Exists(srcDir), $"src directory not found at '{srcDir}' (via [CallerFilePath]).");

            var stray = new List<string>();
            int effectiveConsumerFiles = 0;
            bool sawDeclaringFile = false;

            foreach (string file in Directory.GetFiles(srcDir, "*.cs", SearchOption.AllDirectories))
            {
                string fileName = Path.GetFileName(file);
                string blob = StripCommentsAndNormalize(File.ReadAllText(file));

                if (Regex.IsMatch(blob, @"\.EffectiveLocalFaction\b") || Regex.IsMatch(blob, @"\.HeroMintOwnerSlot\b"))
                    effectiveConsumerFiles++;

                if (string.Equals(fileName, DeclaringFile, StringComparison.Ordinal))
                {
                    sawDeclaringFile = true;
                    // Vacuous-pass guard: the scanned LockstepManager must still DECLARE the stored faction — if it
                    // was renamed/moved, this scan would be guarding a name that no longer exists.
                    Assert.Matches(@"public Faction LocalFaction \{ get; private set; \}", blob);
                    continue; // the declaring class's own raw uses are the sanctioned ones
                }

                foreach (Match m in RawLocalFactionRead.Matches(blob))
                {
                    int start = Math.Max(0, m.Index - 60);
                    stray.Add($"{fileName}: …{blob.Substring(start, Math.Min(blob.Length - start, 90))}…");
                }
            }

            Assert.True(sawDeclaringFile, $"'{DeclaringFile}' was not found under src — the scan's exclusion anchor drifted.");
            Assert.True(stray.Count == 0,
                "Raw LockstepManager.LocalFaction consumer(s) found outside the declaring class — GoOnline/GoSpectate " +
                "mutate that stored value, so a raw read resolves to the stale prior-match faction in a same-process " +
                "offline session (DW-407). Route the read through LockstepManager.EffectiveLocalFaction (perspective/" +
                "personalisation) or LockstepManager.HeroMintOwnerSlot (hero-mint owner filter) instead:\n  " +
                string.Join("\n  ", stray));

            // Vacuous-pass guard: the clamped accessors must actually be consumed across the presentation layer —
            // if this drops to a handful, the file set / regex / stripping broke and the stray assertion proves nothing.
            Assert.True(effectiveConsumerFiles >= 5,
                $"Only {effectiveConsumerFiles} file(s) consume the clamped accessors — the scan's file set or regex drifted.");
        }

        [Fact]
        public void GoOffline_ReclampsTheStoredLocalFaction()
        {
            string blob = StripCommentsAndNormalize(File.ReadAllText(LockstepManagerFile()));
            Match body = Regex.Match(blob, @"public void GoOffline\(\) \{(?<body>[^{}]*)\}");
            Assert.True(body.Success,
                "Could not locate the GoOffline() body in LockstepManager.cs — if the method changed shape, update this pin.");
            Assert.Contains("LocalFaction = LocalFactionPolicy.OfflineLocalFaction;", body.Groups["body"].Value);
        }

        [Fact]
        public void HeroMintOwnerSlot_KeepsTheSpectatorNoMintCarveOut()
        {
            string blob = StripCommentsAndNormalize(File.ReadAllText(LockstepManagerFile()));
            // A spectator's mint owner is Neutral (placed heroes are player-owned → matches none → mints nothing),
            // NOT the Player1 reference view — "simplifying" this accessor to EffectiveLocalFaction would make a
            // spectating client locally mint a stale offline profile into Player1's hero row.
            Assert.Matches(
                @"public Faction HeroMintOwnerSlot => IsSpectator \? Faction\.Neutral : EffectiveLocalFaction;",
                blob);

            // And the accessor is genuinely consumed by the mint sites (vacuous-pass guard for the pin above).
            int consumers = 0;
            foreach (string file in Directory.GetFiles(SrcDir(), "*.cs", SearchOption.AllDirectories))
                if (Regex.IsMatch(StripCommentsAndNormalize(File.ReadAllText(file)), @"\.HeroMintOwnerSlot\b"))
                    consumers++;
            Assert.True(consumers >= 2,
                $"Expected the hero-mint sites (HeroPickerPhase + MainScene) to consume HeroMintOwnerSlot; found {consumers} file(s).");
        }

        [Fact]
        public void DefeatWatcher_SkipsSpectatorExplicitlyAndWatchesTheEffectiveFaction()
        {
            string blob = StripCommentsAndNormalize(File.ReadAllText(MainSceneFile()));
            // The eliminated check must gate on IsSpectator (previously the stale raw-Neutral value skipped it
            // implicitly) and personalise via the clamped accessor.
            Assert.Matches(@"if \(!_localEliminated && !_ctx\.Lockstep\.IsSpectator\)", blob);
            Assert.Matches(@"Faction local = _ctx\.Lockstep\.EffectiveLocalFaction;", blob);
        }

        [Fact]
        public void OfflineReset_IsTheEffectiveClampFixedPoint()
        {
            // The reset value IS the offline reference viewer…
            Assert.Equal(Faction.Player1, LocalFactionPolicy.OfflineLocalFaction);
            // …and is stable under the clamp (re-reading the reset value offline resolves to itself)…
            Assert.Equal(LocalFactionPolicy.OfflineLocalFaction,
                LocalFactionPolicy.Effective(false, false, LocalFactionPolicy.OfflineLocalFaction));
            // …and the stale-faction cases resolve to the SAME value the store is reset to — the stored tier and the
            // clamp tier agree, so raw and clamped reads can no longer disagree offline (the DW-407 defect shape).
            Assert.Equal(LocalFactionPolicy.OfflineLocalFaction, LocalFactionPolicy.Effective(false, false, Faction.Player2));
            Assert.Equal(LocalFactionPolicy.OfflineLocalFaction, LocalFactionPolicy.Effective(false, false, Faction.Neutral));
        }

        /// <summary>Remove block comments (<c>/* … */</c>) and line comments (<c>// …</c>), then collapse every run of
        /// whitespace to a single space — so the regexes see code only, spacing-insensitively, and comment text can
        /// never masquerade as (or hide) a raw read. Mirrors <c>NoHardcodedPlayerCountTests</c>.</summary>
        private static string StripCommentsAndNormalize(string text)
        {
            text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline); // block comments
            text = Regex.Replace(text, @"//[^\n]*", " ");                            // line comments
            return Regex.Replace(text, @"\s+", " ");                                 // collapse whitespace
        }

        // ── path helpers (this file lives in godot/ProjectChimera.Sim.Tests/Meta/) ────────────────
        private static string SrcDir([CallerFilePath] string thisFilePath = "") =>
            ResolveFromHere(thisFilePath, "..", "..", "src");

        private static string LockstepManagerFile([CallerFilePath] string thisFilePath = "") =>
            ResolveFromHere(thisFilePath, "..", "..", "src", "Multiplayer", "LockstepManager.cs");

        private static string MainSceneFile([CallerFilePath] string thisFilePath = "") =>
            ResolveFromHere(thisFilePath, "..", "..", "src", "Core", "MainScene.cs");

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
