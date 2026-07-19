---
title: 'Hero profile load/mint hardening (DW-12, DW-13, DW-15, DW-48)'
type: 'bugfix'
created: '2026-07-19'
status: 'done'
baseline_revision: '10fc2aa43b9cc1ce0d4379d7806a7dc51e5a20dc'
final_revision: '823351025b3dac1cbfc61acdf06bdbfd37e003a4'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/godot/CLAUDE.md'
warnings: []
---

<intent-contract>

## Intent

**Problem:** With Story 3.13 runtime XP landed, four latent defects on the hero profile load/mint path are now live-reachable: (DW-12) on-disk `level`/`xp` are minted into `HeroStore` with no range/cap validation — a cheat/invalid-state vector folded into `StartStateHash`; (DW-13) `LoadInto` mints into the first matching placed hero regardless of owning slot; (DW-15) the Edit↔Play preserve branch re-mints hardcoded `hero.level`+`hero.xp` instead of the manifest-selected attributes; (DW-48) a missing `"slot"` JSON key deserializes to `0` (not the intended `-1`), collapsing a legacy multi-item loadout onto slot 0.

**Approach:** Add a fail-closed range/cap gate in `HeroProfileLoader.LoadInto` before `Mint`; add an optional owner-slot filter (default null = current behavior) threaded from the local faction at the four production call sites; route the preserve snapshot profile through `BuildProfile(... manifest.DeriveProfileShape() ...)` (the same seam Save uses); and make `ProfileInventoryItem` deserialize a missing `"slot"` to `-1` via a nullable-backed JSON binding, preserving the existing serialized form byte-for-byte.

## Boundaries & Constraints

**Always:** Keep `HeroProfileLoader` Godot-free, float-free, ascending-list iteration only (sim-layer rule). Any skip must be a deterministic skip + optional `log?.Warn`, never a partial-state divergence (every peer skips the same rows). New `LoadInto` parameters are optional and default to today's exact behavior so all existing callers/tests are unchanged. `ProfileInventoryItem`'s serialized JSON (`item_id`, `charges`, `slot`, in that order, `slot` always written) stays byte-identical to today.

**Block If:** A determinism golden / start-state-hash differential regresses for a scenario that does NOT deploy a hero profile (would indicate an unintended change to the no-profile path — that path must stay byte-identical).

