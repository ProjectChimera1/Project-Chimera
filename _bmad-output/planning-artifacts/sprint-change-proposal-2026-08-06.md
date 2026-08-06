# Sprint Change Proposal — 2026-08-06

**Epic:** 15 (Deferred-Work Burn-Down & MP Reconnect)
**Trigger:** Epic 15's story list has never been written to by the process that does its work, and the ~21 golden-moving ledger entries left over from the Phase B re-baseline need a packaging decision.
**Scope classification:** **Moderate** — backlog reorganization, no PRD/architecture/UX change.
**Author:** correct-course session, verified against the repo at `ac645a98`.

---

## 1. Issue Summary

Two linked problems, both structural rather than clerical.

**(a) Epic 15's story list is a parallel record nothing writes to.** Twelve of its twenty-one stories are thematic multi-DW sweep containers (`15-4-sim-correctness-sweep-combat-economy-gates`, `15-5-determinism-checksum-test-hardening-sweep`, …). The automated burn-down does not execute stories — it executes DW bundles and closes ledger ids. Story status has only ever moved by a hand reconciliation that re-derives story→DW mapping from prose bundle lists, and the 2026-08-04 attempt recorded that the join no longer matches the authoritative bundle index (bundles were re-cut; `tier1-test-hardening` → `tier1-determinism-test-hardening`). Zero `spec-15-*.md` files exist; six `spec-dw-*.md` files do. Re-cutting the list once would go stale again after one burn-down wave.

**(b) ~21 golden-moving entries did not fit the Phase B batch** and need packaging into a Phase C re-record window.

**Evidence (verified 2026-08-06, not from memory):**

| Claim | Verified |
|---|---|
| Ledger 839 entries | ✅ `grep -c '^### DW-'` = 839 |
| `spec-15-*.md` count | ✅ 0 |
| `spec-dw-*.md` count | ✅ 6 |
| Epic 15 = 21 stories, 20 backlog / 1 done | ✅ `15-1`…`15-21`; only `15-15` done |
| master tip = `ac645a98` | ✅ Phase B **is merged** |

---

## 2. Impact Analysis

### 2.1 The story list is not homogeneous — this is the finding that changes the answer

The twenty-one keys are **two different species filed under one epic**:

**Species A — sweep containers (13 keys).** A theme name plus a bundle list. No named deliverable. `15-4`, `15-5`, `15-6`, `15-7`, `15-8`, `15-9`, `15-14`, `15-15` *(done)*, `15-16`, `15-17`, `15-18`, `15-19`, `15-20`.

**Species B — real stories with named deliverables (8 keys).** `15-1` (MP reconnect v1), `15-2` (map-size determinism unification), `15-3` (status effects become real), `15-10` (Scenario Settings panel), `15-11` (ground-target cast), `15-12` (energy & stack mechanics), `15-13` (Teleport + presentation leaves), `15-21` (creator-authorable hero attribute system).

`epics.md:4174` states this outright for three of them: *"Story 15.21 carries no DW ids — it is a net-new feature split out of the DW-265 decision, like 15.1 and 15.10 which are also bundle-less by design."*

**Consequence: deleting all twenty backlog stories would destroy real, un-duplicated design content.** Species B sections in `epics.md` are the only record of:

- the DW-264/265 scope resolution (`epics.md:4075-4081`) — three of four stacking modes already exist in the closed `StackRule` enum; only per-stack expiry is missing;
- the entire Story 15.21 spec — the seven-preset ARPG attribute table, the `Intelligence → energy` seam Story 15.12 is *required* to leave open, and three open design questions explicitly marked "do not decide these in a dev session";
- the Route-C map-size verdict attached to 15.2 (`epics.md:4029`);
- the DW-200 scope note moving it post-1.0 → 1.0 (`epics.md:4093`).

### 2.2 Three of the four things Question 2 proposes to create already exist

| Q2 proposal | Reality |
|---|---|
| New feature story for **DW-280** (ground-targeted casting) | **Is already Story 15.11**, fully specced at `epics.md:4069-4070` — wire widen 11→12, `ReplayRecorder.VERSION` bump, `EffectContext` ground-point field, cast reticle |
| New feature story for **DW-200** (host-side identity + attested hero) | **Already the largest item in Story 15.14**; `epics.md:4093` already directs *"spec it separately from the five bundles above if it doesn't fit one cycle"* |
| **DW-265** in the correction batch | **Already split** 2026-08-03 into 15.12 (flat regen) + 15.21 (attribute system) |

