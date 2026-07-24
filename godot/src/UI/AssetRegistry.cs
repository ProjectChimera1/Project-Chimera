#nullable enable
using System.Collections.Generic;
using Godot;
using ProjectChimera.Core.Definitions;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 9.9 — a runtime map from a package's zip-relative logical asset id (e.g. "assets/heavy_tank.glb") to the
    /// ingested <see cref="Mesh"/>. A custom unit's <c>MeshPath</c> referencing that logical id resolves through here
    /// (see <see cref="MeshLoader.LoadFromGlb(string, Vector3, Color, AssetRegistry?)"/>); a <c>res://</c> path keeps
    /// the editor load path, and an absent id falls back to the box placeholder — so a missing/invalid asset never
    /// crashes rendering.
    ///
    /// One instance is owned by the render/session root (the bootstrap <c>SceneContext</c>) and shared between the
    /// content browser (which populates it on download via <see cref="IngestPackage"/>) and the unit render bridge
    /// (which consults it when loading per-type meshes).
    /// </summary>
    public sealed class AssetRegistry
    {
        // A neutral placeholder box for a failed/invalid ingest; team colour is applied via material_override at render
        // time, so the registered mesh's own colour is irrelevant.
        private static readonly Vector3 FallbackSize  = new(0.6f, 1.2f, 0.6f);
        private static readonly Color   FallbackColor = new(0.6f, 0.6f, 0.6f);

        private readonly Dictionary<string, Mesh> _meshes = new();

        /// <summary>Register (or replace) the mesh for a logical id.</summary>
        public void Register(string logicalId, Mesh mesh) => _meshes[logicalId] = mesh;

        /// <summary>Look up the mesh for a logical id. Returns false (and null) if absent.</summary>
        public bool TryGet(string logicalId, out Mesh mesh) => _meshes.TryGetValue(logicalId, out mesh!);

        /// <summary>Number of registered assets (for diagnostics/inspection).</summary>
        public int Count => _meshes.Count;

        /// <summary>
        /// Ingest every allow-listed asset a package extracted under <paramref name="extractDir"/>/<c>assets/</c> and
        /// register each under its zip-relative logical id (the entries in <paramref name="assetFiles"/>, e.g.
        /// "assets/heavy_tank.glb" — the same values as <c>UnpackResult.Manifest.AssetFiles</c>). A non-allow-listed
        /// extension is skipped entirely (no scene load); a valid-extension asset is ingested via
        /// <see cref="RuntimeAssetIngest"/>, which registers a box placeholder on any invalid/unsafe/malformed asset.
        /// </summary>
        public void IngestPackage(string extractDir, IEnumerable<string> assetFiles)
        {
            if (assetFiles == null) return;

            foreach (var logicalId in assetFiles)
            {
                string name = System.IO.Path.GetFileName(logicalId);
                string ext = System.IO.Path.GetExtension(name);
                if (!AssetValidator.IsAllowedExtension(ext))
                {
                    GD.Print($"[AssetRegistry] Skipping non-allow-listed asset '{logicalId}'.");
                    continue;
                }

                string absPath = System.IO.Path.Combine(extractDir, "assets", name);
                Mesh mesh = RuntimeAssetIngest.Ingest(absPath, FallbackSize, FallbackColor);
                Register(logicalId, mesh);
            }
        }
    }
}
