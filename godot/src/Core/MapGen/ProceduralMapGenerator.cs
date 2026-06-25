#nullable enable
using System;
using System.Collections.Generic;
using ProjectChimera.Core.Definitions;

namespace ProjectChimera.Core.MapGen
{
    /// <summary>
    /// Story 1.11 (AC4 — Decision #2 / Option B) — a procedural, deterministic map generator: a SIBLING to the
    /// LLM "describe a map in words" generator (<c>MapGeneratorPanel</c> / <c>LLMService.GenerateScenarioAsync</c>),
    /// which is left untouched. The LLM path has no RNG to seed and is non-deterministic by design; THIS path
    /// genuinely satisfies the AC's "fixed seed via SimRng → byte-identical map".
    ///
    /// Determinism contract (why this is cross-platform / MP-safe, unlike the LLM and the float-scored AI):
    ///   • ALL randomness comes from a single <see cref="SimRng"/> seeded by the caller — never
    ///     <c>System.Random</c>, Godot RNG, noise, or wall-clock.
    ///   • ALL placement math is INTEGER (a coarse grid). No <c>float</c>/<c>double</c> appears in the generation
    ///     path — the 1.10b analyzer over <c>src/Core/**</c> enforces this. Integers are an exact subset of the
    ///     16.16 <see cref="Fixed"/> domain, so the output is bit-identical on every machine.
    ///   • Candidate cells are collected in a DETERMINISTIC ascending (x, z) order BEFORE any
    ///     <see cref="SimRng.NextInt"/> draw (AR-15) — iteration order is part of the deterministic contract.
    ///   • The only conversion to <c>float</c> is the final assignment into the <see cref="ScenarioData"/> output
    ///     fields (authoring-legal floats that quantize back to <see cref="Fixed"/> at sim ingest); because every
    ///     placed value is an exact small integer, that conversion and its JSON serialization are platform-stable.
    ///
    /// Godot-free (<c>src/Core/MapGen/</c>) so it lands in the Tier-1 set automatically. Presentation may call it
    /// from a button and load the result; no sim/Godot types leak across the boundary.
    /// </summary>
    public static class ProceduralMapGenerator
    {
        /// <summary>Default playable half-extent (map_bounds) in world units when the caller omits it.</summary>
        public const int DefaultMapBounds = 120;

        /// <summary>Default number of resource nodes to place.</summary>
        public const int DefaultResourceNodeCount = 6;

        /// <summary>Keep bases/nodes this far inside the bounds so nothing hugs the edge.</summary>
        private const int EdgeMargin = 15;

        /// <summary>Candidate-grid granularity for node placement (finer than the spacing → varied layouts).</summary>
        private const int GridStep = 5;

        /// <summary>Minimum separation (Chebyshev, world units) between any two placed nodes. ≥15 satisfies the
        /// existing map constraint AND guarantees Euclidean spacing ≥ 15 (Euclidean ≥ Chebyshev).</summary>
        private const int NodeSpacing = 15;

