#nullable enable
using Godot;
using ProjectChimera.Core.Definitions;
using ProjectChimera.UI;

namespace ProjectChimera.Core.Bootstrap
{
    /// <summary>
    /// Story 1.8c "ScenarioLoad" phase (runtime position 12). The presentation orchestration that resolves the
    /// scenario (AI-generated → file → hardcoded fallback), runs the faction-resolution pre-pass and the Story-1.7
    /// validation gate, and hands the validated model to the Godot-free <see cref="ScenarioApplier"/> (the sole
    /// writer of sim truth), then builds the start-position markers. Publishes Scenario / FallbackMirror /
    /// ScenarioApplied / StartPosBridge on the context (read by the _Ready scenario-hash tail, MoveStartPosition,
    /// CheckWinCondition, and the win/trigger/map-gen UI phases). Behavior-identical to the former
    /// MainScene.LoadAndApplyScenario family; no sim-write path changes.
    /// </summary>
    public sealed class ScenarioLoadPhase : ISetupPhase
    {
        private readonly SceneContext _ctx;
        public ScenarioLoadPhase(SceneContext ctx) => _ctx = ctx;

        public string Name => "ScenarioLoad";

        /// <summary>The single pre-tick validation gate (Godot-free). Shadow-mode on master.</summary>
        private readonly ScenarioValidator _validator = new();
        /// <summary>Fail-closed toggle (CHIMERA_VALIDATE_FAILCLOSED, default off). Flip only on a release branch.</summary>
        private static readonly bool _failClosed = ScenarioGate.IsFailClosed();

        /// <summary>Story 6.5: the ElevationGrid built by the most recent <see cref="BuildAndInjectElevationGrid"/>
        /// (or null on a flat/legacy/failed build), so <see cref="BuildAndInjectPathabilityGrid"/> can derive steep
        /// slope cells from it without re-sampling Terrain3D.</summary>
        private ElevationGrid? _lastElevationGrid;

        /// <summary>
        /// Pending AI-generated scenario: written by the MapGenerator before the scene reload, consumed here.
        /// Static so it survives the Godot scene reload cycle (the new scene's ScenarioLoadPhase reads it).
        /// </summary>
        internal static ScenarioData? PendingGeneratedScenario;

        public void Run() => LoadAndApplyScenario();

        /// <summary>
        /// Resolve <see cref="MainScene.ScenarioPath"/>, load the JSON, and apply it. Falls back to a hardcoded
        /// default if the file is missing or fails to parse.
        /// </summary>
        private void LoadAndApplyScenario()
        {
            // Check for an AI-generated scenario passed across the scene reload boundary.
            if (PendingGeneratedScenario != null)
            {
                var generated = PendingGeneratedScenario;
                PendingGeneratedScenario = null;
                _ctx.Scenario = generated;
                // Story 6.3: restore sculpted terrain and build the sim elevation grid BEFORE apply, so every
                // scenario-placed unit samples the FINALIZED heightmap at spawn (see BuildAndInjectElevationGrid).
                RestoreTerrainFromScenario();
                BuildAndInjectElevationGrid();
                BuildAndInjectPathabilityGrid(); // Story 6.5 — after the elevation grid (slope-derive reads it), before apply
                ApplyScenarioThroughApplier(generated, "ApplyScenario");
                GD.Print($"[MainScene] Loaded AI-generated scenario: \"{generated.DisplayName}\"");
                SetupStartPositionBridge();
                return;
            }

            string abs = ProjectSettings.GlobalizePath(_ctx.Scene.ScenarioPath);
            var scenario = ScenarioSerializer.LoadFromFile(abs);

            if (scenario == null)
            {
                GD.PrintErr($"[MainScene] Scenario not found or failed to parse: {_ctx.Scene.ScenarioPath} — using defaults.");
                // Fallback map is flat (no TerrainRef): no restore. BuildAndInjectElevationGrid is intentionally NOT
                // called here, so explicitly clear the applier's grid — a REUSED applier must not carry a prior
                // sculpted load's grid into this flat fallback. Every unit then spawns at Fixed.Zero elevation
                // (byte-identical to pre-feature). (review pass 1, F6.)
                _ctx.Applier.SetElevationGrid(null);
                _lastElevationGrid = null;
                // Story 6.5: clear pathability too — a REUSED applier/flow-field must not carry a prior sculpted
                // load's blocking into this flat fallback (every unit then moves freely, byte-identical to pre-feature).
                _ctx.Applier.SetPathabilityGrid(null);
                _ctx.FlowFieldSys?.SetStaticBlocked(null);
                _ctx.Pathability = null;
                ApplyFallbackThroughApplier();
            }
            else
            {
                _ctx.Scenario = scenario;
                // Story 6.3: restore terrain + inject the elevation grid BEFORE apply (spawn-time elevation sampling).
                RestoreTerrainFromScenario();
                BuildAndInjectElevationGrid();
                BuildAndInjectPathabilityGrid(); // Story 6.5 — after the elevation grid, before apply
                ApplyScenarioThroughApplier(scenario, "ApplyScenario");
                GD.Print($"[MainScene] Loaded scenario: \"{scenario.DisplayName}\" ({scenario.Id})");
            }

            SetupStartPositionBridge();
        }

