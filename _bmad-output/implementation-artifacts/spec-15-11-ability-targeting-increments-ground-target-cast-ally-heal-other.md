---
title: 'Story 15.11 — Ability targeting increments: ground-target cast + ally-targeted heal-other'
type: 'feature'
created: '2026-08-06'
status: 'done'
baseline_revision: '2f7b0972'
final_revision: '87ebdb5a'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/implementation-artifacts/epic-15-context.md'
  - '{project-root}/_bmad-output/project-context.md'
warnings: [multiple-goals, oversized]
---

<intent-contract>

## Intent

**Problem:** Two authorable ability-targeting modes are dead in the running game. `AbilityTargeting.GroundPoint` (enum value 3) authors and validates but is **not castable** — the command card renders it disabled with `[ground-cast: coming soon]` (`CommandCardSystem.cs:1498-1503`), the 11-byte `UnitOrder` wire has no room for a ground point (both payload slots spent on slot+targetId), and `EffectContext` has no ground-point field (DW-280, open). Separately, single-target casting is enemy-only: there is no way to author an ally-targeted heal-other because no target-affinity hint exists and the click-picker is hardcoded to `FindNearestEnemyUnit` (DW-286, open). The card disable-gate and press-handler enumerate the targeting set twice and will diverge as modes are added (DW-290, carried as an AC of this story).

**Approach:** Thread a ground point end-to-end — widen `UnitOrder` 11→12 (move the ability slot into the new byte, freeing `TargetX`/`TargetZ` to carry the two `Fixed` ground coords), bump `ReplayRecorder.VERSION` and `PROTOCOL_VERSION`, add a transient ground-point to `EffectContext`, and make `AbilityCastSystem` and the effect leaves consume it. Add an optional `target_affinity` hint to `AbilityDefinition` and an affinity-aware click-picker so a `TargetUnit` heal can target an ally. Fold the two command-card targeting enumerations into one shared is-castable predicate. Author a new golden for the ground-cast path (existing goldens must not move).

## Boundaries & Constraints

**Always:**
- Sim math is `Fixed` only; ground coords packed/read as raw ints (`Fixed.FromRaw`/`Fixed.FromFloat` at the UI quantization seam **only**, never in the tick). Process entities in ascending id order. No `using Godot;` / `float` / `Random` in sim files (`src/Core`, `src/Effects`, `src/Multiplayer` sim logic).
- The wire is **not** a `SimChecksum` input and `PendingCast*` are not folded (`SimChecksum.cs:45,98,431`). Existing casts (Self/TargetUnit) must remain **byte-identical** in effect execution — no existing golden may move and `SimChecksum.AlgoVersion` (24) must **not** change.
- Any change to the `UnitOrder` wire stride **must** bump both `ReplayRecorder.VERSION` (4→5) and `PROTOCOL_VERSION` (2→3); an older replay must be rejected cleanly at the version gate (`ReplayPlayer` ctor), never silently misaligned.
- `OrderApplier.ApplyActiveOrder` is the **single** command→world switch (Story 1.12 unified the two the DW cites); the `TickCommandPacket.Write`/`TryRead` decode feeds every apply path — edit the CastAbility case and the decode once, correctly, or all paths desync together.
- The new `target_affinity` field is optional and, when absent/null, must serialize identically to today so `ContentHash`/`CanonicalModelHash` are unchanged for every shipped ability. It deserializes only through `ContentJson.Options` (`UnmappedMemberHandling.Disallow`); the computed parse getter must be `[JsonIgnore]`.
- `AbilityTargeting` stays a 4-value enum; ally targeting is a hint on `TargetUnit`, not a 5th mode.

**Block If:**
- Widening the wire or adding the `EffectContext` field moves any **existing** golden checksum (indicates the new state leaked into a hashed/folded path) — HALT `blocked`, blocking condition `unexpected golden movement on existing scenario`. A NEW golden authored for the ground-cast path is expected and does not trigger this.
- Adding `target_affinity` changes `ContentHash` or `CanonicalModelHash` for any existing shipped ability JSON — HALT `blocked`, blocking condition `content hash moved on existing ability`.
- The godot-mcp bridge is unreachable for the in-engine gate — HALT `blocked`, blocking condition `godot-mcp bridge unreachable` (do not fabricate the artifact).

