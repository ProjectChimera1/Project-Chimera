---
title: 'Land & harden the FMA showcase content as valid Definer outputs (FR-20)'
type: 'feature'
created: '2026-07-10'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false
context: []
warnings: []
baseline_revision: '03ef63cf77471f7a95e996435d269cb83be0d5c0'
final_revision: 'c80e89aed975aeb5a115ea4677394ffd869c4bef'
---

<intent-contract>

## Intent

**Problem:** alpha_faction.json/beta_faction.json carry the FMA-redesigned stats/names/meshes but have never been run through the Story 5.2 `FactionValidator`, and both are missing the 5.2 schema's `ai_preset`/signature-mechanic descriptor fields. Alpha's roster is also missing its Air unit (Greycrest, the Bonded) — an accidental `Bmad-Loop@nextinstall` reinstall commit (`c495454`) overwrote it with two non-FMA junk "Tubby" `Structure` placeholders reusing its mesh.

**Approach:** Restore alpha's `griffin` unit to its pre-regression values (recovered via git history, matching `fma-faction-design.md` exactly) and delete the two junk placeholders; add `ai_preset`/`signature_mechanic_display`/`signature_mechanic_effect_id` to both faction JSONs; extend `FactionValidatorTests` to prove both factions pass `Validate`/`ValidateComplete` and bind the new fields correctly.

## Boundaries & Constraints

**Always:** Data-only — no sim/gameplay code changes; touch only the two faction JSON files + their test coverage. After edits both factions must still `LoadFromFile` without throwing (`FactionValidator.Validate` passes) and `ValidateComplete` must return `Ok:true` for both. Griffin's restored values must match the pre-regression git-history block exactly: hp190, Light armor, speed6.5, Pierce, range2.0, atk-speed1.1, supply2, cost_ore200/cost_crystal0, mesh_scale1.4, train_time18.0, vision_range15.0. `signature_mechanic` ids stay `equal_exchange`/`sanguine_furnace` (already authored, unchanged).

**Block If:** none identified — the roster/schema gaps each have a single defensible resolution grounded in git history, the design doc, and the asset manifest.