        /// <summary>
        /// Story 6.2 — restore saved Terrain3D region data over the flat region TerrainPhase (position 5) imported at
        /// boot, BEFORE this ScenarioLoad phase (position 12) parsed the scenario. When the loaded scenario carries a
        /// non-empty TerrainRef pointing at a folder that actually holds terrain3d_*.res files, assign it as the
        /// terrain's data_directory (the addon reload idiom: clear to "" first, then set the path) so the saved
        /// height + control maps load, recompute the height range, then MarkDirty the NavMesh so the bake reflects
        /// the restored height.
        ///
        /// Empty TerrainRef (new/legacy map, PlaneMesh fallback) ⇒ do nothing: the flat region stays, byte-identical
        /// to pre-feature behavior, with NO new log line. A set-but-missing/empty/corrupt ref ⇒ keep flat + a single
        /// PrintErr diagnostic, never a crash.
        /// </summary>
        private void RestoreTerrainFromScenario()
        {
            string? terrainRef = _ctx.Scenario?.TerrainRef;
            if (string.IsNullOrEmpty(terrainRef)) return;   // flat fallback — silent, byte-identical to today
            if (_ctx.Terrain == null) return;               // PlaneMesh fallback — nothing to restore into

            try
            {
                string absDir = ProjectSettings.GlobalizePath(terrainRef);
                if (!System.IO.Directory.Exists(absDir))
                {
                    GD.PrintErr($"[ScenarioLoad] TerrainRef folder missing — keeping flat terrain: {terrainRef}");
                    return;
                }

                // Honest logging: only claim a restore when the folder actually holds region .res files. An
                // existing-but-empty folder keeps the flat region and says so accurately. The region-file test goes
                // through the Godot-free ContentPackager.IsTerrainRegionFile predicate (Tier-1 unit-tested, review
                // pass 2 / VG2) rather than an inline glob, so the negative-coordinate HYPHEN encoding
                // (Terrain3DUtil.location_to_filename → e.g. (-1,-1) is "terrain3d-01-01.res", not "terrain3d_01_01")
                // stays a pinned, regression-guarded rule. An underscore-anchored glob would miss every map whose
                // regions sit at a negative location — including the default flat region at (-1,-1). [live-verified 6.2]
                bool hasRegionFile = false;
                foreach (var f in System.IO.Directory.GetFiles(absDir, "*.res"))
                    if (Definitions.ContentPackager.IsTerrainRegionFile(System.IO.Path.GetFileName(f))) { hasRegionFile = true; break; }
                if (!hasRegionFile)
                {
                    GD.Print($"[ScenarioLoad] TerrainRef folder has no region files — keeping flat terrain: {terrainRef}");
                    return;
                }

                // Reload idiom: clear then set data_directory so a stale in-memory region does not linger.
                _ctx.Terrain.Set("data_directory", "");
                _ctx.Terrain.Set("data_directory", terrainRef);

                // Recompute height ranges + rebake nav from the restored geometry.
                var data = _ctx.Terrain.Get("data").AsGodotObject();
                data?.Call("calc_height_range", true);
                _ctx.NavObstacles?.MarkDirty();

                GD.Print($"[ScenarioLoad] Restored sculpted terrain from {terrainRef}");
            }
            catch (System.Exception ex)
            {
                GD.PrintErr($"[ScenarioLoad] Terrain restore failed ({ex.Message}) — keeping flat terrain.");
            }
        }

