---
title: 'Single closed-set source of truth for the 6 unit archetypes'
type: 'refactor'
created: '2026-07-19'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false
baseline_revision: '849d0233e56cb8cb7b6ef3a822a3c6a9531e9f61'
final_revision: '5b73626388b4ab2c57b278a57ef252890ad9f54f'
context:
  - '{project-root}/godot/CLAUDE.md'
warnings: []
---

<intent-contract>

## Intent

**Problem:** The 6-archetype closed set (Worker/Melee/Ranged/Siege/Air/Structure) is hand-duplicated across five sites — `UnitDefinitionValidator._categories`, `BehaviorRegistry._archetypes`, `FactionValidator._combatCategories` (a 4-element combat subset), and the `UnitCardPanel`/`BuildingCardPanel` wizard dropdowns — plus two hardcoded validator error strings. No shared constant backs any of them, so adding a 7th `UnitCategory` requires remembering every site with no compiler error if one is missed; a behavior or unit authored against the new archetype would be silently dropped/rejected at load (DW-9, DW-98).

**Approach:** Introduce one closed-set source of truth, `UnitCategories`, derived directly from the canonical `UnitCategory` enum via `Enum.GetNames`, exposing `All` (six names in enum order), `Combat` (All minus the non-combat Worker/Structure), and the `PipeList`/`CombatOrPhrase` error-message fragments. Repoint all five array sites and both error strings at it. Output stays byte-identical today; a future 7th enum member propagates everywhere with zero hand-edits.

## Boundaries & Constraints

**Always:**
- `UnitCategories.All` is derived from the `UnitCategory` enum (`Enum.GetNames`), never a re-typed literal.
- Every touched consumer keeps its exact current idiom (`InSet`, `Array.IndexOf`, `foreach`, dropdown-array field) — only the literal array/string it reads changes.
- The refactor is behavior-preserving: `UnitDefinitionValidator`, `BehaviorRegistry`, and `FactionValidator` produce byte-identical error text and verdicts for all existing inputs (`PipeList` == `"Worker|Melee|Ranged|Siege|Air|Structure"`, `CombatOrPhrase` == `"Melee, Ranged, Siege, or Air"`).
- Match the established shared-closed-set precedent `ResourceCostValidator.KnownResourceIds` (`internal static readonly string[]`, consumed directly by a wizard panel).

**Block If:**
- The `UnitCategory` enum's names or values would need to change to complete this work (they must not — this is a read-only single-source extraction).

**Never:**
- Do NOT touch `UnitDefinition.ParsedCategory` (the string→enum `switch` with its intentional lenient unknown→`Melee` default — a parser, not a duplicated array; changing it risks the lenient-loader contract). Out of scope.
- Do NOT touch the `ComponentGallery`/`ComponentPreview` `Select("Melee","Ranged","Siege","Air")` calls — design-system gallery DEMO widgets using placeholder sample strings, not a functional closed set.
- Do NOT change the `damage_type`/`armor_type`/`separation_priority`/`delivery` closed sets or their error strings (different axes, out of scope).
- Do NOT fold anything new into `SimChecksum`/`CanonicalModelHash`; `UnitCategory` is presentation-read and unhashed by design.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Known archetype | `UnitDefinition.Category` = any of the 6 | `UnitDefinitionValidator` reports no `category` error | No error expected |
| Unknown archetype | `Category` = `"Caster"` | Located `category` error whose text contains `Worker\|Melee\|Ranged\|Siege\|Air\|Structure` | Fail-closed located error |
| Behavior with valid archetype token | behavior file `compatible_archetypes` all in the 6 | Loaded/kept by `BehaviorRegistry.LoadFromDirectory` | No error expected |
| Behavior with out-of-set token | `compatible_archetypes` includes `"Wizard"` | Dropped (reported to `onSkipped`) | Silently skipped (existing behavior) |
| Roster has a combat unit | faction with a Worker + a Ranged | `ValidateComplete` reports no missing-combat error | No error expected |
| Roster missing combat | faction with only a Worker | Located `units` error `"...missing a required combat unit (Melee, Ranged, Siege, or Air)."` | Fail-closed located error |

</intent-contract>

## Code Map

