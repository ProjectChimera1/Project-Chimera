# Story 2.7: CombatFeedbackProfile — profile-driven feedback, presentation-only, excluded from checksum

Status: ready-for-dev

<!-- Context engineered by gds-create-story (ultracode: 6-analyst parallel artifact analysis). Comprehensive developer guide — the dev agent will have ONLY this file. -->

## Story

As a creator,
I want abilities and units to carry a CombatFeedbackProfile (hit particles, impact sound, screen shake, hit-freeze, death effect) with a tuned default that I can override per unit/ability,
So that combat reads as satisfying and is art-directable, without any feedback ever affecting the deterministic simulation.

## Acceptance Criteria

> Verbatim from `epics.md:974-978`. Sub-bullets are the dev's literal pass conditions (what the Acceptance Auditor will check).

**AC1 — default reproduces today's look + is hash-excluded**
**Given** a unit/ability with no explicit feedback override **When** it deals a melee hit, a ranged hit, a splash hit, and a kill **Then** the tuned default profile plays the existing pooled flash colors (orange/yellow/red/white) and a brief camera shake on kill, matching today's CombatFeedbackBridge behavior **And** the CombatFeedbackProfile field is excluded from SimChecksum and the canonical hash.
- The four default looks are byte-for-byte today's constants (table in Dev Notes §"Exact default constants").
- `SimChecksum.cs` never references the profile, `CombatEvent`, or any new field; `SimChecksum.AlgoVersion` stays **8**; `CanonicalModelHash.AlgoVersion` stays **2**; all **10** goldens stay **byte-identical**.

**AC2 — override renders (per unit AND per ability; particle/sound/shake/death)**
**Given** a unit or ability with a custom CombatFeedbackProfile override (different hit particle/sound/shake/death effect) **When** combat events occur for it **Then** the bridge renders the overridden feedback instead of the default, driven by the profile rather than hardcoded constants.
- A **unit** with an override shows its custom flash/shake/sound on its melee, ranged, splash, and kill events.
- An **ability** with an override plays its custom feedback when the ability is cast (the path 2.10 consumes — see §"Downstream contract").
- "different … **sound**" ⇒ audio is part of the override (AudioManager is in-scope — see Scope Decision SD-2).

**AC3 — hit-freeze is presentation-only; render-independent determinism**
**Given** an ability configured with a hit-freeze **When** the freeze plays during combat **Then** the simulation tick continues advancing on schedule (sim time is unaffected) and two runs with and without rendering produce identical golden checksums.
- Hit-freeze touches ONLY presentation state (no `Engine.TimeScale`, no `GetTree().Paused`, no gating/zeroing of `_host.StepOnce()`/`_host.Update(delta)` in `MainScene._Process`).
- A Tier-1 test proves adding/varying a profile or a `CombatEvent` field does not move `SimChecksum` (goldens byte-identical; the golden harness never builds the bridge — structural). The literal "with vs without rendering" leg is confirmed in-engine via `/godot-verify` (Task 9), not a Godot-free Tier-1 diff.

_Covers: FR-12a, AR-29, UX-DR51. Depends on: 2.1, 2.4 (both DONE)._

---

## Scope Decisions (baked-in recommendations — confirm or adjust before/at dev)

> The three ACs *as written* require unit **and** ability overrides, **sound** overrides, and all four event types. That is more than "pure presentation": it stamps presentation-events at the sim push sites. All such touches are **determinism-neutral** (the event queue, projectile store, and the new presentation array are NEVER folded into any hash). Recommendations below are the default the tasks assume; the **carve-off line for a leaner 2.7b** is called out in SD-3/SD-4.

