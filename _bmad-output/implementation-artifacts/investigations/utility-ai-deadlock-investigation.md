# Investigation: Utility AI deadlock — match reaches permanent zero-change state

## Hand-off Brief

1. **What happened.** Workers are combat-active (alpha worker `attack_damage: 5` defeats CombatSystem's only non-combatant gate, `AttackDamage == 0`), so from tick 0 all four workers abandoned mining for idle global-chase, slaughtered each other mid-map, and both economies earned exactly zero ore all match — Confirmed at every link.
2. **Where the case stands.** Root cause Confirmed with exact ore-ledger corroboration (P2: 200−100−75=25 ✓; P1: 700 flat ✓; survivors 2 workers + 1 scout = 3 units ✓). Three compounding mechanisms identified beyond the primary; the session-21 AI rewrite is NOT the primary cause.
3. **What's needed next.** One-line fix in CombatSystem (skip gather-active entities) restores the match; AI income-recovery and building-damage are separate tracked stories.

## Case Info

| Field            | Value                                                                      |
| ---------------- | -------------------------------------------------------------------------- |
| Ticket           | N/A                                                                        |
| Date opened      | 2026-06-09                                                                  |
| Status           | Concluded (root cause Confirmed)                                            |
| System           | Windows 11, Godot 4.6.2 mono, .NET 8, offline Play mode, AiLevel=Normal, alpha_map_01 |
| Evidence sources | Verify report (D:\Brain\Reports\godot-checks\Project_Chimera-2026-06-09\verify-report.md), HUD screenshots, live probes, source, faction/scenario JSON |

## Problem Statement

Original claim: "(1) P2 AI built Barracks by tick 45; (2) a single early P2 combat unit killed both P1 workers at P1's base despite Normal attack threshold 5; (3) P2 income flatlined at 25 ore, sim hash bit-identical ticks 1680→3180; (4) no tech, no expansion, no waves — permanent stalemate. Suspect: session-21 AiOpponentSystem rewrite or worker-gathering resume path."

**Premise corrections found:** (a) income was never positive — P1's ore also never grew (700 flat from the +500 test trigger; gathering never happened for either faction from tick 0); (b) the lone attacker was not an AI attack wave but per-unit idle global-chase; (c) the AI rewrite is an amplifier, not the cause.

## Evidence Inventory

| Source   | Status    | Notes     |
| -------- | --------- | --------- |
| Verify-run observations (HUD, hashes, fog-off probes) | Available | First-hand |
| Source code | Available | CombatSystem, GatheringSystem, AiOpponentSystem, BuildingStore traced |
| Faction/scenario JSON | Available | alpha_faction.json, alpha_map_01.json |
| Godot editor log output | Missing | MCP get_log_messages empty all session — did not block conclusion |
| Deterministic repro | Available | See Reproduction Plan; trigger file edits not required |

## Investigation Backlog

| # | Path to Explore | Priority | Status | Notes |
| - | --------------- | -------- | ------ | ----- |
| 1 | AiOpponentSystem — recovery at low ore | High | Done | No income action exists; all actions affordability-gated (Finding 4) |
| 2 | Why P2 income is exactly 0 | High | Done | Workers never gathered — combat hijack from tick 0 (Findings 1–3) |
| 3 | Lone P2 unit at P1 base | High | Done | Idle global-chase, not LaunchAttack (Finding 2; LaunchAttack never fired — max 1 idle combat unit < threshold 5) |
| 4 | What killed P1's workers | Medium | Done | P2 scout (8 dmg) + P2 workers (5 dmg) in mid-map brawl (Deduction 2) |
| 5 | Premise check: verify test triggers | Medium | Done | Refuted as contributor — only P1 ore +500 and a UI toast; no combat/economy interaction |
| 6 | P2 ore = exactly 25 ledger | Medium | Done | 200 − 100 Barracks − 75 one scout (Finding 5) |
| 7 | AssignedGatherers leak on worker death while assigned | Low | Open | GatheringSystem never decrements on death; max-gatherer slots leak (side defect, small blast radius) |

## Timeline of Events

| Time | Event | Source | Confidence |
| ---- | ----- | ------ | ---------- |
| tick 0 | GatheringSystem assigns all 4 workers to nodes; CombatSystem same tick overwrites their MoveTarget toward nearest enemy (idle global-chase) | Code trace + zero income ever observed | Deduced |
| tick ~45 | AI builds Barracks: P2 200→100 ore | HUD screenshot | Confirmed |
| tick ~350–700 | Workers from both sides converge mid-map, 2v2 brawl (60 hp, 5 dmg) | Walk speeds + map geometry; red specks mid-map post-mortem | Deduced |
| tick ~800–1000 | Barracks completes; one scout trained (P2 100→25); scout (5.5 u/s) joins the brawl | Ore ledger + train times | Deduced |
| ≤ tick 1680 | Both P1 workers dead; P2 keeps 2 workers + 1 scout; scout's last chase ends near P1 base | HUD (P1: 0 units, P2: 3 units 3/20) + fog-off screenshots | Confirmed |
| tick 1680→3180 | Sim hash bit-identical (0xD6ABD8CD) — total fixed point | HUD screenshots | Confirmed |

## Confirmed Findings

### Finding 1: Workers pass CombatSystem's combatant filter

**Evidence:** godot/src/Combat/CombatSystem.cs:48 (`if (world.AttackDamage[i] == Fixed.Zero) continue; // non-combatant`); godot/resources/data/factions/alpha_faction.json:14 (`"attack_damage": 5` on worker).

**Detail:** The only gate excluding a unit from combat processing is zero attack damage. Alpha workers deal 5, so all workers are fully combat-processed every tick. GatheringSystem's header comment (godot/src/Economy/GatheringSystem.cs:11 — "Workers have AttackDamage == 0, so CombatSystem skips them entirely") documents an invariant the data violates.

### Finding 2: Idle command = unbounded global chase that overwrites MoveTarget

**Evidence:** godot/src/Combat/CombatSystem.cs:84-89 — no in-range target → `FindNearestEnemyGlobal` → `world.MoveTarget[i] = world.Position[anyEnemy]` + Moving flag set.

**Detail:** Workers' CommandState is Idle during gathering (gathering is tracked in GatherState, a separate axis). CombatSystem runs every tick and clobbers the MoveTarget GatheringSystem set (godot/src/Economy/GatheringSystem.cs:157), so a worker can never reach a node while any enemy entity exists anywhere on the map.

### Finding 3: Both factions earned zero ore the entire match

**Evidence:** HUD screenshots — P1 ore 700 at tick 45, 1680, 3180 (200 start + 500 test trigger, no deposits ever); P2 ore changes only at purchases (200→100→25).

### Finding 4: AI has no income action and gates every action on current affordability

**Evidence:** godot/src/AI/AiOpponentSystem.cs:248-257 (action enum — no worker/income action), :195-244 (every Score* returns 0 when unaffordable), :353-362 (only Barracks/Archery/Siege adopted as production buildings — CommandCenter never trains).

**Detail:** At 25 ore with zero income, every score is 0 → `Nothing` every tick, forever. LaunchAttack additionally requires ≥5 idle combat units (Normal); the count peaked at 1, so no wave ever launched — confirming the lone attacker was Finding 2's per-unit chase.

### Finding 5: Ore ledger closes exactly — P2 trained exactly one scout

**Evidence:** alpha_faction.json:38 (`"cost_ore": 75` scout), :19 (worker 50); AiOpponentSystem.cs:35 (Barracks 100). 200 − 100 − 75 = 25 ✓. Survivors 3 units @ 3/20 supply = 2 workers + 1 scout ✓ (matches fog-off screenshots: 2 red specks mid-map, 1 red unit near P1 base).

### Finding 6: No gameplay path can damage buildings

**Evidence:** `buildings.Health` is only read (godot/src/UI/CommandCardSystem.cs:126) and hashed (godot/src/Core/SimChecksum.cs:47); `BuildingStore.Destroy` (godot/src/Core/BuildingStore.cs:135) is called only from editor paths (godot/src/UI/EntityPlacer.cs:547,989,994). CombatSystem/ProjectileSystem damage `world.Health` (entities) exclusively.

**Detail:** The alpha_map_01 win condition `DestroyAllBuildings` is unreachable through combat. Even a healthy army parked in the enemy base can never end the match.

## Deduced Conclusions

### Deduction 1: The sim fixed point is fully explained

**Based on:** Findings 1–6.

**Reasoning:** After the last P1 entity died: P2's scout and workers have no global enemy (`FindNearestEnemyGlobal` = −1 → stand still); P2 workers are stuck in GatherState.MovingToResource whose per-tick check (distance > threshold → early return) mutates nothing; the AI scores all-zero; nothing damages buildings; no projectiles, no movement, no income. Every array the checksum hashes is static → bit-identical hash for 1,500 ticks.

### Deduction 2: Kill attribution

**Based on:** Findings 1, 2, 5; unit stats (worker 60 hp/5 dmg/3.5 u/s; scout 80 hp/8 dmg/5.5 u/s).

**Reasoning:** Only damage sources on the map were the 4 workers and 1 scout. P1 lost 2 workers; P2 lost none. The scout's arrival (faster, harder-hitting, ~tick 800–1000) tipped an otherwise mirror 2v2 worker brawl.

## Hypothesized Paths

### Hypothesis 1: AiOpponentSystem utility rewrite has no recovery/income action at low ore

**Status:** Confirmed (as amplifier, not primary cause)

**Resolution:** Finding 4. The rewrite did not cause the income loss; it guarantees non-recovery once income is zero. The old FSM would have hit the same wall (combat hijack is in CombatSystem + data, which predate session 21).

### Hypothesis 2: Worker gathering stops after AI build/train interaction

**Status:** Refuted

**Resolution:** Gathering never started — Findings 1–3. No build/train interaction involved (AI uses `BuildingStore.Create` directly; no worker-build flow on the AI side).

### Hypothesis 3: Lone unit attacked solo despite Normal threshold 5

**Status:** Confirmed (mechanism identified)

**Resolution:** Finding 2 + Finding 4: per-unit idle global-chase self-deploys every trained unit immediately; the threshold-gated wave system never participated. Note this defeats the AI's army-massing design even after the worker fix — trained units trickle solo toward the nearest enemy as they spawn.

### Hypothesis 4 (premise check): Verify test triggers contributed

**Status:** Refuted

**Resolution:** Triggers only added 500 ore to P1 and displayed a toast; no combat/economy code touched. The deadlock reproduces without them.

## Missing Evidence

| Gap | Impact | How to Obtain |
| --- | ------ | ------------- |
| None required for the conclusion | — | — |
| (Optional) GD.Print decision logs | Would visualize the AI's all-zero scoring live | Fix MCP log capture or run godot from CLI capturing stdout |

## Source Code Trace

| Element       | Detail                                      |
| ------------- | ------------------------------------------- |
| Error origin  | godot/src/Combat/CombatSystem.cs:48 (insufficient non-combatant gate) + :84-89 (global chase MoveTarget overwrite) |
| Trigger       | Any faction whose worker definition has `attack_damage > 0` (alpha_faction.json:14) entering Play mode with an enemy present |
| Condition     | Workers keep CommandState.Idle while gathering; CombatSystem runs after GatheringSystem and wins the MoveTarget write race every tick |
| Related files | godot/src/Economy/GatheringSystem.cs:11,40,157 (violated invariant; Build-command skip; node MoveTarget write); godot/src/AI/AiOpponentSystem.cs:195-244,248-257,322-336,353-362 (no recovery; threshold bypassed by idle chase); godot/src/Core/BuildingStore.cs:135 + godot/src/UI/EntityPlacer.cs:547,989 (building damage absent from gameplay) |

## Conclusion

**Confidence: High** (root cause Confirmed; deterministic repro; ore ledger and unit census close exactly).

Four mechanisms stack:

1. **Primary (match-breaking):** Workers are combat-active — `AttackDamage == 0` is the only non-combatant gate and alpha workers deal 5. Idle global-chase then hijacks their movement every tick → zero income for both factions from tick 0 → mutual worker slaughter.
2. **Secondary (makes it permanent):** The utility AI has no income-restoring action and zero-scores everything it can't afford → at 25 ore it no-ops forever.
3. **Tertiary (defeats AI design):** Idle = unbounded global chase self-deploys each trained unit solo, bypassing the threshold-gated attack-wave system entirely.
4. **Quaternary (no match can end):** Nothing in gameplay damages buildings; `DestroyAllBuildings` is unreachable through combat.

The session-21 AI rewrite is exonerated as the primary cause; it inherited a battlefield where economies were already impossible.

## Recommended Next Steps

### Fix direction

- **Mechanism 1 (trivial, one line):** In `CombatSystem.Tick`, skip gather-active entities: `if (world.GatherState[i] != GatherState.Inactive) continue;` after the Alive check (CombatSystem.cs:47-48). Keeps armed workers possible as data without letting auto-combat hijack the economy. Optionally also update the stale GatheringSystem comment. (Per-design note: leave `attack_damage: 5` in data if worker fight-back is wanted later via an explicit command.)
- **Mechanism 3 (small, design decision needed):** Either leash idle chase to `vision_range`, or spawn AI-trained units with `CommandState.Stop` and have `DoLaunchAttack`/`IdleCombatUnits` treat Stop as "available for a wave" (AiOpponentSystem.cs:177-178, 322-336). Recommend the latter for AI integrity; the leash is a global gameplay change Samus/the designer should weigh.
- **Mechanism 2 (story-sized):** Add a TrainWorker/recover-economy action to the utility scorer (CC as worker producer; score scales when worker count < N or income ≈ 0).
- **Mechanism 4 (story-sized, design-relevant):** Building damage path — let attacks target `BuildingStore` (melee + projectile), wire `BuildingStore.Destroy` into win-condition checks. Without it no skirmish on a DestroyAllBuildings map can ever conclude.

### Diagnostic

None required; repro below suffices.

## Reproduction Plan

1. alpha_map_01, AiLevel Normal, offline; enter Play mode; no player input.
2. Observe: P1 ore never increases (no deposits, ever); all 4 workers converge mid-map by ~tick 400 instead of mining.
3. By ~tick 1700: P1 0 units, P2 3 units, P2 ore 25; HUD hash stops changing (compare any two ticks ≥50s apart).
4. Post-fix verification: both factions' ore grows from first deposit (~tick 300); AI techs ArcheryRange after Barracks; attack waves launch with ≥5 units; no hash freeze.

## Side Findings

- `AssignedGatherers` leaks when a worker dies while assigned to a node (no decrement on death) — nodes can permanently lose gatherer slots. Small blast radius today; worth a cleanup task. (godot/src/Economy/GatheringSystem.cs:153-160 vs death path in CombatSystem.cs:259-264.)
- GatheringSystem header comment documents a false invariant (workers damage-0) — fix alongside Mechanism 1.
- MCP editor log capture returned empty all session despite GD.Print calls — tooling gap for future verifies.
- AI building costs are hardcoded constants in AiOpponentSystem.cs:34-37 ("must match EntityPlacer.BUILDING_COSTS and faction JSON") — violates the project's data-driven rule; drift risk.