**Never:** Do not change `HeroStore.Mint`, `StartStateHash`, `SimChecksum`, or `CanonicalModelHash` algorithms/AlgoVersions. Do not edit the deferred-work ledger. Do not clamp `level`/`xp` to a valid range — the gate is fail-closed (reject → mint nothing for that hero), not a silent clamp. Do not add a hard faction *compatibility* gate (faction stays card metadata, D-3); owner-slot scoping is only about which placed entity receives the mint.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Valid profile mint | `level` in `[0, MaxLevel]` (or `MaxLevel==0`), `xp.Raw` in `[0, XpCeiling.Raw]` | Minted as today; hash unchanged | No error expected |
| Negative level | `profile.Level < 0` | 0 minted (whole profile rejected) | `log?.Warn`, deterministic |
| Negative or absurd xp | `xp.Raw < 0` or `xp.Raw > XpCeiling.Raw` | 0 minted (whole profile rejected) | `log?.Warn`, deterministic |
| Over-cap level | `placed.MaxLevel > 0 && level > placed.MaxLevel` | That placed hero skipped (not minted) | `log?.Warn`, deterministic |
| Owner-scoped mint | `ownerSlot` supplied, multiple placed heroes of same unit id across factions | Mint only into the placed hero whose `OwnerFaction == ownerSlot` | Non-matching placed heroes skipped |
| Owner filter absent | `ownerSlot == null` (all existing tests/callers) | Current behavior: mint into every matching placed hero (dup-id skips) | Unchanged |
| Missing slot key | `{"item_id":"ring","charges":2}` (no `"slot"`) | `Deserialize<ProfileInventoryItem>(...).Slot == -1` | Falls back to first free slot on re-mint |
| Explicit slot key | `{"item_id":"ring","charges":2,"slot":3}` | `.Slot == 3`; re-serialize includes `"slot":3` | Slot-faithful |
| Preserve round-trip | Edit→Play→Edit with preserve, manifest carries `hero.level`+`hero.xp`(+`hero.inventory`) | Re-mint identical to today (both keys carried) | — |
| Preserve, partial manifest | Manifest carries only `hero.level` | Preserve carries only `hero.level`; `hero.xp` not preserved | — |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/HeroProfileLoader.cs` -- `LoadInto` (add range/cap gate + optional `ownerSlot` filter); DW-12 + DW-13 core.
- `godot/src/Core/Definitions/PlayerProfile.cs` -- `ProfileInventoryItem` record struct (DW-48: nullable-backed `slot` binding).
- `godot/src/Core/MainScene.cs` -- boot `LoadInto` (l.504) + preserve/discard `LoadInto` (l.1845/1850): pass `ownerSlot`; DW-15 preserve branch (l.1825-1847) reroute through `BuildProfile(... DeriveProfileShape() ...)`.
- `godot/src/Core/Bootstrap/Phases/HeroPickerPhase.cs` -- `Launch` `LoadInto` (l.56): pass `ownerSlot`.
- `godot/src/Combat/HeroXpSystem.cs` -- `XpCeiling` (read-only reference for the xp cap; already reachable via `using ProjectChimera.Combat`).
- `godot/src/Core/Definitions/PersistenceManifest.cs` -- `DeriveProfileShape()` (the seam DW-15 routes through).
- `godot/ProjectChimera.Sim.Tests/Definitions/HeroProfilePersistenceTests.cs` -- DW-12 + DW-48 tests land here.
- `godot/ProjectChimera.Sim.Tests/Persistence/HeroInventoryPersistenceTests.cs` -- DW-13 + DW-48 slot-fallback tests.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Definitions/PlayerProfile.cs` -- DW-48: make `ProfileInventoryItem` deserialize a missing `"slot"` to `-1`. Keep the public `Slot` (int, `-1` when absent) and the 2-arg `new(id, charges)` / 3-arg `new(id, charges, slot)` construction unchanged. Back the JSON with a nullable so an absent key stays null→`-1` (a non-nullable ctor param would be forced to `default(int)=0`); write `item_id`,`charges`,`slot` unchanged. A type-level converter or `[JsonConstructor]`+nullable property are both acceptable — whichever keeps the serialized bytes and the `Slot` accessor identical.
- `godot/src/Core/Definitions/HeroProfileLoader.cs` -- DW-12: before the mint loop, reject the whole profile (return 0 + `log?.Warn`) if `profile.Level < 0` or `xp.Raw < 0` or `xp.Raw > Combat.HeroXpSystem.XpCeiling.Raw`. Inside the loop, after the `UnitId` match, skip (continue + `log?.Warn`) any placed hero where `placed.MaxLevel > 0 && level > placed.MaxLevel`. DW-13: add a trailing optional `Faction? ownerSlot = null` param; when non-null, `continue` for any placed hero whose `OwnerFaction != ownerSlot.Value`. Update the XML doc to describe both gates. Preserve the existing skip/log wording style.
- `godot/src/Core/MainScene.cs` -- Pass `ownerSlot: _ctx.Lockstep?.LocalFaction` at all three `LoadInto` calls (boot l.504, preserve l.1845, discard l.1850). DW-15: replace the hand-built `snapProfile` (hardcoded `hero.level`/`hero.xp` Values) with `HeroProfileLoader.BuildProfile(PendingHeroProfile.ProfileId, .HeroDefId, .FactionId, .DisplayName, .SignatureAbility, snapLevel, snapXp, shape, _ctx.HarvestedHeroInventory ?? PendingHeroProfile.Inventory)` where `shape = (_ctx.Scenario ?? _ctx.FallbackMirror)?.PersistenceManifest?.DeriveProfileShape()`. If `shape` resolves null (defensive; PendingHeroProfile non-null implies persistence was enabled), keep the current hardcoded-Values snapshot as fallback so behavior never regresses.
- `godot/src/Core/Bootstrap/Phases/HeroPickerPhase.cs` -- Pass `ownerSlot: _ctx.Lockstep?.LocalFaction` at the `Launch` `LoadInto` (l.56).
- `godot/ProjectChimera.Sim.Tests/Definitions/HeroProfilePersistenceTests.cs` -- DW-12 tests: negative level → 0 minted; `xp.Raw < 0` → 0 minted; `xp.Raw > XpCeiling.Raw` → 0 minted; `level > placed.MaxLevel` (with `MaxLevel>0`) → 0 minted; valid `level`/`xp` still mint 1 (unchanged). DW-48 test: `JsonSerializer.Deserialize<ProfileInventoryItem>("{\"item_id\":\"ring\",\"charges\":2}").Slot == -1`, and a round-trip of an explicit `slot:3` preserves 3.
- `godot/ProjectChimera.Sim.Tests/Persistence/HeroInventoryPersistenceTests.cs` -- DW-13 test: two placed heroes of the same unit id owned by different factions; with `ownerSlot` set, only the owning faction's hero is minted. DW-48 fallback test: a slot-less legacy loadout (JSON without `"slot"`, or `.Slot == -1`) re-mints contiguously (multi-item loadout does NOT collapse onto slot 0).

