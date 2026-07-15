#nullable enable
using Godot;
using System.Threading.Tasks;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Story 6.7 — presentation-only helper that snapshots the live world from directly above (the same orthographic
    /// top-down SubViewport pattern <see cref="MinimapBridge"/> uses for its 3D minimap layer) and returns a square
    /// PNG the map-export path writes into the package at <c>preview/preview.png</c>.
    ///
    /// Flow: build a throwaway <see cref="SubViewport"/> + orthographic <see cref="Camera3D"/> pointing straight down,
    /// share the caller's <see cref="World3D"/> so it renders the same terrain/buildings, force a one-shot render,
    /// then <c>GetTexture().GetImage()</c> → resize to the requested size → <c>SavePngToBuffer()</c>. Godot-coupled,
    /// so it is a godot-verify surface (the packaging round-trip that CONSUMES its bytes is Godot-free unit-tested in
    /// <c>ContentPackagerTerrainTests</c>). Returns null on any failure so the export gracefully omits the preview
    /// slot (pre-6.7 package parity) rather than writing a broken image.
    /// </summary>
    public partial class MinimapPreviewRenderer : Node
    {
        /// <summary>Default preview edge length in pixels (mod.io / content-browser card size).</summary>
        public const int DEFAULT_SIZE = 256;

        /// <summary>
        /// Render a top-down preview of <paramref name="world"/> covering ±<paramref name="worldHalfExtent"/> and
        /// return it as PNG bytes (square, <paramref name="size"/>×<paramref name="size"/>). Must be awaited so the
        /// SubViewport has a frame to draw. Returns null if rendering produced no image.
        /// </summary>
        public async Task<byte[]?> RenderPreviewPngAsync(World3D world, float worldHalfExtent, int size = DEFAULT_SIZE)
        {
            if (world == null || size <= 0) return null;

            // Render at a generous internal resolution, then downscale for a clean anti-aliased card image.
            int renderSize = System.Math.Max(size, 512);

            var svp = new SubViewport
            {
                Size                   = new Vector2I(renderSize, renderSize),
                World3D                = world,
                OwnWorld3D             = false,
                RenderTargetClearMode  = SubViewport.ClearMode.Always,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Once,
                TransparentBg          = false,
            };

            float half = worldHalfExtent > 0f ? worldHalfExtent : 128f;
            var cam = new Camera3D
            {
                Projection      = Camera3D.ProjectionType.Orthogonal,
                Size            = half * 2f,          // orthographic frustum spans the full playable width
                Near            = 0.1f,
                Far             = 1000f,
                Position        = new Vector3(0f, 500f, 0f),
                RotationDegrees = new Vector3(-90f, 0f, 0f), // straight down
            };
            svp.AddChild(cam);
            AddChild(svp);

            byte[]? png = null;
            try
            {
                // Give the SubViewport frames to render its one-shot pass.
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

                Image img = svp.GetTexture().GetImage();
                if (img != null && img.GetWidth() > 0 && img.GetHeight() > 0)
                {
                    if (img.GetWidth() != size || img.GetHeight() != size)
                        img.Resize(size, size, Image.Interpolation.Lanczos);
                    png = img.SavePngToBuffer();
                }
            }
            catch (System.Exception ex)
            {
                GD.PrintErr($"[MinimapPreview] Render failed: {ex.Message}");
                png = null;
            }
            finally
            {
                svp.QueueFree();
            }

            return png;
        }
    }
}
