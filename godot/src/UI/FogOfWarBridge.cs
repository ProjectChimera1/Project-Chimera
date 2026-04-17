using Godot;
using ProjectChimera.Core;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Presents the FogOfWarSystem grid as a fullscreen plane overlay with a GPU shader.
    ///
    /// Each frame:
    ///   1. Copies the byte[] Grid into an Image (R8 format, one pixel per cell).
    ///   2. Uploads it to an ImageTexture bound to a ShaderMaterial.
    ///   3. The shader samples the texture and outputs:
    ///        0 (Unexplored) → opaque black
    ///        1 (Explored)   → semi-transparent dark overlay
    ///        2 (Visible)    → fully transparent (no fog)
    ///
    /// The plane sits at Y = 1.5 (above terrain, below units) and covers 256×256 world units.
    /// </summary>
    public partial class FogOfWarBridge : Node3D
    {
        private FogOfWarSystem _fog = null!;
        private ImageTexture   _texture = null!;
        private Image          _image   = null!;

        /// <summary>
        /// When true, skips the per-cell fog calculation and renders the entire map as fully
        /// visible (no fog). Used for spectator mode so observers see both factions.
        /// </summary>
        public bool RevealAll { get; set; }

        public void Initialize(FogOfWarSystem fog)
        {
            _fog = fog;

            // Create the image once (R8 format: 1 byte per pixel = our cell state)
            _image = Image.CreateFromData(
                FogOfWarSystem.GRID_SIZE,
                FogOfWarSystem.GRID_SIZE,
                false,
                Image.Format.R8,
                new byte[FogOfWarSystem.GRID_SIZE * FogOfWarSystem.GRID_SIZE]);

            _texture = ImageTexture.CreateFromImage(_image);

            // Build overlay plane
            var mesh = new MeshInstance3D();
            var plane = new PlaneMesh();
            plane.Size = new Vector2(256f, 256f);
            plane.Material = BuildFogShader();
            mesh.Mesh = plane;

            // Slightly above terrain (Y=0), below units
            mesh.Position = new Vector3(0f, 1.5f, 0f);
            AddChild(mesh);
        }

        public override void _Process(double delta)
        {
            if (_fog == null) return;

            // Spectator mode: reveal entire map
            if (RevealAll)
            {
                int totalPixels = FogOfWarSystem.GRID_SIZE * FogOfWarSystem.GRID_SIZE;
                byte[] allVisible = new byte[totalPixels];
                for (int i = 0; i < totalPixels; i++) allVisible[i] = 255;
                _image.SetData(FogOfWarSystem.GRID_SIZE, FogOfWarSystem.GRID_SIZE,
                    false, Image.Format.R8, allVisible);
                _texture.Update(_image);
                return;
            }

            // Upload grid to image
            // Grid bytes: 0=Unexplored, 1=Explored, 2=Visible
            // We scale to 0, 127, 255 so the shader can distinguish clearly.
            byte[] grid = _fog.Grid;
            int size = FogOfWarSystem.GRID_SIZE * FogOfWarSystem.GRID_SIZE;
            byte[] pixels = new byte[size];
            for (int i = 0; i < size; i++)
            {
                pixels[i] = grid[i] switch
                {
                    FogOfWarSystem.UNEXPLORED => 0,
                    FogOfWarSystem.EXPLORED   => 127,
                    FogOfWarSystem.VISIBLE    => 255,
                    _                         => 0,
                };
            }

            _image.SetData(FogOfWarSystem.GRID_SIZE, FogOfWarSystem.GRID_SIZE,
                false, Image.Format.R8, pixels);
            _texture.Update(_image);
        }

        // ── Shader ────────────────────────────────────────────────────────────

        private ShaderMaterial BuildFogShader()
        {
            var shader = new Shader();
            shader.Code = @"
shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_disabled, unshaded;

uniform sampler2D fog_texture : hint_default_black, filter_nearest;

void fragment() {
    // UV goes 0→1 across the plane; fog_texture is row-major with row=Z, col=X
    // UV.x = X direction, UV.y = Z direction in Godot's PlaneMesh
    float v = texture(fog_texture, UV).r;

    // v ≈ 0.0   → unexplored: fully opaque black
    // v ≈ 0.498 → explored:   semi-transparent dark tint
    // v ≈ 1.0   → visible:    fully transparent

    float alpha;
    float brightness;

    if (v < 0.3) {
        // Unexplored
        alpha      = 0.92;
        brightness = 0.0;
    } else if (v < 0.75) {
        // Explored — blend between unexplored and explored based on v
        float t    = (v - 0.3) / 0.45;
        alpha      = mix(0.92, 0.55, t);
        brightness = 0.0;
    } else {
        // Visible — fade out
        float t = (v - 0.75) / 0.25;
        alpha   = mix(0.55, 0.0, t);
        brightness = 0.0;
    }

    ALBEDO = vec3(brightness);
    ALPHA  = alpha;
}
";

            var mat = new ShaderMaterial();
            mat.Shader = shader;
            mat.SetShaderParameter("fog_texture", _texture);
            return mat;
        }
    }
}
