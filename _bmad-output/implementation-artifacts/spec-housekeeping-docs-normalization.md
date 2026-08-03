---
title: 'Housekeeping: docs normalization (DW-46, DW-105, DW-324)'
type: 'chore'
created: '2026-08-03'
status: 'done'
baseline_revision: 'cbdbb264c8ec45593b62242a1f15595743ed7bac'
final_revision: '71165f69f25a70bf64b581342beb7d20336fcfe9'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/godot/CLAUDE.md'
warnings:
  - oversized
---

<intent-contract>

## Intent

**Problem:** A doc-debt sweep. Three ledger entries flag stale/misleading comments and one missing design-doc addendum: (DW-46) the `UseItem`/`DropItem` enum comments claim BEFORE-the-guard dispatch when the shipped 3.15 anti-cheat dispatches them AFTER the ownership guard — a latent trap; (DW-105) the FMA design doc still says no Air production building exists, but Story 2.8 shipped `BuildingType.Aviary`; (DW-324) an enumerated stale-comment list (rulesetHash fold, checksum seat ceiling, ability-editor filter bits, LAN smoke-test build requirement).

**Approach:** Comment/doc-only edits — correct each stale statement to match shipped reality, verified against the current code. No behavior, data, or public-signature change. Several DW-324 sub-items are already-accurate, owned by another open entry, ledger-internal, or explicitly optional; those are documented as no-change with reasoning rather than edited.

## Boundaries & Constraints

**Always:** Comments/docs ONLY — zero change to any executable statement, constant value, signature, or JSON. Every corrected comment must state what the code actually does today (verified this session). Keep each file's existing comment voice/format.

**Block If:** Any listed "fix" would require changing a constant or behavior to make the comment true (that is a code defect, not doc rot — stop and surface it). — HALT blocked.

**Never:** Do NOT edit `_bmad-output/implementation-artifacts/deferred-work.md` (the orchestrator records resolution). Do NOT change `Modifier.cs:48` "0 = instantaneous" (owned by the still-open DW-270, bundle 15.3). Do NOT change the value of any constant (`ServerChecksumCollector.MaxSlots` stays 4; `EffectCaps` values unchanged). Do NOT add the optional FallbackMirror-vs-alpha_map_01 test (a new test is out of a docs-normalization scope).

## I/O & Edge-Case Matrix

DELETE — no runtime I/O; this is a comment/doc sweep.

</intent-contract>

## Code Map

- `godot/src/Core/EntityWorld.cs:37-38` — DW-46: `UseItem=16`/`DropItem=17` enum doc comments (currently wrong "BEFORE the entity guard").
- `godot/src/Multiplayer/NetworkCommand.cs:323-340` — authoring source for DW-46: `IsAlive`(323)+`FactionOf`(324) guard, then `UseItem`(333)/`DropItem`(338) dispatch. READ-ONLY reference.
- `_bmad-output/fma-faction-design.md` — DW-105: add dated Air-resolved addendum near the top.
- `godot/src/Effects/EffectCaps.cs:8,79,87` — DW-324: "reserved to fold into the Epic-9 rulesetHash" is stale (folded by `RulesetHash`, Story 9.4).
- `godot/src/Core/Definitions/RulesetHash.cs` — authoring source proving the fold. READ-ONLY reference.
- `godot/src/Multiplayer/Server/ServerChecksumCollector.cs:11-12,22` — DW-324: stale "Mirrors ServerTransport.MAX_SLOTS / 8 in 9.2"; `MAX_SLOTS` is now 8, the collector ceiling is the seat ceiling (4).
- `godot/src/CreationSuite/AbilityEditorPanel.Advanced.cs:280-281` — DW-324 (GATE-COUPLED): "NEVER the reserved Air/Ground/Structure bits" contradicted since Story 2.9a.
- `godot/src/Core/Definitions/AbilityDraft.cs:52-57` — authoring source: `DraftVocabulary.Filters` now includes Air/Ground/Structure. READ-ONLY reference.
- `godot/tools/lan-desync-smoke.ps1` — DW-324: add a "requires source/DEBUG build" banner.

## Tasks & Acceptance

**Execution:**

