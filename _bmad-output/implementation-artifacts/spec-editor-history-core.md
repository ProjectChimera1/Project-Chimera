---
title: 'Harden the shared EditorHistory core — reset-clear + byte-capped bounded history (DW-138, DW-140)'
type: 'bugfix'
created: '2026-07-27'
status: 'done'
baseline_revision: 'd999eed32c609977cfd67a4364a9a28f1288f00d'
final_revision: 'a0430f43089d03cac0c0e203e5ef3ecf6266507b'
review_loop_iteration: 0
followup_review_recommended: false # patched this pass: medium 1, low 1 → score 3×1+1×1=4 (<5), no high → false
context:
  - '{project-root}/godot/CLAUDE.md'
warnings:
  - multiple-goals
  - oversized
---

<intent-contract>

## Intent

**Problem:** The shared `EditorHistory` (driven by both `EntityPlacer` entity ops and `TerrainBrush` strokes) has two latent hazards. (1) DW-138: its stacks survive the F5 Edit→Play→Edit round-trip — only `_redoStack` is ever cleared — so a post-F5 Ctrl+Z/Y replays undo/redo closures whose captured slot ids went stale at `ClearForReset`+re-apply; Story 6.1 routes those closures into `ScenarioData` too, so a post-F5 undo can strip or re-add a scenario entry that no longer matches the live entity, corrupting the persisted board. (2) DW-140: every terrain stroke deep-`Duplicate`s height+control `Image`s (before AND after) per touched region onto an uncapped stack, so a long sculpt pins hundreds of MB–GB of undo memory.

**Approach:** Add `EditorHistory.Clear()` (wipes both stacks) and invoke it on the match-reset / return-to-Edit seams so no undo/redo closure can outlive the reset that made its ids stale. Add a shared bounded-history policy to `EditorHistory`: a per-entry byte estimate carried on `Push`, a running byte total, and drop-oldest trimming when a configurable byte cap (or entry-count cap) is exceeded, so heavy terrain snapshots are weighed correctly while cheap entity ops (0 bytes) are not penalized. Wire `TerrainBrush` to report each stroke's snapshot byte cost.

## Boundaries & Constraints

**Always:**
- `EditorHistory` stays pure C# — no Godot types, no `using Godot;` (it is in the Godot-free Tier-1 set via `SimSources.props`).
- `Clear()` empties BOTH the undo and redo stacks and resets the byte counter to 0.
- `Push(redo, undo)` keeps working unchanged for existing callers (`EntityPlacer`, `WaterTool`, `RegionTool`, `PathabilityTool`, `CameraTool`) — the byte-cost parameter is optional and defaults to 0.
- Trimming drops the OLDEST undo entries (not the newest) and always retains at least the single most-recent entry, even if it alone exceeds the cap (a giant stroke must stay undoable once).
- Reset-clear is invoked only AFTER the point of no return in `ResetToAuthoredStart` (after `_host.ClearForReset()`), never on an early veto that left the world unchanged.
- LIFO interleave and "push-after-undo clears the redoable future" semantics are preserved exactly.

**Block If:**
- Making `EditorHistory` bounded would require it to take a Godot dependency (it must not — resolve by computing byte estimates on the `TerrainBrush` side and passing a plain `long`).

**Never:**
- Do not edit the deferred-work ledger (`deferred-work.md`) — the orchestrator records resolution.
- Do not add a second parallel history stack or change how entity/terrain ops share the one instance.
- Do not cap or trim the redo stack directly (it is transitively bounded: redo entries only ever come from undone undo-stack entries, and `Push` clears redo).
- Do not clear history on the online/replay match-start path that never ran `ClearForReset`.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Clear wipes both stacks | history with N undo + M redo entries, `_undoBytes>0` | after `Clear()`: `CanUndo==false`, `CanRedo==false`, byte total 0 | No error expected |
| Byte cap drops oldest | cap=100; push 3 entries of 60 bytes each | oldest entry dropped as each new push crosses cap; only entries whose summed bytes ≤ cap (plus the mandatory most-recent) survive; undoing the dropped op is impossible | No error expected |
| Single oversized entry retained | cap=100; push one 500-byte entry | entry is kept (most-recent floor); `CanUndo==true` | No error expected |
| Entry-count cap drops oldest | maxEntries=2; push 3 zero-byte entity ops | oldest op dropped; exactly 2 remain undoable, newest-first | No error expected |
| Cheap ops don't trip byte cap | small byte cap; push many 0-byte entity ops | byte total stays 0; byte cap never trims (only the entry-count cap can) | No error expected |
| Redo after trim | push entries past cap, undo the survivors, redo | redo replays only surviving entries; trimmed-away entries are gone | No error expected |