**Never:**
- Do not add a 5th `AbilityTargeting` enum value, formation/group-cast, or queued cast chaining.
- Do not repurpose `TargetX`/`TargetZ` for ground coords on Self/TargetUnit casts (only GroundPoint casts carry a point).
- Do not bump `SimChecksum.AlgoVersion` or fold `PendingCast*`/`EffectContext` into the checksum.
- Do not use `Fixed.FromFloat` anywhere except the UI screen→world quantization seam.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Ground cast, valid point | GroundPoint ability, click on pathable ground | Order packs slot(byte11)+groundX(TargetX)+groundZ(TargetZ); effect graph resolves centered on the ground point | No error |
| Ground cast, off-map point | GroundPoint ability, click outside map bounds | Cast is denied or clamped per existing target-validity path; no crash, cooldown not consumed on deny | Push OrderDenied cue |
| Ally heal-other | TargetUnit + `target_affinity: Ally`, click a friendly unit (not self) | Picker resolves nearest own-faction unit excluding caster; heal applies to that unit | No error |
| Ally heal-other, click enemy | Ally-affinity ability, click on an enemy | No target resolved (picker excludes enemies); targeting disarms, no cast | Silent disarm |
| Enemy cast (regression) | TargetUnit + no affinity (or `Enemy`), click enemy | Identical behavior to today (byte-identical wire + effect) | Existing path |
| Old replay load | `.chmr` recorded at VERSION 4 | Rejected at version gate with "please re-record" | Clean reject, no misalignment |
| Absent affinity (regression) | Any shipped ability JSON with no `target_affinity` | ContentHash unchanged; ParsedTargetAffinity null → enemy-only picker as today | No error |

</intent-contract>

## Code Map

- `godot/src/Multiplayer/NetworkCommand.cs` -- `UnitOrder` struct (SIZE 11, offsets), `TickCommandPacket.Write`/`TryRead` decode, `OrderApplier.ApplyActiveOrder` CastAbility case (~521-538), `PROTOCOL_VERSION` (~776).
- `godot/src/Multiplayer/ReplayRecorder.cs` -- `VERSION = 4` (line 39); `ReplayPlayer.cs` version gate (~169-177).
- `godot/src/Core/EntityWorld.cs` -- `PendingCast*` SoA fields; add ground-point payload arrays; `CastAbility` doc comment (~27).
- `godot/src/Effects/EffectContext.cs` -- add transient ground-point field, thread through ctors + `WithTarget` (~71-106).
- `godot/src/Effects/AbilityCastSystem.cs` -- `TryCast` target resolution (~379-419); add the GroundPoint branch that reads the ground point and builds the ctx.
- `godot/src/Effects/SearchAreaEffect.cs` (and any leaf centering on `PrimaryTargetId`) -- center on the ground point for a ground cast.
- `godot/src/Core/Definitions/AbilityDefinition.cs` -- add `target_affinity` field + `[JsonIgnore] ParsedTargetAffinity` getter (mirror `ParsedTargeting`, ~106-114).
- `godot/src/Core/Definitions/AbilityValidator.cs` -- validate affinity parse + cross-rule (~81-107).
- `godot/src/UI/SelectionSystem.cs` -- affinity-aware picker (add `FindNearestAllyUnit`; `FindNearestEnemyUnit` ~1117), ground-target arm using `RaycastGround` (~1076), `IssueCastAbilityCommand` overloads (~1023), cast reticle.
- `godot/src/UI/CommandCardSystem.cs` -- fold disable-gate (~1495-1510) + press-handler (~1544-1556) into one shared is-castable predicate; remove `[ground-cast: coming soon]`.
- `godot/src/CreationSuite/AbilityEditorPanel.cs` -- affinity authoring OptionButton (mirror targeting dropdown ~516-529).
- `godot/ProjectChimera.Sim.Tests/Golden/` + `resources/data/abilities/` -- new ground-cast golden + a heal-other ability JSON; Tier-1 wire-round-trip + affinity-parse + content-hash-stability tests.

## Tasks & Acceptance

