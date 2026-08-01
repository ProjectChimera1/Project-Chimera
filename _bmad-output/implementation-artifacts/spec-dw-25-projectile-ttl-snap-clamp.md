---
title: 'DW-25: projectile snap-to-goal clamp + max-lifetime TTL (no more orbiting shells / pool leak)'
type: 'bugfix'
created: '2026-08-01'
status: 'done'
baseline_revision: 'abf3bda0f3beedf867d9dd5aceb3950f9738e57b'
final_revision: '72d36bf2f7798fcd847ed09478aea18e4a4d4a19'
review_loop_iteration: 0
followup_review_recommended: false
context: ['{project-root}/godot/CLAUDE.md']
warnings: []
---

<intent-contract>

## Intent

**Problem:** `ProjectileSystem.Tick` checks the hit radius (0.5 u) *before* advancing, then advances by the full `dir * Speed * dt` with no clamp to the remaining distance and no lifetime cap. At the old hardcoded speed 18 (0.6 u/tick) max overshoot was 0.1 < 0.5 so shots always converged, but Story 3.12 made `projectile_speed` authorable up to the validator's 32768 ceiling — any speed whose per-tick step exceeds the hit radius on final approach overshoots every tick, orbits its target forever, and permanently leaks its `MAX_PROJECTILES` slot until the pool fills and the unit stops firing.

**Approach:** Add two independent safeguards to the advance step: (1) a **snap-to-goal clamp** — when this tick's step would reach or pass the goal, land exactly on the goal instead of stepping past it, so a high-speed shell converges on a stationary/slow target next tick; and (2) a **max-lifetime TTL** — age each shell by `dt` and drop it harmlessly after `MAX_LIFETIME` seconds, the backstop for a target fleeing faster than the shell can travel (which the clamp alone cannot fix). Golden re-baseline is expected only if the change actually moves the committed `DeliveryScenario` sequence — verify empirically and re-record only if so.

## Boundaries & Constraints

**Always:**
- All new sim math is `Fixed` (deterministic), never `float`. Process/age shells in the existing ascending-slot order.
- The **non-overshoot advance path must stay byte-identical** to today's expression: keep the else branch exactly `_store.Position[i] + dir * _store.Speed[i] * dt` (same operand order — Fixed multiply is not associative, so re-ordering would silently move goldens). Only genuine overshoot ticks and TTL expiries may change behavior.
- A recycled projectile slot must never inherit the prior occupant's age: initialize the new `Age` field to `Fixed.Zero` in `Spawn`, and clear it in `Clear`.
- `MAX_LIFETIME` is a single named `Fixed` constant on `ProjectileSystem`, documented like `PROJECTILE_SPEED`/`HIT_SQR`.
- TTL expiry drops the shell harmlessly (no hit resolved), identical to the existing "target died in flight" drop.

**Block If:**
- The `DeliveryScenario` golden moves in a way you cannot explain as a direct consequence of the clamp/TTL (i.e. a shell in that scenario was silently overshooting or aging out) — that would mean the "byte-identical non-overshoot path" invariant was violated; HALT rather than blindly re-record.

**Never:**
- Do NOT change projectile visuals, the `ProjectileBridge` presentation layer, splash resolution, building-target packing, or kill attribution.
- Do NOT add `Age` to the `SimChecksum` fold (`ProjectileStore` is never a checksum input) nor to the save/load lanes (`PA` enum) — persisting it would churn the save format; a reload resetting the TTL clock is a harmless backstop reset, consistent with the presentation-only `Feedback` field which is also unpersisted.
- Do NOT touch the `projectile_speed` validator ceiling or introduce a speed cap — the TTL is the chosen backstop, not a range change.
- Do NOT edit the deferred-work ledger (the orchestrator records resolution).

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Slow shell, non-overshoot tick | speed 6, dt 1/30, target 100 u away | Advances by exactly the old `dir*Speed*dt` (~0.2 u); position byte-identical to pre-change | No error expected |
| High-speed final approach (the bug) | speed 5000, target 4 u away, step (~166 u) ≥ dist | Snaps exactly onto the goal this tick; next tick `distSqr ≤ HIT_SQR` → hit resolves → shell destroyed (slot freed) | No error expected |
| Target fleeing faster than shell | target moves away every tick so `distSqr` never ≤ `HIT_SQR` | Shell ages by `dt`; once `Age ≥ MAX_LIFETIME` it is destroyed with no hit; pool slot freed | Drop harmlessly (no damage) |
| Recycled slot | slot freed then re-`Spawn`ed | New shell's `Age` starts at `Fixed.Zero`, not the prior occupant's accumulated age | No error expected |