        /// <summary>
        /// Story 6.3 — the Godot→sim elevation seam. Reads the FINALIZED Terrain3D heightmap (after
        /// <see cref="RestoreTerrainFromScenario"/>), builds a Godot-free <see cref="ElevationGrid"/> of
        /// <see cref="Fixed"/> heights (one <see cref="Fixed.FromFloat"/> per cell — the sanctioned load-time float→Fixed
        /// boundary), and hands it to the <see cref="ScenarioApplier"/> BEFORE apply so every spawn samples it. Runs
        /// entirely inside the "ScenarioLoad" phase (position 12), which is AFTER "Terrain" (position 5) created the
        /// node and AFTER the restore above — the ordering the sim contract needs.
        ///
        /// <para>The sim NEVER reads Terrain3D: sampling is a Godot-side load-time step, and the sim's
        /// <see cref="ElevationGrid.Sample"/> is a clamped integer cell lookup over the baked <see cref="Fixed"/> array.
        /// NaN/hole cells and any failure degrade to flat (Fixed.Zero) — never a crash. A null/PlaneMesh terrain leaves
        /// the grid unset (applier default ⇒ zero elevation, byte-identical to pre-feature).</para>
        /// </summary>
        private void BuildAndInjectElevationGrid()
        {
            // PlaneMesh fallback — no heightmap to sample. Explicitly clear the grid so a REUSED applier can't carry a
            // prior sculpted load's grid into this flat load (review pass 1, F6); units then spawn at Fixed.Zero.
            if (_ctx.Terrain == null) { _ctx.Applier.SetElevationGrid(null); _lastElevationGrid = null; return; }

            try
            {
                var data = _ctx.Terrain.Get("data").AsGodotObject();
                if (data == null) { _ctx.Applier.SetElevationGrid(null); _lastElevationGrid = null; return; }

                // Sample a grid over the default ±128 world XZ extent at 1 world-unit/cell (256×256). The grid stores
                // its own extent, so the sim is general over resolution; this Godot-side resolution just matches the
                // authored region. get_height does the (allowed) load-time interpolation; the SIM never interpolates.
                const int N = 256;
                const float half = 128f;
                const float cell = (half * 2f) / N; // = 1.0 world unit/cell
                var heights = new Fixed[N * N];
                bool anyNonZero = false;

                for (int row = 0; row < N; row++)
                {
                    float wz = -half + (row + 0.5f) * cell; // cell-centre world Z
                    for (int col = 0; col < N; col++)
                    {
                        float wx = -half + (col + 0.5f) * cell; // cell-centre world X
                        float h = data.Call("get_height", new Vector3(wx, 0f, wz)).AsSingle();
                        if (!float.IsFinite(h)) h = 0f; // hole / NaN → flat, never a bad Fixed
                        if (h != 0f) anyNonZero = true;
                        heights[row * N + col] = Fixed.FromFloat(h);
                    }
                }

                var grid = new ElevationGrid(heights, N, N,
                    Fixed.FromFloat(-half), Fixed.FromFloat(-half), Fixed.FromFloat(cell));
                _ctx.Applier.SetElevationGrid(grid);
                _lastElevationGrid = grid; // Story 6.5: reused by slope-auto-block derivation

                if (anyNonZero)
                    GD.Print("[ScenarioLoad] Built sim elevation grid from sculpted terrain (256×256).");
            }
            catch (System.Exception ex)
            {
                // Never block the load, and never carry a stale grid past a failed build (review pass 1, F6): clear it
                // so units spawn at flat elevation (Fixed.Zero) rather than a prior load's heightmap.
                _ctx.Applier.SetElevationGrid(null);
                _lastElevationGrid = null;
                GD.PrintErr($"[ScenarioLoad] Elevation grid build failed ({ex.Message}) — units spawn at flat elevation.");
            }
        }

