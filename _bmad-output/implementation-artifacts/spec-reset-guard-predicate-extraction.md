---
title: 'Extract the offline-editor-loop reset guard into a pure static predicate (DW-22)'
type: 'refactor'
created: '2026-07-31'
status: 'done'
baseline_revision: '6959bfb602abe6245a8df03d491289696185fad8'
final_revision: '9a6590550aace354ccb8aaeacd24b4dc2262aaf8'
review_loop_iteration: 0
followup_review_recommended: false
context: ['{project-root}/godot/CLAUDE.md']
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** `WinConditionPhase`'s `ModeChanged` handler decides — inline, from `_ctx.ReplayPlayer == null && !_ctx.Lockstep.IsOnline` — whether an Edit↔Play transition runs the destructive `ResetToAuthoredStart` (which clears the world and re-seeds `DEFAULT_RNG_SEED`). This is the highest-blast-radius decision in the change (a regression that lets it fire during a live match or replay causes lockstep desync / a clobbered replay seed), yet it has zero automated coverage: the whole handler is Godot-coupled and this repo has no Godot integration-test project, so the guard is verified only by inspection.

**Approach:** Extract the `(isOnline, hasReplay, targetMode) → reset action` decision into a pure, Godot-free static predicate living directly under `src/Core/Bootstrap/` (globbed into the Tier-1 sim assembly via `SimSources.props`), make `WinConditionPhase` consume it as the single source of truth for the routing, and add a Tier-1 truth-table test. Behavior must be byte-for-byte identical to today.

## Boundaries & Constraints

**Always:**
- The extracted predicate is pure and Godot-free: no `using Godot;`, no `GameMode` type reference (that enum lives in the Godot-coupled `src/UI/GameState.cs` and is NOT compiled into the sim assembly). Represent `targetMode` as a `bool targetIsPlay`.
- Place the predicate file directly under `godot/src/Core/Bootstrap/` (NOT under `Bootstrap/Phases/`, which `SimSources.props` excludes) so it is picked up by the Godot-free glob and is Tier-1 testable.
- `WinConditionPhase` MUST call the predicate — the inline `offlineEditorLoop` computation is removed, not duplicated. The predicate is load-bearing in production, otherwise the tests guard a dead copy.
- Preserve exact observable behavior of both branches: the Play-branch veto fires iff `offlineEditorLoop && !ResetToAuthoredStart(...)`; the Edit-branch calls `ResetMatchOnReturnToEdit()` iff `!offlineEditorLoop || !ResetToAuthoredStart(...)`. `offlineEditorLoop ⟺ (!isOnline && !hasReplay) ⟺ action == AuthoredStart`.
- The `_suppressReset` re-entrancy guard, its set→revert→clear bracket, and the synchronous-emission comment stay exactly as-is.

