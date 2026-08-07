---
title: 'Story 15.13 — Effect vocabulary completion: Teleport + presentation leaves (DW-248)'
type: 'feature'
created: '2026-08-07'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: true # patched-severity score = 3×4(med) + 1×3(low) = 15 ≥ 5
baseline_revision: '90bade8a4c869f3cf2d6762d690f4f3841163a73'
context: ['{project-root}/godot/CLAUDE.md']
warnings: ['oversized']
---

<intent-contract>

## Intent

**Problem:** The one closed effect vocabulary (AR-8) is missing the four leaves Story 2.1 reserved and carved off (DW-248): a sim-mutating `Teleport` and the three presentation leaves `PlayVfx` / `PlaySound` / `ShakeScreen`. Creators cannot author a blink/relocate, a visual burst, an ability sound, or a screen shake as effect graph nodes.

**Approach:** Add four `sealed LeafEffect` subclasses in `godot/src/Effects/`. `Teleport` deterministically relocates the CASTER through the existing placement path (folds into `SimChecksum` via `Position`, like any move). The three presentation leaves are **checksum-neutral**: they push a `CombatEvent` onto the presentation-only `CombatEventQueue` (never a `SimChecksum` input) carrying an authored `CombatFeedbackProfile`, which the existing `CombatFeedbackBridge` / `AudioManager` drainers render. Wire each through the closed pipeline: JSON converter, canonical fold, validator, and the closedness/fold/position guard tests.

## Boundaries & Constraints

**Always:**
- Every new node is a `sealed` `LeafEffect` in namespace `ProjectChimera.Effects`, with **public readonly FIELDS only** (no properties — `EffectFoldCompletenessTests` forbids public instance properties on nodes), and no `float`/`double`/`object`/`Delegate`/Godot field type (`EffectVocabularyTests` closedness scan).
- Presentation leaves mutate **zero** folded sim state — their only effect is a `ctx.Events?.Push(...)`. `CombatEventQueue` is documented as excluded from `SimChecksum`; keep it that way.
- `Teleport` writes `EntityWorld.Position` exactly once and is **placement-class** (a blink bypasses walls between origin and destination), so it is added to the `PositionWriterGuardTests.Sanctioned` table with count 1 + justification — it must NOT route through `CheckedStep.Resolve` (that swept helper would stop the blink at the first wall).
- After moving, `Teleport` re-establishes entity consistency exactly like `EntityWorld.Create` / MovementSystem arrival: set `PrevPosition = dest`, `Velocity = Zero`, re-sample `Elevation = SampleElevation(dest.X, dest.Z)` (a folded SoA — stale = desync), clear the `Moving` flag, set `MoveTarget = dest`, and cancel an in-progress move to `CommandState.Idle` so `FlowFieldBridge` self-clears the stale field.
- Each new kind gets an explicit `CanonicalFold.MixEffect` arm (never the DW-449 reflection default) folding `kind` + every *semantic* field. Presentation payload (`CombatFeedbackProfile`, which carries `float`) is **consciously excluded** from the fold — it cannot be folded deterministically and is presentation-only, matching `CombatFeedbackProfile`'s documented hash exclusion.
- Determinism: process nothing out of ascending-id order; `Fixed` only; no wall-clock/RNG.

**Block If:**
- Adding these leaves is found to move an existing golden or an existing content/canonical hash (it must not — new fold arms are dead for shipped content, no `EffectCaps`/`RulesetHash` constant changes, and no recorded scenario authors the new kinds). If a golden or a hash `AlgoVersion` would move, HALT — that signals an unintended coupling, not this story's scope.