**Execution:**
- `godot/src/Multiplayer/NetworkCommand.cs` -- Widen `UnitOrder` to SIZE 12 by appending one byte carrying the ability slot for CastAbility; update `Write`/`TryRead` and the ctor; in `OrderApplier.ApplyActiveOrder`'s CastAbility case, read slot from the new byte and store the two payload ints (targetId **or** ground coords) into new `PendingCast*` fields without interpreting them; bump `PROTOCOL_VERSION` 2→3. Update the `CastAbility` doc comment in `EntityWorld.cs`.
- `godot/src/Multiplayer/ReplayRecorder.cs` -- Bump `VERSION` 4→5 so a pre-widen replay is rejected at the gate, not decoded at the wrong stride.
- `godot/src/Core/EntityWorld.cs` -- Add `PendingCastPointX`/`PendingCastPointZ` (`Fixed`) SoA arrays alongside existing pending-cast state, reset on slot recycle (SoA-recycle rule); leave them out of `SimChecksum` (transient, cleared each tick).
- `godot/src/Effects/EffectContext.cs` -- Add a transient `TargetPoint` (`FixedVec3`) + `HasTargetPoint` flag; thread through both ctors and `WithTarget`; default absent for Self/TargetUnit so those paths are byte-identical.
- `godot/src/Effects/AbilityCastSystem.cs` -- Add the `GroundPoint` branch in `TryCast`: read the ground point from `PendingCast*`, build `EffectContext` with `HasTargetPoint=true`; deny (OrderDenied cue, no cooldown) if the point is invalid. Self/TargetUnit/None paths unchanged.
- `godot/src/Effects/SearchAreaEffect.cs` (+ any leaf keyed on `PrimaryTargetId` position) -- When `ctx.HasTargetPoint`, center the search/impact on `TargetPoint` instead of the (absent) primary target's position.
- `godot/src/Core/Definitions/AbilityDefinition.cs` -- Add optional `[JsonPropertyName("target_affinity")] string? TargetAffinity` + `[JsonIgnore] ParsedTargetAffinity` getter mapping `Ally|Enemy|Any` (null → default = enemy-for-TargetUnit, matching today).
- `godot/src/Core/Definitions/AbilityValidator.cs` -- Reject an unparseable `target_affinity`; warn (non-fatal `Warnings` channel) if affinity is set on `Self`/`None` (meaningless there).
- `godot/src/UI/SelectionSystem.cs` -- Add `FindNearestAllyUnit` (own faction, excluding caster); make `ArmCastTargeting` carry the affinity and pick the matching set with an affinity-appropriate prompt; add a ground-target arm that reuses `RaycastGround`, quantizes via `Fixed.FromFloat` at this seam, and issues via a new `IssueCastAbilityGroundCommand(caster, slot, groundX, groundZ)`; add a cursor-following cast reticle (presentation Node, modeled on `OrderMarkerBridge`) shown only while a ground arm is live.
- `godot/src/UI/CommandCardSystem.cs` -- Introduce one shared predicate (e.g. `AbilityCardState`/`IsCastable`) consulted by BOTH the disable-gate and the press-handler; remove the `[ground-cast: coming soon]` fence so GroundPoint arms ground targeting and TargetUnit arms affinity-aware targeting; Self/None stay instant.
- `godot/src/CreationSuite/AbilityEditorPanel.cs` -- Add an affinity OptionButton mirroring the targeting dropdown; show it only for `TargetUnit`/`GroundPoint`; wire into load (`ReflectModelIntoForm`) and model rebuild.
- `godot/resources/data/abilities/` -- Author `mend_ally.json` (TargetUnit + `target_affinity: Ally`, heal effect) and `ground_nuke.json` (GroundPoint, `search_area(Enemy)→damage` centered on the point) to exercise both new paths.
- `godot/ProjectChimera.Sim.Tests/` -- Add Tier-1 tests: `UnitOrder` 12-byte round-trip (all commands incl. GroundPoint), `ParsedTargetAffinity` mapping, ContentHash stability across all shipped abilities before/after the field addition (absent → identical), and a **new** golden replaying a ground-cast scenario (record with `CHIMERA_GOLDEN_RECORD=1`, then `dotnet build` to refresh the embedded copy, then commit).

**Acceptance Criteria:**
- Given a `GroundPoint` ability on a selected unit, when the player presses its card, then the card is enabled (no "coming soon"), a cursor-following reticle appears, and a left-click on ground issues a cast whose effect resolves at that ground point (verified in-engine by observed damage/effect at the clicked location vs. authoring source).
- Given a `TargetUnit` ability with `target_affinity: Ally`, when the player presses its card and clicks a friendly unit other than the caster, then that ally receives the effect (e.g. HP rises for a heal); clicking an enemy resolves no target and disarms.
- Given any existing (no-affinity, non-GroundPoint) ability, when a match runs, then behavior and the full test suite are byte-identical to before — no existing golden moved, `SimChecksum.AlgoVersion` still 24, `ContentHash` unchanged for every shipped ability.
- Given a `.chmr` replay recorded before this change, when it is loaded, then it is rejected at the version gate with a re-record message (no silent stride misalignment).
- Given the command card, when targeting modes are evaluated, then the enabled-state and the press-action derive from ONE shared predicate (DW-290) — GroundPoint and unknown are handled identically in both.

## Design Notes

**Wire packing (the crux).** The 11-byte order spends both 4-byte payloads: `TargetX`=slot, `TargetZ`=targetId. A ground point needs 8 bytes, so the DW's "11→12" only works by relocating the 1-byte slot (≤ `MAX_ABILITIES_PER_UNIT`=4) into the new byte 11, freeing `TargetX`/`TargetZ`:

```
CastAbility order (SIZE 12):
  byte 0-1   UnitId
  byte 2     Command (CastAbility | optional Queued flag)
  byte 3-6   TargetX  = groundX (GroundPoint) | 0 (TargetUnit/Self)
  byte 7-10  TargetZ  = groundZ (GroundPoint) | targetId (TargetUnit) | -1 (Self)
  byte 11    ability slot (all cast variants)
Other orders (Move/AttackMove/…) write byte 11 = 0.
```
`OrderApplier` stores raw payloads mode-agnostically into `PendingCast*`; `AbilityCastSystem.TryCast` interprets them by `ab.ParsedTargeting`. Because the wire is not hashed and `PendingCast*`/`EffectContext` are transient (not folded), no existing golden moves and no `AlgoVersion` bump is needed — only the two protocol/replay version bumps (stride changed).

