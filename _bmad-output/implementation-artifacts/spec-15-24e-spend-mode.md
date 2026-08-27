---
title: '15-24e — Spend mode: creator-toggled auto vs player-spent hero attribute points'
type: 'feature'
created: '2026-08-26'
status: 'done'
baseline_commit: '7414f52b28b74e0f693583b1b4e6634b8a257791'
review_loop_iteration: 0
context: ['{project-root}/godot/CLAUDE.md']
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** Hero attribute growth is unconditionally automatic — `HeroXpSystem.ReconcileGrowth` applies the
authored `per_level` attribute vector the moment a level lands, and a creator has no way to hand that choice
to the player. There is also no player-facing level-up cue of any kind today: `hero_level` goes only to the
trigger-DSL feed, and the sole place a hero's Level renders anywhere is the revive-button label.

**Approach:** A creator-authored `spend_mode` on `AttributeModelDefinition` selects `auto` (today's
behaviour, what the campaign ships) or `player_spent`. Under `player_spent`, a level grants an unspent
attribute point instead of applying the per-level attribute vector; the player spends it through a new
`UnitCommand.SpendAttributePoint` wire order surfaced on the hero command card. AI-controlled slots always
behave as `auto`.

## Boundaries & Constraints

**Always:**
- **Default preserves today byte-for-byte.** `spend_mode` is absent from all shipped content and defaults to
  `auto`, so no hero's growth changes, both new folded lanes stay 0, and — with BOUNDED fold arms — **zero
  goldens move**. Same posture that held for leg d; if a golden moves, diagnose the gate before re-recording.
- **Only the ATTRIBUTE term is withheld.** Under `player_spent` the `AttrStatPerLevel` contribution is
  withheld and converted to a point. The flat `HealthPerLevelOf` / `DamagePerLevelOf` / `ArmorPerLevelOf`
  lanes are hero STAT growth, not attributes, and keep applying automatically in every mode.
- **AI-controlled slots behave as `auto`** (Alec, 2026-08-26). "AI-controlled" MUST be read from the
  match-agreed `AiControlPlan` — handshake-agreed and match-constant, identical on every peer — never from
  any UI or client-local notion of who is playing. A per-peer disagreement here desyncs on the first level-up.
- **Exactly ONE way into the sim.** Spending is a wire order applied by `OrderApplier`, both online and
  offline, through the same branch. No local-only mutation path (the DW-405 class).
- The order's subject is a hero ENTITY, so it dispatches in the POST-ownership-guard arm — a player must not
  be able to spend another player's points (the 3.15 anti-cheat rule, and why PlaceBuilding sits there).
- Fixed math only in sim; ascending iteration; authored `float` quantizes at the single load boundary.
- Authored enum-as-string follows the `DerivationShape` template: `string?` + a `[JsonIgnore] Parsed*`
  accessor failing OPEN to the default, a fail-CLOSED token check in the validator, omit-when-default in the
  writer, and a line in `Clone()` — omitting the clone line is the Story 4.5 / DW-1009 silent-drop class.
