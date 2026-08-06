---
title: 'Story 15-22: Phase C — batched golden re-baseline (SimChecksum AlgoVersion 23 → 24)'
type: 'batch-rebaseline'
created: '2026-08-06'
status: 'ready-for-dev'
baseline_revision: 'b5bcb1b1'
baseline_tests: '6050 passed / 0 failed / 1 skipped (Windows, verified 2026-08-06 at b5bcb1b1)'
integration_branch: 'rebaseline/phase-c'
review_loop_iteration: 0
followup_review_recommended: false
context: ['{project-root}/godot/CLAUDE.md', '{project-root}/_bmad-output/planning-artifacts/sprint-change-proposal-2026-08-06.md']
goldens: 'moves — this IS the re-record window. AlgoVersion 23 → 24, one record run, one commit.'
warnings:
  - 'The stock chimera-dw-burndown implement prompt FORBIDS golden movement. This story runs it in `rebaseline: true` mode, which replaces that rule and adds a serial Re-record phase. Never run this story on the default mode — every bundle would return success=false at step 7.'
---

<intent-contract>

## Intent

**Problem.** Seventeen ledger entries are each individually correct-and-blocked: the fix is known and
agreed, but landing it moves a committed `SimChecksum` golden, and the burn-down track forbids that by
construction. They have accumulated behind the same ~10-minute re-record. Phase B (merged `ac645a98`)
proved the batch shape works; this is the second window.

**Approach.** Land every correction with goldens left RED, then re-record **once**, serially, at the end,
in one commit. The proof that the re-record is honest is not a green suite — a green suite over
freshly-recorded goldens cannot see a defect baked into those goldens. The proof is three independent
gates that do not move with the hash:

1. **`SimChecksumCoverageGuardTest`'s pinned known-state hash must NOT move.** No bundle in this batch
   adds, removes, or reorders anything the checksum folds — every one of them changes folded *values*,
   not the folded *set*. If that pin moves, someone changed the fold and this window is the wrong
   vehicle. Hard stop.
2. **The re-baseline differential guard must stay green on its existing frozen control**
   (`rebaseline-guard-frozen-v22-formation-separation.golden.txt`, pinned FNV `0x6864F671`). See
   *Control analysis* below for why the Phase B control survives Phase C unchanged.
3. **Every moved golden must be attributable to a named DW id.** A golden that moves with no
   attributable cause is a defect in a fix, not an expected movement.

**Why AlgoVersion still bumps.** Phase C changes no fold, so the bump is a **re-record generation
marker**, not a fold change — the first entry in that constant's history that is. Without it a Phase-B
golden and a Phase-C golden both stamp `checksum_algo_version: 23` while holding different bytes, and
nothing distinguishes them. Record that explicitly in the v24 doc entry, because every prior entry
documents a fold and a reader will otherwise assume this one does too. Note `SimChecksum.AlgoVersion`
has **no `godot/src` consumer** — it is not in the MP handshake (that is `RulesetHash` +
`CanonicalModelHash.AlgoVersion`), so the bump is a labelling act with test-pin consequences only.

## Membership — 17 entries closed

**12 code corrections** (each moves goldens; none changes the fold):