</intent-contract>

## Code Map

- `godot/src/CreationSuite/EditorHistory.cs` -- the shared undo/redo core; add `Clear()`, byte/entry caps, `Push(redo, undo, estimatedBytes)`, drop-oldest trim. Convert the undo `Stack` to a deque (e.g. `LinkedList`) so oldest entries can be dropped.
- `godot/src/CreationSuite/TerrainBrush.cs` -- `PushStrokeUndo` / `SnapshotRegions`; estimate stroke byte cost (before+after region `Image`s) and pass it to `_history.Push(...)`.
- `godot/src/Core/MainScene.cs` -- `ResetToAuthoredStart` (call `_ctx.Placer.History.Clear()` right after `_host.ClearForReset()`, ~line 1979) and `ResetMatchOnReturnToEdit` (call it there too, covering the online/replay direct return-to-Edit path).
- `godot/src/UI/EntityPlacer.cs` -- owns `_history` / exposes `History`; no change needed (verify default-0 `Push` still compiles).
- `godot/ProjectChimera.Sim.Tests/CreationSuite/EditorHistoryTests.cs` -- extend with Clear() + bounded-policy tests.

## Tasks & Acceptance

**Execution:**
- `godot/src/CreationSuite/EditorHistory.cs` -- Add `Clear()`; add `public const long DefaultMaxBytes = 512L*1024*1024;` and `public const int DefaultMaxEntries = 1000;`; add ctor params `long maxUndoBytes = DefaultMaxBytes` and `int maxUndoEntries = DefaultMaxEntries`; overload `Push(redo, undo, long estimatedBytes = 0)`; track `_undoBytes`; switch the undo store to a `LinkedList<Entry>` so `Trim()` can drop from the oldest end while `(_undoBytes>maxUndoBytes || count>maxUndoEntries) && count>1`; keep `Undo`/`Redo`/`CanUndo`/`CanRedo`/LIFO/redo-clear-on-push semantics; keep it pure C#.
- `godot/src/CreationSuite/TerrainBrush.cs` -- In `PushStrokeUndo`, sum an estimated byte cost over the `before` and `after` snapshots (per `Image`: `width*height*bytesPerPixel(format)`, Height+Control, null-safe) via a small private helper, and pass it as the new `estimatedBytes` arg to `_history.Push(...)`. -- weighs heavy strokes so the cap bounds real memory.
- `godot/src/Core/MainScene.cs` -- Invoke `_ctx.Placer.History.Clear()` immediately after `_host.ClearForReset()` in `ResetToAuthoredStart`, and once in `ResetMatchOnReturnToEdit`; comment why the ResetToAuthoredStart site (past the veto) covers the id-invalidating paths and the ResetMatchOnReturnToEdit site covers the online/replay return path. -- kills stale post-F5 closures before an undo can reach `ScenarioData`.
- `godot/ProjectChimera.Sim.Tests/CreationSuite/EditorHistoryTests.cs` -- Add facts for every I/O Matrix row (Clear wipes both stacks; byte cap drops oldest; single oversized entry retained; entry-count cap drops oldest; 0-byte ops never trip the byte cap; redo after trim). -- pins the bounded policy + Clear contract.

**Acceptance Criteria:**
- Given an `EditorHistory` with entries on both stacks, when `Clear()` is called, then `CanUndo` and `CanRedo` are both false and a subsequent `Push` starts from an empty history.
- Given a byte cap C and pushes whose cumulative estimated bytes exceed C, when each push lands, then the oldest entries are dropped so the retained undo entries' summed bytes ≤ C (except the mandatory most-recent entry), and the dropped ops are no longer undoable.
- Given a single pushed entry larger than the cap, when it is pushed, then it is retained and undoable (most-recent floor).
- Given `maxUndoEntries = N` and more than N zero-byte pushes, when the (N+1)th lands, then exactly N entries remain undoable in newest-first order.
- Given existing callers that call `Push(redo, undo)` with no byte arg, when the code is built, then it compiles unchanged and those ops record 0 bytes.
- Given the code, when `ResetToAuthoredStart` runs past `ClearForReset` OR `ResetMatchOnReturnToEdit` runs, then `_ctx.Placer.History.Clear()` is invoked (verified by inspection + live F5 check).

