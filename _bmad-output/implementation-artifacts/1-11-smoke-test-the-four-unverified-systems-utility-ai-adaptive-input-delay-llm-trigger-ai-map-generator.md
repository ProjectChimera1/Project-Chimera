---
baseline_commit: 2cd247e34a40cb09ea8326b889787b320bbc7433
---

# Story 1.11: Smoke-test the four unverified systems (Utility AI, Adaptive Input Delay, LLM Trigger, AI Map Generator)

Status: ready-for-dev

<!-- Context engine analysis completed — comprehensive developer guide. Validation optional: run validate-create-story before dev-story. -->
<!-- 1.11 is the FIRST post-M1 story (M1 [1.1–1.10c] is GREEN). It is a VERIFICATION story: FR-45 says the four
     "unverified" systems must each pass a documented smoke-test checklist, with sim-touching paths proven
     deterministic against the now-trustworthy golden baseline. Four heterogeneous systems → four task blocks.
     The headline reality (from live source investigation): all four EXIST, but each one's AC assumes something
     that is only partly true. This story's value is to prove what IS deterministic, HONESTLY document what is
     not, and surface the two real gaps (LLM-trigger validation is unenforced; the map generator has no RNG to
     seed). Two scope decisions for Alec are saved at the end — neither blocks starting Tasks 1–2. -->

## Story

As a solo developer who must trust these four never-verified systems before later epics build on them,
I want each system (Utility AI, Adaptive Input Delay, LLM Trigger System, AI Map Generator) to pass a documented smoke-test checklist — with every sim-touching path asserted deterministic via the golden harness and every non-deterministic path proven to be funnelled behind the validator gate,
so that I know these systems actually function and cannot silently inject nondeterminism into the lockstep tick.

## Acceptance Criteria

**Verbatim from `epics.md` (Story 1.11, lines 766–784; covers FR-45, AR-13, AR-39; depends on 1.10c):**

> **Given** the Utility AI **When** its smoke-test checklist runs in the Tier-1/Tier-2 harness **Then** it produces decisions deterministically (golden checksum byte-identical across two runs) and the checklist passes
> **Given** the Adaptive Input Delay in LockstepManager **When** its smoke-test checklist runs **Then** delay negotiation is deterministic and agreed across peers (no desync from delay change) and the checklist passes
> **Given** the LLM Trigger System **When** its smoke-test checklist runs **Then** any LLM output that reaches the sim is funnelled through the ScenarioValidator (validated-only) so non-deterministic generation never mutates the tick directly, and the checklist passes
> **Given** the AI Map Generator **When** its smoke-test checklist runs with a fixed seed via SimRng **Then** it generates a byte-identical map across two runs and the checklist passes

> ⚠ The epic's own quality-review note (epics.md:786): *"define the concrete smoke-test checklist per system; for the LLM-Trigger and Utility-AI items specify exactly which sim-touching outputs are checksummed (not 'decisions deterministically')."* The decomposed ACs below discharge that note — each names the exact hashed/asserted artifact.

### Decomposed, testable acceptance criteria

**AC1 — Utility AI: same-machine decision determinism, proven on an AI-*active* scenario.**
A new AI-active Tier-1 golden scenario (P2 has a production building + ore + ≥ attack-threshold combat units, so `AiOpponentSystem` actually builds/trains/attacks instead of no-op'ing) is stepped through the real 9-system `SimulationHost`. **The hashed artifact is the existing per-tick `SimChecksum` sequence** (the AI's decisions reach it transitively: building spawns → `Alive`/`Health`/`ConstructionTimer`; ore spends → `Ore`/`SupplyUsed`/`SupplyCap`; attack commands → entity `Position` one tick later). Two in-process runs produce a byte-identical sequence (AC1a), and the sequence reproduces against a committed golden file (AC1b). The scenario must be proven *non-vacuous* — i.e. the AI demonstrably acts (assert the checksum stream differs from a quiescent run, or assert a building-count/command delta). **Pin `AiDifficulty` explicitly** (the score weights branch on it). **This golden is recorded once on this machine and is NOT added to the WSL cross-platform gate** — see AC1c.

**AC1c — The float boundary is documented, not fixed.** `AiOpponentSystem` scores actions with raw `float` and picks the winner via a `float >` compare (`AiOpponentSystem.cs:266–271`). Same-machine/same-JIT this is deterministic, so AC1 holds. Cross-platform it is the known `float→Fixed` debt (D2). The smoke-test checklist + the new golden's header must state: **PROVEN = same-machine AI-active determinism; NOT PROVEN = cross-platform AI-active determinism**, and the AI-active golden is deliberately excluded from the 1.10c Win↔Linux WSL gate until the float→Fixed migration lands. **Do not migrate the AI to `Fixed` in this story.**

**AC2 — Adaptive Input Delay: deterministic, commutative agreement + no desync across a delay change.**
Two layers, both required:
- **AC2a (Tier-1 pure-math gate):** the RTT→delay computation and the cross-peer agreement rule are extracted to a Godot-free helper and unit-tested: identical RTT input → identical clamped delay output across two calls; the clamp pins both ends (`ComputeTargetDelay(0)==MIN_DELAY==2`, `ComputeTargetDelay(huge)==MAX_DELAY==12`); and the agreement rule is **commutative** (`Agree(a,b)==Agree(b,a)`) — the property that guarantees both peers pick the same delay regardless of who proposed.
- **AC2b (integration checklist run):** a loopback (or LAN) run forces a delay change mid-match and asserts both peers' per-tick checksums stay matched for 300+ ticks across the transition (server reports all-peers-MATCH, zero DESYNC/HALT), with both logs showing the same `Delay: A → B` transition tick. This proves the *apply-tick* invariant (the real desync risk: both peers must commit the same delay at the same tick).
- **AC2c — the unclamped-`theirDelay` receipt bug is documented, not fixed.** `agreedDelay = Math.Max(myDesired, theirDelay)` (`LockstepManager.cs:556`) does not re-clamp the untrusted wire byte; the checklist records this as **owned by Story 9.4** (server-dictated delay + receipt re-clamp + ACK-commit). **Do not fix it here.**

