---
title: 'Remediation (14.5): editor map-save must not write a default persistence_manifest — absent round-trips absent'
type: 'bugfix'
created: '2026-07-15'
status: 'done'
baseline_revision: 'f28dbafbecf0b9dfab202df36b245db104ef1117'
final_revision: 'c751b456bbc68e5a1d8ffcf43505c91e025e792a'
review_loop_iteration: 1
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-14-context.md'
warnings: [oversized]
---

<intent-contract>

## Intent

**Problem:** A routine editor save must never opt a manifest-less map into hero persistence: a scenario whose JSON has no `persistence_manifest` must round-trip through the editor save path as still having none, and an authored manifest must round-trip unchanged. At the Epic-6 retro (2026-07-15) a shared-`ScenarioData` contamination left a full `enabled:true` manifest on `alpha_map_01.json`, the 2026-07-13 AutoSave cron committed it (a0c8d51), and `PersistenceManifestTests` stayed red for the whole Epic-6 run — the existing guard only watched `alpha_map_01.json`, so nothing netted the other shipped maps and nothing pinned the contract at the actual save-path level.

**Approach:** Investigation (see Design Notes) confirms the serializer already honors the contract — `ScenarioData.PersistenceManifest` is `[JsonIgnore(WhenWritingNull)]` since Story 3.8, every map-save path passes the live model straight through it, and `enabled:true` is only ever produced by the explicit `PersistenceManifestEditing.ApplyMasterToggle`/`ApplyAttributeToggle` toggles. So this is a hardening story, not a serializer bugfix: broaden the shipped-scenario guard from one map to **every** shipped scenario, add an explicit editor-map-save-path round-trip teeth-test, and document the absent-stays-absent contract at the narrowed write site so the class cannot silently recur.

## Boundaries & Constraints

**Always:**
- A scenario loaded from JSON that has no `persistence_manifest` key serializes back with no `persistence_manifest` key (absent round-trips absent), exercised through the real `ScenarioSerializer.SaveToFile` → `LoadFromFile` path a routine editor save uses.
- An authored manifest (present, `enabled` either value, any eligible attributes) round-trips unchanged through that same save/load path.
- The all-shipped guard asserts the manifest-presence of the **serialized** form equals the manifest-presence of the **on-disk** form for every shipped scenario — so it stays correct if a demo map ever legitimately authors persistence.
- New/changed tests live in the Tier-1 `ProjectChimera.Sim.Tests` project (Godot-free, xUnit) and are RED-teeth-proven (Verification demonstrates the net catches a deliberately-injected default manifest before it is reverted).

**Block If:**
- Investigation surfaces a real code path that materialises a non-null `PersistenceManifest` (or writes `enabled:true`) onto a scenario WITHOUT an explicit persistence-authoring action (master switch / checklist toggle) — that would be a live write-path defect beyond hardening; HALT with status `blocked` and the reproduction as the blocking condition.

