---
title: 'Full-content pre-match hash handshake'
type: 'feature'
created: '2026-07-24'
status: 'done'
baseline_revision: '9377f3d6e95dddeeb0718fb2a295249ed521aa43'
final_revision: 'dd320bb'
review_loop_iteration: 0
followup_review_recommended: false
context:
  - '{project-root}/godot/CLAUDE.md'
warnings:
  - oversized
---

<intent-contract>

## Intent

**Problem:** The pre-match handshake proves two peers agree on the SCENARIO (`CanonicalModelHash` folds the map/triggers/pathability) and the effect-graph structural caps (`RulesetHash` folds `EffectCaps`), but it does NOT fingerprint the loaded CONTENT DEFINITIONS the scenario references — the faction rosters and their per-unit combat stats, building defs, research ladders, the `AbilityRegistry`, the `ItemRegistry`, and the `DamageTable`. Two peers whose `damage_table.json` or a single unit's `attack_damage` differ pass every current gate and then desync from the first combat tick (the logged known desync vector "handshake does not cover faction and ability JSON"). Worse: abilities/items are index-keyed by registry load order (`EntityWorld.AbilityId`/`ItemStore.DefId` are registry indices), so a divergent registry silently shifts every runtime id.

**Approach:** Add a Godot-free `ContentHash` — an FNV-64 canonical fold (the `CanonicalModelHash` family conventions: `Fixed` via `.Raw`, enums by NAME, strings length-prefixed UTF-8, collections sorted by a total order, presentation fields EXCLUDED) over the distinct loaded faction defs (incl. inline units/buildings/research), the full `AbilityRegistry`, the full `ItemRegistry`, and the `DamageTable` cells. Reuse the existing typed effect-tree walk for ability/item `EffectGraph`s so a modded effect is rejectable. Fold `ContentHash.Compute(...)` into `MatchAgreementHash` (bump its `AlgoVersion`) so the EXISTING fail-closed handshake gate (`HandshakeGate`/`ServerLobbyPolicy`) now rejects any content-byte difference pre-tick. Expose a per-domain `Breakdown` and surface the local breakdown on a block so the mismatching domain is nameable. A reflection **fold-completeness guard** forces every JSON-mapped definition field to be consciously folded or allowlisted, closing the silent-gap class.

## Boundaries & Constraints

