#nullable enable
using System;
using System.IO;
using System.Text.Json;
using ProjectChimera.Core.Definitions;
using ProjectChimera.UI;
using Xunit;

namespace ProjectChimera.Sim.Tests.UI
{
    /// <summary>
    /// DW-169 — the Godot-free building nav-footprint resolution policy. Proves the resolution order
    /// (authored <c>nav_footprint</c> > built-in table > mesh-AABB size > guarded 5×3×5 default), that every
    /// un-authored BUILT-IN id keeps its legacy footprint byte-identically (and never pays a mesh load), that a
    /// custom building's footprint now derives from its definition instead of the old fixed CUSTOM_FOOTPRINT, and
    /// that the authored field's validity rule is enforced at import time (validator + faction JSON load).
    /// </summary>
    public class BuildingNavFootprintTests
    {
        private static BuildingNavFootprint.Size3 S(float x, float y, float z) => new(x, y, z);

        private static void AssertSize(float x, float y, float z, BuildingNavFootprint.Size3 actual)
        {
            Assert.Equal(x, actual.X);
            Assert.Equal(y, actual.Y);
            Assert.Equal(z, actual.Z);
        }

        // ── Resolution order ──────────────────────────────────────────────────

        /// <summary>Every built-in id, un-authored, resolves its exact legacy TYPE_SIZE entry — and the mesh source
        /// is NEVER consulted for a built-in, even when it would yield a different size (legacy nav bake parity).</summary>
        [Theory]
        [InlineData("command_center", 6f, 4f, 6f)]
        [InlineData("barracks",       5f, 3f, 5f)]
        [InlineData("archery_range",  4f, 3f, 5f)]
        [InlineData("siege_workshop", 5f, 3f, 7f)]
        [InlineData("aviary",         5f, 3f, 7f)]
        public void BuiltIn_id_resolves_legacy_table_and_never_loads_mesh(string id, float x, float y, float z)
        {
            int meshCalls = 0;
            var def = new BuildingDefinition(); // no nav_footprint authored
            var got = BuildingNavFootprint.Resolve(id, def, () => { meshCalls++; return S(99f, 99f, 99f); });

            AssertSize(x, y, z, got);
            Assert.Equal(0, meshCalls); // lazy Func — built-ins never pay a GLB load
        }

        /// <summary>A custom id with no def at all (or no mesh) keeps the old guarded 5×3×5 default — the exact
        /// pre-DW-169 behavior for the no-information case.</summary>
        [Fact]
        public void Custom_id_with_no_def_and_no_mesh_uses_guarded_default()
        {
            AssertSize(5f, 3f, 5f, BuildingNavFootprint.Resolve("watchtower", def: null, meshSize: null));
            AssertSize(5f, 3f, 5f, BuildingNavFootprint.Resolve("watchtower", new BuildingDefinition(), () => null));
            AssertSize(5f, 3f, 5f, BuildingNavFootprint.Resolve("", def: null, meshSize: null));
            AssertSize(5f, 3f, 5f, BuildingNavFootprint.Resolve(null, def: null, meshSize: null));
        }

        /// <summary>THE DW-169 headline: a custom building's footprint derives from its mesh AABB size instead of
        /// the fixed 5×3×5 box, so a large authored building blocks what it renders.</summary>
        [Fact]
        public void Custom_id_without_authored_footprint_uses_mesh_size()
        {
            var def = new BuildingDefinition();
            var got = BuildingNavFootprint.Resolve("watchtower", def, () => S(10.5f, 5.25f, 7.75f));
            AssertSize(10.5f, 5.25f, 7.75f, got);
        }

        /// <summary>An authored nav_footprint is the explicit override — it wins over the mesh for a custom id, and
        /// the mesh source is not even invoked (authored short-circuits).</summary>
        [Fact]
        public void Custom_id_with_authored_footprint_uses_authored_and_skips_mesh()
        {
            int meshCalls = 0;
            var def = new BuildingDefinition { NavFootprint = new[] { 8f, 4f, 6f } };
            var got = BuildingNavFootprint.Resolve("watchtower", def, () => { meshCalls++; return S(10f, 10f, 10f); });

            AssertSize(8f, 4f, 6f, got);
            Assert.Equal(0, meshCalls);
        }

