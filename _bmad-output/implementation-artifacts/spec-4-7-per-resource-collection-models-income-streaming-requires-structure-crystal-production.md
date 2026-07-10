---
title: 'Per-resource collection models (GATHER/INCOME/STREAMING) + requires_structure gate + Crystal production'
type: 'feature'
created: '2026-07-09'
status: 'done'
baseline_revision: '047bdaab792ee2fa2364c571fb5ca15c37a8f73d'
final_revision: '47ae4e4ec6d15d62bb98db9ba550a79e7da1d18b'
review_loop_iteration: 0
followup_review_recommended: true
context: []
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** `ScenarioResourceNode`/`GatheringSystem` support exactly one collection model — a worker carries ore round-trip to the base — so creators can't author idle/tower-defense (flat trickle), base-fed streaming, or structure-gated economies. Crystal balances can be spent (`SpendCrystal`) but no node ever calls `AddCrystal`, so Crystal production is a dead path.

**Approach:** Add `collection_model` (Gather/Income/Streaming, default Gather), `resource_type` (Ore/Crystal, default Ore), `requires_structure`+`requires_structure_radius`, `owner_slot`, and `income_period_ticks` to `ScenarioResourceNode` and the parallel `ResourceNodeStore` SoA. `GatheringSystem` gains an Income tick pass (periodic flat credit, no workers), a Streaming credit-in-place branch in `TickGathering` (no `MovingToBase`/`CarryAmount`), and a `requires_structure` proximity gate (reusing `AiOpponentSystem.FindNearestEnemyBuilding`'s scan shape against the new `BuildingStore` dependency) applied in `FindBestNode` and the Income pass. All node credit dispatches through `AddOre`/`AddCrystal` by `resource_type`, closing the Crystal gap. `ResourceNodeStore` has never been folded into `SimChecksum` — this story adds that fold for the first time alongside the new mutable state (`IncomeTicksElapsed`), and folds the new authored fields into `CanonicalModelHash`.

## Boundaries & Constraints

**Always:** GATHER behavior (worker cycle, `max_gatherers`, deposit-on-arrival) is byte-identical when `collection_model` is omitted/"Gather" — every existing scenario JSON must load and simulate unchanged. All credit math stays in `Fixed`; `income_period_ticks` counts whole ticks via a new `IncomeTicksElapsed` int counter (never `dt`-accumulated, never wall-clock). Nodes/entities iterate ascending id. `owner_slot` resolves to `Faction` exactly like `ScenarioBuilding.Slot`/`ScenarioUnit.Slot` do today (`(Faction)(slot + 1)`), validated against the same `declared` player-slot set `ScenarioValidator` already builds. `requires_structure` matches `BuildingStore.DefinitionId` (the Story 4.1 data-driven id), not the `BuildingType` enum. Every new `ResourceNodeStore` field is a new parallel array, defaulted so `Create(...)` without the new args reproduces today's node exactly, and included in `Clear()`. `SimChecksum.AlgoVersion` bumps once (12→13) covering both the new `ResourceNodeStore` fold and its new mutable fields; `CanonicalModelHash.AlgoVersion` bumps once (4→5) for the new authored fields — one golden re-baseline, one commit.

**Block If:** none identified — this story's shape (new fields with GATHER-preserving defaults, additive fold, single re-baseline) has a direct precedent in Stories 4.4/3.15.

**Never:** Never route Income-node credit through worker assignment (`FindBestNode` must always skip `collection_model=Income` nodes). Never let Streaming retain a `CarryAmount`/`MovingToBase` leg. Never change `MaxGatherers`/`AssignedGatherers` semantics. Never gate `requires_structure` on shared/ally structure visibility — owned-only.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Unknown `collection_model`/`resource_type` string | e.g. `"collection_model": "Trickle"` | Rejected at scenario validation | Located `ValidationResult.Fail` naming the node index and allowed values |
| `collection_model=Income` with no/invalid `owner_slot` | `owner_slot` omitted (-1) or not in `declared` player slots | Rejected at scenario validation | Located error, same style as the Buildings/Units slot-reference check |
| `requires_structure` set, structure exists but wrong faction owns it | Node's `owner_faction` (or candidate worker's faction) has no owned building matching the id in range | Gate stays closed — `FindBestNode` excludes it / Income credit withheld | No error — this is steady-state gameplay, not a validation failure |
| Income node depletes mid-run | `SupplyRemaining` hits 0 exactly on a period credit | `Active=false`, no further credits, matches GATHER's existing depletion behavior | None |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/ScenarioData.cs` -- `ScenarioResourceNode`: add `CollectionModel`("Gather"), `ResourceType`("Ore"), `RequiresStructure`(string?), `RequiresStructureRadius`(15f), `OwnerSlot`(-1), `IncomePeriodTicks`(30); update `Rate`'s doc comment (dual meaning under Income).
- `godot/src/Core/Definitions/ScenarioValidator.cs:127-138` (resource-node loop) -- validate the 6 new fields; reuse the `declared` slot set already built for player slots.
- `godot/src/Core/Definitions/CanonicalModelHash.cs` -- fold the 6 new fields into the resource-node sort key + mix block (`AlgoVersion` 4→5).
- `godot/src/Core/ResourceNodeStore.cs` -- add `ResourceCollectionModel`/`ResourceKind` enums; new parallel arrays `CollectionModel`, `ResourceType`, `RequiresStructureId`, `RequiresStructureRadius`, `OwnerFaction`, `IncomePeriodTicks`, `IncomeTicksElapsed`(mutable); extend `Create(...)` with optional trailing params; extend `Clear()`.
- `godot/src/Economy/GatheringSystem.cs` -- new `BuildingStore` ctor param; `FindBestNode` skips `Income` nodes and applies the `requires_structure` gate; `TickGathering` branches Streaming (direct per-tick credit, no carry/`MovingToBase`) vs Gather (unchanged); new `TickIncomeNodes` pass (ascending node id, counter+credit+deplete); shared `CreditNode`/`FactionHasStructureNear` helpers.
- `godot/src/Core/Sim/SimulationHost.cs:169` -- pass `Buildings` into `new GatheringSystem(...)`. `:208` -- add `Nodes` to `EnableChecksums(...)`.
- `godot/src/Core/Sim/ScenarioApplier.cs:114-119` -- parse `collection_model`/`resource_type` strings (switch, mirrors `ParseBuildingType`), map `owner_slot`→`Faction`, thread all new fields into `_host.Nodes.Create(...)`.
- `godot/src/Core/SimChecksum.cs` -- add `ResourceNodeStore? nodes = null` param; new fold block (live count, then ascending id: `SupplyRemaining`, `Active`, `AssignedGatherers`, `IncomeTicksElapsed`); v13 `AlgoVersion` doc entry.
- `godot/src/Core/SimulationLoop.cs` -- `_checksumNodes` field; thread through `EnableChecksums` and both `Compute` call sites.
- `godot/ProjectChimera.Sim.Tests/Economy/GatheringSystemTests.cs` (new) -- Gather-unchanged regression, Streaming credit-in-place, Income periodic credit + depletion, `requires_structure` gating (both denied and newly-eligible transitions), Crystal-node production, atomic `AddCrystal`/`SpendCrystal` interaction.
- `godot/ProjectChimera.Sim.Tests/Validation/ScenarioValidatorResourceNodeTests.cs` (new) -- the 6 new field validations, including the `owner_slot`/`declared` cross-reference.
- `godot/ProjectChimera.Sim.Tests/Validation/CanonicalModelHashTests.cs` -- extend for the new folded fields (v5).
- `godot/ProjectChimera.Sim.Tests/Golden/SimChecksumCoverageGuardTest.cs` -- add `ResourceNodeStore` fold coverage assertion; re-pin `KnownWorldState_ProducesPinnedV12Hash`→`...V13Hash`; add nodes to `ComputeKnownStateHash`.
- `godot/ProjectChimera.Sim.Tests/Builder/ScenarioApplierTests.cs` -- extend node materialization coverage for the new fields.