- An `AlgoVersion` moves only when THAT hash's fold set changes. `spend_mode` is roster-side, so it folds
  into `ContentHash` ONLY; `CanonicalModelHash` walks `ScenarioData` and must stay 17 (leg d's retraction).

**Never:**
- No third runtime mode. "Have the AI/creator system vector it on its own" is an AUTHORING-time concern —
  the LLM draft hook the parent spec scopes to leg f — not a sim behaviour.
- No change to XP crediting, threshold curves, or `ReconcileThresholds`. No veterancy interaction.
- No PROTOCOL or Replay bump: appending a `UnitCommand` value is format-neutral on a fixed-width wire.
- No respec, no point refund, no undo of a spend, no cap on banked points beyond the authored max level.
- Out of scope: the Attribute Editor's spend-mode control (leg f), rank/level VFX, and AI heroes ever
  spending from a pool.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|---|---|---|---|
| Default content | no `spend_mode` authored | parses to `auto`; growth applies exactly as today; both new lanes stay 0 | N/A |
| Auto mode level-up | `spend_mode: auto` | per-level attribute vector applies immediately; no point banked | N/A |
| Player-spent level-up | `spend_mode: player_spent`, human slot | +1 unspent point; attribute vector NOT applied; flat stat growth still applies | N/A |
| Player-spent, AI slot | same model, slot is AI per `AiControlPlan` | behaves as `auto` — vector applies, no point banked | N/A |
| Spend a point | valid order, points > 0, valid attribute index | point decrements, that attribute's spent total rises, effective stats recompute | N/A |
| Spend with zero points | order arrives, unspent == 0 | order is a no-op; no state change | denied, silently |
| Spend on another player's hero | order's subject is a hero of a different faction | rejected by the ownership guard before the branch runs | denied |
| Spend an unknown attribute index | index outside the model's attribute count | order is a no-op | denied, silently |
| Multi-level in one tick | XP burst crossing 3 thresholds | exactly 3 points banked; no vector applied | N/A |
| Save/load mid-bank | hero holds 2 unspent, 3 spent in STR | both lanes round-trip; effective stats identical after load | fail-closed on lane length mismatch |
| Unknown `spend_mode` token | `spend_mode: "wizard"` | fail-closed: located validator error | error, unit/faction rejected |
| Hero dies holding points | hero with banked points dies and revives | banked points and spent allocations survive — they are hero identity, not entity state | N/A |

</frozen-after-approval>

## Code Map

**The withholding site — the whole behavioural change:**
- `godot/src/Combat/HeroXpSystem.cs:408-485` — `ReconcileGrowth`. The term to withhold is the
  `AppendAttrVector(_heroes.AttrStatPerLevel, …)` contribution at **`:464`**; the flat lanes beside it stay.
- `godot/src/Combat/HeroXpSystem.cs:357-380` — `AdvanceLevels`, where the level lands and `hero_level` is
  raised (`:374`). The point credit belongs on this transition, once per level gained.
- `godot/src/Combat/HeroXpSystem.cs:529-545` — `ReconcileThresholds`: the swap idiom, with the DW-85 Health
  snapshot at `:528`/`:548` and the post-remove `IsAlive` re-check. Reuse it; `RemoveByModifierId` is lethal.

**Hero state — two new FOLDED lanes:**
- `godot/src/Core/HeroStore.cs:101` — `GrowthStacksApplied`, the folded-lane neighbour to declare beside.
- `godot/src/Core/HeroStore.cs:131,134` — `AttrStatBase` / `AttrStatPerLevel`, stride-`AttributeStats.Count`
  flat rings. The new per-attribute SPENT lane mirrors this stride exactly.
- `godot/src/Core/HeroStore.cs:141-147` — `AttributeStatAt`, the single read seam
  (`base + perLevel × (Level−1)`). It must remain ONE function after the spent term and the mode-conditional
  per-level term are folded in — never a second parallel reader.
- Touch list for a new lane: declare `:101` · write in `Mint` `:284` · `Array.Clear` in `Clear()` `:317` ·
  fold in `SimChecksum` after `:655` · save lane in the `HA` enum (`SaveGameState.cs:272-276`, **append-only**
  — a mid-enum insert silently shifts old saves) · capture `~:519` · restore `~:1082` · length-validate
  `:1524-1531`. Mint plumbing crosses `HeroProfileLoader.cs:49,134` and `ScenarioApplier.cs:378-396`.

**Authoring:**
- `godot/src/Core/Definitions/AttributeModelDefinition.cs:25` — per-faction model (`FactionDefinition.AttributeModel:83`);
  `spend_mode` belongs here, beside `Attributes`/`Derived`.
- `godot/src/Core/Definitions/AttributeModelDefinition.cs:114,128-131` — `DerivationShape`: authored `string?`
  + `[JsonIgnore] Parsed*` failing OPEN to default. **The template to copy.**
- `godot/src/Core/Definitions/FactionValidator.cs:437-444` — the matching fail-CLOSED token check.
- `godot/src/Core/Definitions/AttributeModelDefinition.cs:38-59` — `Clone()`. Add the field or Duplicate drops it.
- `godot/src/Core/Definitions/ContentHash.cs:352-374` — `FoldAttributeModel` (called `:217`). Mix the
  **ordinal** beside `:371`, never the raw string, or a re-spelling becomes a false handshake mismatch.
  Bump `ContentHash.AlgoVersion` `:75` (currently **5**).
- `godot/src/Core/Definitions/CanonicalModelHash.cs` — **stays 17.** Roster-side field; this hash walks
  `ScenarioData` only.

**The wire order — all three dispatch claims verified TRUE:**
- `godot/src/Core/EntityWorld.cs:13-55` — `UnitCommand : byte`, contiguous 0-25, **next free = 26**, budget ≤ `0x3F`.
  Stale prose at `:59-63` still says "spans values 0-23" — fix it.
- `godot/src/Core/EntityWorld.cs:107-150` — `UnitCommandTraits.PersistsAsCommandState`; its `default:` at
  `:143` THROWS on an unclassified member. Spending is instantaneous, so classify it non-persisting.
- `godot/src/Multiplayer/NetworkCommand.cs:472-478` — the ownership guard. **POST-guard arm** (`:497-546`,
  beside UseItem/DropItem/PlaceBuilding) because the subject is an entity.
- `godot/src/UI/SelectionSystem.cs:290-297` — `IssueUseItemCommand`: `_lockstep?.EnqueueOrder(PackRef(hero),
  cmd, Fixed.FromRaw(slot), Fixed.Zero) ?? true`. **The issue template** — the `?? true` is what makes offline
  and online travel the same `OrderApplier` branch.
- No PROTOCOL (`NetworkCommand.cs:1010`, 6) or Replay (`ReplayRecorder.cs:48`, 7) bump — wire is fixed-width
  `UnitOrder.SIZE = 14` and the command byte is a blind cast.

**Presentation — the cue does not exist yet, build it:**
- `godot/src/Combat/CombatEventQueue.cs:8-61` — `CombatEventType` has no hero-level member. Append one with
  the golden-safe append comment; the queue is NOT folded, so this moves nothing.
- `godot/src/UI/MatchAlertBridge.cs:66-88` — the drain switch to case; toast at `:98`/`:111` via
  `ChimeraToastHost.Show` (`Components/ChimeraToastHost.cs:56`).
- `godot/src/UI/CommandCardSystem.cs:258` — `_Process`, the per-frame poll and refresh hook;
  `:315-322` — the hero predicate (`HeroIndex[focusId] != HERO_NONE`); `:1290`/`:1499` — panel-build precedent;
  `:1329` — the `Pressed +=` wiring precedent.
- `godot/src/Core/Bootstrap/Phases/CameraPhase.cs:51-80` — where both systems are wired.

**In-engine gate (REQUIRED — `src/UI/**` and `src/Core/Bootstrap/**` are both touched):**
- `godot/tools/verify-in-engine-gate.ps1:52-60` (coupled patterns), `:272-284` — the spec must carry an
  `### In-Engine Gate` heading with a `- digest:` line ≥20 chars not starting `<`, an `- asserted:` line
  ≥20 chars, and `- result: PASS`. Use plain inline keys — a parenthetical in the key reads as missing.
  Exit 127 = bridge not listening on 6550.
- `godot/src/Core/MainSceneDebugSeam.cs:72` — `_mcp_state()`. It reads NOTHING from `HeroStore` today, so a
  hero read (level, unspent points, spent vector) must be added to produce a gate-quality digest.
  `DebugSelectUnit:340` already selects. The seam cannot emit `pressed` and cannot freeze time — drive the
  button by GDScript tree-walk `emit_signal("pressed")` and freeze via the `godot_game_time` MCP tool.
  **Freeze game time before asserting**, or a correct spend reads as failed.

**Guards that will move or catch omissions:**
- `Combat/CombatCommandSwitchCompletenessTests.cs:88,96,145` — the vocabulary pin; drives EVERY member
  through the real `OrderApplier`. An unclassified append fails here immediately.
- `Multiplayer/PlaceBuildingOrderTests.cs:215` — wire round-trip of the newest command; the template to copy.
- `Multiplayer/Story1511WireAndAffinityTests.cs:22,128` — `SIZE = 14` and the ≤`0x3F` budget.
- `Meta/VersionStampConsistencyTests.cs` · `Persistence/SaveLoadTests.cs` · `Definitions/ContentHashAbsolutePinTests.cs`
  · `Golden/SimChecksumCoverageGuardTest.cs` — stamp pins to update.
- `Persistence/EntityWorldSaveCompletenessTests.cs` · `Sim/EntityWorldClearCompletenessTests.cs` — auto-catch
  a missing save lane or `Clear()` reset.
- `Golden/ReBaselineDifferentialGuardTests.cs:70` — frozen v22 control; staying byte-identical is the proof
  the fold stayed bounded. Never re-record it.

## Tasks & Acceptance

**Execution:**
- [ ] `godot/src/Core/Definitions/AttributeSpendMode.cs` -- NEW closed enum `{ Auto = 0, PlayerSpent = 1 }`. -- the two runtime modes.
- [ ] `godot/src/Core/Definitions/AttributeModelDefinition.cs` -- add authored `string? SpendMode` + `[JsonIgnore] ParsedSpendMode` failing OPEN to `Auto`; add the `Clone()` line. -- the DerivationShape template.
- [ ] `godot/src/Core/Definitions/FactionValidator.cs` -- fail-CLOSED token check with a located error. -- an unknown token must not silently become Auto.
- [ ] `godot/src/Core/Definitions/ContentHash.cs` -- fold the ORDINAL in `FoldAttributeModel`; bump `AlgoVersion` 5 to 6. -- roster-side authored field.
- [ ] `godot/src/Core/Definitions/FactionWriter.cs` -- omit-when-default writer arm. -- shipped JSON stays byte-stable.
- [ ] `godot/src/Core/HeroStore.cs` -- add folded `UnspentPoints` and the stride-`AttributeStats.Count` spent lane across ALL touch sites; extend `AttributeStatAt` so it stays the single read seam. -- two new folded lanes, one bump.
- [ ] `godot/src/Combat/HeroXpSystem.cs` -- withhold ONLY the `:464` attribute term under `player_spent` on a non-AI slot, crediting one point per level gained instead; flat stat lanes unchanged. -- the behavioural change.
- [ ] `godot/src/Core/EntityWorld.cs` -- append `SpendAttributePoint = 26`; classify it in `UnitCommandTraits` as non-persisting; fix the stale "0-23" prose. -- the `default:` throws if unclassified.
- [ ] `godot/src/Multiplayer/NetworkCommand.cs` -- POST-guard applier branch: resolve the hero, validate the attribute index and points > 0, decrement, credit the spent lane, trigger recompute. -- one way into the sim, ownership-guarded.
- [ ] `godot/src/UI/SelectionSystem.cs` -- `IssueSpendAttributePointCommand` mirroring `IssueUseItemCommand` verbatim, including the `?? true` offline arm. -- online and offline share one branch.
- [ ] `godot/src/UI/CommandCardSystem.cs` -- hero panel affordance: show banked points and one control per attribute, visible only when points > 0 and the hero is owned; wire `Pressed +=` to the issue method. -- the player-facing half.
- [ ] `godot/src/Combat/CombatEventQueue.cs` + `godot/src/UI/MatchAlertBridge.cs` -- append a hero-level event and case it to a toast. -- there is no level-up cue today at all.
- [ ] `godot/src/Core/MainSceneDebugSeam.cs` -- add a hero read (level, unspent, spent vector) to `_mcp_state()`. -- without it the in-engine gate cannot produce a numeric digest.
- [ ] `godot/src/Core/SimChecksum.cs` -- BOUNDED fold arms for both lanes; bump `AlgoVersion` 28 to 29 + history line. -- fold-on-first-mutability.
- [ ] `godot/src/Core/Persistence/SaveGameState.cs` + `SaveGameFile.cs` -- append both `HA` lanes; bump `FormatVersion` 13 to 14. -- append-only; a mid-enum insert shifts old saves.
- [ ] Stamp-pin updates across `VersionStampConsistencyTests`, `SaveLoadTests`, `ContentHashAbsolutePinTests`, `SimChecksumCoverageGuardTest`. -- conscious, expected movement.
- [ ] `godot/ProjectChimera.Sim.Tests/Combat/SpendModeTests.cs` -- NEW Tier-1 tests covering **every** I/O matrix row, plus: two identical runs byte-identical; a save/load mid-bank restores identical effective stats; the wire round-trip of command 26; an AI-slot arm and a human-slot arm compared on the SAME model. -- prove behaviour and determinism.
- [ ] `_bmad-output/implementation-artifacts/spec-15-24e-spend-mode.md` -- append the `### In-Engine Gate` block with a verbatim digest and expected-vs-observed numbers. -- required; the diff touches coupled directories.

**Acceptance Criteria:**
- Given the shipped content set (no faction authors `spend_mode`), when the full suite runs, then **all 35 goldens are byte-identical and none is re-recorded**, and the frozen v22 control still hashes `0x6864F671`.
- Given one faction model authored `player_spent` and another left default, when both level a hero in the same match, then the first banks a point and applies no attribute vector while the second applies its vector immediately — the two arms compared at the same tick.
- Given a `player_spent` hero with banked points on an AI-controlled slot, when it levels, then it behaves exactly as `auto` — vector applied, nothing banked.
- Given a spend order issued for a hero owned by another faction, when it is applied, then it is rejected by the ownership guard and no state changes on either peer.
- Given a match with spending in play, when the same seed is replayed, then the tick-by-tick `SimChecksum` is identical, and a mid-match save then load resumes a byte-identical checksum stream.
- Given the diff touches `src/UI/**` and `src/Core/Bootstrap/**`, when the story completes, then the In-Engine Gate block is appended with a captured runtime-state digest proving banked points fell by one and the chosen attribute's total rose — verified against the authoring JSON's numbers, with both mode arms exercised.

## Spec Change Log

## Review Triage Log

### 2026-08-26 — review pass 1 (blind-hunter · edge-case-hunter · verification-gap)

Three layers ran blind against the diff; every finding was verified at its named site before triage.
**15 routed to `patch`, 5 to `defer` (DW-1010 … DW-1014). No `intent_gap`, no `bad_spec`, no loopback.**

**The loopback judgment, recorded deliberately.** The workflow says to prefer `bad_spec` when torn, and this
pass was torn — six of the fifteen were genuine correctness bugs, which is a large tail. It was routed as
patches anyway, for three reasons: no finding said the DESIGN was wrong (each named a specific unhandled
edge); the spec's Code Map had ALREADY named the touch sites that were missed (`HeroProfileLoader.cs:49,134`
and `ScenarioApplier.cs:378-396` for the mint path), so re-deriving from the same spec would likely reproduce
the same misses rather than fix them; and a revert would have discarded a verified In-Engine Gate run that
required a live bridge and could not be cheaply reproduced. Precision over re-rolling.