**Acceptance Criteria:**
- Given a profile with `level == -1` (or `xp.Raw == -1`, or `xp.Raw > XpCeiling.Raw`) and a matching placed hero, when `LoadInto` runs, then 0 rows are minted and `HeroStore` equals the empty-store hash.
- Given a profile with `level` above the placed hero's `MaxLevel` (`MaxLevel > 0`), when `LoadInto` runs, then that hero is skipped (0 minted for it).
- Given `ownerSlot` is null (every pre-existing caller/test), when `LoadInto` runs, then minting behavior and the resulting `StartStateHash` are byte-identical to before this change.
- Given `ownerSlot` is set and placed heroes of the same unit id are owned by different factions, when `LoadInto` runs, then only the placed hero whose `OwnerFaction == ownerSlot` is minted.
- Given a scenario whose manifest carries `hero.level`+`hero.xp` and an Edit→Play→Edit preserve round-trip, when the hero is re-minted, then the preserved level/xp are re-minted exactly as before this change (no regression).
- Given a scenario whose manifest carries only `hero.level`, when the preserve path re-mints, then only `hero.level` is carried (xp not preserved).
- Given inventory JSON with no `"slot"` key, when deserialized, then `Slot == -1`; and given an explicit `"slot":3`, the value round-trips to 3 with byte-identical serialization.

## Spec Change Log

No bad_spec loopbacks — the implemented approach met the intent; review produced only patches and rejects.

## Review Triage Log

### 2026-07-19 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 4: (high 0, medium 3, low 1)
- defer: 0
- reject: 9
- addressed_findings:
  - `[medium]` `[patch]` DW-13 silent whole-profile 0-mint when the deployed hero's placements are all owner-filtered (picker offers a profile slot-agnostically, but the mint gate requires the local slot) — added a `log?.Warn` in `LoadInto` so the drop is diagnosable rather than silent.
  - `[medium]` `[patch]` DW-13 owner-slot wiring seam untested (only hand-built `OwnerFaction`/`ownerSlot`) — added `SimResetTests.Applier_RecordsPlacedHeroOwnerFaction_AndOwnerSlotFilterMintsLocalHeroOnly`, asserting the applier populates `PlacedHero.OwnerFaction` from the slot and that a production-style `ownerSlot` mints the local hero / skips a non-local one against the applier-produced list.
  - `[medium]` `[patch]` DW-15 preserve path unverified at a faithful surface — added `SimResetTests.Reset_PreserveThroughPartialManifestShape_CarriesOnlySelectedAttributes`, driving the preserve snapshot through `DeriveProfileShape()` with a manifest that selects only `hero.level` and asserting `hero.xp` is dropped.
  - `[low]` `[patch]` reject-path tests asserted only `minted == 0` — added empty-store-`StartStateHash` assertions to the negative-xp, over-ceiling, and over-cap tests so a rejected profile is proven to fold nothing into the hash.

Rejected (noise / not live): DW-12 `MaxLevel==0` "fail-open" upper bound (unreachable — a validated spawned hero always has `def.Hero` and `MaxLevel` in `[2,100]`; the spec's I/O matrix deliberately treats `MaxLevel==0` as uncapped); custom converter throws on malformed `charges`/`item_id`/`slot` tokens (no regression — STJ's default deserialization threw identically and `LocalProfileSource.LoadAll` is fail-soft try/catch → empty list); converter ignores `JsonSerializerOptions` (by design — a stable snake_case on-disk contract; honoring options would risk byte drift); level/xp curve-coherence check (out of scope — the gate stops absurd values, not incoherent ones); per-hero over-cap `log.Warn` "spam" (expected, deterministic condition); `XpCeiling` raw-vs-`int` headroom (not live — 30000 ≪ 32767); `OwnerFaction` default `Neutral` aliasing (0 is not a player slot); "no test asserts hash-invariance across `ownerSlot` values" (the opposite is intended — different owning slots SHOULD mint different heroes); absent `item_id` → empty string (handled downstream: `registry.IndexOf("")` → −1 → deterministic skip).

## Design Notes

Determinism guardrails: the no-profile / first-boot path (`PendingHeroProfile == null`) must stay byte-identical — `LoadInto(null)` returns 0 before any new gate. `ownerSlot` defaulting to null preserves every existing golden because single-hero scenarios select the same lone placed hero either way. DW-48 changes only *legacy slot-less* deserialization (`0 → -1`); no such profile data exists on disk today (all real captures set `Slot` via `CaptureInventory`), and serialization is unchanged, so no golden re-baseline is required.

`XpCeiling` (30000 in 16.16) is the same saturation the runtime enforces, so a persisted xp above it is by definition unreachable through legitimate play → a valid fail-closed ceiling. Level lower-bound is `< 0` (NOT `< 1`): an inventory-only profile whose manifest omits `hero.level` legitimately mints `level == 0` today (e.g. `HeroInventoryPersistenceTests`), so rejecting 0 would break that path.

