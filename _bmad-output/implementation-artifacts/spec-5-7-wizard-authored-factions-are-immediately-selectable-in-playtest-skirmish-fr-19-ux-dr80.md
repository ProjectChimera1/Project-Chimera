---
title: 'Wizard-authored factions are immediately selectable in playtest & skirmish (FR-19, UX-DR80)'
type: 'feature'
created: '2026-07-11'
status: 'done'
baseline_revision: 'f6e449a9bc145dcfb37133ee67c8b2672772f792'
final_revision: '10a6bee7fb381466f5049ff548ce177cef91e2ac'
review_loop_iteration: 0
followup_review_recommended: false
context: []
warnings: [oversized]
---

<intent-contract>

## Intent

**Problem:** `resources/data/factions/` can already hold more than the two shipped faction files (the Story
5.5/5.6 wizard writes `{id}_faction.json` there), but nothing ever discovers what is in that directory — every
faction load is a hardcoded or scenario-specified single-file path (`MainScene.P1_FACTION_JSON`/`P2_FACTION_JSON`,
`ScenarioLoadPhase.ResolveSlotFactionDefs`'s per-slot `FactionJson`). There is also no real skirmish/lobby setup
screen yet (that UI is Story 11.1, which itself depends on this story's registry list) and no separate "playtest"
picker — so today a wizard-authored faction can be assigned to a scenario slot by hand-editing JSON, but a creator
has no way to see it enumerated, and a faction that fails validation can silently reach a match slot uncaught
(`FactionValidator.ValidateComplete`, the "is this faction genuinely complete/playable" gate, has zero production
callers today — `deferred-work.md` DW-97).

**Approach:** Add a directory-scan discovery method (mirrors `AbilityRegistry.LoadFromDirectory`'s pattern) that
enumerates `*_faction.json` files, validates each with `FactionValidator.ValidateComplete`, and returns only the
valid ones — closing DW-97 for the discovery path. Call it fresh at every `MainScene._Ready` (Godot reloads the
scene from disk on every Play/Playtest, so this alone satisfies "no restart needed", exactly like the existing
Ability/Behavior/Item registries) and print the discovered set to the console (the one observable "selectable list"
surface that exists prior to Story 11.1's real screen). Also close DW-97's match-load half: wire a shadow-mode
`ValidateComplete` diagnostic into `ScenarioLoadPhase.ResolveSlotFactionDefs`, mirroring its own existing
`UnitTagValidator` `GD.PrintErr` idiom exactly.

## Boundaries & Constraints

**Always:**
- New method lives on `FactionDefinition` (which already does file I/O via `LoadFromFile`), NOT on
  `FactionRegistry` — `FactionRegistry`'s own class doc states it "never touches res:// or does file I/O"; that
  invariant is not touched by this story.
- Signature: `public static IReadOnlyList<FactionDefinition> LoadSelectableFromDirectory(string absDir, Action<string, string>? onExcluded = null)`.
  Scans `Directory.GetFiles(absDir, "*_faction.json")` (NOT bare `*.json` — the same folder holds
  `_buildingcard_sample.json`/`_unitcard_sample.json`, which must never be mistaken for factions), ordinal-sorted
  by filename for a deterministic walk (mirrors `AbilityRegistry.LoadFromDirectory`). Missing/absent `absDir`
  returns an empty list, never throws.
- Per file: parse via `JsonSerializer.Deserialize<FactionDefinition>(File.ReadAllText(file), JsonOptions)` inside a
  try/catch (never call `LoadFromFile` here — it throws on `Validate` failure, which would abort the whole scan on
  one bad file). A parse exception or null result → `onExcluded?.Invoke(fileName, message)`, skip. A successful
  parse then runs `FactionValidator.ValidateComplete(def)`; `!Ok` → `onExcluded?.Invoke(fileName, firstLocatedError)`,
  skip; `Ok` → include. Result list is ordinal-sorted by `def.Id` (mirrors `AbilityRegistry`'s stable-index
  convention), so alpha/beta and any authored factions enumerate deterministically alongside each other (AC3's
  "showcase factions remain selectable alongside authored ones").
- `MainScene._Ready`: call `FactionDefinition.LoadSelectableFromDirectory` over the globalized factions dir
  immediately after the existing `_abilityRegistry`/`_behaviorRegistry`/`_itemRegistry` `LoadFromDirectory` calls,
  using the SAME `GD.Print($"[Factions] skipped invalid {name}: {reason}")` idiom for `onExcluded` and one
  additional `GD.Print` line naming the discovered, selectable faction ids/count. This is the AC1 observable
  surface: no skirmish/lobby picker screen exists yet (Story 11.1, which depends on THIS story's registry list,
  per `epics.md`'s own gap-closure note), so a console-visible discovered-set is the honest, currently-real
  "selectable list" this story can produce and verify against.
- `ScenarioLoadPhase.ResolveSlotFactionDefs`: immediately after `FactionDefinition.LoadFromFile(abs)`, run
  `FactionValidator.ValidateComplete(def)` and `GD.PrintErr` each located error, shadow-mode/non-blocking —
  the exact idiom the very next lines in this method already use for `UnitTagValidator.ValidateAndDropUnits`.
  Closes DW-97's match-load half without inventing a new blocking policy (per DW-97's own closure note).
- Close `deferred-work.md` DW-97 (mark resolved, referencing this story) once both wiring points land.

**Block If:** none identified — every type this story reads (`FactionValidator.ValidateComplete`,
`FactionDefinition.JsonOptions`, `AbilityRegistry.LoadFromDirectory`'s pattern) already exists from Stories 5.1/5.2.

**Never:** Do not build a skirmish/lobby/match-setup screen or any new faction-picker UI — that is Story 11.1's
explicitly separate, later deliverable (`epics.md` names it as the "code-referenced-but-never-built setup screen"
that 5.7/10.1/10.11 all assume, and it depends on THIS story's registry list, not the other way around). Do not
touch `MapGeneratorPhase`'s `Slot0FactionJson`/`Slot1FactionJson` (a distinct AI-scenario-authoring concern). Do not
change `MainScene`'s `new FactionRegistry(2)` active-player-count or the checksum-critical `ActiveFactions`/
`ActiveCount` span — per-slot ASSIGNMENT is already `PLAYER_COUNT`-aware and covers Player1-4 today (proven by
`Golden/FactionRegistryTests.cs`'s existing `GetSlotDefinition`/`ToFaction` coverage); widening the ACTIVE (economy-
ticked/checksummed) count beyond 2 has no live surface to exercise it yet (no 3-4 player skirmish screen exists) and
is determinism-critical — log a new deferred-work entry for it instead of touching `MainScene._Ready`'s boot
sequencing. Do not widen `FactionRegistry.SLOT_DEFINITIONS_SIZE`/`PLAYER_COUNT` (Story 9.2's job, DW-94). Do not
change `FactionValidator.Validate`'s existing `LoadFromFile`-safe behavior.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| New wizard-authored faction, valid | A faction saved via Story 5.5/5.6's wizard sits in `resources/data/factions/` as `{id}_faction.json`, next Playtest/Skirmish launch (fresh scene load) | `LoadSelectableFromDirectory` includes it; console log names it | No error expected |
| Showcase factions | `alpha_faction.json`/`beta_faction.json` present (always valid, Story 5.3) | Both always appear in the discovered set alongside any authored ones | No error expected |
| Non-faction sample files present | `_buildingcard_sample.json`/`_unitcard_sample.json` also live in the directory | Neither is scanned/parsed at all (filename filter excludes them) | No error, not even a skip-report |
| Faction file fails ValidateComplete | e.g. missing Worker role or empty `ai_preset` | Excluded from the discovered set; `onExcluded` reports the located field + message | No file written/mutated, no throw |
| Malformed JSON file | Unparsable `*_faction.json` | Excluded via `onExcluded` with the parse-failure message; scan continues to the next file | No throw, scan does not abort |
| Missing directory | `resources/data/factions/` absent | `LoadSelectableFromDirectory` returns an empty list | No throw |
| Slot assignment, match boot | A scenario slot's `FactionJson` names a valid authored faction | `ResolveSlotFactionDefs` loads it via the existing per-slot path; units/buildings spawn correctly | No error expected |
| Slot assignment, invalid faction assigned | A scenario slot names a faction that fails `ValidateComplete` | Load still proceeds (shadow-mode, non-blocking, matching `UnitTagValidator`'s sibling idiom) but a located `GD.PrintErr` diagnostic is emitted naming the slot + failing field | Diagnostic only, no block |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/FactionDefinition.cs` -- add `LoadSelectableFromDirectory(string absDir,
  Action<string,string>? onExcluded = null)`: `*_faction.json` scan, try/catch parse, `ValidateComplete` gate,
  ordinal-by-`Id` sort. Read-only reference: existing `LoadFromFile`/`JsonOptions`.
- `godot/src/Core/MainScene.cs` -- call the new method right after the `_abilityRegistry`/`_behaviorRegistry`/
  `_itemRegistry` `LoadFromDirectory` block (~line 274-290), using the SAME `GlobalizePath` + `GD.Print` idiom;
  add one `GD.Print` line naming the discovered selectable faction ids/count (the AC1 observable surface).
- `godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs` -- in `ResolveSlotFactionDefs` (~line 101-111), add the
  `ValidateComplete` shadow-mode `GD.PrintErr` diagnostic directly after `FactionDefinition.LoadFromFile(abs)`,
  before the existing `UnitTagValidator` block (same non-blocking pattern).
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionDiscoveryTests.cs` -- new file: cover every I/O-matrix row
  against a temp directory (valid faction included; alpha/beta-style valid pair both included; malformed JSON
  excluded with reason; `ValidateComplete`-failing faction excluded with located reason; non-`_faction.json` sample
  files ignored entirely; missing directory returns empty; deterministic ordinal-by-Id ordering with 3+ files).
- `_bmad-output/implementation-artifacts/deferred-work.md` -- mark DW-97 resolved (both wiring points landed); add
  one new entry for the explicitly-deferred active-player-count-still-hardcoded-to-2 gap (see Never section).

## Tasks & Acceptance

**Execution:**
- `FactionDefinition.cs` -- add `LoadSelectableFromDirectory` -- the one non-throwing, `ValidateComplete`-gated
  directory scan every other Code Map item depends on.
- `MainScene.cs` -- wire the call + console-print the discovered set -- delivers AC1's only currently-real
  observable surface.
- `ScenarioLoadPhase.cs` -- wire the shadow-mode `ValidateComplete` diagnostic into `ResolveSlotFactionDefs` --
  delivers AC2's "loads through FactionRegistry correctly" together with DW-97's match-load closure, and AC4's
  non-blocking-with-reason behavior for a slot-assigned bad faction.
- `FactionDiscoveryTests.cs` -- cover every I/O-matrix row -- proves AC1/AC3/AC4 without a live Godot boot.
- `deferred-work.md` -- close DW-97, log the active-count follow-up -- keeps the ledger accurate.

**Acceptance Criteria:**
- Given a faction just saved via the Faction Definer sitting in `resources/data/factions/`, when the game next
  boots (Playtest or Skirmish, no restart), then `LoadSelectableFromDirectory` includes it and the console names it
  (FR-19).
- Given that faction assigned to a scenario player slot, when a match boots, then that slot loads the chosen
  `FactionDefinition` through the existing `FactionRegistry`-backed per-slot path and its units/buildings
  spawn/render correctly (FR-19).
- Given Player1-4 slot assignment, when a faction is assigned to any of those four slots, then the existing
  `FactionRegistry.ToFaction`/`GetSlotDefinition` API resolves it correctly with no 2-slot-only assumption in the
  discovery/assignment code this story adds (AR-3) — the showcase factions remain discoverable alongside authored
  ones.
- Given a faction file failing `ValidateComplete` present in the data folder, when the discovery scan runs, then it
  is excluded from the selectable set with a located reason logged (UX-DR80).

## Spec Change Log

(none — no bad_spec loopback occurred during this story's review)

## Review Triage Log

### 2026-07-11 — Review pass 1
- intent_gap: 0
- bad_spec: 0
- patch: 8 (high 0, medium 1, low 7)
- defer: 3 (high 0, medium 0, low 3)
- reject: 4 (high 0, medium 0, low 4)
- addressed_findings:
  - `[medium]` `[patch]` `deferred-work.md`'s DW-97 resolution note claimed "both match-load halves" of the
    roster-completeness gate were closed and cited `FactionRegistryTests`/`MultiFactionGoldenTests` as coverage for
    the `ScenarioLoadPhase` diagnostic — neither test file exercises `FactionDefinition.LoadFromFile`,
    `ScenarioLoadPhase`, or `FactionValidator` at all (independently confirmed by Verification Gap Reviewer reading
    both files in full, and by Blind Hunter/Edge Case Hunter noting the dedicated-server path
    `MainScene.BuildHeadlessServerSimHost`/`ServerBootstrap.Build` never received the diagnostic). Fixed: DW-97
    reverted to `status: open`, resolution note corrected to describe CLIENT-side-only closure (discovery +
    `ScenarioLoadPhase`), both now backed by a real live godot-verify pass (assigning a `ValidateComplete`-failing
    faction to a scenario slot and confirming the located `GD.PrintErr` fired, non-blocking, scenario still loaded)
    rather than the mistaken test citation. The dedicated-server residual is left open, matching this project's own
    established risk posture for that determinism-critical surface (the same reasoning DW-97 originally used).
  - `[low]` `[patch]` `DW-120`'s first draft named only `MainScene._Ready`'s hardcoded `new FactionRegistry(2)`;
    `BuildHeadlessServerSimHost`'s own independent `new FactionRegistry(2)` + `activeFactionCount: 2` hardcode was
    missing (Blind Hunter). Fixed: DW-120 rewritten to name both boot paths.
  - `[low]` `[patch]` `ScenarioLoadPhase.ResolveSlotFactionDefs`'s new `ValidateComplete` diagnostic ran BEFORE
    `UnitTagValidator.ValidateAndDropUnits`, so it could report "Ok" for a roster that becomes incomplete once an
    unknown-tagged unit is dropped (Blind Hunter). Fixed: reordered to run after tag-drop, live-verified this pass.
  - `[low]` `[patch]` `LoadSelectableFromDirectory` had no dedup for two files sharing the same faction `Id` — both
    would land in the selectable list with no disambiguation (Edge Case Hunter). Fixed: first-file-wins by ordinal
    walk order, duplicate reported via `onExcluded`; new test `DuplicateId_FirstFileWins_SecondReportedAndExcluded`.
  - `[low]` `[patch]` `Directory.GetFiles` itself was unguarded, so an enumeration-level I/O/permission exception
    would violate the method's documented "Never throws" contract (Edge Case Hunter). Fixed: wrapped in try/catch,
    reported via `onExcluded` against the directory path, returns empty on failure.
  - `[low]` `[patch]` Doc comment conflated the walk-order sort (only affects `onExcluded` firing order and
    duplicate-id tie-breaking) with the final `Id` sort (the one that actually delivers deterministic enumeration)
    (Blind Hunter). Fixed: doc comment rewritten to attribute each guarantee to the correct sort.
  - `[low]` `[patch]` No test distinguished an existing-but-empty factions directory from a missing one — different
    code branches (`Directory.Exists` guard vs. an empty `Directory.GetFiles` result) (Blind Hunter). Fixed: added
    `ExistingEmptyDirectory_ReturnsEmpty_NoThrow`.
  - `[low]` `[defer]` Discovered `FactionDefinition`s from `LoadSelectableFromDirectory` are not ability-resolved or
    tag-validated, unlike every other faction def actually used to spawn units — a latent trap for Story 11.1's
    future picker if it naively assigns a discovered def straight to a match slot (Blind Hunter) — logged as DW-121.
  - `[low]` `[defer]` A fourth near-identical directory-scan-loader method now exists alongside
    `AbilityRegistry`/`BehaviorRegistry`/`ItemRegistry`'s `LoadFromDirectory`, with no shared helper — the same
    duplication class as DW-98 for a different concern (Blind Hunter) — logged as DW-122.
  - `[low]` `[defer]` `ScenarioLoadPhase.ResolveSlotFactionDefs`'s `FactionDefinition.LoadFromFile(abs)` call still
    throws uncaught on a lenient-`Validate` failure — pre-existing since Story 5.2, not introduced by this story,
    surfaced incidentally while reviewing the adjacent new code (Edge Case Hunter) — logged as DW-123.
  - `[low]` `[reject]` First-error-only reporting in `LoadSelectableFromDirectory`'s `onExcluded` (vs. the
    `ScenarioLoadPhase` diagnostic's list-all behavior) was flagged as an inconsistency (Blind Hunter) — this is
    exactly what the spec's own Boundaries & Constraints directed (`onExcluded?.Invoke(fileName, firstLocatedError)`)
    for a console-log-only surface with no real picker UI yet; not a defect.
  - `[low]` `[reject]` `selectableFactions` in `MainScene._Ready` is computed, printed, and discarded rather than
    cached (Blind Hunter) — deliberate per the spec's own Design Notes; Story 11.1 owns building real plumbing atop
    this discovery API when it lands.
  - `[low]` `[reject]` No test covers a faction failing `ValidateComplete` on multiple axes simultaneously (Blind
    Hunter) — not needed given the first-error-only design above is accepted, not a defect.
  - `[low]` `[reject]` `MainScene`'s discovery scan doesn't run in the headless/windowed dedicated-server branch
    (Edge Case Hunter) — matches this codebase's own pre-existing precedent: the Ability/Behavior/Item registries
    also only load in the client branch, never on a headless server; not a new deviation this story introduced.

## Design Notes

**Why the filename filter is `*_faction.json`, not `*.json`.** `resources/data/factions/` also holds
`_buildingcard_sample.json` and `_unitcard_sample.json` (unrelated sample content). A bare `*.json` scan would try
to deserialize these as factions; they'd likely fail `ValidateComplete` and get excluded anyway, but with a
misleading "faction failed validation" reason for files that were never factions. The `{id}_faction.json` suffix
already matches the wizard's own write convention (`FactionDefinerWizardCore.cs`) and both shipped files, so
filtering on it is both safer and free (no convention change needed).

**Why the active-player-count (`new FactionRegistry(2)` in `MainScene._Ready`) is explicitly out of scope.**
`SimChecksum`'s per-faction ore/checksum loop iterates exactly `FactionRegistry.ActiveFactions`, which this call
hardcodes to 2 — a real gap for a hypothetical 3-4 player match, but there is no live surface yet (no skirmish
setup screen) that could assign a faction to Player3/4 and expect its economy to tick. Per-slot ASSIGNMENT
(`SlotDefinitions`/`GetSlotDefinition`/`ToFaction`) already supports Player1-4 today, proven by
`Golden/FactionRegistryTests.cs`. Widening the ACTIVE count is checksum/determinism-critical and belongs with
whichever story actually builds a surface to exercise it (Story 11.1 or 9.2), matching this project's own
established precedent for deferring exactly this class of currently-unexercised risk (see DW-97's own reasoning).

## Verification

**Commands:**
- `dotnet build godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: 0 errors.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: all green, including
  the new `FactionDiscoveryTests` cases, and no regression in `FactionRegistryTests`/`MultiFactionGoldenTests`
  (checksum-critical paths untouched).
- `dotnet build godot/godot.csproj` -- expected: 0 errors (MainScene/ScenarioLoadPhase presentation-layer wiring).

**Manual checks (if no CLI):**
- Save a new test faction via the Faction Definer wizard (Edit mode, `X`), then relaunch the game (Play Skirmish or
  Playtest) without restarting the editor; confirm the Godot output console prints the new faction's id among the
  discovered/selectable set, alongside alpha and beta.
- Assign that faction to a scenario slot's `FactionJson` and boot a match; confirm the slot's units/buildings spawn
  using the new faction's data.
- Temporarily break a faction file (e.g. blank a required `mesh_path`) in the factions directory; confirm the
  console reports it as excluded/skipped with a located reason, and it does NOT appear in the discovered set.

## Auto Run Result

Status: done

**Summary:** Added `FactionDefinition.LoadSelectableFromDirectory` — a directory-scan discovery method (mirrors
`AbilityRegistry.LoadFromDirectory`'s established pattern) that enumerates every `*_faction.json` under
`resources/data/factions/`, gates each through `FactionValidator.ValidateComplete`, dedupes by `Id` (first-file-wins),
and returns only the valid/unique ones, ordinal-sorted. Wired into `MainScene._Ready` (console-printed discovered
set — the one currently-real "selectable list" surface, since no skirmish/lobby picker screen exists yet; that is
Story 11.1, which itself depends on this discovery API) and into `ScenarioLoadPhase.ResolveSlotFactionDefs` as a
shadow-mode, non-blocking `ValidateComplete` diagnostic run after tag-drop. This closes the CLIENT-side half of
DW-97 (a pre-existing gap: `FactionValidator.ValidateComplete` had zero production callers, so a malformed faction
could silently reach a match). The review pass caught and fixed a documentation overclaim (DW-97 was prematurely
marked fully "done" when the dedicated-server path remains unwired — corrected to `open`, scoped accurately) plus
several small, real code defects (diagnostic ordering vs. tag-drop, missing duplicate-Id handling, an unguarded
`Directory.GetFiles`, a doc-comment inaccuracy) — all patched and re-verified.

**Files changed:**
- `godot/src/Core/Definitions/FactionDefinition.cs` — added `LoadSelectableFromDirectory` (directory scan,
  try/catch parse, `ValidateComplete` gate, duplicate-`Id` dedup, ordinal-by-`Id` sort, guarded `Directory.GetFiles`).
- `godot/src/Core/MainScene.cs` — wired the discovery call after the Ability/Behavior/Item registry loads; prints
  the discovered selectable set and any skipped/excluded files to the console.
- `godot/src/Core/Bootstrap/Phases/ScenarioLoadPhase.cs` — added the shadow-mode `ValidateComplete` diagnostic to
  `ResolveSlotFactionDefs`, ordered after `UnitTagValidator.ValidateAndDropUnits` (review-pass fix) so it reflects
  the actually-spawnable roster.
- `godot/ProjectChimera.Sim.Tests/Definitions/FactionDiscoveryTests.cs` (new) — 10 xunit tests: every I/O-matrix row
  plus two review-pass additions (existing-but-empty directory, duplicate-Id dedup).
- `_bmad-output/implementation-artifacts/deferred-work.md` — DW-97 reopened with an accurate, narrower resolution
  (client-side closed, dedicated-server residual named); DW-120 corrected to name both hardcoded active-count call
  sites; DW-121/DW-122/DW-123 added for review-surfaced, out-of-scope-for-this-story gaps.

**Review findings breakdown (Review pass 1):** 0 intent_gap, 0 bad_spec, 8 patch (1 medium: the DW-97
overclaim/coverage-citation error; 7 low: DW-120 undercounting, diagnostic ordering, duplicate-Id dedup, unguarded
`Directory.GetFiles`, a doc-comment inaccuracy, and a missing empty-directory test), 3 defer (DW-121: discovered
defs not ability/tag-resolved, a latent trap for Story 11.1; DW-122: a fourth near-duplicate directory-loader
pattern; DW-123: a pre-existing, unguarded `LoadFromFile` throw in `ScenarioLoadPhase`, not caused by this story),
4 reject (first-error-only reporting, uncached discovery list, no multi-axis-failure test, and server-mode discovery
skip — all deliberate-by-spec or matching established codebase precedent).

**Follow-up review recommendation:** false — the one medium finding was a documentation/ledger accuracy correction
(no code behavior change), and the seven low findings were small, individually well-contained code fixes (a
reorder, a dedup guard, an exception guard, a doc comment, one added test) with no public API signature change and
no impact on checksum-critical code paths. All patches were independently rebuilt, retested, and — for the two
behavioral fixes (diagnostic ordering, dedup) — re-verified live via godot-verify in this same pass.

**Verification performed:**
- `dotnet build`/`dotnet test` on `ProjectChimera.Sim.Tests.csproj` and `dotnet build godot.csproj` — independently
  re-run by the orchestrator after both the initial implementation and every review-pass patch (not just taken from
  the implementation subagent's report) — final state: 0 build errors, 1471 passed, 1 pre-existing skip, 0 failed.
- Live in-editor verification (`godot-verify` skill, Godot MCP), performed twice: (1) initial pass — dropped a new
  valid faction file into `resources/data/factions/` and relaunched the game with no editor restart; the console
  printed `[Factions] 3 selectable: alpha, beta, verify57`; a second, deliberately-broken faction file was excluded
  with a located reason (`[Factions] skipped invalid verify57bad_faction.json: ...roster is missing a required
  Worker unit`); both test files were deleted afterward. (2) review-pass verification — temporarily pointed a
  scenario slot's `faction_json` at a `ValidateComplete`-failing faction and relaunched; the console printed
  `[FactionValidator] slot 1 (...): roster is missing a required Worker unit` and the missing-combat-unit error,
  the scenario still loaded and the match still booted (non-blocking, shadow-mode, as designed); the scenario file
  and test faction file were reverted/deleted afterward (confirmed clean via `git status`/`git diff`). Zero new
  console errors in either pass (only pre-existing, unrelated `.NET: Failed to load project assembly` transient
  messages, present before this story's changes too).

**Residual risks:** DW-97's dedicated-server half (`MainScene.BuildHeadlessServerSimHost`/`ServerBootstrap.Build`)
remains unwired — deliberately, matching this project's own established risk posture for that
determinism-critical, currently-unexercised surface (no live 3+ player or dedicated-server-with-authored-factions
path exists yet). DW-120's active-player-count gap (hardcoded to 2 in both boot paths) is unchanged by this story
for the same reason. DW-121 (discovered defs not ability/tag-resolved) is a latent trap for Story 11.1's future
picker, not exploitable today since the discovery list has no live consumer yet. None of these are new regressions
introduced by this story; all are pre-existing or explicitly out-of-scope gaps, now accurately tracked in
`deferred-work.md`.