        /// <summary>
        /// Story 6.5 — the Godot→sim pathability seam. Runs AFTER <see cref="BuildAndInjectElevationGrid"/> in the
        /// "ScenarioLoad" phase, BEFORE apply. Decodes the authored painted bitset
        /// (<see cref="ScenarioData.PathabilityBlocked"/>) and, when <see cref="ScenarioData.SlopeAutoBlock"/> is on,
        /// derives steep cells deterministically from the just-built <see cref="ElevationGrid"/> (neighbor rise/run ≥
        /// <see cref="ScenarioData.SlopeBlockThreshold"/>) and UNIONS them into a Godot-free
        /// <see cref="ProjectChimera.Navigation.PathabilityGrid"/>. Injects that grid into the 3 sim sinks (the applier
        /// → EntityWorld at apply, and the FlowFieldSystem's static obstacle mask) plus the SceneContext (the editor
        /// overlay tool reads it). Null/empty everywhere ⇒ flat (byte-identical to pre-feature).
        ///
        /// <para>The single float→<see cref="Fixed"/> boundary is the base64 decode + the slope derivation here; the
        /// sim's <c>PathabilityGrid.IsBlocked</c> is a pure integer cell lookup. Any decode/derive failure degrades to
        /// no blocking — never a crash.</para>
        /// </summary>
        private void BuildAndInjectPathabilityGrid()
        {
            try
            {
                ScenarioData? s = _ctx.Scenario;

                // 1-3) Resolve the union grid (painted ∪ slope-derived ∪ prop/water footprint, or null when nothing is
                //    blocked) via the ONE shared, Godot-free ScenarioApplier.BuildPathabilityGrid recipe (DW-157 /
                //    Story 14.8). The Edit→Play F5 re-apply path (MainScene.ResetToAuthoredStart) routes through the
                //    EXACT same method, so boot and re-apply can never diverge on the blocked-cell set. The applier's
                //    elevation grid == _lastElevationGrid at boot (both set in BuildAndInjectElevationGrid above), so
                //    the slope re-derivation reads the identical terrain — this refactor is byte-identical to the
                //    former inline slopeOn/threshold/footprint/Resolve block.
                Navigation.PathabilityGrid? grid =
                    ProjectChimera.Core.Sim.ScenarioApplier.BuildPathabilityGrid(s, _lastElevationGrid);

                // 4) Inject into the sim sinks: the applier threads it into EntityWorld at Apply; the FlowFieldSystem
                //    ORs the static mask into its obstacle map on the next RebuildObstacles (FlowFieldInit phase). The
                //    SceneContext carries it so the PathabilityTool overlay (a later phase) can render the union.
                _ctx.Applier.SetPathabilityGrid(grid);
                _ctx.FlowFieldSys?.SetStaticBlocked(grid?.Blocked);
                _ctx.Pathability = grid;

                if (grid != null)
                    GD.Print($"[ScenarioLoad] Built sim pathability grid (slope-auto-block={s != null && s.SlopeAutoBlock && s.SlopeBlockThreshold > 0f}).");
            }
            catch (System.Exception ex)
            {
                // Never block the load, and never carry a stale grid past a failed build: clear all sinks so the map
                // is fully passable rather than a prior load's blocking.
                _ctx.Applier.SetPathabilityGrid(null);
                _ctx.FlowFieldSys?.SetStaticBlocked(null);
                _ctx.Pathability = null;
                GD.PrintErr($"[ScenarioLoad] Pathability grid build failed ({ex.Message}) — map fully passable.");
            }
        }

        /// <summary>
        /// Story 1.7 shadow-mode gate: run the model through the validator and return its result. On failure, log
        /// a LOCATED rejection (presentation-side — the Godot-free validator never logs). The caller applies
        /// <c>result.Value</c> when <see cref="ScenarioGate.ShouldProceed"/> permits. Never throws.
        /// </summary>
        private ValidationResult ValidateBeforeApply(ScenarioData model, string pathLabel)
        {
            // Story 6.8: pass the resolved per-slot faction defs (ResolveSlotFactionDefs ran just before this) so a
            // pre-placed CUSTOM building's authored id is accepted by the retired enum gate.
            ValidationResult result = _validator.Validate(model, _ctx.SlotFactionDefs);
            if (!result.Ok)
                GD.PrintErr($"[ScenarioValidator] {pathLabel} REJECTED: {result.Error}");
            return result;
        }

