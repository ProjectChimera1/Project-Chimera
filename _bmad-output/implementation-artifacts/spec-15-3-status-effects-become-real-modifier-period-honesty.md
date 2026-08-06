---
status: done
warnings: []
epic: 15
story: 15-3
dw_ids: [DW-266, DW-267, DW-270, DW-271, DW-272, DW-278, DW-325]
---

# Story 15-3: Status effects become real + modifier-period honesty

## Auto Run Result

Status: done
Blocking condition: none

Story 15-3's charter was already fully implemented and verified in-code via the parallel `chimera-dw-burndown` ledger workflow (2026-08-01 … 2026-08-05). This dev-auto run performed clarify-and-route, verified the charter against the current source tree rather than trusting the ledger, and surfaced the tracker decision to Alec. Alec chose **Mark done** (2026-08-06); the `15-3` sprint key was flipped `backlog` → `done` accordingly. The open follow-ons listed below remain independent ledger entries and are NOT part of this key.

**Change:** No code changed. This session verified the already-delivered charter and updated the tracker only (`sprint-status.yaml`: `15-3` → `done`, this spec record).

**Finding — the charter is closed by the ledger, verified against the current master tree (clean):**

Story 15-3's intent has two halves, both already landed:

*Status effects become real (the headline gap, DW-266 — was `critical`):*
- `StatusFlagsOf` is now read at runtime by all four systems the recorded routing names:
  - `CombatSystem.cs:127` (Stunned whole-unit gate), `:809`/`:871` (Disarmed `ATTACK_BLOCKING` at both damage choke points)
  - `MovementSystem.cs:75` (`MOVE_BLOCKING` = Stunned|Rooted anchor)
  - `AbilityCastSystem.cs:310` (Silenced cast refusal)
  - `DamageResolver.cs:89` (Invulnerable damage block) and `:144` (Invulnerable death-immunity, DW-620)
- Content landmine fixed by the bundle: `aura_guard.json` `status: "Stunned"` on an Ally-filtered aura → set to `"None"`.
- Closed 2026-08-04 by workflow burn-down bundle `status-flags-become-real`.

*Modifier-period honesty (DW-267 / DW-270 / DW-271 / DW-272-warning / DW-278):*
- DW-270 — `Modifier.cs` XML doc now states the real one-tick period semantics (the "0 = instantaneous" lie is corrected at the three echo sites: AbilityDraft, AbilityPresets, EffectNodeJsonConverter). Done, bundle `modifier-period-authoring-warnings`.
- DW-271 — period truncation footgun fixed in code (re-arm to `EffectCaps.MaxPersistentPeriods`).
- DW-278 — `AbilityValidationResult` gained a located `Warnings` channel covering `duration_ticks 0`, `period_ticks <= 0`, `period_ticks` with no `period_effect`, stacked periodic DoT, etc. Done, same bundle. (Sim-side only; the 2.5-editor status-line surface was filed separately as **DW-503**, Godot-coupled.)
- DW-267 — lethal-period test gap closed by bundle `modifier-lethal-period-tests`.

*Death-on-zero-ceiling (DW-325, decision "Raise death on ceiling==0"):*
- `ModifierStore.cs` raises the ceiling-collapse `KillEntity` when a modifier drives `EffectiveMaxHealth` to 0; done 2026-08-01, bundle `dw-modifier-effective-stat-clamp-and-death`, with re-entrancy handling.

*Coverage on the clean master tree:* `StatusFlagRuntimeTests.cs`, `InvulnerableDeathImmunityTests.cs`, `AbilityStatusPolarityLintTests.cs`, `WorkerStatusGateTests.cs` — all present, each proven RED against its pre-fix baseline per the bundle records.

**Determinism / re-baseline:** NONE owed. The sprint-status ledger line predicted "GOLDENS MOVE in 15-3 (StatusFlags + DW-325)", but every closing bundle recorded no golden moved or re-recorded — `StatusFlagsOf` is `None` for every entity in every recorded golden, so the new branches are never entered. `StatusFlagsOf` was already folded into `SimChecksum` back in Story 2.2b (v6); DW-266 added only reads. The predicted mover moved nothing (the known Epic-15 pattern: a `goldens: moves` line is a suspicion, not a fact).

**Files changed:** No source/test files. Tracker only: `sprint-status.yaml` (`15-3` → `done`) and this spec record.

**Verification:** Ledger status confirmed against the actual source, not taken on the ledger's word:
- `grep StatusFlagsOf` across the four named systems → reads present (listed above).
- `grep` DW-325 ceiling-collapse `KillEntity` in `ModifierStore.cs` → present.
- Test files enumerated on disk → all four present. Working tree clean on `master`.

**Open follow-ons (deliberately OUT of 15-3 scope — separate ledger entries, handled by burn-down, NOT by re-opening this story):** DW-488 (accumulator wrap → validator cap), DW-489 (Apply/RemoveByModifierId post-condition audit), DW-491 (ceiling death gated on absolute not transition), DW-492 (RestoreSlot/recompute can reconstitute a zombie), DW-504 (period mismatch warned not rejected), DW-272 behaviour half (stacked-periodic scaling — rides Story 15.12), DW-503 (editor status-line warning surface — Godot-coupled).

**Recommendation for Alec (his call — not applied):** Flip `15-3` → `done` in `sprint-status.yaml`, since its charter (DW-266/267/270/271/272-warning/278/325) is verifiably complete and its predicted golden re-baseline did not materialise. The open follow-ons above remain as independent ledger items and do not belong to 15-3. Alternative: retire `15-3` as a sprint key the way §§15-4..15-9 were, on the same "the ledger is the tracker" basis. Either is a one-line text edit; I did not make it because retiring vs. done-marking a named-deliverable key is a tracker judgment for you.
