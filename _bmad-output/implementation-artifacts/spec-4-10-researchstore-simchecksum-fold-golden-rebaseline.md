---
title: 'ResearchStore SimChecksum fold + golden re-baseline'
type: 'feature'
created: '2026-07-10'
status: 'done'
baseline_revision: 'cf7d16befe8c641f61698d542658d8918499feaf'
final_revision: '415a37de6627fd6173963662050af9c5b93f1a33'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-4-context.md'
  - '{project-root}/godot/src/Core/SimChecksum.cs'
  - '{project-root}/godot/src/Core/ResearchStore.cs'
  - '{project-root}/godot/src/Core/SimulationLoop.cs'
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** Story 4.9 made `ResearchStore` mid-match-mutable (order start/tick/complete/cancel) but explicitly did NOT fold it into `SimChecksum` — a research-heavy match's in-progress order state and completed-level/cumulative-modifier deltas can silently desync between peers with no detection, the one gap 4.9's own Design Notes call out.

**Approach:** Fold `ResearchStore`'s per-active-faction mutable state into `SimChecksum.Compute` (ascending faction, then ascending research index — the shape `ResearchStore` was already built for), bump `AlgoVersion` 13→14, add a dedicated coverage-guard test, add one new research-driving golden scenario, and re-baseline every existing golden in the same commit (mirrors the 4.7 `ResourceNodeStore` first-ever-fold precedent).

## Boundaries & Constraints

