---
title: 'Story 3.7: Promote-to-Hero — leveling curve, XP-gain rule, signature/ultimate abilities'
type: 'feature'
created: '2026-07-07'
baseline_revision: '43548c48a2fddb668304480220cdecb464518187'
final_revision: 'ae41ea47726086b96b2cae7d3f9583ef1e064789'
status: 'done'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/_bmad-output/project-context.md'
  - '{project-root}/godot/CLAUDE.md'
warnings: [oversized]
---

<intent-contract>

## Intent

**Problem:** A unit can be marked `is_hero` in data (Story 3.2 added the flag + the HeroStore mint path), but a creator has **no way to author a hero** in the Unit Card Editor: there is no Promote-to-Hero switch and none of the hero-defining data — leveling table, XP-gain rule, signature/ultimate ability slots — exists on `UnitDefinition`. Epic 3's title feature ("Author Units & Heroes") and the GDD's "a Hero is a unit with an XP component, leveling table, and hero abilities" are unreachable.

**Approach:** **Authoring data model + validation + editor only** (the XP/leveling *runtime* is Story 3.13, per the story note). (1) A net-new nullable nested `HeroDefinition? Hero` POCO on `UnitDefinition` (JSON `hero`) mirroring the `CombatFeedback` nested-POCO precedent, holding `max_level`, `base_xp`, `xp_growth`, `xp_per_kill`, `signature_ability`, `ultimate_ability` — pure authoring data, no Resolve/SoA/checksum fold (D-2 posture from Story 3.6). (2) A `HeroLevelingPresets` closed preset set mirroring `UnitCompositionPresets`. (3) Extend `UnitDefinitionValidator` with hero rules (range, undefined signature/ultimate ref, composition rule, `is_hero`↔`hero` coherence). (4) A **Promote-to-Hero `ChimeraSwitch`** (its purpose-built `Toggled`/reveal API) in the editor body that drives the **existing** `IsHero` flag and reveals hero fields (Simple = leveling preset; Advanced = every hero field), routed through the existing set→PushHistory→GoToUnit→validate→save pipeline and the raw-JSON round-trip. (5) `FactionWriter` persists/removes the `hero` block.

## Boundaries & Constraints

**Always:**
- **The switch drives the EXISTING `UnitDefinition.IsHero`** (`is_hero`, added by Story 3.2 and read at spawn to mint a HeroStore row) — NEVER a parallel hero marker. This is how "a valid hero's identity matches the stable HeroStore key contract from 3.2" (AC2) is satisfied: 3.2's mint path is unchanged; 3.7 only adds the authored `hero` definition block that Story 3.13 will consume at runtime.
- **Hero data is purely additive authoring data on `UnitDefinition`.** Nested `HeroDefinition? Hero` = null when not a hero. Like `Behaviors` (Story 3.6 D-2): NO `Parsed*`/`Resolve*`, NO `EntityWorld`/SoA array, NO checksum fold — nothing consumes it at runtime this story, so it moves no golden and no sim stamp (`CanonicalModelHash` references by path + id string, 3.4/3.6-verified).
- **Leveling presets are a closed C# set** (`HeroLevelingPresets`, mirroring `UnitCompositionPresets`) — Godot-free, deterministic, Tier-1-testable, with lossless `Detect` round-trip (Simple dropdown ⇄ authored curve). This is a Simple-mode convenience, NOT hardcoded balance a creator can't reach: Advanced exposes every raw field + the JSON hatch (UX-DR54/FR-6).
- **Reuse the existing pipeline, do not rebuild it.** Extend `UnitCardPanel`/`UnitCardPanel.Edit.cs` in place: `AddSelect`/`AddNumFloat`/`AddNumInt`/`AddSection`/`MakeBadge`/`ShowBadge`/`PushHistory`/`GoToUnit`/`OnLiveChanged`/`RevalidateAndReflect`, the `_segment` Simple/Advanced disclosure + `_advancedHost`, and `FactionWriter` persistence. Toggling the switch mutates the def then `GoToUnit(def)` rebuilds the body (the composition-preset precedent) so hero fields appear seeded / disappear cleared.
- **Toggling the switch OFF clears the hero data** (`IsHero=false`, `Hero=null`) leaving a valid non-hero unit; toggling ON sets `IsHero=true` and instantiates `Hero` with the default ("Standard") leveling preset. Both are one undoable step (`PushHistory`).
- **Every new control carries a hover-AND-keyboard-focus tooltip** via `AttachFieldTip` (UX-DR53/NFR-2), and every hero field row carries a located validation badge (`MakeBadge`/UX-DR55).
- Determinism/layer rules hold: `HeroDefinition` is Godot-free (`src/Core/Definitions/`), authoring floats are plain `float`/`int` (quantized to `Fixed` later by the 3.13 consumer, the single boundary — like `MaxEnergy`); the validator is pure, no throw/log.

