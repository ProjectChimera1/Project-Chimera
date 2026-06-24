#nullable enable
using Godot;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;
using ProjectChimera.Core.Sim;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Renders all units of one faction, reading entity positions from the simulation
    /// each frame and interpolating between sim ticks for smooth motion.
    ///
    /// Each unit <em>type</em> gets its own <see cref="MultiMeshInstance3D"/> child so
    /// every archetype shows a distinct mesh (worker, soldier, archer, …) instead of one
    /// shared box. Entities are bucketed by <see cref="EntityWorld.MeshType"/>, which is
    /// the index of the unit's definition within its faction's Units list — the same
    /// order this bridge loads its meshes in.
    ///
    /// Team colour: the GLB source art is flat grey with no own colour, so a single
    /// team-coloured <c>material_override</c> (shared across all of this faction's
    /// sub-meshes) supplies the player's identity colour. Unit silhouette distinguishes
    /// archetypes; colour distinguishes teams.
    ///
    /// One bridge per faction.
    /// </summary>
    public partial class MultiMeshBridge : Node3D
    {
        // Render source (Story 1.8a): the bridge reads sim truth (World + per-tick interpolation alpha) each
        // frame. MainScene supplies a SimulationHost; StressTest supplies its own raw SimulationLoop — both
        // expose World + InterpolationAlpha, so the bridge captures the (stable) World plus a live alpha
        // source instead of coupling to either concrete owner (and the host keeps its SimulationLoop private).
        private EntityWorld        _renderWorld = null!;
        private System.Func<float> _alphaSource = () => 0f;
        private Faction            _faction;

        // One MultiMeshInstance3D per unit type (index == EntityWorld.MeshType).
        private MultiMeshInstance3D[] _mmi          = null!;
        private float[]               _scale        = null!; // per-type uniform mesh scale
        private float[]               _groundOffset = null!; // per-type lift so the mesh base sits on Y=0
        private int[]                 _lastCount    = null!; // per-type, avoids needless InstanceCount realloc
        private int[]                 _counts       = null!; // per-frame scratch
        private int[]                 _cursor       = null!; // per-frame scratch
        private int                   _typeCount;
        private bool                  _initialized;

        /// <summary>
        /// Per-unit-type setup. Loads one mesh per unit definition in <paramref name="factionDef"/>
        /// and applies a shared team-coloured material override.
        /// </summary>
        public void Initialize(SimulationHost host, FactionDefinition factionDef,
                               Faction faction, Color teamColor)
        {
            _renderWorld = host.World;
            _alphaSource = () => host.InterpolationAlpha;
            _faction = faction;

            var units = factionDef.Units;
            _typeCount = Mathf.Max(1, units.Count);
            AllocateArrays();

            // One team material shared by every sub-mesh of this faction.
            var teamMat = new StandardMaterial3D
            {
                AlbedoColor = teamColor,
                Roughness   = 0.6f,
                Metallic    = 0.0f,
            };

            var fallbackSize = new Vector3(0.6f, 1.2f, 0.6f);
            for (int t = 0; t < _typeCount; t++)
            {
                var   def   = t < units.Count ? units[t] : null;
                float scale = def?.MeshScale ?? 1f;
                Mesh  mesh  = MeshLoader.LoadFromGlb(def?.MeshPath ?? "", fallbackSize, teamColor);
                _mmi[t] = BuildSubMesh(t, mesh, scale, teamMat, GroundOffsetFor(mesh, scale));
            }

            _initialized = true;
        }

        /// <summary>
        /// Legacy single-mesh setup (StressTest / benchmarks): every unit of the faction
        /// shares one mesh and keeps its own material (no team override). The mesh's own
        /// vertical placement is controlled by <paramref name="yOffset"/>.
        /// </summary>
        public void Initialize(SimulationLoop simLoop, Mesh unitMesh, int maxInstances,
                               Faction faction, float yOffset = 0f)
        {
            _renderWorld = simLoop.World;
            _alphaSource = () => simLoop.InterpolationAlpha;
            _faction = faction;

            _typeCount = 1;
            AllocateArrays();
            _mmi[0] = BuildSubMesh(0, unitMesh, 1f, teamMat: null, groundOffset: yOffset);

            _initialized = true;
        }

        // ── Setup helpers ──────────────────────────────────────────────────────

        private void AllocateArrays()
        {
            _mmi          = new MultiMeshInstance3D[_typeCount];
            _scale        = new float[_typeCount];
            _groundOffset = new float[_typeCount];
            _lastCount    = new int[_typeCount];
            _counts       = new int[_typeCount];
            _cursor       = new int[_typeCount];
        }

        /// <summary>Lift needed so the mesh's lowest point rests on Y=0 (GLBs: 0; boxes: half-height).</summary>
        private static float GroundOffsetFor(Mesh mesh, float scale) =>
            -mesh.GetAabb().Position.Y * scale;

        private MultiMeshInstance3D BuildSubMesh(int type, Mesh mesh, float scale,
                                                 StandardMaterial3D? teamMat, float groundOffset)
        {
            _scale[type]        = scale;
            _groundOffset[type] = groundOffset;

            var mm = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh            = mesh,
                InstanceCount   = 0,
            };

            var mmi = new MultiMeshInstance3D { Multimesh = mm };
            if (teamMat != null) mmi.MaterialOverride = teamMat;
            AddChild(mmi);
            return mmi;
        }

        // ── Per-frame update ───────────────────────────────────────────────────

        public override void _Process(double delta)
        {
            if (!_initialized) return;

            EntityWorld world = _renderWorld;
            float alpha       = _alphaSource();
            int   simCount    = world.HighWaterMark;

            // Pass 1 — count alive faction entities per mesh type.
            System.Array.Clear(_counts, 0, _typeCount);
            for (int i = 0; i < simCount; i++)
            {
                if (!world.IsAlive(i)) continue;
                if (world.FactionOf[i] != _faction) continue;
                _counts[TypeOf(world, i)]++;
            }

            // Resize each sub-mesh only when its count changes (avoids per-frame realloc).
            for (int t = 0; t < _typeCount; t++)
            {
                if (_counts[t] != _lastCount[t])
                {
                    _mmi[t].Multimesh.InstanceCount = _counts[t];
                    _lastCount[t] = _counts[t];
                }
                _cursor[t] = 0;
            }

            // Pass 2 — write interpolated transforms.
            for (int i = 0; i < simCount; i++)
            {
                if (!world.IsAlive(i)) continue;
                if (world.FactionOf[i] != _faction) continue;

                int t = TypeOf(world, i);

                Vector3 prev = world.PrevPosition[i].ToGodotVector3();
                Vector3 curr = world.Position[i].ToGodotVector3();
                Vector3 pos  = prev.Lerp(curr, alpha) + new Vector3(0f, _groundOffset[t], 0f);

                float s = _scale[t];
                var basis = new Basis(new Vector3(s, 0f, 0f),
                                      new Vector3(0f, s, 0f),
                                      new Vector3(0f, 0f, s));
                _mmi[t].Multimesh.SetInstanceTransform(_cursor[t]++, new Transform3D(basis, pos));
            }
        }

        /// <summary>Clamp the stored mesh type into the valid range (defensive: unknown → type 0).</summary>
        private int TypeOf(EntityWorld world, int id)
        {
            int t = world.MeshType[id];
            return (t >= 0 && t < _typeCount) ? t : 0;
        }
    }
}
