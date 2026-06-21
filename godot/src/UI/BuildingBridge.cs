#nullable enable
using Godot;
using ProjectChimera.Core;
using ProjectChimera.Core.Definitions;

namespace ProjectChimera.UI
{
    /// <summary>
    /// Renders all placed buildings using one MultiMeshInstance3D per building type per faction,
    /// each driven by the faction's building GLB (falls back to a box if the asset is missing).
    ///
    /// Team colour: the GLB source art is flat grey, so a per-faction team-coloured
    /// material_override supplies the player's identity colour (blue P1 / red P2).
    ///
    /// Construction animation: a building grows from ~8% to 100% height while constructing,
    /// anchored at its base so it appears to rise out of the ground.
    /// Progress bar: a thin green bar floats above each under-construction building and grows
    /// left-to-right as construction advances.
    ///
    /// The MultiMesh rebuilds every frame while any building is under construction (for the
    /// grow animation); once all are complete it returns to dirty-flag-only updates.
    /// </summary>
    public partial class BuildingBridge : Node3D
    {
        private BuildingStore _buildings = null!;

        // Two MultiMesh instances per type (P1 / P2).
        private MultiMeshInstance3D[,] _mmi = null!; // [typeIndex, factionIndex 0=P1,1=P2]

        // Per (type, faction) visual metrics derived from the loaded mesh + its mesh_scale.
        private Vector3[,] _typeSize  = null!; // scaled bounding size (X width / Y height used)
        private float[,]   _scale     = null!; // uniform mesh scale
        private float[,]   _groundMinY = null!; // scaled local min-Y (≤0); anchors base to world Y=0

        // One MeshInstance3D progress bar per building slot (pre-allocated, hidden when idle).
        private MeshInstance3D[] _bars = null!;
        private static readonly Color BAR_COLOR = new Color(0.15f, 0.9f, 0.2f); // green

        // One MeshInstance3D rally flag per building slot (hidden until rally point is set).
        private MeshInstance3D[]    _rallyMarkers = null!;
        private StandardMaterial3D  _rallyMatP1   = null!;
        private StandardMaterial3D  _rallyMatP2   = null!;
        private const float RALLY_POLE_HEIGHT = 1.2f;

        // Fallback box sizes (used only when a building GLB is missing).
        private static readonly Vector3[] TYPE_FALLBACK = {
            new Vector3(6f, 4f, 6f), // CommandCenter
            new Vector3(5f, 3f, 5f), // Barracks
            new Vector3(4f, 3f, 5f), // ArcheryRange
            new Vector3(5f, 3f, 7f), // SiegeWorkshop
        };
        private const int TYPE_COUNT = 4;

        private static readonly Color P1_COLOR = new Color(0.2f, 0.5f, 1.0f);
        private static readonly Color P2_COLOR = new Color(1.0f, 0.3f, 0.2f);

        // Bar dimensions (world units) — stretched horizontally over the building footprint.
        private const float BAR_HEIGHT  = 0.25f;
        private const float BAR_DEPTH   = 0.4f;
        private const float BAR_Y_ABOVE = 0.6f; // gap above building top

        private int  _lastSeenCount     = -1;   // last rendered count of ALIVE buildings
        private bool _constructionDirty = true;