**Block If:**
- (none — the intent, the 3.2 designation contract, and the 3.6 authoring precedent fully determine the approach.)

**Never:**
- Never build the XP/leveling **runtime** (kill-credit XP, level-up, stat growth, ability unlock) — that is Story 3.13. This story authors + validates + persists the definition data only.
- Never add a 7th `UnitCategory` or a hero subclass (composition-over-inheritance; heroes are orthogonal to the archetype — a hero may be Melee or Ranged).
- Never fold hero data into `SimChecksum`/`CanonicalModelHash`/`StartStateHash` or add a per-entity SoA array for it this story (no consumer exists → no fold; keeps stamps 9/3/1/2 + StartStateHash 1 and the 18 goldens byte-identical).
- Never require the signature/ultimate slots to be filled for a valid hero (empty = "not authored yet", valid); only a **set-but-undefined** ability ref is rejected.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Promote ON | Editor on a non-hero unit; flip switch | `IsHero=true`, `Hero` instantiated with Standard preset; hero rows revealed (Simple: leveling preset; Advanced: all fields); one undo step | No error |
| Promote OFF | Editor on a hero unit; flip switch off | `IsHero=false`, `Hero=null`; hero rows hidden; unit validates clean as a non-hero; one undo step | No error |
| Valid hero save | `is_hero:true` + in-range curve + resolvable/empty slots | `UnitValidationResult.Ok`; faction JSON gains a `hero` object; reload reproduces identical composition | No error |
| Undefined slot ref | `hero.signature_ability` = an id absent from `AbilityRegistry` | Located error keyed `hero.signature_ability`; badge shown; Save/Playtest blocked | Reject (fail-closed) |
| Out-of-range leveling | `hero.max_level`=1 or 500, or `base_xp`≤0, or `xp_growth`<1 | Located error on the offending `hero.*` key; Save blocked | Reject |
| Composition rule violation | signature == ultimate (both non-empty) | Located error keyed `hero.ultimate_ability` ("signature and ultimate must differ"); Save blocked | Reject |
| Incoherent flag (raw JSON) | `is_hero:true` but no `hero` block, OR `hero` block with `is_hero:false` | Located error keyed `is_hero`; Save blocked | Reject (fail-closed) |
| Duplicate a hero | Duplicate action on a hero unit | Clone carries a deep copy of `Hero` (new id); both validate independently | No error |
| Raw-JSON round-trip | Author hero via form → open raw pane → re-parse | `hero` object present in raw JSON; re-parses to the identical `HeroDefinition` | No error |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/UnitDefinition.cs` -- add `[JsonPropertyName("hero")] public HeroDefinition? Hero { get; set; }` beside `CombatFeedback` (`:205-206`); nullable ⇒ omittable ⇒ existing faction JSON unaffected. `IsHero` (`:185-186`) is unchanged and stays the spawn-time designation. No `Parsed*`/`Resolve`/`[JsonIgnore]` index (D-2, mirrors `Behaviors` `:139-140`).
- `godot/src/Core/Definitions/HeroDefinition.cs` -- NEW Godot-free POCO: `max_level` (int), `base_xp`/`xp_growth`/`xp_per_kill` (float), `signature_ability`/`ultimate_ability` (`string?`). Add `HeroDefinition Clone()` (member-wise copy) for the Duplicate path. Model on `CombatFeedbackProfile` (a plain nested authoring POCO).
- `godot/src/Core/Definitions/HeroLevelingPresets.cs` -- NEW, structural clone of `UnitCompositionPresets`: closed `enum Kind { Custom, Standard, Fast, Slow }`, `All` label table (dropdown order), `Bundle(Kind)` → `(int MaxLevel, float BaseXp, float XpGrowth, float XpPerKill)`, `Detect(HeroDefinition?)` (value-equality on curve fields; no match/null ⇒ `Custom`). `Standard` = the promote-on default (e.g. 10 / 100 / 1.15 / 100).
- `godot/src/Core/Definitions/UnitDefinitionValidator.cs` -- `Validate(def, registry, behaviorRegistry, siblings)` (`:80`) already threads `AbilityRegistry? registry`. Append hero rules (multi-error, D-9) after the behaviors block (`:~185`): `is_hero`↔`hero` coherence; `hero.max_level` ∈ `[HeroLevelMin, HeroLevelMax]`; `hero.base_xp` finite & > 0 & < `Range`; `hero.xp_growth` finite & ≥ 1 & < `HeroGrowthCap`; `hero.xp_per_kill` finite & ≥ 0 & < `Range`; each set `signature/ultimate_ability` must `registry.IndexOf(id) >= 0` (skip when `registry` null, mirroring the ability guard); signature ≠ ultimate when both set. Add the named constants beside `Range` (`:56`).
- `godot/src/Core/Definitions/FactionWriter.cs` -- `ApplyFields` (`:188`) writes `is_hero` via `PutBool` (`:224`). Add a `WriteHero(obj, d)` call after it, mirroring `WriteCombatFeedback` (`:234`): serialize `d.Hero` POCO → `obj["hero"]` when non-null, else `obj.Remove("hero")`. (Hero is fully form-owned, so a deterministic POCO re-serialize is correct — no preserve-untouched constraint like `combat_feedback`.)
- `godot/src/CreationSuite/UnitCardPanel.Edit.cs` -- `BuildEditableBody` (`:62`): add a "Promote to Hero" `AddSection` + a `ChimeraSwitch` row (Simple) after Composition (`:110`); when `IsHero`, a Simple leveling-preset `AddSelect`-style row, and in `_advancedHost` a "Hero" section with `AddNumInt`/`AddNumFloat`/`AddSelect` rows for every hero field (signature/ultimate = a `Select` over `AbilityCatalog()` + a "(none)" entry). Switch `Toggled` handler: set `IsHero`+`Hero`, `PushHistory`(with `GoToUnit(def)`), rebuild. `CloneUnit` (`:813-838`) — add `Hero = s.Hero?.Clone()`. Reuse `AddSelect`/`AddNumInt`/`AddNumFloat` (`:309-364`), `MakeBadge`/`ShowBadge` (`:181/:661`), `AddCompositionRow` (`:534`) as the preset-row template.
- `godot/src/CreationSuite/UnitCardPanel.Edit.cs` (`RevalidateAndReflect`, `:641`) -- already calls `_validator.Validate(_current, _registry, _behaviorRegistry, _faction?.Units)`; the new `hero.*`/`is_hero` located errors flow through the existing `ShowBadge` loop (`:650`) — badge keys must match the row `MakeBadge` keys.
- `godot/src/UI/Components/ChimeraSwitch.cs` -- `Create(bool on)`, `Toggled(bool)` signal, `On`, `SetOn`, `BindReveal` — the purpose-built Promote-to-Hero control (`:26-120`). Reuse; no change.
- `godot/ProjectChimera.Sim.Tests/Definitions/HeroAuthoringTests.cs` -- NEW Tier-1 home (Godot-free), beside `BehaviorAndCompositionTests`.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Definitions/HeroDefinition.cs` -- NEW POCO with the six `[JsonPropertyName]` fields (defaults: `max_level=10`, `base_xp=100`, `xp_growth=1.15f`, `xp_per_kill=100`, slots `null`), a doc comment stating it is authoring-only (no runtime consumer until 3.13; no Resolve/SoA/checksum fold), and `HeroDefinition Clone()`.
- `godot/src/Core/Definitions/UnitDefinition.cs` -- add the nullable `Hero` nested field (JSON `hero`) with a doc comment mirroring `CombatFeedback`/`Behaviors` (nullable, additive, no fold). No `Parsed*`/`Resolve`.
- `godot/src/Core/Definitions/HeroLevelingPresets.cs` -- NEW closed preset set (mirror `UnitCompositionPresets`): `enum Kind`, `All` table, `Bundle(Kind)`, `Detect(HeroDefinition?)`. `Standard`/`Fast`/`Slow` differ in `max_level`/`base_xp`/`xp_growth`; `Custom` = no match (incl. null). Pure, deterministic.
- `godot/src/Core/Definitions/UnitDefinitionValidator.cs` -- add the `HeroLevelMin=2`, `HeroLevelMax=100`, `HeroGrowthCap=100f` constants and the hero rule block (see Code Map). Keyed field paths: `is_hero`, `hero.max_level`, `hero.base_xp`, `hero.xp_growth`, `hero.xp_per_kill`, `hero.signature_ability`, `hero.ultimate_ability`. Skip slot-ref checks when `registry` is null; run every other hero rule regardless. Non-hero units (Hero null, IsHero false) add no hero errors.
- `godot/src/Core/Definitions/FactionWriter.cs` -- add `WriteHero(obj, d)` (POCO serialize/remove) invoked from `ApplyFields` after the `is_hero` line; empty/non-hero ⇒ `hero` absent (no faction JSON churn for existing units).
- `godot/src/CreationSuite/UnitCardPanel.Edit.cs` -- (a) add the Promote-to-Hero `ChimeraSwitch` row (Simple) driving `IsHero`+`Hero` through `PushHistory`+`GoToUnit`; (b) when `IsHero`, a Simple leveling-preset dropdown (apply `Bundle`, `Detect`-preselected, undoable) and an Advanced "Hero" section with a row per hero field (numbers via `AddNumInt`/`AddNumFloat`; signature/ultimate via `AddSelect` over the ability catalog + "(none)"); (c) `CloneUnit` deep-copies `Hero`; (d) every new control gets `AttachFieldTip` + `MakeBadge`. Route all edits through the existing commit/validate/save path; hero-field badge keys match the validator keys.
- `godot/ProjectChimera.Sim.Tests/Definitions/HeroAuthoringTests.cs` -- NEW Tier-1 tests (Godot-free) covering the I/O matrix: valid hero ⇒ 0 errors; each out-of-range `hero.*` ⇒ its located error; undefined signature/ultimate ref ⇒ located error (and null registry ⇒ ref-check skipped); signature==ultimate ⇒ located error; `is_hero`↔`hero` incoherence (both directions) ⇒ located error; non-hero unit ⇒ no hero errors; `HeroLevelingPresets` `Bundle`/`Detect` round-trip (each preset detects back to its Kind; arbitrary curve ⇒ Custom); `HeroDefinition.Clone` independence; and `FactionWriter` hero round-trip (patch adds `hero`, non-hero stays absent, other tokens byte-preserved, POCO re-parses identically).

