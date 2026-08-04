#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 5.7 (FR-19/UX-DR80) — one test per I/O Matrix row for
    /// <see cref="FactionDefinition.LoadSelectableFromDirectory"/>: the directory-scan discovery method that
    /// closes DW-97's discovery half (gates every <c>*_faction.json</c> through
    /// <see cref="FactionValidator.ValidateComplete"/>, never the lenient <see cref="FactionValidator.Validate"/>
    /// that <see cref="FactionDefinition.LoadFromFile"/> uses). All Godot-free (Tier-1) — writes real files to a
    /// temp directory, mirroring <see cref="HeroProfilePersistenceTests"/>'s own <c>TempDir</c> pattern.
    ///
    /// <para>DW-327 adds the registry-aware rows at the bottom: discovery now takes the same
    /// <see cref="AbilityRegistry"/> the Edit→Play launch gate takes, so a dangling
    /// <c>signature_mechanic_effect_id</c> is dropped from the selectable set (with its located reason) instead of
    /// listing as selectable and being hard-vetoed later by <see cref="FactionLaunchGate"/>. The parity row drives
    /// BOTH surfaces off one registry and asserts they agree.</para>
    /// </summary>
    public class FactionDiscoveryTests
    {
        // ── Faction builders (mirrors FactionValidatorTests' baseline helpers) ─────────────

        private static UnitDefinition Worker(string id = "worker") => new()
        {
            Id = id,
            DisplayName = id,
            Category = "Worker",
            MeshPath = "res://assets/worker.glb",
            Hp = 50f,
        };

        private static UnitDefinition Melee(string id = "melee") => new()
        {
            Id = id,
            DisplayName = id,
            Category = "Melee",
            MeshPath = "res://assets/melee.glb",
            Hp = 50f,
        };

        private static BuildingDefinition ValidBuilding(string id = "command_center") => new()
        {
            Id = id,
            DisplayName = id,
            Category = "Structure",
            MeshPath = "res://assets/command_center.glb",
            Hp = 100f,
            ConstructionTime = 10f,
            SupplyBonus = 0,
            ProducesCategory = "Worker",
        };

        /// <summary>A minimal, fully-valid (ValidateComplete-passing) faction, mirroring alpha/beta's own shape.</summary>
        private static FactionDefinition ValidFaction(string id)
        {
            var def = new FactionDefinition { Id = id, DisplayName = id };
            def.Units.Add(Worker());
            def.Units.Add(Melee());
            def.Buildings.Add(ValidBuilding());
            return def;
        }

        /// <summary>A faction that passes the lenient <see cref="FactionValidator.Validate"/> (so it would
        /// LoadFromFile fine) but fails <see cref="FactionValidator.ValidateComplete"/> — no Worker-category unit.</summary>
        private static FactionDefinition IncompleteFaction_MissingWorker(string id)
        {
            var def = new FactionDefinition { Id = id, DisplayName = id };
            def.Units.Add(Melee());
            def.Buildings.Add(ValidBuilding());
            return def;
        }

        private static void WriteFaction(string dir, string fileName, FactionDefinition def)
        {
            File.WriteAllText(Path.Combine(dir, fileName), JsonSerializer.Serialize(def, FactionDefinition.JsonOptions));
        }

        // ── Row: valid wizard-authored faction is included ─────────────────────────────────

        [Fact]
        public void ValidFaction_IsIncluded_NoExclusionReported()
        {
            using var dir = new TempDir();
            WriteFaction(dir.Path, "newfaction_faction.json", ValidFaction("newfaction"));

            var excluded = new List<(string, string)>();
            IReadOnlyList<FactionDefinition> result = FactionDefinition.LoadSelectableFromDirectory(
                dir.Path, (name, reason) => excluded.Add((name, reason)));

            Assert.Single(result);
            Assert.Equal("newfaction", result[0].Id);
            Assert.Empty(excluded);
        }

        // ── Row: showcase factions (alpha/beta-style pair) both appear alongside each other ─

        [Fact]
        public void ShowcaseStylePair_BothIncluded_AlongsideEachOther()
        {
            using var dir = new TempDir();
            WriteFaction(dir.Path, "alpha_faction.json", ValidFaction("alpha"));
            WriteFaction(dir.Path, "beta_faction.json", ValidFaction("beta"));

            IReadOnlyList<FactionDefinition> result = FactionDefinition.LoadSelectableFromDirectory(dir.Path);

            Assert.Equal(2, result.Count);
            Assert.Contains(result, f => f.Id == "alpha");
            Assert.Contains(result, f => f.Id == "beta");
        }

        // ── Row: non-faction sample files are never even scanned ───────────────────────────

        [Fact]
        public void NonFactionSampleFiles_AreIgnoredEntirely_NoSkipReport()
        {
            using var dir = new TempDir();
            WriteFaction(dir.Path, "alpha_faction.json", ValidFaction("alpha"));
            // Deliberately unparseable-as-faction garbage — if these were ever scanned they'd trigger onExcluded.
            File.WriteAllText(Path.Combine(dir.Path, "_buildingcard_sample.json"), "{ not json faction content ][");
            File.WriteAllText(Path.Combine(dir.Path, "_unitcard_sample.json"), "{ not json faction content ][");

            var excluded = new List<(string, string)>();
            IReadOnlyList<FactionDefinition> result = FactionDefinition.LoadSelectableFromDirectory(
                dir.Path, (name, reason) => excluded.Add((name, reason)));

            Assert.Single(result);
            Assert.Equal("alpha", result[0].Id);
            Assert.Empty(excluded); // filename filter excludes the sample files before any parse is attempted
        }

        // ── Row: faction failing ValidateComplete is excluded with a located reason ────────

        [Fact]
        public void ValidateCompleteFailing_Excluded_WithLocatedReason()
        {
            using var dir = new TempDir();
            WriteFaction(dir.Path, "broken_faction.json", IncompleteFaction_MissingWorker("broken"));

            var excluded = new List<(string Name, string Reason)>();
            IReadOnlyList<FactionDefinition> result = FactionDefinition.LoadSelectableFromDirectory(
                dir.Path, (name, reason) => excluded.Add((name, reason)));

            Assert.Empty(result);
            Assert.Single(excluded);
            Assert.Equal("broken_faction.json", excluded[0].Name);
            Assert.False(string.IsNullOrWhiteSpace(excluded[0].Reason));
        }

        // ── Row: malformed JSON is excluded via onExcluded, scan continues ─────────────────

        [Fact]
        public void MalformedJson_Excluded_ScanContinuesToNextFile()
        {
            using var dir = new TempDir();
            File.WriteAllText(Path.Combine(dir.Path, "malformed_faction.json"), "{ this is not valid json ][");
            WriteFaction(dir.Path, "valid_faction.json", ValidFaction("valid"));

            var excluded = new List<(string Name, string Reason)>();
            IReadOnlyList<FactionDefinition> result = FactionDefinition.LoadSelectableFromDirectory(
                dir.Path, (name, reason) => excluded.Add((name, reason)));

            Assert.Single(result);
            Assert.Equal("valid", result[0].Id);
            Assert.Single(excluded);
            Assert.Equal("malformed_faction.json", excluded[0].Name);
            Assert.False(string.IsNullOrWhiteSpace(excluded[0].Reason));
        }

        // ── Row: missing directory returns empty, never throws ─────────────────────────────

        [Fact]
        public void MissingDirectory_ReturnsEmpty_NoThrow()
        {
            string absent = Path.Combine(Path.GetTempPath(), "chimera_faction_discovery_absent_" + System.Guid.NewGuid().ToString("N"));
            IReadOnlyList<FactionDefinition> result = FactionDefinition.LoadSelectableFromDirectory(absent);
            Assert.Empty(result);
        }

        // ── Row: an EXISTING directory with zero matching files (distinct code path from "missing") ─

        [Fact]
        public void ExistingEmptyDirectory_ReturnsEmpty_NoThrow()
        {
            using var dir = new TempDir();
            // Directory exists (Directory.Exists passes) but has no *_faction.json files at all —
            // a distinct branch from MissingDirectory_ReturnsEmpty_NoThrow's Directory.Exists-false path.
            IReadOnlyList<FactionDefinition> result = FactionDefinition.LoadSelectableFromDirectory(dir.Path);
            Assert.Empty(result);
        }

        // ── Row: duplicate faction Id across two files — first file wins, second reported ──

        [Fact]
        public void DuplicateId_FirstFileWins_SecondReportedAndExcluded()
        {
            using var dir = new TempDir();
            // Ordinal filename order: a_dup < b_dup, so "a_dup_faction.json" is the first-seen occurrence.
            WriteFaction(dir.Path, "a_dup_faction.json", ValidFaction("shared"));
            WriteFaction(dir.Path, "b_dup_faction.json", ValidFaction("shared"));

            var excluded = new List<(string Name, string Reason)>();
            IReadOnlyList<FactionDefinition> result = FactionDefinition.LoadSelectableFromDirectory(
                dir.Path, (name, reason) => excluded.Add((name, reason)));

            Assert.Single(result);
            Assert.Equal("shared", result[0].Id);
            Assert.Single(excluded);
            Assert.Equal("b_dup_faction.json", excluded[0].Name);
            Assert.Contains("duplicate", excluded[0].Reason, System.StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void NullOrEmptyDirectory_ReturnsEmpty_NoThrow()
        {
            Assert.Empty(FactionDefinition.LoadSelectableFromDirectory(""));
            Assert.Empty(FactionDefinition.LoadSelectableFromDirectory(null!));
        }

        // ── Row: deterministic ordinal-by-Id ordering, independent of on-disk filename order ─

        [Fact]
        public void ThreeOrMoreFiles_OrderedOrdinalById_IndependentOfFilenameOrder()
        {
            using var dir = new TempDir();
            // Filename order (ordinal): a_file < b_file < c_file — deliberately NOT matching Id order, so this
            // proves the final sort is by def.Id, not by the walk order.
            WriteFaction(dir.Path, "a_file_faction.json", ValidFaction("zzz"));
            WriteFaction(dir.Path, "b_file_faction.json", ValidFaction("mmm"));
            WriteFaction(dir.Path, "c_file_faction.json", ValidFaction("aaa"));

            IReadOnlyList<FactionDefinition> result = FactionDefinition.LoadSelectableFromDirectory(dir.Path);

            Assert.Equal(3, result.Count);
            Assert.Equal(new[] { "aaa", "mmm", "zzz" }, new[] { result[0].Id, result[1].Id, result[2].Id });
        }

        // ══ DW-327: registry-aware discovery — "selectable" must equal "launchable" ════════════════
        // Before the fix LoadSelectableFromDirectory called ValidateComplete with NO registry, so the
        // signature_mechanic_effect_id resolution check was dormant here while the Edit→Play launch gate
        // (FactionLaunchGate, which threads the real registry) enforced it: a faction with a typo'd signature id
        // listed as SELECTABLE at boot and was then hard-vetoed at Play with an error the boot console never showed.

        /// <summary>A faction whose ONLY fault is a signature id that resolves to nothing.</summary>
        private static FactionDefinition DanglingSignatureFaction(string id, string signatureId = "no_such_effect")
        {
            FactionDefinition def = ValidFaction(id);
            def.SignatureMechanicEffectId = signatureId;
            return def;
        }

        // ── Row: dangling signature id + registry → excluded, with the located signature reason ────
        [Fact]
        public void DanglingSignatureId_WithRegistry_Excluded_WithLocatedSignatureReason()
        {
            using var dir = new TempDir();
            WriteFaction(dir.Path, "badsig_faction.json", DanglingSignatureFaction("badsig"));
            var registry = new AbilityRegistry(new[] { new AbilityDefinition { Id = "known_effect" } });

            var excluded = new List<(string Name, string Reason)>();
            IReadOnlyList<FactionDefinition> result = FactionDefinition.LoadSelectableFromDirectory(
                dir.Path, (name, reason) => excluded.Add((name, reason)), registry);

            Assert.Empty(result); // NOT selectable — matches what the launch gate would do
            Assert.Single(excluded);
            Assert.Equal("badsig_faction.json", excluded[0].Name);
            Assert.Contains("signature_mechanic_effect_id", excluded[0].Reason);
            Assert.Contains("no_such_effect", excluded[0].Reason); // names the unresolvable id for the creator
        }

        // ── Row: resolving signature id + registry → still selectable (no over-blocking) ───────────
        [Fact]
        public void ResolvingSignatureId_WithRegistry_IsStillIncluded()
        {
            using var dir = new TempDir();
            WriteFaction(dir.Path, "goodsig_faction.json", DanglingSignatureFaction("goodsig", "known_effect"));
            var registry = new AbilityRegistry(new[] { new AbilityDefinition { Id = "known_effect" } });

            var excluded = new List<(string, string)>();
            IReadOnlyList<FactionDefinition> result = FactionDefinition.LoadSelectableFromDirectory(
                dir.Path, (name, reason) => excluded.Add((name, reason)), registry);

            Assert.Single(result);
            Assert.Equal("goodsig", result[0].Id);
            Assert.Empty(excluded);
        }

        // ── Row: a dangling-signature faction never masks the healthy ones in the same scan ────────
        [Fact]
        public void DanglingSignatureId_WithRegistry_DoesNotAbortScan_HealthyFactionsStillDiscovered()
        {
            using var dir = new TempDir();
            WriteFaction(dir.Path, "a_badsig_faction.json", DanglingSignatureFaction("a_badsig"));
            WriteFaction(dir.Path, "b_ok_faction.json", ValidFaction("b_ok"));
            var registry = new AbilityRegistry(new[] { new AbilityDefinition { Id = "known_effect" } });

            IReadOnlyList<FactionDefinition> result = FactionDefinition.LoadSelectableFromDirectory(
                dir.Path, null, registry);

            Assert.Single(result);
            Assert.Equal("b_ok", result[0].Id);
        }

        // ── Row: null registry keeps the pre-DW-327 lenient behavior (back-compat, documented) ─────
        [Fact]
        public void DanglingSignatureId_WithoutRegistry_StillIncluded_SignatureCheckSkipped()
        {
            using var dir = new TempDir();
            WriteFaction(dir.Path, "badsig_faction.json", DanglingSignatureFaction("badsig"));

            // No registry supplied ⇒ resolution is impossible ⇒ ValidateComplete skips ONLY the signature check
            // (its existing semantics). Every other axis still gates — see the ValidateComplete rows above.
            IReadOnlyList<FactionDefinition> result = FactionDefinition.LoadSelectableFromDirectory(dir.Path);

            Assert.Single(result);
            Assert.Equal("badsig", result[0].Id);
        }

        // ── Row: THE DW-327 PARITY PROPERTY — everything discovery calls selectable is launchable ──
        [Fact]
        public void EveryDiscoveredFaction_PassesTheLaunchGate_WithTheSameRegistry()
        {
            using var dir = new TempDir();
            // A deliberately mixed directory: one healthy faction, one dangling-signature faction, one
            // incomplete roster. Only the healthy one may be discovered, and the discovered set must clear the
            // very gate (FactionLaunchGate) that vetoes Edit→Play — with the SAME registry both sides see.
            WriteFaction(dir.Path, "a_ok_faction.json", ValidFaction("a_ok"));
            WriteFaction(dir.Path, "b_badsig_faction.json", DanglingSignatureFaction("b_badsig"));
            WriteFaction(dir.Path, "c_noworker_faction.json", IncompleteFaction_MissingWorker("c_noworker"));
            var registry = new AbilityRegistry(new[] { new AbilityDefinition { Id = "known_effect" } });

            IReadOnlyList<FactionDefinition> discovered =
                FactionDefinition.LoadSelectableFromDirectory(dir.Path, null, registry);

            Assert.Equal(new[] { "a_ok" }, discovered.Select(d => d.Id).ToArray());
            // Feed the discovered set to the launch gate exactly as MainScene would: no veto is possible.
            Assert.Null(FactionLaunchGate.FirstIncompleteReason(discovered.ToArray(), registry));

            // And the converse: the excluded dangling-signature faction IS vetoed by that same gate — proving
            // the two surfaces agree on this faction rather than both simply being lenient.
            Assert.NotNull(FactionLaunchGate.FirstIncompleteReason(
                new FactionDefinition?[] { DanglingSignatureFaction("b_badsig") }, registry));
        }

        // ── Row: the SHIPPED factions stay selectable against the REAL ability registry ────────────
        [Fact]
        public void ShippedFactions_WithRealAbilitiesRegistry_AreStillSelectable()
        {
            // Belt-and-suspenders: alpha/beta author signature ids (spike_transmutation / furnace_trickle) that
            // MUST resolve in the shipped abilities directory — otherwise this change would silently drop the two
            // showcase factions from the boot "selectable" list. Mirrors FactionLaunchGateTests' real-registry row.
            AbilityRegistry registry = AbilityRegistry.LoadFromDirectory(ResolveAbilitiesDir());
            Assert.True(registry.Count > 0, "real abilities directory resolved to an empty registry — path drift?");

            var excluded = new List<(string Name, string Reason)>();
            IReadOnlyList<FactionDefinition> result = FactionDefinition.LoadSelectableFromDirectory(
                ResolveFactionsDir(), (name, reason) => excluded.Add((name, reason)), registry);

            Assert.Contains(result, f => f.Id == "alpha");
            Assert.Contains(result, f => f.Id == "beta");
            Assert.DoesNotContain(excluded, e => e.Reason.Contains("signature_mechanic_effect_id"));
        }

        /// <summary>Resolve the shipped <c>resources/data/factions/</c> directory by walking up from the test
        /// assembly (mirrors <c>FactionLaunchGateTests.ResolveDataPath</c>).</summary>
        private static string ResolveFactionsDir() => ResolveDataDir("factions");

        /// <summary>Resolve the shipped <c>resources/data/abilities/</c> directory (mirrors
        /// <c>FactionLaunchGateTests.ResolveAbilitiesDir</c>).</summary>
        private static string ResolveAbilitiesDir() => ResolveDataDir("abilities");

        private static string ResolveDataDir(string leaf)
        {
            var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "resources", "data", leaf);
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException(
                $"Could not locate resources/data/{leaf} above {System.AppContext.BaseDirectory}");
        }

        // ── Test-local temp directory helper (mirrors HeroProfilePersistenceTests' own TempDir) ────

        private sealed class TempDir : System.IDisposable
        {
            public string Path { get; }
            public TempDir()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "chimera_faction_discovery_" + System.Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }
            public void Dispose()
            {
                try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch { /* best-effort cleanup */ }
            }
        }
    }
}