**Never:**
- No new composition node (the count-3 assertion in `EffectVocabularyTests` must stay green); these are all leaves.
- No `EffectCaps` / `RulesetHash` change — no structural cap is added, so `RulesetHash.AlgoVersion` stays 3.
- Do not build a general VFX/particle asset system or a screen-shake curve editor — reuse `CombatFeedbackProfile.HitFlash` (pooled flash), `.ImpactSoundId` (AudioManager key), `.Shake` (camera `SetShake`). Presentation ids resolve graceful-silent when absent.
- Do not make `Teleport` displace a *non-caster* target (a "hook"/"yank") or continue the unit's prior order — out of scope; the caster moves and stops.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Blink (ground cast) | `teleport` leaf, GroundPoint cast, `HasTargetPoint=true` | Caster's `Position`→`TargetPoint` (X,Z; Y=0), `Elevation` re-sampled, movement reset; `SimChecksum` changes | No error |
| Charge (unit cast) | `teleport`, TargetUnit cast, live `PrimaryTargetId≠caster`, no target point | Caster's `Position`→ target's position, reset as above | No error |
| Teleport no destination | `teleport`, Self cast (`!HasTargetPoint`, `PrimaryTargetId==caster`) | No-op (caster stays); no checksum change | Graceful no-op |
| Teleport dead caster | `!IsAlive(CasterId)` | No-op | Guarded no-op |
| PlaySound | `play_sound` w/ `feedback.impact_sound` | `CombatEvent(PlaySound)` pushed at event pos; AudioManager plays key (graceful-silent if asset absent); no checksum change | Empty/absent id ⇒ silent |
| PlayVfx | `play_vfx` w/ `feedback.hit_flash` | `CombatEvent(PlayVfx)`; bridge spawns pooled flash from spec; no checksum change | Null flash ⇒ default flash |
| ShakeScreen | `shake_screen` w/ `feedback.shake` | `CombatEvent(ShakeScreen)`; bridge calls camera `SetShake(dur,strength)`; no checksum change | Null shake ⇒ no shake |
| Presentation leaf, null Events sink (bare test) | `ctx.Events == null` | No-op; leaf never touches sim state | Guarded no-op |

</intent-contract>

## Code Map

- `godot/src/Effects/EffectNode.cs` -- `LeafEffect` base (inherit; `RequireTag` gate applied by executor before `Apply`).
- `godot/src/Effects/DirectHpDeltaEffect.cs` -- sealed-leaf structural template to mirror.
- `godot/src/Effects/EffectContext.cs` -- carries `World`, `CasterId`, `PrimaryTargetId`, `TargetPoint`/`HasTargetPoint`, `Events`. No change.
- `godot/src/Effects/EffectExecutor.cs` -- generic `case LeafEffect leaf` already dispatches new sim/presentation leaves; **no change**.
- `godot/src/Effects/AbilityCastSystem.cs:395-489` -- shows GroundPoint cast sets `PrimaryTargetId=-1`, `TargetPoint`=click, `Events` sink; `feedbackPos` pattern (line 488) to mirror for presentation-leaf event position. Reference only.
- `godot/src/Core/EntityWorld.cs` -- `Position`/`PrevPosition`/`Velocity`/`MoveTarget`/`Elevation`/`Flags` SoA; `SampleElevation(x,z)`; `Create()` reset sequence + `CommandState=Idle` precedent.
- `godot/src/Core/Definitions/EffectNodeJsonConverter.cs` -- hand-rolled converter: `Kind*` consts, `ReadNode` switch, `WriteNode` switch, `RejectUnknownProperties`, `ReadRequireTag`, `ReadFixed`.
- `godot/src/Core/Definitions/CanonicalFold.cs:75-130` -- `MixEffect` explicit per-kind arms.
- `godot/src/Core/Definitions/CombatFeedbackProfile.cs` -- presentation DTO (`HitFlash`/`ImpactSoundId`/`Shake`), hash-excluded, Godot-free.
- `godot/src/Combat/CombatEventQueue.cs` -- `CombatEventType` enum, `IsAmbient`, `Push(type,pos,feedback)`; not folded.
- `godot/src/UI/CombatFeedbackBridge.cs` -- per-frame drain + `switch(evt.Type)`; owns `Clear()`; `ApplyShake`/flash pool.
- `godot/src/UI/AudioManager.cs:122-158` -- profile-first `fb.ImpactSoundId` play via `ResolveOverrideStream`; fallback switch.
- `godot/ProjectChimera.Sim.Tests/Effects/EffectVocabularyTests.cs` -- closedness scan (auto-covers new sealed leaves; must stay green).
- `godot/ProjectChimera.Sim.Tests/Validation/EffectFoldCompletenessTests.cs` -- `FoldedFieldsByKind`; add entries + an `ExcludedFieldsByKind` concept (foreshadowed at its line ~35).
- `godot/ProjectChimera.Sim.Tests/Meta/PositionWriterGuardTests.cs` -- `Sanctioned` table; add `Effects/TeleportEffect.cs`.
- `godot/src/Core/Definitions/AbilityValidator.cs` -- `WalkGraph`; optional warnings for inert presentation leaves.

## Tasks & Acceptance