| # | Decision | Recommended (assumed by Tasks) | Alternative / carve-off |
|---|---|---|---|
| **SD-1** | How does an override reach the bridge, given `CombatEvent` carries only `{Type, Position}` and the source may be dead/recycled at drain time? | **Resolve the profile AT push time and carry a nullable `CombatFeedbackProfile?` reference on `CombatEvent`** (null ⇒ tuned default). Source profile reached via a presentation-read `CombatFeedbackProfile?[]` SoA on `EntityWorld`, set in `ApplyUnitDefinition` from `def.CombatFeedback`, null-defaulted in `Create()`, **NOT folded** (CategoryOf/MeshType precedent), A2-compliant. | Per-entity `int` profile index + a presentation-side profile table (more plumbing, same determinism posture). |
| **SD-2** | Is **sound** (impact + death) in scope? | **IN scope.** AC2 literally says "sound"; FR-12a lists "impact sound" + (GDD) "death sound." `AudioManager` reads `evt.Feedback` for sound id/volume, falling back to today's per-event clips. Single-clear contract preserved (AudioManager still never clears). | Visual-only (flash + shake); defer audio. **Rejected** — fails AC2's literal "sound." |
| **SD-3** | Ability-cast feedback emission (abilities emit NO combat event today — only auto-attacks + kills do). | **IN scope (the carve-off candidate).** Add `CombatEventType.AbilityCast`; `AbilityCastSystem` pushes it on a committed cast carrying the ability's `CombatFeedback`. Satisfies AC2-for-abilities **and the 2.10 contract** ("the cast plays its CombatFeedbackProfile", 2.10 ships "no new engine code"). Null profile ⇒ no extra cast juice (opt-in). | **Defer to 2.7b** if 2.7 is too large — but then 2.10 cannot keep its "no new engine code" premise. If deferred, trim AC2 to unit-only and note the 2.10 dependency moves to 2.7b. |
| **SD-4** | Ranged/splash override for a unit (the attacker entity id is lost at `ProjectileSystem.cs:83`). | **IN scope.** Add `CombatFeedbackProfile? Feedback` to `ProjectileStore`, set at `Spawn` from the attacker (`CombatSystem.cs:474-482` has `attacker`). Small; lets ranged units honor their override. | Melee+kill override only; ranged/splash uses default (the 2.6 carve — defer per-source ranged). Acceptable lean-down if Alec wants. |
| **SD-5** | DTO shape vs AC1's four distinct default looks. | The **default** stays event-type-keyed (4 looks: melee/ranged/splash/kill) — shipped as the bridge's fallback. An **override** `CombatFeedbackProfile` is the flat per-source bundle (one hit look + one death look + sound + shake + freeze), applied to whichever event the source produces. Schema in Dev Notes §"DTO schema". | Per-event-type sub-looks on every override profile (richer authoring, larger DTO). Not needed for the ACs. |
| **SD-6** | Where does the profile attach? | **Both** `UnitDefinition.CombatFeedback` (lenient loader) and `AbilityDefinition.CombatFeedback` (strict `ContentJson.Options` — flat declared member, Disallow-safe). FR-12a/AR-29 say "per unit/ability"; 2.10 needs the ability path. | Unit-only now (defer ability attach with SD-3). |
| **SD-7** | Hit-freeze semantics. | Presentation-only "hitstop" inside `CombatFeedbackBridge._Process`. Field = **int frames** (GDD: "hit freeze frames"). **Default 0 = OFF** (preserves today's look). Freeze the hit flash's shrink animation (and optionally the struck unit's interpolation) for N frames; NEVER gate the sim. | A seconds-based field; or a global presentation hold. Keep it local + off-by-default. |
| **SD-8** | `prefers-reduced-motion` accessibility toggle for shake/freeze (UX `EXPERIENCE.md:112`). | **DEFER** (out of the 2.7 ACs; `SettingsData` has only `ColorblindMode` today). Note as a follow-up. | Wire a reduced-motion/screen-shake `SettingsData` toggle now (adds a settings surface). |
| **SD-9** | Ability-editor UI to author the profile (2.5/2.6 editor). | **DEFER** editor surface (2.6 precedent: "attach is a faction-JSON edit"). Profile authoring is raw-JSON / faction-JSON for now; the ACs need only the DTO + bridge behavior. | Add an editor panel section (larger; not required by ACs). |
| **SD-10** | How to prove AC3's "with and without rendering, identical checksums." | Tier-1 exclusion test (profile/`CombatEvent` field does not move `SimChecksum`; AlgoVersion 8; goldens byte-identical) **plus** the in-engine `/godot-verify` run (visuals play while the sim advances). Golden harness never builds the bridge ⇒ render-on/off is identical by construction; assert it. | — |

---

## Tasks / Subtasks

- [ ] **Task 1 — `CombatFeedbackProfile` DTO (Godot-free, presentation-domain)** (AC: 1, 2)
  - [ ] Create `godot/src/Core/Definitions/CombatFeedbackProfile.cs` — `#nullable enable`, **NO `using Godot;`**, plain primitives only (`float`, `float[]`, `int`, `string?`; **never** `Godot.Color`/`Vector3`/`AudioStream`, **never** `Core.Fixed`). PascalCase auto-props + snake_case `[JsonPropertyName]`. Per the DTO schema in Dev Notes §"DTO schema".
  - [ ] Sensible defaults on every field so an unauthored profile is harmless; nested sub-objects (`HitFlash`, `DeathFlash`, `Shake`) are their own small POCOs in the same file (declared members so the strict ability loader's `Disallow` accepts them).
  - [ ] Comment the class header: presentation-domain, excluded from `SimChecksum`/`CanonicalModelHash`, translated to Godot types only at the presentation boundary.
  - [ ] Verify `SimSources.props` already globs `src/Core/Definitions/**` (it does) — the DTO is auto-covered by Tier-1 + the analyzer; **no props edit**.

- [ ] **Task 2 — Attach the profile to definitions** (AC: 1, 2)
  - [ ] `UnitDefinition.cs`: add `[JsonPropertyName("combat_feedback")] public CombatFeedbackProfile? CombatFeedback { get; set; }` (nullable ⇒ omittable ⇒ existing JSON unaffected). Loads via `FactionDefinition`'s lenient options — no converter work.
  - [ ] `AbilityDefinition.cs`: add the same flat member. It deserializes through `ContentJson.Options` (`UnmappedMemberHandling.Disallow`) — a **declared** flat member is Disallow-safe; do NOT add any computed getter unless `[JsonIgnore]` (the `ParsedTargeting`/`ParsedActivation` lesson, else the 2.5a editor round-trip rejects the re-emitted member).
  - [ ] Confirm the 2.5a ability-editor round-trip (`new(ContentJson.Options){WriteIndented=true}`) still serializes/deserializes an ability carrying a profile (plain POCO via default reflection — expected OK; add a round-trip Tier-1 test).

- [ ] **Task 3 — Ship the tuned default (embedded constant set)** (AC: 1)
  - [ ] Encode today's four event-type looks (Dev Notes §"Exact default constants") as an **embedded C# default set** (the canonical source of truth). Values MUST equal today's constants byte-for-byte. **Embed it (not a `res://` JSON), so the Godot-free Tier-1 "default-equals-constants" test in Task 8 can read it** — a `res://`-loaded JSON is unreadable from the Godot-free test assembly. (Feedback is cosmetic, not balance, and is fully overridable per unit/ability, so an embedded default still honors the data-driven rule — creators reach it via override. An optional `resources/data/feedback/default_feedback.json` in-engine override is fine but is NOT the source of truth the Tier-1 test checks.)
  - [ ] Inject the default at the bridge's construction site (`RenderingPhase.cs:43-45`) into a widened `CombatFeedbackBridge.Initialize(...)` (and the audio default at `AudioPhase.cs:23`). (Per unit/ability profiles are resolved via SD-1's event reference, NOT a per-faction table at the bridge — so no post-ScenarioLoad re-wire is needed.)

- [ ] **Task 4 — Carry the source profile to the event (SD-1)** (AC: 2)
  - [ ] `EntityWorld`: add a presentation-read `CombatFeedbackProfile?[] FeedbackProfile` array. Set it in **`ApplyUnitDefinition`** from `def.CombatFeedback` (A2 single-mapper rule); default **null** in `Create()` (recycle safety). Document at the declaration: presentation-read, **NOT folded**. ⚠ Only the **not-folded posture** carries over from `MeshType`/`CategoryOf` — those are value-type arrays (`byte[]`/`UnitCategory[]`); this is **EntityWorld's first reference-typed SoA array** (GC-tracked, mostly-null, negligible at ~2k entities; determinism-safe — never hashed, the coverage guard scans only `ResourceStore`). Value-type-consistent **alternative** (SD-1): an `int[] FeedbackProfileId` + a presentation-side profile table — both determinism-neutral; pick one and note it in the Dev Record.
  - [ ] Extend `ApplyUnitDefinitionGuardTest` so a recycled slot cannot inherit a prior occupant's `FeedbackProfile` (the 1.12/1.13/2.6 zombie-state defect class). Do **NOT** add it to any folded/checksum assertion.
  - [ ] **Spawn-path completeness (pre-empt the 2.6 Edge-Case-Hunter HIGH):** the 3 primary in-match def-based spawns route through `ApplyUnitDefinition` (`ScenarioApplier.SpawnUnit:210`, `BuildingSystem.SpawnTrainedUnit:174`, `EntityPlacer.DoSpawnCombatUnit:486`) → `FeedbackProfile` populated, overrides work for built armies. BUT `EntityPlacer.DoSpawnWorker` (`:432-462`) and `RestoreUnit` (`EntityWorld.cs:374`) do **NOT** call `ApplyUnitDefinition` → `FeedbackProfile` stays null there (a worker's death-effect override is inert when editor-placed / undo-restored). Presentation-only and consistent with the 1.13 worker posture — **DEFER is acceptable; state it in the Dev Record.** If worker death overrides are wanted, set `FeedbackProfile` directly in `DoSpawnWorker` alongside the separation fields (and widen `UnitSnapshot` for `RestoreUnit`).
  - [ ] `CombatEventQueue.cs`: add `CombatFeedbackProfile? Feedback` to `CombatEvent`; add an overload/param to `Push(...)` to set it (default null keeps the existing 2-arg call shape working). Preserve drop-on-full + the bridge-owns-`Clear()` contract.
  - [ ] `ProjectileStore`: add `CombatFeedbackProfile? Feedback`; set it in `Spawn(...)` from the attacker (SD-4). (`ProjectileStore` is never folded — determinism-neutral.)
  - [ ] Stamp the profile at the 3 push sites, preserving the **Story 1.6 AC2 event-before-Apply ordering**:
    - Melee — `CombatSystem.cs:488`: from `world.FeedbackProfile[attacker]`.
    - Ranged/Splash — `ProjectileSystem.cs:83`: from `_store.Feedback[projId]`.
    - Kill — `DamageResolver.cs:70`: from `world.FeedbackProfile[t]` (read BEFORE `world.Destroy(t)` on :72).

- [ ] **Task 5 — Make `CombatFeedbackBridge` profile-driven** (AC: 1, 2, 3)
  - [ ] Rewrite the `switch (evt.Type)` in `_Process` to use **two-level resolution** (the sub-flash specs are nullable, so a profile that authored only a sound must not NPE / render a blank flash):
    - `hitLook  = evt.Feedback?.HitFlash  ?? <default look for evt.Type>` (melee / ranged / splash / `AbilityCast`)
    - `deathLook = evt.Feedback?.DeathFlash ?? <default kill look>` (`UnitKilled`)
    - `shake     = evt.Feedback?.Shake     ?? <default kill shake>` (applied on kill)
    When the whole profile OR a sub-spec is null, reproduce today's exact look (orange/yellow/red/white + `SetShake(0.12f, 0.22f)` on kill). `AbilityCast` uses `HitFlash` with a **no-flash** default (abilities opt into cast juice via their profile).
  - [ ] Move the hardcoded constants into the default set (Task 3). Preserve: 48-slot pool + silent drop, shared `SphereMesh`, `pos.Y += 0.5f` lift, linear shrink-to-zero, the single `_events.Clear()` at end of `_Process`.
  - [ ] Implement **hit-freeze (SD-7)**: a presentation-only frame counter that briefly holds the hit flash's shrink animation (and optionally the struck unit's interpolation). Drive it from `evt.Feedback?.HitFreezeFrames` (default 0 = off). It must NOT touch `_host`/`MainScene._Process`/`Engine.TimeScale`/`GetTree().Paused`. **CRITICAL regression guard — the freeze gates ONLY the per-slot flash-shrink loop (`CombatFeedbackBridge.cs:101-116`); the queue drain and the single `_events.Clear()` (`:97`) MUST run every frame unconditionally.** A freeze that early-returns out of `_Process` would leave the queue uncleared → `AudioManager` (second consumer, never clears) replays the same events every frozen frame (duplicate SFX) and the 256-slot queue overflows and silently drops new events.
  - [ ] Handle the new `AbilityCast` event type (SD-3): render the ability's profile (flash/shake/freeze); null profile ⇒ no extra juice.

- [ ] **Task 6 — Make `AudioManager` profile-driven (SD-2)** (AC: 2)
  - [ ] Restructure `AudioManager._Process` **profile-first**: if `evt.Feedback != null`, play its `ImpactSoundId` (or `DeathSoundId` for the kill look) — graceful-silent when the id is null — for ANY event, **including the new `AbilityCast` type**. The existing `switch (evt.Type)` over the 4 legacy clips becomes the **null fallback** only. (Today's switch is 4-case type-gated, `AudioManager.cs:106-112`; a bare `AbilityCast` event would otherwise fall through to silence — and there is NO default cast clip, so the override's `ImpactSoundId` is the ONLY sound an ability cast can make. Required by AC2's "sound" + the 2.10 contract. Mirror Task 5's explicit `AbilityCast` handling so the two consumers are symmetric.)
  - [ ] Preserve graceful-silence on missing assets and the **must-NOT-clear** contract (`AudioManager.cs:114` — the bridge owns the single `Clear()`); `/godot-verify` (Task 9) should explicitly confirm an ability-cast override's sound plays.

- [ ] **Task 7 — Ability-cast feedback emission (SD-3)** (AC: 2)
  - [ ] Add `AbilityCast` to `CombatEventType`. In `AbilityCastSystem`, on a committed cast push a feedback event at the primary-target (fallback caster) position carrying `ability.CombatFeedback`. Use the `EffectContext.Events` queue already wired in 2.4a. Presentation-only — never folded; do not branch the tick on it.

- [ ] **Task 8 — Tier-1 tests (Godot-free) + determinism teeth** (AC: 1, 3)
  - [ ] `Definitions/` tests: profile JSON round-trip (both loaders); the **embedded default's values equal today's constants** (byte-for-byte, per Task 3); ability carrying a profile round-trips through `ContentJson.Options`.
  - [ ] **Exclusion tooth (A3 discipline):** prove that adding/varying a `CombatFeedbackProfile` (and the new `CombatEvent.Feedback`/`EntityWorld.FeedbackProfile`) does NOT move `SimChecksum.Compute`; assert `AlgoVersion == 8`; assert the 10 goldens byte-identical. Add the CategoryOf-style exclusion note in `SimChecksumCoverageGuardTest`. Prove the tooth has teeth: inject-violation (temporarily fold the profile) → observe RED → revert.
  - [ ] **AC3 sim-advances assertion (concrete):** Tier-1 is Godot-free and CANNOT build the `Node3D` bridge, so there is no literal in-engine rendered-vs-headless checksum diff to write here. Instead assert that **draining + clearing the `CombatEventQueue` each tick (mimicking the bridge) yields a byte-identical checksum to NOT draining it** — proving the feedback path cannot perturb the sim. The literal **"with rendering" leg of AC3 is the `/godot-verify` run (Task 9)**, not a Tier-1 test.
  - [ ] Run the full suite: `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release` — expect baseline ~495 pass/1 skip to RISE by the new tests, **0 fail**, and **zero golden drift**.

- [ ] **Task 9 — In-engine verification (`/godot-verify`)** (AC: 1, 2, 3)
  - [ ] `dotnet build godot/godot.sln` (0 errors) → run the scene via godot-mcp → screenshot + judge: (a) AC1 default flashes (orange/yellow/red/white) + kill shake look unchanged; (b) AC2 a unit/ability override renders different feedback; (c) AC3 a configured hit-freeze plays while the sim keeps advancing. Append the verification result (with screenshots) to the Dev Agent Record. Run BEFORE the code-review marks the story done.

- [ ] **Task 10 — Determinism fence assertions (state in Dev Record)** (AC: 1, 3)
  - [ ] Confirm UNTOUCHED: `SimChecksum.cs` fold set + `AlgoVersion` (8) + `ExpectedV8Hash` (0x983D39AE), `CanonicalModelHash.cs` (`AlgoVersion` 2), `SystemOrderTest.cs`, `VersionStampConsistencyTests`, `ReplayRecorder.VERSION`, `PROTOCOL_VERSION`, all 10 `*.golden.txt`. A golden that moves = a leaked sim read of the profile → fix the leak, never re-record.

---

## Dev Notes

### THE central design problem (read first)

`CombatEvent` carries **only** `{ CombatEventType Type; FixedVec3 Position; }` (`CombatEventQueue.cs:15-19`) — **no source identity**. The presentation bridge drains the queue the frame AFTER the sim tick, by which point the source entity may be **dead/recycled** (for `UnitKilled`, `DamageResolver.Apply` pushes the event then `world.Destroy(t)` in the same call, `DamageResolver.cs:70-72`). Therefore you **cannot** look up "which unit/ability caused this event" at drain time. **Resolve the profile at PUSH time (source is alive) and carry a `CombatFeedbackProfile?` reference on the event** (SD-1). This is the load-bearing decision the whole story hangs on.

### The complete wiring path

```
SIM (tick):
  CombatSystem.TryDealDamage  → CombatEventQueue.Push(MeleeHit,  pos, world.FeedbackProfile[attacker])   [CombatSystem.cs:488]
  ProjectileSystem.ApplyHit   → CombatEventQueue.Push(Ranged/Splash, pos, store.Feedback[projId])         [ProjectileSystem.cs:83]
  DamageResolver.Apply (kill) → CombatEventQueue.Push(UnitKilled, pos, world.FeedbackProfile[t])           [DamageResolver.cs:70]
  AbilityCastSystem (commit)  → CombatEventQueue.Push(AbilityCast, pos, ability.CombatFeedback)            [NEW, SD-3]
PRESENTATION (per frame, decoupled from the sim loop):
  CombatFeedbackBridge._Process → flash + shake + hit-freeze from (evt.Feedback ?? default)  → _events.Clear()   [owns the clear]
  AudioManager._Process         → impact/death sound from (evt.Feedback ?? default)           → does NOT clear
```

The sim advances via `SimulationHost.StepOnce()`/`Update(delta)` driven from `MainScene._Process` (`MainScene.cs:514-544`) — entirely separate from the two bridge nodes. The golden replay harness (`GoldenChecksumReplay`) is **sim-only** and never constructs the bridge/audio/camera → AC3's "with and without rendering, identical checksums" is true by construction; assert it anyway.

### `#1 SCOPE RESOLUTION` — do NOT build effect leaves

AR-29 says the profile "drives D1 presentation leaves (PlayVfx/PlaySound/ShakeScreen)." **These do not exist as effect-graph nodes.** Repo grep: `PlayVfx`/`ShakeScreen` = zero code matches; `PlaySound` exists only as the scenario-trigger delegate `OnPlaySound` (`ScenarioDirector.cs:55`), unrelated to combat. Story 2.1 explicitly **deferred** presentation leaves "to owning stories." The closed D1 effect vocabulary is exactly 7 sealed kinds (`direct_hp_delta, heal, damage, apply_modifier, sequence, search_area, persistent`) and is "pure simulation, no Godot." **The realization mechanism is the existing `CombatEventQueue` → `CombatFeedbackBridge`/`AudioManager` bus** (architecture P3 "Home: … CombatFeedbackBridge upgraded from hardcoded to profile-driven"). Upgrade the bridge; do NOT add an effect node.

### Exact default constants (AC1 — reproduce byte-for-byte)

From `CombatFeedbackBridge.cs` (the as-built look IS the canonical default per UX-DR51 "per as-built CombatFeedbackBridge"):

| Event | Color (RGB) | Emission× | Scale | Duration | Extra | Source |
|---|---|---|---|---|---|---|
| `MeleeHit` | orange `(1.0, 0.50, 0.10)` | 3.0 | 0.9 | 0.18 s | — | `:43,:80` |
| `RangedHit` | yellow `(1.0, 0.85, 0.10)` | 2.5 | 0.7 | 0.15 s | — | `:44,:84` |
| `SplashHit` | red `(1.0, 0.20, 0.05)` | 4.0 | 1.8 | 0.28 s | — | `:45,:88` |
| `UnitKilled` | white `(1.0, 0.95, 0.80)` | 5.0 | 1.2 | 0.25 s | `SetShake(0.12f, 0.22f)` | `:46,:92-93` |

Shared: `MAX_FLASHES = 48`; `SphereMesh` radius 0.3 / height 0.6 / 6 radial / 4 rings; `pos.Y += 0.5f`; `StandardMaterial3D` Unshaded + Emission = color×mult; linear shrink `Scale = One * (baseScale * timer/duration)`; pool exhaustion silently drops.
- **`SetShake(float duration, float strength)`** — param order is **(duration, strength)**. The kill call = duration 0.12 s, strength 0.22 world-units. Preserve the "only override if stronger/longer" merge in `RtsCameraController.SetShake`.
- **⚠ STATUS.md lies:** `STATUS.md:301,303` label the SCALE values (0.9/0.7/1.8/1.2) as "durations" (e.g. "melee=orange 0.9s"). Trust the CODE — durations are 0.18/0.15/0.28/0.25 s.
- AudioManager today: melee `melee_hit.ogg`@0.9 (pitch-rand ±8% via `GD.RandRange`), ranged `ranged_hit.ogg`@0.8 (pitch-rand), splash `explosion.ogg`@1.0, kill `unit_killed.ogg`@0.85; under `res://resources/audio/sfx/`, graceful-silent if absent.

### DTO schema (recommended — SD-5)

```
// godot/src/Core/Definitions/CombatFeedbackProfile.cs  (presentation-domain; NO using Godot; NO Fixed)
class CombatFeedbackProfile {
  [JsonPropertyName("hit_flash")]   FlashSpec? HitFlash       // overrides melee/ranged/splash look for this source
  [JsonPropertyName("impact_sound")] string?   ImpactSoundId  // key/path under res://resources/audio/sfx/
  [JsonPropertyName("shake")]       ShakeSpec? Shake          // → SetShake(Shake.DurationSec, Shake.Strength)
  [JsonPropertyName("hit_freeze_frames")] int  HitFreezeFrames = 0   // presentation hitstop; 0 = off (today's look)
  [JsonPropertyName("death_flash")] FlashSpec? DeathFlash     // overrides kill look
  [JsonPropertyName("death_sound")] string?    DeathSoundId
}
class FlashSpec { float[] ColorRgb; float EmissionMult; float Scale; float DurationSec; }   // ColorRgb = float[3], FactionDefinition.Color precedent
class ShakeSpec { float DurationSec; float Strength; }
```
- **`float` is fine here** and follows the `UnitDefinition` precedent (~15 float stats). The analyzer treats the `float` keyword as **advisory CHM0001** only (NOT the release-gated RS0030) — expect a harmless advisory, like every other `UnitDefinition` float. Because **no sim code reads the profile, its floats are never quantized to `Fixed`** — do NOT add a `Fixed.FromFloat` for them.
- `float[]` for color = the `FactionDefinition.Color` precedent (Godot-free RGBA). The bridge converts `float[]` → `Godot.Color` at the presentation boundary.
- The shipped **default** keeps the 4 event-type looks (the embedded default set from Task 3); a per-source override `CombatFeedbackProfile` supplies one hit look + one death look that REPLACES the default for that source. Don't collapse the 4 default looks into one.
- **DELIBERATE divergence from architecture P3 (sign-off):** the arch bundle (`game-architecture.md:2571-2582`) names `{ hitParticleId, impactSoundId, shake{intensity, durationTicks}, hitFreezeFrames, deathEffectId, deathSoundId }`. This story replaces `hitParticleId`/`deathEffectId` and `shake{intensity, durationTicks}` with the inline `FlashSpec{ColorRgb, EmissionMult, Scale, DurationSec}` / `ShakeSpec{DurationSec, Strength}` shape, because **the as-built bridge is color/material-based and there is no particle-ID or VFX registry to reference**, and `SetShake` takes seconds, not ticks. `impactSoundId`/`deathSoundId`/`hitFreezeFrames` are kept. Intentional and AC-correct — a reviewer cross-checking the arch doc will see different field names by design.

### Determinism / Architecture compliance (AR-29 — the fence)

- **Two hashes, both must never see the profile:** `SimChecksum.Compute(EntityWorld, BuildingStore, ResourceStore, FactionRegistry, ModifierStore?)` takes only those args and folds an explicit set (positions/health/command/separation/effective-stats/armor/modifiers/cooldowns/resources/RNG) — it references no `CombatEvent`, `ProjectileStore`, `UnitDefinition`, or `AbilityDefinition`. `CanonicalModelHash.Compute(ScenarioData)` folds only scenario placement (units by `UnitId/Slot/X/Z`), never definition bodies. A profile on a definition + a field on `CombatEvent`/`ProjectileStore`/`EntityWorld.FeedbackProfile` are **automatically excluded** — just don't add a fold and don't bump a version.
- **The coverage guard already pre-blesses this:** `SimChecksumCoverageGuardTest.cs:24-28` names `CombatFeedbackProfile` as the archetype hash-excluded presentation field ("analogous to the hash-excluded CombatFeedbackProfile"). `CategoryOf` (`SimChecksum.cs:21`) is the in-code precedent: a per-entity presentation-read SoA "deliberately NOT hashed, like MeshType." The reflective guard scans only `ResourceStore` public arrays — a new `EntityWorld` reference array won't trip it.
- **Pins to keep:** `SimChecksum.AlgoVersion = 8`, `ExpectedV8Hash = 0x983D39AE`, `CanonicalModelHash.AlgoVersion = 2`, `ReplayRecorder.VERSION`, `PROTOCOL_VERSION`, `SystemOrderTest`, all 10 goldens (`golden-scenario, golden-multifaction, golden-applier-scenario, same-tick-tie-break, command-vocabulary-scenario, formation-separation-scenario, modifier-scenario, ability-cast-scenario, ai-active-scenario, passive-scenario`). Closest precedent = **2.4b** (presentation+wiring+data only, no fold, no re-record).
- **Hit-freeze landmine (AC3):** the sim is fixed 30 Hz, decoupled from `Node._Process`. A correct hit-freeze is a visual hold in the bridge driven by wall-clock/`delta`. FORBIDDEN: skipping/early-returning `_host.StepOnce()`/`_host.Update()` in `MainScene._Process`; passing a zeroed/reduced `delta`; `Engine.TimeScale = 0`; `GetTree().Paused = true`; any flag the sim loop reads. (No `Engine.TimeScale` exists in the codebase today — do not introduce one.) Wall-clock/`float` in the bridge (presentation) is fine; in sim it is banned.

### Library / framework requirements (serialization)

- **System.Text.Json** only. `[JsonPolymorphic]`/`[JsonDerivedType]` are forbidden project-wide — not needed (the profile is a closed POCO).
- **Two loader paths differ — both must accept the profile:**
  - `UnitDefinition` → `FactionDefinition.LoadFromFile` **lenient** options (`ReadCommentHandling=Skip`, `AllowTrailingCommas`, **no** `Disallow`, **no** string-enum converter, **no** `FixedJsonConverter`). Tolerant: a new nested POCO just works. Keep profile fields `float`/`int`/`string`/`float[]` (no real enums — if you ever need one, use the string + `[JsonIgnore]` `Parsed*` getter pattern).
  - `AbilityDefinition` → `ContentJson.Options` **strict** (`UnmappedMemberHandling.Disallow`; `JsonStringEnumConverter` name-only; `FixedJsonConverter` registered). A **declared** flat `combat_feedback` member is Disallow-safe. Because `Disallow` reaches nested POCOs reflectively, **every** sub-field of the profile must be a declared property. **Never type a profile field as `Core.Fixed`** — `FixedJsonConverter` would quantize it to 16.16 and reject `|v| ≥ 32768`.
- Default profile JSON home: `godot/resources/data/feedback/` (new) or embedded; loaded/injected at `RenderingPhase.cs:43-45`.

### File structure (what to touch)

**NEW**
- `godot/src/Core/Definitions/CombatFeedbackProfile.cs` (DTO + `FlashSpec`/`ShakeSpec`).
- `godot/resources/data/feedback/default_feedback.json` — OPTIONAL in-engine default override only; the canonical default is the embedded C# set (Task 3) so the Godot-free Tier-1 test can verify AC1.
- `godot/ProjectChimera.Sim.Tests/Definitions/CombatFeedbackProfileTests.cs` (round-trip + default-equals-constants + exclusion tooth).

**MODIFIED — data model**
- `UnitDefinition.cs`, `AbilityDefinition.cs` (+`combat_feedback`).
- `EntityWorld.cs` (+ presentation-read `FeedbackProfile?[]`, set in `ApplyUnitDefinition`, null-default in `Create()`).
- `CombatEventQueue.cs` (+`CombatEvent.Feedback`, `Push` overload; +`AbilityCast` enum value).
- `ProjectileStore.cs` (+`Feedback`, set in `Spawn`).

**MODIFIED — sim push sites (presentation-event stamps only; preserve Story 1.6 AC2 ordering)**
- `CombatSystem.cs:488` (melee stamp) + `:474-482` (projectile spawn stamp).
- `ProjectileSystem.cs:83` (ranged/splash stamp).
- `DamageResolver.cs:70` (kill stamp).
- `AbilityCastSystem.cs` (push `AbilityCast` with the ability's profile).

**MODIFIED — presentation**
- `CombatFeedbackBridge.cs` (profile-driven flash/shake/freeze + `AbilityCast`), `RenderingPhase.cs` (widen `Initialize`).
- `AudioManager.cs` (profile-driven sound).

**MODIFIED — tests**
- `ApplyUnitDefinitionGuardTest.cs` (recycle safety for `FeedbackProfile`).
- `SimChecksumCoverageGuardTest.cs` (CategoryOf-style exclusion note — documentation only).

**UNTOUCHED (assert in Dev Record)** — `SimChecksum.cs`, `CanonicalModelHash.cs`, `SystemOrderTest.cs`, `VersionStampConsistencyTests`, `ReplayRecorder`/`PROTOCOL_VERSION`, all 10 `*.golden.txt`.

### Testing requirements

- **Tier-1 (Godot-free):** `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj -c Release`. Baseline ~**495 pass / 1 skip** after 2.6 → must RISE with the new tests, 0 fail, zero golden drift. The DTO is in `SimSources.props` scope automatically.
- **Analyzer release gate:** `-p:ChimeraRelease=true --no-incremental` must stay 0 errors (RS0030 zero-baseline). `float` in the DTO is advisory CHM0001 only — acceptable, like every `UnitDefinition` float.
- **A3 gate-teeth discipline (Epic-1 retro action item):** the exclusion tooth must be proven by inject-violation → observe-RED → revert (e.g., temporarily fold the profile → a golden/known-state test goes red → revert).
- **`/godot-verify` (presentation gate — REQUIRED):** Tier-1 cannot test the Godot `Node3D` bridge. The Godot editor must be open (godot-mcp websocket, port 6550); `dotnet build` must pass first; run the scene, screenshot, judge AC1/AC2/AC3 with direct visual evidence, append the result to the Dev Agent Record. Run it BEFORE the adversarial code-review marks the story done (the game-story equivalent of `/check-site`). There is a live cast to exercise: 2.4b attached `fireball` to the `mage` in `alpha_faction.json` with a pre-placed P1 mage in `alpha_map_01.json`.

### Previous-story intelligence (patterns to reuse, traps to avoid)

- **2.4b is the closest model** — presentation + wiring + data only, no fold, no golden re-record, all version pins held. Mirror its determinism-posture paragraph in your Dev Record.
- **Single-mapper A2 rule (bit 1.12/1.13/2.2a/2.6):** any per-unit SoA derived from `UnitDefinition` MUST flow through `EntityWorld.ApplyUnitDefinition`, be null/sentinel-defaulted in `Create()`, and be covered by `ApplyUnitDefinitionGuardTest` for recycle safety. Applies to the `FeedbackProfile` array. (2.6 added `AuraAbilityIndex`/`OnHitAbilityIndex` exactly this way; `OnUnitDefinitionApplied` is the `Action<int>` end-of-mapper seam if you need spawn-time setup without `src/Core`→presentation coupling.)
- **2.6 `armor` float DRIFT lesson:** every `UnitDefinition` stat is `float`, converted to `Fixed` once at the load boundary only for stats the sim reads. The profile's floats are read by NOBODY in sim → no conversion, no fold.
- **2.3 converter lesson:** `Disallow` rejects only UNKNOWN members; a declared flat member is fine; computed getters must be `[JsonIgnore]`.
- **3-layer adversarial `gds-code-review` is the bar:** Blind Hunter / Edge Case Hunter / Acceptance Auditor. PASS = 0 Critical/0 High, every AC literally MET, every decision + named constraint honored, zero scope creep, determinism fence independently re-verified (Tier-1 green, goldens byte-identical, version anchors untouched, recycle traps closed). The Edge Case Hunter caught 2.6's "feature inert on built armies" by checking every spawn path — make sure `FeedbackProfile` is populated on the real in-match spawn paths (def-based spawns via `ApplyUnitDefinition`), not just one.

### Git intelligence

- `CombatFeedbackBridge.cs` and `CombatEventQueue.cs` have existed unchanged since the initial commit `1751d96` — pristine Phase-1 as-built; no Epic-1/2 story has touched them. 2.4a explicitly fenced `CombatFeedbackBridge.cs` as "out of scope (2.7)." Recent commits (2026-06-25→30) are Epic-2 ability/modifier/passive work; **2.6 (passive abilities)** is the last completed story and bumped `AlgoVersion` 7→8 (you keep 8). Convention: every new `.cs` ships a `.cs.uid` sibling; DTO + tests land together in the same autosave.

### Project Structure Notes

- DTO home `src/Core/Definitions` is the **sim-layer Definitions namespace** (Godot-free, globbed into `SimSources.props` + the analyzer). The combat-feedback CHANNEL (`CombatEventQueue`) correctly lives in `src/Combat`; the visual BRIDGE in `src/UI`. No structural variance — this story fits the existing folder map exactly.
- Solution/project files are `godot/godot.sln` / `godot/godot.csproj` (the `ProjectChimera.sln` name in `godot/CLAUDE.md` is stale). net8.0; 30 ticks/sec.

### Project Context Rules (from `_bmad-output/project-context.md` — must follow)

- **The boundary is sacred (lines 73-81):** sim = `src/Core,Combat,Economy,Navigation` — "No `using Godot;`. No Godot Node types. No `Vector3`/`float` for gameplay state." Presentation = `src/UI`, reads sim arrays each frame, never owns gameplay truth; data flows sim → presentation only; presentation sends commands back, never mutates sim state directly. The `CombatFeedbackProfile` is explicitly named (lines 50-54): "a presentation-domain DTO **excluded from the hash**."
- **Determinism (85-90):** `Fixed` 16.16 for any gameplay value; `Fixed.FromFloat` is load-time only; ascending-id iteration; no `Dictionary`/`HashSet` in sim; no wall-clock/unseeded `Random`. (The profile is presentation data — none of this constrains its float fields, but the push-site stamping code in sim must stay int/ref-only with no float math.)
- **Single def→SoA mapper / A2 rule (92-95):** as above — `FeedbackProfile` rides `ApplyUnitDefinition`, guarded by `ApplyUnitDefinitionGuardTest`.
- **Data-driven platform (97-102):** no gameplay/balance hardcoded where a creator can't reach it; defaults ship as JSON in `resources/data/` (FR-12a "tuned default ships"). Layered complexity (simple + advanced) and the three-question filter (Create/Share/Discover) — the profile serves Create (art-direct combat).
- **Conventions (129-135):** `PascalCase.cs` filename = class name; `#nullable enable` per file; comment public methods; Godot subclasses must be `partial`; `[Export]` float only in presentation; `GD.Print` (presentation) not `Console.WriteLine`; sim layer prints nothing. Tests are Godot-free under `godot/ProjectChimera.Sim.Tests`.

### References

- Story 2.7 (title, user story, AC1-3, dev note, Covers/Depends) — `_bmad-output/planning-artifacts/epics.md:966-982`
- Epic 2 goal + sequencing note (`CombatFeedbackBridge` hardcoded; AR-29 exclusion; hit-freeze never pauses sim) — `epics.md:836-840`
- FR-12a (five profile fields; tuned default ships; override per unit/ability) — `epics.md:80`; `prds/prd-Project_Chimera-2026-06-05/prd.md:185,114`
- AR-29 (presentation-domain DTO, excluded from SimChecksum/canonical hash, drives PlayVfx/PlaySound/ShakeScreen, hit-freeze presentation-only) — `epics.md:216`
- Architecture P3 (authoritative DTO bundle `{hitParticleId, impactSoundId, shake{intensity,durationTicks}, hitFreezeFrames, deathEffectId, deathSoundId}`, ships default as data, Home = Definitions + CombatFeedbackBridge upgraded, Determinism: none) — `_bmad-output/game-architecture.md:2571-2582`; presentation/IO domain never enters tick/hash — `:2536-2540`; Pres leaves cosmetic-only — `:395-396,416-419`
- UX-DR51 (pooled hit-flashes orange/yellow/red/white + brief camera shake on kills, per as-built bridge) — `epics.md:320`; `ux-designs/ux-Project_Chimera-2026-06-20/EXPERIENCE.md:110`; Game Feel & Juice + prefers-reduced-motion — `EXPERIENCE.md:107-113`; "juice" quality gate — `epics.md:162`
- GDD canonical spec (CombatFeedbackProfile: hit particles, impact sounds, screen shake intensity+duration, hit freeze frames, death effects + sounds; default "satisfying" profile ships) — `Project_Chimera_GDD.md:158`; framework defaults — `:121`; art direction/factions — `:41,49-50`; immediate feedback masks latency — `:349`
- Downstream consumer — Story 2.10 ("the cast plays its CombatFeedbackProfile … identical across two golden-checksum runs"; "felt via 2.7 feedback"; "no new engine code") — `epics.md:1038,1044,1046`
- Upstream deps — 2.1 `EffectContext` carries `CombatEventQueue events` (the sim→presentation sink); presentation leaves deferred — `2-1-…keystone.md`; 2.4a `AbilityCastSystem` builds the cast's `EffectContext` with the events queue, defers `CombatFeedbackProfile`/`CombatFeedbackBridge.cs` to 2.7 — `2-4a-….md:158,193`; 2.4b attached `fireball`→`mage` live cast — `2-4b-….md:59-60`
- As-built code — `CombatFeedbackBridge.cs` (constants `:43-46,79-93`, `SetShake(0.12,0.22)` `:93`, clear `:97`, `Initialize` `:38`); `CombatEventQueue.cs:6-19` (event payload + enum); push sites `CombatSystem.cs:488`/`:474-482`, `ProjectileSystem.cs:83`, `DamageResolver.cs:70-72`; `AudioManager.cs:108-114` (second consumer, never clears); `RtsCameraController.cs:165-174` (`SetShake(duration,strength)`); `RenderingPhase.cs:43-45` (wiring); `SimChecksum.cs:21,77,83-217` (fold set, AlgoVersion 8, CategoryOf exclusion); `CanonicalModelHash.cs:29,35-95` (AlgoVersion 2); `SimChecksumCoverageGuardTest.cs:24-28,104,112` (CombatFeedbackProfile named as the hash-excluded archetype; ExpectedV8Hash 0x983D39AE); `ContentJson.cs:33-47` + `FixedJsonConverter.cs:30-50` (strict ability loader); `FactionDefinition.cs:79-95` (lenient unit loader); `EntityWorld.cs:555-589` (`ApplyUnitDefinition`, MeshType set per spawn site)
- Verification — `/godot-verify` SKILL (build → editor open port 6550 → run → screenshot → judge → append to Dev Record, before code-review); Tier-1 + CI — `.github/workflows/determinism-gate.yml:47-52` (`dotnet test … -c Release`, windows+ubuntu, `CHIMERA_GOLDEN_RECORD` never set in verify)

---

## Dev Agent Record

### Agent Model Used

### Debug Log References

### Completion Notes List

### File List