        /// <summary>
        /// Initialize building visuals. Building meshes are taken from each slot's faction
        /// definition so alpha-vs-alpha or beta-vs-beta maps render the correct bases.
        /// </summary>
        public void Initialize(BuildingStore buildings,
                               FactionDefinition? p1Def, FactionDefinition? p2Def,
                               Color p1Color, Color p2Color)
        {
            _buildings = buildings;

            _mmi        = new MultiMeshInstance3D[TYPE_COUNT, 2];
            _typeSize   = new Vector3[TYPE_COUNT, 2];
            _scale      = new float[TYPE_COUNT, 2];
            _groundMinY = new float[TYPE_COUNT, 2];

            var defs  = new[] { p1Def, p2Def };
            var mats  = new[] { BuildTeamMaterial(p1Color), BuildTeamMaterial(p2Color) };

            for (int t = 0; t < TYPE_COUNT; t++)
            {
                for (int fi = 0; fi < 2; fi++)
                {
                    string id    = TechTreeChecker.BuildingTypeId((BuildingType)t);
                    var    def   = defs[fi]?.GetBuilding(id);
                    float  scale = def?.MeshScale ?? 1f;
                    Mesh   mesh  = MeshLoader.LoadFromGlb(def?.MeshPath ?? "", TYPE_FALLBACK[t],
                                                         fi == 0 ? p1Color : p2Color);

                    Aabb aabb         = mesh.GetAabb();
                    _scale[t, fi]     = scale;
                    _typeSize[t, fi]  = aabb.Size * scale;
                    _groundMinY[t, fi] = aabb.Position.Y * scale;

                    _mmi[t, fi] = CreateMmi(mesh, mats[fi]);
                    AddChild(_mmi[t, fi]);
                }
            }

            // Pre-allocate one progress bar MeshInstance3D per building slot.
            _bars = new MeshInstance3D[BuildingStore.MAX_BUILDINGS];
            var barMat = new StandardMaterial3D();
            barMat.AlbedoColor     = BAR_COLOR;
            barMat.ShadingMode     = BaseMaterial3D.ShadingModeEnum.Unshaded;
            barMat.EmissionEnabled = true;
            barMat.Emission        = BAR_COLOR * 1.5f;

            for (int i = 0; i < BuildingStore.MAX_BUILDINGS; i++)
            {
                var mesh = new BoxMesh();
                mesh.Size     = new Vector3(1f, BAR_HEIGHT, BAR_DEPTH); // X scaled at runtime
                mesh.Material = barMat;

                var msi = new MeshInstance3D();
                msi.Mesh    = mesh;
                msi.Visible = false;
                AddChild(msi);
                _bars[i] = msi;
            }

            // Pre-allocate one rally-point flag per building slot (thin glowing pole).
            _rallyMarkers = new MeshInstance3D[BuildingStore.MAX_BUILDINGS];
            _rallyMatP1   = BuildRallyMaterial(P1_COLOR);
            _rallyMatP2   = BuildRallyMaterial(P2_COLOR);

            for (int i = 0; i < BuildingStore.MAX_BUILDINGS; i++)
            {
                var poleMesh = new CylinderMesh();
                poleMesh.TopRadius      = 0.12f;
                poleMesh.BottomRadius   = 0.12f;
                poleMesh.Height         = RALLY_POLE_HEIGHT;
                poleMesh.RadialSegments = 6;

                var msi = new MeshInstance3D();
                msi.Mesh    = poleMesh;
                msi.Visible = false;
                AddChild(msi);
                _rallyMarkers[i] = msi;
            }
        }

        private static StandardMaterial3D BuildTeamMaterial(Color color)
        {
            var mat = new StandardMaterial3D();
            mat.AlbedoColor = color;
            mat.Roughness   = 0.7f;
            mat.Metallic    = 0.0f;
            return mat;
        }

        private static StandardMaterial3D BuildRallyMaterial(Color color)
        {
            var mat = new StandardMaterial3D();
            mat.AlbedoColor     = color;
            mat.ShadingMode     = BaseMaterial3D.ShadingModeEnum.Unshaded;
            mat.EmissionEnabled = true;
            mat.Emission        = color * 2f;
            return mat;
        }

        public override void _Process(double delta)
        {
            if (_buildings == null) return;

            // Gate on the ALIVE count, not BuildingStore.Count (a monotonic high-water
            // mark that never decrements) — otherwise a destroyed building would keep
            // rendering as a ghost until the next placement happened to change Count.
            int  aliveCount = CountAlive();
            bool countChanged = aliveCount != _lastSeenCount;
            bool hasConstruction = HasActiveConstruction();

            if (countChanged || hasConstruction || _constructionDirty)
            {
                _lastSeenCount     = aliveCount;
                _constructionDirty = hasConstruction; // keep dirty until all done
                Rebuild();
            }

            UpdateProgressBars();
            UpdateRallyMarkers();
        }

        // ── Rebuild building MultiMeshes ──────────────────────────────────────

        private void Rebuild()
        {
            // Count per (type, faction) bucket.
            int[,] counts = new int[TYPE_COUNT, 2];
            for (int i = 0; i < _buildings.Count; i++)
            {
                if (!_buildings.Alive[i]) continue;
                int t  = (int)_buildings.Type[i];
                int fi = FactionIndex(_buildings.FactionOf[i]);
                if (fi < 0 || t < 0 || t >= TYPE_COUNT) continue;
                counts[t, fi]++;
            }

            // Resize multimeshes.
            for (int t = 0; t < TYPE_COUNT; t++)
            {
                for (int fi = 0; fi < 2; fi++)
                {
                    _mmi[t, fi].Multimesh.InstanceCount = counts[t, fi];
                    counts[t, fi] = 0; // reuse as write cursor
                }
            }

            // Fill transforms — grow Y from the base during construction.
            for (int i = 0; i < _buildings.Count; i++)
            {
                if (!_buildings.Alive[i]) continue;
                int t  = (int)_buildings.Type[i];
                int fi = FactionIndex(_buildings.FactionOf[i]);
                if (fi < 0 || t < 0 || t >= TYPE_COUNT) continue;

                float wx = _buildings.Position[i].X.ToFloat();
                float wz = _buildings.Position[i].Z.ToFloat();

                float scaleY = ConstructionScaleY(i);
                float scale  = _scale[t, fi];

                // Anchor the base to world Y=0 regardless of mesh pivot, even mid-grow.
                float posY = -_groundMinY[t, fi] * scaleY;

                var basis = new Basis(new Vector3(scale, 0f, 0f),
                                      new Vector3(0f, scale * scaleY, 0f),
                                      new Vector3(0f, 0f, scale));
                var xform = new Transform3D(basis, new Vector3(wx, posY, wz));

                int slot = counts[t, fi]++;
                _mmi[t, fi].Multimesh.SetInstanceTransform(slot, xform);
            }
        }

