---
title: 'match-seed-plumbing: per-match RNG seed producer + reset/recorder threading (DW-17, DW-225)'
type: 'feature'
created: '2026-08-03'
status: 'done'
baseline_revision: '0f6014696d51687c15ab721baf6ecdaade05b66d'
final_revision: 'e986a9639fa49d4a05fe917795521a5ea58a0f9b'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/godot/CLAUDE.md'
warnings: []
---

<intent-contract>

## Intent

**Problem:** The offline Edit→Play reset (`EntityWorld.Clear`, via `ClearForReset`) always re-seeds the sim RNG to the hardcoded `EntityWorld.DEFAULT_RNG_SEED`, and `ReplayRecorder` is constructed with that same literal — there is no per-match seed producer. Correct today (every match starts default-seeded), but the two sites are siblings that must ship together: the day a non-default match seed exists, an in-place reset seeded to `DEFAULT` diverges from the seeded stream (DW-17) and the recorder records a seed the match never ran with (DW-225).

**Approach:** Add a small pure `MatchSeedProducer` (Godot-free, SplitMix64 mixing) and thread its output through a single `SceneContext.LiveMatchSeed` field. The offline `ResetToAuthoredStart` path MINTS a per-match seed (entropy = presentation-side wall-clock), re-seeds the live world to it *after* `ClearForReset` and *before* the authored re-apply, and stores it; the (online-only) `ReplayRecorder` reads `LiveMatchSeed` instead of the literal. The online lobby path pins `LiveMatchSeed = DEFAULT_RNG_SEED` (its determinism contract until the Epic-9 seed handshake), so its recorded seed is behavior-identical to today.

## Boundaries & Constraints

**Always:**
- `MatchSeedProducer` is pure, integer-only, Godot-free (it lives in `src/Core/**` under the banned-API determinism analyzer — NO wall-clock, float, or BCL/engine RNG inside it; the caller supplies entropy).
- `EntityWorld.Clear()` MUST keep re-seeding to `DEFAULT_RNG_SEED` (the "a cleared world == a fresh `new EntityWorld()`" invariant that goldens and `SimResetTests` depend on). The per-match seed is applied *after* `ClearForReset`, never by changing `Clear()`.
- Re-seed the world BEFORE `_applier.Apply(...)` in the reset so the whole match (apply + ticks) rides the per-match stream.
- The online path stays default-seeded: a varying online seed with no handshake would desync lockstep. The AuthoredStart reseed only ever runs offline (`ModeTransitionResetPolicy` returns `AuthoredStart` only when `!isOnline && !hasReplay`); the online recorder must record `DEFAULT_RNG_SEED`.
- `SceneContext.LiveMatchSeed` defaults to `EntityWorld.DEFAULT_RNG_SEED`.

**Block If:**
- Resolving either half would require building the actual MP seed handshake (agreeing a shared seed across peers) — that is Epic-9-future and out of scope; HALT rather than introduce a networked seed.

**Never:**
- Do NOT re-seed the world on the online (`OnMatchStart` player/spectator → `GoOnline`) path to a non-default value, and do NOT touch `ReplayPlayer`'s header restore (`_world.Rng.Seed(Seed)`) — playback already restores the recorded seed correctly.
- Do NOT change `EntityWorld.DEFAULT_RNG_SEED`, `SimRng`, the replay file format/header, or any golden.
- Do NOT re-record or move any golden.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Mix entropy | `Produce(entropy)` | Deterministic 64-bit seed == `new SimRng(entropy).NextRaw()` (canonical SplitMix64 first draw) | None (total function) |
| Distinct/sequential entropy | `Produce(n)` vs `Produce(n+1)` | Well-separated, distinct seeds (avalanche) | None |
| Offline Edit→Play | AuthoredStart reset runs | World RNG re-seeded to a fresh per-match seed; `LiveMatchSeed` == that seed; not `DEFAULT` | Reset veto paths unchanged (world untouched on reject) |
| Two consecutive offline plays | Enter Play, back to Edit, enter Play | Two DIFFERENT non-zero `LiveMatchSeed` values (per-match) | None |
| Online match start | `OnMatchStart` player branch | `LiveMatchSeed` == `DEFAULT_RNG_SEED`; `ReplayRecorder` records `DEFAULT` (as today) | None |

