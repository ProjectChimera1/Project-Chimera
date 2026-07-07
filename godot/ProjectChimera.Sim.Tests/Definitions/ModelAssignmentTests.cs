#nullable enable
using ProjectChimera.Core.Definitions;
using Xunit;

namespace ProjectChimera.Sim.Tests.Definitions
{
    /// <summary>
    /// Story 3.5 — <see cref="ModelAssignment"/>: the Godot-free normalization + folder-derivation the Unit Card
    /// model row relies on. <c>NormalizeMeshPath</c> gives the box-placeholder-is-null contract (empty/whitespace
    /// → null, else trimmed); <c>FolderOf</c> derives the AR-5 last-used folder from a chosen res:// path.
    /// </summary>
    public class ModelAssignmentTests
    {
        [Theory]
        [InlineData("", null)]
        [InlineData("   ", null)]                                  // whitespace-only → null (box placeholder)
        [InlineData("\t\n ", null)]
        [InlineData("res://a/b/x.glb", "res://a/b/x.glb")]
        [InlineData("  res://a/b/x.glb  ", "res://a/b/x.glb")]     // trimmed
        public void NormalizeMeshPath_EmptyOrWhitespaceBecomeNull_ElseTrimmed(string? input, string? expected)
        {
            Assert.Equal(expected, ModelAssignment.NormalizeMeshPath(input));
        }

        [Fact]
        public void NormalizeMeshPath_Null_ReturnsNull()
        {
            Assert.Null(ModelAssignment.NormalizeMeshPath(null));
        }

        [Theory]
        [InlineData("res://a/b/x.glb", "res://a/b")]   // parent dir of a res:// path
        [InlineData("res://x.glb", "res:/")]           // only the res:// slashes remain
        [InlineData("x.glb", "")]                      // no slash → ""
        [InlineData("", "")]
        [InlineData(null, "")]
        public void FolderOf_ReturnsParentDir_OrEmpty(string? input, string expected)
        {
            Assert.Equal(expected, ModelAssignment.FolderOf(input));
        }
    }
}
