---
title: 'DW-26 — Repurpose HeroDefinition.XpPerKill as a runtime per-hero XP-gain multiplier'
type: 'feature'
created: '2026-08-03'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false
baseline_revision: 8b6b5b77100f1cceb3771e16bb1008d9619ccab5
final_revision: 3ebeb7c7ac523dbf6236109409db5a08770c8794
context:
  - '{project-root}/godot/CLAUDE.md'
warnings: [oversized]
---

<intent-contract>

## Intent

**Problem:** `HeroDefinition.XpPerKill` (`xp_per_kill`, default 100) is validated, round-tripped by `FactionWriter`, and surfaced in the Unit Card Editor as a functional "XP per kill" knob, but Story 3.13's runtime is victim-centric (`HeroXpSystem` credits `victim.XpBounty` only) and consumes `xp_per_kill` nowhere — a misleading authoring surface (DW-26).

**Approach:** Give the knob runtime meaning as a **per-hero XP-gain percentage multiplier layered on the victim's XpBounty** (the ledger's chosen resolution): each XP credit to a hero becomes `victim.XpBounty × (xp_per_kill / 100)`. The default 100 = 100% = a neutral ×1.0, so all existing content (which authors 100 or nothing) is bit-identical — no SimChecksum fold, no golden re-baseline. Relabel the editor field so the surface is no longer misleading.

## Boundaries & Constraints

**Always:**
- `xp_per_kill = 100` MUST resolve to an exact ×1.0 in 16.16 `Fixed` (multiply by `Fixed.One` is exact), so every existing hero credits its victim bounty unchanged.
- `XpGainFactorOf` is a NON-FOLDED per-hero constant — it joins the exact posture of `BaseXpOf`/`XpGrowthOf` (def-derived at `Mint`, resolved float→`Fixed` at the single applier load boundary, never quantized inside a tick, NOT added to `SimChecksum`). A divergence surfaces transitively through the folded `Xp`/`Level`.
- Resolve the factor as `Fixed.FromFloat(xp_per_kill / 100f)` ONLY at the `ScenarioApplier` capture boundary (the same `FromFloat` boundary that already resolves the other hero curve constants). No `float` enters sim/tick code.
- The credit multiply MUST be computed in widened `long` raw and saturated at `HeroXpSystem.XpCeiling` (a large factor × a near-ceiling bounty overflows a 16.16 `Fixed`), matching the existing saturation discipline at that site.
- Keep the JSON key `xp_per_kill`, the C# property `XpPerKill`, and the editor field key `"hero.xp_per_kill"` unchanged (round-trip, BalanceSuggestionApplier, and existing tests key off them). Only the runtime semantic, docs, and the editor's display label/tooltip change.
- An authored `xp_per_kill = 0` is honored as 0% (hero earns no kill XP) — it must NOT collapse into the neutral default.

**Block If:**
- Making default content bit-identical would require a golden re-baseline or a `SimChecksum` AlgoVersion bump. (If this triggers, the multiplier is not landing exactly at ×1.0 — stop and fix, do not re-record goldens.)

