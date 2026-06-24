#nullable enable
using Godot;
using System;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 1.8c "Terrain" phase (runtime position 5). Creates the world ground: a Terrain3D region when the
    /// GDExtension is available, else a flat PlaneMesh with the editor-grid shader. Publishes
    /// <see cref="SceneContext.Terrain"/> (null on the PlaneMesh fallback), consumed by Navigation and
    /// TerrainBrush. Behavior-identical to the former MainScene.SetupTerrain (+ TryCreateTerrain3D / BuildGridShader).
    /// </summary>
    public sealed class TerrainPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public TerrainPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "Terrain";

        public void Run()
        {
            Node3D? terrain = null;
            if (ClassDB.ClassExists("Terrain3D") && ClassDB.CanInstantiate("Terrain3D"))
                terrain = TryCreateTerrain3D();

            if (terrain == null)
            {
                // Fallback: flat plane with editor-grid shader
                var ground = new MeshInstance3D();
                var plane  = new PlaneMesh { Size = new Vector2(256, 256) };
                plane.Material = new ShaderMaterial { Shader = BuildGridShader() };
                ground.Mesh = plane;
                _ctx.Scene.AddChild(ground);
                GD.Print("[Terrain] Terrain3D unavailable — using flat PlaneMesh.");
            }

            _ctx.Terrain = terrain;
        }

        /// <summary>
        /// Dynamically instantiate a Terrain3D node and import a flat 256×256 heightmap region centred at the
        /// origin, covering ±128 world units in XZ at Y=0. Returns the node on success, or null if anything fails.
        /// </summary>
        private Node3D? TryCreateTerrain3D()
        {
            try
            {
                var obj = ClassDB.Instantiate("Terrain3D").AsGodotObject();
                if (obj is not Node3D terrain)
                {
                    GD.PrintErr("[Terrain] ClassDB.Instantiate(Terrain3D) did not return a Node3D.");
                    return null;
                }

                terrain.Name = "Terrain3D";
                _ctx.Scene.AddChild(terrain);

                // Set region size before importing data
                terrain.Set("region_size", 256);

                // Flat heightmap: 256×256 RF (32-bit float) image, all zeros = Y=0
                var heightImg = Image.CreateEmpty(256, 256, false, Image.Format.Rf);

                // import_images([heightmap, control_map, color_map], global_pos, offset, scale)
                // Position (-128, 0, -128) centres the region on the world origin.
                var images = new Godot.Collections.Array
                {
                    Variant.From(heightImg),
                    new Variant(), // control map — null/nil
                    new Variant(), // color map  — null/nil
                };
                var terrainData = terrain.Get("data").AsGodotObject();
                terrainData?.Call("import_images", images,
                                  new Vector3(-128f, 0f, -128f), 0f, 1f);

                GD.Print("[Terrain] Terrain3D v1.0.1 initialised — flat 256×256 region (-128..+128 XZ).");
                return terrain;
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[Terrain] Terrain3D setup failed ({ex.Message}) — falling back to PlaneMesh.");
                return null;
            }
        }

        private static Shader BuildGridShader()
        {
            var s = new Shader();
            s.Code = @"
shader_type spatial;
render_mode diffuse_lambert, specular_disabled;
void fragment() {
    vec2 worldUV = UV * 256.0;
    vec2 grid = abs(fract(worldUV) - 0.5) * 2.0;
    float line = max(grid.x, grid.y);
    float mask = smoothstep(0.85, 0.95, line);
    vec3 baseColor = vec3(0.10, 0.16, 0.10);
    vec3 lineColor = vec3(0.20, 0.30, 0.18);
    ALBEDO = mix(baseColor, lineColor, mask * 0.6);
    ROUGHNESS = 1.0;
}
";
            return s;
        }
    }
}
