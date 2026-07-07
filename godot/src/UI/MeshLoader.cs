#nullable enable
using Godot;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Loads a Mesh resource from a GLB file for use with MultiMeshInstance3D.
    ///
    /// GLBs import as PackedScenes in Godot. This helper instantiates the scene,
    /// extracts the first MeshInstance3D's mesh, and frees the temporary instance.
    ///
    /// Falls back to a box placeholder if the file doesn't exist yet — so the game
    /// runs during development before all art assets are ready.
    /// </summary>
    public static class MeshLoader
    {
        /// <summary>
        /// Load a Mesh from a GLB at the given res:// path.
        /// Returns a box placeholder if the file is missing or fails to load.
        /// </summary>
        public static Mesh LoadFromGlb(string resPath, Vector3 fallbackSize, Color fallbackColor)
            => LoadFromGlb(resPath, fallbackSize, fallbackColor, out _);

        /// <summary>
        /// Load a Mesh from a GLB at the given res:// path, reporting whether the box placeholder was used.
        /// <paramref name="usedPlaceholder"/> is set true whenever the box is returned — because the path was
        /// empty/missing OR because the GLB loaded but yielded no <see cref="MeshInstance3D"/>. This lets the
        /// in-panel preview flag a model that fails to load, not just a missing path (Story 3.5, D-3). The
        /// 3-arg overload delegates here, so world-spawn callers (MultiMeshBridge/BuildingBridge) are untouched.
        /// </summary>
        public static Mesh LoadFromGlb(string resPath, Vector3 fallbackSize, Color fallbackColor, out bool usedPlaceholder)
        {
            if (!string.IsNullOrEmpty(resPath) && ResourceLoader.Exists(resPath))
            {
                var packed = GD.Load<PackedScene>(resPath);
                if (packed != null)
                {
                    var instance = packed.Instantiate();
                    var mesh = FindFirstMesh(instance);
                    instance.Free();

                    if (mesh != null)
                    {
                        usedPlaceholder = false;
                        return mesh;
                    }
                }
                GD.PrintErr($"[MeshLoader] GLB loaded but contained no MeshInstance3D: {resPath}");
            }
            else
            {
                GD.Print($"[MeshLoader] '{resPath}' not found — using box placeholder.");
            }

            usedPlaceholder = true;
            return MakePlaceholder(fallbackSize, fallbackColor);
        }

        /// <summary>
        /// Apply a uniform scale to a mesh via a temporary MeshInstance3D — used to match
        /// the mesh_scale field in UnitDefinition without modifying the source asset.
        /// Returns the original mesh (Godot meshes don't embed scale; apply on the Node instead).
        /// Callers should set MeshInstance3D.Scale after using this.
        /// </summary>
        public static Vector3 ScaleFromDefinition(float meshScale) =>
            new Vector3(meshScale, meshScale, meshScale);

        // ── Internals ────────────────────────────────────────────────────────────

        private static Mesh? FindFirstMesh(Node root)
        {
            if (root is MeshInstance3D mi && mi.Mesh != null)
                return mi.Mesh;

            foreach (Node child in root.GetChildren())
            {
                var found = FindFirstMesh(child);
                if (found != null) return found;
            }

            return null;
        }

        private static Mesh MakePlaceholder(Vector3 size, Color color)
        {
            var box = new BoxMesh();
            box.Size = size;
            var mat = new StandardMaterial3D();
            mat.AlbedoColor = color;
            box.Material = mat;
            return box;
        }
    }
}