**Never:**
- Do NOT rename/remove `xp_per_kill`, change the victim-centric `XpBounty` credit source, or make heroes ignore victim bounty (a flat override is explicitly out of scope — the resolution is a multiplier layered ON the bounty).
- Do NOT fold `XpGainFactorOf` into `SimChecksum` or `StartStateHash` (it is a def-derived constant, not mutable mid-match state).
- Do NOT widen the validator bound or alter `HeroLevelingPresets` curve tuples.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Neutral default | Hero `xp_per_kill=100`, victim `XpBounty=30`, in range | Hero `Xp += 30` (exact, ×1.0) | none |
| Amplified | Hero `xp_per_kill=200`, victim `XpBounty=30` | Hero `Xp += 60` | none |
| Reduced | Hero `xp_per_kill=50`, victim `XpBounty=30` | Hero `Xp += 15` | none |
| Zero | Hero `xp_per_kill=0`, victim `XpBounty=30` | Hero `Xp += 0` (never levels from kills) | none, valid |
| Saturation | High factor × high bounty near ceiling | Credited XP saturates at `XpCeiling`, no `Fixed` overflow/throw | clamp high, floor 0 |
| Unset caller | `Mint`/`PlacedHero` without a factor (tests, persistence) | Stored as `Fixed.One` (neutral) | null → One |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/HeroDefinition.cs` -- `XpPerKill` field; update the "SUPERSEDED/dead" XML doc to the new percentage-multiplier runtime semantic.
- `godot/src/Core/HeroStore.cs` -- add non-folded `Fixed[] XpGainFactorOf`; `Mint` gains `Fixed? xpGainFactor = null` (store `?? Fixed.One`); `Clear` clears it.
- `godot/src/Core/Definitions/HeroProfileLoader.cs` -- `PlacedHero` record gains `Fixed? XpGainFactor = null`; `LoadInto` passes it into `Mint`.
- `godot/src/Core/Sim/ScenarioApplier.cs` -- capture site (~L316): resolve `Fixed.FromFloat((hd?.XpPerKill ?? 100f) / 100f)` onto `PlacedHero`.
- `godot/src/Combat/HeroXpSystem.cs` -- credit site (~L112-118): multiply `death.Bounty` by `_heroes.XpGainFactorOf[slot]` in widened `long`, saturate at `XpCeiling`, then add.
- `godot/src/Core/Persistence/SaveGameState.cs` -- add `XpGainFactorOf` to the `HA` enum + both capture (L385-396) and restore (L757-766) loops (mid-match save/load parity with the other curve constants).
- `godot/src/CreationSuite/UnitCardPanel.Edit.cs` -- relabel the `"hero.xp_per_kill"` field (L779) label/tooltip to convey the percentage multiplier; keep the field key.
- `godot/src/Core/Definitions/UnitDefinitionValidator.cs` -- keep the `[0, Range)` bound; update the rule comment to the percentage meaning.
- `godot/ProjectChimera.Sim.Tests/Combat/HeroXpTests.cs` -- add runtime multiplier tests (the I/O matrix rows).
- `godot/ProjectChimera.Sim.Tests/Builder/ScenarioApplierTests.cs` -- extend the curve-capture test (~L330) to assert the captured factor (`XpPerKill=10` → `0.1`).
- `godot/ProjectChimera.Sim.Tests/Persistence/*` -- a hero save/load round-trip must preserve a non-neutral `XpGainFactorOf` (add or extend one assertion).

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/HeroStore.cs` -- add `XpGainFactorOf` array, widen `Mint` with `Fixed? xpGainFactor = null` writing `XpGainFactorOf[slot] = xpGainFactor ?? Fixed.One`, and clear it in `Clear()` -- non-folded per-hero constant, neutral-by-default.
- `godot/src/Core/Definitions/HeroProfileLoader.cs` -- thread `Fixed? XpGainFactor = null` through `PlacedHero` and `LoadInto`→`Mint` -- carry the resolved factor from the applier without a second def lookup.
- `godot/src/Core/Sim/ScenarioApplier.cs` -- resolve `Fixed.FromFloat((hd?.XpPerKill ?? 100f) / 100f)` at the hero capture -- the single float→Fixed boundary (null hero-def → 100/100 = neutral, preserving today's full-bounty credit).
- `godot/src/Combat/HeroXpSystem.cs` -- at the credit site compute `creditedRaw = ((long)death.Bounty.Raw * (long)factor.Raw) >> 16`, saturate to `[0, XpCeiling]`, then `sum = Xp.Raw + creditedRaw` saturated -- the multiplier layered on the victim bounty, overflow-safe.
- `godot/src/Core/Persistence/SaveGameState.cs` -- add `XpGainFactorOf` to `HA` + capture/restore loops -- save/load parity with the other non-folded curve constants.
- `godot/src/Core/Definitions/HeroDefinition.cs` & `godot/src/CreationSuite/UnitCardPanel.Edit.cs` & `godot/src/Core/Definitions/UnitDefinitionValidator.cs` -- update the doc/label/tooltip/comment to the percentage-multiplier meaning (100 = normal; layered on victim XP bounty) -- de-mislead the authoring surface.
- `godot/ProjectChimera.Sim.Tests/**` -- add/extend tests for every I/O matrix row (neutral/amplified/reduced/zero/saturation/unset), the applier factor capture, and a non-neutral save/load round-trip -- prove the runtime and the golden-neutrality.

**Acceptance Criteria:**
- Given a hero authored `xp_per_kill=200` and an identical hero at `100`, when each credits the same victim bounty in range, then the 200 hero accumulates exactly twice the XP of the 100 hero.
- Given every existing hero/golden (authored `xp_per_kill=100` or minted without a factor), when the sim runs, then credited `Xp` values and all goldens are bit-identical (no fold, no re-baseline).
- Given a hero authored `xp_per_kill=0`, when it gets a kill credit, then it gains 0 XP and never levels from kills, and the validator still accepts 0.
- Given a mid-match hero with a non-neutral factor, when the game is saved and reloaded, then `XpGainFactorOf` is restored exactly (the hero keeps its multiplier).
- Given the Unit Card Editor with a promoted hero, when the hero fields render, then the former "XP per kill" field's label/tooltip communicate a percentage multiplier on kill XP (100 = normal), not a flat absolute XP.

## Review Triage Log

### 2026-08-03 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 1: (high 0, medium 1, low 0)
- defer: 0
- reject: 12: (high 0, medium 4, low 8)
- addressed_findings:
  - `[medium]` `[patch]` verification-gap (regression-net hole): no test pinned that the per-hero XP-gain factor is read from the correct hero's slot when two *differently-factored* heroes share one `DeathRecord` in one world/tick (existing tests used equal factors or separate worlds). Added `SharedKill_TwoHeroesDifferentFactors_EachBanksPerItsOwnFactor` — two heroes (×1.0, ×2.0) in one world, one shared death (bounty 30), asserts A banks 30 and B banks 60; a hoist of `factorRaw` out of the credit loop now fails it.
- notable rejects (with reason):
  - Adversarial "save-format corruption" (mid-enum `HA` insert without a `FormatVersion` bump): REFUTED — the hero blob is a self-describing jagged array guarded by `if (Hero.Length != (int)HA.COUNT) Fail(...)` (`SaveGameState.cs:1085`, verified present); a pre-DW-26 save fails closed, never misaligns. No shipped save-compat contract exists (solo in-dev).
  - `xp_per_kill` semantic swap "needs a new key / migration" (raised by 3 lenses): out of scope on the authority of the intent — the decision explicitly directs "Repurpose it … a per-hero XP-gain multiplier" on the SAME knob, the spec forbids rename/remove, and the only content file authoring it is neutral (100).
  - `FromFloat(pct/100)` 16.16 imprecision, non-fold detection blind-spot, Reset-to-Zero window, persisted-vs-re-derived factor: all match the established per-hero curve-constant posture (`BaseXpOf`/`XpGrowthOf`), resolved identically at the same float→Fixed boundary; not introduced by this change.
  - Validator upper bound not tightened to a percentage cap: kept deliberately per spec (`[0, Range)` preserves the `OutOfRangeXpPerKill` test; over-range values saturate harmlessly at `XpCeiling`).
  - Duplicate `<summary>` on `PlacedHero`: matches the record's pre-existing stacked-summary convention; build is 0-warnings (no CS1571).

## Design Notes

Why percentage, not additive/override: only a multiplier whose default resolves to ×1.0 keeps existing content bit-identical, so no golden moves and no `SimChecksum` AlgoVersion bump is needed — the dominant constraint. `Fixed.One` (raw 65536) makes `(bountyRaw × 65536) >> 16 == bountyRaw` exactly. The factor lives with the already-non-folded curve constants (`BaseXpOf` et al.), so `SimChecksum`/`StartStateHash` are untouched.

Neutral-default plumbing uses `Fixed? = null` (a compile-time-constant default) mapped to `Fixed.One` in `Mint`, because a `Fixed` default param cannot be `Fixed.One` and a `default(Fixed)` is `Zero` (which would silently zero every non-passing caller's XP). Explicit `Fixed.Zero` still authors a real 0%.

Credit site (illustrative, ~5 lines):
```csharp
Fixed f = _heroes.XpGainFactorOf[slot];
long creditedRaw = ((long)death.Bounty.Raw * (long)f.Raw) >> 16; // ×1.0 is exact
if (creditedRaw > XpCeiling.Raw) creditedRaw = XpCeiling.Raw; else if (creditedRaw < 0) creditedRaw = 0;
long sum = (long)_heroes.Xp[slot].Raw + creditedRaw;
if (sum > XpCeiling.Raw) sum = XpCeiling.Raw; else if (sum < 0) sum = 0;
_heroes.Xp[slot] = Fixed.FromRaw((int)sum);
```

## Verification

**Commands:**
- `dotnet build godot/godot.csproj` -- expected: build succeeds (C# not hot-loaded; required before any in-engine run).
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: all pass, including the new multiplier/saturation/round-trip tests AND all HeroXp golden tests unchanged (proves golden-neutrality).

**In-Engine Gate:** the diff touches `godot/src/CreationSuite/UnitCardPanel.Edit.cs` — drive the running editor to the Unit Card Editor, promote a unit to hero, and read the relabeled `hero.xp_per_kill` field's label/tooltip + value round-trip; assert the label reflects the percentage-multiplier semantic. Append the `### In-Engine Gate` artifact block.

## In-Engine Gate

### In-Engine Gate - 2026-08-03
- surface: Creation Suite → Unit Card Editor, hero-fields section, driven on the live `UnitCardPanel` (`/root/MainScene/@Node@1692`) in the running game.
- launched: `dotnet build godot/godot.csproj` (succeeded, 0 errors) → `godot_editor_edit run` (main.tscn) → `godot_exec` called `UnitCardPanel.LoadFactionFromPath("res://resources/data/factions/_unitcard_sample.json")` (hero `sample_hero_valid` is unit index 0) → walked the panel subtree reading each field row's Label text + SpinBox value.
- digest: rows captured verbatim — `XP GAIN % = 100.0`, `XP SHARE RADIUS = 12.0`, `XP GROWTH = 1.15`, `BASE XP = 100.0`, `MAX LEVEL = 10.0`, `XP BOUNTY = 275.0`; label scan → `has_new_label(XP GAIN)=true`, `has_old_label(XP PER KILL)=false`; editor error log → `No error messages (cursor 0)`.
- asserted: authoring source `_unitcard_sample.json` sets `hero.xp_per_kill = 100` → the relabeled field renders **"XP GAIN %"** (the pre-DW-26 "XP PER KILL" label is confirmed absent) and round-trips the value **100.0** (exact). Cross-check: `XP BOUNTY = 275.0` = `cost_ore 200 + cost_crystal 75` (the `ResolveXpBounty` derived default, `xp_bounty` unset) — the victim-bounty source the multiplier layers onto is intact. Expected == observed on every field.
- result: PASS

## Auto Run Result

Status: done
Blocking condition: none

**Change:** Repurposed the runtime-dead `HeroDefinition.XpPerKill` (`xp_per_kill`, default 100) into a live per-hero XP-gain **percentage multiplier** layered on the victim's `XpBounty` (DW-26's chosen resolution). Each XP credit a hero banks from a kill becomes `victim.XpBounty × (xp_per_kill / 100)`; the default 100 resolves to an exact ×1.0 in 16.16 `Fixed`, so all existing content and every golden is bit-identical — the factor joins the NON-folded per-hero curve-constant posture (`BaseXpOf` et al.), so there is no `SimChecksum` fold, no AlgoVersion bump, and no golden re-baseline. The Unit Card Editor field was relabeled "XP per kill" → "XP gain %" so the authoring surface is no longer misleading.

**Files changed:**
- `godot/src/Core/Definitions/HeroDefinition.cs` — rewrote the `XpPerKill` doc from "SUPERSEDED/dead" to the percentage-multiplier runtime semantic.
- `godot/src/Core/HeroStore.cs` — added non-folded `Fixed[] XpGainFactorOf`; `Mint` gained `Fixed? xpGainFactor = null` (stored `?? Fixed.One`, deliberate null→neutral); cleared in `Clear()`.
- `godot/src/Core/Definitions/HeroProfileLoader.cs` — `PlacedHero` record gained `Fixed? XpGainFactor = null`; `LoadInto` threads it into `Mint`.
- `godot/src/Core/Sim/ScenarioApplier.cs` — resolves `Fixed.FromFloat((hd?.XpPerKill ?? 100f) / 100f)` at the single float→Fixed load boundary.
- `godot/src/Combat/HeroXpSystem.cs` — credit site scales `death.Bounty` by the per-hero factor in widened `long`, saturating the credit to `[0, XpCeiling]` before the (already saturated) add.
- `godot/src/Core/Persistence/SaveGameState.cs` — added `XpGainFactorOf` to the `HA` enum + both capture/restore loops (mid-match save/load parity; the fail-closed `HA.COUNT` length guard rejects any mismatched blob).
- `godot/src/CreationSuite/UnitCardPanel.Edit.cs` — relabeled the `hero.xp_per_kill` field to "XP gain %" with a percentage tooltip (field key unchanged).
- `godot/src/Core/Definitions/UnitDefinitionValidator.cs` — kept the `[0, Range)` bound; comment updated to the percentage meaning.
- `godot/ProjectChimera.Sim.Tests/Combat/HeroXpTests.cs` — threaded `xpGainFactor` through the `MakeHero` fixture; added 7 I/O-matrix tests + `SharedKill_TwoHeroesDifferentFactors_EachBanksPerItsOwnFactor` (review patch).
- `godot/ProjectChimera.Sim.Tests/Builder/ScenarioApplierTests.cs` — asserts `xp_per_kill=10` → `0.1` factor capture.
- `godot/ProjectChimera.Sim.Tests/Persistence/SaveLoadTests.cs` — hero resume test now uses a non-neutral ×2.0 factor and asserts `XpGainFactorOf` restores exactly.

**Verification (independently re-run):**
- `dotnet build godot/godot.csproj` → Build succeeded, 0 warnings, 0 errors.
- `dotnet test` full Sim suite → 3800 passed / 0 failed / 1 skipped (the pre-existing unrelated `TriggerValidationTests` reservation), then re-run after the review patch with the targeted HeroXp/Golden/SaveLoad/ScenarioApplier subset (294 passed / 0 failed) — golden-neutrality confirmed (all HeroXp golden tests unchanged).
- Matrix Test Audit: every I/O row (neutral/unset, amplified, reduced, zero, saturation) has a dedicated passing test; the AC (2× vs 1×) and the shared-kill per-hero-factor path are pinned.
- In-Engine Gate (CreationSuite touched): driven twice — once by this session and once by the independent gate auditor. Both PASS: the relabeled field renders "XP GAIN %" (old "XP PER KILL" absent), reads the authored `xp_per_kill=100` exactly, and write-binding round-trips at 200 and 50; 0 editor/runtime errors. Artifact block recorded above.

**Review:** 5 layers (adversarial, edge-case, verification-gap, intent-alignment, in-engine-gate). 1 patch applied (the shared-kill regression test); 12 rejected (headline "save corruption" refuted by a verified fail-closed guard; semantic-swap "migration" out of scope on intent authority; the rest consistent with the established curve-constant posture). `followup_review_recommended: false` (patched score 3×medium(1) = 3 < 5, no high).

**Residual risks:** Low. Runtime is fully test-covered and golden-neutral; the factor is correctly non-folded (transitive-divergence detection matches `BaseXpOf`). The editor tooltip copy is delivered via the panel's custom hover mechanism (not `tooltip_text`), so it could not be quantitatively asserted in-engine — cosmetic only; the label and data binding were confirmed.