</intent-contract>

## Code Map

- `godot/src/Core/MatchSeedProducer.cs` -- NEW. Pure static producer; SplitMix64 mix of a caller-supplied `ulong` entropy → match seed.
- `godot/src/Core/SimRng.cs` -- reference: SplitMix64 (`NextRaw` = `(_state += GAMMA)` then finalizer); `Seed(seed)` sets state; tolerates any seed incl. 0. The producer mirrors its first-draw mix.
- `godot/src/Core/EntityWorld.cs` -- `DEFAULT_RNG_SEED` (`:186`), `Clear()` re-seed at `:1256` (UNCHANGED), `Rng` (public).
- `godot/src/Core/Bootstrap/Phases/SceneContext.cs` -- add `public ulong LiveMatchSeed` field (single source of truth read by both reset + recorder).
- `godot/src/Core/MainScene.cs` -- `ResetToAuthoredStartCore`: after `_host.ClearForReset();` (`:2466`) and before `_applier.Apply` (`:2519`), mint+seed+store the per-match seed and log it.
- `godot/src/Core/Bootstrap/Phases/MatchLifecycleController.cs` -- `StartRecording` (`:221`): pass `_ctx.LiveMatchSeed` not the literal; `OnMatchStart` player branch (~`:164`): pin `_ctx.LiveMatchSeed = EntityWorld.DEFAULT_RNG_SEED` before `StartRecording()`.
- `godot/ProjectChimera.Sim.Tests/Determinism/MatchSeedProducerTests.cs` -- NEW xUnit tests (mirrors `SimRngTests` conventions).

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/MatchSeedProducer.cs` -- CREATE a `public static class MatchSeedProducer` in namespace `ProjectChimera.Core` with `public static ulong Produce(ulong entropy)` computing the canonical SplitMix64 first-draw mix (`z = entropy + 0x9E3779B97F4A7C15UL;` then the two `unchecked` multiply-xor-shift rounds and final `z ^ (z >> 31)`). XML-doc it as the per-match seed seam; entropy is caller-supplied.
- `godot/src/Core/Bootstrap/Phases/SceneContext.cs` -- ADD `public ulong LiveMatchSeed = EntityWorld.DEFAULT_RNG_SEED;` with a comment: the seed the live world's RNG was last (re)seeded to at match start; read by the offline reset and the recorder.
- `godot/src/Core/MainScene.cs` -- In `ResetToAuthoredStartCore`, immediately after `_host.ClearForReset();`, add: `ulong matchSeed = MatchSeedProducer.Produce(Time.GetTicksUsec()); _host.World.Rng.Seed(matchSeed); _ctx.LiveMatchSeed = matchSeed; GD.Print($"[MatchSeed] Offline match seed 0x{matchSeed:X16}");` with a comment tying it to DW-17/DW-225 and the "before Apply" requirement.
- `godot/src/Core/Bootstrap/Phases/MatchLifecycleController.cs` -- In `StartRecording`, replace `EntityWorld.DEFAULT_RNG_SEED` (`:221`) with `_ctx.LiveMatchSeed` and update the `:219-220` comment. In `OnMatchStart`, before the player branch's `StartRecording()`, set `_ctx.LiveMatchSeed = EntityWorld.DEFAULT_RNG_SEED;` with a comment: online plays the default-seeded world (all peers agree) until the Epic-9 seed handshake replaces this line with the agreed seed.
- `godot/ProjectChimera.Sim.Tests/Determinism/MatchSeedProducerTests.cs` -- CREATE xUnit tests covering the I/O matrix rows: `Produce(e) == new SimRng(e).NextRaw()` for several `e` (independent oracle); determinism (same input → same output); distinct + sequential inputs → distinct seeds; a zero-entropy input yields a non-zero, well-mixed seed.

**Acceptance Criteria:**
- Given the offline editor loop, when the player enters Play twice (Edit→Play, →Edit, →Play), then the two `LiveMatchSeed` values differ and neither equals `DEFAULT_RNG_SEED` — the reset re-seeded to a fresh per-match seed, not the constant (DW-17).
- Given an online match start, when `StartRecording` runs, then the `ReplayRecorder` is constructed with `_ctx.LiveMatchSeed` and that value equals `DEFAULT_RNG_SEED` — behavior-identical to the pre-change literal (DW-225 seam, no online behavior change).
- Given the Tier-1 determinism suite and every golden, when the full suite runs, then all goldens reproduce byte-for-byte and `SimResetTests` still asserts `DEFAULT_RNG_SEED` after `ClearForReset` (no fold, no re-record).

## Spec Change Log

(No bad_spec loopback occurred — the review pass resolved every this-story finding as a patch. The online establish-vs-assume refinement is recorded in Design Notes.)

## Review Triage Log

### 2026-08-03 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 3: (high 1, medium 1, low 1)
- defer: 2: (medium 1, low 1)
- reject: 7: (medium 2, low 5)
- addressed_findings:
  - `[high]` `[patch]` Offline per-match reseed leaks into a same-session online match — the session `EntityWorld` is reused and `GoOnline`/`GoSpectate` never reseed `World.Rng`, so an offline F5 playtest would carry its per-match seed into lockstep and desync (confirmed real: pre-change `ClearForReset` always returned the world to DEFAULT). Fix: `OnMatchStart` now ESTABLISHES the shared origin — pins `LiveMatchSeed = DEFAULT_RNG_SEED` AND `_ctx.World.Rng.Seed(_ctx.LiveMatchSeed)` before the spectator/player split (was pin-only, assumed DEFAULT).
  - `[medium]` `[patch]` The in-engine gate witnessed only the producer output, not the applied reseed (a dropped/wrong-instance `Rng.Seed` would leave the `[MatchSeed]` log identical). Fix: the log now reads back `world.Rng.State`; gate re-run confirms `world.Rng == matchSeed` for every toggle.
  - `[low]` `[patch]` Misleading comment claimed the authored re-apply "rides this per-match stream" though apply draws no RNG. Fix: reworded — seed placement is for tick-time draws and to set `LiveMatchSeed` before any later read; before/after `Apply` is equivalent for the board.
- deferred (reported to the orchestrator per this run's "do not edit the deferred-work ledger" instruction — NOT written to `deferred-work.md`):
  - `[low]` SP save/load leaves `LiveMatchSeed` holding the discarded step-3 mint while `SaveGameState.RestoreInto` reseeds `World.Rng` to the saved `RngState` — inert today (offline load has no recorder consumer), becomes relevant only when offline recording is wired (DW-429 territory). Location: `MainScene.cs` ResetToAuthoredStartCore step-3 vs step-8 load restore.
  - `[medium]` The per-launch varying offline seed removes cross-launch reproducibility for RNG-touching content, weakening the project's in-engine A/B methodology; there is no debug/env override to PIN the offline seed for repro/verification runs. Out of the bundle's intent (which asked for a per-match seed, not a repro pin). Location: `MainScene.cs` `MatchSeedProducer.Produce(Time.GetTicksUsec())`.
- rejected (noise / out-of-scope): in-body offline guard on a fully caller-gated method (the whole method is offline-only by `ModeTransitionResetPolicy`); same-microsecond wall-clock collision (non-occurring — monotonic µs clock + human/tool-paced resets; the gate empirically minted all-distinct seeds); "no oracle test" (the oracle test exists: `Produce_MatchesFreshSimRngFirstDraw`); online shares the DEFAULT stream (pre-existing, explicitly Epic-9-deferred); a test locking `LiveMatchSeed` out of `StartStateHash` (structurally guaranteed — the hash folds the model, not RNG — and gate-witnessed); the harmless `[MatchSeed]` mint on the Play→Edit direction (overwritten by the next Edit→Play; P1 neutralizes any leak); online "records DEFAULT" not driven in-engine (proportionate residual risk — driving it needs MP infrastructure; documented).

### 2026-08-03 — Review pass (follow-up, review_loop_iteration 0)
- intent_gap: 0
- bad_spec: 0
- patch: 0
- defer: 2: (medium 2)
- reject: 13: (medium 2, low 11)
- addressed_findings:
  - none
- defer (appended to `deferred-work.md` as NEW entries only, per this run's ledger instruction — existing entries incl. the orchestrator-owned DW-17/DW-225 done-marks were left untouched):
  - `[medium]` DW-498 — no automated regression coverage for the LiveMatchSeed plumbing; the load-bearing online reseed-to-DEFAULT invariant (`MatchLifecycleController.cs:157`) and the offline reset wiring (`MainScene.cs:2478-2481`) are guarded only by the manual in-engine gate, so a future Godot-layer refactor could silently regress DW-17/desync with a green `dotnet test`. Behavior is correct today (re-verified in-engine this pass); this is invariant durability.
  - `[medium]` DW-499 — the per-launch varying offline seed removes cross-launch reproducibility for RNG-touching content and there is no debug/env override to PIN the offline seed for repro/A-B verification runs. By-design per the bundle intent (per-match seed), out of its scope to add a pin; deferred as a real usability follow-up.
- rejected (noise / out-of-scope / re-adjudicated consistent with the prior pass): "offline seed minted but never recorded / goal unmet" (the recorder is explicitly online-only in the intent; the DW-17 goal is the per-match seam + seed, which is met — the reproducibility angle is DW-499); tautological/weak oracle test and the duplicated SplitMix64 finalizer (the inline copy + independent `SimRng` oracle is a deliberate, documented Design-Notes choice; delegating to `SimRng` would make the oracle truly tautological); `Produce_ManyDistinctInputs` vacuous for a bijection and "well-mixed only via NotEqual" (low test-quality nits; the oracle ties the mix to the canonical stream); in-body offline guard on `ResetToAuthoredStartCore` (the method is fully caller-gated offline-only by `ModeTransitionResetPolicy` — same rejection as the prior pass); `GetTicksUsec` low-entropy/deterministic-launch collision and the same-microsecond double-reset collision (non-occurring — monotonic µs clock + human/tool-paced resets; the seed-preimage-of-DEFAULT collapse is 1/2^64 and harmless); the `Produce(0) != DEFAULT` decorative assert; StartRecording comment "drift" (the comment accurately describes the shared `LiveMatchSeed` field, not an offline-recording claim); the online reseed's board-parity assumption (out-of-scope Epic-9 MP board-state sync, not this seed seam); the unconditional `[MatchSeed]` `GD.Print` (intentional and load-bearing — the in-engine gate reads back `World.Rng.State` from it to witness the applied reseed).
- intent-alignment note (descriptive, no action): the one contested line is the online `_ctx.World.Rng.Seed(...)` at `MatchLifecycleController.cs:157`. It diverges from the Approach's "pin only" phrasing but NOT from the literal `Never` constraint, whose precise wording forbids reseeding "to a non-default value" — reseeding to `DEFAULT_RNG_SEED` is permitted, and correctness forces it (the reused `EntityWorld` would otherwise carry an offline per-match seed into lockstep). So the two readings converge on the one correct behavior the diff implements; not an intent_gap.

## Design Notes

Why the producer mirrors `SimRng`'s first draw exactly: `Produce(entropy) == new SimRng(entropy).NextRaw()`. This ties the seed to the already-canonical, externally-cited SplitMix64 stream, gives a non-tautological test oracle, and guarantees good separation of near-sequential wall-clock entropy.

```csharp
// canonical SplitMix64 first-draw mix (same as SimRng.NextRaw for a fresh state)
ulong z = unchecked(entropy + 0x9E3779B97F4A7C15UL);
z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
return z ^ (z >> 31);
```

Reading chosen (documented so review sees the alternative was weighed): each offline Edit→Play MINTS a fresh per-match seed (Reading A: "per-match"). The alternative — mint-once then restore the same seed on every reset (Reading B, closer to DW-17's "capture and restore" wording) — is unobservable to anything external: the AuthoredStart reseed runs ONLY offline (online/replay skip it), offline never records and has no peers, and every offline transition is a full authored re-apply (a new match). So the readings converge on all networked/recorded/golden outcomes and diverge only in local offline feel, where "per-match seed" selects fresh-per-match. The `LiveMatchSeed` seam still satisfies DW-17's forward-looking intent: when a real (online) match seed lands, the reset and recorder already read it instead of the hardcoded constant.

Online establish-vs-assume (review refinement): the session's `EntityWorld` is REUSED across matches — nothing on the online entry path (`OnMatchStart`/`GoOnline`/`GoSpectate`) reloads the scene or reseeds `World.Rng`. Because the offline reset now leaves `World.Rng` on a per-match value, an offline F5 playtest followed by an online match in the same session would carry that value into lockstep and desync. So `OnMatchStart` does not merely PIN `LiveMatchSeed = DEFAULT_RNG_SEED`; it also `_ctx.World.Rng.Seed(_ctx.LiveMatchSeed)` — establishing the shared origin the recorder header and every peer rely on, rather than assuming the world is already DEFAULT. The Epic-9 handshake swaps that one `DEFAULT_RNG_SEED` for the agreed seed and the reseed already carries it.

## Verification

**Commands:**
- `dotnet build godot/godot.csproj` -- expected: build succeeds (C# is not hot-loaded; required before the in-engine gate).
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: all tests pass, including the new `MatchSeedProducerTests` and the unchanged `SimResetTests`/golden suite (no golden moved).

**Manual checks (in-engine gate — REQUIRED, this diff touches `src/Core/Bootstrap/**` and `MainScene.cs`):**
- Drive the running editor: enter Play (offline), capture the `[MatchSeed]` log line; return to Edit; enter Play again; capture the second `[MatchSeed]` line. Assert the two seeds differ, are non-zero, and neither equals `0x9E3779B97F4A7C15` (DEFAULT_RNG_SEED). Append the `### In-Engine Gate` artifact block with the verbatim log digests.

### In-Engine Gate - 2026-08-03 (re-run after review patches)
- surface: offline Editor Edit↔Play loop (F5 toggle → `GameState.Toggle` → `ModeChanged` → `MainScene.ResetToAuthoredStartCore` on the default boot scenario).
- launched: rebuilt `dotnet build godot/godot.csproj` (0 errors) AFTER the review patches, restarted the editor to load the fresh C# assembly, `godot_editor_edit run` (main.tscn), then injected four spaced F5 key presses via `godot_input` (Edit→Play→Edit→Play). Digest captured from the running game's `godot.log`. (The log line now reads back `world.Rng.State` — a review fix so the gate WITNESSES the applied reseed, not just the producer's output.)
- digest: verbatim game-log lines (game process stdout), format `... seed 0x<produced> (world.Rng=0x<live-RNG-state-after-reseed>)`:
  ```
  [GameState] Mode → Play
  [MatchSeed] Offline match seed 0xD637210EB0C0E30D (world.Rng=0xD637210EB0C0E30D)
  [GameState] Mode → Edit
  [MatchSeed] Offline match seed 0x7185C7620C114C37 (world.Rng=0x7185C7620C114C37)
  [GameState] Mode → Play
  [MatchSeed] Offline match seed 0x02ABBC8C501928F6 (world.Rng=0x02ABBC8C501928F6)
  [GameState] Mode → Edit
  [MatchSeed] Offline match seed 0x0269BF7D1A71DB16 (world.Rng=0x0269BF7D1A71DB16)
  ```
  (An earlier pre-patch run showed the same properties with seeds `0xE2A2D2066368315C / 0x591CAAAAA6E13927 / 0x958E44EC69E297AC / 0x248C60244962F7D2` and a constant `[Reset]` start-state hash `0x022DFB3438FC42F9` across toggles.)
- asserted:
  - Reseed WITNESSED: for every toggle `world.Rng` == the produced seed exactly (e.g. `0xD637210EB0C0E30D` == `0xD637210EB0C0E30D`) — proving `_host.World.Rng.Seed(matchSeed)` actually landed on the live world's RNG, not merely that the producer ran (closes the review's broken-verification-gap).
  - Two consecutive offline Play entries yield DIFFERENT seeds — arm A (Edit→Play) `0xD637210EB0C0E30D` ≠ arm B (Edit→Play) `0x02ABBC8C501928F6`. All four minted seeds pairwise distinct → per-match producer varies (DW-225) AND the reset reseeds to the live match seed, not the constant (DW-17).
  - No minted seed equals `DEFAULT_RNG_SEED` (`0x9E3779B97F4A7C15`) and none is `0` — the reset no longer clobbers the RNG to the hardcoded default.
  - The pre-patch run confirmed the `[Reset]` start-state model hash is IDENTICAL across toggles (`0x022DFB3438FC42F9`) — the authored board re-applies unchanged; the per-match reseed perturbs only the RNG stream, not the scenario model (no golden-affecting change; `StartStateHash` does not fold RNG state).
  - Online arm (recorder records `DEFAULT_RNG_SEED`) is not driven here — it requires a lobby/server match; verified by inspection: `OnMatchStart` now pins `_ctx.LiveMatchSeed = EntityWorld.DEFAULT_RNG_SEED` AND reseeds `_ctx.World.Rng` to it (review patch: the online world is REUSED across matches and nothing else reseeds it, so the offline per-match seed is explicitly cleared back to DEFAULT before the recorder reads it). Behavior-identical to the pre-change literal for a clean session, and now leak-proof against an offline→online transition.
- result: PASS

## Auto Run Result

Status: done
Blocking condition: none

**Change:** Follow-up review pass on the already-implemented match-seed-plumbing bundle (DW-17 + DW-225). No code changed this pass. Five review layers (adversarial, edge-case, verification-gap, intent-alignment, in-engine-gate auditor) ran in parallel against the full diff since the baseline revision. Triage: 0 intent_gap, 0 bad_spec, 0 patch, 2 defer, 13 reject. The implementation is correct and behavior-verified; the two deferrals are durability/usability follow-ups, not defects in the shipped change.

**Files changed (this pass):**
- `_bmad-output/implementation-artifacts/spec-match-seed-plumbing.md` — appended the follow-up Review Triage Log entry and this Auto Run Result; set `status: done`, `followup_review_recommended: false`.
- `_bmad-output/implementation-artifacts/deferred-work.md` — appended DW-498 and DW-499 as NEW entries only (the orchestrator-owned DW-17/DW-225 done-marks and every other existing entry were left untouched, per this run's ledger instruction).

**Verification:**
- In-engine gate (REQUIRED — the reviewed diff touches `Core/Bootstrap/**` and `MainScene.cs`) — INDEPENDENTLY RE-RUN this pass by the gate auditor: `dotnet build godot/godot.csproj` → 0 errors; drove the offline Edit↔Play loop over godot-mcp with 6 spaced F5 toggles. Witnessed digest — 6 minted seeds (`0x29360A4DBB59D62E`, `0xCC30C223FD78EEF1`, `0x06B3A3CCC8E908FA`, `0xF4FD234E867513B0`, `0x4625CFE23DE8A4DF`, `0xC579AA024F973D56`), each with `world.Rng` byte-identical to the produced seed (reseed provably landed on the live RNG), all pairwise distinct, none equal to `DEFAULT_RNG_SEED` (`0x9E3779B97F4A7C15`) or 0, and the `[Reset]` authored-board model-hash identical across every toggle (`0x022DFB3438FC42F9` — per-match reseed perturbs only the RNG stream, not the scenario model). Gate verdict: PASS. No divergence.
- Prior pass's `dotnet build` (0 errors) + `dotnet test` (3793 passed / 0 failed / 1 pre-existing skip; no golden moved) stand — no code changed this pass to invalidate them.

**Review findings breakdown:**
- Patches applied: 0 (nothing rose to a required trivial fix; the shipped behavior is correct and gate-verified).
- Deferred: 2 (DW-498 medium — no automated regression net for the LiveMatchSeed plumbing, incl. the load-bearing online reseed-to-DEFAULT invariant, currently guarded only by the manual in-engine gate; DW-499 medium — per-launch varying offline seed removes cross-launch reproducibility with no debug/env pin override).
- Rejected: 13 (test-quality nits against a deliberate documented oracle design; astronomically-improbable seed collisions; an in-body guard on a fully caller-gated offline-only method; out-of-scope Epic-9 MP board-parity; the intentional gate-load-bearing `[MatchSeed]` log line).

**Follow-up review recommendation:** false. This pass applied 0 patches (score 0 < 5, no high-severity patch), and the prior pass's `true` — driven by its high-severity online-desync patch — is now satisfied: this follow-up re-reviewed and in-engine-re-verified that fix and found no defect. The story has converged.

**Residual risks:**
- The online "records DEFAULT" path remains Tier-1-excluded (Godot-coupled MP) and not driven in-engine (needs a lobby/server match); verified by inspection + direct code path, and now tracked for a testable seam under DW-498. Low risk — the online reseed is unconditionally correct under today's no-handshake contract and behavior-identical to the pre-change literal for a clean session.
- Offline playtests are non-deterministic run-to-run for RNG-touching content by design (per-match seed); replays still reproduce exactly via the captured seed. Repro-pin gap tracked under DW-499.