</intent-contract>

## Code Map

- `godot/src/Combat/ProjectileSystem.cs` -- `Tick` advance step (lines ~99-117): add TTL age/expiry check and the snap-to-goal clamp; add the `MAX_LIFETIME` constant.
- `godot/src/Combat/ProjectileStore.cs` -- add the `Age` SoA array; initialize in `Spawn`, wipe in `Clear`. NOT added to persistence/fold.
- `godot/ProjectChimera.Sim.Tests/Combat/DeliveryCombatTests.cs` -- home for the new direct behavioral tests (same style/helpers as the existing per-unit-speed test).
- `godot/ProjectChimera.Sim.Tests/Golden/DeliveryGoldenTests.cs` / `DeliveryScenario.cs` -- the cross-platform golden to re-verify (re-record only if it moves; recording via `CHIMERA`-style `RecordEnvVar`).
- `godot/src/Core/Persistence/SaveGameState.cs` -- referenced only to confirm `Age` is deliberately excluded from the `PA` lanes (no edit).

## Tasks & Acceptance

**Execution:**
- `godot/src/Combat/ProjectileStore.cs` -- add `public readonly Fixed[] Age = new Fixed[MAX_PROJECTILES];` with an XML doc noting it is the TTL accumulator (seconds in flight), NOT folded and NOT persisted (like `Feedback`); set `Age[id] = Fixed.Zero;` in `Spawn` (recycled slot never inherits); add `System.Array.Clear(Age);` to `Clear`.
- `godot/src/Combat/ProjectileSystem.cs` -- add `public static readonly Fixed MAX_LIFETIME = Fixed.FromInt(10);` (seconds). In `Tick`, after the `distSqr <= HIT_SQR` hit block and before the advance: `_store.Age[i] += dt; if (_store.Age[i] >= MAX_LIFETIME) { _store.Destroy(i); continue; }`. Replace the advance with the snap clamp: compute `Fixed dist = delta.Magnitude();`, `FixedVec3 dir = delta / dist;`, `Fixed step = _store.Speed[i] * dt;`, then `if (step >= dist) _store.Position[i] = goalPos; else _store.Position[i] = _store.Position[i] + dir * _store.Speed[i] * dt;` (else branch UNCHANGED from today).
- `godot/ProjectChimera.Sim.Tests/Combat/DeliveryCombatTests.cs` -- add tests covering the I/O matrix: (a) a high-speed shell snaps onto the goal and is destroyed within a couple ticks (no orbit, `HighWaterMark`/`Alive` frees the slot); (b) a shell whose goal keeps fleeing beyond reach is destroyed by TTL — assert it is alive before `MAX_LIFETIME` and gone at/after it; (c) a non-overshoot advance still lands at the same position as before (guard the byte-identical path).
- `godot/ProjectChimera.Sim.Tests/Golden/DeliveryGoldenTests.cs` -- run it; if `MatchesCommittedGolden` fails, re-record the baseline per its `WhatToDo` (set `RecordEnvVar=1`, `dotnet test --filter FullyQualifiedName~DeliveryGolden`, `dotnet build`, commit) and record it moved; if it passes, note the golden was unaffected (no re-baseline).