| DW | Fix | Ruling source |
|---|---|---|
| DW-548 | Surface trigger-phase kills to the `unit_dies` source on the following tick | ledger `decision:` 2026-08-04 |
| DW-549 | Loosen the alive gate so a never-prev-alive slot's T+1 recycle-die surfaces | ledger `decision:` 2026-08-04 |
| DW-570 | Derive the flow-field obstacle stamp from the building footprint, not a fixed 3×3 | ledger `decision:` 2026-08-04 |
| DW-658 | Refund the razed producer's queued orders and zero `ProductionQueue`/`ProductionTimer` at `Destroy` | ledger `decision:` 2026-08-05 |
| DW-659 | Wire `Modifiers.RecomputeEffectiveStats` as a third `OnUnitDefinitionApplied` subscriber | ledger fix direction |
| DW-664 | Gate `TickNonCombatant`'s order-wipe on `BaseAttackDamage == 0`, not the effective stat | ledger fix direction |
| DW-674 | Give `DeathLog` the lossless-growth treatment DW-616 applied to `DeathFeed` | **this spec** (§ Rulings) |
| DW-678 | Skip the modifier install entirely when the built modifier is all-zero | **this spec** (§ Rulings) |
| DW-766 | Make the "drained at the checksum boundary" invariant TRUE rather than restating it | **this spec** (§ Rulings) |
| DW-803 | Skip/reset the walk-stall probe when the computed step is zero-length | ledger closure text |
| DW-837 | Delete the `ActiveCount < 3` guard — total wipeout always loses, any faction count | Alec, correct-course 2026-08-06 |
| DW-838 | Delete the `HasLiveCommandCenter` term from the below-threshold raze branch | **Alec, 2026-08-06** (this session) |

**3 in-window riders** — they touch golden files or shipped content but are not sim corrections:

- **DW-514** — clean the committed editor-drag residue in shipped `alpha_map_01.json` and reconcile it
  with `BuildFallbackMirror`. Moves **`CanonicalModelHash`**, a different mechanism from the
  `SimChecksum` bump. Account for it separately.
