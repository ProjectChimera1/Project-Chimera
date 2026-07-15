---
title: 'Remediation DW-106: FactionValidator resolves signature + hero descriptor ids'
type: 'bugfix'
created: '2026-07-15'
status: 'done'
baseline_revision: '766ca42b4f5bf4b38128f5dc39861a85733d4fe0'
final_revision: 'b1faabb9fc7508e60997fe7bd1ce2f77ba11ced9'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-14-context.md'
warnings:
  - oversized
---

<intent-contract>

## Intent

**Problem:** `FactionValidator` never cross-checks a faction's two descriptor-reference fields. `signature_mechanic_effect_id` (a D1 effect/ability id) is never resolved against the `AbilityRegistry`, and `hero_unit_id` is never resolved against the faction's own `Units` roster. This is the systemic gap DW-106 names: it is exactly the class of hole that let alpha's `signature_mechanic_effect_id` silently drift to a string matching no real ability until Story 5.4 caught it by hand — and today the same dangling-id can be authored (notably via the wizard's Advanced raw-JSON pane) with no located error from any validator gate.

**Approach:** Add two roster-completeness checks to `FactionValidator.ValidateComplete` (never the lenient `Validate`/`LoadFromFile` path): (1) a `hero_unit_id` check that resolves a non-empty `HeroUnitId` against `def.Units`' ids; (2) a `signature_mechanic_effect_id` check that, **when given an `AbilityRegistry`**, resolves a non-empty `SignatureMechanicEffectId` against it. `ValidateComplete` gains an optional `AbilityRegistry? abilityRegistry = null` parameter — the signature check runs only when a registry is supplied, so every existing no-registry caller keeps compiling and behaving unchanged while the hero check becomes effective everywhere. Thread the registry through the wizard save-gate (`TryFinish`/`TryFinishFromRawJson` + the `FactionDefinerPanel` edge) so the descriptor check actually fires at the authoring surface DW-106 points to. Add matching `StepForError` routing for both new field paths so a located error surfaces in a real wizard step.

## Boundaries & Constraints

**Always:**
- New id-resolution checks live in `ValidateComplete` only. `Validate` (and therefore `FactionDefinition.LoadFromFile`, the Building/Unit Card Editors' lenient Save self-check) MUST stay registry-free and unchanged — wiring these checks into the lenient path resurrects a prior editor regression (epic-14 technical decision; supersedes DW-106's looser "Validate/ValidateComplete" wording).
- Both checks fire only for a **non-empty** field (`string.IsNullOrWhiteSpace` == false). A null/empty `hero_unit_id` or `signature_mechanic_effect_id` is a legitimate unauthored-descriptor state and must pass — these fields are optional (defaults `null`).
- The signature check runs only when `abilityRegistry != null`; a null registry skips it (cannot resolve without one). Resolution uses `AbilityRegistry.IndexOf(id) >= 0`.
- Every new error is a located, list-all error (append to the same `errors` list, never first-fail) naming the faction id, the field, and the dangling id — matching the existing `Located(...)` idiom. `ValidateComplete` stays pure: no throw, no logging, no `using Godot`, no `float` math.
- `ValidateComplete`'s new optional parameter must be backward-source-compatible: all current callers (`FactionDefinition.LoadSelectableFromDirectory`, `ScenarioLoadPhase`, tests) compile untouched.
- The wizard save-gate keeps its existing `ClearStaleHeroReference(def)` call ahead of `ValidateComplete` unchanged; the new hero check is the systemic net for callers that do NOT pre-clear (discovery/load), not a replacement for the wizard's own repair.

**Block If:**
- (none — the closure is fully specified by DW-106 + the epic-14 technical decision; no unattended design decision is required.)