- `godot/src/Core/UnitCategory.cs` -- home of the canonical enum; add the new `UnitCategories` source-of-truth class here.
- `godot/src/Core/Definitions/UnitDefinitionValidator.cs` -- `_categories` (line ~91), category `InSet` check (~171) + error string (~173).
- `godot/src/Core/Definitions/BehaviorRegistry.cs` -- `_archetypes` (line ~25), `Array.IndexOf` archetype-token check in `TryLoad` (~98).
- `godot/src/Core/Definitions/FactionValidator.cs` -- `_combatCategories` (line ~70), combat-role `foreach` (~264) + error string (~277).
- `godot/src/CreationSuite/UnitCardPanel.Edit.cs` -- `Categories` dropdown array (line ~31), consumed at ~77.
- `godot/src/CreationSuite/BuildingCardPanel.Edit.cs` -- `Categories` dropdown array (line ~33), consumed at ~82 and ~433.
- `godot/ProjectChimera.Sim.Tests/Definitions/BehaviorAndCompositionTests.cs` -- existing `LoadFromDirectory` temp-file pattern to mirror.
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionValidatorTests.cs` -- existing `ValidFaction()`/required-role pattern to mirror.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/UnitCategory.cs` -- Add `internal static class UnitCategories` in the `ProjectChimera.Core` namespace: `internal static readonly string[] All = System.Enum.GetNames(typeof(UnitCategory));` (enum-value order → Worker,Melee,Ranged,Siege,Air,Structure); `internal static readonly string[] Combat = All.Where(c => c != nameof(UnitCategory.Worker) && c != nameof(UnitCategory.Structure)).ToArray();`; `internal static string PipeList => string.Join("|", All);`; `internal static string CombatOrPhrase => OrPhrase(Combat);` with a private `OrPhrase(IReadOnlyList<string>)` producing an Oxford-"or" list (`"a, b, or c"`; 1 item → the item; 0 → ""). Add `using System; using System.Collections.Generic; using System.Linq;`. Document it as the single closed-set source of truth mirroring `ResourceCostValidator.KnownResourceIds`.
- `godot/src/Core/Definitions/UnitDefinitionValidator.cs` -- Delete `_categories`. Change the category check to `InSet(UnitCategories.All, def.Category)` and the error string's parenthetical to `({UnitCategories.PipeList})`. (`All` is `string[]`, so `InSet` is unchanged.)
- `godot/src/Core/Definitions/BehaviorRegistry.cs` -- Delete `_archetypes`. Change the `TryLoad` token check to `Array.IndexOf(UnitCategories.All, token) < 0`.
- `godot/src/Core/Definitions/FactionValidator.cs` -- Delete `_combatCategories`. Change the combat-role loop to `foreach (string combatCategory in UnitCategories.Combat)` and the error string's parenthetical to `({UnitCategories.CombatOrPhrase})`.
- `godot/src/CreationSuite/UnitCardPanel.Edit.cs` -- Change `Categories` to `= UnitCategories.All;` (drop the literal). Keep its type `string[]` and its consumption unchanged.
- `godot/src/CreationSuite/BuildingCardPanel.Edit.cs` -- Change `Categories` to `= UnitCategories.All;` (drop the literal). Keep its type `string[]` and both consumption sites unchanged.
- `godot/ProjectChimera.Sim.Tests/Definitions/UnitCategorySingleSourceTests.cs` -- New xUnit file covering the I/O matrix and the derivation contract (see Acceptance). Mirror the temp-file `LoadFromDirectory` helper in `BehaviorAndCompositionTests` and the `ValidFaction()` builder shape in `FactionValidatorTests`.

