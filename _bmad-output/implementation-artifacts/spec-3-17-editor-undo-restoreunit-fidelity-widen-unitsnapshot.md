---
title: 'Editor undo/RestoreUnit fidelity — widen UnitSnapshot to full authored state'
type: 'bugfix'
created: '2026-07-08'
baseline_revision: 'ee1e24c46f45f61bac77917c55742c463f713a51'
final_revision: '972367e6aac80af3053784885b5d67fea04f978b'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-3-context.md'
  - '{project-root}/godot/src/Core/EntityWorld.cs'
  - '{project-root}/godot/src/UI/EntityPlacer.cs'
  - '{project-root}/godot/ProjectChimera.Sim.Tests/Core/ApplyUnitDefinitionGuardTest.cs'
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** `EntityPlacer`'s placed-unit delete→Ctrl+Z restore rebuilds a unit from a `UnitSnapshot` that carries only ~15 of the ~30 authored per-unit SoA fields, so a restored unit silently reverts armor, passives (aura/on-hit/self), abilities+Energy, feedback profile, tags, attack domain, delivery/projectile speed, collision radius, separation priority, category, and XP bounty to their `Create()` defaults. Six stories deferred this exact "RestoreUnit drops authored state" debt (deferred-work §1.13/§2.2a/§2.4a/§2.6/§2.9a/§3.12).

**Approach:** Route restore through the single def→SoA mapper `EntityWorld.ApplyUnitDefinition` (the A2 rule) so every def-derived field is re-derived from the unit's `UnitDefinition`; snapshot only the non-def residue. Give the sim the def per-entity via a new **non-folded** `SourceDefinition` reference SoA (the `FeedbackProfile` precedent — set in `ApplyUnitDefinition`, null-reset in `Create`), move the snapshot capture+restore into the Godot-free Core layer, and add a Tier-1 round-trip fidelity guard test mirroring `ApplyUnitDefinitionGuardTest`.

## Boundaries & Constraints

**Always:**
- Restore for a def-based unit re-derives all authored fields via `ApplyUnitDefinition(id, def)` — never a hand-copy of def-derived fields (A2).
- Snapshot residue = the def reference + Create-arg fields (Position, Faction, MaxHealth, Speed) + caller-owned fields the mapper does not write (MeshType, GatherState, CarryCapacity, SupplyCost) + the existing raw combat stats used only by the def-less fallback.
- After `ApplyUnitDefinition`, replay the caller-owned residue verbatim so worker overrides (SupplyCost=0, GatherState, CarryCapacity) and MeshType survive — identical to the `DoSpawnWorker`/`DoSpawnCombatUnit` place path.
- `SourceDefinition` is excluded from `SimChecksum` / `CanonicalModelHash` (like `FeedbackProfile`) and null-reset in `Create()` so a recycled slot never carries a prior occupant's def (the SoA-recycle trap).
- Snapshot capture + restore must be reachable from a Godot-free Tier-1 xUnit test (no `using Godot;` in the capture/restore path).
- Determinism untouched: no new folded field, no sim-tick behavior change; the 17 golden checksums replay byte-identical.

**Block If:**
- Investigation of the live code shows the editor place path (`EntityPlacer`/`ScenarioApplier` editor branch) actually mints a `HeroStore` row / sets `HeroIndex` at placement (contradicting the planning investigation) — then hero-link restore semantics are undefined and this needs a human decision.

**Never:**
- Never fold `SourceDefinition` into `SimChecksum`/`CanonicalModelHash` or re-baseline the goldens.
- Never mint a hero or write `HeroIndex` on restore — the editor place path does not, so faithful restore leaves `HERO_NONE`.
- Never change tick-loop behavior, the def-less spawn fallback's stat set, or any unrelated system.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Def-based combat unit round-trip | Placed unit with non-default def (armor, passives, abilities, tags, domain, delivery, collision, separation, category, feedback, xp) deleted then undone | Restored unit byte-equal (`.Raw` on Fixed) on every authored SoA field to pre-delete; passive re-installed via `OnUnitDefinitionApplied` | No error |
| Worker round-trip | Worker (SupplyCost=0, GatherState, CarryCapacity overridden post-mapper) deleted then undone | Worker overrides + MeshType preserved exactly | No error |
| Def-less fallback unit | Unit placed via the def-less branch (`SourceDefinition[id]==null`) deleted then undone | Restored from the snapshot's raw stats (today's behavior), no regression | No error |
| Recycled slot | Slot destroyed then re-`Create()`d with no def applied | `SourceDefinition[reused]==null` (no prior def leaks) | Recycle-trap guarded |
| World full on restore | `EntityWorld` at capacity when undo fires | `Create` returns `-1`; restore returns `-1`, logs, no partial state | Graceful; matches current |