**Never:**
- Do not change `FactionValidator.Validate`, `FactionDefinition.LoadFromFile`, or `ClearStaleHeroReference`'s silent-clear behavior.
- Do not wire the launch/skirmish gate or `ScenarioLoadPhase` to pass a registry — that is Story 14.4's scope (launch gate) and the MP-critical load path; leave both calling `ValidateComplete(def)` with no registry.
- Do not add validation for `starting_ore`/`starting_crystal` (DW-115) or a duplicate buildings/research id check (DW-111) — separate DW items.
- Do not change any sim value; no golden checksum is affected and none may be re-baselined.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Hero resolves | `hero_unit_id` names a unit present in `Units` | `ValidateComplete` Ok (for this axis) | No error expected |
| Hero dangling | `hero_unit_id` = `"ghost"`, no such unit id in `Units` | Located `hero_unit_id` error naming faction + `"ghost"`; result not Ok | Located error, never throws |
| Hero unauthored | `hero_unit_id` null/empty/whitespace | Pass (optional) | No error |
| Signature resolves | registry supplied, `signature_mechanic_effect_id` = a loaded ability id | Ok (for this axis) | No error |
| Signature dangling | registry supplied, `signature_mechanic_effect_id` = `"no_such_effect"` | Located `signature_mechanic_effect_id` error naming faction + dangling id; not Ok | Located error, never throws |
| Signature, no registry | `abilityRegistry` null, any `signature_mechanic_effect_id` | Signature check skipped; existing outcome unchanged | No error from this check |
| Signature unauthored | registry supplied, field null/empty | Pass (optional) | No error |
| Wizard raw-JSON dangling signature | Advanced pane JSON with a dangling `signature_mechanic_effect_id`, registry threaded | `TryFinishFromRawJson` returns `!Ok`, located `signature_mechanic_effect_id` error, no file written | Located `Failure` |
| Shipped alpha/beta | loaded via discovery (no registry) | Unchanged — both still ValidateComplete-Ok (neither authors `hero_unit_id`) | No error |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/FactionValidator.cs` -- `ValidateComplete` (lines ~193-253): add the optional `AbilityRegistry? abilityRegistry = null` param and the two new located checks after the required-roles block, before the final return. `Located` idiom + the `def.Units ?? new List<>()` null-guard style are reused.
- `godot/src/Core/Definitions/AbilityRegistry.cs` -- `IndexOf(string id)` returns the ability index or `-1` (read-only reference); the resolution primitive for the signature check.
- `godot/src/Core/Definitions/FactionDefinition.cs` -- `SignatureMechanicEffectId` (`signature_mechanic_effect_id`, `string?` null) and `HeroUnitId` (`hero_unit_id`, `string?` null) field definitions; `LoadSelectableFromDirectory` (line ~318) calls `ValidateComplete(def)` and must keep compiling unchanged (no registry). Read-only reference.
- `godot/src/Core/Definitions/FactionDefinerWizardCore.cs` -- `TryFinish` (line ~282) and `TryFinishFromRawJson` (line ~200-266) gain the optional `AbilityRegistry? abilityRegistry = null` param, forwarded into `ValidateComplete(def, abilityRegistry)` / `TryFinish`. `StepForError` (line ~162) gains `hero_unit_id` and `signature_mechanic_effect_id` cases.
- `godot/src/CreationSuite/FactionDefinerPanel.Steps.cs` -- the Finish handler (lines ~302-305) globalizes `res://resources/data/abilities`, loads a registry via `AbilityRegistry.LoadFromDirectory`, and passes it to both `TryFinish`/`TryFinishFromRawJson` calls. `res://` abilities-dir constant convention mirrors `MainScene.ABILITIES_DIR` and the existing `FACTIONS_DIR_RES` globalize on line 39/302.
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionValidatorTests.cs` -- add the new ValidateComplete resolution tests here (alongside the existing mesh_path/required-role ValidateComplete rows and the non-empty-string `signature_mechanic_effect_id` assertions at lines 329/339 that DW-106 flagged as insufficient).
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionDefinerWizardTests.cs` -- add the wizard-gate registry test + the `StepForError` routing assertions.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Definitions/FactionValidator.cs` -- Add `AbilityRegistry? abilityRegistry = null` to `ValidateComplete`'s signature. After the required-roles block, before the final return, append: (a) hero check — when `!string.IsNullOrWhiteSpace(def.HeroUnitId)` and `def.Units` is non-null and no non-null unit has `Id == def.HeroUnitId`, add `("hero_unit_id", Located(id, "hero_unit_id", $"names unit '{def.HeroUnitId}' which is not in this faction's roster."))`; (b) signature check — when `abilityRegistry != null` and `!string.IsNullOrWhiteSpace(def.SignatureMechanicEffectId)` and `abilityRegistry.IndexOf(def.SignatureMechanicEffectId) < 0`, add `("signature_mechanic_effect_id", Located(id, "signature_mechanic_effect_id", $"'{def.SignatureMechanicEffectId}' does not resolve to any loaded ability."))`. Update the method's XML doc to record the two checks, the optional-registry semantics, and why they live in `ValidateComplete` not `Validate`. -- Closes DW-106 at the validator surface.
- `godot/src/Core/Definitions/FactionDefinerWizardCore.cs` -- Add the optional `AbilityRegistry? abilityRegistry = null` param to `TryFinish` and `TryFinishFromRawJson`, forwarding to `ValidateComplete(def, abilityRegistry)` / `TryFinish(parsed, factionsDirAbsolute, abilityRegistry)`. Add `StepForError` cases: `case "hero_unit_id": return FactionDefinerStep.Roster;` (a hero is a roster unit — the roster step is the remedy) and `case "signature_mechanic_effect_id": return FactionDefinerStep.AiPreset;` (not editable in any Simple step; a defensible faction-config-level default, per DW-114's routing note). -- Makes the descriptor check reach the wizard save-gate and routes its located errors to a real step.
- `godot/src/CreationSuite/FactionDefinerPanel.Steps.cs` -- In the Finish handler, add an abilities-dir `res://` constant (mirroring `FACTIONS_DIR_RES`), `ProjectSettings.GlobalizePath` it, `AbilityRegistry.LoadFromDirectory(...)`, and pass the registry into both `TryFinishFromRawJson` and `TryFinish` calls. -- Threads a real registry into the save-gate so a dangling `signature_mechanic_effect_id` (notably from the Advanced raw-JSON pane) is blocked in the running editor.
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionValidatorTests.cs` -- Add ValidateComplete tests covering every I/O-Matrix validator row: hero resolves / hero dangling (located `hero_unit_id` error) / hero unauthored; signature resolves (with an in-memory `AbilityRegistry` built from a known ability id) / signature dangling (located `signature_mechanic_effect_id` error) / signature-with-null-registry-skipped / signature unauthored; and a row proving alpha & beta still pass `ValidateComplete` (no-registry) after the change. -- Locks each new branch and guards the shipped factions against regression.
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionDefinerWizardTests.cs` -- Add a test: `TryFinishFromRawJson` (or `TryFinish`) with a registry lacking the authored `signature_mechanic_effect_id` returns `!Ok`, contains a located `signature_mechanic_effect_id` error, `Step == FactionDefinerStep.AiPreset`, and writes no file; plus a `StepForError` assertion for `hero_unit_id` → `Roster`. -- Covers the wizard-gate wiring and step routing.