**Execution:**
- `godot/src/Effects/TeleportEffect.cs` -- NEW `sealed class TeleportEffect : LeafEffect` (only inherited `RequireTag`). `Apply`: return if `!IsAlive(CasterId)`; compute `dest` = `HasTargetPoint` ? `new FixedVec3(TargetPoint.X, Fixed.Zero, TargetPoint.Z)` : (`IsAlive(PrimaryTargetId) && PrimaryTargetId!=CasterId` ? `Position[PrimaryTargetId]` : return no-op); then `world.Position[CasterId]=dest`, `PrevPosition=dest`, `Velocity=Zero`, `Elevation=SampleElevation(dest.X,dest.Z)`, clear `Moving` flag, `MoveTarget=dest`, `CommandState[CasterId]=UnitCommand.Idle`. -- the reserved sim relocation; single `Position` write, placement-class.
- `godot/src/Effects/PlayVfxEffect.cs`, `PlaySoundEffect.cs`, `ShakeScreenEffect.cs` -- NEW sealed leaves, each with `public readonly CombatFeedbackProfile? Feedback` (+ inherited `RequireTag`). `Apply`: resolve `pos` (mirror `AbilityCastSystem` line 488: `HasTargetPoint?TargetPoint : IsAlive(PrimaryTargetId)?Position[PrimaryTargetId] : IsAlive(CasterId)?Position[CasterId] : Zero`), then `ctx.Events?.Push(CombatEventType.PlayVfx|PlaySound|ShakeScreen, pos, Feedback)`. No sim mutation. -- checksum-neutral presentation dispatch.
- `godot/src/Combat/CombatEventQueue.cs` -- append `PlayVfx`, `PlaySound`, `ShakeScreen` to `CombatEventType` (after `TrainingComplete`, with the golden-safe append comment); add all three to `IsAmbient` (=> true: visual/audio juice, individually droppable, must not draw on the notification reserve). -- new presentation cue types.
- `godot/src/UI/CombatFeedbackBridge.cs` -- add `case CombatEventType.PlayVfx:` (spawn a pooled flash from `evt.Feedback?.HitFlash ?? default`) and `case CombatEventType.ShakeScreen:` (`ApplyShake(evt.Feedback?.Shake)`, null ⇒ skip) to the drain `switch`. `PlaySound` needs no visual case. -- render the visual/shake cues.
- `godot/src/UI/AudioManager.cs` -- ensure the profile-first block plays `PlaySound` (its `evt.Feedback.ImpactSoundId`) and give `PlaySound` a sensible `VolumeFor` default; `PlayVfx`/`ShakeScreen` stay audio-silent unless they carry an id. -- play the ability sound.
- `godot/src/Core/Definitions/EffectNodeJsonConverter.cs` -- add `KindTeleport="teleport"`, `KindPlayVfx="play_vfx"`, `KindPlaySound="play_sound"`, `KindShakeScreen="shake_screen"` consts; `ReadNode` cases (Teleport: `RejectUnknownProperties(el,path,"kind","require_tag")`; presentation: allow `"kind","feedback","require_tag"`, deserialize `feedback` via `fbEl.Deserialize<CombatFeedbackProfile>(options)` when present); matching `WriteNode` arms (round-trip). -- authoring parse/serialize.
- `godot/src/Core/Definitions/CanonicalFold.cs` -- add explicit `MixEffect` arms: `teleport`→ mix `"teleport"`+`RequireTag`; each presentation kind→ mix its kind string + `RequireTag` only (`Feedback` deliberately NOT folded — presentation-only, float-bearing, hash-excluded). -- keep new kinds off the DW-449 default arm; additive (no existing hash moves).
- `godot/src/Core/Definitions/AbilityValidator.cs` -- (light) in `WalkGraph`, warn via the existing `Warn` channel when a presentation leaf carries no usable payload (e.g. `play_sound` with null/empty `impact_sound`) — an inert authored cue. No hard reject. -- authoring feedback.
- `godot/ProjectChimera.Sim.Tests/Validation/EffectFoldCompletenessTests.cs` -- add `FoldedFieldsByKind` entries: `TeleportEffect`→`{"RequireTag"}`, each presentation kind→`{"RequireTag"}`; introduce `ExcludedFieldsByKind` (presentation kinds → `{"Feedback"}`) and update `AssertFieldsClassified` so a public field is OK when in folded ∪ excluded (stale-check both). -- keep the completeness guard green with a conscious exclusion.
- `godot/ProjectChimera.Sim.Tests/Meta/PositionWriterGuardTests.cs` -- add `["Effects/TeleportEffect.cs"] = (1, "Teleport blink: authored/instant PLACEMENT, not a swept step — a blink deliberately bypasses walls between origin and destination, so CheckedStep.Resolve must NOT apply. Destination validity is the ground-cast RaycastGround gate (MVP).")`. -- sanction the one new position writer.
- `godot/ProjectChimera.Sim.Tests/Effects/TeleportAndPresentationLeafTests.cs` (or split) -- NEW Tier-1 xUnit tests: Teleport moves caster to `TargetPoint` deterministically (two identical runs, same `Position`); no-destination self-cast is a no-op; `Elevation` re-sampled; presentation leaves push exactly one `CombatEvent` of the right type carrying the profile and mutate no `Health`/`Position`; a null `Events` sink is a safe no-op; round-trip each kind through `EffectNodeJsonConverter` (read→write→read). -- prove behavior + determinism (net-new, no golden file).

