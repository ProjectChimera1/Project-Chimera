---
title: 'Restore skirmish integrity: exclude gatherers from auto-combat, trained units hold position'
type: 'bugfix'
created: '2026-06-09'
status: 'done'
baseline_commit: '65b784ad74e3a6f18d0f2f8187cf1d0ccd5d697f'
context:
  - '_bmad-output/implementation-artifacts/investigations/utility-ai-deadlock-investigation.md'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Workers are combat-active because CombatSystem's only non-combatant gate is `AttackDamage == 0` while alpha workers deal 5 — idle global-chase hijacks their MoveTarget every tick, so neither faction ever gathers ore and all workers march to mutual slaughter (Confirmed: bit-identical sim hash for 1,500 ticks). Separately, trained units spawn `CommandState.Idle` and self-deploy solo cross-map via the same global chase, bypassing the AI's threshold-gated attack-wave design.

**Approach:** (1) Skip gather-active entities (`GatherState != Inactive`) in CombatSystem's per-entity loop — gatherers never auto-combat regardless of their damage stat. (2) Trained units with no rally point spawn on `Stop` (hold position, attack in range only); the AI's wave logic counts and orders both `Idle` and `Stop` units.

## Boundaries & Constraints

**Always:** Pure C# sim layer (no Godot types, no float in gameplay state); deterministic — SoA array reads only, no iteration-order changes; ascending-ID processing preserved.

**Ask First:** Any change to scenario-spawned unit commands (map-authored aggression is a design call); any change to the Idle = global-chase semantics itself (leashing affects all gameplay).

**Never:** Do not zero worker `attack_damage` in faction JSON (fight-back is a future feature; the engine must tolerate armed workers as data). Do not touch win-condition / building-damage code (Mechanism 4, separate story). Do not modify LockstepManager / replay paths.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Worker gathering near enemy | Worker (damage 5, GatherState Idle/Moving/Gathering), enemy in vision | Worker keeps mining; CombatSystem never processes it | N/A |
| Worker on Build command | `CommandState.Build`, GatherState ≠ Inactive | Still skipped by combat; walks to site uninterrupted | N/A |
| Trained unit, no rally | Production completes, `HasRallyPoint == false` | Spawns `CommandState.Stop`: attacks in range, never chases | N/A |
| Trained unit, rally set | `HasRallyPoint == true` | Unchanged: `Move` to rally → existing Move→Stop transition on arrival | N/A |
| AI wave launch | ≥ threshold units in `Idle` **or** `Stop`, cooldown elapsed | All counted; all flipped to `AttackMove` toward P1 base | N/A |
| AttackMove completes | Wave arrives at goal | Unchanged: `ResumeAttackMove` → Idle (intentional engage-all semantics at the front) | N/A |

</frozen-after-approval>

## Code Map

- `godot/src/Combat/CombatSystem.cs` -- per-entity combat loop; line 48 has the insufficient `AttackDamage == 0` gate; lines 84-89 are the global chase that clobbers gatherer MoveTarget
- `godot/src/Economy/GatheringSystem.cs` -- header comment (line ~11) documents the false "workers have AttackDamage == 0" invariant
- `godot/src/Economy/BuildingSystem.cs` -- `SpawnTrainedUnit` (lines 138-184); no-rally branch currently leaves default `Idle`
- `godot/src/AI/AiOpponentSystem.cs` -- `BuildSnapshot` idle-unit count (lines 171-179); `DoLaunchAttack` selection filter (lines 322-336)

## Tasks & Acceptance

**Execution:**
- [x] `godot/src/Combat/CombatSystem.cs` -- after the Alive check in `Tick` (line 47), add `if (world.GatherState[i] != GatherState.Inactive) continue;` with a comment stating gatherers never auto-combat even when armed; update the class doc comment accordingly -- kills the economy hijack at its root
- [x] `godot/src/Economy/GatheringSystem.cs` -- rewrite the header comment lines claiming workers are skipped due to `AttackDamage == 0`; state the real contract (CombatSystem skips `GatherState != Inactive`) -- removes the false invariant
- [x] `godot/src/Economy/BuildingSystem.cs` -- in `SpawnTrainedUnit`, add an `else` to the rally branch: `world.CommandState[id] = UnitCommand.Stop;` -- trained units hold position instead of self-deploying
- [x] `godot/src/AI/AiOpponentSystem.cs` -- in `BuildSnapshot`, count `CommandState == Idle || CommandState == Stop` as available combat units (field renamed `AvailableCombatUnits`); in `DoLaunchAttack`, order both `Idle` and `Stop` units into the wave -- wave logic sees held units