## Tasks & Acceptance

**Execution:**
- `ScenarioData.cs` -- add the 6 fields with GATHER-preserving defaults -- non-breaking schema extension.
- `ScenarioValidator.cs` -- located validation for all 6 fields (closed-set strings, non-negative radius/period, `owner_slot` required+declared only when `collection_model=Income`) -- fail-closed content gate, matching the existing style.
- `CanonicalModelHash.cs` -- fold the 6 fields, bump `AlgoVersion` 4→5 -- keeps the lobby handshake sim-affecting-complete (Story 4.4/2.9b precedent).
- `ResourceNodeStore.cs` -- new enums + 7 parallel arrays + `Create`/`Clear` updates -- the per-node state the new models read/mutate.
- `GatheringSystem.cs` -- Income pass, Streaming branch, `requires_structure` gate in `FindBestNode`, `CreditNode` dispatching `AddOre`/`AddCrystal` by `ResourceType` -- the core behavior; GATHER path must stay byte-identical.
- `SimulationHost.cs` -- wire `Buildings` into `GatheringSystem` ctor; add `Nodes` to `EnableChecksums` -- required dependency + checksum wiring.
- `ScenarioApplier.cs` -- parse + thread new fields into `Nodes.Create(...)` -- the single float→Fixed/string→enum load boundary for nodes.
- `SimChecksum.cs` + `SimulationLoop.cs` -- new `ResourceNodeStore?` fold, `AlgoVersion` 12→13 -- first-ever node-state desync coverage plus the new mutable Income counter.
- `GatheringSystemTests.cs` (new) -- cover every I/O-matrix row plus all 6 epics.md ACs below.
- `ScenarioValidatorResourceNodeTests.cs` (new) -- cover the two validation I/O-matrix rows.
- `CanonicalModelHashTests.cs` -- prove the new fields move the hash; an all-default-omitted node hashes identically to pre-story content.
- `SimChecksumCoverageGuardTest.cs` -- re-pin v13, add node-fold coverage assertion.
- `ScenarioApplierTests.cs` -- prove materialization of the new fields end-to-end.