**Two findings became rulings from Alec rather than patches** — recorded on the frozen block's terms:
- **AI takeover of a DROPPED player's slot must not auto-spend.** Alec revised his own earlier
  "AI slots always behave as auto" boundary, splitting a case this spec had collapsed: a skirmish AI holding
  a slot from match start on `auto` is correct and shipped; an AI taking over a disconnected human's slot is
  not, because the human may reconnect to find banked points spent badly and there is no respec. Filed as
  **DW-1010** with his words verbatim rather than built here, because AI takeover does not exist (DW-1,
  blocked on DW-204) and the toggle would have been an authored field with no consumer — the DW-918/DW-920
  computed-but-never-consumed class.
- **The offline AI mask must derive from slot occupancy** (P4), not the hard-coded `OfflineDefault`.

**DISMISSED — "the spec is filed as unfinished" (blind-hunter):** `status: in-progress`, unticked checkboxes,
empty log sections. Disposed of by rule — a finding whose fix edits the spec this build is implementing is
dismissed — and self-resolving, since this pass advances the status and fills these sections.

**DISMISSED — "unrelated `.uid` files in the changeset" (blind-hunter).** The three named files
(`VeterancySystem.cs.uid`, `VeterancyDefinition.cs.uid`, `VeterancyTests.cs.uid`) are Godot-generated sidecars
for source files that leg d legitimately created. The repository tracks 1073 `.cs.uid` files as settled
convention, so these are the convention being followed, not incidental additions. The claim that tracking is
unsettled is refuted by that count.