**Acceptance Criteria:**
- Given a faction whose `hero_unit_id` names no unit in its `Units`, when `FactionValidator.ValidateComplete` runs (with or without a registry), then the result is not Ok and carries a located `hero_unit_id` error naming the faction and the dangling id.
- Given a faction with a non-empty `signature_mechanic_effect_id` and an `AbilityRegistry` that does not contain it, when `ValidateComplete(def, registry)` runs, then the result is not Ok with a located `signature_mechanic_effect_id` error; given the same faction and `ValidateComplete(def)` (no registry), then the signature check does not fire and the prior outcome is unchanged.
- Given the shipped `alpha`/`beta` factions loaded through `LoadSelectableFromDirectory` (no registry), when discovery runs, then both still pass `ValidateComplete` exactly as before (neither authors `hero_unit_id`; the signature check is skipped without a registry).
- Given the Faction Definer Advanced raw-JSON pane with a dangling `signature_mechanic_effect_id`, when Finish runs with the abilities registry threaded, then the save is blocked with a located `signature_mechanic_effect_id` error routed to the AI Preset step and no faction file is written.
- Given `dotnet build godot/godot.sln` and the full `ProjectChimera.Sim.Tests` suite, when run, then the build succeeds with no new warnings and all tests pass (no golden checksum re-baseline occurs).