**Acceptance Criteria:**
- Given the Unit Card Editor on a composed unit, when I flip the Promote-to-Hero switch on, then hero-only fields appear (leveling curve, XP-gain rule, signature + ultimate ability slots) and persist as a `hero` block on the `UnitDefinition`; and flipping it off hides and clears them, leaving a unit that validates clean as a non-hero — both as single undoable steps.
- Given validate-before-save (AR-39), when a hero authors a missing/out-of-range leveling value, an undefined signature/ultimate ability ref, or a composition-rule violation, then each is rejected with a located UX-DR55 badge on the offending field and Save/Playtest is blocked; and a fully-valid hero saves with no badges, its `is_hero` designation (the Story 3.2 HeroStore mint key) intact, and round-trips through the advanced raw-JSON view unchanged.
- Given the Simple/Advanced disclosure (UX-DR54), when I author hero progression in Simple mode I use leveling-curve presets, and in Advanced mode I get every individual hero field plus the raw-JSON escape hatch (FR-6); every new control shows a tooltip on hover and on keyboard focus (UX-DR53).
- Given this is authoring-time presentation + Godot-free definition work, when the build and Tier-1 suite run, then `godot.csproj` compiles 0-error, all Tier-1 tests pass (including the new hero authoring/preset/writer tests), the 18 goldens are byte-identical, the sim stamps (9/3/1/2 + StartStateHash 1) are unchanged, `PhaseOrderTest` is untouched, and the release analyzer gate holds (RS0030 zero-baseline).