**Always:**
- Fold, per faction in `factions.ActiveFactions` (ascending, mirrors the existing `ResourceStore` per-faction loop): `InProgressIndex`, `RemainingTicks`, then a count-driven loop over `CompletedLevels[idx].Length` (mirrors `ResourceNodeStore`/`AbilityCount`'s count-driven convention — never the fixed 5-faction stride) mixing, per research index: `CompletedLevels[idx][r]`, `CumulativeMaxHealthDelta[idx][r].Raw`, `CumulativeAttackDamageDelta[idx][r].Raw`, `CumulativeMoveSpeedDelta[idx][r].Raw`, `CumulativeArmorDelta[idx][r].Raw`. The four cumulative deltas ARE genuinely mid-match-mutated sim truth (future-spawn catch-up reads them directly), so — consistent with every prior fold's "fold it directly, don't rely on transitive coverage" posture (`ModifierStore`/`EffectiveArmor`/HeroStore's `Xp`) — they fold alongside `CompletedLevels`, not only via the count.
- `research` is a new trailing optional param (`ResearchStore? research = null`) on `SimChecksum.Compute` and `SimulationLoop.EnableChecksums`, appended after `nodes` — mirrors every prior optional-store addition (v6/v11/v12/v13). A null store folds a single `Mix(0)` (legacy/test callers only; `SimulationHost` always constructs a real `ResearchStore` in production, so null-vs-real-empty are never compared against each other).
- `AlgoVersion` bumps 13→14 with a doc-comment entry mirroring the v13 entry's narrative style (what's folded, why, that it's the scheduled re-baseline).
- `SimChecksumCoverageGuardTest`: add differential-mutation teeth (mirrors `AssertResourceNodeStoreFoldedIntoChecksum`) proving each folded `ResearchStore` field moves the hash; update `ComputeKnownStateHash()` to pass a real (empty) `ResearchStore`, assert `AlgoVersion == 14`, and re-pin the known-state hash constant from an actual green run (never hand-computed).
- Add ONE new golden scenario (mirrors `ShopPurchaseScenario`/`ShopPurchaseGoldenTests`) driving a real `StartResearch` → tick-to-completion → (a second `StartResearch` for the next level, proving the re-baselined ladder state moves the hash) through `OrderApplier`/`SimulationHost`, with its own committed `.golden.txt`, run-twice/matches-committed/sequence-evolves/record tests.
- Re-baseline every existing `.golden.txt` (`CHIMERA_GOLDEN_RECORD=1`, filtered per-file per each file's own documented rebaseline hint, then `dotnet build` to refresh embedded copies) in the SAME commit — review the diff to confirm only hash lines moved before committing (mirrors 4.7's precedent exactly).
- `SimulationHost.cs:230`'s `EnableChecksums` call passes `Research` as the new trailing arg; its inline comment gains a `+ ResearchStore (v14)` clause.

**Block If:** None — no decision here requires human input.

**Never:**
- No change to `ResearchSystem`'s order-path logic, gates, or command dispatch (4.9, done) — this story only adds a checksum fold + tests + re-baseline.
- No fold of `StartedAtPosition` — it is read only to push the presentation-only `CombatEventType.ResearchComplete` (never checksummed, mirrors why other completion-event positions aren't folded either).
- No command-card UI, no research authoring editor changes (Story 4.11).
- Don't touch `CanonicalModelHash`/`StartStateHash` — those cover authored content, not runtime sim state; this story adds no new authored fields.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| In-progress order | A faction starts research | `InProgressIndex`/`RemainingTicks` mutation moves the checksum | No error |
| Completed level | An order completes | `CompletedLevels[idx][r]` + all four cumulative deltas moving each independently moves the checksum | No error |
| Idle / no research authored | Faction has no research def, or store passed as `null` | Folds a stable `Mix(0)`; never throws, never diverges from another idle/null run | No error |
| Two-run replay of a research-heavy scenario | New golden scenario stepped twice in-process | Byte-identical sequences; matches committed golden | No error |

</intent-contract>

## Code Map

- `godot/src/Core/SimChecksum.cs` -- add `ResearchStore? research = null` param to `Compute`; fold per-active-faction state (see Always); bump `AlgoVersion` 13→14 with a new doc entry; extend the class-summary "Hashed state" list.
- `godot/src/Core/SimulationLoop.cs` -- add `ResearchStore? research = null` param to `EnableChecksums`, a `_checksumResearch` field, and thread it through both `SimChecksum.Compute` call sites (`StepOnce`/`Update`).
- `godot/src/Core/Sim/SimulationHost.cs:230` -- pass `Research` as the new trailing `EnableChecksums` arg; extend the inline comment.
- `godot/ProjectChimera.Sim.Tests/Golden/SimChecksumCoverageGuardTest.cs` -- add `AssertResearchStoreFoldedIntoChecksum` (mirrors `AssertResourceNodeStoreFoldedIntoChecksum`), call it from `EntityCommandFields_AreFoldedIntoTheChecksum`; rename `KnownWorldState_ProducesPinnedV13Hash` → `...V14Hash`, update `ComputeKnownStateHash()` to pass `new ResearchStore()`, assert `AlgoVersion == 14`, re-pin `ExpectedV14Hash` from a green run.
- `godot/ProjectChimera.Sim.Tests/Golden/ResearchScenario.cs` (new) -- a `FactionDefinition` with one building (`AvailableResearch`) offering a 2-level research def, built via `SimulationHost.Create`, mirroring `ShopPurchaseScenario`'s shape.
- `godot/ProjectChimera.Sim.Tests/Golden/ResearchGoldenTests.cs` (new) -- `RunsTwiceInProcess_AreByteIdentical` / `MatchesCommittedGolden` / `Sequence_Evolves_NotVacuous` / `RecordResearchBaseline`, mirroring `ShopPurchaseGoldenTests` exactly; issues `StartResearch` via `OrderApplier.Apply`, ticks to completion, issues a second `StartResearch` for level 2.
- `godot/ProjectChimera.Sim.Tests/Golden/research-scenario.golden.txt` (new) + all 23 existing `*.golden.txt` -- re-baselined in this commit (v13→v14 moves every existing golden; the new one is recorded fresh).

## Tasks & Acceptance

**Execution:**
- `SimChecksum.cs` -- fold `ResearchStore` + `AlgoVersion` bump -- the actual desync-detection fix.
- `SimulationLoop.cs` / `SimulationHost.cs` -- thread the new store through to production `Compute` calls -- makes the fold live in every host.
- `SimChecksumCoverageGuardTest.cs` -- coverage teeth + re-pinned known-state hash -- proves the fold is real and pins v14.
- `ResearchScenario.cs` / `ResearchGoldenTests.cs` / `research-scenario.golden.txt` (new) -- a dedicated research-driving golden -- covers the AC's "research-heavy scenario replays byte-identical" directly, not just incidentally via the re-baseline.
- All `*.golden.txt` -- re-baselined -- keeps every existing scenario's pin truthful under v14.

**Acceptance Criteria:**
- Given `ResearchStore` (from 4.9), when `SimChecksum.Compute` runs, then it mixes `InProgressIndex`/`RemainingTicks` per active faction and `CompletedLevels` + the four cumulative stat deltas per research index, `AlgoVersion` bumps 13→14 with a doc-comment entry mirroring the v13 entry's narrative style, and `SimChecksumCoverageGuardTest` is re-pinned with a `ResearchStore` fold coverage assertion.
- Given two runs of the new research-driving golden scenario, when replayed, then their checksum sequences are byte-identical and match the committed golden.
- Given the v13→v14 bump, when the full existing golden suite is re-baselined, then every file's checksum lines move (a real fold, not a no-op) and the diff is reviewed to confirm only hash lines changed before committing.

## Review Triage Log

### 2026-07-10 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 4: (high 1, medium 2, low 1)
- defer: 2: (high 0, medium 1, low 1)
- reject: 5: (high 0, medium 0, low 5)
- addressed_findings:
  - `[high]` `[patch]` `SimChecksumCoverageGuardTest.AssertResearchStoreFoldedIntoChecksum` and the new research golden only ever mutated/exercised `Faction.Player1` — a hardcoded-index or mis-ordered per-faction loop bug would fold only Player1 and pass every assertion added by this story undetected. Extended the coverage test with a second active faction (`Player2`) mutation asserted independently of Player1's state. Flagged independently by all three applicable review layers (Blind Hunter, Edge Case Hunter, Verification Gap).
  - `[medium]` `[patch]` The coverage test only ever grew `CompletedLevels` to one research entry (r=0), so an r>0 indexing bug in the inner per-research loop would pass undetected. Extended `EnsureCapacity` to 2 entries and added a mutation + assertion at index 1. Flagged by the Edge Case Hunter review layer.
  - `[medium]` `[patch]` No test exercised the effect of `CancelResearchCommand`'s in-progress→idle transition (`InProgressIndex=-1`, `RemainingTicks=0`) on the fold. Added a reset-to-idle mutation + assertion to the coverage test (cheaper and equally rigorous than reworking the golden scenario's tick narrative to add a live Cancel order). Flagged by the Edge Case Hunter review layer.
  - `[low]` `[patch]` The `SimChecksum.cs` v14 doc comment self-conflicted ("mirrors the ResourceStore per-faction loop shape" stated alongside "never the fixed 5-faction stride," implying ResourceStore's loop IS a fixed stride, which it isn't). Reworded both the `AlgoVersion` doc entry and the inline fold comment to clarify the outer loop mirrors `ActiveFactions` iteration (not a raw stride) and the inner loop is bound by a per-faction count (not a fixed constant). Flagged by the Blind Hunter review layer.
  - Deferred (2): the new fold's `research.InProgressIndex[(int)f]` etc. has no bounds guard against `FactionRegistry` allowing more active factions than `ResearchStore`'s hardcoded `FACTION_COUNT=5` — an existing, already-accepted architectural ceiling shared identically by `ResourceStore`'s own per-faction fold since Story 1.3b, not newly introduced by this story; recorded as DW-87. `ResearchSystem.CompleteResearch`'s (Story 4.9, untouched by this diff) O(n) full-world scan on every completion cites `SupplySystem.Tick`'s per-tick scan as precedent, which undersells the cost-profile difference; a pre-existing comment-accuracy/performance observation out of this story's scope; recorded as DW-88. Both flagged by the Blind Hunter review layer.
  - Rejected (5, all low): the "null store ≡ single Mix(0)" vs. a real-but-empty store's per-faction-loop shape mismatch (Blind Hunter — a deliberate, documented design choice; no test anywhere asserts null-vs-real-empty equality for any of the other optional stores either, and `SimulationHost` always passes a real store in production); the bulk 22-file golden re-baseline being unreviewable by pure line-diff inspection (Blind Hunter — inherent to the established re-baseline mechanism itself, mirrors the 4.7 precedent, a process observation not a code defect); `ResearchScenario.Build()` embedding an `Assert.Equal` inside scenario-construction code (Blind Hunter — an already-established pattern shared by `HeroRevivalScenario`/`ItemScenario`/`ShopPurchaseScenario`, not novel to this story); no dedicated exclusion teeth proving `StartedAtPosition` mutations do NOT move the checksum (Blind Hunter / Edge Case Hunter — the Verification Gap review layer's dedicated analysis concluded this is not a real desync surface, since the position is already covered transitively via `BuildingStore.Position`, and the exclusion is a deliberate, documented choice); a suggested `Math.Min` guard across the five per-faction-per-research arrays' lengths (Edge Case Hunter — unreachable given `ResearchStore.EnsureCapacity`'s atomic five-array resize invariant, an already-fails-safe defensive branch).