## Design Notes

Two-method contract (why `ValidateComplete` only): `Validate` runs on every `FactionDefinition.LoadFromFile` — including the lenient editor Save self-check — and has no registry; putting a registry-dependent or roster-completeness check there would break that path and re-open the editor regression the split was created to prevent. DW-106's original "Validate/ValidateComplete" wording predates the epic-14 refinement; the epic technical decision (ValidateComplete-only, pass the registry in) is authoritative and is followed here.

Optional-registry, not an overload: a single optional parameter keeps `LoadSelectableFromDirectory`/`ScenarioLoadPhase`/tests source-compatible and makes the hero check (registry-independent) effective at every `ValidateComplete` site immediately, while the signature check activates precisely where a registry is threaded (the wizard save-gate this story wires). A null registry deliberately skips the signature check rather than failing closed — resolution is impossible without the registry, and the launch-gate wiring that would guarantee a registry everywhere is Story 14.4.

Hero check vs. the wizard's `ClearStaleHeroReference`: the wizard silently nulls a dangling `HeroUnitId` before calling `ValidateComplete`, so at the wizard save-gate a dangling hero is repaired, not reported (Story 5.6 behavior, unchanged). The new validator check is the systemic net for the paths that do NOT pre-clear — discovery (`LoadSelectableFromDirectory`) and match-load (`ScenarioLoadPhase`) — where a hand-authored dangling `hero_unit_id` would otherwise pass unnoticed.

Sketch (inside `ValidateComplete`, before the final return):
```csharp
if (!string.IsNullOrWhiteSpace(def.HeroUnitId) && def.Units != null
    && !def.Units.Any(u => u != null && u.Id == def.HeroUnitId))
    errors.Add(("hero_unit_id", Located(id, "hero_unit_id",
        $"names unit '{def.HeroUnitId}' which is not in this faction's roster.")));

if (abilityRegistry != null && !string.IsNullOrWhiteSpace(def.SignatureMechanicEffectId)
    && abilityRegistry.IndexOf(def.SignatureMechanicEffectId) < 0)
    errors.Add(("signature_mechanic_effect_id", Located(id, "signature_mechanic_effect_id",
        $"'{def.SignatureMechanicEffectId}' does not resolve to any loaded ability.")));
```

This story also partially closes DW-114 (adds the `StepForError` cases for `signature_mechanic_effect_id`/`hero_unit_id`); `starting_ore`/`starting_crystal` routing remains DW-114/DW-115 territory.

## Verification