        /// <summary>The "built-ins alike" hook: an authored nav_footprint on a BUILT-IN id overrides its legacy
        /// table entry too.</summary>
        [Fact]
        public void BuiltIn_id_with_authored_footprint_prefers_authored()
        {
            var def = new BuildingDefinition { NavFootprint = new[] { 7f, 5f, 7f } };
            AssertSize(7f, 5f, 7f, BuildingNavFootprint.Resolve("command_center", def, meshSize: null));
        }

        /// <summary>A malformed authored footprint (already a located import-time error) falls through to the next
        /// source instead of producing a degenerate obstacle.</summary>
        [Theory]
        [InlineData(new float[0])]                                  // empty
        [InlineData(new[] { 5f, 3f })]                              // wrong length
        [InlineData(new[] { 5f, 3f, 5f, 1f })]                      // wrong length
        [InlineData(new[] { 0f, 3f, 5f })]                          // zero component
        [InlineData(new[] { 5f, -3f, 5f })]                         // negative component
        [InlineData(new[] { 5f, float.NaN, 5f })]                   // non-finite
        [InlineData(new[] { float.PositiveInfinity, 3f, 5f })]      // non-finite
        public void Malformed_authored_footprint_falls_through(float[] bad)
        {
            var def = new BuildingDefinition { NavFootprint = bad };

            // Custom id: falls to the mesh size when one is available…
            AssertSize(9f, 4f, 9f, BuildingNavFootprint.Resolve("watchtower", def, () => S(9f, 4f, 9f)));
            // …or to the guarded default without one.
            AssertSize(5f, 3f, 5f, BuildingNavFootprint.Resolve("watchtower", def, () => null));
            // Built-in id: falls to its legacy table entry.
            AssertSize(5f, 3f, 5f, BuildingNavFootprint.Resolve("barracks", def, meshSize: null));
        }

        /// <summary>A degenerate or non-finite mesh-derived size (broken GLB / placeholder leak) is rejected — the
        /// guarded default applies, never a zero/NaN nav obstacle.</summary>
        [Theory]
        [InlineData(0f, 5f, 5f)]
        [InlineData(5f, -1f, 5f)]
        [InlineData(float.NaN, 5f, 5f)]
        [InlineData(5f, 5f, float.PositiveInfinity)]
        public void Degenerate_mesh_size_is_rejected(float x, float y, float z)
        {
            var def = new BuildingDefinition();
            AssertSize(5f, 3f, 5f, BuildingNavFootprint.Resolve("watchtower", def, () => S(x, y, z)));
        }

        /// <summary>The moved built-in table is byte-identical to the old NavObstacleManager.TYPE_SIZE /
        /// BuildingBridge.TYPE_FALLBACK values — the cross-class "visual and nav obstacle agree" invariant.</summary>
        [Fact]
        public void BuiltIn_table_matches_legacy_values_exactly()
        {
            var t = BuildingNavFootprint.BUILT_IN_FOOTPRINT;
            Assert.Equal(5, t.Length);
            AssertSize(6f, 4f, 6f, t[0]); // CommandCenter
            AssertSize(5f, 3f, 5f, t[1]); // Barracks
            AssertSize(4f, 3f, 5f, t[2]); // ArcheryRange
            AssertSize(5f, 3f, 7f, t[3]); // SiegeWorkshop
            AssertSize(5f, 3f, 7f, t[4]); // Aviary
            AssertSize(5f, 3f, 5f, BuildingNavFootprint.CUSTOM_FOOTPRINT);
        }

        // ── TryGetNavFootprint (the shared validity rule) ─────────────────────

        [Fact]
        public void TryGetNavFootprint_omitted_is_false()
        {
            var def = new BuildingDefinition();
            Assert.False(def.TryGetNavFootprint(out _, out _, out _));
        }

        [Fact]
        public void TryGetNavFootprint_valid_returns_components()
        {
            var def = new BuildingDefinition { NavFootprint = new[] { 8f, 4f, 6f } };
            Assert.True(def.TryGetNavFootprint(out float x, out float y, out float z));
            Assert.Equal(8f, x);
            Assert.Equal(4f, y);
            Assert.Equal(6f, z);
        }

        [Theory]
        [InlineData(new float[0])]
        [InlineData(new[] { 5f, 3f })]
        [InlineData(new[] { 5f, 3f, 5f, 1f })]
        [InlineData(new[] { 0f, 3f, 5f })]
        [InlineData(new[] { 5f, -3f, 5f })]
        [InlineData(new[] { 5f, float.NaN, 5f })]
        [InlineData(new[] { float.NegativeInfinity, 3f, 5f })]
        public void TryGetNavFootprint_malformed_is_false(float[] bad)
        {
            var def = new BuildingDefinition { NavFootprint = bad };
            Assert.False(def.TryGetNavFootprint(out _, out _, out _));
        }

