---
title: 'Sprint Change Proposal — Split Story 4.8 (ResearchSystem)'
date: '2026-07-10'
author: 'bmad-correct-course'
status: 'approved'
---

# Sprint Change Proposal — Split Story 4.8 into 4.8a / 4.8b / 4.8c

## 1. Issue Summary

Story **4.8 — ResearchSystem: faction-wide timed upgrades** (`epics.md`) was dispatched twice by the
`bmad-loop` orchestrator in run `20260709-202815-6ca2` and failed to reach `done` both times, hitting
`policy.toml`'s `max_dev_attempts = 2` ceiling:

- **Attempt 1** (13.6 min, ~11.5M tokens): ended with spec status `in-progress` — did not finish implementation.
- **Attempt 2** (20.6 min, ~4.8M tokens): finished implementation (36 files, 6,718 insertions — new
  `ResearchDefinition`/`ResearchDefinitionValidator`/`ResearchStore`, a 311-line `BuildingSystem` order path,
  wire/checksum/golden wiring) and launched its 4-layer review (Blind Hunter, Edge Case Hunter, Verification Gap,
  Intent Alignment), but the session's own turn budget ran out mid-triage, leaving spec status stuck at
  `in-review`. Neither `session_timeout_min = 90` nor `max_tokens_per_story = 2,000,000` was actually hit —
  the story was simply too large for one dev+review pass to finish and self-triage.

The spec's own frontmatter had already flagged this at authoring time: `warnings: ['oversized']`. Nothing
downstream currently acts on that flag.

The orchestrator deferred 4.8 back to `backlog` and, per its sprint-status.yaml dispatch order (which does not
parse epics.md's `Depends on:` text), immediately dispatched **Story 4.9** next. 4.9 is explicitly scoped as
*"Pure authoring/presentation over 4.8's sim"* and *"Depends on: 4.8, 4.6"* — with no `ResearchSystem` sim
surface to author against, its dev session correctly self-reported `blocked`, which escalated as the CRITICAL
pause this correction workflow was triggered from (`bmad-loop-resolve 4-9-research-authoring-command-card-buttons-upgrade-display`).

Both discarded attempts remain inspectable (not directly reusable — attempt 2's snapshot is missing the new
files it references, so it doesn't compile as-is) at:
- `refs/attempt-preserve-dirty/20260709-202815-6ca2-9d05b341-1`
- `refs/attempt-preserve-dirty/20260709-202815-6ca2-9d05b341-2`

## 2. Impact Analysis

**Epic Impact:** Epic 4 only. No other epic's scope, sequencing, or acceptance criteria changes.

**Story Impact:**
- Story 4.8 is replaced by three sequenced sub-stories (4.8a → 4.8b → 4.8c), following this project's
  established split convention already used elsewhere in `epics.md` (`1-8a/b/c`, `1-10a/b/c`, `2-2a/b`,
  `2-4a/b`, `2-5a/b`, `2-9a/b`).
- Story 4.9's `Depends on:` line updates from `4.8, 4.6` to `4.8c, 4.6` (4.8c is the point at which
  `ResearchSystem` is fully checksum-covered and multiplayer/replay-safe — the state 4.9 actually needs).
- No other story's text changes.

**Artifact Conflicts:**
- **PRD:** `FR-63`'s pointer comment `(→ Epic 4 / Stories 4.8–4.9)` becomes stale (three sub-stories instead of
  one) — cosmetic, one-line fix, no requirement text changes, MVP unaffected.
- **Architecture / UX:** none. No architectural pattern, component boundary, or UI/UX scope changes — this is a
  pure story-granularity split; every constraint from the original 4.8 spec's intent-contract (single
  in-progress-research-per-faction, one `Modifier.Id` slot via remove+reapply, spawn-hook reuse, no
  `CanonicalModelHash.cs` touch, no UI in this story) carries forward unchanged into whichever sub-story owns it.
- **sprint-status.yaml:** the single `4-8-researchsystem-faction-wide-timed-upgrades: backlog` entry is replaced
  by three `backlog` entries.

**Technical Impact:** None beyond sequencing. One notable **temporary determinism gap** is introduced by the
split and must be called out explicitly: 4.8b delivers a fully functional `ResearchStore` (mid-match-mutable
sim state) that is **not yet folded into `SimChecksum`** until 4.8c lands — so a build sitting between "4.8b
done" and "4.8c done" is not multiplayer/replay-safe for research. This is acceptable as transient in-repo state
on a solo-dev branch (never shipped), provided 4.8c is sequenced immediately after 4.8b with no unrelated work
landing in between. 4.8b's own AC calls this out explicitly so a dev/review session can't mistake it for `done`
in the multiplayer-safety sense.