**Affinity default preserves today.** `ParsedTargetAffinity == null` must behave exactly like the current enemy-only `TargetUnit` pick, so shipped content is unchanged. `Ally` = own faction excluding the caster (cross-faction allies via `AllianceStore` are out of scope for the UI pick — file a residual DW if wanted).

## Verification

**Commands:**
- `dotnet build godot/godot.csproj` -- expected: 0 errors (C# not hot-loaded; required before the in-engine gate).
- `dotnet test godot/godot.sln` (or the Sim.Tests project) -- expected: all green; existing goldens unchanged; new ground-cast golden + wire round-trip + affinity + content-hash-stability tests pass. Run twice — a lone `CanonicalModelHashPerfTests…StaysUnderTheRegressionCeiling` fail that passes on re-run is a known CPU-contention flake, not a regression; do not treat it as failure.
- `CHIMERA_GOLDEN_RECORD=1 dotnet test --filter FullyQualifiedName~<GroundCastGolden>` then `dotnet build` -- expected: records the new golden only; re-running without the env var passes.

**In-Engine Gate (mandatory — diff touches `godot/src/UI/**` and CreationSuite):**
- Build, launch a match over godot-mcp, select a unit with a GroundPoint ability and one with an Ally heal. Drive the card press (Button `pressed`) + a ground/ally pick, then capture `godot_runtime_state` digests: for the ground nuke, the target's HP drop at the clicked location; for the ally heal, the friendly unit's HP rising while the caster's is unchanged. Assert the numbers against `ground_nuke.json`/`mend_ally.json`. Append the `### In-Engine Gate` artifact block with a real digest.

## Spec Change Log

No `bad_spec` loopback occurred — the review triaged only localized `patch` and `defer` findings, so no section outside `<intent-contract>` was amended and the code was not re-derived.

## Review Triage Log

### 2026-08-06 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 10: (high 0, medium 7, low 3)
- defer: 2: (high 0, medium 1, low 1)
- reject: 1: (high 0, medium 0, low 1)
- addressed_findings:
  - `[medium]` `[patch]` P1 — `AbilityCastSystem` consume block never cleared `PendingCastPointX/Z`; the "always 0 at the checksum boundary → not folded" invariant was false and a mid-tick save persisted a stale point. Now cleared to `Fixed.Zero` alongside Slot/Target.
  - `[medium]` `[patch]` P2 — queued-order slot bit-pack (`ORDER_QUEUE_SLOT_SHIFT=5`/`MASK=0x1F`) stole bit 5 from the `<=0x3F` command budget of the SimChecksum-folded `OrderQueueCmd`. Fixed to SHIFT=6/MASK=0x3F, bounded the slot before packing, added a pack-site assert.
  - `[medium]` `[patch]` P3 — GroundPoint cast fell back the primary target to the caster, so a bare-leaf ability self-harmed. Now leaves the primary target invalid (no self-harm); added a validator warning when a GroundPoint root won't resolve at the point.
  - `[medium]` `[patch]` P4 — added discriminating tests: ground SearchArea damages entities at the point and NOT the caster; queued-cast bit-pack round-trips slot=3 + command intact.
  - `[medium]` `[patch]` P5 — extracted the Godot-free `CastTargetPicker` (affinity pick + `IsTargetingCastable`) from `SelectionSystem`/`CommandCardSystem` and unit-tested Ally/Any/Enemy selection and the shared disable/press predicate.
  - `[medium]` `[patch]` P6 — online ground-cast impact marker was skipped (spawned after the `!applyNow` return); hoisted before the guard so it shows in MP.
  - `[medium]` `[patch]` P7 — mid-EA-enum save insertion drifted the persisted layout; bumped `SaveGameFile.FormatVersion` 3->4 with the fail-closed reject gate (pre-15.11 saves rejected, not silently misaligned).
  - `[low]` `[patch]` P8 — `target_affinity` on GroundPoint was authorable but silently ignored; validator now warns for any non-TargetUnit ability and the doc says TargetUnit-only.
  - `[low]` `[patch]` P9 — restored `ParsedActivation`'s displaced XML doc comment; `ParsedTargetAffinity` now carries a single summary.
  - `[low]` `[patch]` P10 — dropped the dead `slot < 0` guard in the OrderApplier CastAbility case (slot is now an unsigned wire byte).
  - defer -> DW-881 (`[low]` sim-side ground-point bounds check missing for non-UI issuers; compounds the tracked SqrDistance overflow) and DW-882 (`[medium]` in-match cast gameplay not drivable over the GDScript-only godot-mcp bridge — needs a marshallable debug seam or sandbox roster).
  - reject -> `[low]` several `CommandApplyParityTests` method names still read `...WireUnchanged` though the wire is now 12 bytes (cosmetic test-internal naming, not consumer-facing).

### 2026-08-06 — Review pass (follow-up)
- intent_gap: 0
- bad_spec: 0
- patch: 5: (high 0, medium 0, low 5)
- defer: 2: (high 0, medium 1, low 1)
- reject: 8: (high 0, medium 0, low 8)
- addressed_findings:
  - `[low]` `[patch]` P11 — `GroundPointCast_NowResolves_SpendsAndStartsCooldown`'s doc comment claimed the bare Damage leaf "resolves on the caster-fallback primary target" — the exact OPPOSITE of the shipped P3 code (`AbilityCastSystem.cs:405` sets the primary target to `-1`, never the caster). Corrected the comment AND added the missing regression assertion (`Health[caster] == 100`) so a future regression to the pre-15.11 caster-fallback (self-harm) fails the test; the test previously asserted only energy+cooldown, both of which stay true under that regression.
  - `[low]` `[patch]` P12 — added `QueuedCastAbility_OutOfRangeSlot_IsRejectedAtAppend_FailClosed`: the AppendOrder guard (`NetworkCommand.cs:596`) had no test for the out-of-range branch, so the fail-closed property (slot 4 → `4<<6=256` wraps to 0 and would slip past the dispatch-time guard onto a valid slot-0 fire) was unverified. Test proves a slot-4 queued cast on a 2-slot unit is rejected at append (ring stays empty) and dispatches no cast.
  - `[low]` `[patch]` P13 — added `SlotBitPack_HasRoomForEveryAbilitySlot` pinning `MAX_ABILITIES_PER_UNIT - 1 < (1 << (8 - ORDER_QUEUE_SLOT_SHIFT))`, so a future ability-cap bump past the 2-bit slot field fails at this test instead of silently overflowing the queued command byte into an MP desync (the shift/cap coupling was previously prose-only in the pack comment).
  - `[low]` `[patch]` P14 — added `AffinityOnNonTargetUnit_Warns_ButDoesNotReject` and `GroundPointBareLeaf_Warns_ThatItWillNoOp`: the two new non-fatal validator warnings (P8 affinity-on-non-TargetUnit; P3 GroundPoint-without-SearchArea) had zero tests exercising the warn-generation path (the shipped-content "zero warnings" test can only prove clean content stays clean). Both now assert the warning fires with `Ok` still true.
  - `[low]` `[patch]` P15 — corrected the stale `TickCommandPacket` wire-size doc comment (`orders(11 each)` / `352 bytes` → `12 each` / `384 bytes`) left behind by the 11→12 stride widening.
  - defer -> DW-883 (`[medium]` cast reticle ring radius hard-coded at 2.0u vs the ability's authored SearchArea radius — shipped nuke is radius 4, so the preview shows half the real footprint) and DW-884 (`[low]` reticle Y pinned to 0.15, ignoring the RaycastGround hit height, so the ring detaches from non-flat terrain). Both are Godot-coupled presentation not observable over the current bridge (DW-882) → Epic-10 live-verify batch.
  - reject -> Debug.Assert-compiled-out-in-Release on the queued-command-field guard (guarded by the AppendOrder range check + UnitCommand max 13 « 63, corruption requires a >63 command value that cannot exist); ground-cast self-harm being "filter-dependent" (intended WC3 friendly-fire; no Ally/Any ground AoE ships); misclick-on-empty-ground spends cost/cooldown (intended WC3 semantics; off-map is denied at the UI RaycastGround seam, never issued); `GroundPointResolvesAtPoint` warn-quality for `Sequence[Damage, SearchArea]` and false-positive on deeply-nested SearchArea (best-effort authoring hint, not load-bearing); `PendingCastTarget` carrying a raw coord for a ground cast + no defense-in-depth on a mismatched-targeting order (currently correct — only `TryCast` reads it and it branches on `ParsedTargeting`; speculative churn of committed wire code); `ground_nuke`'s Enemy filter path not uniquely tested (both the filter logic and the ground-centering are each covered; the Enemy+ground combination is marginal).

### 2026-08-06 — Review pass (pass 3)
- intent_gap: 0
- bad_spec: 0
- patch: 1: (high 0, medium 0, low 1)
- defer: 1: (high 0, medium 0, low 1)
- reject: 10: (high 0, medium 0, low 10)
- addressed_findings:
  - `[low]` `[patch]` P16 — the queued-cast bit-pack's command-side budget was pinned only by a Release-stripped `Debug.Assert` + a STALE prose comment (`UnitOrderFlags` said "UnitCommand uses values 0-13"; the enum has since grown to `CancelTrain=23`). Its sibling slot-side invariant got a test (`SlotBitPack_HasRoomForEveryAbilitySlot`) while the command side was explicitly punted ("re-check the UnitCommand ≤ 0x3F budget"). All three code-review lenses converged on the asymmetry. Added `CommandBudget_LeavesRoomForTheQueuedSlotBits` asserting every `UnitCommand` value ≤ `ORDER_QUEUE_CMD_MASK` (so a future command past 0x3F fails HERE, not in an MP checksum divergence), and corrected the stale `UnitOrderFlags` comment to the real 0-23 range with a pointer to the new pin. Pure-sim/Tier-1, no coupled surface touched. Suite 6241 green (was 6240).
  - defer -> DW-885 (`[low]` the CreationSuite affinity dropdown is offered for GroundPoint, but the validator warns and the sim ignores affinity there — a coherent-but-contradictory pair from two deliberate in-story decisions; reconciliation is a design call, Godot-coupled → Epic-10 batch).
  - reject -> (all re-raises or non-defects) reticle radius 2.0 vs authored 4 = already **DW-883**; reticle Y pinned 0.15 = already **DW-884**; in-match cast HP not bridge-observable = already **DW-882**; `target_affinity` not sim-authoritative against a modified client = by-design per intent ("a hint on the click-picker") AND strictly pre-existing (arbitrary target ids were always shippable; the mismatch cases mostly self-harm the cheater, not a desync); no cast-range-from-caster = out of scope on intent authority (intent introduces no range mechanic — a new feature, not a defect); save v3 fail-closed reject = FALSE, already covered by `SaveLoadTests.Load_OlderFormatVersion_ThrowsWithMessage` (future-proof `FormatVersion-1`); ground-cast golden non-vacuity can't distinguish damage from cooldown = the committed-golden checksum (`MatchesCommittedGolden`) + the discriminating `GroundCast_SearchArea_DamagesAtThePoint_NotTheCaster` already catch that exact regression; `FindNearest` picks a friendly building / neutral critter = FALSE, the picker (old and new) scans only the unit SoA, buildings have a separate `FindNearestEnemyBuilding` path; `slot < 0` guard removal = no reachable negative-slot caller (all callers pass a wire byte / masked shift / default 0), a deliberate P10 removal; `PendingCastPointX/Z` overloaded to carry a target id for TargetUnit = currently correct (only `TryCast` reads them, branching on `ParsedTargeting`) — a prior-pass reject re-raised, speculative churn of committed wire code; nested-`SearchArea` GroundPoint warning false-positive = best-effort authoring hint, non-fatal, prior-pass reject; online `EnqueueOrder(slot)` seam untested = a thin one-line forward (`NetworkCommand`/`LockstepManager.cs:364`) into the already-tested `UnitOrder(...,slot)` ctor, and its online-mode test harness is non-trivial — low value.

### In-Engine Gate - 2026-08-06 (pass 3 re-verification)
- surface: same reachable coupled surfaces re-driven this pass by the in-engine gate auditor — CreationSuite `AbilityEditorPanel` targeting/affinity dropdowns + visibility rules, `CastReticleBridge` boot wiring + `SetActive`/`MoveTo`, strict-loader ingest of `ground_nuke.json`/`mend_ally.json`, `CommandCardSystem` "coming soon" fence removal. (This pass's own code change — a Tier-1 test + a sim doc comment — touches NO Godot-coupled surface, so it does not invalidate the prior PASS artifacts; the auditor re-drove the surfaces anyway to re-confirm.)
- launched: `dotnet build godot/godot.csproj` -> Build succeeded, 0 errors (6 pre-existing warnings); `godot_editor_edit run` into a live boot, driven via `godot_exec` tree walk + OptionButton `item_selected` emission (no absolute-mouse click on the GDScript-only bridge).
- digest: affinity dropdown items = ["Enemy (default)","Ally","Any"], default sel 0; targeting items = ["None","Self","Target Unit","Ground Point"]; affinity-row visibility Self:hidden / Target Unit:visible / Ground Point:visible / None:hidden; GroundPoint hint = "Ground Point: the player clicks a location; the effect (e.g. a Search Area) resolves at that point." (old "coming soon" text absent); `ground_nuke.json` -> "Valid — applied to the form." reflected as Ground Point; `mend_ally.json` (carrying `"target_affinity":"Ally"`) -> "Valid — applied", Target Unit, affinity row visible reflecting "Ally"; negative arm `target_affinity:"Frenemy"` -> fail-closed reject `'Frenemy' is not a known affinity (Enemy|Ally|Any).`; `CastReticleBridge` at `/root/MainScene/@Node3D@614`, initial_visible=false, scale (2,2,2); `SetActive(true/false)` -> ring.visible true/false; `MoveTo(37.5,99,-12.25)` -> position (37.5, 0.15, -12.25) (X/Z passthrough, Y clamp 0.15 = the DW-884 finding); `ArmCastGroundTargeting` shows ring, `ArmCastTargeting(...,Enemy)` hides it; 0 editor errors at boot and after driving.
- asserted: dropdown items == authoring enums (`TargetAffinity` Enemy/Ally/Any, `AbilityTargeting` None/Self/TargetUnit/GroundPoint) — MATCH; affinity-row visibility == the TargetUnit/GroundPoint-only rule — MATCH; both JSONs load through the strict `UnmappedMemberHandling.Disallow` loader (an unmapped key hard-throws), proving `target_affinity` is genuinely wired — MATCH; unknown-affinity fail-closed reject — MATCH; `CastReticleBridge` instantiated + steer/hide correct — MATCH; "coming soon" fence gone — MATCH. In-match cast/heal HP digest remains unobtainable over the bridge (DW-882) — proven instead by the 6241-green Sim suite incl. the byte-identical ground-cast golden and the discriminating `GroundCast_SearchArea_DamagesAtThePoint_NotTheCaster` + `CastTargetPicker` Ally-excludes-caster tests.
- result: PASS

### In-Engine Gate - 2026-08-06 (follow-up review re-verification)
- surface: same reachable coupled surfaces re-driven this pass — CreationSuite `AbilityEditorPanel` affinity/targeting dropdowns, `CastReticleBridge` boot wiring + arm/reset/MoveTo, strict-loader ingest of both new ability JSONs, `CommandCardSystem` "coming soon" fence removal.
- launched: `dotnet build godot/godot.csproj` -> Build succeeded, 0 errors (6 pre-existing warnings); `godot_editor_edit run` to boot a live match, driven via `godot_exec` tree walk + OptionButton `item_selected` emission (no absolute-mouse click on the GDScript-only bridge).
- digest: `CastReticleBridge` present at `/root/MainScene/@Node3D@614` = {albedo:(1.0,0.55,0.15,0.85), mesh:TorusMesh, inner:0.85, outer:1.0, scale:(2.0,2.0,2.0), shading:Unshaded, visible:false}; arm/reset drove ring visible false->true->false; `MoveTo(37.5,99.0,-12.25)` -> ring.position (37.5, 0.15, -12.25) (X/Z passthrough, Y clamped 0.15 — the DW-884 finding). Affinity dropdown items = ["Enemy (default)","Ally","Any"]; targeting items = ["None","Self","Target Unit","Ground Point"]; affinity-row visibility None:false/Self:false/TargetUnit:true/GroundPoint:true; reset-on-hide Ally->Self reset sel 1->0; both `ground_nuke` and `mend_ally` in the loaded-abilities snapshot (mend_ally carrying "target_affinity":"Ally") — parse under `UnmappedMemberHandling.Disallow`; 0 editor errors at boot and after driving.
- asserted: dropdown items == authoring enums (`TargetAffinity` Enemy/Ally/Any, `AbilityTargeting` None/Self/TargetUnit/GroundPoint) — MATCH; affinity-row visibility == the TargetUnit/GroundPoint-only rule — MATCH; both JSONs load through the strict `Disallow` loader (an unmapped key hard-throws) proving `target_affinity` is genuinely wired — MATCH; `CastReticleBridge` instantiated + steer-and-hide correct — MATCH; "coming soon" fence gone — MATCH. In-match cast/heal HP digest remains unobtainable over the bridge (DW-882) — proven instead by the 6240-green Sim suite incl. the byte-identical ground-cast golden and the discriminating `GroundCast_SearchArea_DamagesAtThePoint_NotTheCaster` + `CastTargetPicker` Ally-excludes-caster tests.
- result: PASS

### In-Engine Gate - 2026-08-06
- surface: CreationSuite `AbilityEditorPanel` targeting/affinity controls (DW-286 authoring) + `CommandCardSystem` "coming soon" fence removal (DW-280 UI) + match-boot wiring of `CastReticleBridge` + strict-loader ingest of both new ability JSONs. (The in-match cast/heal/reticle gameplay is NOT drivable over the current bridge — see the note below.)
- launched: `dotnet build godot/godot.csproj` -> Build succeeded, 0 warnings, 0 errors (fresh `ProjectChimera.dll`); then `godot_editor_edit run` to boot the game, driven via `godot_exec` tree walk + OptionButton `item_selected` signal emission (no absolute-mouse click on the bridge).
- digest: affinity dropdown items = ["Enemy (default)", "Ally", "Any"]; targeting dropdown items = ["None", "Self", "Target Unit", "Ground Point"]; affinity-row visibility driven live: Self->hidden, GroundPoint->shown, TargetUnit->shown, None->hidden; clear-on-hide: Ally selected (sel=1) then targeting switched to Self -> affinity reset (sel=0); targeting hint text = "Ground Point: the player clicks a location; the effect (e.g. a Search Area) resolves at that point.", old "cast support is pending / coming soon" text absent everywhere; both `ground_nuke` and `mend_ally` appear in the editor's loaded-abilities snapshot (mend_ally carrying "target_affinity":"Ally") — i.e. both parse under `UnmappedMemberHandling.Disallow`; `CastReticleBridge` present in the live tree at `/root/MainScene/@Node3D@614`; 0 editor errors at boot and 0 new errors after all driving. Sim entities are Godot-free SoA (not scene nodes) and `MainScene._world`/`_host` are private, so no `godot_runtime_state` HP digest of a cast is obtainable over the GDScript-only bridge.
- asserted: dropdown items == authoring enums (`TargetAffinity` = Enemy/Ally/Any; `AbilityTargeting` = None/Self/TargetUnit/GroundPoint) — MATCH; affinity-row visibility == the intended TargetUnit/GroundPoint-only rule — MATCH; both new ability JSONs load through the strict `Disallow` loader, which proves the `target_affinity` field is genuinely wired (an unmapped key hard-throws and drops the ability) — MATCH; the DW-280 "[ground-cast: coming soon]" fence is gone — MATCH; `CastReticleBridge` instantiated at boot — MATCH; 0 errors — MATCH.
- result: PASS

**In-engine gate — NOT driven (honest residual, DW-882):** the literal player cast gameplay — press a GroundPoint card, click ground, watch the r4 60-Magic AoE land; press the Ally heal, click a wounded friendly, watch it gain 50 HP — could not be driven or read in the running game. Empirically established this session: the godot-mcp bridge executes GDScript only, and while it can call marshallable C# methods (`MainScene.CountFaction(1)=2` verified), it cannot reach the pure-C# `EntityWorld` SoA, cannot construct `Fixed` to call `IssueCastAbilityGroundCommand`, and neither ability is on any faction roster, so no command card renders them. That gameplay slice is instead proven **through the real engine's simulation** by the byte-identical `ground-cast-scenario.golden.txt` (300-tick deterministic replay of the full wire->PendingCast->GroundPoint branch->SearchArea-centered-on-the-point->damage path) and by the discriminating Tier-1 tests `GroundCast_SearchArea_DamagesAtThePoint_NotTheCaster` and the `CastTargetPicker` Ally-excludes-caster suite. The un-observed remainder is only the Godot-coupled input glue (card->arm->mouse-click->issue) and the reticle-follow, tracked in DW-882 for the Epic-10 live-verify batch.


## Auto Run Result

Status: done
Blocking condition: none

**Change:** Third (follow-up) review pass on the already-converged Story 15.11. Five review lenses (adversarial, edge-case, verification-gap, intent-alignment, in-engine gate) re-ran on the full baseline→HEAD diff. The in-engine gate re-drove the reachable coupled surfaces and returned PASS. Triage: 1 low patch, 1 low defer (DW-885), 10 rejects (all re-raises of already-tracked DW-882/883/884, by-design-per-intent items, or claims verified false against source). The single patch (P16) closed the one genuinely-new, universally-flagged gap: the queued-cast bit-pack's command-side budget (every `UnitCommand` ≤ `ORDER_QUEUE_CMD_MASK`/0x3F) was pinned only by a Release-stripped `Debug.Assert` plus a stale prose comment, while its sibling slot-side invariant already had a dedicated test. Added the missing pinning test and corrected the stale `UnitOrderFlags` comment (0-13 → the real 0-23, max `CancelTrain`). No coupled surface was touched by this pass's code change.

**Files changed:**
- `godot/ProjectChimera.Sim.Tests/Multiplayer/Story1511WireAndAffinityTests.cs` — added `CommandBudget_LeavesRoomForTheQueuedSlotBits`, asserting every `UnitCommand` value fits under `ORDER_QUEUE_CMD_MASK` so a future command past 0x3F fails at the test rather than corrupting a queued-cast byte into an MP desync.
- `godot/src/Core/EntityWorld.cs` — corrected the stale `UnitOrderFlags` XML doc comment (`UnitCommand uses values 0-13` → currently 0-23, max `CancelTrain`=23, all ≤ 0x3F) and pointed it at the new pinning test. Doc-only; no behavior change.
- `_bmad-output/implementation-artifacts/deferred-work.md` — appended DW-885 (GroundPoint affinity dropdown offered-but-ignored; design-reconciliation deferral).
- `_bmad-output/implementation-artifacts/sprint-status.yaml` — (carried from the story dev pass; unchanged this review).

**Verification:**
- `dotnet build godot/godot.csproj` → Build succeeded, 0 errors (6 pre-existing warnings).
- `dotnet test godot/ProjectChimera.Sim.Tests` → **6241 passed**, 0 failed, 1 skipped (was 6240 before P16's new test). Existing goldens unchanged (no golden movement); `SimChecksum.AlgoVersion` still 24.
- In-Engine Gate (pass 3): re-driven over the godot-mcp bridge — affinity/targeting dropdowns == authoring enums, both new ability JSONs ingest through the strict `Disallow` loader, unknown-affinity fail-closed reject, `CastReticleBridge` boot + arm/steer/hide correct, "coming soon" fence gone → **PASS**. In-match cast/heal HP digest remains unobtainable over the GDScript-only bridge (DW-882), proven instead by the byte-identical ground-cast golden + discriminating Tier-1 tests.

**Residual risks:** Low. The pass converged (`followup_review_recommended: false` — 1 low patch, score 1 < 5). Residual observability debt is unchanged and already tracked: DW-882 (in-match cast not bridge-drivable), DW-883/884 (reticle radius/height presentation), DW-885 (GroundPoint affinity UI/validator reconciliation) — all Godot-coupled and folded into the Epic-10 live-verify batch.
