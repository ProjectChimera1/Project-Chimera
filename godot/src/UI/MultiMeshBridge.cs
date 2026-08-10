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
    /// Team colour: routed through <see cref="TeamTintMaterial"/>, which builds ONE material
    /// per unit type. While the source art is flat grey with no albedo texture that is the
    /// same flat team-coloured material this bridge has always used; once an asset ships with
    /// baked texture art the same call returns a shader that tints WITHOUT erasing it (a plain
    /// <c>material_override</c> replaces a mesh's own materials outright). Unit silhouette
    /// distinguishes archetypes; colour distinguishes teams.
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

        /// <summary>DW-920 — the viewer's fog. Null ⇒ no occlusion (byte-identical to pre-DW-920: every unit of this
        /// bridge's faction always draws), which is what the editor / non-fogged callers want.</summary>
        private ProjectChimera.Core.FogOfWarSystem? _fog;

        /// <summary>DW-920 — late-bound "reveal everything" getter, mirroring <c>FogOfWarBridge.RevealAll</c> the way
        /// <c>MinimapBridge</c> does (DW-406). Late-bound rather than copied so a spectator / eliminated-player
        /// reveal flips the 3D units, the minimap and the fog plane together instead of drifting apart.</summary>
        private System.Func<bool>? _revealAll;

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
        /// Story 9.9: an optional <paramref name="registry"/> resolves a unit's non-<c>res://</c> logical
        /// <c>MeshPath</c> to a downloaded custom mesh; null (the default) = today's <c>res://</c>-only behavior.
        /// </summary>
        public void Initialize(SimulationHost host, FactionDefinition factionDef,
                               Faction faction, Color teamColor, AssetRegistry? registry = null)
        {
            _renderWorld = host.World;
            _alphaSource = () => host.InterpolationAlpha;
            _faction = faction;
            _fog = host.Fog; // DW-920 — host truth, so SetViewer/SharedTeamVision changes are picked up live

            var units = factionDef.Units;
            _typeCount = Mathf.Max(1, units.Count);
            AllocateArrays();

            var fallbackSize = new Vector3(0.6f, 1.2f, 0.6f);
            for (int t = 0; t < _typeCount; t++)
            {
                var   def   = t < units.Count ? units[t] : null;
                float scale = def?.MeshScale ?? 1f;
                Mesh  mesh  = MeshLoader.LoadFromGlb(def?.MeshPath ?? "", fallbackSize, teamColor, registry);
                // Per-TYPE material, not one shared across the faction: each unit type carries its own
                // albedo art, so a single shared material cannot supply the right texture. For untextured
                // art TeamTintMaterial returns the identical flat material this loop used to share.
                var mat = TeamTintMaterial.Build(mesh, teamColor, UnitRoughness, out _);
                _mmi[t] = BuildSubMesh(t, mesh, scale, mat, GroundOffsetFor(mesh, scale));
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

        /// <summary>Surface roughness the unit team material has always shipped with.</summary>
        private const float UnitRoughness = 0.6f;

        private MultiMeshInstance3D BuildSubMesh(int type, Mesh mesh, float scale,
                                                 Material? teamMat, float groundOffset)
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

            // DW-920 — fog occlusion. This bridge draws ONE faction; when that faction is not the viewer's own (nor
            // an ally under shared-team vision), each of its units is drawn only while its cell is currently VISIBLE.
            // Without this the fog was terrain paint only: every enemy unit rendered at full brightness through
            // unexplored fog, so both players could watch the other's entire build order and army movements. A
            // spectator / post-elimination observer sets RevealAll and sees everything, exactly as the minimap does
            // (the DW-406 late-bound getter pattern, so every site that flips reveal stays in sync).
            bool fogThisFaction = _fog != null
                               && !_fog.RevealsFaction(_faction)
                               && !(_revealAll?.Invoke() ?? false);

            // Pass 1 — count alive faction entities per mesh type.
            System.Array.Clear(_counts, 0, _typeCount);
            for (int i = 0; i < simCount; i++)
            {
                if (!world.IsAlive(i)) continue;
                if (world.FactionOf[i] != _faction) continue;
                if (fogThisFaction && !IsUnderVision(world, i)) continue;
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
                // Must use the SAME test as pass 1, or the cursor overruns the InstanceCount pass 1 sized.
                if (fogThisFaction && !IsUnderVision(world, i)) continue;

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

        /// <summary>
        /// DW-920 — inject the spectator/observer reveal-all getter (mirrors <c>FogOfWarBridge.RevealAll</c>). Wired
        /// by the rendering bootstrap after the fog bridge exists; unset ⇒ never reveals, so fog applies normally.
        /// </summary>
        public void SetRevealAllSource(System.Func<bool> revealAll) => _revealAll = revealAll;

        /// <summary>
        /// DW-920 — is entity <paramref name="id"/> standing in a cell the viewer can currently SEE? Units use
        /// VISIBLE (not EXPLORED): an enemy unit is only drawn while something of the viewer's actually watches it,
        /// so it vanishes when it walks back into the dark rather than leaving a ghost. Buildings take the opposite
        /// rule in <c>BuildingBridge</c> (remembered once explored), which is the standard RTS split.
        /// Sampled at the unit's CURRENT position, matching the fog stamp's own sampling.
        /// </summary>
        private bool IsUnderVision(EntityWorld world, int id) =>
            _fog!.IsVisible(world.Position[id].X.ToFloat(), world.Position[id].Z.ToFloat());

        /// <summary>Clamp the stored mesh type into the valid range (defensive: unknown → type 0).</summary>
        private int TypeOf(EntityWorld world, int id)
        {
            int t = world.MeshType[id];
            return (t >= 0 && t < _typeCount) ? t : 0;
        }
    }
}