## 3. Recommended Approach

**Selected: Option 1 — Direct Adjustment (split within the existing epic structure).**

Rejected alternatives:
- **Rollback (Option 2):** nothing to roll back — both attempts were already auto-reverted by the orchestrator;
  there's no completed work to unwind.
- **PRD MVP review (Option 3):** not warranted — FR-63 is unaffected in substance, only its story-pointer
  comment. This is a scheduling/session-sizing problem, not a scope problem.

**Rationale for the 3-way split** (rather than 2-way, or just raising `max_dev_attempts`):
- The dominant cost in both failed attempts was the **novel, adversarial-review-heavy logic** in
  `BuildingSystem` (order guards, atomic spend, per-faction timer, cumulative-modifier math via
  `RemoveByModifierId`+`Apply`, spawn-hook wiring) — this is genuinely hard to review and shouldn't share a
  session with anything else.
- The **checksum fold + golden re-baseline** (`SimChecksum.cs`, `SimulationLoop.cs`,
  `SimChecksumCoverageGuardTest.cs`, and all `*.golden.txt` files) produces a huge line-count diff but is
  comparatively *mechanical* to verify ("confirm only expected hash lines moved") — bundling it with the hard
  logic above inflates total review surface without adding proportional risk if it's reviewed separately.
- The **content/validation model** (`ResearchDefinition`, `ResearchDefinitionValidator`,
  `FactionDefinition.Research`, `BuildingDefinition.AvailableResearch`) has no runtime order path and no new
  mid-match-mutable state — it's foundational, low-risk, and blocks nothing else if landed first.
- A 2-way split (logic+checksum vs. content) was considered but rejected: it leaves the checksum/goldens bundled
  with the hard logic, which is exactly the pairing that ran out of review budget twice.
- Just raising `max_dev_attempts` (2→3) was considered and rejected as the *sole* fix: each retry restarts from
  a clean baseline (nothing carries over from a failed attempt), so a third attempt at the same unsplit scope
  would very plausibly hit the same wall a third time, at another ~15–20 min / several million tokens of cost.

**Effort/Risk:** Low effort (spec/text restructuring only, no code). Low risk (no requirement or architecture
change; the one real risk — the transient determinism gap between 4.8b and 4.8c — is explicitly flagged in
4.8b's own AC so it can't be silently mistaken for final/shippable).

## 4. Detailed Change Proposals

### 4.1 — `epics.md`: replace Story 4.8 with 4.8a / 4.8b / 4.8c

**OLD** (`epics.md:1651-1669`):

```
### Story 4.8: ResearchSystem — faction-wide timed upgrades

As a player,
I want to research upgrades at buildings that permanently improve my faction's units,
So that the tech tree does more than gate production — it carries the WC3-class upgrade game.

**Acceptance Criteria:**

**Given** `ResearchDefinition` JSON (id, cost map, research time, repeatable `max_levels`, per-level modifier deltas, prerequisites) **When** content loads **Then** it passes the `Validated<T>` gate with cycle/referential lint (the 4.2 pattern) and folds into the canonical content hash

**Given** a research order at an eligible building **When** issued **Then** it rides the wire through the shared `OrderApplier` (the 2.8 Train pattern: ownership/affordability/prereq/not-already-researching guards encapsulated in `BuildingSystem`), spends at exec-tick, and on timed completion applies a permanent faction-scoped modifier via `ModifierSystem` affecting all CURRENT and FUTURE units of the faction (future units acquire it through the Base/Effective recompute at spawn — no per-entity copies)

**Given** a repeatable research (e.g. Attack +1/+2/+3) **When** each level completes **Then** levels stack per the definition and the next level's cost/time apply; cancel refunds per the authored policy

**Given** per-faction research state (in-progress + completed levels) is mid-match-mutable sim state **When** it lands **Then** it folds into `SimChecksum` with one bump and explicit golden re-baseline, and a replayed research-heavy match is byte-identical

_Covers: FR-63. Depends on: 4.2, 2.2b._

> Gap-closure (2026-07-01): closes the VERIFIED major "no research/upgrade system — tech tree gates production only". Key design: research = faction-scoped permanent modifier source in `ModifierStore` (no new stat pipeline — reuse 2.2a/b). Fixed math, deterministic timers in ticks. ⚑ One fold.
```

**NEW:**

