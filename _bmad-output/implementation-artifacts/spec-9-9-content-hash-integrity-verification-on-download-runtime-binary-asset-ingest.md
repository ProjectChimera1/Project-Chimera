---
title: 'Content-hash integrity verification on download + runtime binary-asset (GLB) ingest'
type: 'feature'
created: '2026-07-24'
status: 'done'
baseline_revision: '8b5b9a233d76d7b9f182cba87b38cacf67fb5cc2'
final_revision: 'b90e75358d043f889eea7fa8016510d2b70761bf'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/project-context.md'
  - '{project-root}/_bmad-output/implementation-artifacts/epic-9-context.md'
  - '{project-root}/godot/src/Core/Definitions/ContentPackager.cs'
  - '{project-root}/godot/src/Core/Definitions/ContentPackageManifest.cs'
  - '{project-root}/godot/src/UI/MeshLoader.cs'
  - '{project-root}/godot/src/UI/MultiMeshBridge.cs'
  - '{project-root}/godot/src/UI/ContentBrowserPanel.cs'
warnings: ['multiple-goals', 'oversized']
---

<intent-contract>

## Intent

**Problem:** A downloaded `.chimera.zip` verifies only its `scenario.json` bytes (`ContentPackager.Unpack`, :259-266) — any bundled custom art is unverified, and no runtime path can even render a custom `.glb` in a shipped (non-editor) build: the only loader, `MeshLoader.LoadFromGlb`, uses `GD.Load<PackedScene>`, which needs an editor `.import` sidecar that downloaded assets never have. So community art is neither trustworthy on download nor visible in exported builds.

**Approach:** Bundle custom assets under `assets/` in the package and fold their bytes into a package-integrity `AssetHash` (a sibling of the existing `ScenarioHash`/`TerrainHash`, same FNV-1a + ordinal-sort family), verified unconditionally in `Unpack` so a tampered/corrupt asset byte rejects the download with a located error. Add a net-new runtime ingest path — `GLTFDocument.AppendFromFile → GenerateScene` (NOT `GD.Load<PackedScene>`) behind a Godot-free `AssetValidator` (allow-list, size cap, vertex/submesh caps) — that registers each valid mesh in a net-new `AssetRegistry` keyed by its logical package path and falls back to the box placeholder on any invalid/unsafe asset, never crashing.

## Boundaries & Constraints