**Acceptance Criteria:**
- Given the `UnitCategory` enum, when reading `UnitCategories.All`, then it equals exactly `["Worker","Melee","Ranged","Siege","Air","Structure"]` in that order AND `All.Length == Enum.GetValues(typeof(UnitCategory)).Length` (derivation locked — a 7th enum member would grow `All`).
- Given `UnitCategories`, when reading `Combat`, `PipeList`, `CombatOrPhrase`, then `Combat` == `["Melee","Ranged","Siege","Air"]`, `PipeList` == `"Worker|Melee|Ranged|Siege|Air|Structure"`, `CombatOrPhrase` == `"Melee, Ranged, Siege, or Air"`.
- Given a `UnitDefinition` whose `Category` iterates over every value in `UnitCategories.All`, when validated, then no `category` error is produced; and given `Category = "Caster"`, then a located `category` error is produced whose message contains `UnitCategories.PipeList`.
- Given behavior files whose `compatible_archetypes` list every value in `UnitCategories.All`, when loaded via `BehaviorRegistry.LoadFromDirectory`, then all are kept; and given one with an out-of-set token, then it is skipped.
- Given a faction with a Worker plus one combat-category unit, when `FactionValidator.ValidateComplete` runs, then no missing-combat error; and given a Worker-only roster, then a located `units` error containing `UnitCategories.CombatOrPhrase`.
- Given the full solution, when built, then it compiles with no new warnings and the whole `ProjectChimera.Sim.Tests` suite passes.

## Review Triage Log

### 2026-07-19 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 4: (high 0, medium 0, low 4)
- defer: 1: (high 0, medium 0, low 1)
- reject: 8
- addressed_findings:
  - `[low]` `[patch]` Panels aliased the validators' `UnitCategories.All` `string[]` by reference (a new coupling vs the old per-site literals) — gave each panel its own copy (`UnitCategories.All.ToArray()`) so an in-place dropdown mutation/sort cannot corrupt the validation vocabulary process-wide.
  - `[low]` `[patch]` `PipeList`/`CombatOrPhrase` re-allocated on every access inside error construction — memoized as `static readonly` (both derive from immutable `All`/`Combat`).
  - `[low]` `[patch]` Restored the deleted archetype-set rationale comment in `BehaviorRegistry` (parity with `UnitDefinitionValidator`, which kept one).
  - `[low]` `[patch]` Removed a tautological test assertion (`Enum.GetValues.Length == Enum.GetNames.Length` is always true) and documented the hardcoded ordered literal as the deliberate enum-change tripwire.

Deferred (surfaced for the orchestrator to ledger — per the invocation constraint this run does NOT edit the deferred-work ledger): future-7th-`UnitCategory` derived-site review. This refactor makes the accept-set (`UnitDefinitionValidator`, `BehaviorRegistry`, wizard dropdowns) auto-widen with the enum, but two downstream seams do not auto-track: `UnitDefinition.ParsedCategory` (string→enum `switch`, `_ => Melee` default) would silently coerce a newly-accepted category to `Melee` at spawn, and `UnitCategories.Combat`'s exclusion definition auto-classifies any new archetype as combat. Neither is a defect today (no 7th member exists; behavior is byte-identical); both are relocated/sharpened future risks the enum-literal test tripwire will surface when a 7th member is actually added.