**Acceptance Criteria:**
- Given a GroundPoint ability whose effect is `{"kind":"teleport"}`, when the caster casts it at a ground point, then the caster's `Position` becomes that point (Y=0), `Elevation` is re-sampled, velocity/move-flag are cleared, and the tick `SimChecksum` reflects the new position deterministically across two identical runs.
- Given a TargetUnit ability with a `teleport` effect and a live target, when cast, then the caster relocates to the target's position; given a Self cast with no target point, the leaf is a no-op.
- Given an ability whose effect is `{"kind":"play_sound","feedback":{"impact_sound":"x"}}` (or `play_vfx`/`shake_screen`), when cast, then exactly one `CombatEvent` of the matching type is enqueued carrying the profile, the drainers render/play it, and the tick `SimChecksum` is byte-identical to the same cast with the presentation leaf removed.
- Given the full suite, when Tier-1 runs, then `EffectVocabularyTests`, `EffectFoldCompletenessTests`, `PositionWriterGuardTests`, and all determinism/hash tests pass, and no existing golden was re-recorded and no hash `AlgoVersion` changed.
- Given the diff touches `godot/src/UI/**`, when the story is completed, then the In-Engine Gate artifact is appended (a real ability authored with these leaves, cast in a running match, with a captured runtime-state digest proving the caster's position moved and the cue fired).

## Design Notes

**Why the caster moves (not the target).** For a GroundPoint cast the machinery sets `PrimaryTargetId=-1` and puts the click in `TargetPoint` (`AbilityCastSystem:405,442`); for a TargetUnit cast `PrimaryTargetId` is the unit and there is no `TargetPoint`. The single rule "move the CASTER to `TargetPoint` if present, else to the primary target's position" is the only reading the closed vocabulary supports for both the flagship blink (self→ground) and a charge (self→target), and it never needs a valid `PrimaryTargetId` for the ground case. "Displace the target" is not expressible in one cast under the current targeting model and is explicitly out of scope.

**Why presentation payload is fold-excluded, not folded.** The three cues carry a `CombatFeedbackProfile` (float-bearing, presentation-only). It cannot be folded deterministically (that is the whole reason for the `float` ban), and it is already excluded from `SimChecksum`/canonical hash everywhere else. Folding only `kind`+`RequireTag` keeps peers agreeing on the *shape* of the effect graph (so a presence/kind divergence is still caught) while presentation divergence stays sim-irrelevant. This is the `EXCLUDED` case the fold-completeness test's own header comment anticipates.

**No re-baseline.** Nothing recorded authors these kinds, so no golden moves; `Teleport` only perturbs `SimChecksum` for scenarios that use it (none shipped). New fold arms are dead for shipped content, and no `EffectCaps` cap changed, so no content/canonical-hash `AlgoVersion` bump. Per the batch rule this is a build, isolated to its own story; re-record only if a golden actually moves (it will not).

## Verification

**Commands:**
- `dotnet build godot/godot.csproj` -- expected: succeeds; banned-API analyzer clean (no `float`/`System.Random`/Godot in the new sim leaves).
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: all green, including the new Teleport/presentation tests, `EffectVocabularyTests`, `EffectFoldCompletenessTests`, `PositionWriterGuardTests`, and every SimChecksum/canonical-hash golden UNCHANGED (no re-record).

**Manual checks (In-Engine Gate — REQUIRED, diff touches `godot/src/UI/**`):**
- Author a temporary ability (or reuse a test faction JSON) whose effect graph is a `sequence` of `teleport` + `play_vfx`/`play_sound`/`shake_screen`, launch a match over the godot-mcp bridge, cast it, and capture a `godot_runtime_state` digest proving (a) the caster entity's position changed to the cast point (compare to the click coords), and (b) the cue fired (flash pool count / camera shake state / audio). Append the `### In-Engine Gate` block with the verbatim digest and expected-vs-observed numbers.

## Review Triage Log

### 2026-08-07 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 7: (high 0, medium 4, low 3)
- defer: 2: (high 0, medium 1, low 1)
- reject: 4: (high 0, medium 0, low 4)
- addressed_findings:
  - `[medium]` `[patch]` P1 — the four new `CanonicalFold` arms had no hash-sensitivity pins. Added to `CanonicalModelHashEffectFoldTests`: per-kind `RequireTag` folds for all four new kinds; a pairwise kind-discriminator distinctness check (catches a copy-paste like ShakeScreen mixing `"play_vfx"`); and the checksum-neutrality teeth — two same-kind presentation leaves with DIFFERENT `Feedback` fold to the SAME hash, proving the float-bearing payload is excluded from the fold value, not merely classified as excluded.
  - `[medium]` `[patch]` P2 — Teleport's defining wall-bypass (placement, not swept) was untested (all tests used a null pathability grid). Added a test installing a blocked `PathabilityGrid` with a full wall between origin and destination, asserting the caster lands PAST the wall — a swept `CheckedStep` move would hard-stop.
  - `[medium]` `[patch]` P3 — the determinism-critical Elevation re-sample was only tested on a flat grid (every sample Zero). Added a non-flat `ElevationGrid` test asserting `Elevation == SampleElevation(dest)` (and not the origin's height) after teleport.
  - `[medium]` `[patch]` P5 — the leaves were only exercised via a hand-built `EffectContext`, never the production cast path. Added a headless integration test driving `AbilityCastSystem` on a real GroundPoint cast of a `TeleportEffect`, asserting the caster relocated to the cast point (exercises the real `PrimaryTargetId = -1` / `TargetPoint` plumbing).
  - `[low]` `[patch]` P4 — the new AbilityValidator inert-cue warnings (play_sound/shake_screen) had zero coverage. Added both-direction tests: id-less play_sound and shake-less shake_screen warn; a populated play_sound, a populated shake_screen, and any play_vfx produce zero warnings.
  - `[low]` `[patch]` P6 — the Teleport charge branch copied the target's `Position` wholesale (incl. Y) while the ground branch flattened Y to Zero. Flattened Y in the charge branch for consistency; added a Y==0 assertion. (Harmless today — shipped `Position.Y` is invariant-Zero — a latent-determinism fix, no golden moves.)
  - `[low]` `[patch]` P7 — `EffectNodeJsonConverter.ReadFeedback` caught only `JsonException`, so a nested-deserialize failure of another type escaped the loader's located-error contract. Broadened to rethrow any exception as a located `JsonException($"{path}.feedback: …")`.
  - defer → DW-891 (`[medium]` Teleport writes Position with no sim-side destination-pathability check — a trigger/online/charge blink can embed a unit in a wall/building/off-map; MVP-scoped per the spec, same class as the SaveGameState restore writer) and DW-892 (`[low]` no validator warning for a Teleport leaf that can never fire under its ability's targeting mode — a Self/None teleport or a require_tag'd GroundPoint teleport silently no-ops after spending cost+cooldown).
  - reject → charge lands the caster on the target's exact cell + Idle (the separation system exists precisely to resolve overlaps; charge-to-attack polish is out of intent); presentation leaves carrying a full `CombatFeedbackProfile` can cross-talk channels (a play_vfx with impact_sound also plays sound) (presentation flexibility, sim-irrelevant, no intent constraint); a presentation leaf under a SearchArea fans out N ambient events (working as designed — the cues are consciously classified ambient/individually-droppable for exactly this); the architecture table's "relocate **target** (Blink)" reading (resolved to caster-blink by the "(Blink)" self-teleport semantics + the machinery's `PrimaryTargetId = -1` on the only mode with a destination point — displacing a non-caster target is not expressible in one cast today and is documented out of scope).

### In-Engine Gate — 2026-08-07 (second pass) — PASS (DW-882 debug seam landed; gate driven and observed)

The blocking condition below was resolved by BUILDING the DW-882 seam rather than waiving the gate. Alec's call,
2026-08-07: "land the DW-882 debug seam first." The gate was then driven end-to-end over the godot-mcp bridge.

- surface: `CombatFeedbackBridge._Process` (new `PlayVfx`/`ShakeScreen` drain arms) and `AudioManager` (`PlaySound`
  routing) — plus the sim-side `TeleportEffect` placement write.
- launched: `dotnet build godot/godot.csproj` → Build succeeded, 0 errors. `godot_editor_edit run` → `/root/MainScene`
  live; `has_method("_mcp_state") = true`, `has_method("DebugSimJson") = true` (the seam is reachable from GDScript —
  the exact wall DW-882 recorded).
- method: `EnterPlayMode()` → `DebugGrantAbility(0, 2, "blink_strike")` → **`godot_game_time freeze`** → read pre-state →
  `DebugCastGround(0, 2, -12.0, 7.0)` → `step frames=3` → read post-state. The freeze is load-bearing: in a free-running
  match the caster walks off the blink point within a few hundred ms, which is why the FIRST attempt at this gate read a
  moved-on position and looked like a failed cast. It was not — all three casts had landed.
- content: `godot/resources/data/abilities/blink_strike.json` — a real GroundPoint ability whose graph is a `sequence` of
  `teleport` + `play_vfx` + `play_sound` + `shake_screen` (the same registry-present / roster-absent posture Story 15.11
  shipped `ground_nuke` and `mend_ally` with). Granted to a live P1 unit at runtime, cast through the production
  `SelectionSystem.IssueCastAbilityGroundCommand` → `OrderApplier` → `AbilityCastSystem.TryCast` path — NOT a test double.

- digest (verbatim, caster entity 0, expected vs observed):

      pre  : {"x": -21.4932861328125, "z": 14.1039733886719, "command": "Idle"}
      cast : DebugCastGround(0, 2, -12.0, 7.0)  → 0 (issued)
      post : {"x": -12.0, "z": 7.0, "raw_x": -786432, "raw_z": 458752,
              "moving": false, "command": "Idle", "elevation": 0.0}
      bridge (CombatFeedbackBridge._mcp_state):
             {"cue_counts": {"AbilityCast": 3, "PlayVfx": 3, "PlaySound": 3, "ShakeScreen": 3, "TrainingComplete": 5},
              "flash_spawns": 3, "flashes_active": 1, "last_cue_x": -12.0, "last_cue_z": 7.0,
              "shake_applies": 3, "last_shake_dur": 0.25, "last_shake_str": 0.349999994039536, "camera_wired": true}
      audio  (AudioManager._mcp_state):
             {"override_plays": 3, "missing_streams": 3, "last_sound_id": "blink_strike",
              "last_sound_type": "PlaySound", "streams_loaded": 7}
      scene  : the one visible flash MeshInstance3D under the bridge sits at global_position (-12.0, 0.5, 7.0)
               — the cast point plus the bridge's documented +0.5 Y lift.

- asserted: **Teleport** — the caster's `Position` became the cast point EXACTLY: `raw_x = -786432 = -12 × 65536` and
  `raw_z = 458752 = 7 × 65536`, i.e. the two `Fixed` raws are exact, not float-approximate; `Elevation` re-sampled to 0.0
  (flat default map), `moving` cleared, `CommandState` Idle — the full `Create`/arrival reset the spec specified.
  **PlayVfx** — 3 cue drains, 3 flash spawns, one flash alive in the scene AT the cast point (node-state, not inference).
  **ShakeScreen** — 3 applies carrying `(0.25, 0.35)`, byte-for-byte the authored `shake` block in `blink_strike.json`,
  handed to a wired camera. **PlaySound** — 3 profile-first routes carrying `impact_sound = "blink_strike"`;
  `missing_streams: 3` records that the clip asset is not authored yet, so it played SILENT — the documented
  graceful-silent contract, and the tap is what makes that distinguishable from "the leaf never routed".
  Counts read 3 because three casts were issued during the session (two before the freeze was introduced); every one of
  them fired all four leaves, which is itself the repeatability evidence.
- screenshot: `godot_editor_read screenshot_game` with the overlay CanvasLayers hidden shows the live battlefield (blue
  P1 left, red P2 right) with the cue dot at the blink point. The camera reframe did not take while frozen (no new frame
  renders under freeze), so the flash is a few pixels rather than a close-up — the node-state and tally evidence above is
  the load-bearing proof, per this project's "verify against numbers, not against how the screen looks" gate rule.
- result: **PASS** — the teleport and all three presentation cues were driven and observed in the running game.

### In-Engine Gate — 2026-08-07 (first pass) — NOT SATISFIED (blocking environment condition, DW-882) — SUPERSEDED by the PASS above
- surface: the two touched coupled surfaces are `CombatFeedbackBridge._Process` (new `PlayVfx`/`ShakeScreen` drain cases) and `AudioManager` (`PlaySound` routing) — per-frame drain `switch` arms that act ONLY on the three new `CombatEventType` values, which are enqueued solely by the new C# presentation leaves during a real ability cast. Both new arms dispatch to pre-existing, already-verified primitives (`SpawnFlashFromSpec` / `ApplyShake` / `ResolveOverrideStream`); the diff adds no new rendering primitive.
- launched: `dotnet build godot/godot.csproj` → Build succeeded, 0 errors. Bridge reachable, editor open.
- BLOCKED: the load-bearing assertions — the caster's `EntityWorld.Position` moving to the cast point (Teleport), and the flash/shake/sound firing — are UNOBTAINABLE over the godot-mcp bridge. (1) No ability JSON under `godot/resources/**` authors `teleport`/`play_vfx`/`play_sound`/`shake_screen`, so no castable roster ability reaches the surface. (2) The GDScript-only bridge cannot construct the `Fixed` ground-cast args to drive a cast, nor read the private C# `EntityWorld` SoA held in `MainScene._world`/`_host` to observe the result. This is exactly DW-882 (status: open); no debug seam has landed since it was filed on Story 15.11. Unlike 15.11, this diff changed no boot-observable coupled surface (no editor dropdown, no new node), so there is no reachable surface to anchor a truthful partial PASS, and the gate contract forbids recording a passing artifact without a real captured digest.
- substitute proof (Tier-1, context for the operator — not a gate substitute): the new behavior is fully covered by the 6308-green suite — `TeleportAndPresentationLeafTests` (ground blink + charge relocate the caster deterministically; wall-bypass placement; non-flat Elevation re-sample; dead-caster/no-destination no-ops; each presentation leaf pushes exactly one correctly-typed non-folded event carrying the profile and mutates zero sim state; null-sink safe; production `AbilityCastSystem` GroundPoint cast relocates the caster; JSON round-trips) and `CanonicalModelHashEffectFoldTests` (the Feedback-exclusion / checksum-neutrality pins).
- result: BLOCKED — requires a human `/godot-verify`: author a temporary ability whose effect graph is a `sequence` of `teleport` + `play_vfx`/`play_sound`/`shake_screen` onto a sandbox roster, cast it in a running match, and capture the position-move + cue digests; or land the DW-882 debug seam so this becomes automatable.

## Auto Run Result

Status: done

**Resolution of the CRITICAL escalation (2026-08-07, interactive session).** The dev session below halted correctly: the
in-engine gate applied and could not be satisfied over the GDScript-only bridge (DW-882). Alec's decision was to LAND THE
SEAM rather than waive the gate — so `godot/src/Core/MainSceneDebugSeam.cs` was built (see the DW-882 ledger entry for the
full surface), the gate was then driven and observed for real (see the PASS block above), and one genuine defect the gate
surfaced was fixed: `AbilityValidator`'s Story-15.11 GroundPoint rule knew only `SearchArea` as point-consuming, so it
warned on every GroundPoint ability built from this story's own vocabulary and advised wrapping a teleport in a
`SearchArea` — advice that would have broken the blink. Widened to a named `ConsumesTargetPoint` predicate covering the
four new leaves, with both-direction tests (the point-aware leaves stay silent; a bare damage root still warns). Caught by
the pre-existing `EveryShippedAbility_ValidatesWithZeroWarnings` guard the moment `blink_strike.json` landed — a case for
shipping demo content alongside a new vocabulary rather than testing it only in hand-built graphs.
Tier-1 after the fix: **6310 passed, 0 failed, 1 skipped** (the pre-existing reserved-story skip); +2 net-new tests.

<details><summary>Original blocked hand-back (superseded)</summary>

Status: blocked
Blocking condition: in-engine gate cannot be satisfied unattended — the diff touches `godot/src/UI/**` (`CombatFeedbackBridge`, `AudioManager`), so the gate applies, but the new teleport/cue behavior is unobservable over the GDScript-only godot-mcp bridge (DW-882: no roster ability authors the four new leaves; the bridge cannot construct the `Fixed` cast args to drive a cast nor read the private C# `EntityWorld` SoA to observe the result), and this diff changed no boot-observable coupled surface to anchor a partial PASS. Needs a human `/godot-verify` pass (or the DW-882 debug seam) to complete.

**Change:** Completed the closed effect vocabulary (AR-8 / DW-248) by adding the four leaves Story 2.1 reserved: the sim-mutating `TeleportEffect` (a deterministic blink that relocates the CASTER to the ground point on a GroundPoint cast, else to a live non-caster target on a TargetUnit cast, else a no-op; a placement-class single `Position` write plus the full `Create`/arrival consistency reset — `PrevPosition`, `Velocity = Zero`, re-sampled `Elevation`, cleared `Moving` flag, `MoveTarget`, `CommandState = Idle`), and the three checksum-neutral presentation leaves `PlayVfxEffect` / `PlaySoundEffect` / `ShakeScreenEffect` (whose only effect is pushing a new, presentation-only `CombatEventType` onto the non-folded `CombatEventQueue` carrying an authored `CombatFeedbackProfile`, rendered by the existing `CombatFeedbackBridge` / `AudioManager` drainers). Wired end-to-end through the closed pipeline: JSON converter (read/write), an explicit `CanonicalFold` arm per kind (folding kind + `RequireTag`; `Feedback` consciously excluded — float-bearing, presentation-only, hash-excluded everywhere else), AbilityValidator inert-cue warnings, and the closedness / fold-completeness / position-writer guard tests (adding an `ExcludedFieldsByKind` concept the fold-completeness guard already foreshadowed, and sanctioning `TeleportEffect` as the one new placement-class `Position` writer). No `EffectCaps`/`RulesetHash` change; new fold arms are dead for shipped content, so no existing golden or hash `AlgoVersion` moved.

**Files changed:**
- `godot/src/Effects/TeleportEffect.cs` (new) — the sim-mutating blink leaf.
- `godot/src/Effects/PlayVfxEffect.cs`, `PlaySoundEffect.cs`, `ShakeScreenEffect.cs` (new) — the checksum-neutral presentation leaves.
- `godot/src/Combat/CombatEventQueue.cs` — appended `PlayVfx`/`PlaySound`/`ShakeScreen` to `CombatEventType` and to the ambient lane.
- `godot/src/UI/CombatFeedbackBridge.cs` — drain cases spawning the flash (`PlayVfx`) and applying camera shake (`ShakeScreen`).
- `godot/src/UI/AudioManager.cs` — `PlaySound` routes through the existing profile-first drain; a unity `VolumeFor` arm.
- `godot/src/Core/Definitions/EffectNodeJsonConverter.cs` — kind consts + read/write arms + `Read/WriteFeedback` (omit-when-null; located error on a malformed payload).
- `godot/src/Core/Definitions/CanonicalFold.cs` — four explicit `MixEffect` arms (kind + `RequireTag`; `Feedback` excluded).
- `godot/src/Core/Definitions/AbilityValidator.cs` — non-fatal warnings for inert `play_sound` / `shake_screen` cues.
- `godot/ProjectChimera.Sim.Tests/Effects/TeleportAndPresentationLeafTests.cs` (new) — behavior/determinism/round-trip coverage incl. wall-bypass, non-flat elevation, and the production cast-path integration test.
- `godot/ProjectChimera.Sim.Tests/Validation/EffectFoldCompletenessTests.cs` — `ExcludedFieldsByKind` + the four new kinds.
- `godot/ProjectChimera.Sim.Tests/Validation/CanonicalModelHashEffectFoldTests.cs` — per-kind `RequireTag`, discriminator-distinctness, and Feedback-exclusion pins.
- `godot/ProjectChimera.Sim.Tests/Meta/PositionWriterGuardTests.cs` — sanctioned `Effects/TeleportEffect.cs` (count 1, placement-class).
- `godot/ProjectChimera.Sim.Tests/Combat/CombatEventQueueCapacityTests.cs` — pinned the three new types as ambient; enum-coverage count 14→17.
- `godot/ProjectChimera.Sim.Tests/Definitions/AbilityInertContentWarningTests.cs` — inert-cue warning tests (both directions).

**Verification:** `dotnet build godot/godot.csproj` → Build succeeded, 0 warnings, 0 errors (banned-API analyzer clean; no `float`/`System.Random`/Godot in the new sim leaves). `dotnet test godot/ProjectChimera.Sim.Tests` → 6308 passed, 1 skipped (pre-existing reserved-story skip), 0 failed — independently re-run after the review patches. No golden re-recorded; no hash `AlgoVersion` moved (`RulesetHash` 3, `CanonicalModelHash` 15 unchanged). Review: 7 patches applied, 2 deferred (DW-891, DW-892), 4 rejected; followup review recommended (patched-severity score ≥ 5). In-engine gate: BLOCKED (DW-882) — see the gate block above.

**Residual risks:** (1) The in-engine visual/behavioral observation is unmet pending a human `/godot-verify` (or the DW-882 debug seam) — the code is complete and fully Tier-1-verified, but no automated in-engine digest of the running cue/teleport could be captured. (2) DW-891 — a blink has no sim-side destination-pathability check, so a trigger/online/charge cast onto a blocked cell can embed a unit (MVP-scoped, same class as SaveGameState restore). (3) DW-892 — a mis-targeted teleport (Self/None, or require_tag on GroundPoint) silently no-ops after spending cost+cooldown, with no authoring warning. (4) Residual working-tree artifacts not part of this change: `_bmad-output/implementation-artifacts/epic-15-context.md` (a planning-context recompile from step-01) and several `*.cs.uid` Godot editor-metadata files auto-generated by the running editor — left in place, not committed.

</details>

**Residual risks (updated 2026-08-07).** Risk (1) above is RESOLVED — the in-engine observation was captured, not waived.
DW-891 and DW-892 stand as filed. New residual: the mouse-pixel to `RaycastGround` sliver still cannot be driven (the
bridge has no absolute-mouse click), so the click-to-cast-point conversion itself remains human-verify; everything
downstream of it is now automatable. The `*.cs.uid` Godot editor-metadata files ARE committed with this change (they are
the editor's per-script identity files and belong beside their sources).
