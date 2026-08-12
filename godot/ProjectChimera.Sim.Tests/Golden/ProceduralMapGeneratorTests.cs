#nullable enable
using System.Text;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.MapGen;
using Xunit;

namespace ProjectChimera.Sim.Tests.Golden
{
    /// <summary>
    /// Story 1.11 (AC4 — Decision #2 / Option B) — the procedural seeded map-generator smoke test. Proves the new
    /// Godot-free <see cref="ProceduralMapGenerator"/> genuinely satisfies the AC's "fixed seed via SimRng →
    /// byte-identical map":
    ///   • AC4b(i)  — the SAME seed generates a byte-identical serialization across two in-process runs AND
    ///     matches a pinned FNV-1a golden hash (a JSON-format-drift tripwire),
    ///   • AC4b(ii) — a DIFFERENT seed drives a DIFFERENT map (the seed is the SOLE entropy source — non-vacuity;
    ///     Id/DisplayName are seed-independent so the difference is purely the generated geometry),
    ///   • AC4b(iii)— the generated <see cref="ScenarioData"/> passes the AR-39 <see cref="ScenarioValidator"/>
    ///     gate, for many seeds (the generator never emits an out-of-bounds map).
    ///
    /// Because generation is integer/<see cref="ProjectChimera.Core.SimRng"/>-only (no float/System.Random/Godot
    /// RNG in the path), this map IS cross-platform-deterministic and MP-safe — unlike the untouched LLM
    /// "describe a map in words" generator, which has no RNG to seed (the authoring-only / non-deterministic
    /// path). No network call is made. Pattern mirrors CanonicalScenarioTests' run-twice determinism check.
    /// </summary>
    public class ProceduralMapGeneratorTests
    {
        private const ulong Seed      = 0xC0FFEEUL;
        private const ulong OtherSeed = 0xBEEFUL;

        /// <summary>
        /// Pinned FNV-1a hash of <c>Serialize(Generate(Seed))</c> — a JSON-format-drift tripwire. Recorded once on
        /// this machine; because the generation path is INTEGER-only this is ALSO the cross-platform value (AC4d),
        /// so this golden COULD later join the WSL cross-platform gate (unlike the AI-active golden). Update it
        /// INTENTIONALLY (alongside a deliberate generator/format change), never to "fix" a red run.
        /// </summary>
        // Re-recorded for Story 4.7: ScenarioResourceNode gained 6 new always-serialized fields (collection_model,
        // resource_type, requires_structure_radius, owner_slot, income_period_ticks — requires_structure omits when
        // null), so every generated node's JSON grew, moving this JSON-format-drift tripwire (an intentional,
        // additive schema change — not a "fix a red run" edit).
        // Re-recorded for Story 7.7: ScenarioSerializer.Serialize now STAMPS schema_version + checksum_algo_version
        // on every save (D3 versioning), so the generated JSON grew by the two stamp keys — the same intentional,
        // additive schema-change class as the 4.7 re-record above.
        // Re-recorded for Story 7.8: CanonicalModelHash.AlgoVersion bumped 8→9 (the custom-UI widget-tree fold), and
        // Serialize stamps that value into `checksum_algo_version`, so the generated JSON's stamp changed 8→9 —
        // moving this byte-hash tripwire by one stamp digit (the same additive stamp-change class as the 7.7 record).
        // DW-272 / Story 15.12: CanonicalModelHash.AlgoVersion bumped 14->15 (the Modifier.PeriodicStacking fold), and
        // Serialize stamps that value into `checksum_algo_version`, so the generated JSON's stamp moved 14->15 — the
        // same additive stamp-change class as every re-record above (the generated map has no modifiers, so the bytes
        // are otherwise unchanged).
        // DW-941: CanonicalModelHash.AlgoVersion bumped 15->16 (the building_min_gap fold), stamp moved 15->16 —
        // the same additive stamp-change class (the generated map authors no building_min_gap, so the key is
        // omitted and the bytes are otherwise unchanged).
        private const uint GoldenHash = 2680846249u;

        [Fact]
        public void SameSeed_TwiceProducesByteIdenticalSerialization_AndMatchesGoldenHash()
        {
            string json1 = ScenarioSerializer.Serialize(ProceduralMapGenerator.Generate(Seed));
            string json2 = ScenarioSerializer.Serialize(ProceduralMapGenerator.Generate(Seed));

            Assert.Equal(json1, json2); // byte-identical across two in-process runs (the determinism guarantee)
            uint hash = ScenarioSerializer.ComputeHash(Encoding.UTF8.GetBytes(json1));
            Assert.Equal(GoldenHash, hash);
        }

        [Fact]
        public void DifferentSeed_ProducesADifferentMap()
        {
            // Id/DisplayName are seed-INDEPENDENT, so any difference here is purely the generated geometry —
            // proving the seed actually DRIVES generation, not just a label (the real non-vacuity control).
            string a = ScenarioSerializer.Serialize(ProceduralMapGenerator.Generate(Seed));
            string b = ScenarioSerializer.Serialize(ProceduralMapGenerator.Generate(OtherSeed));
            Assert.NotEqual(a, b);
        }

        [Theory]
        [InlineData(0UL)]
        [InlineData(1UL)]
        [InlineData(0xC0FFEEUL)]
        [InlineData(0xDEADBEEFUL)]
        [InlineData(ulong.MaxValue)]
        public void AnySeed_GeneratesAMapThatPassesTheValidatorGate(ulong seed)
        {
            ScenarioData map = ProceduralMapGenerator.Generate(seed);
            ValidationResult r = new ScenarioValidator().Validate(map);
            Assert.True(r.Ok, $"seed {seed}: {r.Error}");
        }
    }
}