## Design Notes

- **D-1 — The switch drives the existing `IsHero`, not a new flag.** Story 3.2 added `is_hero` (read at spawn to mint a HeroStore row keyed by the stable `HeroId`) but no UI. AC2's "hero identity matches the stable HeroStore key contract from 3.2" is satisfied precisely by wiring the Promote-to-Hero switch to that same `IsHero` — the mint path is untouched. The new `hero` block is orthogonal *definition* data (leveling table / XP rule / ability slots) that Story 3.13 consumes at runtime; 3.7 reserves and validates it.
- **D-2 — No fold (deliberate, mirrors Story 3.6).** `Hero` gets no `Resolve*`, no SoA array, no checksum fold — nothing reads it at runtime this story. Adding an unread nullable POCO to `UnitDefinition` moves no golden (`CanonicalModelHash` hashes by path + id string) and keeps stamps 9/3/1/2 + StartStateHash 1. Story 3.13 adds the resolve+fold when it builds the runtime.
- **D-3 — Nested POCO, not flat `hero_*` fields.** A nullable nested `HeroDefinition? Hero` (JSON `hero`) mirrors the `CombatFeedback` precedent: promote-off ⇒ `Hero=null` cleanly clears *all* hero data in one assignment (the AC "flip off clears" requirement), and the whole block is omitted from non-hero JSON. `FactionWriter` serializes/removes it as a unit (POCO round-trip, like `combat_feedback`).
- **D-4 — Reveal via GoToUnit rebuild (the composition-preset precedent).** The switch handler mutates `IsHero`/`Hero` then calls `GoToUnit(def)`, which `Refresh()`es and rebuilds the body — so revealed hero rows are seeded fresh and cleared rows vanish, with no stale-control risk. Undo re-renders identically (the existing `AddCompositionRow` preset pattern, `:553`).
- **Example — authored hero block + a violation:**
  ```json
  "is_hero": true,
  "hero": { "max_level": 10, "base_xp": 100, "xp_growth": 1.15,
            "xp_per_kill": 100, "signature_ability": "storm_bolt",
            "ultimate_ability": "avatar" }
  ```
  `max_level:1` → located `hero.max_level` badge; `signature_ability:"nope"` (absent from the registry) → located `hero.signature_ability` badge; `ultimate_ability` == `signature_ability` → located `hero.ultimate_ability` badge; Save disabled in each case.

