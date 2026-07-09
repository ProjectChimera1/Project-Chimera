---
status: blocked
---

# BMad Dev Auto Result

Status: blocked
Blocking condition: missing previous-story continuity — story 4.6 requires story 4.5's implementation (`epic-4-context.md` Cross-Story Dependencies: "4.6 needs 4.2 + 4.5"; 4.6's UX spec explicitly builds the tech-tree node inspector on "the same right-dock inspector as the building editor"), but 4.5's actual code is **absent from `master`** despite bookkeeping marking it done.

Evidence, in order of discovery:

1. This run started with a clean tree (a stray uncommitted frontmatter edit on this very file, `status: ready-for-dev` vs. body `Status: blocked`, was discarded via `git checkout --` to restore the last consistent committed state — the same self-inflicted dirty-tree loop this file has hit twice before).
2. `_bmad-output/implementation-artifacts/sprint-status.yaml:113` reads `4-5-in-app-building-definition-editor-unit-card-pattern-right-dock-inspector: done`, and `deferred-work.md` carries DW-64 through DW-72 from "code review of story-4.5", citing `source_spec: spec-4-5-in-app-building-definition-editor-unit-card-pattern-right-dock-inspector.md` and locations in `godot/src/CreationSuite/BuildingCardPanel.Edit.cs`.
3. **None of those files exist in `master`.** `godot/src/CreationSuite/BuildingCardPanel.cs`, `BuildingCardPanel.Edit.cs`, `godot/src/Core/Definitions/FactionWriter.cs`, and `spec-4-5-in-app-building-definition-editor-unit-card-pattern-right-dock-inspector.md` were added by commit `1521d73` ("story 4-5-...: implemented and reviewed via bmad-dev-auto"), but `1521d73` is **not an ancestor of `master`'s HEAD (`7f550bd`)**.
4. `git reflog` shows `master` briefly pointed at `1521d73` (`master@{4}`) and, earlier, at `0e5784b` — story 4.4's implementation commit (`master@{9}`) — before both were reset back out of `master`'s history. The discarded commits survive only on rescue refs `refs/heads/attempt-preserve/20260708-213039-0231-1521d739` and `refs/heads/attempt-preserve/20260708-213039-0231-0e5784bc` (plus dirty-worktree snapshots on `refs/attempt-preserve-dirty/...-5ad50979-1` and `...-eb905213-1`), all from the same `20260708-213039-0231` event.
5. Instead of the real code, `master`'s mainline only picked up bookkeeping-only commits — `935d642` ("record DW-63 from story 4.4 review") and `232f44f` ("commit story 4.5 completion bookkeeping") — which mark both stories done in `deferred-work.md`/`sprint-status.yaml` without their source changes ever landing.
6. `.bmad-loop/policy.toml` has `[scm] isolation = "none"`, meaning work is expected to happen in place on the checked-out branch with no branch/worktree indirection — so these `attempt-preserve*` refs are not a designed merge-back path, they look like an emergency snapshot taken before an unexplained reset (likely a stalled/aborted run) that then had its status bookkeeping applied by hand or by a later session without reconciling the code.

Net effect: story 4.6 has no building-editor surface to extend, and stories 4.4/4.5 are falsely reported complete on `master` — a two-story gap, not one.

This needs a human decision before any dev-auto run touches epic 4 again: whether to recover the reviewed work by merging/cherry-picking `attempt-preserve/20260708-213039-0231-1521d739` (story 4.5) and `attempt-preserve/20260708-213039-0231-0e5784bc` (story 4.4) into `master` (resolving DW-64..72 and whatever 4.4 left behind as part of that), or to deliberately redo those stories and correct the `done` bookkeeping if the preserved branches are not trusted. Either way, `sprint-status.yaml` and `deferred-work.md` are currently lying about what's actually in the tree, which will keep misleading future dev-auto/context-compilation runs for epic 4 until it's fixed.

---

**Resolved (bmad-loop-resolve, 2026-07-09):** Human reviewed this finding and directed recovery of both stranded rescue branches into `master` rather than a redo. `attempt-preserve/20260708-213039-0231-0e5784bc` (story 4.4) and `attempt-preserve/20260708-213039-0231-1521d739` (story 4.5) were merged into `master` (merge commits recovering each story's reviewed code — both merges were conflict-free per `git merge-tree` dry runs, and `deferred-work.md`'s DW-64..72 entries de-duplicated automatically since both branches had added identical text). `sprint-status.yaml`'s `4-4` entry was flipped `backlog` → `done` to match; `4-5` was already (truthfully, now) `done`. Story 4.4's orphaned spec file was copied from `.bmad-loop/runs/20260708-213039-0231/deferred/.../spec-4-4-....md` into `_bmad-output/implementation-artifacts/` alongside 4.5's (which the rescue commit already carried). Build and Tier-1 test suite verified green post-merge (one pre-existing, unrelated `ProceduralMapGeneratorTests` golden-hash failure confirmed present on `master` before these merges too — not caused by this recovery). Story 4.6 now has a real `BuildingCardPanel`/right-dock-inspector surface to extend; this run can be re-armed and re-driven.