The Q2 instinct is right; the action is **re-scoping existing stories**, not creating new ones.

### 2.3 The batch membership needs four corrections

The proposed nineteen mixes three different kinds of golden movement plus two feature builds.

**Out — DW-160 / DW-162 (and DW-146, which the list omits).** These are Story 15.2, and DW-160's own text describes a far larger movement than a `SimChecksum` bump: *"it changes the pathability persist format (invalidating every stored scenario's `pathability_blocked`) and forces re-baselining every CanonicalModelHash/StartStateHash/golden fixture."* Folding a stored-format migration into a batch of bounded sim corrections means the batch inherits a data migration and the whole window blocks on it. **DW-146** — the float→Fixed elevation grid, the actual cross-platform determinism risk of the trio — is missing from the proposed list entirely; it must travel with 160/162, not separately.

**Out — DW-265.** A feature build (authored `regen_rate` + a folded per-tick regen path), excluded by the same rule the brief correctly applied to DW-200 and DW-280. `epics.md:4081` also warns its regen fold and its stacking fold are two separate movements.

**Out — DW-346.** Fuel accounting, not sim behavior. `epics.md:4116` sets the test: *"verify it does not alter which triggers fire on shipped content, only how much work they are charged for."* It only moves a golden if shipped content hits the fuel cap. Verify first; if clean it is ordinary Story 15.17 work needing no re-record.

**In — DW-838, which the list omits.** Filed by the Phase B session itself and *deliberately* deferred: `AiOpponentSystem.ScoreRazeBuildings`' below-threshold stall-breaker requires a live CommandCenter, "inverting the very condition it exists to handle." That is an AI-behavior correction on the golden path — exactly Phase C material.