## Design Notes

**Why fold the cumulative deltas directly, not just `CompletedLevels`:** the epic-level story text mentions only `InProgressIndex`/`CompletedLevels`, but the four cumulative `Fixed` deltas are independently mid-match-mutated (accumulated via `SaturatingAdd` at each completion) and read directly by future-spawn catch-up — exactly the class of state every prior `AlgoVersion` bump has folded directly rather than leaving to transitive coverage (e.g. `EffectiveArmor`, `ModifierStore` instances, `HeroStore.Xp`). Relying on `CompletedLevels` alone would leave a `SaturatingAdd`/indexing bug in the delta math undetectable until a new unit spawns and its `Effective*` stats happen to diverge.

**Why `StartedAtPosition` stays unfolded:** it is written once at Start and read only to position the presentation-only `CombatEventType.ResearchComplete` event — never read by tick-affecting logic (unlike `BuildingStore.RallyPoint`, which `SpawnTrainedUnit` reads to redirect movement, and which IS folded). This mirrors the existing precedent that event-triggering positions aren't independently hashed.

**Null-store equivalence is documentation, not a tested invariant:** unlike `HeroStore`/`ItemStore`/`ResourceNodeStore` (whose "null ≡ single Mix(0)" is naturally true because a real-but-empty store also produces a single Mix(0) count), a real-but-empty `ResearchStore` would fold once per active faction, not once total. No existing test asserts null-vs-real-empty equality for any of the other optional stores either — the null branch exists solely for legacy/test callers that predate this story; `SimulationHost` always passes a real store in production, so the two branches are never compared against each other.

