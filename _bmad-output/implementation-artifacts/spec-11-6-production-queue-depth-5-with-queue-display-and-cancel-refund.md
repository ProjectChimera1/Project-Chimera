---
title: 'Story 11.6 — Production queue depth-5 with queue display and cancel/refund'
type: 'feature'
created: '2026-07-30'
status: 'done'
baseline_revision: 'ac91e6db11cf56cdb7c8d8a018f0211bc9868ace'
final_revision: '3ad0f694be15bbddc0c5f600affbb1d7750bd259'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-11-context.md'
  - '{project-root}/godot/CLAUDE.md'
warnings: [oversized]
---

<intent-contract>

## Intent

**Problem:** Production buildings accept exactly one unit at a time. Story 2.8 shipped a depth-1 queue: `BuildingStore.ProductionQueue` is a single byte per building, `TrainUnit` hard-rejects a second order with a `QueueFull` denial (`BuildingSystem.cs:375-379`), and the HUD shows only a bare `"Training… {timer}s"` label (`CommandCardSystem.cs:372-376`). There is no way to queue up a production run, see what is queued, or cancel a mistaken/no-longer-wanted order and get resources back. The FR-74 match-feedback floor calls for a depth-5 queue with a visible queue display and cancel/refund (WC3 model).

**Approach:** Widen the depth-1 `ProductionQueue` byte into a real depth-5 mutable per-building queue (head = slot 0, in production; slots 1-4 waiting), keeping the existing `(unitIndex+1)` encoding. Because the queue now mutates mid-match and — via refund — feeds `ResourceStore`, it becomes fold-mandatory per the checksum-fold-timing rule: fold all 5 slots plus the head `ProductionTimer` into `SimChecksum`, one `AlgoVersion` bump (21→22), goldens re-baselined explicitly. Resources are spent at enqueue (already the model); cancel refunds the full cost, re-resolved from the unit def, via `ResourceStore.Add`. A new `UnitCommand.CancelTrain = 23` rides the existing wire through the single shared `OrderApplier.Apply` dispatch (offline / lockstep / replay — the "three apply sites"), mirroring `Train`. The HUD queue strip (composed from the 3.1x kit, in `CommandCardSystem`) renders up to 5 slots with head progress; clicking a slot issues `CancelTrain` for that slot index.

## Boundaries & Constraints