**Always:**
- The asset integrity hash uses the SAME FNV-1a family and ordinal-sort-by-filename pattern as `ContentPackager.HashTerrainFiles` (fold filename bytes then content bytes). `AssetHash == 0` ⇔ no assets bundled, byte-identical to a pre-9.9 package. `Unpack` verifies unconditionally whenever `manifest.AssetFiles.Count > 0` (mirroring the terrain check at :312-322), and a listed-but-absent asset is a located `InvalidDataException`, never a silent skip.
- Runtime `.glb` ingest MUST use `GLTFDocument.AppendFromFile → GenerateScene` so it works in a non-editor/exported build (a downloaded `.glb` has no `.import`); `GD.Load<PackedScene>` is forbidden for downloaded assets.
- Every ingest failure mode — non-allow-listed extension, over size cap, over vertex/submesh cap, malformed GLB, GLB with no `MeshInstance3D`, any thrown exception — falls back to the shared box placeholder (mirroring `MeshLoader`'s `MakePlaceholder`) and NEVER throws to the caller or crashes the game.
- The hash fold + `AssetValidator` decision rules are Godot-free and Tier-1 tested in `ProjectChimera.Sim.Tests`. The `GLTFDocument` ingest, `AssetRegistry`, render-path wiring, and download-verify wiring are the presentation seam (verified by inspection + re-implemented mirrors + documented live-verify), consistent with the accepted boundary of sibling Stories 9.3/9.4/9.6/9.7/9.8.
- Reuse existing infrastructure: `ContentPackager.Pack/Unpack`, the terrain-hash precedent, `MeshLoader` box placeholder, the `MultiMeshBridge` render path, `ContentBrowserPanel.OnDownloadComplete`. Add no NuGet dependency (NakamaClient stays the sole dep).

**Block If:**
- Asset integrity cannot be added without bumping `CanonicalModelHash.AlgoVersion`, `SimChecksum.AlgoVersion`, or `PROTOCOL_VERSION`, or moving a committed golden — this is a package-download integrity hash (the `ScenarioHash` sibling), strictly outside the sim/start-state hash.

**Never:**
- Never fold asset bytes into `SimChecksum` or `CanonicalModelHash`, and never change any committed golden or algo/protocol version. This is the `ContentPackager` FNV package-integrity path only.
- Never treat this as anti-cheat: the hash catches corruption/tampering-in-transit for trusted-friends EA; a wholly re-packaged archive whose manifest hash was recomputed to match is out of scope (server attestation is the later online rail).
- Never build image/sprite/portrait or audio (`.png`/`.ogg`) runtime ingest here — no definition field consumes custom images/audio yet, so `.glb` mesh ingest is the only ingest built (image/audio ingest is a documented AR-27 deferral). Bundling+hashing may still cover any asset file generically.
- Never load an un-validated asset into the scene tree; validate before (size/extension) and after (mesh complexity) generation.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Pack with assets | `PackOptions.AssetPaths=[a.glb,b.glb]` | Entries written under `assets/`, `manifest.AssetFiles` recorded in list order, `AssetHash = FNV(ordinal-sorted name+bytes)` | n/a |
| Pack no assets | `AssetPaths` empty | `AssetHash == 0`, no `assets/` entries, byte-identical to a pre-9.9 package | n/a |
| Unpack clean | `AssetFiles` present, bytes intact | Assets extracted to `assets/` subdir, recomputed hash == `AssetHash`, `UnpackResult.AssetFiles` populated | n/a |
| Unpack tampered asset | one asset byte changed post-publish | recomputed hash != `AssetHash` → `InvalidDataException` (expected vs got, "Package may be corrupt.") | Rejected; not playable |
| Unpack missing listed asset | manifest lists `assets/x.glb` absent from zip | located `InvalidDataException` naming the missing entry | Rejected |
| Ingest valid glb | extracted `.glb`, ≤ caps, ≥1 mesh | `GLTFDocument` scene generated, first `Mesh` registered in `AssetRegistry` under its logical path | n/a |
| Ingest invalid glb | oversized OR over vertex/submesh cap OR malformed OR no `MeshInstance3D` | box placeholder registered under the logical path; no throw | Placeholder, logged |
| Ingest disallowed ext | `assets/evil.exe` (or any non-`.glb`) | `AssetValidator` rejects → not ingested (no scene load) | Skipped, logged |
| Render custom unit | unit `MeshPath` is a logical `assets/*` id in the registry | registry mesh used; id absent → `res://` load → box placeholder | Placeholder |
| Download verify | mod downloaded via `ModIoService` | `Unpack` runs (integrity-verified) then valid `assets/` ingested into `AssetRegistry` before the package is playable; `InvalidDataException` marks it not-playable with the reason | Rejected on mismatch |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/ContentPackageManifest.cs` — add `asset_files` (`List<string>` zip-relative, default empty) + `asset_hash` (`uint`, 0 = none), documented as the `terrain_files`/`terrain_hash` sibling. Update the class doc's `models/` example to `assets/`.
- `godot/src/Core/Definitions/ContentPackager.cs` — `PackOptions` gains `List<string> AssetPaths` (absolute on-disk). `Pack`: enumerate existing asset files → `assets/{name}` (list order for `AssetFiles`, ordinal-sorted for the hash), fold via a `HashAssetFiles` helper (reuse the existing FNV-1a `FNV_PRIME`/`FNV_OFFSET` + the `HashTerrainFiles` folding shape — generalize into one internal `HashFiles(files)` used by both), write entries, set `manifest.AssetFiles`/`AssetHash`. `Unpack`: extract listed assets to an `assets/` subdir, throw located `InvalidDataException` on a missing listed entry, verify recomputed hash unconditionally when `AssetFiles.Count>0`, add `List<string> AssetFiles` to `UnpackResult`.
- `godot/src/Core/Definitions/AssetValidator.cs` — **NEW**, Godot-free. Constants `MaxAssetBytes`, `MaxVertexCount`, `MaxSurfaceCount`, allow-list `{".glb", ".gltf"}`. `AssetValidationResult Validate(string fileName, long byteLength)` (extension + size) and `AssetValidationResult ValidateMeshComplexity(int vertexCount, int surfaceCount)` returning `{ bool Ok; string? Reason }`. Pure; Tier-1 testable.
- `godot/src/UI/RuntimeAssetIngest.cs` — **NEW**, Godot-side (`using Godot`). `Mesh Ingest(string absGlbPath, Vector3 fallbackSize, Color fallbackColor)`: pre-validate via `AssetValidator.Validate` (name+`FileAccess`/`File` size); `GLTFDocument.AppendFromFile` into a fresh `GltfState` → `GenerateScene` → find first `MeshInstance3D.Mesh` (reuse `MeshLoader`'s recursive find); post-validate mesh vertex/surface counts via `AssetValidator.ValidateMeshComplexity`; on ANY failure/exception return `MeshLoader.MakePlaceholder`-equivalent box. Never throws.
- `godot/src/UI/AssetRegistry.cs` — **NEW**, Godot-side. `void Register(string logicalId, Mesh)`, `bool TryGet(string logicalId, out Mesh)`, `void IngestPackage(string extractDir, IEnumerable<string> assetFiles)` (ingest each valid `assets/*.glb` via `RuntimeAssetIngest`, registering under its zip-relative logical id). One instance owned by the render/session root.
- `godot/src/UI/MeshLoader.cs` — expose `MakePlaceholder`/`FindFirstMesh` to `RuntimeAssetIngest` (make `internal`), and add an `AssetRegistry?` consult: when a unit's `MeshPath` is a non-`res://` logical id present in the registry, return the registered mesh; else the existing `res://` path; else box. Keep the 3-arg world-spawn signature working (default null registry = today's behavior).
- `godot/src/UI/MultiMeshBridge.cs` — thread an optional `AssetRegistry` into `Initialize` and pass it to the `MeshLoader.LoadFromGlb` call (:74) so custom-unit meshes resolve through the registry; null registry = unchanged.
- `godot/src/UI/ContentBrowserPanel.cs` — in `OnDownloadComplete` (:852): `Unpack` the downloaded zip to a `user://` cache (integrity verify) and, on success, `AssetRegistry.IngestPackage`; on `InvalidDataException` surface the reason and mark the download not-playable (do not add the card as ready).
- `godot/ProjectChimera.Sim.Tests/Definitions/**` — **NEW** Tier-1 tests: `ContentPackagerAssetHashTests` (pack→unpack round-trip; tamper an asset byte → `InvalidDataException`; missing listed asset → located error; no-assets → `AssetHash==0` + byte-identical) and `AssetValidatorTests` (extension allow/deny, size cap boundary, mesh-complexity cap boundary).

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Definitions/ContentPackageManifest.cs` — add `AssetFiles`/`AssetHash` fields (terrain-sibling); fix `models/`→`assets/` doc.
- `godot/src/Core/Definitions/ContentPackager.cs` — `PackOptions.AssetPaths`; `Pack` bundles+hashes assets; `Unpack` extracts+verifies+returns `AssetFiles`; hoist one shared `HashFiles` helper.
- `godot/src/Core/Definitions/AssetValidator.cs` (NEW) — Godot-free allow-list/size/complexity caps + result type.
- `godot/src/UI/RuntimeAssetIngest.cs` (NEW) — `GLTFDocument`-based `.glb` ingest with pre/post validation + box fallback, never throws.
- `godot/src/UI/AssetRegistry.cs` (NEW) — logical-id→`Mesh` registry + `IngestPackage`.
- `godot/src/UI/MeshLoader.cs` — registry consult + `internal` placeholder/find helpers.
- `godot/src/UI/MultiMeshBridge.cs` — thread the registry into per-type mesh load.
- `godot/src/UI/ContentBrowserPanel.cs` — post-download `Unpack` verify + `IngestPackage`; reject on mismatch.
- `godot/ProjectChimera.Sim.Tests/Definitions/**` (NEW) — Tier-1 tests covering every hash + validator I/O-Matrix row.

**Acceptance Criteria:**
- Given a package whose manifest folds asset bytes into `AssetHash`, when it is unpacked, then the asset bytes are verified and a tampered/corrupt or missing asset is rejected with a located `InvalidDataException` (extending, not replacing, the existing `scenario_hash` check).
- Given a `.glb` in a downloaded package in a non-editor build, when the runtime ingests it, then it loads via `GLTFDocument.AppendFromFile → GenerateScene` (never `GD.Load<PackedScene>`), passes `AssetValidator`, and registers in `AssetRegistry`; an invalid/unsafe/malformed asset registers the box placeholder instead of crashing.
- Given a subscribed package, when download completes, then it is `Unpack`-verified and its valid assets ingested before it is playable, and a hash mismatch blocks it.
- Given the full suite, when it runs, then every pre-existing committed golden is byte-identical and `CanonicalModelHash.AlgoVersion`(14)/`SimChecksum.AlgoVersion`(21)/`PROTOCOL_VERSION`(2) are unchanged (a moved golden = Block-If).

## Spec Change Log

_None — no bad_spec loopback occurred; the review pass resolved via patches only._

## Review Triage Log

### 2026-07-24 — Review pass (review_loop_iteration 0)
- intent_gap: 0
- bad_spec: 0
- patch: 9: (high 1, medium 5, low 3)
- defer: 2
- reject: 6
- addressed_findings:
  - `[high]` `[patch]` Runtime ingest populated the CURRENT scene's `SceneContext.AssetRegistry` at download-complete, but the play path (`HandleLoadMap` → `ReloadCurrentScene`) rebuilds a fresh empty registry before `FactionVisualsPhase` reads it — so a downloaded custom mesh could never render on any real flow. Moved the ingest to the load-to-play path (`FactionVisualsPhase.IngestImportedAssets` from `user://imported_maps/<stem>/assets/`), keeping the download-time `Unpack` integrity-verify; download-time ingest removed as moot.
  - `[medium]` `[patch]` Buildings resolved meshes through the pre-9.9 3-arg `MeshLoader.LoadFromGlb` (BuildingBridge.cs:121), never the registry — custom building meshes would silently box. Threaded `_ctx.AssetRegistry` into `buildingBridge.Initialize` and the per-type load, mirroring the unit bridge.
  - `[medium]` `[patch]` Two `AssetPaths` with the same leaf name collided on `assets/{leaf}`: the hash folded two files but `Unpack` recovered one twice → the package failed its OWN integrity check. `Pack` now rejects duplicate leaf names (test added).
  - `[medium]` `[patch]` The extension allow-list + size cap were enforced only at render-path ingest, so `Unpack` extracted any listed entry (arbitrary extension / unbounded size) to disk. `Unpack` now gates extension + `MaxAssetBytes` with located throws before extraction; `.gltf` dropped from the allow-list (a single bundled file can't carry its external `.bin`/textures). Tests added; `.gltf` test flipped to expect rejection.
  - `[medium]` `[patch]` `ValidateMeshComplexity` passed a `surfaceCount≥1` mesh whose vertex arrays were empty/unreadable (`CountVertices`→0 ≤ cap). Now fails closed on `vertexCount<=0` with surfaces present → placeholder (test added).
  - `[medium]` `[patch]` The download success log dereferenced `Manifest.AssetFiles.Count` unguarded; an explicit `"asset_files": null` made a clean unpack NRE → a sound package wrongly marked "Verify failed ✗". Now `?.Count ?? 0` with null-guarded iteration.
  - `[low]` `[patch]` `RuntimeAssetIngest` used `QueueFree()` on the never-parented generated scene (defers to frame-end, piles up during batch ingest) — switched to `Free()` + corrected comment.
  - `[low]` `[patch]` The verify-only `user://package_cache/<modId>` extraction leaked on both success and failure — now deleted in a `finally`.
  - `[low]` `[patch]` Comments overclaimed the unkeyed FNV `AssetHash` as tamper-proof ("a tampered manifest cannot zero AssetHash"). Reworded to corruption/in-transit detection (matching the ScenarioHash/TerrainHash precedent; anti-cheat is out of scope per intent).
- deferred:
  - No export-side producer populates `PackOptions.AssetPaths` — the "import → package" asset-reference-resolution flow (WC3 Import Manager, epics.md:3707) is a separate/future slice; 9.9 correctly provides the Pack API but no real editor flow bundles a scenario's custom models yet.
  - `Pack` reads each asset for the hash, then re-reads it at `WriteEntry` (a TOCTOU window where a mutation yields a package that fails its own `Unpack`). Pre-existing shape shared with the terrain path; low-likelihood.
- rejected (dropped): sign/HMAC the manifest / "FNV isn't tamper-proof" (intent explicitly excludes anti-cheat; matches the shipped ScenarioHash/TerrainHash unkeyed-FNV precedent — corruption/in-transit only); zip-bomb decompression-ratio / total-size / entry-count caps beyond the per-entry size gate (hostile-actor threat model, out of the trusted-friends EA scope per intent); `MaxSurfaceCount=16` too low + surface-rejection in-UI messaging (documented chosen cap; UI polish beyond scope, tunable later); cross-mod registry-key namespacing (single-active-map load replaces `res://` per `HandleLoadMap`, so no multi-package coexistence collides in any real flow); integration/godot test asserting a bundled GLB renders (the accepted live-verify seam — `GLTFDocument` can't run headless in Tier-1); corrupt zip still shown as a Local card (fails SAFE — `HandleLoadMap.Unpack` rejects it on load).

### 2026-07-24 — Review pass (follow-up, review_loop_iteration 0)
- intent_gap: 0
- bad_spec: 0
- patch: 0
- defer: 4: (high 0, medium 4, low 0)
- reject: 11: (high 0, medium 6, low 5)
- addressed_findings:
  - none
- deferred (appended to `deferred-work.md` as NEW entries):
  - `[medium]` `Pack` validates duplicate leaf names but not asset extension/size, while `Unpack` rejects both — a creator can build a package that packs cleanly yet every downloader's `Unpack` rejects (self-invalidating; discovered only after publish).
  - `[medium]` A download that fails integrity verify cleans only the throwaway `package_cache/` extraction, not the raw `.chimera.zip` in `user://packages/`, so `RefreshLocal` re-lists the rejected package unverified next launch.
  - `[medium]` Load-path ingest enumerates the extracted `imported_maps/<id>/assets/` dir (not the verified manifest `AssetFiles`), and the dir is never cleared before extraction — a stale/orphan `.glb` from a prior same-`Id` import can render unverified.
  - `[medium]` The custom-mesh registry resolution matches `MeshPath` to logical ids by exact, case-sensitive string with no diagnostic on a miss — a mis-authored custom unit renders the grey box with nothing to debug.
- rejected (dropped): all four verification-gap findings (mesh-cap enforcement wiring, MeshLoader registry-resolution predicate, download-verify gate, `IngestImportedAssets` cross-phase coupling untested) and the AssetRegistry skip-branch — the presentation seam is explicitly accepted by intent as inspection + re-implemented mirrors + documented live-verify (sibling 9.3-9.8 boundary); intent-alignment observations (descriptive, confirm the goal reading is implemented faithfully — no action); the "Downloaded" label "overstating" the corruption check (intent explicitly de-scopes anti-cheat; the label reflects the actual check); `OnDownloadComplete` threading concern (unconfirmed/speculative — reviewer could not establish an off-thread callback); duplicate-leaf collision at `Unpack` (unreachable via honest `Pack`, which maps every asset flat to `assets/{leaf}` and already guards dup leaves — requires a hand-crafted manifest, de-scoped); post-`GenerateScene` complexity-cap allocation + `CountVertices` no early-out + re-decode-per-scene-reload perf (adversarial mega-mesh input is de-scoped, and the reload cost is EA-acceptable); asset-count cap + `entry.Length`-trust resource bounds (same hostile-actor threat model the iteration-0 pass already rejected as out of trusted-friends EA scope).

## Design Notes

**Parallel hash, not a combined one.** AC1 says "the full content hash (scenario + bundled asset bytes)". Two readings — one combined hash, or `ScenarioHash` unchanged + a parallel `AssetHash` — produce identical observable outcomes for every AC row (tampered scenario → reject; tampered asset → reject; old package → still valid). The parallel `AssetHash` is chosen because it exactly follows the shipped `TerrainHash` precedent (:293-322), keeps existing `scenario_hash` semantics and byte-compat, and needs no re-baseline. This is a defensible single pick, not an intent gap.

**Caps (chosen, documented).** `MaxAssetBytes = 32 * 1024 * 1024` (32 MB), `MaxVertexCount = 200_000`, `MaxSurfaceCount = 16` — generous for the ~18-30k-vert feet-pivoted GLBs the project already ships, tight enough to reject a hostile mega-mesh. These are named constants a later story can lift.

**GLTFDocument shape (Godot-side, ~6 lines):**
```csharp
var doc = new GltfDocument();
var state = new GltfState();
if (doc.AppendFromFile(absGlbPath, state) != Error.Ok) return Box();
var scene = doc.GenerateScene(state);          // Node
var mesh  = MeshLoader.FindFirstMesh(scene);   // reuse the recursive finder
scene?.QueueFree();
// then AssetValidator.ValidateMeshComplexity(mesh.GetSurfaceCount()/vertex sum) → mesh or Box()
```

**Logical id = zip-relative path.** A custom unit's `MeshPath` referencing `assets/heavy_tank.glb` (a non-`res://` value) is the registry key; `res://` paths keep the editor `GD.Load` path. This is the minimal seam that lets a downloaded unit render its bundled mesh without touching sim/definition schemas.

## Verification

**Commands:**
- `dotnet build godot/godot.csproj` — expected: compiles clean; determinism analyzer green (new `AssetValidator` is Godot-free, adds no `float`/`System.Random`/tick-path `DateTime`); `DependencyHygieneTests` still see NakamaClient 3.13.0 as the sole dep.
- `dotnet test godot/ProjectChimera.Sim.Tests` — expected: all pass incl. new `ContentPackagerAssetHashTests` + `AssetValidatorTests`; every pre-existing golden byte-identical; `CanonicalModelHash.AlgoVersion`(14)/`SimChecksum.AlgoVersion`(21)/`PROTOCOL_VERSION`(2) unchanged.
- `dotnet test godot/ProjectChimera.Sim.Tests --filter "FullyQualifiedName~Golden|SimChecksumCoverageGuard|VersionStampConsistency"` — expected: goldens unchanged (moved golden = Block-If).

**Manual checks (Godot-side / live-verify — NOT Tier-1, documented for the live-verify pass):**
- In a non-editor/exported build: subscribe+download a package bundling a custom `.glb` → confirm it unpacks (no integrity error), then Load Map and confirm the unit AND a custom building render the bundled mesh (not a box), and a corrupt/oversized/disallowed copy is rejected on download with the located reason.
- Feed a malformed/oversized `.glb` → confirm the box placeholder renders and the game does not crash.


## Auto Run Result

Status: done (follow-up review pass on an already-`done` spec; `review_loop_iteration` 0)

### Summary of implemented change
Story 9-9 adds package-integrity verification for bundled custom assets plus a runtime GLB ingest path. `ContentPackager` folds bundled `assets/` bytes into a parallel FNV-1a `AssetHash` (sibling of `ScenarioHash`/`TerrainHash`), verified unconditionally in `Unpack` (extension + size gated before extraction, located `InvalidDataException` on tamper/missing/disallowed/oversized). A net-new `RuntimeAssetIngest` uses `GltfDocument.AppendFromFile → GenerateScene` (sidecar-free, works in exported builds) behind the Godot-free `AssetValidator` (allow-list + size/vertex/surface caps), registering valid meshes in a net-new `AssetRegistry`; every failure mode falls back to the shared box placeholder and never throws. Ingest runs on the load-to-play path (`FactionVisualsPhase.IngestImportedAssets`) into the post-`ReloadCurrentScene` registry the bridges read; the download handler is verify-only. `MeshLoader`'s registry-aware overload is threaded through both unit (`MultiMeshBridge`) and building (`BuildingBridge`) render paths. No algo/protocol version bumped; no golden moved.

### Files changed (reviewed diff, baseline `8b5b9a2` → `beac9cb`; committed prior to this follow-up pass)
- `godot/src/Core/Definitions/ContentPackager.cs` — Pack bundles `assets/` + folds `AssetHash`; Unpack verifies + extension/size-gates asset entries.
- `godot/src/Core/Definitions/ContentPackageManifest.cs` — `AssetFiles` list + `AssetHash` fields.
- `godot/src/Core/Definitions/AssetValidator.cs` — net-new Godot-free allow-list + size/vertex/surface cap decision rules.
- `godot/src/UI/RuntimeAssetIngest.cs` — net-new `GltfDocument` ingest + post-generation complexity gate + placeholder fallback.
- `godot/src/UI/AssetRegistry.cs` — net-new logical-path→Mesh registry + `IngestPackage`.
- `godot/src/UI/MeshLoader.cs` — registry-aware `LoadFromGlb` overload.
- `godot/src/UI/MultiMeshBridge.cs`, `godot/src/UI/BuildingBridge.cs` — thread `AssetRegistry` into unit/building mesh resolution.
- `godot/src/UI/ContentBrowserPanel.cs` — download-time verify-only `Unpack` wiring (`OnDownloadComplete`).
- `godot/src/Core/Bootstrap/Phases/FactionVisualsPhase.cs`, `ContentBrowserPhase.cs`, `SceneContext.cs` — load-path ingest into the rebuilt registry.
- `godot/ProjectChimera.Sim.Tests/Definitions/ContentPackagerAssetHashTests.cs`, `AssetValidatorTests.cs` — net-new Tier-1 tests.

### Review findings breakdown (this follow-up pass)
- Patches applied: 0.
- Deferred: 4 (all `medium`), appended to `deferred-work.md` as NEW entries — Pack extension/size validation asymmetry; rejected-download file persisting in `user://packages/`; directory-enumeration ingest of stale/orphan meshes from an uncleared same-`Id` import dir; silent no-diagnostic box fallback on a `MeshPath`/logical-id mismatch (incl. Windows case-sensitivity).
- Rejected: 11 — the four verification-gap findings + AssetRegistry skip-branch (presentation seam is the accepted inspection/live-verify boundary per intent, sibling 9.3-9.8); intent-alignment observations (descriptive, confirm faithful goal-reading implementation); "Downloaded ✓" label wording (intent de-scopes anti-cheat); unconfirmed threading concern; duplicate-leaf-at-Unpack (unreachable via honest Pack); post-generation cap allocation + no-early-out + re-decode-per-reload perf (adversarial input de-scoped / EA-acceptable); asset-count + `entry.Length`-trust resource bounds (same hostile-actor scope the iteration-0 pass already rejected).

### Follow-up review recommendation
`false`. Computed from this pass's patched findings only: 0 patches (0 high, 0 medium, 0 low) → score `3×0 + 1×0 = 0` (< 5) and no high patch → `false`. The follow-up pass surfaced only deferrable hardening/robustness gaps and out-of-scope noise; nothing required a code fix, so the review loop has converged.

### Verification performed
No code changed in this follow-up pass (0 patches), so the reviewed diff is byte-identical to the commit at which the story was marked `done`; the prior pass's verification (clean `dotnet build`, Tier-1 `ProjectChimera.Sim.Tests` green incl. the new `ContentPackagerAssetHashTests` + `AssetValidatorTests`, all goldens byte-identical, algo/protocol versions unchanged) still holds unchanged. This pass added targeted inspection of the substantive findings against source: confirmed Pack (`ContentPackager.cs` :144-156) guards only duplicate leaf names while Unpack (:390-397) also gates extension + size; confirmed `OnDownloadComplete` (`ContentBrowserPanel.cs` :852-896) cleans only the `package_cache/` verify dir in `finally` and leaves the raw download in `user://packages/`; confirmed Pack maps every asset flat to `assets/{leaf}` (so the duplicate-leaf-at-Unpack finding is unreachable via honest Pack).

### Residual risks
The story's defining behavior — sidecar-free `GltfDocument` GLB ingest, `AssetRegistry` resolution, and render wiring — lives entirely at the presentation seam and is verified by inspection + re-implemented mirrors + documented live-verify only (the accepted 9.3-9.8 boundary); no automated test exercises it, so a regression there would surface only in the live-verify pass. The four deferred hardening gaps remain open in the ledger for later focused attention. `sprint-status.yaml` was modified in the working tree before this run began (orchestrator bookkeeping) and is left untouched — see residual artifacts below.