**Acceptance Criteria:**
- Given the existing GATHER node (Idle→MovingToResource→Gathering→MovingToBase→deposit-on-arrival) with `max_gatherers`, when a worker reaches a saturated node and when it arrives at base, then `FindBestNode` skips the saturated node and the carried amount is added only on base arrival — unchanged by this story.
- Given a node with `collection_model=Income`, a period, and no assigned workers, when the sim runs, then it credits exactly `rate` to the owner's balance every `income_period_ticks` (Fixed math, ticks decremented from `SupplyRemaining`, `Active=false` at zero), and zero workers are ever assigned to it.
- Given a node with `collection_model=Streaming` and workers standing in place while Gathering, then the gathered amount credits directly to the owning faction each tick at the node — no `MovingToBase`, no `CarryAmount` — with the same total mined regardless of base distance.
- Given a node with `requires_structure` set, when the relevant faction has no qualifying owned structure within the configured radius, then `FindBestNode` excludes it and Income/Streaming credit is withheld; once a qualifying structure exists in range, the node becomes eligible.
- Given a scenario whose nodes/units produce and spend Crystal, when a Streaming or GATHER Crystal node deposits and a `CostCrystal` unit is purchased, then `AddCrystal`/`SpendCrystal` credit/debit atomically, closing the dead path.
- Given two runs mixing GATHER, Income, Streaming, and a `requires_structure`-gated Crystal node, when the sim runs N ticks, then golden checksums are byte-identical across runs (post re-baseline) — all credit math in `Fixed`, periods in integer ticks, ascending id order, no wall-clock, no Godot types in `GatheringSystem`/`ResourceNodeStore`/`ResourceStore`.

## Spec Change Log

_Empty until the first bad_spec loopback._

## Review Triage Log