**Acceptance Criteria:**
- Given a projectile with a very high authored speed on final approach to a stationary target, when `Tick` runs, then the shell lands exactly on the goal (never past it) and is destroyed within the next tick, freeing its pool slot — it never orbits.
- Given a target that moves out of reach every tick so the shell can never enter the hit radius, when `MAX_LIFETIME` seconds have elapsed, then the shell is destroyed with no damage dealt and its slot returned to the pool.
- Given a slow shell on a normal (non-overshoot) tick, when `Tick` runs, then its resulting `Position` equals the pre-change advance byte-for-byte (the `DeliveryScenario` golden's non-overshoot behavior is preserved).
- Given a freshly `Spawn`ed shell in a recycled slot, when it is first ticked, then its `Age` began at `Fixed.Zero` (no inherited age).
- Given the full sim test suite, when run, then it is green (the `DeliveryScenario` golden either matches or has been re-recorded because it genuinely moved).

## Review Triage Log

### 2026-08-01 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 1: (high 0, medium 1, low 0)
- defer: 1: (high 0, medium 1, low 0)
- reject: 9
- addressed_findings:
  - `[medium]` `[patch]` Golden coverage gap for the snap branch (blind-hunter + verification-gap + intent-alignment, deduped): the DW-25 snap clamp changes the folded impact `Position` — and, via `ApplySplash`, the folded splash center — on any overshoot tick, but no checksum/cross-platform golden exercises the snap branch (`DeliveryScenario` uses speeds 6/18 that never overshoot, so it stayed byte-identical; the intent explicitly expected golden coverage). Added a new cross-platform golden scenario + test driving a high-speed single-target projectile (overshoots → snap) and a high-speed splash projectile over a tight Neutral cluster (pins the snapped splash center), and recorded its baseline.

## Design Notes

Two safeguards because they cover disjoint failure modes:
- **Snap clamp** fixes overshoot on a *reachable* goal (stationary or slower-than-shell target): once `step ≥ remaining dist`, landing on the goal guarantees `distSqr == 0 ≤ HIT_SQR` next tick.
- **TTL** is the only backstop for an *unreachable* goal (target fleeing faster than `Speed*dt`), where the clamp can't help because the goal keeps receding — without it such a shell orbits forever and leaks its slot.

Determinism note (why the else branch is copied verbatim): Fixed-point multiply rounds, so `(delta/dist)*(Speed*dt)` is not guaranteed equal to `((delta/dist)*Speed)*dt`. Keeping the original `dir * _store.Speed[i] * dt` expression on the non-overshoot branch means every non-overshoot tick is bit-for-bit unchanged, so the `DeliveryScenario` golden (which exercises only speeds 6 and 18 — neither overshoots, none live near 10 s) should stay byte-identical. `MAX_LIFETIME = 10 s` is a deliberately generous upper bound: real shots land within a couple seconds (small attack ranges), so a legit shot is never dropped, while a non-converging shell is bounded to 10 s instead of forever.

## Verification

**Commands:**
- `dotnet build godot/godot.sln` -- expected: builds clean (no new warnings; sim code stays Godot-free / float-free).
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj --filter "FullyQualifiedName~Delivery"` -- expected: the new behavioral tests pass; `DeliveryGolden` either matches or is re-recorded because it genuinely moved.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: full sim suite green (allow the known CanonicalModelHashPerf timing flake on a lone full-suite fail — re-run/isolate to confirm, never a code regression).

## Auto Run Result

Status: done

### Summary
Closed DW-25: `ProjectileSystem.Tick` gained a **snap-to-goal clamp** (when this tick's step would reach or pass the goal, land exactly on it instead of overshooting) and a **max-lifetime TTL** (`MAX_LIFETIME = 10 s`; a shell that never enters the hit radius is dropped harmlessly after 10 s). Together they stop a high authored `projectile_speed` from overshooting the 0.5 u hit radius every tick, orbiting its target forever, and permanently leaking its `MAX_PROJECTILES` pool slot. The non-overshoot advance path is kept byte-identical to the pre-change expression, so no existing golden moved.

### Files changed
- `godot/src/Combat/ProjectileSystem.cs` — added `MAX_LIFETIME`; TTL age/expiry after the hit block; snap-to-goal clamp on the advance (else branch verbatim).
- `godot/src/Combat/ProjectileStore.cs` — added the `Age` SoA accumulator (reset to `Fixed.Zero` at `Spawn`, cleared in `Clear`); not folded, not persisted (like `Feedback`).
- `godot/ProjectChimera.Sim.Tests/Combat/DeliveryCombatTests.cs` — 4 behavioral tests: high-speed snap-then-destroy (no orbit), unreachable-target TTL drop (harmless, slot freed), recycled-slot zero-age, non-overshoot byte-identity.
- `godot/ProjectChimera.Sim.Tests/Golden/ProjectileSnapScenario.cs` *(new, review patch)* — cross-platform golden scenario driving the snap branch (high-speed single-target + high-speed splash over a Neutral cluster).
- `godot/ProjectChimera.Sim.Tests/Golden/ProjectileSnapGoldenTests.cs` *(new, review patch)* — golden test clone (byte-identical / matches-committed / evolves / record).
- `godot/ProjectChimera.Sim.Tests/Golden/projectile-snap-scenario.golden.txt` *(new, review patch)* — recorded 300-sample baseline (checksum algo v22).
- `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` *(review patch)* — explicit `EmbeddedResource` entry for the new golden (the project lists goldens individually; it does not glob).

### Review findings breakdown
- **Patches applied (1, medium):** golden coverage gap — the snap branch changes the folded impact `Position` and (via `ApplySplash`) the splash center, but no checksum/cross-platform golden exercised it (`DeliveryScenario` speeds 6/18 never overshoot). Added a new cross-platform golden that drives the snap branch and pins the snapped splash center on both CI legs.
- **Deferred (1, medium):** see the canonical entry below — **not written to the ledger** because this run was instructed not to edit it; the orchestrator should record it.
- **Rejected (9):** TTL drops legit slow shells (by-design, spec-documented; no real unit needs >10 s flight); fold `Age`/docstring rationale (docstring correctly cites the store-wide checksum exclusion; `Age` is transitively checksummed via damage timing, same policy as `Speed`/`SourceId`); `step==dist` boundary (measure-zero; both paths hit next tick); 10 s nominal-vs-exact under 1/30 accumulation (deterministic; doc says "generous bound"); reload resets `Age` (contrived, spec accepted it); snap doesn't converge a faster-fleeing target (strictly better than the orbit it replaces; such a target *should* miss and be TTL-reaped); no terminal event / bridge freeze (**verified false** — `ProjectileBridge._Process` polls `Alive[]` and hides `!Alive` slots each frame, exactly like the pre-existing dead-target drop); guard `MAX_LIFETIME > 0` (code constant, not user data); TTL preempts arrival on the exact expiry tick (single-tick edge at 10 s, harmless).

### Deferred finding for the orchestrator to record
```markdown
### DW-<next>: ProjectileSystem distance math (SqrMagnitude/Magnitude) silently overflows 16.16 Fixed past ~180 u, corrupting the hit check and the new snap predicate
origin: deferred by review of `_bmad-output/implementation-artifacts/spec-dw-25-projectile-ttl-snap-clamp.md`, 2026-08-01
source_spec: `_bmad-output/implementation-artifacts/spec-dw-25-projectile-ttl-snap-clamp.md`
location: godot/src/Combat/ProjectileSystem.cs:109 (delta.SqrMagnitude() hit check) and :138 (delta.Magnitude() advance)
severity: medium
reason: A projectile whose goal is farther than ~180 world units overflows the 16.16 Fixed range in SqrMagnitude/Magnitude — a wrap-negative distSqr reads as a spurious long-range hit+Destroy, and a wrap-small dist makes the new `step >= dist` predicate snap-teleport the shell across the map. — Evidence: pre-existing (both distance calls predate DW-25; the snap clamp only changes the failure mode), surfaced by the adversarial + edge-case lenses and acknowledged in `DeliveryCombatTests.cs` (targets kept <~180 u to dodge the overflow); reachable by a target fleeing/blinking beyond ~180 u, which the DW-25 TTL's own "receding goal" scenario steers toward. Fix: clamp/guard the goal distance (or cap the addressable playfield) before it feeds the hit check and the snap predicate.
status: open
```

### Verification performed
- `dotnet build godot/godot.sln` — clean, 0 errors (14 pre-existing warnings only; touched sim code stays Godot-free / float-free).
- `dotnet test ... --filter "FullyQualifiedName~Delivery|FullyQualifiedName~ProjectileSnap"` — 35 passed, 0 failed. New `ProjectileSnapGoldenTests` (byte-identical / matches-committed / evolves) all green; `DeliveryGolden.MatchesCommittedGolden` still matches (unmoved).
- `dotnet test ...ProjectChimera.Sim.Tests.csproj` (full sim suite) — 3722 passed, 1 skipped (pre-existing reserved test), 0 failed (+4 vs the pre-change 3718). No existing golden moved; no CanonicalModelHashPerf flake this run.
- Matrix Test Audit: all four I/O rows covered by tests that ran and passed (`NonOvershootAdvance…`, `HighSpeedShell_SnapsOntoGoal…`, `UnreachableTarget_DroppedByTtl…`, `RecycledSlot_StartsWithZeroAge`).

### Residual risks
- `Age` is intentionally unfolded/unpersisted, so a mid-flight save/reload resets a shell's TTL clock — an accepted harmless backstop reset (consistent with the unpersisted `Feedback` field).
- The ~180 u Fixed-overflow cliff in projectile distance math is real but pre-existing and out of DW-25's scope — deferred above for focused attention.
- In-engine gate: **not applicable** (pure `src/Combat/` simulation + tests; no UI/scene/`.tscn`/`.tres` touched). Confirmed by the gate auditor.

### Follow-up review recommendation
`false`. Patched findings this pass = 1 medium, 0 high, 0 low → score `3 × 1 + 1 × 0 = 3` (< 5), no high-severity patch → no follow-up review recommended.
