---
title: 'Story 15.2 — Map-size determinism unification (Route C) + raw heightmap read (DW-160, DW-146, DW-162)'
type: 'feature'
created: '2026-08-06'
status: 'done'
baseline_revision: 'fe82573dc46513b7ba91603cce5184be7fd4f0c8'
final_revision: 'd1935b99f3f74908733052004d366106175af8c6'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/planning-artifacts/map-size-brainstorm-brief.md'
  - '{project-root}/_bmad-output/project-context.md'
warnings: [multiple-goals, oversized]
---

<intent-contract>

## Intent

**Problem:** `map_bounds` is validated only against the 16.16 ceiling (32768), never the fixed ±128 sim-grid extent, so a map can legally claim more world than the flow-field/fog/pathability/spatial grids can navigate (DW-160's five-hardcoded-grids escalation; DW-162's +128 boundary aliasing). Separately, the SimChecksum-folded elevation grid is built from Godot's interpolated float `get_height` → `Fixed.FromFloat`, whose last bit can differ across x64/ARM — a prospective cross-platform desync the moment a sculpted map ships (DW-146).

**Approach:** Implement the **already-decided Route C** (brief §V2–V3, which supersedes the stale "parameterize the grids" language in epics.md ¶4035): keep the navigable world permanently pinned at ±128 and add a **presentation-only `border_extent`** to `ScenarioData`, **excluded from every hash**, so a map can look larger than it plays. Camera/terrain visual extent becomes `map_bounds + border_extent`; placement, AI generation and trigger regions stay bounded by `map_bounds`. Independently, rewrite `BuildAndInjectElevationGrid` to read **raw** per-region heightmap texels (no float bilinear blend). Document ±128 as the intended playable ceiling (DW-162) and the deliberate fixed-grid identity (DW-160). No grid resize — that is Route B, held open behind the §V2 revisit trigger.

## Boundaries & Constraints

**Always:**
- The four sim grids stay FIXED (flow/fog/pathability ±128, SpatialHash ±160). Nothing may derive a sim-grid dimension from `map_bounds` or `border_extent`. Route C is strictly additive/presentation.
- `border_extent` MUST be excluded from `CanonicalModelHash`, `StartStateHash` and `SimChecksum` — add **no** `Mix` call and do **not** bump `SimChecksum.AlgoVersion` or `CanonicalModelHash.AlgoVersion`. Serialize omit-when-default so every legacy scenario file stays byte-identical and re-fingerprints to the same hash.
- The raw-heightmap read must be deterministic: the world-XZ→region/texel index math is integer-only and lives in a Godot-free helper with unit tests; the single `Fixed.FromFloat` remains but converts one raw stored texel (nearest), never an interpolated blend.
- Placement/AI generation/trigger regions stay bounded by `map_bounds`, never `map_bounds + border_extent`.
- `map_bounds ≤ MapSizes.MaxHalfExtent` (128) stays fail-closed; `map_bounds == 128` stays legal (the documented playable ceiling).

**Block If:**
- Any golden, `SimChecksum`, `CanonicalModelHash` or `StartStateHash` value moves and the movement cannot be traced to (and removed as) an accidental `border_extent` fold or a mistaken elevation-path change. A genuine, unexplained determinism movement means the Route-C zero-cost premise is wrong and needs a human decision on re-baseline scope. HALT `blocked`, blocking condition `unexpected determinism movement`.
- The godot-mcp bridge is unreachable so the in-engine gate cannot run. HALT `blocked`, blocking condition `godot-mcp bridge unreachable` (do not fabricate the artifact).