### 2026-07-09 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 8: (high 0, medium 3, low 5)
- defer: 4: (high 0, medium 1, low 3)
- reject: 2: (high 0, medium 0, low 2)
- addressed_findings:
  - `[medium]` `[patch]` `GatheringSystem.FactionHasStructureNear` treated a PLACED-but-still-under-construction building as a qualifying structure, opening the `requires_structure` gate before the building was functional — inconsistent with the codebase-wide `IsUnderConstruction` precedent (`TechTreeChecker.cs:76`, `BuildingSystem.cs` ×4). Added the check; updated the 3 existing gate-fixture tests (which relied on `BuildingStore.Create`'s under-construction default) to build completed structures, and added a new test proving the gate stays closed until completion then opens instantly. Flagged by the Edge Case Hunter review layer.
  - `[medium]` `[patch]` `ScenarioValidator` accepted `income_period_ticks=0` with `collection_model=Income` (only checked non-negative), producing a degenerate "credit every tick" mode instead of the intended periodic trickle. Added a located validation rule requiring `>0` when `collection_model=Income` (inert/unchecked for GATHER/Streaming); added two tests (rejected for Income, accepted for GATHER). Flagged independently by the Blind Hunter and Edge Case Hunter review layers.
  - `[medium]` `[patch]` `CanonicalModelHash` hashed `RequiresStructure=""` differently from `null`, even though `ScenarioApplier` already normalizes both to "no gate" — two behaviorally-identical scenarios could false-positive-mismatch at the lobby handshake. Normalized the hash the same way; added a hash-equality test. Flagged independently by the Blind Hunter and Edge Case Hunter review layers.
  - `[low]` `[patch]` `MatchStats.RecordOreMined`'s doc comment still said "deposited by a worker returning to base," stale now that Income (zero workers) and Streaming (no base trip) also feed it. Updated the comment. Flagged by the Blind Hunter review layer.
  - `[low]` `[patch]` `ScenarioApplier`'s owner_slot out-of-range fallback degraded to `Faction.Neutral` silently, unlike the identical out-of-range condition in the same method's units/buildings loops, which log a warning. Added the matching `_log.Warn` call for diagnostic consistency (shadow-mode-reachable only — the validator already requires a declared, in-range `owner_slot` whenever `collection_model=Income`). Flagged by the Blind Hunter review layer.
  - `[low]` `[patch]` `ResourceNodeStore.Create`'s `requiresStructureRadius` optional parameter defaults to `0`, not `ScenarioResourceNode`'s 15f schema default (a compile-time-constant default can't call `Fixed.FromFloat`) — a latent trap for a hypothetical future direct caller. Documented why it's safe today (only consulted when `requiresStructureId` is also set; the sole caller always passes the resolved value explicitly). Flagged by the Blind Hunter review layer.
  - `[low]` `[patch]` `ResourceDefinition.CollectionModel`'s doc comments (and a mirroring test comment in `NegativeValidationTests.cs`) still claimed "Story 4.7 wires collection models," now stale/misleading since this story built the field on `ScenarioResourceNode` instead (the AC text's own bearer). Corrected all three comments to state the field remains inert and point to the actually-wired field. Flagged by the Verification Gap Reviewer.
  - `[low]` `[patch]` Two behavioral contracts were asserted only in code comments, never proven by a test: GATHER's `requires_structure` gate is checked only at assignment (never re-checked mid-cycle), and a Streaming worker's gate closing then reopening mid-gather withholds then resumes credit. Added both tests. Flagged by the Verification Gap Reviewer (GATHER case) and Edge Case Hunter (Streaming case).

### 2026-07-09 — Review pass (follow-up)
- intent_gap: 0
- bad_spec: 0
- patch: 6: (high 0, medium 3, low 3)
- defer: 0 (3 findings re-surfaced but already tracked as DW-77/79/80 — no new ledger entries, per orchestrator ownership)
- reject: 8: (high 0, medium 0, low 8)
- addressed_findings:
  - `[medium]` `[patch]` Crystal load mis-credited as Ore when a Build command interrupts a returning worker. `BuildingSystem.QueueWorkerBuild` clears `world.GatherTarget=-1` (releasing the node) without touching `CarryAmount`/`GatherState`; the deposit resolved Ore-vs-Crystal from `GatherTarget`, so after the build the worker's `MovingToBase` deposit hit the always-`AddOre` `node<0` fallback — silently crediting a Crystal load as Ore (newly reachable this story, since Crystal production is new; deterministic, not a desync). Fixed by snapshotting the carried resource kind onto a new per-worker `EntityWorld.CarryResourceType` at gather time (paired with `CarryAmount`; unfolded from `SimChecksum` exactly like it, so no golden moves) and dispatching the deposit by the carried kind, eliminating the fragile `GatherTarget` coupling and the duplicate `node<0` fallback (also resolves Blind Hunter F9). Added a regression test reproducing the Build-interrupt clear. Flagged by the Blind Hunter review layer (F1); the Edge Case Hunter (#5) independently flagged the fallback branch but not its reachability.
  - `[medium]` `[patch]` The Income gate's "counter frozen while gated — no backlog burst on reopen" contract (`TickIncomeNodes` withholds credit *before* advancing `IncomeTicksElapsed`) was only tested at `income_period_ticks=1`, where a backlog collapses to a single credit and cannot distinguish frozen-counter from accruing-counter. A regression that advanced the counter while gated would ship green even though `IncomeTicksElapsed` is `SimChecksum`-folded (v13). Added a `period=3` gated test asserting the first post-reopen credit lands a full period of *open* ticks later. Flagged by the Verification Gap Reviewer.
  - `[medium]` `[patch]` The `requires_structure` proximity radius was never exercised in the *excluding* direction — every gate test placed the qualifying structure well within range, so a gate that ignored distance entirely would pass them all. Added a test placing a same-faction, same-id, completed structure beyond the radius and asserting the gate stays closed until one comes into range. Flagged by the Verification Gap Reviewer.
  - `[low]` `[patch]` `CanonicalModelHash`'s node total-order sort added six new `ThenBy` keys, but only `CollectionModel` (the first) was proven to participate in the sort; dropping any later key would ship green (the stable `OrderBy` preserves input order, leaking array order into the lobby-handshake hash). Parameterized the order-stability test across all six new fields. Flagged by the Verification Gap Reviewer.
  - `[low]` `[patch]` `TickIncomeNodes` could credit `Faction.Neutral` a phantom index-0 balance for a shadow-mode Income node whose `owner_slot` degraded to Neutral (validator-bypassed only), contradicting `ScenarioApplier`'s "Neutral never matches a credit target" safety claim. Added an explicit `owner == Neutral` skip (making the claim true) plus a test. Flagged by the Blind Hunter (F5) and Edge Case Hunter (#3) review layers.
  - `[low]` `[patch]` `TickIncomeNodes` had no system-level guard against a non-positive `income_period_ticks` — and `ResourceNodeStore.Create`'s default is `0`, so a direct/internal Income `Create` that bypasses the validator would credit every tick instead of periodically. Added an `IncomePeriodTicks<=0` skip plus a test. Flagged by the Edge Case Hunter (#2).
  - Re-surfaced but already tracked (no new ledger entries): in-app editor cannot author the 6 new fields (DW-77 / Blind Hunter F4); per-worker gather state — now including the new `CarryResourceType` — unfolded from `SimChecksum` (DW-78 / Blind Hunter determinism note); O(nodes×buildings) gate scan (DW-79 / Blind Hunter F2); permanently-gated Streaming worker parks forever (DW-80 / Edge Case Hunter #1).
  - Rejected (8, all low): optional `SimChecksum.Compute(nodes)` param (F3 — deliberate, matches the shipped items/heroes/modifiers null-folds-as-empty precedent this story cites); GATHER gate not re-checked mid-cycle (F6 — intentional per the Always/Never contract, already tested); `MainScene` active-node counter now includes Income nodes (F7 — presentation-only, speculative, out of this sim story's scope); `requires_structure_radius` validated even without a gate (F8 — safe fail-closed over-strictness; a negative radius is nonsensical regardless); sort/hash `""`-vs-null normalization mismatch (F10 — reviewer confirmed no defect, order-stability holds); inert-field hash normalization for radius/owner_slot/period (Edge Case #4 — these are genuine authored-content differences the applier does *not* collapse, unlike the `""`/null case; folding them fails safe toward handshake rejection, never desync); and the Verification Gap Reviewer's two explicit non-gaps (the shadow-mode `owner_slot`-degrade log path, and `MatchStats` being deliberately unhashed).

## Design Notes

**Why `rate` is reused (not a new `income_amount` field) for Income's per-period amount:** every existing field on `ScenarioResourceNode` is already conditionally interpreted by context (`max_gatherers` is inert for Income); `rate`'s meaning — "amount granted per unit of production time" — maps directly onto Income's "amount per period" without growing the schema. The field's doc comment is updated to state both meanings explicitly.

**Why `requires_structure` is a `BuildingStore.DefinitionId` string, not a `BuildingType` enum:** Story 4.1 already made buildings data-driven by id; gating on the closed enum would block a creator-authored custom building from ever satisfying the gate, contradicting the platform's creator-extensible mandate.

**Why one `AlgoVersion` bump covers two changes:** `ResourceNodeStore` was never folded into `SimChecksum` at all (a pre-existing desync-detection gap, not caused by this story) — Story 3.15's `ItemStore?`-param/null-folds-as-empty pattern is the template. Since this story is what first makes node state mid-match-mutable in a checksum-relevant way anyway, folding the pre-existing static fields (`SupplyRemaining`, `Active`, `AssignedGatherers`) in the same bump as the new `IncomeTicksElapsed` avoids a second re-baseline immediately after this one.

## Verification

**Commands:**
- `dotnet build godot/godot.sln -c Debug` -- expected: builds clean.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release` -- expected: all Tier-1 tests green, including new Gathering/Validator/CanonicalModelHash tests.
- `CHIMERA_GOLDEN_RECORD=1 dotnet test --filter FullyQualifiedName~GoldenChecksumReplay` then `dotnet build` -- expected: re-baselines every golden (checksum fold changed for all scenarios); review the diff to confirm only the expected hash lines moved, then commit.
- `git diff --stat godot/ProjectChimera.Sim.Tests/Golden/*.golden.txt` -- expected: non-empty (first-ever `ResourceNodeStore` fold moves every golden) — the opposite of a presentation-only story; confirm via the recorded diff, not an empty-diff assumption.

**Manual checks (if no CLI):**
- `/godot-verify` against a scenario authoring one Income, one Streaming, and one `requires_structure`-gated Crystal node: confirm Income credits with zero workers, Streaming credits without a base trip, the gated node stays ineligible until the required structure is built in range, and Crystal balance changes are visible in the HUD.

## Auto Run Result

Status: done

**Summary:** Added three collection models to `ScenarioResourceNode`/`ResourceNodeStore` — GATHER (unchanged), INCOME (periodic flat credit, zero workers, owner-slot-declared), and STREAMING (credit-in-place, no carry/base-trip) — plus an optional `requires_structure` proximity gate (a `BuildingStore.DefinitionId` string + radius, checked at `FindBestNode` assignment for GATHER/Streaming and live per-tick for Streaming/Income) and a `resource_type` (Ore/Crystal) selector that finally wires `ResourceStore.AddCrystal` into real node production, closing a dead path. `ResourceNodeStore` is folded into `SimChecksum` for the first time (v12→v13); the 6 new authored fields fold into `CanonicalModelHash` (v4→v5). All 23 goldens re-baselined in the same commit.

**Files changed:**
- `godot/src/Core/Definitions/ScenarioData.cs` — 6 new `ScenarioResourceNode` fields, GATHER-preserving defaults.
- `godot/src/Core/Definitions/ScenarioValidator.cs` — validates the 6 fields, including the Income-only `owner_slot`/`income_period_ticks>0` cross-references (review patch).
- `godot/src/Core/Definitions/CanonicalModelHash.cs` — folds the 6 fields (v4→v5); normalizes `RequiresStructure` `""`/`null` (review patch).
- `godot/src/Core/Definitions/ResourceDefinition.cs` — doc-comment correction: this per-resource-id `CollectionModel` remains inert; 4.7 wired the field on `ScenarioResourceNode` instead (review patch).
- `godot/src/Core/ResourceNodeStore.cs` — 2 new enums, 7 new parallel arrays, extended `Create`/`Clear`.
- `godot/src/Core/MatchStats.cs` — `RecordOreMined` doc-comment correction (review patch).
- `godot/src/Economy/GatheringSystem.cs` — Income tick pass, Streaming credit-in-place branch, `requires_structure` gate (with an `IsUnderConstruction` check — review patch), `CreditNode` Ore/Crystal dispatch.
- `godot/src/Core/Sim/SimulationHost.cs` — wires `Buildings` into `GatheringSystem`; adds `Nodes` to `EnableChecksums`.
- `godot/src/Core/Sim/ScenarioApplier.cs` — parses/threads the 6 fields into `Nodes.Create`; logs on out-of-range `owner_slot` (review patch).
- `godot/src/Core/SimChecksum.cs` / `SimulationLoop.cs` — new `ResourceNodeStore?` fold, `AlgoVersion` 12→13.
- `godot/ProjectChimera.Sim.Tests/Economy/GatheringSystemTests.cs` (new, 15 tests) — GATHER regression, Streaming, Income, `requires_structure` (incl. under-construction and mid-cycle/reopen — review patches), Crystal production.
- `godot/ProjectChimera.Sim.Tests/Validation/ScenarioValidatorResourceNodeTests.cs` (new, 12 tests) — the 6-field validations incl. `income_period_ticks=0` (review patch).
- `godot/ProjectChimera.Sim.Tests/Validation/CanonicalModelHashTests.cs` — 8 new tests incl. the null/`""` equality case (review patch).
- `godot/ProjectChimera.Sim.Tests/Validation/NegativeValidationTests.cs` — comment correction (review patch).
- `godot/ProjectChimera.Sim.Tests/Golden/SimChecksumCoverageGuardTest.cs` — re-pinned v13, node-fold coverage teeth.
- `godot/ProjectChimera.Sim.Tests/Builder/ScenarioApplierTests.cs` — node materialization coverage, re-pinned hash.
- `godot/ProjectChimera.Sim.Tests/Meta/VersionStampConsistencyTests.cs`, `Sim/SimResetTests.cs`, `Definitions/CombatFeedbackProfileTests.cs`, `Definitions/HeroProfilePersistenceTests.cs`, `Golden/ProceduralMapGeneratorTests.cs` — re-pinned `AlgoVersion`-dependent hashes (mandated by the version bumps, same category as Stories 4.4/3.15).
- 23 `godot/ProjectChimera.Sim.Tests/Golden/*.golden.txt` — re-baselined (first-ever `ResourceNodeStore` fold moves every golden).
- `_bmad-output/implementation-artifacts/deferred-work.md` — appended DW-77..80.

**Review findings breakdown:** 8 patches applied (3 medium: an under-construction structure incorrectly satisfying `requires_structure`, `income_period_ticks=0` producing a degenerate credit-every-tick mode, and a `CanonicalModelHash` null/`""` inconsistency risking a false-positive lobby-handshake mismatch; 5 low: two stale doc comments, one diagnostic-logging inconsistency, one defaulted-parameter clarification, and missing test coverage for two comment-only-asserted contracts). 4 items deferred to `deferred-work.md` (DW-77..80: the in-app placement tool can't author the new fields yet, per-worker gather state remains unfolded from `SimChecksum`, a bounded algorithmic-complexity note, and a design question about permanently-gated Streaming workers) — none caused by a defect in this story, all pre-existing or open design questions surfaced incidentally. 2 findings rejected after verification (a false-positive file-mode report — this repo has `core.filemode=false`, so git never tracks it — and a `Clear()`-resets-to-null concern that exactly mirrors `BuildingStore.DefinitionId`'s identical, already-shipped, already-safe pattern).

**Follow-up review recommended:** true — 3 of the 8 patches touch determinism-adjacent code (a gating primitive's behavior, and the lobby-handshake content hash) in a story that already re-baselined all 23 goldens; worth an independent second pass for extra confidence given the blast radius, even though every patch is narrow, tested, and the full suite is green.

**Verification performed:** `dotnet build godot/godot.sln -c Debug` clean (0 errors) after every patch. `dotnet test ProjectChimera.Sim.Tests -c Release`: 1284 passed, 0 failed, 1 pre-existing skip (up from the pre-review-pass 1278, reflecting the 6 new review-patch tests). Golden re-baseline (`CHIMERA_GOLDEN_RECORD=1`, all 23 files) performed by the implementation pass, confirmed unaffected by this review pass's patches (none touch shipped scenario content — no shipped scenario authors `collection_model`/`requires_structure`/`resource_type=Crystal`, so the behavioral fixes have no golden-visible effect). Manual `/godot-verify` was not run (CLI verification passed clean; the spec's manual-check section is conditional on "if no CLI").

**Residual risks:** none blocking. See DW-77..80 for the four deferred items (in-app placement-tool gap likely closed by Story 6.4; unfolded per-worker gather state; a bounded O(nodes×buildings) gate-check cost; and an open design question on permanently-gated Streaming workers).

---

### Follow-up review pass (2026-07-09)

The `followup_review_recommended: true` from the pass above triggered an independent second review (four parallel layers: Blind Hunter, Edge Case Hunter, Verification Gap, Intent Alignment). It found one genuine correctness defect and several coverage gaps; all were patched (no bad_spec, no intent_gap). Full breakdown in the Review Triage Log.

**Correctness fix (medium):** Crystal loads were silently mis-credited as Ore when a Build command interrupted a returning worker — `BuildingSystem` clears `GatherTarget` (which the deposit used to resolve Ore-vs-Crystal), sending the load through the always-`AddOre` fallback. This was newly reachable because Crystal production is new this story. Fixed by carrying the resource kind on the worker (`EntityWorld.CarryResourceType`, snapshotted at gather time, dispatched at deposit) — decoupling deposit routing from `GatherTarget` entirely.

**Files changed this pass:**
- `godot/src/Core/EntityWorld.cs` — new `CarryResourceType` per-worker SoA field (init/reset/Clear paired with `CarryAmount`; unfolded from `SimChecksum`, same as `CarryAmount`).
- `godot/src/Economy/GatheringSystem.cs` — snapshot the carried kind at gather; deposit via a new `CreditKind(ResourceKind,…)` (removing the fragile `GatherTarget`-resolution + duplicate `node<0` Ore fallback); `TickIncomeNodes` defensive skips for a Neutral owner and a non-positive period.
- `godot/ProjectChimera.Sim.Tests/Economy/GatheringSystemTests.cs` — +5 tests (Build-interrupt Crystal regression; Income gate counter-frozen at `period=3`; radius excluding-direction; Neutral-owner and non-positive-period guards).
- `godot/ProjectChimera.Sim.Tests/Validation/CanonicalModelHashTests.cs` — the single-field order-stability test parameterized across all 6 new sort keys (`[Theory]`, +5 net cases).

**Verification performed:** `dotnet build godot/godot.sln -c Debug` clean (0 errors). `dotnet test ProjectChimera.Sim.Tests -c Release`: **1294 passed, 0 failed, 1 pre-existing skip** (up from 1284). **Zero golden `.txt` files touched** — confirmed via `git status`; the fix moves no golden because worker carry state is unfolded from `SimChecksum` and the Ore deposit path is byte-identical (only the previously-broken Crystal-on-interrupt path changes). No `AlgoVersion` bump (no fold changed), so the pinned-hash tests pass unchanged.

**Follow-up review recommended:** true — the correctness fix, though localized and comprehensively tested, adds a new shared per-entity field to `EntityWorld` and alters the credit-routing code path in determinism-adjacent economy code; one more independent pass over the deposit-routing change and any other `GatherTarget` consumers adds confidence, consistent with the blast-radius caution from the first pass.

**Residual risks:** none blocking. The new `CarryResourceType` field is deliberately unfolded from `SimChecksum`, extending (not worsening) the pre-existing DW-78 gap; it is deterministically derived from node `ResourceType` at gather time, so it introduces no new desync surface beyond what DW-78 already tracks.