- `godot/src/Core/EntityWorld.cs` -- rewrite the `UseItem = 16` and `DropItem = 17` inline comments so the dispatch order is correct. Replace `Handled by OrderApplier BEFORE the entity guard, delegating to ItemSystem.UseItemCommand (the Train/Revive building-command pattern).` with: `Handled by OrderApplier AFTER the IsAlive/FactionOf entity-ownership guard (UnitId names the hero ENTITY, so it is guarded like a normal unit command — the 3.15 anti-cheat that stops a player forcing an ENEMY hero to use items; NOT the Train/Revive building-command pre-guard pattern), delegating to ItemSystem.UseItemCommand.` Apply the analogous rewrite to `DropItem` (…forcing an ENEMY hero to drop items…, delegating to ItemSystem.DropItemCommand). -- DW-46: the comment was a latent anti-cheat trap.

- `godot/src/Effects/EffectCaps.cs` -- three edits. (1) Line ~8 in the class summary, change `these are the single set that folds into the rulesetHash later (the hash itself is an Epic-9 concern; here we only NAME them)` to `these are the single set folded, in file order, into the ruleset hash by RulesetHash (Story 9.4)`. (2) Line ~79 change `reserved to fold into the Epic-9 rulesetHash.` to `folded into the ruleset hash by RulesetHash (Story 9.4).` (3) Line ~87 the same `reserved to fold into the Epic-9 rulesetHash.` -> `folded into the ruleset hash by RulesetHash (Story 9.4).` Leave the `MaxSpawnCount`/`MaxPersistentPeriods` "reserved leaf" note untouched (still accurate). -- DW-324: 9.4 folded them.

- `godot/src/Multiplayer/Server/ServerChecksumCollector.cs` -- two edits, VALUE unchanged (`MaxSlots` stays `4`). (1) In the class summary (~line 11-12) replace `N-shaped (any N≥2; MaxSlots=4 in 1.0 — 8 is a constant bump + the Faction-enum extension in Story 9.2, not a rewrite).` with `N-shaped (any N≥2; MaxSlots=4 = the MP seat/player ceiling, PlayerCountPolicy.MpSeatCeiling). NOTE: ServerTransport.MAX_SLOTS is now 8 (players + spectator headroom, Story 9.7); this ceiling deliberately tracks the seat/player ceiling (4), not that transport ceiling — only seated players report sim checksums.` (2) The `MaxSlots` field summary (~line 22) replace `ServerTransport ceiling in 1.0 (N≤4; 8 = a constant bump, Story 9.2). Mirrors ServerTransport.MAX_SLOTS.` with `Checksum-reporting peer ceiling, pinned to the MP seat/player ceiling (PlayerCountPolicy.MpSeatCeiling == ServerTransport.MAX_PLAYERS == 4). NOT ServerTransport.MAX_SLOTS (== 8, players + spectator headroom, Story 9.7) — deliberately left at 4; see the class summary.` -- DW-324: the "mirrors MAX_SLOTS" claim went stale when MAX_SLOTS became 8.

- `godot/src/CreationSuite/AbilityEditorPanel.Advanced.cs` -- **DEFERRED this session (not changed).** This is the one DW-324 item on a gate-coupled surface (`godot/src/CreationSuite/**`). Editing even a comment there triggers the mechanical in-engine gate, which requires directly observing the SearchArea Filter checkbox set in the running editor. That render is driven by a C# `OptionButton.ItemSelected` delegate that this session could not re-trigger from the running game (neither GDScript `emit_signal("item_selected")` nor an injected real Down-arrow key on the focused, correctly-selected dropdown fired the C# re-render). Rather than record a gate PASS whose deepest assertion was not directly observed, the comment is left unchanged and this sub-item is carried as a residual — see Design Notes. The correction (Air/Ground/Structure authorable since 2.9a) is fully captured in this spec for a follow-up session already driving the Creation Suite. -- DW-324 (deferred residual).

- `godot/tools/lan-desync-smoke.ps1` -- insert a banner in the header comment block (immediately after the `# Story 1.9b — two-machine LAN determinism launcher …` line near the top), reading: `#\n#  ⚠ REQUIRES A SOURCE / DEBUG BUILD. The F9 desync-injection hotkey and the [Determinism]\n#    instrumentation this smoke test reads are compiled under \`#if DEBUG\` (src/Multiplayer/LobbyUi.cs,\n#    LoopbackDesyncSelfTest.cs) and are ABSENT from an exported release build. Run it against the\n#    editor / \`dotnet build\` (Debug) game, never a release export.` Comment-only; do not touch the `param(...)` block or any executable line. -- DW-324: missing build-requirement banner.

