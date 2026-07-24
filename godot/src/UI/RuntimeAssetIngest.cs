#nullable enable
using System;
using Godot;
using ProjectChimera.Core.Definitions;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 9.9 — runtime ingest of a downloaded custom <c>.glb</c> mesh.
    ///
    /// Unlike <see cref="MeshLoader.LoadFromGlb"/> (which uses <c>GD.Load&lt;PackedScene&gt;</c> and therefore needs an
    /// editor <c>.import</c> sidecar that a downloaded asset never has), this path loads the raw GLB at runtime via
    /// <c>GltfDocument.AppendFromFile → GenerateScene</c>, so it works in a non-editor/exported build.
    ///
    /// Every failure mode — non-allow-listed extension, over the size cap, over the vertex/surface cap, a malformed
    /// GLB, a GLB with no <see cref="MeshInstance3D"/>, or any thrown exception — falls back to the shared box
    /// placeholder and NEVER throws to the caller. An un-validated asset is never left in the scene: the temporary
    /// generated scene is freed and only its extracted <see cref="Mesh"/> is returned (or the box).
    /// </summary>
    public static class RuntimeAssetIngest
    {
        /// <summary>
        /// Ingest the GLB at <paramref name="absGlbPath"/> (an absolute OS path to an extracted asset). Returns the
        /// first valid mesh, or a box placeholder (of <paramref name="fallbackSize"/>/<paramref name="fallbackColor"/>)
        /// on any validation failure or exception. Never throws.
        /// </summary>
        public static Mesh Ingest(string absGlbPath, Vector3 fallbackSize, Color fallbackColor)
        {
            try
            {
                // ── Pre-validate: extension + size, before the loader ever opens the file. ──
                string name = System.IO.Path.GetFileName(absGlbPath);
                long size = System.IO.File.Exists(absGlbPath)
                    ? new System.IO.FileInfo(absGlbPath).Length
                    : -1L;
                var pre = AssetValidator.Validate(name, size);
                if (!pre.Ok)
                {
                    GD.PrintErr($"[RuntimeAssetIngest] Rejected '{name}': {pre.Reason}");
                    return MeshLoader.MakePlaceholder(fallbackSize, fallbackColor);
                }

                // ── Runtime GLB load (non-editor safe): AppendFromFile → GenerateScene. ──
                var doc = new GltfDocument();
                var state = new GltfState();
                if (doc.AppendFromFile(absGlbPath, state) != Error.Ok)
                {
                    GD.PrintErr($"[RuntimeAssetIngest] GltfDocument.AppendFromFile failed for '{name}'.");
                    return MeshLoader.MakePlaceholder(fallbackSize, fallbackColor);
                }

                Node? scene = doc.GenerateScene(state);
                Mesh? mesh = scene != null ? MeshLoader.FindFirstMesh(scene) : null;
                // Free (not QueueFree): the generated scene is never parented into the tree, so release it immediately.
                // A deferred QueueFree would pile up whole scene trees across a multi-asset IngestPackage.
                scene?.Free();

                if (mesh == null)
                {
                    GD.PrintErr($"[RuntimeAssetIngest] GLB '{name}' contained no MeshInstance3D.");
                    return MeshLoader.MakePlaceholder(fallbackSize, fallbackColor);
                }

                // ── Post-validate: mesh complexity (a within-size GLB can still be a hostile mega-mesh). ──
                var post = AssetValidator.ValidateMeshComplexity(CountVertices(mesh), mesh.GetSurfaceCount());
                if (!post.Ok)
                {
                    GD.PrintErr($"[RuntimeAssetIngest] Rejected '{name}': {post.Reason}");
                    return MeshLoader.MakePlaceholder(fallbackSize, fallbackColor);
                }

                return mesh;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[RuntimeAssetIngest] Ingest threw for '{absGlbPath}': {ex.Message}");
                return MeshLoader.MakePlaceholder(fallbackSize, fallbackColor);
            }
        }

        /// <summary>Total vertex count across every surface of <paramref name="mesh"/>. Returns 0 for a mesh whose
        /// surface arrays cannot be read (which the complexity gate then rejects as "no surfaces" if applicable).</summary>
        private static int CountVertices(Mesh mesh)
        {
            int total = 0;
            int surfaces = mesh.GetSurfaceCount();
            for (int s = 0; s < surfaces; s++)
            {
                var arrays = mesh.SurfaceGetArrays(s);
                int vi = (int)Mesh.ArrayType.Vertex;
                if (arrays.Count > vi)
                {
                    var verts = arrays[vi].AsVector3Array();
                    total += verts.Length;
                }
            }
            return total;
        }
    }
}
