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
        private const uint GoldenHash = 0xB46313CAu;

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
