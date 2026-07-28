# Sprint Change Proposal — Deferred-Work Ledger Resolution & Epic 15

**Date:** 2026-07-28
**Trigger:** Alec's directive via `correct-course`: analyze ALL remaining deferred work accumulated during bmad-loop, absolve what can be absolved, file the rest into epics, and make the documentation correct.
**Mode:** Batch. **Prepared by:** Developer (correct-course workflow).

---

## 1. Issue Summary

The deferred-work ledger (`_bmad-output/implementation-artifacts/deferred-work.md`) has grown across Epics 1–9 into a mixed-format file that no longer tells the truth at a glance:

- **131 open DW-format entries** (of 201), partitioned by the 2026-07-26 sweep triage but only 5 of 43 bundles executed so far.
- **127 legacy numbered items** (June 2026 review sections, stories 1.x–3.4) that were never migrated to DW format — no status, no tracking, and many silently resolved by later epics.
- **12 human decisions** answered 2026-07-25/27 (incl. two that explicitly mandate correct-course scoping: DW-2 reconnect and DW-160 map-size determinism) not yet turned into planned work.
- One sweep bundle (`terrain-brush-stroke-lifecycle`, DW-141..144) sits **stopped mid-flight**: the working tree holds its discarded attempt and run `20260726-223325-3575` is paused awaiting manual rollback.