DW-15 mirrors `HeroPickerOverlay.OnSavePressed`/`OnOverwritePressed` exactly (`BuildProfile(... DeriveProfileShape() ...)`) — the single Save-side seam. `BuildProfile` only emits `hero.level`/`hero.xp`/`hero.inventory` when the shape carries those keys, which is precisely the manifest-honoring behavior DW-15 wants.

## Verification

**Commands:**
- `dotnet build godot/godot.sln -c Debug` -- expected: build succeeds, no new warnings.
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: all tests pass, including the new DW-12/DW-13/DW-48 tests; no determinism-golden / start-state-hash differential regresses.

## Auto Run Result

Status: done

**Summary:** Hardened the hero profile load/mint path (DW-12, DW-13, DW-15, DW-48). Added a fail-closed range/cap gate on persisted level/xp before minting; an optional owner-slot filter (default null = unchanged) threaded from `LocalFaction` at the four production call sites; rerouted the Edit↔Play preserve snapshot through `BuildProfile(... DeriveProfileShape() ...)` so it honors the manifest-selected attributes; and made a missing inventory `"slot"` JSON key deserialize to `-1` via a type-level converter (serialized bytes unchanged). Review added a diagnostic log for the owner-filtered whole-profile drop, two wiring/behavior tests, and reject-path hash assertions.

**Files changed:**
- `godot/src/Core/Definitions/PlayerProfile.cs` — DW-48 `ProfileInventoryItemJsonConverter` (missing `"slot"` → −1; byte-identical serialization).
- `godot/src/Core/Definitions/HeroProfileLoader.cs` — DW-12 fail-closed range gate + per-hero over-cap skip; DW-13 optional `ownerSlot` filter; review: diagnostic warn on owner-filtered 0-mint.
- `godot/src/Core/MainScene.cs` — `ownerSlot: _ctx.Lockstep?.LocalFaction` at boot/preserve/discard `LoadInto`; DW-15 preserve reroute through the manifest shape (hardcoded-Values fallback when shape is null).
- `godot/src/Core/Bootstrap/Phases/HeroPickerPhase.cs` — `ownerSlot: _ctx.Lockstep?.LocalFaction` at `Launch`.
- `godot/ProjectChimera.Sim.Tests/Definitions/HeroProfilePersistenceTests.cs` — DW-12 + DW-48 tests; reject-path empty-store-hash assertions.
- `godot/ProjectChimera.Sim.Tests/Persistence/HeroInventoryPersistenceTests.cs` — DW-13 owner-scope test; DW-48 slot-less legacy re-mint test.
- `godot/ProjectChimera.Sim.Tests/Sim/SimResetTests.cs` — DW-13 applier→ownerSlot wiring test; DW-15 partial-manifest preserve test.

**Review breakdown:** 4 patches applied (3 medium: DW-13 diagnostic log, DW-13 wiring test, DW-15 preserve test; 1 low: reject-path hash assertions). 0 intent_gap, 0 bad_spec, 0 deferred (ledger untouched per orchestrator instruction), 9 rejected. See Review Triage Log for rejection rationale.

**Verification:** `dotnet build godot/godot.sln -c Debug` → 0 errors (11 warnings, all pre-existing, in untouched files). `dotnet test` (full Sim.Tests) → 2737 passed, 1 pre-existing skip, 0 failed. `HashAlgoVersions_AreUnchanged` green (no SimChecksum/CanonicalModelHash/StartStateHash algo bumps). No determinism golden regressed.

**Residual risks:**
- **Latent MP determinism (documented, not live):** the production call sites now fold `LocalFaction` (a per-peer value) into which placed hero is minted, and that mint folds into `StartStateHash`. This is inert today — `PendingHeroProfile` is set only by the offline hero picker (null in MP), and `StartStateHash` is not yet in the MP handshake (Epic 9). When Epic 9 wires `StartStateHash` into the server-attested handshake, re-verify that the deployed-profile mint path is either MP-null or slot-consistent across peers before relying on it.
- **Offline degenerate scenario:** if an offline scenario places the deployable hero's unit id ONLY at a non-`Player1` slot (the picker offers it slot-agnostically), the mint now fail-closes to 0 rows. This is the intended owner-scoping (you deploy onto your own slot's hero), and it is now diagnosable via the added `log?.Warn` rather than silent.
- No in-engine (godot-verify) run: the changed production call sites are Godot-edge glue exercised at runtime, not by the Godot-free Sim.Tests tier. The DW-15 preserve behavior is verified at the `BuildProfile`+`DeriveProfileShape` seam the MainScene path routes through, not at the MainScene surface itself.
