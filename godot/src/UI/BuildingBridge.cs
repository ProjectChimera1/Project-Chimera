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

        // Two MultiMesh instances per RENDER BUCKET (P1 / P2). Story 6.8: buckets are keyed by the authored
        // BuildingDefinition.Id (see _bucketOf), NOT the closed BuildingType enum, so a BuildingType.Custom building
        // renders through its own bucket instead of being dropped at enum index 5.
        private MultiMeshInstance3D[,] _mmi = null!; // [bucketIndex, factionIndex 0=P1,1=P2]

        // Per (bucket, faction) visual metrics derived from the loaded mesh + its mesh_scale.
        private Vector3[,] _typeSize  = null!; // scaled bounding size (X width / Y height used)
        private float[,]   _scale     = null!; // uniform mesh scale
        private float[,]   _groundMinY = null!; // scaled local min-Y (≤0); anchors base to world Y=0

        // Story 6.8 — DefinitionId → bucket index, discovered from the loaded faction defs at Initialize. Rebuild /
        // the progress-bar / rally-marker passes route each live building by _buildings.DefinitionId[i] through this
        // map; an id with no bucket (unknown building, defs unloaded) is skipped, never an out-of-range throw.
        private System.Collections.Generic.Dictionary<string, int> _bucketOf = null!;
        private int _bucketCount;

        // DW-171 — a PERMANENT shared fallback render bucket (appended last, index below). A placed building whose
        // DefinitionId had no bucket at Initialize (an id authored/loaded after discovery, or a def-less placement)
        // renders here as a CUSTOM_FALLBACK grey box instead of silently vanishing. Its index is stored so TryBucket
        // can route an unknown id to it. NOTE: this only guarantees an ALREADY-RENDERED faction (P1/P2) draws — a
        // Player3+ building is still skipped by FactionIndex (a separate, larger limitation, out of scope here).
        private int _fallbackBucket;
        // One-time-per-unseen-id diagnostic guard, so an unknown id logs ONCE, not every frame (presentation-only).
        private readonly System.Collections.Generic.HashSet<string> _unknownIdsSeen =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);

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
            new Vector3(5f, 3f, 7f), // Aviary (Story 2.8)
        };
        private const int TYPE_COUNT = 5; // Story 2.8: CommandCenter/Barracks/ArcheryRange/SiegeWorkshop/Aviary
        // Story 6.8 — fallback box for a Custom/authored building whose GLB is missing and which has no TYPE_FALLBACK
        // enum slot. Matches BuildingNavFootprint.CUSTOM_FOOTPRINT (DW-169: the nav tables moved there from
        // NavObstacleManager) so the visual and the nav obstacle agree in the no-GLB case.
        private static readonly Vector3 CUSTOM_FALLBACK = new Vector3(5f, 3f, 5f);

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
                               Color p1Color, Color p2Color,
                               AssetRegistry? registry = null)
        {
            _buildings = buildings;

            var defs   = new[] { p1Def, p2Def };
            var colors = new[] { p1Color, p2Color };

            // Story 6.8: discover the render buckets by authored DefinitionId. Seed the 5 built-in enum ids first (in
            // their stable enum order, so legacy scenarios render exactly as before), then append any extra authored
            // building ids from either faction (a Custom building's id lands here). Deterministic, de-duplicated.
            _bucketOf = new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.Ordinal);
            var ids = new System.Collections.Generic.List<string>();
            for (int t = 0; t < TYPE_COUNT; t++)
                AddBucket(TechTreeChecker.BuildingTypeId((BuildingType)t), ids);
            foreach (var d in defs)
                if (d != null)
                    foreach (var b in d.Buildings)
                        AddBucket(b.Id, ids);
            // DW-171: append ONE permanent shared fallback bucket after the discovered buckets. Its index is the old
            // count; the total bucket count (and every parallel array) grows by one to include it.
            _fallbackBucket = ids.Count;
            _bucketCount    = ids.Count + 1;

            _mmi        = new MultiMeshInstance3D[_bucketCount, 2];
            _typeSize   = new Vector3[_bucketCount, 2];
            _scale      = new float[_bucketCount, 2];
            _groundMinY = new float[_bucketCount, 2];

            for (int t = 0; t < ids.Count; t++)
            {
                string id = ids[t];
                // Fallback box: a built-in id maps to its TYPE_FALLBACK slot (byte-identical); a custom id uses the
                // generic CUSTOM_FALLBACK. Only used when the building's GLB is missing.
                var     enumT    = TechTreeChecker.BuildingTypeFromId(id);
                Vector3 fallback = enumT is BuildingType bt && (int)bt >= 0 && (int)bt < TYPE_FALLBACK.Length
                    ? TYPE_FALLBACK[(int)bt] : CUSTOM_FALLBACK;

                for (int fi = 0; fi < 2; fi++)
                {
                    var    def   = defs[fi]?.GetBuilding(id);
                    float  scale = def?.MeshScale ?? 1f;
                    Mesh   mesh  = MeshLoader.LoadFromGlb(def?.MeshPath ?? "", fallback,
                                                         fi == 0 ? p1Color : p2Color, registry);

                    Aabb aabb         = mesh.GetAabb();
                    _scale[t, fi]     = scale;
                    _typeSize[t, fi]  = aabb.Size * scale;
                    _groundMinY[t, fi] = aabb.Position.Y * scale;

                    // Per-BUCKET material: each building type carries its own albedo art, so one shared
                    // faction material cannot supply the right texture. Untextured art returns the same
                    // flat team material this bridge always used (see TeamTintMaterial).
                    _mmi[t, fi] = CreateMmi(mesh, TeamTintMaterial.Build(mesh, colors[fi],
                                                                         BuildingRoughness, out _));
                    AddChild(_mmi[t, fi]);
                }
            }

            // DW-171: build the permanent shared fallback bucket (a CUSTOM_FALLBACK grey box, scale 1) for each
            // faction column, so an unknown-id building routed here by TryBucket actually has a MultiMesh to draw into.
            for (int fi = 0; fi < 2; fi++)
            {
                Mesh mesh = MeshLoader.LoadFromGlb("", CUSTOM_FALLBACK,
                                                   fi == 0 ? p1Color : p2Color, registry);
                Aabb aabb = mesh.GetAabb();
                _scale[_fallbackBucket, fi]      = 1f;
                _typeSize[_fallbackBucket, fi]   = aabb.Size;      // scale 1 → no multiply
                _groundMinY[_fallbackBucket, fi] = aabb.Position.Y; // scale 1
                _mmi[_fallbackBucket, fi] = CreateMmi(mesh, TeamTintMaterial.Build(mesh, colors[fi],
                                                                                  BuildingRoughness, out _));
                AddChild(_mmi[_fallbackBucket, fi]);
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

        /// <summary>Surface roughness the building team material has always shipped with. The material
        /// itself is now built by <see cref="TeamTintMaterial"/>, which reproduces exactly this flat
        /// material for untextured art and a texture-preserving shader once art carries albedo.</summary>
        private const float BuildingRoughness = 0.7f;

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
            // Count per (bucket, faction). Story 6.8: bucket resolved by DefinitionId, not the enum value.
            int[,] counts = new int[_bucketCount, 2];
            for (int i = 0; i < _buildings.Count; i++)
            {
                if (!_buildings.Alive[i]) continue;
                int fi = FactionIndex(_buildings.FactionOf[i]);
                if (fi < 0 || !TryBucket(i, out int t)) continue;
                counts[t, fi]++;
            }

            // Resize multimeshes.
            for (int t = 0; t < _bucketCount; t++)
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
                int fi = FactionIndex(_buildings.FactionOf[i]);
                if (fi < 0 || !TryBucket(i, out int t)) continue;

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

                int fi = FactionIndex(_buildings.FactionOf[i]);
                if (fi < 0 || !TryBucket(i, out int t)) continue;

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
                // DW-917: the CommandCenter used to be excluded here because it could not produce, so a rally point
                // on it was meaningless decoration. It trains workers now, and rallying the hall at a specific mine
                // (or a new expansion) is standard macro — the sim already honors it (SetRallyCommand has no type
                // gate, and SpawnTrainedUnit walks a trained worker to the rally under DW-634), so the MARKER has to
                // render or the player is steering a rally point they cannot see.
                if (!_buildings.Alive[i] || !_buildings.HasRallyPoint[i])
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

        /// <summary>Story 6.8 — register a DefinitionId → bucket index (skips empty / already-registered ids). The
        /// insertion order into <paramref name="ids"/> IS the bucket index, keeping the map and the parallel arrays
        /// aligned.</summary>
        private void AddBucket(string id, System.Collections.Generic.List<string> ids)
        {
            if (string.IsNullOrEmpty(id) || _bucketOf.ContainsKey(id)) return;
            _bucketOf[id] = ids.Count;
            ids.Add(id);
        }

        /// <summary>Story 6.8 / DW-171 — resolve building slot <paramref name="i"/>'s render bucket from its
        /// DefinitionId. A known id maps to its own bucket; an UNKNOWN id (a building authored/placed after Initialize
        /// discovered the buckets, or a def-less placement) routes to the permanent shared fallback bucket and emits a
        /// one-time diagnostic — never a silent skip, never a throw. Always returns true so the caller renders the
        /// building. (A Player3+ faction is still excluded earlier by <see cref="FactionIndex"/> — out of scope.)</summary>
        private bool TryBucket(int i, out int bucket)
        {
            string id = _buildings.DefinitionId[i] ?? "";
            if (_bucketOf.TryGetValue(id, out bucket)) return true;
            bucket = _fallbackBucket;
            if (_unknownIdsSeen.Add(id))
                GD.PrintErr($"[BuildingBridge] building DefinitionId '{id}' had no render bucket at Initialize; " +
                            "rendering it through the shared fallback bucket (grey box). " +
                            "(If this is a Player3+ building it is still skipped by FactionIndex — out of scope.)");
            return true;
        }

        private static MultiMeshInstance3D CreateMmi(Mesh mesh, Material teamMat)
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
