#nullable enable
using System;
using System.IO;

namespace ProjectChimera.Core.Definitions
{
    /// <summary>
    /// Story 9.9 — the Godot-free decision core for runtime custom-asset ingest. Given a downloaded asset's filename +
    /// byte length (pre-generation) or a generated mesh's vertex/surface counts (post-generation), decides whether the
    /// asset is safe to load into the scene tree. Pure — no Godot types, no I/O, no float — so it is Tier-1 unit
    /// testable and the load-bearing caps are pinned by tests rather than living only in the presentation seam.
    ///
    /// The two-phase check mirrors the intent's "validate before (size/extension) and after (mesh complexity)
    /// generation" rule: <see cref="Validate"/> is the cheap pre-gate (never even opens a non-allow-listed or oversized
    /// file); <see cref="ValidateMeshComplexity"/> is the post-gate (a within-size GLB can still decode to a hostile
    /// mega-mesh, so the vertex/surface caps are enforced after <c>GenerateScene</c>).
    /// </summary>
    public static class AssetValidator
    {
        /// <summary>Max on-disk byte size of a single bundled asset (32 MB) — generous for the ~18-30k-vert feet-pivoted
        /// GLBs the project already ships, tight enough to reject a hostile mega-file before it is ever opened.</summary>
        public const long MaxAssetBytes = 32L * 1024 * 1024;

        /// <summary>Max total vertex count across all surfaces of a generated mesh (200k) — a later story can lift it.</summary>
        public const int MaxVertexCount = 200_000;

        /// <summary>Max surface (submesh) count of a generated mesh (16).</summary>
        public const int MaxSurfaceCount = 16;

        /// <summary>The allow-listed asset file extensions (lowercase, with the dot). Only self-contained binary GLB
        /// today: a single bundled file cannot carry a text <c>.gltf</c>'s external <c>.bin</c>/texture sidecars, so
        /// <c>.gltf</c> is deliberately excluded. Image/audio runtime ingest is a documented AR-27 deferral.</summary>
        public static readonly string[] AllowedExtensions = { ".glb" };

        /// <summary>Result of a validation check: <see cref="Ok"/> true = safe to proceed; otherwise
        /// <see cref="Reason"/> carries a human-readable rejection reason for logging.</summary>
        public readonly struct AssetValidationResult
        {
            public bool Ok { get; }
            public string? Reason { get; }
            public AssetValidationResult(bool ok, string? reason)
            {
                Ok = ok;
                Reason = reason;
            }

            public static AssetValidationResult Pass() => new(true, null);
            public static AssetValidationResult Fail(string reason) => new(false, reason);
        }

        /// <summary>Is <paramref name="ext"/> (lowercase, with leading dot, e.g. ".glb") an allow-listed asset
        /// extension? Case-insensitive.</summary>
        public static bool IsAllowedExtension(string? ext)
        {
            if (string.IsNullOrEmpty(ext)) return false;
            foreach (var allowed in AllowedExtensions)
                if (string.Equals(ext, allowed, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>
        /// Pre-generation gate: reject a non-allow-listed extension or an over-cap / unreadable byte length before the
        /// file is ever opened by the GLTF loader. <paramref name="byteLength"/> &lt; 0 signals a missing/unreadable
        /// file (treated as a failure).
        /// </summary>
        public static AssetValidationResult Validate(string fileName, long byteLength)
        {
            string ext = Path.GetExtension(fileName ?? "");
            if (!IsAllowedExtension(ext))
                return AssetValidationResult.Fail(
                    $"Disallowed asset extension '{ext}' (allowed: {string.Join(", ", AllowedExtensions)}).");

            if (byteLength < 0)
                return AssetValidationResult.Fail($"Asset '{fileName}' is missing or unreadable.");

            if (byteLength > MaxAssetBytes)
                return AssetValidationResult.Fail(
                    $"Asset '{fileName}' is {byteLength} bytes, over the {MaxAssetBytes}-byte cap.");

            return AssetValidationResult.Pass();
        }

        /// <summary>
        /// Post-generation gate: reject a generated mesh whose total vertex count or surface (submesh) count exceeds the
        /// caps. A within-size GLB can still decode to a hostile mega-mesh, so this runs after <c>GenerateScene</c>.
        /// </summary>
        public static AssetValidationResult ValidateMeshComplexity(int vertexCount, int surfaceCount)
        {
            if (surfaceCount < 1)
                return AssetValidationResult.Fail("Generated mesh has no surfaces.");

            // Story 9.9 (review P5): a mesh with surfaces but no readable vertices (vertexCount<=0) is
            // malformed/unreadable — fail closed to the placeholder rather than pass it as "0 <= cap".
            if (vertexCount <= 0)
                return AssetValidationResult.Fail("Generated mesh has no readable vertices.");

            if (surfaceCount > MaxSurfaceCount)
                return AssetValidationResult.Fail(
                    $"Generated mesh has {surfaceCount} surfaces, over the {MaxSurfaceCount} cap.");

            if (vertexCount > MaxVertexCount)
                return AssetValidationResult.Fail(
                    $"Generated mesh has {vertexCount} vertices, over the {MaxVertexCount} cap.");

            return AssetValidationResult.Pass();
        }
    }
}
