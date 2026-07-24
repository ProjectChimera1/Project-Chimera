#nullable enable
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 9.9 — the Godot-free asset-ingest decision core: extension allow/deny, the size-cap boundary, and the
    /// mesh-complexity (vertex/surface) cap boundaries. Pins the load-bearing caps so a later "cleanup" that loosens
    /// them fails a red test.
    /// </summary>
    public class AssetValidatorTests
    {
        // ── Extension allow-list ────────────────────────────────────────────────

        [Theory]
        [InlineData("heavy_tank.glb", true)]
        [InlineData("model.gltf", false)]      // .gltf excluded: a single file can't carry its .bin/texture sidecars
        [InlineData("MODEL.GLB", true)]        // case-insensitive
        [InlineData("evil.exe", false)]
        [InlineData("portrait.png", false)]
        [InlineData("theme.ogg", false)]
        [InlineData("noext", false)]
        public void Validate_ExtensionAllowList(string fileName, bool expectOk)
            => Assert.Equal(expectOk, AssetValidator.Validate(fileName, 1024).Ok);

        [Theory]
        [InlineData(".glb", true)]
        [InlineData(".GLB", true)]
        [InlineData(".gltf", false)]
        [InlineData(".exe", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsAllowedExtension_Matches(string? ext, bool expected)
            => Assert.Equal(expected, AssetValidator.IsAllowedExtension(ext));

        // ── Size cap boundary ───────────────────────────────────────────────────

        [Fact]
        public void Validate_SizeAtCap_Passes()
            => Assert.True(AssetValidator.Validate("a.glb", AssetValidator.MaxAssetBytes).Ok);

        [Fact]
        public void Validate_SizeOverCap_Fails()
            => Assert.False(AssetValidator.Validate("a.glb", AssetValidator.MaxAssetBytes + 1).Ok);

        [Fact]
        public void Validate_NegativeSize_Fails()
            => Assert.False(AssetValidator.Validate("a.glb", -1).Ok);

        [Fact]
        public void Validate_FailureCarriesReason()
        {
            var r = AssetValidator.Validate("evil.exe", 10);
            Assert.False(r.Ok);
            Assert.False(string.IsNullOrEmpty(r.Reason));
        }

        // ── Mesh-complexity cap boundaries ──────────────────────────────────────

        [Fact]
        public void MeshComplexity_AtCaps_Passes()
            => Assert.True(AssetValidator
                .ValidateMeshComplexity(AssetValidator.MaxVertexCount, AssetValidator.MaxSurfaceCount).Ok);

        [Fact]
        public void MeshComplexity_OverVertexCap_Fails()
            => Assert.False(AssetValidator
                .ValidateMeshComplexity(AssetValidator.MaxVertexCount + 1, 1).Ok);

        [Fact]
        public void MeshComplexity_OverSurfaceCap_Fails()
            => Assert.False(AssetValidator
                .ValidateMeshComplexity(100, AssetValidator.MaxSurfaceCount + 1).Ok);

        [Fact]
        public void MeshComplexity_NoSurfaces_Fails()
            => Assert.False(AssetValidator.ValidateMeshComplexity(100, 0).Ok);

        [Fact]
        public void MeshComplexity_ZeroVerticesWithSurface_Fails() // review P5: unreadable-vertex mesh must fail closed
            => Assert.False(AssetValidator.ValidateMeshComplexity(0, 1).Ok);

        [Fact]
        public void MeshComplexity_MinimalValidMesh_Passes()
            => Assert.True(AssetValidator.ValidateMeshComplexity(3, 1).Ok);
    }
}