- **DW-554** — narrow `MfHeader` (and the committed golden's header line) from `~MultiFaction` to
  `~MultiFactionGolden` so an N=4 re-record stops silently also re-recording the N=8 golden.
  **This edits a golden's comment header; it does not move a golden.** It must land BEFORE the record
  run, or the record run itself will destroy the N=8 cross-process pin.
- **DW-839** — correct `AiActiveScenario`'s header comment: the AI takes the raze path there, it never
  launches the wave the comment describes. Free.

**2 closed with no code change** — recorded decisions held (Alec, 2026-08-06, this session):

- **DW-512** — separation query's 32-neighbour over-cap. `decision: 2026-08-04 Keep byte-identical`
  stands. It is deterministic across peers, so it is a crowd-fairness nicety and not a desync; changing
  steering is a game-feel change wanting a playtest, not a correction batch.
- **DW-647** — wall-slide retains the full single-axis displacement. `decision: 2026-08-05 Keep current
  all-or-nothing slide` stands. Same reasoning.

Close both `status: done` with a resolution line recording that the decision was **re-confirmed with the
window open** — i.e. they were not deferred again for cost reasons.

**1 entry LEAVES the batch:**

- **DW-775** (entity-id ABA — a slot recycled into a different hostile faction) → **its own story,
  `15-23`.** Ruled by Alec 2026-08-06. Its own text says the real close-out "needs its own bundle, not a
  bolt-on", and even the narrow version (a generation stamp beside the two held ids) adds **new folded
  state** — which would turn Phase C from a value-only re-record into a genuine fold change and
  invalidate gate (1) above. Behaviour is already pinned by two explicit tests
  (`HeldAutoTarget_RecycledIntoAnotherEnemy_KeepsFiring`,
  `Projectile_PrimaryTargetRecycledIntoAnotherEnemy_StillDetonates`), so nothing regresses while it waits.

## Rulings made in this spec (not previously recorded)

**DW-674 — grow `DeathLog` losslessly; do not merely pin the fallback.** The entry offers two closures:
pin the flags-diff fallback equivalence with a test, or apply DW-616's lossless-growth treatment. Take
the growth. The fallback argument is the weaker one by the entry's own admission — it "silently breaks
for a same-tick die-recycle-die slot, which is exactly the case the log was added to cover" — so pinning
it documents a loss class instead of closing it. `DeathLog` is the primary `unit_dies` source and
`unit_dies` triggers mutate folded state, so an overflow drop is a folded consequence, not a
presentation one. Growth is golden-neutral in practice (no shipped scenario reaches 256 deaths in one
tick); add a Tier-1 regression that a >256-death tick surfaces every death.

**DW-678 — skip the install when the built modifier is all-zero.** No design trade-off exists here: an
empty modifier consumes one of eight `EffectCaps.MaxModifiersPerEntity` ring slots on every living unit
and every future spawn, for exactly zero effect, actively worsening the starvation DW-83/DW-623/DW-625
are about. Skip it. This changes `ModifierStore` contents for already-shipped content, so goldens move —
which is what this window is for.

**DW-766 — make the invariant true, do not weaken the claim.** The entry floats "drop the 'provably
drained' claim and state the real invariant" as probably better. Reject that: `DeathFeed.cs`'s type doc
and `SimChecksum.cs:198`/`:601` all cite *provably drained* as the reason the feed is excluded from the
fold. Weakening the claim leaves mutable state outside the desync detector permanently. Instead ensure
the feed is genuinely empty at the checksum boundary — the residue must still be credited **in the same
tick** (so hero XP does not land a tick late), and the drain must sit after the LAST producer, which is
`ScenarioDirector` at index [15], not merely after `ItemSystem` at [10]. Add an end-of-tick assertion so
a future producer registered past the drain fails loudly instead of silently re-opening the hole. This
moves folded `HeroStore.Xp` timing; the window absorbs it.

## Control analysis — the Phase B control survives, do not re-pick it

`FormationSeparationScenario` is 7 Player-1 units with **zero attack damage**, no buildings, no
gatherers, no triggers (`new ScenarioData()`), no research, no items, no heroes and no deaths. Checked
against every member of this batch:

| Entry | Perturbs the control? | Why |
|---|---|---|
| DW-548/549/674 | No | no deaths, no triggers |
| DW-570 | No | no buildings → `MarkBuildingCells` is a no-op |
| DW-658 | No | no buildings |
| DW-664 | No | every unit is a *permanent* non-combatant (`BaseAttackDamage == 0`), so the Base-gated and Effective-gated arms agree |
| DW-678 | No | no research |
| DW-766 | No | no deaths, no heroes, no items |
| DW-803 | No | no gatherers |
| DW-837 | No | the scenario runs `WinPresetKind.None` (built-in), so the `default:` arm the guard sits in never executes |
| DW-838 | No | Player2 is empty — no raze-capable units — so `ScoreRazeBuildings` scores 0 either way |
| DW-659 | **The one to watch** | it fires on every def-based spawn, including this scenario's. The entry itself flags "a spawn path that sets `Effective*` before the mapper could not be ruled out". With no modifiers installed the recompute is `Base + 0`, so it should be byte-identical — **and this guard is exactly the instrument that proves it.** |

So: **keep the existing control file and its pinned FNV untouched.** If the guard fires, that is a real
finding about DW-659 (or about a fix that reached further than its bundle claimed) — diagnose it. Do
**not** re-freeze the control; that is precisely how this gate was silently defeated between stories 11.6
and Phase B.

## Boundaries & Constraints

**Always:**
- Simulation code stays pure C# — no `using Godot;`, SoA arrays, `Fixed` math in anything the checksum
  covers, entities processed by ascending id. New per-unit SoA fields go through
  `EntityWorld.ApplyUnitDefinition`.
- Every bundle adds regression coverage that would FAIL without its fix, in
  `godot/ProjectChimera.Sim.Tests/`, Godot-free.
- Implement bundles leave goldens RED and report which golden tests they moved and why. The single
  serial Re-record phase is the only thing that touches a `*.golden.txt` payload or
  `SimChecksum.AlgoVersion`.
- Re-record on **Windows**, so the float-AI `ai-active` golden carries a current header instead of going
  stale ([[determinism-gate-ai-active-golden]]).
- Diff recorded goldens with `grep -v '^#'` — the recorder rewrites the `checksum_algo_version` header on
  **every** file, so all 31 show as modified whether or not they really moved.
- Verify with `--logger trx`. The console logger truncates its failure list (it showed 6 of 26 in
  Phase B).
- Stage by path. `git add -A` sweeps in Godot-generated `.uid` sidecars and automation's `Snapshot.md`
  date bump.

**Block If:**
- **`SimChecksumCoverageGuardTest`'s pinned known-state hash moves.** That means the fold set changed;
  this window is scoped to value movement only. HALT and escalate.
- **The re-baseline differential guard fires** on the frozen control. HALT. Diagnose which fix perturbed
  a scenario carrying none of its state. Never re-freeze the control to make it pass.
- **`CanonicalModelHash` or `StartStateHash` moves for any reason other than DW-514.** DW-514 is the only
  shipped-content edit in the batch; any other movement in those families is unexplained.
- **A golden moves that no DW id in this batch explains.** Attribution is the deliverable, not a
  formality.
- A bundle needs new folded state to do its job. Stop and re-home it — that is a different re-baseline.

**Never:**
- Do NOT edit `deferred-work.md`, `sprint-status.yaml`, or `epics.md` from an implement bundle.
  Bookkeeping is a later serial phase; parallel agents editing one ledger is a guaranteed conflict.
- Do NOT run `git stash` in any form. The stash stack is shared across every worktree of this repo and
  holds real unmerged work.
- Do NOT re-record, hand-edit, or overwrite `rebaseline-guard-frozen-v22-formation-separation.golden.txt`.
- Do NOT bring DW-775, DW-512 or DW-647 back into scope.
- Do NOT re-record from WSL or Linux — `ai-active` is Windows-only float AI.

</intent-contract>

## Bundles

Ten bundles, chunked 4 / 4 / 2 (the 6-core / 16 GB ceiling — every implement agent runs a build plus the
full suite). Worklist: `.claude/workflows/dw-worklist-15-22.json`.

| # | Bundle | DW ids | Primary files |
|---|---|---|---|
| 1 | `unitdies-emission-horizon` | 548, 549, 674 | `Core/ScenarioDirector.cs`, `Core/DeathLog.cs` |
| 2 | `flowfield-building-footprint` | 570 | `Navigation/FlowFieldSystem.cs` |
| 3 | `production-destroy-refund` | 658 | `Core/BuildingStore.cs`, the `CancelTrainCommand` refund path |
| 4 | `modifier-effective-stat-remirror` | 659 | `Core/EntityWorld.cs`, `Effects/ModifierSystem.cs`, `Core/Sim/SimulationHost.cs` |
| 5 | `deathfeed-drain-invariant` | 766 | `Core/Sim/SimulationHost.cs`, `Heroes/HeroXpSystem.cs`, `Core/DeathFeed.cs` |
| 6 | `research-empty-modifier-skip` | 678 | `Economy/ResearchSystem.cs` |
| 7 | `gather-walk-stall-zero-step` | 803 | `Economy/GatheringSystem.cs`, `Navigation/CheckedStep.cs` |
| 8 | `combat-order-preservation` | 664 | `Combat/CombatSystem.cs` |
| 9 | `win-and-ai-dead-guards` | 837, 838 | `Core/WinConditionSystem.cs`, `AI/AiOpponentSystem.cs` |
| 10 | `golden-window-riders` | 514, 554, 839 | `resources/data/scenarios/alpha_map_01.json`, `Golden/MultiFactionGoldenTests.cs`, `Golden/AiActiveScenario.cs` |

Bundles 4 and 5 both edit `SimulationHost.cs` and are deliberately placed in **different chunks** so the
second merges onto the first's landed result rather than racing it. Bundle 10 must merge **before** the
Re-record phase (DW-554 narrows the record filter; DW-514 changes shipped content the record then reads).

## Tasks & Acceptance

**Execution — implement phase (parallel, isolated worktrees):**
- Each agent resets to the run base, reads its bundle from the worklist and its `### DW-<id>:` blocks
  from the ledger, verifies each entry against current code before fixing it, implements, adds failing-
  without-the-fix coverage, and gates on `dotnet build` + the full Tier-1 suite in its **own** worktree.
- Golden tests are expected RED. Every other test must be green. Each moved golden is reported with the
  reason it moved.
- A lone `CanonicalModelHashPerfTests…StaysUnderTheRegressionCeiling` failure that passes on an isolated
  `--filter` re-run is the documented CPU-contention flake, not a regression.

**Execution — re-record phase (serial, once, Windows, integration checkout):**
1. Bump `SimChecksum.AlgoVersion` 23 → 24 and add its doc entry, stating plainly that v24 is a
   **re-record generation marker with no fold change** and listing the 12 DW ids whose value movement it
   labels.
2. Update every pinned site: `Assert.Equal(23, SimChecksum.AlgoVersion)` in `CombatFeedbackProfileTests`,
   `HeroProfilePersistenceTests`, `ScenarioDataMapPropertiesTests`, `ObjectiveStateTests`,
   `SimChecksumCoverageGuardTest`, `SaveLoadTests`, `SimResetTests`, plus
   `VersionStampConsistencyTests.ExpectedSimChecksumAlgoVersion`. Several carry a trailing `// v23 = DW-78 …`
   comment — `AlgoVersionPinCommentHygieneTests` fails any pin comment claiming a version below the
   current constant, so each must be brought current or deleted (deleting is preferred; the rationale
   lives once, on the constant's XML doc).
3. **Verify `SimChecksumCoverageGuardTest`'s known-state hash did NOT move.** Block-If if it did.
4. **Run the differential guard BEFORE recording.** Block-If if it fires.
5. Record: `CHIMERA_GOLDEN_RECORD=1 dotnet test …` → `dotnet build` (refreshes embedded copies) → full
   suite with `--logger trx`.
6. Produce the **movement ledger**: for each of the 31 goldens, `moved` / `header-only`, and for each
   moved one the DW id that explains it. Any unexplained movement is a Block-If.
7. Stage by path; one commit.

**Acceptance Criteria:**
- Given the 10 bundles, when each is implemented in isolation, then every non-golden test is green in
  that worktree and every red golden is reported with an attributed cause.
- Given all merges, when the re-record phase runs on Windows, then `SimChecksum.AlgoVersion` is 24, every
  pin and pin-comment is current, and `AlgoVersionPinCommentHygieneTests` is green.
- Given the re-record, when `SimChecksumCoverageGuardTest` runs, then its pinned known-state hash is
  **unchanged** — proving the fold set did not move.
- Given the re-record, when `ReBaselineDifferentialGuardTests` runs, then both the no-perturbation
  assertion and `FrozenControl_OwnBytes_ArePinned` are green **against the unmodified Phase B control**.
- Given the movement ledger, when reviewed, then every moved golden maps to a DW id in this batch and no
  golden moved without one.
- Given `CanonicalModelHash`, when checked, then it moved only via DW-514 and `StartStateHash` did not
  move at all.
- Given the full suite on Windows with `--logger trx`, then it is ≥ 6050 passed / 0 failed / 1 skipped.
- Given `deferred-work.md`, then 17 entries are `status: done` — 15 with a code/content resolution and
  DW-512/DW-647 with a decision-held resolution — and DW-775 is re-homed to Story 15-23.

## Verification

**Commands:**
- `dotnet build godot/godot.csproj` — clean.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj --logger trx` — full Tier-1,
  authoritative failure list.
- `dotnet test … --filter "FullyQualifiedName~ReBaselineDifferentialGuard|FullyQualifiedName~SimChecksumCoverageGuard"`
  — the two halt gates, run explicitly before and after the record.
- `git diff --stat -- godot/ProjectChimera.Sim.Tests/Golden/` then per-file
  `git diff -- <golden> | grep -v '^#'` — real movement vs header churn.

## Auto Run Result

_(populated by the run)_