Rejected (noise / deliberate design): switch `Combat` to a hardcoded allowlist (re-introduces the hand-duplicated subset the intent removes; the exclusion form is deliberate and documented); enum-sentinel speculation (`UnitCategory` is documented as the curated archetype set); `ComponentGallery`/`ComponentPreview` demo `Select(...)` widgets (sample data with no load/authoring path — outside the intent's "dropped/rejected at load" failure mode); `OrPhrase` defensive 0/1/2-item branches; cross-assembly two-instance doc nitpick; `using System.Linq` in Core (already idiomatic across `Core.Definitions`, runs once at static-init, `UnitCategory` is unhashed); exact-literal test assertions (the intentional tripwire); UI-order doc nitpick.

## Design Notes

Single-source rationale mirrors `ResourceCostValidator.KnownResourceIds` (already consumed directly by `BuildingCardPanel` as `CostResourceIds`). `internal static readonly string[]` visibility: the wizard panels compile into the main `ProjectChimera` assembly and the tests compile the Core sources directly (`SimSources.props`), so `internal` is visible to both — no `InternalsVisibleTo` needed. `UnitCategory.cs` is inside `src/Core/**`, already in the shared Tier-1 source set, so `UnitCategories` is unit-testable.

`Enum.GetNames` returns names ordered by underlying value; `UnitCategory` is `Worker=0..Structure=5`, so the order matches every existing literal and the wizard dropdown order is unchanged. `Combat` is defined by exclusion (`All` minus Worker/Structure) so a future 7th archetype is combat-by-default unless explicitly excluded there — a single discoverable decision point, strictly better than today's silent omission.

The `ProjectChimera.Core.Definitions` namespace is nested under `ProjectChimera.Core`, so `UnitCategories` is visible in the three validators without a new `using`; add `using ProjectChimera.Core;` only if the compiler requires it.

Note on DW-9 ledger text: its `reason` field carries a second, unrelated paragraph about the composition-UI (`AddComponentPicker`/`AddCompositionRow`/`ApplyComponentList`) verification gap — a mis-merged fragment from the legacy-ledger migration. It is NOT part of this bundle's stated intent (`unit-category-single-source`) and is deliberately not addressed here.

## Verification

**Commands:**
- `dotnet build godot/godot.csproj` -- expected: build succeeds, no new warnings (validates the wizard-panel + validator changes compile against the shared source in the full Godot assembly).
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: all tests pass, including the new `UnitCategorySingleSourceTests` and the pre-existing `BehaviorAndCompositionTests`/`FactionValidatorTests`/`UnitDefinitionValidatorTests` (proving byte-identical behavior).

## Auto Run Result

Status: done

Resolves deferred-work bundle `unit-category-single-source` (DW-9, DW-98).

**Summary:** Extracted one enum-derived closed-set source of truth — `internal static class UnitCategories` in `godot/src/Core/UnitCategory.cs` (`All` = `Enum.GetNames(typeof(UnitCategory))`, `Combat` = All minus Worker/Structure, `PipeList`, `CombatOrPhrase`) — and repointed all five previously-hand-duplicated archetype sites plus the two validator error strings at it. A future 7th `UnitCategory` member now propagates to the validators, the behavior registry, and the wizard dropdowns automatically; output is byte-identical today.

**Files changed:**
- `godot/src/Core/UnitCategory.cs` — added the `UnitCategories` source-of-truth class (`PipeList`/`CombatOrPhrase` memoized as `static readonly` per review).
- `godot/src/Core/Definitions/UnitDefinitionValidator.cs` — deleted `_categories`; category `InSet` + error string now read `UnitCategories.All`/`PipeList`.
- `godot/src/Core/Definitions/BehaviorRegistry.cs` — deleted `_archetypes`; archetype-token gate reads `UnitCategories.All`; restored rationale comment.
- `godot/src/Core/Definitions/FactionValidator.cs` — deleted `_combatCategories`; combat-role loop + error string read `UnitCategories.Combat`/`CombatOrPhrase`.
- `godot/src/CreationSuite/UnitCardPanel.Edit.cs` / `BuildingCardPanel.Edit.cs` — dropdown `Categories` now a per-panel copy `UnitCategories.All.ToArray()` (derives from the source yet cannot alias it).
- `godot/ProjectChimera.Sim.Tests/Definitions/UnitCategorySingleSourceTests.cs` — new 8-test file locking the derivation contract, the exact error-string fragments, and each repointed consumer's accept/reject behavior.

**Review findings:** 4 patches applied (all low-severity hardening: panel array-copy, memoize strings, restore comment, de-tautologize test), 1 deferred (future-7th-member derived-site review: `ParsedCategory` + `Combat` classification — surfaced here for the orchestrator, not written to the ledger per invocation constraint), 8 rejected. No intent_gap or bad_spec; no repair loopback.

**Verification:**
- `dotnet build godot/godot.csproj -t:Rebuild` — succeeded, 0 errors; 11 warnings all pre-existing (CS8632 nullable-context in EntityWorld/ResourceStore/etc.; CS8604 at `UnitCardPanel.Edit.cs:532`, the chip code) — zero new warnings on any touched line.
- `dotnet test …Sim.Tests` — Passed 2725, Skipped 1 (pre-existing), Failed 0. All 6 I/O-matrix rows covered by the new tests (Matrix Test Audit satisfied).

**Residual risks:** None today (byte-identical behavior). The one deliberate future-facing decision point is the deferred derived-site seam noted above; the enum-literal test tripwire will fail loudly to force a review when a 7th archetype is actually added.

**Follow-up review recommended:** false (four localized low-consequence hardening patches; no behavior/API/data impact).