**Never:**
- Do not change `ScenarioSerializer.Serialize`/`SaveToFile` serialization behavior, the `[JsonIgnore(WhenWritingNull)]` attribute, or the `PersistenceManifest.Enabled` default — the round-trip is already correct; only add documentation comments.
- Do not add a serializer-side guard that strips or rewrites an authored manifest (it would corrupt deliberate persistence authoring — the inverse regression).
- Do not touch sim/checksum/golden code — the manifest is authoring-only and folded into no hash (D-2); no golden re-baseline.
- Do not repair or edit any shipped scenario JSON (the retro already repaired `alpha_map_01.json`).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Manifest-less map, routine save | Shipped map with no `persistence_manifest`; a non-manifest field mutated (e.g. `DisplayName`) then `SaveToFile`→`LoadFromFile` | Written JSON has no `persistence_manifest` key; the mutated field persisted | No error expected |
| Authored manifest, save | Scenario with `persistence_manifest {enabled:true, [hero.level,hero.xp]}` saved+loaded | Manifest round-trips unchanged (enabled + same attribute list) | No error expected |
| All shipped scenarios | Every `*.json` in `resources/data/scenarios/` that loads | Serialized manifest-presence == on-disk manifest-presence (today: none carry one → none written) | Files that fail to load are skipped, not failed |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/ScenarioData.cs:471` -- the `PersistenceManifest` property; already `[JsonIgnore(WhenWritingNull)]` (null ⇒ omitted). Read-only reference; do not change.
- `godot/src/Core/Definitions/ScenarioSerializer.cs` -- `Serialize`/`SaveToFile`/`LoadFromFile`, the save/load contract the tests pin. Add a brief contract-pointer comment only.
- `godot/src/CreationSuite/PersistenceManifestPanel.cs:299-343` (`OnSavePressed`, write site at :331) -- the retro's narrowed suspect; writes the entire shared scenario. Add write-site documentation.
- `godot/src/Core/Definitions/PersistenceManifestEditing.cs` -- the only producers of `enabled:true` (`ApplyMasterToggle`/`ApplyAttributeToggle`); confirms AC2's "explicit action only". Reference only.
- `godot/ProjectChimera.Sim.Tests/Definitions/PersistenceManifestTests.cs` -- has the single-map guard `ShippedScenario_...SerializesWithoutManifest` (:253, `Assert.DoesNotContain` after load+serialize — **the exact shape to generalize to all maps**), `PresentManifest_RoundTripsThroughScenarioSerializerSaveAndLoad` (:369, in-memory authored fidelity), and `ScenariosDir()`/`LoadShipped()` helpers (:392-411). Add the new tests here. Use `System.Text.Json.JsonDocument` for the structural on-disk key check.
- `godot/resources/data/scenarios/` -- shipped scenarios (alpha_map_01, map_02..map_12, 123.json, my-new-map.json); none currently carry a manifest.

## Tasks & Acceptance

**Execution:**
- `godot/ProjectChimera.Sim.Tests/Definitions/PersistenceManifestTests.cs` -- add **(a)** an **all-shipped absolute-absence guard** (generalize the existing single-map `ShippedScenario_...SerializesWithoutManifest`): enumerate every `*.json` in `ScenariosDir()`; for each, `LoadFromFile` and increment a `checked` counter; determine on-disk manifest presence with a **structural key check** (`JsonDocument.Parse(File.ReadAllText(path)).RootElement.TryGetProperty("persistence_manifest", out var el) && el.ValueKind != JsonValueKind.Null`), never a raw `.Contains(...)` substring; for every map NOT in a `PersistenceOptInMaps` whitelist (**empty today** — the D3 shipped-without-persistence contract), assert BOTH `s.PersistenceManifest == null` AND `Serialize(s)` has no `persistence_manifest` key (**absolute absence** — a manifest committed to disk on ANY shipped map, the real a0c8d51 vector, turns this RED); for a whitelisted opt-in map, assert on-disk presence round-trips (serialized presence == on-disk presence) AND the deserialized manifest deep-equals the loaded one (`enabled` + `Attributes`, content fidelity not just presence). After the loop assert `checked > 0` (not merely the file list non-empty) so an all-fail-to-load regression cannot pass vacuously. NOTE: `LoadFromFile` THROWS `JsonException` on malformed JSON — it returns null only for a literal `null` document or a missing file — so a broken file fails loudly; do not add a comment claiming failed files are "skipped". And **(b)** an editor-map-save-path round-trip test: `LoadShipped()` a manifest-less map, assert `s.PersistenceManifest == null`, mutate a non-manifest field (e.g. `DisplayName`), `ScenarioSerializer.SaveToFile` to a **randomized** temp path (`Path.GetRandomFileName()`, not a fixed filename), `LoadFromFile` back, assert `back.PersistenceManifest == null` (load-bearing) AND the written bytes contain no `persistence_manifest` (secondary) AND the mutation survived. Keep the existing single-map and authored-round-trip tests (`Serialize_PresentManifest_...`, `PresentManifest_RoundTrips...`) — they cover in-memory authored content fidelity. -- pins absolute absence across the whole shipped set (the coverage the corruption slipped through) at the serializer/save-path surface the intent names.
- `godot/src/CreationSuite/PersistenceManifestPanel.cs` -- add a concise comment at the `OnSavePressed` write site (the `ScenarioSerializer.SaveToFile` call, ~:331) documenting the absent-stays-absent contract: this writes the entire shared `ScenarioData`; a null manifest is omitted by `[JsonIgnore(WhenWritingNull)]`, and `enabled:true` originates only from the explicit master/checklist toggles in this panel (`ApplyMasterToggle`/`ApplyAttributeToggle`) — a routine map-save must never inject a default manifest, and the all-shipped absolute-absence guard is the Tier-1 backstop that would catch any future in-memory default-manifest injection after a save. -- satisfies AC2 "fix documented at the write site" and records the Node-bound-invariant backstop.
- `godot/src/Core/Definitions/ScenarioSerializer.cs` -- add one line in `Serialize` (near the other omit-when-null normalizations) pointing to the persistence-manifest absent-stays-absent contract and its Tier-1 guards. -- makes the serializer-level contract self-documenting.

**Acceptance Criteria:**
- Given every shipped scenario file NOT on the persistence opt-in whitelist (all of them today), when it is loaded and serialized, then its loaded `PersistenceManifest` is null AND the serialized JSON contains no `persistence_manifest` key — so a manifest committed to disk on any such map turns the guard RED.
- Given a shipped scenario ON the opt-in whitelist (none today), when it is loaded and serialized, then the manifest round-trips with identical `enabled` flag and attribute list (content fidelity, not mere key presence).
- Given the all-shipped guard, when zero scenario files successfully load, then the test fails on the `checked > 0` assertion rather than passing vacuously.
- Given a manifest-less shipped map, when it is mutated on a non-manifest field and saved through `ScenarioSerializer.SaveToFile` then reloaded, then the reloaded `PersistenceManifest` is null, the written JSON contains no `persistence_manifest` key, and the mutation persisted.
- Given a scenario that authors a `persistence_manifest`, when it is saved and reloaded, then the manifest (enabled flag + attribute list) round-trips unchanged (existing coverage retained).
- Given the a0c8d51 contaminated `alpha_map_01.json` state (or a manifest injected into any shipped file), when the all-shipped guard runs, then it fails RED (teeth demonstrated against the real incident vector); reverting the contamination returns it to GREEN.
- Given the write site, when the code is read, then the absent-stays-absent contract and the "enabled:true only via explicit action" invariant (with the all-shipped guard named as its backstop) are documented there.

## Spec Change Log

### 2026-07-15 — Review pass 1 (bad_spec)

- **Triggering finding (HIGH, corroborated by 3 of 4 reviewers — blind-hunter, verification-gap, intent-alignment):** the all-shipped guard as specified asserted `serialized-manifest-presence == on-disk-manifest-presence` (round-trip presence-equality). That is a tautology at the incident state: loading the a0c8d51 contaminated `alpha_map_01.json`, re-serializing reproduces the manifest, and `true == true` passes GREEN — so the guard does NOT catch a manifest committed to disk on any shipped map, recreating the single-map blind spot the story exists to close.
- **Amended (outside `<intent-contract>` only):** rewrote Tasks/AC/Code Map/Design Notes/Verification to require **absolute absence** — every shipped map NOT on an (empty) `PersistenceOptInMaps` whitelist must load with `PersistenceManifest == null` and serialize with no key; generalize the existing `ShippedScenario_...SerializesWithoutManifest` (`Assert.DoesNotContain` after load→serialize) across all maps. Folded corroborating findings: structural `JsonDocument` key check instead of raw substring; `checked > 0` assertion to defeat the all-fail-to-load vacuous pass; removed the misleading "failed files are skipped" comment (`LoadFromFile` throws on malformed JSON); randomized temp filename for run isolation; content-fidelity (deep-equal) assertion on the whitelist path; RED-proof re-specified against a real committed-to-disk contamination on a NON-`alpha_map_01` file; documented that the all-shipped guard is the Tier-1 backstop for the Godot-Node-bound "enabled:true only by explicit action" invariant.
- **Known-bad state avoided:** a green regression net that passes at the exact contaminated state it names, giving false confidence while any map but `alpha_map_01` ships contaminated undetected.
- **KEEP (must survive re-derivation):** (1) the manifest-less-map editor-save-path round-trip test `ManifestLessMap_RoutineSave_WritesNoManifest_AndPersistsMutation` — it correctly pins absent-stays-absent through the real `SaveToFile`; (2) the write-site documentation comments at `PersistenceManifestPanel.OnSavePressed` and `ScenarioSerializer.Serialize`; (3) no production behavior change / no serializer edit / no golden re-baseline — the serializer is already correct, this is a net + docs story; (4) keep the existing `Serialize_PresentManifest_...` and `PresentManifest_RoundTrips...` tests as the in-memory authored-fidelity coverage.

## Review Triage Log

### 2026-07-15 — Review pass 1
- intent_gap: 0
- bad_spec: 8: (high 1, medium 2, low 5)
- patch: 0
- defer: 0
- reject: 1: (low 1)
- addressed_findings:
  - `[high]` `[bad_spec]` All-shipped guard was presence-equality (round-trip tautology) — passes GREEN at the a0c8d51 contaminated state and nets no map but `alpha_map_01`; amended to absolute absence generalizing the existing single-map `Assert.DoesNotContain` guard across all maps, with an empty opt-in whitelist for the D3 shipped-without-persistence contract.
  - `[medium]` `[bad_spec]` Guard checked key presence only, not content; amended the whitelist path to deep-equal (`enabled` + `Attributes`) the authored manifest, plus retained the in-memory authored-fidelity tests.
  - `[medium]` `[bad_spec]` All-files-fail-to-load passed vacuously (only the file list was asserted non-empty); amended to assert a `checked > 0` count of successfully-loaded files.
  - `[low]` `[bad_spec]` "failed files are skipped" comment misstated loader behavior (`LoadFromFile` throws on malformed JSON); amendment removes the claim.
  - `[low]` `[bad_spec]` Raw substring `.Contains("persistence_manifest")` conflates payload text with the structural key; amended to a `JsonDocument` key check.
  - `[low]` `[bad_spec]` Fixed temp filename not run-isolated; amended to `Path.GetRandomFileName()`.
  - `[low]` `[bad_spec]` RED-proof not demonstrable from the artifact; amended Verification to prove RED against a real committed-to-disk contamination on a non-`alpha_map_01` file.
  - `[low]` `[bad_spec]` In-memory "enabled:true only by explicit action" invariant is Godot-Node-bound and untestable in Tier-1; documented the all-shipped absolute-absence guard as its backstop (no code fix — the write site is already safe).
  - `[low]` `[reject]` Test-2 precondition `Assert.Null` "fires on the precondition not the behavior" — the precondition documents the fixture state and the generalized guard covers `alpha_map_01` regardless; no change.

### 2026-07-15 — Review pass 2
- intent_gap: 0
- bad_spec: 0
- patch: 1: (low 1)
- defer: 1: (low 1)
- reject: 7: (low 7)
- addressed_findings:
  - `[low]` `[patch]` The whitelist opt-in branch could be silently `continue`-skipped (a whitelisted map reduced to a `null`/missing document) and vacuously pass, since `checkedCount > 0` stays positive from the other maps. Added a `persistenceOptInMaps.IsSubsetOf(checkedFiles)` assertion so every opt-in map must actually load and be checked (no-op today, load-bearing once the whitelist is populated). Filtered `PersistenceManifestTests` green (33/33).

Pass-2 verdict: all three primary reviewers confirmed the corrected all-shipped absolute-absence guard genuinely catches the a0c8d51 committed-to-disk vector on any shipped map (no longer a tautology, prior fix holds). Deferred: the shared-`ScenarioData` instance-aliasing root mechanism and the Godot-Node-bound panel save seam are not pinned by a Tier-1 test (backstop-only by design; logged to the deferred-work ledger, related to DW-10). Rejected (all low): `.chimera.zip` transport-form scan (Import re-materializes to a `.json` the guard catches), the dead-today whitelist branch (independently covered by `PresentManifest_RoundTrips`), the redundant `serializedHasManifest==false` defensive assertion, the scratch-dir-in-scenarios observation, minor dead-work/triple-read, save-path-test overlap, and the "backstop" doc wording (already scoped to "reaches a shipped file through a save").

## Design Notes

Investigation findings (why this is hardening, not a serializer fix):
- `ScenarioData.PersistenceManifest` carries `[JsonIgnore(Condition = WhenWritingNull)]` since Story 3.8 (commit ec25cda) — a null manifest has ALWAYS been omitted. All four save callers (`PersistenceManifestPanel:331`, `MapGeneratorPanel:254`, `WinConditionPhase` CreateNewMap/Export/Import) hand the live model straight to `ScenarioSerializer.SaveToFile`, so a manifest-less scenario already serializes without the key.
- The a0c8d51 incident wrote `enabled:true, [hero.level, hero.xp, hero.inventory]` — a *fully-authored, valid* manifest, not a serializer default. The panel and the map editor share one `ScenarioData`, so a deliberate authoring action against the shipped demo map left the manifest on the shared object, which the AutoSave cron then committed. The existing `ShippedScenario_...SerializesWithoutManifest` guard DID catch it (went red) — but only after the cron committed, and it only watches `alpha_map_01.json`.
- `enabled:true` is produced solely by `PersistenceManifestEditing.ApplyMasterToggle(on:true)` / `ApplyAttributeToggle(selected:true)` — both explicit UI actions. `BuildChecklist` uses `SetSelected` (no signal) and `Refresh` uses `SetOn(..., animate:false)` (no signal), so opening or re-saving the panel never fabricates a manifest. AC2's invariant already holds; the deliverable is the permanent regression net + documentation.

All shipped scenarios were checked: none currently carry a `persistence_manifest`, so the broadened guard is GREEN on landing and RED only under a real regression.

**Why absolute absence, not presence-equality (review pass 1).** The all-shipped guard must assert *absolute absence* for the default shipped set, NOT `serialized-presence == on-disk-presence`. The a0c8d51 incident was a manifest **committed to the file on disk** (an editor re-save + AutoSave commit), not a serializer that injects a key at write time. A presence-equality check loads the contaminated file, re-serializes it (faithfully reproducing the manifest), and sees `true == true` → it passes GREEN at the exact contaminated state it claims to catch, and nets no map but the one the legacy single-map test already pinned — recreating the single-map blind spot this story exists to close. Generalizing the existing `ShippedScenario_...SerializesWithoutManifest` (`Assert.DoesNotContain` after load→serialize) to every map is the correct teeth: loading a contaminated file then serializing reproduces the key, failing the assertion. The `PersistenceOptInMaps` whitelist (empty today, enforcing the D3 shipped-without-persistence contract) is the deliberate, fail-closed gate a future map must be added to *with that authoring decision*; that is where presence round-trips + content-fidelity apply. This keeps the intent-contract's "authored round-trips unchanged" clause satisfied (via the whitelist path) while delivering absolute absence for the shipped set.

**In-memory invariant is Godot-Node-bound.** AC2's "`enabled:true` only by explicit action" is an in-memory-construction invariant. The producers (`ApplyMasterToggle`/`ApplyAttributeToggle`) are Tier-1-tested, but the panel's `Initialize`/`Refresh`/`BuildChecklist` binding is Godot-`Node` code the Tier-1 assembly cannot instantiate, so a future `?? = new PersistenceManifest()` there is not directly Tier-1-testable. The all-shipped absolute-absence guard is the practical backstop: any such injection reaches a shipped file only through a save, and the guard fails RED on that file. This backstop relationship is recorded in the write-site comment.

## Verification

**Commands:**
- `dotnet build godot/godot.sln` -- expected: build succeeds, no new warnings.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj --filter "FullyQualifiedName~PersistenceManifestTests"` -- expected: all tests (existing + new all-shipped guard + new save-path round-trip) pass.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: full Tier-1 suite green; golden/checksum tests unchanged (manifest is folded into no hash).