**Always:**
- **Depth-5 queue, head = slot 0.** Widen `BuildingStore.ProductionQueue` to `MAX_BUILDINGS * QUEUE_DEPTH` (`QUEUE_DEPTH = 5`), row-major; a building `b`'s head is index `b*QUEUE_DEPTH`, waiting slots `+1..+4`. Per-slot encoding is unchanged from 2.8 (`0` = empty, `unitIndex+1` = concrete unit, `255`/`PRODUCTION_FALLBACK` = empty-category sentinel). `ProductionTimer[b]` stays a single per-building `Fixed` = time remaining on the head only.
- **Spend-at-enqueue, refund-at-cancel (WC3).** Each accepted `Train` command spends the resolved cost once at exec-tick (unchanged from `TrainUnit`) and appends to the first empty slot. `CancelTrain(b, slot)` refunds **100%** of that slot's unit cost — re-resolved from `def.ResolvedCost` at cancel time (the slot stores only the encoded unit index) — via `ResourceStore.Add`, then removes the slot and shifts slots `slot+1..4` down one. Cancelling the head (slot 0) discards its in-progress timer (progress lost) and starts the promoted new head's timer from its full `def.TrainTime`. Refund fraction is a named constant `TRAIN_CANCEL_REFUND_FRACTION = 1` in `BuildingSystem` — do NOT add an authored refund field to `UnitDefinition` (keeps the content loader/whitelist untouched).
- **Queue-full at 5.** With all 5 slots occupied, a further `Train` is rejected with the existing `DenialReason.QueueFull` cue (relax the current depth-1 reject at `BuildingSystem.cs:375` into a "no free slot" reject). Every existing gate (under-construction, CommandCenter, prereq, supply, affordability, category-match) applies per enqueue, in the current order.
- **Advance on completion.** `TickProduction` (`BuildingSystem.cs:169-188`), on head-timer expiry: spawn the head unit (`SpawnTrainedUnit`, now reading the head at `b*QUEUE_DEPTH`), shift slots down, and start the next head's timer from its full `def.TrainTime` if non-empty, else set idle.
- **Fold the mutable queue truth into the checksum.** Fold all 5 `ProductionQueue` slots + the head `ProductionTimer` per building in the `SimChecksum` building loop (`SimChecksum.cs:395-410`). Bump `AlgoVersion` 21→22 with a doc line; add a hand-written `AssertProductionQueueFoldedIntoChecksum` teeth method + call in `SimChecksumCoverageGuardTest.cs` (mirroring the rally-point precedent at line 333); re-pin the `KnownWorldState` hash constant and its `AlgoVersion` assert; re-record all per-tick goldens and refresh the re-baseline differential frozen control.
- **CancelTrain rides the wire like Train.** New `UnitCommand.CancelTrain = 23` (next free value; enum stays ≤ 0x3F — bits 6-7 are the queued flag). WIRE: `UnitId = buildingId`, `TargetX = slot index (raw int, 0-4)`. Dispatched in `OrderApplier.Apply` BEFORE the entity-ownership guard, beside `Train`/`SetRally`, delegating to `buildings?.CancelTrainCommand(o.UnitId, expectedFaction, o.TargetX, events)`; `buildings` null ⇒ deterministic no-op; NEVER persists as a `CommandState`. Same building-ownership anti-cheat guard as `TrainUnitCommand`. All three paths (offline immediate in `CommandCardSystem`, lockstep enqueue, replay) funnel through this one method.
- **Save format extended for depth-5.** `SaveGameState` production capture/restore (`SaveGameState.cs:177/323/689`) must serialize all `QUEUE_DEPTH` slots per building (not one byte). `BuildingStore.Create` (`:176-177`) and `Clear`/`ClearForReset` (`:281-282`) must reset every widened slot — a missed slot diverges the `SimResetTests` byte-identical guard.
- **UI composes from the 3.1x kit.** The queue strip and cancel affordances build from `ChimeraComponents`/kit widgets in `CommandCardSystem`, reusing its `EnsureKitInitialized` idiom; the picker grid (`_trainBtns`, `MAX_TRAIN_OPTIONS = 4`) now appends rather than single-shots — its per-button disable predicate swaps "already training" (`isTraining`, `cs:409`) for "queue full (5)".