        /// <summary>
        /// Generate a deterministic 2-player symmetric skirmish map from <paramref name="seed"/>. Same seed +
        /// same parameters → byte-identical <see cref="ScenarioData"/> on every machine; a different seed drives
        /// a different layout (the seed is the sole entropy source).
        /// </summary>
        /// <param name="seed">Stream seed for the single <see cref="SimRng"/>. Any value is valid (including 0).</param>
        /// <param name="mapBounds">Playable half-extent (map_bounds). Must be &gt;= <c>2 x EdgeMargin</c> (30) so both
        /// point-symmetric bases fit inside the margin; guarded below (throws <see cref="ArgumentOutOfRangeException"/>).</param>
        /// <param name="resourceNodeCount">Target number of resource nodes (fewer if the grid runs out under spacing).</param>
        public static ScenarioData Generate(ulong seed, int mapBounds = DefaultMapBounds,
            int resourceNodeCount = DefaultResourceNodeCount)
        {
            // Precondition (Story 1.11 review patch): both point-symmetric bases are drawn from
            // [EdgeMargin, interior], which needs interior >= EdgeMargin, i.e. mapBounds >= 2*EdgeMargin.
            // Below that, rng.NextInt(interior - EdgeMargin + 1) gets a non-positive count and SimRng.NextInt
            // throws; fail fast with a located message instead of a confusing deep-in-the-RNG throw.
            if (mapBounds < 2 * EdgeMargin)
                throw new ArgumentOutOfRangeException(nameof(mapBounds),
                    $"must be >= {2 * EdgeMargin} (2 x EdgeMargin) so both player bases fit inside the edge margin.");

            var rng = new SimRng(seed);
            int interior = mapBounds - EdgeMargin;            // bases/nodes stay within ±interior

            // ── Player bases: two POINT-SYMMETRIC bases (canonical 1v1). Magnitudes drawn from SimRng, placed at
            //    (-bx, bz) and (+bx, -bz) → symmetric through the origin, both well inside ±mapBounds. ──
            int bx = EdgeMargin + rng.NextInt(interior - EdgeMargin + 1); // [EdgeMargin, interior]
            int bz = rng.NextInt(2 * interior + 1) - interior;           // [-interior, interior]
            var slots = new[]
            {
                MakeSlot(0, -bx,  bz),
                MakeSlot(1,  bx, -bz),
            };

            // ── Resource nodes: collect ALL candidate grid cells in ASCENDING (x, z) order BEFORE any draw
            //    (AR-15), then SimRng-pick with a ≥NodeSpacing separation enforced by removing the neighborhood
            //    of each pick. Integer grid → Fixed-exact, cross-platform stable. ──
            var candidates = new List<(int X, int Z)>();
            for (int x = -interior; x <= interior; x += GridStep)
                for (int z = -interior; z <= interior; z += GridStep)
                    candidates.Add((x, z));                  // nested ascending loops → deterministic ascending order

            var nodes = new List<ScenarioResourceNode>();
            while (nodes.Count < resourceNodeCount && candidates.Count > 0)
            {
                int pick = rng.NextInt(candidates.Count);
                (int nx, int nz) = candidates[pick];
                nodes.Add(MakeNode(nx, nz));

                // Enforce ≥NodeSpacing: drop every remaining candidate inside the Chebyshev radius. A survivor has
                // |dx| >= NodeSpacing OR |dz| >= NodeSpacing → Euclidean distance ≥ NodeSpacing. Integer-only.
                candidates.RemoveAll(c =>
                    c.X > nx - NodeSpacing && c.X < nx + NodeSpacing &&
                    c.Z > nz - NodeSpacing && c.Z < nz + NodeSpacing);
            }

            // Seed-INDEPENDENT id/name: the only thing that can differ between two seeds is the generated geometry,
            // so a "different seed → different serialization" check genuinely proves the seed drives GENERATION
            // (not just the label).
            return new ScenarioData
            {
                Id = "procedural",
                DisplayName = "Procedural Skirmish",
                TerrainRef = "",
                MapBounds = mapBounds,
                WinCondition = WinCondition.DestroyAllBuildings,
                PlayerSlots = slots,
                ResourceNodes = nodes.ToArray(),
                Buildings = Array.Empty<ScenarioBuilding>(),
                Units = Array.Empty<ScenarioUnit>(),
                Triggers = Array.Empty<TriggerDefinition>(),
            };
        }

        /// <summary>A player slot at an integer base position (exact int → float at the output boundary).</summary>
        private static ScenarioPlayerSlot MakeSlot(int slot, int baseX, int baseZ) => new ScenarioPlayerSlot
        {
            Slot = slot,
            FactionJson = "",
            StartOre = 200,
            BaseX = baseX,
            BaseZ = baseZ,
        };

        /// <summary>A resource node at an integer grid position with the standard ore/rate/gatherer defaults.</summary>
        private static ScenarioResourceNode MakeNode(int x, int z) => new ScenarioResourceNode
        {
            X = x,
            Z = z,
            Supply = 400,
            Rate = 5,
            MaxGatherers = 4,
        };
    }
}