**RED-teeth proof (do, observe, revert):**
- Reproduce the real incident vector: temporarily hand-add `"persistence_manifest": {"enabled": true, "attributes": ["hero.level"]}` to a shipped scenario file OTHER than `alpha_map_01.json` (e.g. `map_05_crossroads.json`) — confirm the all-shipped absolute-absence guard turns RED and names the offending file (proving it nets every map, not just the legacy one). Then repeat on `alpha_map_01.json` to confirm the generalized guard subsumes the legacy single-map test. Revert both edits and confirm the suite returns GREEN. Record the observed RED (with the failing filename) in the run result. This is the natural RED — the guard fails on a committed-to-disk manifest, exactly the a0c8d51 shape.

## Auto Run Result

Status: done

**Implemented change:** Hardened the editor map-save path against silent `persistence_manifest` injection (Epic-6 retro A2-E6). Investigation confirmed the serializer already honors absent-stays-absent (`[JsonIgnore(WhenWritingNull)]` since Story 3.8) and that `enabled:true` is produced only by the explicit panel toggles — so this is a regression-net + documentation story with no production behavior change, no serializer edit, and no golden re-baseline. The permanent net generalizes the legacy single-map guard to **absolute absence across every shipped scenario**.

**Files changed:**
- `godot/ProjectChimera.Sim.Tests/Definitions/PersistenceManifestTests.cs` — added `AllShippedScenarios_HaveNoManifest_ExceptOptInWhitelist` (absolute-absence guard over every `*.json`, structural `JsonDocument` key checks, empty opt-in whitelist for the D3 contract, `checkedCount > 0` + whitelist-subset anti-vacuous assertions) and `ManifestLessMap_RoutineSave_WritesNoManifest_AndPersistsMutation` (real `SaveToFile`→`LoadFromFile` round-trip, randomized temp path). Existing tests retained.
- `godot/src/CreationSuite/PersistenceManifestPanel.cs` — write-site documentation at the `OnSavePressed` save call recording the absent-stays-absent contract, the enabled:true-only-via-explicit-toggle invariant, and the all-shipped guard as its Tier-1 backstop.
- `godot/src/Core/Definitions/ScenarioSerializer.cs` — pointer comment in `Serialize` documenting the manifest contract and its guards.