## Verification

**Commands:**
- `dotnet build godot/godot.csproj` -- expected: 0 errors (pre-existing CS86xx warnings only).
- `dotnet test godot/ProjectChimera.Sim.Tests` -- expected: all pass incl. the new `HeroAuthoringTests`; 18 faction goldens byte-identical; sim stamps 9/3/1/2 + StartStateHash 1 unchanged; release analyzer gate 0-err / 0-RS0030. (The pre-existing WSL-only `ProceduralMapGeneratorTests.SameSeed_…` golden-env mismatch is unrelated.)

**Manual checks (in-engine via `/godot-verify`):**
- Enter Edit mode, press `J` to open the Unit Card Editor on a Ranged unit. Flip **Promote to Hero** on → hero fields reveal (state Valid); `is_hero` + a `hero` block persist on Save. Advanced → set `signature_ability` to a real ability and `ultimate_ability` to a different one (Valid); set them equal → located `hero.ultimate_ability` badge, Save disabled. Set `max_level` to 1 → located `hero.max_level` badge. Simple → pick a leveling preset → curve fields populate; switch to Advanced → they reflect; Raw JSON shows the `hero` block and re-parses identically. Ctrl+Z reverts a promote. Flip the switch off → hero fields vanish, unit validates clean. Every new control shows a tooltip on hover and on keyboard focus.