</intent-contract>

## Code Map

- `godot/src/Core/EntityWorld.cs` -- add `SourceDefinition` SoA (declare, null-reset in `Create`, assign in `ApplyUnitDefinition`); host the Godot-free `SnapshotUnit`/`RestoreUnit` capability.
- `godot/src/Core/UnitSnapshot.cs` (new, or a Core struct near EntityWorld) -- widened Godot-free snapshot: def ref + residue + legacy raw stats.
- `godot/src/UI/EntityPlacer.cs` -- `DeleteUnit` captures via `_world.SnapshotUnit(id)`; the undo closure restores via `_world.RestoreUnit(snap)`; retire the private `UnitSnapshot`/`RestoreUnit` (delegate to Core). Redo path (destroy boxed id) unchanged.
- `godot/ProjectChimera.Sim.Tests/Core/ApplyUnitDefinitionGuardTest.cs` -- add the round-trip fidelity guard + `SourceDefinition` recycle-trap, reusing the existing hostile `CombatDef()`/registry fixtures.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/EntityWorld.cs` -- Declare `public readonly UnitDefinition[] SourceDefinition;`, allocate it, null-reset it in `Create()` (alongside `FeedbackProfile`), and assign `SourceDefinition[id] = def;` in `ApplyUnitDefinition`. Add Godot-free `UnitSnapshot SnapshotUnit(int id)` (reads `SourceDefinition[id]` + residue) and `int RestoreUnit(in UnitSnapshot snap)` (`Create` → if `snap.Def != null` `ApplyUnitDefinition(id, snap.Def)` else hand-set raw stats → replay residue: MeshType/GatherState/CarryCapacity/SupplyCost → return id / `-1` if full). -- Route restore through the single mapper; capture only the residue.
- `godot/src/Core/UnitSnapshot.cs` -- Widen the snapshot to `{ UnitDefinition? Def; FixedVec3 Position; Faction Faction; Fixed MaxHealth; Fixed Speed; byte MeshType; GatherState GatherState; Fixed CarryCapacity; byte SupplyCost; + AttackRange/AttackDamage/AttackSpeed/DamageType/ArmorType/VisionRange/SplashRadius (read only in the def-less branch) }`. -- The full authored surface, def-derived half by reference.
- `godot/src/UI/EntityPlacer.cs` -- `DeleteUnit` builds the snapshot via `_world.SnapshotUnit(id)`; undo closure sets `box[0] = _world.RestoreUnit(snap)`; remove the stale carve-off comments (1171-1192) now that the gap is closed. -- Presentation keeps history wiring; data logic lives in Core.
- `godot/ProjectChimera.Sim.Tests/Core/ApplyUnitDefinitionGuardTest.cs` -- Add `SnapshotRestore_ReproducesEveryAuthoredField_OffCreateDefault`: `Create`+`ApplyUnitDefinition(CombatDef-with-abilities/passives)`, set the caller-owned residue off-default, `SnapshotUnit`, `Destroy`, `RestoreUnit`, then `Assert.Equal(original, restored)` for every authored field **plus** `Assert.NotEqual(CreateDefault, restored)` teeth so a dropped field goes RED. Add `RecycledSlot_CarriesNoPriorSourceDefinition`. -- The Tier-1 guard the AC requires.

**Acceptance Criteria:**
- Given a placed def-based unit with non-default authored state, when it is deleted and restored via undo, then every authored SoA field (armor, passive indices, abilities/Energy, feedback, tags, attack domain, delivery/projectile speed, collision radius, separation priority, category, XP bounty, base/effective stats) is byte-identical to the pre-delete state.
- Given a worker (post-mapper overrides) or a def-less-placed unit, when deleted and restored, then worker overrides / def-less raw stats are preserved with no regression.
- Given the widened snapshot, when a per-unit field that would be dropped on restore is left uncovered, then the Tier-1 round-trip guard goes RED (teeth on each authored field); def-derived fields are structurally covered because restore routes through the already-guarded `ApplyUnitDefinition`.
- Given this is editor-only state flow, when the change lands, then `SimChecksum` is unchanged, no field is folded, and the 17 goldens replay byte-identical.

## Spec Change Log

_No spec amendments — review produced no intent_gap or bad_spec loopback._

## Review Triage Log

### 2026-07-08 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 6: (high 0, medium 2, low 4)
- defer: 4
- reject: 5
- addressed_findings:
  - `[medium]` `[patch]` `SnapshotUnit` captured `Effective*` health/speed and fed them to `Create` (→ Base), so a def-based unit's re-installed while-alive self-passive would double-count into Base — changed capture to `BaseMaxHealth`/`BaseMoveSpeed` (correct authored base; Base==Effective for the def-less/no-modifier case).
  - `[medium]` `[patch]` The story's headline behavior (self-passive re-installed on editor undo) was exercised by no test — the install seam is inert under a bare `EntityWorld`. Added `PassiveRuntimeTests.SelfPassive_ReInstalledExactlyOnce_AfterDeleteUndoRestore` (wired `SimulationHost`; restored unit regenerates at exactly a fresh unit's rate → re-installed once, no double, no drop).
  - `[low]` `[patch]` `SourceDefinition` XML doc said "Written ONLY in `ApplyUnitDefinition`" but it is also null-reset in `Create` and bulk-cleared in `Clear` — corrected to "only VALUE-writing site".
  - `[low]` `[patch]` The moved `RestoreUnit` dropped the world-full `GD.PrintErr` (Core cannot log) — restored the user-facing log in the `EntityPlacer` undo closure on a `-1` return.
  - `[low]` `[patch]` The residue half (`MeshType`/`GatherState`/`CarryCapacity`/`SupplyCost`) is still hand-copied, so a NEW caller-owned field would silently drop on undo without the enumerated round-trip guard catching it — added a written coverage rule to `godot/CLAUDE.md` (mirrors the single-mapper rule).
  - `[low]` `[patch]` The def-less round-trip test asserted attack/vision/splash but not restored health — added `BaseMaxHealth`/`EffectiveMaxHealth`/`BaseMoveSpeed` assertions so a future `Create` ctor-arg change can't silently regress def-less restore.

## Design Notes

**Why a per-entity `SourceDefinition` and not EntityPlacer-side def tracking:** `DeleteUnit` deletes units found by `FindNearestUnit`, including scenario-loaded units EntityPlacer never placed, so a presentation-side `Dictionary<int,def>` would miss them and is not recycle-safe. The single mapper `ApplyUnitDefinition` is the one channel every def-based spawn path already uses, so storing the def there captures all of them uniformly and rides the existing `Create`-reset recycle discipline. `FeedbackProfile` already established a non-folded reference SoA — same pattern.

**Why routing beats widening the hand-copy:** re-deriving from the def means any *future* def-derived SoA field is auto-restored with zero snapshot/restore edits (it flows through the mapper the `ApplyUnitDefinitionGuardTest` already guards), permanently closing the recurring drop-debt. This also fixes the standing Base/Effective asymmetry (the old capture read `Effective*` and wrote it into `Base*`): the mapper writes authored `Base*` from the def and mirrors `Effective=Base`.

**Passives on restore:** `ApplyUnitDefinition` fires `OnUnitDefinitionApplied` → `AbilityCastSystem.InstallSelfPassive` (wired in `SimulationHost`), re-applying the while-alive self-passive modifier; aura/on-hit are pure SoA indices read live. `Destroy` already clears the entity's modifiers (`ModifierStore.ClearEntity` on the `OnDestroy` seam), so there is no double-install. The Tier-1 test runs without a `SimulationHost`, so `OnUnitDefinitionApplied` has no subscriber — it asserts the SoA indices (which the mapper sets directly), exactly as the existing guard does.

**Hero fields:** the editor place path does not mint a hero row or set `HeroIndex` (minting is init-time only, in `ScenarioApplier`/`HeroProfileLoader`). Faithful restore therefore reproduces `HERO_NONE`; hero *def* fields (curves/ultimate) are def-derived and return via the mapper. No hero-link residue is captured (see Block If).

## Verification

**Commands:**
- `dotnet build godot/godot.sln -c Debug` -- expected: builds clean (EntityPlacer + Core compile with the moved snapshot/restore).
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release` -- expected: all Tier-1 tests green, including the new round-trip fidelity + `SourceDefinition` recycle-trap tests and the 17 goldens byte-identical (no re-baseline).
- `git diff --stat godot/src/Core/SimChecksum.cs godot/src/**/CanonicalModelHash.cs` -- expected: empty (no checksum/hash change).