        /// <summary>
        /// Returns the Y scale for a building's visual mesh.
        /// Ranges from 0.08 (just placed) to 1.0 (construction complete).
        /// </summary>
        private float ConstructionScaleY(int id)
        {
            if (!_buildings.IsUnderConstruction(id)) return 1f;

            float duration = _buildings.ConstructionDuration[id].ToFloat();
            if (duration <= 0f) return 1f;

            float remaining = _buildings.ConstructionTimer[id].ToFloat();
            float progress  = 1f - remaining / duration;
            return Mathf.Lerp(0.08f, 1.0f, progress);
        }

        // ── Progress bars ─────────────────────────────────────────────────────

        private void UpdateProgressBars()
        {
            for (int i = 0; i < _buildings.Count; i++)
            {
                if (!_buildings.Alive[i] || !_buildings.IsUnderConstruction(i))
                {
                    if (_bars[i].Visible) _bars[i].Visible = false;
                    continue;
                }

                int t  = (int)_buildings.Type[i];
                int fi = FactionIndex(_buildings.FactionOf[i]);
                if (fi < 0 || t < 0 || t >= TYPE_COUNT) continue;

                float duration = _buildings.ConstructionDuration[i].ToFloat();
                float remaining = _buildings.ConstructionTimer[i].ToFloat();
                float progress  = duration > 0f ? 1f - remaining / duration : 1f;
                progress = Mathf.Clamp(progress, 0f, 1f);

                float maxBarWidth = _typeSize[t, fi].X;
                float barWidth    = maxBarWidth * progress;
                if (barWidth < 0.01f) barWidth = 0.01f;

                float wx = _buildings.Position[i].X.ToFloat();
                float wz = _buildings.Position[i].Z.ToFloat();
                float buildingTop = _typeSize[t, fi].Y * ConstructionScaleY(i);

                // Anchor bar at the left edge: centre offset = -(maxWidth - barWidth) / 2
                float xOffset = -(maxBarWidth - barWidth) * 0.5f;

                _bars[i].Scale   = new Vector3(barWidth, 1f, 1f);
                _bars[i].Position = new Vector3(wx + xOffset, buildingTop + BAR_Y_ABOVE, wz);
                _bars[i].Visible  = true;
            }
        }

        // ── Rally markers ─────────────────────────────────────────────────────

        /// <summary>
        /// Show a glowing faction-colored pole at the rally point for each production
        /// building that has one set. Hides the marker when no rally point is active.
        /// </summary>
        private void UpdateRallyMarkers()
        {
            for (int i = 0; i < _buildings.Count; i++)
            {
                if (!_buildings.Alive[i] || !_buildings.HasRallyPoint[i]
                    || _buildings.Type[i] == BuildingType.CommandCenter)
                {
                    if (_rallyMarkers[i].Visible) _rallyMarkers[i].Visible = false;
                    continue;
                }

                var rp = _buildings.RallyPoint[i];
                _rallyMarkers[i].Position = new Vector3(
                    rp.X.ToFloat(), RALLY_POLE_HEIGHT * 0.5f, rp.Z.ToFloat());

                // Assign faction material only when it changes (first time or faction switch)
                var expectedMat = _buildings.FactionOf[i] == Faction.Player1
                    ? _rallyMatP1 : _rallyMatP2;
                if (_rallyMarkers[i].GetActiveMaterial(0) != expectedMat)
                    ((CylinderMesh)_rallyMarkers[i].Mesh).Material = expectedMat;

                _rallyMarkers[i].Visible = true;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private bool HasActiveConstruction()
        {
            for (int i = 0; i < _buildings.Count; i++)
            {
                if (_buildings.Alive[i] && _buildings.IsUnderConstruction(i))
                    return true;
            }
            return false;
        }

        private int CountAlive()
        {
            int n = 0;
            for (int i = 0; i < _buildings.Count; i++)
                if (_buildings.Alive[i]) n++;
            return n;
        }

        private static MultiMeshInstance3D CreateMmi(Mesh mesh, StandardMaterial3D teamMat)
        {
            var mm = new MultiMesh();
            mm.Mesh            = mesh;
            mm.TransformFormat = MultiMesh.TransformFormatEnum.Transform3D;
            mm.InstanceCount   = 0;

            var mmi = new MultiMeshInstance3D();
            mmi.Multimesh        = mm;
            mmi.MaterialOverride = teamMat;
            return mmi;
        }

        private static int FactionIndex(Faction f) => f switch
        {
            Faction.Player1 => 0,
            Faction.Player2 => 1,
            _               => -1,
        };
    }
}