        /// <summary>
        /// Story 1.8b (D4) — presentation faction-resolution pre-pass. Resolves each player slot's res:// faction
        /// JSON to an absolute OS path and populates ctx.SlotFactionDefs IN PLACE before the Godot-free applier
        /// runs. The ONLY ProjectSettings.GlobalizePath on the scenario-apply path. Slots without an explicit
        /// faction_json keep their _Ready-seeded defaults.
        /// </summary>
        private void ResolveSlotFactionDefs(ScenarioData scenario)
        {
            foreach (var slot in scenario.PlayerSlots ?? System.Array.Empty<ScenarioPlayerSlot>())
            {
                if (string.IsNullOrEmpty(slot.FactionJson)) continue;
                var faction = FactionRegistry.ToFaction(slot.Slot); // resolved via the one canonical cast site
                string abs = ProjectSettings.GlobalizePath(slot.FactionJson);
                if (System.IO.File.Exists(abs))
                {
                    var def = FactionDefinition.LoadFromFile(abs);
                    // Story 2.4b: back-fill this slot's freshly-loaded faction defs' ability ids → registry indices
                    // BEFORE the applier spawns its units (ApplyUnitDefinition reads UnitDefinition.AbilityIndices,
                    // empty until ResolveAbilities runs). The registry was built + published on the context by
                    // MainScene._Ready, which runs before this phase (runtime position 12). Idempotent + drops unknown ids.
                    foreach (var u in def.Units) u.ResolveAbilities(_ctx.AbilityRegistry);
                    // Story 2.11 (AC2): closed-set tag validation — drop any unit carrying an unknown tag (fail-closed,
                    // located error). Runs on BOTH legs (here + ServerBootstrap) so client/server stay in parity before
                    // any SpawnUnit; a dropped unit → GetUnit null → the applier's def==null skip → no EntityWorld slot.
                    foreach (string err in UnitTagValidator.ValidateAndDropUnits(def))
                        GD.PrintErr($"[UnitTagValidator] {err} (unit dropped)");
                    // Story 5.7 (FR-19/UX-DR80, DW-97 match-load closure): shadow-mode, non-blocking roster-
                    // completeness diagnostic — mirrors the UnitTagValidator GD.PrintErr idiom immediately above.
                    // Runs AFTER tag-drop so it reflects the roster that will actually spawn (a unit dropped for
                    // an unknown tag could be this faction's only Worker/combat unit — checking pre-drop would
                    // silently miss that). Never blocks the load (no new blocking policy invented per DW-97's own
                    // closure note); just surfaces a located error if the roster fails ValidateComplete (e.g.
                    // missing Worker role or a blank mesh_path) so it isn't a silent unplayable match start.
                    FactionValidationResult completeResult = FactionValidator.ValidateComplete(def);
                    if (!completeResult.Ok)
                        foreach ((string _, string message) in completeResult.Errors)
                            GD.PrintErr($"[FactionValidator] slot {slot.Slot} ({abs}): {message}");
                    _ctx.SlotFactionDefs[(int)faction] = def;
                }
            }
        }

        /// <summary>
        /// Story 1.8b — presentation orchestration for a parsed scenario: faction pre-pass, the 1.7 validation
        /// gate, then (when shadow / fail-closed policy permits) hand the validated model to the applier.
        /// </summary>
        private void ApplyScenarioThroughApplier(ScenarioData scenario, string pathLabel)
        {
            ResolveSlotFactionDefs(scenario);                            // the one Godot path-resolution, hoisted
            ValidationResult r = ValidateBeforeApply(scenario, pathLabel);
            if (ScenarioGate.ShouldProceed(r.Ok, _failClosed))          // shadow proceeds even when r.Ok == false
            {
                _ctx.Applier.Apply(r.Value);
                _ctx.ScenarioApplied = true; // reached only when the gate permits applying (Story 1.7 review patch)
            }
        }