**Block If:**
- The predicate cannot be placed in a Godot-free, Tier-1-globbed location without dragging Godot types in (would mean the extraction can't be tested unattended as intended).

**Never:**
- Do not change when or how `ResetToAuthoredStart` / `ResetMatchOnReturnToEdit` are invoked, their arguments, or the veto mechanics.
- Do not touch the deferred-work ledger — the orchestrator records resolution.
- Do not move or re-namespace the `GameMode` enum, and do not add a new autoload/phase or change phase order.
- No new float in sim code; no SimChecksum fold (this predicate is not a sim array).

## I/O & Edge-Case Matrix

`Decide(isOnline, hasReplay, targetIsPlay)` — full truth table (the reset routing for a mode transition):

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Offline editor loop → Play | isOnline=F, hasReplay=F, targetIsPlay=T | `AuthoredStart` | n/a (pure) |
| Offline editor loop → Edit | isOnline=F, hasReplay=F, targetIsPlay=F | `AuthoredStart` | n/a |
| Online match → Play | isOnline=T, hasReplay=F, targetIsPlay=T | `None` (never re-apply mid-online-match) | n/a |
| Online match → Edit | isOnline=T, hasReplay=F, targetIsPlay=F | `Lifecycle` (pre-3.10 lifecycle-only reset) | n/a |
| Replay playback → Play | isOnline=F, hasReplay=T, targetIsPlay=T | `None` (never clobber restored replay seed) | n/a |
| Replay playback → Edit | isOnline=F, hasReplay=T, targetIsPlay=F | `Lifecycle` | n/a |
| Online + replay → Play | isOnline=T, hasReplay=T, targetIsPlay=T | `None` | n/a |
| Online + replay → Edit | isOnline=T, hasReplay=T, targetIsPlay=F | `Lifecycle` | n/a |

Safety invariant surfaced by the table: `AuthoredStart` is returned **only** when `!isOnline && !hasReplay`, regardless of `targetIsPlay`.

</intent-contract>

## Code Map

- `godot/src/Core/Bootstrap/ModeTransitionResetPolicy.cs` -- NEW. The pure predicate + `ModeResetAction` enum. Godot-free; namespace `ProjectChimera.Core.Bootstrap`.
- `godot/src/Core/Bootstrap/Phases/WinConditionPhase.cs` -- MODIFY. `ModeChanged` handler (lines ~252-286): replace the inline `offlineEditorLoop` bool + both branches' conditions with calls to `ModeTransitionResetPolicy.Decide(...)`.
- `godot/SimSources.props` -- REFERENCE ONLY (no edit). `src\Core\Bootstrap\*.cs` is already globbed (only `Bootstrap\Phases\**` is `<Compile Remove>`d), so the new file is auto-included in the Tier-1 + analyzer assemblies.
- `godot/src/UI/GameState.cs` -- REFERENCE. Defines `GameMode { Edit, Play }` and `ModeChanged(int newMode)`; Godot-coupled, not in sim assembly.
- `godot/src/Multiplayer/LockstepManager.cs` -- REFERENCE. `IsOnline` (line 146).
- `godot/ProjectChimera.Sim.Tests/Bootstrap/ModeTransitionResetPolicyTests.cs` -- NEW. Xunit truth-table test mirroring `Bootstrap/PhaseOrderTest.cs` conventions.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Bootstrap/ModeTransitionResetPolicy.cs` -- Create `public enum ModeResetAction { None, AuthoredStart, Lifecycle }` and `public static class ModeTransitionResetPolicy` with `public static ModeResetAction Decide(bool isOnline, bool hasReplay, bool targetIsPlay)`. Body: `bool offlineEditorLoop = !isOnline && !hasReplay; if (offlineEditorLoop) return ModeResetAction.AuthoredStart; return targetIsPlay ? ModeResetAction.None : ModeResetAction.Lifecycle;`. XML-doc the three outcomes and the desync/seed-clobber rationale drawn from the existing handler comment. -- Extract the guard so it is testable without Godot.
- `godot/src/Core/Bootstrap/Phases/WinConditionPhase.cs` -- In the `ModeChanged` lambda, replace `bool offlineEditorLoop = _ctx.ReplayPlayer == null && !_ctx.Lockstep.IsOnline;` with `var resetAction = ModeTransitionResetPolicy.Decide(_ctx.Lockstep.IsOnline, _ctx.ReplayPlayer != null, mode == (int)GameMode.Play);`. Rewrite the Play branch condition to `resetAction == ModeResetAction.AuthoredStart && !_ctx.Scene.ResetToAuthoredStart(_ctx.PersistenceTestMode)` and the Edit branch condition to `resetAction != ModeResetAction.AuthoredStart || !_ctx.Scene.ResetToAuthoredStart(_ctx.PersistenceTestMode)`. Update the surrounding comment to point at the predicate as the source of truth. Leave `_suppressReset` bracket untouched. -- Make the predicate load-bearing while preserving exact behavior.
- `godot/ProjectChimera.Sim.Tests/Bootstrap/ModeTransitionResetPolicyTests.cs` -- Add `[Theory]` + `[InlineData]` covering all 8 rows of the I/O matrix, asserting `Decide(...)` returns the expected `ModeResetAction`. Add one focused `[Fact]` asserting the safety invariant: for every `targetIsPlay`, `AuthoredStart` is returned iff `!isOnline && !hasReplay`. -- Lock the guard's decision under Tier-1 coverage.

**Acceptance Criteria:**
- Given the sim test assembly, when `dotnet test` runs, then `ModeTransitionResetPolicyTests` exercises all 8 input combinations and passes, and no other test regresses.
- Given the predicate is Godot-free and under `src/Core/Bootstrap/`, when `ProjectChimera.Sim.Tests` builds, then `ModeTransitionResetPolicy` compiles into the Godot-free assembly (no `GodotFreeBoundaryTest` violation) without any `SimSources.props` edit.
- Given an online match or an active replay (`isOnline || hasReplay`), when `Decide` is called for either target mode, then it never returns `AuthoredStart` (the destructive reset is gated off exactly as the inline guard did).
- Given `WinConditionPhase` after the edit, when a mode transition occurs, then the veto and lifecycle-reset behavior is identical to before (same conditions, boolean-equivalent).

## Design Notes

Why an enum, not a bare `bool shouldReset`: the guard actually routes three outcomes, and `targetMode` (named in the DW intent) only discriminates the non-offline case — so a boolean would leave `targetMode` a dead parameter:
- `AuthoredStart` — offline editor playtest loop, BOTH directions (clear + re-apply the authored board).
- `None` — online/replay entering Play (sim already live; re-applying desyncs / clobbers the replay seed).
- `Lifecycle` — online/replay returning to Edit (pre-3.10 lifecycle-only reset via `ResetMatchOnReturnToEdit`).

Behavior-equivalence proof for the call-site rewrite (`offlineEditorLoop == (Decide(...) == AuthoredStart)`):
- Play branch: original `offlineEditorLoop && !Reset(...)` ≡ `action == AuthoredStart && !Reset(...)`.
- Edit branch: original `!offlineEditorLoop || !Reset(...)` ≡ `action != AuthoredStart || !Reset(...)`.
Both hold because `Decide` returns `AuthoredStart` exactly when `!isOnline && !hasReplay`, i.e. the old `offlineEditorLoop`.

`hasReplay` maps to `_ctx.ReplayPlayer != null` (the old guard used `ReplayPlayer == null`); `targetIsPlay` maps to `mode == (int)GameMode.Play`.

Predicate skeleton (~12 lines):
```csharp
public enum ModeResetAction { None, AuthoredStart, Lifecycle }

public static class ModeTransitionResetPolicy
{
    public static ModeResetAction Decide(bool isOnline, bool hasReplay, bool targetIsPlay)
    {
        bool offlineEditorLoop = !isOnline && !hasReplay;
        if (offlineEditorLoop) return ModeResetAction.AuthoredStart;   // clear + re-apply authored board, both directions
        return targetIsPlay ? ModeResetAction.None       // online/replay → Play: never re-apply
                            : ModeResetAction.Lifecycle; // online/replay → Edit: lifecycle-only reset
    }
}
```

## Verification

**Commands:**
- `dotnet build godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: build succeeds; the new predicate compiles into the Godot-free assembly.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj --filter "FullyQualifiedName~ModeTransitionResetPolicy"` -- expected: all truth-table cases pass.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: full Tier-1 suite green (no regression; `GodotFreeBoundaryTest` and `PhaseOrderTest` still pass).

**Manual checks:**
- In-engine gate: the edit touches `src/Core/Bootstrap/Phases/WinConditionPhase.cs`, which the project's godot sub-router flags for the in-engine gate. The change is a behavior-neutral substitution (identical predicate value feeding identical branches), so no in-engine behavior changes; the orchestrator's `[verify]` / `/godot-verify` observation remains the gate of record for the Bootstrap touch. Confirm F5 Edit→Play→Edit round-trip in an offline editor session still resets to the authored board, and that no reset fires when a replay is loaded.

### In-Engine Gate - 2026-07-31
- surface: Offline editor playtest loop on `res://scenes/main.tscn` (default scenario `alpha_map_01.json`) — the F5 Edit→Play→Edit round-trip whose reset routing this refactor extracted (`WinConditionPhase.ModeChanged` → `ModeTransitionResetPolicy.Decide` → `MainScene.ResetToAuthoredStart`).
- launched: `godot_editor_edit run` on main.tscn (offline: `Lockstep.IsOnline=false`, no replay → `ReplayPlayer==null`); mode toggled with real F5 key injection via `godot_input`; state read verbatim from the live HUD/resource `Label` text via a `godot_exec` tree walk; drift accumulated with a `godot_game_time` step. Editor error log clean (0 errors) across the whole round-trip.
- digest: authored Edit board captured verbatim `[EDIT]   Tick 0   Hash —` / `P1: 3 units   P2: 2 units   Total: 5` / `P1    200 ore    100 crystal   0/10 supply` `P2    200 ore   0/10 supply` `Nodes: 8   Buildings: 2`. After F5→Play the live sim drifted it to `[PLAY]   Tick 961` / `P1: 3 units   P2: 3 units   Total: 6` / `P1    480 ore ... P2     80 ore   3/20 supply` `Nodes: 8   Buildings: 3`. After F5→Edit it restored verbatim to `[EDIT]   Tick 0   Hash —` / `P1: 3 units   P2: 2 units   Total: 5` / `P1    200 ore    100 crystal   0/10 supply` `P2    200 ore   0/10 supply` `Nodes: 8   Buildings: 2`.
- asserted: authoring source `alpha_map_01.json` declares slot 0 = 3 units + 1 CommandCenter, slot 1 = 2 units + 1 CommandCenter, `start_ore` 200 each, 8 resource_nodes → expected authored board Total 5 (P1 3 / P2 2), Buildings 2, Nodes 8, ore 200/200, Tick 0. Observed authored board matched on every number. After the drift (Total 6, P1 ore 480, P2 ore 80, Buildings 3, supply cap 20, Tick 961) the return-to-Edit reset restored EVERY number to the authored source (Total 6→5, P1 ore 480→200, P2 ore 80→200, Buildings 3→2, supply→0/10, Tick 961→0): the offline-editor-loop `AuthoredStart` reset arm — the branch the extracted predicate routes — fired on the return edge exactly as the pre-refactor inline guard did.
- result: PASS

## Spec Change Log

_No `bad_spec` loopbacks — spec unchanged during review._

## Review Triage Log

### 2026-07-31 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 2: (high 0, medium 0, low 2)
- defer: 1: (high 0, medium 1, low 0)
- reject: 6: (high 0, medium 1, low 5)
- addressed_findings:
  - `[low]` `[patch]` Test docstrings (`ModeTransitionResetPolicyTests`) implied the truth-table proves the destructive reset *never fires* online/replay, when they assert only `Decide`'s return value → scoped the class + `[Fact]` docs to "returns `AuthoredStart` — the value the handler gates the reset on"; added an explicit scope note that the Godot-coupled handler dispatch is covered by the in-engine gate, not these tests.
  - `[low]` `[patch]` Predicate docs overclaimed: "byte-for-byte identical" (eager `Lockstep.IsOnline` eval differs from the old `&&` short-circuit in unreachable exception semantics) and AuthoredStart "re-seeding DEFAULT_RNG_SEED" (the downstream `ResetToAuthoredStart` re-seeds, not the predicate) → scoped both to "routes only the DECISION; return value identical to the old `offlineEditorLoop`".
- notes:
  - **defer (medium):** three reviewers (blind-hunter, edge-case, verification-gap) converged that the handler's enum→action dispatch (`resetAction == AuthoredStart` → `ResetToAuthoredStart` vs `ResetMatchOnReturnToEdit`) is Godot-coupled and NOT Tier-1 assertable — a wiring regression (arg swap / flipped comparison) would pass all 9 predicate tests yet fire the destructive reset online. This run's invocation forbids editing the deferred-work ledger ("the orchestrator records resolution"), so the canonical DW entry is surfaced under `Auto Run Result → Deferred` for the orchestrator to record, rather than written to `deferred-work.md` here. In-engine gate live-verified the offline arm; the online/replay arm rests on the source-wiring proof (`TryLoadReplay` sets `ReplayPlayer` before `SetMode(Play)`) — consistent with the project's Epic-10 deferral of live online/replay verification.
  - **reject (6):** 3rd-`GameMode` contingency (pre-existing binary `if/else`, not introduced here; `GameMode` has exactly {Edit,Play}); redundant `[Fact]` vs `[Theory]` (intentional exhaustive safety invariant); enum-consumed-as-boolean / `None`-vs-`Lifecycle` inert / make `internal` / use `switch` (design-taste; the 3-valued enum faithfully encodes the intent's named `(…, targetMode)` signature and matches the `DelayMath`/`HandshakeGate` public-predicate precedent; intent-alignment auditor confirmed a legitimate reading, behavior-neutral); eager-eval NRE (unreachable — `Lockstep` is provably non-null whenever `ReplayPlayer != null`; addressed by the doc-scoping patch).

## Auto Run Result

Status: done
DW resolved: DW-22

### Summary
Repair session for the deferred `DW-22` bundle. The predicate extraction itself was already implemented and committed at `9a65905` (the highest-blast-radius `WinConditionPhase.ModeChanged` offline-editor-loop reset guard is now the pure, Godot-free, Tier-1-tested `ModeTransitionResetPolicy.Decide(isOnline, hasReplay, targetIsPlay)`, consumed by the handler as the single source of truth). The prior dev session's work failed deterministic verification because the `tools/verify-in-engine-gate.ps1` gate — required for any `src/Core/Bootstrap/**` touch — had no `### In-Engine Gate` artifact in the story spec at the resolved path. This session performed the in-engine gate live over the godot-mcp bridge and appended the completed block. No code changed; behavior confirmed identical.

### What the repair changed
- `_bmad-output/implementation-artifacts/spec-reset-guard-predicate-extraction.md` — added the `### In-Engine Gate - 2026-07-31` block (real captured HUD/resource digests, numbers asserted against `alpha_map_01.json`); set `status: done`. No `<intent-contract>` edit; no deferred-work ledger edit.

### Verification performed (this session, all re-run — verified, not trusted)
- `dotnet test ProjectChimera.Sim.Tests --filter ModeTransitionResetPolicy` → Passed 9/9.
- `dotnet test ProjectChimera.Sim.Tests` (full Tier-1) → Passed 3714, Skipped 1 (pre-existing `RandomEffect…ReservedUntilStory2_3`), Failed 0. Includes `GodotFreeBoundaryTest` + `PhaseOrderTest` — predicate confirmed in the Godot-free assembly, no regression.
- In-engine gate (godot-mcp, offline arm, LIVE): offline editor loop on `alpha_map_01.json`. Authored Edit board captured verbatim `P1:3 P2:2 Total:5, ore 200/200, Buildings 2, Nodes 8, Tick 0` (matches JSON authoring source). F5→Play then a live-sim drift to `Total:6, P1 ore 480, P2 ore 80, Buildings 3, Tick 961`. F5→Edit restored the authored board byte-for-byte (`Total:5, ore 200/200, Buildings 2, Tick 0`) — the `AuthoredStart` reset arm the predicate routes fired on the return edge exactly as before. Editor error log clean (0 errors).

### Deferred (for the orchestrator to record in the ledger — this run must not edit `deferred-work.md`)
The prior review surfaced one deferral that still stands: the handler's enum→action dispatch (`resetAction == AuthoredStart` → `ResetToAuthoredStart` vs `ResetMatchOnReturnToEdit`) remains Godot-coupled and NOT Tier-1 assertable — a wiring regression (arg swap / flipped comparison) would pass all 9 predicate tests yet fire the destructive reset on an online return-to-Edit. The offline arm is now live-verified by the in-engine gate above; the online/replay arm rests on the source-wiring proof (`TryLoadReplay` assigns `ReplayPlayer` before `SetMode(Play)`), consistent with Epic-10's deferral of live online/replay verification.
```markdown
### DW-<n>: WinConditionPhase enum→reset dispatch is not Tier-1 assertable — handler wiring verified only by inspection/in-engine gate
origin: deferred by review of `_bmad-output/implementation-artifacts/spec-reset-guard-predicate-extraction.md`, 2026-07-31
source_spec: `_bmad-output/implementation-artifacts/spec-reset-guard-predicate-extraction.md`
location: godot/src/Core/Bootstrap/Phases/WinConditionPhase.cs:264-288
severity: medium
reason: DW-22 extracted the reset DECISION into a Tier-1-tested predicate, but the handler's dispatch on that decision (resetAction == AuthoredStart → ResetToAuthoredStart vs ResetMatchOnReturnToEdit) stays Godot-coupled and untested. — Evidence: a wiring regression (swap the three bool args, flip the `== AuthoredStart` comparison, or branch the → Edit path on `!= None`) compiles and passes all 9 predicate tests yet would fire the destructive authored-start reset on an online return-to-Edit and desync lockstep; the offline arm is covered by the in-engine gate but no automated test exercises the online/replay dispatch (no Godot integration-test project). Fix path: extract a second pure step the handler dispatches on, so the wiring is Tier-1 assertable.
status: open
```

### Residual risks
Low. The routing math is exhaustively Tier-1-pinned; the offline behavior is now live-verified numerically against the authoring source. The only unautomated surface is the Godot-coupled handler dispatch around the predicate (the deferred item above), left byte-equivalent and covered by the in-engine gate.