**KEPT and patched, worth naming** — the two the pass most justified itself on: a `player_spent` hero
deployed at level N banked ZERO points and lost all attribute growth with no recovery path (P1), and
accumulated spends overflowed `Fixed` into a negative total, collapsing max health and killing the hero (P2,
the swept SqrDistance class, sixth strike). Both were invisible to the whole suite. So was P5: deleting the
`heroXp` forward from any of three call sites left every test green while online and replay spends silently
vanished — the exact defect `CommandApplyParityTests.cs:563-573` documents for `Research` one story earlier.

## Design Notes

**Why two modes and not three.** The parent spec lists "auto / spent / player-choice". Alec's 2026-08-26
ruling reparsed it: the toggle is *player-spent or auto*, and "set the numbers for auto-spend **or** have the
AI/creator system vector it on its own" describes where the AUTO vector's numbers come from — hand-authored
or AI-generated. That is an authoring-time concern owned by leg f's Attribute Editor and its LLM draft hook,
not a third sim behaviour. Widening to a third value later costs one enum member and a ContentHash bump,
and nothing authors the field yet, so the cheap direction to be wrong in is this one.

**Why AI slots fall back to auto.** Alec's ruling. It also removes the need for any deterministic
AI-spending heuristic, which would otherwise be new float-free AI code on a path where `AiOpponentSystem` is
still float (DW-204) and therefore illegal in lockstep. Reading the mode from the match-agreed
`AiControlPlan` keeps the decision handshake-agreed and match-constant rather than per-peer.