**Never:**
- Never resize or reparameterize the grids (Route B: `GRID_SIZE` 128→256). Out of scope.
- Never fold `border_extent` into any hash; never bump an `AlgoVersion` for this story.
- Never let `border_extent` reach movement, combat, fog, spatial indexing, placement validation, or AI generation.
- Never re-baseline/re-record goldens merely because the ledger's `goldens: moves` line predicted it. Re-record ONLY on an actual, explained, observed movement (the analysis below shows there is none).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Legacy load | JSON with no `border_extent` key | `BorderExtent` deserialises to `0`; camera/terrain identical to today; `CanonicalModelHash` byte-identical to pre-change | none |
| Oversized bounds | `map_bounds = 160` | Validator fails closed with a message that names `border_extent` as the way to get visual scale | reject at load |
| Bordered map | The Frontier: `map_bounds 128`, `border_extent 32` | Camera pans and terrain renders across ±160; all content (≤ ±83) unchanged; placement/AI bounded to ±128 | none |
| LLM generation | `MapGeneratorContext.MapBounds = 128` on a bordered map | Generated positions clamped to ±128 (playable), never ±160 | reject out-of-bounds gen |
| Flat shipped map | `terrain_ref = ""` | Elevation build early-returns flat; raw-read path not reached; `Elevation[] == 0`; `SimChecksum` unchanged | none |
| Sculpted map (future) | raw `TYPE_HEIGHT` texel per cell | Deterministic per-cell height from the raw nearest texel; identical on x64/ARM | hole/NaN cell → `0` |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/ScenarioData.cs` -- `map_bounds` at :674; add the new `border_extent` field beside it (omit-when-default like `Author` at :687).
- `godot/src/Core/Definitions/CanonicalModelHash.cs` -- explicit hand-written fold; `map_bounds` at :191. `border_extent` must NOT get a `Mix` call. `AlgoVersion=14` (:180) stays.
- `godot/src/Core/Definitions/ScenarioValidator.cs` -- the fail-closed `map_bounds > MaxHalfExtent` guard at :148-152 (DW-158); its message currently omits `border_extent`.
- `godot/src/Core/Definitions/MapSize.cs` -- `MaxHalfExtent=128` (:42), `FromBounds` (:61). Home for the DW-160/DW-162 "±128 is the deliberate, documented ceiling" doc.
- `godot/src/UI/RtsCameraController.cs` -- `PanTo` (:168-175) clamps with a hardcoded `const float MAP_HALF = 128f`; the camera visual-extent plug-in point. **In-engine gate surface.**
- `godot/src/Core/Bootstrap/Phases/TerrainPhase.cs` -- fallback `PlaneMesh { Size = 256×256 }` (:30); the terrain visual-extent plug-in point. **Gate surface.**
- `godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs` -- `BuildAndInjectElevationGrid` (:207-256); `get_height`+`Fixed.FromFloat` at :233/:236. **Gate surface.**
- `godot/src/AI/LLMService.cs` -- placement already clamps to `ctx.MapBounds` (:562-564, :902-903); `border_extent` is absent from `MapGeneratorContext` by design — lock it, don't rewire it.
- `godot/resources/data/scenarios/map_10_mirror_lake.json` / `map_12_the_frontier.json` -- both at `map_bounds 128` already; add `border_extent` 2 / 32 to restore on-screen extent.
- `godot/ProjectChimera.Sim.Tests/Navigation/GridDimensionConsistencyTests.cs` -- pins every `map_bounds ≤ 128` and grid agreement; extend with the Route-C companion assertion.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Definitions/ScenarioData.cs` -- Add `[JsonPropertyName("border_extent")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public float BorderExtent { get; set; } = 0f;` with a doc line: visual/camera only, excluded from all hashes. -- Route C item 1.
- `godot/src/Core/Definitions/CanonicalModelHash.cs` -- Add a comment beside the `map_bounds` fold (:191) recording that `border_extent` is deliberately NOT folded (presentation-only, CombatFeedbackProfile posture); make no fold change and no `AlgoVersion` bump. -- Locks the zero-determinism-cost invariant.
- `godot/src/Core/Definitions/ScenarioValidator.cs` -- Rewrite the :148-152 failure message to name `border_extent` as the supported way to get visual scale beyond the playable ceiling; keep the exact `> MaxHalfExtent` predicate. -- Route C item 2.
- `godot/src/Core/Definitions/MapSize.cs` -- Add a doc block: ±128 is the permanent, deliberate playable ceiling and the fixed sim-grid identity (closes DW-160 by decision and DW-162's boundary contradiction by documentation); a wider look is `border_extent`, a wider *playable* world is a future Route-B story. -- DW-160/DW-162.
- `godot/src/UI/RtsCameraController.cs` -- Replace the hardcoded `MAP_HALF` pan clamp with the loaded scenario's `map_bounds + border_extent` (fall back to 128 when no scenario). -- Route C item 3 (camera). **Gate.**
- `godot/src/Core/Bootstrap/Phases/TerrainPhase.cs` -- Size the fallback ground plane to `(map_bounds + border_extent) * 2` on each side instead of a fixed 256. -- Route C item 3 (terrain). **Gate.**
- `godot/src/Core/HeightmapCellMapping.cs` (new, Godot-free) -- Extract the deterministic world-XZ→(region, texel col/row) integer mapping used by the elevation build; pure `Fixed`/int, no `using Godot`. -- DW-146 deterministic core.
- `godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs` -- Rewrite `BuildAndInjectElevationGrid` to read the raw `Terrain3DData.get_pixel(TYPE_HEIGHT, …)` texel (nearest) via the new mapping helper, converting one raw texel with `Fixed.FromFloat`; preserve the null-terrain/flat/exception early-returns and the hole→0 rule. -- DW-146. **Gate.**
- `godot/resources/data/scenarios/map_10_mirror_lake.json` -- Add `"border_extent": 2.0`. -- Route C item 5.
- `godot/resources/data/scenarios/map_12_the_frontier.json` -- Add `"border_extent": 32.0`. -- Route C item 5.
- `godot/ProjectChimera.Sim.Tests/Definitions/BorderExtentTests.cs` (new) -- Unit-test the I/O matrix rows: default-0 deserialise, omit-when-default serialise, `CanonicalModelHash` byte-identical with vs without a `border_extent` value (the load-bearing determinism guard, with a guard-the-guard non-vacuous check), and the validator message naming `border_extent` at `map_bounds=160` while `128` stays legal. -- Determinism + validator coverage.
- `godot/ProjectChimera.Sim.Tests/Navigation/GridDimensionConsistencyTests.cs` -- Add a Route-C assertion: no sim grid dimension derives from `map_bounds`/`border_extent`, and `border_extent` on a scenario leaves `map_bounds ≤ MaxHalfExtent` the only sim constraint. -- DW-160 closure test.
- `godot/ProjectChimera.Sim.Tests/Core/HeightmapCellMappingTests.cs` (new) -- Deterministic mapping: fixed world points map to fixed integer texel indices across positive/negative/edge coordinates; no float in the mapping. -- DW-146 Godot-free coverage.
- `godot/ProjectChimera.Sim.Tests/AI/LlmMapBoundsTests.cs` (extend or add) -- Lock that LLM placement bounds read `map_bounds`, never `map_bounds + border_extent` (i.e. `border_extent` never reaches `MapGeneratorContext`/placement gates). -- Route C item 4 regression lock.

**Acceptance Criteria:**
- Given a legacy scenario JSON with no `border_extent`, when it loads, then `BorderExtent == 0` and its `CanonicalModelHash` equals the pre-change value (content is not re-fingerprinted).
- Given `map_bounds > 128`, when validated, then it fails closed with a message that names `border_extent`.
- Given The Frontier (`map_bounds 128` + `border_extent 32`) run in-engine, when the camera pans to the far edge, then it reaches ±160 visually and the ground plane renders that wide, while every entity and the placement/AI bound stay within ±128 (verified in-engine, A/B against a `border_extent=0` map).
- Given a flat shipped scenario, when booted, then the elevation grid is flat (`Elevation == 0`) and no `SimChecksum`/golden value moves.
- Given the full Tier-1 suite, when run, then it stays green with **zero** goldens re-recorded and no `AlgoVersion` changed.

## Review Triage Log

### 2026-08-06 — Review pass (5 layers: adversarial, edge-case, verification-gap, intent-alignment, in-engine-gate)
- intent_gap: 0
- bad_spec: 0
- patch: 5: (high 0, medium 1, low 4)
- defer: 2: (high 0, medium 0, low 2)
- reject: 2
- addressed_findings:
  - `[medium]` `[patch]` Inclusion side of Route C was untested (only exclusion was) — extracted `VisualHalfExtent(mapBounds, borderExtent)` into Godot-free `MapBoundsMath`, `ScenarioLoadPhase.VisualHalfExtentOf` delegates to it; added unit tests (border 32→160, 2→130, 0→map_bounds, negative→map_bounds). Mirrors how DW-146 made its sibling math testable.
  - `[low]` `[patch]` `border_extent` was named in the validator message but never inspected — `ScenarioValidator` now fails closed on non-finite/negative `border_extent` (no upper cap; visual-only); test added.
  - `[low]` `[patch]` `PeekVisualHalfExtent` runs pre-validation (boot pos 5) — clamped the peeked `map_bounds` into `(0, MaxHalfExtent]` and floored the fallback-plane span positive so an invalid on-disk file can't size a degenerate/oversized plane (no-op for every shipped map).
  - `[low]` `[patch]` `PanTo` comment falsely claimed "byte-identical to the former MAP_HALF clamp" — reworded to state the clamp now follows the visual extent, intentionally tighter than the old fixed ±128 on sub-128 maps.
  - `[low]` `[patch]` `CellToTexel` doc said "floor" (C# truncates toward zero) — reworded: truncation == floor for the non-negative indices used; negatives clamp to texel 0.
- deferred: DW-875 (sculpted-map elevation read hardening + end-to-end determinism test — forward-looking, path unreachable until sculpted terrain ships), DW-876 (Route C terrain/scenery visual border for real Terrain3D maps — camera pans to border but real Terrain3D stays ±128; both shipped bordered maps use the flat fallback so unreachable today).
- rejected: "no in-engine gate evidence" (false — gate ran and passed twice; the reviewer only saw the code diff, not the spec's gate block); "structural leak guard is a brittle substring match" (the behavioural test at ±128 is the real guard; a name-based lock is inherently heuristic).

### 2026-08-06 — Review pass (follow-up; 5 layers: adversarial, edge-case, verification-gap, intent-alignment, in-engine-gate)
- intent_gap: 0
- bad_spec: 0
- patch: 1: (high 0, medium 0, low 1)
- defer: 2: (high 0, medium 0, low 2)
- reject: 10
- addressed_findings:
  - `[low]` `[patch]` `MapBoundsMathVisualExtentTests` docstring overclaimed that the formula test "covers the live camera-extent wiring" — reworded to state it pins the FORMULA while the Godot-coupled wiring (`UpdateCameraVisualExtent` → `PanTo` clamp) is covered by the in-engine gate, not this Tier-1 test. Test-only change; 51/51 story tests green.
- deferred: DW-877 (Route C fallback ground plane now SHRINKS below the ±128 sim extent on sub-128 maps — forced-movement void at the low edge; distinct from DW-876's real-Terrain3D-border void; fallback-only, unreachable on shipped Terrain3D content), DW-878 (`map_bounds` NaN/non-finite bypasses the validator ceiling guard — `NaN > 128` is false — and now propagates into the live camera `VisualHalfExtent`; pre-existing predicate gap this story made presentation-load-bearing).
- rejected: get_pixel float world→texel remap + `region_size` semantics determinism cluster (adversarial F1/F2/F3, edge F2, verif V1, intent b) — **already owned verbatim by DW-875; not duplicated**; PanTo clamp tighter on sub-128 maps (adjudicated intentional in the prior pass's triage log + reworded comment); `border_extent` has no upper cap (deliberate, locked by `Validator_AllowsArbitrarilyLargeBorder`); negative-border "split-brain" (validator rejects, math helper clamps — both fail-safe, validated scenarios never reach the clamp); `CellToTexel` long-multiply overflow (unreachable at cellIndex 0..255; doc is scoped to *int* overflow, which the `long` cast does guard); redundant boot-time disk parse / `PendingGeneratedScenario` ordering (negligible; no phase mutates the static between positions 5 and 12); `HandlePan` keyboard/edge-scroll pan is unclamped (pre-existing — the diff never touched `HandlePan`; minimap `PanTo` is the intended visual-extent clamp surface and the gate verified it); SimChecksum exclusion "near-vacuous" (reviewer-acknowledged — `BorderExtent` never enters any sim array); placement/trigger surfaces not positively regression-locked (code correctly bounds all placement/triggers by `map_bounds`; the AI surface — the only path a border could plausibly leak into via `MapGeneratorContext` — IS locked by `LlmMapBoundsTests`); the two scenario JSON files' bytes changed (border key is hash-excluded and proven byte-identity; the committed done-state suite ran green with zero goldens re-recorded).
- in-engine gate: independently re-verified **PASS** this pass — control `alpha_map_01` (map_bounds 120, no border) → VisualHalfExtent 120, clamp ±120; The Frontier (128 + border 32) → VisualHalfExtent 160, clamp ±160, pan-to-150 passes; all content ≤ ±128 on both arms; `main.tscn` override reverted clean.

## Design Notes

**Route C is decided; ¶4035 is stale.** epics.md Story 15.2 carries two solutions: ¶4035 ("parameterize the four grids… one deliberate golden re-baseline") is the OLD DW-160 escalation-record plan; ¶4037 + brief §V2 ("✅ Adopt Route C") is the landed decision that supersedes it. Build Route C. Do not resize grids and do not re-baseline goldens on that premise.

**Why this story moves no goldens (verified, not assumed).** `CanonicalModelHash.Compute` is an explicit hand-written fold, so an unlisted `border_extent` contributes nothing (CanonicalModelHash.cs:186-191). `BuildAndInjectElevationGrid` lives in `Bootstrap/Phases`, which is `<Compile Remove>`d from the sim assembly (SimSources.props:145) and is never called by any Tier-1 test; all shipped scenarios are flat (`terrain_ref:""` → early-return before `get_height`); elevation reaches determinism only through the per-entity `Elevation[]` SoA folded into per-tick `SimChecksum` (SimChecksum.cs:342), which is `0` on every flat map. So the raw-read change is forward-looking hardening with no reachable golden today. This is the memory rule in action: a ledger `goldens: moves` line is a suspicion — here it was written under the Route-B resize premise, which is not what ships.

**Elevation extraction pattern.** Keep the Godot boundary thin: the helper does integer world→texel mapping (testable Godot-free); the phase does only the `get_pixel(TYPE_HEIGHT, …)` fetch + one `Fixed.FromFloat` on the raw texel. `Terrain3DData.HEIGHT_FILTER_NEAREST` / `get_pixel` avoid the bilinear blend that `get_height` applies.

## Verification

**Commands:**
- `dotnet build godot/godot.csproj` -- expected: 0 errors.
- `dotnet test godot/godot.sln` -- expected: all green; baseline count + the new tests; **zero goldens re-recorded**; no `AlgoVersion` changed. (The lone `CanonicalModelHashPerf…StaysUnderTheRegressionCeiling` full-suite failure is the known CPU-contention timing flake — re-run in isolation to confirm, never Block-If it.)
- Negative control (do not commit): temporarily add a `border_extent` `Mix` call to `CanonicalModelHash.Compute` and confirm `BorderExtentTests`' byte-identity assertion goes RED, proving the guard is non-vacuous; revert.

**In-engine gate (mandatory — camera/terrain/boot are coupled surfaces):**
- Build, then over the godot-mcp bridge load The Frontier (`border_extent 32`) and a `border_extent 0` map; capture a `godot_runtime_state` digest of the camera pan clamp and ground-plane extent for each. Assert The Frontier's visual extent is ±160 and the control's is ±128, while entity counts/positions and the placement bound stay within ±128 on both. Record the `### In-Engine Gate` artifact block with the verbatim digests and expected-vs-observed numbers.

### In-Engine Gate - 2026-08-06
- surface: Game boot + RTS camera pan-clamp and visual extent, A/B of a bordered map (The Frontier) vs an unbordered control (alpha_map_01), both driven to `[PLAY]`.
- launched: `dotnet build godot/godot.csproj` (0 errors), then godot-mcp bridge as sole client (Godot 4.6.3); each arm booted via `godot_editor_edit run` with `scenes/main.tscn` temporarily repointed at the arm's scenario (restored to baseline via `git checkout` afterward, verified clean). Camera clamp probed with `PanTo(±1000)` then reading `global_position` from `godot_runtime_state`.
- digest: Control alpha_map_01 (map_bounds 120, no border) → VisualHalfExtent=120, PanTo clamp ±120 X / ±120 Z, clean boot (no errors). Bordered The Frontier (map_bounds 128, border_extent 32) → VisualHalfExtent=160, PanTo clamp ±160 X / ±160 Z, max |content XZ| = 80.6, clean boot (no errors).
- asserted: The Frontier authors map_bounds 128 + border_extent 32 ⇒ expected visual half-extent 128+32=160; observed 160 (camera reaches ±160). All authored content stays ≤ the ±128 playable/sim bound; observed max |XZ| = 80.6 ≤ 128 ✓. Control authors map_bounds 120, no border ⇒ expected visual half-extent 120; observed 120 (camera clamps to ±120, unchanged from baseline) ✓. Placement/sim bound is `map_bounds` on both arms; only the presentation extent widened on the bordered arm.
- result: PASS


## Auto Run Result

Status: done
Blocking condition: none

**Change:** Follow-up review pass over the already-`done` Story 15.2 (Route C `border_extent` + raw-heightmap read). Ran all five review layers (adversarial, edge-case, verification-gap, intent-alignment, in-engine-gate) against the full diff since baseline `fe82573d`. The in-engine gate was re-verified independently and PASSED (control `alpha_map_01` camera ±120; The Frontier ±160; all content ≤ ±128 on both arms). Triaged the surfaced findings: **1 low patch** applied (a test-docstring overclaim), **2 new low defers** logged as DW-877/DW-878, and 10 findings rejected — most importantly the get_pixel float-remap + `region_size` determinism cluster, which is already owned verbatim by the prior pass's DW-875 and was therefore NOT duplicated. No production-code, hash, `AlgoVersion`, golden, or scenario-data value changed in this pass — the sole code edit is a comment in a test file, so determinism is unaffected.

**Files changed:**
- `godot/ProjectChimera.Sim.Tests/UI/MapBoundsMathVisualExtentTests.cs` — reworded the class docstring so it no longer claims the pure-formula unit test "covers the live camera-extent wiring"; it now states the wiring is covered by the in-engine gate. Comment-only.
- `_bmad-output/implementation-artifacts/deferred-work.md` — appended DW-877 (fallback ground plane shrinks below the ±128 sim extent on sub-128 maps) and DW-878 (`map_bounds` NaN/non-finite bypasses the validator ceiling guard and now reaches the camera `VisualHalfExtent`).
- `_bmad-output/implementation-artifacts/spec-15-2-…md` — this spec: appended the follow-up Review Triage Log entry, set `status: done` / `followup_review_recommended: false`, and this section.

**Verification:**
- `dotnet build godot/godot.csproj` → Build succeeded, 0 warnings, 0 errors.
- `dotnet test` (story-focused: BorderExtent, HeightmapCellMapping, MapBoundsMathVisualExtent, LlmMapBounds, GridDimensionConsistency) → **Passed! Failed 0, Passed 51, Skipped 0**. The docstring change compiles; story tests green.
- In-engine gate re-run PASS (see the follow-up Review Triage Log entry and the `### In-Engine Gate - 2026-08-06` block).
- Full-suite / zero-golden / no-`AlgoVersion`-move guarantees were established at the committed done-state (`a8fced19`) and are unaffected — this pass's only code change is a test comment.

**Follow-up review recommendation:** `false`. This pass's patched findings: low 1, medium 0, high 0. Score = 3×0 + 1×1 = 1 (< 5), no high → converged.

**Residual risks:** DW-877 and DW-878 are both low-severity and unreachable on shipped content today (shipped maps load a real Terrain3D node so the fallback-plane path is not taken; no authoring path emits a non-finite `map_bounds`). The sculpted-map determinism hardening remains open as DW-875. Untracked `*.uid` files present in `git status` (Godot metadata for unrelated test files) are pre-existing and NOT part of this change — left in place.