        // ── Import-time validation ────────────────────────────────────────────

        private static BuildingDefinition ValidDef(float[]? navFootprint = null) => new()
        {
            Id = "watchtower",
            DisplayName = "Watchtower",
            Category = "Structure",
            Hp = 400f, // BuildingDefinition-typed initializer → HpAuthored (DW-55)
            ConstructionTime = 12f,
            SupplyBonus = 0,
            ProducesCategory = "None",
            NavFootprint = navFootprint,
        };

        [Fact]
        public void Validator_accepts_omitted_and_valid_nav_footprint()
        {
            Assert.True(BuildingDefinitionValidator.Validate(ValidDef()).Ok);
            Assert.True(BuildingDefinitionValidator.Validate(ValidDef(new[] { 8f, 4f, 6f })).Ok);
        }

        [Theory]
        [InlineData(new float[0])]
        [InlineData(new[] { 5f, 3f })]
        [InlineData(new[] { 0f, 3f, 5f })]
        [InlineData(new[] { 5f, float.NaN, 5f })]
        public void Validator_rejects_malformed_nav_footprint_with_located_error(float[] bad)
        {
            var result = BuildingDefinitionValidator.Validate(ValidDef(bad));
            Assert.False(result.Ok);
            Assert.Contains(result.Errors, e =>
                e.FieldPath == "nav_footprint" && e.Message.Contains("watchtower") && e.Message.Contains("nav_footprint"));
        }

        // ── Faction JSON round-trip ───────────────────────────────────────────

        private static string FactionJson(string navFootprintJson) => $$"""
        {
          "id": "test_faction",
          "display_name": "Test Faction",
          "units": [],
          "buildings": [
            { "id": "watchtower", "display_name": "Watchtower", "category": "Structure", "hp": 400,
              "construction_time": 12, "supply_bonus": 0, "produces_category": "None"{{navFootprintJson}} }
          ]
        }
        """;

        private static string WriteTempFaction(string json)
        {
            string path = Path.Combine(Path.GetTempPath(), $"chimera_nav_footprint_{Guid.NewGuid():N}.json");
            File.WriteAllText(path, json);
            return path;
        }

        [Fact]
        public void NavFootprint_deserializes_from_faction_json()
        {
            string path = WriteTempFaction(FactionJson(", \"nav_footprint\": [8, 4, 6]"));
            try
            {
                var faction = FactionDefinition.LoadFromFile(path);
                var def = faction.GetBuilding("watchtower");
                Assert.NotNull(def);
                Assert.True(def!.TryGetNavFootprint(out float x, out float y, out float z));
                Assert.Equal(8f, x);
                Assert.Equal(4f, y);
                Assert.Equal(6f, z);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Malformed_nav_footprint_rejected_at_faction_load_naming_id_and_field()
        {
            string path = WriteTempFaction(FactionJson(", \"nav_footprint\": [5, 3]"));
            try
            {
                var ex = Assert.Throws<InvalidOperationException>(() => FactionDefinition.LoadFromFile(path));
                Assert.Contains("watchtower", ex.Message);
                Assert.Contains("nav_footprint", ex.Message);
            }
            finally { File.Delete(path); }
        }

        /// <summary>An omitted nav_footprint serializes to NOTHING (WhenWritingNull) — existing content re-saves
        /// byte-identically; an authored one round-trips.</summary>
        [Fact]
        public void NavFootprint_null_is_omitted_on_serialize_and_value_round_trips()
        {
            string omitted = JsonSerializer.Serialize(ValidDef());
            Assert.DoesNotContain("nav_footprint", omitted);

            string authored = JsonSerializer.Serialize(ValidDef(new[] { 8f, 4f, 6f }));
            Assert.Contains("\"nav_footprint\":[8,4,6]", authored);

            var back = JsonSerializer.Deserialize<BuildingDefinition>(authored);
            Assert.NotNull(back);
            Assert.True(back!.TryGetNavFootprint(out float x, out float y, out float z));
            Assert.Equal(8f, x);
            Assert.Equal(4f, y);
            Assert.Equal(6f, z);
        }
    }
}