**Why the spent allocation needs its own folded lane.** Under `auto`, a hero's attribute total is a pure
function of folded `Level` (which is why 15-21 and 15-24c needed no extra lanes). A player's free-form
allocation is not derivable from anything — it is new, genuinely mutable sim truth — so it must be folded and
persisted. This is the one place leg d's "no second folded array" argument does NOT transfer.

## Verification

**Commands:**
- `dotnet build godot/godot.csproj` -- expected: 0 errors; analyzer clean (no float, no `System.Random`, no Godot types in sim files).
- `dotnet test godot/ProjectChimera.Sim.Tests/ProjectChimera.Sim.Tests.csproj` -- expected: green; `SpendModeTests` and `CombatCommandSwitchCompletenessTests` pass; every golden passes **without** re-recording.
- `git diff --name-only <baseline> -- '*.golden.txt'` -- expected: EMPTY.
- Golden re-record is **not expected**. If a golden moves, diagnose the default-preserves-today gate first.

**Manual checks (In-Engine Gate — REQUIRED):**
- Rebuild `godot.csproj`, launch over the godot-mcp bridge, select a `player_spent` hero, **freeze game time**, grant XP to force a level, read the digest (banked points), drive the spend control by tree-walk `emit_signal("pressed")`, re-read. Assert banked fell by one and the chosen attribute's total rose by the authored amount. Repeat on an `auto` faction and confirm no points bank. Append both arms' numbers verbatim.