**Always:**
- Fold content the SAME way the `CanonicalModelHash` family does: `Fixed`/quantized floats via `.Raw` (`Fixed.FromFloat(v).Raw` for authoring floats), enums by `.ToString()` NAME, strings via a length-prefix + UTF-8 bytes (null distinct from ""), collections SORTED by a total order over every folded field, `AlgoVersion` mixed FIRST, and a `0→1` sentinel. NEVER fold JSON/file bytes or a re-serialized canonical string (the cross-runtime string-format risk + the AI-gen stale-file lesson).
- The content hash folds into `MatchAgreementHash` ONLY. Do NOT fold it into `StartStateHash` or `CanonicalModelHash` — their goldens (`hero-start-state.golden.txt`) and `AlgoVersion`s stay put. Bump `MatchAgreementHash.AlgoVersion` (2→3) and re-pin its in-test expected values; new `ContentHash.AlgoVersion = 1`.
- Ability/item `EffectGraph`s fold through the SAME typed effect-tree walk `CanonicalModelHash` uses for DSL `run_effect` embeds — extract it to a shared internal helper so the two folds are byte-parity and cannot drift (CanonicalModelHash's goldens prove the extract is behavior-preserving; its output must stay byte-identical).
- Registry order = index order (`AbilityRegistry`/`ItemRegistry` are ascending-`Id`-stable), which is the sim's own id assignment — fold the WHOLE registry in that order. Faction defs fold as the DISTINCT loaded set (dedup by `Id`, sorted by `Id` ordinal); the per-slot faction ASSIGNMENT is already `CanonicalModelHash`'s job, not re-folded here.
- Presentation-only content stays EXCLUDED and documented: `CombatFeedbackProfile` (per the 2.7 no-fold rule — it is already excluded from `SimChecksum`), `DisplayName`, `MeshPath`/`MeshScale`, `Icon`, `Color`, `SignatureMechanicDisplay`. `[JsonIgnore]` computed/derived props are never folded (they derive from folded fields).
- Godot-free (`src/Core/Definitions`) so Tier-1 computes it headless; int/`ulong`/`Fixed.Raw` only (banned-API clean — no `float` in the fold, no `Dictionary` enumeration without a stable sort, no `DateTime`/`Random`).
- Computed ONCE at load time in the existing `MainScene._Ready` hash block (after `ScenarioApplier.Apply`, over the already-materialized registries/defs) and cached on the existing `LobbyUi.MatchAgreementHash` field. The Start/Ready path only READS the cached value — no start-button recompute/stall.
- No wire-format change and NO `PROTOCOL_VERSION` bump: the combined `MatchAgreementHash` still rides the unchanged Ready packet; only its computed VALUE moves (mirrors 9.4/9.14). Cross-version peers reject via the value differing + `AlgoVersion`-first discipline.

**Block If:**
- Making the fold banned-API-clean and Godot-free is impossible because a content type transitively pulls in Godot or `float`-in-tick (would need an architecture decision, not a fold tweak). Verify `FactionDefinition`/`AbilityDefinition`/`ItemDefinition`/`ResearchDefinition`/`DamageTable` are all already Godot-free sim types (they are — `src/Core/Definitions` + `src/Combat`).
- Any pre-existing SimChecksum world golden or the `hero-start-state.golden.txt` moves as a result of this change — that would mean the content fold leaked into `SimChecksum`/`StartStateHash`/`CanonicalModelHash`; STOP and do not re-baseline (content defs are immutable load-time data → per the checksum-fold timing rule, NO SimChecksum fold).

**Never:**
- No new mutable sim state, no new per-entity SoA array, no `SimChecksum` fold, no `CanonicalModelHash` behavior change (extract-only refactor there), no wire/`PROTOCOL_VERSION` change.
- Do NOT build the Story 12.4 "Update-Required" mod.io re-download flow — it does not exist and is a separate Epic-12 story that DEPENDS on this one. 9.16 provides the per-domain `Breakdown` as the hook 12.4 will consume; the actual re-download OFFER is out of scope. Do not fabricate a mod.io-resolve UI.
- Do NOT add a per-domain sub-hash EXCHANGE to the wire to let a peer unilaterally NAME the remote's diverging domain — that is a frozen-envelope/PROTOCOL_VERSION change. "Which domain" is satisfied by surfacing the LOCAL per-domain breakdown on a block (trusted-friends-EA context; both peers show theirs for comparison).
- Do NOT fold AI content into the handshake (`AiPreset`): the AI is not lockstep-deterministic (float, D2 debt, not an MP slot), so folding it would false-positive-reject. Exclude + document.
- Do NOT fold content into the replay v2 header (9.11's concern) here.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Unit stat mutation | one peer's faction JSON has `attack_damage` +1 | `ContentHash` (Factions sub-hash) differs → `MatchAgreementHash` differs → gate BLOCKS fail-closed pre-tick | Rejected, no match |
| DamageTable mutation | one peer's `damage_table.json` cell differs | `ContentHash` (DamageTable sub-hash) differs → gate BLOCKS | Rejected |
| Ability effect mutation | one peer's ability `effect` node value differs | `ContentHash` (Abilities sub-hash, via the typed effect walk) differs → gate BLOCKS | Rejected |
| Item / research mutation | item delta or a research level `time_ticks`/`cost` differs | `ContentHash` (Items / Factions.Research sub-hash) differs → gate BLOCKS | Rejected |
| Registry order shift | one peer has an extra ability file (indices shift) | whole-registry fold differs → gate BLOCKS (catches the id-reindex desync) | Rejected |
| Presentation-only edit | `combat_feedback`/`display_name`/`mesh_path`/`icon`/`color` differs | `ContentHash` UNCHANGED → match proceeds (no false-positive reject) | No error |
| Logically-equal content | omitted-vs-default field, JSON array reordered | folds IDENTICALLY (omit-when-default + total-order sort) → no false-positive | No error |
| Two-run determinism | same loaded content, computed twice | byte-identical `ContentHash` and `MatchAgreementHash` | No error |
| New unclassified field | a dev adds a `[JsonPropertyName]` field to a folded def, folds neither nor allowlists it | completeness guard test goes RED | Detected at CI |
| Handshake block surfacing | gate blocks on a content mismatch | the block message/log includes the local per-domain `Breakdown` (ruleset-caps, factions, abilities, items, damage-table) so the diverging domain is identifiable | No error |
| Load-time budget | max-content fixture (full rosters + effect graphs) | `ContentHash.Compute` completes well under a load budget; not on the Start button | No error (non-gated / generous ceiling) |

</intent-contract>

## Code Map

- `godot/src/Core/Definitions/ContentHash.cs` -- **NEW** Godot-free FNV-64 folder. `Compute(IReadOnlyList<FactionDefinition> loadedFactions, AbilityRegistry abilities, ItemRegistry items, DamageTable damage) → ulong` + a `Breakdown Describe(...)` (per-domain sub-hashes: RulesetCaps, Factions, Abilities, Items, DamageTable). `AlgoVersion = 1`. Folds each domain per the family conventions; reuses the shared effect-tree walk.
- `godot/src/Core/Definitions/CanonicalModelHash.cs` -- extract the typed effect-tree + modifier walk (`MixEffect`/`MixModifier`) and the FNV primitives into a shared `internal static` helper (e.g. `CanonicalFold`) that BOTH classes call. **Behavior-preserving** — its output stays byte-identical (goldens guard it). No `AlgoVersion` bump.
- `godot/src/Core/Definitions/MatchAgreementHash.cs` -- bump `AlgoVersion` 2→3; widen `Compute(...)` to accept the loaded content; fold `ContentHash.Compute(...)` immediately AFTER `RulesetHash.Compute()`. Update the class doc's fold list.
- `godot/src/Core/Definitions/RulesetHash.cs` -- UNCHANGED (structural-cap fingerprint stays its own concern; it composes into `MatchAgreementHash` alongside `ContentHash`). Its value is also a `ContentHash.Breakdown` "ruleset-caps" component for surfacing.
- `godot/src/Core/Definitions/FactionDefinition.cs` -- fold source. Top-level: fold `Id`, `Units`, `Buildings`, `Research`, `SignatureMechanicId`, `SignatureMechanicEffectId`, `HeroUnitId`, `PersistenceEnabled`, `StartingOre`/`StartingCrystal` (FromFloat.Raw); EXCLUDE `DisplayName`, `Color`, `AiPreset`, `SignatureMechanicDisplay`.
- `godot/src/Core/Definitions/UnitDefinition.cs` / `BuildingDefinition.cs` -- fold all sim stat/gameplay fields (hp/speed/damage/range/armor/costs/supply/train/vision/splash/delivery/projectile-speed/xp-bounty/collision/separation/prereqs/abilities/attack-domains/tags/is-hero/revives/sells/shop-stock/shop-radius/max-energy). EXCLUDE `DisplayName`/`MeshPath`/`MeshScale`/`CombatFeedback`; allowlist `Behaviors` + `Hero` block as "authoring-only, not sim-read (fold when a story reads them)".
- `godot/src/Core/Definitions/AbilityDefinition.cs` -- fold `Id`/`Targeting`/`Activation`/`CostEnergy`/`CostOre`/`CostCrystal`/`CostHealth`/`AllowSelfLethal`/`Cooldown`/`EffectGraph` (shared effect walk); EXCLUDE `DisplayName`/`CombatFeedback`.
- `godot/src/Core/Definitions/ItemDefinition.cs` -- fold `Id`/`Charges`/`MaxHealthDelta`/`AttackDamageDelta`/`MoveSpeedDelta`/`ArmorDelta`/`EffectGraph`/`CostOre`/`CostCrystal`; EXCLUDE `DisplayName`/`Icon`.
- `godot/src/Core/Definitions/ResearchDefinition.cs` -- fold `Id`/`CancelRefundFraction`/`Prerequisites`/`Levels[]` (each `Cost` map sorted by key ordinal, `TimeTicks`, `ModifierDelta` 4 floats); EXCLUDE `DisplayName`.
- `godot/src/Combat/DamageTable.cs` -- fold all `Get(d,a)` cells over `[0,DamageType.COUNT)×[0,ArmorType.COUNT)` in index order (no class change — `Get` is public; the `Fixed` cells fold via `.Raw`).
- `godot/src/Core/Definitions/AbilityRegistry.cs` / `ItemRegistry.cs` -- fold `.All` in registry (index) order.
- `godot/src/Core/MainScene.cs` -- at the `_Ready` hash-compute block (~:524-556, the `MatchAgreementHash.Compute` call ~:553): gather the distinct loaded faction defs (`_ctx.SlotFactionDefs`/`FactionDef`/`FactionDef2`), `_ctx.AbilityRegistry`, `_host.ItemRegistry`, `_ctx.DamageTable`; pass to the widened `MatchAgreementHash.Compute`. Presentation-only.
- `godot/src/Multiplayer/HandshakeGate.cs` / `LobbyUi.cs` -- on a start block, include the local `ContentHash.Breakdown` (domain sub-hashes) in the surfaced status/log so the diverging domain is identifiable. Light presentation touch.
- Tests (`godot/ProjectChimera.Sim.Tests/`):
  - `Validation/ContentHashTests.cs` -- **NEW**: per-domain mutation-moves-the-hash + presentation-edit-doesn't + logically-equal-folds-equal + two-run determinism + registry-order sensitivity + sentinel-never-0.
  - `Validation/ContentFoldCompletenessTests.cs` -- **NEW**: reflection guard over each folded def's `[JsonPropertyName]` props (folded-set ∪ exclusion-allowlist; RED on a new unclassified field).
  - `Validation/MatchAgreementHashTests.cs` -- re-pin expected (`AlgoVersion=3`); add "content mutation moves the value + gate blocks"; assert `StartStateHash`/`hero-start-state` golden UNCHANGED.
  - `Validation/HandshakeGateTests.cs` -- content-mismatch peer → BLOCK fail-closed; block surfaces the local breakdown domains.
  - `Validation/ContentHashPerfTests.cs` -- **NEW**: max-content fixture, emit median compute ms via `ITestOutputHelper`; generous/no ceiling (heed the `CanonicalModelHashPerf` CPU-contention flaky lesson — do not add another tight-ceiling flaky gate).
  - Reuse (patterns): `Validation/CanonicalModelHashTests.cs`, `CanonicalModelHashEffectFoldTests.cs`, `RulesetHashTests.cs`, `StartStateHashTests.cs`, `Golden/HeroStartStateGoldenTests.cs`.

## Tasks & Acceptance

**Execution:**
- `godot/src/Core/Definitions/CanonicalModelHash.cs` -- Extract the typed effect-tree walk (`MixEffect`/`MixModifier`) + FNV `MixInt`/`MixStr`/`MixULong` into a shared `internal static` helper; have `CanonicalModelHash` call it. Behavior-preserving (its goldens/tests must stay green, byte-identical).
- `godot/src/Core/Definitions/ContentHash.cs` -- **New.** Implement `Compute(loadedFactions, abilities, items, damage) → ulong` and `Describe(...) → Breakdown` per the family conventions, using the shared effect walk for ability/item `EffectGraph`s. Dedup+sort factions by `Id`; fold registries in index order; fold `DamageTable` cells in enum-index order; `AlgoVersion=1`; `0→1` sentinel. Document every exclusion.
- `godot/src/Core/Definitions/MatchAgreementHash.cs` -- Bump `AlgoVersion` 2→3; widen the signature to take the loaded content; fold `ContentHash.Compute(...)` right after `RulesetHash.Compute()`; update the doc.
- `godot/src/Core/MainScene.cs` -- Feed the loaded content into the widened `MatchAgreementHash.Compute` at the `_Ready` hash block; on a start block, surface the local `ContentHash.Breakdown`.
- `godot/src/Multiplayer/HandshakeGate.cs` / `LobbyUi.cs` -- Include the local per-domain breakdown in the block-message/log path (which-domain surfacing).
- `godot/ProjectChimera.Sim.Tests/Validation/ContentHashTests.cs` -- **New.** Cover every I/O-matrix row.
- `godot/ProjectChimera.Sim.Tests/Validation/ContentFoldCompletenessTests.cs` -- **New.** Reflection guard forcing fold-or-allowlist for every JSON-mapped def field.
- `godot/ProjectChimera.Sim.Tests/Validation/MatchAgreementHashTests.cs` -- Re-pin; add content-mutation-blocks + StartStateHash-unchanged assertions.
- `godot/ProjectChimera.Sim.Tests/Validation/HandshakeGateTests.cs` -- Add the content-mismatch block + breakdown-surfacing case.
- `godot/ProjectChimera.Sim.Tests/Validation/ContentHashPerfTests.cs` -- **New.** Emit-only/generous-ceiling load-budget recorder.

**Acceptance Criteria:**
- Given loaded content, when the handshake hash is computed, then `MatchAgreementHash` folds a `ContentHash` covering every sim-relevant domain — factions, units, abilities, items (3.15), research (4.8), buildings, and the damage table — while `CombatFeedbackProfile` and the named presentation-only fields are provably EXCLUDED (a presentation edit does not move the hash).
- Given a single-byte difference in any covered content on one peer, when the lobby/server gate runs, then the mismatch is rejected fail-closed pre-tick (the existing `HandshakeGate`/`ServerLobbyPolicy` blocks), and the local per-domain `Breakdown` is surfaced so the diverging domain is identifiable; the 12.4 mod.io re-download OFFER is out of scope (the breakdown is its hook).
- Given a dev adds a new JSON-mapped field to a folded definition, when the suite runs, then the fold-completeness guard is RED until the field is folded or explicitly allowlisted.
- Given the full test suite, when it runs, then every pre-existing SimChecksum world golden and `hero-start-state.golden.txt` is byte-identical (no `SimChecksum`/`StartStateHash`/`CanonicalModelHash` change; only the in-test `MatchAgreementHash` pins move for the 2→3 bump) and two independent runs produce byte-identical `ContentHash`.
- Given a max-content fixture, when `ContentHash.Compute` runs, then it completes within a load-time budget (recorded; not gated on the Start button, no stall) — no `PROTOCOL_VERSION` bump, no wire change.

## Spec Change Log

## Review Triage Log

### 2026-07-24 — Follow-up review pass (followup_review_recommended)
- intent_gap: 0
- bad_spec: 0
- patch: 3: (high 0, medium 0, low 3)
- defer: 0
- reject: 15
- addressed_findings:
  - `[low]` `[patch]` **Completeness-guard field/init blind spot.** The fold-completeness guard (`ContentFoldCompletenessTests.JsonMappedFields`) enumerated only public-settable `[JsonPropertyName]` props, so a future stat added as a `[JsonInclude]` field or `init`-only prop would deserialize yet stay invisible to the guard — a blind spot in the story's own silent-gap defense. Fixed: `JsonMappedFields` now also folds in `[JsonInclude]` fields and non-public/init setters surfaced via `[JsonInclude]`. Adds nothing for the current all-public-settable defs (no false RED); strictly widens future coverage.
  - `[low]` `[patch]` **Block message omitted build/version skew.** The `AlgoVersion` 2→3 bump guarantees any pre-9.16 peer always mismatches at the gate, yet `HandshakeGate`'s mismatch message directed users to check scenario/ruleset/roster (already identical on a pure version skew). Fixed: the mismatch reason now names "the same game build/version" first, with a note that a different build's algorithm always mismatches even with identical content.
  - `[low]` `[patch]` **Registry index-order invariant unguarded.** The reindex-detection fold folds `AbilityRegistry.All`/`ItemRegistry.All` in enumeration order; its cross-peer determinism rests on `.All` being ascending-`Id` regardless of file-load order (a ctor sort), an implicit cross-class invariant with no test. Fixed: added two guard tests asserting `.All` is ascending-`Id` for shuffled input, pinned next to the fold that depends on it.
- deferred (0): none new. The shared effect-walk `default`-case value-blindness (an `EffectNode`/`Modifier` subtype folds only its type name) re-surfaced this pass but is ALREADY logged to the deferred-work ledger from the first review pass — not re-logged.
- rejected (15): faction scope asymmetry (pass-1 adjudicated — `SlotFactionDefs` per-slot symmetric; unslotted factions can't desync); FNV-64 non-crypto/forgeable (intent scopes this as a trusted-friends-EA desync tripwire, not anti-cheat); ruleset-caps `Breakdown` labeling (by design — it is a labeled sibling `MatchAgreementHash` component, pass-1 reworded); only-local-breakdown-surfaced / solo-player-can't-self-diagnose (intent EXPLICITLY forbids per-domain wire exchange and mandates local-only surfacing — the intent-alignment auditor confirmed the diff faithfully implements this boundary-forced reading); self-derived absolute pins catch only drift (the documented nature of pins; correctness is established by the relative mutation/actuality tests); required-content-param not runtime-enforced (MainScene is the only caller; empty-vs-real content correctly fail-closed-rejects, so no lockout defect); `Describe`/`Compute` double-fold drift (unfounded — `Breakdown.Combined` IS `Compute(...)` verbatim; only a redundant non-gated load-time fold with a generous ceiling per intent); composition-test `_ => a` default (test hygiene only, no current defect — all InlineData handled); AiPreset false-assurance for AI matches (intent excludes `AiPreset` while the AI is not lockstep-deterministic; documented D2 debt); MixEffect default value-blindness (already deferred pass-1); faction dedup drops divergent-content duplicate Id (pass-1 adjudicated — duplicate Id is an upstream validator reject); string fold no NFC/NFD normalization (same posture as every sibling handshake hash — pre-existing, inherited by the behavior-preserving extract); MainScene wiring seam untested (mitigated — pass-1 verified the fold reads the sim's own `_ctx`/`_host` instances + `SlotFactionDefs` is populated-when-applied, and the spec provides the `/godot-verify` manual check; the proposed gather-helper test would not catch the real wrong-instance risk anyway); no symmetric excluded-field non-move sweep (the current excluded fields are fully covered by `PresentationEdits_DoNotMoveTheHash`); CanonicalModelHash pin doc overclaims effect-extraction coverage (the extraction IS covered by the pre-existing `CanonicalModelHashEffectFoldTests`; a doc nuance, not a coverage gap).

### 2026-07-24 — Review pass
- intent_gap: 0
- bad_spec: 0
- patch: 9: (high 0, medium 3, low 6)
- defer: 1
- reject: 5
- addressed_findings:
  - `[medium]` `[patch]` **Omit-vs-default false-positive reject.** `ContentHash` folded several resolve-backed fields as raw nullable + presence bit (`xp_bounty`, unit `Cost`/`CostOre`/`CostCrystal` triple, building `construction_time`/`supply_bonus`) while the sim reads the RESOLVED value — so a peer omitting a field and one authoring its resolved default were sim-identical yet hashed differently (violates the I/O-matrix "logically-equal folds identically"). Fixed: fold the resolved sim-value (`ResolveXpBounty()`, `ResolvedCost`, resolved building defaults); updated the completeness classification; added omit-vs-authored-default equality tests.
  - `[medium]` `[patch]` **Fold-actuality gap.** The completeness guard proved a field was CLASSIFIED as folded, not that `ContentHash` actually mixes it (a listed-but-unmixed field escaped). Fixed: added a reflection-driven mutation sweep that perturbs each folded field and asserts the hash moves.
  - `[medium]` `[patch]` **Synthetic-literal gate test.** The content-mismatch block was proven only with hand-picked literal hashes. Fixed: added a content-derived end-to-end test computing real per-domain `ContentHash`→`MatchAgreementHash` and asserting `HandshakeGate.CheckStart` BLOCKS.
  - `[low]` `[patch]` **Split-brain risk.** `MatchAgreementHash.Compute` content params defaulted to null (a future caller silently folds empty content). Fixed: made them required; updated the call site + tests.
  - `[low]` `[patch]` Item `EffectGraph` typed-walk wiring was untested (only the ability path). Added an item-effect mutation case.
  - `[low]` `[patch]` Presentation-neutrality untested for ability `DisplayName`/`CombatFeedback` + research `DisplayName`. Extended `PresentationEdits_DoNotMoveTheHash`.
  - `[low]` `[patch]` Completeness guard could pass vacuously for a type not using `[JsonPropertyName]`. Added a per-type "uses attribute mapping" assertion.
  - `[low]` `[patch]` Breakdown surfacing over-claimed unilateral "which domain" attribution and could misattribute a non-content-component mismatch. Reworded the surfaced string + docs to a labeled local content fingerprint; reworded the `AiPreset` exclusion as conditional-on-AI-lockstep.
  - `[low]` `[patch]` No absolute-value regression net on the shared FNV primitives. Added absolute-value pins on fixed `ContentHash`/`CanonicalModelHash` fixtures (relative mutation tests can't catch a uniform drift).
- deferred (1): the shared effect-tree walk's `default` case folds only the type name (value-blind), and there is no reflection completeness guard over `EffectNode`/`Modifier` subtypes — a future effect field/type moves the sim but not the hash. PRE-EXISTING (moved verbatim from `CanonicalModelHash`, same behavior for DSL `run_effect` embeds); this change only widens the blast radius to ability/item effects. Logged to the deferred-work ledger.
- rejected (5): faction gather "empty/asymmetric" (verified — `SlotFactionDefs` is per-slot resolved + symmetric for applied scenarios; folding only slotted factions is the correct minimal set, unslotted factions can't desync); registry `.All` order dependence (verified TRUE — `AbilityRegistry.All`/`ItemRegistry.All` are `Id`-ordinal ordered, so the reindex design holds); folded-vs-sim-consumed instance identity (the fold reads the same `_ctx`/`_host` instances the sim was constructed with; the `null→Default` fallbacks are headless/test-construction only); duplicate-faction-Id divergent-content dedup (rests on the real upstream unique-Id load invariant; a duplicate Id is a validator reject, not a real match state); cross-process/cross-platform `ContentHash` determinism untested (same posture as every sibling handshake hash; the fold is integer-FNV over `Fixed.Raw`/UTF-8/enum-names with no `float` in the fold).

## Design Notes

**Why `RulesetHash` isn't just widened:** `RulesetHash` is the effect-graph structural-cap fingerprint and is ALSO folded into the replay header (`MatchLifecycleController`). A distinct `ContentHash` keeps concerns separate, avoids disturbing the replay path, and gives the per-domain `Breakdown` naturally. Both compose into `MatchAgreementHash`.

**Why `MatchAgreementHash`, not `StartStateHash`:** folding content into `StartStateHash` would move `hero-start-state.golden.txt`. `MatchAgreementHash` USES `StartStateHash`'s value without modifying it (the 9.4/9.14 precedent), so the re-baseline is limited to that class's in-test pins — no golden file moves, no `SimChecksum` touch (content is immutable load-time data → checksum-fold timing rule says no fold).

**Silent-gap defense:** hand-folding is the family's established pattern but risks forgetting a new field (the enum-indexed-array touch-site class). The reflection completeness guard makes every JSON-mapped field a conscious fold-or-allowlist decision, so a future stat field can't silently escape the handshake. Scope it to `[JsonPropertyName]`-mapped settable props; `[JsonIgnore]` computed getters are derived and out of scope.

**"Which domain" surfacing (deliberate scope):** the wire carries one combined 64-bit value; unilaterally naming the REMOTE's diverging domain needs a sub-hash exchange (a frozen-envelope/`PROTOCOL_VERSION` change the epic forbids). Given the trusted-friends-EA context, the shippable read is: each peer surfaces its OWN per-domain `Breakdown` on a block, so the two humans (and, later, 12.4) compare domain-by-domain. The `Breakdown` is a pure, tested structure; the in-engine label is presentation (optional `/godot-verify`).

**Effect-fold parity:** ability/item `EffectGraph`s and DSL `run_effect` embeds share ONE typed walk (extracted helper) so a modded effect hashes identically wherever it appears — no second implementation to drift.

## Verification

**Commands:**
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: all green incl. the new `ContentHashTests`, `ContentFoldCompletenessTests`, `ContentHashPerfTests`, and the updated `MatchAgreementHashTests`/`HandshakeGateTests`; **every SimChecksum world golden and `hero-start-state.golden.txt` byte-identical** (only in-test `MatchAgreementHash` pins move); `CanonicalModelHash` goldens unchanged (extract is behavior-preserving).
- `dotnet build godot/godot.csproj` -- expected: clean; the new Godot-free `ContentHash` raises no banned-API findings (no `float`/`Dictionary`-enum/`DateTime`/`Random` in the fold); presentation-layer surfacing stays confined to `MainScene`/`LobbyUi`.

**Manual checks:**
- In-engine (optional, via `/godot-verify`): start a lobby with a locally-mutated `damage_table.json` on one side; confirm the start is BLOCKED and the block message names the DamageTable domain via the surfaced breakdown.


## Auto Run Result

Status: done (follow-up review pass, 2026-07-24)

**Summary:** Follow-up review of the already-shipped Story 9.16 change (`ContentHash` full-content pre-match hash handshake, committed `ef2935a`). Four review layers (adversarial / edge-case / verification-gap / intent-alignment) ran in parallel against the baseline→`ef2935a` diff. No intent gaps and no spec defects — the intent-alignment auditor confirmed the diff faithfully implements the reading the intent's own boundaries force. Three low-severity patches applied to harden the change; all other findings re-raised pass-1-adjudicated points, unfounded claims, or intent-scoped exclusions.

**Files changed this pass:**
- `godot/ProjectChimera.Sim.Tests/Validation/ContentFoldCompletenessTests.cs` — `JsonMappedFields` now also enumerates `[JsonInclude]` fields and non-public/init `[JsonInclude]` setters, closing the completeness guard's field/init blind spot (no effect on current all-public-settable defs).
- `godot/src/Multiplayer/HandshakeGate.cs` — mismatch block message now names game build/version as the first candidate cause (the `AlgoVersion` 2→3 bump makes any pre-9.16 peer always mismatch here).
- `godot/ProjectChimera.Sim.Tests/Validation/ContentHashTests.cs` — added two guard tests pinning `AbilityRegistry.All`/`ItemRegistry.All` ascending-`Id` order regardless of input order (the invariant the reindex-detection fold depends on); added `using System.Linq`.

**Review findings breakdown:** patch 3 (all low) applied; defer 0 new (the MixEffect value-blind default is already in the ledger from pass 1); reject 15.

**Follow-up review recommendation:** false. Patched this pass: high 0, medium 0, low 3 → score `3×0 + 1×3 = 3` (< 5), no high.

**Verification:**
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` — Passed: 3475, Failed: 0, Skipped: 1 (pre-existing). Every SimChecksum world golden and `hero-start-state.golden.txt` byte-identical; `MatchAgreementHash` pins unchanged from the committed state; the 2 new registry-invariant tests + strengthened completeness guard green.
- `dotnet build godot/godot.csproj` — clean, 0 errors (only pre-existing nullable warnings); the `HandshakeGate` message change raises no banned-API findings.

**Residual risks:**
- The shared effect-walk `default` case folds only the type name (value-blind) with no reflection completeness guard over `EffectNode`/`Modifier` subtypes — pre-existing, tracked in the deferred-work ledger (a future effect field/type moves the sim but not the hash).
- The MainScene load-time wiring seam has no automated headless test (Godot-coupled presentation glue); verified by the spec's optional `/godot-verify` manual check and pass-1's instance-identity verification.