**Never:** Do not touch the `aviary` building entries in either JSON — they are legitimate Story 2.8 content (Air-production building + category), predate and are unrelated to the corrupting commit; their `bonded_aerie.glb`/`wraithwing_brood.glb` meshes were never part of the 24-asset FMA manifest and require Alec's local art-gen pipeline to close (out of this session's tooling — track as DW-102, do not fix here). Do not touch beta's pre-existing unmodeled `deferred_mechanics` key. Do not wire any D1 modifier/effect execution (Story 5.4's job) — `signature_mechanic_effect_id` is descriptor-only. Do not add `hero_unit_id`/`persistence_enabled` content (Story 5.6's job).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Both factions load+validate | alpha/beta JSON after edits | `LoadFromFile` succeeds; `FactionValidator.Validate` and `ValidateComplete` both `Ok:true` | n/a |
| Griffin restored | alpha's `units[]` | contains `griffin`/"Greycrest, the Bonded", category `Air`, mesh_path `greycrest_bonded.glb` (exists on disk) | n/a |
| Junk removed | alpha's `units[]` | no `fatso`/`fatso_copy` entries remain | n/a |
| Descriptor fields present | both JSONs | `ai_preset=="balanced"`; `signature_mechanic_display`/`signature_mechanic_effect_id` non-empty | n/a |
| Roster asymmetry unaffected | worker/melee/ranged/siege stat comparison | unchanged from today (already matches fma-faction-design.md) | n/a |

</intent-contract>

## Code Map

- `godot/resources/data/factions/alpha_faction.json` -- replace `fatso`/`fatso_copy` with the restored `griffin` unit; add `ai_preset`/`signature_mechanic_display`/`signature_mechanic_effect_id`.
- `godot/resources/data/factions/beta_faction.json` -- add `ai_preset`/`signature_mechanic_display`/`signature_mechanic_effect_id` (roster/buildings already match the design doc).
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionValidatorTests.cs` -- extend the existing alpha/beta `LoadFromFile` regression tests to assert the new descriptor fields and griffin's presence; add/extend a `ValidateComplete` regression test.
- `_bmad-output/implementation-artifacts/deferred-work.md` -- append DW-102 (aviary mesh assets never generated).

## Tasks & Acceptance

**Execution:**
- `godot/resources/data/factions/alpha_faction.json` -- remove the `fatso`/`fatso_copy` unit entries; insert the `griffin` unit (id `griffin`, display_name "Greycrest, the Bonded", category `Air`, mesh_path `res://assets/models/factions/alpha/greycrest_bonded.glb`, hp190, speed6.5, attack_damage35, attack_range2.0, attack_speed1.1, damage_type Pierce, armor_type Light, cost_ore200, cost_crystal0, supply2, mesh_scale1.4, train_time18.0, vision_range15.0) where they were; add top-level `"ai_preset": "balanced"`, `"signature_mechanic_display": "Equal Exchange"`, `"signature_mechanic_effect_id": "equal_exchange_self_cost"` -- closes the AC1/AC3/AC4 gaps against fma-faction-design.md and the 5.2 schema.
- `godot/resources/data/factions/beta_faction.json` -- add top-level `"ai_preset": "balanced"`, `"signature_mechanic_display": "Sanguine Furnace"`, `"signature_mechanic_effect_id": "furnace_trickle"` (reuses the already-authored baseline passive-regen ability id) -- closes the AC4 gap.
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionValidatorTests.cs` -- extend the alpha/beta `LoadFromFile` regression tests to assert `AiPreset=="balanced"`, non-empty `SignatureMechanicDisplay`/`SignatureMechanicEffectId`, and alpha's `GetUnit("griffin")` non-null with `Category=="Air"`; add/extend a `ValidateComplete` regression test asserting `Ok:true` for both factions post-edit.
- `_bmad-output/implementation-artifacts/deferred-work.md` -- append DW-102 documenting the two `aviary` buildings' missing on-disk meshes as a pre-existing, out-of-boundary asset-generation gap.

**Acceptance Criteria:**
- Given alpha_faction.json/beta_faction.json after this change, when run through `FactionValidator.Validate` and `ValidateComplete`, then both return `Ok:true` with zero errors (epics.md AC1).
- Given the two rosters side by side, when compared unit-for-unit on the buildable baseline (worker/melee/ranged/siege), then hp/speed/armor/supply/cost differ measurably and alpha reads faster/lower-hp, beta slower/higher-hp (epics.md AC2 — already true today, unaffected by this change).
- Given each unit and building entry within both JSONs' 8-unit/4-building FMA-manifest scope (alpha's restored griffin included), when inspected, then display_name is FMA-themed and mesh_path resolves to an on-disk GLB under `assets/models/factions/{alpha|beta}/` (epics.md AC3; the two `aviary` buildings sit outside this manifest scope per DW-102).
- Given both faction JSONs, when inspected, then each declares `ai_preset` and a non-empty signature-mechanic descriptor referencing an effect id (epics.md AC4).
- Given a match launched with these two factions via the existing scenario path, when it runs, then units render with their per-type meshes and team tint as before — no golden-checksum change, since `MeshType`/roster-list length are presentation-only and never folded (epics.md AC5).

## Spec Change Log

## Review Triage Log

### 2026-07-10 — Review pass 1

4 layers (Blind Hunter, Edge Case Hunter, Verification Gap Reviewer, Intent Alignment Auditor), all run against the full diff (JSON edits + 4 new tests + DW-102 ledger entry).

- intent_gap: 0
- bad_spec: 0
- patch: 4 (high 0, medium 1, low 3)
- defer: 3 (high 0, medium 1, low 2)
- reject: 8 (high 0, medium 0, low 8)
- addressed_findings:
  - `[low]` `[patch]` The Story 5.3 test-section header comment implied `ValidateComplete().Ok` certifies on-disk mesh existence and that beta also received a roster/griffin fix — both misreadings independently flagged by 3 of 4 review layers. Rewrote the comment to state `ValidateComplete` is a schema-level (non-blank mesh_path + required-roles) gate only, attribute the roster regression fix to alpha specifically, and point to DW-102/DW-104 for the disk-existence gap.
  - `[low]` `[patch]` `AlphaFaction_LoadFromFile_GriffinRestored_AirCategory_NoJunkPlaceholders` asserted only `Category`/`DisplayName`, leaving griffin's restored numeric stat block (hp/speed/damage/range/cost/supply/mesh_scale/train_time/vision_range/mesh_path) unprotected against a future accidental edit (Edge Case Hunter). Extended the test to assert the full stat block.
  - `[medium]` `[patch]` No test exercised the real, on-disk `alpha_faction.json` through `BuildingSystem.GetProductionUnit`/the Aviary production path — the actual capability this story's griffin restoration unblocks for Player1 (Verification Gap Reviewer, the standout finding this pass). Added `AlphaFaction_RealJson_AviaryProducesGriffin_ThroughBuildingSystem`, wiring the real loaded faction into a `BuildingSystem` and asserting `GetProductionUnit(BuildingType.Aviary, Faction.Player1)?.Id == "griffin"`.
  - `[low]` `[defer]` `FactionDefinition.GetUnit`/`IndexOfUnit`/`GetUnitByCategory`/`GetUnitsByCategory` lack the null-element guard `GetResearch`/`IndexOfResearch` already have — pre-existing, sibling to DW-100/DW-101 (Edge Case Hunter). Logged as DW-103.
  - `[medium]` `[defer]` No validator/test anywhere checks that an authored `mesh_path` resolves to an actual on-disk file — a systemic gap pre-existing since Story 5.2, of which DW-102's aviary meshes are one concrete instance (converged on independently by 3 review layers). Logged as DW-104.
  - `[low]` `[defer]` `fma-faction-design.md`'s narrative ("no Air production building exists") is stale relative to the shipped Story 2.8 Aviary building (Blind Hunter). Logged as DW-105.
  - Rejected (8, all low/cosmetic, no test or behavior change needed): alpha/beta's differing `signature_mechanic_effect_id` authoring convention (mint-new vs. alias-existing) and beta's specific choice of `furnace_trickle` over `furnace_pour` — both cosmetic, non-functional descriptor strings; `ai_preset: "balanced"` being a runtime no-op given the existing C# default — this is exactly what AC4 asked for (explicit authorship); griffin lacking `abilities`/`combat_feedback` — consistent with 3+ sibling alpha units (scout, heavy_infantry, archer) that already lack these for the same reason (needs-code mechanics); redundant `ai_preset` assertions across multiple new tests — harmless test-style overlap; `ValidateComplete` being "hollow" per DW-97 — already tracked, nothing new; asymmetric test depth between alpha/beta — expected, alpha had the regression and beta didn't; no test locking alpha-vs-beta roster-asymmetry (AC2) — epics.md explicitly assigns asymmetry validation to Story 5.8, out of this story's scope on the intent's own authority; AC5 verified by golden-checksum-unchanged reasoning rather than an interactive match launch — already disclosed as a deliberate choice in the spec's Verification section, not an unacknowledged gap.

## Design Notes

**Why the `aviary` buildings are kept, not removed.** They are real, Story-2.8-shipped content (an Air production building + category mapping the design doc, dated before Story 2.8 shipped, assumed didn't exist yet) — removing them would regress a working, cost/prereq-gated build-menu button to a phantom free-build fallback. Their missing GLB assets are an asset-generation gap (needs Alec's local art pipeline), not a data-authoring bug this session can close; tracked as DW-102 instead of blocking the story.

**Why griffin's exact values are trusted.** `git log -S'"fatso"'` isolates the corrupting commit (`c495454`, a `Bmad-Loop@nextinstall` reinstall artifact); the pre-regression `griffin` block it overwrote matches `fma-faction-design.md`'s table field-for-field (including the doc's explicit "vision 15"), so restoring it verbatim is fact-restoration, not new authoring.

## Verification

**Commands:**
- `dotnet build godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: 0 errors.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: all green, including extended `FactionValidatorTests` and unchanged `Golden/*` tests (byte-identical).
- `git status --short -- godot/ProjectChimera.Sim.Tests/Golden/*.golden.txt` -- expected: empty output (no re-baseline needed).

## Auto Run Result

Status: done

**Summary:** Restored alpha's `griffin` Air unit (accidentally destroyed by an unrelated `Bmad-Loop@nextinstall` reinstall commit, `c495454`, and replaced with two non-FMA "Tubby" junk placeholders), added the Story 5.2 schema's `ai_preset`/`signature_mechanic_display`/`signature_mechanic_effect_id` descriptor fields to both showcase faction JSONs, and hardened test coverage — including a review-driven fix proving the restored griffin is actually trainable through the real `BuildingSystem` Aviary production path, not just present in the JSON.

**Files changed:**
- `godot/resources/data/factions/alpha_faction.json` -- removed `fatso`/`fatso_copy`, restored `griffin` (Air) verbatim from pre-regression git history; added `ai_preset`/`signature_mechanic_display`/`signature_mechanic_effect_id`.
- `godot/resources/data/factions/beta_faction.json` -- added `ai_preset`/`signature_mechanic_display`/`signature_mechanic_effect_id` (roster/buildings already matched the design doc).
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionValidatorTests.cs` -- added 5 new tests (descriptor-field checks x2, griffin full-stat-block restoration + junk-removal, `ValidateComplete` regression, and a real-JSON `BuildingSystem.GetProductionUnit(Aviary)` production-path test added during review).
- `_bmad-output/implementation-artifacts/deferred-work.md` -- appended DW-102 through DW-105 (aviary mesh-asset gap; `FactionDefinition` null-element guard gap; systemic mesh-on-disk validation gap; stale design-doc narrative).

**Review findings breakdown:** 0 intent_gap, 0 bad_spec, 4 patch (1 medium, 3 low — all applied), 3 defer (1 medium, 2 low — logged as DW-103/104/105), 8 reject (all low/cosmetic).

**Follow-up review recommendation:** false -- all patched findings were localized (test assertions/comments), no structural or cross-cutting rework was needed.

**Verification performed:** `dotnet build` (0 errors) and `dotnet test` (1421 total, 1420 passed, 1 pre-existing unrelated skip, 0 failed) both re-run after the review patches; `git status --short` on `Golden/*.golden.txt` empty (no re-baseline); the new production-path test independently confirms alpha's Aviary now resolves `griffin` via `BuildingSystem.GetProductionUnit`, the concrete capability this story's data fix was meant to unblock.

**Residual risks:** DW-102 (two `aviary` buildings' `bonded_aerie.glb`/`wraithwing_brood.glb` still don't exist on disk -- needs Alec's local art-gen pipeline, out of this session's tooling); DW-103/DW-104 are pre-existing latent gaps in `FactionDefinition`/`FactionValidator`, not introduced by this story; DW-105 is a documentation-only staleness note. None block this story's own acceptance criteria.