### In-Engine Gate

- environment: Godot 4.6.3-stable (official), `res://scenes/main.tscn`, godot-mcp bridge on 127.0.0.1:6550, `dotnet build godot/godot.csproj` rebuilt immediately before the run (C# is not hot-loaded). Game time FROZEN via the `godot_game_time` MCP tool for every assertion; the spend was driven by a GDScript tree-walk `emit_signal("pressed")` on the real command-card Button, never by a seam shortcut. Editor error log EMPTY for the whole session.
- setup: shipped content declares no hero unit and no faction attribute model, so the `MainSceneDebugSeam` SETUP mutators build the situation from the shipped preset `resources/data/attribute-models/wc3.json` — `DebugMintHero(entity, "wc3", <mode>, level 1, attrBase 10, attrPerLevel 2)` (a CLONE of the preset with `spend_mode` set; every declared attribute at 10 base / +2 per level; primary = strength) and `DebugGrantHeroXp(entity, 100)` (curve base 100 x growth 1.0, so 100 XP is exactly one level). Authored entity max HP, read from the seam before minting: entity 0 = 55.0, entity 1 = 55.0 (both Player1/alpha), entity 2 = 80.0 (Player2/beta). Authored base attack damage is 5.0 on all three — all three read `eff_damage_raw` 983040 = 15.0 at level 1, i.e. 5.0 + the primary rule's 1.0/pt x 10 strength. Every expected number below is the preset JSON's own `per_point` arithmetic: strength->max_health 25.0, strength->energy_regen 0.0017, agility->armor 0.3, intelligence->max_energy 15.0, primary->attack_damage 1.0. The three arms were minted into ONE match and stepped together, so every comparison is at the same tick.
- digest: LEVEL 1, all three arms (verbatim `DebugHeroJson()`, spent rings in full) — `[{"ai_mask":4,"attributes":["strength","agility","intelligence"],"authored_mode":"player_spent","eff_armor_raw":196608,"eff_damage_raw":983040,"eff_max_hp_raw":19988480,"entity":0,"faction":1,"level":1,"slot":0,"spend_mode":"player_spent","spent_raw":[0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0],"unspent":0,"xp_raw":0},{"ai_mask":4,"attributes":["strength","agility","intelligence"],"authored_mode":"auto","eff_armor_raw":196608,"eff_damage_raw":983040,"eff_max_hp_raw":19988480,"entity":1,"faction":1,"level":1,"slot":1,"spend_mode":"auto","spent_raw":[0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0],"unspent":0,"xp_raw":0},{"ai_mask":4,"attributes":["strength","agility","intelligence"],"authored_mode":"player_spent","eff_armor_raw":196608,"eff_damage_raw":983040,"eff_max_hp_raw":21626880,"entity":2,"faction":2,"level":1,"slot":2,"spend_mode":"auto","spent_raw":[0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0],"unspent":0,"xp_raw":0}]` — AFTER +100 XP to each (LEVEL 2) — `[{"ai_mask":4,"attributes":["strength","agility","intelligence"],"authored_mode":"player_spent","eff_armor_raw":196608,"eff_damage_raw":983040,"eff_max_hp_raw":19988480,"entity":0,"faction":1,"level":2,"slot":0,"spend_mode":"player_spent","spent_raw":[0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0],"unspent":1,"xp_raw":0},{"ai_mask":4,"attributes":["strength","agility","intelligence"],"authored_mode":"auto","eff_armor_raw":235929,"eff_damage_raw":1114112,"eff_max_hp_raw":23265280,"entity":1,"faction":1,"level":2,"slot":1,"spend_mode":"auto","spent_raw":[0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0],"unspent":0,"xp_raw":0},{"ai_mask":4,"attributes":["strength","agility","intelligence"],"authored_mode":"player_spent","eff_armor_raw":235929,"eff_damage_raw":1114112,"eff_max_hp_raw":24903680,"entity":2,"faction":2,"level":2,"slot":2,"spend_mode":"auto","spent_raw":[0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0],"unspent":0,"xp_raw":0}]` — AFTER the command card's `+1\nStrength` button `emit_signal("pressed")` on entity 0 — `{"ai_mask":4,"attributes":["strength","agility","intelligence"],"authored_mode":"player_spent","eff_armor_raw":196608,"eff_damage_raw":1048576,"eff_max_hp_raw":21626880,"entity":0,"faction":1,"level":2,"slot":0,"spend_mode":"player_spent","spent_raw":[1638400,65536,0,0,0,111,0,0,0,0,0,0,0,0,0,0,0],"unspent":0,"xp_raw":0}` (entities 1 and 2 byte-identical to their level-2 rows above in that same read). Panel state at that moment: header `"1 attribute point to spend"`, buttons `["+1/Intelligence disabled=false","+1/Agility disabled=false","+1/Strength disabled=false"]`; after the spend `attr_panel_visible=false`. Review P8 denial cue: pressing `+1\nStrength` again on the now-EMPTY bank produced toast labels `["Order denied","Can not do that","Hero level up!"]` (the second rendered verbatim with an apostrophe) and changed no state — `unspent=0`, `spent_raw[0]=1638400`. Review P14 guard: a second `DebugMintHero(0, ...)` on an entity already carrying a hero row returned `-5` (SEAM_BAD_SLOT) instead of orphaning the old row. Production-launch repeat from the first pass (menu PLAY -> skirmish setup -> Launch, a real match at tick 24): entity 0 at 2 banked points, `+1\nAgility` pressed -> `unspent` 2->1, `spent_raw[armor]` 0->19660, `eff_armor_raw` 196608->216268, header re-read as `"1 attribute point to spend"`.
- asserted: (1) PLAYER_SPENT withheld the attribute term: entity 0 went level 1->2 while `eff_max_hp_raw` stayed 19988480 (305.0 = authored 55.0 + 25.0/pt x 10 strength), `eff_damage_raw` stayed 983040 (15.0) and `eff_armor_raw` stayed 196608 (3.0 = 0.3/pt x 10 agility) — and exactly ONE point banked (`unspent` 0->1). (2) The spend fell the banked count by exactly one (1->0) and raised the chosen attribute's totals by the AUTHORING JSON's numbers: `spent_raw[max_health]` = 1638400 raw = 25.0 = wc3.json `strength->max_health per_point 25.0` x 1 point; `spent_raw[attack_damage]` = 65536 = 1.0 = the `primary->attack_damage 1.0` rule following `primary = strength`; `spent_raw[energy_regen]` = 111 raw = 0.0017 quantized to 16.16 (0.0017 x 65536 = 111.4 -> 111); every other stat in the 17-wide ring stayed 0. Effective channels moved to match: `eff_max_hp_raw` 19988480 (305.0) -> 21626880 (330.0) = +25.0; `eff_damage_raw` 983040 (15.0) -> 1048576 (16.0) = +1.0; `eff_armor_raw` UNCHANGED at 196608, because strength feeds no armor rule. (3) The AUTO arm (entity 1, identical authored HP 55.0, identical model/base/per-level, same tick) applied the vector immediately and banked nothing: `eff_max_hp_raw` 19988480 (305.0) -> 23265280 (355.0) = +50.0 = 25.0/pt x 2 strength/level; `eff_damage_raw` 983040 (15.0) -> 1114112 (17.0) = +2.0; `eff_armor_raw` 196608 (3.0) -> 235929 (3.5999) = +0.6 = 0.3/pt x 2 agility/level; `unspent` 0 and the spent ring all-zero throughout. The two arms diverge by exactly 50.0 max health at the same tick — precisely the withheld attribute term. (4) The AI arm (entity 2, authored HP 80.0, model authored `player_spent`, owner Player2 which `ai_mask` 4 marks AI-driven) reported `spend_mode: "auto"` while `authored_mode` stayed `"player_spent"`, and behaved as auto: level-1 `eff_max_hp_raw` 21626880 = 330.0 = 80.0 + 25.0/pt x 10 strength, `eff_armor_raw` 196608 = 3.0, `eff_damage_raw` 983040 = 15.0; at level 2 `eff_max_hp_raw` 24903680 = 380.0 (+50.0), `eff_damage_raw` 1114112 = 17.0 (+2.0), `eff_armor_raw` 235929 (+0.6) — the same deltas the auto arm took — with `unspent` 0 and an all-zero spent ring. So the match-agreed `AiControlPlan`, not the authored token, is the authority. (5) The affordance is honest: three buttons built from the preset's declared attributes in authoring order, all enabled while a point was banked, and the panel hidden (`attr_panel_visible=false`) the moment the bank emptied. (6) A refusal now CUES (review P8): pressing the control on an empty bank produced a denial toast and changed no state. (7) The double-mint guard (review P14) refuses rather than orphaning a folded hero row.
- result: PASS
