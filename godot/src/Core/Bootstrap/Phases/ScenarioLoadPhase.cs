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

        /// <summary>The single pre-tick validation gate (Godot-free). Story 7.7: FAIL-CLOSED, unconditionally —
        /// shadow mode and its env toggle are removed; a rejected model is never applied (the validated fallback
        /// substitutes, extending the missing-file safety net to invalid files).</summary>
        private readonly ScenarioValidator _validator = new();

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
        /// Resolve <see cref="MainScene.ScenarioPath"/>, load the JSON, validate FAIL-CLOSED (Story 7.7), and
        /// apply it. Falls back to the VALIDATED hardcoded mirror if the file is missing, fails to parse, or is
        /// REJECTED by the validator — the established missing-file safety net, extended to invalid files (the
        /// rejected model never touches the sim; the located error is surfaced).
        /// </summary>
        private void LoadAndApplyScenario()
        {
            // Check for an AI-generated scenario passed across the scene reload boundary. Story 7.7: it clears the
            // SAME fail-closed gate as a file scenario BEFORE any sim/terrain side effect — a rejected AI model is
            // substituted by the validated fallback, exactly like an invalid file.
            if (PendingGeneratedScenario != null)
            {
                var generated = PendingGeneratedScenario;
                PendingGeneratedScenario = null;
                FactionDefinition?[] seededDefs = SnapshotSlotFactionDefs(); // pre-resolution defaults (see reject below)
                ResolveSlotFactionDefs(generated);                 // the one Godot path-resolution, hoisted
                ValidationResult rg = ValidateBeforeApply(generated, "ApplyScenario(AI)");
                if (!rg.Ok)
                {
                    // Review follow-up: the pre-pass above resolved the REJECTED model's faction defs into the
                    // shared slot array — re-seed the _Ready defaults (what the missing-file path boots with) so
                    // the fallback never spawns from a rejected scenario's roster; and clear Scenario so no
                    // downstream consumer can observe the rejected model as if applied (missing-file alignment:
                    // Scenario null + FallbackMirror set).
                    RestoreSlotFactionDefs(seededDefs);
                    _ctx.Scenario = null;
                    ApplyFallbackThroughApplier();
                }
                else
                {
                    _ctx.Scenario = generated;
                    // Story 6.3: restore sculpted terrain and build the sim elevation grid BEFORE apply, so every
                    // scenario-placed unit samples the FINALIZED heightmap at spawn (see BuildAndInjectElevationGrid).
                    RestoreTerrainFromScenario();
                    BuildAndInjectElevationGrid();
                    BuildAndInjectPathabilityGrid(); // Story 6.5 — after the elevation grid (slope-derive reads it), before apply
                    _ctx.Applier.Apply(rg.Value);
                    _ctx.ScenarioApplied = true;
                    GD.Print($"[MainScene] Loaded AI-generated scenario: \"{generated.DisplayName}\"");
                }
                SetupStartPositionBridge();
                return;
            }

            string abs = ProjectSettings.GlobalizePath(_ctx.Scene.ScenarioPath);
            var scenario = ScenarioSerializer.LoadFromFile(abs);

            if (scenario == null)
            {
                GD.PrintErr($"[MainScene] Scenario not found or failed to parse: {_ctx.Scene.ScenarioPath} — using defaults.");
                ApplyFallbackThroughApplier();
            }
            else
            {
                // Story 7.7 — validate BEFORE any terrain/grid side effect, so a rejected file leaves no residue
                // and the fallback boots on a clean flat board (the invalid model is never applied).
                FactionDefinition?[] seededDefs = SnapshotSlotFactionDefs(); // pre-resolution defaults (see reject below)
                ResolveSlotFactionDefs(scenario);                  // the one Godot path-resolution, hoisted
                ValidationResult r = ValidateBeforeApply(scenario, "ApplyScenario");
                if (!r.Ok)
                {
                    // Review follow-up (mirrors the AI path above): re-seed the default slot faction defs — the
                    // pre-pass resolved the REJECTED file's defs into the shared array — and clear Scenario so the
                    // reject leaves exactly the missing-file state (Scenario null + FallbackMirror set).
                    RestoreSlotFactionDefs(seededDefs);
                    _ctx.Scenario = null;
                    ApplyFallbackThroughApplier();
                }
                else
                {
                    _ctx.Scenario = scenario;
                    // Story 6.3: restore terrain + inject the elevation grid BEFORE apply (spawn-time elevation sampling).
                    RestoreTerrainFromScenario();
                    BuildAndInjectElevationGrid();
                    BuildAndInjectPathabilityGrid(); // Story 6.5 — after the elevation grid, before apply
                    _ctx.Applier.Apply(r.Value);
                    _ctx.ScenarioApplied = true;
                    GD.Print($"[MainScene] Loaded scenario: \"{scenario.DisplayName}\" ({scenario.Id})");
                }
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
        /// Story 1.7 gate, Story 7.7 fail-closed: run the model through the validator and return its result. On
        /// failure, log the LOCATED rejection and surface it as a toast (presentation-side — the Godot-free
        /// validator never logs); the caller applies <c>result.Value</c> ONLY when <c>result.Ok</c> (no shadow
        /// proceed). Never throws.
        /// </summary>
        private ValidationResult ValidateBeforeApply(ScenarioData model, string pathLabel)
        {
            // Story 6.8: pass the resolved per-slot faction defs (ResolveSlotFactionDefs ran just before this) so a
            // pre-placed CUSTOM building's authored id is accepted by the retired enum gate.
            ValidationResult result = _validator.Validate(model, _ctx.SlotFactionDefs);
            if (!result.Ok)
            {
                GD.PrintErr($"[ScenarioValidator] {pathLabel} REJECTED: {result.Error}");
                // Surface the located error in-scene too (the existing toast convention) — the fallback world that
                // boots instead would otherwise look like a silent wrong-map load. ShowTriggerMessage no-ops
                // internally when the toast label isn't built yet (review follow-up: the former guard here checked
                // ToastLabel while the call went through Scene — one guard, owned by the callee).
                _ctx.Scene.ShowTriggerMessage($"Invalid scenario — loaded the fallback map instead:\n{result.Error}", 8f);
            }
            return result;
        }

        /// <summary>Review follow-up — a shallow snapshot of the shared per-slot faction-def array's CONTENTS,
        /// taken before <see cref="ResolveSlotFactionDefs"/> so a REJECTED scenario's resolved defs can be rolled
        /// back to the _Ready-seeded defaults (what the missing-file fallback boots with).</summary>
        private FactionDefinition?[] SnapshotSlotFactionDefs() =>
            (FactionDefinition?[])_ctx.SlotFactionDefs.Clone();

        /// <summary>Restore a <see cref="SnapshotSlotFactionDefs"/> snapshot IN PLACE — the array reference is
        /// shared with the applier/MainScene and must never be reassigned.</summary>
        private void RestoreSlotFactionDefs(FactionDefinition?[] snapshot)
        {
            for (int i = 0; i < _ctx.SlotFactionDefs.Length; i++)
                _ctx.SlotFactionDefs[i] = snapshot[i];
        }

        /// <summary>
        /// Story 1.8b (D4) — presentation faction-resolution pre-pass. Delegates to the ONE shared
        /// <see cref="SlotFactionResolver.Resolve"/> (DW-229), which resets every slot to its _Ready-seeded default
        /// then resolves each player slot's res:// faction JSON onto ctx.SlotFactionDefs IN PLACE before the
        /// Godot-free applier runs. At first boot the reset is a no-op (the array already equals the seeded
        /// defaults), so this stays behavior-identical to the former inline pre-pass; the same helper drives the
        /// Edit↔Play re-apply so boot and re-apply can never diverge. The reject-rollback around this call
        /// (SnapshotSlotFactionDefs / RestoreSlotFactionDefs) is unchanged.
        /// </summary>
        private void ResolveSlotFactionDefs(ScenarioData scenario) =>
            SlotFactionResolver.Resolve(scenario, _ctx.SlotFactionDefs, _ctx.SeededSlotFactionDefs, _ctx.AbilityRegistry);

        /// <summary>
        /// Story 1.8b / Story 7.7 — the fallback path (scenario JSON missing, unparseable, or REJECTED by the
        /// validator): clear the terrain/pathability sinks (the fallback map is flat — a REUSED applier must not
        /// carry a prior sculpted load's grids), then VALIDATE the Godot-free mirror and apply it through the SAME
        /// <c>Apply(Validated&lt;T&gt;)</c> writer path every other scenario uses (the legacy un-tokened
        /// <c>ApplyFallback</c> is retired — one writer path, one token type). If the mirror itself fails
        /// validation, NOTHING is applied (empty world) and the defect is logged — that is a build defect, not a
        /// runtime path.
        /// </summary>
        private void ApplyFallbackThroughApplier()
        {
            // Flat fallback: no terrain restore, and explicitly clear the grids so a REUSED applier cannot carry a
            // prior sculpted load's elevation/blocking into this flat board (review pass 1 F6 / Story 6.5).
            _ctx.Applier.SetElevationGrid(null);
            _lastElevationGrid = null;
            _ctx.Applier.SetPathabilityGrid(null);
            _ctx.FlowFieldSys?.SetStaticBlocked(null);
            _ctx.Pathability = null;

            // Review follow-up: thread the slot faction defs so the mirror's worker unit_ids resolve by CATEGORY
            // (a custom faction whose worker isn't literally id'd "worker" still spawns its workers).
            ScenarioData mirror = ProjectChimera.Core.Sim.ScenarioApplier.BuildFallbackMirror(_ctx.SlotFactionDefs);
            ValidationResult r = _validator.Validate(mirror, _ctx.SlotFactionDefs);
            if (!r.Ok)
            {
                // Build defect: the shipped mirror must always validate. Apply nothing (empty world), log, and
                // surface it in-scene (review follow-up: an empty world with only a console line read as a silent
                // hang — same toast path as a scenario reject, no-op until the toast label exists).
                GD.PrintErr($"[ScenarioValidator] fallback mirror REJECTED — applying nothing: {r.Error}");
                _ctx.Scene.ShowTriggerMessage($"Fallback map failed validation (build defect) — nothing applied:\n{r.Error}", 8f);
                return;
            }
            _ctx.FallbackMirror = mirror;
            _ctx.Applier.Apply(r.Value);
            _ctx.ScenarioApplied = true; // the validated fallback is the always-applied safety net
        }

        /// <summary>
        /// Create flag-pole markers for the two player start positions. Reads initial XZ from the live scenario
        /// (or fallback defaults). Publishes ctx.StartPosBridge (used by MoveStartPosition).
        /// </summary>
        private void SetupStartPositionBridge()
        {
            // DW-163: reveal markers by DECLARED slot VALUE, not by a contiguous count. Both arrays are indexed by
            // slot value (length MAX_SLOTS) so a validator-legal non-contiguous set (e.g. {0,3}) shows markers 0 and 3
            // at their bases and hides 1 and 2 — the old count-sized array dropped the slot-3 marker at load.
            int max = StartPositionBridge.MAX_SLOTS;
            var positions = new (float x, float z)[max];
            var present   = new bool[max];

            if (_ctx.Scenario?.PlayerSlots != null)
            {
                foreach (var slot in _ctx.Scenario.PlayerSlots)
                {
                    if (slot.Slot < 0 || slot.Slot >= max) continue;
                    positions[slot.Slot] = (slot.BaseX, slot.BaseZ);
                    present[slot.Slot]   = true;
                }
            }
            else
            {
                // Fallback marker positions — these DUPLICATE ScenarioApplier.BuildFallbackMirror()'s slot-0/slot-1
                // BaseX/BaseZ. They are duplicated rather than read from the mirror because this branch runs precisely
                // when there is no _ctx.Scenario to read, and the markers are presentation-only (no sim/checksum
                // effect). The duplication is no longer comment-coupled (DW-324): the mirror's values are pinned by
                // FallbackMirrorParityTests.FallbackMirror_StartPositions_MatchTheScenarioLoadPhaseMarkerFallback,
                // which names these two literals — so moving the mirror's bases without moving these turns Tier-1 RED.
                //
                // The `present[...] = true` flags are LOAD-BEARING, not decoration: StartPositionBridge.Initialize
                // builds every marker but sets Visible from `present[i]`, so dropping them boots the fallback with
                // BOTH flag poles invisible. They were lost exactly that way once (merge c434df6 resolved this hunk
                // wholesale to a branch cut from 024025e, which predated DW-163's `present` array), and no Tier-1 test
                // could see it — this file is <Compile Remove>d from SimSources.props. The parity test above pins the
                // COORDINATES only; MarkerVisibilityFlagsGuard (same test class) is what pins these two flags.
                positions[0] = (-45f, 0f); present[0] = true;
                positions[1] = (+45f, 0f); present[1] = true;
            }

            var startPosBridge = new StartPositionBridge();
            _ctx.Scene.AddChild(startPosBridge);
            startPosBridge.Initialize(positions, present);
            _ctx.StartPosBridge = startPosBridge;
        }
    }
}