This proposal is the full disposition: every one of the **258 items** has been verified against the current codebase (the 131 open DW ids via the 07-26 sweep triage, cross-checked against today's ledger; the 127 legacy items via 8 parallel read-only verification passes on 2026-07-28, each verdict backed by file:line or test evidence).

### Headline verification findings (things the analysis surfaced beyond bookkeeping)

1. **Authored status effects do nothing.** All five `StatusFlags` (Stun/Root/Silence/Disarm/Invulnerable) are authorable in the 2.5 editor and folded into the checksum, but **no system reads them** — an authored stun is a silent no-op that costs energy. (legacy 2.2b#5)
2. **New defect found during verification:** the Advanced ability composer **drops `PersistentEffect.Lifelong` on round-trip** (`AbilityDraft.cs:216-219, 258-265`) — opening `furnace_trickle`/`furnace_pour` in Advanced and saving silently reverts the Story 2.13 lifelong-HoT fix.
3. **Three legacy items escalated from latent to reachable:**
   - `ScenarioLoadPhase.ResolveSlotFactionDefs` is never re-run by `ResetToAuthoredStart` (the 3.10 Edit↔Play loop) → in-session faction_json changes silently don't apply (legacy 1.8b#1).
   - Trained workers never get `GatherState` — reachable since Story 6.8 custom buildings can author `produces_category: "Worker"` (legacy ai-deadlock#4).
   - N≥3 minority desync self-halt silently kills the surviving majority's desync guard — live since 9.7/9.15 shipped 4-player MP (legacy 1.9a#1b).
4. **Content Browser vs Win-Condition import paths have drifted** exactly as predicted: `HandleLoadMap` lacks the 6.2 terrain-file handling → terrain-bearing `.chimera.zip` loads flat via the browser (legacy 1.8c#4b).

---

## 2. Impact Analysis

**Epic impact.** Epics 1–9 are done and unaffected. No done story is re-opened; every "resolved" verdict is evidence-backed by shipped code. The open remainder does not fit the four backlog epics' charters (10 = release readiness, 11 = match/shell, 12 = import manager, 13 = campaign) except where individually noted — a new **Epic 15** is proposed as the home for the burn-down plus the two decision-mandated builds (reconnect, map-size determinism). Epic 14 stays as-is (7/8 done; 14-6 open).

**Story impact.** 14 blocked DW entries get annotated onto their blocking backlog stories (10.9, 10.10, 10.11, 10.14, 10.5/10.8, 10.15, 11.1, 11.4, 14-6). No existing backlog story changes scope.

**Artifact conflicts.**
- `deferred-work.md`: the documentation-correctness core — ~86 closures, ~58 legacy migrations to DW format, new bundle definitions (details §4.1).
- `epics.md`: add FR-79 (MP reconnect), FR coverage row, Epic 15 + stories; annotate 6 backlog stories with DW pointers (§4.2).
- `sprint-status.yaml`: add epic-15 story keys as backlog (§4.3). Text-edit only (known YAML quirk).
- PRD/GDD: FR-79 addition noted for the next GDD reconciliation pass; no MVP change.

**Technical impact.** Three of the proposed work items are determinism-sensitive (golden re-baselines expected, each isolated to its own story/bundle per the checksum-fold timing rule): DW-78 gather-state fold, status-flag honouring, gatherer-slot-release-on-death. Map-size determinism (15.2) does a one-time re-baseline by design.

**MVP.** Unaffected. Nothing here removes 1.0 scope; the proposal *adds* one FR (FR-79 reconnect) per Alec's recorded 2026-07-25 decision.

---

## 3. Recommended Approach: Direct Adjustment (+ one new epic)

**Chosen: Option 1 — Direct Adjustment.** Close what's proven done, migrate what's real, wrap the remainder in a new Epic 15, annotate blocked items onto their owners. No rollback (nothing shipped is wrong) and no MVP reduction (nothing here threatens 1.0 goals).

- Effort: the ledger/epics/sprint-status edits are a single documentation pass (Developer, this session, on approval). The Epic 15 work itself is ~34 sweep bundles + 4 feature-grade stories, executed at the existing bmad-loop sweep cadence (5 bundles/cycle) interleaved with Epic 11 per the Epic-9 retro sequencing.
- Risk: low. The main risk is the known one — determinism-sensitive bundles are flagged and isolated.
- Timeline: does not displace Epic 11 as next; Epic 15 sweeps run in the same alternating rhythm the last four sweep commits already established.

---

## 4. Detailed Change Proposals

### 4.1 `deferred-work.md` — the ledger

#### 4.1.1 Close 17 DW entries as `accepted` (sweep-triage skip verdicts, each with its recorded reason)

DW-33 (unreachable mint path), DW-58 (revisit trigger fired, fix unneeded), DW-61/67/70 (human keep-as-is decisions 07-19), DW-73 (GraphEdit interaction untestable in two-tier model), DW-79 (accepted theoretical perf), DW-84 (Godot-coupled, tier mismatch), DW-88 (perf observation), DW-109 (test-hermeticity, not worth), DW-110 (speculative no-op), DW-122 (pure DRY note), DW-128 (explicit spec boundary), DW-132 (live-verify convention covers), DW-176 (no live exposure), DW-183/187 (stale review-budget notes on shipped-green stories).

#### 4.1.2 Close ~69 legacy items as resolved/obsolete/duplicate (migrate to DW format with `status: done` + resolution evidence)

Every closure carries the verifying evidence. Summary by section (full per-item evidence in the verification record, to be embedded in the ledger entries on execution):

| Section | Closed | Representative evidence |
|---|---|---|
| spec-ai-deadlock | #2 rally (2.12, v9 fold), #7 arrive threshold (2.13, ArrivalTuning) | CommandApplyParityTests; ArrivalRadiusTests |
| story-1.1 | #1 boundary tests (1.2), #2 folder contract (SimSources.props+CHM), #3 CI, #4 lock files | determinism-gate.yml runs Tier-1 win+linux locked-mode |
| story-1.3a/1.3b | both (9.2 array resize + re-pinned tests; FromFloat gone, converter rejects ≥32768) | FactionRegistryTests:42; FixedJsonConverter.cs:63-67 |
| story-1.4 | #2 (CHM0005 covers field initializers) | BannedSimApiAnalyzer.cs:97-104 |
| story-2.9b | #1 negative-cost validator, #2 start-resource bound, #4 fallback seed 3→1 (7.7) | UnitDefinitionValidator:343-350; ScenarioValidator InRange; FallbackMirrorParityTests |
| story-1.7 | #1 default(Validated<T>) guard landed at consumption | ScenarioApplier.cs:129-141 |
| story-1.8c | #1 slot IOOR (=DW-96 done), #4a fallback duplication (7.7 retired writer) | SLOT_DEFINITIONS_SIZE=9; ScenarioApplier.cs:395-416 |
| story-1.9a | #1a quorum re-base on disconnect (9.6 DropExpectedReporter) | ServerChecksumCollector:164-206 |
| story-2.1 (+review) | periodic execution (2.2b), Air/Ground filter (2.9a), SetVariable (Epic 7 DSL), total-work bound (2.3), Validate-admits obsolete | EffectExecutor.cs:113-138; TargetMatcher.cs; EffectCaps+RulesetHash |
| story-2.2a (+review) | checksum fold (v6), MaxHealth semantics, authored MaxEnergy (2.4a), clear-on-death, RestoreUnit (=DW-24), recycle guard, Effective→Base fix (3.17) | SimChecksum.cs:330-346; ModifierStore.cs:462-472; ModifierRecycleGuardTest |
| story-2.2b (+review) | SearchArea-in-period fence, MaxEnergy, snapshot dangerous half, re-entrancy load-time gate | AbilityValidator.cs:154-196 |
| story-2.3 | Write round-trip (2.5a), cast path (2.4), caps in rulesetHash (9.4), #5 already closed | AbilityRoundTripTests; RulesetHash.cs:39-48 |
| story-2.4a/b (+reviews) | command-card wiring (2.4b), RestoreUnit abilities (3.17), worker-cast card (2.9b), ability/faction JSON in handshake (9.16), MAX_ABILITIES obsolete-until-content, def-link note obsolete | ContentHash.cs; HandshakeGate fail-closed both sides |
| story-2.5a/b, 2.6, 2.8, 2.9a, 2.10 | ShowJson sync, id sanitize, empty-Persistent+period_ticks validators, flag checkboxes (2.6), lifelong pulse fix (2.13), RestoreUnit armor/passives+AttackDomain (3.17), Player1 hardcode (9.5), Spike Transmutation strand (2.13) | AbilityEditorPanel.cs:614-693; ModifierStore.cs:270-276; AbilityCastSystem.cs:199 |
| story-3.1a/3.1c/3.2/3.3/3.4 | accent registry (3.1b), Chamfer clamp, cut-lg proof, Reset guard (3.3), HeroStore fold (3.13 + coverage teeth), model readout (3.5), undo-of-delete slots (3.10), shadow tokens accept, File.Exists note obsolete, re-indent accepted | AccentController.cs:86-89; SimChecksumCoverageGuardTest:893-954 |

Also closed as **accepted/documented** (open-but-no-action, recorded as such): 2.8#2 production-queue ≥254 sentinel, 3.4#4 re-indent on save, move-speed 1-tick lag (2.2a#3 ≡ 2.2b#8, one merged entry), 2.2b#2 per-stack expiry (design carve-off, file when content wants it), 1.4#1 converter Write lossy (design tradeoff), 1.12#1 Patrol/Follow path polish (accepted scope; pointer to 10.14).

#### 4.1.3 Migrate ~50 still-open legacy facets to DW format (DW-202+) grouped into new bundles

New bundles (consolidated from the verification; each gets DW ids on execution):

| New bundle | Members (legacy facets + absorbed DW ids) | Notes |
|---|---|---|
| noncombatant-command-gate | ai-deadlock #1 AI wave filter, #5 Build case, 1.12#2 Patrol/Follow gate, 2.9a#1 AttackBuilding inert | hoist movement branches + damage filter together |
| gatherer-slot-release-on-death | ai-deadlock #6 (+BuildingSystem.cs:784 second site) | AssignedGatherers is folded (v13) → golden re-baseline |
| trained-worker-gather-init | ai-deadlock #4 | reachable via 6.8; 4-line ref impl at ScenarioApplier.cs:493-497 |
| scenario-unit-id-validation | 1.11#1 (trigger + pre-placed loops) | fail-closed decision needed for existing scenarios |
| scenario-store-capacity-fail-closed | 1.8b#2 (>64 nodes/buildings silent drop; 7.11 landmark interaction) | |
| scenario-reapply-slot-faction-def-refresh | 1.8b#1 + absorbs bundle editor-panel-scenario-rebind (DW-10) | Edit↔Play reachable |
| scenecontext-producer-consumer-guards | 1.8c#2 (+1.8c#3 phase-order asserts) | class already bit once (Epic 9 CustomHudOverlayPhase) |
| map-package-import-one-path | 1.8c#4b + absorbs content-package-import-roundtrip (DW-82,145,156) | fixes browser terrain-flat drift |
| minority-halt-quorum-rebase | 1.9a#1b + 1.9b#2 collector-window abandonment | live at N≥3; +stale MaxSlots doc |
| match-seed-plumbing | 1.5#2 recorder seed + absorbs DW-17 | must ship together |
| searcharea-target-selection-correctness | 2.1c#5 + 2.1r#1 (+2.1r#3 zero-alloc net) | SpatialHash.QueryRadius filter param |
| modifier-period-semantics-and-authoring-warnings | cr-2.2b #2/#3/#4 + 2.3#6 Warnings channel (+2.5b residuals) | authoring-time, no goldens |
| ability-cast-path-hardening | cr-2.4a #1/#2/#3 + 2.4b#5 + 2.4a#5 log-half | debit asserts, SecondsToTicks clamp, diagnostics |
| ability-composer-lifelong-round-trip | NEW defect (composer drops Lifelong) | checkbox + round-trip test |
| ability-editor-composer-cleanup | 2.5a#4 COUNT-half, 2.5b#4 Depth() unused, stale AC5/EffectCaps/Modifier.cs comments | |
| ability-editor-precision-fidelity | 2.5a#3 ≡ 2.5b#3 SpinBox quantization | |
| lockstep-wiring-fail-loud | 2.8#3 (surface grew: Train/SetRally/Revive/Research) | |
| passive-install-idempotence | 2.6#2 (3-line dedup; seam now has 2 subscribers) | |
| faction-load-fail-closed | 3.3#2 + extends faction-load-error-handling (DW-62, DW-123; co-locate DW-65) | |
| nullable-directive-cleanup | 1.1#5 (2 files remain) | trivial |
| tier1-test-hardening | 1.4#3, 1.5#1, 1.5#3, 2.9b#3 | |
| hud-viewport-resize | 2.9b#5 + 3 sibling compute-once sites | presentation-only |
| loader-duplicate-key-fail-closed | 1.6#1 | or ride the Epic 10-13 authoring-UI story |
| gather-state-checksum-fold | DW-78 (decision: build) | golden re-record on Windows |
| hero-row-free-on-editor-delete | DW-52 (decision: build) | |
| hero-xp-per-kill-repurpose | DW-26 (decision: build) | per-hero XP multiplier |

Plus **command-card-producer-surfaces** gains DW-90 (decision: one-producer-category gate).

#### 4.1.4 Keep open with explicit `blocked-on:` annotations (14 + 2)

DW-49/50/177 → Epic 10 live-verify (10.9); DW-180/182 → 10.15 human pass; DW-121 → 11.1; DW-154 → 14-6; DW-124 → 10.10; DW-1 → post-1.0 MP-resilience (prereq 10.11); DW-200 → post-1.0 fast-follow; DW-17 → absorbed by match-seed-plumbing; DW-36/43/54/57 → latent with named triggers; DW-162 → documented ceiling, rides 15.2; DW-199/201 → the scheduled 9-11/9-14 follow-up review (15.5).

#### 4.1.5 Feature-grade increments — DECIDED 2026-07-28: folded into Epic 15 (Stories 15.11–15.13), not parked

- **energy-economy-regen** (2.2b#4 ≡ 2.4a#3 ≡ 2.4b#3): no regen model exists; behavioral change, goldens move.
- **ground-target cast increment** (2.4a#2 ≡ 2.4b#1, +cr-2.4b#2 as AC): UnitOrder widen 11→12, replay VERSION bump, EffectContext ground field, reticle.
- **ally-targeted heal-other affinity** (2.4b#2): feature; no shipped content blocked.
- **per-stack expiry / stacked-DoT scaling** (2.2b#2, cr-2.2b#4 beyond warning): when content wants it.
- **Teleport/PlayVfx/PlaySound/ShakeScreen effect leaves** (2.1c#4 residual): unbuilt, no owner.
- Pointers filed to existing stories: float AI + hardcoded AI costs → **10.11**; plain-Move ring + Patrol/Follow pathing → **10.14**; DEBUG-gated LAN launcher → **10.5/10.8**; tooltip anchor + numeric-undo granularity + badge factory → **10.6/10.7**; toast cap → **11.4**; ScenarioSerializer/FactionDefinition → ContentJson.Options → D3 loader-unification (ride 15.6 or Epic 12).

### 4.2 `epics.md`

1. **Requirements Inventory:** add
   `FR-79: A disconnected multiplayer player can rejoin an in-progress match; the server replays the buffered command log and the rejoining client fast-forwards to the live tick (v1: replay-from-start).` (+ FR Coverage Map row → Epic 15)
2. **Epic List:** add
   `Epic 15: Deferred-Work Burn-Down & MP Reconnect — retire the verified deferred-work backlog as themed sweep batches, land the two decision-mandated determinism/resilience builds (map-size unification, v1 reconnect), and make authored status effects real.`
3. **Epic Details — proposed stories:**
   - **15.1 MP reconnect v1 (FR-79, DW-2):** server flags a dropped-then-returning peer, streams the buffered command log, client validates content + fast-forward-simulates to live tick, resumes input; lobby/loading UX for "Rejoining…". AC gate: 2-player LAN rejoin mid-match, checksums agree post-catch-up; freeze policy (9.6) remains the fallback.
   - **15.2 Map-size determinism unification (DW-160, DW-146, DW-162):** four sim grids parameterized from one map-size truth source; `BuildAndInjectElevationGrid` reads raw heightmap cells (no float interpolation); per-size `GridDimensionConsistencyTests`; one deliberate golden re-baseline; document the +128 ceiling (closes DW-162's contradiction).
   - **15.3 Status effects become real (headline):** CombatSystem honours Disarmed, Movement honours Rooted/Stunned, AbilityCastSystem honours Silenced, DamageResolver honours Invulnerable; lethal-period-DoT test; golden re-baseline; bundles ability-composer-lifelong-round-trip + modifier-period-semantics-and-authoring-warnings ride along (same surface).
   - **15.4 Sim correctness sweep** (bundles: noncombatant-command-gate, trained-worker-gather-init, gatherer-slot-release-on-death, searcharea-target-selection-correctness, projectile-convergence-clamp, modifier-subsystem-robustness, gathering-closed-gate-reidle, gather-state-checksum-fold, map-bounds-out-of-range-guards).
   - **15.5 Determinism, checksum & test hardening sweep** (scenario-applier-canonical-order, sim-reset-and-clear-test-coverage, match-seed-plumbing, tier1-test-hardening, real-content-signature-test-hardening, canonical-hash-test-name-cleanup, replay-research-parity-test, nullable-directive-cleanup, + the scheduled 9-11/9-14 follow-up review pass [DW-199/201]).
   - **15.6 Scenario & content pipeline fail-closed sweep** (scenario-unit-id-validation, scenario-store-capacity-fail-closed, scenario-reapply-slot-faction-def-refresh, map-package-import-one-path, faction-load-fail-closed, faction-definer-wizard-hardening, faction-art-path-consistency, loader-duplicate-key-fail-closed, load-time-recursion-hardening, settings-serialization-shared-options, housekeeping-docs-and-normalization + doc-debt one-liners).
   - **15.7 MP & bootstrap resilience sweep** (minority-halt-quorum-rebase, scenecontext-producer-consumer-guards, lockstep-wiring-fail-loud, passive-install-idempotence).
   - **15.8 Creation-suite editor fidelity sweep** (terrain-brush-stroke-lifecycle redrive, card-editor-history-safety, card-editor-field-fixes, creation-suite-panel-infrastructure, tech-tree-editor-sync-undo, editor-item-placement-persistence, start-position-editor-fixes, group-op-editor-fidelity, hero-row-free-on-editor-delete, onboarding-panel-fixes, resource-node-placement-controls, map-size-camera-minimap, hud-viewport-resize).
   - **15.9 Ability & command-card authoring sweep** (ability-cast-path-hardening, ability-editor-composer-cleanup, ability-editor-precision-fidelity, hero-xp-per-kill-repurpose, command-card-producer-surfaces+DW-90, custom-building-render, building-def-creation-helper, trigger-custom-building-resolution, dsl-graph-editor-node-inspector, win-condition-robustness, dsl-event-feed-capacity).
   - **15.10 Scenario Settings panel + New-Scenario flow (DW-126, DW-127):** the two 2026-07-25 "build" decisions — unified Scenario Settings surface + Create/New-Scenario empty-canvas flow (onboarding step 4 revisit).
4. **Backlog story annotations:** 10.9 (+DW-49/50/177), 10.10 (+DW-124), 10.11 (+float-scorer & hardcoded-AI-costs pointer), 10.14 (+plain-Move ring, Patrol/Follow pathing), 10.15 (+DW-180/182), 11.1 (+DW-121), 11.4 (+toast cap), 14-6 (+DW-154).

### 4.3 `sprint-status.yaml`

Append (text-edit, preserving file quirks): `epic-15: backlog` + the ten `15-N-…: backlog` story keys mirroring §4.2.

### 4.4 Operational prerequisite (not part of the doc edits)

The stopped terrain-brush attempt must be cleared before the next sweep cycle: back up anything wanted from the working tree, `git reset --hard 0d1fdb150bb9`, then `bmad-loop resume 20260726-223325-3575` (per the run's ATTENTION note). The spec file `spec-terrain-brush-stroke-lifecycle.md` is untracked and will be regenerated on redrive.

---

## 5. Implementation Handoff

**Scope classification: Moderate** (backlog reorganization; no fundamental replan — sequencing from the Epic-9 retro stands: Epic 11 next, sweeps interleaved).

| Who | What |
|---|---|
| Developer (this session, on approval) | Execute §4.1–4.3 edits: ledger closures/migrations/bundles, epics.md FR-79 + Epic 15 + annotations, sprint-status.yaml keys. Nothing else touches code. |
| bmad-loop sweep cycles | Execute Epic 15 sweep-batch stories at the existing 5-bundles/cycle cadence. Determinism-sensitive bundles (gather-fold, gatherer-slot-release, 15.2, 15.3) each isolated with their own golden re-baseline per the checksum-fold timing rule. |
| Alec | Approve/edit this proposal; decide the parked feature list (§4.1.5) 1.0 vs post-1.0; perform the terrain-brush rollback (destructive — not automated by this proposal). |

**Success criteria:** ledger contains zero untracked legacy items and zero unexplained open entries (every open entry = bundled, blocked-annotated, or parked-by-decision); epics.md/sprint-status.yaml reflect Epic 15; the next `bmad-loop sweep` triage runs clean against the new structure.

---

## Approval & Execution Record

- **Approved by Alec 2026-07-28** (batch review): implement in full.
- Decision: feature-grade increments (energy regen, ground-target cast, ally-affinity, stack mechanics, effect-vocabulary completion) **folded into Epic 15** as Stories 15.11–15.13 (not parked post-1.0).
- Decision: terrain-brush stopped attempt **discarded** (`git reset --hard 0d1fdb150bb9`) and run `20260726-223325-3575` resumed for redrive.
- Executed 2026-07-28: ledger migrated (324 canonical DW entries, zero legacy remnants; 17 accepted-closes; 34 annotation lines; DW-202..DW-324 minted — 70 closed-with-evidence, 53 open), `epics.md` (FR-79 + coverage row + Epic 15 with Stories 15.1–15.13 + 8 backlog-story DW annotations), `sprint-status.yaml` (epic-15 block, 13 story keys, backlog).