## Design Notes

Data structure: the undo stack must become a deque so oldest entries can be dropped (a `Stack<T>` can only drop the newest). Use `LinkedList<Entry>` where `Entry` = `(Action redo, Action undo, long bytes)`; `AddLast` = push, `Last`/`RemoveLast` = pop, `RemoveFirst` = drop-oldest. Redo can stay a `Stack<Entry>`.

Redo bounding (why we don't trim redo): redo entries are only ever produced by `Undo()` moving an entry off the (already-capped) undo stack. Since a `Push` clears redo, the redo stack can hold at most the entries that were on the capped undo stack — so total live memory is transitively bounded to ~2× the cap during a full undo sweep. Trimming redo would silently discard a redoable future the user can still reach.

Byte estimate on the `TerrainBrush` side (keeps `EditorHistory` Godot-free):
```csharp
private static long EstimateImageBytes(Image? img)
{
    if (img == null) return 0;
    int bpp = img.GetFormat() switch { Image.Format.Rf => 4, Image.Format.Rgf => 8,
        Image.Format.Rgba8 => 4, Image.Format.Rgb8 => 3, Image.Format.R8 => 1, _ => 4 };
    return (long)img.GetWidth() * img.GetHeight() * bpp;
}
```
Sum this over Height+Control of every `before` and `after` snapshot; that total is the stroke's `estimatedBytes`.

Reset-clear placement: both F5 edges route through `WinConditionPhase.ModeChanged` → `MainScene.ResetToAuthoredStart` (offline loop), which calls `_host.ClearForReset()` (stale-id moment) then ends with `ResetMatchOnReturnToEdit()`. Clearing after `ClearForReset` covers every path that invalidates ids incl. the build-defect empty-board early-return; the `ResetMatchOnReturnToEdit` call additionally covers the online/replay path that returns to Edit via that method directly. A double `Clear()` on the common path is a harmless no-op.

Multiple-goals warning: DW-138 (reset-clear) and DW-140 (byte cap) are two distinct fixes bundled because both live in the one shared `EditorHistory` core; implement both, do not split.

## Verification

**Commands:**
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: all tests pass, including the new `EditorHistoryTests` facts.
- `dotnet build godot/godot.csproj` -- expected: builds clean (confirms `TerrainBrush` + `MainScene` wiring and the unchanged `Push(redo, undo)` callers compile).

**Manual checks (live / godot-mcp — the F5 round-trip and terrain snapshot have no Godot-free surface):**
- In Edit, place/delete an entity, press F5 to Play then F5 back to Edit; Ctrl+Z must NOT alter `ScenarioData` (no phantom add/remove) — the history is empty after the round-trip.
- Sculpt a long terrain session with a large brush; confirm undo memory stops growing once the cap is reached (oldest strokes become non-undoable) rather than climbing unbounded.

## Review Triage Log

### 2026-07-27 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 2: (high 0, medium 1, low 1)
- defer: 5: (high 0, medium 1, low 4)
- reject: 9
- addressed_findings:
  - `[medium]` `[patch]` (verification-gap) `Redo()` byte re-accounting (`_undoBytes += cmd.Bytes`) was unobserved — deleting it left all tests green yet defeats the byte cap on an undo→redo→keep-editing loop. Added `Redo_RestoresByteTotal_SoLaterTrimEvictsCorrectly`, confirmed to fail if the re-add line is removed.
  - `[low]` `[patch]` (verification-gap) production default caps (`new EditorHistory()`) were never exercised — every cap test injected tiny caps. Added `DefaultCaps_BoundEntityOpCount` (1001 zero-byte pushes bounded to 1000).

## Auto Run Result

Status: done
Bundle: editor-history-core (DW-138, DW-140)

**Implemented change:** Hardened the shared `EditorHistory` core. DW-138: added `Clear()` (wipes both stacks + resets the byte counter), invoked after `_host.ClearForReset()` in `ResetToAuthoredStart` and at the top of `ResetMatchOnReturnToEdit`, so no post-F5 undo/redo closure with stale captured slot ids can strip/re-add a `ScenarioData` entry. DW-140: made the undo store a bounded deque with a per-entry byte estimate, a running total, and drop-oldest trimming past a byte cap (512 MiB) or entry-count cap (1000), retaining ≥1 entry; `TerrainBrush.PushStrokeUndo` now weighs each stroke by its before+after Height/Control image bytes. `Push(redo, undo)` stays backward-compatible via an optional `estimatedBytes = 0` (entity/water/region/pathability/camera ops unchanged).

**Files changed:**
- `godot/src/CreationSuite/EditorHistory.cs` — `Clear()`, byte/entry caps + ctor params, `Push(redo, undo, estimatedBytes)`, undo `Stack`→`LinkedList` deque, `Trim()` drop-oldest.
- `godot/src/CreationSuite/TerrainBrush.cs` — `SnapshotBytes`/`EstimateImageBytes`; passes stroke byte cost into `Push`.
- `godot/src/Core/MainScene.cs` — `_ctx.Placer.History.Clear()` at both reset seams.
- `godot/ProjectChimera.Sim.Tests/CreationSuite/EditorHistoryTests.cs` — 8 new facts (6 matrix rows + 2 review patches).

**Review findings breakdown:** 2 patches applied (both test-only, above); 0 bad_spec; 0 intent_gap; 5 deferred (below); 9 rejected (by-design or speculative: 0-byte entity ops are intentional, soft-cap retain-one is spec'd, terrain-used formats Rf/Rgf are covered, mip/reentrancy/ctor-validation speculative, post-Apply clear already covered by the step-7 seam clear, "coalescing" resolves to drop-oldest per the intent's own text + ledger).

**Deferred findings** (recorded here, NOT appended to `deferred-work.md` — the orchestrator owns ledger bookkeeping per the invocation directive):
- `[medium]` (adversarial/edge-case) Pre-existing throw-safety: `Undo()`/`Redo()` remove the entry from its stack *before* running the (Godot GDExtension) delegate, so a throwing terrain restore loses that history step and the exception escapes to the input pump. Present since Story 6.2; this change preserves the same ordering.
- `[low]` (adversarial) `TerrainBrush` pushes a full before/after snapshot for a no-op stroke (no change-detection). Pre-existing since 6.2; now also charges byte cost.
- `[low]` (adversarial) DW-138 relies on two hand-placed `Clear()` sites rather than clearing inside `ClearForReset` itself; a future re-mint caller could reopen the stale-closure class. Current sites match the intent's prescribed locations and are correct today.
- `[low]` (verification-gap) `EstimateImageBytes` format→bpp table cannot be unit-tested in the Godot-free Tier-1 project (`Image.Format` is a Godot type); the formats terrain actually uses (Rf/Rgf) are covered, unknown → 4 bpp fallback. Residual accuracy risk only affects *when* trimming fires, never correctness.
- `[low]` (verification-gap) DW-138 reset-clear wiring (call-site presence/ordering in `MainScene`) has no Godot-free surface; deferred to a live godot-mcp F5 check that must explicitly exercise place→F5→F5→undo.

**Verification performed:**
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` → Passed 3528, Failed 0, Skipped 1 (pre-existing Story-2.3 reserved skip). `EditorHistoryTests` = 12/12.
- `dotnet build godot/godot.csproj` → 0 errors (only pre-existing unrelated CS8632/CS8602/CS8604 warnings).
- Matrix audit: all 6 I/O rows covered by named facts that ran and passed.
- Not run (no Godot-free surface): the two live godot-mcp manual checks (F5 undo-no-corruption; long-sculpt memory plateau) — deferred to a live session per the deferred findings above.

**Residual risks:** byte cap is a soft ceiling (one oversized stroke can exceed it, by design); the byte-estimate accuracy and the reset-clear wiring are only observable in-engine (see deferred findings); redo memory is transitively bounded to ~2× the cap during a full undo sweep.