**Review findings breakdown:**
- Pass 1: 1 high `bad_spec` (the originally-specified guard was a presence-equality round-trip tautology that passed green at the a0c8d51 contaminated state) → spec amended to absolute absence + 7 folded corroborating refinements; code reverted and re-derived. 1 low rejected.
- Pass 2: 0 intent_gap / 0 bad_spec; corrected guard confirmed load-bearing by all three primary reviewers. 1 low patch applied (whitelist silent-skip assertion), 1 low deferred (shared-`ScenarioData` instance-aliasing / Godot-Node panel save seam not Tier-1-pinnable — logged to deferred-work, related to DW-10), 7 low rejected.

**Verification:**
- `dotnet build godot/godot.sln` — 0 warnings, 0 errors.
- `dotnet test ... --filter PersistenceManifestTests` — 33/33 passed.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` — 1781 passed, 1 skipped, 0 failed.
- RED-teeth proof: injecting `persistence_manifest {enabled:true,...}` into `map_05_crossroads.json` and (separately) `alpha_map_01.json` each turned the all-shipped guard RED naming the offending file; reverting returned GREEN. Confirms the guard catches the real committed-to-disk vector on any shipped map. No injection residue remains (all scenario files clean).

**Residual risks:** The a0c8d51 root mechanism (shared-`ScenarioData` instance aliasing across maps) and the Godot-Node-bound panel save seam are covered by documentation + the post-commit all-shipped backstop, not by a runtime/integration test — deferred (see ledger; related to DW-10). The whitelist opt-in fidelity branch is dead today (empty whitelist by the D3 contract) and gains live coverage only when a future map deliberately opts into persistence.