        /// <summary>
        /// Story 1.8b — fallback path (scenario JSON missing): build the ScenarioData mirror so it passes the same
        /// validation gate and yields a real canonical-model hash, then apply the hardcoded fallback through the
        /// applier (the always-applied safety net; its gate result is shadow-validation only).
        /// </summary>
        private void ApplyFallbackThroughApplier()
        {
            _ctx.FallbackMirror = BuildFallbackMirror();
            ValidateBeforeApply(_ctx.FallbackMirror, "fallback"); // shadow-validation only (result intentionally not used)
            _ctx.ScenarioApplied = true; // the fallback is the always-applied safety net (Story 1.7 review patch)
            _ctx.Applier.ApplyFallback();
        }

        /// <summary>
        /// Story 1.7: a ScenarioData mirror of the hardcoded ScenarioApplier.ApplyFallback layout, used ONLY to
        /// feed the validation gate and the canonical-model hash. Keep these literal values in sync with the
        /// applier's fallback; unit_id "worker" is the conventional worker id.
        /// </summary>
        private static ScenarioData BuildFallbackMirror() => new ScenarioData
        {
            Id           = "fallback",
            DisplayName  = "Fallback",
            MapBounds    = 120f,
            WinCondition = WinCondition.DestroyAllBuildings,
            PlayerSlots = new[]
            {
                new ScenarioPlayerSlot { Slot = 0, StartOre = 200f, StartCrystal = 100f, BaseX = -45f, BaseZ = 0f },
                new ScenarioPlayerSlot { Slot = 1, StartOre = 200f, StartCrystal = 100f, BaseX =  45f, BaseZ = 0f },
            },
            ResourceNodes = new[]
            {
                new ScenarioResourceNode { X = -20f, Z = -15f, Supply = 600f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X = -20f, Z =  15f, Supply = 600f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X =  20f, Z = -15f, Supply = 600f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X =  20f, Z =  15f, Supply = 600f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X =   0f, Z = -25f, Supply = 400f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X =   0f, Z =  25f, Supply = 400f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X = -35f, Z =   0f, Supply = 300f, Rate = 5f, MaxGatherers = 4 },
                new ScenarioResourceNode { X =  35f, Z =   0f, Supply = 300f, Rate = 5f, MaxGatherers = 4 },
            },
            Buildings = new[]
            {
                new ScenarioBuilding { Type = "CommandCenter", Slot = 0, X = -45f, Z = 0f, PreBuilt = true },
                new ScenarioBuilding { Type = "CommandCenter", Slot = 1, X =  45f, Z = 0f, PreBuilt = true },
            },
            Units = new[]
            {
                new ScenarioUnit { UnitId = "worker", Slot = 0, X = -42f, Z = -3f },
                new ScenarioUnit { UnitId = "worker", Slot = 0, X = -42f, Z =  3f },
                new ScenarioUnit { UnitId = "worker", Slot = 1, X =  42f, Z = -3f },
                new ScenarioUnit { UnitId = "worker", Slot = 1, X =  42f, Z =  3f },
            },
        };

        /// <summary>
        /// Create flag-pole markers for the two player start positions. Reads initial XZ from the live scenario
        /// (or fallback defaults). Publishes ctx.StartPosBridge (used by MoveStartPosition).
        /// </summary>
        private void SetupStartPositionBridge()
        {
            // Story 6.7: 2–4 start positions (capped at the engine ceiling). Size the placed-position array to the
            // scenario's declared player-slot count so 3- and 4-player maps show all their markers.
            int placed = _ctx.Scenario?.PlayerSlots?.Length ?? 2;
            placed = System.Math.Clamp(placed, 2, StartPositionBridge.MAX_SLOTS);
            var positions = new (float x, float z)[placed];

            if (_ctx.Scenario != null)
            {
                foreach (var slot in _ctx.Scenario.PlayerSlots)
                {
                    int idx = System.Math.Clamp(slot.Slot, 0, StartPositionBridge.MAX_SLOTS - 1);
                    if (idx < positions.Length) positions[idx] = (slot.BaseX, slot.BaseZ);
                }
            }
            else
            {
                // Fallback positions matching ScenarioApplier.ApplyFallback
                positions[0] = (-45f, 0f);
                positions[1] = (+45f, 0f);
            }

            var startPosBridge = new StartPositionBridge();
            _ctx.Scene.AddChild(startPosBridge);
            startPosBridge.Initialize(positions);
            _ctx.StartPosBridge = startPosBridge;
        }
    }
}
