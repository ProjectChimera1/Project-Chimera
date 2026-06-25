---
baseline_commit: 2c5588e840bddfe5074857f748a21cfd719cbe1f
---

# Story 1.12: Full RTS command vocabulary (single-target Attack, Patrol, Follow; Hold distinct from Stop)

Status: ready-for-dev

<!-- Context engine analysis completed — comprehensive developer guide. Validation optional: run validate-create-story before dev-story. -->
<!-- 1.12 is the FIRST post-M1 DG (design-gap) story. M1 [1.1–1.10c] is GREEN and 1.11 verified the four
     unverified systems. This story closes DG-1 / FR-53: the RTS command set is incomplete — Move/Stop/
     AttackMove exist, but single-target Attack, Patrol, Follow, and a TRUE Hold (today Hold === Stop) do not.
     The work is BROWNFIELD: additive sim slices (enum + SoA arrays, CombatSystem branch split,
     MovementSystem Hold anchor, both command-apply switches) + 1 presentation slice (SelectionSystem) + the
     SimChecksum re-baseline that DG stories 1.12/1.13 were always scheduled to do. Patrol is a MULTI-WAYPOINT
     shift-click route (Alec's call) — the largest single piece, size-flagged + splittable in Task 2/Decision #1.
     The single biggest HAZARD is the SimChecksum fold: it is a GLOBAL change that re-baselines EVERY golden and
     trips three version-pin guard tests on purpose — do it ONCE, last, exactly as documented in Task 6. -->

## Story

As a Commander issuing orders in a live match,
I want the complete RTS command set — single-target Attack, Patrol, and Follow — plus a Hold Position that truly holds ground instead of behaving like Stop,
so that I can micro units the way every RTS trains me to (force-fire a specific enemy, patrol a lane, escort a unit, anchor a chokepoint) without the orders silently collapsing into Move/Stop.

## Acceptance Criteria

**Verbatim from `epics.md` (Story 1.12, lines 788–810; covers DG-1 / FR-53, UX-DR66; depends on 1.8c):**

> 1. **Given** the current `UnitCommand:byte` enum (Idle=0, Move=1, AttackMove=2, Stop=3, HoldPosition=4, Build=5) **When** the new orders are added **Then** the enum gains `AttackTarget`, `Patrol`, and `Follow` as new byte values appended AFTER Build (existing values 0–5 unchanged for replay back-compat), each new per-entity field (e.g. forced-target id, patrol anchor/leg, follow leash) is a new parallel SoA array on EntityWorld sized to capacity, and a unit test asserts the three new behaviors each route to a distinct `CombatSystem.Tick*Combat` branch in the per-command switch (not the `default: // Idle` fall-through)
> 2. **Given** an idle friendly unit and a visible enemy unit **When** the Commander right-clicks the enemy (SelectionSystem right-click hit-tests to an enemy entity, not ground) **Then** the unit is issued `AttackTarget` with the clicked enemy's id stored in the forced-target SoA array, it path-moves to and force-attacks ONLY that target ignoring nearer enemies, and a Move command is still issued when the right-click hits ground or a friendly instead of an enemy
> 3. **Given** a unit on `AttackTarget` whose forced target dies or becomes invalid **When** the next CombatSystem tick runs **Then** the forced-target slot is cleared and the unit falls back to `Idle` (acquire-nearest) behavior rather than freezing, stuttering, or holding a dangling target id (error/edge path)
> 4. **Given** a unit issued `Patrol` between two or more waypoints (P key / patrol order) and `Follow` on a friendly target (F key) **When** the patrol unit meets an enemy en route **Then** it auto-engages like AttackMove, then resumes its waypoint loop and reverses at the final leg to return; and the `Follow` unit tracks its friendly target, re-pathing only when it exceeds a fixed leash distance and idling within it, dropping to `Idle` if the followed unit dies
> 5. **Given** units on `HoldPosition` versus `Stop` **When** an enemy enters attack range and when a friendly unit pushes into the holder's tile **Then** Hold attacks any in-range enemy but NEVER sets a MoveTarget toward it and is never displaced from its tile by collision/steering, whereas Stop retains its existing behavior — proving Hold no longer aliases Stop (CombatSystem currently shares the `case Stop: case HoldPosition:` branch)
> 6. **Given** all five orders (Move, AttackMove, Stop, HoldPosition + the three new) issued across both `LockstepManager` and `ReplayPlayer` command-apply switches **When** the pinned golden scenario exercises every order and the run is checksummed **Then** the new orders serialize/deserialize identically through both paths, all new SoA fields fold into `SimChecksum`, and the run reproduces byte-identically against the re-baselined golden checksum on a repeat run

### Decomposed, testable acceptance criteria

**AC1 — Enum extension + new SoA arrays + branch-routing test.**
- **AC1a:** `UnitCommand` gains `AttackTarget`, `Patrol`, `Follow`, and `PatrolAppend` appended AFTER `Build=5` (so `AttackTarget=6, Patrol=7, Follow=8, PatrolAppend=9`). Values `0–5` are UNCHANGED (a pre-1.12 `.chmr` replay byte still means exactly what it meant). `PatrolAppend` is a wire-only "add a waypoint to my patrol route" command — on apply it appends a waypoint then forces `CommandState=Patrol`, so `CombatSystem` only ever sees `Patrol` (never `PatrolAppend`). Update the enum doc comments, and fix the now-false `HoldPosition = 4, // Same as Stop (Phase 1)` comment (`EntityWorld.cs:16`).
- **AC1b:** Each genuinely-new per-entity field is a NEW parallel SoA array on `EntityWorld`, sized `MAX_ENTITIES` (the waypoint buffer is `MAX_ENTITIES * MAX_PATROL_WAYPOINTS`), with sentinels set in BOTH the ctor AND `Create()` (a recycled slot must never carry stale data). The set (see [SoA design](#soa-design-the-new-arrays-what-to-reuse)): `CommandTarget[]` (`int`, sentinel `-1` — the entity the command references: enemy id for AttackTarget, friendly id for Follow); and the **patrol-route ring** — `PatrolWaypoints[]` (`FixedVec3`, flat, indexed `id * MAX_PATROL_WAYPOINTS + k`), `PatrolCount[]` (`byte`, `0` = no route), `PatrolIndex[]` (`byte`, current-leg target index), `PatrolDir[]` (`sbyte`, `+1`/`-1` for the reverse-at-ends walk). `MAX_PATROL_WAYPOINTS` is a named constant (e.g. `8`). The follow leash is a `Fixed` CONSTANT in `CombatSystem`, not a per-entity array.
- **AC1c:** A unit test asserts AttackTarget, Patrol, and Follow each route to a DISTINCT `CombatSystem.Tick*Combat` branch — NOT the `default: // Idle` fall-through. Prove it behaviorally (see [branch-routing proof](#branch-routing-proof-ac1c)): with a nearer enemy present, AttackTarget chases the FORCED (farther) target, Patrol keeps moving its lane, and Follow tracks a FRIENDLY — none of which the Idle branch (global nearest-ENEMY chase) would do.

**AC2 — Right-click enemy → single-target AttackTarget; right-click ground/friendly → Move.**
The Play-mode right-click handler (`SelectionSystem._UnhandledInput`, `:218–226`) first hit-tests for an ENEMY unit under the cursor. Enemy hit → issue `AttackTarget` with that enemy's id stored in `CommandTarget[]`; the attacker path-moves to and force-attacks ONLY that target, **ignoring nearer enemies** (the distinguishing behavior). Ground or friendly hit → the existing `IssueMoveCommand` path (Move) is preserved unchanged. Asserted by a sim-level behavior test (force-fire ignores a nearer enemy) plus the presentation hit-test routing.

**AC3 — Forced target dies/invalid → clear slot, fall back to Idle (no freeze/stutter/dangle).**
When a unit on `AttackTarget` has a `CommandTarget` that is dead or out of valid id range, the next `CombatSystem` tick clears `CommandTarget[i] = -1`, sets `CommandState[i] = Idle`, and the unit resumes normal acquire-nearest behavior. No frozen unit, no per-tick stutter, no dangling id retained. Asserted: spawn attacker + forced target, kill the target, tick once, assert `CommandState==Idle && CommandTarget==-1` and the unit then auto-acquires a different in-range enemy.

**AC4 — Patrol loop (engage + resume + reverse) and Follow leash (track + idle + drop-on-death).**
- **AC4a (Patrol — multi-waypoint, shift-click route):** A `Patrol` unit walks an ORDERED route of waypoints, engaging enemies en route exactly like AttackMove (reuse that combat logic) and resuming toward the current waypoint after. On reaching the current waypoint it advances `PatrolIndex += PatrolDir`, **reversing at either end** (at the last waypoint `PatrolDir → -1`; at the first `→ +1`) — the AC's "reverses at the final leg to return". The route is built by input: a plain `Patrol` click starts a fresh 2-point route `[unit's current position, clicked point]` (so a no-shift patrol IS the classic ping-pong), and each subsequent **shift-click issues `PatrolAppend`**, appending a waypoint up to `MAX_PATROL_WAYPOINTS` (appends past the cap are silently ignored). Deterministic: arrival uses a `Fixed` squared-distance threshold; the index/dir arithmetic is pure integer. This is a strict generalization of the 2-point ping-pong (N=2 is the floor), so AC5's hold/stop proofs are unaffected.
- **AC4b (Follow):** A `Follow` unit reads `CommandTarget` (a friendly id). When the squared distance to the followed unit exceeds `FOLLOW_LEASH_SQR`, it sets `MoveTarget` to the followed unit's position and the `Moving` flag (re-path); within the leash it clears `Moving` and idles in place. If the followed unit dies/invalid → `CommandState = Idle`, `CommandTarget = -1`. Per AC, Follow does NOT auto-engage in 1.12 (tracking only) — see decisions.
- Asserted: a patrol unit with no enemies ping-pongs between two pinned waypoints across N ticks (and reverses); a patrol unit with an enemy on its lane deals damage then resumes; a follow unit tracks a moving friendly, idles when close, and drops to Idle when the friendly is destroyed.

**AC5 — Hold is genuinely distinct from Stop.**
- **AC5a (combat):** Split the shared `case Stop: case HoldPosition:` branch (`CombatSystem.cs:83–86`). Hold gets its own `TickHoldCombat`. Hold attacks any in-range enemy but NEVER sets a `MoveTarget` toward it (today's `TickStopCombat` already satisfies "attack-in-range, never chase, never set MoveTarget", so Hold's combat body may mirror it — the DISTINCTION is enforced by AC5b + a dedicated case label, not by inventing chase behavior).
- **AC5b (anchor — the real teeth):** A `HoldPosition` unit is NEVER displaced from its tile by separation/collision steering. Today `MovementSystem` applies separation to **ALL alive units** (`MovementSystem.cs:72–73,100–101`), so a Hold unit gets shoved. Add a Hold exemption: a `HoldPosition` unit's position is not mutated by the separation push (its neighbours still steer AROUND it). Stop is UNCHANGED — a Stop unit can still be pushed.
- Asserted: a Hold unit with a neighbour crowding its tile keeps `Position` byte-identical across ticks; an otherwise-identical Stop unit's `Position` changes — proving Hold no longer aliases Stop. Both still attack an in-range enemy.

**AC6 — Both apply paths + golden re-baseline + SimChecksum fold.**
- **AC6a (both switches):** The command-apply switches in BOTH `LockstepManager.ApplyOrders` (`:650–700`) AND `ReplayPlayer.ApplyOrders` (`:168–218`) gain cases for `AttackTarget`, `Patrol`, `Follow`, and `PatrolAppend` (HoldPosition is already handled by the Stop/Hold apply case — leave that). The two switches MUST stay in lock-step: an order handled in one but not the other desyncs replay-vs-live. A test asserts both produce identical post-apply world state for each new order (including a `Patrol`-then-`PatrolAppend` sequence).
- **AC6b (serialization round-trip):** The new orders serialize/deserialize identically through both wire paths with NO format change — `AttackTarget`/`Follow` pack the target ENTITY id into the existing `UnitOrder.TargetX` int via `Fixed.FromRaw(id)` (read back as `o.TargetX` directly, never `.ToFloat()`); `Patrol`/`PatrolAppend` use `TargetX/TargetZ` as a ground point like Move. A round-trip test through `TickCommandPacket.Write`→`TryRead` (live) and `ReplayRecorder`→`ReplayPlayer` (file) reproduces the order. `ReplayRecorder.VERSION` is NOT bumped (the 11-byte format is unchanged; the new enum values just become valid command bytes).
- **AC6c (SimChecksum fold + re-baseline):** The new SoA fields (`CommandTarget` + the patrol-route ring `PatrolWaypoints`/`PatrolCount`/`PatrolIndex`/`PatrolDir`) fold into `SimChecksum.Compute` (entity loop, count-driven for the waypoints), `SimChecksum.AlgoVersion` bumps `3 → 4`, the three version-pin guards are updated (see [the re-baseline surface](#the-simchecksum-re-baseline-surface-task-6--do-this-once-last)), and ALL existing goldens + the NEW 1.12 golden are re-recorded via the `CHIMERA_GOLDEN_RECORD` flow. A differential test proves the new fields actually move the hash (mutate one → checksum changes).
- **AC6d (the 1.12 golden):** A new `CommandVocabularyScenario` exercises every order (Move, AttackMove, Stop, HoldPosition, AttackTarget, Patrol, Follow) and is pinned as a golden; two in-process runs are byte-identical and reproduce the committed golden. Because all new fields are integer/`Fixed`-only, this golden IS cross-platform-safe and MAY join the WSL gate (unlike the AI-active golden).

_Covers: **DG-1 / FR-53** (full RTS command vocabulary), **UX-DR66** (default keybindings: P Patrol, H Hold, S Stop, Q Attack-Move; right-click = single-target attack). Depends on: **1.8c** (DONE — the Godot-free `SimulationHost` + 9-system tick order this builds on)._

---

## SCOPE — read this before coding

### ✅ IN scope (this story)
1. **Enum + SoA storage** — append `AttackTarget`/`Patrol`/`Follow`/`PatrolAppend` after `Build`; add `CommandTarget[]` + the patrol-route ring (`PatrolWaypoints`/`PatrolCount`/`PatrolIndex`/`PatrolDir`) SoA arrays + `MAX_PATROL_WAYPOINTS` const, with sentinels in ctor + `Create()`.
2. **CombatSystem behavior** — split Hold from Stop; add `TickAttackTargetCombat`, `TickPatrolCombat`, `TickFollowCombat`, `TickHoldCombat`; extend the gatherer-normalization guard to the new combat commands.
3. **MovementSystem** — Hold-position anchor exemption (the only sim-behavior change to an existing system besides Combat).
4. **Both command-apply switches** — `LockstepManager.ApplyOrders` + `ReplayPlayer.ApplyOrders` gain the three new cases (kept identical).
5. **SelectionSystem (presentation)** — right-click enemy hit-test → AttackTarget; `P` → Patrol; `F` → Follow; an enemy-find helper alongside the existing Player1-only `FindNearestUnit`.
6. **SimChecksum fold + AlgoVersion 3→4 + re-baseline ALL goldens** + the new 1.12 `CommandVocabularyScenario` golden.
7. **Tests** — branch-routing (AC1c), force-fire-ignores-nearer (AC2), dead-target fallback (AC3), patrol loop + follow leash (AC4), hold-vs-stop anchor (AC5), both-paths-agree + serialization round-trip (AC6a/b), checksum-fold differential (AC6c), the golden (AC6d).

### ❌ OUT of scope (do NOT do these here)
- **NO formation / group-move changes.** The flat `ceil(sqrt(N))` grid in `IssueMoveCommand`/`IssueAttackMoveCommand` (`SelectionSystem.cs:332–374, 419–458`) and the symmetric separation in `MovementSystem.cs:72–89` are **Story 1.13's** job (DG-2 / FR-54). 1.12 touches MovementSystem ONLY for the Hold anchor — do NOT add the moving-vs-idle separation bias, `collision_radius`, or role-based formations here. (1.13 also re-baselines the golden again — that is expected; the two DG stories were scheduled as back-to-back checksum bumps.)
- **NO rally points, and NO general shift-queue of arbitrary commands** (shift-Move / shift-Attack chains across command TYPES, queued per-unit). Shift-click is supported for PATROL ROUTES ONLY (appending patrol waypoints via `PatrolAppend`) — a full per-unit command queue for any order type is a separate, larger feature, explicitly deferred. Multi-waypoint patrol uses a FIXED-CAP flat SoA ring (`MAX_PATROL_WAYPOINTS`), never a dynamic per-unit list (which would break SoA/determinism).
- **NO ground-target Attack-Move rebuild.** AttackMove already exists and works (`TickAttackMoveCombat`, `IssueAttackMoveCommand`). VERIFY it still works; do NOT rewrite it. This story is single-TARGET orders + Hold semantics only.
- **Do NOT change `UnitCommand` values 0–5, the `UnitOrder` 11-byte wire format, `ReplayRecorder.VERSION` (=2), or `CanonicalModelHash` (=2).** Only `SimChecksum.AlgoVersion` (3→4) changes. The start-state/lobby hash is scenario-content, not runtime command state — leave it.
- **Do NOT add a new system to the 9-system tick order.** New behavior lives inside the EXISTING `CombatSystem` and `MovementSystem`. `SystemOrderTest` must stay green untouched.
- **Do NOT call a real LLM, add a NuGet `PackageReference`, or touch the existing CI gate jobs.** The `DependencyHygieneTests` one-package guard + `--locked-mode` restore stay green.
- **Do NOT leave float in sim.** All distance/leash/range math uses `Fixed` (16.16); patrol index/dir arithmetic is pure integer. The packed target id rides in `TargetX` as a raw int via `Fixed.FromRaw(id)` / read as `o.TargetX` — NEVER `Fixed.FromFloat`/`.ToFloat()` for an entity id (that path is float and would corrupt the id and break determinism).
- **Do NOT "fix" a red golden by hand-editing a `.golden.txt`.** Re-record via `CHIMERA_GOLDEN_RECORD=1` exactly as Task 6 describes. A red golden that is NOT explained by your intended SimChecksum/behavior change is a finding to investigate, not a license to overwrite.

### Brownfield reality (what exists vs what to build)
| Area | As-built (VERIFY, don't regress) | BUILD in 1.12 |
|---|---|---|
| `UnitCommand` enum | Idle/Move/AttackMove/Stop/HoldPosition/Build (`EntityWorld.cs:10–18`) | Append AttackTarget/Patrol/Follow/PatrolAppend (6/7/8/9) |
| EntityWorld SoA | `AttackTarget[]` (transient live target, `-1`), `CommandGoal[]`, `MoveTarget[]`, `Flags` (`:81–145`) | NEW `CommandTarget[]` + patrol-route ring (`PatrolWaypoints`/`Count`/`Index`/`Dir`) |
| CombatSystem | `TickIdleCombat`/`TickStopCombat`/`TickAttackMoveCombat`; **Stop & Hold SHARE one branch** (`:83–86`); gatherer cmds normalized to Idle (`:68–75`) | Split Hold; +3 new Tick*Combat; extend normalization |
| MovementSystem | separation on ALL units (`:72–73`), position mutated `:101` | Hold anchor exemption |
| Apply switches | `LockstepManager.ApplyOrders:650–700` + `ReplayPlayer.ApplyOrders:168–218` handle Move/AttackMove/Stop/Hold | +3 new cases in BOTH, identical |
| Wire format | `UnitOrder` = unitId(2)+cmd(1)+tx(4)+tz(4) = 11 bytes (`NetworkCommand.cs:63–91`) | REUSE — pack target id in `TargetX` |
| SelectionSystem | right-click → `IssueMoveCommand` (ground only); `FindNearestUnit` = Player1 only (`:495–512`) | enemy hit-test → AttackTarget; P/F keys; enemy-find helper |
| SimChecksum | v3: Position+Health+buildings+resources+RNG (`SimChecksum.cs`); **does NOT hash any command field** | Fold `CommandTarget` + patrol-route ring; AlgoVersion 3→4 |

---

## Tasks / Subtasks

- [ ] **Task 1 — Enum extension + new SoA arrays (AC: 1a, 1b).**
  - [ ] In `godot/src/Core/EntityWorld.cs`, append to `UnitCommand` (after `Build = 5`): `AttackTarget = 6`, `Patrol = 7`, `Follow = 8`, `PatrolAppend = 9` — each with a one-line doc comment (`PatrolAppend` = wire-only "add a waypoint to the patrol route", rewritten to `Patrol` on apply — `CombatSystem` never sees it). Do NOT renumber 0–5. Fix the stale `HoldPosition = 4, // Same as Stop (Phase 1)` comment to describe the new true-hold semantics.
  - [ ] Add `public const int MAX_PATROL_WAYPOINTS = 8;` (named — no bare cap literal; the analyzer's `CHM0004` flags bare caps) and the SoA arrays: `public readonly int[] CommandTarget;` (entity id this command references — enemy for AttackTarget, friendly for Follow; `-1` = none); and the patrol-route ring — `PatrolWaypoints` (`FixedVec3[MAX_ENTITIES * MAX_PATROL_WAYPOINTS]`), `PatrolCount` (`byte[MAX_ENTITIES]`), `PatrolIndex` (`byte[MAX_ENTITIES]`), `PatrolDir` (`sbyte[MAX_ENTITIES]`). Allocate all in the ctor; add `CommandTarget` to the `Array.Fill(..., -1)` sentinel block (`:197–199`); reset ALL in `Create()` (`CommandTarget[id] = -1; PatrolCount[id] = 0; PatrolIndex[id] = 0; PatrolDir[id] = 1;` — `PatrolWaypoints` slots need no reset because `PatrolCount=0` makes them unread until written). **Skipping the `Create()` reset is the classic SoA bug — a recycled slot carries the previous unit's route → nondeterministic ghost behavior.**
  - [ ] Document the reuse vs new decision in code comments: `AttackTarget[]` stays the TRANSIENT live target (set each tick by the spatial-hash query); `CommandTarget[]` is the PERSISTENT player-issued forced/follow target; `CommandGoal[]`/`MoveTarget[]` remain the live move destination (patrol drives `MoveTarget` from its current waypoint each leg).

- [ ] **Task 2 — CombatSystem: split Hold from Stop + three new branches (AC: 1c, 3, 4a, 4b, 5a).**
  - [ ] In `godot/src/Combat/CombatSystem.cs`, change the per-command `switch` (`:78–95`): keep `case Move: continue;`, keep `case Stop: → TickStopCombat`, keep `case AttackMove: → TickAttackMoveCombat`, ADD `case HoldPosition: → TickHoldCombat`, `case UnitCommand.AttackTarget: → TickAttackTargetCombat`, `case UnitCommand.Patrol: → TickPatrolCombat`, `case UnitCommand.Follow: → TickFollowCombat`. Leave `default: → TickIdleCombat` (now catches only Idle + Build).
  - [ ] `TickHoldCombat` (AC5a): mirror `TickStopCombat` (attack in range, never set MoveTarget, never chase). Its distinctness is the dedicated case + the MovementSystem anchor (Task 3) — do NOT add chase logic.
  - [ ] `TickAttackTargetCombat` (AC2, AC3): read `world.CommandTarget[i]`; if dead/invalid (`!IsAlive` or `< 0`) → clear `CommandTarget[i]=-1`, `CommandState[i]=Idle`, then fall through to `TickIdleCombat` (acquire-nearest) — NO freeze/stutter/dangling (AC3). If valid: set `AttackTarget[i]=CommandTarget[i]`; if in range → face/attack via the existing `TryDealDamage`; if out of range → set `MoveTarget[i]=Position[forced]` + `Moving` flag (chase ONLY the forced target — ignore nearer enemies; this is the AC2 distinction). Reuse `TickCooldown`, `TryDealDamage`, range-squared math.
  - [ ] `TickPatrolCombat` (AC4a): same combat body as `TickAttackMoveCombat` (engage in-range enemies, then resume toward the current waypoint `PatrolWaypoints[i*MAX_PATROL_WAYPOINTS + PatrolIndex[i]]`). On arrival (reuse the shared `AMOVE_ARRIVE_SQR` test), advance the route: `PatrolIndex[i] += PatrolDir[i]`; if it would reach the last index (`>= PatrolCount[i]-1`) flip `PatrolDir[i] = -1`, if it would reach `0` flip `PatrolDir[i] = +1` (reverse-at-ends); then set `MoveTarget[i]` to the new current waypoint + the `Moving` flag. Guard the degenerate `PatrolCount[i] <= 1` (just hold in place — no route). Factor the arrival test so AttackMove and Patrol share it. Determinism: index/dir arithmetic is pure integer; threshold is `Fixed`. (N=2 reduces exactly to the classic A↔B ping-pong.)
  - [ ] `TickFollowCombat` (AC4b): read `world.CommandTarget[i]` (friendly id); if dead/invalid → `CommandState[i]=Idle`, `CommandTarget[i]=-1`. Else compute squared distance to the followed unit; if `> FOLLOW_LEASH_SQR` → `MoveTarget[i]=Position[followed]`, set `Moving`; else clear `Moving` (idle in place). Add `private static readonly Fixed FOLLOW_LEASH` (+ its square) as a named constant (document the value; NO bare literal — pick a sensible default like 3.0u and comment it). Follow does NOT auto-acquire/attack in 1.12 (per AC; see decisions).
  - [ ] Extend the gatherer-normalization guard (`:68–75`): a gatherer issued `AttackTarget`/`Patrol`/`Follow`/`PatrolAppend` must also normalize to `Idle` (same invariant as the existing AttackMove/Stop/Hold normalization — a gatherer must never sit in a combat command no system completes).
  - [ ] **EDGE — the `AttackDamage == 0` early-`continue` (`:76`):** it sits BEFORE the command switch, so a zero-damage non-gatherer would be skipped entirely — meaning it could not Patrol/Follow (both are partly movement, not pure combat). For 1.12 this is acceptable (every order is issued to combat units; the golden + tests use damage-bearing units), so keep the guard as-is and note the limitation in a comment. Do NOT hoist the movement branches above the guard in this story — supporting zero-damage units patrolling/following is an Epic-2 (support/ability units) concern. Flag it, don't build it.
  - [ ] Update the class-level XML summary (`:7–19`) to document the four new/changed command behaviors.

- [ ] **Task 3 — MovementSystem: Hold-position anchor (AC: 5b).**
  - [ ] In `godot/src/Navigation/MovementSystem.cs`, exempt `HoldPosition` units from separation displacement: at the top of the per-unit loop (after the `Alive` check, `:41`), if `world.CommandState[i] == UnitCommand.HoldPosition`, zero `world.Velocity[i]` and `continue` (no seek, no separation, no position mutation). Neighbours computing THEIR separation still see the Hold unit (it remains in the spatial hash) and steer around it — only the Hold unit's own position is anchored. Add a `using ProjectChimera.Core;` reference if not already present (it is). Comment WHY (AC5b / DG-1).
  - [ ] Confirm Stop is UNCHANGED — a `Stop` unit still falls through to the separation push (it can be displaced). Do NOT anchor Stop.

- [ ] **Task 4 — Both command-apply switches + serialization (AC: 6a, 6b).**
  - [ ] In `godot/src/Multiplayer/LockstepManager.cs` `ApplyOrders` (`:650–700`), add cases:
    - `case UnitCommand.AttackTarget:` → `world.CommandTarget[id] = o.TargetX;` (raw int id — NOT `Fixed.FromRaw(...).ToFloat()`), `world.AttackTarget[id] = o.TargetX;`, clear `Attacking`, leave CombatSystem to drive movement (do NOT call `OnRequestPath` — the target moves; CombatSystem sets MoveTarget each tick like Idle-chase).
    - `case UnitCommand.Patrol:` → START a fresh route (`base = id * EntityWorld.MAX_PATROL_WAYPOINTS`): `PatrolWaypoints[base+0] = world.Position[id]` (current pos = the return anchor), `PatrolWaypoints[base+1] = (Fixed.FromRaw(o.TargetX), 0, Fixed.FromRaw(o.TargetZ))` (clicked point), `PatrolCount[id]=2`, `PatrolIndex[id]=1`, `PatrolDir[id]=+1`; set `CommandGoal[id]`/`MoveTarget[id]` to the clicked point, set `Moving`, clear `Attacking`. Optionally call `OnRequestAttackMove` for the fixed first leg.
    - `case UnitCommand.PatrolAppend:` → APPEND to the existing route: if the unit is already patrolling and `PatrolCount[id] < EntityWorld.MAX_PATROL_WAYPOINTS`, write `PatrolWaypoints[base + PatrolCount[id]] = (clicked point); PatrolCount[id]++` (do NOT touch the current `PatrolIndex`/`MoveTarget` — the new waypoint just extends the far end). If the unit was NOT already patrolling, treat it exactly like a fresh `Patrol`. **Then force `world.CommandState[id] = UnitCommand.Patrol;`** (overriding the pre-switch `= o.Command`), so the unit's state is `Patrol` and `CombatSystem` never sees `PatrolAppend`. Appends past the cap are silently ignored (deterministic no-op).
    - `case UnitCommand.Follow:` → `world.CommandTarget[id] = o.TargetX;`, clear `Attacking` (CombatSystem's follow tick drives movement each tick).
    - Note: `_world.CommandState[id] = o.Command;` is already set BEFORE the switch (`:659`), so each new case only sets the EXTRA payload — EXCEPT `PatrolAppend`, which must override `CommandState` back to `Patrol` (above).
  - [ ] Mirror the SAME three cases EXACTLY in `godot/src/Multiplayer/ReplayPlayer.cs` `ApplyOrders` (`:168–218`). **A case added to one switch but not the other is a guaranteed replay-vs-live desync — this is the #1 trap of this story (the epic note calls it out).**
  - [ ] Verify the wire format needs NO change: `AttackTarget`/`Follow` pack the target id into `UnitOrder.TargetX` via `Fixed.FromRaw(id)` at issue time (Task 5) and read it back as `o.TargetX` at apply time. `Patrol` uses `TargetX/TargetZ` as a `Fixed.Raw` ground point. `UnitOrder.SIZE` (11) and `ReplayRecorder.VERSION` (2) are unchanged.
  - [ ] Add `godot/ProjectChimera.Sim.Tests/Multiplayer/CommandApplyParityTests.cs`: for each new order, build two identical worlds, apply the same `UnitOrder[]` through `LockstepManager.ApplyOrders` vs `ReplayPlayer.ApplyOrders` (or assert the apply effect matches a hand-computed expected state), and assert the resulting SoA state is identical. Plus a `TickCommandPacket.Write`→`TryRead` round-trip and a `ReplayRecorder`→`ReplayPlayer` file round-trip preserving the new command + packed target (AC6b). (If `LockstepManager.ApplyOrders`/`ReplayPlayer.ApplyOrders` are `private`, test through the public apply surface — `ReplayPlayer.Flush` after loading a crafted in-memory replay — or via `[InternalsVisibleTo]`/an internal test seam mirroring how 1.11 reached `DelayMath`.)

- [ ] **Task 5 — SelectionSystem presentation: right-click enemy / P / F (AC: 2, 4 issue-half).**
  - [ ] In `godot/src/UI/SelectionSystem.cs` `_UnhandledInput` right-click block (`:218–226`): before `IssueMoveCommand`, raycast the cursor and hit-test for an ENEMY unit (new helper `FindNearestEnemyUnit(hit, radius)` — like `FindNearestUnit` `:495–512` but `FactionOf[i]` is an enemy of `Player1`, i.e. not `Player1` and not `Neutral`). Enemy hit → `IssueAttackTargetCommand(enemyId)`; otherwise → existing `IssueMoveCommand` (ground/friendly → Move, AC2 fallback).
  - [ ] Add `IssueAttackTargetCommand(int enemyId)`: for each selected unit, `_lockstep?.EnqueueOrder(unitId, UnitCommand.AttackTarget, Fixed.FromRaw(enemyId), Fixed.Zero) ?? true`; in offline mode (returns true) apply directly to the world (`CommandState=AttackTarget`, `CommandTarget=enemyId`, `AttackTarget=enemyId`, clear flags). Mirror the offline/online split already in `IssueStopCommand`/`IssueMoveCommand`. **Pack the id via `Fixed.FromRaw(enemyId)` — never `Fixed.FromFloat`.**
  - [ ] Add `IssuePatrolCommand(Vector2 screenPos, bool append)` (mirror `IssueAttackMoveCommand` but enqueue `append ? UnitCommand.PatrolAppend : UnitCommand.Patrol`; single destination — NO formation grid here, that's 1.13). Bind `P` to arm a patrol placement (mirror `_awaitingAttackMoveClick` `:247–251`): the FIRST armed click issues `Patrol`; while the player holds **Shift** on a subsequent click, issue `PatrolAppend` and keep the placement armed (so `P, click, shift-click, shift-click…` builds a multi-waypoint route); a plain (non-shift) click disarms. Read `InputEventMouseButton.ShiftPressed` (the click event) for the append flag — pass it as `append`.
  - [ ] Add `IssueFollowCommand(int friendlyId)` (pack id like AttackTarget) bound to `F`: when `F` is pressed, set an `_awaitingFollowClick` flag (mirror `_awaitingAttackMoveClick` `:247–251`); the next left-click hit-tests for a FRIENDLY unit → `IssueFollowCommand`. (Reuse the existing left-click-consume pattern.)
  - [ ] Presentation only — NO Godot types leak into sim; NO sim-state mutation beyond the offline-apply path that already exists. Optional: patrol/attack/follow cursor feedback is nice-to-have, not required.

- [ ] **Task 6 — SimChecksum fold + AlgoVersion bump + re-baseline ALL goldens + new 1.12 golden (AC: 6c, 6d). ⚠ GLOBAL change — do this ONCE, LAST, after Tasks 1–5 are behavior-complete.**
  - [ ] In `godot/src/Core/SimChecksum.cs`: in the entity loop (`:53–62`, after `Health`), fold the new fields — `hash = Mix(hash, world.CommandTarget[i]);`, then the patrol route: `Mix(world.PatrolCount[i])`, `Mix(world.PatrolIndex[i])`, `Mix(world.PatrolDir[i])`, and for `k` in `0..PatrolCount[i]` the three `PatrolWaypoints[i*MAX_PATROL_WAYPOINTS + k].{X,Y,Z}.Raw` mixes (count-driven + ascending → deterministic; all `int`/`Fixed.Raw` → cross-platform safe). Bump `AlgoVersion` `3 → 4` (`:37`) and add a `v4 — Story 1.12: fold CommandTarget + patrol-route ring (full command vocabulary)` line to the version doc.
  - [ ] Update the three version-pin guards (they break ON PURPOSE — that is their job):
    - `godot/ProjectChimera.Sim.Tests/Meta/VersionStampConsistencyTests.cs:48` — `ExpectedSimChecksumAlgoVersion = 3 → 4`. (Leave `ExpectedCanonicalModelHashAlgoVersion = 2` and the ReplayRecorder version pin.)
    - `godot/ProjectChimera.Sim.Tests/Golden/SimChecksumCoverageGuardTest.cs` — `Assert.Equal(3, ...)` → `Assert.Equal(4, ...)` (`:97`), rename `KnownWorldState_ProducesPinnedV3Hash` → `…V4Hash`, and re-pin `ExpectedV3Hash` (`:103`) to the new constant the failing assertion prints. (The known-state world doesn't set the new arrays, so they fold at default `-1`/`Zero` — the hash still moves because new mixes are added.)
    - Add a differential assertion (extend this guard or a new test): mutating `CommandTarget`, or a `PatrolWaypoints` slot on a unit with `PatrolCount>0`, on a live entity MUST change `SimChecksum.Compute` — proving the new fields are actually folded (the EntityWorld analogue of the existing ResourceStore coverage guard, which only reflects `ResourceStore`).
  - [ ] Re-record ALL existing goldens via `CHIMERA_GOLDEN_RECORD=1` (their behavior is unchanged; only the algo adds default-valued fields): `golden-scenario`, `golden-multifaction`, `golden-applier-scenario`, `same-tick-tie-break`, `ai-active-scenario`. Use each golden's OWN test filter (e.g. `…~AiActive`, `…~GoldenChecksumReplay`) — running ALL Golden tests in record mode rewrites everything, which is acceptable HERE (this is the sanctioned re-baseline story) but confirm each header's `checksum_algo_version` line now reads `4`. Then `dotnet build` (refreshes embedded copies) and confirm normal-mode tests pass.
  - [ ] Add `godot/ProjectChimera.Sim.Tests/Golden/CommandVocabularyScenario.cs` (mirror `GoldenScenario.cs`/`AiActiveScenario.cs`): an in-code, all-`Fixed` scenario that issues and exercises EVERY order — at least one unit each on Move, AttackMove, Stop, HoldPosition, AttackTarget (forced onto a specific enemy with a nearer decoy present, to pin force-fire), Patrol (a **3-waypoint route** with an enemy on the lane, so the reverse-at-ends walk + the append path + en-route engagement are all exercised), Follow (a friendly that moves). Drive via `SimulationHost.Create(NullLogSink.Instance, new FactionRegistry(2), …)`, `ChecksumInterval=1`, `DefaultTicks=300`. Author all command state directly (set the SoA fields the apply switches would, e.g. `PatrolCount/Index/Dir` + the waypoint slots) — no Godot, no LLM.
  - [ ] Add `godot/ProjectChimera.Sim.Tests/Golden/CommandVocabularyGoldenTests.cs` (mirror `AiActiveGoldenTests.cs`): AC6d two-run byte-identical + committed-golden match + a record hook + a non-vacuity assertion (the sequence EVOLVES and exercises ≥1 of each order). Add a `GoldenHeader` whose re-baseline hint names THIS filter. **Unlike the AI-active golden, this one is integer/`Fixed`-only → it is NOT Windows-gated and MAY join the cross-platform gate** (add it to `godot/tools/cross-platform-determinism-check.ps1` only if that is the simplest way to keep coverage — otherwise just leave it in the normal Tier-1 set, which both CI legs run).
  - [ ] Register `command-vocabulary-scenario.golden.txt` as an embedded resource in `godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` (the `<None Remove>` + `<EmbeddedResource Include>` pair, beside the existing five at `:27–44`). LF-only (the 1.10c `CrossPlatformGoldenGuardTests` enforces it — `MaybeRecord` writes UTF-8-no-BOM `\n`).

- [ ] **Task 7 — Behavior unit tests (AC: 1c, 2, 3, 4, 5).**
  - [ ] Add `godot/ProjectChimera.Sim.Tests/Combat/CommandVocabularyTests.cs` (new `Combat/` folder, mirroring the `Multiplayer/`/`Validation/` convention). Build a small `EntityWorld` + a `CombatSystem` (and a `MovementSystem` where needed) directly — no Godot:
    - **AC1c branch routing:** AttackTarget unit + a NEARER decoy enemy + the farther forced target → after a tick it moves toward the FORCED target (not the decoy); Patrol unit with NO enemies → keeps moving its lane (not idle-chasing); Follow unit + a friendly → tracks the friendly (Idle would chase the nearest enemy). Each proves "not the default Idle branch".
    - **AC2 force-fire:** AttackTarget unit with a nearer enemy in range → it damages ONLY the forced target, never the nearer one.
    - **AC3 dead-target fallback:** kill the forced target, tick once → `CommandState==Idle && CommandTarget==-1`, and the unit then auto-acquires a different in-range enemy (no freeze/stutter/dangle).
    - **AC4a patrol:** a 3-waypoint route, no enemies, step N ticks → the unit walks W0→W1→W2 then REVERSES (W2→W1→W0) and bounces (assert the `PatrolIndex`/`PatrolDir` sequence + that it heads back toward W0 after W2); a `PatrolAppend` mid-route extends the far end WITHOUT resetting the current leg; with an enemy on the lane → it deals damage then resumes. (N=2 is the degenerate ping-pong floor — also assert it.)
    - **AC4b follow:** a friendly that steps away each tick → the follower re-paths beyond leash and idles (clears `Moving`) within leash; destroy the friendly → `CommandState==Idle`.
    - **AC5 hold-vs-stop:** a Hold unit + a crowding neighbour through `MovementSystem.Tick` → `Position` byte-identical across ticks; an identical Stop unit → `Position` changes. Both attack an in-range enemy.
  - [ ] All test scenarios authored in `Fixed` (no `Fixed.FromFloat`), ascending-id, no wall-clock — they run on every OS including the WSL leg.

- [ ] **Task 8 — Regression, code review, sprint status.**
  - [ ] Run the full Windows CI command `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release` → green (was **239** at 1.11 close; **do not hardcode** — rely on exit code). Confirm: the 5 existing goldens re-recorded to `checksum_algo_version: 4`, the new 1.12 golden added, all three version-pin guards green at v4, `SystemOrderTest` untouched/green, the analyzer gate green (advisory on master).
  - [ ] Confirm the cross-platform integer-safety claim: the new SoA fields are `int`/`Fixed.Raw` only (no float in the hashed path), so the re-recorded goldens stay Win↔Linux byte-identical. If feasible, run the WSL `cross-platform-determinism-check` (1.10c) to confirm the re-baselined goldens still match across platforms.
  - [ ] Run `gds-code-review` (3-layer adversarial, fresh-context / different LLM). On PASS, set this story `done` in `sprint-status.yaml` and update `last_updated`. (This workflow only sets `review`; code-review flips `done`.)

---

## Dev Notes

### Developer context — why this story exists and the one framing that makes it tractable
DG-1 (readiness triage) found the shipped command set incomplete: Move/Stop/AttackMove are built and golden-pinned, but **single-target Attack, Patrol, and Follow don't exist, and Hold is a lie** — the enum comment literally says `HoldPosition = 4, // Same as Stop (Phase 1)` and `CombatSystem` routes both through one `case Stop: case HoldPosition:` branch. This is the FIRST post-M1 design-gap story; M1's determinism floor is exactly what makes it safe to land a SimChecksum-widening change now — the harness will catch any nondeterminism you introduce against a trustworthy baseline.

**The framing that makes this tractable:** there is a clean **sim ↔ presentation split** and a clean **storage ↔ behavior split**.
- **Storage (Task 1):** one enum extension + two SoA arrays. Everything else hangs off these.
- **Sim behavior (Tasks 2–4):** `CombatSystem` (the four command behaviors), `MovementSystem` (the one Hold-anchor line), and the two apply switches (the wire→world step). All pure `Fixed`, ascending-id, Godot-free, Tier-1-testable.
- **Presentation (Task 5):** `SelectionSystem` turns mouse/keys into `EnqueueOrder` calls. It is the ONLY Godot-coupled file you touch and it never owns sim truth.
- **The checksum (Task 6):** the deliberate, scheduled re-baseline. Treat it as a mechanical, well-trodden procedure (1.3b and 1.5 did exactly this).

The two real traps: (1) updating ONE apply switch and not the other (silent replay desync), and (2) the SimChecksum bump breaking three guard tests — both are fully spelled out below.

### The movement truth model (read before building AttackTarget/Patrol/Follow movement)
The sim's movement truth is **`MoveTarget` + the `Moving` flag**, consumed by `MovementSystem` (seek+arrive, direct steering). `OnRequestPath`/`OnRequestAttackMove` are PRESENTATION path-smoothing delegates wired to `FlowFieldBridge` in MainScene — **they are `null` in the Tier-1 golden harness.** So every new order's movement MUST work via `MoveTarget` direct steering (that is what the golden proves). The existing **Idle global-chase** already does this (`CombatSystem.cs:112–118`: sets `MoveTarget = Position[enemy]`, sets `Moving`) — model AttackTarget/Follow chasing on it: CombatSystem updates `MoveTarget` toward the (moving) target EACH tick. Patrol moves toward a FIXED waypoint, so it may optionally take a one-shot path request, but direct steering is sufficient and is what the golden exercises.

### SoA design — the new arrays, what to reuse
| Field | Type / sentinel | Role | Hashed? |
|---|---|---|---|
| `CommandTarget[]` (NEW) | `int`, `-1` | Persistent player-issued target: enemy id (AttackTarget) or friendly id (Follow). Mutually exclusive states → one array serves both. | YES (fold in Task 6) |
| `PatrolWaypoints[]` (NEW) | `FixedVec3`, flat ×`MAX_PATROL_WAYPOINTS` | The ordered patrol route, indexed `id*CAP + k`. Fixed-capacity ring — the SoA-safe way to store a variable-length route (no dynamic list). | YES (count-driven mixes) |
| `PatrolCount/Index/Dir[]` (NEW) | `byte`/`byte`/`sbyte` | Route length, current-leg index, walk direction (`+1`/`-1`, reverse-at-ends). | YES |
| `AttackTarget[]` (reuse) | `int`, `-1` | TRANSIENT live combat target, recomputed each tick by the spatial hash. AttackTarget-command seeds it from `CommandTarget`. | No (derived, as today) |
| `CommandGoal[]` / `MoveTarget[]` (reuse) | `FixedVec3` | Live Move/AttackMove destination + the current patrol-leg target. | No (as today) |
| `FOLLOW_LEASH` (const) | `Fixed` in CombatSystem | Re-path threshold for Follow. Not per-entity → a named constant, not an array. | No |

Why one `CommandTarget` for both AttackTarget and Follow: a unit is in exactly one `CommandState` at a time, so the enemy-id and friendly-id uses never overlap — one `int` array serves both. The patrol route is a FIXED-CAPACITY flat ring (`PatrolWaypoints` + count/index/dir), NOT a per-unit dynamic list — a fixed cap keeps it in the SoA model, deterministic, and cheap to hash. `MAX_PATROL_WAYPOINTS=8` costs ~`4096×8×12B ≈ 384 KB` for the buffer (negligible); tune the cap if you want.

### Branch-routing proof (AC1c)
The `default:` arm is `TickIdleCombat`, whose signature behavior is **global nearest-ENEMY chase** (`FindNearestEnemyGlobal` → `MoveTarget`). So each new branch is "distinct from Idle" iff its behavior diverges from that:
- **AttackTarget** chases the FORCED target even when a nearer enemy exists (Idle would chase the nearer one).
- **Patrol** moves toward its waypoint with no enemy present (Idle would idle/global-chase the nearest enemy, not patrol).
- **Follow** tracks a FRIENDLY (Idle never targets friendlies).
Assert these behavioral divergences — you do not need to reflect on the private methods.

### The SimChecksum re-baseline surface (Task 6 — do this ONCE, last)
`SimChecksum.Compute` currently hashes Position+Health (entities), buildings, resources, and RNG state — it hashes **no command field today**. Folding `CommandTarget` + the patrol-route ring is a GLOBAL algo change: it moves the hash for EVERY scenario (even ones not using the new orders, because new mixes are added at default values). That is why the bump re-bases all five existing goldens and trips three guards. The full surface (nothing else hashes `SimChecksum.AlgoVersion`):
1. `src/Core/SimChecksum.cs:37` — `AlgoVersion 3 → 4` + doc line.
2. `…/Meta/VersionStampConsistencyTests.cs:48` — `ExpectedSimChecksumAlgoVersion 3 → 4`.
3. `…/Golden/SimChecksumCoverageGuardTest.cs:97,103` — `Assert.Equal(4, …)` + re-pin the known-state constant (rename V3→V4) + add the EntityWorld differential coverage check.
4. Re-record 5 goldens (`golden-scenario`, `golden-multifaction`, `golden-applier-scenario`, `same-tick-tie-break`, `ai-active-scenario`) → headers flip to `checksum_algo_version: 4`.
5. Add the new `command-vocabulary-scenario.golden.txt` (+ csproj embed).

LEAVE untouched: `CanonicalModelHash.AlgoVersion` (=2; that's the lobby start-state/scenario-content hash, not runtime command state) and `ReplayRecorder.VERSION` (=2; the 11-byte wire format is unchanged). `SystemOrderTest` (no system added). The 1.10c LF-only / cross-platform golden guards (re-records via `MaybeRecord` stay LF-only by construction).

### Architecture compliance
- **Determinism law (NFR-4 / project-context):** all new distance/leash/range/swap math in `Fixed` (16.16); ascending-entity-id iteration (the existing loops already do this — keep it); no `float`/`double`/`Mathf`/`Math.*` in sim; no wall-clock; no `Dictionary`/`HashSet` enumeration driving sim order; the only RNG is `SimRng` (you likely need NONE — target selection is lowest-id/deterministic, and there is no tie-break that requires a draw; if one arises, collect candidates ascending-id THEN draw, per AR-15). The packed target id rides as a raw `int` (`Fixed.FromRaw`/`o.TargetX`) — it never round-trips through float.
- **Sim ↔ Presentation boundary is sacred:** `CombatSystem`, `MovementSystem`, `EntityWorld`, the apply switches, `SimChecksum` are sim (`src/Core`, `src/Combat`, `src/Navigation`, sim-side `src/Multiplayer`) — NO `using Godot;`. `SelectionSystem` is presentation (`src/UI`) — it converts input to `EnqueueOrder` calls and never mutates sim truth except via the existing offline-apply fallback. The 1.10b banned-API analyzer covers the sim files (advisory on master) — keep them float-free.
- **AR-17 / lockstep contract:** the two apply switches are the deterministic command→world step. Both peers (and replay) MUST apply identically; the new cases must be byte-for-byte equivalent across `LockstepManager` and `ReplayPlayer`.
- **Data-driven (platform rule):** the follow-leash and patrol-arrival thresholds are tuning constants — keep them named + commented in sim code for now (no creator-facing knob is required by this story), but do NOT hardcode them as bare magic literals (the analyzer's `CHM0004` advisory flags bare caps; a named `static readonly Fixed` is correct).

### File structure requirements
**Create:**
- `godot/ProjectChimera.Sim.Tests/Combat/CommandVocabularyTests.cs` — AC1c/2/3/4/5 behavior tests.
- `godot/ProjectChimera.Sim.Tests/Multiplayer/CommandApplyParityTests.cs` — AC6a both-paths-agree + AC6b serialization round-trip.
- `godot/ProjectChimera.Sim.Tests/Golden/CommandVocabularyScenario.cs` — all-orders in-code scenario (AC6d).
- `godot/ProjectChimera.Sim.Tests/Golden/CommandVocabularyGoldenTests.cs` — AC6d two-run + golden + record hook.
- `godot/ProjectChimera.Sim.Tests/Golden/command-vocabulary-scenario.golden.txt` — NEW golden (embedded; LF-only).

**Edit (sim):**
- `godot/src/Core/EntityWorld.cs` — enum append (4 values) + `CommandTarget` + the patrol-route ring (`PatrolWaypoints`/`PatrolCount`/`PatrolIndex`/`PatrolDir`) + `MAX_PATROL_WAYPOINTS` const + sentinels (Task 1).
- `godot/src/Combat/CombatSystem.cs` — split Hold; 3 new Tick*Combat; extend gatherer normalization (Task 2).
- `godot/src/Navigation/MovementSystem.cs` — Hold anchor exemption (Task 3).
- `godot/src/Multiplayer/LockstepManager.cs` + `godot/src/Multiplayer/ReplayPlayer.cs` — 3 new apply cases EACH, identical (Task 4).
- `godot/src/Core/SimChecksum.cs` — fold 2 arrays + AlgoVersion 3→4 (Task 6).

**Edit (presentation):**
- `godot/src/UI/SelectionSystem.cs` — right-click enemy hit-test + P/F handlers + enemy-find helper + Issue* methods (Task 5).

**Edit (tests / guards / project):**
- `…/Meta/VersionStampConsistencyTests.cs`, `…/Golden/SimChecksumCoverageGuardTest.cs` — version pins (Task 6).
- `…/ProjectChimera.Sim.Tests.csproj` — embed the new golden (Task 6).
- 5 existing `*.golden.txt` — re-recorded (Task 6).

**Do NOT touch:** `UnitOrder`/`NetworkCommand` wire layout; `ReplayRecorder.VERSION`; `CanonicalModelHash`; `SystemOrderTest`; `GoldenChecksumReplay.cs` engine; `FixedPoint.cs`; `SimRng.cs`; `SimulationHost`/`SimulationLoop` construction; the CI gate jobs; `godot.csproj`.

### Testing requirements
- **Tier-1 xUnit, Godot-free** for every assertion. Patterns to mirror: `AiActiveGoldenTests.cs` (golden two-run + record hook + header), `GoldenScenario.cs`/`AiActiveScenario.cs` (in-code all-`Fixed` scenario), `SimChecksumCoverageGuardTest.cs` (differential fold proof + known-state pin), `DelayMathTests.cs`/`TriggerValidationTests.cs` (small focused units). Build `EntityWorld` + the system under test directly for behavior tests — no `SimulationHost` needed unless you want the full 9-system pipeline (the golden does).
- **Both apply switches:** if `ApplyOrders` is `private`, drive `ReplayPlayer` via a crafted in-memory `.chmr` and `Flush(tick)` (its public surface), and for `LockstepManager` assert the equivalent post-state, OR add a minimal internal test seam consistent with how 1.11 exposed `DelayMath` (compiled into the test assembly, so `internal` is same-assembly-visible — no `InternalsVisibleTo` needed if the file is in `SimSources.props`; but `LockstepManager` is Godot-coupled and NOT in `SimSources.props`, so prefer the public/replay-file route or a thin static helper extraction kept behavior-neutral).
- **Never hardcode test counts; never set `CHIMERA_GOLDEN_RECORD` in CI/scripts;** new goldens are LF-only; never "fix" a red gate by re-recording without understanding the delta.
- **After the SimChecksum bump:** confirm `git status --short -- '*.golden.txt'` shows the 5 existing goldens MODIFIED (re-recorded to v4) + the 1 new golden ADDED — and nothing else.

### Previous-story intelligence (1.11 + the M1 chain — all DONE, code-reviewed PASS)
- **1.11** established the exact golden pattern you mirror (AI-active scenario + two-run/golden/record-hook tests + a self-identifying `GoldenHeader` whose re-baseline hint names its own filter). It also proved the `CHIMERA_GOLDEN_RECORD` one-shot record→`dotnet build`→commit loop works, and that integer/`Fixed`-only goldens are cross-platform-safe while float-bearing ones (the AI) must be Windows-gated. **Your 1.12 golden is integer/`Fixed`-only → it does NOT need Windows-gating** (the opposite posture from the AI-active golden).
- **1.5** is the precedent for THIS kind of change: it folded `SimRng.State` into `SimChecksum`, bumped `AlgoVersion 2→3`, re-pinned the known-state guard, and re-recorded the goldens — same mechanical surface you repeat for v4. **1.3b** added the coverage guard and is why the known-state hash is a deliberate tripwire.
- **1.8a/1.8c** built the `SimulationHost` 9-system order you compose; `SystemOrderTest` pins it. You add NO system, so that test stays green untouched.
- **Conventions to respect:** brownfield additive slices over rewrites; reuse existing SoA/stores (don't add per-entity classes); comment public methods + non-obvious logic; `#nullable enable` per file; PascalCase/camelCase/SCREAMING_CASE; files match class names under `godot/src/<System>/`.

### Git intelligence
- The repo auto-commits hourly as `[AutoSave] <timestamp>`; story work lands in that stream. The analyzer is advisory on master (a stray-float warning won't block the autosave), but keep sim files float-free regardless. `baseline_commit` for this story: `2c5588e`.
- Build/CI artifacts you must keep green but NOT edit: `.github/workflows/*` (the `tier1-golden-gate` + `cross-platform-determinism-check` jobs), `SimSources.props` (no new sim file needs adding — all edits are to already-compiled files), the `DependencyHygieneTests` package guard.

### Project Context Rules (from `_bmad-output/project-context.md`)
- **`Fixed` (16.16) is the only sim numeric type.** New thresholds (`FOLLOW_LEASH`, patrol arrival) are `static readonly Fixed`; the packed target id is a raw `int`, never a float. No `Fixed.FromFloat` in the tick or in test-scenario authoring.
- **Process entities in ascending ID order** — the existing combat/movement/checksum loops already do; preserve it in every new branch.
- **SoA, not AoO** — new per-entity data is a new parallel array indexed by id, managed by the existing free list; reset it in `Create()`.
- **No `using Godot;` in sim** — `CombatSystem`/`MovementSystem`/`EntityWorld`/`SimChecksum`/apply switches stay pure C#. Only `SelectionSystem` (UI) sees Godot.
- **Lockstep input delay [2,12], replays `.chmr`** — unchanged here; you only add command VALUES, not wire fields.
- **Data-driven / composition** — no hardcoded balance literals; the 6 archetypes are the only "types". (1.13 will read `UnitDefinition.Category` for formations — out of scope here.)
- **Engine/runtime:** Godot 4.6.3, `net8.0`; project files `godot.csproj`/`godot.sln` (untouched).

### Decisions baked in (override BEFORE dev-story if you disagree)
1. **Patrol = multi-waypoint shift-click route** (Alec's call, 2026-06-25), reverse-at-ends, stored as a FIXED-CAPACITY flat SoA ring (`MAX_PATROL_WAYPOINTS=8`) — NOT a dynamic per-unit list (that would break SoA/determinism). A plain `P`-click is the classic 2-point ping-pong; each shift-click appends a waypoint via the new `PatrolAppend` command. This is the literal reading of the AC's "two or more waypoints … reverses at the final leg". **⚠ Size note — this is the single biggest piece of the story:** vs a bare ping-pong it adds 3 extra SoA arrays, the `PatrolAppend` command + its both-switch apply + the shift-detect input flow, and a bigger checksum fold. If Task 2's patrol portion + storage balloons beyond one comfortable dev pass, split multi-waypoint patrol into its own follow-up via `gds-correct-course` — the **N=2 ping-pong is the shippable floor** and satisfies the AC on its own (the route code degrades to it cleanly). Scope is PATROL routes only — NOT a general per-unit command queue (shift-Move/shift-Attack across types stays out).
2. **One `CommandTarget[]` array** serves both AttackTarget (enemy id) and Follow (friendly id), since the states are mutually exclusive. Alternative: two explicitly-named arrays (`ForcedTarget`/`FollowTarget`) — also fine, just hash both.
3. **Follow is tracking-only in 1.12** (re-path beyond leash, idle within, drop-on-death) — it does NOT auto-engage enemies, matching the AC text exactly. If escorts feel too passive in playtest, adding Idle-style in-range auto-attack (without breaking leash to chase) is a clean fast-follow. Flagged, not built.
4. **AttackTarget/Follow movement uses `MoveTarget` direct steering** set by CombatSystem each tick (mirroring Idle global-chase), NOT a one-shot `OnRequestPath` (the target moves; a stale path desyncs the look and the golden harness has no path system anyway).

### References
- `_bmad-output/planning-artifacts/epics.md:788–810` — Story 1.12 (statement, 6 ACs, "Covers DG-1/UX-DR66", the brownfield dev-hint paragraph). `:430` — FR-53. `:242` — DG-1. `:335` (UX-DR66) — default keybindings. `:812–834` — Story 1.13 (the sibling DG story; the formation/separation work explicitly NOT in 1.12).
- Source — sim: `godot/src/Core/EntityWorld.cs:10–18` (enum), `:81–200` (SoA + ctor), `:205–251` (Create reset); `godot/src/Combat/CombatSystem.cs:68–96` (gatherer guard + command switch), `:99–222` (Tick*Combat bodies), `:226–292` (shared helpers); `godot/src/Navigation/MovementSystem.cs:33–103` (per-unit loop; `:72–101` separation+position); `godot/src/Core/SimChecksum.cs:37` (AlgoVersion), `:43–100` (Compute).
- Source — multiplayer: `godot/src/Multiplayer/LockstepManager.cs:230–239` (EnqueueOrder), `:650–700` (ApplyOrders switch); `godot/src/Multiplayer/ReplayPlayer.cs:168–218` (ApplyOrders switch); `godot/src/Multiplayer/NetworkCommand.cs:63–175` (UnitOrder 11-byte format + TickCommandPacket Write/TryRead); `godot/src/Multiplayer/ReplayRecorder.cs:73–91` (RecordTick format).
- Source — presentation: `godot/src/UI/SelectionSystem.cs:140–151` (Enqueue helpers), `:218–258` (right-click + key handlers), `:332–458` (Issue* methods), `:495–531` (FindNearest helpers).
- Tests to mirror: `godot/ProjectChimera.Sim.Tests/Golden/AiActiveGoldenTests.cs`, `GoldenScenario.cs`, `GoldenChecksumReplay.cs`, `SimChecksumCoverageGuardTest.cs`; `…/Meta/VersionStampConsistencyTests.cs`; `…/Sim/SystemOrderTest.cs`; `…/ProjectChimera.Sim.Tests.csproj:27–44` (golden embeds).
- `_bmad-output/project-context.md` — determinism law, Sim/Presentation boundary, `Fixed`/SoA/`SimRng` rules.

## Dev Agent Record

### Agent Model Used

### Debug Log References

### Completion Notes List

### File List

## Change Log

| Date | Version | Change | Author |
|---|---|---|---|
| 2026-06-25 | 0.1 | Story created via `gds-create-story` (exhaustive context-engine analysis; 13+ source files read at line level; SimChecksum re-baseline surface mapped; 4 scope decisions baked in). Status → ready-for-dev. | Alec (SM) |
| 2026-06-25 | 0.2 | Decision #1 changed per Alec: Patrol upgraded from a 2-waypoint ping-pong to a MULTI-WAYPOINT shift-click route (new `PatrolAppend=9` command + fixed-cap `PatrolWaypoints`/`Count`/`Index`/`Dir` SoA ring, reverse-at-ends; N=2 = the ping-pong floor). Threaded through ACs 1/4a/6, SCOPE, brownfield table, Tasks 1/2/4/5/6/7, SoA design, and the decisions list. Size-flagged + splittable. | Alec (SM) |
