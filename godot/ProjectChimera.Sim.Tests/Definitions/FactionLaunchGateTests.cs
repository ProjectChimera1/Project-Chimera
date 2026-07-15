#nullable enable
using System;
using System.IO;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 14.4 (DW-97) — locks every branch of the pure <see cref="FactionLaunchGate.FirstIncompleteReason"/>
    /// launch-gate decision at the Godot-free level (the Godot <c>MainScene.ResetToAuthoredStart</c> call site is
    /// outside the Tier-1 test globs, so the RED-teeth proof lives here). One test per I/O-Matrix row: all-complete
    /// → null; missing-Worker → located reason; dangling signature (registry lacking the id) → located
    /// signature_mechanic_effect_id reason; null slot entries skipped; null registry skips the signature check but
    /// still enforces the roster; plus a row proving the shipped alpha/beta descriptor ids pass with an in-memory
    /// registry containing spike_transmutation/furnace_trickle.
    /// </summary>
    public class FactionLaunchGateTests
    {
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

        /// <summary>A minimal, fully-<see cref="FactionValidator.ValidateComplete"/>-passing faction. Each test
        /// mutates exactly one axis away from this baseline.</summary>
        private static FactionDefinition ValidFaction(string id = "test_faction")
        {
            var def = new FactionDefinition { Id = id, DisplayName = id };
            def.Units.Add(Worker());
            def.Units.Add(Melee());
            def.Buildings.Add(ValidBuilding());
            return def;
        }

        // ── Row: all slot factions complete → null (Play proceeds) ────────────────────────────
        [Fact]
        public void AllSlotFactionsComplete_ReturnsNull()
        {
            var registry = new AbilityRegistry(new[] { new AbilityDefinition { Id = "some_ability" } });
            FactionDefinition?[] slots = { ValidFaction("alpha"), ValidFaction("beta") };

            Assert.Null(FactionLaunchGate.FirstIncompleteReason(slots, registry));
        }

        // ── Row: slot faction missing Worker → located reason, Play vetoed ────────────────────
        [Fact]
        public void SlotFactionMissingWorker_ReturnsLocatedReason_NamingFactionAndWorker()
        {
            FactionDefinition incomplete = ValidFaction("no_worker");
            incomplete.Units.RemoveAll(u => string.Equals(u.Category, "Worker", StringComparison.OrdinalIgnoreCase));
            FactionDefinition?[] slots = { ValidFaction("alpha"), incomplete };

            string? reason = FactionLaunchGate.FirstIncompleteReason(slots, null);

            Assert.NotNull(reason);
            Assert.Contains("no_worker", reason);        // names the offending faction id
            Assert.Contains("is incomplete", reason);
            Assert.Contains("Worker", reason);           // names the located roster error
        }

        // ── Row: slot faction dangling signature id (registry lacks it) → located reason ──────
        [Fact]
        public void SlotFactionDanglingSignatureId_WithRegistry_ReturnsLocatedSignatureReason()
        {
            FactionDefinition dangling = ValidFaction("bad_sig");
            dangling.SignatureMechanicEffectId = "no_such_effect";
            var registry = new AbilityRegistry(new[] { new AbilityDefinition { Id = "known_effect" } });
            FactionDefinition?[] slots = { dangling };

            string? reason = FactionLaunchGate.FirstIncompleteReason(slots, registry);

            Assert.NotNull(reason);
            Assert.Contains("bad_sig", reason);
            Assert.Contains("signature_mechanic_effect_id", reason);
            Assert.Contains("no_such_effect", reason);
        }

        // ── Row: null slot entries among valid defs → nulls skipped, null returned ────────────
        [Fact]
        public void NullSlotEntriesAmongValidDefs_AreSkipped_ReturnsNull()
        {
            FactionDefinition?[] slots = { null, ValidFaction("alpha"), null, ValidFaction("beta") };

            Assert.Null(FactionLaunchGate.FirstIncompleteReason(slots, null));
        }

        [Fact]
        public void NullSlotEntries_DoNotMaskAnIncompleteRealDef()
        {
            FactionDefinition incomplete = ValidFaction("no_worker");
            incomplete.Units.RemoveAll(u => string.Equals(u.Category, "Worker", StringComparison.OrdinalIgnoreCase));
            FactionDefinition?[] slots = { null, incomplete, null };

            string? reason = FactionLaunchGate.FirstIncompleteReason(slots, null);

            Assert.NotNull(reason);
            Assert.Contains("no_worker", reason);
        }

        // ── Row: null / empty registry → signature check skipped, roster still enforced ───────
        [Fact]
        public void NullRegistry_SkipsSignatureCheck_ButEnforcesRoster()
        {
            // A faction whose ONLY fault is a dangling signature id passes when the registry is null
            // (signature check cannot resolve → skipped, matching ValidateComplete semantics).
            FactionDefinition onlyDanglingSig = ValidFaction("sig_only");
            onlyDanglingSig.SignatureMechanicEffectId = "no_such_effect";
            Assert.Null(FactionLaunchGate.FirstIncompleteReason(new FactionDefinition?[] { onlyDanglingSig }, null));

            // But the roster/hero/mesh checks still enforce with a null registry.
            FactionDefinition missingWorker = ValidFaction("no_worker");
            missingWorker.Units.RemoveAll(u => string.Equals(u.Category, "Worker", StringComparison.OrdinalIgnoreCase));
            string? reason = FactionLaunchGate.FirstIncompleteReason(new FactionDefinition?[] { missingWorker }, null);
            Assert.NotNull(reason);
            Assert.Contains("Worker", reason);
        }

        // ── Guard: null slot array → null (no throw) ──────────────────────────────────────────
        [Fact]
        public void NullSlotArray_ReturnsNull()
        {
            Assert.Null(FactionLaunchGate.FirstIncompleteReason(null!, null));
        }

        // ── Row: shipped alpha/beta descriptor ids pass with an in-memory registry ────────────
        [Fact]
        public void ShippedAlphaBeta_WithInMemoryRegistry_ContainingSignatureIds_ReturnsNull()
        {
            // The real defaults launched by MainScene: their signature_mechanic_effect_id values are
            // spike_transmutation (alpha) / furnace_trickle (beta), and neither authors a hero. With a registry
            // containing those two abilities the gate must NOT block the default playtest.
            FactionDefinition alpha = FactionDefinition.LoadFromFile(ResolveDataPath("alpha_faction.json"));
            FactionDefinition beta = FactionDefinition.LoadFromFile(ResolveDataPath("beta_faction.json"));
            var registry = new AbilityRegistry(new[]
            {
                new AbilityDefinition { Id = "spike_transmutation" },
                new AbilityDefinition { Id = "furnace_trickle" },
            });

            FactionDefinition?[] slots = { alpha, beta };
            Assert.Null(FactionLaunchGate.FirstIncompleteReason(slots, registry));
        }

        [Fact]
        public void ShippedAlphaBeta_WithRealAbilitiesRegistry_ReturnsNull()
        {
            // Belt-and-suspenders against the Block-If: the shipped factions must pass with the REAL loaded
            // registry, not just a hand-built stand-in. If this fails it is an out-of-scope data defect (HALT).
            AbilityRegistry registry = AbilityRegistry.LoadFromDirectory(ResolveAbilitiesDir());
            Assert.True(registry.Count > 0, "real abilities directory resolved to an empty registry — path drift?");

            FactionDefinition alpha = FactionDefinition.LoadFromFile(ResolveDataPath("alpha_faction.json"));
            FactionDefinition beta = FactionDefinition.LoadFromFile(ResolveDataPath("beta_faction.json"));

            Assert.Null(FactionLaunchGate.FirstIncompleteReason(new FactionDefinition?[] { alpha, beta }, registry));
        }

        // ── Row: slot faction with a blank mesh_path unit → located mesh_path reason ───────────
        // ValidateComplete enforces four axes (Worker/combat roster, mesh_path, hero_unit_id, signature); the two
        // above cover roster + signature, these two cover mesh_path + hero so every axis the gate can block on is
        // exercised THROUGH the gate, not only inside ValidateComplete's own tests.
        [Fact]
        public void SlotFactionWithBlankMeshPathUnit_ReturnsLocatedMeshPathReason()
        {
            FactionDefinition incomplete = ValidFaction("blank_mesh");
            incomplete.Units.Add(new UnitDefinition
            {
                Id = "ghost", DisplayName = "ghost", Category = "Ranged", MeshPath = "", Hp = 40f,
            });
            FactionDefinition?[] slots = { incomplete };

            string? reason = FactionLaunchGate.FirstIncompleteReason(slots, null); // mesh_path check is registry-independent

            Assert.NotNull(reason);
            Assert.Contains("blank_mesh", reason);
            Assert.Contains("mesh_path", reason);
        }

        // ── Row: slot faction with a dangling hero_unit_id → located hero_unit_id reason ───────
        [Fact]
        public void SlotFactionWithDanglingHeroId_ReturnsLocatedHeroReason()
        {
            FactionDefinition incomplete = ValidFaction("bad_hero");
            incomplete.HeroUnitId = "nonexistent_hero"; // names no unit in the roster
            FactionDefinition?[] slots = { incomplete };

            string? reason = FactionLaunchGate.FirstIncompleteReason(slots, null); // hero check is registry-independent

            Assert.NotNull(reason);
            Assert.Contains("bad_hero", reason);
            Assert.Contains("hero_unit_id", reason);
        }

        /// <summary>Resolve a shipped faction JSON by walking up to <c>resources/data/factions/</c>
        /// (mirrors <c>FactionValidatorTests.ResolveDataPath</c>).</summary>
        private static string ResolveDataPath(string fileName)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "resources", "data", "factions");
                if (Directory.Exists(candidate)) return Path.Combine(candidate, fileName);
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException(
                $"Could not locate resources/data/factions above {AppContext.BaseDirectory}");
        }

        /// <summary>Resolve the shipped <c>resources/data/abilities/</c> directory (mirrors
        /// <c>FactionValidatorTests.ResolveAbilitiesDir</c>).</summary>
        private static string ResolveAbilitiesDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "resources", "data", "abilities");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException(
                $"Could not locate resources/data/abilities above {AppContext.BaseDirectory}");
        }
    }
}