**Reclassify — DW-514 and DW-554.** DW-514 is committed editor-drag residue in shipped `alpha_map_01.json`; changing shipped content moves `CanonicalModelHash`, a different mechanism from a `SimChecksum` bump. DW-554 is golden *metadata* (the N=4 multifaction golden's embedded re-baseline hint over-matches the N=8 record test) — it does not move a golden, it edits one. Both belong **inside the window** but are not sim corrections; sequence them consciously.

### 2.4 A separate finding: the ledger cannot answer "which entries move goldens"

Querying open entries whose text mentions a re-baseline returns **16**: DW-160, 162, 200, 265, 280, 512, 548, 549, 554, 570, 647, 658, 678, 837, 838, 839. The brief's list of 21 has nine entries that self-declare nothing (514, 659, 664, 674, 766, 775, 803, 346, 272) and omits two the ledger does flag (838, 839). **Neither list is derivable from the other.** Golden-moving status currently survives only in session memory, which is why Phase B's leftovers had to be reconstructed by hand.

### 2.5 Documentation contradictions surfaced

1. **`Snapshot.md:18` is stale.** "Next Action: Merge `rebaseline/phase-b`" — already merged at `ac645a98`.
2. **`sprint-status.yaml:500` and `:524` are stale.** Both record the map-size / `ScenarioType` decision as "UNDECIDED for a 3rd epic"; `epics.md:4029` records Route C as landed and 15.2 as unblocked. Code confirms a partial landing: `ScenarioValidator.cs:148` enforces `map_bounds > MapSizes.MaxHalfExtent` fail-closed, but **`border_extent` does not exist anywhere in `godot/src`**. Correct state: **decided, partially built** — the remainder is 15.2's scope.
3. **`sprint-status.yaml:268-271`** flags the batch-vs-isolate contradiction as "Unresolved. Decide before Phase B opens." Phase B opened, batched, and proved it. Resolved — record it.
4. **DW-272 is double-homed** — listed in Story 15.3 (`epics.md:4031`) and again as "DW-272 behavior half" in Story 15.12 (`epics.md:4072`).

---

## 3. Recommended Approach

### Question 1 — **Split the epic. Do not delete wholesale, do not regenerate.**

**Retire the 11 backlog sweep containers** (`15-4`…`15-9`, `15-16`…`15-20`) from `sprint-status.yaml`. The ledger becomes the single tracker for defect burn-down. Keep `15-15` as a done record, and keep `15-14` — re-scoped, not retired (below).

**Keep the 8 Species-B stories** as real, spec-able stories, and **re-scope two** to absorb what the sweep containers were carrying for them:
- `15-14` shrinks to **DW-200 only** (host-side identity enforcement + attested-hero deployment) — its five hardening bundles release to the ledger. This is precisely the split `epics.md:4093` already anticipated.
- `15-11` is confirmed as the DW-280 home; no change needed.

**Add one new story, `15-22`** — the Phase C re-baseline batch (§4).

**Result: Epic 15 goes 21 keys → 11** (10 actionable backlog + 1 done record) — 11 containers retired, 1 story added.

**Why not delete everything (the brief's stated thinking):** eight of the twenty-one are real feature work whose design content exists nowhere else, and three of the four items Question 2 wants to create are among them.

**Why not "regenerate the list from the ledger each wave" (the stated alternative):**
1. It cannot represent `15-1`, `15-10`, `15-21` at all — they carry no DW ids by design.
2. It automates the parallel record rather than removing it. The staleness is structural: the join runs story → prose bundle list → DW id, and the burn-down re-cuts bundles, so every regeneration costs a manual re-derivation. The 2026-08-04 pass recorded exactly this failure.
3. `bmad-loop status` counts `backlog` keys. A generated container the loop can never complete permanently corrupts the remaining-work number — the 2026-08-04 note records that flipping 15 partial stories to `in-progress` dropped the count 43 → 27 and made them invisible to the loop, and had to be reverted.

**Preserve `epics.md`.** Mark the retired sweep subsections **superseded — burn-down scope now tracked in the ledger** rather than deleting them. The ledger carries `decision:` lines (DW-160 shows two), but the *reasoning* behind the 18 answered sweep decisions (DW-80, 327, 331, 342, 343, 349, 366, 370, 374, 382, 446, 458, 478, …) exists only in that prose. Verify each retired section's decision text is present on its ledger entry before considering the section archival; move it if it is not.

### Question 2 — Phase C packaging

**One batch story, `15-22`, containing 14 bounded sim corrections:**

> DW-512, 548, 549, 570, 647, 658, 659, 664, 674, 678, 766, 775, 803, **838**

Plus **1 answered ruling that moves goldens** — DW-837 (§5) — and **3 in-window riders** that touch golden files or shipped content but are not sim corrections: DW-514 (shipped-scenario residue → `CanonicalModelHash`), DW-554 (golden metadata), DW-839 (wrong comment in `AiActiveScenario`, free).

**Total closed in one window: 18.** Both §5 decisions are answered; nothing blocks `15-22`.

**Explicitly out, with homes:**

| Entry | Home | Why not batched |
|---|---|---|
| DW-160, **146**, 162 | Story **15.2** | Changes the pathability persist format; moves `CanonicalModelHash` + `StartStateHash` + invalidates stored `pathability_blocked`. Own story, own re-baseline. |
| DW-265 | Stories **15.12** + **15.21** | Feature build. Same rule that excludes 200/280. |
| DW-200 | Story **15.14** (re-scoped) | Unbuilt feature; moves no existing golden. |
| DW-280 | Story **15.11** | Unbuilt feature; moves no existing golden. |
| DW-346 | Story **15.17** | Fuel accounting. Verify-then-decide; likely no movement. |
| DW-272 | Story **15.12** | Answered 2026-08-06 as a creator-authored mode + cap (§5) — a feature build moving `CanonicalModelHash`, with a default that leaves shipped goldens byte-identical. |

**The batch rule, stated so it does not need re-deriving next time:**

> A re-baseline batch takes **bounded corrections only**. The cost being amortised is a ~10-minute re-record; coupling it to a multi-week feature build costs far more than it saves, because the branch stays open and every other golden-moving fix queues behind it. **Feature stories that move goldens each re-record at the end of their own story.**

This is the general form of the rule the brief already applied correctly to DW-200 and DW-280 — it just also excludes DW-265, DW-160/146/162, and anything else where the work is the expensive part.

**Procedure (proven in Phase B, carry forward verbatim):** bound the fold so scenarios not carrying the new state stay byte-identical; re-record on Windows so `ai-active` does not go stale; diff with `grep -v '^#'` (the recorder rewrites the version header on every file); verify with `--logger trx` (the console logger truncates its failure list — it showed 6 of 26). The reconnected differential guard halts on a botched re-baseline. **When a deliberate halt gate fires, never re-freeze its control.**

---

## 4. Detailed Change Proposals

### 4.1 `sprint-status.yaml` — edit as **TEXT** (this file does not strictly parse as YAML)

**Delete these 12 keys:**
```
15-4-sim-correctness-sweep-combat-economy-gates: backlog
15-5-determinism-checksum-test-hardening-sweep: backlog
15-6-scenario-content-pipeline-fail-closed-sweep: backlog
15-7-mp-bootstrap-resilience-sweep: backlog
15-8-creation-suite-editor-fidelity-sweep: backlog
15-9-ability-command-card-authoring-sweep: backlog
15-14-dedicated-server-drop-path-hardening: backlog
15-16-alliance-awareness-sim-team-mode-completion: backlog
15-17-trigger-dsl-runtime-gate-hardening: backlog
15-18-content-packaging-browsing-replay-lifecycle: backlog
15-19-ai-draft-consolidation-llmservice-lifecycle: backlog
15-20-hud-settings-save-system-polish: backlog
```

**Add, replacing them, one comment block + two keys:**
```
  # ── RE-SHAPED 2026-08-06 (sprint-change-proposal-2026-08-06.md).
  #   The 11 thematic sweep containers were RETIRED: the burn-down executes DW bundles and closes
  #   ledger ids, never stories, so they were a parallel record nothing wrote to. Their scope is
  #   unchanged and now lives ONLY in deferred-work.md. epics.md keeps the prose as archive.
  #   Retired: 15-4..15-9, 15-16..15-20, and 15-14's five hardening bundles.
  #   KEPT: the 8 stories with named deliverables. 15-14 is re-scoped to DW-200 alone.
  #   ADDED: 15-22, the Phase C re-baseline batch.
  15-14-host-side-hero-identity-enforcement-attested-deployment-dw-200: backlog
  15-22-phase-c-batched-golden-re-baseline: backlog
```

**Amend the stale action-item statuses** at `:500` and `:524`: the map-size / `ScenarioType` decision is **decided (Route C) and partially built** — `ScenarioValidator.cs:148` enforces the clamp; `border_extent` remains unbuilt and is Story 15.2's scope. Not "UNDECIDED for a 3rd epic."

**Resolve the flagged contradiction** at `:268-271`: batching is the standing approach as of Phase B; `:241`'s "do not batch them into one re-baseline" is superseded.

### 4.2 `epics.md` — Epic 15 section

- Insert a re-shape note under the Epic 15 header recording the 21 → 10 split and pointing burn-down scope at the ledger.
- Mark §§ 15.4–15.9, 15.16–15.20 **_Superseded 2026-08-06 — retired as sprint keys; scope tracked in `deferred-work.md`. Retained for the decision reasoning._** Do not delete.
- Rewrite § 15.14 to DW-200 only; move the five hardening bundles into the superseded block.
- Add § 15.22 with the batch membership, the batch rule, and the Phase B procedure.
- Resolve the DW-272 double-homing per §5: it lands entirely on **15.12** (authored periodic-stacking mode + system cap, default byte-preserving). Drop the mention from 15.3 and extend § 15.12's stacking scope.

### 4.3 `deferred-work.md`

- Add a `goldens: moves | none | verify` field to the batch entries. §2.4 shows golden-moving status is currently unrecoverable from the ledger — this is what made Phase B's leftovers a hand reconstruction. Without it, Phase D repeats the archaeology.
- Re-home DW-200 → 15.14, DW-280 → 15.11, DW-265 → 15.12/15.21, **DW-272 → 15.12**, DW-346 → 15.17, DW-160/146/162 → 15.2, and the 14 batch entries + DW-837 + 3 riders → 15.22.
- Record both §5 answers as `decision:` lines on DW-272 and DW-837.

### 4.4 `Snapshot.md`

- Replace the stale **Next Action** (`:18`, "merge `rebaseline/phase-b`" — merged at `ac645a98`) with the Phase C / Epic-15 re-shape position.
- Correct the `:45` leftover list: **+DW-838, +DW-146; −DW-265** (feature), and note 160/162 travel with 146 as Story 15.2, not as batch members.

### 4.5 No change required

PRD, GDD, architecture, and UX artifacts are untouched — this is backlog shape and determinism sequencing, not scope or requirements. No FR moves.

---

## 5. Decisions — ANSWERED 2026-08-06 (Alec)

### DW-272 — stacked periodic DoT/HoT: **creator-authored mode, with a system cap**

> *"I want the creator to have the freedom of choosing this. We can create a cap to protect the system, but I want the user to be able to create a stackable debuff or buff in as many ways as they want."*

Not one of the three offered rulings — Alec rejected the premise that the engine should pick. Consistent with the platform rule that every system is data-driven and creator-extensible, and with the 15.12 finding that `StackRule` is a closed enum already needing a split.

**Scope:** an authored periodic-stacking mode on the modifier model (multiply-the-pulse **and** run-the-pulse-N-times both available to content), plus a system-level cap on the periodic contribution as a runaway protector, surfaced in the ability editor's existing stacking dropdown alongside the `StackRule` split.

**Consequence — DW-272 LEAVES the `15-22` batch and lands on Story 15.12.** Reasoning:

1. This is now a **feature build**, not a bounded correction — excluded by the batch rule in §3.
2. Adding authored fields moves **`CanonicalModelHash`**, not `SimChecksum` — a different re-record.
3. The default mode must preserve today's non-scaling pulse **byte-for-byte** (the same principle `epics.md:4079` applies to the `StackRule` split: *"a grouped variant preserving today's behavior byte-for-byte so no shipped content changes meaning"*). With that default, **shipped content is unchanged and no existing golden moves at all** — only opt-in content diverges.
4. 15.12 already owns the `StackRule` split, the per-stack expiry build, and the ability-editor stacking dropdown. This is the same surface.

This also resolves the DW-272 double-homing (§2.5.4) cleanly: it lands entirely on **15.12**; drop the mention from 15.3.

### DW-837 — win rule: **total wipeout always loses, any faction count**

Drop the `ActiveCount < 3` guard at `WinConditionSystem.cs:343` entirely. A faction with no units and no buildings loses regardless of win-condition type or player count. This overrides the recorded Story 7.11 parity concern.

**Moves goldens** (win-condition evaluation on the golden path) → **stays in the `15-22` batch.**

### Net effect on the batch

`15-22` = **14 bounded corrections + 1 ruling (DW-837) + 3 in-window riders = 18 entries closed.**
DW-272 moves to Story 15.12. No decision now blocks `15-22`.

---

## 6. Implementation Handoff

**Scope: Moderate** — backlog reorganization, no replan.

| Step | Artifact | Actor | State |
|---|---|---|---|
| 1 | §5 rulings (DW-272, DW-837) | **Alec** | ✅ answered 2026-08-06 |
| 2 | `sprint-status.yaml` text edits (§4.1) | Dev — **never** round-trip through a YAML parser | ✅ applied |
| 3 | `epics.md` supersede + § 15.22 (§4.2) | Dev | ✅ applied |
| 4 | Ledger re-homing + `goldens:` field (§4.3) | Dev | ✅ applied |
| 5 | `Snapshot.md` refresh (§4.4) | Dev | ✅ applied |
| 6 | Spec + run `15-22` | `chimera-dw-burndown` (all 14 are Godot-free) | next |

**Success criteria**
- `bmad-loop status` reports **10** actionable Epic-15 stories, all genuinely runnable by the loop.
- `15-22` lands as one `AlgoVersion` 23 → 24 bump, one re-record, 18 entries closed.
- The reconnected differential guard stays green; the gather-free control is **not** re-frozen.
- Suite ≥ 6050 passed / 0 failed on Windows, verified with `--logger trx`.

---

## 7. Should `bmad-sprint-planning` regenerate `sprint-status.yaml`?

**No. Hand-edit as text.** Four independent reasons:

1. **The file does not strictly parse as YAML** (project rule + `sprint-status-yaml-structure-quirk`). A generator round-trip is precisely the failure mode the rule exists to prevent.
2. **It carries ~270 lines of irreplaceable hand-written state** — the 2026-08-04 reconciliation with per-story closed/total fractions, action items A1-E5 through A7-E11 with live statuses, and the legend documenting why `in-progress` is unsafe with this engine. Regeneration from `epics.md` destroys all of it.
3. **Mechanical rewrites of this file are proven dangerous here.** The 2026-08-04 `backlog` → `in-progress` flip made 15 stories invisible to the loop and had to be reverted. That is direct evidence against automated regeneration.
4. **The change is 12 deletions and 2 additions.** That is a text edit, not a regeneration.

**Instead:** hand-edit per §4.1, then run **`bmad-sprint-status`** (read-only) and **`bmad-loop status`** to confirm the queue resolves to the expected 9.