## Verification

**Commands:**
- `dotnet build godot/godot.sln -c Debug` -- expected: builds clean.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release --filter FullyQualifiedName~SimChecksumCoverageGuardTest` -- expected: coverage teeth + re-pinned v14 known-state hash pass.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release --filter FullyQualifiedName~ResearchGolden` -- expected: new golden's run-twice/matches-committed/evolves tests pass.
- `CHIMERA_GOLDEN_RECORD=1 dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release` then `dotnet build` -- expected: re-baselines every golden (v13→v14 moves all 23 existing + records the new one); review `git diff --stat` to confirm only expected files/hash lines moved, then commit.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release` -- expected: full Tier-1 suite green, no regressions.

## Auto Run Result

Status: done

**Summary:** Folded `ResearchStore`'s per-active-faction mutable state (`InProgressIndex`/`RemainingTicks` plus, per research index, `CompletedLevels` and the four cumulative `Fixed` stat deltas) into `SimChecksum.Compute`, bumping `AlgoVersion` 13→14 — the first-ever fold of this store, closing the determinism gap Story 4.9 explicitly deferred. Added a dedicated coverage-guard test proving every folded field moves the hash (including, after review, a second active faction, a second research index, and a cancel-shaped idle-reset), re-pinned the known-state hash, added a new research-driving golden scenario exercising a full start→complete→re-start cycle, and re-baselined all 22 pre-existing goldens in the same commit.

**Files changed:**
- `godot/src/Core/SimChecksum.cs` — `Compute` gains a trailing `ResearchStore? research = null` param; folds per-active-faction state; `AlgoVersion` 13→14 with a new doc entry.
- `godot/src/Core/SimulationLoop.cs` — `EnableChecksums` threads the new param through both production `Compute` call sites via a new `_checksumResearch` field.
- `godot/src/Core/Sim/SimulationHost.cs` — passes `Research` as the new trailing `EnableChecksums` arg; updated two stale "NOT yet folded" doc comments.
- `godot/ProjectChimera.Sim.Tests/Golden/SimChecksumCoverageGuardTest.cs` — new `AssertResearchStoreFoldedIntoChecksum` (extended during review to cover a second faction, a second research index, and an idle-reset transition); re-pinned `KnownWorldState_ProducesPinnedV14Hash` with `ExpectedV14Hash = 0x386E5B42`.
- `godot/ProjectChimera.Sim.Tests/Golden/ResearchScenario.cs` (new) — a 2-level "armor_up" research on a "lab" building, built via `SimulationHost.Create`.
- `godot/ProjectChimera.Sim.Tests/Golden/ResearchGoldenTests.cs` (new) — run-twice/matches-committed/evolves/record tests; issues `StartResearch` via `OrderApplier.Apply`, ticks to completion, starts level 2.
- `godot/ProjectChimera.Sim.Tests/Golden/research-scenario.golden.txt` (new) + all 22 existing `*.golden.txt` — re-baselined for v14.
- `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` — `EmbeddedResource`/`None Remove` entries for the new golden file.
- `godot/ProjectChimera.Sim.Tests/Definitions/HeroProfilePersistenceTests.cs`, `CombatFeedbackProfileTests.cs`, `godot/ProjectChimera.Sim.Tests/Sim/SimResetTests.cs`, `godot/ProjectChimera.Sim.Tests/Meta/VersionStampConsistencyTests.cs` — four pre-existing pinned-`AlgoVersion` tripwire tests bumped 13→14 (the intended "forces a conscious re-pin" mechanism, not a side effect).
- `_bmad-output/implementation-artifacts/deferred-work.md` — appended DW-87, DW-88.

**Review findings breakdown:** 4 patches applied (1 high: the coverage test and golden only ever exercised Faction.Player1, so a hardcoded-index/mis-ordered per-faction loop bug would have shipped undetected — independently flagged by all three applicable review layers; 2 medium: no r>0 research-index coverage, and no coverage of the cancel-shaped idle-reset transition; 1 low: a self-conflicting doc comment). 2 findings deferred to `deferred-work.md` as pre-existing (DW-87: the new fold's per-faction array indexing has no bounds guard against `FactionRegistry` allowing more active factions than `ResearchStore`'s hardcoded 5-faction ceiling — an existing gap shared identically by `ResourceStore`'s fold since Story 1.3b; DW-88: `ResearchSystem.CompleteResearch`'s pre-existing O(n) full-world scan comment oversells its precedent). 5 findings rejected (the documented null-store-vs-real-empty-store shape mismatch; the bulk golden re-baseline being unreviewable by pure line-diff, inherent to the mechanism; an inherited scenario-construction `Assert.Equal` pattern; a suggested `StartedAtPosition` exclusion test the Verification Gap layer's own analysis found unnecessary; an unreachable defensive `Math.Min` guard suggestion).

**Verification performed:** `dotnet build godot.sln -c Debug` clean (0 errors), independently re-run by the orchestrating session both before and after the patch pass. Filtered runs (both passes): `~SimChecksumCoverageGuardTest` 3/3 (7/7 including `~ResearchGolden` before the patch pass), `~ResearchGolden` 4/4. Full Tier-1 suite (`dotnet test ProjectChimera.Sim.Tests`): 1365 passed / 1 pre-existing skip / 0 failed, both before and after the patch pass — no regressions at any point. `CHIMERA_GOLDEN_RECORD=1` re-baseline independently verified via `git diff --stat` (22 files, 6202 insertions/deletions, i.e. every line pair) plus a sampled full diff confirming only the `checksum_algo_version` header and per-tick hash values changed — no structural/format drift. All four review layers (Blind Hunter, Edge Case Hunter, Verification Gap, Intent Alignment) ran independently against the diff; the Verification Gap layer's primary finding (Player1-only coverage) was corroborated independently by the other two adversarial layers, giving it the highest confidence of any finding this pass.

**Residual risks:**
- `DW-87`/`DW-88` (see deferred-work.md) — both pre-existing gaps this story's review surfaced incidentally, not caused by this story.
- `DW-83`–`DW-86` (from Story 4.9, still open) — the 8-slot `ModifierStore` ring silent-drop, `MatchLifecycleController`'s untested bootstrap wiring, the max-health-delta burst-heal design question, and the missing replay-vs-live parity test for the research command family. None are this story's scope (checksum fold only), but all remain load-bearing context for future research work.
- The new golden scenario's Player2 is intentionally empty (no research authored) — cross-faction *golden-replay* coverage (as opposed to the coverage-guard's direct-mutation test, which now does cover it) remains a future nicety, not a gap: the coverage-guard test's differential mutation is the actual desync-detection proof; the golden's job is narrative byte-identical replay, which doesn't need two factions racing to make that point.

**Follow-up review recommended:** false — all four patches are narrow and test/comment-only (no production behavior, API, or data-model change); the one high-severity finding was a test-coverage gap, not a shipped defect, and its fix is a self-contained test extension, independently re-verified green. Volume and blast radius are well below this epic's established follow-up threshold (Story 4.8/4.9 each recommended follow-up at 8-9 patches with multiple mediums touching production code).

**Residual artifacts (not part of the committed diff, left in place):** this spec file's own trailing frontmatter update (`final_revision`/`status: done`, written after the commit that produced `final_revision`, so it could not itself be included in that commit) — mirrors the same self-reference pattern already present in `spec-4-8-researchdefinition-content-model-validation.md`'s and `spec-4-9-...md`'s frontmatter.