**Acceptance Criteria:**
- Given alpha_map_01 Normal offline with no player input, when Play runs ~60s game time, then both factions' ore increases above starting values (first deposits ≈ tick 300) and all 4 workers remain alive and cycling.
- Given the AI's Barracks completes, when it trains units, then units accumulate at base in `Stop` (no solo cross-map trickle) until ≥5 available, then a wave `AttackMove`s toward P1 base and the attack cooldown resets.
- Given continued income, when the run extends, then the AI builds an ArcheryRange after the Barracks (tech progression resumes).
- Given any two HUD checksums ≥50s apart in the first 5 minutes, when compared, then they differ (no sim fixed point).

## Design Notes

`Stop` (not `HoldPosition`) matches the codebase's own move-completion semantics ("Move command completion → Stop"). Player-trained units also gain hold-at-spawn behavior — this is the desired RTS convention and removes the same trickle bug for the human player; rally-point flow is untouched. Scenario-spawned combat units intentionally keep `Idle` (map authors may want immediate aggression) — flagged, not changed.

## Spec Change Log

## Verification

**Commands:**
- `dotnet build D:\Projects\Project_Chimera\godot` -- expected: 0 errors, no new warnings

**Manual checks (if no CLI):**
- Run repro from the investigation case file via Godot MCP: alpha_map_01, Normal, Play mode, step ~120s game time; confirm the four ACs (ore growth, no worker deaths, wave of ≥5, hash changes). No GdUnit4 suite exists yet (godot/tests is empty) — manual sim verification is the gate.

**Verification result (2026-06-09, in-engine, ~245s game time):** All four ACs PASS. P1 ore 200→1020 with both workers cycling; AI teched ArcheryRange (4 buildings); wave of ≥5 launched and arrived at P1's CC (unit positions confirmed via MultiMesh transforms at −44…−46); five distinct sim hashes across samples — no fixed point. New observation logged to deferred-work.md #7: arrived wave hovers at ~1.0u in AttackMove (0.5u arrive threshold unreachable under crowding).

## Suggested Review Order

**Economy hijack fix (entry point)**

- The root fix: gatherers exempt from all auto-combat; stray combat commands normalized to Idle
  [`CombatSystem.cs:51`](../../godot/src/Combat/CombatSystem.cs#L51)

- Contract doc: what Idle/Stop mean per command state, and the gatherer exemption
  [`CombatSystem.cs:11`](../../godot/src/Combat/CombatSystem.cs#L11)

**Hold-position spawn**

- No-rally trained units spawn `Stop` instead of default `Idle` (global chase)
  [`BuildingSystem.cs:191`](../../godot/src/Economy/BuildingSystem.cs#L191)

- Spawn offset cycles per trained unit — held units would otherwise stack on one fixed-point coordinate
  [`BuildingSystem.cs:154`](../../godot/src/Economy/BuildingSystem.cs#L154)

- New `TrainedCount` SoA array backing the offset cycle
  [`BuildingStore.cs:56`](../../godot/src/Core/BuildingStore.cs#L56)

**AI wave availability**

- Snapshot counts Idle **and** Stop units as wave-conscriptable
  [`AiOpponentSystem.cs:174`](../../godot/src/AI/AiOpponentSystem.cs#L174)

- Launch flips both states to AttackMove; threshold semantics unchanged
  [`AiOpponentSystem.cs:334`](../../godot/src/AI/AiOpponentSystem.cs#L334)

**Peripherals**

- Header comment now states the real worker/combat contract (old one documented a false invariant)
  [`GatheringSystem.cs:11`](../../godot/src/Economy/GatheringSystem.cs#L11)
