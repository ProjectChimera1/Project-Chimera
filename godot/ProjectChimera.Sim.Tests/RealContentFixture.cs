#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using ProjectChimera.Core.Definitions;   // AbilityRegistry, AbilityValidator, FactionDefinition, UnitDefinition
using Xunit;

namespace ProjectChimera.Sim.Tests
{
    /// <summary>
    /// DW-760 — the ONE owner of "load the REAL shipped content" for the Tier-1 suite: the data-directory
    /// resolution, the shipped-roster floor, and the guarded registry load.
    ///
    /// <para><b>Why it exists.</b> DW-107 and DW-536 each closed the same hole in a different class:
    /// <see cref="AbilityRegistry.LoadFromDirectory"/> SILENTLY drops any file that fails
    /// <c>AbilityValidator</c>, and no callback can ever fire for a file that was deleted outright — so an
    /// unguarded real-content load keeps passing against less-than-shipped content. The fix each time was an
    /// <c>onSkipped</c> collector plus a count FLOOR. But it landed twice, as two private copies with two
    /// independently-maintained floor constants, so a deliberate roster change had to update two numbers that
    /// nothing kept in sync — one could silently drift below the real count and quietly go back to being a
    /// vacuous guard, which is the very silent-degradation class DW-107/DW-536 exist to close, reintroduced one
    /// level up. One floor, one resolver, one guarded load: a roster change now has exactly one number to move.</para>
    ///
    /// <para>Godot-free test-only infrastructure: it reads content JSON off disk and touches no simulation state,
    /// so it can never move a checksum or a golden.</para>
    /// </summary>
    internal static class RealContentFixture
    {
        /// <summary>
        /// The shipped-roster floor for the REAL ability registry: 10 files lived under
        /// <c>resources/data/abilities/</c> when DW-107 pinned it (DW-536 pinned the same number a second time in
        /// a second class; DW-760 merged them here). Revise ONLY on a deliberate shipped-roster change — lowering
        /// it to make a red suite green is exactly the silent shrink the floor exists to catch.
        /// </summary>
        public const int MinShippedAbilityCount = 10;

        /// <summary>
        /// <c>resources/data/&lt;sub&gt;</c>, found by walking up from the test binary's directory. Portable: no
        /// hardcoded absolute path, so it resolves the same on a CI checkout.
        /// </summary>
        public static string DataDir(string sub)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "resources", "data", sub);
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException(
                $"Could not locate resources/data/{sub} above {AppContext.BaseDirectory}");
        }

        /// <summary>
        /// The guarded ability-registry load. Fails LOUD on BOTH silent-shrink paths: a shipped file that fails
        /// <c>AbilityValidator</c> (via <c>onSkipped</c>, which defaults to <c>null</c> on the raw API) and a
        /// shipped file that disappeared entirely (via <paramref name="minAbilityCount"/>, which a skip callback
        /// can never see).
        ///
        /// <para><paramref name="abilitiesDir"/> and <paramref name="minAbilityCount"/> are parameters rather
        /// than baked in so the guard itself stays directly exercisable against a SYNTHETIC directory — against
        /// the real shipped one it can only ever be observed NOT firing, which is exactly how the unguarded load
        /// stayed green while claiming to prove real-content coverage.</para>
        /// </summary>
        public static AbilityRegistry LoadGuardedAbilityRegistry(string abilitiesDir, int minAbilityCount)
        {
            var skippedAbilityFiles = new List<string>();
            AbilityRegistry registry = AbilityRegistry.LoadFromDirectory(abilitiesDir, skippedAbilityFiles.Add);
            Assert.True(skippedAbilityFiles.Count == 0,
                $"shipped ability file(s) failed validation and were silently excluded from the registry: {string.Join(", ", skippedAbilityFiles)}");
            Assert.True(registry.Count >= minAbilityCount,
                $"real ability registry holds {registry.Count} abilities, below the shipped floor of {minAbilityCount} — shipped content shrank (deleted/moved file?); revise RealContentFixture.MinShippedAbilityCount only on a deliberate roster change.");
            return registry;
        }

        /// <summary>
        /// The REAL shipped alpha/beta <see cref="FactionDefinition"/>s plus the REAL guarded
        /// <see cref="AbilityRegistry"/>, with every roster unit's abilities resolved against it — the exact
        /// scenario-link sequence <c>MainScene</c>/<c>ServerBootstrap</c> run, never skipped in a test.
        /// </summary>
        public static (FactionDefinition alpha, FactionDefinition beta, AbilityRegistry registry) LoadShowcaseFactions()
        {
            AbilityRegistry registry = LoadGuardedAbilityRegistry(DataDir("abilities"), MinShippedAbilityCount);
            FactionDefinition alpha = FactionDefinition.LoadFromFile(Path.Combine(DataDir("factions"), "alpha_faction.json"));
            FactionDefinition beta  = FactionDefinition.LoadFromFile(Path.Combine(DataDir("factions"), "beta_faction.json"));
            foreach (UnitDefinition u in alpha.Units) u.ResolveAbilities(registry);
            foreach (UnitDefinition u in beta.Units)  u.ResolveAbilities(registry);
            return (alpha, beta, registry);
        }
    }
}