**Manual checks:**
- In-editor (godot-mcp): place a Ranged unit + a worker, delete each, Ctrl+Z, confirm via `godot_runtime_state` that restored units carry authored category/collision/separation/armor/tags (not Melee/1.0/Normal/0/None defaults).

## Auto Run Result

Status: done

**Summary:** Widened the editor delete→undo restore so a placed unit returns field-identical to its authored state. Restore now re-derives every def-derived field by routing the unit's `UnitDefinition` back through the single `ApplyUnitDefinition` mapper (the A2 rule), permanently closing the "RestoreUnit drops authored state" debt tracked across six stories (§1.13/§2.2a/§2.4a/§2.6/§2.9a/§3.12). The def is held per-entity in a new non-folded `SourceDefinition` reference SoA (the `FeedbackProfile` precedent); the snapshot carries only the def reference + non-def residue. Snapshot/capture/restore moved into the Godot-free Core layer so a Tier-1 guard exercises the round-trip.

**Files changed:**
- `godot/src/Core/EntityWorld.cs` — added non-folded `SourceDefinition` SoA (declared, null-reset in `Create`, cleared in `Clear`, set in `ApplyUnitDefinition`); added Godot-free `SnapshotUnit`/`RestoreUnit`; capture authored `Base*` health/speed; corrected the `SourceDefinition`/`OnUnitDefinitionApplied` doc comments.
- `godot/src/Core/UnitSnapshot.cs` (new) — Godot-free widened snapshot struct: def ref + Create-arg fields + caller-owned residue + legacy raw stats for the def-less branch.
- `godot/src/UI/EntityPlacer.cs` — removed the private `UnitSnapshot`/`RestoreUnit`; `DeleteUnit` captures via `_world.SnapshotUnit`; undo closure restores via `_world.RestoreUnit` and logs on a world-full `-1`.
- `godot/ProjectChimera.Sim.Tests/Core/ApplyUnitDefinitionGuardTest.cs` — round-trip fidelity guard (byte-equality + Create-default teeth), `SourceDefinition` recycle-trap, def-less-fallback restore (with health teeth), and world-full `-1` tests.
- `godot/ProjectChimera.Sim.Tests/Effects/PassiveRuntimeTests.cs` — wired-`SimulationHost` test proving the self-passive is re-installed exactly once on restore.
- `godot/CLAUDE.md` — written coverage rule: new caller-owned residue fields must be added to the snapshot/restore trio.