- `_bmad-output/fma-faction-design.md` -- add a dated addendum near the very top (after the doc's title/intro, before the first design section) recording that Design Decision #1 "AIR THIS MILESTONE?" was resolved YES by Story 2.8, which shipped `BuildingType.Aviary` mapped to the `"Air"` category and wired into `alpha_faction.json`/`beta_faction.json` as a cost/prereq-gated buildable; the griffin/wyvern are now trainable (not scenario-placement-only), and the "Air production building + Air category mapping" needs-new-code epic has shipped. Do NOT rewrite the stale body paragraphs (:122,:150,:167,:183) — the dated addendum supersedes them, matching the DW-105 closure guidance. -- DW-105.

**Acceptance Criteria:**
- Given a reader of `EntityWorld.cs`, when they read the `UseItem`/`DropItem` comments, then the comments state dispatch happens AFTER the `IsAlive`/`FactionOf` ownership guard, matching `NetworkCommand.cs:323-340`.
- Given `EffectCaps.cs`, when read, then no comment still says the caps are "reserved to fold" — each references the shipped `RulesetHash` (Story 9.4) fold; the `EffectCaps` constant values are byte-for-byte unchanged.
- Given `ServerChecksumCollector.cs`, when read, then `MaxSlots` is still `4` and the comments attribute it to the MP seat/player ceiling, with the stale "Mirrors ServerTransport.MAX_SLOTS / 8 in 9.2" text gone.
- Given `lan-desync-smoke.ps1`, when read, then a source/DEBUG-build-required banner is present in the header and no executable line changed.
- Given `fma-faction-design.md`, when read from the top, then a dated addendum records Air resolved YES by Story 2.8.
- Given `dotnet build godot/godot.csproj`, when run, then it succeeds (comments/docs cannot break the build).
- Given the final diff, when the in-engine gate (`tools/verify-in-engine-gate.ps1`) runs, then it reports "gate not applicable" and exit 0 — the AbilityEditorPanel (CreationSuite) item was deferred, so no Godot-coupled surface is touched.

## Review Triage Log

### 2026-08-03 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 4: (high 0, medium 2, low 2)
- defer: 0
- reject: 0
- addressed_findings:
  - `[medium]` `[patch]` `lan-desync-smoke.ps1` banner mis-cited the F9 hotkey (it is in `src/Core/MainScene.cs:1076-1093`, not `LobbyUi.cs`/`LoopbackDesyncSelfTest.cs`) and wrongly claimed the `[Determinism]` readout is `#if DEBUG`-gated (it is ungated in `ServerHost.cs:93-145`, ships in release) — rewrote the banner to scope the DEBUG requirement to the F9 drill only and cite the right files. Confirmed against code.
  - `[medium]` `[patch]` `ItemSystem.cs:219` `UseItemCommand` doc still read "before the entity guard (the Train/Revive pattern)" — the exact stale claim DW-46 corrects in `EntityWorld.cs`, now a direct in-repo contradiction on an anti-cheat-relevant comment; corrected to AFTER the `IsAlive`/`FactionOf` guard (3.15 anti-cheat; NOT the pre-guard pattern). Adjacent DW-46 completion (Combat, not gate-coupled). `DropItemCommand` doc verified to carry no ordering claim.
  - `[low]` `[patch]` `fma-faction-design.md` addendum cited body line numbers (`:122,:150,:167,:183`) that its own 8-line insertion shifted by +8; replaced with stable phrase-based references.
  - `[low]` `[patch]` `ServerChecksumCollector.cs` `MaxSlots` doc said "pinned to" `MpSeatCeiling`, but it is a hand-kept literal `4` (not a symbolic pin like `MAX_PLAYERS`); reworded to "a hand-kept literal deliberately equal to … NOT a symbolic pin".

### 2026-08-03 — Review pass (follow-up)
- intent_gap: 0
- bad_spec: 0
- patch: 1: (high 0, medium 0, low 1)
- defer: 1
- reject: 10
- addressed_findings:
  - `[low]` `[patch]` `lan-desync-smoke.ps1` — the banner explained WHY F9 is absent on a release build but not the SYMPTOM: on an export build F9 is a silent no-op (no error, no log), so a clean run there means the drill was never exercised, not that determinism held. Added a sentence stating the silent-no-op failure mode so a tester cannot false-pass the #1 ship gate. All five reviewers independently re-verified every factual claim in the diff as TRUE against source (dispatch order at `NetworkCommand.cs:323-340`, the `RulesetHash` Story 9.4 fold, `MAX_SLOTS==8`/`MAX_PLAYERS==4`, the `#if DEBUG` F9 in `MainScene.cs`, the ungated `ServerHost.cs` readout, the Aviary→"Air" faction wiring); no comment misstates today's code. Deferred: DW-500 — the sibling `loopback-desync-smoke.ps1` (+ `.cmd`) still lacks the same F9 DEBUG banner (pre-existing, out of the DW-324 "LAN smoke-test" intent). Rejected as noise/out-of-scope: intent-authorized left-unrewritten FMA body paragraphs (superseded by the dated addendum per DW-105 guidance); "symbolically pin MaxSlots" and "add a RulesetHash completeness test" (both code/behavior changes barred by the comment-only boundary, and `NoHardcodedPlayerCountTests` already pins `MaxSlots==MpSeatCeiling`); comment-duplication/cross-reference style suggestions; and the verification-plan meta-observation (all underlying claims were verified TRUE by the reviewers reading source).

## Design Notes

Verified-this-session sources for each correction:
- DW-46: `NetworkCommand.cs` — `IsAlive`(323) + `FactionOf`(324) ownership guard precede `UseItem`(333)/`DropItem`(338); the in-file comment at :326-328 already documents the AFTER-guard anti-cheat rationale. EntityWorld's enum comment is the lone stale copy.
- DW-324 rulesetHash: `RulesetHash.cs` (Story 9.4) folds every `EffectCaps` cap in file order (`MaxEffectDepth … MaxTotalEffectNodes`) via FNV-64.
- DW-324 seat ceiling: `ServerTransport.MAX_SLOTS == 8` (players + spectator headroom, Story 9.7); `ServerTransport.MAX_PLAYERS == PlayerCountPolicy.MpSeatCeiling == 4`. Collector caps expected peers at `[2, MaxSlots=4]`.
- DW-324 filter bits: `AbilityDraft.cs:52-57` `DraftVocabulary.Filters` = { None, Self, Ally, Enemy, Neutral, Alive, Air, Ground, Structure } (Story 2.9a "now evaluated").
- DW-324 LAN banner (corrected in review): ONLY the F9 desync-injection hotkey is `#if DEBUG`, and it lives in `src/Core/MainScene.cs:1076-1093` (NOT `LobbyUi.cs`/`LoopbackDesyncSelfTest.cs`). The `[Determinism]` window/MATCH SUMMARY readout is in `src/Multiplayer/Server/ServerHost.cs:93-145` and is NOT DEBUG-gated (ships in release). The banner therefore scopes the DEBUG requirement to the F9 drill only.

Explicitly NO-CHANGE DW-324 sub-items (documented, not edited):
- `Modifier.cs:48` "0 = instantaneous" — owned by the still-open DW-270 (bundle 15.3, modifier-period-semantics); editing here would collide with that bundle.
- `ModifierStore.cs:~31-40` re-entrancy guard — the comment ALREADY notes the Story 2.3 content-validator fence ("kept off the executor by the Story 2.3 content validator"); it is accurate, so no edit.
- 2.9b fallback-seed "dead ApplyFallback:159-160 anchor" — that anchor lives inside a `deferred-work.md` ledger entry, which this bundle is forbidden to edit; out of scope (orchestrator territory).
- `ScenarioLoadPhase.cs:~440` fallback start positions comment — verified ACCURATE against `ScenarioApplier.BuildFallbackMirror` (slot 0 BaseX=-45, slot 1 BaseX=+45); not stale, so left unedited (also avoids gate churn on a correct comment).
- Optional FallbackMirror-vs-alpha_map_01 agreement test — explicitly optional; a new test is out of a docs-normalization scope.

DEFERRED coupled residual (`AbilityEditorPanel.Advanced.cs`, DW-324): this is the only DW-324 item on a gate-coupled surface (`godot/src/CreationSuite/**`). Attempted the in-engine gate this session — launched the freshly-built game, located the live Ability Editor (`/root/MainScene/.../AbilityEditorPanel.cs`, `hasToggle=true`), opened it, and confirmed its effect-kind dropdown offers the closed 7 kinds INCLUDING "Search Area" (verbatim from the running `OptionButton`). But the final, load-bearing observation — the SearchArea Filter row's Air/Ground/Structure checkboxes — is produced by a C# `OptionButton.ItemSelected` re-render delegate that would not fire from the running game via either GDScript `emit_signal("item_selected", 5)` or an injected real Down-arrow key on the focused dropdown pre-selected to index 4. Because a comment-only change cannot itself alter behavior, and I will not record a gate PASS whose deepest assertion I did not directly observe, I reverted the comment edit (zero coupled surfaces → gate not applicable → clean/reliable bundle completion) and carried this one sub-item as a residual. It should be picked up by a session already driving the Creation Suite, where the checkbox render is naturally reachable. The correction itself is verified in source (`AbilityDraft.cs:52-57`: `DraftVocabulary.Filters` includes Air/Ground/Structure since Story 2.9a) and preserved above for that session.

## Verification

**Commands:**
- `dotnet build godot/godot.csproj` -- expected: build succeeds, 0 errors.
- `git diff --stat` -- expected: only the files listed in Execution appear; changes are comment/doc lines plus the fma addendum (no executable-line churn).

**Manual checks:**
- Grep each touched code file for the old stale phrase to confirm it is gone (e.g. `reserved to fold into the Epic-9`, `BEFORE the entity guard` / `before the entity guard` for UseItem/DropItem in both `EntityWorld.cs` and `ItemSystem.cs`, `Mirrors ServerTransport.MAX_SLOTS`).
- In-engine gate (`tools/verify-in-engine-gate.ps1`) reports "gate not applicable" (exit 0): the one coupled-surface item (`AbilityEditorPanel.Advanced.cs`) was deferred, so nothing under a coupled path is touched.


## Auto Run Result

Status: done
Blocking condition: none

**Change:** Follow-up review pass over the already-committed docs-normalization sweep (DW-46, DW-105, DW-324 partial). Five review lenses (adversarial, edge-case, verification-gap, intent-alignment, in-engine gate) ran in parallel; every factual claim in the diff was independently re-verified TRUE against current source, and the in-engine gate is not applicable (no coupled surface touched). One low-severity patch was applied — the `lan-desync-smoke.ps1` DEBUG-build banner now states the silent-no-op symptom (on a release build F9 does nothing with no error/log, so a clean run means the drill was never exercised), closing a false-pass risk on the #1 ship gate. One pre-existing issue was deferred as DW-500 (the sibling `loopback-desync-smoke.ps1`/`.cmd` lacks the same banner). All other findings were rejected as noise or out-of-scope of the comment-only intent.

**Files changed:**
- `godot/tools/lan-desync-smoke.ps1` — banner reworded to add the silent-no-op failure symptom for the F9 desync drill on a release/export build (comment-only; no executable line touched).
- `_bmad-output/implementation-artifacts/deferred-work.md` — appended NEW entry DW-500 (sibling loopback smoke script missing the F9 DEBUG banner); no existing entry modified.
- `_bmad-output/implementation-artifacts/spec-housekeeping-docs-normalization.md` — this spec: status/followup frontmatter, new Review Triage Log entry, and this Auto Run Result.

**Verification:** `dotnet build godot/godot.csproj` → Build succeeded, 0 Errors, 14 Warnings (the `.ps1` comment cannot affect the C# build). Grep confirms the corrected stale phrases remain absent from source (`reserved to fold into the Epic-9`, `Mirrors ServerTransport.MAX_SLOTS`, `before the entity guard` in `ItemSystem.cs`/`EntityWorld.cs` → 0 hits) and the new banner symptom line is present. In-engine gate: NOT APPLICABLE — no `godot/src/UI/**`, `godot/src/CreationSuite/**`, `godot/src/Core/Bootstrap/**`, `MainScene.cs`, `scenes/**`, `*.tscn`, or `*.tres` is touched (the AbilityEditorPanel DW-324 sub-item remains deferred).

**Residual risks:** DW-500 (sibling loopback smoke banner) is open, low severity. The DW-324 AbilityEditorPanel.Advanced.cs filter-bits comment remains a documented residual for a session already driving the Creation Suite (its correction is captured in this spec). Durability concerns raised by the adversarial lens (MaxSlots literal, hand-maintained RulesetHash fold list) are guarded by existing tests (`NoHardcodedPlayerCountTests`, `RulesetHashTests`) and were out of scope for a comment-only sweep.