**AC3 — LLM Trigger: validated-only ingestion is real and asserted (DECISION-GATED — see Decision #1).**
Today an accepted LLM trigger is written straight into `ScenarioData.Triggers[]` and reaches the tick (`ScenarioDirector`) **without** passing `ScenarioValidator`/`Validated<T>` — the AR-39 gate validates scenario geometry but never inspects `Triggers[]` (the only check is a bypassable, auto-fixing, presentation-side `LLMService.Validate`). The smoke test makes the validated-only guarantee real:
- **AC3a:** `ScenarioValidator.Validate` is extended with a `Triggers[]` validation pass; a well-formed trigger passes (`Ok==true`, wraps the same model) and a malformed/nondeterministic trigger is **rejected with a single located error** (e.g. invalid faction slot, unknown `building_type`, invalid operator, out-of-16.16-range spawn coordinate, dangling unit/timer reference) and never reaches the tick.
- **AC3b:** the no-bypass invariant is asserted structurally — `Validated<T>` remains sole-minted by `ScenarioValidator` (`ValidatedMintingTests` source-scan, extended in spirit to cover trigger content), and the test documents the one residual editor-accept bypass (`TriggerEditorPanel.OnAcceptPressed` appends without the applier) as the routing follow-up.
- **AC3c:** the LLM call itself is confirmed off-tick/editor-time (presentation), so the test never invokes a real LLM — it feeds crafted `TriggerDefinition`s through the pure-C# gate. AR-13's random-effect rule stays **reserved** (no random effect type exists pre-Epic-2) — assert it as a documented pending case, do not fabricate a random effect type.

**AC4 — AI Map Generator: byte-identical generation across two runs (DECISION-GATED — see Decision #2).**
The generator is **purely LLM-driven** (Claude/Ollama → a `ScenarioData`); it has **no `SimRng`, no `System.Random`, no Godot RNG, no noise** — there is no algorithmic seed to fix. Default scope (Option A): prove the *deterministic core* (validation + serialization) is byte-identical and document the LLM boundary.
- **AC4a:** a committed **canned golden LLM response** (a fixed JSON string, embedded-resource like the existing goldens) is run through `LLMService.ValidateScenario` + `ScenarioSerializer.Serialize` **twice**; **the hashed artifact is the serialized validated `ScenarioData` bytes** (via `ScenarioSerializer.ComputeFileHash` / FNV-1a). Both runs are byte-identical to each other and to a pinned golden hash, and all 7 validation passes pass (the "checklist passes" half).
- **AC4b:** the checklist documents that the **LLM generation step is out of the determinism boundary** (authoring-time, single-machine, non-deterministic by nature) and that the AC's literal "fixed seed via SimRng" is **N/A for the as-built generator** (it has no RNG); routing generation through `SimRng` would require building a *procedural* generator (Option B — a separate feature story, see Decision #2). **Do not build a procedural generator under the default scope.**

**AC5 — The documented smoke-test checklist (FR-45 "documented checklist").**
A committed markdown checklist (mirroring `godot/tools/*-determinism-runbook.md`) lists, per system: what is being verified, how to run it, the PASS criteria, and the honest caveat/coverage boundary. Each of the four systems has a ✅ or a documented ⚠-with-owner. The checklist's per-system verdict is recorded in this story's Change Log.

**AC6 — STATUS.md is reconciled to the as-built reality.** The investigation found STATUS.md stale on these systems; update the relevant rows (see [STATUS.md reconciliation](#statusmd-reconciliation-read-this)). No sim regression: the existing Tier-1 suite stays green and the four committed determinism goldens stay byte-identical/unmoved.

_Covers: **FR-45** (four unverified systems pass smoke-test checklists), **AR-13** (SimRng is the only sim randomness; validator forbids unseeded randomness), **AR-39** (single fail-closed `ScenarioValidator` gate). Depends on: **1.10c** (DONE — M1 is GREEN; this is the first post-M1 verification story, run against an already-trustworthy baseline)._

---

## SCOPE — read this before coding

### ✅ IN scope (this story)
1. **Utility AI** — a NEW AI-active Tier-1 golden scenario + two-run/golden determinism test + one new committed golden file (AC1). Same-machine only; documented float caveat (AC1c).
2. **Adaptive Input Delay** — extract the RTT→delay + agreement math to a Godot-free helper, add a Tier-1 pure-math test (AC2a), and a loopback/LAN checklist run proving no-desync-across-delay-change (AC2b). Document the unclamped-receipt bug (AC2c).
3. **LLM Trigger** (Decision #1 default = extend the gate) — add a `Triggers[]` validation pass to `ScenarioValidator` + Tier-1 tests asserting valid-passes / malformed-rejected / no-bypass (AC3).
4. **AI Map Generator** (Decision #2 default = Option A) — a canned golden LLM JSON + a Tier-1 validate-then-serialize byte-identical test + the SimRng-N/A documentation (AC4).
5. **The documented smoke-test checklist** doc (AC5) and the **STATUS.md reconciliation** (AC6).

### ❌ OUT of scope (do NOT do these here)
- **Do NOT migrate `AiOpponentSystem` from `float` to `Fixed`.** That is the D2 debt (tracked in `deferred-work.md` / `[[chimera-mp-disconnect-ai-takeover-reconnect]]`). This story *documents* the cross-platform boundary; it does not close it. Corollary: **do NOT add the AI-active golden to the 1.10c WSL `cross-platform-determinism-check` gate** (it would (rightly) risk a Win↔Linux RED on the float path).
- **Do NOT build server-dictated adaptive input delay, the receipt-side `[2,12]` re-clamp, ACK-gated commit, or the start-state-hash handshake.** Those are **Story 9.4** (and 1.9a's spec already says "Do NOT build server-dictated adaptive input delay"). 1.11 smoke-tests the *as-built P2P* delay only.
- **Do NOT fix the unclamped `Math.Max(myDesired, theirDelay)` at `LockstepManager.cs:556`.** Document it; 9.4 owns it.
- **Do NOT call a real LLM in any test.** Both `GenerateTriggerAsync` and `GenerateScenarioAsync` hit the network and are non-deterministic by design. Tests feed crafted/canned data through the pure-C# seams only.
- **Do NOT build a procedural (SimRng-seeded) map generator** under the default scope. If Alec picks Option B (Decision #2), it becomes its own feature story — it is NOT smoke-test-sized.
- **Do NOT change any existing `*.golden.txt`, `SimChecksum`, the tick order, the 60-tick interval, `FixedPoint`, or any existing sim behavior.** New goldens are *added*; existing goldens are never re-recorded. If a smoke test goes RED on an *existing* path, that is a finding to file — not a license to edit a golden.
- **Do NOT add a new NuGet `PackageReference`** (the `DependencyHygieneTests` one-package guard + `--locked-mode` restore stay green).
- **Do NOT fold triggers/effects into `SimChecksum` or `CanonicalModelHash`.** Trigger/effect canonicalization is **Epic 7** (the AR-39 gate validates triggers as *input*; hashing them is separate, later work).

### Scope reconciliation (read this first) — the as-built reality of each system
The four systems are heterogeneous. The investigation (live source, 2026-06-25) found:

| System | As-built reality | What the AC assumed | The smoke-test's real job |
|---|---|---|---|
| **Utility AI** (`AiOpponentSystem.cs`) | BUILT & functional; pure C#/Godot-free (Tier-1 instantiable); utility-scores 6 actions; **plays Player2**; system **[7]**. Decisions reach `SimChecksum` transitively. | "produces decisions deterministically" | Build an AI-*active* scenario (existing goldens starve it) + checksum it; prove same-machine determinism; **document the float→cross-platform boundary**. |
| **Adaptive Input Delay** (`LockstepManager.cs`) | BUILT (STATUS.md says "📋 Deferred" — **STALE**). `INPUT_DELAY=4`, clamp `[2,12]`, RTT ping/pong, `DelayProposal` `Math.Max` agreement. **Godot-coupled → outside the Tier-1 wall.** Delay value never enters the hashed tick. | "delay negotiation deterministic + agreed, no desync" | Extract pure delay-math → Tier-1 test (determinism + commutative agreement) + loopback run proving the apply-tick invariant. **Document the unclamped-receipt bug (9.4 fixes).** |
| **LLM Trigger** (`LLMService.cs` + `TriggerEditorPanel.cs`) | Generation WORKS (real Anthropic + Ollama). **But the validated-only guarantee is UNENFORCED** — triggers bypass `ScenarioValidator` entirely. | "LLM output … funnelled through ScenarioValidator (validated-only)" | **This is a real GAP.** Extend the AR-39 gate to validate `Triggers[]`, then assert validated-only ingestion (Decision #1). |
| **AI Map Generator** (`MapGeneratorPanel.cs` + `LLMService.cs`) | LLM-driven, output is a `ScenarioData`. **Zero RNG of any kind** (no SimRng/Random/noise). The "7-pass" is *validation*, not generation. Core seam (`ValidateScenario` + `ScenarioSerializer`) is Godot-free/Tier-1. | "fixed seed via SimRng → byte-identical map" | The AC's literal "seed via SimRng" **describes a generator that doesn't exist**. Default (Option A): prove validate+serialize determinism against a canned golden + document the gap (Decision #2). |

---

## Tasks / Subtasks

- [ ] **Task 1 — Utility AI smoke test: AI-active golden determinism (AC: 1, 1c).**
  - [ ] Add a new Tier-1 scenario `godot/ProjectChimera.Sim.Tests/Golden/AiActiveScenario.cs`, modelled on `GoldenScenario.cs` but **inverting the AI-starvation recipe**: give `Faction.Player2` a completed production building (so `AdoptPreplacedBuildings` picks it up → the training loop fires) and/or starting ore > `COST_BARRACKS` (=100), a deposit base + resource node + worker so ore accrues, and **≥ the attack threshold** of idle/stop combat units (5 on `Normal`, 3 on `Hard`) so `ScoreLaunchAttack` fires. Build through `SimulationHost.Create(NullLogSink.Instance, …, aiLevel: <pinned>)` so the AI ticks at its real index 7 with real neighbours. All positions/stats authored in `Fixed` (no `Fixed.FromFloat`).
  - [ ] Add `godot/ProjectChimera.Sim.Tests/Golden/AiActiveGoldenTests.cs`: (a) two in-process `Build()`+step runs produce a byte-identical `SimChecksum` sequence (capture via `Host.SetChecksumSink`, `ChecksumInterval=1`); (b) the sequence reproduces a committed `Golden/ai-active-scenario.golden.txt`; (c) **non-vacuity** — assert the AI demonstrably acted (e.g. P2 building count grew, or P2 issued AttackMove, or the stream differs from a quiescent baseline). Reuse `GoldenChecksumReplay` record/verify + the `CHIMERA_GOLDEN_RECORD` one-shot record flow exactly as the existing goldens do.
  - [ ] Register the new golden as an embedded resource in `ProjectChimera.Sim.Tests.csproj` (`<None Remove>` + `<EmbeddedResource Include>` pair, next to the four existing goldens) and ensure it is LF-only (the 1.10c `CrossPlatformGoldenGuardTests` will assert this — keep it green).
  - [ ] **AC1c documentation:** the golden file header comment + the checklist (Task 5) state PROVEN=same-machine / NOT-PROVEN=cross-platform, and that this golden is **excluded from the WSL cross-platform gate**. Confirm `git status --short -- '*.golden.txt'` shows only the NEW file (no existing golden moved).

- [ ] **Task 2 — Adaptive Input Delay smoke test (AC: 2, 2a, 2b, 2c).**
  - [ ] **AC2a (Tier-1 pure-math):** extract the RTT→delay + agreement math out of `LockstepManager` into a Godot-free static helper `godot/src/Multiplayer/DelayMath.cs` (e.g. `internal static int ComputeTargetDelay(float smoothedRttMs)` lifting the body of `LockstepManager.cs:527–532` verbatim, and `internal static int AgreeDelay(int myDesired, int theirDelayRaw)` wrapping the `:556` `Math.Max`). Refactor `LockstepManager` to *call* the helper (behavior-neutral). Add an explicit `<Compile Include=".../src/Multiplayer/DelayMath.cs" …/>` line to `godot/SimSources.props` (the `src/Multiplayer/` folder is deliberately NOT globbed — mirror the existing single-file `ReplayRecorder/ReplayPlayer/NetworkCommand` includes). Add `[assembly: InternalsVisibleTo("ProjectChimera.Sim.Tests")]` if not present.
  - [ ] Add `godot/ProjectChimera.Sim.Tests/Multiplayer/DelayMathTests.cs`: same RTT → same clamped delay across two calls; `ComputeTargetDelay(0f)==MIN_DELAY (2)` and `ComputeTargetDelay(10000f)==MAX_DELAY (12)`; a monotonic RTT→delay table is stable; `AgreeDelay(3,5)==AgreeDelay(5,3)==5` (commutativity). **Write the `AgreeDelay` test to assert the CURRENT unclamped behavior** so it documents the AC2c gap and will flag when 9.4 adds the re-clamp.
  - [ ] Note the analyzer: `DelayMath.cs` uses `float`, so the 1.10b advisory `CHM0001` will flag it (like `AiOpponentSystem`). That is **advisory, expected, and correct** — the delay value is a non-hashed latency/buffering concern, not sim state. Confirm the analyzer gate stays green (advisory ≠ blocking on master).
  - [ ] **AC2b (integration):** run a loopback delay-change check using the EXISTING tooling — `godot/tools/loopback-desync-smoke.ps1` (1 server + 2 auto-join clients) and/or `LoopbackDesyncSelfTest.cs`. Force the `4→N` delay transition deterministically (inject a large `_smoothedRttMs` via a test hook, or apply `tc netem` latency per the LAN runbook §6). Assert both peers' checksums stay matched 300+ ticks across the transition (server logs all-peers-MATCH, zero DESYNC/HALT; both logs show the same `Delay: A → B` tick). **The two-machine LAN variant is PARKED** (1 machine — same posture as 1.9b AC4); the single-machine loopback run is achievable now and is the recorded proof.
  - [ ] **AC2c documentation:** record in the checklist (Task 5) that `LockstepManager.cs:556` is unclamped on receipt and is **owned by Story 9.4** — not fixed here.

- [ ] **Task 3 — LLM Trigger validated-only smoke test (AC: 3) — DECISION-GATED (#1; default = extend the gate).**
  - [ ] **AC3a:** extend `ScenarioValidator.Validate` with a `Triggers[]` validation pass (a new loop after the units loop, returning a single located error on first failure — mirror the existing `buildings`/`units` loops exactly: `$"scenario.triggers[{i}]…"`). Validate: faction slots in range / declared, known `building_type` (reuse `IsKnownBuildingType`), known operator, spawn coordinates in 16.16 range + map bounds (reuse `CheckCoord`), no dangling unit/timer references. Keep it pure (no throw/log). Do **not** fold triggers into any hash.
  - [ ] Add `godot/ProjectChimera.Sim.Tests/Validation/TriggerValidationTests.cs` (mirror `NegativeValidationTests.cs`'s `ValidModel()` helper): a well-formed trigger → `Ok==true` and `r.Value` wraps the same model; each malformed case (invalid faction slot, unknown building_type, invalid operator, out-of-range spawn X, dangling ref) → `Ok==false` with a located `scenario.triggers[...]` error.
  - [ ] **AC3b (no-bypass):** assert `Validated<T>` is still sole-minted by `ScenarioValidator` (extend/keep `ValidatedMintingTests` green). **Document** (test comment + checklist) the residual editor-accept bypass: `TriggerEditorPanel.OnAcceptPressed` (`:328`) appends straight to `_scenario.Triggers` and `ScenarioApplier.Apply` passes the unwrapped model to `ScenarioDirector.LoadScenario` (`:125`) — so the *gate logic* now rejects bad triggers, but fully closing the editor/file-load ingestion seam (routing both through the validator) is a small follow-up. Pick the routing depth per Decision #1.
  - [ ] **AC3c:** keep AR-13's random-effect rule **reserved** — no random trigger-effect type exists pre-Epic-2; assert it as a documented pending case (xUnit `Skip` or a TODO referencing Story 2.3). Do not invoke a real LLM anywhere.

- [ ] **Task 4 — AI Map Generator smoke test (AC: 4) — DECISION-GATED (#2; default = Option A).**
  - [ ] **AC4a:** commit a canned golden LLM response `godot/ProjectChimera.Sim.Tests/Golden/ai-map-golden-response.json` as an embedded resource (the recorded generator output — it *replaces* the seed). Add `godot/ProjectChimera.Sim.Tests/Golden/AiMapGeneratorGoldenTests.cs`: run `LLMService.ValidateScenario(goldenJson, ctx)` **twice**, `ScenarioSerializer.Serialize(...)` both results with the fixed options, and assert (i) the two serializations are byte-identical, (ii) they equal a pinned golden hash (`ScenarioSerializer.ComputeFileHash` / FNV-1a — so a silent serializer-format drift fails), and (iii) all 7 validation passes pass on the golden. Mirror `CanonicalScenarioTests.P2_4_Scenario_IsDeterministic`'s run-twice-then-`SequenceEqual` pattern. **No network call.**
  - [ ] **AC4b documentation:** in the checklist (Task 5) record that the LLM generation step is out of the determinism boundary (authoring-time, single-machine) and the AC's "fixed seed via SimRng" is **N/A** for the as-built generator (it has no RNG). If Alec chose Option B, this task is replaced by the procedural-generator feature work (out of smoke-test scope).

- [ ] **Task 5 — The documented smoke-test checklist (AC: 5).**
  - [ ] Add `godot/tools/four-systems-smoke-test-checklist.md`, mirroring the structure of `lan-determinism-runbook.md`: one section per system with **(a)** what is verified, **(b)** how to run (the exact Tier-1 filter and/or the loopback command), **(c)** PASS criteria, **(d)** the coverage caveat / owner of any residual gap. End with a per-system verdict table the Change Log mirrors.
  - [ ] Cross-link the caveats: AI float→Fixed (D2), delay receipt-clamp (9.4), LLM-trigger editor-accept routing (Decision #1 follow-up), map-gen procedural-generator (Decision #2 / Option B).

- [ ] **Task 6 — STATUS.md reconciliation (AC: 6).** Update the stale rows (details in [STATUS.md reconciliation](#statusmd-reconciliation-read-this)): adaptive delay (📋→built-but-as-built-P2P), Utility AI (clarify the as-built `AiOpponentSystem` IS utility-scored), LLM trigger + map gen (built-with-documented-caveats, not untouched). Keep edits factual and located.

- [ ] **Task 7 — Regression, checklist run, code review, sprint status.**
  - [ ] Re-run the full Windows CI command `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release` → green (was **210** at 1.10c close on Windows; **do not hardcode** the count — rely on the exit code). Confirm the four EXISTING goldens are byte-identical/unmoved and only NEW goldens/tests were added.
  - [ ] Run the smoke-test checklist end-to-end; record the per-system verdict (date, what passed, the documented caveats) in the Change Log (AC5).
  - [ ] Run `gds-code-review` (3-layer adversarial, fresh-context/different LLM). On PASS, set this story `done` in `sprint-status.yaml` and update `last_updated`.

---

## Dev Notes

### Developer context — why this story exists and the one framing that makes it tractable
M1 (the determinism floor, 1.1–1.10c) is GREEN: the sim is proven byte-identical across two runs, across a record→replay round-trip, and across Windows↔Linux — *for the paths the four committed goldens exercise*. Those goldens deliberately keep four risky systems out of the picture (the AI is starved; no LLM, no adaptive-delay change, no map-gen). FR-45 is the story that finally points the trustworthy harness AT those four systems and asks: do they work, and are their sim-touching paths deterministic?

**The framing that makes four heterogeneous systems tractable:** treat each system by *where its nondeterminism lives*, not by lumping them together.
- **Utility AI** — nondeterminism (float) is *inside the tick* but *same-machine-stable*; so checksum it and document the cross-platform boundary.
- **Adaptive delay** — nondeterminism (float + wall-clock) is real but *never enters the hashed tick* (it only shifts buffer timing); so the risk is the apply-tick agreement, which a loopback run proves, plus a cheap pure-math gate.
- **LLM trigger + map gen** — nondeterminism is the *LLM itself*, which lives at *authoring time, off-tick*; so the job is to prove the **gate** (validator / validated serialization) holds, never to make the LLM deterministic.

Two of the four (trigger, map gen) surfaced a genuine gap rather than a clean pass — that *is* the value of a smoke test. Both are saved as decisions with a recommended default that keeps the story smoke-sized.

### Per-system deep-dive (the crux of each, with file:line)

**1. Utility AI — `godot/src/AI/AiOpponentSystem.cs` (pure C#, Godot-free, system [7]).**
- Self-described "Utility-scored AI opponent" (`:11–12,27`); implements `ISimSystem`; `Tick(EntityWorld, Fixed dt)` at `:99–117`. Plays `Faction.Player2` (`:31`).
- Each tick: prune dead buildings → train on every idle completed production building (`:104–108`) → tick attack cooldown → `BuildSnapshot` (`:139–192`) → `ExecuteBestAction` scores 6 actions and dispatches the max (`:264–290`).
- **Mutations that reach `SimChecksum`:** `_buildings.Create` (Alive/Health/ConstructionTimer — hashed `SimChecksum.cs:65–71`); `_resources.SpendOre/AddOre` + supply (Ore/SupplyUsed/SupplyCap — hashed `:79–89`); `DoLaunchAttack` sets `CommandState/CommandGoal/MoveTarget` (NOT directly hashed, but `MovementSystem` moves the unit next tick → `Position` is hashed, a one-tick lag).
- **The float boundary (do NOT fix):** scores are `float` (`:200–248`); the winner is chosen by `if (score > best)` at `:271`. No `Random`/clock/dictionary-enumeration anywhere; iteration is ascending-id throughout. Same-machine = deterministic (AC1 holds). Cross-platform = the D2 debt (a near-tie `>` can flip → different action → divergent hash). Constants: `COST_BARRACKS=100` (`:35`), attack threshold 5 Normal / 3 Hard (`:88`), difficulty weights `:85–90`.
- **Existing goldens starve it on purpose:** `GoldenScenario.cs:151–156` and `MultiFactionScenario.cs:123–127` (P2 = 3 units < threshold 5, 0 ore → no-ops deterministically). That's why AC1 needs a *new* AI-active scenario.

**2. Adaptive Input Delay — `godot/src/Multiplayer/LockstepManager.cs` (Godot-coupled).**
- `INPUT_DELAY=4` start (`:39`), `MIN_DELAY=2` (`:41`), `MAX_DELAY=12` (`:42`), `BUFFER_SIZE=16` (`:44`, must stay > `MAX_DELAY+1`). RTT via ping/pong every `PING_INTERVAL_TICKS=60` (`:50`); EWMA `RTT_ALPHA=0.125` (`:49`, `:514`); `ComputeTargetDelay` = `Clamp(ceil(owlMs/TICK_MS)+1, MIN, MAX)` (`:527–532`).
- Agreement: `DelayProposal` packet (0x42); receiver picks `agreedDelay = Math.Max(myDesired, theirDelay)` (`:556`, **commutative** → both peers converge) at the later apply-tick; `CommitDelayChange` (`:604–626`) seeds the gap ticks identically on both peers so the transition is desync-free.
- **The delay value never enters the hashed tick** — it only sets `issueTick = currentTick + _currentDelay` (`:298`) and which buffer slot is consumed. `ApplyOrders` (`:655–705`) writes sim state from the command payload only. So determinism holds iff both peers commit the same delay at the same tick (the apply-tick invariant AC2b proves).
- **Godot coupling → outside Tier-1:** `using Godot;` (`:3`), `Time.GetTicksMsec()` in the RTT path (`:500,510`), `GD.Print`, holds `ENetTransport`. `src/Multiplayer/` is NOT in `SimSources.props` (only 3 explicit Replay files + `Server/**`). Zero existing tests reference it. → AC2a needs the `DelayMath.cs` extraction to get a Godot-free testable seam.
- **The unclamped bug (do NOT fix — 9.4):** `Math.Max(myDesired, theirDelay)` (`:556`) does not re-clamp the untrusted wire byte `theirDelay` (0–255); a forged proposal could push delay ≥ `BUFFER_SIZE` and corrupt the ring buffer. Story 9.4 (`epics.md:2262–2278`) owns the receipt re-clamp + ACK-commit + server dictation. 1.9a already says "Do NOT build server-dictated adaptive input delay."

**3. LLM Trigger — `godot/src/AI/LLMService.cs` + `src/CreationSuite/TriggerEditorPanel.cs`.**
- Generation is REAL: `GenerateTriggerAsync` (`LLMService.cs:101`) → live Anthropic HTTPS (`claude-sonnet-4-6`, `:63`) via `System.Net.Http.HttpClient`, Ollama (`llama3.1:8b`) fallback (`:212`). Off-tick (`Task.Run` `:110`), marshalled back via a queue drained in `_Process`. `TriggerEditorPanel` is a Godot `Node` (`:23`), toggled by **L** in Edit mode, hidden in Play mode (`:345–349`) → confirmed authoring-time/presentation.
- **THE GAP:** on Accept, `OnAcceptPressed` (`:328`) appends the trigger straight into `_scenario.Triggers` — **no validator call.** `ScenarioDirector.LoadScenario` takes a raw `ScenarioData` (`ScenarioDirector.cs:72,74`), not `Validated<>`. `ScenarioValidator.Validate` (`ScenarioValidator.cs:59–170`) checks map/slots/nodes/buildings/units and **never reads `m.Triggers`** (AR-13 reserved at `:161–167`). The only "validation" triggers get is the bypassable, auto-fixing, presentation-side `LLMService.Validate` (`:258–323`) — which clamps instead of rejecting and is skipped entirely when a trigger is loaded from a saved scenario file. **So the validated-only guarantee the AC requires is currently UNENFORCED.**
- **Testable seam (Tier-1, no LLM):** `new ScenarioValidator().Validate(ScenarioData)` with a crafted `Triggers[]` — `TriggerDefinition`, `ScenarioValidator`, `Validated<T>`, `ScenarioDirector` are all pure C# / already in the Tier-1 set. `ValidatedMintingTests` (source-scan) already proves `Validated<T>` is sole-minted by the validator.

**4. AI Map Generator — `godot/src/CreationSuite/MapGeneratorPanel.cs` + `src/AI/LLMService.cs`.**
- **LLM-driven, not procedural.** `MapGeneratorPanel` (Godot `Node`, **M** in Edit mode) → `LLMService.GenerateScenarioAsync` → Claude/Ollama JSON → deserialized to a `ScenarioData` (`LLMService.cs:508`). Output is a scenario (slots/nodes/buildings/units/triggers), NOT a heightmap; terrain is flat/decoupled. Applied via a scene reload (`MainScene.cs:1047–1051`) — authoring-time, not in the sim tick.
- **Zero RNG:** grep finds no `System.Random`/`RandomNumberGenerator`/`GD.Rand*`/`Noise`/`SimRng` use in the generator — the only entropy is the LLM. There is no seed on `MapGeneratorContext`, none sent to Claude/Ollama. So the AC's "fixed seed via SimRng" has **nothing to seed**.
- The "7-pass" is `ValidateScenario` (`:491–589`) — a *validation* pipeline with clamps (`Supply<=0→400`, `Rate<=0→5` at `:556–557`), not generation.
- **Testable seam (Tier-1, no LLM):** `LLMService.ValidateScenario` + `ScenarioSerializer.{Serialize,ComputeFileHash}` are all Godot-free and already in the Tier-1 assembly (`SimSources.props:30` globs `src/AI/**`). The map *is* a `ScenarioData`, so the byte-identical artifact to hash is its **stable JSON serialization** (floats are legal at authoring time; they quantize to `Fixed` only at sim ingest, so the Fixed-only rule is not violated here).

### Architecture compliance
- **AR-39 (single fail-closed gate):** `ScenarioValidator` is THE pre-tick gate; AC3 extends it to cover triggers rather than adding a second validator. `Validated<T>` stays sole-minted (`ValidatedMintingTests`). Pure: never throw/log.
- **AR-13 (SimRng is the only sim randomness):** the map generator has *no* sim randomness (it's authoring-time LLM); SimRng is unaffected. The validator's random-effect rule stays reserved until Epic 2's effect schema exists.
- **The sim spine stays Godot-free + Tier-1:** new AI golden + trigger-validation tests compile into `ProjectChimera.Sim.Tests` with zero Godot. The one new sim-folder file (`DelayMath.cs`) is Godot-free and added to `SimSources.props` (so the analyzer covers it too).
- **Determinism law (NFR-4):** new test scenarios author all state in `Fixed` (no `Fixed.FromFloat`), iterate ascending-id, no wall-clock, no unseeded randomness. The AI-active golden is the one exception that *contains* float (the AI's scoring) — hence its same-machine-only caveat and exclusion from the cross-platform gate.
- **AR-37 boundary:** the 1.10c WSL cross-platform gate must NOT gain the AI-active golden (float path). The four existing goldens stay the cross-platform set.

### File structure requirements
**Create:**
- `godot/ProjectChimera.Sim.Tests/Golden/AiActiveScenario.cs` — AI-active scenario builder (AC1).
- `godot/ProjectChimera.Sim.Tests/Golden/AiActiveGoldenTests.cs` — two-run + golden + non-vacuity (AC1).
- `godot/ProjectChimera.Sim.Tests/Golden/ai-active-scenario.golden.txt` — NEW golden (embedded; LF-only). Existing goldens untouched.
- `godot/src/Multiplayer/DelayMath.cs` — Godot-free RTT→delay + agreement helper (AC2a).
- `godot/ProjectChimera.Sim.Tests/Multiplayer/DelayMathTests.cs` — delay determinism + commutativity (AC2a).
- `godot/ProjectChimera.Sim.Tests/Validation/TriggerValidationTests.cs` — validated-only trigger ingestion (AC3).
- `godot/ProjectChimera.Sim.Tests/Golden/AiMapGeneratorGoldenTests.cs` — validate+serialize byte-identical (AC4).
- `godot/ProjectChimera.Sim.Tests/Golden/ai-map-golden-response.json` — canned LLM response (embedded) (AC4).
- `godot/tools/four-systems-smoke-test-checklist.md` — the documented checklist (AC5).

**Edit:**
- `godot/src/Core/Definitions/ScenarioValidator.cs` — add the `Triggers[]` validation pass (AC3a). The ONLY production-sim edit in the default scope.
- `godot/src/Multiplayer/LockstepManager.cs` — call into `DelayMath` (behavior-neutral extraction) (AC2a).
- `godot/SimSources.props` — add the explicit `DelayMath.cs` `<Compile Include>` (AC2a).
- `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` — register the two new embedded resources (AC1, AC4).
- `STATUS.md` — reconcile the stale rows (AC6).

**Do NOT touch:** any existing `*.golden.txt`; `SimChecksum.cs`; `GoldenChecksumReplay.cs`; `FixedPoint.cs`; `SimRng.cs`; the existing CI gates; `AiOpponentSystem`'s float math; `LockstepManager.cs:556` (the unclamped Max); `godot.csproj`.

### Testing requirements
- **Tier-1 xUnit, Godot-free** for AC1 (golden), AC2a (delay-math), AC3 (validator), AC4 (validate+serialize). They run everywhere `dotnet test` runs — including the WSL leg — EXCEPT the AI-active golden, which is recorded/verified same-machine and is **excluded from the cross-platform gate** (Task 1). Pattern: `CanonicalScenarioTests.P2_4_Scenario_IsDeterministic` (run-twice-`SequenceEqual`) and the existing golden tests (record/verify via `CHIMERA_GOLDEN_RECORD`).
- **AC2b is a script/loopback run, not an xUnit test** (it shells into the real server + ENet clients). Its proof is the recorded Change-Log run + the existing `loopback-desync-smoke.ps1`/`LoopbackDesyncSelfTest.cs` infra. The two-machine LAN variant is parked (1 machine).
- **Never call a real LLM in a test.** AC3/AC4 feed crafted/canned data through the pure-C# seams.
- **After every change:** re-run the full Windows suite (green; exit-code-driven, no hardcoded count) and `git status --short -- '*.golden.txt'` shows only the NEW `ai-active-scenario.golden.txt` (no existing golden moved).

### Previous-story intelligence (1.9a/1.9b + 1.10a/b/c — all DONE, code-reviewed PASS)
- **1.9a/1.9b** built the loopback + LAN determinism tooling AC2b reuses: `loopback-desync-smoke.ps1`/`.cmd`, `LoopbackDesyncSelfTest.cs` (DEBUG `--loopback-test`: real server + 2 ENet clients in-process, drives checksums, asserts MATCH then HALT), `lan-determinism-runbook.md` (§6 already has an adaptive-delay watch-item: "starts at 4 … adapts down toward 2 … confirm no desync around the delay reduction"). 1.9b parked its two-machine LAN gate (AC4) on 1 machine — AC2b takes the same posture.
- **1.9a's spec explicitly fences off this story's neighbour:** "Do NOT build … server-dictated adaptive input delay (SD-4 — even though Ping/Pong/DelayProposal wire exists). Epic 9 (Stories 9-3a/9-3b/9-4)." 1.11 confirms the as-built P2P version; 9.4 replaces it.
- **1.10b** added the determinism analyzer: `float` in a sim-set file is advisory `CHM0001` (not blocking on master). `DelayMath.cs` will trip it — expected and acceptable (the delay value is non-hashed). `AiOpponentSystem`'s float already trips it advisorily.
- **1.10c** documented the AI float→Fixed debt as cross-platform suspect #3 and kept the four goldens AI-quiescent precisely so float never reaches the cross-platform hash. AC1c inherits that boundary verbatim — the new AI-active golden stays OFF the WSL gate.
- **Conventions to respect:** never "fix" a red gate by re-recording a golden; assert invariants as Tier-1 tests, not CI-only shell steps; no hardcoded test counts; never set `CHIMERA_GOLDEN_RECORD` in CI/scripts; `[CallerFilePath]`/embedded-resource patterns for new goldens; new goldens are LF-only (the 1.10c guard enforces it).

### Git intelligence
- The repo auto-commits hourly as `[AutoSave] <timestamp>`; story work lands inside that stream. A red smoke test is a signal, not a commit blocker (advisory on master). `baseline_commit` for this story: `2cd247e`.
- Build/CI artifacts: `.github/workflows/determinism-gate.yml` (do not touch the existing jobs), `godot/SimSources.props` (add the one DelayMath include), `godot/ProjectChimera.Sim.Tests/*.csproj` (register the two new embedded resources). Tooling/runbooks live in `godot/tools/` — the new checklist joins them.

### Project Context Rules (from `_bmad-output/project-context.md`)
- **Sim/Presentation boundary is sacred.** LLM I/O, `TriggerEditorPanel`, `MapGeneratorPanel` are presentation/editor-side (Godot Nodes) — the smoke tests target the pure-C# sim seams behind them, never the Nodes. `DelayMath.cs` is Godot-free sim code.
- **`Fixed` (16.16) is the only sim numeric type.** New test scenarios author in `Fixed` (no `Fixed.FromFloat`). The two float exceptions (AI scoring, delay math) are *documented and bounded* — AI float is the D2 debt (same-machine-only); delay float never enters the hashed tick.
- **SimRng is the only sim randomness** and folds into `SimChecksum`. The map generator has none (LLM-driven, authoring-time) — that is the AC4 finding, not a violation.
- **Data-driven / no hardcoded balance:** the AI-active scenario authors stats as data (mirroring the existing goldens); don't hardcode new balance numbers in sim code.
- **Determinism rules** (ascending-id, no `Dictionary`/`HashSet` sim enumeration, no wall-clock, seeded RNG only, `InvariantCulture`) — the smoke tests *verify* these for the four systems; the AI is the only one with a known (documented) float exception.
- **Engine/runtime:** Godot 4.6.3, `net8.0`; project files `godot.csproj`/`godot.sln` (untouched). Brownfield style: reuse the harness + small additive slices; the only production edits are the validator trigger-pass and the behavior-neutral DelayMath extraction.

### References
- `_bmad-output/planning-artifacts/epics.md:766–786` — Story 1.11 (statement, 4 ACs, "Covers FR-45/AR-13/AR-39", the quality-review note). `:130` — FR-45. `:194` — **AR-13**. `:636,638` — **AR-39** (1.7's home). `:2262–2278` — **Story 9.4** (server-dictated delay — the neighbour NOT to build).
- `_bmad-output/project-context.md` — determinism law, Sim/Presentation boundary, `Fixed`/`SimRng` rules, LLM provider rules.
- Source — Utility AI: `godot/src/AI/AiOpponentSystem.cs` (`:99–117` tick, `:200–271` scoring/float, `:35,88` constants), `godot/src/Core/Sim/SimulationHost.cs:88–102` (AI at index 7), `godot/src/Core/SimChecksum.cs:43–100` (what's hashed), `godot/ProjectChimera.Sim.Tests/Golden/GoldenScenario.cs:151–156` + `MultiFactionScenario.cs:123–127` (AI starved), `godot/ProjectChimera.Sim.Tests/Sim/SystemOrderTest.cs` (pins index 7).
- Source — delay: `godot/src/Multiplayer/LockstepManager.cs` (`:39–50` constants, `:298` issueTick, `:527–532` ComputeTargetDelay, `:556` unclamped Max, `:604–626` CommitDelayChange, `:655–705` ApplyOrders), `godot/src/Multiplayer/NetworkCommand.cs:367–424` (Godot-free packet serialization), `godot/SimSources.props:34–45` (the single-file Multiplayer include pattern), `godot/tools/loopback-desync-smoke.ps1`, `LoopbackDesyncSelfTest.cs`, `godot/tools/lan-determinism-runbook.md` §6.
- Source — LLM trigger: `godot/src/AI/LLMService.cs` (`:101` GenerateTriggerAsync, `:258–323` private Validate), `godot/src/CreationSuite/TriggerEditorPanel.cs:23,328` (Node; accept-without-validator), `godot/src/Core/Definitions/ScenarioValidator.cs:59–170` (gate; no trigger checks; `:161–167` AR-13 reserved), `godot/src/Core/Definitions/Validated.cs`, `godot/src/Core/ScenarioDirector.cs:72,74,96` (raw-ScenarioData ingestion), `godot/src/Core/Sim/ScenarioApplier.cs:60,125` (Validated-in, unwrapped-to-LoadScenario), `godot/ProjectChimera.Sim.Tests/Validation/{NegativeValidationTests,ValidatedMintingTests}.cs` (patterns to mirror).
- Source — map gen: `godot/src/CreationSuite/MapGeneratorPanel.cs:16,201` (Node; LLM call), `godot/src/AI/LLMService.cs:491–589` (ValidateScenario 7-pass), `godot/src/Core/Definitions/ScenarioSerializer.cs:23–29,47,59–80` (Serialize/ComputeFileHash), `godot/ProjectChimera.Sim.Tests/Server/CanonicalScenarioTests.cs:42–50` (run-twice-SequenceEqual pattern), `godot/SimSources.props:30` (src/AI/** in Tier-1), `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj:27–39` (embedded-golden pattern).
- Prior stories: `_bmad-output/implementation-artifacts/1-9a-…md` (loopback/quorum; "do NOT build server-dictated delay"), `1-9b-…md` (LAN gate parked on 1 machine), `1-10b-…md` (analyzer advisory CHM0001 for float), `1-10c-…md` (AI float→Fixed cross-platform boundary; WSL gate set).
- Memory: `[[chimera-mp-disconnect-ai-takeover-reconnect]]` (AI float→Fixed is a HARD dep for AI-in-lockstep), `[[banned-api-aot-analyzer-tooling]]` (CHM0001 advisory), `[[project-chimera-gds-planning-chain]]`, `[[linux-env-for-crossplatform-check]]`.

### STATUS.md reconciliation (read this)
The investigation found STATUS.md stale/imprecise on three of the four systems. Update factually (Task 6):
- **§6 "Adaptive delay (dynamic N) | 📋 | … Deferred until online testing"** — **STALE.** Adaptive delay IS built in `LockstepManager` (RTT ping/pong, `[2,12]` clamp, `DelayProposal` agreement, `CommitDelayChange`). Reword to: built (as-built P2P); smoke-tested in 1.11; server-dictated form is Story 9.4; receipt re-clamp pending (9.4).
- **§9 "Utility AI decision system | 📋 | Upgrade from rule-based skeleton"** — clarify: the as-built `AiOpponentSystem` already *is* utility-scored (6 scored actions + max-pick), and FR-45's "Utility AI" = that as-built system, smoke-tested in 1.11 (same-machine). A deeper utility-AI upgrade can remain a future 📋 item, but the row should not imply the utility AI is absent.
- **"Natural language trigger scripting (LLM) | 📋"** and **"AI-assisted map generation | 📋"** — built-with-caveats, not untouched: LLM generation works; 1.11 adds the validated-only trigger gate (the as-built path was unvalidated) and documents the map generator's no-RNG/no-SimRng reality. Reword to reflect "generation built; determinism/validation hardened/clarified in 1.11."

---

## Decisions for Alec (answer before or during dev)

> Saved per the workflow's "save questions for the end." Neither blocks starting Tasks 1–2 (Utility AI + Adaptive Delay are unambiguous). Decision #1 gates Task 3; Decision #2 gates Task 4.

1. **LLM Trigger (AC3) — extend the validator, and how far to close the bypass?** The validated-only guarantee is currently UNENFORCED (triggers bypass `ScenarioValidator`).
   - **Recommended: extend the gate + assert (default).** Add a `Triggers[]` validation pass to `ScenarioValidator` (bounded — one more located-error loop following the exact existing pattern) and assert valid-passes / malformed-rejected / sole-mint. This makes the AC *true* and is smoke-sized. Leave fully routing the editor-accept path (`TriggerEditorPanel.OnAcceptPressed`) and the file-load path through the gate as a small documented follow-up (those are Godot/editor-side and more invasive).
   - **Alternative: document-only (red finding).** Write the smoke test as a failing/red guard that records the gap and defers the validator extension to a later story. Faster, but leaves the AC unmet and the guarantee unenforced.
   - *My lean: extend + assert (default). Route the editor/file paths in a small follow-up unless you want it in-scope now (add ~½ task).*

2. **AI Map Generator (AC4) — Option A (smoke-test the deterministic core) or Option B (build a procedural generator)?** The as-built generator is LLM-only with no RNG, so "fixed seed via SimRng" has nothing to seed.
   - **Recommended: Option A (default).** Prove `ValidateScenario` + `ScenarioSerializer` are byte-identical across two runs against a canned golden JSON, and document that the LLM step is out of the determinism boundary and "via SimRng" is N/A. Smoke-sized (one test + one golden + a doc note); honest about the boundary.
   - **Alternative: Option B.** Build a real *procedural*, `SimRng`-seeded map generator (integer/`Fixed` placement of bases/nodes) that genuinely satisfies the AC's literal text (and would even be MP-safe). This is **new feature work, not a smoke test**, and partly contradicts the "AI/LLM" identity of the current panel — recommend it be a separate story if you want procedural maps.
   - *My lean: Option A now; file Option B as its own feature story if procedural generation is actually wanted.*

3. **Story shape — land the four blocks incrementally or as one review?** The four task blocks are independent (no cross-dependencies). You could land+review them one at a time (Utility AI → Adaptive Delay → LLM Trigger → Map Gen) to keep each review tight, or do one combined review.
   - *My lean: incremental in that order — Tasks 1–2 are unblocked today; Tasks 3–4 wait on Decisions #1/#2.*

4. **Adaptive delay (AC2b) — loopback now, LAN later?** The single-machine loopback delay-change run is achievable today; the two-machine LAN variant is parked on 1 machine (same as 1.9b AC4).
   - *My lean: loopback now (the recorded proof); add the LAN run to the same parked runbook when a 2nd box exists.*

## Dev Agent Record

### Agent Model Used

_(dev agent fills in)_

### Debug Log References

### Completion Notes List

### File List

### Change Log