**Review findings:** 6 patches applied (2 medium: Base/Effective capture correctness, passive-install test; 4 low: doc drift, world-full log, residue written rule, def-less health teeth). 4 deferred (hero-link fidelity on editor undo + orphaned `HeroStore` row on hero delete; current-HP/Energy preservation decision; def-pinned-by-reference across long sessions). 5 rejected (test/free-list coupling, pre-existing/spec-scoped def-less `Delivery` drop, worker mid-gather runtime state, `SnapshotUnit` liveness [flow snapshots before Destroy], world-full "no partial state" teeth). No intent_gap or bad_spec loopback — reviewers judged the design sound; the chartered def-derived debt is fully closed.

**Verification:** `dotnet build godot/godot.sln -c Debug` → 0 errors. `dotnet test ProjectChimera.Sim.Tests -c Release` → 1104 passed, 1 skipped, 1 failed; the single failure (`ProceduralMapGeneratorTests.SameSeed…`) fails identically on the pristine baseline `ee1e24c` (isolated worktree run — a pre-existing platform golden mismatch, not this change). 121 golden tests byte-identical; `git diff` of `SimChecksum.cs`/`CanonicalModelHash.cs` empty (no fold, no re-baseline). The in-editor godot-mcp manual check was not run (needs a live editor session); the Tier-1 round-trip + wired-host passive test cover the fidelity structurally.

**Residual risks:** Hero-linked units deleted+undone in the editor lose their hero link and orphan a `HeroStore` row (deferred — design-laden, outside the def-derived debt). Def-less restore preserves today's behavior (does not re-infer `Delivery` from range). Current HP/Energy restore to full on undo (matches prior behavior).

**Note (session):** An accidental no-arg `git stash pop` during verification popped a pre-existing unrelated stash (`bmad-loop-3-5: line-ending noise`) into the tree; it was cleanly reverted (tracked noise files restored to HEAD, the stash left intact in the stash list) with no impact on this change.