**Commands:**
- `dotnet build godot/godot.sln` -- expected: build succeeds, no new warnings (the `FactionDefinerPanel.Steps.cs` edge compiles against the new optional params).
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj --filter "FullyQualifiedName~FactionValidatorTests|FullyQualifiedName~FactionDefinerWizardTests"` -- expected: all pass, including the new hero/signature resolution tests and the alpha/beta no-regression row.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: full suite green (golden checksum tests unchanged — this story alters no sim value).

## Spec Change Log

_No bad_spec loopback occurred. All review findings were resolved as patches, one defer, or rejects — the code was not re-derived._

## Review Triage Log

### 2026-07-15 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 3: (high 0, medium 1, low 2)
- defer: 1
- reject: 8
- addressed_findings:
  - `[medium]` `[patch]` Missing real-content regression guard (Blind Hunter + Verification Gap, converging). The story's origin defect — a SHIPPED faction's `signature_mechanic_effect_id` drifting to a value matching no real ability — had no guard: the alpha/beta regression test passes NO registry and so proves nothing about resolution, and every wizard/validator test injects a hand-built registry, never exercising the real abilities directory. Added `AlphaAndBeta_SignatureEffectIds_ResolveAgainstRealRegistry_ValidateComplete_IsOk` + a `ResolveAbilitiesDir` helper: loads the REAL `resources/data/abilities/` registry, asserts it is non-empty, asserts alpha's `spike_transmutation` / beta's `furnace_trickle` resolve via `ValidateComplete(def, realRegistry).Ok`, and that a dangling id on the real faction is blocked. Also guards the Edge-Case-Hunter "empty registry over-blocks" assumption (non-empty assertion pins the real dir resolves).
  - `[low]` `[patch]` Resolving-writes-file wizard test asserted only file-existence (Blind Hunter). `SerializeDraftClean` emits `signature_mechanic_effect_id` only when non-empty, so a drop-on-write regression would pass. Added a reload + `SignatureMechanicEffectId == "real_effect"` round-trip assertion.
  - `[low]` `[patch]` `ValidateComplete` XML doc overclaimed the hero check is "effective at EVERY site immediately" (Blind Hunter) — at the wizard save-gate `ClearStaleHeroReference` pre-nulls a dangling `hero_unit_id` before the check runs, so the located hero error surfaces only at the non-wizard sites. Carved that out in the doc, and added a note that id resolution is intentionally ordinal/case-sensitive (unit/ability ids are exact reference keys), distinct from the deliberately case-insensitive `ai_preset`/`Category` closed-set checks (Blind Hunter case-sensitivity finding).
- rejected/deferred (not this story's problem, on the authority noted):
  - `[defer]` The new wizard gate surfaces DW-107's silent-skip-on-invalid-ability as a misleading "does not resolve" faction error (Edge Case Hunter + Blind Hunter). Root cause is DW-107's `LoadFromDirectory` silent skip; the Panel edge is Godot-presentation (not headlessly verifiable). Logged to `deferred-work.md`.
  - Signature check not enforced at discovery/match-load (Blind Hunter + Intent Alignment + Verification Gap, deduped) — match-load registry wiring is explicitly Story 14.4 (the launch gate) per the epic-14 context; discovery is not named by DW-106, whose closure targets the wizard save-gate. Out of scope on intent authority, not the spec's.
  - Signature error routes to the AiPreset step, which cannot edit the field (Blind Hunter) — cosmetic; a defensible default and a documented DW-114 limitation (no wizard step exposes these fields); acknowledged in code.
  - Registry reloaded from disk per Finish press (Blind Hunter) — infrequent editor authoring action; mirrors the established `ScanPresets` reload pattern.
  - Fail-open null-registry posture / title "oversells" systemic closure (Blind Hunter) — the wizard-surface enforcement + deferred launch gate (14.4) is the intended epic-14 split; descriptive.
  - Dangling-signature test's `Step == AiPreset` assertion is incidental (Blind Hunter) — the primary `Errors.Contains(signature error)` assertion carries the weight.
  - No both-descriptors-dangling ordering test (Blind Hunter) — low value; each check is independently pinned.
  - Duplicated `ABILITIES_DIR_RES` constant vs `MainScene.ABILITIES_DIR` (Blind Hunter) — values match today; `MainScene`'s is `private`, so a shared reference would require widening its visibility for marginal gain.
  - Empty-registry over-block when the abilities dir is missing/empty (Edge Case Hunter) — a degraded environment (the abilities dir is a hard whole-game dependency); a Count==0→null fallback would reintroduce a silent-skip risk of its own.

## Auto Run Result

Status: done

### Summary
Closed DW-106 at the `FactionValidator` surface: `ValidateComplete` now resolves both faction descriptor-reference fields — `hero_unit_id` against the faction's own roster (registry-independent, effective at every non-wizard `ValidateComplete` site) and `signature_mechanic_effect_id` against a supplied `AbilityRegistry` (registry-gated via a new optional parameter). The wizard save-gate (`TryFinish`/`TryFinishFromRawJson` + the `FactionDefinerPanel` edge) threads a real registry so a dangling signature id — notably from the Advanced raw-JSON pane — is blocked with a located, step-routed error. `StepForError` gained matching `hero_unit_id → Roster` and `signature_mechanic_effect_id → AiPreset` cases (partially closing DW-114). All existing no-registry callers compile and behave unchanged; the launch-gate registry wiring remains Story 14.4.

### Files changed
- `godot/src/Core/Definitions/FactionValidator.cs` — `ValidateComplete` gains `AbilityRegistry? abilityRegistry = null` and the two located, list-all descriptor-resolution checks; XML doc records the checks, optional-registry semantics, the wizard pre-clear carve-out, the intentional ordinal case-sensitivity, and the ValidateComplete-only rationale. `using System.Linq;` added.
- `godot/src/Core/Definitions/FactionDefinerWizardCore.cs` — optional `AbilityRegistry?` threaded through `TryFinish`/`TryFinishFromRawJson`; `StepForError` cases for the two new field paths.
- `godot/src/CreationSuite/FactionDefinerPanel.cs` — `ABILITIES_DIR_RES` constant (mirrors `MainScene.ABILITIES_DIR`).
- `godot/src/CreationSuite/FactionDefinerPanel.Steps.cs` — Finish handler globalizes the abilities dir, loads the registry, passes it into both finish calls.
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionValidatorTests.cs` — full I/O-matrix coverage of both checks (hero/signature: resolves / dangling / no-registry-skipped / unauthored), an alpha/beta no-registry no-regression row, and (added in review) a REAL-abilities-registry regression guard proving shipped signature ids resolve and a dangling one is blocked + `ResolveAbilitiesDir` helper.
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionDefinerWizardTests.cs` — `StepForError` `InlineData` rows for both field paths; wizard-gate dangling-signature (blocks at AI Preset step, no file) and resolving (writes file, + round-trip assertion added in review) tests.

### Review findings
- Reviewers: Blind Hunter, Edge Case Hunter, Verification Gap, Intent Alignment (all at session model capability).
- Patches applied (3): 1 medium (real-content regression guard — the exact DW-106 origin class was unguarded), 2 low (write round-trip assertion; doc precision + case-sensitivity note).
- Deferred (1): DW-107 silent-skip surfaced as a misleading faction error through the new gate (logged to `deferred-work.md`).
- Rejected (8): signature-not-enforced-at-load (Story 14.4 / intent authority), cosmetic step routing, per-finish registry reload, fail-open posture, incidental Step assertion, both-dangling ordering test, duplicated dir constant, degraded-env empty-registry over-block.

### Verification
- `dotnet build godot/godot.sln` — succeeded, 0 errors (11 warnings, all pre-existing in untouched files).
- `dotnet test …FactionValidatorTests|FactionDefinerWizardTests` — 94/94 passed.
- `dotnet test` (full Sim.Tests suite) — 1768 passed, 1 skipped (pre-existing reserved test), 0 failed. No golden checksum re-baseline — the change alters no sim value.
- Matrix Test Audit: every I/O-matrix row is covered by a test that ran and passed.

### Residual risks
- The signature check is effective only where a registry is threaded (the wizard save-gate today); discovery and match-load still skip it by design — the launch-gate wiring is Story 14.4. The `hero_unit_id` check is effective at those non-wizard sites now.
- The `FactionDefinerPanel` edge (abilities-dir globalize + `LoadFromDirectory` + registry pass) is Godot-presentation and verified by compile only, not by a headless runtime test; the meaningful gate logic (`TryFinish`/`ValidateComplete` forwarding and resolution) is Tier-1 unit-tested, and the real-abilities-dir regression test pins the resolution path against shipped content.