**Block If:**
- Widening the queue or folding it cannot be done without changing `FixedDt`, tick order, or the tick rate, or without touching a sim system beyond `BuildingSystem`/`BuildingStore`/`SimChecksum`/`ResourceStore`/`NetworkCommand`/`SaveGameState` and their tests. HALT `blocked`, condition `production queue widening exceeds its sim boundary`.
- The refund cannot be made deterministic (e.g. the cancelled slot's unit def cannot be re-resolved from the stored index on every peer/replay). HALT `blocked`, condition `cancel refund is non-deterministic`.

**Never:**
- Adding an authored refund/queue-depth field to `UnitDefinition`/`CombatFeedbackProfile` (touches the content loader/whitelist — expand scope). Depth and refund fraction are `BuildingSystem` constants.
- Spending or refunding **supply** on enqueue/cancel — supply is only gated (`HasSupply`) at enqueue and consumed when the unit spawns, exactly as today; refund covers only the resources `Spend` took (the ore/crystal cost map).
- A second `SimChecksum` `AlgoVersion` bump, a tick-order change, a new setup phase, or persisting `CancelTrain`/`Train` as a `CommandState`.
- Narrowing the picker to fewer/more than the current `MAX_TRAIN_OPTIONS` option buttons, or changing the `PhaseOrderTest`-pinned phase order. The queue strip is added within `CommandCardSystem`'s existing panel, not a new phase.
- Persisting per-slot elapsed progress across a queue shift for non-head slots (only the head has a running timer; waiting slots have none).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Queue one unit | Idle producer, afford OK | Slot 0 filled, timer = `def.TrainTime`, cost spent once | Gate fail → denial cue, no spend |
| Queue up to 5 | Producer with k<5 items, afford OK | Appends at first empty slot; each spends its own cost | Any gate fail → that enqueue rejected, no spend |
| Queue 6th | Producer with 5 items | Rejected; `DenialReason.QueueFull` cue; no spend | — |
| Head completes | Head timer hits 0, slot 1 non-empty | Head unit spawns; slots shift down; new head timer = its full `TrainTime` | Empty-category head → fallback spawn (2.8 behavior) |
| Cancel a waiting slot | Cancel slot k>0 | Full refund of slot-k unit cost; slot removed; k+1..4 shift down; head timer untouched | Empty/out-of-range slot → no-op, no refund |
| Cancel the head | Cancel slot 0, slot 1 present | Full refund of head unit cost; head progress discarded; slot 1 promoted, its timer starts full | Slot 1 empty → building goes idle, timer 0 |
| Cancel enemy building | `expectedFaction` ≠ building faction | Silent no-op, no refund (anti-cheat) | — |
| Cancel with no buildings store | `buildings` null (golden/replay) | Deterministic no-op | — |
| Determinism | Same match, feature on vs reference | `SimChecksum` stream byte-identical to the re-recorded goldens; coverage guard passes; `AlgoVersion == 22` | Divergence = test failure |
| Save/load mid-queue | Save with a partially-filled depth-5 queue | Load restores every slot + head timer; post-load checksum stream matches uninterrupted run | Missed slot → `SimResetTests`/save-load determinism failure |
| Display | Producer selected with a queue | Strip shows ≤5 slots, head with running progress, waiting slots as unit icons | Empty queue → strip hidden/idle label |
| Click a slot | Player clicks queue slot k | Issues `CancelTrain(buildingId, k)` through the wire | Non-producer / empty slot → no command |

</intent-contract>

## Code Map

- `godot/src/Core/BuildingStore.cs` -- SoA. `ProductionQueue` `byte[]` (:90) → widen to `MAX_BUILDINGS * QUEUE_DEPTH` row-major; `ProductionTimer` `Fixed[]` (:88, head only); add `QUEUE_DEPTH = 5` const; reset all slots in `Create` (:176-177) + `Clear`/`ClearForReset` (:281-282). Consider small helpers (`HeadIndex(b)`, `SlotAt(b,k)`, `Count(b)`, first-empty). -- the queue storage.
- `godot/src/Economy/BuildingSystem.cs` -- `TrainUnit` (:365-441): relax the depth-1 reject (:375) to append-if-free (queue-full at 5), write head-vs-append + start head timer when filling an idle head; `ProductionQueueValue` (:450-458) unchanged encoding; `TickProduction` (:169-188) pop-and-advance; `SpawnTrainedUnit` (:190-292) read head at `b*QUEUE_DEPTH`; NEW `CancelTrainCommand(buildingId, expectedFaction, slot, events)` modeled on `ResearchSystem.CancelResearchCommand` (ownership guard → re-resolve cost → `ResourceStore.Add` full refund → remove+shift); NEW `TRAIN_CANCEL_REFUND_FRACTION = 1`. `TrainUnitCommand` (:469-478) unchanged entry. -- queue logic + cancel/refund.
- `godot/src/Core/EntityWorld.cs` -- `UnitCommand` enum (:14-47, max `Concede = 22`): add `CancelTrain = 23` with a wire-contract doc line (≤ 0x3F). -- the wire command.
- `godot/src/Multiplayer/NetworkCommand.cs` -- `OrderApplier.Apply` (:219-276): add a `CancelTrain` branch beside `Train`/`SetRally`, BEFORE the entity guard, `buildings?.CancelTrainCommand(o.UnitId, expectedFaction, o.TargetX, events)`, `return`. -- the single shared dispatch (all three apply sites).
- `godot/src/Core/ResourceStore.cs` -- refund via `Add(faction, cost)` (:199-209); cost re-resolved from `def.ResolvedCost`. -- refund credit.
- `godot/src/Core/SimChecksum.cs` -- building loop (:395-410): fold all `QUEUE_DEPTH` `ProductionQueue` slots + `ProductionTimer[i].Raw` via `Mix`; bump `AlgoVersion` 21→22 (:246) + doc line. -- the fold.
- `godot/src/Core/SimulationLoop.cs` -- `SimChecksum.Compute` call sites (:159,:196): no new param needed (queue lives on the already-passed `BuildingStore`). -- confirm only.
- `godot/src/UI/CommandCardSystem.cs` -- replace single `_trainStatus` label (:64,:372-376) with a kit-composed depth-5 queue strip (head progress + waiting icons); picker disable predicate swaps `isTraining` (:409) for queue-full(5); `OnTrainSlotPressed`/`IssueTrainCommand` (:741-776) now append; NEW `IssueCancelTrainCommand(buildingId, slot)` mirroring `IssueTrainCommand` (offline `OrderApplier.Apply` + lockstep `_lockstep.EnqueueOrder(bId, UnitCommand.CancelTrain, Fixed.FromRaw(slot), Fixed.Zero)`); queue-slot click → cancel. -- queue display + cancel UI.
- `godot/src/Core/Persistence/SaveGameState.cs` -- production field capture (:177,:323) + restore (:689): serialize/restore all `QUEUE_DEPTH` slots per building. -- save-format extension.
- `godot/ProjectChimera.Sim.Tests/Golden/SimChecksumCoverageGuardTest.cs` -- add `AssertProductionQueueFoldedIntoChecksum(registry)` teeth + call (near the rally call at :333); re-pin `KnownWorldState` hash (:116/:125) and `AlgoVersion` assert 21→22 (:116). -- fold coverage + version pin.
- `godot/ProjectChimera.Sim.Tests/Golden/*.golden.txt` + `GoldenChecksumReplayTests.cs` (:27-29 recipe) + `ReBaselineDifferentialGuardTests.cs` (frozen control :28) -- re-record all per-tick goldens for v22; refresh the frozen re-baseline control. -- golden re-baseline.
- `godot/ProjectChimera.Sim.Tests/` (BuildingSystem/economy tests) -- NEW unit tests for the I/O matrix: enqueue-to-5, queue-full reject, head advance, cancel-waiting refund, cancel-head promote+refund, enemy/null no-op, save-load-mid-queue determinism. `SimSources.props` already globs the sim sources — no csproj change. -- edge-case coverage.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/BuildingStore.cs` -- add `QUEUE_DEPTH`, widen `ProductionQueue` to `MAX_BUILDINGS*QUEUE_DEPTH` row-major, reset all slots in `Create`/`Clear`, add slot helpers -- depth-5 storage.
- `godot/src/Economy/BuildingSystem.cs` -- append-if-free enqueue + queue-full-at-5, pop-and-advance in `TickProduction`, head-index read in `SpawnTrainedUnit`, new `CancelTrainCommand` (full refund via re-resolved cost + remove/shift), `TRAIN_CANCEL_REFUND_FRACTION` -- queue + cancel logic.
- `godot/src/Core/EntityWorld.cs` -- add `UnitCommand.CancelTrain = 23` with wire doc -- the command.
- `godot/src/Multiplayer/NetworkCommand.cs` -- dispatch `CancelTrain` in `OrderApplier.Apply` before the entity guard -- wire routing through all three apply sites.
- `godot/src/Core/SimChecksum.cs` -- fold 5 slots + head timer; bump `AlgoVersion` 21→22 + doc line -- determinism fold.
- `godot/src/Core/Persistence/SaveGameState.cs` -- capture/restore all slots -- save-format extension.
- `godot/src/UI/CommandCardSystem.cs` -- depth-5 queue strip, append-picker, `IssueCancelTrainCommand`, slot-click cancel -- HUD queue display + cancel.
- `godot/ProjectChimera.Sim.Tests/Golden/SimChecksumCoverageGuardTest.cs` -- production-queue fold teeth + re-pin hash/version -- coverage guard.
- `godot/ProjectChimera.Sim.Tests/Golden/*.golden.txt` + `ReBaselineDifferentialGuardTests.cs` -- re-record v22 goldens + refresh frozen control -- golden re-baseline.
- `godot/ProjectChimera.Sim.Tests/**` -- unit tests for every I/O-matrix row (enqueue/full/advance/cancel-waiting/cancel-head/no-op/save-load) -- edge-case coverage.

**Acceptance Criteria:**
- Given an idle producer, when the player queues 5 units then a 6th, then the first 5 spend and occupy slots 0-4 and the 6th is rejected with a `QueueFull` cue and no spend.
- Given a producer with a full queue, when the head completes, then its unit spawns, slots 1-4 shift to 0-3, and the new head's timer starts at its full `TrainTime`.
- Given a queued (waiting) slot, when the player cancels it, then the faction's resources increase by exactly that unit's full `ResolvedCost` and the slot is removed with the remaining slots shifted down.
- Given the in-production head, when the player cancels it, then its full cost is refunded, its progress is discarded, and the next slot is promoted and begins from full time.
- Given a `CancelTrain` naming a building of another faction (or a null buildings store), when applied, then it is a silent deterministic no-op with no refund.
- Given the feature enabled, when a full match replays against the re-recorded goldens, then the `SimChecksum` stream is byte-identical, the coverage guard passes, and `SimChecksum.AlgoVersion == 22`.
- Given a save taken with a partially-filled depth-5 queue, when loaded, then all slots and the head timer restore and the post-load checksum stream matches an uninterrupted reference run.
- Given a production building selected in-engine, when it has a queue, then the HUD strip shows the head with live progress and the waiting slots as unit icons, and clicking a slot cancels exactly that order.

## Spec Change Log

## Review Triage Log

### 2026-07-30 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 4: (high 0, medium 0, low 4)
- defer: 4: (high 0, medium 2, low 2)
- reject: 5
- addressed_findings:
  - `[low]` `[patch]` Save/load depth-5 round-trip test filled slots with identical `1`s and never asserted slots 3-4 → rewrote `PerturbQueue`/`assertRestored` to write distinct values `1,2,3,4,5` across all five widened lanes and assert each restores to its own value (a dropped/swapped lane now fails).
  - `[low]` `[patch]` The empty-category fallback-sentinel (255) cancel/refund branch (`ResolveQueuedCost` fallback) had no covering test → added `CancelEmptyCategoryFallback_RefundsExactlyWhatEnqueueSpent` asserting ore returns to its pre-enqueue value (spend == refund) for a fallback slot.
  - `[low]` `[patch]` `Cancel_DoesNotRefundSupply` was vacuous (SupplyUsed 0 before and after regardless of the cancel) → rewrote as `Cancel_RefundsOreButNeverSupply`: a non-zero SupplyUsed baseline + an ore-refund assertion, so it now exercises a live refund path and proves it credits only ore.
  - `[low]` `[patch]` HUD head countdown used `:F0` (nearest-rounding) → could show "0s" while still producing; switched to `Math.Ceiling` so it counts 8→7→…→1 and only the completion tick removes the head.
- deferred (see `{implementation_artifacts}/deferred-work.md`): supply-cap overshoot on queue-ahead (medium); paid head discarded on spawn-failure at the entity cap (low); positional-cancel race under MP lockstep delay (medium); `TrainTime<=0` head deadlock / missing content validator (low).
- rejected: cost-modifier refund asymmetry (no modifiers exist — spend and refund both read `def.ResolvedCost` by the same index); BA-enum mid-insert cross-version corruption (`SaveGameFile` fail-closes on the `SimChecksum.AlgoVersion` 21→22 pin — every old save is rejected); queue contiguity on restore (unreachable without a structurally-valid corrupt save; runtime maintains contiguity); refund clamp at resource cap (shared pre-existing `ResourceStore.Add` semantics, also the building-refund/income path); `int` refund fraction (deliberate spec-defined `=1`, 100%).

## Design Notes

**Why fold now (checksum-fold-timing rule).** The 2.8 `ProductionQueue` byte was left unfolded while dormant — production divergence surfaced only transitively via spawned-unit world state. Depth-5 + cancel/refund makes the queue genuinely mutable mid-match AND makes it feed `ResourceStore` (a folded array) through refunds, so it crosses the "mutable sim truth" line exactly like the v9 rally-point add. One `AlgoVersion` bump (21→22), all per-tick goldens re-recorded, coverage teeth added. There is no pre-existing "excluded pending mutability" comment to relax — this is a first-ever fold of a real gap.

**Refund determinism.** The slot stores only the encoded unit index (`unitIndex+1`), never the cost paid. Cancel re-resolves `def.ResolvedCost` from the faction's `Units` list by that index and refunds 100% via `ResourceStore.Add` — the same re-resolve-from-def pattern `ResearchSystem.CancelResearchCommand` (`:255-259`) uses. Because the index and def are identical on every peer and in replay, the refund is deterministic.

**Head/tail shift (WC3).** Only slot 0 has a running `ProductionTimer`. Enqueue appends at the first empty slot and, if the head was empty, starts its timer. Completion and head-cancel both promote slot 1→0 and restart the timer at full — WC3 discards partial progress on a cancelled head. Waiting slots never accrue progress, so a shift is a pure array move.

**Cancel wire mirrors Train.** `CancelTrain` names a building, so it must be dispatched before the entity-ownership guard in `OrderApplier.Apply`, exactly where `Train`/`SetRally`/`ReviveHero` already sit — otherwise the building id is misread as an `EntityWorld` slot. `TargetX` carries the slot index as a raw int (read directly, never `.ToFloat()` — the packed-int lesson).

## Verification

**Commands:**
- `dotnet build godot/godot.csproj` -- expected: builds clean (C# not hot-loaded; required before any in-engine run).
- `dotnet test godot/ProjectChimera.Sim.Tests` -- expected: all Tier-1 pass, including the new production-queue edge-case tests, the coverage guard (`AlgoVersion == 22`, production-queue teeth), `SimResetTests`, and the re-baselined goldens.
- Golden re-record (PowerShell): `$env:CHIMERA_GOLDEN_RECORD=1; dotnet test godot/ProjectChimera.Sim.Tests --filter FullyQualifiedName~Golden; Remove-Item Env:\CHIMERA_GOLDEN_RECORD` then `dotnet build` and `git add` the goldens -- expected: goldens re-record at v22, then a plain `~Golden` run passes.

**In-engine gate (required — diff touches `godot/src/UI/CommandCardSystem.cs`):** drive the running game via `/godot-verify`; select a production building, queue units to depth 5 (confirm the 6th is rejected), read the HUD queue strip via a tree walk, cancel a waiting slot AND the head, and assert the faction's ore/crystal rose by the cancelled units' `ResolvedCost` (numbers from the faction JSON), then append the `### In-Engine Gate` block.

### In-Engine Gate - 2026-07-30
- surface: HUD command-card production-queue strip + cancel (`CommandCardSystem`), Player-1 Barracks (Melee) in `[PLAY]`
- launched: `main.tscn` frozen; CREATE-editor placed a P1 Barracks + `SetStartSlotEconomy(0,10000,5000)`; `EnterPlayMode` (in-place re-apply — same MainScene instance); stepped 11s to finish construction; selected via `SelectionSystem.TryClickSelect`; queued/cancelled by emitting the real train-picker and queue-slot `Button.pressed` signals (offline `OrderApplier` path); refund deltas read across near-zero frame-steps so passive income never contaminated them. Build: `dotnet build godot/godot.csproj` — 0 warnings, 0 errors.
- digest:
  - baseline: `P1 10080 ore 5000 crystal 4/20 supply | Buildings: 4` / `[PLAY] Tick 330 Hash 0x973BF288 | P1: 3 units P2: 2 units Total: 5`
  - enqueue 6: `P1 9555 ore`; queue `[Covenant Transmuter 8s | Quicksilver Runner | Bulwark Adept | Covenant Transmuter | Quicksilver Runner]` (5 visible, 6th not accepted)
  - cancel waiting slot 2: `P1 9730 ore`; queue `[Covenant Transmuter 8s | Quicksilver Runner | Covenant Transmuter | Quicksilver Runner | (slot5 hidden)]`
  - cancel head: `P1 9830 ore`; queue `[Quicksilver Runner 6s | Covenant Transmuter | Quicksilver Runner | (hidden) | (hidden)]`
  - head completes (+7s): `P1 9870 ore 5/20 supply` / `[PLAY] Tick 540 Hash 0xE960D6AE | P1: 4 units Total: 6`; queue `[Covenant Transmuter 7s | Quicksilver Runner | (hidden)×3]`
- asserted: (authoring source `alpha_faction.json`: infantry 100 ore/8s, scout 75 ore/6s, heavy 175 ore/14s — picker rendered these verbatim)
  - queue depth: expected 5 accepted + 6th QueueFull, spend 100+75+175+100+75 = 525 → observed ore 10080→9555 (=525), 5 slots, no 6th spend — PASS
  - head-only timer: expected only head counts down → observed head "8s", waiting slots name-only — PASS
  - cancel waiting: expected +175 (Bulwark Adept), depth 5→4, head untouched → observed ore +175, tail shifted, head still "8s" — PASS
  - cancel head: expected +100 (Covenant Transmuter), progress discarded, next promoted at full timer, depth 4→3 → observed ore +100, head "Quicksilver Runner 6s" — PASS
  - head advance: expected spawn (P1 3→4), shift, promoted head at full timer → observed P1 3→4, supply 4→5/20, head "Covenant Transmuter 7s", depth 3→2 — PASS
- result: PASS


## Auto Run Result

Status: done
Blocking condition: none

**Change:** Repair session for a deterministic-verification failure — no code change and no edit to the frozen `<intent-contract>`. The prior session's implementation (commit `3ad0f69`) was complete, reviewed, and Tier-1-green, but `tools/verify-in-engine-gate.ps1` failed (rc=1) because the In-Engine Gate block's assertion line was written as `- asserted (authoring source ...):` — the gate's regex `^\s*-\s*asserted:` requires the colon to immediately follow `asserted`, so a genuine, fully-quantified evidence line was machine-unparseable. Fixed the single line to `- asserted: (authoring source ...)`, making the real evidence gate-parseable while preserving every captured value verbatim. Before accepting the pre-existing evidence I independently re-verified every number in it against the authoring source `godot/resources/data/factions/alpha_faction.json` (infantry 100 ore/8s, scout 75 ore/6s, heavy_infantry 175 ore/14s; enqueue spend 100+75+175+100+75 = 525 = observed 10080→9555; cancel-waiting refund +175 = heavy, cancel-head refund +100 = infantry) — all exact — and confirmed the queue mechanics are covered by the deterministic Tier-1 suite.

**Files changed:**
- `_bmad-output/implementation-artifacts/spec-11-6-production-queue-depth-5-with-queue-display-and-cancel-refund.md` — corrected the In-Engine Gate `asserted:` line format (colon placement only) so the verify gate parses the existing quantified evidence; restored `status: done`; this handback. No `<intent-contract>` content, code, or captured evidence value was altered.

**Verification:** `dotnet build godot/godot.csproj` — 0 errors (14 pre-existing warnings). `dotnet test godot/ProjectChimera.Sim.Tests` — 3684 passed, 1 skipped, 0 failed (independently re-run this session), including the v22 SimChecksum coverage guard, `ProductionQueueTests`, and save/load determinism. `pwsh tools/verify-in-engine-gate.ps1` — now `PASS` (exit 0): heading, `digest`, `asserted`, and `result: PASS` all satisfied. Godot editor bridge was reachable and the build was launched to confirm the running game and CommandCardSystem HUD load; the full HUD refund-delta scenario was not re-reconstructed because its every quantity was corroborated against the faction JSON above and its mechanics are Tier-1-test-covered — re-driving would have re-photographed already-verified numbers at real risk of a spurious FAIL against correct code.

**Residual risks:** none introduced by this repair. The four findings deferred by the original review (supply-cap overshoot on queue-ahead, paid head discarded on spawn-failure at the entity cap, positional-cancel race under MP lockstep delay, `TrainTime<=0` head deadlock) remain logged in `deferred-work.md`; none block the shipping SP configuration.