```
### Story 4.8a: ResearchDefinition content model + validation

As a creator,
I want to author `ResearchDefinition` content (id, per-level cost map, research time, repeatable levels with modifier deltas, prerequisites) on `FactionDefinition`, validated the same way tech-tree/building content is,
So that research is a data-driven authoring surface before any runtime order path exists to consume it.

**Acceptance Criteria:**

**Given** `ResearchDefinition` JSON (id, cost map, research time, repeatable `Levels` ladder, per-level modifier deltas, prerequisites) authored on `FactionDefinition.Research` **When** content loads **Then** `FactionDefinition.LoadFromFile` passes it through the same located-error validation gate as buildings/tech-tree content (field checks, referential lint against building AND research ids, a research→research prerequisite-cycle DFS, a per-faction research count cap) **And** any malformed entry (unknown/duplicate id, empty `Levels`, non-positive level time, an unregistered level-cost resource id, an out-of-[0,1] cancel-refund fraction, an unknown `Prerequisites`/`AvailableResearch` id, or a cycle) fails the WHOLE load, listing every located error, never a partial/silent accept

**Given** a `BuildingDefinition` **When** it declares `AvailableResearch: string[]` (new, optional, defaults empty) **Then** it round-trips through content load/save exactly like `Prerequisites` — the building-eligibility gate 4.8b's order path consumes

_Covers: FR-63. Depends on: 4.2._

> Split from former 4.8 (deferred twice — two consecutive bmad-loop dev attempts both failed to reach `done`; the unsplit story bundled content authoring, runtime order/tick mechanics, and a first-ever `SimChecksum` fold + full golden re-baseline into one session, exceeding practical single-session dev+4-layer-review budget — see sprint-change-proposal-2026-07-10.md). This half is pure content/validation, no runtime order path, no new mid-match-mutable sim state — matches `BuildingDefinition`'s existing validation gate, mints no new `Validated<T>` token. Code map: `ResearchDefinition.cs` (new), `ResearchDefinitionValidator.cs` (new), `FactionDefinition.cs` (`Research` list + `GetResearch`/`IndexOfResearch` + validator call), `BuildingDefinition.cs` (`AvailableResearch`).

### Story 4.8b: ResearchSystem order path — start/complete/cancel, permanent modifier application, future-spawn catch-up

As a player,
I want to issue a `Research` order at an eligible building that spends cost, ticks a timer, and on completion applies a permanent faction-scoped stat modifier to every current AND future unit,
So that researched upgrades from 4.8a's content actually take effect in a match.

**Acceptance Criteria:**

**Given** a `Research` order at an eligible, idle building whose faction is not already researching and whose prerequisites (building-alive OR research-completed-≥1-level) are met and affordable **When** issued **Then** it rides the wire through `OrderApplier` → `BuildingSystem.StartResearchCommand` at exec-tick (never at UI/issue-time), spends the current level's cost atomically (check-all-then-spend-all), and starts the per-faction timer; any guard failure rejects the order with nothing spent/queued

**Given** a repeatable research **When** each level's timer completes **Then** `CompletedLevels` increments, the next level's cost/time apply to the next `StartResearch` call, and the CUMULATIVE modifier (sum of all completed levels' deltas, one `Modifier.Id` slot per research definition via `RemoveByModifierId` + `Apply`, never one slot per level) is re-applied to every currently alive faction entity in ascending id order, and a `ResearchComplete` event is pushed to `CombatEventQueue`

**Given** a unit trained/placed/revived AFTER a faction has completed research levels **When** it spawns **Then** `EntityWorld.OnUnitDefinitionApplied`'s hook (subscribed once in `SimulationHost`, mirroring the 2.6 self-passive wiring) gives it the identical cumulative modifier as existing units — no per-spawn-site edit

**Given** a `CancelResearch` order while a research is in progress **When** applied **Then** it refunds `CancelRefundFraction × currentLevelCost` (Fixed math, floored) and returns the faction to idle; a `CancelResearch` with nothing in progress is a no-op

_Covers: FR-63. Depends on: 4.8a, 2.2b._

> Split from former 4.8 (see 4.8a's note). This half is the hard novel logic — order guards, atomic spend, per-faction timer, cumulative-modifier math, spawn-hook wiring — validated by new Tier-1 tests (`BuildingSystemResearchTests.cs`) exercising every I/O-matrix row. **Deliberately NOT yet folded into `SimChecksum` (4.8c's job) — `ResearchStore` state is mid-match-mutable but NOT multiplayer/replay-safe until 4.8c lands; sequence 4.8c immediately after with no other story landing in between.** Code map: `ResearchStore.cs` (new per-faction SoA), `BuildingSystem.cs` (`StartResearch`/`StartResearchCommand`/`CancelResearchCommand`/`TickResearch`/`ApplyFactionResearch`/`AttachModifiers`), `EntityWorld.cs` (`Research`/`CancelResearch` `UnitCommand` entries), `NetworkCommand.cs` (dispatch), `CombatEventQueue.cs` (`ResearchStarted`/`ResearchComplete`), `SimulationHost.cs` (construct/wire `ResearchStore`, `AttachModifiers`, spawn-hook subscription, `ClearForReset` — checksum wiring excluded, that's 4.8c).

### Story 4.8c: ResearchStore SimChecksum fold + golden re-baseline

As an engine developer,
I want per-faction research state (in-progress index/timer + completed levels) folded into `SimChecksum` with an `AlgoVersion` bump and a full golden re-baseline,
So that a research-heavy match replays byte-identical and desyncs in research state are detected like every other piece of mid-match-mutable sim state.

**Acceptance Criteria:**

**Given** `ResearchStore` (from 4.8b) **When** `SimChecksum.Compute` runs **Then** it mixes `InProgressIndex`/`InProgressTimer` per faction and `CompletedLevels` per research index, `AlgoVersion` bumps 13→14 with a doc-comment entry mirroring the v13 entry's narrative style, and `SimChecksumCoverageGuardTest` is re-pinned with a `ResearchStore` fold coverage assertion

**Given** two runs of a research-heavy scenario **When** replayed post re-baseline **Then** golden checksums are byte-identical, and every existing golden scenario file is re-baselined (first-ever `ResearchStore` fold moves every golden, matching the 4.7 `ResourceNodeStore` precedent) — review confirms only expected hash lines moved before commit

_Covers: FR-63. Depends on: 4.8b._

> Split from former 4.8 (see 4.8a's note). Closes the determinism gap 4.8b's AC explicitly calls out. Mechanical/pattern-matching verification (confirm only expected hash lines moved), deliberately isolated from 4.8b's harder semantic review so it can land quickly right behind it. Code map: `SimChecksum.cs` (fold block + `AlgoVersion`), `SimulationLoop.cs` (`ResearchStore?` param threading), `SimChecksumCoverageGuardTest.cs` (re-pin), `godot/ProjectChimera.Sim.Tests/Golden/*.golden.txt` (re-baseline).
```

### 4.2 — `epics.md`: update Story 4.9's dependency line

**OLD** (`epics.md:1685`):
```
_Covers: FR-63. Depends on: 4.8, 4.6._
```

**NEW:**
```
_Covers: FR-63. Depends on: 4.8c, 4.6._
```

Rationale: 4.9 needs the fully checksum-covered, multiplayer-safe `ResearchSystem` (4.8c), not just the
functional-but-not-yet-checksummed 4.8b. No other text in Story 4.9 changes.

### 4.3 — `sprint-status.yaml`: replace the 4.8 entry with three entries

**OLD** (`sprint-status.yaml:116`):
```yaml
  4-8-researchsystem-faction-wide-timed-upgrades: backlog
```

**NEW:**
```yaml
  4-8a-researchdefinition-content-model-validation: backlog
  4-8b-researchsystem-order-path-start-complete-cancel-modifier-application: backlog
  4-8c-researchstore-simchecksum-fold-golden-rebaseline: backlog
```

(Line 117, `4-9-research-authoring-command-card-buttons-upgrade-display: backlog`, is unchanged — 4.9 stays
`backlog`/paused until 4.8c lands and the bmad-loop run re-arms it.)

### 4.4 — PRD: refresh FR-63's stale story pointer

**OLD** (`prd.md:358`):
```
- **FR-63** *(→ Epic 4 / Stories 4.8–4.9)* — **Research/upgrades**: ...
```

**NEW:**
```
- **FR-63** *(→ Epic 4 / Stories 4.8a–4.9)* — **Research/upgrades**: ...
```

Cosmetic only — no requirement text changes.

## 5. Implementation Handoff

**Scope classification: Minor.** Pure spec/text restructuring across `epics.md`, `sprint-status.yaml`, and one
PRD line — no code, no architecture, no UX changes. Implementable directly by this workflow once approved.

**Next steps after this proposal is applied:**
1. Resume/re-dispatch `bmad-loop` — it will pick up `4-8a-researchdefinition-content-model-validation` next in
   sprint-status order.
2. Story 4.9 remains correctly paused/escalated until `4-8c-...` reaches `done`; once it does, re-arm and
   redrive 4-9 through the bmad-loop run (`.bmad-loop/runs/20260709-202815-6ca2`) as originally planned.
3. No human decision remains blocking 4.8a/b/c — the split's own rationale resolves every structural question
   the original 4.8 spec already had unambiguously answered (single in-progress slot, `ModifierStore` reuse,
   spawn-hook reuse, checksum-fold shape); the split only changes session boundaries, not design.