## Review Triage Log

### 2026-07-07 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 3: (high 0, medium 1, low 2)
- defer: 0
- reject: 14
- addressed_findings:
  - `[medium]` `[patch]` The new `is_hero`↔`hero` coherence rule retroactively invalidated the shipped `_unitcard_sample.json` fixture (`sample_hero_valid` carried `is_hero:true` with no `hero` block — the Unit Card `/godot-verify` target), and no Tier-1 test pinned shipped hero data. Gave the fixture a valid `hero` block (using its two real abilities `fireball`/`matter_infusion` as signature/ultimate) AND added `ShippedUnitCardSampleFixture_EveryUnitValidates` (loads the fixture + the real `AbilityRegistry`, asserts every unit validates Ok) to guard shipped data against future validator drift.
  - `[low]` `[patch]` `WriteHero` used default serializer options, emitting explicit `"signature_ability": null` / `"ultimate_ability": null` for a default-promoted hero (breaks `ApplyFields`' omit-on-default discipline; JSON churn). Switched to `DefaultIgnoreCondition = WhenWritingNull` so unset slots are omitted (values still round-trip to null), and added `PromotedHero_WithUnsetSlots_OmitsNullSlotKeys` to pin it.
  - `[low]` `[patch]` `HeroLevelingPresets` doc comment falsely claimed each bundle's four curve fields are distinct (all three share `xp_per_kill = 100`); corrected to state the curve 4-tuples are pairwise distinct, which is what `Detect`'s whole-tuple equality actually relies on.

Rejected (not this story's problem or by-design): `WriteHero` re-serializes the form-owned `hero` block wholesale (deliberate — D-3, no preserve-untouched constraint like `combat_feedback`); `Detect` exact-float equality (limited impact, mirrors `UnitCompositionPresets`); triple-error on a same-typo undefined ref (multi-error is D-9 by-design, all three statements true); re-toggle discards a customized curve (spec-mandated clear-on-off, undoable); `JsonOptions`-vs-default divergence (currently equivalent, pre-existing in `WriteCombatFeedback`); missing duplicate/passive-ability hero checks (out of scope — AC2 lists only undefined-ref + signature≠ultimate; ability semantics are 3.13); `float` authoring numbers (informational, matches `MaxEnergy`; quantized at 3.13's single boundary); `Bundle(Custom)` returns Standard (deliberate total mapping, sole caller special-cases Custom); double-revalidate + inert `hero.leveling` badge key (mirrors the existing composition-row pattern); `WriteHero` NaN/Inf throw (unreachable — JSON has no NaN/Inf literal, spinboxes clamp ≥0 finite); coherence-before-persist (both save paths call `_validator.Validate` first — verified); XP-growth spinbox min < validator floor (validator badges it, consistent with the form's other fields); incoherent-load renders no hero rows (hand-edit edge case, surfaced by the `is_hero` badge).

## Auto Run Result

Status: done

**Summary:** Added authoring-only Promote-to-Hero support to the Unit Card Editor — a `ChimeraSwitch` driving the existing `is_hero` designation (Story 3.2's HeroStore mint key) plus a nested `HeroDefinition` block (leveling curve, XP-gain rule, signature/ultimate ability slots) with fail-closed validation, Simple-mode leveling presets, and faction-JSON persistence. Pure authoring data — no runtime, no SoA/checksum fold (the XP/leveling runtime is Story 3.13).

**Files changed:**
- `godot/src/Core/Definitions/HeroDefinition.cs` (NEW) — authoring POCO (`max_level`/`base_xp`/`xp_growth`/`xp_per_kill`/`signature_ability`/`ultimate_ability`) + `Clone()`.
- `godot/src/Core/Definitions/HeroLevelingPresets.cs` (NEW) — closed `Custom/Standard/Fast/Slow` preset set with `Bundle`/`Detect` round-trip (doc comment corrected in review).
- `godot/src/Core/Definitions/UnitDefinition.cs` — nullable nested `Hero` field (JSON `hero`); `IsHero` unchanged.
- `godot/src/Core/Definitions/UnitDefinitionValidator.cs` — hero rules: coherence, curve range, undefined signature/ultimate ref, signature≠ultimate.
- `godot/src/Core/Definitions/FactionWriter.cs` — `WriteHero` (serialize-or-remove; omits unset null slots after review).
- `godot/src/CreationSuite/UnitCardPanel.Edit.cs` — Promote-to-Hero switch, Simple leveling-preset row, Advanced Hero section, `CloneUnit` deep-copy of `Hero`.
- `godot/resources/data/factions/_unitcard_sample.json` — gave `sample_hero_valid` a valid `hero` block (review patch: comply with the new coherence rule).
- `godot/ProjectChimera.Sim.Tests/Definitions/HeroAuthoringTests.cs` (NEW) — 38 Tier-1 tests (36 from dev + 2 review guards: shipped-fixture validation, null-slot omission).

**Review findings:** 3 patches applied (1 medium: shipped-fixture regression + guard test; 2 low: null-slot JSON churn, doc comment), 0 deferred, 14 rejected (by-design or out of scope — see Review Triage Log). No intent_gap, no bad_spec.

**Verification:** `dotnet build godot/godot.csproj` → 0 errors (5 pre-existing CS86xx nullable warnings, confirmed present at baseline in `UnitCardPanel.Edit.cs`, none from this story). `dotnet test godot/ProjectChimera.Sim.Tests` → 826 passed / 1 skipped / 1 failed; the sole failure is the pre-existing WSL-only `ProceduralMapGeneratorTests.SameSeed_…` float-env golden mismatch (unrelated subsystem). All 18 faction goldens, `CanonicalModelHash`, `StartStateHash`, `PhaseOrder`, and `FactionWrite` tests pass — stamps 9/3/1/2 + StartStateHash 1 byte-identical (no fold, D-2 held). Release analyzer gate: 0 errors, **0 RS0030**, no diagnostic on any changed file. Matrix Test Audit: all 9 I/O rows covered by ran-and-passed tests; the switch's UI reveal/undo facet is inherently Godot-side, covered by the `/godot-verify` manual checklist.

**Residual risks:** Low. (1) The editor UI (switch, preset dropdown, hero rows) was compiled and logic-reviewed but not exercised in a live Godot session (no MCP editor in this headless run); it reuses the exact established `AddSelect`/`AddNumInt`/`PushHistory`/`GoToUnit` pipeline, so behavior should match — a live click-through remains the one unverified surface. (2) Story 3.13 will add the runtime resolve/SoA/checksum fold this story deliberately omits; the authored `hero` block and its validator keys are the contract it consumes.
